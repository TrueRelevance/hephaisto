using Hephaisto.Core.Planning;

namespace Hephaisto.Tests.Planning;

/// <summary>
/// A pull request is FOR an issue when it says so with a keyword. The table is the rule: add the
/// row before changing the expression.
/// </summary>
public sealed class PullRequestMentionTests
{
    [Theory]
    // The closing keywords GitHub knows, and the one it does not: "Part of".
    [InlineData("Closes acme/api#12", "acme/api", "acme/api", true)]
    [InlineData("closes acme/api#12.", "acme/api", "acme/web", true)]
    [InlineData("Fixes #12", "acme/api", "acme/api", true)]
    [InlineData("Resolved: #12", "acme/api", "acme/api", true)]
    [InlineData("Part of acme/api#12", "acme/api", "acme/web", true)]
    [InlineData("part  of https://github.com/acme/api/issues/12", "acme/api", "acme/web", true)]
    [InlineData("First line.\n\nPart of acme/api#12\n", "acme/api", "acme/web", true)]
    [InlineData("Closes api#12", "acme/api", "acme/api", true)]
    // A bare number means the pull request's own repository.
    [InlineData("Closes #12", "acme/api", "acme/web", false)]
    [InlineData("Closes api#12", "acme/api", "acme/web", false)]
    // Another number, another repository, another owner.
    [InlineData("Closes acme/api#123", "acme/api", "acme/api", false)]
    [InlineData("Closes acme/web#12", "acme/api", "acme/api", false)]
    [InlineData("Closes other/api#12", "acme/api", "acme/api", false)]
    [InlineData("Closes https://github.com/acme/api/issues/123", "acme/api", "acme/api", false)]
    // A mention is not a claim.
    [InlineData("See acme/api#12 for the discussion", "acme/api", "acme/api", false)]
    [InlineData("This does not close anything. #12 is related.", "acme/api", "acme/api", false)]
    [InlineData("", "acme/api", "acme/api", false)]
    [InlineData(null, "acme/api", "acme/api", false)]
    public void A_pull_request_names_an_issue_only_with_a_keyword(string? body, string issueRepository, string pullRequestRepository, bool expected)
    {
        PullRequestMention.Names(body, issueRepository, 12, pullRequestRepository).Should().Be(expected);
    }
}
