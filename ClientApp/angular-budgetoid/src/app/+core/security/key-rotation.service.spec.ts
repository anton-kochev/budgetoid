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
  type AccountType,
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
  type MeDto,
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
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { EMPTY, Observable, of, throwError } from 'rxjs';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { AccountKeyCustodyService } from './account-key-custody.service';
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
import { openFactorManifest, sealFactorManifest } from './factor-manifest';
import {
  KeyRotationService,
  type KeyRotationFailure,
  type KeyRotationNameCollision,
  type KeyRotationPhase,
  type KeyRotationProgress,
} from './key-rotation.service';
import {
  NARRATIVE_FIELDS,
  openNarrativeField,
  sealNarrativeField,
  type NarrativeField,
} from './narrative-cipher';
import { mintNarrativeRowId } from './narrative-row-id';
// The device's memory of how far an account has already moved, read through the
// module that owns it rather than through a key spelled here — one key per
// account is that module's decision and its own spec is where the spelling is
// pinned.
import { highestRotationEpochSeen } from './rotation-epoch-record';
import type { NameArm } from './rotation-name-collision';
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
  // **The generation in force as bytes, kept rather than wiped**, because a
  // factor enrolled while a run is in flight has to be minted against the very
  // keys every other factor of this account already holds — and there is no way
  // back to them through the two key objects above, which are imported
  // non-extractable. One case reads it; nothing else may.
  readonly keyMaterial: AccountKeys;
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

// One manifest over a set of factors, sealed under the account's content key at
// `epoch`.
//
// **The one place in this file that writes a factor's public key into an
// object**, which is why the fixture and the two moved-set cases below all come
// through here rather than each building their own. Specs are outside
// `factor-public-key-single-source.spec.ts`' census deliberately — a fixture
// point is not a production path — and one site is still fewer than three.
function manifestOver(
  contentKey: CryptoKey,
  factors: readonly MintedFactor[],
  epoch: number,
): Promise<string> {
  return sealFactorManifest(
    contentKey,
    factors.map((factor) => ({
      factorId: factor.factorId,
      publicKey: factor.point,
    })),
    epoch,
  );
}

async function buildFixture(): Promise<Fixture> {
  const keys = generateAccountKeys();
  const keyMaterial: AccountKeys = {
    contentKey: Uint8Array.from(keys.contentKey),
    indexKey: Uint8Array.from(keys.indexKey),
  };
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

  const manifest = await manifestOver(contentKey, factors, EPOCH);

  // The draw's own bytes end here: both key objects were imported from copies,
  // and nothing below this line reads them.
  keys.contentKey.fill(0);
  keys.indexKey.fill(0);

  return {
    contentKey,
    indexKey,
    keyMaterial,
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

// What one of the four ordinary rename routes was sent. `description` is on the
// two described lists' routes and `type` and `openingBalance` on the account's;
// the recorded body is a copy of exactly what arrived, so a member a driver
// added or dropped is visible in its keys.
interface RenameBody {
  readonly name: string;
  readonly nameKey: string;
  readonly description?: string | null;
  readonly type?: AccountType;
  readonly openingBalance?: number;
}

interface RenameCall {
  readonly arm: NameArm;
  readonly method: 'PATCH' | 'PUT';
  readonly id: string;
  readonly body: Readonly<Record<string, unknown>>;
}

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

// A 409 as the API really renders one: `ConflictExceptionHandler` writes a
// problem document with a fixed title, the exception's sentence as `detail`
// and the kind as the one extension member a client branches on; the default
// `IProblemDetailsService` adds `type` and `traceId` beside them. `conflict`
// above is the minimum the driver reads; this is the whole body, so a driver
// that branched on `detail` or `title` would be seen reading prose.
function problemConflict(kind: string, detail: string): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    statusText: 'Conflict',
    url: 'https://api.budgetoid.app/api/me/key-rotation/chunks',
    error: {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
      title: 'The request conflicts with the current state of the resource.',
      status: 409,
      detail,
      conflictKind: kind,
      traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00',
    },
  });
}

// The server's own sentence for `rotation_name_collision`, from
// `NarrativeResealRepository`.
const NAME_COLLISION_DETAIL =
  'Two rows would end up with the same name under the new keys, so this chunk was refused and nothing in it was written. Rename one of them, then carry on with the rotation.';

// What a browser is answered by, modelled closely enough that the run below is a
// real run: the five list reads, the account-key read, and the four rotation
// routes with the completeness gate and the promotion behind them.
class FakeServer {
  public readonly beginBodies: BeginRotationRequestBody[] = [];
  public readonly chunkBodies: ResealChunkRequestBody[] = [];
  public readonly completionBodies: CompleteRotationRequestBody[] = [];
  /** How many times the account-key read has been made. */
  public accountKeyReads = 0;

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
  /** Refuses every chunk with this, before any of it is applied. */
  public refuseEveryChunk: unknown = null;
  /** Called before each completion is judged. */
  public beforeCompletion: (() => void) | null = null;
  /** Refuses every completion with this, instead of judging one. */
  public refuseCompletion: unknown = null;
  /** Refuses the account-key read with this. */
  public refuseAccountKeys: unknown = null;
  /** Answers the account-key read with no manifest at all. */
  public serveNoManifest = false;
  /** Refuses the staged-rotation read with this. */
  public refuseState: unknown = null;
  /** Answers the staged-rotation read by completing without emitting. */
  public answerStateWithoutEmitting = false;
  /** Every ordinary rename made, in order, as its route received it. */
  public readonly renames: RenameCall[] = [];
  /** Refuses every ordinary rename with this, before any of it is applied. */
  public refuseRename: unknown = null;
  /**
   * The members the account list serves beside a row's name, by row. A row not
   * named here is served as `Checking` with nothing opening it.
   */
  public readonly accountDetails = new Map<
    string,
    { readonly type: AccountType; readonly openingBalance: number }
  >();

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
    this.accountKeyReads += 1;

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
          type: this.accountDetails.get(row.id)?.type ?? 'Checking',
          openingBalance: this.accountDetails.get(row.id)?.openingBalance ?? 0,
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
    if (this.refuseState !== null) {
      return throwError(() => this.refuseState);
    }

    // Not a wire shape `HttpClient` produces, and it is here for what the
    // *service* does with it: `firstValueFrom` over an observable that
    // completes without emitting raises, and the read's one `catch` is what
    // decides whether that reaches its caller.
    if (this.answerStateWithoutEmitting) {
      return EMPTY;
    }

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

    const everyChunk: unknown = this.refuseEveryChunk;

    if (everyChunk !== null) {
      return throwError(() => everyChunk);
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

    // The four unique indexes, as the server has them: per list, over the
    // values the rows would hold once this chunk landed. Two rows on one value
    // refuse the whole chunk, and nothing in it is written.
    const collides = (
      rows: readonly StoredNamedRow[],
      entries: readonly { readonly id: string; readonly nameKey: string }[],
    ): boolean => {
      const after = rows.map(
        (row) =>
          entries.find((entry) => entry.id === row.id)?.nameKey ?? row.nameKey,
      );

      return new Set(after).size !== after.length;
    };

    if (
      collides(this.accounts, body.accounts) ||
      collides(this.payees, body.payees) ||
      collides(this.categoryGroups, body.categoryGroups) ||
      collides(this.categories, body.categories)
    ) {
      return throwError(() =>
        problemConflict('rotation_name_collision', NAME_COLLISION_DETAIL),
      );
    }

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

  // -- the four ordinary rename routes --------------------------------------

  /**
   * An ordinary rename, as the four routes behind it behave: the unique index
   * over the list, then both halves of the name replaced together, a note
   * replaced by whatever the body carries, and **the row's rotation stamp
   * disowned** — `Payee.Rename` and its three siblings clear it, because what a
   * rename writes is not something a chunk wrote.
   */
  public rename(
    arm: NameArm,
    method: 'PATCH' | 'PUT',
    id: string,
    body: RenameBody,
  ): Observable<void> {
    this.renames.push({ arm, method, id, body: { ...body } });

    const refusal: unknown = this.refuseRename;

    if (refusal !== null) {
      return throwError(() => refusal);
    }

    const rows = this.namedRows(arm);
    const row = rows.find((candidate) => candidate.id === id);

    if (row === undefined) {
      // A harness fault, for the chunk route's reason: no case renames a row
      // this account does not hold.
      throw new Error(`the rename named a row this account does not hold`);
    }

    if (
      rows.some(
        (candidate) =>
          candidate.id !== id && candidate.nameKey === body.nameKey,
      )
    ) {
      return throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              title: 'One or more validation errors occurred.',
              status: 400,
              errors: {
                // The member as ASP.NET spells it on the wire.
                // eslint-disable-next-line @typescript-eslint/naming-convention
                Name: ['This list already has a record by that name.'],
              },
            },
          }),
      );
    }

    row.name = body.name;
    row.nameKey = body.nameKey;
    row.rotationId = null;

    if (arm === 'categoryGroups' || arm === 'categories') {
      const described = this[arm].find((candidate) => candidate.id === id);

      if (described !== undefined) {
        described.description = body.description ?? null;
      }
    }

    return of(undefined);
  }

  public namedRows(arm: NameArm): StoredNamedRow[] {
    switch (arm) {
      case 'accounts':
        return this.accounts;
      case 'payees':
        return this.payees;
      case 'categoryGroups':
        return this.categoryGroups;
      case 'categories':
        return this.categories;
    }
  }

  /**
   * Forgets every request made so far, so that what a **later** browser posted
   * stands on its own.
   *
   * It moves no account state: the staging row, the seals and every stamp
   * survive, which is the whole of what an interrupted run leaves behind.
   */
  public forgetEveryRequest(): void {
    this.beginBodies.length = 0;
    this.chunkBodies.length = 0;
    this.completionBodies.length = 0;
    this.renames.length = 0;
    this.accountKeyReads = 0;
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

// **Two members, and the second one is not the driver's.** A rotation has no
// business asking this route anything, and every member it might have reached
// for — the passkey handles, the recovery-code count, the export — is still
// absent, so a driver that grew one would fail to inject rather than go
// unnoticed.
//
// `getSessionOwner` is here because the real `AccountKeyCustodyService` is
// standing in the injector below and reads it: an epoch record is filed per
// account, and that route is where a browser learns which account it is in. It
// is **not** a second way for this driver to learn the tenancy — `does not
// begin a run while this browser has not been told its budget` drives
// `SessionService.budgetId` to `null` and requires the run to refuse, which no
// driver reading this route instead could satisfy.
class MeTransport
  implements Pick<MeApiService, 'getAccountKeys' | 'getSessionOwner'>
{
  readonly #server: FakeServer;

  constructor(server: FakeServer) {
    this.#server = server;
  }

  public getAccountKeys(): Observable<AccountKeyCustodyDto> {
    return this.#server.accountKeys();
  }

  public getSessionOwner(): Observable<MeDto> {
    return of({ budgetId: BUDGET_ID, email: 'owner@budgetoid.test' });
  }
}

// `status` is here for the one thing the driver may read it for: a pair of
// names drawn on a screen is hidden once the session is gone.
class SessionStub implements Pick<SessionService, 'budgetId' | 'status'> {
  readonly #budgetId = signal<string | null>(BUDGET_ID);
  readonly #status = signal<SessionStatus>('authenticated');

  public readonly budgetId: Signal<string | null> = this.#budgetId.asReadonly();

  public readonly status: Signal<SessionStatus> = this.#status.asReadonly();

  public setBudgetId(budgetId: string | null): void {
    this.#budgetId.set(budgetId);
  }

  public setStatus(status: SessionStatus): void {
    this.#status.set(status);
  }

  // `SessionService.ended()` as far as these two members see it: the status
  // and the budget go in one call.
  public end(): void {
    this.#status.set('anonymous');
    this.#budgetId.set(null);
  }

  // `SessionService.established()`: the status now, and the budget only when
  // the read it starts lands — which a later `setBudgetId` stands in for.
  public establish(): void {
    this.#status.set('authenticated');
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

// **The real custody, not a double, and that is the point of the cases that
// read it.** What a finished run owes is that this tab ends up holding the
// promoted generation — which is a fact about the class that holds keys, and
// the only way to observe it is through the operations that delegate, because
// there is no accessor and there never may be one.
function custody(): AccountKeyCustodyService {
  return TestBed.inject(AccountKeyCustodyService);
}

// Waits for an adoption to end, whichever way it ended.
//
// `adoptRotated` returns nothing — for the reason `unlock` does — so there is no
// promise to await and the only marker is the status leaving `'unlocking'`.
// `vi.waitFor` gives up loudly, which is the right answer for a hand-over that
// never resolved.
async function settled(keys: AccountKeyCustodyService): Promise<void> {
  await vi.waitFor(() => {
    expect(keys.status()).not.toBe('unlocking');
  });
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
  /**
   * The run that last rewrote this cell's row, or `null` for a row no chunk has
   * reached. It travels with the cell so that a case can ask which generation
   * the wire beside it is sealed under without a second walk over the arms.
   */
  readonly rotationId: string | null;
}

// Every stored cell of one narrative pair, read off the fake rather than off
// anything the driver said. **It throws on a pair it does not cover**, which is
// what makes the loops below a census: a ninth entry in `NARRATIVE_FIELDS`
// fails here instead of being skipped.
function cellsFor(field: NarrativeField): StoredCell[] {
  const pair = `${field.table}.${field.column}`;

  switch (pair) {
    case 'accounts.name':
      return server.accounts.map((row) => ({
        rowId: row.id,
        wire: row.name,
        rotationId: row.rotationId,
      }));
    case 'payees.name':
      return server.payees.map((row) => ({
        rowId: row.id,
        wire: row.name,
        rotationId: row.rotationId,
      }));
    case 'category_groups.name':
      return server.categoryGroups.map((row) => ({
        rowId: row.id,
        wire: row.name,
        rotationId: row.rotationId,
      }));
    case 'category_groups.description':
      return server.categoryGroups.flatMap((row) =>
        row.description === null
          ? []
          : [
              {
                rowId: row.id,
                wire: row.description,
                rotationId: row.rotationId,
              },
            ],
      );
    case 'categories.name':
      return server.categories.map((row) => ({
        rowId: row.id,
        wire: row.name,
        rotationId: row.rotationId,
      }));
    case 'categories.description':
      return server.categories.flatMap((row) =>
        row.description === null
          ? []
          : [
              {
                rowId: row.id,
                wire: row.description,
                rotationId: row.rotationId,
              },
            ],
      );
    case 'transactions.description':
      return server.transactions.flatMap((row) =>
        row.description === null
          ? []
          : [
              {
                rowId: row.id,
                wire: row.description,
                rotationId: row.rotationId,
              },
            ],
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
        rotationId: row.rotationId,
      }));
    case 'payees.name':
      return server.payees.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
        rotationId: row.rotationId,
      }));
    case 'categories.name':
      return server.categories.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
        rotationId: row.rotationId,
      }));
    case 'category_groups.name':
      return server.categoryGroups.map((row) => ({
        rowId: row.id,
        wire: row.nameKey,
        rotationId: row.rotationId,
      }));
    default:
      throw new Error(`this fixture covers no blind index for ${pair}`);
  }
}

// Every narrative cell one run has already re-sealed, carrying the field it
// belongs to and the wire it holds **right now**.
//
// **The wire is captured rather than looked up afterwards, and that is the
// whole point of this helper.** Those bytes are the only copy of those rows'
// plaintext under the generation the interrupted run staged; whether they still
// open once a repair has finished is exactly the claim *the records already
// re-encrypted stay that way* makes, and it cannot be judged from a value read
// after the repair has overwritten it.
function cellsStampedBy(
  rotationId: string,
): readonly { readonly field: NarrativeField; readonly cell: StoredCell }[] {
  return NARRATIVE_FIELDS.flatMap((field) =>
    cellsFor(field)
      .filter((cell) => cell.rotationId === rotationId)
      .map((cell) => ({ field, cell })),
  );
}

// One staged seal, or a harness fault: every case that reads one has just
// arranged for the run to have staged it.
function sealFor(
  seals: readonly RotationSealBody[],
  factorId: string,
): RotationSealBody {
  const seal = seals.find((candidate) => candidate.factorId === factorId);

  if (seal === undefined) {
    throw new Error(`the staged run holds no seal for ${factorId}`);
  }

  return seal;
}

// A factor revoked while a run was in flight, and what the account holds after.
//
// **The live manifest moves with the factor set**, exactly as the path that
// revokes a passkey moves it — that path promotes the manifest in the unit of
// work it already had. A case that deleted the entry alone would meet the
// material module's *served set is not the declared set* refusal instead of the
// moved-set state it meant to arrange.
async function revoke(revoked: MintedFactor): Promise<readonly MintedFactor[]> {
  const surviving = fixture.factors.filter((factor) => factor !== revoked);

  server.entries.delete(revoked.factorId);
  server.manifest = await manifestOver(fixture.contentKey, surviving, EPOCH);

  return surviving;
}

function factorIdsOf(
  factors: readonly { readonly factorId: string }[],
): string[] {
  return factors.map((factor) => factor.factorId).sort();
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

function rowsCarriedBy(chunks: readonly ResealChunkRequestBody[]): number {
  return chunks.reduce((total, chunk) => total + rowsIn(chunk), 0);
}

// The run the fake has on file, or a harness fault: every case below that reads
// one has just arranged for one to be there.
function stagedRun(): StagedRun {
  const staged = server.staged;

  if (staged === null) {
    throw new Error('the fake holds no staged run');
  }

  return staged;
}

// How many narrative-bearing rows carry this run's stamp.
function rowsAlreadyDone(): number {
  return NARRATIVE_ROWS - server.outstanding(stagedRun().rotationId);
}

// A fresh copy of the account's own key material, for the one case that mints a
// factor after the fixture was built. Fresh, because every door that takes an
// `AccountKeys` wipes what it was handed.
function accountKeys(): AccountKeys {
  return {
    contentKey: Uint8Array.from(fixture.keyMaterial.contentKey),
    indexKey: Uint8Array.from(fixture.keyMaterial.indexKey),
  };
}

// The browser after a reload: a new injector, a new service instance, and
// nothing of the run in flight but what the fake still holds.
function freshDriver(): KeyRotationService {
  TestBed.resetTestingModule();
  configureTestBed();

  return TestBed.inject(KeyRotationService);
}

// A first run that got part-way and stopped.
//
// It leaves the staging row and one seal per factor on file, `at - 1` chunks of
// rows re-sealed under the staged generation and stamped, and the rest still
// under the generation in force — then forgets every request, so that what a
// resuming browser posts stands on its own.
async function interruptedRun(
  at: number,
  maxChunkBytes: number,
): Promise<KeyRotationService> {
  server.maxChunkBytes = maxChunkBytes;
  server.refuseChunk = { at, error: new HttpErrorResponse({ status: 0 }) };

  const interrupted = driver();

  await interrupted.begin(ceremonyUnder(firstFactor()));

  // A harness check and not an assertion: every case below is *about* what
  // happens next, and one built on a run that finished or never staged would
  // pass while testing nothing.
  if (interrupted.failure() !== 'unreachable' || server.staged === null) {
    throw new Error('the interruption these cases are built on did not happen');
  }

  server.refuseChunk = null;
  server.forgetEveryRequest();

  return interrupted;
}

beforeAll(async () => {
  fixture = await buildFixture();
}, 60000);

// The providers, as a function rather than inline, because a resume needs them
// stood up a **second** time over the same fake: `KeyRotationService` is
// root-provided, so the only way to a driver that holds nothing of the last run
// is a new injector.
function configureTestBed(): void {
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
          updateAccount: (id: string, body: RenameBody): Observable<void> =>
            server.rename('accounts', 'PUT', id, body),
        },
      },
      {
        provide: PayeesApiService,
        useValue: {
          getPayees: (): Observable<PayeeListResponse> => server.payeeList(),
          // `PATCH api/payees/{id}`, pinned against the real service in
          // `payees-api.service.spec.ts`.
          renamePayee: (id: string, body: RenameBody): Observable<void> =>
            server.rename('payees', 'PATCH', id, body),
        },
      },
      {
        provide: CategoryGroupsApiService,
        useValue: {
          getCategoryGroups: (): Observable<CategoryGroupListResponse> =>
            server.categoryGroupList(),
          updateCategoryGroup: (
            id: string,
            body: RenameBody,
          ): Observable<void> =>
            server.rename('categoryGroups', 'PUT', id, body),
        },
      },
      {
        provide: CategoriesApiService,
        useValue: {
          getCategories: (): Observable<CategoryListResponse> =>
            server.categoryList(),
          updateCategory: (id: string, body: RenameBody): Observable<void> =>
            server.rename('categories', 'PUT', id, body),
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
}

beforeEach(() => {
  // jsdom's `localStorage` is per test *file* and not per case, so the epoch a
  // finished run makes custody record is an invisible fixture for every case
  // after it. Cleared here rather than in the describe that reads one, because
  // every run in this file that reaches its 204 writes one.
  localStorage.clear();
  server = new FakeServer(fixture);
  session = new SessionStub();

  configureTestBed();
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

  // `rotation_name_collision` names no row, so it gets no word of its own: a
  // name landed between a collection and a send, and the next collection is
  // what finds the pair. Nothing in the refused chunk was written, so the pass
  // is spent the way a row arriving mid-run spends one.
  it('collects and sends again when a chunk is refused as a name collision, and finishes', async () => {
    // Arrange
    const service = driver();
    let collections = 0;

    server.onList = (): void => {
      collections += 1;
    };
    server.refuseChunk = {
      at: 1,
      error: problemConflict('rotation_name_collision', NAME_COLLISION_DETAIL),
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    // Collected again, not the refused chunk re-sent: the pair is found by the
    // next collection, so a driver that only retried the post would send the
    // same collision forever.
    expect(collections).toBe(2);
    expect(server.chunkBodies).toHaveLength(2);
    expect(server.completionBodies).toHaveLength(1);
    expect(server.rotationEpoch).toBe(EPOCH + 1);
  });

  it('gives up with the word unfinished after three chunks refused as a name collision', async () => {
    // Arrange
    const service = driver();
    let collections = 0;

    server.onList = (): void => {
      collections += 1;
    };
    server.refuseEveryChunk = problemConflict(
      'rotation_name_collision',
      NAME_COLLISION_DETAIL,
    );

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unfinished'));
    expect(collections).toBe(3);
    expect(server.chunkBodies).toHaveLength(3);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('answers a chunk refused with a conflict kind it has never heard of as unrecognised, and sends nothing more', async () => {
    // Arrange
    const service = driver();

    server.refuseEveryChunk = problemConflict(
      'rotation_something_new',
      'A refusal this client was built before.',
    );

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unrecognised'));
    expect(server.chunkBodies).toHaveLength(1);
    expect(server.completionBodies).toHaveLength(0);
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

// The leg that picks up a run a browser lost.
//
// **Every case here throws the driver away.** A resume's whole claim is that it
// walks back from *server* state — a staging row, a staged epoch, one seal per
// factor and a set of stamps — with nothing in the browser: custody is locked,
// the service is a fresh instance and the generation the interrupted run staged
// is gone from this tab. A case that resumed on the same instance would be
// asserting against two `#` fields it cannot see and that the first run left
// behind.
describe('a run picked up after a reload', () => {
  it('finishes a run this browser never began, and leaves every column under the generation that run staged', async () => {
    // Arrange — a first run that re-sealed part of the account and then stopped.
    const interrupted = await interruptedRun(3, 400);
    const staged = stagedRun();
    const done = rowsAlreadyDone();

    // Neither half may be empty, or this is not the state a resume walks back
    // from: some rows carry the staged generation and the rest carry the one
    // still in force.
    expect(done).toBeGreaterThan(0);
    expect(done).toBeLessThan(NARRATIVE_ROWS);
    expect(server.rotationEpoch).toBe(EPOCH);

    const resumed = freshDriver();

    expect(resumed).not.toBe(interrupted);

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(resumed.failure()).toBeNull();
    expect(resumed.phase()).toBe('finished');
    // Nothing was begun again: the run that finished is the run that was
    // staged, promoted one epoch above where it started.
    expect(server.beginBodies).toHaveLength(0);
    expect(server.rotationEpoch).toBe(EPOCH + 1);
    expect(server.manifest).toBe(staged.manifest);
    // There is nothing left to finish, so the section draws Rotate keys again.
    expect(resumed.staged()).toBeNull();

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
        const binding = { ...field, rowId: cell.rowId };

        await expect(
          openNarrativeField(contentKey, cell.wire, binding),
          pair,
        ).resolves.toBe(textOf(field, cell.rowId));
        // And none of them under the generation this run replaced — including
        // the rows the interrupted browser never reached.
        await expect(
          openNarrativeField(fixture.contentKey, cell.wire, binding),
          pair,
        ).rejects.toThrow();
        opened += 1;
      }
    }

    expect(opened).toBe(12);
  });

  it('publishes the date the staged run began, which is what the section draws beside the control', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);

    const service = freshDriver();

    // Assert — before the read, a browser that just loaded knows of nothing.
    expect(service.staged()).toBeNull();

    // Act
    await service.readStagedRotation();

    // Assert
    expect(service.staged()).toEqual({ startedAtUtc: '2026-02-03T04:05:06Z' });
    // A read and nothing else: the control has not been pressed.
    expect(server.beginBodies).toHaveLength(0);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.accountKeyReads).toBe(0);
  });

  it('has nothing to finish when the server holds no staged run', async () => {
    // Arrange
    const service = driver();

    // Act
    await service.readStagedRotation();
    await service.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.staged()).toBeNull();
    // Nothing became of a run, because there was none: a word here would say
    // one failed.
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('idle');
    expect(server.beginBodies).toHaveLength(0);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    // Not a byte of the account's key material was read either. The press stops
    // at the one read that says there is nothing to pick up.
    expect(server.accountKeyReads).toBe(0);
  });

  // The two cases below are the read's whole failure behaviour, and they are
  // what lets every caller make it without a `.catch` — the initializer in
  // `core.providers.ts` among them, where a rejection would be a blank page on
  // every cold load made while this route is down.
  //
  // Both arrange a run that really is staged, so the `null` each asserts is the
  // read's own answer and not the state of the world: against a server holding
  // nothing, a swallow and a successful read are the same value, and the case
  // above already covers that half.
  it('publishes nothing to finish when the read is refused, and does not reject', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);
    server.refuseState = new HttpErrorResponse({ status: 500 });

    const service = freshDriver();

    // Act — the promise settling is half the assertion.
    await expect(service.readStagedRotation()).resolves.toBeUndefined();

    // Assert
    expect(service.staged()).toBeNull();
    // And no word. The six each say what became of a *run*, and a read made
    // before anybody pressed anything has no run to have become anything.
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('idle');
  });

  it('publishes nothing to finish when the read answers nothing at all', async () => {
    // Arrange — the second shape the same `catch` absorbs: no refusal, no
    // value, just a completion. It costs one flag to drive and it is the one
    // failure a reader assumes is a success.
    await interruptedRun(1, MAX_CHUNK_BYTES);
    server.answerStateWithoutEmitting = true;

    const service = freshDriver();

    // Act
    await expect(service.readStagedRotation()).resolves.toBeUndefined();

    // Assert
    expect(service.staged()).toBeNull();
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('idle');
  });

  it('refuses a staged seal naming a factor this account no longer holds', async () => {
    // Arrange — a run staged for four factors, and one of them revoked since.
    // The live manifest moves with the factor set, exactly as the path that
    // revokes a passkey moves it, or the refusal under test is not the one that
    // fires.
    await interruptedRun(1, MAX_CHUNK_BYTES);
    await revoke(fixture.factors[FACTOR_COUNT - 1]);

    const resumed = freshDriver();

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(resumed.failure()).toBe(word('factors-moved'));
    // Before the run and not at the end of it: a whole account has not been
    // re-sealed for a completion the server was always going to refuse. And a
    // resume never begins: the repair belongs to the other press, which is what
    // the word's own copy sends somebody to.
    expect(server.beginBodies).toHaveLength(0);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    expect(server.rotationEpoch).toBe(EPOCH);
    // The way forward is a fresh begin rather than another attempt to finish,
    // so the section stops offering to finish.
    expect(resumed.staged()).toBeNull();
  });

  it('refuses a live factor the staged run sealed nothing for', async () => {
    // Arrange — a run staged for four factors, and a fifth enrolled since. The
    // resume read leaves that factor out of `seals` rather than filling it in,
    // so the absence is the whole of the signal.
    await interruptedRun(1, MAX_CHUNK_BYTES);

    const enrolled = await mintFactor(0x55, accountKeys());

    server.entries.set(enrolled.factorId, { ...enrolled.entry });
    server.manifest = await manifestOver(
      fixture.contentKey,
      [...fixture.factors, enrolled],
      EPOCH,
    );

    const resumed = freshDriver();

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(resumed.failure()).toBe(word('factors-moved'));
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    expect(server.rotationEpoch).toBe(EPOCH);
    expect(resumed.staged()).toBeNull();
  });

  it('quotes the run the server holds and mints no epoch of its own', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);

    const staged = stagedRun();
    const resumed = freshDriver();

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(resumed.failure()).toBeNull();
    expect(server.beginBodies).toHaveLength(0);
    expect(server.chunkBodies.length).toBeGreaterThan(0);

    for (const chunk of server.chunkBodies) {
      expect(chunk.rotationId).toBe(staged.rotationId);
    }

    expect(server.completionBodies.map((body) => body.rotationId)).toEqual([
      staged.rotationId,
    ]);
    // The staged epoch is the one the interrupted run filed, and it is the one
    // the account ends up at. A resume that minted its own would be two steps.
    expect(stagedRun().rotationEpoch).toBe(EPOCH + 1);
    expect(server.rotationEpoch).toBe(EPOCH + 1);
  });

  it('starts the bar at zero and carries the whole account again', async () => {
    // Arrange
    await interruptedRun(3, 400);

    const done = rowsAlreadyDone();
    const resumed = freshDriver();
    const seen: number[] = [];

    server.onChunk = (): void => {
      seen.push(resumed.progress().resealed);
    };

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(resumed.failure()).toBeNull();
    expect(done).toBeGreaterThan(0);
    // The chapter specifies this rather than tolerating it, and the consequence
    // block on the screen warns about it: the server publishes no per-row
    // progress, the resume read carries none, and a client-side one is refused.
    expect(seen[0]).toBe(0);
    expect(resumed.progress().records).toBe(NARRATIVE_ROWS);
    expect(resumed.progress().resealed).toBe(NARRATIVE_ROWS);
    expect(rowsCarriedBy(server.chunkBodies)).toBe(NARRATIVE_ROWS);
  });
});

// The repair a `factors-moved` refusal points at: press **Rotate keys** over a
// run that is staged for a factor set the account no longer has.
//
// **What makes it a repair rather than a second rotation is the generation.**
// The staged seals are the only copy of the generation every row the
// interrupted run already re-sealed is sealed under, and a begin overwrites
// them in place — so a begin here has to recover that generation out of a
// surviving factor's staged seal and carry it to the **live** set, never mint
// one. That is the whole of *the records already re-encrypted stay that way*.
describe('a run begun again after the factor set moved', () => {
  it('finishes, and the rows the interrupted run re-sealed are not stranded', async () => {
    // Arrange — a run that re-sealed part of the account and stopped, then a
    // factor revoked while it was stopped.
    await interruptedRun(3, 400);

    const staged = stagedRun();
    const done = rowsAlreadyDone();

    // Neither half may be empty, or this is not the state the repair is about:
    // some rows carry the staged generation and the rest carry the one still in
    // force.
    expect(done).toBeGreaterThan(0);
    expect(done).toBeLessThan(NARRATIVE_ROWS);

    const alreadyResealed = cellsStampedBy(staged.rotationId);

    expect(alreadyResealed.length).toBeGreaterThan(0);

    await revoke(fixture.factors[FACTOR_COUNT - 1]);

    const repaired = freshDriver();

    // Act
    await repaired.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(repaired.failure()).toBeNull();
    expect(repaired.phase()).toBe('finished');
    expect(server.rotationEpoch).toBe(EPOCH + 1);
    // One begin, and it re-stages the run that was already on file rather than
    // drawing a second identifier: the rows the interrupted run stamped are
    // rows this run really has done.
    expect(server.beginBodies).toHaveLength(1);
    expect(server.beginBodies[0].rotationId).toBe(staged.rotationId);

    const { contentKey } = await adoptedGeneration(firstFactor());

    // **The assertion this whole case exists for.** The ciphertext those rows
    // held at the moment of the repair — the only copy of their plaintext under
    // the interrupted run's generation — still opens under the generation the
    // account was promoted to. A repair that minted a fresh generation would
    // leave every one of these opening under nothing at all, silently: the
    // staged seals it overwrote were that generation's only copy.
    for (const { field, cell } of alreadyResealed) {
      await expect(
        openNarrativeField(contentKey, cell.wire, {
          ...field,
          rowId: cell.rowId,
        }),
        `${field.table}.${field.column} as the repair found it`,
      ).resolves.toBe(textOf(field, cell.rowId));
    }

    // And the whole account as it stands now, including the rows the
    // interrupted run never reached.
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
        const binding = { ...field, rowId: cell.rowId };

        await expect(
          openNarrativeField(contentKey, cell.wire, binding),
          pair,
        ).resolves.toBe(textOf(field, cell.rowId));
        // And none of them under the generation that was in force before any of
        // this started.
        await expect(
          openNarrativeField(fixture.contentKey, cell.wire, binding),
          pair,
        ).rejects.toThrow();
        opened += 1;
      }
    }

    expect(opened).toBe(12);
  });

  it('posts a seal set that is exactly the live factor set, in both directions', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);

    const surviving = await revoke(fixture.factors[FACTOR_COUNT - 1]);
    const repaired = freshDriver();

    // Act
    await repaired.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(repaired.failure()).toBeNull();
    expect(server.beginBodies).toHaveLength(1);
    // Sorted on both sides, so this is set equality rather than a count: a seal
    // still naming the revoked factor and a live factor with no seal are two
    // different failures, and neither passes.
    expect(factorIdsOf(server.beginBodies[0].seals)).toEqual(
      factorIdsOf(surviving),
    );
  });

  it('stages a manifest that opens under the recovered generation and names the live set', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);

    // The interrupted run's own seal for a factor that survives, captured
    // before the repair overwrites it. Opening it is how this case learns which
    // generation is being carried forward without asking the driver, which has
    // no accessor to ask through and would be the thing under test anyway.
    const carried = sealFor(stagedRun().seals, firstFactor().factorId);
    const surviving = await revoke(fixture.factors[FACTOR_COUNT - 1]);
    const repaired = freshDriver();

    // Act
    await repaired.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(repaired.failure()).toBeNull();
    expect(server.beginBodies).toHaveLength(1);

    const recovered = await openFactorKeypair(
      firstFactor().keyEncryptionKey,
      firstFactor().factorId,
      {
        wrappedPrivateKey: server.factorEntry(firstFactor().factorId)
          .wrappedPrivateKey,
        encapsulatedAccountKeys: carried.encapsulatedAccountKeys,
      },
    );
    const body = server.beginBodies[0];
    const named = await openFactorManifest(
      await importAesGcmKey(recovered.contentKey),
      body.manifest,
      body.rotationEpoch,
    );

    // It opened at all, under that generation and at that epoch, which is what
    // makes the manifest and the seals one run rather than two values of the
    // right shape; and the set it names is the one the account holds now.
    expect(factorIdsOf(named)).toEqual(factorIdsOf(surviving));
    expect(body.rotationEpoch).toBe(EPOCH + 1);
  });
});

// What a finished run leaves the tab holding.
//
// **A run that finished and left the account locked is a broken end state**, and
// `docs/design/components.md` says so where it argues that the two key sections
// do not wait on each other: somebody who arrived at a locked account and
// pressed Rotate ends up holding keys. Every row they own has just been
// rewritten under a generation this tab is the only holder of; reporting a
// finished rotation over an account nothing can read would send them to press
// Unlock and run a second ceremony for keys that are already in the frame.
//
// **The hand-over is `AccountKeyCustodyService.adoptRotated` and this driver
// makes none of the judgements behind it.** The completion answers 204 carrying
// nothing on purpose — a generation from that route would be a number a client
// advanced its record from having judged nothing — so custody re-reads the
// account keys and runs the same four refusals an unlock runs. A second reading
// of the manifest here would be a second, weaker definition of the rule that
// whole chapter defends.
//
// The real custody stands in the injector for these cases, because what is owed
// is a fact about the class that holds the keys and there is no accessor to read
// one through.
describe('the tab a finished run leaves behind', () => {
  it('holds the promoted generation, and says the account is open', async () => {
    // Arrange
    const service = driver();
    const keys = custody();

    expect(keys.status()).toBe('locked');

    // Act
    await service.begin(ceremonyUnder(firstFactor()));
    await settled(keys);

    // Assert
    expect(service.phase()).toBe('finished');
    expect(service.failure()).toBeNull();
    expect(keys.status()).toBe('unlocked');
    expect(keys.unlockFailure()).toBeNull();

    // **Both keys, through the operations that delegate.** A hand-over that
    // happened after the `finally` that drops this run's two fields would have
    // nothing to hand over, and a hand-over that passed the generation being
    // replaced would report an open account that cannot read a single row the
    // run just rewrote.
    const row = server.accounts[0];

    await expect(
      keys.openField({ ...ACCOUNT_NAME_FIELD, rowId: row.id }, row.name),
    ).resolves.toEqual({
      state: 'text',
      value: textOf(ACCOUNT_NAME_FIELD, row.id),
    });
    // The index key too, against the value this run wrote into the column: a
    // lookup keyed under the generation being replaced matches no row, and comes
    // back empty rather than failing.
    await expect(
      keys.blindIndex(
        { ...ACCOUNT_NAME_FIELD, budgetId: BUDGET_ID },
        textOf(ACCOUNT_NAME_FIELD, row.id),
      ),
    ).resolves.toEqual({ state: 'computed', value: row.nameKey });
  });

  it('records the epoch the promotion filed, once and through the gate', async () => {
    // Arrange
    const service = driver();
    const keys = custody();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));
    await settled(keys);

    // Assert
    // The run really promoted, or the record below is a claim about an account
    // that never moved.
    expect(keys.status()).toBe('unlocked');
    expect(server.rotationEpoch).toBe(EPOCH + 1);
    expect(highestRotationEpochSeen(BUDGET_ID)).toBe(EPOCH + 1);
    // **The read behind that record is custody's own.** The driver made one for
    // its material; the second is the one the gate is run over, and without it
    // the record would be rising from the epoch this client chose rather than
    // from the manifest the server now serves.
    expect(server.accountKeyReads).toBe(2);
  });

  it('hands the generation over after a run picked up from the other end too', async () => {
    // Arrange
    // A resume finishes a run this browser never began, and the tab it leaves
    // behind is the same tab. The hand-over lives in the leg both presses share,
    // so a copy written on the begin alone is what this case refuses.
    await interruptedRun(3, 400);

    const resumed = freshDriver();
    const keys = custody();

    // Act
    await resumed.resume(ceremonyUnder(firstFactor()));
    await settled(keys);

    // Assert
    expect(resumed.phase()).toBe('finished');
    expect(keys.status()).toBe('unlocked');

    const row = server.payees[0];

    await expect(
      keys.openField({ ...PAYEE_NAME_FIELD, rowId: row.id }, row.name),
    ).resolves.toEqual({
      state: 'text',
      value: textOf(PAYEE_NAME_FIELD, row.id),
    });
  });

  it('hands nothing over when the run did not finish', async () => {
    // Arrange
    // **The other half of every case above.** A hand-over written outside the
    // branch that answers `'finished'` would unlock this tab from a run that
    // stopped — under a generation the account is not in, so every row on every
    // screen would come back unreadable while the service reported an open
    // account.
    server.refuseChunk = { at: 1, error: new HttpErrorResponse({ status: 0 }) };

    const service = driver();
    const keys = custody();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unreachable'));
    expect(keys.status()).toBe('locked');
    expect(keys.unlockFailure()).toBeNull();
    // One read, the driver's own: custody was never asked to judge anything, so
    // nothing was observed and nothing was recorded.
    expect(server.accountKeyReads).toBe(1);
    expect(localStorage.length).toBe(0);
  });

  it('locks and says so when what the server serves back does not agree', async () => {
    // Arrange
    // **A completion the server refuses as already completed is a run that is
    // done**, and this driver reports it as one — so the hand-over happens, and
    // custody meets an account whose served manifest is still the one from
    // before. Here that is arranged rather than real; on the wire it is the
    // shape of a promotion this client believes happened and the account did not
    // make.
    //
    // The word is custody's and the run's is still `null`, which is the honest
    // division: the rotation really did reach its completion, and what does not
    // agree is the account's own material. `inconsistent` says out loud that no
    // factor clears it, which is true — every factor of an account encapsulates
    // the same two keys.
    server.refuseCompletion = conflict('rotation_already_completed');

    const service = driver();
    const keys = custody();

    // Act
    await service.begin(ceremonyUnder(firstFactor()));
    await settled(keys);

    // Assert
    expect(service.phase()).toBe('finished');
    expect(service.failure()).toBeNull();
    expect(keys.status()).toBe('locked');
    expect(keys.unlockFailure()).toBe('inconsistent');
    // And the device remembered nothing from a body it refused.
    expect(localStorage.length).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// Two records in one list with one name.
// ---------------------------------------------------------------------------

// What the driver publishes about a `same-name` stop, and the entry that
// carries a typed name — `docs/design/components.md`, "Renaming one of two
// records with one name".

// The word, spelled once and run through the union.
const SAME_NAME = word('same-name');

// What a person types into the block. Unique in every list the fixture holds.
const TYPED_NAME = 'Corner shop';

// Rows in the four indexed lists: two of each.
const NAMED_ROWS = 8;

const NAME_FIELDS: Readonly<Record<NameArm, BlindIndexedField>> = {
  accounts: ACCOUNT_NAME_FIELD,
  payees: PAYEE_NAME_FIELD,
  categoryGroups: GROUP_NAME_FIELD,
  categories: CATEGORY_NAME_FIELD,
};

const NOTE_FIELDS = {
  categoryGroups: GROUP_NOTE_FIELD,
  categories: CATEGORY_NOTE_FIELD,
} as const;

// An interrupted run that got as far as every name and no further.
//
// One row per chunk and the ninth refused, so every row of the four indexed
// lists is re-sealed under the staged generation and stamped, and every
// transaction note is still under the generation in force. That is the one
// state in which a stale tab can make a pair: a name it writes is sealed and
// keyed under the outgoing keys, beside a row the run already moved.
async function everyNameResealed(): Promise<void> {
  await interruptedRun(NAMED_ROWS + 1, 1);

  const rotationId = stagedRun().rotationId;
  const unstamped = (
    ['accounts', 'payees', 'categoryGroups', 'categories'] as const
  )
    .flatMap((arm) => server.namedRows(arm))
    .filter((row) => row.rotationId !== rotationId);

  // A harness check: a case built on a list the run never reached makes no
  // pair at all and would pass while testing nothing.
  if (unstamped.length !== 0) {
    throw new Error('the interruption did not reach every named row');
  }
}

// A name written by a tab that loaded before the run began: sealed and keyed
// under the generation still in force, and the stamp disowned, as the server's
// ordinary update disowns it. A note the row holds is re-sealed under the same
// keys, because that tab's form sends the whole row.
async function writtenByAStaleTab(
  arm: NameArm,
  row: StoredNamedRow,
  name: string,
): Promise<void> {
  const field = NAME_FIELDS[arm];

  row.name = await sealNarrativeField(fixture.contentKey, name, {
    ...field,
    rowId: row.id,
  });
  row.nameKey = await computeBlindIndex(
    fixture.indexKey,
    { ...field, budgetId: BUDGET_ID },
    name,
  );
  row.rotationId = null;

  if (arm === 'categoryGroups' || arm === 'categories') {
    const described = server[arm].find((candidate) => candidate.id === row.id);
    const note = noteOf(arm, row.id);

    if (described !== undefined && note !== null) {
      described.description = await sealNarrativeField(
        fixture.contentKey,
        note,
        { ...NOTE_FIELDS[arm], rowId: row.id },
      );
    }
  }
}

// The note a described row was seeded with, or `null` for one seeded without.
function noteOf(
  arm: 'categoryGroups' | 'categories',
  rowId: string,
): string | null {
  const field = NOTE_FIELDS[arm];

  return fixture.texts.get(cellKey(field.table, field.column, rowId)) ?? null;
}

interface NamePair {
  readonly arm: NameArm;
  readonly renamed: StoredNamedRow;
  readonly kept: StoredNamedRow;
  readonly renamedName: string;
  readonly keptName: string;
}

// A stale tab gives the row at `renamedAt` the name of the row at `keptAt` —
// in capitals, so the two spellings differ and only the incoming index says
// they are one name.
async function aPairIn(
  arm: NameArm,
  renamedAt: number,
  keptAt: number,
): Promise<NamePair> {
  const rows = server.namedRows(arm);
  const renamed = rows[renamedAt];
  const kept = rows[keptAt];
  const keptName = textOf(NAME_FIELDS[arm], kept.id);
  const renamedName = keptName.toUpperCase();

  await writtenByAStaleTab(arm, renamed, renamedName);

  return { arm, renamed, kept, renamedName, keptName };
}

// A payee written by a tab still under the outgoing keys: a row the run has not
// reached, whose stored index no incoming value can ever equal.
async function payeeUnderTheOutgoingKeys(
  name: string,
): Promise<StoredNamedRow> {
  const id = mintNarrativeRowId();

  return {
    id,
    name: await sealNarrativeField(fixture.contentKey, name, {
      ...PAYEE_NAME_FIELD,
      rowId: id,
    }),
    nameKey: await computeBlindIndex(
      fixture.indexKey,
      { ...PAYEE_NAME_FIELD, budgetId: BUDGET_ID },
      name,
    ),
    rotationId: null,
  };
}

function expectedCollision(pair: NamePair): KeyRotationNameCollision {
  return {
    arm: pair.arm,
    renamed: { id: pair.renamed.id, name: pair.renamedName },
    kept: { id: pair.kept.id, name: pair.keptName },
  };
}

// A reloaded browser whose resume stopped on the pair, which is where every
// rename begins.
async function stoppedOnSameName(
  arm: NameArm,
  renamedAt: number,
  keptAt: number,
): Promise<{ readonly service: KeyRotationService; readonly pair: NamePair }> {
  await everyNameResealed();

  const pair = await aPairIn(arm, renamedAt, keptAt);

  const service = freshDriver();

  await service.resume(ceremonyUnder(firstFactor()));

  // The precondition every rename case stands on, asserted rather than
  // assumed: without the stop there is no block, and nothing to rename from.
  expect(service.failure(), 'the press before the rename').toBe(SAME_NAME);

  server.forgetEveryRequest();

  return { service, pair };
}

// The generation the staged run is re-sealing onto, recovered the way a factor
// recovers it: its live wrapped private key against its staged seal. Never
// asked of the driver, which has no accessor and is the thing under test.
async function stagedGeneration(): Promise<{
  readonly contentKey: CryptoKey;
  readonly indexKey: CryptoKey;
}> {
  const factor = firstFactor();
  const opened = await openFactorKeypair(
    factor.keyEncryptionKey,
    factor.factorId,
    {
      wrappedPrivateKey: server.factorEntry(factor.factorId).wrappedPrivateKey,
      encapsulatedAccountKeys: sealFor(stagedRun().seals, factor.factorId)
        .encapsulatedAccountKeys,
    },
  );

  return {
    contentKey: await importAesGcmKey(opened.contentKey),
    indexKey: await importHmacSha256Key(opened.indexKey),
  };
}

function stringAt(call: RenameCall, key: string): string {
  const value = call.body[key];

  if (typeof value !== 'string') {
    throw new Error(`the rename carried no string ${key}`);
  }

  return value;
}

describe('two records in one list with one name', () => {
  it('stops on same-name before its pass sends a chunk, and publishes both names as stored', async () => {
    // Arrange
    await everyNameResealed();

    const pair = await aPairIn('payees', 1, 0);
    const service = freshDriver();

    // Act
    await service.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(SAME_NAME);
    // Found by the run, not by the server: nothing was posted to be refused.
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    // Exactly these members: two identifiers and two names, and nothing that
    // is an index, a generation or a key.
    expect(service.collision()).toEqual(expectedCollision(pair));
    expect(service.phase()).toBe('idle');
    // The run is still staged, so the section still has a run to finish.
    expect(service.staged()).not.toBeNull();
  });

  interface RenameCase {
    readonly label: string;
    readonly arm: NameArm;
    readonly renamedAt: number;
    readonly keptAt: number;
    readonly method: 'PATCH' | 'PUT';
    readonly keys: readonly string[];
  }

  const RENAME_CASES: readonly RenameCase[] = [
    {
      label: 'an account',
      arm: 'accounts',
      renamedAt: 1,
      keptAt: 0,
      method: 'PUT',
      keys: ['name', 'nameKey', 'openingBalance', 'type'],
    },
    {
      label: 'a payee',
      arm: 'payees',
      renamedAt: 1,
      keptAt: 0,
      method: 'PATCH',
      keys: ['name', 'nameKey'],
    },
    {
      label: 'a category group holding a note',
      arm: 'categoryGroups',
      renamedAt: 0,
      keptAt: 1,
      method: 'PUT',
      keys: ['description', 'name', 'nameKey'],
    },
    {
      label: 'a category group holding none',
      arm: 'categoryGroups',
      renamedAt: 1,
      keptAt: 0,
      method: 'PUT',
      keys: ['description', 'name', 'nameKey'],
    },
    {
      label: 'a category holding a note',
      arm: 'categories',
      renamedAt: 0,
      keptAt: 1,
      method: 'PUT',
      keys: ['description', 'name', 'nameKey'],
    },
    {
      label: 'a category holding none',
      arm: 'categories',
      renamedAt: 1,
      keptAt: 0,
      method: 'PUT',
      keys: ['description', 'name', 'nameKey'],
    },
  ];

  it.each(RENAME_CASES)(
    'renames $label through its own route under the incoming keys, then finishes',
    async (row) => {
      // Arrange
      if (row.arm === 'accounts') {
        // Not the list's defaults, so a PUT that invented them is visible.
        server.accountDetails.set(server.accounts[row.renamedAt].id, {
          type: 'Savings',
          openingBalance: 125000,
        });
      }

      const { service, pair } = await stoppedOnSameName(
        row.arm,
        row.renamedAt,
        row.keptAt,
      );
      const next = await stagedGeneration();
      const field = NAME_FIELDS[row.arm];
      const binding = { ...field, rowId: pair.renamed.id };

      // Act
      await service.resume(ceremonyUnder(firstFactor()), {
        name: TYPED_NAME,
      });

      // Assert
      expect(server.renames).toHaveLength(1);

      const call = server.renames[0];

      expect(call.arm).toBe(row.arm);
      expect(call.method).toBe(row.method);
      // The row given its name second, and never the one that kept it.
      expect(call.id).toBe(pair.renamed.id);
      expect(Object.keys(call.body).sort()).toEqual(row.keys);

      // Sealed under the incoming content key, so the server's unique index
      // compares it against every row already re-encrypted — and not under the
      // outgoing one, which the completion is about to destroy.
      await expect(
        openNarrativeField(next.contentKey, stringAt(call, 'name'), binding),
      ).resolves.toBe(TYPED_NAME);
      await expect(
        openNarrativeField(fixture.contentKey, stringAt(call, 'name'), binding),
      ).rejects.toThrow();
      expect(stringAt(call, 'nameKey')).toBe(
        await computeBlindIndex(
          next.indexKey,
          { ...field, budgetId: BUDGET_ID },
          TYPED_NAME,
        ),
      );

      if (row.arm === 'accounts') {
        // A full PUT: what the row already holds, carried as collected.
        expect(call.body['type']).toBe('Savings');
        expect(call.body['openingBalance']).toBe(125000);
      }

      if (row.arm === 'categoryGroups' || row.arm === 'categories') {
        const note = noteOf(row.arm, pair.renamed.id);
        const noteBinding = { ...NOTE_FIELDS[row.arm], rowId: pair.renamed.id };

        if (note === null) {
          // Null stays null: the verb clears a note it is sent nothing for, and
          // this row never had one to clear.
          expect(call.body['description']).toBeNull();
        } else {
          // And the note travels re-sealed rather than dropped — on this verb
          // an absent note is a 204 having cleared it.
          await expect(
            openNarrativeField(
              next.contentKey,
              stringAt(call, 'description'),
              noteBinding,
            ),
          ).resolves.toBe(note);
          await expect(
            openNarrativeField(
              fixture.contentKey,
              stringAt(call, 'description'),
              noteBinding,
            ),
          ).rejects.toThrow();
        }
      }

      expect(service.failure()).toBeNull();
      expect(service.phase()).toBe('finished');
      expect(server.completionBodies).toHaveLength(1);
      expect(service.collision()).toBeNull();

      // The re-collection saw the new name and the run carried it: the row ends
      // under the promoted generation holding what was typed.
      const { contentKey } = await adoptedGeneration(firstFactor());
      const stored = server
        .namedRows(row.arm)
        .find((candidate) => candidate.id === pair.renamed.id);

      await expect(
        openNarrativeField(contentKey, stored?.name ?? '', binding),
      ).resolves.toBe(TYPED_NAME);
    },
  );

  it('republishes a different pair found between the presses, and renames nothing', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    // Between the presses the payee pair went away and a category pair arrived.
    await writtenByAStaleTab('payees', server.payees[1], 'Florist');

    const moved = await aPairIn('categories', 1, 0);

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    expect(service.failure()).toBe(SAME_NAME);
    expect(service.collision()).toEqual(expectedCollision(moved));
    // A name typed for one pair answers a question nobody is asking now.
    expect(server.renames).toHaveLength(0);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('renames nothing and carries on when the pair went away between the presses', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    await writtenByAStaleTab('payees', server.payees[1], 'Florist');

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    expect(server.renames).toHaveLength(0);
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('finished');
    expect(service.collision()).toBeNull();
  });

  it.each([
    { label: 'a record the run has not reached yet', typed: 'FLORIST' },
    { label: 'the record that keeps the name', typed: 'bakery' },
  ])(
    'refuses a typed name $label already has, beneath the field and before anything is written',
    async ({ typed }) => {
      // Arrange
      const { service, pair } = await stoppedOnSameName('payees', 1, 0);

      // Still under the outgoing keys, so its stored index can never equal an
      // incoming one: only the run's own comparison can see it.
      server.payees.push(await payeeUnderTheOutgoingKeys('Florist'));

      // Act
      await service.resume(ceremonyUnder(firstFactor()), {
        name: typed,
      });

      // Assert
      expect(service.renameRefusal()).toEqual({ reason: 'taken' });
      expect(server.renames).toHaveLength(0);
      expect(server.chunkBodies).toHaveLength(0);
      expect(server.completionBodies).toHaveLength(0);
      // The block stands over the same pair, so the field keeps its value.
      expect(service.failure()).toBe(SAME_NAME);
      expect(service.collision()).toEqual(expectedCollision(pair));
    },
  );

  it('carries the server refusal keyed on Name verbatim, and does not finish', async () => {
    // Arrange
    const { service, pair } = await stoppedOnSameName('payees', 1, 0);
    const messages = [
      'The name is longer than a payee name may be.',
      'Choose a shorter one.',
    ];

    server.refuseRename = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        // The member as ASP.NET spells it on the wire.
        // eslint-disable-next-line @typescript-eslint/naming-convention
        errors: { Name: messages },
      },
    });

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    expect(service.renameRefusal()).toEqual({
      reason: 'invalid',
      messages,
    });
    expect(server.renames).toHaveLength(1);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    expect(service.phase()).not.toBe('finished');
    expect(service.failure()).toBe(SAME_NAME);
    expect(service.collision()).toEqual(expectedCollision(pair));
  });

  it('does not spend a pass on the collection after a rename', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);
    let completions = 0;

    // The first two completions answer that the run is incomplete, so this
    // press needs all three passes — and has them only if the collection that
    // follows the rename was not counted as one.
    server.beforeCompletion = (): void => {
      completions += 1;
      server.refuseCompletion =
        completions <= 2 ? conflict('rotation_incomplete') : null;
    };

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    expect(server.renames).toHaveLength(1);
    expect(completions).toBe(3);
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('finished');
  });

  it('clears the pair when a later press ends on another word', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    server.refuseState = new HttpErrorResponse({ status: 0 });

    // Act
    await service.resume(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unreachable'));
    expect(service.collision()).toBeNull();
  });

  it('hides the pair once the session has ended', async () => {
    // Arrange
    const { service, pair } = await stoppedOnSameName('payees', 1, 0);

    expect(service.collision()).toEqual(expectedCollision(pair));

    // Act
    session.setStatus('anonymous');

    // Assert
    expect(service.collision()).toBeNull();
  });

  it('hides the refusal beneath the field once the session has ended', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });
    expect(service.renameRefusal(), 'the press before').toEqual({
      reason: 'taken',
    });

    // Act
    session.setStatus('anonymous');

    // Assert
    expect(service.renameRefusal()).toBeNull();
  });

  // Only `anonymous` is a session that ended. `unknown` is a probe that has not
  // answered and `unreachable` one that could not, and hiding the block there
  // would take somebody's typed name away over a blinked request.
  it.each([
    'unknown',
    'unreachable',
  ] as const satisfies readonly SessionStatus[])(
    'keeps the pair and the refusal drawn while the session reads %s',
    async (status) => {
      // Arrange
      const { service, pair } = await stoppedOnSameName('payees', 1, 0);

      await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });

      // Act
      session.setStatus(status);

      // Assert
      expect(service.collision()).toEqual(expectedCollision(pair));
      expect(service.renameRefusal()).toEqual({ reason: 'taken' });
    },
  );

  it('stops on same-name when the pair first appears on a later pass, before that pass sends a chunk', async () => {
    // Arrange
    const service = driver();
    const kept = server.payees[0];
    const renamed = server.payees[1];
    const keptName = textOf(PAYEE_NAME_FIELD, kept.id);
    const renamedName = keptName.toUpperCase();
    // What a stale tab writes, prepared ahead because the hook that lands it
    // runs synchronously: sealed and keyed under the outgoing generation.
    const staleName = await sealNarrativeField(
      fixture.contentKey,
      renamedName,
      { ...PAYEE_NAME_FIELD, rowId: renamed.id },
    );
    const staleKey = await computeBlindIndex(
      fixture.indexKey,
      { ...PAYEE_NAME_FIELD, budgetId: BUDGET_ID },
      renamedName,
    );
    let completions = 0;

    // Pass 1 collects no pair and sends its one chunk. The name lands after
    // that send and before its completion, so the completion answers that the
    // run is incomplete and pass 2's collection is the first to see the pair.
    server.beforeCompletion = (): void => {
      completions += 1;

      if (completions === 1) {
        renamed.name = staleName;
        renamed.nameKey = staleKey;
        renamed.rotationId = null;
      }
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(SAME_NAME);
    // Pass 1's chunk and nothing after it: pass 2 stopped before it sent.
    expect(server.chunkBodies).toHaveLength(1);
    expect(server.completionBodies).toHaveLength(1);
    expect(service.collision()).toEqual(
      expectedCollision({
        arm: 'payees',
        renamed,
        kept,
        renamedName,
        keptName,
      }),
    );
  });

  it('clears the refusal beneath the field as the next press starts, and leaves it clear when that press ends another way', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });
    expect(service.renameRefusal(), 'the press before').toEqual({
      reason: 'taken',
    });

    server.refuseState = new HttpErrorResponse({ status: 0 });

    // Act
    const pressing = service.resume(ceremonyUnder(firstFactor()));
    const whilePressing = service.renameRefusal();

    await pressing;

    // Assert
    // One press carries one name: the sentence about the last one does not
    // stand over this one while it runs, nor after it ends on another word.
    expect(whilePressing).toBeNull();
    expect(service.failure()).toBe(word('unreachable'));
    expect(service.renameRefusal()).toBeNull();
  });

  it.each([
    {
      label: 'a different record given the kept name',
      arrange: async (): Promise<NamePair> => {
        const kept = server.payees[0];
        const keptName = textOf(PAYEE_NAME_FIELD, kept.id);

        await writtenByAStaleTab('payees', server.payees[1], 'Florist');

        const renamed = await payeeUnderTheOutgoingKeys(keptName.toLowerCase());

        server.payees.push(renamed);

        return {
          arm: 'payees',
          renamed,
          kept,
          renamedName: keptName.toLowerCase(),
          keptName,
        };
      },
    },
    {
      // Every name spelled exactly as the lead line spelled it, so only the
      // renamed record's identifier says this is not the pair it named.
      label: 'a different record given the renamed name in the same spelling',
      arrange: async (): Promise<NamePair> => {
        const kept = server.payees[0];
        const keptName = textOf(PAYEE_NAME_FIELD, kept.id);
        const renamedName = keptName.toUpperCase();

        await writtenByAStaleTab('payees', server.payees[1], 'Florist');

        const renamed = await payeeUnderTheOutgoingKeys(renamedName);

        server.payees.push(renamed);

        return { arm: 'payees', renamed, kept, renamedName, keptName };
      },
    },
    {
      // The mirror: the renamed record and both spellings are the ones the
      // lead line named, and only the kept record's identifier moved.
      label: 'the same renamed record against a different kept one',
      arrange: async (): Promise<NamePair> => aPairWithAnotherKeptRecord(),
    },
  ])(
    'republishes $label in the same list, and renames nothing',
    async ({ arrange }) => {
      // Arrange
      const { service } = await stoppedOnSameName('payees', 1, 0);
      const moved = await arrange();

      // Act
      await service.resume(ceremonyUnder(firstFactor()), {
        name: TYPED_NAME,
      });

      // Assert
      // Same list, but not the pair the lead line named, so the name typed
      // under it answers a question nobody is asking now.
      expect(service.failure()).toBe(SAME_NAME);
      expect(service.collision()).toEqual(expectedCollision(moved));
      expect(server.renames).toHaveLength(0);
      expect(server.chunkBodies).toHaveLength(0);
      expect(server.completionBodies).toHaveLength(0);
    },
  );

  it('renames the same two records found under another spelling, and finishes', async () => {
    // Arrange
    const { service, pair } = await stoppedOnSameName('payees', 1, 0);

    await aPairInAnotherSpelling();

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    // The same two rows, judged by their identifiers and never by their
    // spelling: the name typed for them still answers the question asked.
    expect(server.renames).toHaveLength(1);
    expect(server.renames[0].id).toBe(pair.renamed.id);
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('finished');
    expect(service.collision()).toBeNull();
  });

  it('answers a rename refused on a member other than Name as unrecognised, and clears the pair', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    // A validation refusal of the index this client computed. There is no
    // sentence in it for the person — the name they typed is not what was
    // refused — so it is this bundle's defect, and a reload is the remedy.
    server.refuseRename = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        // The member as ASP.NET spells it on the wire.
        // eslint-disable-next-line @typescript-eslint/naming-convention
        errors: { NameKey: ['The name key is not a blind index.'] },
      },
    });

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    expect(service.failure()).toBe(word('unrecognised'));
    expect(service.renameRefusal()).toBeNull();
    // The press ended on a word other than same-name, so the block goes.
    expect(service.collision()).toBeNull();
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('answers a rename that reached no server as unreachable, and clears the pair', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    server.refuseRename = new HttpErrorResponse({ status: 0 });

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: TYPED_NAME,
    });

    // Assert
    // The run's ordinary word, never a refusal beneath the field: nothing
    // about the name was judged.
    expect(service.failure()).toBe(word('unreachable'));
    expect(service.renameRefusal()).toBeNull();
    expect(service.collision()).toBeNull();
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
  });

  it('refuses a typed name a category the run has not reached already has, before anything is written', async () => {
    // Arrange
    const { service, pair } = await stoppedOnSameName('categories', 1, 0);

    // Under the outgoing keys, so its stored index can never equal an incoming
    // one: only the run's own comparison over this list can see it.
    server.categories.push(await categoryUnderTheOutgoingKeys('Groceries'));

    // Act
    await service.resume(ceremonyUnder(firstFactor()), {
      name: 'GROCERIES',
    });

    // Assert
    expect(service.renameRefusal()).toEqual({ reason: 'taken' });
    expect(server.renames).toHaveLength(0);
    expect(server.chunkBodies).toHaveLength(0);
    expect(server.completionBodies).toHaveLength(0);
    expect(service.failure()).toBe(SAME_NAME);
    expect(service.collision()).toEqual(expectedCollision(pair));
  });
});

// What one account's press leaves on a root-provided service, read by whoever
// signs in next in the same tab. `SessionService.established()` puts the status
// back to `authenticated` without a reload — registration does, and so may a
// sign-in — so a rule keyed on `anonymous` alone ends at the next sign-in.
describe('a session that ended, and the one established after it', () => {
  const OTHER_BUDGET_ID = '4e7a2d91-6c3b-4f08-9a15-b2d8e0c7f364';

  it('draws neither the pair nor the refusal for a different account', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });
    expect(service.renameRefusal(), 'the press before').toEqual({
      reason: 'taken',
    });

    // Act
    session.end();
    session.establish();
    session.setBudgetId(OTHER_BUDGET_ID);

    // Assert
    expect(service.collision()).toBeNull();
    expect(service.renameRefusal()).toBeNull();
  });

  it('draws neither while the session established after it has not said which budget it is in', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });

    // Act
    session.end();
    session.establish();

    // Assert
    // Whose account this is is not known yet, so it is not known to be the
    // account that produced the pair.
    expect(service.collision()).toBeNull();
    expect(service.renameRefusal()).toBeNull();
  });

  it('draws the pair and the refusal again for the account that produced them', async () => {
    // Arrange
    const { service, pair } = await stoppedOnSameName('payees', 1, 0);

    await service.resume(ceremonyUnder(firstFactor()), { name: 'bakery' });

    // Act
    session.end();
    session.establish();
    session.setBudgetId(BUDGET_ID);

    // Assert
    // Hidden, not dropped: the names are that account's own, and the run on
    // file is still stopped on them.
    expect(service.collision()).toEqual(expectedCollision(pair));
    expect(service.renameRefusal()).toEqual({ reason: 'taken' });
  });

  it('does not tell a different account how the previous account’s run stopped', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    // Act
    session.end();
    session.establish();
    session.setBudgetId(OTHER_BUDGET_ID);

    // Assert
    // Rendered, the word says *Two records in one list have the same name* to
    // somebody whose account holds no such pair and no run.
    expect(service.failure()).toBeNull();
  });

  it('does not offer a different account the previous account’s run to finish', async () => {
    // Arrange
    const { service } = await stoppedOnSameName('payees', 1, 0);

    expect(service.staged(), 'the press before').not.toBeNull();

    // Act
    session.end();
    session.establish();
    session.setBudgetId(OTHER_BUDGET_ID);

    // Assert
    // Before the section's own read lands, a staged run here draws Finish
    // rotating with the date the previous account's run began.
    expect(service.staged()).toBeNull();
  });

  it('does not hand a different account the size of the previous account’s finished run', async () => {
    // Arrange
    const service = driver();

    await service.begin(ceremonyUnder(firstFactor()));
    expect(service.progress(), 'the press before').toEqual({
      resealed: NARRATIVE_ROWS,
      records: NARRATIVE_ROWS,
    });

    // Act
    session.end();
    session.establish();
    session.setBudgetId(OTHER_BUDGET_ID);

    // Assert
    // Not drawn at rest, and still a count of another account's records on an
    // object every injector in the app can reach.
    expect(service.phase()).toBe('idle');
    expect(service.progress()).toEqual({ resealed: 0, records: 0 });
  });

  // The cases above change the session after a press has ended. These change it
  // while one is still in flight, so every value the press publishes afterwards
  // lands under the other account — and is still the first account's, because
  // a run is keyed inside the budget it began in.
  const switchToAnotherAccount = (): void => {
    session.end();
    session.establish();
    session.setBudgetId(OTHER_BUDGET_ID);
  };

  it('shows a different account nothing of a run that finished after the switch', async () => {
    // Arrange
    const service = driver();
    const seenByTheOtherAccount: {
      readonly phase: KeyRotationPhase;
      readonly progress: KeyRotationProgress;
      readonly running: boolean;
    }[] = [];
    let switched = false;

    // The first chunk is held at the server when the session changes, so the
    // chunk's 204, the finishing phase and the 204 of the completion all land
    // under the other account.
    server.onChunk = (): void => {
      if (!switched) {
        switched = true;
        switchToAnotherAccount();
      }
    };
    server.beforeCompletion = (): void => {
      seenByTheOtherAccount.push({
        phase: service.phase(),
        progress: service.progress(),
        running: service.running(),
      });
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(seenByTheOtherAccount).toEqual([
      {
        phase: 'idle',
        progress: { resealed: 0, records: 0 },
        // Not read through the budget rule: it guards this object.
        running: true,
      },
    ]);
    expect(service.phase()).toBe('idle');
    expect(service.progress()).toEqual({ resealed: 0, records: 0 });
    expect(service.failure()).toBeNull();

    // And the run is the first account's: back there, it finished.
    session.end();
    session.establish();
    session.setBudgetId(BUDGET_ID);
    expect(service.phase()).toBe('finished');
    expect(service.progress()).toEqual({
      resealed: NARRATIVE_ROWS,
      records: NARRATIVE_ROWS,
    });
  });

  it('does not tell a different account the word a run stopped on after the switch', async () => {
    // Arrange
    const service = driver();

    server.onChunk = switchToAnotherAccount;
    server.refuseChunk = { at: 1, error: new HttpErrorResponse({ status: 0 }) };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(service.phase()).toBe('idle');

    session.end();
    session.establish();
    session.setBudgetId(BUDGET_ID);
    expect(service.failure()).toBe(word('unreachable'));
  });

  it('does not draw a different account the pair a resume found after the switch', async () => {
    // Arrange
    await everyNameResealed();

    const pair = await aPairIn('payees', 1, 0);
    const service = freshDriver();

    // Act
    // Switched before the staged-rotation read has answered: everything the
    // press publishes after its first set lands under the other account.
    const pressing = service.resume(ceremonyUnder(firstFactor()));

    switchToAnotherAccount();
    await pressing;

    // Assert
    expect(service.collision()).toBeNull();
    expect(service.failure()).toBeNull();
    expect(service.staged()).toBeNull();

    session.end();
    session.establish();
    session.setBudgetId(BUDGET_ID);
    expect(service.failure()).toBe(SAME_NAME);
    expect(service.collision()).toEqual(expectedCollision(pair));
    expect(service.staged()).not.toBeNull();
  });

  it('does not offer a different account a staged run whose read answered after the switch', async () => {
    // Arrange
    await interruptedRun(1, MAX_CHUNK_BYTES);

    const service = freshDriver();

    // Act
    const reading = service.readStagedRotation();

    switchToAnotherAccount();
    await reading;

    // Assert
    expect(service.staged()).toBeNull();

    session.end();
    session.establish();
    session.setBudgetId(BUDGET_ID);
    expect(service.staged()).toEqual({ startedAtUtc: '2026-02-03T04:05:06Z' });
  });
});

// A category written by a tab still under the outgoing keys, holding no note:
// a row the run has not reached, whose stored index no incoming value equals.
async function categoryUnderTheOutgoingKeys(
  name: string,
): Promise<StoredDescribedRow> {
  const id = mintNarrativeRowId();

  return {
    id,
    name: await sealNarrativeField(fixture.contentKey, name, {
      ...CATEGORY_NAME_FIELD,
      rowId: id,
    }),
    nameKey: await computeBlindIndex(
      fixture.indexKey,
      { ...CATEGORY_NAME_FIELD, budgetId: BUDGET_ID },
      name,
    ),
    rotationId: null,
    description: null,
  };
}

// The pair `stoppedOnSameName('payees', 1, 0)` drew, with the record that kept
// the name deleted by another tab and a new record holding that name in the
// same spelling under the **incoming** keys — which is how a rename made during
// the run writes it. The renamed record and both spellings are unchanged.
async function aPairWithAnotherKeptRecord(): Promise<NamePair> {
  const renamed = server.payees[1];
  const gone = server.payees[0];
  const keptName = textOf(PAYEE_NAME_FIELD, gone.id);
  const renamedName = keptName.toUpperCase();
  const next = await stagedGeneration();
  const id = mintNarrativeRowId();
  const kept: StoredNamedRow = {
    id,
    name: await sealNarrativeField(next.contentKey, keptName, {
      ...PAYEE_NAME_FIELD,
      rowId: id,
    }),
    nameKey: await computeBlindIndex(
      next.indexKey,
      { ...PAYEE_NAME_FIELD, budgetId: BUDGET_ID },
      keptName,
    ),
    rotationId: null,
  };

  server.payees = [renamed, kept];

  return { arm: 'payees', renamed, kept, renamedName, keptName };
}

// The pair `stoppedOnSameName('payees', 1, 0)` drew, with the renamed record
// re-written by the stale tab in a third spelling: the same two identifiers,
// so the same pair, and a lead line whose spelling has moved.
async function aPairInAnotherSpelling(): Promise<NamePair> {
  const kept = server.payees[0];
  const renamed = server.payees[1];
  const keptName = textOf(PAYEE_NAME_FIELD, kept.id);
  const renamedName = keptName.toLowerCase();

  await writtenByAStaleTab('payees', renamed, renamedName);

  return { arm: 'payees', renamed, kept, renamedName, keptName };
}

// The server's `rotation_name_collision` is a backstop the pre-check leaves in
// place: a name that landed between a collection and a send.
describe('a chunk refused as a name collision', () => {
  it('spends a pass on the kind alone, whatever the problem document says', async () => {
    // Arrange
    const service = driver();
    let collections = 0;

    server.onList = (): void => {
      collections += 1;
    };
    server.refuseChunk = {
      at: 1,
      error: problemConflict(
        'rotation_name_collision',
        'The request conflicts with the current state of the resource.',
      ),
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(collections).toBe(2);
    expect(server.completionBodies).toHaveLength(1);
  });

  it('answers an unknown kind as unrecognised even when its detail speaks of a shared name', async () => {
    // Arrange
    const service = driver();
    let collections = 0;

    server.onList = (): void => {
      collections += 1;
    };
    server.refuseEveryChunk = problemConflict(
      'rotation_label_clash',
      NAME_COLLISION_DETAIL,
    );

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBe(word('unrecognised'));
    expect(collections).toBe(1);
    expect(server.chunkBodies).toHaveLength(1);
  });

  it('posts none of the later chunks of a pass whose first chunk was refused', async () => {
    // Arrange
    const service = driver();
    let collections = 0;
    const passOfEachChunk: number[] = [];

    // One row per chunk, so a pass is ten chunks.
    server.maxChunkBytes = 1;
    server.onList = (): void => {
      collections += 1;
    };
    server.onChunk = (): void => {
      passOfEachChunk.push(collections);
    };
    server.refuseChunk = {
      at: 1,
      error: problemConflict('rotation_name_collision', NAME_COLLISION_DETAIL),
    };

    // Act
    await service.begin(ceremonyUnder(firstFactor()));

    // Assert
    expect(service.failure()).toBeNull();
    expect(passOfEachChunk.filter((pass) => pass === 1)).toHaveLength(1);
    expect(server.chunkBodies).toHaveLength(1 + NARRATIVE_ROWS);
  });
});
