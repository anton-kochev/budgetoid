# Budgetoid — Design Book Overview

## What this is

The design system for Budgetoid, across web and future native clients. It is the
authority for every visible and interactive decision: color, type, space, motion,
components, patterns, copy, and accessibility. Read the relevant chapter before building
or changing UI; when a change affects a rule here, update the chapter in the same commit.

Three documents sit around it:

- [`docs/product/problem.md`](../product/problem.md) — the product's reason to exist.
  Every design rule in this book traces back to it; features are measured against it.
- [`branding/BRAND.md`](../../../branding/BRAND.md) — the brand: the Halo Seam mark,
  its geometry, colorways, and usage rules. This book never restates the mark spec.
- [`branding/tokens.css`](../../../branding/tokens.css) — the canonical token values.
  Its runtime copy is `ClientApp/angular-budgetoid/src/assets/theming/_brand-tokens.scss`;
  the two must stay in sync, and both must match the tables in this book.

## The system in one paragraph

Budgetoid looks like a well-kept record, not a dashboard. Warm paper surfaces separated
by hairlines — never shadows on the page plane. Forest ink for what is read, one mint
accent for what is interactive or on track, and color spent only where it carries
meaning: spending is set in plain ink, not red. Two voices of type — Mohave speaks in
headlines, Inter does everything you read and every number you trust, always in tabular
figures. Angular Material (M3) provides behavior and accessibility underneath; this
system provides every visible decision. Nothing ships looking default.

## Chapters

- [Principles](principles.md) — the nine rules everything else derives from.
- [Color](color.md) — roles, both themes, state layers, contrast table, usage discipline.
- [Typography](typography.md) — the two-voice policy, type scale, monetary figures.
- [Layout](layout.md) — spacing scale, grid, breakpoints, surfaces and elevation, radii.
- [Motion](motion.md) — durations, easings, and when not to move.
- [Components](components.md) — anatomy, states, and Material mapping for every part.
- [Patterns](patterns.md) — money display, the entry flow, empty states, feedback.
- [Voice](voice.md) — tone, terminology, microcopy.
- [Accessibility](accessibility.md) — the commitments and how components meet them.

## How to use it when building

1. Start from [principles](principles.md); if a screen decision contradicts one, the
   screen is wrong.
2. Take every value from a token (`--bud-*` or `--mat-sys-*`) — a hard-coded hex, px
   gap, or duration in component styles is a defect unless this book names it.
3. Compose from [components](components.md) and [patterns](patterns.md) before inventing;
   if something new is genuinely needed, add its spec here in the same change.
