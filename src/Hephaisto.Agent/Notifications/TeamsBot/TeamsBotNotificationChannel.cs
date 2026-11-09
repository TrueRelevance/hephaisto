using System.Globalization;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// An alert, as a personal chat message from the bot to each configured person.
/// </summary>
/// <remarks>
/// <para>
/// <b>A personal chat, not the channel.</b> The channel holds one message, the board, which is
/// only ever edited - and an edit rings no phone. Something that needs a person therefore has to
/// be a new message somewhere, and a person's own chat with the bot is the one place where a new
/// message per alert is a history rather than a flood.
/// </para>
/// <para>
/// <b>The card shows the incident as it is now, not as the event found it.</b> This is the one
/// channel that departs from the frozen snapshot, because its cards go on being edited: the
/// reconciler compares every live alert with its incident, and a card rendered from a snapshot
/// would differ on the first comparison and be edited for no visible reason. What the event
/// contributes is the line a lock screen shows, and the decision to post at all.
/// </para>
/// <para>
/// A second alert for an incident is a new message, and the older one is shrunk to a line that
/// points at it. Never deleted; see <see cref="ITeamsBotClient"/>.
/// </para>
/// <para>
/// <b>It takes a scope factory, not a database context.</b> A channel is held for the life of the
/// process by <c>NotificationChannelProbe</c>, which is a singleton, so a channel that took a
/// context directly would either fail the container's scope validation - which is how this was
/// found, on the dev cluster, by an agent that would not start - or, with validation off, keep
/// one context alive for ever. Each send opens its own.
/// </para>
/// </remarks>
public sealed class TeamsBotNotificationChannel(
    ITeamsBotClient client,
    IServiceScopeFactory scopes,
    IClock clock,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<TeamsBotNotificationChannel> logger) : INotificationChannel
{
    public string Name => NotificationChannelNames.TeamsBot;

    public string Describe()
    {
        var bot = options.CurrentValue.TeamsBot;

        if (!bot.IsConfigured)
        {
            return "Teams bot channel is OFF: Notifications:TeamsBot needs TenantId, AppId, ClientSecret and ChannelId.";
        }

        // The app id is printed in the app manifest and is not a credential. The secret is, and
        // is not mentioned at all - not even that it is set, beyond the channel being on.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Teams bot channel is ON as app {bot.AppId}: one board in the configured channel, "
                + $"alerts to {bot.Recipients.Count} recipient(s) by personal chat. It edits and never deletes.");
    }

    public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var o = options.CurrentValue;
        var bot = o.TeamsBot;

        if (!bot.IsConfigured)
        {
            return DeliveryResult.Permanent("Notifications:TeamsBot is not configured");
        }

        // The routes' recipients, and the bot's own list when a matching route named nobody - which
        // is every route written before routes could name anybody (#123).
        var recipients = message.Recipients
            .Concat(message.UsesChannelRecipients ? bot.Recipients : [])
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            // Terminal and loud rather than "delivered to nobody": a route to a channel with no
            // one behind it looks exactly like one that works.
            return DeliveryResult.Permanent("Notifications:TeamsBot:Recipients is empty, so there is nobody to tell");
        }

        await using var scope = scopes.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
        var incidents = scope.ServiceProvider.GetRequiredService<TeamsBotIncidents>();

        var (card, incidentId) = await RenderAsync(db, incidents, message, o, ct).ConfigureAwait(false);
        var hash = TeamsBotCards.Hash(card);

        var told = 0;
        string? retryable = null;
        string? refused = null;

        foreach (var recipient in recipients)
        {
            var already = await db.TeamsBotMessages
                .AnyAsync(m => m.DeliveryId == message.DeliveryId && m.Recipient == recipient, ct)
                .ConfigureAwait(false);

            if (already)
            {
                // An earlier attempt at this delivery reached them. At-least-once must not
                // become "told twice" for the people it worked for.
                told++;

                continue;
            }

            // Somebody who already has a live card for this incident is not rung again for an
            // update to it (#133). The reconciler edits their card to the present - and an edit
            // notifies nobody, which is right for a person who already knows. Somebody who has
            // no card for it yet is exactly who this message is for.
            if (!Rings(message.Snapshot.Event) && incidentId is { } live
                && await db.TeamsBotMessages.AnyAsync(
                    m => m.Kind == TeamsBotMessageKind.Alert
                        && m.State == TeamsBotMessageState.Live
                        && m.IncidentId == live
                        && m.Recipient == recipient,
                    ct).ConfigureAwait(false))
            {
                told++;

                continue;
            }

            var chat = await ChatAsync(db, recipient, ct).ConfigureAwait(false);

            if (chat.Value is not { } conversation)
            {
                Note(chat, recipient, ref retryable, ref refused);

                continue;
            }

            var sent = await client.SendAsync(conversation, card, ct).ConfigureAwait(false);

            if (sent.Value is not { } activity)
            {
                Note(sent, recipient, ref retryable, ref refused);

                continue;
            }

            var now = clock.UtcNow;

            if (incidentId is { } id)
            {
                var older = await db.TeamsBotMessages
                    .Where(m => m.Kind == TeamsBotMessageKind.Alert
                        && m.State == TeamsBotMessageState.Live
                        && m.IncidentId == id
                        && m.Recipient == recipient)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var previous in older)
                {
                    previous.State = TeamsBotMessageState.Superseded;
                    previous.UpdatedAt = now;
                }
            }

            db.TeamsBotMessages.Add(new TeamsBotMessage
            {
                Kind = TeamsBotMessageKind.Alert,

                // An alert about the agent has no incident to follow, so it is never edited.
                State = incidentId is null ? TeamsBotMessageState.Final : TeamsBotMessageState.Live,
                IncidentId = incidentId,
                Recipient = recipient,
                ConversationId = conversation,
                ActivityId = activity,
                ContentHash = hash,
                DeliveryId = message.DeliveryId,
                CreatedAt = now,
                UpdatedAt = now,
            });

            // Per recipient, not once at the end: the id Teams just returned exists nowhere else,
            // and losing it to a failure further down the list would leave a card nothing can edit.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            told++;
        }

        if (retryable is not null)
        {
            return DeliveryResult.Retry(retryable);
        }

        if (told > 0)
        {
            if (refused is not null)
            {
                logger.LogWarning(
                    "Delivery {DeliveryId} reached {Told} of {All} recipients. Not reached: {Detail}",
                    message.DeliveryId,
                    told,
                    recipients.Count,
                    refused);
            }

            return DeliveryResult.Ok();
        }

        return DeliveryResult.Permanent(refused ?? "nobody was reached");
    }

    private static async Task<(JsonObject Card, Guid? IncidentId)> RenderAsync(
        HephaistoDbContext db,
        TeamsBotIncidents incidents,
        NotificationMessage message,
        NotificationOptions o,
        CancellationToken ct)
    {
        var s = message.Snapshot;

        if (s.IncidentId is { } id)
        {
            var known = await incidents
                .ByIdAsync([id], ct, withPendingActions: TeamsBotLinks.Alert(o, null).Approvals)
                .ConfigureAwait(false);

            if (known.TryGetValue(id, out var incident))
            {
                var board = await db.TeamsBotMessages.AsNoTracking()
                    .Where(m => m.Kind == TeamsBotMessageKind.Board
                        && m.State == TeamsBotMessageState.Live
                        && m.Recipient == (o.TeamsBot.ChannelId ?? string.Empty))
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.ActivityId)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                var card = TeamsBotCards.Alert(incident, TeamsBotLinks.Alert(o, board));

                return (TeamsBotCards.WithSummary(card, Announcement(message, incident.Title)), id);
            }
        }

        // A work item's code fix (v0.14.0) and a weekly review's end (#312): no incident, so no
        // card that is edited afterwards and none on the board - one message, final, like an
        // event about the agent.
        return (s switch
        {
            { Event: NotificationEvent.PlannerReviewFinished } => TeamsBotCards.ReviewEvent(message),
            { WorkItemId: null } => TeamsBotCards.AgentEvent(message),
            _ => TeamsBotCards.WorkItemEvent(message),
        }, null);
    }

    /// <summary>
    /// Whether an event is always a new message, or an update to one already sent.
    /// </summary>
    /// <remarks>
    /// The investigation ending and the incident being resolved update what a person was told
    /// when it opened. Everything else asks something of somebody - look, approve, the fix did not
    /// hold - and is its own message.
    /// </remarks>
    internal static bool Rings(NotificationEvent evt) =>
        evt is not (NotificationEvent.IncidentEscalated or NotificationEvent.IncidentResolved);

    /// <summary>
    /// What a lock screen shows: the event that happened, which is not always the state arrived at.
    /// </summary>
    private static string Announcement(NotificationMessage message, string title)
    {
        var what = message.Snapshot.Event switch
        {
            NotificationEvent.IncidentOpened => "Opened",
            NotificationEvent.IncidentUnanswered => "Nobody has answered",
            NotificationEvent.SeverityRaised => "Now " + message.Snapshot.Severity.ToString().ToLowerInvariant(),
            NotificationEvent.IncidentEscalated => "Escalated",
            NotificationEvent.ApprovalRequired => "Approval required",
            NotificationEvent.IncidentResolved => "Resolved",
            NotificationEvent.VerificationFailed => "Verification failed",
            NotificationEvent.CodeFixPlanReady => "Code fix planned",
            NotificationEvent.CodeFixPrOpened => "Pull request opened",
            NotificationEvent.CodeFixFailed => "Code fix ended without a PR",
            _ => "Hephaisto",
        };

        var text = string.IsNullOrWhiteSpace(title) ? what : $"{what}: {title}";

        // The suppressed count rides on the message that does go out, as it does on every other
        // channel, so a storm is visible where somebody is already looking.
        return message.AlsoSuppressed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{text} (+{message.AlsoSuppressed} suppressed)")
            : text;
    }

    /// <summary>
    /// The bot's chat with one person. Reused from the last alert they were sent; looked up in
    /// the team only for somebody who has never had one.
    /// </summary>
    private async Task<TeamsBotResult<string>> ChatAsync(HephaistoDbContext db, string recipient, CancellationToken ct)
    {
        var known = await db.TeamsBotMessages.AsNoTracking()
            .Where(m => m.Kind == TeamsBotMessageKind.Alert && m.Recipient == recipient)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ConversationId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(known))
        {
            return new TeamsBotResult<string>(System.Net.HttpStatusCode.OK, known, null);
        }

        var member = await client.FindMemberAsync(recipient, ct).ConfigureAwait(false);

        if (!member.Ok)
        {
            return member;
        }

        if (member.Value is not { } user)
        {
            // Teams answered, and this address belongs to nobody in the team. Waiting will not
            // add them, so it is refused rather than retried.
            return new TeamsBotResult<string>(System.Net.HttpStatusCode.NotFound, null, member.Detail);
        }

        return await client.OpenChatAsync(user, ct).ConfigureAwait(false);
    }

    private static void Note<T>(TeamsBotResult<T> result, string recipient, ref string? retryable, ref string? refused)
    {
        var delivery = result.ToDelivery();
        var detail = $"{recipient}: {delivery.Detail ?? "no answer"}";

        if (delivery.Disposition == DeliveryDisposition.Retryable)
        {
            retryable ??= detail;
        }
        else
        {
            refused = refused is null ? detail : $"{refused}; {detail}";
        }
    }
}
