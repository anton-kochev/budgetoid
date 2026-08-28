// One envelope for everything this client encrypts, and one version byte in
// front of it saying which one:
//
//   version (1) || nonce (12) || ciphertext || tag (16)
//
// Version `0x01` is AES-256-GCM with a 96-bit nonce and a 128-bit tag, and it is
// the only version there is. The wrapped keys of the current story and the
// narrative fields of a later one are the same bytes in the same layout, on
// purpose: two envelope formats would be two places for the nonce width, the tag
// width or the associated-data binding to drift apart, and every symptom of
// drift here is silent — bytes of exactly the right shape that decrypt to
// nothing on a device that did not seal them.
//
// **The associated data is the caller's and this module never builds it.** What
// an envelope belongs to — a row id, a field name, an account — is known one
// layer up, where the envelope is sealed and where it is found again; a helper
// here that assembled that value would put one binding in two places, and the
// half that drifted would still produce envelopes and still open the ones it
// wrote. So this module binds to whatever it is handed and owns nothing about
// what that is.
//
// The key is a `CryptoKey` rather than raw bytes so a key-encryption key can be
// imported non-extractable and stay that way. A signature taking `Uint8Array`
// would make the extractable import the only one possible, and the bytes of the
// account's key would then be reachable from any code holding the object.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it. The same
// argument `recovery-codes.ts` makes about a class holding a secret alive past
// the render that showed it applies with equal force to key material.

/**
 * The version byte every envelope this client writes leads with, and the only
 * one it will open.
 *
 * There is no version `0x02`. This constant is what makes one possible later:
 * see the refusal in {@link openEnvelope}.
 */
export const ENVELOPE_VERSION = 1;

/**
 * Nonce width, in bytes.
 *
 * Twelve is the one width GCM uses directly as the counter block's prefix
 * instead of hashing the nonce down to one, which is what makes a nonce drawn at
 * random per message safe. Another width is not a tuning knob: it changes the
 * layout, so it is a new {@link ENVELOPE_VERSION}, not an edit to this line.
 */
export const ENVELOPE_NONCE_BYTES = 12;

/**
 * Authentication tag width, in bytes.
 *
 * Sixteen is the full tag. A shorter one is a weaker forgery bound that nothing
 * downstream would notice — the envelope keeps its shape, every round trip still
 * succeeds, and only an attacker sees the difference.
 */
export const ENVELOPE_TAG_BYTES = 16;

// The version occupies one byte by definition of the layout above. It is the
// arithmetic the format is made of rather than a setting, which is why it is not
// exported: a reader who needs it can read the layout, and a writer who wants to
// change it is changing the format.
const VERSION_BYTES = 1;

// Where the nonce ends and the sealed bytes begin. Written once so the two
// functions below cannot disagree about it.
const SEALED_OFFSET = VERSION_BYTES + ENVELOPE_NONCE_BYTES;

// The shortest thing that can be an envelope: a version, a nonce and a tag, with
// no ciphertext between them. An empty plaintext is a legitimate value and seals
// to exactly this, so the refusal below is `<` and not `<=`.
const MINIMUM_ENVELOPE_BYTES = SEALED_OFFSET + ENVELOPE_TAG_BYTES;

const BITS_PER_BYTE = 8;

const ALGORITHM = 'AES-GCM';

// WebCrypto counts the tag in bits and this module counts the layout in bytes.
// The cipher is told the width the layout is built from rather than left on its
// default, so "the last sixteen bytes are the tag" is something this file states
// once and both halves of it read, instead of two numbers that agree until one
// of them moves.
const TAG_BITS = ENVELOPE_TAG_BYTES * BITS_PER_BYTE;

/**
 * Seals `plaintext` under `key`, bound to `associatedData`.
 *
 * The nonce is drawn from `crypto.getRandomValues` and from nowhere else, once
 * per call. `Math.random` would pass every shape-based check — twelve
 * well-formed bytes, different on every call — while being seeded from a value
 * the page does not control and short enough to walk; and a predicted nonce
 * under GCM is a chosen one. Two messages sealed under one key and one nonce
 * leak their XOR *and* give up the authentication subkey, which turns every tag
 * under that key into something an attacker can forge, so a repeated nonce is
 * not a degraded envelope but the end of the guarantee.
 *
 * `associatedData` is authenticated and **not** encrypted, and is not carried in
 * the envelope: {@link openEnvelope} is handed it again from where the envelope
 * was found. That is what makes an envelope moved to another row, another field
 * or another account stop opening.
 */
export async function sealEnvelope(
  key: CryptoKey,
  plaintext: Uint8Array,
  associatedData: Uint8Array,
): Promise<Uint8Array> {
  const nonce = new Uint8Array(ENVELOPE_NONCE_BYTES);
  crypto.getRandomValues(nonce);

  // Hoisted so it can be wiped. `overOwnBuffer` is a second copy of the caller's
  // secret, and inline it would be a copy nothing names: the caller's own
  // `finally` clears the array it owns while these thirty-two bytes stay on the
  // heap for the life of the tab, one per seal. Wiping it is the only reach
  // anything has to it.
  const ownedPlaintext = overOwnBuffer(plaintext);

  // `associatedData` gets a third copy per seal and is deliberately **not**
  // wiped. It is not secret — it names a factor and which of two keys a copy
  // holds, and is re-supplied from where the envelope was found in order to open
  // it — so there is nothing to clear. Wiping it for symmetry with the line above
  // would suggest it carried something it does not.
  let sealed: ArrayBuffer;

  try {
    // WebCrypto returns the tag appended to the ciphertext — the same order this
    // layout specifies — so `sealed` is already `ciphertext || tag` and there is
    // no split to make here. A reader will look for one; there isn't one, and
    // appending the tag a second time is the shape that mistake takes.
    sealed = await crypto.subtle.encrypt(
      {
        name: ALGORITHM,
        iv: nonce,
        additionalData: overOwnBuffer(associatedData),
        tagLength: TAG_BITS,
      },
      key,
      ownedPlaintext,
    );
  } finally {
    // The wipe runs after the cipher has resolved, and **the reason is not the
    // one this comment used to give.** It claimed WebCrypto reads the buffer
    // asynchronously, so a wipe placed ahead of the `await` would seal zeros.
    // **Measured: false.** `encrypt` copies the plaintext in its synchronous
    // prologue, so clearing the buffer the instant the call has returned its
    // promise — before anything is awaited — yields a ciphertext byte-identical
    // to clearing it here, and neither of them is the ciphertext of an all-zero
    // plaintext of the same width, which is the control that makes "identical"
    // mean anything. It is the same finding the two import doors in
    // `account-keys.ts` carry, about the same prologue.
    //
    // The ordering stays, for two smaller reasons worth saying in place of the
    // wrong one. A rejection surfaces inside this frame, so `sealEnvelope` is on
    // the stack trace; and an earlier wipe would rest the plaintext's fate on a
    // detail of the platform's prologue that nothing in this repository states
    // and no test can observe. So say it plainly: **nothing holds this line.**
    // Hoist the wipe above the `await` and the whole suite agrees with you — it
    // is a rule of construction rather than of observation. The wipe itself is
    // not the negotiable part of it.
    ownedPlaintext.fill(0);
  }

  // The version byte is deliberately outside the authenticated data. GCM has no
  // opinion about it, which is why {@link openEnvelope} has to check it on
  // purpose — and why a v2 reader can look at it before it knows the layout,
  // rather than having to guess a layout in order to authenticate the byte that
  // names one.
  const envelope = new Uint8Array(SEALED_OFFSET + sealed.byteLength);
  envelope[0] = ENVELOPE_VERSION;
  envelope.set(nonce, VERSION_BYTES);
  envelope.set(new Uint8Array(sealed), SEALED_OFFSET);

  return envelope;
}

/**
 * Opens `envelope` under `key` and `associatedData`, or rejects.
 *
 * Rejects — never returns a partial reading — on an input too short to be an
 * envelope, on a version byte this code does not know, on associated data other
 * than the data it was sealed under, and on a single flipped bit anywhere in the
 * nonce, the ciphertext or the tag. The last three are GCM's own refusal; the
 * first two are this function's, and are made before anything is attempted.
 *
 * `async` is load-bearing: the refusals below are `throw`s, and an `async`
 * function turns them into a rejected promise. A synchronous throw from a
 * function whose signature promises a `Promise` escapes past every caller's
 * `catch` on the result.
 */
export async function openEnvelope(
  key: CryptoKey,
  envelope: Uint8Array,
  associatedData: Uint8Array,
): Promise<Uint8Array> {
  // Length first, and not only for the message it produces. Without this check a
  // short input reaches `subtle.decrypt` with a nonce sliced out of whatever was
  // there — for some lengths a nonce of the wrong width and a clear error, for
  // others a plausible call that fails as though the data were corrupt. Neither
  // says the thing that is true: this is not an envelope. Checking it before the
  // version byte also keeps an empty input from being reported as a version
  // problem, which is what reading `envelope[0]` out of nothing would say.
  if (envelope.length < MINIMUM_ENVELOPE_BYTES) {
    throw new Error(
      'Not an envelope: the input is too short to hold a version, a nonce and a tag.',
    );
  }

  // A successor does not exist, so bytes claiming one cannot be interpreted by
  // any code that exists. The only two readings available are "refuse" and
  // "decode it as v1 anyway", and the second is how a future format silently
  // becomes unreadable: the day a v2 ships with a different nonce width, every
  // v1 reader that skipped this byte slices the nonce in the wrong place and
  // reports genuine data as corrupt — or worse, hands a v2 envelope back through
  // a v1 writer. Refusing now is what lets a second version exist later.
  if (envelope[0] !== ENVELOPE_VERSION) {
    throw new Error(
      'Not an envelope: the input leads with an unknown version.',
    );
  }

  // `ciphertext || tag` in one piece, for the same reason the seal does not
  // split them: that is the layout WebCrypto expects back.
  const nonce = overOwnBuffer(envelope.subarray(VERSION_BYTES, SEALED_OFFSET));
  const sealed = overOwnBuffer(envelope.subarray(SEALED_OFFSET));

  const opened = await crypto.subtle.decrypt(
    {
      name: ALGORITHM,
      iv: nonce,
      additionalData: overOwnBuffer(associatedData),
      tagLength: TAG_BITS,
    },
    key,
    sealed,
  );

  return new Uint8Array(opened);
}

// A copy onto a buffer WebCrypto will accept, and the copy is the point rather
// than an accident of it.
//
// `BufferSource` is `ArrayBufferView<ArrayBuffer> | ArrayBuffer`: a view over a
// `SharedArrayBuffer` is excluded, and a bare `Uint8Array` is a view over
// either. The parameters above are bare because callers hold bare ones — the
// alternative is a narrower signature that turns an ordinary caller into a cast
// at every call site, which is the same lie spread wider. So the narrowing
// happens once, here, by producing bytes whose buffer this module owns; an
// assertion would instead assert something about the caller's buffer that
// nothing checked.
//
// Two of the three uses need a copy regardless: `subarray` returns a view over
// the envelope's buffer, which carries the caller's buffer type with it.
function overOwnBuffer(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(bytes);
}
