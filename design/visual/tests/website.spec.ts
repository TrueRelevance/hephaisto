import { test, expect } from '@playwright/test';
import * as path from 'path';
import { pathToFileURL } from 'url';

const LANDING = pathToFileURL(path.resolve(__dirname, '../../../website/index.html')).href;

/**
 * The second consumer.
 *
 * Until this page existed, "one token source, three consumers" was untestable - there was one
 * consumer, so nothing could disagree with anything. These baselines are what make the claim
 * cost something: change a token and both the console gallery AND this page move, in the same
 * run, or one of them has quietly stopped consuming the same file.
 */
test.describe('the landing page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(LANDING);
    await expect(page.locator('h1')).toBeVisible();

    // The same silent-failure guard as the gallery. A landing page that lost its stylesheet
    // still renders every word, in Times New Roman, and looks like a deliberate choice to
    // nobody who has not seen it before.
    const styled = await page.evaluate(() =>
      getComputedStyle(document.body).backgroundColor !== 'rgba(0, 0, 0, 0)');
    expect(styled, 'tokens.css or site.css did not load').toBe(true);

    await page.evaluate(() => document.fonts.ready);
    const loaded = await page.evaluate(() => document.fonts.check('16px Gabarito'));
    expect(loaded, 'Gabarito did not load; the page is set in a fallback stack').toBe(true);
  });

  test('the whole page', async ({ page }) => {
    await expect(page).toHaveScreenshot('landing-full.png', { fullPage: true });
  });

  test('hero', async ({ page }) => {
    await expect(page.locator('.hero')).toHaveScreenshot('landing-hero.png');
  });

  /** The section the reader this page was written for came for. */
  test('safety-model', async ({ page }) => {
    await expect(page.locator('#safety')).toHaveScreenshot('landing-safety.png');
  });
});
