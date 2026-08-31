// The blind index — a keyed, deterministic fingerprint of a name, computed so a
// server holding none of the plaintext can still find the rows that share one.
//
// **Every answer below comes out of `blind-index-v1.json`**, computed outside
// this codebase, for the reason `narrative-cipher.spec.ts` gives at its own
// reader: this value is a cross-client contract, and a spec that checked the
// implementation against itself would go green on a format nobody else can
// reproduce. The index key is the file's too, so a vector file read from the
// wrong path takes this whole file down rather than leaving cases green under a
// key of its own.
//
// **The four fields are driven, never sampled, and that is the one guard of its
// kind here.** An implementation that computed the message correctly for
// `BLIND_INDEXED_FIELDS[0]` and refused, mis-keyed or silently constant-folded
// the other three would pass a suite built out of entry zero — the frozen file
// carries answers for three of the four tables, so the fourth would ride in on
// nothing. So the cases below run the census over every entry: the messages of
// all four are built and compared, and the four values a single name produces
// are asserted pairwise distinct.
//
// **The separation is asserted in both directions.** One name under three
// tables gives three unrelated values, which is what the grammar buys; and the
// same name at two different rows gives *one* value, which is what the absence
// of a row id in the message buys — the exact inverse of the narrative grammar
// next door, and the reason the two are two modules. The second half is held by
// a signature, because a function that cannot be handed a row cannot key on one.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';

import { importHmacSha256Key } from './account-keys';
import { UNIT_SEPARATOR } from './associated-data';
import { decodeBase64Url } from './base64url';
import {
  BLIND_INDEXED_FIELDS,
  BLIND_INDEX_MESSAGE_PREFIX,
  blindIndexMessage,
  computeBlindIndex,
} from './blind-index';
import type { BlindIndexedField } from './blind-index';

// ---------------------------------------------------------------------------
// The frozen vectors.

const VECTOR_FILE_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'blind-index-v1.json',
);

interface FrozenVector {
  readonly why: string;
  readonly table: string;
  readonly column: string;
  readonly inputs: readonly string[];
  readonly normalizedUtf8Hex: string;
  readonly blindIndex: string;
}

interface FrozenVectorFile {
  readonly indexKeyHex: string;
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

function requireInputs(
  source: Record<string, unknown>,
  what: string,
): readonly string[] {
  const value = source['inputs'];

  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`${what} lists no inputs.`);
  }

  return value.map((input: unknown, index: number) => {
    if (typeof input !== 'string') {
      throw new Error(`${what} carries a non-string input at ${index}.`);
    }

    return input;
  });
}

function parseVector(value: unknown): FrozenVector {
  if (!isRecord(value)) {
    throw new Error('A vector is not an object.');
  }

  const why = requireString(value, 'why', 'A vector');

  return {
    why,
    table: requireString(value, 'table', why),
    column: requireString(value, 'column', why),
    inputs: requireInputs(value, why),
    normalizedUtf8Hex: requireString(value, 'normalizedUtf8Hex', why),
    blindIndex: requireString(value, 'blindIndex', why),
  };
}

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
    indexKeyHex: requireString(parsed, 'indexKeyHex', 'The vector file'),
    vectors: vectors.map((vector: unknown) => parseVector(vector)),
  };
}

const VECTOR_FILE = parseVectorFile(readFileSync(VECTOR_FILE_PATH, 'utf8'));

// Every `(vector, input)` pair flattened, so each frozen spelling is a case of
// its own rather than one case that stops at the first failure.
const FROZEN_CASES = VECTOR_FILE.vectors.flatMap((vector) =>
  vector.inputs.map((input) => ({
    why: vector.why,
    table: vector.table,
    column: vector.column,
    input,
    normalizedUtf8Hex: vector.normalizedUtf8Hex,
    blindIndex: vector.blindIndex,
  })),
);

const utf8 = new TextEncoder();

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

// The pair a frozen vector names, resolved to the value the closed union holds.
// Resolving rather than casting is what makes a vector naming a table this
// product does not index an error here, instead of a value that types as a legal
// pair and is not one.
function fieldFor(table: string, column: string): BlindIndexedField {
  const found = BLIND_INDEXED_FIELDS.find(
    (candidate) => candidate.table === table && candidate.column === column,
  );

  if (found === undefined) {
    throw new Error(`${table}.${column} is not a blind-indexed field.`);
  }

  return found;
}

// ---------------------------------------------------------------------------
// The key.

// One key for the whole file, imported once. `importHmacSha256Key` zero-fills
// the material it was handed, so the bytes are rebuilt from hex at the point of
// import and never held.
let indexKey: CryptoKey;

beforeAll(async () => {
  indexKey = await importHmacSha256Key(fromHex(VECTOR_FILE.indexKeyHex));
});

// ---------------------------------------------------------------------------
// The census of the field list.

describe('BLIND_INDEXED_FIELDS', () => {
  it('names exactly FR-070s four, in both directions', () => {
    // Arrange
    // **Compared as a set, so reordering never reddens this.** The order of the
    // array changes no value the product computes, and a spec that pinned it
    // would refuse a harmless edit while catching nothing. What is pinned is
    // membership, in both directions: a fifth pair added here and a pair
    // dropped from here are both defects with no symptom at the time they
    // happen — a dropped pair simply stops being indexed, and every index
    // already written under it is never computed again.
    const expected = [
      'accounts.name',
      'categories.name',
      'category_groups.name',
      'payees.name',
    ];

    // Act
    const declared = BLIND_INDEXED_FIELDS.map(
      (field) => `${field.table}.${field.column}`,
    );

    // Assert
    expect([...declared].sort()).toEqual(expected);
    expect(declared).toHaveLength(expected.length);
  });

  it('opens every message with the versioned prefix, exactly', () => {
    // Arrange
    // Part of the definition of every value already computed. It also keeps this
    // grammar apart from the narrative one at the first field: a message opening
    // with the narrative prefix could collide with one of its associated-data
    // values, and an index equal to somebody's associated data is a fact neither
    // side would ever notice.

    // Act
    const prefix = BLIND_INDEX_MESSAGE_PREFIX;

    // Assert
    expect(prefix).toBe('budgetoid/blind-index/v1');
  });
});

// ---------------------------------------------------------------------------
// The message.

describe('blindIndexMessage', () => {
  it.each(FROZEN_CASES)(
    'builds prefix, $table, $column and the normalized name for $input — $why',
    ({ table, column, input, normalizedUtf8Hex }) => {
      // Arrange
      // The expectation is composed here rather than read whole, because the
      // file freezes the normalized *name* and the index, not the message. The
      // three leading fields plus one separator are joined as text and the
      // frozen name bytes are appended, which is the only shape in which the
      // last field can be bytes.
      const head = `${BLIND_INDEX_MESSAGE_PREFIX}${UNIT_SEPARATOR}${table}${UNIT_SEPARATOR}${column}${UNIT_SEPARATOR}`;
      const expected = `${toHex(utf8.encode(head))}${normalizedUtf8Hex}`;

      // Act
      const message = blindIndexMessage(fieldFor(table, column), input);

      // Assert
      expect(toHex(message)).toBe(expected);
    },
  );

  it('puts one separator between fields and none at either end', () => {
    // Arrange
    // The separator is `associated-data.ts`'s and never a second definition of
    // the byte. A trailing or leading one is invisible in every rendering of the
    // value and changes the bytes of everything keyed under it.
    const field = fieldFor('payees', 'name');

    // Act
    const message = blindIndexMessage(field, "Trader Joe's");
    const separators = Array.from(message).filter(
      (byte) => byte === 0x1f,
    ).length;

    // Assert
    expect(separators).toBe(3);
    expect(message[0]).not.toBe(0x1f);
    expect(message[message.length - 1]).not.toBe(0x1f);
    expect(UNIT_SEPARATOR.codePointAt(0)).toBe(0x1f);
  });

  it('takes no row id, so two rows sharing a name share a value', () => {
    // Arrange
    // **The omission is the entire point of the function**, and the assertion
    // that holds it is a signature rather than a value: a row in the message
    // would make every index unique by construction — still stable, still
    // computing, still looking exactly like a working blind index, and answering
    // no query anybody ever writes.
    //
    // The declaration below is the check. A function requiring a third argument
    // is not assignable to a two-parameter type, so growing a row id here is a
    // compile error rather than a case somebody has to remember to write. The
    // arity assertion is its runtime half, for the same claim under a builder
    // that does not type-check.
    const signature: (
      field: BlindIndexedField,
      plaintext: string,
    ) => Uint8Array = blindIndexMessage;

    // Act
    const first = blindIndexMessage(fieldFor('payees', 'name'), 'Duplicate');
    const second = blindIndexMessage(fieldFor('payees', 'name'), 'Duplicate');

    // Assert
    expect(signature).toBe(blindIndexMessage);
    expect(blindIndexMessage.length).toBe(2);
    expect(toHex(first)).toBe(toHex(second));
  });

  it('separates the four fields from one another', () => {
    // Arrange
    // **Every entry, not the first.** An implementation keyed on entry zero and
    // constant elsewhere passes a file built from entry zero, and the frozen
    // vectors only carry answers for three of the four tables.
    const name = 'Shared name';

    // Act
    const messages = BLIND_INDEXED_FIELDS.map((field) =>
      toHex(blindIndexMessage(field, name)),
    );

    // Assert
    expect(new Set(messages).size).toBe(BLIND_INDEXED_FIELDS.length);
  });

  it('refuses a pair that is not one of the four', () => {
    // Arrange
    // The closed union is a fact about callers the compiler assembled. A table
    // name arriving as data through one assertion in a mapper has been through
    // nothing, so the pair is looked up at run time as well.
    // `payees.description` names a real table and a real column, and is refused
    // because the *pair* is not one of the four — the check `refuseInvalidBinding`
    // makes next door, for the same reason.
    const notIndexed = { table: 'payees', column: 'description' };

    // Act
    const refusal = (): Uint8Array =>
      blindIndexMessage(notIndexed as unknown as BlindIndexedField, 'anything');
    const legal = (): Uint8Array =>
      blindIndexMessage(fieldFor('payees', 'name'), 'anything');

    // Assert
    // The legal call is asserted not to throw first, or a function that refused
    // everything — a stub included — would pass the refusal on its own.
    expect(legal).not.toThrow();
    expect(refusal).toThrow();
  });
});

// ---------------------------------------------------------------------------
// The value.

describe('computeBlindIndex', () => {
  it.each(FROZEN_CASES)(
    'computes $blindIndex for $input under $table — $why',
    async ({ table, column, input, blindIndex }) => {
      // Arrange
      // Nine vectors, every spelling of every one of them, driven from the file.

      // Act
      const computed = await computeBlindIndex(
        indexKey,
        fieldFor(table, column),
        input,
      );

      // Assert
      expect(computed).toBe(blindIndex);
    },
  );

  it('gives one name under three tables three unrelated values', async () => {
    // Arrange
    // The separation the grammar buys. The three vectors are selected by their
    // *normalized* bytes rather than by table, so the case is about one name
    // under three tables and not about three rows that happen to sit together
    // in the file — and the values are recomputed rather than read, or the case
    // would assert something about JSON and nothing about this module.
    const sharedName = VECTOR_FILE.vectors.filter(
      (vector) => vector.normalizedUtf8Hex === '747261646572206a6f652773',
    );

    // Act
    const computed = await Promise.all(
      sharedName.map((vector) =>
        computeBlindIndex(
          indexKey,
          fieldFor(vector.table, vector.column),
          vector.inputs[0],
        ),
      ),
    );

    // Assert
    expect(sharedName.length).toBeGreaterThanOrEqual(3);
    expect(computed).toEqual(sharedName.map((vector) => vector.blindIndex));
    expect(new Set(computed).size).toBe(sharedName.length);
  });

  it('gives one name under all four fields four distinct values', async () => {
    // Arrange
    // The other half of the census, and the half the frozen file cannot cover:
    // `category_groups` carries no `Trader Joe's` answer, so an implementation
    // that only ever keyed the first three would pass every vector case above.
    const name = "Trader Joe's";

    // Act
    const values = await Promise.all(
      BLIND_INDEXED_FIELDS.map((field) =>
        computeBlindIndex(indexKey, field, name),
      ),
    );

    // Assert
    expect(new Set(values).size).toBe(BLIND_INDEXED_FIELDS.length);
  });

  it('returns unpadded base64url of exactly forty-three characters', async () => {
    // Arrange
    // The only shape check anything downstream can make. A truncated or
    // re-encoded value is otherwise indistinguishable from a correct one: it is
    // stable, it never collides, and it is wrong for the life of the account.
    const field = fieldFor('payees', 'name');

    // Act
    const value = await computeBlindIndex(indexKey, field, "Trader Joe's");
    const decoded = decodeBase64Url(value);

    // Assert
    expect(value).toHaveLength(43);
    expect(value).not.toContain('=');
    expect(value).not.toContain('+');
    expect(value).not.toContain('/');
    expect(decoded).toHaveLength(32);
  });

  it('is deterministic for one name, key and field', async () => {
    // Arrange
    // The whole trade. Determinism is what makes a uniqueness constraint and an
    // equality lookup work over data the operator cannot read.
    const field = fieldFor('payees', 'name');

    // Act
    const first = await computeBlindIndex(indexKey, field, 'Repeatable');
    const second = await computeBlindIndex(indexKey, field, 'Repeatable');

    // Assert
    expect(first).toBe(second);
  });

  it('rejects rather than throwing synchronously on an illegal pair', async () => {
    // Arrange
    // A synchronous throw out of a function whose signature promises a `Promise`
    // escapes past every caller's `catch` on the result. So the call itself must
    // return, and the refusal must arrive on the promise.
    const notIndexed = { table: 'transactions', column: 'description' };

    // Act
    let returned: unknown;
    let threwSynchronously = false;

    try {
      returned = computeBlindIndex(
        indexKey,
        notIndexed as unknown as BlindIndexedField,
        'anything',
      );
    } catch {
      threwSynchronously = true;
    }

    // Assert
    expect(threwSynchronously).toBe(false);
    expect(returned).toBeInstanceOf(Promise);
    await expect(returned).rejects.toThrow();
  });

  it('rejects a key imported for AES-GCM rather than for HMAC', async () => {
    // Arrange
    // The key is an HMAC-SHA-256 key holding `['sign']` and nothing else.
    // Measured at the import door next door: the platform refuses `sign` under
    // an AES key with `InvalidAccessError`, so substituting one is loud rather
    // than quiet — which is the only reason this can be a test at all.
    const wrongKey = await crypto.subtle.importKey(
      'raw',
      fromHex(VECTOR_FILE.indexKeyHex),
      'AES-GCM',
      false,
      ['encrypt', 'decrypt'],
    );
    const field = fieldFor('payees', 'name');

    // Act
    const refusal = computeBlindIndex(wrongKey, field, "Trader Joe's");

    // Assert
    await expect(refusal).rejects.toThrow();
  });
});
