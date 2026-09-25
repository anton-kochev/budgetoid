# Log Redaction

> Read this before writing to the console, catching an error you mean to report, or adding a
> provider to `app.config.ts` in the web client.

**No console line the web client writes carries an email address, a credential subject
identifier, or the value of a [narrative](data-inventory.md) column, and a line that refers to a
user refers to it by internal identifier only.** In the browser a log record is a console line. It
is the text a person pastes into a bug report and the tab a screenshot catches, and unlike the
screen or a saved export, nobody chose what goes into it. A console line that prints a caught error
whole puts there whatever the error happened to hold.

## One funnel, and what it prints

`src/app/+core/logging/log-failure.ts` is the only shipped module that names `console`. Its export,
`logFailure(reason, ...cause)`, writes exactly one `console.error` line: the reason alone, or the
reason followed by a **projection** of the cause. The projection is a closed union,
`FailureProjection`, and nothing outside it is ever printed:

- `{ kind: 'http', status }` for an Angular `HttpErrorResponse`, recognised by `instanceof` and
  never by shape — a plain object with a `status` member would otherwise print whatever sits there;
- `{ kind: 'error', name? }` for an `Error` or a `DOMException`, where `name` survives only if it is
  on `PRINTABLE_ERROR_NAMES`;
- `{ kind: 'non-error' }` for everything else, a hostile getter and a revoked `Proxy` included.

**The message, the URL, the status text and the body are never printed**, because each is a place
one of the three values can sit — and `HttpErrorResponse.message` embeds the URL, so dropping the
URL while printing the message drops nothing. A **name** needs an allow-list rather than trust
because `name` is writable on any error and a `DOMException` takes any name its constructor is
given; a name is printed only when the list vouches for it, never because it looks like a class
name. It is read **once**, so a getter cannot answer the check with `TypeError` and the print with
an address. The `DOMException` arm exists for the test runner: under jsdom a `DOMException` is not
`instanceof Error` (measured), so without it every WebCrypto and WebAuthn failure a spec builds
would project as `non-error` and the allow-list could not be tested. In a browser the second
check is redundant.

**The funnel never throws.** It is what the `ErrorHandler` calls, so a throw from the projection
would land back in the handler that called it. Anything that throws while being inspected becomes
`non-error`, and the thrown value is not read either.

**The cause is a rest element**, `...cause: [] | [unknown]`, so "no cause" and "the cause was
`undefined`" stay two things: a refusal that never reached a request prints the reason alone, and
a `throw undefined` still prints as `non-error` rather than vanishing.

## The reason is a literal

A reason is printed verbatim, so it has to be the author's own words. `logFailure`'s parameter type
is `LiteralReason<R>`, a **distributive** conditional type that keeps a member only if a record keyed
on it has a required key. That refuses a widened `string`, a template with a runtime hole, and — the
case a non-distributive check misses — a union mixing a literal with a pattern, where the literal's
key alone would otherwise let the pattern through. `transactions.service.ts` shows the intended
shape: its reasons come from a closed literal union, `FailureReason`, prefixed `Transactions:` by a
template over that union, which is still a union of literals.

## What the framework would print on its own

Routing the application's own calls through the funnel is half the job. The other half is the four
places a library or the platform writes to the console without asking, and
`src/app/+core/logging/provide-failure-logging.ts` claims all four in `provideFailureLogging()`:

- **`FailureErrorHandler` replaces Angular's `ErrorHandler`.** The default prints `'ERROR'` and the
  error itself — response body and URL included. The replacement calls `logFailure` and has no
  `super` call, because that call is the print being replaced.
- **`FailureOAuthLogger` replaces the OAuth library's logger.** `provideOAuthClient()` registers
  `console` as `OAuthLogger`, and the library's `tryLoginImplicitFlow` hands `debug` the parsed
  redirect fragment, raw `id_token` and all — whose payload is the subject and the address. The
  library only calls `debug` when `showDebugInformation` is set, which `auth-service.ts` does not
  set; the silent `debug`, `info` and `log` are what keep that one flag from being a leak. `warn`
  and `error` print a fixed reason and the projection of their first argument, never the rest.
- **`provideBrowserGlobalErrorListeners()` claims the window's `error` and `unhandledrejection`
  events**, hands them to the handler above and calls `preventDefault()`, so the browser prints
  nothing of its own.
- **An initializer sets zone.js's `ignoreConsoleErrorUncaughtError` flag on the `Zone`
  constructor**, spelled with `__Zone_symbol_prefix` when a page sets one. Without it zone.js
  prints an unhandled rejection itself — message, value and stack — past every handler here.

**The order in `app.config.ts` is the rule for the logger.** `provideFailureLogging()` sits after
`provideOAuthClient()` because the last provider for a token wins; above it, the library's
`console` would win.

## What holds the line

**The funnel's specs assert exact arguments, never the absence of a string.**
`log-failure.spec.ts`, `failure-error-handler.spec.ts` and `failure-oauth-logger.spec.ts` build
every hostile cause out of an email, a subject and a narrative value, then assert the one line
that must come out. A search for leaked substrings finds only the leaks its author imagined; an
equality over the arguments turns a leak through any channel into a red bar. The same specs spy
on **every** function-valued method of `console` through `src/testing/console-spies.ts`, found by
walking the object rather than listed by hand, so moving the detail from `error` to `debug`,
`group` or `count` is red too. The reason's type is pinned by three `@ts-expect-error` cases in
`log-failure.spec.ts`, which the unit-test builder type-checks.

**`app.config.spec.ts` holds the registrations**, which the funnel's own specs construct directly
and cannot see: the `ErrorHandler` and the `OAuthLogger` resolve to the funnel's classes, a window
`ErrorEvent` and an `unhandledrejection` each end `defaultPrevented` with only the projection
printed, and the zone flag is set once the initializers have run.

**`src/no-console-outside-funnel.spec.ts` is a census over the source**, with comments removed by
the TypeScript printer rather than a regex, which cannot tell `//` inside a string, template or
regular expression from a comment. It asserts that only `log-failure.ts` names `console`, that only
`app.config.ts` calls `provideOAuthClient(`, and that `setupAutomaticSilentRefresh`,
`silentRefresh(` and `refreshToken(` appear nowhere. **The census, not lint, is what holds
`main.ts` and every other file**: an inline `eslint-disable-next-line` switches a lint rule off and
nothing reports it, while the census has no escape. A manufactured probe went red in the census
where lint stayed quiet.

**Lint is the early warning.** `eslint.config.js` carries three rules because each misses a
spelling: `no-console` sees `console.x`, `no-restricted-globals` sees `console` passed or
destructured, and `no-restricted-properties` sees it reached through `globalThis`, `window` or
`self`. The exemptions are the funnel, `src/testing/console-spies.ts` and `*.spec.ts` — nothing
else.

The accounts, categories and transactions service specs each pin one exact failure line, so a
service that went back to printing its cause fails where it lives.

## Deliberate absences

- **No test enumerates the narrative fields.** The projection never reads a string off the cause,
  so exact-argument equality holds the rule for every field at once — and a list here would be a
  client-side copy of the [data inventory](data-inventory.md) that nothing reconciles.
- **No third-party error-reporting service.** It would be a sink this chapter cannot see and an
  origin [no third-party origins](no-third-party-origins.md) refuses.
- **No line names a user at all**, so the internal-identifier half of the rule is met by absence.
  Do not add a user id "for context"; the rule permits one, and nothing needs it.

## What this does not reach

- **A computed spelling.** `globalThis['con' + 'sole']` passes both lint and the census. Review
  holds it.
- **A literal that is itself personal data.** The type admits any literal, an address included.
  Review holds that too.
- **Library code outside `src/`.** The census reads this repository's sources only. The OAuth
  library's code-flow token exchange writes to `console` directly, bypassing the logger: the token
  endpoint's raw error, and a rejection from `processIdToken` that can name both the old and the
  new subject. The client runs the implicit flow — `auth-service.ts` sets no `responseType` — so that
  path is one configuration line away, not live. **A switch to the code flow must re-open this
  chapter.**
- **The browser's own network-error lines**, which show the request URL. The client's request URLs
  carry only GUID path parameters and no query string, so none of the three values appears in one.
- **Console methods the runner's console lacks**, such as `profile` and `timeStamp`, which the spy
  walk cannot find. The census still catches a direct call to any of them.
