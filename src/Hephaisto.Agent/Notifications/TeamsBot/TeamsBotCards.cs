using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// One incident as the board and an alert show it: the present, read at render.
/// </summary>
/// <remarks>
/// <b>The opposite of <see cref="NotificationSnapshot"/>, on purpose.</b> A snapshot freezes what
/// was true when an event happened, because a delivery reports the past. These two cards are
/// edited in place for as long as the incident lives, so what they show is the incident as it is
/// now - and a card that kept saying "Escalated" after somebody closed it would be the stale
/// message this whole channel exists to remove.
/// </remarks>
public sealed record TeamsIncident
{
    public required Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public SignalKind Kind { get; init; }

    public Severity Severity { get; init; }

    public IncidentState State { get; init; }

    public EscalationReason EscalationReason { get; init; }

    /// <summary>Human-readable <c>namespace/kind/name</c>, or empty when there is no target.</summary>
    public string Target { get; init; } = string.Empty;

    public DateTimeOffset OpenedAt { get; init; }

    public string? AssignedTo { get; init; }

    public string? AcknowledgedBy { get; init; }

    public string? ClosedBy { get; init; }

    /// <summary>Who an agent said it acknowledged or closed for (#157). Shown as unverified.</summary>
    public string? AcknowledgedClaimedBy { get; init; }

    public string? ClosedClaimedBy { get; init; }

    /// <summary>The resolution note, when there is one.</summary>
    public string? Summary { get; init; }

    /// <summary>The newest code-fix attempt's state, when the incident has one.</summary>
    public CodeFixState? CodeFix { get; init; }

    public string? PullRequestUrl { get; init; }

    /// <summary>The alert name the incident was opened by, when an alert opened it.</summary>
    public string? AlertName { get; init; }

    /// <summary>The start of what people wrote about that alert (#145), when they wrote something.</summary>
    public string? NoteExcerpt { get; init; }

    /// <summary>What the newest investigation found, when the incident was investigated.</summary>
    public TeamsDiagnosis? Diagnosis { get; init; }

    /// <summary>
    /// Whether ANY investigation of this incident produced a primary finding - not only the
    /// newest, which is all <see cref="Diagnosis"/> shows.
    /// </summary>
    public bool Diagnosed { get; init; }

    /// <summary>
    /// The actions of this incident that are waiting for a person to approve or deny them, oldest
    /// first. Read only for an incident that is awaiting approval; empty for every other.
    /// </summary>
    public IReadOnlyList<TeamsPendingAction> PendingActions { get; init; } = [];

    public bool IsOpen => State is not (
        IncidentState.Resolved
        or IncidentState.Expired
        or IncidentState.Suppressed
        or IncidentState.Closed);

    /// <summary>
    /// Whether a card offers another investigation: where the console's retry banner does, and
    /// the card is still kept in step.
    /// </summary>
    /// <remarks>
    /// The console (<c>IncidentDetail.razor</c>, <c>CanReinvestigate</c>) offers the retry for an
    /// Escalated or Expired incident none of whose investigations has a primary finding, and
    /// offers "reopen" on a Closed one. Only the first of those is an open incident. A card for an
    /// incident that is over is edited once to say how it ended and then left alone, so a button
    /// on it would outlive the state it was drawn for; reopening stays a link into the console.
    /// </remarks>
    public bool CanReinvestigate => State is IncidentState.Escalated && !Diagnosed;
}

/// <summary>
/// One proposed action that waits for a person, as a card names it beside its two buttons: what
/// would be done, and to what. Nobody approves a button that does not say.
/// </summary>
/// <remarks>
/// <see cref="Target"/> and <see cref="Arguments"/> come from a plan a model wrote. The card
/// renders both as <c>TextRun</c>s, redacted and cleaned, like every other model-written string.
/// </remarks>
public sealed record TeamsPendingAction
{
    public required Guid Id { get; init; }

    public ActionType Type { get; init; }

    public RiskTier Risk { get; init; }

    /// <summary>Human-readable <c>namespace/kind/name</c>, or empty when the action has no target.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The action's typed arguments as JSON, e.g. <c>{"replicas":3}</c>, when it has any.</summary>
    public string? Arguments { get; init; }
}

/// <summary>The addresses a card links to. All optional; a card without one is thinner, not broken.</summary>
public sealed record TeamsCardLinks
{
    /// <summary>The externally reachable base URL of this Hephaisto.</summary>
    public string? BaseUrl { get; init; }

    public string? GrafanaUrl { get; init; }

    /// <summary>A link into Teams that opens the board. Null until the board exists.</summary>
    public string? BoardUrl { get; init; }

    /// <summary>
    /// Whether an open alert carries the buttons that act (<c>Notifications:TeamsBot:Actions</c>).
    /// Not an address, but it lives here because it is the other thing a card needs to know about
    /// where it will be read: with it off no card can need Microsoft to call this process.
    /// </summary>
    public bool Actions { get; init; }

    /// <summary>
    /// Whether an open alert also carries Close: <see cref="Actions"/>, and somebody is mapped to
    /// the approver role (<c>Notifications:TeamsBot:Actions:Approvers</c>). With nobody mapped
    /// every click on it would be refused, so it is not drawn.
    /// </summary>
    /// <remarks>
    /// By configuration, never by reader: a card is the same for everybody who sees it, so who may
    /// close is decided at the click.
    /// </remarks>
    public bool Closing { get; init; }

    /// <summary>
    /// Whether an alert whose incident has an action awaiting approval says what it is and carries
    /// Approve and Deny for it (<c>Notifications:TeamsBot:Actions:Approvals</c>). Off, the card
    /// keeps its link into the console and names no action.
    /// </summary>
    public bool Approvals { get; init; }
}

/// <summary>
/// The newest investigation of an incident, as a card shows it: the diagnosis a person reads before
/// they open anything.
/// </summary>
/// <remarks>
/// Everything here but the numbers and the enums was written by a model that read
/// attacker-influenceable logs. The cards render it as <c>TextRun</c>s, which Teams never parses as
/// markdown, so a hypothesis cannot become a link, and it is redacted and cleaned first.
/// </remarks>
public sealed record TeamsDiagnosis
{
    /// <summary>The primary finding's hypothesis. Null when the investigation grounded no finding.</summary>
    public string? Hypothesis { get; init; }

    public string? Category { get; init; }

    public double? Confidence { get; init; }

    /// <summary>The planner's summary of what to do, or that nothing is to be done.</summary>
    public string? Summary { get; init; }

    public TerminationReason Termination { get; init; }

    /// <summary>InProcess, Job or JobFallback (v0.12.0 F5).</summary>
    public string Executor { get; init; } = Core.Investigations.InvestigationExecutors.InProcess;

    public string ModelId { get; init; } = string.Empty;

    /// <summary>The first cited excerpts of the primary finding, verbatim from a tool result.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Where it points in the running revision's source, when the investigator read it.</summary>
    public IReadOnlyList<CodeRef> CodeRefs { get; init; } = [];

    public bool Grounded => !string.IsNullOrWhiteSpace(Hypothesis);
}

/// <summary>
/// The verbs a button can send, which are the only ones the inbound route answers.
/// </summary>
/// <remarks>
/// <para>
/// Three are read-level acts in the console, and any member of the team may click them: saying
/// you have seen something, saying it is yours, and asking for another investigation.
/// </para>
/// <para>
/// <see cref="Close"/> sits with approval in the console, so here it takes somebody the install
/// maps to the approver role (<c>Notifications:TeamsBot:Actions:Approvers</c>).
/// </para>
/// <para>
/// <see cref="Approve"/> and <see cref="Deny"/> take the same role and a switch of their own
/// (<c>Notifications:TeamsBot:Actions:Approvals</c>). <b>Two verbs, never one with a boolean</b>,
/// for the reason the console has two routes: a truncated or mistyped payload must not be able
/// to turn a denial into an approval (backlog #124).
/// </para>
/// </remarks>
public static class TeamsBotVerbs
{
    public const string Acknowledge = "acknowledge";

    public const string AssignToMe = "assignToMe";

    public const string Reinvestigate = "reinvestigate";

    public const string Close = "close";

    public const string Approve = "approve";

    public const string Deny = "deny";

    /// <summary>The id of the Close card's text input, which Teams merges into the button's data.</summary>
    public const string ReasonInput = "reason";

    /// <summary>The key under which an Approve or Deny button carries the action it decides.</summary>
    public const string ActionId = "actionId";

    public static readonly IReadOnlyList<string> All = [Acknowledge, AssignToMe, Reinvestigate, Close, Approve, Deny];

    /// <summary>The verbs only a mapped approver may send.</summary>
    public static readonly IReadOnlyList<string> Approver = [Close, Approve, Deny];

    /// <summary>The verbs that are refused unless approvals from Teams are switched on.</summary>
    public static readonly IReadOnlyList<string> Approval = [Approve, Deny];
}

/// <summary>
/// The two cards the bot maintains, as pure functions of the incidents they show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every button is an <c>Action.OpenUrl</c>, unless <see cref="TeamsCardLinks.Actions"/>.</b> A
/// button that acts needs Microsoft to call this process, through the one authenticated route on
/// its own port (<c>TeamsBotActions</c>). With it off, a test asserts that nothing here can need
/// that route; with it on, that only an open alert carries verbs, that every one of them is in
/// <see cref="TeamsBotVerbs"/>, and which state and which configuration draws each.
/// </para>
/// <para>
/// Times are absolute. "Open for 20 min" would change every minute and turn a board that is
/// edited when something happens into one that is edited constantly.
/// </para>
/// </remarks>
public static class TeamsBotCards
{
    private const string CardVersion = "1.5";

    private const string CardContentType = "application/vnd.microsoft.card.adaptive";

    /// <summary>
    /// The largest board that is sent, in the UTF-16 bytes Teams counts.
    /// </summary>
    /// <remarks>
    /// Measured against a real tenant on 2026-09-28: 58 KB and 114 KB were both accepted, which
    /// is above either limit Microsoft documents (40 KB on one page, 100 KB on another). This
    /// stays inside what was seen to work rather than beside it. A count alone is not a limit -
    /// forty rows with long titles, a PR and a resolution note come to about 160 KB.
    /// </remarks>
    public const int MaxBoardBytes = 100 * 1024;

    /// <summary>A resolution note is prose of any length; a board row is not the place for all of it.</summary>
    private const int MaxSummaryLength = 280;

    /// <summary>A hypothesis on an alert card: enough to act on, not a page.</summary>
    private const int MaxHypothesisLength = 700;

    /// <summary>The one diagnosis line of a board row.</summary>
    private const int MaxBoardDiagnosisLength = 180;

    /// <summary>One cited excerpt. The console has the rest, and the step it came from.</summary>
    private const int MaxExcerptLength = 240;

    private const int MaxEvidenceShown = 2;

    private const int MaxCodeRefsShown = 3;

    /// <summary>The longest reason the Close card accepts, and the route keeps.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Actions an alert offers to decide. A plan rarely proposes more than one; past this the card
    /// says how many it left to the console rather than growing a button row per action.
    /// </summary>
    private const int MaxApprovalsShown = 3;

    /// <summary>
    /// The board: every open incident the caller passed, and a line for the ones it did not.
    /// </summary>
    /// <param name="incidents">Already ordered and already capped.</param>
    /// <param name="total">How many are open altogether, which may be more than are listed.</param>
    /// <param name="updatedAt">
    /// Null renders no timestamp, which is the form <see cref="Hash"/> is taken over - so the
    /// clock moving is not a change.
    /// </param>
    public static JsonObject Board(
        IReadOnlyList<TeamsIncident> incidents,
        int total,
        TeamsCardLinks links,
        DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(links);

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["size"] = "Large",
                ["weight"] = "Bolder",
                ["text"] = string.Create(CultureInfo.InvariantCulture, $"Hephaisto - open incidents ({total})"),
                ["wrap"] = true,
            },
        };

        if (updatedAt is { } at)
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["isSubtle"] = true,
                ["spacing"] = "None",
                ["text"] = $"Updated {Stamp(at)}",
                ["wrap"] = true,
            });
        }

        if (incidents.Count == 0)
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["spacing"] = "Medium",
                ["color"] = "Good",
                ["text"] = "No open incidents.",
                ["wrap"] = true,
            });
        }

        foreach (var incident in incidents)
        {
            body.Add(BoardRow(incident, links));
        }

        if (total > incidents.Count)
        {
            // Said in words rather than left as a silent cut: a board that lists twenty of
            // thirty and looks complete is worse than one that is visibly partial.
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["separator"] = true,
                ["spacing"] = "Medium",
                ["isSubtle"] = true,
                ["text"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"and {total - incidents.Count} more, not shown here. The list in Hephaisto has all of them."),
                ["wrap"] = true,
            });
        }

        var card = Card(body);

        // Full width. At the default width a row's three columns wrap into a column of words.
        card["msteams"] = new JsonObject { ["width"] = "Full" };

        if (!string.IsNullOrWhiteSpace(links.BaseUrl))
        {
            card["actions"] = new JsonArray(OpenUrl("All incidents", $"{links.BaseUrl.TrimEnd('/')}/incidents"));
        }

        return Activity(card, summary: null);
    }

    /// <summary>
    /// The board, listing as many of <paramref name="incidents"/> as fit in
    /// <see cref="MaxBoardBytes"/>. The ones that do not fit are counted in its last line.
    /// </summary>
    public static JsonObject BoardWithin(
        IReadOnlyList<TeamsIncident> incidents,
        int total,
        TeamsCardLinks links,
        DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(incidents);

        var listed = incidents.Count;

        while (true)
        {
            var board = Board([.. incidents.Take(listed)], total, links, updatedAt);

            if (listed == 0 || Encoding.Unicode.GetByteCount(board.ToJsonString()) <= MaxBoardBytes)
            {
                return board;
            }

            listed--;
        }
    }

    /// <summary>
    /// One incident, for one person. Posted once and then edited until the incident is over.
    /// </summary>
    public static JsonObject Alert(TeamsIncident incident, TeamsCardLinks links)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(links);

        var headline = Headline(incident);

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = headline,
                ["weight"] = "Bolder",
                ["size"] = "Medium",

                // Never the only signal: the headline says the state in words first.
                ["color"] = incident.IsOpen ? SeverityColour(incident.Severity) : "Good",
                ["wrap"] = true,
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = Title(incident),
                ["wrap"] = true,
            },
            new JsonObject { ["type"] = "FactSet", ["facts"] = Facts(incident) },
        };

        if (incident.Diagnosis is { } diagnosis)
        {
            // The investigation itself, on the card: what was found and what it rests on, before
            // the person opens anything. The console still has every step behind it.
            body.Add(DiagnosisSection(diagnosis));
        }

        if (!string.IsNullOrWhiteSpace(incident.Summary))
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = incident.Summary,
                ["wrap"] = true,
                ["isSubtle"] = true,
            });
        }

        if (!string.IsNullOrWhiteSpace(incident.NoteExcerpt))
        {
            // What the team wrote about this alert, before the person opens anything. The start of
            // it only: a card read on a lock screen is not where a page of notes goes.
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"Team note: {incident.NoteExcerpt}",
                ["wrap"] = true,
                ["isSubtle"] = true,
            });
        }

        if (links.Actions && links.Approvals && incident.IsOpen && incident.PendingActions.Count > 0)
        {
            // In the body and not in the card's button row, so each pair of buttons sits directly
            // under the sentence that says what it decides.
            body.Add(ApprovalSection(incident));
        }

        var card = Card(body);
        var actions = new JsonArray();

        if (links.Actions && incident.IsOpen)
        {
            // First, because they are what the person holding the phone came to do. An incident
            // somebody already acknowledged does not ask again; taking it over is still possible.
            if (string.IsNullOrWhiteSpace(incident.AcknowledgedBy))
            {
                actions.Add(Execute("Acknowledge", TeamsBotVerbs.Acknowledge, incident.Id));
            }

            actions.Add(Execute("Assign to me", TeamsBotVerbs.AssignToMe, incident.Id));

            if (incident.CanReinvestigate)
            {
                actions.Add(Execute("Reinvestigate", TeamsBotVerbs.Reinvestigate, incident.Id));
            }

            if (links.Closing)
            {
                actions.Add(CloseCard(incident.Id));
            }
        }

        foreach (var link in Links(incident, links).ToArray())
        {
            actions.Add(link!.DeepClone());
        }

        if (!string.IsNullOrWhiteSpace(incident.NoteExcerpt)
            && NotificationLinks.AlertNote(links.BaseUrl, incident.AlertName) is { } noteUrl)
        {
            actions.Add(OpenUrl("Alert note", noteUrl));
        }

        if (!string.IsNullOrWhiteSpace(links.BoardUrl))
        {
            actions.Add(OpenUrl("Open the board", links.BoardUrl));
        }

        if (actions.Count > 0)
        {
            card["actions"] = actions;
        }

        return Activity(card, summary: $"{headline}: {Title(incident)}");
    }

    /// <summary>
    /// What an alert becomes when a newer one for the same incident was posted below it.
    /// </summary>
    /// <remarks>
    /// A second event that needs a person has to be a NEW message, because an edit rings no
    /// phone. The older card cannot be deleted without leaving a "deleted" line, so it is
    /// shrunk to one line that says where to look instead.
    /// </remarks>
    public static JsonObject Superseded(TeamsIncident incident, TeamsCardLinks links)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(links);

        var card = Card(
        [
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["isSubtle"] = true,
                ["text"] = $"Superseded by a newer alert below: {Title(incident)}",
                ["wrap"] = true,
            },
        ]);

        var actions = new JsonArray();

        if (NotificationLinks.Incident(links.BaseUrl, incident.Id) is { } url)
        {
            actions.Add(OpenUrl("Open in Hephaisto", url));
        }

        if (actions.Count > 0)
        {
            card["actions"] = actions;
        }

        return Activity(card, summary: $"Superseded: {Title(incident)}");
    }

    /// <summary>
    /// An event about the agent itself, which has no incident to follow and is never edited.
    /// </summary>
    public static JsonObject AgentEvent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var s = message.Snapshot;

        var headline = s.Event switch
        {
            NotificationEvent.ModeChanged => "Autonomy re-armed",
            NotificationEvent.PolicyChanged => "Policy configuration changed",
            _ => "Hephaisto",
        };

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = headline,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["color"] = SeverityColour(s.Severity),
                ["wrap"] = true,
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = string.IsNullOrWhiteSpace(s.Title) ? "(no title)" : s.Title,
                ["wrap"] = true,
            },
        };

        if (!string.IsNullOrWhiteSpace(s.Reason))
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = s.Reason,
                ["wrap"] = true,
                ["isSubtle"] = true,
            });
        }

        body.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = Stamp(s.At),
            ["isSubtle"] = true,
            ["wrap"] = true,
        });

        return Activity(Card(body), summary: $"{headline}: {s.Title}");
    }

    /// <summary>
    /// A code-fix event about a work item - a GitHub issue handed to Hephaisto - which has no
    /// incident to follow: one message, never edited, and never a row on the board.
    /// </summary>
    /// <remarks>
    /// The issue's title and the plan's summary are somebody else's words and a model's, so both
    /// are <see cref="Plain"/> runs: Teams shows a TextRun as it is and never as markdown. The
    /// sentence between them is Hephaisto's own. Its buttons open pages and decide nothing - a
    /// plan is answered on the issue or in the console.
    /// </remarks>
    public static JsonObject WorkItemEvent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var s = message.Snapshot;

        var headline = s.Event switch
        {
            NotificationEvent.CodeFixPlanReady => "Code fix planned",
            NotificationEvent.CodeFixPrOpened => "Pull request opened",
            NotificationEvent.CodeFixFailed => "Code fix ended without a PR",
            _ => "Hephaisto",
        };

        var issue = string.IsNullOrWhiteSpace(s.Issue) ? "an issue" : s.Issue;
        var title = ModelText(s.Title, 200);

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"{headline} for {issue}",
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["wrap"] = true,
            },
            Plain(title.Length == 0 ? "(no title)" : title),
        };

        if (!string.IsNullOrWhiteSpace(s.Reason))
        {
            body.Add(Plain(ModelText(s.Reason, 600), subtle: true));
        }

        if (s.Event is NotificationEvent.CodeFixPlanReady && ModelText(s.Summary, 600) is { Length: > 0 } summary)
        {
            body.Add(Plain(summary));
        }

        body.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = Stamp(s.At),
            ["isSubtle"] = true,
            ["wrap"] = true,
        });

        var card = Card(body);
        var actions = new JsonArray();

        if (s.Event is NotificationEvent.CodeFixPrOpened && !string.IsNullOrWhiteSpace(s.ExternalUrl))
        {
            actions.Add(OpenUrl("Open the pull request", s.ExternalUrl));
        }

        if (!string.IsNullOrWhiteSpace(message.CodeFixUrl))
        {
            actions.Add(OpenUrl(
                s.Event is NotificationEvent.CodeFixPlanReady ? "Review the plan in Hephaisto" : "Open in Hephaisto",
                message.CodeFixUrl));
        }

        if (!string.IsNullOrWhiteSpace(s.IssueUrl))
        {
            actions.Add(OpenUrl("Open the issue", s.IssueUrl));
        }

        if (actions.Count > 0)
        {
            card["actions"] = actions;
        }

        var announced = $"{headline} for {issue}";

        return Activity(card, summary: message.AlsoSuppressed > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{announced} (+{message.AlsoSuppressed} suppressed)")
            : announced);
    }

    /// <summary>
    /// The end of a weekly review run (#312): the counts, or that it failed and why, and the
    /// review page. One message, never edited, and never a row on the board.
    /// </summary>
    /// <remarks>
    /// The headline is Hephaisto's own words around counts. The sentence under it can carry a
    /// problem - GitHub's answer, a Job's - so it is a <see cref="Plain"/> run, never markdown.
    /// The button opens the page and decides nothing: every decision there is a person's click.
    /// </remarks>
    public static JsonObject ReviewEvent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var s = message.Snapshot;
        var headline = string.IsNullOrWhiteSpace(s.Title) ? "Weekly review" : s.Title;

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = headline,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["color"] = SeverityColour(s.Severity),
                ["wrap"] = true,
            },
        };

        if (ModelText(s.Reason, 1_200) is { Length: > 0 } reason)
        {
            body.Add(Plain(reason, subtle: true));
        }

        body.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = Stamp(s.At),
            ["isSubtle"] = true,
            ["wrap"] = true,
        });

        var card = Card(body);

        if (!string.IsNullOrWhiteSpace(message.ReviewUrl))
        {
            card["actions"] = new JsonArray { OpenUrl("Open the weekly review", message.ReviewUrl) };
        }

        return Activity(card, summary: headline);
    }

    /// <summary>
    /// Identifies a card's content, so an unchanged one is not sent again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over the serialised card rather than over the incidents: what matters is whether Teams
    /// would show something different, and a change to this file is such a difference.
    /// </para>
    /// <para>
    /// <b>Over the card only, not the activity around it.</b> The <c>summary</c> beside the card
    /// is what a phone announces when the message arrives, and it names the event that caused
    /// the alert. Counting it would make the first comparison after every alert find a
    /// difference and edit a card whose content had not changed.
    /// </para>
    /// </remarks>
    public static string Hash(JsonObject activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var card = activity["attachments"]?[0]?["content"]?.ToJsonString() ?? activity.ToJsonString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(card)));
    }

    /// <summary>The same activity, announced differently on a lock screen.</summary>
    public static JsonObject WithSummary(JsonObject activity, string summary)
    {
        ArgumentNullException.ThrowIfNull(activity);

        activity["summary"] = summary;

        return activity;
    }

    /// <summary>
    /// The state in words, always. Somebody reading this on a lock screen has nothing else.
    /// </summary>
    internal static string Headline(TeamsIncident incident) => incident.State switch
    {
        IncidentState.Escalated when incident.EscalationReason
            is EscalationReason.VerificationFailed
            or EscalationReason.RollbackPerformed
            or EscalationReason.Quarantined => "Verification failed - the fix did not hold",
        IncidentState.Escalated when incident.CodeFix is CodeFixState.PlanReady =>
            "Code fix planned - a plan is waiting for a developer",
        IncidentState.Escalated when incident.CodeFix is CodeFixState.PrOpened =>
            "Pull request opened - a code fix is ready for review",
        IncidentState.Escalated when incident.EscalationReason is EscalationReason.NotInvestigated =>
            "Opened - this rule is not investigated; it is yours",
        IncidentState.Escalated when incident.EscalationReason is EscalationReason.Flapping =>
            "Flapping - this alert keeps coming back",
        IncidentState.Escalated => "Escalated - Hephaisto needs a human",
        IncidentState.AwaitingApproval => "Approval required - an action is waiting",
        IncidentState.Resolved => "Resolved - Hephaisto fixed it",
        // The alert stopped firing, which is not the same as anybody fixing it - least of all
        // the agent. Saying "Resolved" here would credit it with a fix it never made (#129).
        IncidentState.Closed when incident.ClosedBy == Hephaisto.Core.IncidentStateMachine.AlertmanagerActor =>
            "Cleared - the alert stopped firing",
        IncidentState.Closed when incident.ClosedBy == Hephaisto.Core.IncidentStateMachine.WatcherActor =>
            "Cleared - the workload is healthy again",
        IncidentState.Closed when !string.IsNullOrWhiteSpace(incident.ClosedBy) => $"Closed by {ActorDisplay.Render(incident.ClosedBy, incident.ClosedClaimedBy)}",
        IncidentState.Closed => "Closed",
        IncidentState.Expired => "Expired - the signal stopped and nobody answered",
        IncidentState.Suppressed => "Suppressed",
        _ => $"In progress - {StateWord(incident.State)}",
    };

    private static JsonObject BoardRow(TeamsIncident incident, TeamsCardLinks links)
    {
        var line = new List<string>();

        if (!string.IsNullOrWhiteSpace(incident.Target))
        {
            line.Add(incident.Target);
        }

        line.Add($"since {Stamp(incident.OpenedAt)}");

        if (!string.IsNullOrWhiteSpace(incident.AssignedTo))
        {
            line.Add($"assigned to {incident.AssignedTo}");
        }
        else if (!string.IsNullOrWhiteSpace(incident.AcknowledgedBy))
        {
            line.Add($"acknowledged by {ActorDisplay.Render(incident.AcknowledgedBy, incident.AcknowledgedClaimedBy)}");
        }

        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("auto", new JsonObject
                    {
                        ["type"] = "TextBlock",
                        ["text"] = incident.Severity.ToString(),
                        ["weight"] = "Bolder",
                        ["color"] = SeverityColour(incident.Severity),
                    }),
                    Column("stretch", new JsonObject
                    {
                        ["type"] = "TextBlock",
                        ["text"] = Title(incident),
                        ["weight"] = "Bolder",
                        ["wrap"] = true,
                    }),
                    Column("auto", new JsonObject
                    {
                        ["type"] = "TextBlock",
                        ["text"] = StateWord(incident.State),
                        ["isSubtle"] = true,
                    }),
                },
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = string.Join(" - ", line),
                ["isSubtle"] = true,
                ["spacing"] = "None",
                ["wrap"] = true,
            },
        };

        if (CodeFixLine(incident) is { } codeFix)
        {
            items.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = codeFix,
                ["spacing"] = "None",
                ["wrap"] = true,
            });
        }

        if (incident.Diagnosis is { Grounded: true } found)
        {
            items.Add(Plain(
                $"Diagnosis: {ModelText(found.Hypothesis, MaxBoardDiagnosisLength)}"
                + (found.Confidence is { } c ? string.Create(CultureInfo.InvariantCulture, $" ({c:0.00})") : string.Empty),
                spacing: "None"));
        }

        var actions = Links(incident, links);

        var details = new JsonArray();

        if (incident.Diagnosis is { } diagnosis)
        {
            details.Add(DiagnosisSection(diagnosis));
        }

        if (!string.IsNullOrWhiteSpace(incident.Summary))
        {
            details.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = incident.Summary.Length > MaxSummaryLength
                    ? incident.Summary[..MaxSummaryLength] + "..."
                    : incident.Summary,
                ["wrap"] = true,
            });
        }

        details.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = Facts(incident) });

        // Unfolds inside the card, in the client. It is not an action in the sense that matters:
        // nothing is sent anywhere.
        actions.Add(new JsonObject
        {
            ["type"] = "Action.ShowCard",
            ["title"] = "Details",
            ["card"] = new JsonObject { ["type"] = "AdaptiveCard", ["body"] = details },
        });

        items.Add(new JsonObject { ["type"] = "ActionSet", ["actions"] = actions });

        return new JsonObject
        {
            ["type"] = "Container",
            ["separator"] = true,
            ["spacing"] = "Medium",
            ["items"] = items,
        };
    }

    /// <summary>The investigation as a card section. Every model-written string goes through <see cref="Plain"/>.</summary>
    private static JsonObject DiagnosisSection(TeamsDiagnosis diagnosis)
    {
        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "Diagnosis",
                ["weight"] = "Bolder",
                ["wrap"] = true,
            },
        };

        if (!diagnosis.Grounded)
        {
            items.Add(Plain(
                $"No grounded diagnosis: the investigation ended {diagnosis.Termination} ({Investigator(diagnosis)}).",
                subtle: true));

            return Section(items);
        }

        items.Add(Plain(ModelText(diagnosis.Hypothesis, MaxHypothesisLength)));

        var meta = new List<string>();

        if (!string.IsNullOrWhiteSpace(diagnosis.Category))
        {
            meta.Add(ModelText(diagnosis.Category, 40));
        }

        if (diagnosis.Confidence is { } confidence)
        {
            meta.Add(string.Create(CultureInfo.InvariantCulture, $"confidence {confidence:0.00}"));
        }

        meta.Add($"investigated {Investigator(diagnosis)}");
        items.Add(Plain(string.Join(" - ", meta), subtle: true, spacing: "None"));

        if (!string.IsNullOrWhiteSpace(diagnosis.Summary))
        {
            items.Add(Plain(ModelText(diagnosis.Summary, MaxHypothesisLength), subtle: true));
        }

        var evidence = diagnosis.Evidence.Where(e => !string.IsNullOrWhiteSpace(e)).Take(MaxEvidenceShown).ToList();

        if (evidence.Count > 0)
        {
            items.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = diagnosis.Evidence.Count > evidence.Count
                    ? string.Create(CultureInfo.InvariantCulture, $"Evidence ({evidence.Count} of {diagnosis.Evidence.Count})")
                    : "Evidence",
                ["isSubtle"] = true,
                ["size"] = "Small",
                ["wrap"] = true,
            });

            foreach (var excerpt in evidence)
            {
                items.Add(Plain(ModelText(excerpt, MaxExcerptLength), monospace: true, spacing: "Small"));
            }
        }

        foreach (var code in diagnosis.CodeRefs.Take(MaxCodeRefsShown))
        {
            var at = code.Ref is { Length: > 0 } sha ? $" at {(sha.Length > 12 ? sha[..12] : sha)}" : string.Empty;
            var lines = code.EndLine is { } end ? string.Create(CultureInfo.InvariantCulture, $"{code.Line}-{end}") : code.Line.ToString(CultureInfo.InvariantCulture);

            items.Add(Plain(ModelText($"Code: {code.Path}:{lines}{at}", 300), monospace: true, spacing: "Small"));
        }

        return Section(items);
    }

    /// <summary>
    /// The actions waiting for a person, each named and each with its own Approve and Deny.
    /// </summary>
    /// <remarks>
    /// What is named is what the policy engine was asked about: the action's type, its target and
    /// its arguments. The target and the arguments were written by a model, so they are
    /// <see cref="Plain"/> text. The buttons carry the action's id and nothing about who may press
    /// them - the card is the same for everybody, and the route decides.
    /// </remarks>
    private static JsonObject ApprovalSection(TeamsIncident incident)
    {
        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "Waiting for approval",
                ["weight"] = "Bolder",
                ["wrap"] = true,
            },
        };

        foreach (var action in incident.PendingActions.Take(MaxApprovalsShown))
        {
            var what = string.IsNullOrWhiteSpace(action.Target)
                ? action.Type.ToString()
                : $"{action.Type} on {ModelText(action.Target, 200)}";

            if (!string.IsNullOrWhiteSpace(action.Arguments))
            {
                what += $" with {ModelText(action.Arguments, 200)}";
            }

            items.Add(Plain($"{what} - risk {action.Risk}"));

            items.Add(new JsonObject
            {
                ["type"] = "ActionSet",
                ["actions"] = new JsonArray
                {
                    Decide($"Approve {action.Type}", TeamsBotVerbs.Approve, "positive", incident.Id, action.Id),
                    Decide($"Deny {action.Type}", TeamsBotVerbs.Deny, "destructive", incident.Id, action.Id),
                },
            });
        }

        if (incident.PendingActions.Count > MaxApprovalsShown)
        {
            items.Add(Plain(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"and {incident.PendingActions.Count - MaxApprovalsShown} more, in Hephaisto."),
                subtle: true));
        }

        items.Add(Plain("Approve and Deny take the approver role. An approved action runs under your name.", subtle: true));

        return Section(items);
    }

    private static JsonObject Decide(string title, string verb, string style, Guid incidentId, Guid actionId)
    {
        var button = Execute(title, verb, incidentId);

        button["style"] = style;
        button["data"]![TeamsBotVerbs.ActionId] = actionId.ToString();

        return button;
    }

    private static JsonObject Section(JsonArray items) => new()
    {
        ["type"] = "Container",
        ["separator"] = true,
        ["spacing"] = "Medium",
        ["items"] = items,
    };

    private static string Investigator(TeamsDiagnosis diagnosis)
    {
        var model = string.IsNullOrWhiteSpace(diagnosis.ModelId) ? "unknown model" : diagnosis.ModelId;

        return diagnosis.Executor switch
        {
            Core.Investigations.InvestigationExecutors.Job => $"by Claude Code in a Job, {model}",
            Core.Investigations.InvestigationExecutors.JobFallback => $"in-process after the Job gave no answer, {model}",
            _ => $"in-process, {model}",
        };
    }

    /// <summary>
    /// Text a model wrote, as a card may show it: secrets redacted, control and direction characters
    /// removed, cut to <paramref name="max"/>.
    /// </summary>
    internal static string ModelText(string? text, int max)
    {
        var clean = UntrustedText.Clean(SecretRedactor.Redact(text ?? string.Empty)).Trim();

        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "...";
    }

    /// <summary>
    /// A <c>RichTextBlock</c> of one <c>TextRun</c>. Teams renders a TextRun's text as it is and never
    /// as markdown - which a <c>TextBlock</c> would, turning <c>[x](url)</c> in a log line a model
    /// quoted into a link on a card people trust.
    /// </summary>
    private static JsonObject Plain(string text, bool subtle = false, bool monospace = false, string? spacing = null)
    {
        var run = new JsonObject { ["type"] = "TextRun", ["text"] = text };

        if (subtle)
        {
            run["isSubtle"] = true;
        }

        if (monospace)
        {
            run["fontType"] = "Monospace";
        }

        var block = new JsonObject { ["type"] = "RichTextBlock", ["inlines"] = new JsonArray(run) };

        if (spacing is not null)
        {
            block["spacing"] = spacing;
        }

        return block;
    }

    private static JsonArray Links(TeamsIncident incident, TeamsCardLinks links)
    {
        var actions = new JsonArray();

        // For a code fix the PR is the subject, so it comes first. A human reviews, merges and
        // deploys it; nothing in a card approves or merges anything.
        if (incident.CodeFix is CodeFixState.PrOpened && !string.IsNullOrWhiteSpace(incident.PullRequestUrl))
        {
            actions.Add(OpenUrl("Open the pull request", incident.PullRequestUrl));
        }

        if (NotificationLinks.Incident(links.BaseUrl, incident.Id) is { } url)
        {
            actions.Add(incident switch
            {
                { State: IncidentState.AwaitingApproval } => OpenUrl("Review and approve in Hephaisto", url),
                { CodeFix: CodeFixState.PlanReady } => OpenUrl("Review the plan in Hephaisto", url + "#codefix"),
                _ => OpenUrl("Open in Hephaisto", url),
            });
        }

        if (!string.IsNullOrWhiteSpace(links.GrafanaUrl))
        {
            var from = incident.OpenedAt.AddMinutes(-30).ToUnixTimeMilliseconds();
            var to = incident.OpenedAt.AddMinutes(30).ToUnixTimeMilliseconds();

            actions.Add(OpenUrl(
                "Grafana",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{links.GrafanaUrl.TrimEnd('/')}/d/hephaisto/hephaisto?from={from}&to={to}")));
        }

        return actions;
    }

    private static JsonArray Facts(TeamsIncident incident)
    {
        var facts = new JsonArray();

        Fact(facts, "Severity", incident.Severity.ToString());
        Fact(facts, "State", StateWord(incident.State));
        Fact(facts, "Kind", incident.Kind.ToString());
        Fact(facts, "Target", incident.Target);

        if (incident.EscalationReason is not EscalationReason.None)
        {
            Fact(facts, "Why", incident.EscalationReason.ToString());
        }

        Fact(facts, "Code fix", CodeFixLine(incident));
        Fact(facts, "Assigned to", incident.AssignedTo);
        Fact(facts, "Since", Stamp(incident.OpenedAt));

        return facts;
    }

    private static string? CodeFixLine(TeamsIncident incident) => incident.CodeFix switch
    {
        null => null,
        CodeFixState.Eligible or CodeFixState.Planning => "Code fix: a plan is being written",
        CodeFixState.PlanReady => "Code fix: a plan is waiting for review",
        CodeFixState.Implementing => "Code fix: the approved plan is being implemented",
        CodeFixState.PrOpened => "Code fix: a pull request is open",
        CodeFixState.Failed => "Code fix: ended without a PR",
        CodeFixState.Denied => "Code fix: the plan was denied",
        CodeFixState.Expired => "Code fix: the plan expired unanswered",
        CodeFixState.Cancelled => "Code fix: cancelled",
        _ => null,
    };

    private static string StateWord(IncidentState state) => state switch
    {
        IncidentState.AwaitingApproval => "Awaiting approval",
        _ => state.ToString(),
    };

    private static string Title(TeamsIncident incident) =>
        string.IsNullOrWhiteSpace(incident.Title) ? "(no title)" : incident.Title;

    private static string SeverityColour(Severity severity) => severity switch
    {
        Severity.Critical => "Attention",
        Severity.Warning => "Warning",
        _ => "Default",
    };

    /// <summary>UTC and said so, because a board is read from more than one time zone.</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static JsonObject Column(string width, JsonObject item) => new()
    {
        ["type"] = "Column",
        ["width"] = width,
        ["items"] = new JsonArray(item),
    };

    /// <summary>
    /// A button Teams delivers to <c>POST /api/teams/messages</c> as an <c>adaptiveCard/action</c>
    /// invoke. The incident id is the only data the button itself carries; who clicked comes from
    /// the token and the team's roster, never from the card.
    /// </summary>
    private static JsonObject Execute(string title, string verb, Guid incidentId) => new()
    {
        ["type"] = "Action.Execute",
        ["title"] = title,
        ["verb"] = verb,
        ["data"] = new JsonObject { ["incidentId"] = incidentId.ToString() },
    };

    /// <summary>
    /// Close, as a card that unfolds in the client and asks why first.
    /// </summary>
    /// <remarks>
    /// <c>Action.ShowCard</c> sends nothing; the <c>Action.Execute</c> inside it does, and Teams
    /// merges the input's value into that button's data under the input's id. The reason is
    /// required in the card, where the person is told at once, and again by the route, because a
    /// card is not the only thing that can send an invoke.
    /// </remarks>
    private static JsonObject CloseCard(Guid incidentId) => new()
    {
        ["type"] = "Action.ShowCard",
        ["title"] = "Close",
        ["card"] = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["body"] = new JsonArray(new JsonObject
            {
                ["type"] = "Input.Text",
                ["id"] = TeamsBotVerbs.ReasonInput,
                ["label"] = "Why is this closed?",
                ["isRequired"] = true,
                ["errorMessage"] = "Give a reason: it is written to the incident.",
                ["isMultiline"] = true,
                ["maxLength"] = MaxReasonLength,
            }),
            ["actions"] = new JsonArray(Execute("Close the incident", TeamsBotVerbs.Close, incidentId)),
        },
    };

    private static JsonObject OpenUrl(string title, string url) => new()
    {
        ["type"] = "Action.OpenUrl",
        ["title"] = title,
        ["url"] = url,
    };

    private static void Fact(JsonArray facts, string title, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add(new JsonObject { ["title"] = title, ["value"] = value });
        }
    }

    private static JsonObject Card(JsonArray body) => new()
    {
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["type"] = "AdaptiveCard",

        // Pinned rather than left to the host: a card that silently degrades to plain text is
        // worse than one that is refused, because it looks delivered.
        ["version"] = CardVersion,
        ["body"] = body,
    };

    private static JsonObject Activity(JsonObject card, string? summary)
    {
        var activity = new JsonObject { ["type"] = "message" };

        if (!string.IsNullOrWhiteSpace(summary))
        {
            // What a phone shows on its lock screen. Without it Teams announces a card as
            // "Sent a card", which tells the person it woke nothing.
            activity["summary"] = summary;
        }

        activity["attachments"] = new JsonArray(
            new JsonObject
            {
                ["contentType"] = CardContentType,
                ["content"] = card,
            });

        return activity;
    }
}
