import { execFileSync } from 'node:child_process';
import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { open, settle } from './helpers';

// The planner (v0.15.0): one GitHub project's open issues - walked one at a time, the day's plan,
// and everything that is open under its top-level issue.
//
// EVERY BUTTON IS PRESSED, AND WHAT IT DID IS READ ON THE OTHER SIDE. The page is checked against
// /api/planner, never against itself, and a click is believed only when the board says so:
//
//   - the stand-in's board   GITHUB_STANDIN_URL=http://<host>:8110   (the default tier; $0)
//   - github.com             PLANNER_E2E_LIVE=1, read with the `gh` of whoever runs the suite
//                            (scripts/e2e/planner-live.sh makes the issues and names them)
//
// With neither, only the first spec runs - the page must say whether the planner is on - and the
// rest skip with a PRECONDITION marker.
//
// The specs are serial and each uses an issue of its own (ITEMS below), because two of them
// close what they touch. Against the stand-in the board is seeded anew before the first one.

const STANDIN = process.env.GITHUB_STANDIN_URL?.replace(/\/$/, '');
const LIVE = process.env.PLANNER_E2E_LIVE === '1';

/** Which issue each spec presses buttons on, by reference. The stand-in's seeded board unless the live runner says otherwise. */
const ITEMS: Record<string, string> = {
  fields: 'acme/billing#88',
  day: 'acme/planning#21',
  carried: 'acme/scraper#57',
  closeDone: 'acme/api#431',
  closeNotPlanned: 'acme/web#240',
  closeFromToday: 'acme/api#440',
  assign: 'acme/planning#17',
  row: 'acme/planning#9',
  ...JSON.parse(process.env.PLANNER_E2E_ITEMS ?? '{}'),
};

const NAME = 'planner-e2e';

type Pull = { repository: string; number: number; state: string };

type Item = {
  itemId: string; repository: string; number: number; title: string; url: string; type: string | null;
  reason: string; emptyFields: string[]; day: string; carriedOver: boolean;
  fields: Record<string, string>; assignees: string[]; parentUrl: string | null;
  subIssues: number; subIssuesDone: number; pullRequests: Pull[];
  body: string | null; comments: number; labels: string[]; createdAt: string;
};

/** The newest walks' order: by when the issue was opened, newest first, then by reference. */
const byNewest = (a: Item, b: Item) =>
  Date.parse(b.createdAt) - Date.parse(a.createdAt) || a.repository.localeCompare(b.repository) || b.number - a.number;

type Planner = {
  state: string; problem: string | null; title: string | null;
  fields: { id: string; name: string; options: { id: string; name: string }[] }[];
  assignees: string[]; items: Item[];
  closedToday: { itemId: string; repository: string; number: number }[];
  repositories: string[]; issueTypes: string[]; triage: boolean;
};

/** What the stand-in's control says of an issue beyond the board's own fields. */
async function standInIssue(request: APIRequestContext, reference: string): Promise<{ title: string; type: string | null; author: string }> {
  const found = (await (await request.get(`${STANDIN}/github/control/project`)).json()).items
    .find((i: { repository: string; number: number }) => ref(i) === reference);
  expect(found, `${reference} is not on the stand-in's board`).toBeTruthy();
  return found;
}

/** The label the scripted triage answers: the first of the repository's own (coder/fake-scripts/default.triage.json). */
const FAKE_LABEL = 'bug';

/** What the other side says about one issue. */
type Truth = { state: string; closedAs: string | null; fields: Record<string, string>; labels: string[]; assignees: string[] };

const ref = (i: { repository: string; number: number }) => `${i.repository}#${i.number}`;

async function planner(request: APIRequestContext, refresh = false): Promise<Planner> {
  const res = await request.get(`/api/planner${refresh ? '?refresh=true' : ''}`);
  expect(res.ok()).toBeTruthy();
  return res.json();
}

async function item(request: APIRequestContext, role: string): Promise<Item> {
  const board = await planner(request);
  const found = board.items.find(i => ref(i) === ITEMS[role]);
  expect(found, `${ITEMS[role]} (${role}) is not among the board's open issues`).toBeTruthy();
  return found!;
}

/** The board's own account of an issue: the stand-in's control, or github.com through `gh`. */
async function truth(request: APIRequestContext, reference: string): Promise<Truth> {
  const [repository, number] = reference.split('#');

  if (!LIVE) {
    const res = await request.get(`${STANDIN}/github/control/project`);
    expect(res.ok()).toBeTruthy();
    const found = (await res.json()).items.find((i: { repository: string; number: number }) => ref(i) === reference);
    expect(found, `${reference} is not on the stand-in's board`).toBeTruthy();
    return { state: found.state, closedAs: found.closedAs, fields: found.fields, labels: found.labels, assignees: found.assignees };
  }

  const [owner, name] = repository.split('/');
  const out = execFileSync('gh', ['api', 'graphql', '-f', `owner=${owner}`, '-f', `name=${name}`, '-F', `number=${number}`, '-f', `query=
    query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { issue(number: $number) {
      state stateReason
      labels(first: 30) { nodes { name } }
      assignees(first: 10) { nodes { login } }
      projectItems(first: 5) { nodes { fieldValues(first: 20) { nodes {
        ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { name } } } } } } }
    } } }`], { encoding: 'utf8' });

  const issue = JSON.parse(out).data.repository.issue;
  const fields: Record<string, string> = {};

  for (const node of issue.projectItems.nodes.flatMap((p: { fieldValues: { nodes: { name?: string; field?: { name: string } }[] } }) => p.fieldValues.nodes)) {
    if (node.name && node.field?.name) fields[node.field.name] = node.name;
  }

  return {
    state: issue.state.toLowerCase(),
    closedAs: issue.stateReason,
    fields,
    labels: issue.labels.nodes.map((l: { name: string }) => l.name),
    assignees: issue.assignees.nodes.map((a: { login: string }) => a.login),
  };
}

/** The walk, opened on one item, with a name in place - which is also the proof that the circuit is live. */
async function openItem(page: Page, itemId: string) {
  await open(page, `/planner?item=${encodeURIComponent(itemId)}`);

  const card = page.getByTestId('planner-item');
  await expect(card).toHaveAttribute('data-item', itemId);

  // The remembered name arrives over the circuit, after the first interactive render. Until
  // then every button is disabled, so an enabled one means a click will be heard.
  await expect(card.getByTestId('planner-day').first()).toBeEnabled();

  return card;
}

/** What the page said about the last click. */
async function said(page: Page, text: string | RegExp, ok = true) {
  const message = page.getByTestId('planner-message');
  await expect(message).toContainText(text);
  await expect(message).toHaveAttribute('data-ok', ok ? 'true' : 'false');
}

test.describe.configure({ mode: 'serial' });

test.describe('planner', () => {
  test.beforeEach(async ({ page }) => {
    // The name every click is recorded under, where nobody is signed in. The same key the other
    // pages keep a typed name in.
    await page.addInitScript(name => window.localStorage.setItem('hephaisto.feedback.name', name), NAME);
  });

  test('the nav links to the page, which says whether a board is planned at all', async ({ page }) => {
    await open(page, '/');

    const link = page.getByTestId('nav-planner');
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute('href', /planner$/);

    await open(page, '/planner');
    await expect(page.locator('h1')).toContainText('planner');

    const board = await planner(page.request);
    const off = await page.getByTestId('planner-off').count();
    const on = await page.getByTestId('planner-views').count();

    expect(on + off, 'the page says either that a board is planned or that the planner is off').toBe(1);
    expect(off, `/api/planner says ${board.state}`).toBe(board.state === 'Off' ? 1 : 0);

    if (off === 1) {
      await expect(page.getByTestId('planner-off')).toContainText('github.planner.enabled');
    }
  });

  test.describe('with a board', () => {
    // Against github.com nothing is tried twice: a second attempt would start from the issues
    // the first one closed, and fail for that and nothing else.
    test.describe.configure({ mode: 'serial', retries: LIVE ? 0 : undefined });

    test.beforeAll(async ({ request }) => {
      const board = await planner(request);

      test.skip(board.state === 'Off', 'PRECONDITION: github.planner.enabled is not set on this install');
      test.skip(!LIVE && !STANDIN, 'PRECONDITION: neither GITHUB_STANDIN_URL nor PLANNER_E2E_LIVE=1 is set, so nothing could say what a click did');

      if (!LIVE) {
        // The seeded board again: two of the specs below close what they touch.
        expect((await request.post(`${STANDIN}/github/control/project/reset`)).status()).toBe(204);
      }

      const fresh = await planner(request, true);
      expect(fresh.state, fresh.problem ?? '').toBe('Read');
    });

    test('the walk goes through the open issues in the order the API lists them', async ({ page }) => {
      const board = await planner(page.request, true);
      const walked = board.items.slice(0, 12);

      await open(page, '/planner');
      await expect(page.getByTestId('planner-refresh')).toBeEnabled();
      await page.getByTestId('planner-refresh').click();

      const card = page.getByTestId('planner-item');
      await expect(card.getByTestId('planner-day').first()).toBeEnabled();

      for (let i = 0; i < walked.length; i++) {
        await expect(card).toHaveAttribute('data-reference', ref(walked[i]));
        await expect(page.getByTestId('planner-count')).toContainText(`${i + 1} of ${board.items.length}`);

        if (i + 1 < walked.length) {
          await expect(page.getByTestId('planner-then')).toContainText(ref(walked[i + 1]));
          await page.getByTestId('planner-next').click();
        }
      }

      await page.getByTestId('planner-previous').click();
      await expect(card).toHaveAttribute('data-reference', ref(walked[walked.length - 2]));
    });

    test('the newest walks go by when an issue was opened, the newest first, and can be linked', async ({ page }) => {
      const board = await planner(page.request, true);
      const newest = [...board.items].sort(byNewest);
      const unplanned = newest.filter(i => i.day === 'Undecided');
      expect(newest.length, 'a board to walk').toBeGreaterThan(2);

      await open(page, '/planner');
      const card = page.getByTestId('planner-item');
      await expect(card.getByTestId('planner-day').first()).toBeEnabled();

      // Over the circuit: the choice is heard once the newest issue is in front of the person.
      await settle(async () => {
        await page.getByTestId('planner-walk').selectOption('newest');
        await expect(card).toHaveAttribute('data-reference', ref(newest[0]), { timeout: 3_000 });
      });

      for (let i = 0; i < Math.min(5, newest.length); i++) {
        await expect(card).toHaveAttribute('data-reference', ref(newest[i]));
        await expect(page.getByTestId('planner-count')).toContainText(`${i + 1} of ${newest.length}`);

        if (i + 1 < Math.min(5, newest.length)) {
          await page.getByTestId('planner-next').click();
        }
      }

      // Only what has no day yet, again from the newest - wherever the walk before it stood.
      await page.getByTestId('planner-walk').selectOption('newest-unplanned');

      if (unplanned.length > 0) {
        await expect(card).toHaveAttribute('data-reference', ref(unplanned[0]));
        await expect(page.getByTestId('planner-count')).toContainText(`1 of ${unplanned.length}`);
        await expect(card).toHaveAttribute('data-day', 'Undecided');
      }

      // And the walk is in the address it can be opened with.
      await open(page, '/planner?walk=newest');
      await expect(page.getByTestId('planner-walk')).toHaveValue('newest');
      await expect(page.getByTestId('planner-item')).toHaveAttribute('data-reference', ref(newest[0]));
    });

    test('each item says why it is there, and its title is text', async ({ page }) => {
      const board = await planner(page.request);
      const reasons = new Set<string>();

      for (const i of board.items.slice(0, 12)) {
        // One of each reason is enough; the walk above has been through all of them.
        if (reasons.has(i.reason)) continue;
        reasons.add(i.reason);

        const card = await openItem(page, i.itemId);

        await expect(card).toHaveAttribute('data-reason', i.reason);
        await expect(card.getByTestId('planner-item-why')).toHaveCount(i.reason === 'None' ? 0 : 1);

        // Somebody else's text: exactly the stored string, and no element inside it.
        const title = card.getByTestId('planner-item-title');
        expect(await title.evaluate(e => e.textContent)).toBe(i.title);
        expect(await title.locator('*').count()).toBe(0);

        for (const field of board.fields) {
          const shown = card.locator(`[data-testid="planner-item-field"][data-field="${field.name}"]`);
          await expect(shown).toContainText(i.fields[field.name] ?? 'empty');
        }

        await expect(card.getByTestId('planner-item-pull')).toHaveCount(i.pullRequests.length);

        // A reason that suggests closing emphasises it, and says so.
        await expect(card.getByTestId('planner-suggested')).toHaveCount(['MergedStillOpen', 'SubIssuesDone'].includes(i.reason) ? 1 : 0);
      }

      if (!LIVE) {
        expect([...reasons].sort(), 'the seeded board holds one of each').toEqual(['FieldEmpty', 'MergedStillOpen', 'None', 'PullRequestQuiet', 'SubIssuesDone']);
      }
    });

    test('the issue\'s own text is shown as text, and can be put away for the whole walk', async ({ page }) => {
      const board = await planner(page.request);
      const written = board.items.slice(0, 30).find(i => i.body);
      const blank = board.items.slice(0, 30).find(i => !i.body);

      test.skip(!written, 'PRECONDITION: none of the first thirty open issues has a description');

      let card = await openItem(page, written!.itemId);

      // Markdown as its source: exactly the stored string, and no element made of it.
      const description = card.getByTestId('planner-item-description');
      expect(await description.evaluate(e => e.textContent)).toBe(written!.body);
      expect(await description.locator('*').count()).toBe(0);

      await expect(card.getByTestId('planner-item-comments')).toHaveText(
        written!.comments === 0 ? 'no comments' : written!.comments === 1 ? '1 comment' : `${written!.comments} comments`);

      // Its labels, without the two that are the day: those are a field of their own.
      const labels = written!.labels.filter(l => !['today', 'not today'].includes(l.toLowerCase()));
      expect(await card.getByTestId('planner-item-label').allTextContents()).toEqual(labels);

      // Put away, it stays away: on the next issue, and after the page was loaded again.
      await card.getByTestId('planner-description-toggle').click();
      await expect(card.getByTestId('planner-item-description')).toHaveCount(0);
      await expect(card.getByTestId('planner-description-toggle')).toHaveText('show description');

      card = await openItem(page, written!.itemId);
      await expect(card.getByTestId('planner-description-toggle')).toHaveText('show description');
      await expect(card.getByTestId('planner-item-description')).toHaveCount(0);

      await card.getByTestId('planner-description-toggle').click();
      await expect(card.getByTestId('planner-item-description')).toHaveCount(1);

      if (blank) {
        card = await openItem(page, blank.itemId);
        await expect(card.getByTestId('planner-item-description-none')).toContainText('Nobody wrote a description');
        await expect(card.getByTestId('planner-item-description')).toHaveCount(0);
      }
    });

    test('every option of every field can be set, and the board says so', async ({ page }) => {
      test.setTimeout(300_000);

      const board = await planner(page.request);
      const target = await item(page.request, 'fields');
      const card = await openItem(page, target.itemId);
      const pressed: string[] = [];
      const last: Record<string, string> = {};
      let closedBy: string | null = null;

      // Status last, and of every field its last option last: a board may close an issue whose
      // status reaches its last column (GitHub's own "auto-close issue" workflow does), and then
      // nothing more can be pressed on it. In this order that is the final click.
      const fields = [...board.fields.filter(f => f.name !== 'Status'), ...board.fields.filter(f => f.name === 'Status')];

      for (const field of fields) {
        const original = target.fields[field.name];
        const end = field.options[field.options.length - 1].name;
        const order = [
          ...field.options.filter(o => o.name !== original && o.name !== end),
          ...field.options.filter(o => o.name === original && o.name !== end),
          ...field.options.filter(o => o.name === end),
        ];

        for (const option of order) {
          const button = card.locator(`[data-testid="planner-option"][data-field="${field.name}"][data-option="${option.name}"]`);

          if ((await button.getAttribute('data-current')) !== 'true') {
            await button.click();
            await said(page, `${field.name} set to ${option.name}`);
            pressed.push(`${field.name}=${option.name}`);
          }

          last[field.name] = option.name;

          await expect(button).toHaveAttribute('data-current', 'true');
          await expect(card.locator(`[data-testid="planner-item-field"][data-field="${field.name}"]`)).toHaveText(option.name);
          await expect(card.locator(`[data-testid="planner-option"][data-field="${field.name}"][data-current="true"]`)).toHaveCount(1);

          await expect.poll(async () => (await truth(page.request, ref(target))).fields[field.name], { timeout: 30_000 }).toBe(option.name);
        }
      }

      const options = board.fields.reduce((n, f) => n + f.options.length, 0);
      expect(pressed.length, `pressed ${pressed.join(', ')}`).toBeGreaterThanOrEqual(options - board.fields.length);

      // And the API agrees with the page - or the board closed the issue when its status reached
      // the last column, and then it is among what was closed today.
      await expect.poll(async () => {
        const now = await truth(page.request, ref(target));
        const fresh = await planner(page.request, true);
        const open = fresh.items.find(i => ref(i) === ref(target));

        if (now.state === 'closed') {
          closedBy = `Status = ${last.Status}`;
          return fresh.closedToday.some(c => ref(c) === ref(target)) && !open;
        }

        return !!open && fields.every(f => open.fields[f.name] === last[f.name]);
      }, { timeout: 60_000, intervals: [3_000] }).toBe(true);

      test.info().annotations.push({ type: 'board', description: closedBy ? `the board closed the issue at ${closedBy}` : 'the board leaves an issue open whatever its status' });
    });

    test('today, not today, and taking the decision back are labels on the issue', async ({ page }) => {
      const target = await item(page.request, 'day');
      const count = (await planner(page.request)).items.length;
      let card = await openItem(page, target.itemId);

      await expect(card).toHaveAttribute('data-day', 'Undecided');
      await expect(card.getByTestId('planner-item-day')).toContainText('not decided yet');

      // Today goes on to the next item - when there is one.
      await card.locator('[data-testid="planner-day"][data-day="today"]').click();
      await said(page, `${ref(target)}: today`);
      await expect.poll(async () => (await truth(page.request, ref(target))).labels, { timeout: 30_000 }).toContain('today');

      if (count > 1) {
        await expect(page.getByTestId('planner-item')).not.toHaveAttribute('data-item', target.itemId);
      }

      await expect(page.locator(`[data-testid="planner-rail-row"][data-list="today"][data-item="${target.itemId}"]`)).toHaveCount(1);
      await expect(page.getByTestId('nav-planner-badge')).toBeVisible();

      card = await openItem(page, target.itemId);
      await expect(card).toHaveAttribute('data-day', 'Today');
      await expect(card.locator('[data-testid="planner-day"][data-day="today"]')).toHaveAttribute('data-current', 'true');

      // Not today takes "today" off and puts the other label on.
      await card.locator('[data-testid="planner-day"][data-day="not today"]').click();
      await said(page, `${ref(target)}: not today`);
      await expect.poll(async () => (await truth(page.request, ref(target))).labels.filter(l => l === 'today' || l === 'not today'), { timeout: 30_000 }).toEqual(['not today']);

      card = await openItem(page, target.itemId);
      await expect(card).toHaveAttribute('data-day', 'NotToday');
      await expect(page.locator(`[data-testid="planner-rail-row"][data-list="away"][data-item="${target.itemId}"]`)).toHaveCount(1);

      // The marked one, clicked again, takes the decision back.
      await card.locator('[data-testid="planner-day"][data-day="not today"]').click();
      await said(page, `${ref(target)}: not decided`);
      await expect(card).toHaveAttribute('data-day', 'Undecided');
      await expect.poll(async () => (await truth(page.request, ref(target))).labels.filter(l => l === 'today' || l === 'not today'), { timeout: 30_000 }).toEqual([]);

      expect((await item(page.request, 'day')).day).toBe('Undecided');
    });

    test('an item planned on an earlier day says so, and a click keeps it for today', async ({ page }) => {
      test.skip(LIVE, 'PRECONDITION: a label of an earlier day cannot be made on github.com; the stand-in seeds one');

      const target = await item(page.request, 'carried');
      expect(target.carriedOver).toBe(true);

      let card = await openItem(page, target.itemId);
      await expect(card.getByTestId('planner-item-day')).toContainText('from an earlier day');

      await card.locator('[data-testid="planner-day"][data-day="today"]').click();
      await said(page, `${ref(target)}: today`);

      const after = await item(page.request, 'carried');
      expect(after.day).toBe('Today');
      expect(after.carriedOver, 'the label was taken off and put on again, so it is dated today').toBe(false);
      expect((await truth(page.request, ref(target))).labels).toContain('today');

      card = await openItem(page, target.itemId);
      await expect(card.getByTestId('planner-item-day')).not.toContainText('from an earlier day');
    });

    test('an issue is assigned to every account the planner offers, and taken off again', async ({ page }) => {
      const board = await planner(page.request);
      const target = await item(page.request, 'assign');
      const card = await openItem(page, target.itemId);

      expect(board.assignees.length, 'the agent\'s own account, at least').toBeGreaterThan(0);

      for (const login of board.assignees) {
        const button = card.locator(`[data-testid="planner-assignee"][data-login="${login}"]`);
        await expect(button).toHaveAttribute('data-current', 'false');
        await button.click();

        if (!LIVE && login === 'stranger') {
          // GitHub answers 201 for an account it will not assign and leaves it out.
          await said(page, /not done.*may not be assigned/, false);
          await expect(button).toHaveAttribute('data-current', 'false');
          expect((await truth(page.request, ref(target))).assignees).not.toContain(login);
          continue;
        }

        await said(page, `assigned to ${login}`);
        await expect(button).toHaveAttribute('data-current', 'true');
        await expect(card.getByTestId('planner-item-assignees')).toContainText(login);
        await expect.poll(async () => (await truth(page.request, ref(target))).assignees.map(a => a.toLowerCase()), { timeout: 30_000 }).toContain(login.toLowerCase());

        await button.click();
        await said(page, `${login} taken off`);
        await expect(button).toHaveAttribute('data-current', 'false');
        await expect.poll(async () => (await truth(page.request, ref(target))).assignees.map(a => a.toLowerCase()), { timeout: 30_000 }).not.toContain(login.toLowerCase());
      }
    });

    test('the board view groups under the top-level issue, filters, and plans a row for today', async ({ page }) => {
      const board = await planner(page.request, true);
      const target = await item(page.request, 'row');

      await open(page, '/planner?view=all');
      await expect(page.getByTestId('planner-count')).toHaveText(`${board.items.length} open`);

      // Every open issue is a row or the head of a group, once.
      const heads = (await page.getByTestId('planner-group').evaluateAll(els => els.map(e => (e as HTMLElement).dataset.epic!))).filter(e => e !== 'none');

      // A group shows its first rows and a button for the rest. Pressed only once the circuit
      // is live - the row switches are enabled when the remembered name has arrived over it -
      // and until none is left, because a click into a page that is not interactive yet is lost.
      await expect(page.getByTestId('planner-row-day').first()).toBeEnabled();

      for (let left = await page.getByTestId('planner-group-more').count(); left > 0; left--) {
        await page.getByTestId('planner-group-more').first().click();
        await expect(page.getByTestId('planner-group-more')).toHaveCount(left - 1);
      }

      const rows = await page.getByTestId('planner-row').evaluateAll(els => els.map(e => (e as HTMLElement).dataset.reference!));

      expect([...heads, ...rows].sort()).toEqual(board.items.map(ref).sort());

      // A sub-issue sits under its parent, one step in.
      for (const i of board.items.filter(i => i.parentUrl && board.items.some(p => p.url === i.parentUrl))) {
        const parentIsHead = heads.some(h => board.items.find(p => ref(p) === h)?.url === i.parentUrl);
        await expect(page.locator(`[data-testid="planner-row"][data-reference="${ref(i)}"]`)).toHaveAttribute('data-depth', parentIsHead ? '0' : /[1-9]/);
      }

      // Today from a row, and back.
      const row = page.locator(`[data-testid="planner-row"][data-item="${target.itemId}"]`);
      const today = row.locator('[data-testid="planner-row-day"][data-day="today"]');
      await expect(today).toBeEnabled();
      await today.click();
      await said(page, `${ref(target)}: today`);
      await expect(row).toHaveAttribute('data-day', 'Today');
      await expect.poll(async () => (await truth(page.request, ref(target))).labels, { timeout: 30_000 }).toContain('today');

      const away = row.locator('[data-testid="planner-row-day"][data-day="not today"]');
      await away.click();
      await said(page, `${ref(target)}: not today`);
      await expect(row).toHaveAttribute('data-day', 'NotToday');

      await away.click();
      await said(page, `${ref(target)}: not decided`);
      await expect(row).toHaveAttribute('data-day', 'Undecided');
      await expect.poll(async () => (await truth(page.request, ref(target))).labels.filter(l => l === 'today' || l === 'not today'), { timeout: 30_000 }).toEqual([]);

      // A filter per field of the board.
      const priority = board.fields.find(f => f.name === 'Priority');

      if (priority) {
        const first = priority.options[0].name;
        await page.locator('[data-testid="planner-filter"][data-field="Priority"]').selectOption(first);

        const expected = board.items.filter(i => i.fields.Priority === first).map(ref);
        await expect(page.getByTestId('planner-row')).toHaveCount(expected.filter(r => !heads.includes(r)).length);
      }
    });

    test('the day\'s plan: planned, put away, brought back, and closed from the list', async ({ page }) => {
      const target = await item(page.request, 'closeFromToday');

      // Planned from the walk.
      const card = await openItem(page, target.itemId);
      await card.locator('[data-testid="planner-day"][data-day="today"]').click();
      await said(page, `${ref(target)}: today`);

      await page.getByTestId('planner-view-today').click();
      await expect(page).toHaveURL(/view=today/);

      const board = await planner(page.request);
      const row = page.locator(`[data-testid="planner-today-row"][data-item="${target.itemId}"]`);
      await expect(row).toHaveCount(1);
      await expect(page.getByTestId('planner-today-row')).toHaveCount(board.items.filter(i => i.day === 'Today').length);
      await expect(page.locator('[data-testid="planner-stat"][data-stat="today"] .hp-stat-value')).toHaveText(String(board.items.filter(i => i.day === 'Today').length));
      await expect(page.locator('[data-testid="planner-stat"][data-stat="away"] .hp-stat-value')).toHaveText(String(board.items.filter(i => i.day === 'NotToday').length));
      await expect(page.locator('[data-testid="planner-stat"][data-stat="done"] .hp-stat-value')).toHaveText(String(board.closedToday.length));

      // Put away from the list, and brought back.
      await row.getByTestId('planner-today-away').click();
      await said(page, `${ref(target)}: not today`);
      await expect(row).toHaveCount(0);

      const put = page.locator(`[data-testid="planner-away-row"][data-item="${target.itemId}"]`);
      await expect(put).toHaveCount(1);
      await put.getByTestId('planner-away-back').click();
      await said(page, `${ref(target)}: today`);
      await expect(row).toHaveCount(1);
      await expect.poll(async () => (await truth(page.request, ref(target))).labels.filter(l => l === 'today' || l === 'not today'), { timeout: 30_000 }).toEqual(['today']);

      // Closed from the list: it leaves the plan and counts as done today.
      await row.getByTestId('planner-today-close').click();
      await said(page, `${ref(target)}: closed as done`);
      await expect(row).toHaveCount(0);
      await expect(page.locator(`[data-testid="planner-done-row"][data-item="${target.itemId}"]`)).toHaveCount(1);

      await expect.poll(async () => (await truth(page.request, ref(target))).state, { timeout: 30_000 }).toBe('closed');
      expect((await planner(page.request)).closedToday.map(ref)).toContain(ref(target));
    });

    for (const [role, how, reason, word] of [
      ['closeDone', 'done', 'COMPLETED', 'closed as done'],
      ['closeNotPlanned', 'not planned', 'NOT_PLANNED', 'closed as not planned'],
    ] as const) {
      test(`an issue is closed as ${how} from its card, and the walk goes on to the next one`, async ({ page }) => {
        const target = await item(page.request, role);
        let card = await openItem(page, target.itemId);

        const [at, of] = ((await page.getByTestId('planner-count').textContent()) ?? '').match(/(\d+) of (\d+)/)!.slice(1).map(Number);

        await card.locator(`[data-testid="planner-close"][data-how="${how}"]`).click();
        await said(page, `${ref(target)}: ${word}`);

        card = page.getByTestId('planner-item');

        if (at < of) {
          // Closing is the end of deciding about it: the next issue is in front of the person,
          // and the one that was closed is one step back.
          await expect(card).not.toHaveAttribute('data-item', target.itemId);
          await expect(page.getByTestId('planner-count')).toContainText(`${at + 1} of ${of}`);

          await page.getByTestId('planner-previous').click();
        }

        // Its card is still there, says what was done, and offers nothing any more.
        await expect(card).toHaveAttribute('data-item', target.itemId);
        await expect(card).toHaveAttribute('data-open', 'false');
        await expect(card.getByTestId('planner-item-closed')).toContainText(word);
        await expect(card.getByTestId('planner-item-reason')).toContainText('closed');

        for (const button of await card.locator('[data-testid="planner-item-actions"] button').all()) {
          await expect(button).toBeDisabled();
        }

        await expect.poll(async () => await truth(page.request, ref(target)), { timeout: 30_000 }).toMatchObject({ state: 'closed', closedAs: reason });

        const after = await planner(page.request);
        expect(after.items.map(ref)).not.toContain(ref(target));
        expect(after.closedToday.map(ref)).toContain(ref(target));
      });
    }

    test('a new issue is opened with the fields chosen, put on the board, and Claude labels it and posts its analysis', async ({ page }) => {
      test.skip(LIVE, 'PRECONDITION: the live tier makes its issues itself; a new issue on github.com is opened by planner-live.sh');

      const board = await planner(page.request);
      expect(board.repositories.length, 'the stand-in tier offers repositories for a new issue').toBeGreaterThan(1);

      await open(page, '/planner?view=new');
      const form = page.getByTestId('planner-new');
      await expect(form).toBeVisible();

      // The first repository is chosen until another is picked.
      await expect(form.locator(`[data-testid="planner-new-repo"][data-repo="${board.repositories[0]}"]`)).toHaveAttribute('data-current', 'true');

      const target = board.repositories[board.repositories.length - 1];
      const title = `Receipts in dark mode ${Date.now()}`;

      // Over the circuit: typing is heard once the submit button wakes up.
      await settle(async () => {
        await page.getByTestId('planner-new-title').fill(title);
        await expect(page.getByTestId('planner-new-submit')).toBeEnabled({ timeout: 3_000 });
      });

      await page.getByTestId('planner-new-body').fill('## Why\n\nThe receipt is white at night. cc @somebody');
      await form.locator(`[data-testid="planner-new-repo"][data-repo="${target}"]`).click();
      await expect(form.locator(`[data-testid="planner-new-repo"][data-repo="${target}"]`)).toHaveAttribute('data-current', 'true');

      const type = board.issueTypes.find(t => t === 'Feature') ?? board.issueTypes[0];
      await form.locator(`[data-testid="planner-new-type"][data-type="${type}"]`).click();
      await form.locator('[data-testid="planner-new-option"][data-field="Priority"][data-option="P2"]').click();

      // A second click on a chosen option takes it back.
      const web = form.locator('[data-testid="planner-new-option"][data-field="Area"][data-option="Web"]');
      await web.click();
      await expect(web).toHaveAttribute('data-current', 'true');
      await web.click();
      await expect(web).toHaveAttribute('data-current', 'false');
      await form.locator('[data-testid="planner-new-option"][data-field="Area"][data-option="Infra"]').click();

      if (board.triage) {
        await expect(page.getByTestId('planner-new-triage')).toBeChecked();
      }

      await page.getByTestId('planner-new-submit').click();

      const opened = page.getByTestId('planner-new-opened');
      await expect(opened).toBeVisible();
      const reference = (await opened.getAttribute('data-reference'))!;
      expect(reference.startsWith(`${target}#`)).toBeTruthy();
      await said(page, `${reference} opened`);

      if (board.triage) {
        await said(page, `Claude is reading ${reference}`);
      }

      // The form is empty again, and keeps the repository for the next one.
      await expect(page.getByTestId('planner-new-title')).toHaveValue('');
      await expect(form.locator(`[data-testid="planner-new-repo"][data-repo="${target}"]`)).toHaveAttribute('data-current', 'true');

      // On the other side: opened by the agent's account, with its type, on the board with its fields.
      expect(await standInIssue(page.request, reference)).toMatchObject({ title, type });
      expect(await truth(page.request, reference)).toMatchObject({ state: 'open', fields: { Priority: 'P2', Area: 'Infra' } });
      expect((await planner(page.request)).items.map(ref)).toContain(reference);

      if (!board.triage) {
        return;
      }

      // Claude's label goes on, from the scripted coder Job in the cluster.
      await expect.poll(async () => (await truth(page.request, reference)).labels, { timeout: 120_000 }).toContain(FAKE_LABEL);

      // Its details: the card, and Claude's analysis read back from the comment.
      await page.getByTestId('planner-new-details').click();
      const detail = page.getByTestId('planner-detail');
      await expect(detail.getByTestId('planner-item')).toHaveAttribute('data-reference', reference);

      const claude = detail.getByTestId('planner-claude');
      await expect(claude.getByTestId('planner-claude-analysis')).toContainText('FAKE SDK triage', { timeout: 60_000 });
      await expect(claude.getByTestId('planner-claude-analysis')).toContainText(`Labels put on: \`${FAKE_LABEL}\``);
      await expect(claude.getByTestId('planner-execute-line')).toHaveText(`Execute github issue ${reference}`);
    });

    test('the today view shows an issue in detail with every button, Claude is asked from there, and the line for Claude Code is copied', async ({ page }) => {
      test.skip(LIVE, 'PRECONDITION: asking Claude on github.com is not part of the live tier');

      const board = await planner(page.request, true);
      const target = board.items.find(i => i.day === 'Today');
      test.skip(!target, 'PRECONDITION: nothing is planned for today on this board');

      await open(page, '/planner?view=today');
      const row = page.locator(`[data-testid="planner-today-row"][data-item="${target!.itemId}"]`);

      // The circuit first: a link followed before the page is interactive changes the address
      // and nothing else, since the live component has not taken over the page yet.
      await expect(row.getByTestId('planner-today-close')).toBeEnabled();
      await row.getByTestId('planner-today-detail').click();

      await expect(page).toHaveURL(new RegExp(`detail=${encodeURIComponent(target!.itemId)}`));
      const detail = page.getByTestId('planner-detail');
      const card = detail.getByTestId('planner-item');
      await expect(card).toHaveAttribute('data-reference', ref(target!));
      await expect(card.getByTestId('planner-item-description').or(card.getByTestId('planner-item-description-none'))).toBeVisible();

      // The walk's buttons, in the detail view: a field is set and the board says so.
      await expect(card.getByTestId('planner-day').first()).toBeEnabled();
      await card.locator('[data-testid="planner-option"][data-field="Area"][data-option="Web"]').click();
      await said(page, `${ref(target!)}: Area set to Web`);
      await expect.poll(async () => (await truth(page.request, ref(target!))).fields.Area, { timeout: 30_000 }).toBe('Web');

      // The line for Claude Code, and its copy button.
      const claude = detail.getByTestId('planner-claude');
      await expect(claude.getByTestId('planner-execute-line')).toHaveText(`Execute github issue ${ref(target!)}`);
      await claude.getByTestId('planner-execute-copy').click();
      await expect(claude.getByTestId('planner-execute-copy')).toHaveText(/copied|copy it by hand/);

      if (!board.triage) {
        await expect(claude.getByTestId('planner-claude-ask')).toHaveCount(0);
        return;
      }

      // Asked from here, Claude answers into the same panel without a reload.
      await expect(claude.getByTestId('planner-claude-none')).toBeVisible();
      await claude.getByTestId('planner-claude-ask').click();
      await expect(claude.getByTestId('planner-claude-message')).toContainText(`Claude is reading ${ref(target!)}`);
      await expect(claude.getByTestId('planner-claude-analysis')).toContainText('FAKE SDK triage', { timeout: 120_000 });
      await expect(claude.getByTestId('planner-claude-done')).toContainText(`labelled ${FAKE_LABEL}`);
      await expect(claude.getByTestId('planner-claude-ask')).toHaveText('ask Claude again');
      await expect.poll(async () => (await truth(page.request, ref(target!))).labels, { timeout: 30_000 }).toContain(FAKE_LABEL);

      await detail.getByTestId('planner-detail-close').click();
      await expect(page.getByTestId('planner-detail')).toHaveCount(0);
    });

    test('a new issue the board cannot take, and a model asking, are refused by the API without a trace on GitHub', async ({ page }) => {
      test.skip(LIVE, 'PRECONDITION: the stand-in tier only');

      const before = (await (await page.request.get(`${STANDIN}/github/control/project`)).json()).items.length;
      const post = (body: object) => page.request.post('/api/planner/issues', { data: { title: 'refused', decidedBy: NAME, triage: false, ...body } });

      const elsewhere = await post({ repository: 'acme/elsewhere' });
      expect(elsewhere.status()).toBe(404);
      expect((await elsewhere.json()).message).toContain('not a repository the planner opens issues in');

      const saga = await post({ repository: (await planner(page.request)).repositories[0], type: 'Saga' });
      expect(saga.status()).toBe(404);
      expect((await saga.json()).message).toContain('not an issue type');

      expect((await post({ repository: 'acme/api', decidedBy: undefined })).status()).toBe(400);
      expect((await (await page.request.get(`${STANDIN}/github/control/project`)).json()).items.length).toBe(before);

      const model = await page.request.post('/api/planner/triage', { data: { repository: 'acme/api', number: 431, decidedBy: 'model' } });
      expect([409]).toContain(model.status());
    });

    test('without a name nothing can be pressed, and the page says why', async ({ browser }) => {
      // A context of its own: the others carry the remembered name.
      const context = await browser.newContext({ baseURL: test.info().project.use.baseURL });
      const page = await context.newPage();

      try {
        await open(page, '/planner');

        test.skip(await page.getByTestId('planner-actor').evaluate(e => e.tagName !== 'INPUT'), 'PRECONDITION: somebody is signed in, so there is no name to leave out');

        await expect(page.getByTestId('planner-locked')).toContainText('Type your name');
        await expect(page.getByTestId('planner-item').getByTestId('planner-day').first()).toBeDisabled();

        // Typing one unlocks it - over the circuit, so retried until it is heard.
        await settle(async () => {
          await page.getByTestId('planner-actor').fill(NAME);
          await expect(page.getByTestId('planner-item').getByTestId('planner-day').first()).toBeEnabled({ timeout: 3_000 });
        });

        await expect(page.getByTestId('planner-locked')).toHaveCount(0);
      } finally {
        await context.close();
      }
    });
  });
});
