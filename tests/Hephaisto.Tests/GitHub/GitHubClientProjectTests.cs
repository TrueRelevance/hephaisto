using System.Net;
using System.Text;
using System.Text.Json;
using Hephaisto.Agent.GitHub;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// The planner's calls (v0.15.0): the project over GraphQL, and the writes.
/// </summary>
/// <remarks>
/// What is pinned here is what GraphQL does differently from the REST calls beside it: a failure
/// is a 200 with <c>errors</c>, its rate limit is a budget of its own that arrives the same way,
/// a board holds things that are not issues, and a page is followed by a cursor.
/// </remarks>
public sealed class GitHubClientProjectTests
{
    private const string Token = "ghp_notARealTokenNotARealToken1234567890";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- where GraphQL is ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.github.com", "https://api.github.com/graphql")]
    [InlineData("https://api.github.com/", "https://api.github.com/graphql")]
    [InlineData("https://github.example/api/v3", "https://github.example/api/graphql")]
    [InlineData("http://github-stand-in.hephaisto-obs:8080/github/api", "http://github-stand-in.hephaisto-obs:8080/github/api/graphql")]
    public void GraphQL_is_beside_the_rest_root_and_on_enterprise_server_a_sibling_of_it(string api, string expected)
    {
        new GitHubOptions { ApiBaseUrl = api }.GraphQlUrl().Should().Be(expected);
    }

    // --- the read -----------------------------------------------------------------------------

    [Fact]
    public async Task The_project_is_asked_for_by_owner_and_number_with_the_token()
    {
        var (client, handler, _) = Build(_ => Json(Page(OneIssue, next: null)));

        var board = await client.GetProjectAsync("acme", 3, Ct);

        board.Ok.Should().BeTrue(board.Detail);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be("https://api.github.example/graphql");
        sent.Headers["Authorization"].Should().Be($"Bearer {Token}");

        using var body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("query").GetString().Should().Contain("projectV2(number: $number)");
        body.RootElement.GetProperty("variables").GetProperty("owner").GetString().Should().Be("acme");
        body.RootElement.GetProperty("variables").GetProperty("number").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task The_board_is_its_single_select_fields_and_its_issues_and_nothing_else_on_it()
    {
        var (client, _, _) = Build(_ => Json(Page(OneIssue + "," + ADraft + "," + APullRequest + "," + ARedacted, next: null)));

        var board = (await client.GetProjectAsync("acme", 3, Ct)).Value!;

        board.Id.Should().Be("PVT_1");
        board.Title.Should().Be("Platform planning");
        board.Url.Should().Be("https://github.com/orgs/acme/projects/3");

        // The text field and the date field of the answer are not choices and are passed over.
        board.Fields.Select(f => f.Name).Should().Equal("Status", "Priority");
        board.Field("status")!.Options.Should().Equal(new BoardOption("s1", "Inbox"), new BoardOption("s2", "In review"));

        var item = board.Items.Should().ContainSingle("a draft, a pull request and an item the token may not see are not issues").Subject;

        item.ItemId.Should().Be("PVTI_1");
        item.IssueId.Should().Be("I_431");
        item.Reference.Should().Be("acme/api#431");
        item.Title.Should().Be("Stale cache entry survives a price change");
        item.Url.Should().Be("https://github.com/acme/api/issues/431");
        item.Type.Should().Be("Bug");
        item.Author.Should().Be("reporter");
        item.Open.Should().BeTrue();
        item.ClosedAt.Should().BeNull();
        item.UpdatedAt.Should().Be(new DateTimeOffset(2026, 10, 11, 14, 2, 10, TimeSpan.Zero));
        item.ParentUrl.Should().Be("https://github.com/acme/planning/issues/17");
        item.SubIssues.Should().Be(3);
        item.SubIssuesDone.Should().Be(1);
        item.Field("Status").Should().Be("In review");
        item.Field("priority").Should().Be("P0");
        item.Assignees.Should().Equal("flo");
        item.Body.Should().Be("## What happens\n\n1. <b>Reload</b> the cart, cc @somebody", "the description is kept as its author wrote it; showing it as text is the page's job");
        item.Comments.Should().Be(4);
    }

    [Fact]
    public async Task A_description_nobody_wrote_is_none_and_one_somebody_pasted_a_log_into_is_cut()
    {
        // GraphQL sends "" where REST sends null.
        var empty = OneIssue.Replace("\"body\":\"## What happens\\n\\n1. <b>Reload</b> the cart, cc @somebody\"", "\"body\":\"\"", StringComparison.Ordinal);
        var huge = OneIssue.Replace("## What happens", new string('x', GitHubClient.MaxIssueBody + 500), StringComparison.Ordinal);

        empty.Should().NotBe(OneIssue);

        var (none, _, _) = Build(_ => Json(Page(empty, next: null)));
        var (cut, _, _) = Build(_ => Json(Page(huge, next: null)));

        (await none.GetProjectAsync("acme", 3, Ct)).Value!.Items.Single().Body.Should().BeNull();

        var body = (await cut.GetProjectAsync("acme", 3, Ct)).Value!.Items.Single().Body!;
        body.Length.Should().Be(GitHubClient.MaxIssueBody + 2);
        body.Should().EndWith("\n…");
    }

    [Fact]
    public async Task A_label_carries_the_time_of_its_newest_labeled_event_or_none()
    {
        var (client, _, _) = Build(_ => Json(Page(OneIssue, next: null)));

        var item = (await client.GetProjectAsync("acme", 3, Ct)).Value!.Items.Single();

        item.Labels.Should().Equal(
            new BoardLabel("today", new DateTimeOffset(2026, 10, 13, 7, 52, 0, TimeSpan.Zero)),
            new BoardLabel("bug", null));
    }

    [Fact]
    public async Task A_pull_request_belongs_to_the_issue_when_github_says_it_closes_it_or_its_text_says_it_is_part_of_it()
    {
        var (client, _, _) = Build(_ => Json(Page(OneIssue, next: null)));

        var item = (await client.GetProjectAsync("acme", 3, Ct)).Value!.Items.Single();

        // #433 closes it (GitHub's list), acme/web#238 says "Part of", acme/web#240 only mentions
        // it, and the issue that references it is not a pull request at all.
        item.PullRequests.Select(p => (p.Reference, p.State)).Should().BeEquivalentTo(
            [("acme/api#433", BoardPullState.Merged), ("acme/web#238", BoardPullState.Open)]);

        item.PullRequests.Single(p => p.Number == 433).At.Should().Be(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), "a merged one is dated by its merge");
    }

    [Fact]
    public async Task A_board_of_more_than_a_page_is_read_to_its_end_by_the_cursor()
    {
        var (client, handler, _) = Build(sent =>
            sent.Body.Contains("\"after\":\"CURSOR1\"", StringComparison.Ordinal)
                ? Json(Page(OneIssue.Replace("PVTI_1", "PVTI_2", StringComparison.Ordinal), next: null))
                : Json(Page(OneIssue, next: "CURSOR1")));

        var board = await client.GetProjectAsync("acme", 3, Ct);

        handler.Requests.Should().HaveCount(2);
        board.Value!.Items.Select(i => i.ItemId).Should().Equal("PVTI_1", "PVTI_2");
        board.Value.Fields.Should().HaveCount(2, "the head is taken once");
    }

    [Fact]
    public async Task A_page_that_fails_fails_the_read_rather_than_showing_half_a_board()
    {
        var (client, _, _) = Build(sent =>
            sent.Body.Contains("CURSOR1", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream") }
                : Json(Page(OneIssue, next: "CURSOR1")));

        var board = await client.GetProjectAsync("acme", 3, Ct);

        board.Outcome.Should().Be(GitHubOutcome.ServerError);
        board.Value.Should().BeNull();
    }

    // --- failures are a 200 -------------------------------------------------------------------

    [Fact]
    public async Task A_project_that_is_not_there_is_not_found()
    {
        var (client, _, _) = Build(_ => Json(
            """{"data":{"organization":{"projectV2":null}},"errors":[{"type":"NOT_FOUND","path":["organization","projectV2"],"message":"Could not resolve to a ProjectV2 with the number 99."}]}"""));

        var board = await client.GetProjectAsync("acme", 99, Ct);

        board.Outcome.Should().Be(GitHubOutcome.NotFound);
        board.Detail.Should().Contain("Could not resolve");
    }

    [Fact]
    public async Task A_token_without_the_scope_is_refused_in_githubs_words()
    {
        var (client, _, _) = Build(_ => Json(
            """{"errors":[{"type":"INSUFFICIENT_SCOPES","message":"Your token has not been granted the required scopes to execute this query. The 'projectV2' field requires one of the following scopes: ['read:project']."}]}"""));

        var board = await client.GetProjectAsync("acme", 3, Ct);

        board.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        board.Describe().Should().Contain("read:project");
    }

    [Fact]
    public async Task The_graphql_rate_limit_is_a_200_and_is_honoured_until_its_reset()
    {
        // The body is what api.github.com answered on 2026-10-13, with the account's number
        // changed; the status was 200 and the headers named the reset.
        var calls = 0;

        var (client, handler, clock) = Build(_ =>
        {
            calls++;

            return Json(
                """{"errors":[{"type":"RATE_LIMIT","code":"graphql_rate_limit","message":"API rate limit already exceeded for user ID 1234."}]}""",
                ("X-RateLimit-Remaining", "0"),
                ("X-RateLimit-Reset", Given.Now.AddMinutes(13).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });

        var first = await client.GetProjectAsync("acme", 3, Ct);

        first.Outcome.Should().Be(GitHubOutcome.RateLimited);
        first.RetryAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(Given.Now.AddMinutes(13).ToUnixTimeSeconds()).AddSeconds(1));

        // Every call until then is answered without a request - REST ones too: the hold is the account's.
        (await client.GetProjectAsync("acme", 3, Ct)).Outcome.Should().Be(GitHubOutcome.RateLimited);
        (await client.GetAuthenticatedUserAsync(Ct)).Outcome.Should().Be(GitHubOutcome.RateLimited);
        calls.Should().Be(1);

        clock.UtcNow = Given.Now.AddMinutes(14);
        await client.GetProjectAsync("acme", 3, Ct);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_read_with_an_error_beside_a_usable_answer_is_an_answer()
    {
        // GitHub reports an item of a repository the token may not see as an error, and sends
        // the rest of the board.
        var page = Page(OneIssue + ",null", next: null);
        var withError = page[..^1] + ""","errors":[{"type":"FORBIDDEN","path":["organization","projectV2","items","nodes",1],"message":"Resource not accessible by personal access token"}]}""";

        var (client, _, _) = Build(_ => Json(withError));

        var board = await client.GetProjectAsync("acme", 3, Ct);

        board.Ok.Should().BeTrue(board.Detail);
        board.Value!.Items.Should().ContainSingle();
    }

    // --- the writes ---------------------------------------------------------------------------

    [Fact]
    public async Task A_field_is_set_by_four_ids()
    {
        var (client, handler, _) = Build(_ => Json("""{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_1"}}}}"""));

        var set = await client.SetProjectFieldAsync("PVT_1", "PVTI_1", "F_prio", "p1", Ct);

        set.Ok.Should().BeTrue(set.Detail);
        set.Value.Should().BeTrue();

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        var variables = body.RootElement.GetProperty("variables");
        body.RootElement.GetProperty("query").GetString().Should().Contain("updateProjectV2ItemFieldValue");
        variables.GetProperty("project").GetString().Should().Be("PVT_1");
        variables.GetProperty("item").GetString().Should().Be("PVTI_1");
        variables.GetProperty("field").GetString().Should().Be("F_prio");
        variables.GetProperty("option").GetString().Should().Be("p1");
    }

    [Theory]
    [InlineData(false, "COMPLETED")]
    [InlineData(true, "NOT_PLANNED")]
    public async Task An_issue_is_closed_by_its_node_id_with_the_reason(bool notPlanned, string reason)
    {
        var (client, handler, _) = Build(_ => Json("""{"data":{"closeIssue":{"issue":{"id":"I_431","state":"CLOSED"}}}}"""));

        var closed = await client.CloseIssueAsync("I_431", notPlanned, Ct);

        closed.Ok.Should().BeTrue(closed.Detail);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        body.RootElement.GetProperty("variables").GetProperty("issue").GetString().Should().Be("I_431");
        body.RootElement.GetProperty("variables").GetProperty("reason").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task A_write_with_any_error_wrote_nothing_whatever_else_the_answer_holds()
    {
        var (client, _, _) = Build(_ => Json(
            """{"data":{"closeIssue":null},"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by personal access token"}]}"""));

        var closed = await client.CloseIssueAsync("I_431", false, Ct);

        closed.Ok.Should().BeFalse();
        closed.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        closed.Value.Should().BeFalse();
        closed.Detail.Should().Contain("Resource not accessible");
    }

    [Fact]
    public async Task A_write_github_answers_without_naming_what_it_wrote_is_not_a_write()
    {
        var (client, _, _) = Build(_ => Json("""{"data":{"updateProjectV2ItemFieldValue":null}}"""));

        var set = await client.SetProjectFieldAsync("PVT_1", "PVTI_1", "F_prio", "p1", Ct);

        set.Ok.Should().BeFalse();
        set.Value.Should().BeFalse();
    }

    [Fact]
    public async Task A_label_is_put_on_and_taken_off_by_its_name_escaped()
    {
        var (client, handler, _) = Build(_ => Json("""[{"name":"not today"}]"""));

        (await client.AddLabelAsync("acme/api", 431, "not today", Ct)).Ok.Should().BeTrue();
        (await client.RemoveLabelAsync("acme/api", 431, "not today", Ct)).Ok.Should().BeTrue();

        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.Should().Be("https://api.github.example/repos/acme/api/issues/431/labels");
        handler.Requests[0].Body.Should().Be("""{"labels":["not today"]}""");
        handler.Requests[1].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[1].Uri.Should().Be("https://api.github.example/repos/acme/api/issues/431/labels/not%20today");
    }

    [Fact]
    public async Task Taking_off_a_label_that_is_not_there_is_not_found()
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Label does not exist"}""") });

        (await client.RemoveLabelAsync("acme/api", 431, "today", Ct)).Outcome.Should().Be(GitHubOutcome.NotFound);
    }

    [Fact]
    public async Task An_assignment_github_accepted_and_left_out_is_said_to_be_false()
    {
        // 201 for an account that may not be assigned there, and the issue comes back without it.
        var (client, handler, _) = Build(sent => Json(
            sent.Body.Contains("stranger", StringComparison.Ordinal)
                ? """{"number":431,"node_id":"I_431","title":"t","state":"open","html_url":"u","user":{"login":"reporter","id":1},"assignees":[]}"""
                : """{"number":431,"node_id":"I_431","title":"t","state":"open","html_url":"u","user":{"login":"reporter","id":1},"assignees":[{"login":"Flo","id":2}]}"""));

        var assigned = await client.AddAssigneeAsync("acme/api", 431, "flo", Ct);
        var refused = await client.AddAssigneeAsync("acme/api", 431, "stranger", Ct);

        assigned.Value.Should().BeTrue("a login is compared without case");
        refused.Ok.Should().BeTrue();
        refused.Value.Should().BeFalse();

        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.Should().Be("https://api.github.example/repos/acme/api/issues/431/assignees");
        handler.Requests[0].Body.Should().Be("""{"assignees":["flo"]}""");
    }

    [Fact]
    public async Task An_assignee_is_taken_off_with_a_body_on_a_delete()
    {
        var (client, handler, _) = Build(_ => Json(
            """{"number":431,"node_id":"I_431","title":"t","state":"open","html_url":"u","user":{"login":"reporter","id":1},"assignees":[]}"""));

        var removed = await client.RemoveAssigneeAsync("acme/api", 431, "flo", Ct);

        removed.Value.Should().BeTrue();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
        handler.Requests.Single().Body.Should().Be("""{"assignees":["flo"]}""");
    }

    // --- a new issue (v0.15.0) ------------------------------------------------------------------

    [Fact]
    public async Task A_new_issue_is_posted_with_its_type_by_name_and_without_one_when_there_is_none()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                """{"number":500,"node_id":"I_500","title":"Dark mode","body":"why","state":"open","html_url":"https://github.com/acme/web/issues/500","user":{"login":"hephaisto-bot","id":1},"assignees":[],"labels":[],"type":{"name":"Bug"}}""",
                Encoding.UTF8, "application/json"),
        });

        var typed = await client.CreateIssueAsync("acme/web", "Dark mode", "why", "Bug", Ct);
        await client.CreateIssueAsync("acme/web", "Dark mode", null, null, Ct);

        typed.Ok.Should().BeTrue(typed.Detail);
        typed.Value!.Number.Should().Be(500);
        typed.Value.NodeId.Should().Be("I_500");
        typed.Value.Type.Should().Be("Bug");

        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.Should().Be("https://api.github.example/repos/acme/web/issues");
        handler.Requests[0].Body.Should().Be("""{"title":"Dark mode","body":"why","type":"Bug"}""");
        handler.Requests[1].Body.Should().Be("""{"title":"Dark mode","body":null}""", "a type GitHub is not told of is no type");
    }

    [Fact]
    public async Task An_issue_is_put_on_a_project_by_two_node_ids_and_the_new_item_is_answered()
    {
        var (client, handler, _) = Build(_ => Json("""{"data":{"addProjectV2ItemById":{"item":{"id":"PVTI_500"}}}}"""));

        var added = await client.AddProjectItemAsync("PVT_1", "I_500", Ct);

        added.Ok.Should().BeTrue();
        added.Value.Should().Be("PVTI_500");

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        body.RootElement.GetProperty("query").GetString().Should().Contain("addProjectV2ItemById");
        body.RootElement.GetProperty("variables").GetProperty("project").GetString().Should().Be("PVT_1");
        body.RootElement.GetProperty("variables").GetProperty("content").GetString().Should().Be("I_500");
    }

    [Fact]
    public async Task Putting_an_issue_on_a_project_the_token_may_not_write_wrote_nothing()
    {
        var (client, _, _) = Build(_ => Json(
            """{"data":{"addProjectV2ItemById":null},"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by personal access token"}]}"""));

        var added = await client.AddProjectItemAsync("PVT_1", "I_500", Ct);

        added.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        added.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_repositorys_labels_are_read_with_their_descriptions_and_an_empty_one_is_none()
    {
        var (client, handler, _) = Build(_ => Json("""[{"name":"bug","description":"Something isn't working"},{"name":"area:api","description":""},{"name":""}]"""));

        var labels = await client.ListLabelsAsync("acme/api", Ct);

        labels.Value.Should().Equal(new GitHubLabel("bug", "Something isn't working"), new GitHubLabel("area:api", null));
        handler.Requests.Single().Uri.Should().Be("https://api.github.example/repos/acme/api/labels?per_page=100");
    }

    [Fact]
    public async Task The_organisations_enabled_issue_types_come_with_the_board_and_none_when_the_token_may_not_read_them()
    {
        var withTypes = Page(OneIssue, next: null).Replace(
            "{\"data\":{\"organization\":{",
            "{\"data\":{\"organization\":{\"issueTypes\":{\"nodes\":[{\"name\":\"Task\",\"isEnabled\":true},{\"name\":\"Legacy\",\"isEnabled\":false},{\"name\":\"Bug\",\"isEnabled\":true}]},",
            StringComparison.Ordinal);

        var (client, _, _) = Build(_ => Json(withTypes));
        (await client.GetProjectAsync("acme", 3, Ct)).Value!.Types.Should().Equal("Task", "Bug");

        var (without, _, _) = Build(_ => Json(Page(OneIssue, next: null)));
        (await without.GetProjectAsync("acme", 3, Ct)).Value!.Types.Should().BeEmpty();
    }

    // --- GitHub's shapes ----------------------------------------------------------------------

    private const string OneIssue = """
        {"id":"PVTI_1",
         "fieldValues":{"nodes":[{},{"name":"In review","field":{"name":"Status"}},{"name":"P0","field":{"name":"Priority"}}]},
         "content":{"__typename":"Issue","id":"I_431","number":431,"title":"Stale cache entry survives a price change",
           "url":"https://github.com/acme/api/issues/431","state":"OPEN",
           "createdAt":"2026-10-04T08:00:00Z","updatedAt":"2026-10-11T14:02:10Z","closedAt":null,
           "body":"## What happens\n\n1. <b>Reload</b> the cart, cc @somebody","comments":{"totalCount":4},
           "author":{"login":"reporter"},"repository":{"nameWithOwner":"acme/api"},"issueType":{"name":"Bug"},
           "parent":{"url":"https://github.com/acme/planning/issues/17"},"subIssuesSummary":{"total":3,"completed":1},
           "assignees":{"nodes":[{"login":"flo"}]},
           "labels":{"nodes":[{"name":"today"},{"name":"bug"}]},
           "closedByPullRequestsReferences":{"nodes":[
             {"number":433,"url":"https://github.com/acme/api/pull/433","state":"MERGED","updatedAt":"2026-10-11T09:05:00Z","mergedAt":"2026-10-11T09:00:00Z","repository":{"nameWithOwner":"acme/api"}}]},
           "timelineItems":{"nodes":[
             {"__typename":"LabeledEvent","createdAt":"2026-10-12T07:40:00Z","label":{"name":"today"}},
             {"__typename":"LabeledEvent","createdAt":"2026-10-13T07:52:00Z","label":{"name":"today"}},
             {"__typename":"CrossReferencedEvent","source":{"__typename":"PullRequest","number":433,"url":"https://github.com/acme/api/pull/433","state":"MERGED","updatedAt":"2026-10-11T09:05:00Z","mergedAt":"2026-10-11T09:00:00Z","body":"Closes #431","repository":{"nameWithOwner":"acme/api"}}},
             {"__typename":"CrossReferencedEvent","source":{"__typename":"PullRequest","number":238,"url":"https://github.com/acme/web/pull/238","state":"OPEN","updatedAt":"2026-10-12T10:00:00Z","mergedAt":null,"body":"Part of acme/api#431","repository":{"nameWithOwner":"acme/web"}}},
             {"__typename":"CrossReferencedEvent","source":{"__typename":"PullRequest","number":240,"url":"https://github.com/acme/web/pull/240","state":"OPEN","updatedAt":"2026-10-12T10:00:00Z","mergedAt":null,"body":"See acme/api#431 for why","repository":{"nameWithOwner":"acme/web"}}},
             {"__typename":"CrossReferencedEvent","source":{"__typename":"Issue"}}]}}}
        """;

    private const string ADraft = """{"id":"PVTI_d","fieldValues":{"nodes":[]},"content":{"__typename":"DraftIssue"}}""";

    private const string APullRequest = """{"id":"PVTI_p","fieldValues":{"nodes":[]},"content":{"__typename":"PullRequest"}}""";

    // What an item of a repository the token cannot read looks like: no content at all.
    private const string ARedacted = """{"id":"PVTI_r","fieldValues":{"nodes":[]},"content":null}""";

    private static string Page(string items, string? next) => PageShape
        .Replace("MORE", next is null ? "false" : "true", StringComparison.Ordinal)
        .Replace("CURSOR", next is null ? "null" : $"\"{next}\"", StringComparison.Ordinal)
        .Replace("ITEMS", items, StringComparison.Ordinal)
        .Trim();

    private const string PageShape = """
        {"data":{"organization":{"projectV2":{
          "id":"PVT_1","title":"Platform planning","url":"https://github.com/orgs/acme/projects/3",
          "fields":{"nodes":[
            {},
            {"id":"F_status","name":"Status","options":[{"id":"s1","name":"Inbox"},{"id":"s2","name":"In review"}]},
            {"id":"F_prio","name":"Priority","options":[{"id":"p0","name":"P0"},{"id":"p1","name":"P1"}]},
            {}]},
          "items":{"pageInfo":{"hasNextPage":MORE,"endCursor":CURSOR},
            "nodes":[ITEMS]}}}}}
        """;

    private static (GitHubClient Client, RecordingHandler Handler, Given.FixedClock Clock) Build(Func<Sent, HttpResponseMessage> answer)
    {
        var handler = new RecordingHandler(answer);
        var clock = Given.Clock();

        var client = new GitHubClient(
            new HttpClient(handler),
            Options.Create(new GitHubOptions { Enabled = true, ApiBaseUrl = "https://api.github.example", Token = Token, Repositories = ["acme/api"] }),
            new GitHubRateLimit(),
            clock,
            NullLogger<GitHubClient>.Instance);

        return (client, handler, clock);
    }

    private static HttpResponseMessage Json(string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private sealed record Sent(HttpMethod Method, string Uri, string Body, IReadOnlyDictionary<string, string> Headers);

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
