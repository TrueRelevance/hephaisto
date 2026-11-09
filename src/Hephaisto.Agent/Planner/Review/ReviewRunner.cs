using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Planning;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>The planner's kept board, for the review: what is on it, without a read of its own.</summary>
public interface IReviewBoard
{
    /// <summary>The board, read when nothing is kept; or why there is none.</summary>
    Task<(Board? Board, string? Problem)> ReadAsync(CancellationToken ct);

    /// <summary>What is kept, without asking GitHub. Null when nothing is.</summary>
    Board? Kept { get; }
}

/// <summary>The planner's own board read (<see cref="PlannerService.ReadAsync"/>): the review adds no second one.</summary>
public sealed class PlannerReviewBoard(PlannerService planner) : IReviewBoard
{
    public async Task<(Board? Board, string? Problem)> ReadAsync(CancellationToken ct)
    {
        var snapshot = await planner.ReadAsync(refresh: false, ct).ConfigureAwait(false);

        return snapshot.Board is { } board ? (board, null) : (null, snapshot.Problem ?? "the planner is off");
    }

    public Board? Kept => planner.Kept?.Board;
}

public enum ReviewStartOutcome
{
    Started = 0,
    Off = 1,

    /// <summary>The actor is one that may not start anything: a model's name, or the system's.</summary>
    Forbidden = 2,

    /// <summary>A run is going, or the schedule's slot already ran.</summary>
    Busy = 3,
}

public sealed record ReviewStart(ReviewStartOutcome Outcome, string Message, Guid? RunId);

/// <summary>
/// The weekly review (#312): one run - by the schedule or by a person, through the same
/// <see cref="StartAsync"/> - lists every open issue of the planner's organisation, picks the ones
/// whose verdict is missing, stale or about an issue that changed, and asks coder Jobs of phase
/// <c>review</c> about them in batches. What they answer is kept for a person to agree with.
/// </summary>
/// <remarks>
/// <para>
/// <b>It writes nothing to GitHub.</b> The run reads (ReviewFactsGatherer) and proposes; every
/// close, Priority or Type is a person's click through <see cref="PlannerService"/>, which is the
/// only file that may call the client's writes. <c>ReviewRunnerTests</c> holds both.
/// </para>
/// <para>
/// <b>One run at a time</b>, in this process and in Postgres (<see cref="IReviewStore.TryStartAsync"/>).
/// At most <c>MaxConcurrentJobs</c> batches run at once, and the whole run at most
/// <c>MaxCostUsd</c>: each Job is given a share of what is left (<see cref="ReviewBudget"/>), and
/// a batch that would start beyond the cap does not. A batch that failed, or did not start, leaves
/// its issues on the verdicts they had - none for a new one - and the run's problem names it; the
/// next run asks about them again, because they are still unreviewed or still changed.
/// </para>
/// <para>
/// <b>A restart forgets a run that was going</b>: the scheduler marks it Failed at startup, what
/// its finished batches answered is kept, and its Jobs end by their deadline. That is cheaper than
/// following Jobs across a restart for a pass that runs again next week, or now on a click.
/// </para>
/// </remarks>
public sealed class ReviewRunner(
    IServiceScopeFactory scopes,
    IOptionsMonitor<GitHubOptions> github,
    IOptionsMonitor<CodeFixOptions> codeFix,
    ICodeFixJobLauncher launcher,
    IReviewStore store,
    IReviewBoard board,
    IClock clock,
    ILogger<ReviewRunner> logger) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Beyond a Job's own deadline, before a Job that never ended is given up on.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    public const string ScheduleActor = "schedule";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task running = Task.CompletedTask;

    /// <summary>How the runner waits between looks at a Job, and the gatherer for a rate limit. Tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public bool Enabled => github.CurrentValue is { Enabled: true, Planner: { Enabled: true, Review.Enabled: true } };

    /// <summary>A run is going in this process.</summary>
    public bool IsRunning => !running.IsCompleted;

    /// <summary>Why a run cannot start here, or null when one can.</summary>
    public string? Unavailable()
    {
        if (!Enabled)
        {
            return "the weekly review is off on this install (github.planner.review.enabled)";
        }

        var o = codeFix.CurrentValue;

        return !launcher.IsAvailable || string.IsNullOrWhiteSpace(o.Image) || string.IsNullOrWhiteSpace(o.ContextRepositoryUrl)
            ? "the review runs as coder Jobs, and this install has no coder image or no dev-context to give them"
            : null;
    }

    /// <summary>
    /// Starts a run, for a person or for the schedule's <paramref name="slot"/>, and answers at
    /// once: the run goes on in the background. Refused while another one runs.
    /// </summary>
    public async Task<ReviewStart> StartAsync(PlannerReviewTrigger trigger, string actor, DateTimeOffset? slot, CancellationToken ct)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (Unavailable() is { } off)
        {
            return new(ReviewStartOutcome.Off, off, null);
        }

        if (trigger == PlannerReviewTrigger.Manual && IncidentStateMachine.IsForbiddenGranter(who))
        {
            return new(ReviewStartOutcome.Forbidden, $"'{who}' may not start the review: running it is a person's click or the schedule's.", null);
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (IsRunning)
            {
                return new(ReviewStartOutcome.Busy, "a review is running already; it says when it is done", null);
            }

            if (slot is { } s && await store.SlotTakenAsync(s, ct).ConfigureAwait(false))
            {
                return new(ReviewStartOutcome.Busy, $"the review of {s:O} ran already", null);
            }

            var run = new PlannerReviewRun
            {
                Trigger = trigger,
                State = PlannerReviewRunState.Running,
                Slot = slot,
                RequestedBy = trigger == PlannerReviewTrigger.Schedule ? ScheduleActor : ReviewVerdicts.Cap(who, 320),
                StartedAt = clock.UtcNow,
            };

            if (!await store.TryStartAsync(run, ct).ConfigureAwait(false))
            {
                return new(ReviewStartOutcome.Busy, "a review is running already, or this slot ran", null);
            }

            logger.LogInformation("The weekly review {Run} starts ({Trigger}, asked for by {Actor}).", run.Id, trigger, run.RequestedBy);

            running = Task.Run(() => RunAsync(run, stopping.Token), CancellationToken.None);

            return new(ReviewStartOutcome.Started, "the review is running", run.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The run that is going, to its end. For a test, and for a host that stops.</summary>
    public Task WaitAsync() => running;

    /// <summary>The host stops: the run's Jobs are not followed any more.</summary>
    public void Stop() => stopping.Cancel();

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
        gate.Dispose();
    }

    private sealed record BatchOutcome(decimal Reserved, decimal Cost, bool Launched, IReadOnlyList<string> Reviewed, string? Problem, bool Failed);

    private async Task RunAsync(PlannerReviewRun run, CancellationToken ct)
    {
        var problems = new List<string>();

        try
        {
            var o = github.CurrentValue;
            var review = o.Planner.Review;

            await using var scope = scopes.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
            {
                await FailAsync(run, "GitHub is off on this install", ct).ConfigureAwait(false);
                return;
            }

            var gatherer = new ReviewFactsGatherer(client, clock, Delay);

            var (onBoard, boardProblem) = await board.ReadAsync(ct).ConfigureAwait(false);

            if (onBoard is null)
            {
                // Without the board every issue would read "not on the board", which is a fact the
                // review hands a model and a person: better no run than a wrong one.
                await FailAsync(run, $"the planner's board could not be read: {boardProblem}", ct).ConfigureAwait(false);
                return;
            }

            var listed = await gatherer.ListOpenAsync(o.Planner.Owner, ct).ConfigureAwait(false);

            if (!listed.Ok)
            {
                await FailAsync(run, listed.Detail ?? listed.Describe(), ct).ConfigureAwait(false);
                return;
            }

            var listing = listed.Value!;
            var open = listing.Issues;
            var repositories = await gatherer.RepositoriesAsync(open.Select(i => i.Repository), ct).ConfigureAwait(false);
            var items = onBoard.Items
                .GroupBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            if (listing.Note is { } note)
            {
                problems.Add(note);
            }

            if (repositories.Where(r => r.Value is null).Select(r => r.Key).ToList() is { Count: > 0 } unknown)
            {
                problems.Add($"Whether {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? " and others" : string.Empty)} is archived could not be read; what was known is kept.");
            }

            var now = clock.UtcNow;

            await store.SyncOpenAsync(
                [.. open.Select(i => new ReviewOpenFacts(i.Repository, i.Issue.Number, i.Issue.Title, i.Issue.Url, items.ContainsKey(i.Reference), repositories.GetValueOrDefault(i.Repository)?.Archived))],
                listing.Complete,
                now,
                ct).ConfigureAwait(false);

            var known = await store.VerdictsAsync(includeGone: true, ct).ConfigureAwait(false);
            var selected = ReviewSelection.Select(
                open.Select(i => new ReviewOpen(i.Reference, i.Repository, i.Issue.Number, i.UpdatedAt)),
                known.Select(k => new ReviewKnown(k.Reference, k.IssueUpdatedAt, k.ReviewedAt, k.GoneAt is not null)),
                now,
                TimeSpan.FromDays(review.RecheckAfterDays));
            var batches = ReviewSelection.Batches(selected, review.IssuesPerJob);

            run.Open = open.Count;
            await store.UpdateRunAsync(run, ct).ConfigureAwait(false);

            logger.LogInformation(
                "The weekly review {Run}: {Open} open issues, {Asked} to ask about in {Batches} batches.",
                run.Id, open.Count, batches.Sum(b => b.Count), batches.Count);

            var byReference = open.ToDictionary(i => i.Reference, StringComparer.OrdinalIgnoreCase);
            var budget = new ReviewBudget(review.MaxCostUsd, review.MaxConcurrentJobs);
            var queue = new Queue<(int Index, IReadOnlyList<ReviewOpen> Batch)>(batches.Select((b, i) => (i + 1, b)));
            var inflight = new List<Task<BatchOutcome>>();
            var failed = 0;

            while (true)
            {
                while (inflight.Count < review.MaxConcurrentJobs && queue.Count > 0 && budget.Reserve() is { } share)
                {
                    var (index, batch) = queue.Dequeue();
                    inflight.Add(BatchAsync(run, index, batches.Count, batch, share, byReference, items, repositories, gatherer, ct));
                }

                if (inflight.Count == 0)
                {
                    break;
                }

                var done = await Task.WhenAny(inflight).ConfigureAwait(false);
                inflight.Remove(done);

                var outcome = await done.ConfigureAwait(false);

                budget.Settle(outcome.Reserved, outcome.Cost);
                run.Jobs += outcome.Launched ? 1 : 0;
                run.CostUsd += outcome.Cost;
                run.Reviewed.AddRange(outcome.Reviewed);
                failed += outcome.Failed ? 1 : 0;

                if (outcome.Problem is { } problem)
                {
                    problems.Add(problem);
                }

                run.Problem = Problem(problems);
                await store.UpdateRunAsync(run, ct).ConfigureAwait(false);
            }

            if (queue.Count > 0)
            {
                problems.Add(
                    $"{queue.Count} of {batches.Count} batches ({queue.Sum(q => q.Batch.Count)} issues) were not started: the run's budget of "
                    + $"${review.MaxCostUsd:0.##} was spent (${budget.Spent:0.##}). Their issues keep the verdicts they had and are asked about in the next run.");
                failed += queue.Count;
            }

            await FinishAsync(run, open, problems, batches.Count > 0 && failed == batches.Count, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReviewVerdicts.Interrupted(run, clock.UtcNow);
            await SaveQuietlyAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The weekly review {Run} failed.", run.Id);

            problems.Add($"The run stopped: {ex.Message}");
            run.State = PlannerReviewRunState.Failed;
            run.FinishedAt = clock.UtcNow;
            run.Problem = Problem(problems);
            await SaveQuietlyAsync(run).ConfigureAwait(false);
        }
    }

    private async Task<BatchOutcome> BatchAsync(
        PlannerReviewRun run,
        int index,
        int of,
        IReadOnlyList<ReviewOpen> batch,
        decimal share,
        IReadOnlyDictionary<string, GitHubOrgIssue> open,
        IReadOnlyDictionary<string, BoardItem> items,
        IReadOnlyDictionary<string, GitHubRepository?> repositories,
        ReviewFactsGatherer gatherer,
        CancellationToken ct)
    {
        var name = $"Batch {index} of {of} ({batch[0].Reference}{(batch.Count > 1 ? $" and {batch.Count - 1} more" : string.Empty)})";
        var o = github.CurrentValue;
        var review = o.Planner.Review;
        var cf = codeFix.CurrentValue;

        try
        {
            var issues = new List<ReviewIssue>();
            var context = new Dictionary<string, ReviewIssueContext>(StringComparer.OrdinalIgnoreCase);
            var unread = new List<string>();

            foreach (var wanted in batch)
            {
                var found = open[wanted.Reference];
                var read = await gatherer.IssueAsync(
                    found, items.GetValueOrDefault(found.Reference), repositories.GetValueOrDefault(found.Repository)?.Archived ?? false, ct).ConfigureAwait(false);

                if (!read.Ok)
                {
                    unread.Add(read.Detail ?? read.Describe());
                    continue;
                }

                issues.Add(read.Value!);
                context[found.Reference] = new ReviewIssueContext(found.Issue.NodeId, found.UpdatedAt);
            }

            var skipped = unread.Count == 0 ? null : $"{unread.Count} issue(s) of it were left out: {unread[0]}";

            if (issues.Count == 0)
            {
                return new(share, 0, false, [], $"{name}: no issue's facts could be read ({unread[0]}).", Failed: true);
            }

            var attemptId = Guid.CreateVersion7();
            var request = new ReviewRequest
            {
                AttemptId = attemptId,
                Budget = new InvestigateBudget(share, (int)review.Deadline.TotalSeconds, review.MaxTurns),
                Context = new CodeFixContextRef(cf.ContextRepositoryUrl, string.IsNullOrWhiteSpace(cf.ContextRepositoryRef) ? "main" : cf.ContextRepositoryRef),
                Repositories = Offered(issues, repositories, o, cf),
                Issues = issues,
            };

            string job;

            try
            {
                job = await launcher.LaunchAsync(
                    ReviewJobSpec.Job(attemptId, run.Id, review, cf),
                    created => ReviewJobSpec.RequestConfigMap(attemptId, cf, JsonSerializer.Serialize(request, CodeFixContract.Json), created),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Batch} of the weekly review {Run} could not start its Job.", name, run.Id);
                return new(share, 0, false, [], $"{name}: the Job could not be started: {ex.Message}", Failed: true);
            }

            var started = clock.UtcNow;
            CodeFixJobObservation observed;

            while (true)
            {
                await Delay(PollInterval, ct).ConfigureAwait(false);
                observed = await launcher.ObserveAsync(job, ct).ConfigureAwait(false);

                if (observed.Phase is not (CodeFixJobPhase.Pending or CodeFixJobPhase.Running))
                {
                    break;
                }

                if (clock.UtcNow - started > review.Deadline + Grace)
                {
                    await DeleteQuietlyAsync(job).ConfigureAwait(false);
                    return new(share, 0, true, [], $"{name}: the Job {job} did not end within its deadline.", Failed: true);
                }
            }

            var log = observed.Phase == CodeFixJobPhase.Missing ? null : await launcher.ReadResultLogAsync(job, ct).ConfigureAwait(false);
            var parsed = CodeFixResultParser.ParseReview(log, attemptId);

            await DeleteQuietlyAsync(job).ConfigureAwait(false);

            if (parsed.Result is not { } result)
            {
                return new(share, 0, true, [], $"{name}: the Job's answer could not be read: {parsed.Violation}", Failed: true);
            }

            if (result.Outcome != "reviewed")
            {
                return new(share, result.CostUsd, true, [],
                    $"{name}: Claude did not finish ({result.Outcome}){(result.Error is { } e ? $": {ReviewVerdicts.Cap(e, 200)}" : string.Empty)}.", Failed: true);
            }

            var verdicts = ReviewVerdicts.FromResult(issues, result.Verdicts, context, run.Id, clock.UtcNow);
            await store.SaveVerdictsAsync(verdicts, ct).ConfigureAwait(false);

            logger.LogInformation("{Batch} of the weekly review {Run}: {Count} verdicts, ${Cost}.", name, run.Id, verdicts.Count, result.CostUsd);

            return new(share, result.CostUsd, true, [.. verdicts.Select(v => v.Reference)], skipped is null ? null : $"{name}: {skipped}", Failed: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Batch} of the weekly review {Run} failed.", name, run.Id);
            return new(share, 0, false, [], $"{name}: {ex.Message}", Failed: true);
        }
    }

    /// <summary>
    /// What the Job may clone: each repository of the batch once, never an archived one, and only
    /// where Hephaisto knows where its code is - a <c>CodeFix:Repositories</c> entry, or github.com
    /// when that is the GitHub this agent talks to. Against a stand-in or an Enterprise server no
    /// github.com address is guessed: those issues are reviewed from their facts.
    /// </summary>
    internal static IReadOnlyList<ReviewRepository> Offered(
        IReadOnlyList<ReviewIssue> issues, IReadOnlyDictionary<string, GitHubRepository?> repositories, GitHubOptions github, CodeFixOptions cf)
    {
        var onGitHubCom = string.Equals(github.ApiBase().Host, "api.github.com", StringComparison.OrdinalIgnoreCase);

        return [.. issues
            .Select(i => i.Repository)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(repository =>
            {
                var facts = repositories.GetValueOrDefault(repository);

                if (facts is null || facts.Archived)
                {
                    return null;
                }

                var binding = cf.BindingForRepository(repository)
                    ?? (onGitHubCom ? CodeFixOptions.GitHubRepository(repository, facts.DefaultBranch) : null);

                return binding is null
                    ? null
                    : new ReviewRepository(
                        repository,
                        binding.Url.Trim(),
                        string.IsNullOrWhiteSpace(binding.DefaultBranch) ? facts.DefaultBranch ?? "main" : binding.DefaultBranch.Trim(),
                        Archived: false);
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .Take(50)];
    }

    private async Task FinishAsync(PlannerReviewRun run, IReadOnlyList<GitHubOrgIssue> open, List<string> problems, bool allFailed, CancellationToken ct)
    {
        var openNow = open.Select(i => i.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verdicts = (await store.VerdictsAsync(includeGone: false, ct).ConfigureAwait(false)).Where(v => openNow.Contains(v.Reference)).ToList();
        var tabs = verdicts.GroupBy(ReviewVerdicts.Tab).ToDictionary(g => g.Key, g => g.Count());

        run.Kept = verdicts.Count(v => v.RunId != run.Id);
        run.CountClose = tabs.GetValueOrDefault(ReviewVerdicts.Close);
        run.CountBug = tabs.GetValueOrDefault(ReviewVerdicts.Bug);
        run.CountFeature = tabs.GetValueOrDefault(ReviewVerdicts.Feature);
        run.CountUnsure = tabs.GetValueOrDefault(ReviewVerdicts.Unsure);
        run.State = allFailed ? PlannerReviewRunState.Failed : PlannerReviewRunState.Finished;
        run.FinishedAt = clock.UtcNow;
        run.Problem = Problem(problems);

        await store.UpdateRunAsync(run, ct).ConfigureAwait(false);

        logger.LogInformation(
            "The weekly review {Run} is {State}: {Reviewed} reviewed, {Kept} kept, {Jobs} Jobs, ${Cost}.{Problem}",
            run.Id, run.State, run.Reviewed.Count, run.Kept, run.Jobs, run.CostUsd, run.Problem is null ? string.Empty : " " + run.Problem);
    }

    private async Task FailAsync(PlannerReviewRun run, string problem, CancellationToken ct)
    {
        logger.LogWarning("The weekly review {Run} failed: {Problem}", run.Id, problem);

        run.State = PlannerReviewRunState.Failed;
        run.FinishedAt = clock.UtcNow;
        run.Problem = Problem([problem]);
        await store.UpdateRunAsync(run, ct).ConfigureAwait(false);
    }

    private async Task SaveQuietlyAsync(PlannerReviewRun run)
    {
        try
        {
            await store.UpdateRunAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The scheduler's startup marks it failed instead.
            logger.LogWarning(ex, "The end of the weekly review {Run} could not be written.", run.Id);
        }
    }

    private async Task DeleteQuietlyAsync(string job)
    {
        try
        {
            await launcher.DeleteAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Job's TTL collects it.
            logger.LogDebug(ex, "The review Job {Job} could not be deleted.", job);
        }
    }

    private static string? Problem(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? null : ReviewVerdicts.Cap(string.Join(" ", problems), HephaistoDbContext.PlannerReviewRunMaxProblem);
}
