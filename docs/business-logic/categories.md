# Categories and Category Groups

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

A **Category** classifies a transaction, such as “Groceries” or “Utility Bills.” Every Category is
organized under exactly one user-defined **Category Group**, such as “Essential Obligations.” A
transaction may be uncategorized, but a Category itself may never be ungrouped. Both entities belong
to one budget (see [budgets.md](budgets.md)) and are manually managed by its owner.

**Both levels are sealed, and the asymmetry this chapter used to be organised around is gone.** All
four narrative columns across the two entities are AEAD envelopes: `category_groups.name` and
`categories.name` each carry a blind index beside them, and `category_groups.description` and
`categories.description` carry none. Nothing on this side reads, measures, folds or compares any of
the four. Every server-side rule about their text — trims, blankness checks, character ceilings,
case folding — is gone from both, so a rule stated here about one level is now a rule about the
other unless it says otherwise, and the differences that remain are about **scope** rather than
about text: a group's position is arranged budget-wide and a category's within its group, while
both names are unique **budget-wide**.

`categories.name` was the **last** of the four blind-indexed name columns to move, and
`categories.description` landed with it. Read that as a closing rather than a step: with
`transactions.description` sealed in the same slice, every one of the eight narrative columns the
grammar names now holds ciphertext, and no column anywhere in the product is waiting to be sealed.

## Key Entities

- **Category Group** — `Id`, `BudgetId`, `Name`, `NameKey`, optional `Description`, `Position`,
  `CreatedAtUtc`.
  - **`Name` is a sealed narrative envelope and not text.** The property is typed `NarrativeField`
    and the column is `bytea NOT NULL`. That type has no constructor, factory or conversion taking a
    `string`, so writing plaintext into this column does not compile — see
    [ciphertext-envelope.md](ciphertext-envelope.md). What the type forecloses on *this* table is a
    particular disclosure: a group name is the coarsest label in a person's ledger — "Medical",
    "Legal", "Debt" — so a handful of rows describe a life with no amount beside them.
  - **`NameKey` is the blind index over the same name** — `ReadOnlyMemory<byte>`, exactly 32 bytes
    (`HMAC-SHA-256` under the account's index key, computed in the browser), on its own
    `bytea NOT NULL` column. It is what the uniqueness rule is enforced over, and the reading it
    carries is the accounts one rather than the payees one: two groups under one name are a confusion
    a person can see and fix, not a deduplication mechanism the domain rests on.
  - **Both are written from one `IndexedName` parameter and never separately.** `CategoryGroup.Create`
    and `CategoryGroup.Update` each take one, and neither offers a spelling for half a name.
  - **`Description` is a sealed narrative envelope too, and it is the first sealed free-text column in
    the product.** Typed `NarrativeField?` against a **nullable** `bytea`, capped at
    `NarrativeFieldLimits.DescriptionBytes` rather than the name class's `NameBytes`, and carrying
    **no blind index, ever** — a description is not looked up, is not unique and is not a name. Its
    own rules are below; they are not the name's with a nullability note attached.
  - **`Id` is supplied to the factory, never minted inside it.** `Guid.CreateVersion7` has left
    `CategoryGroup.cs` entirely; the identifier is the associated data the client sealed **both**
    narrative members against.
- **Category** — `Id`, `BudgetId`, required `CategoryGroupId`, `Name`, `NameKey`, optional
  `Description`, `Position`, `CreatedAtUtc`. **Every one of the five bullets above is now true of
  this entity too**, word for word and for the same reasons, so they are not restated: `Name` is a
  `NarrativeField` against `bytea NOT NULL`, `NameKey` is a 32-byte `ReadOnlyMemory<byte>` against
  its own `bytea NOT NULL`, both are written from one `IndexedName`, `Description` is a
  `NarrativeField?` against a nullable `bytea` capped at `NarrativeFieldLimits.DescriptionBytes`
  and carrying no index, and `Id` is supplied to the factory rather than minted inside it —
  `Guid.CreateVersion7` has left `Category.cs` entirely, with no minting overload behind it. What a
  sealed category name forecloses is its own disclosure: a category list is the vocabulary somebody
  files their spending under, finer than the group headings above it, so it says what a life is
  made of at a level the headings only hint at.

```mermaid
erDiagram
    BUDGET ||--o{ CATEGORY_GROUP : owns
    BUDGET ||--o{ CATEGORY : owns
    CATEGORY_GROUP ||--o{ CATEGORY : contains
    CATEGORY ||--o{ TRANSACTION : "optionally categorizes"
    CATEGORY_GROUP {
        guid Id "client-minted, both envelopes' associated data"
        guid BudgetId
        bytea Name "sealed envelope, NOT NULL"
        bytea NameKey "blind index, exactly 32 bytes, NOT NULL"
        bytea Description "sealed envelope, nullable, no index"
        int Position
        datetime CreatedAtUtc
    }
    CATEGORY {
        guid Id "client-minted, both envelopes' associated data"
        guid BudgetId
        guid CategoryGroupId
        bytea Name "sealed envelope, NOT NULL"
        bytea NameKey "blind index, exactly 32 bytes, NOT NULL"
        bytea Description "sealed envelope, nullable, no index"
        int Position
        datetime CreatedAtUtc
    }
```

## Constraints

### MUST

- **Every Category must reference exactly one Category Group in the same budget.**
  - **Why**: An orphan or cross-budget Category would make the hierarchy invalid and could leak
    tenant data.
  - **Enforced in**: **database-owned.** PostgreSQL maps `(category_group_id, budget_id) → (id,
    budget_id)` as a non-nullable composite foreign key against the `category_groups` alternate key,
    so both halves — that there is a group, and that it is this budget's — hold whatever code path
    wrote the row. Above it, `Category.Create` requires a non-empty `CategoryGroupId` and
    `CreateCategoryHandler` / `PlaceCategoryHandler` resolve the destination through budget-filtered
    repositories, which is what turns another budget's group id into "Category group was not found."
    instead of a constraint violation — error quality rather than enforcement.

- **A category group's name reaches this server sealed, with its blind index beside it, and its
  description reaches it sealed with nothing beside it.** `category_groups.name` is the **fourth**
  column in the product to hold ciphertext and the **third** to carry a blind index;
  `category_groups.description` is the **fifth**, and the first sealed **free-text** column anywhere
  in the product.
  - **Why**: both are narrative text, which is the one thing this product is built not to be able to
    read. The name had to keep its uniqueness rather than surrender it — one group name per budget is
    how a person tells their headings apart — and a blind index is the only construction that lets a
    server holding no plaintext still refuse a duplicate. The description gets **no** index for the
    opposite reason: an index answers "which row holds this value", and a description is never looked
    up, never unique and never a name, so one would publish a deterministic per-account fingerprint of
    somebody's free text with nothing on the other side asking for it. That is a decision, not a gap
    to close in a later slice.
  - **Enforced in**: `CategoryGroup.Name` is typed `NarrativeField`, `CategoryGroup.NameKey` is a
    `ReadOnlyMemory<byte>` and both are assigned from one `IndexedName`, which refuses either half on
    its own; `CategoryGroup.Description` is typed `NarrativeField?` and reaches the entity through
    `NarrativeField.SealedOrAbsent`, the one factory that reads "nothing was supplied" as a value
    rather than as a malformed envelope. `CategoryGroupConfiguration` maps three `bytea` columns
    through **three** converters, each with a **content** comparer over the bytes — and the rule
    below is what a reader needs, because counting them as three of a kind is the mistake both
    tables invite.
    - **A table carrying a narrative pair and a blind index has three converters, and only two of
      them are the same kind of thing.** Stated once here and inherited by every such table rather
      than re-observed on each: `name` takes the non-nullable envelope converter and `description`
      the nullable one — **two narrative converters differing only in nullability**, which a reviewer
      will propose unifying and which each configuration argues against in place, because a nullable
      converter on the `NOT NULL` name would move "this arm never runs" from a fact about the
      property's type, which the compiler holds, to a fact about the schema. The **third is the blind
      index's**, and it is not a narrative converter at all: it moves a `ReadOnlyMemory<byte>` to and
      from a byte array and knows nothing about `NarrativeField`, because an index is a keyed digest
      rather than an envelope. So the nullability argument is about the **pair** and says nothing
      about the index's converter, and a sentence folding all three into "the narrative converters"
      is wrong on both tables that have this shape.
    What holds those
    comparers is this table's own change-tracking class and nothing product-wide — **three of the six
    sealed tables have such a class and three do not**, and the snapshot arm is held by review on all
    six; see [Edge Cases](#edge-cases--known-gotchas). The table carries **five** `CHECK` constraints over its
    sealed and keyed columns — four narrative, one over the
    blind index — each rendered from the constant that owns its number rather than from a literal:
    `CK_category_groups_name_length` and
    `CK_category_groups_description_length` bound their envelopes between
    `CiphertextEnvelope.MinimumLength` and the **two different caps**
    `NarrativeFieldLimits.NameBytes` and `NarrativeFieldLimits.DescriptionBytes`;
    `CK_category_groups_name_version` and `CK_category_groups_description_version` require the leading
    version byte through `substring`; and `CK_category_groups_name_key_length` is an **equality** on
    32 bytes rather than a band, because `HMAC-SHA-256` has one output width and a ceiling would admit
    a short digest silently. The two `NOT NULL` name columns say a **row** cannot be half a name;
    `IndexedName.Of` says a **call** cannot be. Neither restates the other for error quality — one
    refuses a statement reaching the database, the other refuses a caller who meant to write both and
    wrote one.

- **A category's name reaches this server sealed, with its blind index beside it, and its
  description reaches it sealed with nothing beside it.** `categories.name` carries the **fourth and
  last** blind index in the product; it and `categories.description` are two of the **last three**
  narrative columns to be sealed, which landed in one slice with `transactions.description` and left
  no eighth waiting. They have no ordinal of their own between them, because nothing sealed one
  before the other.
  - **Why**: the reasons are the group's, one level down, and they are stronger rather than
    different. The name had to keep its uniqueness because one category name per budget is what
    makes a past transaction's classification mean one thing; the description gets **no** index
    because a description is never looked up, never unique and never a name, so one would publish a
    deterministic per-account fingerprint of somebody's free text with nothing on the other side
    asking for it.
  - **Enforced in**: the same four types the group uses — `NarrativeField`, `IndexedName`,
    `NarrativeField.SealedOrAbsent` and `ReadOnlyMemory<byte>` — and a `CategoryConfiguration` that
    is `CategoryGroupConfiguration` with the entity swapped, plus a `category_group_id` that stays a
    plain `Guid` because it is a foreign key the caller read back from this API rather than
    associated data for anything. It maps three `bytea` columns through **three** converters in the
    shape the group's bullet states as a rule — two narrative ones differing only in nullability,
    plus the blind index's, which is not a narrative converter at all. That rule is stated once, up
    there, and this table is the second instance of it rather than a second observation of it. The table carries **six** `CHECK` constraints —
    four narrative, one over the blind index, one over the position — each rendered from the
    constant that owns its number: `CK_categories_name_length` and
    `CK_categories_description_length` bound their envelopes between
    `CiphertextEnvelope.MinimumLength` and the **two different caps**
    `NarrativeFieldLimits.NameBytes` and `NarrativeFieldLimits.DescriptionBytes`;
    `CK_categories_name_version` and `CK_categories_description_version` require the leading
    version byte through `substring`; and `CK_categories_name_key_length` is an **equality** on 32
    bytes rather than a band. `UseCollation("case_insensitive")` and `HasMaxLength` left both
    columns **by force** — `bytea` is not collatable and a character count is not a thing either
    column has any more. With that departure, **`users.email` is the only column in the schema
    still carrying `case_insensitive`.**

- **Category Group names are unique per budget, enforced over the blind index.**
  `IX_category_groups_budget_id_name_key` is unique over `(budget_id, name_key)`.
  - **Why**: the rule did not change and the mechanism did not change — a unique B-tree index, scoped
    per budget, reported as `23505` under a name the repository matches. What changed is the
    **column**. Uniqueness over `name` would now enforce nothing at all: every seal draws a fresh
    nonce, so two rows holding one name hold different bytes. The blind index survives that, being
    deterministic under the account's index key, so equality of names comes back as equality of
    digests.
  - **Enforced in**: the unique index declared in `CategoryGroupConfiguration` and pinned there as
    `NameIndexName`, which `CategoryGroupRepository` matches `PostgresException.ConstraintName`
    against on both `AddAsync` and `UpdateAsync` to raise "Category group name must be unique."
    The C# constant is still called `NameIndexName` while its **value** ends in `_name_key`: the index
    is for finding the row a name is already taken by, and the schema follows EF's own convention
    rather than carrying a hand-pinned exception to it. `UseCollation("case_insensitive")` left the
    column **by force** — `bytea` is not a collatable type — and what it was doing did not disappear
    but **moved**: see the case-folding rule under
    [Business Rules](#business-rules--invariants). Both verbs answer **400** here, and a payee create
    answers 409 on the same shape of index; the difference is who chose the name, and the argument is
    in the [decision log](_decision-log.md).

- **Category names are unique per budget, across all Category Groups**, not merely inside one group.
  Two groups therefore cannot each hold a "Groceries", and "Fees" cannot sit under both "Banking"
  and "Investments". The case-insensitivity that used to be part of this sentence is now the
  client's — see the case-folding rule under [Business Rules](#business-rules--invariants).
  - **Why**: the group a Category sits in is an arrangement its owner is free to change, not part of
    the Category's identity. `PATCH /api/categories/{id}/placement` moves a Category between groups
    at will, writes nothing to the Transactions that reference it, and never consults the name —
    `PlaceCategoryHandler` resolves the destination group and `CategoryRepository.PlaceAsync`
    reindexes positions, and neither touches the name index. Scoping uniqueness to the group would
    tie the legality of a name to that arrangement: two "Fees" would be legal while they sat apart
    and become a collision the moment their owner tidied one into the other's group, so a
    rearrangement meant to cost nothing could be refused for a reason that has nothing to do with
    rearranging. Budget-wide uniqueness makes a Category name mean exactly one thing inside the
    budget however the hierarchy is rearranged, which is the only scope under which the name stays a
    stable answer to what a past Transaction was filed as.
  - **Enforced in**: **database-owned**, and the mechanism is now the group's.
    `IX_categories_budget_id_name_key` is unique over `(budget_id, name_key)`, and
    `IX_categories_budget_id_name` is **gone** rather than standing beside it — left in place it
    would be a unique constraint over ciphertext that refuses nothing and that nobody would ever see
    fire. The scope is stated once, in [budgets.md](budgets.md#constraints). `CategoryRepository`
    restates it only to turn the unique violation into "Category name must be unique.", which is
    error quality rather than enforcement, on **both** verbs.
    `CategoryIntegrationTests.CategoryNames_AreCaseInsensitivelyUniqueAcrossGroups` is **deleted
    with no replacement** — the third instance of that deletion in this product, after the two payee
    cases — because the collation left `categories.name` by force and there is no server behaviour
    left to assert. What survives is the collision itself, held by
    `RepositoryConstraintAttributionTests`; what does not survive on this side is the folding that
    made two spellings one name. See the case-folding rule under
    [Business Rules](#business-rules--invariants).

### MUST NOT

- **A Category Group must not be deleted while it contains Categories.**
  - **Why**: Deleting the heading out from under its Categories would either orphan them — which the
    hierarchy forbids — or silently take them and their transaction history with it. Making the user
    empty the group first keeps that decision explicit.
  - **Enforced in**: **database-owned, with the application supplying the sentence** — the same split
    as the Category rule below, and for the same reasons. `CategoryConfiguration` maps
    `(category_group_id, budget_id) → category_groups` on `Restrict`, so PostgreSQL refuses to remove
    a group any Category names, whatever wrote the delete — that is the half that is *correct*.
    `CategoryGroupRepository.DeleteAsync` catches that `23503` **by constraint name**
    (`CategoryConfiguration.CategoryGroupForeignKeyName`), detaches the rejected entity so its failed
    state cannot leak into a later save, and turns it into the sentence below;
    `DeleteCategoryGroupHandler` asks `HasCategoriesAsync` first to raise the same sentence before
    the write. Both are *error quality*. The precheck is check-then-act, so its answer can be stale
    in either direction by the time the delete runs, exactly as
    [transactions.md](transactions.md#edge-cases--known-gotchas) describes for the account and
    category guards. Per [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md)
    neither half is redundant cover for the other.
  - **Error**: “Category group cannot be deleted because it has categories.”

- **A Category must not be deleted while a Transaction references it.**
  - **Why**: The classification is part of what a recorded movement means; dropping it would rewrite
    history the user cannot reconstruct. Recategorizing first is a decision only the user can make.
  - **Enforced in**: **database-owned, with the application supplying the sentence.**
    `TransactionConfiguration` maps `(category_id, budget_id) → categories` on `Restrict`, so
    PostgreSQL refuses to remove a Category any transaction names, whatever wrote the delete — that
    is the half that is *correct*. `CategoryRepository.DeleteAsync` catches that `23503` **by
    constraint name** and turns it into the sentence below, and `DeleteCategoryHandler` asks
    `HasTransactionsAsync` first to raise the same sentence before the write; both are *error
    quality*. The precheck is check-then-act, so its answer can be stale in either direction by the
    time the delete runs — the split and both directions are described in
    [transactions.md](transactions.md#edge-cases--known-gotchas). Per
    [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) neither half is
    redundant cover for the other: do not drop the constraint because the precheck passes first, and
    do not drop the catch because the precheck usually gets there first.
  - **Error**: “Category cannot be deleted because it has transactions.”
  - **Erasure does not bypass this, and does not delete categories at all.** It empties the budget's
    transactions and then deletes the *user*; the categories leave by the cascade descending from
    that row, with nothing left referencing them — see [erasure.md](erasure.md).

- **The server MUST NOT be given a rule about either entity's name or description text** — not a
  minimum length, not a blankness check, not a trim, not a character cap, and not a case-folding
  rule. **The prohibition reaches `Category` now as well as `CategoryGroup`**, which is the sentence
  this bullet used to carve an exception out of.
  - **Why**: every one of them is a question about plaintext this deployment has never seen. A reader
    who finds the gap in `CategoryGroup.ValidateOrThrow` or `Category.ValidateOrThrow` and restores a
    check can only restore it against the **envelope** — measuring bytes and calling them characters,
    or refusing a 29-byte envelope that is the correct sealing of an empty string. A reader who
    notices the collation is gone and reaches for a folding rule has nothing to fold. Both are wrong
    answers wearing the shape of the right one, and they are argued in full under
    [Business Rules](#business-rules--invariants), because these are capabilities that **moved**
    rather than rules quietly dropped.
  - **Enforced in**: the absence of any name or description rule in either validator.
    `CategoryGroup.ValidateOrThrow` now judges the identifier, the tenancy and the position and
    nothing else; `Category.ValidateOrThrow` judges the identifier, the tenancy, the destination
    group and the position and nothing else. Above both sit the byte bands under MUST — the only
    lengths anything on this side can measure.

- **`NormalizeDescription` MUST NOT return, in any form, on either entity.** It mapped a
  whitespace-only description onto `null`, and that fold is the one thing these two columns may
  never do again.
  - **Why**: an empty note seals to exactly `CiphertextEnvelope.MinimumLength` bytes — AES-GCM
    ciphertext is the length of its plaintext — while a note nobody wrote is `NULL`. Both are legal
    rows, the schema distinguishes them, and they mean different things: *cleared* and *never filled*.
    Folding them together destroys a distinction the storage layer is capable of keeping.
  - **Enforced in**: the parameter's type, which is what makes the removal forced rather than chosen —
    there is no `string` here to normalise on either entity. The wire edge holds the other half: all
    four handlers test `command.Description is null` and **never** `string.IsNullOrEmpty` or
    `string.IsNullOrWhiteSpace`, because the decoder underneath refuses `null` and `""` identically,
    so the distinction cannot live down there. Either forgiving spelling folds a malformed `""` into
    "absent" and writes `NULL` where a 400 was owed — a legal row, a 201 or a 204 on the wire, and the
    note the person typed silently gone.

## Business Rules & Invariants

- **Rule**: **The server can no longer refuse a blank or a runaway name on either entity, and it can
  no longer refuse an over-long description on either.** "A name is not just spaces", the
  200-character ceiling on the names and the 500-character ceiling on the descriptions are all the
  client's now, applied before it seals. **This rule used to have a Category-shaped exception and it
  no longer does**; a reader who remembers one is remembering the slice before last.
- **Why**: these are **capabilities that moved**, not rules that were quietly dropped, and the
  distinction is why they are written down rather than left as a gap in a validator. What arrives is
  an AEAD envelope over text this server has never seen and holds no key for; "is this nothing but
  spaces?" and "is it longer than a label?" are questions about plaintext. A reader who finds the
  absence and restores a check can only restore it against the **envelope** — measuring bytes and
  calling them characters, or refusing a 29-byte envelope that is the correct sealing of an empty
  string. Both are wrong answers wearing the shape of the right one.
- **Enforced in**: what replaced each half is a byte rule and nothing more. The trims and the
  blankness check are replaced by **nothing on this side**. The four character ceilings are replaced
  by `NarrativeFieldLimits.NameBytes` and `NarrativeFieldLimits.DescriptionBytes` — caps on **stored
  envelope bytes**, applied by `IndexedName.Of` and `NarrativeField.SealedOrAbsent` and restated as
  the upper bounds of `CK_category_groups_name_length`, `CK_category_groups_description_length`,
  `CK_categories_name_length` and `CK_categories_description_length`.
  **The two caps are field *classes* and not four guesses at two numbers**: a helper that sealed a
  description under the name's ceiling would refuse values that column is meant to accept, and one
  that sealed a name under the description's would widen a column nobody asked to widen.
  `CiphertextEnvelope.MinimumLength` is the floor of all four checks and is **not** the blank-name
  rule restored: an envelope over an empty string satisfies it exactly.
- **Example**: a client that seals `"   "` as a group name or a category name gets a `201`. The row
  is well-formed, the constraints are satisfied, and nothing in this deployment can tell that value
  from `"Essentials"` or from `"Groceries"`.
- **Counterexample**: adding a floor above the format's own to approximate "not blank". It refuses
  short real names, admits long blank ones, and is a rule about ciphertext claiming to be a rule about
  text.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **Case folding on both levels' names left this server and did not disappear.**
  "Essentials" and "essentials" are still one group, "Groceries" and "groceries" still one category —
  if, and only if, the client folds the text the same way before it computes the blind index.
- **Why**: the old guarantee was the `case_insensitive` collation, on `category_groups.name` and
  then on `categories.name`, and each departure was **forced rather than chosen** — `case_insensitive`
  is a text collation and `bytea` is not a collatable type. What replaced it is the normalization
  [account-keys.md](account-keys.md#the-normalization-a-name-is-indexed-through) defines — trim, NFKC,
  **full** case fold, UTF-8 — applied in the browser before the `HMAC`. The database still guarantees
  two identical index values cannot coexist; it no longer guarantees two spellings of one name produce
  identical index values, and that half is now the client's to get right.
- **Enforced in**: the client, and by nothing beneath it. Nothing on this side folds anything and
  nothing on this side needs to: the write stores whatever pair it was handed, and only a *different*
  index value can collide. **Two integration cases went with the mechanism, one per level, and
  neither has a replacement or a possible one** — the group's, and
  `CategoryNames_AreCaseInsensitivelyUniqueAcrossGroups`, which asserted the cross-group half by
  sending a second group a lower-cased spelling. There is nothing below the browser that could take
  either one's place, because the server sees a MAC and never a name; the payee case that asserted
  case-insensitive reuse left the same way and for the same reason. **There is no counterexample one
  table away any more**: the last text name column in this chapter became ciphertext with the
  category, so a reader looking for the table where PostgreSQL still folds will not find one here.
- **Counterexample**: a client that folds with the host's `toLowerCase` instead of the shipped fold
  table. It agrees with a correct client on almost every name a person types and disagrees on the
  handful where the difference decides a match, and a blind index **cannot be recomputed** afterwards
  — the plaintext behind it is encrypted.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **"Cleared" and "never filled" are two different rows, and they must stay two.** An empty
  note is an envelope of exactly `CiphertextEnvelope.MinimumLength` bytes; a note nobody wrote is
  `NULL`. **And a lost description is invisible where a lost name is `23502`.**
- **Why**: the second sentence is the hazard these columns add to the product, and there are two of
  them in this chapter now rather than one. **A nullable sealed column is not new — `budgets.name` is
  one, with the same converter shape and the same NULL-tolerant `CHECK`s — but it is reached by no
  route**, so `category_groups.description` was the first that had to decide what an absent member on
  the wire means, and `categories.description` is the second. Both are nullable, so a write path that
  decoded a description and then forgot to assign it writes `NULL`: a legal row, violating no
  constraint, byte-identical to one belonging to somebody who deliberately filed no note. On the
  `NOT NULL` name the same omission is refused by the database. **Nothing in the schema can tell a bug
  from an operation on these columns** — which is exactly why the two states have to be kept apart
  everywhere else, since the only evidence that a note survived a round trip is the note itself coming
  back.
- **Enforced in**: four layers keeping one distinction, and each could collapse it on its own.
  `CategoryGroup.Description` and `Category.Description` are **assigned and never normalised** —
  `NormalizeDescription` is deleted on both and cannot return, per [MUST NOT](#must-not) above. All
  four handlers test `command.Description is null` and nothing more forgiving.
  `CategoryGroupDto.Description` and `CategoryDto.Description` stay `string?` and must never gain a
  `?? string.Empty`, because the member is an envelope, `""` is not a legal one, and a client cannot
  tell the coercion from a value it is expected to decode. **The one DTO in the product that used to
  coerce a null description no longer does**: `TransactionDto.Description` became `string?` with the
  coercion deleted when `transactions.description` was sealed, so a reader will not find the
  exception this paragraph once pointed at. And **all four wire shapes refuse a member they do not
  declare**, which is the one exposure the three above cannot reach, because it arrives on a path
  named by no member of any of them — the rule below. What holds the *invisible* half is neither a
  constraint nor a type but a **test shape**: every write path is covered by a case that reads a
  **non-null** description back, never one asserting the member is merely present or that the response
  was a 204.
- **Example**: a person clears the note on a group, or on a category. The row keeps a 29-byte
  envelope, which is what they wrote — nothing. A person who never filed one keeps `NULL`. Both
  render as no note and the rows are not the same row.
- **Counterexample**: a `PUT` handler that decodes the description into a local and then calls
  `Update` with the name alone. It compiles, it answers 204, every constraint is satisfied, and the
  note is gone — on either entity, since the two update paths have the same shape.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **All four write shapes in this chapter refuse a member they do not declare**, and answer
  **400**. `CreateCategoryGroupCommand`, `CategoryGroupEndpoints.UpdateCategoryGroupRequest`,
  `CreateCategoryCommand` and `CategoryEndpoints.UpdateCategoryRequest` carry
  `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]`; the API's shared JSON options
  carry no such setting, and no sibling shape gets the attribute for company.
- **Why**: each of the four binds a **nullable narrative column**, where an unmapped member is
  indistinguishable from an absent one. Measured under `JsonSerializerDefaults.Web` with the options
  `Api/Program.cs` registers — whose `UnmappedMemberHandling` is the default `Skip` — a body sending
  `descriptionn` and a body sending no description at all both leave `Description` null. On a
  `POST` that is a **201 carrying a note that never arrived**; on a `PUT`, which replaces, it is a
  **204 and the note the row held is gone**. The second is the destructive half and the harder one
  to see: clearing a note is a real operation these routes perform, so no status, no constraint and no
  later read separates "the caller asked" from "the caller typed the member wrong". It is the same
  hazard the null-versus-`""` care above exists to close, reached by a path outside every member
  any of the four shapes names.
  - **This is not the transaction shapes' argument, and the two must not be folded together.** There
    the attribute answers wire **drift**: `CreateTransactionCommand` and
    `TransactionEndpoints.UpdateTransactionRequest` retired `payeeName`, a member a released client
    still sends, and refusing it visibly beat dropping it in silence. **Sealing
    `transactions.description` did not give that pair a second reason**, which is the tidy-looking
    inference to refuse: the description there rides an `Optional<string?>`, and under `Skip` a
    misspelled member binds identically to an *absent* one — which on a `PATCH` means *leave the note
    alone*. That silently drops an **edit**, a real defect, but not the data loss this argument is
    about, and `Optional<T>` is exactly what makes the difference. Nothing here is retired — every
    member is new — so the two reasons stay one apiece, and a shape that binds no nullable narrative
    member earns nothing from either.
  - **What it costs**: any client sending an unknown member to these four routes now gets a 400,
    dressed as `application/problem+json` by this product's pipeline and naming no field; the
    unmappable member reaches the server log and not the response. Nothing sends to any of them
    today — the API service behind each half of `/app/categories` still posts a plaintext name, so
    **neither half of that screen can write**.
- **Enforced in**: the attribute on those four declarations, and **per type is the whole discipline**.
  One integration case holds each shape — the two `POST` shapes are checked to refuse and write
  nothing, the two `PUT` shapes to refuse and leave the stored note standing. The two request records
  are owed cases of their own, because an attribute on a record in `Application` says nothing
  about a record declared in `Api`, and every one of the four misspells the member whose loss is
  **invisible**, never a required one, which `System.Text.Json` would refuse on its own. Beside them
  sit **two** negative controls, one per route group:
  `PatchCategoryGroupPosition_WithAnUnknownMember_StillIgnoresIt` over `MoveCategoryGroupRequest`,
  and its twin over the category placement request, both declared in their own file on their own
  route group and deliberately
  bare. Without them a global `UnmappedMemberHandling` in `Api/Program.cs` satisfies every annotated
  neighbour while silently changing the contract of every route in the product. The second control
  is not a duplicate of the first: it is the per-type control for a *different file's* pair, and an
  attribute pasted onto the placement shape would delete the only thing that reddens.
- **Example**: a `PUT` that renames a group or a category and misspells `description` is refused, and
  both the name and the note are exactly as they were. A `PATCH` to either ordering route carrying an
  extra member is still a 204, and the placement it did declare takes effect.
- **Counterexample**: moving the setting into `Api/Program.cs` to "apply it everywhere". Whether a
  shape refuses what it was not asked for is a contract decision each shape makes for itself, and the
  shapes that bind no nullable narrative member have not made it.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **A name and its blind index move together or not at all**, on both entities, and the
  `UPDATE` grant is the place that rule is hardest to test.
- **Why**: such a column is a pair — the ciphertext nobody here can read, and the keyed digest that is
  the only way a row holding a given name can be found or refused as a duplicate. A row carrying new
  ciphertext under the previous name's index satisfies every constraint, reads back fine, and leaves
  the unique index guarding a name the row no longer holds while the name it *does* hold stays free
  for a second row to take. Nothing on this side can notice — recomputing a digest needs the
  account's index key, which lives in a browser.
- **Enforced in**: three places holding three different moments, exactly as on
  [accounts](accounts.md#business-rules--invariants) and
  [payees](payees.md#business-rules--invariants). The four factories and mutators —
  `CategoryGroup.Create`/`Update` and `Category.Create`/`Update` — take an `IndexedName` and offer no
  spelling for half a name, so a **call** cannot be half; each table's two `NOT NULL` columns mean a
  **row** cannot be; and the role's grants are
  `UPDATE (name, name_key, description, position)` on `category_groups` and
  `UPDATE (name, name_key, description, position, category_group_id)` on `categories`, so the
  **statement** is permitted whole.
  - **The `categories` grant shipped without `name_key`, and the suite was green the whole time.**
    That is the live defect this rule was found by on this table, and it is worth the space because
    the way it hid is not the way anybody predicts. Measured on `postgres:17.10` against the shipped
    list: a genuine rename — `set name = …, name_key = …` — answers **`42501`**, and PostgreSQL
    **names only the relation**. There is no column in the message, no constraint, nothing pointing
    at which entry of the grant is missing; a reader meeting that error has to already suspect a
    column list. Three controls succeeded in the same run, which is what says the refusal is the
    column's and not the role's: a two-column rename on `category_groups`, a `category_group_id`
    move on `categories`, and a `position` write on `categories`.
  - **What kept it quiet is the commonest edit, and it is a property of the *content comparer*
    rather than of the grant.** The update route takes the name and the index together, so a client
    editing only the **description** re-sends the name it already has. Every seal draws a fresh
    nonce, so the `name` bytes change while the `name_key` bytes are byte-identical;
    `BlindIndexContentComparer` compares content, correctly reports the index unchanged, and EF
    emits a **one-column** `UPDATE` that the broken grant permits. Measured: **no genuine category
    rename reached the database at all** until the route bodies were corrected, and nothing went
    red.
  - **So loudness is decided by *which column is missing*, not by which table it is missing on** —
    and an older sentence in this repository split it by table, which produced two opposite wrong
    conclusions. Missing `name` or `name_key` is **loud** on the first genuine rename anybody
    exercises, on all four sealed tables alike, because a rename always names both. Every **other**
    column on a grant list is **quiet**, because EF names only the columns that changed and a rename
    that leaves one alone never mentions it. Which columns those are is a fact about each table
    rather than a shared set: on `categories` they are `description`, `position` and
    `category_group_id`, on `category_groups` `description` and `position`, on `accounts` `type` and
    `opening_balance`, and on `payees` there are none, its list being the name pair and nothing else.
    The reading that `category_groups` is the hard table and `accounts` the easy one was the wrong
    axis: what differs between those tables is how many *quiet* columns their lists carry, not
    whether the name pair is loud — and `payees`, with none, is what shows that the axis was never
    the table.
  - **Which is why the raw statement is the control and the route case is the second one worth
    having.** The tenancy schema case writes **all five** granted `categories` columns in one
    statement on the **app role's own connection** — a superuser skips every privilege check, so an
    admin connection would pass with no grants in place at all — because a control naming fewer
    columns cannot tell a five-column grant from a four-column one. Both of this chapter's tables had
    only ever exercised that statement on the *admin* connection before, their composite foreign keys
    refusing a cross-budget move without any grant being consulted. Beside it, a route case
    performing a **genuine rename** is what catches the loud half through the product's own path; a
    case that changes only the description exercises neither.
  - **And two different cases guard two different defects, which must not be collapsed into one
    sentence.** A route case that changes a **non-empty description** is what catches a grant
    missing `description` — the `PUT` then emits that column and the statement is refused, whether
    or not anything reads the value afterwards. A route case that **reads that description back** is
    what catches a handler silently dropping it. Drop the read-back and the grant is still guarded;
    drop the read-back *and* let the handler drop the description, and the case passes.
- **Counterexample**: a later `Update(NarrativeField name, …)` overload added for a screen that "only
  changes the name". It compiles, it stores, it reads back, no constraint fires, and both failures
  above follow silently.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **Both entities' identifiers are supplied by the client and cross as text**, in the
  lower-case 36-character hyphenated spelling and nothing else. The exception this rule used to carry
  for `Category` is gone.
- **Why**: the identifier is the associated data **both** narrative members were sealed against, and
  associated data is rebuilt from where a ciphertext was found rather than carried inside it — so this
  API has to hand back the same spelling it was sent, and therefore has to refuse the spellings it
  cannot reproduce. Bound as a `Guid`, `System.Text.Json` folds the braced, upper-case and canonical
  forms to one value before any handler sees text, and the refusal becomes **unwritable**: it
  compiles, every test sending a canonical id passes, and it fails in a browser months later. The
  blast radius on both of these tables is one member wider than on accounts or payees — a spelling
  this API cannot reproduce costs a name **and** a note together, with every constraint satisfied and
  nothing red.
- **Enforced in**: `CreateCategoryGroupCommand.Id` and `CreateCategoryCommand.Id` are `string`s,
  judged by `CanonicalIdentifier.TryParse` in their handlers — first of the four opaque members on
  the group and of the four on the category, because a spelling this API cannot reproduce makes the
  envelopes beside it irrelevant whatever they look like. All four are attempted and every failure is
  reported, since one piece of client code produces all four; the **description** is the member most
  at risk of losing that property, because it alone sits behind a branch, and judged inside an early
  return or below the throw it would never be reported alongside the others. Both factories take the
  id as a parameter and refuse `Guid.Empty`, reachable for the first time now that the value arrives
  from outside, and refused there rather than left to the primary key, which accepts all-zero as a
  legal uuid and would answer the *second* such row with the identifier conflict below — a sentence
  true of the row and wrong about the caller. `Guid.CreateVersion7()` has left both entity files
  entirely, with **no minting overload** behind either: a caller that forgot to thread an id through
  would otherwise compile, pass every case that does not assert the returned identifier, and write a
  row holding ciphertext nobody can open.
- **Counterexample**: the two update commands take a `Guid` and their route parameters stay
  `{id:guid}`, and that asymmetry is deliberate. On an update the client re-seals against the row's
  **existing** id, which it read back from this API in the one form a `Guid` renders; the text in the
  URL is never what anything was sealed under, so there is no spelling to preserve. The rule lives
  where an identifier is *chosen*.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A duplicate name on **either** entity answers **400 keyed on `Name`, on the create as well
  as on the update** — and a duplicate **identifier** on either create answers **409 with a sentence
  of its own**.
- **Why**: **what differs between these two tables and payees is who chose the name.** A group and a
  category are both named by a person, in a form, so a collision is a mistake about a field they are
  looking at: the answer names the field, the message attaches to the input, and retyping resolves it.
  There is nothing to re-read — the row already holding the name is not the row they were opening. A
  payee create answers 409 on the identical shape of index because a payee's name was **resolved** by
  the client against a list it decrypted, so a collision says that list was stale rather than that
  anybody chose badly. One rule with two inputs, the verb and the author of the name; the full
  argument is in the [decision log](_decision-log.md), and the two must not be aligned. **Sealing
  `categories.name` moved nothing about which status it earns**, which is the same finding the group
  produced one slice earlier: ciphertext changes what the *server* can see about a name and nothing
  about where the name came from.
  The **identifier** conflict is a third answer because it is not about a name at all: the id is the
  client's, so a POST retried after a network timeout carries a byte-identical body, and the row
  already wearing that id may hold a different name — or sit in a budget the caller cannot read — so
  sending them off to re-read their list would send them looking for something that is not on it.
- **Enforced in**: **database-owned for the refusal, application-owned for the sentence.**
  `CategoryGroupRepository.AddAsync` and `CategoryRepository.AddAsync` each carry two `catch` arms
  over the **same** `23505` from the same statement, matched by **constraint name** — the
  configuration's `PrimaryKeyName` and its name index — so SQLSTATE alone cannot tell the two apart
  and whichever sentence was written first would be given to both. The key's arm is written first
  because that is the one PostgreSQL reports when a row breaks both, decided by **OID** (creation
  order) and measured for payees rather than re-run here; the two filters are mutually exclusive, so
  the order documents the measurement and changes no behaviour. `AK_category_groups_id_budget_id` and
  `AK_categories_id_budget_id` are therefore unreachable as reported names and nothing matches
  either. Both `UpdateAsync` methods match the name index alone and raise the same 400.
  - **The category arms had no coverage at any level until mutation testing went looking**, and that
    is worth recording rather than leaving as a green suite. Retargeting either arm's `when` clause
    at a constraint the statement can never raise — which leaves it unreachable while still
    compiling and still reading as ordinary code — reddened **only** the two new attribution cases,
    `AddCategory_WithADuplicateCategoryName_TranslatesItsOwnUniqueIndex` and
    `UpdateCategory_RenamedOntoATakenName_TranslatesItsOwnUniqueIndex`, because no endpoint case
    exercised a duplicate category name on either verb. The same mutation on the account and payee
    rename arms reddened an endpoint case beside the new one: there the **status** was already pinned
    at the wire and what was missing was the **attribution** — the exception type and the `Name` key,
    which is *which rule the caller broke* and is not something a status says.
  - **An unreachable arm degrades to a 500, not to the neighbouring status.** Measured at the wire on
    the payee rename: nothing in the handler chain maps `DbUpdateException`, so `GlobalExceptionHandler`
    writes a 500. Two things follow. A create's 409 and a rename's 400 cannot silently swap places by
    accident, so any "consistency" between them can only ever arrive as a deliberate edit; and a
    reviewer who deletes an arm gets a loud failure rather than a plausible-looking wrong status.
- **Example**: a person creating a second "Essentials", or a second "Groceries" under a different
  heading, gets a 400 with the error on `Name`. A browser whose create response was lost sends the
  same body again and gets a 409 telling it to read the row back by its identifier, or to mint a
  fresh one.
- **Counterexample**: answering **200 with the row that already exists**, the tidy-looking idempotent
  create. It means deciding whether the existing row is the same group — a comparison over AEAD
  envelopes this server cannot open — and it would still have to choose an answer for the case where
  the id matches and the name does not.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: `PUT /api/category-groups/{id}` and `PUT /api/categories/{id}` are **full replacements**,
  so an absent or null `description` **clears** the note the row held. There is no third state and no
  `Optional<T>` on either path.
- **Why**: a `PUT` has no "leave it alone" reading to express. The transaction routes carry
  `Optional<T>` because they are `PATCH` and genuinely have three states; inventing one here would
  invent a state these routes do not have and no client has ever sent. `""` is not that state either —
  it is a malformed envelope and a 400.
- **Enforced in**: each update request declares `Name`, `NameKey` and a nullable `Description`, and
  `CategoryGroup.Update` and `Category.Update` each assign all three columns or none, with the null
  refusal on the name **above** all three assignments — an implementation that assigned the
  description first and only then dereferenced the name would leave a row with a cleared note and its
  old name, which is a reachable state and the one the refusal cases read all three columns back to
  rule out. **Both update handlers decode above the lookup**, for two reasons a reader will simplify
  away: a malformed body against an unknown id should name the member the caller sent rather than
  send them looking for a row, and the entity the repository hands back is the **tracked** instance,
  so a handler that mutated it and then threw would leave a rename and a cleared note waiting for the
  next save on that context.
- **Example**: a `PUT` carrying only `name` and `nameKey` renames the row and removes its note. A
  `PUT` carrying `"description": null` does the same thing and says so.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: **A Category's group and its position are the two things about it this server can still
  read**, and `PATCH /api/categories/{id}/placement` is unchanged by the sealing.
- **Why**: `category_group_id` is a foreign key the caller read back from this API and `position` is
  an `int`, so neither is narrative and neither lost a capability. The move path never consulted the
  name and still does not — which is why the sealing took no ordering, no lookup and no validation
  away from it.
- **Enforced in**: `Category.Place` is untouched by this slice, `PlaceCategoryHandler` resolves the
  destination group through the budget-filtered repository, and `CategoryRepository.PlaceAsync`
  reindexes positions. The placement request shape deliberately carries **no**
  `[JsonUnmappedMemberHandling]` attribute, because it binds no nullable narrative member — it is the
  negative control named in the rule above.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: `Position` is a zero-based, non-negative integer, and positions are **contiguous** within
  their scope. The two scopes differ:
  - a budget's Category Groups occupy exactly `0..n-1`, **budget-wide**;
  - the Categories inside one Category Group occupy exactly `0..m-1`, **within that group**.
- **Why**: Order is a deliberate personal arrangement, so it is persisted rather than derived.
  Contiguity is what makes a position mean anything: it is the only thing that turns "position 3"
  into "the fourth item", which is what a client sends when someone drops a row into the fourth slot.
  With gaps or duplicates in the stored list, that number stops naming the slot they aimed at. The
  two scopes differ because groups are arranged against each other while categories are arranged
  inside their heading.
- **Enforced in**: **domain-owned**, in `CategoryOrdering` (`Place`, `CloseGap`) and
  `CategoryGroupOrdering` (`MoveToPosition`, `CloseGap`), which reindex the whole affected list on
  every insertion, move and removal; `CategoryRepository.PlaceAsync` / `DeleteAsync` and
  `CategoryGroupRepository.MoveToPositionAsync` / `DeleteAsync` load the siblings and delegate rather
  than reimplementing the algorithm. The database holds only the non-negative half —
  `CK_categories_position` and `CK_category_groups_position` (`position >= 0`) — plus the ordering
  indexes described in [budgets.md](budgets.md#constraints), which are **not** unique.
  `CategoryIntegrationTests.Ordering_StaysContiguousAndZeroBasedAcrossASequenceOfMovesPlacementsAndDeletes`
  asserts the whole invariant as a set over the **stored rows**, read with raw SQL rather than
  through `/api/categories`, because both read services tie-break on `Id` —
  `CategoryReadService.GetAllAsync` orders by `categoryGroup.Position, category.Position,
  category.Id` and `CategoryGroupReadService.GetAllAsync` by `Position` then `Id`. A duplicate
  position is therefore deterministic: it never surfaces as flakiness, only as an item sitting
  quietly in the wrong place.

  **Why contiguity is not at the bottom.** It sits above the layer that could hold *some* of it, and
  under [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) a rule left above
  its lowest capable layer has to say why. Checking contiguity on a write means comparing the row
  against every one of its siblings, and the only PostgreSQL construct that can do that is a deferred
  constraint trigger — procedural logic in the database, which is exactly the boundary ADR 0002 draws
  around "lowest capable layer". So the rule stays in the domain, where it is a loop over an ordered
  list the unit suite can read and test. Do not read the principle as licence to push this down: a
  trigger here would be the thing the ADR names as its one worked example of what not to do.

  A **`DEFERRABLE INITIALLY DEFERRED` unique constraint on `(category_group_id, position)`** is the
  near-miss worth naming, because it catches duplicates declaratively and *is* a constraint rather
  than a trigger — the declarative boundary is satisfied, so the boundary is not what rules it out.
  Three other things do. First, the consequence it would prevent is cosmetic: both read services
  tie-break on `Id`, so a duplicate produces a deterministic-but-wrong order that the next reindex of
  that list repairs on its own — nothing like the tenancy breach or the bulk loss of recorded money
  the rules that do live at the bottom prevent — and the bottom layer's price is paid on every later
  change. Second, EF has no `DEFERRABLE` support, so it would be hand-written SQL inside the single
  baseline migration this repository regenerates by convention; grants can live outside migrations
  because provisioning owns them, but schema has nowhere else to go. Third, it buys half the
  invariant at best: `0, 1, 5` holds no duplicate, is equally broken to the person reading the list,
  and would stay legal. The decision is recorded in [_decision-log.md](_decision-log.md) as
  "Ordering contiguity stays in the domain, and the near-miss constraint is named" (2026-07-29).
- **Example**: the first group a budget receives is position `0`, and so is the first category in
  each group. Deleting the group at position `1` of four leaves the survivors at `0, 1, 2`, not
  `0, 2, 3`; moving a Category to another group closes the gap it left behind and renumbers the
  destination around where it landed, in one save.
- **Counterexample**: scoping Category positions per budget rather than per Category Group makes
  position `0` mean "first in this budget" instead of "first under this heading", so adding one
  category renumbers every other group's — the user's deliberate arrangement is destroyed by an
  edit they made somewhere else. Equally wrong, and far quieter: removing an item and leaving the
  survivors alone. Nothing rejects `0, 2, 3` — the check constraint reads each number in isolation
  and the ordering index is not unique — so the list still renders in the right order, and the defect
  surfaces only later, when the next drag lands a row one slot away from where it was dropped.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Empty Category Groups are valid, and a new budget receives no default group.
- **Why**: A starter hierarchy would be someone else's idea of how this person's money is organized,
  and deleting the parts that do not fit is more work than creating the parts that do.
- **Enforced in**: `Budget.CreateDefault` creates the budget only; no handler seeds groups.
- **Example**: a brand-new user's `/categories` screen is empty until they add a group.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: There is no nesting below Category Group → Category, and no many-to-many membership.
- **Why**: Two levels are enough to produce a readable picker; deeper trees make the choice at entry
  time slower, which is the moment the model most needs to stay fast.
- **Enforced in**: `Category` has a single required `CategoryGroupId`; `CategoryGroup` has no parent.
- **Example**: "Essential Obligations → Groceries" is expressible; "Essential Obligations → Food →
  Groceries" is not.
- **Source**: `[SOURCE: discussion]`

## Workflows & State Transitions

Neither entity has lifecycle states — both have a create, read, edit and guarded-delete lifecycle
with nothing to transition between. On both, that edit is a `PUT` — `/api/category-groups/{id}` and
`/api/categories/{id}` — a **full replacement** of the name, its index and the note in one statement,
so "rename" is the common case rather than the shape of the operation. The stateful behaviour is
**ordering**, which is rewritten as a set on every move:

- New Category Groups append to the budget's group order; new Categories append within their selected
  Category Group.
- `PATCH /api/category-groups/{id}/position` moves one group and reindexes all groups contiguously.
- `PATCH /api/categories/{id}/placement` reorders a Category within its current group or moves it to
  another group, reindexing both source and destination contiguously in one database save.
- Position-bounds validation runs in the same two ordering services that own contiguity; a position
  below zero or past the end of the destination siblings is a validation error, not a clamp. Where
  the invariant lives and why is in Business Rules & Invariants above.
- Reads use persisted position, with ID only as a deterministic tie-breaker for unexpected duplicate
  positions. They are not alphabetically resorted — on **either** level they now *cannot* be, because
  the first differing byte of a sealed name after the version is the nonce.

## Decision Trees

Creating a Category Group (`CreateCategoryGroupHandler`, `POST /api/category-groups`):

```
IF the body carries a member this shape does not declare ← body binding, above the handler: the
  THEN 400 naming no field                                 shape carries Disallow, so the tree
                                                           below is never entered
judge all four opaque members and collect every failure  ← one piece of client code produced all
  IF Id is not the lower-case 36-character hyphenated      four, so a caller that got two wrong must
     uuid, or is the all-zero one                          not learn about the second only after
    THEN an error keyed on Id                              fixing the first
  IF Name is not base64url decoding to a v1 envelope
     within NarrativeFieldLimits.NameBytes
    THEN an error keyed on Name
  IF NameKey is not base64url decoding to exactly
     IndexedName.BlindIndexLength bytes
    THEN an error keyed on NameKey
  IF Description is present and is not base64url         ← `is null` and never IsNullOrEmpty: an
     decoding to a v1 envelope within                      absent member is a group with no note,
     NarrativeFieldLimits.DescriptionBytes                 "" is a malformed one
    THEN an error keyed on Description
IF any error was collected
  THEN 400 naming every member that failed
ELSE
  position = the next free slot in the budget            ← the one member the server still computes,
  CategoryGroup.Create(id, ambient budget,                 because position is the one value it can
                       IndexedName.Of(envelope, index),    still read
                       SealedOrAbsent(description),
                       position, now)
  try to insert
  IF PK_category_groups refused it                       ← checked first because it is the one
    THEN 409 "A category group already exists with this    PostgreSQL reports when a row breaks
              identifier. If this request is a retry,      both; the two catches are mutually
              read that group back by its identifier       exclusive either way
              instead of posting it again; otherwise
              mint a fresh identifier and post again."
  ELSE IF IX_category_groups_budget_id_name_key
          refused it
    THEN 400 "Category group name must be unique."       ← a person typed this name, so the remedy
                                                           is a field they can correct — the payee
  ELSE                                                     create answers 409 for the opposite reason
    THEN 201 with the group, and a Location naming
         GET /api/category-groups/{id}
```

Creating a Category (`CreateCategoryHandler`, `POST /api/categories`) is the **same tree** with three
substitutions and one extra step, so it is written as the difference rather than copied:

```
… the four opaque members, the collection of every failure, and both conflict arms are identical,
  reading `categories`, `PK_categories` and IX_categories_budget_id_name_key for their
  category_groups counterparts, and "Category name must be unique." for the group's sentence

the extra step, before the insert:
  IF CategoryGroupId does not resolve in the ambient       ← a Guid and not a string: it is a foreign
     budget                                                  key the caller read back from this API,
    THEN validation error "Category group was not found."    not associated data for anything, so no
                                                             spelling has to be preserved
  position = the next free slot IN THAT GROUP              ← the group's is budget-wide; a category's
                                                             is scoped to its heading
```

Placing a Category (`PlaceCategoryHandler`, `PATCH /api/categories/{id}/placement`):

```
IF the category id does not resolve in the ambient budget
  THEN 404 "Category was not found."
ELSE IF the destination group id does not resolve in the ambient budget
  THEN validation error "Category group was not found."   ← also the cross-budget answer
ELSE IF the requested position is negative or past the end of the destination siblings
  THEN validation error
ELSE IF the destination group is the category's current group
  THEN reorder within that group and reindex it contiguously
ELSE                                                      ← mutually exclusive with the above
  THEN reindex the source group to close the gap, insert into the destination,
       and reindex it too — one database save
```

## Integration Points

- **[Transactions](transactions.md)**: a transaction accepts an optional `CategoryId` and derives the
  Category Group from it rather than storing one.
- **[Budgets](budgets.md)**: both entities are stamped with and filtered by `BudgetId`, and a
  composite foreign key keeps a Category and its Category Group in the same budget at the schema
  level rather than only in the query filter. The rule and its reasoning are in
  [budgets.md](budgets.md#constraints).
- **[Ciphertext Envelope](ciphertext-envelope.md)**: **four of the eight narrative columns belong to
  this chapter**, which is more than any other file owns. `category_groups.name` was the fourth
  column in the product to store an envelope and `category_groups.name_key` the third blind index;
  `category_groups.description` was the fifth column and the **first from the description field
  class**; `categories.name` carries the fourth and last blind index, and `categories.description`
  the second of the three descriptions. The framing, the two byte caps, the value type a narrative
  column accepts, the wire step for the index and the grammar each envelope is bound to all live
  there. This file owns what these four values *mean* and what the schema no longer refuses about
  them.
- **[Account Keys](account-keys.md)**: the content key all four envelopes are sealed under, the index
  key the two blind indexes are computed under, and the normalization they are taken over. One index
  key per **account** — two would produce two index values for one name, and the uniqueness rules
  would stop colliding while appearing to work.
- **Angular client**: `/app/categories` manages both levels with drag-and-drop, and **neither half of
  it can write any more.** `category-groups-api.service.ts` and the category half's own API service
  both
  still declare `name` as plain text, send no `id` and no `nameKey`, and render the response's `name`
  straight into a list where it is now base64url — so a create on either level is refused on three
  members at once (`id`, `name`, `nameKey`) and a rename on two. **The half that used to work stopped
  working with this slice**, which is the sentence a reader who remembers otherwise needs: the
  category half survived the group's sealing only because `categories.name` was still text, and it is
  not. That is a gap, named here rather than described as though it worked. Transaction entry groups
  Category options under Category Group headings, and **both the options and the headings are
  envelopes now**.

## Edge Cases & Known Gotchas

- **Names are live data, not snapshots.** Renaming a Category or Category Group, or moving a Category
  to another group, immediately changes how every historical Transaction displays, because the
  hierarchy is joined at read time. This is the intended behaviour, not a defect: the user reorganized
  their own labels, and past rows should follow. Nothing is written to the transactions themselves.
  **On both names that is now a property of the envelope as well as of the join**: a snapshot copied
  onto each transaction would be sealed against the **transaction's** row id, so nothing could ever
  compare two of them or tell that one had gone stale.

- **Nothing on either level was ever ordered by name, and that is why this chapter's two slices moved
  no ordering.** The category-group read service orders by `Position` then `Id`, the category read
  service by `categoryGroup.Position, category.Position, category.Id`, and the export read service by
  `CreatedAtUtc` then `Id`. The accounts and payees slices each had to move an ordering that had
  silently become an ordering by the nonce; neither of these had one to move. `Position` is an `int`
  this server can still read, so sealing took no capability away from it — and a reader looking for
  the missing ordering fix should stop here rather than conclude it was forgotten. What a name
  ordering *would* now be is worth stating so nobody adds one: the first differing byte after the
  version is the nonce, freshly drawn on every seal, so the list would be stable within one read and
  reshuffled by every save.

- **Both tables carry six `CHECK` constraints, and which one reports a violation is decided by the
  constraint *name*, alphabetically — an ordering that crosses two columns on each.** Measured on
  `postgres:17.10` over exactly those six, on `category_groups` and again on the new `categories`
  shape, and the two orderings are **identical**: `description_length`, `description_version`,
  `name_key_length`, `name_length`, `name_version`, `position`. So a row breaking a **name** rule and
  a **description** rule is reported under the description, and a row breaking a description rule and
  the position rule is reported under the description too. **The `categories` measurement proves the
  rule is the alphabet rather than creation order**, which the `category_groups` one could not: a row
  breaking `name_key_length` and `name_length` together is reported under `name_key_length`, and that
  constraint was created *after* its neighbour. Nothing in either schema depends on the ordering —
  every predicate here is **total** over every value its column can hold, the two version checks
  through `substring` and the three length checks through `length`, so none of them can raise and
  every ordering yields `23514` naming *some* constraint. What it does forbid is a test asserting a
  constraint **name** for a row carrying more than one violation: a zero-length-name case must leave
  the description `NULL`, or it reports the description's constraint, and a `name_key` case needs a
  legal name *and* a NULL description, since two constraints sort ahead of `name_key_length`.

- **The four version checks *fire*; what nothing here can observe is the difference between two ways
  of spelling them, and the reason is the alphabet above.** Take the two halves apart, because the
  loose reading — "the version checks are uncovered" — is false and is the one that spreads. A value
  of legal length whose leading byte is not the version is refused on all four and reported under
  that check's **own** constraint name, so those cases are worth writing and they bite. What no value
  reaches is the disagreement between `substring` and `get_byte`: `get_byte` reads better and
  *raises* `2202E` on a zero-length `bytea` instead of answering false — no constraint name, no
  failing row, nothing a `catch` filtering on `23514` will ever see — and that is the **only** input
  the two spellings answer differently on. The instinct that a nullable column escapes it is wrong:
  measured, `get_byte(NULL::bytea, 0)` answers NULL and does not raise, so the difference lives
  exclusively at a **present, zero-length** value. And `description_length` sorts before
  `description_version`, while `name_key_length` and `name_length` both sort before `name_version`,
  so a length band reaches that value first on every column. Measured on `postgres:17.10` over both
  six-constraint shapes with the version checks spelled `get_byte`: nothing produced `2202E`, and
  every refusal came back `23514` under a length constraint. **So the length band is what earns the
  `23514` on that one value, and the spelling is not** — `substring` is right because it is *total*
  over every length the column can hold, which is a property no ordering can take away.
  - **The spelling itself is not uncaught.** `SchemaConstraintSnapshotTests` pins each constraint's
    rendered definition, so swapping the predicate moves text it holds and the suite goes red by
    name. What that cannot supply is the *reason*: a literal that moved reads as a paste, and the
    obvious repair is to update the expectation, which is exactly the edit the argument at the
    element exists to refuse. Observing the **behaviour** is the part needing a container probe
    against a table carrying that version check alone — and **a model-only edit cannot even be
    attempted from inside the suite**, because changing a `CHECK` in a configuration desynchronises
    the frozen migration baseline and `PendingModelChangesWarning` kills the run before the database
    is ever handed the new constraint. The whole argument, and the rule for whoever writes the next
    such constraint, is in
    [ciphertext-envelope.md](ciphertext-envelope.md#two-checks-on-one-column-and-which-one-bites).

- **A duplicate-name refusal is unreadable by anybody holding the database, on either level.** The
  `23505` still arrives and still becomes a sentence, but *which* two rows collided is a question only
  a browser holding the account's index key can answer. The same is true of support: there is no query
  anyone can run to find "the group called Essentials", and — this is what changed — **no query to
  find the category called Groceries either**. `categories` was the counterexample one table away
  while its name was text; it is not one now, and this chapter no longer contains a name anybody can
  `WHERE` on.

- **A blind index of the right width and the wrong value is accepted, on both tables.** This side
  holds no index key, so it can never say a value is the index *of* the name beside it. A
  correct-width value computed over the wrong text, under the wrong key, or straight out of a random
  number generator is stable, never collides, keys perfectly, and stands for a name the row does not
  hold for the life of the account. The width is the whole of the defence, refused in two places for
  two arrivals: `IndexedName.Of` refuses a **call**, and `CK_category_groups_name_key_length` or
  `CK_categories_name_key_length` refuses a **row** reaching the database by any other path.

- **Neither `CategoryGroupDto` nor `CategoryDto` carries a `nameKey`, and that absence is a
  decision.** A client recomputes the index from the name it just decrypted, under a key only it
  holds, and needs it solely to write. A member nobody reads would hand every caller a deterministic
  per-account fingerprint of every name in the hierarchy — enough to tell which two accounts label
  their spending the same way, with no key anywhere in the exchange. Anything that sorts, searches or
  groups either DTO's `name` client-side is sorting ciphertext. **The category DTO's
  `categoryGroupName` is a second envelope on one record**, bound to the *group's* row id rather than
  the category's, so a client rebuilding the binding from the wrong identifier gets an authentication
  failure with nothing naming the cause — which is why `categoryGroupId` travels beside it.

- **The move, place and delete paths load rows as *tracked* entities over converted narrative
  columns.** `CategoryGroupRepository.MoveToPositionAsync` / `DeleteAsync` and
  `CategoryRepository.PlaceAsync` / `DeleteAsync` materialise every sibling and reindex through
  `SetPosition`, emitting `UPDATE … SET position` per row. `position` is granted on both tables, so
  nothing about the sealing changes them — named here so that their silence is not read as coverage
  of the converters.

- **This chapter owns two of the product's three change-tracking classes, and their snapshot arm is
  still held by review.** `CategoryGroupChangeTrackingTests` and `CategoryChangeTrackingTests`
  compose a context over a statement-recording interceptor and assert **which columns an `UPDATE`
  names**, so dropping either comparer on either table — the narrative one `name` and `description`
  share, or the blind index's — or falsifying the equality arm reddens them. Measured on the category
  class rather than reasoned: dropping the description's content comparer reddens two cases, dropping
  the index's reddens two others, and falsifying the envelope equality reddens **three** — one more
  than its own comments predicted, because a falsified equality also dirties a *restated* description
  and puts it into a statement the name-pair case asserts the columns of. Three things follow that a
  reader will otherwise take for a product-wide guarantee. **`accounts`, `payees` and `budgets` have
  no equivalent class**, so their comparer arms are held by review alone — and on `accounts` and
  `payees` a broken one is **quiet**: EF restates a column with the bytes the row already holds, both
  halves of the name sit inside that table's `UPDATE` grant, so nothing answers `42501` and the
  spurious statement commits exactly like a rename would. **The third class,
  `TransactionChangeTrackingTests`, is narrower than these two and not by oversight** — see
  [transactions.md](transactions.md#edge-cases--known-gotchas), where the reason is that
  `description` is the only converted property on that table. And **the snapshot arm is held by no
  test on any of the five, now measured rather than argued**: aliasing the copy instead of copying it
  killed nothing in either suite, on `categories` and on `transactions` alike. An aliased snapshot
  diverges from the tracked value only if an accepted envelope's bytes are overwritten **in place**,
  and `NarrativeField` gives nothing the means — the buffer is private, `Envelope` is a window onto a
  copy the factory made, and every write path replaces the whole field. The copy stays anyway,
  because that unobservability is a property of the type as it stands rather than a permanent one.
- Deleting an empty Category Group is allowed; deleting a non-empty one is not. Categories must be
  moved or deleted first.
- **A Category's deletability is read live, not marked on the row.** Deleting the last Transaction
  filed under a Category makes that Category deletable again, which is what turns “Category cannot be
  deleted because it has transactions.” into an instruction the user can follow rather than a dead
  end. The refusal is the foreign key's rather than the precheck's; [Constraints](#must-not) above
  states the split.
- Moving a Category does not touch Transactions, because a Transaction references the Category, not
  the Category Group.
- There are no spending limits or allocations, colors, icons, income/expense restrictions, reports, or
  nested Category Groups in this model.
- Positions in one budget are independent of every other budget's: reordering groups in one budget
  never renumbers another's, because the reindex only ever sees the ambient budget's rows.
