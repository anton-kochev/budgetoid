# ADR 0025 — Give every recovery factor an ECDH key pair

- **Status:** Accepted
- **Date:** 2026-09-15
- **Area:** Domain / Persistence (recovery factors, key rotation, wire formats)

## Context

[ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
put one row per recovery factor on `wrapped_account_keys`, each holding the account's content key
and index key **wrapped under** a key-encryption key that factor derives. For a set of recovery
codes that key comes off a code somebody wrote on a card. For a passkey it is a WebAuthn PRF output,
and a PRF output exists only while the authenticator is being touched.

Symmetric wrapping means whoever re-wraps holds the same key as whoever unwraps. So a content-key
rotation — the remedy for a suspected compromise of the account's own keys, and the reason
[key-rotation.md](../business-logic/key-rotation.md) exists — had to re-wrap the new generation under
**every** surviving factor's key-encryption key, which meant producing every one of those keys inside
one browser session. Ten of them come off a card the person can fetch. The rest come off
authenticators, and an account whose second passkey is a hardware key in a drawer could not rotate at
all.

That is not a cost to be paid more carefully; it is a capability the arrangement does not have. The
encapsulation framing was written down in `Domain/Security/EncapsulatedValueEnvelope` before anything
produced a value in it, precisely against this day — a value **encapsulated to** a public half can be
produced while nobody is holding the factor.

## Decision

**Every recovery factor owns an ECDH P-256 key pair. Its private half is *wrapped under* the
key-encryption key that factor already derives; the account's content key and index key are
*encapsulated to* its public half. Opening an account is therefore two steps, and a rotation needs no
authenticator but the one already in the person's hand.**

1. **Two payload columns on `wrapped_account_keys`, of two cryptographic suites.**
   `wrapped_private_key` carries the factor's PKCS#8 P-256 private key in the AEAD framing;
   `encapsulated_account_keys` carries both account keys in the encapsulation framing. The columns
   `wrapped_content_key` and `wrapped_index_key` are gone. A factor presented yields a
   key-encryption key, that unwraps the private half, and that decapsulates the account's pair.

2. **Both columns are equality checks, and the numbers are measurements rather than arithmetic.**
   Measured on **Chrome 152, Firefox 156 and WebKit 26.6, 120 samples per engine**: a PKCS#8-encoded
   P-256 private key is **exactly 138 bytes**, one distinct length on all three engines. So
   `wrapped_private_key` is `29 + 138 = 167` bytes, refused on both sides of the bound rather than
   banded. A `raw` **public** key is **65 bytes**, the uncompressed SEC1 point the encapsulation
   framing already carries.

   **`raw` import of an EC *private* key is refused by all three engines**, which is the measurement
   that decides the shape and the one a reader will otherwise ask about: the bare 32-byte scalar is
   unreachable from a browser, so PKCS#8 is the only exportable binary form there is to wrap, and a
   number derived from a structure diagram — 32 plus 65 plus DER overhead — would refuse every key
   every one of those engines produces. A PKCS#8 `ECPrivateKey` may or may not carry the optional
   public key and the curve may be named or explicit; each choice moves the total. 138 is what ships.

3. **One encapsulation over both account keys, not one each.** The plaintext is **64 bytes: the
   content key first, then the index key**. Two values encapsulated to the same public key under the
   same KDF `info` are two AES-GCM streams under one derived key, and an implementation that also
   reused the ephemeral pair across them — the obvious way to write "encapsulate these two to this
   factor" — reuses the keystream outright, so the exclusive-or of the two ciphertexts is the
   exclusive-or of the two account keys. One plaintext, one encapsulation, one nonce, and the
   question does not arise. `encapsulated_account_keys` is therefore `94 + 64 = 158` bytes, again an
   equality. It is **smaller** than the wrapped private key beside it despite carrying a 65-byte
   ephemeral point, which is worth noticing before somebody "corrects" one of the two.

   **The order of the halves is the price, and it is held by nothing on this side, ever.** A client
   that encapsulated them the other way round produces a value of exactly the right width carrying
   exactly the right version, which stores, reads back and opens — yielding an index key used to seal
   narrative text and a content key used to compute blind indexes. No factory check, no `CHECK`
   constraint and no test that could ever be written here will notice.

4. **The public halves are carried by `factor_manifests` and by no per-row column.** What has to be
   unforgeable is the **set**: a per-row public key column is a row at a time, so an added, removed
   or swapped row would each have to carry its own authentication, and a client choosing what to
   encapsulate to would have no way to ask whether it was looking at all of them. The manifest is one
   blob per account, authenticated as a set by a key this server does not hold, with `rotation_epoch`
   counting the generations that list has been through. **Four paths carry one, and each carries it
   in the unit of work that already moved the factor set**: registration files the account's first at
   epoch 1, and adding a passkey, replacing a card of recovery codes and revoking a passkey each
   *promote* it. Erasure owes none, because there is nobody left for a list to describe.
   [account-keys.md](../business-logic/account-keys.md) holds the four and what separates them.

5. **A rotation stages a manifest and one value per factor, so `key_rotations` stopped naming one.**
   It lost `factor_id` and both staged envelopes and gained `staged_manifest` and
   `staged_rotation_epoch`; the per-factor values moved to a new `key_rotation_seals`, keyed
   `(user_id, factor_id)`, one payload column and policed by `user_isolation`. It holds `SELECT`,
   `INSERT` and `UPDATE (encapsulated_account_keys)` and **no `DELETE` of any shape**, with `user_id`
   and `factor_id` off that column list so that one statement cannot re-file an account's staged
   generation against another account's factor. `app-role-grants.sql` argues each of them.
   Dropping `factor_id` dropped this table's only foreign key, so `FK_key_rotations_users` restores
   the erasure chain the old edge carried — without it an erased account would have left a staging
   row behind naming its own user, which [erasure.md](../business-logic/erasure.md) forbids outright
   and which no grant could have cleaned up, since the role holds no `DELETE` there.

6. **The role's `UPDATE` on `wrapped_account_keys` narrows from two columns to one.** A rotation
   replaces the account's keys and not the factor's, so `encapsulated_account_keys` moves and
   `wrapped_private_key` is **immutable by omission** from the column list — the mechanism
   [ADR 0004](0004-connect-as-a-least-privilege-role.md) owns, doing real work here, because that is
   the column whose loss would leave a factor able to prove itself and unable to open anything. See
   the amendments on
   [ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).

7. **The pair agrees keys and signs nothing.** It is imported for key agreement only; no member
   anywhere takes or returns a signature under it, and nothing on the server holds a private key of
   any kind — so the server can run neither the ECDH this format names nor the AES-GCM after it. See
   the alternative below for what would reverse that.

## Alternatives considered

**X25519 instead of P-256.** The two are interchangeable for everything this decision needs: both
give a private key with one exportable width, which is all an equality check requires. What decided
it is that P-256 is the curve every client here already implements — the passkey path verifies ES256,
which is ECDSA over P-256 — and that the encapsulation framing was published, with a 65-byte
uncompressed SEC1 point in the middle of it, before this decision existed. X25519 would have been a
second primitive for every client to agree on and would have re-cut a layout already written down.
**It was not eliminated by a measurement the way the option below was**, and this entry says so
rather than implying a number nobody took.

**RSA-OAEP-2048.** Eliminated by measurement, and the measurement is the interesting part rather than
the performance. A PKCS#8 RSA private key **is not a fixed width**: across generated keys it lands
between **1213 and 1219 bytes**, because the encoding's integers carry leading-zero bytes only when
they need them. So `wrapped_private_key` could not have been an equality check at all — it would have
been a band seven bytes wide, and a band is exactly the shape that admits a well-formed row holding
an envelope whose tag cannot verify, discovered on the day somebody needed the account's keys through
that factor. Every column in this design that can be a width is a width.

**Two separate encapsulations, one per account key.** The shape a reader reaches for first, because
it mirrors the two columns it replaces and needs no statement about ordering. It is the keystream
hazard in item 3: two values to one public key under one KDF `info`, and an ephemeral pair reused
across the two — which is what "encapsulate these two to this factor" naturally compiles to — hands
out `contentKey ⊕ indexKey`. That destroys the independence of the two account keys the whole design
rests on, and it destroys it silently: both values are well-formed, both open, and every round trip
passes.

**A public key column on `wrapped_account_keys`, beside the two payloads.** The cheapest place, and
it looks like the manifest without the blob. It answers the wrong question. What a client needs
before it encapsulates is not *what is this factor's public key* but *what are all of this account's
factors' public keys, and is this all of them* — and a per-row column cannot be asked the second
question. An added, removed or swapped row would each need its own authentication, and the set would
still be unauthenticated as a set. Hence one authenticated blob, and hence `factor_manifests` rather
than a column.

**A per-factor signing key beside the agreement key.** Deliberately absent. Every claim a factor
makes today is a claim the **account** makes: the manifest is authenticated under a key the account
holds, so there is nothing for which two factors' contributions have to be told apart. What would
reverse this is a requirement that a factor authenticate something to a party holding no account key
— a manifest attributable to the factor that added an entry rather than to the account as a whole
being the obvious candidate. That is a different decision with a different threat behind it, and a
signing key added now would be a capability with no reader, on a row whose whole point is that the
server can do nothing with what it holds.

## Consequences

- **Two AEAD framings now exist in the schema and both lead with `0x01` on different suites.** The
  AEAD framing numbers AES-256-GCM under a key both sides hold; the encapsulation framing numbers
  ECDH over P-256, then HKDF-SHA-256, then that same AES-256-GCM. Nothing in the bytes says which,
  so **the column is the only discriminator** — and neither version constant may alias the other,
  because reflection cannot tell a `const` literal from a `const` alias and only a source-text census
  holds it. `wrapped_account_keys` and `key_rotation_seals` therefore render four version checks from
  the constants of their own columns' suites, never from the neighbour's.
  [ciphertext-envelope.md](../business-logic/ciphertext-envelope.md) owns the framings and the three
  verbs.
- **Three columns now hold public key material, one in the clear and two sealed**, and none of them
  opens anything: `passkey_public_keys.public_key_cose`, which is a different key for a different
  job — the one an assertion's signature is verified against — and `factor_manifests.manifest` and
  `key_rotations.staged_manifest`, which are the same kind of value one generation apart, each sealed
  under a content key this server does not hold. The data
  inventory's enumeration of non-narrative `byte[]` columns held at fourteen while four of them
  changed, which is exactly the kind of drift a count alone reports as nothing.
  [data inventory](../engineering/data-inventory.md)
- **Epoch 0 is the absence of a manifest row.** An account with no manifest answers 0 — the
  pre-registration state and the state of every account that exists today — so a stored row claiming
  0 would assert its own absence, and *this account has never rotated* and *this account rotated to
  generation zero* would stop being distinguishable to the one read that has to tell them apart.
  Generations count from one, and the negative side falls to the same comparison.
- **The "exactly one greater" epoch transition is held by no declarative layer, and the absence is
  named rather than left to be assumed.** A `CHECK` sees the values of one row and never the step
  between two, and a trigger is the procedural logic
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) forbids pushing down to buy the
  phrase *the database enforces it*. So the arithmetic is application code —
  `FactorManifest.Promote` and nowhere else — with nothing beneath it that will notice if it goes
  wrong, which is why that check is not a restatement of a database rule and deleting it falls back
  on nothing. The **atomicity** half is held, and only that half: EF optimistic concurrency on
  `factor_manifests.rotation_epoch` appends `WHERE rotation_epoch = @original` to every promotion, so
  two started from the same generation cannot both land, while `N + 17` satisfies that predicate
  exactly as `N + 1` does. The token is model-only, with no relational artifact, so no schema census
  would notice it leaving. `key_rotations.staged_rotation_epoch` carries neither guard — a floor and
  nothing else.
- **The guarantee surrendered at the begin came back earlier, and somewhere else, than this entry
  predicted.** The check that went compared the one factor id the command named against a listing of
  **passkey** factors, and was expected back with whatever came to read a manifest. It returned at
  the begin, over the **seals** — the set a run commits to, stated in the clear, where a manifest is
  bytes this server cannot parse — and stronger, because the listing widened to **every** factor: a
  card's ten recovery-code factors are inside the set equality now, and a run that sealed the passkey
  and skipped the ten would have satisfied the old check. It is weaker in one respect, and that half
  is still open: the manifest's own named set is authenticated by a key this server does not hold, so
  a client may stage a manifest that disagrees with its seals. **FR-123 is held over the seals and
  not over the manifest**; the residual is the client's, and only one half of it has a holder.
  `AccountKeyCustodyService` compares the factor set the server serves against the set the manifest
  it just opened names, in both directions, on every sign-in and every unlock — that is the **read**
  half. The **staging** half is unheld anywhere: no client stages a manifest, because no route
  reaches the handler, so nothing compares a staged manifest's named set against the seals submitted
  beside it. Nothing is exposed meanwhile, for the same reason.
  [key-rotation.md](../business-logic/key-rotation.md#what-the-begin-checks-and-the-half-of-fr-123-nothing-here-holds)
- **The baseline was regenerated and this one *drops* columns.** Earlier rebaselines collapsed a
  chain of additions, which an additive migration could in principle have expressed; this one removes
  two columns from `wrapped_account_keys` and three from `key_rotations`, which against a database
  holding rows is data loss with no repair. It is genuinely not expressible as an additive migration,
  which is the case the open `REBASELINE_WINDOW` exists for and the case that stops being available
  the day production holds anything anybody wants back. The new id is
  `20260914230000_InitialCreate`; regenerating obliges resetting production's `__EFMigrationsHistory`
  in the same deploy. [migrations](../engineering/migrations.md) and `DEPLOYMENT.md`.
- **`key_rotation_seals` is reached twice by an erasure**, through `users → key_rotations` and
  through `users → credentials → wrapped_account_keys`, because it carries a foreign key to each.
  PostgreSQL permits the two cascading paths that creates; the multiple-cascade-path restriction is
  SQL Server's, not this server's.
- **The browser has followed, and one of the four paths now has a client behind it.**
  `factor-keypair.ts` mints a factor's pair and opens one, `factor-manifest.ts` seals the account's
  list of public halves and opens it, and the registration flow drives eleven of the first and one
  of the second in a single act; custody runs the read half on every sign-in and every unlock. The
  retired grammar was **deleted** rather than kept beside the new one, and its frozen vectors went
  with it. What has no client at all is the three paths that *promote* a manifest, so the epoch
  arithmetic this decision argues for is exercised by the integration suite alone.
  [account-keys.md](../business-logic/account-keys.md) names the grammar, the three version bytes
  and the one check that catches the plaintext ordering in item 3.
- **A column swap is refused and what replaces it is unreachable by any check here.** 167 bytes
  against 158, two framings and two version constants mean a transposition of the two payloads is
  refused by each column's own pair of constraints, where both values were once 61 bytes carrying one
  version byte. The hazard that replaces it is the plaintext ordering in item 3, a level in, and it
  will never be checkable on this side — which is why it is written into the entity, the
  configuration, the read model and this decision rather than left to one comment.
