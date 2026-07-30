# Layout

Phone first, one hand, CSS Grid by default. Every layout is designed at 360–400px and
grows through `min-width` breakpoints. Surfaces sit flat on warm paper, separated by
hairlines — elevation is reserved for things that are genuinely above the page.

## Spacing scale

4px base. Use the step, not an arbitrary value; if a design wants 18px, it wants 16 or
20.

| Token | Value | Typical use |
| --- | --- | --- |
| `--bud-space-1` | 4px | Icon-to-text gaps, bead offsets |
| `--bud-space-2` | 8px | Tight gaps inside controls |
| `--bud-space-3` | 12px | Gaps between related items, stacked cards |
| `--bud-space-4` | 16px | Default padding and gaps; mobile gutter |
| `--bud-space-5` | 20px | Card padding |
| `--bud-space-6` | 24px | Section gaps; tablet gutter |
| `--bud-space-7` | 32px | Large section breaks; desktop gutter |
| `--bud-space-8` | 40px | Hero padding |
| `--bud-space-9` | 48px | Screen-level breathing room |
| `--bud-space-10` | 64px | Marketing-scale spacing |

`--bud-gutter` is the responsive page inset: 16px, 24px from 600px, 32px from 960px.

## Breakpoints

| Name | Range | What changes |
| --- | --- | --- |
| Compact | < 600px | The design baseline. Bottom navigation, single column, full-width sheets. |
| Medium | 600–959px | Gutter widens, type `display` grows, forms cap at 560px. |
| Expanded | ≥ 960px | Navigation moves to a left rail; content becomes a centered column, max 1080px. |
| Wide | ≥ 1280px | Nothing new appears — white space grows. Never add columns just because they fit. |

Media queries use `min-width` only. The shipped Welcome screen's single 600px query is
the pattern.

## Page anatomy

- The app shell (`.budgetoid-app`) is a padding-free grid; **each route owns its own
  spacing** via `--bud-gutter`.
- A screen is: header row (page title + contextual actions), content, and — on compact
  — the bottom navigation bar. The header is part of the page plane: paper background,
  no elevation; a hairline appears under it only once content has scrolled beneath.
- Full-height screens use `100dvh`, never `100vh`.
- Respect safe areas: bottom navigation and sheets pad with
  `env(safe-area-inset-bottom)`.

## Surfaces and elevation

Three levels, nothing in between:

| Level | What lives there | Treatment |
| --- | --- | --- |
| Page plane | Screens, cards, lists, headers, nav | `--bud-bg` and `--bud-surface`, hairline separation, **no shadow** |
| Lifted | The one thing being dragged | `--bud-shadow-drag` |
| Overlay | Dialogs, sheets, menus, autocomplete panels | `--bud-overlay` + `--bud-shadow-overlay` + `--bud-scrim` (dialogs and sheets only) |

| Token | Light | Dark |
| --- | --- | --- |
| `--bud-shadow-drag` | `0 4px 16px rgb(30 35 30 / 0.14)` | `0 4px 16px rgb(0 0 0 / 0.45)` |
| `--bud-shadow-overlay` | `0 12px 32px rgb(30 35 30 / 0.18)` | `0 12px 32px rgb(0 0 0 / 0.55)` |

Shadows are warm (ink-tinted) in light mode. If a design wants a shadow on the page
plane, it wants a hairline.

## Radius scale

| Token | Value | Use |
| --- | --- | --- |
| `--bud-radius-sm` | 8px | Inputs, chips, skeleton blocks |
| `--bud-radius-md` | 12px | Buttons, cards, snackbars |
| `--bud-radius-lg` | 16px | Dialogs, sheets (top corners) |
| `--bud-radius-full` | 999px | Meters, beads, pills, avatars |

The app icon's 22% superellipse is brand geometry, not a UI radius.

## Grid rules

- `display: grid` is the default; flex only for genuine single-axis flows (a button's
  icon+label, a footer's wrap).
- Prefer `gap` over margins between siblings; margins are for exceptions.
- Columns: `minmax(0, 1fr)` to keep figures from forcing overflow.
- Forms cap at 560px; reading text at 65ch; the content column at 1080px (expanded).

## Touch and pointer

- Interactive targets: **48px minimum** (`--bud-touch-target`) on touch surfaces; a
  visually smaller control (a 24px icon) earns its 48px with padding.
- Primary actions live in the bottom half of compact screens — thumb reach is layout,
  not preference.
- Hover affordances are enhancements; nothing may be reachable only by hover.
