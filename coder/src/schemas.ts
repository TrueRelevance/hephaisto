import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { Ajv2020, type ErrorObject, type ValidateFunction } from 'ajv/dist/2020.js';
import addFormatsModule from 'ajv-formats';
import { z } from 'zod';
import { APP_ROOT } from './config.js';

// The vendored contract (coder/contracts, byte-identical to dev-context/schemas and pinned by
// SCHEMAS.lock) is the AUTHORITY at runtime: every request is validated against it before
// anything runs, and every result before it is written. The zod mirrors below exist for the
// TypeScript types and for contracts.test.ts, which fails if they drift from the JSON.

export const CONTRACTS_DIR = join(APP_ROOT, 'contracts');

export const SCHEMA_FILES = {
  request: 'codefix-request.schema.json',
  requestV2: 'codefix-request-v2.schema.json',
  plan: 'codefix-plan-result.schema.json',
  implement: 'codefix-implement-result.schema.json',
  repos: 'repos.schema.json',
  investigateRequest: 'investigate-request.schema.json',
  investigate: 'investigate-result.schema.json',
  triageRequest: 'triage-request.schema.json',
  triage: 'triage-result.schema.json',
} as const;
export type SchemaName = keyof typeof SCHEMA_FILES;

type JsonSchema = Record<string, unknown>;

const raw: Record<SchemaName, JsonSchema> = Object.fromEntries(
  Object.entries(SCHEMA_FILES).map(([k, f]) => [k, JSON.parse(readFileSync(join(CONTRACTS_DIR, f), 'utf8')) as JsonSchema]),
) as Record<SchemaName, JsonSchema>;

export function rawSchema(name: SchemaName): JsonSchema {
  return raw[name];
}

// ajv-formats is CJS with a default export; under NodeNext the callable is on .default.
const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (a: Ajv2020) => Ajv2020;

const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
for (const s of Object.values(raw)) ajv.addSchema(s);

function compiled(name: SchemaName): ValidateFunction {
  const id = raw[name].$id as string;
  const fn = ajv.getSchema(id);
  if (!fn) throw new Error(`schema ${id} did not compile`);
  return fn;
}

const validators: Record<SchemaName, ValidateFunction> = {
  request: compiled('request'),
  requestV2: compiled('requestV2'),
  plan: compiled('plan'),
  implement: compiled('implement'),
  repos: compiled('repos'),
  investigateRequest: compiled('investigateRequest'),
  investigate: compiled('investigate'),
  triageRequest: compiled('triageRequest'),
  triage: compiled('triage'),
};

export interface Validation {
  ok: boolean;
  errors: string[];
}

export function formatErrors(errors: ErrorObject[] | null | undefined): string[] {
  return (errors ?? []).map((e) => `${e.instancePath || '/'} ${e.message ?? 'invalid'}${e.params && 'additionalProperty' in e.params ? ` (${String(e.params.additionalProperty)})` : ''}`);
}

export function validate(name: SchemaName, value: unknown): Validation {
  const fn = validators[name];
  const ok = fn(value) as boolean;
  return { ok, errors: ok ? [] : formatErrors(fn.errors) };
}

/** Validates against an ad-hoc schema (the agent's structured-output schema). */
export function validateWith(schema: JsonSchema, value: unknown): Validation {
  const fn = ajv.compile(schema);
  const ok = fn(value) as boolean;
  return { ok, errors: ok ? [] : formatErrors(fn.errors) };
}

// ---------------------------------------------------------------------------------------------
// zod mirrors (types + drift test)

export const DenialZ = z
  .object({ tool: z.string().max(64), input: z.string().max(500), reason: z.string().max(500) })
  .strict();

export const VerificationLevelZ = z.enum(['tests', 'build-only', 'typecheck-only', 'none']);

export const PlanResultZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    phase: z.literal('plan'),
    outcome: z.enum(['planned', 'not_a_code_problem', 'insufficient_context', 'failed']),
    summary: z.string().max(2000),
    root_cause: z.string().max(4000),
    confidence: z.number().min(0).max(1),
    files: z.array(z.string().max(512)).max(50),
    steps: z.array(z.string().max(2000)).max(20),
    verification: z
      .object({ level: VerificationLevelZ, not_verifiable: z.array(z.string().max(1000)).max(20) })
      .strict(),
    needs_cait: z.boolean(),
    notes: z.array(z.string().max(2000)).max(20),
    /** What only a person can decide, each with what the plan assumed meanwhile. Optional: absent means none. */
    questions: z.array(z.string().max(600)).max(10).optional(),
    analysed_ref: z.string().max(64).nullable(),
    context_sha: z.string().max(64).nullable(),
    cost_usd: z.number().min(0),
    session_id: z.string().max(128).nullable(),
    error: z.string().max(4000).nullable(),
    denied_tool_calls: z.array(DenialZ).max(50),
  })
  .strict();

export const ImplementResultZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    phase: z.literal('implement'),
    outcome: z.enum(['pr_opened', 'already_exists', 'no_changes', 'build_failed', 'tests_failed', 'policy_diff', 'failed']),
    branch: z.string().max(255).nullable(),
    pr_url: z.string().max(512).nullable(),
    pr_number: z.number().int().min(1).nullable(),
    base_commit: z.string().max(64).nullable(),
    files: z.array(z.string().max(512)).max(100),
    build_passed: z.boolean(),
    tests_passed: z.boolean(),
    log_tail: z.string().max(8192),
    deviations: z.array(z.string().max(2000)).max(20),
    cost_usd: z.number().min(0),
    session_id: z.string().max(128).nullable(),
    error: z.string().max(4000).nullable(),
    denied_tool_calls: z.array(DenialZ).max(50),
  })
  .strict();

const BudgetZ = z
  .object({ max_cost_usd: z.number().min(0).max(1000), deadline_seconds: z.number().int().min(60).max(86400) })
  .strict();

const RepositoryZ = z
  .object({
    url: z.string().max(512).regex(/^(https:\/\/|http:\/\/|file:\/\/)[^\s]+$/),
    default_branch: z.string().min(1).max(255),
    path: z.string().max(512),
    branch: z.string().regex(/^hephaisto\/codefix-[0-9a-f]{12}$/),
  })
  .strict();

const ContextZ = z.object({ repository_url: z.string().min(1).max(512), ref: z.string().min(1).max(255) }).strict();

/** Version 1: the request for an incident. Unchanged since v0.9.0. */
export const RequestZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    incident_id: z.guid(),
    phase: z.enum(['plan', 'implement']),
    budget: BudgetZ,
    repository: RepositoryZ,
    context: ContextZ,
    incident: z
      .object({
        title: z.string().max(512),
        kind: z.string().max(64),
        severity: z.string().max(32),
        target: z
          .object({
            namespace: z.string().max(253),
            kind: z.string().max(64),
            name: z.string().max(253),
            workload: z.string().max(600),
          })
          .strict(),
        image: z.string().max(1024).nullable(),
        rollout_revision: z.string().max(64).nullable(),
        escalation_reason: z.string().max(64),
      })
      .strict(),
    findings: z
      .array(
        z
          .object({
            id: z.guid(),
            primary: z.boolean(),
            category: z.string().max(64),
            confidence: z.number().min(0).max(1),
            hypothesis: z.string().max(4000),
            evidence: z
              .array(z.object({ step_id: z.guid(), tool: z.string().max(128), excerpt: z.string().max(2048) }).strict())
              .max(20),
          })
          .strict(),
      )
      .max(10),
    investigation_summary: z.string().max(8000).nullable(),
    plan: PlanResultZ.nullable(),
  })
  .strict();

/**
 * Version 2 (v0.14.0): the request for a piece of work somebody handed over - a GitHub issue
 * assigned to Hephaisto's account. `work_item` in place of the incident, the findings and the
 * investigation summary, which are absent. Its title, author, body and comments are untrusted.
 */
export const WorkItemRequestZ = z
  .object({
    contract_version: z.literal('2'),
    attempt_id: z.guid(),
    phase: z.enum(['plan', 'implement']),
    budget: BudgetZ,
    repository: RepositoryZ,
    context: ContextZ,
    work_item: z
      .object({
        source: z.enum(['github']),
        repository: z.string().max(200).regex(/^[A-Za-z0-9][A-Za-z0-9-]*\/[A-Za-z0-9._-]+$/),
        number: z.number().int().min(1),
        url: z.string().max(512),
        title: z.string().max(512),
        type: z.string().max(64).nullable(),
        author: z.string().max(64),
        body: z.string().max(65536),
        comments: z.array(z.object({ author: z.string().max(64), body: z.string().max(65536) }).strict()).max(50),
      })
      .strict(),
    /**
     * The earlier attempt's plan, when the work is planned AGAIN (an approver's /replan, or a
     * fresh assignment after an attempt ended); absent for the first plan of a hand-over. A
     * model's text about untrusted text - data, like the issue.
     */
    previous: z
      .object({
        summary: z.string().max(2000),
        questions: z.array(z.string().max(600)).max(10),
        steps: z.array(z.string().max(2000)).max(20),
      })
      .strict()
      .optional(),
    plan: PlanResultZ.nullable(),
  })
  .strict();

const WorkloadZ = z
  .object({
    namespace: z.string(),
    kind: z.string(),
    name: z.string(),
    imageRepo: z.string().optional(),
    helmChart: z.string().optional(),
    aliases: z.array(z.string()).optional(),
  })
  .strict();

export const RepoEntryZ = z
  .object({
    name: z.string(),
    url: z.string().regex(/^(https:\/\/|file:\/\/|\/)/),
    defaultBranch: z.string(),
    stack: z.enum(['dotnet', 'nuxt', 'python', 'other']),
    projectFile: z.string().optional(),
    path: z.string().optional(),
    owner: z.string().optional(),
    coderEnabled: z.boolean(),
    workloads: z.array(WorkloadZ),
    commands: z
      .object({ restore: z.string().optional(), build: z.string().optional(), test: z.string().optional(), typecheck: z.string().optional() })
      .strict(),
    verification: z.object({ hasUnitTests: z.boolean(), note: z.string().optional() }).strict(),
    cait: z.object({ pinned: z.boolean().optional(), sibling: z.enum(['never', 'if-incident-targets-cait']).optional() }).strict().optional(),
    protectedPaths: z.array(z.string()).optional(),
    timeouts: z
      .object({
        restoreSeconds: z.number().int().min(1).optional(),
        buildSeconds: z.number().int().min(1).optional(),
        testSeconds: z.number().int().min(1).optional(),
      })
      .strict()
      .optional(),
  })
  .strict();

/**
 * `defaults.pr.draft` (#297). `true`: every pull request is opened as a draft - the only value
 * until v0.14.0, and what an install gets that changes nothing. `unless-ready`: a draft only
 * when something speaks against a review (pr.ts, draftReasons).
 */
export const DraftModeZ = z.literal([true, 'unless-ready']);
export type DraftMode = z.infer<typeof DraftModeZ>;

export const ReposZ = z
  .object({
    defaults: z
      .object({
        pr: z
          .object({ assignee: z.string(), labels: z.array(z.string()), branchPrefix: z.literal('hephaisto/'), draft: DraftModeZ })
          .strict(),
        clone: z.object({ filter: z.enum(['blob:none', 'none']) }).strict(),
        imageTagIsCommitSha: z.boolean(),
        protectedPaths: z.array(z.string()),
      })
      .strict(),
    repos: z.array(RepoEntryZ),
  })
  .strict();

// ---- investigate (v0.12.0): the same runner, a Job that investigates through Hephaisto's MCP endpoint

const TargetZ = z
  .object({ namespace: z.string().max(253), kind: z.string().max(64), name: z.string().max(253), workload: z.string().max(600) })
  .strict();

export const InvestigateSourceZ = z
  .object({
    url: z.string().max(512).regex(/^(https:\/\/|http:\/\/|file:\/\/)[^\s]+$/),
    default_branch: z.string().min(1).max(255),
    path: z.string().max(512),
    ref: z.string().max(64).nullable(),
    image: z.string().max(1024).nullable(),
  })
  .strict();

export const InvestigateRequestZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    incident_id: z.guid(),
    investigation_id: z.guid(),
    phase: z.literal('investigate'),
    budget: z
      .object({
        max_cost_usd: z.number().min(0).max(1000),
        deadline_seconds: z.number().int().min(60).max(86400),
        max_turns: z.number().int().min(1).max(500),
      })
      .strict(),
    context: z.object({ repository_url: z.string().min(1).max(512), ref: z.string().min(1).max(255) }).strict(),
    endpoint: z.object({ url: z.string().max(512).regex(/^https?:\/\/[^\s]+$/), token: z.string().min(32).max(256) }).strict(),
    incident: z.object({ title: z.string().max(512), kind: z.string().max(64), severity: z.string().max(32), target: TargetZ }).strict(),
    system_prompt: z.string().max(200000),
    opening_message: z.string().max(8000),
    source: InvestigateSourceZ.nullable(),
  })
  .strict();

export const CodeRefZ = z
  .object({
    finding: z.number().int().min(0).max(9),
    path: z.string().max(512),
    line: z.number().int().min(1),
    end_line: z.number().int().min(1).nullable(),
    note: z.string().max(500).nullable(),
  })
  .strict();

export const InvestigateOutcomeZ = z.enum(['concluded', 'no_conclusion', 'budget_exhausted', 'max_turns', 'rate_limited', 'no_credential', 'failed']);
export const BillingZ = z.enum(['subscription', 'api', 'fake']);

export const InvestigateResultZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    phase: z.literal('investigate'),
    outcome: InvestigateOutcomeZ,
    cost_usd: z.number().min(0),
    billing: BillingZ,
    input_tokens: z.number().int().min(0),
    output_tokens: z.number().int().min(0),
    turns: z.number().int().min(0),
    model: z.string().max(128).nullable(),
    session_id: z.string().max(128).nullable(),
    context_sha: z.string().max(64).nullable(),
    source: z.object({ cloned: z.boolean(), analysed_ref: z.string().max(64).nullable(), error: z.string().max(1000).nullable() }).strict().nullable(),
    code_refs: z.array(CodeRefZ).max(20),
    error: z.string().max(4000).nullable(),
    denied_tool_calls: z.array(DenialZ).max(50),
  })
  .strict();

// ---- triage (v0.15.0): the same runner, a Job that labels and analyses one GitHub issue for the planner

export const TriageRequestZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    phase: z.literal('triage'),
    budget: z
      .object({
        max_cost_usd: z.number().min(0).max(1000),
        deadline_seconds: z.number().int().min(60).max(86400),
        max_turns: z.number().int().min(1).max(500),
      })
      .strict(),
    context: z.object({ repository_url: z.string().min(1).max(512), ref: z.string().min(1).max(255) }).strict(),
    issue: z
      .object({
        repository: z.string().max(200).regex(/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/),
        number: z.number().int().min(1),
        url: z.string().min(1).max(512),
        title: z.string().min(1).max(512),
        type: z.string().max(100).nullable(),
        author: z.string().min(1).max(100),
        body: z.string().max(20000).nullable(),
      })
      .strict(),
    labels: z.array(z.object({ name: z.string().min(1).max(100), description: z.string().max(500).nullable() }).strict()).max(200),
  })
  .strict();

export const TriageOutcomeZ = z.enum(['triaged', 'budget_exhausted', 'max_turns', 'rate_limited', 'failed']);

export const TriageResultZ = z
  .object({
    contract_version: z.literal('1'),
    attempt_id: z.guid(),
    phase: z.literal('triage'),
    outcome: TriageOutcomeZ,
    labels: z.array(z.string().min(1).max(100)).max(10),
    summary: z.string().max(500),
    analysis: z.string().max(8000),
    steps: z.array(z.string().min(1).max(1000)).max(15),
    questions: z.array(z.string().min(1).max(500)).max(5),
    cost_usd: z.number().min(0),
    billing: BillingZ,
    turns: z.number().int().min(0),
    session_id: z.string().max(128).nullable(),
    context_sha: z.string().max(64).nullable(),
    error: z.string().max(4000).nullable(),
    denied_tool_calls: z.array(DenialZ).max(50),
  })
  .strict();

export type IncidentRequest = z.infer<typeof RequestZ>;
export type WorkItemRequest = z.infer<typeof WorkItemRequestZ>;
/** What a code-fix Job is asked to do: for an incident (version 1) or for a work item (version 2). Ask subject.ts which. */
export type CodeFixRequest = IncidentRequest | WorkItemRequest;
export type PlanResult = z.infer<typeof PlanResultZ>;
export type ImplementResult = z.infer<typeof ImplementResultZ>;
export type Denial = z.infer<typeof DenialZ>;
export type VerificationLevel = z.infer<typeof VerificationLevelZ>;
export type Repos = z.infer<typeof ReposZ>;
export type RepoEntry = z.infer<typeof RepoEntryZ>;
export type CodeFixPhase = CodeFixRequest['phase'];
export type InvestigateRequest = z.infer<typeof InvestigateRequestZ>;
export type InvestigateResult = z.infer<typeof InvestigateResultZ>;
export type InvestigateOutcome = z.infer<typeof InvestigateOutcomeZ>;
export type Billing = z.infer<typeof BillingZ>;
export type CodeRef = z.infer<typeof CodeRefZ>;
export type TriageRequest = z.infer<typeof TriageRequestZ>;
export type TriageResult = z.infer<typeof TriageResultZ>;
export type TriageOutcome = z.infer<typeof TriageOutcomeZ>;
/** Every phase the runner can report; each one names its result schema. */
export type Phase = CodeFixPhase | 'investigate' | 'triage';
export type AnyResult = PlanResult | ImplementResult | InvestigateResult | TriageResult;
