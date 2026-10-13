import { test, expect } from '@playwright/test';
import * as path from 'path';
import { pathToFileURL } from 'url';

const GALLERY = pathToFileURL(path.resolve(__dirname, '../../gallery.html')).href;

/**
 * One baseline for the whole page, and one per component.
 *
 * The per-component shots are not redundant with the full-page one. A full-page diff says "the
 * page changed" and points at a wall of pixels; a component diff names the thing that moved,
 * which is the difference between a useful failure and one people learn to re-baseline past.
 */
test.describe('the design language', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(GALLERY);
    // The stylesheet is linked, so it must actually have loaded. Comparing against an unstyled
    // page would produce a perfectly stable baseline of the wrong thing.
    await expect(page.locator('.hp-shell')).toBeVisible();
    const styled = await page.evaluate(() =>
      getComputedStyle(document.body).backgroundColor !== 'rgba(0, 0, 0, 0)');
    expect(styled, 'app.css did not load; the baseline would be of an unstyled page').toBe(true);

    // The faces are self-hosted because the pod may have no egress, and a webfont that fails to
    // load fails SILENTLY - the browser falls back to a system stack and renders a page that
    // looks entirely fine. A baseline taken then is a stable picture of the wrong thing, and it
    // would keep passing forever afterwards.
    await page.evaluate(() => document.fonts.ready);
    const loaded = await page.evaluate(() => ({
      archivo: document.fonts.check('16px Archivo'),
      jetbrains: document.fonts.check('16px "JetBrains Mono"'),
    }));
    expect(loaded.archivo, 'Archivo did not load; this shot would be of the fallback stack').toBe(true);
    expect(loaded.jetbrains, 'JetBrains Mono did not load; this shot would be of the fallback stack').toBe(true);
  });

  test('the whole gallery', async ({ page }) => {
    await expect(page).toHaveScreenshot('gallery-full.png', { fullPage: true });
  });

  const sections = [
    ['brand', 'Brand'],
    ['tokens', 'Colour tokens'],
    ['vocabulary', 'State, severity, risk, decision'],
    ['incident-row', 'Hard component 1'],
    ['finding', 'Hard component 2'],
    ['budget-meter', 'Hard component 3'],
    ['code-block', 'Hard component 4'],
    ['form-controls', 'Form controls'],
    ['callouts', 'Callouts'],
    ['verification', 'Verification rows'],
    // #109's controls. Registered here as well as appearing in the full-page shot, because the
    // full-page baseline is the fragile one - every section below an insertion shifts it - and a
    // component covered only by that shot is a component the net will stop covering the first
    // time somebody re-baselines past a diff.
    ['lifecycle', 'Incident lifecycle'],
    ['assignment', 'Assignment'],
    ['connections', 'Connections panel'],
    // v0.9.0: the code-fix table (every state, a running phase, a PR) and the incident section
    // (a plan with approve/deny, the disabled-approve variant, an implementing and a PR attempt).
    ['codefix-table', 'Code fixes table'],
    ['codefix-section', 'Code fix on an incident'],
    // v0.14.0: the work-item list, and the head of an attempt's own page - the issue, the
    // history with who decided and through what, a pull request's description as text.
    ['workitems', 'Work items'],
    // v0.15.0: the planner - the switch between its views, the walk with one issue's card and
    // the day beside it, and a group of the board view with a planned and a put-away row.
    ['planner', 'Planner'],
  ] as const;

  /**
   * Accessibility is part of v0.4.0's acceptance rather than a follow-up, and a focus ring is
   * the one accessibility property that a screenshot can actually hold. Links were missing from
   * the focus-visible rule entirely until this milestone - and links are most of what a keyboard
   * user moves between in this console.
   *
   * Keyboard focus specifically, not `.focus()`: :focus-visible does not match a
   * programmatically focused element in every engine, so asserting on it would have been a test
   * that passes without the rule it exists to check.
   */
  test('focus-ring', async ({ page }) => {
    const row = page.locator('.g-sec').filter({ has: page.getByRole('heading', { name: 'Hard component 1' }) });
    await row.getByRole('link').first().focus();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Shift+Tab');

    const focused = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      return el ? { tag: el.tagName, outline: getComputedStyle(el).outlineWidth } : null;
    });
    expect(focused, 'nothing is focused; the shot below would prove nothing').not.toBeNull();
    expect(focused!.tag, 'expected a link to hold focus').toBe('A');
    expect(focused!.outline, 'the focus ring has no width, so it is not visible')
      .not.toBe('0px');

    await expect(row).toHaveScreenshot('focus-ring.png');
  });

  for (const [slug, heading] of sections) {
    test(slug, async ({ page }) => {
      const section = page.locator('.g-sec').filter({ has: page.getByRole('heading', { name: heading }) });
      await expect(section).toHaveCount(1);
      await expect(section).toHaveScreenshot(`${slug}.png`);
    });
  }
});
