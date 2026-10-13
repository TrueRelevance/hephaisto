# Hephaisto

An autonomous SRE agent that lives in your Kubernetes cluster. It receives Alertmanager
webhooks, investigates what is wrong using PromQL, LogQL, traces and the Kubernetes API,
and writes up a diagnosis with the evidence it used.

It is also a first-class *producer* of telemetry: every investigation is a trace you can
open in Grafana, step through, and then ask the agent about.

> **Status: v0.6.0.** Multi-arch images and Helm charts are published to GHCR with build
> provenance attested, and both are pullable anonymously.
>
> **Diagnosis is measured.** Against ten seeded chaos scenarios on a real cluster, the agent
> identified the correct root cause in **8 of 10** — the project's stated bar is ≥ 7/10 over
> ≥ 10 scenarios. The full end-to-end gate runs ten fixtures in 98 minutes for $0.115 against a
> local `gpt-oss-120b`; it can also be driven by Gemini, DeepSeek, OpenRouter or any
> OpenAI-compatible endpoint.
>
> **Acting has been demonstrated end to end, once.** On `c13-wedged-lock`, the agent diagnosed a
> stale startup lock, proposed a `RestartPod`, had it admitted by the policy engine, executed it,
> saw the workload available in 16s, and the incident reached `Resolved` **41 seconds** later,
> granted by `hephaisto/verifier` — 70 assertions, on 2026-09-02
> ([#72](docs/backlog.md#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever)).
> That is one run. The executor, the policy engine, verification and rollback are also unit- and
> integration-tested.
>
> **Whether it proposes an action is a property of the fixture and the model, and there is no
> such thing as "the action rate".** On c13 — a lock on an `emptyDir`, which the planning prompt
> already names as pod-scoped state a replacement does not reproduce — `gpt-oss:120b` proposed an
> acceptable action on **6 of 6** cluster runs; n = 6 supports "usually", not "always". On
> `c12-stale-lease`, the same fault with the state on a PVC so that acting means overriding a
> *correct* rule, it proposed one in **1 of 11 runs where the planner actually ran**, against
> `deepseek-v4-flash`'s **4 of 8** (Fisher p = 0.11). The **p = 0.0047** published earlier was
> computed over 24 runs, nine of which ended on a budget before the planner ran; it did not
> survive counting the denominator honestly
> ([#88](docs/backlog.md#88-noplan-pools-four-outcomes-and-the-action-rate-counted-all-four-as-declines)).
> **The two fixture numbers must not be averaged** — c13 measures willingness to act, c12
> measures an inference `gpt-oss:120b` gets wrong 7 times in 9
> ([#66](docs/backlog.md#66-the-planner-acts-on-half-of-a-fair-fixture)).
>
> It ships configured to act **nowhere**. `policy.actionableNamespaces` is empty,
> `policy.autoEnabledActionTypes` is empty, and `mode` is `Observe` — three independent
> things you have to change before anything can happen. Do not point it at anything you
> care about.

## What it looks like

![The incident list: one incident the agent fixed, one policy refused, and ten it diagnosed and
escalated](design/shots/console-incidents-all.png)

The top two rows are the ones worth reading. `+ resolved` is an incident the agent acted on and
closed; `^ escalated / PolicyDenied` is the same fault on the same workload, diagnosed just as
correctly, where a safety gate refused the fix. The ten below are `NoPlanProduced` — diagnosed,
and no action proposed.

<details>
<summary>The incident it fixed, and the one it was refused</summary>

![An incident the agent acted on, verified and closed](design/shots/console-incident-resolved.png)

![An incident it diagnosed and was refused by policy](design/shots/console-incident-denied.png)

</details>

Photographed from the shipping console by `scripts/console-shots.sh`, against the published image
running `demo/compose.yaml`. They are regenerated, never retouched.

## See it without a cluster

Twelve real investigations, rendered as static pages: **<https://demo.hephaisto.dev>**. No install, no
account, nothing to run.

To drive the real console instead — two containers, no API key, no Kubernetes, nothing fetched at
runtime:

```sh
curl -fsSL https://raw.githubusercontent.com/TrueRelevance/hephaisto/main/demo/compose.yaml \
  | docker compose -f - up
# then open http://localhost:8080
```

You get the real published image, the real schema and the real console, loaded with **ten
investigations the agent actually ran** against a k3s cluster full of seeded faults — the step
trace, the diagnosis, and every evidence excerpt linking back to the untruncated tool output it
came from.

Each incident's timeline says, in its first entry, where it came from, which model investigated
it and how it was graded. Ten are replays of recorded tool traces, and nine of those ten were
graded correct; the tenth is in there too, labelled, because a demo showing only the ones that
worked would be a different claim from the one this project publishes. The other two are live
captures from a real cluster run - one the agent restarted and saw through to `Resolved`, and
one where policy refused the same fix - and neither is scored against the answer key, because a
replay and a cluster run are not the same measurement.

**What it cannot show you:** the agent is connected to nothing, so it detects nothing and the
executor refuses every action. It is the product's output, not the product running.

## What it does

```
Alertmanager ──▶ ingest ──▶ dedup + correlate ──▶ incident
                                                    │
                                                    ▼
                                          investigation loop
                                    (read-only tools, budget-capped)
                                                    │
                                                    ▼
                                  diagnosis + evidence + proposed plan
                                                    │
                                                    ▼
                                            policy engine
                                            (default-deny)
                                                    │
                              ┌─────────────────────┼─────────────────────┐
                              ▼                     ▼                     ▼
                          escalate            await a human            execute
                         to a human            (approve)         (closed action enum)
                                                    │                     │
                                                    └──────────┬──────────┘
                                                               ▼
                                              verify at T+60s / T+5m / T+15m
                                                               │
                                                    ┌──────────┴──────────┐
                                                    ▼                     ▼
                                                resolved          revert / escalate
```

An investigation is a bounded agentic loop. The model gets read-only tools — Kubernetes
reads, PromQL, LogQL, trace search — and a step, token, cost and wall-clock budget. It
ends by calling `conclude` with a root cause, the evidence it relied on, and a confidence.
Everything it did is persisted as a step trace you can replay in the UI.

## What exists today

Being precise about this matters, because the difference is the whole safety argument.

| | |
|---|---|
| Alertmanager ingest, dedup, correlation, suppression | **works** |
| Investigation loop with read-only tools, budgets, step trace | **works** |
| Diagnosis, evidence, semantic incident search | **works** |
| Policy engine — default-deny, pure, exhaustively unit-tested | **works** |
| Kill switch — three independent arms, most restrictive wins | **works** |
| Audit log, budgets, cooldowns, oscillation detection | **works** |
| Plan generation (schema-constrained, no tools) | **works** |
| Executing a plan against the cluster | observed on a cluster, five action types — see below |
| Verification at T+60s / T+5m / T+15m, and rollback | observed closing an incident once, on one fixture |
| Approval workflow — UI and API | **works** |
| Oscillation detection wired to a workload quarantine | **works** |
| `SilenceAlert` — always requiring approval | built, needs Alertmanager configured |
| Outbound notifications: webhook and Teams, over a Postgres outbox | **works** |
| A Teams bot: one board edited in place, alerts by personal chat | **works against a stand-in**; not yet run against Teams ([#125](docs/backlog.md#125)) |
| `RollbackDeployment`, `PatchResources` | not built — refused, not attempted |
| Runbook memory, OIDC approval identity, in-card approval | not built |
| A written design language, one token set, visual regression baselines | **works** |
| An MCP endpoint a coding agent or an MCP gateway can ask about incidents | **works**, against a gateway with tool search and a real model locally |
| A GitHub issue assigned to Hephaisto's account: planned, the plan answered on the issue, a draft pull request that closes it (v0.14.0) | **works against a stand-in, and against a sandbox repository on github.com** with a scripted model; no real model has planned an issue in a test, and nothing has merged one of its pull requests on github.com |

**The wording of each row is chosen, not casual.** Detection, investigation and diagnosis are
measured against a real cluster over ten seeded scenarios. The delivery path was measured in
v0.3.0, including the assertion it exists for: the receiver taken down, the agent restarted
mid-flight, the receiver brought back, and the delivery arriving anyway.

The acting path is the one to read carefully. The last step — an incident the agent acted on
reaching `Resolved` — was unobserved for four releases, because until v0.5.0 a `RestartPod`
could not be verified at all and after that the resolve transition could not commit
([#72](docs/backlog.md#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever)).
It was confirmed on a cluster on 2026-09-02: `Detected → Triaging → Investigating → Acting →
Verifying → Resolved`, 41 seconds after the restart. **That is one run on one fixture**, and it
took five layered bugs to reach — four of them real, and the fifth an assertion that could never
pass, which had agreed with the other four for long enough that nobody questioned it.

Whether the planner proposes an action is separately measured, and is a property of the
**fixture** as much as the model. Do not average the two numbers: on `c13-wedged-lock`
`gpt-oss:120b` proposed an acceptable action 6 of 6 times, and on `c12-stale-lease` — where
acting means overriding the correct rule that PVC contents survive a pod replacement — 1 of 11
times where the planner ran, against `deepseek-v4-flash`'s 4 of 8
([#66](docs/backlog.md#66-the-planner-acts-on-half-of-a-fair-fixture),
[#90](docs/backlog.md#90-the-acting-path-had-no-fixture-that-could-measure-it)). c13 measures
willingness to act; c12 measures an inference. If you need the agent to propose remediations on
the harder shape, that is a model-selection decision.

`docs/roadmap.md` says where the project stands, and the [issues](https://github.com/TrueRelevance/hephaisto/issues) have everything known to
be broken; `docs/backlog.md` is the frozen list they were moved from.

The executor covers exactly the verbs the write `Role` grants: `RestartPod`,
`RolloutRestart`, `ScaleWorkload`, `DeleteStuckJob` and `DeleteFailedJobPods`. Anything
else is **refused before a call is made**, with `outcome=unsupported` and nothing
attempted — which for `CordonNode` and `DrainNode` is the honest answer, because their
`ClusterRole` ships deliberately unbound. `SilenceAlert` needs an outbound client bound to
Alertmanager: it shipped with v0.3.0's notification stack, and is refused before any call is made
when no Alertmanager URL is configured.

The machinery that *gates* an action was built and tested a full release ahead of the
action itself, deliberately — the policy engine is the argument that auto-remediation would
be safe, and it should exist and be trusted before anything can act.

## The safety model

The design target is **L3**: auto-execute a narrow allowlist of low-risk, reversible
actions in allowlisted namespaces, budget-capped, with automatic rollback. Everything else
escalates to a human. Five things are meant to make that defensible.

**1. RBAC is the hard floor.** Read access is cluster-wide. Write access is a `Role` bound
into specific namespaces and nowhere else. **No access to Secrets at all, ever.** The
cordon/drain `ClusterRole` ships *unbound* — binding it is an explicit, separate human act.
No amount of prompt injection changes what a ServiceAccount is allowed to do.

**2. No model in this process ever holds a mutating handle to the cluster.** The three
phases are separated on purpose:

- *investigate* — read-only tools only
- *plan* — **no tools at all**; emits JSON against a fixed schema
- *execute* — pure C# over a closed `ActionType` enum

A prompt injection in a log line can, at its very best, produce a plan that the policy
engine then rejects. It cannot reach an API call directly.

**3. The policy engine is pure and default-deny.** It is a function over facts passed in by
the caller — no I/O — which is exactly what makes it exhaustively unit-testable. An empty
namespace allowlist means *act nowhere*, and that is the default.

**4. Every auto action is verified** at T+60s / T+5m / T+15m by deterministic C# predicates —
never by a model, and the state machine refuses a model identity as the granter of a
`Resolved`. The three checks answer different questions rather than retrying one, so only the
last may conclude a failure: a pod still pulling its image at T+60s is not a fault, and
reverting on it would make the agent the cause of the next incident.

On a final failure the action is reverted where a revert exists, and escalated where one
does not. Two honest limits there. The rollback spec is written by the model, so it is read
for typed values and never executed as written — the revert is built as an ordinary action
over the same closed enum, and today only `ScaleWorkload` has an inverse that can be
expressed. And a pod delete has no inverse at all: the controller recreates the pod, which
*is* the restart, so the recourse on a failed verification is escalation rather than undo.
That is why the policy engine exempts self-healing actions from needing a rollback spec
instead of accepting a fictional one.

**5. Budgets, cooldowns and oscillation detection** cap the worst sustained case at roughly
ten pod restarts an hour — indistinguishable from a badly tuned HPA.

**6. Code fixes are a second, separately gated stage (v0.9.0).** A coder — Claude Code in a
Kubernetes Job — *does* hold a shell, which is why invariant 2 says "to the cluster" and why the
coder is built to have nothing else: no ServiceAccount token on a ServiceAccount bound to nothing,
no Hephaisto credential and no inbound surface (it answers through a framed block in its own log),
egress only through an allowlist proxy, a non-root read-only pod. Its only mutation target is an
assigned `hephaisto/codefix-*` branch and a **Draft** PR, and only after a human approves the plan
— which needs `codeFix.mode: pr` *and* an authenticated login. `create jobs` is granted in the
coder namespace and nowhere else; the agent refuses to start if it holds it anywhere else. The mode
is its own axis (`Off`/`Plan`/`Pr`, silence is `Off`), and the kill switch overrides it: `Off`,
the emergency stop and the runaway latch all stop coders.

### Two invariants that must never be weakened

- **No audit, no action.** If Postgres is unreachable, the executor refuses to act. An
  unreadable mode ConfigMap is read as `Observe`, never as `Auto`.
- **The budget check, the cooldown check, the kill-switch check and the action INSERT are
  one transaction.** This is why the agent is a single pod with `strategy: Recreate`. Split
  them across replicas and it becomes a distributed TOCTOU race on the one code path where
  losing the race means an unintended `kubectl delete`.

### The kill switch

Three independent arms — an environment variable, a projected ConfigMap, and a database row
— and **the most restrictive one wins**. Modes are `Off` (ingest nothing, investigate
nothing), `Observe` (never mutate), `DryRun` (run the whole flow, but every Kubernetes call
carries `dryRun=All`), and `Auto`.

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Off
```

### Approval identity is attribution, not authentication

`ApprovedBy` is free text typed into the UI. On a single-operator cluster that is an
acceptable trade, and the schema is already OIDC-shaped so the upgrade populates the same
string from a verified claim. **The risk to watch is habituation.** If this ever runs
somewhere with more than one operator, OIDC stops being a roadmap item and becomes a
blocker.

## Requirements

- A Kubernetes cluster and a Prometheus/Alertmanager stack that can POST to a webhook
- **PostgreSQL 17 with `pgvector`** — the agent fails fast without it, on purpose
- A model API key. `Llm:Provider` selects `gemini` or `openai` — the latter being the wire
  format rather than the vendor, so DeepSeek, OpenRouter and a local Ollama or LM Studio
  server are all reached through it by setting `Llm:Endpoint`. Embeddings are configured
  separately (`Llm:EmbeddingProvider`), so a fully self-hosted install can keep the semantic
  arm of search by pointing at any endpoint serving `/v1/embeddings` — no external account.
  Without one, search falls back to its lexical arm and says so
- Optionally Grafana + `grafana-mcp`, which is what gives the agent PromQL, LogQL and trace
  tools. Without it the agent degrades to Kubernetes-only reads and says so in its logs. The
  tool names are those of mcp-grafana 2.0.1 (chart `grafana-mcp` 0.27.1); an older server
  shows as `Degraded` in `/api/status`, with the names it does not offer.

## Running it

Multi-arch images and the Helm chart are published to GHCR on every release tag, with build
provenance attested, and both are pullable anonymously:

```sh
helm install hephaisto oci://ghcr.io/truerelevance/charts/hephaisto \
  --namespace hephaisto --create-namespace
```

That resolves the newest published chart. Add `--version 0.6.0` to pin one; the chart version
and the app version are the same number, set by the tag.

Installed as it ships, the agent acts nowhere: `policy.actionableNamespaces` is empty, so no
write `Role` is rendered at all, and `mode` is `Observe`. Enabling anything means naming a
namespace, labelling that namespace
`hephaisto.dev/destructive-actions-allowed: "true"`, promoting an action type into
`policy.autoEnabledActionTypes`, and raising `mode` — four deliberate acts, in git.

You can also build from source.

**On a laptop:**

```sh
git clone https://github.com/TrueRelevance/hephaisto
cd hephaisto

./scripts/dev-db.sh up          # throwaway Postgres 17 + pgvector on :5433
dotnet run --project src/Hephaisto.AppHost
```

`Hephaisto.AppHost` is .NET Aspire, and it is **dev-time only** — it is excluded from the
container image and no manifest references it.

**In a cluster**, via the chart in `charts/hephaisto`:

```sh
# The chart creates no Secrets, ever. Make them first.
kubectl create namespace hephaisto
kubectl -n hephaisto create secret generic hephaisto-postgres \
  --from-literal=POSTGRES_USER=hephaisto \
  --from-literal=POSTGRES_PASSWORD="$(openssl rand -base64 24)" \
  --from-literal=POSTGRES_DB=hephaisto
# Both keys are optional; which one you need depends on Llm:Provider. GEMINI_API_KEY serves
# the gemini provider and, on any provider, the embedding generator. LLM_API_KEY serves the
# openai-compatible one.
kubectl -n hephaisto create secret generic hephaisto-llm --from-literal=GEMINI_API_KEY=...

helm install hephaisto ./charts/hephaisto -n hephaisto \
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release> \
  --set postgres.embedded.enabled=true
```

Then read what `NOTES.txt` prints, and actually run the two checks it gives you. The chart's
most dangerous setting is `prometheusOperator.selectorLabels`: get it wrong and every object
is created, `kubectl get prometheusrule` shows them all present, Prometheus selects none of
them, and the agent reports itself perfectly healthy while seeing nothing at all.

What the chart installs: the agent, its RBAC, both NetworkPolicies, the PodMonitor, the alert
rules, a Grafana dashboard, and — opt-in — an evaluation Postgres. What it does **not**
install: Prometheus, Alertmanager, Grafana, Loki, Tempo or a collector. You already run those.

Three defaults worth knowing before you install:

- `policy.actionableNamespaces` is **empty**, so no write Role is created at all and the agent
  may act nowhere. Naming a `kube-*` namespace, `default`, its own namespace or the
  observability namespace is a hard render failure, not a dropped entry.
- `networkPolicy.extraIngressCIDRs` is **empty**. It is sometimes the only way to keep kubelet
  probes working — but every CIDR you add can forge an alert to an unauthenticated,
  incident-creating endpoint.
- The cordon/drain ClusterRole is created **unbound**, and no value binds it. That stays a
  hand-written `ClusterRoleBinding` in its own commit.

`charts/hephaisto/ci/negative-tests.sh` asserts all of the above as tests, so they cannot rot
quietly.

`infra/` still holds the hand-written manifests this repo's own dev cluster runs. They are
hand-written rather than generated because this pod's ServiceAccount, ClusterRole and
RoleBindings *are* the security boundary and must be reviewable human diffs.
`infra/app/rbac.yaml` is the most valuable prose in the repo.

### Configuration

Standard .NET configuration, so every key below is also an environment variable with `__`
for `:`.

| Section | What it holds |
|---|---|
| `Persistence:ConnectionString` | Postgres. Required — startup fails without it. |
| `Llm:Provider`, `Llm:Model` | `gemini` or `openai`, and the investigating model. Changing either needs a pod restart, not just a ConfigMap edit — the factory captures them at construction. |
| `Llm:Endpoint` | Which OpenAI-compatible server `Provider=openai` talks to. Address only, not wire format: setting it under `Provider=gemini` retargets the Gemini transport rather than switching protocol. |
| `Llm:PlanningStructuredOutput` | `JsonSchema` (default) or `JsonObject`. A provider capability, not a preference — see below. |
| `Llm:Pricing` | Model id to price. **An unpriced model is charged at zero**, which switches the cost budget off rather than approximating it. Ship a price with the model, always. |
| `Llm:Budget` | Global rolling token/cost windows, counted in Postgres |
| `Investigation` | Per-run step, token, cost and wall-clock budgets |
| `Policy` | `AllowedNamespaces`, protected namespaces and labels, rate caps |
| `Grafana:McpUrl` | grafana-mcp, for PromQL/LogQL tools |
| `Notifications` | Outbound delivery: channels, routing rules, the outbox's retry schedule |
| `Alertmanager:Url` | Write-only, and the only thing written is a silence |
| `KillSwitch` | Arm configuration; env arm defaults to `HEPHAISTO_MODE` |

**On `Llm:PlanningStructuredOutput`.** Phase 2 asks the provider to constrain the plan to a JSON
schema. Not every provider can: DeepSeek answers `400 "This response_format type is unavailable
now"`, and because phase 1 is unaffected the agent then diagnoses correctly and proposes nothing —
which is indistinguishable, in a run summary, from an agent that considered acting and declined.
Set `JsonObject` for such a provider and the schema moves into the prompt instead. It is the
weaker mode and stays off by default: the shape becomes a request rather than a constraint, which
is safe only because nothing downstream takes the model's word for it — every cited finding id is
verified, and an action missing a namespace, kind or name is dropped before an executor sees it.

`Policy:AllowedNamespaces` defaults to **empty**, which means the agent may act nowhere.
`Policy:ProtectedNamespaces` is never actionable whatever the allowlist says, and includes
Hephaisto's own namespace and the observability stack — a self-inflicted outage would also
blind the agent to the fact that it caused one.

### Notifications

Until v0.3.0 nothing left the process. An escalation was a database row, an audit row and a
nudge to any browser tab that happened to be open — and if nobody was looking, nobody was told.

**It still ships that way.** `notifications.routes` is empty and no channel is configured, so a
stock install delivers nowhere. Two independent things have to change, in the same spirit as
`policy.actionableNamespaces` and `mode`.

| | |
|---|---|
| Channels | A generic outbound HTTP endpoint (optionally HMAC-signed); Microsoft Teams via a Power Automate Workflows trigger; Microsoft Teams via a registered bot |
| Events | `IncidentEscalated`, `ApprovalRequired`, `IncidentResolved`, `VerificationFailed`, `ModeChanged`, `PolicyChanged` |
| Routing | Per event, minimum severity and namespace. Additive only — there is no deny rule |
| Delivery | A Postgres outbox with exponential backoff and jitter. **The delivery row and the state transition that caused it are written by one `SaveChangesAsync`** |
| Rate limiting | Per-channel hourly cap, plus a per-workload cooldown. The first message for a workload always goes out; the repeats are suppressed and counted |

The outbox is the point. `IIncidentNotifier` is an in-process channel that drops on overflow by
design — right for nudging a browser, catastrophic for telling somebody the agent gave up. An
incident cannot reach `Escalated` without a delivery row existing, because both are written in
the same transaction, so a pod restart cannot lose the news.

**The Teams bot keeps one board, and never deletes.** A Workflows trigger can post and nothing
else, so every event is a new card and nothing leaves the channel. The bot
(`notifications.teamsBot`) can edit what it posted: the channel holds a single message listing
every open incident, a closed incident disappears from it, and an alert is a personal chat message
that is later edited to say how the incident ended. Deleting is not used at all - Teams leaves
"This message has been deleted." behind, for a post and for a reply alike, and nothing switches
that off.

**A Teams card carries a link, not an Approve button, until you ask for one.** Approving in-card
means accepting a Microsoft Entra identity as an approver, whose role lives in your own identity
provider. The link goes to Hephaisto's own approval UI, where the audit row already lives. Other
buttons do act, when `notifications.teamsBot.actions.enabled` is set. **Acknowledge**, **Assign to
me** and **Reinvestigate** are read-level acts in the console too, and any member of the team may
click them. **Close** asks for a reason and takes an approver, as it does in the console - and
because the click arrives as a Microsoft Entra identity, who is an approver is said explicitly:
`notifications.teamsBot.actions.approvers` lists Entra object ids, and is empty until you fill it.
**Approve** and **Deny** take the same approvers and a switch of their own,
`notifications.teamsBot.actions.approvals.enabled`, off by default: the card then names the
proposed action, its target, its arguments and its risk above the two buttons, and an approval
runs exactly as one given in the console does. Everybody sees the same card, so every check is
made at the click, and somebody who may not close or approve is told so. Microsoft delivers the
click to `POST /api/teams/messages`, the one inbound route it
calls, on a port of its own where nothing else answers; the request must carry a Bot Framework
token for this bot, signed by a key endorsed for Teams, from this tenant, by a member of the team,
and the click is recorded as the team's member list names that person.

### HTTP surface

| | |
|---|---|
| `POST /webhooks/alertmanager` | Alertmanager receiver |
| `POST /webhooks/watchdog` | dead-man's-switch receiver |
| `GET /api/incidents`, `/{id}` | list and read incidents |
| `GET /api/incidents/search?q=` | semantic search over incidents |
| `POST /api/incidents/{id}/reinvestigate` | re-drive an incident's investigation |
| `POST /api/incidents/{id}/feedback` | mark a diagnosis right or wrong |
| `GET /api/status` | mode, budgets, kill-switch arms |
| `GET /api/version` | the running version and commit; touches no database |
| `GET /api/codefixes`, `/counts`, `/mode` | code-fix attempts - an incident's, or one for a GitHub issue (`workItemId`, `issue`, `issueUrl`; `incidentId` is then null) - the running and waiting counts, the code-fix mode |
| `GET /api/codefixes/{attemptId}` | one attempt by its own id, with what it is for: `attempt`, `workItem` (null for an incident's), `mode`, and `attempts` - every attempt of that work item, oldest first, since an issue can be planned again. An attempt carries `questions` beside `notes`: what its planner asks of a person. `decidedThrough` on an attempt says through what its plan was answered: `Ui`, `Api`, `Oidc`, `GitHub` |
| `GET /api/incidents/{id}/codefix` | an incident's code-fix attempts and its latest verdict |
| `POST /api/incidents/{id}/codefix` | ask for a code fix (approver policy) |
| `POST /api/incidents/{id}/codefix/{attemptId}/approve`, `/deny` | decide on a plan (approver policy; approve needs mode `Pr`) |
| `GET /api/workitems?state=Taken\|Done\|Cancelled\|any`, `/api/workitems/{id}` | GitHub issues taken as work (v0.14.0): without `state`, what is taken now; one work item carries its `attempts`, newest first, and `replanRequestedBy` while a new plan was asked for and has not started |
| `POST /api/workitems/{id}/codefix/{attemptId}/approve`, `/deny` | decide on the plan for an issue (approver policy; approve needs mode `Pr`) - the incident routes' body and refusals. The same plan can be answered on the issue itself, by an account listed in `github.approvers`: a comment whose first line is `/approve`, `/reject <reason>` or - to have the issue planned again with the comments written since - `/replan` |
| `GET /api/planner` | the planner (v0.15.0): `state` (`Off`, `Read`, `Unreadable`), the board's single-select `fields`, and its open issues in the planner's order with why each asks for a decision, its day, its fields and the pull requests that name it. `?refresh=true` asks GitHub now |
| `POST /api/planner/items/{itemId}/field`, `/close`, `/day`, `/assignee` | one click of the planner (approver policy): set a field to an option, close an issue as done or not planned, plan it for today or put it away, assign it. Done on GitHub by the agent's account, recorded under `decidedBy` or the signed-in name |
| `GET /api/alerts/{name}/note` | what people wrote about an alert name, and what was done each time |
| `POST /api/alerts/{name}/note/entries` | add a line of what was done this time |
| `PUT /api/alerts/{name}/note` | rewrite the note the agent reads beside its runbook (approver policy) |
| `GET /healthz`, `/readyz`, `/metrics` | health and Prometheus metrics |
| `POST /api/teams/messages` | a click on one of a Teams alert's buttons that act (acknowledge, assign to me, reinvestigate; close for a mapped approver; approve and deny for one too, behind their own switch); off by default, its own port, a Bot Framework token |
| `/` | Blazor Server UI |

v0.9.0 added the code-fix rows, the first new inbound routes since v0.3.0 — all on the console
port, all behind the console's auth, and the two that write behind the approver policy. The coder
itself calls none of them: it has no route in, by design. v0.10.0 added the one route Microsoft
calls, for the Teams buttons that act - off by default, on its own port, and authenticated by a
Bot Framework token rather than by the console's identity provider.

**The Alertmanager webhook is outside OIDC** (Alertmanager cannot sign in). It takes a bearer
token instead, when `secrets.webhookToken` names one - Alertmanager sends it with
`http_config.authorization` - and it is protected by a NetworkPolicy. Without a token that
NetworkPolicy is its *entire* protection. Set the token, and get that policy right.

## Alert rules must declare a real `hephaisto_kind`

Every `PrometheusRule` carries a `hephaisto_kind` label whose value must be a member of
`Hephaisto.Core.Domain.SignalKind`. That label is how an alert selects the runbook the model
is given.

`Enum.TryParse` fails **silently** on anything else, and the classifier then falls back to
guessing from the alert name — which for something like `KubeContainerWaiting` yields
`Unknown` and the default runbook instead of the image-pull one. Nothing about that is
visible from either side: the YAML looks well-labelled and the classifier looks correct.
`ShippedAlertRulesTests` reads the real rule files and fails if any alert declares a kind
that does not parse, or classifies as `Unknown`.

## Versioning

The git tag is the only source of truth. There is no `version.json` and no `<VersionPrefix>`
to bump: [MinVer](https://github.com/adamralph/MinVer) asks git, so a release is `git tag
v0.0.1` and nothing else.

```
tag v0.0.1-rc1      ->  0.0.1-rc1
  + 2 commits       ->  0.0.1-rc1.2
tag v0.0.1          ->  0.0.1
main, 42 commits    ->  0.0.2-main.0.42        (+<commit> as build metadata)
```

The same number is the image tag, the chart `version` and the chart `appVersion`, because
the chart ships exactly one application and both are published from the same tag. A
chart-only fix is `git tag v0.0.2`; patch tags are free.

Two things worth knowing:

- **`dotnet minver -t v -p main.0 -m <floor>`** — neither flag is optional. `minver-cli` does
  not read `Directory.Build.props`, so it silently substitutes its own defaults: without `-p`
  it says `alpha.0` where MSBuild says `main.0`, and without `-m` it cannot see
  `MinVerMinimumMajorMinor` and keeps incrementing the patch from the last tag. Either way the
  CLI and the compiler disagree about what the same commit is called, and the image tag stops
  matching the assembly inside it. Every script here reads the floor out of the props file
  rather than repeating it.
- **`.dockerignore` excludes `.git/`**, correctly, so MinVer cannot run inside a docker
  build. The version is computed once outside and passed in as `--build-arg VERSION=`; the
  Dockerfile then publishes with `-p:MinVerSkip=true`.

What is running is reported in four places, all reading the same assembly attribute:
`GET /api/version`, the console footer, OTel `service.version` on every span and metric, and
`hephaisto_build_info{version,commit}` for joining against any other series.

### Release candidates

A release candidate is an ordinary tag: `git tag v0.0.1-rc1 && git push --tags`. It runs the
same workflow and publishes the same artifacts as a real release — same multi-arch image build,
same provenance attestation, same chart push — which is what makes it a genuine rehearsal of
the publish path rather than a simulation of one.

What an rc deliberately does *not* get is any "this is the current version" signal:

| | release | release candidate |
|---|---|---|
| image `X.Y.Z` tag | yes | yes |
| image `latest`, `0`, `0.0` | yes | **no** |
| chart pushed to OCI | yes | yes |
| selected by a range like `^0.0` | yes | **no** — Helm and SemVer skip prereleases |
| GitHub "Latest release" badge | yes | **no** — marked prerelease |

So an rc is fully installable by anyone who names it exactly (`--version 0.0.1-rc1`) and
reachable by accident by no one. Iterate with `-rc2`, `-rc3`; when it is good, tag `v0.0.1` on
the same commit.

One MinVer behaviour worth knowing: while an rc is the newest tag, untagged builds on main
report `0.0.1-rc1.2` rather than `0.0.2-main.0.2`. MinVer appends height to an existing
prerelease instead of auto-incrementing past it. It is correct — `0.0.1-rc1.2` sorts below
`0.0.1` — and it resolves the moment the final tag lands.

### Releasing

`git tag v0.0.1 && git push --tags`. That is the whole procedure — `.github/workflows/release.yml`
builds the image on native amd64 and arm64 runners, pushes by digest, joins them into one
multi-arch tag, attaches build provenance, and publishes the chart to
`oci://ghcr.io/truerelevance/charts` with `version == appVersion == image tag`, then creates the
GitHub release with the chart tarball attached.

**There is no deploy job, and there must not be one.** CI holds no kubeconfig and no cluster
credential. It publishes versions that no running cluster references; a cluster moves only
when a human changes a pinned version in a GitOps repo. The gap between "CI is green" and
"the cluster changed" is where a person decides — and the thing being published can delete
pods.

## Design

The console has a written design language as of v0.4.0: **[`docs/design.md`](docs/design.md)**,
which is what a contributor is pointed at before touching CSS.

One token set feeds every surface — `src/Hephaisto.Agent/wwwroot/tokens.css` — and it is canonical
by test rather than by convention: a colour written anywhere else fails the build, and both themes
are held to the same contrast bar. `design/gallery.html` renders every component the language has to
keep working, and `scripts/visual-test.sh` photographs it in both themes so a stylesheet refactor
cannot quietly change the console.

`website/` is a single landing page that consumes a byte-identical copy of the same tokens. It is
not the site; it is the proof that "one token source" survives having more than one consumer.

## Repo layout

```
src/Hephaisto.Core/            domain, state machine, policy, digester — ZERO I/O
src/Hephaisto.ServiceDefaults/ OTel wiring, health, resilience
src/Hephaisto.Agent/           THE pod: tools, hosted services, Blazor UI, persistence
src/Hephaisto.AppHost/         Aspire — dev-time orchestration only, NEVER deployed
src/Hephaisto.Simulator/       dev-only fault generator
infra/                         namespaces, observability stack, chaos fixtures, RBAC
docs/                          architecture, roadmap, backlog, history, verification
```

Start with [`docs/architecture.md`](docs/architecture.md) for how it works,
[`docs/roadmap.md`](docs/roadmap.md) for where it is going,
the [issues](https://github.com/TrueRelevance/hephaisto/issues) for what is known-broken and unfixed, and
[`docs/history.md`](docs/history.md) for why it is shaped the way it is.

**`Hephaisto.Core` has zero I/O dependencies, on purpose.** Every safety-critical decision —
the policy engine, the state machine, budgets, oscillation detection, log digestion — is a
pure function over facts passed in by the caller. Nothing in `Core` opens a socket, reads a
file or touches a database. That is what makes the safety surface testable without a
cluster, a database or an LLM. If you are tempted to add a package to `Core` that talks to
something, the design has drifted: the fact belongs in `ClusterFacts`, gathered by the
caller, and passed in.

## Development

```sh
dotnet build
./scripts/test.sh               # NOT `dotnet test` — see below
./scripts/dev-db.sh up          # Postgres 17 + pgvector on :5433, for the integration suite
```

xUnit v3 on Microsoft.Testing.Platform, with
[AwesomeAssertions](https://github.com/AwesomeAssertions/AwesomeAssertions) — the Apache-2.0
community fork of FluentAssertions.

**`dotnet test` cannot run this suite** on the current toolchain. Without a `global.json`
`test.runner` entry it hard-errors; with one it starts the test executable in
`--server dotnettestcli` mode and exits in ~200 ms reporting *"Zero tests ran"*. This was
checked against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter — a xunit.v3
test project is an executable, and running it directly works and gives an honest exit code.
There is deliberately no `global.json` here: adding one turns a loud, actionable error into
a quiet "Zero tests ran", and in a repo whose tests are the safety argument, the loud
failure is worth more.

**The `PolicyEngine` tests are not routine coverage — they are the argument that L3 is
safe.** Treat a change that weakens them as a change to the safety model.

### Chaos fixtures

`infra/chaos/` breaks a cluster in ten documented ways. Every scenario is manual-trigger
only, so nothing brings up a deliberately broken cluster by accident. `infra/chaos/README.md`
maps each scenario to the alert, PromQL, LogQL and Kubernetes event it should produce; that
table is the agent's regression suite.

## Licence

Copyright (C) 2026 Florian (Flou21).

Hephaisto is free software: you can redistribute it and/or modify it under the terms of the
**GNU Affero General Public License, version 3** as published by the Free Software Foundation.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See
the [GNU Affero General Public License](LICENSE) for more details.

**What the "Affero" part means in practice.** Hephaisto serves a web console, so people will
use it over a network without ever receiving a copy of the binary. AGPL §13 says that those
users are entitled to the source of the version they are talking to. If you run a modified
Hephaisto and let anyone else reach its UI or its API, you have to offer them your modified
source. The console's footer carries a source link for exactly this reason — if you fork it,
point that link at your fork.

Running an unmodified Hephaisto for yourself triggers none of this.

Every dependency is MIT, Apache-2.0 or the PostgreSQL licence — all permissive, and all
one-way compatible into an AGPL-3.0 work, so nothing here is in tension with the above.
