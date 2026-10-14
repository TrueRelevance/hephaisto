import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { APP_ROOT } from './config.js';
import type { IncidentRequest, WorkItemRequest } from './schemas.js';

// Templates are data: dev-context/prompts/*.md wins when present, so prompt iteration is a
// dev-context commit (live for the next attempt), and coder/prompts/*.md is the floor that
// ships in the image. Rendering is `{{name}}` substitution and nothing else - no conditionals,
// no includes - so what a template can do is exactly what the driver hands it.

export type TemplateName =
  | 'plan'
  | 'implement'
  | 'evidence-block'
  | 'pr-body'
  | 'investigate'
  // for a work item - a GitHub issue - instead of an incident (v0.14.0)
  | 'plan-issue'
  | 'implement-issue'
  | 'issue-block'
  | 'pr-body-issue'
  // for a work item that is planned again: the earlier plan, and how to read the answers
  | 'replan-block'
  // for the planner: label and analyse one GitHub issue (v0.15.0)
  | 'triage';

/** investigate.md, the section appended to Hephaisto's investigation prompt. */
export const INVESTIGATE_VARS = ['context_dir', 'memory_dir', 'source_block'] as const;

export const PROMPT_VARS = [
  'attempt_id', 'incident_id', 'phase', 'repo_url', 'repo_name', 'default_branch', 'branch', 'analysed_ref', 'image',
  'incident_title', 'incident_kind', 'incident_severity', 'workload', 'escalation_reason', 'evidence_block',
  'investigation_summary', 'plan_json', 'plan_steps', 'plan_files', 'main_moved', 'commands', 'verification_level',
  'repo_notes', 'result_schema', 'cait_ref', 'memory_dir', 'workspace', 'trailers',
] as const;

/** plan-issue.md and implement-issue.md. No image, no workload, no incident - and nothing of the issue's text but `issue_block`. */
export const ISSUE_PROMPT_VARS = [
  'attempt_id', 'issue_ref', 'phase', 'repo_url', 'repo_name', 'default_branch', 'branch', 'analysed_ref', 'issue_block', 'replan_block',
  'plan_json', 'plan_steps', 'plan_files', 'main_moved', 'commands', 'verification_level', 'repo_notes', 'result_schema',
  'cait_ref', 'memory_dir', 'workspace', 'trailers',
] as const;

export const PR_BODY_VARS = [
  'incident_link', 'summary', 'root_cause', 'evidence_md', 'files', 'deviations', 'verification_table',
  'verification_weak', 'notes', 'cost', 'versions', 'attempt_id', 'incident_id', 'workload', 'image', 'analysed_ref',
  'branch', 'repo_url', 'change_summary', 'approved_by', 'opened_as',
] as const;

/** pr-body-issue.md. `issue_md` is the issue's title in a fence; nothing else of the issue is here. */
export const ISSUE_PR_BODY_VARS = [
  'issue_ref', 'issue_url', 'issue_md', 'summary', 'root_cause', 'files', 'deviations', 'verification_table', 'verification_weak',
  'notes', 'cost', 'versions', 'attempt_id', 'default_branch', 'analysed_ref', 'branch', 'repo_url', 'change_summary', 'opened_as',
] as const;

export function loadTemplate(name: TemplateName, contextDir: string | null): { text: string; source: string } {
  let p = join(APP_ROOT, 'prompts', `${name}.md`);
  if (contextDir && existsSync(join(contextDir, 'prompts', `${name}.md`))) p = join(contextDir, 'prompts', `${name}.md`);
  // <!-- ... --> comments are notes for template authors, not for the model or the PR reader
  const text = readFileSync(p, 'utf8').replace(/<!--[\s\S]*?-->\s*/g, '');
  return { text, source: p };
}

/** `{{ name }}` → value; unknown or missing names render empty. Single pass, so a value can never introduce a placeholder. */
export function render(template: string, vars: Record<string, string | undefined>): string {
  return template.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (_, k: string) => vars[k] ?? '');
}

export function xmlEscape(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * Every untrusted string in the request - the investigation summary, each finding's hypothesis
 * and each evidence excerpt - goes into ONE <untrusted-evidence> element, escaped, so no excerpt
 * can close the element and start talking as the prompt.
 */
export function buildEvidenceElement(req: IncidentRequest): string {
  const lines: string[] = ['<untrusted-evidence>'];
  if (req.investigation_summary) lines.push(`<investigation-summary>${xmlEscape(req.investigation_summary)}</investigation-summary>`);
  const ordered = [...req.findings].sort((a, b) => Number(b.primary) - Number(a.primary));
  for (const f of ordered) {
    lines.push(
      `<finding id="${xmlEscape(f.id)}" primary="${f.primary}" category="${xmlEscape(f.category)}" confidence="${f.confidence}">`,
    );
    lines.push(`<hypothesis>${xmlEscape(f.hypothesis)}</hypothesis>`);
    for (const e of f.evidence) {
      lines.push(`<excerpt step="${xmlEscape(e.step_id)}" tool="${xmlEscape(e.tool)}">${xmlEscape(e.excerpt)}</excerpt>`);
    }
    lines.push('</finding>');
  }
  lines.push('</untrusted-evidence>');
  return lines.join('\n');
}

/**
 * The `evidence_block` variable: evidence-block.md is a preamble with no placeholders, placed
 * directly before the escaped element. A template that does carry `{{evidence_element}}` gets
 * it there instead; either way the element appears exactly once and is never dropped.
 */
export function renderEvidenceBlock(req: IncidentRequest, contextDir: string | null): string {
  return behindPreamble('evidence-block', 'untrusted-evidence', 'evidence_element', buildEvidenceElement(req), contextDir);
}

/**
 * Every string of an issue that somebody typed - its title, who opened it, its body, each comment
 * that was passed on - in ONE <untrusted-issue> element, escaped, so that nothing in an issue
 * can close the element and go on as the prompt. The issue's type is an attribute, escaped too:
 * a name an administrator chose is still a name.
 */
export function buildIssueElement(req: WorkItemRequest): string {
  const w = req.work_item;
  const lines: string[] = [`<untrusted-issue type="${xmlEscape(w.type ?? '')}">`];
  lines.push(`<title>${xmlEscape(w.title)}</title>`);
  lines.push(`<opened-by>${xmlEscape(w.author)}</opened-by>`);
  lines.push(`<body>${xmlEscape(w.body)}</body>`);
  for (const c of w.comments) lines.push(`<comment by="${xmlEscape(c.author)}">${xmlEscape(c.body)}</comment>`);
  lines.push('</untrusted-issue>');
  return lines.join('\n');
}

/** The `issue_block` variable: issue-block.md, the preamble, directly before the escaped element. */
export function renderIssueBlock(req: WorkItemRequest, contextDir: string | null): string {
  return behindPreamble('issue-block', 'untrusted-issue', 'issue_element', buildIssueElement(req), contextDir);
}

/**
 * What an earlier attempt for the same work item planned and asked, in ONE <earlier-plan>
 * element, escaped like the issue: a model wrote it about text a stranger wrote, and a summary
 * that quoted the issue must not be able to close the element. Questions and steps are numbered
 * as the issue shows them, so that "to 2: yes" in a comment names the same question here.
 */
export function buildEarlierPlanElement(previous: NonNullable<WorkItemRequest['previous']>): string {
  const lines: string[] = ['<earlier-plan>'];
  lines.push(`<summary>${xmlEscape(previous.summary)}</summary>`);
  previous.questions.forEach((q, i) => lines.push(`<question n="${i + 1}">${xmlEscape(q)}</question>`));
  previous.steps.forEach((s, i) => lines.push(`<step n="${i + 1}">${xmlEscape(s)}</step>`));
  lines.push('</earlier-plan>');
  return lines.join('\n');
}

/**
 * The `replan_block` variable: replan-block.md and the earlier plan behind it when the request
 * carries `previous`, and nothing at all for a first plan - whose prompt is then, apart from one
 * empty line, the prompt it was before a work item could be planned twice.
 */
export function renderReplanBlock(req: WorkItemRequest, contextDir: string | null): string {
  if (!req.previous) return '';
  return behindPreamble('replan-block', 'earlier-plan', 'earlier_plan_element', buildEarlierPlanElement(req.previous), contextDir);
}

/**
 * A preamble template with no element of its own, then the element: a template that carries the
 * placeholder gets the element there, any other gets it appended - exactly once either way, and
 * a tag of the same name in the template's own text is taken out, so the model sees one.
 */
function behindPreamble(template: TemplateName, tag: string, placeholder: string, element: string, contextDir: string | null): string {
  const { text } = loadTemplate(template, contextDir);
  const preamble = text.replace(new RegExp(`</?${tag}[^>]*>`, 'g'), '');
  const rendered = render(preamble, { [placeholder]: element });
  return rendered.includes(element) ? rendered.trim() : `${rendered.trim()}\n\n${element}`;
}

/**
 * Text a model wrote, for a pull request - one that closes an issue anybody could have opened,
 * and one for an incident alike (pr.ts: an incident's description and title were not treated
 * until v0.14.0, and a model that repeats "fixes #12" from a log line closes issue 12 on merge
 * whatever the pull request is for). It stays markdown - a root cause names `path:line` in code
 * spans - but a zero-width space sits wherever GitHub would otherwise act on it, so that it
 * notifies nobody and, in a body that GitHub reads for closing keywords, closes nothing: the
 * one `Closes` in an issue's description is the runner's own line, and is not passed through
 * this.
 *
 * Where, and each of them asked of github.com (scripts/e2e/github-live.sh, L01 - the first
 * three were assumed for a stage and the last was missing; a model that repeated "resolves
 * https://github.com/o/r/issues/3" closed issue 3):
 *
 *   @name            after the @: a mention, and a notification
 *   #12, GH-12       after the # or the hyphen: a reference, and with "fixes" before it a close
 *   https://, www.   inside the scheme and after www: no address is a link
 *   /12              after every slash before a digit. GitHub reads `/issues/12`, `/pull/12`
 *                    and `/discussions/12` as references BY THEMSELVES - no scheme, no host,
 *                    in any case of letters - and `owner/repo/issues/12` as one in another
 *                    repository. Breaking the scheme alone leaves all of them standing.
 *
 * The agent's counterpart for a comment on an issue is IssueComments.Neutralise.
 */
export function inert(text: string): string {
  return text
    .replace(/\u200b/g, '')
    .replace(/@(?=[A-Za-z0-9_])/g, '@\u200b')
    .replace(/(#|\bGH-)(?=\d)/gi, '$1\u200b')
    .replace(/:\/\//g, ':\u200b//')
    .replace(/(\bwww)(?=\.)/gi, '$1\u200b')
    .replace(/\/(?=\d)/g, '/\u200b');
}

/** For a ```json fence: JSON with every backtick escaped (only possible inside strings), so no value can close the fence. */
export function fencedJson(value: unknown): string {
  return JSON.stringify(value, null, 2).replace(/`/g, '\\u0060');
}

/**
 * The target repo's CLAUDE.md. settingSources ['user'] means Claude Code does NOT load project
 * CLAUDE.md files (the SDK documents "Must include 'project' to load CLAUDE.md files"), so the
 * driver injects it itself. It is team-authored, so it is not in the untrusted block, but it is
 * delimited, and a closing tag inside it is neutralised so it cannot end its own element.
 */
export function repoNotesBlock(claudeMd: string | null): string {
  if (!claudeMd || !claudeMd.trim()) return '';
  const capped =
    claudeMd.length > REPO_NOTES_MAX
      ? `${claudeMd.slice(0, REPO_NOTES_MAX)}\n…(cut here: these are the first ${REPO_NOTES_MAX} of ${claudeMd.length} characters. The whole file is CLAUDE.md at the root of the repository you are in - read the rest there before you rely on what is above.)`
      : claudeMd;
  return `<repo-notes trust="team-authored">\n${capped.replace(/<\/repo-notes/gi, '&lt;/repo-notes')}\n</repo-notes>\n\n${NESTED_NOTES}`;
}

/**
 * How much of a CLAUDE.md is pasted into the prompt. It was 24,000, a number nobody had written a
 * reason for, set when the longest file of any service was 11,000 characters; CaitMatchingService's
 * is 34,000 today, and the cut took its notes on testing. This is several times the longest real
 * file and well under the 400,000 the hand-off between the containers carries, so a cut is an
 * accident again - and when it happens the model is told, and where the rest is.
 */
export const REPO_NOTES_MAX = 120_000;

/**
 * Claude Code would read a CLAUDE.md in a directory the model works in. Here nothing does: the
 * project is not a settings source, on purpose. So the model is told to look.
 */
const NESTED_NOTES =
  'A directory inside the repository may have a CLAUDE.md of its own. Nothing loads those for you: read the one in a directory before you change or judge files there.';

/** Verbatim evidence for the PR body: fenced so nothing in a log line renders as markdown or pings anyone. */
export function evidenceMarkdown(req: IncidentRequest): string {
  const out: string[] = [];
  const primary = req.findings.filter((f) => f.primary);
  for (const f of primary.length > 0 ? primary : req.findings.slice(0, 1)) {
    out.push(`**Hypothesis** (${f.category}, confidence ${f.confidence}):`);
    out.push(fence(f.hypothesis));
    for (const e of f.evidence.slice(0, 5)) {
      out.push(`\`${e.tool}\`:`);
      out.push(fence(e.excerpt));
    }
  }
  return out.join('\n\n');
}

export function fence(text: string): string {
  const longest = Math.max(2, ...Array.from(text.matchAll(/`+/g), (m) => m[0].length));
  const f = '`'.repeat(longest + 1);
  return `${f}text\n${text}\n${f}`;
}
