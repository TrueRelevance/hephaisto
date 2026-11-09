using System.Collections.Concurrent;
using System.Text.Json;
using k8s.Models;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Planner.Review;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Planning;

namespace Hephaisto.Tests.Planner;

/// <summary><see cref="IReviewStore"/> in memory, under the same rules (<see cref="ReviewVerdicts"/>) as Postgres.</summary>
internal sealed class InMemoryReviewStore : IReviewStore
{
    private readonly object gate = new();

    public List<PlannerReviewRun> Runs { get; } = [];

    public List<PlannerReviewVerdict> Rows { get; } = [];

    public Task FailInterruptedAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            foreach (var run in Runs.Where(r => r.State == PlannerReviewRunState.Running))
            {
                ReviewVerdicts.Interrupted(run, now);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryStartAsync(PlannerReviewRun run, CancellationToken ct)
    {
        lock (gate)
        {
            if (Runs.Any(r => r.State == PlannerReviewRunState.Running) || (run.Slot is { } slot && Runs.Any(r => r.Slot == slot)))
            {
                return Task.FromResult(false);
            }

            Runs.Add(Copy(run));
            return Task.FromResult(true);
        }
    }

    public Task UpdateRunAsync(PlannerReviewRun run, CancellationToken ct)
    {
        lock (gate)
        {
            Runs[Runs.FindIndex(r => r.Id == run.Id)] = Copy(run);
        }

        return Task.CompletedTask;
    }

    /// <summary>What each run's end announced, in order: the outbox rows that went with it.</summary>
    public List<NotificationDelivery> Announced { get; } = [];

    public Task EndRunAsync(PlannerReviewRun run, IReadOnlyCollection<NotificationDelivery> announce, CancellationToken ct)
    {
        lock (gate)
        {
            Runs[Runs.FindIndex(r => r.Id == run.Id)] = Copy(run);
            Announced.AddRange(announce);
        }

        return Task.CompletedTask;
    }

    public Task<PlannerReviewRun?> LatestRunAsync(CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(Runs.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Select(Copy).FirstOrDefault());
        }
    }

    public Task<bool> SlotTakenAsync(DateTimeOffset slot, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(Runs.Any(r => r.Slot == slot));
        }
    }

    public Task<IReadOnlyList<PlannerReviewVerdict>> VerdictsAsync(bool includeGone, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PlannerReviewVerdict>>([.. Rows.Where(v => includeGone || v.GoneAt is null).Select(Copy)]);
        }
    }

    public Task SyncOpenAsync(IReadOnlyCollection<ReviewOpenFacts> open, bool complete, DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            ReviewVerdicts.Sync(Rows, open, complete, now);
        }

        return Task.CompletedTask;
    }

    public Task SaveVerdictsAsync(IReadOnlyCollection<PlannerReviewVerdict> verdicts, CancellationToken ct)
    {
        lock (gate)
        {
            foreach (var fresh in verdicts)
            {
                if (Rows.FirstOrDefault(r => string.Equals(r.Reference, fresh.Reference, StringComparison.OrdinalIgnoreCase)) is { } kept)
                {
                    ReviewVerdicts.Merge(kept, fresh);
                }
                else
                {
                    Rows.Add(Copy(fresh));
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> DecideAsync(string repository, int number, ReviewDecision? decision, CancellationToken ct)
    {
        lock (gate)
        {
            if (Rows.FirstOrDefault(r => r.Repository == repository && r.Number == number && r.GoneAt is null) is not { } row)
            {
                return Task.FromResult(false);
            }

            ReviewVerdicts.Decide(row, decision);
            return Task.FromResult(true);
        }
    }

    public Task MarkGoneAsync(string repository, int number, DateTimeOffset at, CancellationToken ct)
    {
        lock (gate)
        {
            foreach (var row in Rows.Where(r => r.Repository == repository && r.Number == number && r.GoneAt is null))
            {
                row.GoneAt = at;
            }
        }

        return Task.CompletedTask;
    }

    public PlannerReviewVerdict Verdict(string reference)
    {
        lock (gate)
        {
            return Copy(Rows.Single(r => r.Reference == reference));
        }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

/// <summary>
/// An organisation on GitHub, read through every call the client has. Every write is recorded
/// and answered as if it worked - a run that wrote and went on would otherwise look like a run
/// that wrote nothing.
/// </summary>
internal sealed class ReviewGitHub : IGitHubClient
{
    public List<GitHubOrgIssue> Open { get; } = [];

    public Dictionary<string, GitHubIssueTimeline> Timelines { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<GitHubSubIssue>> SubIssues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Archived { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Reads { get; } = new();

    public ConcurrentQueue<string> Writes { get; } = new();

    /// <summary>A search that answers more than it lists: the listing is not complete.</summary>
    public int? Total { get; set; }

    public static GitHubOrgIssue Issue(string repository, int number, DateTimeOffset updated, string? type = "Bug", int subIssues = 0) =>
        new(
            repository,
            new GitHubIssue(number, $"I_{repository.Replace('/', '_')}_{number}", $"Issue {number} of {repository}", "It breaks.", "open",
                $"https://github.com/{repository}/issues/{number}", new GitHubAccount("reporter", 3), [], ["bug"], type),
            updated.AddDays(-30),
            updated,
            1,
            subIssues);

    public void Touch(string reference, DateTimeOffset at)
    {
        var i = Open.FindIndex(o => o.Reference == reference);
        Open[i] = Open[i] with { UpdatedAt = at };
    }

    public Task<GitHubResult<GitHubSearchPage>> SearchOpenIssuesAsync(string owner, int page, CancellationToken ct)
    {
        Reads.Enqueue($"search {owner} {page}");
        var items = Open.Where(i => i.Repository.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase)).Skip((page - 1) * 100).Take(100).ToList();

        return Ok(new GitHubSearchPage(items, Total ?? Open.Count, false, page * 100 < Open.Count));
    }

    public Task<GitHubResult<GitHubIssueTimeline>> ReadTimelineAsync(string repository, int number, CancellationToken ct)
    {
        Reads.Enqueue($"timeline {repository}#{number}");
        return Ok(Timelines.GetValueOrDefault($"{repository}#{number}") ?? new GitHubIssueTimeline([], [], [], true));
    }

    public Task<GitHubResult<IReadOnlyList<GitHubSubIssue>>> ListSubIssuesAsync(string repository, int number, CancellationToken ct)
    {
        Reads.Enqueue($"sub_issues {repository}#{number}");
        return Ok(SubIssues.GetValueOrDefault($"{repository}#{number}") ?? []);
    }

    public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string repository, CancellationToken ct)
    {
        Reads.Enqueue($"repository {repository}");
        return Ok(new GitHubRepository(repository, "main", Archived.Contains(repository)));
    }

    public Task<GitHubResult<Board>> GetProjectAsync(string owner, int number, CancellationToken ct) =>
        throw new InvalidOperationException("the review reads the planner's kept board, not a board of its own");

    public Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct) => Ok(new GitHubAccount("hephaisto-bot", 9));

    public Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<IReadOnlyList<GitHubReaction>>> ListCommentReactionsAsync(string repository, long commentId, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<IReadOnlyList<GitHubAssignment>>> ListAssignmentsAsync(string repository, int number, string? etag, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<IReadOnlyList<GitHubLabel>>> ListLabelsAsync(string repository, CancellationToken ct) => throw new NotSupportedException();

    public Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct) =>
        Wrote($"comment {repository}#{number}", new GitHubComment(1, body, new GitHubAccount("hephaisto-bot", 9), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "u"));

    public Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct) =>
        Wrote($"edit comment {commentId}", new GitHubComment(commentId, body, new GitHubAccount("hephaisto-bot", 9), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "u"));

    public Task<GitHubResult<GitHubReaction>> AddCommentReactionAsync(string repository, long commentId, string content, CancellationToken ct) =>
        Wrote($"react {commentId}", new GitHubReaction(1, content, new GitHubAccount("hephaisto-bot", 9), DateTimeOffset.UnixEpoch));

    public Task<GitHubResult<bool>> SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct) => Wrote($"field {itemId}", true);

    public Task<GitHubResult<bool>> SetProjectDateAsync(string projectId, string itemId, string fieldId, DateOnly? date, CancellationToken ct) => Wrote($"date {itemId}", true);

    public Task<GitHubResult<bool>> CloseIssueAsync(string issueId, bool notPlanned, CancellationToken ct) => Wrote($"close {issueId}", true);

    public Task<GitHubResult<bool>> AddLabelAsync(string repository, int number, string label, CancellationToken ct) => Wrote($"label {repository}#{number}", true);

    public Task<GitHubResult<bool>> RemoveLabelAsync(string repository, int number, string label, CancellationToken ct) => Wrote($"unlabel {repository}#{number}", true);

    public Task<GitHubResult<bool>> AddAssigneeAsync(string repository, int number, string login, CancellationToken ct) => Wrote($"assign {repository}#{number}", true);

    public Task<GitHubResult<bool>> RemoveAssigneeAsync(string repository, int number, string login, CancellationToken ct) => Wrote($"unassign {repository}#{number}", true);

    public Task<GitHubResult<GitHubIssue>> CreateIssueAsync(string repository, string title, string? body, string? type, CancellationToken ct) =>
        Wrote($"open {repository}", new GitHubIssue(1, "I", title, body, "open", "u", new GitHubAccount("hephaisto-bot", 9), [], [], type));

    public Task<GitHubResult<GitHubIssue>> UpdateIssueAsync(string repository, int number, string? title, string? body, CancellationToken ct) =>
        Wrote($"edit {repository}#{number}", new GitHubIssue(number, "I", title ?? string.Empty, body, "open", "u", new GitHubAccount("hephaisto-bot", 9), [], [], null));

    public Task<GitHubResult<string>> AddProjectItemAsync(string projectId, string contentId, CancellationToken ct) => Wrote($"add {contentId}", "PVTI");

    private static Task<GitHubResult<T>> Ok<T>(T value) => Task.FromResult(new GitHubResult<T>(GitHubOutcome.Ok, value));

    private Task<GitHubResult<T>> Wrote<T>(string what, T value)
    {
        Writes.Enqueue(what);
        return Ok(value);
    }
}

/// <summary>
/// Review Jobs that answer at once: <see cref="Answer"/> turns the request a Job was given into its
/// result. Counts how many ran at the same time.
/// </summary>
internal sealed class ReviewLauncher : ICodeFixJobLauncher
{
    private readonly ConcurrentDictionary<string, (ReviewRequest Request, int Looks)> jobs = new();
    private int running;

    public bool IsAvailable => true;

    public Func<ReviewRequest, ReviewResult?> Answer { get; set; } = Everything("bug", "high", "P1");

    public List<ReviewRequest> Requests { get; } = [];

    public List<string> Deleted { get; } = [];

    public int MostAtOnce { get; private set; }

    /// <summary>A verdict of the given kind for every issue of the request, at no cost.</summary>
    public static Func<ReviewRequest, ReviewResult?> Everything(string group, string confidence, string? priority, decimal cost = 0) =>
        request => Result(request, "reviewed", cost, [.. request.Issues.Select(i => Verdict($"{i.Repository}#{i.Number}", group, confidence, priority))]);

    public static ReviewVerdictResult Verdict(string issue, string group, string confidence, string? priority, string? closeReason = null) =>
        new(issue, group, group == "close" ? closeReason ?? "done" : closeReason, null, confidence, $"Summary of {issue}", $"Reason for {issue}", ["no pull request"], priority);

    public static ReviewResult Result(ReviewRequest request, string outcome, decimal cost, IReadOnlyList<ReviewVerdictResult> verdicts) => new()
    {
        AttemptId = request.AttemptId,
        Outcome = outcome,
        Verdicts = verdicts,
        Clones = [],
        CostUsd = cost,
        Billing = "fake",
        Turns = 1,
        SessionId = null,
        ContextSha = null,
        Error = outcome == "reviewed" ? null : "it stopped",
        DeniedToolCalls = [],
    };

    public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> LaunchAsync(V1Job spec, Func<V1Job, V1ConfigMap> request, CancellationToken ct)
    {
        var json = request(spec).Data["request.json"];
        var parsed = JsonSerializer.Deserialize<ReviewRequest>(json, CodeFixContract.Json)!;

        lock (Requests)
        {
            Requests.Add(parsed);
        }

        jobs[spec.Metadata.Name] = (parsed, 0);
        MostAtOnce = Math.Max(MostAtOnce, Interlocked.Increment(ref running));

        return Task.FromResult(spec.Metadata.Name);
    }

    public Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct)
    {
        var (request, looks) = jobs[jobName];
        jobs[jobName] = (request, looks + 1);

        // Running on the first look, done on the second: two batches can overlap.
        return Task.FromResult(new CodeFixJobObservation(looks == 0 ? CodeFixJobPhase.Running : CodeFixJobPhase.Succeeded, null));
    }

    public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct)
    {
        var result = Answer(jobs[jobName].Request);

        return Task.FromResult<string?>(result is null ? "the coder crashed\n" : "progress\n" + CodeFixResultParser.Frame(JsonSerializer.Serialize(result, CodeFixContract.Json)));
    }

    public Task DeleteAsync(string jobName, CancellationToken ct)
    {
        lock (Deleted)
        {
            Deleted.Add(jobName);
        }

        Interlocked.Decrement(ref running);
        return Task.CompletedTask;
    }
}

internal sealed class ReviewBoardStub(Board? board) : IReviewBoard
{
    public Board? Value { get; set; } = board;

    public Task<(Board? Board, string? Problem)> ReadAsync(CancellationToken ct) =>
        Task.FromResult(Value is { } b ? (b, (string?)null) : ((Board?)null, "the board could not be read"));

    public Board? Kept => Value;
}
