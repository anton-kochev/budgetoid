# Motion

Nothing moves unless it informs. Motion in Budgetoid exists to show a state change, a
spatial origin, or progress — never to entertain. A calm product earns trust by holding
still.

## Tokens

| Token | Value | Use |
| --- | --- | --- |
| `--bud-motion-micro` | 130ms | Hover, press, color and border changes |
| `--bud-motion-small` | 200ms | Fades: content in, skeletons out, chips |
| `--bud-motion-medium` | 350ms | Reveals: verdict lines, expanding rows, sheet dismiss |
| `--bud-motion-large` | 500ms | Sheets and dialogs entering, page-level transitions |
| `--bud-ease-standard` | `ease` | Micro and small transitions |
| `--bud-ease-deliberate` | `cubic-bezier(0.2, 0.7, 0.3, 1)` | Meters filling, reveals, overlays — fast start, long settle |

A meter's fill grows over **900ms** with the deliberate curve — slow enough to read as
measurement, not decoration.

## Rules

- **One entrance per screen.** At most one thing may animate in when a view loads;
  everything else is simply there.
- **Chrome never loops.** Navigation, headers, and controls do not pulse, shimmer, or
  breathe. Skeletons are static blocks that fade out (200ms) — no shimmer sweep.
- **Motion follows meaning.** A sheet slides from the edge it belongs to; a deleted row
  collapses the space it occupied (200ms); a saved transaction confirms via snackbar,
  not confetti.
- **Numbers don't tick.** Figures update instantly — animating a count-up dramatizes
  what should be a plain fact. The exception is a meter's fill, which is a shape, not a
  digit.

## The bead

The one sanctioned signature motion, from
[`branding/BRAND.md`](../../../branding/BRAND.md): the bead may **breathe** — scale
1 → 1.06 → 1 over 2.4s (`--bud-motion-breathe: 2400ms`), ease-in-out — on an empty
state or at launch. Once per context, never in chrome, never more than one breathing
bead per screen.

## The kinetic sentence

The Welcome screen's self-typing sentence is the ceiling of expressiveness for the
whole product — 36ms per character, a 380ms pause, a 350ms verdict fade — and it is
allowed because it *is* the content. Nothing inside the app proper types itself.

## Reduced motion

`prefers-reduced-motion: reduce` is honored everywhere, not as an afterthought:

- Typing effects render their final state statically (the kinetic sentence already
  does).
- Meters render at their value; no grow animation.
- The bead does not breathe.
- Sheets and dialogs may fade (opacity only, ≤200ms) instead of sliding; nothing
  translates or scales.

Every component spec that animates must state its reduced-motion behavior.
