using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Telemetry;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Drains the outbox: picks up due rows, applies the outbound rate limit, hands each to its
/// channel, and records what happened.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>VerificationScheduler</c>, deliberately - prime once before the first
/// delay, then a <c>PeriodicTimer</c>, a scope per tick, a bounded read off an index, and a
/// catch-all so the loop outlives a bad tick. Priming matters here for the reason it matters
/// there: the deliveries that came due while the process was down are the ones most worth
/// sending promptly, because something restarted the pod and somebody has not been told.
/// </para>
/// <para>
/// <b>This is the only retry authority.</b> Channels do not retry, and their HTTP clients opt out
/// of the standard resilience handler, because only the outbox survives a restart and stacking
/// the two would multiply every attempt.
/// </para>
/// </remarks>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly IClock clock;
    private readonly IOptionsMonitor<NotificationOptions> options;
    private readonly HephaistoMetrics metrics;
    private readonly ILogger<NotificationDispatcher> logger;
    private readonly Meter meter;

    /// <summary>
    /// Read by the observable gauge's synchronous callback, written once per tick. Same split as
    /// <c>BudgetGaugePublisher</c>: the value behind the gauge is a database read, and a gauge
    /// callback may not await.
    /// </summary>
    private int pending;

    public NotificationDispatcher(
        IServiceScopeFactory scopes,
        IClock clock,
        IOptionsMonitor<NotificationOptions> options,
        HephaistoMetrics metrics,
        IMeterFactory meterFactory,
        ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        this.scopes = scopes;
        this.clock = clock;
        this.options = options;
        this.metrics = metrics;
        this.logger = logger;

        meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        // Visibility only. Nothing may come to depend on this value - the decision to send is
        // made from the row, never from the gauge.
        meter.CreateObservableGauge(
            HephaistoTelemetry.Metrics.NotificationsPending,
            () => pending,
            unit: "{delivery}",
            description:
                "Outbox rows still pending. A number that climbs and does not come back down is "
                + "a backlog of people who have not been told yet.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.CurrentValue.DispatchInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            var o = options.CurrentValue;

            await using var scope = scopes.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<INotificationOutbox>();

            var channels = scope.ServiceProvider
                .GetServices<INotificationChannel>()
                .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            var now = clock.UtcNow;
            var due = await outbox.DueAsync(o.DispatchBatchSize, now, ct).ConfigureAwait(false);

            pending = await db.NotificationDeliveries
                .CountAsync(d => d.Status == DeliveryStatus.Pending, ct)
                .ConfigureAwait(false);

            foreach (var delivery in due)
            {
                await SendOneAsync(delivery, channels, scope.ServiceProvider, outbox, o, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // The loop must outlive any single bad tick, or one malformed row stops every
            // notification the system would otherwise have sent.
            logger.LogError(ex, "The notification dispatcher's poll failed; it will try again.");
        }
    }

    private async Task SendOneAsync(
        NotificationDelivery delivery,
        Dictionary<string, INotificationChannel> channels,
        IServiceProvider sp,
        INotificationOutbox outbox,
        NotificationOptions o,
        CancellationToken ct)
    {
        if (!channels.TryGetValue(delivery.Channel, out var channel))
        {
            // Startup validation refuses a route naming an unregistered channel, so this means
            // the routing table was hot-reloaded into that state. Terminal rather than retried:
            // waiting will not conjure the channel, and the row is the evidence.
            await FailAsync(
                delivery,
                outbox,
                sp,
                $"no channel named '{delivery.Channel}' is registered",
                ct).ConfigureAwait(false);

            return;
        }

        var now = clock.UtcNow;

        var budget = await outbox
            .BudgetAsync(delivery.Channel, delivery.CorrelationKey, delivery.Event, now, ct)
            .ConfigureAwait(false);

        var rate = NotificationRateLimit.Evaluate(
            delivery.CorrelationKey,
            budget.LastDeliveryForKey,
            budget.DeliveredOnChannelLastHour,
            now,
            o,
            delivery.Event);

        if (rate.IsSuppressed)
        {
            // Recorded, not discarded - and deliberately NOT audited. A storm is exactly when
            // this fires, and an audit row per suppression would reproduce the amplification the
            // limit exists to prevent, one table over. The row and the metric are the record.
            await outbox.MarkSuppressedAsync(delivery, rate.Reason, ct).ConfigureAwait(false);
            metrics.NotificationDelivered(delivery.Channel, DeliveryStatus.Suppressed);

            // #150. The cooldown only ever holds back a repeat of something already said. The
            // hourly cap can hold back the FIRST word about an incident, or the step that exists
            // because nobody answered - and that is a person not being told, at Information.
            if (rate.Exceeded is NotificationLimit.ChannelHour
                && delivery.Event is NotificationEvent.IncidentOpened
                    or NotificationEvent.IncidentUnanswered
                    or NotificationEvent.IncidentEscalated
                    or NotificationEvent.SeverityRaised)
            {
                logger.LogError(
                    "Nobody was told: a {Event} for incident {IncidentId} on {Channel} was suppressed by the hourly cap ({Reason}). "
                        + "Raise notifications.maxPerChannelPerHour if this is not a storm.",
                    delivery.Event,
                    delivery.IncidentId,
                    delivery.Channel,
                    rate.Reason);
            }
            else
            {
                logger.LogInformation(
                    "Suppressed a {Event} on {Channel}: {Reason}",
                    delivery.Event,
                    delivery.Channel,
                    rate.Reason);
            }

            return;
        }

        var links = NotificationLinks.For(o.BaseUrl, o.GrafanaUrl, delivery.IncidentId, delivery.Snapshot);

        var message = new NotificationMessage
        {
            Snapshot = delivery.Snapshot,
            DeliveryId = delivery.Id,
            IncidentUrl = links.Incident,
            CodeFixUrl = links.CodeFix,
            ReviewUrl = NotificationLinks.Review(o.BaseUrl, delivery.Snapshot),
            GrafanaUrl = links.Grafana,
            AlsoSuppressed = budget.SuppressedSinceLastDelivery,
            Recipients = delivery.Recipients,
            UsesChannelRecipients = delivery.UsesChannelRecipients,
        };

        using var activity = HephaistoMetrics.ActivitySource.StartActivity(
            HephaistoTelemetry.Spans.NotificationDeliver,
            ActivityKind.Client);

        activity?.SetTag("hephaisto.notification.channel", delivery.Channel);
        activity?.SetTag("hephaisto.notification.event", delivery.Event.ToString());
        activity?.SetTag("hephaisto.notification.delivery_id", delivery.Id);
        activity?.SetTag("hephaisto.notification.attempt", delivery.AttemptCount + 1);

        DeliveryResult result;

        try
        {
            // Its own timeout rather than the handler's, for the reason GrafanaAnnotator gives:
            // an endpoint that accepts connections and never answers must not hold the loop.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.SendTimeout);

            result = await channel.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The process is stopping. Leave the row pending - that is what it is for.
            throw;
        }
        catch (Exception ex)
        {
            // A channel is not supposed to throw. If one does, it is this loop's problem to
            // contain rather than the other channels' problem to inherit.
            result = DeliveryResult.Retry($"{ex.GetType().Name}: {ex.Message}");
        }

        activity?.SetTag("hephaisto.notification.disposition", result.Disposition.ToString());

        switch (result.Disposition)
        {
            case DeliveryDisposition.Delivered:
                await outbox.MarkDeliveredAsync(delivery, ct).ConfigureAwait(false);
                metrics.NotificationDelivered(delivery.Channel, DeliveryStatus.Delivered);
                metrics.NotificationLatency(delivery.Channel, clock.UtcNow - delivery.CreatedAt);
                break;

            case DeliveryDisposition.Retryable
                when NotificationBackoff.HasAttemptsLeft(delivery.AttemptCount + 1, o):

                var delay = NotificationBackoff.Delay(
                    delivery.AttemptCount + 1,
                    o,
                    Random.Shared.NextDouble());

                await outbox
                    .RetryLaterAsync(delivery, result.Detail ?? "retryable failure", clock.UtcNow + delay, ct)
                    .ConfigureAwait(false);

                logger.LogWarning(
                    "Delivery {DeliveryId} to {Channel} failed and will be retried in {Delay}: {Detail}",
                    delivery.Id,
                    delivery.Channel,
                    delay,
                    result.Detail);
                break;

            default:
                await FailAsync(
                    delivery,
                    outbox,
                    sp,
                    result.Detail ?? "delivery failed",
                    ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Terminal failure. <b>Loud</b>: this is the moment a human was not reached, which is the
    /// worst outcome the system produces and the one nobody is looking at a console to notice.
    /// </summary>
    private async Task FailAsync(
        NotificationDelivery delivery,
        INotificationOutbox outbox,
        IServiceProvider sp,
        string detail,
        CancellationToken ct)
    {
        await outbox.MarkFailedAsync(delivery, detail, ct).ConfigureAwait(false);
        metrics.NotificationDelivered(delivery.Channel, DeliveryStatus.Failed);

        logger.LogError(
            "Gave up delivering {Event} for incident {IncidentId} to {Channel} after {Attempts} attempts. "
                + "NOBODY WAS TOLD. Last error: {Detail}",
            delivery.Event,
            delivery.IncidentId,
            delivery.Channel,
            delivery.AttemptCount,
            detail);

        try
        {
            await sp.GetRequiredService<IAuditRepository>()
                .AppendAsync(
                    new AuditEvent
                    {
                        At = clock.UtcNow,
                        Type = "notification.failed",
                        IncidentId = delivery.IncidentId,
                        Actor = "hephaisto/notifier",
                        Summary = $"could not deliver {delivery.Event} to {delivery.Channel}",
                        Detail = JsonSerializer.Serialize(
                            new
                            {
                                deliveryId = delivery.Id,
                                channel = delivery.Channel,
                                attempts = delivery.AttemptCount,
                                lastError = detail,
                            },
                            AuditJson),
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the failed delivery in the audit trail.");
        }
    }

    public override void Dispose()
    {
        meter.Dispose();
        base.Dispose();
    }

    private static readonly JsonSerializerOptions AuditJson = new();
}
