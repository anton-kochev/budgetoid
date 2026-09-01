# ADR 0022 — Mint narrative row identifiers on the client

- **Status:** Accepted
- **Date:** 2026-08-25
- **Area:** Domain / Client (encryption bindings, identifier custody)

## Context

A narrative field is sealed under the account's content key and bound to **where it lives**:
the table, the column and the row. That binding is the associated data, and associated data is
not carried inside the envelope — it is rebuilt from wherever the ciphertext was found, which
is exactly what makes a ciphertext moved to another row fail to authenticate rather than
decrypt into something. See [ciphertext-envelope.md](../business-logic/ciphertext-envelope.md).

The grammar therefore needs the row's identifier **at the moment the client seals**, and on an
insert the client does not have one. Every row a narrative field could be bound to took its id
server-side inside a Domain factory: `Transaction.Create`, `Payee`, `Account`, `Category`,
`CategoryGroup` and `Budget` each called `Guid.CreateVersion7()` and assigned the result to `Id`. So
on the path that matters most — creating a row whose narrative is encrypted — the client sealed
before the row existed, and **the row half of the binding was unreachable**.

Dropping the row from the grammar is not an option available here: without it, every row in a
column is interchangeable with every other, which is the exact case the binding requirement
names.

## Decision

**A narrative row identifier is minted by the client, in the canonical lower-case
36-character hyphenated spelling of a version-7 UUID, and the server refuses every other
spelling rather than normalising one.**

This is the call [ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
already made for `factor_id`, made again for the same reason and with the same consequence
when it slips.

1. **Client-minted, because the alternative has no moment to run in.** The value has to be
   known to the sealing client before the row exists. Nothing about a row id is secret and
   nothing depends on the server having chosen it — the id's unguessability does real work only
   on `credentials`, whose deletes are issued by primary key against a table carrying no
   row-level security policy, and which is why that id stays server-minted (ADR 0014).

2. **Version 7, not version 4.** Every identifier a Domain factory mints is a version-7 UUID
   — `Transaction`, `Payee`, `Account`, `Category`, `CategoryGroup`, `Budget`, `Session` and
   `Credential` all assign `Guid.CreateVersion7()` — which is time-ordered and therefore
   locally clustered in an index. A row id drawn any other way keeps uniqueness and loses that
   locality on the tables that will hold the most rows.

   **That is not "every identifier in the schema", and the two exceptions are deliberate.**
   `factor_id` is minted on the client by `factor-id.ts` with `crypto.randomUUID()` — version
   4 — for the reason set out below. And `users.id` is neither version: it is derived by
   `RegistrationAccountId.For` from the registration challenge and stamped **version 8**,
   RFC 9562's own slot for a value an application derived rather than drew, because the two
   legs of the ceremony have to reach one identifier without carrying it between them. That
   file records the trade in its own words — the scatter on the `users` primary key is a real
   cost, paid on the rarest insert in the product, and `Guid.CreateVersion7()` "is not an
   option here at any price: it is not a function of its input at all, which is the entire
   requirement."

   **Nothing that meets a *stored* identifier can tell a version-7 UUID from a version-4 one,
   so that half is held by review** — the same answer `CLAUDE.md` gives about a second
   account-creating path, and for the same reason: it is one line that would redden nothing.
   Stated plainly so the next reader does not go looking for the check that catches it. No
   column type, check constraint or policy can see a version nibble; the client predicate
   `isCanonicalFactorId` deliberately inspects neither the version nor the variant nibble, and
   argues for that at its own declaration — a rule invented at that layer starts refusing valid
   identifiers the day the authority over them changes its mind; and the server's canonical
   parse compares a *spelling*, not a version.

   **What a minter *emits* is a different question, and a spec answers it exactly.**
   `mintNarrativeRowId` pins the version nibble at character 14 of the canonical spelling and
   refuses a `crypto.randomUUID` shortcut by name, so the clause is enforced where it is
   enforceable. The **big-endian** timestamp needs its own case and gets one: a little-endian
   layout still produces a well-formed, unique, canonically spelled version-7 UUID that passes
   every predicate either side owns, and loses only the index locality the whole choice was made
   for — visible to nothing but two stubbed clocks and a fixed random tail. What remains held by
   review is which minter a write path calls.

   **Which is exactly what `factor_id` does, and the two answers are not in conflict.**
   `factor-id.ts` mints with `crypto.randomUUID()` — version 4 — and that is right there. The
   difference is **count, not the presence of an index**: `factor_id` *is* a primary key and has
   an index, but an account holds eleven factors in its whole life, so insert locality is a
   property nobody can measure on it. An account holds thousands of transactions. The same
   trade-off, weighed at two scales, lands on two answers — so the narrative minter is a new
   function rather than a second caller of `mintFactorId`.

3. **One spelling, and the server refuses rather than repairs.** The identifier is what the
   associated data was built from, so a client that sealed under one spelling and rebuilt
   another finds its own ciphertext unopenable — permanently, in both directions, with no error
   anywhere naming the cause. The two sides agree on the bytes by the server **never storing a
   value whose rendering differs from what it was sent**, which is a stronger property than any
   normalisation: a client that spells it another way is turned away at the write rather than
   discovering months later that its text does not open.

   The narrative grammar refuses a non-canonical row id for the same reason and **does not
   fold** one, which is the deliberate opposite of the wrapped-key grammar next door. That is
   not an inconsistency — folding defends against a value arriving from elsewhere, while
   emitting one spelling is a property of values a client mints — and both directions are
   argued in [ciphertext-envelope.md](../business-logic/ciphertext-envelope.md).

### Scope of this decision, stated exactly

This decision fixes **the rule, the canonical form, and the grammar that consumes it**. The
narrative grammar exists and refuses a non-canonical row id today.

**The client-side minter and the server-side refusal have both arrived; what is left is the rest of
the factories and a caller.** `+core/security/narrative-row-id.ts` mints a version-7 UUID in the
canonical spelling today, and its spec holds the version nibble, the canonical spelling and the
big-endian timestamp. **Nothing calls it**: no path seals a narrative field for a row it just created.

**Three of the six Domain factories have changed, and the first of them carries the decision's one
exception.** `Budget.Create` and `Budget.CreateDefault`, `Account.Create` and `Payee.Create` all take
the row's identifier as a parameter, and `Guid.CreateVersion7()` has left `Budget.cs`, `Account.cs`
and `Payee.cs` entirely rather than moving behind an overload — a caller that forgot to thread an id
through would otherwise compile, pass every test that does not assert the returned identifier, and
produce a row whose sealed name nobody can ever open. Each of the three refuses `Guid.Empty`, which
is reachable for the first time now that the value arrives from outside. **The exception is that
registration mints the budget's id server-side**, on one written-out line in `RegisterAccountHandler`:
the budget it creates carries **no name**, so nothing is sealed, there is nothing to seal against, and
the browser has no basis on which to choose. A budget that *is* named is created by whoever sealed the
name and hands its id in with it. See [budgets.md](../business-logic/budgets.md).

**The server-side parse that refuses a non-canonical row id is built, and two routes run it.**
`CanonicalIdentifier.TryParse` compares the supplied text **ordinally against what the parsed value
renders as**, per the Consequences below; `POST /api/accounts` and `POST /api/payees` each bind their
`Id` as a `string` and judge it there, first of three opaque members, because a spelling this API
cannot reproduce makes the envelope beside it irrelevant. **The two `PATCH`/`PUT` legs deliberately do
not**: on an update the client re-seals against the row's **existing** id, read back from this API in
the one form a `Guid` renders, so the text in a URL is never what anything was sealed under and there
is no spelling to preserve.

**The remaining three factories arrive with the work that encrypts their columns** —
`Transaction.Create`, `Category.Create` and `CategoryGroup.Create` still mint their own identifiers,
and no route accepts one for them. Read those parts of this document as the decision they will be
built to. **What is still unbuilt on the client is the caller**: `narrative-row-id.ts` mints, and no
path seals a narrative field for a row it just created, because no browser in this product seals
anything.

## Alternatives considered

**Insert the row, then seal, then update it.** Keeps every identifier server-minted and needs
no new contract. Two writes per created row, and — worse — a window in which the row holds the
person's text **in plaintext**, which is the property the encryption exists to remove. A
failure between the two writes leaves it there permanently.

**Bind the column and not the row.** The cheapest grammar, and it does close the swap between a
category's name and its description. It leaves a ciphertext free to move between rows of one
column, which is the case the binding requirement names outright: every payee name in the
account becomes interchangeable with every other, and a shuffle is a silent, successful
decryption rather than a failure.

**Ask the server for an identifier per field, or per row, before sealing.** Keeps ids
server-assigned and hands the client a value in time. A round trip per row created, on the
screen where a person is typing — the cost lands exactly where it is most visible, and it buys
a property (server authority over the value) that nothing here needs.

**A Hi/Lo endpoint handing out blocks of pre-minted identifiers.** The same idea with the round
trips amortised, and it is a real pattern. It solves *server authority over the identifier*,
which is the thing this decision does not need: the ids are not secret, nothing checks who
minted one, and no rule anywhere depends on the server having chosen it. It costs an endpoint,
a block-allocation table or sequence, an exhaustion story and a client-side reservation cache,
all to avoid a value the client can produce in one call.

**`crypto.randomUUID`, which mints version 4 only.** Available in every browser this app runs
in, needs no library, and keeps uniqueness — the argument for leaving the budget id out of the
narrative grammar survives on 122 random bits alone. What it loses is **index locality**: every
identifier a Domain factory mints is version 7 and clusters by creation time, and narrative rows
are the ones there will be most of. It is the closest of the five, and it is rejected on that
one property rather than on correctness.

## Consequences

- **A narrative row identifier joins `factor_id` as a value the client chooses.** The two are
  the only ones, and the reasons are the same in shape: an identifier that has to be known
  before the row it names exists. `credentials.id` stays server-minted, and ADR 0014's argument
  for that is untouched.
- **The canonical-spelling rule now has two subjects.** Whatever refuses a non-canonical row id
  server-side must compare the supplied text **ordinally against what the parsed value renders
  as** — the rule `CanonicalFactorId.TryParse` already keeps — because `Guid.TryParseExact` with
  `"D"` admits upper- and mixed-case hex and trims before it reads the format at all. On the
  client the question is already answered once, by `isCanonicalFactorId` in `factor-id.ts`,
  which `narrative-cipher.ts` imports under an alias rather than restating.
- **All three clauses are enforced somewhere, and the version is enforced at one end only.** The
  spelling is refused at the write and at the seal; client custody is a fact about which side
  calls the minter. The **version** splits in two, and collapsing the halves is how this bullet
  went wrong once. What a **minter emits** is asserted directly — `narrative-row-id.spec.ts`
  reads the version nibble, refuses a `crypto.randomUUID` shortcut by name, and pins the
  big-endian timestamp with stubbed clocks and a fixed random tail. What **which minter a write
  path calls** is remains held by review alone, and so does every *stored* identifier: no column
  type, check constraint or policy can see a version nibble, and neither client predicate nor
  server parse inspects one. Nothing added to the schema would change that half.
- **A mis-spelled identifier is the worst failure mode in this format and it is silent on both
  sides of the wire.** The write succeeds, every response says success, and the person finds out
  on the day the text stops opening. That is why the refusal sits at the sealing end as well as
  at the write, and why neither end folds.
