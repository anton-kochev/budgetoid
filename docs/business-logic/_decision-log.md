# Business Logic Decision Log

Chronological record of non-obvious business decisions. Newest entries go at the top; existing
entries are never edited. If a decision is reversed, add a new entry referencing the original.

Infrastructure/architecture decisions (auth, hosting, DB) live in `docs/decisions/` (ADRs), not
here — this log is for **business/domain** decisions only.

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

**Affected areas:** [accounts.md](accounts.md), [groups.md](groups.md).

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
