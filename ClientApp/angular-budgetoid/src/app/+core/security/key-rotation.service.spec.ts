// The driver of a key rotation, run end to end against a fake that models the
// server.
//
// **The account here is real and so is every byte of it.** The generation in
// force is drawn by `generateAccountKeys`, every factor is minted by
// `mintFactorKeypair`, every narrative column is sealed by `sealNarrativeField`
// under its own binding, every blind index is computed by `computeBlindIndex`
// under the account's index key, and the manifest is sealed by
// `sealFactorManifest`. The only thing standing in for the server is a store
// that accepts what a chunk sends, stamps the rows it named, and **implements
// the completeness gate** — refusing a completion while any narrative-bearing
// row is unstamped.
//
// **That shape is the whole reason this file is as long as it is, and the three
// shorter tests it replaces are worthless.** Acceptance criterion 6 asks that
// after a rotation no field decrypts under the previous content key, and:
//
//   * *"the client sent different ciphertext"* is true under the **same** key,
//     because every seal draws a fresh nonce;
//   * *"one field failed to open under the old key"* passes for a driver that
//     skipped an entire arm;
//   * *"the new keys differ from the old"* says nothing about any single row.
//
// What proves it is opening **every** column the fake now holds, under the
// generation a factor really adopted, and finding the text that column held.
//
// **The column censuses are driven from the source of truth.** `cellsFor` and
// `indexCellsFor` are handed a member of `NARRATIVE_FIELDS` and of
// `BLIND_INDEXED_FIELDS` and throw on a pair they do not cover, so a ninth
// narrative pair or a fifth indexed one fails these cases rather than being
// skipped in silence. `budgets.name` is the one narrative pair with no fixture
// rows, and it is named as an expected absence rather than filtered out — the
// chunk route has five arms and no budget arm, which
// `docs/business-logic/key-rotation.md` argues from FR-099.
//
// **The new generation is never taken from the driver.** It is recovered the way
// a factor recovers it: `openFactorKeypair` over that factor's own
// key-encryption key and the `encapsulated_account_keys` the fake's promotion
// wrote. A test that asked the service for its keys would be asserting against
// the thing under test, and there is no accessor to ask through anyway.
import { HttpErrorResponse } from '@angular/common/http';
import { signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  AccountApiService,
  type AccountDto,
  type AccountListResponse,
} from '@app-core/api/account-api.service';
import {
  CategoriesApiService,
  type CategoryDto,
  type CategoryListResponse,
} from '@app-core/api/categories-api.service';
import {
  CategoryGroupsApiService,
  type CategoryGroupDto,
  type CategoryGroupListResponse,
} from '@app-core/api/category-groups-api.service';
import {
  KeyRotationApiService,
  type BeginRotationRequestBody,
  type CompleteRotationRequestBody,
  type KeyRotationBegunDto,
  type KeyRotationStateDto,
  type ResealChunkRequestBody,
  type RotationInventoryDto,
  type RotationSealBody,
} from '@app-core/api/key-rotation-api.service';
import {
  MeApiService,
  type AccountKeyCustodyDto,
  type AccountKeyEntry,
} from '@app-core/api/me-api.service';
import {
  PayeesApiService,
  type PayeeDto,
  type PayeeListResponse,
} from '@app-core/api/payees-api.service';
import {
  TransactionsApiService,
  type TransactionDto,
  type TransactionListResponse,
} from '@app-core/api/transactions-api.service';
import { SessionService } from '@app-core/session/session.service';
import { Observable, of, throwError } from 'rxjs';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  generateAccountKeys,
  importAesGcmKey,
  importHmacSha256Key,
  type AccountKeys,
} from './account-keys';
import {
  BLIND_INDEXED_FIELDS,
  computeBlindIndex,
  type BlindIndexedField,
} from './blind-index';
import { mintFactorId } from './factor-id';
import { mintFactorKeypair, openFactorKeypair } from './factor-keypair';
import { sealFactorManifest } from './factor-manifest';
import {
  KeyRotationService,
  type KeyRotationFailure,
} from './key-rotation.service';
import {
  NARRATIVE_FIELDS,
  openNarrativeField,
  sealNarrativeField,
  type NarrativeField,
} from './narrative-cipher';
import { mintNarrativeRowId } from './narrative-row-id';
import type { PasskeyAssertionCeremony } from './webauthn-ceremony.service';

// The tenancy every blind index below is keyed inside. `GET /api/me` is where a
// browser really learns it; here `SessionService` is stubbed down to the one
// member this service may read.
const BUDGET_ID = '9c4b1f22-0d6e-4f31-a8b7-5e2c3d4a6b70';

// The generation the account's manifest is in when a run begins. Three rather
// than one, so a driver that read the epoch as a constant would be visible.
const EPOCH = 3;

// The chunk budget the begin publishes by default — wide enough that the whole
// account travels in one chunk, so a case that wants several says so.
const MAX_CHUNK_BYTES = 65536;

// How many rows of the fixture account carry a narrative value: two accounts,
// two payees, two groups, two categories and two of the three transactions.
const NARRATIVE_ROWS = 10;

// How many factors the fixture account holds. Four rather than one, because a
// run's whole point is that it reaches every factor from **one** assertion — and
// a one-factor account is the shape under which `seals[0]` works forever.
const FACTOR_COUNT = 4;

const ACCOUNT_NAME_FIELD = { table: 'accounts', column: 'name' } as const;
const PAYEE_NAME_FIELD = { table: 'payees', column: 'name' } as const;
const GROUP_NAME_FIELD = { table: 'category_groups', column: 'name' } as const;
const GROUP_NOTE_FIELD = {
  table: 'category_groups',
  column: 'description',
} as const;
const CATEGORY_NAME_FIELD = { table: 'categories', column: 'name' } as const;
const CATEGORY_NOTE_FIELD = {
  table: 'categories',
  column: 'description',
} as const;
const TRANSACTION_NOTE_FIELD = {
  table: 'transactions',
  column: 'description',
} as const;

const ACCOUNT_NAMES = ['Everyday', 'Savings'] as const;
const PAYEE_NAMES = ['Bakery', 'Landlord'] as const;
const GROUPS: readonly (readonly [string, string | null])[] = [
  ['Bills', 'Everything with a due date'],
  ['Fun', null],
];
const CATEGORIES: readonly (readonly [string, string | null])[] = [
  ['Rent', 'The flat'],
  ['Cinema', null],
];
const TRANSACTION_NOTES: readonly (string | null)[] = [
  'Lunch with Ada',
  'Coffee',
  null,
];

// ---------------------------------------------------------------------------
// What the fake stores, one shape per arm.
// ---------------------------------------------------------------------------

interface StoredNamedRow {
  readonly id: string;
  name: string;
  nameKey: string;
  rotationId: string | null;
}

interface StoredDescribedRow extends StoredNamedRow {
  description: string | null;
}

interface StoredTransactionRow {
  readonly id: string;
  description: string | null;
  rotationId: string | null;
}

// One factor of the fixture account: the key-encryption key a ceremony under it
// would yield, what the account-key route serves for it, and the point its
// manifest entry carries.
interface MintedFactor {
  readonly factorId: string;
  readonly keyEncryptionKey: CryptoKey;
  readonly entry: AccountKeyEntry;
  readonly point: Uint8Array;
}

// The account as it stands before any rotation.
interface Fixture {
  readonly contentKey: CryptoKey;
  readonly indexKey: CryptoKey;
  readonly factors: readonly MintedFactor[];
  readonly manifest: string;
  readonly accounts: readonly StoredNamedRow[];
  readonly payees: readonly StoredNamedRow[];
  readonly categoryGroups: readonly StoredDescribedRow[];
  readonly categories: readonly StoredDescribedRow[];
  readonly transactions: readonly StoredTransactionRow[];
  /** `${table}|${column}|${rowId}` to the text that cell really holds. */
  readonly texts: ReadonlyMap<string, string>;
}

let fixture: Fixture;

function cellKey(table: string, column: string, rowId: string): string {
  return `${table}|${column}|${rowId}`;
}

// The account's own key as a key object, from a **copy** of the bytes: the two
// import doors wipe what they are handed, so passing the draw's own arrays would
// leave the second import reading zeroes.
function contentKeyOf(keys: AccountKeys): Promise<CryptoKey> {
  return importAesGcmKey(Uint8Array.from(keys.contentKey));
}

function indexKeyOf(keys: AccountKeys): Promise<CryptoKey> {
  return importHmacSha256Key(Uint8Array.from(keys.indexKey));
}

async function mintFactor(
  seed: number,
  keys: AccountKeys,
): Promise<MintedFactor> {
  // A key-encryption key that is a function of its seed, so four seeds are four
  // factors' worth of key material and no two of them open each other's rows.
  const keyEncryptionKey = await importAesGcmKey(new Uint8Array(32).fill(seed));
  const factorId = mintFactorId();
  const minted = await mintFactorKeypair(keyEncryptionKey, factorId, keys);

  return {
    factorId,
    keyEncryptionKey,
    entry: {
      factorId,
      wrappedPrivateKey: minted.wrappedPrivateKey,
      encapsulatedAccountKeys: minted.encapsulatedAccountKeys,
    },
    point: minted.publicKey,
  };
}

async function buildFixture(): Promise<Fixture> {
  const keys = generateAccountKeys();
  const contentKey = await contentKeyOf(keys);
  const indexKey = await indexKeyOf(keys);
  const texts = new Map<string, string>();

  const sealedAt = async (
    field: NarrativeField,
    rowId: string,
    text: string,
  ): Promise<string> => {
    texts.set(cellKey(field.table, field.column, rowId), text);

    return sealNarrativeField(contentKey, text, { ...field, rowId });
  };

  const keyedAt = (field: BlindIndexedField, text: string): Promise<string> =>
    computeBlindIndex(indexKey, { ...field, budgetId: BUDGET_ID }, text);

  const namedRow = async (
    field: BlindIndexedField,
    name: string,
  ): Promise<StoredNamedRow> => {
    const id = mintNarrativeRowId();

    return {
      id,
      name: await sealedAt(field, id, name),
      nameKey: await keyedAt(field, name),
      rotationId: null,
    };
  };

  const factors: MintedFactor[] = [];

  for (let index = 0; index < FACTOR_COUNT; index += 1) {
    factors.push(await mintFactor(0x21 + index, keys));
  }

  const accounts: StoredNamedRow[] = [];

  for (const name of ACCOUNT_NAMES) {
    accounts.push(await namedRow(ACCOUNT_NAME_FIELD, name));
  }

  const payees: StoredNamedRow[] = [];

  for (const name of PAYEE_NAMES) {
    payees.push(await namedRow(PAYEE_NAME_FIELD, name));
  }

  const categoryGroups: StoredDescribedRow[] = [];

  for (const [name, note] of GROUPS) {
    const row = await namedRow(GROUP_NAME_FIELD, name);

    categoryGroups.push({
      ...row,
      description:
        note === null ? null : await sealedAt(GROUP_NOTE_FIELD, row.id, note),
    });
  }

  const categories: StoredDescribedRow[] = [];

  for (const [name, note] of CATEGORIES) {
    const row = await namedRow(CATEGORY_NAME_FIELD, name);

    categories.push({
      ...row,
      description:
        note === null
          ? null
          : await sealedAt(CATEGORY_NOTE_FIELD, row.id, note),
    });
  }

  const transactions: StoredTransactionRow[] = [];

  for (const note of TRANSACTION_NOTES) {
    const id = mintNarrativeRowId();

    transactions.push({
      id,
      description:
        note === null ? null : await sealedAt(TRANSACTION_NOTE_FIELD, id, note),
      rotationId: null,
    });
  }

  const manifest = await sealFactorManifest(
    contentKey,
    factors.map((factor) => ({
      factorId: factor.factorId,
      publicKey: factor.point,
    })),
    EPOCH,
  );

  // The draw's own bytes end here: both key objects were imported from copies,
  // and nothing below this line reads them.
  keys.contentKey.fill(0);
  keys.indexKey.fill(0);

  return {
    contentKey,
    indexKey,
    factors,
    manifest,
    accounts,
    payees,
    categoryGroups,
    categories,
    transactions,
    texts,
  };
}

// ---------------------------------------------------------------------------
// The fake server.
// ---------------------------------------------------------------------------

interface StagedRun {
  readonly rotationId: string;
  readonly manifest: string;
  readonly rotationEpoch: number;
  readonly seals: readonly RotationSealBody[];
}

function conflict(kind: string): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    statusText: 'Conflict',
    error: { conflictKind: kind },
  });
}

// What a browser is answered by, modelled closely enough that the run below is a
// real run: the five list reads, the account-key read, and the four rotation
// routes with the completeness gate and the promotion behind them.
class FakeServer {
  public readonly beginBodies: BeginRotationRequestBody[] = [];
  public readonly chunkBodies: ResealChunkRequestBody[] = [];
  public readonly completionBodies: CompleteRotationRequestBody[] = [];

  public manifest: string;
  public rotationEpoch = EPOCH;
  public readonly entries: Map<string, AccountKeyEntry>;

  public accounts: StoredNamedRow[];
  public payees: StoredNamedRow[];
  public categoryGroups: StoredDescribedRow[];
  public categories: StoredDescribedRow[];
  public transactions: StoredTransactionRow[];

  public maxChunkBytes = MAX_CHUNK_BYTES;
  /** Set to publish an inventory other than the one the rows really hold. */
  public inventoryOverride: RotationInventoryDto | null = null;
  public staged: StagedRun | null = null;

  /** Called as the first list read starts. */
  public onList: (() => void) | null = null;
  /** Called with each chunk body as it arrives, before it is applied. */
  public onChunk: ((body: ResealChunkRequestBody) => void) | null = null;
  /** The 1-based chunk to refuse, and what to refuse it with. */
  public refuseChunk: { readonly at: number; readonly error: unknown } | null =
    null;
  /** Called before each completion is judged. */
  public beforeCompletion: (() => void) | null = null;
  /** Refuses every completion with this, instead of judging one. */
  public refuseCompletion: unknown = null;
  /** Refuses the account-key read with this. */
  public refuseAccountKeys: unknown = null;
  /** Answers the account-key read with no manifest at all. */
  public serveNoManifest = false;

  constructor(seed: Fixture) {
    this.manifest = seed.manifest;
    this.entries = new Map(
      seed.factors.map((factor) => [factor.factorId, { ...factor.entry }]),
    );
    this.accounts = seed.accounts.map((row) => ({ ...row }));
    this.payees = seed.payees.map((row) => ({ ...row }));
    this.categoryGroups = seed.categoryGroups.map((row) => ({ ...row }));
    this.categories = seed.categories.map((row) => ({ ...row }));
    this.transactions = seed.transactions.map((row) => ({ ...row }));
  }

  // -- the account-key read -------------------------------------------------

  public accountKeys(): Observable<AccountKeyCustodyDto> {
    if (this.refuseAccountKeys !== null) {
      return throwError(() => this.refuseAccountKeys);
    }

    return of({
      manifest: this.serveNoManifest ? null : this.manifest,
      rotationEpoch: this.serveNoManifest ? 0 : this.rotationEpoch,
      factors: [...this.entries.values()].map((entry) => ({ ...entry })),
    });
  }

  public factorEntry(factorId: string): AccountKeyEntry {
    const entry = this.entries.get(factorId);

    if (entry === undefined) {
      throw new Error(`the fake holds no factor ${factorId}`);
    }

    return entry;
  }

  // -- the five list reads --------------------------------------------------

  public accountList(): Observable<AccountListResponse> {
    this.onList?.();

    return of({
      items: this.accounts.map(
        (row): AccountDto => ({
          id: row.id,
          name: row.name,
          type: 'Checking',
          openingBalance: 0,
          createdAtUtc: '2026-01-02T03:04:05Z',
          currencyCode: 'USD',
          currencyName: 'US Dollar',
          currencySymbol: '$',
          currencyMinorUnit: 2,
        }),
      ),
    });
  }

  public payeeList(): Observable<PayeeListResponse> {
    return of({
      items: this.payees.map(
        (row): PayeeDto => ({ id: row.id, name: row.name }),
      ),
    });
  }

  public categoryGroupList(): Observable<CategoryGroupListResponse> {
    return of({
      items: this.categoryGroups.map(
        (row, position): CategoryGroupDto => ({
          id: row.id,
          name: row.name,
          description: row.description,
          position,
        }),
      ),
    });
  }

  public categoryList(): Observable<CategoryListResponse> {
    const group = this.categoryGroups[0];

    return of({
      items: this.categories.map(
        (row, position): CategoryDto => ({
          id: row.id,
          name: row.name,
          description: row.description,
          categoryGroupId: group.id,
          // The group's own sealed name, denormalized. A driver that re-sealed
          // it under the category's binding would write a value no read of the
          // group's row could rebuild — and the chunk carries no member for it,
          // which is the point.
          categoryGroupName: group.name,
          position,
        }),
      ),
    });
  }

  public transactionList(): Observable<TransactionListResponse> {
    const account = this.accounts[0];

    return of({
      items: this.transactions.map(
        (row): TransactionDto => ({
          id: row.id,
          amount: 1234,
          date: '2026-01-02',
          description: row.description,
          createdAtUtc: '2026-01-02T03:04:05Z',
          accountId: account.id,
          accountName: account.name,
          currencyCode: 'USD',
          currencySymbol: '$',
        }),
      ),
    });
  }

  // -- the four rotation routes ---------------------------------------------

  public inventory(): RotationInventoryDto {
    return (
      this.inventoryOverride ?? {
        accounts: this.accounts.length,
        payees: this.payees.length,
        categoryGroups: this.categoryGroups.length,
        categories: this.categories.length,
        // Presence-aware, exactly as the gate is: a note-less transaction has
        // nothing to re-seal, so a chunk never names it and it is not counted.
        transactions: this.transactions.filter(
          (row) => row.description !== null,
        ).length,
        budgets: 0,
      }
    );
  }

  public state(): Observable<KeyRotationStateDto> {
    const staged = this.staged;

    // A row is not a run: a completed run leaves its staging row standing, and
    // the epoch is what tells the two apart.
    if (staged === null || staged.rotationEpoch <= this.rotationEpoch) {
      return of({ rotation: null });
    }

    return of({
      rotation: {
        rotationId: staged.rotationId,
        stagedRotationEpoch: staged.rotationEpoch,
        stagedManifest: staged.manifest,
        startedAtUtc: '2026-02-03T04:05:06Z',
        inventory: this.inventory(),
        maxChunkBytes: this.maxChunkBytes,
        seals: staged.seals.map((seal) => ({ ...seal })),
      },
    });
  }

  public begin(
    body: BeginRotationRequestBody,
  ): Observable<KeyRotationBegunDto> {
    this.beginBodies.push(body);

    const live = new Set(this.entries.keys());
    const named = body.seals.map((seal) => seal.factorId);

    if (
      named.length === 0 ||
      new Set(named).size !== named.length ||
      named.length !== live.size ||
      !named.every((factorId) => live.has(factorId))
    ) {
      return throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              errors: { seals: ['the seal set is not the factor set'] },
            },
          }),
      );
    }

    if (body.rotationEpoch !== this.rotationEpoch + 1) {
      return throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              errors: { rotationEpoch: ['not the stored epoch plus one'] },
            },
          }),
      );
    }

    this.staged = {
      rotationId: body.rotationId,
      manifest: body.manifest,
      rotationEpoch: body.rotationEpoch,
      seals: body.seals.map((seal) => ({ ...seal })),
    };

    return of({
      inventory: this.inventory(),
      maxChunkBytes: this.maxChunkBytes,
    });
  }

  public reseal(body: ResealChunkRequestBody): Observable<void> {
    this.chunkBodies.push(body);
    this.onChunk?.(body);

    const refusal = this.refuseChunk;

    if (refusal !== null && refusal.at === this.chunkBodies.length) {
      return throwError(() => refusal.error);
    }

    const staged = this.staged;

    if (staged === null || staged.rotationId !== body.rotationId) {
      return throwError(() => new HttpErrorResponse({ status: 400 }));
    }

    // Every arm is resolved before one is mutated, and the presence rule is
    // applied wherever the column is nullable.
    const named = [
      ...this.resolveNamed(this.accounts, body.accounts),
      ...this.resolveNamed(this.payees, body.payees),
    ];
    const described = [
      ...this.resolveDescribed(this.categoryGroups, body.categoryGroups),
      ...this.resolveDescribed(this.categories, body.categories),
    ];
    const noted = body.transactions.map((entry) => {
      const row = this.transactions.find(
        (candidate) => candidate.id === entry.id,
      );

      if (row === undefined) {
        // On the wire this is a 404. Here it is a **harness** fault: the driver
        // named a row this account does not hold, which no case sets up, so it
        // fails loudly rather than being modelled as an answer.
        throw new Error(`the chunk named a row this account does not hold`);
      }

      if ((row.description === null) !== (entry.description === null)) {
        // The presence rule, and the same harness argument: a reseal that
        // created a note where the column held none, or cleared one, is a
        // defect in the driver rather than an answer a case asked for.
        throw new Error(
          'the chunk changed whether a row holds a note, which the presence rule refuses',
        );
      }

      return [row, entry.description] as const;
    });

    for (const [row, entry] of named) {
      row.name = entry.name;
      row.nameKey = entry.nameKey;
      row.rotationId = staged.rotationId;
    }

    for (const [row, entry] of described) {
      row.name = entry.name;
      row.nameKey = entry.nameKey;
      row.description = entry.description;
      row.rotationId = staged.rotationId;
    }

    for (const [row, description] of noted) {
      row.description = description;
      row.rotationId = staged.rotationId;
    }

    return of(undefined);
  }

  public complete(body: CompleteRotationRequestBody): Observable<void> {
    this.completionBodies.push(body);
    this.beforeCompletion?.();

    if (this.refuseCompletion !== null) {
      return throwError(() => this.refuseCompletion);
    }

    const staged = this.staged;

    if (staged === null || staged.rotationId !== body.rotationId) {
      return throwError(() => new HttpErrorResponse({ status: 400 }));
    }

    if (staged.rotationEpoch <= this.rotationEpoch) {
      return throwError(() => conflict('rotation_already_completed'));
    }

    if (this.outstanding(staged.rotationId) > 0) {
      return throwError(() => conflict('rotation_incomplete'));
    }

    const sealed = new Set(staged.seals.map((seal) => seal.factorId));
    const live = [...this.entries.keys()];

    if (
      sealed.size !== live.length ||
      !live.every((factorId) => sealed.has(factorId))
    ) {
      return throwError(() => conflict('factor_set_moved'));
    }

    this.manifest = staged.manifest;
    this.rotationEpoch = staged.rotationEpoch;

    for (const seal of staged.seals) {
      this.entries.set(seal.factorId, {
        ...this.factorEntry(seal.factorId),
        encapsulatedAccountKeys: seal.encapsulatedAccountKeys,
      });
    }

    return of(undefined);
  }

  /** Rows carrying a narrative value and not stamped with this run. */
  public outstanding(rotationId: string): number {
    const bearing: { readonly rotationId: string | null }[] = [
      ...this.accounts,
      ...this.payees,
      ...this.categoryGroups,
      ...this.categories,
      ...this.transactions.filter((row) => row.description !== null),
    ];

    return bearing.filter((row) => row.rotationId !== rotationId).length;
  }

  private resolveNamed<TRow extends StoredNamedRow>(
    rows: readonly TRow[],
    entries: readonly {
      readonly id: string;
      readonly name: string;
      readonly nameKey: string;
    }[],
  ): (readonly [TRow, { readonly name: string; readonly nameKey: string }])[] {
    return entries.map((entry) => {
      const row = rows.find((candidate) => candidate.id === entry.id);

      if (row === undefined) {
        // On the wire this is a 404. Here it is a **harness** fault: the driver
        // named a row this account does not hold, which no case sets up, so it
        // fails loudly rather than being modelled as an answer.
        throw new Error(`the chunk named a row this account does not hold`);
      }

      return [row, entry] as const;
    });
  }

  private resolveDescribed(
    rows: readonly StoredDescribedRow[],
    entries: readonly {
      readonly id: string;
      readonly name: string;
      readonly nameKey: string;
      readonly description: string | null;
    }[],
  ): (readonly [
    StoredDescribedRow,
    {
      readonly name: string;
      readonly nameKey: string;
      readonly description: string | null;
    },
  ])[] {
    return entries.map((entry) => {
      const row = rows.find((candidate) => candidate.id === entry.id);

      if (row === undefined) {
        // On the wire this is a 404. Here it is a **harness** fault: the driver
        // named a row this account does not hold, which no case sets up, so it
        // fails loudly rather than being modelled as an answer.
        throw new Error(`the chunk named a row this account does not hold`);
      }

      if ((row.description === null) !== (entry.description === null)) {
        // The presence rule, and the same harness argument: a reseal that
        // created a note where the column held none, or cleared one, is a
        // defect in the driver rather than an answer a case asked for.
        throw new Error(
          'the chunk changed whether a row holds a note, which the presence rule refuses',
        );
      }

      return [row, entry] as const;
    });
  }
}

// ---------------------------------------------------------------------------
// The transports, each pinned against the service it stands in for.
// ---------------------------------------------------------------------------

class RotationTransport
  implements Pick<KeyRotationApiService, keyof KeyRotationApiService>
{
  readonly #server: FakeServer;

  constructor(server: FakeServer) {
    this.#server = server;
  }

  public beginRotation(
    body: BeginRotationRequestBody,
  ): Observable<KeyRotationBegunDto> {
    return this.#server.begin(body);
  }

  public resealRows(body: ResealChunkRequestBody): Observable<void> {
    return this.#server.reseal(body);
  }

  public completeRotation(body: CompleteRotationRequestBody): Observable<void> {
    return this.#server.complete(body);
  }

  public getRotationState(): Observable<KeyRotationStateDto> {
    return this.#server.state();
  }
}

// **Only `getAccountKeys`, deliberately.** A wider stub would let a driver that
// reached for `getMe`, the passkey handles or the recovery-code count go
// unnoticed, and a rotation has no business asking this route anything else.
class MeTransport implements Pick<MeApiService, 'getAccountKeys'> {
  readonly #server: FakeServer;

  constructor(server: FakeServer) {
    this.#server = server;
  }

  public getAccountKeys(): Observable<AccountKeyCustodyDto> {
    return this.#server.accountKeys();
  }
}

class SessionStub implements Pick<SessionService, 'budgetId'> {
  readonly #budgetId = signal<string | null>(BUDGET_ID);

  public readonly budgetId: Signal<string | null> = this.#budgetId.asReadonly();

  public setBudgetId(budgetId: string | null): void {
    this.#budgetId.set(budgetId);
  }
}

// ---------------------------------------------------------------------------
// The harness.
// ---------------------------------------------------------------------------

let server: FakeServer;
let session: SessionStub;

function ceremonyUnder(factor: MintedFactor): PasskeyAssertionCeremony {
  return {
    keyEncryptionKey: factor.keyEncryptionKey,
    payload: {
      credentialId: 'Y3JlZGVudGlhbA',
      clientDataJson: 'Y2xpZW50',
      authenticatorData: 'YXV0aA',
      signature: 'c2ln',
      userHandle: null,
    },
  };
}

function firstFactor(): MintedFactor {
  return fixture.factors[0];
}

function driver(): KeyRotationService {
  return TestBed.inject(KeyRotationService);
}

// The word a case expects, run through the union so a typo is a compile error
// rather than an assertion nothing can satisfy.
function word(failure: KeyRotationFailure): KeyRotationFailure {
  return failure;
}

// The generation `factor` really adopted, recovered the way that factor would:
// its **live** wrapped private key against the encapsulated value the fake's
// promotion wrote into its row.
async function adoptedGeneration(factor: MintedFactor): Promise<{
  readonly contentKey: CryptoKey;
  readonly indexKey: CryptoKey;
}> {
  const opened = await openFactorKeypair(
    factor.keyEncryptionKey,
    factor.factorId,
    server.factorEntry(factor.factorId),
  );

  return {
    contentKey: await importAesGcmKey(opened.contentKey),
    indexKey: await importHmacSha256Key(opened.indexKey),
  };
}

interface StoredCell {
  readonly rowId: string;
  readonly wire: string;
}

// Every stored cell of one narrative pair, read off the fake rather than off
// anything the driver said. **It throws on a pair it does not cover**, which is
// what makes the loops below a census: a ninth entry in `NARRATIVE_FIELDS`
// fails here instead of being skipped.
function cellsFor(field: NarrativeField): StoredCell[] {
  const pair = `${field.table}.${field.column}`;

  switch (pair) {
    case 'accounts.name':
      return server.accounts.map((row) => ({ rowId: row.id, wire: row.name }));
    case 'payees.name':
      return server.payees.map((row) => ({ rowId: row.id, wire: row.name }));
    case 'category_groups.name':
      return server.categoryGroups.map((row) => ({
        rowId: row.id,
        wire: row.name,
      }));
    case 'category_groups.description':
      return server.categoryGroups.flatMap((row) =>
        row.description === null
          ? []
          : [{ rowId: row.id, wire: row.description }],
      );
    case 'categories.name':
      return server.categories.map((row) => ({
        rowId: row.id,
        wire: row.name,
      }));
    case 'categories.description':
      return server.categories.flatMap((row) =>
        row.description === null
          ? []
          : [{ rowId: row.id, wire: row.description }],
      );
    case 'transactions.description':
      return server.transactions.flatMap((row) =>
        row.description === null
          ? []
          : [{ rowId: row.id, wire: row.description }],
      );
    case 'budgets.name':
      // The one narrative pair with no arm: the chunk route has five and a
      // budget arm would break FR-099's column grant.
      return [];
    default:
      throw new Error(`this fixture covers no rows for ${pair}`);
  }
}

function indexCellsFor(field: BlindIndexedField): StoredCell[] {
  const pair = `${field.table}.${field.column}`;

  switch (pair) {
    case 'accounts.name':
      return server.accounts.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
      }));
    case 'payees.name':
      return server.payees.map((row) => ({ rowId: row.id, wire: row.nameKey }));
    case 'categories.name':
      return server.categories.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
      }));
    case 'category_groups.name':
      return server.categoryGroups.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
      }));
    default:
      throw new Error(`this fixture covers no blind index for ${pair}`);
  }
}

function textOf(field: NarrativeField, rowId: string): string {
  const text = fixture.texts.get(cellKey(field.table, field.column, rowId));

  if (text === undefined) {
    throw new Error(
      `the fixture holds no text for ${field.table}.${field.column}`,
    );
  }

  return text;
}

// A transaction written by a second tab while a run is going: a real envelope
// under the generation still in force, because that is what such a row really
// holds and a plaintext there would be refused for the wrong reason.
async function lateTransaction(note: string): Promise<StoredTransactionRow> {
  const id = mintNarrativeRowId();

  return {
    id,
    description: await sealNarrativeField(fixture.contentKey, note, {
      ...TRANSACTION_NOTE_FIELD,
      rowId: id,
    }),
    rotationId: null,
  };
}

function rowsIn(chunk: ResealChunkRequestBody): number {
  return (
    chunk.accounts.length +
    chunk.payees.length +
    chunk.categoryGroups.length +
    chunk.categories.length +
    chunk.transactions.length
  );
}

beforeAll(async () => {
  fixture = await buildFixture();
}, 60000);

beforeEach(() => {
  server = new FakeServer(fixture);
  session = new SessionStub();

  TestBed.configureTestingModule({
    providers: [
      {
        provide: KeyRotationApiService,
        useValue: new RotationTransport(server),
      },
      { provide: MeApiService, useValue: new MeTransport(server) },
      { provide: SessionService, useValue: session },
      {
        provide: AccountApiService,
        useValue: {
          getAccounts: (): Observable<AccountListResponse> =>
            server.accountList(),
        },
      },
      {
        provide: PayeesApiService,
        useValue: {
          getPayees: (): Observable<PayeeListResponse> => server.payeeList(),
        },
      },
      {
        provide: CategoryGroupsApiService,
        useValue: {
          getCategoryGroups: (): Observable<CategoryGroupListResponse> =>
            server.categoryGroupList(),
        },
      },
      {
        provide: CategoriesApiService,
        useValue: {
          getCategories: (): Observable<CategoryListResponse> =>
            server.categoryList(),
        },
      },
      {
        provide: TransactionsApiService,
        useValue: {
          getTransactions: (): Observable<TransactionListResponse> =>
            server.transactionList(),
        },
      },
    ],
  });
});

describe('a whole account through one rotation', () => {
  it('opens every narrative column under the new content key to the text it held', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('finished');

    const { contentKey } = await adoptedGeneration(firstFactor());
    let opened = 0;

    for (const field of NARRATIVE_FIELDS) {
      const cells = cellsFor(field);
      const pair = `${field.table}.${field.column}`;

      if (field.table === 'budgets') {
        expect(cells, pair).toHaveLength(0);
        continue;
      }

      expect(cells.length, pair).toBeGreaterThan(0);

      for (const cell of cells) {
        await expect(
          openNarrativeField(contentKey, cell.wire, {
            ...field,
            rowId: cell.rowId,
          }),
          pair,
        ).resolves.toBe(textOf(field, cell.rowId));
        opened += 1;
      }
    }

    // The loop ran over something: an empty census would satisfy every
    // assertion inside it.
    expect(opened).toBe(12);
  });

  it('recomputes every blind index under the new index key, and none is what it was', async () => {
    // Arrange
    const service = driver();
    const before = new Map<string, string>(
      BLIND_INDEXED_FIELDS.flatMap((field) =>
        indexCellsFor(field).map((cell): [string, string] => [
          `${field.table}.${field.column}|${cell.rowId}`,
          cell.wire,
        ]),
      ),
    );

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    const { indexKey } = await adoptedGeneration(firstFactor());
    let checked = 0;

    for (const field of BLIND_INDEXED_FIELDS) {
      const cells = indexCellsFor(field);
      const pair = `${field.table}.${field.column}`;

      expect(cells.length, pair).toBeGreaterThan(0);

      for (const cell of cells) {
        const expected = await computeBlindIndex(
          indexKey,
          { ...field, budgetId: BUDGET_ID },
          textOf(field, cell.rowId),
        );

        expect(cell.wire, pair).toBe(expected);
        expect(cell.wire, pair).not.toBe(before.get(`${pair}|${cell.rowId}`));
        checked += 1;
      }
    }

    expect(checked).toBe(8);
  });

  it('leaves no narrative column that opens under the previous content key', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    let refused = 0;

    for (const field of NARRATIVE_FIELDS) {
      for (const cell of cellsFor(field)) {
        await expect(
          openNarrativeField(fixture.contentKey, cell.wire, {
            ...field,
            rowId: cell.rowId,
          }),
          `${field.table}.${field.column}`,
        ).rejects.toThrow();
        refused += 1;
      }
    }

    expect(refused).toBe(12);
  });

  it('stages one seal per factor from one assertion, each opening under its own factor', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(server.beginBodies).toHaveLength(1);

    const body = server.beginBodies[0];

    expect(body.seals.map((seal) => seal.factorId).sort()).toEqual(
      fixture.factors.map((factor) => factor.factorId).sort(),
    );
    expect(body.credentialId).toBe('Y3JlZGVudGlhbA');

    // Every factor opens a generation, and they all open the *same* one.
    const opened: string[] = [];

    for (const factor of fixture.factors) {
      const generation = await openFactorKeypair(
        factor.keyEncryptionKey,
        factor.factorId,
        server.factorEntry(factor.factorId),
      );

      opened.push([...generation.contentKey, ...generation.indexKey].join(','));
    }

    expect(opened).toHaveLength(FACTOR_COUNT);
    expect(new Set(opened).size).toBe(1);
  });

  it('files the next manifest one epoch above the stored one', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(server.rotationEpoch).toBe(EPOCH + 1);
    expect(server.manifest).not.toBe(fixture.manifest);
  });

  it('names five arms in every chunk and never a budget', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(server.chunkBodies.length).toBeGreaterThan(0);

    for (const chunk of server.chunkBodies) {
      expect(Object.keys(chunk).sort()).toEqual([
        'accounts',
        'categories',
        'categoryGroups',
        'payees',
        'rotationId',
        'transactions',
      ]);
    }
  });
});

describe('the progress a rotation publishes', () => {
  it('counts only rows carried by a chunk the server answered', async () => {
    // Arrange
    const service = driver();
    const seen: number[] = [];

    server.onChunk = (): void => {
      seen.push(service.progress().resealed);
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    // The first chunk arrives with nothing counted: rows in flight are not
    // rows the server has accepted.
    expect(seen[0]).toBe(0);
    expect(service.progress().records).toBe(NARRATIVE_ROWS);
    expect(service.progress().resealed).toBe(NARRATIVE_ROWS);
  });

  it('counts nothing for a chunk the server refused', async () => {
    // Arrange
    const service = driver();

    server.maxChunkBytes = 400;
    server.refuseChunk = {
      at: 2,
      error: new HttpErrorResponse({ status: 500 }),
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unreachable'));
    expect(server.chunkBodies.length).toBeGreaterThan(1);

    const accepted = rowsIn(server.chunkBodies[0]);

    expect(accepted).toBeGreaterThan(0);
    expect(service.progress().resealed).toBe(accepted);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('sizes its chunks by the budget the begin published', async () => {
    // Arrange
    const service = driver();

    server.maxChunkBytes = 400;

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(server.chunkBodies.length).toBeGreaterThan(1);

    for (const chunk of server.chunkBodies) {
      expect(JSON.stringify(chunk).length).toBeLessThanOrEqual(400);
    }
  });
});

describe('the refusals a begin owes', () => {
  it('refuses an inventory naming a budget, and posts no chunk and no completion', async () => {
    // Arrange
    const service = driver();

    server.inventoryOverride = {
      accounts: 2,
      payees: 2,
      categoryGroups: 2,
      categories: 2,
      transactions: 2,
      budgets: 1,
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unrecognised'));
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    expect(server.rotationEpoch).toBe(EPOCH);
  });

  it('refuses an arm it collected fewer rows for than the server counted', async () => {
    // Arrange
    const service = driver();

    server.inventoryOverride = {
      accounts: 2,
      payees: 5,
      categoryGroups: 2,
      categories: 2,
      transactions: 2,
      budgets: 0,
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unrecognised'));
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('accepts an arm it collected more rows for, and raises the denominator', async () => {
    // Arrange
    const service = driver();

    server.inventoryOverride = {
      accounts: 2,
      payees: 1,
      categoryGroups: 2,
      categories: 2,
      transactions: 2,
      budgets: 0,
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    // Nine published, ten collected: the denominator is what a chunk has to
    // carry, never what the begin guessed.
    expect(service.progress().records).toBe(NARRATIVE_ROWS);
    expect(service.progress().resealed).toBe(NARRATIVE_ROWS);
  });

  it('gives up after three passes when rows keep arriving, and says so', async () => {
    // Arrange
    const service = driver();
    const late = [
      await lateTransaction('one'),
      await lateTransaction('two'),
      await lateTransaction('three'),
    ];
    let added = 0;

    // A row created after each collection: the completion goes on answering
    // that the run is incomplete and nothing converges.
    server.beforeCompletion = (): void => {
      const row = late[added];

      added += 1;
      server.transactions.push(row);
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unfinished'));
    expect(server.completionBodies).toHaveLength(3);
    expect(added).toBe(3);
  });

  it('finishes when a row that arrived mid-run is picked up by a later pass', async () => {
    // Arrange
    const service = driver();
    const late = await lateTransaction('written while the run was going');
    let added = false;

    server.beforeCompletion = (): void => {
      if (added) {
        return;
      }

      added = true;
      server.transactions.push(late);
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(server.completionBodies).toHaveLength(2);
    expect(server.rotationEpoch).toBe(EPOCH + 1);
  });

  it('answers a moved factor set with its own word', async () => {
    // Arrange
    const service = driver();

    server.refuseCompletion = conflict('factor_set_moved');

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('factors-moved'));
    expect(server.completionBodies).toHaveLength(1);
  });

  it('treats a completion refused as already completed as a run that is done', async () => {
    // Arrange
    const service = driver();

    server.refuseCompletion = conflict('rotation_already_completed');

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('finished');
  });

  it('answers a 401 with the word that sends somebody back through the front door', async () => {
    // Arrange
    const service = driver();

    server.refuseAccountKeys = new HttpErrorResponse({ status: 401 });

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unauthenticated'));
    expect(server.beginBodies).toHaveLength(0);
  });

  it('answers a server that did not answer with the word that says to wait', async () => {
    // Arrange
    const service = driver();

    server.refuseAccountKeys = new HttpErrorResponse({ status: 0 });

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unreachable'));
    expect(server.beginBodies).toHaveLength(0);
  });

  it('refuses an account whose material does not agree with itself', async () => {
    // Arrange
    const service = driver();

    server.serveNoManifest = true;

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('inconsistent'));
    expect(server.beginBodies).toHaveLength(0);
  });

  it('does not begin a run while this browser has not been told its budget', async () => {
    // Arrange
    const service = driver();

    session.setBudgetId(null);

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unreachable'));
    expect(server.beginBodies).toHaveLength(0);
  });
});

describe('what the service publishes while it runs', () => {
  it('walks the three phases in order and reports a run in flight throughout', async () => {
    // Arrange
    const service = driver();
    const phases: string[] = [];
    const sample = (): void => {
      phases.push(`${service.phase()}:${String(service.running())}`);
    };

    server.onList = sample;
    server.onChunk = sample;
    server.beforeCompletion = sample;

    // Assert — before the act, so the resting state is a claim of its own.
    expect(service.phase()).toBe('idle');
    expect(service.running()).toBe(false);

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(phases).toEqual([
      'collecting:true',
      'resealing:true',
      'finishing:true',
    ]);
    expect(service.phase()).toBe('finished');
    expect(service.running()).toBe(false);
  });
});
