using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.GitHub;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>
/// Asking for a new plan of a work item from the console (v0.15.0) - what <c>/replan</c> on the
/// issue does, for the planner's button and <c>POST /api/workitems/{id}/codefix/{attemptId}/replan</c>.
/// </summary>
/// <remarks>
/// Replanning is an explicit act, so the issue is read again here, as the poller reads it for
/// <c>/replan</c>: the new plan starts from the issue as it reads now. When GitHub cannot be
/// asked, the plan starts from the text Hephaisto took.
/// </remarks>
public sealed class WorkItemActions(CodeFixCoordinator coordinator, IServiceProvider services)
{
    /// <param name="commentId">The comment on the issue that goes with the request - the answer written in the planner - when there is one.</param>
    public async Task<CodeFixDecisionResult> ReplanAsync(
        Guid workItemId, Guid attemptId, string actor, ApprovalSource source, long? commentId, CancellationToken ct)
    {
        IssueText? fresh = null;

        if (services.GetService<IGitHubClient>() is { } client
            && await services.GetRequiredService<WorkItemQueries>().GetAsync(workItemId, ct).ConfigureAwait(false) is { } item
            && await client.GetIssueAsync(item.Repository, item.Number, ct).ConfigureAwait(false) is { Ok: true, Value: { } now })
        {
            fresh = new IssueText(now.Title, now.Body);
        }

        return await coordinator.ReplanWorkItemAsync(workItemId, attemptId, actor, source, commentId, fresh, ct).ConfigureAwait(false);
    }
}
