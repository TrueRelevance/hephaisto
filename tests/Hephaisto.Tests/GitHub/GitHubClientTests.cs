using System.Net;
using System.Reflection;
using System.Text;
using Hephaisto.Agent.GitHub;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// The eleven calls to GitHub's REST API, against a handler that records what was sent and
/// answers with GitHub's own shapes: its field names, its headers, its error bodies.
/// </summary>
/// <remarks>
/// What is pinned here is what a client gets wrong against the real thing and right against a
/// naive fake: the list is issues AND pull requests, a tag is weak and has to go back verbatim,
/// a rate limit is a 403 that looks like a permission problem, a description nobody wrote is
/// null, and a comment's id does not fit in 32 bits.
/// </remarks>
public sealed class GitHubClientTests
{
    private const string Token = "ghp_notARealTokenNotARealToken1234567890";
    private const string Repo = "octo/shop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- what every request carries --------------------------------------------------------

    [Fact]
    public async Task Every_call_says_who_it_is_and_which_api_it_speaks()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, """{"login":"hephaisto-bot","id":9001}"""));

        var user = await client.GetAuthenticatedUserAsync(Ct);

        user.Ok.Should().BeTrue();
        user.Value.Should().Be(new GitHubAccount("hephaisto-bot", 9001));

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Uri.Should().Be("https://github.example/api/v3/user", "a path in the base URL is kept, as GitHub Enterprise Server needs");
        sent.Headers["Accept"].Should().Be("application/vnd.github+json");
        sent.Headers["X-GitHub-Api-Version"].Should().Be("2022-11-28");
        sent.Headers["User-Agent"].Should().Contain("hephaisto", "GitHub refuses a request without a User-Agent");
        sent.Headers["Authorization"].Should().Be($"Bearer {Token}");
    }

    [Fact]
    public void The_client_deletes_nothing_and_closes_only_for_the_planner()
    {
        // Issues as work writes comments: closing an issue is its pull request's job, on merge,
        // and nothing may remove what a person wrote. The planner (v0.15.0) is a person's click
        // in the console, and it brought exactly three calls that end or take something away.
        typeof(IGitHubClient).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Close", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Remove", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(
                nameof(IGitHubClient.CloseIssueAsync),
                nameof(IGitHubClient.RemoveLabelAsync),
                nameof(IGitHubClient.RemoveAssigneeAsync));
    }

    [Fact]
    public void Nothing_but_the_planner_calls_what_closes_or_takes_away()
    {
        // The poller acts by itself, on every pass. A call that closes an issue or removes a
        // label may only be reached from a person's click, which is the planner's service.
        var src = Path.Combine(RepoRoot(), "src", "Hephaisto.Agent");

        var callers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"\.(CloseIssueAsync|RemoveLabelAsync|RemoveAssigneeAsync|SetProjectFieldAsync|AddLabelAsync|AddAssigneeAsync)\("))
            .Select(f => Path.GetFileName(f))
            .ToList();

        callers.Should().BeEquivalentTo("PlannerService.cs");
    }

    // --- the list ---------------------------------------------------------------------------

    [Fact]
    public async Task The_list_asks_for_open_issues_assigned_to_the_account_and_reads_what_github_sends()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, IssueList, etag: "W/\"abc123\""));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", etag: null, Ct);

        var sent = handler.Requests.Single();
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues?assignee=hephaisto-bot&state=open&per_page=100");
        sent.Headers.Should().NotContainKey("If-None-Match", "there is nothing to compare with the first time");

        list.Ok.Should().BeTrue();
        list.ETag.Should().Be("W/\"abc123\"");
        list.Value!.HasMore.Should().BeFalse();

        var issue = list.Value.Issues.Should().ContainSingle("the second entry carries pull_request: it is a pull request").Subject;
        issue.Number.Should().Be(42);
        issue.NodeId.Should().Be("I_kwDOabc");
        issue.Title.Should().Be("The order total is null for an empty cart");
        issue.Body.Should().BeNull("a description nobody wrote is null, not an empty string");
        issue.IsOpen.Should().BeTrue();
        issue.Url.Should().Be("https://github.com/octo/shop/issues/42");
        issue.Author.Should().Be(new GitHubAccount("reporter", 3003));
        issue.IsAssignedTo("Hephaisto-Bot").Should().BeTrue("a login is compared without case, as GitHub does");
        issue.Labels.Should().Equal("bug", "area:checkout");
        issue.Type.Should().Be("Bug");
    }

    [Fact]
    public async Task A_tag_goes_back_exactly_as_it_came_and_a_304_is_unchanged()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", etag: "W/\"abc123\"", Ct);

        // Weak, with its prefix and its quotes: a parsed-and-rewritten tag never matches.
        handler.Requests.Single().Headers["If-None-Match"].Should().Be("W/\"abc123\"");

        list.Outcome.Should().Be(GitHubOutcome.NotModified);
        list.Ok.Should().BeFalse("there is no list in a 304, and nothing may read one out of it");
        list.ETag.Should().Be("W/\"abc123\"", "the tag the caller holds is still the tag");
    }

    [Fact]
    public async Task More_than_a_page_is_said_and_not_fetched()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.OK, IssueList, link: "<https://github.example/api/v3/repositories/1/issues?page=2>; rel=\"next\""));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        list.Value!.HasMore.Should().BeTrue();
        handler.Requests.Should().ContainSingle("one request per repository per pass, however many issues there are");
    }

    [Fact]
    public async Task A_full_page_is_taken_to_have_more_behind_it()
    {
        var hundred = "[" + string.Join(",", Enumerable.Range(1, 100).Select(n =>
            $$"""{"number":{{n}},"node_id":"I_{{n}}","title":"t","body":null,"state":"open","html_url":"u","user":{"login":"a","id":1},"assignees":[],"labels":[]}""")) + "]";

        var (client, _, _) = Build(_ => Json(HttpStatusCode.OK, hundred));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        list.Value!.Issues.Should().HaveCount(100);
        list.Value.HasMore.Should().BeTrue("a proxy may strip the Link header; a full page says the same thing");
    }

    [Fact]
    public async Task One_issue_is_read_and_a_missing_one_is_not_found()
    {
        var (client, handler, _) = Build(r => r.Uri.EndsWith("/issues/42", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, ClosedIssue)
            : Json(HttpStatusCode.NotFound, """{"message":"Not Found","documentation_url":"https://docs.github.com/rest","status":"404"}"""));

        var found = await client.GetIssueAsync(Repo, 42, Ct);
        var missing = await client.GetIssueAsync(Repo, 43, Ct);

        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42");
        found.Value!.IsOpen.Should().BeFalse();
        found.Value.IsAssignedTo("hephaisto-bot").Should().BeFalse();
        found.Value.Author.Login.Should().Be("ghost", "a deleted account's issue stays, with user: null");

        missing.Outcome.Should().Be(GitHubOutcome.NotFound);
        missing.Detail.Should().Contain("Not Found");
    }

    // --- failures are values ------------------------------------------------------------------

    [Fact]
    public async Task A_refused_token_is_unauthorized_and_the_token_is_in_nothing_that_comes_back()
    {
        var (client, _, _) = Build(_ => Json(
            HttpStatusCode.Unauthorized, """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest","status":"401"}"""));

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        result.Detail.Should().Be("HTTP 401: Bad credentials");
        result.Describe().Should().Contain("token was refused").And.NotContain(Token);
    }

    [Fact]
    public async Task A_403_that_names_no_limit_is_a_permission_and_not_a_rate_limit()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.Forbidden,
            """{"message":"Resource not accessible by personal access token","status":"403"}""",
            headers: [("x-ratelimit-remaining", "4990")]));

        var first = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);
        await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        first.RetryAt.Should().BeNull();
        handler.Requests.Should().HaveCount(2, "nothing was told to wait, so the next call is made");
    }

    [Fact]
    public async Task A_used_up_limit_is_a_403_and_nothing_is_asked_until_it_resets()
    {
        var reset = Given.Now.AddSeconds(20);
        var limited = true;

        var (client, handler, clock) = Build(_ => limited
            ? Json(
                HttpStatusCode.Forbidden,
                """{"message":"API rate limit exceeded for user ID 9001.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api"}""",
                headers: [("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))])
            : Json(HttpStatusCode.OK, "[]"));

        var first = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.RateLimited, "GitHub's primary limit is a 403, not a 429");
        first.RetryAt.Should().Be(reset.AddSeconds(1));
        first.Describe().Should().StartWith("rate limited until 12:00:21");

        // Every call, not only the one that was refused: the limit is the account's.
        clock.UtcNow = Given.Now.AddSeconds(15);
        var held = await client.GetIssueAsync(Repo, 42, Ct);
        var alsoHeld = await client.CreateCommentAsync(Repo, 42, "a plan", Ct);

        held.Outcome.Should().Be(GitHubOutcome.RateLimited);
        held.RetryAt.Should().Be(reset.AddSeconds(1));
        alsoHeld.Outcome.Should().Be(GitHubOutcome.RateLimited);
        handler.Requests.Should().ContainSingle("a client that keeps asking through a limit earns a longer one");

        limited = false;
        clock.UtcNow = reset.AddSeconds(2);
        var after = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        after.Ok.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_secondary_limit_says_how_long_to_wait(HttpStatusCode status)
    {
        var (client, _, _) = Build(_ => Json(
            status,
            """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""",
            headers: [("retry-after", "30")]));

        var result = await client.GetPullRequestAsync(Repo, 7, etag: null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.RateLimited);
        result.RetryAt.Should().Be(Given.Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_limit_that_names_no_time_still_waits()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", headers: [("x-ratelimit-remaining", "0")]));

        var result = await client.GetAuthenticatedUserAsync(Ct);
        await client.GetAuthenticatedUserAsync(Ct);

        result.RetryAt.Should().Be(Given.Now.Add(GitHubClient.DefaultRetryAfter));
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_5xx_is_a_server_error_and_is_asked_once(HttpStatusCode status)
    {
        var (client, handler, _) = Build(_ => Json(status, """{"message":"Server Error"}"""));

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.ServerError);
        result.Detail.Should().Be($"HTTP {(int)status}: Server Error");
        handler.Requests.Should().ContainSingle("the next pass is the retry; this client has none of its own");
    }

    [Fact]
    public async Task No_answer_is_unreachable_and_a_timeout_is_no_answer()
    {
        var (refused, _, _) = Build(_ => throw new HttpRequestException("Connection refused (github.example:443)"));
        var (slow, _, _) = Build(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing."));

        var first = await refused.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);
        var second = await slow.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.Unreachable);
        first.Detail.Should().Contain("Connection refused");
        second.Outcome.Should().Be(GitHubOutcome.Unreachable, "the client's own timeout is GitHub not answering, not the caller stopping");
    }

    [Fact]
    public async Task The_caller_stopping_is_not_swallowed_as_a_failure_of_githubs()
    {
        using var stop = new CancellationTokenSource();
        var (client, _, _) = Build(_ =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        });

        var act = () => client.GetAuthenticatedUserAsync(stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_200_that_is_not_githubs_json_is_refused_and_not_thrown()
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Sign in to the corporate proxy</body></html>", Encoding.UTF8, "text/html"),
        });

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.Rejected);
        result.Detail.Should().StartWith("the answer was not GitHub's JSON");
    }

    // --- comments and pull requests: built now, used from stage 2.3 ---------------------------

    [Fact]
    public async Task Comments_are_asked_for_since_a_whole_second_and_their_ids_are_not_32_bit()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, CommentList));

        var comments = await client.ListCommentsAsync(Repo, 42, new DateTimeOffset(2026, 10, 6, 9, 30, 15, 987, TimeSpan.Zero), etag: null, Ct);

        handler.Requests.Single().Uri.Should().Be(
            "https://github.example/api/v3/repos/octo/shop/issues/42/comments?per_page=100&since=2026-10-06T09%3A30%3A15Z");

        comments.Value.Should().HaveCount(2);
        comments.Value![0].Id.Should().Be(1759743015123L);
        comments.Value[0].Author.Should().Be(new GitHubAccount("maintainer", 1001));
        comments.Value[0].Body.Should().Be("/approve");
        comments.Value[0].CreatedAt.Should().Be(new DateTimeOffset(2026, 10, 6, 9, 30, 15, TimeSpan.Zero));
        comments.Value[1].Author.Login.Should().Be("ghost");
    }

    [Fact]
    public async Task Without_a_time_every_comment_is_asked_for()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, "[]"));

        await client.ListCommentsAsync(Repo, 42, since: null, etag: null, Ct);

        handler.Requests.Single().Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42/comments?per_page=100");
    }

    [Fact]
    public async Task A_comment_is_posted_on_the_issue_and_comes_back_with_its_id()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.Created, Comment));

        const string plan = "The plan:\n\n1. \"quote\" it, and <escape> nothing else";

        var created = await client.CreateCommentAsync(Repo, 42, plan, Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42/comments");

        using var body = System.Text.Json.JsonDocument.Parse(sent.Body);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("body");
        body.RootElement.GetProperty("body").GetString().Should().Be(plan, "what GitHub reads back is the text, whatever the encoder escaped");

        created.Ok.Should().BeTrue();
        created.Value!.Id.Should().Be(1759743015123L);
        created.Value.Url.Should().EndWith("#issuecomment-1759743015123");
    }

    [Fact]
    public async Task A_comment_is_edited_by_its_id_and_not_through_its_issue()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, Comment));

        var edited = await client.UpdateCommentAsync(Repo, 1759743015123L, "status: planning", Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Patch);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/comments/1759743015123");
        sent.Body.Should().Be("""{"body":"status: planning"}""");
        edited.Ok.Should().BeTrue();
    }

    // --- reactions on a comment (#298) -----------------------------------------------------------

    [Fact]
    public async Task The_reactions_of_a_comment_are_asked_for_by_the_comments_id_and_say_which_and_whose()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, ReactionList, etag: "W/\"reactions-1\""));

        var listed = await client.ListCommentReactionsAsync(Repo, 1759743015123L, null, Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Get);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/comments/1759743015123/reactions?per_page=100");
        sent.Headers.Should().NotContainKey("If-None-Match");

        listed.Ok.Should().BeTrue();
        listed.ETag.Should().Be("W/\"reactions-1\"");
        listed.Value.Should().BeEquivalentTo(
        [
            new GitHubReaction(318204771, "rocket", new GitHubAccount("hephaisto-bot", 9001), DateTimeOffset.Parse("2026-10-09T09:30:15Z")),
            new GitHubReaction(318204772, "-1", new GitHubAccount("hephaisto-bot", 9001), DateTimeOffset.Parse("2026-10-09T09:30:15Z")),

            // an id beyond 32 bits, and the account that set it by number as well as by name
            new GitHubReaction(5318204773, "rocket", new GitHubAccount("maintainer", 1001), DateTimeOffset.Parse("2026-10-09T09:41:02Z")),

            // a deleted account's reaction stays, with "user": null
            new GitHubReaction(5318204774, "+1", new GitHubAccount("ghost", 0), DateTimeOffset.Parse("2026-10-09T09:42:00Z")),
        ], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Unchanged_reactions_are_a_304_with_the_tag_that_was_sent()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var listed = await client.ListCommentReactionsAsync(Repo, 1759743015123L, "W/\"reactions-1\"", Ct);

        handler.Requests.Single().Headers["If-None-Match"].Should().Be("W/\"reactions-1\"");
        listed.Outcome.Should().Be(GitHubOutcome.NotModified);
        listed.Value.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]

    // one that is already there: GitHub answers 200 with the one that exists
    [InlineData(HttpStatusCode.OK)]
    public async Task A_reaction_is_set_on_a_comment_by_its_content_and_one_that_is_there_is_not_an_error(HttpStatusCode status)
    {
        var (client, handler, _) = Build(_ => Json(status, Reaction));

        var set = await client.AddCommentReactionAsync(Repo, 1759743015123L, "rocket", Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/comments/1759743015123/reactions");
        sent.Body.Should().Be("""{"content":"rocket"}""");

        set.Ok.Should().BeTrue();
        set.Value.Should().Be(new GitHubReaction(318204771, "rocket", new GitHubAccount("hephaisto-bot", 9001), DateTimeOffset.Parse("2026-10-09T09:30:15Z")));
    }

    [Fact]
    public async Task A_token_that_may_not_react_is_unauthorized_and_not_thrown()
    {
        var (client, _, _) = Build(_ => Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}"""));

        var set = await client.AddCommentReactionAsync(Repo, 1759743015123L, "rocket", Ct);

        set.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        set.Describe().Should().Contain("Resource not accessible by personal access token").And.NotContain(Token);
    }

    [Fact]
    public async Task A_comment_github_will_not_take_is_rejected()
    {
        var (client, _, _) = Build(_ => Json(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}"""));

        var result = await client.CreateCommentAsync(Repo, 42, " ", Ct);

        result.Outcome.Should().Be(GitHubOutcome.Rejected);
        result.Detail.Should().Be("HTTP 422: Validation Failed");
    }

    [Fact]
    public async Task A_pull_request_says_whether_it_was_merged()
    {
        var (client, handler, _) = Build(r => r.Uri.EndsWith("/pulls/7", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"number":7,"state":"closed","draft":false,"merged":true,"merged_at":"2026-10-06T10:00:00Z","html_url":"https://github.com/octo/shop/pull/7","head":{"ref":"hephaisto/issue-42"},"body":"Closes octo/shop#42"}""")
            : Json(HttpStatusCode.OK, """{"number":8,"state":"open","draft":true,"merged":false,"merged_at":null,"html_url":"https://github.com/octo/shop/pull/8","head":{"ref":null},"body":null}"""));

        var merged = await client.GetPullRequestAsync(Repo, 7, etag: null, Ct);
        var draft = await client.GetPullRequestAsync(Repo, 8, etag: null, Ct);

        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/pulls/7");

        merged.Value.Should().Be(new GitHubPullRequest(
            7, "closed", false, true, new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), "https://github.com/octo/shop/pull/7", "hephaisto/issue-42"));

        draft.Value!.Merged.Should().BeFalse();
        draft.Value.Draft.Should().BeTrue();
        draft.Value.MergedAt.Should().BeNull();
        draft.Value.HeadRef.Should().BeNull();
    }

    [Fact]
    public async Task A_pull_request_is_open_only_while_it_is_neither_closed_nor_merged()
    {
        GitHubPullRequest Pull(string state, bool merged) => new(7, state, Draft: true, merged, null, "https://github.com/octo/shop/pull/7", null);

        Pull("open", false).IsOpen.Should().BeTrue();
        Pull("OPEN", false).IsOpen.Should().BeTrue();
        Pull("closed", false).IsOpen.Should().BeFalse("closed without merging");
        Pull("closed", true).IsOpen.Should().BeFalse("merged, which GitHub also calls closed");
        Pull("open", true).IsOpen.Should().BeFalse("merged is merged, whatever the state says");
        Pull(string.Empty, false).IsOpen.Should().BeFalse("a state GitHub did not send is not 'open'");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Comments_and_a_pull_request_are_asked_for_with_the_tag_of_the_last_answer()
    {
        // What makes asking on every pass free: a 304 does not count against the rate limit.
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var comments = await client.ListCommentsAsync(Repo, 42, new DateTimeOffset(2026, 10, 6, 9, 30, 15, TimeSpan.Zero), etag: "W/\"c1\"", Ct);
        var pull = await client.GetPullRequestAsync(Repo, 7, etag: "W/\"p1\"", Ct);

        handler.Requests[0].Headers["If-None-Match"].Should().Be("W/\"c1\"");
        handler.Requests[1].Headers["If-None-Match"].Should().Be("W/\"p1\"");

        comments.Outcome.Should().Be(GitHubOutcome.NotModified);
        comments.Value.Should().BeNull("there is no list in a 304");
        pull.Outcome.Should().Be(GitHubOutcome.NotModified);
        pull.Value.Should().BeNull();
        pull.ETag.Should().Be("W/\"p1\"");
    }

    [Fact]
    public async Task An_answer_carries_its_tag_and_no_tag_is_sent_without_one()
    {
        var (client, handler, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("ETag", "W/\"fresh\"");
            return response;
        });

        var comments = await client.ListCommentsAsync(Repo, 42, since: null, etag: null, Ct);

        handler.Requests.Single().Headers.Should().NotContainKey("If-None-Match");
        comments.ETag.Should().Be("W/\"fresh\"");
    }

    [Fact]
    public async Task A_repository_is_asked_for_its_default_branch()
    {
        var (client, handler, _) = Build(request => request.Uri.EndsWith("/octo/shop", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"id":1,"full_name":"octo/shop","private":true,"default_branch":"develop","archived":false}""")
            : Json(HttpStatusCode.OK, """{"id":2,"full_name":"octo/empty","default_branch":null}"""));

        var shop = await client.GetRepositoryAsync(Repo, Ct);
        var empty = await client.GetRepositoryAsync("octo/empty", Ct);

        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop");

        shop.Value.Should().Be(new GitHubRepository("octo/shop", "develop"));
        empty.Value!.DefaultBranch.Should().BeNull("an empty repository has no branch yet, and the caller decides what that means");
    }

    [Fact]
    public async Task A_repository_the_token_cannot_see_is_not_found_and_says_nothing_about_a_branch()
    {
        var (client, _, _) = Build(_ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        var result = await client.GetRepositoryAsync(Repo, Ct);

        result.Outcome.Should().Be(GitHubOutcome.NotFound);
        result.Value.Should().BeNull();
    }

    // --- the timeline: when the account was assigned ------------------------------------------------

    [Fact]
    public async Task The_timeline_gives_the_assignments_with_their_times_and_passes_over_everything_else()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, Timeline, etag: "W/\"t1\""));

        var assignments = await client.ListAssignmentsAsync(Repo, 42, etag: null, Ct);

        handler.Requests.Should().ContainSingle().Which.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42/timeline?per_page=100");
        assignments.Ok.Should().BeTrue(assignments.Detail);
        assignments.ETag.Should().Be("W/\"t1\"");

        // Two assignments, of two accounts; the unassignment, the comment, the cross-reference
        // without an id, the commit without an event's time and the assignment without an
        // assignee are not assignments.
        assignments.Value.Should().Equal(
            new GitHubAssignment(new GitHubAccount("hephaisto-bot", 9001), "reporter", new DateTimeOffset(2026, 10, 8, 9, 20, 0, TimeSpan.Zero)),
            new GitHubAssignment(new GitHubAccount("somebody-else", 4004), null, new DateTimeOffset(2026, 10, 8, 9, 29, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task An_unchanged_timeline_is_a_304_with_the_tag_that_was_sent()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var unchanged = await client.ListAssignmentsAsync(Repo, 42, "W/\"t1\"", Ct);

        handler.Requests.Single().Headers["If-None-Match"].Should().Be("W/\"t1\"");
        unchanged.Outcome.Should().Be(GitHubOutcome.NotModified);
        unchanged.ETag.Should().Be("W/\"t1\"");
    }

    [Fact]
    public async Task A_timeline_of_more_than_a_page_is_read_at_its_end_too_and_comes_without_a_tag()
    {
        // Oldest first: a new assignment is on the LAST page, and page one - and its tag - does
        // not change when it is added. Asked with page one's tag alone, the answer would be
        // "unchanged" for ever.
        static string Page(int minute) =>
            "[{\"event\":\"assigned\",\"created_at\":\"2026-10-08T09:" + minute.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
            + ":00Z\",\"actor\":{\"login\":\"reporter\",\"id\":3003},\"assignee\":{\"login\":\"hephaisto-bot\",\"id\":9001}}]";

        var (client, handler, _) = Build(r => r.Uri.EndsWith("&page=6", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, Page(6))
            : r.Uri.EndsWith("&page=7", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, Page(7))
            : Json(HttpStatusCode.OK, Page(1), etag: "W/\"p1\"",
                link: "<https://github.example/api/v3/repositories/1/issues/42/timeline?per_page=100&page=2>; rel=\"next\", "
                    + "<https://github.example/api/v3/repositories/1/issues/42/timeline?per_page=100&page=7>; rel=\"last\""));

        var assignments = await client.ListAssignmentsAsync(Repo, 42, etag: null, Ct);

        handler.Requests.Select(r => r.Uri).Should().Equal(
            "https://github.example/api/v3/repos/octo/shop/issues/42/timeline?per_page=100",
            "https://github.example/api/v3/repos/octo/shop/issues/42/timeline?per_page=100&page=6",
            "https://github.example/api/v3/repos/octo/shop/issues/42/timeline?per_page=100&page=7");
        handler.Requests.Skip(1).Should().OnlyContain(r => !r.Headers.ContainsKey("If-None-Match"));
        assignments.Value!.Select(a => a.At.Minute).Should().Equal(1, 6, 7);
        assignments.ETag.Should().BeNull("a tag of page one would answer 'unchanged' for a timeline that grew at its end");
    }

    [Fact]
    public async Task Two_pages_are_two_requests_and_a_page_that_fails_fails_the_question()
    {
        var (client, handler, _) = Build(r => r.Uri.EndsWith("&page=2", StringComparison.Ordinal)
            ? Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>")
            : Json(HttpStatusCode.OK, Timeline, etag: "W/\"p1\"", link: "<https://github.example/x?per_page=100&page=2>; rel=\"next\", <https://github.example/x?per_page=100&page=2>; rel=\"last\""));

        var assignments = await client.ListAssignmentsAsync(Repo, 42, etag: null, Ct);

        handler.Requests.Should().HaveCount(2, "page one is not asked for twice");
        assignments.Outcome.Should().Be(GitHubOutcome.ServerError, "half a timeline is not an answer to 'was it assigned again'");
        assignments.Value.Should().BeNull();
        assignments.ETag.Should().BeNull();
    }

    private const string Timeline = """
        [
          {"id": 1, "event": "assigned", "created_at": "2026-10-08T09:20:00Z",
           "actor": {"login": "reporter", "id": 3003}, "assignee": {"login": "hephaisto-bot", "id": 9001}},
          {"id": 2, "event": "commented", "created_at": "2026-10-08T09:21:00Z", "body": "/replan",
           "actor": {"login": "maintainer", "id": 1001}, "user": {"login": "maintainer", "id": 1001}},
          {"event": "cross-referenced", "created_at": "2026-10-08T09:22:00Z", "actor": {"login": "hephaisto-bot", "id": 9001}, "source": {"type": "issue"}},
          {"sha": "583b1e5b75ad", "event": "committed", "author": {"date": "2026-10-08T09:23:00Z"}, "message": "assigned"},
          {"id": 3, "event": "unassigned", "created_at": "2026-10-08T09:28:53Z",
           "actor": {"login": "reporter", "id": 3003}, "assignee": {"login": "hephaisto-bot", "id": 9001}},
          {"id": 4, "event": "assigned", "created_at": "2026-10-08T09:28:59Z", "actor": {"login": "reporter", "id": 3003}, "assignee": null},
          {"id": 5, "event": "assigned", "created_at": "2026-10-08T09:29:00Z", "actor": null, "assignee": {"login": "somebody-else", "id": 4004}}
        ]
        """;

    // --- GitHub's shapes ------------------------------------------------------------------------

    private const string IssueList = """
        [
          {
            "number": 42,
            "node_id": "I_kwDOabc",
            "title": "The order total is null for an empty cart",
            "body": null,
            "state": "open",
            "html_url": "https://github.com/octo/shop/issues/42",
            "user": {"login": "reporter", "id": 3003},
            "assignees": [{"login": "hephaisto-bot", "id": 9001}],
            "labels": [{"id": 1, "name": "bug", "color": "d73a4a"}, "area:checkout"],
            "type": {"id": 5, "name": "Bug"},
            "comments": 0,
            "created_at": "2026-10-06T09:00:00Z",
            "updated_at": "2026-10-06T09:00:00Z"
          },
          {
            "number": 43,
            "node_id": "PR_kwDOabd",
            "title": "fix: the order total",
            "body": "Closes #42",
            "state": "open",
            "html_url": "https://github.com/octo/shop/pull/43",
            "user": {"login": "hephaisto-bot", "id": 9001},
            "assignees": [{"login": "hephaisto-bot", "id": 9001}],
            "labels": [],
            "pull_request": {"url": "https://api.github.com/repos/octo/shop/pulls/43", "merged_at": null}
          }
        ]
        """;

    private const string ClosedIssue = """
        {
          "number": 42, "node_id": "I_kwDOabc", "title": "t", "body": "b", "state": "closed",
          "html_url": "https://github.com/octo/shop/issues/42",
          "user": null, "assignees": [], "labels": [], "type": null
        }
        """;

    private const string Comment = """
        {
          "id": 1759743015123,
          "body": "/approve",
          "user": {"login": "maintainer", "id": 1001},
          "created_at": "2026-10-06T09:30:15Z",
          "updated_at": "2026-10-06T09:30:15Z",
          "html_url": "https://github.com/octo/shop/issues/42#issuecomment-1759743015123"
        }
        """;

    private const string Reaction = """
        {
          "id": 318204771,
          "node_id": "REA_lALOQz1Cf87Slnbtzg",
          "user": {"login": "hephaisto-bot", "id": 9001, "type": "User"},
          "content": "rocket",
          "created_at": "2026-10-09T09:30:15Z"
        }
        """;

    private const string ReactionList = $$"""
        [
          {{Reaction}},
          {"id": 318204772, "user": {"login": "hephaisto-bot", "id": 9001}, "content": "-1", "created_at": "2026-10-09T09:30:15Z"},
          {"id": 5318204773, "user": {"login": "maintainer", "id": 1001}, "content": "rocket", "created_at": "2026-10-09T09:41:02Z"},
          {"id": 5318204774, "user": null, "content": "+1", "created_at": "2026-10-09T09:42:00Z"}
        ]
        """;

    private const string CommentList = $$"""
        [
          {{Comment}},
          {"id": 1759743015124, "body": "me too", "user": null,
           "created_at": "2026-10-06T09:31:00Z", "updated_at": "2026-10-06T09:31:00Z",
           "html_url": "https://github.com/octo/shop/issues/42#issuecomment-1759743015124"}
        ]
        """;

    // --- harness ------------------------------------------------------------------------------

    internal sealed record Sent(HttpMethod Method, string Uri, string Body, IReadOnlyDictionary<string, string> Headers);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (GitHubClient Client, RecordingHandler Handler, Given.FixedClock Clock) Build(Func<Sent, HttpResponseMessage> answer)
    {
        var handler = new RecordingHandler(answer);
        var clock = Given.Clock();

        var client = new GitHubClient(
            new HttpClient(handler),
            Options.Create(new GitHubOptions
            {
                Enabled = true,
                ApiBaseUrl = "https://github.example/api/v3",
                Token = Token,
                Repositories = [Repo],
            }),
            new GitHubRateLimit(),
            clock,
            NullLogger<GitHubClient>.Instance);

        return (client, handler, clock);
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string body,
        string? etag = null,
        string? link = null,
        (string Name, string Value)[]? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

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

    private sealed class RecordingHandler(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase));

            Requests.Add(sent);

            return answer(sent);
        }
    }
}
