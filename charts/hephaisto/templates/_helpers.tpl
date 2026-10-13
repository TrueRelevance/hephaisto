{{/*
Names. `hephaisto` by default rather than the release name, because the RBAC objects, the
NetworkPolicies and the alert rules all refer to each other by name, and an operator reading
`kubectl auth can-i --as=system:serviceaccount:hephaisto:hephaisto` should find what the
documentation says they will. Override with fullnameOverride if you must run two.
*/}}
{{- define "hephaisto.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.fullname" -}}
{{- default (include "hephaisto.name" .) .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.labels" -}}
app.kubernetes.io/name: {{ include "hephaisto.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/part-of: hephaisto
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
{{- end -}}

{{/*
Selector labels. Deliberately NOT including the version or chart labels: those change on
every release, and a Deployment's selector is immutable. Including them makes the second
`helm upgrade` fail with "field is immutable" and the fix is to delete the Deployment.
*/}}
{{- define "hephaisto.selectorLabels" -}}
app.kubernetes.io/name: {{ include "hephaisto.name" . }}
{{- end -}}

{{- define "hephaisto.serviceAccountName" -}}
{{- default (include "hephaisto.fullname" .) .Values.serviceAccount.name -}}
{{- end -}}

{{/*
The labels the Prometheus Operator selects PodMonitors and PrometheusRules by.

This is the single most dangerous value in the chart. Get it wrong and every object is
created successfully, `kubectl get prometheusrule` shows them all present, and Prometheus
never selects any of them: no metrics, no alerts, no incidents - and an agent reporting
itself perfectly healthy because nothing is arriving. There is no error anywhere.

Check it after install with the two commands NOTES.txt prints.
*/}}
{{- define "hephaisto.operatorSelectorLabels" -}}
{{- range $k, $v := .Values.prometheusOperator.selectorLabels }}
{{ $k }}: {{ $v | quote }}
{{- end }}
{{- end -}}

{{/*
Guard for the write Role's namespaces.

`policy.actionableNamespaces` is the list the agent may delete pods in. Refusing outright is
the right failure: a values file that names kube-system is not a typo to be silently dropped,
it is a change someone must see fail. Nothing in the un-charted manifests stopped anyone
editing `namespace: hephaisto-chaos` to `kube-system`; after this, that is a render error.
*/}}
{{- define "hephaisto.validateActionableNamespaces" -}}
{{- $release := .Release.Namespace -}}
{{- $obs := .Values.observabilityNamespace -}}
{{- range .Values.policy.actionableNamespaces -}}
  {{- if hasPrefix "kube-" . -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: the agent must never hold delete on a kube-* namespace." .) -}}
  {{- end -}}
  {{- if eq . "default" -}}
    {{- fail (printf "policy.actionableNamespaces may not contain \"default\": it is where unlabelled workloads land, so it is the one namespace whose contents nobody has decided about.") -}}
  {{- end -}}
  {{- if eq . $release -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: that is Hephaisto's own namespace, and an agent that can restart itself mid-action loses the transaction that was keeping it honest." .) -}}
  {{- end -}}
  {{- if eq . $obs -}}
    {{- fail (printf "policy.actionableNamespaces may not contain %q: an agent that can delete the Prometheus watching it can make its own failures invisible." .) -}}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
Guard for extraEnv.

`extraEnv` is appended LAST in the container spec, because that is what makes it useful: the
agent binds far more configuration than this chart exposes, and an operator has to be able to
set `Llm__Budget__MaxCostUsdPerHour` without the chart growing a value for every options
property.

Last position is also the hazard. Kubernetes takes the last value for a duplicated env name,
so an entry colliding with a chart-managed name does not conflict - it silently WINS, and
`kubectl get deploy -o yaml` shows both, in order, looking entirely reasonable.

Three of these are safety properties rather than settings:

  HEPHAISTO_MODE          shadowing it overrides the configured mode
  HEPHAISTO_SWITCHES_DIR  pointing it elsewhere makes the ConfigMap arm of the kill switch
                          read an empty directory - and an unreadable switch is a switch that
                          is not there
  GEMINI_API_KEY          a literal here is a plaintext credential in `helm get values`, in
  LLM_API_KEY             the release Secret and in the git repo holding your Application,
                          forever. That is precisely what `secrets.llm` exists to avoid. Both
                          are listed because which one an install uses depends on
                          Llm:Provider, and the one nobody remembered to reserve is the one
                          that gets pasted in as a literal.

Every reserved name already has a value that sets it properly, so refusing costs nothing.
*/}}
{{/*
The Teams bot's buttons that act (#124): the port of the one inbound route Microsoft calls, or
empty when there is none. Refused unless the bot itself is on, and unless the port is its own -
the whole argument for the route is that exposing that port exposes nothing else.
*/}}
{{- define "hephaisto.teamsActionsPort" -}}
{{- $actions := .Values.notifications.teamsBot.actions | default dict -}}
{{- /* Approvals (#124) are two more buttons on the same route. Asked for without it they would be
       a switch that reads as on and does nothing, so this is checked whether or not the route is
       on - and the agent refuses the same two things at startup. */ -}}
{{- if and (include "hephaisto.teamsApprovals" .) (not $actions.enabled) -}}
  {{- fail "notifications.teamsBot.actions.approvals.enabled is refused without notifications.teamsBot.actions.enabled: Approve and Deny are buttons, and without the actions route there is nothing for a click to reach." -}}
{{- end -}}
{{- if $actions.enabled -}}
  {{- if not .Values.notifications.teamsBot.enabled -}}
    {{- fail "notifications.teamsBot.actions.enabled is refused without notifications.teamsBot.enabled: a button belongs to a bot." -}}
  {{- end -}}
  {{- if and (include "hephaisto.teamsApprovals" .) (not $actions.approvers) -}}
    {{- fail "notifications.teamsBot.actions.approvals.enabled is refused while notifications.teamsBot.actions.approvers is empty: nobody would be allowed to approve, and the card would offer two buttons that refuse everybody." -}}
  {{- end -}}
  {{- $port := int $actions.port -}}
  {{- if or (eq $port 8080) (and .Values.webhookPort (eq $port (int .Values.webhookPort))) -}}
    {{- fail (printf "notifications.teamsBot.actions.port %d is refused: it must be a port of its own, not the console's (8080) or the webhook's, so that exposing it to Microsoft exposes nothing else." $port) -}}
  {{- end -}}
  {{- $port -}}
{{- end -}}
{{- end -}}

{{/*
Whether approving and denying from a Teams card is switched on (#124): "true", or empty.
*/}}
{{- define "hephaisto.teamsApprovals" -}}
{{- $actions := .Values.notifications.teamsBot.actions | default dict -}}
{{- if ($actions.approvals | default dict).enabled -}}true{{- end -}}
{{- end -}}

{{/*
The MCP endpoint (#157): its port, or empty when it is off. Every refusal here is one the agent
would also make at startup - rendering is just the earlier, cheaper place to hear it.
*/}}
{{- define "hephaisto.mcpPort" -}}
{{- $mcp := .Values.mcp | default dict -}}
{{- if $mcp.enabled -}}
  {{- $port := int $mcp.port -}}
  {{- if eq $port 8080 -}}
    {{- fail "mcp.port 8080 is refused: it is the console's. The endpoint needs a port of its own, so that admitting a gateway to it admits nothing else." -}}
  {{- end -}}
  {{- if and .Values.webhookPort (eq $port (int .Values.webhookPort)) -}}
    {{- fail (printf "mcp.port %d is refused: it is webhookPort." $port) -}}
  {{- end -}}
  {{- with include "hephaisto.teamsActionsPort" . -}}
    {{- if eq $port (int .) -}}
      {{- fail (printf "mcp.port %d is refused: it is the Teams actions port." $port) -}}
    {{- end -}}
  {{- end -}}
  {{- $tokens := $mcp.tokens | default list -}}
  {{- if and (not $tokens) (not (and .Values.auth.enabled $mcp.acceptIdentityProviderTokens)) -}}
    {{- fail "mcp.enabled is refused with no mcp.tokens and no sign-in (auth.enabled with mcp.acceptIdentityProviderTokens): the endpoint is never open to nobody." -}}
  {{- end -}}
  {{- if and $tokens (not .Values.secrets.mcp) -}}
    {{- fail "mcp.tokens without secrets.mcp is refused. The chart never takes a token as a value; it references the Secret that holds one key per token name." -}}
  {{- end -}}
  {{- $names := list -}}
  {{- range $tokens -}}
    {{- if has .name $names -}}
      {{- fail (printf "mcp.tokens naming %q twice is refused. Each consumer gets its own token." .name) -}}
    {{- end -}}
    {{- $names = append $names .name -}}
    {{- if and (eq (.kind | default "shared") "person") (not .subject) -}}
      {{- fail (printf "mcp.tokens %q, a person token with no subject, is refused: whose actions would it record?" .name) -}}
    {{- end -}}
  {{- end -}}
  {{- if .Values.codeFix.enabled -}}
    {{- $coder := include "hephaisto.codeFixNamespace" . -}}
    {{- if has $coder (($mcp.networkPolicy | default dict).fromNamespaces | default list) -}}
      {{- fail (printf "mcp.networkPolicy.fromNamespaces listing %s, the coder's namespace, is refused: the coder Job gets no route to the agent (#157)." $coder) -}}
    {{- end -}}
  {{- end -}}
  {{- $port -}}
{{- end -}}
{{- end -}}

{{- define "hephaisto.validateExtraEnv" -}}
{{- $reserved := list
      "GEMINI_API_KEY" "LLM_API_KEY" "HEPHAISTO_MODE" "HEPHAISTO_SWITCHES_DIR"
      "ConnectionStrings__hephaisto" "ASPNETCORE_URLS"
      "Grafana__McpUrl" "Grafana__ServiceAccountToken"
      "Cluster__Name" "Web__WebhookToken" "Notifications__MaxPerChannelPerHour"
      "Notifications__TeamsBot__Actions__Enabled" "Notifications__TeamsBot__Actions__Port" -}}
{{- /* The indexed entries the chart itself emits. Index 0 of DeniedNamespaces is what keeps the
       investigator's read tools out of the coder namespace; an extraEnv entry at the same index
       would silently replace it. Higher indices are the operator's and still work. */ -}}
{{- range $i, $ns := include "hephaisto.selfNamespaces" . | splitList " " -}}
  {{- $reserved = append $reserved (printf "Ingest__SelfNamespaces__%d" $i) -}}
{{- end -}}
{{- if .Values.codeFix.enabled -}}
  {{- $reserved = append $reserved "Kubernetes__DeniedNamespaces__0" -}}
{{- end -}}
{{- range .Values.extraEnv -}}
  {{- if has .name $reserved -}}
    {{- fail (printf "extraEnv may not set %q: the chart manages it, and because extraEnv is appended last a duplicate would silently win rather than conflict. Use the corresponding value instead - mode, secrets.llm, secrets.grafanaMcp, grafanaMcp.url, postgres.* or codeFix.* - or, for an indexed list, the next free index." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Notifications__TeamsBot__Actions__Approvers__" .name -}}
    {{- fail (printf "extraEnv may not set %q: who holds the approver role in Teams is notifications.teamsBot.actions.approvers, which the chart numbers and the schema checks - an entry here would add an approver no values file names." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Notifications__TeamsBot__Actions__Approvals__" .name -}}
    {{- fail (printf "extraEnv may not set %q: approving from a Teams card is notifications.teamsBot.actions.approvals.enabled, which the chart checks against the route and the approvers it needs." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Mcp__" .name -}}
    {{- fail (printf "extraEnv may not set %q: every Mcp setting is an mcp.* value (and a token is secrets.mcp), and the chart checks them together - the port against the others, a token against its Secret." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Investigation__Job__" .name -}}
    {{- fail (printf "extraEnv may not set %q: every Investigation:Job setting is an investigation.job.* value, checked together with the port and the code-fix stage it borrows from." .name) -}}
  {{- end -}}
  {{- if hasPrefix "GitHub__" .name -}}
    {{- fail (printf "extraEnv may not set %q: every GitHub setting is a github.* value and the token is secrets.github. A token set here is a token in `helm get values` for ever, and a repository or an approver added here is one no values file names." .name) -}}
  {{- end -}}
  {{- if hasPrefix "CodeFix__" .name -}}
    {{- fail (printf "extraEnv may not set %q: every CodeFix setting is a codeFix.* value, and the chart validates them TOGETHER - the namespace against the RBAC it grants, mode pr against auth. A CodeFix__ entry here would win silently and skip every one of those checks." .name) -}}
  {{- end -}}
  {{- if or (hasPrefix "IncidentSweep__" .name) (eq .name "Kubernetes__HealedAfter") -}}
    {{- fail (printf "extraEnv may not set %q: use incidents.healedAfter and incidents.sweep.*." .name) -}}
  {{- end -}}
  {{- if hasPrefix "OTEL_" .name -}}
    {{- fail (printf "extraEnv may not set %q: the OTEL_* block is derived from otel.endpoint/protocol/environment, and a half-overridden set exports telemetry to two places or to none." .name) -}}
  {{- end -}}
  {{- if has .name (list "Ingest__ClusterName" "Kubernetes__ClusterName" "Investigation__Environment__ClusterName") -}}
    {{- fail (printf "extraEnv may not set %q: the cluster is named once, by cluster.name, which fills all three of the settings that used to name it separately (backlog #139)." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Llm__Prices__" .name -}}
    {{- fail (printf "extraEnv may not set %q: prices are llm.pricing, and the chart numbers the entries - one set here would collide with index 0 of that list." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Investigation__Environment__InScopeNamespaces__" .name -}}
    {{- fail (printf "extraEnv may not set %q: use investigation.inScopeNamespaces." .name) -}}
  {{- end -}}
  {{- if hasPrefix "Policy__AllowedNamespaces" .name -}}
    {{- fail (printf "extraEnv may not set %q: the namespace allowlist is what the write Role is rendered from, so setting it here would let the agent believe it may act somewhere RBAC does not permit. Use policy.actionableNamespaces." .name) -}}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
=========================================================================================
Code fixes
=========================================================================================
*/}}

{{/* The coder namespace. One definition, because the Role, the Job env and the refusals must
     never disagree about which namespace holds `create jobs`. */}}
{{- define "hephaisto.codeFixNamespace" -}}
{{- .Values.codeFix.namespace -}}
{{- end -}}

{{/* The coder's ServiceAccount: bound to nothing, token never mounted. */}}
{{- define "hephaisto.codeFixServiceAccountName" -}}
{{- printf "%s-coder" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.codeFixImage" -}}
{{- printf "%s:%s" .Values.codeFix.image.repository (.Values.codeFix.image.tag | default .Chart.AppVersion) -}}
{{- end -}}

{{- define "hephaisto.codeFixEgressName" -}}
{{- printf "%s-coder-egress" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hephaisto.codeFixProxyUrl" -}}
{{- printf "http://%s.%s.svc:3128" (include "hephaisto.codeFixEgressName" .) (include "hephaisto.codeFixNamespace" .) -}}
{{- end -}}

{{- define "hephaisto.codeFixNugetClaim" -}}
{{- printf "%s-coder-nuget" (include "hephaisto.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* The pod label the coder Job carries - CodeFixJobSpec.AppLabel. Every coder policy selects
     on it, so it is written once. */}}
{{- define "hephaisto.codeFixPodSelector" -}}
app.kubernetes.io/name: hephaisto-coder
{{- end -}}

{{- define "hephaisto.codeFixEgressSelector" -}}
app.kubernetes.io/name: {{ include "hephaisto.codeFixEgressName" . }}
app.kubernetes.io/component: egress-proxy
{{- end -}}

{{/* Namespaces whose signals are the agent's own - escalated, never auto-actionable. Space
     separated, deduplicated, in a stable order. */}}
{{- define "hephaisto.selfNamespaces" -}}
{{- $l := list .Release.Namespace -}}
{{- with .Values.observabilityNamespace -}}{{- $l = append $l . -}}{{- end -}}
{{- if .Values.codeFix.enabled -}}{{- $l = append $l .Values.codeFix.namespace -}}{{- end -}}
{{- join " " (uniq $l) -}}
{{- end -}}

{{/*
The proxy allowlist as squid will accept it: allowlist + extraAllowlist, lower-cased,
deduplicated, and with every entry that another entry already covers DROPPED.

Squid 6 treats an overlap as FATAL - "'api.nuget.org' is a subdomain of '.nuget.org' ... You
need to remove 'api.nuget.org'" - and the pod never starts. Overlaps are natural to write (the
default list has two), so the chart resolves them rather than making every operator learn the
rule from a crash loop. `.example.com` covers `example.com`, every `x.example.com` and every
`.x.example.com`; a name without a leading dot covers only itself.
*/}}
{{- define "hephaisto.codeFixAllowlist" -}}
{{- $all := list -}}
{{- range concat .Values.codeFix.egressProxy.allowlist .Values.codeFix.egressProxy.extraAllowlist -}}
  {{- $all = append $all (lower .) -}}
{{- end -}}
{{- $all = uniq $all -}}
{{- range $d := $all -}}
  {{- $covered := false -}}
  {{- range $w := $all -}}
    {{- if and (hasPrefix "." $w) (ne $d $w) (or (hasSuffix $w $d) (eq (printf ".%s" $d) $w)) -}}
      {{- $covered = true -}}
    {{- end -}}
  {{- end -}}
  {{- if not $covered }}
{{ $d }}
  {{- end -}}
{{- end -}}
{{- end -}}

{{/*
Guard for the code-fix stage. Rendered from the Deployment, so it runs on every install - but
checks nothing unless codeFix.enabled, because a disabled stage renders nothing to protect.

The namespace refusals are the same list as validateActionableNamespaces, for the mirror-image
reason: that list is where the agent may DELETE, this one is where it may CREATE JOBS, and the
two must never meet. A namespace that is both is a namespace where the agent can run arbitrary
code next to the workloads it acts on. RbacSelfCheck refuses to boot on the same condition; this
refuses to render, which is earlier and cheaper.
*/}}
{{- define "hephaisto.validateCodeFix" -}}
{{- if .Values.codeFix.enabled -}}
{{- $ns := .Values.codeFix.namespace -}}
{{- if not $ns -}}
  {{- fail "codeFix.namespace is required when codeFix.enabled is true: it is the one namespace Hephaisto may create Jobs in." -}}
{{- end -}}
{{- if eq $ns "default" -}}
  {{- fail "codeFix.namespace may not be \"default\": it is where unlabelled workloads land, and the coder namespace must hold nothing but coders." -}}
{{- end -}}
{{- if hasPrefix "kube-" $ns -}}
  {{- fail (printf "codeFix.namespace may not be %q: a Job in a kube-* namespace runs beside the control plane." $ns) -}}
{{- end -}}
{{- if eq $ns .Release.Namespace -}}
  {{- fail (printf "codeFix.namespace may not be %q, the release namespace: a coder beside the agent could reach its database and its Service, and create jobs there would put arbitrary code next to the process that holds the kill switch." $ns) -}}
{{- end -}}
{{- if eq $ns .Values.observabilityNamespace -}}
  {{- fail (printf "codeFix.namespace may not be %q, the observability namespace: a coder there could tamper with the telemetry the agent judges itself by." $ns) -}}
{{- end -}}
{{- if has $ns .Values.policy.actionableNamespaces -}}
  {{- fail (printf "codeFix.namespace may not be %q: it is in policy.actionableNamespaces, and the namespace the agent may create Jobs in must never also be one it may act in." $ns) -}}
{{- end -}}
{{- if not .Values.codeFix.image.repository -}}
  {{- fail "codeFix.image.repository is required when codeFix.enabled is true: the coder image is a reviewed chart value, never something the agent chooses." -}}
{{- end -}}
{{- if not .Values.secrets.codeFix -}}
  {{- fail "secrets.codeFix is required when codeFix.enabled is true: the Secret in codeFix.namespace holding the coder's tokens. The chart never creates a Secret, it only references one." -}}
{{- end -}}
{{- if and (eq (lower (toString .Values.codeFix.mode)) "pr") (not .Values.auth.enabled) (not .Values.codeFix.allowUnauthenticatedApproval) -}}
  {{- fail "codeFix.mode pr is refused: auth.enabled is required, because approving a repository write needs an authenticated human, not a typed-in name. (codeFix.allowUnauthenticatedApproval exists for throwaway e2e clusters only.)" -}}
{{- end -}}
{{- if and .Values.codeFix.networkPolicy.enabled (not .Values.codeFix.egressProxy.enabled) -}}
  {{- fail "codeFix.egressProxy.enabled is required while codeFix.networkPolicy.enabled is true: the coder policy allows DNS and the proxy and nothing else, so without the proxy every attempt fails on its first request. Enable the proxy, or turn codeFix.networkPolicy off and control the coder's egress yourself." -}}
{{- end -}}
{{- range .Values.codeFix.repositories -}}
  {{- $host := (urlParse .url).host -}}
  {{- if not (has $host $.Values.codeFix.allowedRepositoryHosts) -}}
    {{- fail (printf "codeFix.repositories entry %q may not map to %q: its host %q is not in codeFix.allowedRepositoryHosts, so the mapping could never pass the host gate and would only ever be declined." .workload .url $host) -}}
  {{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Investigation in a Job (v0.12.0 F5): its port, or empty when it is off. Every refusal is one the
agent would also make at startup; rendering is the earlier, cheaper place to hear it.
*/}}
{{- define "hephaisto.investigatorPort" -}}
{{- $job := (.Values.investigation | default dict).job | default dict -}}
{{- if $job.enabled -}}
  {{- if not .Values.codeFix.enabled -}}
    {{- fail "investigation.job.enabled without codeFix.enabled is refused: the investigator Job borrows the coder's namespace, image, ServiceAccount, Secret and egress proxy. codeFix.mode may stay off." -}}
  {{- end -}}
  {{- $port := int $job.port -}}
  {{- if eq $port 8080 -}}
    {{- fail "investigation.job.port 8080 is refused: it is the console's. The investigator port admits investigator pods and nothing else, so it needs a port of its own." -}}
  {{- end -}}
  {{- if and .Values.webhookPort (eq $port (int .Values.webhookPort)) -}}
    {{- fail (printf "investigation.job.port %d is refused: it is webhookPort." $port) -}}
  {{- end -}}
  {{- with include "hephaisto.teamsActionsPort" . -}}
    {{- if eq $port (int .) -}}
      {{- fail (printf "investigation.job.port %d is refused: it is the Teams actions port." $port) -}}
    {{- end -}}
  {{- end -}}
  {{- with include "hephaisto.mcpPort" . -}}
    {{- if eq $port (int .) -}}
      {{- fail (printf "investigation.job.port %d is refused: it is the MCP port. The public endpoint and the investigator's are different surfaces with different callers." $port) -}}
    {{- end -}}
  {{- end -}}
  {{- $port -}}
{{- end -}}
{{- end -}}

{{/*
=========================================================================================
GitHub issues as work (v0.14.0)
=========================================================================================
Guard for the github block. Rendered from the Deployment; checks nothing unless github.enabled.
Every refusal is one the agent would also make at startup (GitHubOptions.Problems) - rendering
is the earlier, cheaper place to hear it - except the last, which only the chart can see.
*/}}
{{- define "hephaisto.validateGitHub" -}}
{{- $gh := .Values.github | default dict -}}
{{- $planner := $gh.planner | default dict -}}
{{- if $planner.enabled -}}
{{- if not $gh.enabled -}}
  {{- fail "github.enabled is required when github.planner.enabled is true: the planner reads its board with the agent's GitHub token, and without github.enabled there is none." -}}
{{- end -}}
{{- if not $planner.owner -}}
  {{- fail "github.planner.owner is required when github.planner.enabled is true: the organisation of github.com/orgs/<owner>/projects/<number>." -}}
{{- end -}}
{{- if not $planner.number -}}
  {{- fail "github.planner.number is required when github.planner.enabled is true: the project's number, the <number> of github.com/orgs/<owner>/projects/<number>." -}}
{{- end -}}
{{- end -}}
{{- if $gh.enabled -}}
{{- if not .Values.secrets.github -}}
  {{- fail "secrets.github is required when github.enabled is true: the Secret in the release namespace holding the agent's GitHub token under key GITHUB_TOKEN. The chart never creates a Secret, it only references one." -}}
{{- end -}}
{{- if not (($gh.issues | default dict).repositories) -}}
  {{- fail "github.issues.repositories is required when github.enabled is true: the list is what authorizes a repository, and an empty one polls nothing while looking configured." -}}
{{- end -}}
{{- if and .Values.codeFix.enabled (eq .Values.secrets.github .Values.secrets.codeFix) -}}
  {{- fail (printf "secrets.github may not be %q, the name of secrets.codeFix: the coder's Secret holds a token that may push and a model credential, and the agent - which reads every issue anybody opens - gets a token of its own that can do neither." .Values.secrets.github) -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
The proxy the agent's GitHub client uses, or empty for a direct connection: the coder's egress
proxy, when it is rendered and github.useEgressProxy has not been turned off. One definition,
because the env var, the agent's egress rule and the proxy's ingress rule must agree on whether
the agent goes that way.
*/}}
{{- define "hephaisto.gitHubProxyUrl" -}}
{{- $gh := .Values.github | default dict -}}
{{- if and $gh.enabled (ne $gh.useEgressProxy false) .Values.codeFix.enabled .Values.codeFix.egressProxy.enabled -}}
{{- include "hephaisto.codeFixProxyUrl" . -}}
{{- end -}}
{{- end -}}
