using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Planner.Review;
using Hephaisto.Core.Planning;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// The weekly review's rules (#312), each on its own: the schedule and its slots, which issues a
/// run asks about, the budget, what a verdict becomes, and what a person's decision survives.
/// </summary>
public sealed class ReviewRulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    // --- the schedule ------------------------------------------------------------------------

    [Theory]
    // A Monday at 06:00 in Berlin is 05:00 UTC in winter and 04:00 in summer.
    [InlineData("2026-11-09T05:00:00Z", "2026-11-16T05:00:00Z")]
    [InlineData("2026-11-09T04:59:00Z", "2026-11-09T05:00:00Z")]
    [InlineData("2026-11-11T12:00:00Z", "2026-11-16T05:00:00Z")]
    [InlineData("2026-03-26T12:00:00Z", "2026-03-30T04:00:00Z")]
    public void Monday_at_six_in_berlin_is_named_in_its_own_time(string after, string next)
    {
        CronSchedule.Parse("0 6 * * 1").Next(At(after), Berlin).Should().Be(At(next));
    }

    [Fact]
    public void A_time_the_clock_jumps_over_does_not_happen_and_one_it_passes_twice_is_the_first()
    {
        // 2026-03-29: 02:00 -> 03:00 in Berlin. 2026-10-25: 03:00 -> 02:00.
        var daily = CronSchedule.Parse("30 2 * * *");

        daily.Next(At("2026-03-28T12:00:00Z"), Berlin).Should().Be(At("2026-03-30T00:30:00Z"), "02:30 on the 29th never happens");
        daily.Next(At("2026-10-24T12:00:00Z"), Berlin).Should().Be(At("2026-10-25T00:30:00Z"), "the first 02:30, still summer time");
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-11-09T05:07:00Z", "2026-11-09T05:15:00Z")]
    [InlineData("0 9-17/4 * * 1-5", "2026-11-09T12:00:00Z", "2026-11-09T16:00:00Z")]
    [InlineData("0 0 1 * *", "2026-11-09T12:00:00Z", "2026-11-30T23:00:00Z")]
    [InlineData("0 6 * * 0,7", "2026-11-09T12:00:00Z", "2026-11-15T05:00:00Z")]
    // Both day fields restricted: either matches, as in cron.
    [InlineData("0 6 13 * 1", "2026-11-10T12:00:00Z", "2026-11-13T05:00:00Z")]
    public void Lists_ranges_and_steps_read_as_cron_reads_them(string expression, string after, string next)
    {
        CronSchedule.Parse(expression).Next(At(after), Berlin).Should().Be(At(next));
    }

    [Theory]
    [InlineData("0 6 * *")]
    [InlineData("0 6 * * MON")]
    [InlineData("60 6 * * 1")]
    [InlineData("0 6 * * 8")]
    [InlineData("0 6 */0 * 1")]
    [InlineData("")]
    public void A_schedule_that_is_not_five_cron_fields_is_refused_with_a_reason(string expression)
    {
        CronSchedule.TryParse(expression, out var schedule, out var why).Should().BeFalse();
        schedule.Should().BeNull();
        why.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_slot_is_due_for_an_hour_after_its_moment_and_not_before_it()
    {
        var review = new GitHubPlannerReviewOptions { Enabled = true };

        ReviewScheduler.Due(review, At("2026-11-09T04:59:00Z")).Should().BeNull();
        ReviewScheduler.Due(review, At("2026-11-09T05:00:00Z")).Should().Be(At("2026-11-09T05:00:00Z"));
        ReviewScheduler.Due(review, At("2026-11-09T05:59:00Z")).Should().Be(At("2026-11-09T05:00:00Z"), "a restart across the slot still runs it");
        ReviewScheduler.Due(review, At("2026-11-09T06:01:00Z")).Should().BeNull("a slot missed by more than an hour is skipped");

        ReviewScheduler.Due(new GitHubPlannerReviewOptions { Enabled = false }, At("2026-11-09T05:00:00Z")).Should().BeNull();
        ReviewScheduler.Next(new GitHubPlannerReviewOptions { Enabled = false }, At("2026-11-09T05:00:00Z")).Should().BeNull();
    }

    [Fact]
    public async Task The_scheduler_starts_a_due_slot_once_whatever_restarts()
    {
        var store = new InMemoryReviewStore();
        var clock = Given.Clock();
        clock.UtcNow = At("2026-11-09T05:00:30Z");

        var (runner, scheduler) = Scheduler(store, clock);

        var started = await scheduler.PassAsync(Ct);
        started!.Outcome.Should().Be(ReviewStartOutcome.Started);
        await runner.WaitAsync();

        store.Runs.Single().Slot.Should().Be(At("2026-11-09T05:00:00Z"));
        store.Runs.Single().Trigger.Should().Be(PlannerReviewTrigger.Schedule);
        store.Runs.Single().RequestedBy.Should().Be("schedule");

        // The same process a minute later, and a new one after a restart in the slot's hour.
        clock.UtcNow = At("2026-11-09T05:01:30Z");
        (await scheduler.PassAsync(Ct)).Should().BeNull();

        var (_, restarted) = Scheduler(store, clock);
        (await restarted.PassAsync(Ct)).Should().BeNull("the slot's run is a row; a restart finds it");

        store.Runs.Should().ContainSingle();
    }

    private static (ReviewRunner Runner, ReviewScheduler Scheduler) Scheduler(InMemoryReviewStore store, Given.FixedClock clock)
    {
        var options = new PlannerServiceTests.StaticOptions<GitHubOptions>(new GitHubOptions
        {
            Enabled = true,
            Token = "t",
            Repositories = ["acme/api"],
            Planner = new GitHubPlannerOptions { Enabled = true, Owner = "acme", Number = 3, Review = new GitHubPlannerReviewOptions { Enabled = true } },
        });

        var runner = new ReviewRunner(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            options,
            new PlannerServiceTests.StaticOptions<Hephaisto.Agent.CodeFix.CodeFixOptions>(new Hephaisto.Agent.CodeFix.CodeFixOptions { Image = "i", ContextRepositoryUrl = "c" }),
            new PlannerServiceTests.StaticOptions<Hephaisto.Core.Notifications.NotificationOptions>(new Hephaisto.Core.Notifications.NotificationOptions()),
            new ReviewLauncher(),
            store,
            new ReviewBoardStub(null),
            clock,
            NullLogger<ReviewRunner>.Instance);

        return (runner, new ReviewScheduler(runner, store, options, clock, NullLogger<ReviewScheduler>.Instance));
    }

    // --- which issues a run asks about ---------------------------------------------------------

    [Fact]
    public void An_issue_is_asked_about_when_it_is_new_changed_or_its_verdict_is_old_and_otherwise_kept()
    {
        var now = Given.Now;
        var recheck = TimeSpan.FromDays(28);
        var open = new ReviewOpen("acme/api#1", "acme/api", 1, now.AddDays(-3));

        ReviewSelection.Why(open, null, now, recheck).Should().Be(ReviewWhy.New);
        ReviewSelection.Why(open, new("acme/api#1", now.AddDays(-3), now.AddDays(-1), Gone: true), now, recheck).Should().Be(ReviewWhy.New, "closed and opened again");
        ReviewSelection.Why(open, new("acme/api#1", now.AddDays(-4), now.AddDays(-1), Gone: false), now, recheck).Should().Be(ReviewWhy.Changed);
        ReviewSelection.Why(open, new("acme/api#1", now.AddDays(-2), now.AddDays(-1), Gone: false), now, recheck)
            .Should().Be(ReviewWhy.Changed, "an updated_at that went back is not the issue the verdict was about either: a stand-in seeded afresh");
        ReviewSelection.Why(open with { UpdatedAt = now.AddDays(-3).AddMilliseconds(400) }, new("acme/api#1", now.AddDays(-3), now.AddDays(-1), Gone: false), now, recheck)
            .Should().Be(ReviewWhy.Kept, "GitHub's times are whole seconds");
        ReviewSelection.Why(open, new("acme/api#1", now.AddDays(-3), now.AddDays(-28), Gone: false), now, recheck).Should().Be(ReviewWhy.Recheck);
        ReviewSelection.Why(open, new("acme/api#1", now.AddDays(-3), now.AddDays(-27), Gone: false), now, recheck).Should().Be(ReviewWhy.Kept);
    }

    [Fact]
    public void The_asked_ones_are_batched_by_repository_and_never_more_than_a_batch_holds()
    {
        var now = Given.Now;
        var open = new[]
        {
            new ReviewOpen("acme/web#3", "acme/web", 3, now),
            new ReviewOpen("acme/api#9", "acme/api", 9, now),
            new ReviewOpen("acme/api#2", "acme/api", 2, now),
            new ReviewOpen("acme/web#1", "acme/web", 1, now.AddDays(-9)),
            new ReviewOpen("acme/api#2", "acme/api", 2, now),
        };
        var known = new[] { new ReviewKnown("acme/web#1", now.AddDays(-9), now.AddDays(-1), false) };

        var batches = ReviewSelection.Batches(ReviewSelection.Select(open, known, now, TimeSpan.FromDays(28)), 2);

        batches.Select(b => string.Join(",", b.Select(i => i.Reference))).Should().Equal("acme/api#2,acme/api#9", "acme/web#3");
    }

    [Fact]
    public void A_job_gets_a_share_of_what_is_left_and_none_starts_when_too_little_is()
    {
        var budget = new ReviewBudget(10m, concurrency: 2);

        budget.Reserve().Should().Be(5m);
        budget.Reserve().Should().Be(5m);
        budget.Reserve().Should().BeNull("both shares are out");

        budget.Settle(5m, 1.25m);
        budget.Reserve().Should().Be(3.75m, "what is left, not more");

        budget.Settle(5m, 6m);
        budget.Settle(3.75m, 2.73m);
        budget.Spent.Should().Be(9.98m, "a Job that went over its share is spent as it went");
        budget.Reserve().Should().BeNull();
    }

    // --- verdicts --------------------------------------------------------------------------------

    [Fact]
    public void A_person_decides_where_a_verdict_is_shown_and_otherwise_low_confidence_is_unsure()
    {
        var v = new PlannerReviewVerdict { Group = "bug", Confidence = "low" };
        ReviewVerdicts.Tab(v).Should().Be("unsure", "the scripted low-confidence bug of the suite (acme/planning#30)");

        v.Confidence = "high";
        ReviewVerdicts.Tab(v).Should().Be("bug");

        ReviewVerdicts.Decide(v, new ReviewDecision("flo", Given.Now, "feature", null, Agreed: false, Unsure: false));
        ReviewVerdicts.Tab(v).Should().Be("feature");

        ReviewVerdicts.Decide(v, new ReviewDecision("flo", Given.Now, "feature", null, Agreed: false, Unsure: true));
        ReviewVerdicts.Tab(v).Should().Be("unsure");

        ReviewVerdicts.Tab(new PlannerReviewVerdict { Group = ReviewVerdicts.Unsure, Confidence = "low" }).Should().Be("unsure");
    }

    [Fact]
    public void A_new_verdict_keeps_the_decision_unless_the_issue_changed()
    {
        var at = Given.Now;
        PlannerReviewVerdict Kept() => new()
        {
            Repository = "acme/api", Number = 1, Group = "bug", Confidence = "high", IssueUpdatedAt = at,
            DecidedBy = "flo", DecidedAt = at, DecisionGroup = "bug", DecisionAgreed = true,
        };

        var recheck = Kept();
        ReviewVerdicts.Merge(recheck, new PlannerReviewVerdict { Repository = "acme/api", Number = 1, Group = "feature", Confidence = "medium", IssueUpdatedAt = at.AddMilliseconds(400) });
        recheck.Group.Should().Be("feature");
        recheck.DecidedBy.Should().Be("flo");

        var changed = Kept();
        ReviewVerdicts.Merge(changed, new PlannerReviewVerdict { Repository = "acme/api", Number = 1, Group = "bug", Confidence = "high", IssueUpdatedAt = at.AddSeconds(5) });
        changed.DecidedBy.Should().BeNull();
        changed.DecisionAgreed.Should().BeFalse();

        var reopened = Kept();
        reopened.GoneAt = at;
        ReviewVerdicts.Merge(reopened, new PlannerReviewVerdict { Repository = "acme/api", Number = 1, Group = "bug", Confidence = "high", IssueUpdatedAt = at });
        reopened.GoneAt.Should().BeNull();
        reopened.DecidedBy.Should().BeNull("closed and opened again is a change");
    }

    [Fact]
    public void A_kept_verdict_whose_issue_is_not_listed_is_gone_only_when_the_listing_was_complete()
    {
        var rows = new List<PlannerReviewVerdict>
        {
            new() { Repository = "acme/api", Number = 1, Title = "old" },
            new() { Repository = "acme/api", Number = 2 },
        };
        var open = new[] { new ReviewOpenFacts("acme/api", 1, "new title", "u", OnBoard: true, Archived: null) };

        ReviewVerdicts.Sync(rows, open, complete: false, Given.Now);
        rows[1].GoneAt.Should().BeNull();
        rows[0].Title.Should().Be("new title");
        rows[0].OnBoard.Should().BeTrue();

        ReviewVerdicts.Sync(rows, open, complete: true, Given.Now);
        rows[1].GoneAt.Should().Be(Given.Now);
        rows[0].GoneAt.Should().BeNull();
    }

    [Fact]
    public void Evidence_is_a_fact_each_and_says_whether_the_gathered_facts_bear_it_out()
    {
        var facts = new ReviewIssue(
            "acme/api", 431, "u", "t", null, "a", null, [], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", Archived: false, Board: null,
            [new ReviewCrossReference("pull_request", "acme/api#433", "fix", "merged", null, null), new ReviewCrossReference("pull_request", "acme/api#440", "wip", "open", null, null)],
            [new ReviewCommit("a6ac13278e5f0d6c9b1e2f3a4b5c6d7e8f901234", "acme/infra", null, null)],
            [new ReviewSubIssue("acme/planning#10", "x", "closed", "completed")],
            [],
            0);

        ReviewVerdicts.Classify("PR acme/api#433 merged", facts).Should().Be(new PlannerReviewEvidence("pull_request", "PR acme/api#433 merged", true));
        ReviewVerdicts.Classify("PR acme/api#440 merged", facts).Ok.Should().BeFalse("the timeline shows it open");
        ReviewVerdicts.Classify("PR acme/api#999 merged", facts).Ok.Should().BeNull("nothing gathered speaks of it");
        ReviewVerdicts.Classify("commit a6ac13278 on main", facts).Should().Be(new PlannerReviewEvidence("commit", "commit a6ac13278 on main", true));
        ReviewVerdicts.Classify("sub-issue acme/planning#10 closed", facts).Kind.Should().Be("sub_issue");
        ReviewVerdicts.Classify("sub-issue acme/planning#10 closed", facts).Ok.Should().BeTrue();
        ReviewVerdicts.Classify("repository archived", facts).Should().Be(new PlannerReviewEvidence("repository", "repository archived", false));
        ReviewVerdicts.Classify("no pull request", facts).Should().Be(new PlannerReviewEvidence("absence", "no pull request", false));
        ReviewVerdicts.Classify("the reporter says it still happens", facts).Should().Be(new PlannerReviewEvidence("note", "the reporter says it still happens", null));
    }

    [Fact]
    public void A_close_without_its_reason_or_an_unknown_group_is_unsure_and_text_is_cut_to_the_contracts_caps()
    {
        var batch = new[]
        {
            new ReviewIssue("acme/api", 1, "u", "t", null, "a", null, [], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", false, null, [], [], [], [], 0),
            new ReviewIssue("acme/api", 2, "u", "t", null, "a", null, [], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", false, null, [], [], [], [], 0),
            new ReviewIssue("acme/api", 3, "u", "t", null, "a", null, [], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", false, null, [], [], [], [], 0),
        };
        var context = batch.ToDictionary(i => $"{i.Repository}#{i.Number}", _ => new ReviewIssueContext("I", Given.Now), StringComparer.OrdinalIgnoreCase);

        var verdicts = ReviewVerdicts.FromResult(
            batch,
            [
                new ReviewVerdictResult("acme/api#1", "close", null, null, "high", "s", "r", [], null),
                new ReviewVerdictResult("acme/api#2", "wontfix", null, null, "high", "s", "r", [], null),
                new ReviewVerdictResult("ACME/api#3", "feature", null, "not a reference", "medium", new string('s', 400), "r", [.. Enumerable.Repeat("e", 12)], "P2"),
            ],
            context,
            Guid.NewGuid(),
            Given.Now);

        verdicts.Select(v => v.Group).Should().Equal("unsure", "unsure", "feature");
        verdicts[0].Confidence.Should().Be("low");
        verdicts[2].Summary.Should().HaveLength(300);
        verdicts[2].Evidence.Should().HaveCount(8);
        verdicts[2].Related.Should().BeNull();
        verdicts[2].Priority.Should().Be("P2");
    }

    // --- configuration ---------------------------------------------------------------------------

    [Fact]
    public void A_review_that_cannot_run_as_configured_is_refused_one_sentence_each()
    {
        new GitHubPlannerReviewOptions { Enabled = false, Schedule = "never" }.Problems().Should().BeEmpty("an option nobody enabled is not a fault");
        new GitHubPlannerReviewOptions { Enabled = true }.Problems().Should().BeEmpty("the defaults run");

        new GitHubPlannerReviewOptions
        {
            Enabled = true,
            Schedule = "weekly",
            TimeZone = "Mars/Olympus",
            IssuesPerJob = 41,
            RecheckAfterDays = 0,
            MaxCostUsd = 0,
            MaxConcurrentJobs = 0,
            Deadline = TimeSpan.FromSeconds(30),
            MaxTurns = 0,
        }.Problems().Should().HaveCount(8);
    }
}
