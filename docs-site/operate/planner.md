# The planner

*New in v0.15.0. Off by default.*

A project board drifts because nobody walks it. Statuses are set by hand, finished work stays
open, and the question "what do I do today" is answered by scrolling. The planner is a page of the
console, `/planner`, that goes through the open issues of **one GitHub project** with a person:
one issue at a time, with the reason it is in front of them and every action it can take.

It needs [GitHub issues as work](./github-issues) to be enabled, because it uses the agent's GitHub
token. It reads the board when somebody opens the page — never on a timer — and every write is one
click of a person who holds the console's approver role.

## Three views of one board

**Plan the day.** One issue: why it is here, the facts a person decides by, and a button for
everything that can be done about it. *Today* and *not today* go on to the next issue. Beside it
the day as it stands — what is planned, what was put away, what comes next.

**Today.** What is planned, by priority, with *close* and *not today* on each row; what was closed
today; what was put away, each with *back to today*.

**All open.** Everything that is open, grouped under its top-level issue with that issue's
progress, sub-issues one step in under their parent, a *today / not today* switch on every row and
a filter per field of the board.

## Why an issue is in front of you

Four reasons, from facts GitHub states. None of them knows what your board calls its columns.

| Reason | When |
|---|---|
| **merged, still open** | every pull request that names the issue is merged, none is open, and the issue is open |
| **every sub-issue done** | the issue has sub-issues, all of them are closed, and it is open |
| **pull request quiet** | a pull request that names the issue is open and nothing happened on either for `staleAfterDays` |
| **a required field is empty** | one of `requiredFields` has no value on the item |

A pull request *names* an issue when GitHub lists it as closing the issue, or when its description
says `Closes`, `Fixes`, `Resolves` or `Part of` followed by the issue (`#12` in the same
repository, `owner/repo#12`, or the issue's address). A pull request that merely mentions an issue
does not count. An issue that has sub-issues is judged by them, not by pull requests.

The walk goes through what asks for a decision first, in the order of the table; then everything
else, by the options of the field named **Priority** (the first option first) and by the columns
of the field named **Status** from the last to the first. A board without fields of those names is
walked by repository and number.

## What a click does on GitHub

| Button | On GitHub |
|---|---|
| an option of a field | sets that single-select field of the project item |
| **close: done**, **close: not planned** | closes the issue, with that reason |
| **today** | puts the label `today` on the issue, and takes `not today` off |
| **not today** | puts the label `not today` on the issue, and takes `today` off |
| the marked one of the two, again | takes the label off |
| an account under *assignee* | assigns the issue to it, or takes it off |

Everything is done by the **agent's account**. The audit trail records the person who clicked: a
row `planner.field`, `planner.close`, `planner.day` or `planner.assign` is written *before* GitHub
is called, and a refusal is a second row, `planner.refused`. Nothing here can be clicked by
somebody without the approver role, or — where sign-in is off — without a name.

### The day is two labels

`today` stays on an issue until somebody takes it off or closes the issue. An issue that was
planned on an earlier day and is still open says so, *from an earlier day*, and a click on *today*
keeps it for today. `not today` counts only on the day it was set: an issue that was put away
comes back the next morning by itself, and the old label is replaced the next time the issue is
planned. A day is a UTC day.

GitHub creates a label a repository does not have, so nobody has to prepare the repositories.

### Assigning the agent's account hands the issue over

The agent's own account is the first under *assignee*. In a repository listed in
`github.issues.repositories`, assigning an issue to it is what
[GitHub issues as work](./github-issues) means: the issue becomes a work item and is planned.
Anywhere else it is an assignment and nothing more.

### A board may act too

A project's own workflows keep running. On a board with GitHub's *auto-close issue* workflow,
setting Status to the last column closes the issue; with *item closed*, closing an issue moves it
there. The planner shows what GitHub says after the click, not what it expected.

## Chart values

```yaml
github:
  enabled: true              # the planner has no token of its own
  # ... see GitHub issues as work

  planner:
    enabled: true
    owner: example           # github.com/orgs/<owner>/projects/<number>
    number: 3
    requiredFields:          # fields an item should not leave empty; default: none
      - Priority
      - Area
    staleAfterDays: 5        # the quiet period for an open pull request
    assignees:               # logins besides the agent's own account
      - octocat
```

The chart refuses `planner.enabled` without `github.enabled`, without an owner or without a
number, and the agent refuses the same at startup.

## What the token needs

More than issues as work does: a project is not a repository, and the board holds issues of
repositories the agent was never asked to work in.

| | Classic token | Fine-grained token |
|---|---|---|
| read the project, set its fields | `project` | organisation permission **Projects: read and write** |
| close, label, assign | `repo` | **Issues: read and write** on every repository whose issues are on the board |

The classic column is what was measured against github.com (`scripts/e2e/planner-live.sh`, every
button, read back from GitHub). The fine-grained column is GitHub's documentation and was **not**
measured.

Without the first, the page says that the board could not be read, in GitHub's words. Without the
second for some repository, a click on one of its issues is refused, the page says so, and nothing
changed. An issue of a repository the token cannot see is not on the page at all.

## Seeing it work

```sh
curl -s https://hephaisto.example.com/api/planner | jq '{state, problem, title, open: (.items | length)}'
```

`state` is `Off`, `Read`, or `Unreadable` with `problem` saying why. With `Read` and a `problem`,
the newest read failed and the items are an older board's: the page keeps the last good one and
says that it is old. `?refresh=true` asks GitHub now; otherwise the answer may be half a minute
old.

The writes are `POST /api/planner/items/{itemId}/field`, `/close`, `/day` and `/assignee`, behind
the approver policy, with `decidedBy` in the body where nobody is signed in.

## Limits, and what is not built

- **One project, of an organisation.** A project owned by a user account is not read.
- **Single-select fields only.** Text, number, date and iteration fields are not shown.
- **Issues only.** Drafts and pull requests on the board are left out.
- **GitHub can lose an item.** An issue added to a project sometimes gets an item and never
  appears in the project's items — not on the planner, and not on the board either. Taking it off
  the project and adding it again lists it. Seen three times in seven on 2026-10-13.
- **The label's date comes from the issue's timeline**, of which the newest forty label and
  cross-reference events are read. On an issue with more, an old `today` is taken to be from an
  earlier day and an old `not today` to have expired — which is what they are.
- **A thousand items.** A larger project is read up to that, and the log says so.
- **No MCP tool** reads or drives the planner.
- **A quiet issue without a pull request is not noticed.** "In progress and nothing happens" would
  need to know which column means in progress.
