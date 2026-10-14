import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { implementOutputSchema, planOutputSchema } from '../src/phases.js';
import { minimalFailed } from '../src/result.js';
import { CONTRACTS_DIR, type SchemaName, rawSchema, validate, validateWith } from '../src/schemas.js';

// ajv against the vendored contract is what the runner uses at runtime: for every request before
// anything runs, and for every result before it is written.

/** Sample file name -> the schema it is a sample of (investigate-request-*, investigate-result-*, request-v2-*, request-*, plan-result-*, implement-result-*). */
function sampleSchema(f: string): SchemaName {
  if (f.startsWith('investigate-request')) return 'investigateRequest';
  if (f.startsWith('investigate-result')) return 'investigate';
  if (f.startsWith('triage-request')) return 'triageRequest';
  if (f.startsWith('triage-result')) return 'triage';
  if (f.startsWith('request-v2')) return 'requestV2';
  if (f.startsWith('request')) return 'request';
  if (f.startsWith('plan-result')) return 'plan';
  if (f.startsWith('implement-result')) return 'implement';
  throw new Error(`sample ${f} names no known schema`);
}

function samples(kind: 'valid' | 'invalid') {
  const dir = join(CONTRACTS_DIR, 'samples', kind);
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .map((f) => ({
      file: f,
      schema: sampleSchema(f),
      doc: JSON.parse(readFileSync(join(dir, f), 'utf8')) as Record<string, unknown>,
    }));
}

describe('vendored samples', () => {
  it('there are valid and invalid samples for every document kind', () => {
    for (const kind of ['valid', 'invalid'] as const) {
      expect(new Set(samples(kind).map((s) => s.schema))).toEqual(new Set(['request', 'requestV2', 'plan', 'implement', 'investigateRequest', 'investigate', 'triageRequest', 'triage']));
    }
  });
  for (const s of samples('valid')) {
    it(`valid/${s.file} validates`, () => {
      const v = validate(s.schema, s.doc);
      expect(v.errors).toEqual([]);
    });
  }
  for (const s of samples('invalid')) {
    it(`invalid/${s.file} is rejected`, () => {
      expect(validate(s.schema, s.doc).ok).toBe(false);
    });
  }
});

describe('runner-side schema facts', () => {
  const planReq = samples('valid').find((s) => s.file === 'request-plan.json')!.doc;

  it('the minimal failed results are themselves valid', () => {
    expect(validate('plan', minimalFailed('plan', '0192a6f0-0000-7000-8000-000000000001', 'x')).errors).toEqual([]);
    expect(validate('implement', minimalFailed('implement', '0192a6f0-0000-7000-8000-000000000001', 'x')).errors).toEqual([]);
  });
  it('the request accepts the local-cluster git server and file:// URLs, and nothing else', () => {
    for (const url of ['https://github.com/Flou21/x', 'http://coder-git.hephaisto-coder.svc/Flou21/x.git', 'file:///tmp/remote.git']) {
      expect(validate('request', { ...planReq, repository: { ...(planReq.repository as object), url } }).ok).toBe(true);
    }
    for (const url of ['ssh://git@github.com/x', 'git@github.com:x/y.git', '/tmp/remote.git']) {
      expect(validate('request', { ...planReq, repository: { ...(planReq.repository as object), url } }).ok).toBe(false);
    }
  });
  it('an unknown member in a request is rejected', () => {
    expect(validate('request', { ...planReq, extra: 1 }).ok).toBe(false);
  });
  it('a request is held to the schema of the version it states, and to neither when it mixes them', () => {
    const v2 = samples('valid').find((s) => s.file === 'request-v2-plan.json')!.doc;
    // each version's own valid sample is refused by the other's schema
    expect(validate('request', v2).ok).toBe(false);
    expect(validate('requestV2', planReq).ok).toBe(false);
    // a work item smuggled into a version-1 request, and an incident into a version-2 one
    expect(validate('request', { ...planReq, work_item: v2.work_item }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, incident: planReq.incident }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, findings: [] }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, investigation_summary: null }).ok).toBe(false);
    // the version is not a free choice of the sender's
    expect(validate('requestV2', { ...v2, contract_version: '1' }).ok).toBe(false);
    expect(validate('request', { ...planReq, contract_version: '2' }).ok).toBe(false);
  });
  it('a version-2 request takes an issue of any length GitHub allows, with or without a type, and the same repository URLs', () => {
    const v2 = samples('valid').find((s) => s.file === 'request-v2-plan.json')!.doc;
    const item = v2.work_item as Record<string, unknown>;
    expect(validate('requestV2', { ...v2, work_item: { ...item, type: null, body: '' } }).errors).toEqual([]);
    expect(validate('requestV2', { ...v2, work_item: { ...item, body: 'x'.repeat(65536) } }).errors).toEqual([]);
    expect(validate('requestV2', { ...v2, work_item: { ...item, body: 'x'.repeat(65537) } }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, work_item: { ...item, source: 'gitlab' } }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, work_item: { ...item, repository: 'owner/repo#1' } }).ok).toBe(false);
    expect(validate('requestV2', { ...v2, work_item: { ...item, comments: [{ author: 'a', body: 'b', id: 1 }] } }).ok).toBe(false);
    for (const url of ['http://coder-git.hephaisto-coder.svc/Flou21/x.git', 'file:///tmp/remote.git']) {
      expect(validate('requestV2', { ...v2, repository: { ...(v2.repository as object), url } }).ok).toBe(true);
    }
    expect(validate('requestV2', { ...v2, repository: { ...(v2.repository as object), branch: 'main' } }).ok).toBe(false);
  });
  it("the plan agent's output schema is cut from the plan result schema, caps included", () => {
    const s = planOutputSchema() as { properties: Record<string, unknown>; required: string[] };
    const full = rawSchema('plan') as { properties: Record<string, unknown> };
    // every member as the contract has it - but for the words about `questions`, which the contract calls optional and the agent must always write
    const { description: forTheAgent, ...asked } = s.properties.questions as Record<string, unknown>;
    const { description: inTheContract, ...contract } = full.properties.questions as Record<string, unknown>;
    expect(asked).toEqual(contract);
    expect(String(inTheContract)).toMatch(/^Optional; absent means none/);
    expect(String(forTheAgent)).toMatch(/Always present: an empty list when nothing is open/);
    for (const k of s.required.filter((k) => k !== 'questions')) expect(s.properties[k]).toEqual(full.properties[k]);
    const sample = samples('valid').find((x) => x.file === 'plan-result-questions.json')!.doc;
    const agentPart = Object.fromEntries(s.required.map((k) => [k, sample[k]]));
    expect(validateWith(planOutputSchema(), agentPart).errors).toEqual([]);
    expect(validateWith(planOutputSchema(), { ...agentPart, risk: 'low' }).ok).toBe(false);
    // required of the agent, optional in the result: a model has to say "nothing is open" by writing the empty list
    expect(s.required).toContain('questions');
    expect((rawSchema('plan') as { required: string[] }).required).not.toContain('questions');
    const { questions: _omitted, ...without } = agentPart;
    expect(validateWith(planOutputSchema(), without).errors.join(' ')).toMatch(/questions/);
    expect(validateWith(planOutputSchema(), { ...agentPart, questions: [] }).errors).toEqual([]);
    expect(validateWith(planOutputSchema(), { ...agentPart, questions: Array.from({ length: 11 }, () => 'q?') }).ok).toBe(false);
    expect(validateWith(planOutputSchema(), { ...agentPart, questions: ['x'.repeat(601)] }).ok).toBe(false);
  });
  it("the implement agent's output schema accepts files + deviations and nothing else", () => {
    expect(validateWith(implementOutputSchema(), { files: ['a.cs'], deviations: [], summary: 'x' }).ok).toBe(true);
    expect(validateWith(implementOutputSchema(), { files: ['a.cs'] }).ok).toBe(false);
    expect(validateWith(implementOutputSchema(), { files: [], deviations: [], build_passed: true }).ok).toBe(false);
  });
});
