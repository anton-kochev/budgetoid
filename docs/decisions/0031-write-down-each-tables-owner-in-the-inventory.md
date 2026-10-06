# ADR 0031 — Write down each table's owner in the inventory and check it against the catalog's column fact

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Infrastructure (schema classification, coverage gates)

## Context

FR-029 asks that adding a table holding user-owned or budget-owned data fail the erasure coverage
test unless the erasure reaches it. NFR-022 names the data inventory as what that test reads, and
NFR-023 asks that adding a table cost no change to a coverage test beyond its entry in the inventory.
All three need one answer the inventory did not give: **which tables does an account own?**

The inventory classified **columns**, and by what the export owes a person — narrative, arithmetic,
excluded ([ADR 0024](0024-key-the-data-inventory-on-the-model-and-reconcile-it-against-the-catalog.md)).
None of the three words says whose rows a table holds. Ownership was written down in prose at the
row-level-security exemptions and in hand-maintained lists inside tests. The one the erasure endpoint
test reads said of itself that nothing checks it against the live schema, and it had drifted:
`key_rotations`, `key_rotation_seals` and `factor_manifests` were mapped, owned by a person, and
missing from it, with nothing reporting it.

## Decision

**The inventory carries a second, table-grain axis: `DataInventory.TableOwners`, one entry per mapped
table, each in one of three words — `User`, `Budget` or `Nobody`.** It lives in `TableOwners.cs`
beside `DataInventory`, and is shaped like the column classification:

- `TableOwnerEntry` has no public constructor. `User` and `Budget` take the owner column; `Nobody`
  takes a reason and no column. "An owned table names its column" and "a table owned by nobody says
  why" are facts about which factory was called.
- **The owner column is written out, never derived.** A reader scoping one account's rows reads it
  directly, so it has to be the column that reader should use, not the one a rule guessed.
- **A table owned by nobody is a decision, and a table nobody decided about is absent.** Absent is
  what `TableOwnerCoverage.Compare` reports as undecided.

It is held in two tiers.

**Unit tier, `TableOwnerCoverageTests`, against the EF design-time model.** Every table
`MappedSchema.TablesOf` returns has an entry, and every entry names a mapped table, with a floor
asserted first so an emptied list cannot pass. No table is named twice. Each owner column is a
mapped column of its own table, **and** it is the column its kind names: `id` on `users`, `user_id`
on every other `User` table, `budget_id` on every `Budget` table. A `Nobody` reason clears the same
80-character floor an excluded column's reason does. `Compare` takes both lists as parameters, so its
controls hand it an empty list and a list with a stale entry rather than reading the shipped one.

**Integration tier, against the migrated database:**
`DataInventoryReconciliationTests.TableOwners_AgreeWithTheOwnershipTheLiveCatalogReads`.
`TableOwnerCoverage.DisagreementsWith` compares each written word with `DiscoveredTable.Ownership`
from `RowLevelSecurityCoverage.DiscoverAsync` — `User` with `UserOwned`, `Budget` with `BudgetOwned`,
`Nobody` with `None` — and reports a table either side holds that the other does not.
`RelationsOutsideTheModel` is removed from the discovered side first, the same exclusion the column
reconciliation makes. `DiscoveredTable.Ownership` is a **column** fact: `budget_id` present, else
`user_id` present, else the table is `users`. The comparison reads that and never the exemptions,
the classification buckets or the policies.

## Consequences

**The two tiers catch different mistakes, and neither replaces the other.** The unit tier knows the
model's tables and columns and nothing about what owning means. Filing `recovery_code_hashes` as
`Nobody` with a long reason passes every unit-tier test and reddens only the reconciliation case —
that was measured, and the case records it. Filing `transactions` under `account_id` is the opposite
shape: a mapped column, a table the catalog agrees is budget-owned, and caught only by the unit
tier's column-by-kind test.

**NFR-023 is met for detection and is partial for repair.** A new mapped table is reported by name
in the unit tier the day it is mapped, with no test edited; the edit it asks for is its entry here.
A reader that checks **rows** rather than names — one that has to see a row of the account in a
table before an erasure to say the erasure removed it — cannot be satisfied by the entry alone. The
erasure tests hold two such readers: `AccountErasureEndpointTests` reports the new table `unseeded`,
and both whole-database tests in `ErasureAtomicityTests` assert that every table they count held
rows before the act. Each file seeds its account through its own helpers, so going back to green
costs a seed row in each of the two, and those are test edits NFR-023 asks not to need.

**The owner column is one column per table, so a reader scoping by it sees that column only.** A row
that names an account through some other column and not through its owner column is outside any
count scoped this way. Whole-database emptiness is a different question, and
`ErasureAtomicityTests.Erasure_WhenNothingFails_RemovesEveryOwnedRow` asks it: it counts the
relations `DiscoverAsync` returns — less views, partitioned parents and the tables its
`TablesOutsideTheTransactionBoundary` names — before and after a successful erasure of a database's
one account.

**A table mapping both owner columns is counted by `budget_id` alone.** The column fact reads
`budget_id` before `user_id`, so such a table agrees with the catalog only when filed `Budget`, and
the unit tier pins every `Budget` entry to `budget_id`; were that column nullable, a row with no
budget and the erased person's `user_id` would fall outside `AccountErasureEndpointTests`' count,
and an erasure leaving it would pass there. No mapped table carries both today, and closing it is
pending hardening.

**A table carrying neither owner column and filed `Nobody` agrees with the catalog by construction.**
The reconciliation cannot tell a table genuinely owned by nobody from an owned table keyed some other
way; both read `None` off their columns. For that case the reason is the hold, and the floor on it
claims only that something was written — the same limit ADR 0024 records for an excluded column.
Such a table also lands in `RowLevelSecurityCoverage`'s unclassifiable bucket unless an exemption
names it, which raises the question of isolation rather than of erasure. The erasure's backstop for
the hole is `ErasureAtomicityTests`' unscoped whole-database count, which never reads the owner
list: an owned table filed `Nobody` is still counted there and still has to be empty after a
successful erasure, provided the arrangement seeds a row of it — and the file's non-vacuity guard
stays red until it does. That is why the file keeps its own hand lists,
`TablesOutsideTheTransactionBoundary` and `TablesAnErasureDoesNotOwn`, and must not be rewritten to
read `TableOwners`: an oracle taken from the list under test agrees with it by construction.

**This extends ADR 0024 and leaves its "the inventory replaces nothing" standing.** The owner axis
reads `RowLevelSecurityCoverage`'s discovery and does not replace it: that type still decides which
relations owe a policy and which policy, and still fails closed on a relation nobody decided about.
Owning a table and policing it are different facts, and the first alternative below is what reading
one as the other would cost.

## Alternatives considered

**Derive ownership from row-level security — owned means policed.** Rejected because four owned
tables are exempt from a policy by design: `credentials`, `passkey_public_keys`,
`recovery_code_hashes` and `session_tokens`, each read before the request has an identity
([ADR 0011](0011-police-the-user-owned-tables.md),
[ADR 0012](0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md),
[ADR 0016](0016-give-recovery-code-hashes-their-own-exempt-table.md),
[ADR 0019](0019-authenticate-a-request-from-a-first-party-session-cookie.md)). Deriving from the
policies would call all four nobody's. `recovery_code_hashes` is the costliest of them to lose: a
redemption arrives anonymous and adopts the `user_id` it finds on the row, so a code an erasure
failed to take would be a live credential naming a person who asked to be forgotten — the argument
the erasure endpoint test makes for counting that table.

**Derive ownership from the columns, with no written list.** The catalog already reads a column
fact, so a list looks redundant. Rejected because a derivation answers every table, including one
that holds a person's rows under neither `user_id` nor `budget_id`: it reads `None`, a reader skips
it, and nothing says so. That fails open. A written list fails closed instead: a mapped table
nobody wrote down is reported in both tiers until somebody does — and writing it as `Nobody` means
writing a reason somebody can argue with — a relation outside the model that
`RelationsOutsideTheModel` does not excuse is reported by the reconciliation, and where a written
word and the column fact disagree, the reconciliation names the table.

**Four words instead of three, splitting `Nobody` into reference data and ceremony state.**
`currencies` and `webauthn_challenges` are owned by nobody for different reasons. Rejected because
nothing branches on the difference: the holds above ask owned-or-not and, if owned, by whom. The
reason carries the distinction where a person can read it, and a fourth word would be a value no
code reads.

**Reuse `TableOwnership` from `RowLevelSecurityCoverage`.** It already has three members that map
one to one. Rejected because its `None` means *undecided* — a table with no ownership column that
nobody has argued about yet, which that type refuses. Here `Nobody` is the opposite, a decision with
a reason, and undecided is the **absence** of an entry. One enum would merge the two, and a table
nobody had thought about would read as one somebody had.
