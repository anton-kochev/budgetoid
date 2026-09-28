# Log Redaction

> Read this before writing to the console, catching an error you mean to report, or adding a
> provider to `app.config.ts` in the web client — and before adding an `ILogger` call, a logging
> provider, a log-level rule or an exception handler to the API.

**No console line the web client writes and no `ILogger` record the API writes carries an email
address, a credential subject identifier, or the value of a [narrative](data-inventory.md)
column, and the API's records carry no WebAuthn credential id either. A line or a record that
refers to a user refers to it by internal identifier only — on the server, `users.id`.** The two
halves hold that rule by different means, because a log is a different thing on each side of the
wire. In the browser a log record is a console line: the text a person pastes into a bug report and
the tab a screenshot catches. On the server it is an `ILogger` record, and it goes to whoever
operates the service and reads its logs. In neither place did anybody choose what goes into it, the
way somebody chose what goes on a screen or into a saved export — a line that prints a caught
error whole puts there whatever the error happened to hold.

## The web client

### One funnel, and what it prints

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

### The reason is a literal

A reason is printed verbatim, so it has to be the author's own words. `logFailure`'s parameter type
is `LiteralReason<R>`, a **distributive** conditional type that keeps a member only if a record keyed
on it has a required key. That refuses a widened `string`, a template with a runtime hole, and — the
case a non-distributive check misses — a union mixing a literal with a pattern, where the literal's
key alone would otherwise let the pattern through. `transactions.service.ts` shows the intended
shape: its reasons come from a closed literal union, `FailureReason`, prefixed `Transactions:` by a
template over that union, which is still a union of literals.

### What the framework would print on its own

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
  and `error` print a fixed reason and the projection of the first argument that is not a string —
  the library leads with its own sentence and passes the failed response after it, so projecting
  the first argument would print `non-error` and drop the status. A call made only of sentences
  prints the reason alone; no sentence the library wrote is printed.
- **`provideBrowserGlobalErrorListeners()` claims the window's `error` and `unhandledrejection`
  events**, hands them to the handler above and calls `preventDefault()`, so the browser prints
  nothing of its own.
- **An environment initializer claims zone.js's unhandled rejections.** It sets the
  `ignoreConsoleErrorUncaughtError` flag on the `Zone` constructor, without which zone.js prints an
  unhandled rejection itself — message, value and stack — past every handler here, and it installs
  zone's `unhandledPromiseRejectionHandler` as a call to `logFailure`. Both keys are spelled with
  `__Zone_symbol_prefix` when a page sets one. The handler is not redundant with the window
  listener above: for a rejection outside Angular's zone, zone builds a `PromiseRejectionEvent`
  whose `promise` is undefined, the constructor throws, and zone swallows the throw — so without
  the handler the flag would turn that rejection from printed whole into printed nowhere. It runs
  as an *environment* initializer so both keys are set before any app initializer can reject.

**The order in `app.config.ts` is the rule for the logger.** `provideFailureLogging()` sits after
`provideOAuthClient()` because the last provider for a token wins; above it, the library's
`console` would win.

### What holds the line in the browser

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
printed, a rejection inside Angular's zone and one run in zone's root zone each print exactly one
funnel line, and both zone keys are set by the environment injector alone, before any app
initializer runs.

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

**`src/no-console-in-bundle.spec.ts` pins what the libraries print.** The source census cannot see
`node_modules`, and the production bundle carries some two dozen `console` calls of its own — the
OAuth library, zone.js, core, the router, the component library. The spec parses every emitted
chunk, keys each reference to the global `console` by its enclosing call with every identifier
replaced by `_` (so a minifier's renames change nothing and a new call changes the key), and
compares the result as a multiset against a table in which every key carries the reason it cannot
print an email, a subject or a narrative value today. A library upgrade that adds a call, or a
second copy of an existing one, is red until somebody writes that reason. What it cannot see is an
existing call whose argument changes meaning under the same shape, and a library reaching the
console through a property or a computed name.

### Deliberate absences in the browser

- **No test enumerates the narrative fields.** The projection never reads a string off the cause,
  so exact-argument equality holds the rule for every field at once — and a list here would be a
  client-side copy of the [data inventory](data-inventory.md) that nothing reconciles.
- **No third-party error-reporting service.** It would be a sink this chapter cannot see and an
  origin [no third-party origins](no-third-party-origins.md) refuses.
- **No line names a user at all**, so the internal-identifier half of the rule is met by absence.
  Do not add a user id "for context"; the rule permits one, and nothing needs it.

### What the browser half does not reach

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

## The API

The server cannot hold the rule the browser's way. The browser prints one projection through one
funnel; the API has the ASP.NET Core, EF Core, Npgsql and JwtBearer stacks writing records of their
own, at levels and in categories this repository does not choose, beside the records its handlers
write. `ServiceDefaults/Extensions.cs` registers OpenTelemetry as a logging provider with the
formatted message **and the scopes** included, and adds an OTLP exporter whenever an endpoint is
configured. A scope is therefore part of what the exporter is handed, not decoration around it,
which is why the census below reads scopes as closely as messages.

A record on the server is a copy the account does not govern. An erasure deletes a row and leaves
the record; a key rotation re-seals a column and leaves the record holding the envelope under the
key the rotation retired; and the record is kept for as long as the log pipeline keeps anything,
readable by whoever it lets read. An address or a provider subject in it is worse than a narrative
envelope, because it names the person outright to somebody with no key at all.

### One list, reached two ways

`Infrastructure/Persistence/Inventory/NeverLoggedColumns.cs` names every column whose value no
record may carry. `NeverLoggedColumn` has a private constructor, so a column reaches the list only
through one of two factories, and they are two because columns reach it for two reasons:

- **`Identifying(table, column, because)`** names a column that identifies a person without being
  narrative. `NeverLoggedColumns.Entries` holds exactly three, each with a written reason:
  `users.email`, `credentials.subject` and `passkey_public_keys.webauthn_credential_id`.
- **`Narrative(entry, because)`** takes an inventory entry rather than a pair of strings, and throws
  for one the inventory does not classify narrative. So a narrative column typed out by hand is not
  something anybody can construct through that door — and the other door, which would take one, is
  held by a unit test refusing any entry the inventory calls narrative.

`NeverLoggedColumns.All` is the inventory's narrative columns — drawn from
`DataInventory.Of(Narrative)` at type initialisation and never written out — followed by `Entries`.
The narrative half can therefore only agree with the inventory, which can only agree with the
model's `NarrativeField` properties: a ninth sealed column is on the list the day it is classified,
with nothing here edited. All the narrative entries share one reason, because the reason is the
classification rather than the column.

**This is a second axis beside the classification, not a fourth word in it.** The inventory says
what the product owes the person for a column; this list says what it owes everybody who is not the
person. The two disagree on purpose. `users.email` is *arithmetic*, so the export carries it, and it
is still never logged: the export goes to the person, and a log goes to whoever operates and reads
logs. Folding "not in a log" into the classification would force one of those two answers to be
wrong.

**`users.id` is deliberately absent**, because it is what a record is meant to refer to a person by.
**The WebAuthn credential id is present for the other half of the rule.** It is a durable identifier
a passkey presents on every assertion, beside the account id — minted by the authenticator, not
rotatable by this server, and naming one person wherever it appears. Without it on the list, a
record that referred to a person by their passkey handle instead of by `users.id` would pass the
census, and "internal identifier only" would be a sentence with nothing checking it.

**A column off the list is not searched at all.** Adding an identifying one is a decision about what
identifies a person, and it costs an edit to the set `NeverLoggedColumnsTests` pins, which a reviewer
reads.

### No redactor at runtime, by decision

There is no processor rewriting records on the way out, and the absence is argued rather than
pending. A redactor has to recognise what it removes, and of the four values only an address has a
recognisable shape: an envelope and a passkey handle are base64url like every other binary member,
and a subject is an opaque string. Nor can it be told which values to remove: no project in the
solution references a data-classification package, so no log call carries a classification a
redactor could key on. A redactor here would come down to an email regex — something that reads like
coverage in a diff, misses a subject, a passkey handle, an envelope and an address hashed on the way
in, and enforces nothing.

It would also coerce rather than refuse, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) does not count a coercion
as enforcement: a value rewritten after the call was made is a defect the pipeline hid, not one
anybody fixed. **The line is held where a value would enter a log call**, and the census below is
what makes crossing it red.

### The exception handler keeps logging the exception

`GlobalExceptionHandler` logs every exception it handles at `Error`, with the method and the path,
and it keeps doing so. A 500 with no stack cannot be debugged, and a handler stripped of its
exception to satisfy this chapter would trade a rule about personal data for a product nobody can
support. What makes that safe is a question about what the exceptions carry, and the census answers
it: it drives two forced 500s through the real handler and searches the whole exception chain of
each record, `Data` included.

### What holds the line: a census over real traffic

`tests/IntegrationTests/LogRedactionTests.cs` drives traffic through real hosts, reads back the
values every listed column held at four moments, and searches each captured record for each
rendering of each of them. The rule itself is one assertion — no offences — and the rest of the file
is what makes that assertion mean something.

**The recorder.** `LogRecorder` is an `ILoggerProvider` and `ISupportExternalScope`. It is enabled
at `Trace` for every category through a filter rule naming the provider, and a rule naming a
provider outranks every category rule that names none, so no `Logging:LogLevel` setting the host
carries can narrow what it sees — `LogRecorderTests` finds a `Trace` record written under an EF
category through a real host. It therefore also sees `Debug` and `Trace` records a deployed host's
level rules would drop, so on levels the census is stricter than Production rather than looser.

It flattens each record to text **at `Log` time**, because a scope can read a pooled `HttpContext`
the next request has already reused and an exception's `Data` can be written after the record left;
a record rendered at snapshot time would describe some later state of the process. What it captures is the formatted message, every template value, every scope with its
key/value pairs, and the whole exception chain — each exception's text, every inner exception and
every member of an aggregate, and each `Data` entry. A `byte[]` template value is rendered as base64
and as hex, because the formatted message alone says `System.Byte[]`.

**Two hosts, because one cannot be both.** The main host repoints the provider's bearer scheme at the
test handler, which is what lets registration run at all — so a token sent there never meets the
real `JwtBearer` handler. The second host leaves that handler real and hands it an `RsaSecurityKey`
and no metadata address, so nothing reaches the network. Against it the traffic sends a token that
**validates** — registration answers 200 then 201, and the handler writes its own success record — a
valid token for an identity that already holds an account, which the options leg refuses, and the
same token with a forged signature, which answers 401 with the handler's failure record carrying an
exception.

**The traffic** covers registration and its refused duplicates; a passkey sign-in, one with an
unregistered handle and one with a tampered signature; a wrong recovery verifier; every narrative
write, update and delete; the list and by-id reads; the export; a malformed body; adding a passkey,
regenerating the recovery codes, redeeming one, signing out and revoking a passkey; a key rotation's
begin, chunk and completion; two forced 500s — issuing codes for an account holding no factor
manifest, and an export over two owned budgets; and the erasure, last, because it takes the account
every other step wrote into.

**The route floor.** `RouteTally` is a startup filter that records which endpoint each request was
matched to and what it answered, and a route counts as reached **only on a 2xx** — a 401 selects the
endpoint too, and counting it would call a route driven whose handler never ran. Every route
`EndpointDataSource` declares must be reached (49 measured, with a floor on the declared count so an
enumeration that found nothing cannot pass by demanding nothing), and the exemption list must name
only routes the table still declares. The list is empty today. The health check carries no method
metadata, so it is not in the declared set and not counted.

**The needles are read back, never written down.** Every value of every column in
`NeverLoggedColumns.All` is read on the admin connection, so row-level security hides nothing, at
four moments: before the updates and deletes, before the revocation and the rotation, before the
erasure, and after the traffic, with the hosts disposed first so a record written at shutdown is in
the snapshot. The **union** is searched, so a value a later step replaced or erased is still looked
for. Beside those, the identifying values the traffic **sent** are needles under the column each
would have filled — including refused ones that were never stored, because a refused duplicate's
address is exactly what a refusal path would log.

**Each value is searched in every rendering a record could carry it in.**

- *Text*: as stored, compared ignoring case when the column's collation is nondeterministic — read
  off the catalog, which makes `users.email` case-insensitive; `Uri.EscapeDataString`; JSON-escaped
  by the default and the relaxed encoder; and the SHA-256 hex, in both cases, of the value and of its
  lower-cased form. A hash is a rendering, not a redaction: anybody holding a guessed address can
  join on its digest.
- *Bytes*: base64, base64url, upper- and lower-case hex, the first 32 bytes as hex, and every 16-byte
  window at every offset as base64 and base64url, cut to the characters those sixteen bytes fully
  determine.

**The 32-byte hex needle exists because of a measurement.** With EF's sensitive-data logging on, EF
renders a `byte[]` parameter as its first 32 bytes followed by `...`, so a whole-value needle misses
that record entirely. The prefix needle is built for every binary value longer than 32 bytes.

**Every needle is at least 16 characters, asserted before the rule is**, because a short needle
matches harmless text by chance and an accidental match trains a reader to ignore a red.

**An offence names the column, the needle kind and the record's category, level and event id — never
the needle.** Printing the value would put it in a log of its own.

**The non-vacuity floors** are what stop a quiet run passing: a record count on each host; the
hosting and EF categories present; at least two `GlobalExceptionHandler` errors carrying an
exception; records from both refusal handlers, `PasskeyVerificationExceptionHandler` and
`RecoveryCodeRedemptionExceptionHandler`; the validated-token and forged-token steps answering as
described above, each with its handler record; the malformed-body step writing a record that carries
the parser's exception; every identifying entry sent at least once; every column holding a value at
some snapshot; and the needles' columns equal, in both directions, to the inventory's narrative
columns plus `Entries`.

**The controls.**

- `LogRecorderTests` proves each capture channel finds a planted value — a template value, a scope
  key the template does not name, an inner exception inside an aggregate, a `Data` entry overwritten
  after the record was written, a `Trace` record under an EF category, a raw `byte[]` — and that each
  rendering is found under its own kind. The channel and rendering cases also search for a
  neighbour value nobody logged and demand it is absent, and every case logs through a real host's
  `ILoggerFactory`, so what is proved is what the application's pipeline hands a provider.
- `Census_ForValuesPlantedThroughTheHost_NamesEveryEntryAndANarrativeColumn` plants a value of every
  entry and of a narrative column through the host and asserts the census names each one under its
  column, and a re-cased address under the email.
- `tests/UnitTests/NeverLoggedColumnsTests.cs` pins that the entries are mapped columns, that they
  are exactly the three with a reason each, that none is narrative, that `All` is the narrative
  columns plus the entries, and that the narrative factory refuses an entry classified anything else.

**Measured forced reds.** Each change below was made, run, and reverted, and the census went red on
each. On the EF side: turning on `EnableSensitiveDataLogging` (22 offences, the narrative columns
caught as the 32-byte hex). In the authentication stack: a `JwtBearer` failure event logging the
address and subject, and an `OnTokenValidated` doing the same. In the handlers: a `LoggerMessage`
logging the credential id, a handler logging an `Email` through its `ToString()` — which returns the
raw address — `/api/me` logging the user by address even at `Debug`, a refused request logged, and an
address logged as its SHA-256 or URL-escaped. In the pipeline: `GlobalExceptionHandler` logging the
request body, an exception `Data` entry carrying envelopes on the export's 500, a `PUT` body logged,
and `AddHttpLogging` with every field on (51 offences).

### What the census host changes, and what it leaves alone

**The census host sets `RouteHandlerOptions.ThrowOnBadRequest` to `false`, as Production has it** —
the option defaults to `true` in Development only. Under that Development default a malformed body
produced no record at all (measured), so a census running on the default would have asserted
cleanliness over a path that wrote nothing.

**`Include Error Detail=true` is not guarded, and that is stated rather than hidden.** Set on the
application connection, it leaked nothing on the duplicate-key paths (measured): the `23505` came
back without the `Key (…)=(…)` detail. [Guessing] PostgreSQL withholds that detail because the tables
carry row-level security. The census connects without the flag, so turning it on in any environment
is a change the census never sees.

### What the API half does not reach

- **Records written outside `ILogger`.** Standard output, an `EventSource`, and OpenTelemetry span
  tags are not captured, so a value there is invisible to the census.
- **Log calls that fire only in Production.** The census host runs in Development, so an
  environment branch the host never takes writes nothing the census reads.
- **A branch a route's traffic reaches the route for but never takes.** The floor asks one 2xx per
  route, not every branch of every handler.
- **A value in a rendering the needles do not model.** Hashed after some other transformation, salted
  or keyed with an HMAC, base64 of a hash, a slice of a binary value shorter than 16 bytes, part of a
  text value, or wrapped inside another encoding — a provider token's payload is base64url of JSON,
  so the address inside a logged token is not the address's own bytes.
- **The `Include Error Detail` flag**, as above.
- **The stricter rules beside this one.** Erasure logs no identifier of an erased account, the
  export logs no identifier, and a recovery code's verifier, hash and set credential id reach no log
  line. Each of those forbids values this census permits or does not search — `users.id` among them —
  and each is held where its own chapter says: [erasure](../business-logic/erasure.md),
  [export](../business-logic/export.md), [recovery codes](../business-logic/recovery-codes.md).
