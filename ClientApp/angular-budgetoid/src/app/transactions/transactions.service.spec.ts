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
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
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
import type { BlindIndexBinding } from '@app-core/security/blind-index';
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
import { SessionService } from '@app-core/session/session.service';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TransactionView } from './transaction-view';
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
// The tenancy every index below is keyed inside. `GET /api/me` is where the real
// one comes from; here it is a constant, and it is folded into the value the
// stub answers with so that a service dropping the member from the binding
// computes a different string rather than the same one.
const BUDGET_ID = '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471';

function indexValue(
  table: string,
  column: string,
  budgetId: string,
  plaintext: string,
): string {
  return `index(${table}.${column}|${budgetId}|${plaintext.trim().toLowerCase()})`;
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
  readonly #indexCalls: { binding: BlindIndexBinding; plaintext: string }[] =
    [];
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
  /**
   * Runs at the top of every seal, before the answer is chosen.
   *
   * The instrument for the one window a stub cannot otherwise stage: custody
   * moving **between** two of this class's operations rather than during
   * either. A case hooks it, changes what the next index answers, and the
   * write then holds a name and a key that belong to two different accounts.
   */
  public onSeal: ((binding: NarrativeFieldBinding) => void) | null = null;
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
    binding: BlindIndexBinding;
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
    this.onSeal?.(binding);
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
    binding: BlindIndexBinding,
    plaintext: string,
  ): Promise<BlindIndexValue> {
    this.#indexCalls.push({ binding, plaintext });

    return Promise.resolve(
      this.indexAnswer ?? {
        state: 'computed',
        value: indexValue(
          binding.table,
          binding.column,
          binding.budgetId,
          plaintext,
        ),
      },
    );
  }

  public unlock(): void {
    throw new Error('the transactions service may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the transactions service may not adopt account keys');
  }

  public adoptRotated(): void {
    throw new Error(
      'the transactions service may not take custody of a rotation',
    );
  }

  public lock(): void {
    throw new Error('the transactions service may not lock the account');
  }
}

// The session, replaced by the one member this service reads. Stubbed rather
// than real, because the real class reads `GET /api/me` from the
// `APP_INITIALIZER` and `http.verify()` would then have to account for that
// request on every case here.
//
// **Only `budgetId`, deliberately.** A wider stub would let a service that
// reached for `status()`, `ended()` or `established()` go unnoticed, and this
// service has no business asking the session anything else.
class SessionStub implements Pick<SessionService, 'budgetId'> {
  readonly #budgetId = signal<string | null>(BUDGET_ID);

  public readonly budgetId: Signal<string | null> = this.#budgetId.asReadonly();

  public setBudgetId(budgetId: string | null): void {
    this.#budgetId.set(budgetId);
  }
}

// A validation problem document, built from pairs rather than written as a
// literal. The API's keys are the C# member names, and this project's lint rule
// reaches into object literals and demands camelCase of them — so a literal
// cannot spell what the wire actually sends.
function withErrors(
  ...entries: readonly (readonly [string, readonly string[]])[]
): Record<string, unknown> {
  return { errors: Object.fromEntries(entries) };
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
  let session: SessionStub;
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
    session = new SessionStub();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: AccountKeyCustodyService, useValue: custody },
        { provide: SessionService, useValue: session },
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

    it('drops every opened list when the account locks', async () => {
      // Arrange — four signals here hold plaintext this browser opened under a
      // key it no longer has, and this service is `providedIn: 'root'`: nothing
      // destroys it when a screen goes away and nothing clears it when a
      // session ends. Sign out on `/app/transactions` and the previous
      // account's notes, counterparties and category names are all still
      // readable from the root injector for the life of the tab.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      service.loadPayees();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          {
            id: GROUP_ID,
            name: sealedWire('category_groups', 'name', GROUP_ID, 'Essentials'),
            description: null,
            position: 0,
          },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({
        items: [
          {
            id: CATEGORY_ID,
            name: sealedWire('categories', 'name', CATEGORY_ID, 'Groceries'),
            description: null,
            categoryGroupId: GROUP_ID,
            categoryGroupName: sealedWire(
              'category_groups',
              'name',
              GROUP_ID,
              'Essentials',
            ),
            position: 0,
          },
        ],
      });
      await settle();
      expect(service.transactions()).not.toBeNull();
      expect(service.payees()).not.toBeNull();
      expect(service.categoryGroups()).not.toEqual([]);
      expect(service.categories()).not.toEqual([]);

      // Act — what `SessionService.ended()` does through `custody.lock()`, and
      // what a failed unlock does through `#fail`.
      custody.setStatus('locked');
      TestBed.tick();

      // Assert — all four, each back to the value it holds before a server has
      // answered: `null` for the two that say "no answer yet" and `[]` for the
      // two picker lists, which say "none to offer" and never "the account has
      // none".
      expect(service.transactions()).toBeNull();
      expect(service.payees()).toBeNull();
      expect(service.categoryGroups()).toEqual([]);
      expect(service.categories()).toEqual([]);
    });

    it('withdraws a failed read’s word when the account locks', async () => {
      // Arrange — a read that genuinely failed, so the word is a claim about
      // something that really happened. `accounts.service.ts` argues once why
      // a lock has to withdraw it.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.load();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();
      expect(service.failed()).toBe(true);

      // Act
      custody.setStatus('locked');
      TestBed.tick();

      // Assert — the list is `null` because this service emptied it, not
      // because a request failed, so there is no read left for the word to be
      // a claim about. This service shipped without the clear while its two
      // neighbours had it, and nothing reddened: the screen's own `locked()`
      // masks the word one layer up, which is a coincidence and not a guard.
      expect(service.failed()).toBe(false);
      expect(service.transactions()).toBeNull();
    });

    it('keeps the list while an unlock is running', async () => {
      // Arrange — the control for the case above, and the reason the predicate
      // is `locked` exactly rather than "anything but unlocked":
      // `accounts.service.spec.ts` argues it once for all three services.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [sealedTransaction()] });
      await settle();

      // Act
      custody.setStatus('unlocking');
      TestBed.tick();

      // Assert
      expect(service.transactions()).not.toBeNull();
    });

    it('reads every list again when the account is unlocked', async () => {
      // Arrange — the four lists a lock empties, each holding an answer. The
      // answers are empty on purpose: what this case is about is which reads
      // are asked for again, not what came back in them. After the lock the
      // list is `null` with nothing loading and nothing failed, which is the
      // one combination that renders blank.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      service.loadPayees();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({ items: [] });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();
      custody.setStatus('locked');
      TestBed.tick();
      expect(service.transactions()).toBeNull();

      // Act — the keys come back with the screen still mounted, so no
      // `ngOnInit` runs to ask for any of this a second time.
      custody.setStatus('unlocked');
      TestBed.tick();

      // Assert — **all four**, because all four were emptied and the screen's
      // own `ngOnInit` asks for all four. Restoring the transactions alone
      // leaves the form's counterparty suggestions and its category picker
      // empty on a screen that never remounts, which is the same hole one
      // control further in.
      expect(service.loading()).toBe(true);
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      http.expectOne(PAYEES_URL).flush({ items: [] });
      http.expectOne(CATEGORY_GROUPS_URL).flush({ items: [] });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();
      expect(service.transactions()).not.toBeNull();
      expect(service.payees()).not.toBeNull();
    });

    it('reads every list again when the ceremony itself was seen running', async () => {
      // Arrange — the same close, over the path an effect usually sees.
      // `accounts.service.ts` argues why the near side is every word but
      // `unlocked` rather than `locked` alone.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
      custody.setStatus('locked');
      TestBed.tick();
      custody.setStatus('unlocking');
      TestBed.tick();

      // Act
      custody.setStatus('unlocked');
      TestBed.tick();

      // Assert
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      http.expectOne(PAYEES_URL).flush({ items: [] });
      http.expectOne(CATEGORY_GROUPS_URL).flush({ items: [] });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();
      expect(service.transactions()).not.toBeNull();
    });

    it('asks for nothing when the first status it sees is unlocked', () => {
      // Arrange — the control that makes the reaction a *transition* rather
      // than a value: this service is `providedIn: 'root'` and is built on
      // first injection, which on an open account is `unlocked` from the first
      // run of the effect. Four reads nobody asked for is what a value-keyed
      // arm costs here.

      // Act
      TestBed.tick();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      http.expectNone(PAYEES_URL);
      http.expectNone(CATEGORY_GROUPS_URL);
      http.expectNone(CATEGORIES_URL);
      expect(service.loading()).toBe(false);
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

    it('publishes a failed read as a word, and clears it on the next one', async () => {
      // Arrange — the list is `null` at rest, in flight and after a failure,
      // so no screen can tell those apart from the list and the running flag.
      // Left unpublished, a failed read draws nothing at all.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.load();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();
      expect(service.failed()).toBe(true);

      // Act — and it is cleared where the read starts, not where it lands, so
      // the sentence goes off the moment somebody retries.
      service.load();

      // Assert
      expect(service.failed()).toBe(false);
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
      expect(service.failed()).toBe(false);
    });
  });

  // How many narrative opens one list read performs.
  //
  // **The first three count opens and never read a value**, which is the only
  // way that question can be asked at all: what a screen renders is identical
  // whether a name was opened once or two hundred times, so every assertion in
  // them is over `custody.openCalls` and the rows are checked only for being
  // there.
  //
  // **The fourth reads values and counts nothing, and it is here because the
  // saving the first three describe is what puts those values at risk.** A read
  // that opens per row cannot pair a name with the wrong row; a read that looks
  // one up in a map it pre-opened can, and a lookup with a loose key does not
  // miss — it answers with somebody else's counterparty. That is the silent
  // defect `transaction-view.ts`'s head names: the tag check never fails,
  // nothing errors, no server can see it, and a person reads another row's
  // payee on their own transaction. It is green against the read that ships
  // today and has to stay green against the one that replaces it.
  //
  // **The fifth is a source-text pin and holds a delegation and nothing more.**
  // Five of the six wrong key grammars produce the counts the right one does,
  // so the pair below pins the count and not the key; the key is pinned in
  // `narrative-batch.spec.ts`, and only if the service actually calls
  // `openNarrativeBatch`. A service rolling its own map with a loose key passes
  // every other case here and never reaches that file.
  //
  // **The first two are a pair, and the second is a conservation check rather
  // than a guard on the first.** One says the count fell to the number of
  // *distinct* values a response holds. The other runs a fixture with nothing
  // to share and says the count is then exactly one open per sealed column —
  // neither fewer, which would be a collapse of two values the key says are
  // different, nor more, which would be a column opened twice. Said plainly
  // because it is easy to overstate: a read answering one constant opens once
  // and fails the first case on its own, and so does one keyed on
  // `<table>.<column>`. What the second adds is the other direction, over the
  // one fixture where the arithmetic has no duplicates hiding either mistake.
  //
  // **The third is the observable form of a rule that lives elsewhere.**
  // `narrative-batch.ts` keeps no state between calls because opened narrative
  // that outlived a read would be a second thing for `SessionService.ended()`
  // to clear, and `account-keys.md` gives clearing exactly one owner. Nothing
  // in the compiler holds that; a read that re-opens its own values does.
  describe('how many opens one list read costs', () => {
    // The five identifier families this fixture draws from. Kept apart by the
    // leading pair of digits so a row id can never collide with an account id
    // and quietly make two questions look like one.
    const ROW_IDS = 10;
    const ACCOUNT_IDS = 11;
    const PAYEE_IDS = 12;
    const CATEGORY_IDS = 13;
    const GROUP_IDS = 14;

    // Two hundred rows, which is a month somebody actually has. Small fixtures
    // cannot ask this question: at three rows the difference between opening
    // every member and opening every distinct member is a handful of calls
    // either way, and nothing about the read's shape shows through it.
    const ROWS = 200;

    // Each case's own stretch of the identifier space, so that no two of them
    // name one row or one counterparty. Nothing shares state between them
    // today — a batch's map is built inside one call and handed to its caller
    // — but these three cases exist because something might, and cases whose
    // fixtures overlap would then fail in each other's names. Disjoint id
    // spaces keep each red bar pointing at the case that earned it.
    const SHARED_NAMES_AT = 0;
    const DISTINCT_NAMES_AT = 1000;
    const SECOND_READ_AT = 5000;

    // Canonical lower-case hyphenated UUIDs, because that is the spelling the
    // wire really carries and the one the codec accepts.
    function censusId(family: number, index: number): string {
      const tail = `${String(family)}${String(index).padStart(10, '0')}`;

      return `0199c3d4-5f6a-7b8c-9d0e-${tail}`;
    }

    // Which account, payee, category and group one row names. Indices rather
    // than identifiers, so a fixture states its own sharing in the one place a
    // reader counts it from.
    interface RowNames {
      readonly account: number;
      readonly payee: number;
      readonly category: number;
      readonly group: number;
    }

    function censusTransaction(row: number, names: RowNames): TransactionDto {
      const id = censusId(ROW_IDS, row);
      const accountId = censusId(ACCOUNT_IDS, names.account);
      const payeeId = censusId(PAYEE_IDS, names.payee);
      const categoryId = censusId(CATEGORY_IDS, names.category);
      const groupId = censusId(GROUP_IDS, names.group);

      return {
        id,
        amount: -20.5,
        date: '2026-07-14',
        description: sealedWire(
          'transactions',
          'description',
          id,
          `Note ${String(row)}`,
        ),
        createdAtUtc: '2026-07-14T10:00:00Z',
        accountId,
        accountName: sealedWire(
          'accounts',
          'name',
          accountId,
          `Account ${String(names.account)}`,
        ),
        currencyCode: 'USD',
        currencySymbol: '$',
        payeeId,
        payeeName: sealedWire(
          'payees',
          'name',
          payeeId,
          `Payee ${String(names.payee)}`,
        ),
        categoryId,
        categoryName: sealedWire(
          'categories',
          'name',
          categoryId,
          `Category ${String(names.category)}`,
        ),
        categoryGroupId: groupId,
        categoryGroupName: sealedWire(
          'category_groups',
          'name',
          groupId,
          `Group ${String(names.group)}`,
        ),
      };
    }

    // A response of `count` rows, each built from its own index.
    //
    // `Array.from`'s mapper takes the element first and the index second, and
    // there is no element here — so the discard is named once, in this one
    // place, rather than at each of the three fixtures below.
    function censusRows(
      count: number,
      row: (index: number) => TransactionDto,
    ): readonly TransactionDto[] {
      return Array.from({ length: count }, (unused, index) => row(index));
    }

    // How many distinct values one sealed member of a response holds.
    //
    // Counting wires counts (binding, wire) pairs here, and that is a property
    // of the fixture rather than a shortcut: every wire above encodes the row
    // id it was sealed under, so two equal wires on one member are two equal
    // bindings as well. An open is charged per pair, which is why this is the
    // unit the table below is written in.
    function distinctWires(
      rows: readonly TransactionDto[],
      member: (row: TransactionDto) => string | null | undefined,
    ): number {
      const wires = rows
        .map(member)
        .filter((wire): wire is string => typeof wire === 'string');

      return new Set(wires).size;
    }

    // Waits for the opens to stop arriving rather than for a fixed number of
    // ticks. `settle()` yields one macrotask, which is everything an inline
    // read needs; a read that pre-opens a whole screenful may hand the frame
    // back to the browser between chunks, and each hand-back is a macrotask of
    // its own. Fifty bounds a hang and is never a count of anything.
    async function settleOpens(): Promise<void> {
      let previous = -1;

      for (
        let attempt = 0;
        attempt < 50 && previous !== custody.openCalls.length;
        attempt += 1
      ) {
        previous = custody.openCalls.length;
        await settle();
      }

      await settle();
    }

    it('opens each distinct narrative once across a two-hundred row list', async () => {
      // Arrange — the ordinary shape of a month: every row carries a note of
      // its own, and the four foreign names on it are copies. Four accounts,
      // forty payees, twenty-five categories, eight groups.
      const rows = censusRows(ROWS, (row) =>
        censusTransaction(SHARED_NAMES_AT + row, {
          account: SHARED_NAMES_AT + (row % 4),
          category: SHARED_NAMES_AT + (row % 25),
          group: SHARED_NAMES_AT + ((row % 25) % 8),
          payee: SHARED_NAMES_AT + (row % 40),
        }),
      );

      // What the fixture actually holds, read off the fixture rather than
      // remembered. The table below restates these five numbers as claims, and
      // these five lines are what stops the table being edited into agreement
      // with whatever the service happened to do.
      expect(distinctWires(rows, (row) => row.description)).toBe(200);
      expect(distinctWires(rows, (row) => row.accountName)).toBe(4);
      expect(distinctWires(rows, (row) => row.payeeName)).toBe(40);
      expect(distinctWires(rows, (row) => row.categoryName)).toBe(25);
      expect(distinctWires(rows, (row) => row.categoryGroupName)).toBe(8);

      // Fixture cardinality → opens it is worth → why, and the expectation is
      // the sum of these terms and of nothing else:
      //
      //   200 notes, one per row, each sealed under its own identifier
      // +   4 account names, each named by fifty rows
      // +  40 payee names
      // +  25 category names
      // +   8 category-group names
      // = 277 distinct (binding, wire) pairs, and 277 opens.
      //
      // Written as a table so that greening this by pasting an observed number
      // means adding a row here and saying what it is a count of. There is no
      // sixth sealed member on this row for such a row to describe.
      const census: readonly {
        readonly member: string;
        readonly distinct: number;
        readonly why: string;
      }[] = [
        {
          distinct: 200,
          member: 'description',
          why: 'sealed under its own row id, so no two rows ask one question',
        },
        {
          distinct: 4,
          member: 'accountName',
          why: 'four accounts, denormalized onto every row that draws on one',
        },
        {
          distinct: 40,
          member: 'payeeName',
          why: 'forty counterparties, most of them named by several rows',
        },
        {
          distinct: 25,
          member: 'categoryName',
          why: 'twenty-five categories over two hundred rows',
        },
        {
          distinct: 8,
          member: 'categoryGroupName',
          why: 'eight groups, and a group is named by every row in it',
        },
      ];

      const expected = census.reduce(
        (total, entry) => total + entry.distinct,
        0,
      );

      // Act
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: rows });
      await settleOpens();

      // Assert — the list is checked for being there and never for what it
      // says, so that a read which opened 277 times and published nothing
      // cannot be mistaken for one that did the work.
      expect(service.transactions()).toHaveLength(ROWS);
      expect(custody.openCalls).toHaveLength(expected);
    });

    it('opens all five members of every row when a list shares no name', async () => {
      // Arrange — the control, and what it holds is a conservation invariant
      // rather than a second reading of the case above. Every row here names an
      // account, a payee, a category and a group of its own, so there is not
      // one duplicate in the response to collapse and the count is pinned to
      // the total number of sealed columns: fewer is a value collapsed that the
      // key says is distinct, more is a column opened twice, and the fixture
      // has no repetition for either to hide behind.
      //
      // **Not** because a read answering a constant would otherwise slip
      // through — that read opens once and fails the 277 above it. The case
      // above is a claim about de-duplication; this one is the claim that
      // nothing else moved.
      const rows = censusRows(ROWS, (row) =>
        censusTransaction(DISTINCT_NAMES_AT + row, {
          account: DISTINCT_NAMES_AT + row,
          category: DISTINCT_NAMES_AT + row,
          group: DISTINCT_NAMES_AT + row,
          payee: DISTINCT_NAMES_AT + row,
        }),
      );

      expect(distinctWires(rows, (row) => row.description)).toBe(ROWS);
      expect(distinctWires(rows, (row) => row.accountName)).toBe(ROWS);
      expect(distinctWires(rows, (row) => row.payeeName)).toBe(ROWS);
      expect(distinctWires(rows, (row) => row.categoryName)).toBe(ROWS);
      expect(distinctWires(rows, (row) => row.categoryGroupName)).toBe(ROWS);

      // The same arithmetic as the case above with every term at its maximum:
      // 200 notes + 200 account names + 200 payee names + 200 category names
      // + 200 group names, which is the five sealed members of every row.
      const membersPerRow = 5;
      const expected = ROWS * membersPerRow;

      // Act
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: rows });
      await settleOpens();

      // Assert
      expect(service.transactions()).toHaveLength(ROWS);
      expect(custody.openCalls).toHaveLength(expected);
    });

    it('opens the second read’s values again rather than reusing the first’s', async () => {
      // Arrange — two reads with nothing in between, and the **same** response
      // both times. The identical payload is the instrument: a store that
      // outlived a read would be missed by a second read of different rows,
      // and this case would then pass straight over it. Ten rows sharing one
      // account, one payee, one category and one group, so a cache built by
      // the first read would have almost everything the second one wants.
      const rows = censusRows(10, (row) =>
        censusTransaction(SECOND_READ_AT + row, {
          account: SECOND_READ_AT,
          category: SECOND_READ_AT,
          group: SECOND_READ_AT,
          payee: SECOND_READ_AT,
        }),
      );

      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: rows });
      await settleOpens();

      const first = custody.openCalls.length;

      expect(first).toBeGreaterThan(0);

      // Act — no lock, no unlock, no session change: the one sequence in which
      // a cache would look free.
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: rows });
      await settleOpens();

      // Assert — on the count and never on the values, because a cache serves
      // exactly the right text and is still the defect. A batch's map lives
      // for one read; anything longer-lived is a second store of opened
      // narrative, needing a clearer of its own, on a rule that has one owner.
      expect(custody.openCalls.length - first).toBe(first);
    });

    // A fifth stretch of the identifier space, for the reason the three above
    // have theirs: disjoint spaces keep a red bar pointing at the case that
    // earned it.
    const OWN_NAMES_AT = 9000;

    // How often each foreign name repeats down the list, and the four numbers
    // are the design of this fixture rather than four arbitrary sizes.
    //
    // **Pairwise coprime**, so a mispairing that hands row *i* the value from
    // row *i + s* disagrees with row *i* on some member unless `s` is a
    // multiple of 3 × 7 × 11 × 13 — three thousand and three, fifteen times
    // longer than this list.
    //
    // **Interleaved (`row % k`) and never blocked (`Math.floor(row / k)`)**,
    // which is the trap a fixture like this falls into first: blocked, fifty
    // neighbouring rows share an account name and an off-by-one is invisible
    // on forty-nine of them. Interleaved, no two neighbours agree on anything.
    const ACCOUNT_CYCLE = 3;
    const PAYEE_CYCLE = 7;
    const CATEGORY_CYCLE = 11;
    const GROUP_CYCLE = 13;

    // Which family an identifier came from and which member of it — the
    // inverse of `censusId`, and what lets a name's owner be read off the
    // identifier the **view** came back with rather than off the fixture that
    // wrote it. That direction is the whole of the pairing question: it asks
    // each published row which account, payee, category and group it names,
    // and then whether the words on it belong to those four rows.
    interface CensusIdentifier {
      readonly family: number;
      readonly index: number;
    }

    function censusIdentifier(id: string): CensusIdentifier {
      // The last twelve digits, which `censusId` writes as two of family
      // followed by ten of index.
      const tail = id.slice(-12);

      return { family: Number(tail.slice(0, 2)), index: Number(tail.slice(2)) };
    }

    // The name an identifier owns, spelled the way `censusTransaction` sealed
    // it. An identifier from the wrong family answers a sentence rather than a
    // name — the colon is what keeps it out of the space of real names — so a
    // row wearing an account id where a payee id belongs is a finding and
    // never an accidental match.
    function nameOwnedBy(
      family: number,
      label: string,
      id: string | null | undefined,
    ): string {
      if (id == null) {
        return `${label}: none`;
      }

      const owner = censusIdentifier(id);

      return owner.family === family
        ? `${label} ${String(owner.index)}`
        : `${label}: from family ${String(owner.family)}`;
    }

    // The identifiers one row carries, on the DTO the fixture wrote and on the
    // view the service published alike. The two are compared against each
    // other below, so one accessor has to be able to read both.
    interface RowIdentifiers {
      readonly id: string;
      readonly accountId: string;
      readonly payeeId?: string | null;
      readonly categoryId?: string | null;
      readonly categoryGroupId?: string | null;
    }

    function namesOwnedBy(row: RowIdentifiers): readonly string[] {
      return [
        nameOwnedBy(ROW_IDS, 'Note', row.id),
        nameOwnedBy(ACCOUNT_IDS, 'Account', row.accountId),
        nameOwnedBy(PAYEE_IDS, 'Payee', row.payeeId),
        nameOwnedBy(CATEGORY_IDS, 'Category', row.categoryId),
        nameOwnedBy(GROUP_IDS, 'Group', row.categoryGroupId),
      ];
    }

    // What a published row actually says. A word that is not `text` travels as
    // the word: `unreadable` is exactly what the stub answers for a name
    // opened under another row's binding, and it has to reach the comparison
    // rather than being flattened into an absence.
    function openedNames(view: TransactionView): readonly string[] {
      return [
        view.description,
        view.accountName,
        view.payeeName,
        view.categoryName,
        view.categoryGroupName,
      ].map((value) => {
        if (value === null) {
          return 'none';
        }

        return value.state === 'text' ? value.value : value.state;
      });
    }

    it('never hands a row another row’s counterparty', async () => {
      // Arrange — two hundred rows on which each foreign name repeats on its
      // own cycle and the note repeats not at all. No case in this file opens
      // a multi-row list and asks whether row seven's payee is row seven's:
      // the binding case up top uses a single row carrying one of each name,
      // where mispairing is not available.
      //
      // It is not hypothetical. A read that opens per row cannot mispair; a
      // read that looks a name up in a map it pre-opened can, and **a lookup
      // with a loose key does not miss — it answers with the wrong value**.
      // Row seven then renders row three's counterparty: the tag check never
      // fails, nothing errors, and no server can see it, because the server
      // holds no key and cannot tell a value that opened from one that did
      // not. That is what `transaction-view.ts`'s head says the file exists to
      // prevent.
      const rows = censusRows(ROWS, (row) =>
        censusTransaction(OWN_NAMES_AT + row, {
          account: OWN_NAMES_AT + (row % ACCOUNT_CYCLE),
          category: OWN_NAMES_AT + (row % CATEGORY_CYCLE),
          group: OWN_NAMES_AT + (row % GROUP_CYCLE),
          payee: OWN_NAMES_AT + (row % PAYEE_CYCLE),
        }),
      );

      // What the fixture holds, read off the fixture. Sharing is the point
      // here rather than a saving: a list on which nothing repeats cannot
      // mispair, because there is no second row to pair a row with.
      expect(distinctWires(rows, (row) => row.description)).toBe(ROWS);
      expect(distinctWires(rows, (row) => row.accountName)).toBe(ACCOUNT_CYCLE);
      expect(distinctWires(rows, (row) => row.payeeName)).toBe(PAYEE_CYCLE);
      expect(distinctWires(rows, (row) => row.categoryName)).toBe(
        CATEGORY_CYCLE,
      );
      expect(distinctWires(rows, (row) => row.categoryGroupName)).toBe(
        GROUP_CYCLE,
      );

      // Three rows spelled out, and they are chosen rather than picked: across
      // them every member holds three **different** values. That is the
      // control that a mispairing is visible at all — the commonest loose key
      // collapses a member to one entry and hands every row one row's name,
      // and whichever row that is, two of these three disagree with it on
      // every member. Rows 5, 13 and 30 differ mod 3, mod 7, mod 11 and mod 13
      // alike, which is what "three of anything nearby" would not.
      const sample = [5, 13, 30];
      const repeated = [0, 1, 2, 3, 4]
        .map((member) => sample.map((row) => namesOwnedBy(rows[row])[member]))
        .filter((values) => new Set(values).size !== sample.length);

      expect(
        repeated,
        `a member repeats across the sample: ${repeated.map((values) => values.join(', ')).join(' | ')}`,
      ).toEqual([]);

      // Act
      service.load();
      http.expectOne(TRANSACTIONS_URL).flush({ items: rows });
      await settleOpens();

      // Assert
      const views = service.transactions() ?? [];

      expect(views).toHaveLength(ROWS);

      // Every row rather than only the sample, because it costs nothing: each
      // published row is asked which four rows it names, and whether the five
      // words on it are the ones those rows own. A row given somebody else's
      // counterparty answers here with a name that exists, on a row that never
      // named it — the whole shape of the defect.
      //
      // Measured rather than argued. Staged through this file's own stub,
      // keyed on `<table>.<column>` with the row id dropped — the loose key a
      // batch-backed opener invites — this fixture reports 199 of the 200 rows
      // and names the first three of them.
      const misplaced = views
        .map((view) => ({
          drew: openedNames(view).join(' / '),
          owns: namesOwnedBy(view).join(' / '),
        }))
        .filter((entry) => entry.drew !== entry.owns)
        .map((entry) => `${entry.drew} — on a row owning ${entry.owns}`);

      expect(
        misplaced,
        `${String(misplaced.length)} of ${String(ROWS)} rows drew a name they do not own: ${misplaced.slice(0, 3).join(' | ')}`,
      ).toEqual([]);

      // And the sample once more, against the **fixture** rather than against
      // each view's own identifiers — the half the sweep above cannot make. A
      // read that published every row's own names in the wrong order agrees
      // with every row about itself and disagrees here.
      for (const row of sample) {
        expect(
          openedNames(views[row]).join(' / '),
          `row ${String(row)} of the published list`,
        ).toBe(namesOwnedBy(rows[row]).join(' / '));
      }
    });

    // The service's own source, read from `src/` rather than from a bundle.
    // The claim is about what was written — an import, a call — and a bundler
    // inlines, renames and tree-shakes enough that the emitted JavaScript
    // would answer this question wrongly whichever way it answered it.
    const servicePath = join(
      process.cwd(),
      'src',
      'app',
      'transactions',
      'transactions.service.ts',
    );

    // Comments blanked rather than deleted, so a line number in a finding is
    // the line number in the file. Blanking is also what keeps the scan honest
    // about prose: the head of `transactions.service.ts` argues its own
    // mechanics at length, and a delegation described in a comment over one
    // that was deleted is precisely the state worth reporting.
    //
    // A copy of `narrative-batch.spec.ts`'s stripper rather than an import of
    // it: that file declares it at module scope and exports nothing, and a
    // spec importing another spec would tie two runs together for the sake of
    // two regular expressions.
    function codeWithoutComments(source: string): string {
      return source
        .replace(/\/\*[\s\S]*?\*\//g, (block) => block.replace(/[^\n]/g, ' '))
        .split('\n')
        .map((line) => line.replace(/(^|\s)\/\/.*$/, '$1'))
        .join('\n');
    }

    // Where the batch lives and what it is called, named once so a module that
    // moves carries this scan with it rather than leaving it watching a string
    // nothing imports.
    const BATCH_MODULE = '@app-core/security/narrative-batch';
    const BATCH_FUNCTION = 'openNarrativeBatch';

    // What the service's code says about the batch, as findings and not as a
    // boolean: this is a rule about what is written, so the text found is the
    // whole of the report.
    function batchDelegation(code: string): readonly string[] {
      const findings: string[] = [];
      const imported = new RegExp(
        `import\\s+(type\\s+)?\\{([^}]*)\\}\\s*from\\s*'${BATCH_MODULE}'`,
      ).exec(code);

      if (imported === null) {
        findings.push(`nothing is imported from ${BATCH_MODULE}`);
      } else {
        const names = imported[2]
          .split(',')
          .map((name) => name.trim())
          .filter((name) => name !== '');

        if (imported[1] !== undefined) {
          findings.push(`${BATCH_MODULE} is imported for its types only`);
        }

        if (!names.includes(BATCH_FUNCTION)) {
          findings.push(
            `${names.join(', ')} is imported from ${BATCH_MODULE}, not ${BATCH_FUNCTION}`,
          );
        }
      }

      if (!code.includes(`${BATCH_FUNCTION}(`)) {
        findings.push(`${BATCH_FUNCTION} is never called`);
      }

      // The roll-your-own shape, which is what the two positives above cannot
      // see on their own: a service may call the batch **and** keep a map of
      // opened values beside it. Honest about being a shape check — it catches
      // the literal `new Map`, and a record built by `reduce`, or an object
      // keyed by hand, walks straight past it.
      code.split('\n').forEach((line, index) => {
        if (/\bnew\s+(Map|WeakMap)\b/.test(line)) {
          findings.push(
            `line ${String(index + 1)}: a map of its own — ${line.trim()}`,
          );
        }
      });

      return findings;
    }

    it('reads the list through the shared batch and keeps no map of its own', () => {
      // Arrange — a source scan, because nothing that runs here can see this.
      // The count pair above pins how many opens a read costs and says nothing
      // about the key they were looked up under: five of the six wrong key
      // grammars cost exactly what the right one costs. The key is pinned in
      // `narrative-batch.spec.ts` — and only for a service that calls the
      // batch at all. One that rolls its own map with a loose key passes every
      // other case in this file and never reaches that spec.
      //
      // **This holds a delegation and cannot hold that the delegation is
      // right.** It says the import is there, the function is called, and no
      // `new Map` sits beside it. It cannot see what is passed, whether the
      // answer is used, or whether a miss falls through to the real opener —
      // the case above it and `narrative-batch.spec.ts` are what say those.
      const source = readFileSync(servicePath, 'utf8');
      const code = codeWithoutComments(source);

      // Two planted sources, so the scan is known to speak in both directions.
      // Without them a pattern matching nothing reports the real file dirty
      // forever and one matching everything reports it clean forever, and
      // neither is visible from a single result.
      const delegating = [
        `import { openNarrativeBatch, type NarrativeRequest } from '${BATCH_MODULE}';`,
        'const opened = await openNarrativeBatch(requests, this.#open);',
      ].join('\n');
      const rollingItsOwn = [
        "import { toTransactionView } from './transaction-view';",
        'const opened = new Map<string, NarrativeText>();',
      ].join('\n');

      // Act
      const findings = batchDelegation(code);
      const plantedClean = batchDelegation(delegating);
      const plantedFindings = batchDelegation(rollingItsOwn);

      // Assert
      // Controls on the read, before any claim about what it found. A path
      // that moved throws, but a stripper that ate the code — or one that ate
      // nothing — hands a wrong answer to the assertion that matters.
      expect(code).toContain('export class TransactionsService {');
      expect(code).not.toContain('**The service mints, seals');

      // The pin. Named and never counted: the finding is the text.
      expect(
        findings,
        `transactions.service.ts: ${findings.join(' | ')}`,
      ).toEqual([]);

      // And the same scan over both plants, so the result above is an answer
      // rather than a blind spot.
      expect(plantedClean).toEqual([]);
      expect(plantedFindings).toHaveLength(3);
      expect(plantedFindings[0]).toContain('nothing is imported');
      expect(plantedFindings[1]).toContain('is never called');
      expect(plantedFindings[2]).toContain('a map of its own');
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
          nameKey: indexValue('payees', 'name', BUDGET_ID, 'Corner Shop'),
        },
      ]);
    });

    it('publishes the payee list in compareNarrative order', async () => {
      // Arrange — four rows carrying all three of `compare-narrative.ts`'s
      // words: two that opened, one that did not, one this browser holds no key
      // for. The order it asks for is `text`, then `unreadable`, then `locked`,
      // and the two opened names between themselves by `localeCompare`.
      // A mixed list is reachable rather than exotic:
      // lockedness is account-wide, but custody can move between the first
      // row's open and the last one's, and `#readPayees` publishes whatever the
      // opens came back with.
      //
      // The two readable names are ASCII and their relative order does not turn
      // on collation, which is deliberate: `localeCompare` reads the host
      // locale, nothing in this app provides `LOCALE_ID`, and the runner pins
      // the time zone and not the locale. Measured from inside a run,
      // `new Intl.Collator().resolvedOptions().locale` answers `en-US` on this
      // machine — which is exactly the value a fixture may not depend on, since
      // nothing configures it and CI is another machine. `Alpha` before `Zulu`
      // is the same answer under every collation there is.
      const bakeryId = '0199c3d4-5f6a-7b8c-9d0e-00000000000a';
      const alphaId = '0199c3d4-5f6a-7b8c-9d0e-00000000000b';
      const aardvarkId = '0199c3d4-5f6a-7b8c-9d0e-00000000000c';
      const zuluId = '0199c3d4-5f6a-7b8c-9d0e-00000000000d';

      // Which row opens to which word, keyed on the row rather than on the text
      // — so the plaintext behind a name that never opens is a real name, and
      // the word is what decides the row's place rather than the letters. It is
      // also what stops the two rows that never open from being sorted by the
      // text behind them: `Aardvark` leads this list alphabetically and comes
      // last. Measured against this fixture, a sort on the plaintext with the
      // word ignored answers locked, Alpha, unreadable, Zulu.
      custody.openWith = (binding, wire) => {
        if (binding.rowId === aardvarkId) {
          return { state: 'locked' };
        }

        if (binding.rowId === bakeryId) {
          return { state: 'unreadable' };
        }

        return { state: 'text', value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? '' };
      };
      service.loadPayees();

      // Act — flushed in an order that is wrong on every axis: not alphabetical
      // (Aardvark, Zulu, Bakery, Alpha) and not identifier order (c, d, a, b),
      // so a read publishing what the server sent and one sorting on `id` both
      // redden here.
      http.expectOne(PAYEES_URL).flush({
        items: [
          sealedPayee(aardvarkId, 'Aardvark'),
          sealedPayee(zuluId, 'Zulu'),
          sealedPayee(bakeryId, 'Bakery'),
          sealedPayee(alphaId, 'Alpha'),
        ],
      });
      await settle();

      // Assert — on the words and their values and never on object identity.
      // The identifier sequence this order carries is b, d, a, c: neither
      // ascending nor descending, so a read that sorted on the id — or on the
      // wire value, whose first varying part is that id — publishes
      // unreadable, Alpha, locked,
      // Zulu instead. Measured against this fixture, both do.
      expect(service.payees()?.map((view) => view.name)).toEqual([
        { state: 'text', value: 'Alpha' },
        { state: 'text', value: 'Zulu' },
        { state: 'unreadable' },
        { state: 'locked' },
      ]);
    });

    it('publishes a locked list in the order the response listed it', async () => {
      // Arrange — the property `compare-narrative.ts` argues and deliberately
      // does not branch for: on a locked account every comparison answers `0`,
      // `Array.prototype.sort` is stable by specification, and the list a
      // screen shows is therefore the order the API sent — insertion, or a
      // position column — rather than a shuffle nobody chose. Adding an
      // `if (locked) skip the sort` is how that gets lost, and so is a tiebreak.
      //
      // **Honest about how narrow this is.** It pins a consequence, not a
      // branch, so almost nothing can object to it: measured, a missing
      // comparator (`sort()` bare, which string-converts every row to
      // `[object Object]`), a reversed comparator and a deleted sort all three
      // publish exactly this. The one change it catches is a **tiebreak on some
      // other key** — the well-meant "a locked list should at least be
      // deterministic". A tiebreak written *inside* `compareNarrative` is also
      // seen by `compare-narrative.spec.ts`'s `returns zero for two locked
      // values`; one written in this file's `byName`, around a comparator that
      // still answers `0`, is seen here and nowhere else.
      //
      // Three rows and not two, because a comparator that is not stable at all
      // leaves a two-element array alone whatever it answers.
      //
      // Staged as the *opens* answering `locked`, never by moving custody's
      // status: that signal is what the clear-on-lock effect watches, so
      // setting it would empty the very list this case reads.
      const thirdId = '0199c3d4-5f6a-7b8c-9d0e-000000000023';
      const firstId = '0199c3d4-5f6a-7b8c-9d0e-000000000021';
      const secondId = '0199c3d4-5f6a-7b8c-9d0e-000000000022';

      custody.openWith = () => ({ state: 'locked' });
      service.loadPayees();

      // Act — the identifiers arrive out of ascending order, 03 then 01 then
      // 02, and the names are out of alphabetical order beside them. Arrival
      // order is then at odds with id order and with the wire value alike —
      // every wire here is `sealed(payees.name|<id>|…)`, whose first varying
      // part is that id — so a tiebreak has nowhere left to hide.
      http.expectOne(PAYEES_URL).flush({
        items: [
          sealedPayee(thirdId, 'Cherry'),
          sealedPayee(firstId, 'Apple'),
          sealedPayee(secondId, 'Banana'),
        ],
      });
      await settle();

      // Assert
      const views = service.payees() ?? [];

      // The control first: one row that opened would make this a different
      // question, and the comparison below would still pass on two of three.
      expect(views.map((view) => view.name.state)).toEqual([
        'locked',
        'locked',
        'locked',
      ]);
      expect(views.map((view) => view.id)).toEqual([
        thirdId,
        firstId,
        secondId,
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

    it('reports the abandon when the note cannot be sealed', async () => {
      // Arrange — four of this write's five exits returned in silence, and a
      // write that wrote nothing and said nothing is indistinguishable, from
      // outside this service, from one that landed. There is no notification
      // surface in this app yet — the design book owes a chapter for it — so
      // the rule is the narrower one: the one channel that exists may not be
      // silent on the way past.
      // The history is cleared, and that is not tidiness. Nothing in this
      // project restores mocks between cases, so the spy an earlier case
      // installed is still in place with its calls on it — and
      // `toHaveBeenCalled()` over that passes whatever this case did.
      // Measured: all four of these cases passed against a service that
      // reported nothing at all.
      const reported = vi
        .spyOn(console, 'error')
        .mockImplementation(() => undefined);

      reported.mockClear();
      custody.sealAnswer = { state: 'locked' };

      // Act
      await service.add(typed());
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      expect(reported).toHaveBeenCalled();
    });

    it('reports the abandon when the payee index answers locked', async () => {
      // Arrange — the second silent exit. A browser holding no index key
      // cannot decide whether this counterparty is already on the list.
      const reported = vi
        .spyOn(console, 'error')
        .mockImplementation(() => undefined);

      reported.mockClear();
      custody.indexAnswer = { state: 'locked' };

      // Act
      await service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      http.expectNone(PAYEES_URL);
      expect(reported).toHaveBeenCalled();
    });

    it('reports the abandon when the payee name cannot be sealed', async () => {
      // Arrange — the third. The note sealed, the index was computed, and the
      // create is the operation that refused.
      const reported = vi
        .spyOn(console, 'error')
        .mockImplementation(() => undefined);

      reported.mockClear();
      custody.sealAnswersByTable.set('payees', { state: 'locked' });

      // Act
      await service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      http.expectNone(PAYEES_URL);
      expect(reported).toHaveBeenCalled();
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
        // The payee list's own key, and nothing for the empty field. The whole
        // binding, so the tenancy is asserted beside the pair and a `rowId`
        // smuggled in is a finding rather than a member nothing looked at.
        {
          binding: { table: 'payees', column: 'name', budgetId: BUDGET_ID },
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

    // **The counterparty of one budget does not answer for another's, and this
    // is where a person would see it.** The account holds one index key, so
    // before the tenancy entered the message a payee read under one budget keyed
    // to exactly the value a write under the next one computed — and the write
    // would file its transaction against a row belonging to somewhere else. The
    // list is read while the browser is in one budget and the write happens while
    // it is in another, which is what a switch between two of them looks like
    // from here.
    it('never reuses a payee keyed inside another budget', async () => {
      // Arrange
      const other = '7c1e42b8-9a05-4d63-8f77-0b2c5e9a1d34';

      await loadPayees(sealedPayee(PAYEE_ID, 'Corner Shop'));

      // The guard that keeps the arrangement honest: under the *same* tenancy
      // this very list and this very name do match — the case above is that —
      // so what changes below is the budget and nothing else.
      expect(service.payees()).toHaveLength(1);
      session.setBudgetId(other);

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      // Assert — a create rather than a reuse, and the row it posts is a new
      // one.
      const create = http.expectOne(PAYEES_URL);
      const body = create.request.body as { id: string; nameKey: string };

      expect(body.id).not.toBe(PAYEE_ID);
      expect(body.nameKey).toBe(
        indexValue('payees', 'name', other, 'Corner Shop'),
      );

      create.flush(sealedPayee(body.id, 'Corner Shop'), {
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
        nameKey: indexValue('payees', 'name', BUDGET_ID, 'Bakery'),
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
      // **Two** calls, over the same untrimmed text: the second is taken on
      // the far side of the seal and compared with the first, because an
      // `adopt()` landing between the two operations is invisible to both. The
      // pair, not the count, is what this expectation is about — a single call
      // here would mean the comparison had gone.
      // Both inside the **same** tenancy, which is the half the pair would not
      // have without it: two reads of the session either side of the seal would
      // key the row's column and the row's match inside two budgets, and the
      // comparison the create makes would then be over two different questions.
      expect(custody.indexCalls).toEqual([
        {
          binding: { table: 'payees', column: 'name', budgetId: BUDGET_ID },
          plaintext: '  Bakery  ',
        },
        {
          binding: { table: 'payees', column: 'name', budgetId: BUDGET_ID },
          plaintext: '  Bakery  ',
        },
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
      create.flush(
        { conflictKind: 'duplicate_name' },
        { status: 409, statusText: 'Conflict' },
      );
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
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
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
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
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
          nameKey: indexValue('payees', 'name', BUDGET_ID, 'Bakery'),
        },
      ]);
    });

    it('puts a created payee in its sorted place rather than at the end', async () => {
      // Arrange — the read answers **Zulu before Alpha**, so the list this case
      // publishes is alphabetical only if something sorted it.
      //
      // **On the question of which of the two sites sorts, this case covers the
      // create path and the read case next door covers the read path — one
      // each, and neither covers the other's.** Measured, strictly 1:1: taking
      // the sort out of `#readPayees` alone reddens `publishes the payee list
      // in compareNarrative order` and leaves this case passing; taking it out
      // of the create path alone reddens this case and leaves that one passing.
      // It can never be otherwise, and no fixture changes it: the create path
      // re-sorts the **whole** list, so it launders an unsorted held list
      // before the ordering assertion below ever reads it. That re-sort is
      // required — an insertion at an index computed there would be a second
      // implementation of the ordering — so the laundering is a property of the
      // code under test, not of the arrangement above.
      //
      // **One row on the read never opens, and it is what makes the *word*
      // ranking load-bearing on this path.** Without it this case pins only
      // that the create path sorts *something*. Measured against the two-row,
      // all-readable fixture it used to carry: a sort on `nameKey`, a sort that
      // ignores the word and reads the plaintext, and an insertion at a
      // computed index all three publish exactly what `compareNarrative`
      // publishes.
      //
      // **What that row bought is one axis of comparator variation out of
      // three.** Measured against the fixture below, with the change confined
      // to this path: a comparator that reads the plaintext and ignores the
      // word now reddens; one with the `unreadable` and `locked` ranks swapped
      // does not, because this fixture holds no `locked` row; one with an `id`
      // tiebreak does not, because no two rows here compare equal.
      //
      // Confining a change to this path at all means splitting `byName` in two,
      // and the *unconfined* versions of those two are held: the rank order by
      // `publishes the payee list in compareNarrative order` and by
      // `compare-narrative.spec.ts`, a tiebreak inside the comparator by
      // `compare-narrative.spec.ts` and one in `byName` by `publishes a locked
      // list in the order the response listed it`. What no case anywhere sees
      // is a create-path-only change on either axis. That is a limit worth
      // naming rather than a gap worth closing: staging a `locked` row on this
      // path would be stub artifice, for the reason the next paragraph gives.
      //
      // **`unreadable` rather than `locked`**, deliberately: a create has to
      // seal a name and index it, so custody is open for the whole of this
      // sequence, and one value that failed to open is the honest shape beside
      // it. A `locked` row here would be a state this sequence cannot be in —
      // stub artifice, standing in for the account-wide word.
      //
      // Each of the three now disagrees, and for its own reason. The row that
      // did not open carries a real name, `Mango`, which sorts **in among** the
      // readable ones — so a comparator reading the plaintext and ignoring the
      // word puts it second where the ordering puts it last. It carries no key
      // at all: `toPayeeView` keys nothing it could not read, so `nameKey` is
      // `null` there, `left.nameKey.localeCompare(…)` does not even compile
      // (TS18047), and the `?? ''` that does sorts the row to the front. And
      // the created name sorts **after** every readable one, which is the
      // position a computed index gets wrong — it finds no readable row to go
      // in front of and appends, past the row that belongs last.
      //
      // **No second `GET /api/payees` is flushed anywhere below**, and that is
      // the guard this case shares with `adds a created payee to the list it
      // holds` next door: `afterEach`'s `http.verify()` refuses a request
      // nobody expected, so an append "simplified" into a re-read — which would
      // also publish a sorted list, and would otherwise take both cases quietly
      // with it — fails rather than passing for the wrong reason.
      const zuluId = '0199c3d4-5f6a-7b8c-9d0e-00000000001c';
      const alphaId = '0199c3d4-5f6a-7b8c-9d0e-00000000001a';
      const unopenedId = '0199c3d4-5f6a-7b8c-9d0e-00000000001b';

      // Keyed on the row and not on the text, so the plaintext behind the value
      // that never opens is a real name rather than a marker — which is what
      // lets a comparator reading it sort the row somewhere plausible.
      custody.openWith = (binding, wire) =>
        binding.rowId === unopenedId
          ? { state: 'unreadable' }
          : { state: 'text', value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? '' };

      await loadPayees(
        sealedPayee(zuluId, 'Zulu'),
        sealedPayee(alphaId, 'Alpha'),
        sealedPayee(unopenedId, 'Mango'),
      );

      // Act — a fourth counterparty, through the same 201 sequence the append
      // case uses. Appending puts it after the row that did not open, where the
      // list stays wrong until the next read hides it again.
      void service.add(typed({ payee: 'Zurich' }));
      await settle();

      const create = http.expectOne(PAYEES_URL);
      const created = create.request.body as { id: string };

      create.flush(sealedPayee(created.id, 'Zurich'), {
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

      // Assert — the whole published sequence, because where the new row landed
      // is only legible against its neighbours. Four ASCII names, whose
      // relative order is the same answer under every collation there is: the
      // runner pins the time zone and not the locale, and `localeCompare` reads
      // the host's.
      expect(service.payees()?.map((view) => view.name)).toEqual([
        { state: 'text', value: 'Alpha' },
        { state: 'text', value: 'Zulu' },
        { state: 'text', value: 'Zurich' },
        { state: 'unreadable' },
      ]);
    });

    it('adds nothing to a payee list no read has answered', async () => {
      // Arrange — no `loadPayees()` anywhere: the list is `null`, which is "no
      // answer yet" and not "this budget has no payees". Appending to it
      // fabricates a list of one over a read that never landed, and the screen
      // then offers a single suggestion as though it had the set.

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

      // Assert
      expect(service.payees()).toBeNull();
    });

    it('abandons the create when custody moves between the index and the seal', async () => {
      // Arrange — the window **neither** ordering closes, and the reason this
      // path re-asks for the key rather than swapping two lines. `blindIndex`
      // compares the generation counter and `sealField` compares key identity,
      // so each refuses an `adopt()` that lands *during* it — and an `adopt()`
      // that lands strictly between the two is invisible to both, whichever
      // runs first. What it produces is a payee row whose `name` was sealed
      // under one account's content key and whose `name_key` was computed
      // under another's index key: the envelope and the uniqueness value
      // disagree, through the one door the server cannot see, because it holds
      // no index key and can never recompute one.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await loadPayees();
      custody.onSeal = (binding) => {
        if (binding.table === 'payees') {
          custody.indexAnswer = {
            state: 'computed',
            value: indexValue('payees', 'name', BUDGET_ID, 'another account'),
          };
        }
      };

      // Act — `void` rather than `await`: an implementation that posts the
      // mismatched pair leaves a request outstanding, and awaiting it would
      // report this case as a timeout rather than as the assertion it is.
      void service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert — nothing is written at all. A payee row is unremovable, so
      // half a pair is worse than no row.
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
    });

    it('reports the abandon when a conflict cannot be resolved', async () => {
      // Arrange — the fourth silent exit, and the severe one: a payee whose
      // own name did not open carries no key, can never match, and 409s
      // forever. Somebody who deals with that counterparty can never record a
      // transaction against it again, so the least this service can do is say
      // so somewhere.
      const reported = vi
        .spyOn(console, 'error')
        .mockImplementation(() => undefined);

      reported.mockClear();
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      expect(reported).toHaveBeenCalled();
    });

    it('says nothing about a conflict it resolves', async () => {
      // Arrange — the other polarity, and the reason the report moved into the
      // branches. A 409 here is the documented resolution path: the list was
      // stale, the re-read answers, the write goes on. Logging it as an error
      // on the way past trains a reader to ignore the one channel this service
      // has, which is what makes the four silent exits above cost anything.
      const reported = vi
        .spyOn(console, 'error')
        .mockImplementation(() => undefined);

      reported.mockClear();
      await loadPayees();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();

      // Assert
      expect(reported).not.toHaveBeenCalled();
    });

    it('keeps only the newest payee read’s answer when two overlap', async () => {
      // Arrange — the argument the list read carries, on the path that never
      // got it: decryption widens the overlap between two reads from one round
      // trip to one round trip plus an open and a MAC per row, and this one is
      // called from two places — the screen and the conflict branch — so two
      // of them in flight is the ordinary case rather than the exotic one.
      // Every resolver is collected: a single `let` would hold the last one
      // only, `Promise.all` would never settle, and the case would pass
      // against an unguarded `set` for a reason that has nothing to do with
      // what it claims.
      const releases: ((value: NarrativeText) => void)[] = [];

      custody.openWith = () =>
        new Promise<NarrativeText>((resolve) => {
          releases.push(resolve);
        });
      service.loadPayees();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Stale')] });
      await settle();

      // Act — a second read overtakes it and lands first.
      custody.openWith = (binding, wire) => ({
        state: 'text',
        value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? binding.rowId,
      });
      service.loadPayees();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Fresh')] });
      await settle();

      for (const release of releases) {
        release({ state: 'text', value: 'Stale' });
      }

      await settle();

      // Assert
      expect(service.payees()).toEqual([
        expect.objectContaining({ name: { state: 'text', value: 'Fresh' } }),
      ]);
    });
  });

  describe('the category picker', () => {
    // Both names cross the wire sealed, and the group's name on a category row
    // is sealed under the **group's** id. Fixtures that carried plaintext could
    // not tell an opened name from a wire value, which is how this picker
    // rendered base64url for a whole phase with a green suite.
    async function loadCategories(): Promise<void> {
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          {
            id: GROUP_ID,
            name: sealedWire('category_groups', 'name', GROUP_ID, 'Essentials'),
            description: sealedWire(
              'category_groups',
              'description',
              GROUP_ID,
              'The bills',
            ),
            position: 0,
          },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({
        items: [
          {
            id: CATEGORY_ID,
            name: sealedWire('categories', 'name', CATEGORY_ID, 'Groceries'),
            description: null,
            categoryGroupId: GROUP_ID,
            categoryGroupName: sealedWire(
              'category_groups',
              'name',
              GROUP_ID,
              'Essentials',
            ),
            position: 0,
          },
        ],
      });
      await settle();
    }

    it('publishes opened names and never wire values', async () => {
      // Act
      await loadCategories();

      // Assert — words, not strings, and the group's name on the category row
      // opened under the group's identifier rather than the category's.
      expect(service.categoryGroups()).toEqual([
        {
          description: { state: 'text', value: 'The bills' },
          id: GROUP_ID,
          name: { state: 'text', value: 'Essentials' },
          position: 0,
        },
      ]);
      expect(service.categories()).toEqual([
        {
          categoryGroupId: GROUP_ID,
          categoryGroupName: { state: 'text', value: 'Essentials' },
          description: null,
          id: CATEGORY_ID,
          name: { state: 'text', value: 'Groceries' },
          position: 0,
        },
      ]);
    });

    it('returns categories belonging to a selected group', async () => {
      // Arrange
      await loadCategories();

      // Act
      const categories = service.categoriesForGroup(GROUP_ID);

      // Assert
      expect(categories.map((category) => category.id)).toEqual([CATEGORY_ID]);
    });

    it('publishes no picker at all when one row’s open is refused', async () => {
      // Arrange — `NarrativeFieldMisuseError` is a defect in this client, so it
      // travels rather than being filed as one name that did not open. This
      // path had no error handling at all before the picker opened anything,
      // which would now make that rejection an unhandled observable error.
      //
      // **Two groups, and only one of them refused**, which is the whole design
      // of this case: with every row refused, `Promise.allSettled` and a
      // failed `Promise.all` both leave the picker holding `[]` and the case
      // cannot tell them apart. One good row is what makes the substitution
      // visible — `allSettled` would publish a picker one group short.
      const otherGroupId = '0199c3d4-5f6a-7b8c-9d0e-000000000006';

      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      custody.openWith = (binding, wire) =>
        binding.rowId === otherGroupId
          ? Promise.reject(new NarrativeFieldMisuseError('refused'))
          : { state: 'text', value: wire };
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          { id: GROUP_ID, name: 'anything', description: null, position: 0 },
          {
            id: otherGroupId,
            name: 'anything',
            description: null,
            position: 1,
          },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });

      // Act
      await settle();

      // Assert — nothing published, rather than the one group that opened.
      expect(service.categoryGroups()).toEqual([]);
      expect(service.categories()).toEqual([]);
    });

    it('keeps only the newest category load’s answer when two overlap', async () => {
      // Arrange — the same argument the transaction list's `switchMap` is
      // written from, on the read it was never applied to. This screen calls
      // `loadCategories()` from `ngOnInit`, so a person landing on it twice in
      // quick succession — a route reload, a back-and-forward — has two in
      // flight, and the slow one publishes last over the fast one's answer.
      const releases: ((value: NarrativeText) => void)[] = [];

      custody.openWith = () =>
        new Promise<NarrativeText>((resolve) => {
          releases.push(resolve);
        });
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          {
            id: GROUP_ID,
            name: sealedWire('category_groups', 'name', GROUP_ID, 'Stale'),
            description: null,
            position: 0,
          },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();

      // Act — a second load overtakes it and lands first.
      custody.openWith = (binding, wire) => ({
        state: 'text',
        value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? binding.rowId,
      });
      service.loadCategories();
      http.expectOne(CATEGORY_GROUPS_URL).flush({
        items: [
          {
            id: GROUP_ID,
            name: sealedWire('category_groups', 'name', GROUP_ID, 'Fresh'),
            description: null,
            position: 0,
          },
        ],
      });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();

      for (const release of releases) {
        release({ state: 'text', value: 'Stale' });
      }

      await settle();

      // Assert
      expect(service.categoryGroups()).toEqual([
        expect.objectContaining({ name: { state: 'text', value: 'Fresh' } }),
      ]);
    });
  });

  // How a write ends, as a value the screen receives.
  //
  // **The pair that matters is the two conflicts.** They are the same status
  // and the same title with opposite remedies, and the service used to read
  // both as "the list was stale": a payee POST retried after a lost answer
  // spent the one re-read looking for a name that was never the problem, found
  // nothing, and abandoned a transaction. Every case below that flushes a 409
  // therefore names its kind, because a 409 with no kind is a third answer
  // again.
  describe('the word a write ends on', () => {
    async function heldPayees(...payees: PayeeDto[]): Promise<void> {
      service.loadPayees();
      http.expectOne(PAYEES_URL).flush({ items: payees });
      await settle();
    }

    it('answers recorded when the transaction lands', async () => {
      // Arrange
      const write = service.add(typed());

      await settle();

      // Act
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();

      // Assert — the one word that permits a form to be cleared.
      expect(await write).toEqual({ state: 'recorded' });
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    // **This case used to assert the opposite, and the rule under it moved.**
    // It was written when a payee id was minted per press: a conflict over a
    // freshly drawn id said nothing about any name, so spending the one re-read
    // on it abandoned a transaction over a counterparty that was not in the
    // way. The id is a **held draft** now — drawn against this very name and
    // kept across a refusal — so the only way the server holds it is that this
    // browser's own earlier create landed and lost its answer, and the row
    // wearing it is exactly what the re-read finds. Same one re-read, same
    // no-loop rule; the meaning of the kind inverted with the id's lifetime.
    it('adopts the payee its own retried create collided with', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await heldPayees();

      const write = service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_identifier' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      await settle();

      // Assert — the transaction is filed against the row that was already
      // there, rather than abandoned over it.
      const transaction = http.expectOne(TRANSACTIONS_URL);

      expect(
        (transaction.request.body as { payeeId: string | null }).payeeId,
      ).toBe(PAYEE_ID);
      transaction.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      expect(await write).toEqual({ state: 'recorded' });
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('abandons when the re-read after an identifier conflict finds nothing', async () => {
      // Arrange — the far side of the case above, and the reason the abandon
      // carries `duplicate-name` rather than the kind the server sent: nothing
      // was recorded, so *this entry is already saved* would be a false
      // sentence about the transaction. What is true is that a counterparty
      // wearing that id exists and this browser cannot match it, which is the
      // same next step — go to the list — as the name conflict's.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await heldPayees();

      const write = service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_identifier' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      // Assert — one re-read and no second create, whatever it found.
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
      expect(await write).toEqual({ state: 'duplicate-name' });
    });

    it('spends no re-read on a payee conflict carrying no kind at all', async () => {
      // Arrange — the third answer on that status: a browser running an older
      // bundle than the API, or a shape nobody anticipated. It is an answer
      // this screen cannot read rather than a guess, and guessing here is what
      // the case above costs.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await heldPayees();

      const write = service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush({ title: 'Conflict' }, { status: 409, statusText: 'Conflict' });
      await settle();

      // Assert
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
      expect(await write).toEqual({ state: 'unreadable' });
    });

    it('answers duplicate-name when the one re-read finds nothing to adopt', async () => {
      // Arrange — the severe exit, and the only place in the product that ends
      // on this word: the row holding that name is one this browser cannot
      // read, so it carries no index, can never match, and every write naming
      // that counterparty ends here.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await heldPayees();

      const write = service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      expect(await write).toEqual({ state: 'duplicate-name' });
    });

    it('says nothing on a conflict the re-read resolves', async () => {
      // Arrange — the other polarity, and the reason `duplicate-name` is not
      // simply "the payee create conflicted": the ordinary path adopts the row
      // and ends in a recorded transaction with no sentence anywhere.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await heldPayees();

      const write = service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Act
      http
        .expectOne(PAYEES_URL)
        .flush(
          { conflictKind: 'duplicate_name' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http
        .expectOne(PAYEES_URL)
        .flush({ items: [sealedPayee(PAYEE_ID, 'Corner Shop')] });
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();

      // Assert
      expect(await write).toEqual({ state: 'recorded' });
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('answers the server’s own sentences when the transaction is refused', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);

      const write = service.add(typed());

      await settle();

      // Act
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(withErrors(['Amount', ['Enter an amount.']]), {
          status: 400,
          statusText: 'Bad Request',
        });
      await settle();

      // Assert
      const outcome = await write;

      expect(outcome.state).toBe('invalid');
      expect(outcome.state === 'invalid' ? [...outcome.errors] : null).toEqual([
        ['Amount', ['Enter an amount.']],
      ]);
    });

    it('tells a server that failed from one that judged', async () => {
      // Arrange — a 500 is a minute's wait; a 403 is a judgement, and the same
      // press collects the same judgement.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);

      const first = service.add(typed());

      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      const second = service.add(typed());

      await settle();

      // Act
      http
        .expectOne(TRANSACTIONS_URL)
        .flush('nope', { status: 403, statusText: 'Forbidden' });
      await settle();

      // Assert
      expect(await first).toEqual({ state: 'unreachable' });
      expect(await second).toEqual({ state: 'unreadable' });
    });

    it('answers locked when the note cannot be sealed', async () => {
      // Arrange — nothing was sent, so there is no answer to classify, and the
      // next step is a factor rather than a retry or a reload.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      custody.sealAnswer = { state: 'locked' };

      // Act
      const outcome = await service.add(typed());

      await settle();

      // Assert
      http.expectNone(TRANSACTIONS_URL);
      expect(outcome).toEqual({ state: 'locked' });
    });

    it('answers locked when the counterparty cannot be keyed', async () => {
      // Arrange — the second of the exits that never reach a request, and it
      // travels out through the payee step rather than being decided by `add`.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      custody.indexAnswer = { state: 'locked' };

      // Act
      const outcome = await service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Assert
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);
      expect(outcome).toEqual({ state: 'locked' });
    });

    // **`unreachable` and deliberately not `locked`.** Every index this write
    // takes is keyed inside a budget, and a browser that has not been told which
    // one cannot resolve a counterparty — so it must not write a transaction
    // naming none either. No factor supplies a budget, so `locked`'s advice
    // cannot come true of this and would send somebody through a ceremony that
    // lands them back here; `unreachable`'s — the same press in a minute — can.
    it('answers unreachable and sends nothing when the budget is not known', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      session.setBudgetId(null);

      // Act
      const outcome = await service.add(typed({ payee: 'Corner Shop' }));

      await settle();

      // Assert
      expect(outcome).toEqual({ state: 'unreachable' });
      http.expectNone(PAYEES_URL);
      http.expectNone(TRANSACTIONS_URL);

      // And the note was not sealed on the way past. It is refused at the top of
      // the write rather than inside the counterparty step, so nothing is sealed
      // for a write that cannot be made — and the loading flag is put down
      // again, or the screen waits forever on a request nobody sent.
      expect(custody.sealCalls).toEqual([]);
      expect(custody.indexCalls).toEqual([]);
      expect(service.loading()).toBe(false);
    });
  });

  // The two identifiers this write carries, across presses.
  //
  // **`accounts.service.spec.ts` argues why a create's id has to survive a
  // refusal.** What is this write's own is that it mints **two**, and that the
  // payee's is the more urgent of the pair: a duplicate transaction is a row
  // somebody can delete, and a duplicate payee is a row on a table the app role
  // holds no `DELETE` on — permanent, and in every autocomplete from then on.
  describe('the identifiers this write carries', () => {
    function postedId(request: TestRequest): string {
      return (request.request.body as { id: string }).id;
    }

    it('keeps the transaction’s identifier across a refusal and redraws it once one lands', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      void service.add(typed());
      await settle();

      const refused = http.expectOne(TRANSACTIONS_URL);
      const drafted = postedId(refused);

      refused.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act
      void service.add(typed());
      await settle();

      const retried = http.expectOne(TRANSACTIONS_URL);

      // Assert — the same id, so a lost answer collides instead of recording
      // the entry twice.
      expect(postedId(retried)).toBe(drafted);
      retried.flush(sealedTransaction(), {
        status: 201,
        statusText: 'Created',
      });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();

      void service.add(typed());
      await settle();

      const next = http.expectOne(TRANSACTIONS_URL);

      expect(postedId(next)).not.toBe(drafted);
      expect(postedId(next)).toMatch(MINTED_ROW_ID);
      next.flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('keeps the counterparty’s identifier across a refusal', async () => {
      // Arrange — the payee create is a request of its own, so it has a lost
      // answer of its own. Per press, a retry writes a second row for one
      // counterparty on a table nothing can delete from.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.loadPayees();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      const refused = http.expectOne(PAYEES_URL);
      const drafted = postedId(refused);

      refused.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act
      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      // Assert
      const retried = http.expectOne(PAYEES_URL);

      expect(postedId(retried)).toBe(drafted);
      retried.flush(sealedPayee(drafted, 'Corner Shop'));
      await settle();
      http
        .expectOne(TRANSACTIONS_URL)
        .flush(sealedTransaction(), { status: 201, statusText: 'Created' });
      await settle();
      http.expectOne(TRANSACTIONS_URL).flush({ items: [] });
      await settle();
    });

    it('redraws the counterparty’s identifier when the name keys to something else', async () => {
      // Arrange — the half that keeps a held id from deadlocking. Reused under
      // a *different* counterparty, the draft names a row the server already
      // holds against other text: the create 409s on the identifier, the
      // re-read cannot match the new name, and every press after that ends the
      // same way. Keyed on the **blind index** rather than on the typed string,
      // so a change of case is the same counterparty and a different name is
      // not.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.loadPayees();
      http.expectOne(PAYEES_URL).flush({ items: [] });
      await settle();

      void service.add(typed({ payee: 'Corner Shop' }));
      await settle();

      const refused = http.expectOne(PAYEES_URL);
      const drafted = postedId(refused);

      refused.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act — the same counterparty in a different case, then a different one.
      void service.add(typed({ payee: 'CORNER SHOP' }));
      await settle();

      const folded = http.expectOne(PAYEES_URL);

      expect(postedId(folded)).toBe(drafted);
      folded.flush('', { status: 500, statusText: 'Server Error' });
      await settle();
      void service.add(typed({ payee: 'Bakery' }));
      await settle();

      // Assert
      const other = http.expectOne(PAYEES_URL);

      expect(postedId(other)).not.toBe(drafted);
      other.flush('', { status: 500, statusText: 'Server Error' });
      await settle();
    });
  });
});
