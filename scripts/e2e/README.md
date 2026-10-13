# The end-to-end release harness

One command that takes a published build, stands it up next to a real observability stack on a
throwaway cluster, breaks things on purpose, and reports what happened.

```sh
scripts/e2e/run.sh                    # dispatch a nightly and test it
scripts/e2e/run.sh --rc               # cut a real release candidate and test it (prompts)
scripts/e2e/run.sh --tag 0.0.1-rc2    # test something already published
```

## Why it exists

`ci.yml`'s `e2e-kind` job states its own limits in a comment:

> - kind's default CNI ACCEPTS NetworkPolicy objects and does not enforce them.
> - No Prometheus here, so the `release:` selector is only covered by the render-time grep.
> - **No real LLM key, so nothing exercises an investigation end to end.**

Nothing anywhere proved that a *published* artifact, installed from GHCR beside a real
Prometheus, Loki, Tempo and collector, detects a real fault and investigates it. This does.

It closes the second and third of those limits. **The first is still open**, and the summary
says so at the end of every run rather than letting a green tick imply otherwise.

## What one run does

| | |
|---|---|
| 1-2 | get a build (dispatch `nightly.yml`, or cut an rc) and wait until it is genuinely pullable |
| 3 | create a single-node kind cluster on a pinned Kubernetes |
| 4 | install kube-prometheus-stack, Loki, Tempo, the OTel collector and grafana-mcp |
| 5 | `helm install` the published chart from `oci://ghcr.io/truerelevance/charts` |
| 6 | apply chaos fixtures `c2 c3 c4 c7` simultaneously |
| 7 | assert detection, investigation, budget arithmetic, RBAC, zero mutation; grade the diagnoses |
| 8 | delete the cluster |
| 9 | print a summary and write `report.md` |

Roughly 25 minutes, and under a dollar of Gemini spend.

## Two things that are structural rather than remembered

**It cannot reach production.** `~/.kube/config` on this machine holds seventeen contexts,
several of them production clusters. The harness never reads it: `kind` writes to a dedicated
kubeconfig and only that is exported. A `kc()` wrapper additionally refuses any context that is
not `kind-hephaisto-e2e`. The context guard alone would not be enough - a stray `--context`
defeats it - which is why the file is the primary mechanism and the guard is the backstop.

**Teardown is an `EXIT` trap, not a final step.** It runs on success, on a failed assertion, on
a `set -e` abort and on Ctrl-C alike, deleting the cluster and restoring the system-wide inotify
limit. A teardown that only works on the happy path leaves both behind exactly when something
went wrong.

## The fixtures, and the ones left out

Default is `c2,c3,c4,c7` - chosen to discriminate rather than to cover, because
`infra/chaos/README.md` records a known-correct answer for each.

- **c4 + c7** are the pair that matters. ImagePullBackOff and CreateContainerConfigError look
  nearly identical in Kubernetes and have entirely different causes; the README calls giving
  both the same diagnosis a failure. It is the one assertion an agent cannot pass by
  pattern-matching the symptom.
- **c2** carries a decisive log line, so it tests that Loki is genuinely reached.
- **c3** has its cause in an Event and in *no metric at all*, so it tests the OTel `k8s_events`
  receiver specifically.

Excluded by default: **c9 is node-wide** and would evict Prometheus and the agent itself (the
harness refuses it even if asked); c6 does not fire on `local-path`; c1's OOM event is
unreliable on containerd; c8 needs 30 minutes; c10 needs a local image build.

**`--full` is the DIAGNOSIS gate**: `c1,c2,c3,c4,c5,c7,c8,c10,c11,c12,c13` — every fixture that
can run on this hardware, which is eleven of the thirteen and the denominator the MVP bar was
always written against. c6 and c9 stay out and no flag overrides that; neither is a scheduling
choice.

**It is the acting gate too, as of v0.8.0 — but only because the run puts the cluster back first.**
`--full` applies its fixtures *simultaneously*, and on a single node that many broken workloads is
over `policy.clusterUnhealthyCeiling`, so the policy engine correctly refuses every action as a
cluster-wide event. For three releases that meant the act assertion could never pass in a `--full`
run and a release needed two commands.

The act phase now calls `chaos_reset_for_acting` first: it **deletes the act fixture's neighbours**,
waits for the cluster-wide unhealthy fraction to fall back below the ceiling, and leaves the act
fixture — and its incident, and the investigation already attached to it — alone. The ceiling is a
property of how much is broken *at once*, so one run can be wide and then narrow.

```sh
scripts/e2e/run.sh --tag <version> --full --mode Auto           # diagnosis AND acting, ~2-4 h
```

Nothing was weakened to get there: not the ceiling, not the fixture, not the assertion. See
backlog #97, which spells out why raising the ceiling would have been the wrong move.

Two runs are still the right thing when measuring a **second** action type — `--fixtures c14 --mode
Auto` for a `RollbackDeployment` — because `ACT_FIXTURE` names one fixture per run and the action
types promoted to unattended follow it.

Budget **about two hours**. c8 alone cannot open an incident sooner than thirty minutes, because
its rule needs `changes(...)[30m] >= 4` — thirty minutes of evidence before the expression can
be true at all — and the incident deadline is raised to match rather than timing out a fixture
that is exactly on schedule. The four-fixture default stays the thing to run while working: a
two-hour gate nobody runs is worth less than a five-minute one everybody does.

## Reusing the real values files

The observability stack is installed from `infra/observability/*.values.yaml` **byte for byte
unmodified**, at the versions the Tiltfile pins. Those files are as much under test as the chart
is. Only two things are overridden on the command line, both genuinely cluster-specific:
`crds.enabled=true` (the file disables them because the dev cluster manages them separately) and
the `cluster` external label.

`local-path-sc.yaml` is what makes that possible: it aliases the StorageClass name k3s uses onto
the provisioner kind runs. Without it every PVC in the stack sits Pending forever.

## Resuming

A twenty-five minute script you cannot resume is unusable while you are debugging the script.

```sh
scripts/e2e/run.sh --tag 0.0.1-rc2 --from validate --keep-cluster
scripts/e2e/run.sh --tag 0.0.1-rc2 --only ui
```

## Run it in tmux, not as a background job

A `--full` run is two to four hours. **Start it in a detached tmux session.**

```sh
tmux new-session -d -s e2e 'cd ~/hephaisto && scripts/e2e/run.sh --tag <version> --full 2>&1 | tee /tmp/e2e.log'
tmux capture-pane -pt e2e | tail -40    # progress without attaching
tmux attach -t e2e                      # live
```

This is not a style preference. A run backgrounded from a shell gets SIGTERM'd: on 2026-09-03 two
consecutive `--full` runs were killed mid-investigation, the second twenty-one minutes into a phase
that needs about three hours, each after every phase that takes real setup had already passed. Both
tore down cleanly and deleted their clusters — the trap is that nothing looks broken, you simply
lose the hour.

`nohup` and `disown` do not help, because the signal goes to the process group. `setsid(1)` would,
and does not exist on macOS. tmux does it properly and gives you scrollback.

The same applies to the machine's other long-lived process: Tilt runs in a detached tmux session in
`~/dev` for exactly this reason.

**A background or non-interactive session also cannot unlock the login keychain**, so `docker pull`
fails against docker.io even for public images. Put a stub `docker-credential-osxkeychain` early on
`PATH` that answers `get` with `credentials not found in native keychain` on stdout and exits 1.

## Requirements

`kind`, `kubectl`, `helm`, `gh`, `docker`, `jq`, `git`, `curl`. Runs on stock macOS bash 3.2 -
no associative arrays, no `brew install bash`.

`HEPHAISTO_GEMINI_API_KEY`, or a real key in `secrets/hephaisto-llm.secret.yaml`. Without one
the run still exercises detection end to end and reports the investigation assertions as
skipped rather than failing them.

### Running against a cheaper provider

Any OpenAI-compatible server — DeepSeek, OpenRouter, or a local Ollama or LM Studio — with four
environment variables. The key is validated against `/v1/models` before the run starts, for the
same reason the Gemini key is: a 401 discovered on the fourth investigation of a twelve-minute
run looks like a bug in the agent.

```sh
HEPHAISTO_LLM_PROVIDER=openai \
HEPHAISTO_LLM_ENDPOINT=https://api.deepseek.com/v1 \
HEPHAISTO_LLM_MODEL=deepseek-v4-flash \
HEPHAISTO_LLM_PLANNING_FORMAT=JsonObject \
HEPHAISTO_LLM_API_KEY=... \
    scripts/e2e/run.sh --mode Auto
```

`HEPHAISTO_LLM_PLANNING_FORMAT=JsonObject` is required for DeepSeek and wrong for most others:
it is a provider *capability*, not a preference, and it is the weaker of the two modes. Without
it DeepSeek answers `400` to every planning call, and because phase 1 is unaffected the run
reports correct diagnoses and no plans — which looks like an agent declining to act. A local
Ollama server needs no such setting: llama.cpp constrains generation with a grammar, so strict
schemas work there even on small models.

**The key is not put on the command line.** It is read from the environment, or from
`LLM_API_KEY` in `secrets/hephaisto-llm.secret.yaml`, which is gitignored twice over.

### Running against a local model, which is free

`gpt-oss-120b` on Ollama matches the hosted frontier model on the scenarios the corpus can carry
and costs nothing per token, which is what makes a two-hour ten-fixture gate affordable to run
before every release. No key is involved:

```sh
HEPHAISTO_LLM_PROVIDER=openai \
HEPHAISTO_LLM_ENDPOINT=http://100.91.41.104:11434/v1 \
HEPHAISTO_LLM_MODEL=gpt-oss:120b \
    scripts/e2e/run.sh --nightly --full --mode Auto
```

Note `--full --mode Auto`: since v0.8.0 the two combine, because the act phase clears the other
fixtures and waits for the cluster to recover before asserting. Run `--fixtures c14 --mode Auto`
separately to measure the second action type.

Two things have to be true, and both were false on a fresh install:

**The endpoint must be an address the CLUSTER can reach.** `localhost` is the mistake, and it is
not an obvious one: from a pod, `127.0.0.1` is the pod. The agent runs in kind, kind runs in
Rancher Desktop's Lima VM, and the model runs on macOS outside all of it. Measured from inside
the VM, all of `192.168.2.77` (LAN), `100.91.41.104` (tailnet), `host.docker.internal` and
`192.168.5.2` (the Lima gateway) reach the host. **Prefer the tailnet address**: it does not
move with DHCP or with docker's bridge topology, and it is the same address the rest of this
machine's tooling already uses. The harness verifies this from a pod during `deps` and fails
there rather than forty minutes later.

**Ollama must be listening off loopback.** It ships bound to `127.0.0.1` only. The macOS app has
an *expose to the network* setting; without it the host probe passes and no pod can connect.
Check with `lsof -nP -iTCP:11434 -sTCP:LISTEN` — it should say `*:11434`, not `127.0.0.1:11434`.

No `HEPHAISTO_LLM_PLANNING_FORMAT` is needed: llama.cpp constrains generation with a grammar, so
the strict schema mode works locally even where a hosted DeepSeek needs the weakened one. The
step ceiling is raised to 20 automatically for any openai-compatible provider — see backlog #59
for what happens when it is not — and `HEPHAISTO_LLM_MAX_STEPS` overrides it either way.

**A local model does not make the image local.** The harness installs the *published* artifact
from GHCR by design, so `--nightly` still pushes the branch and builds it in Actions. What is
local, and free, is the cluster and the model.

## The pager suite (`pager.sh`), and the `pager` phase

A second suite, about a different question: not "does the agent diagnose a real fault" but "is
it a pager a person can rely on" - one incident per alert for as long as it fires, closed when
it clears, a person told before the model answers, the right person, and somebody else when
nobody answers. It is v0.10.0's acceptance test, one scenario per sentence of the milestone's
"Done when" (`pager/P*.sh`; `pager.sh --list` prints them).

The model and Teams are stand-ins (`infra/e2e/notification-receiver`: `LlmStandIn.cs`,
`TeamsStandIn.cs`), which makes a run deterministic and free - and means this suite proves
nothing about a diagnosis. The windows it waits out are seconds (`values-pager.yaml`).

| Where | How | What it adds |
|---|---|---|
| CI | `e2e-pager` in `.github/workflows/ci.yml`, on every change | a standalone Alertmanager (`infra/e2e/alertmanager.yaml`) |
| Dev cluster | `pager-local.sh`, with `"pager-e2e": true` in `tilt_config.json` | a real Prometheus, so P16 runs |
| Release harness | the `pager` phase of `run.sh`, after `ui` | the published artifact |

The phase runs last because it reconfigures the installed agent (`helm upgrade --reuse-values -f
values-pager.yaml`). `--no-pager` skips it.

`pager/KNOWN_RED` lists scenarios whose fix has not landed, each with the milestone that needs it
green. A red one there is reported and does not fail; a green one there fails the run, and so does
every entry under `--strict`, the release gate. An entry whose milestone the version floor has
passed fails the unit tests (`PagerSuiteTests`).

### The MCP scenarios (P29-P48, v0.11.0)

The same suite asks the agent's MCP endpoint what a coding agent would (#157). The driver is
`lib/mcp.sh`: plain JSON-RPC over curl, because the endpoint is stateless streamable HTTP and one
POST is all a gateway sends per call. P29 is its control - the stand-in's decoy server
(`DecoyMcp.cs`), which works and is not the agent.

| File | What |
|---|---|
| `mcp/tools.golden.json` | The reviewed tool list: names, order, who may call each, descriptions |
| `mcp/findability.tsv` | Questions a gateway's tool search must answer with the right tool in its top five |
| `mcp/neighbour-tools.json` | Other servers' tools, listed first in the findability check and by the decoy |
| `mcp-secrets.sh` | Makes the Secret with the five tokens `values-pager.yaml` names; `--print` exports them |
| `infra/e2e/pager-fixture.yaml` | A pod whose log is an instruction; the stand-in's script mode cites it (P33, P41) |

P48 needs the `signin` capability: a second, small install with sign-in on, next to the main one,
against the identity-provider stand-in (`OidcStandIn.cs`). `signin-install.sh --image <production
image>` puts it on the dev cluster; CI installs it from the chart it built. `pager-local.sh` grants
`signin` whenever it finds that install.

Two more tiers, local and on demand, never in CI:

| Script | What | Costs |
|---|---|---|
| `mcp-litellm-local.sh` | The endpoint through a throwaway LiteLLM gateway with tool search (`infra/e2e/litellm.yaml`, pinned by digest): the names it gives the tools, every `findability.tsv` question against the real search - and the real search ranks exactly as `McpFindabilityTests`' scorer predicts - a read and an acknowledgement through it, a key without the agent refused | nothing: the gateway has no models |
| `mcp-model-local.sh` | Claude Code on Haiku, with nothing but the endpoint, asked the three questions the endpoint was built for and an injection probe; `--via litellm` through the gateway. Reports n of m | a few cents a run |

A scenario that needs an investigation with evidence scripts the model stand-in first
(`mcp_script`, `POST /llm/script`): it reads one tool, then concludes citing the step it read.

## The issues suite (`issues-local.sh`), v0.14.0

A third suite, about a third question: can Hephaisto be handed a GitHub issue. An issue assigned
to its account is planned by a Job, the plan is a comment on the issue, an approver answers
`/approve` or `/reject <reason>` in a comment, and the implementing Job opens a draft pull request
that closes the issue (#243). One scenario per file (`issues/G*.sh`; `issues-local.sh --list`
prints them), in the pager suite's shape: a header line, a `scenario()` function, `KNOWN_RED`
with a milestone per entry, `--strict` as the release gate, `IssuesSuiteTests` for the
bookkeeping.

```sh
# tilt_config.json: "github": "stand-in", "coder": true, "coder-mode": "pr", "coder-sdk": "fake"
scripts/e2e/issues-local.sh                 # every scenario, one at a time
scripts/e2e/issues-local.sh --only G01,G03
```

It runs on the dev cluster only. Not in CI yet: a plan clones a repository and the dev-context,
and the in-cluster git server is seeded from a private one.

**GitHub is a stand-in** (`infra/e2e/notification-receiver/GitHubStandIn.cs`), in the pod that
is Teams and the model too, under a Service name of its own: an agent's API base URL is
`http://github-stand-in.hephaisto-obs:8080/github/api`. It answers the nine REST calls the agent
makes, with GitHub's field names, and copies what a client gets wrong against the real thing:
`ETag` and a 304 for a list already given, 30 per page unless asked, `"body": null`, `since`
inclusive at whole seconds, comment ids beyond 32 bits, a rate limit as a 403 - and an issue's
**timeline**: every assignment and unassignment is an event with a time of its own, comments
stand between them, and one of more than a page names its last page in `Link`. What it does not
copy: GitHub's issue list also returns pull requests, and this one never does.

The other half is for the harness - a person at github.com, and a witness. Through Tilt's
forward (`$H:8110`), no token:

| | |
|---|---|
| `POST /github/control/repos/{o}/{r}/issues` `{title, body?, login, id?}` | somebody opens an issue |
| `POST .../issues/{n}/assign`, `.../unassign` | the bot becomes, or stops being, an assignee - an event in the issue's timeline |
| `POST .../issues/{n}/reassign` | off and on again in ONE request: what a person does within seconds, and no poll can fall between |
| `PATCH .../issues/{n}` `{body?, title?, state?}` | an edit; `state` closes and reopens |
| `POST .../issues/{n}/comments` `{body, login, id?}` | a comment as that account |
| `POST .../issues/comments/{id}/reactions` `{content, login, id?}` | that account sets a reaction on a comment - `rocket`, `-1`, `+1`, ...; `DELETE .../reactions/{reaction}` takes it off |
| `PUT .../pulls/{n}` `{merged?, state?}` | what became of a pull request; one nobody registered is an open draft |
| `DELETE .../pulls/{n}` | forget it: an open draft again. The `gh` shim numbers pull requests from 1 in every Job |
| `POST /github/control/fail/{500\|rate-limit\|off}?count=N` | the next N API calls fail |
| `GET /github/control/requests` | every API call the agent made: `seq`, method, path, query, status |
| `GET /github/control/comments` | every comment, with its issue, its author, how often it was edited and the reactions on it |
| `GET /github/control/state`, `DELETE /github/control` | everything it holds; forget it |

An account is a login **and** a number, and a scenario comments as an approver by number
(`lib/issues.sh`: `maintainer`, 1001) - which is what the install under test has to list. The
stand-in's token and the bot's login are in `infra/e2e/teams-stand-in.yaml`.

`lib/issues.sh` has two halves and says which is which: `gh_*` drives the stand-in and was
exercised when it was written; `wi_*` reads an agent API that did not exist yet, and is the one
place a later stage adjusts when a path or a field turns out differently. Its header lists six
things it reads and marks each BUILT or ASSUMED: since stage 2.2 (#245) the work items
(`GET /api/workitems`), a cancel, and `github` in `/api/status` are built, and G02 and G09 are
green. Since stage 2.3 (#246) a work item's attempt is a row of `GET /api/codefixes` with a
`workItemId`, its Job's request is contract version 2 with the issue under `work_item`, and the
plan is a comment on the issue - G01, G07 and G08 are green. `issues_plan_ready`, the road ten
scenarios start on, ends where the issue has been told: `PlanReady` is written by the loop that
collects a Job, the comment by the poller's next pass, and the attempt names it (`planCommentId`)
once it is there. Since stage 2.4 (#247) nothing is ASSUMED: a plan is answered on the issue, the
pull request's body is `prBody` on the attempt (`issues_pr_body` reads it there - the Job's pod is
gone when a scenario looks), a merged pull request is `Done`, and G03 to G06 and G10 to G12 are
green. `KNOWN_RED` lists nothing, so a plain run and `--strict` are the same. Since stage 2.5
(#248) G01 also reads the Teams stand-in: a plan for an issue is announced through the outbox,
and where the install routes `CodeFixPlanReady` to the Teams bot - the dev values do, with
`"teams-bot": "stand-in"` - one message per recipient's chat names the issue by its reference,
says where the plan is answered and links the attempt's page (`issues_route_takes`,
`issues_teams_alerts`). An install without that route skips the block and says why.

**G13 to G17 are the issue as a conversation** (#286, with #252 and #285), and landed before
their code like the twelve: G13, the plan comment shows the planner's questions as a numbered
list and its notes in a fold; G14, an approver answers and writes `/replan` - the waiting plan
ends as denied with "replanned by github:\<login\>", the SAME work item gets a second attempt,
and its Job's request carries the approver's comments, nobody else's, and the earlier plan; G15,
`/replan` from anybody else changes nothing, and an approver's is refused once while a Job runs
and once after a pull request; G16, an attempt that did not work shows what its planner asked
and is planned again by `/replan`; G17, off and on again faster than a poll is noticed once the
attempt has ended, and a waiting plan is left alone. The scripted planner is what makes them
cheap: the fixture's plan asks two questions, a request with `previous` plays
`hephaisto-fixture-dotnet.plan.replan.json`, and a line `FAKE-SDK-PLAN: unclear` in an issue
makes the first plan end as `insufficient_context` (`coder/README.md`). `issues_next_plan_ready`
is the road of a second plan, `issues_plan_comment` and `issues_status_comment` find a comment by
its marker, and `gh_reassign` is the stand-in's `reassign`.

**G18 and G19 are the plan answered by a reaction** (#298): G18, Hephaisto sets a rocket and a
thumbs-down on its own plan comment, which answer nothing, and an approver's rocket on that
comment ends in a pull request from exactly one implementing Job - also after the agent has
looked at the reaction that stayed three more times; G19, a stranger's rocket is answered once
and changes nothing, the approver's login on another account is a stranger, an approver's
thumbs-up, heart and eyes say nothing, and the approver's thumbs-down rejects the plan.
`gh_react_as` and `gh_unreact` are a person's click, `gh_reactions` what is on a comment,
`issues_plan_comment_id` the comment a reaction counts on, and `gh_wait_reaction_reads` the
wait before "nothing happened".

What the bot writes on an issue is one status comment per work item, edited in place as the work
moves, and one comment per attempt with its plan - and, only when somebody answered a plan and
was not heard, one answer per attempt to people who are not approvers and one per cause an
approver was refused for. Never more than five for one attempt, at most five attempts for one
work item, and so - with the status comment - never more than 26 for a work item
(`ISSUES_ATTEMPT_COMMENT_CAP`, `ISSUES_ATTEMPT_CAP` and `ISSUES_COMMENT_CAP`, which a unit test
holds to `IssueComments.MaxPerAttempt`, `MaxAttemptsPerWorkItem` and `MaxPerWorkItem`). `curl -s
http://$H:8110/github/control/comments | jq -r '.[] | "\(.edits) \(.body)"'` shows them.

A scenario answers a plan the way a person does: `gh_comment_as <repo> <n> maintainer 1001
"/approve"` - or `/reject <reason>`, or `/replan`. The command is the comment's first non-blank
line; the number is what makes it count.

All scenarios work in one repository, and each is an attempt on it. `values-dev-coder.yaml` allows
20 a day, which two runs of this suite beside the incident suites would use up - reported as "no
plan for the issue: 20 attempts on this repository today (cap 20)" on the issue and in
`declineReason`. `values-dev-github.yaml` therefore raises `codeFix.budgets.attemptsPerRepositoryPerDay`
to 500, for the stand-in only - and, since every work item's code fix is announced,
`notifications.maxPerChannelPerHour` to 1000: a run is about thirty messages on the one channel,
and the chart's sixty an hour would have the second run within the hour suppress them, with every
incident's message of whatever suite ran beside it.

**The agent is pointed at the stand-in by a values file**, `charts/hephaisto/values-dev-github.yaml`,
which the Tiltfile layers for `"github": "stand-in"`: the stand-in's URL, its bot's login, the one
listed repository, approver 1001, a five-second poll, and `secrets.github` naming the Secret
`hephaisto-github-stand-in` that is applied with the stand-in. The dev agent therefore gets its
token the way an install does - the chart's `secretKeyRef` - and not through `extraEnv`, which
the chart refuses for every `GitHub__` name.

**`github` in `/api/status` is up to a minute behind.** The row is what the poller last saw,
served from the status page's cache, which is refreshed every 60 seconds. G09 waits for each
change of it, so that scenario takes four to five minutes however fast the poller is.

**The stand-in's image can vanish.** It is a fixed tag (`hephaisto/notification-receiver:dev`)
that no container uses between a rebuild and the pod's restart, and a kubelet whose disk is
filling up deletes images nothing uses (seen on the dev node on 2026-10-06, at 84%: `kubectl get
events -A --field-selector reason=ImageGCFailed`). The restart then ends in `ImagePullBackOff`
and takes Teams and the model stand-in with it. Rebuild the image and delete the pod:

```sh
docker build -f infra/e2e/notification-receiver/Dockerfile -t hephaisto/notification-receiver:dev .
kubectl -n hephaisto-obs delete pod -l app.kubernetes.io/name=teams-stand-in
```

## The live tier (`github-live.sh`), v0.14.0

The issues suite asks a stand-in, and a stand-in answers what its authors believed GitHub
answers. `github-live.sh` asks GitHub ([#249](https://github.com/TrueRelevance/hephaisto/issues/249)):
the dev agent against `https://api.github.com`, a real bot account with its two real tokens, the
real `gh` in the coder Job. It is the only automated test in this repository that leaves the
cluster for github.com. The model stays the script, so a run costs nothing but a quarter of an
hour. **Run it before each release candidate**, and after any change to what Hephaisto sends
GitHub or writes there.

```sh
# tilt_config.json: "github": "live", "coder": true, "coder-mode": "pr", "coder-sdk": "fake"
scripts/e2e/github-live.sh                 # every scenario, one at a time, about 20 minutes
scripts/e2e/github-live.sh --only L01
scripts/e2e/github-live.sh --list
scripts/e2e/github-live.sh --sweep         # close whatever a killed run left in the sandbox
```

| | What it asks of GitHub |
|---|---|
| L01 | The whole road: an issue opened and assigned with `gh` is a work item within a poll; the status comment and the plan are comments **by the bot account**, the first edited in place (GitHub says `updated_at > created_at`), never a third; `/approve` by a person whose number the install lists starts the implementing Job; the pull request is a **draft**, by the bot, from a `hephaisto/codefix-<id>` branch into `main`; **GitHub itself** names the issue as closed by it and no other (`closingIssuesReferences`, and the closing keyword its renderer marks); the diff is the scripted fix's files; every commit carries the attempt's trailer and the head `Hephaisto-Issue`; `main` did not move; what the agent kept as `prBody` is the description GitHub has. Then the pull request is closed without merging: the work item is `Cancelled`, "pull request closed without merging", the issue says so, and the issue - still open and assigned - is not taken again |
| L02 | `/reject <reason>`: `Denied` by `github:<login>` with the reason, said on the issue; no Job, no branch, no pull request on GitHub. Closing the issue then ends the work item with "the issue was closed" |
| L03 | The bot unassigned while the plan waits: `Cancelled`, "`<bot>` is no longer an assignee", the issue is told; an `/approve` afterwards changes nothing, starts nothing and is not answered |
| L04 | What was only assumed. An unchanged list is answered **304** (the agent's own counter), through the egress proxy (its log has the agent's tunnels to `api.github.com`); GitHub also answers 304 for the two other things the agent asks with a tag - an issue's comments since a time, and a pull request; `github` is `Healthy`. And **what Hephaisto repeats does nothing**: the plan repeats a line of the issue with a mention, `#n`, `GH-n` and an issue's address, the status comment the same from a `/reject` - GitHub's HTML of both has no mention and no link, and the other issue's timeline has no reference by the bot. With a control: the approver's own comment with the same words does produce a mention, a link and a timeline entry |
| L05 | The issue as a conversation. GitHub's **HTML** of the plan comment has every question of the plan as an item of a list and the notes as a `<details>` fold; an answer and `/replan` written with `gh` end the waiting plan as `Denied`, "replanned by github:\<login\>", and the second Job's request holds the two comments GitHub was given, in order, and the earlier plan's questions; the second plan comment repeats the answer and says it replaces the first. Then the second plan is rejected and the bot is taken off the issue and put back **a second apart**: when no poll fell between the two requests, the **timeline** GitHub answers is what starts a third attempt for the same work item - and when one did, the scenario says so and skips that line |
| L06 | A plan answered by a reaction (#298). GitHub lists a `rocket` and a `-1` **by the bot account** on the plan comment and counts them in the comment's own `reactions` - set with the agent's token, which has Issues read and write and nothing for reactions by name; they answer nothing; the person's `-1`, set with `gh api`, ends the plan as `Denied` by `github:<login>` with "no reason given: rejected with a thumbs-down on the plan", said on the issue; no Job, no branch |

**What it needs**, all made by hand, and checked before anything is written:

- **A bot account**, a member of the sandbox's organisation with write on the sandbox - GitHub
  ignores an assignee it will not accept, and answers 201 all the same.
- **Its two tokens**, fine-grained, in the two Secrets: the agent's in `hephaisto-github`
  (namespace `hephaisto`, key `GITHUB_TOKEN`: Issues read and write, Pull requests read - it
  cannot push), the coder's as `GITHUB_TOKEN` in `hephaisto-codefix` (namespace
  `hephaisto-coder`: Contents read and write, Pull requests read and write). That was enough
  for everything above, including `gh pr create --draft --assignee`.
- **The sandbox**, `TrueRelevance/hephaisto-sandbox`, with GitHub Actions **disabled** - a pushed
  branch must not run anything, and the suite refuses to start when they are on. Its `main` is
  the fixture's `fixture/c15-null-deref`, which is what the scripted fix applies to; the fake
  SDK plays the fixture's scripts for it (`coder/fake-scripts/aliases.json`).
- **The sandbox in dev-context's `repos.yaml`**, `coderEnabled`. The dev cluster serves
  dev-context from `coder-git`, seeded from the local checkout, and
  `values-dev-github-live.yaml` names the ref (`codeFix.contextRepository.ref`).
- **`gh` on this machine**, logged in as an account whose number is in the agent's
  `github.approvers`. The suite plays the person with it.

**It refuses to run** unless the kube context is this machine's, the agent's
`GitHub__ApiBaseUrl` is `https://api.github.com`, the one repository it lists is the sandbox,
`CodeFix__Sdk` is `fake`, the Job's `gh` is not the shim, `gh` is an approver, Actions are off
on the sandbox, and neither the sandbox nor the agent holds anything an earlier run left.

**It writes in one repository.** `lib/live.sh` is `lib/issues.sh` with the person's half
redefined: every call to GitHub goes through `_live_api` or `_live_gh`, which put
`TrueRelevance/hephaisto-sandbox` into the request themselves - the name is a `readonly`
constant, not a setting, and `LiveSuiteTests` fails on a bare `gh` anywhere else, on a merge,
on a push, and on a delete of anything but a `hephaisto/codefix-<id>` branch. A run opens six
issues (one per scenario and a bystander that nothing may point at), writes nine comments as
the person, takes the bot off one issue and puts it back, and has the bot open one branch and
one draft pull request. A trap cleans up on
every exit: its issues are closed, the pull request is closed - never merged - and the branch is
deleted, after the agent has let go of the issues, so that no Job pushes afterwards. The last
four lines of a run say whether the sandbox was left as it was found, `main` included.

**It refuses when somebody else works in the sandbox.** The bot's token is an account's: an
install that holds it and lists the sandbox takes every issue a run opens, writes a second
status comment on it, and plans it with ITS coder. On 2026-10-08 a production install did, with
a real model - four issues of one run, about twenty cents a plan - and three scenarios were red
on nothing but a comment count. No preflight that looks at the dev agent sees that, so the
runner looks at GitHub: the last run's issues must carry no status comment of a work item the
agent under test does not know (`live_foreign_takers`).

What the last run left does not change when that install is taken off the sandbox, so the
refusal is lifted by whoever did that, saying so once: `scripts/e2e/github-live.sh
--other-install-gone`. The flag is a statement about another cluster and is not trusted for
longer than a scenario: after every scenario, with or without it, the issues THIS run opened are
asked the same question, and the run stops at the first one another install took. A run that
passes leaves clean issues, and the next one needs no flag.

**What it does not test**, and where that is tested instead:

- **Merged is `Done`.** A merge would move `main`, and the scripted fix would no longer apply.
  That the agent reads a merge as `Done` is issues G11, against the stand-in; that GitHub will
  close the issue on a merge is what `closingIssuesReferences` says, without merging.
- **Somebody who is not an approver** (G04), and **that nobody else's comment reaches a
  replanning Job** (G14): there is one human account, the approver and every issue's author.
- **GitHub failing or limiting** (G09): it cannot be told to. It did, once - see below.
- **A model.** The coder is the script. What it repeats is what an issue's
  `FAKE-SDK-REPEAT:` line asks for (`coder/src/phases.ts`), which is how a mention and a
  closing keyword get into a plan at all.
- The comment cap (G10), a restart in the middle (G08), the mode (G12), a label on the pull
  request (the sandbox has none named `hephaisto`; the run asserts that the attempt says so).

**GitHub is not a stand-in, and behaves like it.** Two things from the first afternoon:

- *It is late sometimes.* What a pull request will close is worked out by GitHub after the
  pull request is saved: seconds as a rule. On the second run it was still empty eight minutes
  later, a `PATCH` that closes a pull request was answered with an error and no body, and a
  comment the agent wrote was answered 500 - which the agent survived as designed (the next
  pass wrote it, once). L01 waits for the list and asks once more to close; a run in such a
  window may still be red, and the log says which request GitHub refused.
- *It reads more than was assumed.* `/issues/12` is a reference by itself. Both functions that
  make a model's words inert were wrong about that until this suite ran; `git log --grep '#249'`
  has the two fixes.

**The dev cluster in this mode is not the dev cluster.** `values-dev-github-live.yaml` empties
`codeFix.repositories`: with the real `gh` in the Job, a chaos fixture's code fix would fail at
`gh pr list` against the in-cluster git server and hold the one Job slot meanwhile. So
`codefix-local.sh`, `investigate-local.sh` and `issues-local.sh` do not run while it is on. Put
`"github"` back to `"stand-in"` afterwards and wait for the agent.

## The planner's suite (`ui/tests/planner.spec.ts`, `planner-live.sh`), v0.15.0

`/planner` is tested by the console suite, and what makes it more than a page test is that every
click is read back **from the board** and not from the page.

```sh
# tilt_config.json: "github": "stand-in" - the planner is on, against the stand-in's seeded board
HEPHAISTO_URL=http://$H:8100 GITHUB_STANDIN_URL=http://$H:8110 scripts/e2e/ui/run.sh planner.spec.ts
curl -s http://$H:8110/github/control/project | jq .          # the board's side of it
curl -s -X POST http://$H:8110/github/control/project/reset   # the seeded board again
```

Twelve specs, serial, about fifteen seconds: every option of every field, today / not today / the
decision taken back, an issue planned on an earlier day, every account assigned and taken off and
the one GitHub leaves out, both ways of closing from the card and one from the day's list, the
board view's groups, filters and row switch, and the page without a name. The board is seeded
anew before the first one (`GitHubStandIn.Project.cs` says what is on it and why), so two of them
may close what they touch. Without `GITHUB_STANDIN_URL` only the first spec runs - the page says
whether the planner is on - and the rest skip with a `PRECONDITION:` marker.

**Against github.com**: `scripts/e2e/planner-live.sh` (`"github": "live"`, `"planner-live":
true`, and a Secret the script's header says how to make). It makes seven issues in the sandbox,
puts them on the real project, runs the same specs with `PLANNER_E2E_LIVE=1` - `gh` then says
what a click did - and deletes the issues again; `--sweep` removes what an aborted run left. Run
it after changing anything the planner sends GitHub. Three things it knows that the stand-in
does not: GitHub sometimes gives an added issue an item and never lists it (the runner adds each
alone and adds it again); a board's own workflow may close an issue whose status reaches the last
column (the field spec presses that one last); and nothing is retried, because a second attempt
would start from what the first one closed.
