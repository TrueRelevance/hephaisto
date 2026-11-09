using System.Text.Json;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix;
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
/// The weekly review's run (#312), end to end against an organisation in memory: what it asks
/// about, how it batches and bounds its Jobs and its cost, what a batch that fails leaves - and
/// that it writes nothing to GitHub.
/// </summary>
public sealed class ReviewRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Then = Given.Now.AddDays(-2);

    [Fact]
    public async Task A_run_reviews_every_open_issue_on_the_board_and_off_it_and_writes_nothing_to_github()
    {
        var h = Harness.Build(issuesPerJob: 2);
        h.Org(("acme/api", 431), ("acme/api", 475), ("acme/web", 118), ("acme/web", 160), ("acme/legacy-ttd", 1));
        h.GitHub.Archived.Add("acme/legacy-ttd");

        var run = await h.RunAsync();

        run.State.Should().Be(PlannerReviewRunState.Finished, run.Problem);
        run.Trigger.Should().Be(PlannerReviewTrigger.Manual);
        run.Open.Should().Be(5);
        run.Reviewed.Should().BeEquivalentTo("acme/api#431", "acme/api#475", "acme/web#118", "acme/web#160", "acme/legacy-ttd#1");
        run.Kept.Should().Be(0);
        run.Jobs.Should().Be(3, "five issues in batches of two");
        run.CountBug.Should().Be(5);

        h.GitHub.Writes.Should().BeEmpty("the weekly run proposes and writes nothing");
        h.GitHub.Reads.Should().Contain("timeline acme/web#160", "an issue that is not on the board is read like one that is");
        h.GitHub.Reads.Should().Contain("repository acme/legacy-ttd");

        h.Store.Verdict("acme/api#431").OnBoard.Should().BeTrue();
        h.Store.Verdict("acme/web#160").OnBoard.Should().BeFalse();
        h.Store.Verdict("acme/legacy-ttd#1").Archived.Should().BeTrue();

        // A batch's repositories, each once; an archived one is never offered, and against a
        // GitHub that is not github.com no address is guessed.
        h.Launcher.Requests.Should().OnlyContain(r => r.Repositories.Count == 0);
        h.Launcher.Requests.SelectMany(r => r.Issues).Single(i => i.Number == 1).Archived.Should().BeTrue();
        h.Launcher.Requests.SelectMany(r => r.Issues).Single(i => i.Number == 431).Board!.Status.Should().Be("In review");
        h.Launcher.Requests.SelectMany(r => r.Issues).Single(i => i.Number == 160).Board.Should().BeNull();
        h.Launcher.Deleted.Should().HaveCount(3, "every Job is removed once it answered");
    }

    [Fact]
    public async Task The_next_run_asks_again_only_about_what_changed_and_a_persons_decision_sticks_on_the_rest()
    {
        var h = Harness.Build(issuesPerJob: 10);
        h.Org(("acme/api", 431), ("acme/api", 475), ("acme/web", 118));

        var first = await h.RunAsync();
        first.Reviewed.Should().HaveCount(3);

        await h.Store.DecideAsync("acme/api", 475, new ReviewDecision("flo", Given.Now, "feature", null, Agreed: false, Unsure: false), Ct);
        await h.Store.DecideAsync("acme/web", 118, new ReviewDecision("flo", Given.Now, "bug", null, Agreed: true, Unsure: false), Ct);

        h.Clock.UtcNow = Given.Now.AddHours(1);
        h.GitHub.Touch("acme/web#118", Given.Now.AddMinutes(30));
        h.GitHub.Writes.Clear();

        var second = await h.RunAsync();

        second.Reviewed.Should().Equal("acme/web#118");
        second.Kept.Should().Be(2);
        second.Jobs.Should().Be(1);
        h.GitHub.Writes.Should().BeEmpty();

        h.Store.Verdict("acme/web#118").RunId.Should().Be(second.Id);
        h.Store.Verdict("acme/web#118").DecidedBy.Should().BeNull("the issue changed since the decision: the new verdict is shown undecided");
        h.Store.Verdict("acme/api#475").RunId.Should().Be(first.Id);
        h.Store.Verdict("acme/api#475").DecisionGroup.Should().Be("feature", "an unchanged issue keeps what a person decided");
        second.CountFeature.Should().Be(1, "the counts are where the issues are shown, a person's decision included");
    }

    [Fact]
    public async Task An_old_verdict_is_asked_for_again_and_the_decision_on_it_stays()
    {
        var h = Harness.Build(issuesPerJob: 10, recheckAfterDays: 28);
        h.Org(("acme/api", 431), ("acme/api", 475));

        await h.RunAsync();
        await h.Store.DecideAsync("acme/api", 431, new ReviewDecision("flo", Given.Now, "bug", null, Agreed: true, Unsure: false), Ct);

        h.Clock.UtcNow = Given.Now.AddDays(29);

        var later = await h.RunAsync();

        later.Reviewed.Should().HaveCount(2, "four weeks without a change is a recheck, not a free pass");
        h.Store.Verdict("acme/api#431").DecidedBy.Should().Be("flo", "a recheck of an unchanged issue keeps the decision");
    }

    [Fact]
    public async Task A_closed_issue_drops_out_and_a_cut_off_listing_closes_nothing()
    {
        var h = Harness.Build(issuesPerJob: 10);
        h.Org(("acme/api", 431), ("acme/api", 475));
        await h.RunAsync();

        h.GitHub.Open.RemoveAll(i => i.Issue.Number == 431);
        h.GitHub.Total = 5;
        h.Clock.UtcNow = Given.Now.AddHours(1);

        var cut = await h.RunAsync();
        cut.Problem.Should().Contain("5 open issues");
        (await h.Store.VerdictsAsync(includeGone: false, Ct)).Select(v => v.Reference).Should().Contain("acme/api#431", "the search did not list every open issue");

        h.GitHub.Total = null;
        await h.RunAsync();

        var view = await h.Reader().ReadAsync(Ct);
        view.Verdicts.Select(v => $"{v.Repository}#{v.Number}").Should().Equal("acme/api#475");
        view.Pending.Should().Be(1);

        // Closed on the planner since its verdict: out at once. Closed on a board read from
        // before the verdict, which found it open: the older word, and it stays.
        var board = h.Board.Value!;
        var item = board.Items.Single(i => i.Number == 475);
        h.Board.Value = board with { Items = [item with { Open = false, ClosedAt = h.Clock.UtcNow.AddDays(-1) }] };
        (await h.Reader().ReadAsync(Ct)).Verdicts.Should().ContainSingle();

        h.Board.Value = board with { Items = [item with { Open = false, ClosedAt = h.Clock.UtcNow.AddMinutes(1) }] };
        (await h.Reader().ReadAsync(Ct)).Verdicts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_batch_leaves_its_issues_on_the_verdicts_they_had_and_the_run_says_so()
    {
        var h = Harness.Build(issuesPerJob: 1, concurrency: 1);
        h.Org(("acme/api", 1), ("acme/api", 2));

        await h.RunAsync();
        var before = h.Store.Verdict("acme/api#2");

        h.Clock.UtcNow = Given.Now.AddHours(1);
        h.GitHub.Touch("acme/api#1", Given.Now.AddMinutes(1));
        h.GitHub.Touch("acme/api#2", Given.Now.AddMinutes(1));
        h.Launcher.Answer = r => r.Issues[0].Number == 2
            ? ReviewLauncher.Result(r, "budget_exhausted", 1.5m, [])
            : ReviewLauncher.Everything("feature", "medium", "P2")(r);

        var run = await h.RunAsync();

        run.State.Should().Be(PlannerReviewRunState.Finished);
        run.Reviewed.Should().Equal("acme/api#1");
        run.Problem.Should().Contain("acme/api#2").And.Contain("budget_exhausted");
        run.CostUsd.Should().Be(1.5m);
        h.Store.Verdict("acme/api#2").RunId.Should().Be(before.RunId, "a failed batch changes no verdict");

        // ... and the next run asks about it again, because it is still changed since its verdict.
        h.Launcher.Answer = ReviewLauncher.Everything("bug", "high", "P1");
        (await h.RunAsync()).Reviewed.Should().Equal("acme/api#2");
    }

    [Fact]
    public async Task A_run_whose_every_batch_failed_is_failed()
    {
        var h = Harness.Build(issuesPerJob: 1);
        h.Org(("acme/api", 1), ("acme/api", 2));
        h.Launcher.Answer = _ => null;

        var run = await h.RunAsync();

        run.State.Should().Be(PlannerReviewRunState.Failed);
        run.Problem.Should().Contain("could not be read");
        h.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_issue_the_model_left_out_is_unsure_with_low_confidence_and_one_from_another_batch_is_dropped()
    {
        var h = Harness.Build(issuesPerJob: 10);
        h.Org(("acme/api", 1), ("acme/api", 2));
        h.Launcher.Answer = r => ReviewLauncher.Result(r, "reviewed", 0,
        [
            ReviewLauncher.Verdict("acme/api#1", "close", "high", priority: "P1", closeReason: "done"),
            ReviewLauncher.Verdict("acme/other#9", "bug", "high", "P0"),
        ]);

        await h.RunAsync();

        var closed = h.Store.Verdict("acme/api#1");
        closed.Group.Should().Be("close");
        closed.Priority.Should().BeNull("a close carries no priority");

        var missing = h.Store.Verdict("acme/api#2");
        missing.Group.Should().Be(ReviewVerdicts.Unsure);
        missing.Confidence.Should().Be("low");
        ReviewVerdicts.Tab(missing).Should().Be("unsure");

        h.Store.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task No_more_jobs_run_at_once_than_allowed()
    {
        var h = Harness.Build(issuesPerJob: 1, concurrency: 2);
        h.Org([.. Enumerable.Range(1, 7).Select(n => ("acme/api", n))]);

        var run = await h.RunAsync();

        run.Jobs.Should().Be(7);
        h.Launcher.MostAtOnce.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task The_runs_cost_cap_stops_the_batches_beyond_it()
    {
        var h = Harness.Build(issuesPerJob: 1, concurrency: 1, maxCostUsd: 2m);
        h.Org(("acme/api", 1), ("acme/api", 2), ("acme/api", 3));
        h.Launcher.Answer = ReviewLauncher.Everything("bug", "high", "P1", cost: 0.99m);

        var run = await h.RunAsync();

        run.Jobs.Should().Be(2, "$2 allows two Jobs of $0.99 and not a third with $0.02 left");
        run.Reviewed.Should().HaveCount(2);
        run.Problem.Should().Contain("not started").And.Contain("budget");
        h.Launcher.Requests.Should().OnlyContain(r => r.Budget.MaxCostUsd <= 2m);
    }

    [Fact]
    public async Task One_run_at_a_time_and_a_slot_runs_once()
    {
        var h = Harness.Build(issuesPerJob: 10);
        h.Org(("acme/api", 1));
        var slot = Given.Now.AddMinutes(-5);

        var gate = new TaskCompletionSource();
        h.Launcher.Answer = r =>
        {
            gate.Task.Wait(Ct);
            return ReviewLauncher.Everything("bug", "high", "P1")(r);
        };

        var first = await h.Runner.StartAsync(PlannerReviewTrigger.Schedule, ReviewRunner.ScheduleActor, slot, Ct);
        first.Outcome.Should().Be(ReviewStartOutcome.Started);

        (await h.Runner.StartAsync(PlannerReviewTrigger.Manual, "flo", null, Ct)).Outcome.Should().Be(ReviewStartOutcome.Busy);

        gate.SetResult();
        await h.Runner.WaitAsync();

        (await h.Runner.StartAsync(PlannerReviewTrigger.Schedule, ReviewRunner.ScheduleActor, slot, Ct)).Outcome
            .Should().Be(ReviewStartOutcome.Busy, "the slot ran; a restart in its minute must not run it again");
    }

    [Fact]
    public async Task A_model_may_not_start_a_run()
    {
        var h = Harness.Build();

        (await h.Runner.StartAsync(PlannerReviewTrigger.Manual, "model", null, Ct)).Outcome.Should().Be(ReviewStartOutcome.Forbidden);
    }

    [Fact]
    public async Task Without_the_board_there_is_no_run_rather_than_a_wrong_one()
    {
        var h = Harness.Build();
        h.Org(("acme/api", 1));
        h.Board.Value = null;

        var run = await h.RunAsync();

        run.State.Should().Be(PlannerReviewRunState.Failed);
        run.Problem.Should().Contain("board");
        h.Launcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_left_going_by_a_process_that_is_gone_ends_failed_at_startup()
    {
        var store = new InMemoryReviewStore();
        await store.TryStartAsync(new PlannerReviewRun { State = PlannerReviewRunState.Running, StartedAt = Given.Now.AddHours(-1), RequestedBy = "flo" }, Ct);

        await store.FailInterruptedAsync(Given.Now, Ct);

        store.Runs.Single().State.Should().Be(PlannerReviewRunState.Failed);
        store.Runs.Single().Problem.Should().Contain("stopped");
    }

    [Fact]
    public async Task The_read_has_the_shape_the_console_and_the_suite_read()
    {
        var h = Harness.Build(issuesPerJob: 10);
        h.Org(("acme/api", 431));
        await h.RunAsync();

        var json = JsonSerializer.Serialize(await h.Reader().ReadAsync(Ct), Web);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Equal("state", "problem", "run", "nextRunAt", "pending", "verdicts");
        root.GetProperty("state").GetString().Should().Be("Ready");

        var run = root.GetProperty("run");
        run.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "trigger", "state", "problem", "startedAt", "finishedAt", "open", "reviewed", "kept", "jobs", "costUsd", "counts");
        run.GetProperty("trigger").GetString().Should().Be("manual");
        run.GetProperty("state").GetString().Should().Be("Finished");
        run.GetProperty("counts").EnumerateObject().Select(p => p.Name).Should().Equal("close", "bug", "feature", "unsure");

        var verdict = root.GetProperty("verdicts")[0];
        verdict.EnumerateObject().Select(p => p.Name).Should().Equal(
            "repository", "number", "title", "url", "onBoard", "archived", "group", "closeReason", "related", "confidence",
            "summary", "reason", "evidence", "priority", "tab", "decision", "reviewedAt", "runId");
        verdict.GetProperty("evidence")[0].EnumerateObject().Select(p => p.Name).Should().Equal("kind", "text", "ok");
        verdict.GetProperty("decision").ValueKind.Should().Be(JsonValueKind.Null);

        await h.Store.DecideAsync("acme/api", 431, new ReviewDecision("flo", Given.Now, "bug", null, true, false), Ct);
        using var decided = JsonDocument.Parse(JsonSerializer.Serialize(await h.Reader().ReadAsync(Ct), Web));
        decided.RootElement.GetProperty("verdicts")[0].GetProperty("decision").EnumerateObject().Select(p => p.Name)
            .Should().Equal("by", "at", "group", "closeReason", "agreed", "unsure");
        decided.RootElement.GetProperty("pending").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task An_install_without_the_review_reads_off_and_says_why()
    {
        var h = Harness.Build(enabled: false);

        var view = await h.Reader().ReadAsync(Ct);

        view.State.Should().Be(ReviewState.Off);
        view.Problem.Should().Contain("github.planner.review.enabled");
        view.NextRunAt.Should().BeNull();
        (await h.Runner.StartAsync(PlannerReviewTrigger.Manual, "flo", null, Ct)).Outcome.Should().Be(ReviewStartOutcome.Off);
    }

    [Fact]
    public void Nothing_of_the_review_calls_a_write_of_the_client()
    {
        // The run proposes only (#312, the owner's decision): every close, Priority or comment is
        // a person's click through PlannerService. Read off the source, as the planner's rule is.
        var dir = Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "Planner", "Review");
        var writes = typeof(IGitHubClient).GetMethods()
            .Select(m => m.Name)
            .Where(n => !Regex.IsMatch(n, "^(Get|List|Search|Read)"))
            .ToList();

        writes.Should().Contain(["CloseIssueAsync", "CreateCommentAsync", "SetProjectFieldAsync", "AddLabelAsync"], "the list is every call that is not a read");

        var callers = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.(" + string.Join('|', writes) + @")\("))
            .Select(Path.GetFileName)
            .ToList();

        callers.Should().BeEmpty();
    }

    [Fact]
    public void A_batch_is_offered_its_repositories_on_github_com_and_never_an_archived_one()
    {
        var issues = new[] { Issue("acme/api", 1), Issue("acme/api", 2), Issue("acme/web", 3), Issue("acme/old", 4) };
        var repositories = new Dictionary<string, GitHubRepository?>
        {
            ["acme/api"] = new("acme/api", "main"),
            ["acme/web"] = new("acme/web", "develop"),
            ["acme/old"] = new("acme/old", "master", Archived: true),
        };

        var onGitHub = ReviewRunner.Offered(issues, repositories, new GitHubOptions(), new CodeFixOptions());
        onGitHub.Should().Equal(
            new ReviewRepository("acme/api", "https://github.com/acme/api", "main", false),
            new ReviewRepository("acme/web", "https://github.com/acme/web", "develop", false));

        var bound = new CodeFixOptions { Repositories = [new Hephaisto.Core.CodeFix.RepositoryBinding { Workload = "x", Url = "http://coder-git/acme/web.git", DefaultBranch = "fixture" }] };
        ReviewRunner.Offered(issues, repositories, new GitHubOptions { ApiBaseUrl = "http://stand-in/github/api" }, bound)
            .Should().Equal(new ReviewRepository("acme/web", "http://coder-git/acme/web.git", "fixture", false));
    }

    private static ReviewIssue Issue(string repository, int number) => new(
        repository, number, $"https://github.com/{repository}/issues/{number}", "t", null, "a", null, [], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z",
        false, null, [], [], [], [], 0);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private sealed class Harness
    {
        public required ReviewRunner Runner { get; init; }

        public required ReviewGitHub GitHub { get; init; }

        public required ReviewLauncher Launcher { get; init; }

        public required InMemoryReviewStore Store { get; init; }

        public required ReviewBoardStub Board { get; init; }

        public required Given.FixedClock Clock { get; init; }

        public required GitHubOptions Options { get; init; }

        public static Harness Build(bool enabled = true, int issuesPerJob = 25, int concurrency = 2, decimal maxCostUsd = 10m, int recheckAfterDays = 28)
        {
            var github = new ReviewGitHub();
            var services = new ServiceCollection();
            services.AddSingleton<IGitHubClient>(github);

            var options = new GitHubOptions
            {
                Enabled = true,
                ApiBaseUrl = "http://github-stand-in/github/api",
                Token = "t",
                Repositories = ["acme/api"],
                Planner = new GitHubPlannerOptions
                {
                    Enabled = true,
                    Owner = "acme",
                    Number = 3,
                    Review = new GitHubPlannerReviewOptions
                    {
                        Enabled = enabled,
                        IssuesPerJob = issuesPerJob,
                        MaxConcurrentJobs = concurrency,
                        MaxCostUsd = maxCostUsd,
                        RecheckAfterDays = recheckAfterDays,
                    },
                },
            };

            var codeFix = new CodeFixOptions { Image = "hephaisto/coder:dev", ContextRepositoryUrl = "http://coder-git/dev-context.git", Namespace = "hephaisto-coder", Sdk = "fake" };
            var store = new InMemoryReviewStore();
            var launcher = new ReviewLauncher();
            var clock = Given.Clock();
            var board = new ReviewBoardStub(TheBoard());

            var runner = new ReviewRunner(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new PlannerServiceTests.StaticOptions<GitHubOptions>(options),
                new PlannerServiceTests.StaticOptions<CodeFixOptions>(codeFix),
                launcher,
                store,
                board,
                clock,
                NullLogger<ReviewRunner>.Instance)
            {
                // A real yield, so that the batches of a run overlap as they do against a cluster.
                Delay = async (_, _) => await Task.Yield(),
            };

            return new Harness { Runner = runner, GitHub = github, Launcher = launcher, Store = store, Board = board, Clock = clock, Options = options };
        }

        public void Org(params (string Repository, int Number)[] issues)
        {
            foreach (var (repository, number) in issues)
            {
                GitHub.Open.Add(ReviewGitHub.Issue(repository, number, Then));
            }
        }

        public async Task<PlannerReviewRun> RunAsync()
        {
            var started = await Runner.StartAsync(PlannerReviewTrigger.Manual, "flo", null, Ct);
            started.Outcome.Should().Be(ReviewStartOutcome.Started, started.Message);

            await Runner.WaitAsync();

            return Store.Runs.Single(r => r.Id == started.RunId);
        }

        public ReviewReader Reader() =>
            new(Runner, Store, Board, new PlannerServiceTests.StaticOptions<GitHubOptions>(Options), Clock);

        private static Board TheBoard() => new(
            "PVT_1",
            "Platform planning",
            "https://github.com/orgs/acme/projects/3",
            [new BoardField("F_status", "Status", [new("s1", "Inbox"), new("s2", "In review")])],
            [
                new BoardItem(
                    "PVTI_431", "I_431", "acme/api", 431, "Issue 431", "https://github.com/acme/api/issues/431", "Bug", "reporter",
                    true, Given.Now.AddDays(-9), Given.Now.AddDays(-3), null, null, 0, 0,
                    new Dictionary<string, string> { ["Status"] = "In review", ["Area"] = "Platform" }, [], [], [],
                    FieldsChangedAt: new Dictionary<string, DateTimeOffset> { ["Status"] = Given.Now.AddDays(-4) }),
                new BoardItem(
                    "PVTI_475", "I_475", "acme/api", 475, "Issue 475", "https://github.com/acme/api/issues/475", "Bug", "reporter",
                    true, Given.Now.AddDays(-9), Given.Now.AddDays(-3), null, null, 0, 0,
                    new Dictionary<string, string> { ["Status"] = "Inbox" }, [], [], []),
            ]);
    }
}
