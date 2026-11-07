using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Planner;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.Planner;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The planner through the MCP endpoint (#307): what a coding agent reads of the board, and that
/// each of its writes is the planner's own click - audited with the MCP origin, on the kept board
/// at once, and stopped at the first thing GitHub refuses.
/// </summary>
public sealed class McpPlannerTests
{
    private static readonly McpCaller Gateway = new("mcp/litellm", "litellm", McpTokenOptions.Shared, McpTokenOptions.Approver, MayWrite: true);
    private static readonly McpCaller ReadOnly = new("mcp/viewer", "viewer", McpTokenOptions.Shared, McpTokenOptions.Reader, MayWrite: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- reading ------------------------------------------------------------------------------

    [Fact]
    public async Task Today_lists_what_is_left_for_today_and_the_backlog_what_has_status_backlog()
    {
        var (mcp, planner, _, _) = Build();
        await planner.SetFieldAsync("PVTI_431", "Status", "Backlog", "flo", Ct);

        var today = await mcp.ListAsync(null, null, null, null, null, null, 50, refresh: false, Ct);

        today.View.Should().Be("today");
        today.Issues.Select(i => i.Issue).Should().Equal("acme/web#240");
        today.Issues[0].Day.Should().Be("today");
        today.Issues[0].CarriedOver.Should().BeTrue("its today label is two days old and it is still open");
        today.Board.Value.Should().Contain("Platform planning", "a title with spaces is not a name and goes out in the envelope");

        var backlog = await mcp.ListAsync("backlog", null, null, null, null, null, 50, refresh: false, Ct);

        backlog.Issues.Select(i => i.Issue).Should().Equal("acme/api#431");
        backlog.Issues[0].Fields["Status"].Value.Should().Be("Backlog");

        var open = await mcp.ListAsync("open", null, "P1", null, null, null, 50, refresh: false, Ct);

        open.Issues.Select(i => i.Issue).Should().Equal("acme/scraper#57");
    }

    [Fact]
    public async Task An_issue_s_words_go_out_in_the_envelope()
    {
        var (mcp, _, _, _) = Build();

        var page = await mcp.ListAsync("open", null, null, null, null, null, 50, refresh: false, Ct);

        page.Issues.Should().OnlyContain(i => i.Title.Value.StartsWith("<untrusted-evidence>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_view_is_refused_with_the_ones_there_are()
    {
        var (mcp, _, _, _) = Build();

        var act = () => mcp.ListAsync("someday", null, null, null, null, null, 50, refresh: false, Ct);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*today, backlog, later, decide, open, closed*");
    }

    [Fact]
    public async Task The_fields_say_what_a_new_issue_is_filled_in_with()
    {
        var (mcp, _, _, _) = Build();

        var fields = await mcp.FieldsAsync(Ct);

        fields.Fields.Select(f => f.Name.Value).Should().Equal("Status", "Priority");
        fields.Fields.Single(f => f.Name.Value == "Priority").Required.Should().BeTrue();
        fields.IssueTypes.Select(t => t.Value).Should().Equal("Task", "Bug");
        fields.Repositories.Select(r => r.Value).Should().StartWith("acme/api");
        fields.Days.Should().Contain("today").And.Contain("next week");
    }

    [Theory]
    [InlineData("acme/api#431")]
    [InlineData("https://github.com/acme/api/issues/431")]
    [InlineData("api#431")]
    [InlineData("#431")]
    public async Task An_issue_is_named_by_its_reference_its_url_or_a_short_form_that_fits_one(string issue)
    {
        var (mcp, _, _, _) = Build();

        (await mcp.GetAsync(issue, Ct)).Issue.Issue.Should().Be("acme/api#431");
    }

    [Fact]
    public async Task An_issue_not_on_the_board_is_refused_with_how_to_find_one()
    {
        var (mcp, _, _, _) = Build();

        var act = () => mcp.GetAsync("acme/api#9999", Ct);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*not on the board*list_planner_issues*");
    }

    // --- opening an issue ---------------------------------------------------------------------

    [Fact]
    public async Task A_new_issue_for_today_is_on_the_board_planned_for_today_and_audited_as_the_token_through_mcp()
    {
        var (mcp, planner, github, audit) = Build();

        var result = await mcp.CreateAsync(
            Gateway, null, "Planner MCP tools", "So Claude Code opens issues through Hephaisto.", "Task",
            new Dictionary<string, string> { ["Priority"] = "P1" }, "today", "flo", "Florian", Ct);

        result.Done.Should().Equal("acme/api#500 opened and put on the board", "today", "assigned to flo");
        result.Refused.Should().BeNull();
        result.RecordedAs.Value.Should().Be("mcp/litellm");
        result.ClaimedBy!.Value.Should().Be("Florian");
        result.Issue!.Day.Should().Be("today");
        result.Issue.Fields["Priority"].Value.Should().Be("P1");

        github.Calls.Should().ContainInOrder(
            "open acme/api Planner MCP tools Task", "add PVT_1 I_500", "field PVT_1 PVTI_500 F_prio p1", "label acme/api#500 today", "assign acme/api#500 flo");
        planner.Kept!.Entry("PVTI_500")!.Day.Should().Be(PlannerDay.Today, "the console knows of it the moment the call returns");

        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditCreate, PlannerService.AuditDay, PlannerService.AuditAssign);
        audit.Rows.Should().OnlyContain(r => r.Actor == "mcp/litellm");

        foreach (var row in audit.Rows)
        {
            var origin = JsonDocument.Parse(row.Detail!).RootElement.GetProperty("origin");
            origin.GetProperty("source").GetString().Should().Be("mcp");
            origin.GetProperty("claimedBy").GetString().Should().Be("Florian");
            origin.GetProperty("claimVerified").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_read_only_token_opens_nothing()
    {
        var (mcp, _, github, audit) = Build();

        var act = () => mcp.CreateAsync(ReadOnly, null, "Title", null, null, null, "today", null, null, Ct);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*read-only*");
        github.Calls.Should().NotContain(c => c.StartsWith("open", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_day_that_is_not_one_is_refused_before_github_hears_of_anything()
    {
        var (mcp, _, github, _) = Build();

        var act = () => mcp.CreateAsync(Gateway, null, "Title", null, null, null, "someday", null, null, Ct);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*today, not today, tomorrow, next week, backlog, none*");
        github.Calls.Should().BeEmpty();
    }

    // --- changing an issue --------------------------------------------------------------------

    [Fact]
    public async Task Several_changes_are_made_in_order_and_working_on_it_comes_after_planning_it_for_today()
    {
        var (mcp, planner, github, _) = Build();

        var result = await mcp.UpdateAsync(
            Gateway, "acme/scraper#57", "Scraper retries", null, new Dictionary<string, string> { ["Priority"] = "P0" }, "today", null, null, workedOnNow: true, null, Ct);

        result.Done.Should().Equal("title changed", "Priority set to P0", "today", "worked on now");
        github.Calls.Should().ContainInOrder(
            "edit acme/scraper#57 Scraper retries -", "field PVT_1 PVTI_57 F_prio p0", "unlabel acme/scraper#57 not today", "label acme/scraper#57 today", "label acme/scraper#57 active");
        planner.Kept!.Active!.Item.Reference.Should().Be("acme/scraper#57");
        result.Issue!.WorkedOnNow.Should().BeTrue();
    }

    [Fact]
    public async Task The_first_refusal_stops_the_rest_and_what_was_made_is_said()
    {
        var (mcp, _, github, _) = Build();
        await mcp.ListAsync(null, null, null, null, null, null, 50, refresh: false, Ct);

        var writes = 0;
        github.OnWrite = () =>
        {
            if (++writes == 2)
            {
                github.FailWrite = new(GitHubOutcome.Unauthorized, false, "Resource not accessible by integration");
            }
        };

        var result = await mcp.UpdateAsync(
            Gateway, "acme/scraper#57", null, null, new Dictionary<string, string> { ["Priority"] = "P0" }, "tomorrow", "flo", null, null, null, Ct);

        result.Done.Should().Equal("Priority set to P0");
        result.Refused!.Value.Should().Contain("Resource not accessible by integration");
        github.Calls.Should().NotContain(c => c.StartsWith("assign", StringComparison.Ordinal), "nothing after the refusal is tried");
    }

    [Fact]
    public async Task A_change_that_is_refused_first_changes_nothing_and_says_so()
    {
        var (mcp, _, _, _) = Build();

        var act = () => mcp.UpdateAsync(Gateway, "acme/api#431", null, null, new Dictionary<string, string> { ["Priority"] = "P9" }, null, null, null, null, null, Ct);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("Nothing changed*'P9' is not an option of Priority*");
    }

    [Fact]
    public async Task An_update_without_a_change_is_refused()
    {
        var (mcp, _, _, _) = Build();

        var act = () => mcp.UpdateAsync(Gateway, "acme/api#431", null, null, null, null, null, null, null, null, Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("Name at least one change*");
    }

    [Fact]
    public async Task Closing_posts_the_comment_first_and_then_closes_as_not_planned()
    {
        var (mcp, planner, github, _) = Build();

        var result = await mcp.CloseAsync(Gateway, "acme/api#431", "not planned", "Superseded by #500.", "Florian", Ct);

        result.Done.Should().Equal("the comment is on the issue", "closed as not planned");
        github.Calls.Should().ContainInOrder("comment acme/api#431", "close I_431 NOT_PLANNED");
        planner.Kept!.Entry("PVTI_431").Should().BeNull("a closed issue is no longer among the open ones");

        var update = () => mcp.UpdateAsync(Gateway, "acme/api#431", null, null, null, "today", null, null, null, null, Ct);

        await update.Should().ThrowAsync<McpException>().WithMessage("*is closed*");
    }

    // --- commenting ---------------------------------------------------------------------------

    [Fact]
    public async Task A_relayed_comment_names_the_person_and_is_never_taken_for_a_persons_own()
    {
        var (mcp, _, github, audit) = Build();

        await mcp.CommentAsync(Gateway, "acme/web#240", "Moved to next sprint.", "Florian", Ct);

        var body = github.Comments.Single().Comment.Body;
        body.Should().StartWith("Moved to next sprint.").And.EndWith("<sub>Relayed by an agent through Hephaisto, for Florian.</sub>");
        PlannerComment.TryRead(body, out _, out _).Should().BeFalse("a replan passes on only a comment a person wrote in the console");

        audit.Rows.Single().Type.Should().Be(PlannerService.AuditComment);
    }

    [Fact]
    public async Task A_relayed_comment_through_a_shared_token_without_a_name_names_nobody()
    {
        var (mcp, _, github, _) = Build();

        await mcp.CommentAsync(Gateway, "acme/web#240", "Seen.", null, Ct);

        github.Comments.Single().Comment.Body.Should().EndWith("<sub>Relayed by an agent through Hephaisto.</sub>");
    }

    // --- asking Claude -------------------------------------------------------------------------

    [Fact]
    public async Task Asking_claude_starts_the_planners_triage_job_audited_as_the_token_through_mcp()
    {
        var (mcp, _, _, audit, launcher) = BuildWithTriage(triage: true);

        var result = await mcp.AnalyseAsync(Gateway, "acme/api#431", "Florian", Ct);

        result.Done.Should().ContainSingle().Which.Should().StartWith("Claude is reading acme/api#431 in Job triage-");
        result.RecordedAs.Value.Should().Be("mcp/litellm");
        result.Note.Should().Contain("model budget");
        launcher.Request!.Issue.Number.Should().Be(431);

        var row = audit.Rows.Should().ContainSingle(r => r.Type == PlannerService.AuditTriage).Which;
        row.Actor.Should().Be("mcp/litellm");
        var origin = JsonDocument.Parse(row.Detail!).RootElement.GetProperty("origin");
        origin.GetProperty("source").GetString().Should().Be("mcp");
        origin.GetProperty("claimedBy").GetString().Should().Be("Florian");

        var again = () => mcp.AnalyseAsync(Gateway, "acme/api#431", null, Ct);

        await again.Should().ThrowAsync<McpException>().WithMessage("*not asked (Busy)*already at acme/api#431*");
    }

    [Fact]
    public async Task Asking_claude_needs_the_approver_role_and_an_install_that_asks()
    {
        var writer = new McpCaller("mcp/writer", "writer", McpTokenOptions.Shared, McpTokenOptions.Reader, MayWrite: true);
        var (mcp, _, _, _, launcher) = BuildWithTriage(triage: true);

        var reader = () => mcp.AnalyseAsync(writer, "acme/api#431", null, Ct);

        await reader.Should().ThrowAsync<McpException>().WithMessage("*needs the approver role*");
        launcher.Spec.Should().BeNull("nothing was started");

        var (off, _, _, _, _) = BuildWithTriage(triage: false);
        var refused = () => off.AnalyseAsync(Gateway, "acme/api#431", null, Ct);

        await refused.Should().ThrowAsync<McpException>().WithMessage("*not asked (Off)*");
    }

    private static (McpPlanner Mcp, PlannerService Planner, PlannerServiceTests.FakeGitHub GitHub, PlannerServiceTests.FakeAudit Audit) Build()
    {
        var (mcp, planner, github, audit, _) = BuildWithTriage(triage: false);

        return (mcp, planner, github, audit);
    }

    private static (McpPlanner Mcp, PlannerService Planner, PlannerServiceTests.FakeGitHub GitHub, PlannerServiceTests.FakeAudit Audit, PlannerServiceTests.TriageLauncher Launcher) BuildWithTriage(bool triage)
    {
        var (planner, github, clock, audit, scopes, options) = PlannerServiceTests.Parts(enabled: true, repositories: [], triage: triage);
        var launcher = new PlannerServiceTests.TriageLauncher();
        var codeFix = new CodeFixOptions { Image = "ghcr.io/x/coder:1", ContextRepositoryUrl = "https://github.com/acme/dev-context", Sdk = "fake" };
        var runner = new TriageRunner(
            scopes, new PlannerServiceTests.StaticOptions<GitHubOptions>(options), new PlannerServiceTests.StaticOptions<CodeFixOptions>(codeFix),
            launcher, planner, clock, NullLogger<TriageRunner>.Instance);

        var mcp = new McpPlanner(
            planner,
            runner,
            new PlannerServiceTests.StaticOptions<GitHubOptions>(options),
            new PlannerServiceTests.StaticOptions<NotificationOptions>(new NotificationOptions { BaseUrl = "https://hephaisto.example" }),
            new HttpContextAccessor());

        return (mcp, planner, github, audit, launcher);
    }
}
