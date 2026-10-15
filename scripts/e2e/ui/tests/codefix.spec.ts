import { test, expect, Page } from '@playwright/test';
import { incidents, open, settle } from './helpers';

// The code-fix views (v0.9.0). The user-facing promise is that a running code fix and the PR it
// opened are visible in the console, so these assert the console SHOWS what /api/codefixes
// reports - the same shape as the incident-list spec, against the API rather than a constant.
//
// Whether a run produces an attempt at all depends on the code-fix mode the harness installed and
// on the model's diagnosis, so the specs that need one skip with a PRECONDITION marker rather than
// failing; ui/run.sh admits only those. The section and the page themselves must always render.

/** Kept in step with Limit in Components/Pages/CodeFixes.razor, for #49's reason. */
const CODEFIX_LIMIT = 200;

/**
 * An attempt is for an incident or - since v0.14.0 - for a GitHub issue handed to Hephaisto as a
 * work item. Exactly one of incidentId and workItemId is set; `issue` is owner/repo#n.
 */
type Attempt = {
  id: string;
  incidentId: string | null;
  workItemId: string | null;
  issue: string | null;
  issueUrl: string | null;
  state: string;
  prUrl: string | null;
  prBody: string | null;
  approvedBy: string | null;
  decidedThrough: string | null;
  failureReason: string | null;
};

/** GET /api/codefixes/{id}: the attempt with what it is for. */
type Detail = { attempt: Attempt; workItem: { id: string; title: string; authorLogin: string; url: string; state: string } | null };

async function detail(page: Page, id: string): Promise<Detail> {
  const res = await page.request.get(`/api/codefixes/${id}`);
  expect(res.ok(), `GET /api/codefixes/${id}`).toBeTruthy();
  return res.json();
}

async function attempts(page: Page): Promise<Attempt[]> {
  const res = await page.request.get(`/api/codefixes?limit=${CODEFIX_LIMIT}`);
  expect(res.ok()).toBeTruthy();

  const body = await res.json();
  expect(body.length,
    `${CODEFIX_LIMIT} code fixes came back, so the list is truncated; raise the cap here and in CodeFixes.razor together`)
    .toBeLessThan(CODEFIX_LIMIT);

  return body;
}

test.describe('code fixes', () => {
  test('the nav links to the code-fix page, which states the mode', async ({ page }) => {
    await open(page, '/');

    const link = page.getByTestId('nav-codefixes');
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute('href', /codefixes$/);

    await open(page, '/codefixes');
    await expect(page.locator('h1')).toHaveText('code fixes');

    // Checked against the API, not a constant: an empty table under mode Off is the configuration
    // working, and the mode is how a reader tells that apart from the feature being broken.
    const mode = await (await page.request.get('/api/codefixes/mode')).json();
    await expect(page.getByTestId('codefix-mode')).toContainText(String(mode.effective).toLowerCase());
  });

  test('every attempt the API lists renders as a row, with its state', async ({ page }) => {
    let listed: Attempt[] = [];

    // Retried as a unit, for the reason the incident list is: an attempt can move between the
    // API read and the table read, and both halves are telling the truth.
    await settle(async () => {
      await open(page, '/codefixes');
      listed = await attempts(page);

      const rows = page.getByTestId('codefix-row');
      await expect(rows).toHaveCount(listed.length, { timeout: 5_000 });

      const rendered = await rows.evaluateAll(els =>
        els.map(e => `${(e as HTMLElement).dataset.attempt}:${(e as HTMLElement).dataset.state}`));
      expect(new Set(rendered)).toEqual(new Set(listed.map(a => `${a.id}:${a.state}`)));
    });

    test.skip(listed.length === 0,
      'PRECONDITION: no code fix was attempted in this run, so no row could render');

    // Every running attempt says so, with its phase and elapsed time.
    const running = listed.filter(a => a.state === 'Planning' || a.state === 'Implementing');
    for (const a of running) {
      await expect(page.locator(`[data-testid="codefix-row"][data-attempt="${a.id}"]`)
        .getByTestId('codefix-running')).toBeVisible();
    }

    // Every row says what it is for, and links it: an incident's row its code-fix section, a
    // work item's row (v0.14.0) the attempt's own page - by the issue's reference, never its
    // title - with the issue itself one small link away, in a new tab.
    for (const a of listed.slice(0, 25)) {
      const row = page.locator(`[data-testid="codefix-row"][data-attempt="${a.id}"]`);

      if (a.incidentId) {
        await expect(row.getByTestId('codefix-incident-link')).toHaveAttribute('href', `incidents/${a.incidentId}#codefix`);
        await expect(row.getByTestId('codefix-issue')).toHaveCount(0);
      } else {
        await expect(row.getByTestId('codefix-issue')).toHaveText(a.issue!);
        await expect(row.getByTestId('codefix-issue')).toHaveAttribute('href', `codefixes/${a.id}`);
        await expect(row.getByTestId('codefix-incident-link')).toHaveCount(0);

        const external = row.getByTestId('codefix-issue-link');
        await expect(external).toHaveAttribute('href', a.issueUrl!);
        await expect(external).toHaveAttribute('target', '_blank');
        await expect(external).toHaveAttribute('rel', /noopener/);
      }

      // And every attempt, whichever it is for, has a page of its own.
      await expect(row.getByTestId('codefix-detail-link')).toHaveAttribute('href', `codefixes/${a.id}`);
    }
  });

  test('the state filter narrows the table to what the API says is in that state, for both kinds of attempt', async ({ page }) => {
    const listed = await attempts(page);

    test.skip(listed.length === 0,
      'PRECONDITION: no code fix was attempted in this run, so there was nothing to filter');

    const state = listed[0].state;

    await open(page, '/codefixes');

    // Retried as a unit, the choice included: a change event dispatched before the circuit is up
    // is dropped, and an attempt can move between the API read and the table read.
    await settle(async () => {
      await page.getByTestId('codefix-filter').selectOption(state);

      const now = (await attempts(page)).filter(a => a.state === state);
      const rows = page.getByTestId('codefix-row');
      await expect(rows).toHaveCount(now.length, { timeout: 3_000 });

      const rendered = await rows.evaluateAll(els => els.map(e => (e as HTMLElement).dataset.attempt));
      expect(new Set(rendered)).toEqual(new Set(now.map(a => a.id)));
    });
  });

  test('the nav badge counts what needs somebody, whichever kind of attempt it is', async ({ page }) => {
    await settle(async () => {
      await open(page, '/codefixes');

      // /api/codefixes/counts counts every attempt: a plan for an issue waits for an answer
      // exactly as an incident's does, and the badge is where somebody learns of it.
      const counts = await (await page.request.get('/api/codefixes/counts')).json();
      const wanted = counts.running + counts.awaitingApproval;
      const badge = page.getByTestId('nav-codefixes-badge');

      if (wanted > 0) {
        await expect(badge).toHaveText(String(wanted), { timeout: 5_000 });
      } else {
        await expect(badge).toHaveCount(0, { timeout: 5_000 });
      }
    });

    const listed = await attempts(page);
    const waitingForAnIssue = listed.filter(a => a.workItemId && a.state === 'PlanReady').length;
    const counts = await (await page.request.get('/api/codefixes/counts')).json();

    expect(counts.awaitingApproval, 'a work item\'s waiting plan is counted with the incidents\'')
      .toBeGreaterThanOrEqual(waitingForAnIssue);
  });

  test('an opened PR is linked from its row, in a new tab', async ({ page }) => {
    const listed = await attempts(page);
    const withPr = listed.filter(a => a.state === 'PrOpened' && a.prUrl);

    test.skip(withPr.length === 0,
      'PRECONDITION: no code fix in this run reached PrOpened, so there was no PR to link');

    await open(page, '/codefixes');

    for (const a of withPr) {
      const link = page.locator(`[data-testid="codefix-row"][data-attempt="${a.id}"]`)
        .getByTestId('codefix-pr-link');
      await expect(link).toHaveAttribute('href', a.prUrl!);
      await expect(link).toHaveAttribute('target', '_blank');
      await expect(link).toHaveAttribute('rel', /noopener/);
    }
  });

  test('an incident page carries the code-fix section, with its attempts', async ({ page }) => {
    const listed = await attempts(page);

    // An incident with an attempt when there is one, so the attempt itself is checked; otherwise
    // any incident, because the section - mode and verdict - renders on every incident.
    const target = listed.find(a => a.incidentId)?.incidentId ?? (await incidents(page))[0]?.id;
    expect(target, 'no incident exists to open, which the console spec would already have failed on')
      .toBeTruthy();

    await open(page, `/incidents/${target}#codefix`);

    const section = page.getByTestId('codefix-section');
    await expect(section).toBeVisible();
    await expect(section).toHaveAttribute('id', 'codefix');
    await expect(section.getByTestId('codefix-mode')).toBeVisible();

    const mine = listed.filter(a => a.incidentId === target);
    for (const a of mine) {
      await expect(section.locator(`[data-testid="codefix-attempt"][data-attempt="${a.id}"]`)).toBeVisible();
    }

    // Approve and deny exist exactly when a plan is waiting - an approve button on a finished
    // attempt would invite somebody to authorise a write twice.
    const ready = mine.filter(a => a.state === 'PlanReady').length;
    await expect(section.getByTestId('codefix-approve')).toHaveCount(ready);
    await expect(section.getByTestId('codefix-deny')).toHaveCount(ready);
  });
});

// One attempt's own page (v0.14.0). An attempt for a GitHub issue has no incident page, so until
// this page its plan could be read in full only on the issue; an incident's attempt is here too,
// by the same address. Checked against GET /api/codefixes/{id}, never against the list.
test.describe('one code fix', () => {
  test('an id nobody has says so', async ({ page }) => {
    await open(page, '/codefixes/00000000-0000-0000-0000-000000000000');

    await expect(page.locator('h1')).toHaveText('code fix');
    await expect(page.getByTestId('codefix-missing')).toHaveText(/No code fix with this id/);
    await expect(page.getByTestId('codefix-detail')).toHaveCount(0);
  });

  test('an incident\'s attempt has its own page: state, history, the plan, and a link back to the incident', async ({ page }) => {
    const mine = (await attempts(page)).find(a => a.incidentId);

    test.skip(!mine, 'PRECONDITION: no incident in this run got a code fix, so there was no attempt to open');

    let d!: Detail;

    await settle(async () => {
      d = await detail(page, mine!.id);
      await open(page, `/codefixes/${mine!.id}`);

      const header = page.getByTestId('codefix-detail');
      await expect(header).toHaveAttribute('data-subject', 'incident');
      await expect(header).toHaveAttribute('data-state', d.attempt.state, { timeout: 5_000 });
    });

    expect(d.workItem).toBeNull();
    await expect(page.getByTestId('codefix-incident-link')).toHaveAttribute('href', `incidents/${d.attempt.incidentId}#codefix`);
    await expect(page.getByTestId('codefix-issue')).toHaveCount(0);

    // The history starts where the attempt did, and every line is a step with a time.
    const history = page.getByTestId('codefix-history').locator('li');
    await expect(history.first()).toHaveAttribute('data-step', 'created');
    expect(await history.count()).toBeGreaterThanOrEqual(1);

    await expect(page.getByTestId('codefix-attempt')).toHaveAttribute('data-attempt', mine!.id);

    // The same rule as on the incident page: approve and deny exist exactly when a plan waits.
    const ready = d.attempt.state === 'PlanReady' ? 1 : 0;
    await expect(page.getByTestId('codefix-approve')).toHaveCount(ready);
    await expect(page.getByTestId('codefix-deny')).toHaveCount(ready);
  });

  test('an attempt for an issue shows the issue, who decided and through what, and somebody else\'s text as text', async ({ page }) => {
    const listed = (await attempts(page)).filter(a => a.workItemId);

    test.skip(listed.length === 0,
      'PRECONDITION: no GitHub issue was handed to this agent, so no attempt is for a work item');

    // The one that went furthest: a pull request has a description, a denial a reason.
    const mine = listed.find(a => a.state === 'PrOpened') ?? listed.find(a => a.state === 'Denied') ?? listed[0];
    const d = await detail(page, mine.id);

    await open(page, `/codefixes/${mine.id}`);

    const header = page.getByTestId('codefix-detail');
    await expect(header).toHaveAttribute('data-subject', 'workitem');
    await expect(header).toHaveAttribute('data-state', d.attempt.state);

    await expect(page.locator('h1')).toContainText(d.attempt.issue!);
    await expect(page.getByTestId('codefix-issue')).toHaveText(d.attempt.issue!);
    await expect(page.getByTestId('codefix-incident-link')).toHaveCount(0);

    const external = page.getByTestId('codefix-issue-link');
    await expect(external).toHaveAttribute('href', d.workItem!.url);
    await expect(external).toHaveAttribute('target', '_blank');
    await expect(external).toHaveAttribute('rel', /noopener/);

    // The title is whatever somebody typed into GitHub. As text: the element's text is the
    // title, character for character, and it has no element inside it.
    const title = page.getByTestId('codefix-issue-title');
    expect(await title.evaluate(e => e.textContent)).toBe(d.workItem!.title);
    await expect(title.locator('*')).toHaveCount(0);
    await expect(page.getByTestId('codefix-issue-author')).toHaveText(d.workItem!.authorLogin);

    // Who decided, and through what: the console, the API, or a comment on the issue.
    const steps = await page.getByTestId('codefix-history').locator('li').evaluateAll(
      els => els.map(e => (e as HTMLElement).dataset.step));
    expect(steps[0]).toBe('created');

    if (d.attempt.approvedBy) {
      const decided = page.getByTestId('codefix-history')
        .locator(`li[data-step="${d.attempt.state === 'Denied' ? 'denied' : 'approved'}"]`);
      await expect(decided).toContainText(`by ${d.attempt.approvedBy}`);

      if (d.attempt.decidedThrough === 'GitHub') {
        await expect(decided).toContainText('through a comment on the issue');
      } else if (d.attempt.decidedThrough === 'Ui') {
        await expect(decided).toContainText('through the console');
      }
    }

    // The pull request's description, as the runner sent it: a model wrote parts of it. Its
    // markdown is rendered, with no HTML of its own, no image fetched and a link only to http(s).
    if (d.attempt.prBody) {
      const body = page.getByTestId('codefix-pr-body');
      await expect(body).toBeVisible();
      await expect(body.locator('script, img, iframe, object, embed, style')).toHaveCount(0);
      for (const link of await body.locator('a').all()) {
        expect(await link.getAttribute('href')).toMatch(/^(https?:|mailto:)/);
        expect(await link.getAttribute('rel')).toBe('noopener noreferrer nofollow');
      }
      await expect(page.getByTestId('codefix-pr-link')).toHaveAttribute('href', d.attempt.prUrl!);
    } else {
      await expect(page.getByTestId('codefix-pr-body')).toHaveCount(0);
    }

    // A rejection's reason is whatever an approver typed after /reject, or into the console.
    if (d.attempt.state === 'Denied' && d.attempt.failureReason) {
      const reason = page.getByTestId('codefix-failure-reason');
      expect(await reason.evaluate(e => e.textContent)).toBe(d.attempt.failureReason);
      await expect(reason.locator('*')).toHaveCount(0);
    }
  });

  test('a waiting plan for an issue is denied from its page, with the reason kept as text and the console recorded', async ({ page }) => {
    const waiting = (await attempts(page)).find(a => a.workItemId && a.state === 'PlanReady');

    test.skip(!waiting,
      'PRECONDITION: no plan for a GitHub issue is waiting for an answer, so there was nothing to decide');

    // Markup and everything GitHub or a browser would act on, in one reason.
    const reason = '<b>not this way</b> <img src=x onerror=alert(1)> [link](https://example.invalid) fixes #1';
    const who = 'ui-suite';

    await open(page, `/codefixes/${waiting!.id}`);
    await expect(page.getByTestId('codefix-approval')).toBeVisible();

    // Deciding needs a name, as everywhere on this console.
    await expect(page.getByTestId('codefix-deny')).toBeDisabled();

    // Retried as a unit, typing included: an input event dispatched before the circuit is up is
    // dropped silently, and the page's next render then puts the empty value back into the box.
    await settle(async () => {
      await page.getByTestId('codefix-actor').fill(who);
      await page.getByTestId('codefix-deny-reason').fill(reason);
      await expect(page.getByTestId('codefix-deny')).toBeEnabled({ timeout: 2_000 });
      await expect(page.getByTestId('codefix-deny-reason')).toHaveValue(reason, { timeout: 2_000 });
    });

    await page.getByTestId('codefix-deny').click();

    await expect(page.getByTestId('codefix-detail')).toHaveAttribute('data-state', 'Denied');
    await expect(page.getByTestId('codefix-approve')).toHaveCount(0);
    await expect(page.getByTestId('codefix-deny')).toHaveCount(0);

    // The door's own record, through the API: who, through what, and the reason as typed.
    const after = await detail(page, waiting!.id);
    expect(after.attempt.state).toBe('Denied');
    expect(after.attempt.approvedBy).toBe(who);
    expect(after.attempt.decidedThrough).toBe('Ui');
    expect(after.attempt.failureReason).toBe(reason);

    // And on the page it is text: no <b>, no <img>, no link made of it.
    const shown = page.getByTestId('codefix-failure-reason');
    expect(await shown.evaluate(e => e.textContent)).toBe(reason);
    await expect(shown.locator('*')).toHaveCount(0);
    await expect(page.getByTestId('codefix-history').locator('li[data-step="denied"]'))
      .toContainText(`by ${who}, through the console`);
  });
});
