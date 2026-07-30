# Color

Warm paper, forest ink, one mint signal. Every neutral carries a faint forest cast — no
pure grays, no cool whites. The same hues serve both themes; dark mode is a first-class
rendering, not an inversion.

Canonical values live in [`branding/tokens.css`](../../../branding/tokens.css) (fixed
hex) and its Material-aware runtime copy
`ClientApp/angular-budgetoid/src/assets/theming/_brand-tokens.scss`. Brand colorway
rules for the mark itself are in [`branding/BRAND.md`](../../../branding/BRAND.md).

## Roles

### Neutrals

| Role | Token | Light | Dark |
| --- | --- | --- | --- |
| Page background (paper) | `--bud-bg` | `#FAF8F3` | `#111815` |
| Card / surface | `--bud-surface` | `#FFFFFF` | `#19211C` |
| Overlay surface (dialogs, menus, sheets) | `--bud-overlay` | `#FFFFFF` | `#222C26` |
| Hairline | `--bud-hairline` | `#E4DFD3` | `#2A342E` |
| Text | `--bud-text` | `#1E231E` | `#EBEAE3` |
| Text muted | `--bud-text-muted` | `#68706A` | `#909A92` |
| Scrim behind overlays | `--bud-scrim` | `rgb(17 24 21 / 0.4)` | `rgb(0 0 0 / 0.5)` |

In light mode, overlays share the surface white and are distinguished by shadow; in
dark mode they also step one tone lighter, because shadow alone cannot separate dark
surfaces.

### Brand

| Role | Token | Light | Dark |
| --- | --- | --- | --- |
| Forest (primary, ink of the brand) | `--bud-brand-forest` | `#0E5B43` | `#149A72` |
| Gold (brand stamp only) | `--bud-brand-gold` | `#E8A83C` | `#F0B44E` |
| Mint (the accent) | `--bud-brand-mint` | `#35D0A0` | `#35D0A0` |

In the Angular app, prefer the Material roles — `--mat-sys-primary`,
`--mat-sys-on-primary`, `--mat-sys-tertiary` — which resolve to the correct tonal value
per theme; the raw brand hexes are for brand moments (the mark, the lockup).

### Semantic — budget states

| State | Token | Light | Dark |
| --- | --- | --- | --- |
| Positive / on-track (graphics: bars, meters, chips) | `--bud-positive` | `#35D0A0` | `#35D0A0` |
| Positive (text) | `--bud-positive-text` | `#0F7A57` | `#4FDCAE` |
| Near-limit (graphics) | `--bud-caution` | `#C25E0F` | `#E88433` |
| Near-limit (text) | `--bud-caution-text` | `#AD560E` | `#E88433` |
| Over-budget (graphics and text) | `--bud-over` | `#B23A2E` | `#E06A55` |

Caution is a burnt orange, deliberately off-hue from brand gold so warning never reads
as brand. The `-text` variants exist because the graphic hues fail AA as small text on
light surfaces — use graphic tokens for fills, `-text` tokens for words and figures.

### Interactive

| Role | Token | Light | Dark |
| --- | --- | --- | --- |
| Accent (fills, meters, the bead) | `--bud-accent` | mint | mint |
| Accent as text | `--bud-accent-text` | `#0F7A57` | `#4FDCAE` |
| Focus ring | `--bud-focus-ring` | `#0F7A57` | `#35D0A0` |

Mint on light paper is ~1.9:1 — visible as a fill, invisible as a line. The focus ring
therefore uses the deep accent green in light mode and mint in dark mode; both clear
the 3:1 non-text minimum with room to spare.

### State layers

Hover, press, and selection are translucent layers over the component's own surface,
so they work on any background in either theme:

| State | Token | Recipe |
| --- | --- | --- |
| Hover | `--bud-state-hover` | `color-mix(in srgb, var(--bud-text) 6%, transparent)` |
| Pressed | `--bud-state-pressed` | `color-mix(in srgb, var(--bud-text) 12%, transparent)` |
| Selected / active tint | `--bud-state-selected` | `color-mix(in srgb, var(--bud-accent) 14%, transparent)` |
| Drop target | `--bud-state-drop` | `color-mix(in srgb, var(--bud-accent) 8%, transparent)` |

Disabled elements render at `opacity: 0.38` (`--bud-disabled-opacity`) with no state
layers; they never change hue.

## Contrast

Measured WCAG ratios for the pairs the UI actually uses. AA requires 4.5:1 for text,
3:1 for large text and non-text elements. All body-text pairs pass with margin.

| Pair | Light | Dark |
| --- | --- | --- |
| Text on paper | 15.1 | 14.9 |
| Text on surface | 16.0 | 13.7 |
| Muted text on paper | 4.8 | 6.2 |
| Muted text on surface | 5.1 | 5.7 |
| Forest / primary text on paper | 7.6 | 5.1 |
| Positive text on paper | 5.0 | 10.5 |
| Caution text on paper | 4.8 | 6.7 |
| Over text on paper | 5.6 | 5.5 |
| Focus ring on paper (non-text, ≥3:1) | 5.0 | 9.2 |
| Ink `#1E231E` on mint (add button) | 8.1 | 8.1 |
| Mint fill on paper (non-text) | 1.9 | 9.2 |

The last row is the standing caveat: **in light mode, mint fills need a shape to live
in** — a track, a chip, a filled circle adjacent to text — never a thin line or small
glyph alone on paper. In dark mode mint is free.

## Discipline

- **Gold is brand, never semantic.** It never signals a budget state, never fills a
  meter, never colors text. It stays on the app icon and, at most, one celebratory
  moment (a goal fully funded).
- **Mint is the only accent.** Interactive emphasis, progress, the bead, selection —
  all mint. A second accent is a defect.
- **Expenses are ink.** Transaction amounts spend their lives in `--bud-text`. Red
  (`--bud-over`) appears only when a purpose or account is genuinely over.
- **Hairlines separate; color does not.** Do not use background tints to group content
  on the page plane — use hairlines and spacing. Tints are reserved for state layers.
- **Never hard-code a hex** in component styles. If a color has no token, it has no
  business in the UI.

## Charts (reserved roles)

When charts arrive: mint is the primary series, forest the secondary, semantic tokens
keep their meanings, gridlines are hairline, and labels sit directly on the data rather
than in legends where possible. A full chart spec joins this chapter when the first
real chart is designed.
