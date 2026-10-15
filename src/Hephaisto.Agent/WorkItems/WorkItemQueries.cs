using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>One work item, as the API shows it.</summary>
/// <param name="Body">
/// The issue's text as it was when the work item was taken - somebody else's words, verbatim.
/// Whatever renders it treats it as that.
/// </param>
/// <param name="StatusCommentId">The one comment Hephaisto edits on the issue; null until it is written.</param>
/// <param name="DeclineReason">
/// Why no plan was started when it was last asked, while there is no attempt. Asked again on
/// every pass; null once an attempt exists.
/// </param>
/// <param name="StillAssigned">
/// True for a work item that ended - its pull request was merged or closed - while its issue
/// stayed open and assigned. Such an issue is not taken again until Hephaisto was unassigned
/// (or the issue closed) and assigned again.
/// </param>
/// <param name="Attempts">
/// What was tried for it, newest first - on <c>GET /api/workitems/{id}</c>. Null in a list, whose
/// rows do not carry them: <c>GET /api/codefixes</c> rows name their <c>workItemId</c>.
/// </param>
/// <param name="ReplanRequestedBy">
/// Who asked for a new plan after the newest attempt (<c>github:&lt;login&gt;</c>), while that
/// plan has not been started - a cap, a switch. Null otherwise.
/// </param>
public sealed record WorkItemView(
    Guid Id,
    string Source,
    string Repository,
    int Number,
    string Url,
    string Title,
    string? Type,
    string AuthorLogin,
    WorkItemState State,
    string? StateReason,
    DateTimeOffset TakenAt,
    DateTimeOffset? ClosedAt,
    string Body,
    long? StatusCommentId = null,
    string? DeclineReason = null,
    bool StillAssigned = false,
    IReadOnlyList<CodeFixAttemptView>? Attempts = null,
    string? ReplanRequestedBy = null);

/// <summary>
/// What became of one attempt of a work item, for a row of the console's list: enough to say
/// where it stands and to link its page and its pull request, and nothing a model wrote.
/// </summary>
public sealed record WorkItemAttemptRef(Guid Id, CodeFixState State, string? PrUrl, int? PrNumber);

/// <summary>
/// One row of the console's work-item list: the work item, its newest attempt when it has one,
/// and - since an issue can be planned again - the ones before it, oldest first.
/// </summary>
public sealed record WorkItemWithAttempt(WorkItemView Item, WorkItemAttemptRef? Attempt, IReadOnlyList<WorkItemAttemptRef> Earlier);

/// <summary>Reads of <c>work_items</c> for the API. Never tracked, never written.</summary>
public sealed class WorkItemQueries(HephaistoDbContext db)
{
    public const int MaxLimit = 500;

    /// <summary>Newest first. <paramref name="state"/> null is every state.</summary>
    public async Task<IReadOnlyList<WorkItemView>> ListAsync(WorkItemState? state, int limit, CancellationToken ct)
    {
        var query = db.WorkItems.AsNoTracking();

        if (state is { } s)
        {
            query = query.Where(w => w.State == s);
        }

        var rows = await query
            .OrderByDescending(w => w.TakenAt)
            .ThenByDescending(w => w.Id)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(View);
    }

    /// <summary>
    /// The list with each work item's attempt beside it, for the console: one query for the
    /// rows and one for all their attempts, never one per row.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemWithAttempt>> RowsAsync(WorkItemState? state, int limit, CancellationToken ct)
    {
        var items = await ListAsync(state, limit, ct).ConfigureAwait(false);
        var ids = items.Select(i => i.Id).ToList();

        var attempts = await db.CodeFixAttempts.AsNoTracking()
            .Where(a => a.WorkItemId != null && ids.Contains(a.WorkItemId.Value))
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Select(a => new { WorkItemId = a.WorkItemId!.Value, a.Id, a.State, a.PrUrl, a.PrNumber })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Oldest first, so the last of a work item is its newest, and the ones before it are in order.
        var byItem = attempts
            .GroupBy(a => a.WorkItemId)
            .ToDictionary(g => g.Key, g => g.Select(a => new WorkItemAttemptRef(a.Id, a.State, a.PrUrl, a.PrNumber)).ToList());

        return [.. items.Select(i => byItem.TryGetValue(i.Id, out var all)
            ? new WorkItemWithAttempt(i, all[^1], all[..^1])
            : new WorkItemWithAttempt(i, null, []))];
    }

    /// <summary>The attempts of one work item, oldest first: what its issue was planned with, in order.</summary>
    public async Task<IReadOnlyList<WorkItemAttemptRef>> AttemptsAsync(Guid workItemId, CancellationToken ct) =>
        await db.CodeFixAttempts.AsNoTracking()
            .Where(a => a.WorkItemId == workItemId)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Select(a => new WorkItemAttemptRef(a.Id, a.State, a.PrUrl, a.PrNumber))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// The newest work item of one issue, with its newest attempt - what the planner's detail
    /// view offers its plan's buttons for. Null when Hephaisto was never handed the issue.
    /// </summary>
    public async Task<WorkItemWithAttempt?> FindAsync(string repository, int number, CancellationToken ct)
    {
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Repository == repository && w.Number == number)
            .OrderByDescending(w => w.TakenAt)
            .ThenByDescending(w => w.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        var attempts = await AttemptsAsync(item.Id, ct).ConfigureAwait(false);

        return new WorkItemWithAttempt(View(item), attempts.Count > 0 ? attempts[^1] : null, attempts.Count > 1 ? [.. attempts.Take(attempts.Count - 1)] : []);
    }

    public async Task<WorkItemView?> GetAsync(Guid id, CancellationToken ct) =>
        await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false) is { } item
            ? View(item)
            : null;

    public static WorkItemView View(WorkItem w) => new(
        w.Id, w.Source, w.Repository, w.Number, w.Url, w.Title, w.Type, w.AuthorLogin,
        w.State, w.StateReason, w.TakenAt, w.ClosedAt, w.Body, w.StatusCommentId, w.DeclineReason, w.StillAssigned,
        ReplanRequestedBy: w.ReplanAfterAttemptId is null ? null : w.ReplanRequestedBy);
}
