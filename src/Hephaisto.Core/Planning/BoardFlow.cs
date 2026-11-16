namespace Hephaisto.Core.Planning;

/// <summary>
/// What a column of the board means, whatever the board calls it. The order is the order work
/// goes through: a move "forward" never goes back to an earlier stage.
/// </summary>
public enum BoardStage
{
    /// <summary>A Status the operator did not name: somebody's own column, which no rule touches.</summary>
    Unknown = -1,

    /// <summary>No Status at all.</summary>
    None = 0,

    /// <summary>New, not triaged.</summary>
    Inbox = 1,

    /// <summary>Accepted, not scheduled.</summary>
    Backlog = 2,

    /// <summary>Next up.</summary>
    Ready = 3,

    /// <summary>Being worked on.</summary>
    InProgress = 4,

    /// <summary>A pull request is open, or the work waits for verification and deploy.</summary>
    InReview = 5,

    Done = 6,
}

/// <summary>The board's Status options for each stage, by the board's own spelling.</summary>
public sealed record BoardStatuses(string Inbox, string Backlog, string Ready, string InProgress, string InReview, string Done)
{
    public static readonly BoardStatuses Default = new("Inbox", "Backlog", "Ready", "In progress", "In review", "Done");

    public static readonly BoardStage[] Flow =
        [BoardStage.Inbox, BoardStage.Backlog, BoardStage.Ready, BoardStage.InProgress, BoardStage.InReview, BoardStage.Done];

    /// <summary>The stage a Status value stands for. Empty is <see cref="BoardStage.None"/>, a name not configured <see cref="BoardStage.Unknown"/>.</summary>
    public BoardStage Stage(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return BoardStage.None;
        }

        foreach (var stage in Flow)
        {
            if (string.Equals(Name(stage), status.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return stage;
            }
        }

        return BoardStage.Unknown;
    }

    public string? Name(BoardStage stage) => stage switch
    {
        BoardStage.Inbox => Inbox,
        BoardStage.Backlog => Backlog,
        BoardStage.Ready => Ready,
        BoardStage.InProgress => InProgress,
        BoardStage.InReview => InReview,
        BoardStage.Done => Done,
        _ => null,
    };

    /// <summary>
    /// Whether a click that means <paramref name="to"/> moves an item that stands at
    /// <paramref name="from"/>: only forward, and never out of a column nobody named.
    /// </summary>
    public static bool MovesForward(BoardStage from, BoardStage to) => from != BoardStage.Unknown && from < to;
}

/// <summary>One Status change the board's rules ask for, and why - in words that go on the issue.</summary>
public sealed record BoardMove(BoardItem Item, BoardStage From, BoardStage To, string Why);

/// <summary>
/// The board's rules: the Status an issue should have by what GitHub says of it. Pure, over a board
/// somebody else read. <c>planning/README.md</c> of TR Planning is where they come from; Hephaisto
/// applies them on every read of the board (<c>github.planner.keepStatus</c>), so nobody has to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rule fires on news, not on a state.</b> A move is asked for only when the fact behind it
/// is newer than the item's last Status change, where the board says when that was. A person who
/// puts a card back where they want it is not overruled two minutes later; the next fact is.
/// </para>
/// <para>
/// <b>A column nobody named is left alone</b> (<see cref="BoardStage.Unknown"/>): only the six
/// stages are the rules' business.
/// </para>
/// </remarks>
public static class BoardFlow
{
    /// <summary>How long an issue in progress whose pull requests are all merged stays there before it is put in review.</summary>
    public static readonly TimeSpan MergedQuietFor = TimeSpan.FromDays(1);

    /// <summary>How long after a Status change the issue's own <c>updated_at</c> may be Hephaisto's activity line rather than news.</summary>
    public static readonly TimeSpan OwnWriteSlack = TimeSpan.FromMinutes(2);

    /// <summary>At most this many moves per read: a board that was never kept is put in order over a few reads, not in one burst.</summary>
    public const int MaxMovesPerPass = 25;

    public static IReadOnlyList<BoardMove> Moves(Board board, BoardStatuses statuses, IReadOnlyList<string> requiredFields, string? botLogin, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(statuses);

        if (board.Field(PlannerQueue.StatusField) is not { } status)
        {
            return [];
        }

        bool Has(BoardStage stage) =>
            statuses.Name(stage) is { } name && status.Options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        var required = (requiredFields ?? [])
            .Select(name => board.Field(name)?.Name)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stageOf = board.Items.ToDictionary(i => i.ItemId, i => statuses.Stage(i.Field(PlannerQueue.StatusField)));
        var childrenOf = board.Items
            .Where(i => i.ParentUrl is not null)
            .GroupBy(i => i.ParentUrl!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var moves = new List<BoardMove>();

        foreach (var item in board.Items.OrderBy(i => i.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Number))
        {
            var from = stageOf[item.ItemId];

            if (from == BoardStage.Unknown)
            {
                continue;
            }

            var since = item.FieldChangedAt(PlannerQueue.StatusField);
            bool Newer(DateTimeOffset? fact) => since is null || (fact is { } at && at > since);

            var move = Rule(item, from, Newer, childrenOf, stageOf, required, botLogin, now);

            if (move is { } m && m.To != from && Has(m.To))
            {
                moves.Add(m);

                if (moves.Count >= MaxMovesPerPass)
                {
                    break;
                }
            }
        }

        return moves;
    }

    private static BoardMove? Rule(
        BoardItem item,
        BoardStage from,
        Func<DateTimeOffset?, bool> newer,
        Dictionary<string, List<BoardItem>> childrenOf,
        Dictionary<string, BoardStage> stageOf,
        List<string> required,
        string? botLogin,
        DateTimeOffset now)
    {
        if (!item.Open)
        {
            return from != BoardStage.Done && newer(item.ClosedAt)
                ? new BoardMove(item, from, BoardStage.Done, "the issue is closed")
                : null;
        }

        var open = item.PullRequests.Where(p => p.State == BoardPullState.Open).ToList();

        if (open.Count > 0 && from is >= BoardStage.None and < BoardStage.InReview && newer(open.Max(p => p.At)))
        {
            return new BoardMove(item, from, BoardStage.InReview, $"pull request {open.OrderByDescending(p => p.At).First().Reference} is open");
        }

        var merged = item.PullRequests.Where(p => p.State == BoardPullState.Merged).ToList();
        var lastActivity = item.PullRequests.Select(p => p.At).Append(item.UpdatedAt).Max();

        if (from == BoardStage.InProgress && merged.Count > 0 && open.Count == 0 && now - lastActivity >= MergedQuietFor && newer(merged.Max(p => p.At)))
        {
            return new BoardMove(item, from, BoardStage.InReview, "every pull request is merged: it waits for verification and deploy");
        }

        // The issue's updated_at moves with every comment - Hephaisto's own line on it too, a
        // second after a person moved the card. So the assignment counts as news only well after
        // the last Status change, not on that line.
        if (botLogin is not null && from is >= BoardStage.None and < BoardStage.InProgress
            && item.Assignees.Any(a => string.Equals(a, botLogin, StringComparison.OrdinalIgnoreCase)) && newer(item.UpdatedAt - OwnWriteSlack))
        {
            return new BoardMove(item, from, BoardStage.InProgress, $"it is assigned to {botLogin}, who works on it");
        }

        if (item.SubIssues > 0 && from is BoardStage.Backlog or BoardStage.Ready)
        {
            var started = (childrenOf.TryGetValue(item.Url, out var children) ? children : [])
                .Any(c => stageOf[c.ItemId] is BoardStage.InProgress or BoardStage.InReview or BoardStage.Done || !c.Open);

            if (started || item.SubIssuesDone > 0)
            {
                return new BoardMove(item, from, BoardStage.InProgress, "work on its sub-issues has begun");
            }
        }

        if (from is BoardStage.None or BoardStage.Inbox && item.Type is not null && required.All(name => item.Field(name) is not null))
        {
            return new BoardMove(item, from, BoardStage.Backlog,
                required.Count == 0 ? "it has a type" : $"it has a type and {string.Join(", ", required)}");
        }

        return null;
    }
}
