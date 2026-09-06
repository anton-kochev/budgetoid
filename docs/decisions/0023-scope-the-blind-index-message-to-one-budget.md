# ADR 0023 — Scope the blind-index message to one budget

- **Status:** Accepted
- **Date:** 2026-09-06
- **Area:** Client / Domain (blind index, tenancy, disclosure)

## Context

A name in a blind-indexed column is stored twice: as a ciphertext nobody but the account can
open, and as a keyed digest the database compares for equality. The digest is what makes "one
account name per budget" enforceable over bytes PostgreSQL cannot interpret. See
[account-keys.md](../business-logic/account-keys.md) and
[ciphertext-envelope.md](../business-logic/ciphertext-envelope.md).

The message was `budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ normalized-name`, and the index key
is drawn **once per account**. Neither the key nor the message named the budget. So one name
written into two budgets of one account produced a **byte-identical** digest, and an operator
holding full read access could see that equality — learning that two of somebody's budgets held a
payee, an account, a category or a category group of the same name, without ever learning the name.

NFR-014 forbids exactly that. Two things kept it from being noticed. The suite already wrote the
leak down as an accepted disclosure, in a census justification that says an operator "would learn
of any repeat across the account's budgets that two names are the same word without learning the
word". And the requirement's own analysis asserted the opposite in a sentence that refutes itself
— *"the index key is derived per account, so identical names in two budgets produce unrelated
values"* — where being per account is precisely the reason they are identical. NFR-014's
verification method is Analysis, so the requirement was held by that sentence and by nothing else.

Nothing in the product creates a second budget today: there is no budget endpoint, and registration
writes exactly one. The defect was therefore latent rather than live.

## Decision

**The budget is a field of the blind-index message.**

```
budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized-name
```

The budget takes the **fourth** slot, which is where the narrative grammar keeps its row
identifier, so the two codecs read alike side by side. The version prefix does **not** move: no row
was ever deployed under the older grammar, so a `v2` would name a predecessor nobody ever met.

The value is **refused, never folded** — the same call `narrative-cipher.ts` makes about its row id.
Folding defends against values arriving from many places; this one arrives from a single route in a
single spelling, and folding would invent a second spelling at the writing end, where the damage
cannot be undone. `refuseInvalidIndexBinding` makes both judgements — the table-and-column pair
looked up as a pair, and the spelling — because a second refusal beside it would be a second opinion
about what is legal.

**The browser learns which budget it is in from `GET /api/me`**, read from the ambient budget the
request is actually scoped by. It is the one field of the message the client cannot derive: the
grammar and the pair are its own constants, the name is what somebody typed, the index key is in
custody, and the budget is resolved server-side from the session cookie and named in no request and
no other response.

## Why now, with nothing observable broken

The digest is stored in a column, and only a browser holds the key that could recompute one.
Changing this message later means every client re-indexing every row under a key no server has —
not a migration anybody can run. There is no production environment, there is one client, and the
only rows are local development data. This is the cheapest the change will ever be, and the cost
rises monotonically from here.

## Consequences

**Every `name_key` written under the old grammar is silently wrong.** Not unreadable — the digest is
still stable, still 43 characters, still collision-free. It simply is not the value the browser now
computes, so a lookup misses and a duplicate is created where a match was meant. It cannot be fixed
by SQL or by the application; the local database is destroyed and rebuilt instead.

**The unique indexes stay composite.** `budget_id` remains the leading column of all four. A digest
that differs is a property of a conforming client; the column is a property of the schema, and the
schema is what a non-conforming client meets.

**`GET /api/me` publishes an identifier, and the record that forbade that is rewritten rather than
worked around.** The half a test holds — no route accepts a budget or a user as a path segment or a
route parameter — survives untouched, so a client holding the value has nowhere to spend it. The
half that ends is the premise that nothing may be published at all. What earns a member is not that
a screen displays it but that the client cannot derive it and cannot complete its half of a
cryptographic contract without it. A user id fails that on the second clause and stays unpublished,
as do a session id, a credential id and a creation timestamp.

**A write whose budget is not yet known does not happen, and answers `unreachable` rather than
`locked`.** No factor can produce a budget, so `locked`'s advice — present another factor — cannot
come true; the only cause is a read that did not land.

## Alternatives considered

**A per-budget index key.** Cryptographically equivalent to naming the budget in the message, and
worse in every other respect: a wrapped envelope per budget per factor, a "budget with no key" state
the schema cannot forbid, and a direct contradiction with `wrapped_account_keys` being keyed on the
factor. Scope belongs in the message.

**A separate `budgets.index_scope` column**, so the old prohibition on publishing an identifier
stayed literally true. It buys a shadow identifier with the same reach as the one it replaces, and
costs an additive migration, a second wire member, and a second value that must never change for the
life of the account with nothing holding it to that — every name in the account keyed on it, so one
edit re-keys rows nothing can find again, silently. The sentence is what moves.

**Leaving the requirement as it stood and correcting the analysis instead.** Available, since nothing
creates a second budget. Rejected because the requirement is a Must, the analysis that verified it
was wrong rather than merely optimistic, and the fix becomes impossible the day a real account
exists.
