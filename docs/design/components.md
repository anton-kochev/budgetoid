# Components

Anatomy, states, and tokens for every part the product is built from. Angular Material
(M3) provides the behavior and accessibility underneath each mapped component; every
visible value comes from this system. If a component isn't specified here, specify it
here before building it.

All interactive components share: focus ring `2px solid var(--bud-focus-ring)` with
`outline-offset: 2px`; state layers from [color](color.md); 48px minimum touch targets;
`--bud-motion-micro` for state transitions.

## Iconography

**Material Symbols Rounded**, weight 400, grade 0, optical size 24, outlined
(`FILL 0`). Filled (`FILL 1`) marks exactly one thing: the active navigation
destination. Sizes: 20 (inline), 24 (default), 40 (empty states). Icons never appear
without an accessible name — a visible label or `aria-label`.

No icon font is loaded today, because no component uses an icon yet. The first icon to
ship brings a woff2 **subsetted to the glyph names actually used**, served from
`public/fonts/` — the whole face is 1.38 MB against roughly 1.3 kB for two glyphs, and
it may never come from a CDN. The `FILL` axis stays variable so the active-destination
state needs no second file. See
[no third-party origins](../engineering/no-third-party-origins.md).

## App shell and navigation

Destinations: **Home, Transactions, Accounts, Categories**, plus the **Add** action.

**Settings is not a destination.** The Settings screen ships at `/app/settings` with no entry
in the bar and none in the rail; it is reached by typing the URL. Nothing in the shell links
to it yet, and adding it is a decision about the destination list rather than a tidy-up.

### Bottom bar (compact, < 960px)

- Paper background (`--bud-bg`), top hairline, no elevation, no blur. Height 64px +
  `env(safe-area-inset-bottom)`.
- Five slots: four destinations around a centered Add button.
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
| Outline | `--bud-surface`, 1px `--bud-hairline` border | `--bud-text` | Secondary actions; the Google sign-in button is this spec |
| Ghost | transparent | `--bud-accent-text` | Tertiary, inline, and dialog-dismiss actions |
| Destructive | `--bud-over` fill | `#FFFFFF` | Deleting and erasing, only after confirmation UI |

One primary button per view. Hover on Outline may invert to primary fill (the shipped
sign-in hover); Ghost and icon buttons use state layers. Icon-only buttons: 40px
visual, 48px target, always `aria-label`.

A destructive action whose confirmation UI does not exist yet is specified as **Outline,
disabled** — never Destructive. The Destructive fill is a promise that a confirmation
follows, and a control that cannot be activated should not make it. Disabled is not
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

**A stale value is not equally harmful in every section, and the section it harms most sets the rule
for all of them.** A count that is stale is merely old. An address is *wrong*: it is the single
value that answers the only question its row exists for, and the same read is what updates it on the
day changing an address becomes possible, so a refresh that failed would leave the previous address
standing as the answer. A section may not keep its last answer through a failed re-read on the
argument that its own value ages harmlessly — the reader cannot tell a kept answer from a fresh one,
and nothing in the render tells them which they have.

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
passkeys) are both Outline and **disabled** — and **they are not waiting on the same thing**, which
is why the section carries two sentences and not one. The ceremony is not what either of them waits
on: this client creates a passkey on `/register` and asserts one on `/welcome`.

- **Register a passkey** waits on the account's keys. A passkey is a *factor*, every factor stores
  its own wrapped copy of the account's content key and index key, and wrapping them needs them
  unwrapped — which no route hands back. Its sentence names no specific action, because the recovery
  codes section below is blocked by exactly the same thing and says exactly the same words:

  > This gives a new way to sign in its own copy of your account's keys, and Budgetoid can't unlock
  > those keys in the browser yet. The button stays off until it can.

- **Revoke** waits on this screen. `POST /api/me/credentials/{id}/revocation` is live and the fresh
  assertion that authorizes it is a ceremony this client runs; nothing here asks for one. That is
  the erasure section's position, so it is said in the erasure section's words, plural for the
  per-row buttons:

  > Revoking has to be confirmed with a passkey, and this screen doesn't ask for one yet. Those
  > buttons stay off until it does.

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

It sits on `/app/settings` **between Ways to sign in and Export**. Export and Erase are a pair — the
alternative offered beside the destructive act, per [patterns](patterns.md) — and nothing goes
between them.

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
| Failed | "Couldn't load your recovery codes. Reload the page." | Inside the region, `--bud-over` |
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
  left the tab order. It is **word for word** the sentence above the credential list, and that is
  the specification rather than an accident: generating a set is ten factors at once — each code
  derives its own key-encryption key — so it waits on precisely what registering a passkey waits on,
  the account's content key and index key unwrapped on this device. The sentence names no specific
  action so that it can be true in both places:

  > This gives a new way to sign in its own copy of your account's keys, and Budgetoid can't unlock
  > those keys in the browser yet. The button stays off until it can.

  It does **not** say the browser cannot run a passkey check. It can: `/register` creates a
  credential and `/welcome` asserts one, and the assertion this route also demands is the half the
  client already produces.

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
member that posts a set. **The reason is no longer the assertion.** That route takes six members —
five of a fresh WebAuthn assertion, which this client can produce, and ten whole code submissions,
each carrying its own wrapped copy of the account's two keys. It is the wrapping that nothing here
can do, because nothing hands the keys back to unwrap. Redeeming a code has no surface in the app at
all.

**The show-once surface exists and this section is not where it lives.** It is the last step of the
registration flow below, and that flow drives it: somebody creating an account is shown ten codes
once, and posts the account from that step. Nothing above changes: this section still shows no code
and no part of one, and *What replaces this when generation lands* still describes what arrives
**here** when the Settings path opens.

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

The only step that asks for nothing: it states which account is about to be created and offers the
way on. Title **Create your Budgetoid account**.

- **Holding a provider token**, it shows the asserted address back — `body`, with the address itself
  at weight 600, tabular figures and `overflow-wrap: anywhere` for a long one on a 320px screen —
  then one line naming what the next two steps are, then one Primary **Continue**. A person with a
  personal Google account and a work one has no other way to learn which of them this browser is
  still signed in to, and the cost of guessing wrong is not a wasted click: the next step spends a
  challenge, and the account that results is bound to whichever address was asserted.
- **Holding none**, it shows no address and no **Continue**. One line saying this browser is not
  holding a Google address, and an **Outline** **Continue with Google** — the treatment the book
  gives that control wherever it appears, in the welcome screen's own words so that somebody bounced
  here meets the control they already pressed once. A browser arrives in this state routinely: a
  bookmark, a reload an hour later, an exchange that never completed. A **Continue** from there
  would reach a refusal with nothing useful to say about why.

### Step 2 — the passkey

The step that spends the challenge, and the one screen in the flow with seven ways to end badly.
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
- **Two of the seven refusals render no control at all**, and that is the whole of the rule. Whether a
  press could help is a property of the refusal rather than a default, and *no* is not a smaller
  version of *yes*: where the browser cannot run the ceremony, or the authenticator cannot derive the
  value the account's keys are wrapped under, leaving **Create a passkey** on the screen is a retry
  that does not admit to being one — it reads as a way forward, costs another system sheet to
  disprove, and ends in the same sentence.

The sentences are the specification, not an example of them. None is a synonym of another: folded
into one, the screen tells somebody whose browser cannot run WebAuthn at all to try again, and tells
somebody who simply closed the system sheet that their device is unsupported.

| Refusal | Copy | Offers another press |
| --- | --- | --- |
| The browser cannot run a ceremony | "This browser can't create a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can." | No |
| The system sheet was closed, or timed out | "The passkey wasn't created. Nothing has been saved, and nothing was sent — try again whenever you're ready." | Yes |
| The authenticator already holds a credential it was asked to decline | "This device already holds a passkey Budgetoid can't reuse. Try again with a different device or security key." | Yes |
| The device cannot hold the account's keys | "This device can't hold your account's keys, and Budgetoid won't create an account it can't lock. Try a different phone, laptop or security key." | No |
| The ceremony did not finish | "Your device didn't finish creating the passkey. Nothing has been saved." | Yes |
| The server never issued a challenge | "Budgetoid couldn't reach the server to start. Nothing has been saved." | Yes |
| Something nobody predicted, between the challenge arriving and the codes being ready | "Budgetoid didn't finish, and nothing has been saved. Try again." | Yes |

Three of them are worth reading twice.

- **The sixth is the only one on this screen about the *server* rather than the device**, and the
  sentence has to say so: a person told their device failed will go and buy a security key for a
  problem a reload would have fixed.
- **The third is unreachable from this flow today** — the account-registration options leg sends an
  empty `excludeCredentials`, so there is nothing for an authenticator to decline against — and it is
  specified anyway, because a refusal the screen has no sentence for is a screen that says nothing at
  all. See [registration.md](../business-logic/registration.md).
- **The seventh shares a cause with the shell's "no answer" state and must not share its words.**
  Nothing has been posted on this step, so this sentence can say plainly that nothing was created;
  the shell's cannot, because there a request really did leave. Same failure, two screens, two
  truthful sentences.

### Step 3 — the codes

Specified in full in [the recovery-code hand-off](#the-recovery-code-hand-off) below. What the flow
adds around it is an input and an output: the ten codes it has just minted, and the press that posts
the account.

### The four post-request states

The registration request answers in one of four ways, and three of those words **replace the codes
step** rather than sitting under it. There are four states behind the three words, because the
conflict is read two ways. Each carries **its own `h1`** — a document with the codes gone and no
heading of its own is a document titled by a step that is no longer on it — and the ten codes leave
the screen in all four.

| State | Copy | Control |
| --- | --- | --- |
| Refused | **Registration was refused** — "Your account wasn't created and nothing was saved. The ten codes you were just shown open nothing — start again to get a new set." | Primary **Start again** |
| An account already exists, on a first attempt | **You already have an account** — "An account already exists for this Google address. Nothing was created here, and the ten codes you were just shown open nothing — sign in from the Budgetoid home page instead." | none |
| An account already exists, after a restart | **Your first attempt worked** — "Your first attempt did create your account — its answer just didn't reach this browser. Sign in with the passkey you made on that attempt. The ten codes you were shown a moment ago open nothing; the ten from the first attempt are the ones that work." | none |
| No answer came back | **Budgetoid didn't hear back** — "Budgetoid didn't get an answer, so we can't tell you whether your account was created. Keep the ten codes you saved: if it was, they're part of the only way back into it." | Primary **Start again** |

- **The first two say the codes are dead; the last must never.** A judged request was read and left
  the server before a row was written, so saying the ten codes open nothing is a kindness — it tells
  somebody to throw away a piece of paper that is worthless, and it stops ten worthless secrets
  standing in front of a person about to be handed ten real ones. A request that got **no answer**
  says nothing about whether the account exists: it may have arrived, committed and lost its
  response. Telling that person their codes are worthless tells them to discard the only key to an
  account they cannot make more codes for. The two sentences are the requirement; collapsing them is
  the defect.
- **The two conflicts are the same status code and opposite facts**, and the screen may only tell
  them apart by whether **Start again** has been pressed. The server sends four distinct sentences
  under one identical title with no machine-readable code, so the copy above must never be chosen by
  matching the server's text. On a first attempt the 409 is a stranger at the front door and nothing
  was created here. After a restart it is the person's *own* first attempt answering: that POST
  committed and lost its 201, so the account exists, the passkey that opens it is the first
  attempt's, and the live codes are the first attempt's ten. Showing the first sentence there is
  false on every clause and costs the account — somebody who throws the first card away holds a
  passkey, no codes, and no way to make more.
- **Neither conflict carries a control, and after a restart that is now a gap rather than a rule.** On
  a first attempt, starting again spends another challenge and another passkey to be told the same
  thing, so the sentence is the whole offer. After a restart the sentence tells the person to sign in
  with the passkey their first attempt created — and `/welcome` now does exactly that, so the screen
  it names is a screen this state could point at. What ships carries no control there; adding one is
  work, and until it lands this bullet records the departure rather than the reason.
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
passkey sentence with its control, the hand-off, and all four post-request states. The flow runs
the ceremony, draws the account keys, mints the card and eleven factor identifiers, wraps every
factor's copy, posts the account, publishes the session and hands the person to `/app`. The record
that an attempt was abandoned is read on exactly one surface — the conflict, where it decides which
of two opposite sentences is true — and nowhere else.

**One departure, named rather than tidied away.** Outside that conflict, **no step says an attempt
was abandoned**: after **Start again**, nothing on the passkey or codes step tells a person that the
ten codes they may have written down a minute ago belong to nothing. Those two steps are where a
person is still standing rather than being told an outcome, and the sentence that would sit there is
work rather than a rule this chapter is retiring.

## The recovery-code hand-off

The one screen in the product that shows a secret, and the only time it is shown. Built as
`register/steps/codes-step.component`, driven by an `input()`, and rendered as the last step of the
registration flow above — which mints the codes, hands them here, and posts the account when this
step raises its one output.

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
outcomes — `Copied.` and the copy failure — and nothing else. The download is the accessible route.

### The acknowledgement

One required checkbox — M3 `MatCheckbox`, the first in this product — gating the Primary. **Not a
typed word**: the typed word is reserved for destroying data that exists now, and nothing here is
destroyed. This is a person accepting a future risk before anything is created at all.

**The consequence is its own block above the checkbox, never the checkbox's label.** A label is
announced as the control's name every time focus lands on it; a paragraph of consequence read that
way becomes noise the reader learns to skip, which is the opposite of what it is for.

The Primary is `disabledInteractive` until the box is ticked — the third disabled case in the
Buttons chapter — and the gate is repeated in the click handler, for the reason stated there.

### Copy

> **Save your recovery codes**
>
> Your browser made these ten codes. Budgetoid never receives one, and this is the only time
> they're shown.
>
> *(the ten codes)*
>
> Copying puts them on your clipboard, where other apps on this device can read them.
>
> Your passkey and these ten codes are the only ways into this account. Budgetoid keeps no copy of
> either, so if you lose the passkey and every code, everything you record here stays locked — to
> you, and to us. There's no way back, and no one to ask.
>
> ☐ I've saved these codes somewhere I can get to them.
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
screen, saves or copies them, acknowledges the consequence, and presses the control that creates the
account. **This component still mints nothing and posts nothing** — the codes arrive through its
input and the press leaves through its output, which is what stops a set being re-minted by every
re-render of the step. The type styles above — Inter, `tabular-nums`, the tracking — are the one part
of this chapter no test holds: jsdom applies no styles, and a bundle-reading spec was judged not
worth it here.

## Text fields and selects

M3 base: `MatFormField` (outlined appearance), `MatInput`, `MatSelect`,
`MatAutocomplete`, `MatDatepicker`.

- Container: `--bud-surface`, 1px `--bud-hairline` border, `--bud-radius-sm`, min
  height 48px. Focus: border becomes 2px `--mat-sys-primary` (the field's focus is its
  border; no outer ring).
- Label and input text: Inter (`body`); floating label per M3 behavior, muted when
  resting.
- Error state: border and message in `--bud-over`, message text (`caption`) beneath the
  field — never color alone.
- Dropdown and autocomplete panels are overlays: `--bud-overlay`,
  `--bud-shadow-overlay`, `--bud-radius-sm`, options 48px tall with state layers,
  selected option tinted `--bud-state-selected`.
- Forms cap at 560px and stack in a single grid column, `--bud-space-4` gaps.

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
