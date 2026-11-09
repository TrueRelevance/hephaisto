import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { RunnerEnv, WorkPaths } from './config.js';
import { Git, clone, driverGitEnv, shaFromImage } from './git.js';
import { log } from './log.js';
import { NOT_ENABLED, enabledRepo, loadRepos, repoDirName } from './repos.js';
import type { CodeFixRequest, RepoEntry, Repos } from './schemas.js';

// Everything the agent will see is laid out here, by the driver, before the agent exists - and,
// since #116, in another container than the agent's: the prepare role holds the GitHub token
// these clones need, and has ended before the coder role starts. No token is written anywhere
// under /work: git gets it through GIT_ASKPASS, and a remote URL never carries one.
//   /work/context        dev-context @ request.context.ref (its sha goes into the result)
//   /work/.claude        CLAUDE_CONFIG_DIR - dev-context/.claude is the agent's USER scope
//   /work/repos/<name>   the target, blobless clone
//   /work/repos/Cait     the pinned Cait, only when repos.yaml says the repo pins it
//   /work/ref/Cait       symlink to it, for the prompt's "read-only reference" (the guard denies
//                        every write outside the target, so read-only is enforced there)

export interface ContextInfo {
  contextSha: string | null;
  repos: Repos;
}

export function makeDirs(paths: WorkPaths): void {
  for (const d of [paths.root, paths.repos, paths.ref, paths.nuget, paths.nugetPackages, paths.home, paths.out, paths.claudeConfig]) {
    mkdirSync(d, { recursive: true });
  }
}

/** What both request kinds (code fix, investigate) say about the dev-context checkout. */
export interface ContextRequest {
  context: CodeFixRequest['context'];
}

/** The slice of a request cloneTarget needs: a code-fix request's repository, or an investigate request's source. */
export interface CloneRequest {
  repository: { url: string; default_branch: string };
}

export async function prepareContext(req: ContextRequest, env: RunnerEnv, paths: WorkPaths, signal?: AbortSignal): Promise<ContextInfo> {
  const genv = driverGitEnv(env, paths.home);
  rmSync(paths.context, { recursive: true, force: true });
  await clone(req.context.repository_url, paths.context, genv, { signal });
  const git = new Git(paths.context, genv, signal);
  const ref = req.context.ref;
  let checkedOut = false;
  for (const candidate of [`origin/${ref}`, ref]) {
    if ((await git.try(['checkout', '--quiet', '--detach', candidate])).code === 0) {
      checkedOut = true;
      break;
    }
  }
  if (!checkedOut) throw new Error(`dev-context ref ${ref} not found`);
  const contextSha = await git.head();
  log.info(`dev-context ${req.context.repository_url} @ ${ref} = ${contextSha}`);
  populateClaudeConfig(paths);
  return { contextSha, repos: loadRepos(paths.context) };
}

/** dev-context/.claude becomes the agent's user scope: settings, hooks, skills, rules; dev-context/CLAUDE.md its user CLAUDE.md. */
export function populateClaudeConfig(paths: WorkPaths): void {
  const src = join(paths.context, '.claude');
  mkdirSync(paths.claudeConfig, { recursive: true });
  if (existsSync(join(src, 'settings.json'))) cpSync(join(src, 'settings.json'), join(paths.claudeConfig, 'settings.json'));
  for (const dir of ['skills', 'rules', 'hooks', 'agents', 'commands']) {
    if (existsSync(join(src, dir))) cpSync(join(src, dir), join(paths.claudeConfig, dir), { recursive: true });
  }
  if (existsSync(join(paths.context, 'CLAUDE.md'))) cpSync(join(paths.context, 'CLAUDE.md'), join(paths.claudeConfig, 'CLAUDE.md'));
  log.info(`CLAUDE_CONFIG_DIR ${paths.claudeConfig} populated from dev-context/.claude`);
}

export interface Target {
  repo: RepoEntry;
  dir: string;
  git: Git;
  notes: string[];
  claudeMd: string | null;
  caitDir: string | null;
}

export async function cloneTarget(
  req: CloneRequest,
  repos: Repos,
  repo: RepoEntry,
  env: RunnerEnv,
  paths: WorkPaths,
  signal?: AbortSignal,
): Promise<Target> {
  const genv = driverGitEnv(env, paths.home);
  const dir = join(paths.repos, repoDirName(repo));
  rmSync(dir, { recursive: true, force: true });
  await clone(req.repository.url, dir, genv, {
    branch: req.repository.default_branch,
    singleBranch: true,
    filter: repos.defaults.clone.filter,
    signal,
  });
  const git = new Git(dir, genv, signal);
  const claudeMdPath = join(dir, 'CLAUDE.md');
  const claudeMd = existsSync(claudeMdPath) ? readFileSync(claudeMdPath, 'utf8') : null;
  return { repo, dir, git, notes: [], claudeMd, caitDir: null };
}

/**
 * The coder role's view of what the prepare role laid out: the same Target, opened instead of
 * cloned. Its git has no credential (this role's environment holds none), which is enough for
 * everything the driver does after the agent - status, commit, diff, bundle - because all of it
 * reads objects prepare fetched or the agent created.
 */
export function openPrepared(
  repoUrl: string,
  h: { repo_dir: string | null; claude_md: string | null; notes: string[]; cait: boolean },
  env: RunnerEnv,
  paths: WorkPaths,
  signal?: AbortSignal,
): { repos: Repos; repo: RepoEntry; target: Target } {
  const repos = loadRepos(paths.context);
  const repo = enabledRepo(repos, repoUrl);
  if (!repo) throw new Error(NOT_ENABLED);
  const dir = join(paths.repos, repoDirName(repo));
  if (h.repo_dir !== repoDirName(repo) || !existsSync(join(dir, '.git'))) throw new Error(`the prepare role left no checkout at ${dir}`);
  const git = new Git(dir, driverGitEnv(env, paths.home), signal);
  return { repos, repo, target: { repo, dir, git, notes: [...h.notes], claudeMd: h.claude_md, caitDir: h.cait ? join(paths.repos, 'Cait') : null } };
}

/**
 * The plan analyses the commit the running image was built from. Since 2026-09-27 TR images are
 * tagged with it; fixtures use `<id>-<sha>`. A pre-change UUID tag falls back to HEAD, and says so.
 */
export async function checkoutAnalysedRef(target: Target, image: string | null, defaultBranch: string, knownSha?: string | null): Promise<string> {
  // investigate: Hephaisto may already have resolved the running commit; it wins over the tag
  const sha = knownSha && /^[0-9a-f]{40}$/.test(knownSha) ? knownSha : shaFromImage(image);
  if (!sha) {
    const head = await target.git.head();
    target.notes.push(
      `The image tag of ${image ?? '(no image)'} is not a commit sha, so the analysis ran on ${defaultBranch} HEAD ${head}, which may differ from what is deployed.`,
    );
    return head;
  }
  if (!(await target.git.hasCommit(sha))) {
    const f = await target.git.try(['fetch', '--quiet', '--filter=blob:none', 'origin', sha]);
    if (f.code !== 0 || !(await target.git.hasCommit(sha))) {
      const head = await target.git.head();
      target.notes.push(`Commit ${sha} from the image tag could not be fetched, so the analysis ran on ${defaultBranch} HEAD ${head}.`);
      return head;
    }
  }
  await target.git.ok(['checkout', '--quiet', '--detach', sha]);
  log.info(`analysing ${sha} (from image tag)`);
  return sha;
}

/**
 * The target's own Claude settings and MCP config are ignored by construction (settingSources
 * ['user']) and deleted anyway. skip-worktree keeps the deletion out of every later `git add -A`,
 * so it can never become part of the fix.
 */
export async function sanitizeTarget(target: Target): Promise<string[]> {
  const removed: string[] = [];
  const claudeDir = join(target.dir, '.claude');
  const candidates = ['.mcp.json'];
  if (existsSync(claudeDir)) {
    for (const f of readdirSync(claudeDir)) if (/^settings.*\.json$/.test(f)) candidates.push(`.claude/${f}`);
  }
  for (const rel of candidates) {
    const abs = join(target.dir, rel);
    if (!existsSync(abs)) continue;
    rmSync(abs, { force: true });
    removed.push(rel);
    const tracked = await target.git.try(['ls-files', '--error-unmatch', '--', rel]);
    if (tracked.code === 0) await target.git.ok(['update-index', '--skip-worktree', '--', rel]);
  }
  if (removed.length > 0) log.info(`sanitizeTarget removed ${removed.join(', ')} from the target clone`);
  else log.info('sanitizeTarget: no .claude/settings*.json or .mcp.json in the target');
  return removed;
}

// --- Cait ----------------------------------------------------------------------------------

export function findCaitVersion(repoDir: string, projectFile?: string): string | null {
  const files: string[] = [];
  if (projectFile && existsSync(join(repoDir, projectFile))) files.push(join(repoDir, projectFile));
  const walk = (d: string, depth: number) => {
    if (depth > 4) return;
    for (const e of readdirSync(d, { withFileTypes: true })) {
      if (e.name === '.git' || e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules') continue;
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p, depth + 1);
      else if (/\.csproj$|^Directory\.Packages\.props$/.test(e.name)) files.push(p);
    }
  };
  walk(repoDir, 0);
  const patterns = [
    /<Package(?:Reference|Version)\s+Include="Cait"\s+Version="([^"]+)"/,
    /<Package(?:Reference|Version)\s+Version="([^"]+)"\s+Include="Cait"/,
    /<PackageReference\s+Include="Cait"\s*>\s*<Version>([^<]+)<\/Version>/,
  ];
  for (const f of files) {
    const text = readFileSync(f, 'utf8');
    for (const re of patterns) {
      const m = re.exec(text);
      if (m) return m[1]!.trim();
    }
  }
  return null;
}

/**
 * Cait at exactly the pinned version, as a SIBLING at /work/repos/Cait: several test projects
 * reference ../../Cait/Cait.csproj unconditionally and some repos prefer ../Cait even in
 * Release, so a sibling is what makes them build - and the danger was only ever a NEWER Cait.
 * Nothing but the pinned commit is ever checked out there.
 */
export async function prepareCait(target: Target, targetUrl: string, repos: Repos, env: RunnerEnv, paths: WorkPaths, signal?: AbortSignal): Promise<void> {
  if (!target.repo.cait?.pinned) return;
  if (target.repo.name.toLowerCase() === 'cait') return;
  const version = findCaitVersion(target.dir, target.repo.projectFile);
  if (!version) {
    target.notes.push('repos.yaml says this repository pins Cait, but no Cait PackageReference version was found; Cait was not cloned.');
    return;
  }
  // repos.yaml deliberately lists no Cait entry (it is never a fix target); it lives next to the
  // target under the same owner.
  const listed = repos.repos.find((r) => r.name.toLowerCase() === 'cait');
  const caitUrl = listed?.url ?? targetUrl.replace(/\/[^/]+?(\.git)?\/?$/, '/Cait');
  const genv = driverGitEnv(env, paths.home);
  const dir = join(paths.repos, 'Cait');
  rmSync(dir, { recursive: true, force: true });
  try {
    await clone(caitUrl, dir, genv, { filter: repos.defaults.clone.filter, signal, branch: listed?.defaultBranch ?? 'main', singleBranch: true });
  } catch (e) {
    // Belt and braces only: a missing sibling is warning MSB9008 and Release still builds from the pinned package.
    rmSync(dir, { recursive: true, force: true });
    target.notes.push(`Cait ${version} could not be cloned from ${caitUrl} (${(e as Error).message.split('\n')[0]!.slice(0, 200)}); continuing without the reference copy.`);
    return;
  }
  const git = new Git(dir, genv, signal);
  const csprojs = (await git.ok(['ls-files', '*Cait.csproj'])).split('\n').filter((p) => /(^|\/)Cait\.csproj$/.test(p));
  const csproj = csprojs.sort((a, b) => a.length - b.length)[0];
  if (!csproj) {
    target.notes.push('The Cait repository has no Cait.csproj; the pinned version could not be located.');
    rmSync(dir, { recursive: true, force: true });
    return;
  }
  const needle = `<Version>${version}</Version>`;
  const escaped = needle.replace(/[.[\]*^$\\]/g, '\\$&');
  let pinned: string | null = null;
  try {
    if (repos.defaults.clone.filter === 'blob:none') {
      try {
        const fetched = await prefetchPathHistory(git, csproj);
        log.info(`Cait: fetched ${fetched} versions of ${csproj} in one request`);
      } catch (e) {
        // Not fatal: the search below still works, one request per version.
        log.warn(`Cait: the versions of ${csproj} could not be fetched together (${firstLine(e)}); the search fetches them one by one`);
      }
    }
    const shas = (await git.ok(['log', '--format=%H', `-G${escaped}`, '--', csproj])).split('\n').filter(Boolean);
    for (const sha of shas) {
      const content = await git.try(['show', `${sha}:${csproj}`]);
      if (content.code === 0 && content.stdout.includes(needle)) {
        pinned = sha;
        break;
      }
    }
  } catch (e) {
    // The same belt and braces as a clone that failed: the sibling is a convenience, and a plan
    // that dies looking for it is worse than a plan without it.
    rmSync(dir, { recursive: true, force: true });
    target.notes.push(`The Cait commit that sets ${needle} could not be looked up (${firstLine(e)}); continuing without the reference copy.`);
    return;
  }
  if (!pinned) {
    target.notes.push(`No Cait commit sets ${needle} in ${csproj}; Cait was not provided.`);
    rmSync(dir, { recursive: true, force: true });
    return;
  }
  await git.ok(['checkout', '--quiet', '--detach', pinned]);
  const ref = join(paths.ref, 'Cait');
  rmSync(ref, { force: true, recursive: true });
  symlinkSync(dir, ref);
  target.caitDir = dir;
  log.info(`Cait ${version} = ${pinned} at ${dir} (linked from ${ref})`);
}

function firstLine(e: unknown): string {
  return ((e as Error).message ?? String(e)).split('\n')[0]!.slice(0, 200);
}

/**
 * Every version of one file, in one request.
 *
 * A blobless clone holds the history without the file contents, and git fetches a missing one the
 * moment something reads it - one request each. `git log -G` reads every version of the file it
 * is given, so on Cait, whose project file changes with every release, that was 974 requests: the
 * search ran into its ten-minute limit and the plan failed with it, on every code fix production
 * ever started (2026-10-02, -05 and -06).
 *
 * The ids of those versions are in the trees, which the clone has. So they are listed without
 * reading a file and asked for together, the way git itself asks for a missing object: measured
 * against github.com, 126 versions in 2 s, and no request during the search that followed.
 */
export async function prefetchPathHistory(git: Git, path: string): Promise<number> {
  return prefetchHistoryBlobs(git, ['--', path]);
}

/**
 * The same, for whatever `git log <revArgs>` walks: every blob those commits touch, in one
 * request, capped at `maxBlobs` (newest commits first). The review (#312) fetches its recent
 * history this way in the prepare role, so `git show` and `git log -p` answer in the coder
 * role, which has no credential to fetch a missing blob with.
 */
export async function prefetchHistoryBlobs(git: Git, revArgs: string[], maxBlobs = 20_000): Promise<number> {
  const raw = await git.ok(['log', '--no-renames', '--format=', '--raw', '--no-abbrev', ...revArgs]);
  const oids = new Set<string>();
  for (const line of raw.split('\n')) {
    // :100644 100644 <before> <after> M\t<path>
    if (!line.startsWith(':')) continue;
    const [beforeMode, afterMode, before, after] = line.split(/\s+/);
    // a submodule's entry (mode 160000) names a commit of another repository, not a blob of this one
    for (const [mode, oid] of [[beforeMode?.slice(1), before], [afterMode, after]] as const) {
      if (mode === '160000') continue;
      if (oid && /^[0-9a-f]{40,64}$/.test(oid) && !/^0+$/.test(oid) && oids.size < maxBlobs) oids.add(oid);
    }
  }
  if (oids.size === 0) return 0;
  await git.ok(
    ['-c', 'fetch.negotiationAlgorithm=noop', 'fetch', '--quiet', '--no-tags', '--no-write-fetch-head', '--recurse-submodules=no', '--filter=blob:none', '--stdin', 'origin'],
    { input: [...oids].join('\n') + '\n' },
  );
  return oids.size;
}

// --- NuGet -----------------------------------------------------------------------------------

export const GITHUB_NUGET_SOURCE = 'https://nuget.pkg.github.com/TrueRelevance/index.json';

/**
 * The TR repos' own nuget.config reads %username% / %token% for the GitHub Packages source, so
 * the driver sets those two variables in its restore/build/test children and nowhere else. A
 * repository without a nuget.config gets one ONE LEVEL UP, at /work/repos/nuget.config - found
 * by NuGet's directory walk, and outside the repo so it can never be committed. No token
 * literal is ever written to disk.
 */
export function ensureNugetConfig(target: Target, paths: WorkPaths): string | null {
  if (target.repo.stack !== 'dotnet') return null;
  const own = readdirSync(target.dir).find((f) => f.toLowerCase() === 'nuget.config');
  if (own) return join(target.dir, own);
  const p = join(paths.repos, 'nuget.config');
  writeFileSync(
    p,
    `<?xml version="1.0" encoding="utf-8"?>
<!-- Written by hephaisto-coder for a repository without its own nuget.config. The credential
     is the %token% environment variable of the driver's restore process; nothing secret is here. -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="github" value="${GITHUB_NUGET_SOURCE}" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="%username%" />
      <add key="ClearTextPassword" value="%token%" />
    </github>
  </packageSourceCredentials>
</configuration>
`,
  );
  log.info(`no nuget.config in the repository; wrote ${p} (placeholders only)`);
  return p;
}

/**
 * The two variables the TR nuget.config files expand. Driver children only, and only where the
 * token is: in the prepare role's pre-restore. In the coder role this is empty, the placeholders
 * stay unexpanded, and a feed that is asked answers 401 - verify.ts feedRefusal() names that.
 */
export function nugetCredentialEnv(env: RunnerEnv): Record<string, string> {
  return env.nugetToken ? { username: 'x-access-token', token: env.nugetToken } : {};
}
