using Hephaisto.Agent.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>What a run found of an open issue, beyond its verdict: where it is now.</summary>
/// <param name="Archived">Null when the repository could not be asked: what was kept stays.</param>
public sealed record ReviewOpenFacts(string Repository, int Number, string Title, string Url, bool OnBoard, bool? Archived);

/// <summary>A person's decision on a verdict (stage 4 writes it).</summary>
/// <param name="Group">The group the issue goes in: the verdict's own when <paramref name="Agreed"/>.</param>
/// <param name="Unsure">The person cannot tell either: shown under unsure.</param>
public sealed record ReviewDecision(string By, DateTimeOffset At, string Group, string? CloseReason, bool Agreed, bool Unsure);

/// <summary>
/// The weekly review's rows (#312): its runs and the one current verdict per issue. Behind an
/// interface so the runner's rules are tested without a database.
/// </summary>
public interface IReviewStore
{
    /// <summary>Every run left Running by a process that is gone ends Failed. At startup, before anything starts a run.</summary>
    Task FailInterruptedAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Writes a new run, or answers false: another one is running, or the run's slot already ran.</summary>
    Task<bool> TryStartAsync(PlannerReviewRun run, CancellationToken ct);

    Task UpdateRunAsync(PlannerReviewRun run, CancellationToken ct);

    /// <summary>
    /// The run's end, and the notifications that announce it, in one save: a run is announced
    /// exactly when its end is recorded.
    /// </summary>
    Task EndRunAsync(PlannerReviewRun run, IReadOnlyCollection<NotificationDelivery> announce, CancellationToken ct);

    Task<PlannerReviewRun?> LatestRunAsync(CancellationToken ct);

    Task<bool> SlotTakenAsync(DateTimeOffset slot, CancellationToken ct);

    Task<IReadOnlyList<PlannerReviewVerdict>> VerdictsAsync(bool includeGone, CancellationToken ct);

    /// <summary>
    /// What a run found: each open issue's place now (title, on the board, archived), and - when
    /// <paramref name="complete"/>, so that an issue the search left out is not taken for closed -
    /// every kept verdict whose issue is no longer open is gone.
    /// </summary>
    Task SyncOpenAsync(IReadOnlyCollection<ReviewOpenFacts> open, bool complete, DateTimeOffset now, CancellationToken ct);

    /// <summary>New verdicts, each replacing its issue's by <see cref="ReviewVerdicts.Merge"/>.</summary>
    Task SaveVerdictsAsync(IReadOnlyCollection<PlannerReviewVerdict> verdicts, CancellationToken ct);

    /// <summary>A person's decision. False when the issue has no verdict that is not gone.</summary>
    Task<bool> DecideAsync(string repository, int number, ReviewDecision? decision, CancellationToken ct);

    /// <summary>A person closed the issue: it drops out of the review at once.</summary>
    Task MarkGoneAsync(string repository, int number, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Postgres, one scope per call: the runner is a singleton and its batches run side by side.</summary>
public sealed class EfReviewStore(IServiceScopeFactory scopes) : IReviewStore
{
    public async Task FailInterruptedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var running = await db.PlannerReviewRuns.Where(r => r.State == PlannerReviewRunState.Running).ToListAsync(ct).ConfigureAwait(false);

        foreach (var run in running)
        {
            ReviewVerdicts.Interrupted(run, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryStartAsync(PlannerReviewRun run, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        if (await db.PlannerReviewRuns.AnyAsync(r => r.State == PlannerReviewRunState.Running, ct).ConfigureAwait(false))
        {
            return false;
        }

        db.PlannerReviewRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // The slot ran already: a restart within its minute, or another replica.
            return false;
        }
    }

    public async Task UpdateRunAsync(PlannerReviewRun run, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        db.PlannerReviewRuns.Update(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task EndRunAsync(PlannerReviewRun run, IReadOnlyCollection<NotificationDelivery> announce, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(announce);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        db.PlannerReviewRuns.Update(run);
        db.NotificationDeliveries.AddRange(announce);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PlannerReviewRun?> LatestRunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        return await db.PlannerReviewRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SlotTakenAsync(DateTimeOffset slot, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        return await db.PlannerReviewRuns.AnyAsync(r => r.Slot == slot, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlannerReviewVerdict>> VerdictsAsync(bool includeGone, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var query = db.PlannerReviewVerdicts.AsNoTracking();

        return await (includeGone ? query : query.Where(v => v.GoneAt == null)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task SyncOpenAsync(IReadOnlyCollection<ReviewOpenFacts> open, bool complete, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var rows = await db.PlannerReviewVerdicts.Where(v => v.GoneAt == null).ToListAsync(ct).ConfigureAwait(false);

        ReviewVerdicts.Sync(rows, open, complete, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveVerdictsAsync(IReadOnlyCollection<PlannerReviewVerdict> verdicts, CancellationToken ct)
    {
        if (verdicts.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var repositories = verdicts.Select(v => v.Repository).Distinct().ToList();
        var existing = (await db.PlannerReviewVerdicts.Where(v => repositories.Contains(v.Repository)).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(v => v.Reference, StringComparer.OrdinalIgnoreCase);

        foreach (var fresh in verdicts)
        {
            if (existing.TryGetValue(fresh.Reference, out var kept))
            {
                ReviewVerdicts.Merge(kept, fresh);
            }
            else
            {
                db.PlannerReviewVerdicts.Add(fresh);
                existing[fresh.Reference] = fresh;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DecideAsync(string repository, int number, ReviewDecision? decision, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var row = await db.PlannerReviewVerdicts.FirstOrDefaultAsync(v => v.Repository == repository && v.Number == number && v.GoneAt == null, ct).ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        ReviewVerdicts.Decide(row, decision);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task MarkGoneAsync(string repository, int number, DateTimeOffset at, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        await db.PlannerReviewVerdicts
            .Where(v => v.Repository == repository && v.Number == number && v.GoneAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.GoneAt, at), ct)
            .ConfigureAwait(false);
    }
}
