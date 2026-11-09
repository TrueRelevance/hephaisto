import { readFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { Page, TestInfo, expect } from '@playwright/test';

/**
 * Navigate and wait until the Blazor circuit has started coming up.
 *
 * This is a Blazor **Web App** with `<Routes @rendermode="InteractiveServer" />`, so every
 * component renders TWICE: once as static server-rendered HTML delivered with the document, and
 * then again over the circuit, which replaces that DOM wholesale.
 *
 * Between those two moments the page looks completely finished and is completely inert. The
 * elements are there, they are visible, their text is correct - and a click or an input event
 * dispatched into them is dropped, because the handlers belong to a render that has not happened
 * yet. Measured against a live console: `h1` becomes visible at ~50ms and a click does not take
 * effect until ~600ms. Waiting on `h1` alone was never enough; see docs/backlog.md #48.
 *
 * WAIT ON THE NEGOTIATION, NOT ON A WEBSOCKET. The first version of this waited for a `_blazor`
 * websocket, which is a TRANSPORT rather than a state: SignalR negotiates, and where a websocket
 * cannot be established it falls back to server-sent events or long polling and the page is
 * perfectly interactive with no websocket ever opening. That version passed against a
 * development image and timed out against all nine specs on the published one - asserting how
 * the circuit connected rather than that it had.
 *
 * `/_blazor/negotiate` happens under every transport, so it is the portable signal. It is also
 * only the START of the handshake, which is why anything that INTERACTS must additionally be
 * written to retry - see `settle` below.
 */
export async function open(page: Page, path: string) {
  // Registered before navigating, or the request can be made before anyone is listening.
  const circuit = page.waitForRequest(r => r.url().includes('/_blazor'), { timeout: 30_000 });

  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await circuit;

  // The nav is server-rendered, so it is not proof of anything; the h1 is rendered by the
  // component itself.
  await expect(page.locator('h1')).toBeVisible();

  // And the circuit must not have failed on the way up. Both of these are in the layout at
  // all times and hidden until something goes wrong, so asserting they are hidden is a real
  // check rather than a tautology.
  await expect(page.locator('#components-reconnect-modal')).toBeHidden();
  await expect(page.locator('#blazor-error-ui')).toBeHidden();
}

/**
 * Perform an interaction, retrying it until the circuit actually processes it.
 *
 * The honest way to wait for hydration, and the only one that does not depend on knowing how
 * Blazor connected or how long it took. An event dispatched into a not-yet-interactive page is
 * dropped silently - no error, no console entry - so the only reliable signal that the circuit
 * is live is that an interaction had an effect.
 *
 * `toPass` re-runs the whole block, so the action is repeated as well as the assertion. That
 * matters: retrying only the assertion would wait forever on an event that was already lost.
 */
export async function settle(action: () => Promise<void>, timeout = 30_000) {
  await expect(action).toPass({ timeout });
}

/** The status endpoint, so the UI can be checked against the API rather than against itself. */
export async function status(page: Page) {
  const res = await page.request.get('/api/status');
  expect(res.ok()).toBeTruthy();
  return res.json();
}

/**
 * Percentages render via Display.Percent, so the exact formatting is the app's business
 * rather than this suite's. Parse whatever number is in the string.
 */
export function parsePercent(text: string | null): number {
  if (!text) return NaN;
  const m = text.match(/-?[\d.]+/);
  return m ? parseFloat(m[0]) : NaN;
}

/**
 * The page's own row cap, and therefore this suite's (#49).
 *
 * Must match `Limit` in `Components/Pages/Incidents.razor`. It used to be that every spec picked
 * a convenient number: the specs asked for 100 while the page asked for 200, so on a nightly run
 * that opened 103 incidents the table honestly showed 103 and the console spec compared it
 * against a truncated 100 - `Expected: 100, Received: 103`, a red gate reporting a defect in
 * itself. The other call sites failed more quietly: a spec that scans for an incident with some
 * property would find nothing and SKIP, and a skip is treated as a failure here (#1), so the
 * gate went red naming the wrong thing.
 */
export const INCIDENT_LIMIT = 200;

/**
 * Incidents from the API, with the same cap the page uses, refusing to return a truncated list.
 *
 * The cap check is the point. At the cap the list is silently short, and a comparison against a
 * silently short list either passes while meaning nothing or fails while naming the wrong cause.
 * Failing here instead says exactly what to change and where.
 */
export async function incidents(page: Page, query = '') {
  const sep = query ? (query.startsWith('&') ? '' : '&') : '';
  const res = await page.request.get(`/api/incidents?limit=${INCIDENT_LIMIT}${sep}${query}`);
  expect(res.ok()).toBeTruthy();

  const body = await res.json();

  expect(body.length,
    `${INCIDENT_LIMIT} incidents came back, so the list is truncated and any count compared ` +
    `against it is meaningless; raise INCIDENT_LIMIT here and Limit in Incidents.razor together`)
    .toBeLessThan(INCIDENT_LIMIT);

  return body;
}

/**
 * A test that lands before the code that turns it green (scripts/e2e/ui/KNOWN_RED).
 *
 * The UI suite's form of the pager's and the issues suite's known-red list, and held to the same
 * two rules by Playwright itself: a listed test that FAILS is an expected failure and the run
 * stays green; a listed test that PASSES is "expected to fail, but passed" and the run is red -
 * so the code that turns it green and the line's removal land in one commit.
 *
 * A line is `<spec file> <test id> <major.minor> <stage and why>`; the test id is the first word
 * of the test's title. A spec opts in by calling this from a beforeEach:
 *
 *     test.beforeEach(({}, info) => knownRed(info));
 *
 * `UI_STRICT=1` ignores the list - the release gate's mode, where every listed test fails.
 * tests/Hephaisto.Tests/E2e/UiSuiteTests.cs holds the list to its form and its milestone.
 */
export function knownRed(info: TestInfo) {
  if (process.env.UI_STRICT === '1') return;

  const id = info.title.split(' ')[0];
  const file = basename(info.file);
  const entry = readFileSync(join(__dirname, '..', 'KNOWN_RED'), 'utf8')
    .split('\n')
    .map(l => l.trim())
    .filter(l => l.length > 0 && !l.startsWith('#'))
    .map(l => l.split(/\s+/))
    .find(([f, t]) => f === file && t === id);

  if (entry) {
    info.fail(true, `KNOWN RED ${file} ${id} (${entry[2]}): ${entry.slice(3).join(' ')}`);
  }
}
