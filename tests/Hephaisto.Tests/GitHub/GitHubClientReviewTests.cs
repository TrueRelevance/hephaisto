using System.Net;
using System.Text;
using Hephaisto.Agent.GitHub;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// The weekly review's three reads (#312), against GitHub's own shapes: a search that wants
/// <c>is:issue</c> and has a rate limit of its own, a pull request that comes into a timeline as an
/// issue and is merged only by <c>pull_request.merged_at</c>, a commit that is a <c>referenced</c>
/// event naming its repository by URL.
/// </summary>
public sealed class GitHubClientReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_search_asks_for_the_organisations_open_issues_oldest_first_and_leaves_pull_requests_out()
    {
        var (client, handler, _, _) = Build(_ => Json(HttpStatusCode.OK, """
            {"total_count":3,"incomplete_results":false,"items":[
              {"number":431,"node_id":"I_431","title":"Stale cache","body":null,"state":"open","html_url":"https://github.com/acme/api/issues/431",
               "repository_url":"https://api.github.com/repos/acme/api","user":{"login":"someone","id":7},"labels":[{"name":"bug"}],
               "type":{"name":"Bug"},"comments":4,"created_at":"2026-10-20T08:00:00Z","updated_at":"2026-11-07T10:12:00Z",
               "sub_issues_summary":{"total":2,"completed":1,"percent_completed":50}},
              {"number":12,"node_id":"PR_12","title":"A pull request","state":"open","html_url":"https://github.com/acme/api/pull/12",
               "repository_url":"https://api.github.com/repos/acme/api","user":{"login":"someone","id":7},"pull_request":{"url":"x"},
               "created_at":"2026-10-20T08:00:00Z","updated_at":"2026-10-20T08:00:00Z"},
              {"number":1,"node_id":"I_1","title":"Retry","state":"open","html_url":"https://github.com/acme/legacy-ttd/issues/1",
               "repository_url":"https://api.github.com/repos/acme/legacy-ttd","user":null,"comments":0,
               "created_at":"2025-03-01T08:00:00Z","updated_at":"2025-03-02T08:00:00Z"}]}
            """, link: "<https://github.example/api/v3/search/issues?page=2>; rel=\"next\""));

        var page = await client.SearchOpenIssuesAsync("acme", 1, Ct);

        var sent = handler.Requests.Single().Uri;
        Uri.UnescapeDataString(sent).Should().Contain("search/issues?q=org:acme is:issue is:open", "the search answers 422 without is:issue");
        sent.Should().Contain("sort=created&order=asc&per_page=100&page=1");

        page.Ok.Should().BeTrue();
        page.Value!.HasMore.Should().BeTrue();
        page.Value.Total.Should().Be(3);
        page.Value.Issues.Select(i => i.Reference).Should().Equal("acme/api#431", "acme/legacy-ttd#1");

        var first = page.Value.Issues[0];
        first.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-11-07T10:12:00Z", System.Globalization.CultureInfo.InvariantCulture));
        first.Comments.Should().Be(4);
        first.SubIssues.Should().Be(2);
        first.Issue.Type.Should().Be("Bug");
        page.Value.Issues[1].Issue.Author.Login.Should().Be("ghost");
    }

    [Fact]
    public async Task A_search_rate_limit_holds_the_search_and_not_the_poller()
    {
        var (client, handler, _, limit) = Build(sent => sent.Uri.Contains("search/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""",
                headers: [("x-ratelimit-remaining", "0"), ("x-ratelimit-resource", "search"), ("retry-after", "30")])
            : Json(HttpStatusCode.OK, """{"full_name":"acme/api","default_branch":"main","archived":false}"""));

        var searched = await client.SearchOpenIssuesAsync("acme", 1, Ct);
        searched.Outcome.Should().Be(GitHubOutcome.RateLimited);
        limit.SearchUntil.Should().NotBeNull();
        limit.Until.Should().BeNull("the core limit is a budget of its own: the poller still asks");

        (await client.SearchOpenIssuesAsync("acme", 2, Ct)).Outcome.Should().Be(GitHubOutcome.RateLimited);
        (await client.GetRepositoryAsync("acme/api", Ct)).Ok.Should().BeTrue();

        handler.Requests.Count(r => r.Uri.Contains("search/", StringComparison.Ordinal)).Should().Be(1, "the second search waits for the time GitHub named, without asking");
    }

    [Fact]
    public async Task A_repository_says_whether_it_is_archived()
    {
        var (client, _, _, _) = Build(_ => Json(HttpStatusCode.OK, """{"full_name":"acme/legacy-ttd","default_branch":"master","archived":true}"""));

        (await client.GetRepositoryAsync("acme/legacy-ttd", Ct)).Value.Should().Be(new GitHubRepository("acme/legacy-ttd", "master", Archived: true));
    }

    [Fact]
    public async Task A_timeline_gives_the_references_the_commits_and_the_comments_and_merged_is_merged_at()
    {
        var (client, _, _, _) = Build(_ => Json(HttpStatusCode.OK, """
            [
              {"event":"labeled","created_at":"2026-11-01T08:00:00Z","label":{"name":"bug"}},
              {"event":"cross-referenced","created_at":"2026-11-06T09:00:00Z","source":{"type":"issue","issue":{
                 "number":433,"title":"fix(cache): drop entries on reload","state":"closed","closed_at":"2026-11-07T10:12:00Z",
                 "repository_url":"https://api.github.com/repos/acme/api","repository":{"full_name":"acme/api"},
                 "pull_request":{"url":"x","merged_at":"2026-11-07T10:12:00Z"}}}},
              {"event":"cross-referenced","created_at":"2026-11-06T09:30:00Z","source":{"type":"issue","issue":{
                 "number":440,"title":"An abandoned attempt","state":"closed","closed_at":"2026-11-06T10:00:00Z",
                 "repository_url":"https://api.github.com/repos/acme/api","pull_request":{"url":"x","merged_at":null}}}},
              {"event":"cross-referenced","created_at":"2026-11-06T11:00:00Z","source":{"type":"issue","issue":{
                 "number":7,"title":"A related issue","state":"open","repository_url":"https://api.github.com/repos/acme/web"}}},
              {"event":"referenced","created_at":"2026-11-03T08:00:00Z","commit_id":"a6ac13278e5f0d6c9b1e2f3a4b5c6d7e8f901234",
               "commit_url":"https://api.github.com/repos/acme/infra/commits/a6ac13278e5f0d6c9b1e2f3a4b5c6d7e8f901234"},
              {"event":"commented","created_at":"2026-11-07T10:20:00Z","body":"Deployed, looks good.","user":{"login":"reviewer","id":5}}
            ]
            """));

        var timeline = await client.ReadTimelineAsync("acme/api", 431, Ct);

        timeline.Ok.Should().BeTrue();
        timeline.Value!.References.Select(r => (r.Reference, r.PullRequest, r.State)).Should().Equal(
            ("acme/api#433", true, "merged"),
            ("acme/api#440", true, "closed"),
            ("acme/web#7", false, "open"));
        timeline.Value.Commits.Should().ContainSingle().Which.Should().Be(
            new GitHubCommitReference("a6ac13278e5f0d6c9b1e2f3a4b5c6d7e8f901234", "acme/infra", DateTimeOffset.Parse("2026-11-03T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        timeline.Value.Comments.Should().ContainSingle().Which.Author.Should().Be("reviewer");
        timeline.Value.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_long_timeline_is_read_at_its_first_and_its_last_page()
    {
        var (client, handler, _, _) = Build(sent => sent.Uri.Contains("page=5", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """[{"event":"commented","created_at":"2026-11-07T10:20:00Z","body":"newest","user":{"login":"a","id":1}}]""")
            : Json(HttpStatusCode.OK, """[{"event":"commented","created_at":"2026-01-01T10:20:00Z","body":"oldest","user":{"login":"b","id":2}}]""",
                link: "<https://github.example/api/v3/repos/acme/api/issues/1/timeline?per_page=100&page=2>; rel=\"next\", <https://github.example/api/v3/repos/acme/api/issues/1/timeline?per_page=100&page=5>; rel=\"last\""));

        var timeline = await client.ReadTimelineAsync("acme/api", 1, Ct);

        handler.Requests.Should().HaveCount(2);
        timeline.Value!.Comments.Select(c => c.Body).Should().Equal("oldest", "newest");
        timeline.Value.Complete.Should().BeFalse("pages 2 to 4 were not read");
    }

    [Fact]
    public async Task Sub_issues_are_open_or_closed_with_githubs_reason()
    {
        var (client, handler, _, _) = Build(_ => Json(HttpStatusCode.OK, """
            [{"number":10,"title":"The dashboard","state":"closed","state_reason":"completed","repository_url":"https://api.github.com/repos/acme/planning"}]
            """));

        var subs = await client.ListSubIssuesAsync("acme/planning", 9, Ct);

        handler.Requests.Single().Uri.Should().EndWith("repos/acme/planning/issues/9/sub_issues?per_page=100");
        subs.Value.Should().ContainSingle().Which.Should().Be(new GitHubSubIssue("acme/planning", 10, "The dashboard", "closed", "completed"));
    }

    private static (GitHubClient Client, RecordingHandler Handler, Given.FixedClock Clock, GitHubRateLimit Limit) Build(Func<Sent, HttpResponseMessage> answer)
    {
        var handler = new RecordingHandler(answer);
        var clock = Given.Clock();
        var limit = new GitHubRateLimit();

        var client = new GitHubClient(
            new HttpClient(handler),
            Options.Create(new GitHubOptions { Enabled = true, ApiBaseUrl = "https://github.example/api/v3", Token = "t", Repositories = ["acme/api"] }),
            limit,
            clock,
            NullLogger<GitHubClient>.Instance);

        return (client, handler, clock, limit);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? link = null, (string Name, string Value)[]? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        if (link is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", link);
        }

        foreach (var (name, value) in headers ?? [])
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private sealed record Sent(HttpMethod Method, string Uri);

    private sealed class RecordingHandler(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri);
            Requests.Add(sent);
            return Task.FromResult(answer(sent));
        }
    }
}
