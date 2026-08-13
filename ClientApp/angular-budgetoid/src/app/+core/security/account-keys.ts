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
// **A key-encryption key is a non-extractable `CryptoKey`, never bytes.** There
// is no API that reads one back out, so the value that unwraps the account's
// whole keyspace cannot be logged, serialised into a request body, put in
// `localStorage` or handed to a crash reporter by any code holding the object —
// not by discipline, by construction. It is the reason both derivations below
// return a `CryptoKey` instead of a `Uint8Array`, and the reason the spec observes
// them by sealing under them and comparing envelopes: there is nothing else left
// to look at.
//
// This module ships with a spec and **no caller**, on purpose, exactly as
// `recovery-codes.ts` does. The ceremonies that would use it — registering a
// passkey, generating a recovery-code set — are gated behind a WebAuthn assertion
// this client cannot run yet. Its spec is meanwhile the only place several of
// these rules can be checked at all: the frozen vectors are what a second
// implementation has to reproduce, and a non-extractable key has no other witness.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so a function is the whole of it; a class would
// only add a way to hold key material alive past the ceremony that produced it.
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { hkdfSha256 } from './hkdf';
import { openEnvelope, sealEnvelope } from './key-envelope';
import { canonicalRecoveryCode } from './recovery-code-canonical';
import { RECOVERY_CODE_BRANCH_INFO } from './recovery-codes';

/**
 * Width of each account key, in bytes.
 *
 * Thirty-two is AES-256, which is what `key-envelope.ts` seals with. Sixteen
 * would import, seal and open just as happily — the envelope carries no statement
 * about its key's width — so this number is the only place the strength of every
 * envelope the account ever writes is decided.
 */
export const ACCOUNT_KEY_BYTES = 32;

/**
 * The value a passkey's `prf` extension is evaluated against.
 *
 * **Nothing calls this yet**, and that is why it is here rather than in the story
 * that will. No client in this repository can run a WebAuthn ceremony, so there is
 * no PRF output to evaluate; a later story sends this string as the `prf`
 * extension's `eval.first` input and feeds what comes back to
 * {@link keyEncryptionKeyFromPasskey}. Declaring it now puts it under the spec's
 * pin, which is the only thing in the system that would notice it drifting — and
 * the day it drifts, every account that wrapped its keys under the old value is
 * locked out silently, by a passkey that still authenticates perfectly and simply
 * hands back different bytes.
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

// ASCII's unit separator, spelled by its code point rather than typed. A literal
// control character is invisible in every tool a reviewer would read this file
// in, which is the one property a byte of a frozen format cannot afford.
//
// It cannot occur in any of the three fields it separates — the prefix is a
// literal, the factor id is a canonical UUID, and the purpose is one of two words
// — so the fields cannot run into one another and no length prefix is needed.
// That is a claim about the *fields*, which is why `canonicalFactorId` below
// checks the only one whose shape a caller chooses.
const UNIT_SEPARATOR = String.fromCharCode(0x1f);

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
 */
export function generateAccountKeys(): AccountKeys {
  // One draw of sixty-four bytes rather than two of thirty-two: one call to the
  // platform's generator, and the split below is plain arithmetic over its output
  // rather than an assumption that two calls are independent.
  const draw = new Uint8Array(2 * ACCOUNT_KEY_BYTES);
  crypto.getRandomValues(draw);

  // `Uint8Array.from` over a `subarray` copies; the `subarray` alone would be a
  // view over `draw`, leaving the two keys and the draw aliased to one buffer.
  return {
    contentKey: Uint8Array.from(draw.subarray(0, ACCOUNT_KEY_BYTES)),
    indexKey: Uint8Array.from(draw.subarray(ACCOUNT_KEY_BYTES)),
  };
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

  return importKeyEncryptionKey(material);
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

  return importKeyEncryptionKey(material);
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
  const canonical = canonicalFactorId(factorId);

  return utf8.encode(
    `${WRAPPED_KEY_AAD_PREFIX}${UNIT_SEPARATOR}${canonical}${UNIT_SEPARATOR}${purpose}`,
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

// The one import of a key-encryption key, shared by both derivations so neither
// can drift into an extractable one on its own.
//
// `extractable: false` is the reason these functions return a `CryptoKey` at all.
// `encrypt` and `decrypt` and nothing else: this key wraps and unwraps the
// account's keys through the envelope, and a usage list that also carried
// `wrapKey`/`unwrapKey` would open a second path out for key objects, unrelated to
// the bytes this import is refusing to give up.
function importKeyEncryptionKey(material: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    // Copied onto a buffer WebCrypto's `BufferSource` accepts, for the reason
    // `key-envelope.ts` states at length: a bare `Uint8Array` is a view over
    // either kind of buffer, and narrowing by copying asserts nothing about the
    // caller's.
    Uint8Array.from(material),
    'AES-GCM',
    false,
    ['encrypt', 'decrypt'],
  );
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
