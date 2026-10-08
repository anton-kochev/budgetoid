# ADR 0024 — Key the data inventory on the model and reconcile it against the catalog

- **Status:** Accepted
- **Date:** 2026-09-07
- **Area:** Infrastructure (schema classification, coverage gates)

## Context

Five coverage rules need to enumerate columns by what they are: the export must carry every column a
person owns, an erasure must reach every table, a log must carry no narrative value, every narrative
column must be ciphertext, and no coverage test should carry a column name of its own. Today ten
separate surfaces classify columns or tables, and they disagree on six axes — source of truth, grain,
polarity, whether a reason is a value or a comment, where the list lives, and even the spelling of an
identifier. Most of the 93 mapped columns are classified by nobody.

This repository already has the rule that makes that a problem: *two executed lists that disagree
have no adjudicator, and the one that loses fails open.*

The inventory needs one source of truth for "every schema column", and there are two candidates that
are not the same set.

## Decision

**The inventory keys on the EF design-time model, and a container-backed test asserts that the model
and the live catalog describe the same columns, in both directions.**

The requirement says "a column exists **in the model**", a model walk needs no database, and that
keeps the gate in the unit tier where a build gate belongs. But every card that reads the inventory
makes a claim about the **database**, so keying on the model alone would fail open in the worst way:
a column outside the model is classified by nobody, nothing reddens, and that is indistinguishable
from a complete inventory.

The reconciliation is what converts a claim about the model into a claim about the schema. It was
measured when it was written — 93 mapped columns over 16 tables against 95 over 17, the difference
being `__EFMigrationsHistory` and its two columns, both directions empty — and that measurement is
the reason the story's premise holds rather than an assumption that it does.

**An entry excuses a relation, never a column.** `RelationsOutsideTheModel` holds one name and its
reason. A per-column list would offer a middle where a mapped table drops one column out of the
inventory under a reason written about the table.

## Consequences

The reconciliation ships **two permanent negative controls on separate hosts** — one adds a relation
the model does not map, one drops a column the model still does — so neither can pass for the other's
reason. It also asserts both sets are non-empty before comparing them: two empty sets differ in
neither direction, and that half is what stops the check agreeing with itself having examined
nothing.

`RelationsOutsideTheModel` lives in production code, not as a test constant, so a later story
tightening "no coverage test names a table" has nothing to relocate. The same call was already made
for `ProhibitedColumnVocabulary`, for the same reason: a build gate cannot reference a test assembly.

The inventory replaces nothing. `RowLevelSecurityCoverage` still classifies relations for tenancy
from the catalog; the `bytea` census still argues why holding each column unwraps nothing; the
deny-lists still fail open by design; the exact-column-set pins still say *this column must not
exist* where the inventory says *classify it*. A new column on `users` now reddens twice, and both
verdicts are wanted.

## Alternatives considered

**Key on the live catalog.** Every dependent is a claim about the database, so this is the honest
subject. Rejected because it pushes the inventory's own gate into the integration tier and demands a
container of every consumer, including two that otherwise need nothing running — and because the
requirement names the model.

**Key on the model alone, with no reconciliation**, on the ground that `HasPendingModelChanges()`
already proves the model matches the schema. It proves the model matches the **migrations**. It says
nothing about a relation created outside them — a view, a materialized view, anything a script left
behind. That gap is not hypothetical here: a `relkind = 'r'` discovery filter once hid all three
shapes and lost a whole table, which is recorded at the classifier that carries it.

**Fold the inventory into `RowLevelSecurityCoverage`**, since the objects look alike. They point in
opposite directions: that one *discovers* from the catalog and fails closed on a relation nobody
decided about; this one is *authored* and fails closed on a column nobody classified. Merging them
makes one of the two fail in the wrong direction.

**Derive the whole classification from the export**, since the export already knows which columns it
carries. Then the export's completeness gate compares the export against itself. Narrative is
derivable because the column's type is a decision made elsewhere; arithmetic against excluded is a
judgement about what a person owns, and there is no second source to check it against.

## The reason on an excluded column

`ColumnClassificationEntry` has no public constructor, three factories, and only the excluded one
takes a reason with no default. "An excluded column carries a reason" is therefore a compile-time
fact, and so is "a classified column carries no stray reason". A nullable member with a test
demanding non-null leaves the invalid state constructible.

A length floor is asserted and claims nothing more than that something was written. No assertion can
tell a real argument from a fluent one. **One reason per column, never one per table** — a reason
argued at table grain is inherited by columns it was never written about, which is the drift that
forced the row-level-security exemptions to grow the column set their reason covers.
