using System.Globalization;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>
/// What the end of a run tells people (#312): ONE new message per run, through the notification
/// routes that list <see cref="NotificationEvent.PlannerReviewFinished"/>, with the counts and a
/// link to the review page - or that the run failed, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new message, never an edit</b>: the verdicts wait for a person, and an edit notifies
/// nobody. Who hears it is the routing table's, as for every other event: a route of the Teams
/// bot that lists the event tells its recipients, or the bot's own list, by personal chat.
/// </para>
/// <para>
/// <b>Staged with the run's end</b>, in the same save (<see cref="IReviewStore.EndRunAsync"/>), so a
/// run the database never recorded as ended is not announced and one it did is. The words are
/// Hephaisto's own around counts; a problem can carry GitHub's or a Job's text, which every
/// channel shows as text.
/// </para>
/// <para>
/// A run that a restart cut off announces nothing: the scheduler marks it failed at startup, and
/// the page says so.
/// </para>
/// </remarks>
public static class ReviewAnnouncement
{
    public const string Headline = "Weekly review";

    /// <summary>The run's end, as the facts a channel renders. <paramref name="pending"/>: verdicts nobody decided, when known.</summary>
    public static NotificationSnapshot Snapshot(PlannerReviewRun run, int? pending, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(run);

        var failed = run.State == PlannerReviewRunState.Failed;

        return new NotificationSnapshot
        {
            Event = NotificationEvent.PlannerReviewFinished,

            // Its own key per run: no cooldown can swallow one run's end behind another's.
            CorrelationKey = $"planner/review/{run.Id:N}",
            Title = failed ? $"{Headline} failed" : $"{Headline}: {Counts(run)}",
            Reason = failed ? FailedText(run) : FinishedText(run, pending),
            Severity = failed ? Severity.Warning : Severity.Info,
            At = at,
        };
    }

    /// <summary>The deliveries the routing table says a run's end deserves; none when no route lists the event.</summary>
    public static List<NotificationDelivery> Deliveries(PlannerReviewRun run, int? pending, NotificationOptions options, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(options);

        return NotificationEnqueue.For(Snapshot(run, pending, at), options, at).Deliveries;
    }

    /// <summary>"5 to close, 8 bugs, 8 to build, 1 unsure": the four tabs of the page, as the run left them.</summary>
    public static string Counts(PlannerReviewRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.CountClose} to close, {run.CountBug} {(run.CountBug == 1 ? "bug" : "bugs")}, {run.CountFeature} to build, {run.CountUnsure} unsure");
    }

    private static string FinishedText(PlannerReviewRun run, int? pending)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Open} open {(run.Open == 1 ? "issue" : "issues")}: {run.Reviewed.Count} reviewed in this run (new, changed or due for a recheck), "
                + $"{run.Kept} kept from earlier runs.");

        if (pending is > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" {pending} {(pending == 1 ? "waits" : "wait")} for a person.");
        }

        text += " Nothing was changed on GitHub: closing and setting a priority are a person's click on the review page.";

        return run.Problem is { Length: > 0 } problem ? $"{text} Not everything went: {problem}" : text;
    }

    private static string FailedText(PlannerReviewRun run) =>
        $"The run failed: {(run.Problem ?? "no reason was recorded").TrimEnd('.')}. What earlier runs found is kept; the next run - or Run now on the review page - tries again.";
}
