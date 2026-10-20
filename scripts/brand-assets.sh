#!/usr/bin/env bash
# Renders design/brand/social-preview.png from its HTML source, in the pinned Playwright
# container so the output is identical on every machine - the same reason the visual baselines
# use it. The source links the SHIPPING token file and the SHIPPING fonts, so this card cannot
# drift away from the product the way an exported PNG always eventually does.
set -Eeuo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="mcr.microsoft.com/playwright:v1.62.1-noble"

command -v docker >/dev/null 2>&1 || { echo "docker is required" >&2; exit 127; }

docker run --rm --init -v "$REPO":/work -w /work/design/visual \
    -e PLAYWRIGHT_BROWSERS_PATH=/ms-playwright "$IMAGE" bash -lc '
        set -Eeuo pipefail
        npm ci --no-audit --no-fund --silent
        node -e "
          const { chromium } = require(\"@playwright/test\");
          (async () => {
            const b = await chromium.launch();
            // colorScheme dark explicitly: the container defaults to light, and the card is meant to
            // look like the product, which is dark first.
            const p = await b.newPage({ viewport: { width: 1280, height: 640 }, deviceScaleFactor: 1, colorScheme: \"dark\" });
            await p.goto(\"file:///work/design/brand/social-preview.html\");
            await p.evaluate(() => document.fonts.ready);
            const ok = await p.evaluate(() => document.fonts.check(\"16px Gabarito\"));
            if (!ok) { console.error(\"Gabarito did not load; refusing to render the card\"); process.exit(1); }
            await p.screenshot({ path: \"/work/design/brand/social-preview.png\" });
            await b.close();
          })();
        "
    '

echo "wrote design/brand/social-preview.png"
