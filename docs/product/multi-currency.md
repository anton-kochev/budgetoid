# Multi-Currency Budgeting — Design Research

Design exploration for consolidating accounts in multiple currencies under one budget. Nothing here
is implemented; this is the agreed design direction for the multi-currency model, written before the
budget (envelope) layer exists. When that layer is built, these rules migrate into
`docs/business-logic/` as current-state documentation; until then this file is the canonical record
of the design and its rationale. The product stance is set in [problem.md](problem.md):
multi-currency is core, not an afterthought.

## The problem

A user holds accounts in several currencies (EUR, USD, UAH), spends on one category from any of
them, and receives income in more than one currency. Envelope budgeting ("money gets its jobs
first") makes this harder than it is for a spending tracker: envelopes do not track spending, they
partition actual money. The core identity —

> sum of all envelope "available" = sum of all account balances

— is what makes "can I afford this?" honest. With multiple currencies that identity cannot hold
statically: envelopes are denominated in one unit while the money backing them sits in accounts
whose value in that unit changes daily with zero transactions. Every design is a choice about where
that gap surfaces.

## First principles

### Wealth is a vector; budgets need scalars

Holdings in several currencies have no single objective magnitude — wealth is a vector
({€2,100, $400, ₴120,000}) plus exchange possibilities that change constantly. Any single total is a
projection of that vector onto one axis at one moment: a valuation, not a fact. Budgeting needs
scalar comparisons (spent vs. planned, available vs. price), so every design must answer one
question: when and how is the vector collapsed into scalars? Everything else is downstream.

### The trilemma

Any answer wants three properties; no design gets all three:

- **Comparability** — everything in one unit, so numbers sum and compare ("one total", "% of
  income", Grocery vs. Dining).
- **Stability** — recorded and planned numbers never change without user action.
- **Fidelity** — at the moment of spending, the envelope's number matches what actually happens.

| Design | Keeps | Sacrifices |
| --- | --- | --- |
| One base currency, conversions locked at transaction date | comparability + stability | fidelity |
| Recompute everything at the current rate | comparability + fidelity | stability |
| Envelopes hold per-currency vectors, never convert | stability + fidelity | comparability |

A design claiming all three is hiding the loss somewhere. The useful question is which property
matters where — the answer differs by part of the budget (see horizons, below).

### The conservation law

When rates move, the value of holdings in any fixed unit changes. That gain or loss must surface
somewhere: in envelope balances, in a dedicated unallocated line, or apparently nowhere (until any
total is rendered). It can be moved but not destroyed. Only actual currency matching — holding the
currency the money will be spent in — eliminates it. The app therefore cannot remove FX risk; it can
only reveal exposure honestly and make matching easy. Any design that appears to remove drift is
concealing it.

### Flows freeze, stocks float

Borrowed from consolidation accounting, which solved exactly this problem (P&L at transaction-date
rates, balance sheet at closing rate, the plug in a translation-adjustment line):

- A **transaction is an event**: amounts, currencies, a date — and no rate of its own (see the next
  section). Its cost in any other unit is settled by projecting it at its date's rate, once,
  permanently. Nothing budgeting-useful is gained by re-rating last week's groceries.
- A **balance is a state**: it persists, and its value in another unit genuinely is what today's
  rate says. Let it float, and label it a valuation — converted values display with "≈", never with
  false cent precision.

Frozen flows and floating stocks cannot reconcile perfectly; the difference is the drift, and it
gets exactly one home (the live "to allocate" residual — see the construction).

### Where rates live: facts, reference, projection

There is no "the" exchange rate — mid-market, the bank's spread, and the official rate all differ.
More fundamentally, a transaction itself has no rate at all. A ₴1,200 grocery run from a UAH account
is one amount in one currency; nothing was exchanged — the hryvnia already existed. Rates belong to
representations, not events. The model has three layers, and rates as data live in exactly one:

1. **The fact layer** — what happened: amount(s), currency, date. No rates, ever.
2. **The reference layer** — the daily mid-market table `(date, currency → pivot)`. Rates as data
   live here and only here.
3. **The projection layer** — the budget view. A UAH fact rendered in a EUR budget is projected:
   base value = amount × table rate for the transaction's date.

"Freezing" (previous section) is therefore a policy about the projection, not a property of the
event: **a transaction is an event with no rate; its representation in any other unit is pinned to
the rate of its date, permanently.** For a single-currency event there is no true rate to be right
or wrong against — any projection rate is convention, and mid-market-at-date is chosen for
neutrality and stability. When transaction currency equals base currency, no rate enters anywhere;
the machinery wakes only when denominations differ.

A rate becomes part of the fact only when money actually changed denomination — a **real
conversion** — and even then it enters as **two amounts, never a rate**. Exactly two shapes exist:

- the **cross-currency transfer**: −€100 out, +₴4,300 in; the 43.0 is arithmetic, not data;
- the **cross-currency purchase**: a ₴1,200 price tag paid with a EUR card, settling at −€28.10 —
  for a cross-border life, the more frequent case (income received into a differently-denominated
  account is the same shape).

Everything else is a **notional conversion** — a modeling choice for display and budget math, where
neutral mid-market is the only honest pick.

| Event | Amounts on record | Real rate exists? | Rate used for budget math |
| --- | --- | --- | --- |
| Spend/income, currency = base | one | no | none — it already is base |
| Spend/income, currency ≠ base | one | no | table rate at transaction date (notional, pinned) |
| Cross-currency purchase | two: settled + original price | implied by the pair | none if settled currency = base; otherwise project the settled amount |
| Cross-currency transfer | two legs | implied by the pair | no envelope touched; gap vs. mid-market = the FX cost |

For income the pinned projection applies to its report line only; the money itself remains a
floating stock until assigned (see the construction).

Two consequences:

- **Real beats notional.** With a EUR base, a ₴1,200 purchase that settled at €28.10 consumes
  €28.10 from its envelope — the groceries genuinely cost that, the bank's spread included inside
  the real amount rather than leaking into "to allocate". The table's €27.60 is only the fallback
  when no settled amount exists; the ₴1,200 remains as display context ("what the price tag said").
  A notional rate must never overwrite a real one. The construction generalizes this for envelopes
  of any denomination (the envelope consumption rule).
- **Store amounts, never rates.** A stored rate field is either derivable from two amounts
  (redundant — and now able to contradict them) or notional (and does not belong on a fact). An
  optional second leg — counter-amount plus counter-currency — captures every real-rate case that
  exists. Rates are always reconstructible as quotients.

## The design space

| Model | Idea | Verdict |
| --- | --- | --- |
| Single base currency, locked conversions | All budget math in one unit; flows lock at transaction-date rates; drift lands in "to allocate" | **Default** |
| Obligation-denominated envelopes | An envelope may be denominated in the currency of what it pays for | **Refinement on the default** |
| Per-currency budgets | Separate complete envelope worlds per currency | Rejected |
| Multi-currency envelopes | Envelope available is a vector ({€120, $40, ₴3,000}) | Rejected |
| Current rate everywhere | All numbers, including history, recomputed live | Rejected |

Why the rejections:

- **Per-currency budgets** are exact but destroy the product's soul: "can I afford groceries?" gets
  three partial answers, "one total, one truth" dies, and the model contradicts how multi-currency
  people actually live (buy from whichever card fits the moment). That mainstream envelope-budgeting
  tools settle for exactly this ("keep a second budget") is the market gap.
- **Multi-currency envelopes** are bookkeeping-perfect (it is how plain-text accounting models commodities) and
  UX-poison: funding a job becomes an N-dimensional decision, and the affordability answer — "you
  have €120 plus ₴3,000" — is not a plain answer.
- **Current-rate-everywhere** makes history rewrite itself: yesterday's €187 of groceries becomes
  €191 today with no new purchase; retroactive overspend from a rate move is the opposite of calm.
  Current rates belong to stocks only.

### The refinement: liability matching

A budget category guarantees future purchasing power, and that future spending has a currency. Rent
in Kyiv is a UAH obligation; a Spain vacation is a EUR obligation. The unit in which a plan is both
stable and faithful is the unit of the obligation it plans for — liability matching, the same
principle by which CFOs match revenue currency to cost currency. A rent envelope denominated in UAH
never wobbles against the rent, whatever the base currency does. The trilemma is dodged
per-envelope, paying only with comparability — needed only in reports, where "≈" totals suffice
because decisions happen inside envelopes.

Horizons decide where matching matters:

- **Short-cycle jobs** (groceries, dining — refilled monthly, exposure lives for days): drift is
  sub-noise. Base currency; don't overthink.
- **Long-accumulation jobs** (vacation fund built over months, emergency fund, annual insurance):
  months of exposure — drift is material, and matching is the difference between "saved enough" and
  "saved 8% short." These deserve denomination in the target currency — ideally with the money
  itself held in it.

So: **every envelope defaults to the base currency; any envelope may be denominated in the currency
of its obligation.** A single-currency life degenerates to the plain model; a cross-border life opts
into matching exactly where horizons make it real.

## The construction

1. **One base currency per budget** — a user may own several budgets, each its own envelope world
   with its own base (see [multi-budget.md](multi-budget.md)); everywhere below, "the base" means
   the current budget's. The right choice is the currency of dominant consumption (not of income,
   the common wrong default for people living across borders). Changeable later through an explicit
   restatement wizard — a "budget relocation" event: envelope availables restate once at the
   current rate, and history re-projects into the new base at each transaction's original-date
   rates, so reports stay stable in the new base. For an audience that moves between countries this
   is a life event, not an edge case.
2. **Accounts are single-currency; transactions are recorded in the account's currency** — the
   immutable fact. Converted values are derived presentation.
3. **Flows freeze — and assignment is a flow.** A transaction's base-currency value locks at its
   date's rate, permanently. An assignment — moving money from "to allocate" into an envelope —
   locks the assigned amount in the envelope at that moment. Income freezes only its report line at
   the receipt-date rate ("July income ≈ €930", stable forever); the money itself stays unfrozen
   until assigned, because unassigned foreign currency is an open position whose base value
   genuinely floats — freezing it would show a number that can no longer be realized.
4. **Stocks float**: account balances and the single-picture total convert at the current rate,
   displayed as valuations (≈).
5. **"To allocate" is a live residual**: the sum of account balances (current-rate valuation in
   base) minus the sum of envelope availables (frozen). Envelope numbers never move on their own;
   all drift lands in "to allocate" by construction, with no reconciliation step. This is the credo
   applied to currency risk: an FX move creates or destroys unassigned money, and unassigned money
   must get a job — including, optionally, an "FX buffer" envelope.
6. **The envelope consumption rule**: an envelope consumes the transaction leg denominated in its
   own currency when one exists; otherwise the projection of the settled amount at the
   transaction-date rate. A leg in the envelope's own unit is a fact in that unit — zero conversion
   error; conversion switches on only when the envelope's currency is absent from the event.

   | Envelope | Transaction | Envelope consumes |
   | --- | --- | --- |
   | Groceries (EUR) | ₴1,200 from a UAH account | projection ≈ €27.60 — no EUR leg exists |
   | Groceries (EUR) | price tag ₴1,200 on a EUR card, settled −€28.10 | €28.10 — the real leg, spread included |
   | Rent (UAH-denominated) | ₴15,000 from a UAH account | ₴15,000 — exact, no conversion anywhere |
   | Rent (UAH-denominated) | price ₴15,000 on a EUR card, settled −€350 | ₴15,000 — the UAH leg exists |

7. **Originals stay visible**: every converted amount is traceable to the original
   (−₴1,200 ≈ −€27.60), so trust never depends on opaque numbers.
8. **Assignment is the exposure decision**: funding UAH jobs with UAH income is matched; funding EUR
   jobs with UAH income is a currency position, taken knowingly. The app can close the loop: "your
   EUR jobs are funded by UAH — convert now to lock it?"

### Drift is proportional to mismatch

The residual definition makes a stronger fact fall out of plain arithmetic: **drift in "to
allocate" is proportional to the net mismatch between holdings and envelope denominations — and in
a perfectly matched budget it is zero.**

Base EUR at 43 ₴/€. Holdings: ₴120,000 + €2,100. Envelopes: Rent ₴15,000 (UAH-denominated),
Groceries €200, Vacation €800. "To allocate" = ₴105,000 × rate + €1,100 — only the **unmatched**
₴105,000 carries any rate exposure. UAH drops 5%:

- holdings lose ≈ €139.5 of value;
- the UAH-denominated Rent becomes ≈ €17.4 cheaper to back;
- "to allocate" moves by the difference, ≈ −€122 — the drift of the unmatched ₴105,000 and nothing
  else.

Hold ₴15,000 instead — exactly backing Rent — with the rest in EUR, and the two effects cancel to
zero: the budget does not move at all. The product consequence: the app never lectures about
hedging; it simply becomes calmer the better the user's money sits in the currencies of its jobs.
The incentive lives in the mechanics, not in advice — and it reproduces what financially savvy
people in soft-currency countries already do by hand: convert on payday into the currencies their
obligations are in. When a model rewards the folk wisdom of the people living the problem, the
abstractions are right.

This also settles mismatch visibility: no dashboard is needed for the insight to land — matched
envelopes sit still, unmatched holdings make "to allocate" wobble, and the drift card (see the
budget screen) names the cause.

### The harsh case

UAH does not drift gently; it steps (8→26 in 2014, the wartime re-peg to 36.6). Drift surfacing must
handle jumps: a 20% devaluation presents as an event with a guided re-plan of "to allocate", not as
ambient red numbers.

## The product

How the model surfaces, moment by moment. For a user whose accounts share one currency, every
mechanism below is invisible — the product looks and behaves single-currency.

**Onboarding.** One question: "In which currency do you plan your life?" — explained as the
currency of spending, not of income. It sets the base; nothing else about currency is asked up
front.

**The budget screen.** Envelopes show stable numbers in the base currency; obligation-denominated
ones show their own unit with a currency badge ("Rent · ₴15,000", a subtle ≈ €349 beside it). "To
allocate" sits on top, live. Rate movement surfaces asymmetrically, gated by materiality:

- moved up → "to allocate" silently grows; assign when convenient;
- moved down, below the materiality threshold → nothing — noise does not deserve attention;
- moved down materially → one event card: "Exchange rates moved: −€38 needs reassigning" → a guided
  reassignment (pick envelopes to reduce), or one tap from an FX-buffer envelope if one exists.

A step devaluation is the same card with a bigger number and a full re-plan — an event with
guidance, never ambient red.

**Recording a transaction.** The amount field carries a small currency chip defaulting to the
account's currency; most entries never touch it, and the flagship flow stays identical to
single-currency — amount, payee, category, done. A cross-currency purchase (EUR card, ₴1,200 price
tag) enters through the chip: at the moment of recording, the fact the user knows is the price tag,
not the settlement — banks settle days later. So the price-tag leg is recorded as fact, and the
settled leg is created provisionally at the table rate, marked as an estimate. When the statement
shows the real −€28.10, a one-tap correction replaces the provisional amount and the difference
flows through to the envelope (real beats notional). Never corrected — the provisional value simply
stands.

**Transfers and exchanges.** Transfers are first-class and touch no envelope — money changing
accounts changes clothes, not jobs. A cross-currency transfer records two real amounts
(−€100 / +₴4,300), both known from the bank at exchange time; the implied rate is arithmetic. The
spread against mid-market is absorbed by "to allocate".

**Envelope settings.** Denomination is an optional field at category creation, defaulting to base
and invisible until needed — the opt-in that keeps the plain-answers credo intact for everyone
else. Changing an envelope's denomination is an explicit one-time restatement at the current rate —
an event the user confirms, never a silent recompute.

**Reports and the overview.** Category spending is frozen projections — stable history in the base
currency. Live numbers (balances, net worth, "to allocate") carry ≈ and unfold the vector on tap:
"€2,100 + $400 + ₴120,000 ≈ €5,140". Every converted number is traceable to its original.

**What the product does not do.** No rate charts, no forecasts, no "UAH fell 2% today"
notifications. Rates surface only through consequences for the user's own money — a drift card or
a changed valuation. A currency app watches rates; a budget watches jobs.

## Mechanics

- **Rate storage, hybrid**: an exchange-rate table `(date, currency, rate-to-pivot)` — one pivot
  currency, cross rates derived, carry-forward over weekends — plus a stamp of the resolved **base
  amount only** on each transaction (never a rate: a rate is always reconstructible as a quotient,
  and a stored one could contradict the amounts). The table is the source of truth; the stamp is the
  cached projection and the immutability guarantee. Stamps recompute only on user edits or a
  base-currency switch, never because a rate row arrived late. This also makes changing the base
  currency a cheap, real feature (plausible for exactly this audience) rather than a data migration.
- **Provider — the UAH trap**: the free ECB reference feed (~30 currencies; Frankfurter wraps it)
  does not include UAH. Options: the NBU official API alongside ECB (both free and official), or one
  commercial aggregator (150+ currencies, cheap tiers). Mid-market is the right default; the 0.5–2%
  gap vs. card rates is noise at budget scale.
- **Rounding**: each transaction's base amount rounds to the base currency's minor unit; aggregates
  sum the rounded values, so visible numbers always add up.
- **Real conversions are two-legged**: a cross-currency transfer or purchase records both actual
  amounts as an optional second leg (counter-amount, counter-currency) — −€100 out, +₴4,300 in; the
  effective rate is implied. At entry the known leg is recorded as fact; a not-yet-known settled
  leg is created provisionally at the table rate and marked as an estimate until corrected. Which
  leg an envelope consumes is the consumption rule in the construction; for transfers, the spread
  vs. mid-market is absorbed by "to allocate".
- **Prerequisites surfaced**: a first-class transfer concept (today a transfer would be faked as
  expense + income, polluting reports; cross-currency transfers are where real rates appear) and
  computed running balances (needed for the converted single-picture total).
- **What already stands**: single-currency accounts with immutable currency are the correct
  foundation; a transaction's currency stays derivable from its account.

## Deliberately deferred

- **The mismatch dashboard** — the mechanics already deliver the insight: matched envelopes sit
  still, unmatched holdings make "to allocate" wobble, and the drift card names the cause. A
  dedicated exposure view ("next three months of jobs: ₴45k, €800, $120 against holdings ₴120k,
  €2.1k — you are long UAH") adds analysis, not information; revisit if the ambient signal proves
  too subtle.
- **The matching moment on transfers** — a cross-currency exchange that fully backs an
  obligation-denominated envelope could confirm it ("✓ Rent now matched"); the product-soul garnish
  on the core loop, not part of it.
- **The FX-cost report line** — the spread a real conversion pays against mid-market is absorbed
  silently by "to allocate" for now; surfacing it as a visible expense is a later report.

## Open questions

1. **The materiality threshold** for drift cards — a fixed amount, a percentage of the monthly
   budget, or a blend; needs a concrete definition before the budget layer ships.
2. **Rate source** — ECB + NBU vs. one commercial aggregator; an implementation choice,
   deliberately parked until the model above is built.
