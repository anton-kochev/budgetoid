// The accounts screen's service, driven through a hand-written custody stub and
// a real `AccountApiService` over `HttpTestingController`.
//
// **The API is real and only custody is stubbed, and both halves of that are
// deliberate.** What this phase is most likely to get wrong is what goes *on
// the wire* — an id that was minted twice, a name sealed against something
// other than the identifier beside it, a member the server no longer binds — so
// the assertions are over `TestRequest.request.body` rather than over a spy's
// arguments. A stubbed API service would let every one of those defects
// through. Custody, on the other hand, cannot be real here: opening anything
// needs an account's content key, which needs a factor, which needs an
// authenticator this runner does not have.
//
// **The stub's answers encode what they were asked**, so an assertion can say
// *this name was sealed against that identifier* in one comparison instead of
// cross-referencing two recordings. `sealField` answers `sealed(<rowId>|<text>)`
// and `blindIndex` answers `index(accounts.name|<text>)`; `openField` reads the
// first back. A service that minted a second id for an update, or trimmed what
// it sealed, produces a wire value that does not match and the test says which.
//
// **Every one of the three operations reads a `#` field of the stub, and that
// is the instrument for the one trap in this wiring.** The mapper is handed
// `openField` as a capability, and handing it over as the bare method reference
// `custody.openField` type-checks perfectly — `#` privates are invisible to the
// type system's `this` — and answers every call with a `TypeError` on the wrong
// receiver. `@typescript-eslint/unbound-method` is off for specs, so nothing
// but a call finds it. The `#openCalls` push below is that call: every load
// test in this file reddens if the arrow is ever flattened into a reference.
//
// **`implements Pick<AccountKeyCustodyService, keyof AccountKeyCustodyService>`**
// is the compiler's own census of what the real service publishes, so a member
// the stub forgot is an error here rather than a `is not a function` during a
// test somewhere else.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AccountDto } from '@app-core/api/account-api.service';
import type { BlindIndexedField } from '@app-core/security/blind-index';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
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
import { AccountsService } from './accounts.service';

const API_ORIGIN = 'https://api.test';
const ACCOUNTS_URL = `${API_ORIGIN}/api/accounts`;

// A canonical lower-case hyphenated UUID — the spelling `System.Text.Json`
// renders every `Guid` in, so this is what a read really hands back and what an
// update has to re-seal against.
const EXISTING_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e';

// The canonical spelling *and* the version nibble, because the two are
// different claims. `crypto.randomUUID` satisfies the first and mints version 4
// — the one thing `mintNarrativeRowId` exists not to do — so a create that
// reached for the shortcut passes a spelling check and fails this one.
const MINTED_ROW_ID =
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function sealedWire(rowId: string, plaintext: string): string {
  return `sealed(${rowId}|${plaintext})`;
}

function indexValue(plaintext: string): string {
  return `index(accounts.name|${plaintext})`;
}

function sealedAccount(id: string, name: string): AccountDto {
  return {
    id,
    name: sealedWire(id, name),
    type: 'Checking',
    openingBalance: 0,
    createdAtUtc: '2026-01-02T03:04:05Z',
    currencyCode: 'USD',
    currencyName: 'US Dollar',
    currencySymbol: '$',
    currencyMinorUnit: 2,
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
  /** What `blindIndex` answers with, when it is not the encoded value. */
  public indexAnswer: BlindIndexValue | null = null;
  /** How a wire value is read back. Overridable, so a test can defer or lock. */
  public openWith: (
    binding: NarrativeFieldBinding,
    wire: string,
  ) => NarrativeText | Promise<NarrativeText> = (binding, wire) => {
    const match = /^sealed\((.+)\|(.*)\)$/.exec(wire);

    return match !== null && match[1] === binding.rowId
      ? { state: 'text', value: match[2] ?? '' }
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
      this.sealAnswer ?? {
        state: 'sealed',
        wire: sealedWire(binding.rowId, plaintext),
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
      this.indexAnswer ?? { state: 'computed', value: indexValue(plaintext) },
    );
  }

  public unlock(): void {
    throw new Error('the accounts service may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the accounts service may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the accounts service may not lock the account');
  }
}

// Drains the microtask queue the AEAD opens run in. A macrotask boundary is
// what guarantees it: `Promise.all` over N opens settles several ticks deep, and
// counting ticks is how a test becomes flaky.
function settle(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

describe('AccountsService', () => {
  let service: AccountsService;
  let custody: CustodyStub;
  let http: HttpTestingController;

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
        AccountsService,
      ],
    });
    service = TestBed.inject(AccountsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('seals the name against the identifier it posts', async () => {
    // Arrange — nothing to arrange: the stub seals to a value that names the
    // binding it was given, so one comparison says whether the two agree.

    // Act
    void service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();

    // Assert
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { id: string; name: string };

    expect(body.id).toMatch(MINTED_ROW_ID);
    expect(body.name).toBe(sealedWire(body.id, 'Everyday'));
    request.flush(sealedAccount(body.id, 'Everyday'));
    await settle();
  });

  it('computes the blind index over the same text it sealed', async () => {
    // Arrange

    // Act
    void service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();

    // Assert
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { nameKey: string };

    expect(custody.indexCalls).toEqual([
      { field: { table: 'accounts', column: 'name' }, plaintext: 'Everyday' },
    ]);
    expect(body.nameKey).toBe(indexValue('Everyday'));
    request.flush(sealedAccount(EXISTING_ID, 'Everyday'));
    await settle();
  });

  it('posts id, name and nameKey on a create and nothing else', async () => {
    // Arrange

    // Act
    void service.add({
      name: 'Everyday',
      type: 'Savings',
      openingBalance: 12.5,
      currencyCode: 'EUR',
    });
    await settle();

    // Assert
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { id: string };

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      currencyCode: 'EUR',
      id: body.id,
      name: sealedWire(body.id, 'Everyday'),
      nameKey: indexValue('Everyday'),
      openingBalance: 12.5,
      type: 'Savings',
    });
    request.flush(sealedAccount(body.id, 'Everyday'));
    await settle();
  });

  it('puts the created account into the list without a reload', async () => {
    // Arrange — a list that already holds an answer, so an append is
    // distinguishable from a list of one fabricated out of the create. Nothing
    // held this path: every other create case asserts the request body and
    // then flushes, so deleting the whole `switchMap`/`toAccountView`/
    // `subscribe` chain and leaving a bare POST passed the file — and that
    // chain is the only place a created row enters the list, because this
    // route is not followed by a re-read.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Rainy day')] });
    await settle();

    // Act
    void service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { id: string };

    request.flush(sealedAccount(body.id, 'Everyday'));
    await settle();

    // Assert — three claims at once, and each is a different way to lose the
    // row: it is *there*; it is a **word** rather than the wire value, so it
    // went through the mapper instead of being assembled from what was typed;
    // and it sits in `compareNarrative` order rather than at the end, which is
    // what this screen's client-side sort is for.
    expect(service.accounts()?.map((view) => view.name)).toEqual([
      { state: 'text', value: 'Everyday' },
      { state: 'text', value: 'Rainy day' },
    ]);
    expect(service.loading()).toBe(false);
  });

  it('invents no list from a create when no read has answered', async () => {
    // Arrange — `null` is "no answer yet", and appending to it would leave a
    // screen offering one row as though it held the set, over a read that
    // never landed.

    // Act
    void service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { id: string };

    request.flush(sealedAccount(body.id, 'Everyday'));
    await settle();

    // Assert
    expect(service.accounts()).toBeNull();
  });

  it('posts nothing when sealing answers locked', async () => {
    // Arrange — reachable when the account's content key was replaced while the
    // cipher ran.
    custody.sealAnswer = { state: 'locked' };

    // Act
    await service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();

    // Assert
    http.expectNone(ACCOUNTS_URL);
  });

  it('posts nothing when the index answers locked after the seal succeeded', async () => {
    // Arrange — the pair that diverges: a seal compares key identity and keeps
    // its answer through a plain `lock()`, an index compares the generation
    // counter and drops it. So `sealed` beside `locked` is reachable, and
    // posting then writes half a name pair through the one door the server
    // cannot see — it holds no index key, so it can never notice that the
    // column and its index disagree.
    custody.indexAnswer = { state: 'locked' };

    // Act
    await service.add({
      name: 'Everyday',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();

    // Assert
    http.expectNone(ACCOUNTS_URL);
  });

  it('re-seals an update under the existing row id and mints none', async () => {
    // Arrange — the id is the associated data the envelope was sealed against.
    // A freshly minted one here compiles, posts, and leaves a name that never
    // opens again, with nothing anywhere naming the cause.

    // Act
    void service.update(EXISTING_ID, {
      name: 'Renamed',
      type: 'Savings',
      openingBalance: 10,
    });
    await settle();

    // Assert
    const request = http.expectOne(`${ACCOUNTS_URL}/${EXISTING_ID}`);

    expect(request.request.method).toBe('PUT');
    expect(custody.sealCalls).toEqual([
      {
        binding: { table: 'accounts', column: 'name', rowId: EXISTING_ID },
        plaintext: 'Renamed',
      },
    ]);
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  it('sends exactly the declared members on an update', async () => {
    // Arrange

    // Act
    void service.update(EXISTING_ID, {
      name: 'Renamed',
      type: 'Savings',
      openingBalance: 10,
    });
    await settle();

    // Assert — `toEqual` is exact over own members, so an `id` or a
    // `currencyCode` smuggled in here reddens. The route binds four members and
    // the shape refuses what it was not asked for.
    const request = http.expectOne(`${ACCOUNTS_URL}/${EXISTING_ID}`);

    expect(request.request.body).toEqual({
      name: sealedWire(EXISTING_ID, 'Renamed'),
      nameKey: indexValue('Renamed'),
      openingBalance: 10,
      type: 'Savings',
    });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  it('patches the renamed account in the list', async () => {
    // Arrange — the route answers 204, so nothing comes back to map. A service
    // that left the list alone here shows the old name until the next load,
    // and the next load is a page somebody has to think to reach.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();

    // Act
    void service.update(EXISTING_ID, {
      name: 'Renamed',
      type: 'Savings',
      openingBalance: 10,
    });
    await settle();
    http
      .expectOne(`${ACCOUNTS_URL}/${EXISTING_ID}`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    // Assert — the text this browser just sealed, which is what it would read
    // back, beside the two members the route also took.
    expect(service.accounts()).toEqual([
      expect.objectContaining({
        id: EXISTING_ID,
        name: { state: 'text', value: 'Renamed' },
        openingBalance: 10,
        type: 'Savings',
      }),
    ]);
  });

  it('removes the account from the list once the delete is accepted', async () => {
    // Arrange
    service.load();
    http.expectOne(ACCOUNTS_URL).flush({
      items: [
        sealedAccount(EXISTING_ID, 'Everyday'),
        sealedAccount('0199c3d4-0000-7000-8000-00000000000e', 'Rainy day'),
      ],
    });
    await settle();

    // Act
    service.remove(EXISTING_ID);
    const request = http.expectOne(`${ACCOUNTS_URL}/${EXISTING_ID}`);

    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    // Assert
    expect(request.request.method).toBe('DELETE');
    expect(service.accounts()?.map((view) => view.name)).toEqual([
      { state: 'text', value: 'Rainy day' },
    ]);
    expect(service.loading()).toBe(false);
  });

  it('lets a misuse rejection during a load reach the failure branch', async () => {
    // Arrange — `NarrativeFieldMisuseError` is the codec's word for a refusal
    // it made about the *call*, before any cipher ran. It is a defect in this
    // client and says nothing whatever about the rows, so it has to travel to
    // `catchError` and take the whole load down with it.
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    custody.openWith = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));
    service.load();

    // Act
    http.expectOne(ACCOUNTS_URL).flush({
      items: [
        sealedAccount(EXISTING_ID, 'Everyday'),
        sealedAccount('0199c3d4-0000-7000-8000-00000000000e', 'Rainy day'),
      ],
    });
    await settle();

    // Assert — the whole read failed, so the list has no answer at all.
    // `Promise.allSettled` here would file the defect as a per-row result and
    // publish a list — two rows short, or two rows of markers — over a client
    // bug nobody would ever see.
    expect(service.accounts()).toBeNull();
    expect(service.loading()).toBe(false);
  });

  it('sends the typed name untrimmed', async () => {
    // Arrange — the client is forbidden from altering what it seals. The
    // non-blank rule moved to a form validator; a `.trim()` here would seal one
    // text and index another the moment anything else stopped trimming.

    // Act
    void service.add({
      name: '  Everyday  ',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
    await settle();

    // Assert
    const request = http.expectOne(ACCOUNTS_URL);
    const body = request.request.body as { id: string; name: string };

    expect(body.name).toBe(sealedWire(body.id, '  Everyday  '));
    expect(custody.indexCalls[0]?.plaintext).toBe('  Everyday  ');
    request.flush(sealedAccount(body.id, '  Everyday  '));
    await settle();
  });

  it('publishes views and never DTOs', async () => {
    // Arrange
    service.load();

    // Act
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();

    // Assert — the name is a word, never the wire value and never a string.
    expect(service.accounts()).toEqual([
      {
        createdAtUtc: '2026-01-02T03:04:05Z',
        currencyCode: 'USD',
        currencyMinorUnit: 2,
        currencyName: 'US Dollar',
        currencySymbol: '$',
        id: EXISTING_ID,
        name: { state: 'text', value: 'Everyday' },
        openingBalance: 0,
        type: 'Checking',
      },
    ]);
  });

  it('clears the list to null when a load starts', async () => {
    // Arrange — a list that already holds an answer.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    expect(service.accounts()).not.toBeNull();

    // Act
    service.load();

    // Assert — `null`, never `[]`: an empty array is the sentence *you have no
    // accounts*, which is a claim only a server that answered may make.
    expect(service.accounts()).toBeNull();
    http.expectOne(ACCOUNTS_URL).flush({ items: [] });
    await settle();
  });

  it('keeps only the newest load’s answer when two overlap', async () => {
    // Arrange — the first load's opens never settle until this test says so.
    let releaseFirst: (value: NarrativeText) => void = () => undefined;

    custody.openWith = () =>
      new Promise<NarrativeText>((resolve) => {
        releaseFirst = resolve;
      });
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Stale')] });
    await settle();

    // Act — a second load overtakes it. Decryption widens the overlap from one
    // round trip to one round trip plus N AEAD opens, so this is reachable in a
    // browser and not only in a test.
    custody.openWith = (binding, wire) => ({
      state: 'text',
      value: /\|(.*)\)$/.exec(wire)?.[1] ?? binding.rowId,
    });
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Fresh')] });
    await settle();
    releaseFirst({ state: 'text', value: 'Stale' });
    await settle();

    // Assert — the slow first load may not revert the list behind the fast one.
    expect(service.accounts()).toEqual([
      expect.objectContaining({ name: { state: 'text', value: 'Fresh' } }),
    ]);
  });

  it('resets loading and leaves the list null when a load fails', async () => {
    // Arrange
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    service.load();

    // Act
    http
      .expectOne(ACCOUNTS_URL)
      .flush('nope', { status: 500, statusText: 'Server Error' });
    await settle();

    // Assert — the previous answer is not restored and no empty list is
    // invented; the section has nothing to say and says so.
    expect(service.loading()).toBe(false);
    expect(service.accounts()).toBeNull();
  });

  it('drops the opened names when the account locks', async () => {
    // Arrange — the list holds plaintext this browser opened under a key it no
    // longer has. This service is `providedIn: 'root'`, so nothing destroys it
    // when a screen goes away and nothing clears it when a session ends: sign
    // out on `/app/accounts` and the previous account's names are still
    // readable from the root injector for the life of the tab.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    expect(service.accounts()).not.toBeNull();

    // Act — what `SessionService.ended()` does through `custody.lock()`, and
    // what a failed unlock does through `#fail`. The status is the only thing
    // custody publishes about it, so the status is what this service reads.
    custody.setStatus('locked');
    TestBed.tick();

    // Assert — `null`, the same word a load that never answered leaves behind:
    // there is no answer to show, and `[]` would be the sentence *you have no
    // accounts*.
    expect(service.accounts()).toBeNull();
  });

  it('withdraws a failed read’s word when the account locks', async () => {
    // Arrange — a read that genuinely failed, so the word is a claim about
    // something that really happened rather than a flag nobody set.
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush('nope', { status: 500, statusText: 'Server Error' });
    await settle();
    expect(service.failed()).toBe(true);

    // Act
    custody.setStatus('locked');
    TestBed.tick();

    // Assert — the list is `null` because this service emptied it, not because
    // a request failed, so there is no read left for the word to be a claim
    // about. Left standing it advises somebody to check their connection over
    // a list nothing asked the server for — and the two situations are the
    // same `null` from outside, so nothing else can tell them apart. Each of
    // the three services carries this case because each owns its own effect,
    // and until they did the clear was deletable with a green suite — which is
    // how the transactions service came to be shipped without one.
    expect(service.failed()).toBe(false);
    expect(service.accounts()).toBeNull();
  });

  it('keeps the list while an unlock is running', async () => {
    // Arrange — the control for the case above, and the reason the predicate is
    // `locked` exactly rather than "anything but unlocked". `unlocking` is a
    // state whose resolution *restores* the keys, and the screen deliberately
    // keeps the list up through a ceremony — clearing here empties a list
    // somebody is looking at and puts nothing in its place, on a screen whose
    // three other branches all say something untrue about it.
    //
    // Custody has already dropped both keys by this point, so a read *started*
    // now publishes `locked` words rather than text; what survives is a list
    // opened before the ceremony began, for as long as it runs.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();

    // Act
    custody.setStatus('unlocking');
    TestBed.tick();

    // Assert
    expect(service.accounts()).toEqual([
      expect.objectContaining({ name: { state: 'text', value: 'Everyday' } }),
    ]);
  });

  it('reads the list again when the account is unlocked', async () => {
    // Arrange — a list that was opened and then emptied by a lock. What that
    // leaves is the hole the fourth state was added to remove, one step along:
    // `null` with nothing loading and nothing failed renders neither the list,
    // nor a sentence, nor the notice. Nothing today reaches it — the only
    // caller of `lock()` navigates to `/welcome` and destroys the screen — so
    // this is the state being closed while it is still cheap.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    custody.setStatus('locked');
    TestBed.tick();
    expect(service.accounts()).toBeNull();

    // Act — the keys come back with the screen still mounted, so no `ngOnInit`
    // runs to ask for the list a second time.
    custody.setStatus('unlocked');
    TestBed.tick();

    // Assert — the read is in flight before anything is flushed, which is the
    // claim: the service asked, rather than a later screen asking for it.
    const request = http.expectOne(ACCOUNTS_URL);

    expect(service.loading()).toBe(true);
    request.flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    expect(service.accounts()).toEqual([
      expect.objectContaining({ name: { state: 'text', value: 'Everyday' } }),
    ]);
  });

  it('reads the list again when the ceremony itself was seen running', async () => {
    // Arrange — the same close, over the path an effect usually sees. Effects
    // are glitch-free rather than replayed, so whether `unlocking` is observed
    // between the two ends depends on when the flush lands: a reaction keyed on
    // "the previous word was `locked`" restores the list in the case above and
    // leaves it empty here, and the difference is a scheduling detail no screen
    // can control.
    service.load();
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    custody.setStatus('locked');
    TestBed.tick();
    custody.setStatus('unlocking');
    TestBed.tick();

    // Act
    custody.setStatus('unlocked');
    TestBed.tick();

    // Assert
    http
      .expectOne(ACCOUNTS_URL)
      .flush({ items: [sealedAccount(EXISTING_ID, 'Everyday')] });
    await settle();
    expect(service.accounts()).not.toBeNull();
  });

  it('asks for nothing when the first status it sees is unlocked', () => {
    // Arrange — the control that makes the reaction a *transition* rather than
    // a value. This service is `providedIn: 'root'` and is built on first
    // injection, which on an open account is a status of `unlocked` from the
    // first run of the effect. A reaction reading the value alone fires a read
    // here that nobody asked for — before any screen has mounted, and again
    // for every service the injector happens to build.

    // Act
    TestBed.tick();

    // Assert
    http.expectNone(ACCOUNTS_URL);
    expect(service.loading()).toBe(false);
  });

  it('orders the list through compareNarrative', async () => {
    // Arrange — a mixed list is reachable: custody can move between the first
    // row's open and the last one's.
    custody.openWith = (binding, wire) => {
      // The binding is not read here; the wire alone decides the word.
      void binding;

      if (wire.includes('locked')) {
        return { state: 'locked' };
      }

      if (wire.includes('damaged')) {
        return { state: 'unreadable' };
      }

      return { state: 'text', value: /\|(.*)\)$/.exec(wire)?.[1] ?? '' };
    };
    service.load();

    // Act
    http.expectOne(ACCOUNTS_URL).flush({
      items: [
        sealedAccount('0199c3d4-0000-7000-8000-00000000000a', 'locked'),
        sealedAccount('0199c3d4-0000-7000-8000-00000000000b', 'Zulu'),
        sealedAccount('0199c3d4-0000-7000-8000-00000000000c', 'damaged'),
        sealedAccount('0199c3d4-0000-7000-8000-00000000000d', 'Alpha'),
      ],
    });
    await settle();

    // Assert — text before unreadable before locked, two texts by
    // `localeCompare`. A comparator reaching for `a.name.localeCompare` does not
    // compile against the union, and one reaching for `a.name.value` throws on
    // the two rows that have none.
    expect(service.accounts()?.map((view) => view.name)).toEqual([
      { state: 'text', value: 'Alpha' },
      { state: 'text', value: 'Zulu' },
      { state: 'unreadable' },
      { state: 'locked' },
    ]);
  });
});
