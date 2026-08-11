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

**What renders today is not the table above.** No `MatButton` on any screen yet matches its
radius, min-height, label style or surface: Material's M3 defaults are what ships, so
`outlined` draws a pill with a `--mat-sys-primary` label rather than this book's Outline
(`--bud-surface`, 1px `--bud-hairline`, `--bud-text`). The table is the target and bringing
the buttons onto it is its own change, made once in `styles.scss` rather than per component.
The rule is unaffected: whatever the fill turns out to be, an unconfirmable destructive
action is the secondary variant and disabled, never Destructive.

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
  union carries has a word of its own, and none of them is the token the wire uses; then the dated
  caption as `caption` `--bud-text-muted`, wrapped in a `<time>` whose `datetime` carries the stored
  instant. The caption's leading word belongs to the kind too — `Registered` for a thing that was
  attached, `Generated` for a set that was issued (below) — and everything after that word is one
  shared formatter.
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
  two passkeys registered on one day. This is not a hypothetical: the date is formatted inside a
  `computed` the template reads, so formatting that throws abandons the change detection pass and
  takes every section below this one off the screen for the rest of the visit.
- **Action.** One Revoke button per **revocable** row — a passkey — Outline, 48px target. Visible
  label `Revoke`; accessible name `Revoke <type>, registered <date>`, beginning with the visible
  label so voice control still reaches it, and composed because two buttons named "Revoke" cannot be
  told apart.
- **A row nothing can ever revoke carries no Revoke, not even a disabled one.** Every other disabled
  control on this screen is a promise: the ceremony lands, the sentence above the list goes, and the
  button starts working. A control that will never be enabled makes the same promise and cannot keep
  it, which is the worse of the two lies — the reader waits for a release that is not coming. A
  recovery-code set is unrevocable by construction (below), and the **Google** row is unrevocable
  because the federated credential is replaced by an email change rather than removed; neither row
  draws the button. Revocability is a property carried **per kind**, beside that kind's word and
  caption, so a kind added without one fails to compile rather than inheriting an action by
  default — the half of a new kind that cannot be taken back once it is on screen.
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
  reason — a member added to it is a change to what this list renders, caught at compile time, not a
  string that arrives one day and falls through a template. What catches it is that the word, the
  caption and the revocability are held together in one map declared exhaustive over the union: a new
  kind fails the build until someone decides all three. A `switch` with a fallback would render the
  new kind as whatever the fallback said and hand it an action nobody chose for it.
- **Loading** is one line in the section's `role="status"` region. **No skeleton** — that spec is for
  a screen's own subject, and a skeleton for a list this short inside one settings section costs more
  than it explains.
- **Failure** is one sentence in the same region, in `--bud-over`. Colour is never the message.
- **Empty** replaces the list with one plain line, `body` `--bud-text` — not the centred empty-state
  block, which is for a screen's subject, and the section's own action already sits beneath.

**What ships today.** The list renders every kind, the recovery-code set's row included. **Register a
passkey** (below the list) and **Revoke** (on the revocable rows, which is the passkeys) are both
Outline and **disabled**, because both need a WebAuthn ceremony the client cannot run yet. One sentence covers both and sits **above** the list rather than beside each button: per
row, a screen reader would read the same explanation once per entry. When the ceremony lands, the
sentence goes and the controls become live — Revoke keeps its Outline until a confirmation exists
for it, per the destructive-action rule above.

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
- **The count is one line of `body` `--bud-text`**, reserving one line box (`min-height: 1lh`) so the
  page does not shift when the number arrives from the network — the label/value rule above, applied
  to a value whose label is the section heading.
- **The section shows no code and no part of one.** Not a code, not a verifier, not a hash, not the
  set's identifier, and not the day it was generated. The count is the whole of what this section
  says; the generation day is on the set's row in Ways to sign in, and a second copy of it here is a
  second thing to keep in step.

### The six states

They are six, and no two of them are interchangeable. The copy is the specification, not an example
of it.

| State | Copy | Where it renders |
| --- | --- | --- |
| At rest | *nothing* | The `role="status"` region is present and empty; the count line is blank and holds its box |
| Loading | "Loading your recovery codes…" | Inside the region, `body` `--bud-text` |
| Failed | "Couldn't load your recovery codes. Reload the page." | Inside the region, `--bud-over` |
| `0` remaining | "You have no recovery codes." | Outside the region |
| `1` remaining | "You have 1 recovery code left." | Outside the region |
| `n` remaining | "You have 5 recovery codes left." | Outside the region |

**At rest and `0` are different states and must never collapse into one.** A blank line box says the
answer has not arrived; "You have no recovery codes." is a fact about the account. Flattening the
unanswered case to a zero is the defect this row exists to name — the count is held as *no answer
yet* until one arrives, exactly as the credential list holds `null` apart from `[]`.

**At rest and loading are also different states, so loading is published rather than inferred from the
missing count.** Both are *no number yet* — a section that derived its loading line from the absent
count would render the two identically, and since the count is absent from first paint the region
would hold that line as furniture instead of gaining it when the request starts. A state of its own is
what keeps the region empty at rest, and it is the shape the Export outcome on this screen already
uses.

**Loading and failure are exclusive by structure**, not by coincidence: a load that failed is not
still loading, and rendering both would ask the reader to keep waiting on a request that has already
given up. Colour is never the message — the failure sentence reads the same with `--bud-over`
removed.

**Loading and failure live in an unconditional `role="status"` region that is empty at rest.** The
region is in the DOM from first paint and gains its text later; a live region created at the moment
it gains content is announced unreliably, because assistive technology has to have been watching the
node before the text landed. It is `status` rather than `alert` for the reason the export outcome is:
polite is right for the result of a read the screen started on its own, and assertive is reserved for
a failure to save something the person typed.

**The count renders outside that region.** It is a fact about the account, not the outcome of
something the reader just did — the same call the credential list makes when it puts *"Nothing is
attached to your account yet."* outside the region and the loading line inside it. Announcing a
standing fact as an event is how a screen reader ends up narrating the page's own furniture.

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
  left the tab order:

  > Generating a set has to be confirmed with a passkey, and Budgetoid can't run a passkey check in
  > the browser yet. The button stays off until it can.

**What replaces this when generation lands**, so that nobody "completes" the section early: the
sentence above goes, the button becomes live, and two things arrive with it that are deliberately
absent today. First, the codes themselves have to be shown once and only once, with copy saying the
browser minted them and that Budgetoid never receives one — a claim that would be describing a
capability the client does not have if it shipped now. Second, the sequencing warning: a set can only
be generated while the account still holds a working passkey, so codes protect only the person who
generated them beforehand. That sentence is confusing beside a control nobody can press and honest
beside one they can. Redeeming a code has no surface at all — there is no route to reach the anonymous
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
generator that mints a
code and derives its verifier, covered by its own spec and called by nothing; the API service has no
member that posts a set, because that route takes a fresh WebAuthn assertion this client cannot
produce. Redeeming a code has no surface in the app at all.

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
