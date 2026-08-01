# No Third-Party Origins

> Read this before adding a `<link>`, a `<script>`, an `<img>`, or a font to the web app.

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
action and the whole state tree, which today includes the signed-in user's profile. NgRx's
`logOnly` flag narrows what such an extension may *do*, never what it may *see*, so it is
not a mitigation.

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

Federated sign-in still fetches `accounts.google.com/.well-known/openid-configuration` and, from
the URL that document returns, `www.googleapis.com/oauth2/v3/certs`. Neither is a subresource,
and the second appears in no bundle — the library learns the URL at runtime. Ending them means
ending the dependency on a federated identity provider. Until then the page loads privately and
*signing in* does not.

And this covers what the build emits, not what the browser permits. The
`Content-Security-Policy` that enforces the same rule at runtime is not in place yet; when it
lands, it and this test are two halves of one guarantee.
