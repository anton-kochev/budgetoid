# Frontend Performance

> Read this before changing how a screenful of sealed values is read, before quoting a
> throttling rate as though it were a device, before comparing any figure here against one
> taken on another machine, and before proposing that any of it become a gate in CI.

**Every number in this chapter was recorded by a harness that asserts nothing — bar the
per-yield figures, which say so where they are quoted.** There is no wall-clock assertion
anywhere in the suite, the harness does not run in CI, and neither absence is an oversight —
both are argued below. What makes a stale number *visible* is that the browser version, the
calibration and the machine's own BenchmarkIndex sit beside it, so a reader can tell whether
the figure was taken on the machine and the browser they are holding. The per-yield figures
carry none of the three, which is why they are the weakest evidence here and why nothing rests
on them alone.

**And every figure here is a figure about one machine.** A calibrated profile does **not**
transport: measured, the same harness under the same Chrome build and the same calibrated
low-tier profile reads about **2.1× faster** on a second machine — and faster in the
*flattering* direction. So the fingerprint beside a number is not provenance decoration, it
is the condition under which the number may be compared with anything at all. See
[A profile is a device, and only on the machine that calibrated
it](#a-profile-is-a-device-and-only-on-the-machine-that-calibrated-it).

## The read that is measured

The transaction list is the widest narrative read in the product. Five sealed columns arrive
per row — the transaction's own note, plus four denormalised foreign names joined onto it —
so a 200-row page carries **1000 envelopes**. Opened a row at a time, the same account name
and the same counterparty name are opened once per row that references them — a budget with
four accounts pays two hundred account-name opens for four distinct values.

`openNarrativeBatch` in `+core/security/narrative-batch.ts` opens each distinct
`(table, column, rowId, wire)` request **once**, runs the opens concurrently in chunks of
sixteen, and hands the frame back between chunks whenever **8 ms** has been spent since it
last did — through `scheduler.postTask` at `user-visible` priority, falling back to a
`MessageChannel` message where that API is absent. What it hands back is an **opener** rather
than a map: it answers from the batch and falls through to a real open on a miss, so a field
the collector forgot costs one open and can never cost a rendered value.
`TransactionsService` reads in two passes: collect the distinct requests through
`transactionNarrativeRequests`, open them as one batch, then map the rows through the opener
that comes back.

**The loop holding the list is what yields, and that is the load-bearing half.** A yield
written inside a single field's open, under one outer `Promise.all`, chunks nothing: every
open body starts in the same synchronous burst, so each one past the budget awaits the same
yield and they all resume in the same task. Only something holding the whole list can decide
how many opens run before it steps aside.

## The cheapest yield is the one that does not work

**The per-yield figures in this section are the one set here the harness did not take, and
they cannot be re-taken from this repository.** They come from a single probe — Chrome under
a low-tier CPU throttle — run outside it, with no browser version and no calibration recorded
beside them. `tools/narrative-perf` is not that probe and cannot stand in for it: the rig
measures the read path end to end and records which mechanism the frame went back through,
but it never puts two mechanisms side by side. They are why a choice was made rather than
evidence anybody can refresh, and a reader who needs one of them to be true today has to
measure again.

**The ordering does not rest on them.** `narrative-batch.ts` argues it from what each
mechanism is, and the platform's own rules are where a reader checks that: an ordinary task
lets the browser render before the continuation runs, a yield taken at continuation priority
does not, and HTML floors a timer nested more than five deep at 4 ms whatever was asked for.

**`scheduler.yield()` is refused, and it is refused *despite* its number rather than because
of it.** That probe found, per yield: `scheduler.yield()` costs **~0 ms**, and it costs
nothing because it buys nothing — it resumes the continuation ahead of the browser's own
rendering work, and the worst frame gap over a run chunked with it measured the same as over
a run that was not chunked at all. **No frame was drawn.** It is the number that looks best
and the mechanism that does not do the job, so it may not be put back on the strength of the
number. That refusal is held without a clock: a case stubs a `scheduler` carrying both
members and asserts the frame went back through `postTask` while `yield` was never called.

`setTimeout(…, 0)` is at the other end: the nested-timeout clamp costs **4.68 ms** per yield
on that probe, and the 4 ms floor underneath it is normative rather than measured — either
way it is most of a frame spent waiting rather than working. That is why the fallback beneath
`scheduler.postTask` is a `MessageChannel` message — a real task, available wherever
`scheduler` is not, and not subject to the clamp.

## A profile is a device, and only on the machine that calibrated it

`Emulation.setCPUThrottlingRate` slows the renderer's task execution; it does not slow the
machine. So a rate recorded on one machine means a different device on the next one, and
**a rate quoted on its own means nothing at all**.

`tools/narrative-perf/calibrate.ts` reproduces DevTools' own procedure rather than pinning a
number: run Lighthouse's BenchmarkIndex, then bisect on the throttling rate until this
machine scores the reference device's index. **Read against DevTools'
`CalibrationController`, the reproduction is faithful** — the same two reference indices, 264
and 1000; 250 ms per benchmark iteration; eight bisection steps; ten points of score counted
as a match; and rates truncated to hundredths, because the throttling agent truncates there
and a finer candidate would be scored under a rate it was never asked for.

BenchmarkIndex on the measuring machine, unthrottled: **2726**.

| profile | reference device | target index | nominal rate | scored | effective |
| --- | --- | --- | --- | --- | --- |
| mid-tier | Pixel 5 | 1000 | 2.65x | 983 | 2.77x |
| low-tier | Moto G4 Power 2022 | 264 | 10.05x | 260 | 10.50x |

**The profile does not transport, and this is the correction most likely to matter to
whoever reads the figures below.** A calibrated profile is a device *on the machine that
calibrated it* and nowhere else. Measured: the harness was run on a second machine — same
Chrome build, same low-tier profile, calibration reaching the same reference device — and
every cell came back about **2.1× apart**.

| low-tier, batch | recorded here | second machine, run 1 | run 2 |
| --- | --- | --- | --- |
| *typical* median | 56.0 ms | 30.5 ms | 26.3 ms |
| *heavy* median | 73.4 ms | 47.6 ms | 40.2 ms |
| *pathological* median | 178.5 ms | 96.8 ms | 82.5 ms |
| idle rAF band | 16.7–16.8 ms | 33.4 ms | 33.4–33.5 ms |
| BenchmarkIndex at 1x | 2726 | 1368 | 1396 |

**The error runs in the flattering direction, which makes this an anti-conservative
instrument.** A slower measuring machine needs a *lower* throttling rate to reach the same
reference index — and CPU throttling does not slow AES the way it slows a JavaScript loop, so
the crypto runs closer to full speed while the benchmark agrees the device is a low-tier
phone. The weaker machine therefore records the **better** number. A figure read across
machines is not merely noisier than it looks; it is wrong in the direction nobody checks.

**What the calibration delivers is a reproducible procedure, not a comparable number.**
Anybody can re-run it and get a rate derived the way DevTools derives one, which is the whole
of what "calibrated" buys and is worth having: the rate is honest about being a rate. What it
cannot do is make two machines' milliseconds mean the same thing. So a recorded figure carries
its **machine fingerprint** — BenchmarkIndex at 1x, the Chrome build,
`navigator.hardwareConcurrency` — and **a comparison requires the same machine**. Two
measurements that name the same profile on two machines are not comparable; two that name the
same *rate* are not comparable at all.

**Nominal is not effective either, and the harness prints the honest version every run.**
What to argue from is the **`x 1x` column** — a cell's median over the same cell's median
unthrottled, in the same invocation on the same machine — which is the factor the real read
felt rather than the factor a benchmark loop felt. That column carries its own warning: read
it as an order of magnitude and no finer, because the 1x cells finish in single-digit to
low-teens milliseconds, where one task boundary moves a median by a whole tick.

## What was recorded

Harness: `npm run perf:narrative`, which drives `tools/narrative-perf/`. It bundles the real
modules with the toolchain's own esbuild — the ciphers, the strict base64url decoder, the
associated-data builder and the view mapper arrive through the same module graph the
application compiles — serves them to a real Chrome over CDP, calibrates, and records.

- Chrome **152.0.7977.83**, headless.
- `navigator.hardwareConcurrency` **11**.
- BenchmarkIndex at 1x **2726** — the third part of the fingerprint, and the one that decides
  whether any of these figures may be set beside a number somebody else took.
- **9 runs per cell**, after one warm-up run that is discarded.

**NFR-003, low-tier profile, batch path, against the requirement's 100 ms:**

| fixture | cardinality | distinct opens | median | worst |
| --- | --- | --- | --- | --- |
| typical | 4 accounts, 40 payees, 25 categories, 8 groups | 277 of 1000 | 56.0 ms | 65.4 ms |
| heavy | 10 accounts, 200 payees, 40 categories, 10 groups | 460 of 1000 | 73.4 ms | 83.3 ms |
| pathological | every column distinct | 1000 of 1000 | 178.5 ms | 271.9 ms |

**The cardinalities are the measurement**, not scenery. What the batch saves is exactly the
gap between 1000 columns and the distinct values behind them, so a table of timings with no
cardinality beside them describes nothing repeatable.

***pathological* is a ceiling and not a budget anybody has.** Every one of the 1000 columns
distinct is the most work 200 rows can possibly need; no budget produces it, because a budget
names the same accounts and the same counterparties over and over.

**A single invocation is not the number.** Across six separate invocations of the harness the
*heavy* median ranged **66–95 ms**. The figures above are one invocation's; a comparison
drawn from two single runs on different days is measuring the machine's mood.

## Frame behaviour

The harness watches `requestAnimationFrame` gaps and `longtask` entries around the measured
window, and it measures the same window with no crypto running at all, under every profile.

Low-tier, idle rAF band with no crypto: **16.7–16.8 ms**.

**That band is not jitter, and reading it as jitter is how the frame column gets
over-interpreted.** 16.7–16.8 ms is one clean frame at 60 Hz: the rig missed nothing. What the
baseline measures is the renderer's own rAF **tick period**, and on the second machine it comes
back **33.4–33.5 ms on all three profiles, the unthrottled 1x one included** — a 30 Hz tick, in
a headless renderer with nothing to composite, unmoved by the throttle. Two consequences
follow, and both bound what the table below can say.

- **The instrument's resolution is one whole tick.** "1.0× the idle worst" means only that no
  tick was missed. It is not a measurement of how much of a frame the work held, and no
  arithmetic on that ratio recovers one.
- **A dropped frame is invisible on a slower rig.** The *pathological* row's 33.3 ms is one
  missed tick at 60 Hz; on a 30 Hz rig the same read reports one tick and looks perfect. A
  frame gap is readable against the band beside it and against nothing else.

| path | worst frame gap | `longtask` entries |
| --- | --- | --- |
| batch, *typical* and *heavy* | 16.8 ms — 1.0× the idle worst | none, in all 18 runs |
| batch, *pathological* | 33.3 ms | none, in all 9 runs |
| per-row, all three fixtures | 133–183 ms | one per run, covering 115–173 ms |

**Nine samples per fixture, so the counts differ per row**: the first row covers two fixtures
and therefore **18** runs, the second covers one and therefore **9**, and 27 is the three
fixtures together. On the per-row path the single long task covers essentially the whole read,
in **every one** of its 27.

**Zero long-task entries on the batch path is nearly tautological, and it is one result with
the frame gap rather than a second confirmation of it.** A `longtask` entry exists only for a
task of **50 ms or more**, and a chunk is budgeted at **8 ms** — so "no entries" is what the
design *guarantees* wherever the yields work at all, not independent evidence that they do.
What carries the claim is the rAF gap beside it, read against the idle band. The per-row long
tasks are the other half and are real evidence: one per run, covering essentially the whole
read, reproduced on both machines.

**That is what the frame budget buys, and it is the only thing it buys.** On the two shapes a
budget actually has, the batch path holds the main thread for no longer than an idle tick
does, and no long task is reported over the whole read — the second following from the first
rather than adding to it. It buys nothing about *when* the list appears: the frame is handed
back so the page keeps answering while the opens run, not so that rows arrive sooner.

## What de-duplication bought, and where it costs

Low-tier medians, batch against per-row:

| fixture | batch | per-row | saved |
| --- | --- | --- | --- |
| typical | 56.0 ms | 127.6 ms | 71.6 ms |
| heavy | 73.4 ms | 147.6 ms | 74.2 ms |

**Both columns are this machine's, and the "before" figure is exactly as machine-local as the
"after".** On the second machine the per-row path met the requirement's 100 ms on its own —
*typical* 69.7 ms, *heavy* 74.0 ms, *pathological* 73.0 ms — so how much this change was worth
is a fact about a machine before it is a fact about the read. Quoting 127.6 ms as *before* owes
the fingerprint beside it.

**On *pathological* the batch path is slower, and that is the trade rather than a defect** —
178.5 ms against the per-row path's 144.5 ms, because where nothing repeats there is no
duplicate to skip and the batch pays for its bookkeeping anyway.

**What costs is the de-duplication bookkeeping, and not the frame budget serialising chunks
the per-row path runs in one burst.** That was the stated cause, and measuring it refuted it.

**The variant sweep below was taken on the second machine, not the one every table above
was recorded on** — BenchmarkIndex **1402** at 1x against 2726, the same rig, within
noise, whose independent run read *typical* **30.5** and **26.3 ms** against the 56.0
above. That is why its absolute penalties do not line up with the tables, and quoting them
without their rig was this chapter breaking the rule the section before it establishes.
**The shapes reproduce on both rigs; only the milliseconds are machine-local.** The sweep
was taken as one set, so its differences are within-invocation comparisons on one
machine — which is the honest kind, and the whole of what survives here.

- **One burst of all 1000 opens is no faster than sixteen at a time** — 82.4 ms against
  78.1 ms on that rig, inside the noise. Concurrency beyond a chunk buys nothing here, so
  serialising the chunks cannot be what the ceiling pays for. That non-result is what
  kills the frame-budget explanation, and it does not depend on which machine took it.
- **The penalty survives taking the yields away.** Low-tier on that rig, batch minus
  per-row: the shipped arrangement **+14.5** and **+18.0 ms** over two invocations; a
  variant that never yields **+4.3 ms**; one burst **+6.3 ms**; a 2 ms budget **+6.1 ms**.
  Read the ordering; the milliseconds are that rig's.
- **It reproduces unthrottled, in every invocation of that sweep, at +0.5 to +0.9 ms** —
  the bookkeeping is the one term always present, scaling by the profile's factor into the
  +4–6 ms the no-yield variants show.

The work behind it is what *pathological* makes maximal: collecting 1000 requests, 1000 key
joins to build the map, and about 1000 more to read it back, over a set in which not one of
them is a duplicate to be skipped.

**The frame budget's own share cannot be separated from the noise, and this chapter's own rule
is what says so.** On that same rig the untouched `per-row` *pathological* cell ranged
**68.8–76.1 ms** across five invocations, so a 14 ms attribution drawn by differencing two
invocations sits inside the spread of a cell nobody touched — the reading *A single
invocation is not the number* above already forbids. **Wall clock was surrendered for a
held frame, deliberately**, and that trade is real and still worth taking; what was wrong
was the mechanism named for it, guessed and then written in the voice of the measurements
around it. It is recorded here so that whoever measures the ceiling next reads an accepted
trade rather than a regression, and does not "fix" it by deleting the yield.

## This is never a CI gate

**The harness records and never asserts.** Its exit code answers one question — did the
measurement happen — so a missing Chrome, a bundle that would not build or a page that threw
is a non-zero exit, and a slow number is not. **A hang is the one way that exit code can lie,
so nothing in the harness waits without a deadline**: every DevTools command has one, the
launch fails the moment Chrome exits without announcing an endpoint, and the cleanup drops
the browser's connection rather than waiting for it to end politely.

**It does not run in CI, and a perf gate there would be worse than no gate.** CI is a shared
runner. A threshold that goes red on a noisy neighbour is retried, then widened, then
ignored — and the widened, ignored version is worse than an absent one, because it looks like
coverage while catching nothing.

**So nothing automatic catches these numbers going stale.** What is done instead is to record
the browser version, the calibration and the machine's own BenchmarkIndex beside every figure,
so a reader can see what the number was true of — and, the profile not transporting, whether
their own re-run is a comparison at all. Re-running the harness is a deliberate act, taken when
the read path changes.

## What the tests hold, and what they do not

The mechanism is pinned deterministically, and the timings are not pinned at all. Three
specs hold the mechanism — `narrative-batch.spec.ts` (17 cases), `transaction-view.spec.ts`
and `transactions.service.spec.ts`:

- de-duplication happens — two requests naming the same binding *and* the same wire are one
  open;
- the clock is spent as **elapsed time** rather than as a count of opens, so the yield
  decision is about how long the frame has been held and not about how much was done in it;
- the tail is never dropped — every request in the list is in the answer;
- every row keeps **its own** counterparty, which is the failure a key spelled loosely would
  produce;
- the **default frame budget** stays inside the band where chunking means anything — **4 to
  16 ms**, asserted as a count of yields over a known workload on a fake clock only an open
  moves, so `8` may be re-tuned honestly and cannot quietly become `1`, `200` or `5000`;
- the frame goes back through `postTask` and never through `scheduler.yield()`, and the
  `user-visible` priority is held by the literal type on the module's own `PostTaskScheduler`
  interface, so editing it is a compile error rather than a red case;
- the **`MessageChannel` fallback** really is a channel — a case stubs `scheduler` absent and
  counts constructions, so replacing it with the rejected `setTimeout(…, 0)` reddens that
  case;
- the service delegates to `openNarrativeBatch` rather than rolling a map of its own.

Two limits, each of which reads as an oversight to whoever finds it:

- **No wall-clock assertion exists anywhere in the suite, deliberately.** Nothing in `npm
  test` will notice the read getting slower. The reason is the CI argument above, and the
  remedy is not a threshold in a spec — a spec runs on the same shared runners.
- **The harness is not a spec and is not collected as one.** It is a Node entry point under
  `tools/`, run by its own script.

## What the harness does not measure

It measures the AEAD open path, the strict base64url decode, associated-data construction,
and mapping into 200 view models. It does **not** measure:

- Angular change detection or template rendering;
- HTTP, or parsing the JSON the columns arrived in;
- memory pressure or thermal throttling;
- any engine that is not Chrome.

And the **headless idle band is optimistically flat** compared to a real device: a renderer
with nothing to composite is not a phone with a screen to paint. The frame-gap comparison is
sound against its own baseline and is not a claim about a handset.

## How NFR-003 is read against these numbers

NFR-003 asks that encryption add no more than 100 ms to rendering. **The figures above are
the total read cost, not a delta**, because no no-crypto control render was measured — there
is no unencrypted list in this product to render. Reading them as a delta is therefore the
conservative direction: the true added cost is the total minus whatever an unencrypted render
of the same rows would have cost, which is greater than zero.

On the two shapes a budget actually has, the whole read finishes inside the requirement's
budget on **this machine's** calibrated low-tier profile, and it does so without a long task.
The ceiling exceeds it, is reported here, and is not a shape anybody's ledger produces.

**Which machine that was read on is part of the claim, and the requirement is met more easily
on a slower one.** Every median measured on the second machine — both paths, all three fixtures
— fell inside the budget, the ceiling included, for the reason the profile section gives: a
weaker measuring machine calibrates to a gentler throttle and its crypto runs closer to full
speed. So "it fits" is a statement about a rig, and the conservative reading of it is the one
above: the total is being read as a delta, on the machine that recorded the largest totals
available.

See also [frontend testing](frontend-testing.md), which owns what the suite runs and why it
needs a build first.
