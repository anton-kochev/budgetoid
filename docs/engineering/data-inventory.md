# The data inventory

Every column of every table carries exactly one classification — **narrative**, **arithmetic** or
**excluded** — and the build fails on a column nobody classified. The inventory is the single source
the coverage tests read, so that adding a column is a decision made once rather than a change to be
remembered in five places.

`Infrastructure/Persistence/Inventory/` holds it: `DataInventory` (the 93 entries and the reader),
`DataInventoryCoverage` (the comparison), and `MappedSchema` (the enumerator).

## The three words classify what the product owes the person, not what the column holds

This is the sentence a reader gets wrong first, and getting it wrong produces an inventory that
passes every test and misclassifies a third of the schema.

| Word | Means | What a dependent reads from it |
|---|---|---|
| **narrative** | Text a person wrote. | Must be ciphertext; may not reach a log; must be in the export. |
| **arithmetic** | Server-readable, and part of what the person owns — the export is a copy of it. | Must be in the export. |
| **excluded** | Everything the export deliberately does not carry, each with a written reason. | Must be **absent** from the export. Makes no claim about encryption. |

Two worked examples, because the wrong reading survives review:

- **`credentials.created_at_utc` is excluded**, though it is an ordinary timestamp beside a dozen
  exported ones. A credential is not content a person owns, and the instant one was created is a
  fact about sign-in history. **The data type does not decide the word.**
- **`users.email` is arithmetic and is exported**, even though a separate requirement forbids it
  from a log record. That prohibition rides its own named list. A reader looking for the email in
  the narrative set and failing to find it has found the inventory working, not a gap in it.

## Narrative is derived; arithmetic and excluded are authored

`Of(Narrative)` must equal the model's properties typed `NarrativeField`, and a test holds that in
both directions. So a column cannot be quietly re-classified as arithmetic to green a coverage test,
and a ninth sealed column arrives classified whether or not anybody remembered to say so.

The split between arithmetic and excluded cannot be derived from anything: it is a judgement about
what a person owns. Deriving it from the export would make the export's own completeness gate
compare the export against itself.

**One structural detail that is load-bearing and looks incidental.** `NarrativeField` is a sealed
*class*, not a `readonly struct` — argued at the type itself, for an unrelated reason. Because of
that, the two nullable narrative properties report `NarrativeField` and not `Nullable<NarrativeField>`,
and the comparison is one line with no nullable arm. Were it a struct, that comparison would
silently find six of the eight.

## It keys on the model, and a reconciliation is what makes that a claim about the database

The requirement says "a column exists **in the model**", and a model walk needs no container, which
keeps the gate in the unit tier where it belongs. But every card that reads the inventory makes a
claim about the **database** — that a column is stored as ciphertext, that it appears in an export
of real rows, that an erasure reaches it.

So a container-backed test asserts that the EF model and the live catalog describe the same columns,
in both directions. Measured when it was written: 93 mapped columns over 16 tables against 95 over
17, the whole difference being `__EFMigrationsHistory` and its two columns, and both directions
empty.

`DataInventory.RelationsOutsideTheModel` is the written-down half, and it **excuses a relation, never
a column**. A per-column list would offer a middle where a mapped table drops one column out of the
inventory under a reason written about the table. It lives in production rather than as a test
constant so that a later story tightening "no coverage test names a table" has nothing to relocate.

The reconciliation ships two permanent negative controls, on **separate hosts** so neither can pass
for the other's reason: one adds a relation the model does not map, one drops a column the model
still does. It also asserts both sets are non-empty *before* comparing them — two empty sets differ
in neither direction, and without that half the check agrees with itself having examined nothing.

## An excluded column's reason is a compile-time obligation and a review-time judgement

`ColumnClassificationEntry` has no public constructor. Three factories, and only `Excluded` takes a
reason, with no default. So "an excluded column carries a reason" is a fact the compiler holds, and
so is "a classified column carries no stray reason". A nullable member plus a test demanding non-null
would leave the invalid state constructible, with the test the only thing standing between an author
and `""`.

A length floor is asserted over the reason. **It only makes writing nothing impossible.** No
assertion can tell a real argument from a fluent one — `KeyMaterialSecrecyTests` writes that limit
out at its own reason census, and this inventory inherits it rather than pretending otherwise. The
52 reasons are the review surface of this story, and nothing mechanises them.

**One reason per column, never one per table.** The saving is obvious and wrong: a reason argued at
table grain is inherited by columns it was never written about, which is exactly the drift that
forced `TableExemption` to grow the column set its reason covers.

## What this does not replace

The inventory is one axis. Several censuses ask different questions of the same columns and stay:

- `RowLevelSecurityCoverage` classifies **relations** for tenancy, discovered from the catalog, and
  fails closed on a relation nobody decided about.
- `KeyMaterialSecrecyTests.Classifications` argues, per `bytea` column, *why holding this unwraps
  nothing*.
- `ProhibitedColumnVocabulary` and its two siblings are deny-lists over **names**, failing open by
  design.
- `DataMinimizationSchemaTests` pins exact column sets for three tables; its verdict is *this column
  must not exist*, where the inventory's is *classify it*. A new column on `users` now reddens twice,
  and both are wanted.
- `AppRoleGrantMatrixTests` mirrors the grants file and says of itself that it is a mirror, not a
  judge.

**One gap, stated rather than closed:** the client's `NARRATIVE_FIELDS` is a second executed list, in
another language, and nothing reconciles it with the inventory at build time.

## Tests that lock it

- `DataInventoryCoverageTests` — the FR-005 gate, the empty-inventory and stale-entry controls, the
  duplicate check, the narrative derivation in both directions, and the reason floor.
- `DataInventoryReconciliationTests` — the model against the live catalog, with its two controls.
- `MappedSchemaTests` — the enumerator, including that it keeps the table with the column. Two
  columns named `name` on different tables is what proves it; an enumerator that flattens the table
  away collapses them into one, and the copies that existed before this all did.

There is deliberately **no** case asserting each column has exactly one classification. With one
non-nullable enum member per entry and a duplicate check beside it, that is a fact about the types,
and a test restating a type fact is a decoration.
