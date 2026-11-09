namespace Hephaisto.Agent.CodeFix.Contract;

/// <summary>
/// What Hephaisto hands a review Job (#312), mounted like a coder's request at
/// <c>/work/in/request.json</c>: one batch of the organisation's open issues, each with the facts
/// Hephaisto gathered about it, and the repositories the Job may clone read-only.
/// <c>review-request.schema.json</c> is the source of truth; <c>CodeFixSchemaParityTests</c> hold
/// this record to it. Every member is required there, so none is optional here.
/// </summary>
public sealed record ReviewRequest
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "review";

    public required InvestigateBudget Budget { get; init; }

    public required CodeFixContextRef Context { get; init; }

    /// <summary>What may be cloned: only the batch's repositories, and an archived one never.</summary>
    public required IReadOnlyList<ReviewRepository> Repositories { get; init; }

    public required IReadOnlyList<ReviewIssue> Issues { get; init; }
}

/// <param name="Repository"><c>owner/repo</c>.</param>
public sealed record ReviewRepository(string Repository, string Url, string DefaultBranch, bool Archived);

/// <summary>
/// One issue of the batch and what GitHub says about it. Everything a person typed - title, body,
/// labels, comments, the titles of what references it - is somebody else's text.
/// </summary>
/// <param name="CreatedAt">ISO 8601, as text: the schema's <c>date-time</c>, written by Hephaisto with a <c>Z</c>.</param>
/// <param name="Board">The issue's item on the planner's project, or null: not on the board.</param>
public sealed record ReviewIssue(
    string Repository,
    int Number,
    string Url,
    string Title,
    string? Type,
    string Author,
    string? Body,
    IReadOnlyList<string> Labels,
    string CreatedAt,
    string UpdatedAt,
    bool Archived,
    ReviewBoard? Board,
    IReadOnlyList<ReviewCrossReference> CrossReferences,
    IReadOnlyList<ReviewCommit> Commits,
    IReadOnlyList<ReviewSubIssue> SubIssues,
    IReadOnlyList<ReviewIssueComment> Comments,
    int CommentsTotal);

public sealed record ReviewBoard(string? Status, string? Priority, int? DaysInStatus, IReadOnlyList<ReviewBoardField> Fields);

public sealed record ReviewBoardField(string Name, string Value);

/// <param name="Kind"><c>pull_request</c> or <c>issue</c>.</param>
/// <param name="State"><c>open</c>, <c>closed</c> or <c>merged</c> - merged only for a pull request.</param>
public sealed record ReviewCrossReference(string Kind, string Reference, string Title, string State, string? ReferencedAt, string? ClosedAt);

public sealed record ReviewCommit(string Sha, string Repository, string? Message, string? At);

/// <param name="State"><c>open</c> or <c>closed</c>.</param>
public sealed record ReviewSubIssue(string Reference, string Title, string State, string? StateReason);

public sealed record ReviewIssueComment(string Author, string CreatedAt, string Body);

/// <summary>What a review Job answers, as the last framed block of its log.</summary>
public sealed record ReviewResult
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "review";

    /// <summary><c>reviewed</c>, <c>budget_exhausted</c>, <c>max_turns</c>, <c>rate_limited</c> or <c>failed</c>. Only <c>reviewed</c> carries verdicts.</summary>
    public required string Outcome { get; init; }

    public required IReadOnlyList<ReviewVerdictResult> Verdicts { get; init; }

    public required IReadOnlyList<ReviewClone> Clones { get; init; }

    /// <summary>The SDK's figure. Notional when <see cref="Billing"/> is <c>subscription</c>.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary><c>subscription</c>, <c>api</c> or <c>fake</c>.</summary>
    public required string Billing { get; init; }

    public required int Turns { get; init; }

    public required string? SessionId { get; init; }

    public required string? ContextSha { get; init; }

    public required string? Error { get; init; }

    public required IReadOnlyList<CodeFixDenial> DeniedToolCalls { get; init; }
}

/// <summary>One issue's verdict, as the Job wrote it. A model's text about a stranger's: shown as text, never rendered.</summary>
/// <param name="Issue"><c>owner/repo#n</c>.</param>
/// <param name="Group"><c>close</c>, <c>bug</c>, <c>feature</c>, or <c>unsure</c> - the runner's, never the model's.</param>
/// <param name="CloseReason">Set exactly when <paramref name="Group"/> is <c>close</c>.</param>
/// <param name="Confidence"><c>high</c>, <c>medium</c> or <c>low</c>.</param>
/// <param name="Priority"><c>P0</c>, <c>P1</c>, <c>P2</c> or null; null for a close and for unsure.</param>
public sealed record ReviewVerdictResult(
    string Issue,
    string Group,
    string? CloseReason,
    string? Related,
    string Confidence,
    string Summary,
    string Reason,
    IReadOnlyList<string> Evidence,
    string? Priority);

public sealed record ReviewClone(string Repository, bool Cloned, string? Head, string? Note);
