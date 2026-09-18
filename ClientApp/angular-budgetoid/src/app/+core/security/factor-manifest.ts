// The factor manifest — one authenticated blob per account naming every
// recovery factor and the public key it holds, sealed under the account's
// content key.
//
// **What it buys is that the *set* is unforgeable.** A factor's own row already
// carries its wrapped private key and the account's keys encapsulated to its
// public half; what no row can say is which factors an account has. There is
// deliberately no per-row public key column, because a server that could add a
// row could add a factor — and a rotation stages one seal per factor, so a set
// with a stranger in it hands the account's keys to whoever owns the stranger.
// The manifest is that set, written by the client under a key the server does
// not hold. The server enforces presence, framing and epoch, and can never read
// a byte of it.
//
// **The count leads, so a truncated plaintext is a mismatch rather than a
// smaller set.** Without it, chopping the tail off a manifest produces a shorter
// manifest that parses — and the entry it drops is the authenticator somebody
// still has. With it, the reader that compares the named set against the served
// set is comparing against a value that says up front how many entries it owes.
// It is decimal text and not a byte: eleven factors — a passkey and a card of
// ten, which is what a registration writes — is the two characters `11`, and a
// byte would put a hard ceiling at 255 into a format that has no other reason
// for one. The same argument applies to the rotation epoch in the associated
// data below, which is why it is written the same way.
//
// **Entries ascend by the canonical spelling of the factor identifier.** The
// bytes have to be reproducible from a set, and a set has no order; insertion
// order would make two clients holding the same factors write two different
// plaintexts. The sort is over the *text*, never over the identifier's bytes:
// .NET's `Guid.ToByteArray()` is mixed-endian, so a sort over raw UUID bytes
// agrees with this one on most sets and disagrees on some — and the frozen
// three-factor vector is one of the ones it disagrees on, which is the only
// reason that disagreement is visible anywhere.
//
// **This module reads nothing back.** {@link openFactorManifest} hands back the
// plaintext as bytes and stops there, because the only thing this story needs
// from it is that the tag verified: that is how a client confirms the content
// key it has just obtained really is the account's content key. Comparing the
// named set against the set the server served is a later story and a parser is
// what it will need; shipping one now would be an export with no production
// caller.
//
// **Nothing here is secret and nothing here is wiped.** A manifest's plaintext
// is public keys and identifiers — material that is already handed to peers by
// construction — so there is nothing in it to clear, and a `fill(0)` written for
// symmetry with the modules next door would suggest the opposite. It is sealed
// because the *set* must be unforgeable and because the server has no business
// reading which authenticators an account has, not because a point is a secret.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so two functions are the whole of it.
import { joinFields } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { canonicalFactorId } from './factor-id';
import {
  FACTOR_KEYPAIR_LABEL,
  FACTOR_KEYPAIR_VERSION,
  requireUncompressedPoint,
} from './factor-keypair';
import { openEnvelope, sealEnvelope } from './key-envelope';

/**
 * The shortest thing that can be a sealed manifest: the AEAD envelope's own
 * floor — a version, a nonce and a tag with no ciphertext between them.
 *
 * It is the number the server enforces, restated here so this client refuses a
 * value before the server does rather than after. Nothing this module writes can
 * be this short, so the floor only ever fires on the reading side.
 */
export const FACTOR_MANIFEST_MIN_BYTES = 29;

/**
 * The widest sealed manifest the server stores.
 *
 * A ceiling rather than a limit on factors: entries are 103 bytes each — a
 * separator, a 36-character identifier, a separator and a 65-byte point — so
 * this admits 39 factors and refuses 40, measured rather than divided. A client
 * that sealed past it would be told by a 400 on a request it cannot retry, at
 * the end of a ceremony that has already drawn keys.
 */
export const FACTOR_MANIFEST_MAX_BYTES = 4096;

/** One factor of an account's set, as the manifest names it. */
export interface FactorPublicKey {
  /** Folded to the canonical spelling before it is written. */
  readonly factorId: string;
  /** Raw and uncompressed: {@link FACTOR_PUBLIC_KEY_BYTES} bytes leading 0x04. */
  readonly publicKey: Uint8Array;
}

const utf8 = new TextEncoder();

/**
 * Seals `factors` as the account's manifest at `rotationEpoch`, or rejects.
 *
 * The answer is unpadded base64url over the AEAD envelope — the wire form the
 * server's own decoder accepts — and the caller owes it to whichever path is
 * moving the factor set.
 *
 * The entries are ordered here and not by the caller: every path that promotes a
 * manifest holds a set, and a set arriving in the order somebody happened to
 * build it is the normal case rather than the exception.
 */
export async function sealFactorManifest(
  contentKey: CryptoKey,
  factors: readonly FactorPublicKey[],
  rotationEpoch: number,
): Promise<string> {
  const sealed = await sealEnvelope(
    contentKey,
    manifestPlaintext(factors),
    manifestAssociatedData(rotationEpoch),
  );

  // Measured on the sealed value, because that is the value the column holds.
  // Predicting it from the number of factors would be a second description of
  // the framing, kept true by nobody, and it would have to be re-derived the
  // day an envelope's overhead changes.
  requireManifestWidth(sealed.length);

  return encodeBase64Url(sealed);
}

/**
 * Opens a sealed manifest under `contentKey` at `rotationEpoch`, or rejects.
 *
 * Hands back **the plaintext bytes**, not the entries it names — see the note at
 * the top of this file for why that boundary is where it is.
 *
 * Rejects on a wire string that is not unpadded base64url, on anything outside
 * the width window the server enforces, on a version byte this code does not
 * know, on a single flipped bit anywhere, and on an epoch other than the one the
 * manifest was sealed at.
 *
 * **What that last refusal is and is not.** It *binds* the epoch a manifest was
 * sealed at to the epoch it is read under, so the two cannot be recombined: a
 * manifest and an epoch that were never sealed together do not open. It is
 * **not** rollback detection, and reading it as such would be believing this
 * client holds something it does not. The only source of `rotationEpoch` here is
 * the very response that carried the manifest, so whoever can replay one can
 * replay the other, and the pair verifies exactly as it did the day it was
 * written. Detecting that the account has moved on since needs a value this
 * client keeps for itself and compares against — story 12.14, which is where
 * refusing a response that carries no manifest lives too.
 *
 * `async` is load-bearing: the refusals below are `throw`s, and a synchronous
 * throw from a function whose signature promises a `Promise` escapes past every
 * caller's `catch` on the result.
 */
export async function openFactorManifest(
  contentKey: CryptoKey,
  wire: string,
  rotationEpoch: number,
): Promise<Uint8Array> {
  // The strict decoder, which refuses padding, the standard alphabet's `+` and
  // `/`, and a final group no encoder would emit. A lenient reading here would
  // accept a value the server's own decoder rejects.
  const sealed = decodeBase64Url(wire);

  // **Before the cipher, and in this module's own words.** The floor is the
  // envelope's floor to the byte, so `openEnvelope` would turn a short value
  // away on its own — but it would say "not an envelope" about a value that
  // came out of the manifest column, and it has nothing at all to say about the
  // ceiling. Checking here is what makes both ends of the window one rule with
  // one owner, and what lets a reader of the failure know which column the
  // value came from.
  requireManifestWidth(sealed.length);

  return await openEnvelope(
    contentKey,
    sealed,
    manifestAssociatedData(rotationEpoch),
  );
}

// `count ‖ 0x1F ‖ factorId ‖ 0x1F ‖ publicKey ‖ 0x1F ‖ …`, entries ascending by
// the canonical spelling of the identifier.
//
// The join is `associated-data.ts`'s, which is the one definition of the `0x1F`
// rule in this client — separators between the fields and none at either end,
// every field kept, nothing folded. A private copy here would be a second
// spelling of it, and the defect that copy would reintroduce is not
// hypothetical: the shared one was just fixed for a leading empty field, whose
// symptom was a value of the right width with the separator in the wrong place.
//
// The public keys go in raw and are never re-encoded. Pushed through UTF-8 a
// 65-byte point comes out 129 bytes long — measured — with no error anywhere,
// which is the whole reason this grammar joins bytes rather than strings.
function manifestPlaintext(
  factors: readonly FactorPublicKey[],
): Uint8Array<ArrayBuffer> {
  const entries = orderedEntries(factors);

  return joinFields(
    utf8.encode(String(entries.length)),
    ...entries.flatMap((entry) => [
      utf8.encode(entry.factorId),
      entry.publicKey,
    ]),
  );
}

// The entries in the one order this format has, with every identifier folded to
// the spelling it is ordered and written under.
//
// **Folded before it is sorted, never after.** The fold is the thing the order
// is defined over, so sorting first and folding second would order `A1B2…`
// before `a1b2…` and write them the other way round — two clients holding one
// set, writing two plaintexts, neither of them wrong-looking.
//
// **Compared as code units with `<`, never `localeCompare`** — and **nothing
// holds that line**, so it is stated rather than tested. Measured: over 200,000
// random pairs of canonical identifiers the collator and an ordinal comparison
// never disagreed, which is unsurprising once the fold above has left nothing
// but lower-case hex and hyphens. Swap one in and the suite agrees with you. It
// stays because a collator is locale- and implementation-dependent by
// specification while these bytes are frozen: the day this runs under a
// collation that orders them differently, two clients holding one set write two
// plaintexts, both open, and nothing anywhere names the cause. The server
// compares ordinally, and this is that comparison.
function orderedEntries(
  factors: readonly FactorPublicKey[],
): readonly FactorPublicKey[] {
  const folded = factors.map((factor) => {
    // IFR-019, and `factor-keypair.ts`'s guard rather than a length written
    // again here. A point of another width is not a shorter point: the entry
    // has no length prefix, so 64 bytes shift every byte after them and 65
    // bytes under the hybrid encoding look right and derive nothing. The rule
    // has one owner because a second copy is one edit away from admitting the
    // compressed form on the day some other screen finds it convenient.
    requireUncompressedPoint(factor.publicKey);

    return {
      factorId: canonicalFactorId(factor.factorId),
      publicKey: factor.publicKey,
    };
  });

  requireDistinct(folded);

  // A copy is sorted rather than the caller's array, which `map` above already
  // made: `sort` is in place, and re-ordering a set somebody else holds is a
  // side effect nothing at the call site would expect.
  return folded.sort((left, right) =>
    left.factorId < right.factorId
      ? -1
      : left.factorId > right.factorId
        ? 1
        : 0,
  );
}

/**
 * `label ‖ 0x1F ‖ version ‖ 0x1F ‖ rotationEpoch, decimal digits`.
 *
 * The label and the version byte are the factor-keypair grammar's own,
 * imported and never restated: this message is a third message of that grammar,
 * so a second copy of either constant here would be a second grammar that looked
 * like the first until one of them moved.
 *
 * **The epoch is the manifest's associated data and nothing else binds it.** The
 * server refuses anything that is not the stored epoch plus one, but it cannot
 * read what the client sealed — so a manifest that named the right set at the
 * wrong epoch would store, and would come back at a rotation that has since
 * happened. Authenticating the epoch is what turns that into a tag failure.
 */
function manifestAssociatedData(
  rotationEpoch: number,
): Uint8Array<ArrayBuffer> {
  // **`String` does not render every number as digits**, and the two it renders
  // otherwise are both reachable by arithmetic: a fraction comes out `1.5` and
  // anything past 1e21 comes out `1e+21`. Either is a well-formed message that
  // no server and no other client will ever rebuild, so a manifest sealed under
  // one stores and then never opens — the unnamed permanent lockout this whole
  // grammar is careful about. The epoch arrives from the server's stored value
  // plus one, so nothing today hands us such a number; this costs one
  // comparison and the alternative has no repair path.
  if (!Number.isSafeInteger(rotationEpoch) || rotationEpoch < 1) {
    throw new Error(
      `A rotation epoch is a whole number from 1 up, not ${rotationEpoch}.`,
    );
  }

  return joinFields(
    utf8.encode(FACTOR_KEYPAIR_LABEL),
    grammarVersion(),
    utf8.encode(String(rotationEpoch)),
  );
}

// Refuses a factor named twice.
//
// **A manifest naming one factor twice is not a set**, and nothing downstream
// would say so: the count leading the plaintext would agree with the repeat, the
// framing would be well formed, and a rotation reading it would stage two seals
// for one factor and believe it had covered one more than it had. The check is
// over the *folded* identifiers, because the two spellings of one factor are
// only equal after the fold — which is the same reason the fold happens before
// the sort.
//
// It names the offender. A set arrives from somewhere — a caller assembling a
// card of ten beside a passkey — and "a duplicate" without the value is a
// refusal nobody can act on.
function requireDistinct(entries: readonly FactorPublicKey[]): void {
  const seen = new Set<string>();

  for (const entry of entries) {
    if (seen.has(entry.factorId)) {
      throw new Error(
        `A factor manifest names ${entry.factorId} more than once; a set names each factor once.`,
      );
    }

    seen.add(entry.factorId);
  }
}

// Refuses a sealed manifest outside the window the server stores.
//
// One function for both ends and both directions, because they are one rule:
// the column takes between {@link FACTOR_MANIFEST_MIN_BYTES} and
// {@link FACTOR_MANIFEST_MAX_BYTES} bytes. On the writing side only the ceiling
// can fire — an envelope is never shorter than its own floor — and it is the one
// that matters there, since the alternative is a 400 at the end of a ceremony
// that has already drawn keys. On the reading side both can.
//
// The width is in the message. A caller told only that its set is too large
// cannot tell whether it is over by one factor or by ten.
function requireManifestWidth(width: number): void {
  if (width < FACTOR_MANIFEST_MIN_BYTES || width > FACTOR_MANIFEST_MAX_BYTES) {
    throw new Error(
      `A sealed factor manifest is between ${FACTOR_MANIFEST_MIN_BYTES} and ${FACTOR_MANIFEST_MAX_BYTES} bytes, not ${width}.`,
    );
  }
}

// The grammar's version as the one raw byte it is. A fresh array per call, for
// `joinFields`' reason: a shared one is a buffer a caller could reach into and
// change under the next caller's message.
function grammarVersion(): Uint8Array {
  return Uint8Array.of(FACTOR_KEYPAIR_VERSION);
}
