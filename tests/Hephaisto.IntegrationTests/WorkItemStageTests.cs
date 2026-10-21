using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// A code fix without an incident, against a real Postgres (v0.14.0, #246): an issue assigned to
/// Hephaisto's account gets one attempt and one plan Job, its plan is posted on the issue once,
/// and what an incident's attempt goes through - collect, approve, cancel, relaunch - a work
/// item's goes through too.
/// </summary>
/// <remarks>
/// The poller, the coordinator and the database are the real ones; GitHub is a few lists that
/// record every write, and the launcher records instead of creating Jobs - the same doubles
/// <see cref="CodeFixStageTests"/> and <see cref="GitHubIssuePollerTests"/> use, here together
/// because the claims are about the two loops meeting. Every "nothing happened" has its
/// control beside it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed partial class WorkItemStageTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private const string Repo = "octo/shop";
    private const string Bot = "hephaisto-bot";
    private const string CloneUrl = "https://github.com/octo/shop";
    private const string Body = "Open the cart with nothing in it. The total reads null where it should read 0.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- taken -> one attempt, one plan Job ----------------------------------------------------

    [Fact]
    public async Task A_taken_work_item_gets_one_attempt_and_one_plan_job_and_twice_is_once()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body, type: "Bug");
        world.GitHub.Assign(Repo, issue);

        await world.Poller().PassAsync(Ct);

        Guid workItemId, attemptId;

        await using (var db = pg.CreateContext())
        {
            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            (workItemId, attemptId) = (item.Id, attempt.Id);

            attempt.WorkItemId.Should().Be(item.Id);
            attempt.IncidentId.Should().BeNull("an attempt has exactly one subject");
            attempt.InvestigationId.Should().BeNull();
            attempt.Workload.Should().BeEmpty("an issue names a repository, not something that runs");
            attempt.State.Should().Be(CodeFixState.Planning);
            attempt.RepositoryUrl.Should().Be(CloneUrl);
            attempt.DefaultBranch.Should().Be("trunk", "the CodeFix:Repositories entry that names the repository says so");
            attempt.Branch.Should().Be(CodeFixJobSpec.BranchName(attempt.Id));
            attempt.RequestedBy.Should().Be("hephaisto/system");
            attempt.PlanJobName.Should().Be(CodeFixJobSpec.JobName(attempt.Id, CodeFixPhase.Plan));

            var launched = world.Launcher.Launched.Should().ContainSingle().Subject;
            launched.AttemptId.Should().Be(attempt.Id);
            launched.Phase.Should().Be(CodeFixPhase.Plan);

            // Contract version 2: the issue in its own element, and nothing of an incident.
            var request = JsonSerializer.Deserialize<CodeFixWorkItemRequest>(attempt.RequestJson!, CodeFixContract.Json)!;
            request.ContractVersion.Should().Be("2");
            request.Phase.Should().Be("plan");
            request.Repository.Should().Be(new CodeFixRepository(CloneUrl, "trunk", "src", attempt.Branch));
            request.WorkItem.Repository.Should().Be(Repo);
            request.WorkItem.Number.Should().Be(issue);
            request.WorkItem.Type.Should().Be("Bug");
            request.WorkItem.Body.Should().Be(Body);
            request.Plan.Should().BeNull();
            launched.Json.Should().NotContain("\"incident").And.NotContain("\"findings\"");

            // Recorded the way an incident's are: evaluated with its codes, then launched - found
            // by the work item, since there is no incident to file them under.
            var audit = await db.AuditEvents.AsNoTracking().Where(e => e.Type.StartsWith("codefix.")).ToListAsync(Ct);
            audit.Select(e => e.Type).Should().BeEquivalentTo([CodeFixCoordinator.AuditEvaluated, CodeFixCoordinator.AuditLaunched]);
            audit.Should().OnlyContain(e => e.IncidentId == null);

            using var detail = JsonDocument.Parse(audit.Single(e => e.Type == CodeFixCoordinator.AuditEvaluated).Detail!);
            detail.RootElement.GetProperty("work_item_id").GetGuid().Should().Be(item.Id);
            detail.RootElement.GetProperty("number").GetInt32().Should().Be(issue);
            detail.RootElement.GetProperty("detail").GetProperty("eligible").GetBoolean().Should().BeTrue();

            item.DeclineCodes.Should().BeNull();
        }

        // The issue is told once, in one comment.
        var status = world.GitHub.Comments.Should().ContainSingle().Subject;
        status.Body.Should().Contain("**Planning.**").And.Contain(IssueComments.StatusMarker(workItemId));
        status.Author.Should().Be(Bot);

        // And again, and again after a restart - a new poller knows nothing but the database.
        var writes = world.GitHub.Writes.Count;
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher).Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.WorkItems.CountAsync(Ct)).Should().Be(1);
            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).Id.Should().Be(attemptId);
            (await db.AuditEvents.CountAsync(e => e.Type == CodeFixCoordinator.AuditEvaluated, Ct)).Should().Be(1);
        }

        world.Launcher.Launched.Should().ContainSingle("the same list twice is still one Job");
        world.GitHub.Writes.Should().HaveCount(writes, "a pass that finds nothing changed writes nothing on the issue");
        world.GitHub.Comments.Should().ContainSingle().Which.Edits.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_result_makes_it_PlanReady_charges_the_ledger_and_is_posted_on_the_issue_once()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller().PassAsync(Ct);

        var attemptId = await world.CollectPlanAsync(cost: 1.25m);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            attempt.State.Should().Be(CodeFixState.PlanReady);
            attempt.Summary.Should().Contain("null check");
            attempt.PlanCostUsd.Should().Be(1.25m);
            attempt.PlanCommentId.Should().BeNull("the comment is the poller's to write, on its next pass");

            // The same ledger as every other spend, without an incident to file it under.
            var usage = await db.LlmUsage.AsNoTracking().SingleAsync(Ct);
            usage.CodeFixAttemptId.Should().Be(attemptId);
            usage.IncidentId.Should().BeNull();
            usage.CostUsd.Should().Be(1.25m);

            (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditPlanReady, Ct)).IncidentId.Should().BeNull();

            // A route takes these events, and the plan is in the outbox (#248): one row, for a
            // work item and for no incident, saying where it is answered. The issue is still told
            // by the poller's comment - this row is for the people a route names.
            var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync(Ct);
            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);

            delivery.Event.Should().Be(NotificationEvent.CodeFixPlanReady);
            delivery.Channel.Should().Be("webhook");
            delivery.IncidentId.Should().BeNull();
            delivery.CorrelationKey.Should().Be(CodeFixNotifier.WorkItemKey(item.Id));
            delivery.Status.Should().Be(DeliveryStatus.Pending);

            var told = delivery.Snapshot;
            told.IncidentId.Should().BeNull();
            told.WorkItemId.Should().Be(item.Id);
            told.CodeFixAttemptId.Should().Be(attemptId);
            told.Title.Should().Be("The order total is null for an empty cart", "the title is the issue's");
            told.Issue.Should().Be($"{Repo}#{issue}");
            told.IssueUrl.Should().Be($"https://github.com/{Repo}/issues/{issue}");
            told.Summary.Should().Contain("null check");
            told.Reason.Should().Contain($"{Repo}#{issue}").And.Contain("/approve").And.Contain("in the console");

            // Nothing of an incident, rather than something made up: no kind, no namespace, no
            // target, no labels, and the severity at its zero.
            told.Kind.Should().Be(default(SignalKind));
            told.Severity.Should().Be(Severity.Info);
            told.Namespace.Should().BeEmpty();
            told.Cluster.Should().BeEmpty();
            told.Target.Should().BeEmpty();
            told.Labels.Should().BeEmpty();
            told.State.Should().BeNull();
        }

        await world.Poller().PassAsync(Ct);

        long planCommentId;

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);

            var plan = world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains(IssueComments.PlanMarker(attemptId))).Subject;
            planCommentId = plan.Id;

            attempt.PlanCommentId.Should().Be(plan.Id);
            // each command in a block of its own, which GitHub gives a copy button (#298)
            plan.Body.Should().Contain("src/Startup/Endpoints.cs").And.Contain("```\n/approve\n```").And.Contain("```\n/reject <reason>\n```");
            world.GitHub.Reactions.Where(r => r.CommentId == plan.Id).Select(r => r.Content)
                .Should().BeEquivalentTo([IssueCommands.ApproveReaction, IssueCommands.RejectReaction], "the two to click are set with the plan");
            plan.Body.Should().Contain("Implementing is switched off", "this install is in Plan mode");
            plan.Edits.Should().Be(0);

            var status = world.GitHub.Comments.Single(c => c.Id == item.StatusCommentId);
            status.Body.Should().Contain("**A plan is ready**").And.Contain($"#issuecomment-{plan.Id}");
            status.Edits.Should().Be(1, "planning, then plan ready: one comment, edited in place");
            item.StatusCommentDigest.Should().Be(IssueComments.Digest(status.Body));
        }

        world.GitHub.Comments.Should().HaveCount(2, "the status and the plan, and never more");

        var writes = world.GitHub.Writes.Count;
        await world.Poller().PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher).Poller().PassAsync(Ct);

        world.GitHub.Writes.Should().HaveCount(writes);
        world.GitHub.Comments.Should().HaveCount(2);
        world.GitHub.Comments.Single(c => c.Id == planCommentId).Edits.Should().Be(0, "a plan is never edited");
    }

    [Fact]
    public async Task A_plan_that_found_no_code_to_change_ends_the_attempt_and_the_issue_is_told_what_it_found()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "How do I configure the cart?", "A question."));
        await world.Poller().PassAsync(Ct);

        await world.CollectPlanAsync(cost: 0.4m, outcome: "not_a_code_problem", summary: "This is a question about configuration, not a change to code.");
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        await using var db = pg.CreateContext();
        var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);

        attempt.State.Should().Be(CodeFixState.Failed);
        attempt.FailureReason.Should().Contain("not_a_code_problem");

        var status = world.GitHub.Comments.Should().ContainSingle("no plan is posted for an attempt that has none").Subject;
        status.Body.Should().Contain("**It did not work.**").And.Contain("not_a_code_problem")
            .And.Contain("**What it found.** This is a question about configuration, not a change to code.");

        // One attempt per work item: a failed one is not tried again by the next pass.
        world.Launcher.Launched.Should().ContainSingle();
        (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).State.Should().Be(WorkItemState.Taken);
    }

    // --- what Postgres refuses ----------------------------------------------------------------

    [Fact]
    public async Task Postgres_demands_exactly_one_subject_and_lets_one_open_attempt_per_work_item_through()
    {
        await pg.ResetAsync();
        var (workItemId, incidentId) = await SeedSubjectsAsync();

        CodeFixAttempt New(Guid? incident, Guid? workItem, CodeFixState state = CodeFixState.Planning) => new()
        {
            IncidentId = incident,
            WorkItemId = workItem,
            Workload = incident is null ? string.Empty : "shop/Deployment/checkout",
            RepositoryUrl = CloneUrl,
            Branch = "hephaisto/codefix-000000000000",
            State = state,
            CreatedAt = Now,
        };

        async Task<string> RefusedAsync(CodeFixAttempt attempt)
        {
            await using var db = pg.CreateContext();
            db.CodeFixAttempts.Add(attempt);

            var act = () => db.SaveChangesAsync(Ct);

            return (await act.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>().Which.SqlState;
        }

        // Both, and neither: the check constraint.
        (await RefusedAsync(New(incidentId, workItemId))).Should().Be(PostgresErrorCodes.CheckViolation);
        (await RefusedAsync(New(null, null))).Should().Be(PostgresErrorCodes.CheckViolation);

        // A work item that does not exist: the foreign key.
        (await RefusedAsync(New(null, Guid.CreateVersion7()))).Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        // Control: one of each is two rows.
        await using (var db = pg.CreateContext())
        {
            db.CodeFixAttempts.AddRange(New(incidentId, null), New(null, workItemId));
            await db.SaveChangesAsync(Ct);
        }

        // A second OPEN one for the same subject: the two partial unique indexes.
        (await RefusedAsync(New(null, workItemId, CodeFixState.PlanReady))).Should().Be(PostgresErrorCodes.UniqueViolation);
        (await RefusedAsync(New(incidentId, null, CodeFixState.Eligible))).Should().Be(PostgresErrorCodes.UniqueViolation);

        // ...while ended ones stand beside the open one, any number of them.
        await using (var db = pg.CreateContext())
        {
            db.CodeFixAttempts.AddRange(
                New(null, workItemId, CodeFixState.Failed),
                New(null, workItemId, CodeFixState.Denied),
                New(incidentId, null, CodeFixState.Cancelled));
            await db.SaveChangesAsync(Ct);

            (await db.CodeFixAttempts.CountAsync(Ct)).Should().Be(5);

            var indexes = await db.Database
                .SqlQuery<string>($"""select indexdef as "Value" from pg_indexes where tablename = 'code_fix_attempts' and indexname like 'ux_%'""")
                .ToListAsync(Ct);

            indexes.Should().HaveCount(2).And.OnlyContain(i => i.Contains("UNIQUE") && i.Contains("WHERE") && i.Contains("'Implementing'"));
            indexes.Should().Contain(i => i.Contains("(work_item_id)")).And.Contain(i => i.Contains("(incident_id)"));
        }
    }

    [Fact]
    public async Task Two_passes_that_both_read_no_attempt_start_one_plan()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));
        await world.Poller(withWork: false).PassAsync(Ct);

        Guid workItemId;

        await using (var db = pg.CreateContext())
        {
            workItemId = (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).Id;
        }

        var binding = new RepositoryBinding { Url = CloneUrl, DefaultBranch = "trunk" };

        // The second coordinator read "no attempt" before the first one saved: the interleaving
        // a restart in the middle of a pass produces. Made here by hand - an attempt appears
        // between this coordinator's read and its save.
        await using var racing = pg.CreateContext();
        var late = world.Coordinator(racing, beforeSave: async () =>
        {
            await using var other = pg.CreateContext();
            (await world.Coordinator(other).EvaluateWorkItemAsync(workItemId, binding, true, Ct)).Attempt.Should().NotBeNull();
        });

        var second = await late.EvaluateWorkItemAsync(workItemId, binding, true, Ct);

        second.Attempt.Should().BeNull();
        second.Verdict!.Codes.Should().Equal(CodeFixReasonCode.AttemptAlreadyOpen);

        await using var read = pg.CreateContext();
        (await read.CodeFixAttempts.CountAsync(Ct)).Should().Be(1);
        world.Launcher.Launched.Should().ContainSingle();
    }

    // --- not now ------------------------------------------------------------------------------

    [Fact]
    public async Task A_cap_that_is_reached_is_asked_about_every_pass_and_written_down_once()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var blocker = await SeedRunningIncidentAttemptAsync();
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.CountAsync(a => a.WorkItemId != null, Ct)).Should().Be(0);

            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);
            item.State.Should().Be(WorkItemState.Taken, "a decline is not the end of the work item");
            item.DeclineCodes.Should().Be(nameof(CodeFixReasonCode.ConcurrencyCapReached));
            item.DeclineReason.Should().Be("1 coder job(s) running (cap 1)");

            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditEvaluated, Ct);
            audit.Summary.Should().StartWith($"no plan for {Repo}#").And.Contain("1 coder job(s) running");
            audit.Detail.Should().Contain("ConcurrencyCapReached");
        }

        world.Launcher.Launched.Should().BeEmpty();
        world.GitHub.Comments.Should().ContainSingle().Which.Body.Should().Contain("**Waiting.** No plan has been started: 1 coder job(s) running (cap 1).");

        // Three more passes with the same answer: asked each time, recorded and said never again.
        var writes = world.GitHub.Writes.Count;

        for (var i = 0; i < 3; i++)
            await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.AuditEvents.CountAsync(e => e.Type == CodeFixCoordinator.AuditEvaluated, Ct)).Should().Be(1);
        }

        world.GitHub.Writes.Should().HaveCount(writes);
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue("a recorded reason is a pass that did what it was for");

        // The slot is free: the next pass plans it, without anybody touching the issue.
        await using (var db = pg.CreateContext())
        {
            var running = await db.CodeFixAttempts.SingleAsync(a => a.Id == blocker, Ct);
            running.State = CodeFixState.Failed;
            await db.SaveChangesAsync(Ct);
        }

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.WorkItemId != null, Ct)).State.Should().Be(CodeFixState.Planning);

            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);
            item.DeclineCodes.Should().BeNull();
            item.DeclineReason.Should().BeNull();

            (await db.AuditEvents.CountAsync(e => e.Type == CodeFixCoordinator.AuditEvaluated, Ct)).Should().Be(2);
        }

        world.Launcher.Launched.Should().ContainSingle();
        var status = world.GitHub.Comments.Should().ContainSingle().Subject;
        status.Body.Should().Contain("**Planning.**");
        status.Edits.Should().Be(1);
    }

    [Fact]
    public async Task With_the_code_fix_mode_off_no_job_starts_and_the_issue_says_so_until_it_is_turned_on()
    {
        await pg.ResetAsync();
        var world = new World(pg) { Mode = "off" };
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));

        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.CountAsync(Ct)).Should().Be(0);
            (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).DeclineCodes.Should().Be(nameof(CodeFixReasonCode.ModeOff));

            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditEvaluated, Ct);
            audit.Summary.Should().Contain("would have planned").And.Contain("the code-fix mode is Off");
        }

        world.Launcher.Launched.Should().BeEmpty();
        world.GitHub.Comments.Should().ContainSingle().Which.Body.Should().Contain("**Not planned.** The code-fix mode of this install is Off");

        world.Mode = "plan";
        await world.Poller().PassAsync(Ct);

        world.Launcher.Launched.Should().ContainSingle().Which.Phase.Should().Be(CodeFixPhase.Plan);
    }

    [Fact]
    public async Task A_repository_on_a_host_that_is_not_allowed_is_not_planned()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.Options.AllowedRepositoryHosts = ["git.internal"];
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));

        await world.Poller().PassAsync(Ct);

        await using var db = pg.CreateContext();
        (await db.CodeFixAttempts.CountAsync(Ct)).Should().Be(0);
        (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).DeclineCodes.Should().Be(nameof(CodeFixReasonCode.RepositoryHostNotAllowed));
        world.Launcher.Launched.Should().BeEmpty();
    }

    // --- where the code is ---------------------------------------------------------------------

    [Fact]
    public async Task With_no_entry_for_the_repository_it_is_on_github_on_the_branch_github_names_asked_once()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.Options.Repositories = [];
        world.GitHub.DefaultBranch = "develop";
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "first", Body));

        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "second", Body));
        await poller.PassAsync(Ct);

        await using var db = pg.CreateContext();
        var attempts = await db.CodeFixAttempts.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(Ct);

        // The second waits for the first one's slot; the branch was asked for once all the same.
        attempts.Should().ContainSingle();
        attempts[0].RepositoryUrl.Should().Be("https://github.com/octo/shop");
        attempts[0].DefaultBranch.Should().Be("develop");
        world.GitHub.RepositoryCalls.Should().Be(1, "a repository's default branch changing under a running agent is a restart");
    }

    [Fact]
    public async Task While_github_does_not_answer_about_the_branch_nothing_is_planned_on_a_guess()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.Options.Repositories = [];
        world.GitHub.FailRepository = new GitHubResult<GitHubRepository>(GitHubOutcome.ServerError, null, "HTTP 502: Bad Gateway");
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));

        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.Launcher.Launched.Should().BeEmpty("main is a guess, and a plan on the wrong branch is a plan somebody approves");
        world.Health.Polls.Should().ContainSingle().Which.Detail.Should().Contain("default branch of octo/shop could not be read");

        // GitHub says it has no such repository for this token: that is an answer, and it is main.
        world.GitHub.FailRepository = new GitHubResult<GitHubRepository>(GitHubOutcome.NotFound, null, "HTTP 404: Not Found");
        await poller.PassAsync(Ct);

        await using var db = pg.CreateContext();
        (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).DefaultBranch.Should().Be("main");
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    // --- the issue is told, also when GitHub would not listen -------------------------------------

    [Fact]
    public async Task A_comment_github_refuses_fails_nothing_but_itself_and_the_next_pass_writes_it()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));
        world.GitHub.FailWrites = GitHubOutcome.ServerError;

        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.Comments.Should().BeEmpty();
        world.Launcher.Launched.Should().ContainSingle("the plan does not wait for a comment");
        world.Health.Polls.Should().ContainSingle().Which.Should().Match<GitHubRepositoryPoll>(p =>
            !p.Succeeded && p.Detail.Contains("status comment could not be written"));

        var attemptId = await world.CollectPlanAsync(cost: 1m);
        await poller.PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            attempt.State.Should().Be(CodeFixState.PlanReady, "a write GitHub refused is not the attempt's failure");
            attempt.PlanCommentId.Should().BeNull();
            (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).StatusCommentId.Should().BeNull();
        }

        // The list's tag was dropped with every pass that left something undone: GitHub is asked
        // for the whole list again rather than answering "unchanged" about work that was not done.
        world.GitHub.Lists.Skip(1).Should().OnlyContain(l => l.ETagSent == null);

        world.GitHub.FailWrites = null;
        await poller.PassAsync(Ct);

        world.GitHub.Comments.Should().HaveCount(2);
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains(IssueComments.PlanMarker(attemptId)));
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**A plan is ready**"));
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue();

        // Complete again: the tag is kept and sent back.
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        world.GitHub.Lists[^1].ETagSent.Should().NotBeNull();
        world.GitHub.Lists[^1].Answered.Should().Be(GitHubOutcome.NotModified);
    }

    [Fact]
    public async Task A_comment_that_was_written_and_never_recorded_is_recognised_not_repeated()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "t", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller(withWork: false).PassAsync(Ct);

        Guid workItemId;

        await using (var db = pg.CreateContext())
        {
            workItemId = (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).Id;
        }

        // What a process that died between the two steps left behind: its comment on the issue,
        // and no id in the database. Beside it, somebody else's comment that carries the marker.
        var own = world.GitHub.Comment(Repo, issue, Bot, "### Hephaisto\n\n**Taken.** old text\n" + IssueComments.StatusMarker(workItemId));
        var forged = world.GitHub.Comment(Repo, issue, "passerby", "mine now " + IssueComments.StatusMarker(workItemId));

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).StatusCommentId.Should().Be(own, "its own comment, by its own account");
        }

        world.GitHub.Comments.Should().HaveCount(2, "nothing was created");
        world.GitHub.Comments.Single(c => c.Id == own).Body.Should().Contain("**Planning.**", "and it was made to say what is true now");
        world.GitHub.Comments.Single(c => c.Id == forged).Body.Should().StartWith("mine now", "a stranger's comment is never edited");

        // The same for the plan - which is adopted as it is, because a plan is never edited.
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        var posted = world.GitHub.Comment(Repo, issue, Bot, "## Hephaisto's plan for this issue\n\nas posted\n" + IssueComments.PlanMarker(attemptId));

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).PlanCommentId.Should().Be(posted);
        }

        world.GitHub.Comments.Should().HaveCount(3);
        world.GitHub.Comments.Single(c => c.Id == posted).Should().Match<StoredComment>(c => c.Edits == 0 && c.Body.Contains("as posted"));
    }

    // --- taken back -----------------------------------------------------------------------------

    [Theory]
    [InlineData("unassign", "hephaisto-bot is no longer an assignee")]
    [InlineData("close", "the issue was closed")]
    public async Task A_cancelled_work_item_cancels_its_attempt_and_deletes_the_running_job(string how, string reason)
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.Launcher.Phase = CodeFixJobPhase.Running;
        var issue = world.GitHub.Open(Repo, "t", Body);
        world.GitHub.Assign(Repo, issue);

        var poller = world.Poller();
        await poller.PassAsync(Ct);

        // Control: an incident's attempt that is open at the same time is not this loop's.
        var incidentAttempt = await SeedRunningIncidentAttemptAsync(CodeFixState.PlanReady);

        if (how == "unassign")
            world.GitHub.Unassign(Repo, issue);
        else
            world.GitHub.Close(Repo, issue);

        await poller.PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            var item = await db.WorkItems.AsNoTracking().SingleAsync(Ct);
            item.State.Should().Be(WorkItemState.Cancelled);

            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.WorkItemId == item.Id, Ct);
            attempt.State.Should().Be(CodeFixState.Cancelled);
            attempt.FailureReason.Should().Be($"the issue was taken back: {reason}");

            world.Launcher.Deleted.Should().Equal(CodeFixJobSpec.JobName(attempt.Id, CodeFixPhase.Plan));

            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditCancelled, Ct);
            audit.IncidentId.Should().BeNull();
            audit.Detail.Should().Contain(item.Id.ToString());

            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == incidentAttempt, Ct)).State.Should().Be(CodeFixState.PlanReady);
        }

        var status = world.GitHub.Comments.Should().ContainSingle().Subject;
        status.Body.Should().Contain($"**Hephaisto has let go of this issue:** {reason}.");

        // Twice is once, and nothing new is written on an issue that was taken back.
        var writes = world.GitHub.Writes.Count;
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.Launcher.Deleted.Should().ContainSingle();
        world.GitHub.Writes.Should().HaveCount(writes);

        // Handed over again: a new work item, and a plan of its own.
        if (how == "unassign")
        {
            world.GitHub.Assign(Repo, issue);
            world.Launcher.Phase = CodeFixJobPhase.Succeeded;
            await poller.PassAsync(Ct);

            await using var db = pg.CreateContext();
            (await db.WorkItems.CountAsync(Ct)).Should().Be(2);
            (await db.CodeFixAttempts.CountAsync(a => a.WorkItemId != null && a.State == CodeFixState.Planning, Ct)).Should().Be(1);
            world.GitHub.Comments.Should().HaveCount(2, "the new work item has a status comment of its own");
        }
    }

    [Fact]
    public async Task An_issue_taken_back_before_anything_was_written_on_it_is_not_written_on()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "t", Body);
        world.GitHub.Assign(Repo, issue);
        world.GitHub.FailWrites = GitHubOutcome.ServerError;

        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.Unassign(Repo, issue);
        world.GitHub.FailWrites = null;
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.Comments.Should().BeEmpty();

        await using var db = pg.CreateContext();
        (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).State.Should().Be(CodeFixState.Cancelled);
    }

    // --- the approval door ----------------------------------------------------------------------

    [Fact]
    public async Task Control_approval_through_the_work_item_door_in_Pr_mode_starts_exactly_one_implement_job()
    {
        await pg.ResetAsync();
        var world = new World(pg) { Mode = "pr" };
        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        await world.Poller().PassAsync(Ct);

        Guid workItemId;

        await using (var db = pg.CreateContext())
        {
            workItemId = (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).Id;
        }

        world.GitHub.Comments.Single(c => c.Body.Contains(IssueComments.PlanMarker(attemptId)))
            .Body.Should().NotContain("switched off", "in Pr mode an approval is taken");

        // Somebody edits the issue after its plan was written, to swap what gets implemented.
        world.GitHub.Edit(Repo, issue, "Delete the tests instead.");
        await world.Poller().PassAsync(Ct);

        // The incident's door does not open a work item's attempt, nor another work item's id.
        await using (var db = pg.CreateContext())
        {
            (await world.Coordinator(db).DecideAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.NotFound);
            (await world.Coordinator(db).DecideForWorkItemAsync(Guid.CreateVersion7(), attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.NotFound);
            (await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "hephaisto/system", ApprovalSource.Api, true, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.Forbidden);
            (await world.Coordinator(db, allowUnauthenticated: false).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Api, false, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.Forbidden);

            world.Launcher.Launched.Should().ContainSingle("every refusal above started nothing");
        }

        await using (var db = pg.CreateContext())
        {
            var result = await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct);

            result.Outcome.Should().Be(CodeFixDecisionOutcome.Done, result.Message);
            result.Message.Should().Be("approved; implementing");
        }

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            attempt.State.Should().Be(CodeFixState.Implementing);
            attempt.ApprovedBy.Should().Be("flo");
            attempt.ApprovalSource.Should().Be(ApprovalSource.Oidc);
            attempt.ImplementJobName.Should().Be(CodeFixJobSpec.JobName(attemptId, CodeFixPhase.Implement));

            world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement);

            var approved = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditApproved, Ct);
            approved.Actor.Should().Be("flo");
            approved.IncidentId.Should().BeNull();

            // Version 2 again, with the approved plan - and the issue as it was when it was taken.
            var request = JsonSerializer.Deserialize<CodeFixWorkItemRequest>(world.Launcher.Launched[1].Json, CodeFixContract.Json)!;
            request.Phase.Should().Be("implement");
            request.Plan.Should().NotBeNull();
            request.Plan!.Files.Should().Equal("src/Startup/Endpoints.cs");
            request.WorkItem.Body.Should().Be(Body, "an edit after the snapshot changes nothing that is implemented");
            request.Repository.Branch.Should().Be(attempt.Branch);
        }

        // A second answer finds Implementing and is a conflict, not a second Job.
        await using (var db = pg.CreateContext())
        {
            (await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.Conflict);
            world.Launcher.Launched.Should().HaveCount(2);
        }

        await world.Poller().PassAsync(Ct);
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**Implementing.** flo approved the plan."));

        // The pull request: believed only on the attempt's own repository and branch, and then
        // the issue is told where it is.
        await world.CollectImplementAsync(attemptId, $"{CloneUrl}/pull/7");
        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            attempt.State.Should().Be(CodeFixState.PrOpened);
            attempt.PrUrl.Should().Be($"{CloneUrl}/pull/7");
            (await db.LlmUsage.AsNoTracking().CountAsync(u => u.CodeFixAttemptId == attemptId && u.IncidentId == null, Ct)).Should().Be(2);
        }

        world.GitHub.Comments.Should().HaveCount(2);
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"**A pull request is open:** {CloneUrl}/pull/7"));
    }

    [Fact]
    public async Task A_pull_request_on_another_repository_is_not_believed_for_a_work_item_either()
    {
        await pg.ResetAsync();
        var world = new World(pg) { Mode = "pr" };
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));
        await world.Poller().PassAsync(Ct);
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        var workItemId = await SingleWorkItemIdAsync();

        await using (var db = pg.CreateContext())
        {
            await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct);
        }

        await world.CollectImplementAsync(attemptId, "https://github.com/octo/another/pull/7");

        await using var read = pg.CreateContext();
        var attempt = await read.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
        attempt.State.Should().Be(CodeFixState.Failed);
        attempt.FailureReason.Should().Contain("contract violation").And.Contain("not on the mapped repository");
        attempt.PrUrl.Should().BeNull();
    }

    [Fact]
    public async Task Approval_is_refused_below_Pr_and_once_the_issue_was_taken_back_and_a_denial_keeps_its_reason()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "t", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        var workItemId = await SingleWorkItemIdAsync();

        // Plan mode: the plan stays waiting.
        await using (var db = pg.CreateContext())
        {
            var refused = await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct);

            refused.Outcome.Should().Be(CodeFixDecisionOutcome.ModeRefused);
            refused.Message.Should().Contain("approval needs code-fix mode Pr");
        }

        // Pr mode, and the issue was taken back a moment ago: the work item is cancelled, its
        // attempt not yet - the pass that cancels it has not run.
        world.Mode = "pr";

        await using (var db = pg.CreateContext())
        {
            var item = await db.WorkItems.SingleAsync(Ct);
            item.State = WorkItemState.Cancelled;
            item.StateReason = "the issue was closed";
            item.ClosedAt = Now;
            await db.SaveChangesAsync(Ct);

            var refused = await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct);

            refused.Outcome.Should().Be(CodeFixDecisionOutcome.Conflict);
            refused.Message.Should().Contain("no longer Hephaisto's").And.Contain("the issue was closed");

            item.State = WorkItemState.Taken;
            item.StateReason = null;
            item.ClosedAt = null;
            await db.SaveChangesAsync(Ct);
        }

        world.Launcher.Launched.Should().ContainSingle("only the plan Job ran");

        // The other answer.
        await using (var db = pg.CreateContext())
        {
            var denied = await world.Coordinator(db).DecideForWorkItemAsync(
                workItemId, attemptId, false, "flo", ApprovalSource.Api, false, "not this way: the null is the caller's", Ct);

            denied.Outcome.Should().Be(CodeFixDecisionOutcome.Done);
        }

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);
            attempt.State.Should().Be(CodeFixState.Denied);
            attempt.FailureReason.Should().Be("not this way: the null is the caller's");

            (await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct))
                .Outcome.Should().Be(CodeFixDecisionOutcome.Conflict, "a refused plan is not brought back");
        }

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**The plan was rejected** by flo: not this way: the null is the caller's."));
        world.Launcher.Launched.Should().ContainSingle("and a denied work item is not planned a second time");
    }

    // --- the watcher's other paths -----------------------------------------------------------------

    [Fact]
    public async Task An_attempt_that_never_got_its_job_is_relaunched_and_a_plan_nobody_answered_expires()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));
        await world.Poller(withWork: false).PassAsync(Ct);
        var workItemId = await SingleWorkItemIdAsync();

        Guid attemptId;

        // The row a process left that died between saving the attempt and creating its Job.
        await using (var db = pg.CreateContext())
        {
            var stranded = new CodeFixAttempt { WorkItemId = workItemId, RepositoryUrl = CloneUrl, DefaultBranch = "trunk", CreatedAt = Now.AddMinutes(-5) };
            stranded.Branch = CodeFixJobSpec.BranchName(stranded.Id);
            attemptId = stranded.Id;
            db.CodeFixAttempts.Add(stranded);
            await db.SaveChangesAsync(Ct);
        }

        await world.Poller().PassAsync(Ct);
        world.Launcher.Launched.Should().BeEmpty("it has an attempt; a second one is not the answer");

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(Ct);
            await world.Coordinator(db).RelaunchAsync(attempt, Ct);
        }

        var relaunched = world.Launcher.Launched.Should().ContainSingle().Subject;
        relaunched.AttemptId.Should().Be(attemptId);
        relaunched.Phase.Should().Be(CodeFixPhase.Plan);
        relaunched.Json.Should().Contain("\"work_item\"");

        await world.CollectPlanAsync(cost: 1m);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(Ct);
            await world.Coordinator(db).ExpireAsync(attempt, Ct);
        }

        await world.Poller().PassAsync(Ct);

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).State.Should().Be(CodeFixState.Expired);
            (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditExpired, Ct)).IncidentId.Should().BeNull();
        }

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**The plan expired.**"));
    }

    [Fact]
    public async Task A_forged_result_for_a_work_item_fails_the_attempt_and_is_charged_the_cap()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        world.GitHub.Assign(Repo, world.GitHub.Open(Repo, "t", Body));
        await world.Poller().PassAsync(Ct);

        world.Launcher.Log = CodeFixResultParser.Frame(PlanJson(Guid.CreateVersion7(), 0.01m, "planned", "forged"));

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(Ct);
            await world.Coordinator(db).CollectAsync(attempt, Ct);
        }

        await using var read = pg.CreateContext();
        var failed = await read.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);

        failed.State.Should().Be(CodeFixState.Failed);
        failed.FailureReason.Should().Contain("contract violation").And.Contain("names attempt");
        (await read.LlmUsage.AsNoTracking().SingleAsync(Ct)).Should().Match<LlmUsageRecord>(u => u.CostUsd == 5m && u.IncidentId == null);
    }

    // --- the surfaces that knew only incidents -----------------------------------------------------

    [Fact]
    public async Task The_lists_show_a_work_items_attempt_beside_an_incidents_each_with_its_own_subject()
    {
        await pg.ResetAsync();
        var world = new World(pg);
        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        await world.Poller().PassAsync(Ct);
        var workItemId = await SingleWorkItemIdAsync();
        var incidentAttempt = await SeedRunningIncidentAttemptAsync(CodeFixState.Failed);

        await using var db = pg.CreateContext();
        var queries = new CodeFixQueries(db, world.Switch, world.OptionsMonitor, new OptionsStub<AuthOptions>(new AuthOptions()));

        // GET /api/codefixes: both, each with its own subject and neither with the other's.
        var rows = await queries.ListAsync(null, 100, Ct);
        var mine = rows.Should().ContainSingle(r => r.Id == attemptId).Subject;

        mine.WorkItemId.Should().Be(workItemId);
        mine.IncidentId.Should().BeNull();
        mine.IncidentTitle.Should().BeEmpty();
        mine.Issue.Should().Be($"{Repo}#{issue}");
        mine.IssueUrl.Should().Be($"https://github.com/{Repo}/issues/{issue}");
        mine.State.Should().Be(CodeFixState.PlanReady);
        mine.Files.Should().Equal("src/Startup/Endpoints.cs");
        mine.PlanCommentId.Should().NotBeNull();
        mine.Workload.Should().BeEmpty();

        var theirs = rows.Should().ContainSingle(r => r.Id == incidentAttempt).Subject;
        theirs.IncidentId.Should().NotBeNull();
        theirs.WorkItemId.Should().BeNull();
        theirs.Issue.Should().BeNull();

        // As JSON: the names the suite and a client read.
        var json = JsonSerializer.Serialize(mine, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain($"\"workItemId\":\"{workItemId}\"").And.Contain("\"incidentId\":null").And.Contain("\"planCommentId\":");

        // GET /api/workitems/{id}: its attempts, newest first.
        (await queries.ForWorkItemAsync(workItemId, Ct)).Should().ContainSingle().Which.Id.Should().Be(attemptId);
        (await queries.ForWorkItemAsync(Guid.CreateVersion7(), Ct)).Should().BeEmpty();

        var view = WorkItemQueries.View(await db.WorkItems.AsNoTracking().SingleAsync(Ct));
        view.StatusCommentId.Should().NotBeNull();
        view.Attempts.Should().BeNull("a list's rows do not carry them");

        // The counts count it: a plan is waiting for somebody.
        (await queries.CountsAsync(Ct)).AwaitingApproval.Should().Be(1);

        // GET /api/codefixes/{id}: one attempt by its own id, with what it is for.
        var detail = (await queries.AttemptAsync(attemptId, Ct))!;
        detail.Attempt.Id.Should().Be(attemptId);
        detail.WorkItem!.Id.Should().Be(workItemId);
        detail.WorkItem.Title.Should().Be("The order total is null for an empty cart");
        detail.WorkItem.AuthorLogin.Should().NotBeNullOrEmpty();

        var incidentDetail = (await queries.AttemptAsync(incidentAttempt, Ct))!;
        incidentDetail.WorkItem.Should().BeNull();
        incidentDetail.Attempt.IncidentId.Should().NotBeNull();
        incidentDetail.Attempt.IncidentTitle.Should().NotBeEmpty();
        (await queries.AttemptAsync(Guid.CreateVersion7(), Ct)).Should().BeNull();

        // The console's work-item list: the row with its attempt beside it.
        var listed = (await new WorkItemQueries(db).RowsAsync(null, 100, Ct)).Should().ContainSingle().Subject;
        listed.Item.Id.Should().Be(workItemId);
        listed.Attempt.Should().Be(new WorkItemAttemptRef(attemptId, CodeFixState.PlanReady, null, null));

        // MCP list_code_fixes and get_code_fix (#248): both kinds, each row naming its own subject.
        var reader = McpGiven.Reader(pg, Now);
        var page = await reader.CodeFixesAsync(null, null, null, null, null, 50, null, Ct);

        page.CodeFixes.Select(r => r.Id).Should().BeEquivalentTo([attemptId, incidentAttempt]);

        var mcpMine = page.CodeFixes.Single(r => r.Id == attemptId);
        mcpMine.IncidentId.Should().BeNull();
        mcpMine.WorkItemId.Should().Be(workItemId);
        mcpMine.Issue.Should().Be($"{Repo}#{issue}");
        mcpMine.IssueUrl!.Value.Should().Be($"https://github.com/{Repo}/issues/{issue}");
        mcpMine.Workload.Should().BeNull("an issue names no workload");

        var mcpTheirs = page.CodeFixes.Single(r => r.Id == incidentAttempt);
        mcpTheirs.IncidentId.Should().NotBeNull();
        mcpTheirs.WorkItemId.Should().BeNull();
        mcpTheirs.Issue.Should().BeNull();

        // The filters still narrow, for both kinds.
        (await reader.CodeFixesAsync("PlanReady", null, null, null, null, 50, null, Ct)).CodeFixes.Should().ContainSingle().Which.Id.Should().Be(attemptId);
        (await reader.CodeFixesAsync(null, "octo/shop", null, null, null, 50, null, Ct)).CodeFixes.Select(r => r.Id).Should().Contain(attemptId);
        (await reader.CodeFixesAsync(null, "no/such-repository", null, null, null, 50, null, Ct)).CodeFixes.Should().BeEmpty();
        (await reader.CodeFixesAsync(null, null, null, mcpTheirs.IncidentId, null, 50, null, Ct)).CodeFixes.Should().ContainSingle().Which.Id.Should().Be(incidentAttempt);

        // get_code_fix by attempt id, for both.
        (await reader.CodeFixAsync(incidentAttempt, Ct)).Attempt.Id.Should().Be(incidentAttempt);

        var mcpDetail = await reader.CodeFixAsync(attemptId, Ct);
        mcpDetail.Attempt.WorkItemId.Should().Be(workItemId);
        mcpDetail.IssueTitle!.Value.Should().StartWith("<untrusted-evidence>").And.Contain("The order total is null for an empty cart");
        mcpDetail.Note.Should().Contain("comment on the issue");
        mcpDetail.Next.Should().ContainSingle().Which.Should().StartWith($"get_work_item {{\"id\":\"{workItemId}\"}}");

        var asJson = McpAnswer.Of(mcpDetail);
        asJson.Should().Contain($"\"workItemId\":\"{workItemId}\"").And.Contain($"\"issue\":\"{Repo}#{issue}\"").And.NotContain("\"incidentId\"");
        McpAnswer.Of(mcpTheirs).Should().Contain("\"incidentId\"").And.NotContain("\"workItemId\"").And.NotContain("\"issue\"");

        // The Teams board: an incident's card finds its own attempt beside one that has no incident.
        var incidentId = (await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == incidentAttempt, Ct)).IncidentId!.Value;
        var card = (await new TeamsBotIncidents(db).ByIdAsync([incidentId], Ct))[incidentId];

        card.CodeFix.Should().Be(CodeFixState.Failed);
    }

    // ------------------------------------------------------------------------------------------

    private async Task<Guid> SingleWorkItemIdAsync()
    {
        await using var db = pg.CreateContext();
        return (await db.WorkItems.AsNoTracking().SingleAsync(Ct)).Id;
    }

    private async Task<(Guid WorkItemId, Guid IncidentId)> SeedSubjectsAsync()
    {
        await using var db = pg.CreateContext();

        var item = new WorkItem
        {
            Repository = Repo,
            Number = 7,
            NodeId = "I_7",
            Url = $"https://github.com/{Repo}/issues/7",
            Title = "issue 7",
            AuthorLogin = "reporter",
            AuthorId = 3003,
            Body = Body,
            TakenAt = Now,
            UpdatedAt = Now,
        };
        var incident = Incident();

        db.WorkItems.Add(item);
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(Ct);

        return (item.Id, incident.Id);
    }

    /// <summary>An incident's attempt, as the incident path leaves one: here to hold a slot, or to be left alone.</summary>
    private async Task<Guid> SeedRunningIncidentAttemptAsync(CodeFixState state = CodeFixState.Planning)
    {
        await using var db = pg.CreateContext();
        var incident = Incident();
        db.Incidents.Add(incident);

        var attempt = new CodeFixAttempt
        {
            IncidentId = incident.Id,
            Workload = "shop/Deployment/checkout",
            RepositoryUrl = "https://github.com/octo/checkout",
            Branch = "hephaisto/codefix-00000000aaaa",
            State = state,
            PlanJobName = "codefix-00000000aaaa-plan",
            CreatedAt = Now.AddMinutes(-1),
            PlanStartedAt = Now.AddMinutes(-1),
            PlanReadyAt = state == CodeFixState.PlanReady ? Now : null,
        };

        db.CodeFixAttempts.Add(attempt);
        await db.SaveChangesAsync(Ct);

        return attempt.Id;
    }

    private static Incident Incident() => new()
    {
        CorrelationKey = "shop/Deployment/checkout/" + Guid.NewGuid().ToString("N"),
        Title = "CrashLoopBackOff on checkout",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        State = IncidentState.Escalated,
        EscalationReason = EscalationReason.NoPlanProduced,
        Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc", OwnerKind = "Deployment", OwnerName = "checkout" },
        OpenedAt = Now,
        LastSignalAt = Now,
    };

    private static string PlanJson(Guid attemptId, decimal cost, string outcome, string summary, string[]? questions = null, string[]? notes = null) => JsonSerializer.Serialize(new CodeFixPlanResult
    {
        AttemptId = attemptId,
        Outcome = outcome,
        Summary = summary,
        RootCause = "src/Startup/Endpoints.cs:14 dereferences a null list.",
        Confidence = 0.9,
        Files = ["src/Startup/Endpoints.cs"],
        Steps = ["Treat a null list as empty."],
        Verification = new CodeFixVerification { Level = "tests", NotVerifiable = [] },
        NeedsCait = false,
        Notes = notes ?? [],
        Questions = questions,
        AnalysedRef = "583b1e5b75ad0123456789abcdef0123456789ab",
        ContextSha = null,
        CostUsd = cost,
        SessionId = null,
        Error = null,
        DeniedToolCalls = [],
    }, CodeFixContract.Json);

    private static string ImplementJson(Guid attemptId, string branch, string prUrl, int? prNumber = 7) => JsonSerializer.Serialize(new CodeFixImplementResult
    {
        AttemptId = attemptId,
        Outcome = "pr_opened",
        Branch = branch,
        PrUrl = prUrl,
        PrNumber = prNumber,
        BaseCommit = null,
        Files = ["src/Startup/Endpoints.cs"],
        BuildPassed = true,
        TestsPassed = true,
        LogTail = string.Empty,
        Deviations = [],
        CostUsd = 2m,
        SessionId = null,
        Error = null,
        DeniedToolCalls = [],
    }, CodeFixContract.Json);

    /// <summary>
    /// Everything a pass needs, composed the way the agent composes it: a poller whose scopes
    /// hold the real coordinator over the real database, with GitHub and the cluster recorded.
    /// </summary>
    private sealed class World(PostgresFixture pg, FakeGitHub? github = null, RecordingLauncher? launcher = null)
    {
        public FakeGitHub GitHub { get; } = github ?? new FakeGitHub();

        public RecordingLauncher Launcher { get; } = launcher ?? new RecordingLauncher();

        public GitHubHealth Health { get; } = new();

        public string Mode { get; set; } = "plan";

        /// <summary>Who may answer a plan on the issue, by account number. <c>maintainer</c> is 1001.</summary>
        public List<string> Approvers { get; set; } = ["1001"];

        /// <summary>The agent's emergency stop, as the code-fix switch sees it.</summary>
        public bool EmergencyStop { get; set; }

        /// <summary>The agent's runaway latch.</summary>
        public bool Latched { get; set; }

        public CodeFixOptions Options { get; } = new()
        {
            Image = "hephaisto/coder:test",
            Repositories =
            [
                // The first entry that names octo/shop wins; the second is the same repository on another branch.
                new RepositoryBinding { Workload = "shop/Deployment/cart", Url = CloneUrl, DefaultBranch = "trunk", Path = "src" },
                new RepositoryBinding { Workload = "shop/Deployment/cart-worker", Url = CloneUrl + ".git", DefaultBranch = "release" },
            ],
            AllowedRepositoryHosts = ["github.com"],
            EligibleCategories = ["application"],
            AllowUnauthenticatedApproval = true,
        };

        public ICodeFixSwitch Switch => new LiveSwitch(this);

        public IOptionsMonitor<CodeFixOptions> OptionsMonitor => new OptionsStub<CodeFixOptions>(Options);

        public CodeFixCoordinator Coordinator(HephaistoDbContext db, bool allowUnauthenticated = true, Func<Task>? beforeSave = null)
        {
            var clock = new FixedClock(Now);
            var options = Options;

            if (!allowUnauthenticated)
            {
                options = new CodeFixOptions
                {
                    Image = Options.Image,
                    Repositories = Options.Repositories,
                    AllowedRepositoryHosts = Options.AllowedRepositoryHosts,
                    AllowUnauthenticatedApproval = false,
                };
            }

            var notifications = new OptionsStub<NotificationOptions>(new NotificationOptions
            {
                BaseUrl = "http://hephaisto.test",
                Webhook = new HttpChannelOptions { Url = "http://receiver.test/hook" },
                Routes = [new NotificationRoute { Channel = "webhook", Events = [NotificationEvent.CodeFixPlanReady, NotificationEvent.CodeFixPrOpened, NotificationEvent.CodeFixFailed] }],
            });
            var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
            var monitor = new OptionsStub<CodeFixOptions>(options);

            return new CodeFixCoordinator(
                db,
                new AuditRepository(db, clock),
                Switch,
                Launcher,
                new NullWorkloadImageReader(),
                new CodeFixRequestBuilder(monitor),
                new CodeFixStateMachine(clock),
                new CodeFixNotifier(db, notifications, new OptionsStub<GitHubOptions>(new GitHubOptions { Approvers = Approvers }), new NullNotifier(), clock, NullLogger<CodeFixNotifier>.Instance),
                new CodeFixMetrics(meters),
                beforeSave is null ? new NullGlobalLlmBudget() : new InterleavingBudget(beforeSave),
                new NullGrafanaAnnotator(),
                monitor,
                new OptionsStub<IngestOptions>(new IngestOptions()),
                clock,
                NullLogger<CodeFixCoordinator>.Instance);
        }

        /// <param name="withWork">False: a poller that only takes issues, as it was before there was anything to plan them.</param>
        public GitHubIssuePoller Poller(bool withWork = true)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => pg.CreateContext());
            services.AddSingleton<IGitHubClient>(GitHub);
            services.AddMetrics();

            if (withWork)
            {
                services.AddSingleton(OptionsMonitor);
                services.AddSingleton(Switch);
                services.AddScoped(sp => Coordinator(sp.GetRequiredService<HephaistoDbContext>()));
            }

            var provider = services.BuildServiceProvider();

            return new GitHubIssuePoller(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(new GitHubOptions
                {
                    Enabled = true,
                    Token = "ghp_notARealTokenNotARealToken1234567890",
                    BotLogin = Bot,
                    Repositories = [Repo],
                    Approvers = Approvers,
                }),
                new ObserveKillSwitch(),
                Health,
                new GitHubMetrics(provider.GetRequiredService<IMeterFactory>()),
                new FixedClock(Now),
                NullLogger<GitHubIssuePoller>.Instance);
        }

        /// <summary>The plan Job finished and the watcher's loop collects it.</summary>
        public async Task<Guid> CollectPlanAsync(
            decimal cost, string outcome = "planned", string summary = "Endpoints.Map needs a null check.", string[]? questions = null, string[]? notes = null)
        {
            await using var db = pg.CreateContext();
            var attempt = await db.CodeFixAttempts.SingleAsync(a => a.WorkItemId != null && a.State == CodeFixState.Planning, Ct);

            Launcher.Log = CodeFixResultParser.Frame(PlanJson(attempt.Id, cost, outcome, summary, questions, notes));
            await Coordinator(db).CollectAsync(attempt, Ct);

            return attempt.Id;
        }

        /// <param name="prBody">What the publish role printed as the pull request's description, in its block before the result.</param>
        public async Task CollectImplementAsync(Guid attemptId, string prUrl, string? prBody = null, int? prNumber = 7)
        {
            await using var db = pg.CreateContext();
            var attempt = await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct);

            Launcher.Log = (prBody is null ? string.Empty : CodeFixResultParser.FramePrBody(prBody))
                + CodeFixResultParser.Frame(ImplementJson(attemptId, attempt.Branch, prUrl, prNumber));
            await Coordinator(db).CollectAsync(attempt, Ct);
        }

        private sealed class LiveSwitch(World world) : ICodeFixSwitch
        {
            public Task<CodeFixModeResolution> ResolveAsync(CancellationToken ct) => Task.FromResult(CodeFixModeResolver.Resolve(
                [CodeFixModeResolver.Parse("env:CodeFix__Mode", world.Mode)],
                ModeResolver.Resolve(
                [
                    ModeResolver.Parse("env:HEPHAISTO_MODE", "Observe"),
                    ModeResolver.Parse("configmap:killSwitch", world.EmergencyStop ? "Observe" : null),
                    ModeResolver.Parse("db:agent_mode", world.Latched ? "Observe" : null),
                ]),
                "configmap:killSwitch",
                "db:agent_mode"));
        }
    }

    /// <summary>
    /// The global budget is the last thing the coordinator reads before it judges and saves, so
    /// a call here is "between the read and the save" - where another process gets in.
    /// </summary>
    private sealed class InterleavingBudget(Func<Task> beforeSave) : IGlobalLlmBudget
    {
        public async Task<GlobalBudgetVerdict> CheckAsync(Guid? incidentId, CancellationToken ct)
        {
            await beforeSave();
            return GlobalBudgetVerdict.Allow;
        }

        public Task RecordAsync(Guid incidentId, long inputTokens, long outputTokens, decimal costUsd, CancellationToken ct) => Task.CompletedTask;

        public void Enlist(Guid incidentId, Guid? investigationId, long inputTokens, long outputTokens, decimal costUsd)
        {
        }
    }

    private sealed class ObserveKillSwitch : IKillSwitch
    {
        private static readonly ModeArm Arm = ModeResolver.Parse("env:HEPHAISTO_MODE", "Observe");

        public IReadOnlyList<ModeArm> ExternalArms => [Arm];

        public ModeResolution External => ModeResolver.Resolve(Arm);

        public Task<ModeResolution> ResolveAsync(CancellationToken ct) => Task.FromResult(External);
    }

    private sealed class RecordingLauncher : ICodeFixJobLauncher
    {
        public List<(Guid AttemptId, CodeFixPhase Phase, string Json)> Launched { get; } = [];

        public List<string> Deleted { get; } = [];

        public string? Log { get; set; }

        public CodeFixJobPhase Phase { get; set; } = CodeFixJobPhase.Succeeded;

        public bool IsAvailable => true;

        public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct)
        {
            Launched.Add((attempt.Id, phase, requestJson));
            return Task.FromResult(CodeFixJobSpec.JobName(attempt.Id, phase));
        }

        public Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct) =>
            Task.FromResult(new CodeFixJobObservation(Phase, null));

        public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct) => Task.FromResult(Log);

        public Task DeleteAsync(string jobName, CancellationToken ct)
        {
            Deleted.Add(jobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A reaction on a comment: which of GitHub's eight, and whose.</summary>
    private sealed record StoredReaction(long Id, long CommentId, string Content, string Author, long AuthorId);

    private sealed record StoredComment(long Id, string Repository, int Number, string Author)
    {
        /// <summary>The account's number: what an approver list names. The bot's is 9001; anybody else's is 2002 unless said.</summary>
        public long AuthorId { get; init; } = Author == Bot ? 9001 : 2002;

        public string Body { get; set; } = string.Empty;

        public int Edits { get; set; }

        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>A pull request as GitHub would answer it. One nobody set is an open draft.</summary>
    private sealed record StoredPull(string State = "open", bool Merged = false);

    /// <summary>
    /// GitHub, as far as a pass can tell: issues with assignees and a list whose tag changes
    /// when the list does, comments that can be written and edited and that count their edits,
    /// a repository with a default branch - and a record of every write.
    /// </summary>
    private sealed class FakeGitHub : IGitHubClient
    {
        // The planner's calls (v0.15.0). The poller makes none of them: each is a person's click.
        public Task<GitHubResult<Hephaisto.Core.Planning.Board>> GetProjectAsync(string owner, int number, CancellationToken ct) =>
            throw new NotSupportedException("the poller does not read a project");

        public Task<GitHubResult<bool>> SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct) =>
            throw new NotSupportedException("the poller sets no field");

        public Task<GitHubResult<bool>> CloseIssueAsync(string issueId, bool notPlanned, CancellationToken ct) =>
            throw new NotSupportedException("the poller closes nothing");

        public Task<GitHubResult<bool>> AddLabelAsync(string repository, int number, string label, CancellationToken ct) =>
            throw new NotSupportedException("the poller labels nothing");

        public Task<GitHubResult<bool>> RemoveLabelAsync(string repository, int number, string label, CancellationToken ct) =>
            throw new NotSupportedException("the poller labels nothing");

        public Task<GitHubResult<bool>> AddAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            throw new NotSupportedException("the poller assigns nobody");

        public Task<GitHubResult<bool>> RemoveAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            throw new NotSupportedException("the poller assigns nobody");

        public Task<GitHubResult<bool>> SetProjectDateAsync(string projectId, string itemId, string fieldId, DateOnly? date, CancellationToken ct) =>
            throw new NotSupportedException("the poller plans no day");

        public Task<GitHubResult<GitHubIssue>> CreateIssueAsync(string repository, string title, string? body, string? type, CancellationToken ct) =>
            throw new NotSupportedException("the poller opens no issue");

        public Task<GitHubResult<GitHubIssue>> UpdateIssueAsync(string repository, int number, string? title, string? body, CancellationToken ct) =>
            throw new NotSupportedException("the poller edits no issue");

        public Task<GitHubResult<string>> AddProjectItemAsync(string projectId, string contentId, CancellationToken ct) =>
            throw new NotSupportedException("the poller reads no project");

        public Task<GitHubResult<IReadOnlyList<GitHubLabel>>> ListLabelsAsync(string repository, CancellationToken ct) =>
            throw new NotSupportedException("the poller reads no label list");

        private sealed class Stored
        {
            public required int Number { get; init; }
            public required string Title { get; init; }
            public string? Body { get; set; }
            public string State { get; set; } = "open";
            public bool Assigned { get; set; }
            public string? Type { get; init; }
            public int Version { get; set; }
        }

        private readonly Dictionary<(string Repository, int Number), Stored> issues = [];
        private int nextIssue = 100;
        private long nextComment = 1791308488000;

        public List<StoredComment> Comments { get; } = [];

        /// <summary>Every write the agent made: created or edited, on which issue.</summary>
        public List<(string Kind, int Number, long Id)> Writes { get; } = [];

        public List<(string? ETagSent, GitHubOutcome Answered)> Lists { get; } = [];

        public GitHubOutcome? FailWrites { get; set; }

        public string? DefaultBranch { get; set; } = "main";

        public GitHubResult<GitHubRepository>? FailRepository { get; set; }

        public int RepositoryCalls { get; private set; }

        public int Open(string repository, string title, string? body, string? type = null)
        {
            var number = nextIssue++;
            issues[(repository, number)] = new Stored { Number = number, Title = title, Body = body, Type = type };
            return number;
        }

        /// <summary>The issue's timeline, as far as assignments go: who was assigned when. GitHub's clock, in whole seconds.</summary>
        public List<(int Number, string Kind, DateTimeOffset At)> Timeline { get; } = [];

        /// <summary>Every read of an issue's timeline: the tag that was sent, and what was answered.</summary>
        public List<(int Number, string? ETagSent, GitHubOutcome Answered)> TimelineReads { get; } = [];

        public GitHubOutcome? FailTimeline { get; set; }

        /// <param name="at">
        /// When, by GitHub's clock. Ten minutes before the tests' one instant unless said: the
        /// assignment a work item begins with is well before anything that happens to it.
        /// </param>
        public void Assign(string repository, int number, DateTimeOffset? at = null)
        {
            Change(repository, number, i => i.Assigned = true);
            Timeline.Add((number, "assigned", at ?? Now.AddMinutes(-10)));
        }

        public void Unassign(string repository, int number, DateTimeOffset? at = null)
        {
            Change(repository, number, i => i.Assigned = false);
            Timeline.Add((number, "unassigned", at ?? Now.AddMinutes(-10)));
        }

        /// <summary>
        /// Off and on again between two polls: the issue is assigned whenever a list is read,
        /// and the list is as it was - only the timeline knows.
        /// </summary>
        public void Reassign(int number, DateTimeOffset at)
        {
            Timeline.Add((number, "unassigned", at.AddSeconds(-7)));
            Timeline.Add((number, "assigned", at));
        }

        public Task<GitHubResult<IReadOnlyList<GitHubAssignment>>> ListAssignmentsAsync(string repository, int number, string? etag, CancellationToken ct)
        {
            if (FailTimeline is { } failing)
            {
                TimelineReads.Add((number, etag, failing));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubAssignment>>(failing, null, "HTTP 500: Server Error"));
            }

            var events = Timeline.Where(e => e.Number == number).ToList();
            var tag = $"W/\"timeline:{number}:{events.Count}:{Comments.Count(c => c.Number == number)}\"";

            if (etag == tag)
            {
                TimelineReads.Add((number, etag, GitHubOutcome.NotModified));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubAssignment>>(GitHubOutcome.NotModified, null, ETag: tag));
            }

            TimelineReads.Add((number, etag, GitHubOutcome.Ok));

            IReadOnlyList<GitHubAssignment> assigned = [.. events
                .Where(e => e.Kind == "assigned")
                .Select(e => new GitHubAssignment(new GitHubAccount(Bot, 9001), "reporter", e.At))];

            return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubAssignment>>(GitHubOutcome.Ok, assigned, ETag: tag));
        }

        public void Close(string repository, int number) => Change(repository, number, i => i.State = "closed");

        public void Edit(string repository, int number, string body) => Change(repository, number, i => i.Body = body);

        /// <summary>A comment that is simply there: somebody's, or one the agent wrote before it died.</summary>
        /// <param name="touch">
        /// False: the list of assigned issues stays as it was - a comment written in the second
        /// its issue last changed, which the list's tag cannot show.
        /// </param>
        public long Comment(string repository, int number, string author, string body, long? authorId = null, bool touch = true)
        {
            var comment = authorId is { } id
                ? new StoredComment(nextComment++, repository, number, author) { Body = body, CreatedAt = Now, AuthorId = id }
                : new StoredComment(nextComment++, repository, number, author) { Body = body, CreatedAt = Now };
            Comments.Add(comment);

            if (touch)
            {
                Change(repository, number, _ => { });
            }

            return comment.Id;
        }

        public void Reopen(string repository, int number) => Change(repository, number, i => i.State = "open");

        /// <summary>Writing a NEW comment fails with this; reading and editing do not.</summary>
        public GitHubOutcome? FailCreates { get; set; }

        /// <summary>Called when the list of assigned issues is read, before the answer: what happens on GitHub between two reads of one pass.</summary>
        public Action? OnListIssues { get; set; }

        /// <summary>Somebody edits a comment of their own. Not one of the agent's writes.</summary>
        public void EditComment(long id, string body)
        {
            var comment = Comments.Single(c => c.Id == id);
            comment.Body = body;
            comment.Edits++;
        }

        /// <summary>Every read of an issue's comments: the tag that was sent, and what was answered.</summary>
        public List<(int Number, DateTimeOffset? Since, string? ETagSent, GitHubOutcome Answered)> CommentReads { get; } = [];

        /// <summary>Called when an issue's comments are read, before the answer: where a test stops the process.</summary>
        public Action? OnListComments { get; set; }

        /// <summary>Reads of comments fail with this, and nothing else does.</summary>
        public GitHubOutcome? FailCommentReads { get; set; }

        public Dictionary<(string Repository, int Number), StoredPull> Pulls { get; } = [];

        /// <summary>Every read of a pull request: the tag that was sent, and what was answered.</summary>
        public List<(int Number, string? ETagSent, GitHubOutcome Answered)> PullReads { get; } = [];

        public GitHubOutcome? FailPulls { get; set; }

        public void Merge(string repository, int number) => Pulls[(repository, number)] = new StoredPull("closed", Merged: true);

        public void ClosePull(string repository, int number) => Pulls[(repository, number)] = new StoredPull("closed");

        private void Change(string repository, int number, Action<Stored> change)
        {
            var issue = issues[(repository, number)];
            change(issue);
            issue.Version++;
        }

        public Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct) =>
            Task.FromResult(new GitHubResult<GitHubAccount>(GitHubOutcome.Ok, new GitHubAccount(Bot, 9001)));

        public Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct)
        {
            OnListIssues?.Invoke();

            var listed = issues
                .Where(i => i.Key.Repository == repository && i.Value is { Assigned: true, State: "open" })
                .OrderByDescending(i => i.Key.Number)
                .ToList();

            var tag = $"W/\"{repository}:{string.Join(",", listed.Select(i => $"{i.Key.Number}.{i.Value.Version}"))}\"";

            if (etag == tag)
            {
                Lists.Add((etag, GitHubOutcome.NotModified));
                return Task.FromResult(new GitHubResult<GitHubIssuePage>(GitHubOutcome.NotModified, null, ETag: tag));
            }

            Lists.Add((etag, GitHubOutcome.Ok));

            return Task.FromResult(new GitHubResult<GitHubIssuePage>(
                GitHubOutcome.Ok, new GitHubIssuePage([.. listed.Select(i => Issue(repository, i.Value))], HasMore: false), ETag: tag));
        }

        public Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct) =>
            Task.FromResult(issues.TryGetValue((repository, number), out var issue)
                ? new GitHubResult<GitHubIssue>(GitHubOutcome.Ok, Issue(repository, issue))
                : new GitHubResult<GitHubIssue>(GitHubOutcome.NotFound, null, "HTTP 404: Not Found"));

        private static GitHubIssue Issue(string repository, Stored i) => new(
            i.Number,
            $"I_{i.Number}",
            i.Title,
            i.Body,
            i.State,
            $"https://github.com/{repository}/issues/{i.Number}",
            new GitHubAccount("reporter", 3003),
            i.Assigned ? [new GitHubAccount(Bot, 9001)] : [],
            [],
            i.Type);

        public Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, string? etag, CancellationToken ct)
        {
            OnListComments?.Invoke();
            ct.ThrowIfCancellationRequested();

            if ((FailCommentReads ?? FailWrites) is { } failing)
            {
                CommentReads.Add((number, since, etag, failing));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubComment>>(failing, null, "HTTP 500: Server Error"));
            }

            var stored = Comments
                .Where(c => c.Repository == repository && c.Number == number && (since is null || c.CreatedAt >= since))
                .ToList();

            // A tag of the answer, as GitHub's is: it changes when a comment is added or edited.
            var tag = $"W/\"{number}:{since?.ToUnixTimeSeconds()}:{string.Join(",", stored.Select(c => $"{c.Id}.{c.Edits}"))}\"";

            if (etag == tag)
            {
                CommentReads.Add((number, since, etag, GitHubOutcome.NotModified));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubComment>>(GitHubOutcome.NotModified, null, ETag: tag));
            }

            CommentReads.Add((number, since, etag, GitHubOutcome.Ok));

            IReadOnlyList<GitHubComment> found = [.. stored.Select(Wire)];

            return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubComment>>(GitHubOutcome.Ok, found, ETag: tag));
        }

        public Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct)
        {
            if ((FailCreates ?? FailWrites) is { } failing)
            {
                return Task.FromResult(new GitHubResult<GitHubComment>(failing, null, "HTTP 500: Server Error"));
            }

            var comment = new StoredComment(nextComment++, repository, number, Bot) { Body = body, CreatedAt = Now };
            Comments.Add(comment);
            Writes.Add(("create", number, comment.Id));

            // As on GitHub: an issue's comment count is part of the issue, so its list changes.
            Change(repository, number, _ => { });

            return Task.FromResult(new GitHubResult<GitHubComment>(GitHubOutcome.Ok, Wire(comment)));
        }

        public Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct)
        {
            if (FailWrites is { } failing)
            {
                return Task.FromResult(new GitHubResult<GitHubComment>(failing, null, "HTTP 500: Server Error"));
            }

            if (Comments.FirstOrDefault(c => c.Id == commentId && c.Repository == repository) is not { } comment)
            {
                return Task.FromResult(new GitHubResult<GitHubComment>(GitHubOutcome.NotFound, null, "HTTP 404: Not Found"));
            }

            comment.Body = body;
            comment.Edits++;
            Writes.Add(("update", comment.Number, comment.Id));
            Change(repository, comment.Number, _ => { });

            return Task.FromResult(new GitHubResult<GitHubComment>(GitHubOutcome.Ok, Wire(comment)));
        }

        // --- reactions on a comment (#298) ------------------------------------------------------

        private long nextReaction = 318204771;

        /// <summary>Every reaction that is on a comment now, whoever set it.</summary>
        public List<StoredReaction> Reactions { get; } = [];

        /// <summary>Every read of a comment's reactions: the tag that was sent, and what was answered.</summary>
        public List<(long CommentId, string? ETagSent, GitHubOutcome Answered)> ReactionReads { get; } = [];

        /// <summary>Every reaction the agent asked GitHub to set, also one that was there already.</summary>
        public List<(long CommentId, string Content)> ReactionWrites { get; } = [];

        public GitHubOutcome? FailReactionReads { get; set; }

        public GitHubOutcome? FailReactionWrites { get; set; }

        /// <summary>Somebody sets a reaction on a comment. Ids only grow, as GitHub's do.</summary>
        public long React(long commentId, string author, long authorId, string content)
        {
            var reaction = new StoredReaction(nextReaction++, commentId, content, author, authorId);
            Reactions.Add(reaction);
            return reaction.Id;
        }

        /// <summary>Somebody takes a reaction off again.</summary>
        public void Unreact(long reactionId) => Reactions.RemoveAll(r => r.Id == reactionId);

        public Task<GitHubResult<IReadOnlyList<GitHubReaction>>> ListCommentReactionsAsync(string repository, long commentId, string? etag, CancellationToken ct)
        {
            if ((FailReactionReads ?? FailWrites) is { } failing)
            {
                ReactionReads.Add((commentId, etag, failing));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubReaction>>(failing, null, "HTTP 500: Server Error"));
            }

            var stored = Reactions.Where(r => r.CommentId == commentId).OrderBy(r => r.Id).ToList();
            var tag = $"W/\"reactions:{commentId}:{string.Join(",", stored.Select(r => r.Id))}\"";

            if (etag == tag)
            {
                ReactionReads.Add((commentId, etag, GitHubOutcome.NotModified));
                return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubReaction>>(GitHubOutcome.NotModified, null, ETag: tag));
            }

            ReactionReads.Add((commentId, etag, GitHubOutcome.Ok));

            IReadOnlyList<GitHubReaction> found = [.. stored.Select(r => new GitHubReaction(r.Id, r.Content, new GitHubAccount(r.Author, r.AuthorId), Now))];

            return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubReaction>>(GitHubOutcome.Ok, found, ETag: tag));
        }

        public Task<GitHubResult<GitHubReaction>> AddCommentReactionAsync(string repository, long commentId, string content, CancellationToken ct)
        {
            ReactionWrites.Add((commentId, content));

            if ((FailReactionWrites ?? FailWrites) is { } failing)
            {
                return Task.FromResult(new GitHubResult<GitHubReaction>(failing, null, "HTTP 403: Resource not accessible by personal access token"));
            }

            // One account, one comment, one content: GitHub answers with the one that is there.
            var own = Reactions.FirstOrDefault(r => r.CommentId == commentId && r.Content == content && r.Author == Bot);

            if (own is null)
            {
                var id = React(commentId, Bot, 9001, content);
                own = Reactions.Single(r => r.Id == id);
            }

            return Task.FromResult(new GitHubResult<GitHubReaction>(
                GitHubOutcome.Ok, new GitHubReaction(own.Id, own.Content, new GitHubAccount(own.Author, own.AuthorId), Now)));
        }

        private static GitHubComment Wire(StoredComment c) => new(
            c.Id, c.Body, new GitHubAccount(c.Author, c.AuthorId), c.CreatedAt, c.CreatedAt,
            $"https://github.com/{c.Repository}/issues/{c.Number}#issuecomment-{c.Id}");

        public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, string? etag, CancellationToken ct)
        {
            if (FailPulls is { } failing)
            {
                PullReads.Add((number, etag, failing));
                return Task.FromResult(new GitHubResult<GitHubPullRequest>(failing, null, "HTTP 500: Server Error"));
            }

            var pull = Pulls.GetValueOrDefault((repository, number)) ?? new StoredPull();
            var tag = $"W/\"pull:{number}:{pull.State}:{pull.Merged}\"";

            if (etag == tag)
            {
                PullReads.Add((number, etag, GitHubOutcome.NotModified));
                return Task.FromResult(new GitHubResult<GitHubPullRequest>(GitHubOutcome.NotModified, null, ETag: tag));
            }

            PullReads.Add((number, etag, GitHubOutcome.Ok));

            return Task.FromResult(new GitHubResult<GitHubPullRequest>(
                GitHubOutcome.Ok,
                new GitHubPullRequest(number, pull.State, Draft: !pull.Merged, pull.Merged, pull.Merged ? Now : null, $"https://github.com/{repository}/pull/{number}", "hephaisto/codefix-x"),
                ETag: tag));
        }

        public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string repository, CancellationToken ct)
        {
            RepositoryCalls++;

            return Task.FromResult(FailRepository ?? new GitHubResult<GitHubRepository>(GitHubOutcome.Ok, new GitHubRepository(repository, DefaultBranch)));
        }
    }

    private sealed class NullNotifier : IIncidentNotifier
    {
        public void Publish(IncidentLiveEvent liveEvent)
        {
        }

        public async IAsyncEnumerable<IncidentLiveEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class OptionsStub<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
