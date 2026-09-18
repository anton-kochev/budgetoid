// One HKDF-SHA-256, shared by every branch this client derives.
//
// `recovery-codes.ts` carries a private `deriveBranch`, and the next branch —
// the account's key-encryption key — needs the same expansion under a different
// label. That sameness *is* the security argument: HKDF's expand step is a keyed
// PRF over `info`, so two branches over identical input keying material, salt
// and hash are independent, and knowing one output says nothing about the other.
// Two copies of the expansion would be two places for the hash, the salt or the
// label encoding to drift apart, and every symptom of drift is silent — keying
// material of exactly the right width that decrypts nothing.
//
// `info` is therefore the only thing that separates one branch from another, and
// the only thing these functions take besides the material and the width. An
// implementation that dropped it on the floor would fold the verifier that
// crosses the wire into the key-encryption key that never does, and everything
// downstream would keep working: the client derives, the server accepts, and the
// value in a request body is the account's key.
//
// **Two signatures, one expansion.** Most labels here are text, and for those
// the UTF-8 encoding is a decision this module makes rather than each caller —
// see `hkdfSha256`'s note. One branch's label is not text at all: the
// factor-keypair grammar's info carries two raw 65-byte curve points, which
// UTF-8 widens to 129 bytes each. So `hkdfSha256Over` takes the bytes and
// `hkdfSha256` is one line on top of it. What must not follow is a second
// expansion written beside the first — the same drift this module exists to
// prevent, one function further in — which is why exactly one
// `crypto.subtle.importKey` is written below.
//
// The salt is fixed empty, which is a requirement rather than a simplification.
// RFC 5869 permits an empty salt, and a redemption arrives carrying a verifier
// and no identity at all — no account, no credential, no row — so there is no
// per-account value the derivation could take a salt from before the lookup the
// salt would be needed for. A fixed non-secret salt would be domain separation
// spelled twice, and `info` already spells it. See ADR 0015's rejection of
// per-row salting for the server-side half of the same argument.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency. A function is the whole of it.

// See the note above: empty by requirement, and pinned by RFC 5869 §A.3, whose
// expected output is unreachable with a salt of any other value.
const EMPTY_SALT = new Uint8Array(0);

const HKDF_HASH = 'SHA-256';

const BITS_PER_BYTE = 8;

const utf8 = new TextEncoder();

/**
 * Derives `bytes` bytes of keying material: `HKDF-SHA-256(ikm, salt = ∅, info)`.
 *
 * `ikm` is a {@link BufferSource} rather than a string because the two branches
 * that use it start from different things — the passkey branch's input keying
 * material is raw PRF output bytes, the recovery-code branch's is UTF-8 text. So
 * the caller encodes, and this function never guesses at an encoding for
 * material it was handed as bytes.
 *
 * `info` is a string and is encoded UTF-8 here. That is the one encoding
 * decision this module does make, and it is made in one place on purpose: the
 * wrong encoders a reader reaches for (`String.fromCharCode`, a Latin-1 buffer)
 * keep only the low byte of a code unit, so `ф` and `D` would label the same
 * branch and every ASCII-only vector would still pass. **That argument still
 * holds for every caller whose label is text**, which is why this signature
 * stays and why the encoding did not move out to them — it moved *down*, to
 * {@link hkdfSha256Over}, which this function is one line of.
 */
export function hkdfSha256(
  ikm: BufferSource,
  info: string,
  bytes: number,
): Promise<Uint8Array> {
  return hkdfSha256Over(ikm, utf8.encode(info), bytes);
}

/**
 * The same derivation over an `info` that is already bytes.
 *
 * **A branch whose label is not text needs this, and cannot be served by
 * {@link hkdfSha256} at all.** The factor-keypair grammar's info carries two raw
 * 65-byte elliptic-curve points; a point pushed through UTF-8 comes out 129
 * bytes long — measured, with no error anywhere, the replacement character
 * standing in for every byte that is not a code point. So a caller reaching for
 * the string signature there would not spell its `info` awkwardly, it would
 * derive a *different key*, and both sides of that scheme would agree with each
 * other and with nothing else.
 *
 * **One `crypto.subtle.importKey` in this module, and it is here.** Writing the
 * expansion twice — once per signature — would be two places for the hash, the
 * salt or the width to drift apart, and every symptom of that drift is silent:
 * keying material of exactly the right width that decrypts nothing. It is also
 * what `key-import-single-source.spec.ts` counts this file as one owner for.
 *
 * `deriveBits`, not `deriveKey`: the output is bytes the caller decides the
 * meaning of — a verifier that crosses the wire, or material a key is imported
 * from — and a `CryptoKey` of some assumed algorithm is not that.
 */
export async function hkdfSha256Over(
  ikm: BufferSource,
  info: BufferSource,
  bytes: number,
): Promise<Uint8Array> {
  const material = await crypto.subtle.importKey(
    'raw',
    ikm,
    'HKDF',
    // Not extractable, and the key is discarded with the call. It keeps the
    // imported keying material out of anything that could export it.
    false,
    ['deriveBits'],
  );

  const derived = await crypto.subtle.deriveBits(
    {
      name: 'HKDF',
      hash: HKDF_HASH,
      salt: EMPTY_SALT,
      info,
    },
    material,
    // `deriveBits` counts bits; every caller here thinks in bytes. The
    // multiplication lives on this side of the boundary so no caller can get it
    // wrong, and a width read as bits rather than bytes is invisible in the
    // output — the caller gets plausible-looking keying material either way.
    bytes * BITS_PER_BYTE,
  );

  return new Uint8Array(derived);
}
