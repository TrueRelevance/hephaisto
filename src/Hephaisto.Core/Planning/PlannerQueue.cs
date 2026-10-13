namespace Hephaisto.Core.Planning;

/// <summary>Why an item is in front of a person. The order of the members is the order of the queue.</summary>
public enum PlannerReason
{
    /// <summary>Every pull request that names the issue is merged, and the issue is still open.</summary>
    MergedStillOpen = 0,

    /// <summary>Every sub-issue is done, and the parent is still open.</summary>
    SubIssuesDone = 1,

    /// <summary>A pull request is open and nothing happened for <see cref="PlannerRules.StaleAfter"/>.</summary>
    PullRequestQuiet = 2,

    /// <summary>A field the board asks for is empty.</summary>
    FieldEmpty = 3,

    /// <summary>Nothing asks for a decision. The item is open, and that is all.</summary>
    None = 4,
}

/// <summary>What was decided about an item for the day.</summary>
public enum PlannerDay
{
    Undecided = 0,
    Today = 1,
    NotToday = 2,
}

/// <summary>
/// What the operator told the planner about their board. Everything else it reads off the board.
/// </summary>
/// <param name="RequiredFields">Names of fields an item should not leave empty. A name the board does not have is ignored.</param>
/// <param name="StaleAfter">How long an open pull request may be quiet.</param>
public sealed record PlannerRules(IReadOnlyList<string> RequiredFields, TimeSpan StaleAfter)
{
    public static readonly PlannerRules Default = new([], TimeSpan.FromDays(5));
}

/// <summary>One open item as the planner presents it.</summary>
/// <param name="EmptyFields">The required fields that are empty, by the board's own spelling.</param>
/// <param name="CarriedOver">Scheduled on an earlier day and still open.</param>
/// <param name="LastActivity">The newest of the issue's last change and its pull requests'.</param>
public sealed record PlannerEntry(
    BoardItem Item,
    PlannerReason Reason,
    IReadOnlyList<string> EmptyFields,
    PlannerDay Day,
    bool CarriedOver,
    DateTimeOffset? DayAt,
    DateTimeOffset LastActivity)
{
    public bool NeedsDecision => Reason != PlannerReason.None;
}

/// <summary>A row of the board view: an entry, and how deep under its group's head it sits.</summary>
public sealed record PlannerRow(PlannerEntry Entry, int Depth);

/// <summary>
/// The open items under one top-level parent, or - with no <see cref="Epic"/> - the ones that
/// have none.
/// </summary>
public sealed record PlannerGroup(PlannerEntry? Epic, IReadOnlyList<PlannerRow> Rows);

/// <summary>
/// The planner's rules, as pure functions over a board somebody else read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Facts, not column names.</b> A reason is something GitHub says about the issue - its pull
/// requests, its sub-issues, an empty field. No rule knows what a board calls its columns, so
/// the same rules hold for a board with five statuses and for one with three.
/// </para>
/// <para>
/// <b>The day is two labels and their age.</b> <see cref="TodayLabel"/> stays on an issue until
/// somebody takes it off or the issue is closed: scheduled yesterday and still open is
/// <see cref="PlannerEntry.CarriedOver"/>, not forgotten. <see cref="NotTodayLabel"/> counts only
/// on the day it was set - a put-away item comes back the next morning without anybody, or any
/// timer, having to remove a label. A day is a UTC day.
/// </para>
/// </remarks>
public static class PlannerQueue
{
    public const string TodayLabel = "today";
    public const string NotTodayLabel = "not today";

    /// <summary>The field every GitHub project has, by its default name. Its options are the board's columns.</summary>
    public const string StatusField = "Status";

    /// <summary>The field the queue is ordered by when the board has one of this name.</summary>
    public const string PriorityField = "Priority";

    /// <summary>
    /// Every open item, in the order a person should meet them: what asks for a decision first,
    /// by <see cref="PlannerReason"/>; within a reason by priority, then by the board's columns
    /// from the last to the first - what is nearest to done is decided soonest.
    /// </summary>
    public static IReadOnlyList<PlannerEntry> Build(Board board, PlannerRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(rules);

        var required = rules.RequiredFields
            .Select(name => board.Field(name)?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return [.. board.Items
            .Where(item => item.Open)
            .Select(item => Entry(item, required, rules, now))
            .OrderBy(entry => (int)entry.Reason)
            .ThenBy(entry => PriorityRank(board, entry.Item))
            .ThenByDescending(entry => StatusRank(board, entry.Item))
            .ThenBy(entry => entry.Item.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Item.Number)];
    }

    /// <summary>What was closed on the day of <paramref name="now"/>, newest first.</summary>
    public static IReadOnlyList<BoardItem> ClosedToday(Board board, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);

        return [.. board.Items
            .Where(item => !item.Open && item.ClosedAt is { } closed && SameDay(closed, now))
            .OrderByDescending(item => item.ClosedAt)];
    }

    /// <summary>
    /// The entries grouped under their top-level parent. A group comes first when it holds the
    /// highest priority; inside a group the order is by priority, and a sub-issue of a row
    /// follows that row, one step in.
    /// </summary>
    public static IReadOnlyList<PlannerGroup> Group(Board board, IReadOnlyList<PlannerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(entries);

        var byUrl = entries
            .GroupBy(e => e.Item.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        PlannerEntry? ParentOf(PlannerEntry entry) =>
            entry.Item.ParentUrl is { } url && byUrl.TryGetValue(url, out var parent) && !ReferenceEquals(parent, entry) ? parent : null;

        // The top of an entry's chain of parents, as far as the board holds it. A chain that
        // loops - GitHub does not allow one, a stand-in might - ends where it started.
        PlannerEntry RootOf(PlannerEntry entry)
        {
            var seen = new HashSet<PlannerEntry>(ReferenceEqualityComparer.Instance) { entry };
            var current = entry;

            while (ParentOf(current) is { } parent && seen.Add(parent))
            {
                current = parent;
            }

            return current;
        }

        // By the parent itself, not by what it equals: two entries are records, and two records
        // with the same content are one key.
        var children = new Dictionary<PlannerEntry, List<PlannerEntry>>(ReferenceEqualityComparer.Instance);

        foreach (var entry in entries)
        {
            if (ParentOf(entry) is not { } parent)
            {
                continue;
            }

            if (!children.TryGetValue(parent, out var siblings))
            {
                children[parent] = siblings = [];
            }

            siblings.Add(entry);
        }

        foreach (var siblings in children.Values)
        {
            siblings.Sort((x, y) =>
                PriorityRank(board, x.Item).CompareTo(PriorityRank(board, y.Item)) is var byPriority and not 0 ? byPriority
                : x.Item.Number.CompareTo(y.Item.Number) is var byNumber and not 0 ? byNumber
                : string.CompareOrdinal(x.Item.Repository, y.Item.Repository));
        }

        var groups = new List<PlannerGroup>();
        var alone = new List<PlannerRow>();

        foreach (var root in entries.Where(e => ReferenceEquals(RootOf(e), e)))
        {
            if (!children.ContainsKey(root))
            {
                alone.Add(new PlannerRow(root, 0));
                continue;
            }

            var rows = new List<PlannerRow>();
            Descend(root, 0);
            groups.Add(new PlannerGroup(root, rows));

            void Descend(PlannerEntry parent, int depth)
            {
                foreach (var child in children.TryGetValue(parent, out var own) ? own : [])
                {
                    rows.Add(new PlannerRow(child, depth));
                    Descend(child, depth + 1);
                }
            }
        }

        if (alone.Count > 0)
        {
            groups.Add(new PlannerGroup(null, [.. alone.OrderBy(r => PriorityRank(board, r.Entry.Item)).ThenBy(r => r.Entry.Item.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Entry.Item.Number)]));
        }

        return [.. groups
            .OrderBy(g => g.Rows.Min(r => PriorityRank(board, r.Entry.Item)))
            .ThenBy(g => g.Epic is null ? int.MaxValue : PriorityRank(board, g.Epic.Item))
            .ThenBy(g => g.Epic is null ? 1 : 0)
            .ThenBy(g => g.Epic?.Item.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Epic?.Item.Number ?? 0)];
    }

    /// <summary>Where an item's priority stands among the options of the board's Priority field. No priority is last.</summary>
    public static int PriorityRank(Board board, BoardItem item) => Rank(board.Field(PriorityField), item.Field(PriorityField));

    private static int StatusRank(Board board, BoardItem item) =>
        Rank(board.Field(StatusField), item.Field(StatusField)) is var rank and < int.MaxValue ? rank : -1;

    private static int Rank(BoardField? field, string? value)
    {
        if (field is null || value is null)
        {
            return int.MaxValue;
        }

        for (var i = 0; i < field.Options.Count; i++)
        {
            if (string.Equals(field.Options[i].Name, value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static PlannerEntry Entry(BoardItem item, List<string> required, PlannerRules rules, DateTimeOffset now)
    {
        var lastActivity = item.PullRequests.Select(p => p.At).Append(item.UpdatedAt).Max();
        var open = item.PullRequests.Any(p => p.State == BoardPullState.Open);
        var merged = item.PullRequests.Any(p => p.State == BoardPullState.Merged);
        var empty = required.Where(name => item.Field(name) is null).ToList();

        // A parent is judged by its sub-issues: the pull requests that name it are its
        // children's work, and "all merged" says nothing about the ones not started.
        var reason = item.SubIssues > 0
            ? item.SubIssuesDone >= item.SubIssues ? PlannerReason.SubIssuesDone
                : empty.Count > 0 ? PlannerReason.FieldEmpty : PlannerReason.None
            : merged && !open ? PlannerReason.MergedStillOpen
            : open && now - lastActivity >= rules.StaleAfter ? PlannerReason.PullRequestQuiet
            : empty.Count > 0 ? PlannerReason.FieldEmpty
            : PlannerReason.None;

        var (day, carried, at) = DayOf(item, now);

        return new PlannerEntry(item, reason, empty, day, carried, at, lastActivity);
    }

    /// <summary>
    /// The day's decision from the two labels. With both on an issue the newer one counts; a
    /// label whose time is not known is taken to be old.
    /// </summary>
    internal static (PlannerDay Day, bool CarriedOver, DateTimeOffset? At) DayOf(BoardItem item, DateTimeOffset now)
    {
        var today = Label(item, TodayLabel);
        var notToday = Label(item, NotTodayLabel);

        var putAway = notToday is { AddedAt: { } away } && SameDay(away, now);

        if (today is not null && (!putAway || (today.AddedAt ?? DateTimeOffset.MinValue) > notToday!.AddedAt))
        {
            return (PlannerDay.Today, today.AddedAt is not { } at || !SameDay(at, now), today.AddedAt);
        }

        return putAway ? (PlannerDay.NotToday, false, notToday!.AddedAt) : (PlannerDay.Undecided, false, null);
    }

    private static BoardLabel? Label(BoardItem item, string name) =>
        item.Labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool SameDay(DateTimeOffset a, DateTimeOffset b) => a.UtcDateTime.Date == b.UtcDateTime.Date;
}
