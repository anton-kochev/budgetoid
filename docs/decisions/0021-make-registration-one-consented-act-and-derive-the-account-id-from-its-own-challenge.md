# ADR 0021 — Make registration one consented act, and derive the account id from its own challenge

- **Status:** Accepted and implemented, and it is now the **only** way an account comes to exist.
- **Date:** 2026-08-18
- **Area:** API / Application (account creation, WebAuthn ceremony pools, provisioning markers)

## Context

An account had never been asked for. `UserProvisioningMiddleware` minted one from **any** authenticated
request reaching a route group that carried `ProvisionsUser`, so the first time somebody signed in with
the identity provider and a client called `GET /api/transactions`, a `users` row, a `credentials` row
and a `budgets` row came into existence. Nobody consented to anything, and nothing about the account
was chosen.

What that produced is worse than an unwanted row. The account held **one federated credential and
nothing else** — and `federated` is the one credential type that can never open a session reaching
budget content, because an authorization exchange returns claims rather than a secret a client can
turn into a key. So provisioning created an account **holding nothing that could ever read it**. Three
consequences followed, each recorded somewhere in this repository as a gap rather than a design:

- **Onboarding was ordered and the order was invisible.** A brand-new identity had to reach one of the
  six marked groups *before* it could register a passkey, because the passkey routes minted nothing and
  answered a caller with no account `401`. That ordering lived in `ApiFactory.EstablishAccountAsync`
  and in no product code at all.
- **The account could be reached and could not be emptied.** Erasure is gated on a fresh WebAuthn
  assertion, so an account holding no passkey could never clear the gate in front of it.
- **A provider id token outlives an erasure by up to an hour**, and a call to a marked route inside
  that window resurrected the account. [users-and-ownership.md](../business-logic/users-and-ownership.md)
  stated that hole and said it would close "when account creation becomes a consented act".

Making registration an act therefore means one request that creates the account **and** the passkey
that reaches it **and** the card of recovery codes that survives losing the passkey — or creates
nothing.

That runs into a problem the WebAuthn protocol poses and nothing else in this product does. A
registration ceremony is two requests. The **options** leg hands the authenticator a `user.id` — the
WebAuthn **user handle** — and the authenticator stores it forever. The **finish** leg is where the
`users` row is written. The assertion path compares the handle a device presents byte-for-byte against
the account id, so **the handle issued at options time and the `users.id` written at finish time have
to be the same value**. They are produced by two requests that share no session, no account and no
row, and getting them wrong fails **silently and permanently**: the account exists, the passkey
verifies, and every later sign-in from that device simply matches nothing, with no error naming the
cause.

The two legs do share one thing: the 32 server-minted bytes of the ceremony's own challenge.

## Decision

**Registration is two authenticated routes under `/api/registration`; the finish leg writes the whole
account in one `SaveChanges`; and the account's identifier is derived from the ceremony's own
challenge rather than chosen.**

1. **The group's policy names the identity provider's scheme, and no other group in this application
   does.** `RequireAuthorization(policy => policy.RequireAuthenticatedUser()
   .AddAuthenticationSchemes(ProviderAuthentication.SchemeName))`. Everything else on this surface
   either declares nothing and inherits the fallback policy or is anonymous.

   **Not `AllowAnonymous`, and the difference matters more here than anywhere.** An account cannot
   exist without a completed provider exchange, and the scheme is what enforces that; an anonymous
   options leg would hand a caller the ability to decide which account identifiers exist, because that
   leg mints the bytes the identifier comes off. Naming the scheme is also what makes
   `AuthorizationMiddleware` re-authenticate against the provider's handler rather than against
   the default, which is the session cookie's — so without the name a browser already holding a
   session could create an account nobody's provider vouched for, and no bearer would be read at all.
   **This policy is the only reason `JwtBearer` is still registered**: nothing defaults to it and no
   other route names it, so a bearer presented anywhere else authenticates nothing.
   Declaring a policy at all takes both routes out of the
   **fallback** policy and therefore out of `FullSessionRequirement`, which is the right outcome rather
   than a side effect worked around: this caller holds no session, so a rule about what kind of session
   may read budget content has nothing to judge.

2. **A caller with no account reaches these two routes and nothing else, and the scheme on the group's
   policy is what says so.** This began as a third marker attribute, `RegistersAccountAttribute`,
   because a provisioning middleware ran before the endpoint's own policy and would otherwise have
   refused a provider principal with no account before the handler was ever entered. That middleware
   and that marker are both deleted, and **the argument they carried survives intact, held by the
   policy above**: a bearer authenticates here and nowhere else, so "this route serves callers who
   have no account" is a property of the two routes' own authentication rather than of metadata
   somebody could add to a seventh group by mistake. What was the marker's arm is now three
   properties of this group: the claim gates run **before** the handler, as an endpoint filter, so an
   account is never created for a caller whose address the provider declines to assert; nothing
   resolves an account, because a caller about to register has by definition none; and **no identity
   is published**, because the handler derives and publishes the account id itself after the signature
   verifies.

3. **`users.id` is `SHA-256(domain-separation prefix ‖ challenge)`, first 16 bytes, RFC 9562
   version 8, stamped on the big-endian layout.** Both legs call one pure function,
   `RegistrationAccountId.For`, over the same 32 bytes, so the handle and the row are equal **by
   construction** rather than by a value carried between two requests. The prefix is part of the
   construction rather than decoration: the challenge is also the material the authenticator signs
   over, so a bare digest of it is a value two unrelated purposes could arrive at independently with
   neither able to say so. It is a one-way function of the **whole** challenge and never a slice of
   it — truncating the challenge is deterministic, distinct and well-shaped, and hands anybody who can
   influence a challenge the account identifier of their choosing.

4. **The derivation happens only after the challenge store has confirmed it issued and spent exactly
   those bytes for exactly the `AccountRegistration` pool.** On the finish leg the only source of the
   challenge is the client's own `clientDataJSON`. Deriving before `ConsumeAsync` has answered is
   deriving from a value the caller supplied — an account identifier of their choosing, wearing the
   shape of one this server minted. **Moving that line up reads like tidying and is the one change that
   turns this function into a vulnerability.**

5. **One `SaveChanges`, roughly thirty rows, and no transaction wraps it.** The finish leg writes 1
   user, 1 default budget, 3 credentials (`federated`, `passkey`, `recovery_codes`), 1 passkey public
   key, 1 signature counter, 10 recovery-code hashes, 11 `wrapped_account_keys` rows, 1 session and 1
   session token — or nothing at all. The absent transaction is a **correctness ruling** rather than a
   cost one: `BeginTransactionAsync` opens the connection, which is when `SessionContextInterceptor`
   writes `app.current_user_id`, so a wrap whose delegate contains the identity publication configures
   the connection while that setting is still empty and the `users` INSERT meets `''::uuid` in its
   `WITH CHECK` — a `22P02`. `CompleteAssertionHandler` and `RedeemRecoveryCodeHandler` each carry the
   same warning, and `RegisterAccountHandler` states it inline: the argument was first written for
   three rows and is unchanged at thirty.

6. **A set of ten recovery codes is minted in the same act, not offered afterwards.** An account whose
   only factor is one passkey is an account whose keys leave with that device. Eleven
   `wrapped_account_keys` rows land beside them, because a factor is not a credential: the passkey is
   one factor and the card is ten, each code deriving its own key-encryption key.

7. **`201`, no `Location`, a `Set-Cookie`, and a body carrying only the session's kind and expiry.**
   Something was created, so `201` is the honest status — but the resource created is the account and
   this API exposes no address for it, and a `Location` naming one would publish the account identifier
   in a header, which is the one value every envelope on the request was sealed against. No account id,
   credential id, session id, factor id or echo of the address.

## Alternatives considered

**Return the user handle from the options leg and have the client echo it back on the finish leg.**
The obvious answer, and the only one that needs no derivation at all. It fails on the shape of the
failure rather than on cost: the server would be accepting an account identifier off a request body,
so a caller could file an account under any 16 bytes they liked — including bytes chosen to collide
with something later. Constraining it back would mean binding the handle to the challenge on the
server, which is this decision with an extra round trip and an extra way to disagree. And a client that
echoes the *wrong* value — a re-encoding, a trimmed padding character, a handle from a previous
attempt — creates an account no authenticator will ever match, silently and permanently.

**Park a candidate id on the `webauthn_challenges` row at options time and read it back at finish
time.** Keeps the id server-assigned, keeps it out of the request body, and needs no derivation. It
needs a column on an **exempt** table whose pinned column set says *move the column, never widen the
pin* — and that table's own configuration says a further ceremony value is the mechanism for anything
new, never a column. [ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
already refused the identical shape for the factor identifier, in the same words: a whole table's worth
of cost for a property something cheaper already gives.

**Keep transparent provisioning and prompt for a passkey on the next screen.** No new routes, no
derivation, and the ordering problem disappears. It leaves every one of the states above reachable —
an account holding only a federated credential exists between the two steps, and a person who closes
the tab keeps it forever — and the erasure gate then refuses that account permanently. The whole
argument for consent is that the intermediate state must not exist.

**Mint a fresh version 7 uuid at the finish leg and let the handle be a separate value.** The handle
and the account id would then be two columns, related by a lookup. It costs a column on
`passkey_public_keys`, which is exempt from row-level security and pinned — the same refusal as above —
and it retires the property the assertion path is built on: that a presented handle **is** an account
id, compared byte-for-byte with nothing to look up first.

**Make the options leg anonymous so a client can start the ceremony before signing in.** It reads as
a nicer flow and it is the one thing this surface must not do. That leg mints the bytes the account
identifier is derived from, so an anonymous caller could spend nonces to enumerate identifiers, and the
account an unvouched-for caller went on to create would be one no provider ever asserted an address
for.

## Consequences

- **The registration repository writes `sessions` and `session_tokens` itself, and that departs from a
  boundary another handler keeps by hand.** `GenerateRecoveryCodesHandler` refuses to let
  `IRecoveryCodeRepository` open a session, because a repository named for recovery codes that also
  opens sessions puts a route's session rule where nobody reading the route would look — and it can
  afford the second call, because both saves run inside one `ITransactionalExecutor` transaction.
  There is no transaction here, so two calls would be two transactions and atomicity would be lost
  **silently**: a committed account with no session answers `201` and signs nobody in.
  `ISessionRepository.AddAsync`'s own invariant survives untouched — `Registration` carries the session
  **and** its token, so no shape of any call in this system writes one without the other.
- **`users.id` stops being time-ordered, and every other identifier in this schema is a version 7 uuid
  for index locality.** A hash-derived key scatters `users` primary-key inserts instead of appending at
  the right-hand edge of the index. That table is low-volume and one row per account is the rarest
  insert in the product, so the trade is right — but it is a real cost rather than a wash, and this is
  where it is recorded. `Guid.CreateVersion7()` is not an option here at any price: it is not a
  function of its input, which is the entire requirement.
- **Four conflicts answer `409`, narrowed on four pinned constraint names, and one of them is ambiguous
  by construction.** A losing insert can breach the credential's `(provider, subject)` and the email at
  once, and PostgreSQL names only one, picked by write order. EF writes `users` before `credentials`,
  so the credential index being named means the email did not collide; the email index being named says
  nothing about the subject, and only a re-read of the federated credential settles it. **The race
  winner is never adopted**, unlike provisioning's own race: adopting would sign the caller into an
  account their brand-new passkey cannot open.
- **Three of those `409`s tell a caller that an address, an authenticator or an identifier is spoken
  for, and none of them is an enumeration oracle.** The address one is answered to a caller who has
  **just proved control of that address through the provider**, so it discloses nothing they could not
  establish by signing in. The other two are facts about their own device and about a value their own
  client chose.
- **`excludeCredentials` is empty and stays empty**, for three reasons no one of which would settle it
  alone. There is nothing to exclude, because the account does not exist. The read that would produce a
  list has no acceptable shape — scoped to the derived id it is always empty, and unscoped it is an
  enumeration of every handle in the table, which is precisely what the `passkey_public_keys` exemption
  was argued as **not** permitting. And a non-empty list would refuse a legitimate act: a WebAuthn
  credential is keyed on (rpId, user handle) and every registration mints a fresh handle, so somebody
  opening a second account from the same laptop would be turned away at the authenticator, by an error
  the server never sees and cannot explain.
- **The session is opened over the passkey credential and never over the recovery-codes one.** Both
  open a `Full` session, so the mistake satisfies every check constraint, every foreign key and every
  test that reads the response. What it changes is which credential a later revocation sweeps: revoking
  the passkey would leave the session standing, and replacing the card would sign the person out of a
  session their passkey opened.
- **This is the only path that creates an account.** `UserProvisioningMiddleware` stood beside it for
  a time, minting accounts holding a federated credential alone on six marked route groups; it is
  deleted, together with `ProvisionsUserAttribute`, `RegistersAccountAttribute`, `EnsureUserHandler`,
  `ResolveUserHandler`, `IUserRepository.TryAddAsync` and `NoAccountTitle`. So the invariant this
  decision establishes — exactly one federated credential, at least one passkey, exactly one set of
  recovery codes, from the instant the account exists — is now a claim about **every** account in the
  schema.
  - **`Domain.Users.User.Create` went with them, and it is worth being exact about what that buys.**
    The factory that minted an account under a fresh identifier is gone, leaving `CreateWithId` as the
    only way to obtain a `User`, and it takes the identifier from its caller — so a creating path has
    to say in its own diff where the account id came from, rather than minting one on the way past.
    It does **not** make a second such path a compile error. `CreateWithId` takes a plain `Guid`, and
    five call sites in the test projects hand it a freshly minted one deliberately. What holds the
    invariant is that exactly one **production** caller exists, which is a fact a reviewer checks and
    not one the compiler does.
- **The browser runs this ceremony.** `/register` obtains a PRF output from a real authenticator,
  draws the account's keys, mints the card, wraps eleven times and posts the account, and the response
  signs the person in. The server demanding a factor identifier, two envelopes and ten submissions is
  what kept a keyless account from ever existing while the client was catching up — the asymmetry
  [ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
  argues for, now closed on this path.
