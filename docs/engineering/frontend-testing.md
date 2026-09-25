# Frontend Testing Invariant

> Read this before adding a spec to `ClientApp/angular-budgetoid`, or before wondering why the suite
> fails on a clean checkout.

## Where a spec lives and what it runs under

Specs sit **next to their subject** as `*.spec.ts`, and the `test` target in `angular.json` is the
`@angular/build:unit-test` builder with `runner: "vitest"`. It builds the **development**
configuration, type-checks against `tsconfig.spec.json`, and loads `src/test-setup.ts` as its one
setup file. No browser is configured, so the DOM under a spec is jsdom's — which is exactly why the
two modules that touch `navigator.credentials` and the file-save API are injectable services rather
than free functions: the platform behind them does not exist here, and a seam lets a screen stub the
service instead of the platform.

**Test globals are imported explicitly from `vitest`** — `describe`, `it`, `expect`, `vi` — in every
spec. Nothing configures the runner to inject them, so an omitted import is a `ReferenceError` at
run time. Note what does **not** hold the convention: `tsconfig.spec.json` lists `vitest/globals` in
its `types`, so a spec relying on an ambient `describe` type-checks perfectly and fails only when it
runs. The compiler is not the guard here; the house style is.

Test bodies carry `// Arrange`, `// Act`, `// Assert`, and one with fewer phases to name carries
only the ones it has — each bundle-reading spec opens with a case whose whole body is
`expectProductionBuild()` and carries none. Nothing enforces this: `eslint.config.js` declares no
rule for it and the runner has no opinion, so it is held by review, like the import above it.

**A spec that touches `localStorage` clears it in a `beforeEach`, and owes no `afterEach`.**
Measured: jsdom's store is fresh per test **file** and shared across every case inside one, so a
value written by one case is an invisible fixture for the next and nothing carries between files.
The `beforeEach` is therefore the one that is owed — `rotation-epoch-record.spec.ts` is the shape —
and an `afterEach` clear would be a second wipe of a store the next file never sees. Note the
neighbouring rule it is **not**: `restoreMocks` is unconfigured, so a spy installed on
`Storage.prototype` really does survive from case to case within a file and really does need
`vi.restoreAllMocks()` in an `afterEach`. Two different lifetimes, opposite obligations, and one
spec commonly needs both.

## Build before test

**`npm test` needs a prior `npm run build`.** Several specs assert over the emitted production
bundle rather than over `src/`, because the sources are not what the browser runs — the builder
inlines, rewrites and tree-shakes on the way out, and a rule about what *ships* can only be proven
against the output.

**Which specs those are is a property, and this chapter carries the property rather than a roster of
them: it is every spec that imports `expectProductionBuild` from `src/production-bundle.ts`.** That
import is the membership test and the only one — a spec joins the set by writing it and leaves by
deleting it, with nothing to update here either way. A list beside that would be a second copy of a
fact the import already states, kept in step by nobody and reddening nothing when the two disagree,
and the copy that rots is always the written one: a spec is added to `src/` without a chapter two
directories away being opened. What a reader wanting the set should do is search for that identifier,
which answers correctly on the day it is asked. The arguments for the individual rules live with the
rules rather than here: [no third-party origins](no-third-party-origins.md) owns two of them, and
[accessibility](../design/accessibility.md) and [color](../design/color.md) each own one.

They share one precondition through `src/production-bundle.ts` rather than restating — and drifting —
in each of them. `expectProductionBuild()` fails with the command to run when `dist/` is missing,
**and** requires a hashed `main-*.js` in the emitted `index.html`: `outputHashing: all` is
production-only, so an unhashed entry point means a development build is sitting in `dist/` and the
whole set would pass too easily.

**Not every one of them is a bundle *scan*, which is why "reads the production build" is the
membership test rather than "greps the output".** One of them stands a real component up in jsdom
and injects the emitted global stylesheet into the page so the cascade it measures is the one a
browser is served; it needs `dist/` for that reason rather than to read strings out of it. A rule
worded around scanning would have quietly excluded it.

Hence build before test in CI, and `npm run build && npm test` locally.

## The runner's time zone is pinned

`src/test-setup.ts` sets `TZ` to **`Pacific/Kiritimati`**, and deleting that line disables a guard
rather than freeing a constraint.

`export-filename.spec.ts` proves `exportFilename` reads the UTC getters rather than the local ones,
by naming a file for two instants either side of midnight UTC. That pair discriminates only on a host
whose local zone is **not** UTC — and CI is `ubuntu-latest`, whose `TZ` is UTC. Without the pin the
guard is disabled on precisely the machine that gates merges, and a local-getter implementation sails
through it.

`Pacific/Kiritimati` is UTC+14, the largest standard offset in the database, and it keeps no daylight
saving — so the pin cannot drift mid-year and make an expected filename depend on which half of the
year the suite ran in.

**The guard on the guard is a test.** `runs in a zone where a local-getter implementation can be
caught` asserts `getTimezoneOffset()` is not `0`, so removing the pin goes red instead of going
quietly green. Nothing else in the suite is zone-sensitive: the transactions spec builds a local
`Date` and serializes it with local getters, so it is zone-invariant and the pin does not disturb it.

`test-setup.ts` must **not** be named `*.spec.ts`, or the runner collects it as a test file
containing no tests.

## The performance harness is deliberately not a spec

**No spec in this suite asserts a wall-clock duration, and none may.** The narrative read's
cost is measured by a harness that lives outside `src/` — `tools/narrative-perf/`, run by
`npm run perf:narrative` — which drives a real Chrome over CDP, calibrates that machine
against a reference device, and **records** what it finds. Its exit code answers whether the
measurement happened, never whether a number was small enough.

Three reasons it is not a spec, and none of them is that it would be awkward to write:

- **A timing assertion in `npm test` runs on the same shared machine every other case does.**
  It goes red on a noisy neighbour, gets retried, then widened, then ignored — and a widened,
  ignored threshold is worse than no threshold, because it looks like coverage.
- **jsdom is the wrong subject.** The suite runs with no browser configured, so there is no
  compositor, no `longtask` observer worth reading and no `requestAnimationFrame` cadence to
  compare against. The one thing worth measuring here — whether the main thread is handed
  back inside a frame — has no meaning in this runner.
- **A number is not comparable without the machine it was taken on.** The harness prints the
  Chrome version and the calibrated profile beside every figure precisely because a rate or a
  millisecond quoted alone is unreadable; a spec constant carries neither.

What the suite *does* hold is the **mechanism**, deterministically and with no clock:
de-duplication happens, the frame budget is spent as elapsed time rather than as a count of
opens, the tail is never dropped, and every row keeps its own value. Those cases are in
`narrative-batch.spec.ts`, `transaction-view.spec.ts` and `transactions.service.spec.ts`.
The consequence is worth stating plainly: **nothing in `npm test` will notice the read
getting slower.** [frontend performance](frontend-performance.md) owns the numbers, the
calibration, and the argument for keeping them out of CI.
