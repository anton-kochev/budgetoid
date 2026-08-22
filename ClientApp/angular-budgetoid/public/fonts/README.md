# Fonts

Self-hosted typefaces. Nothing here is fetched from another origin at runtime — that is
the point of the directory. See
[no third-party origins](../../../../docs/engineering/no-third-party-origins.md) for the
rule and the test that locks it, and [typography](../../../../docs/design/typography.md)
for what each face is for.

| File                                    | Face                     | Axis           | Coverage   |
| --------------------------------------- | ------------------------ | -------------- | ---------- |
| `inter-latin-wght-normal.woff2`         | Inter                    | `wght 100–900` | latin      |
| `inter-cyrillic-wght-normal.woff2`      | Inter                    | `wght 100–900` | cyrillic   |
| `mohave-latin-wght-normal.woff2`        | Mohave                   | `wght 300–700` | latin      |
| `material-symbols-rounded-subset.woff2` | Material Symbols Rounded | `FILL 0–1`     | four icons |

The `@font-face` rules that bind these live in `src/assets/theming/_fonts.scss`, together
with the `unicode-range` values copied from the same upstream. The Cyrillic cut is there
because payee, category, and account names are user text; its `unicode-range` keeps it off
the wire until such a name is rendered.

Inter and Mohave are SIL OFL 1.1 — see [`OFL.txt`](OFL.txt). Material Symbols is
Apache 2.0, which needs no notice file shipped beside the bytes; the upstream is
[google/material-design-icons](https://github.com/google/material-design-icons).

## Regenerating

The files are the variable, single-axis, upright cuts published by
[Fontsource](https://fontsource.org) at version 5.3.0:

```sh
B=https://cdn.jsdelivr.net/npm/@fontsource-variable
curl -sO $B/inter@5.3.0/files/inter-latin-wght-normal.woff2
curl -sO $B/inter@5.3.0/files/inter-cyrillic-wght-normal.woff2
curl -sO $B/mohave@5.3.0/files/mohave-latin-wght-normal.woff2
```

Filenames are not content-hashed, and `staticwebapp.config.json` serves them as
`immutable` for a year. **Replacing a font means changing its filename**, in
`_fonts.scss` and in the preload in `index.html` along with it — overwriting a file in
place leaves the old bytes in every browser that has already seen them.

## Icons

`material-symbols-rounded-subset.woff2` is **1.5 kB and holds four glyphs**, against 15 MB
for the upstream variable face. It exists because the navigation is the first thing in the
product to draw an icon.

**Two axes decisions are baked into the bytes and both are load-bearing.** `wght`, `GRAD`
and `opsz` are _pinned_ to the 400 / 0 / 24 the design book fixes, because a variable axis
nobody moves is payload nobody uses. `FILL` is _kept variable_, because the book marks the
active destination by filling its icon — so one file serves both states through
`font-variation-settings`, and there is no second file to drift out of step with the first.

**Codepoints, not ligatures.** Material Symbols can be addressed by writing `settings` and
letting an `rlig` ligature swap it for the glyph. Subsetting that way costs about 87 kB
here: the ligature closure drags in a thousand placeholder glyphs and the letters that
spell every name. Addressing the four private-use codepoints directly needs no `GSUB` at
all. The cost is that the markup would be opaque, so the codepoints are never written into
a template — they live in one named map beside the destination list.

### Regenerating

Needs `fonttools` and `brotli`; do it in a throwaway virtualenv rather than in the system
Python. Adding or changing a destination means redoing this, and changing the **filename**
with it — see the immutability note above.

```sh
python3 -m venv /tmp/fontenv && /tmp/fontenv/bin/pip install fonttools brotli
B=https://raw.githubusercontent.com/google/material-design-icons/master/variablefont
curl -sLo /tmp/msr.ttf "$B/MaterialSymbolsRounded%5BFILL%2CGRAD%2Copsz%2Cwght%5D.ttf"

# Pin everything the book fixes; keep FILL variable.
/tmp/fontenv/bin/fonttools varLib.instancer /tmp/msr.ttf \
  wght=400 GRAD=0 opsz=24 -o /tmp/msr-fill.ttf

# receipt_long, account_balance_wallet, category, settings.
/tmp/fontenv/bin/pyftsubset /tmp/msr-fill.ttf \
  --output-file=material-symbols-rounded-subset.woff2 --flavor=woff2 \
  --unicodes=U+EF6E,U+E850,U+E574,U+E8B8 \
  --layout-features='' --no-hinting --drop-tables+=STAT,gasp
```

Check the result before committing it: five glyphs, a single `FILL` axis, a `gvar` table
(without it the fill does not animate) and all four codepoints in the `cmap`.
