using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// One open issue of an organisation, as the search answers it (#312): an issue's own facts and
/// what the review asks of it before it reads more.
/// </summary>
/// <param name="Repository"><c>owner/repo</c>, from the item's <c>repository_url</c>.</param>
/// <param name="Comments">How many comments it has.</param>
/// <param name="SubIssues">How many sub-issues it has, open and closed; 0 when GitHub did not say.</param>
public sealed record GitHubOrgIssue(
    string Repository,
    GitHubIssue Issue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Comments,
    int SubIssues)
{
    public string Reference => $"{Repository}#{Issue.Number}";
}

/// <param name="Total">What the search says it found. More than it will ever list beyond a thousand.</param>
/// <param name="Incomplete">GitHub ran out of time and says the results may be missing some.</param>
/// <param name="HasMore">There is a next page.</param>
public sealed record GitHubSearchPage(IReadOnlyList<GitHubOrgIssue> Issues, int Total, bool Incomplete, bool HasMore);

/// <summary>A pull request or an issue that mentions this one: a <c>cross-referenced</c> event.</summary>
/// <param name="State"><c>open</c>, <c>closed</c> or <c>merged</c>.</param>
/// <param name="ClosedAt">When it was merged or closed.</param>
public sealed record GitHubCrossReference(bool PullRequest, string Repository, int Number, string Title, string State, DateTimeOffset? At, DateTimeOffset? ClosedAt)
{
    public string Reference => $"{Repository}#{Number}";
}

/// <summary>A commit whose message names the issue: a <c>referenced</c> event.</summary>
public sealed record GitHubCommitReference(string Sha, string Repository, DateTimeOffset? At);

public sealed record GitHubTimelineComment(string Author, DateTimeOffset CreatedAt, string Body);

/// <param name="Complete">Every event was read. False when the timeline held more than its first and last pages.</param>
public sealed record GitHubIssueTimeline(
    IReadOnlyList<GitHubCrossReference> References,
    IReadOnlyList<GitHubCommitReference> Commits,
    IReadOnlyList<GitHubTimelineComment> Comments,
    bool Complete);

/// <param name="StateReason">GitHub's: completed, not_planned, duplicate, reopened. Null while open.</param>
public sealed record GitHubSubIssue(string Repository, int Number, string Title, string State, string? StateReason)
{
    public string Reference => $"{Repository}#{Number}";
}

/// <summary>
/// The weekly review's reads (#312): an organisation's open issues, an issue's timeline, its
/// sub-issues. Reads only - the review proposes and writes nothing to GitHub.
/// </summary>
public sealed partial class GitHubClient
{
    public Task<GitHubResult<GitHubSearchPage>> SearchOpenIssuesAsync(string owner, int page, CancellationToken ct)
    {
        // `is:issue` is required since 2025: without it the search answers 422. Oldest first, so a
        // page that was read does not shift while new issues are opened during the run.
        var q = Uri.EscapeDataString($"org:{owner} is:issue is:open");

        return SendAsync(
            HttpMethod.Get,
            $"search/issues?q={q}&sort=created&order=asc&per_page={PageSize}&page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}",
            null,
            null,
            (json, response) =>
            {
                var items = json.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];

                return new GitHubSearchPage(
                    [.. items.Where(i => !i.TryGetProperty("pull_request", out _)).Select(OrgIssue).Where(i => i is not null).Select(i => i!)],
                    json.TryGetProperty("total_count", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : items.Count,
                    json.TryGetProperty("incomplete_results", out var incomplete) && incomplete.ValueKind == JsonValueKind.True,
                    HasNextPage(response));
            },
            ct,
            search: true);
    }

    public async Task<GitHubResult<GitHubIssueTimeline>> ReadTimelineAsync(string repository, int number, CancellationToken ct)
    {
        var path = $"repos/{repository}/issues/{Number(number)}/timeline?per_page={PageSize}";

        var first = await SendAsync(HttpMethod.Get, path, null, null, (json, response) => (Events: json.EnumerateArray().ToList().Select(e => e.Clone()).ToList(), Last: LastPage(response)), ct).ConfigureAwait(false);

        if (!first.Ok)
        {
            return new(first.Outcome, null, first.Detail, RetryAt: first.RetryAt);
        }

        var (events, last) = first.Value;

        if (last > 1)
        {
            // The newest references and comments are at the end of a long timeline.
            var end = await SendAsync(
                HttpMethod.Get, $"{path}&page={last.ToString(CultureInfo.InvariantCulture)}", null, null, (json, _) => json.EnumerateArray().Select(e => e.Clone()).ToList(), ct).ConfigureAwait(false);

            if (!end.Ok)
            {
                return new(end.Outcome, null, end.Detail, RetryAt: end.RetryAt);
            }

            events.AddRange(end.Value!);
        }

        return new(GitHubOutcome.Ok, Timeline(events, complete: last <= 2));
    }

    public Task<GitHubResult<IReadOnlyList<GitHubSubIssue>>> ListSubIssuesAsync(string repository, int number, CancellationToken ct) =>
        SendAsync<IReadOnlyList<GitHubSubIssue>>(
            HttpMethod.Get,
            $"repos/{repository}/issues/{Number(number)}/sub_issues?per_page={PageSize}",
            null,
            null,
            (json, _) => [.. json.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                .Select(i => RepositoryOf(i) is { } repo
                    ? new GitHubSubIssue(repo, i.GetProperty("number").GetInt32(), Text(i, "title") ?? string.Empty, Text(i, "state") ?? "open", Text(i, "state_reason"))
                    : null)
                .Where(i => i is not null)
                .Select(i => i!)],
            ct);

    private static GitHubOrgIssue? OrgIssue(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || RepositoryOf(json) is not { } repository)
        {
            return null;
        }

        var subIssues = json.TryGetProperty("sub_issues_summary", out var summary) && summary.ValueKind == JsonValueKind.Object
            && summary.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number
                ? total.GetInt32()
                : 0;

        return new GitHubOrgIssue(
            repository,
            Issue(json),
            Moment(json, "created_at") ?? DateTimeOffset.MinValue,
            Moment(json, "updated_at") ?? DateTimeOffset.MinValue,
            json.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Number ? comments.GetInt32() : 0,
            subIssues);
    }

    /// <summary>
    /// The events of a timeline the review reads, in the order GitHub gave them. Everything else
    /// - labels, assignments, renames - is passed over.
    /// </summary>
    private static GitHubIssueTimeline Timeline(List<JsonElement> events, bool complete)
    {
        var references = new List<GitHubCrossReference>();
        var commits = new List<GitHubCommitReference>();
        var comments = new List<GitHubTimelineComment>();

        foreach (var e in events.Where(e => e.ValueKind == JsonValueKind.Object))
        {
            switch (Text(e, "event"))
            {
                case "cross-referenced"
                    when e.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("issue", out var other) && other.ValueKind == JsonValueKind.Object
                        && other.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number
                        && RepositoryOf(other) is { } repository:
                {
                    // A pull request comes AS AN ISSUE: open or closed, and only its
                    // pull_request.merged_at says it was merged.
                    var pull = other.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object;
                    var mergedAt = pull ? Moment(pr, "merged_at") : null;
                    var state = mergedAt is not null ? "merged" : string.Equals(Text(other, "state"), "closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open";

                    references.Add(new GitHubCrossReference(
                        pull, repository, number.GetInt32(), Text(other, "title") ?? string.Empty, state,
                        Moment(e, "created_at"), mergedAt ?? Moment(other, "closed_at")));
                    break;
                }

                case "referenced" when Text(e, "commit_id") is { } sha && CommitSha().IsMatch(sha):
                {
                    var repo = Text(e, "commit_url") is { } url && CommitRepository().Match(url) is { Success: true } m ? m.Groups[1].Value : null;

                    if (repo is not null)
                    {
                        commits.Add(new GitHubCommitReference(sha, repo, Moment(e, "created_at")));
                    }

                    break;
                }

                case "commented" when Moment(e, "created_at") is { } at:
                {
                    var author = e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Text(user, "login")
                        : e.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object ? Text(actor, "login")
                        : null;

                    comments.Add(new GitHubTimelineComment(string.IsNullOrWhiteSpace(author) ? "ghost" : author, at, Text(e, "body") ?? string.Empty));
                    break;
                }
            }
        }

        return new GitHubIssueTimeline(references, commits, comments, complete);
    }

    /// <summary>
    /// <c>owner/repo</c> of an issue as REST answers it: its <c>repository</c> object when there is
    /// one, otherwise the last two segments of <c>repository_url</c> - never followed, only read.
    /// </summary>
    private static string? RepositoryOf(JsonElement issue)
    {
        var name = issue.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object ? Text(repo, "full_name") : null;

        if (name is null && Text(issue, "repository_url") is { } url)
        {
            var parts = url.TrimEnd('/').Split('/');
            name = parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : null;
        }

        return name is not null && OwnerRepo().IsMatch(name) ? name : null;
    }

    private static DateTimeOffset? Moment(JsonElement json, string name) =>
        Text(json, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/(?!\.\.?$)[A-Za-z0-9._-]+$")]
    private static partial Regex OwnerRepo();

    [GeneratedRegex(@"^[0-9a-f]{7,64}$")]
    private static partial Regex CommitSha();

    [GeneratedRegex(@"/repos/([A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+)/commits/")]
    private static partial Regex CommitRepository();
}
