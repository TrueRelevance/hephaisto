using System.Text.RegularExpressions;

namespace Hephaisto.Agent.Planner;

/// <summary>
/// A comment a person wrote in the planner (v0.15.0), as it is posted: by the agent's account,
/// beginning with a marker that names the person and ending with a line that says the same.
/// </summary>
/// <remarks>
/// The marker is what lets a replan pass the comment on as that person's answer: only the
/// agent's account can write a comment that begins with it, and the planner writes one only for
/// somebody with the approver role. A marker in anybody else's comment counts for nothing.
/// </remarks>
public static partial class PlannerComment
{
    private const string Prefix = "<!-- hephaisto:planner-comment:";

    public static string Body(string text, string person)
    {
        var name = Name(person);

        return $"{Prefix}{name} -->\n{text}\n\n<sub>Written by {name} in Hephaisto's planner.</sub>";
    }

    /// <summary>
    /// A comment an agent relayed through the MCP endpoint (#307): the words, and a line that says
    /// so and names the person it was for when one is known. No marker - only a person in the
    /// console writes a comment a replan may pass on as theirs.
    /// </summary>
    public static string Relayed(string text, string? person) =>
        person is null
            ? $"{text}\n\n<sub>Relayed by an agent through Hephaisto.</sub>"
            : $"{text}\n\n<sub>Relayed by an agent through Hephaisto, for {Name(person)}.</sub>";

    /// <summary>The person and their words, when <paramref name="body"/> is a planner comment.</summary>
    public static bool TryRead(string? body, out string person, out string text)
    {
        person = string.Empty;
        text = string.Empty;

        if (body is null || Marker().Match(body) is not { Success: true } m)
        {
            return false;
        }

        person = m.Groups["name"].Value;
        text = Signature().Replace(body[m.Length..], string.Empty).Trim();

        return true;
    }

    /// <summary>A name as the marker may hold it: letters, digits and a few signs, at most 64.</summary>
    internal static string Name(string person)
    {
        var kept = new string([.. (person ?? string.Empty).Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '@')]);

        return kept.Length == 0 ? "somebody" : kept[..Math.Min(kept.Length, 64)];
    }

    [GeneratedRegex(@"^<!-- hephaisto:planner-comment:(?<name>[\p{L}\p{N} ._@-]{1,64}) -->\n")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\n*<sub>Written by [^<]{1,100} in Hephaisto's planner\.</sub>\s*$")]
    private static partial Regex Signature();
}
