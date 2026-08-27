// The account owns **one** content key and **one** index key, and every recovery
// factor holds its own wrapped copy of both. A passkey is one factor; a set of
// recovery codes is another. Each factor derives its own key-encryption key from
// whatever it can produce — PRF output, or a typed-back code — wraps the same two
// account keys under it, and stores the two envelopes beside itself.
//
// **The keys belong to the account, never to the credential that guards them.**
// Reverse that and adding a passkey stops being a second way *in* and becomes a
// second, incompatible budget: the second authenticator gets a second content key,
// so everything written under the first is unreadable through it, and a second
// index key, so one merchant name produces two blind index values and the
// uniqueness rule silently stops firing on half the data. Every symptom arrives
// late — the first factor still works, so nothing is noticed until it is gone or
// until the two are used on different devices.
//
// What binds a wrapped copy to where it lives is the associated data, and it
// names the factor *and* which of the two keys it is. Binding neither is IFR-009
// in full: a wrapped copy lifted into another account's row opens there. Binding
// only the factor leaves one factor's two envelopes interchangeable, which is the
// column swap `wrappedKeyAssociatedData` exists to make fail.
//
// **What leaves this module is a non-extractable `CryptoKey`, never bytes.**
// That is a claim about the boundary and about nothing else. There is no API that
// reads such a key back out, so no code holding the object can log the value that
// unwraps the account's whole keyspace, serialise it into a request body, put it
// in `localStorage` or hand it to a crash reporter. It is the reason both
// derivations below return a `CryptoKey` instead of a `Uint8Array`, and the reason
// the spec observes them by sealing under them and comparing envelopes: there is
// nothing else left to look at.
//
// Inside the file it is bytes twice per derivation — what `hkdfSha256` returns,
// and the copy a door below hands WebCrypto — and both are now
// zero-filled at the point they are consumed, in a `finally`, because the path
// that skips a wipe is the path where something already went wrong. The material
// is wiped where it is *used* rather than where it was made, so `hkdfSha256` stays
// a general utility for its other callers instead of being reshaped around one
// caller's hygiene. The recovery-code **verifier** branch keeps its uncleared copy
// on purpose: a verifier is sent to the server, so wiping it locally buys nothing
// the wire has not already given away. A key-encryption key goes nowhere, which is
// the whole reason it is worth the `finally`.
//
// **There are two doors from bytes to a key, and the number that matters about
// them is that it is not zero.** `importAesGcmKey` is one, `importHmacSha256Key`
// is the other, and they are separate because the account's two keys are two
// different kinds of key: the content key encrypts, and the index key is what a
// blind index is computed under, which is HMAC-SHA-256 and not a cipher at all.
// The platform agrees, and refuses to let either stand in for the other —
// measured on this runner, `sign` under a key imported as AES-GCM and `encrypt`
// under a key imported as HMAC are both refused with `InvalidAccessError`. The
// earlier version of this file argued there was *one* place where bytes become a
// key. That was never the claim worth making, because the count was never the
// point: what a second door costs is nothing, and what a *zeroth* would cost is
// everything the paragraph below lists. Two doors keep the claim. A third
// written by hand beside the caller that needed it destroys it.
//
// Each door holds five decisions in one place — the algorithm, the width, the
// usage list, the non-extractability, and the death of the raw bytes — and a
// hand-written `crypto.subtle.importKey` beside a caller holds **none** of the
// five. That is not a remark about carelessness. It is four lines, it compiles,
// it returns a perfectly good `CryptoKey`, and of the five only a wrong
// *algorithm* is ever mentioned by anything — and it is mentioned at the first
// call, not at the import, by which time the material has been wiped or not
// according to nobody's rule. A width silently downgraded, a usage list widened
// to `wrapKey`, an extractable key and a copy of the bytes left on the heap all
// work, forever, and are wrong for the life of the account.
//
// **`crypto.subtle.importKey` is written in two non-spec files today, this one
// and `hkdf.ts`, and in three places across them.** Both of this file's are
// doors. `hkdf.ts`'s is not: it imports input keying material for a derivation
// and gets back an `HKDF` key whose only usage is `deriveBits`, so nothing can
// seal, sign or export under it and nothing can mistake it for a key the account
// uses. That distinction — a usable cipher or MAC key, versus material on its
// way through a derivation — is what "door" means here, and it is the reason the
// third call is not a counter-example to the two.
//
// **Every function here has a live caller, the unwrapping included.**
// `register.service.ts` draws the account's keys and wraps them under a passkey
// and under each of ten recovery codes; `webauthn-ceremony.service.ts` derives
// the passkey branch's key-encryption key on both legs of a ceremony; and
// `unwrapAccountKeys` is reached by `AccountKeyCustodyService` on every passkey
// sign-in, over the entries `GET /api/me/account-keys` hands back. So "keep it,
// something is waiting" is no longer the reason to keep any of it.
//
// What is still uncalled is anything that *uses* an opened key — nothing seals a
// field and nothing computes an index, because nothing in this product is
// encrypted. The spec remains the only place several of these rules can be
// checked at all: the frozen vectors are what a second implementation has to
// reproduce, and a non-extractable key has no other witness.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so a function is the whole of it; a class would
// only add a way to hold key material alive past the ceremony that produced it.
import { buildAssociatedData } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { hkdfSha256 } from './hkdf';
import { openEnvelope, sealEnvelope } from './key-envelope';
import { canonicalRecoveryCode } from './recovery-code-canonical';
import { RECOVERY_CODE_BRANCH_INFO } from './recovery-codes';

/**
 * Width of each account key, in bytes.
 *
 * Thirty-two is AES-256, which is what `key-envelope.ts` seals with. Nothing
 * downstream would object to less: the envelope carries no statement about its
 * key's width, and sixteen bytes import, seal and open just as happily as an
 * AES-128 key.
 *
 * So this number is where the strength of every envelope the account ever writes
 * is decided, and `requireAccountKeyWidth` is where that decision is *enforced* —
 * it refuses any material of another width, which is the only reason the sentence
 * above is a rule rather than a hope. Both doors call it and neither restates it,
 * so the width of an account key has one decision and one enforcement of it
 * however many doors are added later. The two move together: widen this and the
 * seam widens with it; enforce it somewhere else as well and there are two
 * answers to how strong an account's envelopes are.
 *
 * It bounds the index key too, which is not an envelope and has no strength this
 * number obviously governs. That is deliberate: an account's two keys are drawn
 * from one call of one width, and giving the index key a width of its own would
 * mean two numbers to keep true of one draw.
 */
export const ACCOUNT_KEY_BYTES = 32;

/**
 * The value a passkey's `prf` extension is evaluated against.
 *
 * `webauthn-encoding.ts` writes it into the `prf` extension's `eval.first` of a
 * registration's creation options, and `webauthn-ceremony.service.ts` sends the
 * same bytes on the assertion leg and feeds what comes back to
 * {@link keyEncryptionKeyFromPasskey}. It lives here rather than beside either of
 * them because it is one half of a derivation the other half of which is in this
 * file, and because that puts it under the spec's pin — the only thing in the
 * system that would notice it drifting. The day it drifts, every account that
 * wrapped its keys under the old value is locked out silently, by a passkey that
 * still authenticates perfectly and simply hands back different bytes.
 *
 * The `/v1` suffix is not decoration. A change to this string is a new version
 * minted alongside the old one, never an edit to this line.
 */
export const PASSKEY_PRF_EVAL_INPUT = 'budgetoid/passkey/prf-eval-input/v1';

/**
 * The HKDF `info` of the branch that turns PRF output into a key-encryption key.
 *
 * `info` is the only thing separating this branch from the recovery code's, and
 * equal labels fail silently: every wrap works, every unwrap works, and one
 * factor's key-encryption key quietly becomes derivable from another factor's
 * input. Versioned, and for the same reason as {@link PASSKEY_PRF_EVAL_INPUT} —
 * see `RECOVERY_CODE_VERIFIER_INFO` for the identical argument on the other side.
 */
export const PASSKEY_KEY_ENCRYPTION_KEY_INFO =
  'budgetoid/passkey/key-encryption-key/v1';

/**
 * The literal that opens the associated data of every wrapped key.
 *
 * Part of the definition of every envelope already written — associated data is
 * not carried inside an envelope, it is re-supplied from where the envelope was
 * found, so a changed prefix makes every stored copy unopenable with the same
 * failure a corrupted key gives.
 */
export const WRAPPED_KEY_AAD_PREFIX = 'budgetoid/wrapped-key/v1';

/** Which of the account's two keys a wrapped copy holds. */
export type WrappedKeyPurpose = 'content' | 'index';

/** The account's key material, unwrapped. */
export interface AccountKeys {
  /** Encrypts what a person wrote. */
  readonly contentKey: Uint8Array;
  /** Keys the blind index over it. */
  readonly indexKey: Uint8Array;
}

/** One factor's copy of {@link AccountKeys}, as the two values cross the wire. */
export interface WrappedAccountKeys {
  /** Unpadded base64url over an envelope. */
  readonly wrappedContentKey: string;
  /** Unpadded base64url over an envelope. */
  readonly wrappedIndexKey: string;
}

// The canonical spelling, and the ways a caller may write it. **The client
// mints these identifiers**, and that is the decision ADR 0018 §4 exists to
// record: a server-minted one was refused, because letting a caller choose
// `credentials.id` instead would retire ADR 0014's first leg. So nothing
// upstream hands this module a normalised value — the fold below is the only
// place one is made.
//
// **The hazard is case, not braces.** Associated data is re-supplied from where
// the envelope was found rather than carried inside it, so the spelling a
// factor is bound under has to be the spelling every later read reproduces.
// The server keeps a uuid and renders it back lower-case hyphenated, so a
// client that binds an upper-case rendering seals two envelopes whose
// associated data nothing will ever rebuild — permanently, with nothing
// anywhere naming the cause. Folding here is what keeps this client from being
// that client.
//
// The server no longer accepts any other spelling at all: `CanonicalFactorId`
// parses with `"D"` and then compares the submitted text ordinally against
// `parsed.ToString("D")`, so upper-case hex, surrounding whitespace, the bare
// 32-digit form and the brace- and parenthesis-wrapped ones are each refused
// with a 400. Against that contract the tolerance below is dead: a caller that
// mints one of those spellings has its request refused whatever this module
// folded it to. It stays because it is the half of the rule this module can
// hold on its own — the refusal is a fact about today's server, while these
// envelopes are sealed here, before any server has seen the value.
const HYPHENATED_UUID =
  /^([0-9a-f]{8})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{12})$/;
const BARE_UUID =
  /^([0-9a-f]{8})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{12})$/;

const utf8 = new TextEncoder();

/**
 * Draws the account's two keys.
 *
 * Both come from `crypto.getRandomValues` and from nowhere else. `Math.random`
 * passes every shape-based check — two well-formed 32-byte values that differ —
 * while being seeded from a value the page does not control, shared with every
 * other caller on the page and short enough to walk, which would make an account's
 * content key reproducible by anybody who observes a few of its outputs.
 *
 * **Neither key is derived from the other.** `indexKey = hkdf(contentKey, …)` is
 * the tidy-looking mistake: two well-formed values that differ, one secret to back
 * up instead of two, and the index key stops being a second secret — anything that
 * recovers the content key recovers the search keyspace with it for free.
 *
 * The two keys are **copies**, not views over one draw. A caller that wipes one
 * key after wrapping it would otherwise wipe the other, and the account would lose
 * half its material with no error anywhere.
 *
 * The draw itself is zero-filled before the return, and that wipe exists *because*
 * of the copies: they are what puts both account keys somewhere no caller can
 * name, so no caller's own `finally` can reach them.
 */
export function generateAccountKeys(): AccountKeys {
  // One draw of sixty-four bytes rather than two of thirty-two: one call to the
  // platform's generator, and the split below is plain arithmetic over its output
  // rather than an assumption that two calls are independent.
  const draw = new Uint8Array(2 * ACCOUNT_KEY_BYTES);
  crypto.getRandomValues(draw);

  try {
    // `Uint8Array.from` over a `subarray` copies; the `subarray` alone would be a
    // view over `draw`, leaving the two keys and the draw aliased to one buffer.
    return {
      contentKey: Uint8Array.from(draw.subarray(0, ACCOUNT_KEY_BYTES)),
      indexKey: Uint8Array.from(draw.subarray(ACCOUNT_KEY_BYTES)),
    };
  } finally {
    // A `finally` rather than a statement before the return, for the reason the
    // ceremony service's own wipe gives: the path that skips the wipe is the path
    // where something already went wrong, and that is the worst moment to leave
    // both of the account's keys sitting in one buffer nothing else names.
    draw.fill(0);
  }
}

/**
 * Derives a passkey factor's key-encryption key from its PRF output.
 *
 * `HKDF-SHA-256(prfOutput, salt = ∅, info = {@link
 * PASSKEY_KEY_ENCRYPTION_KEY_INFO})`, imported as a non-extractable AES-GCM key —
 * see the note at the top of this file for why the bytes do not come back out.
 *
 * Takes a `BufferSource` because PRF output is raw bytes from the authenticator
 * and no encoding decision belongs to this branch.
 */
export async function keyEncryptionKeyFromPasskey(
  prfOutput: BufferSource,
): Promise<CryptoKey> {
  const material = await hkdfSha256(
    prfOutput,
    PASSKEY_KEY_ENCRYPTION_KEY_INFO,
    ACCOUNT_KEY_BYTES,
  );

  return importAesGcmKey(material);
}

/**
 * Derives a recovery-code factor's key-encryption key from one code.
 *
 * The code is folded to its canonical form first — the rule
 * `recovery-code-canonical.ts` owns, imported rather than restated, because a
 * branch that folded differently by so much as a stripped hyphen would redeem fine
 * and unwrap nothing.
 *
 * The derivation is `RECOVERY_CODE_BRANCH_INFO.keyEncryptionKey`, imported from
 * `recovery-codes.ts` and never written out here: this branch and the verifier's
 * differ by `info` alone, and that is exactly what keeps them independent. Two
 * copies of one label are two places for them to become one — and if they did, the
 * value sitting in a redemption request body would be the account's key-encryption
 * key.
 *
 * Takes a plain `string` rather than a `RecoveryCode` for the same reason
 * `recoveryCodeVerifier` does: the caller this will have is a redemption screen,
 * where the code is whatever a person typed.
 */
export async function keyEncryptionKeyFromRecoveryCode(
  code: string,
): Promise<CryptoKey> {
  const canonical = canonicalRecoveryCode(code);

  // Refused for the reason `recoveryCodeVerifier` refuses it, and after
  // canonicalisation for the same reason: an unbound form control, or a field
  // holding only the grouping the person was shown, derives a perfectly usable
  // key-encryption key, and the account's whole keyspace would then be wrapped
  // under the empty string. Every other wrong code is left alone to unwrap
  // nothing; this one cannot be told from a real code by anything downstream.
  if (canonical.length === 0) {
    throw new Error(
      'A key-encryption key cannot be derived from an empty recovery code.',
    );
  }

  const material = await hkdfSha256(
    utf8.encode(canonical),
    RECOVERY_CODE_BRANCH_INFO.keyEncryptionKey,
    ACCOUNT_KEY_BYTES,
  );

  return importAesGcmKey(material);
}

/**
 * Builds the associated data one wrapped copy is bound to:
 *
 * ```text
 * "budgetoid/wrapped-key/v1" || 0x1F || <factor id> || 0x1F || <purpose>
 * ```
 *
 * in UTF-8.
 *
 * `factorId` is normalised to the canonical lower-case hyphenated spelling and
 * **refused** if it is not a UUID. Both halves are load-bearing: without the
 * normalisation, two spellings of one factor are two different bindings and the
 * envelope written under one will not open under the other; without the refusal, a
 * free-form label — "the passkey on my phone" — reintroduces every ambiguity the
 * separators exist to remove, in a value written once and read back forever.
 *
 * `purpose` is in here so that one factor's two envelopes are not interchangeable.
 * Swap the two stored columns without it and both still open, both still yield 32
 * usable bytes, and the account quietly acquires a second index keyspace in which
 * everything written before the swap is invisible.
 */
export function wrappedKeyAssociatedData(
  factorId: string,
  purpose: WrappedKeyPurpose,
): Uint8Array {
  // The join and the separator are `associated-data.ts`'s, shared with the one
  // other grammar this client seals under. What stays here is what this grammar
  // is made of: these three fields, in this order — and the claim that none of
  // them can contain the separator, which is why `canonicalFactorId` checks the
  // only one whose shape a caller chooses.
  return buildAssociatedData(
    WRAPPED_KEY_AAD_PREFIX,
    canonicalFactorId(factorId),
    purpose,
  );
}

/**
 * Wraps both account keys under one factor's key-encryption key.
 *
 * Two envelopes, each bound to `factorId` and to its own purpose, each rendered as
 * unpadded base64url — the wire form `decodeBase64Url` and the server's decoder
 * both accept, and the one every other secret this client sends already uses.
 *
 * The keys are the *account's*: the caller generates them once and wraps them once
 * per factor. Generating a fresh pair here per factor would pass every round trip
 * and lose the account's whole history the first time the other factor is used.
 */
export async function wrapAccountKeys(
  kek: CryptoKey,
  keys: AccountKeys,
  factorId: string,
): Promise<WrappedAccountKeys> {
  const [wrappedContentKey, wrappedIndexKey] = await Promise.all([
    wrapOne(kek, keys.contentKey, factorId, 'content'),
    wrapOne(kek, keys.indexKey, factorId, 'index'),
  ]);

  return { wrappedContentKey, wrappedIndexKey };
}

/**
 * Opens both wrapped copies under one factor's key-encryption key, or rejects.
 *
 * Rejects on a copy that belongs to another factor, on the two copies presented in
 * each other's place, and on any of the refusals `openEnvelope` and
 * `decodeBase64Url` make. The first two are GCM's authentication failing over the
 * associated data this module built, which is the whole point of building it:
 * neither is distinguishable from corruption, and neither yields bytes.
 */
export async function unwrapAccountKeys(
  kek: CryptoKey,
  wrapped: WrappedAccountKeys,
  factorId: string,
): Promise<AccountKeys> {
  const [contentKey, indexKey] = await Promise.all([
    unwrapOne(kek, wrapped.wrappedContentKey, factorId, 'content'),
    unwrapOne(kek, wrapped.wrappedIndexKey, factorId, 'index'),
  ]);

  return { contentKey, indexKey };
}

/**
 * Imports {@link ACCOUNT_KEY_BYTES} of raw material as a non-extractable AES-GCM
 * key, wipes the material, or rejects.
 *
 * **The one place in this client where bytes become an AES-GCM key, whichever
 * key they are.** Both key-encryption-key derivations above go through it, and so
 * does anything that has to turn one of the account's own keys — which
 * {@link generateAccountKeys} and {@link unwrapAccountKeys} hand back as
 * `Uint8Array`, never as a key object — into something a cipher will take. That
 * makes it, like {@link importHmacSha256Key} beside it, a place where five
 * decisions are taken together: the algorithm, the width, the usage list, the
 * non-extractability, and the death of the raw bytes.
 *
 * The concentration is the point. The alternative is not a weaker version of this
 * function, it is another `crypto.subtle.importKey` written by hand beside a
 * caller, holding **none** of the five — and of the five, only the non-extractability
 * would be noticed downstream, because `sealNarrativeField` refuses an
 * extractable key. A hand-written import that took AES-128, added `wrapKey` to
 * the usages and left the caller's bytes on the heap would work perfectly,
 * forever, with nothing anywhere naming the moment the account's envelopes got
 * weaker.
 *
 * **The width is refused rather than trusted, and that is what makes
 * {@link ACCOUNT_KEY_BYTES}' claim true.** WebCrypto will not do it: measured on
 * Node's implementation, `importKey` accepts 16 and 24 bytes as AES-128 and
 * AES-192 and refuses every other non-32 width with `DataError` — so the two
 * widths nothing else objects to are exactly the two that silently downgrade an
 * account for the rest of its life, sealing and opening without complaint the
 * whole time.
 *
 * The check itself is `requireAccountKeyWidth`, shared with
 * {@link importHmacSha256Key}. Here it closes a two-width gap the platform leaves
 * open; there it is the *entire* guard, because HMAC accepts every width there
 * is. One rule, two doors, and very different amounts of work.
 *
 * `extractable: false` is the reason the derivations return a `CryptoKey` at all.
 * `encrypt` and `decrypt` and nothing else: this key works through the envelope,
 * and a usage list that also carried `wrapKey`/`unwrapKey` would open a second
 * path out for key objects, unrelated to the bytes this import is refusing to
 * give up.
 *
 * **This is where material stops being bytes, so it is where both copies of those
 * bytes die** — including on the refusal, because the path that skips a wipe is
 * the path where something already went wrong. `owned` is the value in the clear
 * on a buffer no name outside this call refers to once `importKey` has been
 * handed it; `material` is the same value one step earlier, and this function is
 * its last consumer. Wiping the caller's array here rather than teaching
 * `hkdfSha256` to hand out a disposable is deliberate: HKDF is a general utility
 * with other callers, and reshaping its signature for one caller's hygiene is an
 * API change every other caller pays for. Consuming code owning the wipe is also
 * the honest reading — the material dies where it is used, not where it was made.
 */
export async function importAesGcmKey(
  material: Uint8Array,
): Promise<CryptoKey> {
  // Copied onto a buffer WebCrypto's `BufferSource` accepts, for the reason
  // `key-envelope.ts` states at length: a bare `Uint8Array` is a view over either
  // kind of buffer, and narrowing by copying asserts nothing about the caller's.
  const owned = Uint8Array.from(material);

  try {
    // Inside the `try`, so the refusal is wiped by the same `finally` the success
    // is — the position matters as much as the check, and the helper's own note
    // says why.
    requireAccountKeyWidth(owned);

    // `await` rather than returning the promise: the wipe has to happen after
    // WebCrypto has read the buffer, and a bare `return` would run the `finally`
    // while `importKey` was still in flight.
    return await crypto.subtle.importKey('raw', owned, 'AES-GCM', false, [
      'encrypt',
      'decrypt',
    ]);
  } finally {
    owned.fill(0);
    material.fill(0);
  }
}

/**
 * Imports {@link ACCOUNT_KEY_BYTES} of raw material as a non-extractable
 * HMAC-SHA-256 key, wipes the material, or rejects.
 *
 * **The one place in this client where bytes become an HMAC key**, and the door
 * the account's index key goes through. A blind index is
 * `HMAC-SHA-256(indexKey, …)` — the index key is not a cipher key, and the
 * platform will not let it be used as one either way round: measured on this
 * runner, `sign` under a key imported as AES-GCM and `encrypt` under a key
 * imported as HMAC are both refused with `InvalidAccessError`. So the shorter
 * route — sending the index key through {@link importAesGcmKey} because it is
 * already written — hands back an object that cannot compute a single index and
 * cannot be corrected afterwards, because by then it is non-extractable and the
 * bytes are zeroes.
 *
 * **Here the width check is not one guard among several. It is the only one.**
 * {@link importAesGcmKey} has the platform underneath it — AES accepts 16, 24 and
 * 32 raw bytes and refuses every other width with `DataError` — so the module's
 * own check there is closing a gap two widths wide. HMAC has no such rule and
 * wants none, which is correct of HMAC and fatal here: measured, `importKey`
 * accepts 1, 15, 16, 24, 31, 32, 33 and 64 bytes as an HMAC-SHA-256 key and signs
 * a full 32-byte tag under every one of them, and refuses exactly one width —
 * zero — with `DataError`. Truncated material therefore imports, signs, and
 * yields a blind index that is stable, collision-free and keyed under a secret
 * that is not the account's index key. Every row the account ever writes is
 * indexed under it; nothing anywhere names the moment it started; and there is no
 * way back once the rows exist, because a blind index cannot be recomputed
 * without the plaintext it was taken over. The width is *recorded* by the
 * platform, on `key.algorithm.length`, and read by nothing in this client — which
 * is the shape of the whole hazard: the mistake is visible and unwatched.
 *
 * **`['sign']` and nothing else, and the omission is `verify`.** It is the usage
 * a reader adds without stopping, because HMAC has two halves and a key that only
 * does one looks unfinished. A blind index is computed and *compared* — this
 * client derives the value, the server matches rows on it — so there is no
 * verification for a browser to perform. What `verify` would add is a second path
 * out of this key: one that answers a boolean about a tag somebody else supplied,
 * under the key that keys the account's entire search space, and that answers it
 * one call at a time to whoever is asking.
 *
 * Everything else is {@link importAesGcmKey}'s reasoning and is not restated
 * here: the copy onto a buffer no caller names, `extractable: false`, the `await`
 * that keeps the wipe behind WebCrypto's read of it, and the `finally` that ends
 * both copies of the bytes on the refusing path exactly as thoroughly as on the
 * succeeding one.
 */
export async function importHmacSha256Key(
  material: Uint8Array,
): Promise<CryptoKey> {
  const owned = Uint8Array.from(material);

  try {
    requireAccountKeyWidth(owned);

    return await crypto.subtle.importKey(
      'raw',
      owned,
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['sign'],
    );
  } finally {
    owned.fill(0);
    material.fill(0);
  }
}

async function wrapOne(
  kek: CryptoKey,
  key: Uint8Array,
  factorId: string,
  purpose: WrappedKeyPurpose,
): Promise<string> {
  const envelope = await sealEnvelope(
    kek,
    key,
    wrappedKeyAssociatedData(factorId, purpose),
  );

  return encodeBase64Url(envelope);
}

function unwrapOne(
  kek: CryptoKey,
  wrapped: string,
  factorId: string,
  purpose: WrappedKeyPurpose,
): Promise<Uint8Array> {
  return openEnvelope(
    kek,
    decodeBase64Url(wrapped),
    wrappedKeyAssociatedData(factorId, purpose),
  );
}

// Folds a factor id to its canonical spelling, or throws.
//
// `toLowerCase`, never `toLocaleLowerCase`: the latter maps `I` to `ı` under a
// Turkish locale, which would fold one factor id to two different bindings on two
// phones — the same trap `recovery-code-canonical.ts` names in the other
// direction.
//
// Braces and parentheses come off before the shape is checked, and the shape is
// then the whole of the validation: eight, four, four, four and twelve hex digits.
// Deliberately no check of the version or variant nibbles — this module is not the
// authority on which UUIDs the server may mint, and a rule invented here would
// start refusing valid factor ids the day that authority changes its mind, with an
// error naming nothing a caller could act on.
function canonicalFactorId(factorId: string): string {
  const lowered = factorId.toLowerCase();
  const unwrapped =
    (lowered.startsWith('{') && lowered.endsWith('}')) ||
    (lowered.startsWith('(') && lowered.endsWith(')'))
      ? lowered.slice(1, -1)
      : lowered;

  const groups = HYPHENATED_UUID.exec(unwrapped) ?? BARE_UUID.exec(unwrapped);

  if (groups === null) {
    throw new Error(
      'A wrapped key can only be bound to a factor id that is a UUID.',
    );
  }

  return groups.slice(1).join('-');
}

// Refuses material that is not exactly `ACCOUNT_KEY_BYTES` wide.
//
// One function rather than an `if` at the top of each door, because two doors
// reading the same constant are two places for the enforcement to drift off the
// decision, and the drift is available in both directions: a bound relaxed on one
// door only, or — far likelier — a third door written later with no check at all,
// because whoever writes it will be importing an algorithm that raised no
// objection while they were testing it.
//
// **Called from inside each door's `try`, never above it.** That is what puts the
// refusal under the same `finally` as the success, so material rejected for being
// the wrong width dies exactly as thoroughly as material that became a key.
// Lifting this call out of the `try` — the tidier-looking arrangement, validation
// before work — is a one-line edit that reddens nothing and leaves the rejected
// bytes on the heap for the collector to get to whenever it does.
//
// The bound is the constant and never the number it currently holds, and the
// message interpolates it for the same reason: this is the enforcement of a
// decision taken at `ACCOUNT_KEY_BYTES`, so a check or a sentence written as `32`
// would go on enforcing, and explaining, a number the module had stopped
// believing in.
//
// It takes the material rather than a length so that no caller can pass the
// wrong one of two numbers in scope — `owned.length` and `material.length` are
// equal at every call site today, and a helper taking a `number` would be one
// copy-paste away from a door that checks the width of a buffer it is not
// importing.
function requireAccountKeyWidth(material: Uint8Array): void {
  if (material.length !== ACCOUNT_KEY_BYTES) {
    throw new Error(
      `A key can only be imported from exactly ${ACCOUNT_KEY_BYTES} bytes of material, not ${material.length}.`,
    );
  }
}
