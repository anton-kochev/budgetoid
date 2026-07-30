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
- Fields signal focus through their 2px primary border instead of an outer ring; that
  border must remain the only exception.
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
  `assertive` except a failed save of user-entered data.
- The kinetic sentence on Welcome is `aria-live="off"` — decorative narrative, not an
  announcement stream; its static reduced-motion rendering is the accessible baseline.

## Language

- `lang` is set on `<html>` and updated if the UI ever localizes.
- Copy follows [voice](voice.md): plain words are an accessibility feature — cognitive
  load counts.

## Review gate

A surface ships only after: keyboard-only walkthrough, screen-reader pass on money
figures and forms, both themes checked against the contrast table, reduced-motion
verified, and 48px target audit at compact width.
