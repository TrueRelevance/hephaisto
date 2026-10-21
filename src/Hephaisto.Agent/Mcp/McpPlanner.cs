using System.Globalization;
using System.Text.RegularExpressions;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Mcp.Answers;
using Hephaisto.Core.Notifications;
using Hephaisto.Agent.Planner;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Planning;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// The planner through the MCP endpoint (#307): the board's issues for a coding agent to read,
/// and the planner's own clicks for it to make on a person's word.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write is a planner method.</b> Opening an issue, a field, a day, an assignee, closing
/// one - each goes through <see cref="PlannerService"/>, so the audit row is written before GitHub
/// hears of it, the kept board shows the change at once, and the console's open planner pages
/// update. An issue opened here is on the board and planned for its day the moment the call
/// returns, where one opened with the gh CLI waits for the next board read and for a person.
/// </para>
/// <para>
/// <b>Recorded as the token</b>, with the origin and any <c>onBehalfOf</c> as a claim, as the
/// incident writes are (<see cref="McpIncidentActions.Authorise"/>). A comment goes out without the
/// planner's marker: a replan passes on only what a person wrote in the console as that person's.
/// Nothing here approves a plan or starts a code fix: an issue's plan is answered by a person.
/// </para>
/// </remarks>
public sealed partial class McpPlanner(
    PlannerService planner,
    IOptionsMonitor<GitHubOptions> github,
    IOptionsMonitor<NotificationOptions> notifications,
    IHttpContextAccessor http)
{
    public const string Today = "today";
    public const string Backlog = "backlog";
    public const string Later = "later";
    public const string Decide = "decide";
    public const string Open = "open";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> Views = [Today, Backlog, Later, Decide, Open, Closed];

    public static readonly IReadOnlyList<string> Days = ["today", "not today", "tomorrow", "next week", "backlog", "none"];

    private const int BodyChars = 8_000;
    private const int CommentChars = 2_000;
    private const int CommentsShown = 10;

    // --- reads ----------------------------------------------------------------------------

    public async Task<PlannerIssuePage> ListAsync(
        string? view, string? status, string? priority, string? repository, string? assignee, string? query, int limit, bool refresh, CancellationToken ct)
    {
        var snapshot = await BoardAsync(refresh, ct).ConfigureAwait(false);
        var board = snapshot.Board!;
        var which = (view ?? Today).Trim().ToLowerInvariant();

        if (!Views.Contains(which))
        {
            throw new McpException($"view '{McpQuery.Echo(view)}' is not one of: {string.Join(", ", Views)}.");
        }

        var backlog = github.CurrentValue.Planner.BacklogStatus;

        // The closed view is today's closed issues, which have no entry: the planner's entries are its open issues.
        var rows = which == Closed
            ? snapshot.ClosedToday.Select(i => (Item: i, Entry: (PlannerEntry?)null))
            : snapshot.Entries
                .Where(e => which switch
                {
                    Today => e.Day == PlannerDay.Today,
                    Backlog => string.Equals(e.Item.Field(PlannerQueue.StatusField), backlog, StringComparison.OrdinalIgnoreCase),
                    Later => e.Day == PlannerDay.Later,
                    Decide => e.NeedsDecision,
                    _ => true,
                })
                .Select(e => (e.Item, Entry: (PlannerEntry?)e));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var found = planner.Search(query, open: which != Closed, limit: int.MaxValue).Select(i => i.ItemId).ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(r => found.Contains(r.Item.ItemId));
        }

        rows = rows
            .Where(r => Is(r.Item.Field(PlannerQueue.StatusField), status))
            .Where(r => Is(r.Item.Field(PlannerQueue.PriorityField), priority))
            .Where(r => string.IsNullOrWhiteSpace(repository) || r.Item.Repository.Contains(repository.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(r => string.IsNullOrWhiteSpace(assignee) || r.Item.Assignees.Contains(assignee.Trim().TrimStart('@'), StringComparer.OrdinalIgnoreCase));

        // The decisions keep the planner's own order, which is why each is in front of a person.
        List<(BoardItem Item, PlannerEntry? Entry)> ordered = which switch
        {
            Decide => rows.ToList(),
            Later => [.. rows.OrderBy(r => r.Entry?.PlannedFor).ThenBy(r => PlannerQueue.PriorityRank(board, r.Item))],
            Closed => [.. rows.OrderByDescending(r => r.Item.ClosedAt)],
            _ => [.. rows
                .OrderByDescending(r => r.Entry?.Active ?? false)
                .ThenBy(r => PlannerQueue.PriorityRank(board, r.Item))
                .ThenByDescending(r => r.Item.UpdatedAt)],
        };

        var shown = ordered.Take(limit).ToList();

        List<string> next = [.. shown.Take(1).Select(r => $"get_planner_issue {{\"issue\":\"{r.Item.Reference}\"}} - its description, comments and Claude's analysis")];
        next.Add("lookup_planner_fields - the fields, options, issue types and repositories a new issue is filled in with");
        next.Add("create_planner_issue {\"title\":\"...\",\"day\":\"today\"} - open an issue on the board, planned for today");

        return new PlannerIssuePage
        {
            View = which,
            Board = McpText.Name(board.Title),
            BoardUrl = McpText.NameOrNull(board.Url),
            ReadAt = snapshot.ReadAt,
            Problem = McpText.UntrustedOrNull(snapshot.Problem, 500),
            Total = ordered.Count,
            Issues = [.. shown.Select(r => Row(board, r.Item, r.Entry))],
            WorkedOnNow = snapshot.Active?.Item.Reference,
            ClosedToday = which == Today ? snapshot.ClosedToday.Count : null,
            Next = next,
        };
    }

    public async Task<PlannerIssueDetail> GetAsync(string issue, CancellationToken ct)
    {
        var snapshot = await BoardAsync(refresh: false, ct).ConfigureAwait(false);
        var item = Resolve(snapshot, issue, openOnly: false);
        var comments = await planner.CommentsAsync(item.Repository, item.Number, ct).ConfigureAwait(false) ?? [];
        var analysis = await planner.AnalysisAsync(item.Repository, item.Number, ct).ConfigureAwait(false);
        var href = planner.DetailHrefFor(item.Repository, item.Number);
        var baseUrl = notifications.CurrentValue.BaseUrl;

        List<string> next = item.Open
            ? [
                $"update_planner_issue {{\"issue\":\"{item.Reference}\",\"day\":\"today\"}} - plan it for today; also fields, assignee, title, worked on now",
                $"comment_on_planner_issue {{\"issue\":\"{item.Reference}\",\"text\":\"...\"}}",
                $"close_planner_issue {{\"issue\":\"{item.Reference}\",\"reason\":\"done\"}}",
            ]
            : [$"comment_on_planner_issue {{\"issue\":\"{item.Reference}\",\"text\":\"...\"}} - it is closed; a comment is still possible"];

        return new PlannerIssueDetail
        {
            Issue = Row(snapshot.Board!, item, snapshot.Entry(item.ItemId)),
            Author = McpText.Name(item.Author),
            CreatedAt = item.CreatedAt,
            Body = McpText.UntrustedOrNull(item.Body, BodyChars),
            CommentCount = Math.Max(item.Comments, comments.Count),
            Comments = [.. comments.TakeLast(CommentsShown).Select(c => new PlannerCommentRow
            {
                Author = McpText.Name(c.Author.Login),
                At = c.CreatedAt,
                Body = McpText.Untrusted(PlannerComment.TryRead(c.Body, out var person, out var text) ? $"{text}\n\n(written by {person} in the planner)" : c.Body, CommentChars),
            })],
            Analysis = analysis is null ? null : McpText.Untrusted(analysis.Body, 4_000),
            ConsoleUrl = href is null || string.IsNullOrWhiteSpace(baseUrl) ? null : McpText.Name($"{baseUrl.TrimEnd('/')}/{href}"),
            Next = next,
        };
    }

    public async Task<PlannerFieldsAnswer> FieldsAsync(CancellationToken ct)
    {
        var snapshot = await BoardAsync(refresh: false, ct).ConfigureAwait(false);
        var board = snapshot.Board!;
        var options = github.CurrentValue.Planner;
        var status = board.Field(PlannerQueue.StatusField);

        return new PlannerFieldsAnswer
        {
            Board = McpText.Name(board.Title),
            BoardUrl = McpText.NameOrNull(board.Url),
            Fields = [.. board.Fields.Select(f => new PlannerFieldOptions
            {
                Name = McpText.Name(f.Name),
                Options = [.. f.Options.Select(o => McpText.Name(o.Name))],
                Required = options.RequiredFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase),
            })],
            IssueTypes = [.. board.Types.Select(t => McpText.Name(t))],
            Repositories = [.. snapshot.NewIssueRepositories.Select(r => McpText.Name(r))],
            Assignees = [.. snapshot.Assignees.Select(a => McpText.Name(a))],
            BacklogStatus = McpText.NameOrNull(status?.Options.FirstOrDefault(o => string.Equals(o.Name, options.BacklogStatus, StringComparison.OrdinalIgnoreCase))?.Name),
            Days = Days,
            Note = snapshot.NewIssueRepositories.Count == 0
                ? "This install opens no issues: github.planner.repositories is empty. Issues can still be read, planned, assigned and closed."
                : "A new issue takes a repository, a type, the fields by name (Status, Priority, Area, ...) with one of their options, a day and an assignee. "
                    + "Choose each from the options here; a field the board asks for (required) should not be left empty.",
        };
    }

    // --- writes ---------------------------------------------------------------------------

    public async Task<PlannerWriteResult> CreateAsync(
        McpCaller caller,
        string? repository,
        string? title,
        string? body,
        string? type,
        IReadOnlyDictionary<string, string>? fields,
        string? day,
        string? assignee,
        string? onBehalfOf,
        CancellationToken ct)
    {
        var (actor, origin) = McpIncidentActions.Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Opening an issue", http.HttpContext);
        var plan = Day(day);
        var snapshot = await BoardAsync(refresh: false, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new McpException("Give the title: one line of what the issue is about.");
        }

        var where = string.IsNullOrWhiteSpace(repository)
            ? snapshot.NewIssueRepositories.FirstOrDefault()
                ?? throw new McpException("This install opens no issues: github.planner.repositories is empty.")
            : repository.Trim();

        var opened = await planner.OpenIssueAsync(
            new PlannerNewIssue(where, title, body, type, fields ?? new Dictionary<string, string>()), actor, ct, origin).ConfigureAwait(false);

        if (opened.ItemId is not { } itemId || opened.Opened is not { } item)
        {
            throw new McpException($"No issue was opened ({opened.Outcome}): {opened.Message}.");
        }

        var done = new List<string> { $"{item.Reference} opened and put on the board" };

        if (opened.Outcome != PlannerActionOutcome.Done)
        {
            return Answer(done, opened.Message, actor, origin, itemId);
        }

        var steps = new List<Func<Task<PlannerActionResult>>>();

        if (plan is { } p)
        {
            steps.Add(() => DayAsync(itemId, p, actor, origin, ct));
        }

        if (!string.IsNullOrWhiteSpace(assignee) && !IsNobody(assignee))
        {
            steps.Add(() => planner.AssignAsync(itemId, assignee.Trim().TrimStart('@'), assign: true, actor, ct, origin));
        }

        return await StepsAsync(done, steps, actor, origin, itemId).ConfigureAwait(false);
    }

    public async Task<PlannerWriteResult> UpdateAsync(
        McpCaller caller,
        string issue,
        string? title,
        string? body,
        IReadOnlyDictionary<string, string>? fields,
        string? day,
        string? assign,
        string? unassign,
        bool? workedOnNow,
        string? onBehalfOf,
        CancellationToken ct)
    {
        var (actor, origin) = McpIncidentActions.Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Changing an issue", http.HttpContext);
        var plan = Day(day);
        var item = Resolve(await BoardAsync(refresh: false, ct).ConfigureAwait(false), issue, openOnly: true);
        var itemId = item.ItemId;
        var steps = new List<Func<Task<PlannerActionResult>>>();

        if (title is not null || body is not null)
        {
            steps.Add(() => planner.EditAsync(itemId, title, body, actor, ct, origin));
        }

        foreach (var (field, option) in fields ?? new Dictionary<string, string>())
        {
            steps.Add(() => planner.SetFieldAsync(itemId, field, option, actor, ct, origin));
        }

        if (plan is { } p)
        {
            steps.Add(() => DayAsync(itemId, p, actor, origin, ct));
        }

        if (!string.IsNullOrWhiteSpace(unassign))
        {
            steps.Add(() => planner.AssignAsync(itemId, unassign.Trim().TrimStart('@'), assign: false, actor, ct, origin));
        }

        if (!string.IsNullOrWhiteSpace(assign))
        {
            steps.Add(() => planner.AssignAsync(itemId, assign.Trim().TrimStart('@'), assign: true, actor, ct, origin));
        }

        // Last: only an issue planned for today can be worked on, and the day above may have just made it one.
        if (workedOnNow is { } active)
        {
            steps.Add(() => planner.SetActiveAsync(itemId, active, actor, ct, origin));
        }

        if (steps.Count == 0)
        {
            throw new McpException("Name at least one change: title, body, fields, day, assign, unassign or workedOnNow.");
        }

        return await StepsAsync([], steps, actor, origin, itemId).ConfigureAwait(false);
    }

    public async Task<PlannerWriteResult> CloseAsync(McpCaller caller, string issue, string? reason, string? comment, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = McpIncidentActions.Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Closing an issue", http.HttpContext);

        var notPlanned = (reason ?? "done").Trim().ToLowerInvariant() switch
        {
            "done" or "completed" or "complete" => false,
            "not planned" or "not-planned" or "notplanned" or "wontfix" or "won't fix" or "duplicate" => true,
            _ => throw new McpException($"reason '{McpQuery.Echo(reason)}' is done or not planned."),
        };

        var item = Resolve(await BoardAsync(refresh: false, ct).ConfigureAwait(false), issue, openOnly: true);
        var done = new List<string>();

        if (!string.IsNullOrWhiteSpace(comment))
        {
            var (outcome, message, _) = await planner.CommentAsync(item.Repository, item.Number, comment, actor, ct, origin).ConfigureAwait(false);

            if (outcome != PlannerActionOutcome.Done)
            {
                throw new McpException($"Nothing changed: the comment was refused ({outcome}), {message}. The issue is still open.");
            }

            done.Add("the comment is on the issue");
        }

        return await StepsAsync(done, [() => planner.CloseAsync(item.ItemId, notPlanned, actor, ct, origin)], actor, origin, item.ItemId).ConfigureAwait(false);
    }

    public async Task<PlannerWriteResult> CommentAsync(McpCaller caller, string issue, string? text, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = McpIncidentActions.Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Commenting", http.HttpContext);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException("Give the text of the comment.");
        }

        var item = Resolve(await BoardAsync(refresh: false, ct).ConfigureAwait(false), issue, openOnly: false);
        var (outcome, message, _) = await planner.CommentAsync(item.Repository, item.Number, text, actor, ct, origin).ConfigureAwait(false);

        if (outcome != PlannerActionOutcome.Done)
        {
            throw new McpException($"The comment was not posted ({outcome}): {message}.");
        }

        return Answer(["the comment is on the issue"], null, actor, origin, item.ItemId,
            "Posted by Hephaisto's GitHub account and signed as relayed by an agent. A replan does not take it as a person's answer: that is a comment in the console, or the person's own on GitHub.");
    }

    // --- the parts ------------------------------------------------------------------------

    private async Task<PlannerSnapshot> BoardAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await planner.ReadAsync(refresh, ct).ConfigureAwait(false);

        return snapshot switch
        {
            { State: PlannerState.Off } => throw new McpException(
                "The planner is off on this install: GitHub and github.planner.enabled must both be on, with the board's owner and number."),
            { Board: null } => throw new McpException($"The board could not be read: {McpQuery.Echo(snapshot.Problem)}"),
            _ => snapshot,
        };
    }

    /// <summary>Runs the changes in order and stops at the first one that is not done.</summary>
    private async Task<PlannerWriteResult> StepsAsync(
        List<string> done, IReadOnlyList<Func<Task<PlannerActionResult>>> steps, string actor, AuditOrigin origin, string itemId)
    {
        foreach (var step in steps)
        {
            var result = await step().ConfigureAwait(false);

            if (result.Outcome == PlannerActionOutcome.Done)
            {
                done.Add(result.Message);
                continue;
            }

            if (done.Count == 0)
            {
                throw new McpException($"Nothing changed ({result.Outcome}): {result.Message}.");
            }

            return Answer(done, result.Message, actor, origin, itemId);
        }

        return Answer(done, null, actor, origin, itemId);
    }

    private PlannerWriteResult Answer(IReadOnlyList<string> done, string? refused, string actor, AuditOrigin origin, string itemId, string? note = null)
    {
        var kept = planner.Kept;
        var item = planner.BoardItem(itemId);

        return new PlannerWriteResult
        {
            Done = done,
            Refused = McpText.UntrustedOrNull(refused, 500),
            RecordedAs = McpText.Name(actor),
            ClaimedBy = McpText.NameOrNull(origin.ClaimedBy),
            ClaimVerified = false,
            Issue = kept?.Board is { } board && item is not null ? Row(board, item, kept.Entry(itemId)) : null,
            Note = note ?? (refused is null
                ? "On the board and in the console now; the audit trail names this token."
                : "GitHub refused the change above; nothing after it was tried. What is in done was made."),
        };
    }

    private Task<PlannerActionResult> DayAsync(string itemId, string day, string actor, AuditOrigin origin, CancellationToken ct) => day switch
    {
        "today" => planner.SetDayAsync(itemId, PlannerDay.Today, actor, ct, origin),
        "not today" => planner.SetDayAsync(itemId, PlannerDay.NotToday, actor, ct, origin),
        "none" => planner.SetDayAsync(itemId, PlannerDay.Undecided, actor, ct, origin),
        "tomorrow" => planner.ScheduleAsync(itemId, PlannerSchedule.Tomorrow, actor, ct, origin),
        "next week" => planner.ScheduleAsync(itemId, PlannerSchedule.NextWeek, actor, ct, origin),
        _ => planner.ScheduleAsync(itemId, PlannerSchedule.Backlog, actor, ct, origin),
    };

    /// <summary>The day as one of <see cref="Days"/>, or null when none was given.</summary>
    private static string? Day(string? day)
    {
        if (string.IsNullOrWhiteSpace(day))
        {
            return null;
        }

        var word = day.Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');

        return Days.Contains(word)
            ? word
            : throw new McpException($"day '{McpQuery.Echo(day)}' is not one of: {string.Join(", ", Days)}.");
    }

    private static bool IsNobody(string who) => who.Trim().ToLowerInvariant() is "nobody" or "none";

    private static bool Is(string? value, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(value, wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An issue of the board by <c>owner/repo#12</c>, its GitHub URL, <c>repo#12</c> or <c>#12</c>
    /// - the last two when only one issue of the board fits.
    /// </summary>
    internal static BoardItem Resolve(PlannerSnapshot snapshot, string? issue, bool openOnly)
    {
        var board = snapshot.Board!;
        var text = issue?.Trim() ?? string.Empty;
        IEnumerable<BoardItem> candidates;

        if (Reference().Match(text) is { Success: true } full)
        {
            var repository = full.Groups["repo"].Value;
            var number = int.Parse(full.Groups["n"].Value, CultureInfo.InvariantCulture);
            candidates = board.Items.Where(i => i.Number == number && string.Equals(i.Repository, repository, StringComparison.OrdinalIgnoreCase));
        }
        else if (Short().Match(text) is { Success: true } part)
        {
            var name = part.Groups["name"].Value;
            var number = int.Parse(part.Groups["n"].Value, CultureInfo.InvariantCulture);
            candidates = board.Items.Where(i => i.Number == number
                && (name.Length == 0 || i.Repository.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            throw new McpException($"issue '{McpQuery.Echo(issue)}' is not owner/repo#12, a GitHub issue URL, repo#12 or #12.");
        }

        var found = candidates.ToList();

        if (found.Count > 1)
        {
            throw new McpException(
                $"'{McpQuery.Echo(issue)}' fits {found.Count} issues of the board: {string.Join(", ", found.Select(i => i.Reference))}. Name it as owner/repo#12.");
        }

        if (found.Count == 0)
        {
            throw new McpException(
                $"'{McpQuery.Echo(issue)}' is not on the board {McpQuery.Echo(board.Title)}. list_planner_issues lists its issues; an issue opened elsewhere a minute ago shows after the next read (refresh: true).");
        }

        var item = found[0];

        if (openOnly && snapshot.Entry(item.ItemId) is null)
        {
            throw new McpException($"{item.Reference} is closed. A closed issue can be commented on, not changed.");
        }

        return item;
    }

    internal static PlannerIssueRow Row(Board board, BoardItem item, PlannerEntry? entry) => new()
    {
        Issue = item.Reference,
        Title = McpText.Untrusted(item.Title, 300),
        Url = McpText.NameOrNull(item.Url),
        Type = McpText.NameOrNull(item.Type),
        Open = item.Open,
        Fields = McpMap.From(item.Fields, 128),
        Day = !item.Open ? "closed" : PlannerService.DayWord(entry?.Day ?? PlannerDay.Undecided),
        PlannedFor = entry?.PlannedFor,
        CarriedOver = entry?.CarriedOver ?? false,
        WorkedOnNow = entry?.Active ?? false,
        NeedsDecision = entry is null ? null : entry.Reason switch
        {
            PlannerReason.MergedStillOpen => "every pull request that names it is merged, and it is still open",
            PlannerReason.SubIssuesDone => "every sub-issue is done, and it is still open",
            PlannerReason.PullRequestQuiet => "a pull request is open and nothing happened on it for days",
            PlannerReason.FieldEmpty => "a field the board asks for is empty",
            _ => null,
        },
        EmptyFields = entry is null ? [] : [.. entry.EmptyFields.Select(f => McpText.Name(f))],
        Assignees = [.. item.Assignees.Select(a => McpText.Name(a))],
        Labels = [.. item.Labels.Select(l => McpText.Name(l.Name))],
        Parent = item.ParentUrl is null ? null : board.Items.FirstOrDefault(i => i.Url == item.ParentUrl)?.Reference,
        SubIssues = item.SubIssues > 0 ? $"{item.SubIssuesDone}/{item.SubIssues}" : null,
        PullRequests = [.. item.PullRequests.Select(p => new PlannerPullRequest { PullRequest = p.Reference, State = p.State, Url = McpText.NameOrNull(p.Url) })],
        UpdatedAt = item.UpdatedAt,
        ClosedAt = item.ClosedAt,
    };

    [GeneratedRegex(@"^(?:https?://github\.com/)?(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?:#|/issues/)(?<n>\d{1,9})/?$")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.-]*)#(?<n>\d{1,9})$")]
    private static partial Regex Short();
}
