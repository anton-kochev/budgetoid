// The categories screen's service, driven through a hand-written custody stub
// and real API services over `HttpTestingController`.
//
// **The API services are real and only custody is stubbed**, the arrangement
// `accounts.service.spec.ts` argues: what this phase is most likely to get
// wrong is what goes *on the wire* — an id minted twice, a name sealed against
// something other than the identifier beside it, a note posted as an empty
// envelope where the route wants `null`, a member the shapes now refuse — so
// the assertions read `TestRequest.request.body`. A stubbed API service would
// let every one of those through. Custody cannot be real here: opening anything
// needs an account's content key, which needs a factor, which needs an
// authenticator this runner does not have.
//
// **The stub's answers encode what they were asked, down to the column.**
// `sealField` answers `sealed(<table>.<column>|<rowId>|<text>)` and `openField`
// reads that back, refusing anything whose table, column or row id disagrees.
// Both tables here carry **two** narrative columns bound to one row id, so the
// column is the only thing telling a name's envelope from a note's — a stub
// keyed on the table alone would let a mapper open a note under the name's
// binding and call it correct.
//
// **Every stubbed operation reads a `#` field**, which is the instrument for
// the trap in this wiring: `openField` is handed to the mappers as a
// capability, and handing it over as the bare method reference
// `custody.openField` type-checks perfectly — `#` privates are invisible to the
// type system's `this` — and answers every call with a `TypeError` on the wrong
// receiver. `@typescript-eslint/unbound-method` is off for specs, so nothing
// but a call finds it.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CategoryDto } from '@app-core/api/categories-api.service';
import type { CategoryGroupDto } from '@app-core/api/category-groups-api.service';
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
import { CategoriesService } from './categories.service';

const API_ORIGIN = 'https://api.test';
const GROUPS_URL = `${API_ORIGIN}/api/category-groups`;
const CATEGORIES_URL = `${API_ORIGIN}/api/categories`;

// Canonical lower-case hyphenated UUIDs — the spelling `System.Text.Json`
// renders every `Guid` in, so these are what a read really hands back and what
// an update has to re-seal against.
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const OTHER_GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';
const OTHER_CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000004';

// The canonical spelling *and* the version nibble, because the two are
// different claims. `crypto.randomUUID` satisfies the first and mints version 4
// — the one thing `mintNarrativeRowId` exists not to do — so a create that
// reached for the shortcut passes a spelling check and fails this one.
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
// shipped fold table — this is a spec double — but enough that an index taken
// over one spelling and an index taken over another agree where the real one
// would.
function indexValue(table: string, column: string, plaintext: string): string {
  return `index(${table}.${column}|${plaintext.trim().toLowerCase()})`;
}

function sealedGroup(
  id: string,
  name: string,
  description: string | null,
  position: number,
): CategoryGroupDto {
  return {
    id,
    name: sealedWire('category_groups', 'name', id, name),
    description:
      description === null
        ? null
        : sealedWire('category_groups', 'description', id, description),
    position,
  };
}

function sealedCategory(
  id: string,
  name: string,
  description: string | null,
  group: CategoryGroupDto,
  position: number,
): CategoryDto {
  return {
    id,
    name: sealedWire('categories', 'name', id, name),
    description:
      description === null
        ? null
        : sealedWire('categories', 'description', id, description),
    categoryGroupId: group.id,

    // Sealed under the **group's** id, which is what makes "opened under the
    // category's id" a red bar here rather than a value that happens to look
    // right.
    categoryGroupName: group.name,
    position,
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
   * What `sealField` answers for one **column**, overriding {@link sealAnswer}.
   *
   * Both tables here seal a name and a note against one row id, so a test that
   * wants to lock exactly one of the two has nothing but the column to say so
   * with — and "the name sealed and the note did not" is a reachable pair worth
   * asking about on its own.
   */
  public readonly sealAnswersByColumn = new Map<string, SealedField>();
  /** What `blindIndex` answers with, when it is not the encoded value. */
  public indexAnswer: BlindIndexValue | null = null;
  /** How a wire value is read back. Overridable, so a test can defer or lock. */
  public openWith: (
    binding: NarrativeFieldBinding,
    wire: string,
  ) => NarrativeText | Promise<NarrativeText> = (binding, wire) => {
    const match = /^sealed\((.+?)\.(.+?)\|(.+?)\|(.*)\)$/.exec(wire);

    // Table, column *and* row id, all three: a value opened under another
    // row's binding — or under this row's other column — is exactly what fails
    // to authenticate in a browser, and it has to fail here for the same
    // reason.
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
      this.sealAnswersByColumn.get(binding.column) ??
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
    throw new Error('the categories service may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the categories service may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the categories service may not lock the account');
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
// what guarantees it: `Promise.all` over N opens settles several ticks deep,
// and counting ticks is how a test becomes flaky.
function settle(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

describe('CategoriesService', () => {
  let service: CategoriesService;
  let custody: CustodyStub;
  let http: HttpTestingController;

  const essentials = sealedGroup(GROUP_ID, 'Essentials', 'The bills', 0);
  const lifestyle = sealedGroup(OTHER_GROUP_ID, 'Lifestyle', null, 1);
  const groceries = sealedCategory(
    CATEGORY_ID,
    'Groceries',
    'Food and drink',
    essentials,
    0,
  );

  // One load, answered with whatever the case wants on it. Both requests are in
  // flight together — the read is a `forkJoin` — so both are flushed here.
  async function loadWith(
    groups: readonly CategoryGroupDto[],
    categories: readonly CategoryDto[],
  ): Promise<void> {
    service.load();
    http.expectOne(GROUPS_URL).flush({ items: groups });
    http.expectOne(CATEGORIES_URL).flush({ items: categories });
    await settle();
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
        CategoriesService,
      ],
    });
    service = TestBed.inject(CategoriesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  describe('reading the hierarchy', () => {
    it('publishes views and never DTOs', async () => {
      // Act
      await loadWith([essentials], [groceries]);

      // Assert — every narrative member is a word, never the wire value and
      // never a string.
      expect(service.groups()).toEqual([
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
          description: { state: 'text', value: 'Food and drink' },
          id: CATEGORY_ID,
          name: { state: 'text', value: 'Groceries' },
          position: 0,
        },
      ]);
      expect(service.loading()).toBe(false);
    });

    it('publishes both lists in the order the API sent them', async () => {
      // Arrange — the server owns this order: both rows carry a `position`
      // column and the routes read by it. A client-side sort here would be a
      // second opinion about a fact the API already settled, and on a locked
      // account it would have nothing to sort by at all.
      //
      // **Both lists, and two categories.** The name said both and the
      // assertion read the groups alone; the fixture held one category, and a
      // one-element list cannot show an ordering defect whatever it is
      // compared against. The two below arrive in the reverse of the order
      // this file's own `sortCategories` would put them in — it orders by the
      // group's position first, and `essentials` is position 0 — so a re-sort
      // applied on the way out reddens here.
      const dining = sealedCategory(
        OTHER_CATEGORY_ID,
        'Dining out',
        null,
        lifestyle,
        0,
      );

      // Act
      await loadWith([lifestyle, essentials], [dining, groceries]);

      // Assert
      expect(service.groups()?.map((view) => view.id)).toEqual([
        OTHER_GROUP_ID,
        GROUP_ID,
      ]);
      expect(service.categories()?.map((view) => view.id)).toEqual([
        OTHER_CATEGORY_ID,
        CATEGORY_ID,
      ]);
    });

    it('answers null for a note nobody wrote and never an empty string', async () => {
      // Arrange — `lifestyle` holds no description at all.

      // Act
      await loadWith([lifestyle], []);

      // Assert
      expect(service.groups()?.at(0)?.description).toBeNull();
    });

    it('clears both lists to null when a load starts', async () => {
      // Arrange — lists that already hold an answer.
      await loadWith([essentials], [groceries]);
      expect(service.groups()).not.toBeNull();

      // Act
      service.load();

      // Assert — `null`, never `[]`: an empty array is the sentence *you have
      // no categories*, which is a claim only a server that answered may make.
      // The load just started is answered here rather than by a second
      // `loadWith`, which would leave this one in flight and `http.verify()`
      // would report it against whichever case ran next.
      expect(service.groups()).toBeNull();
      expect(service.categories()).toBeNull();
      http.expectOne(GROUPS_URL).flush({ items: [] });
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      await settle();
    });

    it('resets loading and leaves both lists null when a load fails', async () => {
      // Arrange
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.load();

      // Act — the good half is answered first and the bad half second, because
      // `forkJoin` cancels its siblings the moment one errors and a cancelled
      // request cannot be flushed.
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      http
        .expectOne(GROUPS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      // Assert — the previous answer is not restored and no empty list is
      // invented; the screen has nothing to say and says so.
      expect(service.loading()).toBe(false);
      expect(service.groups()).toBeNull();
      expect(service.categories()).toBeNull();
    });

    it('lets a misuse rejection during a load reach the failure branch', async () => {
      // Arrange — `NarrativeFieldMisuseError` is the codec's word for a refusal
      // it made about the *call*, before any cipher ran. It is a defect in this
      // client and says nothing whatever about the rows, so it has to travel to
      // `catchError` and take the whole load down with it.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      custody.openWith = () =>
        Promise.reject(new NarrativeFieldMisuseError('refused'));

      // Act
      await loadWith([essentials, lifestyle], [groceries]);

      // Assert — the whole read failed, so neither list has an answer at all.
      // `Promise.allSettled` here would file the defect as a per-member result
      // and publish two lists — short, or full of markers — over a client bug
      // nobody would ever see.
      expect(service.groups()).toBeNull();
      expect(service.categories()).toBeNull();
      expect(service.loading()).toBe(false);
    });

    it('drops both opened lists when the account locks', async () => {
      // Arrange — the lists hold plaintext this browser opened under a key it
      // no longer has. This service is `providedIn: 'root'`, so nothing
      // destroys it when a screen goes away and nothing clears it when a
      // session ends: sign out on `/app/categories` and the previous account's
      // names and notes are still readable from the root injector for the life
      // of the tab.
      await loadWith([essentials], [groceries]);
      expect(service.groups()).not.toBeNull();
      expect(service.categories()).not.toBeNull();

      // Act — what `SessionService.ended()` does through `custody.lock()`, and
      // what a failed unlock does through `#fail`.
      custody.setStatus('locked');
      TestBed.tick();

      // Assert — **both**, because they are published together and a category
      // carries its group's name: one list dropped and the other kept is a
      // half-cleared screen holding the very words this is here to destroy.
      expect(service.groups()).toBeNull();
      expect(service.categories()).toBeNull();
    });

    it('withdraws a failed read’s word when the account locks', async () => {
      // Arrange — a read that genuinely failed, so the word is a claim about
      // something that really happened. `accounts.service.ts` argues once why
      // a lock has to withdraw it.
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      service.load();
      http.expectOne(CATEGORIES_URL).flush({ items: [] });
      http
        .expectOne(GROUPS_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();
      expect(service.failed()).toBe(true);

      // Act
      custody.setStatus('locked');
      TestBed.tick();

      // Assert — the lists are `null` because this service emptied them, not
      // because a request failed, so there is no read left for the word to be
      // a claim about.
      expect(service.failed()).toBe(false);
      expect(service.groups()).toBeNull();
    });

    it('keeps both lists while an unlock is running', async () => {
      // Arrange — the control for the case above, and the reason the predicate
      // is `locked` exactly rather than "anything but unlocked":
      // `accounts.service.spec.ts` argues it once for all three services.
      await loadWith([essentials], [groceries]);

      // Act
      custody.setStatus('unlocking');
      TestBed.tick();

      // Assert
      expect(service.groups()).not.toBeNull();
      expect(service.categories()).not.toBeNull();
    });

    it('reads both lists again when the account is unlocked', async () => {
      // Arrange — two lists that were opened and then emptied by a lock. What
      // that leaves is the hole the fourth state was added to remove, one step
      // along: `null` with nothing loading and nothing failed renders neither
      // the hierarchy, nor a sentence, nor the notice.
      await loadWith([essentials], [groceries]);
      custody.setStatus('locked');
      TestBed.tick();
      expect(service.groups()).toBeNull();

      // Act — the keys come back with the screen still mounted, so no
      // `ngOnInit` runs to ask for the hierarchy a second time.
      custody.setStatus('unlocked');
      TestBed.tick();

      // Assert — **both** requests, because the pair is read, published and
      // cleared together: half a re-read is a list of categories filed under
      // groups this screen has not got.
      expect(service.loading()).toBe(true);
      http.expectOne(GROUPS_URL).flush({ items: [essentials] });
      http.expectOne(CATEGORIES_URL).flush({ items: [groceries] });
      await settle();
      expect(service.groups()).not.toBeNull();
      expect(service.categories()).not.toBeNull();
    });

    it('reads both lists again when the ceremony itself was seen running', async () => {
      // Arrange — the same close, over the path an effect usually sees.
      // `accounts.service.ts` argues why the near side is every word but
      // `unlocked` rather than `locked` alone.
      await loadWith([essentials], [groceries]);
      custody.setStatus('locked');
      TestBed.tick();
      custody.setStatus('unlocking');
      TestBed.tick();

      // Act
      custody.setStatus('unlocked');
      TestBed.tick();

      // Assert
      http.expectOne(GROUPS_URL).flush({ items: [essentials] });
      http.expectOne(CATEGORIES_URL).flush({ items: [groceries] });
      await settle();
      expect(service.groups()).not.toBeNull();
      expect(service.categories()).not.toBeNull();
    });

    it('asks for nothing when the first status it sees is unlocked', () => {
      // Arrange — the control that makes the reaction a *transition* rather
      // than a value: this service is `providedIn: 'root'` and is built on
      // first injection, which on an open account is `unlocked` from the first
      // run of the effect.

      // Act
      TestBed.tick();

      // Assert
      http.expectNone(GROUPS_URL);
      http.expectNone(CATEGORIES_URL);
      expect(service.loading()).toBe(false);
    });

    it('keeps only the newest load’s answer when two overlap', async () => {
      // Arrange — the first load's opens never settle until this case says so.
      // **Every** resolver is collected, and that is the whole instrument: a
      // group row makes two opens and a category row three, so a single
      // `let releaseFirst` would hold only the last of five and leave four
      // promises unsettled — `Promise.all` would never settle, the first inner
      // observable would never emit, and the case would pass under `mergeMap`
      // exactly as happily as under `switchMap`.
      const releases: ((value: NarrativeText) => void)[] = [];

      custody.openWith = () =>
        new Promise<NarrativeText>((resolve) => {
          releases.push(resolve);
        });
      service.load();
      http
        .expectOne(GROUPS_URL)
        .flush({ items: [sealedGroup(GROUP_ID, 'Stale', 'Stale note', 0)] });
      http.expectOne(CATEGORIES_URL).flush({
        items: [
          sealedCategory(CATEGORY_ID, 'Stale', 'Stale note', essentials, 0),
        ],
      });
      await settle();
      expect(releases).toHaveLength(5);

      // Act — a second load overtakes it. Decryption widens the overlap from
      // one round trip to one round trip plus five AEAD opens per pair of rows,
      // so this is reachable in a browser and not only in a test.
      custody.openWith = (binding, wire) => ({
        state: 'text',
        value: /\|([^|]*)\)$/.exec(wire)?.[1] ?? binding.rowId,
      });
      await loadWith(
        [sealedGroup(GROUP_ID, 'Fresh', 'Fresh note', 0)],
        [sealedCategory(CATEGORY_ID, 'Fresh', 'Fresh note', essentials, 0)],
      );
      for (const release of releases) {
        release({ state: 'text', value: 'Stale' });
      }
      await settle();

      // Assert — the slow first load may not revert either list behind the fast
      // one.
      expect(service.groups()).toEqual([
        expect.objectContaining({ name: { state: 'text', value: 'Fresh' } }),
      ]);
      expect(service.categories()).toEqual([
        expect.objectContaining({ name: { state: 'text', value: 'Fresh' } }),
      ]);
    });
  });

  describe('creating a category group', () => {
    it('seals the name against the identifier it posts', async () => {
      // Arrange — the stub seals to a value that names the binding it was
      // given, so one comparison says whether the two agree.

      // Act
      void service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      // Assert
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string; name: string };

      expect(body.id).toMatch(MINTED_ROW_ID);
      expect(body.name).toBe(
        sealedWire('category_groups', 'name', body.id, 'Essentials'),
      );
      request.flush(sealedGroup(body.id, 'Essentials', null, 0));
      await settle();
    });

    it('computes the blind index over the same text it sealed', async () => {
      // Act
      void service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      // Assert
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string; nameKey: string };

      expect(custody.indexCalls).toEqual([
        {
          field: { table: 'category_groups', column: 'name' },
          plaintext: 'Essentials',
        },
      ]);
      expect(body.nameKey).toBe(
        indexValue('category_groups', 'name', 'Essentials'),
      );
      request.flush(sealedGroup(body.id, 'Essentials', null, 0));
      await settle();
    });

    it('posts exactly the four declared members and nothing else', async () => {
      // Act
      void service.addGroup({ name: 'Essentials', description: 'The bills' });
      await settle();

      // Assert — `toEqual` is exact over own members, and the shape carries
      // `[JsonUnmappedMemberHandling(Disallow)]`: a `position` or a stray
      // `descriptionn` smuggled in here is a 400 from the real server, so it
      // has to be a red bar from this one.
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string };

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        description: sealedWire(
          'category_groups',
          'description',
          body.id,
          'The bills',
        ),
        id: body.id,
        name: sealedWire('category_groups', 'name', body.id, 'Essentials'),
        nameKey: indexValue('category_groups', 'name', 'Essentials'),
      });
      request.flush(sealedGroup(body.id, 'Essentials', 'The bills', 0));
      await settle();
    });

    it('posts null for an empty note and seals nothing for it', async () => {
      // Arrange — `''` is not a legal envelope and answers 400. The consequence
      // is worth stating rather than discovering: the column distinguishes a
      // note somebody cleared (a 29-byte envelope over `''`) from one nobody
      // ever wrote (`NULL`), and **this client has no path that produces the
      // first**, so the two are one thing from this browser. A gap, named
      // rather than closed.

      // Act
      void service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      // Assert — one seal, over the name, and `null` on the wire.
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string; description: unknown };

      expect(body.description).toBeNull();
      expect(custody.sealCalls.map((call) => call.binding.column)).toEqual([
        'name',
      ]);
      request.flush(sealedGroup(body.id, 'Essentials', null, 0));
      await settle();
    });

    it('seals a whitespace-only note as typed and never folds it to null', async () => {
      // Arrange — the removed `normalizeDescription` folded `'   '` onto
      // `null`. The client may not alter what it seals, and a note of three
      // spaces is a note somebody typed: **empty** is how this screen says "no
      // note", and whitespace is not empty. Folded, a person clears a note by
      // typing spaces into it and the row silently keeps saying nobody ever
      // wrote one.

      // Act
      void service.addGroup({ name: 'Essentials', description: '   ' });
      await settle();

      // Assert
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string; description: unknown };

      expect(body.description).toBe(
        sealedWire('category_groups', 'description', body.id, '   '),
      );
      request.flush(sealedGroup(body.id, 'Essentials', '   ', 0));
      await settle();
    });

    it('sends the typed name untrimmed', async () => {
      // Arrange — the client is forbidden from altering what it seals. The
      // non-blank rule moved to a form validator; a `.trim()` here would seal
      // one text and index another the moment anything else stopped trimming.

      // Act
      void service.addGroup({ name: '  Essentials  ', description: '' });
      await settle();

      // Assert
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string; name: string };

      expect(body.name).toBe(
        sealedWire('category_groups', 'name', body.id, '  Essentials  '),
      );
      expect(custody.indexCalls[0]?.plaintext).toBe('  Essentials  ');
      request.flush(sealedGroup(body.id, '  Essentials  ', null, 0));
      await settle();
    });

    it('posts nothing when sealing the name answers locked', async () => {
      // Arrange — reachable when the account's content key was replaced while
      // the cipher ran.
      custody.sealAnswer = { state: 'locked' };

      // Act
      await service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      // Assert
      http.expectNone(GROUPS_URL);
    });

    it('posts nothing when the index answers locked after the seal succeeded', async () => {
      // Arrange — the pair that diverges: a seal compares key identity and
      // keeps its answer through a plain `lock()`, an index compares the
      // generation counter and drops it. So `sealed` beside `locked` is
      // reachable, and posting then writes half a name pair through the one
      // door the server cannot see — it holds no index key, so it can never
      // notice that the column and its index disagree.
      custody.indexAnswer = { state: 'locked' };

      // Act
      await service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      // Assert
      http.expectNone(GROUPS_URL);
    });

    it('appends a created group without sorting it', async () => {
      // Arrange — the server computes the next position and appends, so
      // appending is what the next read would show. Sorting here — by name, as
      // the accounts screen does — would fight a `position` column the person
      // set by dragging, and on a locked account it would have no name to sort
      // by anyway.
      await loadWith([essentials], []);

      // Act
      void service.addGroup({ name: 'Lifestyle', description: '' });
      await settle();
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string };

      request.flush(sealedGroup(body.id, 'Lifestyle', null, 1));
      await settle();

      // Assert
      expect(service.groups()?.map((view) => view.name)).toEqual([
        { state: 'text', value: 'Essentials' },
        { state: 'text', value: 'Lifestyle' },
      ]);
    });

    it('posts nothing when the note fails to seal after the name succeeded', async () => {
      // Arrange — the third half. Posting here would create a group with its
      // name intact and the note silently dropped, and `NULL` is a legal value
      // for that column, so the row, the response and every later read would
      // all agree the person never wrote one.
      custody.sealAnswersByColumn.set('description', { state: 'locked' });

      // Act
      await service.addGroup({ name: 'Essentials', description: 'The bills' });
      await settle();

      // Assert
      http.expectNone(GROUPS_URL);
    });
  });

  describe('renaming a category group', () => {
    it('re-seals under the existing row id and mints none', async () => {
      // Arrange — the id is the associated data both envelopes were sealed
      // against. A freshly minted one here compiles, posts, and leaves a name
      // and a note that never open again, with nothing anywhere naming the
      // cause.

      // Act
      void service.updateGroup(GROUP_ID, {
        name: 'Renamed',
        description: 'New note',
      });
      await settle();

      // Assert
      const request = http.expectOne(`${GROUPS_URL}/${GROUP_ID}`);

      expect(request.request.method).toBe('PUT');
      expect(custody.sealCalls).toEqual([
        {
          binding: {
            table: 'category_groups',
            column: 'name',
            rowId: GROUP_ID,
          },
          plaintext: 'Renamed',
        },
        {
          binding: {
            table: 'category_groups',
            column: 'description',
            rowId: GROUP_ID,
          },
          plaintext: 'New note',
        },
      ]);
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    it('sends exactly the three declared members on an update', async () => {
      // Act
      void service.updateGroup(GROUP_ID, {
        name: 'Renamed',
        description: 'New note',
      });
      await settle();

      // Assert — `toEqual` is exact over own members, so an `id` or a
      // `position` smuggled in here reddens. The route binds three members and
      // the shape refuses what it was not asked for.
      const request = http.expectOne(`${GROUPS_URL}/${GROUP_ID}`);

      expect(request.request.body).toEqual({
        description: sealedWire(
          'category_groups',
          'description',
          GROUP_ID,
          'New note',
        ),
        name: sealedWire('category_groups', 'name', GROUP_ID, 'Renamed'),
        nameKey: indexValue('category_groups', 'name', 'Renamed'),
      });
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    it('patches the renamed group in the list and on its categories', async () => {
      // Arrange — the route answers 204, so nothing comes back to map. A
      // service that left the lists alone here shows the old name until the
      // next load, and the group's name is denormalized onto every category in
      // it.
      await loadWith([essentials], [groceries]);

      // Act
      void service.updateGroup(GROUP_ID, {
        name: 'Renamed',
        description: 'New note',
      });
      await settle();
      http
        .expectOne(`${GROUPS_URL}/${GROUP_ID}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert — the text this browser just sealed, which is what it would read
      // back.
      expect(service.groups()).toEqual([
        expect.objectContaining({
          description: { state: 'text', value: 'New note' },
          name: { state: 'text', value: 'Renamed' },
        }),
      ]);
      expect(service.categories()?.at(0)?.categoryGroupName).toEqual({
        state: 'text',
        value: 'Renamed',
      });
    });

    it('patches a cleared note to null and never to an empty word', async () => {
      // Arrange — `''` posts `null`, so what the row holds afterwards is a
      // column with nothing in it. Patching it to `{ state: 'text', value: '' }`
      // would render an empty element where the row now renders nothing, and
      // the next load would disagree with the screen.
      await loadWith([essentials], []);

      // Act
      void service.updateGroup(GROUP_ID, { name: 'Renamed', description: '' });
      await settle();
      http
        .expectOne(`${GROUPS_URL}/${GROUP_ID}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      expect(service.groups()?.at(0)?.description).toBeNull();
    });
  });

  describe('creating a category', () => {
    it('posts exactly the five declared members and nothing else', async () => {
      // Act
      void service.addCategory({
        name: 'Groceries',
        description: 'Food and drink',
        categoryGroupId: GROUP_ID,
      });
      await settle();

      // Assert
      const request = http.expectOne(CATEGORIES_URL);
      const body = request.request.body as { id: string };

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        categoryGroupId: GROUP_ID,
        description: sealedWire(
          'categories',
          'description',
          body.id,
          'Food and drink',
        ),
        id: body.id,
        name: sealedWire('categories', 'name', body.id, 'Groceries'),
        nameKey: indexValue('categories', 'name', 'Groceries'),
      });
      expect(body.id).toMatch(MINTED_ROW_ID);
      request.flush(
        sealedCategory(body.id, 'Groceries', 'Food and drink', essentials, 0),
      );
      await settle();
    });

    it('indexes the category name under the categories pair and not the group’s', async () => {
      // Arrange — an index is looked up as a pair, so `category_groups` beside
      // `name` and `categories` beside `name` are two different keys over one
      // string. Keyed under the wrong table a category would collide with a
      // group of the same name in a lookup that will exist later, and nothing
      // about the value would look wrong.

      // Act
      void service.addCategory({
        name: 'Groceries',
        description: '',
        categoryGroupId: GROUP_ID,
      });
      await settle();

      // Assert
      const request = http.expectOne(CATEGORIES_URL);
      const body = request.request.body as { id: string };

      expect(custody.indexCalls).toEqual([
        {
          field: { table: 'categories', column: 'name' },
          plaintext: 'Groceries',
        },
      ]);
      request.flush(sealedCategory(body.id, 'Groceries', null, essentials, 0));
      await settle();
    });

    it('posts nothing when the note fails to seal after the name succeeded', async () => {
      // Arrange
      custody.sealAnswersByColumn.set('description', { state: 'locked' });

      // Act
      await service.addCategory({
        name: 'Groceries',
        description: 'Food and drink',
        categoryGroupId: GROUP_ID,
      });
      await settle();

      // Assert
      http.expectNone(CATEGORIES_URL);
    });

    it('adds the created category to the list under the group’s opened name', async () => {
      // Arrange — the 201 carries the group's sealed name, so the row that
      // lands in the list has been through the mapper rather than assembled
      // from what was typed.
      await loadWith([essentials], []);

      // Act
      void service.addCategory({
        name: 'Groceries',
        description: '',
        categoryGroupId: GROUP_ID,
      });
      await settle();
      const request = http.expectOne(CATEGORIES_URL);
      const body = request.request.body as { id: string };

      request.flush(sealedCategory(body.id, 'Groceries', null, essentials, 0));
      await settle();

      // Assert
      expect(service.categories()).toEqual([
        expect.objectContaining({
          categoryGroupName: { state: 'text', value: 'Essentials' },
          description: null,
          name: { state: 'text', value: 'Groceries' },
        }),
      ]);
    });

    it('files the created category under its own group rather than at the end', async () => {
      // Arrange — a category created into the **first** group while the second
      // already holds rows. With one row in an empty list a plain append and a
      // sort are indistinguishable, and that is what a weaker version of the
      // case above could not see: the published list is ordered by the group's
      // position and then the row's, and an appended row breaks that contract
      // without any screen noticing today.
      const dining = sealedCategory(
        OTHER_CATEGORY_ID,
        'Dining out',
        null,
        lifestyle,
        0,
      );

      await loadWith([essentials, lifestyle], [dining]);

      // Act
      void service.addCategory({
        name: 'Groceries',
        description: '',
        categoryGroupId: GROUP_ID,
      });
      await settle();
      const request = http.expectOne(CATEGORIES_URL);
      const body = request.request.body as { id: string };

      request.flush(sealedCategory(body.id, 'Groceries', null, essentials, 0));
      await settle();

      // Assert
      expect(service.categories()?.map((view) => view.name)).toEqual([
        { state: 'text', value: 'Groceries' },
        { state: 'text', value: 'Dining out' },
      ]);
    });
  });

  describe('renaming a category', () => {
    it('re-seals under the existing row id and sends the three declared members', async () => {
      // Act
      void service.updateCategory(CATEGORY_ID, {
        name: 'Renamed',
        description: 'New note',
      });
      await settle();

      // Assert
      const request = http.expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}`);

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({
        description: sealedWire(
          'categories',
          'description',
          CATEGORY_ID,
          'New note',
        ),
        name: sealedWire('categories', 'name', CATEGORY_ID, 'Renamed'),
        nameKey: indexValue('categories', 'name', 'Renamed'),
      });
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    it('patches the renamed category in the list', async () => {
      // Arrange
      await loadWith([essentials], [groceries]);

      // Act
      void service.updateCategory(CATEGORY_ID, {
        name: 'Renamed',
        description: '',
      });
      await settle();
      http
        .expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert — the group's name is untouched by a rename of the category.
      expect(service.categories()).toEqual([
        expect.objectContaining({
          categoryGroupName: { state: 'text', value: 'Essentials' },
          description: null,
          name: { state: 'text', value: 'Renamed' },
        }),
      ]);
    });
  });

  describe('positions and removals', () => {
    it('moves a category across groups and reindexes both groups locally', async () => {
      // Arrange
      const utilities = sealedCategory(
        OTHER_CATEGORY_ID,
        'Utilities',
        null,
        essentials,
        1,
      );

      await loadWith([essentials, lifestyle], [groceries, utilities]);

      // Act
      service.placeCategory(CATEGORY_ID, OTHER_GROUP_ID, 0);
      http
        .expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}/placement`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert — the moved row takes the destination group's **opened** name
      // with it, because that is what the next read would hand back.
      expect(
        service.categories()?.map((view) => ({
          categoryGroupId: view.categoryGroupId,
          categoryGroupName: view.categoryGroupName,
          id: view.id,
          position: view.position,
        })),
      ).toEqual([
        {
          categoryGroupId: GROUP_ID,
          categoryGroupName: { state: 'text', value: 'Essentials' },
          id: OTHER_CATEGORY_ID,
          position: 0,
        },
        {
          categoryGroupId: OTHER_GROUP_ID,
          categoryGroupName: { state: 'text', value: 'Lifestyle' },
          id: CATEGORY_ID,
          position: 0,
        },
      ]);
    });

    it('reorders within one group without duplicating categories', async () => {
      // Arrange
      const utilities = sealedCategory(
        OTHER_CATEGORY_ID,
        'Utilities',
        null,
        essentials,
        1,
      );

      await loadWith([essentials], [groceries, utilities]);

      // Act
      service.placeCategory(OTHER_CATEGORY_ID, GROUP_ID, 0);
      http
        .expectOne(`${CATEGORIES_URL}/${OTHER_CATEGORY_ID}/placement`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      expect(
        service.categories()?.map((view) => [view.id, view.position]),
      ).toEqual([
        [OTHER_CATEGORY_ID, 0],
        [CATEGORY_ID, 1],
      ]);
    });

    it('reorders the groups locally once a move is accepted', async () => {
      // Arrange
      await loadWith([essentials, lifestyle], []);

      // Act
      service.moveGroup(OTHER_GROUP_ID, 0);
      http
        .expectOne(`${GROUPS_URL}/${OTHER_GROUP_ID}/position`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      expect(service.groups()?.map((view) => [view.id, view.position])).toEqual(
        [
          [OTHER_GROUP_ID, 0],
          [GROUP_ID, 1],
        ],
      );
    });

    it('removes a group from the list once the delete is accepted', async () => {
      // Arrange
      await loadWith([essentials, lifestyle], []);

      // Act
      service.removeGroup(GROUP_ID);
      const request = http.expectOne(`${GROUPS_URL}/${GROUP_ID}`);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert — the survivors are renumbered from zero, because that is what
      // the next read would show and these local patches exist to agree with
      // it. Asserting the names alone left this uncovered: a delete that
      // dropped the renumbering passed the whole file.
      expect(request.request.method).toBe('DELETE');
      expect(
        service.groups()?.map((view) => [view.name, view.position]),
      ).toEqual([[{ state: 'text', value: 'Lifestyle' }, 0]]);
    });

    it('removes a category from the list once the delete is accepted', async () => {
      // Arrange
      const utilities = sealedCategory(
        OTHER_CATEGORY_ID,
        'Utilities',
        null,
        essentials,
        1,
      );

      await loadWith([essentials], [groceries, utilities]);

      // Act
      service.removeCategory(CATEGORY_ID);
      http
        .expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      expect(
        service.categories()?.map((view) => [view.id, view.position]),
      ).toEqual([[OTHER_CATEGORY_ID, 0]]);
    });

    it('answers no categories for a group while the list has no answer', () => {
      // Arrange — nothing loaded, so the list is `null`. `categoriesForGroup`
      // has to say "none to show" without inventing a list, and without
      // throwing at a template that is rendering during a load.

      // Act
      const categories = service.categoriesForGroup(GROUP_ID);

      // Assert
      expect(categories).toEqual([]);
    });

    it('answers the same array twice for one group', async () => {
      // Arrange — the template calls this **twice per group**, once for the
      // rows and once for `[cdkDropListData]`, and a template call runs on
      // every change-detection tick in a zone-based app. Filtered per call, the
      // drop list's data identity changed on every tick and every group's rows
      // were re-allocated with it.
      await loadWith([essentials], [groceries]);

      // Act
      const first = service.categoriesForGroup(GROUP_ID);
      const second = service.categoriesForGroup(GROUP_ID);

      // Assert — `toBe`, not `toEqual`: identity is the whole claim.
      expect(second).toBe(first);
    });

    it('answers the same empty array twice for a group holding none', async () => {
      // Arrange — the half a `?? []` at the call site would leave open, and it
      // is the commoner case on a screen somebody has just started filling in.
      await loadWith([essentials, lifestyle], [groceries]);

      // Act
      const first = service.categoriesForGroup(OTHER_GROUP_ID);
      const second = service.categoriesForGroup(OTHER_GROUP_ID);

      // Assert
      expect(first).toEqual([]);
      expect(second).toBe(first);
    });

    it('answers a fresh array once the list underneath it changes', async () => {
      // Arrange — the positive control. An implementation that answered one
      // remembered array forever would pass both cases above and show a
      // deleted category until the next page load.
      const utilities = sealedCategory(
        OTHER_CATEGORY_ID,
        'Utilities',
        null,
        essentials,
        1,
      );

      await loadWith([essentials], [groceries, utilities]);
      const before = service.categoriesForGroup(GROUP_ID);

      // Act
      service.removeCategory(CATEGORY_ID);
      http
        .expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      const after = service.categoriesForGroup(GROUP_ID);

      expect(after).not.toBe(before);
      expect(after.map((view) => view.id)).toEqual([OTHER_CATEGORY_ID]);
    });

    it('answers only the categories filed under the group asked for', async () => {
      // Arrange
      const dining = sealedCategory(
        OTHER_CATEGORY_ID,
        'Dining out',
        null,
        lifestyle,
        0,
      );

      await loadWith([essentials, lifestyle], [groceries, dining]);

      // Act
      const categories = service.categoriesForGroup(OTHER_GROUP_ID);

      // Assert
      expect(categories.map((view) => view.id)).toEqual([OTHER_CATEGORY_ID]);
    });
  });

  // How a write ends, as a value the screen receives.
  //
  // **Both halves of this screen are asserted, and that is not padding.** A
  // group and a category are four separate pipes with four separate
  // `catchError`s, and this service shipped once with a difference between two
  // of its arms that nothing could see. A word answered by the group create and
  // swallowed by the category update is exactly that shape again.
  describe('the word a write ends on', () => {
    it('answers recorded when a group create lands', async () => {
      // Arrange
      const write = service.addGroup({ name: 'Essentials', description: '' });

      await settle();

      // Act
      const request = http.expectOne(GROUPS_URL);
      const body = request.request.body as { id: string };

      request.flush(sealedGroup(body.id, 'Essentials', null, 0));

      // Assert — the one word that permits a form to be cleared.
      expect(await write).toEqual({ state: 'recorded' });
    });

    it('answers the server’s own sentences when a group name is already taken', async () => {
      // Arrange — a duplicate group name is a **400 keyed on `Name`** here,
      // where the payee create answers a 409. A screen keyed on the status
      // would pass on this half and fail two files away.
      const write = service.addGroup({ name: 'Essentials', description: '' });

      await settle();

      // Act
      http
        .expectOne(GROUPS_URL)
        .flush(withErrors(['Name', ['Category group name must be unique.']]), {
          status: 400,
          statusText: 'Bad Request',
        });

      // Assert
      const outcome = await write;

      expect(outcome.state).toBe('invalid');
      expect(outcome.state === 'invalid' ? [...outcome.errors] : null).toEqual([
        ['Name', ['Category group name must be unique.']],
      ]);
    });

    it('answers duplicate-identifier on a retried group create', async () => {
      // Arrange — the same status as a duplicate name and the opposite remedy.
      const write = service.addGroup({ name: 'Essentials', description: '' });

      await settle();

      // Act
      http
        .expectOne(GROUPS_URL)
        .flush(
          { conflictKind: 'duplicate_identifier' },
          { status: 409, statusText: 'Conflict' },
        );

      // Assert
      expect(await write).toEqual({ state: 'duplicate-identifier' });
    });

    it('answers unreachable when a category create gets no answer', async () => {
      // Arrange
      await loadWith([essentials], []);

      const write = service.addCategory({
        categoryGroupId: GROUP_ID,
        name: 'Groceries',
        description: '',
      });

      await settle();

      // Act
      http
        .expectOne(CATEGORIES_URL)
        .flush('nope', { status: 500, statusText: 'Server Error' });

      // Assert
      expect(await write).toEqual({ state: 'unreachable' });
    });

    it('answers a category rename’s refusal on the same terms', async () => {
      // Arrange — the fourth pipe, and the one a shared argument would leave
      // uncovered.
      await loadWith([essentials], [groceries]);

      const write = service.updateCategory(CATEGORY_ID, {
        name: 'Groceries',
        description: '',
      });

      await settle();

      // Act
      http
        .expectOne(`${CATEGORIES_URL}/${CATEGORY_ID}`)
        .flush(withErrors(['Name', ['Category name must be unique.']]), {
          status: 400,
          statusText: 'Bad Request',
        });

      // Assert
      const outcome = await write;

      expect(outcome.state).toBe('invalid');
      expect(outcome.state === 'invalid' ? [...outcome.errors] : null).toEqual([
        ['Name', ['Category name must be unique.']],
      ]);
    });

    it('answers locked without sending anything when sealing refuses', async () => {
      // Arrange — nothing was sent, so there is no answer to classify.
      custody.sealAnswer = { state: 'locked' };

      // Act
      const outcome = await service.addGroup({
        name: 'Essentials',
        description: '',
      });

      // Assert
      http.expectNone(GROUPS_URL);
      expect(outcome).toEqual({ state: 'locked' });
    });
  });

  // The identifiers the two creates carry, across presses.
  //
  // **`accounts.service.spec.ts` argues why a create's id has to survive a
  // refusal** — an id minted per press turns a lost answer into two rows and
  // makes `duplicate-identifier` unreachable. What is this screen's own is that
  // there are **two** writing surfaces, on screen together, so one shared draft
  // would hand a refused group's id to the next category create and collide on
  // a table it was never drawn for.
  describe('the identifiers the two creates carry', () => {
    // One press on each form, answered by the caller: `expectOne` consumes the
    // request it matches, so the request itself is what comes back.
    async function pressGroup(): Promise<TestRequest> {
      void service.addGroup({ name: 'Essentials', description: '' });
      await settle();

      return http.expectOne(GROUPS_URL);
    }

    async function pressCategory(): Promise<TestRequest> {
      void service.addCategory({
        categoryGroupId: GROUP_ID,
        description: '',
        name: 'Groceries',
      });
      await settle();

      return http.expectOne(CATEGORIES_URL);
    }

    function postedId(request: TestRequest): string {
      return (request.request.body as { id: string }).id;
    }

    it('keeps a group’s identifier across a refusal and redraws it once one lands', async () => {
      // Arrange
      const refused = await pressGroup();
      const drafted = postedId(refused);

      refused.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act
      const retried = await pressGroup();

      // Assert — the same id, so a lost answer collides instead of writing a
      // second group.
      expect(postedId(retried)).toBe(drafted);
      retried.flush(sealedGroup(drafted, 'Essentials', null, 0));
      await settle();

      const next = await pressGroup();

      expect(postedId(next)).not.toBe(drafted);
      expect(postedId(next)).toMatch(MINTED_ROW_ID);
      next.flush(sealedGroup(postedId(next), 'Essentials', null, 1));
      await settle();
    });

    it('keeps a category’s identifier across a refusal and redraws it once one lands', async () => {
      // Arrange — the same rule on the other form, written out rather than
      // inferred: the two handlers are separate code and a screen that got one
      // right and the other wrong is what shipped last time somebody copied a
      // form.
      const refused = await pressCategory();
      const drafted = postedId(refused);

      refused.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act
      const retried = await pressCategory();

      // Assert
      expect(postedId(retried)).toBe(drafted);
      retried.flush(
        sealedCategory(
          drafted,
          'Groceries',
          null,
          sealedGroup(GROUP_ID, 'Essentials', null, 0),
          0,
        ),
      );
      await settle();

      const next = await pressCategory();

      expect(postedId(next)).not.toBe(drafted);
      next.flush(
        sealedCategory(
          postedId(next),
          'Groceries',
          null,
          sealedGroup(GROUP_ID, 'Essentials', null, 0),
          1,
        ),
      );
      await settle();
    });

    it('keeps the two drafts apart', async () => {
      // Arrange — the case that reddens on one shared field. Both forms are
      // refused, and a single draft would send the group's id to the
      // categories table on the next press — a conflict on a row that has
      // nothing to do with it, and a sentence about an entry somebody never
      // made.
      const group = await pressGroup();
      const groupDraft = postedId(group);

      group.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      // Act
      const category = await pressCategory();

      // Assert
      expect(postedId(category)).not.toBe(groupDraft);
      category.flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      const retriedGroup = await pressGroup();

      expect(postedId(retriedGroup)).toBe(groupDraft);
      retriedGroup.flush('', { status: 500, statusText: 'Server Error' });
      await settle();
    });
  });
});
