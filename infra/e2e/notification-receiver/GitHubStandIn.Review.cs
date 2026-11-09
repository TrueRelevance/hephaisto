using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotificationReceiver;

// What the weekly backlog review reads of GitHub (#312), and the organisation it reads it in.
//
// The review takes EVERY open issue of the planner's organisation - also the ones never put on
// the board, and the ones in an archived repository - and gathers facts about each before a model
// sees it: the pull requests and commits that reference it, its sub-issues, whether its
// repository is archived, how long it has been in its board status. So this answers the REST
// calls that takes, with GitHub's own field names:
//
//   GET /search/issues?q=org:acme is:issue is:open&per_page=100   the organisation's open issues
//   GET /repos/{owner}/{repo}/issues/{number}/timeline            cross-referenced and referenced events
//   GET /repos/{owner}/{repo}/issues/{number}/sub_issues          the sub-issues, open and closed
//   GET /repos/{owner}/{repo}                                     `archived`
//
// What it copies, because a client gets it wrong against the real thing:
//
//   - search wants `is:issue` (or `is:pull-request`) in the query since 2025 and answers 422
//     without it; it pages like every list, with `Link`, and has a rate limit of its own (30 a
//     minute, `x-ratelimit-resource: search`)
//   - a pull request that references an issue is a `cross-referenced` event whose `source.issue`
//     is the pull request AS AN ISSUE: `state` is open or closed, and only
//     `source.issue.pull_request.merged_at` says it was merged. Such an event has no `id`
//   - a commit that references an issue is a `referenced` event with a `commit_id`
//   - a closed pull request that was not merged is closed and NOT merged
//   - a write to an archived repository is a 403, a GraphQL closeIssue there an error
//
// The organisation is SEEDED with the board (GitHubStandIn.Project.cs), at start and on every
// reset of it: the board's issues, and beside them the ones below, which the board does not hold.
// coder/fake-scripts/default.review.json answers a verdict for every open issue of both, and
// scripts/e2e/ui/tests/planner-review.spec.ts reads that file for what to expect.
public static partial class GitHubStandIn
{
    /// <summary>The planner's organisation (values-dev-github.yaml, github.planner.owner).</summary>
    private const string Organisation = "acme";

    /// <summary>A commit that names an issue in its message: a `referenced` event of the issue's timeline.</summary>
    private sealed record LinkedCommit(string Repo, string Sha, DateTimeOffset At, string Message);

    /// <summary>What the stand-in knows of the organisation beyond its issues.</summary>
    private sealed class OrgState
    {
        /// <summary>Repositories that are archived: read-only, and their issues still open.</summary>
        public HashSet<string> Archived { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pull requests that reference an issue, by the issue. A board issue's list is its row's.</summary>
        public Dictionary<(string Repo, int Number), List<BoardPull>> Pulls { get; } = [];

        public Dictionary<(string Repo, int Number), List<LinkedCommit>> Commits { get; } = [];

        /// <summary>The issues the review's seeding added, so that a reset removes them again.</summary>
        public HashSet<(string Repo, int Number)> Seeded { get; } = [];

        /// <summary>Every call that wrote or tried to, oldest first, at most <see cref="Kept"/>.</summary>
        public Queue<JsonObject> Writes { get; } = new();
    }

    /// <summary>
    /// The issues of the organisation that are not on the board, under the caller's lock. Each one
    /// is a verdict of the review that the board alone would not produce.
    /// </summary>
    private static void SeedReview(OrgState org, Dictionary<(string Repo, int Number), Issue> issues, DateTimeOffset now, Account reporter)
    {
        foreach (var key in org.Seeded)
        {
            issues.Remove(key);
        }

        org.Seeded.Clear();
        org.Commits.Clear();
        org.Archived.Clear();

        void Add(string repo, int number, string type, string title, int openedDaysAgo, int updatedDaysAgo, string? body = null, (string Repo, int Number)? parent = null)
        {
            issues[(repo, number)] = new Issue
            {
                Repo = repo,
                Number = number,
                Author = reporter,
                Title = title,
                Body = body,
                Type = type,
                Parent = parent,
                CreatedAt = now.AddDays(-openedDaysAgo),
                UpdatedAt = now.AddDays(-updatedDaysAgo),
            };

            org.Seeded.Add((repo, number));
        }

        // A live bug, opened long before there was a board. The suite comments on it between two
        // runs: it is the one issue the second run looks at again.
        Add("acme/web", 118, "Bug", "Login page throws on an expired session", openedDaysAgo: 200, updatedDaysAgo: 40,
            body: "Open the shop with a session older than a day: the login page shows a 500 instead of the form.");

        // Done, and nobody said so: its pull request was merged and says "Follow-up to", not "Closes".
        Add("acme/web", 160, "Task", "Receipt totals round half-down", openedDaysAgo: 60, updatedDaysAgo: 20);
        org.Pulls[("acme/web", 160)] = [new("acme/web", 161, "MERGED", now.AddDays(-21), "Rounds receipt totals half-up. Follow-up to acme/web#160.", Closing: false)];

        // Done by a commit straight to main, with no pull request at all.
        Add("acme/infra", 44, "Bug", "The public probe hard-codes the old issuer", openedDaysAgo: 30, updatedDaysAgo: 6);
        org.Commits[("acme/infra", 44)] = [new("acme/infra", "a6ac13278e5f0d6c9b1e2f3a4b5c6d7e8f901234", now.AddDays(-6), "Probe both issuers (acme/infra#44)")];

        // In a repository that is archived: it can be agreed with and not closed.
        Add("acme/legacy-ttd", 1, "Task", "Finish the Pubmatic endpoint", openedDaysAgo: 400, updatedDaysAgo: 300);
        org.Archived.Add("acme/legacy-ttd");

        // Still to build, never on the board.
        Add("acme/api", 205, "Feature", "Export invoices as XLSX", openedDaysAgo: 150, updatedDaysAgo: 90,
            body: "Accounting asks for the invoice list as a spreadsheet; CSV loses the number formats.");

        // Nobody can tell from the facts: the scripted answer is a bug with low confidence.
        Add("acme/planning", 30, "Task", "Calculations are slower since the reads moved to the column store", openedDaysAgo: 25, updatedDaysAgo: 12);
    }

    private static void MapReview(RouteGroupBuilder api, object gate, Dictionary<(string Repo, int Number), Issue> issues, OrgState org)
    {
        api.MapGet("/search/issues", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query["q"].ToString();
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            ctx.Response.Headers["x-ratelimit-limit"] = "30";
            ctx.Response.Headers["x-ratelimit-remaining"] = "29";
            ctx.Response.Headers["x-ratelimit-resource"] = "search";

            string? Qualifier(string name) => terms
                .Where(t => t.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t[(name.Length + 1)..])
                .LastOrDefault();

            var kinds = terms.Where(t => t.StartsWith("is:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t[(t.IndexOf(':', StringComparison.Ordinal) + 1)..].ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            if (!kinds.Overlaps(["issue", "pr", "pull-request"]))
            {
                return Results.Json(
                    new
                    {
                        message = "Validation Failed",
                        errors = new[] { new { message = "Query must include 'is:issue' or 'is:pull-request'", resource = "Search", field = "q", code = "invalid" } },
                        documentation_url = $"{Docs}/search/search#search-issues-and-pull-requests",
                        status = "422",
                    },
                    statusCode: 422);
            }

            var owner = Qualifier("org") ?? Qualifier("user");
            var repository = Qualifier("repo");
            var state = kinds.Contains("open") ? "open" : kinds.Contains("closed") ? "closed" : Qualifier("state");
            var archived = Qualifier("archived") is { } a ? bool.Parse(a) : (bool?)null;
            var text = terms.Where(t => !t.Contains(':', StringComparison.Ordinal)).ToList();

            lock (gate)
            {
                // This stand-in has no pull requests among its issues: `is:pr` finds nothing.
                var found = kinds.Contains("issue")
                    ? issues.Values
                        .Where(i => owner is null || i.Repo.StartsWith($"{owner}/", StringComparison.OrdinalIgnoreCase))
                        .Where(i => repository is null || string.Equals(i.Repo, repository, StringComparison.OrdinalIgnoreCase))
                        .Where(i => state is null || i.State == state)
                        .Where(i => archived is null || org.Archived.Contains(i.Repo) == archived)
                        .Where(i => text.All(t => i.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || (i.Body ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)))
                        .ToList()
                    : [];

                var desc = !string.Equals(ctx.Request.Query["order"].ToString(), "asc", StringComparison.OrdinalIgnoreCase);
                var ordered = (ctx.Request.Query["sort"].ToString() switch
                {
                    "updated" => desc ? found.OrderByDescending(i => i.UpdatedAt) : found.OrderBy(i => i.UpdatedAt),
                    "comments" => desc ? found.OrderByDescending(i => i.Comments) : found.OrderBy(i => i.Comments),
                    // "Best match" and `created` alike: the newest first, as both mostly are.
                    _ => desc ? found.OrderByDescending(i => i.CreatedAt) : found.OrderBy(i => i.CreatedAt),
                }).ThenBy(i => i.Repo, StringComparer.Ordinal).ThenBy(i => i.Number).ToList();

                Linked(ctx, ordered.Count);

                return Json(new JsonObject
                {
                    ["total_count"] = ordered.Count,
                    ["incomplete_results"] = false,
                    ["items"] = new JsonArray([.. Page(ctx, ordered).Select(i =>
                    {
                        var json = IssueJson(i, issues.Values);
                        json["score"] = 1.0;
                        return json;
                    })]),
                });
            }
        });

        api.MapGet("/repos/{owner}/{repo}/issues/{number:int}/sub_issues", (string owner, string repo, int number, HttpContext ctx) =>
        {
            lock (gate)
            {
                if (!issues.ContainsKey(($"{owner}/{repo}", number)))
                {
                    return Problem(404, "Not Found");
                }

                var children = issues.Values
                    .Where(i => i.Parent == ($"{owner}/{repo}", number))
                    .OrderBy(i => i.CreatedAt)
                    .ThenBy(i => i.Number)
                    .ToList();

                Linked(ctx, children.Count);

                return Etagged(ctx, new JsonArray([.. Page(ctx, children).Select(i => IssueJson(i, issues.Values))]));
            }
        });
    }

    /// <summary>
    /// The timeline events a reference makes: a pull request's `cross-referenced`, a commit's
    /// `referenced`. In GitHub's shape, recorded from github.com 2026-11-09.
    /// </summary>
    private static IEnumerable<(DateTimeOffset At, long Id, JsonObject Json)> ReferenceEvents(OrgState org, (string Repo, int Number) key)
    {
        var developer = new Account("developer", 4004);

        foreach (var pull in org.Pulls.GetValueOrDefault(key) ?? [])
        {
            var merged = pull.State == "MERGED";
            var at = Stamp(pull.At);

            yield return (pull.At, StableId(pull.Repo, pull.Number), new JsonObject
            {
                ["actor"] = AccountJson(developer),
                ["created_at"] = at,
                ["updated_at"] = at,
                ["event"] = "cross-referenced",
                ["source"] = new JsonObject
                {
                    ["type"] = "issue",
                    ["issue"] = new JsonObject
                    {
                        ["number"] = pull.Number,
                        ["node_id"] = NodeId("PR", pull.Repo, pull.Number),
                        ["title"] = pull.Body.Split('.')[0],
                        ["body"] = pull.Body,
                        ["state"] = pull.State == "OPEN" ? "open" : "closed",
                        ["html_url"] = $"https://github.com/{pull.Repo}/pull/{pull.Number}",
                        ["repository_url"] = $"https://api.github.com/repos/{pull.Repo}",
                        ["repository"] = new JsonObject { ["full_name"] = pull.Repo, ["archived"] = org.Archived.Contains(pull.Repo) },
                        ["user"] = AccountJson(developer),
                        ["created_at"] = at,
                        ["updated_at"] = at,
                        ["closed_at"] = pull.State == "OPEN" ? null : at,
                        ["pull_request"] = new JsonObject
                        {
                            ["url"] = $"https://api.github.com/repos/{pull.Repo}/pulls/{pull.Number}",
                            ["html_url"] = $"https://github.com/{pull.Repo}/pull/{pull.Number}",
                            ["merged_at"] = merged ? at : null,
                        },
                    },
                },
            });
        }

        foreach (var commit in org.Commits.GetValueOrDefault(key) ?? [])
        {
            var id = StableId(commit.Repo, commit.Sha.GetHashCode(StringComparison.Ordinal));

            yield return (commit.At, id, new JsonObject
            {
                ["id"] = id,
                ["node_id"] = $"REFE_{id}",
                ["url"] = $"https://api.github.com/repos/{key.Repo}/issues/events/{id}",
                ["actor"] = AccountJson(developer),
                ["event"] = "referenced",
                ["commit_id"] = commit.Sha,
                ["commit_url"] = $"https://api.github.com/repos/{commit.Repo}/commits/{commit.Sha}",
                ["created_at"] = Stamp(commit.At),
                ["performed_via_github_app"] = null,
            });
        }
    }

    /// <summary>A seeded pull request as the pulls endpoint answers it: merged is closed and merged, never a draft.</summary>
    private static Pull? SeededPull(OrgState org, string repo, int number) =>
        org.Pulls.Values.SelectMany(p => p).FirstOrDefault(p => p.Repo == repo && p.Number == number) is { } seeded
            ? new Pull
            {
                Draft = false,
                Merged = seeded.State == "MERGED",
                MergedAt = seeded.State == "MERGED" ? seeded.At : null,
                State = seeded.State == "OPEN" ? "open" : "closed",
                Body = seeded.Body,
            }
            : null;

    private static long StableId(string repo, int number) =>
        1_000_000_000L + (BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{repo}#{number}")), 0) % 1_000_000_000L);

    /// <summary>A REST call that is not a read, or a GraphQL mutation. A refused one counts: it was tried.</summary>
    private static bool IsWrite(JsonObject record)
    {
        var method = record["method"]!.GetValue<string>();

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return false;
        }

        return record["path"]!.GetValue<string>() != "/graphql" || record["operation"]?.GetValue<string>() != "query";
    }

    /// <summary>
    /// Which GraphQL document a call is: "query", or the mutation's first field - and for a
    /// mutation the issue its <c>issue</c> or <c>content</c> variable names, and the variables.
    /// </summary>
    private static void TagGraphQl(JsonObject record, string body)
    {
        JsonNode? parsed;

        try
        {
            parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        var query = Field(parsed, "query") ?? string.Empty;

        if (MutationPattern().Match(query) is not { Success: true } mutation)
        {
            record["operation"] = "query";
            return;
        }

        record["operation"] = mutation.Groups[1].Value;
        record["variables"] = parsed?["variables"]?.DeepClone();

        if ((Field(parsed?["variables"], "issue") ?? Field(parsed?["variables"], "content")) is { } node && IssueOfNode(node) is { } issue)
        {
            record["issue"] = issue;
        }
    }

    /// <summary>The <c>owner/repo#n</c> an issue's node id (<see cref="NodeId"/>, kind I) stands for.</summary>
    private static string? IssueOfNode(string node)
    {
        if (!node.StartsWith("I_", StringComparison.Ordinal))
        {
            return null;
        }

        var encoded = node[2..];

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=')));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Link</c> for a list of <paramref name="count"/>, as GitHub sends it: next and last
    /// while there are more pages, first and prev after the first.
    /// </summary>
    private static void Linked(HttpContext ctx, int count)
    {
        var size = int.TryParse(ctx.Request.Query["per_page"].ToString(), CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 100) : 30;
        var page = int.TryParse(ctx.Request.Query["page"].ToString(), CultureInfo.InvariantCulture, out var p) ? Math.Max(1, p) : 1;
        var last = Math.Max(1, (count + size - 1) / size);

        if (last == 1)
        {
            return;
        }

        var query = string.Join('&', ctx.Request.Query
            .Where(kv => kv.Key is not ("page" or "per_page"))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}"));
        var self = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}?{(query.Length > 0 ? query + "&" : string.Empty)}per_page={size}";
        var links = new List<string>();

        if (page < last)
        {
            links.Add($"<{self}&page={page + 1}>; rel=\"next\"");
            links.Add($"<{self}&page={last}>; rel=\"last\"");
        }

        if (page > 1)
        {
            links.Add($"<{self}&page=1>; rel=\"first\"");
            links.Add($"<{self}&page={page - 1}>; rel=\"prev\"");
        }

        ctx.Response.Headers.Link = string.Join(", ", links);
    }

    [GeneratedRegex(@"^\s*mutation\b[^{]*\{\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex MutationPattern();

    /// <summary>A path under one issue: <c>/repos/{owner}/{repo}/issues/{number}...</c>.</summary>
    [GeneratedRegex(@"^/repos/([^/]+/[^/]+)/issues/([0-9]+)(/|$)")]
    private static partial Regex IssuePath();

    /// <summary>A path under one repository: <c>/repos/{owner}/{repo}...</c>.</summary>
    [GeneratedRegex(@"^/repos/([^/]+/[^/]+)(/|$)")]
    private static partial Regex RepoPath();
}
