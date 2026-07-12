# Budgetoid Brand Specification

## Essence

Budgetoid = budget + "-oid". The mark is a coin split by a rising chart line —
a circuit-trace seam routed at 45° — holding a single bead in a wide halo at its
center. Four readings in one silhouette: **coin** (budget), **chart** (progress),
**aperture/eye** (a watchful entity), **droid** (the "-oid"). It is a single-color
mark: geometry, not color, carries it.

## The mark

### Geometry (master grid 96×96)

- Coin: circle r 32 at (48,48)
- Chart line: channel width 9.5, edge-to-edge; levels y 64 (left) and y 32 (right);
  45° chamfer, rise 32; centerline `0,64 → 32,64 → 64,32 → 96,32`
- Halo: knockout circle r 17 at (48,48) — the ring gap equals the bead radius
- Bead: circle r 8.5 at (48,48)
- Construction: single `<circle>` masked by the channel polyline + halo circle,
  plus the bead. One fill: `currentColor`.

Master file: `branding/halo/mark.svg` — recolor via CSS `color`; never edit paths
per colorway.

### Minimum sizes and clearspace

- One cut. Recommended minimum 24 px; absolute floor 16 px (favicon). A dedicated
  micro cut is a future refinement, not currently needed.
- Clearspace: keep at least the halo radius (17/96 ≈ 18 % of the mark's width)
  free on all sides.

### Colorways — rules of use

| Context | Mark color | Hex |
| --- | --- | --- |
| Light surfaces (primary) | Forest | `#0E5B43` |
| Dark neutral surfaces | Forest · dark | `#149A72` |
| Dark / forest fields (expressive, app icon) | Gold · dark | `#F0B44E` |
| Photos, colored fields | Paper knockout | `#FAF8F3` |
| Light surfaces, accent stamp only | Gold | `#E8A83C` |

- **Forest leads on light. Either pair leads on dark. Gold never carries the mark
  alone on paper** (≈1.9:1 contrast — stamp/watermark use only).
- Mint `#35D0A0` never appears inside the mark.
- Don'ts: no outlines, no shadows, no gradients, no rotation, no two-tone fills,
  no extra elements. One color per instance, always.

## Color system

### Brand

| Token | Light | Dark |
| --- | --- | --- |
| Forest | `#0E5B43` | `#149A72` |
| Gold | `#E8A83C` | `#F0B44E` |
| Mint (UI accent) | `#35D0A0` | `#35D0A0` |

Rules: **gold is brand, never semantic** — it never signals a budget state.
**Mint is the UI accent** (interactive emphasis, progress) and doubles as the
positive hue; use `--bud-positive-text` / `--bud-accent-text` variants for text.

### Neutrals (warm, faint forest cast)

| Token | Light | Dark |
| --- | --- | --- |
| Background | `#FAF8F3` | `#111815` |
| Surface / card | `#FFFFFF` | `#19211C` |
| Hairline | `#E4DFD3` | `#2A342E` |
| Text | `#1E231E` | `#EBEAE3` |
| Text muted | `#68706A` | `#909A92` |

No pure grays, no cool whites: every neutral is picked warm.

### Semantic — budget states

| State | Light | Dark |
| --- | --- | --- |
| Positive / on-track (graphic) | `#35D0A0` | `#35D0A0` |
| Positive (text-safe) | `#0F7A57` | `#4FDCAE` |
| Near-limit / caution | `#C25E0F` | `#E88433` |
| Over-budget ("in the red") | `#B23A2E` | `#E06A55` |

Caution is a burnt orange, deliberately off-hue from brand gold; over-budget red
appears only for genuine overruns, never as decoration.

Tokens file: `branding/tokens.css` (CSS custom properties, light + dark).

## Typography

- **UI text:** the app's current UI face is acceptable; Inter is the recommended
  upgrade. Body ~65 ch max line length.
- **Monetary figures are typography:** always tabular —
  `font-variant-numeric: tabular-nums` (`.bud-figures` in tokens.css). Aligned
  digits are part of the brand.
- **Wordmark:** lowercase `budgetoid` in **SF Pro Rounded Bold**, classic lockup
  (mark beside word). The production lockup (`branding/halo/lockup.svg`) is
  **outlined to paths** — no font file is shipped or embedded, since Apple's SF
  license restricts the font files to Apple-platform development. Never set the
  wordmark as live text in product; use the lockup SVG.

## Motion (optional, sparing)

One idea, used once: the bead may "breathe" (slow scale 1 → 1.06 → 1, ~2.4 s,
ease-in-out) on app launch or empty states. Never loop it in chrome; respect
`prefers-reduced-motion`.

## Assets

| File | Use |
| --- | --- |
| `branding/halo/mark.svg` | Master mark, `currentColor` |
| `branding/halo/lockup.svg` | Primary lockup (mark + outlined wordmark), `currentColor` |
| `branding/halo/app-icon.svg` | App icon / PWA source (gold·dark on forest, rx 22 %) |
| `branding/halo/favicon.svg` | Browser favicon, theme-adaptive |
| `branding/halo/oauth-logo-120.png` | Google OAuth consent screen logo (120×120) |
| `branding/tokens.css` | Design tokens, light + dark |

Wired into the app: `public/favicon.svg`, `public/apple-touch-icon.png`, and
`src/assets/theming/_brand-tokens.scss` (copy of tokens.css — keep in sync) in
`ClientApp/angular-budgetoid`.

PNG export:

```sh
rsvg-convert -w 512 -h 512 branding/halo/app-icon.svg -o icon-512.png
rsvg-convert -w 192 -h 192 branding/halo/app-icon.svg -o icon-192.png
```
