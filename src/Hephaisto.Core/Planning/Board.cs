namespace Hephaisto.Core.Planning;

/// <summary>One choice of a single-select field: its id is what GitHub is told, its name what a person reads.</summary>
public sealed record BoardOption(string Id, string Name);

/// <summary>A single-select field of the board - Status, Priority, whatever the board's owner added.</summary>
public sealed record BoardField(string Id, string Name, IReadOnlyList<BoardOption> Options);

public enum BoardPullState
{
    Open = 0,
    Merged = 1,

    /// <summary>Closed without merging.</summary>
    Closed = 2,
}

/// <summary>A pull request that names an issue. <paramref name="At"/> is when it was merged, or last changed.</summary>
public sealed record BoardPullRequest(string Repository, int Number, string Url, BoardPullState State, DateTimeOffset At)
{
    public string Reference => $"{Repository}#{Number}";
}

/// <param name="AddedAt">When the label was last put on the issue. Null when its timeline no longer says.</param>
public sealed record BoardLabel(string Name, DateTimeOffset? AddedAt);

/// <summary>
/// One issue on the board, with the facts the planner decides by. Nothing here is the planner's
/// opinion: it is what GitHub said, gathered by the caller and passed in.
/// </summary>
/// <param name="ItemId">The project item's node id - what a field is set on.</param>
/// <param name="IssueId">The issue's node id - what is closed.</param>
/// <param name="Repository"><c>owner/repo</c>.</param>
/// <param name="ParentUrl">The issue this one is a sub-issue of, by its address.</param>
/// <param name="Fields">The board's single-select fields that have a value on this item: field name to option name.</param>
public sealed record BoardItem(
    string ItemId,
    string IssueId,
    string Repository,
    int Number,
    string Title,
    string Url,
    string? Type,
    string Author,
    bool Open,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string? ParentUrl,
    int SubIssues,
    int SubIssuesDone,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<BoardLabel> Labels,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<BoardPullRequest> PullRequests)
{
    public string Reference => $"{Repository}#{Number}";

    /// <summary>The value of a field by its name, whatever its case. Null when the field is empty on this item.</summary>
    public string? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A GitHub project as the planner sees it: its single-select fields and its issues.</summary>
public sealed record Board(
    string Id,
    string Title,
    string Url,
    IReadOnlyList<BoardField> Fields,
    IReadOnlyList<BoardItem> Items)
{
    public BoardField? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}
