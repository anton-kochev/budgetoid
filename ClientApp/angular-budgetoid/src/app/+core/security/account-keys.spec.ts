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
  keyEncryptionKeyFromPasskey,
  keyEncryptionKeyFromRecoveryCode,
  unwrapAccountKeys,
  wrapAccountKeys,
  wrappedKeyAssociatedData,
} from './account-keys';
import * as accountKeysModule from './account-keys';
import { decodeBase64Url } from './base64url';
import { sealEnvelope } from './key-envelope';
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

    // Nothing derives from the eval input yet — no client here can run a
    // WebAuthn ceremony, so there is no PRF output to evaluate against it. That
    // is exactly why it is pinned: this assertion is the only thing in the
    // system that would notice it drifting, and the day it drifts every account
    // that wrapped its keys under the old one is locked out silently, by a
    // passkey that still authenticates perfectly and simply hands back different
    // bytes.
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
