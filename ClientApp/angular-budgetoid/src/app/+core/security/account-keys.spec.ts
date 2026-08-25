// The account owns two keys — one for content, one for the blind index — and
// every recovery factor holds its own wrapped copy of **both**. A passkey is one
// factor; a set of recovery codes is another. Each derives its own
// key-encryption key, wraps the same two account keys under it, and stores the
// two envelopes beside itself. Adding a factor is therefore a second way *in*,
// never a second set of keys: two factors that produced two different content
// keys would give one account two keyspaces, and everything written under the
// one the client happened to unwrap first would be unreadable through the other
// — silently, and only after the first factor is gone.
//
// What binds a wrapped copy to where it lives is the associated data, and it
// names two things rather than one: the factor, and which of the two keys it is.
// Binding only the factor leaves the two envelopes under one factor
// interchangeable, so an operator swapping two columns hands the account a
// second index keyspace with no error anywhere. Binding neither is IFR-009's
// failure in full: a wrapped key copied to another account's row opens there.
//
// **The key-encryption keys are non-extractable by construction**, so no
// assertion below reads their bytes — there is no API that can, which is the
// whole reason the derivations return a `CryptoKey` instead of a `Uint8Array`.
// They are observed the only way left: something is sealed under them and the
// envelopes are compared. That is also the more useful pin, because the envelope
// is what a second implementation has to reproduce, and a key nobody can name is
// still a key two clients must agree on.
import { describe, expect, it, vi } from 'vitest';

import {
  ACCOUNT_KEY_BYTES,
  PASSKEY_KEY_ENCRYPTION_KEY_INFO,
  PASSKEY_PRF_EVAL_INPUT,
  WRAPPED_KEY_AAD_PREFIX,
  generateAccountKeys,
  importAesGcmKey,
  keyEncryptionKeyFromPasskey,
  keyEncryptionKeyFromRecoveryCode,
  unwrapAccountKeys,
  wrapAccountKeys,
  wrappedKeyAssociatedData,
} from './account-keys';
import * as accountKeysModule from './account-keys';
import { decodeBase64Url } from './base64url';
import { openEnvelope, sealEnvelope } from './key-envelope';
import { recoveryCodeVerifier } from './recovery-codes';

const utf8 = new TextEncoder();
const utf8Decoder = new TextDecoder();

// Two factor ids, both canonical. UUIDs rather than free-form labels, which is
// the shape the associated data below refuses to do without.
const PASSKEY_FACTOR_ID = 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6';
const RECOVERY_FACTOR_ID = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0';

// A code the generator could have minted: every character is in the alphabet,
// so it is already its own canonical form and the folds below have somewhere to
// fold *to*.
const PRINTED_CODE = '0123456789ABCDEFGHJKMNPQRS';

// The same code as somebody types it back — lower case off a phone keyboard,
// `o` for `0` and `l` for `1`, grouped with the hyphens it was written down in.
const TYPED_BACK_CODE = 'ol234-56789-abcde-fghjk-mnpqr-s';

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

function fromHex(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(text.match(/../g) ?? [], (pair) => parseInt(pair, 16));
}

// A copy onto a buffer WebCrypto will accept. `BufferSource` excludes a view
// over a `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either, so
// bytes arriving from a decoder are narrowed by being copied rather than by an
// assertion about a buffer nothing checked.
function overOwnBuffer(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(bytes);
}

// The opposite of `overOwnBuffer`: a view over the bytes a `BufferSource`
// already occupies, never a copy of them, so that reading it after the call
// under test returns reads whatever the module left in it.
function liveBytes(source: BufferSource): Uint8Array {
  return ArrayBuffer.isView(source)
    ? new Uint8Array(source.buffer, source.byteOffset, source.byteLength)
    : new Uint8Array(source);
}

// `extractable: false`, matching the shape the derivations under test are
// specified to produce. An extractable import here would quietly stop
// exercising the reason those functions return a `CryptoKey` at all.
function importAesKey(bytes: Uint8Array<ArrayBuffer>): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// The frozen inputs. Structured rather than zero-filled, so a byte that ends up
// in the wrong region is visible in the hex of a failure.
const GOLDEN_PRF_OUTPUT =
  '404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f';
const GOLDEN_NONCE = 'b0b1b2b3b4b5b6b7b8b9babb';
const GOLDEN_PLAINTEXT =
  '202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f';
const GOLDEN_SPEC_ASSOCIATED_DATA = 'budgetoid/account-keys/spec/v1';

const GOLDEN_NONCE_BYTES = fromHex(GOLDEN_NONCE);

// The recovery-code branch's frozen vector carries its own nonce, plaintext and
// associated data, so that a change to either branch reddens exactly one of the
// two and a reader can tell them apart at a glance in a failure's hex.
const GOLDEN_RECOVERY_NONCE = 'c0c1c2c3c4c5c6c7c8c9cacb';
const GOLDEN_RECOVERY_PLAINTEXT =
  '404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f';
const GOLDEN_RECOVERY_SPEC_ASSOCIATED_DATA = 'budgetoid/recovery-code/spec/v1';

const GOLDEN_RECOVERY_NONCE_BYTES = fromHex(GOLDEN_RECOVERY_NONCE);

// Seals under `key` with the frozen nonce and returns the envelope as hex.
//
// The nonce is the one input a caller cannot supply, so it is fed through the
// same seam `key-envelope.spec.ts` uses, and nothing else is replaced: the key
// import, the cipher and the encoder are all production's.
//
// **The mock is installed immediately before the seal and restored immediately
// after**, deliberately not for the length of a test. `generateAccountKeys`
// draws from the same function, and a mock left standing across it would serve
// both account keys out of the nonce's twelve bytes — two keys that are equal,
// short and constant, passing every envelope comparison below for the wrong
// reason.
async function sealedUnder(
  key: CryptoKey,
  plaintext: Uint8Array,
  associatedData: Uint8Array,
  nonce: Uint8Array = GOLDEN_NONCE_BYTES,
): Promise<string> {
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

  try {
    return toHex(await sealEnvelope(key, plaintext, associatedData));
  } finally {
    fixedNonce.mockRestore();
  }
}

describe('the account keys', () => {
  it('are two values of the width the account key length names', () => {
    // Arrange, Act
    const keys = generateAccountKeys();

    // Assert
    // Thirty-two bytes is AES-256, which is what `key-envelope.ts` seals with. A
    // key silently truncated to sixteen still imports, still seals and still
    // opens — AES-128 under an envelope that says nothing about its key width —
    // so the width has to be asserted here or nowhere.
    expect(ACCOUNT_KEY_BYTES).toBe(32);
    expect(keys.contentKey).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(keys.indexKey).toHaveLength(ACCOUNT_KEY_BYTES);
  });

  it('are two different keys, not one key handed back twice', () => {
    // Arrange, Act
    const keys = generateAccountKeys();

    // Assert
    // The content key encrypts what a person wrote; the index key keys the blind
    // index over it. One value in both places would make a search token
    // derivable from the content key and the reverse, which is the separation
    // the two-key design exists for.
    expect(toHex(keys.indexKey)).not.toBe(toHex(keys.contentKey));
  });

  it('are drawn independently, not one derived from the other', () => {
    // Arrange
    // The distinctness above is passed by `indexKey = hkdf(contentKey, …)`: two
    // well-formed 32-byte values that differ, out of a single 32-byte draw. And
    // it is the natural shape of the mistake — it looks tidier, it needs one
    // secret backed up instead of two, and every other assertion in this file
    // still passes. What it costs is the independence: the index key stops being
    // a second secret and becomes a function of the first, so anything that
    // recovers the content key recovers the search keyspace with it for free.
    //
    // So the draw itself is observed. The randomness is replaced by a counter
    // sweep, which makes the bytes handed out identifiable in the output: a key
    // that was drawn appears in what was drawn, and a key that was derived
    // cannot.
    let next = 0;
    const drawn: Uint8Array[] = [];
    const sweep = vi
      .spyOn(crypto, 'getRandomValues')
      .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
        const bytes = new Uint8Array(
          (buffer as ArrayBufferView).buffer,
          (buffer as ArrayBufferView).byteOffset,
          (buffer as ArrayBufferView).byteLength,
        );

        for (let index = 0; index < bytes.length; index += 1) {
          bytes[index] = next % 256;
          next += 1;
        }

        drawn.push(Uint8Array.from(bytes));

        return buffer;
      });

    // Act
    const keys = generateAccountKeys();
    sweep.mockRestore();

    // Assert
    // Sixty-four bytes' worth requested, however many calls it took — one draw
    // of 64 and two draws of 32 are both honest implementations, and a single
    // draw of 32 is the one this rules out.
    const requested = drawn.reduce((total, region) => total + region.length, 0);
    expect(requested).toBe(2 * ACCOUNT_KEY_BYTES);
    expect(requested).toBe(64);

    // And both keys are regions the generator was handed, rather than anything
    // computed from them.
    const handedOut = drawn.map(toHex).join('');
    expect(handedOut).toContain(toHex(keys.contentKey));
    expect(handedOut).toContain(toHex(keys.indexKey));
  });

  it('are copies, so mutating one never reaches the other', () => {
    // Arrange
    // A fresh pair per direction, so each assertion reads a key that nothing in
    // its own test has written to. Reusing one pair would leave the second
    // assertion comparing against a value the first mutation already moved, and
    // it would then pass on an implementation that aliases the two.
    const writingContent = generateAccountKeys();
    const indexBefore = toHex(writingContent.indexKey);

    const writingIndex = generateAccountKeys();
    const contentBefore = toHex(writingIndex.contentKey);

    // Act
    writingContent.contentKey[0] ^= 0xff;
    writingIndex.indexKey[0] ^= 0xff;

    // Assert
    // Both directions, because aliasing is not symmetric in the shapes that
    // produce it — `{ contentKey: draw, indexKey: draw.subarray(0, 32) }` is one
    // array and one view over its front, and only one of the two writes shows
    // it. A caller that wipes one key after wrapping it would otherwise wipe the
    // other, and the account would lose half its material with no error
    // anywhere.
    expect(toHex(writingContent.indexKey)).toBe(indexBefore);
    expect(toHex(writingIndex.contentKey)).toBe(contentBefore);
  });

  it('take their randomness from crypto.getRandomValues and never from Math.random', () => {
    // Arrange
    // `Math.random` passes every assertion above: 32 well-formed bytes twice
    // over, different from each other, drawn separately. It is also seeded from
    // a value the page does not control, shared with every other caller on the
    // page, and short enough to walk — so an account's content key becomes
    // reproducible by anybody who can observe a few of its outputs. Nothing but
    // this assertion separates the two.
    const insecure = vi.spyOn(Math, 'random');
    const secure = vi.spyOn(crypto, 'getRandomValues');

    // Act
    generateAccountKeys();

    // Assert
    expect(insecure).not.toHaveBeenCalled();
    expect(secure).toHaveBeenCalled();

    insecure.mockRestore();
    secure.mockRestore();
  });

  it('leave no live copy of themselves in the draw they were split out of', () => {
    // Arrange
    // The draw is one sixty-four-byte buffer the platform fills, and both
    // account keys are copied out of it and it is then let go. Nothing else
    // ever names it, so nothing else can ever wipe it: it stays on the heap
    // holding the account's content key *and* its index key in the clear for as
    // long as the tab lives, and a heap snapshot, a crash dump or a debugger
    // reads both out of one place.
    //
    // The irony is exact, and is why this is a test rather than a note. The
    // copy is made **for** hygiene — `Uint8Array.from(draw.subarray(…))` exists
    // so that a caller wiping one key does not wipe the other, and the test
    // above pins that — and it is precisely that copy which puts the originals
    // somewhere neither of the wipes that do run can reach. Registration's
    // `finally` clears `keys.contentKey` and `keys.indexKey` and believes the
    // account's keys are gone; the draw they came out of is untouched.
    //
    // The generator is replaced by a sweep that fills every byte non-zero, so
    // "the buffer is all zeros afterwards" cannot be satisfied by an
    // implementation that drew nothing.
    let next = 1;
    const live: Uint8Array[] = [];
    const snapshots: Uint8Array[] = [];
    const sweep = vi
      .spyOn(crypto, 'getRandomValues')
      .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
        const bytes = new Uint8Array(
          (buffer as ArrayBufferView).buffer,
          (buffer as ArrayBufferView).byteOffset,
          (buffer as ArrayBufferView).byteLength,
        );

        for (let index = 0; index < bytes.length; index += 1) {
          // `% 255` then `+ 1`, so no byte of the sweep is ever zero and the
          // whole draw is distinguishable from a wiped one byte by byte.
          bytes[index] = (next % 255) + 1;
          next += 1;
        }

        // The view is kept rather than copied: it *is* the draw, and reading it
        // after the call under test returns is the whole measurement. The
        // snapshot beside it is the copy, taken now, and is what says the
        // buffer held key material before the return rather than after it.
        live.push(bytes);
        snapshots.push(Uint8Array.from(bytes));

        return buffer;
      });

    // Act
    const keys = generateAccountKeys();
    sweep.mockRestore();

    // Assert
    // Every byte the platform handed back was non-zero, and the two keys are
    // exactly what was in there — so this is the draw the account's keys came
    // out of, not some unrelated scratch buffer that a `fill(0)` elsewhere
    // would satisfy by accident.
    const zeroBytesAtDraw = snapshots
      .flatMap((snapshot) => Array.from(snapshot))
      .filter((byte) => byte === 0);
    expect(zeroBytesAtDraw).toHaveLength(0);

    const drawn = snapshots.map(toHex).join('');
    expect(toHex(keys.contentKey) + toHex(keys.indexKey)).toBe(drawn);

    // And nothing of it is left behind. One `fill(0)` before the return buys
    // the only copy of both account keys that no `finally` anywhere else in
    // this client can reach.
    for (const region of live) {
      expect(toHex(region)).toBe('00'.repeat(region.length));
    }
  });
});

// The frozen vector for the passkey branch. A fixed PRF output in, one exact
// 61-byte envelope out, under a fixed plaintext, a fixed nonce and a fixed
// associated data.
//
// It was computed with `node:crypto`'s webcrypto in a standalone script, not by
// running the module under test, so it is an independent answer rather than a
// photograph of current behaviour. What it buys is a target: the mobile client
// has to derive the same key-encryption key from the same PRF output, and this
// is the only artifact that says what that means without pointing at code. What
// it catches is every silent divergence a non-extractable key otherwise hides —
// a changed `info` string, a salt that stopped being empty, a derivation width
// of 16 bytes instead of 32, the `info` encoded as UTF-16, HKDF replaced by a
// bare hash of the PRF output.
//
// If this goes red the question is never "what is the new value". It is which
// input changed, because each of those changes makes every account key already
// wrapped under a passkey permanently unopenable by the client that wrapped it.
describe('a key-encryption key', () => {
  const GOLDEN_PASSKEY_ENVELOPE =
    '01b0b1b2b3b4b5b6b7b8b9babbcc175362fa23e1b57690d974afd12fd44aa18baa1184ae370fa0fef7711ca912e1b77dc29b673f8035c130ca48b65505';

  // The same artifact for the other branch, and the branch that needs it most.
  // Every other assertion the recovery-code branch has is *relative* — different
  // from the passkey branch, different from the verifier, equal to itself across
  // two spellings of one code — and each of those compares the branch against
  // itself, so changing `RECOVERY_CODE_BRANCH_INFO.keyEncryptionKey` moves both
  // sides together and leaves them all green while handing every account a
  // different key-encryption key. The card already printed keeps redeeming
  // perfectly and unwraps nothing.
  //
  // It is also the branch nobody else can check. The server holds opaque bytes
  // and the mobile client has no target to reproduce, so this vector and the row
  // it copies in `docs/business-logic/account-keys.md` are the whole definition.
  // Computed independently through `node:crypto` and cross-checked by deriving
  // the verifier from the same code on the other branch, which came back as the
  // already-shipped `GOLDEN_VERIFIER` in `recovery-codes.spec.ts` — so the two
  // vectors sit on one input and prove the branches are separate rather than
  // merely different on two inputs.
  const GOLDEN_RECOVERY_CODE_ENVELOPE =
    '01c0c1c2c3c4c5c6c7c8c9cacbc989999fa07877001b181630c519f634e2508f0244b66f6a5f691a79c01bee8dd8bfbb2649838e3cf8266e86fa021bf1';

  it('seals the frozen plaintext to the frozen envelope on the passkey branch', async () => {
    // Arrange
    const prfOutput = fromHex(GOLDEN_PRF_OUTPUT);

    // Act
    const kek = await keyEncryptionKeyFromPasskey(prfOutput);
    const envelope = await sealedUnder(
      kek,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );

    // Assert
    // The associated data here is a spec-local string on purpose: this vector is
    // about the derivation, and the real associated data has a frozen vector of
    // its own below, so a change to either reddens exactly one test.
    expect(envelope).toBe(GOLDEN_PASSKEY_ENVELOPE);
    expect(envelope).toHaveLength(61 * 2);
  });

  it('seals the frozen plaintext to the frozen envelope on the recovery-code branch', async () => {
    // Arrange
    // The code is already its own canonical form, so this vector says nothing
    // about the folds — those have their own tests — and everything about the
    // derivation the canonical text feeds.
    const code = PRINTED_CODE;

    // Act
    const kek = await keyEncryptionKeyFromRecoveryCode(code);
    const envelope = await sealedUnder(
      kek,
      fromHex(GOLDEN_RECOVERY_PLAINTEXT),
      utf8.encode(GOLDEN_RECOVERY_SPEC_ASSOCIATED_DATA),
      GOLDEN_RECOVERY_NONCE_BYTES,
    );

    // Assert
    // If this goes red the question is never "what is the new value". It is
    // which input changed — the `info` string, the salt, the output width, the
    // encoding of the code — because each of those makes every account key
    // already wrapped under a recovery code permanently unopenable, while the
    // codes themselves go on redeeming.
    expect(envelope).toBe(GOLDEN_RECOVERY_CODE_ENVELOPE);
    expect(envelope).toHaveLength(61 * 2);
  });

  it('is a different key on the passkey branch than on the recovery-code branch', async () => {
    // Arrange
    // Byte-identical input keying material on both sides: the UTF-8 bytes of a
    // canonical code, handed to the passkey branch as a PRF output and to the
    // recovery-code branch as the code it is. Nothing but `info` differs.
    const material = utf8.encode(PRINTED_CODE);

    // Act
    const fromPasskey = await keyEncryptionKeyFromPasskey(material);
    const fromCode = await keyEncryptionKeyFromRecoveryCode(PRINTED_CODE);

    const passkeyEnvelope = await sealedUnder(
      fromPasskey,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );
    const codeEnvelope = await sealedUnder(
      fromCode,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );

    // Assert
    // The whole design rests on this separation. HKDF's expand step is a keyed
    // PRF over `info`, so two branches over the same material stay independent
    // exactly as long as their labels differ — and equal labels are a silent
    // failure, not a loud one: every wrap still works, every unwrap still works,
    // and one factor's key-encryption key becomes derivable from another
    // factor's input. Same nonce, same plaintext, same associated data, so the
    // only thing these two envelopes can disagree about is the key.
    expect(codeEnvelope).not.toBe(passkeyEnvelope);
  });

  it('reads a typed-back recovery code as the code that was printed', async () => {
    // Arrange
    // Lower case, `o` for `0`, `l` for `1`, and the grouping hyphens — the
    // default way a code comes back, not an edge case. The canonical form is
    // owned by `recovery-code-canonical.ts` and imported by both derivations off
    // a code; a key branch that folded differently by so much as a stripped
    // hyphen would redeem fine and unwrap nothing.
    expect(TYPED_BACK_CODE).not.toBe(PRINTED_CODE);

    // Act
    const fromPrinted = await keyEncryptionKeyFromRecoveryCode(PRINTED_CODE);
    const fromTyped = await keyEncryptionKeyFromRecoveryCode(TYPED_BACK_CODE);

    const printedEnvelope = await sealedUnder(
      fromPrinted,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );
    const typedEnvelope = await sealedUnder(
      fromTyped,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );

    // Assert
    expect(typedEnvelope).toBe(printedEnvelope);
  });

  it('is not the verifier the same recovery code derives', async () => {
    // Arrange
    // The two branches off one code: the verifier crosses the wire and the
    // key-encryption key never does. Equal `info` strings would fold them into
    // one derivation — the code would still redeem, the server would still
    // accept the set, and the value sitting in a request body would be the
    // account's key-encryption key. `recovery-codes.spec.ts` pins the two labels
    // distinct; this pins that the derivations they label have not collapsed.
    const verifier = await recoveryCodeVerifier(PRINTED_CODE);
    const asKey = await importAesKey(overOwnBuffer(decodeBase64Url(verifier)));

    // Act
    const kek = await keyEncryptionKeyFromRecoveryCode(PRINTED_CODE);

    const underVerifier = await sealedUnder(
      asKey,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );
    const underKek = await sealedUnder(
      kek,
      fromHex(GOLDEN_PLAINTEXT),
      utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA),
    );

    // Assert
    expect(underKek).not.toBe(underVerifier);
  });

  it('leaves no live copy of the material it was imported from', async () => {
    // Arrange
    // Read this against the claim at the top of this file and at the top of the
    // module. The module's claim is about the boundary and says only that:
    // "what leaves this module is a non-extractable `CryptoKey`, never bytes".
    // That is true of the object handed back, and the header does not pretend
    // it is true of anything else — it states that inside the file the material
    // is bytes twice per derivation, and that both copies are zero-filled where
    // they are consumed. This test holds up one of those two wipes.
    // `importAesGcmKey` copies the derived material with
    // `Uint8Array.from(material)` and hands the copy to WebCrypto, and until
    // its `finally` runs that copy is the account's key-encryption key in the
    // clear, on a buffer no name in the program refers to once `importKey` has
    // been called. That copy is the one the spy below reaches, and the only one
    // it can: the hkdf output is wiped in the same `finally` and no assertion
    // here sees it. No caller could observe either wipe being dropped — there
    // is no API that reads such a key back out — which is why the buffer is
    // reached through `importKey` rather than through anything the module hands
    // back.
    //
    // Both derivations share this one import, so what is measured here is the
    // recovery-code branch's wipe as much as the passkey branch's — a
    // registration derives eleven of these, one per factor, so a dropped
    // `fill(0)` would leave eleven thirty-two-byte copies of the values that
    // unwrap the account behind.
    //
    // The bytes are read twice, once at the import and once after the
    // derivation resolves. The first reading deliberately does **not** pin
    // them: it asserts a width and that they were not already all zero, which
    // is what makes a green result impossible for an implementation that
    // imported an empty buffer, while leaving the file's rule that no
    // assertion here names a key-encryption key's value intact.
    const prfOutput = fromHex(GOLDEN_PRF_OUTPUT);
    const realImportKey = crypto.subtle.importKey;

    // Empty rather than optional, so the width assertions below double as the
    // proof that the spy saw the import at all and no narrowing is needed.
    let importedMaterial: Uint8Array = new Uint8Array(0);
    let atImportTime: Uint8Array = new Uint8Array(0);
    let aesImports = 0;

    const importer = vi
      .spyOn(crypto.subtle, 'importKey')
      .mockImplementation(
        (format, keyData, algorithm, extractable, keyUsages) => {
          // `hkdfSha256` imports its input keying material through this same
          // function under `'HKDF'`, so the branch is on the algorithm rather
          // than on a call index: an extra import added anywhere below would
          // otherwise silently move which call is measured.
          if (algorithm === 'AES-GCM') {
            aesImports += 1;
            importedMaterial = liveBytes(keyData);
            atImportTime = Uint8Array.from(importedMaterial);
          }

          // Called through rather than faked: the derivation under measurement
          // has to be the real one, or the buffer being read is one no import
          // ever consumed.
          return realImportKey.call(
            crypto.subtle,
            format,
            keyData,
            algorithm,
            extractable,
            keyUsages,
          );
        },
      );

    // Act
    try {
      await keyEncryptionKeyFromPasskey(prfOutput);
    } finally {
      // Restored before the assertions, not after them: a failing expectation
      // below would otherwise leave `crypto.subtle.importKey` spied for every
      // test after this one in the file.
      importer.mockRestore();
    }

    // Assert
    // One key-encryption key was imported, from a full-width buffer that held
    // something — so this is the material the account's wrapping key was made
    // of, and not a scratch buffer a `fill(0)` elsewhere would satisfy by
    // accident.
    expect(aesImports).toBe(1);
    expect(atImportTime).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(
      Array.from(atImportTime).filter((byte) => byte === 0),
    ).not.toHaveLength(ACCOUNT_KEY_BYTES);

    // And the buffer holds nothing now. The wipe costs one `fill(0)` once
    // `importKey` has resolved; leaving it costs the plaintext of the value
    // that unwraps the account's whole keyspace, kept alive by the very
    // function whose `finally` exists to end it.
    expect(importedMaterial).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(toHex(importedMaterial)).toBe('00'.repeat(ACCOUNT_KEY_BYTES));
  });
});

// The one door from raw bytes to a key, and the seam the column encryption
// crosses. `importAesGcmKey` is that door and it is the only one: outside specs
// `crypto.subtle.importKey` appears exactly twice in the application, here and
// in `hkdf.ts`, and the second imports HKDF input keying material rather than an
// AES key. Both key-encryption-key derivations go through it, and so does
// anything that has to turn one of the account's own keys — which
// `generateAccountKeys` and `unwrapAccountKeys` hand back as `Uint8Array`, never
// as a key object — into something a cipher will take.
//
// **That there is one door is the argument, and the argument outlives the change
// that made it one.** A second `crypto.subtle.importKey` is four lines, anybody
// can write it beside this one, and it would hold none of what this holds. That
// is not a hypothesis about careless people: until this function was exported
// there was no other way to turn an account key into a key object at all, so the
// work that encrypts columns had exactly that four-line move in front of it.
//
// Five properties, and the guard on the far side of the seam holds one.
// Measured against `narrative-cipher.ts` as it stands: it refuses a key that is
// `extractable` — that is the one — while it accepts a key whose usages also
// carry `wrapKey`/`unwrapKey`, accepts a **sixteen byte** AES-128 key and
// round-trips under it, and never sees an account key as anything but
// `Uint8Array`. The other four are held **here and nowhere else**: the width by
// the check against `ACCOUNT_KEY_BYTES` inside the module's `try`, the usage
// list and the `extractable: false` flag by the arguments of its single
// `importKey` call, and the death of both copies of the bytes by the `finally`
// that ends them. Each is a line or two, and a line or two is what a refactor
// removes without noticing — which is what every test below exists to make
// impossible. Non-extractability is pinned in both places deliberately: a guard
// on the caller cannot speak for a key no caller ever inspects.
//
// The width is also what makes `ACCOUNT_KEY_BYTES` a decision rather than a
// hope. Its own documentation says the strength of every envelope the account
// ever writes is decided by that number and *enforced* by `importAesGcmKey`, and
// the enforcement is the check these cases exercise — paraphrased rather than
// quoted, because a sentence copied out of another file goes stale the first
// time that file is edited and nothing goes red. Nothing else in the system
// would object to less: a sixteen-byte key imports, seals and opens without
// complaint, so without a refusal here the strength would be decided by whatever
// bytes happened to reach an import.
//
// **The name.** `importKeyEncryptionKey` stops being true the moment an account
// key goes through it, and `importAccountKey` would be the same untruth pointing
// the other way — a factor's key-encryption key is not one of the account's two
// keys. `importAesGcmKey` names what comes out and stays quiet about whose
// secret went in, which is the only thing the two consumers have in common. It
// also names the algorithm, and the algorithm is pinned below, so a reader
// reaching for this to import an HKDF or an HMAC key is told by the name that
// this is not that door.
describe('importing raw bytes as a key', () => {
  // **Which of these cases try this code, and which try WebCrypto.** Measured
  // on the runner's implementation: `importKey` refuses 0, 15, 20, 31, 33 and 64
  // raw bytes with `DataError`, and accepts 16, 24 and 32 as AES-128, AES-192
  // and AES-256. So three of the five cases below are refused by the platform
  // whatever this module does, and they say nothing about `importAesGcmKey` at
  // all. They stay because a member that also refuses what the platform would
  // refuse costs nothing and reads honestly — but they must not be mistaken for
  // evidence.
  //
  // **The two that put this module on trial are 16 and 24**, and they are the
  // whole reason the width rule exists: they are the widths every layer
  // downstream accepts without a word. A check narrowed to "refuse sixteen"
  // leaves 24 green and every other case in this list green with it, and the
  // account writes AES-192 for the rest of its life.
  //
  // The bound itself is `ACCOUNT_KEY_BYTES` and never the number it currently
  // holds — the constant is where the strength is decided, and this is the
  // enforcement of that decision. The guard inside each case is what says so: a
  // constant that ever became one of these widths would leave the list pinning
  // the *accepted* width as refused, and the guard reddens first.
  it.each([
    {
      width: 16,
      why: 'AES-128, which WebCrypto imports happily — nothing else refuses it',
    },
    {
      width: 24,
      why: 'AES-192, the other width the platform calls perfectly legal',
    },
    {
      width: ACCOUNT_KEY_BYTES - 1,
      why: 'one byte short, which the platform refuses on its own',
    },
    {
      width: ACCOUNT_KEY_BYTES + 1,
      why: 'one byte long, which the platform refuses on its own',
    },
    {
      width: 0,
      why: 'nothing at all, which the platform refuses on its own',
    },
  ])('refuses material of $width bytes — $why', async ({ width }) => {
    // Arrange
    // Filled rather than left zero, so nothing here can be refused for being
    // empty when it should have been refused for being the wrong width.
    expect(width).not.toBe(ACCOUNT_KEY_BYTES);
    const material = new Uint8Array(width).fill(0xa5);

    // Act
    // Called from inside a `then` rather than directly: a width check is most
    // naturally written as an `if` at the top of the function, and in a
    // non-`async` function that `throw` lands synchronously. That is still a
    // refusal, and this shape reads it as one instead of failing the test with
    // the very exception it asked for.
    const importing = Promise.resolve().then(() => importAesGcmKey(material));

    // Assert
    // Sixteen and twenty-four are the cases that have to be refused *here*,
    // because nothing else refuses them: WebCrypto imports both, and the guard
    // on the far side of this seam was measured accepting a sixteen-byte key
    // and round-tripping under it. The account then writes AES-128 or AES-192
    // for the rest of its life, with nothing anywhere naming the moment it
    // started.
    await expect(importing).rejects.toThrow();

    // And the refusal ends the bytes as well. The width check sits inside the
    // module's `try` precisely so that one `finally` serves both paths, and the
    // argument is the one the module already makes about its own `finally`s:
    // the path that skips a wipe is the path where something has already gone
    // wrong. A key of the wrong width is still a secret — a sixteen-byte one is
    // a working AES-128 key somebody's account keys could be sealed under.
    //
    // On the zero-width case this assertion degenerates to `'' === ''` and holds
    // nothing; that case carries the refusal and only the refusal.
    expect(toHex(material)).toBe('00'.repeat(width));
  });

  it('accepts exactly the account key width and hands back a CryptoKey', async () => {
    // Arrange
    const material = new Uint8Array(ACCOUNT_KEY_BYTES).fill(0xa5);

    // Act
    const key = await importAesGcmKey(material);

    // Assert
    // The refusals above are only worth having if the accepted width is
    // accepted — a check written as `!== 32` on a module whose constant said
    // something else would pass every case above and no account would ever get
    // a key at all.
    expect(key).toBeInstanceOf(CryptoKey);
    expect(key.type).toBe('secret');

    // `length` is in bits, and it is the one place the width that was accepted
    // shows up in the object rather than in a buffer nothing keeps.
    expect(key.algorithm).toEqual({
      name: 'AES-GCM',
      length: ACCOUNT_KEY_BYTES * 8,
    });
  });

  it('returns a key made of the material it was given and no other', async () => {
    // Arrange
    // Every other test in this block reads the key's *shape* — the algorithm,
    // the width, the flag, the usages — and a shape is exactly what an
    // implementation that imported thirty-two zeros, or a fresh random draw,
    // also has. Nothing so far would tell them apart, and the symptom in
    // production would be an account whose columns are sealed under a key
    // nothing can rebuild.
    //
    // The reference is imported through `crypto.subtle.importKey` directly, from
    // a copy of the material taken **before** the call. Both halves matter: an
    // independent import is what makes this a second opinion rather than the
    // module agreeing with itself, and the copy is what survives the wipe —
    // reading `material` afterwards would build the reference out of zeros and
    // the test would fail for a reason that has nothing to do with the key.
    //
    // The material is a spec-local counter, structured so a byte landing in the
    // wrong place shows in the hex of a failure, and non-zero throughout so that
    // "a key of zeros" is a value it can be told apart from. It is nothing's
    // real key, so naming it here breaks no rule this file keeps about not
    // naming key material.
    const material = Uint8Array.from(
      { length: ACCOUNT_KEY_BYTES },
      (ignored, index) => index + 1,
    );
    const reference = await importAesKey(overOwnBuffer(material));
    const plaintext = fromHex(GOLDEN_PLAINTEXT);
    const associatedData = utf8.encode(GOLDEN_SPEC_ASSOCIATED_DATA);

    // Act
    // Sealed under the key this module returned, opened under the independent
    // one. Cross-opening rather than comparing two envelopes byte for byte, for
    // two reasons: it needs **no mock at all** — a byte comparison would have to
    // fix the nonce through the `getRandomValues` spy, and this file already
    // warns at `sealedUnder` how much damage a nonce mock left standing does —
    // and the failure it produces is production's own. Two keys that disagree
    // fail GCM's authentication, which is the same refusal a corrupted envelope
    // gives and the same one a person would meet on the day their columns
    // stopped opening.
    const key = await importAesGcmKey(material);
    const envelope = await sealEnvelope(key, plaintext, associatedData);
    const opened = await openEnvelope(reference, envelope, associatedData);

    // Assert
    // The plaintext comes back, so the two keys are the same key, so the module
    // imported the bytes it was handed.
    //
    // This is also the only thing in the suite that could catch a dropped
    // `await` before `importKey`, and it catches it **by construction rather
    // than by measurement**: wiping while the import is in flight makes the key
    // out of zeros, and a key of zeros fails here. Measured on this runtime the
    // dropped `await` still produces a correct key, because WebCrypto reads the
    // buffer synchronously — so this test stays green on Node however the module
    // is written. On an engine that reads asynchronously it is the assertion
    // that goes red, and the only one.
    expect(toHex(opened)).toBe(toHex(plaintext));
  });

  it('hands back a key whose bytes cannot come back out', async () => {
    // Arrange
    const material = new Uint8Array(ACCOUNT_KEY_BYTES).fill(0xa5);

    // Act
    const key = await importAesGcmKey(material);
    const reading = crypto.subtle.exportKey('raw', key);

    // Assert
    // Two observations of one property, and they are not the same observation.
    // The flag is what the import asked for; the rejection is what the platform
    // does about it. The first alone would pass on a key some other path built
    // with the flag set and the material still readable through a second handle,
    // and the second alone would pass on a platform that refused the export for
    // an unrelated reason. Together they are the module's boundary claim —
    // "what leaves this module is a non-extractable `CryptoKey`, never bytes" —
    // checked at the point the key is made rather than at the point it is used.
    expect(key.extractable).toBe(false);
    await expect(reading).rejects.toThrow();
  });

  it('gives the key exactly the two usages, sorted, and no third', async () => {
    // Arrange
    const material = new Uint8Array(ACCOUNT_KEY_BYTES).fill(0xa5);

    // Act
    const key = await importAesGcmKey(material);

    // Assert
    // Sorted, so this cannot be satisfied by a list that merely starts the same
    // way. `['encrypt', 'decrypt', 'wrapKey', 'unwrapKey']` is the list the
    // review measured being accepted on the far side of this seam, and it is
    // the list a second `importKey` written by hand is most likely to carry —
    // it looks like the more capable option and costs nothing visible. Why the
    // list is closed is argued at `importAesGcmKey`'s own declaration and is not
    // restated here.
    expect([...key.usages].sort()).toEqual(['decrypt', 'encrypt']);
  });

  it('hands WebCrypto a copy of its own and leaves the caller no bytes', async () => {
    // Arrange
    // The shape is the one the derivation's own wipe test uses, for the same
    // reason: no caller can observe either wipe through anything the module
    // hands back, so the buffer is reached at the platform boundary instead.
    // The bytes are read twice there — a snapshot at the moment `importKey` was
    // called, and the live view afterwards. The first reading is what makes a
    // green result unavailable to an implementation that imported an empty
    // buffer, or imported nothing at all and returned a key it made some other
    // way; without it, "all zeros afterwards" is a property of a buffer that
    // never held anything.
    //
    // It deliberately does not pin the *value*: this file names no key's bytes,
    // and the account's content key is exactly the value it must go on not
    // naming.
    //
    // The buffer's **identity** is read at the same boundary, and it is what
    // holds the defensive copy. Without `Uint8Array.from(material)` the buffer
    // WebCrypto is handed simply *is* the caller's array, and then both readings
    // below pass on a module that copies nothing: it held something at the
    // import and holds zeros after, because it is the array the wipe was always
    // going to reach. What is lost with the copy is the `BufferSource`
    // narrowing — a caller's `Uint8Array` may be a view over a
    // `SharedArrayBuffer` — and any protection from a caller that mutates its
    // own buffer while the import is in flight.
    //
    // What none of this can see: whether the wipe ran *after* WebCrypto finished
    // reading. The snapshot is taken synchronously inside `importKey`, before
    // any `finally` could run, so an implementation that dropped the `await` and
    // wiped while the promise was still in flight passes both readings — and on
    // this runtime, which reads the buffer synchronously, it even produces a
    // correct key. On an engine that reads asynchronously every key would be
    // made of zeros, silently and everywhere at once. That rule is held by the
    // `await` in the module and by nothing in this file.
    const material = new Uint8Array(ACCOUNT_KEY_BYTES).fill(0xa5);
    const realImportKey = crypto.subtle.importKey;

    let importedMaterial: Uint8Array = new Uint8Array(0);
    let atImportTime: Uint8Array = new Uint8Array(0);
    let importedFrom: ArrayBufferLike | null = null;
    let aesImports = 0;

    const importer = vi
      .spyOn(crypto.subtle, 'importKey')
      .mockImplementation(
        (format, keyData, algorithm, extractable, keyUsages) => {
          // On the algorithm rather than on a call index, so that an `'HKDF'`
          // import added inside this function later cannot silently move which
          // call is being measured.
          if (algorithm === 'AES-GCM') {
            aesImports += 1;
            importedMaterial = liveBytes(keyData);
            atImportTime = Uint8Array.from(importedMaterial);
            importedFrom = ArrayBuffer.isView(keyData)
              ? keyData.buffer
              : keyData;
          }

          // Called through, never faked: the import under measurement has to be
          // the real one, or the buffer being read is one nothing ever consumed
          // and the wipe is being observed on a value WebCrypto never saw.
          return realImportKey.call(
            crypto.subtle,
            format,
            keyData,
            algorithm,
            extractable,
            keyUsages,
          );
        },
      );

    // Act
    try {
      await importAesGcmKey(material);
    } finally {
      // Restored before the assertions, so a failure below does not leave
      // `crypto.subtle.importKey` spied for every test after this one.
      importer.mockRestore();
    }

    // Assert
    // One AES-GCM key was imported, from a full-width buffer that held
    // something.
    expect(aesImports).toBe(1);
    expect(atImportTime).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(
      Array.from(atImportTime).filter((byte) => byte === 0),
    ).not.toHaveLength(ACCOUNT_KEY_BYTES);

    // And it was the module's own buffer, never the caller's. Compared by the
    // underlying `ArrayBuffer` rather than by the view, so that handing
    // WebCrypto a `subarray` of the caller's array — a new object over the same
    // memory, and no copy at all — fails here too. Without the copy every other
    // assertion in this test still passes: the buffer the wipe reaches and the
    // buffer WebCrypto read are then the same one, so of course it held
    // something before and holds zeros after.
    expect(importedFrom).not.toBeNull();
    expect(importedFrom).not.toBe(material.buffer);

    // And both copies are gone: the buffer WebCrypto was handed, and the
    // caller's own array. The second is the one that matters to a caller
    // holding an account key — those bytes live in somebody else's variable,
    // and if this function does not end them, the value that decrypts every
    // column the account ever wrote stays on the heap for as long as the tab
    // lives. Nothing outside this module calls `importAesGcmKey` today, which
    // is the reason the rule needs a test rather than a reader: the first
    // caller will arrive long after the argument for the `finally` has stopped
    // being fresh in anybody's memory.
    expect(toHex(importedMaterial)).toBe('00'.repeat(ACCOUNT_KEY_BYTES));
    expect(material).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(toHex(material)).toBe('00'.repeat(ACCOUNT_KEY_BYTES));
  });
});

// The associated data is
//
//   "budgetoid/wrapped-key/v1" || 0x1F || <factor id> || 0x1F || <purpose>
//
// in UTF-8. `0x1F` is the ASCII unit separator and cannot occur in any of the
// three fields — the prefix is a literal, the factor id is a canonical UUID, and
// the purpose is one of two words — so the fields cannot run into one another
// and no length prefix is needed. That is an argument about the *fields*, which
// is why the factor id's shape is checked below rather than assumed.
describe('the associated data of a wrapped key', () => {
  // Frozen, and computed from the layout above with `node:crypto` rather than by
  // running the module. A red result here is never answered by updating the
  // constant: it is answered by naming which of the prefix, the separator, the
  // factor id's spelling or the purpose word changed, because every envelope
  // already written was bound to these bytes and none of them will open against
  // any others.
  const GOLDEN_CONTENT_AAD =
    '6275646765746f69642f777261707065642d6b65792f76311f63316432653366342d356136622d376338642d396530662d6131623263336434653566361f636f6e74656e74';

  // Escaped rather than typed. A literal control character in source is
  // invisible in every tool a reviewer would read this in, which is the one
  // property a frozen vector cannot afford.
  const UNIT_SEPARATOR = String.fromCharCode(0x1f);

  it('is the frozen bytes for a known factor and the content purpose', () => {
    // Arrange, Act
    const associatedData = wrappedKeyAssociatedData(
      PASSKEY_FACTOR_ID,
      'content',
    );

    // Assert
    // The hex is the pin; the text below it is the same value written out so a
    // reader can see the three fields and the two separators without decoding
    // anything.
    expect(toHex(associatedData)).toBe(GOLDEN_CONTENT_AAD);
    expect(utf8Decoder.decode(associatedData)).toBe(
      `budgetoid/wrapped-key/v1${UNIT_SEPARATOR}c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6${UNIT_SEPARATOR}content`,
    );
    expect(associatedData).toHaveLength(69);
  });

  it('reads every spelling of one factor id as the same factor', () => {
    // Arrange
    // A UUID has more than one way of being written down and only one canonical
    // form. This matters more than it looks: associated data is not carried in
    // the envelope, it is re-supplied from wherever the envelope was found — so
    // a client that wrapped under one spelling and read back another finds the
    // envelope unopenable, forever, with the same failure a corrupted key gives
    // and nothing anywhere saying which of the two spellings was right.
    const upperCase = PASSKEY_FACTOR_ID.toUpperCase();
    const braced = `{${PASSKEY_FACTOR_ID}}`;

    // Act
    const canonical = toHex(
      wrappedKeyAssociatedData(PASSKEY_FACTOR_ID, 'content'),
    );
    const fromUpperCase = toHex(wrappedKeyAssociatedData(upperCase, 'content'));
    const fromBraced = toHex(wrappedKeyAssociatedData(braced, 'content'));

    // Assert
    expect(fromUpperCase).toBe(canonical);
    expect(fromBraced).toBe(canonical);
  });

  it('tells the content copy apart from the index copy', () => {
    // Arrange, Act
    const content = toHex(
      wrappedKeyAssociatedData(PASSKEY_FACTOR_ID, 'content'),
    );
    const index = toHex(wrappedKeyAssociatedData(PASSKEY_FACTOR_ID, 'index'));

    // Assert
    // Without the purpose in here, one factor's two envelopes are bound to the
    // same data and are therefore interchangeable. Nothing downstream notices:
    // both open, both yield 32 usable bytes, and the account quietly gets a
    // second index keyspace that everything already written is invisible in.
    expect(index).not.toBe(content);
  });

  it('refuses a factor id that is not a UUID', () => {
    // Arrange
    // The separators only remove ambiguity if the fields cannot contain them,
    // and "cannot" is a claim about the factor id's shape. A free-form label —
    // "the passkey on my phone" — reintroduces every ambiguity the layout exists
    // to remove, and does it in a value that is written once and read back
    // forever.
    const freeForm = 'the passkey on my phone';

    // Act, Assert
    expect(() => wrappedKeyAssociatedData(freeForm, 'content')).toThrow();
  });
});

describe('wrapping the account keys', () => {
  it('renders both copies as unpadded base64url over an envelope', async () => {
    // Arrange
    const kek = await keyEncryptionKeyFromPasskey(fromHex(GOLDEN_PRF_OUTPUT));
    const keys = generateAccountKeys();

    // Act
    const wrapped = await wrapAccountKeys(kek, keys, PASSKEY_FACTOR_ID);

    // Assert
    // Sixty-one bytes is the envelope over a 32-byte key: one version byte,
    // twelve of nonce, thirty-two of ciphertext, sixteen of tag.
    // `decodeBase64Url` refuses padding, refuses `+` and `/`, and refuses a final
    // group no encoder would emit, so decoding is itself the assertion that the
    // wire form is the one the server's decoder will accept.
    for (const wire of [wrapped.wrappedContentKey, wrapped.wrappedIndexKey]) {
      expect(wire).toMatch(/^[A-Za-z0-9_-]+$/);
      expect(wire).not.toContain('=');
      expect(decodeBase64Url(wire)).toHaveLength(61);
    }
  });

  it('unwraps back to exactly the two keys that were wrapped', async () => {
    // Arrange
    const kek = await keyEncryptionKeyFromPasskey(fromHex(GOLDEN_PRF_OUTPUT));
    const keys = generateAccountKeys();

    // Act
    const wrapped = await wrapAccountKeys(kek, keys, PASSKEY_FACTOR_ID);
    const unwrapped = await unwrapAccountKeys(kek, wrapped, PASSKEY_FACTOR_ID);

    // Assert
    // Byte for byte, and compared as hex so a failure names the byte instead of
    // printing two arrays. The round trip is the whole of the module's
    // usefulness and the one thing an off-by-one in either direction breaks
    // loudly.
    expect(toHex(unwrapped.contentKey)).toBe(toHex(keys.contentKey));
    expect(toHex(unwrapped.indexKey)).toBe(toHex(keys.indexKey));
  });

  it('refuses a wrapped copy presented under another factor', async () => {
    // Arrange
    // IFR-009 in one assertion. The key-encryption key is the same on both sides
    // here, so nothing but the binding can refuse this — which is the point: two
    // factors of one account could share a derived key by accident, and a
    // wrapped copy lifted out of one row into another must still not open.
    const kek = await keyEncryptionKeyFromPasskey(fromHex(GOLDEN_PRF_OUTPUT));
    const keys = generateAccountKeys();
    const wrapped = await wrapAccountKeys(kek, keys, PASSKEY_FACTOR_ID);

    // Act
    const opening = unwrapAccountKeys(kek, wrapped, RECOVERY_FACTOR_ID);

    // Assert
    await expect(opening).rejects.toThrow();
  });

  it('refuses the content copy presented as the index copy', async () => {
    // Arrange
    // The reason the purpose is in the associated data at all. Both envelopes
    // are under one key and one factor, so everything else about them matches:
    // swapped, they decrypt to 32 perfectly usable bytes each. An operator or a
    // migration that transposed two columns would hand the account a second
    // index keyspace, and the only symptom is search returning nothing for
    // everything written before the swap.
    const kek = await keyEncryptionKeyFromPasskey(fromHex(GOLDEN_PRF_OUTPUT));
    const keys = generateAccountKeys();
    const wrapped = await wrapAccountKeys(kek, keys, PASSKEY_FACTOR_ID);

    // Act
    const swapped = {
      wrappedContentKey: wrapped.wrappedIndexKey,
      wrappedIndexKey: wrapped.wrappedContentKey,
    };
    const opening = unwrapAccountKeys(kek, swapped, PASSKEY_FACTOR_ID);

    // Assert
    await expect(opening).rejects.toThrow();
  });

  it('gives one account the same two keys through either kind of factor', async () => {
    // Arrange
    // The property that makes a second factor a second way in rather than a
    // second, incompatible budget. The keys are generated once and wrapped
    // twice: once under a passkey's key-encryption key, once under a recovery
    // code's, each bound to its own factor id. An implementation that generated
    // fresh keys per factor passes every other test in this file and loses the
    // account's whole history the first time the other factor is used.
    const keys = generateAccountKeys();
    const fromPasskey = await keyEncryptionKeyFromPasskey(
      fromHex(GOLDEN_PRF_OUTPUT),
    );
    const fromCode = await keyEncryptionKeyFromRecoveryCode(PRINTED_CODE);

    // Act
    const wrappedForPasskey = await wrapAccountKeys(
      fromPasskey,
      keys,
      PASSKEY_FACTOR_ID,
    );
    const wrappedForCode = await wrapAccountKeys(
      fromCode,
      keys,
      RECOVERY_FACTOR_ID,
    );

    const throughPasskey = await unwrapAccountKeys(
      fromPasskey,
      wrappedForPasskey,
      PASSKEY_FACTOR_ID,
    );
    const throughCode = await unwrapAccountKeys(
      fromCode,
      wrappedForCode,
      RECOVERY_FACTOR_ID,
    );

    // Assert
    expect(toHex(throughPasskey.contentKey)).toBe(toHex(keys.contentKey));
    expect(toHex(throughPasskey.indexKey)).toBe(toHex(keys.indexKey));
    expect(toHex(throughCode.contentKey)).toBe(toHex(keys.contentKey));
    expect(toHex(throughCode.indexKey)).toBe(toHex(keys.indexKey));

    // And the two factors' wire values differ, because the key-encryption keys,
    // the nonces and the associated data all do — equal envelopes here would
    // mean one of those three stopped varying.
    expect(wrappedForCode.wrappedContentKey).not.toBe(
      wrappedForPasskey.wrappedContentKey,
    );
  });
});

describe("the contract's constants", () => {
  it('names the three labels by value, the way the verifier label already is', () => {
    // Arrange, Act, Assert
    // Pinned by value rather than derived, for the same reason
    // `RECOVERY_CODE_VERIFIER_INFO` is: these strings are part of the definition
    // of every key and every envelope the client has already written, so a
    // change to one is a new version minted alongside the old, never an edit.
    expect(PASSKEY_KEY_ENCRYPTION_KEY_INFO).toBe(
      'budgetoid/passkey/key-encryption-key/v1',
    );
    expect(WRAPPED_KEY_AAD_PREFIX).toBe('budgetoid/wrapped-key/v1');

    // This one is live now: `webauthn-encoding.ts` writes it into the `prf`
    // extension's `eval.first` of every creation request, and
    // `webauthn-ceremony.service.ts` evaluates against it on both legs of a
    // ceremony — so any passkey this client has registered derived its
    // key-encryption key from this exact string, and the envelopes wrapped
    // under that key open against nothing else. Every other reader spells the
    // value by importing the constant, which leaves this assertion the only
    // thing in the system that would notice the value itself drifting. The day
    // it drifts, every account wrapped under the old one is locked out
    // silently, by a passkey that still authenticates perfectly and simply
    // hands back different bytes.
    expect(PASSKEY_PRF_EVAL_INPUT).toBe('budgetoid/passkey/prf-eval-input/v1');

    // And the three are distinct, which a copy-paste between them would break
    // while leaving the three assertions above intact only if somebody edited
    // them together.
    expect(
      new Set([
        PASSKEY_PRF_EVAL_INPUT,
        PASSKEY_KEY_ENCRYPTION_KEY_INFO,
        WRAPPED_KEY_AAD_PREFIX,
      ]).size,
    ).toBe(3);
  });
});

describe('the module surface', () => {
  it('exports these functions and no others', () => {
    // Arrange
    // Read the claim carefully, because the obvious stronger reading is false:
    // `generateAccountKeys` returns unwrapped key material by definition, so
    // this does **not** say "nothing unwrapped is exported". What it says is
    // that the set of openings is the set somebody argued for. A helper added in
    // passing — an extractable import, a debug dump of a key-encryption key, a
    // wrap that skips the associated data — is a new name here, and a new name
    // is a red test and a conversation rather than a diff nobody read.
    const expected = [
      'generateAccountKeys',
      // The module's one AES-GCM import, open rather than private, and open on
      // purpose: the alternative to opening it is a *second* `importKey`
      // written beside it by whoever needs a key object next, holding none of
      // the width, usage, extractability and wiping rules this one holds. That
      // trade is why it is in this set, and this set is where a later reader
      // finds the trade argued instead of inferred.
      'importAesGcmKey',
      'keyEncryptionKeyFromPasskey',
      'keyEncryptionKeyFromRecoveryCode',
      'unwrapAccountKeys',
      'wrapAccountKeys',
      'wrappedKeyAssociatedData',
    ].sort();

    // Act
    const exported = Object.entries(accountKeysModule)
      .filter(([, value]) => typeof value === 'function')
      .map(([name]) => name)
      .sort();

    // Assert
    expect(exported).toEqual(expected);
  });
});
