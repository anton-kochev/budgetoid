# Transactions

## Table of Contents

- [Purpose](#purpose)
- [Key Entities](#key-entities)
- [Constraints](#constraints)
- [Business Rules & Invariants](#business-rules--invariants)
- [Workflows & State Transitions](#workflows--state-transitions)
- [Decision Trees](#decision-trees)
- [Integration Points](#integration-points)
- [Edge Cases & Known Gotchas](#edge-cases--known-gotchas)

## Purpose

A **Transaction** is a signed amount recorded against one Account on a date. It can
optionally name a **Payee** and select a **Category**. What a transaction does with a payee — supply
a name and get back a row — is documented here; everything about the payee itself is in
[payees.md](payees.md).

## Key Entities

- **Transaction** — `Id`, `BudgetId`, required `AccountId`, signed `Amount`, `Date`, optional
  `Description`, optional `PayeeId`, optional `CategoryId`, `CreatedAtUtc`.

```mermaid
erDiagram
    BUDGET ||--o{ TRANSACTION : owns
    BUDGET ||--o{ PAYEE : owns
    ACCOUNT ||--o{ TRANSACTION : "recorded against"
    PAYEE ||--o{ TRANSACTION : "optionally names"
    CATEGORY ||--o{ TRANSACTION : "optionally categorizes"
    CATEGORY_GROUP ||--o{ CATEGORY : contains
    TRANSACTION {
        guid Id
        guid BudgetId
        guid AccountId
        guid PayeeId
        guid CategoryId
        decimal Amount
        date Date
        string Description
        datetime CreatedAtUtc
    }
```

## Constraints

### MUST

- **The Account must exist and belong to the current budget.**
  - **Why**: The account is what the movement happened to, and it supplies the currency every amount
    on the transaction is displayed in. A transaction against another budget's account would put one
    pool's money into another's picture.
  - **Enforced in**: `CreateTransactionHandler` resolves it through the budget-filtered repository
    and reports "Account was not found." otherwise.

- **A supplied Category must exist and belong to the current budget.**
  - **Why**: Categorization is what the money picture is grouped by, so a category from another pool
    would file this budget's spending under a heading that is not its own.
  - **Enforced in**: `CreateTransactionHandler` resolves `CategoryId` through the budget-filtered
    repository and reports "Category was not found." otherwise.

### MUST NOT

- **A Payee referenced by any Transaction MUST NOT be deleted.** The Transaction's existence is what
  makes the rule bite; the rule itself, its reasoning and its enforcement are stated once, in
  [payees.md](payees.md#must-not).

## Business Rules & Invariants

- **Rule**: The sign of Amount encodes direction — negative is an expense, positive is income. Zero
  is neither, and is a valid transaction.
- **Why**: The net effect on an account is then simply the sum of its amounts, and a "positive
  expense" contradiction is structurally impossible. Zero is legal because a ledger records what
  happened, not only where money moved: a fully discounted purchase, a refund that exactly cancels
  the purchase it reverses, or a zero-value invoice is a real event whose worth is its date, payee
  and category rather than its magnitude. Refusing it would not remove the event — it would force the
  user to invent an amount or drop the entry, and both store something less true than zero.
- **Enforced in**: nothing enforces the meaning; it is a semantic convention. `Transaction.Create`
  enforces only precision and magnitude, so no validation reads the sign at all.
- **Example**: groceries costing £40 are `-40.00`; a £1,500 paycheck is `1500.00`; an order that a
  voucher covered in full is `0`.
- **Counterexample**: recording an expense as `40.00` because the form already labels the row an
  expense makes the account's total climb with every purchase. Nothing rejects it — no validation
  reads the sign — so the mistake never surfaces as an error, only as a total nobody can explain.
- **Source**: `[SOURCE: discussion — 2026-07-28]`

---

- **Rule**: Amount has at most as many decimal places as its Account's currency has minor units, and
  an absolute value of at most 1,000,000,000.
- **Why**: Precision belongs to the currency rather than to the column — a yen has no sub-unit, a
  dinar has three — so a place beyond what the currency has implies a rounding or entry error rather
  than a smaller amount. The cap is a sanity bound against fat-finger entries.
- **Enforced in**: split by layer for the same reason as the identical rule on
  [accounts](accounts.md#business-rules--invariants). `CK_transactions_amount`
  (`abs(amount) <= 1000000000`) owns the magnitude bound, so it holds whatever wrote the row.
  `Transaction.Create` owns the decimal-places half alone, against the minor unit
  `CreateTransactionHandler` resolved from the Account's currency, and restates the magnitude bound
  for the message. That half has nowhere lower to go twice over: `numeric(14,4)` rounds an
  over-precise amount rather than refusing it, and a coercion is not enforcement under
  [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md), while a check
  constraint cannot read the currency's precision without joining another table. The reasoning is in
  [currencies.md](currencies.md#business-rules--invariants).
- **Example**: on a USD account `-40.00` is accepted and `10.005` is rejected as too precise; on a
  JPY account `-4000` is accepted and `-40.5` is rejected as not a whole number; `2000000000` is
  rejected as over the cap in any currency.
- **Source**: `[SOURCE: discussion — 2026-07-28]`

---

- **Rule**: Description is optional, a blank value is stored as null, and a value is at most 500
  characters.
- **Why**: The description is a free-text memo, so an empty string and "no memo" are the same thing
  and should not be two states a reader has to handle.
- **Enforced in**: `Transaction.Create`.
- **Example**: `"   "` is stored as null, not as a blank string.
- **Source**: `[SOURCE: discussion — 2026-07-26]`

---

- **Rule**: A Transaction may be uncategorized, even though every Category's own `CategoryGroupId` is
  required.
- **Why**: Recording that money moved must never be blocked on deciding what it was for — the entry
  has to stay fast enough to do at the till. An unfiled Category, by contrast, has no such excuse:
  the hierarchy is arranged deliberately, not in a hurry.
- **Enforced in**: `Transaction.CategoryId` is nullable; `CreateTransactionHandler` resolves a
  category only when one is supplied.
- **Example**: a `-12.50` corner-shop transaction with no category is valid and appears in lists with
  an empty category column.
- **Source**: `[SOURCE: discussion — 2026-07-14]`

---

- **Rule**: A Transaction may name a Payee or none, and the Payee is supplied as a free-text **name**
  while the Category is supplied as an existing **id**.
- **Why**: The asymmetry follows from when each is chosen. The counterparty is typed mid-entry, and
  making the user create one first would slow down the entry the model most needs to keep fast; the
  category is picked from a list they arranged deliberately, where a name would be a second way to
  say something they can already point at. Both are optional for the same reason a Transaction may be
  uncategorized: recording that money moved must never be blocked on describing it.
- **Enforced in**: `CreateTransactionCommand` carries a nullable `PayeeName` and a nullable
  `CategoryId`; `Transaction.PayeeId` is nullable and set only through `AssignPayee`.
  `CreateTransactionHandler` turns the name into a row by calling `IPayeeRepository.GetOrCreateAsync`
  in the ambient budget. What that call does with the name — trimming, case-insensitive matching,
  what a blank name means, and what happens when two requests race — is documented in
  [payees.md](payees.md#business-rules--invariants).
- **Example**: a transaction submitted with `payeeName: "tesco"` comes back carrying the `payeeId`
  and the stored spelling `Tesco` of the payee that already existed.
- **Source**: `[SOURCE: discussion — 2026-07-13]`

---

- **Rule**: Categorization is selected by Category ID only. Category Group is derived from the
  selected Category and is not copied onto the Transaction.
- **Why**: Storing the group too would let the two disagree the moment a Category is moved to another
  group, and there is no question the stored group could answer that the Category cannot.
- **Enforced in**: `CreateTransactionCommand` carries `CategoryId`; `Transaction` has no
  `CategoryGroupId`.
- **Example**: moving "Groceries" from "Essential Obligations" to "Household" changes what every past
  grocery transaction displays as its group, with no transaction rows written.
- **Counterexample**: copying `CategoryGroupId` onto the Transaction makes the two disagree the
  moment the Category moves — the transaction keeps naming the old heading while the category list
  shows the new one, and no read can tell which of the two was meant.
- **Source**: `[SOURCE: discussion — 2026-07-14]`

---

- **Rule**: Transaction responses are self-contained for display — they carry `CategoryId`,
  `CategoryName`, `CategoryGroupId` and `CategoryGroupName`, plus the account's name, currency code
  and symbol.
- **Why**: A transaction list has to render an amount and its context without the client stitching
  together three other endpoints, and the projection is from current data so a rename shows up
  immediately.
- **Enforced in**: `TransactionDto.FromTransaction`, fed by the handler's resolved account, currency,
  payee, category and category group.
- **Example**: renaming an account is visible in the transaction list on the next read, because the
  name is joined rather than snapshotted.
- **Source**: `[SOURCE: discussion — 2026-07-26]`

## Workflows & State Transitions

A Transaction has no lifecycle states and therefore no state machine: it is created and then read.
Nothing transitions it, because no update or delete path exists (see Edge Cases). The branching that
does exist is in creation, below.

## Decision Trees

Creating a transaction (`CreateTransactionHandler`):

```
IF the account id does not resolve in the ambient budget
  THEN validation error "Account was not found."          ← also the cross-budget answer
ELSE IF the account's currency code has no seeded Currency row
  THEN InvalidOperationException                          ← unreachable; the currency FK forbids it
ELSE
  Transaction.Create validates the amount's precision against that currency's minor unit,
    its magnitude, and the description length
  IF a CategoryId was supplied
    IF it does not resolve in the ambient budget
      THEN validation error "Category was not found."
    ELSE resolve its Category Group and assign the category
  IF a PayeeName was supplied and is not blank
    THEN find-or-create the payee in the ambient budget and assign it
  THEN persist and return the self-contained response
```

The category and payee steps are independent — either, both, or neither may run.

## Integration Points

- **[Accounts](accounts.md)**: required target; its Currency determines both the precision an amount
  may carry and how it is displayed. It cannot be deleted while Transactions reference it.
- **[Categories and Category Groups](categories.md)**: optional Category context. A referenced
  Category cannot be deleted; its Category Group cannot be deleted while the Category exists.
- **[Payees](payees.md)**: optional counterparty, and the only thing a Transaction can bring into
  existence. Transaction creation is the sole writer of the `payees` table, and a Transaction that
  references a payee is what makes that payee undeletable.
- **[Budgets](budgets.md)**: Transactions, Payees, Accounts, Categories and Category Groups are
  budget-filtered, and the `transactions → accounts | categories | payees` references are composite
  foreign keys so PostgreSQL, not only the query filter, refuses a cross-budget reference. Both rules
  and their reasoning live in [budgets.md](budgets.md#constraints). A Transaction's existence is also
  what makes its Budget undeletable, unlike the Budget's other owned entities.

## Edge Cases & Known Gotchas

- Transactions are append-only in the current implementation because update and delete are not built,
  not because immutability is a deliberate business rule. Do not cite the append-only behaviour as a
  constraint.
- **An untouched amount field is a zero, and only the client can tell the two apart.** An empty
  numeric input binds to `0`, so a form submitted with nothing typed records a perfectly valid zero
  transaction. The domain cannot refuse it without refusing the deliberate zeros the sign rule above
  allows, and the database knows even less, so the guard is the form's: **the amount input MUST
  require a value rather than default to one.** Under
  [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) that is the right layer
  — the rule is about the interaction, not about what a ledger may hold — but it is also the only
  layer holding it, with nothing underneath to catch a client that forgets.
- **Creating a transaction that names a new payee writes two rows in two separate database
  transactions**, the payee first. A failure between them leaves a payee no transaction references,
  and payees cannot be deleted — see [payees.md](payees.md#edge-cases--known-gotchas).
- Renaming a Category or Category Group, or moving a Category, immediately changes historical
  Transaction display; see [categories.md](categories.md#edge-cases--known-gotchas).
- A missing Currency row for an Account fails loudly rather than guessing a symbol, but the
  `accounts.currency_code` foreign key means it cannot happen — see
  [currencies.md](currencies.md#edge-cases--known-gotchas).
- `Date` is a calendar date with no timezone. `CreatedAtUtc` is the separate audit timestamp.
