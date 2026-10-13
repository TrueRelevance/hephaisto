using System.Globalization;
using System.Text.RegularExpressions;

namespace Hephaisto.Core.Planning;

/// <summary>
/// Whether a pull request's description says it is FOR an issue: a closing keyword or
/// <c>Part of</c>, followed by the issue.
/// </summary>
/// <remarks>
/// GitHub lists the pull requests that will close an issue, and nothing else. A change that takes
/// three pull requests in three repositories closes its issue with the last one; the first two
/// say <c>Part of owner/repo#12</c>, and to GitHub that is a mention like any other. A mention
/// alone is not enough either - an issue is mentioned by every pull request that discusses it -
/// so the keyword decides.
/// </remarks>
public static partial class PullRequestMention
{
    /// <param name="body">The pull request's description.</param>
    /// <param name="issueRepository"><c>owner/repo</c> of the issue.</param>
    /// <param name="pullRequestRepository"><c>owner/repo</c> of the pull request: a bare <c>#12</c> means its own repository.</param>
    public static bool Names(string? body, string issueRepository, int number, string pullRequestRepository)
    {
        if (string.IsNullOrWhiteSpace(body) || number <= 0)
        {
            return false;
        }

        var n = number.ToString(CultureInfo.InvariantCulture);
        var sameRepository = string.Equals(issueRepository, pullRequestRepository, StringComparison.OrdinalIgnoreCase);
        var name = issueRepository.Contains('/', StringComparison.Ordinal) ? issueRepository[(issueRepository.IndexOf('/', StringComparison.Ordinal) + 1)..] : issueRepository;

        foreach (Match match in Keyword().Matches(body))
        {
            var target = match.Groups["target"].Value;

            if (string.Equals(target, $"https://github.com/{issueRepository}/issues/{n}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, $"{issueRepository}#{n}", StringComparison.OrdinalIgnoreCase)
                || (sameRepository && (target == $"#{n}" || string.Equals(target, $"{name}#{n}", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    // The target ends where the number ends: "#12" is not "#123", and a full stop after it is
    // the sentence's, not the reference's.
    [GeneratedRegex(
        @"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|part\s+of):?\s+(?<target>(?:https://github\.com/[A-Za-z0-9._/-]+/issues/|(?:[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]*#)\d+)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Keyword();
}
