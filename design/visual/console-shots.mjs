/*
 * Photographs the shipping console. Run inside the pinned Playwright container by
 * scripts/console-shots.sh, which is where the docker and font-refusal contract lives.
 *
 * This is a FILE rather than a `node -e` string on purpose. The previous version was a
 * heredoc containing a docker invocation containing `bash -lc` containing `node -e` with a
 * quoted JS program inside it, and two of the three bugs found while writing it were quoting
 * accidents rather than anything to do with the browser.
 */
import { chromium } from '@playwright/test'

const base = process.env.BASE_URL
const out = process.env.OUT
const scheme = process.env.SCHEME || 'dark'

const abs = (h) => (h.startsWith('/') ? h : '/' + h)

const browser = await chromium.launch()
const page = await browser.newPage({
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 2,
    colorScheme: scheme,
})

// NOT networkidle: the console is Blazor Server and holds a SignalR websocket open, so "the
// network went quiet" either never happens or happens before the circuit rendered anything.
const go = async (path) => {
    const r = await page.goto(base + path, { waitUntil: 'domcontentloaded', timeout: 30000 })
    if (!r || !r.ok()) throw new Error(`${path} returned ${r && r.status()}`)
    await page.evaluate(() => document.fonts.ready)
}

await go('/')

// A screenshot in a fallback face is a picture of a different product, and it is not obvious
// in a thumbnail. Same refusal as scripts/brand-assets.sh.
if (!(await page.evaluate(() => document.fonts.check('16px Gabarito')))) {
    throw new Error('Gabarito did not load; refusing to photograph the console')
}

// data-testid, not an href pattern: the console's links are RELATIVE, so a[href^="/incidents/"]
// matches nothing and reports a seeded database as empty. Incidents.razor puts the testid there
// for exactly this. Wait for a row rather than counting whatever the static HTML arrived with.
await page.waitForSelector('[data-testid=incident-link]', { timeout: 60000 }).catch(() => {
    throw new Error('no incident rows rendered within 60s - the circuit never took over, or the database is not seeded')
})

const open = await page.locator('[data-testid=incident-link]').count()
await page.screenshot({ path: `${out}/console-incidents.png` })
console.log(`wrote console-incidents.png (${open} open)`)

// The list defaults to state=open, and a RESOLVED incident is by definition not open - so the
// one thing this release exists to show is filtered out of the default view. The filter is a
// Blazor select bound to a field, not to the query string, so this needs a live circuit.
await page.locator('select').first().selectOption('all')
await page.waitForSelector('.st-resolved', { timeout: 30000 }).catch(() => {
    throw new Error('no resolved incident after selecting state=all - the circuit is not live, or this image predates the live captures')
})
await page.evaluate(() => document.fonts.ready)
await page.screenshot({ path: `${out}/console-incidents-all.png` })
console.log('wrote console-incidents-all.png (every state)')

// Collect hrefs WHILE STILL ON THE LIST. Navigating first and then looking for a list link is
// how this failed once: a detail page has no incident rows, so the locator waited for
// something that could not appear.
const resolvedHref = await page.locator('tr:has(.st-resolved) [data-testid=incident-link]')
    .first().getAttribute('href')
const deniedHref = await page.locator('tr').filter({ hasText: 'PolicyDenied' })
    .locator('[data-testid=incident-link]').first().getAttribute('href').catch(() => null)

// The two frames this release turns on, named rather than "whichever row sorts first".
// Viewport-height, NOT fullPage. A detail page renders every signal the incident carries, and
// the policy-denied capture carries 211 of them - fullPage produced a 26,586px image, which is
// a complete record of nothing anybody will look at. The frame that matters is the top: the
// state, the badges, the callout saying what happened and why. The full trace is a click away
// on demo.hephaisto.dev, which is what that site is for.
await go(abs(resolvedHref))
await page.screenshot({ path: `${out}/console-incident-resolved.png` })
console.log(`wrote console-incident-resolved.png (${resolvedHref})`)

if (deniedHref) {
    await go(abs(deniedHref))
    await page.screenshot({ path: `${out}/console-incident-denied.png` })
    console.log(`wrote console-incident-denied.png (${deniedHref})`)
}

await go('/status')
await page.screenshot({ path: `${out}/console-status.png` })
console.log('wrote console-status.png')

await browser.close()
