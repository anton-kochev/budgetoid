// What drives a key rotation: one press, one run, and every sealed value in the
// account rewritten under a generation this tab draws and hands to every factor.
//
// The order is the act. Assemble the material from one ceremony and the two
// reads; post the begin, which stages the manifest and one seal per factor and
// answers with the inventory; collect every narrative row across the five arms;
// re-seal each under the next content key and recompute each blind index under
// the next index key; post them in chunks; post the completion. **It stops at
// the 204.** Taking custody of the promoted generation is a step of its own and
// is not taken here.
//
// **`key-rotation-material.ts` owns the run's domain rules and this file owns
// none of them.** Which generation is minted and which is recovered, the four
// refusals over the manifest and the seals, the epoch — all of that is decided
// there, framework-free, and is deliberately not restated here. What this file
// adds is everything that needs an injector: the transport, the five list reads,
// the iteration, the chunking, and the words a screen renders.
//
// **Both generations live in `#` fields with no accessor, and there never may be
// one.** It is `AccountKeyCustodyService`'s rule about its own two keys, and it
// binds harder here: for the length of a run this object holds the generation
// still in force *and* the one replacing it, so a getter would hand out the
// whole of an account's keyspace across the change that was supposed to end the
// old one. `private` is not enough — it is erased, so `(driver as never)['next']`
// reads the field at runtime, and so does any devtools panel, any
// `JSON.stringify` of the instance and any structured clone of it.
//
// **NgRx is refused here, and not because the store is empty.** A store's state
// is serializable by construction — that is what a time-travelling inspector,
// a rehydrated snapshot and a devtools bridge all rest on — and the one thing in
// this client that must never be serializable is the next generation's keys. So
// this is a service holding `CryptoKey` objects and publishing three signals,
// and the day somebody "completes" the empty store, this is not the slice to
// complete it with. The signals below carry a phase, two integers and a word;
// none of them is key material and none of them ever may be.
//
// **No per-row progress record is kept anywhere, and `localStorage` least of
// all.** Keeping "which rows are done" across a reload would be a second
// numerator able to disagree with the server's completeness gate, persisted,
// naming which rows an account holds, in a store anybody at this device can
// write. It is precisely the thing the server declines to keep — the chunk route
// answers no count and the resume read carries none, by design — and
// `docs/design/components.md` writes it down so nobody adds it back for a nicer
// bar. A run that is interrupted restarts its bar from zero, and the consequence
// block on the screen says so before it happens.
import { HttpErrorResponse } from '@angular/common/http';
import {
  Injectable,
  computed,
  inject,
  signal,
  type Signal,
} from '@angular/core';
import { AccountApiService } from '@app-core/api/account-api.service';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import {
  KeyRotationApiService,
  type ResealChunkRequestBody,
  type ResealedDescribedRowBody,
  type ResealedNamedRowBody,
  type ResealedTransactionBody,
  type RotationInventoryDto,
} from '@app-core/api/key-rotation-api.service';
import { MeApiService } from '@app-core/api/me-api.service';
import { PayeesApiService } from '@app-core/api/payees-api.service';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { SessionService } from '@app-core/session/session.service';
import { firstValueFrom } from 'rxjs';
import { computeBlindIndex, type BlindIndexedField } from './blind-index';
import {
  assembleKeyRotationMaterial,
  KeyRotationMaterialError,
  type AccountKeyGeneration,
} from './key-rotation-material';
// **The yield is borrowed and not rewritten.** `narrative-batch.ts` argues which
// mechanisms hand a frame back and which only look as though they do —
// `scheduler.yield()` resumes ahead of the browser's rendering and draws no
// frame — and a second copy of that decision here would be one nobody keeps
// true. The opens go through that module's own driver for the same reason.
import {
  handBackTheFrame,
  openNarrativeBatch,
  type NarrativeRequest,
} from './narrative-batch';
import {
  openNarrativeField,
  sealNarrativeField,
  type NarrativeFieldBinding,
} from './narrative-cipher';
import type { NarrativeOpener, NarrativeText } from './narrative-text';
import type { PasskeyAssertionCeremony } from './webauthn-ceremony.service';

/**
 * Where a run is, as the screen's three phase sentences need it.
 *
 * `idle` is at rest — before a press, and after a refusal, because a phase says
 * what is happening now and after a refusal nothing is. `finished` is a run that
 * reached its 204. The three in between are the design chapter's three phases
 * and carry its copy; the word is this service's, the sentence is the screen's.
 */
export type KeyRotationPhase =
  | 'idle'
  | 'collecting'
  | 'resealing'
  | 'finishing'
  | 'finished';

/**
 * Why a run did not finish, in the six words `docs/design/components.md`
 * specifies with their copy.
 *
 * They are **not** `UnlockFailure`'s five, though four of them share a spelling:
 * each of these says what became of the *run*, which custody's lines have no run
 * to say anything about. `unopened` is deliberately absent — a begin is gated on
 * an assertion the server verified against this account, so a factor of this
 * account that then opens nothing is the account's material failing to agree
 * with itself rather than a device somebody could swap.
 */
export type KeyRotationFailure =
  | 'unreachable'
  | 'unauthenticated'
  | 'unrecognised'
  | 'inconsistent'
  | 'unfinished'
  | 'factors-moved';

/**
 * What the bar draws: rows the server has accepted, over rows this run has to
 * carry.
 *
 * **`resealed` counts rows carried by a chunk the server answered 204** — never
 * rows collected, never rows sealed, never rows queued. A chunk is
 * all-or-nothing in one save, so 204 is the only increment that is honest at the
 * moment it is drawn.
 *
 * **`records` is the inventory's five narrative arms, raised to what this client
 * actually collected.** Rows created between the begin and the collection are
 * sealed under the generation being replaced, so a chunk has to visit them and
 * the completeness gate counts them; a denominator left at the published number
 * would be a bar that runs past its own end.
 */
export interface KeyRotationProgress {
  readonly resealed: number;
  readonly records: number;
}

// How many times a run collects, sends and asks to finish before it gives up.
//
// **Bounded, and the bound is the rule.** Rows created after a collection make
// the completion answer that the run is incomplete, and the remedy is to collect
// and send again. An unbounded loop is the same non-converging failure
// `docs/business-logic/key-rotation.md` refuses from the other side, and a person
// watching a bar go round forever has been told less than one who has been told
// to close a tab.
const COLLECTION_PASSES = 3;

// The conflict kinds this client has a branch for, spelled out here and nowhere
// else in this file. `Domain/Common/ConflictKindSpelling.cs` owns them on the
// other side.
const FACTOR_SET_MOVED = 'factor_set_moved';
const ROTATION_INCOMPLETE = 'rotation_incomplete';
const ROTATION_ALREADY_COMPLETED = 'rotation_already_completed';

// The five pairs a chunk carries, as the two censuses spell them. Written as
// constants so the seal and the index over one column cannot drift apart at a
// call site, and typed by the census rather than as strings so a pair this
// product does not encrypt is a compile error.
const ACCOUNT_NAME = { table: 'accounts', column: 'name' } as const;
const PAYEE_NAME = { table: 'payees', column: 'name' } as const;
const GROUP_NAME = { table: 'category_groups', column: 'name' } as const;
const GROUP_NOTE = { table: 'category_groups', column: 'description' } as const;
const CATEGORY_NAME = { table: 'categories', column: 'name' } as const;
const CATEGORY_NOTE = { table: 'categories', column: 'description' } as const;
const TRANSACTION_NOTE = {
  table: 'transactions',
  column: 'description',
} as const;

// One row's narrative, opened. Three shapes because the chunk route has three,
// and a shared one carrying optional members would let an arm send a column its
// table does not have.
interface OpenedNamedRow {
  readonly id: string;
  readonly name: string;
}

interface OpenedDescribedRow extends OpenedNamedRow {
  readonly description: string | null;
}

interface OpenedNotedRow {
  readonly id: string;
  readonly description: string;
}

// Everything a run has to rewrite, in the clear, for the length of one pass.
interface Collection {
  readonly accounts: readonly OpenedNamedRow[];
  readonly payees: readonly OpenedNamedRow[];
  readonly categoryGroups: readonly OpenedDescribedRow[];
  readonly categories: readonly OpenedDescribedRow[];
  readonly transactions: readonly OpenedNotedRow[];
}

// A refusal this driver makes, carrying the word a screen renders. Local,
// because nothing outside acts on the type — what a caller reads is
// {@link KeyRotationService.failure}.
class KeyRotationRefusal extends Error {
  public override readonly name = 'KeyRotationRefusal';

  public readonly word: KeyRotationFailure;

  constructor(word: KeyRotationFailure, message: string, cause?: unknown) {
    super(message, cause === undefined ? undefined : { cause });

    this.word = word;
  }
}

// The `conflictKind` of a 409, or `null` for every other answer — including a
// 409 whose body names a kind this client has never heard of, which is not a
// guess this client is entitled to make.
function conflictKindOf(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body: unknown = error.error;

  if (typeof body !== 'object' || body === null || !('conflictKind' in body)) {
    return null;
  }

  const kind: unknown = body.conflictKind;

  return typeof kind === 'string' ? kind : null;
}

// The plaintext an opener answered with, or a refusal. The opener this service
// builds only ever answers `text` — it rejects rather than reporting a word — so
// the other two members are a shape the compiler can see and this run cannot
// reach.
function plaintextOf(answer: NarrativeText): string {
  if (answer.state !== 'text') {
    throw new KeyRotationRefusal(
      'inconsistent',
      `A narrative value came back as ${answer.state} during a rotation.`,
    );
  }

  return answer.value;
}

// What one entry costs a chunk, in bytes. Every member of every arm is ASCII —
// a rendered uuid, unpadded base64url — so the JSON's length in UTF-16 units is
// its length in bytes; the `+ 1` is the comma that joins it to its neighbour.
function entryBytes(entry: unknown): number {
  return JSON.stringify(entry).length + 1;
}

@Injectable({ providedIn: 'root' })
export class KeyRotationService {
  readonly #rotations = inject(KeyRotationApiService);
  readonly #keys = inject(MeApiService);
  // **`SessionService` for the tenancy, and `GET /api/me` is not read again.**
  // A blind index is keyed inside a budget, so a run cannot recompute one
  // without it. Custody may not inject this class — that class injects custody,
  // and the edge would be a cycle — but nothing of the kind applies here, and a
  // second read of the same route would be a second answer able to disagree
  // with the one every screen is already using.
  readonly #session = inject(SessionService);
  readonly #accountsApi = inject(AccountApiService);
  readonly #payeesApi = inject(PayeesApiService);
  readonly #categoryGroupsApi = inject(CategoryGroupsApiService);
  readonly #categoriesApi = inject(CategoriesApiService);
  readonly #transactionsApi = inject(TransactionsApiService);

  // **Two generations, as key objects and never as bytes, with no accessor.**
  // The material module hands both back already imported and wipes what they
  // were made of on the way; a field typed `Uint8Array` here would keep the
  // plaintext of four keys alive for the life of the tab on an object every
  // injector in the app can reach.
  #current: AccountKeyGeneration | null = null;
  #next: AccountKeyGeneration | null = null;

  readonly #phase = signal<KeyRotationPhase>('idle');
  readonly #progress = signal<KeyRotationProgress>({ resealed: 0, records: 0 });
  readonly #failure = signal<KeyRotationFailure | null>(null);

  public readonly phase: Signal<KeyRotationPhase> = this.#phase.asReadonly();

  public readonly progress: Signal<KeyRotationProgress> =
    this.#progress.asReadonly();

  public readonly failure: Signal<KeyRotationFailure | null> =
    this.#failure.asReadonly();

  /**
   * Whether a run is in flight.
   *
   * **One predicate with one owner.** The Rotate control's `disabled`, its
   * `aria-busy` and its click handler all bind this, and the three content
   * screens read it beside custody's lockedness. A screen deriving its own from
   * {@link phase} would be a second definition of the same fact, which is the
   * drift the Unlock control already paid for once.
   */
  public readonly running: Signal<boolean> = computed(() => {
    const phase = this.#phase();

    return phase !== 'idle' && phase !== 'finished';
  });

  /**
   * Begins a rotation and drives it to its completion, or publishes the word it
   * stopped on.
   *
   * `ceremony` is what a passkey assertion just yielded: the payload the begin
   * is authorized by, and the key-encryption key one factor derives. **The
   * ceremony is the first thing a press does and nothing is posted until it has
   * answered**, which is what makes the five ceremony sentences on the screen —
   * each ending *Nothing has changed.* — true.
   *
   * It resolves rather than rejecting, always. Every refusal is published as
   * {@link failure}, because a rotation's failures are things for a person to
   * read rather than exceptions for a caller to catch, and a rejected promise
   * on a button handler is one `catch` away from a silent one.
   */
  public async begin(ceremony: PasskeyAssertionCeremony): Promise<void> {
    this.#phase.set('collecting');
    this.#progress.set({ resealed: 0, records: 0 });
    this.#failure.set(null);

    try {
      await this.#drive(ceremony);
      this.#phase.set('finished');
    } catch (error: unknown) {
      this.#phase.set('idle');
      this.#failure.set(this.#wordFor(error));
    } finally {
      // **The generations end with the run.** The step that hands the promoted
      // pair to `AccountKeyCustodyService` is a commit of its own and would take
      // custody *before* this line; until it lands, a finished rotation leaves
      // this tab exactly as locked or unlocked as it was.
      this.#current = null;
      this.#next = null;
    }
  }

  async #drive(ceremony: PasskeyAssertionCeremony): Promise<void> {
    const budgetId = this.#session.budgetId();

    if (budgetId === null) {
      // No factor supplies a budget, so a ceremony cannot clear this and the
      // word that offers one would be a road that cannot help. `SessionService`
      // argues the same answer for every write that meets it.
      throw new KeyRotationRefusal(
        'unreachable',
        'This browser has not been told which budget it is in, so nothing can be keyed.',
      );
    }

    const custody = await firstValueFrom(this.#keys.getAccountKeys());
    const state = await firstValueFrom(this.#rotations.getRotationState());
    const material = await assembleKeyRotationMaterial(
      ceremony.keyEncryptionKey,
      custody,
      state,
    );

    this.#current = material.current;
    this.#next = material.next;

    // **A run already in flight keeps its identifier.** The material module has
    // just carried that run's generation forward rather than minting a fresh one
    // — a second begin overwrites the staged seals in place, and every row the
    // interrupted run already rewrote would then open under nothing at all — so
    // the rows it stamped are rows this run really has done. Minting a second
    // identifier here would orphan those stamps and make the design chapter's
    // *the records already re-encrypted stay that way* false.
    const rotationId = state.rotation?.rotationId ?? this.#mintRotationId();
    const begun = await firstValueFrom(
      this.#rotations.beginRotation({
        ...ceremony.payload,
        rotationId,
        manifest: material.manifest,
        rotationEpoch: material.rotationEpoch,
        seals: material.seals,
      }),
    );

    // **A tripwire that costs nothing today and refuses a run that could never
    // finish.** The chunk route has five arms and no budget arm — FR-099 keeps
    // `budgets` at `UPDATE (name)`, so a sixth arm would break a requirement —
    // and `budgets.name` is `NULL` on every row this product creates, so the
    // published count is 0 on every account there is. The day it is not, no
    // chunk this client can send stamps that row and the completeness gate
    // refuses forever; better not begun than begun and unfinishable.
    if (begun.inventory.budgets !== 0) {
      throw new KeyRotationRefusal(
        'unrecognised',
        `The begin published ${String(begun.inventory.budgets)} budget rows to re-seal, and no chunk this client sends can carry one.`,
      );
    }

    await this.#run(rotationId, budgetId, begun.inventory, begun.maxChunkBytes);
  }

  async #run(
    rotationId: string,
    budgetId: string,
    inventory: RotationInventoryDto,
    maxChunkBytes: number,
  ): Promise<void> {
    for (let pass = 1; pass <= COLLECTION_PASSES; pass += 1) {
      this.#phase.set('collecting');

      const collected = await this.#collect();

      // **Judged on the first pass only, because "nothing is posted" is a claim
      // only the first pass can make.** By the second, chunks have landed; and
      // a row deleted by another tab between two passes would make this
      // comparison refuse a run that is perfectly healthy.
      if (pass === 1) {
        this.#requireEveryCountedRow(inventory, collected);
      }

      this.#progress.set({ resealed: 0, records: rowsIn(collected) });
      this.#phase.set('resealing');

      await this.#send(rotationId, budgetId, collected, maxChunkBytes);

      this.#phase.set('finishing');

      if (await this.#complete(rotationId)) {
        return;
      }
    }

    throw new KeyRotationRefusal(
      'unfinished',
      `The account went on changing across ${String(COLLECTION_PASSES)} passes, so this run never caught up with it.`,
    );
  }

  // **Fewer rows than the server counted means this client cannot see rows the
  // run has to visit** — a list that started paging, a filter, a stale read —
  // and a run that starts there can never complete, because the gate counts what
  // the client never sent. More is legal and is the ordinary case: rows created
  // between the begin and this collection are sealed under the generation being
  // replaced, so a chunk has to carry them and the denominator rises to what was
  // collected.
  #requireEveryCountedRow(
    inventory: RotationInventoryDto,
    collected: Collection,
  ): void {
    const arms: readonly (readonly [string, number, number])[] = [
      ['accounts', inventory.accounts, collected.accounts.length],
      ['payees', inventory.payees, collected.payees.length],
      [
        'categoryGroups',
        inventory.categoryGroups,
        collected.categoryGroups.length,
      ],
      ['categories', inventory.categories, collected.categories.length],
      ['transactions', inventory.transactions, collected.transactions.length],
    ];

    for (const [arm, counted, collectedRows] of arms) {
      if (collectedRows < counted) {
        throw new KeyRotationRefusal(
          'unrecognised',
          `The server counts ${String(counted)} ${arm} rows to re-seal and this client can see ${String(collectedRows)}, so a run begun here could never finish.`,
        );
      }
    }
  }

  // The five list reads, then every sealed column opened.
  //
  // **The driver owns the iteration and `openNarrativeBatch` owns the frame.**
  // It hands the frame back between chunks of opens, which is the one place in a
  // pass where this tab would otherwise hold the main thread over a whole
  // account. Nothing opened here is kept past its own read: the plaintext lives
  // in this call's result for the length of one pass and goes with it.
  async #collect(): Promise<Collection> {
    const [accounts, payees, categoryGroups, categories, transactions] =
      await Promise.all([
        firstValueFrom(this.#accountsApi.getAccounts()),
        firstValueFrom(this.#payeesApi.getPayees()),
        firstValueFrom(this.#categoryGroupsApi.getCategoryGroups()),
        firstValueFrom(this.#categoriesApi.getCategories()),
        firstValueFrom(this.#transactionsApi.getTransactions()),
      ]);

    const requests: NarrativeRequest[] = [];

    const want = (binding: NarrativeFieldBinding, wire: string): void => {
      requests.push({ binding, wire });
    };

    for (const row of accounts.items) {
      want({ ...ACCOUNT_NAME, rowId: row.id }, row.name);
    }

    for (const row of payees.items) {
      want({ ...PAYEE_NAME, rowId: row.id }, row.name);
    }

    for (const row of categoryGroups.items) {
      want({ ...GROUP_NAME, rowId: row.id }, row.name);

      if (row.description !== null) {
        want({ ...GROUP_NOTE, rowId: row.id }, row.description);
      }
    }

    for (const row of categories.items) {
      // **`categoryGroupName` is not opened and not re-sealed here.** It is the
      // group's own column, denormalized onto this row and sealed against the
      // *group's* identifier; the group arm rewrites it, and the chunk route
      // carries no member for it on this arm at all.
      want({ ...CATEGORY_NAME, rowId: row.id }, row.name);

      if (row.description !== null) {
        want({ ...CATEGORY_NOTE, rowId: row.id }, row.description);
      }
    }

    for (const row of transactions.items) {
      // A note-less transaction has nothing to re-seal, so a chunk never names
      // it and the presence-aware gate never counts it. Most rows of a real
      // account are this row.
      if (row.description !== null) {
        want({ ...TRANSACTION_NOTE, rowId: row.id }, row.description);
      }
    }

    const opener = await openNarrativeBatch(requests, this.#opener());

    const open = async (
      binding: NarrativeFieldBinding,
      wire: string,
    ): Promise<string> => plaintextOf(await opener(binding, wire));

    return {
      accounts: await Promise.all(
        accounts.items.map(async (row) => ({
          id: row.id,
          name: await open({ ...ACCOUNT_NAME, rowId: row.id }, row.name),
        })),
      ),
      payees: await Promise.all(
        payees.items.map(async (row) => ({
          id: row.id,
          name: await open({ ...PAYEE_NAME, rowId: row.id }, row.name),
        })),
      ),
      categoryGroups: await Promise.all(
        categoryGroups.items.map(async (row) => ({
          id: row.id,
          name: await open({ ...GROUP_NAME, rowId: row.id }, row.name),
          description:
            row.description === null
              ? null
              : await open({ ...GROUP_NOTE, rowId: row.id }, row.description),
        })),
      ),
      categories: await Promise.all(
        categories.items.map(async (row) => ({
          id: row.id,
          name: await open({ ...CATEGORY_NAME, rowId: row.id }, row.name),
          description:
            row.description === null
              ? null
              : await open(
                  { ...CATEGORY_NOTE, rowId: row.id },
                  row.description,
                ),
        })),
      ),
      transactions: await Promise.all(
        transactions.items
          .filter((row) => row.description !== null)
          .map(async (row) => ({
            id: row.id,
            description: await open(
              { ...TRANSACTION_NOTE, rowId: row.id },
              row.description ?? '',
            ),
          })),
      ),
    };
  }

  // The opener the batch runs, and the one place a stored value is read.
  //
  // **It tries the generation in force and then the staged one.** A run that
  // replaces one already in flight carries that run's generation forward, so the
  // rows its chunks already rewrote are sealed under the *next* content key
  // while everything else is still under the current one. Both are live for the
  // length of a run — that is what the staging tables are for — and a driver
  // that only tried one of them would stop dead on the first row an earlier pass
  // had finished.
  #opener(): NarrativeOpener {
    return async (binding, wire): Promise<NarrativeText> => ({
      state: 'text',
      value: await this.#open(binding, wire),
    });
  }

  async #open(binding: NarrativeFieldBinding, wire: string): Promise<string> {
    const current = this.#current;
    const next = this.#next;

    if (current === null || next === null) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'This run holds no generation to open under.',
      );
    }

    try {
      return await openNarrativeField(current.contentKey, wire, binding);
    } catch (cause: unknown) {
      try {
        return await openNarrativeField(next.contentKey, wire, binding);
      } catch {
        // Neither generation opens it, and no factor of this account changes
        // that: every factor encapsulates the same two keys.
        throw new KeyRotationRefusal(
          'inconsistent',
          `${binding.table}.${binding.column} on ${binding.rowId} opens under neither generation of this account's keys.`,
          cause,
        );
      }
    }
  }

  // Re-seals every collected row and posts it, in chunks sized by the budget the
  // begin published.
  //
  // **That budget is advice and not a limit this route enforces** — enforcement
  // is the host's body cap, in one place — so this is a client sizing its
  // batches under it rather than a second ceiling. A single row that does not
  // fit still travels: splitting a row is not a thing a chunk can do.
  async #send(
    rotationId: string,
    budgetId: string,
    collected: Collection,
    maxChunkBytes: number,
  ): Promise<void> {
    const envelopeBytes = entryBytes({
      rotationId,
      accounts: [],
      payees: [],
      categoryGroups: [],
      categories: [],
      transactions: [],
    });

    let accounts: ResealedNamedRowBody[] = [];
    let payees: ResealedNamedRowBody[] = [];
    let categoryGroups: ResealedDescribedRowBody[] = [];
    let categories: ResealedDescribedRowBody[] = [];
    let transactions: ResealedTransactionBody[] = [];
    let rows = 0;
    let used = envelopeBytes;

    const flush = async (): Promise<void> => {
      if (rows === 0) {
        return;
      }

      const body: ResealChunkRequestBody = {
        rotationId,
        accounts,
        payees,
        categoryGroups,
        categories,
        transactions,
      };
      const carried = rows;

      await firstValueFrom(this.#rotations.resealRows(body));

      // **Only here.** The rows above were collected, sealed and queued, and
      // none of that is a fact about the account until the save behind this 204
      // committed.
      this.#progress.update((progress) => ({
        ...progress,
        resealed: progress.resealed + carried,
      }));

      accounts = [];
      payees = [];
      categoryGroups = [];
      categories = [];
      transactions = [];
      rows = 0;
      used = envelopeBytes;
    };

    const room = async (size: number): Promise<void> => {
      if (rows > 0 && used + size > maxChunkBytes) {
        await flush();
        // Between two chunks and never after the last one: the work a final
        // yield would interrupt is over. It is `narrative-batch.ts`'s rule and
        // `narrative-batch.ts`'s mechanism.
        await handBackTheFrame();
      }

      used += size;
      rows += 1;
    };

    for (const row of collected.accounts) {
      const body = await this.#namedBody(ACCOUNT_NAME, budgetId, row);

      await room(entryBytes(body));
      accounts.push(body);
    }

    for (const row of collected.payees) {
      const body = await this.#namedBody(PAYEE_NAME, budgetId, row);

      await room(entryBytes(body));
      payees.push(body);
    }

    for (const row of collected.categoryGroups) {
      const body = await this.#describedBody(
        GROUP_NAME,
        GROUP_NOTE,
        budgetId,
        row,
      );

      await room(entryBytes(body));
      categoryGroups.push(body);
    }

    for (const row of collected.categories) {
      const body = await this.#describedBody(
        CATEGORY_NAME,
        CATEGORY_NOTE,
        budgetId,
        row,
      );

      await room(entryBytes(body));
      categories.push(body);
    }

    for (const row of collected.transactions) {
      const body: ResealedTransactionBody = {
        id: row.id,
        description: await this.#seal(
          { ...TRANSACTION_NOTE, rowId: row.id },
          row.description,
        ),
      };

      await room(entryBytes(body));
      transactions.push(body);
    }

    await flush();
  }

  async #namedBody(
    field: BlindIndexedField,
    budgetId: string,
    row: OpenedNamedRow,
  ): Promise<ResealedNamedRowBody> {
    return {
      id: row.id,
      name: await this.#seal({ ...field, rowId: row.id }, row.name),
      nameKey: await this.#index({ ...field, budgetId }, row.name),
    };
  }

  async #describedBody(
    nameField: BlindIndexedField,
    noteField: typeof GROUP_NOTE | typeof CATEGORY_NOTE,
    budgetId: string,
    row: OpenedDescribedRow,
  ): Promise<ResealedDescribedRowBody> {
    return {
      ...(await this.#namedBody(nameField, budgetId, row)),
      // **The member is always present and `null` is not a way to clear it.**
      // The server refuses a reseal that changes whether a row holds a note in
      // either direction, so sending the member always is what keeps an arm's
      // member set a fact about this client rather than about which rows a chunk
      // happened to name.
      description:
        row.description === null
          ? null
          : await this.#seal({ ...noteField, rowId: row.id }, row.description),
    };
  }

  async #seal(
    binding: NarrativeFieldBinding,
    plaintext: string,
  ): Promise<string> {
    const next = this.#next;

    if (next === null) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'This run holds no generation to re-seal under.',
      );
    }

    return sealNarrativeField(next.contentKey, plaintext, binding);
  }

  async #index(
    binding: BlindIndexedField & { readonly budgetId: string },
    plaintext: string,
  ): Promise<string> {
    const next = this.#next;

    if (next === null) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'This run holds no generation to key under.',
      );
    }

    return computeBlindIndex(next.indexKey, binding, plaintext);
  }

  // Asks the server to promote what the begin staged. `true` when the run is
  // done, `false` when the account moved under it and the remedy is another
  // pass.
  //
  // **A completion refused because the rotation was already completed is a run
  // that is done, and gets no word at all.** The server answers that way when a
  // completion is re-sent — the first one succeeded and this client lost the
  // answer. A refusal sentence there would tell somebody their rotation failed
  // at the moment it had succeeded, and send them to press Rotate again over an
  // account that no longer needs it.
  async #complete(rotationId: string): Promise<boolean> {
    try {
      await firstValueFrom(this.#rotations.completeRotation({ rotationId }));

      return true;
    } catch (error: unknown) {
      const kind = conflictKindOf(error);

      if (kind === ROTATION_ALREADY_COMPLETED) {
        return true;
      }

      if (kind === ROTATION_INCOMPLETE) {
        return false;
      }

      throw error;
    }
  }

  // A run's identifier: a uuid this client mints, carried on the staging row and
  // stamped onto every row a chunk rewrites.
  //
  // **Neither `mintFactorId` nor `mintNarrativeRowId`, and the difference is not
  // cosmetic.** This value is not a factor, so it is not the spelling a factor's
  // envelopes are bound to; and it is not a narrative row, so version 7's index
  // locality buys nothing and would be a promise about a column that does not
  // exist. It is associated data for nothing, and nothing is sealed against it.
  #mintRotationId(): string {
    return crypto.randomUUID().toLowerCase();
  }

  // The word a screen renders, from whatever ended the run.
  //
  // **A refusal this driver made is published as it was made**; everything else
  // is read from the answer. `KeyRotationMaterialError` becomes `inconsistent`
  // for both of its reasons, including `unopened`: a begin is authorized by an
  // assertion the server verified against this account, so a factor of this
  // account that opens nothing here is the account's material failing to agree
  // with itself, and the six words on this screen deliberately carry no *try
  // another passkey* sentence.
  #wordFor(error: unknown): KeyRotationFailure {
    if (error instanceof KeyRotationRefusal) {
      return error.word;
    }

    if (error instanceof KeyRotationMaterialError) {
      return 'inconsistent';
    }

    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) {
        return 'unauthenticated';
      }

      // Status `0` is a request that never reached a server; every 5xx is a
      // server that is up and broken. Both are the state whose remedy is to
      // wait, and the run is still staged when it comes back.
      if (error.status === 0 || error.status >= 500) {
        return 'unreachable';
      }

      if (conflictKindOf(error) === FACTOR_SET_MOVED) {
        return 'factors-moved';
      }
    }

    // A judgement this client cannot act on, a body it could not read, or a
    // defect of its own. The remedy is a reload, which is the one act that
    // changes which JavaScript this tab is running.
    return 'unrecognised';
  }
}

function rowsIn(collected: Collection): number {
  return (
    collected.accounts.length +
    collected.payees.length +
    collected.categoryGroups.length +
    collected.categories.length +
    collected.transactions.length
  );
}
