// theme-without-fonts, not theme: the default bundles Inter as fourteen woff2 files, and
// custom.css re-points --vp-font-family-base at Gabarito, so every one of them would ship
// unreferenced. This repo self-hosts exactly two faces on every surface and this keeps that true.
import DefaultTheme from 'vitepress/theme-without-fonts'
import { useData } from 'vitepress'
import { watch } from 'vue'
import './custom.css'

/**
 * The theme bridge.
 *
 * VitePress signals its resolved appearance by putting a `.dark` class on <html>. tokens.css does
 * not know about that class - it keys off `data-theme` and `prefers-color-scheme`, and its cascade
 * is deliberate enough to have a truth table written next to it. So the two have to be joined, and
 * this is the join.
 *
 * Note the asymmetry that makes it work: the token file has NO `[data-theme="dark"]` block, because
 * dark IS `:root`. Setting `data-theme="dark"` therefore does its work by suppressing the light
 * media query rather than by declaring anything. `data-theme="light"` does have a block and forces
 * light. Writing one of the two explicitly on every change covers all six rows of that table.
 *
 * A watcher alone would flash: it runs after hydration, and the first paint would use the OS
 * preference even for a reader who chose the other one. config.ts injects the same decision as a
 * blocking inline script in <head>, which is what actually prevents that. This watcher is what
 * keeps it right afterwards, when the reader uses the toggle.
 */
export default {
    extends: DefaultTheme,
    setup() {
        const { isDark } = useData()

        if (typeof document === 'undefined') {
            return
        }

        watch(
            isDark,
            (dark) => {
                document.documentElement.dataset.theme = dark ? 'dark' : 'light'
            },
            { immediate: true },
        )
    },
}
