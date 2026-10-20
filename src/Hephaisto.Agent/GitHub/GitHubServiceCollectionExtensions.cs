using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.GitHub;

/// <summary>Refuses a GitHub section that is enabled and cannot work, one sentence per reason.</summary>
public sealed class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubOptions options) =>
        options.Problems() is { Count: > 0 } problems
            ? ValidateOptionsResult.Fail(problems)
            : ValidateOptionsResult.Success;
}

public static class GitHubServiceCollectionExtensions
{
    /// <summary>
    /// GitHub issues as work (v0.14.0). The reads of <c>work_items</c> are registered always, so
    /// <c>/api/workitems</c> answers an empty list on an install that never enabled this; the
    /// client and the poller exist only when <c>GitHub:Enabled</c>, the shape the Teams bot has.
    /// </summary>
    public static IServiceCollection AddHephaistoGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GitHubOptions>()
            .Bind(configuration.GetSection(GitHubOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>());

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<GitHubHealth>();
        services.TryAddSingleton<GitHubMetrics>();
        services.TryAddSingleton<GitHubRateLimit>();
        services.AddScoped<WorkItemQueries>();
        services.AddScoped<WorkItemActions>();

        // The planner (v0.15.0), registered always for the reason the reads above are: its page
        // and /api/planner say "off" on an install that never enabled it.
        services.TryAddSingleton<Planner.PlannerService>();
        services.TryAddSingleton<Planner.TriageRunner>();

        var configured = configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new GitHubOptions();

        if (!configured.Enabled)
        {
            return services;
        }

        // RemoveAllResilienceHandlers, for the reason the notification channels give: the
        // standard handler would turn each call into several against an API that is failing or
        // rate limiting, and the poller's next pass is already the retry. See GitHubClient.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<IGitHubClient, GitHubClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(sp => PrimaryHandler(sp.GetRequiredService<IOptions<GitHubOptions>>().Value))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddHostedService<GitHubIssuePoller>();

        // It follows the Jobs somebody started with a click, and does nothing while there are none.
        services.AddHostedService(sp => sp.GetRequiredService<Planner.TriageRunner>());

        // The planner's board, read ahead so its page does not wait on GitHub (#306). Asks nothing
        // while the planner is off.
        services.AddHostedService<Planner.PlannerWarmer>();

        return services;
    }

    /// <summary>
    /// The connection to GitHub: through <see cref="GitHubOptions.ProxyUrl"/> when one is set,
    /// otherwise however the process reaches anything else.
    /// </summary>
    internal static SocketsHttpHandler PrimaryHandler(GitHubOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            // The factory rotates handlers; this bounds a connection held across a DNS change.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            handler.Proxy = new WebProxy(new Uri(options.ProxyUrl.Trim()));
            handler.UseProxy = true;
        }

        return handler;
    }
}
