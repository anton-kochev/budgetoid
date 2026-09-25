// The browser half of a two-sided contract: `canonicalRecoveryCode` and the two
// HKDF branches derived off it, against known answers neither this codebase nor
// the server's suite authored — `docs/business-logic/vectors/recovery-code-v1.json`.
//
// **This file exists because the fold is written twice and nothing compared the
// two.** The authoritative implementation is `recovery-code-canonical.ts`: it
// runs in a browser and derives real keys. The other is a reproduction in the
// server's integration suite, kept so a test can seed a code a redemption
// spends. Both were self-consistent, both were green, and they disagreed on four
// code points — U+FEFF, U+0085, U+00DF and U+0131. A disagreement here is not
// cosmetic. A code is folded once and two independent branches run off the
// result: the verifier that crosses the wire, and the key-encryption key that
// never leaves the browser. Fold differently and a code that redeems fine opens
// nothing, or the reverse — silently, long after the typing that caused it.
//
// **What this file adds that `recovery-code-canonical.spec.ts` cannot.** That
// spec is an independent statement of the rule, written by a reader of this
// repository. It is the right test and it is not a contract: every answer in it
// was authored beside the implementation, so the two can be wrong together and
// stay green together. The answers below were computed outside both codebases
// from the rule as prose, and they reproduce the two recovery-code values
// already frozen in `docs/business-logic/account-keys.md`. When a case here goes
// red, the implementation moved — the vector did not.
//
// **Inputs are built from `inputUtf8Hex`, never from `input`.** Five rows carry
// a code point nothing renders — a byte order mark, NEXT LINE, a zero width
// space, a no-break space and an ideographic space. Going through the hex is the
// only way a case can be certain its input is the one the vector describes, and
// it puts the row out of reach of an editor that trims, normalises or drops what
// it cannot draw. Outputs are compared the same way: the assertion that counts
// is over `canonicalUtf8Hex`, because two strings differing only by an invisible
// code point print identically in a failure message and a reader comparing them
// by eye would call them equal.
//
// **The key-encryption key is non-extractable by construction**, so no assertion
// below reads its bytes — there is no API that can. It is observed the way
// `account-keys.spec.ts` observes its own key vectors: the frozen hex is
// imported as a key of this spec's own, something is sealed under both with a
// fixed nonce, and the envelopes are compared. Equal envelopes under one nonce,
// one plaintext and one associated data mean one key.
//
// The file is read with `node:fs` and a parser that throws. It must never skip:
// a run that cannot read the artifact has pinned nothing, and "the answers were
// authored elsewhere" is the whole of what these cases buy.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { keyEncryptionKeyFromRecoveryCode } from './account-keys';
import { sealEnvelope } from './key-envelope';
import { canonicalRecoveryCode } from './recovery-code-canonical';
import {
  RECOVERY_CODE_BRANCH_INFO,
  recoveryCodeVerifier,
} from './recovery-codes';

// ---------------------------------------------------------------------------
// The frozen file.

const VECTOR_FILE_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'recovery-code-v1.json',
);

/** One `canonicalForm` row: a text in, the text the fold owes out. */
interface FrozenCanonicalFormRow {
  /** The argument for the row, carried into the case name. */
  readonly why: string;
  readonly input: string;
  readonly inputUtf8Hex: string;
  readonly canonical: string;
  readonly canonicalUtf8Hex: string;
}

/** One `derivations` row: a code in, both branches' answers out. */
interface FrozenDerivationRow {
  readonly why: string;
  readonly code: string;
  readonly canonical: string;
  readonly verifierBase64Url: string;
  readonly keyEncryptionKeyHex: string;
}

/** The two HKDF `info` strings the branches are separated by. */
interface FrozenInfos {
  readonly verifier: string;
  readonly keyEncryptionKey: string;
}

interface FrozenVectorFile {
  readonly infos: FrozenInfos;
  readonly canonicalForm: readonly FrozenCanonicalFormRow[];
  readonly derivations: readonly FrozenDerivationRow[];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requireRecord(
  source: Record<string, unknown>,
  key: string,
  what: string,
): Record<string, unknown> {
  const value = source[key];

  if (!isRecord(value)) {
    throw new Error(`${what} carries no ${key} object.`);
  }

  return value;
}

// Present and a string, empty or not. Two fields in this file are legitimately
// empty — the canonical form of whitespace alone is nothing — and a reader that
// treated empty as missing would throw on the one row stating that the fold is
// total.
function requireText(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = source[key];

  if (typeof value !== 'string') {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

function requireString(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = requireText(source, key, what);

  if (value.length === 0) {
    throw new Error(`${what} carries an empty ${key}.`);
  }

  return value;
}

// Lower-case hex in whole bytes. Upper-case is refused rather than folded: every
// comparison below is a string comparison against this text, so a folded
// spelling would report a mismatch nobody could locate.
function requireHex(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = requireText(source, key, what);

  if (!/^([0-9a-f]{2})*$/.test(value)) {
    throw new Error(`${what} carries a ${key} that is not lower-case hex.`);
  }

  return value;
}

function parseCanonicalFormRow(value: unknown): FrozenCanonicalFormRow {
  if (!isRecord(value)) {
    throw new Error('A canonical-form row is not an object.');
  }

  const why = requireString(value, 'why', 'A canonical-form row');

  return {
    why,
    input: requireText(value, 'input', why),
    inputUtf8Hex: requireHex(value, 'inputUtf8Hex', why),
    canonical: requireText(value, 'canonical', why),
    canonicalUtf8Hex: requireHex(value, 'canonicalUtf8Hex', why),
  };
}

function parseDerivationRow(value: unknown): FrozenDerivationRow {
  if (!isRecord(value)) {
    throw new Error('A derivation row is not an object.');
  }

  const why = requireString(value, 'why', 'A derivation row');

  return {
    why,
    code: requireString(value, 'code', why),
    canonical: requireString(value, 'canonical', why),
    verifierBase64Url: requireString(value, 'verifierBase64Url', why),
    keyEncryptionKeyHex: requireHex(value, 'keyEncryptionKeyHex', why),
  };
}

function parseVectorFile(text: string): FrozenVectorFile {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The recovery-code vector file is not an object.');
  }

  const infos = requireRecord(parsed, 'infos', 'The recovery-code vector file');
  const canonicalForm = parsed['canonicalForm'];
  const derivations = parsed['derivations'];

  if (!Array.isArray(canonicalForm) || canonicalForm.length === 0) {
    throw new Error('The recovery-code vector file lists no canonical forms.');
  }

  if (!Array.isArray(derivations) || derivations.length === 0) {
    throw new Error('The recovery-code vector file lists no derivations.');
  }

  return {
    infos: {
      verifier: requireString(infos, 'verifier', 'infos'),
      keyEncryptionKey: requireString(infos, 'keyEncryptionKey', 'infos'),
    },
    canonicalForm: canonicalForm.map((row: unknown) =>
      parseCanonicalFormRow(row),
    ),
    derivations: derivations.map((row: unknown) => parseDerivationRow(row)),
  };
}

const VECTOR_FILE = parseVectorFile(readFileSync(VECTOR_FILE_PATH, 'utf8'));

// The counts this suite was written against, written out here and read from
// nowhere. Without them, deleting a row is a silent way to make a red case
// green: the per-row cases are generated from the file, so a file with fourteen
// rows runs fourteen green cases and reports nothing missing.
const CANONICAL_FORM_ROWS = 15;
const DERIVATION_ROWS = 2;

// ---------------------------------------------------------------------------
// Bytes, text and the seal used to observe a key nobody can read.

const utf8 = new TextEncoder();

// `fatal: true`, so a row whose hex is not valid UTF-8 throws here rather than
// arriving at the fold as a replacement character and folding to something
// plausible.
//
// **`ignoreBOM: true` is the load-bearing half, and it is named the wrong way
// round.** The option does not mean "ignore the byte order mark"; it means
// *ignore the BOM's special status* and decode it as an ordinary U+FEFF. The
// default — `ignoreBOM: false` — silently **removes** a leading BOM from the
// output. Measured here on the first run of this file: the `efbbbf…` row
// arrived at the fold as a code with no byte order mark in front of it, folded
// to itself, and the case comparing it to `canonicalUtf8Hex` went **green** —
// having asserted that the fold strips a character that was never there. The
// row testing the one platform-defined answer nobody else holds was the one row
// asserting nothing. The `spells its own input` case beside it is what caught
// it, by comparing the decoded text against the `input` field.
const strictUtf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

// The buffer type is spelled out because `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either.
function fromHex(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(text.match(/../g) ?? [], (pair) => parseInt(pair, 16));
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

function utf8FromHex(hex: string): string {
  return strictUtf8.decode(fromHex(hex));
}

function utf8Hex(text: string): string {
  return toHex(utf8.encode(text));
}

// This file's own frozen seal inputs, deliberately different from the ones
// `account-keys.spec.ts` uses, so a failure's hex says which suite produced it.
// None of them is part of the contract — they are a ruler held against two keys.
const SPEC_NONCE = 'd0d1d2d3d4d5d6d7d8d9dadb';
const SPEC_PLAINTEXT =
  '606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f';
const SPEC_ASSOCIATED_DATA = 'budgetoid/recovery-code/fold-vectors/spec/v1';

const SPEC_NONCE_BYTES = fromHex(SPEC_NONCE);

// `extractable: false`, matching the shape `keyEncryptionKeyFromRecoveryCode`
// is specified to produce. An extractable import here would quietly stop
// exercising the reason that function returns a `CryptoKey` at all.
function importAesKey(bytes: Uint8Array<ArrayBuffer>): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// Seals the frozen plaintext under `key` with the frozen nonce, as hex.
//
// The nonce is the one input a caller cannot supply, so it is fed through the
// same seam `account-keys.spec.ts` uses, and nothing else is replaced: the key
// import, the cipher and the encoder are production's. The spy is installed
// immediately before the seal and restored immediately after rather than for the
// length of a case — a mock left standing would serve any other draw in the same
// case out of these twelve bytes.
async function sealedUnder(key: CryptoKey): Promise<string> {
  const fixedNonce = vi
    .spyOn(crypto, 'getRandomValues')
    .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
      const bytes = new Uint8Array(
        (buffer as ArrayBufferView).buffer,
        (buffer as ArrayBufferView).byteOffset,
        (buffer as ArrayBufferView).byteLength,
      );
      bytes.set(SPEC_NONCE_BYTES.subarray(0, bytes.length));

      return buffer;
    });

  try {
    return toHex(
      await sealEnvelope(
        key,
        fromHex(SPEC_PLAINTEXT),
        utf8.encode(SPEC_ASSOCIATED_DATA),
      ),
    );
  } finally {
    fixedNonce.mockRestore();
  }
}

// `restoreMocks` is not configured for this runner, so a spy that escaped the
// `finally` above would otherwise outlive its case and be the thing a later case
// draws its nonce from.
afterEach(() => {
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// The canonical form.

describe('the frozen canonical form of a recovery code', () => {
  it.each(VECTOR_FILE.canonicalForm)(
    'folds the frozen input to the frozen canonical form — $why',
    ({ inputUtf8Hex, canonical, canonicalUtf8Hex }) => {
      // Arrange
      // Built from the hex rather than from `input`, because five of these rows
      // carry a code point nothing renders and the hex is the only spelling that
      // cannot have been silently rewritten on its way into this file.
      const input = utf8FromHex(inputUtf8Hex);

      // Act
      const folded = canonicalRecoveryCode(input);

      // Assert
      // The hex first: it is the assertion that holds, because two strings
      // differing only by an invisible code point print identically in a failure
      // message. The string comparison beside it is what makes the failure
      // readable when the difference *is* visible.
      expect(utf8Hex(folded)).toBe(canonicalUtf8Hex);
      expect(folded).toBe(canonical);
    },
  );

  it.each(VECTOR_FILE.canonicalForm)(
    'spells its own input the same way twice — $why',
    ({ input, inputUtf8Hex }) => {
      // Arrange
      // The rows are driven off the hex, so the human-readable field beside it
      // is otherwise asserted by nothing — and that is exactly the field an
      // editor mangles: a trimmed trailing space, a byte order mark a tool
      // helpfully removed, a no-break space normalised to a plain one. A row
      // whose two halves have drifted apart is a row whose `why` describes
      // something no case runs.
      const fromHexField = utf8FromHex(inputUtf8Hex);

      // Act
      const spelled = utf8Hex(input);

      // Assert
      expect(spelled).toBe(inputUtf8Hex);
      expect(input).toBe(fromHexField);
    },
  );

  it('still carries every canonical-form row this suite was written against', () => {
    // Arrange, Act
    const rows = VECTOR_FILE.canonicalForm.length;

    // Assert
    // Written out rather than read from anywhere, which is the whole of what
    // makes it a pin: the cases above are generated from the file, so a deleted
    // row removes its own case and takes its failure with it.
    expect(rows).toBe(CANONICAL_FORM_ROWS);
  });
});

// ---------------------------------------------------------------------------
// The two branches.

describe('the frozen derivations off a recovery code', () => {
  it('derives on the two branch labels the frozen file names', () => {
    // Arrange
    // The rows below cannot catch an `info` that moved on both branches at once
    // — they would only see two values that no longer match, and a pair of
    // constants edited together stays self-consistent. These two lines are what
    // say the labels themselves are the contract's.
    const infos = VECTOR_FILE.infos;

    // Act, Assert
    expect(RECOVERY_CODE_BRANCH_INFO.verifier).toBe(infos.verifier);
    expect(RECOVERY_CODE_BRANCH_INFO.keyEncryptionKey).toBe(
      infos.keyEncryptionKey,
    );
  });

  it.each(VECTOR_FILE.derivations)(
    'derives the frozen verifier — $why',
    async ({ code, canonical, verifierBase64Url }) => {
      // Arrange, Act
      const verifier = await recoveryCodeVerifier(code);

      // Assert
      // The canonical form is asserted beside the verifier so a failure says
      // whether the fold moved or the derivation did.
      expect(canonicalRecoveryCode(code)).toBe(canonical);
      expect(verifier).toBe(verifierBase64Url);
    },
  );

  it.each(VECTOR_FILE.derivations)(
    'derives the frozen key-encryption key — $why',
    async ({ code, keyEncryptionKeyHex }) => {
      // Arrange
      // The key is non-extractable, so the frozen hex is imported as a key of
      // this spec's own and both are held against the same ruler: one plaintext,
      // one nonce, one associated data. Equal envelopes mean equal keys.
      const frozen = await importAesKey(fromHex(keyEncryptionKeyHex));

      // A key differing from the frozen one in a single bit, to prove the ruler
      // discriminates. Without it a `sealedUnder` that had quietly stopped
      // depending on its key — a nonce mock that swallowed the seal, an envelope
      // built from constants — would pass this case for saying nothing.
      const bytes = fromHex(keyEncryptionKeyHex);
      bytes[0] ^= 0x01;
      const neighbour = await importAesKey(bytes);

      // Act
      const derived = await keyEncryptionKeyFromRecoveryCode(code);

      const underDerived = await sealedUnder(derived);
      const underFrozen = await sealedUnder(frozen);
      const underNeighbour = await sealedUnder(neighbour);

      // Assert
      // If this goes red the question is never "what is the new value". It is
      // which input changed — the fold, the `info` string, the salt, the output
      // width, the encoding of the code — because each of those makes every
      // account key already wrapped under a recovery code permanently unopenable,
      // while the codes themselves go on redeeming.
      expect(underDerived).toBe(underFrozen);
      expect(underDerived).not.toBe(underNeighbour);
    },
  );

  it('answers one verifier and one key-encryption key for both spellings of the code', async () => {
    // Arrange
    // The whole purpose of the fold, stated in the only terms an account cares
    // about: the printed code and the same code typed back grouped and in lower
    // case derive the identical pair. The two rows are asserted against each
    // other rather than only against the file, because that is the claim — a
    // file stating two equal values proves nothing about an implementation that
    // reached them by two different routes.
    const [printed, typed] = VECTOR_FILE.derivations;

    // Not vacuous: the two rows are genuinely different spellings, and they
    // agree on the canonical form the fold takes them to.
    expect(typed.code).not.toBe(printed.code);
    expect(typed.canonical).toBe(printed.canonical);

    // Act
    const [printedVerifier, typedVerifier] = await Promise.all([
      recoveryCodeVerifier(printed.code),
      recoveryCodeVerifier(typed.code),
    ]);

    const printedEnvelope = await sealedUnder(
      await keyEncryptionKeyFromRecoveryCode(printed.code),
    );
    const typedEnvelope = await sealedUnder(
      await keyEncryptionKeyFromRecoveryCode(typed.code),
    );

    // Assert
    expect(typedVerifier).toBe(printedVerifier);
    expect(typedEnvelope).toBe(printedEnvelope);
  });

  it('still carries every derivation row this suite was written against', () => {
    // Arrange, Act
    const rows = VECTOR_FILE.derivations.length;

    // Assert
    // The case above reads both rows by destructuring, so a file that lost one
    // would leave it comparing a row against `undefined` — and the count is what
    // reports that as a missing row rather than as a broken derivation.
    expect(rows).toBe(DERIVATION_ROWS);
  });
});
