// What drives a key rotation: a press, one run, and every sealed value in the
// account rewritten under a generation every factor ends up holding.
//
// The order is the act. Assemble the material from one ceremony and the two
// reads; post the begin, which stages the manifest and one seal per factor and
// answers with the inventory; collect every narrative row across the five arms;
// re-seal each under the next content key and recompute each blind index under
// the next index key; post them in chunks; post the completion; hand the
// promoted generation to `AccountKeyCustodyService`. **That last step makes no
// judgement.** The completion answers 204 carrying nothing on purpose, so
// custody re-reads the account keys and runs its own four refusals over the
// manifest the promotion filed — and a second reading of that rule here is
// exactly what this file must not grow.
//
// **Two presses and one run, because an interruption leaves nothing in the
// browser.** A rotation that lost its tab survives as server state alone — a
// staging row, a staged epoch, one seal per factor and a stamp on every row a
// chunk reached — so `resume()` walks back from that and from one fresh
// ceremony, and `begin()` is the only one of the two that posts a begin or
// draws a generation. Everything after the material is one path: the same
// collection, the same chunking, the same completion, under the identifier the
// server hands back. What differs is entirely in how the run's four values are
// got, and that is `key-rotation-material.ts`' half.
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
// this is a service holding `CryptoKey` objects and publishing signals, and the
// day somebody "completes" the empty store, this is not the slice to complete
// it with. The signals below carry a phase, two integers, a word, the date a
// staged run began, a refusal of a typed name — `taken`, or the server's own
// sentences keyed on `Name` — and, while a `same-name` block is drawn, the
// pair's list and its two records' identifiers and names. None of them is key
// material and none of them ever may be.
//
// **Every one of them is the account's that produced it.** A tab can end one
// session and establish another without a reload, and this service outlives
// both, so each value is stamped with the budget of the press or read that
// published it and reads as its resting value under any other — see
// `#publishedFor`.
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
import {
  AccountApiService,
  type AccountType,
} from '@app-core/api/account-api.service';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import {
  KeyRotationApiService,
  type KeyRotationBegunDto,
  type ResealChunkRequestBody,
  type ResealedDescribedRowBody,
  type ResealedNamedRowBody,
  type ResealedTransactionBody,
  type RotationInventoryDto,
  type StagedRotationDto,
} from '@app-core/api/key-rotation-api.service';
import { MeApiService } from '@app-core/api/me-api.service';
import { PayeesApiService } from '@app-core/api/payees-api.service';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { SessionService } from '@app-core/session/session.service';
import { writeOutcomeOf } from '@app-core/api/write-outcome';
import { firstValueFrom, type Observable } from 'rxjs';
import { AccountKeyCustodyService } from './account-key-custody.service';
import { computeBlindIndex, type BlindIndexedField } from './blind-index';
import {
  assembleKeyRotationBegin,
  assembleKeyRotationResume,
  KeyRotationMaterialError,
  type AccountKeyGeneration,
  type KeyRotationMaterial,
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
import {
  findNameCollision,
  holderOf,
  type NameArm,
  type NamedRowIndex,
} from './rotation-name-collision';
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
 * Why a run did not finish, in the seven words `docs/design/components.md`
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
  | 'factors-moved'
  | 'same-name';

/** One of the two records a `same-name` stop names, as the rename block draws it. */
export interface KeyRotationNamedRecord {
  readonly id: string;
  /** The name as stored, opened. */
  readonly name: string;
}

/**
 * The two records a run stopped on because a rotation would give them one name,
 * and which of them a typed name goes to.
 *
 * **The one opened value this service publishes, and only while the block is
 * drawn.** Two identifiers and two names: no index, no generation, nothing a
 * key could be learned from. See "Renaming one of two records with one name"
 * in `docs/design/components.md`.
 */
export interface KeyRotationNameCollision {
  readonly arm: NameArm;
  /**
   * The record whose name opened under the outgoing generation — the one not
   * yet re-encrypted — and the one the typed name goes to. Never "the one named
   * last": a chunk re-seals what its collection saw, so this client cannot know
   * which of the two was named last.
   */
  readonly renamed: KeyRotationNamedRecord;
  /** The record already under the incoming keys, which keeps its name. */
  readonly kept: KeyRotationNamedRecord;
}

/**
 * Why a typed name was refused, rendered beneath the field and never in the
 * region. `taken` is this client's own observation, made before any request
 * exists; `invalid` is the server's `400` keyed on `Name`, carried verbatim.
 */
export type KeyRotationRenameRefusal =
  | { readonly reason: 'taken' }
  | { readonly reason: 'invalid'; readonly messages: readonly string[] };

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

/**
 * A run this account has staged and not finished, as the screen that offers to
 * finish it needs it.
 *
 * **One member, and the run's identifier is deliberately not one of them.** The
 * chapter's section draws either **Rotate keys** or **Finish rotating** with the
 * date the run started in the line above it, so the date is the whole of what a
 * screen renders from this. {@link KeyRotationService.resume} reads the
 * identifier out of the server's own answer at the moment it quotes it, rather
 * than from a signal a screen has been holding since the page loaded — which
 * over the length of a run is one value able to disagree with what is really
 * staged.
 */
export interface StagedRotation {
  /**
   * When the run was begun, as the server wrote it. It stays a string all the
   * way to whatever renders it; nothing here parses a calendar.
   */
  readonly startedAtUtc: string;
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
const ROTATION_NAME_COLLISION = 'rotation_name_collision';

// The member a rename route keys its refusal of a name on, as ASP.NET spells
// the command's property on the wire.
const NAME_ERROR_KEY = 'Name';

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

// Which of a run's two generations a stored value opened under.
type Generation = NamedRowIndex['openedUnder'];

// One row's narrative, opened. Three shapes because the chunk route has three,
// and a shared one carrying optional members would let an arm send a column its
// table does not have.
//
// **A named row carries its incoming index and the generation it opened
// under**, computed once per collection: the pre-check compares the first and
// needs the second to say which row is not yet re-encrypted, and the chunk
// sends the same index the pre-check judged rather than a second computation
// of it.
interface OpenedNamedRow {
  readonly id: string;
  readonly name: string;
  readonly nameKey: string;
  readonly openedUnder: Generation;
}

// The two members an account's `PUT` carries beside its name, as the list
// served them. Neither is narrative; they travel so a rename rewrites the
// name and nothing else.
interface OpenedAccountRow extends OpenedNamedRow {
  readonly type: AccountType;
  readonly openingBalance: number;
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
  readonly accounts: readonly OpenedAccountRow[];
  readonly payees: readonly OpenedNamedRow[];
  readonly categoryGroups: readonly OpenedDescribedRow[];
  readonly categories: readonly OpenedDescribedRow[];
  readonly transactions: readonly OpenedNotedRow[];
}

// A name typed for one pair: the name, and the pair the person was shown when
// they typed it — `null` when no block was drawn, which no collection matches.
interface RenameRequest {
  readonly name: string;
  readonly shown: KeyRotationNameCollision | null;
}

// The name index of each list, and the blind-indexed field it is keyed as.
const NAME_FIELDS: Readonly<Record<NameArm, BlindIndexedField>> = {
  accounts: ACCOUNT_NAME,
  payees: PAYEE_NAME,
  categoryGroups: GROUP_NAME,
  categories: CATEGORY_NAME,
};

// A value this service published, and the budget of the press or read that
// published it — `null` for one made while the session had not said.
interface Published<T> {
  readonly value: T;
  readonly budgetId: string | null;
}

// Where a press leaves the phase when it did not throw: at its 204, or back at
// rest because there was nothing to do. The two in between are phases a run
// passes through and never ends in.
type RestingPhase = Extract<KeyRotationPhase, 'idle' | 'finished'>;

// What a screen draws from a staged run, or `null` for an account with nothing
// in flight.
//
// **A projection and not the record**, for the reason the registration payload
// projects `getClientExtensionResults()` rather than forwarding it: what a
// screen needs is one date, and the record beside it carries one copy of the
// next generation's account keys per factor. Handed to a signal, those would be
// key material on an object every injector in the app can reach, held for as
// long as a section is on screen, and — being strings — serializable by every
// inspector there is.
function stagedRotationOf(
  staged: StagedRotationDto | null,
): StagedRotation | null {
  return staged === null ? null : { startedAtUtc: staged.startedAtUtc };
}

// The one refusal a resume makes that a begin cannot, made before anything is
// opened and long before anything is posted.
//
// **The server will refuse this run's completion anyway, and that is too
// late.** `factor_set_moved` arrives after a whole account has been collected,
// re-sealed and sent — every row rewritten under a generation whose seal set
// the promotion will not accept. The comparison below costs one pass over two
// short lists and is made before the first list read.
//
// **Both directions, and they are two different events.**
//
//   * A staged seal naming a factor the account no longer holds — an
//     authenticator revoked while the run was in flight.
//   * A live factor the run staged no seal for — one enrolled while it was in
//     flight. The resume read leaves such a factor **out** of `seals` rather
//     than filling it in from its live row, so the absence is the signal; a
//     filled-in value would be the right width, the right version and the
//     generation this run is *replacing*, and a client that adopted it would
//     believe that factor already holds the new keys.
//
// Checked one way only, the other passes cleanly — and it is not the same
// comparison `key-rotation-material.ts` makes either. That module's fourth
// refusal judges the **staged** seals against the **staged** manifest, which on
// a resume is the set as it was when the run began: the two agree perfectly
// while neither of them is the set the account holds now.
//
// **The served list is what this compares against, unauthenticated, and that is
// safe in this direction only.** A set the manifest has not vouched for cannot
// be used here to make a run *proceed* — the material's own second refusal
// still has to find the served set equal to the one the live manifest declares
// — so the worst a shaped response does is refuse a run, which is a thing any
// server can do by answering anything at all.
function requireTheFactorSetHeldStill(
  sealed: readonly { readonly factorId: string }[],
  live: readonly { readonly factorId: string }[],
): void {
  const sealedIds = new Set(sealed.map((seal) => seal.factorId));
  const liveIds = new Set(live.map((entry) => entry.factorId));

  if (
    sealed.every((seal) => liveIds.has(seal.factorId)) &&
    live.every((entry) => sealedIds.has(entry.factorId))
  ) {
    return;
  }

  throw new KeyRotationRefusal(
    'factors-moved',
    'The factors this account holds are not the ones the staged run sealed for, so finishing it would leave one of them holding nothing.',
  );
}

// The tenancy a blind index is keyed inside, as a press read it.
//
// No factor supplies a budget, so a ceremony cannot clear this and the word
// that offers one would be a road that cannot help. `SessionService` argues
// the same answer for every write that meets it.
function budgetOf(budgetId: string | null): string {
  if (budgetId === null) {
    throw new KeyRotationRefusal(
      'unreachable',
      'This browser has not been told which budget it is in, so nothing can be keyed.',
    );
  }

  return budgetId;
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

// A run stopped on `same-name`, carrying the pair the block draws and, when a
// typed name was the reason, why it was refused. Its own class rather than a
// `KeyRotationRefusal` with optional members, because it is the one stop that
// publishes something beside its word.
class NameCollisionStop extends Error {
  public override readonly name = 'NameCollisionStop';

  public readonly collision: KeyRotationNameCollision;

  public readonly refusal: KeyRotationRenameRefusal | null;

  constructor(
    collision: KeyRotationNameCollision,
    refusal: KeyRotationRenameRefusal | null = null,
  ) {
    super(
      'Two records in one list would share a name under the incoming keys.',
    );

    this.collision = collision;
    this.refusal = refusal;
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

// One cell of one collection, as a map key.
function cellOf(binding: NarrativeFieldBinding): string {
  return JSON.stringify([binding.table, binding.column, binding.rowId]);
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
  // **Where the promoted generation goes, and the edge runs this way only.**
  // That class may not inject this one — nothing here is a fact about an
  // account's keys at rest — and it holds every judgement about whether the
  // pair this run produced may be published. What crosses the edge is two key
  // objects and no decision.
  readonly #custody = inject(AccountKeyCustodyService);
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

  // What this service has published, each stamped with the budget it belongs
  // to. The public signals below are these read through `#publishedFor`.
  readonly #staged = signal<Published<StagedRotation | null>>({
    value: null,
    budgetId: null,
  });

  readonly #phase = signal<Published<KeyRotationPhase>>({
    value: 'idle',
    budgetId: null,
  });

  readonly #progress = signal<Published<KeyRotationProgress>>({
    value: { resealed: 0, records: 0 },
    budgetId: null,
  });

  readonly #failure = signal<Published<KeyRotationFailure | null>>({
    value: null,
    budgetId: null,
  });

  readonly #collision = signal<Published<KeyRotationNameCollision | null>>({
    value: null,
    budgetId: null,
  });

  readonly #renameRefusal = signal<Published<KeyRotationRenameRefusal | null>>({
    value: null,
    budgetId: null,
  });

  public readonly phase: Signal<KeyRotationPhase> = this.#publishedFor(
    this.#phase,
    'idle',
  );

  public readonly progress: Signal<KeyRotationProgress> = this.#publishedFor(
    this.#progress,
    { resealed: 0, records: 0 },
  );

  public readonly failure: Signal<KeyRotationFailure | null> =
    this.#publishedFor(this.#failure, null);

  /**
   * The two records a `same-name` stop names, or `null` when the last press did
   * not end there.
   *
   * **The one opened value this service publishes, and the exception is
   * narrow.** Nothing else opened in a run outlives its own read; this pair
   * stands only while the rename block is drawn — it is replaced by the next
   * `same-name`, cleared the moment a rename carried for it lands or a resume
   * finds no pair at all, cleared when a press ends any other way, and hidden
   * from any session but the account's that produced it. A ceremony that fails reaches
   * no press, so it leaves the pair standing, which is the block standing.
   */
  public readonly collision: Signal<KeyRotationNameCollision | null> =
    this.#publishedFor(this.#collision, null);

  /**
   * Why the name the last press carried was refused, or `null`. Rendered
   * beneath the field; it never accompanies any word but `same-name`.
   */
  public readonly renameRefusal: Signal<KeyRotationRenameRefusal | null> =
    this.#publishedFor(this.#renameRefusal, null);

  /**
   * The run there is to finish, or `null` when there is none — which is what
   * decides **which control the section draws**.
   *
   * It is `null` until something reads it: a browser that has just loaded knows
   * of no run, and an interrupted rotation survives as server state alone. It
   * is written by {@link readStagedRotation}, by {@link resume}'s own read, by
   * a {@link begin} as it stops, and at the two moments where this service
   * knows without reading — a run it drove to its 204, and a refusal after
   * which the section's own copy tells somebody to start again rather than to
   * finish.
   *
   * **A begin publishes nothing while it walks, only what it leaves on file
   * when it stops**: the date its own answer carried once it got past its
   * POST, what its read before the POST found when the POST was refused, and
   * one read back when the POST never answered — because whether that one
   * staged a run is unknowable without asking. Published at the 2xx instead,
   * the control's label would flip from **Rotate keys** to **Finish rotating**
   * under the finger that pressed it.
   */
  public readonly staged: Signal<StagedRotation | null> = this.#publishedFor(
    this.#staged,
    null,
  );

  /**
   * Whether this account has a run in flight in this tab — what a screen draws
   * from.
   *
   * **One predicate with one owner.** The Rotate control's `disabled` and
   * `aria-busy` bind this, and the three content screens read it beside
   * custody's lockedness. A screen deriving its own from {@link phase} would be
   * a second definition of the same fact, which is the drift the Unlock control
   * already paid for once.
   *
   * **Read through the budget rule, like every value a screen draws.** A run
   * walking for the account this tab signed out of says nothing about the rows
   * of the one signed in now, and a list replaced by the run's notice there
   * would be a screen blocked by somebody else's work. What guards this object
   * is {@link walking}, never this.
   */
  public readonly running: Signal<boolean> = computed(() =>
    isWalking(this.phase()),
  );

  /**
   * Whether this object is walking a run, whoever's account it is for — **the
   * re-entrancy guard, and never what a screen draws from.**
   *
   * **The one signal not read through the budget rule**, because what it
   * guards is this object rather than an account: a run in flight holds both
   * generations in two fields a second press would overwrite, and a session
   * that changed under it changes nothing about that. It says only that a run
   * is going — nothing of whose, how far, or how it stopped — so a press
   * handler refuses on it, and no content screen may read it.
   */
  public readonly walking: Signal<boolean> = computed(() =>
    isWalking(this.#phase().value),
  );

  // A published value as the session in this tab may read it: the value, when
  // the session is the account's that published it, and `rest` otherwise.
  //
  // **Hidden rather than dropped, and a `computed` rather than a clearing.**
  // Clearing on a session's end has one owner, `SessionService.ended()`, and
  // that class cannot reach this one — this one injects it, and the edge would
  // be a cycle — while an `effect()` clearing from here would be a second owner
  // whose timing injection order decides. So nothing is cleared: a value stops
  // being readable the moment the session is not its account's, and is readable
  // again if that account signs back in, which is the run on file still standing
  // where it stopped.
  //
  // **Two conditions, and the status one is not implied by the budget one.**
  // `ended()` drops the budget and the status together, but only `anonymous` is
  // a session that ended — `unknown` and `unreachable` keep everything, or a
  // blinked request would take a typed name away. The budget is compared by
  // equality, so a current `null` matches no value stamped with a budget: a
  // session that has not yet said which account it is has not said it is this
  // one. A value stamped `null` is one a press published while it, too, had no
  // budget — the `unreachable` word, whose run was refused before anything of an
  // account was read — and it reads under a `null` budget only.
  #publishedFor<T>(source: Signal<Published<T>>, rest: T): Signal<T> {
    return computed(() => {
      const published = source();

      return this.#session.status() !== 'anonymous' &&
        this.#session.budgetId() === published.budgetId
        ? published.value
        : rest;
    });
  }

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
    await this.#press(async (budgetId) => this.#drive(ceremony, budgetId));
  }

  /**
   * Reads whether this account has a run to finish, and publishes it.
   *
   * **It is the read the section makes before it draws anything**, because a
   * rotation that was interrupted survives only as server state — a staging
   * row, a staged epoch and one seal per factor, and nothing in the browser. It
   * posts nothing, opens nothing and touches no key material: what it answers
   * decides which of two controls a person is offered, and that question has no
   * cryptography in it.
   *
   * **A read that did not happen publishes "nothing to finish", and no word.**
   * The seven refusal words each say what became of a *run*, and there is no run
   * here to have become anything — a sentence about a rotation that stopped,
   * rendered at rest before anybody pressed anything, would be false. What the
   * failed read costs is one wrongly-drawn control, and that costs little:
   * {@link begin} makes this very read again and carries a staged generation
   * forward rather than drawing one, so a press of **Rotate keys** over a run
   * that is really there picks that run up — under its own identifier, and
   * without touching the staged seals' generation — whether or not the
   * account's factor set held still, because a begin re-stages to the set it
   * finds. A 401 is not swallowed by this —
   * `sessionExpiryInterceptor` owns that answer for every request in the
   * product and acts on it whatever this method does with the rejection.
   */
  public async readStagedRotation(): Promise<void> {
    // The budget the read was asked under, not the one standing when it lands:
    // an answer arriving after the session changed is still the account's that
    // the cookie on the request named.
    const budgetId = this.#session.budgetId();

    try {
      const state = await firstValueFrom(this.#rotations.getRotationState());

      this.#staged.set({ value: stagedRotationOf(state.rotation), budgetId });
    } catch {
      this.#staged.set({ value: null, budgetId });
    }
  }

  /**
   * Picks up the run this account has staged and drives it to its completion,
   * or publishes the word it stopped on.
   *
   * `ceremony` is what a passkey assertion just yielded, and **the whole
   * ceremony rather than the key it carries**, though nothing here posts its
   * payload: the chunk and the completion carry no assertion, because the
   * authorization for this run was created by the begin and is on file. What
   * the parameter's type buys is that a resume cannot be driven from a key
   * something other than an authenticator produced — the account's own content
   * key, say, which custody holds and which opens none of the staged seals but
   * would be the right shape to try.
   *
   * **Nothing is begun, no epoch is minted and no identifier is drawn.** The
   * staged seals are the only copy of the generation every row the interrupted
   * run already re-sealed is sealed under; a begin overwrites them in place, so
   * a resume that posted one would take those rows' only key with it. This
   * quotes the run the server hands back and re-collects, re-seals and completes
   * under it.
   *
   * `rename` is **Rename and finish**: the name a person typed for the pair
   * {@link collision} published. It is spent on the first collection only, and
   * only when that collection finds the very pair the person was shown — a
   * different pair is republished and nothing is renamed, and no pair at all
   * means somebody fixed it elsewhere and the run carries on. Before anything
   * is posted the name is compared against every record in that list; after it
   * lands, the re-collection that sees it does not spend one of the run's
   * passes.
   *
   * It resolves rather than rejecting, always — {@link begin}'s rule, for
   * {@link begin}'s reason.
   */
  public async resume(
    ceremony: PasskeyAssertionCeremony,
    rename?: { readonly name: string },
  ): Promise<void> {
    // The pair the person was shown when they typed, read before the press
    // clears anything: the name is an answer to *that* pair and no other. Read
    // as the screen reads it, so a pair another account's run left behind is
    // `null` here — nothing was shown, and nothing is renamed.
    const request: RenameRequest | null =
      rename === undefined
        ? null
        : { name: rename.name, shown: this.collision() };

    await this.#press(async (budgetId) =>
      this.#pickUp(ceremony, request, budgetId),
    );
  }

  // The envelope both presses share: the phase, the bar, the word, and the two
  // generations ending with the run.
  //
  // **One owner, because these are the whole of what a screen binds.** A second
  // copy inside the resume would be five chances for the two controls to
  // publish different things about one act, and the two of them are one act
  // with two entry points.
  //
  // The leg answers with the phase it ended in. `'finished'` is a run that
  // reached its 204; `'idle'` is a press that found there was nothing to do,
  // which is not a failure and gets no word.
  //
  // **The pair ends with any press that does not end on `same-name`.** It is
  // left standing while a press runs, because the block stays drawn — its field
  // `readonly` — until the rename it carries lands or the collection finds no
  // pair, which `#renamedAndRecollected` clears as it happens; the refusal
  // beneath the field is cleared at once, because one press carries one name.
  //
  // **Everything a press publishes is stamped with the budget read as it
  // starts**, never with the one standing when a value lands: a run is keyed
  // inside the budget it began in, and what it reports is that account's
  // however long it takes.
  async #press(
    leg: (budgetId: string) => Promise<RestingPhase>,
  ): Promise<void> {
    const budgetId = this.#session.budgetId();
    const publish = <T>(value: T): Published<T> => ({ value, budgetId });

    this.#phase.set(publish('collecting'));
    this.#progress.set(publish({ resealed: 0, records: 0 }));
    this.#failure.set(publish(null));
    this.#renameRefusal.set(publish(null));

    try {
      const resting = await leg(budgetOf(budgetId));

      this.#collision.set(publish(null));

      if (resting === 'finished') {
        // **Custody of the promoted generation, and it happens here.** Both
        // presses end at the same 204 and the `finally` below drops both
        // fields, so this is the one frame that is holding the pair and knows
        // the run reached its completion. A run that finished and left the tab
        // locked is a broken end state: every row in the account has just been
        // rewritten under a generation nothing else in this browser holds, so
        // the three content screens would show a locked account and the way
        // back would be a second ceremony for keys that are already here.
        //
        // **What it hands over is the pair and nothing else.** Whether custody
        // may publish it is decided by the four refusals that class runs over
        // its own re-read of the account keys, and this driver makes none of
        // them: a second reading here would be a second, weaker definition of
        // a rule `docs/business-logic/account-keys.md` spends a chapter on. It
        // is `void` for the same reason `unlock` is, so nothing of that gate's
        // answer lands in this run's word.
        this.#adoptThePromotedGeneration();

        // **The one place this service may say so without reading it.** The
        // staged generation is the live one now and this client is the reason,
        // so "there is nothing to finish" is an observation rather than a guess
        // — and a section still offering **Finish rotating** over a run that
        // has just completed sends somebody through a whole account again.
        this.#staged.set(publish(null));
      }

      this.#phase.set(publish(resting));
    } catch (error: unknown) {
      const failure = this.#wordFor(error);

      if (error instanceof NameCollisionStop) {
        this.#collision.set(publish(error.collision));
        this.#renameRefusal.set(publish(error.refusal));
      } else {
        this.#collision.set(publish(null));
      }

      if (failure === 'factors-moved') {
        // **The section stops offering to finish, because its own copy says to
        // start again.** *Start it again from here* over a control labelled
        // **Finish rotating** is a screen disagreeing with itself, and pressing
        // that control meets the same refusal for ever: the staged seals name a
        // set the account no longer has, and no read changes that.
        //
        // **What the control it draws instead does.** A press of **Rotate
        // keys** over this state reaches {@link begin}, which finds the run
        // still on file, recovers its generation out of a surviving factor's
        // staged seal and re-stages *that* generation to the factor set the
        // account holds now — under the same `rotationId`, so every row the
        // interrupted run stamped is still a row this run has done. That is
        // what makes the word's *the records already re-encrypted stay that
        // way* true. It is not a third entry point: the repair **is** a begin,
        // and the only thing it does differently from any other begin is that
        // it cannot draw a generation while one is staged.
        this.#staged.set(publish(null));
      }

      this.#phase.set(publish('idle'));
      this.#failure.set(publish(failure));
    } finally {
      // **The generations end with the run**, and the hand-over above happens
      // before this line rather than after it: the pair custody takes is read
      // off these two fields, and a hand-over written down here would have
      // nothing left to hand over.
      this.#current = null;
      this.#next = null;
    }
  }

  // Hands the generation this run promoted to the class that holds the
  // account's keys.
  //
  // The throw is unreachable and is written the way its three siblings are
  // written: a leg answers `'finished'` only after the completion it posted was
  // accepted, and neither leg posts anything before it has both generations. A
  // silent `return` there would be a finished run that quietly left the tab
  // locked, which is the state this whole step exists to prevent.
  #adoptThePromotedGeneration(): void {
    const next = this.#next;

    if (next === null) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'This run holds no generation to take custody of.',
      );
    }

    this.#custody.adoptRotated(next.contentKey, next.indexKey);
  }

  async #drive(
    ceremony: PasskeyAssertionCeremony,
    budgetId: string,
  ): Promise<RestingPhase> {
    const custody = await firstValueFrom(this.#keys.getAccountKeys());
    const state = await firstValueFrom(this.#rotations.getRotationState());
    // **What is on file, held and not yet published.** Every stop below
    // publishes what it leaves on file, and none of it is published while the
    // run walks: a run found here flipping the label mid-press would turn
    // **Rotate keys** into **Finish rotating** under the finger.
    const onFile = stagedRotationOf(state.rotation);
    const leaveOnFile = (staged: StagedRotation | null): void => {
      this.#staged.set({ value: staged, budgetId });
    };

    let material: KeyRotationMaterial;

    try {
      material = await assembleKeyRotationBegin(
        ceremony.keyEncryptionKey,
        custody,
        state,
      );
    } catch (error: unknown) {
      // Refused before anything was posted, so what is on file is what the
      // read found.
      leaveOnFile(onFile);

      throw error;
    }

    this.#current = material.current;
    this.#next = material.next;

    // **A run already in flight keeps its identifier.** The material module has
    // just carried that run's generation forward rather than drawing a fresh one
    // — a second begin overwrites the staged seals in place, and every row the
    // interrupted run already rewrote would then open under nothing at all — so
    // the rows it stamped are rows this run really has done. Minting a second
    // identifier here would orphan those stamps and make the design chapter's
    // *the records already re-encrypted stay that way* false.
    const rotationId = state.rotation?.rotationId ?? this.#mintRotationId();
    let begun: KeyRotationBegunDto;

    try {
      begun = await firstValueFrom(
        this.#rotations.beginRotation({
          ...ceremony.payload,
          rotationId,
          manifest: material.manifest,
          rotationEpoch: material.rotationEpoch,
          seals: material.seals,
        }),
      );
    } catch (error: unknown) {
      leaveOnFile(await this.#onFileAfterARefusedBegin(error, onFile));

      throw error;
    }

    try {
      await this.#run(
        rotationId,
        budgetId,
        begun.inventory,
        begun.maxChunkBytes,
        null,
      );
    } catch (error: unknown) {
      // **Past its POST the begin has staged a run, whatever stopped it**, so
      // the section has to offer to finish that one — dated by the begin's own
      // answer rather than by a read, which is the one request that fails
      // exactly when the network does. A `factors-moved` overrides this in
      // `#press`, because that word's copy says to start again.
      leaveOnFile({ startedAtUtc: begun.startedAtUtc });

      throw error;
    }

    return 'finished';
  }

  // What a begin whose POST did not answer 2xx leaves on file.
  //
  // **A refusal staged nothing**, so the run the read before the POST found —
  // or none — is still the whole of it. **A request with no answer may have
  // staged one**, and whether it did is unknowable without asking, so this asks
  // once; a read that fails the same way leaves the pre-POST answer standing,
  // which is the last thing this press observed. "No answer" is exactly
  // `unreachable`'s definition, read through `#wordFor` so there is one.
  async #onFileAfterARefusedBegin(
    error: unknown,
    onFile: StagedRotation | null,
  ): Promise<StagedRotation | null> {
    if (this.#wordFor(error) !== 'unreachable') {
      return onFile;
    }

    try {
      const state = await firstValueFrom(this.#rotations.getRotationState());

      return stagedRotationOf(state.rotation);
    } catch {
      return onFile;
    }
  }

  // The resume: everything a begin does except begin.
  //
  // **The walk is back from what the server holds, and the order is the whole
  // of it.** The state read comes first and costs one request, because an
  // account with nothing staged has nothing for a ceremony to open and no key
  // material worth reading. Then the account keys, then the refusal below, then
  // the material — which pairs the factor's **live** wrapped private key
  // against its **live** encapsulated value for the generation in force and
  // against the **staged** one for the generation replacing it, out of the one
  // ceremony this press ran. Then the run, quoting the identifier the server
  // handed back.
  //
  // **The rows an interrupted run already rewrote are why both generations are
  // needed at once**, and `#open` is where that is spent: a resumed collection
  // meets an account that is part one generation and part the other.
  async #pickUp(
    ceremony: PasskeyAssertionCeremony,
    rename: RenameRequest | null,
    budgetId: string,
  ): Promise<RestingPhase> {
    const state = await firstValueFrom(this.#rotations.getRotationState());
    const staged = state.rotation;

    this.#staged.set({ value: stagedRotationOf(staged), budgetId });

    if (staged === null) {
      // **Nothing to finish, and no word.** The run this press was offering to
      // pick up is not on file: either another tab completed it, or this
      // section was drawn from a read that has since gone stale. Every one of
      // the seven words says a *run* failed, and none of them is true of a run
      // that is not there — so the phase goes back to rest, the signal above
      // has just republished that there is nothing staged, and the control the
      // section draws becomes **Rotate keys**.
      return 'idle';
    }

    const custody = await firstValueFrom(this.#keys.getAccountKeys());

    requireTheFactorSetHeldStill(staged.seals, custody.factors);

    // **The staged run itself, which is the entry point that cannot re-stage
    // anything.** A resume finishes the run on file, so what it needs is that
    // run's manifest and seals restated byte for byte; the begin's entry point
    // over the same value would re-encapsulate to the live set and file a new
    // manifest, which is the repair and not this press.
    const material = await assembleKeyRotationResume(
      ceremony.keyEncryptionKey,
      custody,
      staged,
    );

    this.#current = material.current;
    this.#next = material.next;

    await this.#run(
      // Quoted, never minted: the rows the interrupted run stamped carry this
      // identifier, and the completeness gate counts a row as done only under
      // the run that stamped it.
      staged.rotationId,
      budgetId,
      staged.inventory,
      staged.maxChunkBytes,
      rename,
    );

    return 'finished';
  }

  async #run(
    rotationId: string,
    budgetId: string,
    inventory: RotationInventoryDto,
    maxChunkBytes: number,
    rename: RenameRequest | null,
  ): Promise<void> {
    // **A tripwire that costs nothing today and refuses a run that could never
    // finish.** The chunk route has five arms and no budget arm — FR-099 keeps
    // `budgets` at `UPDATE (name)`, so a sixth arm would break a requirement —
    // and `budgets.name` is `NULL` on every row this product creates, so the
    // published count is 0 on every account there is. The day it is not, no
    // chunk this client can send stamps that row and the completeness gate
    // refuses forever; better not driven than driven and unfinishable.
    //
    // **Here rather than beside the begin, because both presses spend an
    // inventory.** A begin reads it off the answer to its own post and a resume
    // off the staged run, and a refusal written on one path only would let the
    // other walk an account it could never finish.
    if (inventory.budgets !== 0) {
      throw new KeyRotationRefusal(
        'unrecognised',
        `The inventory published ${String(inventory.budgets)} budget rows to re-seal, and no chunk this client sends can carry one.`,
      );
    }

    for (let pass = 1; pass <= COLLECTION_PASSES; pass += 1) {
      this.#phase.set({ value: 'collecting', budgetId });

      let collected = await this.#collect(budgetId);

      // **Judged on the first pass only, because "nothing is posted" is a claim
      // only the first pass can make.** By the second, chunks have landed; and
      // a row deleted by another tab between two passes would make this
      // comparison refuse a run that is perfectly healthy.
      if (pass === 1) {
        this.#requireEveryCountedRow(inventory, collected);
      }

      // **The rename belongs to the first collection and spends no pass.** It
      // is one request between two collections of the same pass: the second
      // is what sees the new name, and counting it as a pass would take one
      // from a run that has to absorb rows arriving mid-run exactly as it
      // would have without the rename.
      if (pass === 1 && rename !== null) {
        collected = await this.#renamedAndRecollected(
          collected,
          rename,
          budgetId,
        );
      }

      requireEveryNameDistinct(collected);

      this.#progress.set({
        value: { resealed: 0, records: rowsIn(collected) },
        budgetId,
      });
      this.#phase.set({ value: 'resealing', budgetId });

      if (!(await this.#sent(rotationId, collected, maxChunkBytes))) {
        continue;
      }

      this.#phase.set({ value: 'finishing', budgetId });

      if (await this.#complete(rotationId)) {
        return;
      }
    }

    throw new KeyRotationRefusal(
      'unfinished',
      `The account went on changing across ${String(COLLECTION_PASSES)} passes, so this run never caught up with it.`,
    );
  }

  // Spends a typed name on the pair it was typed for, and answers the
  // collection that sees it.
  //
  // **Three outcomes, and only one of them writes.** No pair: somebody fixed it
  // elsewhere, nothing is renamed and the collection in hand carries on. A
  // different pair: a name typed for one pair answers a question nobody is
  // asking now, so the new pair is published and nothing is written. The same
  // pair: the rename, then a fresh collection.
  //
  // **The pair goes the moment it stops being true**, not when the press
  // answers: at once when there is none, and as soon as the rename lands —
  // before the re-collection, which is a whole account of opens. The block's
  // lead line names two records with one name, and a run walking on under
  // **Finish rotating** beneath a sentence that is no longer so would be a
  // screen disagreeing with the server.
  async #renamedAndRecollected(
    collected: Collection,
    rename: RenameRequest,
    budgetId: string,
  ): Promise<Collection> {
    const found = publishedCollisionIn(collected);

    if (found === null) {
      this.#collision.set({ value: null, budgetId });

      return collected;
    }

    if (rename.shown === null || !isSamePair(found, rename.shown)) {
      throw new NameCollisionStop(found);
    }

    await this.#rename(collected, found, rename.name, budgetId);

    this.#collision.set({ value: null, budgetId });

    return this.#collect(budgetId);
  }

  // Renames the record a pair says is renamed, through that list's own route.
  //
  // **Sealed under the incoming content key and keyed under the incoming index
  // key**, so the server's unique index compares it against every record the
  // run already re-encrypted. It cannot compare it against a record still
  // under the outgoing key — those indexes were taken under a different key —
  // so this run compares it against **every** record in the list first, and a
  // hit is refused beneath the field with nothing posted.
  //
  // The route's `400` keyed on `Name` is the server's sentence and travels
  // verbatim; every other answer is the run's ordinary word.
  async #rename(
    collected: Collection,
    pair: KeyRotationNameCollision,
    name: string,
    budgetId: string,
  ): Promise<void> {
    const field = NAME_FIELDS[pair.arm];
    const id = pair.renamed.id;
    const nameKey = await this.#index({ ...field, budgetId }, name);

    if (holderOf(nameArmsOf(collected)[pair.arm], nameKey, id) !== null) {
      throw new NameCollisionStop(pair, { reason: 'taken' });
    }

    const sealed = await this.#seal({ ...field, rowId: id }, name);

    try {
      await firstValueFrom(
        await this.#renameRequest(collected, pair.arm, id, sealed, nameKey),
      );
    } catch (error: unknown) {
      const messages = nameMessagesOf(error);

      if (messages === null) {
        throw error;
      }

      throw new NameCollisionStop(pair, { reason: 'invalid', messages });
    }
  }

  // The request that renames one row, on the route that list already has.
  //
  // **Every route but the payee's is a full `PUT`**, so what the row already
  // holds travels beside the name: an account's type and opening balance as
  // the list served them, and a group's or category's note re-sealed under the
  // incoming key — `null` staying `null`. On that verb an absent note is a 204
  // having cleared it, and a note left under the outgoing key would be a row
  // the completion strands.
  async #renameRequest(
    collected: Collection,
    arm: NameArm,
    id: string,
    name: string,
    nameKey: string,
  ): Promise<Observable<void>> {
    switch (arm) {
      case 'accounts': {
        const row = rowIn(collected.accounts, id);

        return this.#accountsApi.updateAccount(id, {
          name,
          nameKey,
          type: row.type,
          openingBalance: row.openingBalance,
        });
      }
      case 'payees':
        return this.#payeesApi.renamePayee(id, { name, nameKey });
      case 'categoryGroups':
        return this.#categoryGroupsApi.updateCategoryGroup(id, {
          name,
          nameKey,
          description: await this.#resealedNote(
            GROUP_NOTE,
            rowIn(collected.categoryGroups, id),
          ),
        });
      case 'categories':
        return this.#categoriesApi.updateCategory(id, {
          name,
          nameKey,
          description: await this.#resealedNote(
            CATEGORY_NOTE,
            rowIn(collected.categories, id),
          ),
        });
    }
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
  async #collect(budgetId: string): Promise<Collection> {
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

    // Which generation each value opened under, by cell, for this collection
    // only. It holds no plaintext and goes with the call.
    const generations = new Map<string, Generation>();
    const opener = await openNarrativeBatch(
      requests,
      this.#opener(generations),
    );

    const open = async (
      binding: NarrativeFieldBinding,
      wire: string,
    ): Promise<string> => plaintextOf(await opener(binding, wire));

    const named = async (
      field: BlindIndexedField,
      id: string,
      wire: string,
    ): Promise<OpenedNamedRow> => {
      const binding = { ...field, rowId: id };
      const name = await open(binding, wire);
      const openedUnder = generations.get(cellOf(binding));

      if (openedUnder === undefined) {
        // Unreachable: every value this collection opens goes through the
        // opener that records it. Written as a refusal rather than a default,
        // because a guessed generation decides which record a person renames.
        throw new KeyRotationRefusal(
          'inconsistent',
          `${binding.table}.${binding.column} on ${id} opened under no recorded generation.`,
        );
      }

      return {
        id,
        name,
        nameKey: await this.#index({ ...field, budgetId }, name),
        openedUnder,
      };
    };

    return {
      accounts: await Promise.all(
        accounts.items.map(async (row) => ({
          ...(await named(ACCOUNT_NAME, row.id, row.name)),
          type: row.type,
          openingBalance: row.openingBalance,
        })),
      ),
      payees: await Promise.all(
        payees.items.map(async (row) => named(PAYEE_NAME, row.id, row.name)),
      ),
      categoryGroups: await Promise.all(
        categoryGroups.items.map(async (row) => ({
          ...(await named(GROUP_NAME, row.id, row.name)),
          description:
            row.description === null
              ? null
              : await open({ ...GROUP_NOTE, rowId: row.id }, row.description),
        })),
      ),
      categories: await Promise.all(
        categories.items.map(async (row) => ({
          ...(await named(CATEGORY_NAME, row.id, row.name)),
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
  //
  // **Which of the two opened it is recorded into `generations`**, because the
  // name pre-check needs it: a pair is one row under the incoming keys and one
  // still under the outgoing, and the generation is the only fact that says
  // which is which. It says nothing about which was named last — a chunk
  // re-seals what its collection saw — so the renamed row is picked by it
  // alone: the one not yet re-encrypted.
  #opener(generations: Map<string, Generation>): NarrativeOpener {
    return async (binding, wire): Promise<NarrativeText> => {
      const opened = await this.#open(binding, wire);

      generations.set(cellOf(binding), opened.under);

      return { state: 'text', value: opened.value };
    };
  }

  async #open(
    binding: NarrativeFieldBinding,
    wire: string,
  ): Promise<{ readonly value: string; readonly under: Generation }> {
    const current = this.#current;
    const next = this.#next;

    if (current === null || next === null) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'This run holds no generation to open under.',
      );
    }

    try {
      return {
        value: await openNarrativeField(current.contentKey, wire, binding),
        under: 'current',
      };
    } catch (cause: unknown) {
      try {
        return {
          value: await openNarrativeField(next.contentKey, wire, binding),
          under: 'next',
        };
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

  // One pass's send. `true` when every chunk landed, `false` when a chunk was
  // refused as a name collision and the pass is spent.
  //
  // **`rotation_name_collision` names no row, so it gets no word of its own and
  // no retry of the post.** A name landed between this pass's collection and its
  // send; the refused chunk wrote nothing, and re-posting it would carry the same
  // pair forever. The next collection is what can see the pair, so the pass is
  // spent the way a row arriving mid-run spends one, and the chunks after the
  // refused one are not sent — they were sealed from the same stale collection.
  // Read from the kind alone, never from the problem document's prose.
  async #sent(
    rotationId: string,
    collected: Collection,
    maxChunkBytes: number,
  ): Promise<boolean> {
    try {
      await this.#send(rotationId, collected, maxChunkBytes);

      return true;
    } catch (error: unknown) {
      if (conflictKindOf(error) === ROTATION_NAME_COLLISION) {
        return false;
      }

      throw error;
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
      // The stamp the pass set travels unchanged: the same run, the same
      // account.
      this.#progress.update((progress) => ({
        ...progress,
        value: {
          ...progress.value,
          resealed: progress.value.resealed + carried,
        },
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
      const body = await this.#namedBody(ACCOUNT_NAME, row);

      await room(entryBytes(body));
      accounts.push(body);
    }

    for (const row of collected.payees) {
      const body = await this.#namedBody(PAYEE_NAME, row);

      await room(entryBytes(body));
      payees.push(body);
    }

    for (const row of collected.categoryGroups) {
      const body = await this.#describedBody(GROUP_NAME, GROUP_NOTE, row);

      await room(entryBytes(body));
      categoryGroups.push(body);
    }

    for (const row of collected.categories) {
      const body = await this.#describedBody(CATEGORY_NAME, CATEGORY_NOTE, row);

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
    row: OpenedNamedRow,
  ): Promise<ResealedNamedRowBody> {
    // The index the pre-check judged, not a second computation of it.
    return {
      id: row.id,
      name: await this.#seal({ ...field, rowId: row.id }, row.name),
      nameKey: row.nameKey,
    };
  }

  async #describedBody(
    nameField: BlindIndexedField,
    noteField: typeof GROUP_NOTE | typeof CATEGORY_NOTE,
    row: OpenedDescribedRow,
  ): Promise<ResealedDescribedRowBody> {
    return {
      ...(await this.#namedBody(nameField, row)),
      // **The member is always present and `null` is not a way to clear it.**
      // The server refuses a reseal that changes whether a row holds a note in
      // either direction, so sending the member always is what keeps an arm's
      // member set a fact about this client rather than about which rows a chunk
      // happened to name.
      description: await this.#resealedNote(noteField, row),
    };
  }

  // A described row's note under the incoming content key, or `null` for a
  // row that holds none — never `''`, which is not an envelope.
  async #resealedNote(
    noteField: typeof GROUP_NOTE | typeof CATEGORY_NOTE,
    row: OpenedDescribedRow,
  ): Promise<string | null> {
    return row.description === null
      ? null
      : this.#seal({ ...noteField, rowId: row.id }, row.description);
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
  // with itself, and the seven words on this screen deliberately carry no *try
  // another passkey* sentence.
  #wordFor(error: unknown): KeyRotationFailure {
    if (error instanceof KeyRotationRefusal) {
      return error.word;
    }

    if (error instanceof NameCollisionStop) {
      return 'same-name';
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

// The four indexed lists of one collection as the pure comparison takes them:
// identifiers, incoming indexes and generations, and no name.
function nameArmsOf(
  collected: Collection,
): Readonly<Record<NameArm, readonly NamedRowIndex[]>> {
  const indexOf = (row: OpenedNamedRow): NamedRowIndex => ({
    id: row.id,
    nameKey: row.nameKey,
    openedUnder: row.openedUnder,
  });

  return {
    accounts: collected.accounts.map(indexOf),
    payees: collected.payees.map(indexOf),
    categoryGroups: collected.categoryGroups.map(indexOf),
    categories: collected.categories.map(indexOf),
  };
}

// The pair in one collection, as the block draws it, or `null` for none.
//
// **Built member by member**, so what reaches the signal is exactly two
// identifiers and two names; a spread of the collected row would carry its
// index and its generation onto an object every injector can reach.
function publishedCollisionIn(
  collected: Collection,
): KeyRotationNameCollision | null {
  const found = findNameCollision(nameArmsOf(collected));

  if (found === null) {
    return null;
  }

  const rows: readonly OpenedNamedRow[] = collected[found.arm];
  const record = (id: string): KeyRotationNamedRecord => {
    const row = rows.find((candidate) => candidate.id === id);

    if (row === undefined) {
      throw new KeyRotationRefusal(
        'inconsistent',
        'The comparison named a row this collection does not hold.',
      );
    }

    return { id: row.id, name: row.name };
  };

  return {
    arm: found.arm,
    renamed: record(found.renamed.id),
    kept: record(found.kept.id),
  };
}

// Whether a collection found the very pair a person was shown: the same list
// and the same two records in the same roles.
//
// **Judged by identifier and never by name.** A stale tab re-spelling one of
// the two leaves the same two rows colliding, and the name typed for them
// still answers the question the block asked; a different record under the
// very same spelling is a different question, and only its identifier says so.
function isSamePair(
  found: KeyRotationNameCollision,
  shown: KeyRotationNameCollision,
): boolean {
  return (
    found.arm === shown.arm &&
    found.renamed.id === shown.renamed.id &&
    found.kept.id === shown.kept.id
  );
}

// One collected row by identifier, or a refusal: every caller is holding an
// identifier the same collection just produced.
function rowIn<TRow extends { readonly id: string }>(
  rows: readonly TRow[],
  id: string,
): TRow {
  const row = rows.find((candidate) => candidate.id === id);

  if (row === undefined) {
    throw new KeyRotationRefusal(
      'inconsistent',
      `This collection holds no row ${id} to rename.`,
    );
  }

  return row;
}

// The server's sentences for a name it refused, verbatim, or `null` for any
// answer that is not a validation refusal keyed on `Name`. Read through
// `writeOutcomeOf`, the one place this client reads a refusal out of an API
// answer.
function nameMessagesOf(error: unknown): readonly string[] | null {
  const outcome = writeOutcomeOf(error);

  return outcome.state === 'invalid'
    ? (outcome.errors.get(NAME_ERROR_KEY) ?? null)
    : null;
}

// **The run finds the pair before it posts anything; the server's refusal is
// only the backstop.** Two rows whose names this pass would re-seal onto one
// incoming index are refused whole by the chunk route on every send, and its
// `rotation_name_collision` names no row — so a run left to meet it would spend
// its passes and end on a word whose remedy cannot help. Made after every
// collection and before any chunk of that pass.
function requireEveryNameDistinct(collected: Collection): void {
  const collision = publishedCollisionIn(collected);

  if (collision !== null) {
    throw new NameCollisionStop(collision);
  }
}

// Whether a phase is one a run passes through rather than one a press rests in.
function isWalking(phase: KeyRotationPhase): boolean {
  return phase !== 'idle' && phase !== 'finished';
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
