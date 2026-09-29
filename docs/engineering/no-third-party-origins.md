# No Third-Party Origins

> Read this before adding a `<link>`, a `<script>`, an `<img>`, or a font to the web app — or a
> cookie, or a key in the browser's storage.

**The web application loads nothing from an origin other than its own.** Not a script, not a
stylesheet, not a typeface, not an icon, not an image. The reason is not performance and not
supply-chain risk: a request to a third party tells that third party the user's IP address, user
agent, and the moment they opened their budget. That includes an identity-provider profile
picture, which is why the claim is dropped at the boundary in `+core/services/auth-service.ts`
rather than stored for a later component to render.

## What holds the line

`src/no-external-origins.spec.ts` reads `dist/angular-budgetoid/browser` and fails on any
absolute `http(s)` URL in the emitted HTML or CSS. It reads the **build output**, not the
sources, because the builder inlines the `@font-face` CSS behind a stylesheet link — a CDN font
disappears from `index.html` as authored and reappears there as an inlined `fonts.gstatic.com`
URL, and a test over `src/` would have called that clean. Hence Build before Test in CI, and
`npm run build && npm test` locally.

Emitted JavaScript gets an allowlist rather than absence, because libraries put documentation
links in error messages and inline SVG carries `www.w3.org` namespace URIs that are never
fetched. Each entry carries its reason; a new dependency that drags in a new origin fails the
test on purpose. `assets/app-config*.json` is skipped — the API base URL and OAuth redirect URI
are an XHR target and a navigation target, not subresources.

## Typefaces and icons

Inter and Mohave are served from `public/fonts/` as variable woff2 files, bound by
`src/assets/theming/_fonts.scss`; provenance, the regeneration recipe, and the filename rule the
immutable cache header depends on are in `public/fonts/README.md`.

No icon font is loaded. `docs/design/components.md` specifies Material Symbols Rounded, but no
component uses an icon yet; the first one to ship brings a woff2 subsetted to the glyph names
actually used — never a CDN link, never the whole 1.38 MB face.

## No state-inspection tooling

**The production build registers no state-inspection or developer-tooling provider, and
carries none of its code.** The Redux DevTools browser extension is a third party in the
sense that matters here: an NgRx store instrumented for it hands over every dispatched
action and the whole state tree. NgRx's `logOnly` flag narrows what such an extension may
*do*, never what it may *see*, so it is not a mitigation.

**The store holds nothing today, and the guard is worth more than the store is.** The last
slice in the application was the provider sign-in button's action chain, and it left with
the button; `provideStore()` and `provideEffects()` in `app.config.ts` are registered
empty. They stay because removing them takes `devtools.providers.ts`, the `angular.json`
`fileReplacements` entry and `no-devtools.spec.ts` with them — and the state tree an
extension would read is the *next* slice's, which arrives without anybody re-deciding this.
Do not read the two idle calls as dead code.

Removal happens at build time, not at runtime. `src/app/devtools.providers.ts` registers
`provideStoreDevtools` and the `fileReplacements` entry in the production configuration of
`angular.json` swaps it for the empty `devtools.providers.prod.ts`. That takes the
`@ngrx/store-devtools` import out of the module graph, and the package is tree-shaken out
of the bundle — roughly 11 kB of `main-*.js` that no longer ships. A runtime
`isDevMode()` branch would not have worked: the import survives it, and so do the devtools
code and its `__REDUX_DEVTOOLS_EXTENSION__` string literal. `ng serve` builds the
development configuration, so the tool is still there while developing.

`src/no-devtools.spec.ts` reads the same emitted directory and fails on either
`store-devtools` or `__REDUX_DEVTOOLS_EXTENSION__` appearing in any emitted script. Both
markers survive minification — the first inside NgRx action-type string literals, the
second as a `window` property name.

## Two gaps this test cannot close

Creating an account still fetches `accounts.google.com/.well-known/openid-configuration` and, from
the URL that document returns, `www.googleapis.com/oauth2/v3/certs`. Neither is a subresource,
and the second appears in no bundle — the library learns the URL at runtime. Ending them means
ending the dependency on a federated identity provider.

**Both fetches have narrowed to the one provider exchange an account is created through.** They are
made in exactly two places, both on `/register`: when the person presses **Continue with Google**,
because the login endpoint that press navigates to is learned from the discovery document; and on
the page load the provider redirects back to, because the `APP_INITIALIZER` must read the provider's
answer off the URL before the router's first navigation. `AuthService` prepares the client at most
once per page load, so a press on the page that came back costs no second fetch. **Nothing else
contacts the provider**: every other cold load — anonymous or signed in, on any screen, and a bare
`/register` that carries no answer — makes no request to Google, signing in is a WebAuthn assertion
against this product's own API, and every request after it authenticates from the first-party
session cookie. So the page loads privately, *signing in* loads privately, and the two fetches above
are reachable only while an account is being created. What used to be a standing cost of using the
product is now a one-time cost of starting to. `core.providers.spec.ts` holds the bootstrap half —
anonymous, signed-in and unreachable visitors alike — and `auth-service.spec.ts` holds the press
and the once-per-page-load half.

**What an allow-listed origin buys, and what it therefore cannot catch.**
`accounts.google.com` is listed above as the issuer `auth-service.ts` configures, legitimately —
the registration redirect is a top-level navigation to exactly that host. The consequence is that a
change putting this application back in *repeated* contact with it adds no origin the bundle did not
already carry, and this test stays green. The concrete case is
`setupAutomaticSilentRefresh()`, which plants a hidden iframe pointed at the provider and re-runs it
on a timer for as long as the tab is open: a third-party request on every page, forever, to renew a
token read on the registration screen and discarded once the tab holds a session. Nothing schedules
one, and what holds that is a pin of its own in `auth-service.spec.ts` rather than anything here.

And this covers what the build emits, not what the browser permits. The
`Content-Security-Policy` that enforces the same rule at runtime ships in `globalHeaders` of
`public/staticwebapp.config.json`. Its `connect-src` names **four** sources — `'self'`, the API, and
the two Google origins above — so the two gaps are the only third parties in it, and nothing else is;
[security headers](security-headers.md) owns it. It and this test are two halves of
one guarantee: build-time absence cannot see markup a defect injects at runtime, and a runtime
policy cannot see a CDN URL that no browser has been asked to fetch yet.

## No cookie from a script, and nothing to consent to

**The product loads nothing from another origin, except the provider exchange a person starts on
`/register`, and sets no cookie but the session handle, so there is no tracker in it and nothing to
ask anybody's consent for.** That exchange is the two fetches under the gaps above, made only
because the person pressed **Continue with Google** or came back from it. That is why the web
client presents no consent banner, no cookie notice and no tracking-preference surface. It is not
an omission waiting on a review; it is what this chapter holding looks like from the screen, and a
surface asking permission for something the product does not do would be a false sentence in front
of everybody. [Patterns](../design/patterns.md#nothing-to-consent-to) states the design half.

"No cookie but the session handle" has two halves, held in two places.

**The API half** is the MUST NOT in [sessions.md](../business-logic/sessions.md) — the API sets no
cookie but `__Host-budgetoid-session` — held by `CookieCensusTests`.

**The client half** is `src/no-cookie-writes.spec.ts`. It reads every `.js` file the production
build emits — so a dependency's code is covered, not only `src/` — parses each with the TypeScript
compiler, and fails on any assignment to a property named `cookie`, dotted or string-keyed, under
any assignment operator, and on any `cookieStore.set` or `cookieStore.delete`. The receiver is not
checked, because a minifier hands `document` to a one-letter local as readily as anything else.
Reads are counted, not refused. Angular ships two: platform-browser's cookie getter, and
HttpClient's XSRF reader, which only reads, and only for same-origin mutating requests — and the API
is another origin. The spec requires at least one read, so a scan that walked nothing cannot pass
on an empty write list. Like the specs above, it needs `npm run build` first.

What it cannot see:

- **A write built to hide from a syntax scan** — `Reflect.set`, `Object.assign`,
  `Object.defineProperty(document, 'cookie', …)`, the cookie setter called through its property
  descriptor, a computed key, a `cookieStore` reached through an alias, `cookieStore.set` reached
  through `.call`, `.bind` or `Reflect.apply`, a destructuring or loop target, `++`.
- **A write assembled from a string** — `eval`, `new Function`, a string handed to `setTimeout`.
  No syntax scan can see one, and this spec does not try: the shipped `Content-Security-Policy`
  closes that door, because `script-src 'self'` carries no `'unsafe-eval'` and the browser refuses
  all three.
- **A `<meta http-equiv="set-cookie">` in HTML.** The spec reads scripts only, but this is not an
  open gap in a supported browser: Chrome stopped honouring it in 65, Firefox in 68, and the HTML
  standard dropped it. [Guessing] that Safari ignores it too; nothing here has checked.
- **A `Set-Cookie` the static host adds to its own responses.** [Guessing] whether Azure Static Web
  Apps sets one on static files: nothing has been observed, because no environment exists to
  observe it on.

### What the device keeps

Storage on the device is judged by one test: **is it strictly necessary for something the person
asked for?** An audit trail fails it — nobody asked for a record of their own acts, and a trail
grows with every act and stamps each one. Growth and time on their own are not the test, and the
table has both: the rotation-epoch record adds one key per account unlocked in this browser, and
two rows carry a time — the session's expiry, and the provider token's stored-at and expiry — each
one value per credential, replaced rather than appended. Each row answers the test in its last
column.

| Key | Where | Holds | Why it is needed, and not a trail |
| --- | --- | --- | --- |
| `__Host-budgetoid-session` | cookie, set by the API, `HttpOnly` | the session handle | Serves the request. One per session; its expiry is the session's lifetime, not a record of an act. |
| angular-oauth2-oidc's token entries | `sessionStorage`, this tab | `access_token`, `id_token`, `id_token_claims_obj` (the decoded claims, the email among them), `granted_scopes`, `session_state`, `nonce`, and stored-at and expiry entries | Serve the registration the person started, and nothing after it. `SessionService` discards them through `AuthService.forgetProviderToken()` whenever the tab learns it holds a session — the registration `201`, a sign-in, a start-up probe that finds one. A tab that abandons registration and never signs in keeps them until it closes. Nothing sends them anywhere else: the bearer is attached only on the two registration routes. The library writes the nonce and PKCE verifier to `localStorage` only on an old-IE user-agent branch no supported browser takes. |
| the library's availability probe | `localStorage` | a `test` key | Written and removed at once, on every cold load: `AuthService` is built at startup, and the library's service with it. |
| `budgetoid-theme` | `localStorage` | `system`, `light` or `dark` | One value, overwritten. A preference, not an observation. Nothing writes it today: neither `ThemeService.setMode` nor `toggle`, which calls it, is called from outside the service, so `theme-prepaint.js` and `ThemeService` only ever read it. |
| `budgetoid-rotation-epoch:<budgetId>` | `localStorage` | one number per account this device has unlocked | Rises only. A rollback control — see [account keys](../business-logic/account-keys.md). |

**The rotation-epoch record leaves a budget id on the device, and nothing in the client removes
it.** `SessionService.ended()` drops the budget signal and has custody drop the keys it holds in
memory; it does not touch this record, and `rotation-epoch-record.ts` exports no way to. So the
key outlives a sign-out, and outlives an erasure too, naming a budget that no longer exists
anywhere else. What stays is one opaque identifier and one number per account that ever unlocked
in this browser, until the person clears the site's data. Clearing it at sign-out is not a free
fix: a device that forgets an account is in the first-visit state, where a rollback cannot be seen
at all.

Nothing is persisted beyond this table — the account's keys are held in memory per tab and never
written anywhere, per [account keys](../business-logic/account-keys.md). **A new key must argue its
row here, against the test above.** No spec compares this table with the code; only review keeps it
honest.
