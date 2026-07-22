# Principles

Nine rules, in priority order. Every other chapter is these rules made concrete. They
derive from [`docs/product/problem.md`](../product/problem.md): the product promises
calm, plain answers, and money assigned to its purposes before it is spent — the
interface must keep those promises visually.

## 1. Plain answers, never judgment

Every screen answers its question at a glance, in words a person uses. The system never
says money was spent badly; it says, plainly, what is still free to spend. No shame
colors, no warning badges on ordinary behavior, no gamification. Copy states facts and
offers the next step ([voice](voice.md)).

## 2. Calm is the product

Money UIs usually shout. This one behaves like a well-kept record: quiet surfaces, few
colors, nothing blinking for attention. If an element competes for attention without
carrying information, remove it. Loud is a defect.

## 3. The central number is "available"

The hierarchy of every money screen leads with what is still available for a purpose —
not what was spent, not the account balance. Spending history and balances exist to
serve that number, and layouts rank them accordingly ([patterns](patterns.md)).

## 4. Numbers are the interface

Monetary figures are typography: always Inter, always tabular, aligned in columns,
cents never dropped or shrunk — the record is honest ([typography](typography.md)).
When a figure and a decoration compete for space, the figure wins.

## 5. Expenses in ink, not red

Spending money is normal, so expenses are set in plain text ink. Color is reserved for
meaning: income in text-safe green, near-limit in burnt orange, over-budget in red —
and red appears only for genuine overruns, never as decoration ([color](color.md)).

## 6. One accent, paper and hairlines

Warm paper, white cards, warm hairlines — no shadows on the page plane, no pure grays,
no cool whites. Mint is the single interactive accent and doubles as the positive hue.
Gold is brand, never semantic. Everything else is neutral ([color](color.md),
[layout](layout.md)).

## 7. Entry costs seconds

The honest record only exists if writing a transaction down costs seconds. The entry
flow pre-answers every field it can — date, account, payee, category — so the person
supplies the amount and confirms. Any added step must justify itself against this
budget ([patterns](patterns.md)).

## 8. Phone first, one hand

Every layout starts at phone width and grows through `min-width` breakpoints; primary
actions sit in thumb reach; touch targets are 48px. CSS Grid is the default layout tool
([layout](layout.md)).

## 9. Material underneath, never visible

Angular Material (M3) supplies behavior, focus management, and accessibility plumbing.
This system supplies every visible decision through tokens. Nothing ships looking
default ([components](components.md)).

## The signature

The Halo Seam mark lends the interface its one ownable motif: the **bead**. It marks
"you are here" — active navigation, the position on a budget meter, the pulse of an
empty state. One motif, many small jobs; never more than one bead moment per view
([components](components.md), [motion](motion.md)).
