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
// **The separation is asserted in three directions now.** One name under three
// tables gives three unrelated values, which is what the pair in the message
// buys; one name under two budgets gives two, which is what the tenancy buys and
// is the half of the file a self-consistent implementation cannot fake; and the
// same name at two different rows gives *one* value, which is what the absence
// of a row id buys — the exact inverse of the narrative grammar next door, and
// the reason the two are two modules.
//
// **The last of those needed a new pin, because the old one stopped meaning
// anything.** It read `blindIndexMessage.length === 2`, and under a binding the
// arity stays 2 while a `rowId` hides inside the object: the assertion would go
// green over exactly the defect it was written for. What replaces it is both
// halves — a type-level check that `BlindIndexBinding` cannot have the member at
// all, and a runtime case that pushes an extra `rowId` through and demands a
// byte-identical message.
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
import type { BlindIndexBinding, BlindIndexedField } from './blind-index';

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
  /**
   * The tenancy this vector was computed under — the file's, unless the vector
   * named one of its own.
   *
   * Resolved at parse time rather than at the call site, so that every case
   * below reads one member and the file's default is applied in exactly one
   * place. A case that read `vector.budgetId ?? FILE.budgetId` itself would be
   * one `??` away from computing the tenth vector under the first's tenancy and
   * reporting a mismatch nobody could locate.
   */
  readonly budgetId: string;
  readonly inputs: readonly string[];
  readonly normalizedUtf8Hex: string;
  readonly blindIndex: string;
}

interface FrozenVectorFile {
  readonly budgetId: string;
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

function parseVector(value: unknown, fileBudgetId: string): FrozenVector {
  if (!isRecord(value)) {
    throw new Error('A vector is not an object.');
  }

  const why = requireString(value, 'why', 'A vector');
  // The one optional member in the file. Absent means the file's, which is what
  // nine of the ten vectors say; the tenth names its own, and that pair is the
  // whole of what the message change buys.
  const own = value['budgetId'];

  if (own !== undefined && (typeof own !== 'string' || own.length === 0)) {
    throw new Error(`${why} carries a budgetId that is not a spelling.`);
  }

  return {
    why,
    table: requireString(value, 'table', why),
    column: requireString(value, 'column', why),
    budgetId: own ?? fileBudgetId,
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

  const budgetId = requireString(parsed, 'budgetId', 'The vector file');

  return {
    budgetId,
    indexKeyHex: requireString(parsed, 'indexKeyHex', 'The vector file'),
    vectors: vectors.map((vector: unknown) => parseVector(vector, budgetId)),
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
    budgetId: vector.budgetId,
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

// A resolved pair plus the tenancy the value is computed inside.
function bindingFor(
  table: string,
  column: string,
  budgetId: string,
): BlindIndexBinding {
  return { ...fieldFor(table, column), budgetId };
}

// The file's own tenancy, for the cases that are not driven by a vector. Read
// off the frozen file rather than typed out, so a case cannot key under a
// spelling no vector was ever computed with.
const FILE_BUDGET_ID = VECTOR_FILE.budgetId;

// A second tenancy of the same account, and the tenth vector's. Read off that
// vector rather than written down, so the cross-tenancy cases below and the
// frozen answer they are checked against can never drift apart.
const SECOND_BUDGET_ID = ((): string => {
  const other = VECTOR_FILE.vectors.find(
    (vector) => vector.budgetId !== FILE_BUDGET_ID,
  );

  if (other === undefined) {
    throw new Error(
      'No vector names a second budget, so the cross-tenancy cases are driven by nothing.',
    );
  }

  return other.budgetId;
})();

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
    'builds prefix, $table, $column, the budget and the normalized name for $input — $why',
    ({ table, column, budgetId, input, normalizedUtf8Hex }) => {
      // Arrange
      // The expectation is composed here rather than read whole, because the
      // file freezes the normalized *name* and the index, not the message. The
      // four leading fields plus one separator are joined as text and the frozen
      // name bytes are appended, which is the only shape in which the last field
      // can be bytes.
      const head = `${BLIND_INDEX_MESSAGE_PREFIX}${UNIT_SEPARATOR}${table}${UNIT_SEPARATOR}${column}${UNIT_SEPARATOR}${budgetId}${UNIT_SEPARATOR}`;
      const expected = `${toHex(utf8.encode(head))}${normalizedUtf8Hex}`;

      // Act
      const message = blindIndexMessage(
        bindingFor(table, column, budgetId),
        input,
      );

      // Assert
      expect(toHex(message)).toBe(expected);
    },
  );

  it('puts one separator between fields and none at either end', () => {
    // Arrange
    // The separator is `associated-data.ts`'s and never a second definition of
    // the byte. A trailing or leading one is invisible in every rendering of the
    // value and changes the bytes of everything keyed under it. Four of them
    // now, not three: prefix, table, column, budget, name is five fields.
    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);

    // Act
    const message = blindIndexMessage(binding, "Trader Joe's");
    const separators = Array.from(message).filter(
      (byte) => byte === 0x1f,
    ).length;

    // Assert
    expect(separators).toBe(4);
    expect(message[0]).not.toBe(0x1f);
    expect(message[message.length - 1]).not.toBe(0x1f);
    expect(UNIT_SEPARATOR.codePointAt(0)).toBe(0x1f);
  });

  it('cannot be handed a row id, so two rows sharing a name share a value', () => {
    // Arrange
    // **The omission is the entire point of the function**, and what used to
    // hold it no longer can. The old pin read `blindIndexMessage.length === 2`
    // over a two-parameter signature; under a binding the arity stays 2 while a
    // `rowId` sits inside the object, so that assertion goes green over exactly
    // the defect it was written for.
    //
    // Both halves are asserted instead. The type-level one is the declaration
    // below: `HasRowId` is `false` only while `BlindIndexBinding` has no such
    // member, so a `rowId` added to the type makes `const … : HasRowId = false`
    // a compile error rather than a case somebody has to remember to write.
    type HasRowId = 'rowId' extends keyof BlindIndexBinding ? true : false;
    const bindingCarriesARowId: HasRowId = false;

    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);
    // The runtime half, for a builder that does not type-check: a caller pushes
    // a row id through anyway — off a row it read, through one `as` in a mapper
    // — and the bytes must not move. An implementation that reached for
    // `binding.rowId` would key every row to its own value and answer no query
    // anybody ever writes.
    // Through `unknown`, and the detour is itself a finding: excess-property
    // checking refuses the direct assertion outright, which is the type-level
    // half saying so a second time at the one call that tries to defeat it.
    const withRowId = {
      ...binding,
      rowId: '4b8f5c2a-31d6-4f0e-9a77-2c1b8e6d0a54',
    } as unknown as BlindIndexBinding;

    // Act
    const plain = blindIndexMessage(binding, 'Duplicate');
    const carrying = blindIndexMessage(withRowId, 'Duplicate');

    // Assert
    expect(bindingCarriesARowId).toBe(false);
    expect(toHex(carrying)).toBe(toHex(plain));
  });

  it('separates the four fields from one another', () => {
    // Arrange
    // **Every entry, not the first.** An implementation keyed on entry zero and
    // constant elsewhere passes a file built from entry zero, and the frozen
    // vectors only carry answers for three of the four tables.
    const name = 'Shared name';

    // Act
    const messages = BLIND_INDEXED_FIELDS.map((field) =>
      toHex(blindIndexMessage({ ...field, budgetId: FILE_BUDGET_ID }, name)),
    );

    // Assert
    expect(new Set(messages).size).toBe(BLIND_INDEXED_FIELDS.length);
  });

  it('separates one name in one field across two budgets', () => {
    // Arrange
    // **What the fourth field is for, at the message rather than at the value.**
    // One account holding two ledgers computes both under one index key, so
    // without the tenancy in the message the two messages are byte-identical
    // and so is every digest taken over them — which is the repetition an
    // operator with full read access must not be able to see.
    const field = fieldFor('payees', 'name');
    const name = "Trader Joe's";

    // Act
    const here = blindIndexMessage(
      { ...field, budgetId: FILE_BUDGET_ID },
      name,
    );
    const there = blindIndexMessage(
      { ...field, budgetId: SECOND_BUDGET_ID },
      name,
    );

    // Assert
    // The guard that keeps the arrangement honest: the two tenancies really are
    // two, so the inequality below is not a comparison of one value with itself.
    expect(SECOND_BUDGET_ID).not.toBe(FILE_BUDGET_ID);
    expect(toHex(there)).not.toBe(toHex(here));
  });

  it('refuses a pair that is not one of the four', () => {
    // Arrange
    // The closed union is a fact about callers the compiler assembled. A table
    // name arriving as data through one assertion in a mapper has been through
    // nothing, so the pair is looked up at run time as well.
    // `payees.description` names a real table and a real column, and is refused
    // because the *pair* is not one of the four — the check `refuseInvalidBinding`
    // makes next door, for the same reason.
    const notIndexed = {
      table: 'payees',
      column: 'description',
      budgetId: FILE_BUDGET_ID,
    };

    // Act
    const refusal = (): Uint8Array =>
      blindIndexMessage(notIndexed as unknown as BlindIndexBinding, 'anything');
    const legal = (): Uint8Array =>
      blindIndexMessage(
        bindingFor('payees', 'name', FILE_BUDGET_ID),
        'anything',
      );

    // Assert
    // The legal call is asserted not to throw first, or a function that refused
    // everything — a stub included — would pass the refusal on its own.
    expect(legal).not.toThrow();
    expect(refusal).toThrow();
  });

  it.each([
    { why: 'the empty string', budgetId: '' },
    { why: 'upper-case hex', budgetId: '01A05F2C-7B19-7C3D-8E4F-5A6B7C8D9E0F' },
    {
      why: 'the bare thirty-two-digit form',
      budgetId: '01a05f2c7b197c3d8e4f5a6b7c8d9e0f',
    },
    {
      why: 'a braced spelling',
      budgetId: '{01a05f2c-7b19-7c3d-8e4f-5a6b7c8d9e0f}',
    },
    {
      why: 'a trailing space',
      budgetId: '01a05f2c-7b19-7c3d-8e4f-5a6b7c8d9e0f ',
    },
    {
      why: 'a trailing newline',
      budgetId: '01a05f2c-7b19-7c3d-8e4f-5a6b7c8d9e0f\n',
    },
  ])('refuses a budget named by $why', ({ budgetId }) => {
    // Arrange
    // **Refused and never folded**, the rule `narrative-cipher.ts` keeps about
    // its row id: the value arrives from one route in one spelling, so a fold
    // could only invent a second spelling of a value that has one — and it would
    // invent it at the writing end, where every row keyed under the invented
    // spelling can never be found again.
    //
    // **The empty string is the case that matters.** The message used to carry
    // an empty field in roughly this position, so a half-finished migration that
    // hands `''` through keys every tenancy to one value and returns the whole
    // defect wearing a flawless-looking digest.
    const binding = {
      ...fieldFor('payees', 'name'),
      budgetId,
    } as BlindIndexBinding;

    // Act
    const refusal = (): Uint8Array => blindIndexMessage(binding, 'anything');
    const legal = (): Uint8Array =>
      blindIndexMessage(
        bindingFor('payees', 'name', FILE_BUDGET_ID),
        'anything',
      );

    // Assert
    // The legal call first, or a builder that refused everything would pass
    // every row of this table on its own.
    expect(legal).not.toThrow();
    expect(refusal).toThrow();
  });
});

// ---------------------------------------------------------------------------
// The value.

describe('computeBlindIndex', () => {
  it.each(FROZEN_CASES)(
    'computes $blindIndex for $input under $table — $why',
    async ({ table, column, budgetId, input, blindIndex }) => {
      // Arrange
      // Ten vectors, every spelling of every one of them, driven from the file.

      // Act
      const computed = await computeBlindIndex(
        indexKey,
        bindingFor(table, column, budgetId),
        input,
      );

      // Assert
      expect(computed).toBe(blindIndex);
    },
  );

  it('gives one name under three tables three unrelated values', async () => {
    // Arrange
    // The separation the pair buys. The vectors are selected by their
    // *normalized* bytes rather than by table, so the case is about one name
    // under three tables and not about three rows that happen to sit together
    // in the file — and the values are recomputed rather than read, or the case
    // would assert something about JSON and nothing about this module.
    //
    // Filtered to the file's own tenancy as well, or the tenth vector joins the
    // set and the case starts asserting two separations at once — at which point
    // a table field dropped from the message is covered by the tenancy field and
    // nothing reddens.
    const sharedName = VECTOR_FILE.vectors.filter(
      (vector) =>
        vector.normalizedUtf8Hex === '747261646572206a6f652773' &&
        vector.budgetId === FILE_BUDGET_ID,
    );

    // Act
    const computed = await Promise.all(
      sharedName.map((vector) =>
        computeBlindIndex(
          indexKey,
          bindingFor(vector.table, vector.column, vector.budgetId),
          vector.inputs[0],
        ),
      ),
    );

    // Assert
    expect(sharedName.length).toBeGreaterThanOrEqual(3);
    expect(computed).toEqual(sharedName.map((vector) => vector.blindIndex));
    expect(new Set(computed).size).toBe(sharedName.length);
  });

  it('gives one name in one field under two budgets two unrelated values', async () => {
    // Arrange
    // **NFR-014, at the value.** One account, one index key, one table, one
    // column, one name — and two tenancies. Before the fourth field these two
    // calls produced byte-identical digests, so an operator holding neither key
    // could read off two rows in two tenancies that they hold the same word.
    //
    // The two are selected out of the file by their normalized bytes rather than
    // typed out, so the pair the case compares is the pair the frozen answers
    // were computed for.
    const name = "Trader Joe's";
    const pair = VECTOR_FILE.vectors.filter(
      (vector) =>
        vector.table === 'payees' &&
        vector.normalizedUtf8Hex === '747261646572206a6f652773',
    );

    // Act
    const here = await computeBlindIndex(
      indexKey,
      bindingFor('payees', 'name', FILE_BUDGET_ID),
      name,
    );
    const there = await computeBlindIndex(
      indexKey,
      bindingFor('payees', 'name', SECOND_BUDGET_ID),
      name,
    );

    // Assert
    // Both frozen answers, and not merely two values that differ: an
    // implementation that mixed the tenancy in under some grammar of its own
    // would separate the two perfectly and agree with no second client.
    expect(pair).toHaveLength(2);
    expect([here, there].sort()).toEqual(
      pair.map((vector) => vector.blindIndex).sort(),
    );
    expect(there).not.toBe(here);
  });

  it('gives one name twice in one budget one value', async () => {
    // Arrange
    // The other half, and the half a tenancy in the message could have
    // destroyed: within one budget the index is still **equal** for equal names,
    // which is what the unique constraint over a column and the lookup that
    // finds the row somebody just typed are both asking of it. An
    // implementation that mixed a nonce, a clock or a row into the fourth field
    // would separate the case above just as well and answer no query anybody
    // ever writes.
    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);

    // Act
    const first = await computeBlindIndex(indexKey, binding, "Trader Joe's");
    const second = await computeBlindIndex(
      indexKey,
      // A second object rather than the same one, so nothing can pass by
      // identity: what has to be equal is the tenancy, not the reference.
      bindingFor('payees', 'name', FILE_BUDGET_ID),
      "Trader Joe's",
    );

    // Assert
    expect(second).toBe(first);
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
        computeBlindIndex(
          indexKey,
          { ...field, budgetId: FILE_BUDGET_ID },
          name,
        ),
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
    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);

    // Act
    const value = await computeBlindIndex(indexKey, binding, "Trader Joe's");
    const decoded = decodeBase64Url(value);

    // Assert
    expect(value).toHaveLength(43);
    expect(value).not.toContain('=');
    expect(value).not.toContain('+');
    expect(value).not.toContain('/');
    expect(decoded).toHaveLength(32);
  });

  it('is deterministic for one name, key and binding', async () => {
    // Arrange
    // The whole trade. Determinism is what makes a uniqueness constraint and an
    // equality lookup work over data the operator cannot read.
    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);

    // Act
    const first = await computeBlindIndex(indexKey, binding, 'Repeatable');
    const second = await computeBlindIndex(indexKey, binding, 'Repeatable');

    // Assert
    expect(first).toBe(second);
  });

  // **Both refusals, driven, because a synchronous throw is a property of the
  // function and not of one branch.** The pair check and the tenancy check are
  // two statements in one refusal, and either could be moved above the `async`
  // frame — into a wrapper, into a caller, into a default parameter — without
  // the other moving with it. A table with one row would hold the half somebody
  // happened to write it for.
  it.each([
    {
      why: 'an illegal pair',
      illegal: {
        table: 'transactions',
        column: 'description',
        budgetId: FILE_BUDGET_ID,
      },
    },
    {
      why: 'a budget in a spelling this module refuses',
      illegal: { table: 'payees', column: 'name', budgetId: '' },
    },
  ])(
    'rejects rather than throwing synchronously on $why',
    async ({ illegal }) => {
      // Arrange
      // A synchronous throw out of a function whose signature promises a
      // `Promise` escapes past every caller's `catch` on the result. So the call
      // itself must return, and the refusal must arrive on the promise.

      // Act
      let returned: unknown;
      let threwSynchronously = false;

      try {
        returned = computeBlindIndex(
          indexKey,
          illegal as unknown as BlindIndexBinding,
          'anything',
        );
      } catch {
        threwSynchronously = true;
      }

      // Assert
      expect(threwSynchronously).toBe(false);
      expect(returned).toBeInstanceOf(Promise);
      await expect(returned).rejects.toThrow();
    },
  );

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
    const binding = bindingFor('payees', 'name', FILE_BUDGET_ID);

    // Act
    const refusal = computeBlindIndex(wrongKey, binding, "Trader Joe's");

    // Assert
    await expect(refusal).rejects.toThrow();
  });
});
