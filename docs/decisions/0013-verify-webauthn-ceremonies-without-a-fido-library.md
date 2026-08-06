# ADR 0013 — Verify WebAuthn ceremonies without a FIDO library

- **Status:** Accepted
- **Date:** 2026-08-05
- **Area:** Application / Security (WebAuthn registration and authentication)

## Context

Passkey sign-in needs a server that can verify two WebAuthn ceremonies: a registration response
carrying an attestation object, and an assertion carrying a signature over authenticator data and
a hash of the client data. Both are binary formats — CBOR, COSE keys, a packed flag byte, a
big-endian counter — and the assertion verification is the load-bearing step of the whole
authentication design.

The obvious answer is a FIDO2 library. The obvious objection is that hand-rolled cryptography is
how systems get broken. Both are true, and this decision records which risk was taken and what
holds it down.

## Decision

**The ceremonies are verified in this repository, on `System.Security.Cryptography` and
`System.Formats.Cbor`, with no FIDO or WebAuthn library.** The verification code is a set of pure
functions with no I/O and no repository access, so every branch below is a unit test that runs in
milliseconds.

Three narrowings are what make that defensible. Each removes a whole category of the work a general
library exists to do:

**`attestation: "none"`, and any other `fmt` is refused outright.** Verifying `packed`, `tpm` or
`android-key` means X.509 chain building against the FIDO Metadata Service, keeping that blob
fresh, and choosing a trust policy — the large majority of a FIDO library's surface. Attestation
answers *which model of authenticator is this*, and this product enforces no authenticator
allow-list and stores no AAGUID, so it has no use for the answer. Refusing a non-`none` format is
stricter than ignoring the field and costs one branch: an unverified attestation statement that is
stored or trusted is worse than no attestation at all.

**ES256 (-7) and RS256 (-257), and nothing else.** ES256 is what every platform authenticator
produces — Apple, Android, Windows Hello. RS256 is what older TPM-backed Windows Hello credentials
produce, and a passkey a person already holds is not one they can be asked to replace. Ed25519
(-8) is excluded for a checkable reason rather than a preference: .NET 10's
`System.Security.Cryptography` ships no in-box Ed25519 verifier, so supporting it would need the
dependency this decision is avoiding.

The allow-list is a **database rule**, not only a C# switch: `cose_algorithm` is a column, so a
check constraint bounds it ([ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) applied
literally). Adding an algorithm later costs an enum member, a migration widening that constraint, a
verifier branch, and a test — and the constraint being part of the cost is the point.

**No `allowCredentials`, so only discoverable credentials.** Sending a credential list requires the
client to name an account before authenticating, which turns the endpoint into an
account-enumeration oracle. Discoverable is also what the product's own definition of a passkey
says. This is why `transports` is not stored: its only use is populating a list that is never sent.

### One dependency, named

`System.Formats.Cbor` is **not** in the shared framework. It is a first-party, out-of-band package
from `dotnet/runtime` with no transitive dependencies, and it is not a WebAuthn or FIDO library.
The alternative is hand-writing a CBOR reader, which would trade a maintained parser for the single
riskiest component in the change. It is referenced; central package management is off, so the
version is pinned per project.

### The failures hand-rolling actually produces, and what pins each

These are enumerated because they are the reason the decision needs a record at all. Each is pinned
by a named test, and the two that are reversible — a signature encoding, an origin comparison — are
pinned from both directions, because for those a wrong implementation is not merely a refusal but a
different rule that accepts something.

| Failure | Why it is easy to write |
|---|---|
| ES256 verified as IEEE P1363 | WebAuthn signatures are ASN.1 DER; the default `ECDsa.VerifyData` overload expects P1363 and refuses every real signature. Verification **must** pass `DSASignatureFormat.Rfc3279DerSequence`. |
| `signCount` read little-endian | `BitConverter` is little-endian on every platform this runs on and produces a silently wrong counter. Use `BinaryPrimitives.ReadUInt32BigEndian`. |
| COSE key over-read | The COSE key runs to the end of the attested credential data with **no length prefix**, so the only thing that locates the extension block is how much the CBOR reader consumed, measured against the buffer it was handed. Stated as the property rather than as a member name, because the member that reports it has changed across framework versions and the property has not. |
| Origin matched by prefix | `https://budgetoid.app.attacker.example` passes both `StartsWith` and `Contains`. Origins are compared by equality against an allow-list. |
| `clientDataJSON` hashed after a round-trip | The signature covers the original bytes; re-encoding a parsed string changes them. |
| rpId compared as a string | The authenticator sends `SHA-256(rpId)`, not the id. |

**The synthetic authenticator used by the tests is a second implementation of the same wire
format**, so a misunderstanding it shares with the verifier passes every test that uses it. That is
answered by **golden vectors** — real captured ceremony responses, checked in with their provenance
and the licence of the corpus they came from, because a vector whose origin is not recorded cannot
be told from a synthesized one and is therefore worth nothing. They are the only tests in the suite
that can catch a shared belief, and obtaining them is an acceptance condition rather than a
nice-to-have.

One of the two is deliberately **not** run through the whole ladder, and that is worth stating
plainly so it does not read as an oversight. The captured assertion predates user verification being
required, so its UV flag is clear. Pushing it through the full sequence would mean either weakening
the requirement or granting the test an override, and both dissolve the rule; instead it pins the
layer it can honestly pin — the parsing and the signature — while a companion test asserts that the
same bytes are refused for exactly that missing flag, and the ladder itself is pinned synthetically.
A captured assertion with UV set should replace it if one becomes available.

### Every assertion failure looks identical

Unknown credential, bad signature, wrong origin, consumed challenge, counter regression,
user-handle mismatch — all produce a byte-identical `401` with one fixed sentence. A distinguishing
message is a credential-enumeration oracle. The specific reason travels as a property on the
exception, for logs only. Registration failures are authenticated, so they surface as ordinary
`400`/`409` with a real sentence.

## Alternatives considered

**Adopt a FIDO2 library.** The honest case for it is strong: it is written by people who have read
the whole specification, it handles attestation formats and conformance-mode CBOR, and it is
maintained. Rejected here on scope rather than on principle — with attestation reduced to `none`,
two algorithms, and no `allowCredentials`, what remains is a few hundred lines of parsing and two
`VerifyData` calls, and the library's remaining value is mostly the parts deliberately not used. A
dependency in the authentication path is also a dependency in the supply chain of the one component
whose compromise is total. Should the narrowings above ever be widened — attestation verification
in particular — this decision should be reopened rather than stretched.

**Hand-roll CBOR as well, for zero dependencies.** Rejected. The parser is the riskiest component
and the one with the least product-specific content; writing it buys nothing and risks the most.

**Support every COSE algorithm an authenticator might offer.** Rejected. Each is a verifier branch
that no test corpus exercises, on a code path where a wrong branch is an authentication bypass.

## Consequences

- The verification code is pure and unit-tested exhaustively, which is the property that makes the
  risk manageable. Keeping it free of I/O is therefore not a style preference — a verifier that
  needs a database to test is a verifier that gets tested less.
- An authenticator producing a format outside the narrowings is **refused**, not misread. The
  likeliest first real-device failure is CBOR conformance mode or an unexpected attestation format,
  and both produce a clean refusal rather than a wrong acceptance.
- The `prf` extension is requested on registration, and the response's `prf.enabled` flag is
  reported back to the client and **stored nowhere**. It is asserted by the client, covered by no
  signature, and the server can neither verify it nor ever see the PRF output, which never leaves
  the authenticator. Whichever change first refuses an authenticator that cannot hold the account's
  keys has to decide what an unverifiable claim may gate; it must not assume this one proved
  anything about it.
- Adding an algorithm, or widening attestation, is a schema change and an ADR revision rather than
  a switch statement edit.
