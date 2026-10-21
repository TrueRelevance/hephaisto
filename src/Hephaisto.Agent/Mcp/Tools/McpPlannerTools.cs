using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>
/// The planner (#307): the GitHub project board's issues - today's plan, the backlog - and the
/// planner's clicks, for a coding agent that is asked to open, plan or close an issue.
/// </summary>
/// <remarks>
/// Three reads any token may call, four writes for a token that may write - none of them
/// approves an issue's plan or starts a code fix. See <see cref="McpPlanner"/>.
/// </remarks>
[McpServerToolType]
public sealed class McpPlannerTools(McpPlanner planner)
{
    private const string IssueHelp = "The issue: owner/repo#12, its GitHub URL, or repo#12 when only one repository of the board has that number.";
    private const string OnBehalfOfHelp = "The person who asked for this, by name. Recorded as a claim beside the token, never as the actor.";
    private const string DayHelp = "When it is planned for: today, not today, tomorrow, next week, backlog (no day, Status Backlog) or none.";

    [McpServerTool(Name = "list_planner_issues", Title = "Planner issues", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the GitHub issues on the planning board (the GitHub project the planner shows, e.g. TR Planning): what is left for today, what is in the backlog, what is planned for a later day, what needs a decision, every open issue, or what was closed today. Each with its owner/repo#number, title, type, Status, Priority, Area and the other fields, the day it is planned for, whether it is worked on now, assignees, labels and pull requests. Filter by status, priority, repository, assignee and free text. Answers which issues are left for today, what is in the backlog and what to work on next.")]
    public async Task<string> ListPlannerIssuesAsync(
        [Description("today (the default: what is planned for today and still open), backlog (Status Backlog), later (planned for a later day), decide (needs a decision), open (every open issue) or closed (closed today).")] string? view = null,
        [Description("The Status option, exactly, e.g. Ready or In progress.")] string? status = null,
        [Description("The Priority option, exactly, e.g. P1.")] string? priority = null,
        [Description("Part of the repository's name, owner/repo.")] string? repository = null,
        [Description("A GitHub login the issue is assigned to.")] string? assignee = null,
        [Description("Words that must all be in the title, description, labels or reference.")] string? query = null,
        [Description("At most this many, 1 to 200. Default 50.")] int? limit = null,
        [Description("true reads the board from GitHub now, about 15 seconds. Default: the board Hephaisto read in the last two minutes, which includes every change made through it.")] bool refresh = false,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.ListAsync(view, status, priority, repository, assignee, query, McpQuery.Limit(limit, 50, 200), refresh, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "get_planner_issue", Title = "One planner issue", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get one GitHub issue of the planning board in full: title, fields, day, assignees, labels, pull requests, sub-issues, its description, the newest comments, Claude's analysis when somebody asked for one in the planner, and its page in the console. Open or closed.")]
    public async Task<string> GetPlannerIssueAsync(
        [Description(IssueHelp)] string issue,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.GetAsync(issue, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "lookup_planner_fields", Title = "Planner fields", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Look up what a GitHub issue on the planning board can be filled in with: the board's fields (Status, Priority, Area, ...) and their options, which fields the board asks for, the issue types (Bug, Feature, Task, Epic), the repositories a new issue can be opened in, the accounts it can be assigned to, and the days it can be planned for. Read before create_planner_issue to fill in every field.")]
    public async Task<string> LookupPlannerFieldsAsync(CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.FieldsAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "create_planner_issue", Title = "Open a planner issue", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Create a GitHub issue on the planning board, through Hephaisto's planner: opened in the repository, put on the board with its type and fields (Status, Priority, Area, ...), planned for a day (today, tomorrow, next week, backlog) and assigned. Use it instead of the gh CLI for a new issue or bug, so the issue is on the board and in today's plan at once. Fill in the type and the fields from lookup_planner_fields. Opened by Hephaisto's GitHub account; written to the audit trail as this token.")]
    public async Task<string> CreatePlannerIssueAsync(
        ClaimsPrincipal user,
        [Description("One line: what the issue is about.")] string title,
        [Description("The description, markdown: what is wrong or wanted, why, and what done looks like.")] string? body = null,
        [Description("The issue type, as lookup_planner_fields lists them: Bug, Feature, Task, Epic.")] string? type = null,
        [Description("The board's fields by name, each to one of its options, e.g. {\"Status\":\"Ready\",\"Priority\":\"P1\",\"Area\":\"Infra\"}.")] Dictionary<string, string>? fields = null,
        [Description(DayHelp + " Default: none.")] string? day = null,
        [Description("owner/repo, one of lookup_planner_fields' repositories. Default: the first of them.")] string? repository = null,
        [Description("A GitHub login from lookup_planner_fields' assignees to assign it to.")] string? assignee = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.CreateAsync(Caller(user), repository, title, body, type, fields, day, assignee, onBehalfOf, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "update_planner_issue", Title = "Change a planner issue", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Update an open GitHub issue of the planning board: plan it for a day (today, not today, tomorrow, next week, backlog), set fields such as Status, Priority or Area, assign or unassign a person, mark it as worked on now or no longer, change its title or description. Several at once, in that order; stops at the first change GitHub refuses and says what was made. Written to the audit trail as this token.")]
    public async Task<string> UpdatePlannerIssueAsync(
        ClaimsPrincipal user,
        [Description(IssueHelp)] string issue,
        [Description(DayHelp)] string? day = null,
        [Description("The board's fields by name, each to one of its options, e.g. {\"Status\":\"In progress\"}.")] Dictionary<string, string>? fields = null,
        [Description("A GitHub login to assign it to.")] string? assign = null,
        [Description("A GitHub login to take off it.")] string? unassign = null,
        [Description("true: worked on now - one issue at a time, and only one planned for today. false: no longer.")] bool? workedOnNow = null,
        [Description("A new title, one line.")] string? title = null,
        [Description("A new description, markdown; it replaces the old one whole. An empty string clears it.")] string? body = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.UpdateAsync(Caller(user), issue, title, body, fields, day, assign, unassign, workedOnNow, onBehalfOf, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "close_planner_issue", Title = "Close a planner issue", Destructive = true, Idempotent = false, OpenWorld = true)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Close a GitHub issue of the planning board as done (completed) or not planned, optionally with a closing comment first. Close only when the person asked; a closed issue can be reopened on GitHub. Issues are never deleted. Written to the audit trail as this token.")]
    public async Task<string> ClosePlannerIssueAsync(
        ClaimsPrincipal user,
        [Description(IssueHelp)] string issue,
        [Description("done (the default) or not planned.")] string? reason = null,
        [Description("A comment posted on the issue before it is closed: what was done, or why not.")] string? comment = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.CloseAsync(Caller(user), issue, reason, comment, onBehalfOf, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "comment_on_planner_issue", Title = "Comment on a planner issue", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Comment on a GitHub issue of the planning board, open or closed: posted by Hephaisto's GitHub account and signed as relayed by an agent, for the person named in onBehalfOf. Not taken as a person's answer to a code-fix plan. Written to the audit trail as this token.")]
    public async Task<string> CommentOnPlannerIssueAsync(
        ClaimsPrincipal user,
        [Description(IssueHelp)] string issue,
        [Description("The comment, markdown.")] string text,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await planner.CommentAsync(Caller(user), issue, text, onBehalfOf, cancellationToken).ConfigureAwait(false));

    private static McpCaller Caller(ClaimsPrincipal user) =>
        McpCaller.From(user) ?? throw new McpException("This request did not come through the MCP endpoint's authentication.");
}
