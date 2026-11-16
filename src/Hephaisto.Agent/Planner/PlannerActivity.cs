using System.Globalization;
using System.Text;

namespace Hephaisto.Agent.Planner;

/// <summary>
/// What happened to an issue through Hephaisto, kept on the issue itself: ONE comment by the
/// agent's account per issue, a line per click or per move of the board's rules, newest last.
/// GitHub is where somebody looks to see what happened, so that is where it is written - the
/// console's audit says the same, for whoever runs Hephaisto.
/// </summary>
/// <remarks>
/// <para>
/// <b>One comment, edited</b>, rather than a comment per click: the first line notifies the
/// issue's watchers once, every line after is an edit and notifies nobody. The project's own
/// timeline entries ("moved this to Ready") say which account moved a card; this says whose click
/// it was.
/// </para>
/// <para>
/// <b>Only the agent's own comment counts</b>: anybody can type the marker. A line is the
/// planner's words and a person's name, never a model's text - nothing in it needs neutralising
/// beyond the name.
/// </para>
/// </remarks>
public static class PlannerActivity
{
    public const string Marker = "<!-- hephaisto:activity -->";

    /// <summary>How many lines are kept; the oldest go first.</summary>
    public const int MaxLines = 150;

    private const string Heading = "**Through Hephaisto** - what was done to this issue in Hephaisto's planner, newest last.";

    private const string Bullet = "- ";

    /// <summary>One line: when, who, what.</summary>
    public static string Line(DateTimeOffset at, string who, string what) =>
        $"{at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · **{PlannerComment.Name(who)}** · {Clean(what)}";

    /// <summary>The comment with <paramref name="line"/> added: a new one when <paramref name="existing"/> is null.</summary>
    public static string Append(string? existing, string line)
    {
        var lines = Lines(existing).Append(Bullet + line).ToList();

        if (lines.Count > MaxLines)
        {
            lines = lines[^MaxLines..];
        }

        var text = new StringBuilder(Marker).Append('\n').Append(Heading).Append("\n\n");

        foreach (var l in lines)
        {
            text.Append(l).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The lines of an activity comment, oldest first; none for any other body.</summary>
    public static IReadOnlyList<string> Lines(string? body) =>
        body is null || !body.StartsWith(Marker, StringComparison.Ordinal)
            ? []
            : [.. body.Split('\n').Where(l => l.StartsWith(Bullet, StringComparison.Ordinal))];

    /// <summary>One line of words: no line breaks, no markup that would open a block, at most 300 characters.</summary>
    private static string Clean(string what)
    {
        var flat = string.Join(' ', (what ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        flat = flat.Replace("<", "&lt;", StringComparison.Ordinal).Replace("@", "@​", StringComparison.Ordinal);

        return flat.Length > 300 ? flat[..299] + "…" : flat;
    }
}
