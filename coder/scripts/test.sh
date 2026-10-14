#!/usr/bin/env bash
# Runs the coder runner's test suite and fails if fewer than MIN_TESTS ran - a suite that
# silently discovers nothing is the green that tested nothing.
#
#   scripts/test.sh                       natively (node 24, git, jq, bash on PATH)
#   scripts/test.sh --in-image [image]    inside the built image (default hephaisto/coder:dev),
#                                         read-only root, exactly as a Job would run it
set -euo pipefail

MIN_TESTS=${MIN_TESTS:-655}
here=$(cd "$(dirname "$0")/.." && pwd)

if [ "${1:-}" = "--in-image" ]; then
  image=${2:-hephaisto/coder:dev}
  exec docker run --rm --read-only \
    --tmpfs /tmp:rw,exec,size=2g,mode=1777 \
    -e HOME=/tmp/home -e MIN_TESTS="$MIN_TESTS" \
    --entrypoint /opt/coder/scripts/test.sh "$image"
fi

cd "$here"
# natively, compile first (bin/guard and the SIGTERM test run dist/); the image ships dist/ read-only
if [ -w "$here" ]; then npx tsc -p tsconfig.json; fi

report=$(mktemp "${TMPDIR:-/tmp}/coder-vitest.XXXXXX")
status=0
npx vitest run --configLoader runner --reporter=default --reporter=json --outputFile="$report" || status=$?

total=$(jq '.numTotalTests // 0' "$report")
passed=$(jq '.numPassedTests // 0' "$report")
failed=$(jq '.numFailedTests // 0' "$report")
rm -f "$report"
echo "coder tests: $passed passed, $failed failed, $total total (floor $MIN_TESTS)"
if [ "$status" -ne 0 ] || [ "$failed" -ne 0 ]; then
  echo "coder tests FAILED" >&2
  exit 1
fi
if [ "$total" -lt "$MIN_TESTS" ]; then
  echo "only $total tests ran, fewer than the floor of $MIN_TESTS: a test file stopped being discovered" >&2
  exit 1
fi
