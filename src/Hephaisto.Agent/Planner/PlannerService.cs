using System.Text.Json;
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
public sealed record PlannerSnapshot(
    PlannerState State,
    string? Problem,
    DateTimeOffset? ReadAt,
    Board? Board,
    IReadOnlyList<PlannerEntry> Entries,
    IReadOnlyList<PlannerGroup> Groups,
    IReadOnlyList<BoardItem> ClosedToday,
    IReadOnlyList<string> Assignees,
    string? BotLogin)
{
    public static readonly PlannerSnapshot Off = new(PlannerState.Off, null, null, null, [], [], [], [], null);

    public PlannerEntry? Entry(string itemId) => Entries.FirstOrDefault(e => e.Item.ItemId == itemId);
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

public sealed record PlannerActionResult(PlannerActionOutcome Outcome, string Message, PlannerSnapshot Snapshot)
{
    public bool Done => Outcome == PlannerActionOutcome.Done;
}

/// <summary>
/// The planner (v0.15.0): one project's issues, read when a person looks, and the writes one
/// click of theirs makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here runs by itself.</b> There is no timer and no pass: a read is a page load or
/// the API being asked, a write is a click. That is why the agent's mode does not gate it - like
/// closing an incident, it is the person's act and not the agent's - and why this file is the only
/// place that may call the client's writes for a project, a label or an assignee
/// (<c>GitHubClientTests</c> holds that).
/// </para>
/// <para>
/// <b>The last read is kept for <see cref="KeepFor"/></b>, because every page renders twice and a
/// person who walks a board looks at it thirty times in a minute. A write does not read again: it
/// changes the kept board the way GitHub was just told to, so the item shows what was clicked
/// rather than what a read replica has not heard of yet.
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
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(30);

    public const string AuditField = "planner.field";
    public const string AuditClose = "planner.close";
    public const string AuditDay = "planner.day";
    public const string AuditAssign = "planner.assign";
    public const string AuditRefused = "planner.refused";

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim gate = new(1, 1);

    private PlannerSnapshot? kept;
    private string? botLogin;

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
        ActAsync(itemId, actor, ct, async (client, _, entry) =>
        {
            var item = entry.Item;
            var word = DayWord(day);

            await AuditAsync(AuditDay, actor, $"{item.Reference}: {word}", item, new { day = word, was = DayWord(entry.Day) }, ct).ConfigureAwait(false);

            var labels = item.Labels.ToList();

            foreach (var label in new[] { PlannerQueue.TodayLabel, PlannerQueue.NotTodayLabel })
            {
                if (labels.FirstOrDefault(l => string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase)) is not { } present)
                {
                    continue;
                }

                var removed = await client.RemoveLabelAsync(item.Repository, item.Number, present.Name, ct).ConfigureAwait(false);

                // Not found: somebody took it off on GitHub since the board was read. That is what was wanted.
                if (!removed.Ok && removed.Outcome != GitHubOutcome.NotFound)
                {
                    return (PlannerActionOutcome.Refused, removed.Describe(), item with { Labels = labels });
                }

                labels.Remove(present);
            }

            if (day != PlannerDay.Undecided)
            {
                var name = day == PlannerDay.Today ? PlannerQueue.TodayLabel : PlannerQueue.NotTodayLabel;
                var added = await client.AddLabelAsync(item.Repository, item.Number, name, ct).ConfigureAwait(false);

                if (!added.Ok)
                {
                    return (PlannerActionOutcome.Refused, added.Describe(), item with { Labels = labels });
                }

                labels.Add(new BoardLabel(name, clock.UtcNow));
            }

            return (PlannerActionOutcome.Done, word, item with { Labels = labels });
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

    public static string DayWord(PlannerDay day) => day switch
    {
        PlannerDay.Today => "today",
        PlannerDay.NotToday => "not today",
        _ => "not decided",
    };

    /// <summary>
    /// One click: who, on what, and then <paramref name="act"/>, which answers with the outcome,
    /// a sentence, and the item as it is now when something of it changed - also after a refusal
    /// halfway, when a label came off and the next one did not go on.
    /// </summary>
    private async Task<PlannerActionResult> ActAsync(
        string itemId,
        string actor,
        CancellationToken ct,
        Func<IGitHubClient, Board, PlannerEntry, Task<(PlannerActionOutcome Outcome, string Message, BoardItem? Changed)>> act)
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
            var snapshot = await ReadLockedAsync(refresh: false, ct).ConfigureAwait(false);

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

            if (changed is not null)
            {
                snapshot = kept = Present(
                    board with { Items = [.. board.Items.Select(i => i.ItemId == changed.ItemId ? changed : i)] },
                    snapshot.ReadAt ?? clock.UtcNow,
                    snapshot.Problem);
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

    private async Task<PlannerSnapshot> ReadLockedAsync(bool refresh, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (!refresh && kept is { ReadAt: { } at, Problem: null } && now - at < KeepFor)
        {
            return kept;
        }

        var planner = options.CurrentValue.Planner;

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return PlannerSnapshot.Off;
        }

        botLogin ??= await BotLoginAsync(client, ct).ConfigureAwait(false);

        var read = await client.GetProjectAsync(planner.Owner.Trim(), planner.Number, ct).ConfigureAwait(false);

        if (!read.Ok)
        {
            var problem = read.Outcome == GitHubOutcome.NotFound
                ? $"GitHub knows no project {planner.Number} of the organisation {planner.Owner}, or the token may not see it ({read.Detail})"
                : read.Describe();

            logger.LogWarning("The planner could not read {Owner}/projects/{Number}: {Problem}", planner.Owner, planner.Number, problem);

            // The last good board stays, and says that it is old.
            return kept = kept is { Board: not null }
                ? kept with { Problem = problem }
                : new PlannerSnapshot(PlannerState.Unreadable, problem, null, null, [], [], [], [], botLogin);
        }

        return kept = Present(read.Value!, now, problem: null);
    }

    private PlannerSnapshot Present(Board board, DateTimeOffset readAt, string? problem)
    {
        var planner = options.CurrentValue.Planner;
        var now = clock.UtcNow;
        var entries = PlannerQueue.Build(board, new PlannerRules(planner.RequiredFields, TimeSpan.FromDays(planner.StaleAfterDays)), now);

        var assignees = new[] { botLogin }
            .Concat(planner.Assignees)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlannerSnapshot(
            PlannerState.Read, problem, readAt, board, entries, PlannerQueue.Group(board, entries), PlannerQueue.ClosedToday(board, now), assignees, botLogin);
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

    private async Task AuditAsync(string type, string actor, string summary, BoardItem item, object detail, CancellationToken ct)
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
                    new { repository = item.Repository, number = item.Number, url = item.Url, item = item.ItemId, change = detail }, AuditJson),
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
            },
            ct).ConfigureAwait(false);
    }
}
