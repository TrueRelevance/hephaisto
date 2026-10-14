import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { type AgentRunResult, buildAgentEnv, guardEnvFor, runAgent } from './agent.js';
import type { RunnerEnv, WorkPaths } from './config.js';
import { type GuardContext, realish } from './guard.js';
import { type PrepareHandoff, newPrepareHandoff } from './handoff.js';
import { billingOf } from './investigate.js';
import { log } from './log.js';
import { loadTemplate, render, xmlEscape } from './prompts.js';
import { minimalFailed } from './result.js';
import { type TriageOutcome, type TriageRequest, type TriageResult, rawSchema, validateWith } from './schemas.js';
import type { QueryFn } from './sdk.js';
import { makeDirs, prepareContext } from './workspace.js';

// The triage phase (v0.15.0): a person on the planner asked Claude to label one GitHub issue and
// to say what working on it means. Nothing is cloned but dev-context - the notes and the memory
// are what tell the model which service an issue is about - and nothing is written: the model
// answers labels, a summary, an analysis and a plan, and Hephaisto puts the labels on and posts
// the analysis.
//
//   prepare   clones dev-context (the only step that needs GITHUB_TOKEN)
//   coder     runs the model read-only in /work/context and prints the result

export interface TriagePrepareDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  abort: AbortController;
  startedAt: number;
}

export interface TriageDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  deadline: number;
  abort: AbortController;
  makeQuery: (vars: Record<string, string>) => Promise<QueryFn>;
}

/** What the model answers. `questions` is required of it, as in a plan: it has to consider whether anything is open. */
const AGENT_TRIAGE_FIELDS = ['outcome', 'labels', 'summary', 'analysis', 'steps', 'questions'] as const;

export type AgentTriage = Pick<TriageResult, Exclude<(typeof AGENT_TRIAGE_FIELDS)[number], 'outcome'>> & { outcome: 'triaged' | 'failed' };

export function triageOutputSchema(): Record<string, unknown> {
  const props = rawSchema('triage').properties as Record<string, Record<string, unknown>>;
  return {
    type: 'object',
    additionalProperties: false,
    required: [...AGENT_TRIAGE_FIELDS],
    properties: {
      ...Object.fromEntries(AGENT_TRIAGE_FIELDS.map((k) => [k, props[k]])),
      outcome: { enum: ['triaged', 'failed'], description: 'triaged: you read the issue and answer the rest. failed: you could not, and summary says why.' },
    },
  };
}

/** The issue in ONE <untrusted-issue> element, escaped, so nothing in it can close the element and go on as the prompt. */
export function triageIssueElement(req: TriageRequest): string {
  const i = req.issue;
  return [
    `<untrusted-issue type="${xmlEscape(i.type ?? '')}">`,
    `<title>${xmlEscape(i.title)}</title>`,
    `<opened-by>${xmlEscape(i.author)}</opened-by>`,
    `<body>${xmlEscape(i.body ?? '')}</body>`,
    '</untrusted-issue>',
  ].join('\n');
}

/** The repository's labels, one per line. Names and descriptions are an administrator's, escaped all the same. */
export function labelsBlock(req: TriageRequest): string {
  if (req.labels.length === 0) return '- (the repository has no labels: answer an empty list)';
  return req.labels.map((l) => `- \`${l.name.replace(/`/g, "'")}\`${l.description ? ` - ${xmlEscape(l.description)}` : ''}`).join('\n');
}

/** Only labels the repository has, by its own spelling, each once, and never the planner's own two. */
export function knownLabels(chosen: string[], req: TriageRequest): string[] {
  const byName = new Map(req.labels.map((l) => [l.name.toLowerCase(), l.name]));
  const out: string[] = [];
  for (const c of chosen) {
    const name = byName.get(c.trim().toLowerCase());
    if (!name || out.includes(name) || ['today', 'not today'].includes(name.toLowerCase())) continue;
    out.push(name);
  }
  return out.slice(0, 10);
}

export async function prepareTriage(req: TriageRequest, deps: TriagePrepareDeps): Promise<PrepareHandoff> {
  const h = newPrepareHandoff(req.attempt_id, 'triage', deps.startedAt);
  try {
    makeDirs(deps.paths);
    const ctx = await prepareContext(req, deps.env, deps.paths, deps.abort.signal);
    h.context_sha = ctx.contextSha;
    return h;
  } catch (e) {
    log.error(`triage could not be prepared: ${(e as Error).stack ?? String(e)}`);
    return { ...h, terminal: { ...minimalFailed('triage', req.attempt_id, (e as Error).message, 'api'), context_sha: h.context_sha } };
  }
}

const STOP: Record<string, TriageOutcome> = { max_turns: 'max_turns', budget: 'budget_exhausted', rate_limited: 'rate_limited' };

export async function runTriage(req: TriageRequest, deps: TriageDeps, h: PrepareHandoff): Promise<TriageResult> {
  const { env, paths } = deps;
  const result = minimalFailed('triage', req.attempt_id, 'triage did not complete', billingOf(env));
  result.context_sha = h.context_sha;
  try {
    makeDirs(paths);
    const schema = triageOutputSchema();
    const vars = {
      attempt_id: req.attempt_id,
      issue_ref: `${req.issue.repository}#${req.issue.number}`,
      issue_element: triageIssueElement(req),
      labels: labelsBlock(req),
      context_dir: paths.context,
      memory_dir: join(paths.context, 'memory'),
      result_schema: JSON.stringify(schema, null, 2),
    };
    const prompt = render(loadTemplate('triage', paths.context).text, vars);
    const guard: GuardContext = { targetDir: realish(paths.context), protectedGlobs: [], homeDir: paths.home, readRoots: [realish(paths.context)] };
    const query = await deps.makeQuery({
      ...vars,
      // the fake's answer names a label the repository has, so the plumbing applies one
      fake_label: req.labels.find((l) => !['today', 'not today'].includes(l.name.toLowerCase()))?.name ?? 'none',
    });
    const agent: AgentRunResult<AgentTriage> = await runAgent<AgentTriage>({
      phase: 'plan',
      prompt,
      cwd: paths.context,
      env: buildAgentEnv(env, paths, guardEnvFor('plan', guard)),
      additionalDirectories: [paths.context].filter((d) => existsSync(d)),
      maxTurns: req.budget.max_turns,
      maxBudgetUsd: req.budget.max_cost_usd,
      outputSchema: schema,
      validate: (v) => validateWith(schema, v),
      guard,
      deadline: deps.deadline,
      abort: deps.abort,
      query,
      model: env.model,
      claudeExecutable: env.claudeExecutable,
    });
    result.cost_usd = agent.costUsd;
    result.session_id = agent.sessionId;
    result.turns = agent.numTurns;
    result.denied_tool_calls = agent.denials;
    if (!agent.ok || !agent.output) {
      result.outcome = (agent.errorKind && STOP[agent.errorKind]) || 'failed';
      result.error = agent.error;
      return result;
    }
    const o = agent.output;
    const labels = knownLabels(o.labels, req);
    if (labels.length < o.labels.length) log.warn(`${o.labels.length - labels.length} of ${o.labels.length} labels are not the repository's and are left out`);
    return {
      ...result,
      outcome: o.outcome,
      labels,
      summary: o.summary,
      analysis: o.analysis,
      steps: o.steps,
      questions: o.questions,
      error: o.outcome === 'failed' ? o.summary || 'the agent reported failed' : null,
    };
  } catch (e) {
    log.error(`triage failed: ${(e as Error).stack ?? String(e)}`);
    result.error = (e as Error).message;
    return result;
  }
}
