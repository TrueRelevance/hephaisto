import { existsSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { type AgentRunResult, buildAgentEnv, guardEnvFor, runAgent } from './agent.js';
import type { RunnerEnv, WorkPaths } from './config.js';
import { Git, clone, driverGitEnv } from './git.js';
import { type GuardContext, realish } from './guard.js';
import { type PrepareHandoff, newPrepareHandoff } from './handoff.js';
import { billingOf } from './investigate.js';
import { log } from './log.js';
import { loadTemplate, render, xmlEscape } from './prompts.js';
import { minimalFailed } from './result.js';
import {
  IssueReferenceRe,
  type ReviewClone,
  type ReviewIssue,
  type ReviewOutcome,
  type ReviewRequest,
  type ReviewResult,
  type Verdict,
  rawSchema,
  validateWith,
} from './schemas.js';
import type { QueryFn } from './sdk.js';
import { makeDirs, prefetchHistoryBlobs, prepareContext, sanitizeTarget } from './workspace.js';

// The review phase (#312): once a week Hephaisto asks Claude about every open issue of the
// organisation, in batches, and a person works through the verdicts. This Job answers one batch:
// one verdict per issue - can be closed (and why), a real bug, still to build - with its evidence.
// It is read-only like triage and writes nothing anywhere; Hephaisto gathered the GitHub facts
// before it started, and every close or field change is a person's click afterwards.
//
//   prepare   clones dev-context and, blobless and read-only, each repository of the batch the
//             request offers (not an archived one), with the blobs of its recent history - the
//             only step that needs GITHUB_TOKEN
//   coder     runs the model read-only in /work/context, keeps one verdict per batch issue and
//             prints the result
//
//   /work/context               dev-context
//   /work/repos/<owner>/<repo>  each cloned repository at its default branch

export interface ReviewPrepareDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  abort: AbortController;
  startedAt: number;
}

export interface ReviewDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  deadline: number;
  abort: AbortController;
  makeQuery: (vars: Record<string, string>) => Promise<QueryFn>;
}

/** How many of a clone's newest commits have their blobs fetched up front: `git show` and `git log -p` reach that far. */
export const RECENT_COMMITS = 300;

/** The model's verdict: one of the three groups, never unsure - that one is the runner's. */
export type AgentVerdict = Omit<Verdict, 'group'> & { group: 'close' | 'bug' | 'feature' };
export interface AgentReview {
  outcome: 'reviewed' | 'failed';
  verdicts: AgentVerdict[];
}

export const refOf = (i: Pick<ReviewIssue, 'repository' | 'number'>): string => `${i.repository}#${i.number}`;

/**
 * What the model answers. Built from the result schema's verdict so a cap changed in dev-context
 * is a cap changed here; `group` without unsure, and no conditional members - the runner applies
 * the rule between group, reason and priority itself (keepBatch).
 */
export function reviewOutputSchema(): Record<string, unknown> {
  const raw = rawSchema('review') as { $defs: Record<string, Record<string, unknown>>; properties: Record<string, Record<string, unknown>> };
  const verdict = raw.$defs.verdict! as { properties: Record<string, Record<string, unknown>>; required: string[] };
  const p = verdict.properties;
  const ref = raw.$defs.reference! as { maxLength: number; description: string };
  // no lookahead for the model's validator: the runner holds every reference to the batch and IssueReferenceRe
  const refPattern = '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+#[1-9][0-9]*$';
  return {
    type: 'object',
    additionalProperties: false,
    required: ['outcome', 'verdicts'],
    properties: {
      outcome: {
        enum: ['reviewed', 'failed'],
        description: 'reviewed: you answer a verdict for the issues of the batch. failed: only when the batch makes no sense at all.',
      },
      verdicts: {
        type: 'array',
        maxItems: (raw.properties.verdicts as { maxItems: number }).maxItems,
        description: 'One per issue of the batch, each issue once.',
        items: {
          type: 'object',
          additionalProperties: false,
          required: [...verdict.required],
          properties: {
            issue: { type: 'string', maxLength: ref.maxLength, pattern: refPattern, description: 'owner/repo#number, exactly as the issue element names it.' },
            group: {
              enum: ['close', 'bug', 'feature'],
              description: 'close = can be closed; bug = a problem that still exists; feature = a feature or task worth doing.',
            },
            close_reason: { ...p.close_reason },
            related: { type: ['string', 'null'], maxLength: ref.maxLength, pattern: refPattern, description: p.related!.description },
            confidence: { ...p.confidence },
            summary: { ...p.summary },
            reason: { ...p.reason },
            evidence: { ...p.evidence },
            priority: { ...p.priority },
          },
        },
      },
    },
  };
}

// ---------------------------------------------------------------------------------------------
// What reaches the model

const attr = (name: string, value: string | number | boolean | null): string => (value === null ? '' : ` ${name}="${xmlEscape(String(value))}"`);

/**
 * One issue in ONE <untrusted-issue> element, every value escaped - the facts Hephaisto gathered
 * as much as what a person typed, since a title or a commit message travels inside them - so
 * nothing in it can close the element and go on as the prompt.
 */
export function reviewIssueElement(i: ReviewIssue): string {
  const lines: string[] = [
    `<untrusted-issue${attr('ref', refOf(i))}${attr('type', i.type)}${attr('author', i.author)}${attr('created', i.created_at)}${attr('updated', i.updated_at)}${attr('repository-archived', i.archived)}>`,
    `<title>${xmlEscape(i.title)}</title>`,
    `<labels>${i.labels.length ? xmlEscape(i.labels.join(', ')) : '(none)'}</labels>`,
  ];
  if (i.board === null) lines.push('<board>not on the board</board>');
  else {
    const fields = i.board.fields.map((f) => `<field${attr('name', f.name)}>${xmlEscape(f.value)}</field>`).join('');
    lines.push(`<board${attr('status', i.board.status)}${attr('priority', i.board.priority)}${attr('days-in-status', i.board.days_in_status)}>${fields}</board>`);
  }
  lines.push(`<body>${xmlEscape(i.body ?? '')}</body>`);
  lines.push(
    i.cross_references.length === 0
      ? '<cross-references>none</cross-references>'
      : [
          '<cross-references>',
          ...i.cross_references.map((c) => {
            const tag = c.kind === 'pull_request' ? 'pull-request' : 'issue';
            return `<${tag}${attr('ref', c.reference)}${attr('state', c.state)}${attr('referenced', c.referenced_at)}${attr('closed', c.closed_at)}>${xmlEscape(c.title)}</${tag}>`;
          }),
          '</cross-references>',
        ].join('\n'),
  );
  lines.push(
    i.commits.length === 0
      ? '<commits>none</commits>'
      : ['<commits>', ...i.commits.map((c) => `<commit${attr('sha', c.sha)}${attr('repository', c.repository)}${attr('at', c.at)}>${xmlEscape(c.message ?? '')}</commit>`), '</commits>'].join('\n'),
  );
  if (i.sub_issues.length > 0) {
    lines.push(
      [
        `<sub-issues${attr('closed', i.sub_issues.filter((s) => s.state === 'closed').length)}${attr('total', i.sub_issues.length)}>`,
        ...i.sub_issues.map((s) => `<sub-issue${attr('ref', s.reference)}${attr('state', s.state)}${attr('state-reason', s.state_reason)}>${xmlEscape(s.title)}</sub-issue>`),
        '</sub-issues>',
      ].join('\n'),
    );
  }
  lines.push(
    i.comments.length === 0
      ? `<comments${attr('total', i.comments_total)}></comments>`
      : [
          `<comments${attr('total', i.comments_total)}${attr('shown', i.comments.length)}>`,
          ...i.comments.map((c) => `<comment${attr('author', c.author)}${attr('at', c.created_at)}>${xmlEscape(c.body)}</comment>`),
          '</comments>',
        ].join('\n'),
  );
  lines.push('</untrusted-issue>');
  return lines.join('\n');
}

export function reviewIssuesBlock(req: ReviewRequest): string {
  return req.issues.map(reviewIssueElement).join('\n\n');
}

/** Where each repository of the batch is, or why it is not there. Names are checked ones; notes are the runner's. */
export function clonesBlock(clones: ReviewClone[], paths: WorkPaths): string {
  if (clones.length === 0) return '- (no repository of this batch could be offered: review from the facts alone)';
  return clones
    .map((c) =>
      c.cloned
        ? `- \`${c.repository}\`: \`${join(paths.repos, c.repository)}\`, its default branch at \`${c.head ?? '?'}\``
        : `- \`${c.repository}\`: not cloned - ${xmlEscape(c.note ?? 'no reason given')}`,
    )
    .join('\n');
}

// ---------------------------------------------------------------------------------------------
// What is kept of the answer

/** The verdict of an issue the model left out: a person decides. */
export function missingVerdict(issue: string): Verdict {
  return {
    issue,
    group: 'unsure',
    close_reason: null,
    related: null,
    confidence: 'low',
    summary: 'Claude gave no verdict for this issue.',
    reason: 'The answer did not name this issue, so a person decides.',
    evidence: [],
    priority: null,
  };
}

/**
 * Exactly one verdict per batch issue, in the batch's order: a verdict for an issue outside the
 * batch is dropped, a second one for the same issue too, and an issue without one is unsure with
 * low confidence. The owner's rule is applied, never guessed around: a close without a reason
 * becomes unsure and low, a priority on a close and a reason on a bug or a feature are dropped,
 * and a related reference that is not one is null.
 */
export function keepBatch(answered: AgentVerdict[], req: ReviewRequest): { verdicts: Verdict[]; dropped: number; missing: number } {
  const batch = new Map(req.issues.map((i) => [refOf(i).toLowerCase(), refOf(i)]));
  const byIssue = new Map<string, Verdict>();
  let dropped = 0;
  for (const v of answered) {
    const key = typeof v.issue === 'string' ? v.issue.trim().toLowerCase() : '';
    const issue = batch.get(key);
    if (!issue || byIssue.has(issue)) {
      dropped++;
      continue;
    }
    const related = typeof v.related === 'string' && IssueReferenceRe.test(v.related.trim()) ? v.related.trim() : null;
    const base = { ...v, issue, related, evidence: [...v.evidence] };
    if (v.group === 'close' && !v.close_reason) {
      byIssue.set(issue, { ...base, group: 'unsure', close_reason: null, confidence: 'low', priority: null });
    } else if (v.group === 'close') {
      byIssue.set(issue, { ...base, group: 'close', priority: null });
    } else {
      byIssue.set(issue, { ...base, group: v.group, close_reason: null });
    }
  }
  let missing = 0;
  const verdicts = req.issues.map((i) => {
    const found = byIssue.get(refOf(i));
    if (found) return found;
    missing++;
    return missingVerdict(refOf(i));
  });
  return { verdicts, dropped, missing };
}

// ---------------------------------------------------------------------------------------------
// The two roles

/**
 * The read-only posture: the plan phase's guard - no Edit or Write, no redirect into a file, no
 * git that changes anything or talks to a remote, no build - in dev-context as its directory.
 */
export function reviewGuard(paths: WorkPaths): GuardContext {
  return { targetDir: realish(paths.context), protectedGlobs: [], homeDir: paths.home, readRoots: [realish(paths.context), realish(paths.repos)] };
}

/** The repositories the batch's issues live in, each once, in the order the batch first names them. */
export function batchRepositories(req: ReviewRequest): string[] {
  const seen = new Map<string, string>();
  for (const i of req.issues) if (!seen.has(i.repository.toLowerCase())) seen.set(i.repository.toLowerCase(), i.repository);
  return [...seen.values()];
}

function firstLine(e: unknown): string {
  return Array.from(((e as Error).message ?? String(e)).split('\n')[0]!).slice(0, 300).join('');
}

/** One repository, blobless at its default branch, its own Claude settings removed, its recent history's blobs fetched. Throws when it cannot be cloned. */
async function cloneReadOnly(
  offered: ReviewRequest['repositories'][number],
  filter: 'blob:none' | 'none',
  env: RunnerEnv,
  paths: WorkPaths,
  signal: AbortSignal,
): Promise<string> {
  const [owner, name] = offered.repository.split('/') as [string, string];
  const dir = join(paths.repos, owner, name);
  rmSync(dir, { recursive: true, force: true });
  const genv = driverGitEnv(env, paths.home);
  try {
    await clone(offered.url, dir, genv, { branch: offered.default_branch, singleBranch: true, filter, signal });
    const git = new Git(dir, genv, signal);
    await sanitizeTarget({ repo: { name, url: offered.url, defaultBranch: offered.default_branch, stack: 'other', coderEnabled: false, workloads: [], commands: {}, verification: { hasUnitTests: false } }, dir, git, notes: [], claudeMd: null, caitDir: null });
    if (filter === 'blob:none') {
      try {
        const n = await prefetchHistoryBlobs(git, ['-n', String(RECENT_COMMITS)]);
        log.info(`${offered.repository}: fetched the ${n} blobs of its last ${RECENT_COMMITS} commits in one request`);
      } catch (e) {
        // not fatal: the tree and the log are there; only older file contents are not
        log.warn(`${offered.repository}: the recent history's blobs could not be fetched (${firstLine(e)}); git show of a commit may fail`);
      }
    }
    return await git.head();
  } catch (e) {
    rmSync(dir, { recursive: true, force: true });
    throw e;
  }
}

export async function prepareReview(req: ReviewRequest, deps: ReviewPrepareDeps): Promise<PrepareHandoff> {
  const { env, paths } = deps;
  const h = newPrepareHandoff(req.attempt_id, 'review', deps.startedAt);
  h.clones = [];
  try {
    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    h.context_sha = ctx.contextSha;
    for (const repository of batchRepositories(req)) {
      const offered = req.repositories.find((r) => r.repository.toLowerCase() === repository.toLowerCase());
      const clone: ReviewClone = { repository, cloned: false, head: null, note: null };
      h.clones.push(clone);
      if (!offered) {
        clone.note = 'not offered for cloning: its issues are reviewed from their facts';
      } else if (offered.archived || req.issues.some((i) => i.repository.toLowerCase() === repository.toLowerCase() && i.archived)) {
        clone.note = 'archived: not cloned';
      } else {
        try {
          clone.head = await cloneReadOnly(offered, ctx.repos.defaults.clone.filter, env, paths, deps.abort.signal);
          clone.cloned = true;
          log.info(`${repository} cloned read-only at ${clone.head}`);
        } catch (e) {
          if (deps.abort.signal.aborted) throw e;
          clone.note = `could not be cloned: ${firstLine(e)}`;
          log.warn(`${repository} ${clone.note}; its issues are reviewed from their facts`);
        }
      }
    }
    return h;
  } catch (e) {
    log.error(`review could not be prepared: ${(e as Error).stack ?? String(e)}`);
    return { ...h, terminal: { ...minimalFailed('review', req.attempt_id, (e as Error).message, 'api'), context_sha: h.context_sha, clones: h.clones ?? [] } };
  }
}

const STOP: Record<string, ReviewOutcome> = { max_turns: 'max_turns', budget: 'budget_exhausted', rate_limited: 'rate_limited' };

export async function runReview(req: ReviewRequest, deps: ReviewDeps, h: PrepareHandoff): Promise<ReviewResult> {
  const { env, paths } = deps;
  const result = minimalFailed('review', req.attempt_id, 'review did not complete', billingOf(env));
  result.context_sha = h.context_sha;
  // believed from prepare, and only where the checkout is still there
  result.clones = (h.clones ?? []).map((c) =>
    c.cloned && !existsSync(join(paths.repos, c.repository, '.git')) ? { ...c, cloned: false, head: null, note: 'the prepare role left no checkout' } : c,
  );
  try {
    makeDirs(paths);
    const schema = reviewOutputSchema();
    const vars = {
      attempt_id: req.attempt_id,
      issue_count: String(req.issues.length),
      issues_element: reviewIssuesBlock(req),
      clones: clonesBlock(result.clones, paths),
      today: new Date().toISOString().slice(0, 10),
      context_dir: paths.context,
      repos_dir: paths.repos,
      memory_dir: join(paths.context, 'memory'),
      result_schema: JSON.stringify(schema, null, 2),
    };
    const prompt = render(loadTemplate('review', paths.context).text, vars);
    const guard = reviewGuard(paths);
    const query = await deps.makeQuery(vars);
    const agent: AgentRunResult<AgentReview> = await runAgent<AgentReview>({
      phase: 'plan',
      prompt,
      cwd: paths.context,
      env: buildAgentEnv(env, paths, guardEnvFor('plan', guard)),
      additionalDirectories: [paths.context, paths.repos].filter((d) => existsSync(d)),
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
    if (agent.output.outcome === 'failed') {
      result.error = 'the agent answered failed: it could not review this batch';
      return result;
    }
    const kept = keepBatch(agent.output.verdicts, req);
    if (kept.dropped > 0) log.warn(`${kept.dropped} verdict(s) named an issue outside the batch or one already answered, and are left out`);
    if (kept.missing > 0) log.warn(`${kept.missing} of ${req.issues.length} issues got no verdict and are unsure with low confidence`);
    return { ...result, outcome: 'reviewed', verdicts: kept.verdicts, error: null };
  } catch (e) {
    log.error(`review failed: ${(e as Error).stack ?? String(e)}`);
    result.error = (e as Error).message;
    return result;
  }
}
