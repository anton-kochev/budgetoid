# Security Header Invariant

> Read this before editing `public/staticwebapp.config.json`, `src/index.html`, the `optimization`
> block in `angular.json`, or the middleware order in `Api/Program.cs`.

**Both origins tell the browser what they permit, and they tell it on every response.** The web
application ships `Content-Security-Policy`, `Strict-Transport-Security` and `Referrer-Policy`; the
API ships those three plus `X-Content-Type-Options`. [No third-party
origins](no-third-party-origins.md) is a property of what the builder emitted, checked once at build
time; these headers are the same boundary enforced by the agent that runs the code, on a document
the builder no longer controls. A defect that injects markup at runtime is invisible to the first
and refused by the second.

## The web application

The three headers live in `globalHeaders` of
`ClientApp/angular-budgetoid/public/staticwebapp.config.json`:

```
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self' https://api.budgetoid.app https://accounts.google.com https://www.googleapis.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'
Strict-Transport-Security: max-age=63072000; includeSubDomains
Referrer-Policy: no-referrer
```

That file is the deployed configuration rather than a source for one. `angular.json` copies
everything under `public/` verbatim into `dist/angular-budgetoid/browser`, and
`.github/workflows/deploy.yml` uploads that directory with `skip_app_build: true`, so nothing
between the repository and Azure rewrites it.

### `globalHeaders`, not a route rule

The rejected mechanism is a `/*` entry in `routes[]` carrying the same three headers, which is the
shape most Static Web Apps examples show. Azure's configuration reference says two things that
combine badly here: `globalHeaders` and a route's `headers` are **unioned**, with the route winning
per header *name*; and route rules are not applied at all to a request that triggers
`navigationFallback`. Every deep link in this application is a `navigationFallback` rewrite to
`/index.html`, so a route rule would ship the headers on the root document and drop them on every
URL a person actually lands on — present where anybody would check, absent where it matters.

The union is what lets the one existing route rule stay untouched. Verified against the real
runtime, `npx @azure/static-web-apps-cli start dist/angular-budgetoid/browser`: all three headers on
`/`, all three on `/app/settings`, and `/fonts/*` answering with its own `Cache-Control: public,
max-age=31536000, immutable` **plus** all three. The font route needed no change and must not grow
one.

### `style-src 'unsafe-inline'` is the one concession

Angular emits component styles inside the JavaScript chunks and injects them as `<style>` elements
at runtime; Angular Material and the CDK inject overlay styles the same way. Both alternatives to
the token are unavailable rather than merely worse: no hash covers an element created after the
response was written, and a nonce requires a server that mints one per response, which Static Web
Apps — serving static files with static headers — is not. The requirement's letter constrains
*origins*, and `'unsafe-inline'` names none, so it is met; its spirit is weakened for styles, and
saying otherwise would be dishonest.

What keeps that weakening bounded is that a CSS injection cannot become an **exfiltration** channel.
Every fetch a stylesheet can start is policed by a separate directive: `background: url(…)` by
`img-src 'self'`, an `@font-face src` by `font-src 'self'`, and a remote `@import` by `style-src
'self'` — the `'unsafe-inline'` token admits an inline declaration, never a remote import. What
remains is UI redress and selector snooping, neither of which has a way to get a byte off this
origin.

### `connect-src`, and the two origins it names

`connect-src` governs fetch destinations rather than subresources, so the origin rule does not reach
it. It admits the API, `accounts.google.com` for the OpenID discovery document, and
`www.googleapis.com` for the JWKS that document points at — the same two fetches
[no-third-party-origins.md](no-third-party-origins.md) already records as gaps it cannot close.
Both entries leave with federated sign-in. `oauth2.googleapis.com` is deliberately absent: the
application runs the implicit flow, so no request reaches a token endpoint.

`img-src` carries no `data:`, verified against the emitted CSS and JavaScript, which contain none.
`data:` is the token a reader adds to make one inlined icon work; it is not an origin, and it admits
every attacker-controlled byte string as an image.

### Two years of HSTS, and no `preload`

`max-age=63072000; includeSubDomains` is two years. The rejected addition is `preload`: submission
to the browsers' preload list is effectively irreversible, and it would commit every future
subdomain of a domain that hosts no production environment yet. The spec asserts the one-year floor
the requirement names rather than the value that ships, so raising the value never reddens and
dropping below a year always does.

There is no `X-Frame-Options`. Every browser that runs Angular 21 honours `frame-ancestors`, and a
browser honouring both ignores the older header — so it would be a second spelling of one rule, able
to disagree with the first and unable to change any outcome.

## Making `script-src 'self'` literally true

`script-src 'self'` refuses an inline `<script>` and an inline event handler, so the document had to
stop containing either before the header could ship. Two constructs were in the way, and both came
from the builder rather than from a source file. The mechanism is read out of the installed
builder's own source, `@angular/build@21.2.17`, not out of documentation:

- `src/utils/index-file/index-html-generator.js:34` adds the critical-CSS plugin **only** when
  `optimization.styles.inlineCritical` is set.
- That plugin is Beasties' only caller here, and constructs it with `preload: 'media'` and
  `noscriptFallback: true` (`inline-critical-css.js:89-90`) — which is exactly the `media="print"`
  plus `onload="this.media='all'"` pair and its `<noscript>` twin. The file names the pattern it
  later has to undo, in `MEDIA_SET_HANDLER_PATTERN` on line 19.

So `"inlineCritical": false` in the production `optimization` block removes the inline `<style>`, the
inline event handler and the duplicated `<noscript>` markup in one move. [ADR
0020](../decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md) holds the
argument for paying that first-paint cost and names the alternatives it refuses.

The hand-written theme pre-paint script moved out of `<head>` into
`ClientApp/angular-budgetoid/public/theme-prepaint.js`, referenced with **no `defer` and no
`type="module"`**. Either attribute postpones execution past first paint, which is the one thing the
script exists to beat; the rejected alternative, pinning a `'sha256-…'` of the snippet in the
policy, puts the hash and the snippet in two files that drift the first time either is edited, with
a theme flash as the only symptom.

### The `optimization` object is written out in full, and has to be

`optimization` was previously unset and defaulted to `true`. The builder's
`normalize-optimization.js` reads `scripts: !!optimization.scripts`, so an `optimization` **object**
that omits `scripts` normalizes to `scripts: false` and silently stops minifying — a production
build that is larger and slower with nothing red anywhere. Every key is therefore spelled out in
`angular.json` rather than assumed.

### What the trade cost, measured

`index.html` fell from 12,819 B to 1,463 B. The 19,005 B `styles-*.css` became render-blocking
instead of deferred. `main-*.js` is still hashed and still minified, and the production `initial`
budget is unchanged at 403 kB. A cold first paint costs one extra round trip rather than two,
because the script and the stylesheet are discovered in the same head parse and fetched in parallel
on a connection that is already open. Warm visits improve: those 11 kB of critical CSS previously
rode along with every navigation, since the SPA shell served through `navigationFallback` cannot be
cached hard.

## Silent refresh is refused, and that is not a regression

`+core/services/auth-service.ts` calls `setupAutomaticSilentRefresh()`, which works through a hidden
iframe. Both legs of that iframe are now refused, for two different reasons: the outbound one frames
Google, governed by `frame-src` falling back to `default-src 'self'`; the return one redirects into
this origin, which `frame-ancestors 'none'` refuses — framed by its own origin is still framed.

The feature was **already broken** before the policy shipped, because there is no
`silent-refresh.html` for the iframe to load. The policy turns an existing silent defect into a
loud one. The correct response to the console error is not to loosen the policy: the caller leaves
with federated sign-in, and `auth-service.ts` was deliberately not touched.

## The API

`api.budgetoid.app` is a separate origin, and it set no response header at all.
`BudgetoidApp/Api/Infrastructure/SecurityHeadersMiddleware.cs` writes four, unconditionally, on
every response:

```
Strict-Transport-Security: max-age=63072000; includeSubDomains
Content-Security-Policy:   default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'
Referrer-Policy:           no-referrer
X-Content-Type-Options:    nosniff
```

**The write happens from a `Response.OnStarting` callback, and that was measured rather than
assumed.** The simple version — writing the headers directly before `await next(…)` — was
implemented first, and `AResponseFromAnExceptionHandler_CarriesTheHeaders` failed against it with
all four header *names* present and every *value* empty, while the other five tests passed. That is
the signature of `ExceptionHandlerMiddleware` calling `HttpResponse.Clear()` before handing the
exception to the registered handlers: not "the middleware did not run", but "the response was
cleared after it ran". Registering outermost does not repair it, because the clear happens below
this middleware and after it has already written. `OnStarting` fires after any such clear and just
before the flush, which is the only point at which *written* and *on the wire* mean the same thing.

**Not `UseHsts()`.** That helper keys on `Request.IsHttps`, which is `false` behind Azure Container
Apps ingress because nothing in this repository configures forwarded-headers middleware — so it
would emit nothing in the one environment that needs it. Writing unconditionally is inert locally,
since RFC 6797 requires a user agent to ignore an HSTS header received over plain HTTP, and it keeps
the shipped header set the same one the tests exercise.

**Each header is assigned through the indexer, never appended.** Two appenders would ship a
duplicate `Strict-Transport-Security`, and RFC 6797 has a user agent process the first and ignore
the rest — a duplicate is a silent downgrade to whichever copy arrives first, not belt-and-braces.

**`/health` gets no exemption**, unlike the first-party header control. That exemption exists
because the platform's probe cannot be taught to *send* a request header of ours. Response headers
run the other way: the probe receives them and ignores them, so an exemption would make one route
differ from every other for no reason anybody could state.

**The tripwire.** The day somebody adds a Swagger or Scalar UI, `default-src 'none'` blanks it — no
script, no stylesheet, no font. The right answer is to override the header on that one endpoint,
never to loosen the global policy so a documentation page can render. Today `MapOpenApi()` is
Development-only and serves JSON, so no HTML UI exists here to break.

**`X-Content-Type-Options: nosniff` maps to no requirement of the story that added it.** It is a
rider: present on the API, absent from the web application's header set. The asymmetry is recorded
rather than tidied away, because a reader who assumes both origins ship the same four will look for
a defect that is not there.

## What holds the line

`ClientApp/angular-budgetoid/src/security-headers.spec.ts` reads the **emitted**
`staticwebapp.config.json` and `index.html` from `dist/angular-budgetoid/browser`, so it needs a
prior `npm run build` — the same Build-before-Test ordering `no-external-origins.spec.ts` already
imposes. It parses the policy into directives and compares **source sets**, never the whole header
string: a string-equality assertion reddens on a harmless reordering, and the cheapest way to green
that is to paste the actual value over the expected one, at which point the test asserts only that
the header equals itself. Adding `'unsafe-inline'`, `'unsafe-eval'`, a hash or a host to
`script-src` fails it by name; so does dropping `frame-ancestors`, which has its own test because it
has no `default-src` fallback; so does an unreviewed `connect-src` entry, and so does a `max-age`
below one year. Its last test derives its own condition from the shipped policy — *if `script-src`
withholds `'unsafe-inline'`, then the emitted document contains no `<script>` without a `src` and no
`on*=` attribute* — so re-enabling `inlineCritical` reddens it without anybody having written down
that `inlineCritical` exists. It also fails on any security header declared on a route rule.

`BudgetoidApp/tests/IntegrationTests/SecurityHeaderTests.cs` is organised by **which part of the
pipeline wrote the response**, not by which header is checked, because the responses a header test
usually misses are the ones no route delegate wrote: an `IExceptionHandler` 500, a `/health` 200, a
401 from the authentication challenge, and a 403 from a middleware that never calls `next`. Each of
its six tests names the smallest production change that turns it red — moving the registration below
`UseExceptionHandler()`, moving it below `FirstPartyRequestMiddleware`, gating the write on the
outgoing status, removing one entry from the header dictionary. The values are read off
`SecurityHeadersMiddleware.Headers` rather than retyped, so a rename cannot leave a test passing
against a header nobody emits; the one exception is `HstsMaxAge_IsAtLeastOneYear`, which writes the
header name itself and parses the directive, so that shortening the value cannot be silenced by
editing a literal in the same commit.

## What none of this proves

- **That Azure emits any of it.** No test in this repository can reach Azure Static Web Apps or
  Azure Container Apps. The specs prove the configuration and the middleware ship with these names
  and these values; the runtime evidence above comes from the Static Web Apps CLI against the build
  output. There is no production environment today, so nothing here has been observed in production.
  `DEPLOYMENT.md`'s verification step is where that gap is closed, by hand, per deploy.
- **That the policy is the right policy.** A directive set both files agree on is still a directive
  set, and a source added to both in one commit passes everything here except the reviewed-origins
  list in the frontend spec.
- **That a browser enforces what it is told.** `frame-ancestors`, `'unsafe-inline'` and HSTS are all
  agent-side behaviours, and an agent that ignores one fails nothing anywhere.
- **The API's response bodies.** These headers govern how a browser treats a response; the tenancy
  of what is in it belongs to [data isolation](data-isolation.md) and is unaffected.
- **`connect-src` against runtime behaviour.** The spec compares the policy's source list with the
  `apiBaseUrl` in `assets/app-config.json`. A library that learns a URL at runtime — which is
  exactly how the JWKS endpoint is reached — appears in no bundle and in no assertion, and shows up
  only as a blocked request in a browser.
