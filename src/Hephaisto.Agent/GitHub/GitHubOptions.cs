using System.Globalization;
using System.Text.RegularExpressions;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// GitHub issues as work (v0.14.0). Bound from <c>GitHub:*</c>; the chart emits <c>GitHub__*</c>.
/// </summary>
/// <remarks>
/// Every default is inert: not enabled, no repository listed. An install that never heard of this
/// holds no GitHub credential and asks GitHub nothing.
/// </remarks>
public sealed partial class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>The fastest the poller may be told to go. GitHub's own guidance for polling is a minute.</summary>
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);

    public bool Enabled { get; set; }

    /// <summary>
    /// The REST API's root. GitHub Enterprise Server's is <c>https://host/api/v3</c>; a test's is a
    /// stand-in. A path in it is kept: every call is made relative to it.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// The account's token, from the environment variable <c>GitHub__Token</c>, which the chart
    /// fills from a Secret of the AGENT's namespace (<c>secrets.github</c>). Never the coder's
    /// Secret: that one the agent cannot read, and must stay unable to.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// The account issues are assigned to. Empty means whoever the token belongs to
    /// (<c>GET /user</c>), which is the right answer unless the token is somebody else's.
    /// </summary>
    public string BotLogin { get; set; } = string.Empty;

    /// <summary>
    /// The authorization list: <c>owner/repo</c>. An issue in a repository that is not listed is
    /// never asked about, however it is assigned. Empty lists nothing.
    /// </summary>
    public List<string> Repositories { get; set; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>An HTTP proxy for every call to GitHub, or empty for a direct connection.</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// Who may answer a plan on the issue, by account NUMBER. Kept as the strings the operator
    /// typed so that a login where a number belongs is refused with a sentence rather than with
    /// a binder's exception. Stored and checked here; read when a comment is a command.
    /// </summary>
    public List<string> Approvers { get; set; } = [];

    /// <summary>The planner (v0.15.0): one project's open issues, walked through in the console.</summary>
    public GitHubPlannerOptions Planner { get; set; } = new();

    /// <summary>The approvers as numbers. Only meaningful once <see cref="Problems"/> is empty.</summary>
    public IReadOnlySet<long> ApproverIds() =>
        Approvers
            .Select(a => long.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

    /// <summary>The API root with exactly one trailing slash, so a relative path is appended to it and not to its parent.</summary>
    public Uri ApiBase() => new(ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    /// <summary>
    /// Where GraphQL is asked - the planner's board is nowhere else. Beside the REST root on
    /// github.com and on a stand-in; on GitHub Enterprise Server it is <c>/api/graphql</c>, a
    /// sibling of the <c>/api/v3</c> the REST root ends in.
    /// </summary>
    public string GraphQlUrl()
    {
        var api = ApiBase();

        return new Uri(api, api.AbsolutePath.EndsWith("/api/v3/", StringComparison.OrdinalIgnoreCase) ? "../graphql" : "graphql").ToString();
    }

    /// <summary>
    /// Everything wrong with this configuration, one sentence each. Empty when it may start -
    /// and always empty when the feature is off, because an option nobody enabled is not a fault.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        if (!Enabled)
        {
            // The one fault an install with GitHub off can have: a planner that was asked for
            // and would silently never show a board.
            return Planner.Enabled
                ? ["GitHub:Planner:Enabled needs GitHub:Enabled: the planner reads its board with the agent's GitHub token, and there is none."]
                : [];
        }

        var problems = new List<string>();

        problems.AddRange(Planner.Problems());

        if (string.IsNullOrWhiteSpace(Token))
        {
            problems.Add(
                "GitHub:Enabled needs GitHub:Token: the account's token, from the Secret secrets.github names "
                + "(key GITHUB_TOKEN). Without one every call is refused and nothing is ever taken.");
        }

        if (Repositories.Count == 0)
        {
            problems.Add(
                "GitHub:Enabled needs at least one entry in GitHub:Repositories (owner/repo): the list is "
                + "what authorizes a repository, and an empty one polls nothing.");
        }

        foreach (var repository in Repositories.Where(r => !RepositoryName().IsMatch(r)))
        {
            problems.Add($"GitHub:Repositories entry '{repository}' is not owner/repo.");
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https"))
        {
            problems.Add($"GitHub:ApiBaseUrl '{ApiBaseUrl}' is not an absolute http(s) URL.");
        }

        if (!string.IsNullOrWhiteSpace(ProxyUrl)
            && (!Uri.TryCreate(ProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https")))
        {
            problems.Add($"GitHub:ProxyUrl '{ProxyUrl}' is not an absolute http(s) URL.");
        }

        if (PollInterval < MinimumPollInterval)
        {
            problems.Add(
                $"GitHub:PollInterval must be at least {MinimumPollInterval.TotalSeconds:0} seconds; "
                + "GitHub asks a poller for a minute.");
        }

        foreach (var approver in Approvers.Where(a => !long.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
        {
            problems.Add(
                $"GitHub:Approvers entry '{approver}' is not an account number. A login can be renamed and "
                + "taken by somebody else; list the numeric id (gh api users/<login> --jq .id).");
        }

        return problems;
    }

    // GitHub's own rules, loosely: an owner is letters, digits and hyphens; a repository adds
    // dots and underscores. Loose is enough - the point is to refuse a URL or a bare name.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryName();
}

/// <summary>
/// The planner (v0.15.0). Bound from <c>GitHub:Planner:*</c>; the chart emits
/// <c>GitHub__Planner__*</c>.
/// </summary>
/// <remarks>
/// Off by default, and it needs <c>GitHub:Enabled</c>: it has no token of its own. What it reads
/// is ONE project of an organisation; what it writes there is always one person's click.
/// </remarks>
public sealed partial class GitHubPlannerOptions
{
    public bool Enabled { get; set; }

    /// <summary>The organisation the project belongs to. A project of a user account is not read.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The project's number, as in <c>github.com/orgs/&lt;owner&gt;/projects/&lt;number&gt;</c>.</summary>
    public int Number { get; set; }

    /// <summary>
    /// Names of the board's fields an item should not leave empty. Only the operator knows which
    /// of a board's fields are meant to be filled; a name the board does not have is ignored.
    /// </summary>
    public List<string> RequiredFields { get; set; } = [];

    /// <summary>How many days an open pull request may be quiet before its issue is put in front of a person.</summary>
    public int StaleAfterDays { get; set; } = 5;

    /// <summary>
    /// The logins an issue can be assigned to from the planner, besides the agent's own account.
    /// A list and not "whoever is signed in": the console knows a person by their identity
    /// provider's name, and that is not a GitHub login.
    /// </summary>
    public List<string> Assignees { get; set; } = [];

    /// <summary>
    /// Where a new issue can be opened from the planner, as <c>owner/repo</c>; the first is chosen
    /// unless somebody picks another. The repositories of the board's issues follow, the one with
    /// the most first - and are all there is when this is empty.
    /// </summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>
    /// The board's date field an issue is planned for a later day by - tomorrow, next week. On
    /// that day it is planned for today; a day that passed with the issue open makes it left
    /// over. A board without a date field of this name plans by the two labels only.
    /// </summary>
    public string DayField { get; set; } = "Planned for";

    /// <summary>The option of the Status field that "backlog" sets.</summary>
    public string BacklogStatus { get; set; } = "Backlog";

    /// <summary>Claude labels an issue and writes an analysis of it, when a person asks (v0.15.0).</summary>
    public GitHubPlannerTriageOptions Triage { get; set; } = new();

    /// <summary>The weekly review of every open issue of <see cref="Owner"/> (#312).</summary>
    public GitHubPlannerReviewOptions Review { get; set; } = new();

    public IReadOnlyList<string> Problems()
    {
        if (!Enabled)
        {
            return [];
        }

        var problems = new List<string>();

        if (!Login().IsMatch(Owner))
        {
            problems.Add($"GitHub:Planner:Owner '{Owner}' is not an organisation's login. It is the <owner> of github.com/orgs/<owner>/projects/<number>.");
        }

        if (Number <= 0)
        {
            problems.Add("GitHub:Planner:Number must be the project's number, the <number> of github.com/orgs/<owner>/projects/<number>.");
        }

        if (StaleAfterDays < 1)
        {
            problems.Add("GitHub:Planner:StaleAfterDays must be at least 1.");
        }

        foreach (var field in RequiredFields.Where(string.IsNullOrWhiteSpace))
        {
            problems.Add("GitHub:Planner:RequiredFields holds an empty entry; each is the name of a field of the board.");
        }

        foreach (var assignee in Assignees.Where(a => !Login().IsMatch(a)))
        {
            problems.Add($"GitHub:Planner:Assignees entry '{assignee}' is not a GitHub login.");
        }

        foreach (var repository in Repositories.Where(r => !Repository().IsMatch(r)))
        {
            problems.Add($"GitHub:Planner:Repositories entry '{repository}' is not owner/repo.");
        }

        problems.AddRange(Triage.Problems());
        problems.AddRange(Review.Problems());

        return problems;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex Login();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$")]
    private static partial Regex Repository();
}

/// <summary>
/// Triage (v0.15.0): a person on the planner asks Claude to label an issue and to write what
/// working on it means. Bound from <c>GitHub:Planner:Triage:*</c>.
/// </summary>
/// <remarks>
/// It runs as a coder Job - the coder's image, namespace, Secret, egress proxy, model and
/// dev-context, all from <c>CodeFix:*</c> - and costs what one read-only Claude run costs. Nothing
/// starts it but a click.
/// </remarks>
public sealed class GitHubPlannerTriageOptions
{
    public bool Enabled { get; set; }

    public decimal MaxCostUsd { get; set; } = 2m;

    public int MaxTurns { get; set; } = 40;

    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>At most this many triage Jobs at once; a click beyond it is refused and says so.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    public IReadOnlyList<string> Problems()
    {
        if (!Enabled)
        {
            return [];
        }

        var problems = new List<string>();

        if (MaxCostUsd <= 0)
        {
            problems.Add("GitHub:Planner:Triage:MaxCostUsd must be more than 0.");
        }

        if (MaxTurns is < 1 or > 500)
        {
            problems.Add("GitHub:Planner:Triage:MaxTurns must be between 1 and 500.");
        }

        if (Deadline < TimeSpan.FromMinutes(2) || Deadline > TimeSpan.FromHours(2))
        {
            problems.Add("GitHub:Planner:Triage:Deadline must be between 2 minutes and 2 hours.");
        }

        if (MaxConcurrentJobs < 1)
        {
            problems.Add("GitHub:Planner:Triage:MaxConcurrentJobs must be at least 1.");
        }

        return problems;
    }
}

/// <summary>
/// The weekly review (#312): every open issue of the planner's organisation, on the board or not,
/// sorted by a read-only coder Job of phase <c>review</c> into can be closed, a real bug, still to
/// build - or unsure. Bound from <c>GitHub:Planner:Review:*</c>.
/// </summary>
/// <remarks>
/// It proposes and writes nothing to GitHub: every close or field change is a person's click
/// through <see cref="Planner.PlannerService"/>. Like triage it runs as coder Jobs and borrows
/// the coder's image, namespace, Secret, egress proxy, model and dev-context from <c>CodeFix:*</c>.
/// </remarks>
public sealed class GitHubPlannerReviewOptions
{
    /// <summary>The schema's cap on a batch (review-request, <c>issues.maxItems</c>).</summary>
    public const int MaxIssuesPerJob = 40;

    public bool Enabled { get; set; }

    /// <summary>Five fields, minute hour day-of-month month day-of-week, in <see cref="TimeZone"/>. Monday 06:00 by default.</summary>
    public string Schedule { get; set; } = "0 6 * * 1";

    /// <summary>An IANA name.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    public int IssuesPerJob { get; set; } = 25;

    /// <summary>An unchanged issue's verdict is asked for again once it is this old.</summary>
    public int RecheckAfterDays { get; set; } = 28;

    /// <summary>What one whole run may cost, across all its Jobs. A batch that would start beyond it does not.</summary>
    public decimal MaxCostUsd { get; set; } = 10m;

    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>One Job's deadline.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>One Job's turns.</summary>
    public int MaxTurns { get; set; } = 60;

    public IReadOnlyList<string> Problems()
    {
        if (!Enabled)
        {
            return [];
        }

        var problems = new List<string>();

        if (Hephaisto.Core.Planning.CronSchedule.TryParse(Schedule, out _, out var why) is false)
        {
            problems.Add($"GitHub:Planner:Review:Schedule '{Schedule}' is not a five-field cron expression: {why}");
        }

        if (Zone() is null)
        {
            problems.Add($"GitHub:Planner:Review:TimeZone '{TimeZone}' is not a time zone this system knows (an IANA name such as Europe/Berlin).");
        }

        if (IssuesPerJob is < 1 or > MaxIssuesPerJob)
        {
            problems.Add($"GitHub:Planner:Review:IssuesPerJob must be between 1 and {MaxIssuesPerJob}.");
        }

        if (RecheckAfterDays < 1)
        {
            problems.Add("GitHub:Planner:Review:RecheckAfterDays must be at least 1.");
        }

        if (MaxCostUsd is <= 0 or > 1000)
        {
            problems.Add("GitHub:Planner:Review:MaxCostUsd must be more than 0 and at most 1000.");
        }

        if (MaxConcurrentJobs < 1)
        {
            problems.Add("GitHub:Planner:Review:MaxConcurrentJobs must be at least 1.");
        }

        if (Deadline < TimeSpan.FromMinutes(2) || Deadline > TimeSpan.FromHours(4))
        {
            problems.Add("GitHub:Planner:Review:Deadline must be between 2 minutes and 4 hours.");
        }

        if (MaxTurns is < 1 or > 500)
        {
            problems.Add("GitHub:Planner:Review:MaxTurns must be between 1 and 500.");
        }

        return problems;
    }

    /// <summary>The time zone the schedule is read in, or null when the system does not know it.</summary>
    public TimeZoneInfo? Zone()
    {
        try
        {
            return string.IsNullOrWhiteSpace(TimeZone) ? null : TimeZoneInfo.FindSystemTimeZoneById(TimeZone.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
