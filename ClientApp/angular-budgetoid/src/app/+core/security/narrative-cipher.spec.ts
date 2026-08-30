// The narrative fields — the free text a person types into a ledger — sealed
// under the account's content key, one envelope per field of per row.
//
// The envelope itself is `key-envelope.ts`'s and is pinned there: version (1) ||
// nonce (12) || ciphertext || tag (16), AES-256-GCM. What this module adds is
// the **binding** — which table, which column, which row a ciphertext belongs to
// — and the wire form the column stores. So the cases below are about the
// grammar and about the rendering, and lean on the envelope's own spec for
// everything the envelope already claims.
//
// **Associated data is not carried inside an envelope.** It is rebuilt from
// wherever the ciphertext was found, which is exactly what makes a ciphertext
// moved to another row, another column or another table fail to authenticate
// rather than decrypt into something. Every failure — wrong key, wrong binding,
// altered bytes — is one indistinguishable error by design, so the assertions
// below say "rejects" and never inspect a message: an implementation that told
// the three apart would be handing an attacker the oracle the format exists to
// deny.
//
// **The row id is refused, never folded.** That is the one place this grammar
// deliberately differs from the wrapped-key grammar next door, which folds
// several spellings on the way in and says at its own fold why that tolerance
// stays. Here the value arrives from a row the client just read, in the one
// spelling the row hands back, so there is nothing to be tolerant of — and a
// fold would seal under a spelling that no later read can reproduce.
//
// **Nothing normalises the plaintext.** The client stores exactly what was
// typed, NFD or not, so the round-trip cases below compare bytes and never
// strings-after-normalising. That is also why the frozen vectors carry their
// plaintext as hex: see the note at the vector reader.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';

import { decodeBase64Url, encodeBase64Url } from './base64url';
import {
  ENVELOPE_NONCE_BYTES,
  ENVELOPE_TAG_BYTES,
  ENVELOPE_VERSION,
  sealEnvelope,
} from './key-envelope';
import {
  NARRATIVE_FIELDS,
  NARRATIVE_FIELD_AAD_PREFIX,
  NarrativeFieldMisuseError,
  narrativeFieldAssociatedData,
  openNarrativeField,
  refuseInvalidBinding,
  sealNarrativeField,
} from './narrative-cipher';
import * as narrativeCipherModule from './narrative-cipher';
import type { NarrativeField, NarrativeFieldBinding } from './narrative-cipher';

// The version occupies one byte by definition of the layout, and it is
// deliberately not imported: `key-envelope.ts` does not export it, for the
// reason it gives there — it is the arithmetic the format is made of rather than
// a setting. The nonce and tag widths *are* exported and *are* imported, so the
// only number written down here is the one nobody can change without changing
// the format.
const VERSION_BYTES = 1;

// Spelled by code point and never typed. A raw U+001F is invisible in a diff, in
// a terminal and in most editors, and does not survive ordinary tooling: the
// vector file records that it was silently swallowed twice while those vectors
// were produced, once on a file write and once on a mutation of the generating
// source, each time leaving a plausible string with the separator simply gone.
const UNIT_SEPARATOR = String.fromCharCode(0x1f);

const utf8 = new TextEncoder();

// `fatal: true`, and that is load-bearing rather than tidy. A lenient decoder
// turns bytes that are not UTF-8 into U+FFFD, which is the precise failure the
// opening case below exists to catch; a lenient one here would let this file
// launder the very thing it is checking for.
const strictUtf8 = new TextDecoder('utf-8', { fatal: true });

// Local on purpose. A helper imported from another spec would make this file's
// answers depend on a file it has no reason to be coupled to, and a hex
// rendering is four lines.
function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

// The buffer type is spelled out because `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either.
function fromHex(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(text.match(/../g) ?? [], (pair) => parseInt(pair, 16));
}

// ---------------------------------------------------------------------------
// The frozen vectors.

// From a spec, `process.cwd()` is the Angular project directory, so the vectors
// live two levels up. They are read from `docs/` rather than copied in here so
// that this client and any second implementation are checked against one
// artifact: a value transcribed into a spec is a second copy of the contract,
// and the copy that drifts still passes its own file.
const vectorFilePath = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'narrative-field-v1.json',
);

interface FrozenBinding {
  readonly table: string;
  readonly column: string;
  readonly rowId: string;
}

// A discriminated union rather than a bag of optional fields: a vector either
// carries a sealed answer or it does not, and `kind` is derived by the reader
// below rather than read out of the file. That is what lets the sealing cases
// take `SEALED_VECTORS` and get the four extra members without a `?.` or a
// non-null assertion anywhere.
type FrozenVector =
  | {
      readonly kind: 'binding-only';
      readonly name: string;
      readonly binding: FrozenBinding;
      readonly aadHex: string;
      readonly aadLength: number;
    }
  | {
      readonly kind: 'sealed';
      readonly name: string;
      readonly binding: FrozenBinding;
      readonly aadHex: string;
      readonly aadLength: number;
      readonly plaintextUtf8Hex: string;
      readonly nonceHex: string;
      readonly envelopeHex: string;
      readonly wire: string;
    };

interface FrozenVectorFile {
  readonly contentKeyHex: string;
  readonly vectors: readonly FrozenVector[];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
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

function requireNumber(
  source: Record<string, unknown>,
  key: string,
  what: string,
): number {
  const value = source[key];

  if (typeof value !== 'number') {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

function parseBinding(value: unknown, what: string): FrozenBinding {
  if (!isRecord(value)) {
    throw new Error(`${what} carries no binding.`);
  }

  return {
    table: requireString(value, 'table', what),
    column: requireString(value, 'column', what),
    rowId: requireString(value, 'rowId', what),
  };
}

function parseVector(value: unknown): FrozenVector {
  if (!isRecord(value)) {
    throw new Error('A vector is not an object.');
  }

  const name = requireString(value, 'name', 'A vector');
  const common = {
    name,
    binding: parseBinding(value['binding'], name),
    aadHex: requireString(value, 'aadHex', name),
    aadLength: requireNumber(value, 'aadLength', name),
  };

  // Sealed-ness is decided by one member and the other three are then
  // *required*, so a vector that lost its nonce in an edit is an error here
  // rather than a case that quietly stops being run.
  if (value['plaintextUtf8Hex'] === undefined) {
    return { kind: 'binding-only', ...common };
  }

  return {
    kind: 'sealed',
    ...common,
    plaintextUtf8Hex: requireString(value, 'plaintextUtf8Hex', name),
    nonceHex: requireString(value, 'nonceHex', name),
    envelopeHex: requireString(value, 'envelopeHex', name),
    wire: requireString(value, 'wire', name),
  };
}

// Separate from the read so that the control case below can feed it text and
// see it refuse. A reader that accepted anything would report an empty vector
// list perfectly, and every case keyed on that list would pass by running
// nothing.
function parseVectorFile(text: string): FrozenVectorFile {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The vector file is not an object.');
  }

  const vectors = parsed['vectors'];

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The vector file lists no vectors.');
  }

  return {
    contentKeyHex: requireString(parsed, 'contentKeyHex', 'The vector file'),
    vectors: vectors.map((vector: unknown) => parseVector(vector)),
  };
}

const VECTOR_FILE = parseVectorFile(readFileSync(vectorFilePath, 'utf8'));

function requireVector(name: string): FrozenVector {
  const vector = VECTOR_FILE.vectors.find(
    (candidate) => candidate.name === name,
  );

  if (vector === undefined) {
    throw new Error(`The vector file carries no vector named ${name}.`);
  }

  return vector;
}

function requireSealedVector(
  name: string,
): Extract<FrozenVector, { kind: 'sealed' }> {
  const vector = requireVector(name);

  if (vector.kind !== 'sealed') {
    throw new Error(`The vector named ${name} carries no sealed answer.`);
  }

  return vector;
}

const SEALED_VECTORS = VECTOR_FILE.vectors.filter(
  (vector) => vector.kind === 'sealed',
);
const BINDING_ONLY_VECTOR = requireVector('associated-data-only');
const MIXED_WIDTH_VECTOR = requireSealedVector('mixed-width');

// **`plaintextUtf8Hex` is the normative value and `plaintextForHumans` is a
// caption on it**, which is why nothing in this file reads the caption — not
// even to assert the two agree. A JSON string cannot distinguish NFC from NFD
// and an editor may silently re-normalise it on save; since nothing normalises
// narrative text before sealing, the byte sequence is the contract and the
// readable string is the value that can rot. Asserting they matched would turn
// a damaged caption into a red cipher test.
//
// The seal takes a *string*, so the normative bytes are decoded strictly and the
// resulting string is what gets sealed. With a fatal decoder that step is an
// identity on any valid UTF-8, and the vector case asserts the round trip in hex
// so a decoder that was not an identity would be visible rather than assumed.
function plaintextOf(
  vector: Extract<FrozenVector, { kind: 'sealed' }>,
): string {
  return strictUtf8.decode(fromHex(vector.plaintextUtf8Hex));
}

// ---------------------------------------------------------------------------
// Keys and bindings.

// Every case in this file seals under the frozen content key, the ordinary ones
// included. One key rather than two: a second, spec-local key would be a value
// nothing else in the system agrees with, and using the frozen one means a
// vector file read from the wrong path takes the whole file down instead of
// leaving the ordinary cases green over a key of their own.
//
// Not `async`, so the promise is returned rather than awaited and re-wrapped.
function importContentKey(extractable: boolean): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    fromHex(VECTOR_FILE.contentKeyHex),
    'AES-GCM',
    extractable,
    ['encrypt', 'decrypt'],
  );
}

// The shape production uses, and the shape the signature exists for: a content
// key that can be read back out is a content key that can be logged, posted or
// stored, and the refusal both functions make is the only thing standing between
// this module and one arriving.
function importNonExtractableContentKey(): Promise<CryptoKey> {
  return importContentKey(false);
}

// A second content key, and it is **not** the second key the note above
// refuses. That note is about a second *frozen* key — a fixed value of this
// file's own that neither the vectors nor any other implementation agrees with,
// which would let the ordinary cases stay green over a vector file that failed
// to load. This one is drawn fresh on every call and nothing is ever sealed
// under it: its entire job is to be the wrong key, so there is nothing for
// anybody to agree with and nothing that can drift.
//
// Non-extractable for the same reason production's is. An extractable key would
// be refused before the cipher — which is the *other* half of this file's
// claims, and would make a case about a ciphertext that will not open pass for
// the opposite reason.
function importOtherContentKey(): Promise<CryptoKey> {
  return crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, [
    'encrypt',
    'decrypt',
  ]);
}

// **The list of legal pairs lives in the module, not here.** `NarrativeField` is
// derived from `NARRATIVE_FIELDS` rather than declared beside it, which is what
// makes the type impossible to widen without widening a value — and a value is
// something a test can count. A hand-written union with a hand-written table
// beside it can only be checked by a type-level assertion, and a type-level
// assertion that has quietly stopped asserting looks exactly like one that
// works.
//
// What stays here is the *argument* for each entry: one reason per pair, keyed
// by the pair. The two lists are pinned against each other below in both
// directions, so a ninth field reaching the module with nobody able to say why
// reddens, and a reason for a pair the module has dropped reddens too.
//
// **The limit of that, measured rather than reasoned.** A ninth *entry appended
// to `NARRATIVE_FIELDS`* — the edit somebody adding a narrative field actually
// makes — reddens two cases: `builds a distinct binding for each of the eight
// narrative fields` on the length, and `carries a reason for every legal pair
// and a pair for every reason` on the coverage. But a ninth member **bolted
// onto the derived type past the array**, written as
// `(typeof NARRATIVE_FIELDS)[number] | { table: 'sessions'; column: 'token' }`,
// reddens **nothing at all**: the array still has eight entries, so both counts
// still agree. That was run, not assumed.
//
// So the second shape is **held by review, not by a build error** — the same
// answer `CLAUDE.md` gives about a second account-creating path, and for the
// same reason: it is one line that reddens nothing. It is left that way on
// purpose. The guard available for it reads this spec's own subject as *text*
// and demands the exact declaration, which would catch that one spelling and
// no other way of assembling the same type — a partial guard wearing the face
// of a total one, which is the class of construction the rest of this file
// exists to refuse. Better a named gap than a check that looks like it closed
// it. What makes the gap survivable is that writing "derive it from the list,
// then add one that is not on the list" says out loud what it is doing.
const NARRATIVE_FIELD_REASONS = new Map<string, string>([
  [
    'transactions|description',
    'the memo on a single transaction — the most freely written text in the ledger',
  ],
  [
    'payees|name',
    'who was paid, which names a person or a business outside this account',
  ],
  [
    'accounts|name',
    'what the person calls an account, which often names their bank',
  ],
  ['categories|name', 'the label on a spending category'],
  [
    'categories|description',
    'the longer note saying what belongs in that category',
  ],
  ['category_groups|name', 'the label on a group of categories'],
  ['category_groups|description', 'the longer note on that group'],
  ['budgets|name', 'what the person calls the budget itself'],
]);

// The pair as one string, so two lists of pairs can be compared as sets. The
// separator is a pipe and not the unit separator: this is a spec-local key for
// comparing lists, never anything that reaches an envelope, and borrowing the
// grammar's byte here would invite somebody to read one as the other.
function fieldKey(field: NarrativeField): string {
  return `${field.table}|${field.column}`;
}

// Two canonical row ids, and a third that differs from the first in one hex
// digit. UUIDs rather than free-form labels, which is the shape the grammar
// refuses to do without.
const ROW_A = '0192f8a1-7c3d-7e00-8b2a-3f4d5e6a7b8c';
const ROW_B = '0192f8a1-7c3d-7e00-9c4e-5a6b7c8d9e0f';
const ROW_NEARLY_A = '0192f8a1-7c3d-7e00-8b2a-3f4d5e6a7b8d';

// A binding rebuilt out of the module's own list, so a vector naming a pair the
// module does not have is an error here rather than a cast.
function bindingFor(
  table: string,
  column: string,
  rowId: string,
): NarrativeFieldBinding {
  const field = NARRATIVE_FIELDS.find(
    (candidate) => candidate.table === table && candidate.column === column,
  );

  if (field === undefined) {
    throw new Error(`${table}.${column} is not one of the narrative fields.`);
  }

  return { ...field, rowId };
}

function bindingOf(vector: FrozenVector): NarrativeFieldBinding {
  return bindingFor(
    vector.binding.table,
    vector.binding.column,
    vector.binding.rowId,
  );
}

// ---------------------------------------------------------------------------
// Envelope regions, over the widths `key-envelope.ts` exports.

const SEALED_OFFSET = VERSION_BYTES + ENVELOPE_NONCE_BYTES;
const SHORTEST_ENVELOPE_BYTES = SEALED_OFFSET + ENVELOPE_TAG_BYTES;

function envelopeOf(wire: string): Uint8Array {
  return decodeBase64Url(wire);
}

function nonceRegion(envelope: Uint8Array): Uint8Array {
  return envelope.subarray(VERSION_BYTES, SEALED_OFFSET);
}

function ciphertextRegion(envelope: Uint8Array): Uint8Array {
  return envelope.subarray(SEALED_OFFSET, envelope.length - ENVELOPE_TAG_BYTES);
}

// One byte of the ciphertext flipped, and the wire re-rendered around it.
//
// Inside the region GCM authenticates, deliberately, and not in the version
// byte: a reader is entitled to refuse a version it does not know on shape
// alone, before the tag is ever checked, so damage there would be a different
// failure wearing the same face.
function withAlteredCiphertext(wire: string): string {
  const envelope = Uint8Array.from(envelopeOf(wire));

  envelope[SEALED_OFFSET] ^= 0xff;

  return encodeBase64Url(envelope);
}

// ---------------------------------------------------------------------------
// Reading the failure itself rather than only its presence.

// Hands back whatever a call threw or rejected with — synchronous throws and
// rejected promises alike — and fails loudly if it did neither.
//
// `rejects.toThrow(SomeType)` covers half of what the cases below need. The
// other half is the **absence** of a type on a ciphertext that did not open, and
// a negated matcher chain there reads as a double negative nobody can check, and
// says nothing about the `name` fallback. Holding the thrown value makes both
// halves one shape and lets every assertion be a positive statement about an
// object this file has in its hand.
//
// The `throw` on the no-throw path is what stops the whole family from passing
// vacuously: an `expect` on a value that was never produced is the one way these
// cases could be green while asserting nothing.
async function thrownBy(call: () => unknown): Promise<unknown> {
  try {
    await Promise.resolve(call());
  } catch (error: unknown) {
    return error;
  }

  throw new Error('The call was expected to refuse and returned instead.');
}

// **The cast is the hazard being tested, not a convenience for writing the
// test.** A binding assembled by the compiler cannot carry a table outside
// `NARRATIVE_FIELDS`, which is exactly why the module leaves the table and the
// column to the type system today and checks only the row id at runtime.
//
// That holds while a binding is assembled by the compiler and stops holding the
// moment a table name travels as **data** — out of a configuration, off a
// response, through a view-model mapper the next slice adds — and the one line
// that gets such a value into a binding is this assertion. So these cases build
// the value the way the defect will build it, and ask the module what it does
// with it.
//
// What it costs when nothing refuses is the damage the row id is already checked
// to prevent: a field sealed under associated data no other client will ever
// reproduce, permanently, with no error naming the cause.
function anIllegalBinding(
  table: string,
  column: string,
  rowId: string,
): NarrativeFieldBinding {
  return { table, column, rowId } as unknown as NarrativeFieldBinding;
}

// Every door a binding can arrive at, so a rule held on one of them and not the
// others is visible as a row rather than as an absence.
//
// The two builders take no key and the two operations do; the shape is the wider
// one and the builders ignore what they are handed. `openNarrativeField` is
// given a wire that is never read, because every refusal these cases are about
// sits above the decoder — if one of them ever stops doing so, this argument
// fails as a decoder error rather than passing quietly.
interface BindingEntryPoint {
  readonly where: string;
  readonly call: (key: CryptoKey, binding: NarrativeFieldBinding) => unknown;
}

const BINDING_ENTRY_POINTS: readonly BindingEntryPoint[] = [
  {
    where: 'refuseInvalidBinding',
    call: (key, binding) => refuseInvalidBinding(binding),
  },
  {
    where: 'narrativeFieldAssociatedData',
    call: (key, binding) => narrativeFieldAssociatedData(binding),
  },
  {
    where: 'sealNarrativeField',
    call: (key, binding) => sealNarrativeField(key, 'Coffee', binding),
  },
  {
    where: 'openNarrativeField',
    call: (key, binding) => openNarrativeField(key, 'AQID', binding),
  },
];

describe('the associated data of a narrative field', () => {
  it('is the frozen bytes for a known binding', async () => {
    // Arrange
    // The binding-only vector: the grammar with no cipher over it. Every sealed
    // vector fails with one indistinguishable authentication error, so an
    // implementation with the binding wrong cannot tell that from a key or a
    // mode it has wrong. This case is what separates the two halves.
    const binding = bindingOf(BINDING_ONLY_VECTOR);

    // Act
    const associatedData = narrativeFieldAssociatedData(binding);

    // Assert
    expect(toHex(associatedData)).toBe(BINDING_ONLY_VECTOR.aadHex);
    expect(associatedData).toHaveLength(BINDING_ONLY_VECTOR.aadLength);

    // And the same bytes read as text, so a reviewer sees four fields in one
    // order rather than a wall of hex. The prefix comes off the module's own
    // export and the three fields off the vector file, so this restates nothing
    // — the hex above is what pins the prefix's *value*, and this pins the
    // shape: four fields, that separator, that order.
    expect(strictUtf8.decode(associatedData)).toBe(
      [
        NARRATIVE_FIELD_AAD_PREFIX,
        BINDING_ONLY_VECTOR.binding.table,
        BINDING_ONLY_VECTOR.binding.column,
        BINDING_ONLY_VECTOR.binding.rowId,
      ].join(UNIT_SEPARATOR),
    );

    // The separator cannot occur in any of the four fields, which is what makes
    // "no length prefixes are needed" true. Named here so the claim has a
    // witness rather than only a comment.
    expect(UNIT_SEPARATOR).toHaveLength(1);
    expect(UNIT_SEPARATOR.charCodeAt(0)).toBe(0x1f);

    // `narrativeFieldAssociatedData` is synchronous and `expect` is not, so the
    // `async` above is only for the shape of the block; there is nothing to
    // await. Awaiting the value itself would pass on a promise, which is the one
    // way this case could look green while asserting about the wrong object.
    await Promise.resolve();
  });

  it('builds a distinct binding for each of the eight narrative fields', () => {
    // Arrange
    // One row id across all eight, so the only thing that can separate them is
    // the pair. It subsumes "two tables sharing a column name": `payees.name`,
    // `accounts.name`, `categories.name`, `category_groups.name` and
    // `budgets.name` are five entries whose column is the same word.

    // Act
    const hexes = NARRATIVE_FIELDS.map((field) =>
      toHex(narrativeFieldAssociatedData({ ...field, rowId: ROW_A })),
    );

    // Assert
    // The list's own size first, and it is a real assertion rather than a
    // restatement because `NarrativeField` is *derived* from this array. A
    // ninth pair cannot enter the type without entering the value, and the
    // value is what this counts — which is the whole reason the module owns the
    // list and this file does not keep a copy of it.
    //
    // Without the length, a list that lost an entry would still have as many
    // distinct hexes as it had entries, and the set check below would be green
    // over seven fields.
    expect(NARRATIVE_FIELDS).toHaveLength(8);
    expect(new Set(hexes).size).toBe(8);
  });

  it('carries a reason for every legal pair and a pair for every reason', () => {
    // Arrange
    // The two lists compared as sets, both directions, because each direction
    // catches a different mistake and neither catches the other's.
    //
    // A ninth field arriving in the module with nobody able to say why it is
    // encrypted fails the first direction. That is the shape the mistake takes:
    // the list is the requirement's list of encrypted columns, and a table added
    // in passing — a session token, an audit row — would be sealed under the
    // account's content key with a grammar the server never agreed to.
    //
    // A reason left behind for a pair the module has dropped fails the second.
    // That one is quieter and worth catching anyway: an argument for a field
    // that no longer exists is the residue that makes the next reader believe
    // the list is longer than it is.
    const legal = NARRATIVE_FIELDS.map(fieldKey).sort();
    const argued = [...NARRATIVE_FIELD_REASONS.keys()].sort();

    // Act, Assert
    expect(legal).toEqual(argued);

    // And every reason says something. An empty string is a row somebody added
    // to make this case pass, which is the one way to satisfy it without having
    // the conversation it exists to force.
    for (const [pair, reason] of NARRATIVE_FIELD_REASONS) {
      expect(reason.length, `${pair} carries no reason.`).toBeGreaterThan(0);
    }
  });

  it('tells one column apart from another on the same row', () => {
    // Arrange
    // The pair that makes the column field load-bearing: same table, same row,
    // two columns that both hold narrative text. Without the column in the
    // grammar, a category's name and its description are interchangeable
    // ciphertexts — and swapping them is a silent, successful decryption.
    const name: NarrativeFieldBinding = {
      table: 'categories',
      column: 'name',
      rowId: ROW_A,
    };
    const description: NarrativeFieldBinding = {
      table: 'categories',
      column: 'description',
      rowId: ROW_A,
    };

    // Act
    const forName = narrativeFieldAssociatedData(name);
    const forDescription = narrativeFieldAssociatedData(description);

    // Assert
    expect(toHex(forName)).not.toBe(toHex(forDescription));
  });

  it('tells one row apart from another in the same column', () => {
    // Arrange
    // One hex digit apart, so a comparison that looked at a prefix of the row id
    // — a truncation, a first-segment shortcut — is caught here rather than by
    // a pair that differs everywhere.
    const first: NarrativeFieldBinding = {
      table: 'transactions',
      column: 'description',
      rowId: ROW_A,
    };
    const second: NarrativeFieldBinding = {
      table: 'transactions',
      column: 'description',
      rowId: ROW_NEARLY_A,
    };

    // Act
    const forFirst = narrativeFieldAssociatedData(first);
    const forSecond = narrativeFieldAssociatedData(second);

    // Assert
    expect(toHex(forFirst)).not.toBe(toHex(forSecond));
  });

  it.each([
    // Upper-case hex. The spelling a client that round-trips a row id through a
    // native UUID type is most likely to hand back.
    { spelling: 'upper-case hex', rowId: ROW_A.toUpperCase() },
    // One nibble in the other case is enough — a fold applied to part of a value
    // is the shape a partial normalisation takes.
    { spelling: 'mixed case', rowId: '0192F8a1-7c3d-7e00-8b2a-3f4d5e6a7b8c' },
    { spelling: 'the brace-wrapped form', rowId: `{${ROW_A}}` },
    { spelling: 'the parenthesis-wrapped form', rowId: `(${ROW_A})` },
    { spelling: 'the bare 32-digit form', rowId: ROW_A.replaceAll('-', '') },
    // The three whitespace rows differ by how they arrive: a copied value picks
    // up a leading space, a form field a trailing one, and a file read line by
    // line a trailing newline. JavaScript's `$` without the `m` flag matches the
    // end of the input rather than the position before a terminal newline, which
    // is what makes the third catchable at all.
    { spelling: 'a leading space', rowId: ` ${ROW_A}` },
    { spelling: 'a trailing space', rowId: `${ROW_A} ` },
    { spelling: 'a trailing newline', rowId: `${ROW_A}\n` },
  ])('refuses a row id in $spelling', ({ rowId }) => {
    // Arrange
    // Each row names the same UUID and is a different value on the wire.
    const binding: NarrativeFieldBinding = {
      table: 'transactions',
      column: 'description',
      rowId,
    };

    // Act & Assert
    // Refused, not folded — deliberately unlike the wrapped-key grammar next
    // door. The row hands back exactly one spelling, so anything sealed against
    // another can never be rebuilt: both directions stop working permanently,
    // with no error naming the cause.
    expect(() => narrativeFieldAssociatedData(binding)).toThrow();
  });

  it('refuses a row id that is not a UUID at all', () => {
    // Arrange
    // The table above is all near-misses, every one of which a lenient parser
    // would accept. This is the other end: values a caller reaches for when the
    // id is missing, which a check written as "reject the spellings we know" —
    // rather than "accept the one spelling" — waves straight through.
    //
    // **The last entry is the load-bearing one, and it is here because of a
    // measured hole rather than a worry.** Thirty-six hyphens is the right
    // length over the right alphabet with none of the structure: no hex digit
    // anywhere, no group of the right width, nothing at the four positions a
    // hyphen belongs at. Everything else in this case and every row of the
    // table above passes a check that looks only at length and alphabet —
    // `/^[0-9a-f-]{36}$/` — so without this entry the whole set is satisfied by
    // a local regular expression written here in the module, with the shared
    // predicate no longer imported at all. That was run: the module's call into
    // `isCanonicalRowId` was replaced with exactly that pattern, the import of
    // `./factor-id` deleted, and this file stayed green from end to end.
    //
    // Which is the failure worth naming, because it is not about somebody
    // writing a careless pattern on purpose. It is that **the shared predicate
    // stops being shared and nothing says so**. The module then holds a second
    // opinion about which spelling is legal, agreeing with the first one on
    // every value anybody happens to test and disagreeing somewhere nobody
    // looked — and the disagreement surfaces as a write the server refuses at
    // the worst possible moment, or worse, as a value sealed under a spelling
    // the row cannot hand back. Same class as the two passkey labels: a second
    // copy of a rule that agrees today and not tomorrow, which is why
    // `passkey-label-single-source.spec.ts` exists at all.
    const notUuids = [
      '',
      'transactions',
      '0',
      'null',
      'undefined',
      '-'.repeat(36),
    ];

    // Act & Assert
    for (const rowId of notUuids) {
      expect(() =>
        narrativeFieldAssociatedData({
          table: 'transactions',
          column: 'description',
          rowId,
        }),
      ).toThrow();
    }

    // The control the list needs: without it a function that threw on
    // everything would pass every row above and every row in the table before
    // it, and seal nothing ever again.
    expect(() =>
      narrativeFieldAssociatedData({
        table: 'transactions',
        column: 'description',
        rowId: ROW_A,
      }),
    ).not.toThrow();
  });
});

describe('sealing a narrative field', () => {
  const BINDING: NarrativeFieldBinding = {
    table: 'transactions',
    column: 'description',
    rowId: ROW_A,
  };

  it('refuses an extractable content key before encrypting anything', async () => {
    // Arrange
    // Same bytes as every other case, imported the one way production never
    // does. A content key that can be read back out is one that can be logged,
    // posted to a crash reporter or written to `localStorage`, and there is no
    // API that undoes an extractable import — so the refusal has to be at the
    // door.
    //
    // **"Before anything else" is a separate claim from "refuses", and a
    // rejection cannot tell them apart**: a function that sealed first and threw
    // on the way out rejects identically. The observable difference is whether
    // the cipher ran at all, so the seam is a spy on `crypto.subtle.encrypt`.
    // It calls through — `vi.spyOn` keeps the original unless something replaces
    // it — because a substituted cipher would make the control below a statement
    // about the substitute rather than about production.
    const key = await importContentKey(true);
    const encrypt = vi.spyOn(crypto.subtle, 'encrypt');

    // Act & Assert
    await expect(sealNarrativeField(key, 'Coffee', BINDING)).rejects.toThrow();
    expect(encrypt).not.toHaveBeenCalled();

    // The control, and the spy needs it twice over: it says the refusal is about
    // the key rather than about the arguments, and it says the spy is watching
    // the call this module actually makes. Without it, a spy on a method nothing
    // reaches reports "never called" perfectly.
    const proper = await importNonExtractableContentKey();

    await expect(
      sealNarrativeField(proper, 'Coffee', BINDING),
    ).resolves.toBeTypeOf('string');
    expect(encrypt).toHaveBeenCalledTimes(1);

    encrypt.mockRestore();
  });

  it('refuses a non-canonical row id on the sealing path itself', async () => {
    // Arrange
    // **Not a second copy of the row-id table above.** That table calls the
    // exported `narrativeFieldAssociatedData`, and this calls the function
    // production actually runs. A module whose seal built its associated data
    // inline — the same four fields, joined the same way, with the refusal left
    // out — passes every row of that table and every other case in this file,
    // and seals under a spelling no later read of that row can reproduce.
    //
    // What that costs is the whole reason the grammar refuses rather than folds:
    // the client seals, the server stores, every response says success, and the
    // person finds out on the day the text stops opening — permanently, with no
    // error anywhere naming the cause.
    const key = await importNonExtractableContentKey();
    const shouted: NarrativeFieldBinding = {
      ...BINDING,
      rowId: ROW_A.toUpperCase(),
    };

    // Act & Assert
    await expect(sealNarrativeField(key, 'Coffee', shouted)).rejects.toThrow();

    // The control: the same call under the canonical spelling of the same UUID
    // succeeds, so the refusal is about the spelling and not about the call.
    await expect(
      sealNarrativeField(key, 'Coffee', BINDING),
    ).resolves.toBeTypeOf('string');
  });

  it('seals the text exactly as it was typed, normalising nothing', async () => {
    // Arrange
    // `e` followed by a combining acute, and the single code point that renders
    // identically. Written as escapes rather than typed, for the reason the
    // vector file gives about its own plaintext: a combining mark is invisible
    // in a diff and one normalising editor away from silently becoming the
    // composed form.
    //
    // **Both frozen plaintexts are already NFC**, so a module that called
    // `.normalize('NFC')` before sealing is a no-op on every vector and green on
    // every other case in this file. This is the one case that can see it.
    //
    // The client stores exactly what was typed, deliberately. The normalisation
    // this product does need arrives with the blind index, where the transform
    // is a different one — case folding on top of a compatibility form — and
    // folding the two together here would seal text nobody wrote.
    const key = await importNonExtractableContentKey();
    const decomposed = 'e\u0301';
    const composed = '\u00e9';

    // Act
    const decomposedSealed = envelopeOf(
      await sealNarrativeField(key, decomposed, BINDING),
    );
    const composedSealed = envelopeOf(
      await sealNarrativeField(key, composed, BINDING),
    );
    const back = await openNarrativeField(
      key,
      await sealNarrativeField(key, decomposed, BINDING),
      BINDING,
    );

    // Assert
    // Three bytes, not two. AES-GCM makes the ciphertext the length of its
    // plaintext, so this single number separates "sealed what was typed" from
    // "sealed what it composed".
    expect(ciphertextRegion(decomposedSealed)).toHaveLength(3);
    expect(ciphertextRegion(composedSealed)).toHaveLength(2);

    // And the other direction: the same two code points come back, not the one
    // they render as. A reader that normalised on the way out would hand a caller
    // text that no longer matches what the caller stored, and the next save would
    // write the folded form over it.
    expect(back).toHaveLength(2);
    expect(back).toBe(decomposed);
  });

  it('keeps the whitespace a person typed', async () => {
    // Arrange
    // Every other plaintext in this file is already trimmed, which is exactly
    // why a `.trim()` before sealing is green everywhere else. Leading and
    // trailing spaces are text somebody typed; a field that is nothing but
    // spaces is the value a trim destroys completely, turning a stored string
    // into an empty one that reads as "never filled in".
    const key = await importNonExtractableContentKey();
    const padded = '  Coffee  ';
    const spacesOnly = '   ';

    // Act
    const paddedSealed = envelopeOf(
      await sealNarrativeField(key, padded, BINDING),
    );
    const spacesOnlySealed = envelopeOf(
      await sealNarrativeField(key, spacesOnly, BINDING),
    );
    const back = await openNarrativeField(
      key,
      await sealNarrativeField(key, padded, BINDING),
      BINDING,
    );

    // Assert
    expect(ciphertextRegion(paddedSealed)).toHaveLength(padded.length);
    expect(ciphertextRegion(spacesOnlySealed)).toHaveLength(spacesOnly.length);
    expect(back).toBe(padded);
  });

  it('lays out the versioned envelope over the UTF-8 of the text', async () => {
    // Arrange
    // The expected width is derived from the widths `key-envelope.ts` exports
    // and from the plaintext, never restated, so this cannot be left behind by a
    // change to either. AES-GCM's ciphertext is the length of its plaintext,
    // which is what makes the arithmetic exact rather than a bound.
    const key = await importNonExtractableContentKey();
    const plaintext = 'Lunch at the market';
    const plaintextBytes = utf8.encode(plaintext);

    // Act
    const envelope = envelopeOf(
      await sealNarrativeField(key, plaintext, BINDING),
    );

    // Assert
    expect(envelope[0]).toBe(ENVELOPE_VERSION);
    expect(envelope).toHaveLength(
      VERSION_BYTES +
        ENVELOPE_NONCE_BYTES +
        plaintextBytes.length +
        ENVELOPE_TAG_BYTES,
    );

    // And the regions named, so a nonce that moved behind the ciphertext — a
    // rearrangement the total length cannot see — is caught too.
    expect(nonceRegion(envelope)).toHaveLength(ENVELOPE_NONCE_BYTES);
    expect(ciphertextRegion(envelope)).toHaveLength(plaintextBytes.length);
  });

  it('renders the envelope as unpadded base64url', async () => {
    // Arrange
    // The wire form the column stores and the wire form the server's decoder
    // accepts. Padding, `+` and `/` are all refused on the other side, and the
    // symptom does not arrive on the malformed value: it arrives later, as a
    // field that will not open.
    const key = await importNonExtractableContentKey();

    // Act
    const wire = await sealNarrativeField(key, 'Lunch at the market', BINDING);

    // Assert
    expect(wire).toMatch(/^[A-Za-z0-9_-]+$/);

    // The alphabet check alone is satisfied by a hex rendering, which uses only
    // characters the pattern admits. The decoded width is what tells the two
    // apart — hex of this envelope would decode as base64url to half again as
    // many bytes as the envelope has.
    expect(decodeBase64Url(wire)).toHaveLength(
      VERSION_BYTES +
        ENVELOPE_NONCE_BYTES +
        utf8.encode('Lunch at the market').length +
        ENVELOPE_TAG_BYTES,
    );
  });

  it('carries text through UTF-8, not UTF-16', async () => {
    // Arrange
    // The mixed-width vector's plaintext spans one-, two-, three- and four-byte
    // UTF-8 sequences, the last of which is also a UTF-16 surrogate pair. What
    // catches a wrong encoder is the ciphertext *length*, which AES-GCM makes
    // equal to the plaintext length — because a self-consistent wrong encoder
    // seals and reopens its own ciphertext perfectly, so no round trip inside
    // one client can see the fault.
    //
    // Measured over this exact string, and recorded in the vector file: UTF-8
    // gives 20 bytes; a `charCodeAt` loop emitting UTF-16LE gives 26; a latin1
    // truncation gives 13 and collapses the emoji's surrogate pair to two bytes;
    // and NFD instead of NFC gives 21, because U+00E9 is the one code point here
    // with a canonical decomposition. Each of those four is a different number,
    // so the single assertion below separates all of them.
    const key = await importNonExtractableContentKey();
    const plaintext = plaintextOf(MIXED_WIDTH_VECTOR);

    // Act
    const envelope = envelopeOf(
      await sealNarrativeField(key, plaintext, bindingOf(MIXED_WIDTH_VECTOR)),
    );

    // Assert
    expect(ciphertextRegion(envelope)).toHaveLength(20);

    // Derived as well as pinned, so the 20 above is the frozen file's answer and
    // not a number this spec invented.
    expect(ciphertextRegion(envelope)).toHaveLength(
      MIXED_WIDTH_VECTOR.plaintextUtf8Hex.length / 2,
    );
  });

  it('draws a fresh nonce for each seal of the same text', async () => {
    // Arrange
    // Two seals of one string under one key and one binding. A repeated nonce
    // under GCM is not a degraded envelope but the end of the guarantee: two
    // messages under one key and one nonce leak their XOR *and* give up the
    // authentication subkey, which turns every tag under that key into something
    // an attacker can forge.
    const key = await importNonExtractableContentKey();

    // Act
    const first = envelopeOf(await sealNarrativeField(key, 'Coffee', BINDING));
    const second = envelopeOf(await sealNarrativeField(key, 'Coffee', BINDING));

    // Assert
    // The nonce region, not merely that the envelopes differ. A counter that
    // never moved would still produce two different envelopes the moment
    // anything else about the call changed, and comparing whole envelopes would
    // report that as freshness.
    expect(toHex(nonceRegion(first))).not.toBe(toHex(nonceRegion(second)));
  });

  it('opens back to exactly the text it sealed', async () => {
    // Arrange
    // Both an ordinary string and the mixed-width one, because a wrong encoder
    // is invisible on ASCII. Compared as bytes rather than as strings, so a
    // round trip that normalised on the way through — NFD in, NFC out — is a
    // failure here rather than a string comparison that helpfully agreed.
    const key = await importNonExtractableContentKey();
    const ordinary = 'Lunch at the market';
    const mixedWidth = plaintextOf(MIXED_WIDTH_VECTOR);

    // Act
    const ordinaryBack = await openNarrativeField(
      key,
      await sealNarrativeField(key, ordinary, BINDING),
      BINDING,
    );
    const mixedWidthBack = await openNarrativeField(
      key,
      await sealNarrativeField(key, mixedWidth, bindingOf(MIXED_WIDTH_VECTOR)),
      bindingOf(MIXED_WIDTH_VECTOR),
    );

    // Assert
    expect(ordinaryBack).toBe(ordinary);
    expect(toHex(utf8.encode(mixedWidthBack))).toBe(
      MIXED_WIDTH_VECTOR.plaintextUtf8Hex,
    );
  });

  it('seals an empty string to the shortest possible envelope', async () => {
    // Arrange
    // An empty narrative field is a legitimate value — a transaction with no
    // memo, a category with no note — and it seals to a version, a nonce and a
    // tag with nothing between them. The refusal on the reading side has to be
    // `<` and not `<=`, and this is the case that says which.
    const key = await importNonExtractableContentKey();

    // Act
    const wire = await sealNarrativeField(key, '', BINDING);
    const envelope = envelopeOf(wire);

    // Assert
    expect(envelope).toHaveLength(SHORTEST_ENVELOPE_BYTES);

    // Pinned as a number too, because the derivation above would follow a
    // change to either exported width without saying so. Twenty-nine is the
    // count the vector file states an envelope adds to what it seals.
    expect(SHORTEST_ENVELOPE_BYTES).toBe(29);

    // And it opens, which is the half that stops "too short" from being read as
    // "empty is not allowed".
    expect(await openNarrativeField(key, wire, BINDING)).toBe('');
  });
});

describe('opening a narrative field', () => {
  const BINDING: NarrativeFieldBinding = {
    table: 'transactions',
    column: 'description',
    rowId: ROW_A,
  };

  it('refuses an extractable content key before decrypting anything', async () => {
    // Arrange
    // The envelope is genuine and the binding is right, so the only thing left
    // for the refusal to be about is the key's custody.
    //
    // The ordering claim carries more here than it does on the sealing side. A
    // reader that refused the key *after* calling the cipher has already put the
    // account's narrative text in memory under a key that was never allowed to
    // touch it — it did the thing the refusal exists to prevent and then
    // reported a failure. So the seam is a spy on `crypto.subtle.decrypt`,
    // calling through for the reason the sealing case gives, installed after the
    // arranging seal so nothing but the call under test can reach it.
    const sealing = await importNonExtractableContentKey();
    const wire = await sealNarrativeField(sealing, 'Coffee', BINDING);
    const extractable = await importContentKey(true);
    const decrypt = vi.spyOn(crypto.subtle, 'decrypt');

    // Act & Assert
    await expect(
      openNarrativeField(extractable, wire, BINDING),
    ).rejects.toThrow();
    expect(decrypt).not.toHaveBeenCalled();

    // The control: the same wire under the same bytes imported properly opens,
    // which says both that the refusal is about the key and that the spy is on
    // the call this module actually makes.
    await expect(openNarrativeField(sealing, wire, BINDING)).resolves.toBe(
      'Coffee',
    );
    expect(decrypt).toHaveBeenCalledTimes(1);

    decrypt.mockRestore();
  });

  it('refuses a non-canonical row id on the opening path itself', async () => {
    // Arrange
    // **The obvious version of this case proves nothing.** Sealing under the
    // canonical spelling and opening under a shouted one rejects on *any*
    // implementation, refusal or none, because the associated data differs and
    // GCM will not authenticate it. So the envelope here is built the way a
    // module that skipped the refusal would build it: the same four fields,
    // joined the same way, with the shouted row id in place — assembled field by
    // field in this file rather than through the module, because the module is
    // the thing being questioned.
    //
    // Under a reader that refuses, this rejects. Under a reader that quietly
    // accepted the spelling, it opens and returns the text — which is the
    // opening half of the lockout: a client that reads one spelling out of a row
    // and writes another goes on working perfectly against its own ciphertext
    // and against nobody else's.
    const key = await importNonExtractableContentKey();
    const shoutedRowId = ROW_A.toUpperCase();
    const shouted: NarrativeFieldBinding = { ...BINDING, rowId: shoutedRowId };
    const shoutedAssociatedData = utf8.encode(
      [
        NARRATIVE_FIELD_AAD_PREFIX,
        BINDING.table,
        BINDING.column,
        shoutedRowId,
      ].join(UNIT_SEPARATOR),
    );

    // Act
    const wire = encodeBase64Url(
      await sealEnvelope(key, utf8.encode('Coffee'), shoutedAssociatedData),
    );

    // Assert
    await expect(openNarrativeField(key, wire, shouted)).rejects.toThrow();

    // The control, and this case is worth nothing without it: the identical
    // construction under the canonical spelling opens. Without it, a mistake in
    // how the envelope above is assembled would reject for a reason that has
    // nothing to do with the row id, and read as a pass.
    const properWire = encodeBase64Url(
      await sealEnvelope(
        key,
        utf8.encode('Coffee'),
        narrativeFieldAssociatedData(BINDING),
      ),
    );

    await expect(openNarrativeField(key, properWire, BINDING)).resolves.toBe(
      'Coffee',
    );
  });

  it('refuses a field presented under another row', async () => {
    // Arrange
    const key = await importNonExtractableContentKey();
    const sealed: NarrativeFieldBinding = { ...BINDING, rowId: ROW_A };
    const elsewhere: NarrativeFieldBinding = { ...BINDING, rowId: ROW_B };
    const wire = await sealNarrativeField(key, 'Coffee', sealed);

    // Act & Assert
    // A ciphertext copied into another row does not decrypt into something; it
    // fails to authenticate, which is what makes a database that shuffled rows a
    // loud failure rather than a quiet lie.
    await expect(openNarrativeField(key, wire, elsewhere)).rejects.toThrow();
  });

  it('refuses a field presented under another column', async () => {
    // Arrange
    const key = await importNonExtractableContentKey();
    const name: NarrativeFieldBinding = {
      table: 'categories',
      column: 'name',
      rowId: ROW_A,
    };
    const description: NarrativeFieldBinding = {
      table: 'categories',
      column: 'description',
      rowId: ROW_A,
    };
    const wire = await sealNarrativeField(key, 'Groceries', name);

    // Act & Assert
    await expect(openNarrativeField(key, wire, description)).rejects.toThrow();
  });

  it('refuses a field presented under another table with the same column name', async () => {
    // Arrange
    // `payees.name` and `categories.name` share a column word and a row id here,
    // so the table is the only field separating them. A grammar that dropped the
    // table — or that folded it into the column — passes every other case in
    // this file and lets a payee's name open as a category's.
    const key = await importNonExtractableContentKey();
    const payee: NarrativeFieldBinding = {
      table: 'payees',
      column: 'name',
      rowId: ROW_A,
    };
    const category: NarrativeFieldBinding = {
      table: 'categories',
      column: 'name',
      rowId: ROW_A,
    };
    const wire = await sealNarrativeField(key, 'Dana', payee);

    // Act & Assert
    await expect(openNarrativeField(key, wire, category)).rejects.toThrow();
  });

  it('refuses bytes that authenticate but are not UTF-8', async () => {
    // Arrange
    // Sealed through `sealEnvelope` directly, under the same key and the same
    // associated data this module would build, so the envelope is genuine in
    // every respect the cipher can check. What is left is the decode, and the
    // lenient reading of it — a `TextDecoder` without `fatal: true` — hands back
    // U+FFFD for each bad byte: a string that looks like damaged text, is
    // indistinguishable from text somebody actually typed, and would be written
    // straight back on the next save.
    //
    // `0xff` and `0xfe` are lead bytes no UTF-8 sequence can begin with, and
    // `0xfd` follows them as a continuation that is not one.
    const key = await importNonExtractableContentKey();
    const associatedData = narrativeFieldAssociatedData(BINDING);
    const notUtf8 = Uint8Array.of(0xff, 0xfe, 0xfd);

    // Act
    const wire = encodeBase64Url(
      await sealEnvelope(key, notUtf8, associatedData),
    );

    // Assert
    await expect(openNarrativeField(key, wire, BINDING)).rejects.toThrow();

    // The control, and this case is worth little without it: the identical path
    // over bytes that *are* UTF-8 opens. Without it, a mistake in how this case
    // builds its envelope — the wrong associated data, the wrong key — would
    // reject for a reason that has nothing to do with the decode, and read as a
    // pass.
    const properWire = encodeBase64Url(
      await sealEnvelope(key, utf8.encode('Coffee'), associatedData),
    );

    await expect(openNarrativeField(key, properWire, BINDING)).resolves.toBe(
      'Coffee',
    );
  });

  it.each([
    // Padding. The one a lenient reading is most likely to admit, because `atob`
    // wants it and stripping it is the encoder's own last step.
    { shape: 'standard padding', wire: 'AQIDBA==' },
    // The standard alphabet's two characters, refused because a value carrying
    // them is a value some other encoder produced.
    { shape: 'a standard-alphabet plus', wire: 'AQID+AQI' },
    { shape: 'a standard-alphabet slash', wire: 'AQID/AQI' },
    // Skipping a character it does not recognise is the worst lenience of all,
    // because it shortens the output silently: an envelope keeps its shape and
    // loses a byte.
    { shape: 'an inner space', wire: 'AQID AQID' },
    { shape: 'punctuation', wire: 'not-base64url!' },
  ])('refuses a wire value carrying $shape', async ({ wire }) => {
    // Arrange
    const key = await importNonExtractableContentKey();

    // Act & Assert
    await expect(openNarrativeField(key, wire, BINDING)).rejects.toThrow();
  });
});

// The type, not merely the throw.
//
// Every case above this point asserts that something rejected and nothing
// asserts *what*. That gap was measured rather than suspected: replacing
// `new NarrativeFieldMisuseError(` with `new Error(` in both of the module's
// refusals leaves the whole suite green, so today the class could fail to extend
// `Error`, could carry the wrong `name`, or could not exist at all.
//
// **The distinction it carries is the only one a caller is entitled to.** A
// rejection out of `openNarrativeField` means either *you asked for something
// impossible* or *this stored value did not open*, and only the second is a
// state a person can be shown. Rendered as damaged text, the first is a bug
// wearing a UI, put in front of somebody over a row that is perfectly fine.
//
// So the cases come in two halves and the second is the load-bearing one. The
// positive half says the two pre-cipher refusals carry the type. The negative
// half says a ciphertext that did not authenticate does **not** — without which
// an implementation that threw the misuse type for everything passes every
// positive case there is, and the distinction the type exists to make is gone
// while all of it stays green.
describe('the type a narrative field refuses its caller with', () => {
  const BINDING: NarrativeFieldBinding = {
    table: 'transactions',
    column: 'description',
    rowId: ROW_A,
  };

  it.each(BINDING_ENTRY_POINTS)(
    'refuses a non-canonical row id out of $where with the misuse type',
    async ({ call }) => {
      // Arrange
      // Every door, because the rule is the module's and not one function's. A
      // refusal that carried the type on the builder and a bare `Error` on the
      // operation would be exactly as useless to a caller as no type at all —
      // the operations are what a caller wraps in a `catch`.
      const key = await importNonExtractableContentKey();
      const shouted: NarrativeFieldBinding = {
        ...BINDING,
        rowId: ROW_A.toUpperCase(),
      };

      // Act
      const error = await thrownBy(() => call(key, shouted));

      // Assert
      expect(error).toBeInstanceOf(NarrativeFieldMisuseError);

      // And it is an `Error`, which `toBeInstanceOf` above does not imply: a
      // class extending nothing satisfies the first assertion perfectly and
      // arrives at a caller with no stack, no `message` and nothing a logger
      // renders.
      expect(error).toBeInstanceOf(Error);
    },
  );

  // The key doors are the two operations and only those: neither builder is
  // handed a key, so an extractable one has nowhere to arrive.
  interface KeyEntryPoint {
    readonly where: string;
    readonly call: (key: CryptoKey, wire: string) => Promise<unknown>;
  }

  const KEY_ENTRY_POINTS: readonly KeyEntryPoint[] = [
    {
      where: 'sealNarrativeField',
      call: (key, wire) => sealNarrativeField(key, 'Coffee', BINDING),
    },
    {
      where: 'openNarrativeField',
      call: (key, wire) => openNarrativeField(key, wire, BINDING),
    },
  ];

  it.each(KEY_ENTRY_POINTS)(
    'refuses an extractable content key out of $where with the misuse type',
    async ({ call }) => {
      // Arrange
      // The same bytes imported the one way production never does. The cases
      // further up already say the cipher never runs; this one says what the
      // caller is handed when it does not, which is the half that decides
      // whether a screen reads its own defect as a damaged ledger.
      const sealing = await importNonExtractableContentKey();
      const wire = await sealNarrativeField(sealing, 'Coffee', BINDING);
      const extractable = await importContentKey(true);

      // Act
      const error = await thrownBy(() => call(extractable, wire));

      // Assert
      expect(error).toBeInstanceOf(NarrativeFieldMisuseError);
      expect(error).toBeInstanceOf(Error);

      // The control: the identical call under the same bytes imported properly
      // answers. Without it the refusal could be about the wire, the binding or
      // the plaintext, and this case would read as a pass either way.
      await expect(call(sealing, wire)).resolves.toBeTypeOf('string');
    },
  );

  it('carries a name a caller can read without instanceof', async () => {
    // Arrange
    // **`instanceof` is the check to reach for and it is not always available.**
    // Two copies of this module loaded into one page are two different class
    // objects, so an error thrown by the far one is `instanceof` nothing a
    // caller holds — while still reading `'NarrativeFieldMisuseError'`. The name
    // is the fallback, which makes it part of the contract rather than a
    // decoration, and a `name` that fell back to `'Error'` would leave the far
    // copy indistinguishable from a ciphertext that did not open.
    //
    // Both refusals, because they are two `throw` statements and a rename
    // reaches one of them at a time.
    const extractable = await importContentKey(true);

    // Act
    const fromBinding = await thrownBy(() =>
      refuseInvalidBinding({ ...BINDING, rowId: ROW_A.toUpperCase() }),
    );
    const fromKey = await thrownBy(() =>
      sealNarrativeField(extractable, 'Coffee', BINDING),
    );

    // Assert
    for (const error of [fromBinding, fromKey]) {
      expect(error).toBeInstanceOf(NarrativeFieldMisuseError);
      expect((error as Error).name).toBe('NarrativeFieldMisuseError');

      // An **own** property, which is the module's stated reason for writing it
      // as a class field rather than leaving it on the prototype: a prototype
      // `name` is in neither `Object.keys` nor `JSON.stringify`, so an error
      // that reached a log or a structured report would arrive carrying nothing
      // that says what it was.
      expect(Object.hasOwn(error as object, 'name')).toBe(true);
      expect(Object.keys(error as object)).toContain('name');
    }
  });

  // The negative half. Each row damages one thing about a genuine, opening
  // fixture and nothing else, so what the assertion is about is the damage.
  interface OpenFixture {
    readonly key: CryptoKey;
    readonly wire: string;
    readonly binding: NarrativeFieldBinding;
  }

  interface UnopenableCase {
    readonly what: string;
    readonly damage: (good: OpenFixture) => Promise<OpenFixture> | OpenFixture;
  }

  const UNOPENABLE_CASES: readonly UnopenableCase[] = [
    {
      what: 'a key that did not seal it',
      damage: async (good) => ({ ...good, key: await importOtherContentKey() }),
    },
    {
      what: 'a row that did not hold it',
      damage: (good) => ({
        ...good,
        binding: { ...good.binding, rowId: ROW_B },
      }),
    },
    {
      what: 'a table and column that did not hold it',
      damage: (good) => ({
        ...good,
        binding: bindingFor('payees', 'name', ROW_A),
      }),
    },
    {
      what: 'altered ciphertext bytes',
      damage: (good) => ({ ...good, wire: withAlteredCiphertext(good.wire) }),
    },
    // The decoder's own refusal, which happens before the cipher and is
    // therefore the row a reader is most likely to expect the misuse type on.
    // It is not one: a wire value is what was *stored*, not what the caller
    // asked for, so a caller that read it as its own defect would tell somebody
    // their ledger is fine when the column holds nonsense.
    {
      what: 'a wire the strict decoder refuses',
      damage: (good) => ({ ...good, wire: 'not-base64url!' }),
    },
    {
      what: 'bytes that authenticate but are not UTF-8',
      damage: async (good) => ({
        ...good,
        wire: encodeBase64Url(
          await sealEnvelope(
            good.key,
            Uint8Array.of(0xff, 0xfe, 0xfd),
            narrativeFieldAssociatedData(good.binding),
          ),
        ),
      }),
    },
  ];

  it.each(UNOPENABLE_CASES)(
    'does not reach for the misuse type over $what',
    async ({ damage }) => {
      // Arrange
      const key = await importNonExtractableContentKey();
      const good: OpenFixture = {
        key,
        wire: await sealNarrativeField(key, 'Coffee', BINDING),
        binding: BINDING,
      };
      const damaged = await damage(good);

      // Act
      const error = await thrownBy(() =>
        openNarrativeField(damaged.key, damaged.wire, damaged.binding),
      );

      // Assert
      // Nothing here inspects *which* failure it is, for the reason the head of
      // this file gives: wrong key, wrong binding and altered bytes are one
      // indistinguishable error on purpose, and a case that told them apart
      // would be pinning the oracle the format exists to deny. The single claim
      // is that none of them is a statement about the call.
      expect(error).not.toBeInstanceOf(NarrativeFieldMisuseError);

      // The name half of the same claim, and not a restatement: two copies of
      // this module in one page make the assertion above pass for an error that
      // *is* the misuse type, thrown by the far copy.
      expect((error as { readonly name?: unknown }).name).not.toBe(
        'NarrativeFieldMisuseError',
      );

      // The control every row needs: the undamaged fixture opens, so the
      // rejection above is the damage and not the way this case was assembled.
      await expect(
        openNarrativeField(good.key, good.wire, good.binding),
      ).resolves.toBe('Coffee');
    },
  );
});

// The other two thirds of the binding.
//
// `refuseInvalidBinding` checks the row id and nothing else. The table and the
// column are closed unions the module declares, so today they are held by the
// compiler alone — and that holds exactly as long as a binding is assembled by
// the compiler.
//
// The next slice builds view-model mappers, which is where a table name starts
// travelling as **data**: out of a configuration, off a response, through an
// `as`. One `as NarrativeFieldBinding` and a field is sealed under associated
// data no other client will ever reproduce — permanent, silent, with no error
// naming the cause. That is the same damage the row id is checked at runtime to
// prevent, over two thirds of the binding that has no such check.
//
// **The third row says what the check has to be.** `transactions` is a real
// table and `name` is a real column, and the pair is not one of the eight: the
// eight are pairs, not a cross product. So a fix written as two membership tests
// — is the table known, is the column known — waves it through, and a mapper
// that reads a table from one place and a column from another produces exactly
// that value. The check has to be a lookup of the **pair** in
// `NARRATIVE_FIELDS`.
//
// Whether the pair *should* be refused is a fair question, and the answer is
// yes for the reason the other two rows are refused: nothing can ever be found
// in a column that does not exist, so a field sealed under that binding is
// sealed under a grammar no read of that row will rebuild. It is not a narrower
// binding — it is a binding pointing at nothing.
describe('the table and column a narrative field is bound to', () => {
  const LEGAL = NARRATIVE_FIELDS[0];

  // Derived from a real entry by changing one field, so neither value can drift
  // into the list by somebody adding a narrative field: a suffix on a name that
  // is already taken is a name the list does not hold, and the derivation is
  // checked below rather than trusted by eye.
  const ABSENT_TABLE = `${LEGAL.table}_v2`;
  const ABSENT_COLUMN = `${LEGAL.column}_v2`;

  const REFUSABLE = [
    {
      what: 'a table the module does not list',
      binding: anIllegalBinding(ABSENT_TABLE, LEGAL.column, ROW_A),
    },
    {
      what: 'a column the module does not list',
      binding: anIllegalBinding(LEGAL.table, ABSENT_COLUMN, ROW_A),
    },
    {
      what: 'a real table paired with a real column of another table',
      binding: anIllegalBinding('transactions', 'name', ROW_A),
    },
  ];

  it('builds its illegal bindings out of the module own list', () => {
    // Arrange
    // The guard on the three rows above: an "invalid" value that quietly became
    // valid would leave twelve cases asserting a refusal of something legal,
    // and the first thing anybody would do about that is delete the check.
    //
    // `Set<string>` and not the inferred `Set<'transactions' | …>`, which is
    // itself a small demonstration of the gap these cases are about: the union
    // is narrow enough that the compiler refuses to let a value outside it even
    // be *asked about*. It is refusing the question here and refusing nothing at
    // all wherever a table name arrives through a cast.
    const tables = new Set<string>(
      NARRATIVE_FIELDS.map((field) => field.table),
    );
    const columns = new Set<string>(
      NARRATIVE_FIELDS.map((field) => field.column),
    );
    const pairs = new Set<string>(NARRATIVE_FIELDS.map(fieldKey));

    // Act, Assert
    expect(tables.has(ABSENT_TABLE)).toBe(false);
    expect(columns.has(ABSENT_COLUMN)).toBe(false);

    // The mismatched row is the one whose halves are both real, which is the
    // whole of its argument: two independent membership tests accept it.
    expect(tables.has('transactions')).toBe(true);
    expect(columns.has('name')).toBe(true);
    expect(pairs.has('transactions|name')).toBe(false);

    // And the control the twelve cases need: the legal pair the two absent
    // values were derived from is accepted, so a refusal below is about the one
    // field that was changed.
    expect(() =>
      refuseInvalidBinding(bindingFor(LEGAL.table, LEGAL.column, ROW_A)),
    ).not.toThrow();
  });

  it.each(
    REFUSABLE.flatMap((subject) =>
      BINDING_ENTRY_POINTS.map((entry) => ({ ...subject, ...entry })),
    ),
  )('refuses $what out of $where', async ({ binding, call }) => {
    // Arrange
    const key = await importNonExtractableContentKey();

    // Act
    const error = await thrownBy(() => call(key, binding));

    // Assert
    // The type and not merely a throw, for the reason the describe above gives:
    // an illegal binding is a fact about the call, and a caller has to be able
    // to tell it from a column whose contents did not open.
    expect(error).toBeInstanceOf(NarrativeFieldMisuseError);
  });

  // **The positive half, and its absence is the largest hole in this describe.**
  //
  // Every refusable row above is built from `NARRATIVE_FIELDS[0]` or from the
  // hard-coded `transactions|name`, so nothing here asks whether the other seven
  // pairs are *accepted*. A lookup written against entry zero alone —
  // `field.table === NARRATIVE_FIELDS[0].table && field.column === …` — passes
  // all twelve refusals and the one control above it, and **silently refuses
  // `budgets.name` and six others**: an account name, every payee, every
  // category label and every group note stop being sealable at all, reported to
  // the caller as its own defect over a pair this module publishes.
  //
  // Driven off `NARRATIVE_FIELDS` and never typed out, for the reason the reason
  // map at the head of this file gives: a ninth pair is covered the day it is
  // added, and a hand-written list here would be a second copy of the field list
  // living in the file that exists to refuse copies of it.
  //
  // **A full round trip per pair rather than a bare "did not throw", and the
  // choice is affordable rather than thorough for its own sake**: eight seals
  // and eight opens is sixteen AES-GCM calls over a five-letter string, which
  // does not move this file's runtime. What it buys over the cheaper version is
  // the pair that is accepted on the way in and spelled differently on the way
  // out — an implementation that normalised the table only inside
  // `sealNarrativeField` refuses nothing and opens nothing it wrote, and a
  // refusal-only sweep is green over it.
  it('drives a case for every pair the module publishes', () => {
    // Arrange, Act, Assert
    // The control on the sweep below, and it is the whole reason the sweep is
    // worth anything: `it.each` over an empty list reports a clean run having
    // executed nothing, and this is the only positive statement in the describe.
    //
    // The *count* is pinned elsewhere — `builds a distinct binding for each of
    // the eight narrative fields` asserts eight — so what is left here is that
    // the list is non-empty and that no pair is written twice, which would make
    // the sweep cover fewer pairs than it appears to.
    expect(NARRATIVE_FIELDS.length).toBeGreaterThan(0);
    expect(new Set(NARRATIVE_FIELDS.map(fieldKey)).size).toBe(
      NARRATIVE_FIELDS.length,
    );
  });

  it.each([...NARRATIVE_FIELDS])(
    'accepts $table with $column and round-trips text bound to it',
    async ({ table, column }) => {
      // Arrange
      const key = await importNonExtractableContentKey();
      const binding = bindingFor(table, column, ROW_A);

      // Act, Assert
      // The named refusal first, because it is the door the other three entry
      // points go through and the one a narrowed lookup closes.
      expect(() => refuseInvalidBinding(binding)).not.toThrow();

      // Then the round trip, which is a second and stronger statement: the pair
      // is not merely admitted, it builds associated data this same module
      // rebuilds byte for byte on the way back out.
      const wire = await sealNarrativeField(key, 'Coffee', binding);

      await expect(openNarrativeField(key, wire, binding)).resolves.toBe(
        'Coffee',
      );
    },
  );

  // **The case of a legal pair, which nothing above tries.**
  //
  // The twelve refusals all differ from a legal pair in their *letters* — a
  // suffix, or a real column belonging to another table — so an implementation
  // that lower-cased both fields before the lookup is green through every one of
  // them and through the eight accepted above. What it then does is seal
  // `Transactions.description` under associated data built from the folded
  // words, which no other client reproduces: the same permanent, silent damage
  // the row id is refused for, over the other two thirds of the binding.
  //
  // **Why folding is wrong here even though `wrappedKeyAssociatedData` folds a
  // uuid next door, and the two are not inconsistent.** A fold is a defence
  // against a value arriving from *elsewhere* — the factor id there is minted by
  // this client and nothing upstream hands that grammar a canonical spelling, so
  // the fold is where one gets made. These two fields arrive from nowhere at
  // all: they are chosen from a list **this module publishes**, in the spelling
  // this module publishes them in. There is no second spelling in existence to
  // be tolerant of, so a fold could only *invent* one — and it would invent it
  // at the sealing end, which is exactly where the damage cannot be undone.
  const MISCASED = [
    {
      what: 'a legal table with one capital letter',
      table: `${LEGAL.table[0].toUpperCase()}${LEGAL.table.slice(1)}`,
      column: LEGAL.column,
    },
    {
      what: 'a legal table shouted',
      table: LEGAL.table.toUpperCase(),
      column: LEGAL.column,
    },
    {
      what: 'a legal column shouted',
      table: LEGAL.table,
      column: LEGAL.column.toUpperCase(),
    },
  ];

  it('miscases exactly one field and lands outside the list either way', () => {
    // Arrange
    // The guard on the three rows above, and it has two halves that catch
    // different mistakes. A row whose value happened to equal the legal one
    // would assert a refusal of something legal — which is how a reader ends up
    // deleting the check. And a row that reached the list some other way would
    // pass for a reason that has nothing to do with case.
    const pairs = new Set<string>(NARRATIVE_FIELDS.map(fieldKey));

    // Act, Assert
    for (const { table, column } of MISCASED) {
      expect(`${table}|${column}`).not.toBe(fieldKey(LEGAL));
      expect(pairs.has(`${table}|${column}`)).toBe(false);

      // **And each one folds back onto a legal pair**, which is the hazard
      // stated as an assertion rather than as prose: these are precisely the
      // values a case-blind lookup would accept, so a fold added later cannot
      // make this describe green while these rows still refuse.
      expect(pairs.has(`${table.toLowerCase()}|${column.toLowerCase()}`)).toBe(
        true,
      );
    }
  });

  it.each(
    MISCASED.flatMap((subject) =>
      BINDING_ENTRY_POINTS.map((entry) => ({ ...subject, ...entry })),
    ),
  )('refuses $what out of $where', async ({ table, column, call }) => {
    // Arrange
    const key = await importNonExtractableContentKey();

    // Act
    const error = await thrownBy(() =>
      call(key, anIllegalBinding(table, column, ROW_A)),
    );

    // Assert
    expect(error).toBeInstanceOf(NarrativeFieldMisuseError);
  });

  // **The ordering, which none of the cases above can see.**
  //
  // They ask which *type* was thrown and never *when*. A `refuseInvalidBinding`
  // moved below the cipher — or a `sealNarrativeField` that sealed first and
  // threw on the way out — rejects with the byte-identical object, and every
  // binding case in this file stays green. The module says as much at the check
  // itself: the ordering is held by construction and by nothing executable, and
  // a reader who assumed otherwise would move one of these checks under the
  // cipher with a clean run behind them.
  //
  // It is not a stylistic difference on the reading side. A module that called
  // `decrypt` and refused afterwards has already put the account's narrative
  // text in memory under a binding it had already decided was illegal, and then
  // reported a failure — the thing the refusal exists to prevent, done, and then
  // apologised for.
  //
  // The seam is the one `refuseExtractableKey`'s two cases already use: spies on
  // `crypto.subtle.encrypt` and `crypto.subtle.decrypt`, calling through,
  // because a substituted cipher would make the controls below statements about
  // the substitute. Both refusals and both operations, because that is four
  // orderings and each is one edit away from the other three.
  const PRE_CIPHER_SUBJECTS = [
    {
      what: 'a pair the module does not list',
      binding: anIllegalBinding('transactions', 'name', ROW_A),
    },
    {
      what: 'a row id in a spelling the module refuses',
      binding: anIllegalBinding(LEGAL.table, LEGAL.column, ROW_A.toUpperCase()),
    },
  ];

  it.each(PRE_CIPHER_SUBJECTS)(
    'reaches neither cipher over $what',
    async ({ binding }) => {
      // Arrange
      // The arranging seal happens *before* the spies are installed, so nothing
      // but the two calls under test can reach them.
      const key = await importNonExtractableContentKey();
      const legal = bindingFor(LEGAL.table, LEGAL.column, ROW_A);
      const wire = await sealNarrativeField(key, 'Coffee', legal);
      const encrypt = vi.spyOn(crypto.subtle, 'encrypt');
      const decrypt = vi.spyOn(crypto.subtle, 'decrypt');

      // Act
      const fromSeal = await thrownBy(() =>
        sealNarrativeField(key, 'Coffee', binding),
      );
      const fromOpen = await thrownBy(() =>
        openNarrativeField(key, wire, binding),
      );

      // Assert
      // The type, so a rejection that came out of the cipher for some other
      // reason could not be read as the refusal this case is about.
      expect(fromSeal).toBeInstanceOf(NarrativeFieldMisuseError);
      expect(fromOpen).toBeInstanceOf(NarrativeFieldMisuseError);

      // Both ciphers, on both calls. A seal that ran `encrypt` is the writing
      // half; an open that ran `decrypt` is the reading half; and each spy also
      // covers the other operation reaching for the wrong primitive.
      expect(encrypt).not.toHaveBeenCalled();
      expect(decrypt).not.toHaveBeenCalled();

      // The controls, and the spies need them twice over: they say the refusals
      // are about the binding rather than about the way this case was assembled,
      // and they say the spies are watching the calls this module really makes.
      // Without them, a spy on a method nothing reaches reports "never called"
      // perfectly.
      await expect(
        sealNarrativeField(key, 'Coffee', legal),
      ).resolves.toBeTypeOf('string');
      await expect(openNarrativeField(key, wire, legal)).resolves.toBe(
        'Coffee',
      );
      expect(encrypt).toHaveBeenCalledTimes(1);
      expect(decrypt).toHaveBeenCalledTimes(1);

      encrypt.mockRestore();
      decrypt.mockRestore();
    },
  );
});

// The frozen vectors. Fixed key, fixed nonce, fixed binding, fixed plaintext —
// one exact envelope each, computed outside this codebase.
//
// What they buy is a target: any second implementation has to reproduce these
// bytes, and this file is the only artifact that says what "the same narrative
// field format" means without pointing at code. What they catch is every silent
// divergence a round trip cannot see — a nonce that moved behind the ciphertext,
// a truncated tag, the associated data built in another order or another
// encoding, a version byte folded into the authenticated data.
//
// If one of these goes red the question is never "what is the new value". It is
// which input changed, because each of those changes makes every field already
// written unreadable by the client that wrote it.
describe('the frozen narrative-field vectors', () => {
  it('reads the vector file this spec was written against', () => {
    // Arrange, Act
    // The file is read at module load; this case is the control on that read.
    // Without it, a path that has gone wrong, a file that lost its vectors in an
    // edit, or a reader that accepted anything all report "nothing found"
    // perfectly — and every case keyed on the vector list below passes by
    // running nothing at all.
    const names = VECTOR_FILE.vectors.map((vector) => vector.name);

    // Assert
    expect(names).toEqual(['associated-data-only', 'ascii', 'mixed-width']);
    expect(SEALED_VECTORS).toHaveLength(2);
    expect(VECTOR_FILE.contentKeyHex).toMatch(/^[0-9a-f]{64}$/);

    // Each vector's own two halves agree, which is what makes `aadLength` worth
    // asserting against separately in the case below rather than being a
    // restatement of the hex.
    for (const vector of VECTOR_FILE.vectors) {
      expect(vector.aadHex.length).toBe(vector.aadLength * 2);
    }

    // And the reader is not a no-op. A parser that shrugged at a missing member
    // would build a vector list of empty objects out of anything, and every
    // assertion downstream would compare `undefined` to `undefined`.
    expect(() => parseVectorFile('{}')).toThrow();
    expect(() =>
      parseVectorFile(
        JSON.stringify({ contentKeyHex: '00', vectors: [{ name: 'x' }] }),
      ),
    ).toThrow();
  });

  it.each(VECTOR_FILE.vectors)(
    'rebuilds the frozen associated data of the $name vector',
    ({ binding, aadHex, aadLength }) => {
      // Arrange
      // The file checking itself, with no cipher anywhere in it. Every sealed
      // vector fails with one indistinguishable error, so without this case an
      // implementation whose grammar is wrong cannot be told from one whose
      // cipher is.
      const rebuilt = bindingFor(binding.table, binding.column, binding.rowId);

      // Act
      const associatedData = narrativeFieldAssociatedData(rebuilt);

      // Assert
      expect(toHex(associatedData)).toBe(aadHex);
      expect(associatedData).toHaveLength(aadLength);
    },
  );

  it.each(SEALED_VECTORS)(
    'seals the frozen $name plaintext to its frozen envelope',
    async ({ binding, plaintextUtf8Hex, nonceHex, envelopeHex, wire }) => {
      // Arrange
      // The nonce is the one input a caller cannot supply, so it is fed through
      // the same seam `key-envelope.spec.ts` uses and nothing else is replaced:
      // the key import, the cipher and the encoder are all production's. A
      // substituted crypto implementation would make the vector a statement
      // about the substitute.
      const nonce = fromHex(nonceHex);
      const fixedNonce = vi
        .spyOn(crypto, 'getRandomValues')
        .mockImplementation(
          <T extends ArrayBufferView | null>(buffer: T): T => {
            const bytes = new Uint8Array(
              (buffer as ArrayBufferView).buffer,
              (buffer as ArrayBufferView).byteOffset,
              (buffer as ArrayBufferView).byteLength,
            );
            bytes.set(nonce.subarray(0, bytes.length));

            return buffer;
          },
        );
      const key = await importNonExtractableContentKey();

      // The normative bytes, decoded strictly and re-encoded by the seal. The
      // hex is the contract; the file's readable caption is never touched.
      const plaintext = strictUtf8.decode(fromHex(plaintextUtf8Hex));

      // Act
      const sealed = await sealNarrativeField(
        key,
        plaintext,
        bindingFor(binding.table, binding.column, binding.rowId),
      );
      fixedNonce.mockRestore();

      // Assert
      expect(sealed).toBe(wire);

      // Both renderings of the same answer, so a wire that matched by accident
      // of the base64url alphabet — or a rendering that dropped a byte the
      // padding hid — is caught by the bytes as well as by the text.
      expect(toHex(decodeBase64Url(sealed))).toBe(envelopeHex);

      // And the decode-then-seal step above is an identity on the normative
      // bytes rather than a normalisation this file introduced.
      expect(toHex(utf8.encode(plaintext))).toBe(plaintextUtf8Hex);
    },
  );

  it.each(SEALED_VECTORS)(
    'opens the frozen $name envelope back to its frozen plaintext',
    async ({ binding, plaintextUtf8Hex, wire }) => {
      // Arrange
      // The other direction over the same bytes, with no seal in front of it and
      // no spy anywhere. A pair of functions that agree only with each other
      // passes every round trip in this file; this is the reader checked against
      // an answer it did not produce.
      const key = await importNonExtractableContentKey();

      // Act
      const opened = await openNarrativeField(
        key,
        wire,
        bindingFor(binding.table, binding.column, binding.rowId),
      );

      // Assert
      // Compared as bytes. A string comparison against the file's readable
      // caption would fold NFC and NFD together on some engines and would fail
      // for a reason that has nothing to do with this module on others.
      expect(toHex(utf8.encode(opened))).toBe(plaintextUtf8Hex);
    },
  );
});

// **The census is split by kind, and the split is the point rather than
// tidiness.** It used to be one list of names filtered on
// `typeof value === 'function'`, and a `class` is one of those at runtime — so
// `NarrativeFieldMisuseError` landed in a list called "functions" the day it was
// exported. Left there, an error type is forever counted as a function, and the
// next reader adding a second one has nothing that tells the two kinds apart:
// the module's *operations* and the *vocabulary a caller `catch`es on* would be
// one set, and a function turned into a class, or a class turned into a factory,
// would move nothing.
//
// A class is told from a function by its prototype chain and not by its name or
// its source text: `Type.prototype instanceof Error` is true for a subclass of
// `Error` under any downlevelling and false for every ordinary function this
// module declares. `kindOf` is the single place that judgement is made, so there
// is one definition of "error class" here rather than one per assertion.
//
// Each half is a **set compared to a set**, so reordering the exports or the
// expectations reddens nothing and only a widening does.
type ExportKind = 'error class' | 'function' | 'value';

function kindOf(value: unknown): ExportKind {
  if (typeof value !== 'function') {
    return 'value';
  }

  const prototype: unknown = (value as { readonly prototype?: unknown })
    .prototype;

  return typeof prototype === 'object' &&
    prototype !== null &&
    prototype instanceof Error
    ? 'error class'
    : 'function';
}

function exportedNamesOfKind(kind: ExportKind): ReadonlySet<string> {
  return new Set(
    Object.entries(narrativeCipherModule)
      .filter(([, value]) => kindOf(value) === kind)
      .map(([name]) => name),
  );
}

describe('the module surface', () => {
  it('exports these functions and no others', () => {
    // Arrange
    // A new name here is a red test and a conversation rather than a diff nobody
    // read. The shapes this is guarding against are specific: a seal that skips
    // the associated data, a lenient decoder added beside the strict one, an
    // exported helper that hands back the plaintext bytes rather than the
    // string.
    //
    // `refuseInvalidBinding` is on the list for the argument the module makes at
    // it: a caller that has to know whether a binding is usable — before a
    // `catch` that would otherwise read its own defect as a damaged stored value
    // — wants the refusal by name, and building associated data in order to
    // throw it away is the shape a reader deletes.
    const expected = new Set([
      'narrativeFieldAssociatedData',
      'openNarrativeField',
      'refuseInvalidBinding',
      'sealNarrativeField',
    ]);

    // Act
    const exported = exportedNamesOfKind('function');

    // Assert
    expect(exported).toEqual(expected);
  });

  it('exports these error types and no others', () => {
    // Arrange
    // **One type, and the count is the claim.** The module's whole distinction
    // is that everything it refuses about the *call* arrives as one type and
    // everything about a ciphertext arrives as none of it. A second error type
    // here splits that vocabulary in two, and every caller's `catch` has to
    // learn the new one or start reading a refusal it does not recognise as a
    // damaged ledger.
    const expected = new Set(['NarrativeFieldMisuseError']);

    // Act
    const exported = exportedNamesOfKind('error class');

    // Assert
    expect(exported).toEqual(expected);
  });

  it('exports these values and no others', () => {
    // Arrange
    // The non-function exports, pinned by name and not by value. Both values are
    // pinned elsewhere and by something stronger: the prefix by the frozen
    // associated data, which would catch a change to it, and the field list by
    // the length and the coverage above. Typing the prefix out here would be a
    // second copy of a string that is part of the definition of every field
    // already written.
    const expected = new Set([
      'NARRATIVE_FIELDS',
      'NARRATIVE_FIELD_AAD_PREFIX',
    ]);

    // Act
    const exported = exportedNamesOfKind('value');

    // Assert
    expect(exported).toEqual(expected);

    // `NARRATIVE_FIELDS` has to be a runtime value and not a type, or the type
    // derived from it goes back to being a hand-written union that can be
    // widened without any value moving. These two assertions are what say it is
    // still a value.
    expect(NARRATIVE_FIELD_AAD_PREFIX).toBeTypeOf('string');
    expect(Array.isArray(NARRATIVE_FIELDS)).toBe(true);
  });

  it('sorts every export into one of the three kinds', () => {
    // Arrange
    // **Trivially true today, and it is the line that stops being trivial the
    // moment `kindOf` grows a fourth answer.** Three exact sets are not a census
    // on their own: a kind nobody wrote an `it` for would leave every one of them
    // exact and the export itself invisible. This is what makes the three add up
    // to the module.
    const everyName = new Set(Object.keys(narrativeCipherModule));

    // Act
    const sorted = new Set([
      ...exportedNamesOfKind('function'),
      ...exportedNamesOfKind('error class'),
      ...exportedNamesOfKind('value'),
    ]);

    // Assert
    expect(sorted).toEqual(everyName);

    // And the classifier answers three different things about three known
    // exports, so a `kindOf` that had collapsed two kinds is named here rather
    // than inferred from whichever set went red.
    expect(kindOf(NarrativeFieldMisuseError)).toBe('error class');
    expect(kindOf(sealNarrativeField)).toBe('function');
    expect(kindOf(NARRATIVE_FIELD_AAD_PREFIX)).toBe('value');
  });
});
