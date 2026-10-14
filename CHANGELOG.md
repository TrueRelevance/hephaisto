# Changelog

What changed in each release, for someone deciding whether to upgrade.

Two companions carry the rest, and this file deliberately does not duplicate them:
[`docs/history.md`](docs/history.md) is the engineering record — what was learned doing the work,
including the wrong turns — and the [issues](https://github.com/TrueRelevance/hephaisto/issues) are everything known to be broken, with the
evidence for each. Until 2026-10-06 that was [`docs/backlog.md`](docs/backlog.md), frozen since;
the links to it below are to that record.

Versions are set by the git tag through MinVer; the chart version and the app version are always
the same number.

## v0.15.0 — unreleased

**A board somebody walks.** v0.14.0 let Hephaisto be handed a GitHub issue. This release is
about the board those issues are on ([#300](https://github.com/TrueRelevance/hephaisto/issues/300)):
a page of the console that goes through one GitHub project with a person, one issue at a time,
and lets them decide each with a click. It is off unless `github.planner.enabled` is set, and an
install that leaves it off gets a navigation entry whose page says so, and nothing else. How to
turn it on - the project, the token, the values - is one page:
[The planner](https://docs.hephaisto.dev/operate/planner).

### New
- **`/planner`: one project's open issues, in three views**
  ([#300](https://github.com/TrueRelevance/hephaisto/issues/300)). *Plan the day* shows one
  issue at a time - why it is in front of the person, the facts, and a button for everything
  that can be done about it. *Today* is the day's plan with what was closed and what was put
  away. *All open* groups everything under its top-level issue, by priority, sub-issues under
  their parent. The board is read when the page opens and on "refresh", never on a timer.
  The card shows the issue's description as its markdown source, with its comment count and
  labels, and it can be put away for the whole walk. Today, not today and closing go on to the
  next issue.
- **Four reasons an issue asks for a decision**, from facts and not from column names: every
  pull request that names it is merged and it is still open; every sub-issue is done; a pull
  request is open and nothing happened for `github.planner.staleAfterDays`; a field named in
  `github.planner.requiredFields` is empty. A pull request names an issue when GitHub lists it as
  closing it or its description says `Closes` or `Part of` followed by the issue.
- **Everything is a click, by an approver, recorded under their name.** Set any single-select
  field of the board, close an issue as done or as not planned, plan it for today or put it away,
  assign it. GitHub is told by the agent's account; the audit trail (`planner.field`,
  `planner.close`, `planner.day`, `planner.assign`, and `planner.refused` when GitHub said no)
  names the person, and its row is written before GitHub is called.
- **The day is two labels on the issue**, `today` and `not today`. `today` stays until somebody
  takes it off; from an earlier day it is shown as carried over. `not today` counts only on the
  day it was set, so what was put away comes back the next morning by itself.
- **A new issue, written in the console.** *New issue* opens one in a repository picked by a
  button (`github.planner.repositories`, the first by default, then the board's own),
  with its title, its description, one of the organisation's issue types and any field of the
  board. The agent's account opens it, puts it on the project and sets the fields; the audit row
  is `planner.create`.
- **Claude labels an issue and writes an analysis and a plan of it**, when a person asks: a tick
  on a new issue, or *ask Claude* on an issue's details (`github.planner.triage.enabled`). It is a
  coder Job of a new phase, `triage` - the coder's image, Secret, egress proxy and model, dev-context
  to read and no repository - that answers labels, a summary, an analysis, steps and questions.
  The agent puts on the labels the repository has (never another, never `today` or `not today`)
  and posts the rest as one comment; `planner.triage` and `planner.triaged` record who asked.
- **Newest first.** Two more walks through the board: every open issue by when it was opened,
  the newest first, and the same for those not planned yet - what came in since the last look.
  `/planner?walk=newest` links to it; `GET /api/planner` gives each item its `createdAt`.
- **Today, in detail.** Every issue of the day opens beside its row with its description, its
  labels and the same buttons as *plan the day*, Claude's newest analysis read back from the
  issue, and the line that hands the issue to Claude Code, `Execute github issue owner/repo#n`,
  with a copy button.
- **`GET /api/planner`** and four writes behind the approver policy:
  `POST /api/planner/items/{itemId}/field`, `/close`, `/day`, `/assignee`; since rc3 also
  `POST /api/planner/issues`, `POST /api/planner/triage` and `GET /api/planner/analysis`.
- **The triage contract**, version 1: `triage-request.schema.json` and `triage-result.schema.json`
  in dev-context, vendored like the code-fix contract.
- **The agent's GitHub client speaks GraphQL**, for the project only: a project is nowhere else.
  The token, the egress proxy, the rate-limit hold and "it retries nothing" are the REST calls'.

### Changed
- **The agent can close an issue now** - from the planner, on a person's click, and from nowhere
  else. Until this release its GitHub client held "nothing that deletes or closes"; it still
  deletes nothing, and a test holds that only the planner's service calls what closes, labels or
  assigns.

### Upgrading
- Nothing to do for an install that does not enable the planner.
- **The planner needs more of the agent's token than issues as work does**: the organisation's
  projects (classic `project`; fine-grained "Projects: read and write"), and issues read and
  write on *every* repository whose issues are on the board, not only the ones in
  `github.issues.repositories`. Without the first the page says that the board could not be read;
  without the second a click on an issue of that repository is refused and says so.
- Assigning an issue to the agent's own account from the planner hands it over, in a repository
  the install lists - that is what `github.enabled` means, and the planner offers the button.
- **A new issue needs the token to open issues** in the repositories it is opened in (classic
  `repo`; fine-grained "Issues: read and write") and to add items to the project (the same
  "Projects: read and write" as above).
- **Asking Claude needs `codeFix.enabled`** - the chart refuses `github.planner.triage.enabled`
  without it - and a coder image of this release: an older image does not know the `triage`
  phase and answers every such Job with a failure. It runs on the coder's model credential; with
  a subscription token its cost is notional, and `maxCostUsd` (default $2) caps a run.

### Known limits
- One project, of an organisation; single-select fields; issues only.
- A run of Claude's is kept in the agent's memory, its answer on GitHub. A restart forgets a run
  that was still going: its Job ends by its deadline, nothing is put on the issue, and the button
  asks again. The analysis shown is read from the issue's first hundred comments.
- GitHub sometimes gives an issue that was added to a project an item and never lists it - on
  the planner and on the board alike. Taking it off the project and adding it again lists it.

## v0.14.0 — 2026-10-12

**A second way in.** Until now the only thing Hephaisto could be handed was an alert. This
release is about handing it a GitHub issue ([#243](https://github.com/TrueRelevance/hephaisto/issues/243)):
an issue assigned to its account is planned, the plan is posted and answered on the issue, and
the draft pull request that follows closes it. It is off unless `github.enabled` is set, and an
install that leaves it off changes in three small ways, listed under *Upgrading*. How to turn it
on - the account, the two tokens, the values - is one page:
[GitHub issues as work](https://docs.hephaisto.dev/operate/github-issues).

The first full release since v0.12.0: v0.13.0 below went out as release candidates only and is
released by this tag, so an upgrade from v0.12.0 takes both sections, migrations included.
Everything outside this file and `docs/roadmap.md` is as it is in `v0.14.0-rc8`, the candidate
the production install ran from 2026-10-10 until the release.

### New
- **An issue assigned to Hephaisto's account is taken as work** ([#245](https://github.com/TrueRelevance/hephaisto/issues/245)).
  With `github.enabled`, the agent asks GitHub which open issues are assigned to its account in
  the repositories listed in `github.issues.repositories` - once a minute, a free 304 while
  nothing changed - and records each as a work item: `GET /api/workitems` and
  `GET /api/workitems/{id}`. Unassigning the account, or closing the issue, cancels it; assigning
  it again is a new work item, with the issue's text as it is then. An issue in a repository that
  is not listed is never asked about. The agent only asks, so there is no webhook to expose.
- **A taken issue is planned, and the plan is posted on the issue** ([#246](https://github.com/TrueRelevance/hephaisto/issues/246)).
  The same read-only plan Job an escalated incident gets, under the same `codeFix.mode`, switches
  and caps - with `codeFix.mode: off` nothing starts, and the issue is told so. There is no
  incident, no investigation and no running image behind it: the Job reads the issue and the
  repository's default branch. Hephaisto then writes two comments on the issue and never more:
  one that says where the work stands, edited in place as it moves, and one with the plan -
  summary, what will change, files, steps, verification - and how to answer it. The issue's text
  travels to the model as it was when the issue was taken, as data in one element of its own; an
  edit afterwards is not picked up. Unassigning or closing the issue cancels the attempt and
  deletes a running Job. An issue is planned once: to have it planned again, hand it over again.
- **A plan is answered on the issue** ([#247](https://github.com/TrueRelevance/hephaisto/issues/247)).
  An approver replies with a comment whose first line is `/approve`, or `/reject` with a reason
  after it (without one it is recorded as "no reason given"). Nothing else is a command: not
  `/approve please`, not `LGTM /approve`, not a quotation or a code block of either, not
  `/Approve`. Who may answer is `github.approvers` - account **numbers**, because a login can be
  given up and registered by somebody else; the login is what is recorded
  (`approvedBy: github:<login>`, source `GitHub`). The first such comment after the plan
  decides, exactly once, also across an agent restart. What is approved is the plan as Hephaisto
  stored it, never the text of a comment, and a comment that was read once is not read again
  whatever it is edited into. An approval in mode `pr` starts the implementing Job; its draft
  pull request says `Closes owner/repo#n`, its title is `fix:`, `feat:` or `chore:` by the
  issue's type, and its commits carry `Hephaisto-Issue: owner/repo#n`. The issue's status comment
  then links the pull request.
  - **Everybody else is answered once.** A command from an account that is not an approver
    changes nothing and gets one short comment per plan, however many follow; it names the first
    of them in a code span, mentions nobody and does not say who the approvers are.
  - **A refusal says why, once per cause.** With `codeFix.mode` at `plan` or `off`, with the
    emergency stop engaged, or for a plan that needs a change in a second repository, `/approve`
    is refused in one sentence that names the cause. A refusal does not use the plan up: once an
    operator has set the mode to `pr`, a **new** `/approve` is taken. (At `off` every open
    attempt is cancelled, as before.)
  - **Never more than six comments** of Hephaisto's for one work item, whatever anybody writes
    there: the status, the plan, and at most four answers. Beyond that it only edits its status
    comment.
  - With `github.approvers` empty nobody can answer by comment, no comment is read, and the
    plan says that it is answered in the console. The API routes
    `POST /api/workitems/{id}/codefix/{attemptId}/approve` and `.../deny` are as they were, for
    whoever the console's approver policy admits.
- **A planner's questions are on the issue, and an answer reaches a new plan**
  ([#286](https://github.com/TrueRelevance/hephaisto/issues/286),
  [#252](https://github.com/TrueRelevance/hephaisto/issues/252)). The first real plan left an
  entry where it was because the issue did not name it, and asked whether it should move too -
  in a field no comment showed. Now:
  - **A plan can ask.** The plan result has `questions` beside `notes`: what only a person can
    decide, one decision each, with what the plan assumed meanwhile. The plan comment shows them
    as a numbered list and the planner's notes in a fold below; the attempt's page in the console,
    `GET /api/codefixes` and MCP `get_code_fix` carry them too. The planning prompt says that the
    questions will be read and how to ask, and that work beside what the issue names is asked
    about - neither added to the plan nor left out in silence.
  - **`/replan`.** A third command, with the grammar of `/approve` and for the same approvers.
    Answer in a comment, reply `/replan`: the waiting plan ends (rejected, with the reason
    `replanned by github:<login>`), and a new attempt plans the **same** issue with the earlier
    plan and with what the issue's author and the approvers wrote since it was handed over -
    nobody else's comments, never Hephaisto's own. The issue's text is read again at that moment.
    A second plan comment follows and says that it replaces the first.
  - **A failed attempt is not the end.** Its status comment shows what its planner found, asked
    and noted, and `/replan` has it tried again - also after a rejection or an expired plan.
  - **Refused in one sentence, once**: while a Job is running for the issue, once a pull request
    is open, and after the fifth attempt of one hand-over.
- **The pull request is followed to its end.** While it is open Hephaisto asks GitHub about it
  on every poll (a free 304 while nothing changed). Merged, and the work item is `Done`; closed
  without merging, and it is `Cancelled` with `pull request closed without merging`; the status
  comment says which. A merge that closes the issue is `Done`, not "the issue was closed": the
  pull request is read before the list of assigned issues.
- **A finished issue is not started over.** An issue that is still open and assigned when its
  work item ends - merged into another branch than the default one, or closed unmerged - is
  not taken again by the next poll. It is taken again when it is handed over again: Hephaisto
  unassigned and assigned once more, or the issue closed and reopened. `stillAssigned` on a
  work item says that it is held this way.
- **Unassigning stops an implementation too.** The implementing Job is deleted, nothing is
  pushed afterwards, and an `/approve` that arrives with or after the unassignment does nothing.
- **Where an issue's code is.** The first `codeFix.repositories` entry whose `url` names the
  repository (it ends in `/owner/repo` or `/owner/repo.git`) gives the clone URL, the branch and
  the path; with no such entry it is `https://github.com/owner/repo` on the default branch
  GitHub reports. The host has to be in `codeFix.allowedRepositoryHosts` either way, and the
  repository enabled in the context repository's `repos.yaml`.
- `GET /api/codefixes` rows carry `workItemId`, `issue` (`owner/repo#12`), `issueUrl` and
  `planCommentId`; `incidentId` is null on a row that is for an issue. `GET /api/workitems/{id}`
  carries `attempts`, and a work item says why no plan was started (`declineReason`) while a cap
  or a switch is in the way - it is asked again by itself.
- **A plan is answered with a click** ([#298](https://github.com/TrueRelevance/hephaisto/issues/298)).
  GitHub's comment editor suggests none of Hephaisto's three commands, and a mistyped one is
  passed over in silence. Now an approver's 🚀 on the plan comment approves that plan and an
  approver's 👎 rejects it - through the door `/approve` and `/reject` knock on, with its
  refusals, and heard from the same accounts by number. Hephaisto sets both on its own plan
  comment, so that GitHub shows them below it; its own are never an answer, and no other
  reaction is one. A reaction counts on the one plan comment it is on, is read once, and one
  that was refused does not decide later by itself: taking it off and setting it again is a new
  one. Somebody who is not an approver is told so once per plan, whether they wrote or clicked.
  A rejection by 👎 is recorded as given without a reason; `/replan` stays a comment. The plan
  comment also shows each command in a block of its own, which GitHub gives a copy button.
  **What it costs:** one click by an approver starts a Job that pushes a branch; unassigning
  stops it. With `github.approvers` empty nothing is set and nothing is read. One more request
  to GitHub per waiting plan and poll, a 304 while nothing changed. The agent's token needs
  nothing new: reading and setting a reaction is within Issues read and write.
- **A pull request can be opened ready for review** ([#297](https://github.com/TrueRelevance/hephaisto/issues/297)).
  Every one was a draft, whatever the runner had seen, so the word said nothing about the pull
  request it stood on. `defaults.pr.draft` in the context repository's `repos.yaml` now takes a
  second value. `true` is what it was and what an install gets that changes nothing: always a
  draft. With `unless-ready` a pull request is a draft only when the runner had no check to run
  for the repository (level `none`) or when a deviation from the approved plan was reported, by
  the runner or by the model; otherwise it is opened ready for review. The last lines of the
  description say which, and why. A label that could not be set is no reason for a draft, and a
  level below `tests` is none either: a repository without unit tests can never do better, and
  the description's "Verification weak" section says what that leaves open. Nobody but a person
  merges, and a change whose build or tests fail is still never pushed. The decision rests on
  what the coder container reports, as the verification table always has. **Set the new value
  only when every image that reads the context repository is this release or newer**: an
  older one refuses a `repos.yaml` whose `draft` is anything but `true`, and every Job reads it.
- **The pull request's description is kept with the attempt**: `prBody` on a row of
  `GET /api/codefixes` and in `attempts` of a work item, for an incident's attempt as for an
  issue's - what the runner sent GitHub, so that "does it close the issue" can be read without
  opening GitHub. Null without a pull request.
- **The console shows what was handed over, and every code fix has a page**
  ([#248](https://github.com/TrueRelevance/hephaisto/issues/248)). *Work items* in the navigation lists
  the issues that were taken, newest first, with what became of each: its state, why it ended -
  or why no plan was started yet - and its attempt. `/codefixes/<attempt id>` is one attempt on
  a page of its own, for an incident's attempt as for an issue's: what it is for, its history
  (created, plan ready, approved or denied by whom and through what - the console, the API, a
  comment on the issue - implementing, the pull request, how it ended), the plan in full, the
  pull request's description, the cost, and approve and deny with the reason box for whoever
  holds the approver role. On the code-fixes page a row for an issue names it (`owner/repo#n`)
  and leads there; an incident's row keeps its link to the incident. The badge beside *code
  fixes* counts a waiting plan of either kind. An issue's title, a plan, a pull request's
  description and a rejection's reason are shown as text: no markdown, no HTML, no link made
  of them.
- **An MCP client can ask about work items.** Two read tools: `list_work_items` (by state,
  repository and time; `enabled` says whether issues are taken at all) and `get_work_item` (by
  id, or by repository and number). `list_code_fixes` and `get_code_fix` include an attempt for
  an issue: a row names `workItemId`, `issue` and `issueUrl` where an incident's names
  `incidentId`, and `get_code_fix` also answers `issueTitle`, `pullRequestBody` and
  `decidedThrough`. Everything a stranger or a model wrote is in the `<untrusted-evidence>`
  envelope, as before. There is no tool that answers a plan: that stays with a person, in the
  console or on the issue.
- **A work item's code fix is announced.** The three moments an incident's code fix announces -
  `CodeFixPlanReady`, `CodeFixPrOpened`, `CodeFixFailed` - go through the notification outbox
  for an issue's too. The message names the issue by its reference, says where the plan is
  answered (the issue or the console; the console alone when `github.approvers` is empty) and
  links the attempt's page. There is no incident behind it, so a route takes it only when it is
  **not scoped** by namespace, cluster, kind or label and asks for no severity above `Info`; a
  fallback route does not. The Teams bot sends it to each recipient's chat - the board stays a
  board of incidents - and the webhook's payload carries `workItem` and `links.codeFix` in
  place of `incident` and `links.incident`.
- `GET /api/codefixes/{attemptId}`: one attempt by its own id with what it is for (`attempt`,
  `workItem`, `mode`). An attempt carries `decidedThrough` - `Ui`, `Api`, `Oidc` or `GitHub` -
  once somebody answered its plan.
- **GitHub among the dependencies.** `github` is a row in the connections of `GET /api/status`,
  the status page and the MCP tool `get_status`: healthy while every listed repository answers,
  degraded with one line of why - a refused token, a rate limit and until when, a 5xx, no
  answer. A rate limit is waited out, not asked through.
- **The agent's own token.** `secrets.github` names a Secret in the release namespace with the
  key `GITHUB_TOKEN`: Issues read and write, Pull requests read. It is not the coder's Secret,
  which the agent still cannot read, and the chart refuses the same name for both. With
  `codeFix.egressProxy` rendered, the agent's GitHub calls go through that proxy
  (`github.useEgressProxy`), and the chart adds the two NetworkPolicy rules that takes.
- `github.approvers` lists who may answer a plan on the issue, by account number
  (`gh api users/<login> --jq .id`). A login there is refused at render and at start.
- Four metrics: `hephaisto.github.polls` by outcome; `hephaisto.workitems.taken`;
  `hephaisto.workitems.closed` by state (`Done`, `Cancelled`) and reason (`merged`,
  `pull_request_closed`, `issue_closed`, `unassigned`, `issue_gone`); and
  `hephaisto.workitems.commands` - every `/approve` and `/reject` read off an issue, by `verb`
  and by what was done with it: `accepted`, `not_approver`, or `refused:<cause>` (`mode-plan`,
  `mode-off`, `emergency-stop`, `kill-switch`, `second-repository`, `not-waiting`,
  `taken-back`).
- Audit rows for a work item: `workitem.taken`, `workitem.cancelled`, `workitem.done`, and
  `workitem.command` - one per comment that decided a plan or was answered, with the account's
  number and the comment's id.
- **All of it was asked of github.com before release** ([#249](https://github.com/TrueRelevance/hephaisto/issues/249)).
  `scripts/e2e/github-live.sh` runs the road above against GitHub itself - a real bot account,
  its two fine-grained tokens, the real `gh`, the agent's client through the egress proxy - in
  a sandbox repository, with the model scripted. It is the first test of this project that
  leaves the cluster for GitHub, and what it found is in this release rather than in yours:
  - **What a model repeats cannot close or mention another issue.** GitHub reads `/issues/12`,
    `/pull/12` and `/discussions/12` as references *by themselves* - no scheme, no host - and
    takes a closing keyword before an issue's address as it does before `#12`. A plan that
    repeated "resolves `<another issue's address>`" from its issue was a pull request GitHub
    listed as closing that other issue too, and a comment that wrote "mentioned this issue"
    into its timeline under the bot's name. Text a model or a stranger wrote now has a
    zero-width space after every slash before a digit, in comments and in a pull request's
    description alike, and in a description no address is a link.
  - **The token permissions named above are enough**, measured: Issues read and write and
    Pull requests read for the agent (it cannot push); Contents and Pull requests read and
    write for the coder, which covers the draft pull request and its assignee. A **label**
    named in dev-context's `repos.yaml` has to exist in the repository: `gh` refuses one that
    does not, and the pull request is then opened without it and the attempt says so
    (`deviations`).
  - **A title keeps a name's capitals.** The first word after `fix:` is lowered only when it
    is an ordinary capitalised word: "The loop ..." becomes "the loop ...", and "HTTP client
    ..." no longer becomes "hTTP client ...". For an incident's pull request too.
  - **What a pull request closes is worked out by GitHub afterwards** - seconds as a rule,
    minutes on a bad day. Nothing in Hephaisto reads it; a script of yours that does should
    wait for it.
  - **A plan's questions are a list on GitHub, its notes a fold, and an assignment has a
    time.** GitHub renders the numbered questions as list items and lets the `<details>` block
    through; an answer and `/replan` written on github.com reach the second plan; and the
    issue's timeline is what told an assignment made a second after an unassignment from one
    that had been there all along.
  - **One account, one install per repository.** The bot's token is an account's. A second
    install that holds it and lists the same repository takes the same issues, writes a second
    status comment and plans them with its own coder: on 2026-10-08 the sandbox was also listed
    on a production install, and each issue of a test run was planned there by a real model. The
    live tier now refuses to run when the last run's issues carry a status comment of a work
    item the agent under test does not know.

### Fixed
- **Unassigning and assigning again within one poll interval is noticed**
  ([#285](https://github.com/TrueRelevance/hephaisto/issues/285)). After a failed attempt the
  issue said "unassign Hephaisto and assign it again"; done within seconds, against a poll a
  minute apart, nothing followed, because an unassignment existed only for a poll that fell into
  it. For an issue whose newest attempt has ended without a pull request Hephaisto now reads the
  issue's timeline, and an assignment newer than that end is a new hand-over: a new attempt for
  the same work item. A waiting plan is left alone - assigning again does not answer it - and for
  it, a running Job or an open pull request the timeline is not read at all. An unchanged
  timeline is a free 304.
- **An approval that races the watcher no longer fails its attempt.** An approval is recorded
  before its implementing Job is created; a watcher pass that fell into that moment ended the
  attempt as failed ("the Implement phase has no job recorded") while the Job was being started -
  and the Job then ran on and opened its pull request for an attempt that said it had not
  worked. For an incident's code fix as for an issue's, since v0.9.0. Such an attempt is now
  left alone for two minutes after it was approved.
- **A code span in a plan is posted as it was written.** `children: [...]` in a plan was shown
  on the issue as `children: \[...\]`: brackets were escaped where an escape is not one. GitHub
  acts on nothing inside a code span, so a span is now left alone and only the text between
  spans is made inert.
- **A plan that suspects no injection no longer tells the issue that it does**
  ([#291](https://github.com/TrueRelevance/hephaisto/issues/291)). A planner that found nothing
  planted in an issue wrote "No suspected prompt injection in the issue." into its notes, and
  the plan comment then said that one note was about text in the issue that read like an
  instruction - on the strength of the word. The console marked the same note "suspected
  injection". A short note that only denies is now an ordinary note, in both places; the
  prompts ask for silence when nothing is suspected; and the sentence for a note that is
  withheld says what is known - that it mentions prompt injection and may quote the issue.
- **A plan's long text keeps its paragraphs and its lists on the issue**
  ([#292](https://github.com/TrueRelevance/hephaisto/issues/292)). The summary and "what is
  wrong, and what will change" were posted as one paragraph whatever they held: a list of the
  entries to move, one to a line, became a run-on sentence. Paragraphs, bulleted and numbered
  items are kept; a line that would begin a heading or a rule is shown as text.
- **A plan's step is shown whole on the issue, with the list inside it**
  ([#294](https://github.com/TrueRelevance/hephaisto/issues/294)). A step was cut at 1000
  characters - half of what a plan result allows - and a list written inside it became part of
  the sentence: the one plan with a long step lost "do not change the template, any route or
  any page". A step, a note and what only a person can confirm are now shown up to the plan
  result's own limits, and a step keeps its paragraphs and its list, indented under its number.
- **A Nuxt repository's verification level is `typecheck-only`, before and after**
  ([#295](https://github.com/TrueRelevance/hephaisto/issues/295)). The runner decided the level
  build first, so an app with a build and a type check was `build-only` in the plan prompt's
  "expected level" and in the pull request's table, while the same prompt and the context's
  rules call it `typecheck-only`. One definition now serves both: unit tests, else a type
  check, else a build.
- **A commit of a Job ends with Hephaisto's two trailers and nothing else.** Claude Code added
  `Co-Authored-By: Claude ...` after them. The runner now starts it with its attribution for
  commits and pull requests set to none; a test with the pinned CLI holds that the instruction
  is gone from what the model is sent.
- **A pull request that is opened without its label says so in its description.** A repository
  without a `hephaisto` label got the pull request without one, as before, but under
  "Deviations from the approved plan" the description said "none": it was written before
  anybody knew. It is now written again for the second try.
- **The pull request for an issue that names no kind is typed by its first commit.** Such an
  issue was always `chore:`, also over a commit that said `feat(nav):`. The commit's type is
  taken when it is one of feat, fix, chore, docs, refactor, perf and test; a kind the issue
  does name still decides, and nothing else of the commit's subject is used.
- **A replanned issue's pull request no longer opens with what changed since the earlier
  plan.** The planner is asked to put that comparison into the summary's last paragraph,
  beginning "Compared with the earlier plan:". The issue shows it; the pull request's
  description, whose reviewer never saw the earlier plan, leaves that paragraph out.
- **The plan prompts no longer say the build may be run while planning.** "The driver will run
  these commands (you may run them too)" was false in the read-only plan phase, where the guard
  refuses them.
- **A coder Job is shown a repository's whole `CLAUDE.md`**
  ([#281](https://github.com/TrueRelevance/hephaisto/issues/281)). The runner pastes that file
  into the Job's prompt, and cut it at 24,000 characters with nothing but "(truncated)": the
  model was not told the file goes on, or where. `CaitMatchingService`'s is 34,000 characters,
  and the cut took its notes on testing. The limit is 120,000 now; a file that is still cut
  says how much of it this is and that the rest is `CLAUDE.md` in the repository; and the model
  is told to read a `CLAUDE.md` in a directory it works in, which nothing loads for it. The
  repository still configures nothing: its text is information. Needs the coder image of this
  version.
- **An incident's pull request closes and mentions nothing a model repeats.** The description
  and the title of a pull request for an *incident's* code fix carried the model's summary, root
  cause, notes and deviations as written. GitHub reads a description for closing keywords,
  references and mentions whatever the pull request is for, so a model that repeated "fixes" and
  an issue's number from a log line would have closed that issue on merge, and one that repeated
  an `@` and a name would have notified that person. Those fields, and the incident's title, now
  get the treatment an issue's pull request has: a zero-width space after `@`, `#` and `GH-`,
  inside `://`, after `www` and after every `/` before a digit. A link a model wrote there is
  text; the evidence, which is in a fence, is as it was. Found by the run against github.com;
  not seen in production. It needs the coder image of this version.
- **A credential named the way an environment names it is redacted.** `GITHUB_TOKEN=...`,
  `GitHub__Token=...` and `CLAUDE_CODE_OAUTH_TOKEN=...` in a log line went to the coder's request
  and to MCP readers as they were unless the value itself had a known prefix: `_` is a word
  character, so the pattern for `token=` did not match inside a longer name. GitHub's `ghu_` and
  `ghr_` tokens are now known by sight as well.
- **A workload that is deleted and created again is found again.** The watcher remembers which
  object owns which, and it remembered "there is no such object" for as long as a hit: an hour.
  A Deployment deleted and created again under its name within that hour - a re-install, a
  `kubectl delete -f` and `apply -f` - keeps its ReplicaSet's name too, so a lookup made in
  between left the new pods' incident filed under `namespace/ReplicaSet/name-hash` instead of
  the Deployment: its own workload key, with its own cooldown, mapping and code-fix repository,
  none of which matched. A request the API server failed had the same effect. "Not there" and
  "could not be read" are now held for 30 seconds; an object that was found is still held for
  the hour.
- **The model is handed the trace tools, and the alert's rule, of a current Grafana MCP
  server** ([#196](https://github.com/TrueRelevance/hephaisto/issues/196)). The agent offers
  the model eighteen of the server's tools by name, and five of the names were not ones a
  current server has: four for Tempo, and `grafana_api_request`, the only way the prompt knew
  to an alert's rule. With mcp-grafana 2.0.1 the `grafana-mcp` row of `GET /api/status` read
  `Degraded - 13 of 18 tools; missing query_tempo_traces, query_tempo_traceql,
  list_tempo_tag_names, list_tempo_tag_values, grafana_api_request`, and every investigation ran
  without traces - concluding all the same, which from outside looks like an investigation that
  needed none. The list now names what that server offers: `search_tempo_traces`,
  `get_tempo_trace`, `list_tempo_attribute_names`, `list_tempo_attribute_values` and
  `alerting_rules_read`. The row reads `Healthy - All 18 allowlisted tools present.`, and an
  investigation of a failing traced service searched for its traces and read one.
  - **It needs a Grafana MCP server that has Tempo tools of its own.** Tested with chart
    `grafana-mcp` 0.27.1, which is mcp-grafana 2.0.1. **An older server now reports the new
    names missing** - `Degraded - 13 of 18 tools; missing search_tempo_traces, ...` - where it
    reported the old ones missing before: upgrade the server. There is no setting that makes an
    old server read `Healthy`. The list is `Grafana:AllowedTools`, the chart has no value for it,
    and configuration can only add a name to it (`Grafana__AllowedTools__0` through `extraEnv`
    appends), never take one away.
  - **`grafana_api_request` is no longer used**, so the server can be started with
    `--disable-api`, as the tested one was. An install that relied on the agent reading its
    rules through that tool gets them through `alerting_rules_read` instead.
  - **Set `grafanaMcp.datasourceUids`** - `tempo` and `prometheus` at least. Every trace tool
    requires the Tempo datasource's uid, and `alerting_rules_read` returns PrometheusRule rules
    only when it is given the uid of the Prometheus that evaluates them; without one it answers
    for Grafana-managed rules, which is none of the shipped ones. The model is told so in its
    prompt, and without the value spends a call on `list_datasources` first.
  - A trace search is held to the same rule as a metrics or logs query: without `start` or
    `end` it is refused and the model is told to supply one.
  - **What this does not fix.** A tool's answer is shown to the model in at most 8 kB, and an
    answer that arrives as one line longer than that is shown as its size and nothing else.
    That is older than this release and true of every tool, but it meets traces first: a
    search that matched twenty traces (41 kB as the agent counts it) was shown as nothing on
    the test cluster, where a trace of two spans (6.5 kB) was shown whole. A trace of some
    dozens of spans is larger than that.
- **A warning about a pod that no longer exists opens no incident.** Kubernetes keeps an event
  for about an hour, longer than the pod it is about may live. An agent that restarted within
  `incidents.healedAfter` of a crash-looping pod being deleted - by a rollout that fixed it, by
  a `helm uninstall` - was handed that pod's `BackOff` warnings again, could no longer ask whose
  pod it had been, and opened "CrashLoopBackOff on `<pod name>`" with the pod as its own
  workload: investigated, escalated and announced, never seen to heal, and matching no cooldown
  or mapping. Such a warning is now dropped, for a pod and for every other kind the agent can
  look up. Only when the API server says the object is gone: one that could not be read is
  handled as before.

### Changed
- **Hephaisto says "pull request" where it said "draft pull request"** - the comment on the
  issue (`**A pull request is open:**`), the Teams cards and their button (`Pull request
  opened`, `Open the pull request`), the Grafana annotation, the audit entry and the console.
  It cannot know which of the two it is, since [#297](https://github.com/TrueRelevance/hephaisto/issues/297);
  the pull request's own description says. Anything that matched on the old words has to
  match on the new ones. The description's last line is now `Opened by hephaisto-coder; a
  human reviews, merges and deploys.`, below the sentence that says in which state it was
  opened; a context repository with its own `prompts/pr-body.md` gets that sentence where it
  writes `{{opened_as}}`, and nowhere if it does not.
- **A work item can have several attempts, one of them open.** It was one attempt, ever: a
  failed, rejected or expired plan ended the issue until it was handed over again. The next
  attempt is never started by itself - an approver's `/replan` or a fresh assignment asks for
  it - and every cap still applies; one that cannot start yet waits, with its reason on the
  status comment. `GET /api/workitems/{id}` lists the attempts, the console's work-item row
  names the earlier ones, and an attempt's page says which of how many it is.
- **The ceiling on what Hephaisto writes on an issue is per attempt.** It was six comments for a
  work item. It is five for one attempt - its plan and four one-time answers - and at most five
  attempts for one hand-over: with the status comment, at most 26.
- **A plan's notes are posted, folded - but for one kind.** They were not posted at all, because
  a note is where a planner quotes text it took for an injected instruction. Such a note is
  still not quoted on the issue: it is counted, and an operator reads it in the console.
- **Hephaisto reads an issue's comments while a Job runs and after a pull request**, not only
  while a plan waits - conditionally, so an unchanged issue costs nothing - to refuse a
  `/replan` in words. `/approve` and `/reject` at those times are passed over, as before.
- **The words about handing an issue over again changed.** An ended attempt's status names
  `/replan` first. Where a poll still has to see the gap - an issue whose pull request was
  merged or closed and that stayed assigned - it says to wait a minute or two before assigning
  again.
- **`hephaisto.workitems.commands` has the verb `replan`.**
- **The request for a work item can carry `previous` and comments**, and the plan result
  `questions` - both optional members of contract versions that did not move. A coder image from
  before this release refuses a request with `previous`, and an agent from before it refuses a
  result with `questions`: as always, the two have to be of one version.
- **`incidentId` on a code-fix attempt can be null** - in `GET /api/codefixes`, and in the
  `code_fix_attempts` and `llm_usage` tables. It is null exactly for an attempt that is for an
  issue; a client that follows it to an incident has to check.
- **The MCP tool list has 26 tools, two more than before**, and the descriptions of
  `list_code_fixes` and `get_code_fix` changed. A gateway that allows tools by name needs
  `list_work_items` and `get_work_item` added; one that indexes descriptions will re-index. In
  the answers of the two code-fix tools `incidentId` is absent for an attempt that is for an
  issue, and `workload` is absent there too - a client that required either has to check.
- **A denial records through what it was given.** `code_fix_attempts.approval_source` used to
  stay `NotApplicable` for a denied attempt; it is now set as an approval sets it. Attempts
  denied before this release keep `NotApplicable`: `decidedThrough` is null for them, and their
  page says who denied them and not through what.
- **The code-fixes page**: the column *incident* is *incident or issue*, and every row's
  timestamp is a link to the attempt's page.
- **The coder's request has a second version.** A Job for an issue is handed contract version 2
  (`codefix-request-v2.schema.json`: `work_item` in place of `incident`, `findings` and
  `investigation_summary`). A Job for an incident is handed version 1, byte for byte what it
  was. Agent and coder image have to be of one version, as before: a v0.13.0 coder refuses a
  version-2 request, and says so in its result.
- A work item's coder spend counts in the day's and the hour's LLM budget like any other, and
  in `codeFix.budgets`; it has no incident whose own ceiling could hold it.
- **The implementing Job prints one more block.** Before its result, the publish role prints
  the description of the pull request it opened (`---HEPHAISTO-PR-BODY-BEGIN ...`, three lines).
  It is not part of the result contract, whose schemas are unchanged, and nothing is decided by
  it: a coder image from before this prints none, and `prBody` is then null.
- `approvalSource` has a seventh value, `GitHub`. The others keep their names and numbers.

### Upgrading
- **The chart and the images have a new address.** The repository moved from `Flou21/hephaisto`
  to `TrueRelevance/hephaisto` on 2026-10-08, and from the release after `v0.14.0-rc1` on
  everything is published under the organisation:
  `oci://ghcr.io/truerelevance/charts/hephaisto`, `ghcr.io/truerelevance/hephaisto` and
  `ghcr.io/truerelevance/hephaisto-coder`. **An install has to change the chart reference it
  pulls from**; the chart's own defaults name the new images, so an install that does not pin
  `image.repository` or `codeFix.image.repository` needs nothing else. Everything up to and
  including `v0.14.0-rc1` stays where it was published, under `ghcr.io/flou21/`, and is not
  published again. Links to the old repository redirect.
- **Migrations run when the agent starts**: six, `WorkItems`, `WorkItemCodeFix`,
  `ApprovalOnIssue`, `ReplanOnIssue`, `AssignedAgain` and `AnswerByReaction` - the last adds one
  nullable column, `code_fix_attempts.command_reaction_id`. The first is a new table. The second
  makes `code_fix_attempts.incident_id` and `llm_usage.incident_id` nullable, adds
  `code_fix_attempts.work_item_id` with a check that exactly one of the two is set, and three
  columns to `work_items`. The third adds `command_comment_id`, `command_answers` and `pr_body`
  to `code_fix_attempts` and `still_assigned` to `work_items`. The last two add three nullable
  columns to `work_items`: `replan_after_attempt_id`, `replan_requested_by` and
  `assignment_seen_at`. Existing rows are valid as they are.
- **The coder image has to be of this version**, as for every release since v0.13.0: the agent
  starts it in three roles and hands a Job for an issue a request an older image refuses. The
  chart's default follows the chart; an install that pins `codeFix.image.tag` moves both.
- **Nothing is on by default.** Without `github.enabled` the agent holds no GitHub credential
  and asks GitHub nothing, `/api/workitems` answers an empty list, and the *work items* page
  says that the feature is off. Three things change for such an install all the same: the
  navigation has that entry; the MCP endpoint lists two more tools (see *Changed*); and an
  incident's pull request is made inert (see *Fixed*).
- To turn it on: a bot account that is a member of the organisation with write access to the
  repositories, two fine-grained tokens - the agent's (Issues read and write, Pull requests
  read) and the coder's (Contents and Pull requests read and write) - `github.enabled`,
  `secrets.github`, `github.issues.repositories`, and the repository enabled in the context
  repository's `repos.yaml`. [GitHub issues as work](https://docs.hephaisto.dev/operate/github-issues)
  has each step.
- To let anybody answer a plan on its issue, list their account numbers in `github.approvers`.
  Until then a plan for an issue is approved in the console or through the API.
- To have a work item's code fix announced, a notification route has to list the code-fix
  events **without** a namespace, cluster, kind or label scope and without a `minSeverity`
  above `Info`. A route written for incidents that is scoped that way stays silent for issues,
  on purpose.

## v0.13.0 — candidates only (`v0.13.0-rc1` to `-rc4` 2026-10-02, `-rc5` and `-rc6` 2026-10-06), released in v0.14.0

**What v0.12.0 left open.** The three items that release carried over, and one production found
the same day.

### New
- **A watcher incident ends when its fault does** ([#158](docs/backlog.md#158)). Once every pod
  of the workload has been running, ready and without a restart for `incidents.healedAfter`
  (ten minutes), the incident closes as `hephaisto/watcher`. An alert still firing on the same
  incident keeps it open, and so does an action in flight. A workload that was deleted, and a
  failed Job, do not heal: those are the sweeper's.
- **Pod logs from Loki first** ([#160](docs/backlog.md#160)). Set `grafanaMcp.podLogSelector`
  to the stream selector for one pod - `'{namespace="<namespace>", pod="<pod>"}'` with promtail's
  labels - and the model is told to read pod logs in Loki, where every container and the lines
  from before a restart are one query, and to use `get_pod_logs` only for what Loki lacks.
  Empty changes nothing.
- **`get_pod_logs` reads a pod with a sidecar.** With no container named it reads the
  application container and says which other containers the pod has.
- **Close many incidents at once** ([#161](docs/backlog.md#161)). The MCP tool `close_incidents`
  takes the filters of `search_incidents`; the incident list in the console has the same for its
  own filter. Approver only. The first call counts and closes nothing; the second names that
  count and a reason, and closes only if the count still holds. One audit entry per incident.
- **Acknowledge many at once, in the console.** "acknowledge all issues" sits beside "close all
  issues" in the incident list's filter box. It counts first, then acknowledges the open
  incidents the filter matches that nobody holds yet; one somebody already acknowledged is left
  alone.
- **Close and Reinvestigate from a Teams card** ([#124](docs/backlog.md#124)). With
  `notifications.teamsBot.actions.enabled`, an escalated alert that no investigation found
  anything for carries **Reinvestigate**, for any member of the team, as in the console.
  **Close** asks for a reason and takes an approver: `notifications.teamsBot.actions.approvers`
  lists Microsoft Entra object ids (`az ad user show --id <address> --query id -o tsv`) and is
  empty by default, which draws no Close and refuses a click that asks for it. With somebody
  named, everybody sees the button; a member who is not on the list is told so and nothing
  changes. Both go through the console's own close and re-investigate, so the audit row is the
  same one.
- **Approve and Deny from a Teams card, behind their own switch** ([#124](docs/backlog.md#124)).
  `notifications.teamsBot.actions.approvals.enabled`, off by default, and refused without
  `actions.enabled` and without at least one entry in `actions.approvers`. An alert whose
  incident is awaiting approval then says what is proposed - the action, its target, its
  arguments, its risk - above an **Approve** and a **Deny** for it. Only a mapped approver's
  click is taken; it is recorded as that person, with `Teams` as the approval's source, and runs
  through the same admission as an approval in the console. A second click on a card that is
  still on somebody's screen is answered "already decided" and changes nothing. With the switch
  off, both stay links and a forged click is refused.
- **The sweeper has chart values**: `incidents.sweep.enabled`, `expireAfter`, `approvalTimeout`.
  Still off by default.

### Changed
- **A coder Job is three containers, and the one the model runs in holds no GitHub or NuGet
  token** ([#116](docs/backlog.md#116)). **Needs the coder image of this version**: the agent
  now starts that image three times, and an older image knows nothing of the roles - it runs
  its whole flow in the container that has no GitHub token, and with private repositories
  every attempt fails at its first clone. `codeFix.image.tag` defaults to the chart's version,
  so the two move together unless the tag is pinned.
  `prepare`, an init container, holds `GITHUB_TOKEN` and `NUGET_GITHUB_TOKEN` and does the
  clones, the open-PR and branch checks and the package restore before the model exists.
  `coder` holds the model credential alone and runs the agent, the build and the tests.
  `publish`, for an implementation only, holds `GITHUB_TOKEN` and starts after `coder` has
  ended: it pushes and opens the Draft PR from a copy of its own, having checked the diff
  against the publishing policy again. The Secret, its keys and every chart value are as they
  were. Three things an operator may notice:
  - `kubectl logs job/codefix-…-impl` wants `-c publish` for the result and `-c coder` for the
    agent's log and an unpushed patch; a plan's and an investigation's result is still in
    `coder`.
  - The build and tests run **without package-feed credentials**, from what the restore in
    `prepare` cached. A fix that adds or bumps a package from a private feed therefore ends
    `build_failed`, and its deviations say that in words and that a person has to restore it.
  - With `codeFix.nugetCache.enabled`, the cache is mounted read-only into `coder`, so only the
    restore of the untouched default branch writes to it. That closes the channel between
    attempts the chart warned about; it also means a new package cannot be fetched there at
    all, not even from nuget.org.
- **An investigator Job is two containers.** `prepare` clones dev-context and, with source
  access, the workload's repository; `coder` gets the model credential and no GitHub token.
  The endpoint, the per-run token and the fallback are unchanged.
- **`IncidentSweep__*` and `Kubernetes__HealedAfter` are refused in `extraEnv`.** An install that
  set the sweeper there moves it to `incidents.sweep`.
- **A container that was OOMKilled once and has run cleanly since is no longer reported.** It
  was, at every relist, for as long as the pod lived.
- **Kubernetes events older than `incidents.healedAfter` are ignored**, so an agent restart no
  longer re-reports a warning from before a pod healed.

### Fixed
- **Deciding an action whose incident has moved on is a conflict, not a server error.** Approving
  or denying an action that still said it was waiting, on an incident somebody had closed in the
  meantime, left `POST /api/incidents/{id}/actions/{actionId}/approve` and `/deny` as a 500. Both
  answer 409 with the reason now, and nothing is changed - as for an action already decided.
- **A code fix for a service that pins Cait gets as far as planning**
  ([#117](docs/backlog.md#117)). Every plan production started ended after ten minutes in
  `git log -G<Version>… -- Cait.csproj exited 137`: the coder looked for the pinned commit in a
  clone without file contents, and git fetched each of the project file's 974 versions with a
  request of its own. They are fetched in one request now, and a lookup that still fails is a
  note on the plan and no longer the end of it. Needs the coder image of this version.
- **An investigator Job is told the workload, not the pod, when an alert opened the incident.**
  An incident Alertmanager opens names a pod and nothing above it, and the request to the Job
  carried that pod as the workload while the source it was handed belonged to the pod's
  Deployment. Which of the alert and the watcher opens an incident first is a race, so the same
  fault read differently from one run to the next. The request asks the cluster now, as the
  source lookup beside it always did.
- **A protected path with an unusual name is still protected** ([#116](docs/backlog.md#116)).
  The check that keeps `.github/**` and the other protected paths out of a pushed fix compared
  names as git prints them, and git prints a name with a backslash or a non-ASCII byte in
  quotes: such a file under a protected directory passed. And a `.gitattributes` in the fix
  could mark a file as binary and hide a credential-shaped line from the same check. Both
  found while moving the check; neither was seen used.
- **One metric name is one instrument** ([#15](docs/backlog.md#15)). Six names were registered
  twice. `hephaisto_signals_received_total{source="Kubernetes"}`,
  `hephaisto_investigation_terminations_total` and `hephaisto_grounding_rejected_total` each
  counted an event twice and now read half of what they did: a panel or alert with a
  threshold on one of them needs a look. `hephaisto_investigation_steps_total` and
  `hephaisto_investigation_duration_milliseconds_*` are gone; the histogram
  `hephaisto_investigation_steps` and `hephaisto_investigation_duration_seconds` are what the
  shipped dashboard reads. Both carry `kind` and `termination_reason` now, and the termination
  counter `kind`, so the dashboard's kind filter works on them.
  `hephaisto_signals_dropped_total` has `source` on every series and `kind` on none.
- **A grounding rejection says why in the log**, at Warning. Its detail was in no log line.
- **The incident list says who acknowledged a row** ([#165](docs/backlog.md#165)).
- **A finding that quotes a grafana-mcp result survives grounding.** An MCP text block was stored
  serialised, so every quote in its JSON read `"` and an excerpt with real quotes never
  matched: investigations with the right numbers ended `GroundingRejected`.

## v0.12.0 — 2026-10-02

**What production found.** See `docs/roadmap.md`, v0.12.0. The first full release since v0.8.0:
v0.9.0, v0.10.0 and v0.11.0 below went out as release candidates only, so an upgrade from v0.8.0
takes all four sections, migrations included. `src/` and `charts/` are those of `v0.12.0-rc9`.

It carries F5, the investigation's model loop in a Job, and F2, settled by removing the readiness
detectors rather than tuning them. **F1, F3 and F4 are not in it** and stay open: a watcher
incident still never closes by itself ([#158](docs/backlog.md#158)), `get_pod_logs` still cannot
read a pod with a sidecar ([#160](docs/backlog.md#160)), and closing is still one incident at a
time ([#161](docs/backlog.md#161)).

### New
- **Investigation in a Job, off by default** ([#164](docs/backlog.md#164)). With
  `investigation.job.enabled` and the executor at `job`, an investigation's model loop runs as
  Claude Code in a Job next to the coder - on the subscription token in `secrets.codeFix`, with the
  model in `investigation.job.model` - while Hephaisto serves it the same read-only tools over an
  internal port and grounds its conclusion exactly as before. See `docs/architecture.md`,
  "Investigating in a Job".
- **Its own axis.** `investigation.job.executor` (env) and the switch ConfigMap's
  `investigationExecutor`, `inprocess | job`, most restrictive winning; silence, a typo, the
  emergency stop and the runaway latch are all in-process. `GET /api/investigations/executor` says
  which, and why.
- **The investigator port** (`investigation.job.port`, 8084): `/investigate` and nothing else,
  admitted only from investigator pods, each run with its own token. Not the public `/mcp`.
- **Fallback, never a lost investigation.** A Job that is refused, fails, vanishes, outlives its
  deadline or hits a subscription limit is replaced by the in-process investigation; the console,
  the API and MCP say `JobFallback`. `fallbackToInProcess: false` escalates instead.
- **Storm control.** `concurrentJobs` (1) and `jobsPerHour` (20): a run without a slot runs
  in-process at once; nothing queues.
- **Source access, read-only** (`investigation.job.source.enabled`, off). A workload mapped
  through `codeFix.repositories` is cloned at its running commit; the investigator may name file
  and line, shown on the finding and passed to a code fix's plan. Never evidence.
- **The investigation on the Teams cards.** An alert card shows the newest investigation's
  diagnosis - the primary hypothesis, category and confidence, who investigated (Claude Code in a
  Job or in-process, and the model), the plan's summary, the first two cited excerpts and any code
  references - and says so when an investigation grounded nothing. Each board row gets a one-line
  "Diagnosis: ... (0.85)" and the full section under Details. Model-written text is redacted and
  rendered as TextRuns, never as markdown, so a quoted log line cannot become a link on a card.
- **A Job investigation plans in the Job (rc4).** `conclude` grounds the findings on the spot and
  answers with the planning prompt over them; the Job then calls the new `propose_plan` tool, which
  Hephaisto grounds against those findings and hands to the policy engine exactly like the
  in-process planner's reply. A Job investigation never calls `Llm:Provider`: no plan proposed is
  no plan, a diagnosis for a human. A concluded Job gets five minutes to plan before it is removed.
- **Overflow can wait (rc4).** `investigation.job.overflow: wait` holds an investigation that finds
  every `concurrentJobs` slot taken, or `jobsPerHour` reached, until a slot frees - re-deciding
  every poll, so switching the executor back ends the wait - and sends another cluster's incident
  to a Job too. With `fallbackToInProcess: false` as well, investigations never use the in-process
  model. Default `inprocess`, as in rc1-rc3.
- **Telemetry.** `hephaisto.investigation.terminations` gains an `executor` label;
  `hephaisto.investigation.job.fallbacks{reason,fallback}` is new; two dashboard panels.
- **Who is signed in, on every page.** The navigation ends in the signed-in username, linking to a
  new account page (`/account`): username, name, email, every role the sign-in carried with what
  each one allows here, and whether this account may decide. The answer to "why can I not close
  this" no longer needs a token decoded in the IdP.

### Changed
- **The watcher no longer opens `ReadinessFlapping` incidents** ([#159](docs/backlog.md#159)).
  Both detectors are gone: four `Unhealthy` readiness events (a lifetime count, so any slow
  rollout crossed it - a Mimir upgrade opened one incident per component) and four Ready
  transitions in ten minutes. They were 204 of 264 production incidents in two weeks. A pod that
  stays not-ready is the `KubePodNotReady` rule's, as `PodNotReady`; the `TargetFlapping` rule
  still raises `ReadinessFlapping` through Alertmanager. `Kubernetes:ReadinessFlapThreshold` and
  `Kubernetes:ReadinessFlapWindow` are removed; a value still set for them is ignored.
- **The theme choice moved to the account page**, as three buttons (system, light, dark) in
  place of the button in the navigation that cycled through them. Still remembered per browser.
- **`Kubernetes:IgnoredKinds`** - signal kinds the watcher never reports, empty by default. For a
  cluster whose alert rules cover a kind better: production ignores `Unschedulable`, because the
  watcher fired on a cait-scraper rollout's first scheduler refusal (01a0f29f, 2026-09-30) while
  the `KubernetesPodUnschedulable` rule waits `for: 30m`.

### Fixed
- **With `auth.enabled`, the console's pages now require sign-in.** They were mapped without the
  read policy, and the API answers a missing cookie with 401 rather than a redirect, so nothing
  ever sent a browser to the IdP: the console was readable without OIDC, every viewer was
  anonymous, and close, approve and deny were disabled for approvers too. Production found it.
  `v0.12.0-rc7` carried the first half only: the pages took the API's policy, whose bearer scheme
  answers the challenge, so opening the console was a 401 rather than the IdP's login page. They
  have a policy of their own now, and a signed-in user without the reader role gets a 403.

### Upgrading
- **With `auth.enabled`, the ID token must carry the roles** before this lands, or every reader
  gets a 403. In Keycloak: client scope `roles`, mapper `realm roles`, "Add to ID token" on - it
  is off by default. The client also needs `<console>/signin-oidc` as a valid redirect URI.
- **Nothing changes until `investigation.job.enabled` is set**, and enabling it switches no
  investigation over by itself: the executor defaults to `inprocess`. An install that does not set
  it renders byte-for-byte what v0.11 rendered.
- **Needs `codeFix.enabled`** - the investigator borrows the coder's namespace, image,
  ServiceAccount, Secret and egress proxy - but no code-fix mode.
- **Two migrations run at startup**, both additive: `InvestigationExecutor` (nullable
  `investigations.executor`; existing rows read as in-process) and `FindingCodeRefs`
  (`findings.code_refs` jsonb, default empty).
- **Before production:** resolve backlog #118 (the OAuth token's terms for headless use). #160 is
  not in this release (#159 is), so an investigation of a mesh-injected pod reads its logs only if
  the model names the container.
- A subscription run is charged $0 to the global LLM budget (its notional cost is in the step);
  the Job caps bound it. An API-key run is charged what it cost.

## v0.11.0 — candidates only (`v0.11.0-rc1` 2026-09-29), released in v0.12.0

**An agent can ask it.** An MCP endpoint over the incidents, their investigations and their
history ([#157](docs/backlog.md#157)), for coding agents and for an MCP gateway in front of them.
See `docs/roadmap.md`, v0.11.0.

### New
- **`/mcp`, on a port of its own (8083), off by default** (`mcp.enabled`). Stateless streamable
  HTTP. Nothing else answers on that port, and `/mcp` answers on no other.
- **23 tools.** Find incidents (`search_incidents`, `count_incidents`, `lookup_incident_filters`),
  read one (`get_incident`, its signals, timeline, notifications, findings with their evidence,
  investigation, raw blobs, actions), its history (`get_incident_history`), the alert's note,
  code fixes, the agent's status, and who the caller is. Six changes: acknowledge, assign, a note
  entry, feedback - and with the approver role, close and re-investigate. **No tool approves,
  denies, re-arms or sets a mode.**
- **Tokens.** `mcp.tokens` names each consumer; values come from the Secret `secrets.mcp`, one key
  per name, at least 32 characters. A `shared` token (a gateway's) acts as `mcp/<name>`; a
  `person` token as its subject. With `auth.enabled`, the identity provider's tokens are
  accepted too; a signed-in user is an approver only if `auth.approverRole` is set and held.
- **The audit trail says where a change came from**: `origin` in the audit detail. A shared token
  must name the person it acknowledges for; the name is stored as a claim, shown in the console
  and on Teams cards as "an agent (litellm), for flo - unverified".
- **Untrusted text arrives as data**: redacted, escaped and inside `<untrusted-evidence>`; every
  answer stays under 32,000 characters and says what it cut.

### Upgrading
- **Nothing changes until `mcp.enabled` is set.** An install that does not set it has no port, no
  Service port and no NetworkPolicy rule for it, and `/mcp` is a 404.
- **Two migrations run at startup**: `McpReadSurface` (an index on `alert_name, opened_at`, and
  `alert_name` filled in for incidents that predate it) and `McpWrites` (three nullable
  `*_claimed_by` columns on `incidents`, `relayed_by_agent` on `alert_note_entries`).
- **Admit the gateway to the port**: `mcp.networkPolicy.fromNamespaces` with its namespace. The
  chart refuses the coder's namespace there.
- **Registering it in an MCP gateway** is an HTTP server with the token as a bearer:
  `url: http://<release>.<namespace>:8083/mcp`, `transport: http`, `auth_type: bearer_token`.
- `extraEnv` may not set `Mcp__*`.

### Fixed
- **Re-investigating as the model** is refused with a reason (403), where it used to surface as an
  unhandled exception. **Feedback** refuses the model as its author.

## v0.10.0 — candidates only (`v0.10.0-rc1` 2026-09-28), released in v0.12.0

### Fixed in rc2
- **A model's `null` in its conclusion no longer fails the investigation** ([#156](docs/backlog.md#156)).
  Found by the rc1 release gate: a finding with `"evidence": null` threw inside the mapper and
  escalated a concluded investigation as `InvestigationFailed`.

**It is the only thing that tells a person.** On its first production install Hephaisto replaces
the incident service and the pager that alerts go to today; this release is what that needs. See
`docs/roadmap.md`, v0.10.0.

### Upgrading
- **Add `IncidentOpened` to the routes that should hear first.** It is a new event, and a route
  only carries the events it names: without it an existing route stays silent until the
  investigation ends, as before.
- **Add a route for `hephaisto_route="external"` to a receiver that is not Hephaisto**, ahead of
  the route to Hephaisto. Without it the agent-presence alerts go to the agent, which is exactly
  the one place they cannot help.
- **Incidents opened before the upgrade do not absorb the alerts after it.** The fingerprint and the
  correlation key now carry the cluster, so the first firing after the upgrade opens a new
  incident. Close the old ones.
- **`cluster.name` is required, and rendering fails without it.** Set it to the value of the
  `cluster` label on this cluster's metrics and logs. It replaces `Ingest:ClusterName`,
  `Kubernetes:ClusterName` and `Investigation:Environment:ClusterName`, which the chart never set
  and which defaulted to the development machine's name - so on any other install every
  fingerprint carried somebody else's cluster, and the model was told to filter on a label that
  matched nothing ([#139](docs/backlog.md#139)). `extraEnv` may no longer set the three old keys.
  Changing the name re-keys every future signal: new alerts will not dedupe against incidents
  opened before the upgrade.
- **An unpriced model no longer starts.** With any cost cap set - they all are, by default - the
  agent refuses to start when `Llm:Model` or `Llm:PlanningModel` has no price, because an unpriced
  model bills as $0 and no cap binds ([#140](docs/backlog.md#140)). Add it to `llm.pricing`; a
  price of 0 is accepted for a model that really is free.

### Added
- **The pager suite** (`scripts/e2e/pager.sh`, [#146](docs/backlog.md#146)): alerts through a
  real Alertmanager or straight to the webhook, into the installed chart, and assertions about
  what a person would have seen - which incidents, in which state, which Teams messages were
  sent and edited, what the model was asked. A stand-in answers as the model, so a run is
  deterministic and free. It gates every change in CI (`e2e-pager`), runs on the dev cluster
  (`scripts/e2e/pager-local.sh`) and is a phase of the release harness.
- `GET /api/incidents` takes `state=any` and `alertname=`.
- **`secrets.webhookToken`**: a Secret whose key `token` the Alertmanager webhook requires as a
  bearer token ([#138](docs/backlog.md#138)). Alertmanager sends it with
  `http_config.authorization.credentials_file`. Optional - without it nothing changes, and the
  NetworkPolicy remains the webhook's whole protection - and a token under 16 characters refuses
  to start. Set it and the receiver in the same change, or every alert is refused.
- **`llm.pricing`**: prices as a list of `{model, inputPerMillionUsd, outputPerMillionUsd}`, so a
  model behind a gateway, under a name no environment variable can carry, can be priced from the
  chart. A response under a model id the gateway chose is priced as the configured model.
- **`investigation.inScopeNamespaces`**: the namespaces the model is told are in scope. Empty, the
  default, says nothing about scope instead of naming the development machine's chaos namespace.

- **`SignalKind.Pipeline`** and its runbook, for an alert about a feed, a queue or an export
  rather than a Kubernetes object.
- **Escalation steps** ([#142](docs/backlog.md#142)): `steps` on a route - after how long
  unacknowledged, whom to tell. Each fires once per outage; an acknowledgement stops them and a
  reopen starts them again. Events `IncidentUnanswered` and `SeverityRaised` (a warning that turned
  critical reaches the routes that want criticals, [#148](docs/backlog.md#148)).
- `notifications.maxPerChannelPerHour`; a message the cap holds back that a person needed is
  logged at Error ([#150](docs/backlog.md#150)).
- A database migration, `NotificationSteps`: `notification_deliveries.step`.
- **Teams buttons that act, first cut** ([#124](docs/backlog.md#124)):
  `notifications.teamsBot.actions.enabled` (off by default) puts **Acknowledge** and **Assign to
  me** on an open alert. Microsoft delivers the click to `POST /api/teams/messages`, the one
  inbound route it calls, served on `notifications.teamsBot.actions.port` (8082) and nowhere else,
  with nothing else answering on that port. A click needs a Bot Framework token for this bot's
  app id, signed by a key endorsed for Teams, whose `serviceurl` matches the activity, from this
  tenant, by a member of the team; it is recorded as the team's member list names that person.
  Close, approve and deny are still links. Set the Azure Bot's messaging endpoint to
  `https://<your host>/api/teams/messages`, routed to that port only.
- **A note per alert name** ([#145](docs/backlog.md#145)). What the people paged for an alert have
  learned about it - what it means, what to look at first, which dashboard - kept under its
  `alertname` rather than in one incident. A curated body that approvers rewrite, and under it one
  line per "what was done this time" that anybody who can read the console may add and nobody can
  edit afterwards. Shown on the incident page and on `/alerts/{name}`, its start and a link on the
  Teams alert, and given to the model after the runbook, framed as operator-written reference and
  never as instruction. API: `GET /api/alerts/{name}/note`, `POST /api/alerts/{name}/note/entries`,
  `PUT /api/alerts/{name}/note` (approver policy).
- A database migration, `AlertNotes`. It adds two tables and changes none.

### Changed
- **The right person** ([#141](docs/backlog.md#141), [#123](docs/backlog.md#123)). A route can
  match the alert's labels, clusters and kinds, name its own recipients, and one route can be the
  `fallback` for whatever no scoped route owns. Each delivery records who it was for and which
  routes sent it. Existing routes behave as before.
- A database migration, `IncidentLabelsAndRecipients`: `incidents.labels`, `incidents.alert_name`,
  and `recipients`, `routes`, `uses_channel_recipients` on `notification_deliveries`.
- **A person hears first, and the model second** ([#133](docs/backlog.md#133)). A new event,
  `IncidentOpened`, goes out when triage ends - before the investigation, or instead of one. The
  escalation that follows updates what was sent: the Teams bot edits the card of somebody already
  told and rings only people who were not. The notification cooldown is per event.
- **A rule can opt out of investigation** with `hephaisto_investigate: "false"`
  ([#134](docs/backlog.md#134)): the incident opens, a person is told, the model is not asked.
- **The model is shown the alert** ([#135](docs/backlog.md#135)): its labels less the scrape's,
  its annotations and the rule's expression, quoted as data, at most twenty signals.
- A Teams card for an alert that cleared says so - "Cleared - the alert stopped firing" - and never
  "Resolved", which is the verifier's word for a fix. An alert that keeps coming back is escalated
  as `Flapping`, with its own headline.
- A database migration, `SignalAnnotations`: `signals.annotations`, and the notification cooldown's
  index gains the event.
- **The webhook answers after it has written, and 503 when it could not** ([#136](docs/backlog.md#136)).
  Alertmanager's retry is the queue: a delivery during a database outage or a pod restart is
  refused and re-sent, instead of acknowledged and lost. `/readyz` checks the database.
- **The agent's own absence reaches a person** ([#137](docs/backlog.md#137)): `HephaistoAbsent`,
  `HephaistoAlertPathSilent` and `HephaistoIngestFailing` (`alerts.agentPresence`, on by default),
  labelled `hephaisto_route: external`. **Route that label to a receiver that is not Hephaisto** -
  see Upgrading. `HephaistoNotProcessingSignals` is removed: with the pod gone its series is gone,
  and it could never fire.
- **An alert is one incident for as long as it fires** ([#129](docs/backlog.md#129),
  [#130](docs/backlog.md#130)). A repeat is absorbed however long after the first firing it
  arrives, into the alert instance's own row. A resolve opens nothing; once nothing on the
  incident still fires it closes the incident, recorded as closed by `hephaisto/alertmanager` -
  not resolved, because the alert going away is not the agent fixing anything. An incident with an
  action in flight is left to the verifier. The same alert firing again within
  `Ingest:ReopenWindow` (24 hours) reopens its incident, which is then decided like a new one, and
  the reopen clears the acknowledgement ([#149](docs/backlog.md#149)). A person who closed an
  incident while its alert was still firing is not paged again by its repeats.
- **A flapping alert is escalated, never suppressed**, and flap detection counts per cluster
  ([#147](docs/backlog.md#147)).
- An investigation that ends after its incident closed is kept and changes nothing
  ([#152](docs/backlog.md#152)). The console's close now counts toward
  `hephaisto.incidents.open` ([#154](docs/backlog.md#154)); a reopen is
  `hephaisto.incidents.reopened`.
- A database migration, `AlertLifecycle`: `incidents.reopened_at`.
- **An alert's identity comes from its labels.** The `cluster` label is part of it, so the same
  workload in two clusters is two incidents ([#131](docs/backlog.md#131)); an alert that names no
  object is one incident per series of its rule, not one per rule, titled by what tells its series
  apart ([#132](docs/backlog.md#132)); a kube-state-metrics alert about a deployment is about the
  deployment, not the exporter's pod ([#126](docs/backlog.md#126)). Each alert instance has an
  `AlertKey`, and a signal row has a status, for the lifecycle that follows.
- **An incident about another cluster is honest about it** ([#131](docs/backlog.md#131)). It is
  offered no Kubernetes tool - those read this cluster, where a same-named workload is a
  different one - gets no rollout lookup, and the model is told which cluster it is about and to
  filter every query on it. The policy engine refuses any action on another cluster's target
  (`ForeignCluster`, checked first). The outbound webhook's payload carries `incident.cluster`.
- **Only the rule named `Watchdog` is the watchdog** ([#151](docs/backlog.md#151)). Before, any
  alert whose name contained the word was swallowed as a heartbeat.
- The kind classifier matches keywords as words ([#134](docs/backlog.md#134)).
- A database migration, `AlertIdentity`: `signals.status`, `signals.alert_key`, and
  `target_cluster` on `incidents`, `signals` and `agent_actions`. Adds only; existing rows read
  as firing, in this cluster.
- The environment card names the policy engine's protected namespaces when it has no list of
  its own, rather than a second, different copy.
- Every comment and page that said Alertmanager cannot send a credential has been corrected.

### Known
- The buttons have run against the Teams stand-in and not against Teams
  ([#125](docs/backlog.md#125)).

## v0.9.0 — candidates only (`v0.9.0-rc1` 2026-09-27, `v0.9.0-rc2` to `-rc5` 2026-09-28), released in v0.12.0

**It proposes the fix, and a person opens the door.** Most real incidents on the cluster this runs
against are code bugs, and v0.8.0's planner correctly declines to touch them - "a code problem a
human must fix" - so a grounded, correct diagnosis stopped at an escalation. v0.9.0 adds a second,
separately gated stage: an escalation whose primary finding points at code, on a workload the
operator mapped to a repository, starts a **coder** (Claude Code through the Agent SDK) in a
Kubernetes Job that has no cluster identity. It analyses the repository at the commit the running
image was built from and writes a plan; a human approves it; a fresh Job implements it on an
assigned branch, the driver builds and tests it, and a **Draft PR** opens. A human reviews, merges
and deploys. Nothing about what the agent may do to the cluster changed.

Upgrading changes nothing until you opt in: `codeFix.enabled` is false, and the mode defaults to
Off. With it Off every escalation is still judged and audited, so the console can say "would have
started a code fix, but the mode is Off" before you turn anything on.

### Added
- **The code-fix stage.** `codeFix.mode` `off | plan | pr`, its own axis beside `mode` - Observe
  does not refuse it, and the kill switch (Off, emergency stop, runaway latch) overrides it. A pure,
  default-deny eligibility gate with 21 closed reason codes; `code_fix_attempts` with one open
  attempt per incident enforced by Postgres; a watcher that collects results, enforces deadlines,
  expires unanswered plans and cancels on the switch; coder spend in the same `llm_usage` ledger.
- **The coder runner**, `ghcr.io/flou21/hephaisto-coder`: TypeScript around the Agent SDK, one
  authoritative tool guard checked three times, a fake SDK for $0 plumbing runs, uid 64198 with a
  read-only root. It answers only through a sha256-framed block in its own log.
- **Approval**: `POST /api/incidents/{id}/codefix/{attempt}/approve|deny`, under a row lock, only
  in mode `pr`, and `pr` refuses to start without `auth.enabled` - a repository write needs an
  authenticated human.
- **In the console**: a **code fixes** page listing running attempts, plans waiting on a person and
  opened PRs; a count in the navigation; a code-fix section on every incident with the full plan,
  denied tool calls and suspected injection called out.
- **Chart**: the coder namespace Role (the only `create jobs` grant anywhere), an unbound
  ServiceAccount, a NetworkPolicy, and a squid egress proxy with a domain allowlist and one log line
  per request.
- **Notifications** `CodeFixPlanReady`, `CodeFixPrOpened`, `CodeFixFailed`, the PR link first on
  the card; a Grafana annotation when a PR opens.
- **Fixtures** c15 (a null dereference at startup) and c19 (c15 plus prompt-injection bait in the
  log), from `Flou21/hephaisto-fixture-dotnet`, and `scripts/e2e/codefix-local.sh`.

### Added in rc2
- **`codeFix.model`** - the Claude model the coder runs (`CODEFIX_MODEL`); empty keeps the CLI's
  default. Measured on the fixture: Haiku 4.5 fixed c15 end to end for about $0.20.

### Fixed in rc2
- **An incident opened by an Alertmanager alert could never start a code fix.** An alert names
  the bare pod and the repository mapping is keyed by workload, so it declined with
  `NoRepositoryMapping`; the stage now resolves the pod's owner itself, through its ReplicaSet when
  the named pod is already gone.
- **A new coder pod's first clone is retried** - a NetworkPolicy controller admits a new pod's IP
  a moment late, and the clone is the pod's first connection.
- **A repository without tests can get a PR** - tests are required only when the plan promised them.

### Fixed in rc3
- **OIDC login behind a TLS-terminating ingress.** The agent had no forwarded-headers handling, so
  behind nginx/Cloudflare it sent the IdP an `http://` redirect URI and dropped its own sign-in
  cookies: a login that loops. `Web:TrustForwardedHeaders` (off by default; set
  `Web__TrustForwardedHeaders=true` when the console is reached only through the proxy).

### Added in rc4
- **A Teams bot** (`notifications.teamsBot`), beside the Workflows channel and not instead of it.
  The channel holds **one message**: a board of every open incident, edited in place, from which a
  closed incident disappears. An **alert** goes to each recipient as a personal chat message from
  the bot and is edited to say how the incident ended. Both show the incident as it is now.
- **It never deletes.** Teams leaves "This message has been deleted." behind for a channel post
  and for a reply alike, so the bot's client has no delete at all.
- You need a single-tenant app registration, an Azure Bot resource (free tier) and a Teams app with
  scopes `team` and `personal`, all in the tenant your Teams runs in. The client secret is a
  Secret (`secrets.notificationTeamsBot`, key `clientSecret`) and cannot be a value.
- Every button is a link. Buttons that act need an inbound route
  ([#124](docs/backlog.md#124)); alert recipients are one list for every route
  ([#123](docs/backlog.md#123)).
- A database migration, `TeamsBotMessages`. It adds one table and changes none.

### Fixed in rc5
- **Signed in, the console no longer asks for your name** ([#127](docs/backlog.md#127)). Every
  control on the incident page and the re-arm control on the status page showed a "your name" box
  and recorded whatever was typed, even with OIDC on. They now read "signed in as ..." and record
  the token's name. Without an identity provider nothing changes: the name is typed, as before.
- **Close, approve and deny need the approver role in the console too.** They always did on the
  API.
- `POST /api/mode/re-arm` and `POST /api/incidents/{id}/feedback` take the actor from the token
  when there is one. The body's field is still required by the contract and is ignored.

### Changed
- The headline invariant is re-scoped: **no model in this process ever holds a mutating handle to
  the cluster.** The coder holds a shell by construction; its only mutation target is a branch.
- `Ingest:SelfNamespaces` is now set by the chart ([#115](docs/backlog.md#115)).
- The install-ergonomics work ([#108](docs/backlog.md#108)) moves to v0.13.0. v0.10.0 is what
  stands between Hephaisto and being the only incident system, v0.11.0 lets an agent ask it over
  MCP ([#157](docs/backlog.md#157)) and v0.12.0 adds SMS and voice, in `docs/roadmap.md`.

### Known
- The Teams bot has run against a stand-in and not against Teams
  ([#125](docs/backlog.md#125)).
- The coder's agent and its driver share a uid ([#116](docs/backlog.md#116)); the gate exercises
  only the fixture repository ([#117](docs/backlog.md#117)); the subscription token's headless
  terms are unverified ([#118](docs/backlog.md#118)).

## v0.8.0 — 2026-09-13

**An on-call engineer can actually use it.** The agent diagnosed well and said so nowhere a
person could act on. An incident could not be closed, acknowledged or assigned; there was no
authentication, so every actor in the audit trail was a string somebody typed; and every
dependency was probed once at startup and the result thrown into a log line.

Shaped by the first production deployment, on 2026-09-11. Installing it is something you do
once and it had just been done; working the incidents is daily.

### Added
- **Close, acknowledge and reopen an incident** ([#109](docs/backlog.md#109)). Three of the ten
  `IncidentState` members had no producer: `Expire()` had zero callers so `Expired` was
  unreachable, nothing swept `AwaitingApproval` so `ApprovalTimedOut` had none either, and
  `Escalated` counts as open — which on an Observe install is where every incident ends up. The
  open count could only ever rise. `IncidentSweeper` gives the first two producers, which also
  closes [#44](docs/backlog.md#44).
- **OIDC on the console and API** ([#110](docs/backlog.md#110)), with the authenticated subject
  replacing the typed actor on every approval, closure, acknowledgement and audit row. Two
  schemes against one authority — authorization-code for the browser, bearer tokens for scripts —
  and two roles, because "anyone who can log in may authorise a change to the cluster" is not a
  default worth shipping. Fails closed, deliberately.
- **A connections panel on the status page** ([#111](docs/backlog.md#111)). Four states, not two:
  *not configured* is a choice and must not render as a fault, and *degraded* is separate from
  *healthy* because a grafana-mcp connected without its Tempo tools is [#31](docs/backlog.md#31)
  and would otherwise show green. Six rows: postgres, kubernetes, grafana-mcp,
  grafana-annotations, notifications, and the IdP. The IdP row matters most when it is red -
  authentication fails closed, so an unreachable Keycloak presents as "Hephaisto is down", and
  this is the only thing that says which of the two actually broke. It fetches the discovery
  document rather than a health endpoint, because that is the resource sign-in actually needs,
  and a 200 carrying no `jwks_uri` reports *degraded* rather than green.
- **Assignment** ([#112](docs/backlog.md#112)), distinct from acknowledgement — assigning is
  second person, acknowledging is first, and the gap between them is the signal.
- **`/webhooks` on its own port**, so a NetworkPolicy can protect the unauthenticated receiver
  while the console is exposed by ordinary means.

### Fixed
- **A values file belonging to a different chart rendered a plausible install.** The schema's
  `required` list could never fail, because Helm validates values *after* merging the chart's own
  defaults. `additionalProperties: false` now makes it a template error. Locking it first required
  declaring `grafana`, `grafanaMcp.datasourceUids` and `postgres.appUser`, which the schema had
  never declared and nothing had ever compared.
- **Two `values.yaml` comments that sent the first production install wrong**: `secrets.grafanaMcp`
  is the caller bearer rather than a Grafana credential, and `grafanaMcp.url` needs its `/mcp` path.
- **A completion whose `role` is empty no longer discards the investigation**
  ([#101](docs/backlog.md#101)). The repair happens in the handler, so a provider that omits a
  field the schema calls required costs a retry instead of a whole diagnosis.
- **The release gate is one run again** ([#97](docs/backlog.md#97)), and it now *confirms acting*
  rather than skipping it. `--full --mode Auto` had defeated itself for three releases: twelve
  simultaneous fixtures put more than `clusterUnhealthyCeiling` of the cluster's pods in a bad
  state, so gate 7 correctly refused every action and the act phase reported "the agent did not
  act" — measuring the harness, not the agent. The act phase now clears the other fixtures, waits
  for the cluster-wide unhealthy fraction to fall back below the ceiling, and asks for a fresh
  plan. **Nothing was weakened to get there**: not the ceiling, not the fixture, not the
  assertion. Measured on 2026-09-12: `c13 was acted on`, `available after the restart`,
  `incident reached Resolved`, in the same run as the twelve-fixture diagnosis corpus.
- **The gate stopped being wrong about why** in three places, all found by running it:
  a headline reading `only 11 of 12 fixture incidents were investigated` directly above its own
  detail line `c8: investigated, but no finding survived` — machinery that did not run now fails,
  a model that ran and found nothing skips; the console spec comparing a capped API call against
  an uncapped page ([#49](docs/backlog.md#49)), fixed for the whole suite via a helper that
  *refuses* a truncated list; and the diagnosis wait demanding the model **succeed**, which c10
  reliably does not, so both full runs that day sat out ~70 minutes of deadline each waiting for
  something that was never coming.

### Known
- [#70](docs/backlog.md#70) is narrowed, not closed. c1 and c3 are still classified by a different
  rule than the README expects. It gates Auto rather than this release.
- [#114](docs/backlog.md#114) — approving an action has no deterministic coverage. The e2e spec
  that clicks it asks the page whether a live control exists and returns when none does, because
  the control renders only while the action is `AwaitingApproval` and in Auto the executor moves
  it on in seconds. That makes it opportunistic rather than a gate. `DecideActionAsync` wants a
  host fixture the test suite does not have.
- [#113](docs/backlog.md#113) — c14 has no cassette, so the only fixture whose correct answer is
  a rollback cannot be replayed. It also cannot be reproduced on the dev cluster, which runs no
  Tempo and therefore generates no span metrics for either c10 or c14.

## v0.7.0 — 2026-09-11

**It survives a bad deploy.** The agent can now roll a Deployment back, there is finally a
fixture whose fault has a *cause* rather than merely a presence, and the incident card says when
a rollout preceded the incident.

Two defects in this list reached every install of the chart and were found while planning the
release rather than by anyone running it.

### Added
- **`rollback_deployment` can actually be carried out.** Everything around this action was built
  two releases ago and unit-tested — the policy gate with its two tuned windows, the revision
  facts, the `get_rollout_history` tool, the RBAC grant, the runbook guidance, the model-facing
  description — and the only thing between the model and a rollback it had correctly reasoned its
  way to was `ActionCapability.IsImplemented` returning false, which rendered the action to the
  planner as *"Not available in this build."*

  Three things worth knowing if you enable it. There is **no rollback subresource** — Kubernetes
  removed that API in 1.16, so a rollback is a client-side patch of the previous ReplicaSet's pod
  template, which is why the existing `patch` grant was already sufficient and **no RBAC
  changed**. The `pod-template-hash` label is stripped, because the controller owns it. And a
  rollback **does not restore the old revision number** — rolling back from revision 3 to 2
  produces revision *4* — so verification asserts on the **ReplicaSet**, which the controller
  re-scales rather than recreating. The obvious predicate would have failed on every successful
  rollback and rolled the cluster forward onto the revision that caused the incident.

  A rollback has **no inverse, deliberately**. `ActionRollback` is only ever called because a
  verification failed, which for a rollback means the previous revision is not healthy either —
  so the one situation where rolling forward is reachable is exactly the situation where it is
  the worst available move. It escalates instead.
- **`c14-bad-deploy`, the first fixture whose setup has a timeline.** All thirteen fixtures before
  it inject a fault that is simply *there*; not one performs a rollout. So the corpus could not
  ask the question an on-call engineer asks first — **what changed?** c14 deploys healthy, dwells,
  and is then broken by a rollout. It is also the first fixture where a **restart is the wrong
  answer**: its answer key accepts `RollbackDeployment` and nothing else, because restarting its
  pods replaces them with more pods running the same bad revision.
- **Change correlation in the incident card.** An incident on a workload whose current revision is
  minutes old now says so, with the revision, the images and the gap, *before* the investigation
  starts — because #74 established that step budget is the binding constraint on accuracy, and a
  fact given for free is a step not spent. It is phrased as evidence with its own caveat rather
  than as a conclusion, and a rollout that happened *after* the incident opened is never offered,
  since that is often somebody deploying the fix.
- **The console shows whether an action worked.** `AgentAction.Verifications` has carried the
  T+60s / T+5m / T+15m outcomes since v0.2.0, persisted and read by nothing — while *"everything
  it does is verified, then reverted if it did not work"* is the safety claim on the landing page.
  The rows now render, `hephaisto-eval export` carries them into transcripts, and the design
  gallery photographs them.
- **Five runbooks**, for kinds that shipped an alert rule and fell through to the default one:
  `HighLatency`, `TargetDown`, `ReplicaMismatch`, `RestartStorm` and the new `PodNotReady`. The
  default runbook is Kubernetes-shaped — *who owns this, get_events, previous-container logs* —
  which is not merely unhelpful for a burn-rate alert computed from span metrics, it points the
  investigation at the wrong evidence.
- **`SignalKind.PodNotReady`**, so `ReadinessFlapping` means flapping again. See Changed.
- **Three policy settings became chart values**: `policy.rollbackFreshRevisionWindow`,
  `policy.rollbackPreviousHealthyMinimum` and `policy.clusterUnhealthyCeiling`. They were code
  defaults with no way to reach them, which was defensible only while `rollback_deployment` was
  unimplemented.

### Changed
- **An incident's kind is no longer decided permanently by whichever signal opened it.**
  `IncidentTriage.Attach` folded every later signal in while updating only `LastSignalAt` and
  `Severity`. It now also re-labels upward when a later signal identifies the failure more
  specifically — a `PodNotReady` incident becomes `ImagePullBackOff` when the signal that knows
  the mechanism arrives. Since `SignalKind` selects the runbook, a stale kind did not merely
  mislabel the incident, it handed the model instructions written for a different failure.

  Re-labelling stops once the incident leaves `Detected`/`Triaging`: after a runbook has been read
  into a prompt, changing the label underneath it conceals that the investigation ran against the
  wrong instructions rather than correcting it. The change is audited.
- **BREAKING for anyone matching on it — `KubePodNotReady` now declares
  `hephaisto_kind: PodNotReady`, not `ReadinessFlapping`.** Two rules shared one kind and only one
  of them meant it: flapping is *intermittent*, and that rule fires on a pod that is persistently
  **stuck**. A stuck pod was being handed a runbook whose entire argument is that the fault is
  intermittent and a restart will not help. Nothing needs doing unless you filter on that label.
- **`Llm:Budget:MaxTokensPerHour` defaults to 50,000,000, up from 2,000,000.** See Fixed.
- **`--full` runs twelve fixtures**, adding c14. The acting gate is still a separate run per #97,
  and is now two runs testing two different action types.

### Fixed
- **A shipped alert rule named a chaos fixture and fired forever on every other cluster.**
  `ServiceNoTraffic` asserted `absent(traces_spanmetrics_calls_total{service="faulty-service"})`,
  and `alerts.slo` defaults to **true** — so on any install that is not this repo's dev cluster
  running c10, the series is permanently absent, the alert fires after five minutes and never
  stops. It carries `hephaisto_kind: TargetDown`, so the agent did not merely log it: it opened an
  incident, spent its budget investigating a workload that does not exist, escalated, and
  repeated. The rule moved to its own file behind `alerts.noTraffic`, defaulting to **false**.
  **This one directly undercut v0.6.0's whole theme, and nobody could have hit it here.**
- **Every latency incident was un-actionable by construction.** The three latency rules aggregated
  `sum by (service)` while the error-rate rules twenty lines above used
  `sum by (service, k8s_namespace_name)`. An empty namespace fails the policy engine's allow-list
  gate, is part of the signal fingerprint, and is what notification routes filter on. #33 fixed
  the *reader* half of this in v0.3.0 and everyone recorded "namespace: solved"; the rules were
  never fixed to emit what the reader reads.
- **The hourly token cap was the real budget, and the cost cap was decoration.** The two caps imply
  a price — $3.00 over 2M tokens is $1.50/1M — which is *exactly* `gemini-3.7-flash`'s blended
  rate. On `gpt-oss-120b` at $0.065/1M, 2M tokens is thirteen cents, so the first full `--mode
  Auto` run refused **14 of 27** investigations outright, escalating them `BudgetExhausted`,
  having spent **$0.066 against a $3.00 cap**. It cost a milestone rather than a run: the MVP bar
  reported *"not applicable: 8 scenarios scored, the bar needs 10"* with accuracy at 7 of 8. The
  harness had worked around it, which made the harness the only configuration where the budgets
  were right — so a stranger installing the chart got the broken calibration. That workaround is
  now deleted rather than adjusted.
- **A crashed investigation was reported as a budget ceiling, with no exception.** `Faulted` is not
  a ceiling — a ceiling is a control working, a fault is a bug — and pooling them let a real
  exception hide behind a tolerance written for budget exhaustion. The exception was in the
  database, in the API response, and in the harness's own snapshot the whole time; it was simply
  never printed. The run report now names the fault, attributes it to a fixture, and prints the
  message, and *"only 10 of 11 fixture incidents were investigated"* now says which and why.
- **A single readiness-probe failure was classified as "flapping", so every ordinary rollout
  opened a spurious incident — and captured every real one that followed it.** `SignalMapper` has
  two detectors for `ReadinessFlapping`: one counts ready-transitions and refuses to claim it
  below four, and the other claimed it from **one** `Unhealthy` event. A probe fails once on any
  pod slower to start than its `initialDelaySeconds`, so the spurious incident opened *first* —
  seconds in, against minutes for anything metric-derived — and every later signal correlated into
  an incident already carrying the wrong kind and therefore the wrong runbook. The event path now
  requires the same repetition count as the detector beside it.
- **Verification predicates were workload-shaped**, which is right for a restart — the pod is gone
  by definition — and wrong for a rollback, whose previous revision's pods were Ready throughout.
  A predicate that passes on a no-op is worse than none, because it closes the incident.
- **`c13` was absent from `infra/chaos/README.md`** — no row, no listing, and the header still said
  "Twelve" — while `CLAUDE.md` calls that table the agent's regression suite. The "Expected alert
  name" column is now labelled as the specification it is: **not one** of those `Chaos*` names is
  implemented by any `PrometheusRule`, and reading it as an inventory is what produced #70's wrong
  cause and left it standing for four releases.
- **The e2e harness graded `c1` against another fixture's incident.** It matched a fixture to its
  incident with a bare `startswith`, and `c1` is a prefix of `c10` through `c14` — so c1's
  assertion collected five other fixtures' incidents and graded whichever the API returned first.
  True since c10 was added, and invisible because c11–c13 all classify `CrashLoopBackOff`, which is
  a plausible answer for an OOM-killed pod. The eval harness's answer key had guarded against this
  exact trap, in a comment naming `c1` and `c10`; the e2e harness had not, and the two were never
  compared.
- **The acting gate's availability check could never pass for a multi-container fixture.** It
  compared the joined container-ready flags against the literal string `"true"`, which assumes
  exactly one container. c13 has one, so it held; c14 has two (its app and a traffic sidecar), so
  the check yielded `"true true"` and failed however healthy the workload was — burning a
  240-second timeout to report "the action ran but the workload did not recover" on a run where
  the rollback had fully succeeded and the incident had already reached `Resolved` two lines
  later.
- **Grading read a snapshot that no longer described the run, and hid a correct diagnosis.** The
  fixture-to-incident map is written during the validate phase; a fixture whose alert re-fires past
  the correlation window opens a *new* incident afterwards. c14 opened three, the judge saw only
  the one in the map, and it had no finding — while a later one carried the correct answer at 0.73
  confidence, naming the revision and the rollout. So the release's headline fixture was reported
  ungradeable while its diagnosis was sitting in the database. The judge now re-resolves a
  fixture's incidents against the live API and grades all of them.
- **`c5` could never score an action.** It is the obvious `DeleteStuckJob` / `DeleteFailedJobPods`
  fixture and had no `AcceptableActions` at all.
- **Four documentation surfaces described a harness and two limitations that no longer exist**:
  `docs/verification.md`'s fixture set and `ACT_FIXTURE` default, a limitation on
  `docs-site/reference/agent-options.md` closed by #82, `docs-site/project/index.md` calling a
  closed backlog entry open, and `TryGrantConcludingStep`'s own comment claiming it grants one
  step when it has granted two since #78.

## v0.6.0 — 2026-09-03

**Someone else can run it.** Three public sites, a demo that runs on a laptop with one command
and no API key, and — for the first time in the project's life — an incident the agent acted on
reaching `Resolved`.

### Added
- **The agent has been observed closing an incident it acted on.** On `c13-wedged-lock`:
  `Detected → Triaging → Investigating → Acting → Verifying → Resolved`, 41 seconds after the
  restart, granted by `hephaisto/verifier`, 70 assertions. **This retires v0.5.0's "Not
  established" line below.** It is one run on one fixture; the acting path is now demonstrated
  end to end rather than reliable, and the docs say which. Reproduced on the release gate:
  `--fixtures c13 --mode Auto` passed 70 assertions in 24m37s, executing a `RestartPod` and
  closing the incident.
- **The release gate is two runs, not one.** `--full` applies its fixtures simultaneously, which
  on a single node crosses `policy.clusterUnhealthyCeiling` — so the policy engine correctly
  refuses every action as a cluster-wide event and the acting path cannot be tested in the same
  run that tests diagnosis. Diagnosis: `--full`, which scored **8/8 correct** over 85 assertions.
  Acting: `--fixtures c13 --mode Auto`. The harness now says so instead of reporting a working
  safety gate as a broken executor.
- **Three sites**, on Cloudflare Pages: [hephaisto.dev](https://hephaisto.dev),
  [docs.hephaisto.dev](https://docs.hephaisto.dev) and
  [demo.hephaisto.dev](https://demo.hephaisto.dev). The docs site *transcludes* the repository —
  the runbooks, the prompt fragments, `values.yaml`, `architecture.md` and this file are included
  from their real locations rather than copied, and `ignoreDeadLinks` is false so a moved file
  breaks the build instead of rendering a blank section.
- **`c13-wedged-lock`**, a thirteenth chaos fixture, because the acting path had no fixture that
  could measure it. `c11` and `c12` both put the wedged state on a PVC, so acting means overriding
  the correct rule that PVC contents survive a pod replacement — and a decline is then ambiguous
  between "will not act" and "did not make the inference". c13 puts the same fault on an
  `emptyDir`, which the planning prompt already names as pod-scoped. It measures willingness to
  act; c12 keeps measuring the inference. **Quote the two numbers separately.**
- **`hephaisto-eval export`**, a fifth CLI verb. Snapshots a *finished* incident out of the
  database into a transcript — the transitions it made, the action it executed or was refused,
  and the policy decision behind it. `run` can never produce those: replay constructs an
  investigation runner and no executor, no policy engine and no state machine.
- **Termination reporting in the eval harness.** A run cut off by a token or step budget used to
  render as a bare `no finding` and read as a wrong answer. The per-scenario line now names the
  termination reason, and the summary prints the histogram — with the number that matters beside
  it: how many attempts the planner actually ran in.
- **A demo that needs no cluster.** `demo/compose.yaml` brings up Postgres and the published
  image with twelve recorded investigations loaded — the step trace, the diagnosis, and every
  evidence excerpt linked back to the raw tool output it came from. No API key, no Kubernetes,
  nothing fetched at runtime. Ten are replays; two are live captures of the agent acting and of
  policy refusing it, which a replay cannot produce.
- **`Kubernetes:Enabled`.** The agent can now start without a cluster, which it could not before:
  the RBAC self-check ran forty-odd access reviews at boot and building a client outside a pod
  fell back to a kubeconfig that was not there. Disabling it skips the watchers and leaves the
  executor that refuses everything.
- **`Llm:EmbeddingProvider`.** Embeddings are configured separately from chat and can now point
  at any endpoint serving `/v1/embeddings`, so a self-hosted install keeps the semantic arm of
  search without a Google account. The default is unchanged.
- **`hephaisto-eval run --transcripts`**, and a `redact` verb. Recording what a replay computes
  and then discards is what makes the demo possible.
- **Screenshots of the shipping console**, in `design/shots/`, photographed by
  `scripts/console-shots.sh` from the published image running `demo/compose.yaml` — so they are
  the product rather than a rendering of the design system, and they are regenerated rather than
  retouched. Taking them found a bug: the console told every escalated incident that no diagnosis
  had been produced, over the top of the diagnosis.
- **A vulnerability reporting path** — [`SECURITY.md`](SECURITY.md), through GitHub private
  advisories.
- A README for the chart, so Artifact Hub stops rendering an empty page.

### Changed
- **BREAKING — the Kubernetes label prefix is `hephaisto.dev/`, not `hephaisto.io/`.** A label
  prefix is meant to be a DNS domain you control, and `hephaisto.io` never was one. Three labels
  moved, all read by the policy engine:

  | before | after |
  |---|---|
  | `hephaisto.io/destructive-actions-allowed` | `hephaisto.dev/destructive-actions-allowed` |
  | `hephaisto.io/allow-single-replica-restart` | `hephaisto.dev/allow-single-replica-restart` |
  | `hephaisto.io/protected` | `hephaisto.dev/protected` |

  There is **no compatibility shim and no dual-prefix read**, deliberately: a policy engine that
  accepts two spellings of "this namespace opted in" is a worse thing to reason about than one
  that accepts one.

  **Upgrading.** This fails *closed*. An upgraded agent looks for a label that is not there, the
  policy engine denies, and the reason is recorded on the action row — nothing is silently
  permitted, and the failure mode of skipping this note is an agent that stops acting, not one
  that acts where it should not. Relabel every namespace you had opted in:

  ```sh
  kubectl label ns <ns> hephaisto.dev/destructive-actions-allowed=true
  kubectl label ns <ns> hephaisto.io/destructive-actions-allowed-
  ```

  Same for any workload carrying `hephaisto.io/protected` or
  `hephaisto.io/allow-single-replica-restart`. These are **code defaults, not chart values**, so
  if you overrode `Policy:RequiredNamespaceLabel`, `Policy:ProtectedLabels` or
  `Policy:AllowSingleReplicaRestartLabel` in config or env, update those too — the defaults moved
  and your overrides did not.
- **`ACT_FIXTURE` now defaults to `c13`**, with `c11` and `c12` still selectable. `--full` runs
  eleven fixtures rather than ten.
- **`PlanVerdict` gained `PlannerNeverRan`.** `NoPlan` pooled four outcomes, and any action rate
  over the total counted a run that never reached the planner as a decline.
- Hosting moved from GitHub Pages to Cloudflare Pages: one Pages site binds one custom domain,
  and this needs three.
- The README leads with what it is, what it looks like and how to try it, before the safety
  argument.

### Fixed
- **An incident that was successfully acted on sat in `Verifying` forever.** The resolve
  transition wrote its audit detail as a bare string into a `jsonb` column, so Postgres rejected
  it (`22P02`) and rolled back the transition that had just succeeded. Four more layers sat on
  top of it: an action with no owner reference could not be verified, a harness wait that ended
  before verification did, an `instance` label read as a node name (so `ClusterFacts` came back
  empty and default-deny refused every action), and finally an assertion that read `.target.name`
  from a list endpoint that does not return it — an assertion that could never pass, and that had
  agreed with four genuine bugs in a row.
- **The `conclude` tool asked for a wrapper `gpt-oss` never sends**, and three budget ceilings hid
  it. `TokenBudgetExhausted` went from 6 of 24 attempts to 0 of 24.
- **Confidence was offered in two places and read from one**, so a well-formed conclusion could be
  scored as though it had none.
- **An auto-executed action left `ApprovedBy` null**, against an invariant three doc comments
  assert. Nothing had ever executed an action on a cluster, so the assertion had no subject.
- **The demo site shipped another state's glyph for three states.** `Escalated` rendered with
  `AwaitingApproval`'s marker, `Investigating` with `Detected`'s, `Detected` with `Expired`'s — on
  every page, since the site existed. The vocabulary is now parsed from `Display.cs`, which
  `docs/design.md` names as its owner, instead of copied.
- **A seeded replay was reported as `PolicyDenied`** by a policy engine the demo stack never
  constructs.
- **The console told every escalated incident that "no diagnosis was produced"**, on pages whose
  primary finding sat directly below the banner. An incident escalates for eleven reasons and only
  some of them mean nothing was produced — one refused by the policy engine is escalated *and*
  fully diagnosed.
- **The redactor's `\b` missed an address that reached a rendered page**, because `\n` before a
  digit is not a word boundary — and then its replacement lost to `\u0022`, which the serializer
  writes for a nested quote and which ends in a digit. The same defect twice, from the same cause:
  this runs over an escaped document, and an escape ends in whatever the encoder chose.
- **`--nightly` published the branch it claimed to be testing**, not the one asked for.
- The README announced `Status: v0.2.0` on a v0.5.0 repository and its install command pinned
  `--version 0.2.0`, so anyone following it installed a three-release-old chart.

## v0.5.0 — 2026-09-01

**Paying the debt down.** A release whose feature is that the list got shorter, scheduled rather
than hoped for.

- **The end-to-end gate went green on the full corpus for the first time** — ten fixtures, 77
  assertions, 98 minutes, $0.115 against a local `gpt-oss-120b`.
- **The MVP bar became evaluable, and was met:** `8/10 correct root cause` against a bar of ≥ 7/10
  over ≥ 10 scenarios, quoted since v0.1.0 and never before gradeable. A truncated investigation
  produces no finding, and no finding cannot be graded — the accuracy was never short, the
  denominator was.
- **Cheaper providers**, pulled forward mid-milestone: `Llm:Provider=openai` reaches DeepSeek,
  OpenRouter and a local Ollama or LM Studio through one factory, at $0.031 per investigation
  against $0.080.
- Three product bugs on the acting path: an auto-executed action left `ApprovedBy` null against
  its own documented invariant; a workload cooldown refused an action as its own precedent, which
  had left four safety gates dormant on the entire auto path; and a `RestartPod` could never be
  verified because its target carried no owner.
- Eighteen fixes in total, most of them in the instrument rather than the product.

**Not established:** the agent has still not been observed closing an incident it acted on.

## v0.4.0 — 2026-08-30

**A design language.** One canonical token set that the console and the landing page both consume
from the same file, canonical by test rather than by convention, and a visual regression net that
photographs every component in both themes on every pull request. Light mode stopped being a
courtesy.

It also found that the console had **never been interactive in any released image** —
`blazor.web.js` returned 404 in every published build, so every button was dead across four
releases, and nothing had been able to see it.

## v0.3.0 — 2026-08-30

**It reaches people.** An escalation is written to a Postgres outbox in the same transaction as
the state change that caused it, and delivered to a generic HTTP endpoint or a Teams card with
retry, rate limiting and a link back to the incident. Ships delivering nowhere.

Measured against a real cluster, including the assertion the design exists for: receiver taken
down, agent restarted mid-flight, receiver brought back, delivery arriving anyway.

## v0.2.0 — 2026-08-30

**It acts, carefully.** Executes a narrow allowlist of reversible actions, verifies them at
T+60s / T+5m / T+15m against deterministic predicates, reverts or escalates when they do not hold,
and closes the incident when they do. Ships configured to act nowhere.

## v0.1.0 — 2026-08-29

**Diagnosis you can trust.** The eval harness and a cassette corpus, so a prompt change is
measurable without a cluster. Met its gate at 22/24 correct root cause over replay.

## v0.0.1 — 2026-08-29

Multi-arch image and Helm chart published to GHCR with build provenance attested.
