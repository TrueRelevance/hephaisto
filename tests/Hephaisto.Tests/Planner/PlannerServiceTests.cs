using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Planner;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// The planner's service: when it asks GitHub, what a click sends, and that the audit row is
/// there before GitHub hears of anything.
/// </summary>
public sealed class PlannerServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- reading ------------------------------------------------------------------------------

    [Fact]
    public async Task Off_asks_github_nothing()
    {
        var (planner, github, _, _) = Build(enabled: false);

        var snapshot = await planner.ReadAsync(refresh: true, Ct);

        snapshot.State.Should().Be(PlannerState.Off);
        github.Calls.Should().BeEmpty();
        planner.Kept.Should().BeNull();
    }

    [Fact]
    public async Task A_read_is_kept_for_half_a_minute_unless_a_person_asks_again()
    {
        var (planner, github, clock, _) = Build();

        var first = await planner.ReadAsync(refresh: false, Ct);
        await planner.ReadAsync(refresh: false, Ct);

        first.State.Should().Be(PlannerState.Read);
        first.Entries.Select(e => e.Item.Number).Should().Equal(431, 240, 57);
        github.Calls.Count(c => c == "project acme/3").Should().Be(1);

        await planner.ReadAsync(refresh: true, Ct);
        github.Calls.Count(c => c == "project acme/3").Should().Be(2);

        clock.UtcNow = clock.UtcNow.Add(PlannerService.KeepFor).AddSeconds(1);
        await planner.ReadAsync(refresh: false, Ct);
        github.Calls.Count(c => c == "project acme/3").Should().Be(3);
    }

    [Fact]
    public async Task The_agents_own_account_is_the_first_an_issue_can_be_assigned_to()
    {
        var (planner, _, _, _) = Build();

        (await planner.ReadAsync(false, Ct)).Assignees.Should().Equal("hephaisto-bot", "flo");
    }

    [Fact]
    public async Task A_board_that_cannot_be_read_says_why_and_a_later_failure_keeps_the_last_good_one()
    {
        var (planner, github, _, _) = Build();

        github.FailProject = new(GitHubOutcome.Unauthorized, null, "HTTP 403: Resource not accessible by personal access token");
        var never = await planner.ReadAsync(false, Ct);

        never.State.Should().Be(PlannerState.Unreadable);
        never.Problem.Should().Contain("Resource not accessible");
        never.Entries.Should().BeEmpty();

        github.FailProject = null;
        (await planner.ReadAsync(false, Ct)).State.Should().Be(PlannerState.Read, "a failure is not kept: the next look asks again");

        github.FailProject = new(GitHubOutcome.RateLimited, null, "limit", RetryAt: Given.Now.AddMinutes(5));
        var stale = await planner.ReadAsync(refresh: true, Ct);

        stale.State.Should().Be(PlannerState.Read);
        stale.Problem.Should().Contain("rate limited");
        stale.Entries.Should().HaveCount(3, "an empty page would read as an empty board");
    }

    // --- a field ------------------------------------------------------------------------------

    [Fact]
    public async Task A_field_is_set_by_name_and_github_is_told_the_ids_after_the_audit_row_exists()
    {
        var (planner, github, _, audit) = Build();

        github.OnWrite = () => audit.Rows.Should().ContainSingle("no audit, no action").Which.Type.Should().Be(PlannerService.AuditField);

        var result = await planner.SetFieldAsync("PVTI_431", "priority", "p1", "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Message.Should().Be("Priority set to P1");
        github.Calls.Should().Contain("field PVT_1 PVTI_431 F_prio p1");
        result.Snapshot.Entry("PVTI_431")!.Item.Field("Priority").Should().Be("P1");

        var row = audit.Rows.Single();
        row.Actor.Should().Be("flo");
        row.Summary.Should().Be("Priority of acme/api#431 set to P1");
        row.Detail.Should().Contain("\"from\":\"P0\"").And.Contain("\"to\":\"P1\"").And.Contain("\"number\":431");

        github.Calls.Count(c => c == "project acme/3").Should().Be(1, "a write changes the kept board; it does not read it again");
    }

    [Theory]
    [InlineData("Severity", "High", "the board has no field 'Severity'")]
    [InlineData("Priority", "P9", "'P9' is not an option of Priority")]
    public async Task What_is_not_on_the_board_is_not_sent_and_not_recorded(string field, string option, string says)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.SetFieldAsync("PVTI_431", field, option, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.NotFound);
        result.Message.Should().Be(says);
        github.Calls.Should().NotContain(c => c.StartsWith("field", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_is_a_second_row_and_changes_nothing_on_the_board()
    {
        var (planner, github, _, audit) = Build();

        github.FailWrite = new(GitHubOutcome.Unauthorized, false, "HTTP 403: Resource not accessible by personal access token");

        var result = await planner.SetFieldAsync("PVTI_431", "Priority", "P1", "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Refused);
        result.Message.Should().Contain("Resource not accessible");
        result.Snapshot.Entry("PVTI_431")!.Item.Field("Priority").Should().Be("P0");
        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditField, PlannerService.AuditRefused);
        audit.Rows[1].Summary.Should().StartWith("acme/api#431: GitHub refused");
    }

    [Theory]
    [InlineData("hephaisto/llm")]
    [InlineData("model")]
    public async Task A_models_name_may_not_decide(string actor)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.CloseAsync("PVTI_431", false, actor, Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Forbidden);
        github.Calls.Should().NotContain(c => c.StartsWith("close", StringComparison.Ordinal));
        audit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_item_that_is_not_among_the_open_ones_is_not_found()
    {
        var (planner, _, _, _) = Build();

        (await planner.CloseAsync("PVTI_nope", false, "flo", Ct)).Outcome.Should().Be(PlannerActionOutcome.NotFound);
    }

    // --- closing ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "close I_431 COMPLETED", "closed as done")]
    [InlineData(true, "close I_431 NOT_PLANNED", "closed as not planned")]
    public async Task A_closed_issue_leaves_the_queue_and_counts_as_done_today(bool notPlanned, string call, string says)
    {
        var (planner, github, _, audit) = Build();

        var result = await planner.CloseAsync("PVTI_431", notPlanned, "flo", Ct);

        result.Message.Should().Be(says);
        github.Calls.Should().Contain(call);
        result.Snapshot.Entry("PVTI_431").Should().BeNull();
        result.Snapshot.ClosedToday.Select(i => i.Number).Should().Equal(431);
        audit.Rows.Single().Type.Should().Be(PlannerService.AuditClose);
    }

    // --- the day ------------------------------------------------------------------------------

    [Fact]
    public async Task Today_puts_the_label_on_and_takes_the_other_one_off_first()
    {
        var (planner, github, _, audit) = Build();

        // #57 carries a "not today" of yesterday.
        var result = await planner.SetDayAsync("PVTI_57", PlannerDay.Today, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("unlabel acme/scraper#57 not today", "label acme/scraper#57 today");

        var entry = result.Snapshot.Entry("PVTI_57")!;
        entry.Day.Should().Be(PlannerDay.Today);
        entry.CarriedOver.Should().BeFalse();
        audit.Rows.Single().Summary.Should().Be("acme/scraper#57: today");
    }

    [Fact]
    public async Task A_label_that_is_already_there_is_set_again_so_that_it_is_dated_today()
    {
        var (planner, github, _, _) = Build();

        // #240 carries a "today" of two days ago: carried over.
        (await planner.ReadAsync(false, Ct)).Entry("PVTI_240")!.CarriedOver.Should().BeTrue();

        var result = await planner.SetDayAsync("PVTI_240", PlannerDay.Today, "flo", Ct);

        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("unlabel acme/web#240 today", "label acme/web#240 today");
        result.Snapshot.Entry("PVTI_240")!.CarriedOver.Should().BeFalse();
    }

    [Fact]
    public async Task Not_today_and_taking_the_decision_back()
    {
        var (planner, github, _, _) = Build();

        var away = await planner.SetDayAsync("PVTI_431", PlannerDay.NotToday, "flo", Ct);
        away.Snapshot.Entry("PVTI_431")!.Day.Should().Be(PlannerDay.NotToday);
        away.Message.Should().Be("not today");

        var back = await planner.SetDayAsync("PVTI_431", PlannerDay.Undecided, "flo", Ct);
        back.Snapshot.Entry("PVTI_431")!.Day.Should().Be(PlannerDay.Undecided);
        back.Message.Should().Be("not decided");

        github.Calls.Where(c => c.Contains("label", StringComparison.Ordinal)).Should().Equal("label acme/api#431 not today", "unlabel acme/api#431 not today");
    }

    [Fact]
    public async Task A_label_somebody_took_off_on_github_meanwhile_is_no_refusal()
    {
        var (planner, github, _, _) = Build();

        github.FailUnlabel = new(GitHubOutcome.NotFound, false, "HTTP 404: Label does not exist");

        var result = await planner.SetDayAsync("PVTI_57", PlannerDay.Today, "flo", Ct);

        result.Outcome.Should().Be(PlannerActionOutcome.Done);
        result.Snapshot.Entry("PVTI_57")!.Day.Should().Be(PlannerDay.Today);
    }

    // --- assigning ----------------------------------------------------------------------------

    [Fact]
    public async Task An_issue_is_assigned_only_to_an_account_the_planner_offers()
    {
        var (planner, github, _, audit) = Build();

        var bot = await planner.AssignAsync("PVTI_431", "Hephaisto-Bot", assign: true, "flo", Ct);
        var stranger = await planner.AssignAsync("PVTI_431", "stranger", assign: true, "flo", Ct);

        bot.Outcome.Should().Be(PlannerActionOutcome.Done);
        bot.Message.Should().Be("assigned to hephaisto-bot");
        bot.Snapshot.Entry("PVTI_431")!.Item.Assignees.Should().Equal("hephaisto-bot");
        stranger.Outcome.Should().Be(PlannerActionOutcome.NotFound);

        github.Calls.Where(c => c.Contains("assign", StringComparison.Ordinal)).Should().Equal("assign acme/api#431 hephaisto-bot");
        audit.Rows.Single().Type.Should().Be(PlannerService.AuditAssign);
    }

    [Fact]
    public async Task An_assignee_is_taken_off_and_an_assignment_github_left_out_is_a_refusal()
    {
        var (planner, github, _, _) = Build();

        await planner.AssignAsync("PVTI_431", "flo", assign: true, "flo", Ct);
        var off = await planner.AssignAsync("PVTI_431", "flo", assign: false, "flo", Ct);

        off.Snapshot.Entry("PVTI_431")!.Item.Assignees.Should().BeEmpty();

        github.LeaveOut = true;
        var refused = await planner.AssignAsync("PVTI_431", "flo", assign: true, "flo", Ct);

        refused.Outcome.Should().Be(PlannerActionOutcome.Refused);
        refused.Message.Should().Contain("may not be assigned in acme/api");
    }

    // --- the board under test -----------------------------------------------------------------

    private static (PlannerService Planner, FakeGitHub GitHub, Given.FixedClock Clock, FakeAudit Audit) Build(bool enabled = true)
    {
        var github = new FakeGitHub();
        var audit = new FakeAudit();
        var clock = Given.Clock();

        var services = new ServiceCollection();
        services.AddSingleton<IGitHubClient>(github);
        services.AddSingleton<IAuditRepository>(audit);

        var options = new GitHubOptions
        {
            Enabled = true,
            Token = "t",
            BotLogin = "hephaisto-bot",
            Repositories = ["acme/api"],
            Planner = new GitHubPlannerOptions { Enabled = enabled, Owner = "acme", Number = 3, RequiredFields = ["Priority"], Assignees = ["flo"] },
        };

        var planner = new PlannerService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions<GitHubOptions>(options),
            clock,
            NullLogger<PlannerService>.Instance);

        return (planner, github, clock, audit);
    }

    private static Board TheBoard() => new(
        "PVT_1",
        "Platform planning",
        "https://github.com/orgs/acme/projects/3",
        [
            new BoardField("F_status", "Status", [new("s1", "Inbox"), new("s2", "In review")]),
            new BoardField("F_prio", "Priority", [new("p0", "P0"), new("p1", "P1")]),
        ],
        [
            Item("PVTI_431", "acme/api", 431, priority: "P0", pulls: [new("acme/api", 433, "https://github.com/acme/api/pull/433", BoardPullState.Merged, Given.Now.AddDays(-2))]),
            Item("PVTI_240", "acme/web", 240, priority: null, labels: [new("today", Given.Now.AddDays(-2))]),
            Item("PVTI_57", "acme/scraper", 57, priority: "P1", labels: [new("not today", Given.Now.AddDays(-1))]),
        ]);

    private static BoardItem Item(string id, string repository, int number, string? priority, BoardLabel[]? labels = null, BoardPullRequest[]? pulls = null)
    {
        var fields = new Dictionary<string, string> { ["Status"] = "Inbox" };

        if (priority is not null)
        {
            fields["Priority"] = priority;
        }

        return new BoardItem(
            id, $"I_{number}", repository, number, $"Issue {number}", $"https://github.com/{repository}/issues/{number}", "Task", "reporter",
            true, Given.Now.AddDays(-9), Given.Now.AddDays(-3), null, null, 0, 0, fields, labels ?? [], [], pulls ?? []);
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeAudit : IAuditRepository
    {
        public List<AuditEvent> Rows { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct)
        {
            Rows.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken ct)
        {
            Rows.AddRange(auditEvents);
            return Task.CompletedTask;
        }

        public void Enlist(AuditEvent auditEvent) => Rows.Add(auditEvent);

        public Task<IReadOnlyList<AuditEvent>> GetForIncidentAsync(Guid incidentId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class FakeGitHub : IGitHubClient
    {
        public List<string> Calls { get; } = [];

        public GitHubResult<Board>? FailProject { get; set; }

        public GitHubResult<bool>? FailWrite { get; set; }

        public GitHubResult<bool>? FailUnlabel { get; set; }

        public bool LeaveOut { get; set; }

        public Action? OnWrite { get; set; }

        private Task<GitHubResult<bool>> Write(string call, bool value = true)
        {
            OnWrite?.Invoke();
            Calls.Add(call);

            return Task.FromResult(FailWrite ?? new GitHubResult<bool>(GitHubOutcome.Ok, value));
        }

        public Task<GitHubResult<Board>> GetProjectAsync(string owner, int number, CancellationToken ct)
        {
            Calls.Add($"project {owner}/{number}");

            return Task.FromResult(FailProject ?? new GitHubResult<Board>(GitHubOutcome.Ok, TheBoard()));
        }

        public Task<GitHubResult<bool>> SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct) =>
            Write($"field {projectId} {itemId} {fieldId} {optionId}");

        public Task<GitHubResult<bool>> CloseIssueAsync(string issueId, bool notPlanned, CancellationToken ct) =>
            Write($"close {issueId} {(notPlanned ? "NOT_PLANNED" : "COMPLETED")}");

        public Task<GitHubResult<bool>> AddLabelAsync(string repository, int number, string label, CancellationToken ct) =>
            Write($"label {repository}#{number} {label}");

        public Task<GitHubResult<bool>> RemoveLabelAsync(string repository, int number, string label, CancellationToken ct)
        {
            if (FailUnlabel is { } failing)
            {
                Calls.Add($"unlabel {repository}#{number} {label}");
                return Task.FromResult(failing);
            }

            return Write($"unlabel {repository}#{number} {label}");
        }

        public Task<GitHubResult<bool>> AddAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            Write($"assign {repository}#{number} {login}", value: !LeaveOut);

        public Task<GitHubResult<bool>> RemoveAssigneeAsync(string repository, int number, string login, CancellationToken ct) =>
            Write($"unassign {repository}#{number} {login}");

        public Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<IReadOnlyList<GitHubReaction>>> ListCommentReactionsAsync(string repository, long commentId, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubReaction>> AddCommentReactionAsync(string repository, long commentId, string content, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string repository, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubResult<IReadOnlyList<GitHubAssignment>>> ListAssignmentsAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
