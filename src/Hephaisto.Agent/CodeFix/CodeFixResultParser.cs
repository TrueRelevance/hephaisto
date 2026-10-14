using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix.Contract;

namespace Hephaisto.Agent.CodeFix;

/// <summary>What came out of a coder's log.</summary>
public sealed record CodeFixParse<T>(T? Result, string? Json, string? Violation)
    where T : class
{
    public bool Ok => Result is not null;

    public static CodeFixParse<T> Violated(string why) => new(null, null, why);
}

/// <summary>
/// Reads the framed result block off the end of a coder pod's log. Pure: a string in, a verdict out.
/// </summary>
/// <remarks>
/// <para>
/// The coder has no credential for Hephaisto and no way to call it, so its answer travels the one
/// channel Hephaisto can already read: its own log. That log also carries everything the agent
/// printed while reading attacker-influenceable production evidence, so the parser trusts the
/// framing and nothing else:
/// </para>
/// <list type="bullet">
/// <item>the LAST <c>BEGIN</c>/<c>END</c> pair wins - an earlier block, or one echoed by a tool, is
/// not the answer;</item>
/// <item>the declared byte count and sha256 must match the payload exactly, so a block cut by log
/// rotation or stitched together by an echo is refused rather than half-read;</item>
/// <item>unknown members are refused, sizes are capped, and the attempt id must be the one this Job
/// was launched for - a pod printing another attempt's result changes nothing.</item>
/// </list>
/// </remarks>
public static partial class CodeFixResultParser
{
    public const int MaxPayloadBytes = 512 * 1024;

    private const string End = "---HEPHAISTO-RESULT-END---";

    [GeneratedRegex(@"^---HEPHAISTO-RESULT-BEGIN sha256=(?<sha>[0-9a-f]{64}) bytes=(?<bytes>\d{1,7})---$", RegexOptions.Multiline)]
    private static partial Regex BeginLine();

    public static CodeFixParse<CodeFixPlanResult> ParsePlan(string? log, Guid attemptId) =>
        Parse<CodeFixPlanResult>(log, attemptId, "plan", r => (r.AttemptId, r.Phase));

    public static CodeFixParse<CodeFixImplementResult> ParseImplement(string? log, Guid attemptId) =>
        Parse<CodeFixImplementResult>(log, attemptId, "implement", r => (r.AttemptId, r.Phase));

    /// <summary>An investigator Job's answer (v0.12.0 F5), under exactly the same framing rules.</summary>
    public static CodeFixParse<InvestigateResult> ParseInvestigate(string? log, Guid attemptId) =>
        Parse<InvestigateResult>(log, attemptId, "investigate", r => (r.AttemptId, r.Phase));

    /// <summary>A triage Job's answer (v0.15.0), under exactly the same framing rules.</summary>
    public static CodeFixParse<TriageResult> ParseTriage(string? log, Guid attemptId) =>
        Parse<TriageResult>(log, attemptId, "triage", r => (r.AttemptId, r.Phase));

    public static readonly IReadOnlySet<string> TriageOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "triaged", "budget_exhausted", "max_turns", "rate_limited", "failed",
    };

    public static readonly IReadOnlySet<string> InvestigateOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "concluded", "no_conclusion", "budget_exhausted", "max_turns", "rate_limited", "no_credential", "failed",
    };

    private static CodeFixParse<T> Parse<T>(string? log, Guid attemptId, string phase, Func<T, (Guid, string)> identity)
        where T : class
    {
        var (payload, violation) = LastBlock(log, BeginLine(), End);

        if (payload is null)
            return CodeFixParse<T>.Violated(violation!);

        T? result;

        try
        {
            result = JsonSerializer.Deserialize<T>(payload, CodeFixContract.Json);
        }
        catch (JsonException ex)
        {
            return CodeFixParse<T>.Violated($"the result does not match the v{CodeFixContract.Version} contract: {ex.Message}");
        }

        if (result is null)
            return CodeFixParse<T>.Violated("the result is null");

        var (id, reportedPhase) = identity(result);

        if (id != attemptId)
            return CodeFixParse<T>.Violated($"the result names attempt {id}, not {attemptId}");

        if (!string.Equals(reportedPhase, phase, StringComparison.Ordinal))
            return CodeFixParse<T>.Violated($"the result is for phase '{reportedPhase}', not '{phase}'");

        return new CodeFixParse<T>(result, payload, null);
    }

    /// <summary>
    /// The payload of the LAST block a BEGIN line opens, or why there is none: one line after the
    /// BEGIN line, the END marker on the line after that, and exactly the bytes and the sha256
    /// the BEGIN line declares.
    /// </summary>
    private static (string? Payload, string? Violation) LastBlock(string? log, Regex beginLine, string end)
    {
        if (string.IsNullOrEmpty(log))
            return (null, "the coder's log is empty; no result block");

        var begins = beginLine.Matches(log);

        if (begins.Count == 0)
            return (null, "no result block in the coder's log");

        var begin = begins[^1];
        var payloadStart = begin.Index + begin.Length;

        // The payload is exactly one line after the BEGIN line.
        if (payloadStart < log.Length && log[payloadStart] == '\r')
            payloadStart++;

        if (payloadStart >= log.Length || log[payloadStart] != '\n')
            return (null, "the result block has no payload line");

        payloadStart++;
        var payloadEnd = log.IndexOf('\n', payloadStart);

        if (payloadEnd < 0)
            return (null, "the result block is truncated (no END marker)");

        var payload = log[payloadStart..payloadEnd].TrimEnd('\r');
        var rest = log[(payloadEnd + 1)..];
        var endLine = rest.Split('\n', 2)[0].TrimEnd('\r');

        if (endLine != end)
            return (null, "the result block is not terminated by the END marker");

        var bytes = Encoding.UTF8.GetBytes(payload);
        var declaredBytes = int.Parse(begin.Groups["bytes"].Value, System.Globalization.CultureInfo.InvariantCulture);

        if (bytes.Length > MaxPayloadBytes)
            return (null, $"the result is {bytes.Length} bytes; the cap is {MaxPayloadBytes}");

        if (bytes.Length != declaredBytes)
            return (null, $"the result declares {declaredBytes} bytes and carries {bytes.Length}");

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        return string.Equals(sha, begin.Groups["sha"].Value, StringComparison.Ordinal)
            ? (payload, null)
            : (null, "the result's sha256 does not match its payload");
    }

    /// <summary>GitHub's own limit for a pull request's description. What is stored is never longer.</summary>
    public const int MaxPrBodyChars = 65_536;

    private const string PrBodyEnd = "---HEPHAISTO-PR-BODY-END---";

    [GeneratedRegex(@"^---HEPHAISTO-PR-BODY-BEGIN sha256=(?<sha>[0-9a-f]{64}) bytes=(?<bytes>\d{1,7})---$", RegexOptions.Multiline)]
    private static partial Regex PrBodyBeginLine();

    /// <summary>
    /// The pull request's description as the publish role sent it (v0.14.0), read from the same
    /// log as the result: a block of its own before it, framed the same way, whose one payload
    /// line is the text as a JSON string - so no line of the text can be a line of the log, and
    /// nothing in it can open or close a block.
    /// </summary>
    /// <remarks>
    /// Not part of the result contract, and nothing depends on it: it is what a person reads in
    /// the console instead of opening GitHub, and what a suite reads where GitHub is a stand-in
    /// that never saw the pull request. A block that is missing, cut or does not add up is
    /// <c>null</c>, never a failed attempt - the runner of an older image prints none.
    /// </remarks>
    public static string? ParsePrBody(string? log)
    {
        if (LastBlock(log, PrBodyBeginLine(), PrBodyEnd).Payload is not { } payload)
            return null;

        try
        {
            var body = JsonSerializer.Deserialize<string>(payload);

            if (string.IsNullOrWhiteSpace(body))
                return null;

            if (body.Length <= MaxPrBodyChars)
                return body;

            // Never half of a character: Postgres refuses a lone surrogate.
            return body[..(char.IsHighSurrogate(body[MaxPrBodyChars - 1]) ? MaxPrBodyChars - 1 : MaxPrBodyChars)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Frames a pull request's description exactly as the publish role does. For tests.</summary>
    public static string FramePrBody(string body)
    {
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);

        return $"---HEPHAISTO-PR-BODY-BEGIN sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))} bytes={bytes.Length}---\n{json}\n{PrBodyEnd}\n";
    }

    /// <summary>Plan outcomes the contract allows.</summary>
    public static readonly IReadOnlySet<string> PlanOutcomes =
        new HashSet<string>(StringComparer.Ordinal) { "planned", "not_a_code_problem", "insufficient_context", "failed" };

    public static readonly IReadOnlySet<string> ImplementOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "pr_opened", "already_exists", "no_changes", "build_failed", "tests_failed", "policy_diff", "failed",
    };

    /// <summary>
    /// The checks Hephaisto makes itself before believing a PR exists. The runner is trusted to
    /// report, not to decide: a PR on another repository, from another branch, or on a red build is
    /// refused however confidently it is described.
    /// </summary>
    public static string? CheckImplementPostConditions(
        CodeFixImplementResult result,
        string assignedBranch,
        string repositoryUrl,
        IReadOnlyCollection<string> allowedHosts,
        bool requireGreenBuild,
        bool requireTests = true)
    {
        if (!ImplementOutcomes.Contains(result.Outcome))
            return $"unknown outcome '{result.Outcome}'";

        if (result.Outcome is not ("pr_opened" or "already_exists"))
            return null;

        if (!string.Equals(result.Branch, assignedBranch, StringComparison.Ordinal))
            return $"the PR is from '{result.Branch}', not the assigned branch '{assignedBranch}'";

        if (!Uri.TryCreate(result.PrUrl, UriKind.Absolute, out var pr))
            return "the PR URL is not an absolute URL";

        if (!allowedHosts.Contains(pr.Host, StringComparer.OrdinalIgnoreCase))
            return $"the PR is on host '{pr.Host}', which is not allowed";

        if (RepositoryPath(repositoryUrl) is not { } repo
            || !pr.AbsolutePath.StartsWith(repo + "/pull/", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repoUri)
            || !string.Equals(repoUri.Host, pr.Host, StringComparison.OrdinalIgnoreCase))
        {
            return $"the PR '{result.PrUrl}' is not on the mapped repository '{repositoryUrl}'";
        }

        // Tests are required only when the approved plan claimed a test-level verification. Most
        // service repositories have no test project at all; the runner then reports
        // tests_passed=false because nothing ran, and demanding it would refuse every PR from
        // them. Those PRs carry a literal "Verification weak" heading instead - the honest place
        // for that fact - and the build is still required either way.
        if (requireGreenBuild && result.Outcome == "pr_opened" && !result.BuildPassed)
            return "a PR was opened on a build that was not green";

        if (requireGreenBuild && requireTests && result.Outcome == "pr_opened" && !result.TestsPassed)
            return "a PR was opened on a test run that was not green, for a plan that promised tests";

        return null;
    }

    /// <summary><c>/owner/repo</c> of a repository URL, without <c>.git</c>.</summary>
    public static string? RepositoryPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');

        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>Frames a payload exactly as the runner does. For tests and the dev forger in the e2e.</summary>
    public static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        return $"---HEPHAISTO-RESULT-BEGIN sha256={sha} bytes={bytes.Length}---\n{json}\n{End}\n";
    }
}
