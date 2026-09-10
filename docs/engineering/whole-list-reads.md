# Whole List Reads

> Read this before adding a parameter to a read service, before hanging a query string on a
> `GET` that answers with a list, and before promoting a row into the gate's table or demoting
> one out of it.

**Five list reads are delivered whole — no page, no cursor, no filter, no search term.** They
are `IPayeeReadService.GetAllAsync`, `IAccountReadService.GetAllAsync`,
`ICategoryGroupReadService.GetAllAsync`, `ICategoryReadService.GetAllAsync` and
`ICurrencyReadService.GetAllAsync`. The claim is held at two layers — the Application contract
and the route table — by `WholeListDeliveryTests`.

The failure it exists for has no symptom anybody would recognise as pagination. The client
sorts and filters these lists itself, because their names are sealed and this server cannot
order by them; paginate one server-side while the client still orders it and the client sorts
**each page independently**. Nothing throws, nothing logs, and no other test in the suite
reddens. It looks like a list in a strange order.

## Four reasons, not one

**The ordering argument holds for two of the five and does not hold for three, and a reader who
flattens the four reasons into one will go and fix the wrong thing.** Each row of the gate's
table carries its own sentence, and the test refuses two rows that share one.

- **Payees and accounts — no server-side name order exists at all.** The name column holds an
  AEAD envelope drawn under a fresh nonce, so the first differing byte of two seals is nonce
  rather than text: an ordering by it is stable within one read and reshuffled by every
  unrelated save. There is no page boundary with a stable meaning to cut at.
  `IPayeeReadService.GetAllAsync` argues this in its own remarks and is the place to read it;
  it is not restated here. **Accounts carries a second half payees does not**: the client
  already sorts accounts by the **opened** name today, so a page boundary there does not
  merely forbid a future screen, it breaks one that ships.
- **Categories and category groups — not the ordering argument.** `position` is a readable
  integer and the server does order by it, so a page of either list would be perfectly stable.
  Their reason is the shape of what is rendered: the categories screen draws one tree whose
  positions are relative to the **entire** set, so a page boundary splits a tree and leaves the
  client arranging branches it cannot see the rest of — and a move recomputes positions across
  the set.
- **Currencies — a bounded reference set the client picks from in full.** Shared data belonging
  to no tenant, ordered by the ISO 4217 code the server reads in the clear. Nothing about a
  nonce or a tree applies. A picker offering a window of the currencies that exist is a picker
  that cannot be used to choose the one somebody wants, and the set does not grow with anybody's
  data.

## Transactions are outside the gate, by decision

`ITransactionReadService.GetAllWithPayeeAsync` comes back whole today and is **deliberately not
held that way**. It orders by `date` then the creation instant — both columns this server reads
in the clear — so unlike every gated row a page of it is stable and a client does not have to
reorder it, and pagination and filtering for the transaction list are planned.

Gating it would cost nothing today and would forbid a change the product intends to make, which
is the failure a table like this one is most likely to produce: a rule that reads as caution and
is really a veto nobody argued for. The row is marked pageable-later on purpose, and it carries
no route half.

Three further reads are outside the gate because they are keyed on an **owner** rather than on
the ambient budget — `IAccountKeyReadService.ListForAccountAsync`,
`ICredentialReadService.ListForUserAsync` and `IExportReadService.ListOwnedBudgetsAsync`. Each
is held by something stronger than a route assertion where it matters: the account-key read says
in its own remarks that paging it would hand somebody nine of their ten ways back into their
account, and the export **refuses rather than truncates** ([export](../business-logic/export.md)),
which is a claim running in the opposite direction from this one.

## What the gate reaches

Two layers, both container-free. Layer B boots the API in `Production` over a connection string
nothing answers on and reads the route table without making a request, the way
`CompositionBoundaryTests` does.

**The contract census.** Every list read the Application assembly declares is discovered
**structurally** — a member of a `*ReadService` interface returning `Task<IReadOnlyList<T>>` —
and each must be claimed by exactly one row of a written table. Discovery finds nine: five
delivered whole, one pageable-later, three scoped by an owner id. A written list of names alone
fails open the day a tenth read arrives; a **name** filter fails open too, and this codebase
already proves it, since the transaction list is called `GetAllWithPayeeAsync` and a `GetAll`
filter would be missing it today. A read that changes *shape* — an `IAsyncEnumerable<T>`, a
`Task<PagedResult<T>>`, a port whose name loses the suffix — leaves discovery entirely, which is
why the nine keys are additionally pinned by name.

Each gated row is then read for what it offers a caller: the member takes nothing but an
optional cancellation token, its query record declares no properties, and its response record is
**exactly one** list — stronger than "carries one list", because a response holding `Items` and
`NextCursor` satisfies the weaker sentence while being precisely the shape this gate refuses.

**The route pin.** Each gated `GET`'s bound parameters must be exactly its handler and a
`CancellationToken`. It is an **allow-list over what may be bound, never a deny-list over
parameter names**, which is why it survives somebody calling the knob `cursor`, `after`, `top`
or `windowStart` — a deny-list is a list the person adding the parameter would have to extend.
The lookup is by route pattern and demands exactly one matching `GET`: a pattern nothing answers
to is reported rather than skipped, and a group prefix plus `MapGet("/")` produces a **trailing
slash**, so the patterns in the table are `/api/payees/` and not `/api/payees`.

**Measured, and worth stating as a caught case: a paging knob hidden inside an `[AsParameters]`
struct is caught.** Its members flatten into the route's binding metadata as separate entries
and the wrapper itself does not appear, while a signature read sees the wrapper and none of its
members — so a read of the binding metadata names two paging knobs where a read of
`MethodInfo.GetParameters()` walks straight past a harmless-looking struct. That is the whole
reason Layer B exists beside Layer A rather than being folded into it. `[FromHeader]`,
`[FromQuery]` and nullable-optional parameters are caught for the same reason: what is bound is
what is inspected, whatever the source or the arity.

## The holes

**Every one of these is open, and a chapter that overstated the coverage would be worse than no
chapter.** Both layers read declarations, so:

1. **A read that fetches every row and returns the first fifty passes.** Neither layer executes
   a read service.
2. **A `Where(...)` narrowing that is not caller-supplied passes — and must.** The tenancy filter
   is exactly that shape ([data isolation](data-isolation.md)).
3. **Paging read out of the request's query collection inside a delegate body passes**, as does
   anything resolved from request services there. This is the same limitation
   `CompositionBoundaryTests` records about reading signatures rather than bodies.
4. **A `Take` inside the read-service implementation passes, and this is the likeliest real
   regression.** Neither layer looks at the persistence project at all: the contract stays
   honest, the route stays bare, and the answer is short. It is entirely uncovered, and saying
   so plainly is better than leaving it to be discovered.
5. **A second, paged route added beside the whole one passes.** The gate inspects only the
   routes its table names; it is **not a census over the route table**. "The route pin holds the
   route table" is the sentence to keep out of a reader's head.
6. **A row promoted into the gate that is whole by accident rather than by necessity passes.**
   The shape assertions cannot tell a list that must be whole from one that merely is; only the
   written reason can.

What this gate holds is that the **contract** and the **route** offer a caller no way to ask for
less than everything. Where another gate covers what this one does not, it is named: a route
that reaches past the use case and queries the database directly is refused by
`CompositionBoundaryTests`, not by this file ([dependency direction](dependency-direction.md)).

## The table is deliberately editable

**Demoting a row is permitted, and it is now audible.** The pairing of a row's disposition with
its route half compares the two halves to each other, so dropping the route beside the
disposition satisfies it — measured: flipping the payee row and dropping its route together left
the whole suite green while real pagination shipped on the query and on the live route. A
separate pin holds the delivered-whole set **by name**, so that same edit now reddens exactly one
test and the failure names the read it took out.

That is a **pin and not a prohibition**. Pagination for the transaction list is planned and will
one day need exactly this edit; a gated row could follow it the day its own reason stops holding.
What the gate buys is not that the edit is impossible — it is that the edit costs a written
reason a reviewer can weigh, in a diff where the stale sentence is visible beside the changed
disposition.

**Nothing machine-checks the reason against the disposition, and nothing cheap could.** A demoted
row can keep a sentence still arguing the gated case — measured, one did. A keyword rule over
free prose is satisfied by pasting the keyword, which is the make-it-green move this gate exists
to resist. What stands in for it is the red: the person clearing it edits the pinned set and that
row in one diff.

## What holds it

`BudgetoidApp/tests/IntegrationTests/WholeListDeliveryTests.cs`, in two layers and with a
permanent control beside each live assertion — a reflection query that silently came back empty
would satisfy every emptiness assertion while inspecting nothing.

- `Discovery_FindsExactlyTheListReadsTheApplicationDeclares` pins the nine keys, so a read that
  changes shape goes red rather than quietly leaving.
- `EveryListRead_IsClaimedByExactlyOneRow` reports a read no row claims, a read two rows claim,
  and a row naming no member.
- `EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated` refuses an undefined disposition, a
  reason too short to be a sentence, a reason **shared** by two rows, and a route hung on an
  ungated row or missing from a gated one.
- `DeliveredWholeReads_AreExactlyTheSetThisFileNames` is the pin that makes a demotion audible.
- `EveryDeliveredWholeRead_TakesNothingButACancellationToken` and
  `EveryDeliveredWholeRead_AsksForNothingAndAnswersWithOneList` are Layer A.
- `EveryDeliveredWholeRoute_BindsNothingButItsHandlerAndACancellationToken` is Layer B.
- The controls ship permanently rather than being run once and reverted:
  `NarrowingParameters_NamesTheParameterOfAPagedRead`,
  `UnexpectedBindings_NamesAPagingParameterOnARoute`,
  `UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct`,
  `UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter`,
  `Census_ReportsADiscoveredReadInNoRow`, `Census_ReportsARowNoMemberAnswersTo`,
  `Census_AcceptsAReadClaimedByExactlyOneRow` and
  `EveryListRead_ClassifiedAgainstAnEmptyTable_ComesBackUnclassified`.
- `UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter` is what the `[FromHeader]`,
  `[FromQuery]` and nullable-optional sentence above rests on: it binds a nullable `int?`, a
  `[FromQuery]` string and a `[FromHeader]` string beside the handler and the cancellation token,
  and the classifier names all three in declaration order. Its `[FromQuery]` parameter is
  deliberately **not** named anything page-like, which is the allow-list surviving a knob no
  deny-list would have thought to hold; its `[FromHeader]` one never appears in the URL at all, so
  the same case is the tripwire if a future .NET release changes whether a header-bound parameter
  reaches a route's binding metadata. The bound-parameter count is asserted at **five**, so the
  handler and the token were seen and dropped rather than never seen — and the case was confirmed
  live by mutation rather than by a first green: a wrong single expected name reported three items
  where one was expected, and three wrong names reported the first mismatch as `cursor`.

Both layers read one table, and Layer B cannot live in `tests/UnitTests` — that project holds no
reference to Api, and the absence is a pinned row in `ProjectReferenceGraphTests`
([dependency direction](dependency-direction.md)). So both live in the integration project, and
a connect timeout in this file is a finding about the file rather than the suite's usual load
noise, because nothing here reaches a database.

See also [payees.md](../business-logic/payees.md), which owns what the payee list is *for* and
why the client is the only side that can order it.
