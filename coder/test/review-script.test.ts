import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { APP_ROOT } from '../src/config.js';
import { FakeScriptZ } from '../src/fake-sdk.js';

// The weekly review's scripted answer (#312): that the fake SDK can load it and that it reads
// nothing but dev-context. The phase that plays it (src/review.ts) is held by review.test.ts; the
// verdicts' shape against the stand-in's seeding by tests/Hephaisto.Tests/E2e/UiSuiteTests.cs.

const path = join(APP_ROOT, 'fake-scripts', 'default.review.json');

describe('review: the shipped fake script', () => {
  it('is a script the fake SDK loads, with one result step that answers', () => {
    const script = FakeScriptZ.parse(JSON.parse(readFileSync(path, 'utf8')));
    const results = script.steps.filter((s) => 'result' in s);
    expect(results).toHaveLength(1);
    const out = (results[0] as { result: { structured_output: { outcome: string; verdicts: unknown[] }; cost_usd: number } }).result;
    expect(out.cost_usd).toBe(0);
    expect(out.structured_output.outcome).toBe('reviewed');
    expect(out.structured_output.verdicts.length).toBeGreaterThan(0);
  });

  it('is read-only: no Bash, Edit, Write, patch or commit step', () => {
    const script = FakeScriptZ.parse(JSON.parse(readFileSync(path, 'utf8')));
    for (const s of script.steps) {
      expect(['patch', 'patch_file', 'append', 'commit'].some((k) => k in s)).toBe(false);
      if ('tool' in s) expect(['Read', 'Grep', 'Glob']).toContain(s.tool);
    }
  });
});
