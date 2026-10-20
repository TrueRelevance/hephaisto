import { defineConfig } from 'vitepress'

/**
 * The domain, in one place.
 *
 * It appears in exactly four places across the repository - here, in demo-site/site.js, in the nav
 * and footer of website/index.html, and in the GitHub repository's homepage field. Changing the
 * TLD is a four-line diff, which is the point: the sites were built before the domain was bought.
 */
const SITE = 'https://hephaisto.dev'
const DOCS = `${SITE.replace('https://', 'https://docs.')}`
const DEMO = `${SITE.replace('https://', 'https://demo.')}`
const REPO = 'https://github.com/TrueRelevance/hephaisto'

/**
 * Resolve the theme before first paint.
 *
 * VitePress ships its own version of this for its `.dark` class. tokens.css keys off `data-theme`
 * instead, so without this the first paint uses the OS preference and then snaps to the reader's
 * stored choice - a flash of the wrong theme on every cold load. Reads the same storage key
 * VitePress writes, so the two can never disagree.
 */
const THEME_SCRIPT = `
(() => {
  try {
    const stored = localStorage.getItem('vitepress-theme-appearance') || 'auto'
    const dark = stored === 'dark'
      || (stored !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches)
    document.documentElement.dataset.theme = dark ? 'dark' : 'light'
  } catch (e) {
    /* Private mode, or storage disabled. The media query in tokens.css is the fallback. */
  }
})()
`

export default defineConfig({
    title: 'Hephaisto',
    description:
        'An autonomous SRE agent for Kubernetes. It investigates incidents, writes a diagnosis '
        + 'citing its evidence, and acts only inside limits you set.',
    lang: 'en-GB',
    cleanUrls: true,
    lastUpdated: true,

    // The site is small enough to check every link, and the whole argument for keeping docs in the
    // repo is that they cannot drift from it. A dead link is drift that reported itself.
    ignoreDeadLinks: false,

    sitemap: { hostname: DOCS },

    head: [
        ['link', { rel: 'icon', href: '/favicon.svg', type: 'image/svg+xml' }],
        // These two literals are the only colours outside tokens.css on this surface, for the same
        // reason App.razor and website/index.html are allowed theirs: theme-color paints the
        // browser's own chrome and is read before any CSS, so it cannot be a var(). A test asserts
        // they equal --bg in each theme.
        ['meta', { name: 'theme-color', content: '#212123', media: '(prefers-color-scheme: dark)' }],
        ['meta', { name: 'theme-color', content: '#f8f7f8', media: '(prefers-color-scheme: light)' }],
        ['meta', { property: 'og:type', content: 'website' }],
        ['meta', { property: 'og:site_name', content: 'Hephaisto' }],
        ['script', {}, THEME_SCRIPT],
    ],

    themeConfig: {
        siteTitle: 'Hephaisto',
        logo: '/favicon.svg',

        nav: [
            { text: 'Guide', link: '/guide/what-it-is', activeMatch: '/guide/' },
            { text: 'Reference', link: '/reference/configuration', activeMatch: '/reference/' },
            { text: 'Operate', link: '/operate/verify', activeMatch: '/operate/' },
            { text: 'How it works', link: '/internals/architecture', activeMatch: '/internals/' },
            { text: 'Demo', link: DEMO },
            { text: 'Site', link: SITE },
        ],

        sidebar: [
            {
                text: 'Guide',
                collapsed: false,
                items: [
                    { text: 'What it is', link: '/guide/what-it-is' },
                    { text: 'See it without a cluster', link: '/guide/without-a-cluster' },
                    { text: 'Requirements', link: '/guide/requirements' },
                    { text: 'Install', link: '/guide/install' },
                    { text: 'Observe → DryRun → Auto', link: '/guide/promotion-path' },
                ],
            },
            {
                text: 'Reference',
                collapsed: false,
                items: [
                    { text: 'How configuration works', link: '/reference/configuration' },
                    { text: 'Helm values', link: '/reference/helm-values' },
                    { text: 'Agent options', link: '/reference/agent-options' },
                    { text: 'Reserved env and safety rails', link: '/reference/env-and-rails' },
                    { text: 'HTTP surface', link: '/reference/http-api' },
                    { text: 'hephaisto-eval CLI', link: '/reference/cli' },
                ],
            },
            {
                text: 'Operate',
                collapsed: false,
                items: [
                    { text: 'Verify your install', link: '/operate/verify' },
                    { text: 'Troubleshooting', link: '/operate/troubleshooting' },
                    { text: 'Alerting and hephaisto_kind', link: '/operate/alerting' },
                    { text: 'Notifications', link: '/operate/notifications' },
                    { text: 'GitHub issues as work', link: '/operate/github-issues' },
                    { text: 'The planner', link: '/operate/planner' },
                ],
            },
            {
                text: 'How it works',
                collapsed: false,
                items: [
                    { text: 'Architecture', link: '/internals/architecture' },
                    { text: 'The safety model', link: '/internals/safety-model' },
                    { text: 'What the agent is told', link: '/internals/prompts' },
                    { text: 'Incident reference', link: '/internals/runbooks/' },
                    { text: 'Evaluation and evidence', link: '/internals/evaluation' },
                ],
            },
            {
                text: 'Project record',
                collapsed: true,
                items: [
                    { text: 'Why there is one', link: '/project/' },
                    { text: 'Changelog', link: '/project/changelog' },
                ],
            },
        ],

        socialLinks: [{ icon: 'github', link: REPO }],

        editLink: {
            pattern: `${REPO}/edit/main/docs-site/:path`,
            text: 'Edit this page on GitHub',
        },

        search: {
            // Local search is a static index built at compile time. The alternative wants an
            // Algolia account, which is a runtime dependency on somebody else's service for a site
            // whose entire premise is that it has none.
            provider: 'local',
        },

        outline: { level: [2, 3] },

        footer: {
            message: `AGPL-3.0-only · <a href="${REPO}">Source</a>`,
            copyright: 'Hephaisto',
        },
    },

    markdown: {
        lineNumbers: false,

        // NOTE: the runbooks use ```promql and ```logql fences. Shiki has no grammar for either,
        // so each one prints a build warning and renders as plain text. languageAlias does not
        // help - it is applied after the grammar lookup that fails. The fences are correct at
        // their source and the source is what ships to the model, so they stay as they are.

        config: (md) => {
            /*
             * Transcluded files carry repository-relative links.
             *
             * A page like /project/changelog is CHANGELOG.md verbatim, and CHANGELOG.md links to
             * ./docs/history.md because that is where history lives for a reader on GitHub. That
             * link is correct at its source and dead here, and rewriting the source to suit this
             * site would break it for the reader it was written for.
             *
             * So the link is rewritten at render time instead: anything pointing at a path that
             * exists in the repository but not on this site resolves to GitHub. The list is
             * explicit rather than clever, because a heuristic that silently sent a real docs link
             * off-site would be worse than the dead link it replaced.
             */
            const REPO_PATHS = /^\.{0,2}\/?((?:docs|src|charts|infra|scripts|tests|design|demo|website)\/|(?:SECURITY|CONTRIBUTING|CHANGELOG|README|LICENSE|CODEOWNERS)\.md)/

            const defaultRender = md.renderer.rules.link_open
                || ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))

            md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
                const href = tokens[idx].attrGet('href')

                if (href && REPO_PATHS.test(href)) {
                    const clean = href.replace(/^\.{0,2}\//, '')
                    tokens[idx].attrSet('href', `${REPO}/blob/main/${clean}`)
                    tokens[idx].attrSet('target', '_blank')
                    tokens[idx].attrSet('rel', 'noreferrer')
                }

                return defaultRender(tokens, idx, options, env, self)
            }
        },

        // Shiki's own palettes would be the one place on this site where a colour is not a token.
        // Both themes resolve to the same neutral rendering, so code inherits --vp-code-color -
        // which is what the console does with tool output too.
        theme: { light: 'github-light', dark: 'github-dark' },
    },
})
