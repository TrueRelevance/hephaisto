using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// A stand-in for GitHub, as the agent's issue poller sees it (v0.14.0, #244).
//
// In v0.14.0 an issue assigned to Hephaisto's account is a piece of work: the agent polls
// GitHub's REST API for it, posts its plan as a comment and reads the answer from the comments.
// Nothing stood in for that side - git is a server in the cluster, `gh` is a script, the model is
// a script. This answers the seven calls the agent makes, with GitHub's own field names so that
// the client under test is the client that ships, and keeps every request it was sent so a
// scenario can read back what the agent asked and how often.
//
// It is not a model of GitHub. What it copies are the behaviours a client gets wrong against the
// real thing and right against a naive fake:
//
//   - a list answers `ETag`, and the same list asked again with `If-None-Match` is a 304 with no
//     body. On GitHub a 304 is free against the rate limit, which is what makes polling cheap
//   - `per_page` is 30 unless asked, never more than 100
//   - an issue nobody wrote a description for has `"body": null`, not an empty string
//   - `since` on comments is inclusive and times are whole seconds, so a client that asks from
//     the last time it saw is handed that comment again and has to know it by id
//   - a comment's id does not fit in 32 bits
//   - a rate limit is a 403 with `x-ratelimit-remaining: 0`, not a 429
//   - an account is a login AND a number, and only the number is for ever: a control can comment
//     as somebody whose login is an approver's and whose id is not
//
//   - an issue has a timeline: every assignment and unassignment is an event with a time of its
//     own, in whole seconds, and comments stand between them. Off and on again within a second
//     leaves the issue assigned in every list and two events in the timeline - which is the only
//     place a client can learn that the assignment is a new one
//   - a timeline of more than a page names its last page in `Link`, and its first page - and that
//     page's ETag - does not change when an event is added to the end
//
// What it does NOT copy, and a client still has to survive on the real one: GitHub's issue list
// also returns pull requests (each with a `pull_request` key); this one lists issues only. And
// every repository exists here - a list of one nobody created an issue in is empty, not a 404.
//
// Issue numbers and comment ids start at the clock and never start again, also not after
// DELETE /github/control. The agent keeps what it has worked on in Postgres by repository and
// number, and a stand-in that began at 1 after every restart would hand it an issue it has
// already finished.
//
// The API, as the agent sees it. `Authorization: Bearer <GITHUB_STANDIN_TOKEN>` on every call:
//
//   GET   /github/api/user
//   GET   /github/api/repos/{owner}/{repo}/issues?assignee=&state=&per_page=
//   GET   /github/api/repos/{owner}/{repo}/issues/{number}
//   GET   /github/api/repos/{owner}/{repo}/issues/{number}/comments?since=&per_page=
//   POST  /github/api/repos/{owner}/{repo}/issues/{number}/comments       {body}
//   PATCH /github/api/repos/{owner}/{repo}/issues/comments/{id}           {body}
//   GET   /github/api/repos/{owner}/{repo}/issues/comments/{id}/reactions?per_page=
//   POST  /github/api/repos/{owner}/{repo}/issues/comments/{id}/reactions {content}   201, or 200 when it was there
//   GET   /github/api/repos/{owner}/{repo}/pulls/{number}
//   GET   /github/api/repos/{owner}/{repo}/issues/{number}/timeline?per_page=&page=
//   GET   /github/api/repos/{owner}/{repo}                                   full_name, default_branch
//
// The harness's side: what a person at github.com would do, and what the agent was seen doing.
// Not authenticated, like /teams/messages - it is not part of the thing being imitated:
//
//   POST   /github/control/repos/{owner}/{repo}/issues                    {title, body?, login, id?, labels?, type?}
//   PATCH  /github/control/repos/{owner}/{repo}/issues/{number}           {title?, body?, state?}
//   POST   /github/control/repos/{owner}/{repo}/issues/{number}/assign    the bot becomes an assignee
//   POST   /github/control/repos/{owner}/{repo}/issues/{number}/unassign
//   POST   /github/control/repos/{owner}/{repo}/issues/{number}/reassign  off and on again, in one step; {login?, id?} is who
//   POST   /github/control/repos/{owner}/{repo}/issues/{number}/comments  {body, login, id?}
//   POST   /github/control/repos/{owner}/{repo}/issues/comments/{id}/reactions           {content, login, id?}  somebody reacts
//   DELETE /github/control/repos/{owner}/{repo}/issues/comments/{id}/reactions/{reaction}   and takes it off again
//   PUT    /github/control/repos/{owner}/{repo}/pulls/{number}            {merged?, state?, draft?, head?, body?}
//   DELETE /github/control/repos/{owner}/{repo}/pulls/{number}            forget it: an open draft again
//   POST   /github/control/fail/{500|rate-limit|off}?count=N              the next N API calls fail
//   GET    /github/control/requests       every API call: seq, method, path, query, status, when
//   DELETE /github/control/requests
//   GET    /github/control/comments       every comment, with its issue, its author, its edits and its reactions
//   GET    /github/control/state          the bot, the failure mode, every issue and pull request
//   DELETE /github/control                forget everything but the counters
public static partial class GitHubStandIn
{
    private const int Kept = 2000;
    private const string Prefix = "/github/api";
    private const string Docs = "https://docs.github.com/rest";

    private sealed record Account(string Login, long Id);

    private sealed class Issue
    {
        public required string Repo { get; init; }
        public required int Number { get; init; }
        public required Account Author { get; init; }
        public required string Title { get; set; }
        public string? Body { get; set; }
        public string State { get; set; } = "open";
        public List<Account> Assignees { get; } = [];
        public List<string> Labels { get; init; } = [];
        public string? Type { get; init; }
        public int Comments { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class Comment
    {
        public required long Id { get; init; }
        public required string Repo { get; init; }
        public required int Number { get; init; }
        public required Account Author { get; init; }
        public required string Body { get; set; }
        public int Edits { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>A reaction on a comment: one of GitHub's eight, and whose. One per account, comment and content.</summary>
    private sealed record Reaction(long Id, string Repo, long CommentId, string Content, Account Author, DateTimeOffset At);

    /// <summary>GitHub's eight, by the words its API uses for them.</summary>
    private static readonly string[] Reactions = ["+1", "-1", "laugh", "confused", "heart", "hooray", "rocket", "eyes"];

    /// <summary>An <c>assigned</c> or <c>unassigned</c> event of an issue's timeline.</summary>
    private sealed record Event(long Id, string Repo, int Number, string Kind, Account Actor, Account Assignee, DateTimeOffset At);

    private sealed class Pull
    {
        public bool Draft { get; set; } = true;
        public bool Merged { get; set; }
        public string State { get; set; } = "open";
        public DateTimeOffset? MergedAt { get; set; }
        public string? Head { get; set; }
        public string? Body { get; set; }
    }

    public static void Map(WebApplication app, IConfiguration configuration)
    {
        // Not a credential: it opens nothing but this process. Its own variable name, so that no
        // manifest ever has a reason to put a real GITHUB_TOKEN into this pod.
        var token = configuration["GITHUB_STANDIN_TOKEN"] ?? "stand-in-github-token";
        var bot = new Account(
            configuration["GITHUB_STANDIN_BOT_LOGIN"] ?? "hephaisto-bot",
            long.TryParse(configuration["GITHUB_STANDIN_BOT_ID"], CultureInfo.InvariantCulture, out var botId) ? botId : 9001);

        // One lock for all of it. A poll, a comment and a control arrive at the same moment in
        // every scenario, and nothing here is hot enough to deserve anything finer.
        var gate = new object();
        var issues = new Dictionary<(string Repo, int Number), Issue>();
        var comments = new List<Comment>();
        var pulls = new Dictionary<(string Repo, int Number), Pull>();
        var events = new List<Event>();
        var reactions = new List<Reaction>();
        var requests = new Queue<JsonObject>();
        var failMode = "off";
        var failLeft = 0;
        var seq = 0L;

        // Seconds since 2026 for an issue, milliseconds since 1970 for a comment: both only grow
        // across restarts, and the second is the size GitHub's own comment ids have reached.
        var nextNumber = (int)(DateTimeOffset.UtcNow - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
        var nextComment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nextEvent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nextReaction = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Everything under the API prefix, served or not: the record, the failure mode and the
        // token. A middleware rather than a filter on the routes, because a call to a path this
        // stand-in does not serve is exactly the request somebody will need to see.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments(Prefix))
            {
                await next(ctx);
                return;
            }

            var record = new JsonObject
            {
                ["method"] = ctx.Request.Method,
                ["path"] = ctx.Request.Path.Value![Prefix.Length..],
                ["query"] = ctx.Request.QueryString.Value,
                ["conditional"] = ctx.Request.Headers.IfNoneMatch.Count > 0,
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            };

            string failing;
            lock (gate)
            {
                // Numbered, because only the last 2000 are kept: "what was asked since" is a
                // number to compare, never a position in the list or a clock on another machine.
                record["seq"] = ++seq;
                requests.Enqueue(record);

                while (requests.Count > Kept)
                {
                    requests.Dequeue();
                }

                failing = failLeft > 0 ? failMode : "off";

                if (failLeft > 0 && --failLeft == 0)
                {
                    failMode = "off";
                }
            }

            var limited = failing == "rate-limit";
            var reset = DateTimeOffset.UtcNow.AddSeconds(limited ? 20 : 3600).ToUnixTimeSeconds();
            ctx.Response.Headers["x-ratelimit-limit"] = "5000";
            ctx.Response.Headers["x-ratelimit-remaining"] = limited ? "0" : "4999";
            ctx.Response.Headers["x-ratelimit-used"] = limited ? "5000" : "1";
            ctx.Response.Headers["x-ratelimit-reset"] = reset.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["x-ratelimit-resource"] = "core";

            var header = ctx.Request.Headers.Authorization.ToString();

            IResult? refusal = failing switch
            {
                "500" => Problem(500, "Server Error"),
                // The reset is twenty seconds away, not an hour: a client that waits for it, as
                // it should, still recovers inside a scenario.
                "rate-limit" => Problem(403, $"API rate limit exceeded for user ID {bot.Id}.", $"{Docs}/overview/rate-limits-for-the-rest-api"),
                _ when header.Length == 0 => Problem(401, "Requires authentication"),
                // GitHub takes both spellings of the scheme.
                _ when header != $"Bearer {token}" && header != $"token {token}" => Problem(401, "Bad credentials"),
                _ => null,
            };

            if (refusal is null)
            {
                await next(ctx);
            }
            else
            {
                await refusal.ExecuteAsync(ctx);
            }

            lock (gate)
            {
                record["status"] = ctx.Response.StatusCode;
            }

            Console.WriteLine($"GITHUB {ctx.Request.Method} {record["path"]}{record["query"]} -> {ctx.Response.StatusCode}");
        });

        var api = app.MapGroup(Prefix);

        api.MapGet("/user", () => Results.Json(AccountJson(bot)));

        // A repository's own page, asked for its default branch. Any name is a repository here -
        // there is no list of them - and its branch is `main` unless the pod is told another.
        api.MapGet("/repos/{owner}/{repo}", (string owner, string repo) => Results.Json(new JsonObject
        {
            ["full_name"] = $"{owner}/{repo}",
            ["name"] = repo,
            ["private"] = false,
            ["default_branch"] = configuration["GITHUB_STANDIN_DEFAULT_BRANCH"] is { Length: > 0 } branch ? branch : "main",
        }));

        api.MapGet("/repos/{owner}/{repo}/issues", (string owner, string repo, HttpContext ctx) =>
        {
            var assignee = ctx.Request.Query["assignee"].ToString();
            var state = ctx.Request.Query["state"].ToString() is { Length: > 0 } s ? s : "open";

            lock (gate)
            {
                var found = issues.Values
                    .Where(i => i.Repo == $"{owner}/{repo}")
                    .Where(i => state == "all" || i.State == state)
                    .Where(i => assignee.Length == 0 || i.Assignees.Any(a => string.Equals(a.Login, assignee, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(i => i.Number);

                return Etagged(ctx, new JsonArray([.. Page(ctx, found).Select(IssueJson)]));
            }
        });

        api.MapGet("/repos/{owner}/{repo}/issues/{number:int}", (string owner, string repo, int number, HttpContext ctx) =>
        {
            lock (gate)
            {
                return issues.TryGetValue(($"{owner}/{repo}", number), out var issue)
                    ? Etagged(ctx, IssueJson(issue))
                    : Problem(404, "Not Found");
            }
        });

        api.MapGet("/repos/{owner}/{repo}/issues/{number:int}/comments", (string owner, string repo, int number, HttpContext ctx) =>
        {
            var since = DateTimeOffset.TryParse(
                ctx.Request.Query["since"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            lock (gate)
            {
                if (!issues.ContainsKey(($"{owner}/{repo}", number)))
                {
                    return Problem(404, "Not Found");
                }

                var found = comments
                    .Where(c => c.Repo == $"{owner}/{repo}" && c.Number == number && c.UpdatedAt >= since)
                    .OrderBy(c => c.Id);

                return Etagged(ctx, new JsonArray([.. Page(ctx, found).Select(CommentJson)]));
            }
        });

        api.MapPost("/repos/{owner}/{repo}/issues/{number:int}/comments", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var text = Field(await ReadAsync(ctx), "body");

            if (string.IsNullOrWhiteSpace(text))
            {
                return Problem(422, "Validation Failed");
            }

            lock (gate)
            {
                return issues.TryGetValue(($"{owner}/{repo}", number), out var issue)
                    ? Results.Text(CommentJson(Say(issue, bot, text)).ToJsonString(), "application/json", statusCode: 201)
                    : Problem(404, "Not Found");
            }
        });

        api.MapPatch("/repos/{owner}/{repo}/issues/comments/{id:long}", async (string owner, string repo, long id, HttpContext ctx) =>
        {
            var text = Field(await ReadAsync(ctx), "body");

            if (string.IsNullOrWhiteSpace(text))
            {
                return Problem(422, "Validation Failed");
            }

            lock (gate)
            {
                var comment = comments.FirstOrDefault(c => c.Id == id && c.Repo == $"{owner}/{repo}");

                if (comment is null)
                {
                    return Problem(404, "Not Found");
                }

                comment.Body = text;
                comment.Edits++;
                comment.UpdatedAt = Now();
                Touch(comment.Repo, comment.Number);

                return Results.Text(CommentJson(comment).ToJsonString(), "application/json");
            }
        });

        // The reactions on one comment, oldest first. What an approver's click on a plan is read
        // from; the tag makes an unchanged comment a 304.
        api.MapGet("/repos/{owner}/{repo}/issues/comments/{id:long}/reactions", (string owner, string repo, long id, HttpContext ctx) =>
        {
            lock (gate)
            {
                if (!comments.Exists(c => c.Id == id && c.Repo == $"{owner}/{repo}"))
                {
                    return Problem(404, "Not Found");
                }

                var found = reactions.Where(r => r.CommentId == id).OrderBy(r => r.Id);

                return Etagged(ctx, new JsonArray([.. Page(ctx, found).Select(ReactionJson)]));
            }
        });

        // As the token's account. One that is there already is answered with a 200 and itself.
        api.MapPost("/repos/{owner}/{repo}/issues/comments/{id:long}/reactions", async (string owner, string repo, long id, HttpContext ctx) =>
        {
            var content = Field(await ReadAsync(ctx), "content");

            if (content is null || !Reactions.Contains(content, StringComparer.Ordinal))
            {
                return Problem(422, "Validation Failed");
            }

            lock (gate)
            {
                return comments.Exists(c => c.Id == id && c.Repo == $"{owner}/{repo}")
                    ? React($"{owner}/{repo}", id, bot, content)
                    : Problem(404, "Not Found");
            }
        });

        // The issue's timeline: its assignment events and its comments, oldest first, as GitHub
        // orders them. With more than a page it names the last one in `Link`.
        api.MapGet("/repos/{owner}/{repo}/issues/{number:int}/timeline", (string owner, string repo, int number, HttpContext ctx) =>
        {
            lock (gate)
            {
                if (!issues.ContainsKey(($"{owner}/{repo}", number)))
                {
                    return Problem(404, "Not Found");
                }

                var all = events
                    .Where(e => e.Repo == $"{owner}/{repo}" && e.Number == number)
                    .Select(e => (e.At, e.Id, Json: EventJson(e)))
                    .Concat(comments
                        .Where(c => c.Repo == $"{owner}/{repo}" && c.Number == number)
                        .Select(c => (At: c.CreatedAt, c.Id, Json: CommentedJson(c))))
                    .OrderBy(e => e.At)
                    .ThenBy(e => e.Id)
                    .Select(e => e.Json)
                    .ToList();

                var size = int.TryParse(ctx.Request.Query["per_page"].ToString(), CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 100) : 30;
                var page = int.TryParse(ctx.Request.Query["page"].ToString(), CultureInfo.InvariantCulture, out var p) ? Math.Max(1, p) : 1;
                var last = Math.Max(1, (all.Count + size - 1) / size);

                if (last > 1)
                {
                    var self = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}?per_page={size}";
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

                return Etagged(ctx, new JsonArray([.. all.Skip((page - 1) * size).Take(size)]));
            }
        });

        // A pull request nobody registered is answered as an open draft. The `gh` that "opens"
        // one in a coder Job is a script with no network (coder/test/gh-shim), so the stand-in
        // never hears of it; what a scenario can do is say what became of it afterwards.
        api.MapGet("/repos/{owner}/{repo}/pulls/{number:int}", (string owner, string repo, int number, HttpContext ctx) =>
        {
            lock (gate)
            {
                return Etagged(ctx, PullJson($"{owner}/{repo}", number, pulls.GetValueOrDefault(($"{owner}/{repo}", number)) ?? new Pull()));
            }
        });

        // GitHub's 404 rather than an empty one, for a call this stand-in does not serve.
        // The project board of the planner (v0.15.0): GitHubStandIn.Project.cs.
        var seedBoard = MapProject(app, api, gate, issues, bot);

        api.MapFallback(() => Problem(404, "Not Found"));

        var control = app.MapGroup("/github/control");

        control.MapPost("/repos/{owner}/{repo}/issues", async (string owner, string repo, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            if (Field(body, "title") is not { Length: > 0 } title || Who(body) is not { } author)
            {
                return Results.BadRequest(new { error = "an issue needs a title and the login of whoever opens it" });
            }

            lock (gate)
            {
                var issue = new Issue
                {
                    Repo = $"{owner}/{repo}",
                    Number = nextNumber++,
                    Author = author,
                    Title = title,
                    Body = Field(body, "body"),
                    Labels = [.. (body?["labels"] as JsonArray ?? []).Select(l => l?.ToString() ?? string.Empty).Where(l => l.Length > 0)],
                    Type = Field(body, "type"),
                    CreatedAt = Now(),
                    UpdatedAt = Now(),
                };

                issues[(issue.Repo, issue.Number)] = issue;
                Console.WriteLine($"GITHUB {author.Login} opened {issue.Repo}#{issue.Number}");

                return Results.Text(IssueJson(issue).ToJsonString(), "application/json", statusCode: 201);
            }
        });

        control.MapPatch("/repos/{owner}/{repo}/issues/{number:int}", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            if (Field(body, "state") is { } state && state is not ("open" or "closed"))
            {
                return Results.BadRequest(new { error = "state is open or closed" });
            }

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Results.NotFound(new { error = $"no issue {owner}/{repo}#{number}" });
                }

                issue.Title = Field(body, "title") ?? issue.Title;
                issue.State = Field(body, "state") ?? issue.State;

                // Present and null clears it, absent leaves it: an edit to the title is not one
                // to the body.
                if (body is JsonObject o && o.ContainsKey("body"))
                {
                    issue.Body = Field(body, "body");
                }

                issue.UpdatedAt = Now();
                Console.WriteLine($"GITHUB {issue.Repo}#{number} was edited, and is {issue.State}");

                return Results.Text(IssueJson(issue).ToJsonString(), "application/json");
            }
        });

        // `reassign` is off and on again in ONE step, under the lock: what a person does within
        // a few seconds, and what no poll can fall between here. Each step is an event of the
        // issue's timeline, with its own time - the reassignment's `assigned` a second after its
        // `unassigned`, as two clicks are.
        foreach (var verb in new[] { "assign", "unassign", "reassign" })
        {
            control.MapPost($"/repos/{{owner}}/{{repo}}/issues/{{number:int}}/{verb}", async (string owner, string repo, int number, HttpContext ctx) =>
            {
                var actor = Who(await ReadAsync(ctx));

                lock (gate)
                {
                    if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                    {
                        return Results.NotFound(new { error = $"no issue {owner}/{repo}#{number}" });
                    }

                    var by = actor ?? issue.Author;
                    var at = Now();

                    if (verb != "assign" && issue.Assignees.RemoveAll(a => a.Id == bot.Id) > 0)
                    {
                        events.Add(new Event(nextEvent++, issue.Repo, number, "unassigned", by, bot, at));
                    }

                    if (verb != "unassign" && issue.Assignees.All(a => a.Id != bot.Id))
                    {
                        issue.Assignees.Add(bot);
                        events.Add(new Event(nextEvent++, issue.Repo, number, "assigned", by, bot, verb == "reassign" ? at.AddSeconds(1) : at));
                    }

                    issue.UpdatedAt = Now();
                    Console.WriteLine($"GITHUB {bot.Login} {verb}ed: {issue.Repo}#{number}");

                    return Results.Text(IssueJson(issue).ToJsonString(), "application/json");
                }
            });
        }

        control.MapPost("/repos/{owner}/{repo}/issues/{number:int}/comments", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            if (Field(body, "body") is not { Length: > 0 } text || Who(body) is not { } author)
            {
                return Results.BadRequest(new { error = "a comment needs a body and the login of whoever writes it" });
            }

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Results.NotFound(new { error = $"no issue {owner}/{repo}#{number}" });
                }

                Console.WriteLine($"GITHUB {author.Login} ({author.Id}) commented on {issue.Repo}#{number}");

                return Results.Text(CommentJson(Say(issue, author, text)).ToJsonString(), "application/json", statusCode: 201);
            }
        });

        // Somebody clicks a reaction on a comment - an approver's rocket on a plan, a stranger's
        // thumbs-up. It changes nothing about the issue: GitHub does not touch an issue's
        // updated_at for a reaction, so the list of assigned issues stays a 304.
        control.MapPost("/repos/{owner}/{repo}/issues/comments/{id:long}/reactions", async (string owner, string repo, long id, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            if (Field(body, "content") is not { } content || !Reactions.Contains(content, StringComparer.Ordinal) || Who(body) is not { } author)
            {
                return Results.BadRequest(new { error = $"a reaction needs a content ({string.Join(", ", Reactions)}) and the login of whoever sets it" });
            }

            lock (gate)
            {
                if (!comments.Exists(c => c.Id == id && c.Repo == $"{owner}/{repo}"))
                {
                    return Results.NotFound(new { error = $"no comment {id} in {owner}/{repo}" });
                }

                Console.WriteLine($"GITHUB {author.Login} ({author.Id}) set {content} on comment {id} of {owner}/{repo}");

                return React($"{owner}/{repo}", id, author, content);
            }
        });

        control.MapDelete("/repos/{owner}/{repo}/issues/comments/{id:long}/reactions/{reaction:long}", (string owner, string repo, long id, long reaction) =>
        {
            lock (gate)
            {
                var gone = reactions.RemoveAll(r => r.Id == reaction && r.CommentId == id && r.Repo == $"{owner}/{repo}");

                Console.WriteLine($"GITHUB reaction {reaction} on comment {id} of {owner}/{repo} was taken off ({gone})");

                return gone > 0 ? Results.NoContent() : Results.NotFound(new { error = $"no reaction {reaction} on comment {id}" });
            }
        });

        // {merged: true} merges, {state: "closed"} closes without merging; draft, head and body
        // are what the pull request is said to be. Merging ends the draft, as it does on GitHub.
        control.MapPut("/repos/{owner}/{repo}/pulls/{number:int}", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            lock (gate)
            {
                if (!pulls.TryGetValue(($"{owner}/{repo}", number), out var pull))
                {
                    pulls[($"{owner}/{repo}", number)] = pull = new Pull();
                }

                pull.Head = Field(body, "head") ?? pull.Head;
                pull.Body = Field(body, "body") ?? pull.Body;
                pull.Draft = Flag(body, "draft") ?? pull.Draft;
                pull.State = Field(body, "state") is "open" or "closed" ? Field(body, "state")! : pull.State;

                if (Flag(body, "merged") == true && !pull.Merged)
                {
                    pull.Merged = true;
                    pull.MergedAt = Now();
                    pull.State = "closed";
                    pull.Draft = false;
                }

                Console.WriteLine($"GITHUB pull request {owner}/{repo}#{number} is {(pull.Merged ? "merged" : pull.State)}");

                return Results.Text(PullJson($"{owner}/{repo}", number, pull).ToJsonString(), "application/json");
            }
        });

        // Forgets what a pull request was said to be, so that it is answered as an open draft
        // again. The `gh` shim numbers pull requests from 1 in every Job (its state is the pod's),
        // so "pull request 1 of this repository" is a different one in every scenario - and one
        // that a scenario merged would be found merged by the next scenario's pull request.
        control.MapDelete("/repos/{owner}/{repo}/pulls/{number:int}", (string owner, string repo, int number) =>
        {
            lock (gate)
            {
                pulls.Remove(($"{owner}/{repo}", number));
            }

            Console.WriteLine($"GITHUB pull request {owner}/{repo}#{number} is forgotten: an open draft again");
            return Results.NoContent();
        });

        // The next `count` API calls, whoever makes them and whatever they ask. A number rather
        // than a switch, so a scenario that forgets to turn it off has not broken the next one.
        control.MapPost("/fail/{mode}", (string mode, int? count) =>
        {
            if (mode is not ("500" or "rate-limit" or "off"))
            {
                return Results.BadRequest(new { error = "the mode is 500, rate-limit or off" });
            }

            lock (gate)
            {
                failLeft = mode == "off" ? 0 : Math.Max(1, count ?? 1);
                failMode = mode;
                Console.WriteLine($"GITHUB failure mode {mode}{(mode == "off" ? string.Empty : $" for {failLeft} calls")}");

                return Results.Json(new { mode = failMode, remaining = failLeft });
            }
        });

        control.MapGet("/requests", () =>
        {
            lock (gate)
            {
                return Results.Text(new JsonArray([.. requests.Select(r => r.DeepClone())]).ToJsonString(), "application/json");
            }
        });

        control.MapDelete("/requests", () =>
        {
            lock (gate)
            {
                requests.Clear();
            }

            return Results.NoContent();
        });

        control.MapGet("/comments", () =>
        {
            lock (gate)
            {
                return Results.Text(
                    new JsonArray([.. comments.OrderBy(c => c.Id).Select(c =>
                    {
                        var json = CommentJson(c);
                        json["repository"] = c.Repo;
                        json["number"] = c.Number;
                        json["edits"] = c.Edits;
                        json["reactions"] = new JsonArray([.. reactions.Where(r => r.CommentId == c.Id).OrderBy(r => r.Id).Select(ReactionJson)]);
                        return json;
                    })]).ToJsonString(),
                    "application/json");
            }
        });

        control.MapGet("/state", () =>
        {
            lock (gate)
            {
                return Results.Text(
                    new JsonObject
                    {
                        ["bot"] = AccountJson(bot),
                        ["fail"] = new JsonObject { ["mode"] = failLeft > 0 ? failMode : "off", ["remaining"] = failLeft },
                        ["issues"] = new JsonArray([.. issues.Values.OrderBy(i => i.Number).Select(i =>
                        {
                            var json = IssueJson(i);
                            json["repository"] = i.Repo;
                            return json;
                        })]),
                        ["pulls"] = new JsonArray([.. pulls.OrderBy(p => p.Key).Select(p =>
                        {
                            var json = PullJson(p.Key.Repo, p.Key.Number, p.Value);
                            json["repository"] = p.Key.Repo;
                            return json;
                        })]),
                    }.ToJsonString(),
                    "application/json");
            }
        });

        control.MapDelete("", () =>
        {
            lock (gate)
            {
                issues.Clear();
                comments.Clear();
                pulls.Clear();
                events.Clear();
                reactions.Clear();
                requests.Clear();
                failMode = "off";
                failLeft = 0;

                // The board is not a scenario's doing: it is there again after everything went.
                seedBoard();
            }

            Console.WriteLine("GITHUB forgot everything");
            return Results.NoContent();
        });

        // Under the lock. GitHub keeps one reaction per account, comment and content.
        IResult React(string repository, long commentId, Account author, string content)
        {
            if (reactions.Find(r => r.CommentId == commentId && r.Content == content && r.Author.Id == author.Id) is { } there)
            {
                return Results.Text(ReactionJson(there).ToJsonString(), "application/json", statusCode: 200);
            }

            var reaction = new Reaction(nextReaction++, repository, commentId, content, author, Now());
            reactions.Add(reaction);

            return Results.Text(ReactionJson(reaction).ToJsonString(), "application/json", statusCode: 201);
        }

        Comment Say(Issue issue, Account author, string text)
        {
            var comment = new Comment
            {
                Id = nextComment++,
                Repo = issue.Repo,
                Number = issue.Number,
                Author = author,
                Body = text,
                CreatedAt = Now(),
                UpdatedAt = Now(),
            };

            comments.Add(comment);
            issue.Comments++;
            issue.UpdatedAt = comment.CreatedAt;

            return comment;
        }

        // An edited comment moves its issue's updated_at, as on GitHub - which is what changes
        // the list's ETag, and so what a conditional poll notices.
        void Touch(string repo, int number)
        {
            if (issues.TryGetValue((repo, number), out var issue))
            {
                issue.UpdatedAt = Now();
            }
        }
    }

    private static JsonObject AccountJson(Account a) => new() { ["login"] = a.Login, ["id"] = a.Id };

    private static JsonObject IssueJson(Issue i) => new()
    {
        ["number"] = i.Number,
        ["node_id"] = NodeId("I", i.Repo, i.Number),
        ["title"] = i.Title,
        ["body"] = string.IsNullOrEmpty(i.Body) ? null : i.Body,
        ["state"] = i.State,
        ["html_url"] = $"https://github.com/{i.Repo}/issues/{i.Number}",
        ["user"] = AccountJson(i.Author),
        ["assignees"] = new JsonArray([.. i.Assignees.Select(AccountJson)]),
        ["labels"] = new JsonArray([.. i.Labels.Select(l => new JsonObject { ["name"] = l })]),
        ["type"] = i.Type is null ? null : new JsonObject { ["name"] = i.Type },
        // Counted, as GitHub counts them: times are whole seconds, and without the count a
        // comment written in the second its issue last changed would leave the list - and its
        // ETag - exactly as they were.
        ["comments"] = i.Comments,
        ["created_at"] = Stamp(i.CreatedAt),
        ["updated_at"] = Stamp(i.UpdatedAt),
    };

    private static JsonObject CommentJson(Comment c) => new()
    {
        ["id"] = c.Id,
        ["body"] = c.Body,
        ["user"] = AccountJson(c.Author),
        ["created_at"] = Stamp(c.CreatedAt),
        ["updated_at"] = Stamp(c.UpdatedAt),
        ["html_url"] = $"https://github.com/{c.Repo}/issues/{c.Number}#issuecomment-{c.Id}",
    };

    private static JsonObject ReactionJson(Reaction r) => new()
    {
        ["id"] = r.Id,
        ["user"] = AccountJson(r.Author),
        ["content"] = r.Content,
        ["created_at"] = Stamp(r.At),
    };

    /// <summary>An assignment event, with the members GitHub's timeline gives one (recorded from the sandbox, 2026-10-08).</summary>
    private static JsonObject EventJson(Event e) => new()
    {
        ["id"] = e.Id,
        ["node_id"] = NodeId(e.Kind == "assigned" ? "AE" : "UE", e.Repo, e.Number) + e.Id.ToString(CultureInfo.InvariantCulture),
        ["url"] = $"https://api.github.com/repos/{e.Repo}/issues/events/{e.Id}",
        ["actor"] = AccountJson(e.Actor),
        ["event"] = e.Kind,
        ["commit_id"] = null,
        ["commit_url"] = null,
        ["created_at"] = Stamp(e.At),
        ["assignee"] = AccountJson(e.Assignee),
        ["performed_via_github_app"] = null,
    };

    /// <summary>A comment as a timeline holds it: the comment, an `event`, and an `actor` beside its `user`.</summary>
    private static JsonObject CommentedJson(Comment c)
    {
        var json = CommentJson(c);
        json["event"] = "commented";
        json["actor"] = AccountJson(c.Author);
        return json;
    }

    private static JsonObject PullJson(string repo, int number, Pull p) => new()
    {
        ["number"] = number,
        ["state"] = p.State,
        ["draft"] = p.Draft,
        ["merged"] = p.Merged,
        ["merged_at"] = p.MergedAt is { } at ? Stamp(at) : null,
        ["html_url"] = $"https://github.com/{repo}/pull/{number}",
        ["head"] = new JsonObject { ["ref"] = p.Head },
        ["body"] = p.Body,
    };

    /// <summary>
    /// The answer with its <c>ETag</c>, or a 304 when the caller already holds it. Weak, as
    /// GitHub's are for JSON: a client has to send back exactly what it was given.
    /// </summary>
    private static IResult Etagged(HttpContext ctx, JsonNode body)
    {
        var json = body.ToJsonString();
        var etag = $"W/\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32]}\"";

        ctx.Response.Headers.ETag = etag;

        return ctx.Request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries).Contains(etag)
            ? Results.StatusCode(304)
            : Results.Text(json, "application/json");
    }

    /// <summary>
    /// GitHub's page size and nothing more of its pagination: no <c>Link</c> header, because no
    /// scenario has thirty of anything.
    /// </summary>
    private static IEnumerable<T> Page<T>(HttpContext ctx, IEnumerable<T> all)
    {
        var size = int.TryParse(ctx.Request.Query["per_page"].ToString(), CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 100) : 30;
        var page = int.TryParse(ctx.Request.Query["page"].ToString(), CultureInfo.InvariantCulture, out var p) ? Math.Max(1, p) : 1;

        return all.Skip((page - 1) * size).Take(size);
    }

    /// <summary>An error the way GitHub words one: a message and where to read about it.</summary>
    private static IResult Problem(int status, string message, string? docs = null) => Results.Json(
        new { message, documentation_url = docs ?? Docs, status = status.ToString(CultureInfo.InvariantCulture) },
        statusCode: status);

    /// <summary>
    /// Who a control acts as: a login and its number. The number is derived from the login when
    /// nobody gives one, so an account is the same account every time it is named.
    /// </summary>
    private static Account? Who(JsonNode? body)
    {
        if (Field(body, "login") is not { Length: > 0 } login)
        {
            return null;
        }

        return new Account(
            login,
            body?["id"] is JsonValue v && v.TryGetValue<long>(out var id)
                ? id
                : BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(login.ToLowerInvariant())), 0) % 100_000_000);
    }

    private static string? Field(JsonNode? body, string name) =>
        body?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Flag(JsonNode? body, string name) =>
        body?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string NodeId(string kind, string repo, int number) =>
        $"{kind}_{Convert.ToBase64String(Encoding.UTF8.GetBytes($"{repo}#{number}")).TrimEnd('=')}";

    /// <summary>Whole seconds: what GitHub stores, and what its timestamps say.</summary>
    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<JsonNode?> ReadAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();

        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
