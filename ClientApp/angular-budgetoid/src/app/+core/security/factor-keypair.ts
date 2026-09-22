// The factor keypair — the ECDH P-256 pair every recovery factor holds.
//
// A factor no longer carries its own copy of the account's two keys. It carries
// a keypair: the private half *wrapped under* the key-encryption key the factor
// already derives, and the account's content key and index key *encapsulated to*
// its public half. The three verbs are not interchangeable and this file uses
// each of them once — *sealed under* a key over data, *wrapped under* a key over
// another key, *encapsulated to* a public key.
//
// **What that buys is a rotation that does not need every authenticator in the
// room.** Under the old arrangement, re-wrapping the account's keys for eleven
// factors meant holding eleven key-encryption keys at once, which means a person
// with a passkey on one phone and a card of codes in a drawer cannot rotate at
// all. Encapsulation needs the old content key and a *set of public keys*, and a
// public key is exactly the thing an absent authenticator can leave behind.
//
// **Three version bytes are all 1 and none of them may be derived from another.**
// {@link FACTOR_KEYPAIR_VERSION} versions this *grammar* — the associated-data
// messages and the HKDF info — and versions neither suite. `ENVELOPE_VERSION` in
// `key-envelope.ts` is the AEAD suite's, and leads the wrapped private key.
// {@link ENCAPSULATION_VERSION} below is the encapsulation suite's, and leads an
// encapsulated value. A reader who aliases any two of them renumbers a suite
// nobody meant to touch, and nothing anywhere would say so: all three spellings
// emit the same byte today, which is precisely why only a source-text reading can
// tell them apart. Each is written out as its own literal for that reason.
//
// **The messages are bytes, not text, and that is not a style preference.** The
// grammar version is a raw byte and both public keys are raw 65-byte points. Push
// a point through a UTF-8 string and it comes out 129 bytes long — measured — with
// no error anywhere; compose the version as text and the scheme is correct at 1
// and silently wrong from 0x80 up, where UTF-8 widens one byte into two. The
// frozen vectors carry a 0x80 message for exactly that reason. So this grammar
// joins with `associated-data.ts`'s `joinFields`, the bytes spelling of the join
// `buildAssociatedData` is now written on top of — one rule, one separator, and
// no second copy of "the fields are joined by 0x1F" living here.
//
// **The encapsulation message is a strict prefix of the private-key message**,
// which is the whole reason both are pinned. An implementation that forgets the
// purpose field does not produce garbage — it produces the *other* valid message
// of this same scheme, and a factor's two envelopes become interchangeable.
//
// **What leaves this module.** Two wire strings, the factor's public point, and —
// from {@link openFactorKeypair} — the account's two keys as bytes, because the
// doors next door take bytes. **A private key never leaves**: not as bytes, not
// as an extractable handle, and not as a non-extractable one. The extractable
// window at mint is forced by the platform and is as narrow as it can be made:
// `generateKey(..., extractable: false)` cannot be exported at all, and an EC
// private key has no `raw` export, so PKCS#8 is the only route out and the key
// has to be drawn extractable to take it. `drawFactorPkcs8` is therefore its own
// frame — the extractable handle is a local of a function that returns bytes, so
// it is unreachable from the line after the call, and there is nowhere in this
// module for it to be parked.
//
// **The re-import is where the private half really begins.** The bytes come back
// in non-extractable and the PKCS#8 dies in a `finally`, and only *then* is the
// public point lifted out of it at offset 73 — after the platform has vouched for
// the encoding rather than before. That order is load-bearing: `importKey`
// refuses a PKCS#8 whose embedded point was spliced in from another key
// (`DataError`, measured), so importing first makes the platform the authority on
// the bytes this module is about to read. Lifting first would read 65 bytes out
// of something nothing had checked.
//
// **Two mechanisms answer two different questions about a point and neither
// subsumes the other.** {@link requireUncompressedPoint} is IFR-019 — the
// uncompressed 65-byte encoding and nothing else, a synchronous check of a length
// and a leading byte. `crypto.subtle.importKey` is IFR-023 — a point off the
// curve, the point at infinity — and it performs that check itself, measured. The
// platform *accepts* two encodings IFR-019 forbids (the parity-consistent hybrid
// form and the compressed form), and the encoding guard cannot see whether a
// well-formed point is on the curve. So the guard does no curve arithmetic: doing
// it here would add unvectored cryptography to duplicate a check that already
// happens, and would redden the two frozen rows that require this guard to pass
// bytes the platform then refuses.
//
// **Exporting a correct guard is not the same as calling one**, and the
// difference is worth one sentence because it is invisible in a round trip. An
// implementation that hands a hybrid ephemeral point straight to `importKey`
// derives the right key and returns the right account keys. The frozen
// `guardIsActuallyCalled` value exists to open for that implementation, so the
// only thing that refuses it is an actual call at the agreement site — which is
// why the guard is written at the top of `encapsulationKey`, on both points,
// before either becomes a key object and before anything is derived.
//
// **`crypto.subtle.importKey` is written here twice, and neither is a door.**
// The two doors live in `account-keys.ts` and this file uses one of them — the
// encapsulation key goes through `importAesGcmKey`, which is what holds its
// algorithm, its width, its usage list, its non-extractability and the death of
// its bytes in one place. What is written here is the **factor's** two ECDH
// halves and nothing else: a PKCS#8 private half whose only usage is
// `deriveBits`, and a peer public point with *no* usages at all. Neither can
// seal, sign or export anything, and neither is the account's key material —
// which is why they are written here rather than added to that file, and the
// standing `key-import-single-source.spec.ts` records for this file says so.
//
// **The HKDF expansion is not written here, and a third import is what writing
// it would look like.** The raw ECDH agreement goes into `hkdfSha256Over`, whose
// `info` is bytes because this one is two raw points and no string can hold
// them; the hash, the empty salt and the width arithmetic stay with `hkdf.ts`.
// Note what would *not* catch a copy of them here: the key-import census counts
// this file as an owner, so a hand-written expansion beside its caller passes it
// silently. What catches it is `hkdf.spec.ts`, which holds that one module names
// the algorithm.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so functions are the whole of it. A class would
// only add somewhere for a private key to live past the ceremony that drew it.
import {
  ACCOUNT_KEY_BYTES,
  importAesGcmKey,
  type AccountKeys,
} from './account-keys';
import { joinFields } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { canonicalFactorId } from './factor-id';
import { hkdfSha256Over } from './hkdf';
import { openEnvelope, sealEnvelope } from './key-envelope';

/**
 * The literal that opens every message of this grammar — both associated-data
 * messages and the HKDF info.
 *
 * Part of the definition of every value already stored. Associated data is not
 * carried inside an envelope, it is re-supplied from where the envelope was
 * found, so a changed label makes every stored pair unopenable with the same
 * failure a corrupted key gives. The `/v1` suffix is not decoration: a change is
 * a new label minted beside this one, never an edit to this line.
 */
export const FACTOR_KEYPAIR_LABEL = 'budgetoid/factor-keypair/v1';

/**
 * The grammar's own version, written into every message as **one raw byte**.
 *
 * It versions the shape of the messages below and nothing else — not the AEAD
 * suite that frames a wrapped private key, and not the encapsulation suite that
 * frames an encapsulated value. All three are 1 today and each is written out
 * separately; see the note at the top of this file for what aliasing any two of
 * them costs.
 */
export const FACTOR_KEYPAIR_VERSION = 1;

/**
 * Width of a factor's private key in PKCS#8, in bytes.
 *
 * A P-256 private key exported as PKCS#8 with its public key included is exactly
 * this wide, and the width is *enforced* rather than observed: it is what makes
 * {@link FACTOR_PUBLIC_KEY_OFFSET} mean anything. An engine that omitted the
 * optional public-key field would export a shorter structure, and a lift at
 * offset 73 out of that reads whatever is there — a well-formed-looking 65 bytes
 * that are not this key's point. Refusing the width turns that into a failure at
 * the moment of the draw instead of a factor manifest naming a point nobody
 * holds.
 */
export const FACTOR_PRIVATE_KEY_BYTES = 138;

/** Width of an uncompressed P-256 point: the `0x04` prefix and two coordinates. */
export const FACTOR_PUBLIC_KEY_BYTES = 65;

/**
 * Where the factor's public key sits inside its PKCS#8 encoding.
 *
 * **A client lifts the factor's own public key from here and from nowhere
 * else** — not from anything stored, not from anything transmitted, and not from
 * the `publicKey` half of the handle the draw returned. The point goes into the
 * account's factor manifest and into a later rotation's HKDF info, so the
 * question it has to answer is "which point does *this* private key agree
 * under", and the private key's own encoding is the only place that answers it
 * without a second value to keep true.
 *
 * The number is pinned against the frozen PKCS#8 itself rather than against a
 * number somebody chose.
 */
export const FACTOR_PUBLIC_KEY_OFFSET = 73;

/**
 * Width of a wrapped private key: `version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)`
 * over a {@link FACTOR_PRIVATE_KEY_BYTES}-byte PKCS#8.
 *
 * Exactly, never at least. The column is fixed at this width, so a value of any
 * other width is refused here before it is refused there — and a reader that
 * repaired a width would be reading a value the peer's own decoder rejects.
 */
export const WRAPPED_PRIVATE_KEY_BYTES = 167;

/**
 * Width of an encapsulated account-keys value:
 * `version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext ‖ tag(16)`
 * over the 64-byte plaintext `contentKey ‖ indexKey`.
 *
 * Exactly, for {@link WRAPPED_PRIVATE_KEY_BYTES}' reason.
 */
export const ENCAPSULATED_ACCOUNT_KEYS_BYTES = 158;

/** One factor's keypair, as the two values cross the wire. */
export interface FactorKeypairEnvelopes {
  /** Unpadded base64url over {@link WRAPPED_PRIVATE_KEY_BYTES} bytes. */
  readonly wrappedPrivateKey: string;
  /** Unpadded base64url over {@link ENCAPSULATED_ACCOUNT_KEYS_BYTES} bytes. */
  readonly encapsulatedAccountKeys: string;
}

/** What {@link mintFactorKeypair} hands back: the two envelopes and the point. */
export interface MintedFactorKeypair extends FactorKeypairEnvelopes {
  /**
   * The factor's public key, uncompressed. It belongs in the account's factor
   * manifest; **there is deliberately no per-row column for it**, because what
   * has to be unforgeable is the *set*.
   */
  readonly publicKey: Uint8Array;
}

/** What {@link openFactorKeypair} hands back. */
export interface OpenedFactorKeypair extends AccountKeys {
  /** The factor's public key, lifted from the PKCS#8 this call just opened. */
  readonly publicKey: Uint8Array;
}

// The field of the private-key message that the encapsulation message does not
// have. It is the only difference between the two, which is why it is written
// once rather than at the one call site: the messages are a prefix pair, so a
// typo here does not produce a message nothing understands, it produces a
// message some other reader of this scheme will happily accept.
const PRIVATE_KEY_PURPOSE = 'private-key';

// The encapsulation suite's own version byte, and **not** an alias of
// `ENVELOPE_VERSION` or of `FACTOR_KEYPAIR_VERSION`. Both framings lead with
// `0x01` on different suites and nothing in the bytes says which, so the column a
// value came out of is the only discriminator there is.
const ENCAPSULATION_VERSION = 1;

// The encapsulation suite's nonce and tag widths. Written here rather than
// imported from `key-envelope.ts` for the reason the version byte is: this is a
// second framing, and borrowing the AEAD suite's constants would mean a v2 of
// *that* suite silently relaying a new nonce width into *this* layout. Twelve and
// sixteen are the same numbers for the same reasons — twelve is the one nonce
// width GCM uses directly as the counter block's prefix, sixteen is the full tag —
// and that argument is made in full at `key-envelope.ts`.
const ENCAPSULATION_NONCE_BYTES = 12;
const ENCAPSULATION_TAG_BYTES = 16;

const BITS_PER_BYTE = 8;

// WebCrypto counts the tag in bits and this layout counts in bytes.
const ENCAPSULATION_TAG_BITS = ENCAPSULATION_TAG_BYTES * BITS_PER_BYTE;

// The layout, as arithmetic rather than as three numbers that agree until one
// moves. The version occupies one byte by definition of the framing above.
const ENCAPSULATION_VERSION_BYTES = 1;
const EPHEMERAL_POINT_OFFSET = ENCAPSULATION_VERSION_BYTES;
const ENCAPSULATION_NONCE_OFFSET =
  EPHEMERAL_POINT_OFFSET + FACTOR_PUBLIC_KEY_BYTES;
const ENCAPSULATION_SEALED_OFFSET =
  ENCAPSULATION_NONCE_OFFSET + ENCAPSULATION_NONCE_BYTES;

// One plaintext, two keys, **content key first and no separator between them**.
// The halves are told apart by position alone, so a reversed pair is the right
// width, the right version, it stores, it reads back and it opens. Nothing on the
// server can see it and no round trip can see it; the only witness this scheme
// has is a known answer whose two keys differ, asserted in both positions.
const ACCOUNT_KEYS_PLAINTEXT_BYTES = 2 * ACCOUNT_KEY_BYTES;
const CONTENT_KEY_OFFSET = 0;
const INDEX_KEY_OFFSET = ACCOUNT_KEY_BYTES;

// P-256's field width, which is what a raw ECDH agreement is. It equals
// `ACCOUNT_KEY_BYTES` today and is not the same decision — one is a property of
// the curve, the other is how strong this account's envelopes are — so it gets
// its own name rather than borrowing that one.
const RAW_AGREEMENT_BYTES = 32;

const UNCOMPRESSED_POINT_PREFIX = 0x04;

const ECDH_P256 = { name: 'ECDH', namedCurve: 'P-256' } as const;

const AEAD = 'AES-GCM';

const utf8 = new TextEncoder();

// The private half and the point that belongs to it, as one value. They are
// handed back together because the second is only trustworthy as a consequence
// of the first — see `importFactorPrivateKey`.
interface FactorPrivateHalf {
  readonly privateKey: CryptoKey;
  readonly publicKey: Uint8Array;
}

// Everything the encapsulation key is derived from. An object rather than five
// positional parameters because two of them are 65-byte arrays that differ only
// in meaning, and at a call site that is one transposition away from an
// implementation that agrees with itself and with nothing else.
interface EncapsulationAgreement {
  readonly factorId: string;
  /** Raw, uncompressed. First of the two points in the info. */
  readonly ephemeralPublicKey: Uint8Array;
  /** Raw, uncompressed. Second of the two points in the info. */
  readonly factorPublicKey: Uint8Array;
  /** Whichever private half this side holds. */
  readonly privateKey: CryptoKey;
  /** The other half, raw — always one of the two points above. */
  readonly peerPublicKey: Uint8Array;
}

/**
 * Draws a factor keypair, wraps its private half under `keyEncryptionKey`, and
 * encapsulates the account's two keys to its public half.
 *
 * The two envelopes come back as unpadded base64url — the wire form
 * `decodeBase64Url` and the server's own decoder both accept — beside the public
 * point, which the caller owes to the account's factor manifest.
 *
 * **The value is opened again before it is returned.** A fault in the
 * encapsulation — a corrupted ciphertext, a derivation that disagrees with
 * itself — is otherwise discovered by the person who needs this factor, which is
 * by definition the moment they have lost the others. So the whole read path is
 * run against what was just written and the result is compared to what went in;
 * the cost is one extra agreement per factor and the alternative is a factor that
 * looks enrolled and opens nothing.
 *
 * The account's keys are the *account's*. A caller draws them once and mints one
 * of these per factor; drawing a fresh pair here would pass every round trip and
 * lose the account's whole history the first time another factor is used.
 */
export async function mintFactorKeypair(
  keyEncryptionKey: CryptoKey,
  factorId: string,
  keys: AccountKeys,
): Promise<MintedFactorKeypair> {
  requireAccountKeyPair(keys);

  // The PKCS#8 of a freshly drawn private key, and the only window in which this
  // module ever holds one in the clear. `drawFactorPkcs8` owns the extractable
  // handle and does not return it, so from this line on there is no way back to
  // an exportable key object.
  const pkcs8 = await drawFactorPkcs8();

  let wrappedPrivateKey: string;
  let factor: FactorPrivateHalf;

  try {
    // Sealed *before* the import, because `importFactorPrivateKey` ends these
    // bytes — it is the door, and a door's last act is to wipe what it was
    // handed. The order is the only coupling between the two steps.
    wrappedPrivateKey = encodeBase64Url(
      await sealEnvelope(
        keyEncryptionKey,
        pkcs8,
        wrappedPrivateKeyAssociatedData(factorId),
      ),
    );

    factor = await importFactorPrivateKey(pkcs8);
  } finally {
    // The door's own wipe covers the door; this one covers the window between
    // the export above and the call to it, where a rejection out of `seal` would
    // otherwise leave a private key on the heap for the life of the tab. Wiping
    // twice costs nothing; the path that skips a wipe is the path where
    // something already went wrong.
    pkcs8.fill(0);
  }

  const encapsulatedAccountKeys = await encapsulateAccountKeys(
    factorId,
    keys,
    factor,
  );
  const envelopes: FactorKeypairEnvelopes = {
    wrappedPrivateKey,
    encapsulatedAccountKeys,
  };

  await requireItOpens(keyEncryptionKey, factorId, envelopes, keys);

  return { ...envelopes, publicKey: factor.publicKey };
}

/**
 * Opens a factor's pair under `keyEncryptionKey`, or rejects.
 *
 * Hands back the account's two keys and the factor's public point. Rejects on a
 * wire string that is not unpadded base64url, on either value at any width but
 * its own, on an encapsulated value leading with a version this code does not
 * know, on an ephemeral point in any encoding but the uncompressed one, on a
 * point that is not on the curve, and on either envelope presented under a factor
 * id other than the one it was bound to. None of those yields bytes and none of
 * them is distinguishable from corruption, which is the point of binding.
 *
 * `async` is load-bearing: the refusals below are `throw`s, and a synchronous
 * throw from a function whose signature promises a `Promise` escapes past every
 * caller's `catch` on the result.
 */
export async function openFactorKeypair(
  keyEncryptionKey: CryptoKey,
  factorId: string,
  envelopes: FactorKeypairEnvelopes,
): Promise<OpenedFactorKeypair> {
  // The strict decoder, which refuses padding, the standard alphabet's `+` and
  // `/`, and a final group no encoder would emit. A lenient reading here would
  // accept a value the server's own decoder rejects, months apart from the
  // request that produced it.
  const wrapped = decodeBase64Url(envelopes.wrappedPrivateKey);
  const encapsulated = decodeBase64Url(envelopes.encapsulatedAccountKeys);

  requireWidth(wrapped, WRAPPED_PRIVATE_KEY_BYTES, 'A wrapped private key');
  requireWidth(
    encapsulated,
    ENCAPSULATED_ACCOUNT_KEYS_BYTES,
    'An encapsulated account-keys value',
  );

  // A successor does not exist, so bytes claiming one cannot be read by any code
  // that does. Refusing now is what lets a second version exist later — and this
  // byte is outside the authenticated data, so it has to be checked on purpose.
  if (encapsulated[0] !== ENCAPSULATION_VERSION) {
    throw new Error(
      'Not an encapsulated value: the input leads with an unknown version.',
    );
  }

  const factor = await importFactorPrivateKey(
    await openEnvelope(
      keyEncryptionKey,
      wrapped,
      wrappedPrivateKeyAssociatedData(factorId),
    ),
  );

  const ephemeralPublicKey = overOwnBuffer(
    encapsulated.subarray(EPHEMERAL_POINT_OFFSET, ENCAPSULATION_NONCE_OFFSET),
  );

  const key = await encapsulationKey({
    factorId,
    ephemeralPublicKey,
    factorPublicKey: factor.publicKey,
    privateKey: factor.privateKey,
    peerPublicKey: ephemeralPublicKey,
  });

  const plaintext = await openEncapsulated(
    key,
    encapsulated,
    encapsulatedAccountKeysAssociatedData(factorId),
  );

  try {
    requireWidth(
      plaintext,
      ACCOUNT_KEYS_PLAINTEXT_BYTES,
      "An encapsulated value's plaintext",
    );

    // Copies rather than views, for `generateAccountKeys`' reason: two keys that
    // are windows onto one buffer are one secret a caller can wipe by halves,
    // and the `finally` below would take both of them with it.
    return {
      contentKey: Uint8Array.from(
        plaintext.subarray(
          CONTENT_KEY_OFFSET,
          CONTENT_KEY_OFFSET + ACCOUNT_KEY_BYTES,
        ),
      ),
      indexKey: Uint8Array.from(
        plaintext.subarray(
          INDEX_KEY_OFFSET,
          INDEX_KEY_OFFSET + ACCOUNT_KEY_BYTES,
        ),
      ),
      publicKey: factor.publicKey,
    };
  } finally {
    plaintext.fill(0);
  }
}

/**
 * Encapsulates the account's two keys to one factor's public key, or rejects.
 *
 * **This is the half of a mint that a rotation performs on its own.** A
 * rotation carries the *next* generation of the account's two keys to every
 * factor the account has, and all it holds of each of them is the public key
 * the account's factor manifest names — no private half, no key-encryption key,
 * and no authenticator in the room. {@link mintFactorKeypair} cannot answer for
 * it, because that ceremony *draws* the keypair it encapsulates to, which is
 * exactly the act a rotation must not perform on a factor that already exists.
 * The value this returns is meant to be paired with the wrapped private key
 * already stored for `factorId`, and the two open together although they were
 * never written together.
 *
 * The answer is unpadded base64url over {@link ENCAPSULATED_ACCOUNT_KEYS_BYTES}
 * bytes, the form the column takes and the server's own decoder accepts.
 *
 * **Exactly two things have to agree for it to open, and neither is checked
 * here because neither can be**: `factorId`, which binds both the associated
 * data and the HKDF info, and `factorPublicKey`, which is the point the
 * recipient's private half agrees under. This side holds nothing of the factor
 * but its point, so a point belonging to somebody else produces a value of
 * exactly the right width that nobody can ever open. What keeps a set of these
 * honest is the manifest the points were read out of — authenticated under a
 * key the server does not hold — and not anything available in this frame.
 *
 * **What it does not do is re-open what it wrote, and that is not an
 * omission.** {@link mintFactorKeypair} does (FR-134) because it is holding the
 * private half it has just drawn; there is no private half here to read a value
 * back with, for any factor, ever. The check stays where the material for it
 * exists rather than moving to where it would have to be faked.
 *
 * `async` is load-bearing for {@link openFactorKeypair}'s reason: the width
 * refusal below is a `throw`, and a synchronous throw from a function whose
 * signature promises a `Promise` escapes past every caller's `catch` on the
 * result.
 */
export async function encapsulateAccountKeysTo(
  factorId: string,
  keys: AccountKeys,
  factorPublicKey: Uint8Array,
): Promise<string> {
  // The mint refuses these widths too, one draw earlier. Refusing again is not
  // a second opinion: this is an entry point in its own right, and the value a
  // short key produces is the most expensive kind of wrong — the plaintext is
  // one fixed-width buffer split by position, so a short content key pads
  // itself out with zeroes and the result stores, reads back and opens.
  requireAccountKeyPair(keys);

  // The ephemeral pair is drawn **non-extractable**: its public half is
  // exportable regardless (the platform makes every generated public key so,
  // measured), and its private half is needed for exactly one agreement and
  // must survive nothing. It is also drawn *per call* — a caller that reused one
  // ephemeral pair, or one nonce, across the factors of one rotation passes
  // every round trip and hands two factors the same AES-GCM `(key, nonce)`
  // pair, which surrenders the plaintext of both and the authentication subkey
  // with it. Which is why it is drawn here rather than taken as an argument:
  // there is no way for a caller to supply one.
  const ephemeral = await crypto.subtle.generateKey(ECDH_P256, false, [
    'deriveBits',
  ]);
  const ephemeralPublicKey = new Uint8Array(
    await crypto.subtle.exportKey('raw', ephemeral.publicKey),
  );

  const key = await encapsulationKey({
    factorId,
    ephemeralPublicKey,
    factorPublicKey,
    privateKey: ephemeral.privateKey,
    peerPublicKey: factorPublicKey,
  });

  // Content key first, no separator. Hoisted so it can be wiped: inline it would
  // be a third copy of both of the account's keys that nothing names, one per
  // factor, for the life of the tab.
  const plaintext = new Uint8Array(ACCOUNT_KEYS_PLAINTEXT_BYTES);
  plaintext.set(keys.contentKey, CONTENT_KEY_OFFSET);
  plaintext.set(keys.indexKey, INDEX_KEY_OFFSET);

  try {
    return encodeBase64Url(
      await sealEncapsulated(
        key,
        ephemeralPublicKey,
        plaintext,
        encapsulatedAccountKeysAssociatedData(factorId),
      ),
    );
  } finally {
    plaintext.fill(0);
  }
}

/**
 * Refuses a point in any encoding but the uncompressed 65-byte one.
 *
 * **IFR-019 and exactly IFR-019**: a length and a leading byte, synchronously,
 * and nothing else. It says nothing about whether the point is on the curve, is
 * the point at infinity, or belongs to anybody — `crypto.subtle.importKey`
 * answers that (IFR-023) and answers it properly, so a second opinion written
 * here in BigInt would be unvectored cryptography duplicating a platform check.
 *
 * **What it is for is the gap the platform leaves open in the other direction.**
 * The platform accepts a parity-consistent hybrid encoding and a compressed one;
 * both are on the curve and both are perfectly usable, and both are the wrong
 * width for a field of a message that has no length prefixes. A 33-byte point in
 * an HKDF info shifts every byte after it, and a hybrid one is 65 bytes that
 * *look* right — so an implementation that tolerated either would derive a key
 * its peer cannot reproduce, and would find out a rotation later.
 */
export function requireUncompressedPoint(point: Uint8Array): void {
  if (
    point.length !== FACTOR_PUBLIC_KEY_BYTES ||
    point[0] !== UNCOMPRESSED_POINT_PREFIX
  ) {
    throw new Error(
      `A public key must be an uncompressed point: ${FACTOR_PUBLIC_KEY_BYTES} bytes leading with 0x04, not ${point.length} bytes leading with 0x${(point[0] ?? 0).toString(16)}.`,
    );
  }
}

// Draws a factor keypair and hands back its PKCS#8, and nothing else.
//
// **The extractable handle lives and dies inside this frame.** The draw has to be
// extractable — `generateKey(..., extractable: false)` cannot be exported at all,
// and an EC private key has no `raw` export, so PKCS#8 is the only way to get
// bytes a caller can wrap — and the narrowest window the platform allows for that
// is one function that returns bytes rather than a key. There is no field, no
// module variable and no return value anywhere in this file that a `CryptoKey`
// with `extractable: true` can reach.
//
// `deriveBits` and nothing else, on both halves. The pair never signs, never
// seals and never wraps.
async function drawFactorPkcs8(): Promise<Uint8Array<ArrayBuffer>> {
  const drawn = await crypto.subtle.generateKey(ECDH_P256, true, [
    'deriveBits',
  ]);

  return new Uint8Array(
    await crypto.subtle.exportKey('pkcs8', drawn.privateKey),
  );
}

// Turns PKCS#8 bytes into a non-extractable private half, lifts the public point
// out of them, wipes them, or rejects.
//
// **One door, two callers, and the order inside it is the reason it is a
// function at all.** A mint and an open both arrive here holding a PKCS#8 and
// both need the same three things done to it in the same sequence: import it,
// *then* lift the point, then end the bytes. Reversed, the lift reads 65 bytes
// out of a structure nothing has checked — and a PKCS#8 whose embedded point was
// spliced in from another key is refused by `importKey` with `DataError`
// (measured), so importing first is what makes the platform, rather than this
// module, the authority on the point it is about to read.
//
// The width is refused before either, because the offset only means anything
// inside the structure the constant describes.
//
// `deriveBits` and nothing else: this key agrees, and does not sign, seal or
// unwrap. `extractable: false` is the whole point of the re-import — from here on
// there is no API that reads the private half back out, so nothing holding the
// object can log it, serialise it into a request body or hand it to a crash
// reporter.
//
// The wipe covers the refusing path as thoroughly as the succeeding one, which
// is why the width check is inside the `try` rather than above it —
// `requireAccountKeyWidth`'s position argument in `account-keys.ts`, and it
// applies to a private key with more force than to a key-encryption key.
async function importFactorPrivateKey(
  pkcs8: Uint8Array,
): Promise<FactorPrivateHalf> {
  const owned = overOwnBuffer(pkcs8);

  try {
    requireWidth(owned, FACTOR_PRIVATE_KEY_BYTES, "A factor's private key");

    const privateKey = await crypto.subtle.importKey(
      'pkcs8',
      owned,
      ECDH_P256,
      false,
      ['deriveBits'],
    );

    const publicKey = Uint8Array.from(
      owned.subarray(
        FACTOR_PUBLIC_KEY_OFFSET,
        FACTOR_PUBLIC_KEY_OFFSET + FACTOR_PUBLIC_KEY_BYTES,
      ),
    );

    requireUncompressedPoint(publicKey);

    return { privateKey, publicKey };
  } finally {
    owned.fill(0);
    pkcs8.fill(0);
  }
}

// Derives the key an encapsulated value is sealed under:
// `HKDF-SHA-256(ECDH(privateKey, peerPublicKey), salt = ∅, info, 32)`.
//
// **The guard is called here, on both points, before either becomes a key object
// and before anything is derived.** That placement is the whole of IFR-019's
// enforcement in this module: `peerPublicKey` is by construction one of the two
// arrays checked immediately above — the ephemeral point when opening, the
// factor's point when encapsulating — so there is no route from a point to an
// agreement that does not pass through these two lines. A guard exported and
// never called is indistinguishable from a correct implementation in every round
// trip; only a call here tells them apart.
//
// **Both points go into the info raw and in this order — ephemeral first,
// recipient second.** Swapped, two matched implementations still agree with each
// other and with nothing else. Binding both is what makes a substituted factor
// public key derive a different key; no vector can show that, and the argument
// lives in `account-keys.md`.
async function encapsulationKey(
  agreement: EncapsulationAgreement,
): Promise<CryptoKey> {
  requireUncompressedPoint(agreement.ephemeralPublicKey);
  requireUncompressedPoint(agreement.factorPublicKey);

  // IFR-023, and the reason the guard above does no curve arithmetic: off the
  // curve and the point at infinity are refused right here, by the platform,
  // with no code of ours to be wrong about.
  //
  // **No usages at all.** A public key that agrees needs none — the usage list
  // belongs to the private half — and an empty one is the narrowest thing that
  // can be written.
  const peer = await crypto.subtle.importKey(
    'raw',
    overOwnBuffer(agreement.peerPublicKey),
    ECDH_P256,
    false,
    [],
  );

  const agreed = new Uint8Array(
    await crypto.subtle.deriveBits(
      { name: ECDH_P256.name, public: peer },
      agreement.privateKey,
      RAW_AGREEMENT_BYTES * BITS_PER_BYTE,
    ),
  );

  try {
    // The raw agreement is *not* a key and is never used as one: it is the x
    // coordinate of a point, it is not uniformly distributed, and it carries no
    // binding to anything. HKDF over it with the info below is what turns it into
    // one, and the info is what makes this key this factor's and no other's.
    //
    // **`hkdf.ts`'s, not written out here.** That module owns the hash, the
    // empty salt — a requirement rather than a simplification, and its argument
    // applies here unchanged — the expand loop and the width arithmetic, and
    // this derivation has no business holding a second opinion about any of
    // them. It takes the bytes spelling because this info is two raw 65-byte
    // points, which UTF-8 would widen to 129 bytes each.
    const derived = await hkdfSha256Over(
      agreed,
      encapsulationInfo(agreement),
      ACCOUNT_KEY_BYTES,
    );

    // The door, rather than a third `importKey` written beside its caller. It
    // holds the algorithm, the width, the usage list, the non-extractability and
    // the death of `derived`, and refuses any width but the account's.
    return await importAesGcmKey(derived);
  } finally {
    agreed.fill(0);
  }
}

// The mint's own spelling of {@link encapsulateAccountKeysTo}, over the private
// half it has just drawn.
//
// It exists so that the mint never names a point of its own: the one it
// encapsulates to is the one `importFactorPrivateKey` lifted out of this key's
// PKCS#8, carried here as the half it belongs to rather than as a loose array a
// call site could transpose. A rotation has no such companion — a public key is
// exactly the thing an absent authenticator leaves behind — which is why the
// exported one takes the point on its own.
function encapsulateAccountKeys(
  factorId: string,
  keys: AccountKeys,
  factor: FactorPrivateHalf,
): Promise<string> {
  return encapsulateAccountKeysTo(factorId, keys, factor.publicKey);
}

// `version ‖ ephemeral point ‖ nonce ‖ ciphertext ‖ tag`.
//
// The nonce is drawn from `crypto.getRandomValues` and from nowhere else, once
// per call: a predicted nonce under GCM is a chosen one, and two messages under
// one key and one nonce leak their XOR *and* give up the authentication subkey.
//
// WebCrypto returns the tag appended to the ciphertext — the same order this
// layout specifies — so there is no split to make here and no second append.
async function sealEncapsulated(
  key: CryptoKey,
  ephemeralPublicKey: Uint8Array,
  plaintext: Uint8Array,
  associatedData: Uint8Array,
): Promise<Uint8Array> {
  const nonce = new Uint8Array(ENCAPSULATION_NONCE_BYTES);
  crypto.getRandomValues(nonce);

  const sealed = await crypto.subtle.encrypt(
    {
      name: AEAD,
      iv: nonce,
      additionalData: overOwnBuffer(associatedData),
      tagLength: ENCAPSULATION_TAG_BITS,
    },
    key,
    overOwnBuffer(plaintext),
  );

  const value = new Uint8Array(ENCAPSULATION_SEALED_OFFSET + sealed.byteLength);
  value[0] = ENCAPSULATION_VERSION;
  value.set(ephemeralPublicKey, EPHEMERAL_POINT_OFFSET);
  value.set(nonce, ENCAPSULATION_NONCE_OFFSET);
  value.set(new Uint8Array(sealed), ENCAPSULATION_SEALED_OFFSET);

  return value;
}

// The inverse. The version byte and the ephemeral point are outside the
// authenticated data — the point is bound by the info the key was derived from,
// which is stronger than binding it here — and the caller has already refused a
// width and a version by the time this runs.
async function openEncapsulated(
  key: CryptoKey,
  value: Uint8Array,
  associatedData: Uint8Array,
): Promise<Uint8Array> {
  const opened = await crypto.subtle.decrypt(
    {
      name: AEAD,
      iv: overOwnBuffer(
        value.subarray(
          ENCAPSULATION_NONCE_OFFSET,
          ENCAPSULATION_NONCE_OFFSET + ENCAPSULATION_NONCE_BYTES,
        ),
      ),
      additionalData: overOwnBuffer(associatedData),
      tagLength: ENCAPSULATION_TAG_BITS,
    },
    key,
    overOwnBuffer(value.subarray(ENCAPSULATION_SEALED_OFFSET)),
  );

  return new Uint8Array(opened);
}

// FR-134: a value is opened before it is handed to anybody.
//
// **The fault this catches is not a bug in the arithmetic, it is a fault in the
// run** — a corrupted ciphertext, a derivation that came back wrong, an engine
// that agreed under a different point than the one it exported. All of them
// produce a perfectly well-formed pair of envelopes, of exactly the right widths,
// that never open. The whole read path is run here rather than a piece of it,
// because a check that shared a step with the writer would agree with the writer
// about that step.
//
// The re-opened copies are wiped in a `finally`: they are a second copy of both
// of the account's keys, made by this module and named by nothing else.
async function requireItOpens(
  keyEncryptionKey: CryptoKey,
  factorId: string,
  envelopes: FactorKeypairEnvelopes,
  keys: AccountKeys,
): Promise<void> {
  const opened = await openFactorKeypair(keyEncryptionKey, factorId, envelopes);

  try {
    if (
      !sameBytes(opened.contentKey, keys.contentKey) ||
      !sameBytes(opened.indexKey, keys.indexKey)
    ) {
      throw new Error(
        'A freshly minted factor keypair did not open into the keys it was given.',
      );
    }
  } finally {
    opened.contentKey.fill(0);
    opened.indexKey.fill(0);
  }
}

/**
 * `label ‖ 0x1F ‖ version ‖ 0x1F ‖ factorId ‖ 0x1F ‖ "private-key"`.
 *
 * The purpose field is the only thing separating this message from the one
 * below, and the one below is a strict prefix of it.
 */
function wrappedPrivateKeyAssociatedData(
  factorId: string,
): Uint8Array<ArrayBuffer> {
  return joinFields(
    utf8.encode(FACTOR_KEYPAIR_LABEL),
    grammarVersion(),
    utf8.encode(canonicalFactorId(factorId)),
    utf8.encode(PRIVATE_KEY_PURPOSE),
  );
}

/** `label ‖ 0x1F ‖ version ‖ 0x1F ‖ factorId`. */
function encapsulatedAccountKeysAssociatedData(
  factorId: string,
): Uint8Array<ArrayBuffer> {
  return joinFields(
    utf8.encode(FACTOR_KEYPAIR_LABEL),
    grammarVersion(),
    utf8.encode(canonicalFactorId(factorId)),
  );
}

/**
 * `label ‖ 0x1F ‖ version ‖ 0x1F ‖ factorId ‖ 0x1F ‖ ephemeral ‖ 0x1F ‖ factor`.
 *
 * Both points raw and never re-encoded, and their widths are what makes the
 * message unambiguous: `0x1F` occurs inside a point often enough that the
 * separators alone would not tell the last two fields apart. That is the second
 * reason {@link requireUncompressedPoint} runs before this is built.
 */
function encapsulationInfo(
  agreement: EncapsulationAgreement,
): Uint8Array<ArrayBuffer> {
  return joinFields(
    utf8.encode(FACTOR_KEYPAIR_LABEL),
    grammarVersion(),
    utf8.encode(canonicalFactorId(agreement.factorId)),
    agreement.ephemeralPublicKey,
    agreement.factorPublicKey,
  );
}

// The grammar's version as the one raw byte it is. A fresh array per call, for
// `buildAssociatedData`'s reason: a shared one is a buffer a caller could reach
// into and change under the next caller's message.
function grammarVersion(): Uint8Array {
  return Uint8Array.of(FACTOR_KEYPAIR_VERSION);
}

// Refuses anything but the one width, and names both numbers.
//
// Every width in this scheme is exact, never a floor: each of the two values
// lives in a fixed-width column, and every framing here is a fixed layout with no
// length prefix in it. A value of another width is not a shorter reading of the
// same thing, it is a different thing.
function requireWidth(bytes: Uint8Array, width: number, what: string): void {
  if (bytes.length !== width) {
    throw new Error(`${what} is exactly ${width} bytes, not ${bytes.length}.`);
  }
}

// Refuses account keys of any width but the account's.
//
// The plaintext is one 64-byte value split by position, so a short content key
// does not produce a short envelope — it shifts the index key into the content
// key's half and pads the rest, and the value stores, reads back and opens. The
// bound is `ACCOUNT_KEY_BYTES`, imported rather than restated, so there is one
// decision about how strong an account's material is.
function requireAccountKeyPair(keys: AccountKeys): void {
  requireWidth(keys.contentKey, ACCOUNT_KEY_BYTES, "The account's content key");
  requireWidth(keys.indexKey, ACCOUNT_KEY_BYTES, "The account's index key");
}

// Whether two byte strings are equal.
//
// **Not constant time, and it does not need to be.** Both sides here are values
// this module produced in this call — `requireItOpens` compares what it just
// wrote against what it was handed — so there is no attacker-supplied operand and
// nothing to learn from how long the comparison ran. A constant-time comparison
// against authenticated data is a different function for a different question,
// and writing one here would suggest this comparison guards something.
function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
  return (
    left.length === right.length && left.every((byte, at) => byte === right[at])
  );
}

// A copy onto a buffer WebCrypto will accept, and the copy is the point rather
// than an accident of it. `key-envelope.ts` makes the argument in full at its own
// copy: `BufferSource` excludes a view over a `SharedArrayBuffer`, a bare
// `Uint8Array` is a view over either, and narrowing by copying asserts nothing
// about the caller's buffer where a cast would. Two of the uses below are over a
// `subarray`, which needs the copy regardless.
function overOwnBuffer(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(bytes);
}
