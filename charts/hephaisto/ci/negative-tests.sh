#!/usr/bin/env bash
#
# The properties this chart exists to hold, asserted as tests.
#
# Every check below is a NEGATIVE: something the chart must REFUSE to render, or something
# that must never appear in its output. Positive rendering is covered by `helm template` in
# CI; these are the ones that rot silently, because a chart that quietly starts granting
# `secrets` still installs perfectly and still looks fine in a diff.
#
#     charts/hephaisto/ci/negative-tests.sh
#
set -uo pipefail

CHART="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PASS=0
FAIL=0

pass() { printf '  ok    %s\n' "$1"; PASS=$((PASS + 1)); }
fail() { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL + 1)); }

# Renders and expects FAILURE. The message must also be explanatory: a `fail` with no reason
# sends someone to read the template instead of the values file they got wrong.
refuses() {
    local what="$1"; shift
    local out
    if out=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "$@" 2>&1); then
        fail "$what -- rendered successfully, but must be refused"
    elif grep -qi "may not contain\|may not set\|may not be\|may not map\|is required\|is refused\|don't meet the specifications of the schema" <<<"$out"; then
        # values.schema.json rejects some of these before a template runs, which is an
        # earlier and better refusal than a `fail` in a template. Both count.
        pass "$what"
    else
        fail "$what -- refused, but with an unexplained error: $(head -2 <<<"$out" | tr '\n' ' ')"
    fi
}

renders() {
    local what="$1"; shift
    if helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "$@" >/dev/null 2>&1; then
        pass "$what"
    else
        fail "$what -- must render, but did not"
    fi
}

echo "The write Role refuses namespaces it must never hold delete on:"
refuses "kube-system"                 --set 'policy.actionableNamespaces[0]=kube-system'
refuses "kube-public"                 --set 'policy.actionableNamespaces[0]=kube-public'
refuses "default"                     --set 'policy.actionableNamespaces[0]=default'
refuses "its own release namespace"   --set 'policy.actionableNamespaces[0]=hephaisto'
refuses "the observability namespace" --set 'policy.actionableNamespaces[0]=hephaisto-obs' --set observabilityNamespace=hephaisto-obs
refuses "a bad namespace hidden behind a good one" \
    --set 'policy.actionableNamespaces[0]=app' --set 'policy.actionableNamespaces[1]=kube-system'

echo
echo "The cluster is named, once, by a value with no default (#139):"
# Every render above and below passes --set cluster.name, so none of their refusals can pass
# for this reason instead of their own. This one takes it away again.
refuses "no cluster name"             --set cluster.name=""
refuses "a legacy cluster key in extraEnv" \
    --set 'extraEnv[0].name=Ingest__ClusterName' --set 'extraEnv[0].value=eu-1'
refuses "a price set through extraEnv" \
    --set 'extraEnv[0].name=Llm__Prices__0__Model' --set 'extraEnv[0].value=x'
refuses "the sweeper set through extraEnv" \
    --set 'extraEnv[0].name=IncidentSweep__Enabled' --set 'extraEnv[0].value=true'
refuses "the heal period set through extraEnv" \
    --set 'extraEnv[0].name=Kubernetes__HealedAfter' --set 'extraEnv[0].value=00:01:00'
refuses "a heal period that is not hh:mm:ss" \
    --set incidents.healedAfter=10m
refuses "a pod log selector that names no pod" \
    --set-string 'grafanaMcp.podLogSelector={namespace="<namespace>"}'
refuses "a price without a model" \
    --set 'llm.pricing[0].inputPerMillionUsd=1' --set 'llm.pricing[0].outputPerMillionUsd=1'

echo
echo "Secret names are required rather than silently dangling:"
refuses "no postgres secret name" --set secrets.postgres=""
refuses "no llm secret name"      --set secrets.llm=""
refuses "grafana-mcp url with no token secret" \
    --set grafanaMcp.url=http://grafana-mcp:8000/mcp --set secrets.grafanaMcp=""

echo
echo "Outbound notifications refuse to be half-configured:"
# A signed channel with no key would render a dangling secretKeyRef and surface twenty minutes
# later as CreateContainerConfigError on a pod nobody is watching yet.
refuses "a signed webhook with no secret name" \
    --set notifications.webhook.url=https://r.example/hook \
    --set notifications.webhook.signed=true \
    --set secrets.notificationWebhook=""
refuses "teams enabled with no secret name" \
    --set notifications.teams.enabled=true \
    --set secrets.notificationTeams=""

# The bot needs four things together. Three missing ones are refused here rather than at
# startup, and the fourth - the secret - can only ever be named, never given.
BOT=(--set notifications.teamsBot.enabled=true
     --set notifications.teamsBot.tenantId=f4447a03-0000-0000-0000-000000000000
     --set notifications.teamsBot.appId=58b00500-0000-0000-0000-000000000000
     --set 'notifications.teamsBot.channelId=19:abc@thread.tacv2')

refuses "the teams bot enabled with no secret name" "${BOT[@]}" --set secrets.notificationTeamsBot=""
refuses "the teams bot enabled with no channel"     "${BOT[@]}" --set notifications.teamsBot.channelId=""
refuses "the teams bot enabled with no app id"      "${BOT[@]}" --set notifications.teamsBot.appId=""
refuses "the teams bot enabled with no tenant"      "${BOT[@]}" --set notifications.teamsBot.tenantId=""
refuses "a board larger than was ever measured"     "${BOT[@]}" --set notifications.teamsBot.board.maxIncidents=41
# The schema is closed, so the one way to put the credential in a values file does not exist.
refuses "the teams bot's client secret as a value"  "${BOT[@]}" --set notifications.teamsBot.clientSecret=hunter2

# Buttons that act (#124): the one inbound route Microsoft calls. It belongs to a bot, and its
# port must be its own - exposing it must expose neither the console nor the webhook.
refuses "buttons that act without a bot" --set notifications.teamsBot.actions.enabled=true
refuses "buttons that act on the console's port" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true --set notifications.teamsBot.actions.port=8080
refuses "buttons that act on the webhook's port" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true --set webhookPort=8081 --set notifications.teamsBot.actions.port=8081
refuses "the actions switch set behind the chart's back" \
    --set 'extraEnv[0].name=Notifications__TeamsBot__Actions__Enabled' --set 'extraEnv[0].value=true'

# Who may close from a card (#124): Microsoft Entra object ids, and nothing that only looks like
# a person. An address would never match a click's from.aadObjectId, so it would map nobody.
APPROVER=6bc676d8-34ad-8cef-60ff-07a371204c78
refuses "an approver named by address instead of object id" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true \
    --set 'notifications.teamsBot.actions.approvers[0]=oncall@example.com'
refuses "an approver's object id without its dashes" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true \
    --set-string 'notifications.teamsBot.actions.approvers[0]=6bc676d834ad8cef60ff07a371204c78'
refuses "an approver added behind the chart's back" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true \
    --set 'extraEnv[0].name=Notifications__TeamsBot__Actions__Approvers__0' --set "extraEnv[0].value=$APPROVER"

# With an approver named, too: a list of approvers is not a reason for a route to exist.
OFF=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER" 2>&1)
if grep -q 'teams-actions\|Notifications__TeamsBot__Actions' <<<"$OFF" || ! grep -q 'kind: Deployment' <<<"$OFF"; then
    fail "with the buttons off, nothing of the inbound route is rendered"
else
    pass "with the buttons off, nothing of the inbound route is rendered"
fi

APPROVERS=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" \
    --set notifications.teamsBot.actions.enabled=true \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER" \
    --set 'notifications.teamsBot.actions.approvers[1]=0D6C2BEB-1B06-5CBD-AA54-5B6D1184A188' 2>&1 \
    | grep -A1 'name: Notifications__TeamsBot__Actions__Approvers__' | grep 'value:' | tr -d ' "' | tr '\n' ' ')
if [ "$APPROVERS" = "value:$APPROVER value:0D6C2BEB-1B06-5CBD-AA54-5B6D1184A188 " ]; then
    pass "the approvers reach the pod as the list they were written as"
else
    fail "the approvers must reach the pod in order, found: '$APPROVERS'"
fi

NOBODY=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" \
    --set notifications.teamsBot.actions.enabled=true 2>&1)
if grep -q 'Notifications__TeamsBot__Actions__Enabled' <<<"$NOBODY" && ! grep -q 'Actions__Approvers' <<<"$NOBODY"; then
    pass "with the buttons on and nobody named, nobody is an approver"
else
    fail "the default must map nobody to the approver role"
fi

# Approve and Deny from a card (#124): the one click that can change the cluster, so a switch of
# its own - which needs the route to exist and somebody who may press the buttons.
refuses "approvals from a card without the buttons" \
    "${BOT[@]}" --set notifications.teamsBot.actions.approvals.enabled=true \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER"
refuses "approvals from a card with nobody mapped to approve" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true \
    --set notifications.teamsBot.actions.approvals.enabled=true
refuses "approvals switched on behind the chart's back" \
    "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER" \
    --set 'extraEnv[0].name=Notifications__TeamsBot__Actions__Approvals__Enabled' --set-string 'extraEnv[0].value=true'

# The buttons and an approver do not turn approvals on.
if grep -q 'Actions__Approvals' <<<"$NOBODY" || helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" \
    --set notifications.teamsBot.actions.enabled=true \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER" 2>&1 | grep -q 'Actions__Approvals'; then
    fail "approvals must stay off until notifications.teamsBot.actions.approvals.enabled says otherwise"
else
    pass "with the buttons on and an approver named, approvals are still off"
fi

APPROVALS=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" \
    --set notifications.teamsBot.actions.enabled=true \
    --set "notifications.teamsBot.actions.approvers[0]=$APPROVER" \
    --set notifications.teamsBot.actions.approvals.enabled=true 2>&1 \
    | grep -A1 'name: Notifications__TeamsBot__Actions__Approvals__Enabled' | grep -c 'value: "true"')
if [ "$APPROVALS" = "1" ]; then
    pass "approvals asked for with the route and an approver reach the pod"
else
    fail "approvals must render as Notifications__TeamsBot__Actions__Approvals__Enabled=true, found $APPROVALS"
fi

ON=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true --set networkPolicy.enabled=true 2>&1)
ACTION_RULE=$(printf '%s' "$ON" | python3 -c '
import sys, yaml
for doc in yaml.safe_load_all(sys.stdin):
    if doc and doc.get("kind") == "NetworkPolicy" and doc["metadata"]["name"].endswith("-ingress"):
        for rule in doc["spec"]["ingress"]:
            ports = [p["port"] for p in rule.get("ports", [])]
            if 8082 in ports:
                print(",".join(str(p) for p in ports))
')
if [ "$ACTION_RULE" = "8082" ]; then
    pass "the rule that opens the actions port opens nothing else"
else
    fail "the actions port must be opened by a rule of its own, found: '$ACTION_RULE'"
fi

# The routing vocabulary is closed in the schema, so a typo is refused at `helm template`
# rather than becoming a rule that matches nothing and delivers nowhere - which is the exact
# failure this whole feature exists to remove, and it looks identical to working.
refuses "a route to a channel that does not exist" \
    --set 'notifications.routes[0].channel=slack' \
    --set 'notifications.routes[0].events[0]=IncidentEscalated'
refuses "a route carrying an event that does not exist" \
    --set 'notifications.routes[0].channel=teams' \
    --set 'notifications.routes[0].events[0]=IncidentExploded'
refuses "a route carrying no events at all" \
    --set 'notifications.routes[0].channel=teams' \
    --set 'notifications.routes[0].namespaces[0]=app'
refuses "a severity that is not a severity" \
    --set 'notifications.routes[0].channel=teams' \
    --set 'notifications.routes[0].events[0]=IncidentEscalated' \
    --set 'notifications.routes[0].minSeverity=Urgent'

echo
echo "The safe defaults still render:"
renders "defaults"     --values "$CHART/ci/minimal-values.yaml"
renders "full values"  --values "$CHART/ci/full-values.yaml"
renders "an allowed namespace" --set 'policy.actionableNamespaces[0]=hephaisto-chaos'

echo
echo "Invariants in the rendered output:"

FULL=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" 2>/dev/null)
MIN=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/minimal-values.yaml" 2>/dev/null)

# The single most valuable assertion here. "No access to Secrets at all, ever" is the claim
# the whole safety argument rests on, and it is one careless line away from being false.
if grep -A3 -E '^\s+resources:' <<<"$FULL" | grep -qE '^\s*-?\s*("?secrets"?)\s*$|"secrets"'; then
    fail "the read ClusterRole must never mention secrets"
else
    pass "no rule anywhere mentions secrets"
fi

# Read means read. Any of these verbs in the cluster-wide ClusterRole would make "reads
# cannot break anything" untrue.
if grep -qE '"(create|update|patch|delete|deletecollection)"' <<<"$(awk '/^  name: hephaisto-read$/,/^---/' <<<"$FULL")"; then
    fail "the read ClusterRole has grown a write verb"
else
    pass "the read ClusterRole has no write verbs"
fi

# A chart that creates a Secret puts its contents in `helm get values`, in the release Secret,
# and in whatever git repo holds the Argo Application - forever.
if grep -qE '^kind: Secret$' <<<"$FULL"; then
    fail "the chart rendered a Secret; it must only ever reference them"
else
    pass "no Secret is ever rendered"
fi

# Two replicas make the budget/cooldown/kill-switch check and the action INSERT a distributed
# TOCTOU race on the one code path that ends in `kubectl delete pod`.
if grep -qE '^\s+replicas: 1$' <<<"$FULL" && ! grep -qE '^\s+replicas: [02-9]' <<<"$FULL"; then
    pass "the Deployment is a singleton"
else
    fail "the Deployment is not replicas: 1"
fi

if grep -q 'type: Recreate' <<<"$FULL"; then
    pass "strategy is Recreate, so a rollout never runs two executors"
else
    fail "strategy must be Recreate"
fi

# The whole outbound feature ships off, in the same direction as an empty
# actionableNamespaces and mode: Observe. Two independent things have to change to be told
# anything, and this asserts that neither has happened by accident.
if grep -q 'Notifications__' <<<"$MIN"; then
    fail "notifications must be entirely absent by default"
else
    pass "notifications ship off - no Notifications__ env at all by default"
fi

# One name for the cluster (#139): the value reaches the agent, and only as Cluster__Name.
if grep -A1 'name: Cluster__Name' <<<"$FULL" | grep -q 'value: "ci-negative"' \
    && ! grep -qE 'name: (Ingest|Kubernetes|Investigation__Environment)__ClusterName' <<<"$FULL"; then
    pass "cluster.name renders as the one Cluster__Name"
else
    fail "cluster.name must render as Cluster__Name and nothing else may name the cluster"
fi

# Prices are values, indexed by the chart (#140).
if grep -A1 'name: Llm__Prices__0__Model' <<<"$FULL" | grep -q 'my-gateway-model'; then
    pass "llm.pricing renders as Llm__Prices__N"
else
    fail "llm.pricing did not render"
fi

# The webhook token (#138) is off unless named, and never a value when it is.
if grep -q 'Web__WebhookToken' <<<"$FULL"; then
    fail "no webhook token may be rendered unless secrets.webhookToken names one"
else
    pass "no webhook token by default"
fi
TOKEN=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" \
    --set secrets.webhookToken=hephaisto-webhook-token 2>/dev/null)
if grep -A4 'name: Web__WebhookToken' <<<"$TOKEN" | grep -q 'secretKeyRef:' \
    && grep -A4 'name: Web__WebhookToken' <<<"$TOKEN" | grep -q 'key: token'; then
    pass "the webhook token is only ever a secretKeyRef"
else
    fail "the webhook token must be a secretKeyRef to key token"
fi

# The Teams trigger URL carries its bearer token in the query string, so it is the one setting
# here that must NEVER be renderable as a plain value. If this ever passes as `value:`, the
# credential is in `helm get values`, in the release Secret, and in the git repo holding the
# Argo Application - forever.
TEAMS=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" \
    --set notifications.teams.enabled=true 2>/dev/null)

if grep -A1 'name: Notifications__Teams__WorkflowUrl' <<<"$TEAMS" | grep -q 'valueFrom:'; then
    pass "the Teams trigger URL is only ever a secretKeyRef"
else
    fail "the Teams trigger URL rendered as a plain value; it is a credential"
fi

# The same for the bot's client secret, and the ids beside it are the control: they DO render as
# values, so a pass here is not the template having rendered nothing at all.
BOTTED=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" 2>/dev/null)

if grep -A1 'name: Notifications__TeamsBot__ClientSecret' <<<"$BOTTED" | grep -q 'valueFrom:' \
    && grep -A1 'name: Notifications__TeamsBot__AppId' <<<"$BOTTED" | grep -q 'value: "'; then
    pass "the Teams bot's client secret is only ever a secretKeyRef"
else
    fail "the Teams bot's client secret rendered as a plain value, or the bot did not render"
fi

# Egress is off by default, and that default is load-bearing: adding Egress to a policy denies
# everything not listed, which for this pod means DNS, the API server, Postgres and the LLM.
# An accidental default here is an agent that starts, reports healthy, and does nothing.
if awk '/^  name: hephaisto-ingress$/,/^---$/' <<<"$FULL" | grep -q '^\s*- Egress$'; then
    fail "egress must be off by default"
else
    pass "egress is off by default"
fi

EGRESS=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" \
    --set networkPolicy.egress.enabled=true \
    --set 'networkPolicy.egress.apiServerCIDRs[0]=10.0.0.1/32' 2>/dev/null)

# Postgres talks to nothing, so its policy must stay Ingress-only even when the agent's grows
# an egress section. Restricting the wrong pod is how this lands as a database outage.
if awk '/^  name: hephaisto-postgres-ingress$/,/^---$/' <<<"$EGRESS" | grep -q '^\s*- Egress$'; then
    fail "the Postgres policy must never gain an Egress section"
else
    pass "enabling egress does not restrict Postgres"
fi

# Losing DNS or the API server is not a weakened control, it is an outage - and a silent one.
if grep -q 'port: 53' <<<"$EGRESS" && grep -q '10.0.0.1/32' <<<"$EGRESS"; then
    pass "egress allows DNS and the API server when enabled"
else
    fail "egress must allow DNS and the configured API server CIDRs"
fi

# The cordon/drain role ships unbound. Binding it is a separate, hand-written human act.
if grep -q 'hephaisto-node' <<<"$FULL" && ! awk '/^kind: ClusterRoleBinding$/,/^---$/' <<<"$FULL" | grep -q 'hephaisto-node'; then
    pass "the node ClusterRole exists and is not bound"
else
    fail "the node ClusterRole must be created but never bound"
fi

# The failure mode of getting these wrong is silence, so they are asserted rather than trusted.
for kind in PodMonitor PrometheusRule; do
    if awk "/^kind: $kind\$/,/^spec:/" <<<"$FULL" | grep -qE '^\s+release: "?kube-prometheus-stack"?$'; then
        pass "every $kind carries the operator selector label"
    else
        fail "a $kind is missing the operator selector label; Prometheus would never select it"
    fi
done

# The empty allowlist must produce no write access at all, not an empty Role.
if grep -qE '^kind: Role$' <<<"$MIN"; then
    fail "an empty policy.actionableNamespaces still rendered a write Role"
else
    pass "an empty allowlist renders no write Role at all"
fi

# The published image writes nothing to its own filesystem, so the DEFAULT must be a read-only
# root. values-dev.yaml turns it off deliberately, because Tilt runs a dev image whose
# entrypoint is a compiler - but that is an override, and it must never become the default.
if grep -q 'readOnlyRootFilesystem: true' <<<"$FULL" && ! grep -q 'readOnlyRootFilesystem: false' <<<"$FULL"; then
    pass "the root filesystem is read-only by default"
else
    fail "readOnlyRootFilesystem is not true by default"
fi

# CI stamps the version at package time; a real number here is a second source of truth.
if grep -qE '^version: 0\.0\.0$' "$CHART/Chart.yaml" && grep -qE '^appVersion: "0\.0\.0"$' "$CHART/Chart.yaml"; then
    pass "Chart.yaml still reads 0.0.0 (CI stamps the real version)"
else
    fail "Chart.yaml has a hard-coded version; the git tag is meant to be the only source"
fi

# The serving role must be a DIFFERENT role from the owner, or audit_events is not append-only:
# Postgres cannot restrain a table's owner, which may always grant itself back. This renders the
# connection string, so a template edit that drops it fails the release rather than quietly
# returning the agent to owner privileges.
if grep -q 'ConnectionStrings__hephaisto_app' <<<"$FULL" \
   && grep -q 'Username=hephaisto_app' <<<"$FULL"; then
    pass "the agent serves on a non-owner role (audit_events stays append-only)"
else
    fail "no hephaisto_app connection string rendered; the agent would serve as the database owner"
fi

# The optional flag is what keeps an upgrade from wedging on a Secret that predates the key.
# Without it the pod sits in CreateContainerConfigError instead of falling back and warning.
if grep -A 4 'key: POSTGRES_APP_PASSWORD' <<<"$FULL" | grep -q 'optional: true'; then
    pass "POSTGRES_APP_PASSWORD is optional, so an older Secret degrades rather than wedges"
else
    fail "POSTGRES_APP_PASSWORD is a hard secretKeyRef; upgrading an older release would not start"
fi

# OCI tags cannot contain '+'. Helm rewrites it to '_', so the chart would ask for a tag no
# registry has ever heard of - and the error arrives at pull time, not at render time.
if grep -E '^\s+image:' <<<"$FULL" | grep -q '+'; then
    fail "an image tag contains '+', which is not a legal OCI tag"
else
    pass "no image tag contains '+'"
fi

# -------------------------------------------------------------------------------------------
# extraEnv is appended LAST, which is what makes it useful and what makes it dangerous.
# Kubernetes takes the last value for a duplicated env name, so a collision with a
# chart-managed name does not conflict - it silently wins. Three of these are safety
# properties, not preferences: shadowing HEPHAISTO_MODE or HEPHAISTO_SWITCHES_DIR disables an
# arm of the kill switch, and a literal GEMINI_API_KEY or LLM_API_KEY is a plaintext
# credential in `helm get values` forever.
# -------------------------------------------------------------------------------------------
refuses "extraEnv cannot shadow GEMINI_API_KEY" \
    --set 'extraEnv[0].name=GEMINI_API_KEY' --set 'extraEnv[0].value=sk-plaintext'
# The same hazard, one provider along. Reserving only the key the chart happened to ship
# first is how the other one ends up pasted in as a literal.
refuses "extraEnv cannot shadow LLM_API_KEY" \
    --set 'extraEnv[0].name=LLM_API_KEY' --set 'extraEnv[0].value=sk-plaintext'
refuses "extraEnv cannot shadow HEPHAISTO_MODE" \
    --set 'extraEnv[0].name=HEPHAISTO_MODE' --set 'extraEnv[0].value=Auto'
refuses "extraEnv cannot redirect the kill switch's ConfigMap dir" \
    --set 'extraEnv[0].name=HEPHAISTO_SWITCHES_DIR' --set 'extraEnv[0].value=/tmp/nowhere'
refuses "extraEnv cannot half-override the OTEL_* block" \
    --set 'extraEnv[0].name=OTEL_SERVICE_NAME' --set 'extraEnv[0].value=something-else'
refuses "extraEnv cannot widen the namespace allowlist behind RBAC's back" \
    --set 'extraEnv[0].name=Policy__AllowedNamespaces__0' --set 'extraEnv[0].value=kube-system'
refuses "extraEnv entries must be named" \
    --set 'extraEnv[0].value=orphan'

# The whole point of the seam: configuration the chart does not expose must still be settable.
renders "extraEnv can set an unexposed option" \
    --set 'extraEnv[0].name=Llm__Budget__MaxCostUsdPerHour' --set 'extraEnv[0].value=1.00'

# And it must land after the chart's own entries, or last-wins works against the operator
# rather than for them.
EXTRA=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative \
    --set 'extraEnv[0].name=Llm__Budget__MaxCostUsdPerHour' --set 'extraEnv[0].value=1.00' 2>/dev/null)
if [ "$(grep -n 'Llm__Budget__MaxCostUsdPerHour' <<<"$EXTRA" | head -1 | cut -d: -f1)" -gt \
     "$(grep -n 'name: GEMINI_API_KEY' <<<"$EXTRA" | head -1 | cut -d: -f1)" ]; then
    pass "extraEnv is appended after the chart's own env"
else
    fail "extraEnv renders before the chart's env, so it cannot override anything"
fi

echo
echo "Authentication refuses to be half-configured:"
refuses "auth enabled with no authority" --set auth.enabled=true
refuses "auth enabled with no secret name" \
    --set auth.enabled=true --set auth.authority=https://kc/realms/h --set secrets.auth=""
renders "auth enabled with an authority" \
    --set auth.enabled=true --set auth.authority=https://kc/realms/h

echo
echo "The webhook port split keeps the console hole out of the receiver:"

# The property, stated plainly: with webhookPort set, an address admitted by extraIngressCIDRs
# may read the console and may NOT post an alert. On a single port those were the same grant,
# and that is what forced the first production install to disable the NetworkPolicy outright to
# get a dashboard - its ingress controller runs hostNetwork, so its packets carry the node
# address and no namespaceSelector can ever match them.
SPLIT=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative \
    --set webhookPort=8081 \
    --set 'networkPolicy.extraIngressCIDRs[0]=10.10.0.0/24' 2>/dev/null)

CIDR_PORTS=$(printf '%s' "$SPLIT" | python3 "$CHART/ci/netpol-ports.py")

if [ "$CIDR_PORTS" = "8080" ]; then
    pass "extraIngressCIDRs reaches the console and not the webhook"
else
    fail "extraIngressCIDRs reaches ports [$CIDR_PORTS]; must be 8080 only, or admitting a node address to read a dashboard also admits forged alerts"
fi

# The other half. Kestrel listening on both ports is not the split - without these the app
# serves every endpoint on both, and the policy guards a door with no wall beside it.
if grep -q 'name: Web__WebhookPort' <<<"$SPLIT" && grep -q 'name: Web__MainPort' <<<"$SPLIT"; then
    pass "the split is routed in the app, not only bound in Kestrel"
else
    fail "webhookPort is set but Web__WebhookPort/Web__MainPort are not, so every endpoint answers on both ports"
fi

renders "webhookPort=0 keeps the previous single-port behaviour" --set webhookPort=0

echo
echo "Code fixes: the one place 'create jobs' is granted must stay one place:"

# The minimum an enabled stage needs, so each refusal below differs from a rendering install by
# exactly the one value under test.
CF=(--set codeFix.enabled=true)

refuses "codeFix enabled with an empty image repository" "${CF[@]}" --set codeFix.image.repository=""
refuses "codeFix enabled with no secret name"            "${CF[@]}" --set secrets.codeFix=""
refuses "coder namespace = the release namespace"        "${CF[@]}" --set codeFix.namespace=hephaisto
refuses "coder namespace = kube-system"                  "${CF[@]}" --set codeFix.namespace=kube-system
refuses "coder namespace = default"                      "${CF[@]}" --set codeFix.namespace=default
refuses "coder namespace = the observability namespace"  "${CF[@]}" --set codeFix.namespace=hephaisto-obs
refuses "coder namespace = an actionable namespace" \
    "${CF[@]}" --set codeFix.namespace=hephaisto-chaos --set 'policy.actionableNamespaces[0]=hephaisto-chaos'
refuses "coder namespace empty"                          "${CF[@]}" --set codeFix.namespace=""
# A repository write on the strength of an unauthenticated click. The agent refuses this at
# startup too; refusing at render is earlier and says why in the pipeline that caused it.
refuses "mode pr with auth off"                          "${CF[@]}" --set codeFix.mode=pr
refuses "a mode that is not a mode"                      "${CF[@]}" --set codeFix.mode=auto
refuses "a Go duration where a .NET TimeSpan belongs"    "${CF[@]}" --set codeFix.deadlines.plan=30m
refuses "a repository whose host is not allowed" \
    "${CF[@]}" --set 'codeFix.repositories[0].workload=shop/Deployment/api' \
               --set 'codeFix.repositories[0].url=https://gitlab.com/example/api'
refuses "the coder NetworkPolicy without the proxy it points at" \
    "${CF[@]}" --set codeFix.egressProxy.enabled=false
refuses "extraEnv cannot set a CodeFix__ key behind the chart's checks" \
    --set 'extraEnv[0].name=CodeFix__Namespace' --set 'extraEnv[0].value=hephaisto-chaos'
refuses "extraEnv cannot displace the coder namespace from the investigator's deny list" \
    "${CF[@]}" --set 'extraEnv[0].name=Kubernetes__DeniedNamespaces__0' --set 'extraEnv[0].value=nothing'

renders "codeFix enabled with its defaults"              "${CF[@]}"
renders "mode pr with auth on"  "${CF[@]}" --set codeFix.mode=pr \
    --set auth.enabled=true --set auth.authority=https://kc/realms/h
renders "mode pr with the throwaway-cluster escape hatch" "${CF[@]}" --set codeFix.mode=pr \
    --set codeFix.allowUnauthenticatedApproval=true
renders "no proxy and no coder NetworkPolicy (egress controlled elsewhere)" "${CF[@]}" \
    --set codeFix.egressProxy.enabled=false --set codeFix.networkPolicy.enabled=false

# Disabled means ABSENT, not merely inert: no Role anywhere that mentions jobs with a create
# verb, no coder ServiceAccount, no CodeFix__ env - only the switch key, reading off.
if grep -v '^\s*#' <<<"$MIN" | grep -q 'CodeFix__\|hephaisto-coder\|Kubernetes__DeniedNamespaces'; then
    fail "codeFix disabled must render nothing code-fix related"
else
    pass "codeFix disabled renders no coder objects and no CodeFix__ env"
fi

if grep -qE '^\s+codeFixMode: "off"$' <<<"$MIN"; then
    pass "codeFix disabled still ships the switch key, reading off"
else
    fail "the switch ConfigMap must carry codeFixMode: \"off\" even when disabled"
fi

JOBS_GRANTS=$(printf '%s' "$MIN" | python3 "$CHART/ci/rbac-grants.py" jobs create)
if [ -z "$JOBS_GRANTS" ]; then
    pass "codeFix disabled grants create on jobs nowhere"
else
    fail "codeFix disabled still grants create jobs: $JOBS_GRANTS"
fi

# Enabled: create on jobs in EXACTLY the coder namespace, through a namespaced Role - never a
# ClusterRole, which a single ClusterRoleBinding would turn into code execution everywhere.
JOBS_GRANTS=$(printf '%s' "$FULL" | python3 "$CHART/ci/rbac-grants.py" jobs create)
if [ "$JOBS_GRANTS" = "Role hephaisto-coder/hephaisto-codefix" ]; then
    pass "create jobs is granted by one Role, in the coder namespace only"
else
    fail "create jobs grants must be exactly the coder Role; found: [$JOBS_GRANTS]"
fi

# The Role holds nothing it does not need: no pod creation (the Job controller makes the pod),
# no secrets (the kubelet resolves the coder's secretKeyRefs), no patch/update of anything.
EXTRA=$(printf '%s' "$FULL" | python3 "$CHART/ci/rbac-grants.py" --role hephaisto-codefix --forbidden)
if [ -z "$EXTRA" ]; then
    pass "the coder Role grants no pod creation, no secrets and no update/patch"
else
    fail "the coder Role grants more than it needs: $EXTRA"
fi

# The coder's own identity is bound to NOTHING. It is a model with a shell that has read
# attacker-influenceable text; whatever this ServiceAccount could do, an injection could.
BOUND=$(printf '%s' "$FULL" | python3 "$CHART/ci/rbac-grants.py" --subject hephaisto-coder:hephaisto-coder)
if [ -z "$BOUND" ]; then
    pass "the coder ServiceAccount is the subject of no RoleBinding or ClusterRoleBinding"
else
    fail "the coder ServiceAccount is bound: $BOUND"
fi

if printf '%s' "$FULL" | python3 -c '
import sys, yaml
sa = [d for d in yaml.safe_load_all(sys.stdin)
      if d and d.get("kind") == "ServiceAccount" and d["metadata"]["name"] == "hephaisto-coder"]
sys.exit(0 if sa and all(d.get("automountServiceAccountToken") is False for d in sa) else 1)'; then
    pass "the coder ServiceAccount never mounts its token"
else
    fail "the coder ServiceAccount must set automountServiceAccountToken: false"
fi

# The investigator must never read what a coder wrote: that log is a model's output after
# reading attacker-influenceable evidence, and feeding it into the next investigation is a
# reflection path for an injection.
if grep -A1 'name: Kubernetes__DeniedNamespaces__0' <<<"$FULL" | grep -q 'value: "hephaisto-coder"'; then
    pass "the coder namespace is denied to the investigator's read tools"
else
    fail "Kubernetes__DeniedNamespaces__0 must be the coder namespace when codeFix is enabled"
fi

# The coder policy is independent of the top-level flag, and denies all ingress.
NOPOL=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --values "$CHART/ci/full-values.yaml" \
    --set networkPolicy.enabled=false 2>/dev/null)
if awk '/^kind: NetworkPolicy$/,/^---$/' <<<"$NOPOL" | grep -q 'name: hephaisto-coder$'; then
    pass "the coder NetworkPolicy renders even with the top-level networkPolicy off"
else
    fail "the coder NetworkPolicy must not depend on networkPolicy.enabled"
fi

# Squid refuses to start on an allowlist entry another entry covers, and the default list has
# two. The rendered list must have none, or the proxy - the coder's only way out - never starts.
OVERLAP=$(printf '%s' "$FULL" | python3 -c '
import sys, yaml
for d in yaml.safe_load_all(sys.stdin):
    if d and d.get("kind") == "ConfigMap" and "allowlist.txt" in d.get("data", {}):
        names = [l.strip() for l in d["data"]["allowlist.txt"].splitlines() if l.strip()]
        for a in names:
            for w in names:
                if w.startswith(".") and a != w and (a.endswith(w) or "." + a == w):
                    print(a, "is covered by", w)
')
if [ -z "$OVERLAP" ]; then
    pass "the proxy allowlist has no entry squid would refuse as covered"
else
    fail "the proxy allowlist would crash squid: $OVERLAP"
fi

if grep -q 'CONNECT !SSL_ports' <<<"$FULL" && grep -q 'http_access deny all' <<<"$FULL"; then
    pass "the proxy tunnels only to 443 and denies by default"
else
    fail "the proxy config must deny CONNECT to non-443 ports and end in deny all"
fi

echo
echo "The MCP endpoint (#157): its own port, never open to nobody, tokens only from a Secret:"
MCP=(--set mcp.enabled=true --set 'mcp.tokens[0].name=gateway' --set secrets.mcp=hephaisto-mcp)
renders "an endpoint with a token from a Secret" "${MCP[@]}"
refuses "an endpoint nobody could ever sign in to" --set mcp.enabled=true
refuses "tokens with no Secret to read them from" --set mcp.enabled=true --set 'mcp.tokens[0].name=gateway'
refuses "a token's value in the values file" "${MCP[@]}" --set 'mcp.tokens[0].value=hunter2hunter2hunter2hunter2hunter2'
refuses "the console's port" "${MCP[@]}" --set mcp.port=8080
refuses "the webhook's port" "${MCP[@]}" --set webhookPort=8081 --set mcp.port=8081
refuses "the Teams actions port" "${MCP[@]}" "${BOT[@]}" --set notifications.teamsBot.actions.enabled=true --set mcp.port=8082
refuses "one token name twice" "${MCP[@]}" --set 'mcp.tokens[1].name=gateway'
refuses "a person token for nobody" "${MCP[@]}" --set 'mcp.tokens[1].name=flo' --set 'mcp.tokens[1].kind=person'
refuses "a token name the audit trail cannot carry" --set mcp.enabled=true --set 'mcp.tokens[0].name=Gate Way' --set secrets.mcp=hephaisto-mcp
refuses "a role that does not exist" "${MCP[@]}" --set 'mcp.tokens[0].role=admin'
refuses "the switch set behind the chart's back" --set 'extraEnv[0].name=Mcp__Enabled' --set 'extraEnv[0].value=true'
refuses "a token set behind the chart's back" --set 'extraEnv[0].name=Mcp__Tokens__0__Value' --set 'extraEnv[0].value=x'
refuses "the coder's namespace admitted to the endpoint" "${MCP[@]}" --set codeFix.enabled=true \
    --set 'mcp.networkPolicy.fromNamespaces[0]=hephaisto-coder'
renders "an endpoint with no token behind sign-in" --set mcp.enabled=true --set auth.enabled=true \
    --set auth.authority=https://idp.example/realms/r --set auth.clientId=hephaisto

OFF=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative 2>&1)
if grep -q 'Mcp__\|name: mcp\|8083' <<<"$OFF"; then
    fail "with the endpoint off, nothing of it is rendered"
else
    pass "with the endpoint off, nothing of it is rendered"
fi

ON=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${MCP[@]}" --set networkPolicy.enabled=true \
    --set 'mcp.networkPolicy.fromNamespaces[0]=gateway' 2>&1)
MCP_RULE=$(printf '%s' "$ON" | python3 -c '
import sys, yaml
for doc in yaml.safe_load_all(sys.stdin):
    if doc and doc.get("kind") == "NetworkPolicy" and doc["metadata"]["name"].endswith("-ingress"):
        for rule in doc["spec"]["ingress"]:
            ports = [p["port"] for p in rule.get("ports", [])]
            if 8083 in ports:
                print(",".join(str(p) for p in ports))
')
if [ "$MCP_RULE" = "8083" ]; then
    pass "the rule that opens the MCP port opens nothing else"
else
    fail "the MCP port must be opened by a rule of its own, found: '$MCP_RULE'"
fi
if grep -q 'Mcp__Tokens__0__Value' <<<"$ON" && ! grep -A4 'Mcp__Tokens__0__Value' <<<"$ON" | grep -q '^ *value:'; then
    pass "a token reaches the agent by secretKeyRef, never as a value"
else
    fail "a token must reach the agent by secretKeyRef"
fi

echo
echo "Investigation in a Job (v0.12.0 F5) is off unless asked for, and borrows only what the coder has:"
IJ=(--set codeFix.enabled=true --set investigation.job.enabled=true)
renders "the investigator beside the code-fix stage" "${IJ[@]}"
renders "the investigator with the code-fix stage's mode off" "${IJ[@]}" --set codeFix.mode=off
refuses "the investigator without the code-fix stage it borrows from" --set investigation.job.enabled=true
refuses "the console's port" "${IJ[@]}" --set investigation.job.port=8080
refuses "the webhook's port" "${IJ[@]}" --set webhookPort=8081 --set investigation.job.port=8081
refuses "the MCP port" "${IJ[@]}" "${MCP[@]}" --set investigation.job.port=8083
refuses "an executor that is not inprocess or job" "${IJ[@]}" --set investigation.job.executor=always
refuses "an overflow that is not inprocess or wait" "${IJ[@]}" --set investigation.job.overflow=queue
refuses "the switch set behind the chart's back" --set 'extraEnv[0].name=Investigation__Job__Executor' --set 'extraEnv[0].value=job'

OFF=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative 2>&1)
if grep -q 'Investigation__Job__\|investigationExecutor\|8084' <<<"$OFF"; then
    fail "with investigation.job off, nothing of it is rendered"
else
    pass "with investigation.job off, nothing of it is rendered"
fi
IJ_ON=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${IJ[@]}" 2>&1)
IJ_RULE=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${IJ[@]}" --set networkPolicy.enabled=true 2>&1 | python3 -c '
import sys, yaml
for doc in yaml.safe_load_all(sys.stdin):
    if doc and doc.get("kind") == "NetworkPolicy" and doc["metadata"]["name"].endswith("-ingress"):
        for rule in doc["spec"]["ingress"]:
            if 8084 in [p["port"] for p in rule.get("ports", [])]:
                for peer in rule.get("from", []):
                    ns = peer.get("namespaceSelector", {}).get("matchLabels", {}).get("kubernetes.io/metadata.name")
                    app = peer.get("podSelector", {}).get("matchLabels", {}).get("app.kubernetes.io/name")
                    print(str(ns) + "/" + str(app) + "/" + str(len(rule.get("ports", []))))
')
if [ "$IJ_RULE" = "hephaisto-coder/hephaisto-investigator/1" ]; then
    pass "the investigator port admits investigator pods in the coder namespace, and nothing else, to that port alone"
else
    fail "the investigator port must admit exactly hephaisto-coder/hephaisto-investigator on one port, found: '$IJ_RULE'"
fi
if grep -A1 'name: Investigation__Job__Executor' <<<"$IJ_ON" | grep -q 'value: "inprocess"' \
    && grep -q 'investigationExecutor: "inprocess"' <<<"$IJ_ON"; then
    pass "enabling it switches no investigation over by itself"
else
    fail "enabling investigation.job must leave both executor arms at inprocess"
fi

echo
echo "GitHub issues as work (v0.14.0) is off unless asked for, and the agent's token is its own:"
# The minimum an enabled block needs, so each refusal differs from a rendering install by one value.
GH=(--set github.enabled=true --set secrets.github=hephaisto-github --set 'github.issues.repositories[0]=example/shop')
renders "github enabled with a secret name and one repository" "${GH[@]}"
refuses "github enabled without a secret name" \
    --set github.enabled=true --set 'github.issues.repositories[0]=example/shop'
refuses "github enabled with no repository listed" \
    --set github.enabled=true --set secrets.github=hephaisto-github
refuses "a repository given as a URL"          "${GH[@]}" --set 'github.issues.repositories[0]=https://github.com/example/shop'
refuses "an approver named by login instead of account number" "${GH[@]}" --set 'github.approvers[0]=maintainer'
refuses "a Go duration where a .NET TimeSpan belongs" "${GH[@]}" --set github.pollInterval=60s
refuses "an API root that is not a URL"        "${GH[@]}" --set github.apiBaseUrl=api.github.com
refuses "the token as a chart value"           "${GH[@]}" --set github.token=hunter2
refuses "the coder's Secret as the agent's"    "${GH[@]}" --set codeFix.enabled=true --set secrets.github=hephaisto-codefix

# The planner (v0.15.0): one project, and it has no token of its own.
PLANNER=("${GH[@]}" --set github.planner.enabled=true --set github.planner.owner=example --set github.planner.number=3)
renders "the planner with an owner and a number" "${PLANNER[@]}" \
    --set 'github.planner.requiredFields[0]=Priority' --set 'github.planner.assignees[0]=octocat'
refuses "the planner without github.enabled" \
    --set github.planner.enabled=true --set github.planner.owner=example --set github.planner.number=3
refuses "the planner without an owner"         "${GH[@]}" --set github.planner.enabled=true --set github.planner.number=3
refuses "the planner without a number"         "${GH[@]}" --set github.planner.enabled=true --set github.planner.owner=example
refuses "a planner owner given as a URL"       "${PLANNER[@]}" --set github.planner.owner=https://github.com/example
refuses "a planner number that is a name"      "${PLANNER[@]}" --set github.planner.number=planning
renders "the planner's repositories for a new issue" "${PLANNER[@]}" --set 'github.planner.repositories[0]=example/planning'
refuses "a planner repository given as a URL"  "${PLANNER[@]}" --set 'github.planner.repositories[0]=https://github.com/example/planning'
refuses "asking Claude without the coder"      "${PLANNER[@]}" --set github.planner.triage.enabled=true
renders "asking Claude with the coder"         "${PLANNER[@]}" "${CF[@]}" --set github.planner.triage.enabled=true
refuses "a triage deadline that is not hh:mm:ss" "${PLANNER[@]}" --set github.planner.triage.deadline=15m
refuses "a quiet period of no days"            "${PLANNER[@]}" --set github.planner.staleAfterDays=0
refuses "an assignee that is not a login"      "${PLANNER[@]}" --set 'github.planner.assignees[0]=some one'
refuses "a planner key nobody declared"        "${PLANNER[@]}" --set github.planner.project=3
refuses "the token set behind the chart's back" \
    --set 'extraEnv[0].name=GitHub__Token' --set 'extraEnv[0].value=hunter2'
refuses "a repository added behind the chart's back" \
    "${GH[@]}" --set 'extraEnv[0].name=GitHub__Repositories__1' --set 'extraEnv[0].value=example/other'

if grep -q 'GitHub__' <<<"$OFF"; then
    fail "with github off, no GitHub__ env is rendered"
else
    pass "with github off, no GitHub__ env is rendered"
fi

# What the agent is handed, read off the Deployment: the token by reference to key GITHUB_TOKEN
# of the named Secret and never as a literal; an account number as its digits (a YAML number is a
# float to Helm, and 12345678 printed carelessly is 1.2345678e+07); and the proxy, or not.
github_env() {
    python3 -c '
import sys, yaml
for doc in yaml.safe_load_all(sys.stdin):
    if doc and doc.get("kind") == "Deployment":
        for env in doc["spec"]["template"]["spec"]["containers"][0].get("env", []):
            if env["name"].startswith("GitHub__"):
                ref = env.get("valueFrom", {}).get("secretKeyRef")
                print(env["name"] + "=" + (("secret:" + ref["name"] + "/" + ref["key"]) if ref else str(env.get("value"))))
'
}
# Who may reach the proxy from outside the coder namespace, and where the agent may go in it.
github_peers() {
    python3 -c '
import sys, yaml
def sel(peer, key):
    return peer.get(key, {}).get("matchLabels", {})
for doc in yaml.safe_load_all(sys.stdin):
    if not doc or doc.get("kind") != "NetworkPolicy":
        continue
    name = doc["metadata"]["name"]
    if name.endswith("-coder-egress"):
        for rule in doc["spec"].get("ingress", []):
            for peer in rule.get("from", []):
                ns = sel(peer, "namespaceSelector").get("kubernetes.io/metadata.name")
                if ns:
                    print("proxy-from=" + ns + "/" + str(sel(peer, "podSelector").get("app.kubernetes.io/name")) + "/" + ",".join(str(p["port"]) for p in rule["ports"]))
    if name.endswith("-ingress") and not name.endswith("postgres-ingress"):
        for rule in doc["spec"].get("egress", []):
            for peer in rule.get("to", []):
                if sel(peer, "podSelector").get("app.kubernetes.io/component") == "egress-proxy":
                    print("agent-to=" + str(sel(peer, "namespaceSelector").get("kubernetes.io/metadata.name")) + "/" + ",".join(str(p["port"]) for p in rule.get("ports", [])))
'
}

GH_ENV=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${GH[@]}" \
    --set 'github.approvers[0]=12345678' 2>/dev/null | github_env)
if grep -qx 'GitHub__Token=secret:hephaisto-github/GITHUB_TOKEN' <<<"$GH_ENV"; then
    pass "the agent's token is a reference to key GITHUB_TOKEN of secrets.github, never a value"
else
    fail "GitHub__Token must be a secretKeyRef to secrets.github/GITHUB_TOKEN, found: $(grep Token <<<"$GH_ENV" | tr '\n' ' ')"
fi
if grep -qx 'GitHub__Approvers__0=12345678' <<<"$GH_ENV" && grep -qx 'GitHub__Repositories__0=example/shop' <<<"$GH_ENV"; then
    pass "an account number of eight digits is rendered as its digits"
else
    fail "GitHub__Approvers__0 must be 12345678, found: $(grep Approvers <<<"$GH_ENV" | tr '\n' ' ')"
fi
if grep -q 'GitHub__ProxyUrl' <<<"$GH_ENV"; then
    fail "without the coder's proxy there is no proxy to send the agent through"
else
    pass "without the coder's proxy the agent's GitHub client is given none"
fi

GH_PROXIED=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${GH[@]}" \
    --set codeFix.enabled=true --set networkPolicy.egress.enabled=true 2>/dev/null)
if grep -qx 'GitHub__ProxyUrl=http://hephaisto-coder-egress.hephaisto-coder.svc:3128' <<<"$(github_env <<<"$GH_PROXIED")"; then
    pass "with the coder's proxy on, the agent's GitHub client goes through it"
else
    fail "GitHub__ProxyUrl must be the coder's egress proxy, found: $(github_env <<<"$GH_PROXIED" | grep Proxy | tr '\n' ' ')"
fi
# Both halves or neither: a proxy URL the agent's own egress policy does not let it reach, or
# one the proxy's ingress policy refuses, is an agent that reports GitHub unreachable for ever.
if [ "$(github_peers <<<"$GH_PROXIED" | sort | tr '\n' ' ')" = "agent-to=hephaisto-coder/3128 proxy-from=hephaisto/hephaisto/3128 " ]; then
    pass "the agent may reach the proxy on 3128, and the proxy admits the agent's pods and no other outsider"
else
    fail "expected exactly agent-to=hephaisto-coder/3128 and proxy-from=hephaisto/hephaisto/3128, found: $(github_peers <<<"$GH_PROXIED" | sort | tr '\n' ' ')"
fi

GH_DIRECT=$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative "${GH[@]}" \
    --set codeFix.enabled=true --set networkPolicy.egress.enabled=true --set github.useEgressProxy=false 2>/dev/null)
if [ -z "$(github_peers <<<"$GH_DIRECT")" ] && ! github_env <<<"$GH_DIRECT" | grep -q 'GitHub__ProxyUrl'; then
    pass "with github.useEgressProxy off, the agent is neither sent to the proxy nor admitted by it"
else
    fail "github.useEgressProxy=false must render no proxy URL and no rule between the agent and the proxy"
fi

# Without github, the coder's proxy stays the coder's: nothing outside its namespace is admitted.
if [ -z "$(helm template t "$CHART" --namespace hephaisto --set cluster.name=ci-negative --set codeFix.enabled=true \
        --set networkPolicy.egress.enabled=true 2>/dev/null | github_peers)" ]; then
    pass "with github off, the proxy admits nothing from outside the coder namespace"
else
    fail "with github off, the agent must have no route to the coder's proxy"
fi

echo
printf '%d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
