#!/usr/bin/env bash
# Invoked by scripts/e2e/run.sh with HEPHAISTO_URL pointing at its port-forward.
#
# Installs on first use rather than checking a node_modules into the repo. The browser
# download is ~150MB and cached in ~/.cache/ms-playwright, so this is slow once and fast
# afterwards.
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

command -v npm >/dev/null 2>&1 || { echo "npm is not installed; skipping the UI suite" >&2; exit 127; }

if [ ! -d node_modules ]; then
    echo "installing @playwright/test (first run only)"
    npm install --no-audit --no-fund --silent
fi

# --with-deps is Linux-only and fails on macOS; the browser alone is what is needed here.
#
# NOT silenced. It used to be `>/dev/null 2>&1 || <retry>`, which made this the one step in the
# suite you could not watch - and it is the step that hangs. On a cold cache it downloads ~130MB
# and then unzips it, and with the output suppressed a run that had wedged looked exactly like a
# run that was working. Twice.
echo "ensuring the chromium build playwright wants is present (this is slow on a cold cache)"
npx --no-install playwright install chromium || npx playwright install chromium

# NOT `exec`: the exit status of `playwright test` is not sufficient on its own.
#
# Playwright exits 0 when every spec is SKIPPED, and that is exactly what happened - the
# committed report read {"expected":0,"skipped":5,"ok":true} and the harness recorded the phase
# as a pass on a run that asserted nothing. A suite that silently stops running is worse than
# one that fails, because it keeps reporting the last thing anyone believed about the console.
rm -f results.json

status=0
npx --no-install playwright test "$@" || status=$?

if [ ! -f results.json ]; then
    echo "playwright wrote no results.json; cannot tell a pass from a suite that never ran" >&2
    exit 1
fi

expected=$(jq -r '.stats.expected // 0' results.json)
skipped=$(jq -r '.stats.skipped  // 0' results.json)
unexpected=$(jq -r '.stats.unexpected // 0' results.json)

echo "playwright: expected=$expected skipped=$skipped unexpected=$unexpected"

# Known red (KNOWN_RED, tests/helpers.ts knownRed): a listed test that failed is counted as
# expected by Playwright itself, so it is named here - a green run must not hide what is still red.
known=$(jq -r '[ .. | objects | select(has("annotations")) | .annotations[]? | select(.type == "fail")
                | select((.description // "") | startswith("KNOWN RED")) | .description ] | unique | .[]' results.json 2>/dev/null || true)
if [ -n "$known" ]; then
    echo "playwright: known red, expected to fail (scripts/e2e/ui/KNOWN_RED; UI_STRICT=1 runs them for real):"
    printf '%s\n' "$known" | sed 's/^/  - /'
fi

if [ "$expected" -eq 0 ]; then
    echo "no spec actually ran (expected=0); refusing to report this as a pass" >&2
    exit 1
fi

# A skip is admitted only when it SAYS WHY, which is the "or say so" half of the rule above.
#
# #1 was filed because this suite reported a PASS on a run that asserted nothing - silence was
# the fault, not skipping. Two acting specs need an incident that produced a plan, and whether
# any does is the planner's judgement rather than a property of the console (#66, #79). Failing
# there reports a model declining as if the console were broken; skipping silently would be #1
# again. So: a skip carrying a PRECONDITION marker is reported and allowed, and anything else
# still fails the phase.
if [ "$skipped" -ne 0 ]; then
    stated=$(jq -r '
        [ .. | objects | select(has("annotations"))
          | .annotations[]? | select(.type == "skip")
          | select((.description // "") | startswith("PRECONDITION:")) ] | length
    ' results.json 2>/dev/null || echo 0)

    if [ "${stated:-0}" -ge "$skipped" ]; then
        echo "playwright: $skipped spec(s) skipped, all naming an unmet precondition:"
        jq -r '.. | objects | select(has("annotations")) | .annotations[]?
               | select(.type == "skip") | select((.description // "") | startswith("PRECONDITION:"))
               | "  - " + .description' results.json 2>/dev/null | sort -u
    else
        echo "$skipped spec(s) skipped and only ${stated:-0} said why; the console suite must run in full or say so" >&2
        exit 1
    fi
fi

exit "$status"
