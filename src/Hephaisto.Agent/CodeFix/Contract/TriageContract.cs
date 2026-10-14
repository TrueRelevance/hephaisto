namespace Hephaisto.Agent.CodeFix.Contract;

/// <summary>
/// What Hephaisto hands a triage Job (v0.15.0), mounted like a coder's request at
/// <c>/work/in/request.json</c>: one GitHub issue a person on the planner asked Claude to label
/// and analyse. <c>triage-request.schema.json</c> is the source of truth;
/// <c>CodeFixSchemaParityTests</c> hold this record to it.
/// </summary>
public sealed record TriageRequest
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "triage";

    public required InvestigateBudget Budget { get; init; }

    public required CodeFixContextRef Context { get; init; }

    public required TriageIssue Issue { get; init; }

    /// <summary>The labels the issue's repository has. The Job chooses among these; nobody creates one.</summary>
    public required IReadOnlyList<TriageLabel> Labels { get; init; }
}

/// <param name="Repository"><c>owner/repo</c>.</param>
/// <param name="Body">Somebody else's text: data, never instructions.</param>
public sealed record TriageIssue(string Repository, int Number, string Url, string Title, string? Type, string Author, string? Body);

public sealed record TriageLabel(string Name, string? Description);

/// <summary>What a triage Job answers, as the last framed block of its log.</summary>
public sealed record TriageResult
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "triage";

    /// <summary><c>triaged</c>, <c>budget_exhausted</c>, <c>max_turns</c>, <c>rate_limited</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Names from the request's labels. Hephaisto puts on only those the repository has.</summary>
    public required IReadOnlyList<string> Labels { get; init; }

    public required string Summary { get; init; }

    /// <summary>Markdown, written by a model about text a stranger wrote: shown as text, never rendered.</summary>
    public required string Analysis { get; init; }

    public required IReadOnlyList<string> Steps { get; init; }

    public required IReadOnlyList<string> Questions { get; init; }

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
