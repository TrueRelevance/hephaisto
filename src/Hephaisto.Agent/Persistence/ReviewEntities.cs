namespace Hephaisto.Agent.Persistence;

public enum PlannerReviewTrigger
{
    Manual = 0,
    Schedule = 1,
}

public enum PlannerReviewRunState
{
    Running = 0,

    /// <summary>Every batch was tried. <see cref="PlannerReviewRun.Problem"/> says which did not answer.</summary>
    Finished = 1,

    /// <summary>Nothing was reviewed: the facts could not be gathered, every batch failed, or the agent stopped mid-run.</summary>
    Failed = 2,
}

/// <summary>
/// One weekly review (#312) - started by the schedule or by a person - and what it did. A row, so
/// that a restart knows which slot already ran and the console can show the last run all week.
/// </summary>
/// <remarks>
/// No foreign key from a verdict: a verdict names the run that gave it, and outlives it if old
/// runs are ever trimmed.
/// </remarks>
public sealed class PlannerReviewRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public PlannerReviewTrigger Trigger { get; set; }

    public PlannerReviewRunState State { get; set; }

    /// <summary>The schedule's moment this run is for; null for a person's. Unique: a slot runs once, restart or not.</summary>
    public DateTimeOffset? Slot { get; set; }

    /// <summary>Who asked: a person's name, or <c>schedule</c>.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>What went wrong, in sentences; also beside a run that finished, for the batches that did not answer.</summary>
    public string? Problem { get; set; }

    /// <summary>How many issues of the organisation were open.</summary>
    public int Open { get; set; }

    /// <summary>The issues this run gave a verdict, <c>owner/repo#n</c>.</summary>
    public List<string> Reviewed { get; set; } = [];

    /// <summary>Open issues that kept the verdict an earlier run gave them.</summary>
    public int Kept { get; set; }

    public int Jobs { get; set; }

    public decimal CostUsd { get; set; }

    // Where the review's open issues were when the run ended, by the tab each is shown on.
    public int CountClose { get; set; }

    public int CountBug { get; set; }

    public int CountFeature { get; set; }

    public int CountUnsure { get; set; }
}

/// <summary>One fact a verdict names, and whether what Hephaisto gathered bears it out.</summary>
/// <param name="Kind"><c>pull_request</c>, <c>issue</c>, <c>commit</c>, <c>sub_issue</c>, <c>repository</c>, <c>absence</c> or <c>note</c>.</param>
/// <param name="Ok">True when the facts say so, false when they say otherwise, null when they say nothing.</param>
public sealed record PlannerReviewEvidence(string Kind, string Text, bool? Ok);

/// <summary>
/// The current verdict on one open issue of the organisation (#312), and what a person decided
/// about it. One row per issue: a later verdict replaces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A person's decision sticks</b> across runs - also across a recheck that replaces the
/// verdict - <b>unless the issue changed</b> (its <c>updated_at</c> is not the one the verdict was given for): then the new verdict
/// is shown undecided and the old decision is cleared. Its history is the audit row the click
/// wrote, not this row.
/// </para>
/// <para>
/// <b>A closed issue drops out</b>: <see cref="GoneAt"/> is set when a complete listing of the
/// organisation no longer holds it, or when a person closed it from the review. Opened again, it
/// is reviewed again.
/// </para>
/// </remarks>
public sealed class PlannerReviewVerdict
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Repository { get; set; } = string.Empty;

    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>The issue's node id, for a close.</summary>
    public string NodeId { get; set; } = string.Empty;

    public bool OnBoard { get; set; }

    public bool Archived { get; set; }

    /// <summary><c>close</c>, <c>bug</c>, <c>feature</c>, or <c>unsure</c> - the runner's own, for an issue the model left out.</summary>
    public string Group { get; set; } = string.Empty;

    public string? CloseReason { get; set; }

    public string? Related { get; set; }

    public string Confidence { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public List<PlannerReviewEvidence> Evidence { get; set; } = [];

    public string? Priority { get; set; }

    /// <summary>The issue's <c>updated_at</c> the verdict was given for: what "changed" is measured against.</summary>
    public DateTimeOffset IssueUpdatedAt { get; set; }

    public DateTimeOffset ReviewedAt { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset? GoneAt { get; set; }

    // A person's decision (stage 4 writes it). Null DecidedBy: nobody decided.
    public string? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The group the person put it in, the verdict's own when they agreed.</summary>
    public string? DecisionGroup { get; set; }

    public string? DecisionCloseReason { get; set; }

    public bool DecisionAgreed { get; set; }

    public bool DecisionUnsure { get; set; }

    public string Reference => $"{Repository}#{Number}";
}
