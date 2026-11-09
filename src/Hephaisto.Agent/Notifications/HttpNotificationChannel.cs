using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// The generic outbound HTTP channel: POSTs the message as JSON to a configured URL.
/// </summary>
/// <remarks>
/// <para>
/// Built first, and it is the one channel with no third-party account behind it - so the outbox,
/// the routing and the rate limit are all proven against a local receiver before any
/// vendor-shaped payload exists. It is also the escape hatch for anybody using something this
/// milestone does not ship.
/// </para>
/// <para>
/// <b>Not called "the webhook channel" in code.</b> In this repository a webhook is inbound: the
/// <c>/webhooks</c> route group Alertmanager posts to, whose NetworkPolicy is its entire
/// authentication. Reusing the word for the opposite direction is how somebody later reads a
/// security note about one and applies it to the other.
/// </para>
/// </remarks>
public sealed class HttpNotificationChannel(
    HttpClient http,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<HttpNotificationChannel> logger) : INotificationChannel
{
    /// <summary>
    /// Written by us over the exact bytes on the wire, so a receiver can prove the request came
    /// from this agent. <c>sha256=&lt;hex&gt;</c>, the shape most receivers already understand.
    /// </summary>
    public const string SignatureHeader = "X-Hephaisto-Signature";

    /// <summary>Stable across retries, so a receiver can dedupe an at-least-once delivery.</summary>
    public const string DeliveryHeader = "X-Hephaisto-Delivery-Id";

    public const string EventHeader = "X-Hephaisto-Event";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name => NotificationChannelNames.Webhook;

    public string Describe()
    {
        var o = options.CurrentValue.Webhook;

        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return "Outbound webhook channel is OFF: Notifications:Webhook:Url is not set.";
        }

        var signed = string.IsNullOrWhiteSpace(o.SigningSecret)
            ? "UNSIGNED - the receiver cannot tell this came from Hephaisto"
            : "signed";

        // The URL is a plain configured address here, not a credential - unlike the Teams one.
        return $"Outbound webhook channel is ON, posting to {o.Url} ({signed}).";
    }

    public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var o = options.CurrentValue.Webhook;

        if (string.IsNullOrWhiteSpace(o.Url))
        {
            // Permanent: the dispatcher should stop rather than retry a channel that is not
            // configured, and the row is the record that it was routed here.
            return DeliveryResult.Permanent("Notifications:Webhook:Url is not set");
        }

        // Serialised once, to bytes, because the signature has to cover exactly what is sent.
        // Re-serialising for the HMAC is how a signature ends up correct in a unit test and
        // wrong on the wire.
        var body = JsonSerializer.SerializeToUtf8Bytes(Payload.From(message), Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, o.Url)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation(DeliveryHeader, message.DeliveryId.ToString());
        request.Headers.TryAddWithoutValidation(EventHeader, message.Snapshot.Event.ToString());

        if (!string.IsNullOrWhiteSpace(o.SigningSecret))
        {
            request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(body, o.SigningSecret));
        }

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

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
            // Including the per-call timeout, which surfaces here because the linked source
            // cancelled without ct being cancelled. Retryable: a transport failure is the exact
            // condition the outbox exists to outlive.
            logger.LogWarning(ex, "Outbound webhook delivery {DeliveryId} failed.", message.DeliveryId);

            return DeliveryResult.Retry($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>HMAC-SHA256 over the request body, lowercase hex, <c>sha256=</c> prefixed.</summary>
    public static string Sign(byte[] body, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        return $"sha256={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>
    /// The wire format. Explicit rather than serialising the domain records directly, so a
    /// rename inside <c>Hephaisto.Core</c> cannot silently change somebody else's integration.
    /// </summary>
    private sealed record Payload
    {
        [JsonPropertyName("deliveryId")]
        public required Guid DeliveryId { get; init; }

        [JsonPropertyName("event")]
        public required string Event { get; init; }

        [JsonPropertyName("at")]
        public required DateTimeOffset At { get; init; }

        [JsonPropertyName("incident")]
        public IncidentPayload? Incident { get; init; }

        [JsonPropertyName("links")]
        public LinksPayload? Links { get; init; }

        /// <summary>How many further messages the cooldown swallowed. Zero is the normal case.</summary>
        [JsonPropertyName("alsoSuppressed")]
        public int AlsoSuppressed { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("severity")]
        public required string Severity { get; init; }

        [JsonPropertyName("codeFix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CodeFixPayload? CodeFix { get; init; }

        /// <summary>
        /// The issue a code-fix event is about, when it is about a work item and not an incident
        /// (v0.14.0). <c>incident</c> is then absent, and <c>severity</c> is <c>Info</c>: an
        /// issue has none.
        /// </summary>
        [JsonPropertyName("workItem")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkItemPayload? WorkItem { get; init; }

        public static Payload From(NotificationMessage message)
        {
            var s = message.Snapshot;

            return new Payload
            {
                DeliveryId = message.DeliveryId,
                Event = s.Event.ToString(),
                At = s.At,
                Title = s.Title,
                Reason = s.Reason,
                Severity = s.Severity.ToString(),
                AlsoSuppressed = message.AlsoSuppressed,

                // Absent for the two events that are about the agent rather than an incident,
                // rather than present and full of empty strings.
                Incident = s.IncidentId is not { } id
                    ? null
                    : new IncidentPayload
                    {
                        Id = id,
                        Kind = s.Kind.ToString(),
                        State = s.State?.ToString(),
                        PreviousState = s.PreviousState?.ToString(),
                        EscalationReason = s.EscalationReason.ToString(),
                        Namespace = s.Namespace,
                        Cluster = s.Cluster,
                        Target = s.Target,
                        Summary = s.Summary,
                        CorrelationKey = s.CorrelationKey,
                    },

                CodeFix = s.CodeFixAttemptId is not { } attempt
                    ? null
                    : new CodeFixPayload { AttemptId = attempt, Repository = s.Repository, PrUrl = s.ExternalUrl },

                WorkItem = s.WorkItemId is not { } workItem
                    ? null
                    : new WorkItemPayload { Id = workItem, Issue = s.Issue, Url = s.IssueUrl, Summary = s.Summary },

                Links = message.IncidentUrl is null && message.GrafanaUrl is null && message.CodeFixUrl is null && message.ReviewUrl is null
                    ? null
                    : new LinksPayload { Incident = message.IncidentUrl, CodeFix = message.CodeFixUrl, Grafana = message.GrafanaUrl, Review = message.ReviewUrl },
            };
        }
    }

    private sealed record WorkItemPayload
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        /// <summary><c>owner/repo#12</c>.</summary>
        [JsonPropertyName("issue")]
        public string? Issue { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        /// <summary>The plan's summary - a model's words, as <c>incident.summary</c> is.</summary>
        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }

    private sealed record CodeFixPayload
    {
        [JsonPropertyName("attemptId")]
        public required Guid AttemptId { get; init; }

        [JsonPropertyName("repository")]
        public string? Repository { get; init; }

        [JsonPropertyName("prUrl")]
        public string? PrUrl { get; init; }
    }

    private sealed record IncidentPayload
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("previousState")]
        public string? PreviousState { get; init; }

        [JsonPropertyName("escalationReason")]
        public required string EscalationReason { get; init; }

        [JsonPropertyName("namespace")]
        public required string Namespace { get; init; }

        // Empty for an incident from before v0.10.0, which is the agent's own cluster (#131).
        [JsonPropertyName("cluster")]
        public required string Cluster { get; init; }

        [JsonPropertyName("target")]
        public required string Target { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("correlationKey")]
        public required string CorrelationKey { get; init; }
    }

    private sealed record LinksPayload
    {
        [JsonPropertyName("incident")]
        public string? Incident { get; init; }

        /// <summary>The attempt's own console page, for a work item's code-fix event; absent otherwise.</summary>
        [JsonPropertyName("codeFix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CodeFix { get; init; }

        [JsonPropertyName("grafana")]
        public string? Grafana { get; init; }

        /// <summary>The weekly review's page, for <c>PlannerReviewFinished</c> (#312); absent otherwise.</summary>
        [JsonPropertyName("review")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Review { get; init; }
    }
}
