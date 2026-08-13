// One envelope for everything this client encrypts, and one version byte in
// front of it saying which one:
//
//   version (1) || nonce (12) || ciphertext || tag (16)
//
// Version `0x01` is AES-256-GCM with a 96-bit nonce and a 128-bit tag, and it is
// the only version there is. The wrapped keys of the current story and the
// narrative fields of a later one are the same bytes in the same layout, which
// is the point: two envelope formats would be two places for the nonce width,
// the tag width or the associated-data binding to drift apart, and drift here is
// silent — bytes of exactly the right shape that decrypt to nothing on a device
// that did not seal them.
//
// The associated data is the caller's. This module binds an envelope to whatever
// it is handed and never builds that value itself, because what an envelope
// belongs to is known where it is sealed — a row id, a field name, an account —
// and not here. So the tests below hand it in and pin only that the binding
// holds, which is the property every use of it downstream rests on.
//
// The key is a `CryptoKey` rather than raw bytes so that a key-encryption key
// can be imported non-extractable and stay that way. A signature taking
// `Uint8Array` would make the extractable import the only one possible, and the
// bytes of the account's key would then be reachable from any code that got hold
// of the object.
import { describe, expect, it, vi } from 'vitest';

import {
  ENVELOPE_NONCE_BYTES,
  ENVELOPE_TAG_BYTES,
  ENVELOPE_VERSION,
  openEnvelope,
  sealEnvelope,
} from './key-envelope';

// The version occupies one byte by definition of the format above, so this is
// not a knob and is deliberately not imported from the module: it is the
// arithmetic the layout is made of, written here so a change to it has to be
// argued for rather than absorbed.
const VERSION_BYTES = 1;

const utf8 = new TextEncoder();

// Ordinary inputs for everything that is not the frozen vector. Fixed rather
// than random so a failure is reproducible, and structured rather than
// zero-filled so a byte that ends up in the wrong region is visible in the hex.
const TEST_KEY_BYTES = Uint8Array.from({ length: 32 }, (value, index) => index);
const TEST_PLAINTEXT = Uint8Array.from(
  { length: 32 },
  (value, index) => 0x80 + index,
);
const TEST_ASSOCIATED_DATA = utf8.encode('budgetoid/spec/associated-data/v1');

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

function fromHex(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(text.match(/../g) ?? [], (pair) => parseInt(pair, 16));
}

// Not `async`, so the promise is returned rather than awaited and re-wrapped.
// `extractable: false` is the shape production uses and the shape the signature
// exists for; a test that imported an extractable key would quietly stop
// exercising the reason the parameter is a `CryptoKey` at all.
//
// The buffer type is spelled out because `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either.
function importAesKey(bytes: Uint8Array<ArrayBuffer>): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

function nonceRegion(envelope: Uint8Array): Uint8Array {
  return envelope.subarray(VERSION_BYTES, VERSION_BYTES + ENVELOPE_NONCE_BYTES);
}

function tagRegionStart(envelope: Uint8Array): number {
  return envelope.length - ENVELOPE_TAG_BYTES;
}

// A copy with one bit of one byte inverted. A single bit rather than a whole
// byte on purpose: a tag check that compares only some of the bytes, or an
// implementation that truncates the tag, survives a coarse change more easily
// than a fine one.
function withBitFlipped(envelope: Uint8Array, index: number): Uint8Array {
  const tampered = Uint8Array.from(envelope);
  tampered[index] ^= 0x01;

  return tampered;
}

describe('a sealed envelope', () => {
  it('lays out a version byte, a nonce, the ciphertext and a tag', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);

    // Act
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );

    // Assert
    // The expected width is derived from the shipped constants and the
    // plaintext, not restated, so the assertion cannot be left behind by a
    // change to either — and then the total is pinned too, because the
    // derivation alone would still hold if the nonce grew by a byte and the tag
    // lost one.
    const expectedLength =
      VERSION_BYTES +
      ENVELOPE_NONCE_BYTES +
      TEST_PLAINTEXT.length +
      ENVELOPE_TAG_BYTES;

    expect(envelope).toHaveLength(expectedLength);
    expect(expectedLength).toBe(61);

    // And each region on its own, for the same reason. Twelve bytes is the one
    // nonce width GCM uses directly instead of hashing down to a counter block,
    // which is what makes a random nonce safe to draw per message; sixteen is
    // the full tag, and a shorter one is a weaker forgery bound that nothing
    // downstream would notice.
    expect(ENVELOPE_NONCE_BYTES).toBe(12);
    expect(ENVELOPE_TAG_BYTES).toBe(16);
  });

  it('leads with the version byte, before anything a reader must parse', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);

    // Act
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );

    // Assert
    // First byte, not a trailer and not a field somewhere inside: a reader has
    // to know what it is holding before it can decide where the nonce ends, and
    // a version it can only find by first assuming a layout is not a version.
    expect(envelope[0]).toBe(ENVELOPE_VERSION);
    expect(ENVELOPE_VERSION).toBe(1);
  });

  it('opens back to exactly the bytes that were sealed', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);

    // Act
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );
    const opened = await openEnvelope(key, envelope, TEST_ASSOCIATED_DATA);

    // Assert
    // The round trip is the whole of the module's usefulness, and it is also the
    // one thing that an off-by-one in any of the three region offsets breaks
    // loudly. Compared as hex so a failure names the byte rather than printing
    // two arrays.
    expect(toHex(opened)).toBe(toHex(TEST_PLAINTEXT));
  });

  it('draws a fresh nonce for each seal, so two seals never share one', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);

    // Act
    const first = await sealEnvelope(key, TEST_PLAINTEXT, TEST_ASSOCIATED_DATA);
    const second = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );

    // Assert
    // The nonce region specifically, and that is the whole assertion. "The two
    // envelopes differ" passes on an implementation that reuses one nonce and
    // varies something else, and nonce reuse under one key is the failure GCM
    // does not survive: two messages under the same key and nonce leak their
    // XOR, and the pair also gives up the authentication subkey, which turns
    // every tag under that key into something an attacker can forge. A fixed
    // nonce is the natural shape of the bug — a constant next to the algorithm
    // name, no error anywhere, ciphertext that round-trips perfectly.
    expect(toHex(nonceRegion(second))).not.toBe(toHex(nonceRegion(first)));
    expect(toHex(second)).not.toBe(toHex(first));
  });

  it('takes its randomness from crypto.getRandomValues and never from Math.random', async () => {
    // Arrange
    // `Math.random` passes every other assertion in this file: it is fast, it
    // produces twelve well-formed bytes, and the nonces it draws differ from
    // each other. It is also seeded from a value the page does not control,
    // shared with every other caller, and short enough to walk — so the nonces
    // are predictable, and a predicted nonce is a chosen one. Nothing but this
    // assertion separates the two.
    const insecure = vi.spyOn(Math, 'random');
    const secure = vi.spyOn(crypto, 'getRandomValues');
    const key = await importAesKey(TEST_KEY_BYTES);

    // Act
    await sealEnvelope(key, TEST_PLAINTEXT, TEST_ASSOCIATED_DATA);

    // Assert
    expect(insecure).not.toHaveBeenCalled();
    expect(secure).toHaveBeenCalledTimes(1);

    const [requested] = secure.mock.calls[0];
    expect(requested).toBeInstanceOf(Uint8Array);
    expect((requested as Uint8Array).length).toBe(ENVELOPE_NONCE_BYTES);

    insecure.mockRestore();
    secure.mockRestore();
  });
});

describe('opening an envelope that is not the one that was sealed', () => {
  it('refuses an envelope with a single bit flipped anywhere in the tag', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );

    // Act, Assert
    // Every byte of the tag, not one representative byte: a comparison that
    // stops at the first eight, or one that only ever looks at the last, is
    // otherwise indistinguishable from a full one here — and both leave a
    // forgery bound far below the 128 bits the width promises.
    for (
      let index = tagRegionStart(envelope);
      index < envelope.length;
      index += 1
    ) {
      const tampered = withBitFlipped(envelope, index);

      await expect(
        openEnvelope(key, tampered, TEST_ASSOCIATED_DATA),
      ).rejects.toThrow();
    }
  });

  it('refuses an envelope with a single bit flipped anywhere in the ciphertext', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );

    // Act, Assert
    // GCM is a stream cipher underneath, so a flipped ciphertext bit is a
    // flipped plaintext bit and nothing about the output's shape gives it away:
    // a wrapped key stays 32 bytes and stays a perfectly usable AES key, and the
    // only symptom is data that will not decrypt later, on a different request,
    // with no way back. The tag is what makes this a refusal instead, and an
    // implementation that decrypted without verifying one would pass every test
    // above this line.
    const ciphertextStart = VERSION_BYTES + ENVELOPE_NONCE_BYTES;

    for (
      let index = ciphertextStart;
      index < tagRegionStart(envelope);
      index += 1
    ) {
      const tampered = withBitFlipped(envelope, index);

      await expect(
        openEnvelope(key, tampered, TEST_ASSOCIATED_DATA),
      ).rejects.toThrow();
    }
  });

  it('refuses associated data other than the data it was sealed under', async () => {
    // Arrange
    // The property every binding in the system rests on. Associated data is not
    // encrypted and is not carried in the envelope — it is re-supplied at open
    // time by the caller, from where the envelope was found — so what it buys is
    // that an envelope moved to another row, another field or another account
    // stops opening. An implementation that dropped the parameter, or passed an
    // empty buffer, would still round-trip, still refuse tampering, and still
    // reproduce nothing but this test's failure: every envelope would open
    // anywhere, and a swapped row would be readable rather than detectable.
    const key = await importAesKey(TEST_KEY_BYTES);
    const envelope = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );
    const elsewhere = utf8.encode('budgetoid/spec/associated-data/v2');

    // Act
    const opening = openEnvelope(key, envelope, elsewhere);

    // Assert
    await expect(opening).rejects.toThrow();
  });

  it('refuses an unrecognised version byte even over otherwise genuine bytes', async () => {
    // Arrange
    // The rest of the envelope is untouched and would decrypt, which is exactly
    // why this has to be refused deliberately. There is no version `0x02`: no
    // code that exists knows what bytes claiming one mean, so the only readings
    // available are "refuse" and "assume they are v1 anyway", and the second is
    // how a future format silently becomes unreadable. The day a v2 ships with a
    // different nonce width, every v1 reader that skipped this byte slices the
    // nonce in the wrong place and reports the data as corrupt — or worse, ships
    // a v2 envelope back through a v1 writer. A refusal now is what lets a
    // second version exist later.
    const key = await importAesKey(TEST_KEY_BYTES);
    const genuine = await sealEnvelope(
      key,
      TEST_PLAINTEXT,
      TEST_ASSOCIATED_DATA,
    );
    const unrecognised = Uint8Array.from(genuine);
    unrecognised[0] = ENVELOPE_VERSION + 1;

    // Act
    const opening = openEnvelope(key, unrecognised, TEST_ASSOCIATED_DATA);

    // Assert
    // Note what this does *not* rest on: the version byte is not part of the
    // authenticated data, so GCM has no opinion about it. Nothing catches this
    // except a check written on purpose.
    await expect(opening).rejects.toThrow();
  });

  it('refuses an envelope too short to hold a version, a nonce and a tag', async () => {
    // Arrange
    const key = await importAesKey(TEST_KEY_BYTES);
    const genuine = await sealEnvelope(
      key,
      new Uint8Array(0),
      TEST_ASSOCIATED_DATA,
    );
    const shortestPossible =
      VERSION_BYTES + ENVELOPE_NONCE_BYTES + ENVELOPE_TAG_BYTES;

    // Act, Assert
    // Every length below the minimum, taken as a prefix of a real envelope so
    // nothing but the truncation is wrong. Without a length check, a short input
    // reaches `subtle.decrypt` with a nonce sliced out of whatever was there —
    // which for some lengths is a nonce of the wrong width and a clear error,
    // and for others is a plausible call that fails as though the data were
    // corrupt. Neither says the thing that is true: the input is not an
    // envelope.
    expect(shortestPossible).toBe(29);

    for (let length = 0; length < shortestPossible; length += 1) {
      await expect(
        openEnvelope(key, genuine.slice(0, length), TEST_ASSOCIATED_DATA),
      ).rejects.toThrow();
    }

    // And the other side of the pin, without which "too short" means whatever
    // number the implementation picked. An empty plaintext is a legitimate
    // value, its envelope is exactly the minimum, and it opens.
    expect(genuine).toHaveLength(shortestPossible);
    expect(await openEnvelope(key, genuine, TEST_ASSOCIATED_DATA)).toHaveLength(
      0,
    );
  });
});

// The golden vector. Fixed key, fixed nonce, fixed associated data, fixed
// plaintext — one exact envelope.
//
// It was computed once with `node:crypto`'s webcrypto in a standalone script,
// not by running the module under test, so it is an independent answer rather
// than a photograph of current behaviour. What it buys is a target: the mobile
// client and any second implementation have to reproduce these 61 bytes, and
// this is the only artifact that says what "the same envelope format" means
// without pointing at code. What it catches is every silent divergence — a nonce
// that moved behind the ciphertext, a tag appended twice, a truncated tag, the
// associated data passed as UTF-16, a version byte folded into the authenticated
// data, AES-128 from a key silently truncated.
//
// If this goes red the question is never "what is the new value". It is which
// input changed, because each of those changes makes every envelope already
// written unreadable by the client that wrote it.
describe('the frozen envelope vector', () => {
  const GOLDEN_KEY =
    '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f';
  const GOLDEN_NONCE = 'a0a1a2a3a4a5a6a7a8a9aaab';
  const GOLDEN_PLAINTEXT =
    '808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f';
  const GOLDEN_ASSOCIATED_DATA = 'budgetoid/key-envelope/spec/v1';
  const GOLDEN_ENVELOPE =
    '01a0a1a2a3a4a5a6a7a8a9aaab6699feaec14e8438eaec0d588bf74e51e03dcb830622d4fb0497bc1de336eb9e21d4cc389d668944133ecec0a071274d';

  it('seals the frozen inputs to the frozen sixty-one bytes', async () => {
    // Arrange
    // The nonce is the one thing a caller cannot supply, so it is fed through
    // the same seam `recovery-codes.spec.ts` uses and nothing else is replaced:
    // the key import, the cipher and the encoder are all production's. A
    // substituted crypto implementation would make the vector a statement about
    // the substitute.
    const nonce = fromHex(GOLDEN_NONCE);
    const fixedNonce = vi
      .spyOn(crypto, 'getRandomValues')
      .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
        const bytes = new Uint8Array(
          (buffer as ArrayBufferView).buffer,
          (buffer as ArrayBufferView).byteOffset,
          (buffer as ArrayBufferView).byteLength,
        );
        bytes.set(nonce.subarray(0, bytes.length));

        return buffer;
      });
    const key = await importAesKey(fromHex(GOLDEN_KEY));

    // Act
    const envelope = await sealEnvelope(
      key,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_ASSOCIATED_DATA),
    );
    fixedNonce.mockRestore();

    // Assert
    expect(toHex(envelope)).toBe(GOLDEN_ENVELOPE);
    expect(envelope).toHaveLength(61);
  });

  it('opens the frozen envelope back to the frozen plaintext', async () => {
    // Arrange
    // The other direction over the same bytes, with no seal in front of it and
    // no spy anywhere. A pair of functions that agree only with each other
    // passes every round-trip test in this file; this one is the reader checked
    // against an answer it did not produce.
    const key = await importAesKey(fromHex(GOLDEN_KEY));

    // Act
    const opened = await openEnvelope(
      key,
      fromHex(GOLDEN_ENVELOPE),
      utf8.encode(GOLDEN_ASSOCIATED_DATA),
    );

    // Assert
    expect(toHex(opened)).toBe(GOLDEN_PLAINTEXT);
  });
});
