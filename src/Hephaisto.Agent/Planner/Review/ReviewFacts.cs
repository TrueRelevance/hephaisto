using System.Globalization;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>The organisation's open issues as the search listed them.</summary>
/// <param name="Complete">Every open issue is in <see cref="Issues"/>: the search was not cut off. Only then is an issue missing from it taken for closed.</param>
/// <param name="Note">Why the listing is not complete, when it is not.</param>
public sealed record ReviewListing(IReadOnlyList<GitHubOrgIssue> Issues, bool Complete, string? Note);

/// <summary>
/// The facts the weekly review hands a model (#312), gathered from GitHub before any model runs:
/// the organisation's open issues through the search, each repository's archived flag once per
/// run, and for an issue that is asked about its timeline (the pull requests, issues and commits
/// that reference it, its last comments) and its sub-issues. The board's facts - status,
/// priority, how long in that status - are the planner's kept board, not a read of their own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only.</b> Nothing here is a write, and a test holds it so (<c>ReviewRunnerTests</c>).
/// </para>
/// <para>
/// <b>Bounded</b> by the contract's caps (review-request.schema.json) and by GitHub's: the search
/// lists at most a thousand issues, ten pages; a timeline is read at its first and last page; the
/// sub-issues are asked for only when the issue has any. An issue costs two to four requests and
/// only the ones a run asks about cost any.
/// </para>
/// <para>
/// <b>A rate limit is waited out, once, when it ends soon</b> (<see cref="MaxWait"/>). The client
/// holds the limit itself and answers every call until it ends without asking GitHub, so this is
/// not a retry against GitHub: it is waiting for the time GitHub named, which a weekly pass over
/// a few hundred issues meets more often than a poller does. A longer limit ends the gathering,
/// and the run says so.
/// </para>
/// </remarks>
public sealed class ReviewFactsGatherer(IGitHubClient client, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>GitHub's search answers no more than the first thousand results.</summary>
    public const int MaxSearchPages = 10;

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private const int MaxBody = 3000;
    private const int MaxComment = 1000;
    private const int MaxTitle = 300;

    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    /// <summary>Every open issue of the organisation, or why they could not be listed.</summary>
    public async Task<GitHubResult<ReviewListing>> ListOpenAsync(string owner, CancellationToken ct)
    {
        var issues = new List<GitHubOrgIssue>();
        var total = 0;
        var incomplete = false;

        for (var page = 1; page <= MaxSearchPages; page++)
        {
            var n = page;
            var read = await AskAsync(() => client.SearchOpenIssuesAsync(owner, n, ct), ct).ConfigureAwait(false);

            if (!read.Ok)
            {
                return new(read.Outcome, null, $"the open issues of {owner} could not be listed: {read.Describe()}", RetryAt: read.RetryAt);
            }

            issues.AddRange(read.Value!.Issues);
            total = Math.Max(total, read.Value.Total);
            incomplete |= read.Value.Incomplete;

            if (!read.Value.HasMore)
            {
                break;
            }
        }

        var distinct = issues.DistinctBy(i => i.Reference, StringComparer.OrdinalIgnoreCase).ToList();
        var note = incomplete
            ? "GitHub's search said its results may be incomplete, so no issue missing from them was taken for closed."
            : total > distinct.Count
                ? $"{owner} has {total} open issues and the search lists the first {distinct.Count}; the others are not reviewed, and none is taken for closed."
                : null;

        return new(GitHubOutcome.Ok, new ReviewListing(distinct, note is null, note));
    }

    /// <summary>Each repository once: whether it is archived and its default branch. Null for one that could not be asked.</summary>
    public async Task<IReadOnlyDictionary<string, GitHubRepository?>> RepositoriesAsync(IEnumerable<string> repositories, CancellationToken ct)
    {
        var found = new Dictionary<string, GitHubRepository?>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in repositories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asked = await AskAsync(() => client.GetRepositoryAsync(repository, ct), ct).ConfigureAwait(false);
            found[repository] = asked.Ok ? asked.Value : null;
        }

        return found;
    }

    /// <summary>One issue's facts for the request, or why they could not be read.</summary>
    public async Task<GitHubResult<ReviewIssue>> IssueAsync(GitHubOrgIssue found, BoardItem? item, bool archived, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(found);

        var timeline = await AskAsync(() => client.ReadTimelineAsync(found.Repository, found.Issue.Number, ct), ct).ConfigureAwait(false);

        if (!timeline.Ok)
        {
            return new(timeline.Outcome, null, $"the timeline of {found.Reference} could not be read: {timeline.Describe()}", RetryAt: timeline.RetryAt);
        }

        IReadOnlyList<GitHubSubIssue> subIssues = [];

        if (found.SubIssues > 0 || item is { SubIssues: > 0 })
        {
            var read = await AskAsync(() => client.ListSubIssuesAsync(found.Repository, found.Issue.Number, ct), ct).ConfigureAwait(false);

            if (!read.Ok)
            {
                return new(read.Outcome, null, $"the sub-issues of {found.Reference} could not be read: {read.Describe()}", RetryAt: read.RetryAt);
            }

            subIssues = read.Value!;
        }

        return new(GitHubOutcome.Ok, Build(found, item, archived, timeline.Value!, subIssues, clock.UtcNow));
    }

    /// <summary>The request's issue, from what was read. Pure; every cap is the schema's.</summary>
    internal static ReviewIssue Build(GitHubOrgIssue found, BoardItem? item, bool archived, GitHubIssueTimeline timeline, IReadOnlyList<GitHubSubIssue> subIssues, DateTimeOffset now)
    {
        var issue = found.Issue;
        var self = found.Reference;

        // The board's closing pull requests beside the timeline's references: GitHub lists a
        // closing one in both, and an old one only on the board once the timeline is long.
        var references = timeline.References
            .Where(r => !string.Equals(r.Reference, self, StringComparison.OrdinalIgnoreCase))
            .Select(r => new ReviewCrossReference(
                r.PullRequest ? "pull_request" : "issue", r.Reference, ReviewVerdicts.Cap(r.Title, MaxTitle), r.State, Stamp(r.At), Stamp(r.ClosedAt)))
            .Concat((item?.PullRequests ?? []).Select(p => new ReviewCrossReference(
                "pull_request",
                p.Reference,
                string.Empty,
                p.State switch { BoardPullState.Merged => "merged", BoardPullState.Open => "open", _ => "closed" },
                null,
                p.State == BoardPullState.Open ? null : Stamp(p.At))))
            .Where(r => IsReference(r.Reference))
            .GroupBy(r => r.Reference, StringComparer.OrdinalIgnoreCase)
            // The timeline's, with a title and a time, when both have it; the newest first.
            .Select(g => g.First())
            .OrderByDescending(r => r.ReferencedAt ?? r.ClosedAt, StringComparer.Ordinal)
            .Take(20)
            .ToList();

        var commits = timeline.Commits
            .GroupBy(c => c.Sha, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(c => c.At)
            .Take(10)
            .Select(c => new ReviewCommit(c.Sha.ToLowerInvariant(), c.Repository, null, Stamp(c.At)))
            .ToList();

        var comments = timeline.Comments
            .OrderBy(c => c.CreatedAt)
            .TakeLast(3)
            .Select(c => new ReviewIssueComment(ReviewVerdicts.Cap(c.Author, 100), Stamp(c.CreatedAt)!, ReviewVerdicts.Cap(c.Body, MaxComment)))
            .ToList();

        return new ReviewIssue(
            found.Repository,
            issue.Number,
            ReviewVerdicts.Cap(issue.Url, 512),
            ReviewVerdicts.Cap(string.IsNullOrWhiteSpace(issue.Title) ? "(no title)" : issue.Title, 512),
            issue.Type is { Length: > 0 } type ? ReviewVerdicts.Cap(type, 100) : null,
            ReviewVerdicts.Cap(string.IsNullOrWhiteSpace(issue.Author.Login) ? "ghost" : issue.Author.Login, 100),
            string.IsNullOrWhiteSpace(issue.Body) ? null : ReviewVerdicts.Cap(issue.Body, MaxBody),
            [.. issue.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Take(30).Select(l => ReviewVerdicts.Cap(l, 100))],
            Stamp(found.CreatedAt)!,
            Stamp(found.UpdatedAt)!,
            archived,
            item is null ? null : Board(item, now),
            references,
            commits,
            [.. subIssues
                .Where(s => IsReference(s.Reference))
                .Take(30)
                .Select(s => new ReviewSubIssue(
                    s.Reference,
                    ReviewVerdicts.Cap(s.Title, MaxTitle),
                    string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open",
                    s.StateReason is { Length: > 0 } why ? ReviewVerdicts.Cap(why, 40) : null))],
            comments,
            Math.Max(found.Comments, timeline.Comments.Count));
    }

    internal static ReviewBoard Board(BoardItem item, DateTimeOffset now)
    {
        var days = item.FieldChangedAt("Status") is { } since && now >= since ? (int)(now - since).TotalDays : (int?)null;

        var fields = item.Fields
            .Where(f => !string.Equals(f.Key, "Status", StringComparison.OrdinalIgnoreCase) && !string.Equals(f.Key, "Priority", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ReviewBoardField(ReviewVerdicts.Cap(f.Key, 100), ReviewVerdicts.Cap(f.Value, 200)))
            .Concat((item.Dates ?? new Dictionary<string, DateOnly>()).Select(d =>
                new ReviewBoardField(ReviewVerdicts.Cap(d.Key, 100), d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
            .Where(f => f.Name.Length > 0)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new ReviewBoard(
            item.Field("Status") is { } status ? ReviewVerdicts.Cap(status, 100) : null,
            item.Field("Priority") is { } priority ? ReviewVerdicts.Cap(priority, 100) : null,
            days,
            fields);
    }

    /// <summary>ISO 8601 in UTC with a Z, whole seconds - GitHub's own form.</summary>
    public static string? Stamp(DateTimeOffset? at) =>
        at is { } t && t > DateTimeOffset.MinValue ? t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;

    private static bool IsReference(string reference) =>
        System.Text.RegularExpressions.Regex.IsMatch(reference, @"^[A-Za-z0-9][A-Za-z0-9-]*/(?!\.\.?#)[A-Za-z0-9._-]+#[1-9][0-9]*$");

    private async Task<GitHubResult<T>> AskAsync<T>(Func<Task<GitHubResult<T>>> call, CancellationToken ct)
    {
        var answer = await call().ConfigureAwait(false);

        if (answer.Outcome == GitHubOutcome.RateLimited && answer.RetryAt is { } at && at - clock.UtcNow is var left && left <= MaxWait)
        {
            await wait(left > TimeSpan.Zero ? left + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            answer = await call().ConfigureAwait(false);
        }

        return answer;
    }
}
