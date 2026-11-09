import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { knownRed, open, settle } from './helpers';

// The weekly backlog review (#312): once a week every open issue of the planner's organisation -
// on the board or not, in an archived repository or not - gets a verdict from a read-only coder
// Job of phase `review`: can be closed (and why), a real bug, still to build, or unsure. A person
// agrees with a verdict or moves it, and closing what was agreed is one click.
//
// THE RUN PROPOSES AND WRITES NOTHING TO GITHUB. Every write - a close, a Priority - is a
// person's click through PlannerService. So the stand-in's record of writes is read before and
// after each step: empty after a run, exactly the click's after a click.
//
// Against the stand-in only (GITHUB_STANDIN_URL), with the scripted coder: the verdicts are
// coder/fake-scripts/default.review.json, read here for what to expect, and the organisation is
// the stand-in's acme (infra/e2e/notification-receiver/GitHubStandIn.Review.cs). Each test's id
// is the first word of its title: scripts/e2e/ui/KNOWN_RED lists the ones whose code has not
// landed yet. The tests are serial and build on each other: R01 runs the review, R07 runs it again.

const STANDIN = process.env.GITHUB_STANDIN_URL?.replace(/\/$/, '');
const NAME = 'planner-e2e';
const ORG = 'acme';

/** The seeded issues each test acts on, by what makes them interesting. */
const ISSUES = {
  doneByPull: 'acme/api#431',          // on the board; its closing pull request acme/api#433 is merged
  doneOffBoard: 'acme/web#160',        // never on the board; merged acme/web#161 says "Follow-up to", not "Closes"
  doneByCommit: 'acme/infra#44',       // a commit on main references it, no pull request
  doneBySubIssues: 'acme/planning#9',  // its only sub-issue is closed
  archived: 'acme/legacy-ttd#1',       // its repository is archived
  bug: 'acme/web#118',                 // opened before the board; the one that changes between the two runs
  bugOnBoard: 'acme/api#475',          // on the board with Priority empty: agreeing sets P1 there
  feature: 'acme/api#205',             // never on the board
  unsure: 'acme/planning#30',          // low confidence: a person moves it to the bugs
};

type Tab = 'close' | 'bug' | 'feature' | 'unsure';

/** One verdict of the script, in the contract's words (review-result, dev-context). */
type Scripted = {
  issue: string; group: 'close' | 'bug' | 'feature'; close_reason: string | null; related: string | null;
  confidence: 'high' | 'medium' | 'low'; summary: string; reason: string; evidence: string[]; priority: string | null;
};

const SCRIPT: Scripted[] = JSON.parse(readFileSync(join(__dirname, '..', '..', '..', '..', 'coder', 'fake-scripts', 'default.review.json'), 'utf8'))
  .steps.find((s: { result?: unknown }) => s.result).result.structured_output.verdicts;

const scripted = (reference: string) => {
  const found = SCRIPT.find(v => v.issue === reference);
  expect(found, `${reference} has no verdict in coder/fake-scripts/default.review.json`).toBeTruthy();
  return found!;
};

/** Where a verdict is shown: a low confidence is a person's to decide, whatever the group. */
const tabOf = (v: Scripted): Tab => (v.confidence === 'low' ? 'unsure' : v.group);

/** What GET /api/planner/review answers (stages 3 and 4). */
type Evidence = { kind: string; text: string; ok: boolean | null };
type Decision = { by: string; at: string; group: string; closeReason: string | null; agreed: boolean; unsure: boolean };
type Verdict = {
  repository: string; number: number; title: string; url: string; onBoard: boolean; archived: boolean;
  group: 'close' | 'bug' | 'feature'; closeReason: string | null; related: string | null; confidence: string;
  summary: string; reason: string; evidence: Evidence[]; priority: string | null;
  tab: Tab; decision: Decision | null; reviewedAt: string; runId: string;
};
type Counts = Record<Tab, number>;
type Run = {
  id: string; trigger: 'schedule' | 'manual'; state: 'Running' | 'Finished' | 'Failed'; problem: string | null;
  startedAt: string; finishedAt: string | null; open: number; reviewed: string[]; kept: number; jobs: number; costUsd: number; counts: Counts;
};
type Review = { state: 'Off' | 'Ready' | 'Running'; problem: string | null; run: Run | null; nextRunAt: string | null; pending: number; verdicts: Verdict[] };

type Write = { seq: number; method: string; path: string; operation: string | null; issue: string | null; status: number; variables?: Record<string, unknown> };
type StandInIssue = { repository: string; number: number; state: string; state_reason: string | null };

const ref = (i: { repository: string; number: number }) => `${i.repository}#${i.number}`;

/** The review as the agent has it. A missing endpoint fails here, at once: that is the feature missing. */
async function review(request: APIRequestContext): Promise<Review> {
  const res = await request.get('/api/planner/review');
  expect(res.status(), 'GET /api/planner/review: the weekly review is not served by this agent').toBe(200);
  return res.json();
}

/**
 * The review, or a skip that says why it cannot be tried here. Every test starts with it, so that
 * an agent without the feature fails each at once, and an install that has it switched off skips.
 */
async function ready(request: APIRequestContext): Promise<Review> {
  const r = await review(request);
  test.skip(r.state === 'Off', `PRECONDITION: the weekly review is off on this install (${r.problem ?? 'github.planner.review.enabled'})`);
  test.skip(!STANDIN, 'PRECONDITION: GITHUB_STANDIN_URL is not set, so nothing could say what the run read or wrote');
  return r;
}

function verdict(r: Review, reference: string): Verdict {
  const found = r.verdicts.find(v => ref(v) === reference);
  expect(found, `${reference} has no verdict`).toBeTruthy();
  return found!;
}

/**
 * A run, started the way the schedule starts one (ReviewRunner, trigger manual), and waited for.
 * The Jobs are the scripted coder's: seconds each, but a Job has to be scheduled first.
 */
async function runReview(request: APIRequestContext): Promise<Run> {
  const res = await request.post('/api/planner/review/run', { data: { decidedBy: NAME } });
  expect(res.status(), await res.text()).toBe(202);
  const { runId } = await res.json();
  expect(runId).toBeTruthy();

  await expect.poll(async () => (await review(request)).run, { timeout: 300_000, intervals: [2_000] })
    .toMatchObject({ id: runId, state: expect.stringMatching(/^(Finished|Failed)$/) });

  const run = (await review(request)).run!;
  expect(run.state, run.problem ?? '').toBe('Finished');
  expect(run.trigger).toBe('manual');
  return run;
}

/** Every write the stand-in saw since `since`, in the organisation or through GraphQL. */
async function writesSince(request: APIRequestContext, since: number): Promise<Write[]> {
  const res = await request.get(`${STANDIN}/github/control/writes?since=${since}&owner=${ORG}`);
  expect(res.ok()).toBeTruthy();
  return (await res.json()).writes;
}

async function writeSeq(request: APIRequestContext): Promise<number> {
  return (await (await request.get(`${STANDIN}/github/control/writes`)).json()).seq;
}

/** Every open issue of the organisation, as the stand-in holds them. */
async function openInOrg(request: APIRequestContext): Promise<string[]> {
  const state = await (await request.get(`${STANDIN}/github/control/state`)).json();
  return state.issues
    .filter((i: StandInIssue) => i.repository.startsWith(`${ORG}/`) && i.state === 'open')
    .map(ref).sort();
}

async function standInIssue(request: APIRequestContext, reference: string): Promise<StandInIssue> {
  const state = await (await request.get(`${STANDIN}/github/control/state`)).json();
  const found = state.issues.find((i: StandInIssue) => ref(i) === reference);
  expect(found, `${reference} is not on the stand-in`).toBeTruthy();
  return found;
}

async function commentsOn(request: APIRequestContext, reference: string): Promise<{ body: string; user: { login: string } }[]> {
  const all = await (await request.get(`${STANDIN}/github/control/comments`)).json();
  return all.filter((c: { repository: string; number: number }) => ref(c) === reference);
}

type TeamsMessage = { id: string; text: string; activity: unknown };

async function teamsMessages(request: APIRequestContext): Promise<TeamsMessage[]> {
  const res = await request.get(`${STANDIN}/teams/messages`);
  expect(res.ok()).toBeTruthy();
  return (await res.json()).messages;
}

/** The page, with a name in place - which is also the proof that the circuit is live. */
async function openReview(page: Page) {
  await open(page, '/planner/review');
  await expect(page.locator('h1')).toContainText('Weekly review');
  // Until the circuit is up every button is disabled: an enabled one means a click is heard.
  await expect(page.getByTestId('review-tab-close')).toBeEnabled();
}

async function showTab(page: Page, tab: Tab) {
  await settle(async () => {
    await page.getByTestId(`review-tab-${tab}`).click();
    await expect(page.getByTestId(`review-tab-${tab}`)).toHaveAttribute('aria-selected', 'true', { timeout: 3_000 });
  });
}

const card = (page: Page, reference: string) => page.locator(`[data-testid="review-card"][data-reference="${reference}"]`);

/** What the page said about the last click. */
async function said(page: Page, text: string | RegExp, ok = true) {
  const message = page.getByTestId('review-message');
  await expect(message).toContainText(text);
  await expect(message).toHaveAttribute('data-ok', ok ? 'true' : 'false');
}

// Shared by the serial tests: what the first run left, and where the records stood before it.
let first: Run | undefined;
let second: Run | undefined;
let teamsBefore = 0;

test.describe.configure({ mode: 'serial' });

test.describe('weekly review', () => {
  test.beforeEach(async ({ page }, info) => {
    knownRed(info);
    await page.addInitScript(name => window.localStorage.setItem('hephaisto.feedback.name', name), NAME);
  });

  test('R01 a run reviews every open issue of the organisation, on the board and off it, and writes nothing to GitHub', async ({ request }) => {
    await ready(request);

    // The seeded organisation again. Every seeded time is relative to the reset, so to the
    // incremental run every issue has changed since any earlier verdict: this run looks at all.
    expect((await request.post(`${STANDIN}/github/control/project/reset`)).status()).toBe(204);

    const writes = await writeSeq(request);
    const reads = (await (await request.get(`${STANDIN}/github/control/requests`)).json()).at(-1)?.seq ?? 0;
    teamsBefore = Math.max(0, ...(await teamsMessages(request)).map(m => Number(m.id)));

    first = await runReview(request);
    const after = await review(request);
    const open = await openInOrg(request);

    // Everything open in the organisation, and the script answers every seeded issue.
    for (const v of SCRIPT) {
      expect(open, `${v.issue} is seeded open on the stand-in`).toContain(v.issue);
      expect(first.reviewed, `${v.issue} was looked at by the first run`).toContain(v.issue);
    }

    expect(first.reviewed.every(r => open.includes(r)), 'only open issues of the organisation are reviewed').toBe(true);
    expect(first.open).toBe(open.length);
    expect(first.jobs).toBeGreaterThan(0);

    // Each verdict is the script's, where it is shown is its confidence's, and nothing is decided yet.
    for (const s of SCRIPT) {
      const v = verdict(after, s.issue);
      expect(v, s.issue).toMatchObject({
        group: s.group, closeReason: s.close_reason, related: s.related, confidence: s.confidence,
        summary: s.summary, reason: s.reason, priority: s.priority, tab: tabOf(s), decision: null, runId: first.id,
      });
    }

    // The facts are GitHub's, gathered before the model: on the board or not, archived or not.
    expect(verdict(after, ISSUES.doneByPull).onBoard).toBe(true);
    expect(verdict(after, ISSUES.doneOffBoard).onBoard).toBe(false);
    expect(verdict(after, ISSUES.archived).archived).toBe(true);
    expect(verdict(after, ISSUES.bug).archived).toBe(false);

    // The counts are the verdicts', and the badge is what waits for a person.
    const counts = (t: Tab) => after.verdicts.filter(v => v.tab === t).length;
    expect(first.counts).toEqual({ close: counts('close'), bug: counts('bug'), feature: counts('feature'), unsure: counts('unsure') });
    expect(after.pending).toBe(after.verdicts.length);

    // It READ the organisation - the search, and the timeline of an issue no board holds ...
    const asked = ((await (await request.get(`${STANDIN}/github/control/requests`)).json()) as { seq: number; method: string; path: string; query: string | null }[])
      .filter(r => r.seq > reads && r.method === 'GET');
    expect(asked.some(r => r.path === '/search/issues' && decodeURIComponent((r.query ?? '').replace(/\+/g, ' ')).includes(`org:${ORG}`)),
      'the run lists the organisation\'s open issues through the search').toBe(true);
    expect(asked.some(r => r.path === '/repos/acme/web/issues/160/timeline'), 'the run reads the timeline of an issue that is not on the board').toBe(true);
    expect(asked.some(r => r.path === '/repos/acme/legacy-ttd'), 'the run asks whether a repository is archived').toBe(true);

    // ... and wrote NOTHING: no comment, no label, no field, no close.
    expect(await writesSince(request, writes), 'the weekly run proposes only').toEqual([]);
  });

  test('R02 the page shows the run and four tabs, each issue under its group, the close cards by their reason and with their evidence', async ({ page }) => {
    const r = await ready(page.request);
    expect(first, 'R01 ran the review').toBeTruthy();

    await open(page, '/');
    const nav = page.getByTestId('nav-review');
    await expect(nav).toBeVisible();
    await expect(nav).toHaveAttribute('href', /planner\/review$/);
    await expect(page.getByTestId('nav-review-badge')).toHaveText(String(r.pending));

    await openReview(page);
    await expect(page.getByTestId('review-run')).toHaveAttribute('data-run', first!.id);
    await expect(page.getByTestId('review-run-reviewed')).toContainText(String(first!.reviewed.length));

    for (const t of ['close', 'bug', 'feature', 'unsure'] as Tab[]) {
      await expect(page.getByTestId(`review-tab-${t}`)).toHaveAttribute('data-count', String(first!.counts[t]));
      await showTab(page, t);

      const expected = r.verdicts.filter(v => v.tab === t).map(ref).sort();
      await expect.poll(async () => (await page.getByTestId('review-card').evaluateAll(
        cards => cards.map(c => c.getAttribute('data-reference') ?? ''))).sort()).toEqual(expected);
    }

    // Close cards are grouped by why they can be closed.
    await showTab(page, 'close');
    const group = (reason: string) => page.locator(`[data-testid="review-close-group"][data-reason="${reason}"]`);
    for (const reference of [ISSUES.doneByPull, ISSUES.doneOffBoard, ISSUES.doneByCommit, ISSUES.doneBySubIssues]) {
      await expect(group('done').locator(`[data-testid="review-card"][data-reference="${reference}"]`)).toHaveCount(1);
    }
    await expect(group('obsolete').locator(`[data-testid="review-card"][data-reference="${ISSUES.archived}"]`)).toHaveCount(1);

    // The reason is the verdict's own words, as text; the chips are the evidence, a fact each.
    const chips = (reference: string) => card(page, reference).getByTestId('review-evidence');
    await expect(card(page, ISSUES.doneByPull).getByTestId('review-card-reason')).toContainText(scripted(ISSUES.doneByPull).reason);
    await expect(card(page, ISSUES.doneByPull).getByTestId('review-card-reason').locator('a, b, script, img')).toHaveCount(0);
    await expect(chips(ISSUES.doneByPull).filter({ hasText: 'acme/api#433' }).filter({ hasText: /merged/i })).toHaveCount(1);
    await expect(chips(ISSUES.doneOffBoard).filter({ hasText: 'acme/web#161' }).filter({ hasText: /merged/i })).toHaveCount(1);
    await expect(chips(ISSUES.doneByCommit).filter({ hasText: /a6ac132/ })).toHaveCount(1);
    await expect(chips(ISSUES.doneBySubIssues).filter({ hasText: /sub-issue/i })).toHaveCount(1);
    await expect(chips(ISSUES.archived).filter({ hasText: /archived/i })).toHaveCount(1);
    await expect(card(page, ISSUES.doneOffBoard).getByTestId('review-card-board')).toContainText(/not on the board/i);
  });

  test('R03 the card of an issue in an archived repository can be agreed with, offers no Close, and says why', async ({ page }) => {
    await ready(page.request);
    await openReview(page);
    await showTab(page, 'close');

    const archived = card(page, ISSUES.archived);
    await expect(archived.getByTestId('review-archived')).toContainText(/archived/i);
    await expect(archived.getByTestId('review-archived')).toContainText(/unarchived/i);
    await expect(archived.getByTestId('review-agree')).toBeEnabled();
    await expect(archived.getByTestId('review-close-one')).toHaveCount(0);

    // Every other close card is not in an archived repository and says nothing of the kind.
    await expect(card(page, ISSUES.doneByPull).getByTestId('review-archived')).toHaveCount(0);
  });

  test('R04 agreeing with a bug on the board sets the suggested priority there, as a click of the planner', async ({ page }) => {
    const r = await ready(page.request);
    const target = verdict(r, ISSUES.bugOnBoard);
    expect(target.priority).toBe('P1');

    const board = await (await page.request.get(`${STANDIN}/github/control/project`)).json();
    expect(board.items.find((i: { repository: string; number: number }) => ref(i) === ISSUES.bugOnBoard).fields.Priority, 'seeded without a priority').toBeUndefined();

    const since = await writeSeq(page.request);
    await openReview(page);
    await showTab(page, 'bug');

    const agree = card(page, ISSUES.bugOnBoard).getByTestId('review-agree');
    await expect(agree).toContainText('P1');
    await agree.click();
    await said(page, ISSUES.bugOnBoard);

    await expect.poll(async () => (await (await page.request.get(`${STANDIN}/github/control/project`)).json())
      .items.find((i: { repository: string; number: number }) => ref(i) === ISSUES.bugOnBoard).fields.Priority).toBe('P1');

    // The click's write and nothing else: one field set on the board.
    const written = await writesSince(page.request, since);
    expect(written.map(w => w.operation)).toEqual(['updateProjectV2ItemFieldValue']);

    await expect(card(page, ISSUES.bugOnBoard).getByTestId('review-decided')).toContainText(NAME);
    expect(verdict(await review(page.request), ISSUES.bugOnBoard).decision).toMatchObject({ by: NAME, agreed: true, group: 'bug' });
  });

  test('R05 moving a verdict puts the issue under the other group, in the person\'s name', async ({ page }) => {
    const r = await ready(page.request);
    expect(verdict(r, ISSUES.unsure).tab).toBe('unsure');

    await openReview(page);
    await showTab(page, 'unsure');
    await card(page, ISSUES.unsure).getByTestId('review-move-bug').click();
    await said(page, ISSUES.unsure);
    await expect(card(page, ISSUES.unsure)).toHaveCount(0);

    await showTab(page, 'bug');
    await expect(card(page, ISSUES.unsure).getByTestId('review-decided')).toContainText(NAME);

    const moved = verdict(await review(page.request), ISSUES.unsure);
    expect(moved).toMatchObject({ tab: 'bug', decision: { by: NAME, group: 'bug', agreed: false } });
    await expect(page.getByTestId('review-tab-unsure')).toHaveAttribute('data-count', String((await review(page.request)).verdicts.filter(v => v.tab === 'unsure').length));
  });

  test('R06 Close N issues closes the agreed ones that can be closed, each with a comment that carries its reason, and leaves the archived one open', async ({ page }) => {
    await ready(page.request);
    await openReview(page);
    await showTab(page, 'close');

    for (const reference of [ISSUES.doneByPull, ISSUES.doneOffBoard, ISSUES.archived]) {
      await card(page, reference).getByTestId('review-agree').click();
      await said(page, reference);
      await expect(card(page, reference).getByTestId('review-agree')).toHaveAttribute('aria-pressed', 'true');
    }

    // Three agreed, two that can be closed: the archived one is counted out, and the bar says so.
    const close = page.getByTestId('review-close-agreed');
    await expect(close).toHaveText(/Close 2 issues/);
    await expect(page.getByTestId('review-apply-note')).toContainText(/archived/i);

    const since = await writeSeq(page.request);
    await close.click();
    await said(page, /Closed 2/);

    for (const reference of [ISSUES.doneByPull, ISSUES.doneOffBoard]) {
      await expect.poll(async () => standInIssue(page.request, reference)).toMatchObject({ state: 'closed', state_reason: 'completed' });

      const fromAgent = (await commentsOn(page.request, reference)).filter(c => c.user.login === 'hephaisto-bot');
      expect(fromAgent, `${reference} got one comment from the agent's account`).toHaveLength(1);
      expect(fromAgent[0].body).toContain(scripted(reference).reason);
      expect(fromAgent[0].body).toContain(NAME);
    }

    // Nothing was tried on the archived one, and nothing on the close cards nobody agreed with.
    expect(await standInIssue(page.request, ISSUES.archived)).toMatchObject({ state: 'open' });
    expect(await commentsOn(page.request, ISSUES.archived)).toEqual([]);

    const written = await writesSince(page.request, since);
    expect(written.filter(w => w.issue === ISSUES.archived), 'no write was even tried on the archived repository').toEqual([]);
    expect([...new Set(written.map(w => w.issue).filter(Boolean))].sort(), 'the click wrote to the two issues it closed and to no other')
      .toEqual([ISSUES.doneByPull, ISSUES.doneOffBoard].sort());
    for (const untouched of [ISSUES.doneByCommit, ISSUES.doneBySubIssues]) {
      expect(await standInIssue(page.request, untouched)).toMatchObject({ state: 'open' });
    }

    // A closed issue is no longer open, so it is no longer in the review.
    const after = await review(page.request);
    expect(after.verdicts.map(ref)).not.toContain(ISSUES.doneByPull);
    expect(after.verdicts.map(ref)).not.toContain(ISSUES.doneOffBoard);
    await expect(card(page, ISSUES.doneByPull)).toHaveCount(0);
    expect(verdict(after, ISSUES.archived).decision).toMatchObject({ by: NAME, agreed: true });
  });

  test('R07 the next run looks again only at the issue that changed, writes nothing, and a person\'s verdict sticks', async ({ request }) => {
    await ready(request);
    expect(first, 'R01 ran the review').toBeTruthy();

    // Somebody says the bug is still there: the one change in the organisation since the first run.
    const comment = await request.post(`${STANDIN}/github/control/repos/acme/web/issues/118/comments`, {
      data: { body: 'Still happens after the last release.', login: 'reporter', id: 3003 },
    });
    expect(comment.status()).toBe(201);

    const before = verdict(await review(request), ISSUES.bug);
    const writes = await writeSeq(request);

    second = await runReview(request);
    expect(second.id).not.toBe(first!.id);
    expect(second.reviewed).toEqual([ISSUES.bug]);
    expect(second.kept).toBe((await openInOrg(request)).length - 1);
    expect(await writesSince(request, writes), 'the weekly run proposes only').toEqual([]);

    const after = await review(request);
    expect(Date.parse(verdict(after, ISSUES.bug).reviewedAt)).toBeGreaterThan(Date.parse(before.reviewedAt));
    expect(verdict(after, ISSUES.bug).runId).toBe(second.id);
    expect(verdict(after, ISSUES.feature).runId, 'an unchanged issue keeps the verdict it had').toBe(first!.id);

    // What people decided is still theirs.
    expect(verdict(after, ISSUES.unsure)).toMatchObject({ tab: 'bug', decision: { by: NAME, group: 'bug' } });
    expect(verdict(after, ISSUES.bugOnBoard).decision).toMatchObject({ by: NAME, agreed: true });
    expect(verdict(after, ISSUES.archived).decision).toMatchObject({ by: NAME, agreed: true });
  });

  test('R08 every finished run posts one message to Teams, with its counts and a link to the review', async ({ request }) => {
    await ready(request);
    expect(first && second, 'R01 and R07 ran the review twice').toBeTruthy();

    const posted = (await teamsMessages(request))
      .filter(m => Number(m.id) > teamsBefore && /weekly review/i.test(m.text));
    expect(posted, 'one message per finished run').toHaveLength(2);

    for (const [message, run] of [[posted[0], first!], [posted[1], second!]] as const) {
      expect(message.text).toMatch(new RegExp(`\\b${run.counts.close} to close\\b`));
      expect(message.text).toMatch(new RegExp(`\\b${run.counts.bug} bugs?\\b`));
      expect(message.text).toMatch(new RegExp(`\\b${run.counts.feature} to build\\b`));
      expect(JSON.stringify(message.activity)).toContain('/planner/review');
    }
  });
});
