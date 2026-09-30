# Email Change

## Table of Contents

- [Purpose](#purpose)
- [Key Entities](#key-entities)
- [Constraints](#constraints)
- [Business Rules & Invariants](#business-rules--invariants)
- [Workflows & State Transitions](#workflows--state-transitions)
- [Decision Trees](#decision-trees)
- [Integration Points](#integration-points)
- [Edge Cases & Known Gotchas](#edge-cases--known-gotchas)

## Purpose

The email change moves an existing account to the Google identity and address a fresh provider
sign-in asserts. It is the one route besides the two registration routes that reads a provider token,
and the one path in the product that writes `users.email` after the account exists. It creates no account:
`RegisterAccountHandler` is still the only code that brings one into existence, and nothing here
inserts a `users` row.

**The server half is built and the client half is not.** `POST /api/me/email-change` answers, and the
integration suite drives it end to end. No settings control calls it, and the provider's redirect
does not come back to `/app/settings`, so no browser reaches it today. Every rule below is about the
route; what the screen will do is specified in [components.md](../design/components.md) and is not
described here as built.

The act asks for **three proofs at once, and each has one owner**: a full session, a provider token
judged beside it, and a fresh passkey assertion. The rules, the order they run in, and what each
refusal leaves behind live here rather than being split across
[users-and-ownership.md](users-and-ownership.md), [sessions.md](sessions.md) and
[passkeys.md](passkeys.md), which point at this file.

## Key Entities

The change owns no table. It moves rows that already exist:

- **`FederatedIdentityChange`** — a *decision*, never an act. `Decide` compares the provider's
  subject and address with the account's stored federated credential and address, and answers one
  of three shapes: nothing to do (`IsNoChange`), an address to move with the credential kept, or a
  credential to retire (`Retired`) and a replacement to file (`Filed`) beside the address. It
  touches neither the `User` nor the `Credential` it was asked about.
- **`EmailChangeOutcome`** — what one save came to: `SubjectTaken` (`0`, so `default` is a refusal),
  `Applied`, `EmailTaken`, `FederatedCredentialMoved`. `EmailTaken` is ambiguous by construction,
  exactly as registration's is.
- **`IEmailChangeRepository`** / **`EmailChangeRepository`** — two reads, both keyed on the account
  id the session names, and one save.
- **`ProviderAuthorizationGate.VouchedIdentity`** — the subject and address the provider's token
  carried, as the gate judged them. Set on `HttpContext.Features` by the gate and read by the route
  through `ProviderAuthorizationGate.IdentityOf`, which throws when the gate is absent.
- **`EmailChange`** — the response, `{"sessionsEnded": n}`.

```mermaid
erDiagram
    USER ||--|| CREDENTIAL_FEDERATED : "exactly one, replaced whole"
    CREDENTIAL_FEDERATED ||--o{ SESSION : "revoked, then cascaded away"
    USER {
        guid Id
        string Email "UPDATE (email) — the one column that moves"
    }
    CREDENTIAL_FEDERATED {
        guid Id
        string Provider "copied from the retired row"
        string Subject "new row, never rewritten"
    }
```

Deliberately **absent** from the request: a subject, an address, or an account id. The body carries
the five members of a passkey assertion and nothing else. Deliberately **absent** from the response:
the new address, the new subject and any identifier. The caller sent the first two, and a value in a
response body is a value in a log.

## Constraints

### MUST

- **Be authenticated by a live full session**, on the fallback policy. A session opened by the
  federated credential answers `403`, and a provider token with no session answers the fallback's
  own `401`. → the three-proofs rule below.
- **Carry a provider token the provider scheme validates**, with a usable `sub` and `email` and the
  address asserted as verified — judged by `ProviderAuthorizationGate`, never by a policy. → the
  two-principals rule below.
- **Carry a fresh passkey assertion over a `reauthentication` challenge**, for a passkey registered
  to the account the session names. → the passkey rule below.
- **Judge the provider token before the passkey**, so a refused token spends no nonce. → the order
  rule below.
- **Write the retired credential's delete, the replacement's insert and the address in one save,
  inside one transaction with the session sweep.** → the one-save rule below.
- **Revoke the retired credential's sessions before deleting it**, and report how many ended.
  → the sweep rule below.

### MUST NOT

- **Read the new subject or address from anywhere but the provider's principal** — not from
  `HttpContext.User`, whose `sub` is the account id, and not from the body.
- **Merge the provider's principal into the session's.** → the two-principals rule below.
- **Return a refusal from the transactional delegate.** Every refusal throws, or the sweep commits
  behind it. → the one-save rule below.
- **Rewrite a credential's subject in place.** `credentials` holds no `UPDATE` grant of any shape.
- **Name an account in the command.** The account is the session's, read from `IUserContext`.
- **Name the address or the subject in any refusal body.**

## Business Rules & Invariants

- **Rule**: Three proofs, three owners, and none stands in for another. The **session** is the
  fallback policy's: cookie scheme, `RequireAuthenticatedUser`, `FullSessionRequirement`. The
  **provider token** is `ProviderAuthorizationGate`'s, an endpoint filter the route adds through
  `RequireProviderAuthorization()`. The **passkey** is `PasskeyReauthentication`'s, called at the
  top of `ChangeEmailHandler`.
- **Why**: each proves something the other two cannot.
  - **The session** says which account is being changed. It is also the only one of the three the
    product can end, and the only one that carries a kind — a session a federated sign-in opened
    must not reach this route, because the Google identity is exactly what such a session holds.
  - **The provider token** says the person controls the Google identity and the address being moved
    to, *now*. Nothing else in the product can vouch for an address.
  - **The passkey** says the person holding the session is the account's owner. See the passkey rule
    below for why a session is not enough on its own.
- **Enforced in**: the route declares no policy and no scheme, so it inherits the fallback — see
  `EmailChangeEndpoints` and `ProviderAuthorizationEndpointExtensions.RequireProviderAuthorization`.
  `EmailChangeEndpointTests` holds each proof's absence separately:
  - `EmailChange_WithAProviderTokenAndNoSession_IsRefused401_AndChangesNothing` — a valid token and a
    valid assertion, no cookie: the fallback's `401`, with **no** `refusal` member.
  - `EmailChange_ForALockedSession_IsRefused403_WhileAFullSessionOnTheSameAccountSucceeds` — the
    same account, a locked session refused `403` and a full one answered `200`.
  - `EmailChange_WithAFullSessionAndNoProviderToken_IsRefused401ProviderToken_AndChangesNothing` and
    `EmailChange_WithAnInvalidPasskeyAssertion_IsRefused401Assertion_AndChangesNothing` for the
    other two.
  - `EmailChange_WithoutTheClientHeader_IsRefused403` for the first-party control in front of all
    three.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The provider's principal and the session's principal meet on this one request and are
  **never merged**. The gate calls `AuthenticateAsync(ProviderAuthentication.SchemeName)` itself and
  judges *that* result. `HttpContext.User` stays the session's for the whole request.
- **Why**: the session principal's `sub` is this installation's account id; the provider
  principal's `sub` is a Google subject. Two claims of one name with two meanings on one request is
  the whole hazard.
  - **A filter that read `HttpContext.User` would find a usable `sub`** — the account's own id — and
    file a credential under it. It would answer `200`.
  - **A policy naming both schemes would merge them.** `AuthorizationMiddleware` combines the
    principals of every scheme a policy names into `HttpContext.User`, and a merged principal carries
    two `sub` claims; whichever a reader found first would decide whose identity was filed. Naming a
    scheme on the route would also take it off the fallback policy and its `FullSessionRequirement`.
    [ADR 0027](../decisions/0027-authenticate-the-email-change-on-the-session-and-a-fresh-provider-token-side-by-side.md)
    records the alternatives.
  - **`AuthenticateAsync` returns a result and replaces nothing**, which is why the session stays the
    request's identity for everything after the gate.
- **Enforced in**: `ProviderAuthorizationGate.InvokeAsync`, which sets a `VouchedIdentity` on
  `HttpContext.Features`; the route reads it through `IdentityOf` and nowhere else.
  `EmailChangeRequest` has no member for a subject or an address.
  `EmailChange_WithANewSubjectAndAddress_StoresBothFromTheProviderToken` asserts the filed subject is
  **not** the account id before it asserts it is the token's — the trap first.
  `EmailChange_TheRequestBodyCannotCarryAnAddressOrSubject` sends both in the body and asserts
  neither landed and the token's did. `FederatedIdentityChangeTests` asserts the same "not the
  account id" at the domain.
- **Counterexample**: `httpContext.User.FindFirstValue("sub")` in the route delegate. It compiles,
  every status is right, and every account moves to a "Google identity" equal to its own id.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Which routes reach the provider scheme is readable off the route table, through a marker.
  `RequireProviderAuthorization()` adds `RequiresProviderAuthorizationMetadata` and the gate as one
  act, and the marker's constructor is `internal`, so nothing outside `Api` can add it alone.
- **Why**: an endpoint filter is a delegate, not metadata. A dump of the email change's
  `RouteEndpoint.Metadata` shows the handler's method, binding and response metadata and the marker,
  and no trace of the filter type (measured, as `RegistrationRouteTests` records). Without the
  marker, a census of which routes reach the provider scheme would read the registration group's
  policy and miss this route entirely.
- **Enforced in**: `RegistrationRouteTests.TheProviderScheme_IsReachedByExactlyTheRegistrationRoutesAndTheEmailChange`
  pins two sets, each against a written-out list: the routes whose policy names the provider scheme
  (the two registration routes), and the routes carrying the marker (`/api/me/email-change`). A
  registration route that moved onto the gate, or this route moving into a policy, is a red in both.
  It also pins the fallback's own schemes to the session cookie's alone.
  - **What it cannot see**: a route whose own code calls `AuthenticateAsync` with the provider scheme,
    or adds `ProviderAuthorizationGate` directly without the marker. Review holds that.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The provider's claims are judged by **one** definition, `ProviderClaims.RefusalFor`,
  shared with `RegistrationClaimGate`: a non-blank `sub` and `email`, and `email_verified` read by
  `bool.TryParse` as `true`. The missing-claim check runs first.
- **Why**: two copies of three checks is how one gate starts admitting `"1"` as verified while the
  other refuses it. The verdict is shared; the response is not. Registration answers by title
  alone; this route answers with a `refusal` word, because here a `401` has three causes with three
  different next steps — sign in to Google again, choose an address Google verifies, retry the
  passkey.
- **Enforced in**: `ProviderAuthorizationGate` maps `ProviderClaims.Refusal.MissingClaims` and a
  failed authentication to `refusal: "provider_token"`, and `UnverifiedEmail` to
  `refusal: "email_unverified"`, each under its own title. `EmailChangeEndpointTests` drives the
  real `JwtBearer` handler for the bearer-shaped cases:
  - `EmailChange_WithAProviderTokenTheBearerHandlerRejects_IsRefused401ProviderToken_AndChangesNothing`
    — a forged signature, a wrong audience, an expired token and a wrong issuer.
  - `EmailChange_WithAProviderTokenTheBearerHandlerValidates_StoresItsSubjectAndAddress` is the
    control, without which a filter refusing every bearer would pass the four above.
  - `EmailChange_WithoutSubOrEmailClaims_IsRefused401ProviderToken` and
    `EmailChange_WithABlankSubOrEmailClaim_IsRefused401ProviderToken` for the claim shapes, and
    `EmailChange_WhenTheProviderDoesNotVerifyTheAddress_IsRefused401EmailUnverified_AndChangesNothing`
    for `"false"` and an absent claim.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The change is also authorized by a **fresh passkey assertion** over a
  `reauthentication` challenge, exactly as erasure is. The gate runs first in the handler, to
  completion, **outside** the transactional delegate, and publishes no identity.
- **Why**: a stolen full-session cookie plus the attacker's own Google account is otherwise enough
  to re-point the account's Google identity and address at the attacker. The session proves only
  that *somebody* holds the cookie; the provider token proves only that the *caller* controls the
  Google account they chose, which the attacker does. Neither says the caller is the account's
  owner. This gate was added above the story's acceptance criteria, by decision — see
  [the decision log](_decision-log.md).
  - **Outside the delegate**, for erasure's two reasons, which `ChangeEmailHandler` repeats at the
    call: the nonce must stay spent when the change rolls back, and the delegate is replayed by the
    execution strategy, where a second consume would refuse a valid request.
  - **First in the handler**, because the next statement is the subject pre-check, which asks the
    exempt discovery lookup whether a Google identity holds an account. Asked by an unproven caller,
    that lookup is an oracle for which Google identities hold accounts.
  - **No identity is published**, for the reason [passkeys.md](passkeys.md) gives on the
    re-authentication ceremony: `PasskeyReauthentication` takes `IUserContext` and looks the key up
    owner-scoped, so another account's passkey answers nothing.
- **Enforced in**: `ChangeEmailHandler.HandleAsync`, its first call.
  `ChangeEmailHandlerTests.HandleAsync_WhenTheAssertionFails_ReadsAndWritesNothing` asserts that a
  refused assertion reaches no lookup, no repository, no executor and no sweep.
  `EmailChange_WithAnotherAccountsPasskey_IsRefused401Assertion_AndChangesNeitherAccount` asserts
  both accounts survive.
- **Counterexample**: dropping the gate because "the provider already re-authenticated them". The
  provider re-authenticated whoever holds the Google account they chose, which is not the question.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The provider token is judged **before** the passkey. A refused token spends no
  challenge.
- **Why**: the passkey gate consumes its nonce whatever happens next. A person told their Google
  sign-in lapsed has to be able to sign in again and retry with the assertion they already made. The
  reverse order would spend it on every provider refusal and make them run the ceremony twice.
- **Enforced in**: the pipeline order itself — an endpoint filter runs before the route delegate,
  and the passkey gate is inside the handler the delegate calls.
  `EmailChange_RefusedForItsProviderToken_LeavesThePasskeyChallengeUnspent` sends one assertion
  twice: refused `401 provider_token` with no token, then `200` with a valid one.
  - **The cost**: a filter runs after model binding, so a malformed body is a framework `400` before
    the token is looked at — the same accepted cost `RegistrationClaimGate` argues.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A new subject is a credential **retired and one filed**, never a subject rewritten, and
  the delete, the insert and the address update land in **one save**.
- **Why**: `credentials` holds `INSERT` and `DELETE` and no `UPDATE` of any shape, so "replace" can
  only be delete plus insert. One save rather than two is what makes a refusal write nothing: split,
  the swap commits and the address is refused after it, and the account answers to a Google identity
  the caller was told did not attach.
  - **EF sends the `DELETE` ahead of the `INSERT`, and that is measured.**
    `IX_credentials_user_id_federated` allows one federated row per account, so an insert first would
    collide with the row about to go. A variant saving the insert ahead of the delete reddened seven
    of the fourteen cases in `EmailChangeRepositoryTests`, six on a `23505`.
  - **The address goes through `UPDATE (email)`**, the one column grant on `users`. `users.email`
    has two writers: registration's insert, and this route's `UPDATE (email)` through
    `User.ChangeEmail`. The repository loads the user and moves its one property, so EF emits an `UPDATE`
    naming `email` alone; a detached `Update(user)` would name every column and die with `42501` on
    `created_at_utc`.
  - **The replacement's provider is read off the retired row**, never named in `Decide`, so a
    replacement cannot be filed under an issuer the account did not already hold.
- **Enforced in**: `EmailChangeRepository.ApplyAsync`.
  `ApplyAsync_WithANewSubject_DeletesTheRetiredInsertsTheFiledAndUpdatesTheEmail_InOneSave` is the
  success path, and the case that goes red if an EF upgrade reorders the batch; it also reads
  `created_at_utc` back unchanged. `ApplyAsync_WithAnAddressOnlyChange_UpdatesOnlyTheEmail` asserts on
  the wire that one `UPDATE users` names `email` and not `created_at_utc`, and nothing touches
  `credentials`. `ApplyAsync_ToAnAddressAnotherAccountHolds_AnswersEmailTaken_AndChangesNothing`
  carries a new subject beside the taken address, so "nothing" covers the credential swap too. Every
  case runs on the least-privilege role, because a superuser skips the grant checks the design rests
  on.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: Retiring the federated credential **revokes its sessions, then deletes it**, and the
  response reports the count as `sessionsEnded`. The sweep is keyed on the **retired** credential.
  The requesting session was opened by a passkey and survives.
- **Why**: the rule every credential-removal path follows, from [sessions.md](sessions.md). The
  cascade from `credentials` removes the same session rows either way, so the count is the only
  evidence the sweep ran. Keyed on the filed credential it would report zero; keyed on the account
  it would sign the person out of the browser in their hand.
  - **A second `DiscardTrackedEntities()` sits between the sweep and the save.** The sweep loads the
    retired credential's sessions into the change tracker; removing the credential with them tracked
    makes EF emit its own `DELETE FROM sessions`, on a table the role holds no `DELETE` on, and the
    request dies with `42501`. The answer is the discard, never a grant.
- **Enforced in**: `ChangeEmailHandler`, through `RevokeSessionsForCredentialHandler`.
  `HandleAsync_WithANewSubject_RevokesTheRetiredCredentialsSessionsBeforeApplying` compares the call
  order and names the swept credential. `HandleAsync_WithANewSubject_ReportsTheSessionsItEnded` seeds
  three sessions on the retired credential and one on the passkey, and requires `3` and the passkey's
  session live. `EmailChange_ReplacingTheCredential_EndsTheSessionsTheRetiredCredentialOpened_AndReportsThem`
  does it end to end and reads `GET /api/me` on the requesting session afterwards.
  - **No unit test pins the second discard** — the fakes have no change tracker, as the handler says
    at the call. Two endpoint tests do, measured: with the discard deleted,
    `EmailChange_ReplacingTheCredential_EndsTheSessionsTheRetiredCredentialOpened_AndReportsThem` and
    `EmailChange_ForALockedSession_IsRefused403_WhileAFullSessionOnTheSameAccountSucceeds` both went
    red with `42501`, from the EF cascade into the tracked sessions. Each seeds a session on the
    credential the change retires.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The sweep and the save share **one transaction**, and every refusal leaves the delegate
  by **throwing**.
- **Why**: the sweep commits on a save of its own before the change is applied. A delegate that
  *returned* a refusal would hand the executor a commit of that sweep, signing the person's other
  browsers out of a Google credential the response says is still theirs.
  - **Replay hygiene**: the delegate discards tracked entities as its first line, because the
    execution strategy replays it against a database that rolled back and a change tracker that did
    not.
- **Enforced in**: `ChangeEmailHandler`, with the argument on the class and at the throw.
  `HandleAsync_OnEmailTaken_WithTheSubjectNowHeldElsewhere_AnswersProviderIdentityInUse`,
  `HandleAsync_WhenApplyAnswersSubjectTaken_AnswersProviderIdentityInUse` and
  `HandleAsync_OnFederatedCredentialMoved_AnswersAccountIdentityMoved` each assert the executor
  committed nothing. `EmailChange_RefusedAfterTheSweepWouldRun_LeavesTheRetiredCredentialsSessionsLive`
  holds it end to end: a locked session on the retired credential, a taken address, a `409`, and the
  session still live. `HandleAsync_WhenTheUnitOfWorkIsReplayed_DiscardsTrackedEntitiesAtTheStartOfEveryAttempt`
  holds the replay discard, measured: with that first discard deleted it went red with `[]` against
  `[1,2]`.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: What counts as a change. The subject is compared **ordinally after trimming**; the
  address is compared **ordinally once normalised** through `Email.Create`. The same subject and the
  same address is **no change**: `200`, `sessionsEnded: 0`, nothing written. The same subject with a
  new address moves the address and **keeps** the credential and its sessions.
- **Why**: `CreateFederated` stored the subject trimmed, and the `sub` claim is case-sensitive, so a
  case-only difference is another Google identity. The address comparison is ordinal on purpose:
  the unique index's case-insensitive collation says who *else* may hold an address, not whether this
  one changed — re-casing one's own address is a change that lands. Ending sessions on an
  address-only change would sign a person out over a column no session depends on.
- **Enforced in**: `FederatedIdentityChange.Decide`, held by `FederatedIdentityChangeTests` —
  `Decide_SubjectDifferingOnlyInCase_IsANewSubject`, `Decide_SubjectWithSurroundingWhitespace_IsComparedAfterTrim`,
  `Decide_AddressDifferingOnlyInCase_IsAnAddressChange` and the rest.
  `EmailChange_ToTheAddressAlreadyStoredUnderTheSameSubject_Answers200AndWritesNothing` records every
  statement the API sends and finds no write to `users` or `credentials`.
  `EmailChange_ForTheSameSubjectWithANewAddress_KeepsTheCredentialAndUpdatesTheAddress` and
  `ApplyAsync_ToTheSameAddressInAnotherCase_OnTheSameAccount_Succeeds` cover the other two shapes.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: Three `409`s, each with its own `conflictKind`, and none names the address or the
  subject.
  - `provider_identity_in_use` — the chosen Google identity is attached to another account.
  - `email_already_linked` — the address is held by another account, and the chosen Google
    identity is attached to nobody else. Registration's member, for the same fact.
  - `account_identity_moved` — another change of this account's own Google credential landed between
    this request's read and its save.
- **Why**: the three have different next steps. The first two are both answered by choosing a
  different Google account, but tell the person different facts. The third is a timing fact: read the
  address back, and change it again only if it is not the one chosen. `ConflictKind` carries the
  argument for each.
  - **`provider_identity_in_use` is reached three ways**: a pre-check before any transaction opens,
    so the common case needs no sweep to roll back; the save refused on
    `IX_credentials_provider_subject`, when another account filed the subject after the pre-check;
    and an `EmailTaken` save whose re-read finds another account on the subject.
  - **`EmailTaken` is ambiguous and a re-read settles it**, as registration's is: one save can breach
    the subject and the address at once, and PostgreSQL names one. The re-read runs inside the
    delegate, which works because EF takes a savepoint before a save inside an open transaction. The
    account's **own** id is not "another account" — it means the address alone collided.
  - **`account_identity_moved` is reached two ways**: the retired row's `DELETE` matching nothing,
    and — the realistic race, measured — the filed row's `INSERT` meeting the winner's replacement on
    `IX_credentials_user_id_federated`.
  - **Not an enumeration oracle.** Reaching the subject lookup takes a full session, a passkey
    assertion and a provider token for that exact subject, so a caller can only ask about a Google
    identity they already control.
- **Enforced in**: `ChangeEmailHandler.RefusalFor` and the pre-check; `EmailChangeRepository.ApplyAsync`
  narrows each catch on a pinned constraint name, so any other `23505` escapes as a `500`.
  - End to end: `EmailChange_WithASubjectAnotherAccountIsFiledUnder_IsRefused409ProviderIdentityInUse_AndChangesNeitherAccount`
    and `EmailChange_ToAnAddressAnotherAccountHolds_IsRefused409EmailAlreadyLinked_AndChangesNeitherAccount`
    (exact and re-cased), each asserting the body names neither value.
  - In the handler: `HandleAsync_WhenTheSubjectBelongsToAnotherAccount_RefusesBeforeTheTransaction`,
    the three kind tests above, `HandleAsync_OnEmailTaken_WithTheSubjectHeldByNobodyElse_AnswersEmailAlreadyLinked`
    and `HandleAsync_WithASubjectInSurroundingWhitespace_PreChecksAndReReadsTheTrimmedSubject`.
  - In the repository: `ApplyAsync_WithASubjectAnotherAccountHolds_AnswersSubjectTaken_AndChangesNeitherAccount`,
    `ApplyAsync_ToAnAddressAnotherAccountHoldsInAnotherCase_AnswersEmailTaken`,
    `ApplyAsync_WhenTheRetiredCredentialWasAlreadyDeleted_AnswersFederatedCredentialMoved`,
    `ApplyAsync_WhenARacingChangeAlreadyReplacedTheRetiredCredential_AnswersFederatedCredentialMoved`,
    `ApplyAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape` and
    `ApplyAsync_AfterARefusedSave_LeavesTheTransactionUsable` for the savepoint.
  - Which kind each site raises, in source order: the `ChangeEmailHandler` row of
    `ConflictKindDispositionCensusTests`.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The account changed is the session's, and nothing else can name one. `ChangeEmailCommand`
  carries a subject, an address and an assertion. The credential read is scoped on the owner **and**
  the type.
- **Why**: `credentials` is exempt from row-level security, so the owner in the predicate is the only
  thing narrowing the read, and the credential it returns is the one the save deletes. The type keeps
  a passkey or a recovery-code credential from ever being handed to `Decide` as the one to retire.
  The delete that follows takes the loaded entity, which is what [ADR 0014](../decisions/0014-scope-the-credential-delete-in-the-application.md)
  requires of every credential delete.
- **Enforced in**: `EmailChangeRepository.FindFederatedCredentialAsync`, and `ChangeEmailHandler`
  reading `IUserContext.UserId`.
  `EmailChangeRepositoryTests.FindFederatedCredentialAsync_ReturnsOnlyThisAccountsFederatedCredential`
  seeds a stranger that sorts first and a passkey filed ahead of the account's federated row, so a
  read missing either predicate returns the wrong row. `EmailChange_ForOneAccount_LeavesAnotherAccountsFederatedCredentialWhereItWas`
  holds the isolation end to end. `HandleAsync_UsesTheSessionsAccountIdAndNeverAnIdFromTheCommand`
  asserts every port was handed the session's id and nothing else.
  `Decide_WhenCurrentBelongsToAnotherUser_Throws` and `Decide_WhenCurrentIsNotFederated_Throws` hold
  the same two rules again at the domain.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: An account with no federated credential, or a session naming no user row, is a `500` on
  purpose.
- **Why**: every account is registered with exactly one federated credential, so its absence is an
  integrity failure, not a request error. Nothing the caller sent is wrong and nothing they could
  send would help — the reasoning `RevokePasskeyHandler` applies to a missing manifest.
- **Enforced in**: `ChangeEmailHandler`, throwing `InvalidOperationException`.
  `HandleAsync_WhenTheAccountHoldsNoFederatedCredential_Throws`.
- **Source**: `[SOURCE: discussion]`

## Workflows & State Transitions

There is no state to move through. The account has one federated credential and one address before
and after; a change swaps them, and nothing records that it happened beyond the new credential's own
`created_at_utc`.

```mermaid
sequenceDiagram
    participant C as Client
    participant O as POST /api/passkeys/reauthentication/options
    participant R as POST /api/me/email-change
    participant F as ProviderAuthorizationGate
    participant H as ChangeEmailHandler
    participant D as PostgreSQL

    C->>O: session cookie
    O->>D: issue a reauthentication challenge
    O-->>C: challenge
    Note over C: the authenticator signs it; Google is signed in to separately
    C->>R: cookie + provider bearer + the five assertion members
    Note over R: fallback policy — a full session, or 401/403
    R->>F: after model binding
    F->>F: AuthenticateAsync(provider scheme) — never HttpContext.User
    F->>F: ProviderClaims.RefusalFor — or 401 provider_token / email_unverified
    F->>R: VouchedIdentity(subject, email) on Features
    R->>H: ChangeEmailCommand(subject, email, assertion)
    H->>D: consume the nonce, verify the passkey — outside the transaction
    H->>D: is the subject another account's? — or 409, no transaction opened
    H->>D: BEGIN
    H->>D: load the federated credential (owner + type) and the user
    H->>H: Decide — no change, address only, or retire and file
    H->>D: revoke the retired credential's sessions (own save)
    H->>H: DiscardTrackedEntities()
    H->>D: DELETE credential, INSERT credential, UPDATE users (email) — one save
    D-->>D: cascade: the retired credential's sessions and tokens
    H->>D: COMMIT
    R-->>C: 200 {"sessionsEnded": n}
```

## Decision Trees

How `POST /api/me/email-change` is answered:

```
IF the request carries no X-Budgetoid-Client header
  THEN 403 from FirstPartyRequestMiddleware
ELSE IF no live session cookie                               ← a provider bearer alone is not one
  THEN 401 from the fallback policy, no refusal member
ELSE IF the session was opened by the federated credential
  THEN 403 from FullSessionRequirement
ELSE IF the body does not bind
  THEN 400 from the framework                                ← before the token is looked at
ELSE IF the provider token is absent, invalid, or lacks a usable sub or email
  THEN 401 refusal "provider_token"                          ← no nonce spent
ELSE IF the provider does not assert the address as verified
  THEN 401 refusal "email_unverified"                        ← no nonce spent
ELSE IF the passkey gate refuses the assertion
  THEN 401 refusal "assertion", the nonce spent
ELSE IF another account holds the chosen Google identity
  THEN 409 provider_identity_in_use                          ← no transaction opened
ELSE open the transaction
  IF the account holds no federated credential or no user row
    THEN 500
  ELSE IF the subject and the address both match what is stored
    THEN 200 {"sessionsEnded": 0}, nothing written
  ELSE IF the address is not one this product accepts
    THEN 400 keyed under Email
  ELSE
    IF the subject changed
      revoke the retired credential's sessions, discard the tracker
    save once
    IF the save lost on the provider subject
      THEN 409 provider_identity_in_use
    ELSE IF it lost on the address
      re-read the subject                                    ← the name alone cannot separate them
      IF another account holds it now
        THEN 409 provider_identity_in_use
      ELSE
        THEN 409 email_already_linked
    ELSE IF the retired credential was already gone, or another replacement stands
      THEN 409 account_identity_moved
    ELSE
      THEN 200 {"sessionsEnded": n}
```

Every arm from the passkey gate down has spent the challenge. Every refusal inside the transaction
rolls the sweep back.

## Integration Points

- **[Registration](registration.md)** — the other route that reads a provider token. It reaches the
  scheme through its group's **policy**, because its caller holds no session; this route reaches it
  through a **filter**, beside a session. The claim judgement is shared through `ProviderClaims`; the
  response shapes are not.
- **[Sessions](sessions.md)** — the fallback policy and `FullSessionRequirement` gate the route; the
  sweep is `RevokeSessionsForCredentialHandler`, and this is one of its callers.
- **[Passkeys](passkeys.md)** — the `reauthentication` pool and `PasskeyReauthentication`, shared
  with erasure, passkey revocation, recovery-code generation and the rotation begin. Every passkey
  refusal in the product carries the constant `refusal: "assertion"` from
  `PasskeyVerificationExceptionHandler`, so a client here can tell "retry the passkey" from "sign in
  to Google again" without the refusal telling anybody *why* the passkey failed.
- **[Users & Ownership](users-and-ownership.md)** — the federated credential, `users.email`, the
  `UPDATE (email)` grant this route spends, and the rule that the provider changes a stored account
  only on a request the person makes for it.
- **The grant matrix** — `credentials` `INSERT` and `DELETE`, `users` `UPDATE (email)`, `sessions`
  `UPDATE (revoked_at_utc)`. Nothing new was granted for this route. See
  [data isolation](../engineering/data-isolation.md) for the credential reads and the delete.
- **[Log redaction](../engineering/log-redaction.md)** — `ProviderAuthorizationGate` writes nothing
  to the log. `LogRedactionTests` drives the route on both hosts: on the main host a
  `409, 409, 401, 200` sequence covering both conflicts, the unverified address and a success, and on
  the real-bearer host a forged token (`401`) and a valid one (`200`).
- **The web client** — not built. The client's `apiCredentialsInterceptor` attaches the provider's
  bearer on the two registration routes only, and nothing calls this route, so no browser sends it a
  token today. The screen is specified in [components.md](../design/components.md).

## Edge Cases & Known Gotchas

- **The client is not built.** No settings control posts here and the provider's redirect does not
  return to `/app/settings`. Only the integration suite reaches the route. Nothing in this file
  describes a screen as working.
- **`sessionsEnded` is `0` on every real account today.** The sweep can only find sessions the
  federated credential opened, and nothing opens a locked session yet — see
  [sessions.md](sessions.md). Every test that expects a non-zero count seeds the locked session
  through the database.
- **The repository's concurrency catch is narrowed by a clause no test reaches.** The catch admits a
  `DbUpdateConcurrencyException` only when a credential was retired **and** every conflicting entry
  is that credential's delete. The first half is held by
  `ApplyAsync_WhenTheAccountIsErasedUnderneathTheSave_LetsTheConcurrencyFailureEscape`. The entries
  clause is not: cut it to "a credential was retired" and every case stays green, so the narrowing
  to the retired credential, and `All` over `Any`, rest on the argument in `EmailChangeRepository`
  alone.
- **An erasure racing a subject change answers `409 account_identity_moved`.** [Guessing] Argued in
  `EmailChangeRepository`, not run: the erasure's cascade takes the retired credential too, so its
  `DELETE` is the first statement to fail and the catch reads it as a racing change. The person is
  told another change landed, when the account is gone. An **address-only** change racing an erasure
  escapes as a `500` instead, which is the tested case above.
- **There is no rate limit.** Each attempt costs the caller a fresh passkey assertion and a valid
  provider token, but nothing counts attempts. The same accepted gap as the rest of the API —
  [passkeys.md](passkeys.md) records it for the anonymous options leg.
- **A provider token's freshness is bounded only by its own `exp`.** The bearer handler's only
  time check is `ValidateLifetime` in `Api/Program.cs`, and nothing here reads `iat` or `auth_time`,
  so a token minted up to its expiry ago is accepted. "A fresh provider sign-in" means "an unexpired
  one".
- **A refused address and every `409` come after the passkey gate, so the challenge is spent.** An
  address `Email.Create` refuses (over 254 characters, or blank after trimming) is a `400` from
  inside the transaction; the pre-check's `409` comes before the transaction and the save's `409`s
  inside it, and the nonce was consumed ahead of all of them. The person runs the ceremony again, as
  on every assertion-gated route.
- **The response carries no address.** A client that wants to show the new address reads
  `GET /api/me`, which is also the remedy `account_identity_moved` names.
- **Nothing records that a change happened**, beyond the replacement credential's `created_at_utc`
  (and, in the database's own write-ahead log and backups, that a row changed). An address-only
  change leaves no trace in any column. That is the product's no-audit-trail rule holding, not a
  gap — see [users-and-ownership.md](users-and-ownership.md#must).
