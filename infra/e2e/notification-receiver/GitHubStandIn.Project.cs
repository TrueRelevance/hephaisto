using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// The stand-in's project board, for the planner (v0.15.0, #300).
//
// A project is only in GraphQL, so this is the one GraphQL endpoint here: POST /graphql, which
// knows four documents by a word in them - the project read, updateProjectV2ItemFieldValue,
// closeIssue and addProjectV2ItemById - and answers each in GitHub's shape. Beside it the REST
// calls a click makes: a label on and off, an assignee on and off, a new issue, and a
// repository's labels.
//
// What it copies, because a client gets it wrong against the real thing:
//
//   - a failure is a 200 with `errors`, and `data` beside it holds null where the answer was
//   - a board holds things that are not issues: a draft, and an item whose `content` is null
//     because the token may not see its repository
//   - a field that is not a single-select comes back as an empty object among the fields, and a
//     field value that is not one comes back as an empty object among the values
//   - a label that is already on an issue is not put on again: no second `labeled` event, so its
//     date stays the old one
//   - taking off a label that is not there is a 404
//   - an account that may not be assigned is answered 201 and left out of the issue
//   - a new issue with a type the organisation does not have is a 422, and a disabled type is
//     listed among the organisation's types with isEnabled false
//
// The board is SEEDED, at start and whenever the stand-in is told to forget: issues of
// repositories `acme/...`, which no install lists, so the poller never asks about them and the
// planner's suite finds the same board every time. One of each: finished and still open, a
// parent whose sub-issues are all done, a quiet pull request, an empty field, planned on an
// earlier day, put away today, closed today.
public static partial class GitHubStandIn
{
    private const string ProjectId = "PVT_standin";
    private const int ProjectNumber = 3;

    /// <summary>The one login the stand-in answers 201 for and never assigns.</summary>
    private const string Unassignable = "stranger";

    private static readonly (string Id, string Name, (string Id, string Name)[] Options)[] ProjectFields =
    [
        ("F_status", "Status", [("s_inbox", "Inbox"), ("s_backlog", "Backlog"), ("s_ready", "Ready"), ("s_progress", "In progress"), ("s_review", "In review"), ("s_done", "Done")]),
        ("F_priority", "Priority", [("p_0", "P0"), ("p_1", "P1"), ("p_2", "P2")]),
        ("F_area", "Area", [("a_api", "API"), ("a_web", "Web"), ("a_infra", "Infra")]),
    ];

    /// <summary>The organisation's issue types: the last one is disabled, and a new issue may not have it.</summary>
    private static readonly (string Name, bool Enabled)[] IssueTypes = [("Task", true), ("Bug", true), ("Feature", true), ("Epic", true), ("Legacy", false)];

    /// <summary>The labels every repository of the board has. A label put on an issue is listed too.</summary>
    private static readonly (string Name, string? Description)[] RepositoryLabels =
        [("bug", "Something isn't working"), ("enhancement", "New feature or request"), ("documentation", null), ("question", "Further information is requested")];

    /// <summary>The board's date fields: the day an issue is planned for, and a roadmap date beside it.</summary>
    private static readonly (string Id, string Name)[] DateFields = [("F_planned", "Planned for"), ("F_start", "Start date")];

    private sealed record BoardPull(string Repo, int Number, string State, DateTimeOffset At, string Body, bool Closing);

    private sealed class BoardRow
    {
        public required string ItemId { get; init; }
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (string Repo, int Number)? Parent { get; init; }
        public List<BoardPull> Pulls { get; init; } = [];
        public Dictionary<string, DateTimeOffset> LabelledAt { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Dates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? ClosedAt { get; set; }
        public string? ClosedAs { get; set; }
    }

    /// <summary>
    /// Maps the board's routes and seeds it. Returns the seeding, for the control that forgets
    /// everything; it must be called under <paramref name="gate"/>.
    /// </summary>
    private static Action MapProject(WebApplication app, RouteGroupBuilder api, object gate, Dictionary<(string Repo, int Number), Issue> issues, List<Comment> comments, Account bot)
    {
        var board = new Dictionary<(string Repo, int Number), BoardRow>();

        void Seed()
        {
            foreach (var key in board.Keys.ToList())
            {
                issues.Remove(key);
                comments.RemoveAll(c => c.Repo == key.Repo && c.Number == key.Number);
            }

            board.Clear();

            var now = Now();
            var reporter = new Account("reporter", 3003);

            Issue Add(string repo, int number, string type, string title, string? status, string? priority, string? area,
                (string Repo, int Number)? parent = null, BoardPull[]? pulls = null, int updatedDaysAgo = 1, string? body = null, int comments = 0)
            {
                var issue = new Issue
                {
                    Repo = repo,
                    Number = number,
                    Author = reporter,
                    Title = title,
                    Body = body,
                    Comments = comments,
                    Type = type,
                    // Opened one after the other, in the order of their numbers, a few weeks ago:
                    // the newest walks have an order to follow, and every issue is older than
                    // its last change.
                    CreatedAt = now.AddDays(-30).AddHours(number),
                    UpdatedAt = now.AddDays(-updatedDaysAgo),
                };

                var row = new BoardRow { ItemId = $"PVTI_{repo.Replace('/', '_')}_{number}", Parent = parent, Pulls = [.. pulls ?? []] };

                if (status is not null) row.Fields["Status"] = status;
                if (priority is not null) row.Fields["Priority"] = priority;
                if (area is not null) row.Fields["Area"] = area;

                issues[(repo, number)] = issue;
                board[(repo, number)] = row;

                return issue;
            }

            void Label(string repo, int number, string label, DateTimeOffset at)
            {
                issues[(repo, number)].Labels.Add(label);
                board[(repo, number)].LabelledAt[label] = at;
            }

            void Close(string repo, int number, DateTimeOffset at)
            {
                issues[(repo, number)].State = "closed";
                board[(repo, number)].ClosedAt = at;
                board[(repo, number)].ClosedAs = "COMPLETED";
            }

            // An epic whose description names its work in every way GitHub knows one.
            Add("acme/planning", 17, "Epic", "Epic: move order history to the column store", "In progress", "P1", "API",
                body: "Moves order history to the column store.\n\n- [ ] acme/api#431 the stale cache\n- [ ] #21 replay every account\n- [ ] https://github.com/acme/api/issues/440\n- [ ] acme/unknown#5 somebody else's");

            // Finished and still open: its one pull request is merged.
            // Its description is markdown with everything a page must handle: a heading, a list, an
            // element that must stay text, a link, a mention, a reference to another issue, an
            // image that must not be loaded and a link that must not be one.
            Add("acme/api", 431, "Bug", "Stale cache entry survives a price change", "In review", "P0", "API", ("acme/planning", 17),
                [new("acme/api", 433, "MERGED", now.AddDays(-2), "Closes #431", Closing: true)], updatedDaysAgo: 2,
                body: "## What happens\n\nAfter a price change the cart shows the old total until the entry expires.\n\n1. Change a price\n2. <b>Reload</b> the cart\n\nSee [the runbook](https://example.invalid/runbook), cc @somebody, related to #12.\n\n![the dashboard](https://example.invalid/dashboard.png) and [a trap](javascript:alert(1)).",
                comments: 4);
            issues[("acme/api", 431)].Labels.Add("customer");

            Add("acme/planning", 21, "Task", "Fill the store by replaying every active account", "In progress", "P1", "API", ("acme/planning", 17));

            // A pull request that says "Part of", open, and nothing happened for nine days.
            Add("acme/api", 440, "Bug", "Mirror delete fails for large batches", "In review", "P1", "API", ("acme/planning", 21),
                [new("acme/web", 441, "OPEN", now.AddDays(-9), "Part of acme/api#440", Closing: false),
                 new("acme/web", 442, "OPEN", now.AddDays(-1), "See acme/api#440 for the discussion", Closing: false)], updatedDaysAgo: 9);

            // An open pull request that moved today: work in progress, nobody's decision.
            Add("acme/billing", 88, "Bug", "Refund webhook is retried after a 200", "In progress", "P0", "API", null,
                [new("acme/billing", 90, "OPEN", now, "Fixes #88", Closing: true)], updatedDaysAgo: 0,
                body: "The provider retries although we answer 200.", comments: 1);

            // Priority and area are empty.
            Add("acme/web", 240, "Feature", "Dark mode for the invoice view", "Inbox", null, null);

            // Planned two days ago and still open; put away today.
            Add("acme/scraper", 57, "Task", "Robots rules are fetched once per URL", "Ready", "P2", "API");
            Label("acme/scraper", 57, "today", now.AddDays(-2));

            // Planned by the board's day field: for today, and for three days from now.
            Add("acme/billing", 92, "Bug", "Dunning mails go out twice", "Ready", "P1", "API");
            board[("acme/billing", 92)].Dates["Planned for"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Add("acme/web", 251, "Feature", "Export the receipts as CSV", "Backlog", "P2", "Web");
            board[("acme/web", 251)].Dates["Planned for"] = now.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Planned yesterday and three days ago, and not finished: the today view's left-overs.
            Add("acme/web", 250, "Bug", "Invoice PDF misses the VAT id", "Ready", "P1", "Web");
            Label("acme/web", 250, "today", now.AddDays(-1));
            Add("acme/api", 452, "Task", "Rotate the export signing key", "Backlog", "P2", "API");
            Label("acme/api", 452, "today", now.AddDays(-3));

            // Planned for today, for the one issue worked on at a time. The second has a description
            // longer than a screen.
            Add("acme/web", 262, "Bug", "Checkout button hides behind the cookie banner", "Ready", "P1", "Web");
            Label("acme/web", 262, "today", now.AddHours(-2));
            Add("acme/api", 461, "Task", "Retry the invoice webhook with backoff", "Ready", "P1", "API",
                body: "## Retry plan\n\n" + string.Join("\n", Enumerable.Range(1, 80).Select(i => $"{i}. Step {i}: wait {i * 2} s, then post the invoice again.")));
            Label("acme/api", 461, "today", now.AddHours(-2));

            Add("acme/infra", 31, "Bug", "mongo-3 cannot keep up with replication", "Backlog", "P1", "Infra");
            Label("acme/infra", 31, "not today", now);

            // A parent whose only sub-issue is done.
            Add("acme/planning", 9, "Epic", "Epic: one dashboard per pipeline", "In progress", "P2", "Infra");
            Add("acme/planning", 10, "Feature", "Dashboards: the scraping pipeline", "Done", "P2", "Infra", ("acme/planning", 9));
            Close("acme/planning", 10, now.AddDays(-3));

            // Closed today.
            Add("acme/web", 229, "Bug", "Receipt e-mail links to the old shop", "Done", "P1", "Web");
            Close("acme/web", 229, now.AddMinutes(-30));
        }

        lock (gate)
        {
            Seed();
        }

        JsonObject ItemJson((string Repo, int Number) key)
        {
            var issue = issues[key];
            var row = board[key];
            var children = board.Where(b => b.Value.Parent == key).ToList();

            var values = new JsonArray { new JsonObject() };

            foreach (var (name, option) in row.Fields)
            {
                values.Add(new JsonObject { ["name"] = option, ["field"] = new JsonObject { ["name"] = name } });
            }

            foreach (var (name, date) in row.Dates)
            {
                values.Add(new JsonObject { ["date"] = date, ["field"] = new JsonObject { ["name"] = name } });
            }

            var timeline = new JsonArray();

            foreach (var (label, at) in row.LabelledAt.OrderBy(l => l.Value))
            {
                timeline.Add(new JsonObject { ["__typename"] = "LabeledEvent", ["createdAt"] = Stamp(at), ["label"] = new JsonObject { ["name"] = label } });
            }

            foreach (var pull in row.Pulls)
            {
                timeline.Add(new JsonObject { ["__typename"] = "CrossReferencedEvent", ["source"] = PullNode(pull, withBody: true) });
            }

            // An issue that mentions this one: a cross-reference that is not a pull request.
            timeline.Add(new JsonObject { ["__typename"] = "CrossReferencedEvent", ["source"] = new JsonObject { ["__typename"] = "Issue" } });

            return new JsonObject
            {
                ["id"] = row.ItemId,
                ["fieldValues"] = new JsonObject { ["nodes"] = values },
                ["content"] = new JsonObject
                {
                    ["__typename"] = "Issue",
                    ["id"] = NodeId("I", issue.Repo, issue.Number),
                    ["number"] = issue.Number,
                    ["title"] = issue.Title,
                    ["url"] = $"https://github.com/{issue.Repo}/issues/{issue.Number}",
                    ["state"] = issue.State == "open" ? "OPEN" : "CLOSED",
                    ["createdAt"] = Stamp(issue.CreatedAt),
                    ["updatedAt"] = Stamp(issue.UpdatedAt),
                    ["closedAt"] = row.ClosedAt is { } closed ? Stamp(closed) : null,
                    // GitHub's GraphQL sends "" for a description nobody wrote, where REST sends null.
                    ["body"] = issue.Body ?? string.Empty,
                    ["comments"] = new JsonObject { ["totalCount"] = issue.Comments },
                    ["author"] = new JsonObject { ["login"] = issue.Author.Login },
                    ["repository"] = new JsonObject { ["nameWithOwner"] = issue.Repo },
                    ["issueType"] = issue.Type is null ? null : new JsonObject { ["name"] = issue.Type },
                    ["parent"] = row.Parent is { } parent ? new JsonObject { ["url"] = $"https://github.com/{parent.Repo}/issues/{parent.Number}" } : null,
                    ["subIssuesSummary"] = new JsonObject
                    {
                        ["total"] = children.Count,
                        ["completed"] = children.Count(c => issues[c.Key].State != "open"),
                    },
                    ["assignees"] = new JsonObject { ["nodes"] = new JsonArray([.. issue.Assignees.Select(a => new JsonObject { ["login"] = a.Login })]) },
                    ["labels"] = new JsonObject { ["nodes"] = new JsonArray([.. issue.Labels.Select(l => new JsonObject { ["name"] = l })]) },
                    ["closedByPullRequestsReferences"] = new JsonObject
                    {
                        ["nodes"] = new JsonArray([.. row.Pulls.Where(p => p.Closing).Select(p => PullNode(p, withBody: false))]),
                    },
                    ["timelineItems"] = new JsonObject { ["nodes"] = timeline },
                },
            };
        }

        api.MapPost("/graphql", async (HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);
            var query = Field(body, "query") ?? string.Empty;
            var variables = body?["variables"];

            lock (gate)
            {
                if (query.Contains("updateProjectV2ItemFieldValue", StringComparison.Ordinal) && Field(variables, "date") is { } date)
                {
                    var dateField = DateFields.FirstOrDefault(f => f.Id == Field(variables, "field"));
                    var dated = board.Values.FirstOrDefault(r => r.ItemId == Field(variables, "item"));

                    if (Field(variables, "project") != ProjectId || dateField.Id is null || dated is null
                        || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        return GraphQlError("updateProjectV2ItemFieldValue", "NOT_FOUND", "Could not resolve to a node with the global id given.");
                    }

                    dated.Dates[dateField.Name] = date;
                    Console.WriteLine($"GITHUB board: {dated.ItemId} {dateField.Name} = {date}");

                    return Json(new JsonObject { ["data"] = new JsonObject { ["updateProjectV2ItemFieldValue"] = new JsonObject { ["projectV2Item"] = new JsonObject { ["id"] = dated.ItemId } } } });
                }

                if (query.Contains("clearProjectV2ItemFieldValue", StringComparison.Ordinal))
                {
                    var cleared = board.Values.FirstOrDefault(r => r.ItemId == Field(variables, "item"));
                    var dateField = DateFields.FirstOrDefault(f => f.Id == Field(variables, "field"));
                    var selectField = ProjectFields.FirstOrDefault(f => f.Id == Field(variables, "field"));

                    if (Field(variables, "project") != ProjectId || cleared is null || (dateField.Id is null && selectField.Id is null))
                    {
                        return GraphQlError("clearProjectV2ItemFieldValue", "NOT_FOUND", "Could not resolve to a node with the global id given.");
                    }

                    if (dateField.Id is not null) cleared.Dates.Remove(dateField.Name);
                    if (selectField.Id is not null) cleared.Fields.Remove(selectField.Name);
                    Console.WriteLine($"GITHUB board: {cleared.ItemId} {(dateField.Name ?? selectField.Name)} cleared");

                    return Json(new JsonObject { ["data"] = new JsonObject { ["clearProjectV2ItemFieldValue"] = new JsonObject { ["projectV2Item"] = new JsonObject { ["id"] = cleared.ItemId } } } });
                }

                if (query.Contains("updateProjectV2ItemFieldValue", StringComparison.Ordinal))
                {
                    var field = ProjectFields.FirstOrDefault(f => f.Id == Field(variables, "field"));
                    var chosen = (field.Options ?? []).FirstOrDefault(o => o.Id == Field(variables, "option")).Name;
                    var row = board.Values.FirstOrDefault(r => r.ItemId == Field(variables, "item"));

                    if (Field(variables, "project") != ProjectId || field.Id is null || chosen is null || row is null)
                    {
                        return GraphQlError("updateProjectV2ItemFieldValue", "NOT_FOUND", "Could not resolve to a node with the global id given.");
                    }

                    row.Fields[field.Name] = chosen;
                    Console.WriteLine($"GITHUB board: {row.ItemId} {field.Name} = {chosen}");

                    return Json(new JsonObject { ["data"] = new JsonObject { ["updateProjectV2ItemFieldValue"] = new JsonObject { ["projectV2Item"] = new JsonObject { ["id"] = row.ItemId } } } });
                }

                if (query.Contains("addProjectV2ItemById", StringComparison.Ordinal))
                {
                    var key = issues.Keys.FirstOrDefault(k => NodeId("I", k.Repo, k.Number) == Field(variables, "content"));

                    if (Field(variables, "project") != ProjectId || key == default)
                    {
                        return GraphQlError("addProjectV2ItemById", "NOT_FOUND", "Could not resolve to a node with the global id given.");
                    }

                    // An issue already on the board keeps its item, as on GitHub.
                    if (!board.TryGetValue(key, out var row))
                    {
                        row = new BoardRow { ItemId = $"PVTI_{key.Repo.Replace('/', '_')}_{key.Number}" };
                        board[key] = row;
                        Console.WriteLine($"GITHUB board: {key.Repo}#{key.Number} added as {row.ItemId}");
                    }

                    return Json(new JsonObject { ["data"] = new JsonObject { ["addProjectV2ItemById"] = new JsonObject { ["item"] = new JsonObject { ["id"] = row.ItemId } } } });
                }

                if (query.Contains("closeIssue", StringComparison.Ordinal))
                {
                    var key = board.Keys.FirstOrDefault(k => NodeId("I", k.Repo, k.Number) == Field(variables, "issue"));

                    if (key == default || !issues.TryGetValue(key, out var issue))
                    {
                        return GraphQlError("closeIssue", "NOT_FOUND", "Could not resolve to a node with the global id given.");
                    }

                    issue.State = "closed";
                    issue.UpdatedAt = Now();
                    board[key].ClosedAt = Now();
                    board[key].ClosedAs = Field(variables, "reason");
                    Console.WriteLine($"GITHUB board: {key.Repo}#{key.Number} closed as {board[key].ClosedAs}");

                    return Json(new JsonObject { ["data"] = new JsonObject { ["closeIssue"] = new JsonObject { ["issue"] = new JsonObject { ["id"] = Field(variables, "issue"), ["state"] = "CLOSED" } } } });
                }

                if (!query.Contains("projectV2(number:", StringComparison.Ordinal))
                {
                    return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "This stand-in knows the project read, updateProjectV2ItemFieldValue, clearProjectV2ItemFieldValue, closeIssue and addProjectV2ItemById." }) });
                }

                var number = variables?["number"] is JsonValue n && n.TryGetValue<int>(out var asked) ? asked : 0;

                if (number != ProjectNumber)
                {
                    return Json(new JsonObject
                    {
                        ["data"] = new JsonObject { ["organization"] = new JsonObject { ["projectV2"] = null } },
                        ["errors"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "NOT_FOUND",
                            ["path"] = new JsonArray("organization", "projectV2"),
                            ["message"] = $"Could not resolve to a ProjectV2 with the number {number}.",
                        }),
                    });
                }

                // Pages of two, although the client asks for fifty: the suite's board is a dozen
                // issues, and a client that follows the cursor once would not be known to follow it.
                const int pageSize = 2;

                var keys = board.Keys.OrderBy(k => k.Repo, StringComparer.Ordinal).ThenBy(k => k.Number).ToList();
                var from = int.TryParse(Field(variables, "after"), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor) ? cursor : 0;
                var page = keys.Skip(from).Take(pageSize).ToList();
                var nodes = new JsonArray([.. page.Select(ItemJson)]);

                if (from == 0)
                {
                    // What a board holds besides issues: a draft, and an item the token may not see.
                    nodes.Add(new JsonObject { ["id"] = "PVTI_draft", ["fieldValues"] = new JsonObject { ["nodes"] = new JsonArray() }, ["content"] = new JsonObject { ["__typename"] = "DraftIssue" } });
                    nodes.Add(new JsonObject { ["id"] = "PVTI_redacted", ["fieldValues"] = new JsonObject { ["nodes"] = new JsonArray() }, ["content"] = null });
                }

                var more = from + pageSize < keys.Count;

                return Json(new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["organization"] = new JsonObject
                        {
                            ["issueTypes"] = new JsonObject
                            {
                                ["nodes"] = new JsonArray([.. IssueTypes.Select(t => new JsonObject { ["name"] = t.Name, ["isEnabled"] = t.Enabled })]),
                            },
                            ["projectV2"] = new JsonObject
                            {
                                ["id"] = ProjectId,
                                ["title"] = "Platform planning",
                                ["url"] = $"https://github.com/orgs/{Field(variables, "owner")}/projects/{ProjectNumber}",
                                ["fields"] = new JsonObject
                                {
                                    ["nodes"] = new JsonArray(
                                    [
                                        new JsonObject(),
                                        .. ProjectFields.Select(f => new JsonObject
                                        {
                                            ["id"] = f.Id,
                                            ["name"] = f.Name,
                                            ["options"] = new JsonArray([.. f.Options.Select(o => new JsonObject { ["id"] = o.Id, ["name"] = o.Name })]),
                                        }),
                                        .. DateFields.Select(f => new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["dataType"] = "DATE" }),
                                        new JsonObject { ["id"] = "F_title", ["name"] = "Title", ["dataType"] = "TITLE" },
                                    ]),
                                },
                                ["items"] = new JsonObject
                                {
                                    ["pageInfo"] = new JsonObject
                                    {
                                        ["hasNextPage"] = more,
                                        ["endCursor"] = more ? (from + pageSize).ToString(CultureInfo.InvariantCulture) : null,
                                    },
                                    ["nodes"] = nodes,
                                },
                            },
                        },
                    },
                });
            }
        });

        // A new issue, opened by the token's account. Numbers go on from the highest the
        // repository has, so a suite that opens one finds it after the seeded ones.
        api.MapPost("/repos/{owner}/{repo}/issues", async (string owner, string repo, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);
            var title = Field(body, "title");
            var type = Field(body, "type");

            if (string.IsNullOrWhiteSpace(title))
            {
                return Problem(422, "Validation Failed");
            }

            if (type is not null && !IssueTypes.Any(t => t.Enabled && string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)))
            {
                return Problem(422, "Validation Failed: type is not an issue type of the organisation");
            }

            lock (gate)
            {
                var name = $"{owner}/{repo}";
                var number = Math.Max(100, issues.Keys.Where(k => k.Repo == name).Select(k => k.Number).DefaultIfEmpty(0).Max()) + 1;
                var now = Now();
                var issue = new Issue
                {
                    Repo = name,
                    Number = number,
                    Author = bot,
                    Title = title,
                    Body = Field(body, "body"),
                    Type = type is null ? null : IssueTypes.First(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)).Name,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                issues[(name, number)] = issue;
                Console.WriteLine($"GITHUB board: {name}#{number} opened");

                return Results.Text(IssueJson(issue).ToJsonString(), "application/json", statusCode: 201);
            }
        });

        // An edit of the title or the description, as the planner's MCP tool sends it (#307):
        // only what changes is sent, and a description sent empty clears it.
        api.MapPatch("/repos/{owner}/{repo}/issues/{number:int}", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Problem(404, "Not Found");
                }

                if (Field(body, "title") is { } title)
                {
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return Problem(422, "Validation Failed");
                    }

                    issue.Title = title;
                }

                if (body is JsonObject o && o.ContainsKey("body"))
                {
                    issue.Body = Field(body, "body") is { Length: > 0 } text ? text : null;
                }

                issue.UpdatedAt = Now();
                Console.WriteLine($"GITHUB board: {issue.Repo}#{number} edited");

                return Results.Text(IssueJson(issue).ToJsonString(), "application/json");
            }
        });

        api.MapGet("/repos/{owner}/{repo}/labels", (string owner, string repo) =>
        {
            lock (gate)
            {
                var name = $"{owner}/{repo}";
                var used = issues.Values.Where(i => i.Repo == name).SelectMany(i => i.Labels)
                    .Where(l => !RepositoryLabels.Any(r => string.Equals(r.Name, l, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                return Json(new JsonArray([
                    .. RepositoryLabels.Select(l => new JsonObject { ["name"] = l.Name, ["description"] = l.Description }),
                    .. used.Select(l => new JsonObject { ["name"] = l, ["description"] = null }),
                ]));
            }
        });

        api.MapPost("/repos/{owner}/{repo}/issues/{number:int}/labels", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Problem(404, "Not Found");
                }

                foreach (var label in (body?["labels"] as JsonArray ?? []).Select(l => l?.ToString() ?? string.Empty).Where(l => l.Length > 0))
                {
                    // Already there: nothing happens, and no event is written. Its date stays.
                    if (issue.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    issue.Labels.Add(label);
                    issue.UpdatedAt = Now();

                    if (board.TryGetValue((issue.Repo, issue.Number), out var row))
                    {
                        row.LabelledAt[label] = Now();
                    }
                }

                return Json(new JsonArray([.. issue.Labels.Select(l => new JsonObject { ["name"] = l })]));
            }
        });

        api.MapDelete("/repos/{owner}/{repo}/issues/{number:int}/labels/{name}", (string owner, string repo, int number, string name) =>
        {
            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Problem(404, "Not Found");
                }

                if (issue.Labels.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase)) is not { } present)
                {
                    return Problem(404, "Label does not exist");
                }

                issue.Labels.Remove(present);
                issue.UpdatedAt = Now();

                if (board.TryGetValue((issue.Repo, issue.Number), out var row))
                {
                    row.LabelledAt.Remove(present);
                }

                return Json(new JsonArray([.. issue.Labels.Select(l => new JsonObject { ["name"] = l })]));
            }
        });

        // No timeline event is written for these two, unlike the controls' assign and unassign:
        // the board's repositories are ones no install lists, so no poller reads their timeline.
        api.MapPost("/repos/{owner}/{repo}/issues/{number:int}/assignees", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Problem(404, "Not Found");
                }

                foreach (var login in (body?["assignees"] as JsonArray ?? []).Select(a => a?.ToString() ?? string.Empty).Where(a => a.Length > 0))
                {
                    if (string.Equals(login, Unassignable, StringComparison.OrdinalIgnoreCase) || issue.Assignees.Any(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    issue.Assignees.Add(string.Equals(login, bot.Login, StringComparison.OrdinalIgnoreCase) ? bot : new Account(login, 7000 + login.Length));
                    issue.UpdatedAt = Now();
                }

                return Results.Text(IssueJson(issue).ToJsonString(), "application/json", statusCode: 201);
            }
        });

        api.MapDelete("/repos/{owner}/{repo}/issues/{number:int}/assignees", async (string owner, string repo, int number, HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            lock (gate)
            {
                if (!issues.TryGetValue(($"{owner}/{repo}", number), out var issue))
                {
                    return Problem(404, "Not Found");
                }

                foreach (var login in (body?["assignees"] as JsonArray ?? []).Select(a => a?.ToString() ?? string.Empty))
                {
                    issue.Assignees.RemoveAll(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
                }

                issue.UpdatedAt = Now();

                return Json(IssueJson(issue));
            }
        });

        var control = app.MapGroup("/github/control/project");

        // The board as it is now, flat: what a suite compares the planner's page with.
        control.MapGet("", () =>
        {
            lock (gate)
            {
                return Json(new JsonObject
                {
                    ["number"] = ProjectNumber,
                    ["fields"] = new JsonArray([.. ProjectFields.Select(f => new JsonObject
                    {
                        ["name"] = f.Name,
                        ["options"] = new JsonArray([.. f.Options.Select(o => (JsonNode)o.Name)]),
                    })]),
                    ["items"] = new JsonArray([.. board.OrderBy(b => b.Key.Repo, StringComparer.Ordinal).ThenBy(b => b.Key.Number).Select(b =>
                    {
                        var issue = issues[b.Key];

                        return new JsonObject
                        {
                            ["item"] = b.Value.ItemId,
                            ["repository"] = issue.Repo,
                            ["number"] = issue.Number,
                            ["title"] = issue.Title,
                            ["type"] = issue.Type,
                            ["author"] = issue.Author.Login,
                            ["state"] = issue.State,
                            ["closedAs"] = b.Value.ClosedAs,
                            ["fields"] = new JsonObject([.. b.Value.Fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))]),
                            ["dates"] = new JsonObject([.. b.Value.Dates.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)d.Value))]),
                            ["labels"] = new JsonArray([.. issue.Labels.Select(l => (JsonNode)l)]),
                            ["assignees"] = new JsonArray([.. issue.Assignees.Select(a => (JsonNode)a.Login)]),
                        };
                    })]),
                });
            }
        });

        // The seeded board again, and nothing else of the stand-in touched.
        control.MapPost("/reset", () =>
        {
            lock (gate)
            {
                Seed();
            }

            Console.WriteLine("GITHUB board: seeded again");
            return Results.NoContent();
        });

        return Seed;
    }

    private static JsonObject PullNode(BoardPull pull, bool withBody)
    {
        var node = new JsonObject
        {
            ["__typename"] = "PullRequest",
            ["number"] = pull.Number,
            ["url"] = $"https://github.com/{pull.Repo}/pull/{pull.Number}",
            ["state"] = pull.State,
            ["updatedAt"] = Stamp(pull.At),
            ["mergedAt"] = pull.State == "MERGED" ? Stamp(pull.At) : null,
            ["repository"] = new JsonObject { ["nameWithOwner"] = pull.Repo },
        };

        if (withBody)
        {
            node["body"] = pull.Body;
        }

        return node;
    }

    private static IResult Json(JsonNode body) => Results.Text(body.ToJsonString(), "application/json");

    private static IResult GraphQlError(string mutation, string type, string message) => Json(new JsonObject
    {
        ["data"] = new JsonObject { [mutation] = null },
        ["errors"] = new JsonArray(new JsonObject { ["type"] = type, ["path"] = new JsonArray(mutation), ["message"] = message }),
    });
}
