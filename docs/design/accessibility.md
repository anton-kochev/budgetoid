# Accessibility

Commitments, not aspirations: WCAG 2.2 AA is the floor for every shipped surface, in
both themes. Material provides the plumbing (focus management, ARIA roles, dialog
traps); this chapter is what the system adds and what every component must satisfy.

## Contrast

- Body text ≥ 4.5:1, large text and non-text indicators ≥ 3:1, in both themes. The
  measured table for every sanctioned pair is in [color](color.md); components may
  only combine colors from that table.
- The tokens already encode the hard cases: use `-text` variants
  (`--bud-positive-text`, `--bud-caution-text`, `--bud-accent-text`) for words and
  figures; graphic tokens only for fills. In light mode, mint never draws lines or
  glyphs on paper.
- Nothing is communicated by color alone: budget states pair color with words
  ([patterns](patterns.md)), errors pair color with message text, the active nav
  destination pairs color with a filled icon and the bead.

## Focus

- Every interactive element shows `2px solid var(--bud-focus-ring)` with
  `outline-offset: 2px` on `:focus-visible`. The token is theme-aware (deep green on
  light, mint on dark) so the ring always clears 3:1 against paper.
- **No button shows that ring unless `src/styles.scss` puts it there.** Material sets
  `outline: none` on `.mdc-button`, so until the global block landed no button in the
  application drew one at all. It is a single `:focus-visible` block naming every
  interactive selector, written globally rather than per component so it reaches
  Material's buttons, the hand-rolled ones, and anything a later screen adds.
  - **Element selectors, not `:where()`.** The rule has to out-specify
    `.mdc-button { outline: none }` at (0,1,0), and `:where()` contributes zero
    specificity — so the tidier selector ships a ring that loses the cascade and looks
    like the rule was never written.
  - `mat.strong-focus-indicators()` is not included and is not the answer: it draws an
    inset border on a pseudo-element and has no offset option, so it cannot express the
    `outline-offset: 2px` above.
- **`src/focus-ring.spec.ts` proves the rule *ships*, not that it wins.** It reads the
  emitted production stylesheet and requires one rule carrying both the token and
  `outline-offset` — both, because a `:focus-visible` selector naming only the token draws
  a ring flush against the control, where it reads as a border. It reads `.css` only:
  component styles are inlined into the JavaScript chunks, so a ring written in one
  component's stylesheet would otherwise satisfy, on its own, a rule about every
  interactive element in the application. What it cannot see is specificity, source order
  and contrast — those belong to the keyboard walkthrough below, which no automated check
  replaces. It needs a build first; see
  [frontend testing](../engineering/frontend-testing.md).
- Fields signal focus through their 2px primary border instead of an outer ring; that
  border must remain the only exception.
- **A checkbox is not a second exception, it is the rule applied to the right element.**
  Material renders the real `<input type="checkbox">` at `opacity: 0`, stretched over the
  visible box, so the global `:focus-visible` ring is painted on something nobody can
  see — the control looks unfocused while being focused. The ring moves to the visible
  box with `:has(:focus-visible)`, at the same 2px and the same offset. It is the same
  ring in the same place to a sighted keyboard user; what changes is which node draws it.
  The same will be true of any control Material builds this way.
- Focus is never hidden, never `outline: none` without replacement, and never trapped
  outside overlays. Dialogs and sheets trap focus while open and restore it on close
  (Material behavior — do not disable it).

## Touch and pointer

- 48px minimum targets on touch surfaces; icon buttons pad up to it. Inline links in
  prose are exempt.
- Hover-only affordances are forbidden; anything hover reveals must also be reachable
  by focus and touch.
- Drag and drop always has a keyboard/menu equivalent ([components](components.md)).

## Motion

- `prefers-reduced-motion: reduce` disables all non-informational motion
  ([motion](motion.md)): no typing effects, no meter growth, no breathing bead, no
  slide transitions. Opacity-only fades ≤ 200ms are permitted.
- Nothing flashes; nothing loops in chrome regardless of the setting.

## Money for screen readers

Figures are visually compressed; their accessible names are not.

- A displayed amount carries a full-word accessible name: "8 euros 20" — with its
  meaning where the visual layout implies it: "8 euros 20 left in Groceries".
- Sign conventions become words: income "plus", over-budget "over by". The unsigned
  ink expense reads as a plain amount.
- `≈` converted values read "approximately 42 dollars".
- A meter is a `role="meter"` (or `progressbar`) with `aria-valuenow` and a label
  naming the purpose; the bead is decorative (`aria-hidden`).

## Structure and semantics

- One `h1` per screen (the page title); heading levels never skip.
- Lists of transactions, accounts, and categories are semantic lists; rows are single
  focusable targets with a composed accessible name (payee, amount, date).
- Form fields always have programmatic labels; errors bind via `aria-describedby` and
  announce on submit.
- Snackbar confirmations announce politely (`aria-live="polite"`); nothing uses
  `assertive` except a failed save of user-entered data — and that carve-out is for a
  save whose failure lands **after attention has moved on**, out of sight of the press
  that started it. A write refused in front of the person is answered `status`: the
  press is a second old, the form is still on screen holding what was typed, and the
  region sits in reading order where they are already pointed, so interrupting buys
  nothing ([components](components.md), *A write that does not happen*). Spending the
  carve-out also costs a second live region — politeness is a property of the node, so
  raising a screen's one region to `alert` raises its loading line and its notices with
  it.
- The kinetic sentence on Welcome is `aria-live="off"` — decorative narrative, not an
  announcement stream; its static reduced-motion rendering is the accessible baseline.
- **Secrets are content, not announcements.** The ten recovery codes are a semantic list
  in reading order and never inside a live region. A `role="status"` holding a list
  narrates every entry as an event and puts ten secrets into a speech buffer, which buys
  nothing a reader could not get by reading — the list role already announces the count,
  and the download is the route that does not require hearing any of them. Only the
  one-sentence outcomes of the save and copy controls go in the region.

## Language

- `lang` is set on `<html>` and updated if the UI ever localizes.
- Copy follows [voice](voice.md): plain words are an accessibility feature — cognitive
  load counts.

## Review gate

A surface ships only after: keyboard-only walkthrough, screen-reader pass on money
figures and forms, both themes checked against the contrast table, reduced-motion
verified, and 48px target audit at compact width.
