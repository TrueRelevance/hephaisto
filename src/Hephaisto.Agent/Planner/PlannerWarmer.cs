namespace Hephaisto.Agent.Planner;

/// <summary>
/// Keeps the planner's board read (#306): once at startup and every
/// <see cref="PlannerService.WarmEvery"/> after, so opening the planner takes the kept board
/// instead of waiting for GitHub - a whole board was 13 seconds of paged GraphQL.
/// </summary>
/// <remarks>
/// It reads, and with <c>github.planner.keepStatus</c> it applies the board's rules to what it read
/// (<see cref="PlannerService.KeepStatusAsync"/>) - the one write that is not a click. It asks nothing while the planner is off; turning the planner on is picked up
/// at the next tick. Not gated by the agent's mode, for the reason the planner's clicks are not: the
/// board is the person's, and reading it starts nothing. A read that fails keeps the last good
/// board, which then says what went wrong, and the next tick is the retry.
/// </remarks>
public sealed class PlannerWarmer(PlannerService planner, ILogger<PlannerWarmer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted services after this one start only once this method first awaits.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await planner.WarmAsync(stoppingToken).ConfigureAwait(false);

                // The board's rules on what was just read: off unless github.planner.keepStatus.
                if (await planner.KeepStatusAsync(stoppingToken).ConfigureAwait(false) is var moved and > 0)
                {
                    logger.LogInformation("The board's rules moved {Count} issues to the Status GitHub's facts ask for.", moved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading the planner's board ahead failed; the next read is in {Interval}.", PlannerService.WarmEvery);
            }

            try
            {
                await Task.Delay(PlannerService.WarmEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
