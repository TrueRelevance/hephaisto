using Hephaisto.Agent.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// A real Postgres, from <c>ConnectionStrings__hephaisto</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It throws when the variable is missing rather than skipping.</b> A suite that quietly
/// skips when its dependency is absent reports green on a machine where it ran nothing, and
/// the tests below exist precisely because a whole layer went untested while everything
/// looked healthy. Failing loudly is the point.
/// </para>
/// <para>
/// Locally: <c>./scripts/dev-db.sh up</c>. In CI: a <c>pgvector/pgvector:pg17</c> service
/// container. Both must also create the <c>hephaisto_app</c> role - the migration's
/// audit-immutability block is wrapped in <c>IF EXISTS (SELECT 1 FROM pg_roles ...)</c> and
/// silently does nothing without it, which would make <see cref="AuditImmutabilityTests"/>
/// pass while asserting nothing at all.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string SkipReason = "requires ConnectionStrings__hephaisto";

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__hephaisto")
            ?? throw new InvalidOperationException(
                "ConnectionStrings__hephaisto is not set. These tests need a real Postgres "
                + "with pgvector - run ./scripts/dev-db.sh up first. They are not skipped "
                + "when it is missing, deliberately: a green run that tested nothing is "
                + "worse than a red one.");

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public HephaistoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HephaistoDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector())
            .EnableSensitiveDataLogging()
            .Options;

        return new HephaistoDbContext(options);
    }

    /// <summary>
    /// The schema the tables actually landed in.
    /// </summary>
    /// <remarks>
    /// Resolved rather than assumed, because it is not the same everywhere. Nothing in the
    /// code calls <c>HasDefaultSchema</c>, so EF creates unqualified tables and Postgres puts
    /// them wherever <c>search_path</c> points - which is <c>"$user", public</c> by default.
    /// On the throwaway container from <c>scripts/dev-db.sh</c> no schema is named after the
    /// user, so they land in <c>public</c>. In the cluster the init script creates a
    /// <c>hephaisto</c> schema and the same migration puts them there instead.
    ///
    /// That divergence is not cosmetic: the migration's audit-immutability block grants on
    /// <c>SCHEMA public</c> by name, so it covers the tables on a developer's machine and
    /// covers nothing in the cluster.
    /// </remarks>
    public async Task<string> SchemaAsync()
    {
        await using var db = CreateContext();

        return await db.Database
            // EF projects SqlQuery<T> through a subquery that reads a column literally named
            // "Value", so the alias is required rather than cosmetic.
            .SqlQuery<string>($"""select table_schema as "Value" from information_schema.tables where table_name = 'audit_events'""")
            .SingleAsync();
    }

    /// <summary>
    /// Empties the incident graph between tests. Not the schema itself, and not
    /// <c>__EFMigrationsHistory</c> or <c>agent_mode</c>.
    /// </summary>
    public async Task ResetAsync()
    {
        var schema = await SchemaAsync();

        await using var db = CreateContext();

        // EF1002 is suppressed rather than worked around, because the interpolated value is a
        // table SCHEMA IDENTIFIER, and identifiers cannot be parameterised - ExecuteSqlAsync
        // would send `TRUNCATE TABLE $1`, which is a syntax error, not a safer query. The
        // value comes from SchemaAsync(), which reads it from the model this same assembly
        // built; it is never user input. Narrowly scoped to this one statement so the rule
        // still guards every other call site.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection.
        await db.Database.ExecuteSqlRawAsync($"""
            TRUNCATE TABLE
              {schema}.evidence, {schema}.evidence_blobs, {schema}.findings,
              {schema}.investigation_steps, {schema}.investigations,
              {schema}.agent_actions, {schema}.action_plans, {schema}.verifications,
              {schema}.incident_digests, {schema}.incident_events,
              {schema}.human_feedback, {schema}.audit_events, {schema}.signals,
              {schema}.workload_action_locks, {schema}.llm_usage,
              {schema}.notification_deliveries, {schema}.code_fix_attempts,
              {schema}.teams_bot_messages, {schema}.work_items,
              {schema}.alert_note_entries, {schema}.alert_notes,
              {schema}.planner_review_runs, {schema}.planner_review_verdicts,
              {schema}.incidents
            RESTART IDENTITY CASCADE;
            """);
#pragma warning restore EF1002
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
