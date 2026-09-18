// The factor keypair — the ECDH P-256 pair every recovery factor holds, its
// private half *wrapped under* the key-encryption key the factor already
// derives, and the account's two keys *encapsulated to* its public half.
//
// **Every answer below comes out of `factor-keypair-v1.json`**, computed by a
// third implementation (Node's OpenSSL-backed crypto) rather than by this
// codebase or by the server suite. That provenance is the whole value of this
// file: a scheme with this many composed choices — a grammar version that is
// not a suite version, an associated-data message that is a strict prefix of
// another valid message of the same scheme, two public keys in one HKDF info,
// two account keys concatenated in one order — is trivially self-consistent
// while being wrong, and a suite that checked the implementation against its own
// output would go green on a format no other client can reproduce.
//
// **So the file is read, never transcribed, and a missing or malformed file
// takes this whole spec down** rather than leaving cases green under values a
// spec invented for itself. The parser below throws on every shape it does not
// recognise, including a `lengthBytes` that disagrees with its own `hex` — there
// is deliberately no skip, no `describe.skipIf` and no default.
//
// **Two things here cannot be proved by any round trip, and they are why the
// frozen values are two different values.** The 64-byte plaintext inside an
// encapsulated value is `contentKey || indexKey`, content key first; reversed,
// it is the right width, the right version, it stores, it reads back and it
// opens. The only thing that can see the swap is a known answer where the two
// keys differ, asserted in both positions — which is what the first case does.
// The second is the same argument about the factor's own public key: it is
// lifted out of the PKCS#8 encoding at offset 73 rather than taken from
// anything stored or transmitted, so the offset is pinned against the frozen
// PKCS#8 itself instead of against a number somebody chose.
//
// **Two mechanisms answer two different questions about a point, and neither
// subsumes the other.** IFR-019 is an encoding rule — the uncompressed 65-byte
// form and nothing else — and `requireUncompressedPoint` is exactly
// `length !== 65 || point[0] !== 0x04`, a synchronous check and no curve
// arithmetic. IFR-023 is partial public-key validation — off the curve, the
// point at infinity — and `crypto.subtle.importKey` performs it, measured. The
// table is driven on `encodingGuardRefuses`, so two rows here require the guard
// to *accept* bytes the platform later refuses; that is the split, not a gap,
// and a reader who closes it by adding BigInt curve math to the guard reddens
// those rows. The two rows the platform accepts and the encoding rule refuses —
// `hybrid-parity-consistent` and `compressed` — are the guard's whole
// justification, and a census case exists so that deleting either one reddens
// something.
//
// **The `adversarial` section holds what no round trip can produce.** One value
// opens successfully for an implementation that never calls the guard at the
// agreement site, which is the only thing here that can tell an exported guard
// from a called one; the other is a second, wholly independent instance, which
// is the only thing here that can tell an implementation reading its arguments
// from one that hard-codes the first instance's answers.
//
// **Three cases at the bottom reach for `crypto.subtle` itself, and they are the
// only ones that may.** Two of them inject a fault a correct implementation
// cannot be made to produce — a ciphertext that comes back corrupted, a
// `decrypt` that returns a short buffer — because the checks that catch those
// are exactly the checks nothing else in this file can see. One spies on
// `importKey` without changing it, because a non-extractable key has no witness
// but a spy at the platform boundary. `restoreMocks` is **not** configured in
// this project, so the `afterEach` below is what keeps any of that inside its
// own case.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

import { decodeBase64Url, encodeBase64Url } from './base64url';
import {
  ENCAPSULATED_ACCOUNT_KEYS_BYTES,
  FACTOR_PRIVATE_KEY_BYTES,
  FACTOR_PUBLIC_KEY_BYTES,
  FACTOR_PUBLIC_KEY_OFFSET,
  WRAPPED_PRIVATE_KEY_BYTES,
  mintFactorKeypair,
  openFactorKeypair,
  requireUncompressedPoint,
  type FactorKeypairEnvelopes,
} from './factor-keypair';
import * as factorKeypairModule from './factor-keypair';

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
}

/** One encoding a client may be handed where it performs key agreement. */
interface FrozenPointCase {
  readonly name: string;
  readonly why: string;
  readonly lengthBytes: number;
  readonly hex: string;
  /** IFR-023, measured: what WebCrypto's own `importKey` does with these bytes. */
  readonly platformAccepts: boolean;
  /**
   * IFR-019, and exactly `length !== 65 || point[0] !== 0x04` — what
   * `requireUncompressedPoint` must answer, and nothing more than that.
   */
  readonly encodingGuardRefuses: boolean;
  /** Which of the two mechanisms turns this row away, in words. */
  readonly refusedBy: string;
}

/**
 * A value built to be opened successfully by an implementation that skips a
 * check, so that a case asserting refusal fails when the check is missing.
 */
interface FrozenGuardIsActuallyCalled {
  readonly why: string;
  readonly factorId: string;
  readonly keyEncryptionKeyHex: string;
  readonly wrappedPrivateKeyHex: string;
  readonly encapsulatedAccountKeysHex: string;
  readonly ephemeralPointHex: string;
}

/** A wholly independent instance — different key, factor, keypair and keys. */
interface FrozenSecondInstance {
  readonly factorId: string;
  readonly keyEncryptionKeyHex: string;
  readonly contentKeyHex: string;
  readonly indexKeyHex: string;
  readonly factorPublicKeyHex: string;
  readonly wrappedPrivateKeyHex: string;
  readonly encapsulatedAccountKeysHex: string;
}

interface FrozenInputs {
  readonly contentKeyHex: string;
  readonly indexKeyHex: string;
  readonly keyEncryptionKeyHex: string;
  readonly factorId: string;
}

interface FrozenDerived {
  readonly factorPublicKeyHex: string;
  readonly factorPkcs8Hex: string;
  readonly ephemeralPublicKeyHex: string;
  readonly rawSharedSecretHex: string;
}

interface FrozenVectorFile {
  readonly inputs: FrozenInputs;
  readonly derived: FrozenDerived;
  readonly vectors: readonly FrozenVector[];
  readonly pointCases: readonly FrozenPointCase[];
  readonly guardIsActuallyCalled: FrozenGuardIsActuallyCalled;
  readonly secondInstance: FrozenSecondInstance;
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

// The file states a length beside every value, and the two are checked against
// one another here rather than trusted. A `hex` edited without its
// `lengthBytes` — or the reverse — is the one corruption of this file that
// every case below would otherwise report as an implementation defect.
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

  return { name, why, lengthBytes, hex };
}

function parsePointCase(value: unknown): FrozenPointCase {
  if (!isRecord(value)) {
    throw new Error('A point-validation case is not an object.');
  }

  const name = requireString(value, 'name', 'A point-validation case');
  const why = requireString(value, 'why', name);
  const hex = requireHex(value, 'hex', name);
  const lengthBytes = requireNumber(value, 'lengthBytes', name);

  requireAgreedLength(hex, lengthBytes, name);

  return {
    name,
    why,
    lengthBytes,
    hex,
    platformAccepts: requireBoolean(value, 'platformAccepts', name),
    encodingGuardRefuses: requireBoolean(value, 'encodingGuardRefuses', name),
    refusedBy: requireString(value, 'refusedBy', name),
  };
}

function parseGuardIsActuallyCalled(
  source: Record<string, unknown>,
): FrozenGuardIsActuallyCalled {
  const what = 'adversarial.guardIsActuallyCalled';
  const encapsulatedAccountKeysHex = requireHex(
    source,
    'encapsulatedAccountKeysHex',
    what,
  );

  requireAgreedLength(
    encapsulatedAccountKeysHex,
    requireNumber(source, 'lengthBytes', what),
    what,
  );

  return {
    why: requireString(source, 'why', what),
    factorId: requireString(source, 'factorId', what),
    keyEncryptionKeyHex: requireHex(source, 'keyEncryptionKeyHex', what),
    wrappedPrivateKeyHex: requireHex(source, 'wrappedPrivateKeyHex', what),
    encapsulatedAccountKeysHex,
    ephemeralPointHex: requireHex(source, 'ephemeralPointHex', what),
  };
}

function parseSecondInstance(
  source: Record<string, unknown>,
): FrozenSecondInstance {
  const what = 'adversarial.secondInstance';
  const wrappedPrivateKeyHex = requireHex(source, 'wrappedPrivateKeyHex', what);
  const encapsulatedAccountKeysHex = requireHex(
    source,
    'encapsulatedAccountKeysHex',
    what,
  );

  // Both widths are stated beside their values here as they are everywhere else
  // in the file, so both are checked against their own hex rather than trusted.
  requireAgreedLength(
    wrappedPrivateKeyHex,
    requireNumber(source, 'wrappedPrivateKeyLengthBytes', what),
    `${what}.wrappedPrivateKey`,
  );
  requireAgreedLength(
    encapsulatedAccountKeysHex,
    requireNumber(source, 'encapsulatedAccountKeysLengthBytes', what),
    `${what}.encapsulatedAccountKeys`,
  );

  return {
    factorId: requireString(source, 'factorId', what),
    keyEncryptionKeyHex: requireHex(source, 'keyEncryptionKeyHex', what),
    contentKeyHex: requireHex(source, 'contentKeyHex', what),
    indexKeyHex: requireHex(source, 'indexKeyHex', what),
    factorPublicKeyHex: requireHex(source, 'factorPublicKeyHex', what),
    wrappedPrivateKeyHex,
    encapsulatedAccountKeysHex,
  };
}

function parseVectorFile(text: string): FrozenVectorFile {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The vector file is not an object.');
  }

  const inputs = requireRecord(parsed, 'inputs', 'The vector file');
  const derived = requireRecord(parsed, 'derived', 'The vector file');
  const vectors = parsed['vectors'];
  const pointValidation = requireRecord(
    parsed,
    'pointValidation',
    'The vector file',
  );
  const pointCases = pointValidation['cases'];
  const adversarial = requireRecord(parsed, 'adversarial', 'The vector file');
  const guardIsActuallyCalled = requireRecord(
    adversarial,
    'guardIsActuallyCalled',
    'adversarial',
  );
  const secondInstance = requireRecord(
    adversarial,
    'secondInstance',
    'adversarial',
  );

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The vector file lists no vectors.');
  }

  if (!Array.isArray(pointCases) || pointCases.length === 0) {
    throw new Error('The vector file lists no point-validation cases.');
  }

  return {
    inputs: {
      contentKeyHex: requireHex(inputs, 'contentKeyHex', 'inputs'),
      indexKeyHex: requireHex(inputs, 'indexKeyHex', 'inputs'),
      keyEncryptionKeyHex: requireHex(inputs, 'keyEncryptionKeyHex', 'inputs'),
      factorId: requireString(inputs, 'factorId', 'inputs'),
    },
    derived: {
      factorPublicKeyHex: requireHex(derived, 'factorPublicKeyHex', 'derived'),
      factorPkcs8Hex: requireHex(derived, 'factorPkcs8Hex', 'derived'),
      ephemeralPublicKeyHex: requireHex(
        derived,
        'ephemeralPublicKeyHex',
        'derived',
      ),
      rawSharedSecretHex: requireHex(derived, 'rawSharedSecretHex', 'derived'),
    },
    vectors: vectors.map((vector: unknown) => parseVector(vector)),
    pointCases: pointCases.map((entry: unknown) => parsePointCase(entry)),
    guardIsActuallyCalled: parseGuardIsActuallyCalled(guardIsActuallyCalled),
    secondInstance: parseSecondInstance(secondInstance),
  };
}

const VECTOR_FILE = parseVectorFile(readFileSync(VECTOR_FILE_PATH, 'utf8'));

// ---------------------------------------------------------------------------
// The one width this file states as a number, taken from the wire contract.
//
// `account-keys-wire-v1.json` binds the member NAMES of the four messages that
// carry key custody, and restates the widths beside them so that a reader
// binding a member learns in the same breath what it is allowed to be. Both
// suites read it. Every other width below is already read off a frozen value's
// own hex, so this is the only literal there was to take from it — and taking it
// is what keeps the two files from disagreeing about a point's width while each
// agrees with itself. A missing or malformed artifact throws at import.
const WIRE_CONTRACT_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'account-keys-wire-v1.json',
);

function wireWidth(name: string): number {
  const parsed: unknown = JSON.parse(readFileSync(WIRE_CONTRACT_PATH, 'utf8'));

  if (!isRecord(parsed)) {
    throw new Error('The account-key wire contract is not an object.');
  }

  const widths = requireRecord(parsed, 'widths', 'The wire contract');
  const entry = requireRecord(widths, name, "The wire contract's widths");
  const width = requireNumber(entry, 'exactBytes', `widths.${name}`);

  if (width <= 0) {
    throw new Error(`The wire contract's ${name} width is not a width.`);
  }

  return width;
}

const WIRE_FACTOR_PUBLIC_KEY_BYTES = wireWidth('factorPublicKey');

// Several vectors share a `name` — two manifest messages differing only by
// epoch, two wrapped-private-key messages differing only by version — so a
// selection names both halves and refuses anything but a single hit. Selecting
// on the name alone would silently take the first of a pair, which is how a
// case ends up asserting the epoch-1 answer under the epoch-10 arrangement.
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

// The file stores the two envelopes as hex; they cross the wire as unpadded
// base64url. The conversion is `base64url.ts`'s own encoder rather than a
// second one written here — a local encoder that disagreed with the shipped one
// would hand this module input the product never produces.
function toWire(hex: string): string {
  return encodeBase64Url(fromHex(hex));
}

const WRAPPED_PRIVATE_KEY = frozenVector(
  'wrappedPrivateKey',
  'the stored value',
);
const ENCAPSULATED_ACCOUNT_KEYS = frozenVector(
  'encapsulatedAccountKeys',
  'the stored value',
);

const FROZEN_ENVELOPES: FactorKeypairEnvelopes = {
  wrappedPrivateKey: toWire(WRAPPED_PRIVATE_KEY.hex),
  encapsulatedAccountKeys: toWire(ENCAPSULATED_ACCOUNT_KEYS.hex),
};

// The account keys the frozen envelopes were built over, for the round trip.
const FROZEN_ACCOUNT_KEYS = {
  contentKey: fromHex(VECTOR_FILE.inputs.contentKeyHex),
  indexKey: fromHex(VECTOR_FILE.inputs.indexKeyHex),
};

// A second factor, in the one spelling the server accepts. Nothing frozen is
// computed under it: it exists only to be *not* the factor these envelopes were
// bound to, so any canonical value that differs does the job.
//
// **The frozen `factorId` is not a version-4 UUID and that is deliberate** — its
// version and variant nibbles are whatever the vector author typed. This module
// is not the authority on which UUIDs the server may mint, so it folds the
// spelling and checks nothing else; a "fix" that made these vectors RFC 4122
// would be inventing a rule here and would strand every value already bound to
// the identifiers as they are.
const OTHER_FACTOR_ID = '00000000-0000-4000-8000-0000000000ff';

// The plaintext an encapsulated value carries, read off the two frozen keys
// rather than written as 64. It is the discriminator both fault injections
// below use to tell the encapsulation's cipher call from the wrapped private
// key's, so it has to be the file's number and not a number in this spec.
const ACCOUNT_KEYS_PLAINTEXT_BYTES =
  (VECTOR_FILE.inputs.contentKeyHex.length +
    VECTOR_FILE.inputs.indexKeyHex.length) /
  2;

// P-256's field width, which is what a raw ECDH agreement is — read off the
// frozen agreement rather than written as 32, which in this scheme is also the
// width of an account key and of the derived key, for unrelated reasons.
const RAW_AGREEMENT_BYTES = VECTOR_FILE.derived.rawSharedSecretHex.length / 2;

// One `crypto.subtle.importKey` call, read back off the spy. The arguments
// arrive as `unknown` and are narrowed here rather than asserted on in place:
// what matters about each call is four facts, and a case that indexed into a
// tuple would be reading positions instead of naming them.
interface ImportCall {
  readonly format: string;
  readonly width: number;
  readonly algorithm: string;
  readonly extractable: boolean;
  readonly usages: readonly string[];
}

function widthOf(value: unknown): number {
  if (value instanceof ArrayBuffer) {
    return value.byteLength;
  }

  if (ArrayBuffer.isView(value)) {
    return value.byteLength;
  }

  throw new Error('An import was handed key data that is not bytes.');
}

function algorithmNameOf(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }

  if (isRecord(value) && typeof value['name'] === 'string') {
    return value['name'];
  }

  throw new Error('An import was handed an algorithm with no name.');
}

function readImportCall(call: readonly unknown[]): ImportCall {
  const [format, keyData, algorithm, extractable, usages] = call;

  if (typeof format !== 'string') {
    throw new Error('An import was made with no format.');
  }

  if (typeof extractable !== 'boolean') {
    throw new Error('An import was made with no extractable flag.');
  }

  if (!Array.isArray(usages)) {
    throw new Error('An import was made with no usage list.');
  }

  return {
    format,
    width: widthOf(keyData),
    algorithm: algorithmNameOf(algorithm),
    extractable,
    usages: usages.map((usage: unknown) => String(usage)),
  };
}

// The distinct shapes a group of calls took. Distinct rather than listed, so the
// assertion says "every call of this kind carried exactly these arguments"
// without pinning how many of them there were.
function shapesOf(calls: readonly ImportCall[]): readonly unknown[] {
  const shapes = calls.map((call) =>
    JSON.stringify({
      algorithm: call.algorithm,
      extractable: call.extractable,
      usages: call.usages,
      width: call.width,
    }),
  );

  return [...new Set(shapes)].map((shape: string): unknown =>
    JSON.parse(shape),
  );
}

// The adversarial pair. Neither can be produced by a round trip: the first is
// framed around an encoding this client never emits, and the second belongs to
// an instance this suite holds no private material for.
const GUARDED = VECTOR_FILE.guardIsActuallyCalled;
const SECOND = VECTOR_FILE.secondInstance;

const GUARDED_ENVELOPES: FactorKeypairEnvelopes = {
  wrappedPrivateKey: toWire(GUARDED.wrappedPrivateKeyHex),
  encapsulatedAccountKeys: toWire(GUARDED.encapsulatedAccountKeysHex),
};

const SECOND_ENVELOPES: FactorKeypairEnvelopes = {
  wrappedPrivateKey: toWire(SECOND.wrappedPrivateKeyHex),
  encapsulatedAccountKeys: toWire(SECOND.encapsulatedAccountKeysHex),
};

const SECOND_ACCOUNT_KEYS = {
  contentKey: fromHex(SECOND.contentKeyHex),
  indexKey: fromHex(SECOND.indexKeyHex),
};

// The encapsulation framing, for the one case that has to look inside a value
// this spec did not receive an answer for: `version(1) ‖ ephemeral point(65) ‖
// nonce(12) ‖ ciphertext ‖ tag(16)`. The point width is the module's own
// constant rather than a second 65 written here; the nonce width is AES-GCM's.
// **Both offsets are checked against the frozen ephemeral point before they are
// used**, in the case that uses them, so a misread framing reddens there rather
// than quietly comparing two slices of ciphertext.
const AEAD_NONCE_BYTES = 12;
const EPHEMERAL_POINT_OFFSET = 1;
const ENCAPSULATION_NONCE_OFFSET =
  EPHEMERAL_POINT_OFFSET + FACTOR_PUBLIC_KEY_BYTES;

// **Every refusal case below settles its calls together rather than awaiting
// them one at a time, and that is not a style choice.** A promise that rejects
// while no handler is attached is reported as an unhandled rejection, and the
// runner exits non-zero on one even when every case passed — so the sequential
// form (`await expect(legal).resolves…` before the refusal is ever awaited)
// turns a *correct* implementation red for a reason no assertion names.
// Measured against a reference implementation: 25 passing cases, exit 1.
// `Promise.allSettled` attaches a handler to all of them in the tick they are
// created in, and the assertions become plain state checks.
type Outcome = PromiseSettledResult<unknown>;

function settleAll(
  ...promises: readonly Promise<unknown>[]
): Promise<Outcome[]> {
  return Promise.allSettled(promises);
}

// The refusal half of every such pair. Asserted as a rejection carrying an
// error, never merely "not fulfilled", so a promise resolving to `undefined`
// cannot read as a refusal.
function expectRefused(outcome: Outcome, what: string): void {
  expect(outcome.status, `${what} was not refused`).toBe('rejected');

  if (outcome.status === 'rejected') {
    expect(outcome.reason).toBeInstanceOf(Error);
  }
}

function ephemeralPointOf(encapsulated: string): string {
  return toHex(
    decodeBase64Url(encapsulated).slice(
      EPHEMERAL_POINT_OFFSET,
      ENCAPSULATION_NONCE_OFFSET,
    ),
  );
}

function encapsulationNonceOf(encapsulated: string): string {
  return toHex(
    decodeBase64Url(encapsulated).slice(
      ENCAPSULATION_NONCE_OFFSET,
      ENCAPSULATION_NONCE_OFFSET + AEAD_NONCE_BYTES,
    ),
  );
}

// ---------------------------------------------------------------------------
// The key-encryption key.

// Imported once, non-extractable, exactly as the custody path imports the key a
// passkey's PRF output derives. Non-extractable matters to the subject: the
// private half is unwrapped *under* this key and the implementation may never
// need the raw bytes back.
let keyEncryptionKey: CryptoKey;

// The second instance's, imported the same way. Two keys rather than one is the
// whole of what makes an implementation that ignores the argument visible.
let secondKeyEncryptionKey: CryptoKey;

beforeAll(async () => {
  [keyEncryptionKey, secondKeyEncryptionKey] = await Promise.all(
    [VECTOR_FILE.inputs.keyEncryptionKeyHex, SECOND.keyEncryptionKeyHex].map(
      (hex) =>
        crypto.subtle.importKey('raw', fromHex(hex), 'AES-GCM', false, [
          'encrypt',
          'decrypt',
        ]),
    ),
  );
});

// **`restoreMocks` is not configured in this project.** Three cases below spy on
// or stub `crypto.subtle`, and a stub that survived its case would corrupt every
// later cipher call in the file — or, worse, leave a `toHaveBeenCalled` passing
// on an earlier case's history. This is the only thing standing between those
// three cases and the rest of the suite.
afterEach(() => {
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// Opening the frozen pair.

describe('openFactorKeypair', () => {
  it('opens the frozen encapsulated account keys into the frozen content key and index key', async () => {
    // Arrange
    // **Both halves are asserted, and they are asserted to differ.** The
    // plaintext is one 64-byte value split in two, content key first, and a
    // reversed split is the right width, the right version, and opens. Nothing
    // on the server can see it and no round trip can see it — only a known
    // answer whose two halves are different values, checked in both positions.
    const expectedContentKey = VECTOR_FILE.inputs.contentKeyHex;
    const expectedIndexKey = VECTOR_FILE.inputs.indexKeyHex;

    // Act
    const opened = await openFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ENVELOPES,
    );

    // Assert
    // The guard on the arrangement first: two identical frozen keys would make
    // the pair of assertions below one assertion written twice.
    expect(expectedIndexKey).not.toBe(expectedContentKey);
    expect(toHex(opened.contentKey)).toBe(expectedContentKey);
    expect(toHex(opened.indexKey)).toBe(expectedIndexKey);
  });

  it("returns the factor's public key, and it is the one embedded in the frozen PKCS#8 at offset 73", async () => {
    // Arrange
    // **The offset is pinned as a fact about the encoding, not as a number.**
    // A client lifts the factor's public key out of the PKCS#8 it just
    // generated rather than out of anything stored or transmitted, so the
    // constant is checked against the frozen PKCS#8 itself — an
    // implementation that slices at 72 or 74 disagrees with the file here
    // before it ever disagrees with a peer.
    const pkcs8 = fromHex(VECTOR_FILE.derived.factorPkcs8Hex);
    const embedded = pkcs8.slice(
      FACTOR_PUBLIC_KEY_OFFSET,
      FACTOR_PUBLIC_KEY_OFFSET + FACTOR_PUBLIC_KEY_BYTES,
    );

    // Act
    const opened = await openFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ENVELOPES,
    );

    // Assert
    expect(FACTOR_PUBLIC_KEY_OFFSET).toBe(73);
    // The width comes from the wire contract rather than from a 65 typed here,
    // so this client and the server cannot drift apart on how wide a point is
    // while each goes on agreeing with itself. The offset stays a literal: it is
    // a fact about a PKCS#8 encoding, which crosses no wire and which the two
    // assertions beneath this one pin against the frozen key anyway.
    expect(FACTOR_PUBLIC_KEY_BYTES).toBe(WIRE_FACTOR_PUBLIC_KEY_BYTES);
    expect(toHex(opened.publicKey)).toBe(
      VECTOR_FILE.derived.factorPublicKeyHex,
    );
    expect(toHex(embedded)).toBe(VECTOR_FILE.derived.factorPublicKeyHex);
  });

  it('refuses an encapsulated value presented under another factor id', async () => {
    // Arrange
    // One factor's wrapped private key beside another factor's encapsulated
    // value. The wrapped half opens, so the refusal can only come from the
    // encapsulated half's own binding — which is the half that would otherwise
    // hand this factor an account it has no business holding.
    const mine = await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ACCOUNT_KEYS,
    );
    const theirs = await mintFactorKeypair(
      keyEncryptionKey,
      OTHER_FACTOR_ID,
      FROZEN_ACCOUNT_KEYS,
    );
    const mixed: FactorKeypairEnvelopes = {
      wrappedPrivateKey: mine.wrappedPrivateKey,
      encapsulatedAccountKeys: theirs.encapsulatedAccountKeys,
    };

    // Act
    const [legal, refusal] = await settleAll(
      openFactorKeypair(keyEncryptionKey, VECTOR_FILE.inputs.factorId, mine),
      openFactorKeypair(keyEncryptionKey, VECTOR_FILE.inputs.factorId, mixed),
    );

    // Assert
    // The legal call is asserted to succeed first, or an implementation that
    // refused everything — a stub included — would pass the refusal alone.
    expect(legal.status).toBe('fulfilled');
    expectRefused(refusal, "another factor's encapsulated value");
  });

  it('refuses a wrapped private key that belongs to another factor', async () => {
    // Arrange
    // Two refusals of the same rule, because the rule has two ways to break.
    // The mixed pair is another factor's *key*; the frozen pair under another
    // id is this factor's own key under another factor's *binding* — the
    // associated data is re-supplied from where the envelope was found and
    // never carried inside it, so the second is the only arrangement in which
    // the binding is the sole difference.
    const mine = await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ACCOUNT_KEYS,
    );
    const theirs = await mintFactorKeypair(
      keyEncryptionKey,
      OTHER_FACTOR_ID,
      FROZEN_ACCOUNT_KEYS,
    );
    const mixed: FactorKeypairEnvelopes = {
      wrappedPrivateKey: theirs.wrappedPrivateKey,
      encapsulatedAccountKeys: mine.encapsulatedAccountKeys,
    };

    // Act
    const [legal, foreignKey, foreignBinding] = await settleAll(
      openFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ENVELOPES,
      ),
      openFactorKeypair(keyEncryptionKey, VECTOR_FILE.inputs.factorId, mixed),
      openFactorKeypair(keyEncryptionKey, OTHER_FACTOR_ID, FROZEN_ENVELOPES),
    );

    // Assert
    expect(OTHER_FACTOR_ID).not.toBe(VECTOR_FILE.inputs.factorId);
    expect(legal.status).toBe('fulfilled');
    expectRefused(foreignKey, "another factor's wrapped private key");
    expectRefused(foreignBinding, 'the frozen pair under another binding');
  });

  it('opens a second, wholly independent instance into that instances own two keys', async () => {
    // Arrange
    // **Every other value in this file belongs to one instance**, so an
    // implementation that ignores its `keyEncryptionKey` argument, or hard-codes
    // the first instance's answers, passes all of them. This one has a different
    // key-encryption key, a different factor, a different keypair and a
    // different pair of account keys, and it cannot be satisfied by returning
    // anything the earlier cases accept.
    const expectedContentKey = SECOND.contentKeyHex;
    const expectedIndexKey = SECOND.indexKeyHex;

    // Act
    const opened = await openFactorKeypair(
      secondKeyEncryptionKey,
      SECOND.factorId,
      SECOND_ENVELOPES,
    );

    // Assert
    // The four ways the two instances differ, asserted before the answers, so
    // that an instance quietly edited to share the first's material could not
    // make this case pass by being the first case again.
    expect(SECOND.keyEncryptionKeyHex).not.toBe(
      VECTOR_FILE.inputs.keyEncryptionKeyHex,
    );
    expect(SECOND.factorId).not.toBe(VECTOR_FILE.inputs.factorId);
    expect(expectedContentKey).not.toBe(VECTOR_FILE.inputs.contentKeyHex);
    expect(expectedIndexKey).not.toBe(VECTOR_FILE.inputs.indexKeyHex);
    expect(toHex(opened.contentKey)).toBe(expectedContentKey);
    expect(toHex(opened.indexKey)).toBe(expectedIndexKey);
    expect(toHex(opened.publicKey)).toBe(SECOND.factorPublicKeyHex);
  });

  it.each([
    {
      why: 'an encapsulated value one byte short',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: FROZEN_ENVELOPES.wrappedPrivateKey,
        encapsulatedAccountKeys: toWire(
          ENCAPSULATED_ACCOUNT_KEYS.hex.slice(0, -2),
        ),
      }),
    },
    {
      why: 'an encapsulated value with one byte appended',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: FROZEN_ENVELOPES.wrappedPrivateKey,
        encapsulatedAccountKeys: toWire(`${ENCAPSULATED_ACCOUNT_KEYS.hex}00`),
      }),
    },
    {
      why: 'a wrapped private key one byte short',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: toWire(WRAPPED_PRIVATE_KEY.hex.slice(0, -2)),
        encapsulatedAccountKeys: FROZEN_ENVELOPES.encapsulatedAccountKeys,
      }),
    },
    {
      why: 'an empty wrapped private key',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: '',
        encapsulatedAccountKeys: FROZEN_ENVELOPES.encapsulatedAccountKeys,
      }),
    },
    {
      why: 'a wrapped private key padded the way standard base64 pads',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: `${FROZEN_ENVELOPES.wrappedPrivateKey}=`,
        encapsulatedAccountKeys: FROZEN_ENVELOPES.encapsulatedAccountKeys,
      }),
    },
    {
      why: 'an encapsulated value carrying a character outside the alphabet',
      envelopes: (): FactorKeypairEnvelopes => ({
        wrappedPrivateKey: FROZEN_ENVELOPES.wrappedPrivateKey,
        encapsulatedAccountKeys: `${FROZEN_ENVELOPES.encapsulatedAccountKeys.slice(0, -1)}!`,
      }),
    },
  ])('refuses $why', async ({ envelopes }) => {
    // Arrange
    // Two shapes of malformed input in one table, because both arrive the same
    // way — off a row, through one decoder. The widths are fixed, and a reader
    // that repaired a width would be reading a value the peer's own decoder
    // refuses; the wire form is unpadded base64url, and a lenient decoder here
    // accepts an envelope the server rejects, months apart from the request
    // that produced it.
    const malformed = envelopes();

    // Act
    const [legal, refusal] = await settleAll(
      openFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ENVELOPES,
      ),
      openFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        malformed,
      ),
    );

    // Assert
    expect(legal.status).toBe('fulfilled');
    expectRefused(refusal, 'a malformed envelope');
  });
});

// ---------------------------------------------------------------------------
// Minting a pair.

describe('mintFactorKeypair', () => {
  it('mints a pair that opens back into the same two account keys', async () => {
    // Arrange
    // The round trip proves the two ends of *this* implementation agree, which
    // is the one thing the frozen cases above cannot say about a freshly drawn
    // keypair — and, on its own, the one thing a self-consistent wrong scheme
    // also says. So the widths are pinned beside it: both stored values are
    // fixed-length, and a value of any other width is refused by the column
    // before it is refused by a reader.
    const keys = FROZEN_ACCOUNT_KEYS;

    // Act
    const minted = await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      keys,
    );
    const opened = await openFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      minted,
    );

    // Assert
    expect(toHex(opened.contentKey)).toBe(VECTOR_FILE.inputs.contentKeyHex);
    expect(toHex(opened.indexKey)).toBe(VECTOR_FILE.inputs.indexKeyHex);
    expect(toHex(opened.publicKey)).toBe(toHex(minted.publicKey));
    // Decoded through the strict decoder, so the wire form is pinned as
    // unpadded base64url at the same time as the width.
    expect(decodeBase64Url(minted.wrappedPrivateKey)).toHaveLength(
      WRAPPED_PRIVATE_KEY_BYTES,
    );
    expect(decodeBase64Url(minted.encapsulatedAccountKeys)).toHaveLength(
      ENCAPSULATED_ACCOUNT_KEYS_BYTES,
    );
    expect(WRAPPED_PRIVATE_KEY_BYTES).toBe(WRAPPED_PRIVATE_KEY.lengthBytes);
    expect(ENCAPSULATED_ACCOUNT_KEYS_BYTES).toBe(
      ENCAPSULATED_ACCOUNT_KEYS.lengthBytes,
    );
  });

  it('draws a new ephemeral point and a new nonce every time', async () => {
    // Arrange
    // **Nothing in a round trip can see a constant draw.** A mint that reused
    // one ephemeral keypair, or one nonce, under one key-encryption key passes
    // every other case in this file and hands two factors the same AES-GCM
    // (key, nonce) pair — which surrenders the plaintext of both. Two mints
    // under the *same* key and the *same* account keys is the arrangement that
    // shows it: everything an implementation is allowed to repeat is repeated.
    const framing = ENCAPSULATED_ACCOUNT_KEYS.hex;

    // Act
    const first = await mintFactorKeypair(
      secondKeyEncryptionKey,
      SECOND.factorId,
      SECOND_ACCOUNT_KEYS,
    );
    const second = await mintFactorKeypair(
      secondKeyEncryptionKey,
      SECOND.factorId,
      SECOND_ACCOUNT_KEYS,
    );

    // Assert
    // The framing offsets first, against the frozen value's own ephemeral
    // point. Without this the two comparisons below could be slicing
    // ciphertext, where two mints differ for reasons that say nothing about
    // the draw.
    expect(ephemeralPointOf(toWire(framing))).toBe(
      VECTOR_FILE.derived.ephemeralPublicKeyHex,
    );
    expect(ephemeralPointOf(second.encapsulatedAccountKeys)).not.toBe(
      ephemeralPointOf(first.encapsulatedAccountKeys),
    );
    expect(encapsulationNonceOf(second.encapsulatedAccountKeys)).not.toBe(
      encapsulationNonceOf(first.encapsulatedAccountKeys),
    );
    expect(toHex(second.publicKey)).not.toBe(toHex(first.publicKey));
  });

  it('returns the public key in the one encoding the guard accepts', async () => {
    // Arrange
    // What this value is *for*: it goes into the account's factor manifest and
    // into somebody else's HKDF info. A compressed or hybrid encoding here is
    // the same width mismatch the guard refuses at the other end, discovered a
    // rotation later.

    // Act
    const minted = await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ACCOUNT_KEYS,
    );
    const guard = (): void => {
      requireUncompressedPoint(minted.publicKey);
    };

    // Assert
    expect(minted.publicKey).toHaveLength(FACTOR_PUBLIC_KEY_BYTES);
    expect(minted.publicKey[0]).toBe(0x04);
    expect(guard).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// The point guard.

describe('requireUncompressedPoint — IFR-019 encoding only, never IFR-023 curve validation', () => {
  it.each(VECTOR_FILE.pointCases)(
    '$name, refused by $refusedBy — $why',
    ({ hex, encodingGuardRefuses }) => {
      // Arrange
      // **Driven on `encodingGuardRefuses`, which is exactly
      // `length !== 65 || point[0] !== 0x04` and deliberately nothing more.**
      // Two rows here are on-curve failures — `off-curve` and
      // `all-zero-coordinates` — and this guard must *accept* their bytes and
      // let `importKey` refuse them a moment later. That is not an oversight
      // to be tidied up: reimplementing point validation in BigInt would add
      // unvectored cryptography to duplicate a check the platform already
      // makes, and the file's `_twoMechanisms` note argues it. A reader who
      // "fixes" this guard by adding curve arithmetic reddens those two rows.
      //
      // The accepted row is in this table too, so a guard that refused
      // everything fails here rather than passing every refusal for free.
      const point = fromHex(hex);

      // Act
      const call = (): void => {
        requireUncompressedPoint(point);
      };

      // Assert
      if (encodingGuardRefuses) {
        expect(call).toThrow();
      } else {
        expect(call).not.toThrow();
      }
    },
  );

  it('still carries the rows the platform accepts and the encoding rule refuses', () => {
    // Arrange
    // **The only rows that give this guard a purpose.** Everywhere else either
    // the platform refuses the bytes anyway or the encoding is the legal one,
    // so a table without these two is a table a no-op passes. Both are on the
    // curve and both are accepted by `importKey`, measured: only IFR-019 turns
    // them away.

    // Act
    const delta = VECTOR_FILE.pointCases.filter(
      (entry) => entry.platformAccepts && entry.encodingGuardRefuses,
    );
    const names = delta.map((entry) => entry.name);

    // Assert
    expect(delta.length).toBeGreaterThanOrEqual(1);
    expect(names).toContain('hybrid-parity-consistent');
    expect(names).toContain('compressed');
  });

  it('leaves the on-curve failures to the platform, and the table says which', () => {
    // Arrange
    // The other half of the split, pinned so that a later edit cannot quietly
    // hand curve validation to this function by flipping two booleans. These
    // rows are refused by `importKey` alone, and the guard is required to pass
    // their bytes through.

    // Act
    const platformOnly = VECTOR_FILE.pointCases.filter(
      (entry) => !entry.platformAccepts && !entry.encodingGuardRefuses,
    );
    const names = platformOnly.map((entry) => entry.name);

    // Assert
    expect(names).toContain('off-curve');
    expect(names).toContain('all-zero-coordinates');
    // Neither mechanism subsumes the other, so every row is refused by at
    // least one of them — a row refused by neither would be an encoding this
    // scheme accepts, and there is exactly one of those.
    const acceptedByBoth = VECTOR_FILE.pointCases.filter(
      (entry) => entry.platformAccepts && !entry.encodingGuardRefuses,
    );
    expect(acceptedByBoth.map((entry) => entry.name)).toEqual(['uncompressed']);
  });
});

// ---------------------------------------------------------------------------
// The adversarial values — built to open for an implementation that skips a
// check, so that a case asserting refusal fails when the check is missing.

describe('the guard at the agreement site', () => {
  it('refuses a hybrid ephemeral point the platform would accept — the only case that tells an exported guard from a called one', async () => {
    // Arrange
    // **This value opens.** Its ephemeral point is a parity-consistent hybrid
    // encoding used consistently in the framing and in the HKDF info, so an
    // implementation that hands the point straight to `importKey` derives the
    // right key and returns the right account keys — measured, and the file
    // records what those keys would be. Nothing else in this suite can see the
    // difference between a module that *exports* a correct
    // `requireUncompressedPoint` and one that *calls* it where key agreement
    // happens, because every other encapsulated value here is well encoded.
    //
    // What the file's `_opensIfUnguarded` names is deliberately not asserted:
    // it documents why refusal is the right expectation, and asserting on it
    // would be asserting the defect.
    const ephemeral = fromHex(GUARDED.ephemeralPointHex);

    // Act
    const [refusal] = await settleAll(
      openFactorKeypair(keyEncryptionKey, GUARDED.factorId, GUARDED_ENVELOPES),
    );
    const guard = (): void => {
      requireUncompressedPoint(ephemeral);
    };

    // Assert
    // The arrangement first: this really is the 65-byte hybrid form, and it
    // really is the point the value is framed around — otherwise the rejection
    // below could be a width mismatch or a corrupted envelope and would prove
    // nothing about the guard.
    expect(ephemeral).toHaveLength(FACTOR_PUBLIC_KEY_BYTES);
    expect(ephemeral[0]).not.toBe(0x04);
    expect(ephemeralPointOf(GUARDED_ENVELOPES.encapsulatedAccountKeys)).toBe(
      GUARDED.ephemeralPointHex,
    );
    expect(guard).toThrow();
    // The key-encryption key and the factor are the first instance's, so the
    // wrapped private key here opens: the encoding of the ephemeral point is
    // the only thing left for the refusal to come from.
    expect(GUARDED.keyEncryptionKeyHex).toBe(
      VECTOR_FILE.inputs.keyEncryptionKeyHex,
    );
    expect(GUARDED.factorId).toBe(VECTOR_FILE.inputs.factorId);
    expect(GUARDED.wrappedPrivateKeyHex).toBe(WRAPPED_PRIVATE_KEY.hex);
    expectRefused(refusal, 'a hybrid ephemeral point');
  });
});

// ---------------------------------------------------------------------------
// FR-134 — a value is opened before anybody is told it exists.

describe('the re-open at mint', () => {
  it('rejects when the value it just wrote does not open, so a cipher fault surfaces here rather than at the recovery that needed it', async () => {
    // Arrange
    // **The fault this catches is a fault in the *run*, not in the arithmetic**
    // — a corrupted ciphertext, an engine that agreed under a different point
    // than the one it exported. Every one of them produces a pair of envelopes
    // of exactly the right widths that never open, and without this check the
    // discovery happens at the moment somebody has lost every other factor.
    //
    // The stub corrupts **only the encapsulation's** ciphertext. The wrapped
    // private key goes through the same `crypto.subtle.encrypt`, so the two are
    // told apart by the width of the plaintext handed in — 64 for the account's
    // two keys, the PKCS#8's width for the wrap. Corrupting both would leave
    // the case unable to say which check refused.
    const realEncrypt = crypto.subtle.encrypt.bind(crypto.subtle);
    const sealedWidths: number[] = [];

    vi.spyOn(crypto.subtle, 'encrypt').mockImplementation(
      async (algorithm, key, data) => {
        const sealed = await realEncrypt(algorithm, key, data);

        sealedWidths.push(data.byteLength);

        if (data.byteLength !== ACCOUNT_KEYS_PLAINTEXT_BYTES) {
          return sealed;
        }

        const corrupted = new Uint8Array(sealed);
        corrupted[0] ^= 0xff;

        return corrupted.buffer;
      },
    );

    // Act
    const [minted] = await settleAll(
      mintFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ACCOUNT_KEYS,
      ),
    );

    // Assert
    // The arrangement first, and it is the trap in this case: the wrap really
    // was sealed untouched and the encapsulation really was the value that got
    // corrupted. Without these two the refusal could be the wrapped private
    // key's and the case would be testing the wrong half.
    expect(sealedWidths).toContain(FACTOR_PRIVATE_KEY_BYTES);
    expect(sealedWidths).toContain(ACCOUNT_KEYS_PLAINTEXT_BYTES);
    expectRefused(minted, 'a mint whose encapsulated value was corrupted');
  });
});

// ---------------------------------------------------------------------------
// The doors this module opens onto the platform.

describe('the key imports', () => {
  it('imports every key non-extractable, and the private half with deriveBits and nothing else', async () => {
    // Arrange
    // **A non-extractable key has no witness but a spy at the platform
    // boundary**, which is `account-keys.md`'s convention: a spec that pins a
    // door has to name the function. Flipping this flag to `true` changes no
    // value this module computes and no test that only reads its output — and
    // it is the whole of the claim that a private key never leaves here, in any
    // form.
    //
    // A mint exercises the two imports this module writes — the PKCS#8 private
    // half and the peer public point — plus the one `hkdf.ts` makes on its
    // behalf, the raw agreement on its way into the expansion.
    // Asserted over the *arguments* of every call rather than over a count, so
    // a fourth call site added for an unrelated reason does not redden this.
    const spy = vi.spyOn(crypto.subtle, 'importKey');

    // Act
    await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ACCOUNT_KEYS,
    );

    const imports = spy.mock.calls.map((call: readonly unknown[]) =>
      readImportCall(call),
    );
    const privateHalves = imports.filter((call) => call.format === 'pkcs8');
    const peerPoints = imports.filter(
      (call) => call.format === 'raw' && call.algorithm === 'ECDH',
    );
    const derivationInputs = imports.filter(
      (call) => call.format === 'raw' && call.algorithm === 'HKDF',
    );

    // Assert
    // Nothing this module hands the platform may come back out of it.
    expect(imports.filter((call) => call.extractable)).toEqual([]);
    // Each of the three shapes is present, and every call of that shape carries
    // exactly these arguments.
    expect(privateHalves.length).toBeGreaterThanOrEqual(1);
    expect(shapesOf(privateHalves)).toEqual([
      {
        algorithm: 'ECDH',
        extractable: false,
        usages: ['deriveBits'],
        width: FACTOR_PRIVATE_KEY_BYTES,
      },
    ]);
    expect(peerPoints.length).toBeGreaterThanOrEqual(1);
    expect(shapesOf(peerPoints)).toEqual([
      {
        algorithm: 'ECDH',
        extractable: false,
        usages: [],
        width: FACTOR_PUBLIC_KEY_BYTES,
      },
    ]);
    expect(derivationInputs.length).toBeGreaterThanOrEqual(1);
    expect(shapesOf(derivationInputs)).toEqual([
      {
        algorithm: 'HKDF',
        extractable: false,
        usages: ['deriveBits'],
        width: RAW_AGREEMENT_BYTES,
      },
    ]);
  });
});

// ---------------------------------------------------------------------------
// Three widths that are otherwise held only by an AEAD failing.

describe('the widths nothing else names', () => {
  it.each([
    { why: 'a content key of half the width', content: 16, index: 32 },
    { why: 'an index key of half the width', content: 32, index: 16 },
    { why: 'a content key one byte too wide', content: 33, index: 32 },
  ])('refuses to mint under $why', async ({ content, index }) => {
    // Arrange
    // **A short content key does not produce a short envelope.** The plaintext
    // is one 64-byte value split by position, so sixteen bytes shift the index
    // key into the content key's half and pad the rest — and what comes out is
    // storable, openable and wrong, under a content key half of which is zero.
    // Nothing downstream can see it, because every width it meets is the
    // envelope's rather than the key's.
    const narrowed = {
      contentKey: new Uint8Array(content),
      indexKey: new Uint8Array(index),
    };

    // Act
    const [legal, refusal] = await settleAll(
      mintFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ACCOUNT_KEYS,
      ),
      mintFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        narrowed,
      ),
    );

    // Assert
    expect(legal.status).toBe('fulfilled');
    expectRefused(refusal, 'account keys of the wrong width');
  });

  it("refuses an encapsulated value whose plaintext is not the two keys' width", async () => {
    // Arrange
    // **Reachable only by injection, and that is the finding rather than a
    // caveat.** The framing has no length prefix, so a plaintext of any other
    // width is a value of another total width, and the outer width check
    // refuses it first. What is pinned here is the inner check that stands
    // behind it: a platform whose `decrypt` returned a short buffer would
    // otherwise be read as an account key half of which is whatever followed.
    // The wrapped private key's own open is told apart by the width it
    // produces, so only the encapsulation's plaintext is truncated.
    const realDecrypt = crypto.subtle.decrypt.bind(crypto.subtle);
    let truncated = 0;

    vi.spyOn(crypto.subtle, 'decrypt').mockImplementation(
      async (algorithm, key, data) => {
        const opened = await realDecrypt(algorithm, key, data);

        if (opened.byteLength !== ACCOUNT_KEYS_PLAINTEXT_BYTES) {
          return opened;
        }

        truncated += 1;

        return opened.slice(0, ACCOUNT_KEYS_PLAINTEXT_BYTES - 1);
      },
    );

    // Act
    const [refusal] = await settleAll(
      openFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ENVELOPES,
      ),
    );

    // Assert
    expect(truncated).toBe(1);
    expectRefused(refusal, 'a plaintext of the wrong width');
  });

  it('refuses an encapsulated value leading with a version this scheme does not have', async () => {
    // Arrange
    // **This byte is outside the authenticated data**, so nothing in the cipher
    // refuses it: the bytes below decrypt perfectly and hand back the right two
    // keys if the check is missing. Refusing an unknown version now is what
    // lets a version 2 exist later — code that cannot read it must not read it
    // as a version 1.
    const successor = `02${ENCAPSULATED_ACCOUNT_KEYS.hex.slice(2)}`;
    const claimed: FactorKeypairEnvelopes = {
      wrappedPrivateKey: FROZEN_ENVELOPES.wrappedPrivateKey,
      encapsulatedAccountKeys: toWire(successor),
    };

    // Act
    const [legal, refusal] = await settleAll(
      openFactorKeypair(
        keyEncryptionKey,
        VECTOR_FILE.inputs.factorId,
        FROZEN_ENVELOPES,
      ),
      openFactorKeypair(keyEncryptionKey, VECTOR_FILE.inputs.factorId, claimed),
    );

    // Assert
    // Only the first byte moved, and the value is still the frozen width — or
    // the refusal below would be the width check's and this case would pin
    // nothing of its own.
    expect(successor.slice(2)).toBe(ENCAPSULATED_ACCOUNT_KEYS.hex.slice(2));
    expect(decodeBase64Url(claimed.encapsulatedAccountKeys)).toHaveLength(
      ENCAPSULATED_ACCOUNT_KEYS_BYTES,
    );
    expect(legal.status).toBe('fulfilled');
    expectRefused(refusal, 'an unknown encapsulation version');
  });
});

// ---------------------------------------------------------------------------
// The module surface.
//
// **This is the backstop `key-import-single-source.spec.ts` cites, and until
// now this module did not have one.** That spec is a rule *between* files: it
// is blind inside an owner by construction, and it says so — a hand-written
// `crypto.subtle.importKey` written *inside* a file that already has standing
// is caught by the export census of that file, and by nothing else. Measured on
// this module before this block existed: an extractable, unwiped,
// width-unchecked `importKey('raw', …, 'AES-GCM', true, ['encrypt','decrypt'])`
// planted inside `factor-keypair.ts` left the whole suite green.
//
// **It names every export, not every exported *function*.** The census in
// `account-keys.spec.ts` filters `typeof value === 'function'`, which is a hole
// its own comment does not admit to: an exported `const`, class or object slips
// past a check that promises "a new name here is a red test", and the leak the
// case below is about — a module-level array of drawn keys — is exactly that
// shape. So this one is type-blind, and that spec has been extended to match.
//
// Interfaces are absent because they are erased: `FactorKeypairEnvelopes`,
// `MintedFactorKeypair` and `OpenedFactorKeypair` exist only at compile time and
// no runtime reading of the namespace can see them. That is not a gap this
// census could close — a type cannot carry a key.

describe('the module surface', () => {
  it('offers these names and no others, whatever kind of value each one is', () => {
    // Arrange
    // Read the claim exactly: this is not "nothing sensitive is exported".
    // `openFactorKeypair` hands back the account's two keys as bytes by
    // definition, because the doors next door take bytes. What it says is that
    // the set of openings is the set somebody argued for — a helper added in
    // passing is a new name here, which is a red test and a conversation rather
    // than a diff nobody read.
    const expected = [
      // The grammar: one label and one version byte, both part of the
      // definition of every value already stored.
      'FACTOR_KEYPAIR_LABEL',
      'FACTOR_KEYPAIR_VERSION',
      // The four widths and the one offset. Every one of them is read by a
      // neighbour — the manifest's entry width, the wire contract, the two
      // columns — so each is open on purpose rather than by omission.
      'FACTOR_PRIVATE_KEY_BYTES',
      'FACTOR_PUBLIC_KEY_BYTES',
      'FACTOR_PUBLIC_KEY_OFFSET',
      'WRAPPED_PRIVATE_KEY_BYTES',
      'ENCAPSULATED_ACCOUNT_KEYS_BYTES',
      // The two ceremonies.
      'mintFactorKeypair',
      'openFactorKeypair',
      // The encoding guard, open because `factor-manifest.ts` calls it rather
      // than writing a second `length !== 65` beside its own loop. It is the
      // one rule in this module with a second caller.
      'requireUncompressedPoint',
      // **Ten names and no eleventh.** `drawFactorPkcs8`,
      // `importFactorPrivateKey`, `encapsulationKey` and `sealEncapsulated` are
      // deliberately not here: each of the four is a step of a ceremony that
      // only means anything in the order the two exported ones run it, and the
      // first of them is the only frame in this codebase that ever holds an
      // extractable private key.
    ].sort();

    // Act
    const exported = Object.keys(factorKeypairModule).sort();

    // Assert
    expect(exported).toEqual(expected);
  });

  it('hands out no CryptoKey, at the surface or anywhere reachable from it', async () => {
    // Arrange
    // **The claim this holds is the file header's**: a private key never leaves
    // this module — not as bytes, not as an extractable handle, and not as a
    // non-extractable one. Nothing tested it. A reviewer leaked `drawn.privateKey`
    // into an exported module-level array, which is the exact escape the header
    // calls impossible, and the suite answered 598 passed.
    //
    // **A ceremony is run first, so the walk sees a module that has drawn keys**
    // rather than one that has not been asked to yet. A leak parked at import
    // time is a leak this would catch either way; a leak that happens on the
    // path where a key actually exists is the one that needs the mint above it,
    // and it must not depend on which case in this file ran before it.
    await mintFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ACCOUNT_KEYS,
    );
    await openFactorKeypair(
      keyEncryptionKey,
      VECTOR_FILE.inputs.factorId,
      FROZEN_ENVELOPES,
    );

    // Act
    const leaked = cryptoKeysReachableFrom(factorKeypairModule);

    // Assert
    // The control first, and without it this case passes on a detector that can
    // see nothing at all. `keyEncryptionKey` is a real non-extractable
    // `CryptoKey` and it is found through the same walk, parked at the same
    // depth a leak would be.
    expect(
      cryptoKeysReachableFrom({ control: { held: [keyEncryptionKey] } }),
      'the walk cannot see a CryptoKey it is handed, so its silence below means nothing',
    ).toEqual(['control.held[0]']);

    expect(
      leaked,
      `factor-keypair.ts hands out a CryptoKey at ${leaked.join(', ')} — a private key never leaves this module, not as bytes, not as an extractable handle and not as a non-extractable one`,
    ).toEqual([]);

    // **What this does not cover, plainly.** It sees the module's exports and
    // what an ordinary property read reaches from them. A key parked in a
    // module-level variable that is *not* exported is invisible to it, and to
    // every other test that can be written: nothing in the language lets a spec
    // read a module's private scope, and nothing running in this process can
    // enumerate the heap. The same goes for a key captured in a closure. What
    // narrows that gap is not a test — it is that `drawFactorPkcs8` returns
    // bytes rather than a key, so the extractable handle is a local of a frame
    // that has already returned, and the file has nowhere to park one.
  });
});

// Every `CryptoKey` an ordinary property read reaches from `root`, by the path
// it was found at.
//
// **A brand check and never `instanceof`**, for `webauthn-encoding.ts`'s reason
// about `isArrayBuffer`: `instanceof` is a question about one realm's
// constructor, and a key that arrived from another one answers it `false` while
// being perfectly usable. `Object.prototype.toString` reads the
// `Symbol.toStringTag` the platform puts on the class — measured as
// `[object CryptoKey]` on this runner.
//
// Arrays, `Set`s and `Map`s are walked as containers rather than as objects,
// because the leak this exists for is a *collection* of keys and `Object.keys`
// of a `Set` is empty. A `CryptoKey`'s own enumerable properties are empty
// (measured), so a found key is never descended into.
function cryptoKeysReachableFrom(root: unknown): string[] {
  const found: string[] = [];
  const seen = new WeakSet<object>();

  const walk = (value: unknown, path: string): void => {
    if (Object.prototype.toString.call(value) === '[object CryptoKey]') {
      found.push(path);

      return;
    }

    if (
      value === null ||
      (typeof value !== 'object' && typeof value !== 'function')
    ) {
      return;
    }

    if (seen.has(value)) {
      return;
    }

    seen.add(value);

    if (Array.isArray(value)) {
      value.forEach((entry: unknown, index: number) => {
        walk(entry, `${path}[${index}]`);
      });

      return;
    }

    if (value instanceof Set) {
      [...value].forEach((entry: unknown, index: number) => {
        walk(entry, `${path}<set>[${index}]`);
      });

      return;
    }

    if (value instanceof Map) {
      [...value.entries()].forEach(([key, entry]: [unknown, unknown]) => {
        walk(key, `${path}<map key>`);
        walk(entry, `${path}<map>[${String(key)}]`);
      });

      return;
    }

    // In a `try`, because a member reached this way may be an accessor and an
    // accessor may throw. A walk that died on one would report an empty list,
    // which is the answer this case is looking for and must never get by
    // accident.
    let entries: [string, unknown][] = [];

    try {
      entries = Object.entries(value);
    } catch {
      return;
    }

    for (const [name, entry] of entries) {
      walk(entry, `${path}.${name}`);
    }
  };

  walk(root, '');

  // The root's own name is nobody's, so a path always starts at its first
  // member: `.drawn[0]` reads as a slip and `drawn[0]` reads as a finding.
  return found.map((path) => path.replace(/^\./, ''));
}
