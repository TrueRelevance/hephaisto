# hephaisto

An autonomous SRE agent that investigates Kubernetes incidents and reports what it found.

It receives Alertmanager webhooks, investigates with PromQL, LogQL and the Kubernetes API
under a step, token, cost and wall-clock budget, writes a diagnosis citing the evidence it
used, and — where you have allowed it to — executes a narrow allowlist of reversible actions,
verifies them, and reverts or escalates when they do not hold.

```sh
helm install hephaisto oci://ghcr.io/truerelevance/charts/hephaisto \
  --namespace hephaisto --create-namespace \
  --set cluster.name=<the cluster label on this cluster's metrics>
```

`cluster.name` is the one value with no default, and rendering fails without it: it is in every
signal fingerprint and in every query the model writes.

**Installed as it ships, the agent acts nowhere.** `policy.actionableNamespaces` is empty, so no
write `Role` is rendered at all; `policy.autoEnabledActionTypes` is empty; and `mode` is
`Observe`. Enabling anything takes four deliberate changes, in git — naming a namespace,
labelling that namespace `hephaisto.dev/destructive-actions-allowed: "true"`, promoting one
action type, and raising the mode.

## What it needs

| | |
|---|---|
| Kubernetes | any recent version; the chart targets prometheus-operator CRDs |
| **PostgreSQL 17 with `pgvector`** | required — the agent fails fast without it, on purpose |
| Prometheus + Alertmanager + prometheus-operator | required — the shipped `PrometheusRule`s are its input |
| A model API key | Gemini, or any OpenAI-compatible endpoint including a local Ollama |
| Grafana + `grafana-mcp` | optional; without it the agent degrades to Kubernetes-only reads |

`postgres.embedded.enabled=true` brings up a single-replica StatefulSet for evaluation. It is
explicitly **not** a production database.

## This chart creates no Secrets, ever

A value passed to a chart ends up in `helm get values`, in the release Secret, and in whatever
git repo holds your Argo Application — forever, and readable by anyone with `get` on Secrets in
that namespace. So every secret is *referenced* by name and each reference is wrapped in
`required`, which makes a missing one fail at template time rather than twenty minutes later as
`CreateContainerConfigError` on a pod nobody is watching yet.

```sh
kubectl create namespace hephaisto

kubectl -n hephaisto create secret generic hephaisto-postgres \
  --from-literal=POSTGRES_USER=hephaisto \
  --from-literal=POSTGRES_PASSWORD='...' \
  --from-literal=POSTGRES_DB=hephaisto \
  --from-literal=POSTGRES_APP_PASSWORD='...'

# Both keys are optional; which you need depends on Llm:Provider.
kubectl -n hephaisto create secret generic hephaisto-llm \
  --from-literal=GEMINI_API_KEY='...'

helm install hephaisto oci://ghcr.io/truerelevance/charts/hephaisto \
  -n hephaisto \
  --set cluster.name=<your-cluster-label> \
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release>
```

Two more are worth setting on day one. `secrets.webhookToken` names a Secret whose key `token`
the Alertmanager webhook then requires as a bearer token - Alertmanager sends it with
`http_config.authorization.credentials_file` on the receiver. And `llm.pricing`, if your model is
not in the built-in price table: with a cost cap set (they all are, by default) the agent refuses
to start on a model it cannot price, because an unpriced model bills as $0 and no cap would bind.

That last `--set` matters more than it looks: if your Prometheus does not select the shipped
`PrometheusRule`s, the agent detects nothing and reports itself perfectly healthy. `NOTES.txt`
prints the two commands that verify it, and they are worth running.

## Configuring it

`values.yaml` is densely commented and `values.schema.json` rejects invalid combinations at
template time rather than at runtime — including several that would look like a working install.

The agent binds far more configuration than the chart promotes to values: `Llm:Model`,
`Llm:Investigation:MaxSteps`, `Llm:Budget:MaxCostUsdPerHour` and the rest are settable as
`Section__Key` environment variables through `extraEnv`. That is deliberate — mirroring every
options class in YAML would duplicate them and drift the first time one is renamed.

## Routing alerts to it (v0.10.0)

Two routes, in this order, in the Alertmanager that sends to Hephaisto:

```yaml
route:
  routes:
    # The agent's own absence (alerts.agentPresence). An agent that is down cannot report
    # being down, so these go to something that is not the agent.
    - receiver: not-hephaisto
      matchers: [ hephaisto_route = "external" ]
      continue: false
  receiver: hephaisto
receivers:
  - name: hephaisto
    webhook_configs:
      - url: http://hephaisto.hephaisto:8080/webhooks/alertmanager
        send_resolved: true            # a resolve is what closes an incident
        http_config:
          authorization:
            credentials_file: /etc/alertmanager/secrets/hephaisto-webhook-token/token
  - name: not-hephaisto
    webhook_configs:
      - url: https://your-other-channel.example/hook
```

`send_resolved: true` is not optional any more: without it an incident closes only when a person
closes it. The token is `secrets.webhookToken`. The webhook answers 503 when it cannot write, and
Alertmanager retries - so a restart or a database outage costs minutes, not a lost alert.

## Code fixes (v0.9.0)

When an investigation's grounded primary finding is a bug in application code, nothing in the
cluster can fix it. With `codeFix.enabled`, Hephaisto hands that finding to a **coder** — Claude
Code in a Kubernetes Job of its own — which clones the repository the workload is mapped to and
writes a fix **plan**, read-only and automatically. After a human approves the plan, a second Job
implements it on a `hephaisto/codefix-*` branch, runs the build and tests, and opens a pull
request - a **draft**, unless the context repository's `repos.yaml` says
`defaults.pr.draft: unless-ready`. A human reviews, merges and deploys; nothing here does.

It ships **off, and unrendered**: `codeFix.enabled: false` renders no coder object at all, and
`codeFix.mode` (`off | plan | pr`) is its own axis, independent of the agent's `mode`. An
`Observe` agent can still plan code fixes — planning writes nothing anywhere — and the agent's kill
switch still stops the stage.

**What the coder can reach.** No cluster identity: its ServiceAccount is bound to nothing and its
token is never mounted. No Hephaisto credential and no inbound surface: it answers by printing a
framed result to its own log, which Hephaisto reads. The model runs in a container that is
handed its own credential and no GitHub or NuGet token: those go to an init container that has
ended before the model starts (`prepare`) and, for the push and the Draft PR, to one that starts
after it has ended (`publish`). Egress only through a squid allowlist proxy
(`codeFix.egressProxy`), enforced by a NetworkPolicy that is independent of the top-level one —
DNS and the proxy, nothing else — and every request it makes is a line in the proxy's log.

**What Hephaisto gains.** `create jobs`, in exactly one namespace. The chart refuses to render if
that namespace is `default`, `kube-*`, the release or observability namespace, or any
`policy.actionableNamespaces` entry, and the agent refuses to boot if it holds `create jobs`
anywhere else.

```sh
kubectl create namespace hephaisto-coder
# Every key optional. GITHUB_TOKEN: a FINE-GRAINED PAT, contents:write + pull_requests:write,
# limited to the mapped repositories. Hephaisto never reads this Secret.
kubectl -n hephaisto-coder create secret generic hephaisto-codefix \
  --from-literal=CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-... \
  --from-literal=GITHUB_TOKEN=github_pat_...

helm upgrade hephaisto oci://ghcr.io/truerelevance/charts/hephaisto -n hephaisto --reuse-values \
  --set codeFix.enabled=true --set codeFix.mode=plan \
  --set codeFix.contextRepository.url=https://github.com/you/dev-context \
  --set 'codeFix.repositories[0].workload=shop/Deployment/shop-api' \
  --set 'codeFix.repositories[0].url=https://github.com/you/shop'
```

Protect the default branch of every mapped repository before the first attempt — no direct push,
no force push, for everyone including the coder's token. It is the one control the chart cannot
check, and `NOTES.txt` lists the repositories it applies to. Start in `plan`, read the plans, and
move to `pr` only with `auth.enabled: true`: approving a repository write on an unauthenticated
click is refused at render time and again at startup.

## Investigating in a Job (v0.12.0)

With `investigation.job.enabled`, an investigation's **model loop** can run as Claude Code in a Job
beside the coder - on the same Secret's `CLAUDE_CODE_OAUTH_TOKEN`, with a model of your choice -
while Hephaisto keeps the tools, records every step and grounds the conclusion exactly as it does
in-process. The Job reaches evidence only through the agent's **investigator port**
(`investigation.job.port`, 8084, `/investigate`), admitted only from investigator pods, each run
with a token of its own. It has no cluster identity and no Grafana token.

It ships **off, and unrendered**, and enabling it switches nothing over: the executor
(`investigation.job.executor`, plus the switch ConfigMap's `investigationExecutor`, most
restrictive winning) defaults to `inprocess`. It needs `codeFix.enabled` - the Job borrows the
coder's namespace, image, Secret and proxy - but no code-fix mode. A run that cannot have one of
`concurrentJobs` slots, or would exceed `jobsPerHour`, runs in-process at once; a Job that fails,
vanishes or hits a subscription limit is replaced by the in-process investigation.

```sh
helm upgrade hephaisto oci://ghcr.io/truerelevance/charts/hephaisto -n hephaisto --reuse-values \
  --set investigation.job.enabled=true --set investigation.job.executor=job \
  --set investigation.job.model=opus
# and back, without a rollout:
kubectl -n hephaisto patch cm hephaisto-switches --type merge -p '{"data":{"investigationExecutor":"inprocess"}}'
```

`investigation.job.source.enabled` also gives the investigator a read-only clone of a mapped
workload's repository at its running commit, so a finding can name file and line - shown on the
finding and passed to a code fix, never counted as evidence.

## GitHub issues as work (v0.14.0)

With `github.enabled`, an issue **assigned to Hephaisto's account**, in a repository listed in
`github.issues.repositories`, is taken as work. The agent asks GitHub - one poll per repository
every `github.pollInterval`, answered with a free 304 while nothing changed - so nothing has to
reach in and there is no webhook to expose. A taken issue is planned by a coder Job under
`codeFix.mode` and every `codeFix` cap; the plan is posted on the issue; an approver answers it
there; and the pull request the implementing Job opens - a draft, whose description says
`Closes owner/repo#n` - is followed until it is merged (the work item is done) or closed.
`GET /api/workitems` shows them, and `github` is among the connections of `GET /api/status` and
the MCP tool `get_status`. Unassigning the account, or closing the issue, takes it back at any
point and stops a running Job.

It ships **off, and unrendered**. The account is an ordinary GitHub account (a machine user) and
not a GitHub App, because an App cannot be an assignee; make it a collaborator on every listed
repository. Its token is the **agent's own**, in a Secret of the release namespace - never
`secrets.codeFix`, which lives in the coder namespace, may push, and stays unreadable to the
agent. The chart refuses the same name for both.

```sh
# Signed in as the bot account. Organisation repositories: a fine-grained token limited to the
# listed repositories - Issues read and write, Pull requests read. A repository a USER owns is
# out of a fine-grained token's reach for a collaborator; that needs a classic token (`repo`).
kubectl -n hephaisto create secret generic hephaisto-github --from-literal=GITHUB_TOKEN=github_pat_...

helm upgrade hephaisto oci://ghcr.io/truerelevance/charts/hephaisto -n hephaisto --reuse-values \
  --set github.enabled=true --set secrets.github=hephaisto-github \
  --set 'github.issues.repositories[0]=you/shop' \
  --set 'github.approvers[0]=1234567'        # gh api users/<login> --jq .id
```

`github.approvers` is who may answer a plan on the issue, by account **number** - a login can be
renamed and taken by somebody else. An approver replies with a comment whose first line is
`/approve`, or `/reject` and a reason; what is approved is the plan as Hephaisto stored it, never
the comment's text. Anybody else's `/approve` changes nothing and is answered once. With
`codeFix.mode` at `plan` or `off` an approval is refused and the issue is told which. Hephaisto
writes at most six comments for one issue it was handed: where the work stands (edited in place),
the plan, and a few one-time answers. With the list empty nobody can answer by comment and the
plan is approved in the console. **Whoever is listed can make Hephaisto push a branch** to the
listed repositories: list maintainers.

An issue whose pull request was merged or closed is not started over while it simply stays
assigned: unassign the account and assign it again (or close and reopen the issue) to hand it
back.

**Where else it shows.** The console lists what was taken under *work items*, and every attempt
has a page of its own (`/codefixes/<attempt id>`) with its history, the plan in full, and approve
and deny for the approver role. The MCP tools `list_code_fixes` and `get_code_fix` include an
issue's attempt, and `list_work_items` and `get_work_item` read the work items; no tool answers a
plan. And a work item's code fix is announced through `notifications.routes` like an incident's
(`CodeFixPlanReady`, `CodeFixPrOpened`, `CodeFixFailed`) - to a route that is **not scoped** by
namespace, cluster, kind or label and asks for no severity above `Info`, because an issue has
none of those; a fallback route is for incidents and does not take it. The Teams board stays a
board of incidents.

**The planner** (`github.planner`, v0.15.0, off by default) is a page of the console over ONE
project of an organisation: its open issues one at a time, the day's plan, and everything open
under its top-level issue. It reads the project when somebody opens the page and writes only on a
click of the approver role - a single-select field, close, the labels `today` and `not today`, an
assignee - through the agent's token, recorded under the person's name.

```yaml
github:
  planner:
    enabled: true
    owner: example        # github.com/orgs/<owner>/projects/<number>
    number: 3
    requiredFields: [Priority]
    staleAfterDays: 5
    assignees: [octocat]
```

It needs more of the agent's token than issues as work: the organisation's projects (classic
`project`; fine-grained "Projects: read and write") and issues read and write on every repository
whose issues are on the board. Measured with a classic token (`scripts/e2e/planner-live.sh`); the
fine-grained permissions are GitHub's documentation.

**What it takes on github.com**, as measured against it (`scripts/e2e/github-live.sh`), not as
read in its documentation:

- The agent's token above is enough: with Issues and Pull requests it can also ask whose token
  it is and what the repository's default branch is, and set and list the reactions on its
  plan comment. It cannot push, and should not be able to.
- The coder's token (`secrets.codeFix`, key `GITHUB_TOKEN`): Contents read and write, Pull
  requests read and write. That clones, pushes the one branch, opens the **draft** pull request
  and assigns it. A plan without draft pull requests in private repositories refuses the
  `--draft`, and Hephaisto opens nothing else (GitHub's documentation; not measured).
- The account has to be **assignable** in the repository - a member or collaborator with write.
  GitHub answers an assignment it ignores with success.
- A **label** the context repository names for pull requests (`defaults.pr.labels` in its
  `repos.yaml`) has to exist in the target repository. `gh` refuses one that does not; the pull
  request is then opened without it, and the attempt's `deviations` say so.
- Text a model or a stranger wrote is posted with a zero-width space wherever GitHub would act
  on it - after `@`, `#`, `GH-`, inside `://`, after every `/` before a digit - so a copy of an
  address or a path out of a plan comment or a pull request's description carries that
  character.

**Egress.** With `codeFix.egressProxy` rendered, the agent's GitHub calls go through that proxy
(`github.useEgressProxy`, on by default): `api.github.com` is already on its allowlist, its log
then shows the agent's requests beside the coder's, and the chart adds the two NetworkPolicy
rules that needs - the agent may reach the proxy's pods on 3128, and the proxy admits the agent's
pods. Without the proxy and with `networkPolicy.egress.enabled`, GitHub has to be reachable
through `networkPolicy.egress.extraEgressCIDRs` on 443. With the agent's `mode: Off` nothing is
asked and nothing is taken.

## Try it without a cluster

The published image can run with no Kubernetes behind it at all, loaded with recorded
investigations from a real cluster:

```sh
curl -fsSL https://raw.githubusercontent.com/TrueRelevance/hephaisto/main/demo/compose.yaml \
  | docker compose -f - up
```

## Links

- [Source, and the documentation](https://github.com/TrueRelevance/hephaisto)
- [What is known to be broken](https://github.com/TrueRelevance/hephaisto/issues)
- [How it is verified](https://github.com/TrueRelevance/hephaisto/blob/main/docs/verification.md)

Licensed AGPL-3.0-only.
