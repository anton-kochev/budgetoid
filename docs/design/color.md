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
| Over-budget container (a tint) | `--bud-over-container` | `#F4E3E2` | `#3F1E18` |
| Over-budget on-container (words on that tint; the hover state) | `--bud-over-on-container` | `#923026` | `#E68574` |

Caution is a burnt orange, deliberately off-hue from brand gold so warning never reads
as brand. The `-text` variants exist because the graphic hues fail AA as small text on
light surfaces — use graphic tokens for fills, `-text` tokens for words and figures.

**The error role is mapped into Material, not read out of it.** The advice above runs
one way — reach for `--mat-sys-*` and let it resolve per theme — and `--bud-over` also
has to run the other way, because Material ships an error colour of its own and a
refused field takes it unless the theme says otherwise. `src/styles.scss` says so in two
`mat.theme-overrides` blocks beside the one pinning the five brand neutrals:
`(error: var(--bud-over))` for a field at rest, and
`(error-container: …, on-error-container: …)` for a field under a pointer. Two, because
Material's form field reads two roles and no spelling of the first reaches the second.

**One alias rather than a list.** Measured in `@angular/material` 21.2.14's own
form-field stylesheet: eighteen error tokens exist and this application sets none of
them. Thirteen fall back to `var(--mat-sys-error)` — the message, the caret, the active
indicator, the outline, the label and their focus states — so the one declaration reaches
all thirteen, where naming them would be thirteen guesses about an appearance no screen
pins.

**On the theme rather than in a component's `:host`.** A `:host` declaration stops at
that component's own subtree, so a `mat-select`'s panel — rendered into a CDK overlay on
`<body>` — sits outside it. And copies of one fact drift: nothing compares two screens'
idea of the error colour, and a screen arriving later has to remember a declaration
whose absence nothing would notice.

**The remaining five are the hover states, and they read the other role.**
`--mat-form-field-error-hover-*`, `--mat-form-field-filled-error-hover-*` and
`--mat-form-field-outlined-error-hover-*` fall back to
`var(--mat-sys-on-error-container)`, which no spelling of the first alias can reach. That
role is not an unmapped name resolving to nothing: `mat.theme()` **emits** it from
Material's own palette — measured in `@angular/material` 21.2.14, `light-dark(#93000a,
#ffdad6)` — so an unaliased hover puts an actively declared **foreign** red on the field.
An absent mapping and somebody else's mapping look identical on screen and are found by
opposite investigations, which is why the mechanism is written down and not just the
outcome.

**So the two rows above are minted here rather than borrowed.** `--bud-*` carried no
container role to alias: the semantic families are single hues with a `-text` variant,
not the background-and-on-colour pairs Material's container roles are.

**Both values are derived from `--bud-over`, not picked.** The container is the
over-budget hue taken **86% toward white** on paper and **72% toward black** on ink. The
on-colour is the same hue taken **18% toward black in light and 18% toward white in
dark** — so a hover *deepens* the red on paper and *lightens* it on ink, and in both
themes it reads as the same colour with more of itself rather than as a second red
arriving under the pointer. The 18% is a ceiling and not a floor: the contrast below
clears with room in every pair, so what stops the step going further is the red having to
stay the same red.

**The on-colour does two jobs and the tone answers to both.** Material uses
`--mat-sys-on-error-container` as a hover **foreground on a field standing on the page**,
not only as text on the container the role is named for, so a value measured against the
tint alone would be a hover state nobody can read on paper. Both duties clear 4.5:1:

| Pair | Light | Dark |
| --- | --- | --- |
| On-container on paper | 7.40 | 6.84 |
| On-container on surface | 7.86 | 6.25 |
| On-container on overlay | 7.86 | 5.47 |
| On-container on its own container | 6.33 | 5.65 |
| Container on paper (a tint, not a separation) | 1.17 | 1.21 |

The last row is the container doing its job rather than failing at one: a wash the eye
reads as the same plane. It is not licence to tint content — the hairline rule below
stands.

**The container half is read by nothing this application draws, and it is aliased
anyway.** Measured in 21.2.14: five references to `--mat-sys-error-container` in the whole
package, four in prebuilt themes and the fifth in an optional `.mat-bg-error-container`
utility class this application does not emit. No component reads it. It is mapped because
the two are one semantic pair, and half a pair leaves the book's deepened red standing on
a tint from another palette the first time something here wants one.

**A misspelled key here is silent, and that is a fact about the tool.**
`mat.theme-overrides` emits a declaration only for a name it finds in M3's system map —
`core/tokens/_system.scss`, guarded by `map.has-key` — and drops everything else with no
warning of any kind. `on-error-container-color`, `errorContainer`, or a role Material
retires in a later version compiles clean, ships and does nothing. The surprise is
local: the M2 theming API next door is loud about a bad map, raising `@error` for a hue
that is not in a palette and again for a colour config missing `primary`, `accent` or
`warn`. Loudness is not a property of the library, so it may not be assumed here.

**What holds it.** `src/material-error-colour.spec.ts` renders a real `mat-error` under
Material's real stylesheet and walks the token chain the cascade delivered, in three
cases: the message's resting colour resolves to `--bud-over`, every one of the five hover
declarations resolves to `--bud-over-on-container`, and both system roles read at the root
resolve to the pair. The hover declarations are **discovered** from the stylesheets in the
page rather than named in the spec, so a Material that renamed or moved them leaves the
scan empty — which the case refuses. Deleting either override, misspelling a key or
deleting a token reddens it.

**It proves the chain and not the hue.** jsdom computes no colour, so substituting
Material's `#93000a`, `#00ff00` or a nonsense string for `--bud-over-on-container` leaves
the spec green — measured. Every hex on this page and every ratio in the tables is held by
review alone. Two neighbouring limits, said for the same reason: nothing compares
`branding/tokens.css` against its runtime copy in `_brand-tokens.scss`, so the two can
disagree silently; and the hover case asks where a declaration *points*, not which rule
wins the cascade under a real pointer, so a later stylesheet overriding Material's rule
would pass.

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

The over-budget container pair has its own measured table in the Semantic block above,
where the derivation it answers to is. Its numbers stay there rather than being copied
here, so nothing can drift between two tables.

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
