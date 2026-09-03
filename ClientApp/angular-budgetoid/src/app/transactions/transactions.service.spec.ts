// The transactions screen's service, driven through a hand-written custody stub
// and real API services over `HttpTestingController`.
//
// **The API services are real and only custody is stubbed**, the arrangement
// `accounts.service.spec.ts` argues: what this phase is most likely to get wrong
// is what goes *on the wire* — a member the API retired, a note sealed against
// something other than the id beside it, a payee create that should never have
// been made — so the assertions read `TestRequest.request.body`. A stubbed API
// service would let every one of those through. Custody cannot be real here:
// opening anything needs an account's content key, which needs a factor, which
// needs an authenticator this runner does not have.
//
// **The stub's answers encode what they were asked, and its index folds case.**
// `sealField` answers `sealed(<table>.<column>|<rowId>|<text>)` and `openField`
// reads that back, refusing anything whose table, column or row id disagrees —
// which is what makes "this name was opened under the wrong row" a red bar
// rather than a value that happens to look right. `blindIndex` answers
// `index(<table>.<column>|<folded text>)`, folding trim and case the way the
// real normalization does, because the whole point of matching a payee on its
// index is that `trader joe's` and `Trader Joe's` are one counterparty. A stub
// that did not fold would let a text comparison pass every case in this file.
//
// **Every stubbed operation reads a `#` field**, which is the instrument for the
// trap in this wiring: `openField` and `blindIndex` are handed to the mappers as
// capabilities, and handing either over as the bare method reference
// `custody.openField` type-checks perfectly — `#` privates are invisible to the
// type system's `this` — and answers every call with a `TypeError` on the wrong
// receiver. `@typescript-eslint/unbound-method` is off for specs, so nothing but
// a call finds it.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PayeeDto } from '@app-core/api/payees-api.service';
import type { TransactionDto } from '@app-core/api/transactions-api.service';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import type { BlindIndexedField } from '@app-core/security/blind-index';
import {
  NarrativeFieldMisuseError,
  type NarrativeFieldBinding,
} from '@app-core/security/narrative-cipher';
import type {
  BlindIndexValue,
  NarrativeText,
  SealedField,
} from '@app-core/security/narrative-text';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TransactionsService } from './transactions.service';

const API_ORIGIN = 'https://api.test';
const TRANSACTIONS_URL = `${API_ORIGIN}/api/transactions`;
const PAYEES_URL = `${API_ORIGIN}/api/payees`;
const CATEGORY_GROUPS_URL = `${API_ORIGIN}/api/category-groups`;
const CATEGORIES_URL = `${API_ORIGIN}/api/categories`;

// Canonical lower-case hyphenated UUIDs — the spelling `System.Text.Json`
// renders every `Guid` in, so these are what a read really hands back.
const TRANSACTION_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const ACCOUNT_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const PAYEE_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000004';
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000005';

// The canonical spelling *and* the version nibble, because the two are
// different claims. `crypto.randomUUID` satisfies the first and mints version 4
// — the one thing `mintNarrativeRowId` exists not to do.
const MINTED_ROW_ID =
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function sealedWire(
  table: string,
  column: string,
  rowId: string,
  plaintext: string,
): string {
  return `sealed(${table}.${column}|${rowId}|${plaintext})`;
}

// The stub's model of the real normalization: trim, then fold case. Not the
// shipped fold table — this is a spec double — but enough that a match decided
// on the index and a match decided on the typed text answer differently.
function indexValue(table: string, column: string, plaintext: string): string {
  return `index(${table}.${column}|${plaintext.trim().toLowerCase()})`;
}

function sealedPayee(id: string, name: string): PayeeDto {
  return { id, name: sealedWire('payees', 'name', id, name) };
}

function sealedTransaction(
  overrides: Partial<TransactionDto> = {},
): TransactionDto {
  return {
    id: TRANSACTION_ID,
    amount: -20.5,
    date: '2026-07-14',
    description: sealedWire(
      'transactions',
      'description',
      TRANSACTION_ID,
      'Weekly shop',
    ),
    createdAtUtc: '2026-07-14T10:00:00Z',
    accountId: ACCOUNT_ID,
    accountName: sealedWire('accounts', 'name', ACCOUNT_ID, 'Everyday'),
    currencyCode: 'USD',
    currencySymbol: '$',
    payeeId: PAYEE_ID,
    payeeName: sealedWire('payees', 'name', PAYEE_ID, 'Corner Shop'),
    categoryId: CATEGORY_ID,
    categoryName: sealedWire('categories', 'name', CATEGORY_ID, 'Groceries'),
    categoryGroupId: GROUP_ID,
    categoryGroupName: sealedWire(
      'category_groups',
      'name',
      GROUP_ID,
      'Essentials',
    ),
    ...overrides,
  };
}

class CustodyStub
  implements Pick<AccountKeyCustodyService, keyof AccountKeyCustodyService>
{
  readonly #sealCalls: { binding: NarrativeFieldBinding; plaintext: string }[] =
    [];
  readonly #indexCalls: { field: BlindIndexedField; plaintext: string }[] = [];
  readonly #openCalls: { binding: NarrativeFieldBinding; wire: string }[] = [];
  readonly #status = signal<AccountKeyStatus>('unlocked');

  public readonly status: Signal<AccountKeyStatus> = this.#status.asReadonly();
  public readonly unlockFailure: Signal<UnlockFailure | null> =
    signal<UnlockFailure | null>(null).asReadonly();

  /** What `sealField` answers with, when it is not the encoded wire. */
  public sealAnswer: SealedField | null = null;
  /**
   * What `sealField` answers for one table, overriding {@link sealAnswer}.
   *
   * A write here seals two different tables — the transaction's note and,
   * sometimes, a payee's name — and telling them apart is the only way to ask
   * which one is sealed **first**.
   */
  public readonly sealAnswersByTable = new Map<string, SealedField>();
  /** What `blindIndex` answers with, when it is not the encoded value. */
  public indexAnswer: BlindIndexValue | null = null;
  /** How a wire value is read back. Overridable, so a test can defer or lock. */
  public openWith: (
    binding: NarrativeFieldBinding,
    wire: string,
  ) => NarrativeText | Promise<NarrativeText> = (binding, wire) => {
    const match = /^sealed\((.+?)\.(.+?)\|(.+?)\|(.*)\)$/.exec(wire);

    // Table, column *and* row id, all three: a value opened under another
    // row's binding is exactly what fails to authenticate in a browser, and it
    // has to fail here for the same reason.
    return match !== null &&
      match[1] === binding.table &&
      match[2] === binding.column &&
      match[3] === binding.rowId
      ? { state: 'text', value: match[4] ?? '' }
      : { state: 'unreadable' };
  };

  public get sealCalls(): readonly {
    binding: NarrativeFieldBinding;
    plaintext: string;
  }[] {
    return this.#sealCalls;
  }

  public get indexCalls(): readonly {
    field: BlindIndexedField;
    plaintext: string;
  }[] {
    return this.#indexCalls;
  }

  public get openCalls(): readonly {
    binding: NarrativeFieldBinding;
    wire: string;
  }[] {
    return this.#openCalls;
  }

  public setStatus(status: AccountKeyStatus): void {
    this.#status.set(status);
  }

  public sealField(
    binding: NarrativeFieldBinding,
    plaintext: string,
  ): Promise<SealedField> {
    this.#sealCalls.push({ binding, plaintext });

    return Promise.resolve(
      this.sealAnswersByTable.get(binding.table) ??
        this.sealAnswer ?? {
          state: 'sealed',
          wire: sealedWire(
            binding.table,
            binding.column,
            binding.rowId,
            plaintext,
          ),
        },
    );
  }

  public openField(
    binding: NarrativeFieldBinding,
    wire: string,
  ): Promise<NarrativeText> {
    // The `#` read that catches a bare method reference. A detached
    // `custody.openField` throws `TypeError` here rather than answering.
    this.#openCalls.push({ binding, wire });

    return Promise.resolve(this.openWith(binding, wire));
  }

  public blindIndex(
    field: BlindIndexedField,
    plaintext: string,
  ): Promise<BlindIndexValue> {
    this.#indexCalls.push({ field, plaintext });

    return Promise.resolve(
      this.indexAnswer ?? {
        state: 'computed',
        value: indexValue(field.table, field.column, plaintext),
      },
    );
  }

  public unlock(): void {
    throw new Error('the transactions service may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the transactions service may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the transactions service may not lock the account');
  }
}

// Drains the microtask queue the AEAD opens run in. A macrotask boundary is
// what guarantees it: a write here settles several ticks deep — seal, index,
// one or two round trips — and counting ticks is how a test becomes flaky.
function settle(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

describe('TransactionsService', () => {
  let service: TransactionsService;
  let custody: CustodyStub;
  let http: HttpTestingController;

  // What the form hands over: typed text and identifiers, nothing sealed.
  function typed(
    overrides: Partial<Parameters<TransactionsService['add']>[0]> = {},
  ): Parameters<TransactionsService['add']>[0] {
    return {
      accountId: ACCOUNT_ID,
      amount: -20.5,
      categoryId: null,
      date: '2026-07-14',
      description: 'Weekly shop',
      payee: '',
      ...overrides,
    };
  }

  beforeEach(() => {
    custody = new CustodyStub();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: AccountKeyCustodyService, useValue: custody },
        TransactionsService,
      ],
    });
    service = TestBed.inject(TransactionsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  describe('reading the list', () => {
    it('opens each sealed member under the binding of the row it belongs to', async () => {
      // Arrange — the stub answers `unreadable` for any binding whose table,
      // column or row id disagrees with the wire value, so five readable words
      // is the assertion that five bindings were right.
      service.load();

      // Act
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      await settle();

      // Assert
      expect(service.transactions()).toEqual([
        expect.objectContaining({
          accountName: { state: 'text', value: 'Everyday' },
          categoryGroupName: { state: 'text', value: 'Essentials' },
          categoryName: { state: 'text', value: 'Groceries' },
          description: { state: 'text', value: 'Weekly shop' },
          payeeName: { state: 'text', value: 'Corner Shop' },
        }),
      ]);
    });

    it('clears the list to null when a load starts', async () => {
      // Arrange — a list that already holds an answer.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      await settle();
      expect(service.transactions()).not.toBeNull();

      // Act
      service.load();

      // Assert — `null`, never `[]`: an empty array is the sentence *you have
      // no transactions*, which is a claim only a server that answered may
      // make.
      expect(service.transactions()).toBeNull();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('keeps only the newest load’s answer when two overlap', async () => {
      // Arrange — the first load's opens never settle until this test says so.
      // **Every** resolver is collected, and that is not tidiness: a row here
      // makes five opens, so a single `let` would hold only the last one and
      // `Promise.all` would never settle whatever the operator is — the case
      // would then pass under `mergeMap` too, for a reason that has nothing to
      // do with what it claims. Measured: it did.
      const releases: ((value: NarrativeText) => void)[] = [];

      custody.openWith = () =>
        new Promise<NarrativeText>((resolve) => {
          releases.push(resolve);
        });
      service.load();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush({ items: [sealedTransaction({ id: TRANSACTION_ID })] });
      await settle();

      // Act — a second load overtakes it. Decryption widens the overlap from
      // one round trip to one round trip plus five AEAD opens per row, so this
      // is reachable in a browser and not only in a test.
      custody.openWith = (binding, wire) => ({
        state: 'text',
        value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? binding.rowId,
      });
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({
        items: [
          sealedTransaction({
            description: sealedWire(
              'transactions',
              'description',
              TRANSACTION_ID,
              'Fresh',
            ),
          }),
        ],
      });
      await settle();

      for (const release of releases) {
        release({ state: 'text', value: 'Stale' });
      }

      await settle();

      // Assert — the slow first load may not revert the list behind the fast
      // one. `mergeMap` here publishes 'Stale' over an answer somebody is
      // already reading.
      expect(service.transactions()).toEqual([
        expect.objectContaining({
          description: { state: 'text', value: 'Fresh' },
        }),
      ]);
    });

    it('lets a misuse rejection during a load reach the failure branch', async () => {
      // Arrange — `NarrativeFieldMisuseError` is the codec's word for a refusal
      // it made about the *call*, before any cipher ran. It is a defect in this
      // client and says nothing whatever about the rows, so it has to take the
      // whole load down with it.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      custody.openWith = () =>
        Promise.reject(new NarrativeFieldMisuseError('refused'));
      service.load();

      // Act
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      await settle();

      // Assert — `Promise.allSettled` here would file the defect as a per-member
      // result and publish a list of markers over a client bug nobody sees.
      expect(service.transactions()).toBeNull();
      expect(service.loading()).toBe(false);
    });

    it('resets loading and leaves the list null when a load fails', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.load();

      // Act
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      // Assert
      expect(service.loading()).toBe(false);
      expect(service.transactions()).toBeNull();
    });
  });

  describe('reading the payees', () => {
    it('opens each payee name and keys the opened text', async () => {
      // Arrange
      service.loadPayees();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      await settle();

      // Assert — the key is recomputed here because no read hands one back:
      // `payees.name_key` is on no route, deliberately.
      expect(service.payees()).toEqual([
        {
          id: PAYEE_ID,
          name: { state: 'text', value: 'Corner Shop' },
          nameKey: indexValue('payees', 'name', 'Corner Shop'),
        },
      ]);
    });
  });

  describe('writing a transaction', () => {
    it('posts exactly the seven members the route binds', async () => {
      // Arrange

      // Act
      void service.add(typed({ categoryId: CATEGORY_ID }));
      await settle();

      // Assert — `toEqual` is exact over own members, so anything smuggled in
      // reddens. The route refuses what it was not asked for with a 400.
      const request = http.expectOne(TRANSACTIONS_URL);
      const body = request.request.body as { id: string };

      expect(request.request.method).toBe('POST');
      expect(body.id).toMatch(MINTED_ROW_ID);
      expect(request.request.body).toEqual({
        accountId: ACCOUNT_ID,
        amount: -20.5,
        categoryId: CATEGORY_ID,
        date: '2026-07-14',
        description: sealedWire(
          'transactions',
          'description',
          body.id,
          'Weekly shop',
        ),
        id: body.id,
        payeeId: null,
      });
      request.flush(sealedTransaction({ id: body.id }), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('never sends payeeName', async () => {
      // Arrange — the member the API retired. Under `Disallow` a body still
      // carrying it answers 400, so every write this client makes would fail;
      // before `Disallow` it answered 201 with no payee attached, which is the
      // silent version of the same defect.

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http.expectOne(PAYEES_URL).flush(sealedPayee(PAYEE_ID, 'Corner Shop'), {
        status: 201,
        statusText: 'Created',
      });
      await settle();

      // Assert
      const request = http.expectOne(TRANSACTIONS_URL);
      const body = request.request.body as Record<string, unknown>;

      expect(Object.keys(body).sort()).toEqual([
        'accountId',
        'amount',
        'categoryId',
        'date',
        'description',
        'id',
        'payeeId',
      ]);
      expect(body['payeeId']).toBe(PAYEE_ID);
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('seals the note against the identifier it posts', async () => {
      // Arrange — the id is the associated data the note is sealed against. A
      // second `mintNarrativeRowId()` for the body compiles, posts, and leaves
      // a note that never opens again with nothing naming the cause.

      // Act
      void service.add(typed());
      await settle();

      // Assert
      const request = http.expectOne(TRANSACTIONS_URL);
      const body = request.request.body as {
        id: string;
        description: string;
      };

      expect(custody.sealCalls).toEqual([
        {
          binding: {
            table: 'transactions',
            column: 'description',
            rowId: body.id,
          },
          plaintext: 'Weekly shop',
        },
      ]);
      expect(body.description).toBe(
        sealedWire('transactions', 'description', body.id, 'Weekly shop'),
      );
      request.flush(sealedTransaction({ id: body.id }), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('posts a null note for an empty one and seals nothing', async () => {
      // Arrange — `''` is not a legal envelope and answers 400, and this client
      // has no path that produces the 29-byte sealing of an empty string. So
      // "cleared" and "never filled" are one thing from this browser, which is
      // a gap rather than a bug: the column can hold both and nothing here can
      // write the first.

      // Act
      void service.add(typed({ description: '' }));
      await settle();

      // Assert
      const request = http.expectOne(TRANSACTIONS_URL);
      const body = request.request.body as { description: string | null };

      expect(body.description).toBeNull();
      expect(custody.sealCalls).toEqual([]);
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('seals a note of spaces rather than dropping it', async () => {
      // Arrange — the absence test is `=== ''` and never `.trim() === ''`. The
      // form refuses a blank note, so this value cannot arrive from the screen
      // today; the rule is what happens when it does, and a trim here loses a
      // note somebody typed with nothing anywhere saying so. It is the same
      // sentence the client keeps everywhere: it may not alter what it seals.

      // Act
      void service.add(typed({ description: '   ' }));
      await settle();

      // Assert
      const request = http.expectOne(TRANSACTIONS_URL);
      const body = request.request.body as { id: string; description: string };

      expect(body.description).toBe(
        sealedWire('transactions', 'description', body.id, '   '),
      );
      request.flush(sealedTransaction({ id: body.id }), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('posts nothing when sealing the note answers locked', async () => {
      // Arrange — reachable when the account's content key was replaced while
      // the cipher ran.
      custody.sealAnswer = { state: 'locked' };

      // Act
      await service.add(typed());
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      http.expectNone(PAYEES_URL);
    });

    it('publishes a running state across both round trips of a write', async () => {
      // Arrange — this write is **two** requests, and the flag is the only
      // thing between a double press and two transactions. The payee half
      // survives a second press by accident: the create 409s, re-reads and
      // matches the row the first press made. The transaction half does not —
      // it mints a fresh id and posts again, so an impatient click buys a
      // duplicate row wearing a perfectly legitimate client-minted identifier,
      // with nothing on either side able to tell it from a real one.
      //
      // No clock and no `waitFor`: an unflushed `TestRequest` *is* the
      // suspension point, so the flag is read where the write genuinely is.
      expect(service.loading()).toBe(false);

      // Act
      void service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert — first round trip: the payee is being created.
      const create = http.expectOne(PAYEES_URL);
      const created = create.request.body as { id: string };

      expect(service.loading()).toBe(true);
      create.flush(sealedPayee(created.id, 'Bakery'), {
        status: 201,
        statusText: 'Created',
      });
      await settle();

      // Second round trip: the transaction itself.
      const request = http.expectOne(TRANSACTIONS_URL);

      expect(service.loading()).toBe(true);
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();

      // And down again once the re-read the write ends with has landed.
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
      expect(service.loading()).toBe(false);
    });

    it('clears the running state when the write fails', async () => {
      // Arrange — the same defect wearing the other polarity. A flag left set
      // greys the submit button for the life of the screen, so a person whose
      // write failed cannot try again and nothing tells them why. It is
      // cleared on **every** exit, which is why it lives in a `finally` rather
      // than on the success path.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);

      // Act
      void service.add(typed());
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      // Assert — and no re-read, because there is nothing new to read.
      expect(service.loading()).toBe(false);
      http.expectNone(TRANSACTIONS_URL);
    });

    it('re-reads the list rather than guessing where the new row belongs', async () => {
      // Arrange — this screen has no ordering of its own: the rows arrive in
      // the order the server chose and nothing here can reproduce it, so the
      // create's own answer is dropped and the list is read again.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();

      // Act
      void service.add(typed());
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();

      // Assert
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      await settle();
      expect(service.transactions()).toHaveLength(1);
    });
  });

  describe('resolving the payee', () => {
    async function loadPayees(...payees: PayeeDto[]): Promise<void> {
      service.loadPayees();
      http.expectOne(PAYEES_URL).flush({ items: payees });
      await settle();
    }

    it('sends no payee and creates none when nothing was typed', async () => {
      // Arrange
      await loadPayees(sealedPayee(PAYEE_ID, 'Corner Shop'));

      // Act
      void service.add(typed({ payee: '' }));
      await settle();

      // Assert
      const request = http.expectOne(TRANSACTIONS_URL);

      expect(
        (request.request.body as { payeeId: string | null }).payeeId,
      ).toBeNull();
      expect(custody.indexCalls).toEqual([
        // The payee list's own key, and nothing for the empty field.
        {
          field: { table: 'payees', column: 'name' },
          plaintext: 'Corner Shop',
        },
      ]);
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('reuses a payee whose blind index matches, whatever the case typed', async () => {
      // Arrange — the decisive case. `case_insensitive` left the column by
      // force when it became `bytea`, and what replaced it is the fold inside
      // the index. A match decided on decrypted text agrees with this one on
      // almost every name a person types and disagrees here — and the symptom
      // is a second payee for one counterparty, on the column whose whole
      // purpose is that equal names collide.
      await loadPayees(sealedPayee(PAYEE_ID, 'Corner Shop'));

      // Act
      void service.add(typed({ payee: 'corner shop' }));
      await settle();

      // Assert — no create, and the existing row's id on the transaction.
      http.expectNone(PAYEES_URL);

      const request = http.expectOne(TRANSACTIONS_URL);

      expect((request.request.body as { payeeId: string }).payeeId).toBe(
        PAYEE_ID,
      );
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('mints, seals and creates a payee when nothing matches', async () => {
      // Arrange
      await loadPayees(sealedPayee(PAYEE_ID, 'Corner Shop'));

      // Act
      void service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert — three members and no more, each judged by the route on its
      // own: the id in the canonical spelling, the envelope, and the index.
      const create = http.expectOne(PAYEES_URL);
      const body = create.request.body as { id: string };

      expect(create.request.method).toBe('POST');
      expect(body.id).toMatch(MINTED_ROW_ID);
      expect(create.request.body).toEqual({
        id: body.id,
        name: sealedWire('payees', 'name', body.id, 'Bakery'),
        nameKey: indexValue('payees', 'name', 'Bakery'),
      });
      create.flush(sealedPayee(body.id, 'Bakery'), {
        status: 201,
        statusText: 'Created',
      });
      await settle();

      const request = http.expectOne(TRANSACTIONS_URL);

      expect((request.request.body as { payeeId: string }).payeeId).toBe(
        body.id,
      );
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('seals the typed name untrimmed while keying the folded one', async () => {
      // Arrange — the client may not alter what it seals: a trimmed seal beside
      // an untrimmed index keys a row to a value nothing looks up. Folding is
      // the index codec's own job and it does it inside.
      await loadPayees();

      // Act
      void service.add(typed({ payee: '  Bakery  ' }));
      await settle();

      // Assert
      const create = http.expectOne(PAYEES_URL);
      const body = create.request.body as { id: string; name: string };

      expect(body.name).toBe(
        sealedWire('payees', 'name', body.id, '  Bakery  '),
      );
      expect(custody.indexCalls).toEqual([
        { field: { table: 'payees', column: 'name' }, plaintext: '  Bakery  ' },
      ]);
      create.flush(sealedPayee(body.id, '  Bakery  '), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('never matches a payee whose name did not open', async () => {
      // Arrange — a row this browser cannot read is a row it cannot reuse: it
      // has no idea what counterparty it names. The create it falls through to
      // is what the server's unique index judges, on bytes, which is the one
      // party left that can.
      await loadPayees({
        id: PAYEE_ID,
        name: 'sealed(payees.name|somebody-elses-row|Corner Shop)',
      });

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      // Assert
      const create = http.expectOne(PAYEES_URL);

      expect((create.request.body as { id: string }).id).not.toBe(PAYEE_ID);
      create.flush(sealedPayee(PAYEE_ID, 'Corner Shop'), {
        status: 409,
        statusText: 'Conflict',
      });
      await settle();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();
    });

    it('re-reads the list once on a conflict and reuses the match it finds', async () => {
      // Arrange — the list this browser holds was stale: somebody else's tab
      // created the counterparty between the read and the write.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush('conflict', { status: 409, statusText: 'Conflict' });
      await settle();

      // Assert — one re-read, and the row it finds is the one the transaction
      // names. Reading the 409 as success instead would attach the transaction
      // to a payee this client never confirmed.
      const reread = http.expectOne(PAYEES_URL);

      expect(reread.request.method).toBe('GET');
      reread.flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      await settle();

      const request = http.expectOne(TRANSACTIONS_URL);

      expect((request.request.body as { payeeId: string }).payeeId).toBe(
        PAYEE_ID,
      );
      request.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('abandons the write when the re-read still finds no match', async () => {
      // Arrange — the unresolvable case, and the reason abandoning exists. A
      // payee whose name did not open can never match, so a retry posts the
      // same name and 409s again; retrying with a fresh id fixes an
      // astronomically unlikely id collision and loses to the same duplicate
      // name in the case that actually happens.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush('conflict', { status: 409, statusText: 'Conflict' });
      await settle();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      // Assert — no second create, and no transaction. The counterparty was
      // never resolved, and a transaction filed without it loses the one thing
      // the person typed it for.
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
      expect(service.loading()).toBe(false);
    });

    it('seals the note before it creates a payee', async () => {
      // Arrange — the order is the rule and it is about what survives a
      // failure. Creating the payee first and *then* discovering the note
      // cannot be sealed leaves a payee row no transaction names, on a table
      // the app role holds no `DELETE` on: nothing in this product can remove
      // it. Only the note's seal is locked here, because a stub that locked
      // both would abandon on the payee's own seal and the case would pass
      // whichever order the code ran in — measured, it did.
      await loadPayees();
      custody.sealAnswersByTable.set('transactions', { state: 'locked' });

      // Act
      await service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
    });

    it('abandons the write when the payee create fails for any other reason', async () => {
      // Arrange — a 500 is not a stale list, so there is nothing to re-read.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      // Assert
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
    });

    it('creates no payee and posts nothing when the index answers locked', async () => {
      // Arrange — a browser holding no index key cannot decide whether this
      // counterparty is already on the list, and creating one anyway is how a
      // budget grows a second row for a name it already has.
      await loadPayees();
      custody.indexAnswer = { state: 'locked' };

      // Act
      await service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      // Assert
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
    });

    it('adds a created payee to the list it holds', async () => {
      // Arrange — otherwise the next transaction naming the same counterparty
      // posts a create the server refuses, and the write pays a conflict and a
      // re-read for something this browser already knew.
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Bakery' }));
      await settle();

      const create = http.expectOne(PAYEES_URL);
      const created = create.request.body as { id: string };

      create.flush(sealedPayee(created.id, 'Bakery'), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();

      // Assert — the text this browser just sealed, beside the key it just
      // computed, which is what it would read back.
      expect(service.payees()).toEqual([
        {
          id: created.id,
          name: { state: 'text', value: 'Bakery' },
          nameKey: indexValue('payees', 'name', 'Bakery'),
        },
      ]);
    });
  });

  describe('the category picker', () => {
    async function loadCategories(): Promise<void> {
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          { id: GROUP_ID, name: 'Essentials', description: null, position: 0 },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({
        items: [
          {
            id: CATEGORY_ID,
            name: 'Groceries',
            description: null,
            categoryGroupId: GROUP_ID,
            categoryGroupName: 'Essentials',
            position: 0,
          },
        ],
      });
      await settle();
    }

    it('loads category groups and categories for the picker', async () => {
      // Act
      await loadCategories();

      // Assert
      expect(service.categoryGroups()).toHaveLength(1);
      expect(service.categories()).toHaveLength(1);
    });

    it('returns categories belonging to a selected group', async () => {
      // Arrange
      await loadCategories();

      // Act
      const categories = service.categoriesForGroup(GROUP_ID);

      // Assert
      expect(categories.map((category) => category.id)).toEqual([CATEGORY_ID]);
    });
  });
});
