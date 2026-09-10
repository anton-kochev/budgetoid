# Whole List Reads

> Read this before adding a parameter to a list read, before hanging a query string on a
> `GET` that answers with a list, and before promoting a row into the gate's table or demoting
> one out of it.

**Seven list reads are delivered whole — no page, no cursor, no filter, no search term.** They
are `IPayeeReadService.GetAllAsync`, `IAccountReadService.GetAllAsync`,
`ICategoryGroupReadService.GetAllAsync`, `ICategoryReadService.GetAllAsync`,
`ICurrencyReadService.GetAllAsync`, `ICredentialReadService.ListForUserAsync` and
`IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync`. The claim is held at two layers — the
contract that declares the read, and the place its list lands — by `WholeListDeliveryTests`.

**What these rows are held against is not one failure, and the worst of them is not a list on a
screen at all.** Truncate the payee or account list and nothing throws, nothing logs and no other
test in the suite reddens: the client sorts those names itself, because they are sealed and this
server cannot order by them, so a page boundary hands each page to a sort that cannot see the
others. It looks like a list in a strange order. Truncate
`IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync` and the same silence hides something
else entirely — those handles are the `excludeCredentials` of a registration ceremony, so an
authenticator already enrolled and left off the page it was handed is offered the ceremony again
and enrols a second credential for the same key. Nobody watches that happen, because it is a fact
about what a device did rather than about anything a screen draws.

## Six reason families, and prose that is per row

**Flatten these rows into one reason and a reader will go and fix the wrong thing.** Two of the
seven rest on an ordering that does not exist, two on a tree, one on a bounded set, and two on
somebody's control of their own account. Each row therefore carries its own sentence *and* a
`ReasonFamily` — a closed tag, checked for coverage rather than for uniqueness. The four families
below are the gated ones; the other two belong to the rows that are **not** gated and are argued in
the two sections after this.

- **Payees and accounts — no server-side name order exists at all** (`SealedNarrativeName`). The
  name column holds an AEAD envelope drawn under a fresh nonce, so the first differing byte of two
  seals is nonce rather than text: an ordering by it is stable within one read and reshuffled by
  every unrelated save. There is no page boundary with a stable meaning to cut at.
  `IPayeeReadService.GetAllAsync` argues this in its own remarks and is the place to read it; it is
  not restated here. **The two share a family because they share the argument**, and the client
  sorts both lists the same way, through the one function `sort-by-narrative-name.ts` exports. The
  payee row carries one thing beside the argument: it is the list a client resolves a counterparty
  against before it posts a transaction, so a name missing from the page it was handed reads as a
  payee that must be created, and the create collides on `IX_payees_budget_id_name_key`.
- **Categories and category groups — not the ordering argument** (`WholeTree`). `position` is a
  readable integer and the server does order by it, so a page of either list would be perfectly
  stable. Their reason is the shape of what is rendered: the categories screen draws one tree whose
  positions are relative to the **entire** set, so a page boundary splits a tree and leaves the
  client arranging branches it cannot see the rest of.
- **Currencies — a bounded reference set the client picks from in full**
  (`BoundedReferenceSet`). Shared data belonging to no tenant, ordered by the ISO 4217 code the
  server reads in the clear. Nothing about a nonce or a tree applies. A picker offering a window of
  the currencies that exist is a picker that cannot be used to choose the one somebody wants, and
  the set does not grow with anybody's data.
- **Credentials and passkey handles — a window removes a person's control of their own account**
  (`AccountControl`). `GET /api/me/credentials` backs the *Ways to sign in* list on `/app/settings`,
  which draws one row per credential and hangs Revoke on the rows that carry one, so a window is a
  credential a person can neither see nor act on. The passkey handles are the exclusion list above.
  Both sets are bounded by how many authenticators somebody enrols, so there is nothing for a page
  to relieve.

**The family is what may not be pasted across unrelated rows; the prose may repeat an argument and
say so.** The set is closed and **every declared family must be claimed by at least one row**, so
tagging the whole table with one family leaves the rest unclaimed and goes red. The escape is to
delete a family from the enum, which is a visible edit a reviewer weighs. There is deliberately
**no rule that two rows may not share a sentence**: such a rule gives two rows that genuinely share
an argument no legal way to say so, and what it produces is a manufactured distinction written to
satisfy it. What the prose is still held to is being a sentence at all — blank, or shorter than
sixty characters, is reported.

**Nothing machine-checkable holds a row's prose against the family it claims**, and nothing cheap
could: a keyword rule over free prose is satisfied by pasting the keyword, which is the
make-it-green move this gate exists to resist.

## Transactions are outside the gate, by decision

`ITransactionReadService.GetAllWithPayeeAsync` comes back whole today and is **deliberately not
held that way**. It orders by `date` descending and then by the creation instant, also descending —
both columns this server reads in the clear — so a page of it is stable and a client does not have
to reorder it.

**Stability is not what separates it from the gated rows, and reading it that way is the mistake
this paragraph exists to stop.** The category-group row says plainly that a page of *its* list
would hold still too. What is true of transactions and of nothing else in the table is that
**nothing renders a transaction relative to the rest of the set** — no tree, no client-side sort
over a sealed column, no exclusion list. Pagination and filtering for the transaction list are
planned, and gating it would cost nothing today while forbidding a change the product intends to
make: a rule that reads as caution and is really a veto nobody argued for. The row is marked
pageable-later on purpose, and it carries no surface.

It is not outside the *keying* rule, though, and that is the point of keying being declared per row
rather than per disposition: the transaction read declares that it takes nothing, so a parameter
added to it reddens like a parameter added to a gated row. Paging it means editing that column,
which is the visible cost.

## Two reads are held by a stronger rule

`IAccountKeyReadService.ListForAccountAsync` and `IExportReadService.ListOwnedBudgetsAsync` are
whole today and are held somewhere stronger than a shape assertion in this file could hold them.
The account-key read says in its own remarks that paging it would hand somebody nine of their ten
ways back into their account. The export **refuses rather than truncates**
([export](../business-logic/export.md)), which is a claim running in the opposite direction from
this one. Restating either here would put a weaker sentence on top of a stronger one, and the
weaker one is what a later reader quotes.

**Being keyed on an owner is not what puts a read outside the gate.** Four rows in the table take
an owner key and two of them are gated: `ICredentialReadService.ListForUserAsync` and the passkey
handles both name the account whose rows they are, and both are held whole. Scoping and pageability
are different questions, and an owner argument answers only the first — the credential row says so
in its own words, because `credentials` is exempt from row-level security and the owner filter
there is doing real work while saying nothing at all about how much of the list comes back.

## What the gate reaches

Two layers, both container-free. The contract layer is pure reflection. The route layer boots the
API in `Production` over a connection string nothing answers on and reads the route table without
making a request, the way `CompositionBoundaryTests` does.

**The census.** Every list read the swept ports declare is discovered **structurally** — a member
returning `Task<IReadOnlyList<T>>` — and each must be claimed by exactly one row of a written
table. **Discovery finds ten**: seven delivered whole, one pageable-later, two held elsewhere.

**What is swept is two naming conventions and one port named individually**, the shape
`CompositionBoundaryTests` uses against the identical weakness. `ReadService` is what this codebase
calls a projection's port and `Repository` what it calls an aggregate's, and both halves hold list
reads — `IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync` lives in `Domain` and a
`ReadService` sweep alone would never see it. Both halves are still conventions, so the residual
hole is a port conforming to neither; what covers it is listing such a port by name —
`IWebAuthnChallengeStore` is the one there is, it holds no list read today, and that line is what
makes the day it grows one audible. A second such port costs a line, and that is the intended cost.

A written list of names alone fails open the day an eleventh read arrives; a **name** filter fails
open too, and this codebase already proves it, since the transaction list is called
`GetAllWithPayeeAsync` and a `GetAll` filter would be missing it today. A read that changes *shape*
— an `IAsyncEnumerable<T>`, a `Task<PagedResult<T>>`, a port whose name loses the suffix — leaves
discovery entirely, which is why the ten keys are additionally pinned by name.

**What a gated read offers a caller.** Its query record declares no properties, and its response is
**exactly one** list — stronger than "carries one list", because a response holding `Items` and
`NextCursor` satisfies the weaker sentence while being precisely the shape this gate refuses. The
query and the response are **read off the handler's own `IQueryHandler<,>` arguments** rather than
written into the table beside it. Typed as columns of their own, nothing tied them to the handler:
changing a handler's generic arguments left the shape assertions inspecting types nothing used and
green, which is reflection inspecting nothing — the exact failure this file ships controls against.

**What a row takes is declared, not derived.** A row states `TakesNothing` or `KeyedOnAnAccount`,
and the assertion runs over the **whole** table rather than over the gated rows. Deriving it would
open the hole the gate is about: a rule saying merely "every parameter is a `Guid` or a cancellation
token" is satisfied by `GetAllAsync(Guid afterId)`, which is a keyset cursor, which is pagination.
**The limit is real and is shipped as a control rather than admitted in prose** — an owner key and
a cursor are the same type, so the same `Guid` cursor passes on a row declaring `KeyedOnAnAccount`,
and `ContractClassifiers_NameAPageAndCannotTellACursorFromAnOwner` demonstrates exactly that. What
the declaration buys is that the escape costs an edit to that column, beside the parameter, in one
diff.

**Where the list lands is a closed pair of cases, and a gated read does not need a route.** A row's
surface is either served over one `GET`, or reached from inside the server — the second naming the
type and the property the list lands on. Tying "delivered whole" to "has a route" would have forced
whoever added the passkey-handle read to invent a route or drop the row. **Neither case is
satisfied by a value that is merely typed in**: a route pattern nothing answers to is reported
rather than skipped, and a carrier must name a property that **exists** and is a list, so a
misspelled member or one that is not a list reddens.

**The route pin.** Each routed row's bound parameters must be exactly its handler and a
`CancellationToken`. It is an **allow-list over what may be bound, never a deny-list over parameter
names**, which is why it survives somebody calling the knob `cursor`, `after`, `top` or
`windowStart` — a deny-list is a list the person adding the parameter would have to extend. The
lookup is by route pattern and demands exactly one matching `GET`. The pattern is the endpoint's
raw text exactly: a group prefix plus `MapGet("/")` produces a **trailing slash**, which is why
five of the six routed patterns end in one — `/api/payees/` and not `/api/payees` — and
`/api/me/credentials`, mapped at its full path, does not.

**Measured, and worth stating as a caught case: a paging knob hidden inside an `[AsParameters]`
struct is caught.** Its members flatten into the route's binding metadata as separate entries and
the wrapper itself does not appear, while a signature read sees the wrapper and none of its members
— so a read of the binding metadata names two paging knobs where a read of
`MethodInfo.GetParameters()` walks straight past a harmless-looking struct. That is the whole
reason the route layer exists beside the contract one rather than being folded into it.
`[FromHeader]`, `[FromQuery]` and nullable-optional parameters are caught for the same reason: what
is bound is what is inspected, whatever the source or the arity.

## The holes

**Every one of these is open, and a chapter that overstated the coverage would be worse than no
chapter.** Both layers read declarations, so:

1. **A read that fetches every row and returns the first fifty passes.** Neither layer executes a
   read service.
2. **A `Where(...)` narrowing that is not caller-supplied passes — and must.** The tenancy filter
   is exactly that shape ([data isolation](data-isolation.md)), and so is the owner predicate on
   every one of the four owner-keyed rows.
3. **Paging read out of the request's query collection inside a delegate body passes**, as does
   anything resolved from request services there. This is the same limitation
   `CompositionBoundaryTests` records about reading signatures rather than bodies.
4. **A `Take` inside the read-service implementation passes, and this is the likeliest real
   regression.** Neither layer looks at the persistence project at all: the contract stays honest,
   the surface stays bare, and the answer is short. It is entirely uncovered, and saying so plainly
   is better than leaving it to be discovered.
5. **A second, paged route added beside the whole one passes.** The gate inspects only the routes
   its table names; it is **not a census over the route table**. "The route pin holds the route
   table" is the sentence to keep out of a reader's head.
6. **A route-less row is thinner than a routed one, in two ways.** It has no query, so it
   contributes nothing to the asks-for-nothing assertion, and it has no binding metadata, so the
   route layer never sees it. What stands in for both is the carrier assertion and the pinned
   surface.
7. **A row promoted into the gate that is whole by accident rather than by necessity passes.** The
   shape assertions cannot tell a list that must be whole from one that merely is; only the written
   reason can, and no test reads it.

What this gate holds is that the **contract** and the **place the list lands** offer a caller no
way to ask for less than everything. Where another gate covers what this one does not, it is named:
a route that reaches past the use case and queries the database directly is refused by
`CompositionBoundaryTests`, not by this file ([dependency direction](dependency-direction.md)).

## The table is deliberately editable

**Demoting a row is permitted, and it is audible.** The pairing of a row's disposition with its
surface compares the two halves **to each other**, so dropping the surface beside the disposition
satisfies it and says nothing — measured: flipping the payee row and dropping its route in one edit
left the whole suite green while real pagination shipped on the query and on the live route. What
catches that is a separate pin over the delivered-whole set, holding each read **by name and by
surface**, which reddens on exactly that edit and names the read it took out. The surface is pinned
beside the key for a second reason: a routed row rewritten as a route-less one keeps its key and
moves that line, where it would otherwise leave the route layer in silence.

That is a **pin and not a prohibition**. Pagination for the transaction list is planned and will
one day need exactly this edit; a gated row could follow it the day its own reason stops holding.
What the gate buys is not that the edit is impossible — it is that the edit costs a written reason
a reviewer can weigh, in a diff where the stale sentence is visible beside the changed disposition.

**Nothing machine-checks the reason against the disposition, and nothing cheap could.** A demoted
row can keep a sentence still arguing the gated case — measured, one did. What stands in for it is
the red: the person clearing it edits the pinned set and that row in one diff.

## What holds it

`BudgetoidApp/tests/IntegrationTests/WholeListDeliveryTests.cs`, in **twelve tests** across two
layers, with synthetic controls shipped permanently beside the live assertions rather than run once
and reverted — a reflection query that silently came back empty would satisfy every emptiness
assertion while inspecting nothing.

- `Discovery_FindsExactlyTheListReadsTheSweptPortsDeclare` pins the ten keys, so a read that
  changes shape goes red rather than quietly leaving.
- `EveryListRead_IsClaimedByExactlyOneRow` reports a read no row claims, a read two rows claim, and
  a row naming no member.
- `EveryRow_CarriesADecidedDispositionFamilyKeyingReasonAndSurface` refuses an undefined
  disposition, family or keying, a reason too short to be a sentence, a **declared family no row
  claims**, and a surface hung on an ungated row or missing from a gated one.
- `DeliveredWholeReads_AreExactlyTheSetThisFileNames` is the pin that makes a demotion audible, and
  it names each gated read together with where its list lands.
- `EveryListRead_TakesOnlyWhatItsKeyingDeclares` is the contract allow-list, over every row in the
  table and not only the gated ones.
- `EveryDeliveredWholeRead_AsksForNothingAndAnswersWithOneList` reads the query and the response off
  each routed row's handler, and holds a route-less row's carrier member to the same claim.
- `EveryRoutedWholeRead_BindsNothingButItsHandlerAndACancellationToken` is the route layer.
- `ContractClassifiers_NameAPageAndCannotTellACursorFromAnOwner` builds a read service with a page
  on one member and a `Guid` cursor on another, proves both are refused on a row declaring
  `TakesNothing`, and then proves the cursor **passes** on a row declaring `KeyedOnAnAccount` —
  the limit demonstrated rather than described.
- `Census_ReportsADiscoveredReadInNoRow` and `Census_ReportsARowNoMemberAnswersTo` prove both
  failure directions of the census over synthetic names, so neither proof depends on a real row
  being wrong.
- `UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct` is the measurement behind the
  flattened-struct paragraph above: it names both members separately and, in the same case, shows
  the delegate's signature carrying the wrapper and none of them.
- `UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter` binds a nullable `int?`, a
  `[FromQuery]` string and a `[FromHeader]` string beside the handler and the cancellation token,
  and the classifier names all three in declaration order. Its `[FromQuery]` parameter is
  deliberately **not** named anything page-like, which is the allow-list surviving a knob no
  deny-list would have thought to hold; its `[FromHeader]` one never appears in the URL at all, so
  the same case is the tripwire if a future .NET release changes whether a header-bound parameter
  reaches a route's binding metadata. The bound-parameter count is asserted at **five**, so the
  handler and the token were seen and dropped rather than never seen.

Both layers read one table, and the route layer cannot live in `tests/UnitTests` — that project
holds no reference to Api, and the absence is a pinned row in `ProjectReferenceGraphTests`
([dependency direction](dependency-direction.md)). So both live in the integration project, and a
connect timeout in this file is a finding about the file rather than the suite's usual load noise,
because nothing here reaches a database.

See also [payees.md](../business-logic/payees.md), which owns what the payee list is *for* and why
the client is the only side that can order it. What the passkey handles are offered back *for* is
stated on `IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync` itself, beside the ceremony
rules in [passkeys.md](../business-logic/passkeys.md).
