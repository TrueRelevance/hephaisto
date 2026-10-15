using System.Globalization;

using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Planning;
using Hephaisto.Agent.Observability;

namespace Hephaisto.Agent.Components;

/// <summary>
/// Presentation-only formatting shared by the pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every state and severity carries a glyph and a word, never colour alone.</b> The
/// intended reader is someone woken at 3am, reading a dense table on whatever monitor is in
/// the room, possibly colour-blind, definitely not at their best. Colour is a second
/// channel here, never the only one - a row whose meaning disappears on a badly calibrated
/// screen is worse than a plain one, because it looks like it is telling you something.
/// </para>
/// <para>
/// ASCII glyphs rather than emoji: emoji render at unpredictable sizes and break the
/// monospace grid the tables depend on, and the pod's base image carries no colour font
/// anyway.
/// </para>
/// </remarks>
public static class Display
{
    public static string StateGlyph(IncidentState state) => state switch
    {
        IncidentState.Detected => "*",
        IncidentState.Triaging => "?",
        IncidentState.Suppressed => "-",
        IncidentState.Investigating => "~",
        IncidentState.AwaitingApproval => "!",
        IncidentState.Acting => ">",
        IncidentState.Verifying => "=",
        IncidentState.Resolved => "+",
        IncidentState.Escalated => "^",
        IncidentState.Expired => ".",
        // A human dealt with it. Distinct from Expired's "." - that one means nobody ever
        // answered and the signal stopped, which is not the same outcome at all.
        IncidentState.Closed => "x",
        _ => "?",
    };

    /// <summary>Maps to a CSS class, never to an inline colour: the palette lives in one
    /// stylesheet so a contrast fix is one edit.</summary>
    public static string StateClass(IncidentState state) => state switch
    {
        IncidentState.Detected => "st-detected",
        IncidentState.Triaging => "st-triaging",
        IncidentState.Suppressed => "st-suppressed",
        IncidentState.Investigating => "st-investigating",
        IncidentState.AwaitingApproval => "st-awaiting",
        IncidentState.Acting => "st-acting",
        IncidentState.Verifying => "st-verifying",
        IncidentState.Resolved => "st-resolved",
        IncidentState.Escalated => "st-escalated",
        IncidentState.Expired => "st-expired",
        IncidentState.Closed => "st-closed",
        _ => "st-detected",
    };

    /// <summary>
    /// A connection's state (#111). Four glyphs, because four states is the point: a panel that
    /// cannot distinguish "deliberately off" from "broken" is one people stop reading.
    /// </summary>
    public static string ConnectionGlyph(ConnectionState state) => state switch
    {
        ConnectionState.Healthy => "+",
        ConnectionState.Degraded => "~",
        ConnectionState.Unreachable => "!!",
        ConnectionState.NotConfigured => "-",
        _ => "?",
    };

    /// <remarks>
    /// NotConfigured borrows the suppressed colour rather than a warning one: it is a choice
    /// somebody made, not a fault, and painting it amber would make a correct install look wrong.
    /// </remarks>
    public static string ConnectionClass(ConnectionState state) => state switch
    {
        ConnectionState.Healthy => "conn-healthy",
        ConnectionState.Degraded => "conn-degraded",
        ConnectionState.Unreachable => "conn-unreachable",
        ConnectionState.NotConfigured => "conn-unset",
        _ => "conn-unset",
    };

    /// <summary>
    /// A code-fix attempt's state (v0.9.0). Its own vocabulary, because an attempt has its own
    /// lifecycle and is routinely running on an incident that is already Closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The glyphs borrow the incident vocabulary wherever the meaning is the same, so a reader
    /// who knows one table can read the other: <c>*</c> exists but nothing runs yet, <c>~</c> is
    /// read-only work in progress (as investigating), <c>!</c> waits for a human (as awaiting
    /// approval), <c>&gt;</c> is writing (as acting), <c>+</c> is the good end, <c>.</c> is
    /// nobody answered (as expired).
    /// </para>
    /// <para>
    /// The three that have no incident twin: <c>x</c> failed, <c>-</c> a person said no, and
    /// <c>/</c> cancelled - cut off by the kill switch or the mode, which is neither a failure
    /// of the coder nor a human judgement on the plan.
    /// </para>
    /// </remarks>
    public static string CodeFixGlyph(CodeFixState state) => state switch
    {
        CodeFixState.Eligible => "*",
        CodeFixState.Planning => "~",
        CodeFixState.PlanReady => "!",
        CodeFixState.Implementing => ">",
        CodeFixState.PrOpened => "+",
        CodeFixState.Failed => "x",
        CodeFixState.Denied => "-",
        CodeFixState.Expired => ".",
        CodeFixState.Cancelled => "/",
        _ => "?",
    };

    /// <summary>
    /// Reuses the incident state classes where the meaning matches, so the colour of "waiting for a
    /// human" or "writing" is one decision in app.css rather than two that can drift.
    /// </summary>
    /// <remarks>
    /// Denied takes the closed colour - dim, a person decided - and Cancelled the suppressed one,
    /// faint: a switch stopped it and nobody judged it. Failed takes the escalated red because it
    /// is the one outcome that says "look at this".
    /// </remarks>
    public static string CodeFixClass(CodeFixState state) => state switch
    {
        CodeFixState.Eligible => "st-detected",
        CodeFixState.Planning => "st-investigating",
        CodeFixState.PlanReady => "st-awaiting",
        CodeFixState.Implementing => "st-acting",
        CodeFixState.PrOpened => "st-resolved",
        CodeFixState.Failed => "st-escalated",
        CodeFixState.Denied => "st-closed",
        CodeFixState.Expired => "st-expired",
        CodeFixState.Cancelled => "st-suppressed",
        _ => "st-detected",
    };

    /// <summary>The word beside the glyph. Spelled out because "propened" is not a word.</summary>
    public static string CodeFixWord(CodeFixState state) => state switch
    {
        CodeFixState.Eligible => "eligible",
        CodeFixState.Planning => "planning",
        CodeFixState.PlanReady => "plan ready",
        CodeFixState.Implementing => "implementing",
        CodeFixState.PrOpened => "pr opened",
        CodeFixState.Failed => "failed",
        CodeFixState.Denied => "denied",
        CodeFixState.Expired => "expired",
        CodeFixState.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>
    /// An address that may be the target of a link: absolute, http or https. Anything else -
    /// empty, relative, another scheme - is null, and the caller shows text instead. An issue's
    /// address is whatever the API that was asked said it is.
    /// </summary>
    public static string? HttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? url : null;

    /// <summary>
    /// Through what a plan was answered, in the words a reader of the attempt's page needs. The
    /// console records <c>Ui</c>; the API <c>Api</c>, or <c>Oidc</c> when the caller carried a
    /// token; a comment on the issue <c>GitHub</c> (v0.14.0). Null where nothing was recorded.
    /// </summary>
    public static string? DecidedThrough(ApprovalSource? source) => source switch
    {
        ApprovalSource.Ui => "the console",
        ApprovalSource.Api => "the API",
        ApprovalSource.Oidc => "the API, signed in",
        ApprovalSource.Teams => "a Teams card",
        ApprovalSource.GitHub => "a comment on the issue",
        _ => null,
    };

    /// <summary>
    /// A work item's three states (v0.14.0), in the code-fix vocabulary: taken is work in
    /// progress, done is the good end, and cancelled - the issue closed, Hephaisto unassigned,
    /// its pull request closed unmerged - is nobody's failure.
    /// </summary>
    public static string WorkItemGlyph(WorkItemState state) => state switch
    {
        WorkItemState.Taken => "~",
        WorkItemState.Done => "+",
        WorkItemState.Cancelled => "/",
        _ => "?",
    };

    public static string WorkItemClass(WorkItemState state) => state switch
    {
        WorkItemState.Taken => "st-investigating",
        WorkItemState.Done => "st-resolved",
        WorkItemState.Cancelled => "st-suppressed",
        _ => "st-detected",
    };

    public static string WorkItemWord(WorkItemState state) => state switch
    {
        WorkItemState.Taken => "taken",
        WorkItemState.Done => "done",
        WorkItemState.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>
    /// Why the planner (v0.15.0) puts an item in front of a person. Every reason is "a person
    /// decides", so they share the colour of a plan that waits; the glyph and the word tell them
    /// apart. An item nothing is wrong with is quiet.
    /// </summary>
    public static string PlannerReasonGlyph(PlannerReason reason) => reason switch
    {
        PlannerReason.MergedStillOpen => "!",
        PlannerReason.SubIssuesDone => "+",
        PlannerReason.PullRequestQuiet => "~",
        PlannerReason.FieldEmpty => "?",
        PlannerReason.None => ".",
        _ => "?",
    };

    public static string PlannerReasonClass(PlannerReason reason) => reason switch
    {
        PlannerReason.None => "st-closed",
        _ => "st-awaiting",
    };

    public static string PlannerReasonWord(PlannerReason reason) => reason switch
    {
        PlannerReason.MergedStillOpen => "merged, still open",
        PlannerReason.SubIssuesDone => "every sub-issue done",
        PlannerReason.PullRequestQuiet => "pull request quiet",
        PlannerReason.FieldEmpty => "a required field is empty",
        PlannerReason.None => "open",
        _ => "unknown",
    };

    /// <summary>The day's decision, in the words of the two labels that hold it.</summary>
    public static string PlannerDayWord(PlannerDay day) => day switch
    {
        PlannerDay.Today => "today",
        PlannerDay.NotToday => "not today",
        PlannerDay.Undecided => "not decided yet",
        PlannerDay.Later => "a later day",
        _ => "unknown",
    };

    public static string PlannerDayGlyph(PlannerDay day) => day switch
    {
        PlannerDay.Today => "*",
        PlannerDay.NotToday => "-",
        PlannerDay.Undecided => ".",
        PlannerDay.Later => ">",
        _ => "?",
    };

    /// <summary>
    /// A priority by where it stands among its field's options, because a board names them as it
    /// likes: the first is the one that cannot wait, the second matters, the rest are quiet.
    /// </summary>
    public static string PlannerPriorityClass(int rank) => rank switch
    {
        0 => "sev-critical",
        1 => "sev-warning",
        _ => "sev-info",
    };

    public static string PullStateGlyph(BoardPullState state) => state switch
    {
        BoardPullState.Merged => "+",
        BoardPullState.Open => "~",
        BoardPullState.Closed => "x",
        _ => "?",
    };

    public static string PullStateClass(BoardPullState state) => state switch
    {
        BoardPullState.Merged => "st-resolved",
        BoardPullState.Open => "st-investigating",
        BoardPullState.Closed => "st-closed",
        _ => "st-detected",
    };

    public static string PullStateWord(BoardPullState state) => state switch
    {
        BoardPullState.Merged => "merged",
        BoardPullState.Open => "open",
        BoardPullState.Closed => "closed",
        _ => "unknown",
    };

    /// <summary><c>owner/repo#12</c> from an issue's address; the address itself when it is not one.</summary>
    public static string ShortIssue(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 4 && parts[2] == "issues" && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return $"{parts[0]}/{parts[1]}#{number.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return url;
    }

    /// <summary>How long ago, in the one unit a person would say: today, yesterday, N days ago.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now.UtcDateTime.Date - at.UtcDateTime.Date).TotalDays);

        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            _ => $"{days.ToString(CultureInfo.InvariantCulture)} days ago",
        };
    }

    /// <summary>
    /// The code-fix mode as the nav's agent-mode badge colours it: Off is quiet, Plan only reads
    /// (the observe colour), Pr can end in a write and takes the auto colour.
    /// </summary>
    public static string CodeFixModeClass(CodeFixMode mode) => mode switch
    {
        CodeFixMode.Plan => "mode-observe",
        CodeFixMode.Pr => "mode-auto",
        _ => "mode-off",
    };

    /// <summary>
    /// <c>owner/repo</c> from a clone URL, for a dense cell. The full URL always goes in a title.
    /// </summary>
    public static string ShortRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "—";
        }

        var trimmed = url.Trim().TrimEnd('/');

        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        // git@host:owner/repo as well as https://host/owner/repo.
        var parts = trimmed.Replace(':', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : trimmed;
    }

    /// <summary>Seven characters of a commit sha, the length git itself abbreviates to.</summary>
    public static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "—"
        : sha.Length > 7 && sha.All(Uri.IsHexDigit) ? sha[..7]
        : sha;

    public static string SeverityGlyph(Severity severity) => severity switch
    {
        Severity.Critical => "!!",
        Severity.Warning => "!",
        _ => "i",
    };

    public static string SeverityClass(Severity severity) => severity switch
    {
        Severity.Critical => "sev-critical",
        Severity.Warning => "sev-warning",
        _ => "sev-info",
    };

    public static string RiskClass(RiskTier risk) => risk switch
    {
        RiskTier.Critical => "risk-critical",
        RiskTier.High => "risk-high",
        RiskTier.Medium => "risk-medium",
        _ => "risk-low",
    };

    public static string DecisionGlyph(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => "+",
        PolicyDecision.RequireApproval => "!",
        _ => "x",
    };

    public static string DecisionClass(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => "dec-allow",
        PolicyDecision.RequireApproval => "dec-approval",
        _ => "dec-deny",
    };

    public static string VerificationGlyph(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "+",
        VerificationOutcome.Failed => "x",
        VerificationOutcome.Inconclusive => "?",
        _ => "\u00b7",
    };

    /// <summary>
    /// Inconclusive gets its own class rather than sharing Failed's.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point of the outcome existing. A check that could not run
    /// has learned nothing, and rendering it in the same red as a check that ran and found the
    /// fix did not work would tell a reader the opposite of what happened - the agent does not
    /// roll back on Inconclusive, and a page implying it should have is a page that produces
    /// the wrong follow-up.
    /// </remarks>
    public static string VerificationClass(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "ver-passed",
        VerificationOutcome.Failed => "ver-failed",
        VerificationOutcome.Inconclusive => "ver-inconclusive",
        _ => "ver-pending",
    };

    /// <summary>Which of the three scheduled checks this is, as the schedule names them.</summary>
    public static string VerificationWhen(int attempt) => attempt switch
    {
        1 => "T+60s",
        2 => "T+5m",
        3 => "T+15m",
        _ => $"attempt {attempt}",
    };

    /// <summary>Compact and monotonic: 4s, 3m12s, 2h05m, 3d04h. Never "a few minutes ago" -
    /// during an incident the difference between 9 and 14 minutes is the whole question.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d{span.Hours:D2}h"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m{span.Seconds:D2}s"
                    : $"{span.TotalSeconds:F1}s";
    }

    public static string Millis(long ms) =>
        ms >= 1000 ? $"{ms / 1000.0:F2}s" : $"{ms}ms";

    /// <summary>UTC, always, with the offset spelled out. Local time on a page read from two
    /// timezones is how two people compare timestamps and reach different conclusions.</summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string TimeOnly(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Bytes(int bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} kB",
        _ => $"{bytes} B",
    };

    public static string Tokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:F1}k" : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>Six decimals. A single step costs fractions of a cent and rounding it to two
    /// makes every row read $0.00, which is exactly the number nobody can act on.</summary>
    public static string Usd(decimal usd) => $"${usd.ToString("F6", CultureInfo.InvariantCulture)}";

    public static string Percent(double ratio) =>
        (ratio * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>Short id for a dense table. The full value is always in a title attribute -
    /// a truncated identifier you cannot recover is a dead end.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>Only ever used with the full text one expander away.</summary>
    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : text.Length <= max ? text
        : string.Concat(text.AsSpan(0, max), "…");
}
