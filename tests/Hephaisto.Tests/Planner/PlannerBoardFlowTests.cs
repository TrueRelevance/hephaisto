using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Planner;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// The board as the source of truth: every click moves the issue's Status where the click means
/// it to be, leaves a line in the issue's one activity comment, and the board's rules move what
/// GitHub's facts say has moved - each move a line on the issue too.
/// </summary>
public sealed class PlannerBoardFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A board with every column of the flow, and one nobody named.</summary>
    private static Board FullBoard(params BoardItem[] items) => new(
        "PVT_1",
        "Platform planning",
        "https://github.com/orgs/acme/projects/3",
        [
            new BoardField("F_status", "Status", [new("s_inbox", "Inbox"), new("s_backlog", "Backlog"), new("s_ready", "Ready"), new("s_progress", "In progress"), new("s_review", "In review"), new("s_done", "Done")]),
            new BoardField("F_prio", "Priority", [new("p0", "P0"), new("p1", "P1")]),
        ],
        items,
        ["Task", "Bug"],
        [new BoardDateField("F_planned", "Planned for")]);

    private static BoardItem Item(int number, string status, bool open = true, DateTimeOffset? closedAt = null, BoardPullRequest[]? pulls = null, BoardLabel[]? labels = null) =>
        new($"PVTI_{number}", $"I_{number}", "acme/api", number, $"Issue {number}", $"https://github.com/acme/api/issues/{number}", "Task", "reporter",
            open, Given.Now.AddDays(-9), Given.Now.AddDays(-3), closedAt, null, 0, 0,
            new Dictionary<string, string> { ["Status"] = status, ["Priority"] = "P1" }, labels ?? [], [], pulls ?? [],
            FieldsChangedAt: new Dictionary<string, DateTimeOffset> { ["Status"] = Given.Now.AddDays(-5) });

    private static async Task<(PlannerService Planner, PlannerServiceTests.FakeGitHub GitHub, PlannerServiceTests.FakeAudit Audit)> Read(Board board, bool activity = true, bool keepStatus = false)
    {
        var (planner, github, _, audit) = PlannerServiceTests.Build(activity: activity, keepStatus: keepStatus);
        github.FailProject = new GitHubResult<Board>(GitHubOutcome.Ok, board);
        await planner.ReadAsync(refresh: true, Ct);
        github.Calls.Clear();

        return (planner, github, audit);
    }

    private static string Status(PlannerService planner, string itemId) => planner.Kept!.Board!.Items.Single(i => i.ItemId == itemId).Field("Status")!;

    private static string Activity(PlannerServiceTests.FakeGitHub github, int number) =>
        github.Comments.Single(c => c.Number == number && c.Comment.Body.StartsWith(PlannerActivity.Marker, StringComparison.Ordinal)).Comment.Body;

    // --- a click moves the card ----------------------------------------------------------------

    [Fact]
    public async Task Today_is_ready_working_on_it_is_in_progress_stopping_is_ready_again_and_closing_is_done()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "Backlog")), activity: false);

        (await planner.SetDayAsync("PVTI_1", PlannerDay.Today, "flo", Ct)).Message.Should().Be("today; Status Backlog → Ready");
        Status(planner, "PVTI_1").Should().Be("Ready");

        (await planner.SetActiveAsync("PVTI_1", true, "flo", Ct)).Message.Should().Be("worked on now; Status Ready → In progress");
        Status(planner, "PVTI_1").Should().Be("In progress");

        (await planner.SetActiveAsync("PVTI_1", false, "flo", Ct)).Message.Should().Be("no longer worked on; Status In progress → Ready");

        (await planner.CloseAsync("PVTI_1", false, "flo", Ct)).Message.Should().Be("closed as done; Status Ready → Done");

        github.Calls.Where(c => c.StartsWith("field ", StringComparison.Ordinal)).Should().Equal(
            "field PVT_1 PVTI_1 F_status s_ready",
            "field PVT_1 PVTI_1 F_status s_progress",
            "field PVT_1 PVTI_1 F_status s_ready",
            "field PVT_1 PVTI_1 F_status s_done");
    }

    [Fact]
    public async Task A_click_never_moves_a_card_back()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "In review")), activity: false);

        (await planner.SetDayAsync("PVTI_1", PlannerDay.Today, "flo", Ct)).Message.Should().Be("today");
        (await planner.SetActiveAsync("PVTI_1", true, "flo", Ct)).Message.Should().Be("worked on now");

        github.Calls.Should().NotContain(c => c.StartsWith("field ", StringComparison.Ordinal), "an issue in review stays in review");
        Status(planner, "PVTI_1").Should().Be("In review");
    }

    [Fact]
    public async Task Handing_an_issue_to_the_agent_puts_it_in_progress()
    {
        var (planner, _, _) = await Read(FullBoard(Item(1, "Ready")), activity: false);

        (await planner.AssignAsync("PVTI_1", "hephaisto-bot", true, "flo", Ct)).Message.Should().Be("assigned to hephaisto-bot; Status Ready → In progress");
        (await planner.AssignAsync("PVTI_1", "flo", true, "flo", Ct)).Message.Should().Be("assigned to flo");
    }

    // --- the activity comment ------------------------------------------------------------------

    [Fact]
    public async Task Every_click_is_a_line_in_one_comment_that_names_the_person()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "Backlog")));

        await planner.SetDayAsync("PVTI_1", PlannerDay.Today, "flo", Ct);
        await planner.SetFieldAsync("PVTI_1", "Priority", "P0", "anna", Ct);

        github.Comments.Should().ContainSingle("one comment, edited after the first line");
        github.Calls.Where(c => c.Contains("comment", StringComparison.Ordinal)).Should().Equal(
            "comments acme/api#1", "comment acme/api#1", "edit comment acme/api 1");

        var lines = PlannerActivity.Lines(Activity(github, 1));
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("**flo** · today; Status Backlog → Ready");
        lines[1].Should().Contain("**anna** · Priority set to P0");
    }

    [Fact]
    public async Task After_a_restart_the_agents_own_comment_is_found_and_somebody_elses_marker_counts_for_nothing()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "Backlog")));

        github.Comments.Add(("acme/api", 1, new GitHubComment(77, PlannerActivity.Marker + "\n- forged", new GitHubAccount("mallory", 9), Given.Now, Given.Now, "u")));
        await planner.SetFieldAsync("PVTI_1", "Priority", "P0", "flo", Ct);

        github.Comments.Should().HaveCount(2, "mallory's marker is not the agent's comment");
        var ours = github.Comments.Single(c => c.Comment.Author.Login == "hephaisto-bot").Comment;

        // A new process: nothing kept, so it reads the comments once and edits its own.
        var (restarted, github2, _) = await Read(FullBoard(Item(1, "Backlog")));
        github2.Comments.Add(("acme/api", 1, ours));
        await restarted.SetFieldAsync("PVTI_1", "Priority", "P1", "anna", Ct);

        github2.Calls.Should().Equal("field PVT_1 PVTI_1 F_prio p1", "comments acme/api#1", $"edit comment acme/api {ours.Id}");
        PlannerActivity.Lines(github2.Comments.Single().Comment.Body).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_line_that_cannot_be_written_is_said_and_the_click_stands()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "Backlog")));
        github.FailComments = new(GitHubOutcome.ServerError, null, "HTTP 502");

        var result = await planner.SetFieldAsync("PVTI_1", "Priority", "P0", "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Message.Should().Contain("the line on the issue could not be written");
        planner.Kept!.Board!.Items.Single().Field("Priority").Should().Be("P0");
    }

    // --- the board's rules ---------------------------------------------------------------------

    [Fact]
    public async Task The_rules_move_what_github_says_moved_each_with_a_line_and_an_audit_row_and_only_once()
    {
        var board = FullBoard(
            Item(1, "In review", open: false, closedAt: Given.Now.AddHours(-1)),
            Item(2, "Ready", pulls: [new("acme/api", 9, "https://github.com/acme/api/pull/9", BoardPullState.Open, Given.Now.AddHours(-2))]),
            Item(3, "Ready"));
        var (planner, github, audit) = await Read(board, keepStatus: true);

        (await planner.KeepStatusAsync(Ct)).Should().Be(2);

        Status(planner, "PVTI_1").Should().Be("Done");
        Status(planner, "PVTI_2").Should().Be("In review");
        Status(planner, "PVTI_3").Should().Be("Ready");

        github.Comments.Should().NotContain(c => c.Number == 1, "a closed issue's move to Done is no news to its watchers");
        Activity(github, 2).Should().Contain("Status Ready → In review: pull request acme/api#9 is open");
        audit.Rows.Where(r => r.Type == PlannerService.AuditStatus).Select(r => r.Actor).Should().Equal(PlannerService.RulesActor, PlannerService.RulesActor);

        // A read that has not heard of the moves yet hands back the old board: nothing twice.
        await planner.ReadAsync(refresh: true, Ct);
        (await planner.KeepStatusAsync(Ct)).Should().Be(0);
        github.Calls.Count(c => c.StartsWith("field ", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Without_keep_status_the_rules_write_nothing()
    {
        var (planner, github, _) = await Read(FullBoard(Item(1, "In review", open: false, closedAt: Given.Now)), keepStatus: false);

        (await planner.KeepStatusAsync(Ct)).Should().Be(0);
        github.Calls.Should().BeEmpty();
    }
}
