using Microsoft.Extensions.DependencyInjection;

using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Planner.Review;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The weekly review's rows against a real database (#312). The rules are pinned in memory
/// (ReviewRunnerTests, ReviewRulesTests); what needs Postgres is the unique slot, the one verdict
/// per issue, the jsonb columns coming back as they went in, and a run left going by a process
/// that is gone.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlannerReviewStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 5, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slot_runs_once_and_only_one_run_goes_at_a_time()
    {
        await pg.ResetAsync();
        var store = Store();

        var first = new PlannerReviewRun { Trigger = PlannerReviewTrigger.Schedule, State = PlannerReviewRunState.Running, Slot = Now, RequestedBy = "schedule", StartedAt = Now };
        (await store.TryStartAsync(first, Ct)).Should().BeTrue();
        (await store.TryStartAsync(new PlannerReviewRun { State = PlannerReviewRunState.Running, RequestedBy = "flo", StartedAt = Now }, Ct)).Should().BeFalse("one is running");

        first.State = PlannerReviewRunState.Finished;
        first.Reviewed = ["acme/api#1"];
        await store.UpdateRunAsync(first, Ct);

        (await store.TryStartAsync(new PlannerReviewRun { Trigger = PlannerReviewTrigger.Schedule, State = PlannerReviewRunState.Running, Slot = Now, RequestedBy = "schedule", StartedAt = Now.AddSeconds(30) }, Ct))
            .Should().BeFalse("the slot is unique in Postgres: a restart within its minute does not run it twice");
        (await store.SlotTakenAsync(Now, Ct)).Should().BeTrue();

        var latest = await store.LatestRunAsync(Ct);
        latest!.Id.Should().Be(first.Id);
        latest.Reviewed.Should().Equal("acme/api#1");

        var left = new PlannerReviewRun { State = PlannerReviewRunState.Running, RequestedBy = "flo", StartedAt = Now.AddHours(1) };
        (await store.TryStartAsync(left, Ct)).Should().BeTrue();
        await store.FailInterruptedAsync(Now.AddHours(2), Ct);
        (await store.LatestRunAsync(Ct))!.State.Should().Be(PlannerReviewRunState.Failed);
    }

    [Fact]
    public async Task A_verdict_replaces_its_issues_and_a_decision_survives_a_recheck_but_not_a_change()
    {
        await pg.ResetAsync();
        var store = Store();

        await store.SaveVerdictsAsync([Verdict(1, Now, "bug"), Verdict(2, Now, "close")], Ct);
        (await store.DecideAsync("acme/api", 1, new ReviewDecision("flo", Now, "bug", null, true, false), Ct)).Should().BeTrue();
        (await store.DecideAsync("acme/api", 2, new ReviewDecision("flo", Now, "feature", null, false, false), Ct)).Should().BeTrue();

        await store.SaveVerdictsAsync([Verdict(1, Now, "feature"), Verdict(2, Now.AddMinutes(5), "close")], Ct);

        var rows = (await store.VerdictsAsync(includeGone: false, Ct)).ToDictionary(v => v.Number);
        rows.Should().HaveCount(2);
        rows[1].Group.Should().Be("feature");
        rows[1].DecidedBy.Should().Be("flo");
        rows[2].DecidedBy.Should().BeNull();
        rows[1].Evidence.Should().Equal(new PlannerReviewEvidence("pull_request", "PR acme/api#9 merged", true));

        await store.SyncOpenAsync([new ReviewOpenFacts("acme/api", 1, "renamed", "u", OnBoard: true, Archived: true)], complete: true, Now, Ct);
        await store.MarkGoneAsync("acme/api", 1, Now, Ct);

        (await store.VerdictsAsync(includeGone: false, Ct)).Should().BeEmpty("one was not listed, the other a person closed");
        (await store.VerdictsAsync(includeGone: true, Ct)).Single(v => v.Number == 1).Title.Should().Be("renamed");
        (await store.DecideAsync("acme/api", 1, null, Ct)).Should().BeFalse("a gone verdict is not decided on");
    }

    private EfReviewStore Store()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => pg.CreateContext());

        return new EfReviewStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static PlannerReviewVerdict Verdict(int number, DateTimeOffset updated, string group) => new()
    {
        Repository = "acme/api",
        Number = number,
        Title = $"Issue {number}",
        Url = $"https://github.com/acme/api/issues/{number}",
        NodeId = $"I_{number}",
        Group = group,
        CloseReason = group == "close" ? "done" : null,
        Confidence = "high",
        Summary = "s",
        Reason = "r",
        Evidence = [new PlannerReviewEvidence("pull_request", "PR acme/api#9 merged", true)],
        IssueUpdatedAt = updated,
        ReviewedAt = Now,
        RunId = Guid.CreateVersion7(),
    };
}
