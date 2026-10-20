using System.Text.Json;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Planning;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Planner;

public enum PlannerState
{
    /// <summary><c>github.planner.enabled</c> is not set, or GitHub is off.</summary>
    Off = 0,

    /// <summary>A board was read. <see cref="PlannerSnapshot.Problem"/> says when the newest read failed and this is an older one.</summary>
    Read = 1,

    /// <summary>No board was ever read, and <see cref="PlannerSnapshot.Problem"/> says why.</summary>
    Unreadable = 2,
}

/// <summary>The board as the planner presents it, at one moment.</summary>
/// <param name="Assignees">The logins an issue can be assigned to: the agent's own account first, then the configured ones.</param>
/// <param name="Repositories">Where a new issue can be opened, the one chosen unless somebody picks another first.</param>
public sealed record PlannerSnapshot(
    PlannerState State,
    string? Problem,
    DateTimeOffset? ReadAt,
    Board? Board,
    IReadOnlyList<PlannerEntry> Entries,
    IReadOnlyList<PlannerGroup> Groups,
    IReadOnlyList<BoardItem> ClosedToday,
    IReadOnlyList<string> Assignees,
    string? BotLogin,
    IReadOnlyList<string>? Repositories = null)
{
    public static readonly PlannerSnapshot Off = new(PlannerState.Off, null, null, null, [], [], [], [], null);

    public IReadOnlyList<string> NewIssueRepositories => Repositories ?? [];

    public PlannerEntry? Entry(string itemId) => Entries.FirstOrDefault(e => e.Item.ItemId == itemId);

    /// <summary>What is being worked on right now, when anything is.</summary>
    public PlannerEntry? Active => Entries.FirstOrDefault(e => e.Active);
}

public enum PlannerActionOutcome
{
    Done = 0,

    /// <summary>The item, the field or the option is not on the board.</summary>
    NotFound = 1,

    /// <summary>GitHub said no. Nothing was changed.</summary>
    Refused = 2,

    /// <summary>The planner is off.</summary>
    Off = 3,

    /// <summary>The actor is one that may not decide: a model's name, or the system's.</summary>
    Forbidden = 4,
}

/// <param name="ItemId">The item a new issue became. Only for an issue that was opened.</param>
public sealed record PlannerActionResult(PlannerActionOutcome Outcome, string Message, PlannerSnapshot Snapshot, string? ItemId = null, BoardItem? Opened = null)
{
    public bool Done => Outcome == PlannerActionOutcome.Done;
}

/// <summary>A new issue, as a person wrote it on the planner.</summary>
/// <param name="Fields">Field name to option name, for the board's single-select fields.</param>
public sealed record PlannerNewIssue(string Repository, string Title, string? Body, string? Type, IReadOnlyDictionary<string, string> Fields);

/// <summary>Claude's newest analysis of an issue, as the comment the agent posted holds it.</summary>
/// <param name="Body">The comment without its marker: markdown a model wrote about somebody else's text.</param>
public sealed record PlannerAnalysis(string Body, DateTimeOffset At, string Url);

/// <summary>
/// The planner (v0.15.0): one project's issues, read when a person looks, and the writes one
/// click of theirs makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here writes by itself.</b> A write is a click. That is why the agent's mode does not
/// gate it - like closing an incident, it is the person's act and not the agent's - and why this
/// file is the only place that may call the client's writes for a project, a label or an assignee
/// (<c>GitHubClientTests</c> holds that).
/// </para>
/// <para>
/// <b>The board is read ahead of anybody looking</b> (#306): a whole board is a GraphQL page per 50
/// items, one after the other, and TR Planning's 169 items took 13 seconds - which a page load
/// used to wait for after every start and every half minute of quiet. <see cref="PlannerWarmer"/>
/// reads it at startup and every <see cref="WarmEvery"/> through <see cref="WarmAsync"/>, which
/// fetches without holding the gate, so a click or a page load is never queued behind it. Four
/// GraphQL points a read; reads only.
/// </para>
/// <para>
/// <b>The last read is served for <see cref="KeepFor"/></b>, a little longer than the warm period, so
/// a page load takes the kept board and a late warm read does not make anybody wait. "Refresh"
/// still reads while the person waits. A write does not read again: it changes the kept board the
/// way GitHub was just told to, so the item shows what was clicked rather than what a read replica
/// has not heard of yet - and a warm read that was already on its way when a click landed is
/// dropped rather than allowed to put the click back (<c>patches</c>).
/// </para>
/// <para>
/// <b>No audit, no action.</b> The row is written before GitHub is called, under the person's
/// name. A refusal is a second row, so the trail never says something happened that did not.
/// </para>
/// </remarks>
public sealed class PlannerService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<GitHubOptions> options,
    IClock clock,
    ILogger<PlannerService> logger)
{
    /// <summary>How long a read is served without asking GitHub again. Longer than <see cref="WarmEvery"/>, so a warm board is never older than this.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(3);

    /// <summary>How often <see cref="PlannerWarmer"/> reads the board while the planner is on.</summary>
    public static readonly TimeSpan WarmEvery = TimeSpan.FromMinutes(2);

    public const string AuditField = "planner.field";
    public const string AuditClose = "planner.close";
    public const string AuditDay = "planner.day";
    public const string AuditAssign = "planner.assign";
    public const string AuditRefused = "planner.refused";
    public const string AuditCreate = "planner.create";
    public const string AuditTriage = "planner.triage";
    public const string AuditTriaged = "planner.triaged";
    public const string AuditSchedule = "planner.schedule";
    public const string AuditComment = "planner.comment";
    public const string AuditActive = "planner.active";

    public const int MaxComment = 60_000;

    /// <summary>What begins every comment the agent posts with Claude's analysis. Only one by the agent's own account counts.</summary>
    public const string AnalysisMarker = "<!-- hephaisto:triage";

    public const int MaxTitle = 256;
    public const int MaxBody = 60_000;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim gate = new(1, 1);

    private PlannerSnapshot? kept;
    private string? botLogin;

    // How many times a click has changed the kept board. Under the gate, like kept itself: a warm
    // read notes it before fetching and drops what it fetched if it moved meanwhile.
    private long patches;

    /// <summary>
    /// The kept board changed: it was read, or a click changed it. For the navigation's badge,
    /// which shows the day's plan on every page and asks GitHub nothing. Whoever listens takes
    /// itself off again when it goes away - this service outlives every circuit.
    /// </summary>
    public event Action? Changed;

    public bool Enabled => options.CurrentValue is { Enabled: true, Planner.Enabled: true };

    /// <summary>What was last read, without asking GitHub. For the navigation, which may not cost a request per tab.</summary>
    public PlannerSnapshot? Kept => Enabled ? kept : null;

    /// <summary>
    /// The board. Asks GitHub when nothing is kept, when what is kept is older than
    /// <see cref="KeepFor"/>, or when <paramref name="refresh"/> says so. A read that fails keeps
    /// the last good board and says what went wrong.
    /// </summary>
    public async Task<PlannerSnapshot> ReadAsync(bool refresh, CancellationToken ct)
    {
        if (!Enabled)
        {
            return PlannerSnapshot.Off;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await ReadLockedAsync(refresh, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<PlannerActionResult> SetFieldAsync(string itemId, string field, string option, string actor, CancellationToken ct) =>
        ActAsync(itemId, actor, ct, async (client, board, entry) =>
        {
            if (board.Field(field) is not { } boardField)
            {
                return (PlannerActionOutcome.NotFound, $"the board has no field '{field}'", null);
            }

            if (boardField.Options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
            {
                return (PlannerActionOutcome.NotFound, $"'{option}' is not an option of {boardField.Name}", null);
            }

            var item = entry.Item;
            var before = item.Field(boardField.Name);

            await AuditAsync(AuditField, actor, $"{boardField.Name} of {item.Reference} set to {chosen.Name}", item,
                new { field = boardField.Name, from = before, to = chosen.Name }, ct).ConfigureAwait(false);

            var set = await client.SetProjectFieldAsync(board.Id, item.ItemId, boardField.Id, chosen.Id, ct).ConfigureAwait(false);

            if (!set.Ok)
            {
                return (PlannerActionOutcome.Refused, set.Describe(), null);
            }

            var fields = new Dictionary<string, string>(item.Fields, StringComparer.OrdinalIgnoreCase) { [boardField.Name] = chosen.Name };

            return (PlannerActionOutcome.Done, $"{boardField.Name} set to {chosen.Name}", item with { Fields = fields });
        });

    public Task<PlannerActionResult> CloseAsync(string itemId, bool notPlanned, string actor, CancellationToken ct) =>
        ActAsync(itemId, actor, ct, async (client, _, entry) =>
        {
            var item = entry.Item;
            var how = notPlanned ? "not planned" : "done";

            await AuditAsync(AuditClose, actor, $"{item.Reference} closed as {how}", item, new { reason = how }, ct).ConfigureAwait(false);

            var closed = await client.CloseIssueAsync(item.IssueId, notPlanned, ct).ConfigureAwait(false);

            return closed.Ok
                ? (PlannerActionOutcome.Done, $"closed as {how}", item with { Open = false, ClosedAt = clock.UtcNow })
                : (PlannerActionOutcome.Refused, closed.Describe(), null);
        });

    /// <summary>
    /// Sets the day: <see cref="PlannerQueue.TodayLabel"/>, <see cref="PlannerQueue.NotTodayLabel"/>,
    /// or neither. Whatever of the two is on the issue is taken off first, also the one that is
    /// asked for - a label that is already there would keep its old date, and the date is what
    /// says which day it is for.
    /// </summary>
    public Task<PlannerActionResult> SetDayAsync(string itemId, PlannerDay day, string actor, CancellationToken ct) =>
        ActAsync(itemId, actor, ct, async (client, board, entry) =>
        {
            var item = entry.Item;
            var word = DayWord(day);

            await AuditAsync(AuditDay, actor, $"{item.Reference}: {word}", item, new { day = word, was = DayWord(entry.Day) }, ct).ConfigureAwait(false);

            var (refused, changed) = await ApplyDayAsync(client, board, item, day, ct).ConfigureAwait(false);

            return refused is not null
                ? (PlannerActionOutcome.Refused, refused, changed)
                : (PlannerActionOutcome.Done, word, changed);
        });

    /// <summary>
    /// The labels of the day as <paramref name="day"/> says, and the board's day field emptied.
    /// A day other than today takes <see cref="PlannerQueue.ActiveLabel"/> off too: what is not
    /// planned for today is not worked on today. The item as it is now, also after a refusal.
    /// </summary>
    private async Task<(string? Refused, BoardItem Item)> ApplyDayAsync(IGitHubClient client, Board board, BoardItem item, PlannerDay day, CancellationToken ct)
    {
        var labels = item.Labels.ToList();
        string[] off = day == PlannerDay.Today
            ? [PlannerQueue.TodayLabel, PlannerQueue.NotTodayLabel]
            : [PlannerQueue.TodayLabel, PlannerQueue.NotTodayLabel, PlannerQueue.ActiveLabel];

        foreach (var label in off)
        {
            if (labels.FirstOrDefault(l => string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase)) is not { } present)
            {
                continue;
            }

            var removed = await client.RemoveLabelAsync(item.Repository, item.Number, present.Name, ct).ConfigureAwait(false);

            // Not found: somebody took it off on GitHub since the board was read. That is what was wanted.
            if (!removed.Ok && removed.Outcome != GitHubOutcome.NotFound)
            {
                return (removed.Describe(), item with { Labels = labels });
            }

            labels.Remove(present);
        }

        if (day != PlannerDay.Undecided)
        {
            var name = day == PlannerDay.Today ? PlannerQueue.TodayLabel : PlannerQueue.NotTodayLabel;
            var added = await client.AddLabelAsync(item.Repository, item.Number, name, ct).ConfigureAwait(false);

            if (!added.Ok)
            {
                return (added.Describe(), item with { Labels = labels });
            }

            labels.Add(new BoardLabel(name, clock.UtcNow));
        }

        // A day decided now replaces a later day planned before: the date would make it
        // planned again, or left over, on a day nobody chose any more.
        var (cleared, dates) = await ClearPlannedAsync(client, board, item, ct).ConfigureAwait(false);

        return cleared is { } refused
            ? (refused, item with { Labels = labels })
            : (null, item with { Labels = labels, Dates = dates });
    }

    /// <summary>
    /// Starts or stops working on an item: <see cref="PlannerQueue.ActiveLabel"/> on it, and off
    /// every other open issue of the board - one thing at a time. Only an item planned for today
    /// can be started; one left over from an earlier day is planned for today again with it.
    /// </summary>
    public Task<PlannerActionResult> SetActiveAsync(string itemId, bool active, string actor, CancellationToken ct) =>
        ActManyAsync(itemId, actor, ct, async (client, board, entry) =>
        {
            var item = entry.Item;
            var label = item.Labels.FirstOrDefault(l => string.Equals(l.Name, PlannerQueue.ActiveLabel, StringComparison.OrdinalIgnoreCase));

            if (!active)
            {
                if (label is null)
                {
                    return (PlannerActionOutcome.Done, "it was not being worked on", []);
                }

                await AuditAsync(AuditActive, actor, $"{item.Reference}: no longer worked on", item, new { active = false }, ct).ConfigureAwait(false);

                var removed = await client.RemoveLabelAsync(item.Repository, item.Number, label.Name, ct).ConfigureAwait(false);

                return !removed.Ok && removed.Outcome != GitHubOutcome.NotFound
                    ? (PlannerActionOutcome.Refused, removed.Describe(), [])
                    : (PlannerActionOutcome.Done, "no longer worked on", [item with { Labels = [.. item.Labels.Where(l => !ReferenceEquals(l, label))] }]);
            }

            if (entry.Day != PlannerDay.Today)
            {
                // Like a field the board does not have: refused before anything is written, not by GitHub.
                return (PlannerActionOutcome.NotFound, "only an issue planned for today can be worked on now: plan it for today first", []);
            }

            var was = kept?.Active;

            await AuditAsync(AuditActive, actor, $"{item.Reference}: worked on now", item,
                new { active = true, was = was is null || was.Item.ItemId == item.ItemId ? null : was.Item.Reference }, ct).ConfigureAwait(false);

            var changed = new List<BoardItem>();

            // Off the others first: should GitHub refuse halfway, nothing shows two at once.
            foreach (var other in board.Items.Where(i => i.Open && i.ItemId != item.ItemId).ToList())
            {
                if (other.Labels.FirstOrDefault(l => string.Equals(l.Name, PlannerQueue.ActiveLabel, StringComparison.OrdinalIgnoreCase)) is not { } on)
                {
                    continue;
                }

                var removed = await client.RemoveLabelAsync(other.Repository, other.Number, on.Name, ct).ConfigureAwait(false);

                if (!removed.Ok && removed.Outcome != GitHubOutcome.NotFound)
                {
                    return (PlannerActionOutcome.Refused, $"{other.Reference} is still worked on: {removed.Describe()}", changed);
                }

                changed.Add(other with { Labels = [.. other.Labels.Where(l => !ReferenceEquals(l, on))] });
            }

            if (entry.CarriedOver)
            {
                var (refused, planned) = await ApplyDayAsync(client, board, item, PlannerDay.Today, ct).ConfigureAwait(false);
                item = planned;

                if (refused is not null)
                {
                    return (PlannerActionOutcome.Refused, refused, [.. changed, item]);
                }
            }

            if (label is null)
            {
                var added = await client.AddLabelAsync(item.Repository, item.Number, PlannerQueue.ActiveLabel, ct).ConfigureAwait(false);

                if (!added.Ok)
                {
                    return (PlannerActionOutcome.Refused, added.Describe(), [.. changed, item]);
                }

                item = item with { Labels = [.. item.Labels, new BoardLabel(PlannerQueue.ActiveLabel, clock.UtcNow)] };
            }

            return (PlannerActionOutcome.Done, "worked on now", [.. changed, item]);
        });

    public Task<PlannerActionResult> AssignAsync(string itemId, string login, bool assign, string actor, CancellationToken ct) =>
        ActAsync(itemId, actor, ct, async (client, _, entry) =>
        {
            var item = entry.Item;

            if (kept!.Assignees.FirstOrDefault(a => string.Equals(a, login, StringComparison.OrdinalIgnoreCase)) is not { } account)
            {
                return (PlannerActionOutcome.NotFound, $"'{login}' is not an account the planner assigns to", null);
            }

            await AuditAsync(AuditAssign, actor, assign ? $"{item.Reference} assigned to {account}" : $"{account} taken off {item.Reference}", item,
                new { login = account, assigned = assign }, ct).ConfigureAwait(false);

            var done = assign
                ? await client.AddAssigneeAsync(item.Repository, item.Number, account, ct).ConfigureAwait(false)
                : await client.RemoveAssigneeAsync(item.Repository, item.Number, account, ct).ConfigureAwait(false);

            if (!done.Ok)
            {
                return (PlannerActionOutcome.Refused, done.Describe(), null);
            }

            if (!done.Value)
            {
                return (PlannerActionOutcome.Refused, $"GitHub did not {(assign ? "assign" : "take off")} {account}: the account may not be assigned in {item.Repository}", null);
            }

            var assignees = item.Assignees.Where(a => !string.Equals(a, account, StringComparison.OrdinalIgnoreCase)).ToList();

            if (assign)
            {
                assignees.Add(account);
            }

            return (PlannerActionOutcome.Done, assign ? $"assigned to {account}" : $"{account} taken off", item with { Assignees = assignees });
        });

    /// <summary>
    /// Opens an issue, puts it on the board and sets the fields that were chosen - three requests,
    /// each of which can fail after the one before it worked. The issue is never taken back: what
    /// failed is said, with the issue's number, and the issue is on GitHub to be fixed by hand.
    /// </summary>
    public async Task<PlannerActionResult> OpenIssueAsync(PlannerNewIssue issue, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (!Enabled)
        {
            return new(PlannerActionOutcome.Off, "the planner is off", PlannerSnapshot.Off);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var snapshot = kept is { Board: not null } ? kept : await ReadLockedAsync(refresh: false, ct).ConfigureAwait(false);

            if (IncidentStateMachine.IsForbiddenGranter(who))
            {
                return new(PlannerActionOutcome.Forbidden, $"'{who}' may not decide here: what the planner does is a person's click.", snapshot);
            }

            if (snapshot.Board is not { } board)
            {
                return new(PlannerActionOutcome.NotFound, snapshot.Problem ?? "the board could not be read", snapshot);
            }

            var title = issue.Title?.Trim() ?? string.Empty;

            if (title.Length == 0 || title.Length > MaxTitle)
            {
                return new(PlannerActionOutcome.NotFound, $"a title is one line of 1 to {MaxTitle} characters", snapshot);
            }

            if (issue.Body is { Length: > MaxBody })
            {
                return new(PlannerActionOutcome.NotFound, $"a description holds at most {MaxBody} characters", snapshot);
            }

            if (snapshot.NewIssueRepositories.FirstOrDefault(r => string.Equals(r, issue.Repository?.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } repository)
            {
                return new(PlannerActionOutcome.NotFound, $"'{issue.Repository}' is not a repository the planner opens issues in", snapshot);
            }

            string? type = null;

            if (!string.IsNullOrWhiteSpace(issue.Type))
            {
                type = board.Types.FirstOrDefault(t => string.Equals(t, issue.Type.Trim(), StringComparison.OrdinalIgnoreCase));

                if (type is null)
                {
                    return new(PlannerActionOutcome.NotFound, $"'{issue.Type}' is not an issue type of the organisation", snapshot);
                }
            }

            var chosen = new List<(BoardField Field, BoardOption Option)>();

            foreach (var (name, value) in issue.Fields)
            {
                if (board.Field(name) is not { } field
                    || field.Options.FirstOrDefault(o => string.Equals(o.Name, value, StringComparison.OrdinalIgnoreCase)) is not { } option)
                {
                    return new(PlannerActionOutcome.NotFound, $"'{value}' is not an option of a field '{name}' of the board", snapshot);
                }

                chosen.Add((field, option));
            }

            await using var scope = scopes.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
            {
                return new(PlannerActionOutcome.Off, "the planner is off", PlannerSnapshot.Off);
            }

            await AuditAsync(AuditCreate, who, $"issue '{title}' opened in {repository}", repository, null, null, null,
                new { title, type, fields = chosen.ToDictionary(c => c.Field.Name, c => c.Option.Name) }, ct).ConfigureAwait(false);

            var body = string.IsNullOrWhiteSpace(issue.Body) ? null : issue.Body;
            var created = await client.CreateIssueAsync(repository, title, body, type, ct).ConfigureAwait(false);

            if (!created.Ok)
            {
                return await RefusedAsync(who, repository, null, null, created.Describe(), snapshot, ct).ConfigureAwait(false);
            }

            var opened = created.Value!;
            var reference = $"{repository}#{opened.Number}";
            var added = await client.AddProjectItemAsync(board.Id, opened.NodeId, ct).ConfigureAwait(false);

            if (!added.Ok)
            {
                return await RefusedAsync(who, repository, opened.Number, opened.Url,
                    $"{reference} was opened, and GitHub would not put it on the board: {added.Describe()}", snapshot, ct).ConfigureAwait(false);
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();

            foreach (var (field, option) in chosen)
            {
                var set = await client.SetProjectFieldAsync(board.Id, added.Value!, field.Id, option.Id, ct).ConfigureAwait(false);

                if (set.Ok)
                {
                    fields[field.Name] = option.Name;
                }
                else
                {
                    failed.Add($"{field.Name} ({set.Describe()})");
                }
            }

            var now = clock.UtcNow;
            var item = new BoardItem(
                added.Value!, opened.NodeId, repository, opened.Number, opened.Title, opened.Url, opened.Type ?? type,
                opened.Author.Login, Open: true, now, now, ClosedAt: null, ParentUrl: null, SubIssues: 0, SubIssuesDone: 0,
                fields, [.. opened.Labels.Select(l => new BoardLabel(l, now))], [], [], body);

            snapshot = kept = Present(board with { Items = [.. board.Items.Where(i => i.ItemId != item.ItemId), item] }, snapshot.ReadAt ?? now, snapshot.Problem);
            patches++;
            Changed?.Invoke();

            if (failed.Count > 0)
            {
                var message = $"{reference} was opened and put on the board; GitHub would not set {string.Join(", ", failed)}";

                logger.LogWarning("The planner opened {Issue} for {Actor}, and some fields were refused: {Detail}", reference, who, message);
                await AuditAsync(AuditRefused, who, message, repository, opened.Number, opened.Url, item.ItemId, new { detail = message }, ct).ConfigureAwait(false);

                return new(PlannerActionOutcome.Refused, message, snapshot, item.ItemId, item);
            }

            return new(PlannerActionOutcome.Done, $"{reference} opened", snapshot, item.ItemId, item);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// What Claude answered for an issue a person asked about: the labels it chose that the
    /// repository has, put on, and its analysis as one comment. Labels are only ever added here,
    /// never taken off, and none but the repository's own - GitHub would create any other.
    /// </summary>
    /// <param name="requestedBy">Who asked: the audit names them, since the agent's account is only the hand.</param>
    public async Task<(bool Done, string Message)> ApplyTriageAsync(
        TriageIssue issue, Hephaisto.Agent.CodeFix.Contract.TriageResult result, IReadOnlyList<GitHubLabel> repositoryLabels, string requestedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(result);

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return (false, "the planner is off");
        }

        var labels = result.Labels
            .Select(l => repositoryLabels.FirstOrDefault(r => string.Equals(r.Name, l?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name)
            .Where(l => l is not null && !string.Equals(l, PlannerQueue.TodayLabel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(l, PlannerQueue.NotTodayLabel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(l, PlannerQueue.ActiveLabel, StringComparison.OrdinalIgnoreCase))
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        await AuditAsync(AuditTriaged, requestedBy,
            $"Claude analysed {issue.Repository}#{issue.Number}{(labels.Count > 0 ? $" and labelled it {string.Join(", ", labels)}" : string.Empty)}",
            issue.Repository, issue.Number, issue.Url, null,
            new { attempt = result.AttemptId, labels, cost = result.CostUsd, billing = result.Billing }, ct).ConfigureAwait(false);

        var refused = new List<string>();

        foreach (var label in labels)
        {
            var added = await client.AddLabelAsync(issue.Repository, issue.Number, label, ct).ConfigureAwait(false);

            if (!added.Ok)
            {
                refused.Add($"{label} ({added.Describe()})");
            }
        }

        var comment = await client.CreateCommentAsync(issue.Repository, issue.Number, TriageComment.Render(issue, result, labels, requestedBy), ct).ConfigureAwait(false);

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (kept is { Board: { } board } && board.Items.FirstOrDefault(i => string.Equals(i.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase) && i.Number == issue.Number) is { } item)
            {
                var now = clock.UtcNow;
                var on = item.Labels.ToList();
                on.AddRange(labels.Where(l => !refused.Any(r => r.StartsWith(l + " ", StringComparison.Ordinal)) && !on.Any(o => string.Equals(o.Name, l, StringComparison.OrdinalIgnoreCase))).Select(l => new BoardLabel(l, now)));
                var changed = item with { Labels = on, Comments = item.Comments + (comment.Ok ? 1 : 0) };

                kept = Present(board with { Items = [.. board.Items.Select(i => i.ItemId == changed.ItemId ? changed : i)] }, kept.ReadAt ?? now, kept.Problem);
                patches++;
                Changed?.Invoke();
            }
        }
        finally
        {
            gate.Release();
        }

        if (!comment.Ok || refused.Count > 0)
        {
            var message = string.Join("; ", new[]
            {
                refused.Count > 0 ? $"GitHub would not put on {string.Join(", ", refused)}" : null,
                comment.Ok ? null : $"the analysis could not be posted: {comment.Describe()}",
            }.Where(m => m is not null));

            await AuditAsync(AuditRefused, requestedBy, $"{issue.Repository}#{issue.Number}: {message}", issue.Repository, issue.Number, issue.Url, null, new { detail = message }, ct).ConfigureAwait(false);

            return (false, message);
        }

        return (true, labels.Count > 0 ? $"labelled {string.Join(", ", labels)}, and the analysis is on the issue" : "the analysis is on the issue; no label fitted");
    }

    /// <summary>
    /// The newest comment with Claude's analysis of an issue: the newest one that begins with
    /// <see cref="AnalysisMarker"/> AND was written by the agent's own account - anybody can type
    /// the marker. Null when there is none, or when the comments cannot be read.
    /// </summary>
    public async Task<PlannerAnalysis?> AnalysisAsync(string repository, int number, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return null;
        }

        botLogin ??= await BotLoginAsync(client, ct).ConfigureAwait(false);

        var comments = await client.ListCommentsAsync(repository, number, since: null, etag: null, ct).ConfigureAwait(false);

        if (!comments.Ok || botLogin is null)
        {
            return null;
        }

        return comments.Value!
            .Where(c => c.Body.StartsWith(AnalysisMarker, StringComparison.Ordinal) && string.Equals(c.Author.Login, botLogin, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new PlannerAnalysis(TriageComment.WithoutMarker(c.Body), c.CreatedAt, c.Url))
            .FirstOrDefault();
    }

    /// <summary>An audit row for what the planner starts beside a click on an item: asking Claude.</summary>
    public Task RecordAsync(string type, string actor, string summary, string repository, int number, string? url, object detail, CancellationToken ct) =>
        AuditAsync(type, actor, summary, repository, number, url, null, detail, ct);

    private async Task<PlannerActionResult> RefusedAsync(string actor, string repository, int? number, string? url, string message, PlannerSnapshot snapshot, CancellationToken ct)
    {
        logger.LogWarning("The planner's new issue by {Actor} in {Repository} was refused: {Detail}", actor, repository, message);

        await AuditAsync(AuditRefused, actor, $"{repository}: GitHub refused, {message}", repository, number, url, null, new { detail = message }, ct).ConfigureAwait(false);

        return new(PlannerActionOutcome.Refused, message, snapshot);
    }

    public static string DayWord(PlannerDay day) => day switch
    {
        PlannerDay.Today => "today",
        PlannerDay.NotToday => "not today",
        PlannerDay.Later => "a later day",
        _ => "not decided",
    };

    public static string ScheduleWord(PlannerSchedule schedule) => schedule switch
    {
        PlannerSchedule.Tomorrow => "tomorrow",
        PlannerSchedule.NextWeek => "next week",
        _ => "backlog",
    };

    /// <summary>
    /// Plans an item for a later day - the board's date field set to tomorrow or to next
    /// Monday - or puts it back into the backlog: no day, and Status <c>BacklogStatus</c>. The
    /// two labels of the day come off in every case, and the one of being worked on: what was
    /// decided for today no longer holds.
    /// </summary>
    public Task<PlannerActionResult> ScheduleAsync(string itemId, PlannerSchedule schedule, string actor, CancellationToken ct) =>
        ActAsync(itemId, actor, ct, async (client, board, entry) =>
        {
            var item = entry.Item;
            var planner = options.CurrentValue.Planner;
            var day = PlannerQueue.DayFor(schedule, clock.UtcNow);
            var field = board.DateField(planner.DayField);

            if (day is not null && field is null)
            {
                return (PlannerActionOutcome.NotFound, $"the board has no date field '{planner.DayField}' to plan a later day by", null);
            }

            BoardField? status = null;
            BoardOption? backlog = null;

            if (schedule == PlannerSchedule.Backlog)
            {
                status = board.Field(PlannerQueue.StatusField);
                backlog = status?.Options.FirstOrDefault(o => string.Equals(o.Name, planner.BacklogStatus, StringComparison.OrdinalIgnoreCase));

                if (backlog is null)
                {
                    return (PlannerActionOutcome.NotFound, $"the board's {PlannerQueue.StatusField} has no option '{planner.BacklogStatus}'", null);
                }
            }

            var word = day is { } on ? $"planned for {on.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture)}" : "back in the backlog";

            await AuditAsync(AuditSchedule, actor, $"{item.Reference}: {word}", item,
                new { schedule = ScheduleWord(schedule), day = day?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), was = DayWord(entry.Day) }, ct).ConfigureAwait(false);

            var labels = item.Labels.ToList();

            foreach (var present in labels.Where(l => string.Equals(l.Name, PlannerQueue.TodayLabel, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(l.Name, PlannerQueue.NotTodayLabel, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(l.Name, PlannerQueue.ActiveLabel, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var removed = await client.RemoveLabelAsync(item.Repository, item.Number, present.Name, ct).ConfigureAwait(false);

                if (!removed.Ok && removed.Outcome != GitHubOutcome.NotFound)
                {
                    return (PlannerActionOutcome.Refused, removed.Describe(), item with { Labels = labels });
                }

                labels.Remove(present);
            }

            var dates = new Dictionary<string, DateOnly>(item.Dates ?? new Dictionary<string, DateOnly>(), StringComparer.OrdinalIgnoreCase);

            if (day is { } planned)
            {
                var set = await client.SetProjectDateAsync(board.Id, item.ItemId, field!.Id, planned, ct).ConfigureAwait(false);

                if (!set.Ok)
                {
                    return (PlannerActionOutcome.Refused, set.Describe(), item with { Labels = labels });
                }

                dates[field.Name] = planned;

                return (PlannerActionOutcome.Done, word, item with { Labels = labels, Dates = dates });
            }

            var (cleared, after) = await ClearPlannedAsync(client, board, item, ct).ConfigureAwait(false);

            if (cleared is { } refused)
            {
                return (PlannerActionOutcome.Refused, refused, item with { Labels = labels });
            }

            var moved = await client.SetProjectFieldAsync(board.Id, item.ItemId, status!.Id, backlog!.Id, ct).ConfigureAwait(false);

            if (!moved.Ok)
            {
                return (PlannerActionOutcome.Refused, moved.Describe(), item with { Labels = labels, Dates = after });
            }

            var fields = new Dictionary<string, string>(item.Fields, StringComparer.OrdinalIgnoreCase) { [status.Name] = backlog.Name };

            return (PlannerActionOutcome.Done, word, item with { Labels = labels, Dates = after, Fields = fields });
        });

    /// <summary>
    /// An issue's comments, oldest first - the first page of a hundred, as GitHub lists them.
    /// Only for an issue on the board. Null when it is not, or when GitHub said no.
    /// </summary>
    public async Task<IReadOnlyList<GitHubComment>?> CommentsAsync(string repository, int number, CancellationToken ct)
    {
        if (!Enabled || OnBoard(repository, number) is null)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return null;
        }

        var comments = await client.ListCommentsAsync(repository, number, since: null, etag: null, ct).ConfigureAwait(false);

        return comments.Ok ? comments.Value : null;
    }

    /// <summary>
    /// A comment on an issue of the board, open or closed, written by a person in the console and
    /// posted by the agent's account - so it ends by saying whose words they are.
    /// </summary>
    public async Task<(PlannerActionOutcome Outcome, string Message, GitHubComment? Comment)> CommentAsync(
        string repository, int number, string body, string actor, CancellationToken ct)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (!Enabled)
        {
            return (PlannerActionOutcome.Off, "the planner is off", null);
        }

        if (IncidentStateMachine.IsForbiddenGranter(who))
        {
            return (PlannerActionOutcome.Forbidden, $"'{who}' may not write here: a comment from the planner is a person's.", null);
        }

        var text = body?.Trim() ?? string.Empty;

        if (text.Length == 0 || text.Length > MaxComment)
        {
            return (PlannerActionOutcome.NotFound, $"a comment holds 1 to {MaxComment} characters", null);
        }

        if (OnBoard(repository, number) is not { } item)
        {
            return (PlannerActionOutcome.NotFound, $"{repository}#{number} is not on the board", null);
        }

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return (PlannerActionOutcome.Off, "the planner is off", null);
        }

        await AuditAsync(AuditComment, who, $"{item.Reference}: a comment", item, new { length = text.Length }, ct).ConfigureAwait(false);

        var posted = await client.CreateCommentAsync(item.Repository, item.Number, PlannerComment.Body(text, who), ct).ConfigureAwait(false);

        if (!posted.Ok)
        {
            await AuditAsync(AuditRefused, who, $"{item.Reference}: GitHub refused the comment, {posted.Describe()}", item, new { detail = posted.Describe() }, ct).ConfigureAwait(false);

            return (PlannerActionOutcome.Refused, posted.Describe(), null);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (kept is { Board: { } board } && board.Items.FirstOrDefault(i => i.ItemId == item.ItemId) is { } current)
            {
                var changed = current with { Comments = current.Comments + 1 };
                kept = Present(board with { Items = [.. board.Items.Select(i => i.ItemId == changed.ItemId ? changed : i)] }, kept.ReadAt ?? clock.UtcNow, kept.Problem);
                patches++;
                Changed?.Invoke();
            }
        }
        finally
        {
            gate.Release();
        }

        return (PlannerActionOutcome.Done, "the comment is on the issue", posted.Value);
    }

    /// <summary>
    /// The board's issues - open, closed, or both - whose title, description, reference or labels
    /// hold every word of <paramref name="query"/>, whatever the case; the newest first. From the
    /// kept board: no request to GitHub per keystroke.
    /// </summary>
    public IReadOnlyList<BoardItem> Search(string? query, bool? open, int limit = 100)
    {
        if (Kept is not { Board: { } board })
        {
            return [];
        }

        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [.. board.Items
            .Where(i => open is null || i.Open == open)
            .Where(i => words.All(w => Matches(i, w)))
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(i => i.Number)
            .Take(limit)];

        static bool Matches(BoardItem item, string word) =>
            item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || item.Reference.Contains(word, StringComparison.OrdinalIgnoreCase)
            || ("#" + item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Equals(word, StringComparison.OrdinalIgnoreCase)
            || (item.Body?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Labels.Any(l => l.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The console's own page of an issue of the board - its detail in the today view - or null
    /// for one the board does not hold. What an issue reference in rendered markdown links to.
    /// </summary>
    public string? DetailHrefFor(string repository, int number) =>
        OnBoard(repository, number) is { } item ? $"planner?view=today&detail={Uri.EscapeDataString(item.ItemId)}" : null;

    /// <summary>An item of the kept board by its id, open or closed.</summary>
    public BoardItem? BoardItem(string itemId) => Kept?.Board?.Items.FirstOrDefault(i => i.ItemId == itemId);

    private BoardItem? OnBoard(string repository, int number) =>
        kept?.Board?.Items.FirstOrDefault(i => string.Equals(i.Repository, repository?.Trim(), StringComparison.OrdinalIgnoreCase) && i.Number == number);

    /// <summary>Empties the board's day field on an item that has one. A message when GitHub said no.</summary>
    private async Task<(string? Refused, IReadOnlyDictionary<string, DateOnly>? Dates)> ClearPlannedAsync(IGitHubClient client, Board board, BoardItem item, CancellationToken ct)
    {
        if (board.DateField(options.CurrentValue.Planner.DayField) is not { } field || item.Date(field.Name) is null)
        {
            return (null, item.Dates);
        }

        var cleared = await client.SetProjectDateAsync(board.Id, item.ItemId, field.Id, null, ct).ConfigureAwait(false);

        if (!cleared.Ok)
        {
            return (cleared.Describe(), item.Dates);
        }

        var dates = new Dictionary<string, DateOnly>(item.Dates!, StringComparer.OrdinalIgnoreCase);
        dates.Remove(field.Name);

        return (null, dates);
    }

    /// <summary>
    /// One click: who, on what, and then <paramref name="act"/>, which answers with the outcome,
    /// a sentence, and the item as it is now when something of it changed - also after a refusal
    /// halfway, when a label came off and the next one did not go on.
    /// </summary>
    private Task<PlannerActionResult> ActAsync(
        string itemId,
        string actor,
        CancellationToken ct,
        Func<IGitHubClient, Board, PlannerEntry, Task<(PlannerActionOutcome Outcome, string Message, BoardItem? Changed)>> act) =>
        ActManyAsync(itemId, actor, ct, async (client, board, entry) =>
        {
            var (outcome, message, changed) = await act(client, board, entry).ConfigureAwait(false);

            return (outcome, message, changed is null ? [] : [changed]);
        });

    /// <summary>One click that may change more than its own item: starting work on one stops it on another.</summary>
    private async Task<PlannerActionResult> ActManyAsync(
        string itemId,
        string actor,
        CancellationToken ct,
        Func<IGitHubClient, Board, PlannerEntry, Task<(PlannerActionOutcome Outcome, string Message, IReadOnlyList<BoardItem> Changed)>> act)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (!Enabled)
        {
            return new(PlannerActionOutcome.Off, "the planner is off", PlannerSnapshot.Off);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // A click acts on the board the person is looking at, however long they looked: a
            // read here would cost seconds before every click of a slow walk, and could hand
            // back a replica that has not heard of the click before. Only with nothing kept at
            // all - the API, on a fresh process - is the board read first.
            var snapshot = kept is { Board: not null } ? kept : await ReadLockedAsync(refresh: false, ct).ConfigureAwait(false);

            if (IncidentStateMachine.IsForbiddenGranter(who))
            {
                return new(PlannerActionOutcome.Forbidden, $"'{who}' may not decide here: what the planner does is a person's click.", snapshot);
            }

            if (snapshot.Board is not { } board || snapshot.Entry(itemId) is not { } entry)
            {
                return new(PlannerActionOutcome.NotFound, "that item is not among the board's open issues", snapshot);
            }

            await using var scope = scopes.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
            {
                return new(PlannerActionOutcome.Off, "the planner is off", PlannerSnapshot.Off);
            }

            var (outcome, message, changed) = await act(client, board, entry).ConfigureAwait(false);

            if (changed.Count > 0)
            {
                var byId = changed.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Last());

                snapshot = kept = Present(
                    board with { Items = [.. board.Items.Select(i => byId.TryGetValue(i.ItemId, out var now) ? now : i)] },
                    snapshot.ReadAt ?? clock.UtcNow,
                    snapshot.Problem);

                patches++;
                Changed?.Invoke();
            }

            if (outcome == PlannerActionOutcome.Refused)
            {
                logger.LogWarning("The planner's click by {Actor} on {Item} was refused: {Detail}", who, entry.Item.Reference, message);

                await AuditAsync(AuditRefused, who, $"{entry.Item.Reference}: GitHub refused, {message}", entry.Item, new { detail = message }, ct).ConfigureAwait(false);
            }

            return new(outcome, message, snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads the board ahead of anybody looking, for <see cref="PlannerWarmer"/>. With nothing kept
    /// yet it reads under the gate, so a page opened meanwhile waits for this read instead of
    /// starting a second one; after that it fetches without the gate and keeps what it fetched
    /// only if no click changed the kept board in the meantime.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        long before;

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (kept is not { Board: not null })
            {
                await ReadLockedAsync(refresh: true, ct).ConfigureAwait(false);
                return;
            }

            before = patches;
        }
        finally
        {
            gate.Release();
        }

        var at = clock.UtcNow;
        var (read, login) = await FetchAsync(ct).ConfigureAwait(false);

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (patches != before)
            {
                logger.LogDebug("A click changed the planner's board during a warm read; the read is dropped and the next one is the retry.");
                return;
            }

            Keep(read, login, at);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PlannerSnapshot> ReadLockedAsync(bool refresh, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (!refresh && kept is { ReadAt: { } at, Problem: null } && now - at < KeepFor)
        {
            return kept;
        }

        var (read, login) = await FetchAsync(ct).ConfigureAwait(false);

        return Keep(read, login, now);
    }

    /// <summary>One read of the board from GitHub. Touches nothing kept, so it may run outside the gate. Null when there is no client.</summary>
    private async Task<(GitHubResult<Board>? Read, string? Login)> FetchAsync(CancellationToken ct)
    {
        var planner = options.CurrentValue.Planner;

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return (null, null);
        }

        var login = botLogin ?? await BotLoginAsync(client, ct).ConfigureAwait(false);
        var read = await client.GetProjectAsync(planner.Owner.Trim(), planner.Number, ct).ConfigureAwait(false);

        return (read, login);
    }

    /// <summary>Makes a read the kept board. Under the gate.</summary>
    private PlannerSnapshot Keep(GitHubResult<Board>? fetched, string? login, DateTimeOffset readAt)
    {
        if (fetched is not { } read)
        {
            return PlannerSnapshot.Off;
        }

        botLogin ??= login;

        if (!read.Ok)
        {
            var planner = options.CurrentValue.Planner;
            var problem = read.Outcome == GitHubOutcome.NotFound
                ? $"GitHub knows no project {planner.Number} of the organisation {planner.Owner}, or the token may not see it ({read.Detail})"
                : read.Describe();

            logger.LogWarning("The planner could not read {Owner}/projects/{Number}: {Problem}", planner.Owner, planner.Number, problem);

            // The last good board stays, and says that it is old.
            return kept = kept is { Board: not null }
                ? kept with { Problem = problem }
                : new PlannerSnapshot(PlannerState.Unreadable, problem, null, null, [], [], [], [], botLogin);
        }

        kept = Present(read.Value!, readAt, problem: null);
        Changed?.Invoke();

        return kept;
    }

    private PlannerSnapshot Present(Board board, DateTimeOffset readAt, string? problem)
    {
        var planner = options.CurrentValue.Planner;
        var now = clock.UtcNow;
        var entries = PlannerQueue.Build(
            board, new PlannerRules(planner.RequiredFields, TimeSpan.FromDays(planner.StaleAfterDays), planner.DayField), now);

        var assignees = new[] { botLogin }
            .Concat(planner.Assignees)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Where a new issue goes: the configured repositories first - the first of them is the
        // default - then the board's own, the one with the most issues first.
        var repositories = planner.Repositories
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Concat(board.Items
                .GroupBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlannerSnapshot(
            PlannerState.Read, problem, readAt, board, entries, PlannerQueue.Group(board, entries), PlannerQueue.ClosedToday(board, now), assignees, botLogin, repositories);
    }

    /// <summary>The account the token belongs to, unless the configuration names another: the one "assign to Hephaisto" means.</summary>
    private async Task<string?> BotLoginAsync(IGitHubClient client, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.CurrentValue.BotLogin))
        {
            return options.CurrentValue.BotLogin.Trim();
        }

        var user = await client.GetAuthenticatedUserAsync(ct).ConfigureAwait(false);

        return user.Ok ? user.Value!.Login : null;
    }

    private Task AuditAsync(string type, string actor, string summary, BoardItem item, object detail, CancellationToken ct) =>
        AuditAsync(type, actor, summary, item.Repository, item.Number, item.Url, item.ItemId, detail, ct);

    private async Task AuditAsync(string type, string actor, string summary, string repository, int? number, string? url, string? itemId, object detail, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAuditRepository>().AppendAsync(
            new AuditEvent
            {
                At = clock.UtcNow,
                Type = type,
                Actor = actor,
                Summary = summary,
                Detail = JsonSerializer.Serialize(
                    new { repository, number, url, item = itemId, change = detail }, AuditJson),
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
            },
            ct).ConfigureAwait(false);
    }
}
