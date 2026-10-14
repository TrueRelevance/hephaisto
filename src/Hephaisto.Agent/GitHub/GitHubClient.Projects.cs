using System.Globalization;
using System.Text.Json;
using Hephaisto.Core.Planning;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// The planner's calls (v0.15.0): one project read, and the writes a person's click makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>A project is only in GraphQL.</b> These are the client's first GraphQL calls, and they go
/// through the same <see cref="SendAsync{T}"/> as everything else: the token, the proxy, the
/// rate-limit hold and "it retries nothing" are not said twice. What differs is where a failure
/// is: GraphQL answers 200 and puts it into <c>errors</c>, so that is read here and turned into
/// the outcomes the REST calls have.
/// </para>
/// <para>
/// <b>A read with errors and a usable answer is an answer.</b> A board spans repositories, and
/// GitHub reports an item the token may not see as an error beside the rest. The item is left
/// out. A write is stricter: any error, and nothing is said to have been written.
/// </para>
/// </remarks>
public sealed partial class GitHubClient
{
    /// <summary>Items per request. A project of a thousand is twenty requests; more is not read.</summary>
    public const int ProjectPageSize = 50;

    public const int MaxProjectPages = 20;

    /// <summary>
    /// How much of an issue's description is kept. A board is read whole, and one issue somebody
    /// pasted a log into must not make every read of it megabytes.
    /// </summary>
    public const int MaxIssueBody = 20_000;

    private const string ProjectQuery = """
        query($owner: String!, $number: Int!, $after: String) {
          organization(login: $owner) {
            issueTypes(first: 25) { nodes { name isEnabled } }
            projectV2(number: $number) {
              id title url
              fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } }
              items(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  fieldValues(first: 20) { nodes { ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { name } } } } }
                  content { __typename ... on Issue {
                    id number title url state createdAt updatedAt closedAt
                    body
                    comments { totalCount }
                    author { login }
                    repository { nameWithOwner }
                    issueType { name }
                    parent { url }
                    subIssuesSummary { total completed }
                    assignees(first: 10) { nodes { login } }
                    labels(first: 30) { nodes { name } }
                    closedByPullRequestsReferences(first: 10, includeClosedPrs: true) {
                      nodes { number url state updatedAt mergedAt repository { nameWithOwner } } }
                    timelineItems(itemTypes: [CROSS_REFERENCED_EVENT, LABELED_EVENT], last: 40) { nodes {
                      __typename
                      ... on LabeledEvent { createdAt label { name } }
                      ... on CrossReferencedEvent { source { __typename ... on PullRequest {
                        number url state updatedAt mergedAt body repository { nameWithOwner } } } }
                    } }
                  } }
                }
              }
            }
          }
        }
        """;

    private const string SetFieldMutation = """
        mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
          updateProjectV2ItemFieldValue(input: {projectId: $project, itemId: $item, fieldId: $field, value: {singleSelectOptionId: $option}}) {
            projectV2Item { id }
          }
        }
        """;

    private const string AddItemMutation = """
        mutation($project: ID!, $content: ID!) {
          addProjectV2ItemById(input: {projectId: $project, contentId: $content}) { item { id } }
        }
        """;

    private const string CloseIssueMutation = """
        mutation($issue: ID!, $reason: IssueClosedStateReason!) {
          closeIssue(input: {issueId: $issue, stateReason: $reason}) { issue { id state } }
        }
        """;

    public async Task<GitHubResult<Board>> GetProjectAsync(string owner, int number, CancellationToken ct)
    {
        Board? board = null;
        var items = new List<BoardItem>();
        string? after = null;

        for (var page = 0; page < MaxProjectPages; page++)
        {
            var answer = await GraphQlAsync(
                ProjectQuery,
                new { owner, number, after },
                ProjectPage,
                strict: false,
                ct).ConfigureAwait(false);

            if (!answer.Ok)
            {
                return new(answer.Outcome, null, answer.Detail, RetryAt: answer.RetryAt);
            }

            var read = answer.Value!;

            board ??= read.Head;
            items.AddRange(read.Items);

            if (read.Next is null)
            {
                return new(GitHubOutcome.Ok, board with { Items = items });
            }

            after = read.Next;
        }

        logger.LogWarning(
            "The project {Owner}/{Number} holds more than {Max} items; the planner shows the first ones only.",
            owner, number, MaxProjectPages * ProjectPageSize);

        return new(GitHubOutcome.Ok, board! with { Items = items });
    }

    public Task<GitHubResult<bool>> SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct) =>
        WriteAsync(
            SetFieldMutation,
            new { project = projectId, item = itemId, field = fieldId, option = optionId },
            "updateProjectV2ItemFieldValue",
            "projectV2Item",
            ct);

    public Task<GitHubResult<bool>> CloseIssueAsync(string issueId, bool notPlanned, CancellationToken ct) =>
        WriteAsync(
            CloseIssueMutation,
            new { issue = issueId, reason = notPlanned ? "NOT_PLANNED" : "COMPLETED" },
            "closeIssue",
            "issue",
            ct);

    public async Task<GitHubResult<string>> AddProjectItemAsync(string projectId, string contentId, CancellationToken ct)
    {
        var added = await GraphQlAsync(
            AddItemMutation,
            new { project = projectId, content = contentId },
            data => WrittenId(data, "addProjectV2ItemById", "item"),
            strict: true,
            ct).ConfigureAwait(false);

        return new(added.Outcome, added.Value, added.Detail, RetryAt: added.RetryAt);
    }

    public Task<GitHubResult<GitHubIssue>> CreateIssueAsync(string repository, string title, string? body, string? type, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/issues",
            type is null ? new { title, body } : (object)new { title, body, type },
            null,
            (json, _) => Issue(json),
            ct);

    public Task<GitHubResult<bool>> AddLabelAsync(string repository, int number, string label, CancellationToken ct) =>
        // GitHub creates a label that the repository does not have yet, so a board over twenty
        // repositories needs nobody to prepare them.
        SendAsync(HttpMethod.Post, $"repos/{repository}/issues/{Number(number)}/labels", new { labels = new[] { label } }, null, (_, _) => true, ct);

    public Task<GitHubResult<bool>> RemoveLabelAsync(string repository, int number, string label, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"repos/{repository}/issues/{Number(number)}/labels/{Uri.EscapeDataString(label)}", null, null, (_, _) => true, ct);

    public Task<GitHubResult<bool>> AddAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/issues/{Number(number)}/assignees",
            new { assignees = new[] { login } },
            null,
            // GitHub answers 201 for an account it will not assign and leaves it out of the issue.
            (json, _) => Issue(json).IsAssignedTo(login),
            ct);

    public Task<GitHubResult<bool>> RemoveAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Delete,
            $"repos/{repository}/issues/{Number(number)}/assignees",
            new { assignees = new[] { login } },
            null,
            (json, _) => !Issue(json).IsAssignedTo(login),
            ct);

    /// <summary>
    /// One GraphQL request. <paramref name="read"/> is given <c>data</c> and answers null when
    /// what it needs is not there; <paramref name="strict"/> makes any entry in <c>errors</c> a
    /// failure, which is what a write wants.
    /// </summary>
    private async Task<GitHubResult<T>> GraphQlAsync<T>(string query, object variables, Func<JsonElement, T?> read, bool strict, CancellationToken ct)
        where T : class
    {
        var now = clock.UtcNow;

        var sent = await SendAsync(
            HttpMethod.Post,
            options.Value.GraphQlUrl(),
            new { query, variables },
            null,
            (json, response) => GraphQlAnswer(json, response, read, strict, now),
            ct).ConfigureAwait(false);

        if (!sent.Ok)
        {
            return new(sent.Outcome, default, sent.Detail, RetryAt: sent.RetryAt);
        }

        var answer = sent.Value;

        if (answer.Outcome == GitHubOutcome.RateLimited && answer.RetryAt is { } retryAt)
        {
            rateLimit.Hold(retryAt);

            logger.LogWarning("GitHub rate limit on a GraphQL request: not asking again before {RetryAt:O}. {Detail}", retryAt, answer.Detail);
        }

        return answer;
    }

    /// <summary>A mutation: written when GitHub names what it wrote and reports no error.</summary>
    private async Task<GitHubResult<bool>> WriteAsync(string mutation, object variables, string name, string member, CancellationToken ct)
    {
        var written = await GraphQlAsync(mutation, variables, data => WrittenId(data, name, member), strict: true, ct).ConfigureAwait(false);

        return new(written.Outcome, written.Ok, written.Detail, RetryAt: written.RetryAt);
    }

    private static GitHubResult<T> GraphQlAnswer<T>(JsonElement json, HttpResponseMessage response, Func<JsonElement, T?> read, bool strict, DateTimeOffset now)
        where T : class
    {
        var errors = json.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];

        if (errors.Count == 0 || !strict)
        {
            var value = json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? read(data) : default;

            if (value is not null)
            {
                return new(GitHubOutcome.Ok, value);
            }

            if (errors.Count == 0)
            {
                return new(GitHubOutcome.NotFound, default, "GitHub answered without what was asked for");
            }
        }

        var first = errors[0];
        var detail = Trim(Text(first, "message") ?? "GitHub gave no reason");

        // "type" on github.com; "extensions.code" is where the specification puts it.
        var type = (Text(first, "type")
            ?? (first.TryGetProperty("extensions", out var extensions) ? Text(extensions, "code") : null)
            ?? string.Empty).ToUpperInvariant();

        return type switch
        {
            "RATE_LIMITED" or "RATE_LIMIT" => new(GitHubOutcome.RateLimited, default, detail, RetryAt: GraphQlRetryAt(response, now)),
            "NOT_FOUND" => new(GitHubOutcome.NotFound, default, detail),
            "FORBIDDEN" or "INSUFFICIENT_SCOPES" or "UNAUTHORIZED" => new(GitHubOutcome.Unauthorized, default, detail),
            _ => new(GitHubOutcome.Rejected, default, detail),
        };
    }

    /// <summary>
    /// GraphQL's limit is its own budget and arrives with a 200, so <see cref="RateLimitedUntil"/>
    /// never sees it. The headers are the same.
    /// </summary>
    private static DateTimeOffset GraphQlRetryAt(HttpResponseMessage response, DateTimeOffset now)
    {
        if (Header(response, "retry-after") is { } after
            && int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return now.AddSeconds(Math.Max(1, seconds));
        }

        return Header(response, "x-ratelimit-reset") is { } reset
            && long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            && unix > now.ToUnixTimeSeconds()
            && unix < now.AddDays(1).ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(unix).AddSeconds(1)
                : now.Add(DefaultRetryAfter);
    }

    private static string? WrittenId(JsonElement data, string mutation, string member) =>
        data.TryGetProperty(mutation, out var result) && result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty(member, out var written)
            ? Text(written, "id")
            : null;

    private sealed record ProjectPageRead(Board Head, List<BoardItem> Items, string? Next);

    /// <summary>The project's head, one page of its issues, and the cursor of the next page if there is one.</summary>
    private static ProjectPageRead? ProjectPage(JsonElement data)
    {
        if (!data.TryGetProperty("organization", out var organization) || organization.ValueKind != JsonValueKind.Object
            || !organization.TryGetProperty("projectV2", out var project) || project.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = Nodes(project, "fields")
            .Where(f => f.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            .Select(f => new BoardField(
                Text(f, "id") ?? string.Empty,
                Text(f, "name") ?? string.Empty,
                [.. f.GetProperty("options").EnumerateArray().Select(o => new BoardOption(Text(o, "id") ?? string.Empty, Text(o, "name") ?? string.Empty))]))
            .ToList();

        var items = Nodes(project, "items").Select(BoardItemOf).Where(i => i is not null).Select(i => i!).ToList();

        var next = project.TryGetProperty("items", out var connection)
            && connection.TryGetProperty("pageInfo", out var pageInfo)
            && pageInfo.TryGetProperty("hasNextPage", out var more) && more.ValueKind == JsonValueKind.True
                ? Text(pageInfo, "endCursor")
                : null;

        // The organisation's issue types, for a new issue. A token that may not read them reads
        // the board all the same, and a new issue then has no type to choose.
        var types = Nodes(organization, "issueTypes")
            .Where(t => !t.TryGetProperty("isEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.False)
            .Select(t => Text(t, "name"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        return new(new Board(Text(project, "id") ?? string.Empty, Text(project, "title") ?? string.Empty, Text(project, "url") ?? string.Empty, fields, [], types), items, next);
    }

    /// <summary>
    /// An issue on the board. A draft, a pull request and an item the token may not see are not
    /// issues and are passed over.
    /// </summary>
    private static BoardItem? BoardItemOf(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var issue) || issue.ValueKind != JsonValueKind.Object || Text(issue, "__typename") != "Issue")
        {
            return null;
        }

        var repository = issue.TryGetProperty("repository", out var repo) ? Text(repo, "nameWithOwner") ?? string.Empty : string.Empty;
        var number = issue.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
        var timeline = Nodes(issue, "timelineItems");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in Nodes(item, "fieldValues"))
        {
            if (Text(value, "name") is { } option && value.TryGetProperty("field", out var field) && Text(field, "name") is { } name)
            {
                fields[name] = option;
            }
        }

        // When a label was put on, from the timeline's newest "labeled" event for it. An issue
        // with more events than one page of them no longer says, and the label counts as old.
        var labelled = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in timeline.Where(e => Text(e, "__typename") == "LabeledEvent"))
        {
            if (e.TryGetProperty("label", out var label) && Text(label, "name") is { } name && Time(e, "createdAt") is { } at)
            {
                labelled[name] = at;
            }
        }

        var pulls = Nodes(issue, "closedByPullRequestsReferences")
            .Concat(timeline
                .Where(e => Text(e, "__typename") == "CrossReferencedEvent" && e.TryGetProperty("source", out var s) && Text(s, "__typename") == "PullRequest")
                .Select(e => e.GetProperty("source"))
                .Where(s => PullRequestMention.Names(Text(s, "body"), repository, number, s.TryGetProperty("repository", out var r) ? Text(r, "nameWithOwner") ?? string.Empty : string.Empty)))
            .Select(BoardPullOf)
            .Where(p => p is not null)
            .Select(p => p!)
            .GroupBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var subIssues = issue.TryGetProperty("subIssuesSummary", out var summary) && summary.ValueKind == JsonValueKind.Object ? summary : default;

        return new BoardItem(
            Text(item, "id") ?? string.Empty,
            Text(issue, "id") ?? string.Empty,
            repository,
            number,
            Text(issue, "title") ?? string.Empty,
            Text(issue, "url") ?? string.Empty,
            issue.TryGetProperty("issueType", out var type) ? Text(type, "name") : null,
            issue.TryGetProperty("author", out var author) ? Text(author, "login") ?? "ghost" : "ghost",
            string.Equals(Text(issue, "state"), "OPEN", StringComparison.OrdinalIgnoreCase),
            Time(issue, "createdAt") ?? DateTimeOffset.MinValue,
            Time(issue, "updatedAt") ?? DateTimeOffset.MinValue,
            Time(issue, "closedAt"),
            issue.TryGetProperty("parent", out var parent) ? Text(parent, "url") : null,
            Count(subIssues, "total"),
            Count(subIssues, "completed"),
            fields,
            [.. Nodes(issue, "labels").Select(l => Text(l, "name")).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => new BoardLabel(l!, labelled.TryGetValue(l!, out var at) ? at : null))],
            [.. Nodes(issue, "assignees").Select(a => Text(a, "login")).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!)],
            pulls,
            Text(issue, "body") is { Length: > 0 } body && !string.IsNullOrWhiteSpace(body)
                ? body.Length <= MaxIssueBody ? body : string.Concat(body.AsSpan(0, MaxIssueBody), "\n…")
                : null,
            issue.TryGetProperty("comments", out var comments) ? Count(comments, "totalCount") : 0);
    }

    private static BoardPullRequest? BoardPullOf(JsonElement pull)
    {
        if (Text(pull, "url") is not { } url || !pull.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var state = Text(pull, "state")?.ToUpperInvariant() switch
        {
            "MERGED" => BoardPullState.Merged,
            "OPEN" => BoardPullState.Open,
            _ => BoardPullState.Closed,
        };

        return new BoardPullRequest(
            pull.TryGetProperty("repository", out var repository) ? Text(repository, "nameWithOwner") ?? string.Empty : string.Empty,
            number.GetInt32(),
            url,
            state,
            Time(pull, "mergedAt") ?? Time(pull, "updatedAt") ?? DateTimeOffset.MinValue);
    }

    /// <summary>The <c>nodes</c> of a connection, without the nulls GitHub puts where it may not answer.</summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement parent, string connection) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(connection, out var c) && c.ValueKind == JsonValueKind.Object
        && c.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object)
            : [];

    private static DateTimeOffset? Time(JsonElement json, string name) =>
        Text(json, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static int Count(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
