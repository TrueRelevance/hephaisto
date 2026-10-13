# -*- mode: Python -*-
#
# Hephaisto's inner loop. Run from ~/hephaisto with:
#
#     tilt up --port 10351
#
# Port 10351 because the ~/dev workspace already runs a detached Tilt on 10350. The two
# share one k3s cluster but no ports and no resources.
#
# NEVER run a bare `tilt down`: it helm-uninstalls the observability stack and takes
# Grafana's PVC with it. Every dashboard and datasource here is declarative so that losing
# the PVC costs nothing - which only holds as long as nobody creates dashboards by hand.

# Tilt refuses to deploy to a context it does not recognise as local, and k3s under Rancher
# Desktop is not on its allowlist. Both spellings appear: this machine's kubeconfig calls it
# studio-rancher-desktop, the laptop calls the same cluster rancher-desktop.
allow_k8s_contexts(['studio-rancher-desktop', 'rancher-desktop'])

load('ext://helm_resource', 'helm_resource', 'helm_repo')
load('ext://namespace', 'namespace_create')

# HOST and HOST_IP are per-machine and are read from tilt_config.json, which is NOT tracked
# - see tilt_config.sample.json. They are assigned just below config.parse().

def tailnet(host_port, container_port):
    return port_forward(host_port, container_port, host = HOST)

# --- why some forwards are local_resources and not port_forwards ---------------------------
#
# A Tilt resource forwards to ONE pod. helm_resource makes the whole Helm release a single
# resource, so a chart that deploys several distinct servers - kube-prometheus-stack is
# Prometheus AND Grafana AND Alertmanager - can only ever have one of them reachable through
# port_forwards. Declaring three there is silently wrong: Tilt binds all three host ports to
# whichever pod it selected, so :9090 works and :3030 and :9093 answer nothing at all. The
# same bites Loki, whose release includes loki-0 and loki-gateway on different ports.
#
# So: one port_forward per resource for its primary pod, and an explicit `kubectl
# port-forward` against the SERVICE for the rest. Forwarding to a Service also survives the
# pod restarts that a helm upgrade causes, which the pod-bound version does not.
def svc_forward(name, namespace, service, host_port, service_port, deps = []):
    # Retry forever rather than exiting. kubectl port-forward dies immediately if the Service
    # does not resolve yet, and helm_resource reports ok as soon as helm returns - which is
    # before the operator has created the Services this forwards to. Without the loop, grafana
    # and alertmanager come up dead on every fresh `tilt up` and need a manual trigger, while
    # loki happens to win the race and works. It also reconnects across the pod restarts a
    # helm upgrade causes.
    return local_resource(
        name,
        serve_cmd = 'until kubectl -n %s port-forward --address %s svc/%s %d:%d; do sleep 5; done' % (
            namespace, HOST_IP, service, host_port, service_port),
        resource_deps = deps,
        labels = ['forwards'],
        auto_init = True,
    )

# --- toggles ------------------------------------------------------------------------------

# Where the port-forwards bind. The default keeps a fresh clone working with no config at
# all. Set them in tilt_config.json to bind to something every machine on your network can
# reach - a VPN or Tailscale interface, say - and then one address works from everywhere.
# The tradeoff is that localhost then does NOT work, not even from a shell on this machine.
config.define_string('host',    args = False, usage = 'Hostname the port-forwards bind to')
# kubectl only accepts an IP or `localhost` for --address, never a hostname, so the
# svc_forward calls need the address `host` resolves to.
config.define_string('host-ip', args = False, usage = 'The IP that `host` resolves to')

config.define_bool('observability', args = False, usage = 'Prometheus, Grafana, Loki, Alertmanager, collector')
config.define_bool('tracing',       args = False, usage = 'Tempo + the Aspire dashboard')
config.define_bool('agent',         args = False, usage = 'Postgres and the Hephaisto pod')
config.define_bool('chaos',         args = False, usage = 'Register the chaos fixtures (still manual-trigger)')
# The code-fix stage (v0.9.0). Off by default: it builds a ~1.3 GB image, and with coder-sdk=real
# every eligible incident spends subscription quota.
config.define_bool('coder',         args = False, usage = 'Code fixes: coder image, coder-git server, codeFix.enabled')
config.define_string('coder-mode',  args = False, usage = 'codeFix.mode: off | plan | pr (default plan)')
config.define_string('coder-sdk',   args = False, usage = 'codeFix.sdk: fake ($0 scripted plumbing, default) | real')
# Investigation in a Job (v0.12.0 F5). Needs `coder`: the investigator Job borrows its image,
# namespace and Secret. investigator-sdk=fake is a $0 scripted investigator that still calls the
# agent's real investigator endpoint; real spends subscription quota on every investigation.
config.define_bool('investigator',     args = False, usage = 'investigation.job.enabled with executor job (needs coder)')
config.define_string('investigator-sdk', args = False, usage = 'investigation.job.sdk: fake ($0 scripted, default) | real')
# The agent's own model. Defaults to whatever `coder` is, because a code-fix run is an
# investigation first, and this project's dev cluster does not investigate with Gemini.
config.define_bool('local-llm',     args = False, usage = 'Investigate with the local Ollama (gpt-oss:120b) at host-ip:11434')
# The Teams bot: one board in a channel, edited in place, and alerts by personal chat.
#   off       (default)
#   stand-in  against infra/e2e/teams-stand-in.yaml - no tenant, nothing leaves the cluster
#   real      against Microsoft Teams. Needs charts/hephaisto/values-dev-teams-bot.local.yaml
#             (ignored by git; the ids and recipients) and the Secret
#             hephaisto-notification-teams-bot in namespace hephaisto, both made by hand.
config.define_string('teams-bot',   args = False, usage = 'Teams bot: off | stand-in | real (default off)')
# The pager suite (scripts/e2e/pager-local.sh): the model and Teams become the stand-in and every
# window a scenario waits out becomes seconds. It overrides local-llm and teams-bot, because the
# suite needs both to be the stand-in; switch it off to get the dev agent back on the local model.
config.define_bool('pager-e2e',     args = False, usage = 'Layer scripts/e2e/values-pager.yaml: stand-in model and Teams, short windows')
# The MCP endpoint (#157) with the pager suite's five tokens (scripts/e2e/values-mcp.yaml), forwarded
# to 8183. On whenever pager-e2e is, because the suite's P29-P48 ask it.
config.define_bool('mcp',           args = False, usage = 'The MCP endpoint on 8183, with the tokens mcp-secrets.sh makes')
# GitHub issues as work (v0.14.0, #243): an issue assigned to Hephaisto's account is polled for,
# planned, and approved in a comment.
#   off       (default)
#   stand-in  against infra/e2e/teams-stand-in.yaml, whose pod is GitHub too (GitHubStandIn.cs,
#             Service github-stand-in) - no account, nothing leaves the cluster. It is what
#             scripts/e2e/issues-local.sh runs against.
#   live      against github.com: the real bot account, its two tokens, the real `gh`
#             (charts/hephaisto/values-dev-github-live.yaml). For scripts/e2e/github-live.sh and
#             nothing else: it lists ONE repository, the sandbox, and needs `coder`. The Secrets
#             are made by hand; see that file's header.
config.define_string('github',      args = False, usage = 'GitHub issues as work: off | stand-in | live (default off)')
# The planner (v0.15.0, #300) against a real project on github.com, for scripts/e2e/planner-live.sh.
# Needs "github": "live" and a Secret made by hand; charts/hephaisto/values-dev-planner-live.yaml
# says which. With "github": "stand-in" the planner is on already, against the stand-in's board.
config.define_bool('planner-live',  args = False, usage = 'The planner against a real GitHub project (needs github: live)')
cfg = config.parse()

HOST    = cfg.get('host', 'localhost')
HOST_IP = cfg.get('host-ip', '127.0.0.1')

observability = cfg.get('observability', True)
tracing       = cfg.get('tracing', True)
agent         = cfg.get('agent', True)
chaos         = cfg.get('chaos', False)
coder         = cfg.get('coder', False)
coder_mode    = cfg.get('coder-mode', 'plan')
coder_sdk     = cfg.get('coder-sdk', 'fake')
local_llm     = cfg.get('local-llm', coder)
investigator  = cfg.get('investigator', False)
investigator_sdk = cfg.get('investigator-sdk', 'fake')
teams_bot     = cfg.get('teams-bot', 'off')
pager_e2e     = cfg.get('pager-e2e', False)
mcp           = cfg.get('mcp', False) or pager_e2e
github        = cfg.get('github', 'off')
planner_live  = cfg.get('planner-live', False)
if pager_e2e:
    local_llm = False
    teams_bot = 'stand-in'

if coder_mode not in ['off', 'plan', 'pr']:
    fail("coder-mode must be off, plan or pr - got '%s'" % coder_mode)
if teams_bot not in ['off', 'stand-in', 'real']:
    fail('teams-bot must be off, stand-in or real, not %r' % teams_bot)
if github not in ['off', 'stand-in', 'live']:
    fail('github must be off, stand-in or live, not %r' % github)
if planner_live and github != 'live':
    fail('planner-live needs github=live: it layers a real project over the live tier. With github=stand-in the planner already runs, against the stand-in\'s board.')
if github == 'live' and not coder:
    fail('github=live needs coder: an issue is planned and implemented by a coder Job, and the live tier is about its real `gh`.')
if github == 'live' and pager_e2e:
    fail('github=live and pager-e2e do not go together: the pager suite replaces the model and every window under a run against github.com.')
if coder_sdk not in ['fake', 'real']:
    fail("coder-sdk must be fake or real - got '%s'" % coder_sdk)
if investigator_sdk not in ['fake', 'real']:
    fail("investigator-sdk must be fake or real - got '%s'" % investigator_sdk)
if investigator and not coder:
    fail('investigator needs coder: the investigator Job borrows the coder image, namespace and Secret.')

# --- namespaces ---------------------------------------------------------------------------

k8s_yaml('infra/namespaces.yaml')

# --- helm repos ---------------------------------------------------------------------------
#
# Every chart is pinned to an exact version. A helm_resource without --version resolves
# "latest", which will silently major-upgrade Prometheus on some future `tilt up` and leave
# you debugging a stack you did not change.

if observability or tracing:
    helm_repo('prometheus-community', 'https://prometheus-community.github.io/helm-charts', labels = ['repos'])
    helm_repo('grafana-charts',       'https://grafana.github.io/helm-charts',              labels = ['repos'])
    # Distinct repo, not a mirror. The Tempo and grafana-mcp charts migrated here after
    # 2026-01-30; grafana/tempo tops out at 1.24.4 (deprecated) and grafana/grafana-mcp at
    # 0.3.1, so the pinned versions below simply do not exist in grafana-charts.
    helm_repo('grafana-community',    'https://grafana-community.github.io/helm-charts',    labels = ['repos'])
    helm_repo('open-telemetry',       'https://open-telemetry.github.io/opentelemetry-helm-charts', labels = ['repos'])

# --- observability stack ------------------------------------------------------------------

if observability:
    helm_resource(
        'kube-prometheus-stack',
        'prometheus-community/kube-prometheus-stack',
        namespace = 'hephaisto-obs',
        release_name = 'hephaisto',
        flags = [
            '--version', '81.1.0',
            '--values', 'infra/observability/kube-prometheus-stack.values.yaml',
            '--create-namespace',
        ],
        resource_deps = ['prometheus-community'],
        # Prometheus only. Grafana and Alertmanager are separate pods in this same release
        # and get their own Service forwards below - see the comment on svc_forward.
        port_forwards = [tailnet(9090, 9090)],
        labels = ['observability'],
    )

    svc_forward('grafana-forward', 'hephaisto-obs', 'hephaisto-grafana',
                3030, 80, deps = ['kube-prometheus-stack'])
    svc_forward('alertmanager-forward', 'hephaisto-obs',
                'hephaisto-kube-prometheus-alertmanager',
                9093, 9093, deps = ['kube-prometheus-stack'])

    helm_resource(
        'loki',
        'grafana-charts/loki',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '6.40.0',
            '--values', 'infra/observability/loki.values.yaml',
        ],
        resource_deps = ['grafana-charts'],
        # No port_forwards: this release has loki-0 (3100) and loki-gateway (80), and Tilt
        # would bind 3100 to whichever it selected - it picked the gateway, so :3100 answered
        # nothing.
        labels = ['observability'],
    )

    svc_forward('loki-forward', 'hephaisto-obs', 'loki', 3100, 3100, deps = ['loki'])

    helm_resource(
        'otel-collector',
        'open-telemetry/opentelemetry-collector',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '0.171.0',
            '--values', 'infra/observability/otel-collector.values.yaml',
        ],
        # Depends on its exporters existing first: a collector that starts before Tempo and
        # Loki spends its first minutes logging connection refused, which looks alarming and
        # is not.
        resource_deps = ['open-telemetry', 'kube-prometheus-stack', 'loki'] + (['tempo'] if tracing else []),
        port_forwards = [tailnet(4317, 4317), tailnet(4318, 4318)],
        labels = ['observability'],
    )

    helm_resource(
        'grafana-mcp',
        'grafana-community/grafana-mcp',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '0.27.1',
            '--values', 'infra/observability/grafana-mcp.values.yaml',
        ],
        resource_deps = ['grafana-community', 'kube-prometheus-stack'],
        port_forwards = [tailnet(8200, 8000)],
        labels = ['observability'],
    )

    # Datasources and dashboards are ConfigMaps picked up by Grafana's sidecars, and the
    # alert rules are CRs picked up by the operator's ruleSelector. Editing one is a
    # full-resource replace, not a live sync - that is expected, they are declarative state.
    #
    # The datasource ConfigMap is NOT optional. The values file sets
    # grafana.sidecar.datasources.defaultDatasourceEnabled: false so the chart does not
    # provision its own Prometheus datasource and fight this file over the `prometheus` uid.
    # Leaving this un-applied therefore does not fall back to a default - it leaves Grafana
    # with ZERO datasources, an empty Explore, and every dashboard panel showing "Datasource
    # not found". Nothing logs an error, because from Grafana's point of view it was simply
    # never told about any.
    k8s_yaml('infra/observability/grafana-datasources.yaml')
    # The alert rules and the Hephaisto dashboard are NOT applied here any more: they come
    # from the chart, below, because they are the agent's input rather than this stack's own
    # telemetry. Prometheus still selects them - ruleSelector matches on `release: hephaisto`
    # with ruleNamespaceSelector: {}, so it does not care that the chart renders them into
    # `hephaisto` rather than `hephaisto-obs`.
    k8s_resource(
        objects = ['hephaisto-datasources:configmap'],
        new_name = 'grafana-provisioning',
        resource_deps = ['kube-prometheus-stack'],
        labels = ['observability'],
    )

# --- traces -------------------------------------------------------------------------------

if tracing:
    helm_resource(
        'tempo',
        'grafana-community/tempo',
        namespace = 'hephaisto-obs',
        flags = [
            '--version', '2.3.0',
            '--values', 'infra/observability/tempo.values.yaml',
        ],
        resource_deps = ['grafana-community'],
        port_forwards = [tailnet(3200, 3200)],
        labels = ['observability'],
    )

    # Not a replacement for Tempo - Tempo is the durable system of record and this is
    # in-memory only. It earns its place by rendering gen_ai.* semconv spans natively:
    # model, token counts, prompts and tool arguments, with no panel to build.
    k8s_yaml('infra/observability/aspire-dashboard.yaml')
    k8s_resource(
        'aspire-dashboard',
        port_forwards = [tailnet(18888, 18888)],
        labels = ['observability'],
    )

# --- the agent ----------------------------------------------------------------------------

# One binary, three jobs: the c19 egress canary, the Teams stand-in and the GitHub one. Built
# once, here, so that none depends on another being switched on.
if chaos or teams_bot == 'stand-in' or github == 'stand-in':
    local_resource(
        'notification-receiver-image',
        cmd = 'docker build -q -f infra/e2e/notification-receiver/Dockerfile -t hephaisto/notification-receiver:dev .',
        deps = [
            'infra/e2e/notification-receiver/Program.cs',
            'infra/e2e/notification-receiver/TeamsStandIn.cs',
            'infra/e2e/notification-receiver/LlmStandIn.cs',
            'infra/e2e/notification-receiver/DecoyMcp.cs',
            'infra/e2e/notification-receiver/OidcStandIn.cs',
            'infra/e2e/notification-receiver/GitHubStandIn.cs',
            'scripts/e2e/mcp/neighbour-tools.json',
            'infra/e2e/notification-receiver/notification-receiver.csproj',
            'infra/e2e/notification-receiver/Dockerfile',
        ],
        labels = ['e2e'],
    )

if agent:

    # disable_push=True builds straight into this node's docker daemon. tilt_config.json in
    # ~/dev learned this the hard way; here there is no registry path at all, so there is
    # nothing to accidentally re-enable.
    custom_build(
        'hephaisto/agent',
        'docker build -t $EXPECTED_REF -f Dockerfile.dev .',
        deps = ['src', 'Directory.Build.props', 'Directory.Packages.props', 'Dockerfile.dev'],
        disable_push = True,
        live_update = [
            # A plain source edit syncs and dotnet watch hot-reloads. Only a .csproj or a
            # package change needs the full rebuild that fall_back_on forces.
            fall_back_on(['Directory.Packages.props', 'Directory.Build.props']),
            sync('src', '/app/src'),
        ],
    )

    # --- the chart, not hand-applied manifests ---------------------------------------------
    #
    # This is the same chart a consumer installs, rendered with values-dev.yaml. Every
    # `tilt up` is therefore a render test against a real cluster, and dev and prod stop being
    # two sources of truth that drift. infra/app/*.yaml is kept as the reference for what this
    # cluster ran before the chart existed; the chart is now what actually runs.
    #
    # helm() renders locally with no API server, which is why the chart uses explicit
    # .Values.*.enabled booleans and never .Capabilities.APIVersions.Has - a capability check
    # would make Tilt, `helm template` in CI, and a live install disagree with each other.
    #
    # Alerts and the dashboard are switched off when the observability stack is not up: they
    # target CRDs and a Grafana sidecar that do not exist in that configuration, which is
    # exactly where they used to sit before the chart.
    chart_values = ['charts/hephaisto/values-dev.yaml']
    chart_set = [] if observability else [
        'alerts.kubernetes=false',
        'alerts.slo=false',
        'alerts.watchdog=false',
        'alerts.observabilitySelfcheck=false',
        'dashboard.enabled=false',
    ]

    # --- the local model --------------------------------------------------------------------
    #
    # Ollama runs NATIVELY on this machine (GPU), not in the cluster, so the endpoint is the
    # host's address - host-ip from tilt_config.json - and the pod reaches it through the VM.
    # The values file carries placeholders for the two endpoints; they are overwritten here BY
    # NAME, reading the file rather than assuming an index, because --set addresses a list by
    # position and a hardcoded index silently overwrites the wrong entry the day somebody adds
    # one above it (the same lesson as deploy_extra_env_count in scripts/e2e/lib/deploy.sh).
    #
    # This file's extraEnv is the WHOLE list: Helm replaces lists rather than merging them, so
    # if values-dev.yaml ever grows an extraEnv of its own, move those entries in here too.
    if local_llm:
        if HOST_IP in ['127.0.0.1', 'localhost']:
            fail('local-llm needs host-ip in tilt_config.json: from inside a pod, 127.0.0.1 is the pod.')
        llm_values = 'charts/hephaisto/values-dev-local-llm.yaml'
        chart_values.append(llm_values)
        for i, entry in enumerate(read_yaml(llm_values)['extraEnv']):
            if entry['name'] in ['Llm__Endpoint', 'Llm__EmbeddingEndpoint']:
                chart_set.append('extraEnv[%d].value=http://%s:11434/v1' % (i, HOST_IP))

    # --- the code-fix stage -----------------------------------------------------------------
    #
    # Two images, built two different ways, and the difference is the point:
    #
    #   hephaisto/coder      is NOT a workload anything here deploys - the AGENT creates Jobs from
    #                        it, naming it in an env var (CodeFix__Image). custom_build could be
    #                        made to follow it there (match_in_env_vars=True), but then every
    #                        coder rebuild rewrites the agent Deployment's env and restarts the
    #                        agent, and a broken coder Dockerfile blocks every agent deploy. So a
    #                        plain local_resource builds a FIXED tag,
    #                        hephaisto/coder:dev, straight into this node's docker daemon (Rancher
    #                        Desktop's k3s runs on the same moby daemon, which is also why the
    #                        agent's disable_push build works), and values-dev-coder.yaml asks for
    #                        exactly that tag with pullPolicy Never. The next Job after a rebuild
    #                        picks it up; the agent does not restart. Nothing is pushed anywhere.
    #
    #   hephaisto/coder-git  IS a workload, so it is a normal custom_build with disable_push.
    if coder:
        chart_values.append('charts/hephaisto/values-dev-coder.yaml')
        chart_set.append('codeFix.mode=%s' % coder_mode)
        chart_set.append('codeFix.sdk=%s' % coder_sdk)
        # The shim follows where the repositories live, not which SDK runs: every dev mapping points
        # at the in-cluster git server, which real gh refuses, so a real-model run would push its
        # branch and then fail at `gh pr create`. With github=live the one repository is on
        # github.com, and running the real gh against it is what that mode is for.
        if github != 'live':
            chart_set.append('codeFix.gh=shim')
        if investigator:
            chart_values.append('charts/hephaisto/values-dev-investigator.yaml')
            chart_set.append('investigation.job.sdk=%s' % investigator_sdk)

        local_resource(
            'coder-image',
            cmd = 'docker build -q -t hephaisto/coder:dev coder',
            deps = ['coder'],
            ignore = ['coder/node_modules', 'coder/dist', 'coder/coverage'],
            labels = ['coder'],
        )

        # Seeded at build time from local checkouts (FIXTURE_REPO, DEV_CONTEXT_REPO - see the
        # script). The seed/ output is deliberately NOT a dep: the build writes it, and a dep on
        # it would rebuild forever. Re-seed after committing to the fixture repo or dev-context
        # with `tilt trigger coder-git`; that also resets every pushed branch.
        custom_build(
            'hephaisto/coder-git',
            'scripts/coder-git-seed.sh && docker build -t $EXPECTED_REF infra/coder/git-server',
            deps = [
                'scripts/coder-git-seed.sh',
                'infra/coder/git-server/Dockerfile',
                'infra/coder/git-server/lighttpd.conf',
                'infra/coder/git-server/entrypoint.sh',
                'infra/coder/git-server/index.cgi',
            ],
            disable_push = True,
        )
        k8s_yaml('infra/coder/git-server/git-server.yaml')
        k8s_resource(
            'coder-git',
            objects = [
                'coder-git-ingress:networkpolicy',
                'coder-git-dev-egress:networkpolicy',
                'coder-git-dev-proxy-egress:networkpolicy',
            ],
            labels = ['coder'],
        )

    # --- the Teams bot -----------------------------------------------------------------------
    #
    # The stand-in's two addresses have no chart value on purpose - nobody but a test harness
    # should move where the bot's token comes from - so they go in as extraEnv. Helm replaces a
    # list rather than merging it and --set addresses one by position, so they are APPENDED after
    # whatever the values files already put there, counted rather than assumed.
    #
    # The pager suite's values carry the whole stand-in configuration - the bot, its routes and
    # its two addresses - so with pager-e2e on, nothing below is layered or appended.
    if pager_e2e:
        chart_values.append('scripts/e2e/values-pager.yaml')

    # The MCP endpoint's tokens live in a Secret the chart only names; make it before the agent,
    # or the pod waits on a secretKeyRef that does not resolve. An existing Secret is kept.
    if mcp:
        chart_values.append('scripts/e2e/values-mcp.yaml')
        local_resource(
            'mcp-secrets',
            cmd = 'scripts/e2e/mcp-secrets.sh --context %s' % k8s_context(),
            deps = ['scripts/e2e/mcp-secrets.sh'],
            labels = ['agent'],
        )

    if teams_bot == 'stand-in' and not pager_e2e:
        chart_values.append('charts/hephaisto/values-dev-teams-bot.yaml')
        chart_set.append('notifications.baseUrl=http://%s:8100' % HOST)

        taken = 0
        for f in chart_values:
            layer = read_yaml(f)
            if 'extraEnv' in layer:
                taken = len(layer['extraEnv'])

        stand_in = 'http://teams-stand-in.hephaisto-obs:8080'
        chart_set.append('extraEnv[%d].name=Notifications__TeamsBot__LoginUrl' % taken)
        chart_set.append('extraEnv[%d].value=%s' % (taken, stand_in))
        chart_set.append('extraEnv[%d].name=Notifications__TeamsBot__ServiceUrl' % (taken + 1))
        chart_set.append('extraEnv[%d].value=%s/teams' % (taken + 1, stand_in))
        # Fast enough to watch. The default is sized for a channel people read.
        chart_set.append('extraEnv[%d].name=Notifications__TeamsBot__RefreshInterval' % (taken + 2))
        chart_set.append('extraEnv[%d].value=00:00:05' % (taken + 2))
        # The buttons that act (#124): the stand-in's Bot Framework key document, served over
        # plain http inside the cluster - which is the one reason RequireHttpsMetadata is off here.
        chart_set.append('extraEnv[%d].name=Notifications__TeamsBot__Actions__OpenIdMetadataUrl' % (taken + 3))
        chart_set.append('extraEnv[%d].value=%s/teams/openid/.well-known/openidconfiguration' % (taken + 3, stand_in))
        chart_set.append('extraEnv[%d].name=Notifications__TeamsBot__Actions__RequireHttpsMetadata' % (taken + 4))
        # 'false ' with the space, and not a typo: `--set` - all Tilt's helm() has - types a bare
        # false as a boolean, the chart's schema wants every env value a string and refuses the
        # render, and Tilt then deploys nothing. Helm leaves the padded word a string, and .NET's
        # bool parsing trims it. values-pager.yaml quotes it and never needed this.
        chart_set.append("extraEnv[%d].value=false " % (taken + 4))

    # --- GitHub issues as work ---------------------------------------------------------------
    #
    # The stand-in is the same pod as Teams', under a Service name of its own, so "stand-in" here
    # only makes sure that pod is applied - below, with Teams'.
    #
    # The agent is pointed at it by a values file, like the Teams bot: its API base URL becomes
    #
    #     http://github-stand-in.hephaisto-obs:8080/github/api
    #
    # its token the stand-in's GITHUB_STANDIN_TOKEN - by the chart's own secretKeyRef, from the
    # Secret hephaisto-github-stand-in that is applied with the stand-in below - and the account
    # it polls for the stand-in's GITHUB_STANDIN_BOT_LOGIN (infra/e2e/teams-stand-in.yaml).
    # scripts/e2e/issues-local.sh reads GitHub__ApiBaseUrl off the Deployment to tell which
    # GitHub an agent talks to; if the variable gets another name, change it there too.
    if github == 'stand-in':
        chart_values.append('charts/hephaisto/values-dev-github.yaml')

    # Live: github.com itself, through the coder's egress proxy, with the token of the Secret
    # hephaisto-github - made by hand and applied by nobody here. Layered after
    # values-dev-coder.yaml, whose context ref and workload mappings it replaces. The stand-in
    # pod is not applied for it (Teams' still is, when teams-bot says so).
    # scripts/e2e/github-live.sh reads GitHub__ApiBaseUrl, GitHub__Repositories__*,
    # GitHub__Approvers__* and CodeFix__Sdk off the Deployment and refuses anything but this.
    if github == 'live':
        chart_values.append('charts/hephaisto/values-dev-github-live.yaml')

    # The planner on a real project, after the live tier it needs: another Secret, the bot named
    # by its login, and the project. scripts/e2e/planner-live.sh reads GitHub__Planner__* off the
    # Deployment and refuses an agent whose planner is anywhere else.
    if planner_live:
        chart_values.append('charts/hephaisto/values-dev-planner-live.yaml')

    if teams_bot == 'stand-in' or github == 'stand-in':

        k8s_yaml('infra/e2e/teams-stand-in.yaml')
        k8s_resource(
            'teams-stand-in',
            objects = ['hephaisto-notification-teams-bot-stand-in:secret', 'hephaisto-github-stand-in:secret'],
            resource_deps = ['notification-receiver-image'],
            port_forwards = [tailnet(8110, 8080)],
            labels = ['agent'],
        )

    # The pod whose log is an instruction, for the pager suite's MCP scenarios (P33, P41).
    if pager_e2e:
        k8s_yaml('infra/e2e/pager-fixture.yaml')
        k8s_resource(
            'pager-payments',
            resource_deps = ['notification-receiver-image'],
            labels = ['e2e'],
        )

    if teams_bot == 'real':
        real_values = 'charts/hephaisto/values-dev-teams-bot.local.yaml'
        if not os.path.exists(real_values):
            fail(('teams-bot=real needs %s: notifications.baseUrl, notifications.teamsBot and '
                  + 'notifications.routes for YOUR tenant. It is ignored by git.') % real_values)
        chart_values.append(real_values)

    k8s_yaml(helm(
        'charts/hephaisto',
        name = 'hephaisto',
        namespace = 'hephaisto',
        values = chart_values,
        set = chart_set,
    ))

    k8s_resource(
        'hephaisto-postgres',
        port_forwards = [tailnet(5433, 5432)],
        labels = ['agent'],
    )

    k8s_resource(
        'hephaisto',
        port_forwards = [tailnet(8100, 8080)] + ([tailnet(8183, 8083)] if mcp else []),
        resource_deps = ['hephaisto-postgres'] + (['kube-prometheus-stack'] if observability else []) + (['mcp-secrets'] if mcp else []),
        labels = ['agent'],
    )

    # The chart's cluster-scoped and loose objects, grouped so the Tilt UI shows them as one
    # thing rather than a dozen unexplained entries.
    k8s_resource(
        objects = [
            'hephaisto:serviceaccount',
            'hephaisto-read:clusterrole',
            'hephaisto-read:clusterrolebinding',
            'hephaisto-node:clusterrole',
            'hephaisto-write:role',
            'hephaisto-write:rolebinding',
            'hephaisto-switches:configmap',
            'hephaisto-postgres-init:configmap',
            'hephaisto-ingress:networkpolicy',
            'hephaisto-postgres-ingress:networkpolicy',
        ],
        new_name = 'hephaisto-rbac',
        labels = ['agent'],
    )

    if coder:
        # The chart's code-fix objects in the coder namespace. The squid Deployment is its own
        # resource (hephaisto-coder-egress) because it is a workload; the rest - the one Role
        # that grants `create jobs`, the coder's unbound ServiceAccount, the policies, the NuGet
        # cache - are grouped here so the UI shows them as one thing.
        k8s_resource(
            objects = [
                'hephaisto-codefix:role:hephaisto-coder',
                'hephaisto-codefix:rolebinding:hephaisto-coder',
                'hephaisto-coder:serviceaccount:hephaisto-coder',
                'hephaisto-coder:networkpolicy:hephaisto-coder',
                'hephaisto-coder-egress:networkpolicy:hephaisto-coder',
                'hephaisto-coder-egress:configmap:hephaisto-coder',
                'hephaisto-coder-nuget:persistentvolumeclaim:hephaisto-coder',
            ],
            new_name = 'coder-rbac',
            labels = ['coder'],
        )
        k8s_resource('hephaisto-coder-egress', labels = ['coder'])

    if observability:
        # These need the Prometheus Operator's CRDs to exist before they can be applied at
        # all, and the Grafana sidecar to be running before the dashboard means anything -
        # which is the dependency the pre-chart Tiltfile expressed by putting them inside the
        # observability block.
        k8s_resource(
            objects = [
                'hephaisto-kubernetes-rules:prometheusrule',
                'hephaisto-slo-rules:prometheusrule',
                'hephaisto-watchdog:prometheusrule',
                'hephaisto-agent-presence:prometheusrule',
                'hephaisto-observability-selfcheck:prometheusrule',
                'hephaisto:podmonitor',
                'hephaisto-dashboard:configmap',
            ],
            new_name = 'alert-rules',
            resource_deps = ['kube-prometheus-stack'],
            labels = ['observability'],
        )

# --- chaos --------------------------------------------------------------------------------
#
# Every fixture is auto_init=False and manual-trigger, so `tilt up` never brings up a
# deliberately broken cluster by accident. You break things on purpose, one at a time, and
# watch what the agent makes of it.

# The fixtures with a SOURCE REPOSITORY behind them (v0.9.0) are the exception to "the resource
# is the file's basename": their Deployment is named for the service it pretends to be (shop-api,
# catalog-api), which is what the agent sees and what codeFix.repositories maps, so the resource
# is renamed back to the fixture id. Their image is built from the pinned fixture commit rather
# than pulled - see fixture_image_resource.
FIXTURE_REPO = '../hephaisto-fixture-dotnet'
CHAOS_WORKLOADS = {'c15-null-deref': 'shop-api', 'c19-injection': 'catalog-api'}

def fixture_image_resource(name):
    # Builds the EXACT pinned tag from the fixture branch's commit into the node's docker; no
    # pull, no push. The tag and the sha both come from the fixture manifest, so the running
    # image maps to exactly one commit - the property the coder's analysed_ref relies on.
    path = 'infra/chaos/%s.yaml' % name
    d = read_yaml(path)
    sha = d['metadata']['annotations']['hephaisto.dev/source-sha']
    image = d['spec']['template']['spec']['containers'][0]['image']
    local_resource(
        name + '-image',
        cmd = ['bash', '-c', ('set -euo pipefail; docker image inspect %s >/dev/null 2>&1 || ' +
               'git -C %s archive --format=tar %s | docker build -q -t %s -') % (image, FIXTURE_REPO, sha, image)],
        deps = [path],
        labels = ['chaos'],
    )
    return name + '-image'

if chaos:
    for f in listdir('infra/chaos'):
        if not f.endswith('.yaml'):
            continue

        # The resource name is the workload name inside the manifest, which is the file's
        # basename - c1-oomkill, not a 'chaos-' prefix - except for CHAOS_WORKLOADS above.
        # Deriving a different name here makes k8s_resource fail with "unknown resource" at load
        # time.
        name = os.path.basename(f).replace('.yaml', '')
        k8s_yaml(f)
        workload = CHAOS_WORKLOADS.get(name, name)
        extra = {'new_name': name} if workload != name else {}
        k8s_resource(
            workload,
            resource_deps = [fixture_image_resource(name)] if name in CHAOS_WORKLOADS else [],
            auto_init = False,
            trigger_mode = TRIGGER_MODE_MANUAL,
            # c9-memhog drives the whole node into memory pressure and will evict unrelated
            # pods, including Hephaisto's own. Run it alone, deliberately, and clean up.
            labels = ['chaos'],
            **extra
        )

    # The c19 injection canary: a second notification-receiver that counts anything reaching
    # it. The injected log lines tell the coder to `curl` it; a non-zero count means a command
    # from a log line ran. Its image is built above, because the Teams stand-in runs it too.
    k8s_yaml('infra/e2e/egress-canary.yaml')
    k8s_resource(
        'egress-canary',
        resource_deps = ['notification-receiver-image'],
        auto_init = False,
        trigger_mode = TRIGGER_MODE_MANUAL,
        labels = ['chaos'],
    )
