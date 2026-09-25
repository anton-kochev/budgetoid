# Adversarial Properties

> Read this before telling anybody what the operator cannot read, before adding a column, a key,
> a factor kind or a request that carries key material, and before widening what a rotation
> trusts. Every claim below is either held by a named test or argued here.

**Four properties are verified by argument, not by a run, and this chapter is that argument.**
NFR-013 (the database and its backups yield no narrative), NFR-015 (only a holder of one of the
account's recovery factors obtains its keys), NFR-016 (names cannot be correlated across tables)
and NFR-028 (a party writing stored values cannot make a rotation encapsulate to a key the account
did not register) can each be *supported* by tests, and each section names them — but a test
exercises the cases its author thought of, and these properties are claims about every case. A
requirement verified by analysis is held by the analysis and by nothing else, so a wrong sentence
here is a defect in the product rather than in the prose;
[ADR 0023](../decisions/0023-scope-the-blind-index-message-to-one-budget.md) records what one such
sentence cost. NFR-014 is held by a test, named below. The last two sections state the other side:
what the operator and the identity provider do observe.

Every argument reasons from the wire and storage formats in
[ciphertext-envelope.md](../business-logic/ciphertext-envelope.md) and the key hierarchy in
[account-keys.md](../business-logic/account-keys.md) and
[key-rotation.md](../business-logic/key-rotation.md), and restates only what an argument needs.

## The adversary, and what it is not

**The operator is the adversary for the narrative and for the account's keys.** It holds the
database, every point-in-time backup inside the 7-day window, the API's logs and the hosting
platform. It can read every row and — for NFR-028 — write any row. It is trusted with the
arithmetic and the email address by design
([What the operator reads](#what-the-operator-reads-fr-056-nfr-026)).

**The code the browser runs is trusted, and the operator is the one who serves it.** This is the
assumption every property below rests on, and it is stated rather than argued because it cannot
be argued: the keys are opened by JavaScript this deployment ships, so a modified bundle takes
them at the next unlock, and no envelope, manifest or epoch refusal can see it — the code doing
the judging is the attack. `script-src 'self'` does not help; it refuses *other* origins. Two
cases are different and must not be flattened:

- **A bundle swapped for everyone** — a compromised pipeline or a deliberate release. Detectable
  in principle, because every visitor receives the same bytes and the source is public; nothing
  lets anybody check the served bundle against that source.
- **A bundle served to one person.** Invisible to every other observer. No web client closes this:
  a service worker cannot pin code, because an update check made more than 24 hours after the last
  bypasses the cache and the server answers it. Only a client delivered outside the browser tab —
  one binary for everybody, updated through a channel the operator does not solely control — can
  make the code-level form of these claims true.

The same assumption covers the other ways plaintext meets the page: a cross-site script in the
client, or malware on the person's device, reads what the tab reads. And it covers one server-side
gap that belongs here rather than under NFR-013: **the server cannot tell ciphertext from plaintext
dressed as ciphertext.** It checks a narrative value's version byte and length band and nothing
else, so a client that does not seal stores readable text — `NarrativeSecrecyTests.
Scan_ReportsAPlaintextNameWrittenThroughARealRoute` writes exactly that through a real route and
it answers 201. The server can never close this; it has no key to test the bytes against.

## The writer who once held a content key

**Every property below holds only against a party that has never held a content key of the
account while its current factors existed.** This is the widest narrowing in the chapter, and it
is stated once, here, because it cuts across all four.

A factor's public key is kept only inside the factor manifest, sealed under the content key, and a
factor's key pair lives as long as the factor — a rotation replaces the account's keys, never a
factor's key pair. So a party that held any generation of the content key read every factor's
public key and still holds it after that generation is retired. An encapsulation carries no proof
of who made it: anyone holding a factor's public key can encapsulate a pair of keys of their own
choosing to it, and the factor opens the result as readily as a genuine one. Put together with the
ability to write the database:

- **A staged rotation the attacker planted is finished by the person.** A run in flight is resumed
  or repaired by opening the staged seal under the presented factor, and nothing ties the staged
  generation to the generation in force. A planted run naming keys the attacker chose is re-sealed
  into, row by row, by the person's own browser, and every value it writes opens — for the
  attacker.
- **An unlock can be handed substituted keys.** The unlock gate compares the factor *identifiers*
  the manifest names with those served, and opens nothing of the account's narrative before
  presenting the account as unlocked; a manifest and encapsulations built around keys the attacker
  chose pass it. This form is loud — every existing row then fails to open — but what is written
  afterwards is sealed under keys the attacker holds.

A rotation is therefore not a remedy against a writer who once held a content key: it hands that
writer the next generation. It remains the remedy against a party that held a key and cannot write.

## NFR-013 — the database and its backups yield no narrative

**Claim.** A party holding full read access to the database and every backup recovers no narrative
value of any account.

**Argument.** Take the eight narrative columns first. Each holds a version-1 envelope — AES-256-GCM
under the account's content key, a fresh random nonce per seal, associated data binding the table,
column and row. Opening one takes the content key. So the property reduces to: *nothing the
operator holds yields a content key.* Walk every stored binary value:

| Stored value | What it is | Why it opens nothing |
|---|---|---|
| `wrapped_account_keys.encapsulated_account_keys` | content key ‖ index key, encapsulated to a factor's P-256 public key | opening takes that factor's private key |
| `wrapped_account_keys.wrapped_private_key` | the factor's private key, wrapped under its key-encryption key | the key-encryption key comes from a PRF output evaluated inside an authenticator, or from a recovery code; neither is stored or transmitted (NFR-015) |
| `key_rotation_seals.encapsulated_account_keys` | the next generation's pair, same shape | same chain, same private keys |
| `factor_manifests.manifest`, `key_rotations.staged_manifest` | the factor public keys, sealed under the content key (the staged copy under the next one) | opening takes the key being protected |
| `recovery_code_hashes.verifier_hash` | SHA-256 of a verifier derived from the code | the verifier and the key-encryption key are sibling HKDF expansions under different `info` labels; reaching either from the hash means inverting SHA-256 and then guessing a 130-bit code |
| `session_tokens.token_hash` | SHA-256 of a 32-byte random token | a hash is not a cookie; and a live session reaches the narrative only as ciphertext |
| `passkey_public_keys.public_key_cose`, `passkey_public_keys.webauthn_credential_id`, `webauthn_challenges.challenge` | a signature-verification key; an authenticator's handle; a random nonce | input to no key derivation |
| `name_key` on four tables | HMAC-SHA-256 under the index key | a keyed digest, not an encryption; see NFR-016 for what it discloses |

Every row that could yield a content key ends in the same chain: **the operator opens an
encapsulated account key only by first unwrapping a private key it has no key for.** The table is
complete over binary columns while it matches the list
`KeyMaterialSecrecyTests.Schema_ClassifiesEveryBinaryColumn` holds against the live catalog. A new
`bytea` column fails that test, and its argument must then be added here by hand; any other new
column arrives at the data inventory, which is reconciled against the catalog in both directions
(`DataInventoryReconciliationTests.Model_AndTheLiveCatalog_DescribeTheSameColumns`).

**Backups add nothing new and keep something old.** A point-in-time backup holds the same bytes the
live database held at that moment, so every argument above applies to it unchanged. What it adds is
*time*: a ciphertext overwritten by a rotation survives in the backups for up to seven days, under
the content key it was sealed with. That matters only to a party that already holds the old content
key, which is the party the section above already concedes more than this to.

**What supports the argument.** `NarrativeEncryptionCoverageTests.
Narrative_ColumnsAreStoredAsCiphertext` holds that every narrative column is typed for an envelope
in the model. `NarrativeSecrecyTests.AppRoleWithABudgetSession_ReadsNoNarrativeValueInPlaintext`
seeds a marker into every narrative column — through real routes for seven, and through the domain
factory for `budgets.name`, which has no write route — and scans every column of every relation
for it. That scan runs **as the application role on one user's session**, so rows row-level
security hides from that role are not scanned, and a re-encoded copy of a marker is not
recognised. No test scans the database with full read access; the full-read case is carried by
the argument above, which is why it is written down.

## NFR-015 — only a holder of one of the account's own recovery factors obtains the keys

**Claim.** No party other than a holder of one of the account's recovery factors — a registered
passkey or one of its recovery codes — obtains the content key or the index key.

**Argument, in three steps.**

1. **The keys are born in the browser and leave it only encapsulated.** The pair is 64 bytes from
   `crypto.getRandomValues`, split into two 32-byte keys and imported non-extractable. It reaches
   the server only inside `encapsulated_account_keys` and `key_rotation_seals`, encapsulated to a
   factor's public key. `register.service.spec.ts` "sends nothing the server could open" decodes
   every string in the registration body and fails if any is the content key, the index key, or a
   32-byte value other than a verifier; "sends no recovery code to the server" checks every code in
   raw, grouped and canonical form; `webauthn-encoding.spec.ts` "never carries the PRF output"
   holds the ceremony's side.
2. **Opening an encapsulated pair takes the factor's private key, and that key exists only wrapped
   under something the factor alone yields.** For a passkey, the key-encryption key is HKDF over a
   PRF output the authenticator evaluates on a fixed client-chosen input — the server cannot choose
   it and never sees the result. For a recovery code, it is HKDF over the code itself, 130 bits
   from `getRandomValues`, shown once and never sent. The verifier the server does receive at
   redemption is a sibling expansion of the same input under a different `info` label, so it
   carries nothing toward the key-encryption key; the two labels are pinned distinct by
   `recovery-codes.spec.ts` and frozen in `vectors/recovery-code-v1.json`.
3. **The server computes nothing that would help.** Production server code performs no HKDF, ECDH
   or AES-GCM. Beyond WebAuthn signature verification, which no key-encryption key stands in any
   relation to, it only hashes: a recovery-code verifier, a session token and ceremony inputs.

**Where the claim is narrower than its words.** Beyond
[the writer who once held a content key](#the-writer-who-once-held-a-content-key), "one of the
account's own recovery factors" is read at the moment of the attack, and two factors outlive what a
person might think ends them:

- **A revoked factor still opens the current keys until a rotation.** Revocation deletes the
  factor's rows and promotes a manifest; it does not change the content key. Whoever copied that
  factor's rows before — from a backup inside the window, or as an operator who kept them — and
  holds the factor opens the live pair.
- **A spent recovery code stays a factor by design** (FR-140). Redeeming it removes its power to
  sign in, not its power to open the keys; a person who has used a code and kept the card still
  holds a way in.

**The non-committing cipher costs nothing only while every factor secret is large.** AES-GCM is not
key-committing: a ciphertext can be built to open under two keys its author chose. Against a
key-encryption key derived from 130 bits or from a PRF output that buys nothing, because the
candidate keys cannot be enumerated. A factor derived from anything a person types would turn it
into a partitioning oracle and would void this argument. The argument assumes the authenticator
returns the 32-byte PRF output WebAuthn specifies.

## NFR-016 — names cannot be correlated across tables within an account

**Claim.** A party with full read access cannot determine whether two rows of different tables
within one account carry the same name.

**Argument.** A name appears twice in storage: as an envelope and, for four columns, as a blind
index. **The envelope** is drawn under a fresh random nonce and binds its table, column and row in
the associated data, so one name sealed twice — in two tables or in the same one — produces two
unrelated byte strings. **The blind index** is HMAC-SHA-256 under the index key over a message that
carries the table, the column and the budget before the normalized name, so one name in two columns
produces two unrelated digests. `blind-index.spec.ts` "gives one name under three tables three
unrelated values" pins it against frozen vectors in `vectors/blind-index-v1.json`.

**NFR-014 — the same property across budgets — is held by a test rather than by this chapter.** The
budget is a field of the blind-index message
([ADR 0023](../decisions/0023-scope-the-blind-index-message-to-one-budget.md)), and
`blind-index.spec.ts` "gives one name in one field under two budgets two unrelated values" pins it.
Nothing creates a second budget, so no stored pair can exhibit it.

**Where the claim is narrower than its words: length.** An envelope is exactly the UTF-8 length of
its plaintext plus 29 bytes, and nothing pads it. So a reader sees that an account named "Rent" and
a category named "Rent" have equal lengths — which does not prove the names equal, while two
different lengths *do* prove two names different. Ruling equality out is partial information, and
the claim holds only in the weaker sense that no stored value lets a reader *confirm* two names
match. Padding every name to a fixed width would close it at the cost of storage and the byte caps;
it is not done.

## NFR-028 — a writer cannot make a rotation reach a key the account did not register

**Claim.** A party able to modify stored values cannot cause a rotation to encapsulate an account
key to a public key the account did not register.

**Argument.** A rotation encapsulates to the public keys named in the factor manifest and takes a
public key from nowhere else — `factor-public-key-single-source.spec.ts` pins every place that may
construct one. The manifest is sealed under the current content key, so a writer who does not hold
that key cannot produce one that opens, and cannot edit one without breaking its tag. What such a
writer *can* do is serve an older manifest that still opens, and the begin refuses it in order: it
refuses a response with no manifest, a manifest that does not open, a served factor set that is not
the manifest's set in both directions, and — once the manifest has authenticated the epoch as its
own associated data — an epoch below the highest this device has recorded for the budget
(`key-rotation-material.spec.ts` "refuses a manifest from before the epoch this device has
recorded"). The server independently refuses a begin whose seals are not exactly the live factor
set, and refuses at completion (`FactorManifest.Promote`) a staged manifest whose epoch is not the
stored one plus one. None of this needs a signature over the public keys, because every consumer
of a factor public key either generated it that instant or read it from a value only a
content-key holder could have written.

**Where the claim is narrower than its words.**

- **A writer who holds, or once held, a content key defeats it.** The manifest's only
  authenticator is the content key, and a rotation is the remedy for suspecting that key leaked.
  A party holding the current key seals a manifest of its own naming its own keypair, at an epoch
  no device record exceeds, and inserts a factor row for it; every refusal above passes on every
  device. A party that held an earlier key plants a staged run instead
  ([the writer who once held a content key](#the-writer-who-once-held-a-content-key)).
- **A device with no record cannot see a replay.** The epoch record is kept per device and per
  budget, and the budget is named by the server's own answer to `GET /api/me`. On a first visit — a
  new browser, cleared storage, a private window — or under a budget identifier the device has
  never recorded, an older manifest that still opens is indistinguishable from the current one.
  Because revoking a factor promotes a manifest under the *same* content key, that older manifest
  can name a revoked factor, and a rotation begun on such a device reaches it. A factor the account
  registered and then revoked is not, literally, one it "did not register"; it is one it
  deliberately gave up, which is what the requirement means.

## What the operator reads (FR-056, NFR-026)

**The person is told on the settings screen, and that copy is the product's statement toward
NFR-026.** The section *What we can read* — specified in [components.md](../design/components.md)
and pinned whole by `settings.component.spec.ts` — names amounts, dates, currency codes, account
types, the order things are arranged in, the timestamps on every row and the identifiers behind
them, how many of each thing exists and which point at which, the email address, the length of
names and notes, and the renames and repeats the blind index makes visible. Every item there is
read from a column this server holds, in the clear or inferred from its length or its digest.

**It is true, and it is narrower than what the operator reads.** Its "timestamps on every row"
covers some of the following without saying the rows exist:

- **The provider identity.** Which provider gated registration and its subject identifier for the
  person — a stronger cross-service identifier than the email address.
- **The shape of a person's security setup.** How many passkeys and recovery codes the account
  holds (from its factor rows, and from the manifest's length, since every entry is the same
  width), each passkey's COSE algorithm, which narrows the device family, and each credential's
  enrolment time.
- **Sign-in history.** Every session's creation, expiry and revocation time and which credential
  opened it, kept until that credential is revoked or replaced or the account is erased, and in the
  backups for seven days after; each passkey's signature counter, which counts its assertions —
  sign-ins and re-authentications — where the authenticator keeps one. From the sessions a
  recovery code opened, when codes were redeemed; from the hash rows left against the factor rows,
  how many are spent.
- **Key history.** The rotation epoch, which counts every change to the factor set and every
  rotation; while a rotation is in flight, that one is and when it began; and the `rotation_id`
  stamp on every narrative row, which names the run that last re-sealed it.
- **Edit timing.** No column on a narrative-bearing row records an update time, but an edit clears
  that row's `rotation_id` stamp, so after a rotation the operator reads which rows changed since
  it ran. Beyond that, every save of a sealed value writes a new ciphertext under a new nonce, so
  row versions, the write-ahead log and the backups show when a row changed.
- **Outside the database.** Request paths, which carry resource identifiers, response codes and
  timing in the API's logs, and the client address the hosting platform sees on every request.

## What the identity provider observes (CON-012, NFR-025)

**This product claims no anonymity.** An account cannot exist without a completed exchange with the
registration provider, so the provider learns that the person registered, when, and from which
address, and the product stores the address the provider asserted and the provider's subject
identifier. The person's browser contacts the provider in two moments and no other: when the
registration screen starts the exchange, and when the provider redirects that exchange back to
`/register` ([no-third-party-origins.md](no-third-party-origins.md)). Signing in is a passkey
assertion against this product's own API and reaches no provider. The API itself fetches the
provider's published signing keys to validate a registration's token, which names no person. Of the
three moments NFR-025 permits, the email change and the locked sign-in are not built.

## Keeping this chapter true

- **A new binary column** fails `KeyMaterialSecrecyTests.Schema_ClassifiesEveryBinaryColumn`. Add
  its row to the NFR-013 table *and* argue it there; a row the table does not show is a row the
  property does not cover.
- **A new factor kind** must derive its key-encryption key from at least 128 bits, or the
  non-committing argument under NFR-015 is void. Say which secret it is and how large.
- **A new path that encapsulates to a public key** must take the key from the manifest or generate
  it that instant; anything else is a consumer `factor-public-key-single-source.spec.ts` does not
  admit, and that census must be widened in the same commit, with its reason — and the NFR-028
  argument re-made here.
- **A change to what a staged run or an unlock trusts** is a change to
  [the writer who once held a content key](#the-writer-who-once-held-a-content-key), and that
  section says what holds after it in the same commit.
- **A change to what is readable** changes *What we can read* on the settings screen and the list
  above in the same commit.
