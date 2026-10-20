# Hephaisto — working notes for agents

**Read `README.md` first** for what Hephaisto is, the safety model and the invariants. This
file is the part that does not belong in a public readme: how to run the thing on *this*
machine, and the traps that have already cost time.

## Everything here is in git

This is one project, so `~/hephaisto` **is** the git repo and every Dockerfile, manifest,
values file and Tiltfile is tracked. A fresh clone builds. Keep it that way. (This differs
from the `~/dev` workspace next door, which holds ten repos with untracked `Dockerfile.dev`
files — the reason `git worktree` is unusable there and fine here.)

The one deliberate exception is `tilt_config.json`: per-machine settings, ignored, with
`tilt_config.sample.json` tracked beside it and the Tiltfile defaulting to `localhost` so a
clone with no config still works.

There is no `nuget.config`, no private feed and **no PAT build argument anywhere**.
Hephaisto references no internal package. If you find yourself adding a credential to a
Dockerfile, something has gone wrong.

## Layout

```
src/Hephaisto.Core/            domain, state machine, policy, digester — ZERO I/O
src/Hephaisto.ServiceDefaults/ OTel wiring, health, resilience — deployed as a dll
src/Hephaisto.Agent/           THE pod: tools, hosted services, Blazor UI, persistence
src/Hephaisto.AppHost/         Aspire — dev-time orchestration only, NEVER deployed
src/Hephaisto.Simulator/       dev-only fault generator
infra/                         namespaces, observability stack, chaos fixtures, RBAC
```

**If you are tempted to add a package to `Core` that talks to something, the design has
drifted.** The fact belongs in `ClusterFacts`, gathered by the caller, and passed in. The
zero-I/O rule is what makes the whole safety surface unit-testable.

### Aspire is dev-time only

`Hephaisto.AppHost` gives you `dotnet run` with Postgres, the agent and the simulator on a
laptop. It is excluded from the container image and no manifest references it.
`Hephaisto.ServiceDefaults` is a plain class library with an ASP.NET framework reference and
**no `Aspire.Hosting.*` package** — referencing one there would drag the orchestrator into
the pod.

Manifests are **hand-written**. Do not use `aspire publish` or aspir8:
`Aspire.Hosting.Kubernetes` is preview and emits Helm charts, which would be a second source
of truth drifting from what Tilt applies. More importantly this pod's ServiceAccount,
ClusterRole and RoleBindings *are* the security boundary — they must be reviewable
human-written diffs with comments explaining why each verb is granted.
`infra/app/rbac.yaml` is the most valuable prose in the repo.

### Telemetry is never gated on a dev flag

There is deliberately no global off switch in `ServiceDefaults`, and no `DEV_LOGGING`-style
early return. For a project whose product *is* telemetry, being blind in development is a
defect rather than a tradeoff — and it is how you end up shipping an agent that is blind in
production. Every Aspire convenience degrades instead: no OTLP endpoint means console plus
`/metrics`, never a crash and never silence.

## Tilt deploys the chart, not the manifests

`tilt up` renders `charts/hephaisto` with `values-dev.yaml` and applies that. Every start is
therefore a render test of the chart a consumer installs, and dev and prod cannot drift into
two sources of truth. `infra/app/*.yaml` is kept as the reference for what this cluster ran
before the chart existed; it is no longer applied.

Two consequences that cost an afternoon to find:

- **Moving an object between Tilt resources needs a RESTART, not a re-evaluation.** Tilt
  re-evaluated the switch to the chart happily and reported everything ok - then garbage
  collected the ServiceAccount, ConfigMaps and NetworkPolicies a second after applying them,
  because the *previous* Tiltfile's `uncategorized` resource still owned them and they were no
  longer in its set. The symptom was `serviceaccount "hephaisto" not found` on a pod that
  could not start. Stop Tilt and `tilt up` again; never `tilt down`.
- **`values-dev.yaml` sets `securityContext.readOnlyRootFilesystem: false` and a 3Gi limit,
  and both are load-bearing.** Tilt runs the DEV image, whose entrypoint is `dotnet watch` - a
  compiler. With a read-only root it dies at startup with `Read-only file system:
  '/app/src/Hephaisto.Core/obj/Debug'`, which reads like a permissions bug and is a design
  mismatch; at 1Gi it is OOM-killed about ninety seconds after every hot reload. The chart's
  defaults (read-only, 1Gi) are correct for the published image and wrong for this one.

## Tilt is the inner loop, on port 10351

**Run Tilt from `~/hephaisto`.** It coexists with the `~/dev` instance already running
detached on 10350; the two share a cluster but no ports.

```sh
cd ~/hephaisto && tilt up --host $HOST_IP --port 10351

# every other tilt CLI call needs the same two flags, because the client
# defaults to localhost:10350 and the server is no longer there
tilt logs   -f hephaisto        --host $HOST_IP --port 10351
tilt trigger  hephaisto         --host $HOST_IP --port 10351
```

`$HOST_IP` is the `host-ip` value from `tilt_config.json`. **`--host` is not optional.** The
port-forwards in the Tiltfile bind to that interface because each one passes `host=`
explicitly, but Tilt's own web UI is a separate server that defaults to `127.0.0.1` — so
without the flag every service below is reachable from another machine and the Tilt UI alone
is not, which reads as a network problem rather than a missing flag.

Because the forwards bind to that interface, **use that hostname, not `localhost`** — not
even from a shell on this machine. Throughout the docs it is `$H`:

```fish
set -x H (jq -r '.host // "localhost"' ~/hephaisto/tilt_config.json)
```

| What | Port | |
|---|---|---|
| Hephaisto UI | 8100 |
| Prometheus | 9090 |
| Grafana | 3030 |
| Alertmanager | 9093 |
| Loki | 3100 |
| Tempo | 3200 |
| OTel Collector (OTLP grpc / http) | 4317 / 4318 |
| grafana-mcp | 8200 |
| Aspire Dashboard | 18888 |
| Postgres | 5433 |
| Tilt UI | 10351 | (needs `--host`, see above) |

### The Gemini key

`scripts/bootstrap-secrets.sh` creates every secret except this one, which it skips unless
`HEPHAISTO_GEMINI_API_KEY` is exported. The alternative, and the easier one:

```sh
$EDITOR secrets/hephaisto-llm.secret.yaml     # replace the placeholder
kubectl apply -f secrets/hephaisto-llm.secret.yaml
kubectl -n hephaisto rollout restart deploy/hephaisto
```

It uses `stringData`, so the key goes in verbatim — no base64. The file is gitignored twice
(`secrets/` and `*.secret.yaml`) and is the only place in the repo allowed to hold a live
credential.

**A wrong key is quiet.** The agent does not crash without a valid one: the model call fails,
the incident escalates with the error, and detection, dedup, correlation and the UI carry on.
So confirm it took, rather than assuming — the chat span in Tempo carries
`gen_ai.usage.input_tokens` once a call actually succeeds:

```sh
curl -s -G "http://$H:3200/api/search" --data-urlencode 'q={name=~"chat.*"}' | jq '.traces | length'
```

### Never run a bare `tilt down`

It runs `helm uninstall` on the observability stack and takes the Grafana PVC with it. Every
dashboard and datasource here is declarative precisely so that losing the PVC costs nothing —
**do not create dashboards by hand in the Grafana UI**, they will not survive and they will
not be in git.

### Chaos fixtures never start on their own

`infra/chaos/` breaks the cluster in ten documented ways. Each scenario is
`auto_init=False, trigger_mode=TRIGGER_MODE_MANUAL`, so `tilt up` never brings up a
deliberately broken cluster by accident. `c9-memhog` causes node-level memory pressure —
**run it alone and deliberately.**

`infra/chaos/README.md` maps each scenario to the alert, PromQL, LogQL and Kubernetes event
it should produce. That table is the agent's regression suite; keep it accurate.

### Pin every chart version

A `helm_resource` without an explicit `--version` resolves "latest", which will silently
major-upgrade Prometheus on some future `tilt up`.

### Code fixes on the dev cluster (v0.9.0)

Six `tilt_config.json` toggles, all off in the sample:

| Toggle | Default | Effect |
|---|---|---|
| `coder` | `false` | builds `hephaisto/coder:dev`, deploys `coder-git`, layers `values-dev-coder.yaml` |
| `coder-mode` | `plan` | `codeFix.mode`: `off`, `plan` or `pr` |
| `coder-sdk` | `fake` | `fake` is a $0 scripted run with the `gh` shim; `real` spends subscription quota |
| `local-llm` | = `coder` | the agent investigates with Ollama at `host-ip:11434` (`gpt-oss:120b`) instead of Gemini |
| `investigator` | `false` | v0.12.0 F5: layers `values-dev-investigator.yaml` - `investigation.job.enabled`, executor `job`. Needs `coder` |
| `investigator-sdk` | `fake` | `fake` is a $0 scripted investigator that still calls the agent's real investigator endpoint; `real` spends quota |

Three things that are not obvious:

- **The coder image is a `local_resource`, not a `custom_build`.** Nothing Tilt applies runs it -
  the agent names it in `CodeFix__Image` and creates Jobs from it - so it is built as the fixed tag
  `hephaisto/coder:dev` into the node's docker with `pullPolicy: Never`. The next Job after a
  rebuild uses it; the agent does not restart. The two must be of one version: the agent starts
  the image in three roles (#116), and an image from before v0.13.0 does not know them.
- **`coder-git` stands in for GitHub** (`infra/coder/git-server`): bare repos over smart HTTP, push
  enabled, seeded at build time from `~/hephaisto-fixture-dotnet` and `~/dev/dev-context` by
  `scripts/coder-git-seed.sh`. Pushed branches live in an emptyDir; `tilt trigger coder-git`
  re-seeds and resets them. `curl` its `/` through a port-forward to see every branch.
- **git ignores `HTTP_PROXY` for `http://` URLs** - only lowercase `http_proxy` counts - so a
  coder's clone of `coder-git` bypasses the egress proxy. `git-server.yaml` carries a dev-only
  NetworkPolicy that allows exactly that. Do not "fix" it by widening the chart's coder policy.

## Alert rules must declare a `hephaisto_kind` that is a real `SignalKind`

Every `PrometheusRule` carries a `hephaisto_kind` label whose value has to be a member name of
`Hephaisto.Core.Domain.SignalKind`. That label is how an alert selects the runbook the model
is given.

`Enum.TryParse` fails **silently** on anything else, and the classifier then falls back to
guessing from the alertname — which for a name like `KubeContainerWaiting` yields `Unknown`
and the default runbook instead of the image-pull one. Nothing about that is visible from
either side: the YAML looks well-labelled and the classifier looks correct.

`ShippedAlertRulesTests` reads the real files in `charts/hephaisto/files/alerts/` and fails if
any alert declares a kind that does not parse, or classifies as `Unknown`. If you add a rule
whose failure mode has no matching `SignalKind`, add the member **and its runbook** rather
than inventing a label value.

## Versioning: `minver-cli` reads no props file, so pass `-p` AND `-m`

`minver-cli` does not read `Directory.Build.props` at all. Every setting in there is invisible
to it, and it substitutes its own defaults silently. Two of them matter:

```sh
dotnet minver -t v                       # 0.0.0-alpha.0.47   <- WRONG twice, and looks fine
dotnet minver -t v -p main.0             # 0.2.1-main.0.20    <- right phase, wrong floor
dotnet minver -t v -p main.0 -m 0.3      # 0.3.0-main.0.20    <- what MSBuild stamps
```

`-p` matches `MinVerDefaultPreReleaseIdentifiers`; `-m` matches `MinVerMinimumMajorMinor`,
which is the floor a human raises when a milestone starts. Without `-m` the CLI keeps
auto-incrementing the patch from the last tag, so after `v0.2.0` it insists on `0.2.1` however
much the release actually contains.

Get this wrong in a build script and the image tag disagrees with the assembly inside it, which
surfaces as `/api/version` reporting something the registry has never heard of. **The `-m` form
is worse than that**, and it was live until 2026-08-30: `scripts/e2e/lib/build.sh` derives an rc
tag from this command, so it would have PUSHED `v0.2.1-rc1` for the v0.3.0 release - and
`release.yml`'s own guard would then refuse to publish, because MinVer under MSBuild disagrees
with the tag. A permanent public tag with nothing behind it.

**Do not hardcode the floor.** All four call sites - `ci.yml`, `release.yml`, `nightly.yml` and
`build.sh` - read it out of `Directory.Build.props`:

```sh
M=$(sed -n 's:.*<MinVerMinimumMajorMinor>\(.*\)</MinVerMinimumMajorMinor>.*:\1:p' \
      Directory.Build.props | head -1)
V=$(dotnet minver -t v -p main.0 ${M:+-m "$M"})
```

**Release candidates need no special handling.** `v0.0.1-rc1` is just a tag; MinVer resolves it
to `0.0.1-rc1` and the release workflow branches on `case "$V" in *-*)` to withhold the moving
image tags and mark the GitHub release prerelease. Measured, not assumed:

```
tag v0.0.1-rc1   -> 0.0.1-rc1      + 2 commits -> 0.0.1-rc1.2
tag v0.0.1-rc2   -> 0.0.1-rc2
tag v0.0.1       -> 0.0.1          + 2 commits -> 0.0.2-main.0.2
```

Note the second column: while an rc is the newest tag, main builds are `0.0.1-rc1.N`, not
`0.0.2-main.0.N`. MinVer appends height to an existing prerelease rather than incrementing past
it. Harmless - the ordering is still correct - but do not "fix" it.

The dev image reports `0.0.0-dev` on purpose: there is no `.git` inside the build context,
and a dev image reporting a release-shaped number is how a dev build gets mistaken for a
release in a screenshot.

## The agent can act, as of v0.2.0

It ships unable to: `policy.actionableNamespaces` empty, `policy.autoEnabledActionTypes` empty,
`mode: Observe`. On **this** cluster `values-dev.yaml` sets `hephaisto-chaos`, pre-approves
`RestartPod`, and leaves `mode: DryRun` — so the whole flow runs, every Kubernetes call carries
`dryRun=All`, and nothing changes. Flipping `mode` to `Auto` in that file is the single line
that turns autonomy on, and it is deliberately the only thing left to change.

**The mode is a Helm value and nothing else can raise it.** There is no API and no UI control
that sets it; the database arm carries the runaway latch and is otherwise silent. If you find
yourself writing an UPDATE against `agent_mode` to make the agent act, the thing to change is
`values-dev.yaml`.

Two traps worth knowing before you test acting:

- **Every actionable namespace must also carry
  `hephaisto.dev/destructive-actions-allowed: "true"`.** `infra/namespaces.yaml` sets it on
  `hephaisto-chaos`. Without it the policy engine denies, and the reason says so — but only on
  the action row, so it reads as a mysterious refusal if you are watching the pod logs.
- **`c11-transient` remembers.** Its generation counter lives on a PVC, so deleting only the
  Deployment leaves it at 2 and the fixture comes back healthy — which looks exactly like the
  agent fixing something it never touched. Delete the PVC to reset it.

## Code fixes, as of v0.9.0

A second, separately gated stage: an escalation whose grounded primary finding points at code,
on a workload mapped in `codeFix.repositories`, starts a **coder** Job in `hephaisto-coder`
(`coder/`, Claude Code through the Agent SDK). Plan automatically, implement only after a human
approves, and only a person merges. The pull request is a draft - always, or with
`defaults.pr.draft: unless-ready` in dev-context's `repos.yaml` only when the runner had no check
to run or a deviation from the approved plan was reported (#297). Five things to know before
touching it:

- **Its own mode, and silence is Off.** `CodeFixMode {Off, Plan, Pr}` - env `CodeFix__Mode` plus
  the `codeFixMode` key of `hephaisto-switches`, most restrictive wins, and the agent's kill
  switch (Off, emergency stop, runaway latch) overrides it *by arm*. `AgentMode.Observe` does not
  refuse; that is the point.
- **The contract is vendored.** `dev-context/schemas` (TrueRelevance/dev-context) is the source of
  truth; `scripts/sync-schemas.sh` copies it into `src/Hephaisto.Agent/CodeFix/Contract/schemas`
  and `coder/contracts` with a `SCHEMAS.lock`. Never edit the copies; parity tests on both sides
  fail on a hand edit.
- **Three containers, and the model's holds no git token** (v0.13.0, #116). One image, started
  as `prepare` (init: clones and restore, the GitHub and NuGet tokens), `coder` (the agent, build
  and tests, the model credential only) and - to implement - `publish` (push and pull request, the
  GitHub token), with `coder` as an init container there so it has ended before `publish`
  starts. Never set `shareProcessNamespace`, never hand `coder` another Secret key, and never
  make `publish` run git in `/work`: it pushes from a bundle it imported into its own `/tmp`
  (`coder/src/publish.ts` says why). An implementation's result is read from `publish`.
- **The coder answers through its log.** The last framed block, sha256 and byte count checked,
  read only from a pod the Job's controller created. It has no Hephaisto credential and no route
  in - do not add a callback. **The investigator (v0.12.0 F5) is the one exception, and it is
  narrow:** a pod labelled `app.kubernetes.io/name=hephaisto-investigator` may reach port 8084,
  `/investigate` only, with a token valid for one investigation until its deadline, serving that
  investigation's read-only tools and nothing else. A coder pod carries `hephaisto-coder` and the
  agent's ingress rule names the investigator label, so code fixes stay sealed. Do not widen the
  rule to the namespace, and do not route anything else onto that port.
- **`create jobs` exists in one namespace.** `RbacSelfCheck` refuses to boot if it is held
  cluster-wide, in `kube-system` or in an actionable namespace.

Locally: `tilt_config.json` `"coder": true` builds the coder image and an in-cluster git server
(`coder-git`, seeded from `~/hephaisto-fixture-dotnet` and `~/dev/dev-context`), `"coder-sdk":
"fake"` runs a scripted $0 coder, `"local-llm": true` points investigations at the host's Ollama
(`gpt-oss:120b`) - never Gemini. The end-to-end check is `scripts/e2e/codefix-local.sh`, which pins
itself to `studio-rancher-desktop` through a private kubeconfig. See `docs/verification.md`.

## Investigating in a Job, as of v0.12.0

`investigation.job`: the investigation's model loop as Claude Code in a Job, Hephaisto still the
tools (`src/Hephaisto.Agent/Investigation/Job/`, `coder/src/investigate.ts`). Four things to know:

- **Only the model loop moves.** `InvestigationRunner` asks `IInvestigationJobLoop` whether a Job
  takes the run and, if so, hands it the SAME wrapped tools; grounding, planning and persistence do
  not know the difference. Never let a Job report its own steps or evidence - that is what keeps
  grounding honest.
- **Its own axis, silence in-process.** `InvestigationExecutor {InProcess, Job}`: env
  `Investigation__Job__Executor` plus the `investigationExecutor` key of `hephaisto-switches`, most
  restrictive wins, not enabled / typo / emergency stop / latch all in-process.
- **Fallback, never a lost investigation.** A Job that cannot answer is replaced by the in-process
  loop (`JobFallback`); overflow runs in-process at once. Sessions are in memory on purpose - an
  agent restart orphans running Jobs and `InvestigatorJobSweeper` removes them.
- **A scripted investigator gets no model credential.** The runner refuses fake mode beside one,
  and the dev Secret holds a real token for the coder.

Locally: `"investigator": true` (needs `"coder": true`) with `"investigator-sdk": "fake"`, then
`scripts/e2e/investigate-local.sh` ($0; `--strict` is the release gate) and, not gating,
`scripts/e2e/investigate-model-local.sh` (Haiku). If a Tilt image build fails with "keychain cannot
be accessed", pre-pull its base image with a stub `docker-credential-osxkeychain` on PATH and
`tilt trigger` it; never `kubectl apply` a Tilt-built manifest by hand - it replaces the image.

Four things that made that suite red in October 2026, none of them in what it tests:

- **Do not delete the chaos fixtures between two runs.** The suite reuses the open incidents of
  `shop-api` and `catalog-api`. A fixture that is deleted and brought up again is a new incident
  each time, and the fourth within the hour is flapping (`Ingest:FlapThreshold` 3, quarantined
  for `FlapCooldown`, four hours): suppressed, not investigated.
- **One fixture is up to three incidents.** The watcher's, under the Deployment; the alert's that
  names the pod (`KubePodCrashLooping`), under that pod, by design; and - when an incident of the
  workload was closed by a person in the last 24 hours - that closed one, reopened by the
  Deployment-level alert although one is already open (alerts reopen by fingerprint before they
  correlate; watcher signals never reopen). Each is investigated, and there is one Job slot:
  `iv_wait_quiet` is why a scenario that needs the slot gets it.
- **I1 runs a real model**, and it may propose an action. The incident then waits for an approval
  and cannot be investigated again; `iv_reinvestigate` denies what is pending first.
- **A new agent pod first runs the build its image was made with**, for the seconds until Tilt
  has synced the tree and `dotnet watch` has rebuilt. A suite that restarts the agent (I10, G08)
  runs that old code against everything it replays. Before a gate run, rebuild the image under
  the tag the Deployment names, inside the VM:
  `rdctl shell sh -c 'cd ~/hephaisto && docker build -q -t <the Deployment's image> -f Dockerfile.dev .'`,
  then delete the pod.

## How an incident ends by itself, as of v0.13.0

- **An alert's incident** closes when Alertmanager resolves its last firing alert.
- **A watcher's incident** closes when its workload has run cleanly for `incidents.healedAfter`
  (`KubernetesWatcherService.ReportHealedAsync`, `SignalMapper.HealedWorkload`). The watcher asks
  the DATABASE what it has open; do not move that to in-memory state, a restart would orphan
  every incident it opened before. Three things keep a closed incident closed, and removing any
  of them reopens it: an old OOMKill in `lastState` is not a signal once the container has run
  cleanly, events older than the quiet period are dropped, and a healed pod produces no signal.
- **Everything else** - a deleted workload, a failed Job - is the sweeper's (`incidents.sweep`,
  off by default), which expires and never closes.

The pager install sets `healedAfter` to 20 seconds (P49). Do not copy that anywhere a crash loop
is real: its back-off reaches five minutes, and a container between two crashes looks healed.

## Pod logs: Loki first, as of v0.13.0

`grafanaMcp.podLogSelector` puts a LogQL selector on the model's environment card and tells it
to read pod logs in Loki before `get_pod_logs`. The label names are the log shipper's, which is
why it is a value and not a default: this stack's collector writes `k8s_namespace_name` and
`k8s_pod_name`, promtail writes `namespace` and `pod`. A runbook that needs logs should say
"the previous container's logs" and name both sources, never `get_pod_logs` alone.

## GitHub issues as work, as of v0.14.0

`github.enabled`: an issue assigned to the agent's account, in a listed repository, is a
`WorkItem` (`src/Hephaisto.Agent/GitHub/`, `src/Hephaisto.Agent/WorkItems/`, table `work_items`).
Built: the client, the poller, the work item, `github` in `/api/status` (#245); a code fix
without an incident (#246) - the plan Job runs for a taken work item, and the plan is a comment
on the issue; the answer on the issue, the pull request followed to its end, `Done` (#247);
the console, MCP and notifications for a work item (#248); and the issue as a conversation -
the planner's questions on the issue, `/replan`, a fresh assignment known by its time (#286,
#252, #285). Five things to know about the poller:

- **The poller is level-triggered: no queue, no retry state.** A pass states what should be true
  and makes it so; the next pass is the retry. Do not add "remember what failed". What comes
  after taking an issue belongs in the same pass, asked every time ("every taken work item has
  an attempt"), never hung on the moment a row is created.
- **A 304 skips the comparison, and one rule makes that sound:** a list's ETag is kept only when
  everything the list called for was done. Keep the tag after a write or a read failed and the
  failure waits until somebody touches an issue.
- **A rate limit is honoured in the client** (`GitHubRateLimit`, one per process), and the
  client's resilience handler is removed on purpose. Never wrap a call in a retry.
- **`Body` is a snapshot.** A later edit of the issue is not copied, and an issue assigned again
  after a cancel is a NEW row - the partial unique index allows one `Taken` row per issue. It is
  read again at exactly one kind of moment: a person asks for a new plan on purpose (`/replan`,
  a fresh assignment), inside the transaction that records the request.
- **A connection probe must be registered in `AddHephaistoWeb`.** The first one there is a
  `TryAdd` on the service type, so a probe registered earlier makes Postgres's row vanish.

And six about an attempt that is for a work item (`GitHubIssuePoller.Work.cs`,
`CodeFixCoordinator.EvaluateWorkItemAsync`, `WorkItems/IssueComments.cs`, `coder/src/subject.ts`):

- **An attempt has exactly one subject**, `IncidentId` or `WorkItemId` - a check constraint, and
  one partial unique index each. Everything after the start asks `CodeFixSubject`, never
  `attempt.IncidentId`: a new path that loads `db.Incidents.First(...)` for an attempt throws for
  every issue. Since #248 every surface shows both kinds - except the Teams BOARD, which stays a
  board of incidents on purpose.
- **One OPEN attempt per work item, at most `WorkItem.MaxAttempts` in a row, and the next one
  only when a person asked.** Asking is a pointer, not a queue: `WorkItem.ReplanAfterAttemptId`
  names the attempt a new plan is wanted AFTER, written by `CodeFixCoordinator.ReplanWorkItemAsync`
  (an approver's `/replan`) or `HandOverAgainAsync` (a fresh assignment) under the work item's
  row lock, and "wanted" is true while that attempt is still the newest - so the poller's
  `PlanAsync` makes it true on any pass, like a first plan. Do not start the Job from the door.
  "Not now" (a cap, a switch) is no attempt at all: it is asked again on every pass and written
  down - audit row, `WorkItem.DeclineReason`, the status comment - only when the reason CODES
  change. A replanned WAITING plan ends as `Denied` ("replanned by github:<login>"), never
  `Cancelled`: Cancelled tells every route that a code fix failed.
- **The newest attempt is the one that counts**, by `(CreatedAt, Id)` - never `Single()` over a
  work item's attempts, and never the first. A new attempt inherits the old one's comment
  cursor (`CommandCommentId`), which is why the door writes the `/replan` comment's id itself:
  read again by the attempt it started, that comment would be answered "a Job is running".
- **Two comments, and the text is a function of the row.** The status comment is edited when its
  digest differs from `WorkItem.StatusCommentDigest`, so nothing that changes by itself (a clock,
  the mode, a counter) may go into `IssueComments.Status`. The plan comment is written once and
  never edited. Both end in a marker with their id, by which a restart finds what it wrote.
  GitHub first, the ids afterwards - `planCommentId` is visible only once both writes happened.
  The one thing of the plan the STATUS carries is a failed attempt's questions and notes: it
  has no plan comment.
- **Nothing of the issue's text is repeated in a comment, and a model's text goes through
  `IssueComments.Neutralise`** (the runner's counterpart for a pull request is `inert()`), which
  leaves a code span as it was written - GitHub acts on nothing inside one - except a span that
  holds `<!--`: no model may write one of the markers. A plan's `questions` are a numbered list
  and its `notes` a `<details>` fold (`AskedAndNoted`), but a note that speaks of injection
  (`CodeFixQueries.IsInjectionNote`) is counted and NOT quoted: that is where a model quotes
  what it was told to ignore. The one exception is a short note that only says there was none
  (it starts with a denial and holds no quotation mark, backtick, colon or line break) - widen
  it and a note that quotes gets through. A plan's summary and root cause keep their paragraphs
  and list items (`NeutraliseBlock`), and so does a step, indented under its number
  (`ItemBlock`); a note, a question and a file are one line. Nothing of a plan is cut below the
  plan result's own limits - `MaxBody` is what bounds a comment.
- **The request is contract version 2** (`codefix-request-v2.schema.json`, a file of its own;
  version 1 is byte for byte what it was, and a test holds it). The body is `WorkItem.Body`, the
  snapshot. It is written with the attempt's ROW (`RequestJson`), not when the Job is created:
  a relaunch sends the same document, and an implementing Job is told the comments its plan was
  told. A first plan carries no comment and no `previous`; a later one carries what the issue's
  AUTHOR and the APPROVERS wrote since the hand-over (`ConversationAsync` - nobody else's text
  reaches a Job, never the bot's own) and the earlier plan. `questions` and `previous` are
  OPTIONAL members and left out when null: do not make either required, every stored plan would
  stop parsing.

And seven about what is said on the issue, and the pull request (`GitHubIssuePoller.Answers.cs`,
`GitHubIssuePoller.Assignments.cs`, `GitHubIssuePoller.PullRequests.cs`, `WorkItems/IssueCommands.cs`):

- **A command is a comment's first non-blank line**: exactly `/approve`, exactly `/replan`, or
  `/reject` alone or followed by white space and a reason. Lower case, nothing before it, not
  indented into a code block. `IssueCommands.Parse` is pure and its test is a table - add the
  row before the rule. Strict in one direction on purpose: a comment wrongly read as a command
  pushes a branch. What follows a `/replan` line is an answer, and reaches the Job as the
  comment's text.
- **The comments of every taken work item's NEWEST attempt are read on every pass**, in every
  state, because `/replan` has to be refused in words while a Job runs and after a pull request.
  `/approve` and `/reject` outside a waiting plan are passed over in silence. The pass reads
  commands, then assignments, then plans, then puts the comments right.
- **The number decides, the login is shown.** `GitHub:Approvers` holds account ids; the actor is
  `github:<login>`, the source `ApprovalSource.GitHub`. With the list empty no comment is read
  at all and the plan comment says so. The poller's own audit row (`workitem.command`) is where
  the account's number and the comment's id are.
- **Once, without a queue.** The decision is the attempt's own state (not `PlanReady`: not asked
  about); `CodeFixAttempt.CommandCommentId` is the newest comment looked at, and a comment is
  never looked at twice - so a command counts by its text when first seen;
  `CommandAnswers` holds the keys of the one-time answers, each of which also carries a marker
  (`<!-- hephaisto:answer:<attempt>:<key> -->`) that a process which died after writing finds.
  The tags and `since` of the comment reads are memory only. Do not move any of it into memory.
- **It answers rarely, and never past `IssueComments.MaxPerAttempt`** for one attempt (five: its
  plan and four answers; with at most five attempts and the status comment that is
  `MaxPerWorkItem`, 26). One answer per attempt to non-approvers, one per attempt and cause for
  a refusal of a door's (`CodeFixRefusal` on the decision result - the console's message names
  arms and ConfigMaps and is never put on an issue). A new cause is a new member there, a key in
  `IssueComments.AnswerKey` and a sentence in `IssueComments.Refused`.
- **A fresh assignment is known by its time, and only for an attempt that ENDED**
  (`GitHubIssuePoller.Assignments.cs`, `IsNewHandOver`): the timeline is read for a taken work
  item whose newest attempt failed, was denied, expired or was cancelled, and for nothing else -
  a waiting plan is left alone. Two clocks are compared, so `WorkItem.AssignmentSeenAt` is what
  makes one assignment one hand-over; do not replace it with "newer than the attempt's end"
  alone. A timeline of more than a page comes without an ETag on purpose.
- **The pull request is read BEFORE the list of assigned issues**, and once more, without a
  tag, for a work item the list says is gone: a merge closes the issue it names, and read the
  other way round that is a cancellation. A work item that ends by its pull request is marked
  `StillAssigned`, and its issue is not taken again until one complete list did not hold it -
  remove that and a merged issue GitHub did not close is planned again on every pass, for ever.

And five about where a work item shows besides its issue (#248: `Components/Pages/CodeFixDetail.razor`,
`Pages/WorkItems.razor`, `Mcp/McpIncidentReader.WorkItems.cs`, `CodeFix/CodeFixNotifier.cs`):

- **An attempt has a page of its own**, `/codefixes/{id}`, for both kinds; `/workitems` lists
  what was taken. The plan is ONE component, `CodeFixPlan.razor`, used by that page and by the
  incident page's `CodeFixSection` - change the plan's rendering there and nowhere else. Why
  approve is unavailable is `CodeFixDoor.BlockedBecause`, also shared; the guard is still the
  coordinator's, by the attempt's subject (`DecideAsync` or `DecideForWorkItemAsync`).
- **Somebody else's text is text.** An issue's title, a plan, `prBody` and a rejection's reason
  are a stranger's, a model's or an approver's. Razor's encoding only: never `MarkupString`,
  never a markdown renderer, never a link built from them. An address becomes a link only
  through `Display.HttpUrl`. `codefix.spec.ts` compares `textContent` with the stored string
  and counts the elements inside: none.
- **The history is the row's own timestamps** (`CodeFixHistory.Of`), not an audit query - a work
  item's audit rows carry no incident id to be found by. That is why `Deny` records
  `ApprovalSource` now: "through what" for a denial was only in its audit row.
- **A work item's notification has no incident, and invents none.** `CodeFixNotifier.Enlist`
  has an overload per subject. The work item's snapshot leaves `IncidentId`, kind, namespace,
  cluster and labels empty and the severity at `Info`, so only an UNSCOPED route that lists the
  event takes it - the router is unchanged, do not teach it about work items. Its link is the
  attempt's page (`NotificationLinks.For`, `NotificationMessage.CodeFixUrl`). On a card its
  title and summary are TextRuns (`Plain`), never TextBlocks: a TextBlock renders markdown.
- **MCP: two reads, no write.** `list_work_items` and `get_work_item` are in
  `McpWorkItemTools`; `list_code_fixes` and `get_code_fix` show both kinds, and a row names
  `incidentId` OR `workItemId`/`issue`. `issue` is the one `[ServerAuthored]` string there - a
  validated repository name and a number; everything else of an issue is `McpText`. A third
  partial file of the reader means `McpToolSurfaceTests.The_reader_writes_nothing` has to read
  it, and it counts the files so that a fourth cannot be forgotten.

And three smaller ones:

- **`hephaisto.workitems.commands`** (`verb`, `outcome`) counts a command where its comment is
  put behind the cursor - once. `hephaisto.workitems.closed` carries `state` and `reason`.
  `workitem.command` and `workitem.done` are audit types with no incident id.
- **The stand-in's pull requests are numbered by the `gh` shim, from 1 in every Job.** A
  scenario that merges one has to `gh_pr_forget` it (G11), or the next one is found merged.
- **A commit message is the one text nobody neutralises.** A closing keyword there closes an
  issue on merge; the implement prompt forbids it and nothing checks. A pull request's
  description and title ARE made inert, for an incident as for an issue (`coder/src/pr.ts`).

The agent's token is `secrets.github`, a Secret of the agent's namespace - never the coder's
`hephaisto-codefix`, which it still cannot read. Locally: `"github": "stand-in"` layers
`charts/hephaisto/values-dev-github.yaml`; `curl "http://$H:8100/api/workitems?state=any"` and
`curl http://$H:8110/github/control/state` show both sides, and `scripts/e2e/issues-local.sh`
is how a change here is accepted (`scripts/e2e/README.md`; `issues/KNOWN_RED` lists
nothing - G13 to G17 landed there and left with #286 - and a new scenario may land there).
A plan is also answered by a reaction on its comment (#298, `GitHubIssuePoller.Reactions.cs`,
G18/G19): an approver's 🚀 approves, 👎 rejects, each reaction is read once
(`CommandReactionId`), and Hephaisto sets both on its plan comment itself.
A plan Job with the scripted coder is ready ten seconds after its attempt exists: a scenario
that needs "while a Job runs" uses the implementing Job (G15). After changing
`infra/e2e/notification-receiver` the stand-in's pod forgets every issue; and a hot reload
applies code and NOT a migration - after adding one, replace the agent's pod. G12 lowers the code-fix switch and
does not end before the MODE is back - the ConfigMap reaches the agent through a volume, up to
a minute after the key was put back. G01 also holds that a plan for an issue
reached a person's chat on the Teams stand-in, when the install routes `CodeFixPlanReady` to
the bot. `values-dev-github.yaml` raises `notifications.maxPerChannelPerHour` to the pager
suite's 1000 - it is layered AFTER `values-pager.yaml`, so nothing in it may be lower. The `github` row follows the poller by
up to a minute: it is served from `ConnectionHealthCache`. A plan is answered by hand with
`curl -X POST "http://$H:8110/github/control/repos/<owner>/<repo>/issues/<n>/comments" -H 'content-type: application/json' -d '{"body":"/approve","login":"maintainer","id":1001}'`
(1001 is the approver `values-dev-github.yaml` names; `"/replan"` the same way, and
`POST .../issues/<n>/reassign` is off and on again in one request), or through the API as before:
`curl -X POST "http://$H:8100/api/workitems/<id>/codefix/<attemptId>/approve" -H 'content-type: application/json' -d '{"decidedBy":"you"}'`.

### The live tier: github.com itself

`scripts/e2e/github-live.sh` (`scripts/e2e/README.md`, "The live tier") is the one suite that
talks to GitHub: `"github": "live"` layers `charts/hephaisto/values-dev-github-live.yaml` - the
real API through the egress proxy, the bot `tr-agent-dev`, ONE repository
(`TrueRelevance/hephaisto-sandbox`), the real `gh` in the Job, the model still scripted. Run it
before a release candidate and after changing anything Hephaisto sends GitHub or writes there.
Six things to know:

- **It refuses rather than guesses**: any other repository on the agent, a real coder, the shim,
  a `gh` that is not an approver, Actions enabled on the sandbox, leftovers of an earlier run
  (`--sweep` removes those), and a second install on the sandbox. Do not loosen a refusal to
  get a run through.
- **The bot's token is an account's, and whoever else holds it and lists the sandbox plans the
  suite's issues too** - with a real model, if that is what it runs. On 2026-10-08 production
  listed the sandbox: one run of this suite was four real plans there. Before a run, make sure
  no other install lists `TrueRelevance/hephaisto-sandbox`; the runner can only see it
  afterwards, in the status comments of the last run's issues (`live_foreign_takers`), and in
  this run's after every scenario, where it stops. After a run that met one, the refusal is
  lifted once with `--other-install-gone` - by whoever took the sandbox off that install and
  saw the rollout, never to get a run through.
- **Never read the two tokens.** They are in `hephaisto-github` (namespace `hephaisto`) and
  `hephaisto-codefix` (namespace `hephaisto-coder`), they see more than the sandbox, and nothing
  here needs their value: the suite plays the person with the `gh` of whoever runs it.
- **`lib/live.sh` names the repository itself.** Every call goes through `_live_api` or
  `_live_gh`; `LiveSuiteTests` fails on a bare `gh`, a merge, a push, or a delete of anything
  but a `hephaisto/codefix-<id>` branch. It never merges - `main` must stay the fixture's c15
  commit, or the scripted patch stops applying - so "merged is Done" is the stand-in's (G11).
- **In this mode the incident suites cannot run**: the values file empties
  `codeFix.repositories` (the real `gh` refuses coder-git). Switch back to `"stand-in"` after.
- **The sandbox is in dev-context's `repos.yaml` on a branch** (`feat/hephaisto-sandbox-repo`,
  which `codeFix.contextRepository.ref` names) until that is merged; coder-git serves the LOCAL
  checkout's branches, so after pulling dev-context: `scripts/coder-git-seed.sh`, rebuild
  `infra/coder/git-server` as `hephaisto/coder-git:manual` in the VM, delete its pod.

What GitHub taught on the first afternoon, so that it is not relearned: **`/issues/12` is a
reference by itself** (and `/pull/12`, `/discussions/12`, `owner/repo/issues/12`), so a broken
scheme never made an address inert - `IssueComments.Neutralise` and the runner's `inert()` both
break the slash before a digit now, and a new rule in one belongs in the other. `POST /markdown`
with `mode=gfm` and a `context` repository renders text as a comment would be, without writing
one: ask it before believing a neutralisation. And GitHub fails for minutes at a time - an
empty-bodied error on a write, a 500 on a comment, `closingIssuesReferences` empty for eight
minutes - so a red live run is read request by request before it is believed.

## The planner, as of v0.15.0

`github.planner`: `/planner` in the console walks ONE project's open issues with a person
(`src/Hephaisto.Core/Planning/`, `src/Hephaisto.Agent/Planner/`, `Components/Pages/Planner.razor`,
`GitHub/GitHubClient.Projects.cs`; #300). Eleven things to know before touching it:

- **Nothing in it runs by itself.** No timer, no pass: a read is a page load or the API, a write
  is a click. So the agent's mode does not gate it, and `PlannerService.cs` is the ONLY file that
  may call the client's writes for a project, a label or an assignee -
  `GitHubClientTests.Nothing_but_the_planner_calls_what_closes_or_takes_away` holds that. Do not
  call them from the poller.
- **A click acts on the kept board and does not read again.** The kept board is patched the way
  GitHub was told to; reading first cost seconds per click and could hand back a replica that had
  not heard of the click before. A page load serves the kept board for `PlannerService.KeepFor`.
- **The board is read ahead** (#306): `PlannerWarmer` calls `PlannerService.WarmAsync` at startup
  and every `WarmEvery`. It fetches outside the gate and drops its result if `patches` moved -
  a click landed meanwhile - so it can neither block a click nor put one back.
- **The walk keeps its own order** (`_order` in `Planner.razor`): a click changes an issue's
  reason and its place in the queue, and the issue a person looks at must not move away. Today,
  not today and closing go on to the next issue; a closed issue's card is one step back, from
  `_closed`.
- **The description is the board read's, and its markdown goes through `SafeMarkdown`.** `body`
  comes with the project query, cut at `GitHubClient.MaxIssueBody`; GraphQL sends `""` where REST
  sends null. `Components/SafeMarkdown.cs` (Markdig) renders it - and comments, Claude's analysis,
  a PR body - with HTML disabled, links only to http(s)/mailto and images turned into links. Never
  hand somebody else's text to a `MarkupString` any other way.
- **The rules are facts, not column names** (`PlannerQueue`): no rule may know "In review". The
  only names it knows are the fields `Status` and `Priority`, for the order.
- **The day is two labels and their age**, from the newest `labeled` event of the issue's
  timeline. Setting a day takes BOTH labels off first, also the one that is asked for: a label
  that is already there keeps its old date, and the date is what says which day it is for.
  `active` is a third label - the one issue worked on now, for the whole board: `SetActiveAsync`
  takes it off every other open issue, and a day other than today takes it off its own. A click
  that changes more than one item goes through `ActManyAsync`, which patches all of them.
- **GraphQL answers 200 with `errors`**, its rate limit included (`GraphQlAnswer`). A read with
  an error beside a usable answer is an answer; a write with any error wrote nothing.
- **Razor drops the spaces around a conditional block.** Pieces that sit on one line go into
  `hp-planner-ids`, not next to each other with a space between them.
- **A new issue is three requests, and none is taken back.** `PlannerService.OpenIssueAsync` opens
  it, puts it on the project, sets the fields; a failure after the first says which issue was
  opened. The service method is `OpenIssueAsync` and not `CreateIssueAsync` on purpose: the write
  guard's regex matches the client's call by name.
- **Asking Claude is a coder Job of phase `triage`** (`Planner/TriageRunner.cs`,
  `TriageJobSpec.cs`, `coder/src/triage.ts`, contract `triage-*.schema.json` in dev-context). The
  run is in memory, the answer on GitHub: labels the repository has, and one comment that starts
  with `PlannerService.AnalysisMarker`, believed only from the agent's own account. Applying it
  goes through `PlannerService.ApplyTriageAsync` - the runner calls no write itself.
- **GitHub loses items, and a board acts by itself.** An issue added to a project sometimes gets
  an item and is never listed; `auto-close issue` closes an issue whose status reaches the last
  column. Both are in `scripts/e2e/planner-live.sh` and its spec, which is why they look the way
  they do.

Locally: `"github": "stand-in"` turns the planner on against the stand-in's seeded board
(`infra/e2e/notification-receiver/GitHubStandIn.Project.cs`;
`curl http://$H:8110/github/control/project`) - with `"coder": true` also asking Claude, as the
scripted `coder/fake-scripts/default.triage.json` when `"coder-sdk": "fake"` - and
`HEPHAISTO_URL=http://$H:8100 GITHUB_STANDIN_URL=http://$H:8110 scripts/e2e/ui/run.sh planner.spec.ts`
is how a change to it is accepted. After changing the stand-in, rebuild its image in the VM and
restart `deploy/teams-stand-in`. `scripts/e2e/planner-live.sh` is the same suite against
github.com and needs a token that can see the project - the bot's cannot; its header says how.

## The Teams bot, as of v0.9.0-rc4

`notifications.teamsBot`: one board in a channel, edited in place, and alerts by personal chat.
`src/Hephaisto.Agent/Notifications/TeamsBot/`. Four things to know before touching it:

- **It must never delete.** Teams leaves "This message has been deleted." behind for a channel
  post and for a reply alike. `ITeamsBotClient` has no delete and a test asserts it stays that
  way. A message goes away by being edited into something smaller.
- **An edit notifies nobody.** Anything that needs a person is a NEW message, to their personal
  chat with the bot. Do not "simplify" an alert into an edit of the board.
- **Its cards show the present, not a snapshot** - the one channel where that is true. The
  reconciler compares on a timer; there is no queue of edits. What a lock screen announces is
  kept out of the content hash, and hashing it back in edits every alert once for no reason.
- **A channel must be constructible by a singleton.** `NotificationChannelProbe` holds every
  channel for the life of the process, so a channel takes `IServiceScopeFactory`, never a
  `HephaistoDbContext` ([#121](docs/backlog.md#121)).
- **There is exactly one inbound route from Microsoft**, and it is off by default:
  `POST /api/teams/messages` for the buttons that act (`notifications.teamsBot.actions`, #124):
  acknowledge, assign to me and reinvestigate for any member of the team; close for the Entra
  object ids in `actions.approvers`; approve and deny for the same people, and only with
  `actions.approvals.enabled`. Its own port (8082) where nothing else answers, its own
  `BotFramework` JWT scheme - never the console's - and a key endorsement check a stock validator
  skips. Do not map anything else onto that port, and do not add a verb without a handler and a
  test (`TeamsBotVerbs`).
- **A card is the same for everybody**, so a button is never hidden per person. What is drawn
  follows the configuration and the incident's state; who may press it is decided at the click,
  and each click calls the `IncidentQueries` method the console calls - never the database.

Locally: `tilt_config.json` `"teams-bot": "stand-in"` deploys `teams-stand-in` in `hephaisto-obs`
and points the agent at it; `curl http://$H:8110/teams/messages` shows what Teams would show, and
`curl -X POST http://$H:8110/teams/click -H 'content-type: application/json' -d '{"incidentId":"<id>","verb":"acknowledge","user":"oncall@example.com"}'`
signs a click the way Microsoft would and delivers it to the agent's actions port (vary `appId`,
`tenant`, `user` or `"endorse": false` to see each refusal). `"verb": "close"` takes a `"reason"`,
`"approve"` and `"deny"` an `"actionId"`, and only `oncall@example.com` is an approver there: the
stand-in derives an object id from the address (`curl http://$H:8110/teams/members` lists them),
and `values-dev-teams-bot.yaml` names that one.
The pager suite cannot make an action wait for approval (its model plans nothing), so an approval
that runs is unit-tested and has to be clicked through by hand here.
`"real"` needs `charts/hephaisto/values-dev-teams-bot.local.yaml` (ignored by git) and the Secret
`hephaisto-notification-teams-bot`, both made by hand. **Do not test against a channel people
read**: a test board cannot be removed afterwards.

## The pager suite, as of v0.10.0

`scripts/e2e/pager.sh` is how a change to anything between an alert arriving and a person being
told is accepted. **A change there is not done when its unit tests pass; it is done when its
scenarios are green here and in CI.** Nobody verifies paging by hand at every change, and before
this suite nothing could.

```sh
# tilt_config.json: "pager-e2e": true (overrides local-llm and teams-bot), then
scripts/e2e/pager-local.sh                  # every scenario, about 15 minutes
scripts/e2e/pager-local.sh --only P05,P09   # some
scripts/e2e/pager-local.sh --list           # what exists, and what is known red
```

- **The model is a stand-in** (`LlmStandIn.cs`, in the receiver's binary, Service
  `model-stand-in`): every investigation concludes at once with one ungrounded finding, so every
  incident escalates. `POST /llm/hold` makes it wait; `GET /llm/requests` is what it was asked.
- **`scripts/e2e/pager/KNOWN_RED`** lists scenarios that land before their fix. Green and still
  listed fails the run, so the fix and the removal land in one commit.
- **Windows are seconds** (`scripts/e2e/values-pager.yaml`). The suite relies on their order,
  never their size. A scenario gets its own alert names (`pager_name`), so nothing it asserts
  depends on another scenario or an earlier run.
- **The stand-in's image is a fixed tag.** After changing `infra/e2e/notification-receiver`,
  `kubectl -n hephaisto-obs rollout restart deploy/teams-stand-in`; pager-local.sh refuses a
  stand-in that predates the model.
- **Run it LAST when the chaos fixtures are up, or wait an hour after it.** Its values set
  `incidents.healedAfter` to 20 seconds, and a crash-looping `shop-api` or `catalog-api` looks
  healed between two crashes: on 2026-10-07 a fifteen-minute run opened four incidents for
  `shop-api` and five for `catalog-api`. `codefix-local.sh` started a minute later found c15
  flap-suppressed (`Ingest:FlapThreshold` 3 in an hour) and failed with "no primary finding".
  Nothing clears that but the hour.

## The console has a design language, as of v0.4.0

**Read [`docs/design.md`](docs/design.md) before changing any CSS.** Four rules there are enforced
by tests rather than by goodwill, and the first two will fail your build:

- **Colours live in `src/Hephaisto.Agent/wwwroot/tokens.css` and nowhere else.** A hex, `rgb()` or
  `hsl()` literal in `app.css` or `website/site.css` fails `./scripts/test.sh`. Two colours escaped
  before the rule existed and rendered near-black on dark red in light mode for three releases.
- **A token needs a consumer in the same commit** - the design form of the config rule above.
- Both themes are held to the same contrast bar, asserted in tests.
- State is never colour alone: glyph, word, then colour.
- **Since v0.15.0 the language is True Relevance v3** (docs/design.md says what that means): brand
  hues only as `--wash-*` fields, solid only as `--brand-*` circles behind a glyph; controls are
  pills, cards are `--radius-lg` with `--shadow-card`, no coloured left stripes and no rule under a
  heading. `.hp-submit` is ink as a form's primary and the yellow wash inside an `.hp-field` row.

Half the system is in C#. `Components/Display.cs` owns the glyph vocabulary, the enum-to-class
mapping and every number format; changing what a state looks like usually means touching both files.

### There is a visual safety net, and it is not the console suite

```sh
scripts/visual-test.sh            # compare against the committed baselines
scripts/visual-test.sh --update   # regenerate them, then LOOK at the diff
```

`scripts/e2e/ui` asserts BEHAVIOUR against a live agent and every one of its assertions passes
against a console whose layout has collapsed. The visual baselines are separate, need no cluster,
and photograph [`design/gallery.html`](design/gallery.html) - every component the language has to
keep working, rendered from the shipping stylesheet with frozen data.

**If you change a component, change it in the gallery too**, or the net stops covering it.

Always runs in the pinned Playwright container, including on this machine. Font rasterisation is
not portable, and baselines that fail in CI on antialiasing are baselines people learn to
re-baseline past. Requires docker.

### `website/` is the second consumer

`website/tokens.css` and the two font binaries are byte-identical copies of the console's, and a
test fails if they ever differ. Update them with `cp`, never by editing:

```sh
cp src/Hephaisto.Agent/wwwroot/tokens.css website/tokens.css
```

## Commit messages

Undocumented for three releases and derived from the log, so it is written down now: a conventional
type with an optional scope, then **a lowercase clause that makes a claim** rather than naming a
file. Frequently two clauses joined by a comma, where the second is the surprising half.

```
fix(policy): a workload with nothing Ready has no last replica to protect
fix(e2e): the receiver image could not build, so the notify phase tested nothing
build: v0.3.0 is a minor bump, and four scripts would have called it a patch
```

Bodies are long and hard-wrapped near 76 columns: the symptom, the wrong hypotheses, the evidence,
and what was actually verified. **A fix closes its issue in the same commit** (`Closes #n` in the body),
not in a sweep afterwards. `docs/backlog.md` is frozen since 2026-10-06: "backlog #N" in a comment
is an entry there, its issue has the same number from 77 up, and the file's last table names the
issue of every lower one - so a bare `#N` below 77 in a commit or a pull request is a pull request. Scopes in use: `pipeline`, `policy`, `safety`, `chart`,
`telemetry`, `persistence`, `web`, `e2e`, `notify`, `design`, `website`, `prompts`, `docs`.

**A change lands as commits on `main`, pushed - no branch and no pull request.** The owner's
decision of 2026-10-09, for this repository only. Three things follow from there being no gate
after you:

- **What a pull request's checks would have run, you run first**: `./scripts/test.sh`, and the
  integration tests, the coder's tests, the chart's and the visual ones where the change reaches
  them. A push to `main` starts `ci.yml` and `deploy.yml`, and `deploy.yml` publishes the three
  public sites; a red `main` is yours to fix before anything else.
- **The commit message is the whole record**: why, what was verified, what was not - what a
  pull request's description used to carry. One commit per piece of work, as before.
- **`git pull --ff-only` before, never a force-push after.** A mistake on `main` is fixed by a
  commit that says what was wrong. A release is still its own step, on the owner's word.

`CONTRIBUTING.md` is for everybody else and still asks for a pull request.

## The cluster is a single shared resource

There is one k3s node, shared with the whole stack in the `~/dev` workspace. Parallel agents
can draft code and run unit tests freely, but **cluster verification is serial**. Two agents
applying chaos fixtures at once produce garbage for both.

**The working tree is part of the cluster while Tilt runs.** Tilt watches `src/`, the chart, the
Tiltfile and `infra/`, so anything that rewrites them - `git stash`, a checkout, a rebase -
re-renders the chart and replaces the agent's pod, twice, under whatever suite is running. To
build or test one commit of a stack, use `git worktree add` somewhere else; committing is safe.

`.tiltignore` keeps `src/**/bin` and `src/**/obj` out of that: the agent's image is a
`custom_build`, which does not read `.dockerignore`, and without the file every local
`dotnet build` synced a macOS apphost over the pod's Linux one - the pod's next start was
`Exec format error`, with a green build above it. If you see that, touch a `.cs` file.

Images the agent only NAMES are not rebuilt by a pod restart, and a Tilt-triggered build can
fail on the macOS keychain in a background session. Build inside the VM:
`rdctl shell sh -c 'cd ~/hephaisto && docker build -q -t hephaisto/coder:dev coder'` (and
`-f infra/e2e/notification-receiver/Dockerfile -t hephaisto/notification-receiver:dev .` for the
stand-in, then delete its pod). The node's image GC removes a locally built tag nothing runs:
`ImagePullBackOff` on one of these means "rebuild it", not "push it".

## Verifying a change

Prefer the running cluster over reasoning about it.

```sh
cd ~/hephaisto && dotnet build && ./scripts/test.sh
tilt trigger hephaisto --host $HOST_IP --port 10351
kubectl -n hephaisto logs deploy/hephaisto --tail=50
curl -s http://$H:8100/healthz

# RBAC is actually bounded: first three must be "no", the last "yes"
kubectl auth can-i delete secrets             --as=system:serviceaccount:hephaisto:hephaisto -A
kubectl auth can-i delete pods -n kube-system --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i create clusterrolebindings --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i delete pods -n hephaisto-chaos --as=system:serviceaccount:hephaisto:hephaisto
```

The **five-hop correlation test** is the acceptance test for the whole observability stack —
exemplar → trace → logs → metrics → service graph. It is written out in
`docs/verification.md`; if all five hops work, the traces, exemplar, OTLP metrics, OTLP logs
and both Grafana correlation configs are simultaneously proven.

## Testing

### Run tests with `./scripts/test.sh`, not `dotnet test`

`dotnet test` **cannot run this suite** on the current toolchain. Without a `global.json`
`test.runner` entry it hard-errors; with one it starts the test executable in
`--server dotnettestcli` mode and exits in ~200 ms reporting *"Zero tests ran"*.

This was checked against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter, and
with and without central transitive pinning — identical every time, so it is xunit.v3's
server-mode support rather than anything in this repo. A xunit.v3 test project is an
executable, and running it directly works: every test discovers and runs, and the exit code
is honest (0 on pass, non-zero otherwise), so CI is safe.

There is deliberately **no `global.json` in this repo.** Adding one turns a loud, actionable
error into a quiet *"Zero tests ran"* — and in a repo whose tests are the safety argument,
the loud failure is worth more. Revisit after a xunit.v3 bump.

Assertions are **AwesomeAssertions**, the Apache-2.0 community fork of FluentAssertions,
API- and namespace-compatible. FluentAssertions 8.x moved to a paid Xceed licence for
commercial use, so bumping to it is a procurement decision rather than a dependency bump.

The tests covering `PolicyEngine` are not routine coverage — **they are the argument that L3
is safe.** Treat a change that weakens them as a change to the safety model.

### Local database

`./scripts/dev-db.sh up` starts a throwaway Postgres 17 + pgvector on port **5433** and
applies the migrations; `down` removes it. It is a plain container, not the cluster's
database, so it cannot disturb anything in k3s. `tests/Hephaisto.IntegrationTests` reads
`ConnectionStrings__hephaisto` and **throws** if it is unset, rather than skipping silently.

If the pull fails with *"keychain cannot be accessed"*, docker's credential helper cannot
reach the macOS keychain from a non-interactive shell — run the script from a normal
terminal, or unlock the keychain once with
`security -v unlock-keychain ~/Library/Keychains/login.keychain-db`.

**The agent does not start without a database.** `Program.cs` awaits
`MigrateHephaistoDatabaseAsync()` before `RunAsync()`, and `AddHephaistoPersistence` throws at
registration when there is no connection string. That is deliberate — an agent that cannot
persist must not pretend to be healthy, because "no audit, no action" is only enforceable if
the audit store is known to be there.
