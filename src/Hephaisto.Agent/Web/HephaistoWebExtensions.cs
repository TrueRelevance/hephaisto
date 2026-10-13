using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Hephaisto.Core.Abstractions;
using Hephaisto.Agent.Observability;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;

namespace Hephaisto.Agent.Web;

/// <summary>
/// The human-surface stream's contribution to the composition root: one AddXxx and one
/// MapXxx, so Program.cs stays one readable page.
/// </summary>
public static class HephaistoWebExtensions
{
    public static IServiceCollection AddHephaistoWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Enums as names on the wire, matching how they are stored in Postgres. An API that
        // answers `"state": 8` forces every consumer to keep a copy of the enum's numbering,
        // and renumbering it later silently changes the meaning of every recorded response.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Registered here as well as in AddHephaistoPersistence so the two are order-
        // independent; TryAdd means whichever runs first wins and there is still exactly one.
        services.TryAddSingleton<IClock>(SystemClock.Instance);

        // Which port the webhook answers on. Bound here rather than in Program.cs so the whole
        // web surface is configured in one place.
        services.AddOptions<WebOptions>()
            .BindConfiguration(WebOptions.SectionName)
            .Validate(
                o => string.IsNullOrEmpty(o.WebhookToken) || o.WebhookToken.Trim().Length >= WebhookTokenFilter.MinimumLength,
                $"Web:WebhookToken must be at least {WebhookTokenFilter.MinimumLength} characters. A short "
                + "token is a placeholder, not a credential; generate one with `openssl rand -hex 32`.")
            .ValidateOnStart();

        services.TryAddSingleton<IIncidentNotifier, IncidentNotifier>();
        services.TryAddSingleton<WatchdogMonitor>();
        services.TryAddSingleton<IncidentQueries>();

        // The connections panel (#111). Every one of these dependencies already describes itself
        // at startup and the description is thrown into a log line, so "is it actually working?"
        // was answerable only by reading pod logs. The probes re-ask on a timer and the cache
        // serves the last answer with its timestamp.
        //
        // Registered unconditionally, including the probes for things that may be unconfigured:
        // NotConfigured is an answer the panel needs to give, and a missing row would read as
        // "fine". Each resolves what it needs and reports rather than throwing.
        services.TryAddSingleton<IConnectionProbe, PostgresProbe>();
        services.AddSingleton<IConnectionProbe, KubernetesProbe>();
        services.AddSingleton<IConnectionProbe, GrafanaMcpProbe>();
        services.AddSingleton<IConnectionProbe, NotificationChannelProbe>();
        services.AddHttpClient<IConnectionProbe, GrafanaAnnotationProbe>();

        // Its own HttpClient rather than a shared one: the discovery fetch must not inherit the
        // Grafana probe's bearer token, and an IdP that 401s a request carrying someone else's
        // credential would report Unreachable for a reason that is not about the IdP.
        services.AddHttpClient<IConnectionProbe, OidcProbe>();

        // GitHub (v0.14.0), from what the issue poller last saw. Here and not in
        // AddHephaistoGitHub, because the first probe above is a TryAdd on the SERVICE type: a
        // probe registered before this method runs would make that one a no-op, and the
        // Postgres row would be the one that went missing.
        services.TryAddSingleton<GitHub.GitHubHealth>();
        services.AddOptions<GitHub.GitHubOptions>();
        services.AddSingleton<IConnectionProbe, GitHub.GitHubProbe>();

        services.TryAddSingleton<ConnectionHealthCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ConnectionHealthCache>());

        // TryAdd, so the ingest stream can register the real sink before this runs and this
        // will not overwrite it. The no-op logs and drops - see ISignalSink for why that is
        // the right failure shape for a webhook.
        services.TryAddSingleton<ISignalSink, LoggingSignalSink>();

        return services;
    }

    /// <summary>
    /// Every HTTP route this stream owns. Call once from Program.cs.
    /// </summary>
    /// <summary>
    /// Refuses a request that arrived on the wrong listener.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403: from the caller's side the endpoint genuinely does not exist on that
    /// port, and saying "forbidden" would confirm it exists somewhere - which is the one thing
    /// the split is trying not to advertise about the webhook.
    /// </remarks>
    internal static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>>
        OnPort(int port) =>
        async (context, next) =>
            context.HttpContext.Connection.LocalPort == port
                ? await next(context)
                : Results.NotFound();

    /// <summary>
    /// The console's pages and their circuit, behind the read policy for pages.
    /// </summary>
    /// <remarks>
    /// The pages are where a person signs in: the API answers a missing cookie with a 401, never
    /// a redirect, so a page is the only thing that can send a browser to the IdP. Mapped without
    /// the policy, nothing ever did - with Auth on, every viewer of the console was anonymous,
    /// could read it, and held no role, so close and approve were disabled for the approvers
    /// too. Production found that.
    /// </remarks>
    public static WebApplication MapHephaistoConsolePages(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode()
            .WithStaticAssets()
            .RequireAuthorization(AuthenticationExtensions.PagesPolicy);

        return app;
    }

    public static WebApplication MapHephaistoEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The webhook is the one surface OIDC does not cover - Alertmanager cannot sign in - and
        // with WebhookPort set it answers on its own port so a NetworkPolicy can protect it
        // without also deciding who may read the console. See WebOptions for why they shared a
        // port and what that cost. Its credential is a bearer token of its own, when configured.
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;

        // MapGroup("") adds no prefix and is both a route builder and a convention builder, so it
        // is the seam that lets a host constraint be applied to a set of endpoints without every
        // Map* method having to return one.
        var webhooks = app.MapGroup(string.Empty);
        webhooks.MapAlertmanagerEndpoints();

        var console = app.MapGroup(string.Empty);
        console.MapIncidentEndpoints();
        console.MapStatusEndpoints();
        console.MapModeEndpoints();
        console.MapVersionEndpoints();
        console.MapAlertNoteEndpoints();
        CodeFix.CodeFixEndpoints.MapCodeFixEndpoints(console);
        WorkItems.WorkItemEndpoints.MapWorkItemEndpoints(console);
        Planner.PlannerEndpoints.MapPlannerEndpoints(console);
        Investigations.Jobs.InvestigationJobEndpoints.MapInvestigationJobEndpoints(console);

        // Authentication (#110). The webhook group is the ONE surface outside OIDC: Alertmanager
        // cannot sign in. It checks a bearer token of its own instead, when one is configured
        // (#138) - Alertmanager sends one with http_config.authorization. Everything else
        // requires a signed-in user.
        webhooks.AllowAnonymous();
        console.RequireAuthorization(AuthenticationExtensions.ReadPolicy);

        if (!string.IsNullOrEmpty(web.WebhookToken))
        {
            webhooks.AddEndpointFilter(WebhookTokenFilter.Require(web.WebhookToken.Trim()));
        }
        else
        {
            app.Logger.LogWarning(
                "Web:WebhookToken is not set, so /webhooks accepts any request that can reach it. "
                + "Set the chart value secrets.webhookToken and send it from Alertmanager with "
                + "http_config.authorization.credentials_file.");
        }

        if (web.WebhookPortIsSeparate)
        {
            // The LISTENING port, not RequireHost.
            //
            // RequireHost matches the HOST HEADER, which is a different thing that happens to
            // look the same in a curl against 127.0.0.1:8080. Through a port-forward the header
            // carries the LOCAL port - `localhost:18100` - and behind an ingress it carries
            // whatever the ingress was reached on, so the constraint never matches and the whole
            // API 404s. The e2e gate found exactly that: `/api/version reports ''`.
            //
            // Connection.LocalPort is the socket the request actually arrived on. Nothing
            // upstream can rewrite it.
            webhooks.AddEndpointFilter(OnPort(web.WebhookPort));

            // And the reverse, which is the half that is easy to forget. If the API still
            // answered on the webhook port, the tight policy in front of that port would be
            // guarding one door with the wall missing beside it - anything allowed to deliver an
            // alert could also read every incident and approve an action.
            console.AddEndpointFilter(OnPort(web.MainPort));
        }

        // The one route Microsoft calls, when the Teams bot's buttons act (#124). On its own port
        // and only there: TeamsBotActionsExtensions.UseTeamsBotActionsPort answers nothing else on
        // that port, and the filter inside MapTeamsBotActions answers this nowhere else. It carries
        // its own policy, whose only scheme is the Bot Framework's - never the console's.
        var teamsActions = app.Services
            .GetRequiredService<IOptions<Hephaisto.Core.Notifications.NotificationOptions>>().Value.TeamsBot.Actions;

        if (teamsActions.Enabled)
        {
            Notifications.TeamsBot.TeamsBotActionsExtensions.MapTeamsBotActions(app, teamsActions.Port);
        }

        return app;
    }
}
