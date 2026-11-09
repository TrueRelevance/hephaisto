using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Microsoft Teams, through a Power Automate Workflows trigger posting an Adaptive Card.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the "Incoming Webhook" URL from every tutorial.</b> Microsoft retired Office 365
/// connectors in Teams and is switching them off; the supported path for an unattended poster is
/// a Workflows trigger, or a registered bot for anything interactive. The envelope below - a
/// <c>message</c> with one <c>attachments</c> entry of contentType
/// <c>application/vnd.microsoft.card.adaptive</c> - is what the "post a card" Workflows template
/// forwards. <b>Confirm the envelope and the card schema version against current Microsoft
/// documentation when touching this</b>, rather than against a blog post: it is the part of this
/// milestone most likely to have moved.
/// </para>
/// <para>
/// <b>The card carries a link, not an Approve button.</b> Teams' interactive paths go through
/// Power Automate or a registered bot, and both mean accepting inbound calls on a service whose
/// only current inbound route is deliberately unauthenticated and protected solely by a
/// NetworkPolicy. That is a security change rather than a feature increment. Linking out costs
/// nothing and skips a throwaway design, because the identity story converges anyway: approving
/// in Hephaisto's own UI makes the free-text ApprovedBy the weak point, and the fix for that is
/// OIDC - which for a Teams shop is Entra ID, the same directory the card was delivered through.
/// </para>
/// </remarks>
public sealed class TeamsNotificationChannel(
    HttpClient http,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<TeamsNotificationChannel> logger) : INotificationChannel
{
    /// <summary>
    /// Teams renders up to 1.5 across the surfaces that matter here. Pinned rather than left to
    /// the host: a card that silently degrades to plain text is worse than one that is refused,
    /// because it looks delivered.
    /// </summary>
    private const string CardVersion = "1.5";

    private static readonly JsonSerializerOptions Json = new();

    public string Name => NotificationChannelNames.Teams;

    public string Describe()
    {
        var url = options.CurrentValue.Teams.WorkflowUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            return "Teams channel is OFF: Notifications:Teams:WorkflowUrl is not set.";
        }

        return $"Teams channel is ON, posting to {Redact(url)}.";
    }

    /// <summary>
    /// Host only.
    /// </summary>
    /// <remarks>
    /// A Workflows trigger URL is a <b>bearer credential in a query string</b> - the <c>sig</c>
    /// parameter is the whole authentication. Logging the configured URL, which is what every
    /// other channel here can safely do, would write a live credential into the pod log and from
    /// there into whatever collects it. The host is enough to tell an operator it is pointed
    /// somewhere plausible.
    /// </remarks>
    private static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? $"{parsed.Scheme}://{parsed.Host} (path and signature redacted)"
            : "a configured URL (redacted)";

    public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var url = options.CurrentValue.Teams.WorkflowUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            return DeliveryResult.Permanent("Notifications:Teams:WorkflowUrl is not set");
        }

        var payload = BuildEnvelope(message);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(Json), System.Text.Encoding.UTF8, "application/json"),
        };

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Truncated, and it must never be logged with the URL beside it - a Workflows
            // failure body can echo the request.
            return DeliveryResult.FromStatus(
                response.StatusCode,
                text.Length > 200 ? text[..200] : text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately does NOT include the URL, which is the credential.
            logger.LogWarning(ex, "Teams delivery {DeliveryId} failed.", message.DeliveryId);

            return DeliveryResult.Retry($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The Workflows envelope, with the card inside it.</summary>
    internal static JsonObject BuildEnvelope(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = BuildCard(message),
                }),
        };
    }

    private static JsonObject BuildCard(NotificationMessage message)
    {
        var s = message.Snapshot;

        // A work item's and a weekly review's text can carry somebody else's words (#312: a
        // problem is GitHub's or a Job's answer), so they are shown as they are, never as markdown.
        var plain = s.WorkItemId is not null || s.Event is NotificationEvent.PlannerReviewFinished;

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = Headline(s.Event),
                ["weight"] = "Bolder",
                ["size"] = "Medium",

                // The card's only colour, and it is never the only signal: the headline says
                // the state in words. Colour is the third channel here, as it is in the console.
                ["color"] = s.Severity switch
                {
                    Severity.Critical => "Attention",
                    Severity.Warning => "Warning",
                    _ => "Default",
                },
                ["wrap"] = true,
            },
            Words(string.IsNullOrWhiteSpace(s.Title) ? "(no title)" : s.Title, plain: plain),
        };

        var facts = new JsonArray();

        if (s.WorkItemId is not null)
        {
            // An issue has no severity, kind or target; it has a reference and a repository.
            AddFact(facts, "Issue", s.Issue);
            AddFact(facts, "Repository", s.Repository);
        }
        else if (s.Event is not NotificationEvent.PlannerReviewFinished)
        {
            AddFact(facts, "Severity", s.Severity.ToString());
        }

        if (s.IncidentId is not null)
        {
            AddFact(facts, "Kind", s.Kind.ToString());
            AddFact(facts, "Target", s.Target);

            if (s.EscalationReason is not EscalationReason.None)
            {
                AddFact(facts, "Why", s.EscalationReason.ToString());
            }

            AddFact(facts, "Repository", s.Repository);
        }

        AddFact(facts, "When", s.At.ToString("u", System.Globalization.CultureInfo.InvariantCulture));

        if (facts.Count > 0)
        {
            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = facts });
        }

        if (!string.IsNullOrWhiteSpace(s.Summary))
        {
            body.Add(Words(s.Summary, plain: plain, subtle: true));
        }

        if (!string.IsNullOrWhiteSpace(s.Reason))
        {
            body.Add(Words(s.Reason, plain: plain, subtle: true));
        }

        if (message.AlsoSuppressed > 0)
        {
            // The suppressed count rides on the card that does go out, so a storm is visible
            // where somebody is already looking rather than only in a metric they would have to
            // go and find.
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"{message.AlsoSuppressed} further notification(s) about this workload "
                    + "were suppressed by the outbound rate limit.",
                ["wrap"] = true,
                ["isSubtle"] = true,
            });
        }

        var actions = new JsonArray();

        // For a code fix the PR IS the subject, so it comes first. A human reviews, merges and
        // deploys it; nothing in the card approves or merges anything.
        if (s.Event is NotificationEvent.CodeFixPrOpened && !string.IsNullOrWhiteSpace(s.ExternalUrl))
        {
            actions.Add(new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Open the pull request",
                ["url"] = s.ExternalUrl,
            });
        }

        // An incident's page, or - for a work item's attempt, which has none - the attempt's own.
        var console = message.IncidentUrl ?? message.CodeFixUrl ?? message.ReviewUrl;

        if (!string.IsNullOrWhiteSpace(console))
        {
            // Deliberately "Open" rather than "Approve". See the class remarks.
            actions.Add(new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = s.Event switch
                {
                    NotificationEvent.ApprovalRequired => "Review and approve in Hephaisto",
                    NotificationEvent.CodeFixPlanReady => "Review the plan in Hephaisto",
                    NotificationEvent.PlannerReviewFinished => "Open the weekly review",
                    _ => "Open in Hephaisto",
                },
                ["url"] = console,
            });
        }

        if (s.WorkItemId is not null && !string.IsNullOrWhiteSpace(s.IssueUrl))
        {
            actions.Add(new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Open the issue",
                ["url"] = s.IssueUrl,
            });
        }

        if (!string.IsNullOrWhiteSpace(message.GrafanaUrl))
        {
            actions.Add(new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Open in Grafana",
                ["url"] = message.GrafanaUrl,
            });
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = CardVersion,
            ["body"] = body,
        };

        if (actions.Count > 0)
        {
            card["actions"] = actions;
        }

        return card;
    }

    /// <summary>
    /// The state in words, always. Somebody reading this on a phone at 3am should not have to
    /// work out what happened from a colour or from the body text.
    /// </summary>
    private static string Headline(NotificationEvent kind) => kind switch
    {
        NotificationEvent.IncidentEscalated => "Escalated - Hephaisto needs a human",
        NotificationEvent.ApprovalRequired => "Approval required - an action is waiting",
        NotificationEvent.IncidentResolved => "Resolved - Hephaisto fixed it",
        NotificationEvent.VerificationFailed => "Verification failed - the fix did not hold",
        NotificationEvent.ModeChanged => "Autonomy re-armed",
        NotificationEvent.PolicyChanged => "Policy configuration changed",
        NotificationEvent.CodeFixPlanReady => "Code fix planned - a plan is waiting for a developer",
        NotificationEvent.CodeFixPrOpened => "Pull request opened - a code fix is ready for review",
        NotificationEvent.CodeFixFailed => "Code fix ended without a PR",
        NotificationEvent.PlannerReviewFinished => "Weekly review - verdicts on the open issues wait for a person",
        _ => "Hephaisto",
    };

    /// <summary>
    /// One paragraph of a card. <paramref name="plain"/> for a work item's (v0.14.0): the title
    /// is whatever somebody typed into an issue and the summary a model's, and a
    /// <c>TextBlock</c> renders a markdown subset - <c>[x](address)</c> becomes a link on a card
    /// people trust. A <c>RichTextBlock</c> of one <c>TextRun</c> is shown as it is. An
    /// incident's paragraphs stay the TextBlocks they have been since v0.2.0.
    /// </summary>
    private static JsonObject Words(string text, bool plain, bool subtle = false)
    {
        if (!plain)
        {
            var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };

            if (subtle)
            {
                block["isSubtle"] = true;
            }

            return block;
        }

        var run = new JsonObject { ["type"] = "TextRun", ["text"] = text };

        if (subtle)
        {
            run["isSubtle"] = true;
        }

        return new JsonObject { ["type"] = "RichTextBlock", ["inlines"] = new JsonArray(run) };
    }

    private static void AddFact(JsonArray facts, string title, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add(new JsonObject { ["title"] = title, ["value"] = value });
        }
    }
}
