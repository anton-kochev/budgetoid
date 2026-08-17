# ADR 0020 — Trade inlined critical CSS for a literal `script-src 'self'`

- **Status:** Accepted — in force in the production build and in the configuration the deploy
  workflow uploads. There is no production environment, so nothing below has been observed served
  from Azure: the runtime evidence comes from the Static Web Apps CLI run against the build output,
  and the repository's own guarantee stops at what the builder emitted.
- **Date:** 2026-08-17
- **Area:** Frontend build / Security (`Content-Security-Policy`, Angular build optimization, Azure
  Static Web Apps configuration)

## Context

The web application's `Content-Security-Policy` is only worth having if the document it guards can
load under it. `script-src 'self'` refuses two things outright: a `<script>` element with no `src`,
and an `on*=` attribute. Ship the header over a document containing either and the page does not
degrade — it breaks, in production only, on the first deploy.

The production build contained both, and neither came from a source file. Angular's default
`optimization` inlines critical CSS, and the mechanism is worth stating exactly, because a reader
who believes this is a matter of taste will turn it back on. Read from the installed builder,
`@angular/build@21.2.17`, rather than from documentation:

- `src/utils/index-file/index-html-generator.js:34` pushes the critical-CSS plugin into the pipeline
  **only** behind `options?.optimization?.styles?.inlineCritical`.
- That plugin is the only caller of Beasties here, and constructs it with `preload: 'media'` and
  `noscriptFallback: true` (`inline-critical-css.js:89-90`). Those two options *are* the pattern:
  the stylesheet link is rewritten to `media="print"` with `onload="this.media='all'"`, the extracted
  rules are written into an inline `<style>`, and a `<noscript>` twin of the original link is added
  for agents without scripting. The file names the shape it later has to undo, in
  `MEDIA_SET_HANDLER_PATTERN` on line 19.

So one flag produced an inline `<style>`, an inline event handler and a duplicated `<link>`. The
third is harmless under the policy; the first two are not.

The document also carried one hand-written inline script: the theme pre-paint, which reads
`localStorage['budgetoid-theme']` and sets `color-scheme` on the root element before anything is
painted, so that a person who chose dark does not see a white flash. It exists precisely because it
runs early, which rules out every deferral.

The failure mode of getting this wrong is what makes it a decision rather than a configuration
change. `onload="this.media='all'"` is the line that promotes the stylesheet from `print` to `all`.
Blocked by the policy, it never runs, the stylesheet stays scoped to print media forever, and the
application ships **unstyled** — a total visual failure, with nothing in the response and nothing in
any test naming the cause.

## Decision

**Critical-CSS inlining is off in the production build, and the policy says `script-src 'self'` with
no hash, no nonce and no `'unsafe-inline'`.**

`"inlineCritical": false` in the production `optimization` block of `angular.json` removes the
inline `<style>`, the inline event handler and the `<noscript>` duplicate in one move. The theme
pre-paint moves to `ClientApp/angular-budgetoid/public/theme-prepaint.js` and is referenced from
`<head>` with **no `defer` and no `type="module"`** — either would postpone execution past first
paint, which is the one thing the script exists to beat.

The `optimization` object is written out in full rather than reduced to the one key that changed.
`optimization` was previously unset, defaulting to `true`; the builder's `normalize-optimization.js`
reads `scripts: !!optimization.scripts`, so an object that omits `scripts` normalizes to
`scripts: false` and silently stops minifying. A production build that quietly grew and nothing red
anywhere is a worse outcome than the one this ADR is about.

**The stylesheet is now render-blocking, and that is the thing being bought and paid for.** It is
not a side effect to be optimised away later by a different mechanism that reintroduces an inline
script.

## Alternatives considered

**Keep `inlineCritical` and add `'unsafe-hashes' 'sha256-…'` for the `onload` attribute.** The
standards-shaped answer, and refused on its failure mode rather than on principle. `'unsafe-hashes'`
has patchy history across agents; an agent that ignores the token blocks the handler, the stylesheet
stays at `media="print"`, and the application ships **unstyled** for that agent alone — silent,
total, and browser-specific, which is the hardest class of defect to reproduce from a bug report. It
also pins a hash of a string the builder generates, so a builder upgrade that changes one character
of the handler breaks the site with a green build.

**Keep the theme pre-paint inline and pin a `'sha256-…'` of the snippet.** Sound in principle and
rejected on drift: the hash and the snippet live in two files, and nothing fails when they disagree.
The symptom is a theme flash — cosmetic enough that it survives review and long enough to be blamed
on something else. Moving the snippet to a file makes `script-src 'self'` cover it with no second
place to keep in step.

**Angular 21's `security.autoCsp`.** It emits a `<meta http-equiv="Content-Security-Policy">` policy
into the document. That does not replace the header — it intersects with it, so the effective policy
becomes the conjunction of two values maintained in two places, and the tighter of them wins in ways
neither file states. `frame-ancestors` is ignored when delivered in a `<meta>` element, so the
header is required regardless, and the clickjacking half of the requirement could not move there
even if the duplication were acceptable.

**`ngCspNonce`.** A nonce is a per-response value, and Azure Static Web Apps serves static files with
static headers: there is no server here to mint one. A nonce baked into a file at build time is a
public constant that every attacker reads out of the document, which is a token spelled like a
control.

**Ship `style-src 'unsafe-inline'` and let the inline `<style>` through.** This is what actually
happens for styles, for a reason that does not extend to scripts: Angular injects component styles
as `<style>` elements at runtime, Material and the CDK inject overlay styles the same way, and no
hash covers an element created after the response was written. But the concession buys nothing for
critical CSS specifically — the blocking construct is the `onload` handler, which `style-src` does
not govern. Loosening `script-src` instead is the trade this ADR exists to refuse.

**Serve the frontend from something that can set per-response headers.** A real answer to the nonce
problem, and out of proportion: it swaps a static host for a running one, in a repository whose
frontend deploy is a directory upload, to recover roughly one round trip on a cold first paint.

## Consequences

- **First paint costs one extra round trip on a cold visit, not two.** `index.html` fell from
  12,819 B to 1,463 B, and the 19,005 B `styles-*.css` is now render-blocking. The script and the
  stylesheet are discovered in the same head parse and fetched in parallel on the connection already
  open, so the cost is one round trip's latency rather than a serialised chain.
- **Warm and repeat visits improve.** Those 11 kB of critical CSS previously rode along with every
  navigation, because the SPA shell served through `navigationFallback` cannot be cached hard, while
  the hashed stylesheet can be cached forever. The trade is not one-directional.
- **The build's other outputs are unchanged.** `main-*.js` is still hashed and minified and the
  production `initial` budget is unchanged at 403 kB, which is the check that the full
  `optimization` object did not quietly disable something else.
- **The rule is held by a test that never names `inlineCritical`.**
  `ClientApp/angular-budgetoid/src/security-headers.spec.ts` derives its condition from the shipped
  policy: if `script-src` withholds `'unsafe-inline'`, the emitted `index.html` contains no
  `<script>` without a `src` and no `on*=` attribute. Turning inlining back on reddens it through
  the markup rather than through the flag, which is what makes the guard survive a future builder
  that produces the same shape by another route.
- **A public asset is now load-bearing.** `public/theme-prepaint.js` is copied verbatim into the
  build output and referenced by path; deleting or renaming it fails nothing here, and shows up as a
  404 in `<head>` and a theme flash. Its own comment carries the reason it is a file.
- **Re-enabling inlining for a first-paint score is the recurring temptation, and it has one honest
  form.** Not a hash, not a nonce: an inlining strategy that emits no script and no event handler at
  all. Until such a mechanism exists in the builder, the answer is no, and this record is what a
  reader is expected to argue with rather than around.
