# Fonts

Self-hosted typefaces. Nothing here is fetched from another origin at runtime — that is
the point of the directory. See
[no third-party origins](../../../../docs/engineering/no-third-party-origins.md) for the
rule and the test that locks it, and [typography](../../../../docs/design/typography.md)
for what each face is for.

| File                               | Face   | Axis           | Coverage |
| ---------------------------------- | ------ | -------------- | -------- |
| `inter-latin-wght-normal.woff2`    | Inter  | `wght 100–900` | latin    |
| `inter-cyrillic-wght-normal.woff2` | Inter  | `wght 100–900` | cyrillic |
| `mohave-latin-wght-normal.woff2`   | Mohave | `wght 300–700` | latin    |

The `@font-face` rules that bind these live in `src/assets/theming/_fonts.scss`, together
with the `unicode-range` values copied from the same upstream. The Cyrillic cut is there
because payee, category, and account names are user text; its `unicode-range` keeps it off
the wire until such a name is rendered.

Both faces are SIL OFL 1.1 — see [`OFL.txt`](OFL.txt).

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

There is no icon font here yet. `docs/design/components.md` specifies Material Symbols
Rounded, but no component uses an icon, so nothing is loaded. The first icon to ship
brings a woff2 subsetted to the glyph names actually used — two glyphs weigh about 1.3 kB
against 1.38 MB for the whole face — self-hosted here like everything else.
