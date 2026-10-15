using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.Planner;

/// <summary>What <c>GET /api/planner</c> answers: the board, and its open issues in the planner's order.</summary>
/// <param name="State"><c>Off</c>, <c>Read</c> or <c>Unreadable</c>.</param>
/// <param name="Problem">Why the newest read failed. With <c>Read</c>, the items are an older board's.</param>
/// <param name="Repositories">Where a new issue can be opened; the first is the default.</param>
/// <param name="IssueTypes">The organisation's issue types a new issue can have.</param>
/// <param name="Triage">Whether Claude can be asked to label and analyse an issue.</param>
public sealed record PlannerView(
    PlannerState State,
    string? Problem,
    DateTimeOffset? ReadAt,
    string? Title,
    string? Url,
    IReadOnlyList<BoardField> Fields,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<PlannerItemView> Items,
    IReadOnlyList<PlannerClosedView> ClosedToday,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> IssueTypes,
    bool Triage);

/// <param name="Reason">Why the item asks for a decision, or <c>None</c>.</param>
/// <param name="Day"><c>Today</c>, <c>NotToday</c> or <c>Undecided</c>.</param>
/// <param name="Fields">The board's single-select fields that have a value: field name to option name.</param>
/// <param name="Body">The issue's description, as markdown source. Somebody else's text; null when there is none.</param>
/// <param name="Labels">Every label of the issue, the two of the day among them.</param>
/// <param name="CreatedAt">When the issue was opened: what the newest walks go by.</param>
/// <param name="DayAt">When the label of <paramref name="Day"/> was put on; with <paramref name="CarriedOver"/>, the earlier day it was planned on.</param>
/// <param name="PlannedFor">The day in the board's day field, when there is one.</param>
public sealed record PlannerItemView(
    string ItemId,
    string Repository,
    int Number,
    string Title,
    string Url,
    string? Type,
    PlannerReason Reason,
    IReadOnlyList<string> EmptyFields,
    PlannerDay Day,
    bool CarriedOver,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Assignees,
    string? ParentUrl,
    int SubIssues,
    int SubIssuesDone,
    IReadOnlyList<BoardPullRequest> PullRequests,
    DateTimeOffset LastActivity,
    string? Body,
    int Comments,
    IReadOnlyList<string> Labels,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DayAt,
    DateOnly? PlannedFor);

public sealed record PlannerClosedView(string ItemId, string Repository, int Number, string Title, string Url, DateTimeOffset? ClosedAt);

/// <param name="DecidedBy">Who, when nobody is signed in. Ignored for a signed-in request, as everywhere.</param>
public sealed record PlannerFieldRequest(string? Field, string? Option, string? DecidedBy);

public sealed record PlannerCloseRequest(bool NotPlanned, string? DecidedBy);

/// <param name="Day"><c>today</c>, <c>not today</c>, or <c>none</c> to take the decision back.</param>
public sealed record PlannerDayRequest(string? Day, string? DecidedBy);

public sealed record PlannerAssigneeRequest(string? Login, bool Assign, string? DecidedBy);

/// <param name="Item">The item as it is now. Null once it is closed: it is no longer among the open ones.</param>
public sealed record PlannerActionResponse(PlannerActionOutcome Outcome, string Message, PlannerItemView? Item);

/// <param name="Fields">Field name to option name, for the board's single-select fields.</param>
/// <param name="Triage">Ask Claude to label and analyse the issue once it is open.</param>
/// <param name="Day">What the new issue is planned for: <c>today</c>, <c>not today</c>, <c>tomorrow</c>, <c>next week</c>, <c>backlog</c>, or nothing.</param>
public sealed record PlannerNewIssueRequest(
    string? Repository, string? Title, string? Body, string? Type, IReadOnlyDictionary<string, string>? Fields, bool Triage, string? DecidedBy, string? Day = null);

/// <param name="When"><c>tomorrow</c>, <c>next week</c> or <c>backlog</c>.</param>
public sealed record PlannerScheduleRequest(string? When, string? DecidedBy);

public sealed record PlannerCommentRequest(string? Repository, int Number, string? Body, string? DecidedBy);

public sealed record PlannerCommentView(long Id, string Author, DateTimeOffset CreatedAt, string Body, string Url);

public sealed record PlannerCommentsView(string Repository, int Number, IReadOnlyList<PlannerCommentView> Comments);

/// <param name="Open">True for an open issue, false for a closed one.</param>
public sealed record PlannerSearchHit(string ItemId, string Repository, int Number, string Title, string Url, string? Type, bool Open, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Labels);

/// <param name="Triage">What asking Claude answered, when it was asked.</param>
public sealed record PlannerNewIssueResponse(PlannerActionOutcome Outcome, string Message, PlannerItemView? Item, string? Url, TriageStartResponse? Triage);

public sealed record PlannerTriageRequest(string? Repository, int Number, string? DecidedBy);

public sealed record TriageStartResponse(TriageStartOutcome Outcome, string Message, TriageRunView? Run);

/// <param name="State"><c>Running</c>, <c>Done</c> or <c>Failed</c>.</param>
public sealed record TriageRunView(string Repository, int Number, TriageState State, string RequestedBy, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Message, string JobName);

/// <summary>What <c>GET /api/planner/analysis</c> answers for one issue.</summary>
/// <param name="Run">The newest time Claude was asked about it, as far as this process knows.</param>
/// <param name="Analysis">The newest analysis the agent posted on the issue.</param>
/// <param name="Execute">The line to hand the issue to Claude Code with.</param>
public sealed record PlannerAnalysisView(string Repository, int Number, TriageRunView? Run, PlannerAnalysis? Analysis, string Execute);

/// <summary>
/// <c>/api/planner</c> (v0.15.0). Reading is behind the console's read policy. Every write is a
/// POST behind the approver policy - the door closing an incident has - and names a person.
/// </summary>
public static class PlannerEndpoints
{
    public static IEndpointRouteBuilder MapPlannerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/planner", ReadAsync).WithName("ReadPlanner");

        var item = app.MapGroup("/api/planner/items/{itemId}");

        item.MapPost("/field", (string itemId, PlannerFieldRequest body, HttpContext http, PlannerService planner, CancellationToken ct) =>
                string.IsNullOrWhiteSpace(body.Field) || string.IsNullOrWhiteSpace(body.Option)
                    ? Task.FromResult<Results<Ok<PlannerActionResponse>, NotFound<PlannerActionResponse>, Conflict<PlannerActionResponse>, ValidationProblem>>(Missing("field", "option"))
                    : ActAsync(itemId, body.DecidedBy, http, actor => planner.SetFieldAsync(itemId, body.Field, body.Option, actor, ct)))
            .WithName("SetPlannerField")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        item.MapPost("/close", (string itemId, PlannerCloseRequest body, HttpContext http, PlannerService planner, CancellationToken ct) =>
                ActAsync(itemId, body.DecidedBy, http, actor => planner.CloseAsync(itemId, body.NotPlanned, actor, ct)))
            .WithName("ClosePlannerIssue")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        item.MapPost("/day", (string itemId, PlannerDayRequest body, HttpContext http, PlannerService planner, CancellationToken ct) =>
                Day(body.Day) is { } day
                    ? ActAsync(itemId, body.DecidedBy, http, actor => planner.SetDayAsync(itemId, day, actor, ct))
                    : Task.FromResult<Results<Ok<PlannerActionResponse>, NotFound<PlannerActionResponse>, Conflict<PlannerActionResponse>, ValidationProblem>>(
                        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["day"] = ["day is 'today', 'not today' or 'none'."] })))
            .WithName("SetPlannerDay")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        item.MapPost("/assignee", (string itemId, PlannerAssigneeRequest body, HttpContext http, PlannerService planner, CancellationToken ct) =>
                string.IsNullOrWhiteSpace(body.Login)
                    ? Task.FromResult<Results<Ok<PlannerActionResponse>, NotFound<PlannerActionResponse>, Conflict<PlannerActionResponse>, ValidationProblem>>(Missing("login"))
                    : ActAsync(itemId, body.DecidedBy, http, actor => planner.AssignAsync(itemId, body.Login, body.Assign, actor, ct)))
            .WithName("SetPlannerAssignee")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        item.MapPost("/schedule", (string itemId, PlannerScheduleRequest body, HttpContext http, PlannerService planner, CancellationToken ct) =>
                Schedule(body.When) is { } when
                    ? ActAsync(itemId, body.DecidedBy, http, actor => planner.ScheduleAsync(itemId, when, actor, ct))
                    : Task.FromResult<Results<Ok<PlannerActionResponse>, NotFound<PlannerActionResponse>, Conflict<PlannerActionResponse>, ValidationProblem>>(
                        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["when"] = ["when is 'tomorrow', 'next week' or 'backlog'."] })))
            .WithName("SchedulePlannerItem")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        app.MapGet("/api/planner/search", (string? q, string? state, PlannerService planner) =>
                TypedResults.Ok(planner.Search(q, state?.Trim().ToLowerInvariant() switch { "open" => true, "closed" => false, _ => null })
                    .Select(i => new PlannerSearchHit(i.ItemId, i.Repository, i.Number, i.Title, i.Url, i.Type, i.Open, i.CreatedAt, i.ClosedAt, i.Fields, [.. i.Labels.Select(l => l.Name)]))
                    .ToList()))
            .WithName("SearchPlanner");

        app.MapGet("/api/planner/comments", async Task<Results<Ok<PlannerCommentsView>, NotFound, ValidationProblem>> (string? repository, int? number, PlannerService planner, CancellationToken ct) =>
                string.IsNullOrWhiteSpace(repository) || number is not > 0
                    ? Missing("repository", "number")
                    : await planner.CommentsAsync(repository.Trim(), number.Value, ct) is { } comments
                        ? TypedResults.Ok(new PlannerCommentsView(repository.Trim(), number.Value, [.. comments.Select(View)]))
                        : TypedResults.NotFound())
            .WithName("ReadPlannerComments");

        app.MapPost("/api/planner/comments", CommentAsync)
            .WithName("CommentOnPlannerIssue")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        app.MapPost("/api/planner/issues", CreateAsync)
            .WithName("CreatePlannerIssue")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        app.MapPost("/api/planner/triage", TriageAsync)
            .WithName("StartPlannerTriage")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        app.MapGet("/api/planner/analysis", AnalysisAsync).WithName("ReadPlannerAnalysis");

        return app;
    }

    /// <summary>The line a person pastes into Claude Code to work on an issue.</summary>
    public static string ExecuteLine(string repository, int number) => $"Execute github issue {repository}#{number}";

    public static PlannerCommentView View(GitHub.GitHubComment c) => new(c.Id, c.Author.Login, c.CreatedAt, c.Body, c.Url);

    /// <summary>A day word of the API: the day itself, or a later one. Null for none or an unknown word.</summary>
    public static (PlannerDay? Day, PlannerSchedule? Schedule)? DayChoice(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => null,
        "today" => (PlannerDay.Today, null),
        "not today" or "not-today" => (PlannerDay.NotToday, null),
        _ when Schedule(word) is { } s => (null, s),
        _ => (null, null),
    };

    private static PlannerSchedule? Schedule(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "tomorrow" or "next day" or "next-day" => PlannerSchedule.Tomorrow,
        "next week" or "next-week" => PlannerSchedule.NextWeek,
        "backlog" => PlannerSchedule.Backlog,
        _ => null,
    };

    private static async Task<Results<Ok<PlannerCommentView>, NotFound<string>, Conflict<string>, ValidationProblem>> CommentAsync(
        PlannerCommentRequest body, HttpContext http, PlannerService planner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Repository) || body.Number <= 0 || string.IsNullOrWhiteSpace(body.Body))
        {
            return Missing("repository", "number", "body");
        }

        var actor = ActorResolution.Resolve(http.User, body.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return NobodyNamed();
        }

        var (outcome, message, comment) = await planner.CommentAsync(body.Repository.Trim(), body.Number, body.Body, actor, ct);

        return outcome switch
        {
            PlannerActionOutcome.Done => TypedResults.Ok(View(comment!)),
            PlannerActionOutcome.NotFound => TypedResults.NotFound(message),
            _ => TypedResults.Conflict(message),
        };
    }

    public static TriageRunView View(TriageRun run) =>
        new(run.Repository, run.Number, run.State, run.RequestedBy, run.StartedAt, run.EndedAt, run.Message, run.JobName);

    public static TriageStartResponse View(TriageStart start) => new(start.Outcome, start.Message, start.Run is { } run ? View(run) : null);

    private static async Task<Results<Ok<PlannerNewIssueResponse>, NotFound<PlannerNewIssueResponse>, Conflict<PlannerNewIssueResponse>, ValidationProblem>> CreateAsync(
        PlannerNewIssueRequest body, HttpContext http, PlannerService planner, TriageRunner triage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Repository) || string.IsNullOrWhiteSpace(body.Title))
        {
            return Missing("repository", "title");
        }

        var actor = ActorResolution.Resolve(http.User, body.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return NobodyNamed();
        }

        if (DayChoice(body.Day) is { Day: null, Schedule: null })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["day"] = ["day is 'today', 'not today', 'tomorrow', 'next week', 'backlog' or empty."] });
        }

        var result = await planner.OpenIssueAsync(
            new PlannerNewIssue(body.Repository, body.Title, body.Body, body.Type, body.Fields ?? new Dictionary<string, string>()), actor, ct);

        // Its day, right after: a refusal there leaves the issue open and says so.
        if (result.ItemId is { } openedId && DayChoice(body.Day) is { } choice)
        {
            var planned = choice.Day is { } day
                ? await planner.SetDayAsync(openedId, day, actor, ct)
                : await planner.ScheduleAsync(openedId, choice.Schedule!.Value, actor, ct);

            result = result with { Message = $"{result.Message}; {planned.Message}", Snapshot = planned.Snapshot, Outcome = planned.Done ? result.Outcome : PlannerActionOutcome.Refused };
        }

        TriageStartResponse? asked = null;

        if (result.Opened is { } opened && body.Triage)
        {
            asked = View(await triage.StartAsync(opened.Repository, opened.Number, actor, ct));
        }

        var response = new PlannerNewIssueResponse(
            result.Outcome, result.Message, result.ItemId is { } id && result.Snapshot.Entry(id) is { } entry ? View(entry) : null, result.Opened?.Url, asked);

        return result.Outcome switch
        {
            PlannerActionOutcome.Done => TypedResults.Ok(response),
            PlannerActionOutcome.NotFound => TypedResults.NotFound(response),
            _ => TypedResults.Conflict(response),
        };
    }

    private static async Task<Results<Ok<TriageStartResponse>, NotFound<TriageStartResponse>, Conflict<TriageStartResponse>, ValidationProblem>> TriageAsync(
        PlannerTriageRequest body, HttpContext http, TriageRunner triage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Repository) || body.Number <= 0)
        {
            return Missing("repository", "number");
        }

        var actor = ActorResolution.Resolve(http.User, body.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return NobodyNamed();
        }

        var start = View(await triage.StartAsync(body.Repository.Trim(), body.Number, actor, ct));

        return start.Outcome switch
        {
            TriageStartOutcome.Started => TypedResults.Ok(start),
            TriageStartOutcome.NotFound => TypedResults.NotFound(start),
            _ => TypedResults.Conflict(start),
        };
    }

    private static async Task<Results<Ok<PlannerAnalysisView>, ValidationProblem>> AnalysisAsync(
        string? repository, int? number, PlannerService planner, TriageRunner triage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository) || number is not > 0)
        {
            return Missing("repository", "number");
        }

        var repo = repository.Trim();

        return TypedResults.Ok(new PlannerAnalysisView(
            repo,
            number.Value,
            triage.Run(repo, number.Value) is { } run ? View(run) : null,
            await planner.AnalysisAsync(repo, number.Value, ct),
            ExecuteLine(repo, number.Value)));
    }

    private static ValidationProblem NobodyNamed() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["decidedBy"] = ["decidedBy is required when nobody is signed in; the audit trail names a person, not a request."],
        });

    /// <summary><c>?refresh=true</c> asks GitHub now; otherwise the answer may be up to half a minute old.</summary>
    private static async Task<Ok<PlannerView>> ReadAsync(bool? refresh, PlannerService planner, TriageRunner triage, CancellationToken ct) =>
        TypedResults.Ok(View(await planner.ReadAsync(refresh == true, ct), triage.Enabled));

    public static PlannerView View(PlannerSnapshot snapshot, bool triage = false) => new(
        snapshot.State,
        snapshot.Problem,
        snapshot.ReadAt,
        snapshot.Board?.Title,
        snapshot.Board?.Url,
        snapshot.Board?.Fields ?? [],
        snapshot.Assignees,
        [.. snapshot.Entries.Select(View)],
        [.. snapshot.ClosedToday.Select(i => new PlannerClosedView(i.ItemId, i.Repository, i.Number, i.Title, i.Url, i.ClosedAt))],
        snapshot.NewIssueRepositories,
        snapshot.Board?.Types ?? [],
        triage);

    public static PlannerItemView View(PlannerEntry entry) => new(
        entry.Item.ItemId,
        entry.Item.Repository,
        entry.Item.Number,
        entry.Item.Title,
        entry.Item.Url,
        entry.Item.Type,
        entry.Reason,
        entry.EmptyFields,
        entry.Day,
        entry.CarriedOver,
        entry.Item.Fields,
        entry.Item.Assignees,
        entry.Item.ParentUrl,
        entry.Item.SubIssues,
        entry.Item.SubIssuesDone,
        entry.Item.PullRequests,
        entry.LastActivity,
        entry.Item.Body,
        entry.Item.Comments,
        [.. entry.Item.Labels.Select(l => l.Name)],
        entry.Item.CreatedAt,
        entry.DayAt,
        entry.PlannedFor);

    private static PlannerDay? Day(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "today" => PlannerDay.Today,
        "not today" or "not-today" or "nottoday" => PlannerDay.NotToday,
        "none" or "undecided" => PlannerDay.Undecided,
        _ => null,
    };

    private static async Task<Results<Ok<PlannerActionResponse>, NotFound<PlannerActionResponse>, Conflict<PlannerActionResponse>, ValidationProblem>> ActAsync(
        string itemId, string? decidedBy, HttpContext http, Func<string, Task<PlannerActionResult>> act)
    {
        var actor = ActorResolution.Resolve(http.User, decidedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["decidedBy"] = ["decidedBy is required when nobody is signed in; the audit trail names a person, not a request."],
            });
        }

        var result = await act(actor);
        var response = new PlannerActionResponse(result.Outcome, result.Message, result.Snapshot.Entry(itemId) is { } entry ? View(entry) : null);

        return result.Outcome switch
        {
            PlannerActionOutcome.Done => TypedResults.Ok(response),
            PlannerActionOutcome.NotFound => TypedResults.NotFound(response),
            _ => TypedResults.Conflict(response),
        };
    }

    private static ValidationProblem Missing(params string[] fields) =>
        TypedResults.ValidationProblem(fields.ToDictionary(f => f, f => new[] { $"{f} is required." }));
}
