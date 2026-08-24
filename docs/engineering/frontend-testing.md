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

## Build before test

**`npm test` needs a prior `npm run build`.** Several specs assert over the emitted production
bundle rather than over `src/`, because the sources are not what the browser runs — the builder
inlines, rewrites and tree-shakes on the way out, and a rule about what *ships* can only be proven
against the output. **Which specs those are is a property, not a list: every spec that imports
`expectProductionBuild` from `src/production-bundle.ts`** — a naming that outlives the next one
added. Today it holds `no-external-origins.spec.ts` and `no-devtools.spec.ts` (see
[no third-party origins](no-third-party-origins.md), which owns their argument),
`focus-ring.spec.ts`, `security-headers.spec.ts`, `no-profile-scope.spec.ts` and
`registration-redirect-uri.spec.ts`.

They share one precondition through `src/production-bundle.ts` rather than restating — and drifting —
in each of them. `expectProductionBuild()` fails with the command to run when `dist/` is missing,
**and** requires a hashed `main-*.js` in the emitted `index.html`: `outputHashing: all` is
production-only, so an unhashed entry point means a development build is sitting in `dist/` and the
whole set would pass too easily.

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
