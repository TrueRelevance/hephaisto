using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Core.CodeFix;
using k8s.Models;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Planner;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// The planner's service: when it asks GitHub, what a click sends, and that the audit row is
/// there before GitHub hears of anything.
/// </summary>
public sealed class PlannerServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- reading ------------------------------------------------------------------------------

    [Fact]
    public async Task Off_asks_github_nothing()
    {
        var (planner, github, _, _) = Build(enabled: false);

        var snapshot = await planner.ReadAsync(refresh: true, Ct);

        snapshot.State.Should().Be(PlannerState.Off);
        github.Calls.Should().BeEmpty();
        planner.Kept.Should().BeNull();
    }

    [Fact]
    public async Task A_read_is_kept_for_half_a_minute_unless_a_person_asks_again()
    {
        var (planner, github, clock, _) = Build();

        var first = await planner.ReadAsync(refresh: false, Ct);
        await planner.ReadAsync(refresh: false, Ct);

        first.State.Should().Be(PlannerState.Read);
        first.Entries.Select(e => e.Item.Number).Should().Equal(431, 240, 57);
        github.Calls.Count(c => c == "project acme/3").Should().Be(1);

        await planner.ReadAsync(refresh: true, Ct);
        github.Calls.Count(c => c == "project acme/3").Should().Be(2);

        clock.UtcNow = clock.UtcNow.Add(PlannerService.KeepFor).AddSeconds(1);
        await planner.ReadAsync(refresh: false, Ct);
        github.Calls.Count(c => c == "project acme/3").Should().Be(3);
    }

    [Fact]
    public async Task The_agents_own_account_is_the_first_an_issue_can_be_assigned_to()
    {
        var (planner, _, _, _) = Build();

        (await planner.ReadAsync(false, Ct)).Assignees.Should().Equal("hephaisto-bot", "flo");
    }

    [Fact]
    public async Task A_board_that_cannot_be_read_says_why_and_a_later_failure_keeps_the_last_good_one()
    {
        var (planner, github, _, _) = Build();

        github.FailProject = new(GitHubOutcome.Unauthorized, null, "HTTP 403: Resource not accessible by personal access token");
        var never = await planner.ReadAsync(false, Ct);

        never.State.Should().Be(PlannerState.Unreadable);
        never.Problem.Should().Contain("Resource not accessible");
        never.Entries.Should().BeEmpty();

        github.FailProject = null;
        (await planner.ReadAsync(false, Ct)).State.Should().Be(PlannerState.Read, "a failure is not kept: the next look asks again");

        github.FailProject = new(GitHubOutcome.RateLimited, null, "limit", RetryAt: Given.Now.AddMinutes(5));
        var stale = await planner.ReadAsync(refresh: true, Ct);

        stale.State.Should().Be(PlannerState.Read);
        stale.Problem.Should().Contain("rate limited");
        stale.Entries.Should().HaveCount(3, "an empty page would read as an empty board");
    }

    // --- a field ------------------------------------------------------------------------------

    [Fact]
    public async Task A_field_is_set_by_name_and_github_is_told_the_ids_after_the_audit_row_exists()
    {
        var (planner, github, _, audit) = Build();

        github.OnWrite = () => audit.Rows.Should().ContainSingle("no audit, no action").Which.Type.Should().Be(PlannerService.AuditField);

        var result = await planner.SetFieldAsync("PVTI_431", "priority", "p1", "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Message.Should().Be("Priority set to P1");
        github.Calls.Should().Contain("field PVT_1 PVTI_431 F_prio p1");
        result.Snapshot.Entry("PVTI_431")!.Item.Field("Priority").Should().Be("P1");

        var row = audit.Rows.Single();
        row.Actor.Should().Be("flo");
        row.Summary.Should().Be("Priority of acme/api#431 set to P1");
        row.Detail.Should().Contain("\"from\":\"P0\"").And.Contain("\"to\":\"P1\"").And.Contain("\"number\":431");

        github.Calls.Count(c => c == "project acme/3").Should().Be(1, "a write changes the kept board; it does not read it again");
    }

    [Fact]
    public async Task A_click_acts_on_the_board_that_is_kept_however_old_and_reads_only_when_there_is_none()
    {
        var (planner, github, clock, _) = Build();

        // Nothing kept: the API on a fresh process. The board is read first.
        (await planner.SetFieldAsync("PVTI_431", "Priority", "P1", "flo", Ct)).Done.Should().BeTrue();
        github.Calls.Count(c => c == "project acme/3").Should().Be(1);

        // A person who looked at an item for ten minutes before deciding.
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        (await planner.SetFieldAsync("PVTI_431", "Priority", "P0", "flo", Ct)).Done.Should().BeTrue();
        github.Calls.Count(c => c == "project acme/3").Should().Be(1);

        // Looking again is what reads again.
        await planner.ReadAsync(refresh: false, Ct);
        github.Calls.Count(c => c == "project acme/3").Should().Be(2);
    }

    [Theory]
    [InlineData("Severity", "High", "the board has no field 'Severity'")]
    [InlineData("Priority", "P9", "'P9' is not an option of Priority")]
    public async Task What_is_not_on_the_board_is_not_sent_and_not_recorded(string field, string option, string says)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.SetFieldAsync("PVTI_431", field, option, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.NotFound);
        result.Message.Should().Be(says);
        github.Calls.Should().NotContain(c => c.StartsWith("field", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_is_a_second_row_and_changes_nothing_on_the_board()
    {
        var (planner, github, _, audit) = Build();

        github.FailWrite = new(GitHubOutcome.Unauthorized, false, "HTTP 403: Resource not accessible by personal access token");

        var result = await planner.SetFieldAsync("PVTI_431", "Priority", "P1", "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Refused);
        result.Message.Should().Contain("Resource not accessible");
        result.Snapshot.Entry("PVTI_431")!.Item.Field("Priority").Should().Be("P0");
        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditField, PlannerService.AuditRefused);
        audit.Rows[1].Summary.Should().StartWith("acme/api#431: GitHub refused");
    }

    [Theory]
    [InlineData("hephaisto/llm")]
    [InlineData("model")]
    public async Task A_models_name_may_not_decide(string actor)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.CloseAsync("PVTI_431", false, actor, Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Forbidden);
        github.Calls.Should().NotContain(c => c.StartsWith("close", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_item_that_is_not_among_the_open_ones_is_not_found()
    {
        var (planner, _, _, _) = Build();

        (await planner.CloseAsync("PVTI_nope", false, "flo", Ct)).Outcome.Should().Be(PlannerActionOutcome.NotFound);
    }

    // --- closing ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "close I_431 COMPLETED", "closed as done")]
    [InlineData(true, "close I_431 NOT_PLANNED", "closed as not planned")]
    public async Task A_closed_issue_leaves_the_queue_and_counts_as_done_today(bool notPlanned, string call, string says)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.CloseAsync("PVTI_431", notPlanned, "flo", Ct);

        result.Message.Should().Be(says);
        github.Calls.Should().Contain(call);
        result.Snapshot.Entry("PVTI_431").Should().BeNull();
        result.Snapshot.ClosedToday.Select(i => i.Number).Should().Equal(431);
        audit.Rows.Single().Type.Should().Be(PlannerService.AuditClose);
    }

    // --- the day ------------------------------------------------------------------------------

    [Fact]
    public async Task Today_puts_the_label_on_and_takes_the_other_one_off_first()
    {
        var (planner, github, _, audit) = Build();

        // #57 carries a "not today" of yesterday.
        var result = await planner.SetDayAsync("PVTI_57", PlannerDay.Today, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("unlabel acme/scraper#57 not today", "label acme/scraper#57 today");

        var entry = result.Snapshot.Entry("PVTI_57")!;
        entry.Day.Should().Be(PlannerDay.Today);
        entry.CarriedOver.Should().BeFalse();
        audit.Rows.Single().Summary.Should().Be("acme/scraper#57: today");
    }

    [Fact]
    public async Task A_label_that_is_already_there_is_set_again_so_that_it_is_dated_today()
    {
        var (planner, github, _, _) = Build();

        // #240 carries a "today" of two days ago: carried over.
        (await planner.ReadAsync(false, Ct)).Entry("PVTI_240")!.CarriedOver.Should().BeTrue();

        var result = await planner.SetDayAsync("PVTI_240", PlannerDay.Today, "flo", Ct);

        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("unlabel acme/web#240 today", "label acme/web#240 today");
        result.Snapshot.Entry("PVTI_240")!.CarriedOver.Should().BeFalse();
    }

    [Fact]
    public async Task Not_today_and_taking_the_decision_back()
    {
        var (planner, github, _, _) = Build();

        var away = await planner.SetDayAsync("PVTI_431", PlannerDay.NotToday, "flo", Ct);
        away.Snapshot.Entry("PVTI_431")!.Day.Should().Be(PlannerDay.NotToday);
        away.Message.Should().Be("not today");

        var back = await planner.SetDayAsync("PVTI_431", PlannerDay.Undecided, "flo", Ct);
        back.Snapshot.Entry("PVTI_431")!.Day.Should().Be(PlannerDay.Undecided);
        back.Message.Should().Be("not decided");

        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("label acme/api#431 not today", "unlabel acme/api#431 not today");
    }

    [Fact]
    public async Task A_label_somebody_took_off_on_github_meanwhile_is_no_refusal()
    {
        var (planner, github, _, _) = Build();

        github.FailUnlabel = new(GitHubOutcome.NotFound, false, "HTTP 404: Label does not exist");

        var result = await planner.SetDayAsync("PVTI_57", PlannerDay.Today, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Snapshot.Entry("PVTI_57")!.Day.Should().Be(PlannerDay.Today);
    }

    // --- assigning ----------------------------------------------------------------------------

    [Fact]
    public async Task An_issue_is_assigned_only_to_an_account_the_planner_offers()
    {
        var (planner, github, _, audit) = Build();

        var bot = await planner.AssignAsync("PVTI_431", "Hephaisto-Bot", assign: true, "flo", Ct);
        var stranger = await planner.AssignAsync("PVTI_431", "stranger", assign: true, "flo", Ct);

        bot.Outcome.Should().Be(PlannerActionOutcome.Done);
        bot.Message.Should().Be("assigned to hephaisto-bot");
        bot.Snapshot.Entry("PVTI_431")!.Item.Assignees.Should().Equal("hephaisto-bot");
        stranger.Outcome.Should().Be(PlannerActionOutcome.NotFound);

        github.Calls.Where(c => c.Contains("assign", StringComparison.Ordinal)).Should().Equal("assign acme/api#431 hephaisto-bot");
        audit.Rows.Single().Type.Should().Be(PlannerService.AuditAssign);
    }

    [Fact]
    public async Task An_assignee_is_taken_off_and_an_assignment_github_left_out_is_a_refusal()
    {
        var (planner, github, _, _) = Build();

        await planner.AssignAsync("PVTI_431", "flo", assign: true, "flo", Ct);
        var off = await planner.AssignAsync("PVTI_431", "flo", assign: false, "flo", Ct);

        off.Snapshot.Entry("PVTI_431")!.Item.Assignees.Should().BeEmpty();

        github.LeaveOut = true;
        var refused = await planner.AssignAsync("PVTI_431", "flo", assign: true, "flo", Ct);

        refused.Outcome.Should().Be(PlannerActionOutcome.Refused);
        refused.Message.Should().Contain("may not be assigned in acme/api");
    }

    // --- the board under test -----------------------------------------------------------------

    // --- a new issue ---------------------------------------------------------------------------

    [Fact]
    public async Task A_new_issue_is_opened_put_on_the_board_and_given_its_fields_after_the_audit_row()
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);

        github.OnWrite = () => audit.Rows.Should().ContainSingle("no audit, no action").Which.Type.Should().Be(PlannerService.AuditCreate);

        var result = await planner.OpenIssueAsync(
            new PlannerNewIssue("acme/web", "  Dark mode for receipts ", "why and what", "bug", new Dictionary<string, string> { ["priority"] = "p1", ["Status"] = "Inbox" }),
            "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Message.Should().Be("acme/web#500 opened");
        result.ItemId.Should().Be("PVTI_500");
        github.Calls.Should().ContainInOrder(
            "open acme/web Dark mode for receipts Bug", "add PVT_1 I_500", "field PVT_1 PVTI_500 F_prio p1", "field PVT_1 PVTI_500 F_status s1");

        var entry = result.Snapshot.Entry("PVTI_500")!;
        entry.Item.Reference.Should().Be("acme/web#500");
        entry.Item.Field("Priority").Should().Be("P1");
        entry.Day.Should().Be(PlannerDay.Undecided);

        var row = audit.Rows.Single();
        row.Actor.Should().Be("flo");
        row.Summary.Should().Be("issue 'Dark mode for receipts' opened in acme/web");
        github.Calls.Count(c => c == "project acme/3").Should().Be(1, "the new issue is put into the kept board, not read back");
    }

    [Fact]
    public async Task A_new_issue_goes_to_the_configured_repositories_first_and_then_to_the_boards_own()
    {
        var (planner, _, _, _) = Build();

        (await planner.ReadAsync(false, Ct)).NewIssueRepositories.Should().Equal("acme/api", "acme/scraper", "acme/web");

        var (configured, _, _, _) = Build(repositories: ["acme/planning", "ACME/web"]);

        (await configured.ReadAsync(false, Ct)).NewIssueRepositories.Should().Equal("acme/planning", "ACME/web", "acme/api", "acme/scraper");
    }

    [Theory]
    [InlineData("acme/elsewhere", "Bug", "Priority", "P1", "not a repository the planner opens issues in")]
    [InlineData("acme/web", "Saga", "Priority", "P1", "not an issue type of the organisation")]
    [InlineData("acme/web", null, "Priority", "P7", "not an option of a field 'Priority'")]
    [InlineData("acme/web", null, "Colour", "P1", "not an option of a field 'Colour'")]
    public async Task A_new_issue_the_board_cannot_take_is_refused_before_github_hears_of_it(string repository, string? type, string field, string option, string why)
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);

        var result = await planner.OpenIssueAsync(new PlannerNewIssue(repository, "Title", null, type, new Dictionary<string, string> { [field] = option }), "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.NotFound);
        result.Message.Should().Contain(why);
        github.Calls.Should().NotContain(c => c.StartsWith("open", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_issue_github_would_not_put_on_the_board_is_said_to_be_open_all_the_same()
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);
        github.FailAdd = new(GitHubOutcome.Unauthorized, null, "Resource not accessible by integration");

        var result = await planner.OpenIssueAsync(new PlannerNewIssue("acme/web", "Title", null, null, new Dictionary<string, string>()), "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Refused);
        result.Message.Should().StartWith("acme/web#500 was opened, and GitHub would not put it on the board");
        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditCreate, PlannerService.AuditRefused);
    }

    [Fact]
    public async Task A_model_may_not_open_an_issue()
    {
        var (planner, github, _, _) = Build();

        var result = await planner.OpenIssueAsync(new PlannerNewIssue("acme/web", "Title", null, null, new Dictionary<string, string>()), "model", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Forbidden);
        github.Calls.Should().NotContain(c => c.StartsWith("open", StringComparison.Ordinal));
    }

    // --- Claude's answer ------------------------------------------------------------------------

    [Fact]
    public async Task Claudes_labels_go_on_only_when_the_repository_has_them_and_the_analysis_is_one_marked_comment()
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);

        var (done, message) = await planner.ApplyTriageAsync(
            new TriageIssue("acme/api", 431, "https://github.com/acme/api/issues/431", "", null, "", null),
            Answer(labels: ["BUG", "invented", "today", "area:api"], analysis: "Ask @somebody about #12, see https://example.invalid."),
            [new("bug", null), new("area:api", null), new("today", null)],
            "flo",
            Ct);

        done.Should().BeTrue(message);
        message.Should().Be("labelled bug, area:api, and the analysis is on the issue");
        github.Calls.Should().Contain(["label acme/api#431 bug", "label acme/api#431 area:api", "comment acme/api#431"]);
        github.Calls.Should().NotContain(c => c.Contains("invented", StringComparison.Ordinal) || c == "label acme/api#431 today");

        var comment = github.Comments.Single().Comment.Body;
        comment.Should().StartWith("<!-- hephaisto:triage:");
        comment.Should().Contain("Execute github issue acme/api#431");
        comment.Should().NotContain("@somebody", "a model's text mentions nobody on the agent's account");
        comment.Should().NotContain(" #12", "nor references another issue");

        audit.Rows.Should().ContainSingle(r => r.Type == PlannerService.AuditTriaged).Which.Actor.Should().Be("flo");
        planner.Kept!.Entry("PVTI_431")!.Item.Labels.Select(l => l.Name).Should().Contain(["bug", "area:api"]);
    }

    [Fact]
    public async Task The_analysis_shown_is_the_newest_marked_comment_of_the_agents_own_account()
    {
        var (planner, github, _, _) = Build();

        github.Comments.Add(("acme/api", 431, new GitHubComment(1, "<!-- hephaisto:triage:aa -->\nold", new GitHubAccount("hephaisto-bot", 1), Given.Now.AddHours(-3), Given.Now, "https://x/1")));
        github.Comments.Add(("acme/api", 431, new GitHubComment(2, "<!-- hephaisto:triage:bb -->\nnew", new GitHubAccount("hephaisto-bot", 1), Given.Now.AddHours(-1), Given.Now, "https://x/2")));
        github.Comments.Add(("acme/api", 431, new GitHubComment(3, "<!-- hephaisto:triage:cc -->\nforged", new GitHubAccount("mallory", 9), Given.Now, Given.Now, "https://x/3")));

        var analysis = await planner.AnalysisAsync("acme/api", 431, Ct);

        analysis.Should().NotBeNull();
        analysis!.Body.Should().Be("new");
        analysis.Url.Should().Be("https://x/2");
        (await planner.AnalysisAsync("acme/web", 240, Ct)).Should().BeNull();
    }

    // --- asking Claude ---------------------------------------------------------------------------

    [Fact]
    public async Task Asking_claude_starts_one_triage_job_with_the_issue_and_the_repositorys_labels()
    {
        var (runner, _, github, launcher, audit, _) = BuildTriage();

        var start = await runner.StartAsync("acme/api", 431, "flo", Ct);

        start.Outcome.Should().Be(TriageStartOutcome.Started, start.Message);
        launcher.Spec!.Metadata.Name.Should().StartWith("triage-");
        launcher.Spec.Metadata.Labels["hephaisto.dev/phase"].Should().Be("triage");
        launcher.Spec.Metadata.Labels["app.kubernetes.io/name"].Should().Be(CodeFixJobSpec.AppLabel, "the coder's NetworkPolicy holds for it");
        launcher.Spec.Metadata.Annotations[TriageJobSpec.IssueAnnotation].Should().Be("acme/api#431");
        launcher.Spec.Spec.Template.Spec.InitContainers.Should().ContainSingle().Which.Name.Should().Be("prepare");
        launcher.Spec.Spec.Template.Spec.Containers.Should().ContainSingle().Which.Name.Should().Be("coder");

        var request = launcher.Request!;
        request.Phase.Should().Be("triage");
        request.Issue.Should().Be(new TriageIssue("acme/api", 431, "https://github.com/acme/api/issues/431", "Issue 431", "Bug", "reporter", "body of 431"));
        request.Labels.Select(l => l.Name).Should().Equal("bug", "area:api", "today");
        request.Context.Should().Be(new CodeFixContextRef("https://github.com/acme/dev-context", "main"));
        request.Budget.Should().Be(new InvestigateBudget(2m, 900, 40));

        audit.Rows.Should().ContainSingle(r => r.Type == PlannerService.AuditTriage).Which.Actor.Should().Be("flo");
        (await runner.StartAsync("acme/api", 431, "flo", Ct)).Outcome.Should().Be(TriageStartOutcome.Busy, "Claude is at it already");
        github.Calls.Should().NotContain(c => c.StartsWith("label ", StringComparison.Ordinal), "nothing is put on before Claude answered");
    }

    [Fact]
    public async Task A_finished_job_is_applied_and_deleted_and_the_run_says_so()
    {
        var (runner, planner, github, launcher, _, _) = BuildTriage();
        await planner.ReadAsync(false, Ct);
        TriageRun? ended = null;
        runner.Ended += r => ended = r;

        await runner.StartAsync("acme/api", 431, "flo", Ct);
        launcher.Log = request => CodeFixResultParser.Frame(JsonSerializer.Serialize(Answer(request.AttemptId, labels: ["bug"]), CodeFixContract.Json));

        await runner.PassAsync(Ct);

        var run = runner.Run("acme/api", 431)!;
        run.State.Should().Be(TriageState.Done, run.Message);
        run.Message.Should().Be("labelled bug, and the analysis is on the issue");
        ended.Should().Be(run);
        launcher.Deleted.Should().Equal(run.JobName);
        github.Calls.Should().Contain(["label acme/api#431 bug", "comment acme/api#431"]);
    }

    [Theory]
    [InlineData("forged", "the Job's answer could not be read")]
    [InlineData("failed", "Claude did not finish (failed): the context could not be cloned")]
    public async Task A_job_without_a_good_answer_puts_nothing_on_the_issue(string how, string message)
    {
        var (runner, _, github, launcher, _, _) = BuildTriage();

        await runner.StartAsync("acme/api", 431, "flo", Ct);
        launcher.Log = request => how == "forged"
            ? CodeFixResultParser.Frame(JsonSerializer.Serialize(Answer(Guid.NewGuid()), CodeFixContract.Json))
            : CodeFixResultParser.Frame(JsonSerializer.Serialize(Answer(request.AttemptId) with { Outcome = "failed", Error = "the context could not be cloned" }, CodeFixContract.Json));

        await runner.PassAsync(Ct);

        var run = runner.Run("acme/api", 431)!;
        run.State.Should().Be(TriageState.Failed);
        run.Message.Should().StartWith(message);
        github.Calls.Should().NotContain(c => c.StartsWith("label ", StringComparison.Ordinal) || c.StartsWith("comment ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_job_that_never_ends_is_given_up_after_its_deadline()
    {
        var (runner, _, _, launcher, _, clock) = BuildTriage();

        await runner.StartAsync("acme/api", 431, "flo", Ct);
        launcher.Phase = CodeFixJobPhase.Running;

        await runner.PassAsync(Ct);
        runner.Run("acme/api", 431)!.State.Should().Be(TriageState.Running);

        clock.UtcNow = clock.UtcNow.Add(TimeSpan.FromMinutes(15) + TriageRunner.Grace).AddSeconds(1);
        await runner.PassAsync(Ct);

        runner.Run("acme/api", 431)!.Message.Should().Be("the Job did not end within its deadline");
        launcher.Deleted.Should().ContainSingle();
    }

    [Fact]
    public async Task Asking_is_refused_when_it_is_off_for_a_model_and_beyond_the_concurrency_cap()
    {
        var (off, _, _, _, _, _) = BuildTriage(triage: false);
        (await off.StartAsync("acme/api", 431, "flo", Ct)).Outcome.Should().Be(TriageStartOutcome.Off);

        var (runner, _, _, launcher, _, _) = BuildTriage();
        (await runner.StartAsync("acme/api", 431, "model", Ct)).Outcome.Should().Be(TriageStartOutcome.Forbidden);

        launcher.Phase = CodeFixJobPhase.Running;
        (await runner.StartAsync("acme/api", 431, "flo", Ct)).Outcome.Should().Be(TriageStartOutcome.Started);
        (await runner.StartAsync("acme/web", 240, "flo", Ct)).Outcome.Should().Be(TriageStartOutcome.Started);
        (await runner.StartAsync("acme/scraper", 57, "flo", Ct)).Outcome.Should().Be(TriageStartOutcome.Busy, "two at once is the cap");
    }

    // --- a later day -------------------------------------------------------------------------

    [Fact]
    public async Task Tomorrow_takes_the_days_labels_off_and_sets_the_day_field_after_the_audit_row()
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);

        github.OnWrite = () => audit.Rows.Should().ContainSingle(r => r.Type == PlannerService.AuditSchedule, "no audit, no action");

        var result = await planner.ScheduleAsync("PVTI_240", PlannerSchedule.Tomorrow, "flo", Ct);

        var tomorrow = DateOnly.FromDateTime(Given.Now.UtcDateTime).AddDays(1);

        result.Outcome.Should().Be(PlannerActionOutcome.Done, result.Message);
        result.Message.Should().Be("planned for " + tomorrow.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture));
        github.Calls.Should().ContainInOrder("unlabel acme/web#240 today", $"date PVT_1 PVTI_240 F_planned {tomorrow:yyyy-MM-dd}");

        var entry = result.Snapshot.Entry("PVTI_240")!;
        entry.Day.Should().Be(PlannerDay.Later);
        entry.PlannedFor.Should().Be(tomorrow);
        entry.Item.Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task Backlog_empties_the_day_field_and_sets_the_status_and_a_day_decided_now_empties_it_too()
    {
        var (planner, github, _, _) = Build();
        await planner.ReadAsync(false, Ct);
        await planner.ScheduleAsync("PVTI_431", PlannerSchedule.NextWeek, "flo", Ct);

        var backlog = await planner.ScheduleAsync("PVTI_431", PlannerSchedule.Backlog, "flo", Ct);

        backlog.Outcome.Should().Be(PlannerActionOutcome.Done, backlog.Message);
        backlog.Message.Should().Be("back in the backlog");
        github.Calls.Should().ContainInOrder(
            $"date PVT_1 PVTI_431 F_planned {PlannerQueue.DayFor(PlannerSchedule.NextWeek, Given.Now):yyyy-MM-dd}", "undate PVT_1 PVTI_431 F_planned", "field PVT_1 PVTI_431 F_status s3");
        backlog.Snapshot.Entry("PVTI_431")!.Item.Field("Status").Should().Be("Backlog");
        backlog.Snapshot.Entry("PVTI_431")!.Day.Should().Be(PlannerDay.Undecided);

        await planner.ScheduleAsync("PVTI_431", PlannerSchedule.Tomorrow, "flo", Ct);
        var today = await planner.SetDayAsync("PVTI_431", PlannerDay.Today, "flo", Ct);

        today.Snapshot.Entry("PVTI_431")!.PlannedFor.Should().BeNull("today replaces the later day");
        github.Calls.Last().Should().Be("undate PVT_1 PVTI_431 F_planned");
    }

    [Fact]
    public async Task A_later_day_on_a_board_without_the_field_is_refused_before_github_hears_of_it()
    {
        var (planner, github, _, audit) = Build(dayField: "Due");
        await planner.ReadAsync(false, Ct);

        var result = await planner.ScheduleAsync("PVTI_240", PlannerSchedule.Tomorrow, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.NotFound);
        result.Message.Should().Contain("no date field 'Due'");
        audit.Rows.Should().BeEmpty();
        github.Calls.Should().NotContain(c => c.StartsWith("unlabel", StringComparison.Ordinal));
    }

    // --- comments and search ----------------------------------------------------------------

    [Fact]
    public async Task A_comment_is_posted_with_whose_words_they_are_only_on_an_issue_of_the_board()
    {
        var (planner, github, _, audit) = Build();
        await planner.ReadAsync(false, Ct);

        var (outcome, _, comment) = await planner.CommentAsync("acme/api", 431, "  Looked at it: the cache key misses the currency.  ", "flo", Ct);

        outcome.Should().Be(PlannerActionOutcome.Done);
        comment!.Body.Should().Be("<!-- hephaisto:planner-comment:flo -->\nLooked at it: the cache key misses the currency.\n\n<sub>Written by flo in Hephaisto's planner.</sub>");
        audit.Rows.Should().ContainSingle(r => r.Type == PlannerService.AuditComment).Which.Actor.Should().Be("flo");
        (await planner.CommentsAsync("acme/api", 431, Ct))!.Should().ContainSingle();

        (await planner.CommentAsync("acme/elsewhere", 1, "hi", "flo", Ct)).Outcome.Should().Be(PlannerActionOutcome.NotFound);
        (await planner.CommentAsync("acme/api", 431, "hi", "model", Ct)).Outcome.Should().Be(PlannerActionOutcome.Forbidden);
        (await planner.CommentAsync("acme/api", 431, "   ", "flo", Ct)).Outcome.Should().Be(PlannerActionOutcome.NotFound);
        github.Calls.Count(c => c.StartsWith("comment ", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Search_finds_by_every_word_by_number_and_by_state_newest_first()
    {
        var (planner, _, _, _) = Build();
        await planner.ReadAsync(false, Ct);
        await planner.CloseAsync("PVTI_57", false, "flo", Ct);

        planner.Search("issue", open: null).Select(i => i.Number).Should().BeEquivalentTo([431, 240, 57]);
        planner.Search("ISSUE 240", open: null).Select(i => i.Number).Should().Equal(240);
        planner.Search("#431", open: null).Select(i => i.Number).Should().Equal(431);
        planner.Search(null, open: false).Select(i => i.Number).Should().Equal(57);
        planner.Search("acme/scraper", open: true).Should().BeEmpty();
        planner.Search("today", open: true).Select(i => i.Number).Should().Equal([240], "a label is searched too");
    }

    private static TriageResult Answer(Guid? attempt = null, string[]? labels = null, string analysis = "It lies in the API.") => new()
    {
        AttemptId = attempt ?? Guid.NewGuid(),
        Outcome = "triaged",
        Labels = labels ?? [],
        Summary = "Fix the cache.",
        Analysis = analysis,
        Steps = ["Drop the entry on a price change."],
        Questions = [],
        CostUsd = 0.12m,
        Billing = "subscription",
        Turns = 4,
        SessionId = null,
        ContextSha = null,
        Error = null,
        DeniedToolCalls = [],
    };

    private static (TriageRunner Runner, PlannerService Planner, FakeGitHub GitHub, TriageLauncher Launcher, FakeAudit Audit, Given.FixedClock Clock) BuildTriage(bool triage = true)
    {
        var (planner, github, clock, audit, scopes, options) = Parts(enabled: true, repositories: [], triage: triage);
        var launcher = new TriageLauncher();
        var codeFix = new CodeFixOptions { Image = "ghcr.io/x/coder:1", ContextRepositoryUrl = "https://github.com/acme/dev-context", Sdk = "fake" };

        var runner = new TriageRunner(
            scopes, new StaticOptions<GitHubOptions>(options), new StaticOptions<CodeFixOptions>(codeFix), launcher, planner, clock, NullLogger<TriageRunner>.Instance);

        return (runner, planner, github, launcher, audit, clock);
    }

    /// <summary>A launcher whose Job is whatever the test says: its phase, and the log it leaves.</summary>
    private sealed class TriageLauncher : ICodeFixJobLauncher
    {
        public V1Job? Spec { get; private set; }

        public TriageRequest? Request { get; private set; }

        public CodeFixJobPhase Phase { get; set; } = CodeFixJobPhase.Succeeded;

        public Func<TriageRequest, string?> Log { get; set; } = _ => null;

        public List<string> Deleted { get; } = [];

        public bool IsAvailable => true;

        public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> LaunchAsync(V1Job spec, Func<V1Job, V1ConfigMap> request, CancellationToken ct)
        {
            Spec = spec;
            spec.Metadata.Uid = Guid.NewGuid().ToString();
            Request = JsonSerializer.Deserialize<TriageRequest>(request(spec).Data["request.json"], CodeFixContract.Json);

            return Task.FromResult(spec.Metadata.Name);
        }

        public Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct) => Task.FromResult(new CodeFixJobObservation(Phase, null));

        public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct) => Task.FromResult(Log(Request!));

        public Task DeleteAsync(string jobName, CancellationToken ct)
        {
            Deleted.Add(jobName);
            return Task.CompletedTask;
        }
    }

    private static (PlannerService Planner, FakeGitHub GitHub, Given.FixedClock Clock, FakeAudit Audit) Build(bool enabled = true, string[]? repositories = null, string dayField = "Planned for")
    {
        var (planner, github, clock, audit, _, _) = Parts(enabled, repositories ?? [], triage: false, dayField);

        return (planner, github, clock, audit);
    }

    private static (PlannerService Planner, FakeGitHub GitHub, Given.FixedClock Clock, FakeAudit Audit, IServiceScopeFactory Scopes, GitHubOptions Options) Parts(
        bool enabled, string[] repositories, bool triage, string dayField = "Planned for")
    {
        var github = new FakeGitHub();
        var audit = new FakeAudit();
        var clock = Given.Clock();

        var services = new ServiceCollection();
        services.AddSingleton<IGitHubClient>(github);
        services.AddSingleton<IAuditRepository>(audit);

        var options = new GitHubOptions
        {
            Enabled = true,
            Token = "t",
            BotLogin = "hephaisto-bot",
            Repositories = ["acme/api"],
            Planner = new GitHubPlannerOptions
            {
                Enabled = enabled,
                Owner = "acme",
                Number = 3,
                RequiredFields = ["Priority"],
                Assignees = ["flo"],
                Repositories = [.. repositories],
                Triage = new GitHubPlannerTriageOptions { Enabled = triage },
                DayField = dayField,
            },
        };

        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var planner = new PlannerService(scopes, new StaticOptions<GitHubOptions>(options), clock, NullLogger<PlannerService>.Instance);

        return (planner, github, clock, audit, scopes, options);
    }

    private static Board TheBoard() => new(
        "PVT_1",
        "Platform planning",
        "https://github.com/orgs/acme/projects/3",
        [
            new BoardField("F_status", "Status", [new("s1", "Inbox"), new("s2", "In review"), new("s3", "Backlog")]),
            new BoardField("F_prio", "Priority", [new("p0", "P0"), new("p1", "P1")]),
        ],
        [
            Item("PVTI_431", "acme/api", 431, priority: "P0", pulls: [new("acme/api", 433, "https://github.com/acme/api/pull/433", BoardPullState.Merged, Given.Now.AddDays(-2))]),
            Item("PVTI_240", "acme/web", 240, priority: null, labels: [new("today", Given.Now.AddDays(-2))]),
            Item("PVTI_57", "acme/scraper", 57, priority: "P1", labels: [new("not today", Given.Now.AddDays(-1))]),
        ],
        ["Task", "Bug"],
        [new BoardDateField("F_planned", "Planned for")]);

    private static BoardItem Item(string id, string repository, int number, string? priority, BoardLabel[]? labels = null, BoardPullRequest[]? pulls = null)
    {
        var fields = new Dictionary<string, string> { ["Status"] = "Inbox" };

        if (priority is not null)
        {
            fields["Priority"] = priority;
        }

        return new BoardItem(
            id, $"I_{number}", repository, number, $"Issue {number}", $"https://github.com/{repository}/issues/{number}", "Task", "reporter",
            true, Given.Now.AddDays(-9), Given.Now.AddDays(-3), null, null, 0, 0, fields, labels ?? [], [], pulls ?? []);
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeAudit : IAuditRepository
    {
        public List<AuditEvent> Rows { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct)
        {
            Rows.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken ct)
        {
            Rows.AddRange(auditEvents);
            return Task.CompletedTask;
        }

        public void Enlist(AuditEvent auditEvent) => Rows.Add(auditEvent);

        public Task<IReadOnlyList<AuditEvent>> GetForIncidentAsync(Guid incidentId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class FakeGitHub : IGitHubClient
    {
        public List<string> Calls { get; } = [];

        public GitHubResult<Board>? FailProject { get; set; }

        public GitHubResult<bool>? FailWrite { get; set; }

        public GitHubResult<bool>? FailUnlabel { get; set; }

        public bool LeaveOut { get; set; }

        public Action? OnWrite { get; set; }

        private Task<GitHubResult<bool>> Write(string call, bool value = true)
        {
            OnWrite?.Invoke();
            Calls.Add(call);

            return Task.FromResult(FailWrite ?? new GitHubResult<bool>(GitHubOutcome.Ok, value));
        }

        public Task<GitHubResult<Board>> GetProjectAsync(string owner, int number, CancellationToken ct)
        {
            Calls.Add($"project {owner}/{number}");

            return Task.FromResult(FailProject ?? new GitHubResult<Board>(GitHubOutcome.Ok, TheBoard()));
        }

        public Task<GitHubResult<bool>> SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct) =>
            Write($"field {projectId} {itemId} {fieldId} {optionId}");

        public Task<GitHubResult<bool>> SetProjectDateAsync(string projectId, string itemId, string fieldId, DateOnly? date, CancellationToken ct) =>
            Write(date is { } day ? $"date {projectId} {itemId} {fieldId} {day:yyyy-MM-dd}" : $"undate {projectId} {itemId} {fieldId}");

        public Task<GitHubResult<bool>> CloseIssueAsync(string issueId, bool notPlanned, CancellationToken ct) =>
            Write($"close {issueId} {(notPlanned ? "NOT_PLANNED" : "COMPLETED")}");

        public Task<GitHubResult<bool>> AddLabelAsync(string repository, int number, string label, CancellationToken ct) =>
            Write($"label {repository}#{number} {label}");

        public Task<GitHubResult<bool>> RemoveLabelAsync(string repository, int number, string label, CancellationToken ct)
        {
            if (FailUnlabel is { } failing)
            {
                Calls.Add($"unlabel {repository}#{number} {label}");
                return Task.FromResult(failing);
            }

            return Write($"unlabel {repository}#{number} {label}");
        }

        public Task<GitHubResult<bool>> AddAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            Write($"assign {repository}#{number} {login}", value: !LeaveOut);

        public Task<GitHubResult<bool>> RemoveAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            Write($"unassign {repository}#{number} {login}");

        public Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct)
        {
            Calls.Add($"issue {repository}#{number}");

            return Task.FromResult(new GitHubResult<GitHubIssue>(GitHubOutcome.Ok, new GitHubIssue(
                number, $"I_{number}", $"Issue {number}", $"body of {number}", "open", $"https://github.com/{repository}/issues/{number}",
                new GitHubAccount("reporter", 3), [], [], "Bug")));
        }

        public List<(string Repository, int Number, GitHubComment Comment)> Comments { get; } = [];

        public GitHubResult<string>? FailAdd { get; set; }

        public Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, string? etag, CancellationToken ct)
        {
            Calls.Add($"comments {repository}#{number}");

            return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubComment>>(
                GitHubOutcome.Ok, [.. Comments.Where(c => c.Repository == repository && c.Number == number).Select(c => c.Comment)]));
        }

        public Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct)
        {
            OnWrite?.Invoke();
            Calls.Add($"comment {repository}#{number}");

            var comment = new GitHubComment(Comments.Count + 1, body, new GitHubAccount("hephaisto-bot", 1), Given.Now, Given.Now, $"https://github.com/{repository}/issues/{number}#issuecomment-{Comments.Count + 1}");
            Comments.Add((repository, number, comment));

            return Task.FromResult(new GitHubResult<GitHubComment>(GitHubOutcome.Ok, comment));
        }

        public Task<GitHubResult<GitHubIssue>> CreateIssueAsync(string repository, string title, string? body, string? type, CancellationToken ct)
        {
            OnWrite?.Invoke();
            Calls.Add($"open {repository} {title} {type ?? "-"}");

            return Task.FromResult(FailWrite is { } failing
                ? new GitHubResult<GitHubIssue>(failing.Outcome, null, failing.Detail)
                : new GitHubResult<GitHubIssue>(GitHubOutcome.Ok, new GitHubIssue(
                    500, "I_500", title, body, "open", $"https://github.com/{repository}/issues/500", new GitHubAccount("hephaisto-bot", 1), [], [], type)));
        }

        public Task<GitHubResult<string>> AddProjectItemAsync(string projectId, string contentId, CancellationToken ct)
        {
            Calls.Add($"add {projectId} {contentId}");

            return Task.FromResult(FailAdd ?? new GitHubResult<string>(GitHubOutcome.Ok, "PVTI_500"));
        }

        public Task<GitHubResult<IReadOnlyList<GitHubLabel>>> ListLabelsAsync(string repository, CancellationToken ct)
        {
            Calls.Add($"labels {repository}");

            return Task.FromResult(new GitHubResult<IReadOnlyList<GitHubLabel>>(
                GitHubOutcome.Ok, [new("bug", "Something isn't working"), new("area:api", null), new("today", null)]));
        }

        public Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<IReadOnlyList<GitHubReaction>>> ListCommentReactionsAsync(string repository, long commentId, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubReaction>> AddCommentReactionAsync(string repository, long commentId, string content, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string repository, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<IReadOnlyList<GitHubAssignment>>> ListAssignmentsAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
