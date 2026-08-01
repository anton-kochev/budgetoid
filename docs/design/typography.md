# Typography

Two voices. **Mohave** is the app's voice — it speaks in headlines and eyebrows.
**Inter** is everything you read, enter, or count — body, labels, controls, and every
number you trust. The boundary is strict because Mohave is a condensed display face:
distinctive in short bursts, wrong for reading and dangerous for data.

Families: `--bud-font-display: 'Mohave', sans-serif` ·
`--bud-font-ui: 'Inter', sans-serif`. Both are served from the app's own origin as
variable woff2 files in `public/fonts/` — Mohave `wght 300–700` latin, Inter
`wght 100–900` latin and Cyrillic, all `font-display: swap`. Nothing loads from a font
CDN; see [no third-party origins](../engineering/no-third-party-origins.md). The
wordmark is not typography — it ships as outlined paths
(see [`branding/BRAND.md`](../../../branding/BRAND.md)); never set "budgetoid" in live
text.

## The two-voice rule

Mohave may appear in exactly two forms:

1. **Headlines** — display, headline, and page-title roles, 20px and up.
2. **The eyebrow** — the one codified small exception: uppercase section labels.

Everything else is Inter. Mohave never sets body copy, control labels, form fields,
table content, or any digit that refers to money.

## Type scale

Rem-based (16px root), mobile-first. Sizes are fixed across breakpoints except
`display`, which scales fluidly.

| Role | Face | Weight | Size | Line height | Use |
| --- | --- | --- | --- | --- | --- |
| `display` | Mohave | 700 | `clamp(2rem, 4.5vw, 2.75rem)` | 1.08 | Welcome headline; marketing moments only |
| `headline` | Mohave | 500 | 1.75rem / 28 | 1.3 | Hero statements (the kinetic sentence) |
| `title` | Mohave | 500 | 1.5rem / 24 | 1.2 | Page titles |
| `eyebrow` | Mohave | 600 | 0.75rem / 12 | 1.2 | Uppercase section labels, `letter-spacing: 0.14em` |
| `body-lg` | Inter | 400 | 1rem / 16 | 1.5 | Lead paragraphs, dialog body |
| `body` | Inter | 400 | 0.875rem / 14 | 1.55 | Default UI text |
| `body-sm` | Inter | 400 | 0.8125rem / 13 | 1.5 | Secondary lines, list metadata |
| `label` | Inter | 600 | 0.9375rem / 15 | 1.2 | Buttons, tabs, emphasized links |
| `caption` | Inter | 400 | 0.75rem / 12 | 1.4 | Fine print, dates, nav labels |

Text blocks cap at **65ch**; centered marketing copy at 46ch. Headlines get
`text-wrap: balance`.

## Monetary figures

Figures are a first-class text style, not body text with digits in it.

| Role | Face | Weight | Size | Use |
| --- | --- | --- | --- | --- |
| `figure-hero` | Inter | 600 | 2.25rem / 36 | The available amount; the entry form amount |
| `figure-lg` | Inter | 600 | 1.5rem / 24 | Card-level totals |
| `figure` | Inter | 500 | 0.9375rem / 15 | Row amounts in lists |
| `figure-sm` | Inter | 500 | 0.8125rem / 13 | Inline amounts in secondary text |

Rules, all of them brand rules:

- **Always tabular**: `font-variant-numeric: tabular-nums` (`.bud-figures` /
  `[data-figures]` in tokens). Aligned digits are part of the brand.
- **Cents are never dropped or shrunk.** The record is honest at every size.
- Columns of figures right-align; a total row uses weight (600), not size, to stand
  out.
- Color and sign rules for figures live in [patterns](patterns.md) — expenses in ink,
  income in positive text, `≈` for converted values.

## Material mapping

`mat.theme()` typography is configured with Mohave as the brand family and **Inter as
the plain family** (`_theme-config.scss`), so Material components render their own text
in Inter automatically. The Mohave roles above are applied by component styles, never
by Material defaults. Weights: regular 400, medium 500, bold 700.
