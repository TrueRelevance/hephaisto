using System.Text;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Web;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.Planner.Review;

public enum ReviewActionOutcome
{
    Done = 0,

    /// <summary>The issue has no verdict in the review, or is closed already.</summary>
    NotFound = 1,

    /// <summary>GitHub said no. The decision was not recorded.</summary>
    Refused = 2,

    /// <summary>The planner is off.</summary>
    Off = 3,

    /// <summary>The actor is one that may not decide: a model's name, or the system's.</summary>
    Forbidden = 4,

    /// <summary>The click does not fit the verdict or the moment: nothing to agree with, a run under way, nothing agreed.</summary>
    Conflict = 5,
}

public sealed record ReviewActionResult(ReviewActionOutcome Outcome, string Message)
{
    public bool Done => Outcome == ReviewActionOutcome.Done;
}

/// <param name="Closed">The issues closed, <c>owner/repo#n</c>.</param>
/// <param name="Refused">The ones GitHub refused, each with why.</param>
/// <param name="Archived">The agreed ones in an archived repository: nothing was tried on them.</param>
public sealed record ReviewCloseResult(
    ReviewActionOutcome Outcome, string Message, IReadOnlyList<string> Closed, IReadOnlyList<string> Refused, IReadOnlyList<string> Archived)
{
    public bool Done => Outcome == ReviewActionOutcome.Done;
}

/// <summary>
/// A person's clicks on the weekly review (#312): agree with a verdict, move it to another group,
/// mark it unsure, and close what was agreed. The console's page and the API both come here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every GitHub write is the planner's</b> (<see cref="PlannerService"/>): agreeing with a bug
/// on the board sets its Priority through <see cref="PlannerService.SetFieldAsync"/>, closing
/// goes through <see cref="PlannerService.CloseReviewedAsync"/>. Nothing in this folder calls a
/// write of the client - <c>ReviewRunnerTests</c> holds that.
/// </para>
/// <para>
/// <b>A decision is what the click recorded</b>, on the verdict's row, and its history is the
/// audit row each click writes (<see cref="PlannerService.AuditReview"/>): a new verdict for an
/// issue that changed clears the decision, the trail keeps it.
/// </para>
/// <para>
/// <b>An issue in an archived repository can be agreed with and is never closed</b>: GitHub
/// refuses every change there, so no write is even tried until the repository is unarchived.
/// </para>
/// </remarks>
public sealed class ReviewActions(PlannerService planner, IReviewStore store, IClock clock, ILogger<ReviewActions> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>A click changed a verdict's decision or closed issues: for the navigation's badge.</summary>
    public event Action? Changed;

    /// <summary>Agree with the verdict as it stands. On a bug on the board, its suggested Priority is set there.</summary>
    public Task<ReviewActionResult> AgreeAsync(string repository, int number, string actor, CancellationToken ct, AuditOrigin? origin = null) =>
        DecideAsync(repository, number, actor, origin, ct, async (row, who) =>
        {
            if (!ReviewQueue.Groups.Contains(row.Group))
            {
                return (ReviewActionOutcome.Conflict, "Claude gave no verdict to agree with: move it to the group it belongs in", null);
            }

            var said = $"agreed: {ReviewQueue.GroupWord(row.Group)}";
            var decision = new ReviewDecision(who, clock.UtcNow, row.Group, row.CloseReason, Agreed: true, Unsure: false);

            await RecordAsync(who, row, said, decision, ct, origin).ConfigureAwait(false);

            if (row.Group == ReviewVerdicts.Bug && row.Priority is { } priority && row.OnBoard)
            {
                if ((await planner.ReadAsync(refresh: false, ct).ConfigureAwait(false)).Entries.FirstOrDefault(e => Same(e.Item, row)) is not { } entry)
                {
                    return (ReviewActionOutcome.Done, $"{said}; it is not among the board's open issues now, so no Priority was set", decision);
                }

                if (string.Equals(entry.Item.Field(PlannerQueue.PriorityField), priority, StringComparison.OrdinalIgnoreCase))
                {
                    return (ReviewActionOutcome.Done, $"{said}; Priority is {priority} already", decision);
                }

                var set = await planner.SetFieldAsync(entry.Item.ItemId, PlannerQueue.PriorityField, priority, who, ct, origin).ConfigureAwait(false);

                return set.Done
                    ? (ReviewActionOutcome.Done, $"{said}; Priority set to {priority}", decision)
                    : (set.Outcome == PlannerActionOutcome.Refused ? ReviewActionOutcome.Refused : ReviewActionOutcome.Conflict, $"Priority {priority} was not set: {set.Message}", null);
            }

            return (ReviewActionOutcome.Done, said, decision);
        });

    /// <summary>Put the issue in another group: the person's word, not Claude's. A close needs a reason.</summary>
    public Task<ReviewActionResult> MoveAsync(string repository, int number, string? group, string? closeReason, string actor, CancellationToken ct, AuditOrigin? origin = null) =>
        DecideAsync(repository, number, actor, origin, ct, async (row, who) =>
        {
            var to = group?.Trim().ToLowerInvariant() ?? string.Empty;
            var reason = closeReason?.Trim().ToLowerInvariant();

            if (!ReviewQueue.Groups.Contains(to))
            {
                return (ReviewActionOutcome.Conflict, "group is close, bug or feature", null);
            }

            if (to == ReviewVerdicts.Close && (reason is null || !ReviewVerdicts.CloseReasons.Contains(reason)))
            {
                return (ReviewActionOutcome.Conflict, $"moving to close needs a reason: {string.Join(", ", ReviewQueue.CloseReasonOrder)}", null);
            }

            var decision = new ReviewDecision(who, clock.UtcNow, to, to == ReviewVerdicts.Close ? reason : null, Agreed: false, Unsure: false);
            var said = to == ReviewVerdicts.Close
                ? $"moved to can be closed, as {ReviewQueue.CloseReasonWord(reason)}"
                : $"moved to {ReviewQueue.GroupWord(to)}";

            await RecordAsync(who, row, said, decision, ct, origin).ConfigureAwait(false);

            return (ReviewActionOutcome.Done, said, decision);
        });

    /// <summary>The person cannot tell either: the issue is shown under unsure, and nothing closes it.</summary>
    public Task<ReviewActionResult> UnsureAsync(string repository, int number, string actor, CancellationToken ct, AuditOrigin? origin = null) =>
        DecideAsync(repository, number, actor, origin, ct, async (row, who) =>
        {
            var decision = new ReviewDecision(who, clock.UtcNow, row.Group, row.CloseReason, Agreed: false, Unsure: true);

            await RecordAsync(who, row, "marked unsure", decision, ct, origin).ConfigureAwait(false);

            return (ReviewActionOutcome.Done, "marked unsure", decision);
        });

    /// <summary>
    /// Closes every issue a person decided to close (<see cref="ReviewQueue.Closable"/>), each with
    /// one comment that carries its reason, and leaves the ones in an archived repository alone.
    /// Refused while a run is under way: its batches would write a verdict for an issue just closed.
    /// </summary>
    public async Task<ReviewCloseResult> CloseAgreedAsync(string actor, CancellationToken ct, AuditOrigin? origin = null)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (Refusal(who) is { } refused)
        {
            return new(refused.Outcome, refused.Message, [], [], []);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (await store.LatestRunAsync(ct).ConfigureAwait(false) is { State: PlannerReviewRunState.Running })
            {
                return new(ReviewActionOutcome.Conflict, "a review run is under way: close what was agreed when it has finished", [], [], []);
            }

            var rows = (await store.VerdictsAsync(includeGone: false, ct).ConfigureAwait(false))
                .Where(r => ReviewQueue.DecidedToClose(r.DecidedBy is not null, r.DecisionGroup, r.DecisionUnsure))
                .OrderBy(r => r.Repository, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Number)
                .ToList();

            var archived = rows.Where(r => r.Archived).Select(r => r.Reference).ToList();
            var closable = rows.Where(r => !r.Archived).ToList();

            if (closable.Count == 0)
            {
                return new(ReviewActionOutcome.Conflict,
                    archived.Count > 0 ? $"nothing to close: {ArchivedWords(archived.Count)}" : "nothing to close: no issue is agreed to be closed yet",
                    [], [], archived);
            }

            var closed = new List<string>();
            var failed = new List<string>();

            foreach (var row in closable)
            {
                var reason = row.DecisionCloseReason ?? row.CloseReason ?? "done";
                var (outcome, message) = await planner.CloseReviewedAsync(
                    row.Repository, row.Number, row.NodeId, ReviewQueue.CloseReasonWord(reason), ReviewComment.Render(row), who, ct, origin).ConfigureAwait(false);

                if (outcome == PlannerActionOutcome.Done)
                {
                    await store.MarkGoneAsync(row.Repository, row.Number, clock.UtcNow, ct).ConfigureAwait(false);
                    closed.Add(row.Reference);
                }
                else
                {
                    logger.LogWarning("The review's close of {Issue} by {Actor} was refused: {Detail}", row.Reference, who, message);
                    failed.Add($"{row.Reference} ({message})");

                    if (outcome is PlannerActionOutcome.Off or PlannerActionOutcome.Forbidden)
                    {
                        break;
                    }
                }
            }

            var text = new StringBuilder($"Closed {closed.Count} {(closed.Count == 1 ? "issue" : "issues")}");

            if (closed.Count > 0)
            {
                text.Append(": ").Append(string.Join(", ", closed));
            }

            if (failed.Count > 0)
            {
                text.Append(". Not closed: ").Append(string.Join("; ", failed));
            }

            if (archived.Count > 0)
            {
                text.Append(". ").Append(char.ToUpperInvariant(ArchivedWords(archived.Count)[0])).Append(ArchivedWords(archived.Count)[1..]);
            }

            if (closed.Count > 0)
            {
                Changed?.Invoke();
            }

            return new(failed.Count == 0 ? ReviewActionOutcome.Done : ReviewActionOutcome.Refused, text.ToString(), closed, failed, archived);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ArchivedWords(int count) =>
        count == 1
            ? "1 agreed issue is in an archived repository and stays open until it is unarchived"
            : $"{count} agreed issues are in archived repositories and stay open until they are unarchived";

    private async Task<ReviewActionResult> DecideAsync(
        string repository,
        int number,
        string actor,
        AuditOrigin? origin,
        CancellationToken ct,
        Func<PlannerReviewVerdict, string, Task<(ReviewActionOutcome Outcome, string Message, ReviewDecision? Decision)>> act)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        var reference = $"{repository}#{number}";

        if (Refusal(who) is { } refused)
        {
            return new(refused.Outcome, $"{reference}: {refused.Message}");
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var row = (await store.VerdictsAsync(includeGone: false, ct).ConfigureAwait(false))
                .FirstOrDefault(v => string.Equals(v.Repository, repository?.Trim(), StringComparison.OrdinalIgnoreCase) && v.Number == number);

            if (row is null)
            {
                return new(ReviewActionOutcome.NotFound, $"{reference} has no verdict in the review, or it is closed");
            }

            var (outcome, message, decision) = await act(row, who).ConfigureAwait(false);

            if (decision is not null && !await store.DecideAsync(row.Repository, row.Number, decision, ct).ConfigureAwait(false))
            {
                return new(ReviewActionOutcome.NotFound, $"{row.Reference} was closed meanwhile");
            }

            if (decision is not null)
            {
                Changed?.Invoke();
            }

            return new(outcome, $"{row.Reference}: {message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private ReviewActionResult? Refusal(string who) =>
        !planner.Enabled ? new(ReviewActionOutcome.Off, "the planner is off")
        : IncidentStateMachine.IsForbiddenGranter(who) ? new(ReviewActionOutcome.Forbidden, $"'{who}' may not decide here: the review's verdicts are a person's to agree with")
        : null;

    /// <summary>The click's audit row, before anything is written: no audit, no action.</summary>
    private Task RecordAsync(string who, PlannerReviewVerdict row, string said, ReviewDecision decision, CancellationToken ct, AuditOrigin? origin) =>
        planner.RecordAsync(
            PlannerService.AuditReview,
            who,
            $"{row.Reference}: {said}",
            row.Repository,
            row.Number,
            row.Url,
            new
            {
                verdict = new { group = row.Group, closeReason = row.CloseReason, confidence = row.Confidence, priority = row.Priority, run = row.RunId },
                decision = new { group = decision.Group, closeReason = decision.CloseReason, agreed = decision.Agreed, unsure = decision.Unsure },
                was = row.DecidedBy is null ? null : new { by = row.DecidedBy, group = row.DecisionGroup, agreed = row.DecisionAgreed, unsure = row.DecisionUnsure },
            },
            ct,
            origin);

    private static bool Same(BoardItem item, PlannerReviewVerdict row) =>
        string.Equals(item.Repository, row.Repository, StringComparison.OrdinalIgnoreCase) && item.Number == row.Number;
}

/// <summary>
/// The comment an issue is closed with after the review (#312), without its signature - the
/// planner adds the person's name. Claude's words in it go through <see cref="IssueComments"/>'
/// neutralisers, as an analysis does: no mention, no link, no reference on the agent's account.
/// </summary>
public static class ReviewComment
{
    public static string Render(PlannerReviewVerdict row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var reason = row.DecisionCloseReason ?? row.CloseReason ?? "done";
        var text = new StringBuilder();

        text.Append("Closed after Hephaisto's weekly review: **").Append(ReviewQueue.CloseReasonWord(reason)).Append("**.\n\n");

        if (row.DecisionAgreed && row.Group == ReviewVerdicts.Close)
        {
            text.Append("Claude's reason, agreed with: ").Append(IssueComments.Neutralise(row.Reason, 1000)).Append("\n\n");

            if (row.Evidence.Count > 0)
            {
                foreach (var e in row.Evidence.Take(8))
                {
                    text.Append("- ").Append(IssueComments.Neutralise(e.Text, 200)).Append('\n');
                }

                text.Append('\n');
            }
        }
        else
        {
            text.Append("A person chose the reason: Claude's verdict had it as ")
                .Append(ReviewQueue.GroupWord(row.Group))
                .Append(".\n\n");
        }

        return text.ToString();
    }
}
