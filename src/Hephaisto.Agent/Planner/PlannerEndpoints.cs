using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.Planner;

/// <summary>What <c>GET /api/planner</c> answers: the board, and its open issues in the planner's order.</summary>
/// <param name="State"><c>Off</c>, <c>Read</c> or <c>Unreadable</c>.</param>
/// <param name="Problem">Why the newest read failed. With <c>Read</c>, the items are an older board's.</param>
public sealed record PlannerView(
    PlannerState State,
    string? Problem,
    DateTimeOffset? ReadAt,
    string? Title,
    string? Url,
    IReadOnlyList<BoardField> Fields,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<PlannerItemView> Items,
    IReadOnlyList<PlannerClosedView> ClosedToday);

/// <param name="Reason">Why the item asks for a decision, or <c>None</c>.</param>
/// <param name="Day"><c>Today</c>, <c>NotToday</c> or <c>Undecided</c>.</param>
/// <param name="Fields">The board's single-select fields that have a value: field name to option name.</param>
/// <param name="Body">The issue's description, as markdown source. Somebody else's text; null when there is none.</param>
/// <param name="Labels">Every label of the issue, the two of the day among them.</param>
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
    IReadOnlyList<string> Labels);

public sealed record PlannerClosedView(string ItemId, string Repository, int Number, string Title, string Url, DateTimeOffset? ClosedAt);

/// <param name="DecidedBy">Who, when nobody is signed in. Ignored for a signed-in request, as everywhere.</param>
public sealed record PlannerFieldRequest(string? Field, string? Option, string? DecidedBy);

public sealed record PlannerCloseRequest(bool NotPlanned, string? DecidedBy);

/// <param name="Day"><c>today</c>, <c>not today</c>, or <c>none</c> to take the decision back.</param>
public sealed record PlannerDayRequest(string? Day, string? DecidedBy);

public sealed record PlannerAssigneeRequest(string? Login, bool Assign, string? DecidedBy);

/// <param name="Item">The item as it is now. Null once it is closed: it is no longer among the open ones.</param>
public sealed record PlannerActionResponse(PlannerActionOutcome Outcome, string Message, PlannerItemView? Item);

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

        return app;
    }

    /// <summary><c>?refresh=true</c> asks GitHub now; otherwise the answer may be up to half a minute old.</summary>
    private static async Task<Ok<PlannerView>> ReadAsync(bool? refresh, PlannerService planner, CancellationToken ct) =>
        TypedResults.Ok(View(await planner.ReadAsync(refresh == true, ct)));

    public static PlannerView View(PlannerSnapshot snapshot) => new(
        snapshot.State,
        snapshot.Problem,
        snapshot.ReadAt,
        snapshot.Board?.Title,
        snapshot.Board?.Url,
        snapshot.Board?.Fields ?? [],
        snapshot.Assignees,
        [.. snapshot.Entries.Select(View)],
        [.. snapshot.ClosedToday.Select(i => new PlannerClosedView(i.ItemId, i.Repository, i.Number, i.Title, i.Url, i.ClosedAt))]);

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
        [.. entry.Item.Labels.Select(l => l.Name)]);

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
