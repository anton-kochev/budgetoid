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
// **This file parses the frozen plaintext for itself, and that is deliberate —
// now that `openFactorManifest` parses one too.** It answers the factor set
// rather than the bytes, so the reader below is no longer a stand-in for a
// parser this module lacks; it is the *second* implementation, and its whole
// value is that it was not written by whoever wrote the first. The cases that
// assert bytes therefore open the envelope directly, and the cases that assert
// entries go through the production door — see the two blocks that say so where
// they are.
//
// It walks the framing structurally rather than splitting on the separator,
// because a 65-byte public key holds a `0x1F` with probability 0.2216 and a
// split would shear such an entry in half. **Sixty-four free bytes and not
// sixty-five**: the leading byte of an uncompressed point is always `0x04`, so
// the figure is `1 - (255/256) ** 64` = 0.2216, and the 0.2246 that `** 65`
// gives is about a point this format cannot carry. That is not a calculation
// about hypothetical points either — one of the three frozen ones contains the
// byte, and four of the eleven do.
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
import { canonicalFactorId, isCanonicalFactorId } from './factor-id';
import { FACTOR_PUBLIC_KEY_BYTES } from './factor-keypair';
import {
  FACTOR_MANIFEST_MAX_BYTES,
  FACTOR_MANIFEST_MIN_BYTES,
  openFactorManifest,
  sealFactorManifest,
  type FactorPublicKey,
} from './factor-manifest';
import * as factorManifestModule from './factor-manifest';
import { openEnvelope, sealEnvelope } from './key-envelope';

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

/**
 * One manifest **plaintext** — the value a reader is handed once the tag has
 * verified. Not an envelope, and not an input to the cipher.
 *
 * It is deliberately not a {@link FrozenVector}. `emptyPlaintext` is zero bytes,
 * so its `hex` is the empty string, and {@link requireHex} refuses one on
 * purpose: every other value in this file would be a defect at zero length. A
 * row that is exactly what it claims to be must not take the spec down.
 */
interface FrozenPlaintextCase {
  readonly name: string;
  readonly why: string;
  readonly lengthBytes: number;
  readonly hex: string;
}

interface FrozenVectorFile {
  readonly contentKeyHex: string;
  readonly vectors: readonly FrozenVector[];
  readonly pointCases: readonly FrozenPointCase[];
  /** The plaintexts a manifest reader must refuse. */
  readonly plaintextRefusals: readonly FrozenPlaintextCase[];
  /** The one that is well formed and names nobody. */
  readonly zeroFactors: FrozenPlaintextCase;
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

// Lower-case hex of an even length, **including none of it**. The one caller is
// the plaintext-refusal section, whose `emptyPlaintext` row is legitimately zero
// bytes; `requireHex` stays strict for everything else because an empty value
// anywhere else in this file is a corruption.
function requirePlaintextHex(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = source[key];

  if (typeof value !== 'string' || !/^([0-9a-f]{2})*$/.test(value)) {
    throw new Error(`${what} carries a ${key} that is not lower-case hex.`);
  }

  return value;
}

function parsePlaintextCase(value: unknown, what: string): FrozenPlaintextCase {
  if (!isRecord(value)) {
    throw new Error(`${what} is not an object.`);
  }

  const name = requireString(value, 'name', what);
  const why = requireString(value, 'why', name);
  const hex = requirePlaintextHex(value, 'hex', `${name}: ${why}`);
  const lengthBytes = requireNumber(value, 'lengthBytes', `${name}: ${why}`);

  requireAgreedLength(hex, lengthBytes, `${name}: ${why}`);

  return { name, why, lengthBytes, hex };
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
  const plaintextRefusals = requireRecord(
    parsed,
    'manifestPlaintextRefusals',
    'The vector file',
  );
  const refusalCases = plaintextRefusals['cases'];

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The vector file lists no vectors.');
  }

  if (!Array.isArray(pointCases) || pointCases.length === 0) {
    throw new Error('The vector file lists no point-validation cases.');
  }

  if (!Array.isArray(refusalCases) || refusalCases.length === 0) {
    throw new Error('The vector file lists no manifest plaintext refusals.');
  }

  return {
    contentKeyHex: requireHex(inputs, 'contentKeyHex', 'inputs'),
    vectors: vectors.map((vector: unknown) => parseVector(vector)),
    pointCases: pointCases.map((entry: unknown) => parsePointCase(entry)),
    plaintextRefusals: refusalCases.map((entry: unknown) =>
      parsePlaintextCase(entry, 'A manifest plaintext refusal'),
    ),
    // **Read from its own key and never from the refusal list.** The file keeps
    // `zeroFactors` outside `cases` on purpose — a manifest naming nobody parses
    // cleanly and is answered one layer up by set equality — and a reader that
    // flattened the two into one list would let it be filed as a refusal, which
    // is the specific mistake the file warns about in place.
    zeroFactors: parsePlaintextCase(
      plaintextRefusals['positiveEdgeCase'],
      'The manifest plaintext positive edge case',
    ),
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

// One malformed plaintext, by name, refusing anything but a single hit — a name
// that stopped matching must take the case down rather than arrive as
// `undefined` and seal nothing. A refusal case that silently became a seal of
// zero bytes would be a green case asserting the wrong refusal.
function frozenPlaintextCase(name: string): FrozenPlaintextCase {
  const found = VECTOR_FILE.plaintextRefusals.filter(
    (entry) => entry.name === name,
  );

  if (found.length !== 1) {
    throw new Error(
      `${found.length} manifest plaintext cases are named ${name}, not one.`,
    );
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
// The plaintext, read off the envelope rather than off the manifest reader.
//
// **Every case that asserts bytes opens the envelope directly, and that is a
// decision rather than a convenience.** What those cases assert is a fact about
// the plaintext — the count as decimal text, both separators of every entry,
// every point raw and unre-encoded — and a comparison of parsed *entries* could
// not see a changed separator, a count composed as one byte, or a stray byte
// between two fields. That is the whole of what they exist to catch.
// `openEnvelope` is the door `factor-manifest.ts` itself opens envelopes
// through, so nothing is invented here and no byte-returning member is added to
// that module for a test's benefit — an export with no production caller is the
// export this codebase refuses by name.
//
// **It reaches past the width window on purpose, and nothing is lost.** The
// 29–4096 rule belongs to the manifest reader and `openEnvelope` has no opinion
// about it; the two cases under "the width window" below are the ones that hold
// it, and they go through `openFactorManifest` and assert its wording. No case
// here is leaning on that refusal.
//
// The associated data is the file's frozen message, never rebuilt, for the
// reason this file gives everywhere else: a second spelling of that grammar
// would be kept true by nobody. **That is what pins these cases to the two
// epochs the file freezes a message for.**

const MANIFEST_AD = frozenVector('manifestAssociatedData', 'epoch 1:');
const MANIFEST_AD_TEN = frozenVector('manifestAssociatedData', 'epoch 10:');

// The epoch each frozen message is the message *for*, read rather than typed: a
// case spelling `1` beside a vector that moved would fail with a tag error
// naming nothing.
const MANIFEST_EPOCH = MANIFEST_AD.rotationEpoch ?? 0;
const MANIFEST_EPOCH_TEN = MANIFEST_AD_TEN.rotationEpoch ?? 0;

async function plaintextOf(
  wire: string,
  associatedData: FrozenVector,
): Promise<Uint8Array> {
  return await openEnvelope(
    contentKey,
    decodeBase64Url(wire),
    fromHex(associatedData.hex),
  );
}

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
    // The epoch is the frozen message's own, so the seal and the associated data
    // the assertion opens under cannot drift apart.
    const opened = await plaintextOf(
      await sealFactorManifest(contentKey, factors, MANIFEST_EPOCH),
      MANIFEST_AD,
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
    const opened = await plaintextOf(
      await sealFactorManifest(contentKey, factors, MANIFEST_EPOCH),
      MANIFEST_AD,
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
    const opened = await plaintextOf(
      await sealFactorManifest(contentKey, factors, MANIFEST_EPOCH),
      MANIFEST_AD,
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
    // And it is the epoch the frozen associated-data message below is the
    // message *for*. A sealed vector that moved to an epoch the file states no
    // message for would otherwise fail as a tag error naming nothing, which is
    // the failure the comment above is about.
    expect(epoch).toBe(MANIFEST_EPOCH);

    // Act
    const opened = await plaintextOf(toWire(SEALED.hex), MANIFEST_AD);

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
// The width window, whose two ends are the API edge's as well as this module's.
//
// **Both ends match the layer that enforces them, to the byte.** Every request
// carrying a manifest — a registration, an added passkey, a regenerated card, a
// revoked passkey — hands it to one decoder at the API edge, and that decoder
// measures the decoded bytes against this framing's floor, its version byte and
// the entity's ceiling of 4096. A value this module refuses to write is a value
// that request would be refused for. **The stored rule is wider at the bottom
// on purpose**: `length(manifest) between 1 and 4096` is the column saying
// "this `bytea` is not empty", which is the whole of what a column can state
// declaratively about a blob, and the entity restates exactly that. 29 is a
// total length rather than a byte inside the sealed blob, so the floor is
// applied where the bytes arrive, without anything sealed being read.

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
    //
    // **Epoch 10 and not the 7 this case used to name**, because the plaintext is
    // read off the envelope and the associated data for that read is the file's
    // frozen message rather than one rebuilt here — so the epoch has to be one
    // the file states a message for. Ten is the better of the two: it is the
    // multi-digit one, so the round trip carries the decimal-text rule as well.
    // Nothing in the assertions named 7, and the epoch binding itself is held by
    // its own case above.
    const [first, second] = await Promise.all([
      sealFactorManifest(contentKey, factors, MANIFEST_EPOCH_TEN),
      sealFactorManifest(contentKey, factors, MANIFEST_EPOCH_TEN),
    ]);
    const [openedFirst, openedSecond] = await Promise.all([
      plaintextOf(first, MANIFEST_AD_TEN),
      plaintextOf(second, MANIFEST_AD_TEN),
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
// What a reader is handed once the tag has verified.
//
// **Every case below goes through `openFactorManifest`, never through a parser
// called by hand.** A check that exists but was never wired into the open-then-
// read path refuses nothing, and calling the parser directly is exactly how that
// goes unnoticed until somebody sends a forged manifest. So each case seals the
// frozen malformed plaintext itself — with the product's own `sealEnvelope`,
// under the frozen content key, against the frozen associated-data message — and
// hands the result to the reader the product uses. The tag is therefore correct
// on every one of them, which is the whole point: **six of these are exactly 310
// bytes**, the width of the frozen positive. No width check, no version byte and
// no round trip can tell them apart from the real manifest. Only a refusal
// inside the reader catches them.
//
// **The associated data is read, not rebuilt** — {@link MANIFEST_AD}, for the
// reason given where it is declared. The case above already pins that message as
// the bytes `sealFactorManifest` hands the cipher, so sealing against it is
// sealing against the product's own. Nothing asserts that separately here and
// nothing needs to: every case carries a legal control, and a wrong message
// would fail the control rather than the refusal.

// A plaintext sealed the way the product seals one, handed back on the wire.
async function sealPlaintext(hex: string): Promise<string> {
  return encodeBase64Url(
    await sealEnvelope(contentKey, fromHex(hex), fromHex(MANIFEST_AD.hex)),
  );
}

function refusalWire(name: string): Promise<string> {
  return sealPlaintext(frozenPlaintextCase(name).hex);
}

// The frozen three-factor manifest, sealed here rather than taken from
// `manifestSealed`, so a refusal and its control differ in the plaintext alone
// and in nothing about how either was sealed.
function legalWire(): Promise<string> {
  return sealPlaintext(THREE_FACTORS.hex);
}

// The rejection's value, for the one case that has to say what kind of failure
// it was rather than only that there was one.
function reasonValue(outcome: Outcome): unknown {
  return outcome.status === 'rejected' ? outcome.reason : undefined;
}

// **The bridge between what the reader returns today and what it owes.** It
// reads the answer through `unknown` and refuses everything it does not
// recognise, the way `additionalDataOf` reads a cipher argument: a cast would
// let a `Uint8Array` — which is what this module hands back before story 12.14 —
// arrive as an empty list and read as a manifest naming nobody.
function factorSetOf(value: unknown, what: string): readonly FactorPublicKey[] {
  if (!Array.isArray(value)) {
    throw new Error(`${what} did not answer a list of factors.`);
  }

  return value.map((entry: unknown, index: number) => {
    if (!isRecord(entry)) {
      throw new Error(`${what} carries a non-entry at ${index}.`);
    }

    const factorId = entry['factorId'];
    const publicKey = entry['publicKey'];

    if (typeof factorId !== 'string') {
      throw new Error(`${what} carries an entry with no factorId at ${index}.`);
    }

    if (!(publicKey instanceof Uint8Array)) {
      throw new Error(
        `${what} carries an entry with no publicKey at ${index}.`,
      );
    }

    return { factorId, publicKey };
  });
}

// An answered set in the shape this file's own reader produces, so the two can
// be compared entry by entry — identifiers *and* points. A comparison of the
// identifiers alone goes green on a reader that hands back the right names
// attached to the wrong keys, which is the set an account would then rotate to.
function namedSet(entries: readonly FactorPublicKey[]): readonly ParsedEntry[] {
  return entries.map((entry) => ({
    factorId: entry.factorId,
    publicKeyHex: toHex(entry.publicKey),
  }));
}

// The same shape, off a frozen plaintext, by this file's structural walk.
function namedSetOf(vector: FrozenVector): readonly ParsedEntry[] {
  return parseManifestPlaintext(fromHex(vector.hex)).entries;
}

describe('reading a manifest back', () => {
  it('refuses a plaintext whose entries do not number its count, in either direction', async () => {
    // Arrange
    // **Both are 310 bytes — the width of the real manifest — and both
    // authenticate.** Low is the direction that costs somebody an authenticator:
    // a reader that loops `count` times and stops never sees the third entry, so
    // the account quietly stops naming a factor that still opens it. High is the
    // direction the count field was added for: a reader that walks to the end of
    // the buffer and never compares cannot see truncation at all.
    const [legal, low, high] = await Promise.all([
      legalWire(),
      refusalWire('countLow'),
      refusalWire('countHigh'),
    ]);

    // Act
    const [control, tooFew, tooMany] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, low, MANIFEST_EPOCH),
      openFactorManifest(contentKey, high, MANIFEST_EPOCH),
    );

    // Assert
    // The control first, and it is in every case below for the same reason: a
    // reader that refused every manifest would pass the two refusals alone.
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(tooFew, 'a count naming one fewer entry than are carried');
    expectRefused(tooMany, 'a count naming one more entry than are carried');
  });

  it('refuses a final entry with fewer than a whole public key left', async () => {
    // Arrange
    // The last point is 20 bytes short. `subarray(at, at + 65)` never throws —
    // it answers a short array — so the width has to be checked *after* the
    // slice, and a reader that takes 65 bytes and trusts the take reports a
    // factor holding a 45-byte key.
    const truncated = frozenPlaintextCase('truncatedEntry');

    expect(truncated.lengthBytes).toBeLessThan(THREE_FACTORS.lengthBytes);

    const [legal, short] = await Promise.all([
      legalWire(),
      sealPlaintext(truncated.hex),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, short, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'an entry whose public key is cut short');
  });

  it('refuses a plaintext it did not consume to the last byte', async () => {
    // Arrange
    // A shaved fourth entry on the end with the count left alone. A reader that
    // stops after `count` entries never looks at it, and then the bytes it
    // authenticated and the bytes it read are not the same bytes — which is the
    // gap anything downstream is entitled to assume does not exist.
    const [legal, trailing] = await Promise.all([
      legalWire(),
      refusalWire('trailingBytes'),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, trailing, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'bytes left over past the last entry');
  });

  it('refuses entries that do not ascend by the canonical spelling of the factor id', async () => {
    // Arrange
    // Entries one and two exchanged: same width, same set, same three points.
    //
    // **This case asserts a refusal and must never assert the order that comes
    // back.** A reader that *sorted* what it read instead of refusing would
    // satisfy an order assertion and look correct — and from that moment no
    // second implementation can reproduce these bytes from the set they name,
    // because the order is the only thing making the plaintext a function of the
    // set. The ordering rule is only observable as a refusal.
    const outOfOrder = frozenPlaintextCase('outOfOrder');

    // The arrangement, off the vector rather than off this file's description of
    // it: the same three identifiers as the positive, in a different order.
    expect(
      namedSetOf(THREE_FACTORS).map((entry) => entry.factorId),
    ).not.toEqual(
      parseManifestPlaintext(fromHex(outOfOrder.hex)).entries.map(
        (entry) => entry.factorId,
      ),
    );

    const [legal, unordered] = await Promise.all([
      legalWire(),
      sealPlaintext(outOfOrder.hex),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, unordered, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'entries out of ascending order');
  });

  it('refuses a factor named twice, and says which one, rather than leaving the ordering rule to catch it', async () => {
    // Arrange
    // **Not redundant with the ordering case, and the reason is one character.**
    // Entry two carries entry one's identifier, so the two are *equal* rather
    // than descending: a reader comparing with `<` refuses this on the ordering
    // and never reaches a duplicate check, but relaxing that comparison to `<=`
    // — which reads as a harmless tidy-up — makes the ordering hold and leaves a
    // distinct duplicate refusal as the only thing left. Without one, a `Map`
    // keyed on the identifier lets the last write win, the count still agrees
    // with the number of entries read, and a factor the account serves goes
    // unnamed.
    //
    // So the case asks for the refusal **by name**, which an ordering refusal
    // has no reason to give. That is this module's own convention on the writing
    // side — `requireDistinct` names the offender because a set arrives from
    // somewhere and "a duplicate" without the value is a refusal nobody can act
    // on — and it is the assertion that separates the two rules here.
    const duplicate = frozenPlaintextCase('duplicateFactorId');
    const named = parseManifestPlaintext(fromHex(duplicate.hex)).entries.map(
      (entry) => entry.factorId,
    );
    const repeated = named[0];

    expect(named.filter((factorId) => factorId === repeated)).toHaveLength(2);

    const [legal, twice] = await Promise.all([
      legalWire(),
      sealPlaintext(duplicate.hex),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, twice, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'a factor named twice');
    expect(reasonOf(refusal)).toContain(repeated);
  });

  it('refuses an identifier in any spelling but the canonical one, rather than folding it', async () => {
    // Arrange
    // Entry one upper-cased, same width. **Folding it is the defect**: the
    // manifest is a value two implementations must be able to reproduce from one
    // set, and a reader that accepts a second spelling has given that set two
    // plaintexts. The fold belongs on the *writing* side, where a caller's
    // spelling is an input; on the reading side the bytes are the contract.
    const nonCanonical = frozenPlaintextCase('nonCanonicalFactorId');
    const [named] = parseManifestPlaintext(fromHex(nonCanonical.hex)).entries;

    // The arrangement through the one owner of that spelling, so a case built on
    // a vector that drifted into some other malformation fails here rather than
    // passing for the wrong reason: this identifier is not canonical, and it
    // folds to the identifier the positive carries.
    expect(isCanonicalFactorId(named.factorId)).toBe(false);
    expect(canonicalFactorId(named.factorId)).toBe(
      namedSetOf(THREE_FACTORS)[0].factorId,
    );

    const [legal, misspelled] = await Promise.all([
      legalWire(),
      sealPlaintext(nonCanonical.hex),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, misspelled, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'an identifier in a non-canonical spelling');
  });

  it('refuses a public key that is not an uncompressed point, which nothing below the reader will do', async () => {
    // Arrange
    // Entry one's **own** point, re-prefixed `0x04` to `0x06` under the parity
    // rule — same x, same y, same width, on the curve. The prefix is not
    // borrowed from a sibling entry on purpose: with a borrowed point two
    // entries would share coordinates, and a reader refusing a repeated public
    // key would turn this away for a reason that has nothing to do with
    // encoding.
    //
    // **The platform is not the refusal here.** The Act below imports these very
    // bytes through WebCrypto and they are accepted, so a reader that takes 65
    // bytes positionally and hands them to `importKey` builds a working key from
    // a point every other client re-derives a different key from. Only the
    // encoding guard refuses it.
    const hybrid = frozenPlaintextCase('hybridPoint');
    const point = fromHex(
      parseManifestPlaintext(fromHex(hybrid.hex)).entries[0].publicKeyHex,
    );

    expect(point).toHaveLength(FACTOR_PUBLIC_KEY_BYTES);
    expect(point[0]).toBe(0x06);

    const [legal, reprefixed] = await Promise.all([
      legalWire(),
      sealPlaintext(hybrid.hex),
    ]);

    // Act
    const [control, refusal, platform] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, reprefixed, MANIFEST_EPOCH),
      crypto.subtle.importKey(
        'raw',
        point,
        { name: 'ECDH', namedCurve: 'P-256' },
        false,
        [],
      ),
    );

    // Assert
    // The platform's answer first, because it is what makes the refusal mean
    // something. If this ever starts rejecting, the case has stopped being about
    // the guard and somebody must be told rather than left with a green bar.
    expect(platform.status, 'WebCrypto on the hybrid point').toBe('fulfilled');
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'a manifest carrying a hybrid-encoded point');
    // The guard's own words — `requireUncompressedPoint` is IFR-019 and it is
    // imported. A local `length !== 65` written beside the reader passes every
    // other assertion in this case and fails this one.
    expect(reasonOf(refusal)).toContain('uncompressed point');
  });

  it('refuses a separator at either end', async () => {
    // Arrange
    // The grammar puts one separator between fields and none at either end.
    // Leading is the dangerous half: a split-based reader produces a zero-length
    // first field, `Number('')` is 0, and the value arrives as a manifest naming
    // nobody rather than as a malformed one. Trailing is the quiet half — a
    // reader that trims or ignores it has given one factor set a second
    // spelling.
    const [legal, leading, trailing] = await Promise.all([
      legalWire(),
      refusalWire('leadingSeparator'),
      refusalWire('trailingSeparator'),
    ]);

    // Act
    const [control, atTheFront, atTheBack] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, leading, MANIFEST_EPOCH),
      openFactorManifest(contentKey, trailing, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(atTheFront, 'a separator before the count');
    expectRefused(atTheBack, 'a separator after the last public key');
  });

  it('refuses a count that is not decimal digits, and one with a leading zero', async () => {
    // Arrange
    // **`+3` is the discriminating one.** `Number('+3')` and `parseInt('+3')`
    // are both 3, measured, so every reader that *coerces* the count accepts it
    // and only one that requires decimal digits refuses it. A count of `+`
    // alone would prove nothing — `Number('+')` is `NaN`, so a coercing reader
    // refuses that too, for free and for the wrong reason.
    //
    // `03` is the same rule from the other side: `Number('03')` is 3, so one
    // factor set would have two valid plaintexts — the exact property the
    // decimal spelling exists to prevent.
    const [legal, signed, padded] = await Promise.all([
      legalWire(),
      refusalWire('countNotDigits'),
      refusalWire('countLeadingZero'),
    ]);

    // Act
    const [control, notDigits, leadingZero] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, signed, MANIFEST_EPOCH),
      openFactorManifest(contentKey, padded, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(notDigits, 'a count carrying a sign');
    expectRefused(leadingZero, 'a count with a leading zero');
  });

  it('refuses a count too large to be entries, without sizing a container from it', async () => {
    // Arrange
    // A count of 999,999,999,999,999 over three entries. The refusal that
    // matters is the one that comes from *comparing* the count against what was
    // read, not from failing to allocate for it.
    //
    // **What the second assertion can and cannot say.** Measured: `new
    // Array(n)`, `Array.from({ length: n })`, `new Uint8Array(n)` and `a.length
    // = n` all throw a `RangeError` on this value, and nothing else on this path
    // does — so a `RangeError` here is a reader that sized a container from the
    // declared count before it had read an entry. It does **not** catch a reader
    // that allocates successfully for a merely large count, which no vector in
    // this file exercises; the honest claim is the narrow one, and it is the
    // strongest one available without reaching into the allocator.
    const astronomical = frozenPlaintextCase('countAstronomical');
    const declared = Number(
      parseManifestPlaintext(fromHex(astronomical.hex)).count,
    );

    expect(declared).toBeGreaterThan(2 ** 32);

    const [legal, enormous] = await Promise.all([
      legalWire(),
      sealPlaintext(astronomical.hex),
    ]);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, enormous, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'a count no buffer could hold');
    expect(
      reasonValue(refusal),
      'the count was allocated for',
    ).not.toBeInstanceOf(RangeError);
  });

  it('refuses a plaintext of no bytes at all', async () => {
    // Arrange
    // There is no count field to read. **Nothing beneath the reader turns this
    // away**: sealed, it is a 29-byte envelope, which is the framing floor to
    // the byte and therefore inside the width window this module enforces — so
    // the width check cannot be what refuses it.
    const empty = frozenPlaintextCase('emptyPlaintext');

    expect(empty.lengthBytes).toBe(0);

    const [legal, nothing] = await Promise.all([
      legalWire(),
      sealPlaintext(empty.hex),
    ]);

    expect(decodeBase64Url(nothing)).toHaveLength(FACTOR_MANIFEST_MIN_BYTES);

    // Act
    const [control, refusal] = await settleAll(
      openFactorManifest(contentKey, legal, MANIFEST_EPOCH),
      openFactorManifest(contentKey, nothing, MANIFEST_EPOCH),
    );

    // Assert
    expect(control.status, 'the frozen three-factor manifest').toBe(
      'fulfilled',
    );
    expectRefused(refusal, 'a plaintext of no bytes');
  });

  it('opens the frozen three-factor manifest into the three entries it names, in the frozen order', async () => {
    // Arrange
    // **The sealed value from the file, not one this spec sealed.** The nonce,
    // the ciphertext and the tag were computed outside this codebase, so a
    // reader that agreed only with this client's own writer cannot pass.
    const epoch = SEALED.rotationEpoch ?? 0;

    expect(epoch).toBe(MANIFEST_EPOCH);

    // Act
    const opened: unknown = await openFactorManifest(
      contentKey,
      toWire(SEALED.hex),
      epoch,
    );

    // Assert
    expect(
      Array.isArray(opened),
      'openFactorManifest answers the set the manifest names',
    ).toBe(true);

    const entries = factorSetOf(opened, 'the frozen three-factor manifest');

    // Identifiers *and* points, in order. A reader returning the right number of
    // wrong entries fails here; one returning a byte count never reaches it.
    expect(namedSet(entries)).toEqual(namedSetOf(THREE_FACTORS));
    // And the order once more against the list the file states for itself,
    // rather than against this file's walk of the same bytes — so a red bar says
    // which order came out.
    expect(entries.map((entry) => entry.factorId)).toEqual(
      THREE_FACTORS.expectedOrder,
    );
    expect(entries).toHaveLength(3);
  });

  it('opens the frozen eleven-factor manifest into all eleven', async () => {
    // Arrange
    // A passkey and a card of ten, which is what a registration writes — and the
    // vector that holds the count as *text*, since `11` is two characters. It is
    // also where splitting on the separator stops being a hypothetical: four of
    // these eleven points contain a `0x1F`, so a split sees 29 fields where
    // there are 23 and shears four entries in half.
    const wire = await sealPlaintext(ELEVEN_FACTORS.hex);

    // Act
    const opened: unknown = await openFactorManifest(
      contentKey,
      wire,
      MANIFEST_EPOCH,
    );

    // Assert
    expect(
      Array.isArray(opened),
      'openFactorManifest answers the set the manifest names',
    ).toBe(true);

    const entries = factorSetOf(opened, 'the frozen eleven-factor manifest');

    expect(entries).toHaveLength(11);
    expect(namedSet(entries)).toEqual(namedSetOf(ELEVEN_FACTORS));
    // Every point back raw and whole. A reader that sheared one on a separator
    // hands back a short key, and a `toEqual` over the pair would say only that
    // 11 entries differ.
    for (const entry of entries) {
      expect(entry.publicKey).toHaveLength(FACTOR_PUBLIC_KEY_BYTES);
      expect(entry.publicKey[0]).toBe(0x04);
    }
  });

  it('opens a manifest naming nobody into an empty set', async () => {
    // Arrange
    // **This one is not a refusal, and filing it as one is how somebody makes
    // the parser reject it.** The count `0` and no entries is well formed; what
    // turns it away is set equality one layer up, against the account's live
    // factor set. Make the grammar refuse it and an account midway through
    // losing its last factor cannot be read at all — the one moment its owner
    // most needs to see what is left.
    const zero = VECTOR_FILE.zeroFactors;

    expect(zero.name).toBe('zeroFactors');
    expect(zero.lengthBytes).toBe(1);

    const wire = await sealPlaintext(zero.hex);

    // Act
    const opened: unknown = await openFactorManifest(
      contentKey,
      wire,
      MANIFEST_EPOCH,
    );

    // Assert
    expect(
      Array.isArray(opened),
      'openFactorManifest answers the set the manifest names',
    ).toBe(true);
    expect(factorSetOf(opened, 'a manifest naming nobody')).toEqual([]);
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
//
// **A closed list, and that is the whole of its value.** A name arriving here is
// a deliberate act somebody had to sign off, and the signature is the edit that
// adds it. A sixth reddens this case, which is what a new export is meant to
// cost.

describe('the module surface', () => {
  it('offers these names and no others, whatever kind of value each one is', () => {
    // Arrange
    const expected = [
      // The width window, restated in full at the describe that stands cases on
      // either side of it. Both ends are the API edge's to the byte, restated
      // here so this client refuses a value before the request carrying it
      // would be refused; the floor is the AEAD envelope's own, and the stored
      // rule is wider at the bottom on purpose, `between 1 and 4096` being what
      // a column can state declaratively about a blob. Both ends are open
      // because the screens that assemble a factor set read them.
      'FACTOR_MANIFEST_MIN_BYTES',
      'FACTOR_MANIFEST_MAX_BYTES',
      // **The one type this module hands a caller, and a type is the only honest
      // way to carry what it carries.** `openFactorManifest` refuses twice
      // before any cipher runs — a wire string that is not unpadded base64url,
      // and a sealed value outside the width window — and neither refusal has
      // observed a byte of the account's key material. So the unlock gate
      // catches this type and publishes `unrecognised`, *reload this tab*, while
      // every other refusal from that call — a tag that does not verify, every
      // grammar refusal on an **authenticated** plaintext — stays a bare `Error`
      // and stays `inconsistent`. The alternative is matching a message:
      // messages are prose, several of them exist on each side of the line, and
      // a caller matching one would quietly stop covering the rest. This file
      // already makes that argument for `NarrativeFieldMisuseError` and
      // `AccountKeyResponseError`; this is the third time it has been made and
      // the reasoning has not moved.
      'FactorManifestWireError',
      // The two functions, and **still no parser beside them**, which is the
      // thing this census now holds. `openFactorManifest` parses the plaintext
      // and answers the factor set, so the parser exists — it is simply not a
      // name. Exporting it, or a door back to the bytes beside it, would be the
      // export with no production caller that this codebase refuses by name, and
      // a third function here is what that arriving would look like.
      'sealFactorManifest',
      'openFactorManifest',
    ].sort();

    // Act
    const exported = Object.keys(factorManifestModule).sort();

    // Assert
    expect(exported).toEqual(expected);
  });
});
