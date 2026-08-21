# ADR 0018 — Give the wrapped account keys a policed table, and their own factor identifier

- **Status:** Accepted
- **Date:** 2026-08-13
- **Area:** Persistence / Domain (row-level security, grant matrix, recovery factors)

## Context

The account's narrative is to be encrypted client-side. The account owns **one** content key and
**one** index key, and every recovery factor stores its own copy of both, wrapped under a
key-encryption key derived from that factor. Deriving the keys from a credential instead would give a
second passkey a second index key, two blind index values for one name, and a uniqueness constraint
that appears to work while enforcing nothing.

So the server has to hold wrapped copies. Three questions had to be answered before a row could be
written, and two of them were already answered elsewhere in this repository.

**Where the rows live** was settled before this change existed. The `recovery_code_hashes` block of
`app-role-grants.sql`, the exemption reasons in `RowLevelSecurityCoverage.cs`, and
[data-isolation.md](../engineering/data-isolation.md) all say the same thing, and two of them name
this work by name: a wrapped key is read **after** the request has an identity, so it belongs on a
table carrying `user_id` — never as a column on one of the tables exempt from row-level security
because they are read *before* one exists. Those exemptions are held to their reasons by pinned
column sets whose rule is *move the column, never widen the pin*.

**What binds a wrapped key to its factor** is associated data, so that a copy moved to another factor
fails to authenticate. That value has to be known to the client at the moment it wraps — and the
client does not know one. Credential ids are minted server-side, and `POST /api/passkeys/registration`
returns no body, so nothing tells the client which id its new credential got.

The obvious answer was to let the client mint `credentials.id` and use it. That is the decision this
ADR exists to record refusing.

## Decision

**Wrapped account keys live on `wrapped_account_keys`, one row per recovery factor, policed by
`user_isolation`. The value their associated data binds is a `factor_id` column of that table, minted
by the client — and deliberately not `credentials.id`. That column is the table's primary key.**

1. **One row per factor, and a factor is not a credential.** `factor_id` is the primary key;
   `credential_id` is an ordinary, **non-unique** column. A passkey is one factor and one credential.
   A set of recovery codes is one credential and **ten** factors, because each of the ten codes is a
   secret of its own from which the client derives its own key-encryption key — so a set stores ten
   rows, one per code, and `credential_id` repeats across them.

   The first draft of this decision keyed the table on `credential_id`, and it was wrong in a way
   worth recording rather than quietly correcting: with one row per set, only whichever code that
   row's envelopes had been sealed under could open the account. A person redeems whichever code they
   still have, so nine redemptions out of ten would have opened a session that unlocks nothing — on
   the day they had already lost their authenticator. The client API was per-code from the start and
   the schema was per-set; nobody reconciled the two until a review asked which of the ten it was.

   `wrapped_content_key` and `wrapped_index_key` are both `NOT NULL`, so "a factor carries a copy of
   both keys, or no row at all" is a declarative fact. **That a factor has a row at all is not one** —
   one-to-optional is not expressible without a trigger, which ADR 0002 forbids pushing down. What
   holds it is a **property of the write surface rather than a count**: every path that can bring a
   factor into existence demands the members and writes the row in the credential's own
   `SaveChanges`, so no path can half-comply. The number of such paths is a fact about today and the
   property is the rule — see the consequence below. The composite foreign key to
   `AK_credentials_id_user_id_type` cascades, so revoking a factor takes its keys with it, and
   replacing a set takes all ten.

   Nothing links a code's `recovery_code_hashes` row to its `wrapped_account_keys` row, deliberately:
   the link would have to live on the hash table, which is exempt from row-level security and holds a
   pinned column set. A client tries each row in turn and exactly one opens, which is what the
   associated data is for.

2. **Policed, with no new rule and no exemption entry.** The table carries `user_id`, so the shared
   classifier requires `user_isolation` of it without being told, and it appears in no exemption list.
   That absence is the mechanism working rather than an omission, and it is the whole of the argument
   — nothing about this table's isolation is re-argued, only cited.

3. **The envelope's shape is refused by the database, not merely by the application.** Each column is
   `bytea` with two checks: `length(...) = 61` and `get_byte(...,0) = 1`. Sixty-one is a width and not
   a cap — one version byte, a twelve-byte nonce, thirty-two bytes of ciphertext because AES-GCM
   ciphertext is the length of its plaintext, and a sixteen-byte tag — so an envelope over a wrapped
   key has exactly one legal size. The version check is IFR-007 at the lowest layer that can hold it:
   a successor version does not exist, so a row carrying one is a client claiming a contract this
   deployment has never implemented.

4. **`factor_id` is its own column, and `credentials.id` stays server-minted.**
   [ADR 0014](0014-scope-the-credential-delete-in-the-application.md) states as a heading that no
   source of a `Credential` accepts a caller-chosen id, so that a fabricated instance can never name
   an existing row — which matters because the credential deletes are issued by primary key against a
   table carrying no row-level security policy. Letting the client choose that id would retire that
   leg, leaving only the owner-bearing read in the same transaction, and the ADR is explicit that the
   first leg is what makes the third one checkable. Six comment blocks across the Domain ports, both
   repositories, the in-memory fake and `app-role-grants.sql` assert the retired property today.

   A separate column costs one `uuid` — which then became the primary key — and touches none of that.
   It also removes
   a naming hazard rather than adding one: `ReauthenticationAssertion.CredentialId` already means the
   WebAuthn handle, so "credential id" was about to acquire a third meaning.

5. **`SELECT, INSERT`, and nothing else.** No `UPDATE` of any shape: every column is immutable, and
   registering or revoking a factor writes new rows rather than editing them. A content-key rotation
   is the one operation that would ever rewrite these two columns, and it must arrive with its own
   argument for the grant it needs.

   **`SELECT` is granted to a reader that is not production code, and that is a real tension worth
   naming rather than glossing.** The paragraph above argues that withholding a privilege until
   something uses it costs nothing while granting an unused one leaves a standing capability with no
   reader to explain it — and then `SELECT` is granted to the row-level-security probes and to nothing
   else. The difference that decides it: without the grant, the policy could never be *observed*, so
   the isolation this whole decision rests on would be asserted and unchecked. An ungranted `UPDATE`
   leaves nothing unobservable; an ungranted `SELECT` does. When the unlock story arrives it will
   bring the production reader this grant is already sized for, and this paragraph should go with it.

   No `DELETE`: revoking a factor removes its keys by the cascade from `credentials`, which runs with
   the referencing table owner's privileges rather than the application role's.
   [ADR 0017](0017-consume-a-recovery-code-by-deleting-its-row.md) states the hazard the absent grant
   closes — with `DELETE` granted, an EF cascade into rows the change tracker happens to be holding
   succeeds *silently*; without it, the same mistake dies loudly with `42501`. That now binds a
   second table: `GenerateRecoveryCodesHandler` already must never materialise the replaced set's
   child rows, and a future "load the wrapped keys so we can count them" read on that path is how it
   breaks.

### What the database cannot check, stated rather than implied

The policy scopes which rows the application role may see and write. It cannot tell a content key
from an index key, and it cannot notice the two envelopes being written to each other's column — both
are 61 bytes, both carry version 1, both columns are `NOT NULL`. That binding is cryptographic and
lives in the associated data of each envelope, checkable only by a client holding the key-encryption
key. The database's part is that one account's row is unreachable from another's session.

## Alternatives considered

**Put the wrapped key on `recovery_code_hashes`, beside the code it is wrapped under.** The obvious
place, and the one that block refuses in writing. The exemption there is granted to a *query* — the
anonymous lookup by hash — but PostgreSQL applies it to a whole *table*, so a column read only after
redemption has answered who is asking would become readable by every session.

**Put it on `credentials`, where every factor already has a row.** Same shape of mistake, on the
table whose exemption is the most load-bearing of all: it is read before the request has any identity
at all.

**Let the client mint `credentials.id` and use it as the associated data.** Uniform across both
factor kinds, and the binding would have been the row's own primary key with nothing to keep in sync.
Rejected on ADR 0014, above. It would also have cost more than it looked: `PasskeyRepository` filters
its unique-violation catch on the WebAuthn index alone, so a colliding client-minted primary key
would have surfaced as a 500 rather than a refusal, and fixing that means `TryAddAsync` can no longer
answer with a `bool` — one message would have had to cover two unrelated races.

**Pre-mint the id in the begin-registration options and bind it to the challenge.** Keeps ids
server-assigned and still hands the client a value before it wraps. It needs a column on
`webauthn_challenges`, an exempt table whose pinned column set says *move the column, never widen the
pin* — a whole table's worth of cost for a property a plain column on the policed table already gives.

**Register the factor first, then upload its wrapped keys in a second request.** Removes the need for
any client-known identifier. It also makes a factor that exists without its keys a reachable state —
and reachable is the operative word, because nothing in the schema forbids that state. The two
`NOT NULL` columns say a row carries both keys or neither; what says a factor *has* a row is that
every write path demands the members and writes them in the same `SaveChanges` as the credential. A
second request would be a write path that does not, and the first one whose failure leaves a passkey
that proves identity and unlocks nothing.

## Consequences

- **A wrapped key of one account is unreachable from another's session, and something has watched
  that happen.** `Database_HidesAnotherAccountsWrappedKeys_FromASessionNamingThisUser` reads the table
  as the application role on a session naming one owner and asserts the other owner's row is absent;
  `Database_RefusesAWrappedKeyReadOnASessionNamingNobody` pins the `22P02` an unset
  `app.current_user_id` reaches the policy as. Both go red the day the policy goes and the day the
  grant goes, which is why `app-role-grants.sql` names them where it justifies granting `SELECT` to a
  table no application code reads yet.
- **Every column is refused an `UPDATE` individually.**
  `Database_RefusesEveryUpdateOnAWrappedAccountKey_WhileStillAllowingInsert` states it column by
  column, each `42501` paired with a permitted insert on the same connection so the refusal cannot be
  vacuous.
- **The missing `DELETE` is safe rather than merely narrow, and one test says why.**
  `Database_RefusesADeleteOnAWrappedAccountKey_WhileTheCascadeFromItsCredentialStillTakesIt` refuses
  the direct delete and then proves the row leaves anyway when its credential does.
- **The baseline was regenerated rather than extended.** The rebaseline window in `migrations-guard`
  is open and production holds no data; the window is *not* closed by this change, because further
  schema work in this epic follows. Regenerating obliges resetting production's `__EFMigrationsHistory`
  in the same deploy — see [migrations.md](../engineering/migrations.md) and `DEPLOYMENT.md`.
- **No endpoint returns a wrapped key, on purpose.** Nothing can unlock anything yet: there is no
  client ceremony able to produce a key-encryption key and no ciphertext to read. The story that needs
  such an endpoint argues for it in place, where its own threat model can be stated.
- **`factor_id` is unique across the table rather than per account.** A collision between two accounts
  therefore surfaces as the same refusal as a collision within one. The value is 122 random bits, so
  this is a statement about what the schema guarantees rather than about an event anyone will see.
- **A set's ten identifiers are required to differ by the handler, not by the key.** Left to
  `PK_wrapped_account_keys`, the refusal would arrive as a 409 *after* the previous set had been
  deleted inside the same transaction, and it would say "that factor identifier is already registered"
  about a factor the client never registered. It is also the same evidence the existing
  verifier-distinctness rule is: a client repeating an identifier inside one set has randomness that
  is not what it claims.
- **The write surface has three paths, and the count moving is what showed the rule was never the
  count.** This decision was written against two — registering a passkey and issuing a set of
  recovery codes — and account registration is the third, writing the passkey's pair and one pair per
  code, **eleven rows**, inside the one save that creates the whole account
  ([ADR 0021](0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md)).
  It arrived without weakening anything, which is what the property holding looks like from the
  outside. Read the rule as the property and the number as a fact about today: a **fourth** path that
  demands the envelopes and writes them in the credential's own save costs nothing, and a fourth that
  does not creates a factor holding no share of the keys and **reddens nothing** — there is no
  constraint, no policy and no test that would see it.
- **A factor identifier has exactly one spelling on the wire, and `Guid.TryParseExact(…, "D")` is not
  enough to say so.** That overload also accepts upper-case hex, mixed case and surrounding
  whitespace — it trims before it looks at the format. Since the identifier is the associated data
  both envelopes were sealed with, a client that bound one spelling and sent another finds its own
  envelopes permanently unopenable, with no error naming the cause. Every write path therefore
  compares the text ordinally against `parsed.ToString("D")`, through the one
  `CanonicalFactorId.TryParse` they all call — a rule that drifted on one of them would seal an
  account's keys under a spelling the others cannot reproduce. That comparison looks redundant
  beside the parse and is not: it is the rendering itself rather than a hand-maintained copy of it,
  so it cannot drift from what the runtime actually produces.
