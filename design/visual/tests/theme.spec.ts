import { test, expect } from '@playwright/test';
import * as path from 'path';
import { pathToFileURL } from 'url';

const GALLERY = pathToFileURL(path.resolve(__dirname, '../../gallery.html')).href;

/**
 * The six combinations of what the reader chose and what their operating system says.
 *
 * These are assertions about computed values rather than screenshots, and that is deliberate:
 * the question here is whether the CASCADE resolves correctly, and a screenshot answers it only
 * incidentally while costing a baseline per case. A theme system that is right in four of six
 * is the ordinary bug - the two that break are almost always "chose light on a dark OS", where
 * the media query never matches, and "chose dark on a light OS", where it matches and wins.
 *
 * Both projects run this file, so each case is checked under both `prefers-color-scheme`
 * values and the project name is the OS half of the pair.
 */
const LIGHT_BG = 'rgb(248, 247, 248)'; // --bg, light
const DARK_BG = 'rgb(33, 33, 35)';     // --bg, dark

async function backgroundWith(page: import('@playwright/test').Page, choice: string | null) {
  await page.goto(GALLERY);
  await expect(page.locator('.hp-shell')).toBeVisible();

  await page.evaluate((c) => {
    if (c === null) {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', c);
    }
  }, choice);

  return page.evaluate(() => getComputedStyle(document.body).backgroundColor);
}

test.describe('choosing a theme', () => {
  test('an explicit choice wins over the operating system', async ({ page }, testInfo) => {
    // The case the media query alone cannot serve, and the reason #50 existed: a reader on a
    // dark-mode laptop presenting on a projector in a bright room.
    expect(await backgroundWith(page, 'light')).toBe(LIGHT_BG);
    expect(await backgroundWith(page, 'dark')).toBe(DARK_BG);

    // And it must win in BOTH directions, which is the half that is usually missed. Under the
    // light project the media query matches, so `light` here is only proof of anything because
    // `dark` beside it is too.
    expect(testInfo.project.name).toMatch(/^(dark|light)$/);
  });

  test('no choice means the operating system decides', async ({ page }, testInfo) => {
    // "system" is the absence of the attribute, not a third palette. This is what every
    // release before v0.5.0 did, and it has to keep working - a stored value that is missing,
    // unrecognised, or unreadable in a private window all land here.
    const expected = testInfo.project.name === 'light' ? LIGHT_BG : DARK_BG;

    expect(await backgroundWith(page, null)).toBe(expected);
  });

  test('an unrecognised stored value falls back to the operating system', async ({ page }, testInfo) => {
    // app.js only ever writes 'system' | 'light' | 'dark', but the value lives in the reader's
    // localStorage where anything can end up - an older build, a hand edit, a truncated write.
    // It must degrade to the OS rather than to an unstyled page.
    const expected = testInfo.project.name === 'light' ? LIGHT_BG : DARK_BG;

    expect(await backgroundWith(page, 'sepia')).toBe(expected);
  });
});
