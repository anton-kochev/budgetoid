// The factor manifest — one authenticated blob per account naming every
// recovery factor and the public key it holds, sealed under the account's
// content key.
//
// **Every answer below comes out of `factor-keypair-v1.json`**, computed by a
// third implementation rather than by this codebase, for the reason
// `factor-keypair.spec.ts` gives at its own reader: the manifest is a
// cross-implementation contract — this client writes it, this client reads it
// back, and the server stores bytes it can never open — so a suite that checked
// the implementation against its own output would go green on a format nobody
// else can reproduce. The file is read, never transcribed, and a missing or
// malformed file takes this whole spec down: the parser below throws on every
// shape it does not recognise, including a `lengthBytes` that disagrees with its
// own `hex`. There is deliberately no skip and no default.
//
// **This file parses the frozen plaintext for itself, and that is deliberate.**
// `openFactorManifest` hands back bytes, never entries — comparing the named set
// against the set the server served is a later story, and an exported parser
// with no production caller is the export this codebase refuses by name. So the
// reader below is the spec's own: it walks the framing structurally rather than
// splitting on the separator, because a 65-byte public key holds a `0x1F` with
// probability 0.225 and a split would shear such an entry in half. That is not
// a calculation about hypothetical points — one of the three frozen ones
// contains the byte, and four of the eleven do.
//
// **The order case is the one that cannot be written any other way.** Entries
// ascend by the *canonical spelling* of the factor identifier, and the frozen
// triple was chosen so that two of its identifiers order one way by that
// spelling and the other way under .NET's mixed-endian `Guid.ToByteArray()`.
// An implementation sorting raw UUID bytes agrees with this one on most triples
// and disagrees on this one, which is why the supplied order and the expected
// order are two different lists in the file and why this spec feeds the first
// and asserts the second.
//
// **The count leads, and the eleven-factor vector is what holds it as text.**
// A single-digit count cannot tell a decimal rendering from a raw byte; eleven
// factors — a passkey and a card of ten, which is what a registration actually
// writes — spell it `11`, two characters. The epoch in the associated data
// carries the same hazard and has the same answer, at epoch 10.
//
// **One case reaches for `crypto.subtle` itself, because associated data has no
// other witness.** It is never carried inside an envelope, so nothing this
// module returns contains it; a spy at the cipher boundary is the only place
// the bytes are visible. `restoreMocks` is **not** configured in this project,
// so the `afterEach` below is what keeps that inside its own case.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

import { importAesGcmKey } from './account-keys';
import { UNIT_SEPARATOR } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { FACTOR_PUBLIC_KEY_BYTES } from './factor-keypair';
import {
  FACTOR_MANIFEST_MAX_BYTES,
  FACTOR_MANIFEST_MIN_BYTES,
  openFactorManifest,
  sealFactorManifest,
  type FactorPublicKey,
} from './factor-manifest';
import * as factorManifestModule from './factor-manifest';

// ---------------------------------------------------------------------------
// The frozen vectors.

const VECTOR_FILE_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'factor-keypair-v1.json',
);

/** The hex-and-length shape every entry under `vectors` carries. */
interface FrozenVector {
  /** Several vectors share a `name`, so a selection names both. */
  readonly name: string;
  readonly why: string;
  readonly lengthBytes: number;
  readonly hex: string;
  /**
   * The order a case hands the implementation, where the file states one. It is
   * *not* the order the answer is in — that is the whole subject of the case
   * that uses it.
   */
  readonly suppliedOrder?: readonly string[];
  /** The order the frozen plaintext is in, stated so the case can guard it. */
  readonly expectedOrder?: readonly string[];
  /** Present on the two associated-data messages. */
  readonly rotationEpoch?: number;
}

/**
 * One encoding a client may be handed where a point enters this format.
 *
 * The rows are the same table `factor-keypair.spec.ts` drives its guard from,
 * read here for the one row that no constructed value can stand in for — see
 * {@link HYBRID_POINT}.
 */
interface FrozenPointCase {
  readonly name: string;
  readonly why: string;
  readonly lengthBytes: number;
  readonly hex: string;
  /** IFR-023, measured: what WebCrypto's own `importKey` does with these bytes. */
  readonly platformAccepts: boolean;
  /** IFR-019: what `requireUncompressedPoint` must answer, and nothing more. */
  readonly encodingGuardRefuses: boolean;
}

interface FrozenVectorFile {
  readonly contentKeyHex: string;
  readonly vectors: readonly FrozenVector[];
  readonly pointCases: readonly FrozenPointCase[];
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

function requireString(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = source[key];

  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

// Lower-case hex of an even length and nothing else. Upper-case is refused
// rather than folded: every comparison below is a string comparison against
// this text, so a folded spelling would report a mismatch nobody could locate.
function requireHex(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = requireString(source, key, what);

  if (!/^[0-9a-f]+$/.test(value) || value.length % 2 !== 0) {
    throw new Error(`${what} carries a ${key} that is not lower-case hex.`);
  }

  return value;
}

function requireNumber(
  source: Record<string, unknown>,
  key: string,
  what: string,
): number {
  const value = source[key];

  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new Error(`${what} carries no whole ${key}.`);
  }

  return value;
}

// An optional list of identifiers, checked element by element. Optional because
// only the ordering vector carries them, and refused rather than coerced when
// present: a `suppliedOrder` that lost its shape would otherwise arrive at the
// case as an empty list and seal an empty manifest.
function optionalIdentifiers(
  source: Record<string, unknown>,
  key: string,
  what: string,
): readonly string[] | undefined {
  const value = source[key];

  if (value === undefined) {
    return undefined;
  }

  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`${what} carries a ${key} that is not a non-empty list.`);
  }

  return value.map((entry: unknown, index: number) => {
    if (typeof entry !== 'string' || entry.length === 0) {
      throw new Error(
        `${what} carries a non-identifier in ${key} at ${index}.`,
      );
    }

    return entry;
  });
}

function optionalNumber(
  source: Record<string, unknown>,
  key: string,
  what: string,
): number | undefined {
  return source[key] === undefined
    ? undefined
    : requireNumber(source, key, what);
}

// The file states a length beside every value, and the two are checked against
// one another here rather than trusted. A `hex` edited without its
// `lengthBytes` — or the reverse — is the one corruption of this file that every
// case below would otherwise report as an implementation defect.
function requireAgreedLength(
  hex: string,
  lengthBytes: number,
  what: string,
): void {
  if (hex.length !== lengthBytes * 2) {
    throw new Error(
      `${what} says ${lengthBytes} bytes and carries ${hex.length / 2}.`,
    );
  }
}

function parseVector(value: unknown): FrozenVector {
  if (!isRecord(value)) {
    throw new Error('A vector is not an object.');
  }

  const name = requireString(value, 'name', 'A vector');
  const why = requireString(value, 'why', name);
  const hex = requireHex(value, 'hex', `${name}: ${why}`);
  const lengthBytes = requireNumber(value, 'lengthBytes', `${name}: ${why}`);

  requireAgreedLength(hex, lengthBytes, `${name}: ${why}`);

  return {
    name,
    why,
    lengthBytes,
    hex,
    suppliedOrder: optionalIdentifiers(value, 'suppliedOrder', name),
    expectedOrder: optionalIdentifiers(value, 'expectedOrder', name),
    rotationEpoch: optionalNumber(value, 'rotationEpoch', name),
  };
}

// Refused rather than coerced, for `requireNumber`'s reason: these two booleans
// are what makes a point case the delta row, and a missing one arriving as
// `undefined` would quietly demote the row this file selects on.
function requireBoolean(
  source: Record<string, unknown>,
  key: string,
  what: string,
): boolean {
  const value = source[key];

  if (typeof value !== 'boolean') {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

function parsePointCase(value: unknown): FrozenPointCase {
  if (!isRecord(value)) {
    throw new Error('A point-validation case is not an object.');
  }

  const name = requireString(value, 'name', 'A point-validation case');
  const why = requireString(value, 'why', name);
  const hex = requireHex(value, 'hex', `${name}: ${why}`);
  const lengthBytes = requireNumber(value, 'lengthBytes', `${name}: ${why}`);

  requireAgreedLength(hex, lengthBytes, `${name}: ${why}`);

  return {
    name,
    why,
    lengthBytes,
    hex,
    platformAccepts: requireBoolean(value, 'platformAccepts', name),
    encodingGuardRefuses: requireBoolean(value, 'encodingGuardRefuses', name),
  };
}

function parseVectorFile(text: string): FrozenVectorFile {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The vector file is not an object.');
  }

  const inputs = requireRecord(parsed, 'inputs', 'The vector file');
  const vectors = parsed['vectors'];
  const pointValidation = requireRecord(
    parsed,
    'pointValidation',
    'The vector file',
  );
  const pointCases = pointValidation['cases'];

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The vector file lists no vectors.');
  }

  if (!Array.isArray(pointCases) || pointCases.length === 0) {
    throw new Error('The vector file lists no point-validation cases.');
  }

  return {
    contentKeyHex: requireHex(inputs, 'contentKeyHex', 'inputs'),
    vectors: vectors.map((vector: unknown) => parseVector(vector)),
    pointCases: pointCases.map((entry: unknown) => parsePointCase(entry)),
  };
}

const VECTOR_FILE = parseVectorFile(readFileSync(VECTOR_FILE_PATH, 'utf8'));

// Several vectors share a `name` — two manifest plaintexts differing only in how
// many factors they name, two associated-data messages differing only by epoch —
// so a selection names both halves and refuses anything but a single hit.
// Selecting on the name alone would silently take the first of a pair, which is
// how a case ends up asserting the three-factor answer under the eleven-factor
// arrangement.
function frozenVector(name: string, whyFragment: string): FrozenVector {
  const found = VECTOR_FILE.vectors.filter(
    (vector) => vector.name === name && vector.why.includes(whyFragment),
  );

  if (found.length !== 1) {
    throw new Error(
      `${found.length} vectors named ${name} match "${whyFragment}", not one.`,
    );
  }

  return found[0];
}

// One row of the point-validation table, by name, refusing anything but a
// single hit — a name that stopped matching must take the case down rather than
// arrive as `undefined` and seal nothing.
function frozenPointCase(name: string): FrozenPointCase {
  const found = VECTOR_FILE.pointCases.filter((entry) => entry.name === name);

  if (found.length !== 1) {
    throw new Error(`${found.length} point cases are named ${name}, not one.`);
  }

  return found[0];
}

// **The one encoding the platform accepts and this format must refuse.** It is
// read and never constructed: the hybrid prefix carries a parity bit — `0x06`
// when the point's Y is even and `0x07` when it is odd — so a hand-built `0x06`
// over an arbitrary point is refused by `importKey` for the wrong reason, and a
// case built on one would prove nothing about the delta this guard exists for.
const HYBRID_POINT = frozenPointCase('hybrid-parity-consistent');

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

// The file stores every value as hex; a sealed manifest crosses the wire as
// unpadded base64url. The conversion is `base64url.ts`'s own encoder rather than
// a second one written here — a local encoder that disagreed with the shipped
// one would hand this module input the product never produces.
function toWire(hex: string): string {
  return encodeBase64Url(fromHex(hex));
}

// ---------------------------------------------------------------------------
// Reading a manifest plaintext.
//
// **Structural, never a split on the separator.** A public key is 65 raw bytes
// and `0x1F` occurs inside one often enough that splitting would shear entries
// apart — on the eleven-factor vector it is not a hypothetical. So the reader
// takes the count up to the first separator, then alternates: an identifier up
// to the next separator (an identifier is hex and hyphens, so it holds none),
// one separator, exactly 65 bytes, and either the end or one more separator.
//
// It refuses rather than repairs. Every refusal below is a plaintext this
// module cannot have written, and a reader that skipped to the next plausible
// identifier would turn a framing defect into a shorter set that looks fine.

const SEPARATOR_BYTE = UNIT_SEPARATOR.charCodeAt(0);

// `fatal`, so a byte sequence that is not UTF-8 throws here instead of arriving
// as U+FFFD — a replacement character in an identifier compares unequal to
// everything and says nothing about why.
const utf8Decoder = new TextDecoder('utf-8', { fatal: true });

interface ParsedEntry {
  readonly factorId: string;
  readonly publicKeyHex: string;
}

interface ParsedManifest {
  /** As it was written, text and not a number: this is the case's subject. */
  readonly count: string;
  readonly entries: readonly ParsedEntry[];
}

function parseManifestPlaintext(plaintext: Uint8Array): ParsedManifest {
  const countEnd = plaintext.indexOf(SEPARATOR_BYTE);

  if (countEnd < 0) {
    return { count: utf8Decoder.decode(plaintext), entries: [] };
  }

  const count = utf8Decoder.decode(plaintext.subarray(0, countEnd));
  const entries: ParsedEntry[] = [];

  let at = countEnd + 1;

  while (at < plaintext.length) {
    const identifierEnd = plaintext.indexOf(SEPARATOR_BYTE, at);

    if (identifierEnd < 0) {
      throw new Error('A manifest entry carries no public key.');
    }

    const publicKeyStart = identifierEnd + 1;
    const publicKeyEnd = publicKeyStart + FACTOR_PUBLIC_KEY_BYTES;

    if (publicKeyEnd > plaintext.length) {
      throw new Error("A manifest entry's public key is cut short.");
    }

    entries.push({
      factorId: utf8Decoder.decode(plaintext.subarray(at, identifierEnd)),
      publicKeyHex: toHex(plaintext.subarray(publicKeyStart, publicKeyEnd)),
    });

    at = publicKeyEnd;

    if (at < plaintext.length) {
      if (plaintext[at] !== SEPARATOR_BYTE) {
        throw new Error('A manifest entry is not followed by a separator.');
      }

      at += 1;
    }
  }

  return { count, entries };
}

// The factors a frozen plaintext names, in the order it names them — the input
// side of a case, recovered from the answer rather than transcribed beside it.
// A case then hands them back in whatever order it is testing.
function factorsOf(vector: FrozenVector): readonly FactorPublicKey[] {
  return parseManifestPlaintext(fromHex(vector.hex)).entries.map((entry) => ({
    factorId: entry.factorId,
    publicKey: fromHex(entry.publicKeyHex),
  }));
}

// The same factors, reordered to the list the file supplies. It throws on an
// identifier the plaintext does not name, so a `suppliedOrder` that drifted from
// its own `hex` takes the case down instead of silently sealing a shorter set.
function inSuppliedOrder(
  factors: readonly FactorPublicKey[],
  order: readonly string[],
): readonly FactorPublicKey[] {
  return order.map((factorId) => {
    const found = factors.find((factor) => factor.factorId === factorId);

    if (found === undefined) {
      throw new Error(`The frozen plaintext does not name ${factorId}.`);
    }

    return found;
  });
}

// ---------------------------------------------------------------------------
// Refusals.
//
// **Every refusal case below settles its calls together rather than awaiting
// them one at a time, and that is not a style choice.** A promise that rejects
// while no handler is attached is reported as an unhandled rejection, and the
// runner exits non-zero on one even when every case passed — so the sequential
// form turns a *correct* implementation red for a reason no assertion names.
// `Promise.allSettled` attaches a handler to all of them in the tick they are
// created in, and the assertions become plain state checks.
type Outcome = PromiseSettledResult<unknown>;

function settleAll(
  ...promises: readonly Promise<unknown>[]
): Promise<Outcome[]> {
  return Promise.allSettled(promises);
}

// Asserted as a rejection carrying an error, never merely "not fulfilled", so a
// promise resolving to `undefined` cannot read as a refusal.
function expectRefused(outcome: Outcome, what: string): void {
  expect(outcome.status, `${what} was not refused`).toBe('rejected');

  if (outcome.status === 'rejected') {
    expect(outcome.reason).toBeInstanceOf(Error);
  }
}

// The message of a refusal, for the two cases that have to tell *which* check
// turned a value away — the window this module enforces and the envelope's own
// floor are the same number, so only the wording separates them.
function reasonOf(outcome: Outcome): string {
  return outcome.status === 'rejected' && outcome.reason instanceof Error
    ? outcome.reason.message
    : '';
}

// ---------------------------------------------------------------------------
// The account's content key.

const THREE_FACTORS = frozenVector('manifestPlaintext', 'three factors');
const ELEVEN_FACTORS = frozenVector('manifestPlaintext', 'eleven factors');
const SEALED = frozenVector('manifestSealed', 'CONTENT key');

// Imported through the door the product imports it through, non-extractable,
// rather than through a hand-written `crypto.subtle.importKey` beside the case.
// A fresh array per call because the door wipes what it is handed.
let contentKey: CryptoKey;

beforeAll(async () => {
  contentKey = await importAesGcmKey(fromHex(VECTOR_FILE.contentKeyHex));
});

// **`restoreMocks` is not configured in this project.** The associated-data case
// spies on `crypto.subtle.encrypt`, and a spy that survived its case would leave
// a later `toHaveBeenCalled` passing on this one's history.
afterEach(() => {
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// The plaintext.

describe('sealFactorManifest', () => {
  it('orders entries by the canonical spelling of the factor id, whatever order they arrive in', async () => {
    // Arrange
    // The guard on the arrangement first: if the two lists were the same list,
    // every implementation that keeps insertion order would pass this case.
    const supplied = THREE_FACTORS.suppliedOrder ?? [];
    const expected = THREE_FACTORS.expectedOrder ?? [];

    expect(supplied).not.toEqual(expected);
    expect([...supplied].sort()).toEqual([...expected].sort());

    const factors = inSuppliedOrder(factorsOf(THREE_FACTORS), supplied);

    // Act
    const opened = await openFactorManifest(
      contentKey,
      await sealFactorManifest(contentKey, factors, 1),
      1,
    );

    // Assert
    // The whole plaintext, byte for byte — the count, both separators of every
    // entry, and every point raw. A comparison of the identifier order alone
    // would go green on a manifest that re-encoded its points.
    expect(toHex(opened)).toBe(THREE_FACTORS.hex);

    // And the order again, read back through this file's own parser, so a red
    // bar says which list came out rather than only that 310 bytes differ.
    expect(
      parseManifestPlaintext(opened).entries.map((entry) => entry.factorId),
    ).toEqual(expected);
  });

  it('orders on the folded spelling, so an identifier handed over upper-case lands where its canonical form does', async () => {
    // Arrange
    // **The fold happens before the sort, and only a mixed-case set can see
    // it.** Every identifier in the frozen file is already canonical, so an
    // implementation that ordered the spelling it was handed and folded
    // afterwards writes the frozen plaintext for all of them. Here the middle
    // entry arrives upper-case: ordered as handed it sorts by `F` and lands
    // first, and the answer is a valid-looking manifest with its entries in an
    // order no other client will reproduce.
    const factors = factorsOf(THREE_FACTORS).map((factor, index) =>
      index === 2
        ? {
            factorId: factor.factorId.toUpperCase(),
            publicKey: factor.publicKey,
          }
        : factor,
    );

    expect(factors.map((factor) => factor.factorId)).not.toEqual(
      THREE_FACTORS.expectedOrder,
    );

    // Act
    const opened = await openFactorManifest(
      contentKey,
      await sealFactorManifest(contentKey, factors, 1),
      1,
    );

    // Assert
    // The same 310 bytes as the all-canonical set: the spelling is folded away
    // before anything is ordered or written.
    expect(toHex(opened)).toBe(THREE_FACTORS.hex);
  });

  it('writes the count as decimal text, so eleven factors lead with two characters', async () => {
    // Arrange
    // A passkey and a card of ten, which is what a registration writes. Handed
    // over **reversed**, so the case carries the ordering rule as well: the
    // eleven identifiers ascend in the frozen plaintext, and an implementation
    // that kept insertion order would need every other assertion here to still
    // hold.
    const factors = [...factorsOf(ELEVEN_FACTORS)].reverse();

    expect(factors).toHaveLength(11);

    // Act
    const opened = await openFactorManifest(
      contentKey,
      await sealFactorManifest(contentKey, factors, 1),
      1,
    );

    // Assert
    expect(toHex(opened)).toBe(ELEVEN_FACTORS.hex);

    // The count as it was written, and this is the assertion the vector exists
    // for: `11` is two characters. A count composed as one byte is 0x0b, which
    // is a plaintext of a different width that this file's own reader would
    // still parse.
    expect(parseManifestPlaintext(opened).count).toBe('11');
  });
});

// ---------------------------------------------------------------------------
// Reading one back.

describe('openFactorManifest', () => {
  it('opens the frozen sealed manifest under the frozen content key at the epoch it was sealed at', async () => {
    // Arrange
    // The epoch is the file's, read rather than typed beside it: a case that
    // spelled `1` here and a vector that moved to another epoch would fail with
    // a tag error naming nothing.
    const epoch = SEALED.rotationEpoch ?? 0;

    expect(epoch).toBeGreaterThan(0);

    // Act
    const opened = await openFactorManifest(
      contentKey,
      toWire(SEALED.hex),
      epoch,
    );

    // Assert
    // **This is the case that certifies the whole read path against another
    // implementation.** Nothing in this file sealed these bytes: the nonce, the
    // ciphertext and the tag were computed outside this codebase, so an opener
    // that agreed only with this client's own writer cannot pass it.
    expect(toHex(opened)).toBe(THREE_FACTORS.hex);
  });

  it('refuses the same manifest at another epoch, because the epoch is authenticated and not stored beside it', async () => {
    // Arrange
    const epoch = SEALED.rotationEpoch ?? 0;
    const wire = toWire(SEALED.hex);

    // Act
    // Both in one tick: the legal reading is what makes the refusal mean
    // something, and a rejection awaited a turn later is an unhandled one.
    const [legal, refusal] = await settleAll(
      openFactorManifest(contentKey, wire, epoch),
      openFactorManifest(contentKey, wire, epoch + 1),
    );

    // Assert
    // The pair, not the refusal alone. An implementation that refused every
    // epoch — a mis-built message, a label that drifted — passes the second
    // assertion and fails the first.
    expect(legal.status, 'the manifest at its own epoch').toBe('fulfilled');
    expectRefused(refusal, 'the manifest at the next epoch');
  });
});

// ---------------------------------------------------------------------------
// The width window the server enforces.

describe('the width window', () => {
  it(`refuses a sealed value below ${FACTOR_MANIFEST_MIN_BYTES} bytes in its own words, not the envelope's`, async () => {
    // Arrange
    // One byte short of the floor, which is also one byte short of an AEAD
    // envelope — **the two numbers are the same number**, so the only thing
    // separating this module's refusal from `openEnvelope`'s own is what the
    // message says. Without the wording, a module that checked nothing and let
    // the envelope answer would pass this case.
    const tooShort = encodeBase64Url(
      new Uint8Array(FACTOR_MANIFEST_MIN_BYTES - 1),
    );

    // The other end, on the reading side. One byte past the ceiling is a value
    // the column cannot hold, and the cipher would refuse it too — for the
    // wrong reason, with the wrong message, after doing the work.
    const tooLong = encodeBase64Url(
      new Uint8Array(FACTOR_MANIFEST_MAX_BYTES + 1),
    );
    const shortest = toWire(SEALED.hex);

    // Act
    const [refusal, overCeiling, legal] = await settleAll(
      openFactorManifest(contentKey, tooShort, 1),
      openFactorManifest(contentKey, tooLong, 1),
      openFactorManifest(contentKey, shortest, SEALED.rotationEpoch ?? 0),
    );

    // Assert
    expectRefused(refusal, 'a sealed value below the floor');
    expect(reasonOf(refusal)).toContain('factor manifest');
    expectRefused(overCeiling, 'a sealed value above the ceiling');
    expect(reasonOf(overCeiling)).toContain('factor manifest');
    // The other side of the window, so a module that refused every width — a
    // comparison the wrong way round — cannot pass on the refusal alone.
    expect(legal.status, 'a sealed manifest inside the window').toBe(
      'fulfilled',
    );
  });

  it(`refuses 40 factors, which seal to 4151 bytes, and accepts the 39 that seal to 4048`, async () => {
    // Arrange
    // **Measured, not divided.** An entry costs a separator, a 36-character
    // identifier, a separator and a 65-byte point — 103 bytes — and the count
    // and the envelope add the rest, but the number that matters is the width
    // of the value the server would store, so the case asserts that width
    // rather than the arithmetic that predicts it.
    //
    // The points are well-formed encodings and not points on the curve: this
    // module checks the encoding and never imports one, and a case that drew
    // forty real keypairs would be measuring the platform.
    const [thirtyNine, forty] = [39, 40].map((count) =>
      syntheticFactors(count),
    );

    // Act
    const [legal, refusal] = await settleAll(
      sealFactorManifest(contentKey, thirtyNine, 1),
      sealFactorManifest(contentKey, forty, 1),
    );

    // Assert
    expect(legal.status, '39 factors').toBe('fulfilled');
    expect(
      legal.status === 'fulfilled' && typeof legal.value === 'string'
        ? decodeBase64Url(legal.value).length
        : 0,
      'the widest manifest that fits',
    ).toBe(4048);
    expectRefused(refusal, '40 factors');
    // The measured width is in the message, because the person reading it has
    // just been told that a set they hold cannot be written down.
    expect(reasonOf(refusal)).toContain('4151');
  });
});

// A factor set of `count` entries, each with a distinct canonical identifier and
// a well-formed uncompressed point. The point's coordinates are filler — nothing
// here imports one — and the identifiers are generated rather than drawn so the
// ordering of the set is the same on every run.
function syntheticFactors(count: number): readonly FactorPublicKey[] {
  return Array.from({ length: count }, (unused: undefined, index: number) => {
    const publicKey = new Uint8Array(FACTOR_PUBLIC_KEY_BYTES);

    publicKey[0] = 0x04;
    publicKey[1] = index;

    return {
      factorId: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
      publicKey,
    };
  });
}

// ---------------------------------------------------------------------------
// What is not a factor set.

describe('the entries', () => {
  it('refuses a public key that is not an uncompressed point, through the one guard that owns that rule', async () => {
    // Arrange
    // **Three shapes, and only the third is a rule this format owns.**
    //
    //   * 64 bytes — a raw coordinate pair with the prefix lost somewhere.
    //   * 65 bytes leading `0x03` — `0x02` and `0x03` are the *compressed*
    //     prefixes, which belong to a 33-byte point, so this is a prefix and a
    //     width that do not go together. `crypto.subtle.importKey` refuses it
    //     with `DataError` (measured), which makes it ordinary bad-prefix
    //     coverage rather than the delta row it was once described as.
    //   * The frozen `hybrid-parity-consistent` point — 65 bytes, on the curve,
    //     leading `0x06`, and **accepted by the platform**. This is the row the
    //     guard exists for: nothing downstream turns it away, so a manifest
    //     carrying it would be sealed and stored, naming a point every other
    //     client re-derives a different key from.
    //
    // **The second and third redden on the same mutations here** — this module
    // imports no point, so a local `length !== 65` written in place of the
    // guard admits both — and both are kept anyway, because what they document
    // differs. No implementation of anything produces the second: it is a
    // corruption, refused by every reader in the system including the platform.
    // The third is a point a correct ECDH implementation can hand somebody, and
    // the only thing in the product that turns it away is the guard this case
    // is about.
    const [valid] = syntheticFactors(1);
    const short = { factorId: valid.factorId, publicKey: new Uint8Array(64) };
    const compressedPrefix = {
      factorId: valid.factorId,
      publicKey: Uint8Array.from(valid.publicKey).fill(0x03, 0, 1),
    };
    const hybrid = {
      factorId: valid.factorId,
      publicKey: fromHex(HYBRID_POINT.hex),
    };

    // Act
    const [legal, tooShort, wrongPrefix, platformWouldAccept] = await settleAll(
      sealFactorManifest(contentKey, [valid], 1),
      sealFactorManifest(contentKey, [short], 1),
      sealFactorManifest(contentKey, [compressedPrefix], 1),
      sealFactorManifest(contentKey, [hybrid], 1),
    );

    // Assert
    // The arrangement first, off the frozen table rather than off this file's
    // description of it: the hybrid row really is 65 bytes, really leads a
    // prefix that is not `0x04`, and the file still records it as the row the
    // platform accepts and the encoding rule refuses. A table edit that flipped
    // either boolean would leave the refusal below passing for the wrong
    // reason.
    expect(hybrid.publicKey).toHaveLength(FACTOR_PUBLIC_KEY_BYTES);
    expect(hybrid.publicKey[0]).toBe(0x06);
    expect(HYBRID_POINT.platformAccepts, 'the delta row').toBe(true);
    expect(HYBRID_POINT.encodingGuardRefuses, 'the delta row').toBe(true);

    expect(legal.status, 'an uncompressed point').toBe('fulfilled');
    expectRefused(tooShort, 'a 64-byte public key');
    expectRefused(wrongPrefix, 'a 65-byte public key leading 0x03');
    expectRefused(platformWouldAccept, 'a parity-consistent hybrid point');
    // **The guard's own words, which is the assertion that says the rule was
    // not written a second time here.** `requireUncompressedPoint` is IFR-019
    // and it is imported; a local `length !== 65` beside the caller would pass
    // every other assertion in this case but the hybrid one.
    expect(reasonOf(tooShort)).toContain('uncompressed point');
    expect(reasonOf(wrongPrefix)).toContain('uncompressed point');
    expect(reasonOf(platformWouldAccept)).toContain('uncompressed point');
  });

  it('refuses a factor named twice, however the two spellings differ', async () => {
    // Arrange
    // The repeat arrives upper-case, which is the shape a real one takes: the
    // two spellings name one factor and compare unequal until they are folded.
    // A manifest naming one factor twice is not a set, and the count leading
    // the plaintext would agree with it — which is exactly why nothing
    // downstream would catch it.
    //
    // **The identifiers are the frozen ones and not this file's synthetic
    // ones**, because the synthetic ones are all decimal digits and
    // upper-casing one of those is the identity. A case built on them asserts
    // the fold and exercises a plain string repeat.
    const [first, second] = factorsOf(THREE_FACTORS);
    const repeat = {
      factorId: first.factorId.toUpperCase(),
      publicKey: second.publicKey,
    };

    expect(repeat.factorId).not.toBe(first.factorId);

    // Act
    const [legal, refusal] = await settleAll(
      sealFactorManifest(contentKey, [first, second], 1),
      sealFactorManifest(contentKey, [first, second, repeat], 1),
    );

    // Assert
    expect(legal.status, 'two distinct factors').toBe('fulfilled');
    expectRefused(refusal, 'a factor named twice');
    // By name, because the set came from somewhere and the person fixing it
    // needs to know which entry arrived twice.
    expect(reasonOf(refusal)).toContain(first.factorId);
  });
});

// ---------------------------------------------------------------------------
// The associated data.
//
// **It has no witness but the cipher boundary.** Associated data is
// authenticated and not encrypted, and it is never carried inside the envelope —
// it is re-supplied from where the envelope was found — so nothing this module
// returns contains it and no round trip can see a byte of it. A spy on
// `crypto.subtle.encrypt` that calls through is the only place the message is
// visible, which is the same technique `account-keys.spec.ts` uses on a
// non-extractable key for the same reason.

describe('the associated data', () => {
  // The two frozen messages, and **the second is the whole point of the pair**:
  // a single-digit epoch cannot tell decimal text from a raw byte, because 1
  // renders as one character either way. Ten does.
  const cases = [
    frozenVector('manifestAssociatedData', 'epoch 1:'),
    frozenVector('manifestAssociatedData', 'epoch 10:'),
  ];

  it('refuses an epoch that has no decimal spelling, on both sides', async () => {
    // Arrange
    // **`String` is the whole message for this field, and it does not render
    // every number as digits.** A fractional epoch comes out `1.5`, and one
    // past 1e21 comes out `1e+21` — both are well-formed messages that no
    // server and no other client will ever rebuild, so a manifest sealed under
    // one stores happily and never opens again. The epoch reaches here from the
    // server's stored value plus one, so nothing *today* hands it a number like
    // this; refusing costs one comparison and the alternative has no repair
    // path.
    const factors = factorsOf(THREE_FACTORS);
    const wire = toWire(SEALED.hex);

    // Act
    const [fractional, enormous, reading] = await settleAll(
      sealFactorManifest(contentKey, factors, 1.5),
      sealFactorManifest(contentKey, factors, 1e21),
      openFactorManifest(contentKey, wire, 1.5),
    );

    // Assert
    expectRefused(fractional, 'a fractional epoch');
    expectRefused(enormous, 'an epoch past the safe integers');
    // The reading side too. It builds the same message, so a guard written on
    // one side only would leave the other rebuilding `1.5` and reporting a tag
    // failure.
    expectRefused(reading, 'a fractional epoch on the reading side');
    expect(reasonOf(reading)).toContain('epoch');
  });

  it.each(cases)(
    'binds a manifest to the label, the grammar version and the epoch as decimal digits ($why)',
    async (vector: FrozenVector) => {
      // Arrange
      // Called through, never replaced: the seal has to really happen, or this
      // case would pin a message no envelope was ever bound to.
      const spy = vi.spyOn(crypto.subtle, 'encrypt');

      // Act
      await sealFactorManifest(
        contentKey,
        factorsOf(THREE_FACTORS),
        vector.rotationEpoch ?? 0,
      );

      // Assert
      // One seal, one message. Read off the argument the cipher was handed
      // rather than off anything this module chose to hand back.
      const messages = spy.mock.calls.map((call: readonly unknown[]) =>
        additionalDataOf(call),
      );

      expect(messages).toEqual([vector.hex]);
    },
  );
});

// The `additionalData` of one `crypto.subtle.encrypt` call, as hex.
//
// It reads the argument through `unknown` and refuses everything it does not
// recognise. A cast would let a message that arrived as an `ArrayBuffer`, or as
// nothing at all, read as an empty string — and an empty string is exactly what
// an implementation that bound no associated data would produce.
function additionalDataOf(call: readonly unknown[]): string {
  const [algorithm] = call;

  if (!isRecord(algorithm)) {
    throw new Error('A cipher call carries no algorithm object.');
  }

  const additionalData = algorithm['additionalData'];

  if (!(additionalData instanceof Uint8Array)) {
    throw new Error('A cipher call carries no additionalData bytes.');
  }

  return toHex(additionalData);
}

// ---------------------------------------------------------------------------
// The round trip.

describe('a sealed manifest', () => {
  it('opens into the plaintext it was sealed over, under a nonce drawn afresh each time', async () => {
    // Arrange
    const factors = factorsOf(ELEVEN_FACTORS);

    // Act
    // Sealed twice over one set, which is the only way to see the nonce from
    // out here: the two wire values must differ and the two plaintexts must not.
    const [first, second] = await Promise.all([
      sealFactorManifest(contentKey, factors, 7),
      sealFactorManifest(contentKey, factors, 7),
    ]);
    const [openedFirst, openedSecond] = await Promise.all([
      openFactorManifest(contentKey, first, 7),
      openFactorManifest(contentKey, second, 7),
    ]);

    // Assert
    // A repeated nonce under one key is not a degraded envelope, it is the end
    // of the guarantee — so two seals of one set are two different values.
    expect(second).not.toBe(first);
    expect(toHex(openedSecond)).toBe(toHex(openedFirst));

    // And the plaintext names exactly the set that went in, with each point
    // back as it was handed over. Asserted through this file's own reader
    // rather than against a hex string, because the subject here is the round
    // trip and not the format.
    expect(parseManifestPlaintext(openedFirst).entries).toEqual(
      [...factors]
        .map((factor) => ({
          factorId: factor.factorId,
          publicKeyHex: toHex(factor.publicKey),
        }))
        // `<` and not `localeCompare`, for the reason the module gives at its
        // own sort: a collator is locale- and implementation-dependent, and an
        // expectation built on one would follow the implementation wherever it
        // went instead of holding it to the ordinal order the format has.
        .sort((left, right) => (left.factorId < right.factorId ? -1 : 1)),
    );
  });
});

// ---------------------------------------------------------------------------
// The module surface.
//
// **The backstop `key-import-single-source.spec.ts` names, for this module.**
// That spec is a rule between files and is blind inside a file that already has
// standing; what it cites in its place is a file's export census, and this
// module had none. `factor-keypair.spec.ts` carries the same block and the
// argument is made there in full.
//
// **Every export, not every exported function.** An exported `const`, class or
// object slips past a `typeof value === 'function'` filter, which is the shape
// the leak next door took.
//
// Unlike `factor-keypair.ts`, nothing here could hold a `CryptoKey` to begin
// with: a content key arrives as a parameter, is used inside one call and is
// never stored — so the reachability walk that file runs has no subject here,
// and a census that names every export is what would report one appearing.

describe('the module surface', () => {
  it('offers these names and no others, whatever kind of value each one is', () => {
    // Arrange
    const expected = [
      // The window the server enforces, restated so this client refuses a value
      // before the server does. Both ends are open because the screens that
      // assemble a factor set read them.
      'FACTOR_MANIFEST_MIN_BYTES',
      'FACTOR_MANIFEST_MAX_BYTES',
      // The two functions, and no parser beside them. `openFactorManifest`
      // hands back bytes on purpose — comparing the named set against the
      // served set is story 12.14's, and an exported parser with no production
      // caller is the export this codebase refuses by name. **A third name here
      // is that parser arriving early**, which is the change this census exists
      // to make visible.
      'sealFactorManifest',
      'openFactorManifest',
    ].sort();

    // Act
    const exported = Object.keys(factorManifestModule).sort();

    // Assert
    expect(exported).toEqual(expected);
  });
});
