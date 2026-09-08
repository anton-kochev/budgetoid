# The data inventory

Every column of every table carries exactly one classification — **narrative**, **arithmetic** or
**excluded** — and the build fails on a column nobody classified. The inventory is the single source
the coverage tests read, so that adding a column is a decision made once rather than a change to be
remembered in five places.

`Infrastructure/Persistence/Inventory/` holds it: `DataInventory` (the 93 entries and the reader),
`DataInventoryCoverage` (the comparison), `MappedSchema` (the enumerator, in two walks — one over
what the model says a property holds, one over what the store is handed), and
`NarrativeEncryptionCoverage` (the gate that demands ciphertext of the narrative half).

## The three words classify what the product owes the person, not what the column holds

This is the sentence a reader gets wrong first, and getting it wrong produces an inventory that
passes every test and misclassifies a third of the schema.

| Word | Means | What a dependent reads from it |
|---|---|---|
| **narrative** | Text a person wrote. | Must be ciphertext; may not reach a log; must be in the export. |
| **arithmetic** | Server-readable, and part of what the person owns — the export is a copy of it. | Must be in the export. |
| **excluded** | Everything the export deliberately does not carry, each with a written reason. | Must be **absent** from the export. Makes no claim about encryption. |

Two worked examples, because the wrong reading survives review:

- **`credentials.created_at_utc` is excluded**, though it is an ordinary timestamp beside a dozen
  exported ones. A credential is not content a person owns, and the instant one was created is a
  fact about sign-in history. **The data type does not decide the word.**
- **`users.email` is arithmetic and is exported**, even though a separate requirement forbids it
  from a log record. That prohibition rides its own named list. A reader looking for the email in
  the narrative set and failing to find it has found the inventory working, not a gap in it.

## Narrative is derived; arithmetic and excluded are authored

`Of(Narrative)` must equal the model's properties typed `NarrativeField`, and a test holds that in
both directions. So a column cannot be quietly re-classified as arithmetic to green a coverage test,
and a ninth sealed column arrives classified whether or not anybody remembered to say so.

The split between arithmetic and excluded cannot be derived from anything: it is a judgement about
what a person owns. Deriving it from the export would make the export's own completeness gate
compare the export against itself.

**One structural detail that is load-bearing and looks incidental.** `NarrativeField` is a sealed
*class*, not a `readonly struct` — argued at the type itself, for an unrelated reason. Because of
that, the two nullable narrative properties report `NarrativeField` and not `Nullable<NarrativeField>`,
and the comparison is one line with no nullable arm. Were it a struct, that comparison would
silently find six of the eight.

## It keys on the model, and a reconciliation is what makes that a claim about the database

The requirement says "a column exists **in the model**", and a model walk needs no container, which
keeps the gate in the unit tier where it belongs. But every card that reads the inventory makes a
claim about the **database** — that a column is stored as ciphertext, that it appears in an export
of real rows, that an erasure reaches it.

So a container-backed test asserts that the EF model and the live catalog describe the same columns,
in both directions. Measured when it was written: 93 mapped columns over 16 tables against 95 over
17, the whole difference being `__EFMigrationsHistory` and its two columns, and both directions
empty.

`DataInventory.RelationsOutsideTheModel` is the written-down half, and it **excuses a relation, never
a column**. A per-column list would offer a middle where a mapped table drops one column out of the
inventory under a reason written about the table. It lives in production rather than as a test
constant so that a later story tightening "no coverage test names a table" has nothing to relocate.

The reconciliation ships two permanent negative controls, on **separate hosts** so neither can pass
for the other's reason: one adds a relation the model does not map, one drops a column the model
still does. It also asserts both sets are non-empty *before* comparing them — two empty sets differ
in neither direction, and without that half the check agrees with itself having examined nothing.

## An excluded column's reason is a compile-time obligation and a review-time judgement

`ColumnClassificationEntry` has no public constructor. Three factories, and only `Excluded` takes a
reason, with no default. So "an excluded column carries a reason" is a fact the compiler holds, and
so is "a classified column carries no stray reason". A nullable member plus a test demanding non-null
would leave the invalid state constructible, with the test the only thing standing between an author
and `""`.

A length floor is asserted over the reason. **It only makes writing nothing impossible.** No
assertion can tell a real argument from a fluent one — `KeyMaterialSecrecyTests` writes that limit
out at its own reason census, and this inventory inherits it rather than pretending otherwise. The
52 reasons are the review surface of this story, and nothing mechanises them.

**One reason per column, never one per table.** The saving is obvious and wrong: a reason argued at
table grain is inherited by columns it was never written about, which is exactly the drift that
forced `TableExemption` to grow the column set its reason covers.

## Every narrative column is checked for ciphertext, and the check is four facts

`NarrativeEncryptionCoverage.Compare` holds the narrative half of the inventory against the schema
as the *store* sees it. Four facts per column, and four rather than one boolean because each names a
different file to open: the provider is handed `byte[]`, the column is declared `bytea`, the table
carries that column's version check, and it carries that column's length band. A store type that is
not `bytea` is a `HasColumnType` call disagreeing with the converter; a missing check is a deleted
`HasCheckConstraint`.

**A provider type that is not `byte[]` is a *substituted* converter and never a lost one, and only
one of the two can reach this gate at all.** Removing a narrative property's converter refuses the
model build outright, taking the whole unit tier down with an EF message that names no column — so a
lost converter cannot produce that defect. What reaches here is a converter pointed at another type,
which builds cleanly. The two type facts are also independent only because all eight columns state
`HasColumnType`: EF **derives** the store type from the provider type when that call is absent, so a
ninth column omitting it recouples them, one substitution then reports both defects, and the reader
chasing the store-type line goes looking for a call that is not in the file.

**Both sides are parameters and neither reaches `DataInventory.Entries`** — `DataInventoryCoverage`'s
argument, unchanged: a classifier reaching for the shipped inventory could only be trusted, never
tested, because the shipped inventory agrees with the shipped schema and an implementation ignoring
both its arguments answers "nothing wrong" to every question anybody can put to it. It does read
`Classification`, where its neighbour refuses to, and that is the subject rather than an
inconsistency: coverage asks whether a column was decided about, this asks something of the columns
decided one particular way. Filtering inside rather than trusting a caller to pre-filter is what
stops a caller who passed the whole inventory from demanding ciphertext of `transactions.amount` —
a failure that would be loud, and loud in a way that teaches the next reader to widen the gate.

**The schema side is a second walk, `MappedSchema.StoredColumnsOf`, and not a richer `ColumnsOf`.**
The two answer deliberately opposite questions about one property — what the model says it holds
against what the provider is handed — and the narrative columns are exactly where the two answers
differ. `ColumnsOf` carries the model-side type on purpose and has two name-scanning readers that
flatten the table away; a storage description bolted onto it would be dead weight on both and would
put two contradictory answers about one property behind two members of one value.

**The two type checks are a floor, not a ceiling, and reading the pair the other way round is the
mistake available here.** Measured over this model: 19 of the 93 mapped columns already have an
effective provider type of `byte[]`, and 11 of those are not narrative — the four `name_key` blind
indexes, `session_tokens.token_hash`, `recovery_code_hashes.verifier_hash`, both of
`wrapped_account_keys`' envelopes, `passkey_public_keys`' two columns and
`webauthn_challenges.challenge`. Raw bytes carrying no envelope at all satisfy both type checks, so
a column misclassified as narrative passes half the gate on shape alone. What tells a sealed column
from a hashed one is the version check and the length band; the type pair only rules out a column
this server can read directly.

**The effective provider type comes from the converter first, and the fallback is the ordinary case
rather than the edge.** `GetProviderClrType()` answers null for all eight narrative columns — their
converters are supplied as objects rather than through the generic overload — while
`GetValueConverter()?.ProviderClrType` answers `byte[]` for every one of them; and
`GetProviderClrType()` answers non-null elsewhere, `accounts.type` among them. The two accessors are
complementary, so a reader taking either alone reports every narrative column as storing
`NarrativeField` — eight defects shaped exactly like a real substitution and belonging entirely to
the reader, over a schema in which nothing is wrong. Falling
through to the property's own type covers 63 of the 93 columns, which answer neither accessor, and
it is not a guess dressed as an answer: a property with no converter and no declared provider type
is handed to the provider as its own type, and that is the whole of the claim.

**A constraint is matched to a column by its whole predicate, and the two obvious alternatives are
both broken here.** The expected predicate is rendered from `CiphertextEnvelope.MinimumLength`,
`CiphertextEnvelope.Version` and a `NarrativeFieldLimits` cap, and compared for equality once runs of
whitespace are collapsed. Matching by SQL substring instead hands `length(name_key) = 32` to the
column `name`, because the digest's predicate mentions `name`; matching by constraint-name prefix
does the same, because `CK_accounts_name_key_length` starts with `CK_accounts_name`. Four tables
carry a `name_key` beside their narrative `name`, so this is measured rather than theoretical: under
either rule, deleting the real `CK_accounts_name_length` leaves a constraint "about name" standing
and the gate green, which is the exact failure the gate exists to make impossible. Whitespace is
collapsed because the length predicate is built by concatenating two interpolated fragments across a
line break, so re-wrapping it is a meaning-free edit; keyword case is **not** folded, because no
configuration here spells one in upper case and a predicate arriving as `LENGTH(name) BETWEEN …` is
somebody rewriting the constraint, which deserves a second pair of eyes.

**One consequence moves a claim made elsewhere, so it is written out here.** The version predicate's
`substring(… from 1 for 1)` spelling — which `get_byte` may not replace, argued at
`AccountConfiguration` and in
[ciphertext-envelope.md](../business-logic/ciphertext-envelope.md#two-checks-on-one-column-and-which-one-bites)
— is a different string under this comparison and reddens. So the spelling now has **two holders at
two stages**: this gate reads the model, so it answers on the configuration edit itself with no
container involved, while `SchemaConstraintSnapshotTests` reads `pg_constraint` on a live database
and answers only once a migration carrying the change exists and has run. The drift guard reddens on
a configuration-only edit too and is **not** a third holder of the spelling: what it reports is that
the model moved without a migration, and it names no predicate. None of the three supplies the
*reason* — a moved literal reads as a paste, and the natural repair is to update the expectation.

**Read this before defining a version 2, because the expectation renders exactly one accepted
version.** Bumping `CiphertextEnvelope.Version` re-renders the gate's expectation and all eight
configurations together: the suite goes green while the schema begins refusing every row written
under version 1, and the constraint that would actually be correct — the one accepting both
versions — is the one this gate reports as *missing*. Measured against the shipped gate: a predicate
naming both versions, one accepting anything up to the second, and one naming the second alone are
each reported as a missing version check. So the gate pushes toward the single migration that
destroys data, and the cheap reaction under a deadline is to widen the matcher. The remedy is to
change the shape first; widening the matcher so a version-2 constraint passes buys a green suite by
retiring the check.

## What the encryption gate does not check

**It does not check a column's cap at all** — not the value, not the direction, not agreement with
the other columns of its class. Which of `NarrativeFieldLimits`' two numbers a column earns is a
judgement about field class, and the model states nothing a derivation could read it off, so a
length band is accepted when it names *either*. Both directions are silent and they are different
accidents: a name column widened from 1024 to 2560 quietly accepts two and a half times what its
class allows, and a description narrowed the other way is a silent **refusal** — a note that stored
yesterday answers `23514` today. There is no cross-column opinion either, so `accounts.name` at 2560
beside `payees.name` at 1024 is green. `NarrativeFieldLimits` holds two constants over field classes
precisely so that eight columns cannot disagree about one rule, and this gate does not restore that
property: a ninth narrative column arrives free to pick either number.
`Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth` makes the limit executable
rather than leaving it in prose, and a red there means somebody closed the limit: the remedy is to
delete that case, never to widen anything.

**Which neighbour picks that up depends on whether the column already exists, and the two answers
are opposite.** For a column that **exists**, changing its cap moves the model, so the drift guard
reddens before any migration is written — the shape the paragraph below draws for constraint names —
and once one has run, `SchemaConstraintSnapshotTests` holds all eight length constraints as expected
literals with their numbers rendered in them, so a name widened to 2560, a description narrowed to
1024 and two columns of one class disagreeing each move a line there. For a **new** column, no tier
judges the number at all: that snapshot asserts an *equivalent set*, so a ninth constraint arrives as
an unexpected item and is answered by pasting in its rendered text, cap included. The cap a new
narrative column is given is held by review, in the same place review already holds which of the
three words a column earns.

**It is blind to constraint names.** Renaming `CK_accounts_name_length` is invisible here, and so is
deleting it and adding an unrelated constraint carrying the same predicate. As a check on
*encryption* that is honest, because PostgreSQL enforces a `CHECK` whatever it is called — but the
alphabetical ordering that decides which of a column's checks fires first is a property of the
**name**, so something has to hold it. Two neighbours do, and neither covers the other's stage: the
migration drift guard, `BudgetoidDbContextConstructionTests.Migrations_MatchTheModel`, catches a
rename living in the configuration alone, because it diffs the migrations snapshot against the
design-time model; `SchemaConstraintSnapshotTests` catches one only once a migration carrying it
exists and has run. **The drift guard opens no connection and still lives in the container tier**,
which is the thing to know about it: skip that tier and the only configuration-stage guard over
constraint names *and* over an existing column's cap does not run at all, leaving both to review
with nothing saying so.

**It reads the EF model, not the applied schema.** A narrative column whose migration is unwritten,
or whose migration says `text`, passes it. That is the inventory's own limit inherited rather than a
new one, and it is closed the same way: `DataInventoryReconciliationTests` holds the model against
the live catalog, and `NarrativeSecrecyTests` reads real rows back off a real connection. Neither is
replaced by this gate, and this gate is not replaced by them.

**The fact pin behind `StoredColumnsOf` is two columns deep, not ninety-three.**
`StoredColumns_DescribeTheSameSchemaTheEnumeratorDoes` holds the two walks equal as sets in both
directions and as counts — a set comparison is blind to a duplicate — and then pins the provider
type, the store type and the table's whole constraint list on `accounts.name` and on
`transactions.amount`. Everything else in that case is names, so a walk returning the right column
names with the wrong facts on some other column passes this file. Two columns is what the pin is
worth, and the alternative is a second copy of the schema in a test file. What forced even that much
was measured: a `StoredColumnsOf` handing *every* column the union of *every* table's constraints
agrees on every name, in both directions, at the same count, and reddened nothing — and combined
with deleting `CK_accounts_name_length` it left **this gate** green, because
`length(name) between 29 and 1024` stands on three other tables and the matcher found it on the
union. Inside this gate, binding a constraint to its own table is the whole of what holds the
`name_key` case, and those two columns are what hold that. The deletion itself is not this gate's
alone to catch — the drift guard sees the model move, and the catalog snapshot names that constraint
a migration later.

## A second gate stands over a layer that does not exist yet

`EnvelopeBudgetingIsolationTests` asserts that no envelope-budgeting computation reads a narrative
field — CON-005, which says that assignments, activity, available and "to allocate" are arithmetic
over amounts, dates and identifiers only, and that the constraint exists so the envelope layer
cannot break that later. **There is no envelope-budgeting code in this repository**, so the gate
is over the empty set today. `EnvelopeBudgeting_DeclaresNoTypesYet` pins that emptiness and carries
the sentence routing whoever reddens it: a red there means the first computation landed, so the pin
is retired by moving a non-emptiness floor into the gate beside the assembly floor it already
carries — never by deleting it, which would leave the suite with no statement about whether the
subject exists.

**The subject is a namespace convention** — `Application.Budgeting.*` and `Domain.Budgeting.*` — and
deliberately not an opt-in marker. A marker interface or attribute reads better and fails worse: a
forgotten marker is silent, which is the polarity argument this repository already makes where it
matters most. A namespace is not forgettable the same way, because a file has to be filed somewhere
and the folder is where a reviewer looks. It does not read the inventory at all: its target is the
`NarrativeField` type by name, so re-classifying a column can neither green nor redden it.

**Both halves of the detector are load-bearing, and that was measured rather than reasoned.** A
signature-only census misses the violation entirely, because a computation that *reads* a narrative
property declares nothing — it touches one in a method body, through a projection or a predicate.
Dropping the signature decoding does not silence the detector either, and the honest statement is
sharper than a count: it loses **exactly the property-read shape**, which is the shape a computation
actually uses. A property read compiles to a call whose declaring type is the entity, so the target
appears only in the referenced member's signature and never as a token in the body — while a touch
whose token owner already *is* the narrative type, or which names it structurally through a generic
instantiation, survives the same mutation. That is what makes the half load-bearing without making
it the only half. The reproducible counts, per mutation, live with the cases rather than here, so
that they cannot rot in three places.

**The escape scan is what keeps the gate pointed at its subject.** A guard over the empty set cannot
tell "there is no envelope-budgeting code yet" from "the prefix stopped naming it", so a second
census reads the two assemblies for a type whose namespace carries a `Budgeting` segment and reports
any that is not under its ring's pinned prefix — as a whole dot-separated segment and never a
substring, or the fixture namespace this suite deliberately files a violation in would be swept in.
It answers from the assemblies rather than from the prefix constant, which is the whole of what lets
it object to a mistyped prefix: a floor derived from the spelling it stands in for cannot object to
that spelling being wrong.

**`Infrastructure` and `Api` are outside both prefixes, and the exclusion is worth a number rather
than an assurance.** Persistence stores and returns narrative values, which is not deciding what a
person may spend, and transport does neither — so neither ring is a home the gate covers. Pointed at
`Infrastructure.ReadServices`, though, the same detector reports **six** computations touching
`NarrativeField` today, over eleven types, while `Api` reports none over thirty-eight. That ring is
where an envelope-balance query written to the existing repository pattern would most plausibly
land, so a gate over it as things stand would report six things nobody wants reported — and anybody
weighing a third home later should meet the six rather than discover it.

**Its limits, each of which reads as an oversight to whoever finds it.** A narrative value reached
through `dynamic` or through reflection carries no signature naming the type, and neither does a
`calli` standalone signature. Three more are measured on the shipped detector and the first is a
decision rather than a gap: **delegation is not followed**, not even one hop, so a computation under
the prefix that calls a helper filed elsewhere — the helper reading the column and handing back a
`decimal` — is reported clean, because following the edge needs a call graph plus a judgement about
which member in the chain *is* the computation. The counter-intuitive half is what the escape
requires: a helper whose **own signature** names the type *is* reported at the call site, so only the
shape that launders the type behind a row parameter gets through. Beside it, an implemented
interface's **type arguments are not walked** — `: INotes<NarrativeField>` leaves neither a body
token nor a member signature — and an **attribute's are not either**. Anything filed outside the two
prefixes is invisible by construction,
which a permanent fixture makes visible rather than leaving to be discovered. A **consistent** double
typo — the namespace segment constant and the prefix misspelled the same way — blinds the escape
scan too, and no check inside one file can catch two edits that agree with each other; what it buys
is that the single-edit version, the one somebody actually makes, is loud. And the *meaning* of a
read is not judged: a computation touching a narrative field to pass it straight through to a
response is reported exactly like one that branches on it, deliberately, because that judgement is
one no scan can make.

**Nothing mechanically holds that a future envelope-budgeting layer is filed under those
namespaces**, and the escape scan above is not the neighbour a reader will take it for: it reports a
`Budgeting` segment filed in the wrong place, so a computation the next author names something else
entirely carries no segment and is invisible to it as well as to the gate. That obligation is held
by review, the way "exactly one path creates an account" is, and `CLAUDE.md` carries it for the same
reason: it is one line that would redden no build. The remedy
when a computation turns up outside is to move it under the prefix, or to state a third prefix with
the reason the layer grew a third home — never to widen the segment until it matches wherever the
code drifted to.

**No ADR is written for either gate, and the absence is deliberate.** The decision both rest on —
key on the model, and let a container-backed reconciliation make that a claim about the database —
was taken in
[ADR 0024](../decisions/0024-key-the-data-inventory-on-the-model-and-reconcile-it-against-the-catalog.md)
and is inherited whole. What is new in the two is mechanism, argued where it is implemented and
restated here.

## What this does not replace

The inventory is one axis, and **it replaces none of the ten censuses beside it.** They ask different
questions of the same columns, so none of them is the inventory said another way and none is retired
by it. The families a reader adding a column will meet:

- `RowLevelSecurityCoverage` classifies **relations** for tenancy, discovered from the catalog, and
  fails closed on a relation nobody decided about.
- `KeyMaterialSecrecyTests.Classifications` argues, per `bytea` column, *why holding this unwraps
  nothing*.
- `ProhibitedColumnVocabulary` and its two siblings are deny-lists over **names**, failing open by
  design.
- `DataMinimizationSchemaTests` pins exact column sets for three tables; its verdict is *this column
  must not exist*, where the inventory's is *classify it*. **A new column on `users` reddens twice
  now, and both verdicts are wanted** — the second is not noise the first makes redundant. Answering
  the inventory alone files the column under one of three words and says nothing about whether it may
  be there; answering the pin alone decides it may be there and leaves it classified by nobody. The
  reflex a double red produces is to delete one of the two reds, and either deletion loses a
  question that has no other asker.
- `AppRoleGrantMatrixTests` mirrors the grants file and says of itself that it is a mirror, not a
  judge.

The two gates above are **readers** of the inventory rather than censuses beside it, and only one of
them reads it at all. `NarrativeEncryptionCoverage` asks one thing of the columns the inventory calls
narrative and nothing whatever of the other eighty-five — a column nobody classified is the
neighbouring gate's verdict, not this one's. `EnvelopeBudgetingIsolationTests` reads neither the
inventory nor the schema; its subject is IL.

**One gap, stated rather than closed:** the client's `NARRATIVE_FIELDS` is a second executed list, in
another language, and nothing reconciles it with the inventory at build time.

## Adding a column edits the inventory and nothing else

That property is held by there being one enumerator and one classification, not by anybody
remembering. Two coverage tests used to carry their own walk over the design-time model, and a third
carried its own list of the narrative columns.

- `ProhibitedColumnVocabularyTests` and `ErasureRemnantVocabularyTests` read `MappedSchema.TablesOf`
  and `MappedSchema.ColumnsOf`. Each still flattens the table straight back out, because its subject
  **is** a name — an identifier spelling that would be a refusal wherever it appeared — which is the
  live demonstration that a name and a column are different questions, and the reason `MappedColumn`
  carries a table at all.
- `NarrativeSecrecyTests.Markers` is held equal to `DataInventory.Of(Narrative)` in both directions.
  Its seeding is the one authored half of that census: the scan reads the catalog, so a ninth
  narrative column is searched the day it appears — but nothing would write a marker into it, and
  the presence assertion that makes the run non-vacuous would pass over it in silence, reporting the
  requirement met over a column it had nothing to find in.

Reaching the model through the inventory is what makes the second of those a **three-way** agreement
rather than two lists agreeing by coincidence: the narrative classification is derived from the
model, so a column typed for a sealed value cannot stay quiet in all three places at once.

**Two limits that binding does not reach**, both held by review. The marker *text* is not compared,
only the columns — two markers that swapped tables agree on every qualified name, and a red census
would then attribute a leak to the wrong write path. And whether the seeding actually lands a value
in each column is a claim about rows that no comparison of two authored lists can make; only the
container scan reading the database back can see it.

**The encryption gate keeps that property, and the one thing it does not carry arrives a stage
later.** A ninth narrative column arrives classified, because narrative is derived from the model's
own types, and it is examined for ciphertext the same day with nothing in either test file edited —
no name of any of the eight appears in a case that checks it. Which **cap** it earns is the
exception, and on a *new* column no tier judges the number: this gate accepts a band naming either,
and the catalog snapshot reports the unfamiliar constraint as an unexpected item, which is answered
by pasting in its rendered text with the cap already in it. So adding a column still edits the
inventory and nothing else, and the cap it was given is a line in a diff somebody approves rather
than a rule anything checks.

## Tests that lock it

- `DataInventoryCoverageTests` — the FR-005 gate, the empty-inventory and stale-entry controls, the
  duplicate check, the narrative derivation in both directions, and the reason floor.
- `DataInventoryReconciliationTests` — the model against the live catalog, with its two controls.
- `MappedSchemaTests` — the enumerator, including that it keeps the table with the column. Two
  columns named `name` on different tables is what proves it; an enumerator that flattens the table
  away collapses them into one, which is what its two name-scanning readers do deliberately and
  what an inventory may never do.
- `NarrativeSecrecyTests.Markers_NameExactlyTheColumnsTheInventoryClassifiesNarrative` — the seeding
  list against the narrative classification, both directions reported apart because they are
  different defects, and non-vacuity asserted before either. It opens no connection and claims
  nothing about any column being encrypted.
- `NarrativeEncryptionCoverageTests` — the FR-057 gate over the shipped schema and the shipped
  classifications; the pin holding `StoredColumnsOf` equal to `ColumnsOf` in both directions, as
  counts, and on the facts of two named columns; and the controls, each a `StoredColumn` built by
  hand so that no case asks production to be mutated in order to watch the gate fail — a column
  stored as text, one the model states no store type for, one missing each of its two checks, the
  `name_key` trap, an inventory classifying nothing narrative, an inventory naming a plaintext column
  narrative, and a narrative entry the model no longer maps. One case asserts a **limit** rather than
  a rule — `Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth` — so a red there is
  somebody's fix and the remedy is to delete the case.
- `EnvelopeBudgetingIsolationTests` — the CON-005 gate over `Application.Budgeting.*` and
  `Domain.Budgeting.*`, the pin on its subject being empty, and eleven controls over synthetic
  subjects in the test assembly: the body read, the declaration, a clean computation, a
  compiler-generated display class, an expression tree, both cross-assembly arms, a type whose name
  merely starts with the target's, a generic instantiation, the namespace limit, and the mistyped
  prefix the escape scan answers.

There is deliberately **no** case asserting each column has exactly one classification. With one
non-nullable enum member per entry and a duplicate check beside it, that is a fact about the types,
and a test restating a type fact is a decoration.
