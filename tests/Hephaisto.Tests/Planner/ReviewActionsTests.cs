using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Planner;
using Hephaisto.Agent.Planner.Review;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// A person's clicks on the weekly review (#312): what each sends to GitHub - through the
/// planner, and nothing else - what it records, and that an archived repository is never tried.
/// </summary>
public sealed class ReviewActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Run = Guid.CreateVersion7();

    // --- agree ---------------------------------------------------------------------------------

    [Fact]
    public async Task Agreeing_with_a_bug_of_the_board_sets_its_suggested_priority_there_and_writes_nothing_else()
    {
        var (actions, store, github, audit) = Build(Verdict("acme/web", 240, "bug", priority: "P1"));

        var result = await actions.AgreeAsync("acme/web", 240, "flo", Ct);

        result.Outcome.Should().Be(ReviewActionOutcome.Done, result.Message);
        result.Message.Should().StartWith("acme/web#240:").And.Contain("Priority set to P1");
        Writes(github).Should().Equal("field PVT_1 PVTI_240 F_prio p1");

        store.Verdict("acme/web#240").Should().BeEquivalentTo(new
        {
            DecidedBy = "flo", DecisionGroup = "bug", DecisionAgreed = true, DecisionUnsure = false,
        });

        // The click's own row first - no audit, no action - then the planner's for the field.
        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditReview, PlannerService.AuditField);
        audit.Rows[0].Actor.Should().Be("flo");
        audit.Rows[0].Summary.Should().Be("acme/web#240: agreed: a real bug");
    }

    [Fact]
    public async Task Agreeing_with_a_bug_whose_priority_is_already_there_or_that_is_not_on_the_board_writes_nothing()
    {
        var (actions, store, github, _) = Build(
            Verdict("acme/scraper", 57, "bug", priority: "P1"),
            Verdict("acme/web", 118, "bug", priority: "P1", onBoard: false),
            Verdict("acme/api", 205, "feature", priority: "P2", onBoard: false));

        (await actions.AgreeAsync("acme/scraper", 57, "flo", Ct)).Message.Should().Contain("Priority is P1 already");
        (await actions.AgreeAsync("acme/web", 118, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Done);
        (await actions.AgreeAsync("acme/api", 205, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Done);

        Writes(github).Should().BeEmpty();
        store.Rows.Should().OnlyContain(r => r.DecidedBy == "flo" && r.DecisionAgreed);
    }

    [Fact]
    public async Task A_priority_the_board_refuses_leaves_the_verdict_undecided()
    {
        // The board's Priority knows P0 and P1, not P2.
        var (actions, store, github, _) = Build(Verdict("acme/web", 240, "bug", priority: "P2"));

        var result = await actions.AgreeAsync("acme/web", 240, "flo", Ct);

        result.Outcome.Should().Be(ReviewActionOutcome.Conflict);
        result.Message.Should().Contain("P2 was not set");
        Writes(github).Should().BeEmpty();
        store.Verdict("acme/web#240").DecidedBy.Should().BeNull();
    }

    [Fact]
    public async Task There_is_nothing_to_agree_with_when_claude_gave_no_verdict()
    {
        var (actions, store, _, _) = Build(Verdict("acme/web", 240, ReviewVerdicts.Unsure, confidence: "low"));

        (await actions.AgreeAsync("acme/web", 240, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Conflict);
        store.Verdict("acme/web#240").DecidedBy.Should().BeNull();
    }

    // --- move and unsure ---------------------------------------------------------------------

    [Fact]
    public async Task Moving_is_the_persons_word_and_a_move_to_close_needs_a_reason()
    {
        var (actions, store, github, audit) = Build(
            Verdict("acme/planning", 30, "bug", confidence: "low", priority: "P2"),
            Verdict("acme/api", 205, "feature", onBoard: false));

        var moved = await actions.MoveAsync("acme/planning", 30, "bug", null, "flo", Ct);
        moved.Outcome.Should().Be(ReviewActionOutcome.Done);
        moved.Message.Should().Be("acme/planning#30: moved to a real bug");
        ReviewVerdicts.Tab(store.Verdict("acme/planning#30")).Should().Be("bug");
        store.Verdict("acme/planning#30").DecisionAgreed.Should().BeFalse("a move is not agreeing with Claude, even to Claude's group");

        (await actions.MoveAsync("acme/api", 205, "close", null, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Conflict);
        (await actions.MoveAsync("acme/api", 205, "unsure", null, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Conflict, "unsure is a mark, not a group");
        store.Verdict("acme/api#205").DecidedBy.Should().BeNull();

        (await actions.MoveAsync("acme/api", 205, "close", "stale", "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Done);
        store.Verdict("acme/api#205").Should().BeEquivalentTo(new { DecisionGroup = "close", DecisionCloseReason = "stale", DecisionAgreed = false });

        Writes(github).Should().BeEmpty("a move writes nothing to GitHub");
        audit.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Type == PlannerService.AuditReview);
    }

    [Fact]
    public async Task Unsure_puts_the_issue_under_unsure_whatever_claude_said()
    {
        var (actions, store, _, _) = Build(Verdict("acme/api", 431, "close", closeReason: "done"));

        (await actions.UnsureAsync("acme/api", 431, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Done);

        ReviewVerdicts.Tab(store.Verdict("acme/api#431")).Should().Be("unsure");
    }

    [Fact]
    public async Task A_models_name_decides_nothing_and_an_issue_without_a_verdict_is_not_found()
    {
        var (actions, store, github, audit) = Build(Verdict("acme/api", 431, "close", closeReason: "done"));

        (await actions.AgreeAsync("acme/api", 431, "gemini", Ct)).Outcome.Should().Be(ReviewActionOutcome.Forbidden);
        (await actions.CloseAgreedAsync("model", Ct)).Outcome.Should().Be(ReviewActionOutcome.Forbidden);
        (await actions.AgreeAsync("acme/api", 999, "flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.NotFound);

        store.Verdict("acme/api#431").DecidedBy.Should().BeNull();
        Writes(github).Should().BeEmpty();
        audit.Rows.Should().BeEmpty();
    }

    // --- close ---------------------------------------------------------------------------------

    [Fact]
    public async Task Close_closes_what_was_decided_with_its_reason_and_never_tries_an_archived_repository()
    {
        var (actions, store, github, audit) = Build(
            Verdict("acme/api", 431, "close", closeReason: "done", reason: "Its pull request @octocat merged was acme/api#433.", evidence: ["PR acme/api#433 merged"]),
            Verdict("acme/web", 160, "close", closeReason: "done", onBoard: false),
            Verdict("acme/legacy-ttd", 1, "close", closeReason: "obsolete", onBoard: false, archived: true),
            Verdict("acme/infra", 44, "close", closeReason: "done"),
            Verdict("acme/api", 205, "feature", onBoard: false));

        await actions.AgreeAsync("acme/api", 431, "flo", Ct);
        await actions.AgreeAsync("acme/web", 160, "flo", Ct);
        await actions.AgreeAsync("acme/legacy-ttd", 1, "flo", Ct);
        await actions.MoveAsync("acme/api", 205, "close", "stale", "flo", Ct);
        audit.Rows.Clear();

        var result = await actions.CloseAgreedAsync("flo", Ct);

        result.Outcome.Should().Be(ReviewActionOutcome.Done, result.Message);
        result.Closed.Should().Equal("acme/api#205", "acme/api#431", "acme/web#160");
        result.Archived.Should().Equal("acme/legacy-ttd#1");
        result.Message.Should().StartWith("Closed 3 issues").And.Contain("archived");

        // One comment, then the close as completed, per issue - by node id, so an issue that is not
        // on the board closes as well - and nothing on the archived one or the undecided one.
        Writes(github).Should().Equal(
            "comment acme/api#205", "close I_205 COMPLETED",
            "comment acme/api#431", "close I_431 COMPLETED",
            "comment acme/web#160", "close I_160 COMPLETED");

        var comment = github.Comments.Single(c => c.Number == 431).Comment.Body;
        comment.Should().StartWith("Closed after Hephaisto's weekly review: **already done**.")
            .And.Contain("Its pull request")
            .And.EndWith("<sub>Written by flo in Hephaisto's planner.</sub>");
        comment.Should().NotContain("@octocat", "a model's words mention nobody on the agent's account")
            .And.NotContain("acme/api#433", "nor reference another issue")
            .And.NotContain("hephaisto:planner-comment", "the reason is Claude's, so no replan may take it for the person's");
        github.Comments.Single(c => c.Number == 205).Comment.Body.Should().Contain("**stale**").And.Contain("A person chose the reason");

        store.Rows.Where(r => r.GoneAt is not null).Select(r => r.Reference).Should().BeEquivalentTo("acme/api#205", "acme/api#431", "acme/web#160");
        store.Verdict("acme/legacy-ttd#1").Should().BeEquivalentTo(new { GoneAt = (DateTimeOffset?)null, DecidedBy = "flo", DecisionAgreed = true });

        audit.Rows.Select(r => r.Type).Should().Equal(PlannerService.AuditClose, PlannerService.AuditClose, PlannerService.AuditClose);
    }

    [Fact]
    public async Task A_close_github_refuses_keeps_the_issue_in_the_review_and_says_the_comment_is_there()
    {
        var (actions, store, github, audit) = Build(Verdict("acme/api", 431, "close", closeReason: "done"));
        await actions.AgreeAsync("acme/api", 431, "flo", Ct);

        github.FailWrite = new(GitHubOutcome.Unauthorized, false, "HTTP 403: Repository was archived so is read-only.");
        var result = await actions.CloseAgreedAsync("flo", Ct);

        result.Outcome.Should().Be(ReviewActionOutcome.Refused);
        result.Closed.Should().BeEmpty();
        result.Message.Should().Contain("the comment is on the issue, but GitHub would not close it");
        store.Verdict("acme/api#431").GoneAt.Should().BeNull();
        audit.Rows.Select(r => r.Type).Should().EndWith([PlannerService.AuditClose, PlannerService.AuditRefused]);
    }

    [Fact]
    public async Task Nothing_closes_while_a_run_is_under_way_or_when_nothing_was_decided()
    {
        var (actions, store, github, _) = Build(Verdict("acme/api", 431, "close", closeReason: "done"));

        (await actions.CloseAgreedAsync("flo", Ct)).Outcome.Should().Be(ReviewActionOutcome.Conflict);

        await actions.AgreeAsync("acme/api", 431, "flo", Ct);
        await store.TryStartAsync(new PlannerReviewRun { State = PlannerReviewRunState.Running, StartedAt = Given.Now, RequestedBy = "flo" }, Ct);

        var during = await actions.CloseAgreedAsync("flo", Ct);
        during.Outcome.Should().Be(ReviewActionOutcome.Conflict);
        during.Message.Should().Contain("under way");
        Writes(github).Should().BeEmpty();
    }

    // --- helpers -------------------------------------------------------------------------------

    private static (ReviewActions Actions, InMemoryReviewStore Store, PlannerServiceTests.FakeGitHub GitHub, PlannerServiceTests.FakeAudit Audit) Build(
        params PlannerReviewVerdict[] verdicts)
    {
        var (planner, github, clock, audit) = PlannerServiceTests.Build();
        var store = new InMemoryReviewStore();
        store.Rows.AddRange(verdicts);

        return (new ReviewActions(planner, store, clock, NullLogger<ReviewActions>.Instance), store, github, audit);
    }

    /// <summary>What was sent that changes something: the board read is not a write.</summary>
    private static IEnumerable<string> Writes(PlannerServiceTests.FakeGitHub github) =>
        github.Calls.Where(c => !c.StartsWith("project ", StringComparison.Ordinal));

    internal static PlannerReviewVerdict Verdict(
        string repository, int number, string group, string? closeReason = null, string confidence = "high", string? priority = null,
        bool onBoard = true, bool archived = false, string? reason = null, string[]? evidence = null) => new()
    {
        Repository = repository,
        Number = number,
        Title = $"Issue {number}",
        Url = $"https://github.com/{repository}/issues/{number}",
        NodeId = $"I_{number}",
        OnBoard = onBoard,
        Archived = archived,
        Group = group,
        CloseReason = closeReason,
        Confidence = confidence,
        Summary = $"Summary of {number}",
        Reason = reason ?? $"Reason for {number}",
        Evidence = [.. (evidence ?? []).Select(e => new PlannerReviewEvidence("note", e, null))],
        Priority = priority,
        IssueUpdatedAt = Given.Now.AddDays(-1),
        ReviewedAt = Given.Now,
        RunId = Run,
    };
}
