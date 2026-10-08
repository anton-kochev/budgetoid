# ADR 0002 — Enforce each rule at the lowest layer that can enforce it declaratively

- **Status:** Accepted
- **Date:** 2026-07-28
- **Area:** Architecture / Cross-cutting (rule enforcement across database, application, client)

## Context

Three layers in Budgetoid can say "no" to the same thing: PostgreSQL constraints, indexes and
collations; the .NET domain and application handlers; and the Angular client. Nothing in the
repository said which of them *owns* a given rule, so placement was decided per rule, by whoever
wrote it, on whichever layer was convenient at the time.

The question was settled entity by entity in a walk through the whole domain, and the same answer
came out every time. That answer lived in a conversation and in a per-user, per-machine assistant
memory file that no other contributor and no other AI assistant can read. A rule every contributor is
expected to follow has to live in the repository, which is what this record is for.

The stakes are visible in the schema already. The budget boundary between two rows is a schema
guarantee because a query filter enforces nothing on a write: composite foreign keys refuse a
cross-budget reference whatever code path produced the row, and unique indexes are what make name
uniqueness and provisioning race-safe. The same reasoning applies to every rule the domain has, and
without a stated principle it has to be re-argued each time.

## Decision

**Every rule is owned by the lowest layer that can enforce it declaratively — database first, then
application, then client.** A rule placed lower holds for code paths that do not exist yet; a rule
placed higher holds only for the paths that remember to ask.

**Declarative, not procedural.** No business logic is pushed into triggers or PL/pgSQL merely to
satisfy "lowest layer". The live case is category position contiguity — positions must be contiguous
and zero-based. It *is* expressible in PostgreSQL as a deferred constraint trigger, and it
deliberately stays in the domain, in `CategoryOrdering.CloseGap`
(`BudgetoidApp/Domain/Categories/CategoryOrdering.cs:46`) and `CategoryGroupOrdering.CloseGap`
(`BudgetoidApp/Domain/CategoryGroups/CategoryGroupOrdering.cs:38`). Enforcing it at the bottom would
mean comparing every sibling row on each write.

**Invariants go down, policy stays up.** Domain invariants belong in the schema: a transaction
belongs to exactly one budget, and recorded money movement is never orphaned. Product policy belongs
above it, because the bottom is the most expensive layer to change — a migration, a deploy, and
possibly a lock and a backfill.

**Upper layers may restate a rule for error quality and UX, never for enforcement.** A raw `23505` is
not the sentence "Account name must be unique." — that sentence comes from
`AccountRepository.DuplicateNameValidationException`
(`BudgetoidApp/Infrastructure/Repositories/AccountRepository.cs:88`), while the unique index is what
makes the rule true. This is legitimate layering, not duplication to be collapsed; the failure mode to
guard against is a future reader deleting either half as redundant.

**A violation report names one rule, not every rule that was violated.** A single row can breach two
constraints at once, and PostgreSQL returns one constraint name, chosen by index creation order
rather than by anything the caller did. Filtering a catch by constraint name therefore answers
*whether this code models the failure*, not *what went wrong*; where two rules can fire together,
the discrimination has to come from a second question the application asks after the rejection.
`RegisterAccountHandler` asks it by re-reading the credential's provider and subject
(`BudgetoidApp/Application/Registration/RegisterAccountHandler.cs`), because a losing concurrent
registration duplicates a subject and its email in the same write.

**A precheck is racy by design.** `IBudgetRepository.HasTransactionsAsync`
(`BudgetoidApp/Domain/Budgets/IBudgetRepository.cs:44`, implemented at
`BudgetoidApp/Infrastructure/Repositories/BudgetRepository.cs:22`) is check-then-act and exists for
the *message*; the foreign-key constraint is what is *correct*. Neither half is a defect: nobody
should "fix" the race with a lock, and nobody should drop the constraint on the grounds that the check
already covers it.

**"The database enforces it" means rejects, not coerces.** A `numeric` column's scale does not reject
an over-precise value — PostgreSQL silently rounds it. Inserting `0.00005` into a `numeric(14,4)`
column stores `0.0001` and raises nothing. The scale bounds what is *representable*; the *rejection*
rule stays domain-owned. A coercion that quietly changes the caller's data is not enforcement, and
treating it as such would leave a rule with no owner at all.

**Where database rules physically live.** Schema — constraints, indexes, collations — lives in the
single regenerated baseline migration, and the repository keeps exactly one, pinned by
`Migrations_ContainASingleFreshBaseline`
(`BudgetoidApp/tests/IntegrationTests/BudgetoidDbContextConstructionTests.cs:302`). Roles and grants
cannot live there: `dotnet ef migrations add` discards hand-added `migrationBuilder.Sql(...)` on every
regeneration. They belong in **provisioning** instead — the Aspire AppHost locally, azd/bicep in
Azure. This is the first thing whoever introduces a least-privilege application role will hit.

**A rule that stays above its lowest capable layer says why.** Position contiguity, read-side
isolation and decimal precision are all such cases. Each one is documented where the rule is
documented, so the next reader does not apply the principle mechanically and start writing triggers.

## Alternatives considered

- **Enforce primarily in the application, with the database as storage.** Rejected — a query filter
  enforces nothing on a write, and every future code path that bypasses the filtered repositories
  loses the guarantee silently, with no failure signal to notice it by.
- **Push everything expressible downward, including procedural triggers.** Rejected — this is the
  boundary above. Business logic in PL/pgSQL is invisible to the type system, untested by the unit
  suite, and versioned only by migrations.
- **Adopt Row-Level Security now.** Composite foreign keys pushed *writes* down to the schema; *reads*
  still rely on the application-layer `BudgetIsolation` query filters
  (`BudgetoidApp/Infrastructure/Persistence/BudgetoidDbContext.cs:43-51`). RLS is the genuinely lower
  option for reads. Rejected *for now* — it needs `SET LOCAL` per request under connection pooling,
  complicates the Aspire wiring, and is materially harder to test. Deferred, not discarded: until it
  is taken, reads are protected by application code and must not be assumed protected at the bottom.

## Consequences

- Moving a rule into the schema costs a migration and a hand-updated catalog snapshot test, and
  changing it later costs the same again — deliberately, since that price is the reason policy stays
  above invariants.
- The one-baseline convention constrains what may be expressed as a migration at all. Anything that
  cannot survive regeneration — notably roles and grants — has to be provisioning, not schema.
- Database-level behavioural tests become the *primary* tests for the rules that live down there, not
  an extra pass over rules already covered elsewhere. Behavioural tests assert the rule; catalog
  snapshots assert the rule *set*, catching drift in constraints nobody wrote a behavioural test for.
  Both shapes already exist in `BudgetoidApp/tests/IntegrationTests/`.
- Some rules are consciously left above their lowest capable layer, and each such case carries the
  reason in the doc that describes the rule. A rule found high with no stated reason is a defect, not
  a precedent.
- Error messages stay an application concern permanently. Adding a constraint does not remove the
  obligation to translate its violation into a sentence a person can act on.
