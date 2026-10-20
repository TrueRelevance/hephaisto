#!/usr/bin/env node
/*
 * Renders the demo site.
 *
 * Input:  src/Hephaisto.Agent/Demo/transcripts/*.json - the same files the compose demo seeds a
 *         database from, read in place rather than copied, so there is one corpus and not two.
 * Output: dist/, static HTML with no client JavaScript at all.
 *
 * Why static rather than the real console: the console is Blazor Server, so every page is a
 * SignalR circuit against a process holding a database connection. Hosting that publicly would
 * mean running an agent on the internet to show what an agent looks like. The transcripts already
 * contain everything a detail page renders - the step trace, the digests the model actually saw,
 * the untruncated evidence blobs, the citations, the plan and the grade - so the page can simply
 * be rendered once, at build time.
 *
 * It reuses the console's own app.css and its hp-* class vocabulary, so this is the product's
 * appearance rather than an imitation of it.
 *
 * TWO DELIBERATE DIFFERENCES FROM DemoSeeder, both of which would otherwise be lies:
 *
 * 1. Timestamps are NOT rebased. DemoSeeder shifts the whole graph forward because
 *    RetentionService would otherwise sweep evidence older than its window. There is no retention
 *    service here and nothing to protect the rows from, so the original recording times are shown
 *    and the provenance sentence says so instead of claiming a shift that did not happen.
 * 2. The provenance sentence is otherwise ported verbatim from DemoSeeder.Provenance, including
 *    the structurally-unsound caveat. It states the grade whatever the grade was.
 */

import { readFileSync, writeFileSync, mkdirSync, copyFileSync, readdirSync, rmSync } from 'node:fs'
import { join, dirname, basename } from 'node:path'
import { fileURLToPath } from 'node:url'
import { read as readEnums, label } from './enums.mjs'
import { read as readDisplay, look } from './display.mjs'

const HERE = dirname(fileURLToPath(import.meta.url))
const REPO = join(HERE, '..')
const TRANSCRIPTS = join(REPO, 'src', 'Hephaisto.Agent', 'Demo', 'transcripts')
const DIST = join(HERE, 'dist')

/** The domain, in one place - see docs-site/.vitepress/config.ts for the other three. */
const SITE = 'https://hephaisto.dev'
const DOCS = 'https://docs.hephaisto.dev'
const REPO_URL = 'https://github.com/TrueRelevance/hephaisto'

const ordinal = (id) => {
    const m = /^c(\d+)/.exec(id)
    return m ? Number(m[1]) : Number.MAX_SAFE_INTEGER
}

const maps = readEnums(join(REPO, 'src', 'Hephaisto.Core', 'Domain', 'Enums.cs'))
const display = readDisplay(join(REPO, 'src', 'Hephaisto.Agent', 'Components', 'Display.cs'))

// ---------------------------------------------------------------- formatting

const esc = (s) => String(s ?? '')
    .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')

const millis = (ms) => ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.round(ms)}ms`
const tokens = (n) => n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n)
const usd = (n) => n >= 0.01 ? `$${n.toFixed(2)}` : `$${n.toFixed(4)}`
const bytes = (n) => n >= 1024 ? `${(n / 1024).toFixed(1)} KiB` : `${n} B`
const day = (iso) => String(iso ?? '').slice(0, 10)
const stamp = (iso) => String(iso ?? '').slice(0, 19).replace('T', ' ')

const duration = (from, to) => {
    if (!from || !to) return '—'
    const s = Math.max(0, (new Date(to) - new Date(from)) / 1000)
    if (s < 60) return `${Math.round(s)}s`
    if (s < 3600) return `${Math.floor(s / 60)}m ${Math.round(s % 60)}s`
    return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
}

const sevClass = (v) => `sev-${label(maps, 'Severity', v).toLowerCase()}`
// Read from Display.cs rather than copied. The copy that used to live here had Escalated,
// Investigating and Detected all wrong, and each wrong value was another state's glyph.
const stateGlyph = (name) => look(display, 'stateGlyph', name)
const stateClass = (name) => look(display, 'stateClass', name)
const decisionGlyph = (name) => look(display, 'decisionGlyph', name)
const decisionClass = (name) => look(display, 'decisionClass', name)

/** The console's own state badge: glyph, word, then colour - never colour alone. */
const stateBadge = (name) =>
    `<span class="hp-state ${stateClass(name)}"><span class="glyph">${esc(stateGlyph(name))}</span> `
    + `${esc(name.toLowerCase())}</span>`

// ------------------------------------------------------- provenance, ported

/**
 * Ported from DemoSeeder.Provenance, with the timestamp clause corrected for this surface.
 * It states the grade whatever the grade was, and says when the run was unsound - hiding either
 * would quietly curate the demo up from the 8-of-10 this project publishes.
 */
function provenance(t) {
    // A live capture was never replayed, has no cassette behind it and no second model's tool
    // trace, so none of the replay sentence is true of it. Ported from DemoSeeder.Provenance,
    // which branches the same way and for the same reason.
    if (capture(t) === 'Cluster') {
        return `DEMO DATA — LIVE CAPTURE, exported from the agent's own database after a real run `
            + `on a real k3s cluster. Investigated by ${t.origin.modelId} on `
            + `${day(t.origin.recordedAt)}, agent ${t.origin.agentVersion}. The state, the `
            + 'transitions and the policy decision are what the agent did, not what this page '
            + 'composed. Timestamps are the original recording times. Not part of the replayed '
            + 'cassette corpus and not counted in its score.'
    }

    const caveat = t.score.structurallySound
        ? ''
        : ' NOTE: this replay was structurally unsound — the recorded tool trace did not cover '
        + 'what the model asked for, so the verdict reflects the recording rather than the '
        + 'reasoning.'

    const against = t.origin.recordedAgainstModelId ?? 'an earlier run'

    return `DEMO DATA — replayed from cassette ${t.cassetteId}, recorded against a real k3s `
        + `cluster. Investigated by ${t.origin.modelId} on ${day(t.origin.recordedAt)} against a `
        + `tool trace from ${against}, and graded ${t.score.verdict} against the answer key. `
        + `Timestamps are the original recording times.${caveat}`
}

/** How the file was made. The payload cannot answer this; TranscriptOrigin.Capture does. */
const capture = (t) => t.origin.capture ?? 'Replay'

const events = (t) =>
    [...(t.incident.events ?? [])].sort((a, b) => new Date(a.at) - new Date(b.at))

/**
 * The state history: read when the file has one, composed when it does not.
 *
 * The discriminator is the transitions themselves, because they ARE the history - a flag would
 * be a second source of truth for a fact the payload already settles, and it would fail OPEN:
 * an exporter that forgot the events but set the flag would render an empty timeline under a
 * heading promising what happened. This fails safe, into the labelled synthesis.
 */
function transitions(t) {
    const recorded = events(t)

    if (recorded.length > 0) {
        return recorded.map((e) => ({
            from: e.from == null ? null : label(maps, 'IncidentState', e.from),
            to: label(maps, 'IncidentState', e.to),
            at: e.at,
            reason: e.reason,
        }))
    }

    const inv = t.investigation
    const noPlan = !inv.plan

    return [
        { from: null, to: 'Detected', at: t.incident.openedAt, reason: provenance(t) },
        {
            from: 'Detected', to: 'Investigating', at: inv.startedAt,
            reason: `Investigating with ${inv.modelId}.`,
        },
        {
            from: 'Investigating', to: 'Escalated', at: inv.completedAt ?? inv.startedAt,
            reason: noPlan
                ? 'Diagnosed, and no action was proposed. Escalated to a human.'
                : 'Diagnosed, and a plan was proposed. Nothing executes in Observe mode.',
        },
    ]
}

/**
 * The state to show in the badge.
 *
 * Deliberately NOT `incident.state`. Detected is zero, which is also what the serializer writes
 * for a field nothing set, so on a replayed transcript the two are indistinguishable and
 * rendering it would swap today's wrong answer for a different one. When there IS a history the
 * terminal state comes from its last transition, which arrives with a timestamp and a reason -
 * so the badge and the timeline cannot disagree.
 */
function terminalState(t) {
    const recorded = events(t)

    if (recorded.length === 0) {
        return 'Escalated'
    }

    const last = label(maps, 'IncidentState', recorded[recorded.length - 1].to)
    const column = label(maps, 'IncidentState', t.incident.state)

    if (last !== column) {
        // A column and a log that disagree is a bug worth failing a build over, not worth
        // rendering. Same second-opinion reasoning as the unredacted-address scan below.
        throw new Error(
            `${t.cassetteId}: the last transition says ${last} and incident.state says ${column}. `
            + 'One of them is wrong and this page would publish whichever it happened to read.',
        )
    }

    return last
}

/**
 * The plan, and what the policy engine did with it.
 *
 * The banner used to say "Would have done this - nothing was executed" unconditionally, which
 * was true of every replayed transcript and is false of a cluster capture where an action was
 * admitted and run. It branches on the actions now.
 *
 * The policy cell is the point of the whole page for the reader docs/design.md names - a
 * platform team asking "what stops it?". A decision with no reasons beside it is an assertion;
 * with them it is an argument. Every class here already exists in the console's app.css and is
 * already photographed in design/gallery.html, so nothing new is invented.
 */
function renderPlan(plan) {
    const actions = plan.actions ?? []
    const executed = actions.filter((a) => a.executedAt && !a.dryRun)
    const denied = actions.filter((a) => label(maps, 'PolicyDecision', a.decision) === 'Deny')

    const banner = executed.length > 0
        ? `<div class="hp-plan-banner">
    <span class="glyph">${esc(decisionGlyph('Allow'))}</span>
    <strong>This was executed.</strong>
    <span class="hp-muted">The policy engine admitted it, C# over a closed action vocabulary
      carried it out, and deterministic predicates checked the result at T+60s, T+5m and
      T+15m. The planning model held no tools at any point.</span>
  </div>`
        : denied.length > 0
            ? `<div class="hp-plan-banner">
    <span class="glyph">${esc(decisionGlyph('Deny'))}</span>
    <strong>Refused by policy — the agent diagnosed this and was not allowed to act.</strong>
    <span class="hp-muted">The plan below was produced, judged, and denied before anything
      could touch the cluster. The reason is recorded on the action itself.</span>
  </div>`
            : `<div class="hp-plan-banner">
    <span class="glyph">${esc(decisionGlyph('RequireApproval'))}</span>
    <strong>Would have done this — nothing was executed.</strong>
    <span class="hp-muted">The planning model holds no tools and emits JSON against a schema;
      execution is separate C# over a closed action vocabulary. Every action below was judged by
      the policy engine before anything could touch it.</span>
  </div>`

    const rows = actions.map((a) => {
        const decision = label(maps, 'PolicyDecision', a.decision)
        const reasons = (a.decisionReasons ?? []).length
            ? `<ul class="hp-reasons">${a.decisionReasons.map((r) => `<li>${esc(r)}</li>`).join('')}</ul>`
            : ''

        const exec = a.executedAt
            ? `<div class="hp-exec mono">state ${esc(label(maps, 'ActionState', a.state))}
                 · executed ${esc(stamp(a.executedAt))}${a.dryRun ? ' · dryRun' : ''}
                 · by ${esc(a.approvedBy ?? 'nobody')}
                 (${esc(label(maps, 'ApprovalSource', a.approvalSource))})</div>`
            : `<div class="hp-exec mono">state ${esc(label(maps, 'ActionState', a.state))}</div>`

        return `<li>
      <div class="mono"><strong>${esc(label(maps, 'ActionType', a.type))}</strong>
        ${esc(a.target?.name ?? '')}
        <span class="hp-chip mono">${esc(label(maps, 'RiskTier', a.risk))} risk</span>
        <span class="hp-decision ${decisionClass(decision)}"><span class="glyph">${esc(decisionGlyph(decision))}</span>
          ${esc(decision.toLowerCase())}</span></div>
      ${a.predictedEffect ? `<p class="hp-muted">${esc(a.predictedEffect)}</p>` : ''}
      ${reasons}
      ${exec}
    </li>`
    }).join('')

    const body = actions.length === 0
        ? `<p class="hp-empty">No action was proposed.${plan.noActionRequired
            ? ' The planner set <code>no_action_required</code> — which is the expected outcome for'
              + ' most incidents, and what the planning prompt tells it to default to.' : ''}</p>`
        : `<ul class="hp-plan">${rows}</ul>`

    return `${banner}
  ${plan.summary ? `<p class="hp-plan-summary">${esc(plan.summary)}</p>` : ''}
  ${body}`
}

// ------------------------------------------------------------------ chrome

function page({ title, description, body, depth }) {
    const up = '../'.repeat(depth)
    return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>${esc(title)}</title>
<meta name="description" content="${esc(description)}" />
<meta name="theme-color" content="#212123" media="(prefers-color-scheme: dark)" />
<meta name="theme-color" content="#f8f7f8" media="(prefers-color-scheme: light)" />
<link rel="icon" href="${up}favicon.svg" type="image/svg+xml" />
<link rel="stylesheet" href="${up}tokens.css" />
<link rel="stylesheet" href="${up}app.css" />
<link rel="stylesheet" href="${up}demo.css" />
</head>
<body>
<header class="hp-nav">
  <a class="hp-brand" href="${up}index.html"><span class="hp-brand-name">Hephaisto</span> demo</a>
  <span class="spacer"></span>
  <a href="${DOCS}">Docs</a>
  <a href="${SITE}">Site</a>
  <a href="${REPO_URL}">GitHub</a>
</header>
<main class="hp-wrap">
${body}
</main>
<footer class="hp-foot">
  <span>Recorded investigations, rendered as static pages. Nothing here is live.</span>
  <a href="${REPO_URL}">Source</a>
  <a href="${DOCS}/internals/evaluation">How this was measured</a>
  <span>AGPL-3.0</span>
</footer>
</body>
</html>
`
}

// ------------------------------------------------------------------- index

/**
 * One sentence about what the live captures show, in whichever combination exists.
 *
 * Built rather than concatenated from optional clauses: with no executed action the old
 * version rendered "1 shows it being refused", where "it" had lost its antecedent along with
 * the clause that vanished.
 */
function headline(acted, denied, n) {
    const verb = (k) => (k === 1 ? 'shows' : 'show')

    if (acted > 0 && denied > 0) {
        return `${acted} of these ${n} ${verb(acted)} the agent acting, and ${denied} `
            + `${verb(denied)} it refused by policy.`
    }

    if (acted > 0) {
        return `${acted} of these ${n} ${verb(acted)} the agent acting.`
    }

    return `${denied} of these ${n} ${verb(denied)} the agent refused by policy - it diagnosed `
        + 'the fault, proposed a fix, and was not allowed to apply it.'
}

function renderIndex(all) {
    const rows = all.map((t) => {
        const inv = t.investigation
        const kind = label(maps, 'SignalKind', t.incident.kind)
        const sev = label(maps, 'Severity', t.incident.severity)
        const finding = inv.findings?.[0]
        const verdict = t.score.verdict

        return `<tr class="hp-row">
  <td class="c-state">${stateBadge(terminalState(t))}</td>
  <td class="c-sev"><span class="hp-sev ${sevClass(t.incident.severity)}">${esc(sev)}</span></td>
  <td class="c-kind mono">${esc(kind)}</td>
  <td class="c-target mono"><span class="hp-ns">${esc(t.incident.target?.namespace ?? '—')}</span>/${esc(t.incident.target?.name ?? '—')}</td>
  <td class="c-title"><a href="i/${esc(t.cassetteId)}.html">${esc(t.incident.title)}</a>
      <span class="hp-sub">${esc(finding ? finding.hypothesis.slice(0, 110) + (finding.hypothesis.length > 110 ? '…' : '') : 'No finding.')}</span></td>
  <td class="c-num mono">${inv.stepsUsed}</td>
  <td class="c-num mono">${usd(inv.costUsd)}</td>
  <td class="c-diag"><span class="hp-chip ${verdict === 'Correct' ? 'chip-primary' : ''}">${esc(verdict)}</span></td>
</tr>`
    }).join('\n')

    // Two disjoint sets. The published accuracy figure is over the REPLAYED corpus, and
    // widening its denominator to include cluster captures would change the measurement while
    // keeping the label - so every count below says which set it is over.
    const replays = all.filter((t) => capture(t) === 'Replay')
    const captures = all.filter((t) => capture(t) === 'Cluster')

    const correct = replays.filter((t) => t.score.verdict === 'Correct').length
    const declined = replays.filter((t) => t.score.planVerdict === 'CorrectlyDeclined').length
    const missed = replays.filter((t) => t.score.planVerdict === 'MissedAnAction').length
    const noPlan = replays.filter((t) => t.score.planVerdict === 'NoPlan').length

    const acted = all.filter((t) =>
        (t.investigation.plan?.actions ?? []).some((a) => a.executedAt && !a.dryRun)).length
    const denied = all.filter((t) => (t.investigation.plan?.actions ?? [])
        .some((a) => label(maps, 'PolicyDecision', a.decision) === 'Deny')).length

    const cost = all.reduce((a, t) => a + t.investigation.costUsd, 0)

    // Every one of these was hard-coded as a word, and the words had drifted from the files:
    // "Eight correctly declined, one proposed nothing, and in two an action was missed" is
    // eleven statements about ten investigations. Counting is the fix.
    const n = all.length

    const body = `
<div class="hp-demo-banner">
  <strong>These are recordings, not a live agent.</strong>
  ${n} investigations the agent actually ran against a k3s cluster full of seeded faults, rendered
  as static pages. Nothing here is connected to anything: no cluster, no database, no
  model. Each incident's timeline says where it came from, which model investigated it,
  and how it was graded — including the one that got it wrong.${captures.length > 0
    ? ` ${captures.length === 1 ? 'One of them was' : `${captures.length} of them were`} exported
      from the agent's own database after a real run, so the state and the policy decision on
      ${captures.length === 1 ? 'it' : 'those'} are recorded rather than composed.` : ''}
</div>

<h1>${n} investigations</h1>

<p class="hp-lede">
  Every row below links to the full step trace: what the model asked for, what it was shown, the
  diagnosis it wrote, and every evidence excerpt resolving back to the untruncated tool output it
  came from.
</p>

<dl class="hp-summary">
  <div><dt>graded correct</dt><dd class="mono">${correct} of ${replays.length} replayed</dd></div>
  ${acted > 0 ? `<div><dt>acted on</dt><dd class="mono">${acted}</dd></div>` : ''}
  ${denied > 0 ? `<div><dt>refused by policy</dt><dd class="mono">${denied}</dd></div>` : ''}
  <div><dt>total cost</dt><dd class="mono">${usd(cost)}</dd></div>
  <div><dt>investigating model</dt><dd class="mono">${esc(replays[0].origin.modelId)}</dd></div>
  <div><dt>tool traces from</dt><dd class="mono">${esc(replays[0].origin.recordedAgainstModelId ?? '—')}</dd></div>
</dl>

<table class="hp-table hp-incidents">
<thead><tr>
  <th>state</th><th>sev</th><th>kind</th><th>target</th><th>incident</th>
  <th>steps</th><th>cost</th><th>graded</th>
</tr></thead>
<tbody>
${rows}
</tbody>
</table>

<section class="hp-section">
<h2>What this shows, and what it does not</h2>
${acted + denied > 0 ? `<p>
  <strong>${headline(acted, denied, n)}</strong>
  ${acted + denied === 1 ? 'That one was' : 'Those were'} exported from the agent's own database
  after a real run, which is the only way this page can show either: a replay serves a recorded
  tool trace to a live model and constructs no executor, no policy engine and no state machine,
  so it has nothing to act with and nothing to be refused by.
</p>` : ''}
<p>
  <strong>None of the ${replays.length} replayed investigations shows the agent acting.</strong>
  ${declined} correctly declined to propose an action, ${noPlan === 1 ? 'one produced no plan at all'
    : `${noPlan} produced no plan at all`}, and in ${missed} the grader judged an action was missed.
  That is a measured property of the model these were replayed against, not a limitation of this
  page — on that fixture <code>gpt-oss:120b</code> proposed an action in 1 of 11 runs where the
  planner actually ran, where <code>deepseek-v4-flash</code> proposed one in 4 of 8.
</p>
<p>
  So the honest version is: the replayed corpus is what the agent's <em>diagnosis</em> looks like,
  and whether it acts is a separate question measured separately, on a separate fixture.
  <a href="${DOCS}/internals/evaluation">The evidence page</a> has the denominators.
</p>
</section>
`
    return page({
        title: `Hephaisto — ${n} recorded investigations`,
        description:
            `${n} real Kubernetes incident investigations, with the full step trace, evidence and `
            + 'grading. Static pages, no account, nothing live.',
        body,
        depth: 0,
    })
}

// ------------------------------------------------------------------ detail

function renderStep(step, blobsById) {
    const isTool = label(maps, 'StepKind', step.kind) === 'ToolCall'
    const metrics = [
        millis(step.durationMs),
        (step.inputTokens + step.outputTokens) > 0
            ? `${tokens(step.inputTokens + step.outputTokens)} tok` : null,
        step.costUsd > 0 ? usd(step.costUsd) : null,
        step.resultBytes > 0 ? bytes(step.resultBytes) : null,
    ].filter(Boolean).join(' · ')

    const blob = step.rawBlobId ? blobsById.get(step.rawBlobId) : null

    return `<li>
<details id="step-${esc(step.id)}" class="hp-step${step.failed ? ' step-failed' : ''}">
  <summary>
    <span class="hp-ordinal mono">${step.ordinal}</span>
    <span class="hp-step-kind ${isTool ? 'kind-tool' : 'kind-llm'}">${isTool ? 'tool' : 'llm'}</span>
    <span class="hp-step-name mono">${esc(step.toolName ?? (isTool ? 'tool' : 'model turn'))}</span>
    ${step.toolServer ? `<span class="hp-server mono">${esc(step.toolServer)}</span>` : ''}
    <span class="hp-step-metrics mono">${esc(metrics)}</span>
    ${step.resultTruncated ? '<span class="hp-truncated"><span class="glyph">~</span> truncated</span>' : ''}
    ${step.failed ? '<span class="hp-failed"><span class="glyph">x</span> failed</span>' : ''}
  </summary>
  <div class="hp-step-body">
    ${step.error ? `<div class="hp-callout callout-error"><span class="glyph">x</span> ${esc(step.error)}</div>` : ''}
    ${step.arguments ? `<h4>arguments</h4><pre class="hp-code">${esc(step.arguments)}</pre>` : ''}
    <h4>${isTool
        ? 'result digest <span class="hp-muted">— what the model actually saw</span>'
        : 'model output <span class="hp-muted">— its reasoning, and the tools it asked for</span>'}</h4>
    <pre class="hp-code">${esc(step.resultDigest ?? '')}</pre>
    ${blob ? `<details class="hp-raw"><summary>raw result — the untruncated tool output (${bytes(blob.content.length)})</summary>
      <pre class="hp-code hp-raw-code">${esc(blob.content)}</pre></details>` : ''}
  </div>
</details>
</li>`
}

function renderFinding(f, stepsById) {
    const cites = (f.evidence ?? []).map((e) => {
        const step = stepsById.get(e.stepId)
        return `<li class="hp-cite${step ? '' : ' hp-cite-broken'}">
  ${step ? `<a href="#step-${esc(e.stepId)}">step ${step.ordinal}</a>` : 'unresolved step'}
  <span class="hp-excerpt mono">${esc(e.excerpt)}</span>
</li>`
    }).join('\n')

    return `<article class="hp-finding${f.isPrimary ? ' finding-primary' : ''}">
  <header>
    ${f.isPrimary ? '<span class="hp-chip chip-primary">primary</span>' : ''}
    <span class="hp-chip mono">${esc(f.category)}</span>
    <span class="hp-conf" title="Model's own estimate. Advisory, uncalibrated — not a measured probability.">
      <span class="hp-conf-track"><span class="hp-conf-fill" style="width:${Math.round(f.confidence * 100)}%"></span></span>
      <span class="mono">${f.confidence.toFixed(2)}</span>
    </span>
  </header>
  <p class="hp-hypothesis">${esc(f.hypothesis)}</p>
  ${cites
      ? `<ul class="hp-evidence">${cites}</ul>`
      : '<p class="hp-empty">No evidence. This finding cites nothing and should not have survived.</p>'}
</article>`
}

function renderDetail(t, all) {
    const inv = t.investigation
    const inc = t.incident
    const blobsById = new Map((t.blobs ?? []).map((b) => [b.id, b]))
    const stepsById = new Map((inv.steps ?? []).map((s) => [s.id, s]))
    const kind = label(maps, 'SignalKind', inc.kind)
    const sev = label(maps, 'Severity', inc.severity)
    const state = terminalState(t)

    // Only worth a chip when it says something: None is the absence of a reason, not a reason.
    const escalationName = label(maps, 'EscalationReason', inc.escalationReason ?? 0)
    const escalation = escalationName === 'None' ? null : escalationName

    const idx = all.findIndex((x) => x.cassetteId === t.cassetteId)
    const prev = all[idx - 1]
    const next = all[idx + 1]

    const signals = (inc.signals ?? []).map((s) => `<tr>
  <td class="mono">${esc(s.reason)}</td>
  <td class="hp-msg">${esc(s.message)}</td>
  <td class="c-time mono">${esc(stamp(s.firstSeen))}</td>
  <td class="c-num mono">${s.count}</td>
</tr>`).join('\n')

    const timeline = transitions(t).map((e) => `<li>
  <span class="mono">${esc(stamp(e.at))}</span>
  <strong>${e.from ? `${esc(e.from)} → ` : ''}${esc(e.to)}</strong>
  <span class="hp-muted">${esc(e.reason)}</span>
</li>`).join('\n')

    const findings = (inv.findings ?? []).length
        ? inv.findings.map((f) => renderFinding(f, stepsById)).join('\n')
        : `<p class="hp-empty">No surviving findings. A finding whose evidence failed the
           grounding check is dropped rather than shown as fact, so this can mean the model
           concluded nothing <em>or</em> that everything it claimed failed verification.</p>`

    const plan = inv.plan ? renderPlan(inv.plan) : '<p class="hp-empty">No plan was produced.</p>'

    const body = `
<div class="hp-demo-banner">${esc(provenance(t))}</div>

<p class="hp-muted"><a href="../index.html">← all ${all.length} investigations</a></p>

<div class="hp-detail-header">
  <h1>${esc(inc.title)}</h1>
  <div class="hp-badges">
    ${stateBadge(state)}${escalation
      ? `<span class="hp-chip mono">${esc(escalation)}</span>` : ''}
    <span class="hp-sev ${sevClass(inc.severity)}">${esc(sev)}</span>
    <span class="hp-chip mono">${esc(kind)}</span>
    <span class="hp-chip mono">cassette ${esc(t.cassetteId)}</span>
  </div>
  <dl class="hp-kv">
    <div><dt>target</dt><dd class="mono">${esc(inc.target?.namespace)}/${esc(inc.target?.kind)}/${esc(inc.target?.name)}</dd></div>
    <div><dt>workload</dt><dd class="mono">${esc(inc.target?.workloadKey ?? '—')}</dd></div>
    <div><dt>node</dt><dd class="mono">${esc(inc.target?.nodeName ?? '—')}</dd></div>
    <div><dt>opened</dt><dd class="mono">${esc(stamp(inc.openedAt))}</dd></div>
    <div><dt>investigated</dt><dd class="mono">${duration(inv.startedAt, inv.completedAt)}</dd></div>
    ${inc.resolvedAt
      ? `<div><dt>resolved</dt><dd class="mono">${esc(duration(inc.openedAt, inc.resolvedAt))} after it opened</dd></div>`
      : ''}
  </dl>
  ${inc.resolution
    ? `<p class="hp-plan-summary">${esc(inc.resolution)}</p>` : ''}
</div>

<section class="hp-section">
  <h2>expected root cause <span class="hp-muted">— the answer key</span></h2>
  <p class="hp-answer-key">${esc(t.expectedRootCause)}</p>
  <p class="hp-muted">
    This is never shown to the model. It is what the grader compared the diagnosis against, and it
    is on this page because a demo that showed only the answer would be asking you to take the
    grading on trust.
  </p>
</section>

<section class="hp-section">
  <h2>signals <span class="hp-count">${(inc.signals ?? []).length}</span></h2>
  ${signals ? `<table class="hp-table"><thead><tr><th>reason</th><th>message</th><th>first seen</th><th>n</th></tr></thead><tbody>${signals}</tbody></table>`
      : '<p class="hp-empty">No signals recorded.</p>'}
  <h3>state transitions</h3>
  <ol class="hp-timeline">${timeline}</ol>
</section>

<section class="hp-section hp-investigation">
  <h2>investigation</h2>
  <dl class="hp-kv hp-inv-header">
    <div><dt>model</dt><dd class="mono">${esc(inv.modelId)}</dd></div>
    <div><dt>steps</dt><dd class="mono">${inv.stepsUsed}</dd></div>
    <div><dt>tool calls</dt><dd class="mono">${inv.toolCallsUsed}</dd></div>
    <div><dt>tokens</dt><dd class="mono">${tokens(inv.inputTokens)} in / ${tokens(inv.outputTokens)} out</dd></div>
    <div><dt>cost</dt><dd class="mono">${usd(inv.costUsd)}</dd></div>
    <div><dt>confidence</dt><dd class="mono">${(inv.confidence ?? 0).toFixed(2)}</dd></div>
    <div><dt>ended</dt><dd class="mono">${esc(label(maps, 'TerminationReason', inv.terminationReason))}</dd></div>
  </dl>

  <h3>trace</h3>
  <ol class="hp-steps">
${(inv.steps ?? []).map((s) => renderStep(s, blobsById)).join('\n')}
  </ol>

  <h3>findings <span class="hp-count">${(inv.findings ?? []).length}</span></h3>
  ${findings}

  <h3>plan</h3>
  ${plan}
</section>

<section class="hp-section">
  <h2>how it was graded</h2>
  <dl class="hp-kv">
    <div><dt>root cause</dt><dd class="mono">${esc(t.score.verdict)}</dd></div>
    <div><dt>plan</dt><dd class="mono">${esc(t.score.planVerdict ?? '—')}</dd></div>
    <div><dt>structurally sound</dt><dd class="mono">${t.score.structurallySound ? 'yes' : 'no'}</dd></div>
    <div><dt>recorded</dt><dd class="mono">${esc(day(t.origin.recordedAt))}</dd></div>
    <div><dt>agent version</dt><dd class="mono">${esc(t.origin.agentVersion)}</dd></div>
  </dl>
  ${t.origin.promptFreshness ? `<p class="hp-muted">${esc(t.origin.promptFreshness)}</p>` : ''}
  ${t.score.judgeReason ? `<p class="hp-muted">${esc(t.score.judgeReason)}</p>` : ''}
</section>

<nav class="hp-pager">
  ${prev ? `<a href="${esc(prev.cassetteId)}.html">← ${esc(prev.incident.title)}</a>` : '<span></span>'}
  ${next ? `<a href="${esc(next.cassetteId)}.html">${esc(next.incident.title)} →</a>` : '<span></span>'}
</nav>
`
    return page({
        title: `${inc.title} — Hephaisto demo`,
        description: t.expectedRootCause.slice(0, 180),
        body,
        depth: 1,
    })
}

// -------------------------------------------------------------------- main

function main() {
    const files = readdirSync(TRANSCRIPTS).filter((f) => f.endsWith('.json')).sort()

    if (files.length === 0) {
        throw new Error(`no transcripts in ${TRANSCRIPTS} - refusing to build an empty demo`)
    }

    const all = files.map((f) => JSON.parse(readFileSync(join(TRANSCRIPTS, f), 'utf8')))
        // Number('13-resolved') is NaN, and a comparator that returns NaN gives an
        // implementation-defined order - which would scramble the index and the prev/next
        // pager silently, with no id that is not exactly c<digits>.
        .sort((a, b) =>
            (capture(b) === 'Cluster') - (capture(a) === 'Cluster')
            || ordinal(a.cassetteId) - ordinal(b.cassetteId)
            || a.cassetteId.localeCompare(b.cassetteId))

    // The redactor replaces IPv4 with 0.0.0.0. Publishing a transcript that slipped through would
    // be publishing an address, so the build refuses rather than trusting that redact was run.
    //
    // THIS CHECK HAS NOW BEEN WRONG TWICE, both times by agreeing with the redactor. First it
    // used \b, which is what the redactor used, and both called c8.json clean when it was not.
    // Then it copied the replacement lookbehind - and both missed a pod IP 66 times in the first
    // exported transcript, because the serializer writes a quote as \u0022 and the boundary
    // before the address is therefore the digit 2.
    //
    // So it no longer reasons about boundaries in an escaped document at all. It DECODES the
    // \uXXXX escapes first and scans what the text means. That is a different question from the
    // one the redactor answers, which is the whole point of a second opinion: sharing the
    // premise is how it agreed with the bug twice.
    const IPV4 = /(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])/g
    const decodeEscapes = (s) => s.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    for (const [i, t] of all.entries()) {
        const raw = decodeEscapes(readFileSync(join(TRANSCRIPTS, files[i]), 'utf8'))
        const leaked = [...raw.matchAll(IPV4)].map((m) => m[0]).filter((a) => a !== '0.0.0.0')
        if (leaked.length > 0) {
            throw new Error(
                `${files[i]} contains ${leaked.length} unredacted IPv4 address(es). `
                + 'Run `hephaisto-eval redact` before building - this site is published.',
            )
        }
        void t
    }

    rmSync(DIST, { recursive: true, force: true })
    mkdirSync(join(DIST, 'i'), { recursive: true })
    mkdirSync(join(DIST, 'fonts'), { recursive: true })

    for (const asset of ['tokens.css', 'app.css', 'demo.css', 'favicon.svg']) {
        copyFileSync(join(HERE, asset), join(DIST, asset))
    }
    for (const font of readdirSync(join(HERE, 'fonts'))) {
        copyFileSync(join(HERE, 'fonts', font), join(DIST, 'fonts', font))
    }

    writeFileSync(join(DIST, 'index.html'), renderIndex(all))
    for (const t of all) {
        writeFileSync(join(DIST, 'i', `${t.cassetteId}.html`), renderDetail(t, all))
    }

    console.log(`wrote ${all.length + 1} pages to dist/ from ${basename(TRANSCRIPTS)}/`)
}

main()
