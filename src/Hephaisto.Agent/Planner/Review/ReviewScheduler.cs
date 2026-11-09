using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Planning;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>
/// Starts the weekly review at each moment of <c>github.planner.review.schedule</c>, read in its
/// time zone (#312). Off unless <c>github.planner.review.enabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A slot runs once</b>, whatever restarts: the run row carries its slot and Postgres holds the
/// slot unique, so a restart within the slot's minute - or a second replica - finds it taken.
/// </para>
/// <para>
/// <b>A missed slot runs once, on startup, if it passed less than <see cref="CatchUp"/> ago</b> -
/// a deploy or a restart across the slot - and is skipped otherwise: a review that turns up hours
/// late, on a Wednesday after a long outage, is a surprise and costs money nobody planned for; the
/// console's run button is there for that. The same rule wakes the scheduler every minute, so a
/// slot that met a run already going (a person's) starts when that one ends, within the hour.
/// </para>
/// <para>
/// At startup, before anything can start a run, a run left Running by a process that is gone is
/// marked Failed (<see cref="IReviewStore.FailInterruptedAsync"/>).
/// </para>
/// </remarks>
public sealed class ReviewScheduler(
    ReviewRunner runner,
    IReviewStore store,
    IOptionsMonitor<GitHubOptions> github,
    IClock clock,
    ILogger<ReviewScheduler> logger) : BackgroundService
{
    /// <summary>How late a slot may still be started.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(1);

    /// <summary>The longest the scheduler sleeps: a changed schedule takes effect within it.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The slot that is due at <paramref name="now"/>, or null: the newest moment of the schedule
    /// within <see cref="CatchUp"/> before now. Whether it ran already is the store's to say.
    /// </summary>
    public static DateTimeOffset? Due(GitHubPlannerReviewOptions review, DateTimeOffset now) =>
        review.Enabled && CronSchedule.TryParse(review.Schedule, out var schedule, out _) && review.Zone() is { } zone
            ? schedule!.Latest(now, CatchUp, zone)
            : null;

    /// <summary>The next moment of the schedule after <paramref name="now"/>, or null when the review is off.</summary>
    public static DateTimeOffset? Next(GitHubPlannerReviewOptions review, DateTimeOffset now) =>
        review.Enabled && CronSchedule.TryParse(review.Schedule, out var schedule, out _) && review.Zone() is { } zone
            ? schedule!.Next(now, zone)
            : null;

    /// <summary>One look: starts the due slot when there is one that did not run. Answers what it started.</summary>
    public async Task<ReviewStart?> PassAsync(CancellationToken ct)
    {
        var review = github.CurrentValue.Planner.Review;

        if (!runner.Enabled || Due(review, clock.UtcNow) is not { } slot || runner.IsRunning)
        {
            return null;
        }

        if (await store.SlotTakenAsync(slot, ct).ConfigureAwait(false))
        {
            return null;
        }

        var started = await runner.StartAsync(PlannerReviewTrigger.Schedule, ReviewRunner.ScheduleActor, slot, ct).ConfigureAwait(false);

        if (started.Outcome != ReviewStartOutcome.Started)
        {
            logger.LogWarning("The weekly review of {Slot:O} did not start: {Message}", slot, started.Message);
        }

        return started;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.FailInterruptedAsync(clock.UtcNow, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Runs of the weekly review left by an earlier process could not be marked failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Including a database that is away for a minute: the next tick asks again.
                logger.LogWarning(ex, "The weekly review's schedule could not be looked at.");
            }

            var next = Next(github.CurrentValue.Planner.Review, clock.UtcNow);
            var sleep = next is { } at ? at - clock.UtcNow : Tick;

            try
            {
                await Task.Delay(sleep < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : sleep > Tick ? Tick : sleep, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        runner.Stop();
        return base.StopAsync(cancellationToken);
    }
}
