namespace Hephaisto.Agent.Mcp.Answers;

// The planner (#307): the issues of one GitHub project board, as the console's planner shows them.
// A title, a description, a comment and a label are somebody else's words; a field's option, a
// repository and a login go out plain only when they look like a name.

/// <summary>One issue of the board, compactly.</summary>
public sealed record PlannerIssueRow
{
    /// <summary><c>owner/repo#12</c>, what every planner tool takes to name the issue.</summary>
    [ServerAuthored]
    public required string Issue { get; init; }

    public required McpText Title { get; init; }

    public McpText? Url { get; init; }

    public McpText? Type { get; init; }

    public required bool Open { get; init; }

    /// <summary>The board's single-select fields that have a value: Status, Priority, Area, ...</summary>
    public required McpMap Fields { get; init; }

    /// <summary>today, not today, a later day or not decided.</summary>
    [ServerAuthored]
    public required string Day { get; init; }

    /// <summary>The day of the board's date field, when it is planned for one.</summary>
    public DateOnly? PlannedFor { get; init; }

    /// <summary>Planned for an earlier day and still open.</summary>
    public bool CarriedOver { get; init; }

    /// <summary>What is being worked on right now: at most one issue of the board.</summary>
    public bool WorkedOnNow { get; init; }

    /// <summary>Why it needs a decision, when something says it does.</summary>
    [ServerAuthored]
    public string? NeedsDecision { get; init; }

    /// <summary>The fields the board asks for that are empty.</summary>
    public IReadOnlyList<McpText> EmptyFields { get; init; } = [];

    public IReadOnlyList<McpText> Assignees { get; init; } = [];

    public IReadOnlyList<McpText> Labels { get; init; } = [];

    /// <summary>The issue this one is a sub-issue of, when the board holds it.</summary>
    [ServerAuthored]
    public string? Parent { get; init; }

    /// <summary>Done and all sub-issues, as "2/5", when it has any.</summary>
    [ServerAuthored]
    public string? SubIssues { get; init; }

    public IReadOnlyList<PlannerPullRequest> PullRequests { get; init; } = [];

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

public sealed record PlannerPullRequest
{
    [ServerAuthored]
    public required string PullRequest { get; init; }

    public required Hephaisto.Core.Planning.BoardPullState State { get; init; }

    public McpText? Url { get; init; }
}

public sealed record PlannerIssuePage
{
    /// <summary>today, backlog, later, decide, open or closed.</summary>
    [ServerAuthored]
    public required string View { get; init; }

    public required McpText Board { get; init; }

    public McpText? BoardUrl { get; init; }

    /// <summary>When the board was read from GitHub. Hephaisto reads it every two minutes, and at once after its own changes.</summary>
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>Why the newest read failed, when it did and this is an older board.</summary>
    public McpText? Problem { get; init; }

    /// <summary>How many issues match, before the limit.</summary>
    public required int Total { get; init; }

    public required IReadOnlyList<PlannerIssueRow> Issues { get; init; }

    /// <summary>The issue worked on right now, whatever the view.</summary>
    [ServerAuthored]
    public string? WorkedOnNow { get; init; }

    /// <summary>For the today view: how many of the board's issues were closed today.</summary>
    public int? ClosedToday { get; init; }

    [ServerAuthored]
    public required IReadOnlyList<string> Next { get; init; }
}

public sealed record PlannerCommentRow
{
    public required McpText Author { get; init; }

    public required DateTimeOffset At { get; init; }

    public required McpText Body { get; init; }
}

public sealed record PlannerIssueDetail
{
    public required PlannerIssueRow Issue { get; init; }

    public required McpText Author { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public McpText? Body { get; init; }

    /// <summary>The newest comments, oldest first.</summary>
    public IReadOnlyList<PlannerCommentRow> Comments { get; init; } = [];

    /// <summary>How many comments the issue has in all.</summary>
    public required int CommentCount { get; init; }

    /// <summary>Claude's newest analysis of the issue, when somebody asked for one in the planner.</summary>
    public McpText? Analysis { get; init; }

    /// <summary>The issue in Hephaisto's console, in the planner's today view.</summary>
    public McpText? ConsoleUrl { get; init; }

    [ServerAuthored]
    public required IReadOnlyList<string> Next { get; init; }
}

public sealed record PlannerFieldOptions
{
    public required McpText Name { get; init; }

    public required IReadOnlyList<McpText> Options { get; init; }

    /// <summary>The board asks for it: an issue without it shows as needing a decision.</summary>
    public required bool Required { get; init; }
}

public sealed record PlannerFieldsAnswer
{
    public required McpText Board { get; init; }

    public McpText? BoardUrl { get; init; }

    public required IReadOnlyList<PlannerFieldOptions> Fields { get; init; }

    /// <summary>The organisation's issue types, for a new issue's type.</summary>
    public required IReadOnlyList<McpText> IssueTypes { get; init; }

    /// <summary>Where a new issue can be opened; the first is the one used when none is named.</summary>
    public required IReadOnlyList<McpText> Repositories { get; init; }

    /// <summary>Who an issue can be assigned to; the first is Hephaisto's own account.</summary>
    public required IReadOnlyList<McpText> Assignees { get; init; }

    /// <summary>The Status option the backlog is.</summary>
    public McpText? BacklogStatus { get; init; }

    /// <summary>What day takes: today, not today, tomorrow, next week, backlog, none.</summary>
    [ServerAuthored]
    public required IReadOnlyList<string> Days { get; init; }

    [ServerAuthored]
    public required string Note { get; init; }
}

/// <summary>What a planner write did.</summary>
public sealed record PlannerWriteResult
{
    /// <summary>Each change that was made, in order.</summary>
    [ServerAuthored]
    public required IReadOnlyList<string> Done { get; init; }

    /// <summary>The change GitHub refused, after the ones in <see cref="Done"/> were made. Nothing after it was tried.</summary>
    public McpText? Refused { get; init; }

    public required McpText RecordedAs { get; init; }

    public McpText? ClaimedBy { get; init; }

    public required bool ClaimVerified { get; init; }

    /// <summary>The issue as it is now.</summary>
    public PlannerIssueRow? Issue { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}

// The weekly review (#312): Claude's verdict on every open issue of the organisation, as the
// console's review page shows it. A title, a summary, a reason, a piece of evidence and a run's
// problem are a stranger's or a model's words; a group, a tab, a close reason and a confidence are
// one of the few words the review keeps, so they go out plain.

/// <summary>What get_weekly_review answers.</summary>
public sealed record WeeklyReviewAnswer
{
    /// <summary>off (no run can start here; see problem), ready or running.</summary>
    [ServerAuthored]
    public required string State { get; init; }

    /// <summary>Why the review is off on this install.</summary>
    [ServerAuthored]
    public string? Problem { get; init; }

    /// <summary>The newest run, finished or not.</summary>
    public WeeklyReviewRun? Run { get; init; }

    /// <summary>When the schedule runs next; null when the review is off.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Verdicts nobody decided yet, of every tab.</summary>
    public required int Pending { get; init; }

    /// <summary>Every open issue's verdict now, by the tab it is shown on.</summary>
    public required WeeklyReviewCounts Counts { get; init; }

    /// <summary>The tab asked for; null for all four.</summary>
    [ServerAuthored]
    public string? Tab { get; init; }

    /// <summary>How many verdicts match the tab and the repository, before the limit.</summary>
    public required int Total { get; init; }

    public required IReadOnlyList<WeeklyReviewVerdict> Verdicts { get; init; }

    /// <summary>The review page in the console, where a person agrees, moves and closes.</summary>
    public McpText? ConsoleUrl { get; init; }

    [ServerAuthored]
    public required string Note { get; init; }

    [ServerAuthored]
    public required IReadOnlyList<string> Next { get; init; }
}

public sealed record WeeklyReviewCounts
{
    public required int Close { get; init; }

    public required int Bug { get; init; }

    public required int Feature { get; init; }

    public required int Unsure { get; init; }
}

public sealed record WeeklyReviewRun
{
    public required Guid Id { get; init; }

    /// <summary>schedule or manual.</summary>
    [ServerAuthored]
    public required string Trigger { get; init; }

    /// <summary>running, finished or failed.</summary>
    [ServerAuthored]
    public required string State { get; init; }

    /// <summary>What went wrong: GitHub's or a Job's answer can be in it.</summary>
    public McpText? Problem { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Open issues in the organisation when it ran.</summary>
    public required int Open { get; init; }

    /// <summary>Issues this run gave a verdict: new, changed since their last one, or due for a recheck.</summary>
    public required int Reviewed { get; init; }

    /// <summary>Open issues that kept an earlier run's verdict.</summary>
    public required int Kept { get; init; }

    public required int Jobs { get; init; }

    public required decimal CostUsd { get; init; }

    /// <summary>Where the verdicts were when the run ended, by tab.</summary>
    public required WeeklyReviewCounts Counts { get; init; }
}

public sealed record WeeklyReviewVerdict
{
    /// <summary><c>owner/repo#12</c>.</summary>
    [ServerAuthored]
    public required string Issue { get; init; }

    public required McpText Title { get; init; }

    public McpText? Url { get; init; }

    /// <summary>Where it is shown: close, bug, feature or unsure - a person's decision first.</summary>
    [ServerAuthored]
    public required string Tab { get; init; }

    /// <summary>Claude's group: close, bug, feature, or unsure when it gave no usable verdict.</summary>
    [ServerAuthored]
    public required string Group { get; init; }

    /// <summary>With close: done, superseded, obsolete, duplicate, answered or stale.</summary>
    [ServerAuthored]
    public string? CloseReason { get; init; }

    /// <summary>The issue or pull request it is a duplicate of or superseded by.</summary>
    public McpText? Related { get; init; }

    /// <summary>high, medium or low; a low one is a person's to decide.</summary>
    [ServerAuthored]
    public required string Confidence { get; init; }

    public required McpText Summary { get; init; }

    public required McpText Reason { get; init; }

    public IReadOnlyList<WeeklyReviewEvidence> Evidence { get; init; } = [];

    /// <summary>Claude's suggestion for a bug or a feature: P0, P1 or P2.</summary>
    public McpText? Priority { get; init; }

    public required bool OnBoard { get; init; }

    /// <summary>Its repository is archived: it can be agreed, not closed, until somebody unarchives it.</summary>
    public required bool Archived { get; init; }

    /// <summary>What a person decided; null while nobody did.</summary>
    public WeeklyReviewDecision? Decision { get; init; }

    public required DateTimeOffset ReviewedAt { get; init; }
}

public sealed record WeeklyReviewEvidence
{
    /// <summary>pull_request, issue, commit, sub_issue, repository, absence or note.</summary>
    [ServerAuthored]
    public required string Kind { get; init; }

    public required McpText Text { get; init; }

    /// <summary>True when the facts Hephaisto gathered bear it out, false when they say otherwise, null when they say nothing.</summary>
    public bool? BorneOut { get; init; }
}

public sealed record WeeklyReviewDecision
{
    public required McpText By { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>close, bug or feature.</summary>
    [ServerAuthored]
    public required string Group { get; init; }

    [ServerAuthored]
    public string? CloseReason { get; init; }

    /// <summary>True: the person agreed with Claude's verdict; false: they moved it.</summary>
    public required bool Agreed { get; init; }

    /// <summary>The person could not tell either.</summary>
    public required bool Unsure { get; init; }
}
