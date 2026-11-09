# Notifications

Until v0.3.0 nothing left the process. An escalation was a database row, an audit row and a nudge
to any browser tab that happened to be open — and if nobody was looking, nobody was told.

**It still ships that way.** `notifications.routes` is empty and no channel is configured, so a
stock install delivers nowhere. Two independent things have to change, in the same spirit as
`policy.actionableNamespaces` and `mode`.

## The pieces

| | |
|---|---|
| **Channels** | A generic outbound HTTP endpoint (optionally HMAC-signed), and Microsoft Teams via a Power Automate Workflows trigger |
| **Events** | `IncidentEscalated`, `ApprovalRequired`, `IncidentResolved`, `VerificationFailed`, `ModeChanged`, `PolicyChanged` |
| **Routing** | Per event, minimum severity and namespace. **Additive only — there is no deny rule** |
| **Delivery** | A Postgres outbox with exponential backoff and jitter |
| **Rate limiting** | Per-channel hourly cap, plus a per-workload cooldown |

## Configuring it

```yaml
notifications:
  # Required as soon as any route exists. The pod cannot work out the address a person reaches
  # it on - it only knows the one it binds - and every message exists to make someone open a link.
  baseUrl: https://hephaisto.example.com

  webhook:
    url: https://hooks.example.com/hephaisto
    signed: true          # needs secrets.notificationWebhook

  routes:
    - channel: webhook
      events: [IncidentEscalated, ApprovalRequired]
      minSeverity: Warning
      namespaces: [payments]
```

A route naming a channel you have not configured is **refused at startup** rather than silently
delivering nowhere. A route with no events, an unknown event, or an invalid severity is a chart
render failure.

## An event without an incident

A code fix for a [GitHub issue](/operate/github-issues) announces the same three events an
incident's does — `CodeFixPlanReady`, `CodeFixPrOpened`, `CodeFixFailed` — and has no incident
behind it: no severity, no namespace, no cluster, no kind, no labels. A route owns it only when it
is scoped by none of those and asks for no `minSeverity` above `Info`; a fallback route, which is
for incidents nobody else owns, does not. The same rule `ModeChanged` and `PolicyChanged` have
always lived by, for their scope.

The end of a run of the planner's [weekly review](/operate/planner#the-weekly-review) is the
event `PlannerReviewFinished`, under the same rule: one message per run, finished or failed, with
the counts of what can be closed, the bugs, what is still to build and what is unsure, and a link to
`/planner/review`. A failed run is announced at `Warning`, a finished one at `Info`, so a route with
`minSeverity: Warning` hears only of failures. It is staged with the run's end in one save, like
an escalation's delivery.

## Why the outbox exists

`IIncidentNotifier` is an in-process channel that drops on overflow by design — right for nudging a
browser tab, catastrophic for telling somebody the agent gave up.

So an incident **cannot reach `Escalated` without a delivery row existing**, because both are
written by one `SaveChangesAsync`. A pod restart cannot lose the news. This was tested the way it
needed to be: the receiver taken down, the agent restarted mid-flight, the receiver brought back,
and the delivery arriving anyway.

## Rate limiting behaviour worth knowing

The **first** message for a workload always goes out. Repeats within the cooldown are suppressed
and counted, not dropped silently — the count is visible on the incident. A storm therefore
produces one notification and a number, rather than either a flood or silence.

## Why the card carries a link {#why-the-card-carries-a-link}

The Teams card carries a **link, not an Approve button**.

Approving in-card means accepting inbound calls on a service whose only inbound route is
deliberately unauthenticated. That is a security change rather than a feature, and it is why
[the HTTP surface](/reference/http-api) has not gained a route since v0.3.0. The link goes to
Hephaisto's own approval UI, where the audit row already lives.

There is a test asserting no `Action.Submit` exists anywhere in the card. It exists to make
removing it a decision rather than a detail.

The registered bot (`notifications.teamsBot`) is the one channel where that decision was made, and
it is opt-in: with `notifications.teamsBot.actions.enabled`, an alert carries **Acknowledge**,
**Assign to me** and **Reinvestigate** for any member of the team, and **Close** for the people
`notifications.teamsBot.actions.approvers` names by Microsoft Entra object id. The inbound route
that makes this possible is on a port of its own and takes a Bot Framework token; see
[the chart's values](/reference/helm-values) for what turning it on exposes. Approving an action
is still a link unless `notifications.teamsBot.actions.approvals.enabled` is set as well: then the
card names the proposed action above **Approve** and **Deny**, for those same approvers.

## Signing

An unsigned outbound webhook means your receiver cannot tell a delivery from Hephaisto apart from
anything else that can reach it. Set `notifications.webhook.signed` and create
`secrets.notificationWebhook`.
