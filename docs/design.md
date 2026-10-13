# Hephaisto design language

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

This is what a contributor is pointed at before touching CSS.

**Read this first if you are about to change a colour:** the token set in
[`design/tokens.json`](../design/tokens.json) is canonical and a test fails if a colour is
written anywhere else. [`design/gallery.html`](../design/gallery.html) is what the visual
baselines photograph; if you change a component, change it there too, and run
`scripts/visual-test.sh`.

---

## The brief already existed, as a comment

For three releases the design system was the header of a single file,
`src/Hephaisto.Agent/wwwroot/app.css`. It is a better brief than most projects write, and
nothing in this document contradicts it:

> Plain CSS, no framework, no CDN. This pod can run in a cluster with no egress, and an incident
> console whose stylesheet fails to load is unreadable at exactly the moment somebody needs it.
>
> Dark first, dense, monospace for anything a human might compare character by character — ids,
> workload keys, log excerpts, timestamps. Target reader: on call at 3am, on whatever monitor is
> in the room.
>
> **STATE IS NEVER COLOUR ALONE.** Every state, severity, risk and decision carries a glyph and a
> word next to it. Colour is the third channel, never the only one.

What it lacked was reach. It could not be found by anybody deciding a landing-page question, and
nothing outside that one file could consume it.

---

## Direction, settled before anything was drawn

Recorded here **before** the first option was drafted, deliberately. Deciding after looking at
three pictures means the picture makes the argument, and the reasoning is reconstructed to fit
whichever one felt best.

### The landing page looks like the product

Dark, dense, terminal-adjacent. Not a contrasting editorial treatment.

The console *is* the pitch. A landing page that looks like a different product asks the reader to
take on trust that the thing behind the button is good, when a screenshot of the incident table
makes the argument directly. It also costs one palette and one density scale rather than two, and
every pixel of that investment is shared with the thing people actually use at 3am.

### There is one reader, and it is a platform team assessing autonomy risk

Not an SRE shopping for a tool, and not a contributor.

This is the decision that orders the landing page. The safety model is this project's most
distinctive asset — Observe by default, empty allowlists, per-action-type autonomy, policy gates
with recorded reasons, an immutable audit trail, a kill switch with a runaway latch — and the
question a platform team actually has is *"what stops it?"*, not *"what can it do?"*. So what
stops it leads, and the capability is the second screen.

An SRE reading a page written for a platform team still learns what it does. A platform team
reading a page written for an SRE has to go looking for the part that decides their answer.

### Light mode stops being "a courtesy"

`app.css` used to say, at the top of its light block, *"Light mode is a courtesy, not the design
target: this thing is read in a dark room."* That was honest and it is no longer the position.

A landing page brings evaluators, and some of them open the console in a bright room on a
projector in a meeting about whether to trust it. **Both themes are supported, both have their
contrast checked, and both are photographed by the visual baselines.** A component is not done
until it is correct in both.

This is a real cost — it roughly doubles the palette work and the baseline count — and it is
accepted rather than hedged.

### Typography respects the tighter constraint everywhere

The pod may run in a cluster with no egress, so the console cannot load a font from a CDN. The
landing page and docs have no such limit, and could diverge.

**They do not.** System stacks and self-hosted faces only, on all surfaces. "The landing page
looks like the product" is not a claim you can make while setting it in a typeface the product
cannot load.

### Personality was not decided in the abstract

Hephaisto is the god of the forge, which offers an obvious metaphor and an equally obvious cliché.
That question is not answerable from a table, so it was not asked as one: the candidate directions
each took a different position on it and the choice between them settled it.

---

## The direction that was chosen: Forge

Three complete directions were drawn and judged side by side. **Forge** was picked.

**Heat is an encoding, not an ornament.** The ground is warmed, the accent is an ember, and the
cool end of the ramp is where things have settled. There are no anvils, no hammers and no sparks
flying off a horizon; the metaphor earns its place by doing semantic work or it does not appear.

The other two are recorded here because knowing what was rejected is how a future reader avoids
re-proposing it. *Instrument* was the current palette disciplined, with no metaphor at all — the
smallest change and the least memorable result. *Ledger* was near-monochrome with hairline rules
and a serif, arguing that the distinctive asset is the record rather than the remediation; it
was the most restrained and the least forgiving of a sloppy detail.

### The cost Forge carries, and what stops it being paid

`--accent` is purely interactive — links, focus rings, the approval affordance — and red, orange
and yellow are the severity ramp. An ember accent therefore sits in the middle of the hue range
that already means *something is wrong*.

This was named as the price of the direction before it was chosen, and it is real. The first
palette drafted for it put `--accent` and `--orange` **1.24:1** apart, which is two colours a
reader cannot tell from one another: a link and a warning would have looked the same.

`TheInteractiveAccentIsNotMistakableForASeverity` is what stops that recurring. It asserts more
than 1.5:1 between the accent and every severity colour, in both themes. That is not a legibility
threshold — it is a *these are visibly two different colours* threshold, and the specific decay it
forbids is the accent drifting back into the severity ramp while somebody adjusts something else.

---

## The rules

### 1. State is never colour alone

Every state, severity, risk and decision carries **a glyph and a word** beside it. Colour is the
third channel and never the only one.

This is the oldest rule here and the most load-bearing. It is what makes the console readable to a
colour-blind operator, in a monochrome screenshot pasted into a ticket, and on whatever projector
is in the room. It is also what lets Forge use a near-monochrome severity ramp in light mode
without losing information.

### 2. Colours live in `tokens.css`, and nowhere else

[`src/Hephaisto.Agent/wwwroot/tokens.css`](../src/Hephaisto.Agent/wwwroot/tokens.css) is the
canonical set. A hex, `rgb()` or `hsl()` literal in any consuming stylesheet **fails the build**.

The rule is not stylistic. Two colours escaped before the rule existed: `#10131a`, written twice
as the text colour on a `var(--red)` ground. That is correct in dark mode, where `--red` is a light
pink, and wrong in light mode, where it is a dark crimson — so the error banner rendered
near-black on dark red for three releases and nobody saw it. A convention lasts exactly as long as
the person who remembers it.

**The one exception is `theme-color`**, which the browser reads before any CSS and which therefore
cannot be a `var()`. It is allowed to be a literal only because a test asserts it still equals
`--bg` in each theme.

### 3. Both themes, held to the same bar

Light mode is no longer a courtesy. Contrast is asserted in tests, in both themes:

| Role | Bar | Why |
|---|---|---|
| body text, secondary text, the accent, text on an alert ground | **4.5:1** (AA) | it is read as prose |
| faint labels, every semantic hue, meter fills | **3:1** (AA large) | it is seen, not read |
| borders | **1.2–3:1**, stated | see below |

Borders are deliberately held *below* the control bar. `--border-strong` measures 1.86:1 dark and
1.79:1 light. WCAG 1.4.11 asks 3:1 of anything that identifies a component or its state, and these
borders do neither — no border in this console ever carries state, because of rule 1. Raising them
would draw every hairline about as loudly as the text it separates, on a page whose whole argument
is density. So the bar is *stated with both bounds* rather than dropped: a border must stay visible
as a division **and** stay quiet.

### 4. A token needs a consumer, in the same commit

The design-language form of this repo's standing rule that config needs a reader. A token nothing
reads is a colour somebody will later assume is in use.

This is why there is **no spacing scale**. The paddings in `app.css` are hand-tuned per component —
0.5/0.7, 0.5/0.8, 0.25/0.5, 0.7/0.9 — and no ratio joins them. Extracting one would mean *inventing*
it and renumbering about thirty declarations, changing every surface in the console. That is a real
improvement and a real decision; it is not one to make silently while moving tokens around.

Density is adjusted through `--root-size` instead, which works because every length in `app.css` is
already expressed in `rem`.

---

## Half the design system is in C#

[`src/Hephaisto.Agent/Components/Display.cs`](../src/Hephaisto.Agent/Components/Display.cs) owns
the glyph vocabulary, the enum-to-class mapping and every number format. **A guideline that covered
only the stylesheet would document half the system.**

| Concern | Where |
|---|---|
| `StateGlyph`, `SeverityGlyph`, `DecisionGlyph`, `CodeFixGlyph`, `WorkItemGlyph` | `Display.cs` |
| `StateClass`, `SeverityClass`, `RiskClass`, `CodeFixClass`, `WorkItemClass` | `Display.cs` |
| timestamps (always UTC), USD to 6 dp, `ShortId`, byte and token counts | `Display.cs` |
| everything those classes then look like | `app.css` |

**The glyphs are ASCII and never emoji.** Emoji render at unpredictable sizes across platforms and
break the monospace grid, and the grid is what lets a reader compare two ids character by
character.

The full vocabulary — ten states, three severities, four risk tiers, three decisions — is rendered
in the gallery, which is how the "glyph and a word" rule stays enforceable rather than aspirational.

### The code-fix vocabulary (v0.9.0)

A code-fix attempt has its own lifecycle — it routinely runs on an incident that is already
Closed — so it has its own glyphs, and **borrows the incident ones wherever the meaning is the
same**, so a reader of one table can read the other. The state cells reuse the `st-*` classes
through `CodeFixClass`, which makes "waiting for a human" one colour on both tables by construction.
No token was added for it.

| State | Glyph | Word | Class | Why |
|---|---|---|---|---|
| Eligible | `*` | eligible | `st-detected` | exists, nothing runs yet |
| Planning | `~` | planning | `st-investigating` | read-only work in progress |
| PlanReady | `!` | plan ready | `st-awaiting` | waits for a human |
| Implementing | `>` | implementing | `st-acting` | writing |
| PrOpened | `+` | pr opened | `st-resolved` | the good end for Hephaisto |
| Failed | `x` | failed | `st-escalated` | the one that says "look at this" |
| Denied | `-` | denied | `st-closed` | a person decided; dim, not faint |
| Expired | `.` | expired | `st-expired` | nobody answered |
| Cancelled | `/` | cancelled | `st-suppressed` | a switch stopped it; nobody judged it |

The console's approve/deny for a plan goes through `CodeFixCoordinator.DecideAsync` with the actor
resolved as the API resolves it: the token's name when signed in (no name field is offered), the
typed name otherwise, and the approve policy checked so the console is never a wider door than
`POST .../approve`. Approve is disabled — with the reason printed beside it — when the mode is not
Pr, when the plan needs a Cait change first, or when the viewer lacks the approver role. Deny stays
available: refusing a plan never needs more than being a named human.

### Work items, and an attempt's own page (v0.14.0)

A GitHub issue handed to Hephaisto is a work item, and it has three states. They take the
code-fix vocabulary rather than a fourth one, and **no token, class or component was added**:

| State | Glyph | Word | Class | Why |
|---|---|---|---|---|
| Taken | `~` | taken | `st-investigating` | work in progress |
| Done | `+` | done | `st-resolved` | its pull request was merged |
| Cancelled | `/` | cancelled | `st-suppressed` | taken back, closed, or its pull request closed; nobody's failure |

`/workitems` is the code-fix table with other columns. `/codefixes/{id}` is one attempt on a page
of its own - the detail header, the timeline as its history, and the attempt panel the incident
page has, whose plan is the shared `CodeFixPlan` component so the two cannot come to describe one
plan differently. Approve and deny there follow the rules above and go through the same door, by
the attempt's subject (`DecideAsync` for an incident, `DecideForWorkItemAsync` for a work item).

### The planner (v0.15.0)

`/planner` is one GitHub project's open issues in three views. It took the existing vocabulary
and **no token**: the card is the code-fix card, the reason is the banner of a plan that waits,
the facts are `hp-kv`, a row of choices is the feedback form's `hp-field` with `hp-thumb on` for
the current one.

| What | Glyph | Word | Class |
|---|---|---|---|
| merged, still open | `!` | merged, still open | `st-awaiting` |
| every sub-issue done | `+` | every sub-issue done | `st-awaiting` |
| pull request quiet | `~` | pull request quiet | `st-awaiting` |
| a required field is empty | `?` | a required field is empty | `st-awaiting` |
| nothing asks for a decision | `.` | open | `st-closed` |

Every reason is "a person decides", so they share the colour of a plan that waits and are told
apart by glyph and word - which is why `DisplayVocabularyTests` holds both unique. A priority is
coloured by where it stands among its field's options (`sev-critical`, `sev-warning`, then
`sev-info`), because a board names them as it likes. The current choice of a field carries `*`;
today is the accent edge of a row *and* the marked half of its switch; put away is faint and says
so in the other half.

The issue's description sits under its title in `hp-excerpt` - the reading face, as high as a
screen allows and no higher, because the buttons are below it - as its markdown source. It is
never rendered: a heading stays `##`, an element stays its angle brackets.

New in `app.css`, all layout: `hp-seg` (the joined switch between views, and small on a row),
`hp-planner` and `hp-planner-queue` (the walk's two columns), `hp-planner-day` (today / not today,
larger and set apart), `hp-epic-head` (a group's heading with its progress), and
`hp-planner-ids` - a reference with what is said about it on one line, with a gap, because Razor
drops the spaces around a conditional block and the pieces would otherwise touch.

**Somebody else's text is text.** An issue's title, a plan, a pull request's description and a
rejection's reason are written by a stranger, a model, or an approver typing into a comment. They
are rendered through Razor's encoding and nowhere through `MarkupString`: no markdown, no HTML,
no link made from them. An address is a link only where Hephaisto recorded it as one - the
issue's, the pull request's - and only when it is absolute http(s) (`Display.HttpUrl`). The
console suite holds this by comparing an element's text with the stored string and counting the
elements inside it: none.

---

## Naming

Two tiers, and this was the convention for three releases before anybody wrote it down:

- **`hp-` prefixed** classes are blocks: `hp-finding`, `hp-meter`, `hp-table`.
- **unprefixed** classes are modifiers applied *alongside* a block: `.hp-state.st-escalated`,
  `.hp-chip.chip-primary`, `.hp-meter-fill.meter-warn`.

Modifier families are `st-*` (state), `sev-*`, `risk-*`, `dec-*`, `mode-*`, `chip-*`, `callout-*`,
`meter-*`.

**Do not select on these from a test.** The e2e suite used to assert `.not.toHaveClass(/hp-alarm/)`
and `.hp-callout.callout-escalated` with a count of zero — two assertions of *absence* anchored on
presentation classes. Renaming either during a refactor would not have broken them; it would have
made them pass forever, on a page that no longer contained the thing they were watching for. State
is exposed to tests as `data-testid` and `data-*` attributes instead.

---

## Typography

| Role | Face | Notes |
|---|---|---|
| prose, headings, UI | **Archivo** 400/700 | a grotesque drawn for signage and small sizes; slightly condensed, which buys a nine-column table about a column of room |
| ids, workload keys, log excerpts, timestamps, anything compared character by character | **JetBrains Mono** 400/700 | drawn for long terminal sessions |

**Both are self-hosted, and that is a hard constraint rather than a preference.** The pod may run
in a cluster with no egress, so a CDN webfont is a stylesheet that falls back silently to a system
stack on exactly the installs that are hardest to debug. Latin subsets only: 66KB for both, against
roughly 300KB for the full set.

The landing page accepts the same constraint even though it does not have it. *The landing page
looks like the product* is not a claim you can make while setting it in a typeface the product
cannot load.

The scale is `--text-2xs` through `--text-4xl` around `--text-root`, and note that almost every step
is **below** the root: this is a dense console and most of its text is smaller than the browser
default.

---

## Accessibility

Part of acceptance, not a follow-up.

- **Contrast** is asserted in both themes by the table above.
- **Focus is visible on everything focusable.** Links were missing from the `:focus-visible` rule
  until v0.4.0 — and links are most of what a keyboard user moves between here. There is a
  committed baseline of the focus ring, taken with real keyboard focus rather than `.focus()`,
  because `:focus-visible` does not match a programmatically focused element in every engine.
- **The page heading is the exception**, deliberately. `<FocusOnNavigate Selector="h1" />` moves
  focus there on every navigation, and the heading carries `tabindex="-1"` so it is never reachable
  by Tab. A ring there is not telling a keyboard user where they are; it is drawing a box around a
  heading somebody just navigated to on purpose. Suppressed.
- **`prefers-reduced-motion` is honoured**, in the two places anything moves, and the test each
  time is *does the information survive*. The citation flash is **substituted** — it becomes a
  static outline rather than disappearing, because the flash is the only thing telling you where
  you just landed. The running-investigation pulse is simply **removed**, because the glyph and
  the word beside it already say `running`; rule 1 is what makes that safe.

---

## Before you change any CSS

```sh
scripts/visual-test.sh            # compare against the committed baselines
scripts/visual-test.sh --update   # regenerate them, then LOOK at the diff
./scripts/test.sh                 # the token guards
```

[`design/gallery.html`](../design/gallery.html) is what the baselines photograph: every component
the language has to keep working, rendered from the shipping stylesheet with frozen data. **If you
change a component, change it there too**, or the net stops covering it. That is not hypothetical —
two near-duplicate font sizes coexisted unnoticed precisely because the only components that used
them were the form controls, which the gallery did not have.

Baselines are taken in the pinned Playwright container on every machine, because font rasterisation
is not portable and a suite that fails in CI on antialiasing is a suite people learn to re-baseline
past.

---

## What this does not cover, and is honest about

- **There is no responsive design.** Zero width breakpoints. The tables are laid out expecting
  roughly 1200px of room, and below that they scroll inside themselves rather than reflowing. This
  console is opened on a desktop by somebody on call; a phone layout has never been attempted and
  should not be claimed.
- **There is no spacing scale.** See rule 4.
- **The theme choice lives in one browser.** System, light or dark is chosen on the account page
  and remembered in `localStorage`, so it does not follow a person to another machine. Storing it
  on the account would need somewhere to keep per-user state, and this console has none.
- **Two components are duplicated** and should be consolidated: `hp-meter-*` and `hp-conf-*` are
  unrelated implementations of a bar, and `hp-code` and `hp-excerpt` are near-duplicate treatments
  of a monospace block.
