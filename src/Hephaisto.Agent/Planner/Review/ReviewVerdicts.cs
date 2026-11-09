using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>
/// The weekly review's rules about verdicts (#312), pure: what a Job's answer becomes, how a new
/// verdict replaces a kept one, which tab one is shown on. Both stores call these, so the rules
/// hold the same in Postgres and in a test.
/// </summary>
public static partial class ReviewVerdicts
{
    public const string Close = "close";
    public const string Bug = "bug";
    public const string Feature = "feature";

    /// <summary>The runner's own group, for an issue the model left out or closed without a reason. Never the model's.</summary>
    public const string Unsure = "unsure";

    public static readonly IReadOnlySet<string> Groups = new HashSet<string>(StringComparer.Ordinal) { Close, Bug, Feature, Unsure };

    public static readonly IReadOnlySet<string> CloseReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "done", "superseded", "obsolete", "duplicate", "answered", "stale",
    };

    public static readonly IReadOnlySet<string> Confidences = new HashSet<string>(StringComparer.Ordinal) { "high", "medium", "low" };

    public static readonly IReadOnlySet<string> Priorities = new HashSet<string>(StringComparer.Ordinal) { "P0", "P1", "P2" };

    public const string MissingSummary = "Claude gave no verdict for this issue.";

    /// <summary>
    /// Where a verdict is shown. A person's decision decides; otherwise a low confidence is a
    /// person's to decide, whatever the group - and the runner's unsure is always low.
    /// </summary>
    public static string Tab(PlannerReviewVerdict v)
    {
        ArgumentNullException.ThrowIfNull(v);

        if (v.DecidedBy is not null)
        {
            return v.DecisionUnsure ? Unsure : v.DecisionGroup is { } g && Groups.Contains(g) ? g : v.Group;
        }

        return v.Confidence == "low" || v.Group == Unsure ? Unsure : v.Group;
    }

    /// <summary>
    /// The verdicts a Job's answer gives its batch: exactly one per issue of the batch, in its
    /// order. The coder keeps the batch already (<c>keepBatch</c>); this holds the same rule
    /// against any answer - one for an issue not in the batch is dropped, the first of two counts,
    /// one that is missing or breaks the contract's rules is unsure with low confidence.
    /// </summary>
    public static IReadOnlyList<PlannerReviewVerdict> FromResult(
        IReadOnlyList<ReviewIssue> batch, IReadOnlyList<ReviewVerdictResult> answered, IReadOnlyDictionary<string, ReviewIssueContext> context, Guid runId, DateTimeOffset now)
    {
        var byIssue = new Dictionary<string, ReviewVerdictResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in answered)
        {
            byIssue.TryAdd(v.Issue ?? string.Empty, v);
        }

        return [.. batch.Select(issue =>
        {
            var reference = $"{issue.Repository}#{issue.Number}";
            var ctx = context[reference];
            var v = byIssue.GetValueOrDefault(reference) is { } given && Sound(given) ? given : null;

            var row = new PlannerReviewVerdict
            {
                Repository = issue.Repository,
                Number = issue.Number,
                Title = Cap(issue.Title, 512),
                Url = Cap(issue.Url, 512),
                NodeId = Cap(ctx.NodeId, 128),
                OnBoard = issue.Board is not null,
                Archived = issue.Archived,
                IssueUpdatedAt = ctx.UpdatedAt,
                ReviewedAt = now,
                RunId = runId,
            };

            if (v is null)
            {
                row.Group = Unsure;
                row.Confidence = "low";
                row.Summary = MissingSummary;
                row.Reason = byIssue.ContainsKey(reference)
                    ? "The model's verdict for this issue broke the review's rules, so a person decides."
                    : "The model's answer did not name this issue, so a person decides.";
                return row;
            }

            row.Group = v.Group;
            row.CloseReason = v.Group == Close ? v.CloseReason : null;
            row.Related = v.Related is { } related && Reference().IsMatch(related) ? Cap(related, 220) : null;
            row.Confidence = v.Group == Unsure ? "low" : v.Confidence;
            row.Summary = Cap(v.Summary ?? string.Empty, 300);
            row.Reason = Cap(v.Reason ?? string.Empty, 1000);
            row.Priority = v.Group is Bug or Feature && v.Priority is { } p && Priorities.Contains(p) ? p : null;
            row.Evidence = [.. (v.Evidence ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Take(8).Select(e => Classify(Cap(e.Trim(), 200), issue))];
            return row;
        })];
    }

    /// <summary>A verdict the contract allows: a known group and confidence, and a close with its reason.</summary>
    private static bool Sound(ReviewVerdictResult v) =>
        Groups.Contains(v.Group ?? string.Empty)
        && Confidences.Contains(v.Confidence ?? string.Empty)
        && (v.Group != Close || CloseReasons.Contains(v.CloseReason ?? string.Empty));

    /// <summary>
    /// One fact a verdict names, and whether what Hephaisto gathered bears it out: a pull request
    /// said to be merged that the timeline shows merged is <c>ok</c>; one the timeline shows open
    /// is not; a fact nothing gathered speaks of is neither.
    /// </summary>
    public static PlannerReviewEvidence Classify(string text, ReviewIssue facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (PullEvidence().Match(text) is { Success: true } pull)
        {
            var said = pull.Groups["state"].Value.ToLowerInvariant();
            var found = facts.CrossReferences.FirstOrDefault(c => c.Kind == "pull_request" && string.Equals(c.Reference, pull.Groups["ref"].Value, StringComparison.OrdinalIgnoreCase));

            return new("pull_request", text, found is null ? null : found.State == said);
        }

        if (SubIssueEvidence().Match(text) is { Success: true } sub)
        {
            var found = facts.SubIssues.FirstOrDefault(s => string.Equals(s.Reference, sub.Groups["ref"].Value, StringComparison.OrdinalIgnoreCase));

            return new("sub_issue", text, found is null ? null : found.State == sub.Groups["state"].Value.ToLowerInvariant());
        }

        if (CommitEvidence().Match(text) is { Success: true } commit)
        {
            var sha = commit.Groups["sha"].Value.ToLowerInvariant();

            return new("commit", text, facts.Commits.Any(c => c.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase)) ? true : null);
        }

        if (text.Contains("archived", StringComparison.OrdinalIgnoreCase))
        {
            return new("repository", text, facts.Archived);
        }

        if (AbsenceEvidence().Match(text) is { Success: true } absent)
        {
            var what = absent.Groups["what"].Value.ToLowerInvariant();
            bool? ok = what switch
            {
                "pull request" or "pr" => !facts.CrossReferences.Any(c => c.Kind == "pull_request"),
                "commit" => facts.Commits.Count == 0,
                _ => null,
            };

            return new("absence", text, ok);
        }

        if (IssueEvidence().Match(text) is { Success: true } other)
        {
            var found = facts.CrossReferences.FirstOrDefault(c => string.Equals(c.Reference, other.Groups["ref"].Value, StringComparison.OrdinalIgnoreCase))
                ?? (facts.SubIssues.FirstOrDefault(s => string.Equals(s.Reference, other.Groups["ref"].Value, StringComparison.OrdinalIgnoreCase)) is { } s
                    ? new ReviewCrossReference("issue", s.Reference, s.Title, s.State, null, null)
                    : null);

            return new("issue", text, found is null ? null : true);
        }

        return new("note", text, null);
    }

    /// <summary>
    /// A new verdict replaces a kept one. The person's decision stays unless the issue changed
    /// since the verdict it was made on - or was closed and opened again: then it is cleared, and
    /// the audit row of the click is its history.
    /// </summary>
    public static void Merge(PlannerReviewVerdict kept, PlannerReviewVerdict fresh)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(fresh);

        var changed = ReviewSelection.IsChanged(fresh.IssueUpdatedAt, kept.IssueUpdatedAt) || kept.GoneAt is not null;

        kept.Title = fresh.Title;
        kept.Url = fresh.Url;
        kept.NodeId = fresh.NodeId;
        kept.OnBoard = fresh.OnBoard;
        kept.Archived = fresh.Archived;
        kept.Group = fresh.Group;
        kept.CloseReason = fresh.CloseReason;
        kept.Related = fresh.Related;
        kept.Confidence = fresh.Confidence;
        kept.Summary = fresh.Summary;
        kept.Reason = fresh.Reason;
        kept.Evidence = fresh.Evidence;
        kept.Priority = fresh.Priority;
        kept.IssueUpdatedAt = fresh.IssueUpdatedAt;
        kept.ReviewedAt = fresh.ReviewedAt;
        kept.RunId = fresh.RunId;
        kept.GoneAt = null;

        if (changed)
        {
            Decide(kept, null);
        }
    }

    /// <summary>A person's decision, or none (null): what <see cref="IReviewStore.DecideAsync"/> writes.</summary>
    public static void Decide(PlannerReviewVerdict row, ReviewDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.DecidedBy = decision?.By;
        row.DecidedAt = decision?.At;
        row.DecisionGroup = decision?.Group;
        row.DecisionCloseReason = decision?.CloseReason;
        row.DecisionAgreed = decision?.Agreed ?? false;
        row.DecisionUnsure = decision?.Unsure ?? false;
    }

    /// <summary>What a run found, onto the kept verdicts that are not gone.</summary>
    public static void Sync(IEnumerable<PlannerReviewVerdict> rows, IReadOnlyCollection<ReviewOpenFacts> open, bool complete, DateTimeOffset now)
    {
        var byReference = open
            .GroupBy(o => $"{o.Repository}#{o.Number}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Where(r => r.GoneAt is null))
        {
            if (byReference.TryGetValue(row.Reference, out var facts))
            {
                row.Title = Cap(facts.Title, 512);
                row.Url = Cap(facts.Url, 512);
                row.OnBoard = facts.OnBoard;
                row.Archived = facts.Archived ?? row.Archived;
            }
            else if (complete)
            {
                row.GoneAt = now;
            }
        }
    }

    public static void Interrupted(PlannerReviewRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        run.State = PlannerReviewRunState.Failed;
        run.FinishedAt = now;
        run.Problem = Cap(
            "The agent stopped during this run. What its finished batches answered is kept; the Jobs still running end by their deadline and nothing they answer is read."
                + (run.Problem is { Length: > 0 } before ? " " + before : string.Empty),
            HephaistoDbContext.PlannerReviewRunMaxProblem);
    }

    public static string Cap(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];

    private const string Ref = @"(?<ref>[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+#[1-9][0-9]*)";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/(?!\.\.?#)[A-Za-z0-9._-]+#[1-9][0-9]*$")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\b(?:PR|pull request)\s+" + Ref + @"\s+(?<state>merged|open|closed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PullEvidence();

    [GeneratedRegex(@"\bsub-issues?\s+" + Ref + @"\s+(?<state>open|closed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubIssueEvidence();

    [GeneratedRegex(@"\bcommit\s+(?<sha>[0-9a-f]{7,64})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommitEvidence();

    [GeneratedRegex(@"^\s*no\s+(?<what>pull request|pr|commit)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbsenceEvidence();

    [GeneratedRegex(Ref)]
    private static partial Regex IssueEvidence();
}

/// <summary>What the runner knows of a batch issue that the request does not carry.</summary>
public sealed record ReviewIssueContext(string NodeId, DateTimeOffset UpdatedAt);
