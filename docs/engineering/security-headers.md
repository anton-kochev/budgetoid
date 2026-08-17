# Security Header Invariant

> Read this before editing `public/staticwebapp.config.json`, `src/index.html`, the `optimization`
> block in `angular.json`, or the middleware order in `Api/Program.cs`.

**Both origins tell the browser what they permit, and both name the same four headers:**
`Content-Security-Policy`, `Strict-Transport-Security`, `Referrer-Policy` and
`X-Content-Type-Options`. [No third-party origins](no-third-party-origins.md) is a property of what
the builder emitted, checked once at build time; these headers are the same boundary enforced by the
agent that runs the code, on a document the builder no longer controls. A defect that injects markup
at runtime is invisible to the first and refused by the second.

**Only one of the two origins delivers them on *every* response, and the difference is not a
detail.** The API writes its four from a middleware that runs on every request, so a 500, a 401 and a
403 carry them exactly as a 200 does. The web application's four are `globalHeaders` in a static
host's configuration, and Azure Static Web Apps applies `globalHeaders` to the responses it serves
*from the content* — not to the ones it synthesizes. A request for a file that does not exist is the
measured counterexample: `GET /does-not-exist.js` answers **404** with `Content-Type: text/html` and
**none** of the four. `navigationFallback.exclude` is what creates those responses — it deliberately
keeps `*.{json,css,js,…}` out of the SPA rewrite, so a missing asset 404s instead of being answered
with `index.html`. Nothing in this repository can close that gap: there is no configuration key for
the headers on a synthesized 404, and the body is a host-generated page rather than one of ours. It
is recorded here rather than papered over, because "on every response" is what a reader will
otherwise assume from the API half. The measurement comes from the Static Web Apps CLI, with the
limits the next section gives.

## The web application

The four headers live in `globalHeaders` of
`ClientApp/angular-budgetoid/public/staticwebapp.config.json`:

```
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self' https://api.budgetoid.app https://accounts.google.com https://www.googleapis.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'
Strict-Transport-Security: max-age=63072000; includeSubDomains
Referrer-Policy: no-referrer
X-Content-Type-Options: nosniff
```

That file is the deployed configuration rather than a source for one. `angular.json` copies
everything under `public/` verbatim into `dist/angular-budgetoid/browser`, and
`.github/workflows/deploy.yml` uploads that directory with `skip_app_build: true`, so nothing
between the repository and Azure rewrites it.

### `globalHeaders`, not a route rule

The rejected mechanism is a `/*` entry in `routes[]` carrying the same headers, which is the shape
most Static Web Apps examples show. Azure's configuration reference says two things that combine
badly here: `globalHeaders` and a route's `headers` are **unioned**, with the route winning per
header *name*; and route rules are not applied at all to a request that triggers
`navigationFallback`. Every deep link in this application is a `navigationFallback` rewrite to
`/index.html`, so a route rule would ship the headers on the root document and drop them on every
URL a person actually lands on — present where anybody would check, absent where it matters.

The union is what lets the one existing route rule stay untouched. Exercised with
`npx @azure/static-web-apps-cli start dist/angular-budgetoid/browser`: the configured `globalHeaders`
on `/`, the same set on `/app/settings`, and `/fonts/*` answering with its own `Cache-Control:
public, max-age=31536000, immutable` **plus** that set. The font route needed no change and must not
grow one.

### What the CLI run is, and what it is not

**The Static Web Apps CLI is an emulator, so a run of it is a strong smoke test of *this
repository's configuration* and never evidence of the header set Azure emits.** It is cited above
and below on those terms, and the distinction is load-bearing for one specific reason: **the
emulator injects headers of its own that appear in no configuration file here** —
`X-Content-Type-Options: nosniff`, `X-XSS-Protection: 1; mode=block` and
`X-DNS-Prefetch-Control: off`. So a header seen in a CLI response is not thereby a header this
repository asked for. `X-Content-Type-Options` is exactly the trap: it is now declared in
`globalHeaders`, and it would also have appeared in a CLI run that declared it nowhere. What the run
does establish is the part the emulator has no reason to fake — *which paths* receive the configured
`globalHeaders`, which is the question the route-rule decision above turns on.

`X-XSS-Protection: 1; mode=block` is a header this repository would not set. The value is deprecated
and harmful rather than merely obsolete: the heuristic filter it switches on was itself shown to
introduce vulnerabilities, and current engines have removed it or never shipped it. `0` is the value
modern guidance gives, and a policy-based defence — which is what the `Content-Security-Policy` above
is — is the replacement. It is named here because it is not ours to remove — if the managed runtime injects it the way
the emulator does, it is a header served from this origin that nothing in this repository controls,
and a reviewer reading a production response should recognise it rather than go looking for the
commit that added it. `globalHeaders` can override a value by name, which is the only lever available
should that turn out to be needed; it is not exercised today, because whether the managed runtime
injects anything is unmeasured.

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

### `connect-src`, and the four sources it names

`connect-src` governs fetch destinations rather than subresources, so the origin rule does not reach
it. It names four sources: `'self'`, the API, `accounts.google.com` for the OpenID discovery
document, and `www.googleapis.com` for the JWKS that document points at. The last two are the same
fetches [no-third-party-origins.md](no-third-party-origins.md) already records as gaps it cannot
close, and both leave with federated sign-in. `oauth2.googleapis.com` is deliberately absent: the
application runs the implicit flow, so no request reaches a token endpoint.

`img-src` carries no `data:`, verified against the emitted CSS and JavaScript, which contain none.
`data:` is the token a reader adds to make one inlined icon work; it is not an origin, and it admits
every attacker-controlled byte string as an image.

### `base-uri 'self'` must not be tightened to `'none'`

The two origins disagree here on purpose: the API says `base-uri 'none'`, the web application says
`base-uri 'self'`. That reads like an oversight, and "harmonising" the web application onto the
API's value is the trap.

`src/index.html` carries `<base href="/">`, which Angular's router and every relative URL in the
document depend on. Under `base-uri 'none'` the browser does not merely ignore an *injected* `<base>`
— it ignores **ours**, and the document base falls back to the URL of the page itself. On the root
that changes nothing, so it survives every check anybody would think to run. On a deep link it is
fatal, and the emitted document says how far it reaches: `index.html` references
`styles-<hash>.css`, `main-<hash>.js`, `polyfills-<hash>.js` and every `modulepreload` chunk
**document-relative**, with no leading slash. At `/app/settings` the document base becomes `/app/`,
so each is requested under `/app/…`; `navigationFallback.exclude` keeps `*.{css,js}` out of the SPA
rewrite, so every one of those requests 404s. The application does not merely ship **unstyled on
every deep link** — it does not boot there at all, with nothing in the response naming the cause.
(`/theme-prepaint.js` is written root-relative and would survive, which is exactly the kind of
partial symptom that sends a reader looking in the wrong place.)

That is the same failure [ADR 0020](../decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md)
exists to prevent, reached through a different directive: a policy change that makes the stylesheet
never arrive, silent in the build and total in the browser. The API can afford `'none'` because it
serves no document and has no `<base>` to lose.

`'self'` is not a concession. An injected `<base href="https://evil.example/">` is refused by it just
as `'none'` would refuse one, because the injected value is cross-origin; what `'self'` additionally
permits is the document's own `<base href="/">`, which is the one the application needs.

### Two years of HSTS, and no `preload`

`max-age=63072000; includeSubDomains` is two years. The rejected addition is `preload`: submission
to the browsers' preload list is effectively irreversible, and it ships in the browser binary rather
than in a response, so it also binds an agent that has never contacted this domain. The spec asserts
the one-year floor the requirement names rather than the value that ships, so raising the value never
reddens and dropping below a year always does.

**`includeSubDomains` commits every future subdomain too, and the argument for refusing `preload`
does not by itself distinguish them.** The difference is reversibility and reach, not kind. Stated
concretely, so nobody has to rediscover it: a future `staging.budgetoid.app` served over plain HTTP,
or with a self-signed certificate, is **unreachable** — not warned about, unreachable, with no
click-through — for every agent that has loaded `https://budgetoid.app/` inside the preceding two
years. Undoing it means serving `max-age=0` from every host under the domain and waiting for each
agent to come back; `.app` being HSTS-preloaded at the TLD level means the plaintext half is already
settled regardless, but the certificate half is not.

**The project is on the side that says yes: every subdomain of `budgetoid.app` is HTTPS-only with a
real certificate from its first day, and `includeSubDomains` is how that is stated rather than a
cost that slipped through.** A subdomain that cannot meet it is a subdomain that does not get created
under this domain. `preload` is refused on top of that only because it is irreversible on a timescale
this project does not control, while `includeSubDomains` is reversible in two years' worth of visits
by a decision this project can make alone.

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
build that is larger and slower with nothing red anywhere.

**The sharp version, because "it defaults to false" is not what is happening.** `@angular/build`'s
own `schema.json` declares `"scripts": { "default": true }` inside the `optimization` object. That
default is never applied, because `optimization` is declared under a `oneOf` — the schema validator
fills defaults for the matched branch's declared properties in the simple case, and this
object-or-boolean union is the case where it does not, leaving `optimization.scripts` `undefined` for
`!!` to read as `false`. So the configuration is stepping around a **divergence between Angular's
schema and Angular's normalizer**, not around a documented default. That divergence is load-bearing
today and **nothing in this repository pins it**: an upstream fix that made the schema default apply
would make the explicit `"scripts": true` redundant rather than wrong, but an upstream change in the
other direction would be invisible here. `scripts` is the key that makes the object have to be
written out in full. See [ADR 0020](../decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md).

### What the trade cost, measured

`index.html` fell from 12,819 B to 1,463 B. The 19,005 B `styles-*.css` became render-blocking
instead of deferred. `main-*.js` is still hashed and still minified, and the production `initial`
budget is unchanged at 403 kB. A cold first paint costs one extra round trip rather than two,
because the script and the stylesheet are discovered in the same head parse and fetched in parallel
on a connection that is already open.

**Repeat visits do not clearly improve, and that is the number most likely to be overstated.** The
document did shed 11 kB of critical CSS that previously rode along with every navigation. Against
that, `theme-prepaint.js` is a new **parser-blocking** request — no `defer`, deliberately — served
with the Static Web Apps default `Cache-Control: must-revalidate, max-age=30`; only `/fonts/*`
carries `immutable`, from the one route rule. Past thirty seconds every visit therefore pays a
blocking conditional request before first paint, which the inline version cost nothing. `immutable`
cannot be set on the file, because its name carries no build hash and a wrong cached copy would be
unfixable. Which way the net lands is unmeasured and depends on connection latency; the honest
statement is that it is a trade in both directions, not a win. [ADR
0020](../decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md) holds the same
reasoning.

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
cleared after it ran". **No registration position repairs the direct write** — the clear happens
below this middleware and after it has already written, wherever "below" begins. `OnStarting` fires
after any such clear and just before the flush, which is the only point at which *written* and *on
the wire* mean the same thing.

**The converse is what a reader gets wrong, so it is stated too: with the write in `OnStarting`,
moving the registration below `UseExceptionHandler()` changes nothing.** Measured — all four headers
still ship on a 500. `HttpResponse.Clear()` resets the status and the headers and does not touch the
`OnStarting` callback list, which lives on the response feature and has no public API to reset. So
ordering against `UseExceptionHandler()` is not what pins the registration.
**`FirstPartyRequestMiddleware` is**, and only that: it answers 403 without invoking `next`, so
anything registered below it never runs on a refused request and that 403 ships bare.

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

**The tripwire, and the two answers that do not work.** The day somebody adds a Swagger or Scalar UI,
`default-src 'none'` blanks it — no script, no stylesheet, no font. The obvious fix is "override the
header on that one endpoint", and **neither shape of that works**; both were measured, and the reason
is the same `OnStarting` mechanism that makes the write survive a 500:

- An endpoint that assigns `Response.Headers.ContentSecurityPolicy` while it handles the request is
  **overwritten**. Any `OnStarting` callback runs later, at flush, so the middleware's write is
  simply the last one.
- An endpoint that registers **its own** `OnStarting` callback loses as well. Kestrel keeps these
  callbacks in a `Stack` and runs them **LIFO**, so this middleware — registered first and
  outermost — runs **last** and overwrites the endpoint's.

The working answer is an opt-out read **inside this middleware's own callback**, off **endpoint
metadata**: `HttpContext.GetEndpoint()` is available at flush time, so the callback can find a marker
on the documentation endpoint and write that endpoint's policy instead. Metadata rather than
`HttpContext.Items`, because metadata **fails closed** — `ExceptionHandlerMiddleware` nulls the
endpoint, so a 500 can never inherit a documentation page's relaxed policy. Never loosen the global
policy so a documentation page can render. Today `MapOpenApi()` is Development-only and serves JSON,
and the only OpenAPI package is `Microsoft.AspNetCore.OpenApi`, so no HTML UI exists here to break.

**The price of running last: a throw in any *other* `OnStarting` callback drops all four headers.**
Kestrel's `ProcessEvents` holds its `try`/`catch` outside the pop loop, so the first callback that
throws abandons the rest of the stack — and LIFO puts this one at the bottom of it, the most exposed
position there is. Measured: a probe endpoint registering a throwing callback produced a bodyless 500
carrying none of the four headers. **This is not a defect today**: the application registers exactly
one `OnStarting` callback and it is this one. It is written down because it is the cost this approach
carries, and it becomes real the day a second callback is registered anywhere in the pipeline — at
which point this one's delivery is conditional on that one not throwing.

## What holds the line

`ClientApp/angular-budgetoid/src/security-headers.spec.ts` reads the **emitted**
`staticwebapp.config.json` and `index.html` from `dist/angular-budgetoid/browser`, so it needs a
prior `npm run build` — the same Build-before-Test ordering `no-external-origins.spec.ts` already
imposes. It parses the policy into directives and compares **source sets**, never the whole header
string: a string-equality assertion reddens on a harmless reordering, and the cheapest way to green
that is to paste the actual value over the expected one, at which point the test asserts only that
the header equals itself.

**The whole policy is held by one reviewed table, not by a test per directive**, and the shape is the
point. The table maps `directive → { sources, why }`, and three assertions read it:

1. the set of directive **names** in the shipped policy equals the table's keys;
2. per directive, the shipped **sources** equal the table's list — set equality in both directions,
   so a source added in place reddens exactly like one removed;
3. no directive name appears **twice** in the raw header.

A test per directive reads only the directives somebody thought to name, which leaves three ways to
widen the policy in silence, and the three assertions close them in order: a directive nobody reads
(`script-src-attr 'unsafe-inline'` re-permits exactly the inline handlers ADR 0020 disabled
critical-CSS inlining to refuse; `report-uri` is an exfiltration channel spelled as a diagnostic), a
directive nobody reads being *widened* (`base-uri`, `form-action`, `object-src`), and a directive
repeated so that two readers disagree.

**The parser is first-wins, and that is a correctness fix rather than a style choice.** CSP Level 3
has a browser keep the **first** occurrence of a repeated directive; a `Map` built by iterating
keeps the **last**. The two disagree in the dangerous direction — a policy ending
`…; script-src 'self' 'unsafe-inline'` would be read as permissive by a last-wins parser while the
browser enforced the strict first copy, or, worse, read as strict while the browser enforced a
permissive first copy. The parser now matches the agent, and assertion 3 makes the ambiguity itself
fail rather than relying on the parser to resolve it well.

**Why the table shape matters more than its contents:** a widening cannot be greened by editing an
expected array, because every key carries a written justification the editor has to compose. Turning
`script-src` into `'self' 'unsafe-inline'` means overwriting the sentence that says
`'unsafe-inline'`, `'unsafe-eval'`, a hash, a nonce or a host each re-opens the injection path the
header exists to close. That is the same friction `allowedConnectSources` already applies to an
origin, extended to every directive.

Two further guards live in the same file. **Every `<script src>` in the emitted document must
resolve to a file the build actually emitted** — a `src` pointing at nothing is a 404 in `<head>`
that no policy assertion can see, because a file that was never emitted is still `'self'`; deleting
`public/theme-prepaint.js` is the concrete case, and its only symptom is a theme flash. And **the
pre-paint script's storage key is compared against `theme.service.ts` rather than retyped here** —
the same literal otherwise lives in two files in two languages with nothing reconciling them, and
renaming it in the service leaves the pre-paint reading a dead key with no error and no failing
build.

Its script-policy test derives its own condition from the shipped policy — *if `script-src`
withholds `'unsafe-inline'`, then the emitted document contains no `<script>` without a `src` and no
`on*=` attribute* — so re-enabling `inlineCritical` reddens it without anybody having written down
that `inlineCritical` exists. It also fails on any security header declared on a route rule, and on
a `max-age` below one year.

`BudgetoidApp/tests/IntegrationTests/SecurityHeaderTests.cs` is organised by **which part of the
pipeline wrote the response**, not by which header is checked, because the responses a header test
usually misses are the ones no route delegate wrote: an `IExceptionHandler` 500, a `/health` 200, a
401 from the authentication challenge, and a 403 from a middleware that never calls `next`. Each of
its six tests names the smallest production change that turns it red.

**One of those levers is not what it looks like, and it is the one a reader most reliably gets
wrong.** `AResponseFromAnExceptionHandler_CarriesTheHeaders` discriminates the **write technique**,
not the registration's position: the change that reddens it is writing the four headers straight
onto `Response.Headers` before `await next(…)` instead of from the `OnStarting` callback. Moving the
registration below `UseExceptionHandler()` does **not** redden it — measured, all four still ship on
the 500. What pins the **position** is `ARefusedNonFirstPartyRequest_CarriesTheHeaders`, because
`FirstPartyRequestMiddleware` answers 403 without calling `next`, so registering below it ships that
403 bare. The remaining levers are gating the write on the outgoing status, and removing one entry
from the header dictionary.

The header names are read off `SecurityHeadersMiddleware.Headers` rather than retyped, so a rename
cannot leave a test passing against a header nobody emits. Two names are deliberately written out:
`HstsMaxAge_IsAtLeastOneYear` writes its own and parses the directive, so shortening the value
cannot be silenced by editing a literal in the same commit, and `Framing_IsForbidden` writes its own
so a rename in the dictionary fails it rather than redirecting it to whichever header the dictionary
now calls the policy.

## What none of this proves

- **That Azure emits any of it.** No test in this repository can reach Azure Static Web Apps or
  Azure Container Apps. The specs prove the configuration and the middleware ship with these names
  and these values; the runtime evidence above comes from the Static Web Apps **CLI**, which is an
  emulator against the build output and injects headers of its own — see [what the CLI run is, and
  what it is not](#what-the-cli-run-is-and-what-it-is-not). There is no production environment today,
  so nothing here has been observed in production. `DEPLOYMENT.md`'s verification step is where that
  gap is closed, by hand, per deploy.
- **That the web application's headers reach a response Azure synthesizes.** `globalHeaders` covers
  what the host serves from the content; a 404 for a missing asset was measured carrying none of the
  four. The API has no equivalent gap, because its headers come from a middleware rather than from a
  host's configuration.
- **That the policy is the right policy.** A directive set both files agree on is still a directive
  set, and a source added to both in one commit passes everything here except the reviewed policy
  table in the frontend spec — which does not judge the source either, only make somebody write a
  sentence defending it.
- **That a browser enforces what it is told.** `frame-ancestors`, `'unsafe-inline'` and HSTS are all
  agent-side behaviours, and an agent that ignores one fails nothing anywhere.
- **The API's response bodies.** These headers govern how a browser treats a response; the tenancy
  of what is in it belongs to [data isolation](data-isolation.md) and is unaffected.
- **`connect-src` against runtime behaviour.** The spec compares the policy's source list with the
  `apiBaseUrl` in `assets/app-config.json`. A library that learns a URL at runtime — which is
  exactly how the JWKS endpoint is reached — appears in no bundle and in no assertion, and shows up
  only as a blocked request in a browser.
