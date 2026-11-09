namespace Hephaisto.Core.Notifications;

/// <summary>
/// What a human might need to be told about, as a closed vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// Closed on purpose. This enum is a metric label, a routing key and a persisted column, and
/// each of those breaks differently if the set is open: unbounded label cardinality, a routing
/// rule that silently matches nothing, and a stored value no later version can interpret.
/// </para>
/// <para>
/// The list is deliberately short. Every member is something a person would want to be woken
/// for, or would be angry to discover happened without them. "An investigation started" is
/// neither, which is why it is not here even though the in-process
/// <c>IncidentLiveEventKind</c> carries it - a UI nudge and a page are different products.
/// </para>
/// </remarks>
public enum NotificationEvent
{
    /// <summary>
    /// Not a real event, and never routed. It takes the zero value so a default-constructed
    /// row cannot claim to be an escalation - the mistake recorded as backlog #38, where
    /// <c>ApprovalSource</c>'s zero was <c>Ui</c> and actions nobody ever saw read as though a
    /// human had approved them.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The agent gave up and a human is needed. The reason this milestone exists: before it,
    /// this reached nobody unless a browser tab happened to be open.
    /// </summary>
    IncidentEscalated = 1,

    /// <summary>
    /// An action is proposed and waits on a person. The message carries the deep link into the
    /// approval UI, which is the whole payload - see the note on why v1 does not approve in-card.
    /// </summary>
    ApprovalRequired = 2,

    /// <summary>
    /// The agent fixed something unattended and the verifier agreed. Worth telling people about
    /// precisely because nobody was involved.
    /// </summary>
    IncidentResolved = 3,

    /// <summary>
    /// An executed action did not hold, and was rolled back, quarantined, or escalated. Distinct
    /// from <see cref="IncidentEscalated"/> because "the agent tried and was wrong" is a
    /// different thing to learn than "the agent declined to try".
    /// </summary>
    VerificationFailed = 4,

    /// <summary>
    /// Autonomy came back after a runaway latch was cleared. Arguably the single most important
    /// event in the system to be able to attribute, and until v0.2.0 nothing wrote it at all.
    /// </summary>
    ModeChanged = 5,

    /// <summary>
    /// The hot-reloaded policy configuration moved. A silent policy change is indistinguishable
    /// from an attack, which is why this is notifiable and not merely audited.
    /// </summary>
    PolicyChanged = 6,

    /// <summary>
    /// A coder has written a fix plan and it waits on a person. Routed to developers rather than
    /// on-call: the decision is "is this the right change to a repository", not "is the cluster ok".
    /// </summary>
    CodeFixPlanReady = 7,

    /// <summary>A pull request exists. Carries its URL; a human reviews, merges and deploys.</summary>
    CodeFixPrOpened = 8,

    /// <summary>
    /// A code fix ended without a PR - failed, cancelled or expired. Silence would read as "still
    /// working", which is the one wrong conclusion to leave someone with.
    /// </summary>
    CodeFixFailed = 9,

    /// <summary>
    /// Triage finished: the incident opened - or reopened - and is about to be investigated, or
    /// was escalated without one. The first thing a person hears (#133), sent before the model
    /// has said anything, because as the pager the delay between a fault and the first person to
    /// hear of it must not be set by a model.
    /// </summary>
    IncidentOpened = 10,

    /// <summary>
    /// Nobody acknowledged the incident within one of its route's steps, so the step tells
    /// somebody else (#142). A new message every time, and exempt from the per-workload cooldown:
    /// it exists precisely because the first message was not enough.
    /// </summary>
    IncidentUnanswered = 11,

    /// <summary>
    /// An open incident's severity rose - a warning that turned critical - and a route that wants
    /// the new severity was not told before (#148). Delivered to the routes that carry
    /// <see cref="IncidentOpened"/>, because for them this is the incident opening at the
    /// severity they care about; and exempt from the cooldown, which would otherwise swallow it
    /// behind the opening a minute earlier.
    /// </summary>
    SeverityRaised = 12,

    /// <summary>
    /// A run of the planner's weekly review ended (#312): what it found, as counts, and a link to
    /// the review page - or that it failed, and why. One new message per run, never an edit: the
    /// verdicts wait for a person. Like an event about the agent it has no incident, namespace,
    /// cluster, kind or label, so an unscoped route that lists it takes it.
    /// </summary>
    PlannerReviewFinished = 13,
}
