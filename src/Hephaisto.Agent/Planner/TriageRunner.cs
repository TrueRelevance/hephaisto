using System.Collections.Concurrent;
using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Planner;

public enum TriageState
{
    Running = 0,

    /// <summary>Claude answered, and what it answered is on the issue.</summary>
    Done = 1,

    Failed = 2,
}

/// <summary>One time somebody asked Claude about an issue, as far as this process knows.</summary>
public sealed record TriageRun(
    string Repository,
    int Number,
    string Url,
    Guid AttemptId,
    string JobName,
    string RequestedBy,
    DateTimeOffset StartedAt,
    TriageState State,
    string? Message,
    DateTimeOffset? EndedAt)
{
    public string Reference => $"{Repository}#{Number}";
}

public enum TriageStartOutcome
{
    Started = 0,
    Off = 1,
    Forbidden = 2,
    NotFound = 3,

    /// <summary>Claude is already at this issue, or as many Jobs run as may.</summary>
    Busy = 4,
    Refused = 5,
}

public sealed record TriageStart(TriageStartOutcome Outcome, string Message, TriageRun? Run);

/// <summary>
/// Claude's analysis of an issue (v0.15.0): a coder Job per click, watched until it answers, and
/// its answer handed to <see cref="PlannerService.ApplyTriageAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing starts it but a person.</b> A click on the planner, or opening an issue there with
/// "ask Claude" ticked. There is no pass that looks for issues to analyse.
/// </para>
/// <para>
/// <b>The runs are kept in memory, the answers on GitHub.</b> The labels and the comment are the
/// durable part, and the planner reads the analysis back from the comment. A restart forgets a
/// run that was still going: its Job ends by its deadline and is collected, nothing is put on the
/// issue, and the button asks again. That is cheaper than a table for what a click repeats.
/// </para>
/// </remarks>
public sealed class TriageRunner(
    IServiceScopeFactory scopes,
    IOptionsMonitor<GitHubOptions> github,
    IOptionsMonitor<CodeFixOptions> codeFix,
    ICodeFixJobLauncher launcher,
    PlannerService planner,
    IClock clock,
    ILogger<TriageRunner> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a finished run is still shown. Its answer is on the issue for good.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromHours(6);

    /// <summary>Beyond the Job's own deadline, before a Job that never ended is given up on.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, TriageRun> runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A run ended. For the page that shows it.</summary>
    public event Action<TriageRun>? Ended;

    public bool Enabled => github.CurrentValue is { Enabled: true, Planner: { Enabled: true, Triage.Enabled: true } };

    /// <summary>The newest run for an issue that this process knows of, or null.</summary>
    public TriageRun? Run(string repository, int number) =>
        runs.TryGetValue(Key(repository, number), out var run) ? run : null;

    public IReadOnlyList<TriageRun> Runs => [.. runs.Values.OrderByDescending(r => r.StartedAt)];

    public async Task<TriageStart> StartAsync(string repository, int number, string actor, CancellationToken ct)
    {
        var who = actor?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (!Enabled)
        {
            return new(TriageStartOutcome.Off, "asking Claude is off on this install", null);
        }

        if (IncidentStateMachine.IsForbiddenGranter(who))
        {
            return new(TriageStartOutcome.Forbidden, $"'{who}' may not ask here: asking Claude is a person's click.", null);
        }

        var o = codeFix.CurrentValue;
        var triage = github.CurrentValue.Planner.Triage;

        if (!launcher.IsAvailable || string.IsNullOrWhiteSpace(o.Image) || string.IsNullOrWhiteSpace(o.ContextRepositoryUrl))
        {
            return new(TriageStartOutcome.Off, "Claude runs as a coder Job, and this install has no coder image or no dev-context to give it", null);
        }

        var key = Key(repository, number);

        if (runs.TryGetValue(key, out var running) && running.State == TriageState.Running)
        {
            return new(TriageStartOutcome.Busy, $"Claude is already at {running.Reference}, since {running.StartedAt:HH:mm} UTC", running);
        }

        if (runs.Values.Count(r => r.State == TriageState.Running) >= triage.MaxConcurrentJobs)
        {
            return new(TriageStartOutcome.Busy, $"Claude is at {triage.MaxConcurrentJobs} issues already; ask again when one is done", null);
        }

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            return new(TriageStartOutcome.Off, "asking Claude is off on this install", null);
        }

        var issue = await client.GetIssueAsync(repository, number, ct).ConfigureAwait(false);

        if (!issue.Ok)
        {
            return new(issue.Outcome == GitHubOutcome.NotFound ? TriageStartOutcome.NotFound : TriageStartOutcome.Refused, issue.Describe(), null);
        }

        var labels = await client.ListLabelsAsync(repository, ct).ConfigureAwait(false);

        if (!labels.Ok)
        {
            return new(TriageStartOutcome.Refused, $"the labels of {repository} could not be read: {labels.Describe()}", null);
        }

        var attemptId = Guid.CreateVersion7();
        var request = Request(attemptId, repository, issue.Value!, labels.Value!, triage, o);
        var json = JsonSerializer.Serialize(request, CodeFixContract.Json);
        var reference = $"{repository}#{number}";

        await planner.RecordAsync(PlannerService.AuditTriage, who, $"Claude asked to analyse {reference}", repository, number, issue.Value!.Url,
            new { attempt = attemptId }, ct).ConfigureAwait(false);

        string jobName;

        try
        {
            jobName = await launcher.LaunchAsync(
                TriageJobSpec.Job(attemptId, reference, triage, o),
                job => TriageJobSpec.RequestConfigMap(attemptId, o, json, job),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The triage Job for {Issue} could not be started.", reference);

            return new(TriageStartOutcome.Refused, $"the Job could not be started: {ex.Message}", null);
        }

        var run = new TriageRun(repository, number, issue.Value!.Url, attemptId, jobName, who, clock.UtcNow, TriageState.Running, null, null);
        runs[key] = run;

        logger.LogInformation("Claude is analysing {Issue} in Job {Job}, asked for by {Actor}.", reference, jobName, who);

        return new(TriageStartOutcome.Started, $"Claude is reading {reference}", run);
    }

    /// <summary>One look at every run that is still going. A finished Job's answer is applied here.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        foreach (var (key, run) in runs.ToArray())
        {
            if (run.State != TriageState.Running)
            {
                if (run.EndedAt is { } ended && now - ended > ShowFor)
                {
                    runs.TryRemove(new KeyValuePair<string, TriageRun>(key, run));
                }

                continue;
            }

            try
            {
                await FollowAsync(key, run, now, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The next pass asks again; one Job's trouble does not stop the others.
                logger.LogWarning(ex, "Following the triage Job {Job} failed.", run.JobName);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!runs.IsEmpty)
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FollowAsync(string key, TriageRun run, DateTimeOffset now, CancellationToken ct)
    {
        var observed = await launcher.ObserveAsync(run.JobName, ct).ConfigureAwait(false);

        if (observed.Phase is CodeFixJobPhase.Pending or CodeFixJobPhase.Running)
        {
            if (now - run.StartedAt > github.CurrentValue.Planner.Triage.Deadline + Grace)
            {
                await EndAsync(key, run, TriageState.Failed, "the Job did not end within its deadline", ct).ConfigureAwait(false);
            }

            return;
        }

        var log = observed.Phase == CodeFixJobPhase.Missing ? null : await launcher.ReadResultLogAsync(run.JobName, ct).ConfigureAwait(false);
        var parsed = CodeFixResultParser.ParseTriage(log, run.AttemptId);

        if (parsed.Result is not { } result)
        {
            await EndAsync(key, run, TriageState.Failed,
                observed.Phase == CodeFixJobPhase.Missing ? "the Job is gone and left no answer" : $"the Job's answer could not be read: {parsed.Violation}", ct).ConfigureAwait(false);

            return;
        }

        if (result.Outcome != "triaged")
        {
            await EndAsync(key, run, TriageState.Failed, $"Claude did not finish ({result.Outcome}){(result.Error is { } e ? $": {e}" : string.Empty)}", ct).ConfigureAwait(false);

            return;
        }

        await using var scope = scopes.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGitHubClient>() is not { } client)
        {
            await EndAsync(key, run, TriageState.Failed, "GitHub is off", ct).ConfigureAwait(false);
            return;
        }

        // The labels again, not the ones the request held: what is put on must exist now.
        var labels = await client.ListLabelsAsync(run.Repository, ct).ConfigureAwait(false);

        if (!labels.Ok)
        {
            // Not ended: the Job's log stays until its TTL, and the next pass tries again.
            logger.LogWarning("The labels of {Repository} could not be read for {Issue}: {Detail}", run.Repository, run.Reference, labels.Describe());
            return;
        }

        var issue = new TriageIssue(run.Repository, run.Number, run.Url, string.Empty, null, string.Empty, null);
        var (done, message) = await planner.ApplyTriageAsync(issue, result, labels.Value!, run.RequestedBy, ct).ConfigureAwait(false);

        await EndAsync(key, run, done ? TriageState.Done : TriageState.Failed, message, ct).ConfigureAwait(false);
    }

    private async Task EndAsync(string key, TriageRun run, TriageState state, string message, CancellationToken ct)
    {
        var ended = run with { State = state, Message = message, EndedAt = clock.UtcNow };

        if (!runs.TryUpdate(key, ended, run))
        {
            return;
        }

        if (state == TriageState.Failed)
        {
            logger.LogWarning("Claude's analysis of {Issue} failed: {Message}", run.Reference, message);
        }
        else
        {
            logger.LogInformation("Claude's analysis of {Issue} is done: {Message}", run.Reference, message);
        }

        try
        {
            await launcher.DeleteAsync(run.JobName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The Job's TTL collects it.
            logger.LogDebug(ex, "The triage Job {Job} could not be deleted.", run.JobName);
        }

        Ended?.Invoke(ended);
    }

    internal static TriageRequest Request(Guid attemptId, string repository, GitHubIssue issue, IReadOnlyList<GitHubLabel> labels, GitHubPlannerTriageOptions triage, CodeFixOptions o) => new()
    {
        AttemptId = attemptId,
        Budget = new InvestigateBudget(triage.MaxCostUsd, (int)triage.Deadline.TotalSeconds, triage.MaxTurns),
        Context = new CodeFixContextRef(o.ContextRepositoryUrl, string.IsNullOrWhiteSpace(o.ContextRepositoryRef) ? "main" : o.ContextRepositoryRef),
        Issue = new TriageIssue(
            repository,
            issue.Number,
            Cap(issue.Url, 512),
            Cap(string.IsNullOrWhiteSpace(issue.Title) ? "(no title)" : issue.Title, 512),
            issue.Type is null ? null : Cap(issue.Type, 100),
            Cap(string.IsNullOrWhiteSpace(issue.Author.Login) ? "ghost" : issue.Author.Login, 100),
            issue.Body is null ? null : Cap(issue.Body, 20_000)),
        Labels = [.. labels.Take(200).Select(l => new TriageLabel(Cap(l.Name, 100), l.Description is null ? null : Cap(l.Description, 500)))],
    };

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];

    private static string Key(string repository, int number) => $"{repository.Trim()}#{number}";
}
