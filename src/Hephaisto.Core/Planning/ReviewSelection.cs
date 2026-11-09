namespace Hephaisto.Core.Planning;

/// <summary>Why the weekly review asks about an issue again, or that it does not (#312).</summary>
public enum ReviewWhy
{
    /// <summary>Its verdict stands: the issue did not change, and the verdict is not old.</summary>
    Kept = 0,

    /// <summary>It has no verdict - or one from before it was closed and opened again.</summary>
    New = 1,

    /// <summary>GitHub's <c>updated_at</c> is not the one its verdict was given for - in practice it moved on.</summary>
    Changed = 2,

    /// <summary>Unchanged, and its verdict is older than the recheck period.</summary>
    Recheck = 3,
}

/// <summary>An open issue as the run found it.</summary>
/// <param name="Reference"><c>owner/repo#n</c>.</param>
public sealed record ReviewOpen(string Reference, string Repository, int Number, DateTimeOffset UpdatedAt);

/// <summary>A verdict as it is kept.</summary>
/// <param name="IssueUpdatedAt">The issue's <c>updated_at</c> when the verdict was given.</param>
/// <param name="Gone">The issue was found closed, or no longer open, after the verdict.</param>
public sealed record ReviewKnown(string Reference, DateTimeOffset IssueUpdatedAt, DateTimeOffset ReviewedAt, bool Gone);

/// <summary>
/// Which open issues a run asks about (#312). Pure: the open issues, the kept verdicts, the time.
/// </summary>
/// <remarks>
/// Incremental, so a weekly run over a few hundred issues costs what changed: an issue is asked
/// about when it has no verdict, when GitHub says it changed since its verdict, or when the
/// verdict is older than <c>recheck</c> - a merged pull request or an archived repository does
/// not always touch the issue it finishes.
/// </remarks>
public static class ReviewSelection
{
    public static ReviewWhy Why(ReviewOpen issue, ReviewKnown? known, DateTimeOffset now, TimeSpan recheck)
    {
        ArgumentNullException.ThrowIfNull(issue);

        if (known is null || known.Gone)
        {
            return ReviewWhy.New;
        }

        // GitHub's times are whole seconds, and the stored one is GitHub's own. Different, not
        // later: on github.com it only moves on, but an issue that was deleted and made again, or
        // a stand-in that was seeded afresh, is not the issue the verdict was about either.
        if (IsChanged(issue.UpdatedAt, known.IssueUpdatedAt))
        {
            return ReviewWhy.Changed;
        }

        return now - known.ReviewedAt >= recheck ? ReviewWhy.Recheck : ReviewWhy.Kept;
    }

    /// <summary>
    /// Every open issue with why, the ones to ask about first in the order they are batched: by
    /// repository, so a batch clones few, then by number.
    /// </summary>
    public static IReadOnlyList<(ReviewOpen Issue, ReviewWhy Why)> Select(
        IEnumerable<ReviewOpen> open, IEnumerable<ReviewKnown> known, DateTimeOffset now, TimeSpan recheck)
    {
        var byReference = known
            .GroupBy(k => k.Reference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(k => k.ReviewedAt).First(), StringComparer.OrdinalIgnoreCase);

        return [.. open
            .DistinctBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(i => (Issue: i, Why: Why(i, byReference.GetValueOrDefault(i.Reference), now, recheck)))
            .OrderBy(x => x.Why == ReviewWhy.Kept)
            .ThenBy(x => x.Issue.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Issue.Number)];
    }

    /// <summary>The issues to ask about, in batches of at most <paramref name="size"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<ReviewOpen>> Batches(IEnumerable<(ReviewOpen Issue, ReviewWhy Why)> selected, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        return [.. selected.Where(s => s.Why != ReviewWhy.Kept).Select(s => s.Issue).Chunk(size).Select(c => (IReadOnlyList<ReviewOpen>)c)];
    }

    /// <summary>Whether an issue's <c>updated_at</c> is not the one a verdict was given for, to the second.</summary>
    public static bool IsChanged(DateTimeOffset now, DateTimeOffset given) => now.ToUnixTimeSeconds() != given.ToUnixTimeSeconds();
}

/// <summary>
/// What one run may still spend (#312): the cap, what finished Jobs cost, and what running Jobs
/// were allowed. A Job starts with a share of what is left and none starts when too little is.
/// </summary>
/// <remarks>Not thread-safe; the runner calls it from one loop.</remarks>
public sealed class ReviewBudget(decimal cap, int concurrency)
{
    /// <summary>Less than this left is no budget for a Job: a model run that is cut off at once answers nothing.</summary>
    public const decimal MinimumPerJob = 0.05m;

    public decimal Cap { get; } = cap;

    public decimal Spent { get; private set; }

    public decimal Reserved { get; private set; }

    public decimal Available => Cap - Spent - Reserved;

    /// <summary>A Job's budget, reserved, or null when what is left is too little to start one.</summary>
    public decimal? Reserve()
    {
        var share = Math.Min(Available, Math.Round(Cap / Math.Max(1, concurrency), 2, MidpointRounding.ToZero));

        if (share < MinimumPerJob)
        {
            return null;
        }

        Reserved += share;
        return share;
    }

    /// <summary>A Job ended: its reservation is let go and what it cost is spent - more than it was allowed, if the SDK went over.</summary>
    public void Settle(decimal reserved, decimal cost)
    {
        Reserved = Math.Max(0, Reserved - reserved);
        Spent += Math.Max(0, cost);
    }
}
