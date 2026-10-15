# The planner

*New in v0.15.0. Off by default.*

A project board drifts because nobody walks it. Statuses are set by hand, finished work stays
open, and the question "what do I do today" is answered by scrolling. The planner is a page of the
console, `/planner`, that goes through the open issues of **one GitHub project** with a person:
one issue at a time, with the reason it is in front of them and every action it can take.

It needs [GitHub issues as work](./github-issues) to be enabled, because it uses the agent's GitHub
token. It reads the board when somebody opens the page — never on a timer — and every write is one
click of a person who holds the console's approver role.

## Three views of one board, and a new issue

**Plan the day.** One issue: why it is here, what its author wrote, the facts a person decides by,
and a button for everything that can be done about it. *Today*, *not today* and closing go on to
the next issue; a closed issue's card is one step back and says what was done. Beside it the day
as it stands — what is planned, what was put away, what comes next.

The issue's description is shown under its title as rendered markdown, with its comment count and
its labels. Somebody else wrote it, so the rendering has three rules: none of the author's own
HTML (it shows as the characters it is), a link only to http(s) or mailto (opening in a new tab,
without an opener), and no image loaded - an image becomes a link to it. The same holds for
comments, Claude's analysis and a pull request's description.

Issue references are links, as on GitHub: `#12` in the issue's own repository, `owner/repo#12`,
and the address of an issue or a pull request. One that is on the board opens its page in the
console, in the same tab - an epic's list of work leads to each piece of it; any other goes to
GitHub. Inside code and inside a link nothing becomes one. The card lists an issue's sub-issues
the same way, each a link, and its parent is a link back up. *Hide description* puts
it away for the whole walk, and the browser remembers that.

**Today.** First what is **left over**: planned on an earlier day and still open, the oldest
first, each saying when it was planned (*yesterday*, or the day) with *today* (keep it for today),
*not today* (until tomorrow), *off the plan* (neither label), *close: done* and *details*. Then
what is planned today, by priority, with *close* and *not today* on each row; what was closed
today; what was put away, each with *back to today*. A row reads by the issue's title, which
leads to its details; the reference, type and reason are small beneath it. *Details* on a row opens the issue beside the
list: its description, its labels, the same buttons as *plan the day*, Claude's newest analysis of
it, and the line that hands it to Claude Code - `Execute github issue owner/repo#n` - with a copy
button. The address carries the issue (`/planner?view=today&detail=<item>`), so it can be linked.

**New issue.** A title, a description in markdown, a button per repository it may be opened in, one
per issue type of the organisation, one per option of every field of the board, and its day:
none, today, not today, tomorrow, next week or backlog. *Open issue*
opens it as the agent's account, puts it on the project and sets the fields chosen; with *ask
Claude* ticked, Claude is asked about it next. The form keeps the repository and the type for the
next issue.

**Search.** The board's issues, open, closed or both, by every word of a title, a description, a
label or a reference (`#88` for a number). A hit opens in detail like an issue of the day; a
closed issue's card offers no button, and it can still be commented on. `?q=` and `?state=` are in
the address, so a search can be linked.

Every detail view shows the issue's **comments**, rendered the same way, and a box to write one.
It is posted by the agent's account and ends with *Written by &lt;name&gt; in Hephaisto's planner*;
the audit row is `planner.comment`.

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

*Walk through* chooses what the walk holds and in which order:

| Walk | Holds | Order |
|---|---|---|
| every open item | all open issues | the planner's, as above |
| needs a decision, then the top priorities | those with a reason, and the two highest priorities | the planner's |
| needs a decision only | those with a reason | the planner's |
| newest first | all open issues | by when the issue was opened, the newest first |
| newest first, not planned yet | those with neither `today` nor `not today` | the same |

A newest walk begins at the newest issue whenever it is chosen; the others stay on the issue in
front of the person. `/planner?walk=newest` (or `newest-unplanned`) opens the page on it.

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

### A new issue

| Step | On GitHub |
|---|---|
| **open issue** | opens the issue in the chosen repository, with the chosen type |
| | adds it to the project |
| | sets each field that was chosen |

Three requests, and none is taken back: when a later one is refused, the page says which issue
was opened and what is missing, and the issue is on GitHub to be finished by hand. The audit row
is `planner.create`.

The repositories are `github.planner.repositories`, the first chosen by default, followed by the
repositories of the board's issues, the one with the most first. The types are the
organisation's enabled issue types; a token that may not read them leaves the type out.

### Asking Claude

*Ask Claude* on an issue's details, or the tick on a new issue, starts one **coder Job** of phase
`triage`. It runs Claude Code read-only in dev-context - the team's notes and memory, which is
what places an issue in a service - with the issue's title, author and description and the labels
its repository has. It answers labels, a summary, an analysis, steps and open questions.

What the agent does with the answer:

| | On GitHub |
|---|---|
| labels | puts on those of Claude's labels the repository has - never another, never `today` or `not today`; nothing is taken off |
| the rest | posts one comment: *Claude's analysis*, the plan, the questions, and the line for Claude Code |

The comment is what the details show, read back from the issue: the newest that begins with
Hephaisto's marker **and** was written by the agent's account - anybody can type the marker. A
model wrote it about somebody else's text: it is rendered under the rules above, and on GitHub it
mentions nobody and links nothing.

The audit trail names the person who asked: `planner.triage` when the Job starts,
`planner.triaged` when its answer is applied. At most `maxConcurrentJobs` run at once and one per
issue; a click beyond that says so.

### A later day is a date on the board

*Tomorrow* and *next week* (the next Monday) take both labels of the day off and set the board's
date field `dayField` (default **Planned for**) to that day. On that day the issue is planned for
today without anybody writing anything; a day that passed with the issue open makes it left over.
The today view lists what is planned for a later day, the soonest first, with *today* to bring it
forward. *Backlog* takes the labels and the date off and sets Status to `backlogStatus` (default
**Backlog**). A label set today wins over the date, and setting today, not today or taking the day
back empties the date.

The field is the board's own: create a field of type **Date** with that name on the project.
Without it, *tomorrow* and *next week* are refused and say why; today, not today and backlog work.

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
    repositories:            # where a new issue is opened; the first is the default
      - example/planning
    dayField: Planned for    # the board's Date field tomorrow / next week set
    backlogStatus: Backlog   # the Status option backlog sets
    triage:                  # Claude labels and analyses an issue when asked
      enabled: true          # needs codeFix.enabled
      maxCostUsd: 2
      maxTurns: 40
      deadline: "00:15:00"
      maxConcurrentJobs: 2

codeFix:
  enabled: true              # the image, Secret, egress proxy, model and dev-context of the Job
```

The chart refuses `planner.enabled` without `github.enabled`, without an owner or without a
number, and `planner.triage.enabled` without `codeFix.enabled`; the agent refuses the first three
at startup, and a click on *ask Claude* says so when the coder has no image or no dev-context.

Asking Claude costs one read-only Claude Code run on the coder's model credential: notional with
a subscription token (`CLAUDE_CODE_OAUTH_TOKEN`), metered with an API key, and capped by
`maxCostUsd` either way. The coder image must be of this release - an older one does not know the
`triage` phase and answers every such Job with a failure.

## What the token needs

More than issues as work does: a project is not a repository, and the board holds issues of
repositories the agent was never asked to work in.

| | Classic token | Fine-grained token |
|---|---|---|
| read the project, set its fields | `project` | organisation permission **Projects: read and write** |
| close, label, assign | `repo` | **Issues: read and write** on every repository whose issues are on the board |
| open a new issue | `repo` | **Issues: read and write** on every repository in `repositories` |
| put it on the project | `project` | organisation permission **Projects: read and write** |
| comment on an issue | `repo` | **Issues: read and write** on its repository |

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
the approver policy, with `decidedBy` in the body where nobody is signed in. A new issue is
`POST /api/planner/issues` (`repository`, `title`, `body`, `type`, `fields`, `triage`), asking
Claude `POST /api/planner/triage` (`repository`, `number`), and what Claude said
`GET /api/planner/analysis?repository=&number=` - the run as far as the agent knows it, the
newest analysis on the issue, and the line for Claude Code. A later day is
`POST /api/planner/items/{itemId}/schedule` (`when`: `tomorrow`, `next week`, `backlog`); a new
issue takes `day` with any of those or `today` / `not today`. `GET /api/planner/search?q=&state=`
searches the board, `GET` and `POST /api/planner/comments` read and write an issue's comments.

```sh
kubectl -n hephaisto-coder get jobs -l hephaisto.dev/phase=triage    # a run in progress
```

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
- **A run of Claude's lives in the agent's memory**, its answer on GitHub. A restart forgets a run
  that was still going: the Job ends by its deadline, nothing is put on the issue, and *ask Claude*
  asks again. The analysis shown is read from the issue's first hundred comments.
- **Claude labels, and does not set fields.** Priority, area and status are the person's buttons.
- **A quiet issue without a pull request is not noticed.** "In progress and nothing happens" would
  need to know which column means in progress.
