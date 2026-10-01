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
import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

import { listFiles } from '../../../production-bundle';
import { hkdfSha256, hkdfSha256Over } from './hkdf';

// `src/` rather than the build output, for the reason
// `associated-data.spec.ts` gives at its own source-reading case: `src/` is what
// a reviewer reads and what the rule is about, and reading it needs no prior
// build.
const modulePath = join(
  process.cwd(),
  'src',
  'app',
  '+core',
  'security',
  'hkdf.ts',
);

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

// An `info` that no string can carry: sixty-five bytes shaped like an
// uncompressed P-256 point, leading with `0x04` and then walking from 0x81 up.
// Every byte from 0x80 on is where UTF-8 widens one byte into two, so the string
// spelling of these bytes is 129 bytes long — measured, and asserted below
// rather than claimed.
const POINT_LIKE_INFO = Uint8Array.from(
  [...Array(65).keys()].map((at) => (at === 0 ? 0x04 : (0x80 + at) & 0xff)),
);

// The answer for it, computed outside this codebase by Node's own
// `crypto.hkdfSync` — OpenSSL's HKDF, not the platform this module calls — over
// the same 22-byte test-case-3 keying material and an empty salt. That
// implementation reproduces the RFC's test case 3 above byte for byte, which is
// what makes it worth quoting here: it is a second opinion that has already
// answered a published question correctly.
//
// It is not ours to update either. A red result is the hash, the salt, the
// expand loop or the *handling of a raw `info`* having changed, and the last of
// those changes every encapsulated value this client has ever written.
const POINT_LIKE_OKM =
  '854ffa4c881c8bb8edc4818e5b8e02638f49aaed27b4db1c43a38ce5cdcf41e9';

const utf8 = new TextEncoder();

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

// The same derivation, with the one encoding decision lifted out to the caller.
//
// `hkdfSha256` is expressed on top of this function rather than beside it, so
// what the cases above pin — the hash, the empty salt, the expand loop, the
// width — is pinned once for both spellings. What is left to check here is the
// seam: that the bytes path answers a UTF-8 `info` exactly as the string path
// does, and that it can express an `info` the string path cannot.
describe('HKDF-SHA-256 over a bytes info', () => {
  it('answers a UTF-8 info exactly as the string spelling does', async () => {
    // Arrange
    // Both frozen answers, not one. Test case 3's empty `info` is the published
    // vector and says nothing about encoding; the non-ASCII label is the one
    // that does, and a bytes path wired to a different hash or a different salt
    // would fail the first while a path that re-encoded its `info` would fail
    // the second.
    const ikm = TEST_CASE_3_IKM;

    // Act
    const [empty, nonAscii, viaString] = await Promise.all([
      hkdfSha256Over(ikm, new Uint8Array(0), TEST_CASE_3_LENGTH),
      hkdfSha256Over(ikm, utf8.encode(NON_ASCII_INFO), 32),
      hkdfSha256(ikm, NON_ASCII_INFO, 32),
    ]);

    // Assert
    expect(toHex(empty)).toBe(TEST_CASE_3_OKM);
    expect(toHex(nonAscii)).toBe(NON_ASCII_OKM);

    // And the two spellings named against each other, so a day when both frozen
    // answers are edited together still leaves one assertion saying the string
    // path is this function under an encoder.
    expect(toHex(viaString)).toBe(toHex(nonAscii));
  });

  it('derives over an info the string path could not express', async () => {
    // Arrange
    // **This is the whole reason the sibling exists.** IFR-020's info carries
    // two raw 65-byte points, and a point pushed through UTF-8 comes out 129
    // bytes with no error anywhere — the replacement character standing in for
    // every byte that is not a code point. So the string path does not merely
    // spell this `info` awkwardly, it derives a *different key*, and both sides
    // of a scheme that made that mistake would agree with each other and with
    // nothing else.
    const asString = String.fromCharCode(...POINT_LIKE_INFO);

    // Act
    const [overBytes, overString] = await Promise.all([
      hkdfSha256Over(TEST_CASE_3_IKM, POINT_LIKE_INFO, 32),
      hkdfSha256(TEST_CASE_3_IKM, asString, 32),
    ]);

    // Assert
    // The inflation first, measured rather than asserted about: sixty-five
    // bytes in, one hundred and twenty-nine out. Without this the inequality
    // below could be read as two arbitrary values differing.
    expect(POINT_LIKE_INFO).toHaveLength(65);
    expect(utf8.encode(asString)).toHaveLength(129);

    // The independently computed answer, so this case pins the derivation and
    // not only the difference between two paths.
    expect(toHex(overBytes)).toBe(POINT_LIKE_OKM);
    expect(toHex(overString)).not.toBe(POINT_LIKE_OKM);
  });

  it('is the only module that names the algorithm', () => {
    // Arrange
    // **The hole the key-import census cannot cover any more.** That rule is
    // between files, and `factor-keypair.ts` now has standing to write
    // `crypto.subtle.importKey` — so the expansion this module exists to own
    // could be written back out beside its caller there, and nothing in that
    // census would say a word. Measured: with the expansion re-inlined in
    // `factor-keypair.ts`, all 587 cases in this folder passed.
    //
    // What cannot be hidden is the algorithm's name. Every route to an
    // expansion — `importKey`, `deriveBits`, `deriveKey` — has to name it as a
    // string, so the quoted literal is the needle. Prose says HKDF constantly
    // and says it in prose; the quotes are what tell a mention from a call.
    //
    // Specs are exempt for `key-import-single-source.spec.ts`'s reason: this
    // file names the algorithm in the sentence above, and a reference
    // implementation in a spec is the only second opinion a derivation has.
    const sourceDir = join(process.cwd(), 'src');
    const needle = "'HKDF'";

    // Act
    const namers = listFiles(sourceDir)
      .filter((path) => path.endsWith('.ts'))
      .filter((path) => !path.endsWith('.spec.ts'))
      .filter((path) => readFileSync(path, 'utf8').includes(needle))
      .map((path) => relative(sourceDir, path))
      .sort();

    // Assert
    // Named rather than counted, so a red bar says which module started
    // expanding on its own instead of saying that some module did.
    expect(namers).toEqual([join('app', '+core', 'security', 'hkdf.ts')]);
  });

  it('keeps one key import, so the string path is this function and not a copy', () => {
    // Arrange
    // A claim about the source text, which is where it has to be made: two
    // expansions written side by side agree on every vector in this file on the
    // day they are written, and `key-import-single-source.spec.ts` counts
    // files rather than call sites inside one, deliberately. So the second copy
    // this module could grow is caught here or by nothing.
    const source = readFileSync(modulePath, 'utf8');

    // Act
    // The needle carries its open parenthesis, so what is counted is a call and
    // not a mention: the module's own prose names the member twice, and a rule
    // that counted those would be red the day it was written. The looser needle
    // is what `key-import-single-source.spec.ts` uses, correctly — it asks which
    // *files* carry the member and a comment in the wrong file is worth
    // reporting. Here the question is how many times this module expands, and
    // the failure direction is still the safe one: a comment writing the call
    // out in full over-counts and reddens, it never hides a second expansion.
    const imports = source.split('crypto.subtle.importKey(').length - 1;

    // Assert
    // A control first: the read reached this module rather than an empty string
    // from a path that moved, which would report zero imports perfectly.
    expect(source).toContain('export async function hkdfSha256Over(');
    expect(imports).toBe(1);
  });
});
