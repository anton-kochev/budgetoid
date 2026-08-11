# ADR 0015 — Mint recovery codes on the client and store only a hash of a verifier

- **Status:** Accepted
- **Date:** 2026-08-11
- **Area:** Security / Domain (recovery factors, key custody, credential storage)

## Context

An account could hold exactly one thing that opens a session reaching budget content: a passkey. An
authenticator that is lost, wiped or destroyed therefore took the account with it — there is no
operator override, no escrow and no support path, by design. A recovery code is the second secret the
account holder already has, and this decision is about **where that secret is created and what the
server is allowed to hold**.

The question is not the usual one. For an ordinary secret the answer would be "hash it and move on",
and the interesting arguments would be about salt and work factor. Here a second thing rides on the
same value: the account's **key-encryption key** is derived from the code. Whoever holds a code can
unwrap the account's content and index keys, which is the entire point of a recovery factor — and it
is also what makes the code categorically different from a password. A password reaching a server is a
credential the server was going to check anyway. A recovery code reaching this server is the key.

So the storage question and the minting question are one question, and getting the first right while
getting the second wrong buys nothing.

## Decision

**The client mints each recovery code. The code never leaves the browser. The client derives a
verifier `V = HKDF(code, …)` and sends only `V`; the server stores `SHA-256(V)` and holds `V` no
longer than the request that presented it.**

The key-encryption key is derived from the same code on an **independent HKDF branch**. That
independence is the whole mechanism: a database reader holding `SHA-256(V)` can neither redeem — there
is no preimage — nor derive the key, because the hash is on the wrong branch and one-way besides.

Three properties fall out, and each is enforced by shape rather than by a check:

1. **No member exists for a code to travel in.** `GenerateRecoveryCodesCommand` declares
   `IReadOnlyList<string> Verifiers` and nothing else that carries bytes. There is no type for a code
   anywhere in the backend.
2. **The hashing happens inside the entity.** `RecoveryCodeHash.From` takes the verifier and calls
   `RecoveryCodeHash.HashOf` itself. A factory taking a hash the caller computed would mean a raw
   verifier could be assigned to an object something can persist, and every call site would be a place
   to get it wrong once.
3. **`HashOf` is the single spelling of the digest**, so the value written and the value a redemption
   will be matched against cannot drift. If they ever did, the symptom would be silent: every code the
   account was ever issued simply stops matching.

### The entropy rule has no enforcer, and that is recorded rather than papered over

A code carries at least 128 bits of entropy. **The server cannot enforce this and no layer at or below
the API can.** It receives fixed-length opaque bytes; a set of ten identical zero-filled verifiers is
byte-indistinguishable here from a set a good generator produced, and the hash of a weak code is a
perfectly well-formed 32-byte row.

What the server pins instead is the complete list: the verifier's exact decoded width (32 bytes), the
set size (10), and that the ten are distinct from one another. Distinctness is the closest this layer
gets to the property it cannot measure — a client repeating a verifier inside one set has randomness
that is not what it claims — but it is a symptom check and not the rule.

[ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) requires the doc owning a rule to say
why it sits above its lowest capable layer. Here the answer is stronger than "it sits higher": the
rule is **not observable** below the client, in the same sense the WebAuthn `prf` extension result is
a claim the server cannot verify. So the entropy rule belongs to the browser that mints the code, and
that browser is not built yet — which means the rule currently has no enforcer at all, and the first
client to generate a set owns making it true and owns the test that says so. See
[recovery-codes.md](../business-logic/recovery-codes.md).

## Alternatives considered

**Mint the codes on the server and return them in the response.** The obvious design, and the one
every framework tutorial describes. Rejected outright: the server would hold, for the duration of one
request, the input to the key-encryption key's own derivation for an account whose keys it is
otherwise structurally unable to read. One request log, one crash dump, one debugging breakpoint, one
memory snapshot, and the product's central promise is false for that account — and false in a way
nobody can detect afterwards, because the artifact is a log line and not a row. It would also put ten
plaintext secrets in a response body, which is a category of value this API otherwise never returns.

The counterargument is real and worth naming: a browser's `crypto.getRandomValues` is a weaker thing
to trust than a server's CSPRNG, and moving generation to the client moves the entropy rule to the one
layer that has no gate above it. That cost is accepted and written down as an unheld rule rather than
hidden, because the alternative trades a rule nobody can check for a secret the operator provably
holds.

**Store `SHA-256(code)` rather than `SHA-256(V)`.** This is the subtle one, and it is the reason the
wording throughout this area is pedantic. It looks identical: the widths match, redemption still
works, the column still holds a digest nobody can reverse, and **every test in the suite still
passes**. What changes is that the code itself must now cross the wire for the server to hash it — so
this alternative is not a storage decision at all, it is the server-minting decision above wearing a
different hat. The verifier exists precisely so that the value transmitted is *not* the value the
key-encryption key is derived from. Rejected.

**Run a slow key-derivation function over the verifier — Argon2id, or PBKDF2 with a high iteration
count.** This is the hardening a future reader will reach for first, which is why the argument is
recorded before they arrive. A slow KDF exists to make a **guessable** input expensive to enumerate.
The input here is a uniform 256-bit value the client derived from a code with at least 128 bits of
entropy: there is no dictionary to slow down, no rainbow table that could exist, and an attacker
holding the database has no cheaper attack to be made expensive. What a work factor would buy is
latency on a request that already holds the account, and — since Argon2 is not in the BCL — a package.
That package would move a pinned row in `ProjectReferenceGraphTests` and put a third-party dependency
on `Domain`, which declares none. See
[dependency direction](../engineering/dependency-direction.md).

**Salt each row.** Rejected because it makes the only lookup that matters impossible. A redemption
arrives carrying a verifier and **no identity at all** — no credential, no account, nothing — so the
row must be findable by its hash alone. A per-row salt is a value the lookup cannot know before it has
found the row it needs the salt to find. Anything else per-call — a nonce, or a keyed MAC over a
deployment secret — turns a single indexed lookup into a query with no argument to give it, and the
keyed variant additionally introduces a deployment secret whose loss silently invalidates every code
in the system.

**Keep the code client-side but send a signature over a server nonce instead of a verifier.** A
challenge-response would avoid transmitting any long-lived derived value. Rejected as buying nothing
here: the verifier is already a one-way derivation of the code on a branch independent of the key, so
what crosses the wire is not the secret, and the design would add a second nonce pool, a second
ceremony vocabulary, and a stateful exchange in front of a request whose whole appeal is that it is one
call from a person who has just lost their device.

## Consequences

- **`Domain` gains a cryptographic operation and no dependency.** `RecoveryCodeHash` uses
  `System.Security.Cryptography.SHA256` from the BCL. That is the only reason the "no slow KDF"
  argument and the "no package" argument are the same argument.
- **The verifier width is refused from both sides, and the database cannot help.**
  `CK_recovery_code_hashes_verifier_hash_length` watches the *hash*, which is 32 bytes whatever went
  into it, so a short verifier hashes to a well-formed row and only `RecoveryCodeHash.From` stops it.
  Refused rather than truncated: truncating would store the hash of a prefix, and no code would ever
  redeem.
- **The width of the verifier and the width of the digest are numerically equal and are not the same
  bound.** The HKDF output the client is specified to produce is chosen independently of SHA-256's
  output length. `RecoveryCodeHashConfiguration` keeps its own constant for the column for that reason,
  and the two must not be folded together.
- **The server can never answer "was this code ever valid?"** It holds no preimage and, once a code is
  redeemed or replaced, no row either. That is a support question the product cannot answer, and it is
  the same trade [ADR 0017](0017-consume-a-recovery-code-by-deleting-its-row.md) makes explicitly.
- **This ADR is the reason the next epic can wrap keys at all.** The key-encryption key derivation is
  not built; what this decision buys is that when it lands, the branch it derives from has never been
  observable by the deployment. Reversing this ADR later is not a refactor — it retroactively
  invalidates that property for every code issued before the reversal.
