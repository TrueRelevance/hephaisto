using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Planner.Review;

public enum ReviewState
{
    /// <summary>No run can start here: the review is off, or there is no coder to run it. <see cref="ReviewView.Problem"/> says which.</summary>
    Off = 0,

    Ready = 1,

    Running = 2,
}

/// <summary>What <c>GET /api/planner/review</c> answers (#312).</summary>
/// <param name="Run">The newest run, finished or not.</param>
/// <param name="NextRunAt">The schedule's next moment; null when the review is off.</param>
/// <param name="Pending">Verdicts nobody decided yet: the console's badge.</param>
/// <param name="Verdicts">One per open issue that has one; closed issues drop out.</param>
public sealed record ReviewView(
    ReviewState State,
    string? Problem,
    ReviewRunView? Run,
    DateTimeOffset? NextRunAt,
    int Pending,
    IReadOnlyList<ReviewVerdictView> Verdicts);

/// <param name="Trigger"><c>manual</c> or <c>schedule</c>.</param>
/// <param name="Reviewed">The issues this run gave a verdict, <c>owner/repo#n</c>.</param>
/// <param name="Kept">Open issues that kept the verdict an earlier run gave them.</param>
/// <param name="Counts">Where the open issues' verdicts were when the run ended, by tab.</param>
public sealed record ReviewRunView(
    Guid Id,
    string Trigger,
    PlannerReviewRunState State,
    string? Problem,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Open,
    IReadOnlyList<string> Reviewed,
    int Kept,
    int Jobs,
    decimal CostUsd,
    ReviewCountsView Counts);

public sealed record ReviewCountsView(int Close, int Bug, int Feature, int Unsure);

/// <param name="Group"><c>close</c>, <c>bug</c>, <c>feature</c>, or <c>unsure</c> for an issue the model gave no usable verdict.</param>
/// <param name="Tab">Where it is shown: the person's decision, else <c>unsure</c> for a low confidence, else the group.</param>
/// <param name="Summary">A model's text about a stranger's: text, never markup.</param>
public sealed record ReviewVerdictView(
    string Repository,
    int Number,
    string Title,
    string Url,
    bool OnBoard,
    bool Archived,
    string Group,
    string? CloseReason,
    string? Related,
    string Confidence,
    string Summary,
    string Reason,
    IReadOnlyList<ReviewEvidenceView> Evidence,
    string? Priority,
    string Tab,
    ReviewDecisionView? Decision,
    DateTimeOffset ReviewedAt,
    Guid RunId);

public sealed record ReviewEvidenceView(string Kind, string Text, bool? Ok);

public sealed record ReviewDecisionView(string By, DateTimeOffset At, string Group, string? CloseReason, bool Agreed, bool Unsure);

/// <param name="DecidedBy">Who, when nobody is signed in. Ignored for a signed-in request, as everywhere.</param>
public sealed record ReviewRunRequest(string? DecidedBy);

public sealed record ReviewRunStarted(Guid RunId, string Message);

/// <param name="DecidedBy">Who, when nobody is signed in. Ignored for a signed-in request, as everywhere.</param>
public sealed record ReviewDecideRequest(string? DecidedBy);

/// <param name="Group"><c>close</c>, <c>bug</c> or <c>feature</c>.</param>
/// <param name="CloseReason">With <c>close</c>, and only then: done, superseded, obsolete, duplicate, answered or stale.</param>
public sealed record ReviewMoveRequest(string? Group, string? CloseReason, string? DecidedBy);

/// <param name="Verdict">The verdict as it is now; null once the issue is out of the review.</param>
public sealed record ReviewActionResponse(ReviewActionOutcome Outcome, string Message, ReviewVerdictView? Verdict);

/// <param name="Closed">The issues closed, <c>owner/repo#n</c>.</param>
/// <param name="Refused">The ones GitHub refused, each with why.</param>
/// <param name="Archived">Agreed, in an archived repository, and left open: nothing was tried on them.</param>
public sealed record ReviewCloseResponse(ReviewActionOutcome Outcome, string Message, IReadOnlyList<string> Closed, IReadOnlyList<string> Refused, IReadOnlyList<string> Archived);

/// <summary>The review as the console and the API read it (#312).</summary>
public sealed class ReviewReader(ReviewRunner runner, IReviewStore store, IReviewBoard board, IOptionsMonitor<Hephaisto.Agent.GitHub.GitHubOptions> github, IClock clock)
{
    public async Task<ReviewView> ReadAsync(CancellationToken ct)
    {
        var problem = runner.Unavailable();
        var latest = await store.LatestRunAsync(ct).ConfigureAwait(false);
        var state = problem is not null ? ReviewState.Off
            : runner.IsRunning || latest is { State: PlannerReviewRunState.Running } ? ReviewState.Running
            : ReviewState.Ready;

        // An issue the kept board knows to be closed SINCE its verdict - a person closed it on
        // the planner a minute ago - is out before the next run finds it gone. One closed before
        // the verdict was given was open when the run looked, so the board is the older word.
        var closedAt = (board.Kept?.Items ?? [])
            .Where(i => !i.Open && i.ClosedAt is not null)
            .GroupBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(i => i.ClosedAt!.Value), StringComparer.OrdinalIgnoreCase);

        var verdicts = (await store.VerdictsAsync(includeGone: false, ct).ConfigureAwait(false))
            .Where(v => !(closedAt.TryGetValue(v.Reference, out var at) && at > v.ReviewedAt))
            .Select(View)
            .OrderBy(v => Array.IndexOf(Tabs, v.Tab))
            .ThenBy(v => v.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Number)
            .ToList();

        return new ReviewView(
            state,
            problem,
            latest is null ? null : View(latest),
            ReviewScheduler.Next(github.CurrentValue.Planner.Review, clock.UtcNow),
            verdicts.Count(v => v.Decision is null),
            verdicts);
    }

    private static readonly string[] Tabs = [ReviewVerdicts.Close, ReviewVerdicts.Bug, ReviewVerdicts.Feature, ReviewVerdicts.Unsure];

    public static ReviewRunView View(PlannerReviewRun run) => new(
        run.Id,
        run.Trigger == PlannerReviewTrigger.Schedule ? "schedule" : "manual",
        run.State,
        run.Problem,
        run.StartedAt,
        run.FinishedAt,
        run.Open,
        run.Reviewed,
        run.Kept,
        run.Jobs,
        run.CostUsd,
        new ReviewCountsView(run.CountClose, run.CountBug, run.CountFeature, run.CountUnsure));

    public static ReviewVerdictView View(PlannerReviewVerdict v) => new(
        v.Repository,
        v.Number,
        v.Title,
        v.Url,
        v.OnBoard,
        v.Archived,
        v.Group,
        v.CloseReason,
        v.Related,
        v.Confidence,
        v.Summary,
        v.Reason,
        [.. v.Evidence.Select(e => new ReviewEvidenceView(e.Kind, e.Text, e.Ok))],
        v.Priority,
        ReviewVerdicts.Tab(v),
        v.DecidedBy is { } by
            ? new ReviewDecisionView(by, v.DecidedAt ?? v.ReviewedAt, v.DecisionGroup ?? v.Group, v.DecisionCloseReason, v.DecisionAgreed, v.DecisionUnsure)
            : null,
        v.ReviewedAt,
        v.RunId);
}

/// <summary>
/// <c>/api/planner/review</c> (#312): the read behind the console's read policy, like the rest of
/// the planner; starting a run is a POST behind the approver policy and names a person.
/// </summary>
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/planner/review", async (ReviewReader reader, CancellationToken ct) => TypedResults.Ok(await reader.ReadAsync(ct)))
            .WithName("ReadPlannerReview");

        app.MapPost("/api/planner/review/run", RunAsync)
            .WithName("RunPlannerReview")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        // A person's clicks (stage 4): each names them, and every GitHub write is the planner's.
        var verdict = app.MapGroup("/api/planner/review/verdicts/{owner}/{repo}/{number:int}");

        verdict.MapPost("/agree", (string owner, string repo, int number, ReviewDecideRequest? body, HttpContext http, ReviewActions actions, ReviewReader reader, CancellationToken ct) =>
                DecideAsync(owner, repo, number, body?.DecidedBy, http, reader, ct, actor => actions.AgreeAsync($"{owner}/{repo}", number, actor, ct)))
            .WithName("AgreeWithReviewVerdict")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        verdict.MapPost("/move", (string owner, string repo, int number, ReviewMoveRequest body, HttpContext http, ReviewActions actions, ReviewReader reader, CancellationToken ct) =>
                DecideAsync(owner, repo, number, body.DecidedBy, http, reader, ct, actor => actions.MoveAsync($"{owner}/{repo}", number, body.Group, body.CloseReason, actor, ct)))
            .WithName("MoveReviewVerdict")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        verdict.MapPost("/unsure", (string owner, string repo, int number, ReviewDecideRequest? body, HttpContext http, ReviewActions actions, ReviewReader reader, CancellationToken ct) =>
                DecideAsync(owner, repo, number, body?.DecidedBy, http, reader, ct, actor => actions.UnsureAsync($"{owner}/{repo}", number, actor, ct)))
            .WithName("MarkReviewVerdictUnsure")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        app.MapPost("/api/planner/review/close", CloseAsync)
            .WithName("CloseAgreedReviewIssues")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        return app;
    }

    private static async Task<Results<Ok<ReviewActionResponse>, NotFound<ReviewActionResponse>, Conflict<ReviewActionResponse>, ValidationProblem>> DecideAsync(
        string owner, string repo, int number, string? decidedBy, HttpContext http, ReviewReader reader, CancellationToken ct, Func<string, Task<ReviewActionResult>> act)
    {
        var actor = ActorResolution.Resolve(http.User, decidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return NobodyNamed();
        }

        var result = await act(actor);
        var now = (await reader.ReadAsync(ct)).Verdicts
            .FirstOrDefault(v => string.Equals(v.Repository, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase) && v.Number == number);
        var response = new ReviewActionResponse(result.Outcome, result.Message, now);

        return result.Outcome switch
        {
            ReviewActionOutcome.Done => TypedResults.Ok(response),
            ReviewActionOutcome.NotFound => TypedResults.NotFound(response),
            _ => TypedResults.Conflict(response),
        };
    }

    private static async Task<Results<Ok<ReviewCloseResponse>, Conflict<ReviewCloseResponse>, ValidationProblem>> CloseAsync(
        ReviewDecideRequest? body, HttpContext http, ReviewActions actions, CancellationToken ct)
    {
        var actor = ActorResolution.Resolve(http.User, body?.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return NobodyNamed();
        }

        var result = await actions.CloseAgreedAsync(actor, ct);
        var response = new ReviewCloseResponse(result.Outcome, result.Message, result.Closed, result.Refused, result.Archived);

        return result.Done ? TypedResults.Ok(response) : TypedResults.Conflict(response);
    }

    private static ValidationProblem NobodyNamed() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["decidedBy"] = ["decidedBy is required when nobody is signed in; the audit trail names a person, not a request."],
        });

    private static async Task<Results<Accepted<ReviewRunStarted>, Conflict<string>, ValidationProblem>> RunAsync(
        ReviewRunRequest? body, HttpContext http, ReviewRunner runner, CancellationToken ct)
    {
        var actor = ActorResolution.Resolve(http.User, body?.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["decidedBy"] = ["decidedBy is required when nobody is signed in; a run names who started it."],
            });
        }

        var started = await runner.StartAsync(PlannerReviewTrigger.Manual, actor, slot: null, ct);

        return started is { Outcome: ReviewStartOutcome.Started, RunId: { } id }
            ? TypedResults.Accepted("/api/planner/review", new ReviewRunStarted(id, started.Message))
            : TypedResults.Conflict(started.Message);
    }
}
