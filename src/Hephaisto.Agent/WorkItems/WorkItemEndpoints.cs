using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>
/// <c>/api/workitems</c>: what Hephaisto was handed. Reading is behind the console's read policy
/// like <c>/api/codefixes</c> - a work item begins and ends on the issue, not here. Deciding on
/// its plan is the one write, and it is the approver policy's, exactly as for an incident's.
/// </summary>
public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/workitems", ListAsync).WithName("ListWorkItems");
        app.MapGet("/api/workitems/{id:guid}", GetAsync).WithName("GetWorkItem");

        // The same door as /api/incidents/{id}/codefix/{attemptId}/approve|deny, for an attempt
        // whose subject is a work item: the same policy, the same refusals, the same body.
        var codefix = app.MapGroup("/api/workitems/{id:guid}/codefix");

        codefix.MapPost("/{attemptId:guid}/approve", (Guid id, Guid attemptId, CodeFixDecisionRequest body, HttpContext http, CodeFixCoordinator c, CancellationToken ct)
                => CodeFixEndpoints.DecideAsync(body, http, (actor, source, authenticated, reason) => c.DecideForWorkItemAsync(id, attemptId, true, actor, source, authenticated, reason, ct)))
            .WithName("ApproveWorkItemCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        codefix.MapPost("/{attemptId:guid}/deny", (Guid id, Guid attemptId, CodeFixDecisionRequest body, HttpContext http, CodeFixCoordinator c, CancellationToken ct)
                => CodeFixEndpoints.DecideAsync(body, http, (actor, source, authenticated, reason) => c.DecideForWorkItemAsync(id, attemptId, false, actor, source, authenticated, reason, ct)))
            .WithName("DenyWorkItemCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        // What /replan on the issue does (v0.15.0): the newest attempt's plan given up, a new one asked for.
        codefix.MapPost("/{attemptId:guid}/replan", (Guid id, Guid attemptId, CodeFixDecisionRequest body, HttpContext http, WorkItemActions actions, CancellationToken ct)
                => CodeFixEndpoints.DecideAsync(body, http, (actor, source, _, _) => actions.ReplanAsync(id, attemptId, actor, source, commentId: null, ct)))
            .WithName("ReplanWorkItemCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        return app;
    }

    /// <summary>
    /// <c>?state=Taken|Done|Cancelled|any</c>, default <c>Taken</c>: what the agent holds now.
    /// History is asked for, as on <c>/api/incidents</c>.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<WorkItemView>>, ValidationProblem>> ListAsync(
        string? state, int? limit, WorkItemQueries queries, CancellationToken ct)
    {
        WorkItemState? parsed = WorkItemState.Taken;

        if (string.Equals(state, "any", StringComparison.OrdinalIgnoreCase))
        {
            parsed = null;
        }
        else if (!string.IsNullOrWhiteSpace(state))
        {
            // A number parses as an enum; "1" is not a state anybody meant.
            if (!Enum.TryParse<WorkItemState>(state, ignoreCase: true, out var s) || int.TryParse(state, out _))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = [$"'{state}' is not a WorkItemState. Use one of: {string.Join(", ", Enum.GetNames<WorkItemState>())}, or 'any'."],
                });
            }

            parsed = s;
        }

        return TypedResults.Ok(await queries.ListAsync(parsed, limit ?? 100, ct));
    }

    /// <summary>The work item with every member the list has, and <c>attempts</c>: what was tried for it, newest first.</summary>
    private static async Task<Results<Ok<WorkItemView>, NotFound>> GetAsync(
        Guid id, WorkItemQueries queries, CodeFixQueries codeFixes, CancellationToken ct) =>
        await queries.GetAsync(id, ct) is { } item
            ? TypedResults.Ok(item with { Attempts = await codeFixes.ForWorkItemAsync(id, ct) })
            : TypedResults.NotFound();
}
