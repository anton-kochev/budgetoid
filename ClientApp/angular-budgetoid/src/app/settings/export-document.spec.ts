// What `export-document.ts` owes the export screen: read the server's export
// text strictly, open every sealed member through one opener, and write the
// opened document back out.
//
// The fixture is the server's text, built the way `ExportDocument.cs` shapes
// it: camelCase members in declaration order, money as unquoted numbers with
// four decimals, and every narrative member a real envelope sealed under a real
// non-extractable key for its own table, column and row. So the cases that
// open through `realOpener` run the real cipher and the real binding, and a
// value moved to another row comes back unreadable for the reason production
// would give.
//
// Money is written into the text as a raw lexeme (`-0.0100`), which
// `JSON.stringify` cannot produce, so the fixture carries a marker string and
// `serverText` swaps it for the bare number. Values are compared as scaled
// BigInts, never as text: `12.5000` coming back as `12.5` is accepted.
//
// Every case builds its own fixture and its own opener. The runner configures
// no `restoreMocks`, so a spy shared across cases would carry call history from
// one case into the next.
import { describe, expect, it, vi } from 'vitest';

import { encodeBase64Url } from '@app-core/security/base64url';
import {
  ENVELOPE_VERSION,
  MINIMUM_ENVELOPE_BYTES,
} from '@app-core/security/key-envelope';
import {
  NARRATIVE_FIELDS,
  NarrativeFieldMisuseError,
  openNarrativeField,
  sealNarrativeField,
} from '@app-core/security/narrative-cipher';
import type {
  NarrativeField,
  NarrativeFieldBinding,
} from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';

import {
  decodeExportDocument,
  openExportDocument,
  serializeExportDocument,
} from './export-document';
import type {
  OpenedExportDocument,
  SealedExportDocument,
} from './export-document';

type JsonValue = null | boolean | number | string | JsonValue[] | JsonObject;

interface JsonObject {
  [member: string]: JsonValue;
}

type Path = readonly (string | number)[];

interface SealedValue {
  readonly table: NarrativeField['table'];
  readonly column: NarrativeField['column'];
  readonly rowId: string;
  readonly wire: string;
  readonly plaintext: string;
  readonly path: Path;
}

interface Fixture {
  readonly key: CryptoKey;
  readonly doc: JsonObject;
  readonly sealed: readonly SealedValue[];
}

// Canonical lower-case hyphenated UUIDs, version 7 in shape, as the server's
// `Guid` serializes them.
const IDS = {
  user: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e01',
  budget: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e02',
  account: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e03',
  groupBills: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e04',
  groupFun: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e05',
  categoryRent: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e06',
  categoryCinema: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e07',
  payeeBakery: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e08',
  payeeCinema: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e09',
  transactionCake: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e0a',
  transactionRefund: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e0b',
} as const;

// Where each narrative table's rows live in the document; `null` is the budget
// itself. A switch over the table union, so a table added to NARRATIVE_FIELDS
// fails to compile here until somebody says where its rows are.
function rowsCollectionOf(
  table: NarrativeField['table'],
):
  | 'accounts'
  | 'categoryGroups'
  | 'categories'
  | 'payees'
  | 'transactions'
  | null {
  switch (table) {
    case 'budgets':
      return null;
    case 'accounts':
      return 'accounts';
    case 'category_groups':
      return 'categoryGroups';
    case 'categories':
      return 'categories';
    case 'payees':
      return 'payees';
    case 'transactions':
      return 'transactions';
  }
}

const AMOUNT_MARKER = '@@amount:';

// A money value as the server writes it. Replaced by the bare lexeme in
// `serverText`.
function amount(lexeme: string): string {
  return `${AMOUNT_MARKER}${lexeme}`;
}

function serverText(doc: JsonObject): string {
  return JSON.stringify(doc).replace(/"@@amount:([^"]*)"/g, '$1');
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function pathValue(root: unknown, path: Path): unknown {
  let current: unknown = root;

  for (const step of path) {
    if (typeof step === 'number') {
      current = Array.isArray(current) ? current[step] : undefined;
    } else {
      current = isJsonObject(current) ? current[step] : undefined;
    }
  }

  return current;
}

function objectAt(root: unknown, path: Path): JsonObject {
  const value = pathValue(root, path);

  if (!isJsonObject(value)) {
    throw new Error(`No object at ${path.join('.')}.`);
  }

  return value;
}

function budgetOf(doc: JsonObject): JsonObject {
  return objectAt(doc, ['budgets', 0]);
}

function rowOf(doc: JsonObject, collection: string, index: number): JsonObject {
  return objectAt(doc, ['budgets', 0, collection, index]);
}

function setAt(root: JsonObject, path: Path, value: JsonValue): void {
  const member = path[path.length - 1];

  if (typeof member !== 'string') {
    throw new Error(`No member at the end of ${path.join('.')}.`);
  }

  objectAt(root, path.slice(0, -1))[member] = value;
}

// Bytes shaped like an envelope — the version byte this client writes, then
// zeros — so a wire value built over them fails for the one reason its row
// names and no other. None of them authenticates under anything.
function envelopeShaped(length: number): Uint8Array {
  return versionedAt(ENVELOPE_VERSION, length);
}

// The same bytes led by any version. Only the key envelope's own constant is
// ever the right one here: the encapsulation and keypair framings also lead
// with 0x01 on other suites, so their constants must never stand in for it.
function versionedAt(version: number, length: number): Uint8Array {
  const bytes = new Uint8Array(length);
  bytes[0] = version;
  return bytes;
}

// Standard, padded base64 — the dialect the strict decoder refuses and `atob`
// would happily read.
function standardBase64(bytes: Uint8Array): string {
  let binary = '';

  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary);
}

// Thirty bytes whose second and third groups are 0xFBEFBE and 0xFFFFFF, which
// standard base64 spells `++++` and `////` — the two characters the URL-safe
// alphabet replaces. Thirty bytes carry no padding, so this value is refused for
// its alphabet alone.
function standardAlphabetWire(): string {
  const bytes = envelopeShaped(30);
  bytes.set([0xfb, 0xef, 0xbe, 0xff, 0xff, 0xff], 3);
  return standardBase64(bytes);
}

// Thirty-one bytes end in a two-character group whose second character carries
// four bits no encoder sets. Zero in everything an encoder emits (`A`); `B`
// sets one of them, and `atob` would drop it and return the same bytes.
function nonCanonicalTailWire(): string {
  return `${encodeBase64Url(envelopeShaped(31)).slice(0, -1)}B`;
}

function generateContentKey(): Promise<CryptoKey> {
  return crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, [
    'encrypt',
    'decrypt',
  ]);
}

async function buildFixture(): Promise<Fixture> {
  const key = await generateContentKey();
  const sealed: SealedValue[] = [];

  const seal = async (
    field: NarrativeField,
    rowId: string,
    plaintext: string,
    path: Path,
  ): Promise<string> => {
    const wire = await sealNarrativeField(key, plaintext, { ...field, rowId });
    sealed.push({
      table: field.table,
      column: field.column,
      rowId,
      wire,
      plaintext,
      path,
    });
    return wire;
  };

  const budgetPath = ['budgets', 0] as const;

  const doc: JsonObject = {
    schemaVersion: 1,
    user: {
      id: IDS.user,
      email: 'dana.fields@example.com',
      createdAtUtc: '2026-01-04T09:15:22.123456Z',
    },
    budgets: [
      {
        id: IDS.budget,
        userId: IDS.user,
        name: await seal(
          { table: 'budgets', column: 'name' },
          IDS.budget,
          'Household',
          [...budgetPath, 'name'],
        ),
        // Null, because every budget today has one: nothing sets a base
        // currency (docs/business-logic/budgets.md). A code is a named variant
        // below, not the default.
        baseCurrencyCode: null,
        createdAtUtc: '2026-01-04T09:15:23.5Z',
        accounts: [
          {
            id: IDS.account,
            budgetId: IDS.budget,
            name: await seal(
              { table: 'accounts', column: 'name' },
              IDS.account,
              'Everyday checking',
              [...budgetPath, 'accounts', 0, 'name'],
            ),
            type: 'Checking',
            openingBalance: amount('1250.5000'),
            currencyCode: 'EUR',
            createdAtUtc: '2026-01-04T09:20:00Z',
          },
        ],
        categoryGroups: [
          {
            id: IDS.groupBills,
            budgetId: IDS.budget,
            name: await seal(
              { table: 'category_groups', column: 'name' },
              IDS.groupBills,
              'Bills',
              [...budgetPath, 'categoryGroups', 0, 'name'],
            ),
            description: await seal(
              { table: 'category_groups', column: 'description' },
              IDS.groupBills,
              'Monthly fixed costs',
              [...budgetPath, 'categoryGroups', 0, 'description'],
            ),
            position: 0,
            createdAtUtc: '2026-01-04T09:21:00Z',
          },
          {
            id: IDS.groupFun,
            budgetId: IDS.budget,
            name: await seal(
              { table: 'category_groups', column: 'name' },
              IDS.groupFun,
              'Fun',
              [...budgetPath, 'categoryGroups', 1, 'name'],
            ),
            description: null,
            position: 1,
            createdAtUtc: '2026-01-04T09:22:00Z',
          },
        ],
        categories: [
          {
            id: IDS.categoryRent,
            budgetId: IDS.budget,
            categoryGroupId: IDS.groupBills,
            name: await seal(
              { table: 'categories', column: 'name' },
              IDS.categoryRent,
              'Rent',
              [...budgetPath, 'categories', 0, 'name'],
            ),
            // A note somebody wrote and then emptied, which is not "no note".
            description: await seal(
              { table: 'categories', column: 'description' },
              IDS.categoryRent,
              '',
              [...budgetPath, 'categories', 0, 'description'],
            ),
            position: 0,
            createdAtUtc: '2026-01-04T09:23:00Z',
          },
          {
            id: IDS.categoryCinema,
            budgetId: IDS.budget,
            categoryGroupId: IDS.groupFun,
            name: await seal(
              { table: 'categories', column: 'name' },
              IDS.categoryCinema,
              'Cinema',
              [...budgetPath, 'categories', 1, 'name'],
            ),
            description: null,
            position: 0,
            createdAtUtc: '2026-01-04T09:24:00Z',
          },
        ],
        payees: [
          {
            id: IDS.payeeBakery,
            budgetId: IDS.budget,
            name: await seal(
              { table: 'payees', column: 'name' },
              IDS.payeeBakery,
              'Corner Bakery',
              [...budgetPath, 'payees', 0, 'name'],
            ),
            createdAtUtc: '2026-01-05T08:00:00Z',
          },
          {
            id: IDS.payeeCinema,
            budgetId: IDS.budget,
            name: await seal(
              { table: 'payees', column: 'name' },
              IDS.payeeCinema,
              'Odeon Riverside',
              [...budgetPath, 'payees', 1, 'name'],
            ),
            createdAtUtc: '2026-01-05T08:01:00Z',
          },
        ],
        transactions: [
          {
            id: IDS.transactionCake,
            budgetId: IDS.budget,
            accountId: IDS.account,
            amount: amount('-42.1700'),
            date: '2026-02-14',
            description: await seal(
              { table: 'transactions', column: 'description' },
              IDS.transactionCake,
              'Birthday cake for Mira',
              [...budgetPath, 'transactions', 0, 'description'],
            ),
            payeeId: IDS.payeeBakery,
            categoryId: IDS.categoryCinema,
            createdAtUtc: '2026-02-14T17:45:10Z',
          },
          {
            id: IDS.transactionRefund,
            budgetId: IDS.budget,
            accountId: IDS.account,
            amount: amount('12.5000'),
            date: '2026-02-20',
            description: null,
            payeeId: null,
            categoryId: null,
            createdAtUtc: '2026-02-20T11:02:33Z',
          },
        ],
      },
    ],
  };

  return { key, doc, sealed };
}

// The opener production hands over, reduced to its two answers a real key can
// give: the text, or `unreadable` for anything that did not open. A misuse
// error is a defect in the call and is re-thrown, as custody does.
function realOpener(key: CryptoKey): NarrativeOpener {
  return async (binding, wire) => {
    try {
      return {
        state: 'text',
        value: await openNarrativeField(key, wire, binding),
      };
    } catch (error: unknown) {
      if (error instanceof NarrativeFieldMisuseError) {
        throw error;
      }
      return { state: 'unreadable' };
    }
  };
}

function answeringOpener(
  answerFor: (binding: NarrativeFieldBinding) => NarrativeText,
): NarrativeOpener {
  return (binding) => Promise.resolve(answerFor(binding));
}

function isField(
  binding: NarrativeFieldBinding,
  field: NarrativeField,
): boolean {
  return binding.table === field.table && binding.column === field.column;
}

function decoded(text: string): SealedExportDocument {
  const result = decodeExportDocument(text);

  if (result.kind !== 'decoded') {
    throw new Error('The fixture did not decode.');
  }

  return result.doc;
}

async function openedWith(
  text: string,
  open: NarrativeOpener,
): Promise<OpenedExportDocument> {
  const result = await openExportDocument(decoded(text), open);

  if (result.kind !== 'opened') {
    throw new Error(`The fixture did not open: ${result.kind}.`);
  }

  return result.doc;
}

// Every narrative member the document carries, found by walking
// NARRATIVE_FIELDS over the document rather than by a hand-kept list.
function narrativeMembersOf(
  doc: unknown,
): { readonly path: Path; readonly value: unknown }[] {
  const members: { path: Path; value: unknown }[] = [];
  const budgets = pathValue(doc, ['budgets']);

  if (!Array.isArray(budgets)) {
    return members;
  }

  for (const field of NARRATIVE_FIELDS) {
    budgets.forEach((budget: unknown, b) => {
      const collection = rowsCollectionOf(field.table);
      const rows: { row: unknown; path: Path }[] =
        collection === null
          ? [{ row: budget, path: ['budgets', b] }]
          : (() => {
              const list = pathValue(budget, [collection]);
              return Array.isArray(list)
                ? list.map((row: unknown, r) => ({
                    row,
                    path: ['budgets', b, collection, r],
                  }))
                : [];
            })();

      for (const { row, path } of rows) {
        members.push({
          path: [...path, field.column],
          value: pathValue(row, [field.column]),
        });
      }
    });
  }

  return members;
}

// Differences between two parsed documents: member order per object, array
// length, and every value except the narrative members named in `skip`.
function shapeDifferences(
  actual: unknown,
  expected: unknown,
  skip: ReadonlySet<string>,
  path: Path = [],
): string[] {
  const where = path.join('.') || '(root)';

  if (Array.isArray(expected)) {
    if (!Array.isArray(actual) || actual.length !== expected.length) {
      return [`${where}: array differs in length or kind`];
    }
    return expected.flatMap((item: unknown, i) =>
      shapeDifferences(actual[i], item, skip, [...path, i]),
    );
  }

  if (isJsonObject(expected)) {
    if (!isJsonObject(actual)) {
      return [`${where}: not an object`];
    }
    const expectedKeys = Object.keys(expected);
    const actualKeys = Object.keys(actual);
    if (JSON.stringify(actualKeys) !== JSON.stringify(expectedKeys)) {
      return [
        `${where}: members ${JSON.stringify(actualKeys)} != ${JSON.stringify(expectedKeys)}`,
      ];
    }
    return expectedKeys.flatMap((key) =>
      shapeDifferences(actual[key], expected[key], skip, [...path, key]),
    );
  }

  if (skip.has(where)) {
    return [];
  }

  return Object.is(actual, expected)
    ? []
    : [`${where}: ${String(actual)} != ${String(expected)}`];
}

// A decimal lexeme as an exact count of ten-thousandths. Throws on a value
// finer than four decimals, which would itself be a loss.
function tenThousandths(lexeme: string): bigint {
  const match = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(lexeme);

  if (match === null) {
    throw new Error(`${lexeme} is not a JSON number.`);
  }

  const [, sign, whole, fraction = '', exponent = '0'] = match;
  const shift = Number(exponent) - fraction.length + 4;
  let digits = BigInt(`${whole}${fraction}`);

  if (shift >= 0) {
    digits *= 10n ** BigInt(shift);
  } else {
    const divisor = 10n ** BigInt(-shift);
    if (digits % divisor !== 0n) {
      throw new Error(`${lexeme} is finer than four decimals.`);
    }
    digits /= divisor;
  }

  return sign === '-' ? -digits : digits;
}

// The raw number lexeme of every `amount` and `openingBalance` in `text`,
// keyed by the id of the row carrying it. Uses the reviver's source text, so
// nothing passes through a double on the way to the comparison.
function moneyLexemesById(text: string): Map<string, string> {
  const lexemes = new Map<string, string>();

  JSON.parse(
    text,
    function (
      this: unknown,
      key: string,
      value: unknown,
      context?: { readonly source?: string },
    ): unknown {
      if (
        (key === 'amount' || key === 'openingBalance') &&
        typeof value === 'number' &&
        isJsonObject(this) &&
        typeof this['id'] === 'string' &&
        context?.source !== undefined
      ) {
        lexemes.set(this['id'], context.source);
      }
      return value;
    },
  );

  return lexemes;
}

type Mutation = (doc: JsonObject) => void;

describe('export document', () => {
  it('opens every narrative member, and none of the fixture wire strings appears anywhere in the serialized output', async () => {
    // Arrange
    const fixture = await buildFixture();

    // Act
    const opened = await openedWith(
      serverText(fixture.doc),
      realOpener(fixture.key),
    );
    const output = serializeExportDocument(opened);

    // Assert
    expect(fixture.sealed.length).toBeGreaterThan(0);
    for (const value of fixture.sealed) {
      expect(pathValue(opened, value.path)).toBe(value.plaintext);
      expect(output).not.toContain(value.wire);
    }
  });

  it('opens exactly the fields NARRATIVE_FIELDS lists and hands the opener no other member', async () => {
    // Arrange
    const fixture = await buildFixture();
    const open = vi.fn<NarrativeOpener>(realOpener(fixture.key));
    const listedPairs = new Set(
      NARRATIVE_FIELDS.map((field) => `${field.table}.${field.column}`),
    );
    const sealedRequests = new Set(
      fixture.sealed.map(
        (value) =>
          `${value.table}.${value.column}|${value.rowId}|${value.wire}`,
      ),
    );

    // Act
    await openExportDocument(decoded(serverText(fixture.doc)), open);

    // Assert
    const askedPairs = new Set(
      open.mock.calls.map(([binding]) => `${binding.table}.${binding.column}`),
    );
    const askedRequests = new Set(
      open.mock.calls.map(
        ([binding, wire]) =>
          `${binding.table}.${binding.column}|${binding.rowId}|${wire}`,
      ),
    );
    expect(listedPairs.size).toBe(8);
    expect(askedPairs).toEqual(listedPairs);
    expect(askedRequests).toEqual(sealedRequests);
  });

  it.each<[string, Mutation]>([
    [
      'at the top level',
      (doc) => (doc['exportedAtUtc'] = '2026-03-01T00:00:00Z'),
    ],
    ['on the user', (doc) => (objectAt(doc, ['user'])['displayName'] = 'Dana')],
    ['on the budget', (doc) => (budgetOf(doc)['nameKey'] = 'q7c3Vd0wYk1xRz8')],
    [
      'nameKey on an account',
      (doc) => (rowOf(doc, 'accounts', 0)['nameKey'] = 'q7c3Vd0wYk1xRz8'),
    ],
    [
      'nameKey on a category group',
      (doc) => (rowOf(doc, 'categoryGroups', 0)['nameKey'] = 'q7c3Vd0wYk1xRz8'),
    ],
    [
      'nameKey on a category',
      (doc) => (rowOf(doc, 'categories', 0)['nameKey'] = 'q7c3Vd0wYk1xRz8'),
    ],
    [
      'nameKey on a payee',
      (doc) => (rowOf(doc, 'payees', 0)['nameKey'] = 'q7c3Vd0wYk1xRz8'),
    ],
    [
      'on a transaction',
      (doc) => (rowOf(doc, 'transactions', 0)['payeeName'] = 'Corner Bakery'),
    ],
  ])('refuses an undeclared member %s', async (what, mutate) => {
    // Arrange
    const fixture = await buildFixture();
    const untouched = decodeExportDocument(serverText(fixture.doc));
    mutate(fixture.doc);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(untouched.kind).toBe('decoded');
    expect(result, what).toStrictEqual({ kind: 'unrecognised' });
  });

  it.each<[string, Mutation]>([
    ['missing: top-level user', (doc) => delete doc['user']],
    ['missing: top-level budgets', (doc) => delete doc['budgets']],
    ['missing: user email', (doc) => delete objectAt(doc, ['user'])['email']],
    [
      'missing: budget baseCurrencyCode (nullable, still declared)',
      (doc) => delete budgetOf(doc)['baseCurrencyCode'],
    ],
    [
      'missing: budget payees collection',
      (doc) => delete budgetOf(doc)['payees'],
    ],
    [
      'missing: account openingBalance',
      (doc) => delete rowOf(doc, 'accounts', 0)['openingBalance'],
    ],
    [
      'missing: category group position',
      (doc) => delete rowOf(doc, 'categoryGroups', 0)['position'],
    ],
    [
      'missing: category categoryGroupId',
      (doc) => delete rowOf(doc, 'categories', 0)['categoryGroupId'],
    ],
    [
      'missing: payee createdAtUtc',
      (doc) => delete rowOf(doc, 'payees', 0)['createdAtUtc'],
    ],
    [
      'missing: a null transaction description',
      (doc) => delete rowOf(doc, 'transactions', 1)['description'],
    ],
    [
      'missing: a null transaction payeeId',
      (doc) => delete rowOf(doc, 'transactions', 1)['payeeId'],
    ],
    ['wrong type: budgets as an object', (doc) => (doc['budgets'] = {})],
    [
      'wrong type: user id as a number',
      (doc) => (objectAt(doc, ['user'])['id'] = 7),
    ],
    [
      'wrong type: budget name as a number',
      (doc) => (budgetOf(doc)['name'] = 42),
    ],
    [
      'wrong type: accounts as an object',
      (doc) => (budgetOf(doc)['accounts'] = {}),
    ],
    [
      'wrong type: account type as a number',
      (doc) => (rowOf(doc, 'accounts', 0)['type'] = 0),
    ],
    [
      'wrong type: category position as a string',
      (doc) => (rowOf(doc, 'categories', 0)['position'] = '0'),
    ],
    [
      'wrong type: transaction amount as a string',
      (doc) => (rowOf(doc, 'transactions', 0)['amount'] = '-42.1700'),
    ],
    [
      'wrong type: category group description as a boolean',
      (doc) => (rowOf(doc, 'categoryGroups', 1)['description'] = false),
    ],
    [
      'wrong type: budget baseCurrencyCode as a number',
      (doc) => (budgetOf(doc)['baseCurrencyCode'] = 978),
    ],
    [
      'wrong type: category group position 0.5, not an integer',
      (doc) => (rowOf(doc, 'categoryGroups', 0)['position'] = 0.5),
    ],
    [
      'wrong type: category position 0.5, not an integer',
      (doc) => (rowOf(doc, 'categories', 0)['position'] = 0.5),
    ],
    [
      'wrong type: transaction date as a number',
      (doc) => (rowOf(doc, 'transactions', 0)['date'] = 20260214),
    ],
    [
      'null where not nullable: account name',
      (doc) => (rowOf(doc, 'accounts', 0)['name'] = null),
    ],
    [
      'null where not nullable: payee name',
      (doc) => (rowOf(doc, 'payees', 0)['name'] = null),
    ],
    [
      'null where not nullable: category group name',
      (doc) => (rowOf(doc, 'categoryGroups', 0)['name'] = null),
    ],
    [
      'null where not nullable: category name',
      (doc) => (rowOf(doc, 'categories', 0)['name'] = null),
    ],
    [
      'null where not nullable: transaction accountId',
      (doc) => (rowOf(doc, 'transactions', 0)['accountId'] = null),
    ],
    [
      'null where not nullable: user email',
      (doc) => (objectAt(doc, ['user'])['email'] = null),
    ],
  ])('refuses a declared member that is %s', async (what, mutate) => {
    // Arrange
    const fixture = await buildFixture();
    const untouched = decodeExportDocument(serverText(fixture.doc));
    mutate(fixture.doc);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(untouched.kind).toBe('decoded');
    expect(result, what).toStrictEqual({ kind: 'unrecognised' });
  });

  it('refuses text that is not JSON', () => {
    // Act
    const result = decodeExportDocument('{"schemaVersion":1,"user":');

    // Assert
    expect(result).toStrictEqual({ kind: 'unrecognised' });
  });

  it('decodes a budget carrying a base currency code and keeps it', async () => {
    // Arrange
    // The variant of the default fixture, whose base currency is null.
    const fixture = await buildFixture();
    budgetOf(fixture.doc)['baseCurrencyCode'] = 'EUR';

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(result.kind).toBe('decoded');
    expect(pathValue(result, ['doc', 'budgets', 0, 'baseCurrencyCode'])).toBe(
      'EUR',
    );
  });

  // **A wire string the strict decoder refuses is a body this client could not
  // read, and it is refused here, before any cipher.** components.md, "Export
  // section": an envelope whose wire string the strict decoder refuses lands on
  // `unrecognised`, because the refusal comes before any cipher runs and
  // observes no key material. Left for the opener, the same value comes back
  // `unreadable` — the word that says the keys were here and the bytes were not
  // theirs, which is a different next step for a person.
  it.each<[string, () => string]>([
    ['a character outside the base64url alphabet', () => 'not*base64url!'],
    ['the standard alphabet’s + and /', standardAlphabetWire],
    ['standard padding', () => standardBase64(envelopeShaped(31))],
    [
      'a length no base64url encoding can have',
      () => `${encodeBase64Url(envelopeShaped(30))}A`,
    ],
    ['a final group carrying bits no encoder sets', nonCanonicalTailWire],
    [
      'bytes one short of the envelope floor',
      () => encodeBase64Url(envelopeShaped(MINIMUM_ENVELOPE_BYTES - 1)),
    ],
    ['no bytes at all', () => ''],
    [
      'the floor led by version 0x00',
      () => encodeBase64Url(versionedAt(0x00, MINIMUM_ENVELOPE_BYTES)),
    ],
    [
      'the floor led by the version after this client’s',
      () =>
        encodeBase64Url(
          versionedAt(ENVELOPE_VERSION + 1, MINIMUM_ENVELOPE_BYTES),
        ),
    ],
  ])(
    'refuses an account name whose wire string is %s as unrecognised',
    async (what, wire) => {
      // Arrange
      const fixture = await buildFixture();
      const untouched = decodeExportDocument(serverText(fixture.doc));
      rowOf(fixture.doc, 'accounts', 0)['name'] = wire();

      // Act
      const result = decodeExportDocument(serverText(fixture.doc));

      // Assert
      expect(untouched.kind).toBe('decoded');
      expect(result, what).toStrictEqual({ kind: 'unrecognised' });
    },
  );

  it.each(
    NARRATIVE_FIELDS.map(
      (field) => [`${field.table}.${field.column}`, field] as const,
    ),
  )(
    'refuses a wire string outside the base64url alphabet on %s as unrecognised',
    async (what, field) => {
      // Arrange
      // Every narrative member, so a check written for names alone — or for
      // one table — goes red on the member it forgot.
      const fixture = await buildFixture();
      const member = fixture.sealed.find(
        (value) => value.table === field.table && value.column === field.column,
      );
      if (member === undefined) {
        throw new Error(`The fixture seals no ${what}.`);
      }
      setAt(fixture.doc, member.path, 'not*base64url!');

      // Act
      const result = decodeExportDocument(serverText(fixture.doc));

      // Assert
      expect(result, what).toStrictEqual({ kind: 'unrecognised' });
    },
  );

  // A version this client does not write is a body it cannot read, refused
  // before any cipher — on every narrative member, so a check written for one
  // table goes red on the member it forgot.
  it.each(
    NARRATIVE_FIELDS.map(
      (field) => [`${field.table}.${field.column}`, field] as const,
    ),
  )(
    'refuses a floor-length wire string led by version 0x02 on %s as unrecognised',
    async (what, field) => {
      // Arrange
      const fixture = await buildFixture();
      const member = fixture.sealed.find(
        (value) => value.table === field.table && value.column === field.column,
      );
      if (member === undefined) {
        throw new Error(`The fixture seals no ${what}.`);
      }
      setAt(
        fixture.doc,
        member.path,
        encodeBase64Url(versionedAt(0x02, MINIMUM_ENVELOPE_BYTES)),
      );

      // Act
      const result = decodeExportDocument(serverText(fixture.doc));

      // Assert
      expect(result, what).toStrictEqual({ kind: 'unrecognised' });
    },
  );

  it('decodes a wire string of exactly the envelope floor, leaving its tag to the opener', async () => {
    // Arrange
    // Control for the rows above. Twenty-nine bytes is an envelope around an
    // empty text and is the floor, not below it; whether its tag verifies is
    // the cipher's question, answered `unreadable`, never the decoder's.
    const fixture = await buildFixture();
    rowOf(fixture.doc, 'accounts', 0)['name'] = encodeBase64Url(
      envelopeShaped(MINIMUM_ENVELOPE_BYTES),
    );

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(result.kind).toBe('decoded');
  });

  it.each(
    NARRATIVE_FIELDS.map(
      (field) => [`${field.table}.${field.column}`, field] as const,
    ),
  )('delivers nothing when %s alone is unreadable', async (what, field) => {
    // Arrange
    const fixture = await buildFixture();
    const open = answeringOpener((binding) =>
      isField(binding, field)
        ? { state: 'unreadable' }
        : { state: 'text', value: 'opened' },
    );

    // Act
    const result = await openExportDocument(
      decoded(serverText(fixture.doc)),
      open,
    );

    // Assert
    expect(result, what).toStrictEqual({ kind: 'unreadable' });
  });

  it.each(
    NARRATIVE_FIELDS.map(
      (field) => [`${field.table}.${field.column}`, field] as const,
    ),
  )('answers locked when %s alone is locked', async (what, field) => {
    // Arrange
    const fixture = await buildFixture();
    const open = answeringOpener((binding) =>
      isField(binding, field)
        ? { state: 'locked' }
        : { state: 'text', value: 'opened' },
    );

    // Act
    const result = await openExportDocument(
      decoded(serverText(fixture.doc)),
      open,
    );

    // Assert
    expect(result, what).toStrictEqual({ kind: 'locked' });
  });

  it.each<[string, NarrativeField, NarrativeField]>([
    [
      'the unreadable field comes first',
      { table: 'budgets', column: 'name' },
      { table: 'transactions', column: 'description' },
    ],
    [
      'the locked field comes first',
      { table: 'transactions', column: 'description' },
      { table: 'budgets', column: 'name' },
    ],
  ])(
    'answers locked over unreadable when %s',
    async (what, unreadableField, lockedField) => {
      // Arrange
      const fixture = await buildFixture();
      const open = answeringOpener((binding) => {
        if (isField(binding, unreadableField)) {
          return { state: 'unreadable' };
        }
        if (isField(binding, lockedField)) {
          return { state: 'locked' };
        }
        return { state: 'text', value: 'opened' };
      });

      // Act
      const result = await openExportDocument(
        decoded(serverText(fixture.doc)),
        open,
      );

      // Assert
      expect(result, what).toStrictEqual({ kind: 'locked' });
    },
  );

  it.each<[string, (fixture: Fixture) => void | Promise<void>]>([
    [
      'a payee name moved onto an account row',
      ({ doc }) => {
        rowOf(doc, 'accounts', 0)['name'] = rowOf(doc, 'payees', 0)['name'];
      },
    ],
    [
      'a category name moved into the same row description',
      ({ doc }) => {
        const row = rowOf(doc, 'categories', 0);
        row['description'] = row['name'];
      },
    ],
    [
      'two category group names swapped between their rows',
      ({ doc }) => {
        const bills = rowOf(doc, 'categoryGroups', 0);
        const fun = rowOf(doc, 'categoryGroups', 1);
        const billsName = bills['name'];
        bills['name'] = fun['name'];
        fun['name'] = billsName;
      },
    ],
    [
      'a budget name sealed under the user id instead of the budget id',
      async ({ doc, key }) => {
        budgetOf(doc)['name'] = await sealNarrativeField(key, 'Household', {
          table: 'budgets',
          column: 'name',
          rowId: IDS.user,
        });
      },
    ],
  ])(
    'binds each field to its own row, table and column: %s is unreadable',
    async (what, transplant) => {
      // Arrange
      const fixture = await buildFixture();
      const open = realOpener(fixture.key);
      const untouched = await openExportDocument(
        decoded(serverText(fixture.doc)),
        open,
      );
      await transplant(fixture);

      // Act
      const result = await openExportDocument(
        decoded(serverText(fixture.doc)),
        open,
      );

      // Assert
      expect(untouched.kind).toBe('opened');
      expect(result, what).toStrictEqual({ kind: 'unreadable' });
    },
  );

  it('keeps null descriptions and a null budget name null, and an emptied description empty', async () => {
    // Arrange
    const fixture = await buildFixture();
    budgetOf(fixture.doc)['name'] = null;

    // Act
    const opened = await openedWith(
      serverText(fixture.doc),
      realOpener(fixture.key),
    );

    // Assert
    expect(pathValue(opened, ['budgets', 0, 'name'])).toBeNull();
    expect(
      pathValue(opened, ['budgets', 0, 'categoryGroups', 1, 'description']),
    ).toBeNull();
    expect(
      pathValue(opened, ['budgets', 0, 'categories', 1, 'description']),
    ).toBeNull();
    expect(
      pathValue(opened, ['budgets', 0, 'transactions', 1, 'description']),
    ).toBeNull();
    expect(
      pathValue(opened, ['budgets', 0, 'categories', 0, 'description']),
    ).toBe('');
  });

  it('keeps every amount value exact through decode, open and serialize', async () => {
    // Arrange
    const fixture = await buildFixture();
    const amounts: readonly (readonly [string, string])[] = [
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f01', '9999999999.9999'],
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f02', '-9999999999.9999'],
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f03', '0.0001'],
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f04', '-0.0100'],
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f05', '12.5000'],
      ['01927f3a-6b1c-7d2e-8f30-4a5b6c7d8f06', '0.0000'],
    ];
    const openingBalance = '-9999999999.9999';
    rowOf(fixture.doc, 'accounts', 0)['openingBalance'] =
      amount(openingBalance);
    budgetOf(fixture.doc)['transactions'] = amounts.map(([id, lexeme]) => ({
      id,
      budgetId: IDS.budget,
      accountId: IDS.account,
      amount: amount(lexeme),
      date: '2026-03-01',
      description: null,
      payeeId: null,
      categoryId: null,
      createdAtUtc: '2026-03-01T12:00:00Z',
    }));
    const expected = new Map<string, string>([
      ...amounts,
      [IDS.account, openingBalance],
    ]);

    // Act
    const output = serializeExportDocument(
      await openedWith(serverText(fixture.doc), realOpener(fixture.key)),
    );

    // Assert
    const written = moneyLexemesById(output);
    expect([...written.keys()].sort()).toEqual([...expected.keys()].sort());
    for (const [id, lexeme] of expected) {
      expect(tenThousandths(written.get(id) ?? 'missing'), id).toBe(
        tenThousandths(lexeme),
      );
    }
  });

  it.each<[string, Mutation]>([
    [
      'an amount of exactly 1e10',
      (doc) =>
        (rowOf(doc, 'transactions', 0)['amount'] = amount('10000000000.0000')),
    ],
    [
      'an amount of exactly -1e10',
      (doc) =>
        (rowOf(doc, 'transactions', 0)['amount'] = amount('-10000000000.0000')),
    ],
    [
      'an amount written in exponent form at 1e10',
      (doc) => (rowOf(doc, 'transactions', 0)['amount'] = amount('1e10')),
    ],
    [
      'an amount far past the column',
      (doc) =>
        (rowOf(doc, 'transactions', 1)['amount'] = amount('12345678901234.5')),
    ],
    [
      'an opening balance of exactly 1e10',
      (doc) =>
        (rowOf(doc, 'accounts', 0)['openingBalance'] =
          amount('10000000000.0000')),
    ],
    // Finer than `numeric(14,4)` holds, below the ceiling. The column cannot
    // have produced it, so it is a body the server did not write — and read
    // as it stands it is a fifth decimal the file would carry as though the
    // ledger had it.
    [
      'an amount of 0.00005, five decimal places',
      (doc) => (rowOf(doc, 'transactions', 0)['amount'] = amount('0.00005')),
    ],
    [
      'an amount of 1.23456, five decimal places',
      (doc) => (rowOf(doc, 'transactions', 0)['amount'] = amount('1.23456')),
    ],
    [
      'an amount written in exponent form finer than four decimals',
      (doc) => (rowOf(doc, 'transactions', 0)['amount'] = amount('5e-5')),
    ],
    [
      'an opening balance of -0.00001, five decimal places',
      (doc) =>
        (rowOf(doc, 'accounts', 0)['openingBalance'] = amount('-0.00001')),
    ],
  ])('refuses %s', async (what, mutate) => {
    // Arrange
    const fixture = await buildFixture();
    rowOf(fixture.doc, 'transactions', 1)['amount'] = amount('9999999999.9999');
    const atTheEdge = decodeExportDocument(serverText(fixture.doc));
    mutate(fixture.doc);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(atTheEdge.kind).toBe('decoded');
    expect(result, what).toStrictEqual({ kind: 'unrecognised' });
  });

  // Control for the scale rows above: every value `numeric(14,4)` can hold
  // still decodes, as an amount and as an opening balance. Several are values
  // a double scaled by 10⁴ does not land on an integer for — measured on
  // Node: 0.0003 gives 2.9999999999999996, 12.3456 gives 123455.99999999999,
  // 1.005 gives 10049.999999999998, and both edges give …99999.02, off by more
  // than a small epsilon — so a scale check written as
  // `Number.isInteger(value * 10000)`, or with a tight tolerance, refuses real
  // money here.
  it.each([
    '9999999999.9999',
    '-9999999999.9999',
    '0.0001',
    '-0.0001',
    '-0.0100',
    '0.0003',
    '12.3456',
    '1.0050',
    '0.0000',
  ])('decodes %s as an amount and as an opening balance', async (lexeme) => {
    // Arrange
    const fixture = await buildFixture();
    rowOf(fixture.doc, 'transactions', 0)['amount'] = amount(lexeme);
    rowOf(fixture.doc, 'accounts', 0)['openingBalance'] = amount(lexeme);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(result.kind).toBe('decoded');
  });

  it.each<[string, Mutation]>([
    ['2', (doc) => (doc['schemaVersion'] = 2)],
    ['the string "1"', (doc) => (doc['schemaVersion'] = '1')],
    ['missing', (doc) => delete doc['schemaVersion']],
  ])('refuses schemaVersion %s', async (what, mutate) => {
    // Arrange
    const fixture = await buildFixture();
    const untouched = decodeExportDocument(serverText(fixture.doc));
    mutate(fixture.doc);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(untouched.kind).toBe('decoded');
    expect(result, what).toStrictEqual({ kind: 'unrecognised' });
  });

  it.each<[string, Mutation]>([
    [
      'an upper-case payee id',
      (doc) => (rowOf(doc, 'payees', 0)['id'] = IDS.payeeBakery.toUpperCase()),
    ],
    [
      'a braced account id',
      (doc) => (rowOf(doc, 'accounts', 0)['id'] = `{${IDS.account}}`),
    ],
    [
      'an upper-case budget id, which budgets.name is bound to',
      (doc) => (budgetOf(doc)['id'] = IDS.budget.toUpperCase()),
    ],
    [
      'an upper-case transaction id carrying a description',
      (doc) =>
        (rowOf(doc, 'transactions', 0)['id'] =
          IDS.transactionCake.toUpperCase()),
    ],
    [
      'an unhyphenated category group id',
      (doc) =>
        (rowOf(doc, 'categoryGroups', 0)['id'] = IDS.groupBills.replaceAll(
          '-',
          '',
        )),
    ],
    [
      'an upper-case category id',
      (doc) =>
        (rowOf(doc, 'categories', 0)['id'] = IDS.categoryRent.toUpperCase()),
    ],
  ])('refuses %s as unrecognised', async (what, mutate) => {
    // Arrange
    const fixture = await buildFixture();
    const untouched = decodeExportDocument(serverText(fixture.doc));
    mutate(fixture.doc);

    // Act
    const result = decodeExportDocument(serverText(fixture.doc));

    // Assert
    expect(untouched.kind).toBe('decoded');
    expect(result, what).toStrictEqual({ kind: 'unrecognised' });
  });

  it('lets a NarrativeFieldMisuseError from the opener propagate rather than becoming unreadable', async () => {
    // Arrange
    const fixture = await buildFixture();
    const misuse = new NarrativeFieldMisuseError(
      'A binding this call made up.',
    );
    const open: NarrativeOpener = (binding) =>
      isField(binding, { table: 'transactions', column: 'description' })
        ? Promise.reject(misuse)
        : Promise.resolve<NarrativeText>({ state: 'text', value: 'opened' });

    // Act
    const opening = openExportDocument(decoded(serverText(fixture.doc)), open);

    // Assert
    await expect(opening).rejects.toBe(misuse);
  });

  it('hands the frame back between chunks through the budget it is given', async () => {
    // Arrange
    // Twenty transactions, each a distinct row and so a distinct open, on top
    // of the fixture's other members — more than one chunk's worth. A zero
    // budget is spent at every chunk boundary, so a document opened through
    // the batch has to yield at least once; one that called the opener
    // directly never touches the budget at all.
    const fixture = await buildFixture();
    const cake = rowOf(fixture.doc, 'transactions', 0);
    budgetOf(fixture.doc)['transactions'] = [...Array(20).keys()].map(
      (i): JsonObject => ({
        ...cake,
        id: `01927f3a-6b1c-7d2e-8f30-4a5b6c7d9${String(i).padStart(3, '0')}`,
      }),
    );
    const frameYield = vi.fn(() => Promise.resolve());

    // Act
    const result = await openExportDocument(
      decoded(serverText(fixture.doc)),
      answeringOpener(() => ({ state: 'text', value: 'opened' })),
      { frameBudgetMs: 0, yield: frameYield },
    );

    // Assert
    expect(result.kind).toBe('opened');
    expect(frameYield).toHaveBeenCalled();
  });

  it('keeps every non-narrative value and the server member order', async () => {
    // Arrange
    const fixture = await buildFixture();
    const text = serverText(fixture.doc);
    const input: unknown = JSON.parse(text);
    const narrativePaths = new Set(
      narrativeMembersOf(input)
        .filter((member) => member.value !== null)
        .map((member) => member.path.join('.')),
    );

    // Act
    const opened = await openedWith(text, realOpener(fixture.key));

    // Assert
    expect(narrativePaths.size).toBe(fixture.sealed.length);
    expect(shapeDifferences(opened, input, narrativePaths)).toEqual([]);
  });

  it('writes text that parses back to the opened document', async () => {
    // Arrange
    const fixture = await buildFixture();
    const opened = await openedWith(
      serverText(fixture.doc),
      realOpener(fixture.key),
    );

    // Act
    const output = serializeExportDocument(opened);

    // Assert
    expect(JSON.parse(output)).toStrictEqual(opened);
  });
});
