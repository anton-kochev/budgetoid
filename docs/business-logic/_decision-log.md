# Business Logic Decision Log

Chronological record of non-obvious business decisions. Newest entries go at the top; existing
entries are never edited. If a decision is reversed, add a new entry referencing the original.

Infrastructure/architecture decisions (auth, hosting, DB) live in `docs/decisions/` (ADRs), not
here — this log is for **business/domain** decisions only.

---

## 2026-09-03 — A sealed description carries no blind index, and "cleared" is not "never filled"

**Context:** `category_groups.description` is the first sealed **free-text** column in the product.
Every sealed column before it was a `name`, and every one a route could write was `NOT NULL`, carried
a blind index, and held a value some rule elsewhere compares. `budgets.name` is the near miss and not
the precedent: it is nullable and unindexed, with the same converter shape and the same NULL-tolerant
`CHECK`s, but **no route accepts one**, so nothing before now had to decide what an absent member on
the wire means. A description is none of those things, and two questions the earlier
slices never had to answer arrived together — whether it gets an index like its neighbours, and what
a nullable sealed column does with the difference between a value somebody removed and a value
nobody ever supplied. `CategoryGroup.NormalizeDescription` had been answering the second one for as
long as the column held text, by mapping a whitespace-only description onto `null`.

**Decision:** **the description is sealed and nothing else.** No `description_key`, not now and not
in a later slice. It is `NarrativeField?` against a nullable `bytea`, capped at
`NarrativeFieldLimits.DescriptionBytes` rather than `NameBytes`, with a length band and a version
check that pass vacuously on NULL. And **`NULL` and a 29-byte envelope are two different rows that
must stay two**: `NormalizeDescription` is deleted and may not return in any form.

**No index, because an index answers a question nothing asks here.** A blind index exists so that
equal names can be found equal — for a uniqueness constraint or an equality lookup. A description is
not looked up, is not unique and is not a name, so an index over one would buy nothing and would
publish a deterministic per-account fingerprint of somebody's free text, with the operator holding
every row. Read that beside `budgets.name`'s exclusion and keep the two apart: the budget's is a
uniqueness rule **surrendered** and this is a mechanism that was never wanted. `IndexedName.Of` says
the same thing from the other end — it names `NameBytes` itself and takes no ceiling parameter,
because every blind-indexed column in the product is a `name`.

**The fold had to go, and its removal is forced rather than chosen.** An empty plaintext seals to
exactly `CiphertextEnvelope.MinimumLength` bytes, and a note nobody wrote is `NULL`; the schema
represents both and distinguishes them, measured. `NormalizeDescription` collapsed the first onto the
second, and it cannot be rewritten to survive the change because there is no text on this side to
inspect — it left with the `string` parameter. Two layers above it could still collapse the
distinction and neither may: the handlers' absence test is `command.Description is null` and never
`string.IsNullOrEmpty` or `string.IsNullOrWhiteSpace`, because the decoder underneath refuses `null`
and `""` identically; and `CategoryGroupDto.Description` stays `string?` with no `?? string.Empty`,
because `""` is not a legal envelope and a client cannot tell a coercion from a value it is expected
to decode.

**The hazard this column adds to the product, which no schema rule can close.** The column is
nullable, so **a write path that decodes a description and then forgets to assign it writes `NULL`** —
a legal row, violating no constraint, byte-identical to one belonging to somebody who deliberately
filed no note. On the `NOT NULL` name the same omission is `23502`. Nothing in the schema can tell a
bug from an operation here, so what holds it is a **test shape**: every write path is covered by a
case that reads a *non-null* description back, never one asserting the member is merely present or
that the response was a 204. That obligation is written into
[categories.md](categories.md#business-rules--invariants) because it is the kind of rule a later
author weakens while tidying an assertion.

**Alternatives rejected, and they fail differently.** **Give the description an index for
consistency with the name beside it** — it is the tidy-looking symmetry and it hands out a
fingerprint of free text for a lookup nobody performs. **Keep a normalisation step that maps an
empty envelope to NULL** — unimplementable: this side cannot tell an envelope over `"   "` from an
envelope over a paragraph. **Reuse `NameBytes` as the cap so there is one number** — the two caps are
field *classes*, and a description sealed under the name's ceiling is refused at a size this column
is meant to accept. **Use `NarrativeField.Sealed` with a nullable parameter instead of
`SealedOrAbsent`** — `default(ReadOnlyMemory<byte>)` is a non-null, zero-length buffer, so an absent
description would be judged as an envelope and refused for a rule written about values that exist.
**Let the `PUT` carry `Optional<string?>` so an absent member means "leave it alone"** — that invents
a third state a full replacement does not have and no client has ever sent; the transaction routes
carry it because they are `PATCH`.

**Consequences.** `NarrativeFieldLimits.DescriptionBytes` and `NarrativeField.SealedOrAbsent` have
production callers for the first time, so the two caps are a pair rather than a constant and a spare
— and a swap between them is quiet in both directions. `CategoryGroupConfiguration` is the first
persistence configuration holding **two narrative converters of different nullability**, which a
reviewer will propose unifying and which is argued against in place: a nullable converter on the
`NOT NULL` name column moves "this arm never runs" from a fact about the property's type to a fact
about the schema. And the alphabetical `CHECK` ordering now crosses two columns, which produced the
one measurement that corrected an earlier reading: `description_length` sorts before
`description_version`, so the length band **shields** a `get_byte` spelling on the version check and
nothing in the suite can catch one — recorded in
[ciphertext-envelope.md](ciphertext-envelope.md#two-checks-on-one-column-and-which-one-bites) rather
than left as a rule somebody assumes a test holds.

**Affected areas:** [categories.md](categories.md),
[ciphertext-envelope.md](ciphertext-envelope.md), [export.md](export.md),
[_overview.md](_overview.md).

---

## 2026-09-03 — A duplicate category group name stays a field error, and sealing a column does not move it

**Context:** `category_groups.name` became ciphertext with a blind index beside it — the third
table to take that change — and `IX_category_groups_budget_id_name_key` now refuses a duplicate over
`name_key` instead of over a case-insensitively collated `name`. The entry below settled that an
account create answers **400** and a payee create answers **409** on the identical shape of index,
by asking who chose the name. This table was named in that entry's consequences as falling out of the
rule with no decision of its own, on the grounds that its names are typed into a form — and the same
sentence dismissed the column's plaintext type as irrelevant to the answer. That column is no longer
plaintext, so the dismissal is now load-bearing rather than incidental, and a reviewer looking at
three sealed tables with two different create answers will read it as drift.

**Decision:** **the group create keeps its 400 keyed on `Name`, and so does the rename.** Sealing a
name column moves nothing about which status a collision earns. `CategoryGroupRepository.AddAsync`
and `UpdateAsync` both match the `23505` **by constraint name** and raise
`Domain.Common.ValidationException` naming `Name`.

**Because the input to the rule is the author of the name, and sealing does not change who that is.**
A person opens a form and types "Essentials"; a collision is a mistake about a field they are looking
at, the message attaches to the input, and retyping resolves it. There is nothing to re-read — the
group already holding the name is not the group they were creating. A payee's name is **resolved** by
the client against a list it decrypted before it posts, so a payee collision says that list was stale
rather than that anybody chose badly, and a bare 409 is the only answer with somewhere to put "adopt
the row that already exists". Ciphertext changes what the *server* can see about the name; it changes
nothing about where the name came from.

**A third answer arrives with the client-minted identifier, and it is not this one.**
`CategoryGroupRepository.AddAsync` carries a second `catch` arm on
`CategoryGroupConfiguration.PrimaryKeyName` raising `ConflictException` → **409**, with the sentence
the account and payee repositories already carry: a POST retried after a network timeout sends a
byte-identical body, and the row wearing that id may hold a different name — or sit in a budget the
caller cannot read — so it must never be sent off to re-read a group list. Both arms catch the same
SQLSTATE from the same statement, so neither may match on SQLSTATE alone.

**Alternatives rejected, and they fail differently.** **409 on the group create, to match the payee**
— it reads as consistency between the two most recently sealed tables and it spends the field a form
needs, telling somebody who typed a name they already own to go re-read a list that will not help
them. **Deciding by whether the column is sealed** — the rule this entry exists to refuse: it would
have flipped this table's answer as a side effect of an encryption slice, which is a product change
with no product argument behind it, and it says nothing at all about `categories`, whose create must
keep answering 400 while its column is still text. **Deciding by verb — creates conflict, renames
validate** — rejected in the entry below and unchanged: applied evenly it turns the account create
into a 409 too.

**Consequences.** The four collision answers still read as one rule with two inputs, and the rule now
has a worked example of the input that does *not* matter: `category_groups` changed columns and kept
its status. [budgets.md](budgets.md#must) says so in the paragraph that lists all four, so the next
sealed table is not read as a reason to revisit its own answer. And a fifth named entity gets its
status by asking the same question it always did — who chose the name — rather than by asking whether
the server can read it.

**Affected areas:** [categories.md](categories.md), [budgets.md](budgets.md),
[payees.md](payees.md), [ciphertext-envelope.md](ciphertext-envelope.md).

---

## 2026-09-02 — A repeated create is a conflict about the identifier, and it gets a sentence of its own

**Context:** a `POST /api/payees` or `POST /api/accounts` carrying an identifier the table already
held answered **500**. Since the row identifier became the client's — it is the associated data the
name was sealed against, so nothing on this side may choose it — **a retry after a network timeout
is a byte-identical body**. That is the ordinary behaviour of an HTTP client, on two routes that
fire from a form and an autocomplete, and the one answer that would have told the client what to do
was the one it did not get: the caller could not tell a payee it had stored from one it had lost.
The *name* collision was settled a day earlier, in the entry below, and this one was dismissed in a
clause of it: not a duplicate name, therefore not to be sent off to re-read a list. Right about the
premise, and wrong about what follows from it — not being the name conflict is an argument for its
**own** answer, not for no answer.

**Decision:** **both add paths translate a `23505` on the primary key into a conflict, and its
sentence is not the duplicate-name one.** `PayeeRepository.AddAsync` and `AccountRepository.AddAsync`
each carry a second `catch` arm, matched by constraint name against
`PayeeConfiguration.PrimaryKeyName` / `AccountConfiguration.PrimaryKeyName`, raising
`Domain.Common.ConflictException` → **409**. The duplicate-*name* answers are untouched: still 409
on a payee create, still 400 keyed on `Name` on an account create, still 400 on both renames.

**Two clauses, because the server cannot tell the two readings apart and the client can.** The
sentence names a retry that already succeeded and an identifier reused by mistake, and gives each
its own next step — read that row back by its identifier, or mint a fresh one — because the only
party that knows which of the two happened is the party that knows whether it sent this body
before. The shared conflict handler writes one title for every 409 in the product and adds no
extension member, so the sentence is the whole of what distinguishes these two conflicts, and the
account's is worded alongside the payee's on purpose: the caller's situation is identical on both
routes, so the two are read and changed together.

**"Read it back" is an instruction and not a promise, and that is the narrow disclosure this entry
exists to record.** The primary key spans the whole table while a by-id read is scoped to the
ambient budget, so an identifier another budget holds answers 409 here and **404** on the
read-back — which is why the sentence carries a second clause at all, and why it is phrased as
something to try rather than as a claim that the row is saved. The cost is one bit: a caller learns
that *some* budget in this deployment holds the identifier it proposed. **It is accepted.**
Identifiers are client-minted 128-bit values, so provoking that bit deliberately means guessing a
uuid, no content crosses with it, and the alternative is a caller that cannot distinguish a stored
row from a lost one. Closing it means widening the key to `(budget_id, id)`, which is a schema
change and a decision to be taken again rather than a defect to patch.

**Which constraint reports a row that breaks both rules is decided by OID — creation order — and
that is *not* the alphabetical rule this repository already documents for a column's `CHECK`
constraints.** Measured twice, both directions, and written out in
[ciphertext-envelope.md](ciphertext-envelope.md#which-constraint-a-row-is-reported-under-is-decided-by-oid)
beside the rule it will be mistaken for. Two consequences follow: a byte-for-byte retry is reported
under the **key**, so the identifier's answer arrives in front of the name's on both tables; and the
alternate key on each table is unreachable as a reported name, so an arm naming it would be dead
code no black-box test could ever redden.

**Alternatives rejected, and they fail differently.** **Leaving it unhandled**, which is what stood
until now — it answers 500 to a correct client doing the one thing HTTP clients do, and a 500 is the
one status that says nothing about what to do next. **One sentence for both conflicts**
— it is the tidy-looking collapse of two `catch` arms that differ only in a constant, and the row
already wearing the identifier may hold a different name or a name in a budget the caller cannot
read, so "re-read the payee list" sends it looking for something that is not on it. **Matching on
SQLSTATE alone** — the two arms raise the identical `23505` from the identical statement, so
whichever sentence was written first would be given to both, and a stranger's unique violation
flushed by the same `SaveChanges` would wear one of them. **Answering 200 with the row that already
exists**, the idempotent create — it needs the row read back and judged the same payee, which is a
comparison over envelopes this server cannot open, so it could only compare a blind index and would
still have to choose an answer for the case where the id matches and the index does not. **A
precheck before the insert** — it is check-then-act, so the key still has to catch the loser of a
race, and it cannot see the case that matters: a row in another budget is filtered out of every
read this side can issue, so the precheck answers "free" and the insert fails anyway. **Widening the
key to `(budget_id, id)` now** — it removes the disclosure and makes two budgets able to hold one
identifier, which is a change to what a row id *is* on routes whose ids are also associated data;
too large to make as a side effect of an error message.

**Consequences.** The payee create now has **three** answers and the account create **two**, so both
chapters carry the branch and the payee state diagram gained a state. A duplicate name answers 400
on the account create **only under a fresh identifier**, which is a live trap for the case that
proves it: reuse the seeded account's id while editing that case later and the status changes to
409 with nothing saying why, and the natural repair deletes the field-keyed refusal a person needs.
And the sentence is now load-bearing in the same way its neighbour is — it is the whole of what a
caller is told, so it names no constraint, no SQLSTATE and no row.

**Affected areas:** [payees.md](payees.md), [accounts.md](accounts.md),
[ciphertext-envelope.md](ciphertext-envelope.md).

---

## 2026-09-02 — A duplicate name is a field error on an account and a conflict on a payee, and the difference is who chose the name

**Context:** the entry below argues 409-on-a-create against 400-on-a-rename **within payees**, and
it left a larger disagreement unexamined. `POST /api/accounts` carrying a name whose blind index
another account in the budget already holds answers **400** with an error keyed on `Name`.
`POST /api/payees` carrying a duplicate index answers **409** with no `errors` member at all. Two
creates, and everything underneath them is the same: a unique B-tree index over
`(budget_id, name_key)`, a `23505` matched by constraint name in a repository, and a rename leg on
each table that answers 400. The only place the two tables disagree is the create — and **every
copy of the 409-versus-400 argument, in the docs and at both repository members, argues the payee's
create against the payee's rename and stops there.** Not one of them says why an account create is
not a 409 as well. [budgets.md](budgets.md#must) lists all four entities' collision behaviour in one
paragraph and names the payee create as the exception, as a fact. Stated and unargued is exactly the
shape an accidental departure takes, and it reads as one to anybody arriving without the history.

**Decision:** the two statuses stay apart, and the distinction that holds them apart is **who chose
the name**.

**An account is named by a person, in a form.** A duplicate means they picked a name they had
already used in that budget, which is a mistake about a field they are looking at — so the answer
names the field, the message attaches to the input, and retyping resolves it. There is nothing to
re-read: the account that already holds the name is not the account they were trying to open, and
adopting it is not an outcome anybody asked for. Two accounts called "Savings" is a person's error
and a 400 is what an error about a field is for.

**A payee is not named at the moment it is created; it is resolved.** The client folds and indexes
the typed counterparty, compares the digest against the payee list it already holds and can decrypt,
and posts only on a miss. So a collision does not say the person chose badly — it says **the list
the decision was made against was stale**, because another tab created the row, or the list was read
before it existed. The text they typed was right, and the remedy is to re-read and use the payee
already there, which is precisely what a bare 409 says and what no field-keyed 400 has anywhere to
put. Keying that failure on `Id`, `Name` or `NameKey` would ask a browser to correct three values it
computed exactly as intended.

**Said once, the rule is about the request rather than the table: a 400 is for a request its caller
can rewrite, a 409 is for a request that was correct against a state the caller no longer has.** The
verb is not the input — a rename collides on both tables and answers 400 on both, because a rename
is a name a person chose in both cases. The author is.

**Alternatives rejected, and they fail differently.** **409 on the account create too**, the option
that makes the four answers uniform — it buys consistency and spends the one thing the account form
needs, a field for the message to attach to, and it tells somebody who typed a name they already own
to go and re-read a list that will not help them. **400 on the payee create** — argued in the entry
below and unchanged: the members are all exactly what the caller meant, so the document would name a
field nobody can act on. **Deciding by verb — creates conflict, renames validate** — it happens to
produce today's payee answers and is the wrong rule underneath: applied evenly it turns the account
create into a 409, and it has nothing to say about a future entity whose names are machine-resolved
on a rename as well as on a create. **Aligning the statuses in either direction to remove the
asymmetry** — rejected because the asymmetry is carrying information; what was wrong was that it was
undocumented, not that it existed.

**Where the argument is weak, and it is weak in the same way the entry below is.** Each status
follows its dominant case and misses the other. A person can deliberately want a second payee for
one counterparty, and "re-read the list" is unhelpful advice to them; two tabs can race an account
create, and "correct the field" is unhelpful advice there. Neither miss is silent and neither loses
a row, which is why the dominant case is allowed to decide.

**Consequences.** The four collision answers now read as one rule with two inputs — the verb and the
author of the name — rather than as three agreements and an exception. `categories` and
`category_groups` fall out of it on the account's side without a decision of their own: their names
are typed into a form, so their creates stay 400, and their name columns being plaintext has nothing
to do with it. A fifth named entity gets its status by asking the same question. And
`ConflictExceptionHandler` staying extension-free stays load-bearing: the payee create's `Detail`
sentence has to carry the whole instruction, because it is the only place the instruction can live.

**Affected areas:** [payees.md](payees.md), [accounts.md](accounts.md), [budgets.md](budgets.md),
[categories.md](categories.md).

---

## 2026-09-01 — One payee-name index, two statuses: 409 on a create, 400 on a rename

**Context:** `payees.name` became ciphertext with a blind index beside it, and the consequence was
larger than on any column before it. `payees` was the one table this server looked rows up in **by
name**: `PayeeRepository.GetOrCreateAsync` trimmed a name, matched it through a `case_insensitive`
collation, and inserted only on a miss. None of that survives sealing — two seals of one name are
different bytes, the stable digest is taken under a key that lives in a browser, and `bytea` is not
collatable — so find-or-create became **unimplementable**, creating a payee became a request of its
own, and `IX_payees_budget_id_name_key` became the only thing standing between one counterparty and
two rows. That index is now reached by two verbs that were previously one, and each of them has to
be told something when it fires.

**Decision:** **a create that collides answers `409`, and a rename that collides answers `400` keyed
on `Name`.** `PayeeRepository.AddAsync` matches the `23505` **by constraint name** and raises
`Domain.Common.ConflictException`; `PayeeRepository.UpdateAsync` matches the identical violation on
the identical index and raises `Domain.Common.ValidationException`. One constraint, one table, two
statuses.

**What differs is the remedy, not the constraint, and that is the whole argument.** A create that
collides means a payee already carries this name in this budget and the client's list was stale. The
resolution is to **adopt the row that already exists**, which is not something a person corrects by
editing a field — so there is no member for a validation problem document to be keyed on, and a 400
would name a field nobody can act on. That reading is the one find-or-create used to make silently,
and the sentence [payees.md](payees.md) already carried about it survives intact: *reporting a race
somebody had no part in, over a payee that now exists and is the one they meant*. What changed is
**who re-reads** — this side cannot, so the 409 is what asks the client to. A rename that collides
means a person chose a name another row holds, and the resolution is to **choose a different one** —
a statement about `Name` in the request, which is exactly what a validation problem document carries
and a bare conflict status has nowhere to put.

**Both readings are wrong in the corner cases, and the status follows the dominant one.** A rename
can lose a race between two tabs, where "choose another name" is bad advice; a create can be a
person deliberately making a second payee, where "adopt the existing row" is. Neither miss is silent
and neither loses data.

**Alternatives rejected, and they fail differently.** **`409` on both**, the option that keeps one
status per constraint — rejected because the rename would lose the field-keyed `400`, and that loss
is larger than the asymmetry it removes: the status would say two things disagree without saying
*which field*, a form would have nothing to attach the message to, and a caller would handle two
statuses for one kind of mistake depending on which entity they were editing. That is the argument
`payees.md` has always made against answering a rename with a conflict, and the create does not
contradict it — it is a different act. **`400` on both** — it keys a create's failure on a member
the caller cannot usefully change; the id, the envelope and the index are all exactly what the
caller meant, and the only correct next step is a re-read. **Putting the existing payee's id in the
409 body** — `ConflictExceptionHandler` is shared by every conflict in the product and adds no
extension member, so there is nowhere to carry one, and the client has to decrypt the list to
confirm the row is the one it meant regardless. **A precheck before either write** — it cannot be
written: there is no name to look up, and the index-shaped version of it answers "taken" for the row
being renamed, so every case-only correction would be rejected as a duplicate of itself. It is
check-then-act besides, so the index still has to catch the loser of a race.

**Consequences.** The conflict handler's `Detail` sentence is now load-bearing: it is the whole of
what distinguishes this 409 from any other in the product, so it has to say what the caller does
next by itself — and it names no payee id, no SQLSTATE and no constraint. `PayeeConfiguration`
pins the index name for a **new** reason: it used to protect a swallow-and-re-read inside
find-or-create, and it now carries **attribution** for two different answers, so matching on
SQLSTATE alone would put either sentence on a violation of some other rule flushed by the same
`SaveChanges`. A collision on `PK_payees` is the same SQLSTATE and is a **third** answer carrying a
sentence of its own: a caller re-posting an id it already used is not a duplicate *name*, and must
not be sent off to re-read a list the payee it is being told about may not be on — which is
precisely why it could not share this one. The entry above on the repeated create carries it. And
the reviewer's instinct to harmonise the two is expected rather than guarded against: the argument
is written at both repository members and in `payees.md`, because nothing in the build can hold it.

**Affected areas:** [payees.md](payees.md), [transactions.md](transactions.md),
[budgets.md](budgets.md), [ciphertext-envelope.md](ciphertext-envelope.md).

---

## 2026-09-01 — A name and its blind index move together, and the `UPDATE` grant is where that was first broken

**Context:** `accounts.name` became ciphertext with a blind index beside it — the second sealed
column, and the first where **uniqueness had to survive the change rather than be surrendered**.
That makes a name two columns: `name`, the envelope nobody on this side can read, and `name_key`,
the keyed digest `IX_accounts_budget_id_name_key` enforces one-name-per-budget over. The domain
already refused to write half of one: `Account.Create` and `Account.Update` take a single
`IndexedName` and offer no spelling for a bare `NarrativeField`, and both columns are `NOT NULL`.
What nobody had asked was what the **grant** does with a pair, and the answer was already wrong:
`GRANT UPDATE (name, type, opening_balance)` admitted the envelope and not the index.

**The defect that produced was total and silent in the suite.** PostgreSQL checks column privileges
per column *named in the statement*. `Account.Update` assigns both properties, EF emits one `UPDATE`
naming both columns, and PostgreSQL refused the whole statement with `42501` — so **renaming an
account was impossible for the application role**, on every path, for as long as the grant stood.
Nothing was red. The test that read as the proof that renaming worked issued
`update accounts set name = @value`, one column, so it exercised a **column privilege** and reported
it as an **operation**; every path that renames through EF failed on a grant that test was not
sending. Measured against a container in both directions: `42501` under the old list, `UPDATE 1`
under the new.

**Decision:** the grant names `name` and `name_key` together, and the rule written beside it is
general rather than about this table — **a name and its index move together or not at all**. An
`UPDATE` list reaching one of a blind-indexed pair and not the other narrows nothing; it has exactly
two outcomes and both are wrong. A review that finds one half of such a pair on an `UPDATE` list has
found the defect without needing to know which table it was looking at. Every blind-indexed name
column that follows wants the same pair — granted together, withheld together, and made immutable,
if it ever is, by leaving **both** off the list.

**What the other direction produces, and why nothing can see it.** Withholding one column forbids
the operation loudly. **Permitting one and not the other permits half of it, and that is the worse
half**: the row would carry an envelope for the new name and a digest taken over the old one. The
unique index would go on policing a name the row no longer holds; a search for the new name would
miss the row that has it; a search for the old one would return a row that does not; and a rename
onto a name already taken would be accepted. Every constraint is satisfied, nothing reads back
wrong, and **no layer beneath the browser can notice**, because recomputing either half needs the
account's index key — which this server has never held and never will. There is no `CHECK`, no
constraint, no trigger and no test below the application that can compare the two columns; the only
witness is a client that holds the key, months later, looking for a row it cannot find.

**Alternatives rejected, and they fail differently.** **A table-wide `GRANT UPDATE ON accounts`** —
it would have fixed the rename in one word and taken `budget_id`, `currency_code` and
`created_at_utc` with it, since PostgreSQL column privileges are additive and a `REVOKE` cannot
subtract a column from a table-wide grant; the whole immutability story on this table is the
*omission* of a column from a list. **Splitting `Account.Update` so a rename writes one column** —
it makes the grant honest by making the domain dishonest, and re-creates the mispaired row above as
an ordinary code path. **A trigger comparing the two columns** — it cannot compare anything: there
is nothing on this side to recompute a digest from, and ADR 0002 refuses procedural logic pushed
down for the sake of being low. **Leaving the test as a one-column `UPDATE` and adding a second
case** — the one-column statement is not the operation, so a suite containing it would go on
reporting a column privilege as a rename; the fix was to make the existing success half write the
statement `Account.Update` actually emits.

**Consequences.** The rule now has three owners at three moments, and none of them is a restatement
of another: `IndexedName` refuses a **call** that is half a name, the `NOT NULL` pair refuses a
**row** that is, and the grant permits the **statement** whole. The immutability idiom on this table
is unchanged in spirit and one column longer in fact. And the general lesson is about tests rather
than about grants: **a test that pairs a refusal with a permitted write has to issue the write the
operation issues** — a narrower one passes, proves a privilege, and says nothing about whether the
product works.

**Affected areas:** [accounts.md](accounts.md), [ciphertext-envelope.md](ciphertext-envelope.md),
[budgets.md](budgets.md), [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md).

---

## 2026-08-31 — `Domain` grants its internals to `Infrastructure`, and to nothing else

**Context:** the entry below left one consequence open. `NarrativeField.FromStore` — the unchecked
door that rebuilds a sealed value from bytes a column already holds — is `internal`, because a public
member that skips the validating factory *is* the hole the type was built to close: a way to put
unjudged bytes into a narrative column, on a call site that reads like bookkeeping and passes any
review not looking for it. With `budgets.name` becoming the first column that stores an envelope,
something had to materialise it, and the only code that can is a persistence configuration's value
converter — which lives in `Infrastructure`, one ring out. So the door had to open to exactly one
assembly, or the column could not be read back at all.

**Decision:** `Domain.csproj` carries `<InternalsVisibleTo Include="Infrastructure" />` — the first
grant of internal visibility in this solution — and the argument for it is written beside the
element, not in a commit message. It names one assembly: `Application`, `Api` and both test projects
still cannot call `FromStore`.

**What it buys, and what it does not cost.** Nothing about it inverts the Dependency Rule. `Domain`
still declares no reference of any kind, and `Infrastructure` already reaches `Domain`; what travels
is **visibility, not a dependency**. That is also why it is an edge no compiler notices — an
outward grant closes no loop, so MSBuild's cycle detection, which refuses the outward
`ProjectReference` between rings for free, is blind to it.

**Alternatives rejected, and they fail differently.** **Make `FromStore` public** — a member every
ring can reach so that one of them can, and the mistake it admits is the invisible kind: a row
holding plaintext is a well-formed row that stores, reads back, violates nothing and simply hands
the operator the ledger. **Materialise through the validating factory instead**, needing no grant at
all — rejected on the read side's own argument: a validating read makes the caps retroactive, so
lowering one by a byte throws every older row out of the middle of a query, and a future version 2
would destroy the version 1 rows it exists to rewrite. **Put the mapping in `Domain`** — it would
carry an EF Core package on the innermost ring, which is the Dependency Rule breaking inward and the
headline case the pinned graph exists to catch. **Grant more widely, to the test projects as well**
— rejected on the property that makes this grant reviewable at all: one recipient can be weighed
against one argument, and a list cannot. The pinned edge set names no other recipient, and a second
row on a test project is the edit the guard was sabotaged with.

**The guard was taught to see it in the same change, and it holds the set rather than the wisdom.**
`ProjectReferenceGraphTests` renders `InternalsVisibleTo` as an edge kind — one row per grant,
`Domain: internals Infrastructure`, in the pinned set — so a second grant beside this one reddens
the pin **by name** instead of arriving with nothing red. One row per grant and never a count or a
flag: "Domain grants its internals to somebody" would stay true while the somebody changed. It was
sabotaged with a second grant before it was believed. What the guard cannot judge is whether a grant
*deserves* to exist — a reviewer widening the pinned set in the same commit passes it — which is
exactly why the argument lives in the csproj and this entry exists. The guard's whole job is to make
sure nobody has to notice the line in order to be told it moved.

**Consequences.** The read side's no-revalidation rule is no longer held by review *and by nothing
running*: it now has a caller, and the converter's read arm is where a reviewer will next propose
adding a check. `Domain` keeps its "declares nothing" invariant as a single pinned line, with the
grant as a second line of a different sort beside it. And the next grant, whenever it is proposed,
has a written precedent to be argued against rather than a blank file to be added to.

**Affected areas:** [ciphertext-envelope.md](ciphertext-envelope.md), [budgets.md](budgets.md),
[dependency direction](../engineering/dependency-direction.md).

---

## 2026-08-31 — A narrative column takes a value type, and the read side does not judge

**Context:** the server was about to gain columns holding text it must never be able to read. The
requirement is easy to state and impossible to test: *no narrative value is ever server-readable.*
Nothing observable goes wrong when it is broken — a row holding plaintext is a well-formed row, it
stores, it reads back, no constraint fires, no round trip disagrees, and the operator simply has the
ledger. So the question was not how to check the rule but **what could make breaking it a build
failure**, given that the checkable surface here is framing and a length and nothing else: the
server holds no key and never will.

**Decision:** **a narrative column is typed `NarrativeField`, a value type with no constructor,
factory or conversion taking a `string`.** Bytes get in through one validating factory — framing
from `CiphertextEnvelope`, a ceiling the caller names — and through nothing else. A searchable name
goes one step further: `IndexedName` carries the sealed name and its blind index as **one** value,
so a call cannot supply half. `NarrativeFieldLimits` declares the two caps the factories apply, and
`BlindIndexText` is the index's own wire step.

**What the type buys that a check cannot.** With the column typed this way, writing plaintext into
one does not compile. The rule leaves review and enters the build — which is the whole point,
because every other layer is blind to the mistake. It is the same shape as the second
account-creating path `CLAUDE.md` warns about, one line that reddens nothing, with the compiler put
in front of it.

**Alternatives rejected, and they fail differently.** **Raw bytes on the entity with a private
length check per entity** — eight columns across six entities, so six copies of one rule, and the
copy that drifts still stores, still reads back and still opens; it differs from its siblings only
in what it admits from a client nobody exercised that day. **Raw bytes plus one shared static
validator** — it fixes the drift and leaves the worse half standing: the property is still typed as
a buffer, so it is still assignable from anything in scope, and the next member added to the entity
assigns bytes nothing judged while the validator sits one file over looking like the rule was kept.
A type is the one guard a later caller cannot forget to call. A **`readonly struct`** was rejected
on a narrower point: its unhidable parameterless constructor makes `default(NarrativeField)` a
narrative field holding no envelope, assignable to a non-nullable property — exactly what an entity
built by a path that forgot to seal a member would carry.

**The read side deliberately does not re-validate, and that is the half a reviewer will want to
"fix".** `FromStore` copies and checks nothing. A validating read makes the caps **retroactive**:
lower a cap by one byte and every row written under the old number stops materialising — thrown out
of the middle of a query rather than refused at an edge where somebody could be told — so the screen
fails whole and the value is unreachable by every path including the export. A one-integer diff
would have become data loss. The same argument protects a future version 2 from destroying the
version 1 rows it exists to read and rewrite.

**Consequences.** The caps are **two** numbers over field classes rather than eight over columns,
and they bound the **envelope**, not the text — the only length this side can measure — so they must
never be "corrected" into character limits. The pair type and the schema's `NOT NULL` pair are two
guards at two moments and neither replaces the other; collapsing either passes green. `FromStore` is
`internal` and the solution grants no assembly access to it, so nothing outside `Domain` can call it
yet and its no-revalidation rule is held by review alone — the grant that closes that is one line
naming its recipient, and it belongs in a diff somebody reads. And the headline claim is covered by
**no test and no test that could exist**: it is held by an absent member, which the spec file states
about itself so the case count is not mistaken for evidence.

**Affected areas:** [ciphertext-envelope.md](ciphertext-envelope.md), [_overview.md](_overview.md).

---

## 2026-08-31 — The case fold is a table this product ships, at a Unicode version this product picks

**Context:** a blind index is a MAC over a *normalized* name, and the third step of that
normalization is a full case fold. Two clients that fold one name differently key it to two values,
which is a duplicate that never merges on a column whose entire purpose is that equal names collide
— and it cannot be repaired afterwards, because a blind index cannot be recomputed without the
plaintext it was taken over and that plaintext is encrypted. So the fold had to come from somewhere
whose behaviour this product controls. The obvious somewhere is the platform, and the question is
whether the platform can stand there.

**Decision:** **ship the fold as data.** `case-fold-table.ts` is `CaseFolding.txt` at **Unicode
17.0**, statuses **C and F** only, frozen into two base-36 strings that `case-fold.ts` decodes and
nothing else reads. The Unicode version is a value this product names and exports, and it rides in
`vectors/blind-index-v1.json` beside a digest of the table and the frozen answers, so the constant,
the data and what they produce are held against one another.

**The measurement that settled it, and both halves were run rather than reasoned.**
`String.prototype.toLowerCase` is not a fold at all — it is a lowercase *mapping*, a different
transform that agrees on ASCII and diverges wherever the difference decides a match: it gives a
different answer for **239** of the table's entries and leaves 211 untouched, keeps `ß` as `ß` where
the fold gives `ss`, keeps the `ﬁ` ligature, and cannot follow Cherokee, which folds *upward*. It is
contextual besides, where the fold is not. And every platform API that genuinely *is* a fold reads
the host's Unicode data: the runtime this repository builds on reports Unicode **16.0**, and **52**
code points fold in the shipped table that it does not fold at all. Two clients each calling their
own platform would therefore key one name to two values, which is what CON-009 forbids.

**Alternatives rejected.** **`toLowerCase`** — not the transform, per the measurement above.
**`toLocaleLowerCase`** — the same transform plus a locale, and a Turkish host maps `i` to `İ`,
making the key depend on where the browser thinks it is. **`Intl.Collator` at a case-insensitive
sensitivity** — it answers an *ordering*, and a blind index needs bytes to take a MAC over.
**Status S, the simple fold** — nearly the same name and nearly the same output, which is what makes
it dangerous: it agrees on almost every name a person types and disagrees on the handful where the
difference decides a match. **Status T, the Turkic conditional pair** — it is the only part of
`CaseFolding.txt` that would make the transform locale-dependent, which is the property the table
exists to remove. **A runs-only table** — it would fold almost every name correctly and miss all 104
multi-output entries, `ß` among them, which is the single most common case the table exists for.

**Consequences.** The cost is a generated file nobody may hand-edit — a value corrected by eye there
is a name that stops matching itself the day a second client folds it correctly — and a fold that is
*ahead* of the host, so a whole-space cross-check against the engine's own Unicode data can only
find under-coverage and can never confirm agreement with 17.0. Raising the version is a **format
change and not a dependency bump**: every index already written was computed under the fold the
version names, so a name that folds differently becomes a row that no longer answers its own search,
silently and forever. What it buys is that the fold is a property of the product rather than of
whatever browser or runtime a client happens to be, which is the only shape in which "every client
keys one name to one value" is a claim anybody can make. **One consequence must be written down
before somebody fixes it**: `İstanbul` and `istanbul` are two different names under this contract —
U+0130 folds to `i` plus a combining dot under statuses C and F — and a client that made them agree
would disagree with the database's unique index and with every other implementation, silently in
both directions. The frozen vectors carry both spellings with their two distinct answers so the
contract is what a reader meets.

**Affected areas:** [account-keys.md](account-keys.md),
[ciphertext-envelope.md](ciphertext-envelope.md), [_overview.md](_overview.md).

---

## 2026-08-30 — A seal interrupted mid-cipher is judged by key identity, not by the generation counter

**Context:** `AccountKeyCustodyService` keeps a generation counter, bumped by everything that
changes custody, and `openField` compares it after the cipher — a read that resolves into a tab
whose keys were dropped is holding narrative plaintext the tab is no longer entitled to, so it
answers `locked` instead. `sealField` sits in the mirror position and hands back a ciphertext,
which raises the question of whether the same check belongs there. Two different events can land
inside that window: a `lock()`, which is a sign-out while a save is in flight, and an `adopt()`,
which publishes **another account's** keys over the ones the seal is running under.

**Decision:** **a seal compares key identity and never the counter.** Keep the wire while the
account holds the very key object this seal ran under, or holds none at all; answer `locked` only
when the content key was **replaced**. Object identity is the test rather than a stand-in for one:
one private method is the only writer of that field, a `CryptoKey` is opaque, and re-adopting the
same object is the same account.

**Why the counter is the wrong instrument.** It moves for both events, by design, so it cannot tell
them apart — and one answer for both is wrong in whichever direction it is given. Keeping the wire
after a replacement invites the caller to write one account's ciphertext into a row belonging to
the next, where nothing in the product will ever open it and nothing on the server can see that it
happened. Dropping it after a plain `lock()` discards text somebody has just typed, in exchange for
nothing at all: the wire is still that account's, readable only under the key it was sealed under,
and there is nobody it could be wrong for. This was settled by mutation rather than by argument —
the counter copied onto the seal greens the replacement case and reddens its neighbour, one for
one.

**Alternatives rejected.** **A second counter bumped only by `adopt`** is the identity test with a
level of indirection in front of it, and it goes silently wrong the first time a path that
publishes keys forgets to bump it. **Reading `status()` after the cipher** separates nothing: an
account whose keys were replaced reports `unlocked`, and so does one that was never touched.
**Answering `locked` for every interruption** is the tidy symmetry with the read and costs somebody
their work. **Answering `sealed` for every interruption** — what an unguarded `return` gives — is
the cross-account write.

**Consequences.** The two frames now carry two instruments, and that has to be written down or the
next reader "fixes" the asymmetry: the read compares the counter, because plaintext is entitled to
nobody once the keys are gone, and the seal compares the key, because a ciphertext is entitled only
to whoever holds the key it was sealed under. The accepted cost is the bound of identity itself —
the same bytes re-imported are a different object and would be read as a replacement, discarding a
wire that would in fact have opened. Nothing in the platform can compare the material behind two
non-extractable keys, so identity is the only test available, and of the two ways it can be wrong
it chooses the one that loses a save over the one that writes a ciphertext nobody can ever open.
Both events are pinned by cases arranged identically at the two platform boundaries, so they read
as one decision made twice rather than as two checks that happen to differ.

**Affected areas:** [account-keys.md](account-keys.md).

---

## 2026-08-30 — Sealing and opening a narrative field are operations on custody, not a key anybody borrows

**Context:** the codec that seals a narrative field takes the account's content key as its **first
parameter**, and the class that holds that key may return one to nobody. Those two facts together
leave the operations nowhere else to go: a module other than `AccountKeyCustodyService` that wanted
to seal a description would have to be *handed* the key, which is the member that class exists to
refuse. The question this entry settles is therefore not *where* — that was forced — but which
types cross the boundary and what a refusal looks like.

**Decision:** **`sealField` and `openField` are methods on `AccountKeyCustodyService`.** Each takes
a `NarrativeFieldBinding` — the caller's fact about which table, which column and which row — and
answers a small union: `SealedField` is `sealed` or `locked`, `NarrativeText` is `text`, `locked`
or `unreadable`. Nothing in the product calls either one, because no column holds an envelope.

**Alternatives rejected.** **An accessor**, in any of its three costumes: a getter, a
`Signal<CryptoKey | null>`, or `withContentKey(use)`. The callback is the worst rather than the
compromise — it *looks* scoped, which is what invites the widening, and the key survives in a
closure the moment somebody stores the callback or awaits inside it. Non-extractability answers
none of the three: it stops the bytes leaving and does nothing about a caller decrypting a whole
budget into a log line. The pressure toward this is live and comes from a linter — two
`eslint-disable no-unused-private-class-members` directives stood on the class, and the other
reading of "unused private field" is "add a getter"; this change retired one of them by giving
`#contentKey` real readers. **Moving the eight `table × column` pairs onto custody too**, as eight
methods or one `switch` on `binding.table`: custody would then know the pairs, a ninth would become
two edits with nothing forcing the second, and a missing case falls through to `undefined` at
runtime rather than failing to compile. **A `NarrativeCryptoService` injecting custody** is not a
third option — to seal it needs the key, so custody has to hand it one, and it collapses into the
accessor with an injector in front.

**Consequences.** Custody imports the *type* `NarrativeFieldBinding` and never `NARRATIVE_FIELDS`:
erased at runtime, and derived from that list at compile time, so custody follows the codec and
cannot lead it. Two source-text rules carry the rest — no public member returns a `CryptoKey`, and
none of the eight tables' or columns' words appears in the file — both comparing **sets**, so
reordering never reddens and only a widening does. The census over the public surface cannot be
shown to redden on disk: the settings spec's `Pick`-over-`keyof` stub makes any new public member a
compile error first, which is a good order and not a proof. Two rules that looked held were
established by mutation and are pinned now: the refusal on an **extractable** content key was
deletable, and the **order of the two gates** — binding judged before custody — was unpinned, with
a reversal reporting an unrecoverable caller defect to an unlocked tab and swallowing it in a locked
one. Only refusals a person can act on become results; a non-canonical row id and an extractable key
keep throwing. The blind index is **not** part of this: its message grammar waits on a
specification revision, so `#indexKey` keeps the one remaining suppression and no placeholder was
written.

**Affected areas:** [account-keys.md](account-keys.md),
[ciphertext-envelope.md](ciphertext-envelope.md).

---

## 2026-08-28 — The account-keys read is keyed on the account, reversing the credential narrowing

**Context:** reverses the decision of 2026-08-27 below, "The wrapped account keys get a reader,
narrowed by the credential that opened the session". That entry's central argument was that only the
rows under the credential which just authenticated can be opened by anything the browser is holding.
The argument is false, and two things already in the repository say so.

**`PasskeyReauthentication` looks a passkey up by *account*.** It calls
`IPasskeyRepository.FindByWebAuthnCredentialIdForUserAsync` with `IUserContext.UserId`, never with the
session's credential. And the assertion options carry **no `allowCredentials`** — `PasskeyRequestOptions`
and `BeginReauthenticationHandler` each state that as a decision — so the **authenticator** chooses
which of the account's credentials answers a ceremony. The client cannot know in advance which one it
will be.

**The failure is reachable and silent.** Somebody signs in by redeeming a recovery code, so the session
opens over the recovery-codes credential. They ask for a new set of codes, which is gated on a fresh
**passkey** assertion. The ceremony yields the passkey's key-encryption key; the narrowed read hands
back the ten recovery-code envelopes; every unwrap fails, and the client tells them to present another
factor having just been given a valid one. Every row is correct, the status is `200`, and nothing on
the server sees it.

**Decision:** **`GET /api/me/account-keys` returns the envelopes of every factor the authenticated
account holds** — one entry per registered passkey and ten per set of recovery codes, so eleven for an
ordinary account. The response shape is unchanged; only the number of entries is. The account is the
unit the keys belong to, and a read keyed on anything narrower refuses a factor that was just verified.

**What widening costs, and why it is accepted.** A caller now receives entries it holds nothing to
open. The operator already holds every one of these rows, so nothing is disclosed to the party the
design defends against, and a factor's envelopes open **only** under a key-encryption key derived from
that factor. What is genuinely new is the count, which the same principal can already assemble from
`GET /api/me/credentials` and `GET /api/me/recovery-codes`.

**Consequences.** The handler no longer needs a session at all: `ISessionRepository` is gone,
`GetAccountKeysQuery` declares no member, and the endpoint reads no claim. The `404`-versus-empty rule
survives its own reason dying — there is no guessable identifier left and therefore no enumeration
oracle, and what keeps `200 []` now is that the client reads an empty list as "present another factor"
and any failed read as "try again in a minute". `Cache-Control: no-store` is stated on this route, the
one endpoint in the product that returns key material. And a keyless factor is conceded as a fourth
cause of an empty answer: "every factor has a row" is a property of the three write paths that exist,
not a fact the schema holds.

**Affected areas:** [account-keys.md](account-keys.md), [sessions.md](sessions.md),
[ADR 0018](../decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).

---

## 2026-08-27 — The account's keys are held for the session, in one tab, and nowhere a reload survives

**Context:** the entry below gave the wrapped account keys a route to come back through, and closed
with "the client still calls nothing". This is the client. A browser that had just proved a passkey
held a key-encryption key and a route it never asked, and a browser that had just created an account
held the two keys in the clear on a screen it was about to navigate away from. The question this
entry settles is not *how* to open an envelope — `unwrapAccountKeys` had been written and pinned for
some time — but **who holds the result, for how long, and what a page load does to it.**

**Decision:** **`AccountKeyCustodyService` holds the account's content key and index key as
non-extractable `CryptoKey` objects, `providedIn: 'root'`, for the life of the document and no
longer.** A passkey sign-in hands it a key-encryption key and it reads the envelopes back and opens
them; registration hands it the pair directly on the `201`. It reports lockedness and a failure word
and returns nothing else.

**Root-provided, and that breaks the component-provided habit on purpose.** `RegisterService` and
`SignInService` are provided on their screens because an abandoned *attempt* should die with the
screen that abandoned it. These keys are not an attempt: they are state of the **session**, and a
session outlives every screen — a person unlocks once and stays unlocked while they move around the
app. Route-providing on `app` is the near miss and is worse than it reads: `guestGuard` bounces an
authenticated visitor off `/welcome`, that bounce destroys the `app` injector, and a back button or
a bookmark then discards both keys and locks the account with no ceremony on screen to unlock it
again — with nothing red and no symptom but an account that was readable a moment ago. The price of
root-providing is that ending custody has to be a method, which is why clearing has exactly one
owner: `SessionService.ended()`.

**Per tab, never shared and never persisted, and this half has to be written down because it is an
absence.** A non-extractable `CryptoKey` is **structured-cloneable**, so both IndexedDB and a
`BroadcastChannel` hand-off work with no byte ever exposed, and the next person who learns that will
read their absence as an oversight rather than as a refusal. IndexedDB is the expensive one: it
makes the account's decryption capability outlive the browser closing, so whoever has the device and
a live cookie reads the narrative with **no factor presented**, and every check in the product still
passes while the requirement fails. A `BroadcastChannel` is weaker and wrong the same way one step
down — it unlocks a tab in which nobody presented anything. What the refusals buy is the property
the design rests on: **a page reload locks the account, and getting back in costs a ceremony.**

**That reload leaves a *locked account*, which is not a locked session.** The two words are kept
apart deliberately: a locked account is a browser that does not hold the content key, while a locked
session is a row a federated credential opened. The state is vacuously satisfied today, because
nothing in this product is encrypted; the screen that says so and the control that leaves it are a
later story.

**Alternatives considered:**

- **Component-provided, like its two neighbours.** Consistent, and wrong for the same reason
  route-providing is: it ties the account's keys to a screen, so the first navigation away locks the
  account.
- **Route-provided on `app`.** The tidiest-looking answer — the keys would belong to the part of the
  route table that renders budget content. Rejected above: `guestGuard` makes the router able to
  destroy them silently.
- **Persist the keys in IndexedDB so a reload does not lock the account.** Rejected: it is the one
  change that makes an unlocked account survive the browser closing, and nothing about a stored
  non-extractable key looks wrong to any check this product has.
- **Hand the keys to a second tab over a `BroadcastChannel`.** Rejected: a tab that presented no
  factor would be unlocked by one that did.
- **Have registration discard its pair and re-read `wrapped_account_keys` through the route.** The
  more principled-looking option, and rejected on three counts: it needs the passkey's
  key-encryption key to survive the codes step on some instance, which is the same power one step
  removed; it puts a round trip and a new failure mode on the happiest path in the product; and the
  verification it appears to buy is illusory, because a passkey session's read returns the **passkey
  factor's pair alone** and never exercises the ten code pairs, which is exactly where the
  mispairing hazard lives.
- **Let `unlock` return a promise.** Rejected as a signature, not as an implementation detail: the
  caller is a sign-in, somebody would await it, and one refactor later that `await` grows a `catch`
  — turning a key that did not open into an authentication that failed. Only `anonymous` may bounce
  anybody out of an account, and a factor that opened nothing is not that.
- **Lock the keys from an `effect()` over the session status rather than inside `ended()`.**
  Rejected: it fires on construction, so whether it wipes an already-adopted pair is decided by
  injection order, and the only honest predicate available to it locks on `unreachable` too —
  destroying both keys over one blinked request.

**Affected areas:** [account-keys.md](account-keys.md), [sessions.md](sessions.md),
[passkeys.md](passkeys.md), [recovery-codes.md](recovery-codes.md),
[ciphertext-envelope.md](ciphertext-envelope.md), [_overview.md](_overview.md).

---

## 2026-08-27 — The wrapped account keys get a reader, narrowed by the credential that opened the session

**Context:** the 2026-08-13 entry below recorded "no endpoint returns a wrapped key" as one of four
deliberate absences, and said the story needing one would argue for it in place, where its own threat
model could be stated. This is that story. Every account created since registration became one
consented act owns a content key and an index key wrapped under eleven factors, and nothing on the
server would hand any of them back, so a browser that had signed in held a key-encryption key and
nothing to use it on.

**Decision:** **`GET /api/me/account-keys` returns the envelopes filed under the credential that
opened the calling session, and nothing else about them.** One entry for a passkey, ten for a set of
recovery codes; each carries the factor identifier both envelopes were sealed against and the two
envelopes as unpadded base64url. No credential id, no user id, no registration instant.

**Narrowed by the credential rather than by the account, and that is the decision rather than an
optimisation.** An account holding a passkey and a set of codes has eleven rows across two
credentials, and only the ones under the credential that just authenticated can be opened by anything
the browser is holding. Returning all eleven would hand a passkey session ten envelopes it can never
open — material travelling further than it is needed, which is a defect whether or not anything reads
it. The narrowing also does not have to widen for the story that adds a factor without re-encrypting:
that browser has **already** unwrapped the content and index keys through the factor it signed in
with, so it wraps the new factor itself and never reads another factor's envelopes. Without that
sentence written down, the next reader widens the route for good reasons.

**An empty array, never a `404`.** A session that was never established, one that has already ended,
and one belonging to somebody else are one indistinguishable answer on purpose. This is the single
most likely thing a later reader corrects, because "not found → 404" is right almost everywhere else;
here it rebuilds the enumeration oracle, on the one route that names an account's key custody. A
credential holding no factor rows answers the same empty array — a revocation or an erasure that
landed between authenticating and reading, which is a race rather than a corruption.

**Consequences.** The client still calls nothing: what `unwrapAccountKeys` was waiting on has moved
from the server to the browser, and the module stays uncalled until a screen needs it. The `SELECT`
grant on `wrapped_account_keys` now answers to two kinds of reader, and the two row-level-security
isolation tests keep their sentence — an application read answering correctly says nothing about what
the policy refused.

**Affected areas:** [account-keys.md](account-keys.md), [sessions.md](sessions.md),
[recovery-codes.md](recovery-codes.md), [ciphertext-envelope.md](ciphertext-envelope.md),
[data-isolation.md](../engineering/data-isolation.md),
[ADR 0018](../decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).

---

## 2026-08-21 — Sign in with a passkey, not with Google

**Context:** two ways into this product stood beside each other, and only one of them was consented
to. `POST /api/registration` created an account as a single act — the passkey, the card of ten
recovery codes and the eleven wrapped copies of the account keys in one save. Beside it,
`UserProvisioningMiddleware` still turned any authenticated Google bearer into an account on six
marked route groups, and the account it produced held **one federated credential and nothing that
could read it**: no passkey, so no session reaching budget content, and no way past the
re-authentication gate in front of erasure. Every invariant registration established had to be
written down twice — once as what that path does, once as a warning that it was not a claim about
every row in `users`. Three separate documents carried a promise that the marker set would collapse
"when account creation becomes a consented act". It had; nothing had collapsed.

The provider was also still the thing that authenticated ordinary requests. `Budgetoid.Bridge`, a
policy scheme forwarding to the session cookie handler when a cookie was present and to `JwtBearer`
otherwise, was the API's default, and it existed so the surface kept working while sessions landed one
commit at a time. Everything downstream of it was shaped by its presence: `FullSessionRequirement`
had to admit a principal that authenticated on any scheme but the cookie's, because a Google bearer
carried no session and therefore no kind claim, and a requirement refusing what it did not find would
have refused the whole product.

**Decision:** **the identity provider is contacted once in an account's life, and a session cookie
authenticates everything else.**

- `UserProvisioningMiddleware` is deleted, with `ProvisionsUserAttribute` and its six
  `.WithMetadata(…)` applications, `RegistersAccountAttribute`, `EnsureUserHandler`,
  `ResolveUserHandler`, their commands, `ProvisionedUser`, `IUserRepository.TryAddAsync` and
  `NoAccountTitle`.
- `User.Create` — the factory that minted an account under a fresh identifier — is deleted too, so
  `User.CreateWithId` is the only way to obtain a `User` and it takes an id derived from a ceremony's
  own challenge. **That buys a visible edit, not a compile error**: `CreateWithId` takes a plain
  `Guid`, so a second creating path is one line that would redden nothing — what it can no longer do
  is invent an account id without saying so in the diff a reviewer reads.
- The two claim gates move to `RegistrationClaimGate`, an `IEndpointFilter` on the
  `/api/registration` group, carrying `MissingClaimsTitle` and `UnverifiedEmailTitle`.
- The bridge scheme is deleted. The session cookie handler is the default, the fallback policy names
  it explicitly and carries `FullSessionRequirement`, and `FullSessionRequirement`'s
  another-scheme escape hatch goes with the bridge. `JwtBearer` stays registered and is reached by
  exactly one policy — registration's.

**What that buys, verified by running rather than reasoned:** an authenticated bearer naming no
account now answers **401, indistinguishable from an anonymous request**, because
`AuthorizationMiddleware` re-authenticates against the cookie handler alone and it returns
`NoResult`. Six route comments used to end with "an authenticated subject with no account is refused
instead"; the refusal survives and the enforcer changed, which is why those sentences were rewritten
rather than deleted.

**Alternatives considered:**

- *Keep the bridge as a permanent seam*, so a bearer keeps working for clients that have not moved.
  It is the cheapest option and it is what the deletion is for: while any scheme but the cookie can
  authenticate an ordinary route, `FullSessionRequirement` must admit a principal carrying no kind
  claim, which is a hole with a comment on it rather than a rule. A permanent seam also keeps
  `NoAccountTitle` alive — a titled refusal describing a state the product can no longer be in.
- *Push the three claim rungs into the Application ring*, which is what `RegisterAccountHandler`'s
  remarks and [registration.md](registration.md) both promised. **It could not be done, and the
  promise is corrected rather than kept.** Judging `email_verified` there needs either a
  `ClaimsPrincipal` inside `Application` — against the rule that keeps `System.Security.Claims` out of
  that project, the reason the endpoint reads the two claim members off the principal at the call
  site — or a member on `RegisterAccountCommand` for the answer to land in, which
  [users-and-ownership.md](users-and-ownership.md) argues against by name: the verified-email claim is
  read and never stored, and the command carries only the subject and the address precisely so there
  is nowhere for it to go.
- *An authorization requirement or a `RequireAssertion` on the group's policy* instead of an endpoint
  filter. Both run earlier and both answer **403 with no title**, collapsing two refusals a caller
  acts on differently into one untitled status.
- *`JwtBearerEvents.OnTokenValidated`*, which runs earliest of all and can answer 401. Writing a
  titled `ProblemDetails` from there needs `OnChallenge` written too, and the gate becomes a property
  of the **scheme** rather than of the route — invisible to anybody reading the route table, which is
  where every other rule about who may reach those two routes is declared.
- *A new middleware reading a new marker.* It is the deleted middleware under another name: the same
  opt-in metadata, the same silence when a group forgets it.

**Accepted behaviour change:** a filter runs after model binding, so a caller sending an unverified
address **and** a malformed body is now answered `400` where the middleware answered `401`. Nothing
measures it. It is a worse order to be told things in, not a disclosure — a deserialization failure is
a fact about the caller's own request.

**Known gap, stated rather than hidden:** deleting `IUserRepository.TryAddAsync` removed the
**mis-attribution control** the repository-attribution census cited by name — a test staging an
unrelated unique violation into that method's two-index catch filter, proving the filter did not claim
violations it should let escape. `RegistrationRepository` narrows on the same two index names,
`IX_users_email` and `IX_credentials_provider_subject`, and has **no equivalent control at any layer**.
Its own census entry argued the missing control was cheap because three of its four indexes are keyed
on a user id derived for that one registration — and that argument does not cover these two, which are
keyed on values a stranger holds, which is the whole point of both rules. A control closed a gap and a
deletion partly reopened it.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md),
[registration.md](registration.md), [sessions.md](sessions.md), [erasure.md](erasure.md),
[export.md](export.md), [recovery-codes.md](recovery-codes.md), [passkeys.md](passkeys.md),
[budgets.md](budgets.md),
[ADR 0019](../decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md),
[ADR 0021](../decisions/0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md).

---

## 2026-08-14 — A recovery factor is one code, not one set of ten

**Context:** the entry below records wrapping the account's two keys under every recovery factor. It
left one question unasked, and a review asked it: **which of a set's ten codes derives the
key-encryption key?** The client API was per-code from the first line — `keyEncryptionKeyFromRecoveryCode(code)`
— while the schema was per-set, because `wrapped_account_keys` was keyed on `credential_id` and a set
of recovery codes is one `credentials` row. Nobody reconciled the two.

Held together, the two halves said something nobody would have written down: only whichever code the
set's single envelope pair happened to be sealed under could open the account. A person redeems
whichever code they still have, so **nine redemptions out of ten would have opened a session that
unlocks nothing** — on the day they had already lost their authenticator, which is the only day this
route exists for. Every test passed, because a set with one wrapped-key row is exactly what the schema
asked for.

**Decision:** **a recovery factor is one secret, not one credential.** `factor_id` becomes the primary
key of `wrapped_account_keys` and `credential_id` becomes an ordinary, non-unique column. A passkey is
one factor and one row. A set of recovery codes is one credential and **ten** factors — ten rows, each
with its own client-minted factor identifier and its own pair of envelopes sealed under the
key-encryption key derived from *that* code.

`POST /api/me/recovery-codes` therefore carries ten **submissions** rather than ten verifier strings:
each is a verifier, a factor identifier, and two wrapped keys. The ten identifiers must differ, and
that rule lives in the handler rather than in the primary key — as a `23505` it would arrive after the
previous set had already been deleted inside the same transaction, and it would say "that factor
identifier is already registered" about a factor the client never registered.

**Alternatives considered.** *Wrap under a per-set key and wrap that key under each code* — the ten
wrapped set-keys need ten rows of their own, so it buys a second envelope kind and a second table for
nothing. *Hang the envelopes off `recovery_code_hashes`, which already has exactly ten rows per set* —
refused for the reason [ADR 0018](../decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
refuses it: that table is exempt from row-level security and holds a pinned column set, and a column
read only after redemption would become readable by every session. *Leave recovery codes carrying no
keys until a later story* — smaller, and it contradicts the requirement that names the recovery-code
branch by name.

**Consequences.** Nothing links a code's hash row to its wrapped row, deliberately: a client tries each
of the ten and exactly one opens, because the associated data binds each pair to its own factor. Twenty
AEAD attempts is a cost nobody can measure. And **redeeming a code deletes its hash row while leaving
its wrapped row standing** — consuming a code removes its ability to authenticate, never its ability to
decrypt, because the secret that opens the envelope is the code itself, written on a card this system
has never seen. Nothing is exposed that was not already: whoever holds a spent code and a copy of the
database could have decrypted with it before redeeming too.

**Affected areas:** [account-keys.md](account-keys.md), [recovery-codes.md](recovery-codes.md),
[ADR 0018](../decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).
This supersedes the entry below wherever the two disagree. That entry argues the shape correctly and
was written while "factor" was still assumed to mean "credential", so read every factor in it as one
**secret** — one passkey, or one code of a set — rather than as one `credentials` row.

---

## 2026-08-13 — The account keys are demanded by the server before any client can produce them

**Context:** an account owns one content key and one index key, and every recovery factor stores its
own wrapped copy of both. Building that meant deciding, for a set of things that are *deliberately
absent*, whether each absence is this story's or a later one's. Every one of them reads as an
oversight to whoever finds it next, which is why they are written down here rather than left to be
inferred from what is missing.

**Decision:** **the two write paths demand the wrapped keys now, and nothing in the browser can
produce them yet.** `POST /api/passkeys/registration` and `POST /api/me/recovery-codes` refuse a
request that carries no factor identifier and no pair of envelopes; the crypto that would mint them
lives in `+core/security/account-keys.ts` and **has no caller**, because this client cannot run a
WebAuthn ceremony and so cannot obtain a PRF output from a real authenticator. The only requests that
have ever satisfied the new requirement are the integration suite's.

That order is the deliberate part, and the alternative is what makes it worth recording: opening the
routes first and tightening them once a client existed would leave a window in which a factor could be
registered holding no share of the account's keys — a passkey that proves identity and unlocks
nothing, discovered by its owner months later when there is no way to reconstruct what it should have
held. Closing the write path first means that window never opens. The cost is a server stricter than
its own client, and it is paid up front.

**Four further absences, each this story's decision rather than an unfinished edge:**

- **No endpoint returns a wrapped key.** Nothing can unlock anything yet: there is no ceremony able to
  produce a key-encryption key and no ciphertext to read. An endpoint handing out wrapped keys is a
  real surface, and the story that needs one argues for it in place, where its own threat model can be
  stated.
- **The blind-index collision requirement is deferred.** The rule that one payee name entered in two
  sessions unlocked by two different passkeys must produce the same index value cannot be proved
  before a blind index exists. It is recorded as a deferral, not as a pass.
- **The PRF eval input is pinned and unwired.** Nothing derives from it, so its literal in the client
  spec is the only thing that would notice it drifting — and a drifted eval input locks every account
  out silently, because the key-encryption key it produces is simply a different key.
- **The `prf.enabled` gate stays, and stays a guess.** A reader seeing wrapped keys arrive on the same
  request will conclude the gate has been made real. It has not: the server cannot tell a
  key-encryption key derived from an authenticator's PRF output from one derived out of a constant, and
  no member it could be handed would let it. What the wrapped keys buy is smaller and real — both
  write paths refuse a registration that carries no share of the account keys, and there are only two
  write paths.

**Affected areas:** [account-keys.md](account-keys.md), [passkeys.md](passkeys.md),
[recovery-codes.md](recovery-codes.md),
[ADR 0018](../decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).

---

## 2026-08-11 — A regeneration opens a session exactly when its sweep ended one, and the rule is about the set

**Context:** replacing an account's recovery codes revokes the sessions the replaced set had opened and
then deletes that set's credential, whose cascade removes those rows outright. The entry below records
why the sweep must be explicit and why its count is reported. What neither it nor any test said is what
the *account* is left holding afterwards. The person this route exists for is very often signed in on
one of the swept rows: they lost the authenticator, redeemed a code, registered a replacement passkey,
and are regenerating the card while signed in on the session that redemption opened. Every assertion
about the path was about the **replaced credential** — that its sessions were stamped, and how many —
and all of them are true of a handler that hands somebody ten fresh codes and throws them out of the
flow in the same response.

**Decision:** **replacing a set that was carrying live sessions opens one session over the new set;
replacing a set that was carrying none opens nothing.** The condition is `sessionsEnded > 0`, the
session is `Full` and lasts the same 14 days the other two establishing paths give, it is derived from
the new set's own `Credential` rather than named, and it is written inside the same transaction and
stamped from the same instant as the sweep, the credential and the ten hash rows. The response gains a
nested `session` member, JSON `null` when nothing was opened and **never absent**: a member that
appears only sometimes makes *"the server did not tell me"* and *"the server told me no"* the same
observation for a client.

**The rule is stated about the *set*, not about the caller, and that is the deliberate part.** Nothing
on this request presents a session — the proof is a WebAuthn assertion — so the server cannot know
whose session it swept. It asks instead whether the set it replaced was carrying any, and a rule with
no referent is worse than a slightly generous one.

**The generosity runs in one direction only, and the asymmetry is chosen rather than tolerated.** No
false negatives: a live session over the replaced set is always swept, so anybody signed out here is
signed back in. Two false positives: a live session on another device, and a session unrevoked but past
its expiry, because the sweep narrows on `revoked_at_utc is null` and says nothing about expiry. Each
costs one inert row that hands nothing to anybody. Tightening it to a live-at-now reading is not
available as a local edit: the number is `RevokeSessionsForCredentialHandler`'s, and
`RevokePasskeyHandler` reports the same number through the same sweep while meaning only evidence by
it, so narrowing the condition is a change to what both paths count.

**What this does to the entry below:** *"Why the response reports `sessionsEnded`"* gave one reason —
the cascade erases the evidence, so the count is the only place the fact can live. There is now a
second, and it is the stronger of the two: **the number decides whether a row is written.** What it
counts is load-bearing twice over, which raises the cost of ever changing it.

**Say "thrown out of the flow", not "locked out".** The caller has just proved possession of a passkey
at the gate in front of this route, so a new session is always one assertion away. The defect was being
ejected from a flow at the worst possible moment, not losing the account, and the stronger word does not
survive a reader checking the code.

**Alternatives considered:**

- *Always establish, unconditionally* — one line shorter and it reads as the safe direction. A first
  issue is the most frequent call to this route, so it would put a sign-in nobody made in front of
  somebody who has just written a card down at onboarding: indistinguishable from a compromise, and
  revoking it does not undo having been told about it.
- *Never establish — the shipped behaviour* — defensible only while nothing presents a session. It is
  the defect: ten fresh codes and an immediate ejection, for the exact person the route is for, and the
  day a session token authenticates a request it becomes a sign-out in the middle of an account
  recovery.
- *Establish whenever there was a previous set to replace* — agrees with the chosen rule everywhere
  except on the account that generated a card, never redeemed a code, and is regenerating from a device
  signed in with its passkey. That person's session was opened by the passkey, the sweep never touches
  it, and a second one opened over the codes is a session nobody asked for on a credential they have
  not used.
- *Key it on the caller's own session* — the reading everybody wants, and it has no referent: no
  request on this route presents a session, so there is nothing to compare against. It becomes
  available the day a session token authenticates a request, and this rule is what it would replace.
- *Let the client ask — a member on the request, or a second call afterwards* — a member would be the
  first thing a caller supplies that decides what this route writes, on a route whose whole design
  keeps every identity value server-resolved. A follow-up call is worse: between the two the person is
  signed out, which is the window this rule exists to close, and it costs a second ceremony.
- *Stop sweeping, so the caller keeps the session they arrived on* — it does not work. The delete's
  cascade takes those rows whether the sweep ran or not, so the person is signed out anyway, and the
  only observable evidence that access ended deliberately is destroyed with it.

**Known gap, stated rather than hidden:** nothing presents a session yet, so neither half of this is
observable outside a test — the sweep signs nobody out and the re-establishment signs nobody in. Both
are anticipatory in the sense [sessions.md](sessions.md) already records for revocation, and the
re-established session is correct about the rows now so that the day a token authenticates a request,
a regeneration is already signing the person back in.

**Affected areas:** [recovery-codes.md](recovery-codes.md), [sessions.md](sessions.md).

---

## 2026-08-11 — The client mints every recovery code, and the hashes live on a table of their own

**Context:** an account had exactly one thing that could open its budget — a passkey — so losing the
authenticator meant losing the account, with no operator override and no escrow to fall back on. A
recovery code is the second secret the account holder already possesses. Two questions had to be
answered together, because answering one well and the other badly buys nothing: **where the secret is
created**, and **where its trace is stored**. They are one question here and not two, because a
recovery code is not a password. The account's key-encryption key is derived from the same code, so a
code that reaches the server is not a credential the server was going to check — it is the key.

**Decision:** the **browser mints each code and the server never sees one**. The client derives a
verifier `V = HKDF(code, …)` and sends only `V`; the server stores `SHA-256(V)`, unsalted, from the
BCL. The key-encryption key comes off the same code on an **independent HKDF branch**, so a database
reader holding the stored value can neither redeem — there is no preimage — nor derive a key. Those
hashes go on a table of their own, `recovery_code_hashes`, **exempt** from row-level security and
carrying a pinned column set, because a code is redeemed by an *anonymous* request: somebody redeeming
one has lost the authenticator that would have proved who they are, so the lookup by hash is what
establishes the identity, and a policy keyed on `app.current_user_id` would refuse the very query that
produces the value it wants to compare against — loudly, with `22P02`, on every redemption. One
`credentials` row stands for a whole **set**, never one per code, so redeeming one code deletes a row
while the set survives and replacing the set is a single delete the cascade carries the codes away
with.

**The consequence that has to be stated because nothing enforces it:** the server **cannot** enforce
the 128-bit entropy rule. It receives fixed-length opaque bytes, and ten identical zero-filled
verifiers are byte-indistinguishable here from a set a good generator produced. What it pins instead is
the whole list — the verifier's exact decoded width, the set size of ten, and distinctness within the
set. ADR 0002 requires the owning doc to say why a rule sits above its lowest capable layer; here the
answer is stronger, that **no layer at or below the API is capable of it at all**, in the same sense
the WebAuthn `prf` result is a claim the server cannot verify. The rule belongs to the browser that
mints the code, and that browser is not built — so today it has no enforcer anywhere.

**Alternatives considered:**

- *Mint the codes on the server and return them in the response* — the design every tutorial
  describes. The server would hold, for one request, the input to the key-encryption key's own
  derivation for an account whose keys it is otherwise structurally unable to read; one request log,
  crash dump or breakpoint makes the product's central promise false for that account, and false in a
  way nobody can detect afterwards.
- *Store `SHA-256(code)` rather than `SHA-256(V)`* — indistinguishable in the schema, in the width, and
  in every test that passes. It is not a storage choice at all: the code must cross the wire for the
  server to hash it, so it is the alternative above wearing a different hat.
- *Run Argon2 or PBKDF2 over the verifier* — the hardening a future reader reaches for first. A work
  factor makes a **guessable** input expensive to enumerate; the input here is a uniform 256-bit value,
  so there is no dictionary to slow down. It buys latency on a request that already holds the account,
  plus a package that would move a pinned row in the dependency-graph test and put a third-party
  dependency on `Domain`, which declares none.
- *Salt each row* — it makes the only lookup that matters impossible. A redemption arrives with no
  identity at all, so the row must be findable by its hash alone, and a per-row salt is a value the
  lookup cannot know before it has found the row it needs the salt to find.
- *Put the hashes on `credentials`* — refused by that table's pinned exemption column set, and it would
  make a credential row mean *one code* rather than *one way of signing in*, dissolving the
  one-set-per-account rule into something no partial unique index can express.
- *Put the hashes on `passkey_public_keys`* — the trap that exemption's own written reason names by
  name, having predicted "a recovery-code hash" as exactly the write-once secret that must not join it.
  Attractive precisely because the table already holds key material, is already exempt, and already
  holds no `UPDATE`, so an append-only argument passes without a murmur.
- *Police the new table and run the discovery read on an elevated connection* — the worst option on the
  list. It puts an elevated connection into the request path at the exact moment the request has proved
  nothing, and the elevated role is not subject to row-level security, so the policy it was added to
  satisfy would not apply to the only statement that reads the table. Coverage would report it policed
  and the protection would be zero.
- *Stamp a `redeemed_at_utc` instead of deleting the row* — it needs `UPDATE` on a table nothing
  beneath the application bounds, it makes every read carry a predicate the first forgetful one drops,
  and the stamp is a behavioural record about a person in a schema that keeps none.

**Known gap, stated rather than hidden:** three of them, and the first is the largest thing in the
story. **Generating a set requires a fresh passkey assertion, so somebody who has already lost their
authenticator can never generate one** — the feature protects only people who generated a set
beforehand. The gate is nevertheless right: with a stolen bearer token, an ungated regeneration would
mint a *persistent* factor that survives token rotation entirely. The consequence is a sequencing
requirement on the client — push generation at or near passkey registration — and that work belongs to
a later story. Second, **a redeemed code leaves no trace**, so *"was this code used, or never issued?"*
is unanswerable by anyone, which is the same trade erasure already makes against a deletion record.
Third, **no browser mints a code and no screen redeems one**. The server side is whole — the anonymous
lookup the exemption was written for is routed, the `DELETE` grant has its caller, and a redemption
establishes the `Full` session — but nothing anywhere presents a verifier, so both write paths are
reached only by a test. Neither is observable in any case, because nothing presents a session yet.

**Affected areas:** [recovery-codes.md](recovery-codes.md), [passkeys.md](passkeys.md),
[sessions.md](sessions.md), [users-and-ownership.md](users-and-ownership.md),
[erasure.md](erasure.md), [export.md](export.md).

---

## 2026-08-10 — "The last credential" means the last passkey, and a revoked passkey's sessions are reported rather than recorded

**Context:** a lost or compromised authenticator could only be dealt with by erasing the account. The
requirement is that a request removing an account's *last credential* be rejected, that revoking a
credential end every session it established, and that the account settings surface list every way of
signing in.

**Decision, and the reading that makes the rule mean anything:** "the last credential" is read as
**the last passkey**. Every account holds exactly one federated Google credential — the schema
enforces at most one, and provisioning creates it — so an account can never reach zero credentials of
any type while it exists, and a rule written literally would be unreachable and its test vacuous. The
floor that matters is the passkey: it is the only credential type that opens a session reaching budget
content, so an account left holding only its federated credential could still sign in, still could not
reach its own money, and could not even prove presence for an erasure. The **federated** credential is
therefore not revocable through this path at all; it is replaced rather than removed, by an email
change that is not built.

**Why the refusal is a 409 and the wrong target is a 404:** the last-passkey request is well-formed
and would succeed the moment a second passkey exists, which is a conflict with resource state rather
than a malformed request. An unknown credential, another account's credential, and the federated
credential all answer the same 404 **by construction** — one lookup carrying id, owner and type — and
not by a branch on the type, because a branch is a comparison a later refactor deletes while a missing
predicate changes what the database returns.

**The deliberate absence, recorded because it is the thing a future reader will try to fix.** A list
entry carries `id`, `type` and `createdAtUtc` and nothing else: no device name, no user-chosen label,
no last-used instant, no AAGUID, no transports. Two passkeys are told apart by the day they were
registered and by nothing better. That is not an oversight — a column on `credentials` breaks the
pinned exemption column set, whose own doctrine is *move the column, never widen the pin*; the AAGUID
arrives zeroed because the ceremony requests `attestation: "none"` precisely so registration collects
no device fingerprint; and a "last used" timestamp is a usage record sitting next to the `last_login`
that `ProhibitedColumnVocabulary` refuses, so it needs its own argument rather than a free ride on
this one. It has its own story.

**Why the response reports `sessionsEnded`.** Deleting a credential cascades its sessions away, so the
explicit revocation that must precede the delete leaves no trace in the schema — a test asserting "no
active session afterwards" is green with the revocation removed and therefore proves nothing. The
count is the only place the fact can live, and the port already argued for returning it: *a caller
that cannot say what a revocation did cannot report it.* That makes it a published contract; widening
it later is cheap and narrowing it is breaking.

**Alternatives considered:**

- *Read "last credential" literally* — the rule survives as text and dies as a check.
- *Refuse the federated credential with its own 409* — a better client message, bought by turning a
  by-construction property into a branch.
- *Answer 401 for the last passkey, matching every other refusal on the endpoint* — the uniform 401
  exists so an **unproven** caller learns nothing. Past the gate the caller has proved possession of an
  authenticator registered to this account, so there is nobody left to enumerate about, and a real
  sentence costs nothing. This is the argument `CompleteRegistrationHandler` already makes for itself.
- *A `revoked_at_utc` on `credentials` instead of a delete* — refused by the pinned column set, and it
  leaves a row a bug can bring back. See
  [ADR 0014](../decisions/0014-scope-the-credential-delete-in-the-application.md).

**Known gap, stated rather than hidden:** the passkey count and the delete are not serialized against
each other, so two concurrent revocations of an account's last two passkeys can leave it with zero.
Closing it needs a row lock whose raw-SQL spelling is a compile error here. Accepted, not solved.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md), [passkeys.md](passkeys.md),
[sessions.md](sessions.md).

---

## 2026-08-10 — `GET /api/me` returns the email address alone

**Context:** the account settings surface has to show the address the account is registered under,
and nothing in the product could tell it. The address is stored on `users`, but no endpoint returned
it: the only two places it surfaced were the export document — which drags a person's entire budget
along with it and refuses outright when the owned-budget set is not exactly the ambient one — and the
passkey registration options, which is a ceremony that mints and burns a challenge. The client cannot
read it out of the ID token either: `auth-service.spec.ts` pins that the client reads no claim, and
the stored address is deliberately never refreshed from the provider, so the token and the account
legitimately disagree.

**Decision:** a new authenticated read, `GET /api/me`, answering `{"email":"…"}` — one member, and
the response record carries no `Id` and no `CreatedAtUtc`. It reuses the existing
`IUserAccountReadService.FindEmailAsync` rather than adding a port method, and it carries no
`ProvisionsUser`, so it can refuse but never mint.

**Why the id is the expensive member, and the whole reason the shape is this narrow:** the client has
never seen an internal user id. Every tenancy value in this API is resolved server-side from the
authenticated subject and none is ever addressed by the caller — `BudgetRouteConstructionTests` holds
the route table to that, and `ExportDataQuery` and `EraseAccountCommand` each refuse a user-id member
for the same reason. Publishing one for a field nothing renders is the first half of a
client-supplied tenancy parameter: once a caller holds an id, the next request that accepts one has a
value to carry. Widening the response later is additive and costs a story; narrowing it is breaking.

**Alternatives considered:**

- *Return the whole `users` row* — the natural shape, and it already exists as `ExportedUser`, whose
  completeness is argued against the table's column inventory. A second shape of the same row on a
  display path is one that drifts from it, and it publishes the id for nothing.
- *Return `{id, email}`* — the id as a client-side cache key. Same cost as above for a benefit
  nothing has asked for; a cache key can be minted client-side.
- *Return a bare JSON string* — no room to add a second member without breaking every reader, on an
  endpoint whose whole design bet is that widening stays cheap.
- *Read the `email` claim from the ID token* — no backend work at all, and wrong: it shows what the
  provider asserts today rather than the address the account can be reached at, and it would need two
  shipped client pins relaxed to do it.

**The pin that guards it was watched fail.** `Me_ResponseCarriesTheEmailAndNothingElse` enumerates
the arriving members and joins them, so a widened record reports `"createdAtUtc, email"` rather than
that a count moved. It is green the day it was written — nothing else in either suite goes red when a
member is added — so it was proved against a deliberately widened record before being trusted.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-08 — The export's row order promises `CreatedAtUtc` and deliberately stops short of the tiebreaker

**Context:** the export orders every array by `CreatedAtUtc` then `Id`, and that pair was written down
as the read port's contract — the stated reason being that an id-only order would put a
database-backed implementation and an in-memory one into silent disagreement. Two independent reviews
pointed out the same thing: the tiebreaker reintroduces the very disagreement the contract claims to
remove. `uuid` collation is provider-defined — PostgreSQL compares sixteen bytes big-endian,
`Guid.CompareTo` compares fields — so two rows sharing an instant order differently through the read
service and through the in-memory fake, and neither is wrong.

**Decision:** narrow the contract to what is true. `CreatedAtUtc` ascending is promised. `Id` breaks
ties deterministically within one implementation and is explicitly outside the contract; two rows
sharing an instant may order either way across implementations. No behaviour changed — `.ThenBy(Id)`
stays on all six queries and in the fake.

**Why not make the tiebreaker provider-independent instead** (`.ThenBy(x => x.Id.ToString())`, or
comparing big-endian bytes): it would buy a guarantee for a state the product cannot currently
produce — nothing writes two rows of one collection from a single clock reading — at the price of a
sort that no longer uses the index and a rule whose reason nobody could reconstruct later. The honest
narrowing costs nothing and stops the doc from promising what no test holds.

**The gap it exposes is real and stays open:** the ordering test seeds instants minutes apart, so the
tiebreaker is never exercised. `.ThenBy(Id)` could be deleted from all six queries today without a
single test going red. That is acceptable precisely because it is outside the contract now — but
anyone tempted to promote it back into one owes the suite a test that seeds two rows sharing an
instant.

**Alternatives considered:**

- *Leave the contract as written* — it would keep claiming a cross-implementation agreement that no
  code delivers, and the first person to write a second implementation would find it out the hard way.
- *Drop the tiebreaker entirely* — determinism within one implementation is worth keeping; without it
  two exports of unchanged data could diff on a tie.

**Affected areas:** [export.md](export.md).

---

## 2026-08-08 — The export refuses rather than truncates when it cannot reach every budget the user owns

**Context:** the export has to hand back every budget a user owns. Both isolation layers beneath it —
the `BudgetIsolation` query filter and the `budget_isolation` policy — are scoped to the *ambient*
budget, and neither takes an argument. Today the question never arises: only provisioning creates
budgets, so a user owns exactly one and it is always the ambient one. The two sets coincide by
accident of what the product does not yet do, and nothing anywhere said what should happen when they
stop coinciding.

**Decision:** read the owned budgets scoped by `user_id`, and **throw** unless that set is exactly
the ambient budget — set equality, both directions. The export answers everything or it answers
nothing. The failure is a plain `500` from the catch-all handler, with no exception handler of its
own, so the only way to soften the answer is to change the throw.

**The set-equality direction that looks redundant is not.** A count-only guard passes the case where
the ambient budget is one the user does not own, and that case files one budget's rows under
another's id — a wrong document rather than a short one.

**Alternatives considered:**

- *Silently export the ambient budget* — this is the truncation the requirement forbids, and it would
  ship as a green feature. Every test would pass, the file would look complete, and the data loss
  would begin on the day a second budget becomes creatable, in a code path nobody would revisit
  because it had been working for a year.
- *Loop the ambient budget over each owned budget* — there is no mechanism. `IBudgetContext` is
  resolved once per request by the provisioning middleware, and `SessionContextInterceptor` writes
  `app.current_budget_id` at **connection open**, so re-pointing the ambient budget part-way through a
  request would need a fresh connection per budget. That is precisely the thing
  [ADR 0008](../decisions/0008-read-the-ambient-budget-inside-the-policy.md) and the
  connection-opened interceptor rule exist to prevent, and it would also make a single consistent
  read impossible.
- *`IgnoreQueryFilters`* — a compile error by `BannedSymbols.txt`, and the RLS policy underneath would
  return nothing anyway. Worth naming only because it is the first thing that comes to mind.
- *A dedicated `IExceptionHandler` mapping the refusal to a friendlier status* — rejected. `404` says
  the export does not exist, `400` blames a request with no field to correct, and `409` implies a
  resolution the client cannot perform. A named mapping is also the seam through which someone later
  turns the refusal into "return what we have, with a warning".

**Affected areas:** [export.md](export.md), [budgets.md](budgets.md).

---

## 2026-08-08 — The export document carries its own records rather than the ones the list endpoints return

**Context:** every entity the export names already has a DTO and a read service behind the list
endpoints. Reusing them is the obvious move, and the reviewer who finds two nearly-identical record
sets will reach for it.

**Decision:** the export gets its own records and its own read path. The display shapes are lossy for
an archive and are free to change with the screens that consume them.

**The losses are concrete, not theoretical.** `TransactionDto` coerces a null description to the
empty string, so "the person wrote nothing" becomes "the person wrote an empty string" and cannot be
told apart afterwards. `PayeeDto` carries neither `CreatedAtUtc` nor `BudgetId`. `AccountDto` adds
currency name and symbol, which no column holds. The read services also order for display — payees by
name — and a rename would then reshuffle the whole file, so two exports of unchanged data would diff.

**Alternatives considered:**

- *Reuse the DTOs and widen them* — every added field is one the list screens serialize on every
  request for no reader, and the coercion in `TransactionDto` cannot be removed without changing what
  those screens receive.
- *Project straight from the entities with no records at all* — the wire shape would then be whatever
  the domain classes happen to expose, and the schema version would stop meaning anything: a private
  setter added to an entity would silently change a documented file format.

**Affected areas:** [export.md](export.md), [transactions.md](transactions.md),
[payees.md](payees.md), [accounts.md](accounts.md).

---

## 2026-08-08 — The refusal to leave a remnant is its own vocabulary, and irreversibility is pinned on the route table

**Context:** "erasure leaves no remnant and offers no way back" was, until now, a rule the docs cited
as settled without ever stating it — two separate files referred to "the rule against tombstones" as
though it were written down somewhere, and it was not. Nothing stopped a `deleted_at` column or a
`POST /api/me/erasure/restore` route from being added, and both would have been read as ordinary
engineering by anyone who had not been in the room. There was already a schema-wide classifier
refusing column names — the one that keeps analytics and device identifiers out — and the obvious
move was to add a fifth category to it.

**Decision:** a **separate** vocabulary, sharing only the matcher, plus two route pins of different
shapes. The existing classifier's categories are documented as naming *what the schema is keeping
about a person*, and a `deleted_at` says nothing about a person — it says the row is still there. The
remedy differs too, and so does the file that owns the rule. What the two genuinely share is the
matching mechanism, which is now written once: two lists are two rules, but two tokenizers would be
one rule spelled twice, and the copy that quietly missed a fix would start disagreeing with the
original about names nobody was watching.

**The route pins are a corollary, not a second promise.** A restore path needs something to restore
from, so the schema rule is what makes irreversibility true; the pins only stop somebody building the
front half of a path whose back half cannot exist.

**Alternatives considered:**

- *A fifth category on the existing classifier* — fewer files, and it makes that classifier's own
  summary false. A tombstone red would also be reported under a heading named for data minimization,
  which tells the reader the wrong thing at the moment they most need the right one.
- *Copying the tokenizer into the new vocabulary* — rejected for the reason above: the drift is
  silent and shows up as one scanner catching a name the other misses.
- *Refusing the word `cancel` on the route table* — rejected. Cancelling something before it takes
  effect brings nothing back, so a pin refusing the word claims more than the rule does; and a pin
  that has to be fought in order to build a capability the requirements describe is a pin that gets
  deleted instead of extended.
- *Refusing `archived` alongside `archive`* — rejected. Hiding a closed account is a plausible
  live-row product state, and a rule that cannot tell it from a copy-aside would refuse the feature.
  What that costs is accepted rather than waved away: the pinned column sets reach three of the
  thirteen mapped tables, so a `transactions.is_archived` is refused by neither the pins nor the
  vocabulary. The account row is closed; the other ten are open on this axis by choice.
- *Documentation alone, with no test* — the requirement's own stated verification method is
  inspection, and inspection is a person remembering. Prose describing an absence rots quietly,
  because nothing goes red when it stops being true.
- *A second behavioural test proving no remnant row is written during an erasure* — already covered:
  the atomicity suite enumerates every relation that stores rows and asserts each is empty
  afterwards. A list-based duplicate would be strictly weaker than the discovery-based one that
  exists.
- *Leaving the vocabulary in the production assembly* — rejected. It is a test-only deny-list with
  two readers, both of them test projects, and `TestSupport` is where shared test-only code with more
  than one reader already lives. On the production side it was public API on the assembly the API
  container ships, sitting beside four files that genuinely run at deploy time. The token matcher
  stays behind because its own reader is production-side.
- *Refusing `discarded` was itself rejected at first, and that was wrong.* The argument was that
  *discard* is this product's word for an intentional hard delete, so a rule on it would red the
  correct behaviour and the wrong one alike. It does not hold: the classifier reads catalog and model
  *names*, and a hard delete leaves no column behind, so the behaviour the word describes correctly
  can never reach the classifier at all. Every `discarded_at` that does reach it is a soft delete
  wearing the product's own hard-delete word — the spelling a reviewer waves through.
- *Hand-written plural twins for each pattern* — rejected in favour of expanding the plural when the
  rules are compiled. Fourteen patterns become twenty-eight by hand and the fifteenth gets forgotten,
  which is exactly how `tombstones`, `archives` and `users_archives` passed a list that refused their
  singulars.
- *Refusing `recover` on the route table* — rejected on the same ground as `cancel`, from the
  opposite direction. In a passkey product *account recovery* means regaining access to a live
  account; a pin that cannot separate that from resurrecting an erased one reds a path this product
  needs, on every pass, and teaches the reader to ignore it.
- *Freezing the whole `/api/me` surface* — rejected once it was clear `/api/me` is the
  current-principal namespace rather than erasure's. The pin is scoped to the `/api/me/erasure`
  resource instead: a rule that argues with `GET /api/me` or an export route is a rule that gets
  widened by whoever meets it next, and a widened pin catches nothing.
- *Promising "no trace that it happened"* — withdrawn as written. What is held is a name scan and a
  row count, which between them reach the database and nothing else. Logs, traces and metrics are
  named as their own rule with their own narrow test, and the two nouns no name can catch — an
  anonymized remnant and a message payload — are credited to the row count rather than to the
  vocabulary. A promise wider than its gates is the failure mode this whole entry exists to avoid.

**Affected areas:** `erasure.md`.

---

## 2026-08-07 — "Every row unchanged" means the erasure's rows, not the request's

**Context:** a failed erasure must leave every row unchanged. Read as covering the whole request that
is false here, and not by accident: the re-authentication gate runs to completion *before* the
transaction opens, and it writes twice. `ConsumeAsync` deletes the spent nonce from
`webauthn_challenges`; `SaveCounterAsync` advances `passkey_signature_counters.signature_counter`.
Neither comes back with the rollback. Either the rule is scoped, or the gate moves inside the
transaction — there is no third position, and the two cannot both stand.

**Decision:** the rule is scoped to the erasure, and the scope is not chosen here — it is already in
the wording of the requirements themselves. Atomicity is stated as deleting every row **an erasure
covers** or none, and the guarantee is triggered by a failure in *part of an erasure*, where the gate
is the authorization deciding whether an erasure begins rather than a part of one. Accepting that a
failed erasure costs the person their assertion: they must repeat the ceremony before trying again.

**That the anchor is the requirement's own phrasing is load-bearing and not decoration.** Read as a
scope this codebase picked afterwards, the whole entry is the implementation excusing itself, and the
next reader would be right to distrust it.

**Alternatives considered:**

- *Move the gate inside the transaction* — the literal reading, and rejected twice over. A rolled-back
  erasure would restore the spent nonce, making the same assertion replayable; and the delegate is
  replayed by the retrying execution strategy, so a valid erasure would be refused with an attacker's
  error because the database blinked.
- *Gate inside, but consume the nonce on a second connection* — buys nothing, since the delete commits
  either way, and drags the counter advance inside, where a rollback destroys the clone-detection
  evidence. Strictly worse than doing nothing.
- *Only the counter advance inside* — the same defect. An accepted assertion has to mean the same
  thing on every path, and rewinding lets a cloned authenticator re-assert at a value already used.
- *Mark the nonce spent instead of deleting it* — a changed row is still a changed row, so it does not
  make the literal reading true, and it turns a self-cleaning table into a store of spent nonces.
- *Reissue a fresh nonce when the erasure fails* — creates a row rather than restoring one, and
  auto-minting proof of presence for a failed destructive request is a worse rule than asking the
  person to repeat a ceremony they can repeat.

**Affected areas:** `erasure.md`.

---

## 2026-08-07 — An account and its first budget stopped being two saves

**Context:** the user row and its first credential were written in one `SaveChanges` — an orphan user
holding a unique email no sign-in resolves to is unhealable — while the default budget went in a
second. That second save made "a user row with no budget" a reachable state, which is why a
find-or-create heal ran on **every** authenticated request. The heal cost a `SELECT` per request, and
it was a rule the next reader could delete as apparent duplication once it lived in two handlers: doing
so would leave a person whose budget insert was lost unable even to erase their account, with every
test still green.

**Decision:** all three rows are written in one save, through
`IUserRepository.TryAddAsync(User, Credential, Budget, …)`, and the heal is deleted. A resolved account
with no budget now throws. Chosen to remove the state rather than tolerate it — the atomicity argument
already made for the credential applies to the budget verbatim — accepting that the state is
unreachable *from the only path that creates a user* rather than unreachable outright.

**That distinction is the accepted cost and must not be read as an oversight.** Nothing in the schema
forbids the state, so a direct `DELETE FROM budgets` still produces it, and the account is then dead
rather than healed. The stronger guarantee is a participation constraint — a circular
`users.default_budget_id → budgets(id)`, `NOT NULL DEFERRABLE INITIALLY DEFERRED`, checked at commit —
which PostgreSQL can express declaratively and which would make the state unstorable. It was
**deliberately deferred as a separate decision**, because it puts a new column on a table whose column
set is deliberately pinned, and the pin's own rule is *move the column, never widen the pin*. The
present trade is bounded by production holding no data.

**Alternatives considered:**

- *`ITransactionalExecutor` over the three writes* — the named mechanism for "several writes in one
  handler must be atomic". Rejected, and at three rows the reason became correctness rather than cost:
  `BeginTransactionAsync` opens the connection, and that is when `SessionContextInterceptor` writes
  `app.current_user_id`. Inside a transaction the interceptor runs once, at the begin, so a wrap whose
  delegate contains the identity publication configures the connection while the setting is empty and
  the `users` INSERT fails `22P02` against its own `WITH CHECK`. Fixable by publishing outside the
  wrap, but the fix is a new ordering rule someone must not re-break.
- *A dedicated `IAccountProvisioning` port* — a third port for one call site, and it would carry the
  `23505` attribution away from the repository where every other constraint catch in this codebase
  lives. What it buys is a better name, and a name is cheaper to get from a method.
- *Modelling `Credential` inside the `User` aggregate* — already rejected before this change, for
  reasons unchanged: the root would then have to grow to hold sessions and passkeys too.
- *Keeping the heal as belt-and-braces* — rejected. With one save it repairs a state nothing produces,
  and an unconditional repair whose reason has evaporated is exactly the code a future reader deletes
  without understanding what it was for.

**A property the split save did not have, gained here.** When the credential insert loses a race, the
loser now wrote nothing and must adopt the winner's budget — and that budget is *guaranteed* to exist,
because a reported unique violation means the winner's transaction committed and that transaction
contained its budget row. Under the split save the winner could commit its user and lose its budget.
The loser must publish the winner's identity **before** reading its budget: `budgets` is policed by
`user_isolation`, so a read under the loser's phantom id matches nothing.

**Affected areas:** [budgets.md](budgets.md), [users-and-ownership.md](users-and-ownership.md),
[erasure.md](erasure.md).

---

## 2026-08-07 — Provisioning mints an account only where a route declares it may

**Context:** the entry below accepted that a second erasure request is refused, and weighed only the
status code the caller sees. It never asked what the *row* costs. `UserProvisioningMiddleware` minted
a full account — `users` with the email, `credentials` with the Google subject, a default `budgets` —
on any authenticated request whose credential did not resolve, and a Google ID token stays valid for
up to an hour after the account it names is erased. One in-flight poll or second tab therefore
resurrected the account moments after the person asked to be forgotten. Worse, the re-authentication
gate made that resurrection **unerasable**: the fresh account holds no passkey, so erasure refuses it
forever. The product's flagship claim is that leaving means leaving.

**Decision:** provisioning becomes create-on-demand, opt-in at the route group. A `ProvisionsUser`
marker on six data route groups is the only thing that permits minting; every other authenticated
route resolves an existing account or answers 401 and writes nothing. Chosen so the erasure path and
the identity-bearing routes beside it cannot write a row on the way past, accepting that the six
marked routes still resurrect an account while the token lives.

**Opt-in rather than opt-out, and the polarity is the fix.** Marking the routes that must *not* mint
leaves the mint set as "everything else", so a stale token resurrects the account through
`GET /api/transactions` — the original scenario, unfixed — and a new endpoint whose author forgets the
marker mints silently. Under opt-in a forgotten marker gives brand-new users a 401 on that group:
loud, caught by any integration test, and it writes nothing.

**Alternatives considered:**

- *Opt-out on the three routes that must not mint.* Rejected for the reason above. It is also thrown
  away rather than simplified the day registration becomes explicit.
- *A single registration route as the only minting endpoint, now.* The correct end state, and it is
  what a consented registration step will be. Rejected for today: it needs a frontend that has no
  passkey or erasure flow at all, and doing it half — a route the client calls on boot — buys nothing
  over the marker while pretending to.
- *A tombstone keyed on the Google subject, so a returning erased identity is recognised.* Rejected
  outright: retained personal data about a person who left is the regression restated.
- *Letting the erasure route run identity-less and answer 204 for "no account".* Tempting, because it
  restores idempotency and matches the endpoint's own post-condition philosophy. Rejected: it creates
  a path through the erasure handler that reports success having verified nothing.
- *Matching paths in the middleware instead of reading endpoint metadata.* Rejected: a route string in
  middleware is the second definition of the routing surface that
  [passkeys.md](passkeys.md) already argues against for the anonymous legs.

**What this does not fix, recorded so nobody reads it as settled:** a client calling a marked route on
app boot still resurrects an erased account within the token's remaining life. That is older than this
decision and closes when account creation becomes a consented act and the six markers collapse to one.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md), [erasure.md](erasure.md).

---

## 2026-08-07 — Erasure is gated by an assertion carried in the request, not by a stored re-authentication instant

**Context:** erasure had to stop being reachable on a bearer token alone. The requirement is stated in
two halves — a fresh WebAuthn assertion for a credential registered to the account, and a rejection
once more than five minutes have elapsed since that re-authentication — and the second half reads like
a two-step flow in which the server records when someone re-authenticated and checks that record
later. There is nowhere to record it: no session token is issued or presented, so the API has no
per-session state to hang it on, and `webauthn_challenges` cannot gain an owner column because its
pinned column set is exactly what holds its row-level-security exemption to its reason.

**Decision:** the assertion travels in the erasure request itself, and the five-minute window is
realised as the lifetime of the server-issued challenge it was built on — already five minutes,
already server-held, already enforced inside `ConsumeAsync`. Chosen to keep the elapsed check
server-side with no client-supplied instant to distrust and no new mutable per-user table, accepting
that the window is measured from challenge issue rather than from the authenticator touch. That
accepted tradeoff runs in the safe direction: the enforced gap is **shorter** than five minutes, so
the rule is stricter than the requirement rather than looser.

**A future reader will read the missing table as an oversight and try to add one. It is not.**

**Alternatives considered:**

- *A `reauthentications` table holding the instant.* Rejected. It is mutable per-user state on an
  account whose whole purpose is to be destroyable wholesale, it is the shape `app-role-grants.sql`
  argues against accumulating, and it would exist before sessions authenticate requests at all — so
  its owner column would have nothing to key on but the same bearer identity the gate exists to
  distrust.
- *A column on `webauthn_challenges` binding the nonce to a user.* Rejected, and it fails closed by
  design: `RowLevelSecurityCoverage.Exemptions` pins that table's five columns and the coverage test
  goes red on any addition. That pin is what pushed the account binding into the handler, which is
  where it belongs anyway.
- *A short-lived server-signed token minted by a separate re-authentication step.* Rejected: the
  product already refuses to hand the client a handle to a session for exactly this reason, and a
  second bearer artifact would be the thing somebody later decides is close enough to a session token.
- *Reusing the ordinary sign-in nonce pool.* Rejected — that pool is minted from an **anonymous**
  endpoint, so a phished ordinary sign-in assertion would destroy an account. A third ceremony value
  was added instead, and the registration pool is refused too because it is minted for an
  already-signed-in person, which is the stolen-session adversary itself.

**The account comes from the request here, and from the credential at sign-in.** Sign-in has no
identity yet, so the verified credential establishes one; erasure already has one, and publishing the
credential's account over it would be destructive rather than redundant, because
`SessionContextInterceptor` fixes the user and the budget together at connection open and a
re-published user id does not move the budget. Alice's bearer token with Bob's passkey would empty
Alice's budget while deleting Bob's user row. The binding is therefore an owner-scoped lookup rather
than a comparison, so another account's handle is indistinguishable from one nothing answers to.

**Erasure stopped being idempotent to the caller, and that was accepted rather than worked around.** A
second request authenticates as the brand-new account user provisioning just minted, which holds no
passkey, so it is refused. Answering `204` without a valid assertion would defeat the gate, and
storing a marker that an erasure happened would contradict the rule against tombstones. The cost — a
client retrying a lost response sees a failure over data that is already gone — is a client-side
concern.

**Affected areas:** [erasure.md](erasure.md), [passkeys.md](passkeys.md),
[data isolation](../engineering/data-isolation.md).

---

## 2026-08-06 — Erasure empties one table itself and leaves the rest of the graph to the cascade

**Context:** the entry below establishes that one `DELETE` on `users` empties the account's
structural graph, because PostgreSQL performs a referential action with the referencing table
owner's privileges. That is true of every edge that is a cascade. Five are not:
`transactions → budgets`, `→ accounts`, `→ categories`, `→ payees`, and
`categories → category_groups`.

**Decision:** erasure deletes explicitly **only what a `RESTRICT` edge would otherwise block**, in
dependency order, and leaves everything joined by `CASCADE` alone to the cascade — to get a rule a
future table can be measured against rather than a list of statements, accepting one extra
round-trip on an action that runs once per account.

Today exactly one table satisfies that rule: `transactions`, the child of four of the five edges.
Emptying it also disarms the fifth, because a `RESTRICT` edge cannot bite once its child rows are
gone. So the sequence is `transactions`, then the user row — two saves in one transaction, ordered
by the handler rather than by EF.

**`categories → category_groups` is deliberately left to the cascade**, and it was worth settling
rather than assuming. Both tables cascade from `budgets`, so one `budgets` delete reaches two tables
joined to each other by a `RESTRICT` edge, which looks like it should depend on which referential
trigger fires first. It does not: PostgreSQL queues the check for that edge as an after-row trigger
when the `category_groups` row is deleted, strictly after the cascade into `categories` was already
queued, and the after-trigger queue is FIFO. Verified on PostgreSQL 17 against schemas built with
the two constraints created in either order, so their OIDs — and with them the RI trigger names that
decide firing order — were reversed. Both leave the tables empty.

**Alternatives considered:**

- *Delete `categories` explicitly as well.* This is what shipped first, on the belief that the
  cascade rested on constraint-creation order. Removed once that was disproved: it bought nothing,
  and its unit test asserted a refusal PostgreSQL does not make, so it could not fail.
- *Trust the cascade for the `transactions` edges too.* Rejected outright: a budgeting product's
  accounts hold transactions, so `23503` would be the ordinary case rather than the corner.
- *Migrate `budgets → transactions` to `ON DELETE CASCADE`.* Rejected twice over. It dissolves the
  guard that edge exists for — an ordinary budget delete would then silently take recorded money
  movement with it — and it does not even buy the one statement it appears to: `transactions →
  accounts` and `→ payees` stay `RESTRICT`, so a `budgets` delete would still be cascading into
  tables joined to each other by a `RESTRICT` edge, in the opposite direction to the one settled
  above.
- *One `SaveChanges` for the whole batch.* Rejected: EF orders a batch topologically by the foreign
  keys between the entity types **in** it, and `Budget` — the type carrying the edge — is never in
  the tracker. No edge means no guarantee, and a draw that puts the user delete first is refused.
- *Relax `BannedSymbols.txt` to allow `ExecuteDelete` here.* Rejected. The ban's value is that it has
  no exceptions, and erasure is the highest-consequence write in the product — a set-based delete
  with a wrong predicate is the last statement that should be unreadable by the type system.

**Read the absent `budgets` DELETE grant as a decision too.** Removing the tracked `Budget` from the
context is what stops EF composing its own `DELETE FROM budgets`; without it the request dies with
`42501`, an error that names a permission while the cause is the change tracker. Answering that with
a grant would widen the role's reach and fail the grant-matrix pin.

**Affected areas:** erasure (new), users-and-ownership, transactions, categories, budgets.

---

## 2026-08-06 — Erasure needs one delete grant, not one per table

**Context:** erasing an account has to run as the least-privilege application role rather than on an
elevated connection, so the role needs to be able to delete every user- and budget-owned row. The
obvious reading is a `DELETE` grant per owned table — seven of them, on `users`, `budgets`, `payees`,
`credentials`, `sessions`, `passkey_public_keys` and `passkey_signature_counters`.

**Decision:** grant `DELETE` on **`users` alone**. Every owned table hangs off that row by
`ON DELETE CASCADE`, and PostgreSQL performs a referential action through internal triggers running
with the privileges of the **referencing table's owner** rather than of the role that issued the
statement — so one grant empties the structural graph and a grant on any child buys nothing erasure
can use. This is not a belief the code rests on:
`AppRoleGrantsTests.Database_AllowsDeletingAUserAndCascadesTheAccountAway` deletes as the application
role and asserts every child table is empty afterwards.

**It is not sufficient on its own.** `budgets → transactions` is `Restrict`, so a budget holding one
recorded movement refuses the delete — the ordinary case for a budgeting product, not the corner.
Erasure deletes transactions first, per budget, and only then the user row; those are two shapes of
session, because `transactions` is policed on the budget and this delete on the user.

**Alternatives considered:** *all seven* — rejected, and not merely as surplus. `credentials` and
`passkey_public_keys` are exempt from row-level security because they are read before a request has
an identity a policy could key on, so a `DELETE` there would be **unpoliced**: one statement carrying
the wrong id removes somebody else's only way in, with nothing to catch it. On
`passkey_signature_counters` a `DELETE` reopens counter rewind — deleting the row and re-inserting it
at zero is exactly what the single-column `GRANT UPDATE (signature_counter)` exists to forbid, and a
clone giving itself away against that counter is the reason the column is there. *The four policed
tables only* (`users`, `budgets`, `payees`, `sessions`) — rejected: the three extra grants still do
nothing the cascade does not already do, and each would invert a written argument for no gain.

**Read the six absences as a decision.** They look like an oversight, and the obvious "fix" for a
future reader is to close them; the grant paragraph in `app-role-grants.sql` and the rule in
[users-and-ownership.md](users-and-ownership.md) both say why they must not be.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md), [passkeys.md](passkeys.md),
[sessions.md](sessions.md).

---

## 2026-08-06 — Registration is refused on an unverifiable claim, and the docs say so

**Context:** an authenticator that cannot derive a PRF secret cannot hold the account's keys, and the
person holding it finds out by losing their records permanently. The only signal the product has is
`prf.enabled` in the client extension results — asserted by the client, covered by no signature, and
impossible for the server to reproduce or check. Two earlier documents deferred the question rather
than answer it: what may a claim like that be allowed to gate?

**Decision:** gate registration on it, and record in the same breath that this is a **product gate,
not a security control**, so no later reader mistakes it for a guarantee. The justification is
narrower than "defence in depth": there is no adversary here. The claim is the account holder's own
browser describing the account holder's own authenticator, and whoever forges it registers a passkey
whose keys they will not be able to derive — harming only themselves. That places it exactly where
ADR 0002 puts an upper-layer restatement: it exists for error quality, never for enforcement.
Enforcement of key custody arrives with the keys themselves, where a wrapped key under a nonexistent
PRF output simply cannot be produced.

Two consequences worth keeping: the check runs **last**, after signature verification, so a
malformed or replayed response is never told the lie that its authenticator is at fault; and the 201
response stops echoing the flag, because a value the *server* returns reads as a value the server
established.

**Alternatives considered:** *refuse nothing until key custody exists* — correct on the letter, and
it leaves every passkey registered in the meantime to fail silently at the moment it is needed most.
*Put the check with the ceremony verification code* — rejected: everything there judges signed
material, and a verification failure meaning "your client said no" would blur that boundary for
every later reader. *Keep reporting the flag in the response body* — rejected: once `true` is the
only reachable value it carries no information, and echoing it invites the exact misreading this
entry exists to prevent.

**Affected areas:** [passkeys.md](passkeys.md), [_overview.md](_overview.md) (glossary),
[ADR 0013](../decisions/0013-verify-webauthn-ceremonies-without-a-fido-library.md).

---

## 2026-08-05 — A passkey's material is split by whether it is read before or after identity

**Context:** signing in with a passkey means finding a public key and checking a signature *before*
the server knows whose account this is. Until that moment every statement runs with
`app.current_user_id` empty, so a policed table would refuse the very query that establishes the
identity. The question was which of a passkey's facts have to live on a table exempt from row-level
security, and which must not.

**Decision:** the line is drawn at **before proof / after proof**, and it is drawn as a *table
boundary* rather than a convention. `passkey_public_keys` — credential id, public key, algorithm — is
exempt, because all of it is read by the discovery lookup. `passkey_signature_counters` carries
`user_id` and is policed, because the specification compares a counter only after the signature
verifies. `credentials` grows no column at all, which also leaves its frozen constraint snapshot and
its pinned exemption untouched.

What holds the exempt table to its reason is its **pinned column set**, not the absent `UPDATE` and
`DELETE` grants. Those stop mutable per-user state accumulating, which is real but is not the
threat: a wrapped key or a recovery-code hash is written once and never updated, so it satisfies any
append-only rule perfectly while being exactly what must not sit on a table every session reads in
full. A red on the pin means move the column, never widen the pin.

Four candidate columns were **refused outright** rather than relocated — AAGUID, transports, a
last-used instant, and backup-eligibility flags. Nothing in this design reads any of them, and an
authenticator model identifier is a device fingerprint by another name. Each arrives with the feature
that reads it.

**Consequence to carry:** the assertion path must publish the identity only after the signature
verifies, and must not open a transaction before that publication — a transaction opened earlier
configures the connection while the identity is still empty, and every policed statement inside it
fails with `22P02`. The whole ceremony runs over the real least-privilege connection in an
integration test for exactly this reason.

Recorded as [ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md);
the verification decisions that go with it are [ADR 0013](../decisions/0013-verify-webauthn-ceremonies-without-a-fido-library.md).

---

## 2026-08-05 — A session is revoked by writing an instant, not by deleting the row

**Context:** the product now records a sign-in server-side so it can end one without asking an
identity provider. That needs a decision on what "ended" is: a `revoked_at_utc` an application role
may write, or a row it may delete.

**Decision:** revocation is an **`UPDATE` of `revoked_at_utc`**, and the application role holds
**no `DELETE` grant on `sessions` at all**. The row is what says access ended and when; deleting it
needs the one privilege that could erase every session on the system, and it cannot tell "already
revoked" from "never existed" — a distinction anything reporting a revocation needs. `Session.Revoke`
is idempotent, so a retried sweep keeps the instant access actually ended rather than restamping it,
and the `ExecuteUpdate` ban is what keeps the transition running in the domain per row where that
idempotence lives.

Alongside it: the foreign key from a session to its credential is **`CASCADE`, not `RESTRICT`**, so
a session can never hold up the deletion of a credential and through it an account erasure — a row
of access bookkeeping must not outrank a person's request to be forgotten. The cost is named rather
than hidden: because the cascade exists, deleting a credential row satisfies "revoking a credential
ends its sessions" invisibly, so any credential-removal path must revoke explicitly and then delete
or the fact is unobservable.

**Alternatives considered:** *Revocation by `DELETE`* — rejected above. The usual argument for it,
that updated rows accumulate, does not separate the two options: an unrevoked but expired row
accumulates identically, so retention is a problem either mechanism has and neither solves. No sweep
exists yet, and the grant one would need is the grant this decision withholds. *A `bool` on the
session saying whether it may read budget content* — rejected in favour of a derived `kind`: a
boolean named after a permission reads as a permission a caller sets, and the kind is derived from
the establishing credential's type with no way to supply one.

**Affected areas:** [sessions.md](sessions.md), [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The deny-list refuses a bare word only where no honest column can carry it

**Context:** the vocabulary landed with `event` as a bare token, and the names most likely to arrive
next are spelled with the same words a tracking column is. A transactional outbox carries
`event_type`; revocable sessions carry `session_id` and `last_used_at`; a passkey record carries
`credential_id` and `device_name`. Each of those is a security or correctness record, and a rule
that cannot tell one from a measurement column refuses the feature along with the surveillance.

**Decision:** the vocabulary refuses a bare token only where no legitimate column in this domain can
carry it — `analytics`, `advertising`, `fingerprint`, `telemetry`, `tracking`, `utm`, `impression`,
a vendor's name — and a phrase everywhere else. Four omissions are therefore deliberate and each
looks like an oversight: **`event`** protects the outbox, **`session`** protects a revocable
session, **`login`** protects a session's own start time, and **`cookie`** protects a first-party
opaque value. The general rule: when a word cannot separate the surveillance case from the security
case, the pattern narrows, because a red bar on a legitimate column is spent credibility and a
missed column is still caught by review. The compensating move goes the other way — the scan now
classifies **relation names** as well as column names, so scope rises a level even as individual
patterns get narrower. A behavioural feature is modelled as a table far more often than as a column.

**Alternatives considered:** *Refuse the bare tokens and carve the exceptions out in each rule's
`Reason`* — rejected: prose is read after a red bar and can only help argue one away, never stop it,
and the vocabulary's own remarks say the check is worth having only while its reds are believed.
*Keep a per-table exemption list the way the row-level security coverage check does* — rejected: an
exemption list is a thing people append to, and appending is the drift this rule exists to stop; a
narrower pattern has no append surface. *Defer until the sessions story lands* — rejected: the
collision would then be discovered as a red bar on somebody else's work, which is the worst moment
to be arguing about a deny-list. *Keep `analytics_event` as a `BehaviouralEvent` rule* — rejected:
it can never fire, because `analytics` matches first whatever the order, and a rule that cannot
fire makes the list's claim to disjoint patterns false.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The list of columns the product refuses to carry is production code, not a test constant

**Context:** "the account row holds nothing but an id, an address and a timestamp" is worth little if
the same data arrives one table over. Refusing analytics identifiers, advertising identifiers, device
fingerprints and behavioural events needs a list of what those look like, and the cheapest place to
put a list read by exactly one assertion is inside that assertion. It would not stay read by one
assertion: the data inventory that classifies every column will have to read the same list from a
build gate, and a build gate cannot reference a test assembly.

**Decision:** `ProhibitedColumnVocabulary` lives in the production assembly beside
`RowLevelSecurityCoverage`, which is the same shape of object for the same reason — a classifier
placed where more than one consumer can reach it so the rule has one spelling. It exposes the rules
with a stated reason each, and a `Classify` that names the category a column name falls into.
Matching is over `_`-separated tokens rather than substrings, because `event_name` must be caught
while `name` must not. It is checked over **every** relation in the `public` schema rather than over
`users`, so a new table is covered without anyone remembering. Two controls make the pair mean
something: a probe table proves the scan can fail, and a false-positive test over the mapped columns
proves the patterns are not wide enough to swallow a real one.

**Alternatives considered:** *A constant in the schema test* — rejected: it is a source the inventory
story is guaranteed to duplicate or move, and a follow-up commit that relocates a rule's definition is
the kind of commit that quietly changes it. *Wire it into the deploy-time verifier beside the
row-level security check* — rejected: that check runs at deploy time because a policy fails **open**
and can drift from outside the repository. A column name cannot; it arrives through a migration CI
already reads, so the check buys nothing there and adds a failure mode to the deploy path.
*Enforce it in the database* — rejected: PostgreSQL cannot refuse a column for what its name
connotes, and reaching it would need an event trigger, which ADR 0002 rules out. The build is the
lowest capable layer, and a rule sitting above its apparent floor has to say why.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The provider must vouch for the address, and a takeover path closes with it

**Context:** the API read `sub` and `email` off the principal and trusted both. An `email` claim the
provider has not vouched for is an address anybody could have typed into a profile field, and the
account row is keyed on exactly that address — it is unique, it is what a support request, an export
and every future notification go on. "One email, one user" below is explicit that this was a known
hole: it rejected rebinding a stale row's subject on collision partly *because* `email_verified` was
not read, so an unverified address in a token would have been enough to reach an existing account.
That premise no longer holds. Two other statements in that entry are also no longer true of the
schema: the `google_subject` column it bounds moved to `credentials.subject`, and the `display_name`
column it bounds was dropped outright. Neither change touches the decision that entry records — the
uniqueness and case-insensitivity of `users.email` — which stands.

**Decision:** require `email_verified` on every authenticated principal, checked in
`UserProvisioningMiddleware` immediately after the missing-claim check and **before** provisioning, so
a refused principal writes no row. Accept only a value `bool.TryParse` reads as `true`, which is
case-insensitive and therefore takes `"True"`; reject absent, blank, `"false"` and anything
unparseable, `"1"` included. No provider this codebase talks to emits a truthy-string dialect, and
accepting one is how a gate quietly stops being a gate. Rejection is a 401 ProblemDetails of the same
shape as the missing-claim path but with its own title, because the caller holds the token and can
read the claim themselves — naming the reason leaks nothing and saves a debugging session. The claim
is read and **not stored**: it decides whether the address may be registered, and answers nothing
about the person worth keeping afterwards. No scope changes; Google returns `email_verified` under
the `email` scope the client already asks for.

**Alternatives considered:** *Enforce it inside JWT validation* — rejected: the integration host
replaces the whole authentication stack, so the rule would ship with no test exercising it, and it
conflates "is this token authentic" with "may this address be registered", which is a provisioning
precondition of the same kind as "the principal carries a `sub`". *Carry the flag on
`EnsureUserCommand`* — rejected: an authentication concern in the Application layer, and a command
field nothing stores. *Enforce it in the database* — rejected: the database cannot inspect a token,
and reaching it would need procedural logic, which ADR 0002 rules out; the API boundary is the lowest
layer capable of the rule. *Revisit rebinding a stale row's subject now that the claim is checked* —
rejected: that rejection rests on a second, independent ground — `sub` must not be mutable — and this
change does not reopen it.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-03 — The client stops reading the identity token, and the greeting goes with it

**Context:** the entry below removed the stored name but left the client's home-screen greeting
alone, on the argument that a name rendered from the ID token and never sent to the API is not
something the product *stores*. That reasoning holds, and it is not why this changes. The owner
decided the greeting is not worth what it costs: it was the **only** thing on the client reading
any ID-token claim, and so the only reason to ask Google for the `profile` scope at all.

**Decision:** remove the greeting, and with it everything that existed to serve it — the NgRx
`profile` slice, the `userProfileInformation` effect, `AuthService.userProfile$`, `+common/guid.ts`
and the `immer` dependency. The authorization request narrows to `openid email`. `AuthService` now
reads no claim from the token whatsoever; the token is a bearer credential and nothing else.

**A route moved as a consequence, not as an intention.** The greeting was the entire content of
`/app/home`, so removing it would have left the post-sign-in landing screen blank. `/app` now lands
on `/app/transactions` — the screen that actually shows something — and the `home` route is gone
rather than kept as an empty shell waiting for a dashboard nobody has designed.

**On the invariant that was guarding this:** `auth-service.spec.ts` pinned FR-086 ("no image
supplied by the identity provider is displayed") at the claims-mapping boundary. Deleting the
mapping would have deleted the guard, so it was replaced by a stronger one — that no observable
`AuthService` exposes reads *any* ID-token claim. "Drops the picture" is a special case of that.
`no-profile-scope.spec.ts` reads the **built bundle** and asserts the shipped config requests
`openid` and `email` and nothing else; it is an allow-list rather than a `profile` deny-list, so a
scope nobody vetted fails it too. Pinning the artifact rather than a test fixture is the point —
the fixture proves nothing about what deploys.

---

## 2026-08-03 — The account keeps the address and nothing else the provider says

**Context:** `users.display_name` was written from the Google `name` claim on every sign-in and read
by **nothing** — no endpoint returns it, no serialiser touches it, and the client's home-screen
greeting renders the claim straight from the ID token without ever asking the API. So the column
was a copy of somebody's real name, kept indefinitely, protected at every layer, included in any
future export and any future breach, serving no purpose that could be named out loud. It arrived
because the claim was in the token, which is the worst reason to store anything.

**Decision:** the user row holds an internal id, an email address, and a creation timestamp. Of
what the identity provider asserts, only the address is kept — it is the one channel by which the
product can reach its user and the reason one provider account maps to one account here. The
`name` claim is no longer read at all. `User.Create` loses its multi-field validation aggregation
along with the second field, because aggregating one field's errors is machinery with nothing to do.

**Why the column and not just the write:** leaving it nullable and unwritten would keep every row
that already has a value, so the data would still be there to leak and still have to be erased. A
column nobody writes is not minimization, it is a slower version of the same exposure.

**What did not change, and is worth knowing:** the greeting on the home screen still shows the
person's name. It comes from the ID token into client-side state and never crosses the API, so it
is not something the product stores. When sign-in stops carrying an ID token on every request that
feature loses its source and gets decided then, on its own merits, rather than being quietly
removed here as a side effect of a storage decision.

---

## 2026-08-03 — The provider is not consulted about an account after it exists

**Context:** provisioning re-read the Google ID token's `email` and `name` claims on **every**
authenticated request and wrote back anything that differed. That made the provider a standing
authority over the account: whatever it reported today became what the account held today. It also
meant the one channel by which the product can reach its user could move without the user doing
anything, and that the provider was consulted continuously rather than once. "One email, one user"
(2026-07-28) and the entry below both assumed that refresh existed — the email was described there
as a *cached copy of an attribute the identity provider owns*, and this entry is what retires that
description.

**Decision:** the provider vouches for a person **once**, when the account is created, and is not
asked again. `EnsureUserHandler`'s existing-user branch resolves the account and returns; it
performs no write at all. Changing the stored address becomes its own operation, requiring its own
fresh authorization exchange, rather than a side effect of signing in. `User.UpdateProfile` and
`UserRepository.UpdateProfileAsync` are deleted rather than left unused — an unused write path is
one somebody restores.

**Consequence, accepted rather than worked around:** the stored address **freezes** at whatever
registration captured, and there is no way to change it until the gated exchange is built. A user
whose provider address changes keeps the old one on file. That is worse than a stale-free refresh
in exactly one respect and better in three: nothing moves the account's reachable address without
the user asking, the provider stops observing every sign-in, and a compromised provider account no
longer silently rewrites what the product knows about its owner.

**What this deletes, so nobody looks for it:** the asymmetry where an email collision was a 409 on
the insert path and *ignored* on the refresh path. There is now one path on which a collision can
occur, and it fails closed. The reasoning for the asymmetry was sound while two paths existed; it
is not a rule that was wrong, it is a rule whose second half no longer has a subject.

**Alternatives considered:** *Refresh only when the address is unclaimed, and warn otherwise* —
rejected: it keeps the provider as standing authority and only changes what happens on the rare
conflict. *Keep the refresh until the gated change ships, so no address ever goes stale* — rejected:
the refresh is not a stopgap for the missing feature, it is the behaviour the feature exists to
replace, and shipping the replacement is not made easier by leaving the thing it replaces running.

---

## 2026-08-03 — A credential is a typed row, and the account holds no identity key of its own

**Context:** "One email, one user" (2026-07-28) and the entries around it treat `users.google_subject`
as the identity anchor: one column, one provider, one way in. That shape can hold exactly one sign-in
method per account, so a second authenticator or a provider-free sign-in has nowhere to live, and it
puts an identifier an external party issues in the position of *defining* the account rather than
merely reaching it.

**Decision:** Move every way of signing in into a **`credentials` table**, each row carrying exactly
one type — `federated` (a provider vouches, naming its `provider` and the `subject` it issued) or
`passkey` (the authenticator holds it, so neither column applies). The user row keeps only its
internal id, its cached email and display name, and its creation timestamp; `google_subject` ceases
to exist. `EnsureUserHandler` resolves a principal by `(provider, subject)` and adopts a race
winner's *user* rather than its row. An account may hold more than one credential — nothing today
registers a second, but the schema no longer refuses one.

Three choices inside that, each the reason a later reader might otherwise "simplify" it back:

- **One table with CHECK-enforced shape**, not table-per-type and not a discriminated hierarchy.
  `CK_credentials_type` bounds the vocabulary; `CK_credentials_type_shape` says federated rows carry
  provider and subject while passkey rows carry neither, which is what makes "exactly one type" a
  database rule instead of a convention. Passkey columns arrive later as nullable additions and one
  widened arm.
- **The unique index over `(provider, subject)` is partial**, filtered to federated rows. A plain
  unique index would also work — `NULL`s are distinct — but by accident rather than by statement.
- **The user row and its first credential are written in one save.** A refused insert must leave
  neither behind: an orphaned user row would hold its unique email while no credential resolved to
  it, and every later sign-in with that address would 409 with no path back. Unlike a missing default
  budget, nothing heals it on the next request.

**Alternatives considered:** *Keep `google_subject` and add a second nullable column per future
method* — rejected: it re-decides the same thing at every new method and cannot express "more than
one of the same kind". *Give credentials their own repository now* — rejected as premature: the only
consumer is provisioning, and the atomic two-row insert wants one `DbContext` and one save; the
question reopens when listing and revoking arrive. *Deciding the insert outcome from the reported
constraint name* — still rejected, for the reason the 2026-07-28 entry gives, and now with an extra
edge: the user row is written before its credential, so a self-race reports the email index.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md),
[ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md) (the grant matrix gains a table
with no `UPDATE` and no `DELETE` grant of any shape).

---

## 2026-07-29 — A payee's name is correctable, and the payee list gains no other write

**Context:** "Payees are free-text find-or-create, not a managed list" (2026-07-13) below chose the
lighter of two designs — a payee comes into existence as a side effect of naming a counterparty on a
transaction, with no create/edit/delete payee UI or endpoints — and accepted in as many words that
payees "can't be renamed or pruned directly". That acceptance carried two consequences, and the entry
priced only one of them. It named the near-duplicate problem, which case-insensitive matching
partially answers. It did not name what happens to a name typed wrong: a misspelling entered once was
permanent, and self-propagating besides, because correcting it on the transaction does not undo it —
the corrected spelling runs the same find-or-create and mints a *second* payee, so the fix leaves the
budget holding both spellings with the wrong one still offered by autocomplete. The name is also the
entirety of a payee: there is no other field a person could tell two apart by, and therefore no other
field they can be wrong about. A list a user can only ever add to, in which the single field that
identifies a row is uncorrectable and a mistake reproduces itself when fixed, is not the lightness
the original decision was buying.

**Decision:** Ship `PATCH /api/payees/{id:guid}` — 204 on success; 404 for an unknown id and for one
belonging to another budget alike, on the standing tenancy rule; 400 for a name another payee in the
budget already holds — so that **the payee list gains exactly one write operation, rename, and no
others.** The name is **required** rather than a three-state optional field like the six on
`PATCH /api/transactions/{id:guid}` ("A recorded transaction is corrected in place, and silence is not
an empty value", 2026-07-29 above): a payee has exactly one mutable field, so a body that omits it
asks for nothing, and there is no second field whose silence would need a meaning. `BudgetId` stays
immutable — moving a payee between pools is the tenancy rule, not an edit. Three operations are
deliberately still absent, and each for its own reason. **No create**: a counterparty nobody has
transacted with is not a fact about the budget worth storing, and making the user register one first
puts a second task in front of the entry the original decision exists to keep fast — find-or-create
stays the only way a payee comes into being. **No delete**: the composite `transactions → payees`
foreign key is `Restrict` and the schema refuses while any transaction references the row, which is
the same protection of recorded history the account and category guards give; a delete that could only
ever succeed for rows nobody has used is not an operation worth an endpoint. **And no merge** —
repointing every transaction from one payee onto another and removing the emptied row — even though
merge, not rename, is what would actually clean up a duplicated list. Rename fixes a misspelling; it
fixes nothing about two spellings that both already carry transactions, since the second cannot take
the first's name (the unique index refuses it) and nothing moves the transactions. Merge is a real
feature rather than an increment on this one: it rewrites references across a table, it needs an
answer for which name survives, and it is the only payee operation that could not be undone. The
accepted consequence of the rename is that **it rewrites the counterparty shown on every past
transaction** — `TransactionDto` resolves the payee name at read time, so the correction is
retroactive by construction and costs no fan-out write. That is intended, and it is what separates a
meaningful rename from a cosmetic one: the row stands for one real-world party over its whole life,
so its history is not a set of independently spelled events. It does not contradict the payee delete
refusal stated above, or "Money movement is discarded only by explicit intent, never as a side
effect" (2026-07-29 below), both of which take the opposite stance on letting history change: the
schema will not let a payee be erased from transactions that already happened, while a rename changes
what every one of them displays. The two positions reconcile on one distinction: **a name is a
mutable label on a stable identity, while a transaction is the record of an event.** Relabelling the
party alters nothing about what happened.

**Alternatives considered:** *Leave payees unwritable and let correction happen on the transaction* —
rejected: that is the status quo the context argues against, and it is worse than doing nothing at
all, because the act that looks like a correction adds a row instead of repairing one. *A full payee
CRUD with its own managed list* — rejected for exactly the reasons 2026-07-13 rejected it, which this
decision leaves standing: creating a payee ahead of the transaction slows the entry, and a managed
list is heavier than a personal budgeting app needs. Rename is the one operation whose absence was a
defect rather than a simplification. *Refuse a duplicate name with 409 instead of 400* — rejected: a
taken name is a statement about a field of the request, which is what a validation problem document
carries and what a bare conflict status has nowhere to put, and `AccountRepository` already answers
400 for a duplicate account name, so a second status for the same class of mistake would only make
clients branch on which entity they were editing. *Precheck the new name with a lookup and report the
duplicate from the application* — rejected twice over: it is check-then-act, so the unique index has
to catch the loser of a race regardless and the catch it was meant to replace cannot be removed; and
"does a payee with this name exist?" answers yes for the row being renamed, which would refuse
`"starbucks"` → `"Starbucks"` — the commonest real use of the feature, since a payee is minted with
whatever casing was typed mid-entry. *Make the rename non-retroactive by snapshotting the name onto
each transaction as it is recorded* — rejected: it turns a rename into a fan-out write across
history, and until that write finishes, or if it is never run at all, the same counterparty reads two
different ways on two different screens.

**Affected areas:** [payees.md](payees.md), [_overview.md](_overview.md). This amends "Payees are
free-text find-or-create, not a managed list" (2026-07-13) below rather than reversing it: the
find-or-create creation path, the absence of a create endpoint, the absence of a delete and the
case-insensitive near-duplicate answer all stand — only the "can't be renamed" half is reversed, and
"can't be pruned" is untouched. [transactions.md](transactions.md) needs nothing: the payee is still
supplied by name on both transaction write paths, and no rule about a transaction changes.

---

## 2026-07-29 — A recorded transaction is corrected in place, and silence is not an empty value

**Context:** Transactions were create-read-delete, so the only remedy for a mis-entered row was to
delete it and record it again. That is a poor remedy for the mistake it has to serve. Correcting one
digit costs the user every other field on the row — account, date, payee, category, memo — retyped
from memory, and the row they are copying from disappears the moment they delete it, so whatever they
misremember is silently lost in the act of fixing something else. It also changes the row's identity:
a new `Id` and a new `CreatedAtUtc` for what the person experienced as fixing a typo, which points
anything holding a transaction id — a client cache, an in-flight list, and any future attachment,
reconciliation mark or import key — at a row that no longer exists. The gap was already named as a
defect in the alternatives of "Money movement is discarded only by explicit intent, never as a side
effect" (2026-07-29) below: *"No edit path exists either, so the only remedy the product offers for a
mis-entered row would remain none at all."* The second question fell out of the first. A partial edit
has to say what an unmentioned field means, and the wire format has only two states to say it with,
so the answer could not be left to fall out of a serializer's defaults.

**Decision:** Ship `PATCH /api/transactions/{id:guid}` — 204 on success; 404 for an unknown id and
for one belonging to another budget alike, on the standing tenancy rule — so that **a recorded
transaction is corrected in place, keeping its identity.** `amount`, `date`, `description`,
`accountId`, `payeeName` and `categoryId` are mutable; the budget, the `id` and `CreatedAtUtc` are
not accepted at all, because none of them is the caller's to rewrite and the first would move money
between pools. **And make the edit partial in the three-state sense**: each mutable field is either
absent (leave the stored value alone), present with a value (replace it), or present and null (clear
it), carried by `Optional<T>` and an `OptionalJsonConverterFactory` registered on the HTTP JSON
options. That is not a preference between two workable designs — both ways of collapsing three states
into two are defects. If an absent property reads as null, an edit that fixes an amount silently
erases the memo, the payee and the category the caller never mentioned, and nothing rejects it,
because all three may legitimately be empty. If an absent property reads as "leave it", clearing an
optional field becomes impossible and a category assigned once can never be removed. Three of the six
fields are declared over non-nullable types — amount, date and account — so an explicit null for them
is a `JsonException` and a 400 rather than a clear: a transaction without them is not a transaction,
and there is no empty `decimal` to clear to, so a permissive design would fall back on `0`, which is
a legal amount, and zero an entry instead of refusing a request that meant nothing. That falls out of
the type rather than from a branch anyone has to maintain. The contract is **application- and
transport-owned by necessity**: the database has no opinion about which properties a request
mentioned, and by the time a row is written the distinction has already been resolved into a value
([ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md)). Two consequences are
accepted. **An edit can mint a payee**, because the counterparty is supplied by name with the same
find-or-create as on creation, which makes `UpdateTransactionHandler` a second writer of the `payees`
table and is why its write runs inside `ITransactionalExecutor` for exactly the reason creation's
does — a payee committed without the edit that named it is permanent litter, since nothing deletes
payees. And **editing a transaction to name a different counterparty strands the old payee** just as
deleting the transaction does, so the payee list grows with corrections as well as with new entries,
and fixing a misspelt name leaves both spellings in it (see [payees.md](payees.md)). This does not
disturb "Money movement is discarded only by explicit intent, never as a side effect" (2026-07-29)
below, which stands in full: **correcting an entry is not discarding it.** No row leaves the ledger
on this path — an edit changes what a recorded movement says about itself, and the record survives
the change. The two decisions are complementary rather than competing: the delete is for a row that
should never have existed, the edit is for a row that should exist differently, and it is the delete
that stays the only act which removes recorded movement.

**Alternatives considered:** *Leave transactions create-read-delete and let correction be
delete-and-re-record* — rejected: this is the status quo restated, and it is the case the context
above argues against — identity churn, retyping from memory, and the source row gone before the
replacement is typed. *A full-body `PUT` replace* — rejected: it makes the client the authority on
every field of every edit, so it must hold the whole row and echo it back, and an edit to one field
overwrites a concurrent change to another with no signal that anything was lost. It also does not
remove the question, it hides it: a client that omits a property from a replace body is still
asking for something, and the answer is now "clear it" whether they meant it or not. *A two-state
PATCH, either reading absent as null or reading null as absent* — rejected, and it is one rejection
rather than two: both directions are the collapse argued against above, one destroying unmentioned
fields and the other making an optional field unclearable once filled. *JSON Patch (RFC 6902)
operation documents* — rejected: it buys array indexing and nested paths that a flat record of six
scalar fields has no use for, and it moves validation out of a typed command into a document
interpreter, where a malformed path is a runtime error instead of a binding failure. *Supply the
payee by id on edit even though creation takes a name* — rejected: the same field would resolve by a
different rule according to which verb carried it, so a corrected transaction could end up on a
different payee row from an identical one typed right the first time, which is the duplication a
shared payee row exists to prevent.

**Affected areas:** [transactions.md](transactions.md), [payees.md](payees.md) — which gains a
second writer and a second cause of stranded rows — and [_overview.md](_overview.md). The delete
guards in [accounts.md](accounts.md) and [categories.md](categories.md) are untouched: an edit that
moves the last transaction off an account or a category clears their refusals by the same mechanism
a delete does, without any of their rules changing.

---

## 2026-07-29 — Money movement is discarded only by explicit intent, never as a side effect

**Context:** Transactions were append-only at every layer — no update, no delete, no repository
method that could remove one — and that was never a decision, only what had been built. Against that
sits a sentence recorded in "A budget holding transactions cannot be deleted; its empty structure
still cascades" (2026-07-26) and repeated in `TransactionConfiguration` beside the `Restrict`
foreign key: **recorded money movement is the one thing a budget must not lose.** Read literally,
that forbids a person removing an entry they fat-fingered, which is not what it decided. What it
decided is that money movement is never thrown away as *collateral* — as a side effect of a delete
aimed at something else, where the user asked to remove a budget and their history went out with it
unasked. Deleting one's own mistaken entry is a different act entirely: the movement is the thing
the user is aiming at. The record is entered by hand, so it carries hand-made mistakes — a purchase
typed twice because the first attempt looked like it failed, an amount recorded against the wrong
account — and a ledger that cannot drop such a row is not more truthful than one that can. It holds
a movement that never happened, and every sum a person builds on it is wrong by exactly that amount.
The wording forbade the correction by accident, and one consequence was already visible:
`DeleteAccountHandler` refuses with "Account cannot be deleted because it has transactions.", a
sentence naming an obstacle the system gave the user no way to clear.

**Decision:** Ship `DELETE /api/transactions/{id:guid}` — 204 on success, and 404 for an unknown id
and for one belonging to another budget alike, because the `BudgetIsolation` filter makes that row
invisible and this API deliberately answers no **tenancy** refusal with 403 — a foreign row is a row
that is not there, not a row you may not have — and **sharpen the rule to: money movement is
never discarded as a side effect, only ever by explicit intent.** That clarifies "A budget holding
transactions cannot be deleted; its empty structure still cascades" (2026-07-26) rather than
reversing it. **The schema does not move.** `transactions.budget_id → budgets.id` stays `Restrict`
and its four siblings stay `Cascade`, so a budget holding any transaction is still undeletable; the
two rules are about different acts, not different strengths of the same act — one refuses a delete
aimed at a budget, the other permits a delete aimed at exactly one transaction. The removal is a
**hard delete**: the row goes, nothing is flagged. The real user-facing win is a knock-on.
`DeleteAccountHandler`'s refusal above, and `CategoryRepository.DeleteAsync`'s "Category cannot be
deleted because it has transactions.", were dead ends while nothing could remove a transaction —
the user was told what to do and given no way to do it. Deleting the last transaction that
referenced an account or a category now makes that row deletable again, which is what turns a
refusal into an instruction. Two consequences are accepted. A delete can **strand a payee** no
transaction names, and payees have no delete path at all, so the list only grows and the orphan is
permanent (see [payees.md](payees.md)). And the `23503` catches in `AccountRepository.DeleteAsync`
and `CategoryRepository.DeleteAsync` **stop being belt-and-braces and become the real guard**: a
precheck reading "no transactions" can now be invalidated by a concurrent insert, and one reading
"has transactions" by a concurrent delete, so both directions are live where only one was before.
Per [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) the precheck exists
for the *message* and the constraint is what is *correct*: those catch blocks must not be simplified
away as redundant later. `DeleteTransactionHandler` needs no catch of its own — nothing in the schema
references `transactions`, so a delete has no foreign key to violate.

**Alternatives considered:** *A soft delete behind a deleted flag* — rejected: there is no sharing,
no compliance retention, nothing that restores a deleted row, and no soft-delete infrastructure
anywhere in this schema, so the flag would put a predicate on every transaction query — and a second
filter interacting with `BudgetIsolation` — to serve a need this product has not expressed, while
leaving a row the user asked to be gone still sitting there. It reopens the moment any of four
things is wanted: an undo that restores a deletion, a trash the user can pull a row back out of,
more than one person per budget, or an export whose integrity depends on deletions staying
recoverable. *Keep transactions append-only and answer the account and category refusals some other
way* — rejected: this is the status quo restated, and there is no other way. No edit path exists
either, so the only remedy the product offers for a mis-entered row would remain none at all, while
two error messages go on instructing the user to do something the system does not let them do.
*Answer 403 for a transaction in another budget* — rejected on the standing rule in
[budgets.md](budgets.md#must-not): a 403 confirms the row exists, and a by-id target in this API
surfaces as 404 whether it is missing or someone else's.

**Affected areas:** [transactions.md](transactions.md). The delete guards in
[accounts.md](accounts.md) and [categories.md](categories.md) gain a remedy without any of their
rules changing, and the permanent-orphan gap in [payees.md](payees.md) widens to a second cause.
This clarifies "A budget holding transactions cannot be deleted; its empty structure still cascades"
(2026-07-26) below, which stands in full.

---

## 2026-07-29 — Ordering contiguity stays in the domain, and the near-miss constraint is named

**Context:** Category Group and Category positions are persisted, zero-based, and **contiguous**
within their scope — groups over the whole budget, categories inside their group. Contiguity is what
makes a position mean anything: it is the only thing that turns "position 3" into "the fourth item",
which is what a client sends when someone drops a row into the fourth slot. With gaps or duplicates
in the stored list, that number stops naming the slot the user aimed at. The rule lives in
`CategoryOrdering` and `CategoryGroupOrdering`, which reindex the whole affected list on every
insertion, move and removal. That puts it **above** its nominally lowest capable layer, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) requires such a rule to
say why — without which the next reader reads the gap as an oversight and starts writing triggers.
The database holds only the non-negative half, in `CK_categories_position` and
`CK_category_groups_position`; the ordering indexes are deliberately **not** unique.

**Decision:** Keep contiguity domain-owned, because checking it on a write means comparing a row
against every one of its siblings and the only PostgreSQL construct that can do that is a deferred
constraint trigger — procedural logic in the database, which is precisely the boundary ADR 0002
draws around "lowest capable layer". Record the reasoning in
[categories.md](categories.md#business-rules--invariants) beside the rule, and name the rejected
near-miss there explicitly rather than leaving it to be rediscovered.

**Alternatives considered:** A **`DEFERRABLE INITIALLY DEFERRED` unique constraint on
`(category_group_id, position)`** is the near-miss worth naming, because it catches duplicates
declaratively and *is* a constraint rather than a trigger — so the declarative boundary is satisfied
and is not what rules it out. Three other things do. First, the consequence it would prevent is
cosmetic: both read services tie-break on `Id`, so a duplicate produces a deterministic-but-wrong
order that the next reindex of that list repairs on its own — nothing like the tenancy breach or the
bulk loss of recorded money the rules that do live at the bottom prevent — and the bottom layer's
price is paid on every later change. Second, EF has no `DEFERRABLE` support, so it would be
hand-written SQL inside the single baseline migration this repository regenerates by convention;
grants can live outside migrations because provisioning owns them, but schema has nowhere else to
go. Third, it buys half the invariant at best: `0, 1, 5` holds no duplicate, is equally broken to
the person reading the list, and would stay legal.

**Affected areas:** [categories.md](categories.md).

---

## 2026-07-28 — A zero amount is a legal transaction: the record is the point, not the number

**Context:** "Amount must be non-zero (zero has no direction and records no movement)" was recorded
below as a closing clause of the direction decision (2026-07-13) and never argued in its own right.
It is wrong on the ledger's own terms: a ledger records what happened, not only where money moved. A
fully discounted purchase, a refund that exactly cancels the purchase it reverses, a zero-value
invoice worth keeping for its payee, date and category — each is a real event, and what makes such a
row worth having is the record itself, not the number on it. Refusing zero does not remove the event;
it forces the user to invent an amount or drop the entry, and both are worse than storing the truth.
The rule also produced an asymmetry against `Account.OpeningBalance`, which has always accepted zero,
and the reason given for that asymmetry — a new account may legitimately start empty, unlike a
transaction, which must move something — was an assertion rather than an argument. The likeliest real
reason the rule existed is neither of these: an empty amount field binds to `0`, so a blank form
submit would otherwise record a meaningless entry.

**Decision:** **Allow `Amount == 0`.** `Transaction.Create` validates precision and magnitude only,
and the sign keeps exactly the meaning it had, with zero reading as neither direction rather than as
an invalid one. The blank-form guard the rule was probably standing in for **becomes the client's,
explicitly and by name**: nothing below the client can tell a deliberate zero from an untouched
field, so the amount input must require a value rather than default to one. Under
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) that is where a rule of
this shape belongs — it is about the interaction, not about what a ledger may hold — but a guard that
changes layers without anyone building it is a guard that was deleted, which is why it is named here
and in [transactions.md](transactions.md) instead of being left to follow from the principle.
Accepting that lists and totals can now contain rows that add nothing to any sum; that is the
intended outcome, because they add a fact.

**Alternatives considered:** *Keep the rule as the blank-form guard* — rejected: it is product policy
wearing an invariant's clothes, and it pays for catching one careless submit by refusing every honest
zero, permanently and for every caller — including importers and any future consumer that has no form
at all. *Refuse zero only when description, payee and category are all absent* — rejected: it makes
an amount's legality depend on unrelated fields, so the same number is valid or invalid according to
what else was typed, and it still cannot see the difference it is trying to detect. *Leave it and let
users record `0.01` or nothing* — rejected: this is the status quo restated. It either puts a cent
that never moved into the account's total or drops a record the user wanted, and the second is what
people actually do.

**Affected areas:** [transactions.md](transactions.md), [accounts.md](accounts.md),
[_overview.md](_overview.md). This reverses the final sentence of "Transaction direction is the sign
of a single Amount" (2026-07-13) below; the rest of that entry — direction as the sign of one signed
amount, and the rejection of a separate type flag — stands.

---

## 2026-07-28 — Recorded precision is a property of the currency, not a constant

**Context:** `Account` and `Transaction` both rounded against a hard-coded two decimal places, while
every seeded `Currency` carried a `MinorUnit` of 0 to 4 that **nothing read**. The model therefore
advertised a precision it could not honour, and failed in both directions: a JPY account accepted
`1000.5`, half a yen being a denomination that does not exist, while a BHD or KWD account — three
minor units — could not record its smallest unit at all, since `0.125` was refused as over-precise
and, had it got past the domain, `numeric(14,2)` would have silently stored it as `0.13`. Reference
data nothing reads is decoration; worse, this piece of it was already documented as coupled to the
scale of the money columns while that scale was 2 and its own ceiling was 4.

**Decision:** **Make the currency's minor unit the precision rule.** `Transaction.Create`,
`Account.Create` and `Account.Update` take an `int minorUnit` and reject any amount `decimal.Round`
would change, with the message computed from it — "Amount must be a whole number." for JPY, "…no more
than 3 decimal places." for BHD. An out-of-range `minorUnit` is an `ArgumentOutOfRangeException`
rather than a validation error, because it can only arrive from a `currencies` row that
`CK_currencies_minor_unit` would have refused: a broken caller, not user input.
`accounts.opening_balance` and `transactions.amount` widen from `numeric(14,2)` to `numeric(14,4)`,
so the most precise currency the constraint permits is representable at all.
`UpdateAccountHandler` gains `ICurrencyReadService`, which it had never needed — `Account.Update`
re-validates against the account's own unchangeable `CurrencyCode` and now needs the currency behind
it, resolved after the load, where a miss is corruption rather than user error. The rule is
**domain-owned by necessity, not by preference**: checking a row's precision against its currency
means joining `currencies` per row, which PostgreSQL can only do in a trigger, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) rules procedural logic out
of the database even where it would be the lower layer. What the schema owns is the envelope — the
column scale bounds what is representable, the check bounds what a currency may claim — and the
domain picks the right value inside it. The scale still coerces rather than rejects, so widening it
changes what fits, not who enforces.

**Alternatives considered:** *Round everything to four places, the widest a currency may declare* —
rejected: it swaps one lie for another. A USD account would accept `10.0001`, which is not money in
the currency that account is denominated in, and the yen case would be no better than before.
*Express the rule in the schema* — rejected on ADR 0002's declarative boundary: a per-row check would
have to read `currencies`, so it exists only as a trigger — business logic invisible to the type
system, untested by the unit suite, and versioned only by migrations. *Widen the precision as well,
to `numeric(15,4)`* — rejected: `CK_accounts_opening_balance` and `CK_transactions_amount` already
refuse anything above 1e9, and `numeric(14,4)` still leaves ten integer digits, tenfold headroom
above a magnitude no accepted write can reach. The extra digit would buy room for values the schema
rejects, at the cost of changing more of the column than the decision requires.

**Affected areas:** [currencies.md](currencies.md) — now the canonical home of the precision rule and
of why it sits above its nominally lowest layer — [accounts.md](accounts.md),
[transactions.md](transactions.md). The `numeric(14,2)` illustration in
[users-and-ownership.md](users-and-ownership.md) and in
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) names these same two
columns and now reads `numeric(14,4)`; the point it makes — a scale coerces and therefore enforces
nothing — is untouched by the widening.

---

## 2026-07-28 — The provisioned budget has no name, and race safety keys on that absence

**Context:** The budget created at provisioning was named from a constant, and the unique index over
`(user_id, name)` was what made two concurrent first requests collide: both racers wrote the same
literal, so one of them got a `23505` and re-read the other's row. The guarantee therefore rested on
a string two callers had to agree on. Such a constant can drift — a second creation path, a caller
that supplies a name of its own, a rename that looks harmless — and the day it drifts nothing fails
loudly: both inserts succeed and the user silently owns two budgets, the second one invisible behind
a lookup that returns the first. The literal was also product copy living in storage, and a
non-nullable `Name` went on asserting that every budget has a name when the only budget that exists
was never named by anyone.

**Decision:** Make `budgets.name` **nullable** and give the provisioned budget **no name at all** —
`Budget.CreateDefault` produces `Name == null`, the default-name constant is deleted, and
`Budget.Create` still requires a trimmed name of at most 200 characters, so the two factories differ
in exactly that one respect and share a single owner check. Declare the unique index over
`(user_id, name)` **`NULLS NOT DISTINCT`** (`AreNullsDistinct(false)`, PostgreSQL 15+), which is what
makes two nameless rows collide: PostgreSQL's default treats every NULL as distinct, so without the
opt-out both racers' inserts land. The index now states a real invariant instead of a coincidence of
a shared literal — **at most one unnamed budget per user, plus any number of named ones** — which is
exactly the shape multi-budget needs. What a client shows in place of a missing name is
**presentation and stays in the client**, localized there; neither the domain nor the schema holds a
display string, so there is nothing to keep in sync and nothing a migration would have to translate.
Accepting that `Name` is nullable everywhere it is read, so every future budget list, header and
export has to answer "no name" explicitly rather than inherit an answer from storage — which is the
point, but it makes the render contract something each client must be given rather than something it
can assume.

**Alternatives considered:** *A store-level `DEFAULT` on `name` carrying the display string* —
rejected twice over: EF Core sends every mapped, non-store-generated property in the INSERT, so the
default would never fire unless the property were made store-generated, and it would put UI-visible
product copy in the schema, unlocalizable and changeable only by migration. That is the "invariants
down, policy up" boundary of
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md): the uniqueness invariant
belongs at the bottom, the label a person reads does not. *An empty string instead of NULL* —
rejected: `""` is a sentinel meaning both "default by design" and "blank by accident", and a
non-nullable `Name` would keep claiming a name always exists, so the compiler could not tell the two
states apart and every reader would have to remember the convention. *A unique index on `user_id`
alone* — already rejected in "Budget replaces the user as the unit of tenancy" (2026-07-25) as a
constraint a later release must remember to drop; nothing here changes that reasoning.

**Affected areas:** [budgets.md](budgets.md). This supersedes the half of "Budget replaces the user
as the unit of tenancy" below that made provisioning race-safe "because the default budget's name is
a constant"; the rest of that entry, including the deliberate absence of a one-budget-per-user
constraint, stands.

---

## 2026-07-28 — One email, one user: a legible 409 beats silently provisioning a second person

**Context:** `users.email` was unbounded and unconstrained, so two rows could hold the same address.
That sits uncomfortably against the identity model this log already records: identity is keyed on the
Google `sub` **precisely because email is mutable**, and a duplicate address is exactly what a user
whose `sub` changed looks like. Making email unique therefore introduces a failure mode that did not
exist before — a legitimate Google account refused because a stale row holds its address. What tips
the balance is the alternative, which is worse and silent: without the constraint, that same person is
provisioned as a brand-new user with an empty default budget and no signal whatsoever that their
accounts, categories and transactions still exist under the old row. They see a working, empty app and
conclude their data is gone. The address is also the only human-readable identifier a user has, so two
rows holding one address make every support question, export and future notification ambiguous.

**Decision:** Make `users.email` **unique, case-insensitively** — `varchar(254)` on the
`case_insensitive` collation under `IX_users_email` — and bound the other two text columns at
`varchar(255)` for `google_subject` (Google's documented `sub` cap) and `varchar(200)` for
`display_name` (matching every other name column). The two collision paths behave **asymmetrically on
purpose**: a brand-new `sub` presenting a taken address gets a **409** with a sentence naming the
cause, because there is no row to adopt and failing closed is the honest answer; an already-identified
user whose refreshed email is taken has the change **discarded** and the sign-in proceeds on the
stored one, because email is a cached copy of an identity-provider attribute and a cached attribute
failing to refresh must never lock a person out of their own budget. The 409-versus-continue split is
application policy sitting deliberately above the database, which rejects both writes identically and
cannot know that one of them is a sign-in. Accepting three consequences: an over-long email now fails
an existing user's sign-in with a 400 (truncating or swallowing is ruled out by ADR 0002); the
collation folds case but not accents, so `josé@` and `jose@` remain two users; and, being
nondeterministic, it makes `LIKE` on the column fail with `0A000`, so the first search-by-email needs
an explicit `COLLATE`.

**Alternatives considered:** *Leave email unconstrained* — rejected: it converts a changed `sub` into
silent, unsignalled data loss, and the database is the only layer that still holds when the
provisioning handler is wrong. *Rebind the stale row's `google_subject` to the new subject on
collision* — rejected: it makes `sub` mutable in direct contradiction of the identity rule this log
already records, and it would only be safe if the `email_verified` claim were checked, which the API
does not read — an unverified address in a token would then be enough to take over an existing
account. *Throw on both paths, or swallow on both* — rejected: throwing on refresh locks out existing
users over an attribute they do not control, and swallowing on insert drops the caller back into the
race re-read, which finds nothing under the new `sub` and produces the unexplained 500 this change
exists to remove.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

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
