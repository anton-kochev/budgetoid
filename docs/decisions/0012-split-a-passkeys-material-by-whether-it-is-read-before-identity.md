# ADR 0012 — Split a passkey's material by whether it is read before identity

- **Status:** Accepted
- **Date:** 2026-08-05
- **Area:** Persistence / Security (row-level security coverage, provisioning, WebAuthn)

## Context

[ADR 0011](0011-police-the-user-owned-tables.md) left `credentials` as the one exempt user-owned
table, because it is read to answer *who is asking* and a policy keyed on that identity would
refuse the query that resolves it. It also recorded that the exemption is granted to a **query**
but applied by PostgreSQL to a whole **table**, pinned the column set the reason was argued over,
and said what a red on that pin means: *move the column*, never append to the list.

Passkey sign-in is the first change that has to act on that instruction. A WebAuthn assertion
arrives carrying a credential id and a signature and nothing else. The server must find a public
key and verify a signature **before** it knows whose account this is; until that moment every
statement it issues runs on a connection whose `app.current_user_id` is `''`, so any policed table
it touches fails with `22P02`. A signature counter, by contrast, is compared only *after* the
signature verifies — the specification orders it that way — and it is the one value in the whole
ceremony that changes.

ADR 0011 predicted the counter would "arrive as a column that goes ON the [`GRANT UPDATE`] list"
of `credentials`. This decision is where that prediction is answered, and it is answered
differently.

## Decision

**A passkey's material is split by whether it is read before or after the ceremony has produced a
trusted identity.** `credentials` grows no column at all.

### `passkey_public_keys` — exempt, and pinned

Holds `credential_id` (PK), `user_id`, `credential_type`, `webauthn_credential_id`,
`public_key_cose`, `cose_algorithm`. Every one of them is read by the discovery lookup, before the
request has an identity. It carries a written exemption declaring `ExemptDespite = UserOwned`, the
same shape `credentials` uses.

**What holds this exemption to its reason is the pinned column set, not the grant matrix.** Be
precise about that, because the appealing answer is the wrong one. The hazard ADR 0011 recorded is
not mutation — it is that the exemption is granted to a *query* and applied by PostgreSQL to a whole
*table*, so every column on it is readable by every application session whoever that session names.
A wrapped key is written once at registration and never updated; a recovery-code hash likewise. Both
would satisfy any append-only rule perfectly, and this table — already holding key material, already
keyed on the credential — is the most attractive place in the schema to propose one. So the column
set is pinned, and `Exemptions_PinTheColumnsTheirReasonCovers` turns a new column red until someone
answers for it. The answer is to move the column to a table carrying `user_id`, never to widen the
pin.

The grant matrix is the corollary, and it is worth having because it is checkable in one line:

```sql
REVOKE ALL ON passkey_public_keys FROM budgetoid_app;
GRANT SELECT, INSERT ON passkey_public_keys TO budgetoid_app;
```

No `UPDATE` of any shape, no `DELETE`. Rows leave only by the cascade from `credentials`.
`credentials`'s exemption covers a table that may one day take an `UPDATE` column list; this one
never can, so mutable per-user state cannot accumulate here. That is a genuine narrowing — it just
does not cover the case that actually threatens the exemption, which is a secret that never changes.

### `passkey_signature_counters` — policed by `user_isolation`

Holds `credential_id` (PK), `user_id`, `credential_type`, `signature_counter`. It carries `user_id`
`NOT NULL`, so the classifier reaches the verdict from the table's own columns with no new rule and
no entry in `Exemptions` — which is the mechanism working rather than an exception to it.

```sql
GRANT SELECT, INSERT ON passkey_signature_counters TO budgetoid_app;
GRANT UPDATE (signature_counter) ON passkey_signature_counters TO budgetoid_app;
```

A one-column `UPDATE` list; the other three columns are immutable **by omission**, per
[ADR 0004](0004-connect-as-a-least-privilege-role.md).

### `webauthn_challenges` — exempt, owned by a ceremony rather than by a person

Holds a nonce, its ceremony, and its lifetime. It carries no `user_id` **deliberately**: the
authentication ceremony issues a challenge before anyone has said who they are — that is what a
discoverable credential means — so there is nobody for a policy to key on, and a nullable ownership
column is refused at the gate anyway. `ExemptDespite = None`, with its column set pinned, because
the pin is what keeps a person-identifying column from landing on it later.

It is granted `DELETE`, which three of the six identity tables (`users`, `credentials`, `sessions`,
`passkey_public_keys`, `passkey_signature_counters` and this one) hold and three do not — most of the
budget-owned tables hold it for the ordinary reason that people delete their own records. The grant
paragraph says why this one does: its rows are nonces, consuming one *is* deleting it, and a row
nobody can delete is a row swept by something that does not exist. The other two hold it for
unrelated reasons. `users` is the root the whole owned graph cascades from, so deleting it is how an
account is erased, and that grant reaches these two tables through the cascade rather than through a
privilege of their own. `credentials` gained one later still, for **revocation** rather than for
erasure — and it is the one grant in the matrix that no policy bounds, which is its own decision
([ADR 0014](0014-scope-the-credential-delete-in-the-application.md)).

What keeps `passkey_public_keys` and `passkey_signature_counters` ungranted is unchanged and is worth
restating against that third grant: a referential action runs as the referencing table's owner and
descends from one row, whereas a `DELETE` privilege on `passkey_public_keys` would be unpoliced,
because that table is exempt from row-level security. `credentials` took exactly that cost knowingly;
these two have no reason to.

### Both new rows reference the credential compositely

`(credential_id, user_id, credential_type)` → `credentials (id, user_id, type)` against the
existing `AK_credentials_id_user_id_type`, `ON DELETE CASCADE`, with a check pinning
`credential_type = 'passkey'`. The same idiom `sessions` uses, for the same reason: it makes "one
person's key attached to another person's credential" and "a public key attached to a federated
credential" both *unstorable* rather than merely unlikely.

### The identity is published only after the signature verifies

The assertion path runs: consume the challenge → read the public key (no identity) → verify the
signature → **`IUserContextWriter.ResolveUser`** → open a transaction → accept the counter,
establish the session.

The ordering is load-bearing in both directions. Publishing earlier would mean trusting a
credential id an unauthenticated caller supplied. Opening the transaction earlier would mean the
connection is opened — and the interceptor's `set_config` run — while `app.current_user_id` is
still empty, so every policed statement inside it fails with `22P02`. ADR 0011 states that
precondition in the abstract; this is the first code path that can violate it.

## Alternatives considered

**Keep the counter on `passkey_public_keys` and exempt the whole thing.** ADR 0011 itself says a
counter leaking across sessions is "an enumeration and correlation surface, not a credential
compromise", so the exemption could have stretched to cover it. Rejected: the cost of the second
table is one integer column and a policy block, and the benefit is that the exempt table keeps a
property statable in one sentence and enforced by a `GRANT` line. A rule that needs re-deriving is
a rule that eventually is not.

**Put the counter on `credentials`, as ADR 0011 expected.** Rejected. It would give `credentials`
its first `UPDATE` grant, widen the pinned column set, and move the frozen constraint snapshot —
spending the exemption's remaining margin on the first story that tested it.

**Police `passkey_public_keys` with a policy admitting a row when the session names nobody.** This
is the trap ADR 0011 already documented on `credentials`, in a new location. A permissive
`user_id = current_user OR current_user IS NULL` policy is not a narrowing at all once any request
can arrive with no identity — which, with an anonymous assertion endpoint, is now every request to
that endpoint.

**Store AAGUID, transports, backup-eligibility flags and a last-used timestamp.** Rejected — not
relocated, refused. Nothing in this design reads any of them: no `allowCredentials` is ever sent,
so transports are unused; under `attestation: "none"` the AAGUID arrives zeroed; and an
authenticator model identifier is a device fingerprint, which the product's own data-minimization
rule refuses. Each arrives with the feature that reads it, argued by that change.

**A random per-user WebAuthn user handle column.** Rejected. The internal `users.id` is already an
identifier no external party supplies ([ADR 0011](0011-police-the-user-owned-tables.md)'s premise),
and a second one would be a column nothing else reads.

## Consequences

- `credentials` is untouched: no new column, `CK_credentials_type_shape` unchanged, its pinned
  exemption column set unchanged, its constraint snapshot unmoved, and still **no `UPDATE` grant of
  any shape**. The comment in `CredentialConfiguration` and the paragraph in `app-role-grants.sql`
  that both predicted otherwise are rewritten to record where the material went.
- `RlsCoverageTests` goes red the moment the three tables exist and stays red until two `Exemptions`
  entries are written. That red is the design signal; answering it by editing the `credentials`
  entry would be the failure this ADR exists to prevent.
- **An exempt table scopes nothing, so the application is the only thing scoping reads of it.** The
  discovery lookup is the one query allowed to read `passkey_public_keys` without naming an owner;
  every other read must carry its own `where user_id = …`, exactly as `FindFirstForUserAsync` does
  on `budgets`. The `excludeCredentials` read is the first such call site, and the test that
  notices a missing filter is the one registering a passkey to two different accounts.
- The assertion options endpoint is **the first unauthenticated write path in the system**: anyone
  can make it insert a challenge row. Growth is bounded by a five-minute lifetime and an
  opportunistic sweep, not by rate limiting, which does not exist here. That is an accepted gap
  rather than a solved problem.
- A future recovery factor's wrapped keys land on a table carrying `user_id`, policed, with no
  further argument needed — the split this ADR draws is the one the requirements already draw
  between identity and key custody.
- Erasure gains two more tables to cover. `passkey_public_keys` and `passkey_signature_counters`
  both cascade from `credentials`, which cascades from `users`, so the coverage is structural rather
  than enumerated. `webauthn_challenges` is not the third: it carries no foreign key at all, because
  it names nobody to cascade from. Its rows leave by being consumed or by expiring — erasure has
  nothing to erase there, and a change that gives it an owner would be the change that puts it on
  this list.
