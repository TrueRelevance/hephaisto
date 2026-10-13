using Hephaisto.Core.Planning;

namespace Hephaisto.Tests.Planning;

/// <summary>
/// The planner's rules: why an item is in front of a person, in what order, what the two day
/// labels mean on which day, and how the board view groups.
/// </summary>
public sealed class PlannerQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly PlannerRules Rules = new(["Priority", "Area"], TimeSpan.FromDays(5));

    private static Board BoardOf(params BoardItem[] items) => new(
        "PVT_1",
        "Platform planning",
        "https://github.com/orgs/acme/projects/3",
        [
            new BoardField("F_status", "Status", [new("s1", "Inbox"), new("s2", "Backlog"), new("s3", "In progress"), new("s4", "In review"), new("s5", "Done")]),
            new BoardField("F_prio", "Priority", [new("p0", "P0"), new("p1", "P1"), new("p2", "P2")]),
            new BoardField("F_area", "Area", [new("a1", "API"), new("a2", "Web")]),
        ],
        items);

    private static BoardItem Item(
        int number,
        string? status = "Backlog",
        string? priority = "P1",
        string? area = "API",
        string? parent = null,
        int subIssues = 0,
        int subIssuesDone = 0,
        bool open = true,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? closedAt = null,
        BoardLabel[]? labels = null,
        BoardPullRequest[]? pullRequests = null)
    {
        var fields = new Dictionary<string, string>();

        if (status is not null) fields["Status"] = status;
        if (priority is not null) fields["Priority"] = priority;
        if (area is not null) fields["Area"] = area;

        return new BoardItem(
            $"PVTI_{number}", $"I_{number}", "acme/api", number, $"Issue {number}", Url(number), "Task", "reporter",
            open, Now.AddDays(-20), updatedAt ?? Now.AddDays(-1), closedAt, parent, subIssues, subIssuesDone,
            fields, labels ?? [], [], pullRequests ?? []);
    }

    private static string Url(int number) => $"https://github.com/acme/api/issues/{number}";

    private static BoardPullRequest Pull(BoardPullState state, int daysAgo) =>
        new("acme/api", 900, "https://github.com/acme/api/pull/900", state, Now.AddDays(-daysAgo));

    private static PlannerEntry Only(BoardItem item) => PlannerQueue.Build(BoardOf(item), Rules, Now).Should().ContainSingle().Subject;

    [Fact]
    public void An_item_nothing_is_wrong_with_has_no_reason()
    {
        Only(Item(1)).Reason.Should().Be(PlannerReason.None);
        Only(Item(1)).NeedsDecision.Should().BeFalse();
    }

    [Fact]
    public void Every_pull_request_merged_and_the_issue_open_is_a_reason()
    {
        Only(Item(1, pullRequests: [Pull(BoardPullState.Merged, 2)])).Reason.Should().Be(PlannerReason.MergedStillOpen);
    }

    [Fact]
    public void One_open_pull_request_beside_a_merged_one_is_work_in_progress()
    {
        var item = Item(1, pullRequests: [Pull(BoardPullState.Merged, 2), Pull(BoardPullState.Open, 1) with { Number = 901 }]);

        Only(item).Reason.Should().Be(PlannerReason.None);
    }

    [Fact]
    public void A_pull_request_closed_without_merging_finishes_nothing()
    {
        Only(Item(1, pullRequests: [Pull(BoardPullState.Closed, 2)])).Reason.Should().Be(PlannerReason.None);
    }

    [Fact]
    public void An_open_pull_request_is_quiet_only_after_the_period_and_only_if_the_issue_was_quiet_too()
    {
        Only(Item(1, updatedAt: Now.AddDays(-9), pullRequests: [Pull(BoardPullState.Open, 6)])).Reason.Should().Be(PlannerReason.PullRequestQuiet);
        Only(Item(1, updatedAt: Now.AddDays(-9), pullRequests: [Pull(BoardPullState.Open, 4)])).Reason.Should().Be(PlannerReason.None);
        Only(Item(1, updatedAt: Now.AddDays(-1), pullRequests: [Pull(BoardPullState.Open, 6)])).Reason.Should().Be(PlannerReason.None);
    }

    [Fact]
    public void A_required_field_that_is_empty_is_named_by_the_boards_spelling()
    {
        var entry = PlannerQueue.Build(BoardOf(Item(1, priority: null, area: null)), new PlannerRules(["priority", "AREA", "Nonexistent"], TimeSpan.FromDays(5)), Now).Single();

        entry.Reason.Should().Be(PlannerReason.FieldEmpty);
        entry.EmptyFields.Should().Equal("Priority", "Area");
    }

    [Fact]
    public void Without_required_fields_an_empty_one_is_nobodys_business()
    {
        PlannerQueue.Build(BoardOf(Item(1, priority: null)), PlannerRules.Default, Now).Single().Reason.Should().Be(PlannerReason.None);
    }

    [Fact]
    public void A_parent_is_judged_by_its_sub_issues_and_not_by_the_pull_requests_that_name_it()
    {
        Only(Item(1, subIssues: 3, subIssuesDone: 3)).Reason.Should().Be(PlannerReason.SubIssuesDone);
        Only(Item(1, subIssues: 3, subIssuesDone: 1, pullRequests: [Pull(BoardPullState.Merged, 2)])).Reason.Should().Be(PlannerReason.None);
    }

    [Fact]
    public void A_closed_issue_is_not_in_the_queue_and_counts_as_done_on_the_day_it_was_closed()
    {
        var board = BoardOf(
            Item(1, open: false, closedAt: Now.AddHours(-2)),
            Item(2, open: false, closedAt: Now.AddDays(-1)),
            Item(3));

        PlannerQueue.Build(board, Rules, Now).Select(e => e.Item.Number).Should().Equal(3);
        PlannerQueue.ClosedToday(board, Now).Select(i => i.Number).Should().Equal(1);
    }

    [Fact]
    public void The_queue_is_by_reason_then_priority_then_the_column_nearest_to_done()
    {
        var board = BoardOf(
            Item(1, status: "Backlog", priority: "P2"),
            Item(2, status: "Inbox", priority: "P0"),
            Item(3, status: "In review", priority: "P0"),
            Item(4, priority: null),
            Item(5, priority: "P2", pullRequests: [Pull(BoardPullState.Merged, 2)]),
            Item(6, priority: "P0", subIssues: 2, subIssuesDone: 2),
            Item(7, status: null, priority: null, area: "Web"));

        PlannerQueue.Build(board, Rules, Now).Select(e => e.Item.Number).Should().Equal(5, 6, 4, 7, 3, 2, 1);
    }

    [Fact]
    public void Today_stays_until_somebody_takes_it_off_and_says_when_it_is_from_an_earlier_day()
    {
        var fresh = Only(Item(1, labels: [new("today", Now.AddHours(-1))]));
        var old = Only(Item(1, labels: [new("Today", Now.AddDays(-1))]));
        var unknown = Only(Item(1, labels: [new("today", null)]));

        fresh.Day.Should().Be(PlannerDay.Today);
        fresh.CarriedOver.Should().BeFalse();
        old.Day.Should().Be(PlannerDay.Today);
        old.CarriedOver.Should().BeTrue();
        unknown.Day.Should().Be(PlannerDay.Today);
        unknown.CarriedOver.Should().BeTrue();
    }

    [Fact]
    public void Not_today_counts_on_the_day_it_was_set_and_is_forgotten_the_next_morning()
    {
        Only(Item(1, labels: [new("not today", Now.AddHours(-1))])).Day.Should().Be(PlannerDay.NotToday);
        Only(Item(1, labels: [new("not today", Now.AddDays(-1))])).Day.Should().Be(PlannerDay.Undecided);
        Only(Item(1, labels: [new("not today", null)])).Day.Should().Be(PlannerDay.Undecided);
        Only(Item(1, labels: [new("bug", Now)])).Day.Should().Be(PlannerDay.Undecided);
    }

    [Fact]
    public void With_both_labels_the_newer_one_counts_and_an_old_not_today_never_hides_a_today()
    {
        Only(Item(1, labels: [new("today", Now.AddHours(-3)), new("not today", Now.AddHours(-1))])).Day.Should().Be(PlannerDay.NotToday);
        Only(Item(1, labels: [new("today", Now.AddHours(-1)), new("not today", Now.AddHours(-3))])).Day.Should().Be(PlannerDay.Today);
        Only(Item(1, labels: [new("today", Now.AddDays(-2)), new("not today", Now.AddDays(-1))])).Day.Should().Be(PlannerDay.Today);
    }

    [Fact]
    public void A_day_is_a_utc_day()
    {
        var lateYesterday = new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.Zero);

        Only(Item(1, labels: [new("not today", lateYesterday)])).Day.Should().Be(PlannerDay.Undecided);
    }

    [Fact]
    public void The_board_view_groups_under_the_top_parent_and_puts_a_sub_issue_under_its_row()
    {
        var board = BoardOf(
            Item(17, priority: "P1", subIssues: 4, subIssuesDone: 1),
            Item(21, priority: "P1", parent: Url(17), subIssues: 2),
            Item(24, priority: "P1", parent: Url(21)),
            Item(40, priority: "P0", parent: Url(21)),
            Item(27, priority: "P2", parent: Url(17)),
            Item(88, priority: "P0"),
            Item(57, priority: "P2"),
            Item(60, priority: "P1", parent: "https://github.com/acme/api/issues/9999"));

        var groups = PlannerQueue.Group(board, PlannerQueue.Build(board, Rules, Now));

        groups.Should().HaveCount(2);

        // Both hold a P0; the one with an epic's own priority comes before the one without an epic.
        groups[0].Epic!.Item.Number.Should().Be(17);
        groups[0].Rows.Select(r => (r.Entry.Item.Number, r.Depth)).Should().Equal((21, 0), (40, 1), (24, 1), (27, 0));

        groups[1].Epic.Should().BeNull();
        groups[1].Rows.Select(r => r.Entry.Item.Number).Should().Equal(88, 60, 57);
    }

    [Fact]
    public void A_group_comes_first_when_it_holds_the_highest_priority()
    {
        var board = BoardOf(
            Item(1, priority: "P2", subIssues: 1),
            Item(2, priority: "P2", parent: Url(1)),
            Item(3, priority: "P1", subIssues: 1),
            Item(4, priority: "P0", parent: Url(3)),
            Item(5, priority: "P1"));

        var groups = PlannerQueue.Group(board, PlannerQueue.Build(board, Rules, Now));

        groups.Select(g => g.Epic?.Item.Number).Should().Equal(3, null, 1);
    }

    [Fact]
    public void A_parent_whose_children_are_all_closed_is_a_row_like_any_other()
    {
        var board = BoardOf(Item(1, subIssues: 2, subIssuesDone: 2), Item(2, parent: Url(1), open: false, closedAt: Now.AddDays(-3)));

        var groups = PlannerQueue.Group(board, PlannerQueue.Build(board, Rules, Now));

        groups.Should().ContainSingle().Which.Epic.Should().BeNull();
    }
}
