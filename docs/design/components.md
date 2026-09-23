# Components

Anatomy, states, and tokens for every part the product is built from. Angular Material
(M3) provides the behavior and accessibility underneath each mapped component; every
visible value comes from this system. If a component isn't specified here, specify it
here before building it.

All interactive components share: focus ring `2px solid var(--bud-focus-ring)` with
`outline-offset: 2px`; state layers from [color](color.md); 48px minimum touch targets;
`--bud-motion-micro` for state transitions.

**A quoted sentence is transcribed, not typeset.** Several chapters below say of their copy
tables that *the copy is the specification, not an example of it*, and the characters are part
of what that sentence promises. The product's apostrophe is typographic — `’`, U+2019, the rule
[voice](voice.md) states — so a table spelling it `'` specifies a string no screen renders, and
nothing catches it: the two look alike in a diff, in a review, and on the page. It runs one way
only. This book's own prose keeps the plain apostrophe, so a mismatch is fixed by reading the
string out of the source and matching it character for character, never by replacing one
character throughout the file — a substitution that happens to produce the right glyph proves
nothing about the sentence, and quietly "corrects" every quotation that has no implementation
to be checked against.

## Iconography

**Material Symbols Rounded**, weight 400, grade 0, optical size 24, outlined
(`FILL 0`). Filled (`FILL 1`) marks exactly one thing: the active navigation
destination. Sizes: 20 (inline), 24 (default), 40 (empty states). Icons never appear
without an accessible name — a visible label or `aria-label`.

The navigation was the first icon to ship, and it brought the font with it:
`public/fonts/material-symbols-rounded-subset.woff2`, **1.5 kB for four glyphs** against
15 MB for the upstream face. It may never come from a CDN. `wght`, `GRAD` and `opsz` are
pinned into the bytes at the 400 / 0 / 24 above, and the **`FILL` axis stays variable**, so
the active-destination state is a `font-variation-settings` change on one file rather than
a second file that could drift.

**Glyphs are addressed by codepoint, not by ligature**, and the codepoints never appear in
a template — they live in one named map beside the destination list. Subsetting for
ligatures costs about 87 kB here, because the ligature closure drags in a thousand
placeholder glyphs and every letter that spells a name. Adding an icon means regenerating
the subset; the recipe and the verification checklist are in `public/fonts/README.md`. See
[no third-party origins](../engineering/no-third-party-origins.md).

## App shell and navigation

Destinations: **Home, Transactions, Accounts, Categories, Settings**, plus the **Add**
action.

**The navigation belongs to a route, not to the root shell.** It is drawn by `ShellComponent`, the
layout component on the `app` route, and it is the only place in the product that renders a
navigation at all. So **which screens carry a bar is a fact about the route table**: everything under
`/app` is a child of that layout and behind `authGuard`, while `/welcome` and `/register` are
*siblings* of it — a layout mounted there draws over the one set and cannot reach the other, however
anything reads.

The alternative is a root shell asking the session service whether to draw a bar, and it is wrong
twice. It answers a question about **identity** where one about **position** was asked, and it keeps
a signed-in navigation painted over the welcome screen for as long as a stale status says the visitor
holds a session — on the two screens whose whole job is getting somebody a session in the first
place.

**Settings is a destination.** It was specified as deliberately *not* one, reachable only by
typing its URL. That was a product decision and it was overturned: Settings is where signing
out, the export, the credential list and the recovery-code count live, so a surface with no
entry to it is one whose whole account-management half is unreachable without a keyboard and
prior knowledge.

**What ships is four of the six.** The shell renders Transactions, Accounts, Categories and
Settings, evenly distributed, and neither **Home** nor **Add** is built:

- **Home** has no route. `/app` redirects to `/app/transactions` and there is no screen for a
  home destination to reach.
- **Add** has no flow. Each screen carries its own inline form, so a central button has
  nothing to open, and this book's own rule is that a control which cannot be activated does
  not get to look like the most important thing on screen. When the flow exists, the bar
  parts around it as specified below; until then the four are spaced evenly rather than left
  with a gap where it would go.

Both are departures from what follows, and the sections below are the target rather than a
report. `shell.component.ts` names both omissions beside the destination list so they read
as decisions rather than oversights.

### Bottom bar (compact, < 960px)

- Paper background (`--bud-bg`), top hairline, no elevation, no blur. Height 64px +
  `env(safe-area-inset-bottom)`.
- Five slots: four destinations around a centered Add button. **Four even slots ship
  today** — see the destination note above.
- Item: icon 24 above a `caption` label. Inactive: `--bud-text-muted`, icon outlined.
  Active: `--mat-sys-primary`, icon filled, label weight 600, and a **4px mint bead**
  centered beneath the icon — the bead marks "you are here".
- **Add button**: 48px circle, `--bud-accent` fill, ink `#1E231E` plus icon (both
  themes — ink on mint is 8.1:1), sits flush in the bar. It is the most important
  control in the product; nothing else in the bar may be filled.

### Rail (expanded, ≥ 960px)

- 88px left rail on paper, right hairline. Mark (not lockup) at top, 28px, forest.
- Add button (48px mint circle) directly under the mark, then destinations as
  icon-over-label items. Active: filled icon, primary color, 4px bead centered under
  the icon pair.
- Content area becomes a centered column, max 1080px.

**The bead has one placement, not two.** The bar section says "beneath the icon" and the rail
section "under the icon pair"; what ships puts it under the pair in both, because a bead
wedged between an icon and the label it belongs to splits a pair this book otherwise keeps
together.

**The bar and the rail are one `<nav>` and one list**, switched by grid rules rather than
rendered as two elements. Two would put every destination into the accessibility tree twice.
The mark is the only part that belongs to one layout, and it leaves by `display: none`, which
takes it out of that tree as well as out of the picture.

**This is the largest component stylesheet in the product**, and the production
`anyComponentStyle` warning was raised from 2 kB to 3 kB for it — the error stays at 4 kB. It
is the one component that carries two complete layouts, and a standing build warning teaches
a reader to ignore build warnings.

### Screen header

Part of the page plane: page title (`title` role, Mohave 500 24) left, contextual
actions right, padded by `--bud-gutter`. A hairline fades in under it (130ms) only
when content is scrolled beneath. No Material toolbar styling.

## Buttons

M3 base: `MatButton`. Radius `--bud-radius-md`, min-height 48px, padding 12px 24px,
label style `label` (Inter 600 15), icon 20 with 8px gap.

| Variant | Surface | Label | Use |
| --- | --- | --- | --- |
| Primary | `--mat-sys-primary` fill | `--mat-sys-on-primary` | The screen's one main action |
| Outline | `--bud-surface`, 1px `--bud-hairline` border | `--bud-text` | Secondary actions; the provider control on the registration flow's introduction step is this spec |
| Ghost | transparent | `--bud-accent-text` | Tertiary, inline, and dialog-dismiss actions |
| Destructive | `--bud-over` fill | `#FFFFFF` | Deleting, erasing, and any act with no way back; only after confirmation UI |

One primary button per view. Hover on Outline may invert to primary fill; Ghost and icon
buttons use state layers. Icon-only buttons: 40px visual, 48px target, always
`aria-label`. **Nothing ships that inversion today** — the one control that drew it was a
shared provider sign-in button, deleted when the provider stopped signing anybody in, and
the provider control that replaced it is a plain Material `outlined`. Read the inversion as
the target, like the rest of this table.

**The fill makes two promises and an act needs only the second to earn it**: that a confirmation
follows, and that there is no way back. Deleting and erasing keep both, which is why the Use column
above named them first and for a long time named nothing else. The key-rotation control is the first
to take the fill without deleting anything — it overwrites every sealed value in an account in
place, keeps no copy of what was there, and ends with the previous keys opening nothing, which is
its purpose rather than a side effect. Read the column as the second promise with two examples in
front of it, not as a list of two.

A destructive action whose confirmation UI does not exist yet is specified as **Outline,
disabled** — never Destructive. A control that cannot be activated should not promise that a
confirmation follows. Disabled is not
self-explanatory either: a `[disabled]` button leaves the tab order and screen readers skip
it, so the sentence saying what it waits on is **visible prose beside the button**, never a
`title`, a tooltip, or an `aria-describedby` on the disabled element. A control that is
merely *busy* is a different case: it keeps its place in the tab order — Material's
`disabledInteractive` renders the disabled appearance while leaving the element focusable —
because a button that goes truly `disabled` under the finger drops focus to `<body>`.

**A third case: a control waiting on the person, not on work.** A button gated on a choice the
reader can make right now — ticking an acknowledgement a few pixels above it — is neither busy nor
unavailable for the life of the screen. It takes `disabledInteractive`, for a different reason than
the busy one: the press *has* an answer and the answer is one tab stop away, so a control that left
the tab order would hide the gate from the only person who can open it. It needs no sentence beside
it either — the thing it waits on is the visible control immediately above, and "waits on the
checkbox above it" is noise, not help.

**Where `disabledInteractive` is used, the gate is also in the handler, and that is not belt and
braces.** Material's click-halt is applied to anchors only; on a `<button>` the DOM `disabled`
property stays `false`, so the click reaches the component. A gate written only as an attribute is
an ungated action wearing a disabled appearance — on the registration step, an account created for
somebody who acknowledged nothing.

**What renders today is not the table above.** No `MatButton` on any screen yet matches its
radius, min-height, label style or surface: Material's M3 defaults are what ships, so
`outlined` draws a pill with a `--mat-sys-primary` label rather than this book's Outline
(`--bud-surface`, 1px `--bud-hairline`, `--bud-text`). The table is the target and bringing
the buttons onto it is its own change, made once in `styles.scss` rather than per component.
The rule is unaffected: whatever the fill turns out to be, an unconfirmable destructive
action is the secondary variant and disabled, never Destructive.

## A value read from the network

Any section that fills itself from a request answers by this rule, whatever it renders — a row, a
list, a count, a figure. The chapters below apply it; none of them owns it.

**A value is on screen only as the answer to the read that is running.** A section renders at most
one of: the value, the line saying the read is running, the sentence saying it failed.

**The value is therefore cleared when a read starts, not when one fails.** Cleared on failure only,
the previous answer is still on screen *during* the retry, beside the line saying it has not
arrived — the same contradiction one state earlier. Where the value's box is reserved
(`min-height: 1lh`, below) clearing costs no layout at all; where it is a list, what replaces it is
the loading line the reader is looking at anyway.

**What it clears to is the section's own "no answer yet", never a substitute for it** — `null` and
not `0` for a count, `null` and not `[]` for a list, `null` and not `""` for a string. Each
substitute is a sentence the section may render only once the server has said so: that you have no
recovery codes, that nothing is attached to your account, that your address is blank. Substituting
one turns a request that failed into a claim about the account.

**"No answer yet" and "the read is running" are different states too, so the running state is
published rather than inferred from the absent value.** The value is absent at rest, absent in
flight and absent after a failure, so a section reading its loading line off that absence has one
predicate covering three states — and it goes on saying *loading* to somebody whose request has
already given up. Published, it is set when the read starts and cleared however the read ends,
which makes the exclusivity above structural rather than a matter of which template branch happens
to be written first. A section may render no loading line at all: then the reserved blank box is
what says the answer has not arrived, and the rule stands with that render unused.

**Loading and failure are exclusive by structure**, not by coincidence: a load that failed is not
still loading, and rendering both asks the reader to keep waiting on a request that has already
answered.

**The lines land in a `role="status"` region that is in the DOM from first paint**, empty until
there is something to say. A live region created at the moment it gains content is announced
unreliably, because assistive technology has to have been watching the node before the text landed.
It is `status` and never `assertive`: these are results of reads a screen started on its own, and
assertive is reserved for a failure to save something the person typed
([accessibility](accessibility.md)). Colour is never the message — every failure sentence reads the
same with `--bud-over` removed.

**A list that is absent with nothing loading is a read that failed, and it gets its own render.**
Three states are not enough for a section whose value is a list: locked, the list, and "the read is
running" leave a fourth — absent, not loading — with no branch, so a failed read draws **nothing at
all**. All three content screens shipped that way; the fourth state is now one of two words a
**published predicate** answers with, inside the region below, rather than a fourth arm of the render
chain.

**That predicate is where the exclusivity actually lives, and three things it has to get right were
each held by nothing until they were measured.** The running flag is set by every **write**, not only
by reads — so a predicate that reads the flag alone draws *reading…* underneath a list that is
already on screen, every time somebody saves. The two flags are **simultaneously reachable**: the
failure flag is cleared by a read starting, so a failed read followed by a write raises both, and
which one wins has to be decided rather than fallen into — *reading* wins, because that request is
in flight now. And the predicate is **silent while the locked notice is up**, because the notice
speaks for that state itself.

Two more traps from the same work. A screen mixing `?.length === 0` in one place with `?? 0` in
another disagrees with itself about `null` and disagrees **toward silence** — a sentence vanishes
while a control stays disabled. And on one screen a spec case *asserted* the blank render by name,
so the fix arrived as a red bar and would have looked like a regression to anyone who did not read
it.

**A region created at the moment it gains content passes every test that only checks the text.**
Measured: the naive fix — wrapping the region in the same condition as its content — was green over
the entire existing suite. What holds the rule is a case asserting the node is present **before**
there is anything to say.

**There is exactly one such region per screen, and each screen counts them in every state it
renders** — value on screen, read in flight, read failed, account locked — because a second region
added inside a branch is invisible from any other. A duplicate is not cosmetic: a screen reader
announces both, and the first one in document order is the only one a `querySelector`-shaped test can
see, so the twin is silent to the suite and loud to the person. The region's **position** is
deliberately unpinned: that is a layout decision this book owns, and a spec asserting DOM order would
fight the next legitimate change.

**A stale value is not equally harmful in every section, and the section it harms most sets the rule
for all of them.** A count that is stale is merely old. An address is *wrong*: it is the single
value that answers the only question its row exists for, and the same read is what updates it on the
day changing an address becomes possible, so a refresh that failed would leave the previous address
standing as the answer. A section may not keep its last answer through a failed re-read on the
argument that its own value ages harmlessly — the reader cannot tell a kept answer from a fresh one,
and nothing in the render tells them which they have.

## A write that does not happen

The write-side sibling of the chapter above, and it sits here because it inherits that chapter's
shape: one region per screen, published states, exclusivity by structure. What it does not inherit
is the polarity, and the inversion is the first rule. A **read** clears its value when the read
starts, because what is on screen may only ever answer the request that is running. A **write**
clears nothing until an answer says the row exists, because what is on screen is the person's own
text and it answers no request at all. Copy the wrong half and the screen empties a field in order
to describe it.

Every write in the product answers by this rule, and there are two kinds of them — **four writing
surfaces and five row acts, across three screens**. The surfaces are `/app/accounts`, both halves
of `/app/categories` and the transaction form; the acts are the deletes and moves specified in
*A write that holds no typed text* below, which hold no keystroke and take their own copy. The
chapters below apply it; none of them owns it.

**A refused write is never silent, and it never costs a keystroke.** Two rules, and neither implies
the other: a screen can say exactly what happened and still have emptied the field the sentence is
about, which is worse than saying nothing, because it is silence plus a false claim about what is
in the box.

### The service has to have an outcome to report

**A write says how it ended, and "it finished" is not how it ended.** A write pipeline that logs a
refusal and completes with no value does not make this chapter hard to implement, it makes it
unreachable: there is nothing for a template to branch on, and a screen that wanted to render a
refusal could not. So the contract is part of this specification rather than an implementation note
beneath it — a screen receives one of the outcomes below, and **a screen never reads a status code
to find out which**.

### Three places, and only one of them is validation

- **A message about a field the person can correct goes beneath that field**, per [text fields and
  selects](#text-fields-and-selects): the border and the message in `--bud-over`, the message as
  `caption` under the control, bound by `aria-describedby`. This is the only outcome that touches a
  field at all.
- **Everything else goes in the screen's one `role="status"` region** — one sentence, or one line
  per entry where the outcome carries several — `--bud-over`, the Account keys section's treatment,
  applied to an act instead of a read. A conflict and an unanswered request are not validation:
  nothing the person typed is wrong, so there is no field to hang a message on and hanging one on
  a field would ask somebody to retype a correct value.
- **Nothing goes in a snackbar.** The [Snackbar](#snackbar) chapter's prohibition takes no exception
  here and gains no clause. A snackbar lasts four seconds and never stacks, so a sentence naming a
  field is gone before the person has found the field — and the field is where the correction
  happens.

**The region is the one the screen already has**, shared with the read outcome and the locked
notice, counted in every state per the chapter above. A second region for write outcomes is the
duplicate that rule refuses: announced twice, and invisible to the branch that did not create it.
Exclusivity extends unchanged, and it is over **outcomes, not sentences**: a screen never shows
two accounts of what happened. One outcome may take more than one line, and may render in two
places at once — an `errors` map only some of whose keys the form can place puts a message under
each of those controls *and* a line in the region for every key it cannot, which is one answer
to one write rather than two. What may never share the screen is a second outcome. Where a read
is in flight its line wins, because that request is running now and this one has answered. A
write's sentence is cleared when the next write starts, and by nothing else.

**So a write's sentence comes back when a read that displaced it finishes, and that is the intent
rather than a leak.** The read's line wins for as long as the read runs, and only the next write
clears the write's sentence — so a refusal made before a slow read reappears the moment the read
answers, possibly minutes later. The next reader will take that for a string somebody forgot to
clear. It is not: the form is still holding the text that was refused, nothing has been saved, and
the sentence is as true when it returns as it was when it was written. Clearing it on the read's
answer would leave a form full of unsaved text with nothing on screen saying why — the silence the
rule above refuses. The sentence goes when the person acts on it, and the act is the next write.

### The states

The copy is the specification, not an example of it.

| State | Copy | Where it renders |
| --- | --- | --- |
| At rest, and after a write that landed | *nothing* | No field carries a message; the region carries no sentence of this chapter's |
| The write is running | *nothing* | The pressed control is held busy per [Buttons](#buttons); an ordinary one-request write is not narrated |
| The write is running, where it is more than one request | "Recording…" | Inside the region, `body` `--bud-text` — the transaction form's payee-then-transaction pair, and nothing else today |
| The `errors` map names a control this form has | The server's sentence for that key, verbatim | Beneath that control, `caption` `--bud-over`, per text fields; focus moves to the first such control |
| The `errors` map names anything else | The server's sentence, verbatim, one line per entry | Inside the region, `--bud-over` |
| `conflictKind: duplicate_identifier` | "This entry is already saved. Reload the page to see it." | Inside the region, `--bud-over` |
| `conflictKind: duplicate_name` on the payee create — its one source — and the one re-read still finds nothing | "This payee already exists under a name this tab can’t read. Choose it from the list, or use a different name." | Inside the region, `--bud-over` |
| Nothing answered, or the server failed rather than judged | "Budgetoid couldn’t reach the server. Nothing you typed has been lost — try again in a minute." | Inside the region, `--bud-over` |
| An answer this screen cannot read | "Budgetoid couldn’t save this, and didn’t say why. What you typed is still here — copy it, then reload the page." | Inside the region, `--bud-over` |

**The unreachable sentence is the Account keys section's, with one clause added, and the clause is
the whole difference.** That section answers a read it started on its own, where nothing of the
person's is at stake and there is nothing to reassure them about. Here somebody is looking at a form
holding text they typed, and the one thing they need before pressing anything is that it is still
there. The first sentence stays word for word so the two read as one fact about the network; the
clause is what makes it true of a write.

**A 5xx shares that sentence and an unreadable judgement does not, which is the distinction a reader
will collapse.** A 500 and a dead connection are both the server failing to answer the question, and
a minute is a real remedy for both. A 400 carrying no usable map, or a 409 carrying no kind, is the
server *judging* — it looked and said no, and a minute changes nothing — so its sentence promises no
retry. Two next steps, two sentences; folding them sends somebody to press the same button until
they give up.

**It ends in a step all the same, because copy that stops at reassurance is this chapter's own
complaint one section later.** [voice](voice.md) asks for what happened plus what to do, and *Whose
sentence goes beneath the field* below faults two API strings for carrying the first half alone; a
state whose copy said what happened, added that the text is safe and stopped would be the same
omission, in the book that names it. What cannot be offered here is a **retry** — the
server judged, and the same press collects the same judgement. What can is a **reload**: the one
reading of this state a person can act on is a browser running an older bundle than the API it is
talking to, which is how a 400 with no usable map or a 409 with an unrecognised kind reaches a
screen at all. That is not a promise and the sentence makes none — it says what is on screen is
still there, and what to do with it. **Copy it, then reload** is one instruction in that order for a
reason: a reload is the one act that discards the typed value, so the screen says so and lets the
person spend it, rather than clearing the form on their behalf. Where the cause is on the server
instead, the reload changes nothing and the person still has what they typed.

**`duplicate_identifier` is a create's answer and never a rename's.** The id is minted in the
browser, so a POST retried after a lost answer carries the same one and collides on the primary key
— which is what makes a lost `201` legible instead of duplicating a row. The sentence therefore says
the entry is saved rather than offering another press, because a second press sends the same id and
collects the same 409. **That legibility rests on the write keeping its id across a refusal**: an id
minted per press turns a lost answer into two rows wearing two legitimate identifiers, and this
outcome becomes unreachable.

**The id belongs to the service and is drawn lazily** — on the first press that needs one, kept
through every refusal, cleared by the write that landed. Not at the moment a form is opened, which
would make a service know a component's lifecycle; drawn on the press, it gives the property this
outcome rests on — two presses of the same unsaved content carry one id — and takes no such
knowledge to do it. **No component may hold it under any arrangement**: the id is the associated
data the value was sealed against, so the one place it can be dropped is the one place it must not
be. A lock does not clear it either, deliberately — a locked write sent nothing, and redrawing
across a ceremony puts the two-row defect back on the far side of every unlock. **The payee's draft
is keyed on the blind index rather than on the typed text**, because the index is what the local
match and the server's unique constraint both decide on: a change of case is the same counterparty
and keeps the draft, a different name draws a new one. That is the draft this product guards
hardest, and `payees` is why — the app role holds no `DELETE` there, so a row written twice is
written for good.

**Two wrong readings are accepted and named.** An identifier held by a budget this caller cannot
read answers the same 409 and reloading shows nothing, which takes a guessed 128-bit value to reach.
And a draft outlives the screen that drew it, because the services are root-provided: type a name,
lose the answer, leave, come back to add a *different* row, and that create carries the first
attempt's id — so where the first attempt did in fact land, somebody is told an entry is already
saved, which is true of the id and false of what is in the form in front of them. It is accepted
because the alternative fails in the commoner direction, writing the row twice where nothing on
either side can see it afterwards. See [payees.md](../business-logic/payees.md).

**`duplicate_name` has exactly one source, and the key reads wider than the state is.** The payee
create is the only write in the product that answers a repeated name with a 409. A payee *rename*
answers **400 keyed on `Name`**, and so does an account, a category or a category group on either
verb — those land beneath the field, on the field-keyed row above, and never reach the region. The
cell therefore names the source: somebody arriving from `/app/accounts` looks the key up, finds copy
about payees, and would otherwise read it as a sentence their own screen can render. The split it
comes from is argued in [payees.md](../business-logic/payees.md) — a create's remedy is to adopt the
row that already exists, which is not a field anybody can correct, and a rename's is to choose
another name, which is.

**`duplicate_name` is usually invisible, and the sentence is for when it is not.** The transaction
form answers that conflict by re-reading its payee list **once** and adopting the row it finds, so
the ordinary path ends in a recorded transaction with no sentence anywhere. What is left is a payee
whose own name did not open: it carries no blind index, it can never match, and the re-read comes
back with nothing to adopt — which is why that path abandons rather than looping. The form is
reachable only on an unlocked account, so the cause is a value that failed to authenticate rather
than a missing key, and **Unlock is not the remedy** — the row is in the list, wearing
`narrative-value`'s unreadable marker, and choosing it is.

**On the payee create both conflict kinds buy that one re-read, and an identifier collision
surviving it renders the payee sentence and not the saved one.** A held id is drawn against the name
it was drawn for, so the only way this browser's own draft is taken is that its own earlier create
landed and lost its answer — the row wearing that id holds this name and this index, and is exactly
what the re-read finds. Where the re-read finds nothing, the row is one this browser cannot read,
which is the payee state's own case. *This entry is already saved* would be false twice over there:
the transaction was not saved, and the payee is not one anybody can adopt. The identifier row above
is keyed on what a screen renders, and a create nested inside another write answers with the outcome
of the write it belongs to.

### Whose sentence goes beneath the field

**The server's, rendered verbatim.** The client writes no copy for a field-keyed refusal and holds
no table of its own.

**The decisive argument is that the client cannot enumerate what it would have to write.** The
`errors` map is the server's and the form is the client's, so a client-authored sentence needs a
lookup from wire key to copy that is total over every string the server can send — the credential
list's problem exactly — and the fallback for a key nobody anticipated is a generic sentence
standing at the one place a person is trying to make a correction. That is this chapter's own defect
one layer in. Rendering what arrived is total by construction: there is a sentence for every key,
including the keys nobody has thought of.

**The second argument is drift, and it runs the other way from where a reader expects.** A
client-side copy of a server rule does not fail loudly when the rule moves. The API narrows a rule
or adds one, the response is a 400 either way, and the browser goes on rendering the sentence for
the rule that is not the one that fired — telling somebody to do something that will not work, with
nothing red anywhere on either side.

**Rendering a sentence is not branching on one, and this is the place that distinction has to be
made.** The registration chapter forbids choosing its four conflict copies by matching the server's
text, and [payees.md](../business-logic/payees.md) refuses `Detail` as a discriminant for the same
reason — that is what `conflictKind` exists for. Both rules are about using prose as a **decision**.
Here there is no decision: the field key already says where the sentence goes, and the sentence is
the payload. A reader who knows those rules will over-apply them here, and the two acts are not the
same act.

**What this costs, said out loud rather than mitigated away.** The `ValidationException` messages
become UI copy — "Payee name must be unique.", "Category group name must be unique." — and they are
edited by people who do not read this book. So they come under [voice](voice.md) by this rule, and
the gap comes with them: voice's error pattern is *what happened plus what to do*, and those two
sentences carry only the first half. **That is a defect in the API, corrected in the API.** Writing
the better sentence in the browser is how the second definition gets born.

**What the alternative buys is real**: copy under this book's eye, and one string table to
translate. Neither is free the other way either — rendering puts the API's strings under voice
rather than leaving them ungoverned, and a translated client would still have to translate a
sentence it did not write. The trade is a governance cost against a silent-wrongness cost, and this
book takes the audible one everywhere else.

### A field the form does not have

**Every entry in the map is rendered somewhere.** The map is the server's and the form is the
client's, so a key the form cannot place is an ordinary case rather than a corrupt response — a
control behind a branch, a section not on screen, a rule about a member the person never sees. A
dropped entry is this chapter's defect with a better excuse.

- **The lookup from key to control is a `Map`, total over every string, and never an object literal
  indexed by the wire key.** `constructor`, `toString` and `valueOf` are keys that hit on a literal,
  and the entry gets placed under a control that does not exist. The argument is the credential
  list's and is not restated.
- **A miss renders in the region**, one line per entry, in the order the map sends them, `--bud-over`
  — the same treatment as a conflict, because from the person's side it is the same thing: a fact
  about the attempt with no field to correct.
- **A key the form has but is not currently rendering is a miss**, and takes the miss path. The
  question is whether a message can be *placed*, not whether the name is one the form recognises.
- **The key is not printed.** `Name` is a wire member, not a label anybody recognises, and printing
  it puts the shape of the API on screen while explaining nothing. **The cost is real and is the
  reason it is bearable**: two unplaced sentences arrive with nothing to tell them apart, so each has
  to name its own subject — which is one more thing the server's sentence does and a client-authored
  fallback could not.
- **A key whose message array is empty is not a message.** It places nothing, reddens no field, and
  falls to the unreadable-answer sentence. An empty `mat-error` is a red border with no words in it,
  which is colour as the message.

### The typed value survives every refusal

**Nothing clears until an answer says the row exists.** The clear is a consequence of a `201` or a
`204`, never of a press — and a clear written on the line after the write is dispatched runs before
any answer arrives, which empties the form on every outcome including the ones this chapter exists
to render.

- **Nothing navigates either.** A refused write does not close a dialog, collapse the form, reset the
  control it was submitted from, or route anywhere.
- **Focus moves to the first control carrying a message, and only then.** A message bound by
  `aria-describedby` is announced when its control takes focus rather than when it appears, which is
  what [accessibility](accessibility.md)'s *announce on submit* asks for, and the field may be off
  screen besides. Where no control carries a message, focus stays where the press left it: moving a
  keyboard user into a region takes them away from the control they are about to press again.
- **The no-answer sentence may not claim nothing was written.** The request may have arrived,
  committed and lost its response — the registration flow's rule, applied to a row instead of an
  account. The sentence says the server was not reached and says what is safe: the typed value is
  intact. What makes another press safe is the client-minted id, not a promise.

### The region stays polite, and the carve-out is not spent here

[accessibility](accessibility.md) reserves `assertive` for a failed save of data somebody typed, and
this chapter is the case that reads closest to it. It is answered `status` all the same, on two
grounds.

**Assertive is for a failure that arrives after attention has moved on.** Here the press is a second
or two old, the form is still on screen holding everything in it, and the region sits in reading
order where the person is already pointed. Interrupting buys nothing and interrupts nothing.

**And the region is shared.** Raising it to `alert` raises the read's loading line and the locked
notice with it, because there is one region and its politeness is a property of the node rather than
of the sentence. Taking the carve-out therefore means a second region — the duplicate the chapter
above refuses. The carve-out keeps its case and this is not it; a save that completes in the
background, out of sight of the press that started it, is the shape it was written for.

**Colour is never the message, and this is where the chapter says so — once, for both of its
tables.** Every sentence in the states table above and every sentence in *A write that holds no
typed text* below reads the same with `--bud-over` removed, and every field message reads the same
without its border. The rule is stated here rather than beside each table because a rule written
twice is a rule that can be narrowed in one place and left standing in the other.

### What a writer will get wrong

- **Reaching for a snackbar.** It is the first idea, it is written into the code as a suggestion, and
  the Snackbar chapter already refuses it twice over: never for validation, which belongs to the
  field, and never for an error that needs a decision. Four seconds and no stacking is the mechanism
  behind both.
- **Branching on the status code.** It works on three resources and fails on the fourth: `409` means
  *this was a retry* on an account, a category, a category group and a transaction, and means *adopt
  the row that already exists* on a payee create — inside the transaction write, the path a person
  hits most. Read `conflictKind`, which exists because these two answers are otherwise the same
  response. A 409 whose kind is missing or unrecognised is an answer this screen cannot read, and
  takes that sentence rather than a guess.
- **Clearing the form on dispatch.** The clear belongs to the answer, not to the press.
- **Letting the write pipeline swallow.** A refusal that reaches a console and completes with no
  value takes every screen above it out of this chapter, and the screen looks correct while it does.
- **Rendering the problem document.** Not the status, not the `traceId`, not the title — the title
  is one fixed string across every conflict in the product, so it names nothing — and not `Detail`
  either. Both conflict states in the table carry the client's own sentence, so a `Detail` rendered
  beside one puts two accounts of one outcome on screen, breaking the *one outcome* rule the region
  is built on. `Detail` is copy and answers to [voice](voice.md) wherever it is read; these
  screens do not read it, and nothing anywhere matches against it — that is what `conflictKind`
  is for.
- **Assuming a 400 means the map is usable.** It may carry no `errors` member, an empty one, or an
  entry with no message in it, and each of those is the unreadable-answer sentence rather than a
  field turning red with nothing to say.
- **Making the region assertive for this one sentence.** It is one region, so that decision reaches
  the loading line and the locked notice too.
- **Advising a retry on a judgement.** *Try again in a minute* is true of silence and false of a
  refusal, and it is the sentence a writer reaches for because it fits everywhere.

**Every writing surface answers by this chapter, and the two halves closed together.** Each of the
four awaits its outcome and clears the form on `recorded` and on nothing else; each renders a
field-keyed refusal beneath the control the server named, as a `mat-error`, which is the machinery
that binds the sentence to the input with `aria-describedby`; and every other outcome takes one line
in the screen's existing single `role="status"` region. A write answers one of seven words —
`recorded`, `invalid`, `duplicate-name`, `duplicate-identifier`, `unreachable`, `unreadable`,
`locked` — read out of the problem document and never off the status. The sentences live in one
module rather than one copy per writing site, and there are **nine** of those now — these four
forms and the five row acts below, every one of them calling `+shared/write-outcome-report.ts`.
Nine copies drift, nothing anywhere compares two screens' wording, and the day one of them is
edited the product says two different things about one outcome with every test green.
**`locked` has no sentence, and the table's silence about it is the specification**: the screen's
locked notice is already the account of that state, and a second line is the duplicate the region
refuses.

### A write that holds no typed text

**Five writes end in a word and none of them holds a keystroke** — a row's delete on
`/app/accounts`, and a group's move, a group's delete, a category's placement and a category's
delete on `/app/categories`. Each is awaited rather than dispatched, which is *The service has to
have an outcome to report* applied to a press with no form behind it, and each renders one line in
the screen's existing single `role="status"` region — the same region and the same treatment a
form's refusal takes. What they may not take is the copy: **the state table above is written for a
form holding text somebody typed** — *what you typed is still here*, *nothing you typed has been
lost* — and a delete holds none. So `+shared/write-outcome-report.ts` carries two report functions,
split on what the write held: `writeReportOf` for a form, `rowActReportOf` for an act. The copy is
the specification, not an example of it.

| State | Copy | Where it renders |
| --- | --- | --- |
| A delete: nothing answered, or the server failed rather than judged | "Budgetoid couldn’t reach the server. Nothing has been deleted — try again in a minute." | Inside the region, `--bud-over` |
| A delete: an answer this screen cannot read | "Budgetoid couldn’t delete this, and didn’t say why. The row is still here." | Inside the region, `--bud-over` |
| A move: nothing answered, or the server failed rather than judged | "Budgetoid couldn’t reach the server. Nothing has moved — try again in a minute." | Inside the region, `--bud-over` |
| A move: an answer this screen cannot read | "Budgetoid couldn’t move this, and didn’t say why. Everything is where it was." | Inside the region, `--bud-over` |

**Four sentences spelled out, and not two parameterised by a noun.** The two acts differ by one word
in each pair, and a template holding that word is the shape this book refuses everywhere copy is
decided: a sentence assembled from parts is not a sentence anybody reviewed. *The region stays
polite* above states the colour rule for these four along with the other table's, and it is not
restated here.

**The unreadable pair offers neither a retry nor a reload, and the asymmetry against the form's
sentence is the decision rather than an omission.** No retry, for the reason *Advising a retry on a
judgement* gives above: the server judged, and the same press collects the same judgement. And no
*copy it, then reload* — that clause exists so somebody can rescue text a reload would discard, and
there is nothing typed here to spend a reload on. What is left is the truest thing available: the
row the press was made on is still in the list.

**`duplicate-name` and `duplicate-identifier` take the act's unreadable sentence by meaning, not as
a fallback.** Both words are a *create's* answers — one says adopt the row that already exists, the
other says the row you meant is already saved — and neither is something a delete or a move can be
told. A 409 arriving over one of them is an answer this screen genuinely cannot read, which is
exactly what that sentence says. Rendering the form's *This entry is already saved.* here would tell
somebody their deletion was recorded.

**`invalid` sends every keyed sentence to the region.** There is no form, so there is no control a
key could be placed on: every entry takes the miss path the *field the form does not have* section
specifies, one line per entry, in the order the map sends them. On `/app/categories` the write is
filed under a **`null` surface** for the same reason, so neither of that screen's two forms shows a
`mat-error` for a press made on a row. Nothing takes focus either — there is no control the sentence
is about, and moving a keyboard user off the row they are working on is what the focus rule already
refuses.

**`locked` is silent, and on these five it is unreachable.** Nothing here seals, so no key is ever
asked for and the word never arrives. It shares `recorded`'s silence rather than earning a guard of
its own: a screen with nothing to say is what both mean.

**The copy's honesty rests on a property of the code, and the chapter states it rather than assuming
it.** All five services mutate their list inside `tap`, which runs on success alone, so a refusal
reaches its `catchError` with the list untouched. That is what makes *the row is still here* and
*everything is where it was* true rather than reassuring — the difference this chapter faults copy
for elsewhere. Two specs assert that premise directly.

**Three things nothing here holds, recorded because a rule with no gate is how an unenforced rule
survives review.** Nothing pairs *which service call a drag handler makes* with *which act word it
renders* in one assertion, so a handler that calls the right method under the wrong word — a move
reported as a delete — reddens nothing. The report being cleared as one of these writes starts is
asserted on the accounts delete and the category-group delete; the other three carry no such case.
And nothing asserts the loading flag comes back to false after a refused delete or move.

## Settings section and label/value row

- A settings section is a `<section aria-labelledby>` with an `eyebrow` heading,
  `--bud-space-4` between heading and content, `--bud-space-7` between sections, body text
  capped at 65ch. Sections are flat: nothing on the screen has to be opened before a control
  can be reached.
- A label/value row is a `<dl>`, label stacked above value: label `caption`
  `--bud-text-muted`, value `body` `--bud-text`, `--bud-space-1` between them. No border and
  no card — a card groups, and one row is not a group. The value reserves one line box
  (`min-height: 1lh`) so the page does not shift when a value arrives from the network.
- A non-interactive row is not a 48px target. The rule applies to controls, not to text.

### Sign out

The one live control on the Settings screen, and it sits in the **Account** section under the
label/value row — beside who the account belongs to, not under *Ways to sign in*, which is about
what is attached to the account rather than about the browser holding it right now.

- **Outline**, 48px target, visible label `Sign out`. Not Primary: Export is the screen's one main
  action, and a screen with two is a screen with none. Not Destructive either — nothing is lost and
  signing in again restores everything, which is precisely what the erasure control one section down
  cannot say. Conflating the two treatments would spend the Destructive fill on the reversible act.
- **Enabled, with no sentence beside it.** Every other control on this screen is off and explains
  itself; this one is the way out, and a person who cannot leave an account is in a worse position
  than one who cannot register a second passkey. The "not built yet" sentence pattern is for a
  control that refuses a press, and this one does not.
- **It posts, ends the session, and then navigates to `/welcome`** — that order, because the guard on
  the way out reads the session status the moment the router is asked. It shows no busy state and no
  confirmation: the screen it would render one on is replaced within the same tick.
- **A failed request signs the person out anyway.** The route is idempotent and its cookie is
  `HttpOnly`, so this browser cannot read it, clear it, or tell whether it is still live — there is
  nothing to do differently with the knowledge. Leaving somebody stranded on a signed-in screen,
  pressing a button that keeps failing in front of whoever is at the keyboard, is the worse outcome.
  This is the one place on this screen that deliberately does not apply the four-valued reading of a
  request that got no answer, because here silence is not evidence about the visitor: the visitor has
  already said what they want.

## Credential list and row

M3 base: **none** — a plain semantic list, `<ul role="list">` with one `<li>` per entry, for the
reason the transaction row has none. `MatList` makes a 48px interactive row out of content that is
not interactive, and the rule above already says a non-interactive row is not a target. The `role`
is written explicitly because the list carries no bullets, and under `list-style: none` Safari drops
list semantics — without it nobody hears "list, 2 items".

- **Row anatomy.** CSS Grid, mobile-first: one column at phone width, facts then action below at
  `justify-self: start`; from `min-width: 600px`, `[facts 1fr] [action auto]` with the action centred
  on the cross axis. `--bud-space-3` gap and vertical padding, `1px solid var(--bud-hairline)` on
  every row after the first. No card and no border box — the section already groups them, and a card
  never wraps a single list.
- **Facts**, stacked with `--bud-space-1`: the type in words as `body` `--bud-text` — every kind the
  union carries has a word of its own, none of them is the token the wire uses, and a kind the union
  does **not** carry has a word of its own as well (below); then the dated caption as `caption`
  `--bud-text-muted`, wrapped in a `<time>` whose `datetime` carries the stored instant. The
  caption's leading word belongs to the kind too — `Registered` for a thing that was attached,
  `Generated` for a set that was issued, `Added` for a kind this bundle cannot name — and everything
  after that word is one shared formatter.
- **The date is absolute, in the reader's locale, and is the reader's own calendar day** computed
  from the stored instant in their zone — never the UTC day. Relative words ("Today") belong to
  transaction lists; a record of what is attached to an account states a date. `DatePipe` is not
  available for this: nothing provides `LOCALE_ID`, so it would silently pin every date to `en-US`.
- **Nothing else is shown.** No device name, no nickname, no last-used instant, no identifier, no
  provider subject. The row shows what the server holds and what tells one entry from another;
  anything more is a device fingerprint or an identifier with no reason to be on screen. Two passkeys
  registered on the same day are told apart by nothing better, and that is the honest maximum — the
  ceremony requests `attestation: "none"` precisely so registration collects no device identity.
- **A date the client cannot read is not shown at all.** The row states the type and drops the dated
  caption line entirely, its leading word with it — no placeholder, no raw stored value, no
  `Invalid Date`, and no
  `<time>` element, since that element's whole contract is a machine-readable instant. The accessible
  name of Revoke drops the clause with it (`Revoke Passkey`), rather than ending mid-sentence at
  `registered `. Two entries that cannot be told apart is a state the row is already honest about for
  two passkeys registered on one day. This is not a hypothetical, and it is the position the kind is
  in as well: a row is composed inside a `computed` the template reads, so anything that throws while
  composing one abandons the change detection pass, and Angular caches that failure on the signal and
  rethrows it on every later read — the list stays on its loading line and every section below this
  one stops updating for the rest of the visit. No `try` can be placed around a signal read. So every
  fact a row is composed from is **total over what a 200 can carry**: an instant the client cannot
  read has an answer, and so does a type it has never heard of.
- **Action.** One Revoke button per **revocable** row — a passkey — Outline, 48px target. Visible
  label `Revoke`; accessible name `Revoke <type>, <the row's own caption word, lower-cased> <date>`,
  which on the one revocable kind there is today reads `Revoke Passkey, registered 2 February 2026`
  for a reader whose locale renders the day that way.
  It begins with the visible label so voice control still reaches it, and it is composed because two
  buttons named "Revoke" cannot be told apart. The clause word comes from the row rather than being
  written into the name: a hard-coded `registered` reads correctly only while every revocable kind
  happens to be captioned `Registered`, and the day one is not, a screen reader would describe the
  control by a word the sighted reader is not looking at.
- **A row nothing can ever revoke carries no Revoke, not even a disabled one.** Every other disabled
  control on this screen is a promise: the ceremony lands, the sentence above the list goes, and the
  button starts working. A control that will never be enabled makes the same promise and cannot keep
  it, which is the worse of the two lies — the reader waits for a release that is not coming. A
  recovery-code set is unrevocable by construction (below), and the **Google** row is unrevocable
  because the federated credential is replaced by an email change rather than removed; neither row
  draws the button. A row whose kind this bundle does not recognise draws none either, on a different
  argument: unknown is undecided, revocation is the one unrecoverable act on this screen, and the
  undecided answer to an unrecoverable act is no control at all. Revocability is a property carried
  **per kind**, beside that kind's word and caption, so a kind added without one fails to compile
  rather than inheriting an action by default — the half of a new kind that cannot be taken back once
  it is on screen.
- **A recovery-code set is a row of this list**, with the type in words as **Recovery codes**. The
  list is documented as every way into the account, and redeeming a code opens a full session, so a
  set belongs here on the same argument a passkey does. It carries **no action**: pointing a
  revocation at it answers the 404 an unknown id answers, because the lookup behind that route is
  scoped to passkeys by type, and a set is *replaced* by generating again rather than removed. Its
  place in the list is wherever its instant puts it — the server sends the list ascending by that
  instant and the client never reorders.
- **Its caption reads `Generated <date>`, not `Registered <date>`.** The instant is the generation
  instant, and it moves every time the set is replaced, so the word a passkey's row uses would say
  the wrong thing about a set on its second issue. Everything else about the caption is identical:
  the same `<time>` carrying the stored instant, the same reader's-own-calendar-day rule, the same
  drop-the-whole-line behaviour when the stored value cannot be read. The formatter is shared —
  `credential-registration-date.ts` computes the day and the leading word is the only difference —
  because a second date path is a second place for a UTC day to leak back in.
- **The wire value for the type is `recovery_codes`**, the schema's own token, and the response
  spells it that way rather than camel-casing a property name: `recoveryCodes` is what a naming
  policy would produce and it agrees with nothing. `CredentialKind` is a **closed** union for this
  reason — a member added to it is a change to what this list renders, caught at compile time. What
  catches it is that the word, the caption and the revocability are held together in one map declared
  exhaustive over the union: a new kind fails the build until someone decides all three. A `switch`
  with a fallback would render the new kind as whatever the fallback said and hand it an action
  nobody chose for it.
- **That closure is a guarantee about this source, and it says nothing about the value that
  arrives.** A declared response type is an assertion about JSON, not a check of it, and the ordinary
  way a type nobody here has named reaches this list is a browser holding yesterday's bundle against
  today's API — which is exactly the string arriving one day and falling through a template. So the
  list needs both halves and neither substitutes for the other: the union holds the source, and the
  lookup from a wire value to its word, caption and revocability is **total over every string**.
- **A kind this bundle does not recognise is a row like any other, and it is dated.** It reads
  **Sign-in method** as its type, its caption is **Added**, and it carries **no action**. Dropping
  such a row is the worse lie: this list is documented as *every* way into the account, so filtering
  one out tells somebody auditing their credentials that a way in they cannot see does not exist, and
  leaves them nothing to act on. `Registered` and `Generated` are precisely the two claims the row
  cannot make — one says the thing was attached, the other says it was issued — so the caption says
  only that it is there, and the date beside it is the one fact the server sent that needs no
  vocabulary to read.
- **The lookup is a `Map`, never the map object indexed by the wire string.** An object literal
  inherits from `Object.prototype`, so `constructor`, `toString` and `valueOf` are keys that *hit*: a
  `?? fallback` written over the literal never fires for them, the row is handed an object with no
  word in it, and the type renders **blank** with no error raised anywhere. A `Map` built from the
  literal's entries holds its own keys and nothing else, so a miss is a miss for every string that is
  not one of them. The defect is in the lookup, not in the fallback — a `??` cannot fix it.
- **Loading** is one line in the section's `role="status"` region. **No skeleton** — that spec is for
  a screen's own subject, and a skeleton for a list this short inside one settings section costs more
  than it explains.
- **Failure** is one sentence in the same region, in `--bud-over`. Colour is never the message.
- **Empty** replaces the list with one plain line, `body` `--bud-text` — not the centred empty-state
  block, which is for a screen's subject, and the section's own action already sits beneath.
- **The empty line renders outside the region, with the list it replaces**, and the recovery-codes
  section below makes the opposite call about its own count deliberately. The two answers this
  section can give are *a list* and *that line*, and a list cannot go in a live region — a
  `role="status"` that gained a `role="list"` and n rows would narrate every entry as an event. Put
  the empty half in on its own and the region speaks only when the answer is *nothing*, which is the
  one answer a reader would then be sure they had heard. Both answers stay in content, in reading
  order. The cost is stated rather than hidden: somebody present while the list loads hears that it
  started and is not told how it ended. Where the whole answer is one sentence, as it is for the
  count next door, that cost is not worth paying and the sentence goes inside.

**What ships today.** The list renders every kind the union carries, the recovery-code set's row
included, and renders one it does not as **Sign-in method**, captioned **Added** and carrying no
action. **Register a passkey** (below the list) and **Revoke** (on the revocable rows, which is the
passkeys) are both Outline and **disabled**, and the section carries two sentences and not one,
because the recovery-codes section below no longer shares this one's words. Across the screen's
four inert controls there are **three reasons said in four sentences** — Revoke and Erase share a
reason and word it differently, one naming a row's buttons and the other the screen's — so the
count of sentences is never the count of reasons here. The browser's ability to run a ceremony is
not what any of them waits on: this client creates a passkey on `/register` and asserts one on
`/welcome`.

**The four sentences and the Account keys section shipped together**, and that was a scheduling
rule rather than a preference: two of them name unlocking, and a screen carrying this wording with
no Unlock on it would be naming a capability the reader cannot find — the same defect as the wording
it replaced, one step removed. The rule outlives the commit, because the next narrowing of any of
the four will be earned by a capability in exactly the same way.

- **Register a passkey** waits on the account's keys **as bytes**, and on nothing else. A passkey
  is a *factor*, every factor stores its own wrapped copy of the account's content key and index
  key, and wrapping them takes the keys themselves rather than the ability to use them:

  > A new passkey needs its own copy of your account’s keys, and unlocking lets this browser use
  > those keys without ever getting hold of them. The button stays off until that copy can be made.

  **The clause naming what is missing has been narrowed twice, each time by a capability that
  landed.** It first said Budgetoid could not unlock the keys in the browser, which stopped being
  true when `GET /api/me/account-keys` and `AccountKeyCustodyService` arrived — a person reading it
  was being told their browser could not do what it had just done one screen earlier. It then said
  the copy takes *a passkey this screen doesn't ask for*, and that stops being true the moment the
  Account keys section puts an **Unlock** on this screen. Left standing, the same sentence would
  have committed the same defect twice, against a control the reader had just used.

  **What is left is the part no ceremony on this screen closes.** `unlock` takes the
  key-encryption key as an **argument** and hands it to custody, which keeps what it opened as two
  non-extractable `CryptoKey` objects behind no accessor. There is no route from anything the tab
  is holding back to bytes: bytes come only from unwrapping again under a key-encryption key
  **held long enough to wrap with**, and holding one that long is precisely what the unlock path
  refuses to do — the key travels as an argument through one statement and is never named on a
  field. So the sentence stops claiming a ceremony is missing and says what the ceremony does not
  give.

  **The sentence is no longer shared with the recovery-codes section, and the split is the
  specification.** The two were word for word while one fact held both controls off. It no longer
  does: registering a passkey waits on the bytes alone, while generating a set waits on the bytes
  **and** on a passkey assertion the *server* checks — which the unlock ceremony deliberately is
  not, its assertion being minted locally and discarded. Three missing pieces across four
  sentences: the bytes here, the bytes and a checked assertion under Recovery codes, a checked
  assertion under Revoke and again under Erase — those last two sharing the piece and not the
  wording. Pasting any one over another puts a sentence on the screen that
  is true of a different control. The quotes and their phrase constants in
  `settings.component.spec.ts` move with the template in one commit, and there are now two
  constants where one served both sites.

- **Revoke** waits on an assertion the server checks. `POST /api/me/credentials/{id}/revocation` is
  live and the fresh assertion that authorizes it is a ceremony this client runs; what this screen
  runs is not it. **That is the erasure section's position, and the two share the reason without
  sharing the wording** — this one names revoking and speaks of the rows' buttons in the plural,
  the erasure one names erasing and speaks of a single button. Two strings for one reason, which is
  how three reasons come to be said in four sentences:

  > Revoking has to be confirmed with a passkey Budgetoid checks itself, and this screen doesn’t
  > ask for one yet. Those buttons stay off until it does.

  **The qualifier is new and it is not decoration.** Without it the sentence says this screen asks
  for no passkey at all, which the Account keys section makes false — somebody who has just watched
  their authenticator answer an **Unlock** would read, two sections down, that this screen cannot
  ask for what it asked for a moment ago. The erasure section makes the same claim in its own words
  and takes the same qualifier in the same commit, or the two come apart.

Both sentences sit **above** the list rather than beside each button: per row, a screen reader would
read the same explanation once per entry. The account-keys one is also **above the list** and not
merely above the Register control it explains, because the first inert control a reader meets in this
section is a row's Revoke — below the rows the sentence is an apology, above them an instruction.
When each block clears, its own sentence goes and its own control becomes live; neither release
carries the other. Revoke keeps its Outline until a confirmation exists for it, per the
destructive-action rule above.

## Recovery codes section

The account's other way back in, stated as a number. M3 base: **none** — two lines of prose, one line
of `body` carrying the count, and one button. Not a card, not a list, not a meter: one number about
one thing is a sentence, and every component that would wrap it exists to group things there is more
than one of.

It sits on `/app/settings` **between Ways to sign in and Account keys**. Everything that is neither
Export nor Erase arrives above them both: those two are a pair — the alternative offered beside the
destructive act, per [patterns](patterns.md) — and nothing goes between them.

- **Anatomy.** A settings section per the spec above: `<section aria-labelledby>`, `eyebrow` heading
  **Recovery codes**, `--bud-space-4` between heading and content, `--bud-space-7` to the next
  section, prose capped at 65ch. One grid column, `justify-items: start`, `--bud-space-4` gap — at
  every width, and that is a decision rather than an omission. The section holds one control and no
  rows, so there is nothing for a second column to carry and no breakpoint changes it; the row's
  600px split in the credential list exists because a row has facts *and* an action, and this
  section's control belongs to the section rather than to any line above it.
- **`--bud-space-4` is the gap between the section's own children** — heading, region, prose,
  button. The count is not one of them: it sits inside the `role="status"` region, and that region
  carries the screen's outcome gap, `--bud-space-2`. So a sentence and the count beneath it are set
  closer to each other than to anything else in the section, which is what makes them read as one
  outcome rather than as two things the section listed. Token to token, both of them; neither is a
  literal.
- **The count is one line of `body` `--bud-text`**, the last child of the region, reserving one line
  box (`min-height: 1lh`) so the page does not shift when the number arrives from the network — the
  label/value rule above, applied to a value whose label is the section heading.
- **The section shows no code and no part of one.** Not a code, not a verifier, not a hash, not the
  set's identifier, and not the day it was generated. The count is the whole of what this section
  says; the generation day is on the set's row in Ways to sign in, and a second copy of it here is a
  second thing to keep in step.

### The six states

They are six, and no two of them are interchangeable. The copy is the specification, not an example
of it.

| State | Copy | Where it renders |
| --- | --- | --- |
| At rest | *nothing* | The `role="status"` region carries no sentence; the count line inside it is blank and holds its box |
| Loading | "Loading your recovery codes…" | Inside the region, `body` `--bud-text` |
| Failed | "Couldn’t load your recovery codes. Reload the page." | Inside the region, `--bud-over` |
| `0` remaining | "You have no recovery codes." | Inside the region, as its last child |
| `1` remaining | "You have 1 recovery code left." | Inside the region, as its last child |
| `n` remaining | "You have 5 recovery codes left." | Inside the region, as its last child |

**At rest and `0` are different states and must never collapse into one.** A blank line box says the
answer has not arrived; "You have no recovery codes." is a fact about the account. Flattening the
unanswered case to a zero is the defect this row exists to name — the count is held as *no answer
yet* until one arrives, exactly as the credential list holds `null` apart from `[]`.

**At rest and loading are also different states, so loading is published rather than inferred from the
missing count.** Both are *no number yet*, and so is a load that **failed**: the count is absent in
all three, so a section deriving its loading line from that absence has one predicate for three
states, and it would go on saying *loading* to somebody whose request has already given up. Published,
the in-flight state is set when the read starts and cleared however the read ends, which is what makes
the exclusivity below a matter of structure rather than of which template branch happens to be written
first. It is the shape the Export outcome on this screen already uses. At rest is the section before
any read has begun; the read begins as the screen initialises, so on a visit the region is already
carrying its loading line at the first paint.

**Loading and failure are exclusive by structure**, not by coincidence: a load that failed is not
still loading, and rendering both would ask the reader to keep waiting on a request that has already
given up. Colour is never the message — the failure sentence reads the same with `--bud-over`
removed.

**Every state but at rest lives in one unconditional `role="status"` region** — loading, failure and
the count alike. The region is in the DOM from first paint and gains its text later; a live region
created at the moment it gains content is announced unreliably, because assistive technology has to
have been watching the node before the text landed. It is `status` rather than `alert` for the reason
the export outcome is: polite is right for the result of a read the screen started on its own, and
assertive is reserved for a failure to save something the person typed.

**The count is inside that region, as its last child.** The read starts as the screen initialises, in
the same turn and before the first update pass, so the region is holding *"Loading your recovery
codes…"* from first paint rather than gaining it — and a live region that already has text when
assistive technology registers it is announced unreliably, while text **removed** from one is
announced by nothing at all. With the count outside, a reader who was present while the section
loaded therefore heard that something had started and never learned how it ended, on the one section
whose whole job is saying whether there is a way back into the account. Announcing a beginning with no
end is worse than either half. Inside, the count is the last thing the region says in every outcome —
the answer to what was announced as pending — and a reader arriving after the response meets it
unchanged, in normal reading order and in the same place on the page.

**The credential list keeps its own standing fact outside its region, and the difference is
deliberate rather than an inconsistency.** That section can answer with *a list* or with *"Nothing is
attached to your account yet."*, and a list cannot go in a live region — a `role="status"` that gained
a `role="list"` and n rows would narrate every entry as an event — so putting only the empty half in
would leave a region that speaks when the answer is *nothing* and stays silent when it is not. Here
the whole answer is one sentence, every outcome is announced the same way, and the section pays no
such price for it.

### The `0` sentence is true both ways

**`0` covers "never generated" and "all spent", and the client cannot tell them apart.**
`GET /api/me/recovery-codes` answers `{"remaining": 0}` for both — deliberately, since an account
with no set has zero codes rather than a missing resource, and a redeemed code is consumed by
deleting its row, so nothing anywhere records that a set once existed. See
[recovery-codes.md](../business-logic/recovery-codes.md).

That is a **copy constraint**, not an implementation note. "No recovery codes left" is the sentence a
writer reaches for and it is false half the time: *left* presupposes a set that once existed, and it
tells somebody who has never generated one that they have spent something. The zero branch therefore
drops the word the other two branches carry, and says only what is true of both: **"You have no
recovery codes."** The two readings share a next step — generate a set — so nothing downstream needs
the distinction either.

### Pluralisation is three template branches, never a pipe

`1` and `n` are separate branches with separate strings, and `0` is a third. **`I18nPluralPipe` is
not available for this**: nothing in this app provides `LOCALE_ID`, so the pipe would silently pin
every count to `en-US` plural rules — the same trap `credential-registration-date.ts` exists to avoid
for dates, where `DatePipe` would have pinned every day to an `en-US` format. A pipe that is wrong
only in locales nobody on the team reads is worse than three lines of template, because nothing on
the screen shows that it went wrong.

The number is a numeral in every branch, per [voice](voice.md). Ten is what a set holds, so `n` is
bounded at 10 today; the branch is written for any `n` regardless, because that bound is product
policy in the API rather than a fact the client is told.

### The Generate control

**Present and disabled**, with visible prose above it saying why — the shape the erasure control and
the credential registration and revocation controls already use on this screen.

- **Outline, disabled**, `min-height: var(--bud-touch-target)`. Outline rather than Destructive even
  though generating **replaces** an existing set and invalidates every code printed from it: the
  Destructive fill is a promise that a confirmation follows, and no confirmation exists behind this
  control. Plain `disabled`, not `disabledInteractive` — the carve-out for a *busy* control that will
  come back within the second does not apply to one that is unavailable for the whole life of the
  screen, and keeping it focusable would offer the keyboard a stop that answers every press with
  silence.
- **Visible label** `Generate recovery codes`. No composed accessible name: it is the only Generate
  on the screen, so there is nothing to tell it apart from. No count in the label.
- **The sentence sits above the button as visible prose** — never a `title`, a tooltip, or an
  `aria-describedby` on the disabled element, all of which are read to nobody once the control has
  left the tab order. It is **this section's own**, and no longer the credential list's:

  > Ten new codes each need their own copy of your account’s keys, and replacing a set also has to
  > be confirmed with a passkey Budgetoid checks itself — not the one unlocking asks for, which
  > never leaves this device. The button stays off until this screen asks for both.

  **Two facts hold this control off and only one of them holds its neighbour off, which is why the
  shared sentence had to end.** The first is common ground and is argued once in the credential-list
  chapter above rather than twice: generating a set is ten factors at once — each code derives its
  own key-encryption key — so it waits on the account's content key and index key **as bytes**
  exactly as registering a passkey does, and the Account keys section below does not supply them.
  The second belongs to this route alone: it is gated on a fresh assertion the **server** verifies,
  and the unlock ceremony's assertion is minted in the browser and discarded, so a person can run
  Unlock all afternoon without moving this control one step. One sentence covering both sites would
  have to drop that clause, and the reader would be told two controls wait on the same thing when
  one waits on strictly more.

  **A third fact holds it too and is deliberately not in the copy.** Replacing a set moves the
  account's factor set on a generation, so the request carries a manifest re-sealed at the stored
  epoch plus one — a *promotion*, which no path in this client has ever performed; registration
  files the first manifest and sends no epoch. It stays out of the sentence because it names nothing
  a person can do, recognise or wait for, and the copy rule here is that a disabled control explains
  itself in terms of what the reader is owed rather than what the client has left to build.

  **The clause naming the second fact is what keeps the sentence true in front of the reader.** It
  does not say the browser cannot run a passkey check — `/register` creates a credential and
  `/welcome` asserts one — and, with the Account keys section immediately below, it may no longer
  say this screen asks for no passkey either. It asks for one, in plain sight, a few lines further
  down the page. What it says instead is which *kind* is missing, which is a fact about the route
  rather than about the device.

**What replaces this when generation lands**, so that nobody "completes" the section early: the
sentence above goes, the button becomes live, and two things arrive with it that are deliberately
absent today. First, the codes themselves have to be shown once and only once, with copy saying the
browser minted them and that Budgetoid never receives one — a claim that would be describing a
capability the client does not have if it shipped now. Second, the sequencing warning: a set can only
be generated while the account still holds a working passkey, so codes protect only the person who
generated them beforehand. That sentence is confusing beside a control nobody can press and honest
beside one they can. **It is about *this* path and not about every path**: registration issues a set
as part of creating the account, in the same act as the first passkey, so the account is never
without one — read the sequencing rule as "a *replacement* set needs a working passkey", which is
what makes it a warning rather than a description of how sets come to exist. Redeeming a code has no surface at all — there is no route to reach the anonymous
redemption from — and it is a separate screen, not part of this section.

### Accessibility

Heading level `h2` under the screen's one `h1`; no level skipped. The count line is plain text and
not a target — a non-interactive line is not 48px. The region is `role="status"`, polite, and never
`assertive`. Nothing here is communicated by colour alone.

### What ships today

**The reading half of this chapter is on the screen; the generating half is not.** The section is on
`/app/settings` in its specified place, `GET /api/me/recovery-codes` is called when the screen opens,
and the count, every state in the table above and the `role="status"` region behind them all render as
written.

**Generate recovery codes** is present and disabled with its sentence above it. That is the specified
state, not an unfinished one, and *What replaces this when generation lands* above is the whole of what
arrives with the button — nothing here asks for it to be made live on its own. The client holds the
generator that mints a code and derives its verifier from that code's canonical form, and the
registration flow is its one caller; nothing on **this** screen calls it, and the API service has no
member that posts a set. **Neither the route, nor the client's ability to run a ceremony, nor any
piece of the cryptography is the obstacle.** That route takes eight members — five of a fresh
WebAuthn assertion, which this client plainly produces; ten whole code submissions, each carrying
its own factor keypair; a manifest naming the factor set the request leaves behind; and the rotation
epoch that manifest was sealed under. Seven of the eight have a live implementation and a live
caller on the registration path. **The eighth is the epoch, and it is the one member registration
cannot stand in for** — that path files a first manifest and sends no epoch at all, so no code in
this client has ever computed the number this route wants, which is the stored generation plus one.
It is the third wait below, named here as a member rather than left to be inferred from a count.

**What this section waits on is three things, and the Account keys section below closes none of
them.** The first is the account's keys **as bytes**: `GET /api/me/account-keys` hands the envelopes
back and the browser opens them, but what comes out is held as non-extractable key objects behind no
accessor, and an encapsulation takes bytes. Reaching them means opening a factor again under a
key-encryption key **held long enough to encapsulate with**, which is exactly what the unlock path
refuses to do — it takes the key as an argument, hands what it opened to custody in one statement
and keeps no name for either. The second
is a passkey assertion the **server** verifies. Unlock's is minted locally and thrown away, so it
is not that assertion and cannot become it; that is a different ceremony, with a server's challenge
behind it, and this control waits on it exactly as Erase does. The third is a manifest
**promotion** — this route moves a generation, so it carries the stored epoch plus one and a
manifest re-sealed at that number, and nothing in this client has ever promoted one: registration
files the first at epoch 1 and sends no epoch at all. Redeeming a code has no surface in
the app at all.

**The show-once surface exists and this section is not where it lives.** It is the last step of the
registration flow below, and that flow drives it: somebody creating an account is shown ten codes
once, and posts the account from that step. Nothing above changes: this section still shows no code
and no part of one, and *What replaces this when generation lands* still describes what arrives
**here** when the Settings path opens.

## Account keys section

The way back into a locked account, and the one section on this screen that asks the person's own
device for anything. M3 base: **none** — two lines of prose, one `role="status"` region and one
button, for the reason the recovery-codes section beside it has none: one act about one thing is a
sentence, and every component that would wrap it exists to group things there is more than one of.

It sits on `/app/settings` **between Recovery codes and Export**, on the placement rule the
recovery-codes chapter above already argues and which is not restated here: Export and Erase are a
pair, so nothing goes between them and everything else arrives above them.

**A locked account is a browser holding no content key, and every tab starts in one.** Nothing
about the account's keys survives a page load — a decision
[account-keys.md](../business-logic/account-keys.md) argues at length, not an omission. It is
**not** a locked *session*, which is a different word for a different thing: somebody on a full
session whose tab was reloaded is signed in and their account is locked, which is the ordinary case
rather than a corner. Everybody who reads this section is signed in already. What they are missing
is a key, not a session, and no sentence in it may suggest otherwise.

**This chapter specifies the act and not the state.** What a locked account *looks* like — which
screens may draw budget content before the keys are held, and what stands in its place — belongs to
the surfaces that draw that content, and it is specified in
[the locked account](#the-locked-account) below. This section is what those surfaces point at: each
of them renders a notice naming **Unlock in Settings**, and this control is the one it means.

### Why it is a section, and not a route or a banner

**`/app/unlock` was rejected because it is an address anybody can open.** A route is reachable from
the URL bar on an account that is already unlocked, so the chapter specifying it would have to say
what an unlock screen shows when there is nothing left to unlock — a second design, for a state
nobody navigates to on purpose, ending in a screen about nothing. A section inside a screen has no
such problem: it renders what is true where the person already is, and its control is simply not
drawn when there is nothing for it to do.

**A permanent shell banner is rejected too, and the consequence behind a locked account is what
refuses it rather than what argues for it.** Every narrative screen seals what it writes, so a
locked tab reads no name back — and **the three screens that draw budget content say so
themselves**, in place of the list the person came for, per
[the locked account](#the-locked-account). A bar above them would repeat that sentence on every
cold load, over screens already carrying it and over the ones with nothing to be locked out of.
Where the notice replaces the content it is about, a banner sits above content it is not about —
and a standing warning is how a reader is taught to stop reading them.

### What unlocking is for

A person's records are encrypted, and unlocking is the difference between a screen they can read and
one they cannot. The section says so plainly, in the register the **What we can read** section
already uses on this screen — as a fact about the system, with no apology around it:

> Your passkey holds the keys your records are encrypted with. Budgetoid never sees them, and this
> browser forgets them every time the page reloads.
>
> Unlocking is what lets this tab read the names and notes on your accounts, categories and
> transactions.

**Both sentences are standing prose, so both have to be true in all three states** — and that is
what keeps the second from becoming the line the Anatomy below forbids. *Until you unlock, this tab
can’t read…* is the sentence a writer reaches for, and it is a statement that the account is
locked: said in prose, beside a control that says it already, and left standing on an account whose
keys are held. Written as what unlocking *does*, the same fact reads true while the account is open,
while a ceremony is running, and before one has been asked for.

**Neither sentence may promise more than the account's keys open.** What they open is the narrative
— the names and notes on accounts, payees, categories and transactions — and every amount, date and
figure on those screens is readable whatever this tab is holding. Copy saying that unlocking reveals
*the record* would send the reader looking for whatever it had revealed. **The rule runs in both
directions**, and the second is the easier one to write by accident: copy saying unlocking changes
nothing anybody can see is false the moment a screen seals a name. A sentence about unlocking is
measured against exactly what the two keys open — no wider and no narrower.

### Anatomy

- A settings section per the spec above: `<section aria-labelledby>`, `eyebrow` heading **Account
  keys**, `--bud-space-4` between heading and content, `--bud-space-7` to the next section, prose
  capped at 65ch. One grid column, `justify-items: start`, `--bud-space-4` gap at every width — the
  recovery-codes section's argument for a single column applies unchanged: one control, no rows,
  and nothing for a second column to carry.
- **One `role="status"` region, in the DOM from first paint and empty at rest**, carrying every
  line this section says: both waits, all ten refusals, and the line saying the keys are held.
  Why a region has to exist before it has content is argued in *A value read from the network*
  above and again in the recovery-codes chapter, and is not argued a third time here. `status` and
  never `alert`: the person asked for this, and assertive is reserved for a failure to save
  something they typed.
- **The line saying the keys are held is the region's last child and reserves one line box**
  (`min-height: 1lh`), so nothing below it moves when an answer arrives — the label/value rule
  applied to a value whose label is the section heading.
- **There is no line saying the account is locked.** The Unlock control being on the screen is that
  statement, and a sentence beside it would say the same thing twice: once in prose a reader has to
  parse, once in a control they can press.

### The Unlock control

- **Outline** (`mat-stroked-button`), 48px target, visible label **Unlock**. Not Primary, and the
  near miss is worth stating because a reader will propose it: a Primary *while locked* reads as
  the obvious move, and the state has a real consequence behind it, which makes the proposal a
  serious one. It is refused twice, and each reason stands on its own. Export is this screen's
  one main action, and a screen with two is a screen with none; and to anybody not tracking lock
  state — which is everybody, since **nothing on this screen but this section** is drawn differently
  when it flips, everything that changes being on three other screens — a Primary that comes and
  goes is just two Primary buttons on one screen. **The consequence is not a third reason, and it is
  not an argument for the Primary either** — a locked tab reads no name back, which makes a Primary
  here *honest* rather than right, and the two reasons above refuse an honest promise exactly as
  they refuse any other.
- **Not Destructive.** Nothing is lost either way, and the Destructive fill is a promise that a
  confirmation follows.
- **No composed accessible name.** It is the only Unlock on the screen, so there is nothing to tell
  it apart from — the credential list composes its Revoke names precisely because there is one per
  row.
- **No sentence beside it saying what it waits on**, because it waits on nothing. Every other
  control in this half of the screen is off and explains itself; this one is live, and the "not
  built yet" pattern is for a control that refuses a press.
- **While either half is running it takes `disabledInteractive` and `aria-busy="true"`** — the
  Export control's treatment and the Export control's reason: a button that goes truly `disabled`
  under the finger drops focus to `<body>`, and somebody who pressed Unlock from the keyboard loses
  their place in the document at the moment the outcome is announced. `aria-busy` resolves to
  `null` at rest rather than to `'false'`, so the attribute is absent instead of asserting that no
  work is happening.
- **The gate is in the flow as well as in the attribute, and the flow's has to be at least as wide
  as the attribute it backstops.** Material's click-halt is applied to anchors only, so on a
  `<button>` the DOM `disabled` property stays `false` and a second press arrives whatever the
  attribute says; the handler is the only thing that can refuse it. It was once the narrower of the
  two, and the gap was a defect rather than a theoretical one: the attribute was bound to a reading
  the template assembled for itself out of both in-flight states, while the handler guarded on the
  ceremony's flag alone, so every press made during the account-key read was a press the screen had
  drawn as impossible. It raised a second system sheet over an unlock already finished and made
  custody discard the read the first press was about to complete — the account closing by a button
  that looked disabled.
- **So the attribute and the guard read one predicate with one owner.** `AccountUnlockService`
  publishes "either half of an attempt is running" as a computed; the control's `disabled` and
  `aria-busy` bind it and the handler guards on it. Two spellings of one fact drift, and the drift
  is silent in both directions — a template that narrows draws a live control over an attempt
  already running, and a handler that narrows accepts the press behind it. It is the rule
  `apiCredentialsInterceptor` keeps about "is this our API?": one definition, and the second reader
  imports it rather than restating it.
- **It is not rendered at all once the keys are held.** That is the next rule, not a tidy-up.

### The control leaves when there is nothing to unlock

**A press on an already-unlocked account can only make things worse, so it is never offered.**
Custody drops both keys the instant `unlock` starts — its own invariant is that "status is not
`unlocked`" implies "this instance holds no key" at every moment, which is what stops it reporting
a failure while still holding what it failed to replace. The consequence here is blunt: a press
made on an open account and then refused at any point after the key was handed over leaves the
account **locked**, having gained nothing. A control whose best outcome is no change and whose
ordinary failure is a loss is not a control.

**The refusal path is held separately, and the two rules cover different halves.** Not drawing the
control closes the door on this screen. What holds the flow is that **a refused unlock leaves
custody exactly as it found it**: a ceremony that produced no key never calls `unlock`, so nothing
was dropped, and no failure branch may call `lock()` to tidy up after itself. That is not a hazard
invented in advance to be guarded against. The flow is a class rather than a template branch, so
the moment it is reached from a surface that does offer the control beside an open account — or in
any window where a render has not caught up with custody — a `lock()` in the cancellation branch
discards both keys, silently, with every pixel on the screen looking correct.

### The three blocks, and the thirteen lines inside them

**Which block renders is `custody.status()`'s answer and only its.** Three values, three blocks,
and nothing else is consulted to choose between them:

- `unlocked` → the line saying the keys are held, and **no control**.
- `unlocking` → *Opening your account…*, and the control held busy.
- `locked` → the control, and at most one sentence.

**The custody branch is the outer one and the flow's in-flight reading is nested inside it, and
that shape is the rule rather than a style.** The two in-flight states overlap on purpose: the flow
hands custody the key *before* it clears its own busy flag, so that no frame exists in which both
are false and the section flashes back to its resting state with a second press available.
Something has to break that tie. The nesting breaks it in the one direction that reads true —
`unlocking` is answered at the top level, and motion is consulted only after custody has said
`locked`, where it can mean the ceremony and nothing else because custody is not reading there by
construction. Once the key has been handed over the ceremony is finished, so the truer sentence is
about the read that is running now rather than about the device that has already answered.

**Flattening the nesting into two sibling tests is the mistake worth naming, because it looks like
a simplification.** Side by side, the two in-flight readings have to be ordered by hand, and the
order a reader reaches for first — motion, then custody — tells somebody watching a network request
that their passkey is still being waited on. The thing that sentence asks them to do is touch a
sensor, and it cannot help: nothing is asking them for anything. Nested, that ordering is not a
thing anyone has to remember.

**Two in-flight sentences and not one flag**, because they are two different moments and a person
can act on the difference. *Waiting for your passkey.* is the system sheet — the thing to do is
touch a sensor or pick a key up off the desk. *Opening your account…* is a request — the thing to
do is wait, and the thing that can go wrong is the network.

**One signal drives the busy *treatment*, and it says less than either sentence does.** That
signal is `AccountUnlockService`'s `working` — true while the ceremony is up and true while custody
is reading — and the control's `disabled`, its `aria-busy` and the handler's guard are the three
things bound to it, per the gate rule above. It is a coarser reading laid over the two moments and
never a merge of them, and the nesting is what keeps it from becoming one: which *block* renders is
decided there, and inside the `locked` block this same signal is what chooses the waiting sentence
— where it can only mean the ceremony's half, because custody is not reading. So one predicate
decides whether the control is pressable everywhere, and names a moment only in the one place there
is a single moment it could name. A screen that assembles that predicate for itself instead of
reading it is the drift the gate rule names.

| State | Copy | Where it renders |
| --- | --- | --- |
| Locked, at rest | *nothing* | The region carries no sentence; the Unlock control is what says the account is locked |
| Waiting for the device | "Waiting for your passkey." | Inside the region, `body` `--bud-text` |
| Opening | "Opening your account…" | Inside the region, `body` `--bud-text` |
| Keys held | "Your account is unlocked in this tab." | Inside the region, as its last child; no control is drawn |
| `unsupported` | "This browser can’t check a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can." | Inside the region, `--bud-over` |
| `cancelled` | "The passkey check was cancelled. Nothing has changed — try again whenever you’re ready." | Inside the region, `--bud-over` |
| `no-prf` | "This device can’t open your account’s keys. Try the device that holds the passkey you made this account with." | Inside the region, `--bud-over` |
| `ceremony-failed` | "Your device didn’t finish the passkey check. Nothing has changed." | Inside the region, `--bud-over` |
| `unknown` | "Budgetoid couldn’t finish unlocking. Nothing has changed — try again." | Inside the region, `--bud-over` |
| `unopened` | "Budgetoid couldn’t open your account’s keys with that passkey. If this account has another passkey, try again and choose that one." | Inside the region, `--bud-over` |
| `unreachable` | "Budgetoid couldn’t reach the server. Try again in a minute." | Inside the region, `--bud-over` |
| `unauthenticated` | "Budgetoid wouldn’t hand your keys back to this browser. Sign out and sign in again." | Inside the region, `--bud-over` |
| `unrecognised` | "Budgetoid couldn’t read what the server sent back. Reload the page — that’s the one thing here that can change the answer." | Inside the region, `--bud-over` |
| `inconsistent` | "Something about this account’s keys doesn’t line up — no passkey or recovery code will change it." | Inside the region, `--bud-over` |

The copy is the specification, not an example of it. **Thirteen lines in fourteen states**: the
table's first row is the resting one and says nothing, because the control standing there is what
says the account is locked.

**`unrecognised`'s sentence names the act and never the cause, and the sentence to keep out is
*this tab is running an older version*.** It reads as the more helpful line and it is a diagnosis
the evidence cannot carry: the same refusal covers this bundle meeting the **retired bare-array
response shape**, which is a newer client and an older route, where that clause is simply false and
the remedy it offers is a loop — reload, get this same bundle back, be refused again. What is true
either way is the act, so the act is what the copy carries. A reload is the only thing in the
product that fetches different JavaScript from the static host, which is why *that's the one thing
here that can change the answer* is a promise the screen can keep however the skew runs.

**A second source now raises this word and the sentence covers it unchanged, which is recorded here
rather than answered with an edit.** A manifest this browser cannot **read** — a wire string the
strict decoder refuses, or a sealed value outside the width window this bundle reads by — lands on
`unrecognised` and not on `inconsistent`. Both refusals are made before any cipher runs, so neither
has observed a byte of the account's key material and neither is a statement about it: what happened
is that the server answered and this browser could not read the answer, which is word for word what
the line already says, and a reload is the act it already names. **It is covered because the copy
names the act and not the cause** — the same property that keeps *this tab is running an older
version* out of it. A sentence written for the older, narrower source would have had to be replaced
here; this one does not, and changing it to mention a manifest would put a piece of the wire format
in front of somebody who can do nothing with it.

**The ten refusals come from two sources, and neither union is derived from the other.** The first
five are `AccountUnlockService`'s and are facts about a *device*; the last five are
`AccountKeyCustodyService`'s and are facts about a *read*, a *factor*, an *answer* and — in one
case — the account's own *key material*. The flow's union
deliberately carries no member a key that opened nothing could be filed under — `unknown` is a
rejection out of a method whose contract is to answer with a result, and nothing else — so the two
cannot be quietly merged by a reader looking for somewhere to put an eleventh word.

**The flow's failure wins, and custody's renders only when the flow reports none.** Both are
readable at once, and the state that produces it is ordinary rather than contrived: press one is
answered `unopened` — the envelopes were read and none opened — then press two is cancelled at the
system sheet, which never reaches custody, so custody's answer from the previous press is still
standing. Rendered together, the section gives two answers to one question and marks neither as the
older. The precedence is what makes "at most one sentence" true by structure rather than by
whichever template branch happens to be written first.

**`duplicate` gets no member**, exactly as `SignInService` argues for the same union one screen
over: it is an authenticator declining a credential named in an exclusion list, and an assertion
carries no exclusion list to decline against. A sentence about one would describe something that
did not happen.

**"Waiting for your passkey." is not the registration step's "Waiting for your device."**, and the
difference is not decoration. There the device is about to *make* something and the person is
waiting on a machine. Here they are being asked for an object they already own, often for a
specific one, and the sentence names the thing to go and find.

**The control never changes its name and never leaves on a refusal**, which is where this section
departs from the registration step deliberately. That step renames its Primary to **Try again** or
replaces it, because four of its nine refusals are dead ends with somewhere else to go. This
section has nowhere else: its one control is the way out of the state the section exists for, so
removing it would leave an account locked with nothing on screen to change that. A second press is
also a genuinely different attempt on most of these, because the authenticator rather than the
screen chooses which credential answers. **Two of the ten are real dead ends and each carries its
own way out in its sentence rather than in the control.** `unsupported` names one — a different
browser. `inconsistent` names none, because there is none: it is the only line in the table whose
copy says out loud that nothing the person does here changes the answer. In both cases a section
with no control at all is a worse answer than a control whose sentence says not to press it.

**Colour is never the message** — every refusal above reads the same with `--bud-over` removed.

### Five custody failures, and four next steps

**They are five because a person's next move is four different things and, in one case, nothing at
all** — and collapsing any two sends somebody down a road that cannot help them. The rule is
[account-keys.md](../business-logic/account-keys.md)'s; this section renders it rather than
re-arguing it.

- `unopened` — the envelopes were read and none opened under the factor presented. The way forward
  is another way in.
- `unreachable` — no usable answer came back at all. The way forward is the same press in a minute.
- `unauthenticated` — the server *answered*, and the answer was that this browser may not read
  these envelopes: a `401`, or the `403` the CSRF control gives. The way forward is neither of the
  other two, because retrying cannot change it and no other factor can either.
- `unrecognised` — the server answered and this browser could not read the answer. The way forward
  is to **reload the page**, and it is none of the other three.
- `inconsistent` — a factor opened, and what came out of it does not agree with the account's own
  manifest. **This is the one refusal with no way forward**, which is why it is a word rather than a
  reuse of the nearest one.

**The fourth word is the one a later author will fold into `unreachable`, and that is the one place
it must not go.** The three above it are about the account, the factor or the network; this one is
about **the answer** — what disagrees is the shape of the body, and a reload is the only act that
fetches a different copy of the JavaScript from the static host. `unreachable`'s copy is *try again
in a minute*, which is advice that can never succeed here: the next minute runs the same bundle
against the same route and is refused the same way. A sentence that sends somebody round a loop with
no exit is worse than a sentence that names an awkward remedy, and the remedy here is one press of a
reload button. **What the word may not do is say which side is stale** — the same refusal covers a
newer bundle reading the retired response shape — which is why it is `unrecognised` and why its copy
names the act rather than the cause.

**The fifth word is the one whose absence costs the most, because the nearest word is actively
harmful over it.** Reported as `unopened`, the line offers another passkey. Every factor of an
account encapsulates the same two keys, so a pair that will not open the manifest will not open it
under any of them — not another passkey, not any of the ten recovery codes, not in another browser
and not after a reload. Told to keep trying, somebody spends an entire recovery card on a door that
cannot open, and the screen encourages them the whole way. So the sentence names the **material**
and not the factor, states the dead end plainly, and offers no press: *no passkey or recovery code
will change it* is the whole of what is true. Naming the material rather than the authenticator is
also what lets the three neighbouring observations sit under this same word instead of adding rows
to the table: **four different things now raise it** — a response carrying no manifest, a manifest
that **reached the cipher** and would not open, an epoch below one this device has watched the
account pass, and a served factor set that is not the one the manifest names. A manifest that never
reached the cipher is `unrecognised`, for the reason that row gives.

**The sentence was re-checked against all four and stays exactly as written, which is worth
recording rather than leaving to be re-derived.** Three of the four are what the line has always
described: nothing the person holds changes them. The fourth — the rolled-back epoch — has one act
behind it that would: **clearing this browser's site data** drops the record the refusal compares
against, and the account then opens. The copy is still accurate, because its claim is narrower than
*nothing will change it* — it says **no passkey or recovery code** will, and neither will. Naming
the wider act is refused, and not on grounds of length: the record is the whole of the rollback
defence, so advice to clear site data is advice to switch it off, handed to the one person in the
product who has just been shown evidence it may be doing its job. It is also advice that destroys
nothing and fixes nothing in the far commoner reading, where the manifest genuinely does not agree
with itself. A line that repairs a symptom by removing the control is not a way forward, and
[voice](voice.md)'s rule about naming a door the screen does not have applies to a door it must not
open.

**`unrecognised` and `unreachable` are indistinguishable from inside the flow and are told apart by
the type the API boundary throws** — never by a message, because several are written there and a
reading matched against one of them would quietly stop covering the rest.

**The screen says "sign in again" and does not sign anybody out.** `AccountUnlockService` injects
the ceremony and custody and **nothing else** — no `SessionService`, no `Router` — so the sentence
is the whole of what it *can* do, and a collaborator census pins that: a third dependency reddens.
The absence is the structural half of a rule custody states in prose. A section that navigated to
`/welcome` on `unauthenticated` would be doing from a template exactly what the class refuses to do
from its code, and would be doing it to somebody whose session may be perfectly live — a `403` is
what the CSRF control answers a browser whose session is intact. **Sign out** is on this screen,
several sections up, under the label the sentence names.

**`unopened`'s sentence names only doors that exist.** Redeeming a recovery code has no surface
anywhere in the product, so "another way in" can offer another passkey and nothing else — the
registration chapter's rule about a sentence naming a door the screen does not have. It gains its
second clause the day redemption lands, and not before.

**`inconsistent`'s sentence names a recovery code and does not break that rule**, which is worth one
line so nobody "corrects" it into line with its neighbour. The rule is about **offering** a door the
screen does not have. This sentence offers nothing: it names both kinds of factor in order to say
that neither changes the answer, and a reader who has a card in a drawer needs to be told that
before they go and fetch it. Narrowed to the passkey, the line would leave the recovery card looking
like the thing still worth trying.

### The ceremony carries its own challenge, and the assertion is discarded

**Nothing on the server verifies an unlock, and nothing needs to.** The wrapped envelopes are the
proof. Associated data binds every envelope to its own factor identifier, so a factor that is not
this account's opens none of them and one that is opens exactly its own: the question a server
would be asked — *is this device one of this account's factors?* — is answered by the cryptography,
on the device, in the only terms that matter. The keys come out, or they do not. There is no
authorisation decision here for a forged ceremony to win.

So `WebauthnCeremonyService.deriveKeyFromLocalAssertion()` mints its own 32-byte challenge from
`crypto.getRandomValues`, runs the assertion and throws it away: the client data, the authenticator
data and the signature go nowhere. **No server nonce is spent by an unlock.** It takes no
parameters and hands back a bare `CryptoKey`, both of which are enforcement rather than
convenience — with no options object there is no member through which a caller could thread a
server nonce, and with no wrapper interface there is no `{ keyEncryptionKey }` one member away from
growing a `payload`. The flow passes what comes back straight to `custody.unlock` in one statement,
the rule `SignInService` keeps about the same value: never named on a field, a signal or a local.

**The precedent is in this client already and is not being invented here.** The same module runs a
discarded local assertion during registration — the second route to a PRF output, when `create()`
returns none — and argues in place why replaying a *server's* challenge is the shape this mistake
takes: an assertion signed over a value the server has already consumed, which nothing notices
until the day somebody decides to send it.

**Three members go into the options, and the three that are absent are as specified as the three
that are present.** The challenge is the first. `userVerification: 'required'` is the second and is
a literal because there are no server options here to read it off: omitted, WebAuthn's default is
`'preferred'` and every device that can skip the gesture does, handing back the account's content
key for a ceremony that established nobody. The `prf` extension is the third, keyed on `eval` and
never `evalByCredential` — that map is keyed on a credential id and this leg holds none.

- **No `rpId`.** There is no relying-party id on the client to pass; it is the server's, frozen at
  the production hostname, and a value invented here would be a second copy of it, wrong on the day
  the first is read from a different environment. Omitted, the browser answers for the page it is
  on, which is the one source that cannot disagree with itself.
- **No `allowCredentials`.** A discoverable assertion, as the sign-in leg's is: the authenticator
  chooses which of the account's credentials answers, which is why custody tries every entry in
  turn. **The screen does hold an identifier per credential row, and it is the wrong kind** — which
  is a stronger argument than an absence, because the absence is refutable by one glance at the row
  model. Every row carries the id `POST /api/me/credentials/{id}/revocation` is addressed by. It is
  never rendered, and it is the credential *row's* identifier: `allowCredentials` takes the
  identifier the **authenticator** minted, which lives on the passkey's public-key material, is
  returned by nothing this screen reads, and is not what a revocation route names. So a list built
  from what is reachable here would not name a credential at all, and one built from what could
  would narrow the ceremony to a credential the authenticator may not be offering.
  `GET /api/me/account-keys` returns no identifier of either kind — a factor identifier is not a
  credential id.
- **No `timeout`.** The server owns that number on the other two legs, and a literal here would be
  a third copy of it, drifting against the two that are sent.

Two server routes were rejected, and both look tidier than minting a challenge:

- **`POST /api/passkeys/assertion/options`**, the anonymous sign-in leg. It would make an anonymous
  route load-bearing for a screen deep inside the authenticated app, and it would mint a nonce that
  is **never spent** — one live challenge per press, sitting in a pool where nothing distinguishes
  it from the ones a real sign-in is about to redeem.
- **`POST /api/passkeys/reauthentication/options`**, the authenticated leg. **That pool authorizes
  three sensitive acts and not one** — erasing the account, revoking a passkey, and replacing the
  set of recovery codes, one handler each — and nothing on a nonce records which of them it was
  asked for, so every press of Unlock would leave behind a live one spendable on any of the three,
  the act that cannot be undone included, on behalf of an act that destroys nothing. **The breadth
  strengthens the refusal rather than weakening it**, and reading the pool as erasure's alone is
  what invites the opposite thought: an unlock is not an erasure, so borrowing the pool looks
  harmless. The whole value of a re-authentication nonce is the distance between what it was minted
  for and what it can be spent on, and this would spend that distance for a convenience, three ways
  at once.

**The day something on the server does have to check a factor here, that is a different ceremony.**
Replacing a set of recovery codes is the case, and it carries a server's challenge behind it. It
does not inherit this one and this one may not grow into it — which is exactly what the
recovery-codes section's own copy says to the reader, in the sentence holding its Generate control
off.

### Accessibility

Heading level `h2` under the screen's one `h1`; no level skipped. One `role="status"` region,
polite, in the DOM from first paint and empty at rest, never `assertive`. The Unlock control is a
48px target, keeps its place in the tab order while busy (`disabledInteractive` with
`aria-busy="true"`, the Buttons chapter's busy case), and its visible label is the whole of its
accessible name. Nothing is communicated by colour alone — every refusal in the table reads the
same with `--bud-over` removed. No line in this section is hung on the control by a `title`, a
tooltip or an `aria-describedby`: the prose is prose, in reading order, above the control it
belongs to.

### What ships today

**The section is on `/app/settings`, above the Export/Erase pair as the placement rule requires and
with Key rotation now between it and Export, and everything above renders as written** — the three
blocks, the thirteen lines, the one
`role="status"` region and the control that leaves when the keys are held.
`AccountKeyCustodyService` holds the account's keys and publishes the five failure words;
`WebauthnCeremonyService.deriveKeyFromLocalAssertion()` mints the challenge, runs the assertion and
returns the key; `AccountUnlockService` joins the two, provided on the Settings component rather
than at the root — it holds an *attempt*, and an attempt abandoned on a screen should die with the
screen, which is `RegisterService`'s and `SignInService`'s argument unchanged. What the attempt
produces is not held there at all: it goes to custody, which is root-provided because the keys are
state of the session.

**A reloaded tab is now recoverable from inside the account rather than by leaving it.** Two of the
three other producers of a key-encryption key — the assertion on `/welcome` and the registration
flow — sit behind `guestGuard`, which turns an authenticated visitor away, so until this section
existed somebody whose tab reloaded had to sign out and sign back in to get their own keys back.
That exit is still on the screen and is no longer the only one. The fourth producer is the section
below this one, whose finished run hands the promoted generation to custody — a fact about that act
and not a second way in, which is why no copy on either section mentions it.

**The flow carries no `available()` check of its own, and the omission is deliberate**, so nobody
adds one back for symmetry with `SignInService`. That check exists there because a challenge is a
nonce the server persisted and a browser that was never going to finish must not spend one. This
flow spends nothing — no options leg, no nonce, no round trip before the ceremony — so there is
nothing an earlier check could save, and the ceremony already answers `unsupported` for itself.
A copy would be a second enforcement with no observable difference, which
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) refuses.

**Being locked costs something anybody can see**, because every narrative screen seals what it
writes: a reload leaves the account's names unreadable until this control is pressed. It stays a
section rather than a screen standing in front of the app because the app is navigable while
locked, and the [locked account](#the-locked-account) chapter is what each content screen renders in
the meantime. This control is the only way out of that state a person can be *sent* to — a finished
rotation leaves the tab holding keys too, but nothing offers rotating as a way to unlock and nothing
may — which is why nothing may put this one behind anything.

## Key rotation section

The act that gives an account new keys and rewrites every sealed value in it under them. M3 base:
**none** — prose, one checkbox, one button, one `role="status"` region and one `progressbar`, for
the reason the two sections above it have none: one act about one thing is a sentence, and every
component that would wrap it exists to group things there is more than one of.

It sits on `/app/settings` **between Account keys and What we can read**, which is a narrower claim
than the placement rule alone makes. The rule — Export and Erase are a pair, so nothing goes
between them and everything else arrives above them — permits anywhere above that pair. The
narrowing is argued below, because the section it displaces argued its own position and that
argument is still good.

### Why it goes above the transparency statement rather than below it

**What we can read** claims the last place above Export and Erase, and the reason it gives is that
the two controls beneath it are what somebody reaches for when the statement tells them something
they are not willing to live with. That argument names the acts *below* the statement as its
remedies.

**A rotation is not one of those remedies, and placing it under the statement would say it is.**
Nothing in those three paragraphs reads differently after a rotation: the operator sees the same
columns, the same lengths and the same short codes beside the same names. A control offered
directly under a statement, beside two that answer it, is offered as a third answer to it — and
this one would be answering a question it does not touch. Above the statement, beside Unlock, it
reads as what it is: the second thing this screen does with the account's keys.

### Why it is a section and not a route

The Account keys chapter's argument transfers unchanged and is not re-derived here: `/app/rotate` is
an address anybody can open, including somebody with nothing to rotate, so the chapter specifying it
would owe a second design for a screen about nothing.

**A second reason belongs to this section alone.** A rotation that was interrupted survives only as
*server* state — a staging row and one seal per factor, and nothing in the browser — so the way back
into one has to be somewhere a person arrives without being sent. They arrive here. A route would
have to be linked from somewhere, and the only honest place to link it from is this screen, which
leaves the route as a second address for the thing the link already is.

### Two key sections, and neither control waits on the other

**A rotation does not wait on an unlock.** It runs its own passkey ceremony and holds its own copy
of both generations — the argument is
[key-rotation.md](../business-logic/key-rotation.md)'s, and the short version is that a resumed run
has no unlocked custody to borrow from, so the driver must be able to do this from one ceremony or
there are two mechanisms for one job.

Three consequences, and the third is the one a writer gets wrong:

- **Rotate is offered on a locked account**, at full strength, with no sentence about unlocking
  first.
- **The two controls can be pressed in either order** and neither section's copy may imply a
  sequence.
- **Finishing a rotation unlocks the account in this tab.** The promotion hands the new generation
  to custody through the same gate an unlock passes, so somebody who arrived at a locked account and
  pressed Rotate ends up holding keys. That is a fact about the act, not a feature to advertise:
  no copy here offers rotation as a way to unlock, because a person who wants to read their records
  should press the control that takes a second.

### The copy is the specification

Two blocks, and the second is the consequence. They are two because the consequence has to be able
to stand on its own next to the gate, per the recovery-code hand-off's rule — a consequence folded
into standing prose is read once by somebody who came for something else.

> Rotating gives your account new keys and re-encrypts every name and note under them. Every passkey
> and recovery code you have keeps working — the old keys stop opening anything.

> This rewrites every record in the account, and nothing can put the old keys back. If this tab
> closes part-way through, the rotation stops where it is and this section offers to finish it — a
> rotation picked up again starts over from the first record.

**Both blocks are standing prose, so both have to be true at rest, while a run is in flight, and
after one has finished** — the Account keys chapter's rule, applied to a longer act. Written as what
rotating *does*, rather than as what is about to happen, every sentence survives all three.

**Five sentences a writer reaches for, and why each is refused:**

- *Nothing is saved until you press Rotate.* The registration flow's line, and it is the one most
  likely to be pasted here. It is false: a run writes from its first accepted chunk, and the
  staging row is written before that.
- *This may take a few minutes.* A wall-clock promise nothing measures. The
  [frontend performance](../engineering/frontend-performance.md) rule about figures that do not
  transport applies harder to a number said to a person than to one in a doc — the bar is the honest
  version of this sentence.
- *You will need to unlock again afterwards.* False, per the section above.
- *Your records are safe while this runs.* A reassurance with nothing checkable in it, and the copy
  rule on this screen is a fact about the system with nothing around it.
- *Rotate your keys if you think one has leaked.* Advice, and advice the product cannot support: it
  has nothing to tell the person about whether one has.

### The Rotate control

- **Destructive** (`--bud-over` fill, `#FFFFFF` label), 48px target, visible label **Rotate keys**.
- **Not Primary.** Export is the screen's one main action and a screen with two is a screen with
  none — Sign out's reason, unchanged.
- **Not Outline.** That is Unlock's treatment and Unlock's reason is that nothing is lost either
  way. Something is lost here, deliberately.
- **Not Outline-disabled.** That treatment is for a destructive act whose confirmation UI does not
  exist. Here it does, and it is the checkbox immediately above the button.
- **This is the first control in the book to take the Destructive fill without deleting anything,
  and the Buttons table is widened in the same commit to say so.** The fill is two promises — a
  confirmation follows, and there is no way back — and a rotation keeps the second as squarely as an
  erasure does. Every narrative column in the account is overwritten in place, no copy of the
  previous ciphertext is kept anywhere, and making the previous keys stop opening things is the
  act's *purpose* rather than a side effect of it.
- **No composed accessible name.** It is the only Rotate on the screen.
- **While a run is in flight it takes `disabledInteractive` and `aria-busy="true"`** — the Unlock
  control's treatment and the Unlock control's reason, which is not restated here.
- **The attribute and the handler's guard read one predicate with one owner.** The flow service
  publishes "a run is in flight" and the control's `disabled`, its `aria-busy` and the handler all
  bind that one signal. The drift this prevents, and the defect it already caused once on the Unlock
  control, are argued there and not argued again.
- **It is replaced, not joined, when there is a run to finish.** The resume read answers either "no
  rotation" or one rotation, so the section draws exactly one control: **Rotate keys**, **Finish
  rotating** with the date the run started in the line above it, or **Rename and finish** when the
  last run stopped on `same-name`. The last wins over the other two, because that run cannot finish
  until a name changes. Two controls would ask a person to choose between starting over and
  continuing, which is a choice with a wrong answer.

### The acknowledgement, and the one rule it departs from

- **A checkbox above the control.** The control takes `disabledInteractive` until it is ticked —
  the Buttons chapter's third case, a control waiting on the person rather than on work — and needs
  no sentence beside it, because the thing it waits on is the visible control immediately above.
- **The gate is in the click handler as well as in the attribute, and that is not belt and braces.**
  Material's click-halt is applied to anchors only, so on a `<button>` the DOM `disabled` property
  stays `false` and the click reaches the component. On the registration step the cost of getting
  this wrong is an account created for somebody who acknowledged nothing; here it is a run begun by
  somebody who acknowledged nothing, which writes to every row they own.
- **The consequence is its own block above, never the checkbox's label** — the hand-off's rule. A
  label carrying the consequence is read by the person who has already decided to tick it.
- **The label is** *I’ll leave this tab open until it finishes.*
- **It arrives unticked every time the section is drawn, including on a resume.** A box that arrives
  ticked acknowledges nothing, and a resumed run rewrites the account exactly as the first press
  did.
- **This is the book's second acknowledgement and the first whose act has not happened yet, which
  departs from [voice](voice.md) knowingly.** That rule — an acknowledgement is what the person did,
  not what they promise — exists because a promise is not checkable, and the hand-off had a real
  prior act to name. Nothing precedes a rotation the way saving the codes precedes leaving the
  hand-off. So the label names the one act that is the person's to perform *during* the run, in
  their own words, and the thing it asserts is true at the moment it is ticked: the tab is open.
  Refused, and for voice's own reasons: *I understand this can’t be undone*, which asks for a
  feeling; and *I’ve saved my recovery codes*, which is true of a different screen and would send
  somebody away before an act that keeps those codes working.
- **The gate is not a threat, and the consequence block is what keeps it from reading as one.**
  Breaking the promise costs time rather than records — the section offers to finish the run. It
  still earns a gate, because the cost of breaking it is the whole account being re-sealed a second
  time from the first record.

### Anatomy

- A settings section per the spec above: `<section aria-labelledby>`, `eyebrow` heading **Key
  rotation**, `--bud-space-4` between heading and content, `--bud-space-7` to the next section,
  prose capped at 65ch. One grid column, `justify-items: start`, `--bud-space-4` gap at every width,
  for the reason the two sections above give: one control, no rows, nothing for a second column to
  carry.
- **The order inside the section is the reading order the act needs**: standing prose, consequence
  block, the rename block when there is one, checkbox, control, progress block. The consequence is
  read before the gate and the gate before the press, and nothing between them competes for the eye.
  A name is typed before the gate is ticked, so the gate is still what sits immediately above the
  press.
- **One `role="status"` region, in the DOM from first paint and empty at rest**, carrying the phase
  sentence and every refusal. `status` and never `alert`: the person asked for this.
- **The bar is not inside the region, and that is the one accessibility decision this section makes
  that the sections above did not have to.** A determinate `progressbar` whose value moves once per
  accepted chunk, inside a live region, narrates a number several hundred times over one run. The
  region holds the phase sentence, which changes three times; the bar sits beside it in reading
  order carrying its own `aria-valuenow`, `aria-valuemax` and label, and announces nothing. It is
  the rule [accessibility](accessibility.md) already states about the recovery codes — content is
  not an announcement — reached from the other end.
- **The phase sentence reserves one line box** (`min-height: 1lh`), so nothing below it moves when
  a phase changes.

### The progress line

**One denominator, published once, and a numerator that counts only what the server accepted.** The
inventory arrives with the begin and is republished on a resume; the chunk route answers no count
and the resume read carries none, by design. So the denominator is the inventory's five narrative
arms, and the numerator is **rows carried by a chunk the server answered 204** — never rows
collected, never rows sealed, never rows queued. A chunk is all-or-nothing in one save, so 204 is
the only increment that is honest at the moment it is drawn.

**Three phases, and the word carries more than the percentage does:**

| Phase | Copy | Bar |
| --- | --- | --- |
| Collecting | "Reading your records." | none |
| Re-sealing | "Re-encrypting your records." | determinate, with **`n` of `m` records** beside it |
| Finishing | "Finishing." | determinate, left where it stands |

**No bar during collecting**, because a determinate bar sitting at zero while five list reads run
says that nothing is happening. The phase word says what is, and a person watching it change three
times learns more about where a run is than a percentage tells them.

**A resumed run starts the bar at zero, and the consequence block says so before it happens.** That
is the sentence this chapter exists to get right: a bar restarting looks broken unless the person
was told, one screen earlier, that a rotation picked up again starts over from the first record.
The server publishes no per-row progress and the resume read deliberately carries none.

**A client-side per-row record is refused, and it is written down here so nobody adds it back for a
nicer bar.** Keeping "which rows are done" in `localStorage` would be a second numerator able to
disagree with the server's completeness gate, persisted across reloads, naming which rows an account
holds, in a store anybody at this device can write. It is precisely the thing the server declines to
keep.

### The refusals

**Twelve words, from three sources, and no sentence is shared with a section that means something
else by it.**

**The ceremony's five come from the Account keys chapter's table, four of them verbatim** —
`unsupported`, `cancelled`, `no-prf` and `ceremony-failed` — including their closing *Nothing has
changed.* That borrowing is honest because of a constraint on the flow rather than a judgement about
the words: **the ceremony is the first thing every press does, and nothing is posted until it
answers.** On a begin nothing has been written; on a resume nothing new has. A flow that ever posted
before the ceremony would make five sentences false at once, which is why the order is stated here
as a rule and not as an implementation note.

**The fifth is the one that cannot travel, because it is the only one of the five that names an
act.** Custody's line is *Budgetoid couldn’t finish unlocking.*, and the other four say what a
*device* or a *browser* did, which is the same fact on any screen that asks for a passkey. Here it
becomes:

| Word | Copy |
| --- | --- |
| `unknown` | "Budgetoid couldn’t finish rotating your keys. Nothing has changed — try again." |

The shape is custody's, down to the clause: the subject is the software, the act is the one this
screen offers, and the closing promise is the one the ordering rule above makes true. Only the act
is renamed, and it has to be — a person who pressed **Rotate keys** and is told that unlocking
failed has been handed a sentence about a control one section up, and the obvious next move it
suggests is to go and press that one, which changes nothing about why this failed. It reads as
act-neutrally on **Finish rotating** and **Rename and finish** as on **Rotate keys**: all three
presses are rotating this account's keys, and none has written anything by the time this line can
appear.

**Seven are this section's own**, and they differ from custody's five where they share a word: each
says what became of the run, which the Account keys lines have no run to say anything about.

| Word | Copy |
| --- | --- |
| `unreachable` | "Budgetoid couldn’t reach the server. The rotation stopped where it is — try again in a minute and it picks up from there." |
| `unauthenticated` | "Budgetoid stopped accepting this rotation from this browser. Sign out and sign in again, then finish it from here." |
| `unrecognised` | "Budgetoid couldn’t work with what the server sent back. Reload the page — that’s the one thing here that can change the answer." |
| `inconsistent` | "Something about this account’s keys doesn’t line up — no passkey or recovery code will change it." |
| `unfinished` | "This account kept changing while it was being re-encrypted, so the rotation stopped where it is. Close Budgetoid in other tabs and on other devices, then finish it here — the records already re-encrypted stay that way." |
| `factors-moved` | "The passkeys and recovery codes on this account changed while the rotation was running. Start it again from here — the records already re-encrypted stay that way." |
| `same-name` | "Two records in one list have the same name, so the rotation stopped where it is. Give one of them a new name to finish it — the records already re-encrypted stay that way." |

The copy is the specification, not an example of it.

**One of the seven is custody's sentence unchanged, and one is custody's sentence widened by a
word.** `inconsistent` survives verbatim because it is the one line in either table that says out
loud that nothing the person does changes the answer, and a run standing in front of it changes
nothing about that.

**`unrecognised` keeps custody's remedy and loses custody's first clause, because this section
raises the word from two sources where the server's answer was perfectly readable.** A run refuses
before it posts anything when the published inventory names a budget, and when an arm comes back
with fewer rows than that inventory counted — the first because no chunk can stamp a budget and the
completion could never pass, the second because a client that cannot see rows the completeness gate
counts has a run that can never finish. In both the body parsed, every member bound, and this
bundle could not act on it. *Couldn’t read* is false there, so the sentence says **couldn’t work
with**, which covers a body this client could not read and a body it could not drive without naming
which — and neither is a distinction a person can act on differently.

**The remedy is what had to survive, and it does.** A reload is still the only act in the product
that fetches different JavaScript from the static host, which is the only thing that changes a
refusal about what this bundle can drive; and where the cause is instead a list read that went
stale, a reload re-reads it. The staged run is still on the server either way, so nothing is spent
by trying. That is the same property the Account keys chapter names — **the copy names the act and
not the cause** — which is why widening the source cost one word rather than a seventh line.

**`factors-moved` is the one whose remedy has a rule behind it, and the rule is not visible in the
sentence.** Starting again after this refusal must re-stage **the generation the interrupted run
already held**, never a freshly minted one. The staged seals are the only copy of that generation
anywhere; a second begin overwrites them in place; and every row a chunk already re-sealed under it
would then open under nothing at all — silently, with no error and no repair path, which is the same
shape as completing a run early. So the restart carries the recovered keys forward, which is also
what makes the sentence's last clause true. A fresh generation is drawn in exactly one case: the
resume read said there is no rotation.

**And it carries them to the set the account has now, which is the other half of the same rule.**
The restart recovers that generation out of a surviving factor's staged seal, encapsulates it to
every factor the account currently holds and seals a manifest over that set, at the epoch a begin
files at. Restating the staged pair would post a seal set naming the factors that were enrolled when
the run began — the very thing this refusal is about — and the server would refuse it. Recovering
needs a factor present in **both** the staged seal set and the live set; when the last of those is
gone the generation is unrecoverable, the rows already re-sealed under it are stranded, and the word
is `inconsistent` rather than a suggestion to try another passkey.

**What holds that rule is a shape rather than a check.** `key-rotation-material.ts` has one entry
point per press and neither can draw a generation while a run is staged: the begin's takes the
resume read itself and decides on `rotation === null`, so the one case that may draw is the only case
in which drawing is reachable, and the resume's takes the staged run rather than the read, so it has
no such case at all. A flag over one function was the other shape and is weaker — a caller holding a
staged run can pass the wrong value. Nothing else enforces any of it, on either side of the wire.

**`unfinished` is bounded, and the sentence is what the person sees after the bound is spent.** Rows
created after a collection make the completion answer that the run is incomplete, and the remedy is
to collect and send again — three passes, then this word. An unbounded loop is the same
non-converging failure [key-rotation.md](../business-logic/key-rotation.md) refuses from the other
side, and a person watching a bar go round forever has been told less than one who has been told to
close a tab.

The sentence names what was seen and the act that stops it, never the tab that caused it. **Other
devices are named because the forms on this one cannot be the source**: a tab that learned of the run
disables its own forms, so what keeps changing the account is a browser that loaded before the begin,
on this device or another. A chunk the server refuses because two records would share a name spends a
pass too, since the next collection is what finds the pair. So this word is also what somebody sees
when another browser keeps writing a name the run has just re-sealed.

**`same-name` is the one refusal whose remedy is typed, and the section takes it itself.** A tab that
loaded before the begin still holds the outgoing keys, so it can give a record a name the run has
already re-sealed on another record, and nothing refuses that: the two blind indexes are taken under
different keys. The next pass would re-seal both onto one value, which the server refuses whole on
every send. No sentence clears that and no other screen can either, because every content form is
disabled while a run is staged ([below](#what-a-run-does-to-the-rest-of-the-app)). The rename block
[below](#renaming-one-of-two-records-with-one-name) is the remedy.

**The run finds the pair before it posts anything, and the server's refusal is only the backstop.**
After each collection it compares every name's incoming index within each list, and two equal values
stop the run on this word before that pass sends a chunk. The server's `rotation_name_collision` names
no row and cannot, so it gets no word of its own: it means a name landed between a collection and a
send, and it spends a pass the way `unfinished`'s cause does. Every `same-name` a person reads
therefore arrives with both names.

**The sentence names no tab and no screen.** *Another tab renamed a payee* is a cause this client
watched nobody commit. *Rename it in Payees* is false while a run is staged. *Unlock to see the names*
is false too: the names come from the run's own keys, never from Unlock's.

**A completion refused because the rotation was already completed gets no word at all, and that is
a decision rather than an omission.** The server answers that way when a completion is re-sent — the
first one succeeded and this client lost the answer. The run is *finished*, so the honest render is
the finished one: the flow goes on to take custody of the promoted generation exactly as a 204
would have had it do, and the section reports a rotation that is done. A refusal sentence there
would tell somebody their rotation failed at the moment it had succeeded, and send them to press
Rotate again over an account that no longer needs it.

**Colour is never the message** — every line above reads the same with `--bud-over` removed.

### Renaming one of two records with one name

The block the section draws while `same-name` stands. M3 base: one outline text field.

**Anatomy**, top to bottom, inside the section's one column:

- **A lead line naming the pair**, plain text and not a target. When the two spellings match:
  *Two payees are called “Groceries”. The new name goes to the one that was given this name after the
  rotation started.* When they differ: *A payee called “Groceries” and one called “groceries” count as
  the same name. The new name goes to “groceries”, which was given its name after the rotation
  started.* The noun follows the list — account, payee, category group, category — with the article
  English gives it (*An account called …*), and the names
  render as stored, the way every list renders them.
- **One text field**, label **New name**, `autocomplete="off"`, whose accessible description is the
  lead line. It takes the cap and the whitespace-only refusal the ordinary name fields take, from the
  same constant.
- The section's checkbox and its one control, now **Rename and finish**, Destructive because it
  rewrites the account exactly as **Finish rotating** does. It stays `disabledInteractive` until the
  box is ticked **and** the field holds a name, and the click handler refuses on the same pair.

**The product picks which of the two is renamed: the one that took the name second.** The two records
look the same on every screen, and the screens that could tell them apart are blocked, so a choice
between them gives the person nothing to choose with. The record given its name after the rotation
started is the one whose name arrived second, so the other keeps what it had first.

**The rename asks for a passkey, because the keys went with the run.** A run that stops holds
nothing — both generations end with it — so the press is a finish that carries a name, and its
ceremony comes first, which keeps the five ceremony sentences and their *Nothing has changed.* true
here. A run that stayed paused holding both generations while somebody thought of a name would save a
tap on a rare path and break the one rule this chapter's copy rests on.

**It works the same on a locked account.** The names and the keys come from the run's own ceremony,
never from Unlock, so nothing in the block mentions unlocking.

**The name is sealed under the incoming keys**, so the server's unique index compares it against every
record already re-encrypted. Before posting, the run also compares it against every record in that
list, including those not yet visited, because the server cannot compare an incoming index against a
row still under the outgoing key.

**What it refuses**, each beneath the field and never in the region:

- **A name another record in the same list already has**, found by the run before anything is
  written: *Another payee already has this name. Choose a different one.* This is the chapter's own
  sentence rather than the server's, because it is the client's observation, made before any request
  exists, over the very value the unique index compares.
- **The server's `400` keyed on `Name`**, rendered verbatim, per
  [Whose sentence goes beneath the field](#whose-sentence-goes-beneath-the-field).
- Blank and over-length names never reach a press: the control waits on the field.

**States.** *Standing*: the field is editable. *Working*: the field is `readonly` and keeps its value
and focus, since one press carries one name. *Refused at the field*: the value is kept and the field
is `aria-invalid`. *A different pair found*: the lead line is replaced and the field cleared, because
a name typed for one pair answers a question nobody is asking now; nothing is renamed. *No pair
found*: somebody fixed it elsewhere; nothing is renamed and the run carries on. *Any other word*: the
block goes, and that word's remedy governs. A ceremony that fails leaves the block standing.

**The region never carries a name.** It holds the refusal; the names are content, which is the rule
the bar already follows from the other end. The phase stays *Reading your records.* across the rename,
which is one request between two collections — a phase word for it would flash.

**Accessibility.** The field has a programmatic label and is a 48px target. When a press ends on
`same-name` or on a refusal beneath the field, focus moves to the field, because the next act is
there. Arriving at the screen with the block already drawn moves nothing.

### What a run does to the rest of the app

**While a rotation is in flight, the three content screens render the locked-account notice in place
of their lists, and their forms are disabled.** This is the chapter's decision that reaches other
screens, and it is specified here so that it is not discovered by whoever builds the third one.

**The reason is not that the tab holds no key — it holds one.** Custody holds the generation in
force; the rotation driver holds both. A row a chunk has already re-sealed will not open under
what custody holds, so a list drawn mid-run is part names and part em dashes, and the proportion of
dashes rises as the run succeeds. A screen that looks more broken the better things are going is
worse than one that says what is happening.

**Letting custody open under either generation is refused outright.** It is the exact capability the
story's last acceptance criterion says must not exist — nothing may decrypt under the previous
content key once a rotation is done — and building it for the length of a run builds it.

**The notice gains a second render rather than a second component.** One input naming which of the
two states sent it; no injection, no status read, no `Router` — the emptiness that makes "it cannot
navigate" structural is untouched, because the screen already computes the reason and passes it.

| Reason | Copy |
| --- | --- |
| `locked` | "This tab can’t read your account yet. Unlock it in Settings." |
| `rotating` | "Budgetoid is giving this account new keys. Your records come back when it finishes — watch it in Settings." |

**The second sentence exists because the first one's advice is false during a run.** Pressing Unlock
mid-rotation gets the generation that is on its way out, and the list stays half dashes; the
smallest act that clears this block is waiting, and the place to watch it is named for the same
reason Settings is named in the other line. This is the *do not advise when unsure* rule from
**Two predicates** below, applied to a state where there is something certain to say.

**The predicate the screens read gains a term and does not gain a shape.** The form is usable only
when custody says `unlocked` **and** no run is in flight — written positively, so a state nobody has
thought of yet arrives disabled. The notice renders when custody says `locked` **or** a run is in
flight, and which sentence it carries is decided by which of the two is true, with the run winning
when both are: somebody who arrived locked and pressed Rotate is going to be able to read their
records when it finishes, and the Unlock advice would send them to a control that cannot help.

**Disabling the forms is not tidiness.** A row created after a run has collected is a row the run
will never visit, and the completion refuses until it does — the `unfinished` word above is what a
person is told when that happens three times. Keeping the forms live during a run means offering
somebody a way to make their own rotation fail.

### Accessibility

Heading level `h2` under the screen's one `h1`; no level skipped. The checkbox has a programmatic
label and is a 48px target; the phase sentence and the `n` of `m` line are plain text and are not
targets. The region is `role="status"`, polite, and never `assertive` — a rotation is something the
person started and is watching, and the carve-out for `alert` is for a failure that lands after
attention has moved on. The bar carries `aria-valuenow`, `aria-valuemax` and a label naming what is
being counted, and sits outside the region. Nothing here is communicated by colour alone.

### What ships today

**The section renders and both controls can be pressed.** `key-rotation-section.component.*` draws
it on `/app/settings` directly below Account keys: both blocks of prose character for character, the
acknowledgement under its specified label, one Destructive control that is **Rotate keys** or
**Finish rotating** and never both, the `role="status"` region carrying the three phase sentences
and all twelve refusals, and the determinate bar outside that region with its own `aria-valuenow`,
`aria-valuemax` and label. The checkbox is a signal initialised to `false` on the component, so it
arrives unticked on every construction including over a staged run, and no path sets it from
anywhere else.

**The gate is in the click handler as well as in the attribute**, and the handler is as wide as the
attribute it backstops: the control is drawn unpressable on `working || !acknowledged()` and
`rotate()` refuses on exactly that pair. Removing either half of it reddens exactly one case and the
attribute assertions stay green, which is the measurement this rule exists for.

**"A run is in flight" has one owner.** `RotationFlowService.working` is `busy || rotations.running()`,
and the control's `disabled`, its `aria-busy`, the component's handler and the flow's own entry point
all read it. That last reader is what closes the gap named below this chapter's control section:
`KeyRotationService.begin()` still has no re-entrancy guard of its own, and the flow is where the
second press is refused.

**The flow is a fourth producer of a key-encryption key in this client**, and the first that spends a
server-minted challenge for one. `ReauthenticationApiService` posts
`/api/passkeys/reauthentication/options` — the authenticated pool, never the anonymous assertion one
— and the ceremony is the first thing either press does, so *Nothing has changed.* is true on a begin
and on a resume alike. The flow checks `available()` before that call for `SignInService`'s reason
and not the Unlock control's: a nonce the server persisted must not be spent by a browser that was
never going to finish.

Underneath it the driver is unchanged. `begin()` takes a passkey assertion and runs a whole rotation
to its 204 — and over a run that is still staged it is the restart this chapter's `factors-moved`
rule describes, carrying that run's generation to the corrected factor set under the identifier
already on file; `resume()` finishes a run this browser never began, quoting the identifier the
server hands back rather than beginning anything. `readStagedRotation()` is the read the section
makes in `ngOnInit`, and it is what decides which control there is to draw. **Finishing a rotation
unlocks the account in this tab, as the section above specifies**, and no copy anywhere advertises
it.

**The rename block ships**: while the driver publishes a pair the section draws the lead line, the
**New name** field and **Rename and finish**, and `RotationFlowService.renameAndFinish` runs the
ceremony before a `resume` that carries the name.

**Four departures, each named as work rather than smoothed over by moving the target.**

- **The ceremony says nothing either, and this is the gap a person actually feels.** Between the
  press and the first phase word the region is empty for the whole of the passkey check — observed
  in a browser, where that is several seconds of a screen that looks inert with only a busy control
  moving. The Account keys section one above carries *Waiting for your passkey.* for exactly this
  moment and argues that it is a moment a person can act on: the thing to do is touch a sensor or
  pick a key up off the desk, and no other line in that table tells them so. The phase table here
  has three rows and the ceremony is none of them, so the sentence is owed.
- **A finished run says nothing.** The phase table has three rows, and `finished` is not one of them
  — while the paragraph about a re-sent completion says "the section reports a rotation that is
  done". Nothing is invented in the template: the region is empty at `idle` and at `finished` alike.
  What partly covers it today is the Account keys section one above, which starts saying the account
  is unlocked the moment the promoted generation reaches custody. The sentence is owed here.
- **The line above Finish rotating is the date and nothing else.** This chapter names it as "the
  date the run started" and specifies no sentence around it, so the section renders the reader's own
  calendar day through `credentialRegistrationDate` and stops there.
- **The bar's accessible name is not in any table.** *Records re-encrypted* is what ships, chosen to
  satisfy "a label naming what is being counted". It is the one string on the section that no copy
  table specifies.

**What a run does to the rest of the app ships.** `locked-account-notice` takes one input and
renders both sentences in the table above character for character; `/app/accounts`,
`/app/categories` and the transactions screen each carry the run as a second term on both of their
predicates, and each passes the word that decides which sentence. "A run is in flight" is
`KeyRotationService.running()` **or** a staged run on file, read on each screen off the driver's two
signals — the second term is what survives a reload, and it is read once in the `APP_INITIALIZER`,
after the probe has answered and only for a visitor it found authenticated.

**The sentence beside each disabled form is now specified** rather than left to the screens, and so
is the locked one beside it, which had shipped unwritten since the locked treatment was built —
[the locked account](#the-locked-account) carries both, because that chapter owns the disabled-form
rule.

**What the placement claim above costs today** is smaller than it was and has not gone: the section
sits between Account keys and Export, because **What we can read** renders below Erase rather than
above Export. That is the departure recorded in that chapter, and this section is above the pair as
the rule requires.

## What we can read

The product's transparency statement: what the people running Budgetoid can see of an account, and
what they cannot. M3 base: **none** — three paragraphs of prose, no control and no region, for the
reason the two sections above it have none: one thing to say is a paragraph, and every component
that would wrap it exists to group things there is more than one of.

It sits on `/app/settings` **between Key rotation and Export**, on the placement rule the
recovery-codes chapter argues and which is not restated here: Export and Erase are a pair, so
nothing goes between them and everything else arrives above them. The
[key-rotation](#key-rotation-section) chapter argues why that section lands between Account keys and
this one rather than under it. Being the last thing above that pair is right for this section rather than merely
permitted by the rule — the two controls beneath
it are what somebody reaches for when this section tells them something they are not willing to
live with, so the statement comes first and the acts follow it.

### The copy is the specification

> We can read the numbers and the structure of what you record: amounts, dates, currency codes,
> account types, the order you arrange things in, the timestamps on every row, and the identifiers
> behind them. We can see how many accounts, payees, categories and transactions you have and which
> of them point at each other, and we can read your email address.
>
> We can’t read the names and notes you type. Your browser encrypts those before they’re sent,
> under keys it takes from your passkey or one of your recovery codes, and we never receive one of
> those keys. What we can see about a name or a note is how long it is.
>
> Names on accounts, payees, categories and category groups are stored beside a short code your
> browser works out from the name, under a key of its own that we never receive. The code is what
> lets your browser spot a name it has already used without sending us the name. The same name
> always gives the same code, so we can tell when one of these names changes and when one comes
> back. The code can’t be turned back into a name, and we can’t check a guess against one.

**Three paragraphs, three questions, and the order is the argument.** *What can you see?* — the
plainest and longest answer, and it goes first, because a statement that leads with what it cannot
read is selling something. *What can you not see?* — second, where it is worth something, having
been said after the first paragraph rather than instead of it. *What is left over?* — third,
because the short code beside each name is the one part of this design that is neither readable nor
invisible, and a reader who works it out later on their own will read its absence here as the
section's one omission, and grade every other sentence by it.

**The first list is long on purpose and may not be summarised.** *Everything except the words* is
shorter, reads better and is the sentence to refuse: a transparency statement that summarises has
chosen what to leave out, and the reader cannot see what was dropped. Every item in it is a column
this server reads in the clear.

**Three phrasings may never appear anywhere under `src/`**: *nothing is encrypted*, *not encrypted
yet*, and *nothing you record is encrypted*. They are refused by a rule over the source text — the
shape `no-external-origins.spec.ts` and `key-import-single-source.spec.ts` already use — rather
than by a reader's care, because each is a sentence somebody writes in good faith while editing the
paragraph around it, and the screen carrying one renders perfectly, ships green and is wrong about
the single thing this section exists to state.

### Why it says "we"

**The heading and every sentence under it are first person, and this is the one section on the
screen where that is true.** Everywhere else the subject of a sentence is the software — *Budgetoid
couldn't reach the server* — which is [voice](voice.md)'s rule and is right everywhere it applies,
because everywhere else the thing that succeeded or failed is a program. Here the subject is the
people who run the service and hold its database. That is not a program, and written as one it
becomes somebody else: *Budgetoid's operators can read…* is the same fact in the third person and
reads as a description of a party the reader is being introduced to, rather than as an admission by
the party they are talking to.

**The precedent is already in the book** — the recovery-code hand-off's *to you, and to us*, in
[voice](voice.md), where the clause doing the work is the one that puts the operator in the same
position as the reader. This section is that clause at the length of a screen.

**No apology around it, and no reassurance either.** Not *we know this matters to you*, not *your
privacy is important*, not a heading that softens the subject into a topic — *Privacy*, *Your
data*, *Data visibility* all describe an area, where **What we can read** makes a claim and puts a
name to who is making it. This is the register the Account keys section above names as the one this
screen speaks in, and it is the whole of it: a fact about the system, with nothing around it.

### The third paragraph, and exactly what the code gives away

**The code is a keyed digest of the name under the account's own index key, and this section names
neither the algorithm nor the route.** What it is, what it is computed over and what it is for
belong in [account-keys.md](../business-logic/account-keys.md) and
[ciphertext-envelope.md](../business-logic/ciphertext-envelope.md). Four columns carry one —
`accounts.name_key`, `payees.name_key`, `category_groups.name_key` and `categories.name_key` — and
the three description columns and `budgets.name` carry none, because an index answers *which row
holds this name* and a note is never looked up.

**What it does not give away rests on the key, not on the algorithm, and the copy's last sentence
is the one that needs this.** The digest is keyed on 32 random bytes drawn in a browser and never
sent, so there is no guessing attack available: nobody holding the database can compute the code
for a name they suspect and look for it. That is the whole difference between this and a plain hash
of the name, and it is what lets the copy say we can’t check a guess. Every code is the same width
as every other, so the name's length does not leak through it — the length leaks through the
**envelope**, which is what the second paragraph's last sentence is for, and the two facts are
deliberately attached to the two different things that disclose them.

**What it does give away is narrower than "equal names are visible", and the difference is worth
the paragraph.** Equal names do produce equal codes; that is the point of it, and it is what both
the duplicate check and the uniqueness rule are built on. But comparison has fewer axes than it
first appears:

- **Not across rows.** Within one of these columns the database refuses a second row carrying the
  same code — `IX_accounts_budget_id_name_key` and its three twins are unique over
  `(budget_id, name_key)` — so two rows of one column in one budget cannot share one.
- **Not across columns.** The message the code is taken over names the table and the column, so one
  name under `payees` and the same name under `categories` are unrelated values. An operator cannot
  learn that a payee and a category are called the same thing.
- **Not across accounts.** The index key is per account, so nothing learned about one transfers to
  another. Two people who both record the same shop key it to two different codes.
- **Not across budgets.** The message names the budget too, so one name in two budgets of one
  account is two unrelated codes. It did not always: the key alone is per account, and until the
  budget was written into the message an operator could see that two of somebody's budgets held a
  payee of the same name — without ever learning the name. That was the one axis this list used to
  get wrong, and the copy may not go back to claiming it.
- **Across time, and that is the one that is left.** A ciphertext changes on every save, because a
  fresh nonce is drawn for each one; the code changes only when the name does. So a rename is
  visible as a rename, a save that left the name alone is visible as one, and a name coming back —
  a row removed and a later one taking its name — is visible as a repeat.

That last bullet is what the copy claims, in the reader's words, and it is the whole of what the
copy may claim. **The sentence to keep out is *we can see which of your rows share a name*.** It is
the reading a writer arrives at from the mechanism alone, it sounds more candid than the true one,
and it describes something the schema forbids.

### The asymmetry, and why the safe error is the one that survives

**A transparency statement that overclaims privacy is worse than one that overclaims exposure, and
putting those two in order is not permission to commit the second.** The first hurts a person: they
read that a note is unreadable, write something down on the strength of it, and the sentence was
wrong. The second harms no data at all — and it spends the only asset this section has. Its job is
to be believed. A reader who catches one false sentence here has no way to grade the rest, so the
page reverts to something they have to take on trust, which is the thing it was written to replace.

**The direction that costs nothing is the direction that survives, and that is the practical
warning.** A sentence saying the operator reads more than it can is refuted by nothing: no test
fails, no constraint fires, and nobody writes in to complain that a product undersold its own
privacy. It is found by somebody rereading the screen against the schema, and it is found late. So
the rule is not *err toward exposure*. It is that **every sentence here is measured against what
the columns hold on the day it is written**, and that this section is re-read every time a
capability lands — [voice](voice.md)'s re-check rule, which two other sentences on this screen have
already been narrowed by.

### What a writer will get wrong

- **Turning the second paragraph into a feature claim.** *End-to-end encrypted*, *zero knowledge*,
  *military-grade* — each is a term with a definition the reader cannot check, which is the
  opposite of what this section is for, and each reads as a badge rather than a fact.
- **Naming the algorithm, the route or the column.** A person reading this is deciding whether to
  trust the product, not implementing it. The mechanism is in the business-logic chapters linked
  above, where a second implementer can read it.
- **Dropping the length clause or the third paragraph** because they complicate the good news. Each
  is a small true fact whose absence, once somebody finds it, costs the section everything the rest
  of its sentences were worth.
- **Making any of it conditional on this tab.** The section is standing prose and is true whether
  the account's keys are held or not: what the operator can read does not change when somebody
  presses Unlock. *Until you unlock, we can’t…* is a sentence about the browser wearing a sentence
  about the operator.
- **Introducing an attacker.** The subject here is the people running the service and what they
  hold. A clause about somebody stealing the database changes the subject, and it is alarm this
  section has not measured — the reader can draw that conclusion from what is written, and drawing
  it for them is the same overreach in the other direction.
- **Adding a control, a link or a live region.** Nothing in this section is read from the network
  and nothing in it can be acted on here; the two acts it might prompt are the two controls
  immediately below it.

### Anatomy

- A settings section per the spec above: `<section aria-labelledby>`, `eyebrow` heading **What we
  can read**, `--bud-space-4` between heading and content, `--bud-space-7` to the next section,
  prose capped at 65ch.
- Three `body` `--bud-text` paragraphs, `--bud-space-4` between them, in reading order. No list, no
  card, no table: the first paragraph's inventory is a sentence a person reads through, and set as
  bullets it becomes a specification they are expected to audit.
- **No `role="status"` region.** Every other section on this screen has one because it renders a
  value that arrives from the network; this one renders nothing that can arrive, change or fail.
- **No control.** The rule that a disabled control gets a sentence beside it does not reach here,
  because there is no control to explain.

### Accessibility

Heading level `h2` under the screen's one `h1`; no level skipped. Ordinary content in reading
order, hung on nothing by a `title`, a tooltip or an `aria-describedby`. Nothing is communicated by
colour — the section carries no accent and reads identically without one. No target rule applies:
there is nothing to press.

### What ships today

**The section is on `/app/settings` under its specified heading, its copy is the copy above, and
its position is not.** The placement is a departure this chapter names as work rather than
describes as the design.

**The copy.** `settings.component.html` renders all three paragraphs, character for character and
in the specified order: the inventory, the names-and-notes paragraph with its length clause, and
the paragraph about the short code beside each name. Nothing in the template invents copy, and
none of the three is summarised, shortened or split.

**The position.** It renders below Erase, where the placement rule puts it above Export. Moving it
is one section's worth of work and changes nothing else on the screen.

## The locked account

**A locked account and a locked session are two different things, and the screens may not borrow
each other's words.** A locked *session* is a server fact — the row a federated credential opens,
reaching exactly one route ([sessions.md](../business-logic/sessions.md)). A locked *account* is a
browser fact: the session is live, every request is answered, and the words that come back cannot be
read because this tab holds no content key. Only the second is what this chapter renders.
[account-keys.md](../business-logic/account-keys.md) owns the rule; this chapter renders it.

**A reload locks the account, and that is the design rather than a gap in it.** The two keys sit on
private fields of one root-provided service and are persisted nowhere — not to IndexedDB, not across
a `BroadcastChannel` — each refusal argued where the service is. So closing the tab ends this
browser's ability to read the account back, and the way in is one press: **Unlock**, in the Account
keys section of Settings.

### Two components, and why the marker is not inlined

**`narrative-value` renders one value; `locked-account-notice` explains one screen.** They are two
because they answer two different questions and change for different reasons — a value that failed
to open is a fact about that value, and a locked account is a fact about the tab.

The marker is its own component rather than three lines of template repeated per screen because the
em-dash rule would otherwise exist in four copies across three screens, and **the first copy to
drift is invisible**: a marker that renders the right dash with the wrong accessible name looks
identical to a sighted reviewer. One component, one pair of names, one place to change them.

### `narrative-value` — four renders, and none of them is a substitute

A narrative value arrives in one of four shapes and each gets its own render. The rule is the same
one [A value read from the network](#a-value-read-from-the-network) states about clearing: what a
section shows when it has no answer is never a stand-in for an answer it might have had.

- **text** — the value, rendered plainly. An empty string is text and renders as an **empty
  element**, not as nothing; it is a note somebody cleared, not a note nobody wrote.
- **unreadable** — an em dash whose accessible name is **`Couldn’t be read`**. The value is this
  account's and the key is present; these particular bytes did not authenticate.
- **locked** — an em dash whose accessible name is **`Locked`**. Nothing was attempted; this tab has
  no key.
- **absent** (the column held nothing) — nothing at all, no dash. There is no value here to be
  unable to show.

**The empty string and the absent column hold each other up, and neither case holds alone.**
Rendering nothing for `''` and rendering an empty element for `null` each redden their own case and
only their own — measured, both ways. The phrase to keep out of a reader's head is *renders
nothing*: an empty element is nothing **visible**, and it is not nothing.

**The name rides `role="img"` plus `aria-label` on the dash itself, and both dashes are one element
in the template.** The role is not decoration: `aria-label` on a bare `<span>` sits on a generic
element where a name is not required to be exposed at all, and the role is what turns the label into
the accessible name while the glyph inside goes unread. One element rather than one per state is
what makes *colour is never the message* structural here — there is no second class for a stylesheet
to reach for. The cost, recorded rather than hidden: some screen readers announce "image" ahead of
the name. The alternative — an `aria-hidden` glyph beside visually-hidden text — buys a cleaner
announcement and puts the word into `textContent`, so it would be copied out with any row. The
choice stands until a screen-reader pass says otherwise, and that pass is work this chapter waits
on.

**The two dashes carry different accessible names on purpose.** They look identical and they are
not: one says the app could not read something it should have been able to read, the other says it
did not try. Collapsing them tells a screen-reader user that their data is damaged when the remedy
is a single press.

**A mapper may never collapse these into `''` or `'—'` on the way here.** The moment a `locked`
becomes an empty string, the screen renders "no description" as a claim about the account rather
than about the tab, and nothing downstream can tell the two apart again.

### `locked-account-notice` — a link, never a redirect

Rendered by each content screen **in place of its list**, and by nothing else. The copy is the
blocked-action pattern from [voice](voice.md): the fact, then the way forward, and the way forward
is the smallest act that clears the block — *This tab can’t read your account yet. Unlock it in
Settings.* — with **Settings** a `routerLink` to `/app/settings`.

**Two states send it, and it renders a different sentence for each.** One input names which —
`locked`, or `rotating` while a key rotation is in flight — and the
[key-rotation](#key-rotation-section) chapter owns the second, both the copy and the reason a run
takes the lists away. **The input is the whole of what changes here.** The component still injects
nothing, reads no status and calls no `Router`: the screen already computes which state it is in,
and passing a word is not reaching for one. That is what keeps *it cannot navigate* structural
rather than remembered.

**It links and does not navigate.** Three shapes were considered and two refused:

- **A route guard** — refused. It would be synchronous against a fact with no resolution on the
  navigation path, so it could only bounce every reload, and it would put key state where
  `AccountUnlockService` is built to keep it out of.
- **Swapping the shell's outlet** — refused, and it is the tempting one because it is a single
  change covering every screen. It would lock Settings too, and Settings holds Unlock: the one
  screen that must stay reachable is the one this shape takes away.
- **A notice each screen renders itself**, which is what ships. Three call sites is the price of the
  way out staying open.

### The form is disabled in the DOM, with the reason beside it

Each screen's create/edit form is **disabled**, not merely hidden or visually dimmed. An enabled
form submits, the service refuses because it cannot seal, and nothing happens — which is worse than
a control that is plainly off, because the person cannot tell a limitation from a failure.

Material's click-halt is anchors only, so a `<button>` left `disabledInteractive` still receives the
click; the form is disabled through the form itself. This is the same rule the recovery-code
hand-off states about its acknowledgement gate, and it has been got wrong once already on this
codebase.

**The reason is a sentence beside the form, never a bare disabled control** — the *Not built yet*
pattern in [voice](voice.md), except that this is not "not built": it is a capability the tab has
temporarily lost and can get back. The sentence says so, and it names the press that returns it
**only where there is one to name**.

| State | Copy |
| --- | --- |
| `locked` | "Adding and editing are off while this tab can’t read your account. Press Unlock in Settings to turn them back on." |
| `rotating` | "Adding and editing are off while Budgetoid gives this account new keys. They come back when it finishes." |

The copy is the specification, not an example of it. **On the transactions screen, which has no
edit, both lines open *Adding is off* and their pronouns follow** — *turn it back on*, and *It comes
back when it finishes*. The singular is not a detail to be normalised away later: a screen that
offers one act and says two are off is describing a different screen.

**The second sentence exists because the first one's advice is false during a run**, and it names no
press for the notice's reason: the smallest act that clears this block is waiting. That is the whole
difference between the two — one is a capability this tab can take back in a single press, and the
other is one the account is in the middle of rebuilding, where the press that would help does not
exist and the one that looks like it would does nothing.

**Both lines were shipping before either was written down here.** The locked one has been on three
screens since the locked treatment was built and appeared in no table; the run's was written when
the run reached those screens. Recording a departure for the second while the first sat unspecified
would have been the wrong repair — the book states what a surface *shall* say, so the answer to a
string nobody specified is to specify it.

**A disabled form is excluded from validation, so a control gated on validity alone comes back to
life exactly when it should not.** Angular's status becomes the third value `DISABLED`, and **both**
`valid` and `invalid` then answer false — so `[disabled]="form.invalid"` on a submit button *enables*
it the moment the form is switched off. The lock condition therefore has to be named a second time
on the control and a third time in the handler. Measured. Note the mechanism rather than the
shorthand: the form does not call itself *valid*, which is the easier sentence to remember and the
wrong one to reason from — a control gated on `form.valid` stays off, and one gated on `form.invalid`
comes on. It will read as belt-and-braces on every screen that copies it, and it is not.

### A control holding an opened value leaves the DOM; disabling it is not enough

A disabled control still renders what it holds, and on these screens what it holds is decrypted.
**Measured on `mat-select`: emptying its option list does not clear the trigger** — it goes on
rendering the selected option's text after the option is gone. So the account select, the payee
autocomplete and the category picker are behind the same predicate as the list, not merely disabled
beside it.

This is the rule the notice already states, applied one level in: the notice renders **in place of**
anything showing account content, and a form control holding an opened name is account content.

**Four controls leave the DOM, and a fifth thing happens instead.** The transaction form's account
select, payee autocomplete and category picker; the category form's group picker. Those four hold an
opened name at rest, so the notice replaces them.

**A prefilled form leaves edit mode and its values come down.** `/app/accounts` and both halves of
`/app/categories` fill a form from a decrypted row when somebody presses Edit — a name, a note, and
on accounts an opening **balance**, which is the value FR-065 names out loud. Those controls are
ordinary text and number inputs, so nothing about their *shape* says they are holding account
content, and they sit outside the guard the list is behind. A lock landing mid-edit therefore used
to leave a decrypted name and a balance on screen, disabled, beside a notice saying this tab cannot
read the account. It ends the edit and clears what the edit put there instead.

**What comes down is what the edit put there, not what somebody typed.** Text typed into a *create*
survives, exactly as the transaction form's amount, date and description survive — they are the
person's own words rather than the account's, and no key was needed to show them. The two screens
would otherwise disagree about whose text it is.

**The predicate here is `locked` exactly, and it is the one place that reasoning inverts.** Elsewhere
a form follows "anything but `unlocked`", because leaving a control live by mistake is silent and
switching it off by mistake is loud. This action is **destructive**: a discarded edit is somebody's
work and no later state gives it back, so the fail-safe direction is not to act. An `unlocking`
ceremony ends in keys; an edit thrown away mid-ceremony is paid for nothing.

**A key rotation is deliberately not a term here**, though it is a term on the two predicates below.
A run ends in keys the way a ceremony does, and it ends in the *same words* — the plaintext a
prefill holds is what the run re-seals, not something it invalidates — so an edit that survives a
run is an edit somebody can still save when the form comes back. Clearing it would throw work away
to tidy a screen, which is the trade this section refuses.

### Two predicates, failing safe in opposite directions

The account key status is three-valued — `locked`, `unlocking`, `unlocked` — and the screen reads it
**twice**, for two different questions. Folding them into one predicate makes one of two mistakes
unavoidable.

- **The form is usable only when the status is `unlocked`.** Written positively, so `locked`,
  `unlocking` and any state added later all arrive **disabled**. A state nobody has thought about
  yet must be inert and visible, never live and silent.
- **The notice renders on `locked` alone, of custody's three values.** Its sentence is *advice* —
  press Unlock in Settings — and that advice is already false for somebody whose unlock is running. So during `unlocking` the
  list stays where it is, **still showing the words it opened before the ceremony began**. The
  services drop their opened lists on `locked` exactly, for the same reason: an `unlocking` resolves
  back into keys, and blanking a screen somebody is reading in order to fill it again seconds later
  buys nothing. A `locked` marker appears in that list only where a read *started* during the
  ceremony had nothing to open with — a statement about one value rather than advice about the
  account, which is why it cannot go stale the way the notice would.

That is the whole distinction: **disable when unsure, but do not advise when unsure.** Written
`!== 'locked'` the form goes live mid-ceremony; written `!== 'unlocked'` the notice tells somebody
to press a button they are already holding down.

**A key rotation in flight is a second term on both predicates and changes the shape of neither.**
The form is usable when custody says `unlocked` **and** no run is in flight; the notice renders when
custody says `locked` **or** a run is in flight, carrying the run's sentence when both are true. The
rule survives the addition because each term was written in the direction its own mistake is
audible, and the new one is no different: a form left live during a run offers somebody a way to
make their own rotation fail, and Unlock advice given during a run names a control that cannot
help. The [key-rotation](#key-rotation-section) chapter argues why a run takes the lists away at
all; this chapter is where the predicates live.

`unlocking` is **unreachable from this route today** — the ceremony runs from Settings and there is
one tab — so the two cases naming it are the only thing keeping the split alive.

### A name that cannot be read cannot be renamed — but it can still be deleted

Edit is disabled on any row whose name is not `text`, with the reason in the row itself rather than
in a tooltip, because a tooltip is an explanation nobody hears. Prefilling the field with an empty
value and letting the person save would overwrite a name they cannot see with a blank: that is not
an edit, it is a deletion wearing an edit's clothes.

**Delete stays available**, and the asymmetry is the point. Somebody looking at a row they cannot
read may still decide it should not exist; removing a row is not rewriting its contents, and
refusing both would strand every unreadable row permanently.

On this screen the handler's half of that gate is held by the **compiler** rather than by a test —
reading the value off a `NarrativeText` does not type-check until the state has been narrowed — so
the mutation removing it does not compile. That is stronger than a red bar and it is worth knowing
it is what is holding the rule, because the next screen may not get it for free.

### Ordering falls out, and is not special-cased

Lists sort through `compareNarrative`: opened text first, then unreadable, then locked. **Which
lists those are is narrower than that sentence sounds** — the accounts list and the payee list
behind the transaction form's suggestions order by name through it, while the categories and their
groups order on `position`, a plaintext column their owner arranges by hand, and must never be
"corrected" into sorting by name. On a locked account every value is `locked`, every comparison
answers 0, and a stable sort leaves the order the rows arrived in. That is the correct behaviour and
it is a **consequence** of the ordering rather than a branch anybody wrote. Do not add a "if locked,
skip sorting" case; there is nothing for it to do.

**One function does it for every screen, and it is not a comparator each service keeps a copy of.**
`sortByNarrativeName` takes a list and the accessor naming which of a row's words it is ordered by,
and it is the only caller `compareNarrative` has outside its own spec. A copy per service is a place
a defect can hide from its twin's cases; one function is covered by whichever of its callers has the
better one.

Two things about the comparison, written down and deliberately not fixed. Two opened names compare
with `localeCompare`, which reads the host's locale, and nothing in this app provides `LOCALE_ID`.
And they compare **trimmed** — surrounding whitespace is a *primary* collation difference, so
without that step a name typed with a space in front of it sits above every other name on the screen
while rendering identically to its neighbours. The trim is presentation and reaches nothing stored:
what a screen seals is still what somebody typed, character for character.

### Accessibility

- Neither component is a live region. The locked state is the answer to a navigation the person
  made, not an event that arrived — and a list of ten markers in a `role="status"` would narrate ten
  em dashes as news.
- The notice is ordinary content in the region the list would have occupied, so it lands in reading
  order where the reader is already looking.
- Colour is never the message: both markers and the notice read the same with every accent removed.
- The disabled form keeps its labels and its reason in the accessibility tree; a disabled control
  whose explanation is a tooltip is an explanation nobody hears.

### Two things the specs do not hold, measured rather than assumed

Both are recorded here because a chapter that states a rule and names no gate for it is how an
unenforced rule survives review.

**Colour being the message is not gated by anything.** The markers are one element with one class,
so a *second class* on one state reddens — but a stylesheet reaching the state through
`.nv-marker[aria-label="Locked"] { color: … }` leaves every assertion green: same tag, same class,
same text. Neither spec reads a computed style. The structural choice above is what makes this
unlikely rather than impossible, and closing it needs a new assertion — a source-text pin over the
stylesheet, the shape `key-import-single-source.spec.ts` already uses — not another mutation.

**The accessible name is checked by proxy.** jsdom has no accessibility tree, so the specs assert
`role` and `aria-label` and infer the name from them. Measured: wrapping the marker in an
`aria-hidden` ancestor leaves all four assertions green while the name reaches nobody. Only a
screen-reader pass closes that, and it is the same pass the `role="img"` decision above is waiting
on.

### The book has no link style, and this component invented one

`accessibility.md` knows inline links in prose exist — it exempts them from the 48px target — and
`typography.md` gives "emphasized links" the `label` type style. **Neither says what an inline text
link looks like**: no colour, no decoration, no states. The notice's Settings link therefore ships
`--bud-accent-text` with an underline, chosen so the affordance survives every accent being removed,
and that choice was made here rather than read from anywhere.

It is recorded as a gap and not quietly adopted as a rule, because the next screen that needs a link
will invent a second one. Specifying the inline link — anatomy, states, and how it differs from a
Ghost button — is work this book owes.

### What ships today

**Every narrative screen seals what it writes and opens what it reads** — `/app/accounts`, the
transaction form and both halves of `/app/categories` — so both components in this chapter are
reached in earnest and a reload leaves names unreadable until Unlock. **The notice takes one input
and renders both sentences**, and all three screens carry the key-rotation term on both predicates:
the form is usable on `unlocked` **and** no run, the notice renders on `locked` **or** a run, and
the run's sentence is the one that renders when both are true. The Account keys
section above says the same thing from the other end: being locked costs something anybody can see,
and its control is the only way out of the state, which is why nothing may put that control behind
one.

One consequence worth knowing before trusting a green bar: `tsconfig.app.json` is `files:
["src/main.ts"]`, so `npm run build` compiles only what `main.ts` reaches and says **nothing** about
a file no screen imports. Measured — a mutation that fails `npm test` to compile exits 0 on the
build. `npm test` is the gate that compiles specs, and `npx ng lint` is the only one of the three
that sees an unused import or a missing return type.

## The welcome screen

The one public surface, at `/welcome`, and the one screen written in marketing voice
([voice](voice.md)). It states what the product is for and then offers the two ways into an account —
and from the commit that gave it the second one, neither of them is the identity provider.

- **One `h1`, and it is the hero line.** The screen carried an `<h2>` wearing the hero type style's
  name, which left the first page anybody meets with no top-level heading at all. The element
  changes; the class stays, because the class is what carries the type scale.
- **Two calls to action and exactly one Primary.** **Create account** is the Primary and goes to the
  registration flow. **Sign in with a passkey** is Outline and runs the assertion ceremony against
  this product's own API. Two filled buttons side by side would ask a person to choose between two
  things the design has already decided between: the screen is selling the first, and somebody
  returning is looking for a control rather than being persuaded by one. Both are verbs in sentence
  case, and both clear the 48px target.
- **There is no `/sign-in` route.** The screen carries one control and no fields, because **the
  authenticator is the form** — there is nothing to type, so there is no page to type it on. A
  sign-in *address* is the reflex carried over from password screens, where the second page exists
  to hold the two fields, and here it would buy a URL for a ceremony that takes no input while
  costing two things: a second entry point into one assertion, and the single refusal sentence
  below split across two screens that would then have to be kept saying the same thing. The
  absence is written down because an absence cannot be found by grep — a reader looking for the
  sign-in page finds nothing, and nothing reads like an oversight.
- **Mobile first**: the two stack full width in one grid column and sit side by side, each at its
  label's width, from the 600px query this screen already uses.
- **The outcome of a sign-in lands in one `role="status"` region, in the DOM from first paint and
  empty at rest.** It is the "value read from the network" rule above applied to an act rather than a
  read: the waiting line and the sentence that follows it share one region, so the second replaces
  the first instead of stacking under it. `status` and never `alert` — nothing is typed here.
- **Busy keeps the pressed control in place.** `disabledInteractive` while the ceremony runs, for the
  buttons chapter's busy reason, with the live region saying why it cannot be pressed. The gate is in
  the flow's own method as well, because Material's click-halt is applied to anchors only.
- **Every refusal from the server says one thing, and it names no cause.** The route answers one
  fixed `401` for an unknown credential, a bad signature, an untrusted origin, a spent challenge, a
  counter regression and a user-handle mismatch alike, so that nobody can discover which handles are
  registered. A screen that rendered a cause it was handed would put that oracle back in front of the
  person. A refusal and an answer that never came are still different sentences: one says this
  passkey does not work here and points at another way in, the other says the server could not be
  reached and points at the same press a minute later.
- **The provider line says what Google is for, in three facts and in this order**: an account starts
  there, it happens once and only to check an address, and signing in afterwards never goes near it.
  The order is the reassurance. The sentence it replaced — that the Google account is what signs you
  in — stopped being true the moment the passkey control landed, and leaving it would have been worse
  than leaving nothing: it is the sentence a cautious person reads before deciding whether to hand
  over an address at all.
- **The kinetic sentence is unchanged** and remains `aria-live="off"` decorative narrative
  ([motion](motion.md), [accessibility](accessibility.md)).

### What ships today

All of the above. What does **not** ship is a way back in for somebody holding no passkey: redeeming
a recovery code has no surface anywhere in the app, so this screen offers no third control and says
nothing about one.

## Registration

The flow that creates an account: three steps behind one address, ending in a session and the app.
It is the only surface in the product that holds the account's keys in the clear, and the only one
that shows a secret.

### The flow shell

One screen at `/register`, which turns away anybody already holding a session — there is nothing
here for them, and reaching the second step would spend a challenge and a passkey to find that out.
The identity provider's redirect lands on this address, so a person who pressed **Continue with
Google** comes back to the screen that uses what they consented to.

- **The steps are not routes and get no URL of their own.** A step is in-memory state — the account
  keys, the ten codes and the eleven sealed envelopes live in a service the screen provides — so
  `/register/codes` would be an address whose state is already gone, and a link somebody could be
  sent to a screen whose whole premise is that ten codes were minted moments ago. One URL; the step
  is a signal behind it.
- **The shell owns the page and nothing inside it.** It sets the surface a short step sits on —
  background, ink, `min-height: 100dvh` — and adds **no padding of its own**, because each step sets
  its own inset.
- **Nothing asks on the way out.** No confirmation dialog and no unload prompt. Until the last press
  nothing has been created and the ten codes on screen are inert verifiers no server has ever seen,
  so a dialog would imply the opposite of what is true. Every step carries the standing line
  **"Nothing is saved until the last step."** instead, which is what removes the reason to be afraid
  of leaving.
- **One `h1` per step, and never one on the shell.** A heading in both places gives every step two
  competing titles; a heading on the shell alone leaves each step titled by the one before it.

### The step caption

Every step opens with **`Step n of 3`** — `caption`, `--bud-text-muted` — above its `h1`, the two
set as one block at `--bud-space-2` rather than at the step's own `--bud-space-6` rhythm, so a line
does not float away from the title it belongs to.

**The caption belongs to the step and not to the shell**, and that is structural rather than a
preference: each step's `:host` owns its inset and the shell adds none, so a caption rendered up
there would sit outside every step's padding and align with nothing on the screen. By the third step
it is also doing more than orientation — somebody being asked to transcribe ten 26-character strings
by hand cannot answer *is there more of this afterwards?* from anything else on the display.

### Step 1 — the introduction

The step that asks whether this account may exist at all. Title **Create your Budgetoid account**.

**It used to ask for nothing, and that is the whole of this step's design.** The options leg refuses
when the provider identity already holds an account, and it refuses *above* its own challenge — so
the answer exists at the first press. Read at the second one, which is where step 2 used to read it,
somebody who already has an account is shown the address their account will be created under,
presses **Continue**, reads a screen about authenticators, presses again, and only then is told no.
A promise made in the product's own words and broken two screens later.

- **Holding a provider token**, it shows the asserted address back — `body`, with the address itself
  at weight 600, tabular figures and `overflow-wrap: anywhere` for a long one on a 320px screen —
  then one line naming what the next two steps are, then one Primary **Continue**. A person with a
  personal Google account and a work one has no other way to learn which of them this browser is
  still signed in to, and the cost of guessing wrong is not a wasted click: the account that results
  is bound to whichever address was asserted.
- **The press is a request, so this step has a busy state and two refusals**, on the terms the
  passkey step's region sets and which are not restated here: one `role="status"`, in the DOM from
  first paint, empty at rest, carrying the busy line and the refusal alike. While the request is
  out, the Primary is held exactly where it was with `disabledInteractive` and the region reads
  **"Checking your account."** The line is this step's own: "Waiting for your device." is the passkey
  step's, and is false here because no authenticator has been asked for anything.
- **A conflict takes the promise down with it.** "Your account will be created under &lt;address&gt;"
  is false the moment the server says that address already holds an account, and a promise standing
  beside its own refusal is exactly the defect this step was changed to remove — one screen earlier,
  not one screen later. The lead becomes **"This browser is signed in to Google as
  &lt;address&gt;."**, and the address stays, because the refusal says *this Google address* and a
  sentence pointing at nothing is worse than the promise was. A server that never answered leaves the
  promise standing, because it said nothing about the address.
- **A browser that cannot run a passkey ceremony asks for nothing and moves on anyway.** It gets no
  refusal here and meets its own on step 2. A challenge is a nonce the server persisted, and on this
  route it is also the value the account identifier is derived from, so spending one for a browser
  that was never going to finish is the cost this rule refuses. Learning about a conflict would not
  repay it either: the way out of a conflict is a passkey assertion, which needs the same WebAuthn
  this browser does not have.
- **Holding no *usable* token**, it shows no address and no **Continue**. One line saying this
  browser is not holding a Google address, and an **Outline** **Continue with Google** — the
  treatment the book gives that control wherever it appears. **The test is validity, not
  presence**: a token whose hour has run out is read as no token at all, so a browser reloading the
  screen after lunch lands here rather than being shown a promise its next press cannot keep. A
  browser arrives in this state routinely: a bookmark, a reload an hour later, an exchange that
  never completed. A **Continue** from there would reach a refusal with nothing useful to say about
  why.

**The provider control appears in three places and is one specification.** This arm, the refusal
below when a token is rejected mid-visit, and the same refusal on step 2. There is no shared
component behind it — the welcome screen carries no provider button, so there is no other screen's
copy to match and no sentence about meeting a control already pressed — but the name, the Outline
treatment and the act are the same in all three, because what the person has to do is the same and
only how they arrived differs.

The three sentences are the specification. None is a copy of the passkey step's word for the same
outcome: that one ends "no passkey was made", which is worth saying where a system sheet was on the
screen a moment ago and says nothing at all here, where no passkey was ever going to be made.

| Refusal | Copy | Offers another press |
| --- | --- | --- |
| The Google address already has an account | "An account already exists for this Google address. Nothing has been created — sign in from the Budgetoid home page instead." | No — **Go to sign in** instead |
| The server never answered | "Budgetoid couldn’t reach the server. Nothing has been created." | Yes — **Try again**, which is another **Continue** under a name that admits to being one |
| The provider token the request carried was rejected | "Your Google sign-in has expired. Nothing has been created — continue with Google and you’ll come straight back to this page." | No — **Continue with Google** instead |

**The third keeps the promise above it, and the second reason is why.** The server said nothing
about the address — it never read the request — so "your account will be created under this one" is
still what will happen once a fresh token carries it. Taking the promise down there would put the
only false sentence on the screen. And **Try again** is refused as a control precisely because it
would work: it would attach the same dead token and collect the same refusal, which is a way of
being told no twice.

### Step 2 — the passkey

The step that runs the ceremony, and the one screen in the flow with nine ways to end badly.
Title **Create your passkey**, then two lines: what the device is about to ask for, and that the same
authenticator holds the keys the records are locked with.

- **One `role="status"` region**, in the DOM from first paint, empty at rest, holding its box
  (`min-height: 1lh`) so the screen does not shift when a sentence arrives. It carries the busy line
  and the refusal alike, so the second **replaces** the first instead of stacking under it. `status`
  and never `alert`: nothing is typed on this screen, and every sentence that lands there is the
  outcome of an act the person asked for.
- **One control, under one of two names, and sometimes none at all.** At rest, a Primary **Create a
  passkey**. While the ceremony runs, the same Primary held exactly where it was with
  `disabledInteractive` — the *busy* case in the Buttons chapter, not the acknowledgement case — with
  **"Waiting for your device."** in the region above it. After a refusal worth another press, **Try
  again**.
- **Four of the nine offer no second press.** Whether a press could help is a property of the
  refusal rather than a default, and *no* is not a smaller version of *yes*: where the browser cannot
  run the ceremony, where the authenticator cannot derive the value the account's keys are wrapped
  under, where the account already exists, or where the provider token the request carried was
  rejected, leaving **Create a passkey** on the screen is a retry
  that does not admit to being one — it reads as a way forward, costs another system sheet or another
  request to disprove, and ends in the same sentence.
- **Two of those four carry a different control rather than none, and they are different doors**,
  which is the rule. *No way forward* and *nowhere to go* are not the same state, and neither is
  *the wrong place to go*. Two of the four are dead ends on this device and the copy says so. The
  conflict is not: the account exists, so there is somewhere to be, and the step renders a Primary
  **Go to sign in** to `/welcome` — the one address in this application
  that runs a passkey assertion. A sentence that names a door the screen does not have is the defect
  this control exists to remove, and the shell's own conflict readings already had to fix it once.
  The rejected token is not either, and **`/welcome` would be the wrong door for it**: that person
  has no account, so the assertion there answers the byte-identical 401 it answers every unknown
  credential with, and they would be told nothing twice. The exchange is the one act that changes
  the answer, so the control is an Outline **Continue with Google** — and the copy says it comes
  back to the *first* step, because the provider's `redirectUri` is `/register` and this screen is
  not where it lands.
  **Three screens now carry that control** — the introduction, this step and the shell — which is
  three sentences that must keep naming the same door. The third is the one that arrived last and the
  one this table's conflict row now describes a narrower case for: the introduction answers the
  ordinary conflict, and what reaches step 2 is the refetch a restart or a failed ceremony makes,
  which another tab or another device can have raced in the meantime.

The sentences are the specification, not an example of them. None is a synonym of another: folded
into one, the screen tells somebody whose browser cannot run WebAuthn at all to try again, and tells
somebody who simply closed the system sheet that their device is unsupported.

| Refusal | Copy | Offers another press |
| --- | --- | --- |
| The browser cannot run a ceremony | "This browser can’t create a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can." | No |
| The system sheet was closed, or timed out | "The passkey wasn’t created. Nothing has been saved, and nothing was sent — try again whenever you’re ready." | Yes |
| The authenticator already holds a credential it was asked to decline | "This device already holds a passkey Budgetoid can’t reuse. Try again with a different device or security key." | Yes |
| The device cannot hold the account's keys | "This device can’t hold your account’s keys, and Budgetoid won’t create an account it can’t lock. Try a different phone, laptop or security key." | No |
| The ceremony did not finish | "Your device didn’t finish creating the passkey. Nothing has been saved." | Yes |
| The server never issued a challenge | "Budgetoid couldn’t reach the server to start. Nothing has been saved." | Yes |
| The Google address already has an account | "An account already exists for this Google address. Nothing was created and no passkey was made — sign in from the Budgetoid home page instead." | No — **Go to sign in** instead |
| Something nobody predicted, between the challenge arriving and the codes being ready | "Budgetoid didn’t finish, and nothing has been saved. Try again." | Yes |
| The provider token the request carried was rejected | "Your Google sign-in has expired. Nothing has been saved — continue with Google and you’ll come back to the first step." | No — **Continue with Google** instead |

Five of them are worth reading twice. **Named rather than numbered**, because the ordinals this list
used to carry had already come apart from the table's order and each new refusal breaks them again.

- **The unreachable server and the rejected token are the two on this screen about the *server*
  rather than the device**, and the sentence has to say so: a person told their device failed will go
  and buy a security key for a problem a reload would have fixed. They are not variants of one
  another. The first is silence and another press is a real way forward; the second is an answer, and
  another press would attach the same dead token and collect the same refusal.
- **The declined credential is unreachable from this flow today** — the account-registration options
  leg sends an empty `excludeCredentials`, so there is nothing for an authenticator to decline
  against — and it is specified anyway, because a refusal the screen has no sentence for is a screen
  that says nothing at all. See [registration.md](../business-logic/registration.md).
- **The conflict is answered before the device is ever asked**, which is what its copy is allowed to
  promise. The options leg refuses a Google identity that already holds an account above its own
  challenge, so on this path no system sheet opens and no credential is left on the authenticator —
  and the sentence says both, because "no passkey was made" is the clause a person acts on. It must
  not be reworded into the shell's two conflict sentences: those say the ten codes just shown open
  nothing, and on this step no code has been minted at all.
- **The unpredicted failure shares a cause with the shell's "no answer" state and must not share its
  words.** Nothing has been posted on this step, so this sentence can say plainly that nothing was
  created; the shell's cannot, because there a request really did leave. Same failure, two screens,
  two truthful sentences.

### Step 3 — the codes

Specified in full in [the recovery-code hand-off](#the-recovery-code-hand-off) below. What the flow
adds around it is **two inputs and an output**: the ten codes it has just minted, whether the account
is being created right now, and the press that posts it.

**The wait is handed down rather than rendered by the shell**, and that is structural rather than a
preference. This step owns the only `role="status"` region on the screen, in the DOM from first paint
and empty at rest; a region the shell created at the moment it had something to say would be
announced unreliably or not at all — the failure the live-region rule above exists to prevent. An
input also keeps the step what it is: it still mints nothing, posts nothing, navigates nowhere, and
holds no reference to the flow.

### The four post-request states

The registration request answers in one of four ways, and three of those words **replace the codes
step** rather than sitting under it. There are four states behind the three words, because the
conflict is read two ways. Each carries **its own `h1`** — a document with the codes gone and no
heading of its own is a document titled by a step that is no longer on it — and the ten codes leave
the screen in all four.

| State | Copy | Control |
| --- | --- | --- |
| Refused | **Registration was refused** — "Your account wasn’t created and nothing was saved. The ten codes you were just shown open nothing — start again to get a new set." | Primary **Start again** |
| An account already exists, and no earlier request went unanswered | **You already have an account** — "An account already exists for this Google address. Nothing was created here, and the ten codes you were just shown open nothing — sign in from the Budgetoid home page instead." | Primary **Go to sign in** |
| An account already exists, after an earlier request went unanswered | **Your first attempt worked** — "Your first attempt did create your account — its answer just didn’t reach this browser. Sign in with the passkey you made on that attempt. The ten codes you were shown a moment ago open nothing; the ten from the first attempt are the ones that work." | Primary **Go to sign in** |
| No answer came back | **Budgetoid didn’t hear back** — "Budgetoid didn’t get an answer, so we can’t tell you whether your account was created. Keep the ten codes you saved: if it was, they’re part of the only way back into it." | Primary **Start again** |

- **The first three say the ten codes on screen are dead; the last must never.** A judged request
  was read and left the server before a row was written, so saying those ten open nothing is a
  kindness — it tells somebody to throw away a piece of paper that is worthless, and it stops ten
  worthless secrets standing in front of a person about to be handed ten real ones. The third state
  says it of the codes on screen while naming an *earlier* ten as the live ones, which is the same
  claim about the same request. A request that got **no answer**
  says nothing about whether the account exists: it may have arrived, committed and lost its
  response. Telling that person their codes are worthless tells them to discard the only key to an
  account they cannot make more codes for. The two sentences are the requirement; collapsing them is
  the defect.
- **The two conflicts are the same status code and opposite facts**, and the screen tells them apart
  by **what the previous registration request ended as** — never by whether a button was pressed. The
  server sends four distinct sentences under one identical title, so the copy above must never be
  chosen by matching the server's text. It also sends a `conflictKind` member naming which of the
  four it is, and **this screen does not read it yet** — that is a gap and named as one here, not a
  reason to reach for the prose. What the client does know
  is how its own earlier POST ended, and only one of those endings leaves the question open: a lost
  answer. A `400` and a `409` are judgements — the server looked and said no, and every one of those
  paths leaves the handler before a row is written — so neither of them ever opens it. With no
  earlier unanswered request the 409 is a stranger at the front door and nothing was created here.
  After one it is the person's *own* first attempt answering: that POST committed and lost its 201,
  so the account exists, the passkey that opens it is the first attempt's, and the live codes are the
  first attempt's ten. Showing the first sentence there is false on every clause and costs the
  account — somebody who throws the first card away holds a passkey, no codes, and no way to make
  more.
  - **Forking on the press was the defect, and the shape of it is worth keeping.** **Start again** is
    offered from two states, so *refused → start again → 409* rendered "Your first attempt worked"
    at somebody whose first attempt had created nothing, and sent them to sign in with a passkey the
    server never saw — met by a byte-identical 401 that names no cause. The reading is also
    **one-directional**: once a request has gone unanswered, nothing later closes the question, and
    an account that may exist does not stop existing because the request after it was refused.
- **Both conflicts carry one control, and it is rendered outside the fork.** A single Primary **Go to
  sign in**, routing to `/welcome`, which is the one screen in the product that runs a passkey
  assertion — exactly what both readings need, since one has a passkey from an earlier visit and the
  other has the one their first attempt registered. Outside the fork on purpose: the branch that most
  needs the control is the one a reader is least likely to be looking at, and a control written twice
  is a control somebody forgets once.
  - **A button, not an `<a routerLink>`.** What a link buys is opening the address somewhere else,
    and doing that from here leaves this dead end standing in the old tab with ten worthless codes on
    it. This is a way *out* of a flow that has ended, of the same kind as the **Start again** its
    neighbours offer — not navigation somebody might want beside what they are reading.
  - **Start again is still absent from both**, and that argument is unchanged: it spends another
    challenge and another passkey to meet the same 409. It reaches "not that control", never "no
    control" — which is what left both branches telling a person to go somewhere with nothing to
    press, on a route that carries no navigation of its own.
- **Start again re-draws everything**: a new challenge, a new passkey, new account keys, ten new
  codes and eleven new factor identifiers. It returns to step 2, and the set on screen when the flow
  next reaches step 3 is a different set.
- **There is no Retry, and there may not be one.** The challenge is consumed before anything is
  verified, so re-sending the same body is a guaranteed refusal — a control that looks like a way out
  and is only a way to be told no twice. See [registration.md](../business-logic/registration.md).

### Accessibility

One `h1` on every step and on each of the four post-request states; no level skipped. Both live
regions are `role="status"`, polite, in the DOM from first paint and empty at rest — and the ten
codes are never in one, per the hand-off chapter. Colour is never the message: every refusal reads
the same sentence with `--bud-over` removed. Every control is a 48px target.

### What ships today

The whole of the above renders: three steps, the caption, both branches of the introduction, every
passkey sentence with its control, the hand-off with its busy state, and all four post-request
states with their controls. The flow runs the ceremony, draws the account keys, mints the card and
eleven factor identifiers, wraps every factor's copy, posts the account, publishes the session and
hands the person to `/app`. **Nothing on this screen reads whether a step was restarted** — the one
place two opposite sentences had to be told apart reads how the previous request ended instead, and
a press is recorded nowhere.

**One departure, named rather than tidied away.** **No step says an attempt was abandoned**: after
**Start again**, nothing on the passkey or codes step tells a person that the ten codes they may
have written down a minute ago belong to nothing. Those two steps are where a person is still
standing rather than being told an outcome, and the sentence that would sit there is work rather
than a rule this chapter is retiring.

## The recovery-code hand-off

The one screen in the product that shows a secret, and the only time it is shown. Built as
`register/steps/codes-step.component`, driven by its inputs, and rendered as the last step of the
registration flow above — which mints the codes, hands them here, and posts the account when this
step raises its one output.

**The contract is two inputs and one output.** The ten codes are **required**: an empty default would
render a screen promising ten codes and showing none, and the failure would read as a styling problem
rather than as a flow that forgot to mint. Whether the account is being created right now is
**optional and defaults to false**, so the step renders its resting screen without being told. The
output carries nothing — the codes are already the caller's, and handing them back out would put a
second reference to ten secrets into a flow with no use for it.

### Anatomy

M3 base: **none**. A plain `<ol role="list">`, one `<li>` per code, `list-style: none`.

- **Ordered, and the numbers are printed.** Ten near-identical 26-character strings is a screen
  somebody loses their place in, and the number is what lets them put the pen down and say "seven of
  ten". Without a visible index the `<ol>` is a `<ul>` wearing a different tag.
- **The printed index is `aria-hidden`.** The list role already announces "7 of 10", so exposing the
  number as well reads the position twice before every code. The number is for eyes; the role is for
  ears; each says it once.
- **`role="list"` is written out**, because under `list-style: none` Safari drops list semantics and
  nobody hears the count at all.
- **Not a card, not a `MatList`.** These are ten values, not ten interactive rows, and a
  non-interactive line is not a 48px target.
- **Grouped `XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XX`.** Free, because `recovery-code-canonical.ts` strips
  hyphens and whitespace before anything derives from a code — so the grouping is presentation and
  the value is unchanged.
- **Inter, `tabular-nums`, loosened tracking.** There is no monospace family in this system and none
  may be added: a third face would have to be self-hosted and justified, for a string whose
  confusable characters the code alphabet already excludes. `tabular-nums` is what makes ten stacked
  codes align well enough to scan.
- `user-select: all` per code, so a person copying one by hand selects a whole code and not a
  fragment of it.
- One column; two from `min-width: 600px`; **never three** — a third column at desktop widths puts
  the codes far enough apart to lose the reading thread, and nothing is gained.

### The two ways out, and they are not equals

**Save to file** is Outline. **Copy** is **Ghost**, deliberately a step quieter: the clipboard is
the worst storage on the device, the sentence beside the button says so, and two equal-weight
controls would be telling the person the routes are equivalent while the copy underneath says they
are not.

The saved file is **bare** — the grouped codes and nothing else. No header, no caption, no product
name. A label inside a file of recovery codes names the secret for whoever finds the disk; the
filename pays that cost once already so somebody can find the file again, and the contents must not
pay it twice. The printed index is **not** in the file or on the clipboard either, which is the trap
the visible number creates: the obvious implementation builds the payload from the rendered line.

A **failed copy gets a sentence** — `navigator.clipboard.writeText` rejects routinely, on an
insecure origin, in an iframe, or in Safari without a user gesture — and the codes stay on screen
behind it. Silence after a press is the defect the export control next door already argues against.

### Announcements

**The codes are never in a live region**, and this is the rule most likely to be "fixed". A
`role="status"` holding a list narrates every entry as an event and puts ten secrets in a speech
buffer, for no gain: they are content, and content is read in reading order. One `role="status"`
region exists, in the DOM from first paint and empty at rest, and it carries the one-sentence
outcomes — `Copied.`, the copy failure, and the line saying the account is being created — and
nothing else. The download is the accessible route.

**The wait comes first and wins**, the way it does on the passkey step: the press that starts it is
the press that ends the flow, so `Saved.` left standing underneath would be the screen answering a
question nobody is asking any more. It is one region and not two, so the outcome of the last press
is always in one place and nobody has to know where to look for bad news as opposed to good.

### The acknowledgement

One required checkbox — M3 `MatCheckbox`, the first in this product — gating the Primary. **Not a
typed word**: the typed word is reserved for destroying data that exists now, and nothing here is
destroyed. This is a person accepting a future risk before anything is created at all.

**The consequence is its own block above the checkbox, never the checkbox's label.** A label is
announced as the control's name every time focus lands on it; a paragraph of consequence read that
way becomes noise the reader learns to skip, which is the opposite of what it is for.

The Primary is `disabledInteractive` until the box is ticked — the third disabled case in the
Buttons chapter — and the gate is repeated in the click handler, for the reason stated there.

### While the account is being created

**The press that ends this flow is the longest wait in the product** — roughly thirty rows across
nine relations in one save — so a screen that renders unchanged for several seconds reads as a
control that did nothing, which is what invites the second press. It is specified, not optional.

- **`Creating your account…`** lands in the region above, replacing whatever outcome was there.
- **The Primary becomes unavailable and keeps its place in the tab order** — the *busy* case in the
  Buttons chapter: `disabledInteractive` renders the unavailable appearance and sets `aria-disabled`
  while leaving the DOM `disabled` property false, so a keyboard user standing on the control is not
  dropped to `<body>` mid-press.
- **The label does not change.** The region says what is happening; a control that renames itself
  under the finger moves the answer somewhere a screen reader has to be told to go back to, and
  leaves the one press in this flow that matters with no stable name.
- **The codes stay exactly where they are.** Nothing is taken off the screen while the request is
  out: this is the last moment anybody can check a transcription against them, and the four states
  that replace this step arrive only once the request has answered.

The attribute is presentation here as everywhere else — the flow refuses a second press itself while
a request is outstanding, which is the layer that owns the question.

### Copy

> **Save your recovery codes**
>
> Your browser made these ten codes. Budgetoid never receives one, and this is the only time
> they’re shown.
>
> *(the ten codes)*
>
> Copying puts them on your clipboard, where other apps on this device can read them.
>
> Your passkey and these ten codes are the only ways into this account. Budgetoid keeps no copy of
> either, so if you lose the passkey and every code, everything you record here stays locked — to
> you, and to us. There’s no way back, and no one to ask.
>
> ☐ I’ve saved these codes somewhere I can get to them.
>
> Nothing is saved until the last step.

The last line stands on every step of registration: abandoning costs nothing, and the copy says so
rather than a dialog implying otherwise. The consequence block is the requirement that the person be
told, and be seen to accept, that losing every factor destroys the record — it is the reason the
checkbox exists and not the other way round.

### Accessibility

One `h1`. The three controls are 48px targets; the code lines are not targets and are not padded to
look like them. Nothing is communicated by colour alone — the copy failure reads the same with its
colour removed.

### What ships today

All of it, and the flow that reaches it: a person creating an account is shown ten codes on this
screen, saves or copies them, acknowledges the consequence, presses the control that creates the
account, and is told the account is being created while they wait. **This component still mints
nothing and posts nothing** — the codes and the wait arrive through its inputs and the press leaves
through its output, which is what stops a set being re-minted by every re-render of the step, and
what keeps the step holding no reference to the flow at all. The type styles above — Inter,
`tabular-nums`, the tracking — are the one part of this chapter no test holds: jsdom applies no
styles, and a bundle-reading spec was judged not worth it here.

## Text fields and selects

M3 base: `MatFormField` (outlined appearance), `MatInput`, `MatSelect`,
`MatAutocomplete`, `MatDatepicker`.

- Container: `--bud-surface`, 1px `--bud-hairline` border, `--bud-radius-sm`, min
  height 48px. Focus: border becomes 2px `--mat-sys-primary` (the field's focus is its
  border; no outer ring).
- Label and input text: Inter (`body`); floating label per M3 behavior, muted when
  resting.
- Error state: border and message in `--bud-over`, message text (`caption`) beneath the
  field — never color alone. Under a pointer the same parts take
  `--bud-over-on-container`, the deepened half of the same red ([color](color.md)).
- Dropdown and autocomplete panels are overlays: `--bud-overlay`,
  `--bud-shadow-overlay`, `--bud-radius-sm`, options 48px tall with state layers,
  selected option tinted `--bud-state-selected`.
- Forms cap at 560px and stack in a single grid column, `--bud-space-4` gaps.

**The error bullet is true of a field at rest, and a field under a pointer now takes the book's red
as well — the deepened on-colour of a pair rather than `--bud-over` itself.** It took two
declarations on the theme, because Material's form field reads **two** roles. Thirteen of its
eighteen error tokens fall back to `var(--mat-sys-error)` — the message, the caret, the active
indicator, the outline, the label and their focus states — all covered by aliasing that one role to
`--bud-over`. The other five are the hover states: `--mat-form-field-error-hover-*`,
`--mat-form-field-filled-error-hover-*` and `--mat-form-field-outlined-error-hover-*` fall back to
`var(--mat-sys-on-error-container)`, which no spelling of the first alias can reach. That role is
aliased too, to `--bud-over-on-container`. [color](color.md) owns the pair, its derivation and its
measured contrast; nothing about the values is restated here. Measured in `@angular/material`
21.2.14.

**The mechanism was never a token resolving to nothing, and a reader who learns the wrong one goes
looking for the wrong thing next time.** `mat.theme()` **emits**
`--mat-sys-on-error-container` from Material's own palette — `light-dark(#93000a, #ffdad6)` — so an
unaliased hover did not fall through to a blank: it moved the indicator and the label off
`--bud-over` and onto an actively declared **foreign** red. An absent mapping and somebody else's
mapping look identical on screen, and they are found by opposite investigations.

**Closing it was a decision this chapter could not take, and it was taken where it belongs.**
`--bud-*` carried no container role to alias — the semantic families in [color](color.md) are
single hues with a `-text` variant, not the background-and-on-colour pairs Material's container
roles are — so the way to close it was to mint one there, both themes, contrast measured against
the pairs the UI uses, and only then a second alias on the theme beside the first. That is what
happened, and the constraint outlives it: a theme file minting a value to satisfy a hover state
would still be taking a palette decision by accident.

**The suite sees the chain and not the colour.** `src/material-error-colour.spec.ts` renders a
refused field under Material's real stylesheet and walks all five hover declarations, asserting
each resolves through `--bud-over-on-container`; the declarations are **discovered** from the
stylesheets in the page rather than written into the spec, so a Material that renamed or moved them
leaves the scan empty and the case fails. Removing either override, misspelling a key or deleting
the token reddens it. What it cannot see is the hue: jsdom computes no colour, so Material's
`#93000a`, `#00ff00` or a nonsense string substituted for that token leaves the spec green.
Measured. The hexes and every ratio behind them are held by review, in [color](color.md). Two more
limits of the same kind: nothing compares `branding/tokens.css` with its runtime copy in
`_brand-tokens.scss`, so the two can disagree silently; and the hover case asks where a declaration
*points*, not which rule wins the cascade under a real pointer, so a later stylesheet overriding
Material's rule would pass.

## The amount field

The flagship control — the first thing focused in the entry flow.

- A `figure-hero` input (Inter 600 36, tabular), centered, on the entry surface — no
  visible box until focus (caret and a hairline underline suffice).
- Currency symbol as a muted `figure-lg` affix, placed per locale; placeholder `0.00`
  muted.
- `inputmode="decimal"`; sign is chosen by context (expense/income toggle in the entry
  flow), never typed.
- Validation is silent until submit: never interrupt digit entry with errors.

## Transaction row

The product's most repeated unit. M3 base: none — a plain semantic list.

- Two-line grid `[content 1fr] [figures auto]`, min-height 64px, padded by
  `--bud-gutter`, hairline separator (inset to the gutter), full row is one 48px+
  target.
- Line 1: payee (or description) — Inter 500 14, `--bud-text`; amount right —
  `figure` (15 tabular). Expense: plain ink, no sign. Income: `+` prefix,
  `--bud-positive-text`.
- Line 2: category · account — `body-sm` muted; date right — `caption` muted.
- Uncategorized shows "No category" muted — a plain fact, not a warning.
- Press: `--bud-state-pressed` layer. No swipe actions (transactions are immutable).

**Every word on this row arrives as ciphertext, and what opens it is the mapper rather than the
row.** `payees.name`, `accounts.name`, `categories.name`, `category_groups.name` and
`transactions.description` are all sealed columns, so `payeeName`, `accountName`, `categoryName`,
`categoryGroupName` and `description` on a transaction response are AEAD envelopes in base64url.
`toTransactionView` opens **all five**, each under the binding for its own table, column and **row
id** — which is why the response carries `payeeId`, `accountId`, `categoryId` and `categoryGroupId`
beside the names it joined, and why the transaction's own description is the single envelope on the
row bound to the transaction's **own** id. Opening a foreign name under this row's id authenticates
against nothing, permanently, with no error naming the cause.

**Three of the five reach the page, and that is a fact about the row and not about the mapper.**
The row draws the account's name and the category's name on line 2 and the counterparty's on line
1; it draws the **description only where there is no counterparty**, and it draws the **category
group never**. So a member can be opened, correct, and invisible — the sentence above is about what
is decrypted, and the bullets are about what is rendered. Do not read one as a description of the
other: a reader who takes "the screen opens each" for a list of what is on the row will go looking
for a note that is drawn nowhere.

Anything that does not open renders through [`narrative-value`](#the-locked-account) rather than as
empty text, so a value that failed and a value nobody typed stay distinguishable on the row.

**The list is still published once, and the read handing the frame back between chunks of opens is
not licence to draw a partial one.** What yielding buys is a page that keeps responding *while* the
opens run — see [frontend performance](../engineering/frontend-performance.md) — and not earlier
pixels: rows arriving in batches would show a screenful assembling itself out of order, and a
screenful is one snapshot or it is nothing.

**Two rules of this chapter are unaffected and worth saying so, because they will look like
casualties.** "Uncategorized shows *No category* muted" still holds: that branch turns on the
category being absent, which is a null the client can still see, not on reading a name. And a row
whose ciphertext the browser cannot open is the credential list's problem restated — a fact a row is
composed from has to be **total** over what a 200 can carry — so whatever the wiring does with a
value that fails to authenticate, it may not throw while composing the row.

**The row that ships is the row above.** Two lines in a `[content 1fr] [figures auto]` grid, 64px
minimum height, `--bud-gutter` padding and the hairline inset to it; the counterparty leads line 1
in `--bud-text` with the figure in the figures column; line 2 is category · account with the date
in its own figures slot. The figure goes through `Intl.NumberFormat` with `style: 'currency'` — an
expense unsigned in plain ink, income `+` and `--bud-positive-text`, a zero neither — so
[money display](patterns.md) is kept by the formatter rather than by a string this row assembles.
Nothing in this application provides `LOCALE_ID`, so the locale is the reader's own, read from an
injection token whose only other caller is a spec naming one: an expectation on a formatted figure
written without that seam is a claim about the machine the suite ran on. A currency code the
platform refuses raises a `RangeError` **inside change detection**, which abandons the render pass
and takes every section below the list with it, so the formatter falls back to a plain two-decimal
figure and keeps the sign rule.

**The lead falls through to the note on an *absent* counterparty and never on an unreadable one,
and those are two different facts the row keeps apart.** `payeeName` is `null` exactly where the
column held nothing, which this browser knows with no key at all. A row whose counterparty name
failed to open still *has* a counterparty, so it leads with the unreadable marker and not with the
note — falling through there would put a different value under one heading depending on whether a
key happened to be held, with nothing on screen saying which arrived. It is the same split the
uncategorized rule makes one line down, which turns on `categoryId` and never on the name, and a
spec case holds each half.

**Line 2 reads "No category · account", in that order.** The uncategorized fact takes the
category's place in the line rather than displacing it, and the separator keeps its no-break spaces
on both kinds of row alike.

**The date is the stored value, rendered as it arrives** — the row's `date` member, an ISO calendar
day. The bullets above give that cell a slot and a type scale and name no format; neither does the
row, which applies no formatter, no relative word and no locale to it. The credential-list chapter
above says relative words belong to transaction lists, and this list has none of them. Specifying
the format is work this chapter owes, so the stored day is a gap with a chapter behind it rather
than an oversight nobody noticed.

**Three things the row gives up, said plainly, because each of them is content leaving the screen.**

- **The note, wherever there is a counterparty.** The description is drawn in the payee's absence
  and nowhere else, so a transaction carrying both shows the payee and the note appears nowhere on
  the row.
- **The category group, entirely.** It is a fifth sealed name this book names in no bullet, and a
  row drawing it was putting a value on screen no chapter had specified.
- **The stored sign.** An expense reads `$20.50` where the number behind it is `-20.5`. The expense
  is the unmarked case and income is told apart by `+` **and** colour together, which is what
  [money display](patterns.md) asks for and what keeps the colour from being the message.

**The press layer draws on a row with nothing behind it, and this chapter owns that rather than
leaving a reader to find it.** The anatomy gives the row a `--bud-state-pressed` layer and one 48px+
target, and the row takes both — but there is no row action on this screen: no edit, no detail, no
menu. A press darkens the row and nothing happens, which is an affordance promising an act that
does not exist. Which way it closes — the act arriving, or the layer going until it does — is work
rather than a rule being stated here. The rename rule under
[the locked account](#the-locked-account) has no surface here for the same reason: a row offering
no Edit and no Delete has no control to disable on a value that did not open.

**None of this row's presentation is held by a test, and the gap is wide enough to name.** Delete
the component's whole `styles` block and all 68 cases in `transactions.component.spec.ts` stay
green: jsdom computes no layout, so the grid, the 64px floor, the gutter padding, the inset
hairline, the press layer, the right alignment, the type weights and the tabular figures are held
by review and by a browser. Four stacked `<div>`s pass everything. Two narrower ones sit under it:
nothing pins **which cell lands in which column** — each is read by its own class, so a template
putting the figure in the content column and the date under the counterparty reddens nothing — and
nothing pins the **list semantics**, so a set of rows built from plain `<div>`s with no `role="list"`
passes as well. What the suite does hold is the text: which member leads, which member does not
appear at all, the separator's exact spelling, the whole of the date cell, and every string the
formatter renders under two named locales.

**The write half of this screen is closed, and what closed it is worth keeping.** Nothing
could be recorded from it at all: the entry form posted `payeeName`, which both transaction
wire shapes refuse by name, so every create and every edit answered 400 — and it sent no
client-minted `id` and a plaintext `description` where an envelope is required, so **removing the
retired member alone would not have made a write succeed**. The refusal was the API's choice rather
than an accident, and it is the better of two failures: the alternative — an unmappable member
dropped in silence — accepts the body and files the transaction with no counterparty on it, which is
a record quietly losing who the money went to. The form now mints its own row id, resolves the typed
payee **on the blind index** and creates one through `POST /api/payees` when nothing matches, sends
`payeeId`, and seals the note before either request leaves; the list opens every joined envelope
under its own row id. The entry flow in [patterns](patterns.md) stays the target. See
[payees.md](../business-logic/payees.md) and
[transactions.md](../business-logic/transactions.md).

## Cards

- `--bud-surface`, 1px `--bud-hairline` border, `--bud-radius-md`, padding
  `--bud-space-5`, **no shadow** (page plane).
- Internal structure: optional `eyebrow` header, content, hairline-separated rows.
  Cards never nest; a card never contains another card.
- Cards group; they do not decorate. A single item on a screen needs no card around it.

## Seam meter

The signature component: available-vs-assigned drawn with the mark's own geometry.

- **Track**: 10px tall, `--bud-radius-full`, `--bud-hairline`.
- **Fill**: state color — `--bud-positive`, `--bud-caution`, or `--bud-over` — grows
  900ms `--bud-ease-deliberate` on first paint; clamps at 100% when over.
- **Bead**: 14px `--bud-accent` circle riding the fill's leading edge, knocked out from
  the track by a 7px halo gap of the surrounding surface color — the mark's
  halo-equals-bead-radius DNA at component scale.
- Labels: purpose name left (`body`), available figure right (`figure`, state text
  color when not positive). The words come from [voice](voice.md): "left", "Over by".
- The bead appears only on a screen's **hero meter**; inline meters (list rows) are
  6px track+fill with no bead.
- Reduced motion: renders at value, no grow.

## Dialogs and sheets

M3 base: `MatDialog` / `MatBottomSheet`. Overlay level: `--bud-overlay` surface,
`--bud-shadow-overlay`, `--bud-scrim`.

- Compact: bottom sheet, top corners `--bud-radius-lg`, 32×4px hairline drag handle
  centered 8px from top, content padded `--bud-space-5`, safe-area bottom padding.
- Expanded: centered dialog, max 560px, `--bud-radius-lg`.
- Title: Inter 600 16 (`body-lg` weight 600) — overlay titles are Inter, not Mohave.
- Actions right-aligned: Ghost for dismiss, Primary (or Destructive) for commit.
- Entry: slide up (large duration, deliberate ease) / fade in for dialogs. Reduced
  motion: fade only.

## Snackbar

M3 base: `MatSnackBar`. The inverse voice — the only inverted surface in the system.

- Light theme: `--bud-text` surface, `--bud-bg` text. Dark theme: `--bud-text` surface
  (which is near-paper), `--bud-bg` text. Radius `--bud-radius-md`, margin
  `--bud-space-4` above the bottom bar.
- One line of `body`, one optional action in mint (8.1:1 on ink). Duration 4s.
  Never stacks; a new message replaces the old.
- Used for confirmations with optional undo — never for errors that need a decision
  (those are dialogs) and never for validation (that belongs to the field).

## Empty states

Every screen defines one; see [patterns](patterns.md) for the copy structure.

- Centered block: optional bead motif (14px mint circle, may breathe once per
  [motion](motion.md)), a one-line plain statement (`body-lg`, ink), an optional
  muted line (`body`), one action (Primary or Ghost).
- Never an illustration, never a sad face, never an exclamation mark.

## Skeletons

- Static blocks: `--bud-hairline` fill, `--bud-radius-sm`, in the layout the content
  will occupy. No shimmer.
- Shown only when loading exceeds 300ms; content fades in over 200ms.
- Figures never skeleton as fake digits — a blank block, not `0.00`.

## Drag and drop

CDK base: `CdkDropList` / `CdkDrag` (shipped on Categories).

- Handle: `drag_indicator` icon, muted, 48px target. Rows without a handle don't drag.
- Lifted: `--bud-shadow-drag`, opaque `--bud-surface` — the one shadow on the page
  plane, because the item is genuinely above it. No scaling, no tilt.
- Placeholder: the vacated slot tinted `--bud-state-drop`, `--bud-radius-sm`.
- Settle: 200ms `--bud-ease-deliberate`.
- Every drag operation has a keyboard/menu equivalent ("Move up / Move down / Move to
  group…") — reordering must not require a pointer.
