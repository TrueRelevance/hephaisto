import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { z } from 'zod';
import { run } from './exec.js';
import { log } from './log.js';
import { InvestigatorClient } from './mcp-client.js';
import type {
  HookCallbackMatcher,
  HookJSONOutput,
  Options,
  PreToolUseHookInput,
  PostToolUseHookInput,
  SDKAssistantMessage,
  SDKMessage,
  SDKResultMessage,
  SDKSystemMessage,
  SDKUserMessage,
} from './sdk.js';

// A scripted stand-in for query(): it yields real SDKMessage shapes and - the part that matters -
// routes every scripted tool call through the SAME hooks and canUseTool the real CLI would call.
// A script that asks for `git push --force origin main` therefore produces a real guard denial,
// recorded exactly as it would be in production. No network, no model, no tokens: main.ts
// refuses to start fake mode while an Anthropic credential is present.

const ResultStepZ = z.object({
  subtype: z.enum(['success', 'error_max_turns', 'error_max_budget_usd', 'error_during_execution', 'error_max_structured_output_retries']).default('success'),
  structured_output: z.unknown().optional(),
  cost_usd: z.number().min(0).default(0),
  num_turns: z.number().int().min(0).optional(),
  errors: z.array(z.string()).default([]),
  is_error: z.boolean().optional(),
  api_error_status: z.number().int().optional(),
  text: z.string().optional(),
});

/** investigate: keep group 1 (or the whole match) of this step's result text as ${as}, and its [step <id>] as ${as.step}. */
const CaptureZ = z.object({ as: z.string().regex(/^[A-Za-z_][A-Za-z0-9_]*$/), regex: z.string() }).strict();

const StepZ = z.union([
  z.object({ tool: z.string(), input: z.record(z.string(), z.unknown()), capture: CaptureZ.optional() }).strict(),
  z.object({ patch: z.string() }).strict(),
  /** A unified diff kept next to the script (relative to the script dir). */
  z.object({ patch_file: z.string() }).strict(),
  z.object({ append: z.object({ path: z.string(), text: z.string() }).strict() }).strict(),
  z.object({ commit: z.string() }).strict(),
  z.object({ text: z.string() }).strict(),
  z.object({ sleep_ms: z.number().int().min(0) }).strict(),
  z.object({ throw: z.string() }).strict(),
  z.object({ result: ResultStepZ }).strict(),
]);

export const FakeScriptZ = z
  .object({
    description: z.string().optional(),
    steps: z.array(StepZ),
    /** Played instead of `steps` when the runner resumes the session for its one repair turn. */
    repair: z.array(StepZ).optional(),
  })
  .strict();

export type FakeScript = z.infer<typeof FakeScriptZ>;
type Step = z.infer<typeof StepZ>;

export interface FakeContext {
  scriptDir: string;
  repoName: string;
  phase: 'plan' | 'implement' | 'investigate' | 'triage';
  /** Substituted into every string of the script as {{name}}. */
  vars: Record<string, string>;
  /** investigate: script file names to try, in order, instead of <repoName>.<phase>.json / default.<phase>.json. */
  scripts?: string[];
  /** investigate: what ${request.<path>} reads. */
  request?: unknown;
  /**
   * plan: `<name>.<phase>.<variant>.json` is looked for before `<name>.<phase>.json`, for the
   * repository, its alias and `default` in that order. Lower-case letters, digits and hyphens.
   */
  variant?: string | undefined;
}

/** The file in a script directory that says which repositories play another one's scripts. */
export const ALIASES_FILE = 'aliases.json';

const AliasesZ = z.record(z.string(), z.string().regex(/^[A-Za-z0-9._-]+$/));

/**
 * The name a repository's scripts are filed under when it has none of its own: `aliases.json`
 * in the script directory maps a repos.yaml name to another one. A copy of a fixture repository
 * under a second name (the live tier's sandbox on github.com) then plays the fixture's scripts
 * AND its patch, from the one set of files - there is nothing to keep in step.
 */
export function scriptAlias(scriptDir: string, repoName: string): string | null {
  const p = join(scriptDir, ALIASES_FILE);
  if (!repoName || !existsSync(p)) return null;
  const aliases = AliasesZ.parse(JSON.parse(readFileSync(p, 'utf8')));
  const alias = Object.hasOwn(aliases, repoName) ? aliases[repoName]! : null;
  return alias && alias !== repoName ? alias : null;
}

export function loadScript(ctx: FakeContext): { script: FakeScript; path: string } {
  // its own script first, then the one it is an alias of, then the default
  const names = ctx.scripts ? [] : [ctx.repoName, scriptAlias(ctx.scriptDir, ctx.repoName), 'default'].filter((n): n is string => n !== null);
  const variant = ctx.variant && /^[a-z0-9-]{1,32}$/.test(ctx.variant) ? ctx.variant : null;
  const candidates = ctx.scripts
    ? ctx.scripts.map((f) => join(ctx.scriptDir, f))
    : [...(variant ? names.map((n) => `${n}.${ctx.phase}.${variant}.json`) : []), ...names.map((n) => `${n}.${ctx.phase}.json`)].map((f) => join(ctx.scriptDir, f));
  const path = candidates.find((p) => existsSync(p));
  if (!path) throw new Error(`no fake script: looked for ${candidates.join(', ')}`);
  const raw: unknown = JSON.parse(readFileSync(path, 'utf8'));
  return { script: FakeScriptZ.parse(substitute(raw, ctx.vars)), path };
}

function substitute(v: unknown, vars: Record<string, string>): unknown {
  if (typeof v === 'string') return v.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (_, k: string) => vars[k] ?? '');
  if (Array.isArray(v)) return v.map((x) => substitute(x, vars));
  if (v && typeof v === 'object') return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, substitute(x, vars)]));
  return v;
}

/** Cumulative cost per session, so a resumed session reports a running total like the real CLI. */
const sessionCosts = new Map<string, number>();
/** investigate: what a session captured, so a resumed turn can cite the step ids the "model" saw before. */
const sessionCaptures = new Map<string, Map<string, Captured>>();

function shq(s: string): string {
  return `'${s.replace(/'/g, `'\\''`)}'`;
}

class AbortError extends Error {
  override name = 'AbortError';
}

interface Captured {
  value: string;
  step: string;
}

/** investigate: ${name}, ${name.step} and ${request.a.b} in every string of a step's input, resolved when the step runs. */
export function template(v: unknown, captures: Map<string, Captured>, request: unknown): unknown {
  if (typeof v === 'string') {
    return v.replace(/\$\{([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)\}/g, (whole, expr: string) => {
      const parts = expr.split('.');
      if (parts[0] === 'request') {
        let cur: unknown = request;
        for (const k of parts.slice(1)) cur = cur && typeof cur === 'object' ? (cur as Record<string, unknown>)[k] : undefined;
        return cur === undefined || cur === null ? '' : String(cur);
      }
      const c = captures.get(parts[0]!);
      if (!c) {
        log.warn(`FAKE SDK \${${expr}}: nothing captured as ${parts[0]}`);
        return '';
      }
      if (parts.length === 1) return c.value;
      if (parts.length === 2 && parts[1] === 'step') return c.step;
      return whole;
    });
  }
  if (Array.isArray(v)) return v.map((x) => template(x, captures, request));
  if (v && typeof v === 'object') return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, template(x, captures, request)]));
  return v;
}

/** `mcp__<server>__<tool>` → its parts; the server key may itself contain single underscores. */
function mcpParts(tool: string): { server: string; name: string } | null {
  const m = /^mcp__(.+?)__(.+)$/.exec(tool);
  return m ? { server: m[1]!, name: m[2]! } : null;
}

export async function* fakeQuery(params: { prompt: string; options: Options }, ctx: FakeContext): AsyncGenerator<SDKMessage, void> {
  const { options } = params;
  const { script, path } = loadScript(ctx);
  const resuming = typeof options.resume === 'string';
  const steps: Step[] = resuming ? (script.repair ?? []) : script.steps;
  const sessionId = resuming ? options.resume! : randomUUID();
  const cwd = options.cwd ?? process.cwd();
  const signal = options.abortController?.signal;
  const denials: SDKResultMessage['permission_denials'] = [];
  let turns = 0;
  const investigate = ctx.phase === 'investigate';
  const captures = (resuming && sessionCaptures.get(sessionId)) || new Map<string, Captured>();
  sessionCaptures.set(sessionId, captures);
  const mcpServers = (options.mcpServers ?? {}) as Record<string, { type?: string; url?: string; headers?: Record<string, string> }>;
  const clients = new Map<string, InvestigatorClient>();
  log.info(`FAKE SDK script ${path}${resuming ? ' (repair turn)' : ''}: ${steps.length} step(s)`);

  const init = {
    type: 'system',
    subtype: 'init',
    apiKeySource: 'none',
    claude_code_version: 'fake',
    cwd,
    tools: ['Read', 'Grep', 'Glob', 'Bash', 'Edit', 'Write'].filter((t) => !(options.disallowedTools ?? []).includes(t)),
    mcp_servers: Object.keys(mcpServers).map((name) => ({ name, status: 'connected' })),
    model: 'fake',
    permissionMode: options.permissionMode ?? 'default',
    slash_commands: [],
    output_style: 'default',
    skills: [],
    plugins: [],
    uuid: randomUUID(),
    session_id: sessionId,
  } as unknown as SDKSystemMessage;
  yield init;

  const checkAbort = () => {
    if (signal?.aborted) throw new AbortError('Claude Code process aborted by user');
  };

  const assistant = (content: unknown[]): SDKAssistantMessage =>
    ({
      type: 'assistant',
      message: { id: `msg_${randomUUID()}`, type: 'message', role: 'assistant', model: 'fake', content, stop_reason: null, stop_sequence: null, usage: { input_tokens: 0, output_tokens: 0 } },
      parent_tool_use_id: null,
      uuid: randomUUID(),
      session_id: sessionId,
    }) as unknown as SDKAssistantMessage;

  const toolResult = (toolUseId: string, content: string, isError: boolean): SDKUserMessage =>
    ({
      type: 'user',
      message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: toolUseId, content, is_error: isError }] },
      parent_tool_use_id: null,
      uuid: randomUUID(),
      session_id: sessionId,
    }) as unknown as SDKUserMessage;

  /** The permission path of the real CLI, in order: disallowed → PreToolUse hooks → allow rules / mode → canUseTool. */
  async function permit(tool: string, input: Record<string, unknown>, toolUseId: string): Promise<string | null> {
    const offered = Array.isArray(options.tools) ? options.tools : null;
    // tools pins the built-ins; an MCP tool is offered when its server is configured
    const mcp = mcpParts(tool);
    const isOffered = mcp ? mcp.server in mcpServers : !offered || offered.includes(tool);
    if ((options.disallowedTools ?? []).includes(tool) || !isOffered) return `No such tool available: ${tool}`;
    for (const m of matchersFor(options.hooks?.PreToolUse, tool)) {
      for (const h of m.hooks) {
        const hin: PreToolUseHookInput = {
          hook_event_name: 'PreToolUse',
          tool_name: tool,
          tool_input: input,
          tool_use_id: toolUseId,
          session_id: sessionId,
          transcript_path: '',
          cwd,
        };
        const out = (await h(hin, toolUseId, { signal: signal ?? new AbortController().signal })) as HookJSONOutput;
        const hs = (out as { hookSpecificOutput?: { permissionDecision?: string; permissionDecisionReason?: string } }).hookSpecificOutput;
        if (hs?.permissionDecision === 'deny') return hs.permissionDecisionReason ?? 'denied by hook';
        if ((out as { decision?: string }).decision === 'block') return (out as { reason?: string }).reason ?? 'blocked by hook';
      }
    }
    const autoAllowed =
      (options.allowedTools ?? []).includes(tool) || (options.permissionMode === 'acceptEdits' && ['Edit', 'Write', 'MultiEdit', 'NotebookEdit'].includes(tool));
    if (!autoAllowed) {
      if (!options.canUseTool) return `${tool} needs permission and nobody can grant it`;
      const r = await options.canUseTool(tool, input, { signal: signal ?? new AbortController().signal, toolUseID: toolUseId, requestId: randomUUID() });
      if (!r || r.behavior === 'deny') return r?.message ?? 'denied';
    }
    return null;
  }

  async function execute(tool: string, input: Record<string, unknown>): Promise<{ out: string; isError: boolean }> {
    const abs = (p: unknown) => (typeof p === 'string' ? (isAbsolute(p) ? p : resolve(cwd, p)) : '');
    switch (tool) {
      case 'Bash': {
        const r = await run('bash', ['-c', String(input.command ?? '')], { cwd, env: options.env as NodeJS.ProcessEnv, timeoutMs: 120_000, maxOutputBytes: 30_000 });
        return { out: `${r.stdout}${r.stderr}`.slice(-30_000) || `(exit ${r.code})`, isError: r.code !== 0 };
      }
      case 'Read': {
        const p = abs(input.file_path);
        if (!existsSync(p)) return { out: `File does not exist: ${p}`, isError: true };
        return { out: readFileSync(p, 'utf8').split('\n').slice(0, 2000).join('\n'), isError: false };
      }
      case 'Write': {
        const p = abs(input.file_path);
        mkdirSync(dirname(p), { recursive: true });
        writeFileSync(p, String(input.content ?? ''));
        return { out: `File written: ${p}`, isError: false };
      }
      case 'Edit': {
        const p = abs(input.file_path);
        if (!existsSync(p)) return { out: `File does not exist: ${p}`, isError: true };
        const before = readFileSync(p, 'utf8');
        const oldS = String(input.old_string ?? '');
        if (!before.includes(oldS)) return { out: 'old_string not found', isError: true };
        const after = input.replace_all ? before.split(oldS).join(String(input.new_string ?? '')) : before.replace(oldS, String(input.new_string ?? ''));
        writeFileSync(p, after);
        return { out: `File edited: ${p}`, isError: false };
      }
      default: {
        const mcp = mcpParts(tool);
        const server = mcp ? mcpServers[mcp.server] : undefined;
        if (mcp && server?.type === 'http' && server.url) {
          // a REAL call against the configured endpoint, with the headers the real CLI would send
          let client = clients.get(mcp.server);
          if (!client) {
            const auth = server.headers?.Authorization ?? server.headers?.authorization ?? '';
            client = new InvestigatorClient({ url: server.url, token: auth.replace(/^Bearer\s+/i, '') }, 'hephaisto-coder-fake-sdk');
            clients.set(mcp.server, client);
          }
          const r = await client.callTool(mcp.name, input, signal);
          return { out: r.text, isError: r.isError };
        }
        return { out: `(fake SDK: ${tool} not executed)`, isError: false };
      }
    }
  }

  let lastOutput = '';

  async function* toolCall(tool: string, input: Record<string, unknown>, sideEffect?: () => Promise<{ out: string; isError: boolean }>) {
    checkAbort();
    turns++;
    const id = `toolu_${randomUUID().replace(/-/g, '').slice(0, 24)}`;
    yield assistant([{ type: 'tool_use', id, name: tool, input }]);
    const denied = await permit(tool, input, id);
    if (denied !== null) {
      log.info(`FAKE SDK ${tool} denied: ${denied}`);
      denials.push({ tool_name: tool, tool_use_id: id, tool_input: input });
      yield toolResult(id, denied, true);
      return false;
    }
    const r = sideEffect ? await sideEffect() : await execute(tool, input);
    lastOutput = r.out;
    for (const m of matchersFor(options.hooks?.PostToolUse, tool)) {
      for (const h of m.hooks) {
        const hin: PostToolUseHookInput = {
          hook_event_name: 'PostToolUse',
          tool_name: tool,
          tool_input: input,
          tool_response: r.out,
          tool_use_id: id,
          session_id: sessionId,
          transcript_path: '',
          cwd,
        };
        await h(hin, id, { signal: signal ?? new AbortController().signal });
      }
    }
    yield toolResult(id, r.out, r.isError);
    return !r.isError;
  }

  try {
    for (const step of steps) {
      checkAbort();
      if ('sleep_ms' in step) {
        await new Promise<void>((res) => {
          const t = setTimeout(res, step.sleep_ms);
          signal?.addEventListener('abort', () => {
            clearTimeout(t);
            res();
          }, { once: true });
        });
        continue;
      }
      if ('throw' in step) throw new Error(step.throw);
      if ('text' in step) {
        turns++;
        yield assistant([{ type: 'text', text: step.text }]);
        continue;
      }
      if ('tool' in step) {
        const input = investigate ? (template(step.input, captures, ctx.request) as Record<string, unknown>) : step.input;
        lastOutput = '';
        const ok: boolean = yield* toolCall(step.tool, input);
        if (step.capture) {
          const stepId = /^\[step ([0-9a-fA-F-]{36})\]/.exec(lastOutput)?.[1] ?? '';
          const m = ok ? new RegExp(step.capture.regex, 'm').exec(lastOutput) : null;
          const value = m ? (m[1] ?? m[0]) : '';
          if (!m) log.warn(`FAKE SDK capture ${step.capture.as}: /${step.capture.regex}/ matched nothing in the ${step.tool} result`);
          captures.set(step.capture.as, { value, step: stepId });
        }
        continue;
      }
      if ('append' in step) {
        const p = isAbsolute(step.append.path) ? step.append.path : resolve(cwd, step.append.path);
        const before = existsSync(p) ? readFileSync(p, 'utf8') : '';
        yield* toolCall('Write', { file_path: p, content: before + step.append.text });
        continue;
      }
      if ('patch' in step || 'patch_file' in step) {
        const diff = 'patch' in step ? step.patch : readFileSync(resolve(ctx.scriptDir, step.patch_file), 'utf8');
        const files = [...diff.matchAll(/^\+\+\+ b\/(.+)$/gm)].map((m) => m[1]!.trim());
        let allowed = true;
        for (const f of files) {
          // each touched path asks for permission exactly like an Edit of that file
          const ok: boolean = yield* toolCall('Edit', { file_path: resolve(cwd, f), old_string: '', new_string: '(patch)' }, async () => ({ out: 'ok', isError: false }));
          allowed &&= ok;
        }
        if (allowed) {
          const r = await run('git', ['apply', '--whitespace=nowarn', '-'], { cwd, env: options.env as NodeJS.ProcessEnv, input: diff });
          if (r.code !== 0) log.warn(`FAKE SDK git apply failed: ${r.stderr.trim()}`);
        }
        continue;
      }
      if ('commit' in step) {
        // the two trailers the prompt names: Hephaisto-Incident or Hephaisto-Issue, then Hephaisto-Attempt
        const msg = `${step.commit}\n\n${ctx.vars.trailers ?? `Hephaisto-Incident: ${ctx.vars.incident_id ?? ''}\nHephaisto-Attempt: ${ctx.vars.attempt_id ?? ''}`}`;
        if (yield* toolCall('Bash', { command: 'git add -A' })) yield* toolCall('Bash', { command: `git commit -q -m ${shq(msg)}` });
        continue;
      }
      if ('result' in step) {
        const r = step.result;
        const prior = resuming ? (sessionCosts.get(sessionId) ?? 0) : 0;
        const total = prior + r.cost_usd;
        sessionCosts.set(sessionId, total);
        const spentThisQuery = r.cost_usd;
        let subtype = r.subtype;
        if (subtype === 'success' && options.maxBudgetUsd !== undefined && spentThisQuery > options.maxBudgetUsd) subtype = 'error_max_budget_usd';
        const common = {
          type: 'result' as const,
          duration_ms: 1,
          duration_api_ms: 0,
          num_turns: r.num_turns ?? turns,
          stop_reason: null,
          total_cost_usd: total,
          usage: {} as SDKResultMessage['usage'],
          modelUsage: {},
          permission_denials: denials,
          uuid: randomUUID() as SDKResultMessage['uuid'],
          session_id: sessionId,
        };
        const msg: SDKResultMessage =
          subtype === 'success'
            ? {
                ...common,
                subtype: 'success',
                is_error: r.is_error ?? false,
                api_error_status: r.api_error_status ?? null,
                result: r.text ?? (r.structured_output !== undefined ? JSON.stringify(r.structured_output) : ''),
                structured_output: r.structured_output,
              }
            : { ...common, subtype, is_error: true, errors: r.errors };
        yield msg;
        return;
      }
    }
    if (investigate) {
      // an investigation answers through conclude, not through a result step: ending is success
      yield {
        type: 'result',
        subtype: 'success',
        duration_ms: 1,
        duration_api_ms: 0,
        is_error: false,
        num_turns: turns,
        stop_reason: null,
        total_cost_usd: resuming ? (sessionCosts.get(sessionId) ?? 0) : 0,
        usage: {} as SDKResultMessage['usage'],
        modelUsage: {},
        permission_denials: denials,
        api_error_status: null,
        result: '',
        structured_output: undefined,
        uuid: randomUUID() as SDKResultMessage['uuid'],
        session_id: sessionId,
      } as SDKResultMessage;
      return;
    }
    yield {
      type: 'result',
      subtype: 'error_during_execution',
      duration_ms: 1,
      duration_api_ms: 0,
      is_error: true,
      num_turns: turns,
      stop_reason: null,
      total_cost_usd: 0,
      usage: {} as SDKResultMessage['usage'],
      modelUsage: {},
      permission_denials: denials,
      errors: ['fake script ended without a result step'],
      uuid: randomUUID() as SDKResultMessage['uuid'],
      session_id: sessionId,
    };
  } finally {
    for (const c of clients.values()) await c.close();
  }
}

function matchersFor(list: HookCallbackMatcher[] | undefined, tool: string): HookCallbackMatcher[] {
  return (list ?? []).filter((m) => !m.matcher || m.matcher === '*' || new RegExp(`^(?:${m.matcher})$`).test(tool));
}
