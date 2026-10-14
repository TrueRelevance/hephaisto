import { closeSync, constants, createWriteStream, existsSync, fstatSync, lstatSync, mkdirSync, openSync, readSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { z } from 'zod';
import { BUNDLE_MAX_BYTES, HANDOFF_MAX_BYTES } from './config.js';
import { DraftModeZ, VerificationLevelZ } from './schemas.js';

// What one role leaves for the next (backlog #116). Three files, and who may believe which:
//
//   <handoff>/prepare.json   prepare -> coder     written before the model exists, read before the
//                                                 model exists: believed.
//   <seal>/prepare.json      prepare -> publish   the same document on a volume the coder
//                                                 container does not mount: believed.
//   <handoff>/coder.json     coder -> publish     written in the container the model ran in, on
//   <handoff>/branch.bundle                       the volume it could write: NOT believed. publish
//                                                 reads both as hostile bytes (readUntrusted,
//                                                 copyUntrusted), checks the schema, and
//                                                 re-derives everything a push depends on.
//
// Nothing secret is ever in any of them. A role that fails writes its failed RESULT here
// (`terminal`, or `publish: false`) and exits 0, and the role that prints prints it - so a
// failure in prepare reaches Hephaisto as the ordinary framed block, from the container it reads.

export const HANDOFF_VERSION = 1;
export const PREPARE_FILE = 'prepare.json';
export const CODER_FILE = 'coder.json';
export const BUNDLE_FILE = 'branch.bundle';

const Sha = z.string().regex(/^[0-9a-f]{40,64}$/);

export const PrepareHandoffZ = z
  .object({
    handoff_version: z.literal(HANDOFF_VERSION),
    role: z.literal('prepare'),
    attempt_id: z.string().max(64),
    phase: z.enum(['plan', 'implement', 'investigate', 'triage']),
    /** When prepare started. The Job's deadline has been running since then, so the later roles count from here. */
    started_at_ms: z.number().int().min(0),
    /** An early result - already_exists, or a failure. The role that prints prints it, and nothing else runs. */
    terminal: z.record(z.string(), z.unknown()).nullable(),
    context_sha: z.string().max(64).nullable(),
    /** The directory under <work>/repos that holds the checkout: the target, or an investigation's source. */
    repo_dir: z.string().max(255).nullable(),
    /** The target's CLAUDE.md as cloned, for the prompt. */
    claude_md: z.string().max(400_000).nullable(),
    /** plan: the commit the analysis runs on. */
    analysed_ref: z.string().max(64).nullable(),
    /** implement: the default-branch HEAD the assigned branch was created from. */
    base_commit: Sha.nullable(),
    /** The pinned Cait sibling is at <work>/repos/Cait. */
    cait: z.boolean(),
    notes: z.array(z.string().max(4000)).max(50),
    deviations: z.array(z.string().max(4000)).max(50),
    main_moved: z.string().max(4000).nullable(),
    /** implement, for publish: the publishing policy and the PR's shape, read from dev-context before the agent could touch it. */
    protected_globs: z.array(z.string().max(512)).max(500),
    pr: z
      .object({ assignee: z.string().max(255), labels: z.array(z.string().max(255)).max(50), draft: DraftModeZ, template: z.string().max(200_000) })
      .strict()
      .nullable(),
    /** investigate: how the read-only source clone went. */
    source: z.object({ cloned: z.boolean(), analysed_ref: z.string().max(64).nullable(), error: z.string().max(1000).nullable() }).strict().nullable(),
  })
  .strict();
export type PrepareHandoff = z.infer<typeof PrepareHandoffZ>;

const StepZ = z
  .object({
    name: z.enum(['restore', 'build', 'test', 'typecheck']),
    command: z.string().max(4000),
    exit: z.number().int(),
    durationMs: z.number().min(0),
    timedOut: z.boolean(),
    summary: z.string().max(1000).optional(),
  })
  .strict();

export const VerificationReportZ = z
  .object({
    level: VerificationLevelZ,
    steps: z.array(StepZ).max(8),
    buildPassed: z.boolean(),
    testsPassed: z.boolean(),
    failed: StepZ.nullable(),
    logTail: z.string().max(400_000),
    honestyNote: z.string().max(8000),
  })
  .strict();

export const CoderHandoffZ = z
  .object({
    handoff_version: z.literal(HANDOFF_VERSION),
    role: z.literal('coder'),
    attempt_id: z.string().max(64),
    /**
     * true: the driver's verification was green and branch.bundle holds the branch - publish may
     * push it, after its own checks. false: `result` is the outcome and nothing is pushed.
     */
    publish: z.boolean(),
    /** An implement result. With publish: true its outcome is a placeholder publish replaces. */
    result: z.record(z.string(), z.unknown()),
    /** The commit the driver verified; publish refuses a bundle whose tip is any other. */
    head: Sha.nullable(),
    change_summary: z.string().max(4000),
    report: VerificationReportZ.nullable(),
  })
  .strict();
export type CoderHandoff = z.infer<typeof CoderHandoffZ>;

export function newPrepareHandoff(attemptId: string, phase: PrepareHandoff['phase'], startedAtMs: number): PrepareHandoff {
  return {
    handoff_version: HANDOFF_VERSION,
    role: 'prepare',
    attempt_id: attemptId,
    phase,
    started_at_ms: startedAtMs,
    terminal: null,
    context_sha: null,
    repo_dir: null,
    claude_md: null,
    analysed_ref: null,
    base_commit: null,
    cait: false,
    notes: [],
    deviations: [],
    main_moved: null,
    protected_globs: [],
    pr: null,
    source: null,
  };
}

const cut = (s: string, max: number): string => (s.length > max ? s.slice(0, max) : s);
const cutAll = (xs: string[], items: number, max: number): string[] => xs.slice(0, items).map((x) => cut(x, max));

/** The writer keeps to the caps the reader enforces: a long CLAUDE.md is cut, not a reason to fail the run. */
function fit(doc: PrepareHandoff | CoderHandoff): PrepareHandoff | CoderHandoff {
  if (doc.role === 'coder') return { ...doc, change_summary: cut(doc.change_summary, 4000) };
  return {
    ...doc,
    claude_md: doc.claude_md === null ? null : cut(doc.claude_md, 400_000),
    notes: cutAll(doc.notes, 50, 4000),
    deviations: cutAll(doc.deviations, 50, 4000),
    main_moved: doc.main_moved === null ? null : cut(doc.main_moved, 4000),
    protected_globs: cutAll(doc.protected_globs, 500, 512),
    pr: doc.pr === null ? null : { ...doc.pr, template: cut(doc.pr.template, 200_000) },
  };
}

/** Whole or not at all: a reader never sees half a document. */
export function writeHandoff(dir: string, file: string, doc: PrepareHandoff | CoderHandoff): string {
  mkdirSync(dir, { recursive: true });
  const p = join(dir, file);
  const tmp = `${p}.${process.pid}.tmp`;
  writeFileSync(tmp, JSON.stringify(fit(doc)), { mode: 0o644 });
  renameSync(tmp, p);
  return p;
}

export class HandoffError extends Error {}

/**
 * A handoff prepare wrote, from a place the model could not have written yet (or ever).
 * Null when there is none.
 */
export function readPrepareHandoff(dir: string): PrepareHandoff | null {
  const p = join(dir, PREPARE_FILE);
  if (!existsSync(p)) return null;
  const parsed = PrepareHandoffZ.safeParse(JSON.parse(readUntrusted(p, HANDOFF_MAX_BYTES).toString('utf8')));
  if (!parsed.success) throw new HandoffError(`the prepare handoff does not match its schema: ${parsed.error.issues.slice(0, 5).map((i) => `${i.path.join('.')} ${i.message}`).join('; ')}`);
  return parsed.data;
}

/**
 * coder.json, as publish reads it: hostile bytes. Null when there is none. Every failure is the
 * same short sentence - an error text that quoted the file would carry whatever the file had
 * been pointed at into the pod log.
 */
export function readCoderHandoff(dir: string): CoderHandoff | null {
  if (!isPlainDirectory(dir)) return null;
  const p = join(dir, CODER_FILE);
  let bytes: Buffer;
  try {
    bytes = readUntrusted(p, HANDOFF_MAX_BYTES);
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw new HandoffError(`the coder's handoff could not be read as a plain file (${(e as NodeJS.ErrnoException).code ?? 'refused'})`);
  }
  let raw: unknown;
  try {
    raw = JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new HandoffError("the coder's handoff is not JSON");
  }
  const parsed = CoderHandoffZ.safeParse(raw);
  if (!parsed.success) throw new HandoffError("the coder's handoff does not match its schema");
  return parsed.data;
}

function isPlainDirectory(dir: string): boolean {
  try {
    return lstatSync(dir).isDirectory();
  } catch {
    return false;
  }
}

/**
 * Reads a file somebody else could have replaced: never through a symbolic link (a link to
 * /proc/self/environ would read the reader's own credentials into whatever it does next), only
 * a regular file, and only up to `max` bytes.
 */
export function readUntrusted(path: string, max: number): Buffer {
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  try {
    const st = fstatSync(fd);
    if (!st.isFile()) throw Object.assign(new Error('not a regular file'), { code: 'ENOTREG' });
    if (st.size > max) throw Object.assign(new Error('too large'), { code: 'EFBIG' });
    const buf = Buffer.alloc(st.size);
    let off = 0;
    while (off < buf.length) {
      const n = readSync(fd, buf, off, buf.length - off, off);
      if (n <= 0) break;
      off += n;
    }
    return buf.subarray(0, off);
  } finally {
    closeSync(fd);
  }
}

/** readUntrusted for something too large to hold: a bounded copy into a place the writer cannot reach. */
export async function copyUntrusted(path: string, dest: string, max = BUNDLE_MAX_BYTES): Promise<number> {
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  try {
    const st = fstatSync(fd);
    if (!st.isFile()) throw Object.assign(new Error('not a regular file'), { code: 'ENOTREG' });
    if (st.size > max) throw Object.assign(new Error('too large'), { code: 'EFBIG' });
    const out = createWriteStream(dest, { mode: 0o600, flags: 'wx' });
    const chunk = Buffer.alloc(1024 * 1024);
    let total = 0;
    try {
      for (;;) {
        const n = readSync(fd, chunk, 0, chunk.length, total);
        if (n <= 0) break;
        total += n;
        // the file can grow after the fstat; the cap is on what is copied, not on what was promised
        if (total > max) throw Object.assign(new Error('too large'), { code: 'EFBIG' });
        if (!out.write(Buffer.from(chunk.subarray(0, n)))) await new Promise<void>((r) => out.once('drain', () => r()));
      }
    } finally {
      await new Promise<void>((resolve, reject) => {
        out.once('error', reject);
        out.end(() => resolve());
      });
    }
    return total;
  } finally {
    closeSync(fd);
  }
}
