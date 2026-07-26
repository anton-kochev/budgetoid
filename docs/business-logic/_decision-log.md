# Business Logic Decision Log

Chronological record of non-obvious business decisions. Newest entries go at the top; existing
entries are never edited. If a decision is reversed, add a new entry referencing the original.

Infrastructure/architecture decisions (auth, hosting, DB) live in `docs/decisions/` (ADRs), not
here — this log is for **business/domain** decisions only.

---

## 2026-07-26 — A budget holding transactions cannot be deleted; its empty structure still cascades

**Context:** Every entity a budget owns cascaded from `budgets.id`, so a single budget delete would
take accounts, category groups, categories, payees **and every transaction** with it. That treats
recorded money movement and the scaffolding around it as equally disposable. They are not: structure
can be retyped from memory, financial history cannot, and a mistakenly created budget is a real
scenario a user must be able to undo without an archive feature existing first.

**Decision:** Make `transactions.budget_id → budgets.id` **`Restrict`** while its four siblings —
`accounts`, `category_groups`, `categories`, `payees` — stay **`Cascade`**, so **a budget that holds
any transaction cannot be deleted at all**, and a budget with no recorded movement is deletable and
takes its structure out with it. Recorded money movement is what deserves the guard; empty
scaffolding does not, and protecting it too would only trade a lost history for a budget the user can
never get rid of. Add `IBudgetRepository.HasTransactionsAsync` — implemented in `BudgetRepository` as
a `BudgetIsolation`-filtered `Transactions.AnyAsync` — as the application-side seam for asking the
question. It takes **no budget id**: tenancy comes from the query filter via `IBudgetContext`, the
system's only authorization mechanism, so a caller-supplied budget id would be a tenancy parameter
with no ownership check to pair with it. Accepting that the delete policy is no longer uniform across
the five owned tables — the asymmetry has to be explained rather than inferred, and a future reader
may take it for an oversight — and that, because PostgreSQL fires referential actions in foreign-key
creation order, the refusal is guaranteed while *which* constraint reports it is not, so any future
delete feature needs an application precheck to explain itself rather than an error translation.

**Alternatives considered:** *Make all five references `Restrict`* — rejected: an empty, mistakenly
created budget would then be undeletable, so undoing a typo would require building an archive feature
first. *Keep all five `Cascade` and guard the delete in application code only* — rejected: a single
unguarded write path silently destroys financial history, and the database is the only control that
still holds when application code is wrong. *Give `HasTransactionsAsync` a `budgetId` parameter and
`IgnoreQueryFilters()`* — rejected: that token is forbidden on this DbContext and a CI guard for it
is planned, and the parameter would reintroduce tenancy as an argument no ownership check validates.

**Affected areas:** [budgets.md](budgets.md), [transactions.md](transactions.md). This partially
reverses "Budget replaces the user as the unit of tenancy" below, which shipped uniform `Cascade`
from a budget to all five entities it owns without recording that as a decision; the rest of that
entry stands.

---

## 2026-07-25 — Transaction references are same-budget in the schema, not just in application code

**Context:** `transactions` reached `accounts`, `categories` and `payees` through plain single-column
foreign keys, so the database would accept a transaction pointing at another budget's row. Nothing
produced one, because `CreateTransactionHandler` resolves every reference through a
`BudgetIsolation`-filtered repository — but the guarantee lived entirely in application code, and a
query filter enforces nothing on a write.

**Decision:** Give `Account`, `Category` and `Payee` an alternate key `(Id, BudgetId)` and reference
them through composite foreign keys `(account_id, budget_id)`, `(category_id, budget_id)` and
`(payee_id, budget_id)` → `(id, budget_id)`, so **PostgreSQL refuses a cross-budget reference**
whatever code path wrote the row. Optionality survives free: a multi-column check is skipped entirely
when any of its columns is NULL (MATCH SIMPLE). The payee reference takes `Restrict`, making **a
referenced payee undeletable** — the guard accounts and categories already have, forcing an explicit
decision about historical rows instead of silently erasing the counterparty from past transactions.
Accepting that the three alternate keys create `UNIQUE (id, budget_id)` indexes redundant with each
primary key: PostgreSQL requires a unique constraint on a foreign key's referenced columns, and
promoting the primary key to `(id, budget_id)` would break every single-column foreign key and every
by-id lookup, so the redundancy is unavoidable — already accepted once for `category_groups`.

**Alternatives considered:** *Leave the boundary to application code* — rejected: every future write
path that bypasses the filtered repositories loses it silently, with no failure signal. *Keep SET
NULL on the payee reference via PostgreSQL 15+ column-list `ON DELETE SET NULL (payee_id)`* —
rejected: unreachable from EF Core 10, whose `ReferentialAction` has no column-list variant, and raw
SQL would need re-applying on every baseline regeneration while the model snapshot still recorded
`SetNull`, leaving the tooling diffing against a lie. *Exclude payees to preserve SET NULL* —
rejected: it leaves one of the three references unprotected for a delete path no application code
exercises.

**Affected areas:** [transactions.md](transactions.md), [budgets.md](budgets.md),
[categories.md](categories.md).

---

## 2026-07-25 — Budget replaces the user as the unit of tenancy

**Context:** A person can preside over more than one pool of money — funds for an event, a club, or a
relative are under their control without being part of their own life — and forcing those pools into
one total falsifies the single picture rather than simplifying it. Ownership was per user, which made
that impossible to express. The envelope-budgeting layer about to be built (allocations, carryover,
month view, base currency) hangs off whatever the unit of tenancy is, so it had to be settled before
that layer exists; retrofitting tenancy underneath a finished envelope layer would be a far larger
change.

**Decision:** Put a **Budget between the user and everything else** — a user owns budgets, a budget
owns accounts, category groups, categories, payees and transactions — because the single picture
belongs to a coherent pool of money, not to a person. `UserId` is **dropped** from those five
entities in favour of `BudgetId`, leaving `Budget.UserId` as the only owner link; name uniqueness and
ordering re-scope to the budget; payees do not cross budgets. Isolation moves from `UserIsolation` to
`BudgetIsolation` query filters reading an ambient `IBudgetContext` resolved once per request. The
schema is **multi-budget-ready from day one** — no one-budget-per-user constraint at all; the unique
index is `(user_id, name)`, which still makes provisioning race-safe and idempotent because the
default budget's name is a constant, so two racers collide and the loser adopts the winner's row. The
default budget is created **inside `EnsureUser`**, unconditionally on every authenticated request, so
"an account exists ⇒ it has its budget" stays one idea and a partially provisioned user heals on the
next sign-in. Shipped as a **single fresh initial migration** with no data migration, accepting the
loss of existing development data.

**Alternatives considered:** *Keep both `UserId` and `BudgetId`* — rejected: it creates an
`entity.UserId == entity.Budget.UserId` invariant enforceable only by composite foreign keys on all
five tables, protecting a column no query reads. *A unique index on `user_id` alone as a temporary
one-budget-per-user guard* — rejected: a constraint that a later release must remember to drop is a
trap, and the one-per-user property is better pinned by tests over the only code path that inserts a
budget. *A separate provisioning handler for the budget* — rejected: it opens a window where a user
exists with no budget, after which every filtered query throws. *A lazy budget lookup inside the query
filter* — rejected: a synchronous property getter issuing a query on the very context being queried.
*A budget identifier in routes* — rejected: it turns tenancy into a client-supplied, tamperable
parameter and makes the concept visible to a user who has only one budget.

**Affected areas:** [budgets.md](budgets.md) (now the canonical home of the tenancy invariant),
[users-and-ownership.md](users-and-ownership.md), [accounts.md](accounts.md),
[categories.md](categories.md), [transactions.md](transactions.md), [currencies.md](currencies.md).

---

## 2026-07-14 — Replace flat Groups with required Category Group → Category hierarchy

**Context:** The original `Group` entity was actually a flat transaction category. It could not
represent a user-facing organizational heading such as “Essential Obligations” containing
“Groceries” and “Utility Bills,” and its name made the domain ambiguous.

**Decision:** Replace Group with two user-owned resources: every **Category** belongs to exactly one
**Category Group**, while a Transaction may still be uncategorized. Membership is required and
same-owner at the database level. Names are case-insensitively unique per user (Category names across
all groups), both levels use persisted custom order, and typed PATCH operations move/reorder items.
Block deleting a non-empty Category Group and a Category referenced by Transactions. Read historical
Transactions through current Category/Category Group names rather than snapshots. Make a clean API
and schema break with no `/api/groups` aliases or data migration.

**Alternatives considered:** Keep Group as the Category name and add a loosely associated heading —
rejected because it preserves ambiguous terminology and permits invalid membership. Optional
Category Group membership — rejected because orphan Categories violate the intended hierarchy.
Nested or many-to-many groups — rejected as unnecessary complexity. Alphabetical order — rejected
because users need deliberate personal organization. Snapshotting names on Transactions — rejected
because renames and moves should update historical display.

**Affected areas:** [categories.md](categories.md), [transactions.md](transactions.md),
[users-and-ownership.md](users-and-ownership.md). This reverses the Category portion of the
2026-07-13 account/group deletion entry below; the Account decision remains unchanged.

---

## 2026-07-13 — Payees are free-text find-or-create, not a managed list

**Context:** A transaction can name a counterparty (payee). We had to decide whether payees are a
first-class thing the user creates and manages, or something lighter.

**Decision:** Treat a payee as **free text with autocomplete**, created automatically (find-or-create
by name, case-insensitive) as a side effect of recording a transaction — no create/edit/delete payee
UI or endpoints — to keep transaction entry fast and frictionless, accepting that payees can't be
renamed or pruned directly and that near-duplicates are only prevented by case-insensitive matching.

**Alternatives considered:** A managed payee list with its own CRUD and an FK picker on the
transaction form — rejected as heavier than a personal budgeting app needs and slower to use. A plain
free-text string with no entity at all — rejected because a shared payee entity is what powers
autocomplete and consistent naming across transactions.

**Affected areas:** [transactions.md](transactions.md) (Payees).

---

## 2026-07-13 — Deleting an account or group with transactions is blocked, not cascaded

**Context:** Users can delete accounts and groups. Those entities may have transactions pointing at
them. We had to decide what happens to the transactions.

**Decision:** **Block the delete** while any transactions reference the account or group (a validation
error the user must resolve), rather than cascade-deleting or silently nulling the references — to
protect financial history from accidental bulk loss, accepting that the user must recategorize or
clear transactions before removing the account/group.

**Alternatives considered:** Cascade delete (remove the transactions too) — rejected: a single
mis-click could wipe months of records. Null the reference and keep the transactions — rejected for
accounts (a transaction with no account has no currency/context); considered less harmful for groups
but kept symmetric with accounts for consistency and predictability.

**Affected areas:** [accounts.md](accounts.md), groups (now [categories.md](categories.md)).

---

## 2026-07-13 — Transaction direction is the sign of a single Amount

**Context:** A transaction is either money in (income) or money out (expense). We had to decide how
to represent direction.

**Decision:** Encode direction as the **sign of one signed `Amount`** — negative = expense, positive =
income — rather than a separate type/flag field, so the net effect on an account is simply the sum of
its amounts and a "positive expense" contradiction is structurally impossible. Amount must be
non-zero (zero has no direction and records no movement).

**Alternatives considered:** A separate `TransactionType` enum (Income/Expense) plus an unsigned
amount — rejected as redundant with the sign, and it introduces an invalid-combination surface
(e.g. Type=Expense with a positive amount) that then needs its own validation. Two separate amount
columns — rejected as over-modeled for the need.

**Affected areas:** [transactions.md](transactions.md).
