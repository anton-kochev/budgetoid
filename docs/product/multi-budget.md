# Multiple Budgets — Design Research

Design exploration for letting one user own several budgets, each a complete envelope world of
its own. Nothing here is implemented; this is the agreed design direction, written before the
budget (envelope) layer exists. When that layer is built, these rules migrate into
`docs/business-logic/` as current-state documentation; until then this file is the canonical
record of the design and its rationale. It amends one statement in
[multi-currency.md](multi-currency.md): the base currency belongs to a budget, not to a user.

## The problem

Sometimes a person manages money that is not theirs. Organizing an event with its own funds and
inflows; acting as treasurer for a club; handling a relative's finances; running a small side
venture. The pattern is always the same: a distinct pool of money with its own income, its own
obligations, and its own purposes — merely under this person's control, not part of their life.

Today the model offers two answers, both wrong:

- **Mix it in.** Record the event's money in the personal budget. This corrupts the single
  picture — the personal total now includes money that is not the user's, so "one total, one
  truth" becomes a lie, and "can I afford this?" answers from a number that was never
  affordable. The foundation the product stands on breaks exactly where it matters.
- **Second account.** Sign up with another identity and switch between them. This violates
  "entry must cost seconds": nobody signs out and back in to record an expense mid-event, so
  the honest record dies where recording friction appears.

## First principles

### The single picture belongs to a pool of money, not to a person

"One total, one truth about what is where" is a statement about a coherent pool of money —
funds with one owner-purpose, answering one affordability question. A person can preside over
more than one such pool. Forcing them into a single total does not simplify the picture; it
falsifies it. Multiple budgets are therefore not a dilution of the credo — they are its
defense. Each budget is its own complete single picture: its own accounts, its own envelopes,
its own "to allocate", its own plain answer.

### The boundary is ownership and purpose, not currency

[multi-currency.md](multi-currency.md) rejects "per-currency budgets" — splitting one person's
money into separate envelope worlds by currency — because one life deserves one answer. That
rejection stands untouched. The boundary drawn here is different in kind: it separates pools
that genuinely have different owners or mandates. The event's hryvnia and the user's hryvnia
belong in different budgets not because of the currency but because of whose money it is and
what it is for. One pool, one budget — however many currencies it holds.

### The budget is the unit of tenancy

Everything a user owns today — accounts, category groups, categories, payees, transactions —
actually belongs to a *budget*; the user owns budgets. This is the structural reading of the
two principles above, and it settles questions before they are asked: names are unique within
a budget, ordering is per budget, and the base currency (the scalar axis every projection in
multi-currency.md collapses onto) is a property of the budget, because each pool of money plans
its life in its own unit. An event run in hryvnia budgets in hryvnia, whatever the owner's
personal base is. Currencies remain global reference data — the one shared table.

## The construction

1. **A Budget sits between the user and everything else.** A user owns budgets; a budget owns
   accounts, category groups, categories, payees, and transactions. Currency stays global.
2. **Every user has a default budget**, created automatically at provisioning. A single-budget
   user never encounters the concept — no switcher, no budget name, nothing to configure. The
   product looks and behaves exactly as it does today until a second budget exists.
3. **One base currency per budget** — chosen at budget creation; for the default budget, by the
   existing onboarding question. Everything multi-currency.md derives from the base currency
   applies per budget.
4. **Uniqueness and ordering re-scope from user to budget.** Category names unique per budget,
   category group order per budget, payee find-or-create per budget. Payees deliberately do not
   leak across budgets: the caterer paid from the event budget is the event's counterparty, not
   a personal one.
5. **Budgets never aggregate.** No cross-budget totals, reports, or transfers — separate
   pictures stay separate, because summing pools with different owners produces a number that
   answers no one's question. Money genuinely moving between pools is two records: an expense
   in the budget it left, an income in the budget it entered — which is what actually happened.

## The product

For a user with one budget, every mechanism above is invisible — this is the hard requirement.
The concept surfaces only when the user creates a second budget, and creating one asks exactly
two things: a name and a base currency.

With two or more budgets, the current budget becomes ambient context: always visible, cheap to
switch, and impossible to mistake — recording into the wrong budget is the one new error this
design introduces, and the UI's job is to make the active budget legible at the moment of
entry, not buried in a menu.

Switching budgets is a view change, not a session change: same identity, same sign-in, seconds
not minutes.

## Sequencing

The structural change — the Budget entity, budget-scoped ownership, default-budget
provisioning — lands together with the envelope layer, not before it as a standalone refactor
and not after it as a retrofit. The envelope layer is where the base currency gets wired in;
building it user-scoped and re-scoping later would be the expensive mistake this document
exists to prevent. The multi-budget surface (creating, switching) ships after the core
envelope loop is solid: structure first, feature when it has something to offer.

## Deliberately deferred

- **Budget sharing.** Out of scope, as ever — but the budget is the natural unit a household
  budget would share if that day comes. Noted, not designed.
- **Closing a budget.** Events end. An archived budget should keep its history readable without
  cluttering the switcher; the shape of "closed" is undesigned.
- **Cross-budget insight.** A person curious how much they manage in total across mandates is
  asking a reporting question, not a budgeting one. Revisit only if real use demands it.

## Open questions

1. **The closing story** — what an archived budget can still do: read-only, or fully frozen?
2. **Wrong-budget entry** — whether ambient context is enough, or the entry flow needs its own
   guard (e.g. the account picker implicitly names the budget). Decide when the switcher is
   designed.
