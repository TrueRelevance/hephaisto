import { existsSync, readFileSync } from 'node:fs';
import { DEADLINE_HEADROOM_SECONDS, PUBLISH_MARGIN_SECONDS, type Role, type RunnerEnv, parseEnv, roleEnv, roleViolation, workPaths } from './config.js';
import { CODER_FILE, type CoderHandoff, HANDOFF_VERSION, PREPARE_FILE, type PrepareHandoff, newPrepareHandoff, readPrepareHandoff, writeHandoff } from './handoff.js';
import { billingOf, fakeScriptCandidates, prepareInvestigate, runInvestigate } from './investigate.js';
import { addRedaction, log, redact, setLogPrefix } from './log.js';
import { type PhaseDeps, coderImplement, coderPlan, prepareImplement, preparePlan } from './phases.js';
import { runPublish } from './publish.js';
import { prepareTriage, runTriage } from './triage.js';
import { NIL_UUID, emitPrBody, emitResult, hasEmitted, minimalFailed, validUuid } from './result.js';
import { type AnyResult, type CodeFixRequest, type InvestigateRequest, type InvestigateResult, type Phase, SCHEMA_FILES, type TriageRequest, validate } from './schemas.js';
import { loadQuery } from './sdk.js';

// Entry point. One invariant above all others: exactly ONE framed result is the last thing on
// the stdout of the container Hephaisto reads, whatever happens - an invalid request, a crash,
// SIGTERM from the kubelet. A Job that ends without one is a ContractViolation on Hephaisto's
// side, which is the worst way to fail.
//
// Since #116 a Job is the same image started in up to three roles (config.ts ROLES), and only
// one of them prints:
//
//   plan, investigate,  prepare (init) -> coder             coder prints
//   triage
//   implement           prepare (init) -> coder (init) -> publish   publish prints
//
// A role that does not print hands its result to the next through a handoff file and exits 0,
// so the printing role still starts and prints it. An init container that exits non-zero fails
// the pod, and then nothing prints: that is Hephaisto's "the Job failed without a result", the
// same as a runner that was OOM-killed before #116.

export interface MainOptions {
  env?: NodeJS.ProcessEnv;
  write?: (s: string) => void;
  /** Tests run several mains in one process; the CLI entry installs signal handlers only once. */
  installSignalHandlers?: boolean;
}

export interface MainResult {
  phase: Phase;
  role: Role;
  /** What this role concluded - printed, or handed to the next role. Null when it handed over work, not a result. */
  result: AnyResult | null;
  /** The framed JSON, when this role printed. */
  json: string | null;
  /** Non-zero only when a role that does not print could not hand its result on: the pod must fail, since nothing would explain it. */
  exitCode: number;
}

/** Whether THIS process is the one Hephaisto reads, as far as it knows yet. For the entry point's last-resort handler. */
let printing = true;

interface Identity {
  attemptId: string;
  phase: Phase;
}

function identityFrom(raw: unknown): Identity {
  const o = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  return {
    attemptId: typeof o.attempt_id === 'string' && validUuid(o.attempt_id) ? o.attempt_id : NIL_UUID,
    phase: o.phase === 'implement' ? 'implement' : o.phase === 'investigate' ? 'investigate' : o.phase === 'triage' ? 'triage' : 'plan',
  };
}

export function internalDeadline(deadlineSeconds: number, now = Date.now()): number {
  const seconds = Math.max(deadlineSeconds - DEADLINE_HEADROOM_SECONDS, Math.floor(deadlineSeconds / 2));
  return now + seconds * 1000;
}

/** The publish role works after coder's internal deadline, in the headroom that deadline leaves. */
export function publishDeadline(deadlineSeconds: number, startedAt: number, now = Date.now()): number {
  return Math.max(startedAt + (deadlineSeconds - PUBLISH_MARGIN_SECONDS) * 1000, now + 30_000);
}

/** Which container Hephaisto reads: the one regular container of the pod. */
export function prints(role: Role, phase: Phase): boolean {
  if (role === 'prepare') return false;
  if (role === 'coder') return phase !== 'implement';
  return true;
}

function roleOf(env: NodeJS.ProcessEnv): Role {
  const r = env.CODEFIX_ROLE;
  return r === 'prepare' || r === 'coder' || r === 'publish' ? r : 'all';
}

export async function main(opts: MainOptions = {}): Promise<MainResult> {
  const rawEnv = opts.env ?? process.env;
  const role = roleOf(rawEnv);
  let identity: Identity = { attemptId: NIL_UUID, phase: 'plan' };
  printing = prints(role, identity.phase);
  let env: RunnerEnv | null = null;
  const abort = new AbortController();
  /** investigate: the result so far, so a SIGTERM still reports the cost, tokens and context it knows. */
  let live: InvestigateResult | null = null;
  const startedAt = Date.now();

  const emit = (result: AnyResult) => {
    // an error text that quotes a credential or the endpoint token must not carry it
    const r = result.error ? { ...result, error: redact(result.error) } : result;
    return emitResult(identity.phase, r, { sink: env?.resultSink, write: opts.write });
  };
  const failed = (error: string): AnyResult => {
    if (identity.phase === 'investigate' && live) return { ...live, outcome: 'failed', error };
    return minimalFailed(identity.phase, identity.attemptId, error, billingOf(env));
  };

  /** A result from a role that does not print: on to the next one, which will. Throws when it cannot be written. */
  const handOver = (result: AnyResult): void => {
    const r = result.error ? { ...result, error: redact(result.error) } : result;
    const handoffDir = env?.handoffDir ?? `${rawEnv.CODEFIX_WORK_DIR ?? '/work'}/handoff`;
    if (role === 'prepare') {
      const doc: PrepareHandoff = { ...newPrepareHandoff(identity.attemptId, identity.phase, startedAt), terminal: r as unknown as Record<string, unknown> };
      writePrepare(doc, handoffDir, env?.sealDir ?? '/sealed');
    } else {
      const doc: CoderHandoff = { handoff_version: HANDOFF_VERSION, role: 'coder', attempt_id: identity.attemptId, publish: false, result: r as unknown as Record<string, unknown>, head: null, change_summary: '', report: null };
      writeHandoff(handoffDir, CODER_FILE, doc);
    }
  };

  const done = (result: AnyResult | null, json: string | null = null, exitCode = 0): MainResult => ({ phase: identity.phase, role, result, json, exitCode });

  /** Every way a role ends with a RESULT (as opposed to work for the next role) goes through here. */
  const finish = (result: AnyResult): MainResult => {
    if (prints(role, identity.phase)) return done(result, emit(result));
    try {
      handOver(result);
    } catch (e) {
      log.error(`${role}: ${result.outcome}${result.error ? `: ${redact(result.error)}` : ''} - and it could not be handed on (${(e as Error).message}); failing the pod`);
      return done(result, null, 1);
    }
    log.info(`${role}: ${result.outcome}${result.error ? `: ${redact(result.error)}` : ''} - handed to the role that prints`);
    return done(result);
  };

  if (opts.installSignalHandlers) {
    let terminating = false;
    const onSignal = (sig: NodeJS.Signals) => {
      if (terminating) return; // a second signal must not bypass the report
      terminating = true;
      abort.abort();
      if (prints(role, identity.phase)) {
        log.error(`received ${sig}; reporting failure before the kubelet kills the pod`);
        if (!hasEmitted()) emit(failed(`runner terminated by ${sig} before it finished (Job deadline or eviction)`));
      } else {
        // the pod is being deleted: no later container will start, so there is nobody to hand a result to
        log.error(`received ${sig} in the ${role} role; the pod is ending and no role will print`);
      }
      process.exit(143);
    };
    process.on('SIGTERM', onSignal);
    process.on('SIGINT', onSignal);
    const onCrash = (e: unknown) => {
      log.error(`unhandled: ${redact((e as Error)?.stack ?? String(e))}`);
      const result = failed(`runner crashed: ${(e as Error)?.message ?? String(e)}`);
      if (prints(role, identity.phase)) {
        if (!hasEmitted()) emit(result);
        process.exit(1);
      }
      try {
        handOver(result);
        process.exit(0); // the next role starts, and prints this
      } catch {
        process.exit(1);
      }
    };
    process.on('uncaughtException', onCrash);
    process.on('unhandledRejection', onCrash);
  }

  // --- environment
  try {
    env = parseEnv(rawEnv);
  } catch (e) {
    return finish(minimalFailed(identity.phase, identity.attemptId, `invalid runner environment: ${(e as Error).message.slice(0, 1000)}`));
  }
  if (env.sdkMode === 'fake') setLogPrefix('FAKE SDK');
  for (const secret of [env.githubToken, env.nugetToken, env.anthropicAuth?.value]) addRedaction(secret);

  // --- request
  let raw: unknown;
  try {
    if (!existsSync(env.requestPath)) throw new Error(`request file ${env.requestPath} does not exist`);
    raw = JSON.parse(readFileSync(env.requestPath, 'utf8'));
  } catch (e) {
    log.error(`request could not be read: ${(e as Error).message}`);
    return finish(minimalFailed(identity.phase, identity.attemptId, `request could not be read: ${(e as Error).message}`));
  }
  identity = identityFrom(raw);
  printing = prints(role, identity.phase);
  const investigating = identity.phase === 'investigate';
  const triaging = identity.phase === 'triage';
  // A code-fix request is one of two documents, told apart by the version it states: 1 is for an
  // incident, 2 for a work item. Each is held to its own schema, so a refusal names one file and
  // one member - never "matches neither of two shapes".
  const schema = investigating ? 'investigateRequest' : triaging ? 'triageRequest' : (raw as { contract_version?: unknown } | null)?.contract_version === '2' ? 'requestV2' : 'request';
  const v = validate(schema, raw);
  if (!v.ok) {
    log.error(`request does not match ${SCHEMA_FILES[schema]}: ${v.errors.join('; ')}`);
    return finish(minimalFailed(identity.phase, identity.attemptId, `request does not match the contract: ${v.errors.slice(0, 10).join('; ')}`, billingOf(env)));
  }
  if (investigating) addRedaction((raw as InvestigateRequest).endpoint.token);

  // --- fake mode must never be able to spend money or reach a real model
  if (env.sdkMode === 'fake' && env.anthropicAuth) {
    return finish(minimalFailed(identity.phase, identity.attemptId, 'CODEFIX_SDK=fake refuses to start while CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is set', billingOf(env)));
  }
  // --- a role beside a credential that is not its own is the pod #116 is about
  const violation = roleViolation(env);
  if (violation) {
    log.error(violation);
    return finish(minimalFailed(identity.phase, identity.attemptId, violation, billingOf(env)));
  }
  // only the roles that run the model need its credential
  if ((role === 'all' || role === 'coder') && env.sdkMode === 'real' && !env.anthropicAuth) {
    const error = 'no Anthropic credential: set CLAUDE_CODE_OAUTH_TOKEN (or ANTHROPIC_API_KEY)';
    if (investigating) return finish({ ...minimalFailed('investigate', identity.attemptId, error, billingOf(env)), outcome: 'no_credential' });
    return finish(minimalFailed(identity.phase, identity.attemptId, error));
  }
  if (role === 'publish' && identity.phase !== 'implement') {
    return finish(minimalFailed(identity.phase, identity.attemptId, `the publish role exists only for the implement phase, and this request is ${identity.phase}`, billingOf(env)));
  }
  if (role === 'all' && (env.githubToken || env.nugetToken) && env.sdkMode === 'real') {
    log.warn('CODEFIX_ROLE is not set: every role runs in this one process, and nothing separates the model from the GitHub and NuGet tokens. A Job sets it.');
  }

  const runnerEnv = env;
  const paths = workPaths(runnerEnv.workDir);
  const deadlineSeconds = (raw as { budget: { deadline_seconds: number } }).budget.deadline_seconds;
  const as = (r: Exclude<Role, 'all'>): RunnerEnv => (role === 'all' ? roleEnv(runnerEnv, r) : runnerEnv);

  // ---------------------------------------------------------------------------------------------
  // the three roles; each returns either a result or what the next role needs

  const prepare = async (): Promise<PrepareHandoff> => {
    const e = as('prepare');
    const deadline = internalDeadline(deadlineSeconds, startedAt);
    log.info(`attempt ${identity.attemptId} phase ${identity.phase} role prepare sdk=${e.sdkMode}`);
    const timer = setTimeout(() => {
      log.warn('internal deadline reached while preparing; aborting');
      abort.abort();
    }, Math.max(0, deadline - Date.now()));
    try {
      if (investigating) return await prepareInvestigate(raw as InvestigateRequest, { env: e, paths, abort, startedAt });
      if (triaging) return await prepareTriage(raw as TriageRequest, { env: e, paths, abort, startedAt });
      const req = raw as CodeFixRequest;
      const deps = { env: e, paths, deadline, abort, startedAt };
      return req.phase === 'plan' ? await preparePlan(req, deps) : await prepareImplement(req, deps);
    } finally {
      clearTimeout(timer);
    }
  };

  /** The prepare handoff as the coder role must find it: present, and for this attempt and phase. */
  const prepared = (dir: string, who: string): PrepareHandoff => {
    const h = readPrepareHandoff(dir);
    if (!h) throw new Error(`the prepare role left no handoff in ${dir}; the ${who} role cannot run without it`);
    if (h.attempt_id !== identity.attemptId || h.phase !== identity.phase) throw new Error(`the prepare handoff in ${dir} is for another attempt or phase`);
    return h;
  };

  const coder = async (h: PrepareHandoff): Promise<AnyResult | CoderHandoff> => {
    const e = as('coder');
    const deadline = internalDeadline(deadlineSeconds, h.started_at_ms);
    log.info(`attempt ${identity.attemptId} phase ${identity.phase} role coder sdk=${e.sdkMode} deadline in ${Math.round((deadline - Date.now()) / 1000)}s`);
    if (investigating) {
      const ireq = raw as InvestigateRequest;
      const result = await runInvestigate(
        ireq,
        {
          env: e,
          paths,
          deadline,
          abort,
          onProgress: (r) => (live = r),
          makeQuery: () =>
            loadQuery(e.sdkMode, {
              scriptDir: e.fakeScriptDir,
              repoName: '',
              phase: 'investigate',
              vars: {},
              scripts: fakeScriptCandidates(ireq, e.fakeScript),
              request: ireq,
            }),
        },
        h,
      );
      log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)}, ${result.billing})`);
      return result;
    }
    if (triaging) {
      const result = await runTriage(
        raw as TriageRequest,
        {
          env: e,
          paths,
          deadline,
          abort,
          makeQuery: (vars) =>
            loadQuery(e.sdkMode, {
              scriptDir: e.fakeScriptDir,
              repoName: (raw as TriageRequest).issue.repository.split('/')[1] ?? '',
              phase: 'triage',
              vars,
            }),
        },
        h,
      );
      log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)}, ${result.billing})`);
      return result;
    }
    const req = raw as CodeFixRequest;
    const deps: PhaseDeps = {
      env: e,
      paths,
      deadline,
      abort,
      makeQuery: (fake) => loadQuery(e.sdkMode, { scriptDir: e.fakeScriptDir, repoName: fake.repoName, phase: req.phase, vars: fake.vars, variant: fake.variant }),
    };
    if (req.phase === 'plan') {
      const result = await coderPlan(req, deps, h);
      log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)})`);
      return result;
    }
    return coderImplement(req, deps, h);
  };

  const publish = async (sealed: PrepareHandoff): Promise<AnyResult> => {
    const e = as('publish');
    log.info(`attempt ${identity.attemptId} phase implement role publish`);
    const result = await runPublish(
      raw as CodeFixRequest,
      {
        env: e,
        deadline: publishDeadline(deadlineSeconds, sealed.started_at_ms),
        abort,
        // beside the result and before it, on the stream Hephaisto reads the result from
        onPrBody: (body) => emitPrBody(body, { write: opts.write }),
      },
      sealed,
    );
    log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)})`);
    return result;
  };

  /** A terminal result from prepare, as the role that prints it sees it. */
  const terminal = (h: PrepareHandoff): AnyResult => {
    const t = { ...(h.terminal as unknown as AnyResult), attempt_id: identity.attemptId, phase: identity.phase } as AnyResult;
    // prepare holds no model credential, so it cannot say who would have paid; this role can
    return identity.phase === 'investigate' || identity.phase === 'triage' ? ({ ...t, billing: billingOf(runnerEnv) } as AnyResult) : t;
  };

  try {
    if (role === 'prepare') {
      const h = await prepare();
      writePrepare(h, runnerEnv.handoffDir, runnerEnv.sealDir);
      if (h.terminal) log.info(`prepare: ended early with ${String(h.terminal.outcome)}${h.terminal.error ? `: ${redact(String(h.terminal.error))}` : ''} - handed to the role that prints`);
      else log.info('prepare: done; the workspace is ready for the coder role');
      return done(h.terminal ? terminal(h) : null);
    }

    if (role === 'coder') {
      const h = prepared(runnerEnv.handoffDir, 'coder');
      if (h.terminal) {
        // implement: publish prints it, from the sealed copy; this role has nothing to add
        if (identity.phase === 'implement') {
          log.info(`coder: prepare ended the run with ${String(h.terminal.outcome)}; the agent is not started`);
          return done(terminal(h));
        }
        return finish(terminal(h));
      }
      const out = await coder(h);
      if ('handoff_version' in out) {
        writeHandoff(runnerEnv.handoffDir, CODER_FILE, out);
        log.info(out.publish ? 'coder: verified; the branch is handed to the publish role' : `coder: ${String(out.result.outcome)}${out.result.error ? `: ${redact(String(out.result.error))}` : ''} - handed to the publish role, which prints it`);
        return done(out.publish ? null : (out.result as unknown as AnyResult));
      }
      return finish(out);
    }

    if (role === 'publish') {
      const sealed = prepared(runnerEnv.sealDir, 'publish');
      return finish(sealed.terminal ? terminal(sealed) : await publish(sealed));
    }

    // --- all: the three roles, one after the other, through the same files
    writePrepare(await prepare(), runnerEnv.handoffDir, runnerEnv.sealDir);
    const h = prepared(runnerEnv.handoffDir, 'coder');
    if (h.terminal) return finish(terminal(h));
    const out = await coder(h);
    if (!('handoff_version' in out)) return finish(out);
    writeHandoff(runnerEnv.handoffDir, CODER_FILE, out);
    return finish(await publish(prepared(runnerEnv.sealDir, 'publish')));
  } catch (e) {
    log.error(`${role} failed: ${redact((e as Error).stack ?? String(e))}`);
    return finish(failed(`runner failed: ${(e as Error).message}`));
  }
}

/**
 * prepare's handoff goes to the coder role through the shared volume, and - implement only - to
 * the publish role through the sealed one. A terminal result whose phase is unknown (the request
 * could not be read) is sealed too, when there is a sealed volume to write.
 */
function writePrepare(h: PrepareHandoff, handoffDir: string, sealDir: string): void {
  writeHandoff(handoffDir, PREPARE_FILE, h);
  if (h.phase === 'implement' || (h.terminal !== null && existsSync(sealDir))) writeHandoff(sealDir, PREPARE_FILE, h);
}

// CLI entry (dist/main.js in the image)
const invokedDirectly = process.argv[1] && /main\.(js|ts)$/.test(process.argv[1]) && !process.env.VITEST;
if (invokedDirectly) {
  main({ installSignalHandlers: true })
    .then((r) => process.exit(r.exitCode))
    .catch((e: unknown) => {
      log.error(`fatal: ${redact((e as Error)?.stack ?? String(e))}`);
      // only the role Hephaisto reads may print; any other leaves the pod to fail without a result
      if (printing && !hasEmitted()) emitResult('plan', minimalFailed('plan', NIL_UUID, `runner crashed: ${(e as Error)?.message ?? String(e)}`));
      process.exit(1);
    });
}
