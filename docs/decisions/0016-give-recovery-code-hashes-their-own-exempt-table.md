# ADR 0016 — Give recovery-code hashes their own table, exempt it, and put nothing else on it

- **Status:** Accepted
- **Date:** 2026-08-11
- **Area:** Persistence / Security (row-level security coverage, grant matrix, recovery factors)

## Context

[ADR 0011](0011-police-the-user-owned-tables.md) settled row-level-security coverage by ownership and
left `credentials` exempt, because it is the table read to answer *who is asking* — a policy keyed on
the identity it resolves would refuse the query that resolves it. It recorded that the exemption is
granted to a **query** and applied by PostgreSQL to a whole **table**, pinned the column set the
reason was argued over, and named the fix for a red on that pin: *move the column, never widen the
pin*.

[ADR 0012](0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md) applied that
instruction for the first time, splitting a passkey's material by whether it is read before or after
the ceremony has produced a trusted identity: `passkey_public_keys` exempt, `passkey_signature_counters`
policed. It also named, twice and in advance, the thing that must **not** land on the exempt table:

> A wrapped key is written once at registration and never updated; a recovery-code hash likewise. Both
> would satisfy any append-only rule perfectly, and this table — already holding key material, already
> keyed on the credential — is the most attractive place in the schema to propose one.

That hypothetical has now arrived. This decision is where it is answered, and it is answered exactly
as those two ADRs predicted — which is the mechanism working rather than an exception to it.

The difficulty is that a recovery-code hash has the *same* structural property as a passkey public
key, for a *different* reason. A code is redeemed by an **anonymous** request: somebody redeeming one
has lost the authenticator that would have proved who they are, so the lookup by hash is what
establishes the identity. A policy keyed on `app.current_user_id` would refuse the very query that
produces the value it wants to compare against — and refuse it **loudly**, because an unset setting
reaches the policy as `''::uuid` and raises `22P02` on every redemption. Same shape as the two
exemptions above; third occurrence of one argument.

## Decision

**A new table, `recovery_code_hashes`, exempt from row-level security, with a pinned column set and
nothing on it that is read after redemption has answered who is asking.**

### The shape

`verifier_hash` (`bytea`, primary key, exactly 32 bytes), `credential_id`, `user_id`,
`credential_type`, `created_at_utc`. Five columns, and the pin covers all five: the hash the row is
found by, and the credential, user and type the redemption then adopts. Nothing else — no
`redeemed_at_utc`, no attempt counter, no label, no wrapped key.

The hash **is** the primary key. A surrogate id would be a second name for the same thing and a worse
one, because the redemption request arrives carrying a code and nothing else, so the hash is the only
handle it has. Keying on it also makes two codes hashing alike *unstorable* rather than a duplicate
nobody would notice.

`credential_id`, `user_id` and `credential_type` reference `credentials (id, user_id, type)` through
`AK_credentials_id_user_id_type` as **one composite foreign key**, `ON DELETE CASCADE`, with
`CK_recovery_code_hashes_credential_type` bounding the type column's own vocabulary. That is the idiom
`sessions` and `passkey_public_keys` already use, and it matters more here than on either of them: a
redemption arrives anonymous and **adopts the `user_id` it finds on this row**, and this table is
exempt, so a row whose owner disagreed with its credential's would hand a redeemer somebody else's
account with nothing beneath the application watching. `Cascade` rather than `Restrict` for the reason
the `credentials → users` edge already records — an unredeemed code must not outrank a person's request
to be forgotten.

### One `credentials` row per *set*

The set is the credential; the codes are its children. That is what lets redeeming one code delete a
row while the set — and the account's ability to redeem the rest — survives, and it lets revoking a set
be a single delete the cascade carries the codes away with.
`IX_credentials_user_id_recovery_codes`, partial on `type = 'recovery_codes'`, gives each account at
most one set: two sets would be two remaining-counts with nothing saying which one binds. The filter is
load-bearing rather than tidy — an unfiltered unique index over `user_id` enforces this rule just as
well and *also* refuses an account a second passkey, which is expressly allowed.

### The exemption, and what holds it to its reason

The entry in `RowLevelSecurityCoverage.Exemptions` declares `ExemptDespite = UserOwned`, because the
table genuinely carries `user_id` and the classifier would otherwise demand `user_isolation` of it with
no rule added. `Exemptions_PinTheColumnsTheirReasonCovers` turns any new column red until somebody
answers for it, and the answer is the one ADR 0011 wrote: **move the column**.

The line the move follows is already drawn by the reason. The exempt table holds what the lookup needs
*before* an identity exists. **A wrapped key is the column this table will be offered first** — the
next epic wraps the account's content and index keys under every recovery factor, and this is the
obvious place to put the recovery-code copy. It is the wrong place, and for the reason the pin exists:
a wrapped key is read *after* redemption has answered who is asking, so it belongs on a table carrying
`user_id`, which the classifier polices by itself with no argument needed. A redemption timestamp and
an attempt counter are the same case with less at stake.

### The grants

```sql
REVOKE ALL ON recovery_code_hashes FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON recovery_code_hashes TO budgetoid_app;
```

`DELETE`, because consuming a code *is* deleting it — the argument is
[ADR 0017](0017-consume-a-recovery-code-by-deleting-its-row.md)'s, and the sentence is the
`webauthn_challenges` sentence word for word. **No `UPDATE` of any shape**, which is the corollary
worth having because it is checkable in one statement: with no `UPDATE`, the delete is the only way a
row can stop counting, so that decision cannot be quietly reversed into a stamp.

This `DELETE` is bounded by no policy at all — the property `credentials`' and `webauthn_challenges`'
also have, and `users`' does not: that one is scoped by `user_isolation`, while these are scoped by
the application or by nothing. ADR 0014's
three legs are what hold it and all three must keep holding: the delete takes the **loaded entity**
and never a hash a caller supplied; the read producing that entity names the **owner** as well as the
hash, so the owner-less lookup the exemption exists for is not on the path to any write; and the read
and the write share one transaction. That lookup is bounded by something other than a predicate, and
it has to be, because a predicate is the one thing unavailable to a statement whose answer is the
identity — what bounds it is that the caller's own input names the row, found by `SHA-256` of a
256-bit secret they must present in full, so selecting a row you cannot name is guessing it. That is
not a new argument: `ConsumeAsync` already deletes a challenge by the nonce the caller presents, on
the table directly above this one in the grants file — the difference being that a challenge row names
no person, so there is no owner a second read could add.

## Alternatives considered

**Put the hashes on `credentials`, one row per code.** The smallest change: no table, no foreign key,
no exemption entry. Rejected three times over. It needs a new column on `credentials`, which the pinned
exemption column set refuses outright and whose own doctrine is *move the column, never widen the pin*.
It would make the credential row mean *one code* rather than *one way of signing in*, so an account
would hold ten credentials of one type and every rule that counts credentials — the last-passkey floor
above all — would have to learn to ignore them. And it dissolves the one-set-per-account index into
nothing checkable: "at most one set" is expressible as a partial unique index only while a set is one
row.

**Put the hashes on `passkey_public_keys`.** This is the alternative ADR 0012's own written reason
names by name as the thing that must not go there — *"a wrapped key or a recovery-code hash is written
once and never updated, so it satisfies any append-only rule perfectly while being exactly what must
not sit on a table every session reads in full"*. It is superficially attractive for exactly the
reasons that ADR listed: the table already holds key material, it is already keyed on the credential,
it is already exempt, and it already holds no `UPDATE` of any shape, so an append-only argument passes
without a murmur. Rejected. The hazard is not mutation and never was: the exemption is argued about a
query and applied to a whole table, so every column there is readable by every application session
whatever user that session names. Putting a recovery-code hash beside a public key would also
mis-describe both — one is the material an assertion is *checked against*, the other is a secret's
digest — on a table whose composite foreign key pins `credential_type = 'passkey'`.

**Police `recovery_code_hashes` with `user_isolation` and run the discovery read on an elevated
connection.** The answer the rest of the schema would predict, and it is the worst option on the list.
It puts a second connection into the request path — an elevated one, at the exact moment the request
has proved nothing at all — which dissolves [ADR 0004](0004-connect-as-a-least-privilege-role.md) for
the one query an anonymous caller controls the input to. The elevated role is not subject to row-level
security, so the policy it was added to satisfy would not apply to the only statement that reads the
table. And it would be green: coverage would report the table policed, and the protection would be
zero.

**Police it with a policy admitting a row when the session names nobody.** The trap ADR 0011 already
documented on `credentials` and ADR 0012 documented again on `passkey_public_keys`, in a third
location. A permissive `user_id = current_user OR current_user IS NULL` is not a narrowing at all once
any request can arrive with no identity — which, for a redemption endpoint, is *every* request to it.

**Give the row a lifetime, so unredeemed codes expire.** Rejected as a different feature wearing this
one's clothes. An expiry column is read before identity (it would have to be, to refuse a stale
redemption), so it would pass the pin's reason — but a person who set codes aside two years ago and
lost their phone today is exactly the person this feature exists for, and an expiry is the rule that
would refuse them. If one is ever wanted it is a product decision with its own argument, not a column
this table grows quietly.

## Consequences

- **`recovery_code_hashes` joins `RowLevelSecurityCoverage.Exemptions` carrying a pinned column set**,
  as every entry whose reason is an argument about what its columns hold does. The entries that pin
  nothing — `currencies` and `__EFMigrationsHistory` — do not because neither reason turns on the
  table's shape.
- **The hypothetical in ADRs 0011 and 0012 stays where it is.** Both name "a recovery-code hash" as the
  write-once secret that must not join an exempt column set. It has now happened, on its own table, and
  the example is retained rather than retired: the hypothetical is what made the decision visible
  *before* there was anything to decide about, and the next write-once secret gets argued the same way.
- **The exemption is used by one lookup and the `DELETE` grant by one caller.** `POST
  /api/recovery-codes/redemption` reaches `FindByVerifierHashAsync`, which matches a row by the
  `SHA-256` of the presented verifier on a connection naming nobody, and
  `RecoveryCodeRepository.ConsumeAsync` spends that row; regeneration deletes the *set's*
  `credentials` row instead and the hashes leave by the cascade. A reader who finds no second caller
  of either should not add one.
- **`RlsCoverageTests` and the deploy-time verifier both required a decision the moment the table
  existed**, and answering that red by editing an existing entry would have been the failure ADR 0011
  exists to prevent.
- **Erasure gains a table and no statement.** `recovery_code_hashes` cascades from `credentials`, which
  cascades from `users`, so coverage is structural rather than enumerated and no grant on this table is
  needed for it.
- **A new column here is a decision somebody has to make out loud.** The pin trips on **any** new
  column, including a benign one. That is intended: what is being forced is the decision, not the
  column's exclusion.
