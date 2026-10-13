#!/usr/bin/env bash
#
# The planner against the REAL github.com (#300): the dev agent on studio-rancher-desktop, one
# real project, and the console's own suite pressing every button of /planner - with GitHub, read
# through the `gh` of whoever runs this, saying what each click did.
#
#   scripts/e2e/planner-live.sh            # make the issues, run the suite, delete the issues
#   scripts/e2e/planner-live.sh --sweep    # delete whatever an aborted run left, and stop
#   scripts/e2e/planner-live.sh --keep     # leave the issues where they are afterwards
#
# The same specs run for $0 against the stand-in's board (scripts/e2e/ui, GITHUB_STANDIN_URL),
# which answers what this project's authors believed GitHub answers. This asks GitHub: the
# project over GraphQL through the egress proxy, a field set by four ids, an issue closed by its
# node id, a label that GitHub creates where the repository has none, an account assigned.
#
# WHAT IT NEEDS (tilt_config.json: "github": "live", "planner-live": true, "coder": true;
# charts/hephaisto/values-dev-planner-live.yaml says the rest):
#   - the Secret hephaisto-github-planner-live in namespace hephaisto, key GITHUB_TOKEN: a token
#     that may read and write the organisation's projects and the sandbox's issues. From the
#     `gh` of the person running this, without the token ever being printed:
#
#       gh auth token | tr -d '\n' | kubectl --context studio-rancher-desktop -n hephaisto \
#           create secret generic hephaisto-github-planner-live --from-file=GITHUB_TOKEN=/dev/stdin
#
#     and afterwards: kubectl --context studio-rancher-desktop -n hephaisto delete secret hephaisto-github-planner-live
#   - `gh` on this machine, logged in as somebody who may write the project and administer the
#     sandbox (deleting an issue needs that).
#
# WHAT IT WRITES, and where. Seven issues in the sandbox, each added to the project, each named
# "[planner-e2e] ...". Every click of the suite is on one of them: sixteen field changes or so,
# two labels on and off, two accounts assigned and taken off, three issues closed. Of everything
# else on the board it only reads. Afterwards the seven are taken off the project and deleted,
# and the labels `today` and `not today` are removed from the sandbox when this run created them.
#
# ASSIGNING THE BOT IS A HAND-OVER. The suite assigns the bot's account to one sandbox issue for
# a moment. An agent that lists the sandbox takes that issue as work and may start a plan Job
# before the suite takes the bot off again - with a real coder, that is a real plan. The issue
# is deleted at the end either way.
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

CONTEXT=studio-rancher-desktop
SANDBOX=TrueRelevance/hephaisto-sandbox
MARK='[planner-e2e]'
ROLES=(fields day closeDone closeNotPlanned closeFromToday assign row)

say()  { printf '%s\n' "$*"; }
fail() { printf 'planner-live: %s\n' "$*" >&2; exit 1; }

mode=run
keep=false
for arg in "$@"; do
    case "$arg" in
        --sweep) mode=sweep ;;
        --keep) keep=true ;;
        *) fail "unknown argument: $arg" ;;
    esac
done

for tool in gh jq kubectl curl; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool is not installed"
done

gh auth status >/dev/null 2>&1 || fail "gh is not logged in"

# One GraphQL request with gh, and its failure as a failure.
gql() { gh api graphql "$@"; }

# What an aborted run left: open issues of the sandbox whose title carries the mark.
leftovers() {
    gh issue list --repo "$SANDBOX" --state all --limit 100 --search "in:title \"$MARK\"" --json number,title \
        --jq ".[] | select(.title | startswith(\"$MARK\")) | .number"
}

# Takes an issue off the project and deletes it. Deleting is what keeps the sandbox, and the
# board's "closed today", free of this suite.
remove() {
    local number=$1 item
    item=$(gql -f owner="${SANDBOX%%/*}" -f name="${SANDBOX##*/}" -F number="$number" -f query='
        query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { issue(number: $number) {
          projectItems(first: 10) { nodes { id project { id } } } } } }' \
        --jq '.data.repository.issue.projectItems.nodes[] | "\(.project.id) \(.id)"' 2>/dev/null || true)

    while read -r project id; do
        [ -n "${id:-}" ] || continue
        gql -f project="$project" -f item="$id" -f query='
            mutation($project: ID!, $item: ID!) { deleteProjectV2Item(input: {projectId: $project, itemId: $item}) { deletedItemId } }' >/dev/null || true
    done <<<"$item"

    gh issue delete "$number" --repo "$SANDBOX" --yes >/dev/null 2>&1 || say "could not delete $SANDBOX#$number; do it by hand"
}

# The numbers of the sandbox's issues that the project's LISTING holds - which is what the
# planner reads, and what the board shows.
listed() {
    gql -f owner="$owner" -F number="$number" -f q="repo:$SANDBOX" -f query='
        query($owner: String!, $number: Int!, $q: String!) { organization(login: $owner) { projectV2(number: $number) {
          items(first: 100, query: $q) { nodes { content { ... on Issue { number } } } } } } }' \
        --jq '.data.organization.projectV2.items.nodes[].content.number'
}

# Adds an issue to the project and waits until the project lists it.
#
# GITHUB LOSES ITEMS. Measured on 2026-10-13: of seven issues added one after the other, three
# got an item id, said on the issue's own page that they were on the project - and never appeared
# in the project's items, ten minutes later neither, by any page size, order or filter. Taking
# such an item off the project and adding it again lists it within twenty seconds. So each issue
# is added alone, waited for, and added again when the listing does not come to hold it.
put_on_board() {
    local n=$1 node item now
    node=$(gh api "repos/$SANDBOX/issues/$n" --jq .node_id)

    for _ in 1 2 3; do
        item=$(gql -f project="$project_id" -f content="$node" -f query='
            mutation($project: ID!, $content: ID!) { addProjectV2ItemById(input: {projectId: $project, contentId: $content}) { item { id } } }' \
            --jq .data.addProjectV2ItemById.item.id)

        for _ in $(seq 1 8); do
            sleep 4

            # Into a variable first: under pipefail, `listed | grep -q` fails whenever grep has
            # its match before gh has written its last line.
            now=$(listed)

            if grep -qx "$n" <<<"$now"; then
                return 0
            fi
        done

        say "  GitHub gave $SANDBOX#$n an item and does not list it; taking it off and adding it again"
        gql -f project="$project_id" -f item="$item" -f query='
            mutation($project: ID!, $item: ID!) { deleteProjectV2Item(input: {projectId: $project, itemId: $item}) { deletedItemId } }' >/dev/null || true
        sleep 3
    done

    return 1
}

if [ "$mode" = sweep ]; then
    count=0
    for number in $(leftovers); do
        remove "$number"
        say "removed $SANDBOX#$number"
        count=$((count + 1))
    done
    say "sweep: $count issue(s) removed"
    exit 0
fi

# ---- what this runs against, read off the Deployment and off the agent ------------------------

env_of() {
    kubectl --context "$CONTEXT" -n hephaisto get deploy hephaisto \
        -o jsonpath="{.spec.template.spec.containers[0].env[?(@.name=='$1')].value}"
}

[ "$(env_of GitHub__Planner__Enabled)" = true ] || fail 'the agent has no planner: tilt_config.json needs "github": "live" and "planner-live": true'

api=$(env_of GitHub__ApiBaseUrl)
[ "${api:-https://api.github.com}" = https://api.github.com ] || fail "the agent talks to $api, not to github.com; this suite is for the live tier"

owner=$(env_of GitHub__Planner__Owner)
number=$(env_of GitHub__Planner__Number)
[ -n "$owner" ] && [ -n "$number" ] || fail "the agent's planner names no project"

HOST=$(jq -r '.host // "localhost"' tilt_config.json)
URL=${HEPHAISTO_URL:-http://$HOST:8100}

state=$(curl -fsS -m 30 "$URL/api/planner?refresh=true" | jq -r '"\(.state) \(.problem // "")"')
[ "${state%% *}" = Read ] || fail "the agent cannot read $owner/projects/$number: $state"

project=$(gql -f owner="$owner" -F number="$number" -f query='
    query($owner: String!, $number: Int!) { organization(login: $owner) { projectV2(number: $number) { id title } } }' \
    --jq '.data.organization.projectV2 | "\(.id) \(.title)"')
project_id=${project%% *}
[ -n "$project_id" ] || fail "gh cannot see $owner/projects/$number"

[ -z "$(leftovers)" ] || fail "an earlier run left issues in $SANDBOX; scripts/e2e/planner-live.sh --sweep removes them"

say "planner-live: $URL against ${project#* } ($owner/projects/$number), issues in $SANDBOX"

# ---- the issues -------------------------------------------------------------------------------

labels_before=$(gh label list --repo "$SANDBOX" --limit 200 --json name --jq '.[].name')
created=()
items='{}'

cleanup() {
    local status=$?

    if $keep; then
        say "planner-live: --keep, so ${#created[@]} issue(s) stay: ${created[*]:-}"
    else
        for n in "${created[@]:-}"; do
            [ -n "$n" ] && remove "$n"
        done

        # The two labels, when this run is what made them.
        for label in today 'not today'; do
            if ! grep -qxF "$label" <<<"$labels_before"; then
                gh label delete "$label" --repo "$SANDBOX" --yes >/dev/null 2>&1 || true
            fi
        done

        say "planner-live: ${#created[@]} issue(s) removed"
    fi

    exit "$status"
}
trap cleanup EXIT

run=$(date -u +%m%d-%H%M%S)

for role in "${ROLES[@]}"; do
    url=$(gh issue create --repo "$SANDBOX" --title "$MARK $role $run" \
        --body "Made by scripts/e2e/planner-live.sh for the planner's suite, role \`$role\`. It is deleted when the run ends." | tail -1)
    n=${url##*/}
    created+=("$n")

    put_on_board "$n" || fail "GitHub does not list $SANDBOX#$n on the project after three attempts"

    items=$(jq -c --arg role "$role" --arg ref "$SANDBOX#$n" '. + {($role): $ref}' <<<"$items")
    say "  $role: $SANDBOX#$n"
done

# The agent has to see all seven before a spec asks for one.
want=$(jq -r '[.[]] | sort | join(" ")' <<<"$items")
seen=""
for _ in $(seq 1 30); do
    seen=$(curl -fsS -m 30 "$URL/api/planner?refresh=true" |
        jq -r --arg repo "$SANDBOX" '[.items[] | select(.repository == $repo) | "\(.repository)#\(.number)"] | sort | join(" ")')
    [ "$seen" = "$want" ] && break
    sleep 4
done
[ "$seen" = "$want" ] || fail "the agent's board does not hold the new issues: wanted '$want', saw '$seen'"

# ---- the suite --------------------------------------------------------------------------------

HEPHAISTO_URL=$URL PLANNER_E2E_LIVE=1 PLANNER_E2E_ITEMS=$items scripts/e2e/ui/run.sh planner.spec.ts
