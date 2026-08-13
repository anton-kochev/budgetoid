// One HKDF, shared. `recovery-codes.ts` carried a private `deriveBranch` and
// the next branch needs the same derivation with a different `info` — the
// account's key-encryption key, whose whole security argument is that it is the
// *same* expansion over a *different* label. Two copies of that expansion is two
// places for the hash, the salt or the label encoding to drift apart, and every
// symptom of drift is silent: keying material of the right width that decrypts
// nothing.
//
// What this file pins is a primitive, so it pins it against a published answer
// rather than against itself. RFC 5869 §A.3 is the only test case in the RFC
// that fits this shape, because Test Cases 1 and 2 both use a non-empty salt and
// this function fixes the salt empty — which is not a simplification but a
// requirement: a redemption arrives carrying a verifier and no identity, so
// there is no row a salt could be taken from (ADR 0015). The vector is therefore
// doing double duty. It is a known-answer test for the hash, the expand loop and
// the output width, and it is the *only* thing that pins the salt, since TC3's
// expected output is unreachable with a salt of any other value. There is
// deliberately no assertion that reads the salt out of the implementation.
import { describe, expect, it } from 'vitest';

import { hkdfSha256 } from './hkdf';

// RFC 5869 §A.3, Test Case 3: "with SHA-256 and zero-length salt/info".
//   IKM  = 0x0b repeated 22 times
//   salt = (0 octets)
//   info = (0 octets)
//   L    = 42
//
// This value comes from the RFC and is not ours to update. A red result here is
// never a new expected output — it is the hash, the salt, the expand loop or the
// requested width having changed, and each of those changes every key and every
// verifier this codebase has ever derived.
const TEST_CASE_3_IKM = new Uint8Array(22).fill(0x0b);
const TEST_CASE_3_LENGTH = 42;
const TEST_CASE_3_OKM =
  '8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8';

// A label with characters outside ASCII, and the answer for it, frozen the same
// way. Both of its non-ASCII runs matter: the Cyrillic characters sit above
// U+00FF, where a `String.fromCharCode`-style encoder truncates a code unit to
// its low byte, and `é` sits below it, where Latin-1 and UTF-8 disagree without
// truncating. Its UTF-8 bytes are written out beside it so the two can be
// compared by eye.
const NON_ASCII_INFO = 'budgetoid/ключ-é/v1';
// UTF-8: 6275646765746f69642f d0ba d0bb d18e d187 2d c3a9 2f7631
const NON_ASCII_OKM =
  '7bc098a971503df93e1a5bdd69b96b5c8b7de96ccfb064d0be9df08bf905fb9d';

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

describe('HKDF-SHA-256 over an empty salt', () => {
  it('reproduces RFC 5869 test case 3, the one published vector of this shape', async () => {
    // Arrange
    const ikm = TEST_CASE_3_IKM;

    // Act
    const okm = await hkdfSha256(ikm, '', TEST_CASE_3_LENGTH);

    // Assert
    // Forty-two bytes is more than SHA-256 produces in one round, so this also
    // exercises the expand loop across a block boundary and the truncation of
    // its last block — 42 is neither one block nor a whole number of them.
    expect(toHex(okm)).toBe(TEST_CASE_3_OKM);
  });

  it('returns exactly the number of bytes it was asked for', async () => {
    // Arrange
    // Sixteen is inside one SHA-256 round, thirty-two is exactly one, and
    // sixty-four is two — so the set spans the boundary in both directions. A
    // width parameter that is silently ignored, or read as bits rather than
    // bytes, fails here and is otherwise invisible: the caller gets keying
    // material of a plausible length either way.
    const widths = [16, 32, 64];

    // Act
    const derived = await Promise.all(
      widths.map((width) => hkdfSha256(TEST_CASE_3_IKM, 'width', width)),
    );

    // Assert
    for (const [index, width] of widths.entries()) {
      expect(derived[index]).toHaveLength(width);
    }
  });

  it('produces unrelated output for two info strings over the same key material', async () => {
    // Arrange
    // The property every branch separation in this codebase rests on. Same
    // input keying material, same salt, same hash — `info` is the *only* thing
    // that distinguishes the verifier that crosses the wire from the
    // key-encryption key that never does, so an implementation that drops
    // `info` on the floor folds the two into one derivation. Everything else
    // still works: the client derives, the server accepts, and the value the
    // operator sees in a request body is the account's key.
    //
    // Note what would otherwise let that through — RFC 5869's test case 3 uses
    // an empty `info`, so the vector above passes on an implementation that
    // ignores the parameter entirely. This is the test that does not.
    const ikm = TEST_CASE_3_IKM;

    // Act
    const [first, second] = await Promise.all([
      hkdfSha256(ikm, 'budgetoid/spec/branch-a/v1', 32),
      hkdfSha256(ikm, 'budgetoid/spec/branch-b/v1', 32),
    ]);

    // Assert
    expect(toHex(second)).not.toBe(toHex(first));
  });

  it('is a function of its inputs rather than a generator', async () => {
    // Arrange
    // Determinism is what makes anything derived here recoverable at all: the
    // key wrapped at onboarding and the key derived from the same code typed
    // back months later have to be one value, with no account, no salt and no
    // server state in between. A nonce or a timestamp anywhere in the
    // derivation loses the account's data with no error at any point.
    const ikm = TEST_CASE_3_IKM;
    const info = 'budgetoid/spec/deterministic/v1';

    // Act
    const [once, twice] = await Promise.all([
      hkdfSha256(ikm, info, 32),
      hkdfSha256(ikm, info, 32),
    ]);

    // Assert
    expect(toHex(twice)).toBe(toHex(once));
  });

  it('encodes info as UTF-8, so a label outside ASCII cannot be truncated', async () => {
    // Arrange
    // `ф` is U+0444. A UTF-8 encoder gives two bytes for it; the two wrong
    // encoders a reader would reach for both give one — `String.fromCharCode`
    // and a Latin-1 `Buffer` each keep the low byte of the code unit, which for
    // U+0444 is 0x44, the ASCII `D`. So an implementation that swapped UTF-8 for
    // either would derive the *same* bytes for these two labels, and every
    // ASCII-only vector in this file would still pass.
    const cyrillic = 'ф';
    const collidingAscii = 'D';

    // Act
    const [fromCyrillic, fromAscii, fromMixed] = await Promise.all([
      hkdfSha256(TEST_CASE_3_IKM, cyrillic, 16),
      hkdfSha256(TEST_CASE_3_IKM, collidingAscii, 16),
      hkdfSha256(TEST_CASE_3_IKM, NON_ASCII_INFO, 32),
    ]);

    // Assert
    expect(toHex(fromCyrillic)).not.toBe(toHex(fromAscii));

    // And the exact bytes, frozen, for a label mixing ASCII with characters on
    // both sides of U+00FF. The inequality above rules out truncation; this
    // rules out every other encoding of the same string, including a Latin-1
    // one that happens not to truncate.
    expect(toHex(fromMixed)).toBe(NON_ASCII_OKM);
  });
});
