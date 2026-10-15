namespace Hephaisto.Core.Planning;

/// <summary>One choice of a single-select field: its id is what GitHub is told, its name what a person reads.</summary>
public sealed record BoardOption(string Id, string Name);

/// <summary>A single-select field of the board - Status, Priority, whatever the board's owner added.</summary>
public sealed record BoardField(string Id, string Name, IReadOnlyList<BoardOption> Options);

/// <summary>A date field of the board - the day an issue is planned for, among others.</summary>
public sealed record BoardDateField(string Id, string Name);

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
/// <param name="Body">The issue's description as its author wrote it: markdown, and somebody else's text. Null when nobody wrote one.</param>
/// <param name="Comments">How many comments the issue has.</param>
/// <param name="Dates">The board's date fields that have a value on this item: field name to day.</param>
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
    IReadOnlyList<BoardPullRequest> PullRequests,
    string? Body = null,
    int Comments = 0,
    IReadOnlyDictionary<string, DateOnly>? Dates = null)
{
    public string Reference => $"{Repository}#{Number}";

    /// <summary>The value of a date field by its name, whatever its case. Null when it is empty on this item.</summary>
    public DateOnly? Date(string name) =>
        Dates?.FirstOrDefault(d => string.Equals(d.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Value : null;

    /// <summary>The value of a field by its name, whatever its case. Null when the field is empty on this item.</summary>
    public string? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A GitHub project as the planner sees it: its single-select fields and its issues.</summary>
/// <param name="IssueTypes">The issue types of the project's organisation that are enabled, for a new issue. Empty when the token may not read them.</param>
/// <param name="DateFields">The board's date fields.</param>
public sealed record Board(
    string Id,
    string Title,
    string Url,
    IReadOnlyList<BoardField> Fields,
    IReadOnlyList<BoardItem> Items,
    IReadOnlyList<string>? IssueTypes = null,
    IReadOnlyList<BoardDateField>? DateFields = null)
{
    public IReadOnlyList<string> Types => IssueTypes ?? [];

    public BoardDateField? DateField(string? name) =>
        name is null ? null : DateFields?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public BoardField? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}
