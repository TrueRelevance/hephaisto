using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// What comes after taking an issue (v0.14.0): its plan, the answer to it, and what the issue is
/// told. The second half of every pass, in the same shape as the first - four statements that
/// should be true of a repository, each made true where it is not, with nothing remembered about
/// last time.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>No attempt outlives its work item.</b> An issue that was closed or taken back has its
/// open attempt cancelled, and a running Job deleted - first, because that frees the slot the
/// next statement may need.</item>
/// <item><b>Every taken work item has an attempt - and the next one, where a person asked for
/// it - or a recorded reason why not.</b> The coordinator is asked for each one that has none,
/// and for each whose newest attempt somebody asked to have followed by another
/// (<c>/replan</c>), on every pass: a cap that was reached at noon is asked about again at five
/// past, and answered in the audit trail only when the answer is a different one. A plan that
/// is made again is made with the conversation, read from the issue at that moment.</item>
/// <item><b>The issue says where its work stands.</b> One status comment per work item, edited
/// in place when its text would be a different one; and for an attempt whose plan is waiting,
/// one comment with the plan.</item>
/// <item><b>What an approver said on an issue has been heard</b>
/// (<c>GitHubIssuePoller.Answers.cs</c>). The fourth statement, and made true before the second
/// and the third: a <c>/replan</c> is then planned, and what an answer led to is on the issue,
/// in the pass that read it.</item>
/// <item><b>An issue whose attempt has ended has been asked whether it was assigned again</b>
/// (<c>GitHubIssuePoller.Assignments.cs</c>). The fifth, and made true before the second as
/// well.</item>
/// </list>
/// <para>
/// A write that GitHub refuses fails nothing but itself: the attempt stays where it is, the
/// repository's row says what could not be written, the list's tag is dropped, and the next
/// pass states the same three things again.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller
{
    /// <summary>How long after a work item ended its status comment is still put right. A day of GitHub being away.</summary>
    internal static readonly TimeSpan ClosedStatusWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// How far before the work item was taken a comment of its own is looked for. GitHub's clock
    /// and this one are two clocks.
    /// </summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    // What GitHub said a repository's default branch is, asked once per process like the
    // account's login: it changing under a running agent is a restart. Touched by the one loop.
    private readonly Dictionary<string, RepositoryBinding> githubRepositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The second half of a pass for one repository. Null when everything it called for was
    /// done; otherwise the first thing that was not, in a sentence.
    /// </summary>
    private async Task<string?> WorkAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        List<Guid> orphaned;

        await using (var scope = scopes.CreateAsyncScope())
        {
            // Registered wherever the agent is composed. Absent only where a poller is built
            // on its own, and then there is nothing that could plan an issue.
            if (scope.ServiceProvider.GetService<CodeFixCoordinator>() is null)
            {
                return null;
            }

            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
            var open = CodeFixStates.Open.ToArray();

            orphaned = await db.CodeFixAttempts.AsNoTracking()
                .Where(a => a.WorkItem != null
                    && a.WorkItem.Source == WorkItem.GitHubSource
                    && a.WorkItem.Repository == repository
                    && a.WorkItem.State != WorkItemState.Taken
                    && open.Contains(a.State))
                .Select(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        string? problem = null;

        foreach (var attemptId in orphaned)
        {
            try
            {
                await CancelAttemptAsync(attemptId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problem ??= $"an attempt of {repository} could not be cancelled: {ex.GetType().Name}";
                logger.LogError(ex, "Could not cancel code fix {AttemptId}, whose issue was taken back; retrying next interval.", attemptId);
            }
        }

        // What was said on the issues, first: a /replan that is accepted here is planned by the
        // statement below in the same pass, and what an answer led to is on the issue when the
        // comments are put right at the end of it (GitHubIssuePoller.Answers.cs).
        try
        {
            problem ??= await AnswersAsync(client, repository, bot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem ??= $"the answers on the issues of {repository} could not be read: {ex.GetType().Name}";
            logger.LogError(ex, "Could not read the answers on the issues of {Repository}; retrying next interval.", repository);
        }

        // And whether an issue whose attempt has ended was handed over again by assigning it
        // (GitHubIssuePoller.Assignments.cs) - before the plans, for the same reason.
        try
        {
            var unasked = await AssignedAgainAsync(client, repository, bot, ct).ConfigureAwait(false);
            problem ??= unasked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem ??= $"the assignments on the issues of {repository} could not be read: {ex.GetType().Name}";
            logger.LogError(ex, "Could not read the assignments on the issues of {Repository}; retrying next interval.", repository);
        }

        // Asked whatever the statements before it came to: one issue whose comments could not
        // be read must not keep every other issue of the repository from being planned.
        try
        {
            var unplanned = await PlanAsync(client, repository, bot, ct).ConfigureAwait(false);
            problem ??= unplanned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem ??= $"the work items of {repository} could not be planned: {ex.GetType().Name}";
            logger.LogError(ex, "Could not plan the work items of {Repository}; retrying next interval.", repository);
        }

        try
        {
            problem ??= await CommentAsync(client, repository, bot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem ??= $"the comments of {repository} could not be put right: {ex.GetType().Name}";
            logger.LogError(ex, "Could not put the comments of {Repository} right; retrying next interval.", repository);
        }

        return problem;
    }

    private sealed record Unplanned(Guid Id, int Number, DateTimeOffset TakenAt, long AuthorId, bool Again);

    /// <summary>
    /// The second statement: every taken work item has an attempt - and, where a person asked
    /// for another after its newest one, that other one - or a recorded reason why not.
    /// </summary>
    private async Task<string?> PlanAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        List<Unplanned> unplanned;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            unplanned = await db.WorkItems.AsNoTracking()
                .Where(w => w.Source == WorkItem.GitHubSource
                    && w.Repository == repository
                    && w.State == WorkItemState.Taken
                    && (w.ReplanAfterAttemptId != null || !db.CodeFixAttempts.Any(a => a.WorkItemId == w.Id)))
                .OrderBy(w => w.TakenAt)
                .Select(w => new Unplanned(w.Id, w.Number, w.TakenAt, w.AuthorId, w.ReplanAfterAttemptId != null))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (unplanned.Count == 0)
        {
            return null;
        }

        var (binding, unresolved) = await RepositoryAsync(client, repository, ct).ConfigureAwait(false);

        if (unresolved is not null)
        {
            // Not "plan it on main and see": a plan made on the wrong branch is a plan
            // somebody approves. GitHub is asked again on the next pass.
            return unresolved;
        }

        string? problem = null;

        foreach (var item in unplanned)
        {
            try
            {
                IReadOnlyList<CodeFixWorkItemComment>? conversation = null;

                if (item.Again)
                {
                    // Read for the plan that is about to be made, and kept with it: the
                    // attempt's request is written with its row. A read that fails starts
                    // nothing - a replan without the answers is the plan nobody asked for.
                    (conversation, var unread) = await ConversationAsync(client, repository, bot, item, ct).ConfigureAwait(false);

                    if (conversation is null)
                    {
                        problem ??= unread;
                        continue;
                    }
                }

                await using var scope = scopes.CreateAsyncScope();

                await scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>()
                    .EvaluateWorkItemAsync(item.Id, binding, repositoryListed: true, conversation, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problem ??= $"a work item of {repository} could not be planned: {ex.GetType().Name}";
                logger.LogError(ex, "Could not start a plan for work item {WorkItemId}; retrying next interval.", item.Id);
            }
        }

        return problem;
    }

    /// <summary>
    /// What was written on an issue since it was handed over, as far as a replanning Job is
    /// given it: the comments of the issue's AUTHOR and of the install's APPROVERS, by account
    /// number, oldest first - and nobody else's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nobody else's text reaches a Job.</b> An issue's comments are open to anybody with an
    /// account; the issue itself is the author's, and a plan is an approver's to decide. What a
    /// third person adds is read by people, on the issue. Hephaisto's own comments are left out
    /// by the account it writes as: its plan is handed over as <c>previous</c>, structured.
    /// </para>
    /// <para>
    /// "Since it was handed over" is the work item's <c>TakenAt</c> less
    /// <see cref="ClockSlack"/>: a comment written between the assignment and the poll that
    /// noticed it belongs to the hand-over, and GitHub's clock is not this one. One page of
    /// <see cref="GitHubClient.PageSize"/>, oldest first, which is what GitHub offers for one
    /// issue; the request builder keeps the newest and caps their size.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<CodeFixWorkItemComment>? Comments, string? Problem)> ConversationAsync(
        IGitHubClient client, string repository, string bot, Unplanned item, CancellationToken ct)
    {
        var since = item.TakenAt - ClockSlack;
        var approvers = options.Value.ApproverIds();
        var list = await client.ListCommentsAsync(repository, item.Number, since, etag: null, ct).ConfigureAwait(false);

        if (list is not { Ok: true, Value: { } comments })
        {
            return (null, $"{repository}#{item.Number}: its comments could not be read for a new plan: {list.Describe()}");
        }

        return (Conversation(comments, bot, item.AuthorId, approvers, since), null);
    }

    /// <summary>
    /// The comments a replanning Job is given, from a page of an issue's comments: the author's,
    /// the approvers', and one written in the planner (v0.15.0) - posted by the agent's account
    /// for a person with the approver role, and passed on as that person's, without the marker
    /// and the signature. Every other comment of the agent's account is left out.
    /// </summary>
    internal static IReadOnlyList<CodeFixWorkItemComment> Conversation(
        IReadOnlyList<GitHubComment> comments, string bot, long authorId, IReadOnlySet<long> approvers, DateTimeOffset since) =>
        [.. comments
            // `since` is by when a comment was last changed: an older one that was edited is not part of this hand-over.
            .Where(c => c.CreatedAt >= since)
            .Where(c => !string.IsNullOrWhiteSpace(c.Body))
            .OrderBy(c => c.Id)
            .Select(c => string.Equals(c.Author.Login, bot, StringComparison.OrdinalIgnoreCase)
                ? Planner.PlannerComment.TryRead(c.Body, out var person, out var text) && text.Length > 0
                    ? new CodeFixWorkItemComment($"{person} (in the planner)", text)
                    : null
                : c.Author.Id > 0 && (c.Author.Id == authorId || approvers.Contains(c.Author.Id))
                    ? new CodeFixWorkItemComment(c.Author.Login, c.Body)
                    : null)
            .Where(c => c is not null)
            .Select(c => c!)];

    private async Task CancelAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        // Read again, tracked: the watcher may have ended it between the pass's read and now.
        var attempt = await db.CodeFixAttempts
            .Include(a => a.WorkItem)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
            .ConfigureAwait(false);

        if (attempt is not { WorkItem: { State: not WorkItemState.Taken } item } || !attempt.State.IsOpen())
        {
            return;
        }

        await scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>()
            .CancelAsync(attempt, $"the issue was taken back: {item.StateReason ?? item.State.ToString()}", ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Cancelled code fix {AttemptId}: {Repository}#{Number} is no longer Hephaisto's.", attemptId, item.Repository, item.Number);
    }

    /// <summary>
    /// Where the code of a listed repository is, and the branch a plan is made on: the operator's
    /// <c>CodeFix:Repositories</c> entry when one names it, read every time; otherwise github.com
    /// under its own name with the default branch GitHub reports, asked once.
    /// </summary>
    /// <returns>
    /// The binding, or why there is none yet. GitHub not saying - a token that may not read the
    /// repository's own page, a repository GitHub does not show it - is <c>main</c>; GitHub not
    /// ANSWERING is nothing, and the next pass asks again.
    /// </returns>
    private async Task<(RepositoryBinding? Binding, string? Problem)> RepositoryAsync(IGitHubClient client, string repository, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CodeFixOptions>>().CurrentValue.BindingForRepository(repository) is { } configured)
            {
                return (configured, null);
            }
        }

        if (githubRepositories.TryGetValue(repository, out var known))
        {
            return (known, null);
        }

        var asked = await client.GetRepositoryAsync(repository, ct).ConfigureAwait(false);

        if (asked.Outcome is GitHubOutcome.RateLimited or GitHubOutcome.ServerError or GitHubOutcome.Unreachable)
        {
            return (null, $"the default branch of {repository} could not be read: {asked.Describe()}");
        }

        if (!asked.Ok)
        {
            logger.LogWarning(
                "GitHub does not say what the default branch of {Repository} is ({Detail}); plans for its issues are made on main. "
                + "A CodeFix:Repositories entry for it names another.",
                repository, asked.Describe());
        }

        return (githubRepositories[repository] = CodeFixOptions.GitHubRepository(repository, asked.Value?.DefaultBranch), null);
    }

    private sealed record Commentable(
        Guid Id,
        int Number,
        string Url,
        WorkItemState State,
        string? StateReason,
        string? DeclineCodes,
        string? DeclineReason,
        DateTimeOffset TakenAt,
        long? StatusCommentId,
        string? StatusCommentDigest,
        Guid? ReplanAfterAttemptId,
        string? ReplanRequestedBy);

    /// <summary>The newest attempt of a work item, how many it has had, and from when its plan comment can be on the issue.</summary>
    private sealed record Newest(IssueAttempt Attempt, int Count, DateTimeOffset? PlanReadyAt);

    /// <summary>
    /// Makes the comments of a repository's work items say what is stored. Reads before it writes
    /// anything: on a pass where nothing moved this is two queries and no request to GitHub.
    /// </summary>
    private async Task<string?> CommentAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        var closedSince = clock.UtcNow - ClosedStatusWindow;

        List<Commentable> items;
        Dictionary<Guid, Newest> attempts;
        CodeFixMode mode;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            // What is taken, and what ended a little while ago with a comment that may still
            // say it is being worked on. Never the body: this runs on every pass.
            items = await db.WorkItems.AsNoTracking()
                .Where(w => w.Source == WorkItem.GitHubSource
                    && w.Repository == repository
                    && (w.State == WorkItemState.Taken || (w.StatusCommentId != null && w.ClosedAt >= closedSince)))
                .OrderBy(w => w.TakenAt)
                .Select(w => new Commentable(
                    w.Id, w.Number, w.Url, w.State, w.StateReason, w.DeclineCodes, w.DeclineReason, w.TakenAt, w.StatusCommentId, w.StatusCommentDigest,
                    w.ReplanAfterAttemptId, w.ReplanRequestedBy))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (items.Count == 0)
            {
                return null;
            }

            var ids = items.ConvertAll(i => i.Id);

            attempts = (await db.CodeFixAttempts.AsNoTracking()
                    .Where(a => a.WorkItemId != null && ids.Contains(a.WorkItemId.Value))
                    .OrderBy(a => a.CreatedAt)
                    .ThenBy(a => a.Id)
                    .Select(a => new
                    {
                        WorkItemId = a.WorkItemId!.Value,
                        Attempt = new IssueAttempt(a.Id, a.State, a.FailureReason, a.Summary, a.ApprovedBy, a.PrUrl, a.PlanCommentId, a.Branch, a.DefaultBranch),

                        // An attempt that did not work has no plan comment: what its planner
                        // asked and noted is said on the status comment, so it is read here -
                        // for that state only. This runs on every pass.
                        Plan = a.State == CodeFixState.Failed ? a.PlanResultJson : null,
                        a.PlanReadyAt,
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .GroupBy(a => a.WorkItemId)
                .ToDictionary(g => g.Key, g => new Newest(WithWhatWasAsked(g.Last().Attempt, g.Last().Plan), g.Count(), g.Last().PlanReadyAt));

            mode = (await scope.ServiceProvider.GetRequiredService<ICodeFixSwitch>().ResolveAsync(ct).ConfigureAwait(false)).Effective;
        }

        string? problem = null;

        foreach (var item in items)
        {
            problem ??= await CommentOnAsync(client, repository, bot, item, attempts.GetValueOrDefault(item.Id), mode, ct).ConfigureAwait(false);
        }

        return problem;
    }

    /// <summary>The attempt with its planner's questions and notes, read out of the plan result it stored - when it stored one.</summary>
    private static IssueAttempt WithWhatWasAsked(IssueAttempt attempt, string? planResultJson)
    {
        if (planResultJson is null)
        {
            return attempt;
        }

        try
        {
            var plan = System.Text.Json.JsonSerializer.Deserialize<CodeFixPlanResult>(planResultJson, CodeFixContract.Json);

            return attempt with { Questions = plan?.Questions, Notes = plan?.Notes };
        }
        catch (System.Text.Json.JsonException)
        {
            // Stored by another contract. The summary and the reason are columns, and are said.
            return attempt;
        }
    }

    /// <summary>
    /// One issue: the plan comment if a plan is waiting without one, then the status comment if
    /// its text would be a different one. GitHub first, and the ids afterwards - so "the plan is
    /// on the issue", as the database tells it, is never true before the status says so too.
    /// </summary>
    private async Task<string?> CommentOnAsync(
        IGitHubClient client, string repository, string bot, Commentable item, Newest? newest, CodeFixMode mode, CancellationToken ct)
    {
        string? problem = null;
        long? planCommentId = null;
        var attempt = newest?.Attempt;
        var since = item.TakenAt - ClockSlack;

        // With the mode Off the watcher is about to cancel the attempt; a plan that says how to
        // approve it would be untrue before anybody read it.
        if (item.State == WorkItemState.Taken && mode != CodeFixMode.Off && attempt is { State: CodeFixState.PlanReady, PlanCommentId: null })
        {
            var (body, why, approvable) = await PlanBodyAsync(attempt.Id, mode, newest!.Count, ct).ConfigureAwait(false);

            if (body is null)
            {
                // It stopped waiting between the two reads. The next pass sees what it became.
                return why;
            }

            // A plan's comment cannot be older than its plan: looked for from there, so that a
            // long conversation - a page holds a hundred comments, oldest first - does not hide
            // one that was written and never recorded.
            var planSince = newest.PlanReadyAt is { } ready ? ready - ClockSlack : since;

            var written = await EnsureCommentAsync(client, repository, item.Number, bot, IssueComments.PlanMarker(attempt.Id), body, planSince, edit: false, ct).ConfigureAwait(false);

            if (written.Id is null)
            {
                problem = $"{repository}#{item.Number}: the plan could not be written on the issue: {written.Problem}";
            }
            else
            {
                planCommentId = written.Id;
                attempt = attempt with { PlanCommentId = planCommentId };

                // The two reactions an approver clicks (#298), with the plan: below it when it
                // is first read, and not a poll later. Not this statement's problem when GitHub
                // refuses - the pass that reads the plan's reactions sets what is missing, and
                // says so when it cannot.
                if (options.Value.ApproverIds().Count > 0
                    && await OfferReactionsAsync(client, repository, written.Id.Value, approvable, [], ct).ConfigureAwait(false) is { } refused)
                {
                    logger.LogInformation(
                        "{Repository}#{Number}: a reaction to click could not be set on the plan just written ({Refused}); the next pass tries again.",
                        repository, item.Number, refused);
                }
            }
        }

        // Asked for after the newest attempt, and not started yet. A request that names an
        // older attempt asks for nothing: the attempt it wanted exists.
        var askedFor = attempt is not null && item.ReplanAfterAttemptId == attempt.Id ? item.ReplanRequestedBy ?? "somebody" : null;

        var status = IssueComments.Status(new IssueStatus(
            item.Id, item.State, item.StateReason, item.DeclineCodes, item.DeclineReason, item.Url, attempt,
            Attempts: newest?.Count ?? 0,
            ReplanRequestedBy: askedFor,
            Answerable: options.Value.ApproverIds().Count > 0));
        string? digest = IssueComments.Digest(status);
        var statusCommentId = item.StatusCommentId;
        var statusWritten = false;

        if (statusCommentId is null)
        {
            // An issue that was taken back before anything was written on it is not written on now.
            if (item.State == WorkItemState.Taken)
            {
                var written = await EnsureCommentAsync(client, repository, item.Number, bot, IssueComments.StatusMarker(item.Id), status, since, edit: true, ct).ConfigureAwait(false);

                statusCommentId = written.Id;
                statusWritten = written.Id is not null;
                problem ??= written.Id is null ? $"{repository}#{item.Number}: the status comment could not be written: {written.Problem}" : null;
            }
        }
        else if (!string.Equals(digest, item.StatusCommentDigest, StringComparison.Ordinal))
        {
            var edited = await client.UpdateCommentAsync(repository, statusCommentId.Value, status, ct).ConfigureAwait(false);

            if (edited.Ok)
            {
                statusWritten = true;
            }
            else if (edited.Outcome == GitHubOutcome.NotFound)
            {
                // Somebody deleted it. Forgotten, so that the next pass writes one again - if the
                // issue is still Hephaisto's by then.
                statusCommentId = null;
                statusWritten = true;
                digest = null;
            }
            else
            {
                problem ??= $"{repository}#{item.Number}: the status comment could not be edited: {edited.Describe()}";
            }
        }

        if (planCommentId is null && !statusWritten)
        {
            return problem;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        // Two columns each, by statement: the rows are the coordinator's and the watcher's too,
        // and a tracked save from here would be this loop's reading of everything else on them.
        if (planCommentId is { } plan && attempt is not null)
        {
            await db.CodeFixAttempts.Where(a => a.Id == attempt.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PlanCommentId, plan), ct)
                .ConfigureAwait(false);
        }

        if (statusWritten)
        {
            await db.WorkItems.Where(w => w.Id == item.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.StatusCommentId, statusCommentId).SetProperty(w => w.StatusCommentDigest, digest),
                    ct)
                .ConfigureAwait(false);
        }

        return problem;
    }

    /// <summary>The plan comment's text, from the attempt as it is now - or nothing, when no plan is waiting any more.</summary>
    /// <returns>The plan comment's text, or why there is none; and whether the plan can be approved on the issue at all.</returns>
    private async Task<(string? Body, string? Problem, bool Approvable)> PlanBodyAsync(Guid attemptId, CodeFixMode mode, int ordinal, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var attempt = await db.CodeFixAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId, ct).ConfigureAwait(false);

        if (attempt is not { State: CodeFixState.PlanReady, PlanCommentId: null })
        {
            return (null, null, false);
        }

        // What the console shows of the plan, read the same way: a plan stored by an older
        // contract still has its denormalised columns.
        var view = CodeFixQueries.Plan(attempt);

        return (IssueComments.Plan(attempt, view, mode, answerable: options.Value.ApproverIds().Count > 0, ordinal), null, !attempt.NeedsCait);
    }

    /// <summary>
    /// A comment of Hephaisto's on an issue, written once. Before writing, the issue's comments
    /// since the work item was taken are read for one that carries the marker and was written by
    /// the account itself: writing and recording the id are two steps, and a process that died
    /// between them left a comment it must recognise instead of repeating.
    /// </summary>
    /// <param name="edit">
    /// Whether one that was found is made to say <paramref name="body"/>. The status comment is;
    /// a plan is never edited, and the one found is the plan that was posted.
    /// </param>
    private static async Task<(long? Id, string? Problem)> EnsureCommentAsync(
        IGitHubClient client, string repository, int number, string bot, string marker, string body, DateTimeOffset since, bool edit, CancellationToken ct)
    {
        var existing = await client.ListCommentsAsync(repository, number, since, etag: null, ct).ConfigureAwait(false);

        if (existing is not { Ok: true, Value: { } comments })
        {
            return (null, existing.Describe());
        }

        if (comments.FirstOrDefault(c => string.Equals(c.Author.Login, bot, StringComparison.OrdinalIgnoreCase)
                && c.Body.Contains(marker, StringComparison.Ordinal)) is { } own)
        {
            if (!edit || string.Equals(own.Body, body, StringComparison.Ordinal))
            {
                return (own.Id, null);
            }

            // Its own, and saying what was true before the restart.
            var edited = await client.UpdateCommentAsync(repository, own.Id, body, ct).ConfigureAwait(false);

            return edited.Ok ? (own.Id, null) : (null, edited.Describe());
        }

        var created = await client.CreateCommentAsync(repository, number, body, ct).ConfigureAwait(false);

        return created is { Ok: true, Value: { } comment } ? (comment.Id, null) : (null, created.Describe());
    }
}
