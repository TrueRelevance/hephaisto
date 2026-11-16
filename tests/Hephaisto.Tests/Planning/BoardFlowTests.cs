using Hephaisto.Core.Planning;

namespace Hephaisto.Tests.Planning;

/// <summary>
/// The board's rules (<see cref="BoardFlow"/>): which Status an issue should have by what GitHub
/// says, and that a rule fires on news - never against a person who put a card back.
/// </summary>
public sealed class BoardFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Required = ["Priority"];

    private static Board BoardOf(params BoardItem[] items) => new(
        "PVT_1",
        "Platform planning",
        "https://github.com/orgs/acme/projects/3",
        [
            new BoardField("F_status", "Status", [new("s0", "Inbox"), new("s1", "Backlog"), new("s2", "Ready"), new("s3", "In progress"), new("s4", "In review"), new("s5", "Done"), new("s6", "Parked")]),
            new BoardField("F_prio", "Priority", [new("p0", "P0"), new("p1", "P1")]),
        ],
        items);

    private static BoardItem Item(
        int number,
        string? status,
        string? priority = "P1",
        string? type = "Task",
        bool open = true,
        DateTimeOffset? statusAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? closedAt = null,
        string[]? assignees = null,
        BoardPullRequest[]? pulls = null,
        string? parent = null,
        int subIssues = 0,
        int subIssuesDone = 0)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (status is not null)
        {
            fields["Status"] = status;
        }

        if (priority is not null)
        {
            fields["Priority"] = priority;
        }

        return new BoardItem(
            $"PVTI_{number}", $"I_{number}", "acme/api", number, $"Issue {number}", $"https://github.com/acme/api/issues/{number}", type, "reporter",
            open, Now.AddDays(-30), updatedAt ?? Now.AddDays(-3), closedAt, parent, subIssues, subIssuesDone, fields, [], assignees ?? [], pulls ?? [],
            FieldsChangedAt: statusAt is { } at ? new Dictionary<string, DateTimeOffset> { ["Status"] = at } : null);
    }

    private static BoardPullRequest Pull(int number, BoardPullState state, DateTimeOffset at) =>
        new("acme/api", number, $"https://github.com/acme/api/pull/{number}", state, at);

    private static IReadOnlyList<BoardMove> Moves(params BoardItem[] items) =>
        BoardFlow.Moves(BoardOf(items), BoardStatuses.Default, Required, "hephaisto-bot", Now);

    private static string[] Said(IReadOnlyList<BoardMove> moves) => [.. moves.Select(m => $"#{m.Item.Number} {m.From}->{m.To}")];

    [Fact]
    public void A_closed_issue_goes_to_done_and_one_done_already_stays()
    {
        Said(Moves(
            Item(1, "In review", open: false, closedAt: Now.AddHours(-1), statusAt: Now.AddDays(-2)),
            Item(2, "Done", open: false, closedAt: Now.AddHours(-1))))
            .Should().Equal("#1 InReview->Done");
    }

    [Fact]
    public void An_open_pull_request_puts_the_issue_in_review_once_and_a_person_moving_it_back_is_not_overruled()
    {
        var pr = Pull(9, BoardPullState.Open, Now.AddHours(-2));

        Moves(Item(1, "In progress", statusAt: Now.AddDays(-1), pulls: [pr])).Single().Should().BeEquivalentTo(new
        {
            From = BoardStage.InProgress,
            To = BoardStage.InReview,
            Why = "pull request acme/api#9 is open",
        });

        // Somebody put it back to In progress after the pull request was opened: the rule waits for news.
        Moves(Item(1, "In progress", statusAt: Now.AddHours(-1), pulls: [pr])).Should().BeEmpty();
    }

    [Fact]
    public void Merged_and_quiet_for_a_day_goes_from_in_progress_to_in_review()
    {
        var merged = Pull(9, BoardPullState.Merged, Now.AddDays(-2));

        Said(Moves(
            Item(1, "In progress", statusAt: Now.AddDays(-3), updatedAt: Now.AddDays(-2), pulls: [merged]),
            Item(2, "In progress", statusAt: Now.AddDays(-3), updatedAt: Now.AddHours(-2), pulls: [Pull(10, BoardPullState.Merged, Now.AddHours(-3))])))
            .Should().Equal("#1 InProgress->InReview");
    }

    [Fact]
    public void Assigned_to_the_agent_is_being_worked_on()
    {
        Said(Moves(
            Item(1, "Ready", statusAt: Now.AddDays(-2), updatedAt: Now.AddHours(-1), assignees: ["hephaisto-bot"]),
            Item(2, "Ready", statusAt: Now.AddDays(-2), updatedAt: Now.AddHours(-1), assignees: ["flo"])))
            .Should().Equal("#1 Ready->InProgress");

        // A person put it back a minute ago, and Hephaisto's line on that is what moved updated_at.
        Moves(Item(3, "Ready", statusAt: Now.AddMinutes(-1), updatedAt: Now.AddMinutes(-1).AddSeconds(2), assignees: ["hephaisto-bot"])).Should().BeEmpty();
    }

    [Fact]
    public void A_triaged_issue_leaves_the_inbox_and_one_without_its_fields_stays()
    {
        Said(Moves(
            Item(1, "Inbox"),
            Item(2, null),
            Item(3, "Inbox", priority: null),
            Item(4, "Inbox", type: null)))
            .Should().Equal("#1 Inbox->Backlog", "#2 None->Backlog");
    }

    [Fact]
    public void A_parent_whose_sub_issues_began_is_in_progress()
    {
        var parent = Item(1, "Backlog", subIssues: 2);

        Said(Moves(parent, Item(2, "In progress", parent: parent.Url), Item(3, "Backlog", parent: parent.Url)))
            .Should().Equal("#1 Backlog->InProgress");

        Said(Moves(Item(4, "Ready", subIssues: 2, subIssuesDone: 1))).Should().Equal("#4 Ready->InProgress");
    }

    [Fact]
    public void A_column_nobody_named_is_never_touched()
    {
        Moves(
            Item(1, "Parked", open: false, closedAt: Now),
            Item(2, "Parked", pulls: [Pull(9, BoardPullState.Open, Now)]))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_stage_the_board_has_no_column_for_is_not_asked_for()
    {
        var board = new Board("PVT_1", "b", "u", [new BoardField("F_status", "Status", [new("s0", "Todo"), new("s5", "Done")])],
            [Item(1, "Todo", pulls: [Pull(9, BoardPullState.Open, Now)])]);

        BoardFlow.Moves(board, BoardStatuses.Default with { Inbox = "Todo" }, Required, "hephaisto-bot", Now).Should().BeEmpty();
    }

    [Fact]
    public void A_pass_moves_at_most_its_share()
    {
        var items = Enumerable.Range(1, BoardFlow.MaxMovesPerPass + 10).Select(n => Item(n, "Inbox")).ToArray();

        Moves(items).Should().HaveCount(BoardFlow.MaxMovesPerPass);
    }

    [Fact]
    public void Statuses_are_read_whatever_their_case_and_a_click_only_moves_forward()
    {
        BoardStatuses.Default.Stage("in PROGRESS").Should().Be(BoardStage.InProgress);
        BoardStatuses.Default.Stage(null).Should().Be(BoardStage.None);
        BoardStatuses.Default.Stage("Parked").Should().Be(BoardStage.Unknown);

        BoardStatuses.MovesForward(BoardStage.Backlog, BoardStage.Ready).Should().BeTrue();
        BoardStatuses.MovesForward(BoardStage.InReview, BoardStage.Ready).Should().BeFalse();
        BoardStatuses.MovesForward(BoardStage.Unknown, BoardStage.Ready).Should().BeFalse();
    }
}
