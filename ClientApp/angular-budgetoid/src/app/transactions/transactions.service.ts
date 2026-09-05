// The transactions screen's state, and the second service in this product that
// seals what it writes and opens what it reads.
//
// **The service mints, seals, indexes and resolves; the component never does.**
// A screen hands over the text somebody typed and nothing else. A component
// holding a minted row id would have to keep it alive across a form reset and a
// cancel, and that id is the associated data every envelope on the row is
// sealed against — so the one place it can be dropped is the one place it must
// not be.
//
// **Resolving the counterparty is this client's job now, and it is a
// capability that moved rather than one that was added.** The server used to
// find-or-create a payee by name inside the transaction write; it cannot any
// more. `payees.name` is an AEAD envelope drawn under a fresh nonce, so two
// seals of one name are different bytes; the digest that *is* stable is taken
// under the account's index key, which lives here; and the case folding the old
// lookup leant on left with the column's `case_insensitive` collation, because
// `bytea` is not collatable. So the browser folds, indexes, matches against the
// list it already holds, and posts `POST /api/payees` only on a miss.
//
// **The match is on the blind index and never on decrypted text.** Case folding
// comes free that way — `trader joe's` and `Trader Joe's` normalize to one
// message and key to one value — and the local match and the server's unique
// index are then decided by the same bytes, so they cannot disagree about what
// "the same name" means. Text equality agrees with this on almost every name a
// person types and disagrees on exactly the ones where the difference decides a
// match; the symptom is a second payee for one counterparty, on the column
// whose whole purpose is that equal names collide, and a blind index cannot be
// recomputed after the fact because the plaintext behind it is encrypted.
//
// **Every exit that writes nothing says so, and `add` answers a word rather
// than `void`.** Five paths end this write with no row on the server, and an
// exit that returns in silence is from outside indistinguishable from a write
// that landed — so a screen with nothing to read has no way to keep the form
// it is about to clear. The severe case is not exotic: a payee whose own
// name does not open carries no index, can never match, 409s, re-reads to the
// same answer and abandons, so the person dealing with that counterparty can
// never record a transaction against it again.
//
// **The word is now the product's word, and the abandon is no longer one
// state.** `docs/design/components.md`, "A write that does not happen", is the
// authority: a screen receives a {@link WriteOutcome} and never reads a status
// code to find out which. So the five exits above are classified rather than
// counted — a browser holding no keys is `locked`, a duplicate counterparty
// nothing can adopt is `duplicate-name`, a server that failed to answer is
// `unreachable`, and an answer nobody here can read is `unreadable` — and each
// of those is a different next step for a person. `#report` stays beside them
// as the console line it always was: it is what a developer reads, not what a
// screen renders, and the two were only ever one thing because there was no
// second channel.
//
// **Both of this write's row ids are drawn once and redrawn only by a write
// that landed, so this service holds two fields it did not.**
// `docs/design/components.md` states the rule under "A write that does not
// happen": an id minted per press turns a lost answer into two rows wearing two
// legitimate identifiers, and `duplicate-identifier` — the outcome whose whole
// job is to make a lost `201` legible — becomes unreachable from this client.
// `accounts.service.ts` argues the shared half of the lifetime; what is this
// file's own is that **the payee is the more urgent of the two**. The
// transaction's twin is a duplicate row somebody can delete; the payee's is a
// row on a table the app role holds **no `DELETE`** on, so it is permanent, it
// stays in every autocomplete, and the only thing that would have prevented it
// is the id.
//
// **The payee's draft is keyed on the name it was drawn for, and that is not
// tidiness.** A draft id reused under a *different* name is an id the server
// already holds against other text: the create 409s on the identifier, the
// re-read cannot match the new name, and the write would abandon on that pair
// forever. So the draft carries the blind index it was drawn against and is
// redrawn the moment the typed counterparty keys to anything else.
//
// **A conflict buys exactly one re-read, and it is now both conflicts rather
// than one.** `conflictKind: duplicate_name` says this budget already holds the
// counterparty and the list this browser had was stale, so re-reading and
// matching again is the resolution the server is asking for.
// `conflictKind: duplicate_identifier` is the *same status* and used to be the
// opposite remedy — and under a per-press id it was: the id was a fresh draw,
// so a collision said nothing about the name and the one re-read was spent on
// it for nothing. Under a **held** id the meaning inverts. The only way this
// browser's own draft is taken is that its own earlier create landed and lost
// its answer, so the row wearing it holds this name, this index, and is exactly
// what the re-read finds. Reading the two kinds alike is therefore not a
// widening but the same rule under the new id lifetime — and it is what stops a
// lost answer stranding a payee nobody can delete. Every other status still
// says something a second look at the list cannot answer.
//
// A no-match after either conflict abandons, and **drops the draft**: whichever
// kind it was, the id is provably taken by a row this browser cannot adopt, so
// reusing it collects the same answer for the life of the tab.
//
// What must not follow the re-read is a loop: a payee whose name did not open
// can *never* match, so a second attempt posts the same name and conflicts
// again, forever. Retrying with a fresh id is worse than pointless — it fixes
// an astronomically unlikely id collision and loses to the duplicate name in
// the case that actually happens. And reading the conflict as success is worse
// still: it would file the transaction against a payee this client never
// confirmed.
//
// **An empty note posts `null` and seals nothing.** `''` is not a legal
// envelope and answers 400. The consequence is worth stating at the element
// rather than leaving to be discovered: the column distinguishes a note
// somebody cleared (a 29-byte envelope over `''`) from one nobody ever filed
// (`NULL`), and **this client has no path that produces the first**, so the two
// are one thing from this browser. That is a gap rather than a bug — nothing
// here writes the wrong value, there is a value it cannot write — and the day a
// transaction gains an edit screen it becomes a decision that screen has to
// make.
//
// **The typed text is sealed untouched.** No `.trim()` anywhere on this path: a
// trimmed seal beside an untrimmed index keys a row to a value nothing looks
// up. Folding is the index codec's own job and it does it inside, over the same
// string. The non-blank rule the old `.trim()` was accidentally enforcing moved
// to the form, where refusing is all it does.
//
// **The name and the key are checked against each other, because no ordering
// of the two calls closes the window between them.** `blindIndex` compares the
// generation counter and `sealField` compares key identity, so each refuses an
// `adopt()` that lands *while it runs* — and an `adopt()` landing strictly
// between them is invisible to both, whichever goes first. Swapping the two
// lines moves the window rather than shutting it. What it produces is a payee
// row whose `name` was sealed under one account's content key and whose
// `name_key` was computed under another's index key, through the one door the
// server cannot see: it holds no index key and can never recompute one. So the
// create asks for the key **again after the seal** and posts only if the two
// agree — a custody move between the two answers changes the value or answers
// `locked`, and either abandons. The index is still asked for *first*, and
// that is forced rather than preferred: it is what decides whether there is a
// create at all, so sealing ahead of it would seal a name for a row that is
// usually never made.
//
// **All three reads are last-write-wins, and two of them were not.** The list
// is `switchMap` over a subject; the category picker is the same; the payee
// read is a bare `await` guarded by a counter, because the conflict branch
// needs the value it read and not the one a later read published. Decryption
// widens the overlap between two reads from one round trip to one round trip
// plus an AEAD open — five of them per transaction row, one plus a MAC per
// payee — so a slow first read can finish after a fast second and silently
// revert a list to rows the person has already replaced. `mergeMap` and an
// unguarded `set` are the same defect written two ways.
//
// **The list is `TransactionView[] | null` and clears to `null` when a load
// starts** — `docs/design/components.md`, "A value read from the network".
// `null` and never `[]`: an empty array is the sentence *you have no
// transactions*, which is a claim only a server that answered may make.
//
// **A created transaction is not spliced into the list; the list is read
// again.** The accounts screen patches its own list because it has a client-side
// order (`compareNarrative`) and therefore knows where a new row belongs. This
// screen has no order of its own — the rows arrive in the order the server
// chose — so splicing would put the new row wherever the code happened to
// append it. The extra round trip buys the server's ordering staying the only
// one.
//
// **All four lists are dropped when the account locks, and this service is
// where that rule lives rather than in whoever ended the session.**
// `accounts.service.ts` argues the whole of it: `providedIn: 'root'` means no
// injector destroys this object and no navigation clears it, so an opened list
// outlives `custody.lock()`, `SessionService.ended()` and every route change;
// `SessionService` may not reach for three feature services from `+core`; and
// the word is `locked` exactly, because `unlocking` resolves back into keys and
// the screen deliberately keeps the list up through a ceremony. What is this
// file's own is that **each list returns to its own empty value** — `null` for
// the two that mean "no answer yet", `[]` for the two picker lists, which mean
// "nothing to offer" and were never a claim about the account. Setting the
// pickers to `null` would not compile; setting the other two to `[]` would have
// the screen say *you have no transactions* over a session that just ended.
//
// **All four are read again when the account is unlocked**, on the *transition*
// into `unlocked` out of any other word and never on the value —
// `accounts.service.ts` argues every half of that, including why a first run
// reads nothing and why the near side is not `locked` alone. What is this
// file's own is that the arm asks for **all four** and not for the list alone:
// the lock empties four signals and the screen's own `ngOnInit` calls three
// loaders, so a re-read of the transactions by itself hands back the list and
// leaves the form's counterparty suggestions and its category picker empty —
// the same blank one control further in, on a screen that never remounts.
//
// **`openField` and `blindIndex` are handed over as arrows and never as bare
// method references.** Each reads a `#` field, so `this.#custody.openField` on
// its own type-checks perfectly and answers every call with a `TypeError` on
// the wrong receiver.
import { Injectable, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { PayeesApiService } from '@app-core/api/payees-api.service';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { writeOutcomeOf, type WriteOutcome } from '@app-core/api/write-outcome';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
} from '@app-core/security/account-key-custody.service';
import { mintNarrativeRowId } from '@app-core/security/narrative-row-id';
import type {
  NarrativeIndexer,
  NarrativeOpener,
} from '@app-core/security/narrative-text';
import {
  EMPTY,
  Observable,
  Subject,
  catchError,
  firstValueFrom,
  forkJoin,
  from,
  map,
  of,
  switchMap,
} from 'rxjs';
import {
  toCategoryGroupView,
  type CategoryGroupView,
} from '../categories/category-group-view';
import { toCategoryView, type CategoryView } from '../categories/category-view';
import {
  PAYEE_NAME_FIELD,
  matchPayeeByIndex,
  payeeNameBinding,
  toPayeeView,
  type PayeeView,
} from './payee-view';
import {
  toTransactionView,
  transactionDescriptionBinding,
  type TransactionView,
} from './transaction-view';

/** What a screen hands over to record a transaction: text, and nothing sealed. */
export interface NewTransaction {
  readonly amount: number;
  /** The day the money moved, as `yyyy-mm-dd`. */
  readonly date: string;
  readonly accountId: string;
  /** What was typed. `''` is a transaction with no note, never an empty seal. */
  readonly description: string;
  /** The counterparty as typed. `''` is a transaction naming no payee. */
  readonly payee: string;
  readonly categoryId: string | null;
}

// How a load ended, as a word rather than as the absence of a value. A failure
// that emitted nothing would leave the outer subscription unable to clear the
// loading line.
type LoadOutcome =
  | { readonly state: 'loaded'; readonly views: readonly TransactionView[] }
  | { readonly state: 'failed' };

// A note on its way to a column. Three words where `SealedField` has two,
// because "nobody filed one" is a third outcome and it is not a failure: it is
// the value `null` on the wire.
type SealedNote =
  | { readonly state: 'absent' }
  | { readonly state: 'sealed'; readonly wire: string }
  | { readonly state: 'locked' };

// How the counterparty was settled. `none` is a transaction naming no payee and
// is an answer; `abandoned` is the whole write called off, and the two may
// never be folded — folding them files a transaction with no counterparty on
// behalf of somebody who typed one.
//
// An abandon **carries the word the whole write ends on**, because the step
// that refused is the only one that knows which refusal it was: a locked key
// and a counterparty nothing can adopt are two different next steps for a
// person, and `add` has no way to tell them apart after the fact.
type ResolvedPayee =
  | { readonly state: 'none' }
  | { readonly state: 'resolved'; readonly id: string }
  | { readonly state: 'abandoned'; readonly outcome: WriteOutcome };

// How the payee read the conflict branch depends on ended. A word rather than
// `null`, for the reason every union in this file is one: the caller has to
// carry the failure outward as the write's own outcome, and `null` would make
// it invent one.
type PayeeRead =
  | { readonly state: 'read'; readonly views: readonly PayeeView[] }
  | { readonly state: 'failed'; readonly outcome: WriteOutcome };

// The identifier the next payee create will carry, and the blind index it was
// drawn against. The pair travels together because the id is only reusable for
// the name it was drawn for — the head of this file argues why.
interface DraftPayee {
  readonly id: string;
  readonly nameKey: string;
}

@Injectable({ providedIn: 'root' })
export class TransactionsService {
  readonly #api = inject(TransactionsApiService);
  readonly #payeesApi = inject(PayeesApiService);
  readonly #categoryGroupsApi = inject(CategoryGroupsApiService);
  readonly #categoriesApi = inject(CategoriesApiService);
  readonly #custody = inject(AccountKeyCustodyService);
  readonly #transactions = signal<readonly TransactionView[] | null>(null);
  readonly #payees = signal<readonly PayeeView[] | null>(null);
  readonly #categoryGroups = signal<readonly CategoryGroupView[]>([]);
  readonly #categories = signal<readonly CategoryView[]>([]);
  readonly #loading = signal(false);
  readonly #failed = signal(false);
  readonly #loads = new Subject<void>();
  readonly #categoryLoads = new Subject<void>();
  // Which payee read owns the signal. A counter and not a `switchMap`, because
  // this read hands its answer back as well as publishing it: the conflict
  // branch has to match against the list **it** read, and a later read's
  // answer is not a substitute for it.
  #payeeReads = 0;
  // The two drafts the head of this file argues for: drawn on the first press
  // that needs one, kept through every refusal, cleared by the write that
  // landed.
  #draftTransactionId: string | null = null;
  #draftPayee: DraftPayee | null = null;

  // The two arrows the head of this file argues for. Never
  // `this.#custody.openField` and never `this.#custody.blindIndex`.
  readonly #open: NarrativeOpener = (binding, wire) =>
    this.#custody.openField(binding, wire);
  readonly #index: NarrativeIndexer = (field, plaintext) =>
    this.#custody.blindIndex(field, plaintext);

  public readonly transactions = this.#transactions.asReadonly();
  public readonly payees = this.#payees.asReadonly();
  public readonly categoryGroups = this.#categoryGroups.asReadonly();
  public readonly categories = this.#categories.asReadonly();
  public readonly loading = this.#loading.asReadonly();
  /**
   * Whether the last read of the list came back a failure.
   *
   * A fourth state the screen needs and could not infer: the list is `null` at
   * rest, in flight **and** after a failure, so a screen reading the list and
   * the running flag alone renders nothing at all over a read that failed —
   * no sentence, and no way for a person to tell that from an account with
   * nothing in it.
   */
  public readonly failed = this.#failed.asReadonly();

  constructor() {
    this.#loads
      .pipe(
        switchMap(() =>
          this.#api.getTransactions().pipe(
            switchMap((response) =>
              from(
                Promise.all(
                  // `Promise.all` and never `allSettled`: a
                  // `NarrativeFieldMisuseError` is a defect in this client and
                  // has to reach the failure branch, not be filed as one member
                  // that did not open.
                  response.items.map((dto) =>
                    toTransactionView(dto, this.#open),
                  ),
                ),
              ),
            ),
            map((views): LoadOutcome => ({ state: 'loaded', views })),
            catchError((error: unknown): Observable<LoadOutcome> => {
              this.#report('the transactions could not be read', error);

              return of({ state: 'failed' });
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.#loading.set(false);
        this.#failed.set(outcome.state === 'failed');

        if (outcome.state === 'loaded') {
          // Published in the order the server sent, which is an order — there
          // is no client-side sort here to impose a second one.
          this.#transactions.set(outcome.views);
        }
      });

    // The picker's own read, over the same operator and for the same reason as
    // the list's. It was a bare `forkJoin` publishing unconditionally, which
    // this screen reaches on every `ngOnInit`.
    this.#categoryLoads
      .pipe(
        switchMap(() =>
          forkJoin({
            groups: this.#categoryGroupsApi.getCategoryGroups(),
            categories: this.#categoriesApi.getCategories(),
          }).pipe(
            switchMap((response) =>
              from(
                Promise.all([
                  Promise.all(
                    response.groups.items.map((dto) =>
                      toCategoryGroupView(dto, this.#open),
                    ),
                  ),
                  Promise.all(
                    response.categories.items.map((dto) =>
                      toCategoryView(dto, this.#open),
                    ),
                  ),
                ]),
              ),
            ),
            // Inside the inner pipe, so a failed read ends that read alone and
            // leaves the subject able to carry the next one.
            catchError((error: unknown) => {
              this.#report('the categories could not be read', error);

              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(([groups, categories]) => {
        this.#categoryGroups.set(groups);
        this.#categories.set(categories);
      });

    // The word this effect saw last, and `null` until it has run at all. A
    // local rather than a field, for the reason `accounts.service.ts` gives at
    // its own copy: nothing outside this closure may decide what "the previous
    // status" was.
    let seen: AccountKeyStatus | null = null;

    // The one reader of custody's status in this file; the head of the file and
    // `accounts.service.ts` argue why the reaction lives here, why the clearing
    // word is `locked` exactly, and why the reading arm is a transition. It
    // runs once on construction over four lists that are already empty, so the
    // first run is a no-op whatever the injection order was — and it reads
    // nothing either, because a first run has no transition behind it.
    effect(() => {
      const status = this.#custody.status();
      const previous = seen;

      seen = status;

      if (status === 'locked') {
        this.#transactions.set(null);
        this.#payees.set(null);
        this.#categoryGroups.set([]);
        this.#categories.set([]);

        // Cleared beside the lists, for the reason `accounts.service.ts`
        // writes out at its own copy of this line: the word is a claim about
        // the last read, and after the lines above there is no read left for
        // it to be a claim about. This service went without it while both
        // neighbours had it and nothing went red, because the screen's own
        // `locked()` never lets the word reach a render — a coincidence one
        // layer away, not a guard.
        this.#failed.set(false);

        return;
      }

      // The far side of a ceremony, and the three loaders the screen's own
      // `ngOnInit` calls. The head of this file argues why it is all three.
      if (
        status === 'unlocked' &&
        previous !== null &&
        previous !== 'unlocked'
      ) {
        this.load();
        this.loadPayees();
        this.loadCategories();
      }
    });
  }

  public load(): void {
    // Set before the subject is pushed, so a screen reading these two
    // synchronously after `load()` sees the state of the load it just started.
    this.#loading.set(true);
    this.#failed.set(false);
    this.#transactions.set(null);
    this.#loads.next();
  }

  public loadPayees(): void {
    void this.#readPayees();
  }

  /**
   * Reads the picker's two lists and opens every name in them.
   *
   * **The mappers are the categories screen's own and this file declares
   * none.** Both names are sealed columns, so the picker used to render
   * base64url; the fix was never a transform belonging here, because a second
   * one would be a second definition of the bindings those envelopes were
   * sealed against — and `categories.categoryGroupName` in particular is opened
   * under the **group's** identifier, which is the mistake a local copy makes
   * first.
   *
   * `Promise.all` rather than `allSettled`, for the reason every read path in
   * this product gives: a `NarrativeFieldMisuseError` is a defect in this client
   * and has to reach the failure branch. `catchError` is what stops that being
   * an unhandled error on a path that previously had no handling at all; the
   * picker is left holding whatever it held, which for a first load is nothing.
   *
   * The read itself lives in the constructor, over a subject and a
   * `switchMap`: two of these in flight is the ordinary case — this screen
   * asks on every `ngOnInit` — and the slower one publishing last is a picker
   * showing names that have since been renamed.
   */
  public loadCategories(): void {
    this.#categoryLoads.next();
  }

  public categoriesForGroup(categoryGroupId: string): readonly CategoryView[] {
    return this.#categories().filter(
      (category) => category.categoryGroupId === categoryGroupId,
    );
  }

  /**
   * Records one transaction, and answers how the write ended.
   *
   * **Every outcome but `recorded` has already been reported to the console**,
   * here or in the step that refused, so a caller neither has to nor may report
   * it again. What the caller does with the word is two things: keep what
   * somebody typed — five paths end this write with nothing on the server, and
   * a screen that clears its form on the way past destroys the text before the
   * outcome exists — and say which of them happened: the server's field-keyed
   * sentences go beneath the controls they name, and every other refusal takes
   * a line in the screen's one `role="status"` region.
   */
  public async add(transaction: NewTransaction): Promise<WriteOutcome> {
    // Drawn once and used twice — as the binding the note is sealed against,
    // and as the `id` on the wire. The two must be the same value, which is why
    // there is one `const`; and it is `??=` rather than a fresh mint, because a
    // press that follows a refusal has to carry the id the refused press did.
    const id = (this.#draftTransactionId ??= mintNarrativeRowId());

    this.#loading.set(true);

    try {
      // Sealed before the counterparty is resolved, and the order is the rule:
      // resolving may *create* a payee row, and the app role holds no `DELETE`
      // on that table, so a note that turns out to be unsealable after one was
      // created leaves a payee nothing names and nothing can remove.
      //
      // **It closes that one cause and not the class.** The commoner one is
      // still open and is not this file's to close: the payee create lands,
      // the transaction that needed it fails — a 400, a 409, a dropped
      // connection — and the row stays, unreferenced and unremovable, still in
      // the autocomplete. The two writes are two requests, so no transaction
      // spans them; `payees.md` argues why a compensating delete is worse than
      // the strandings it would answer.
      const note = await this.#sealNote(id, transaction.description);

      if (note.state === 'locked') {
        this.#report('the note could not be sealed');

        return { state: 'locked' };
      }

      const payee = await this.#resolvePayee(transaction.payee);

      if (payee.state === 'abandoned') {
        // Already reported, by the step that decided it: only that step knows
        // which of its four refusals happened — which is also why the word
        // travels back on the value rather than being decided here.
        return payee.outcome;
      }

      await firstValueFrom(
        this.#api.createTransaction({
          accountId: transaction.accountId,
          amount: transaction.amount,
          categoryId: transaction.categoryId,
          date: transaction.date,
          description: note.state === 'sealed' ? note.wire : null,
          id,
          payeeId: payee.state === 'resolved' ? payee.id : null,
        }),
      );
    } catch (error: unknown) {
      this.#report('the transaction could not be recorded', error);

      return writeOutcomeOf(error);
    } finally {
      this.#loading.set(false);
    }

    // Spent: the row exists under this id, so the next entry draws a new one.
    // After the `await` and never before it — a clear on the way past is the
    // press deciding what only the answer may.
    this.#draftTransactionId = null;

    // The create's own answer is dropped rather than spliced in: this screen
    // has no ordering of its own, so the server's is the only one there is.
    this.load();

    return { state: 'recorded' };
  }

  // `''` exactly, and never `.trim()`: a note of spaces is a note somebody
  // typed, and the client may not alter what it seals. The form refuses a
  // blank one, where refusing is all it does.
  async #sealNote(rowId: string, plaintext: string): Promise<SealedNote> {
    if (plaintext === '') {
      return { state: 'absent' };
    }

    const sealed = await this.#custody.sealField(
      transactionDescriptionBinding(rowId),
      plaintext,
    );

    return sealed.state === 'locked'
      ? { state: 'locked' }
      : { state: 'sealed', wire: sealed.wire };
  }

  async #resolvePayee(plaintext: string): Promise<ResolvedPayee> {
    if (plaintext === '') {
      return { state: 'none' };
    }

    // The index first, and that order is forced rather than preferred: it is
    // what decides whether there is a create at all, so sealing ahead of it
    // would seal a name for a row that is usually never made. A browser
    // holding no index key cannot tell whether this counterparty is already on
    // the list, and creating one anyway is how a budget grows a second row for
    // a name it already has.
    const indexed = await this.#index(PAYEE_NAME_FIELD, plaintext);

    if (indexed.state === 'locked') {
      this.#report('the counterparty could not be keyed');

      return { state: 'abandoned', outcome: { state: 'locked' } };
    }

    const held = this.#payees() ?? [];
    const match = matchPayeeByIndex(held, indexed.value);

    return match === null
      ? await this.#createPayee(plaintext, indexed.value)
      : { state: 'resolved', id: match.id };
  }

  // `matchKey` is the value the miss was decided on, and it comes back here to
  // be compared rather than posted. The head of this file argues why.
  async #createPayee(
    plaintext: string,
    matchKey: string,
  ): Promise<ResolvedPayee> {
    const id = this.#drawPayeeId(matchKey);
    const sealed = await this.#custody.sealField(
      payeeNameBinding(id),
      plaintext,
    );

    if (sealed.state === 'locked') {
      this.#report('the counterparty’s name could not be sealed');

      return { state: 'abandoned', outcome: { state: 'locked' } };
    }

    // The key again, on the far side of the seal, and the pair is posted only
    // if the two answers agree. Neither operation can see an `adopt()` that
    // lands between them — a seal compares key identity and an index compares
    // the generation counter, and both had returned — so the agreement of two
    // values taken either side of it is the only evidence this service can
    // gather that one account owns both halves of the row it is about to
    // write. A `locked` here is the same refusal by a louder route.
    const nameKey = await this.#index(PAYEE_NAME_FIELD, plaintext);

    if (nameKey.state === 'locked' || nameKey.value !== matchKey) {
      // `locked` for both arms, and the two are not the same event: one is a
      // browser that lost its keys and one is a browser whose keys were
      // *replaced* mid-write. They share the only thing a person can act on —
      // nothing was sent and the account's key state moved underneath — and the
      // second arm is unreachable from any route table this product has, since
      // `adopt()`'s one caller is registration and `/register` is behind
      // `guestGuard` while this screen is behind `authGuard`. Splitting them
      // would add a word no screen can produce and none could render.
      this.#report(
        'the account’s keys changed while the counterparty was being sealed',
      );

      return { state: 'abandoned', outcome: { state: 'locked' } };
    }

    try {
      const created = await firstValueFrom(
        this.#payeesApi.createPayee({
          id,
          name: sealed.wire,
          nameKey: nameKey.value,
        }),
      );

      // Spent: the row exists under this id. Cleared here rather than when the
      // transaction lands, because the two are separate requests — a create
      // that succeeded beside a transaction that failed leaves a payee this
      // browser now holds locally, so the next press matches it and never
      // reaches a create at all.
      this.#draftPayee = null;

      // Held locally rather than re-read: this browser sealed that text under a
      // key it holds and computed that index itself, so what it would read back
      // is what it just sent. Without this, the next transaction naming the
      // same counterparty pays a conflict and a re-read for something already
      // known.
      this.#payees.update((payees) =>
        // A `null` list is "no answer yet", and appending to it would fabricate
        // a list of one over a read that never landed — a screen would then
        // offer one suggestion as though it held the set.
        payees === null
          ? payees
          : [
              ...payees,
              {
                id: created.id,
                name: { state: 'text', value: plaintext },
                nameKey: nameKey.value,
              },
            ],
      );

      return { state: 'resolved', id: created.id };
    } catch (error: unknown) {
      const refusal = writeOutcomeOf(error);

      // **Keyed on the conflict's *kind*, never on its status**, and both kinds
      // reach the one re-read. `duplicate_name` is this budget holding the
      // counterparty under a list this browser had stale. `duplicate_identifier`
      // is this browser's own earlier create landing and losing its answer —
      // the id is a **held** draft now, drawn against this very name, so the row
      // wearing it is the one the re-read is about to find. The head of this
      // file argues why the second reading inverted with the id's lifetime and
      // why reading them alike is not a widening;
      // `docs/business-logic/payees.md` is where the vocabulary lives. Every
      // other refusal says something a second look at the list cannot answer.
      if (
        refusal.state !== 'duplicate-name' &&
        refusal.state !== 'duplicate-identifier'
      ) {
        this.#report('the counterparty could not be created', error);

        return { state: 'abandoned', outcome: refusal };
      }

      // Reported in the branches below and never here: a conflict is the
      // documented resolution path, and logging one as an error on the way
      // through trains a reader to ignore the only channel this service has.
      //
      // Exactly one re-read, and no second create whatever it finds. The head
      // of this file argues why a loop cannot terminate and why a fresh id
      // would not help.
      const refreshed = await this.#readPayees();

      if (refreshed.state === 'failed') {
        // `#readPayees` reported the read that failed, and its word is what
        // this write ends on: the conflict is not what a person can act on
        // here — the read that would have resolved it is.
        return { state: 'abandoned', outcome: refreshed.outcome };
      }

      const match = matchPayeeByIndex(refreshed.views, nameKey.value);

      if (match === null) {
        // Nothing to adopt. The draft goes with it: whichever kind the conflict
        // was, the id is provably held by a row this browser cannot match, so
        // reusing it collects the same answer for the life of the tab.
        this.#draftPayee = null;

        // The severe one, and the reason `add` answers a word: the row holding
        // that name is one this browser cannot read, so every write naming
        // this counterparty ends here, for as long as the row exists. It is
        // the one place in the product that ends on `duplicate-name` — the
        // ordinary conflict resolves one line down and says nothing anywhere.
        // The **`duplicate-name`** word rather than `refusal`, on both kinds:
        // an identifier collision that survives the re-read is a row this
        // browser cannot read wearing the id it drew, which is the same next
        // step for a person and never *this entry is already saved* — the
        // transaction was not.
        this.#report(
          'a counterparty of that name exists and this browser cannot read it',
        );

        return { state: 'abandoned', outcome: { state: 'duplicate-name' } };
      }

      // Adopted, so the draft is spent on a row that exists.
      this.#draftPayee = null;

      return { state: 'resolved', id: match.id };
    }
  }

  // The id the next payee create carries, kept across a refusal and redrawn
  // when the counterparty being created is a different one.
  //
  // **Keyed on the blind index and never on the typed text**, because that is
  // what "a different counterparty" means everywhere else on this path: the
  // index is what the local match and the server's unique constraint both
  // decide on, so `Trader Joe's` retyped as `trader joe's` is the same draft
  // and a genuinely different name is not. Comparing the text would redraw the
  // id over a change of case and hand the server two rows for one name.
  #drawPayeeId(nameKey: string): string {
    if (this.#draftPayee?.nameKey === nameKey) {
      return this.#draftPayee.id;
    }

    const id = mintNarrativeRowId();

    this.#draftPayee = { id, nameKey };

    return id;
  }

  // Answers the list as well as publishing it, because the conflict path needs
  // the value and the screen needs the signal, and re-reading twice for one
  // question would be two answers that can disagree.
  async #readPayees(): Promise<PayeeRead> {
    const generation = ++this.#payeeReads;

    try {
      const response = await firstValueFrom(this.#payeesApi.getPayees());
      const views = await Promise.all(
        response.items.map((dto) => toPayeeView(dto, this.#open, this.#index)),
      );

      // Published only by the newest read. An open plus a MAC per row is
      // enough to let a slow read land after a fast one and put back the
      // payees a newer answer had already replaced.
      if (generation === this.#payeeReads) {
        this.#payees.set(views);
      }

      // Answered whether it published or not: the caller asked *this* read a
      // question, and the newer read's answer is not a reply to it.
      return { state: 'read', views };
    } catch (error: unknown) {
      this.#report('the counterparties could not be read', error);

      return { state: 'failed', outcome: writeOutcomeOf(error) };
    }
  }

  // The developer's channel, and the only place this service names itself.
  //
  // **It is not the screen's channel and must not become one.** `add` answers a
  // {@link WriteOutcome} now, and `docs/design/components.md` says where that
  // word renders — beneath a field, or in the screen's one `role="status"`
  // region, and never in a snackbar, which is what the TODO that stood here
  // proposed. What survives is a console line per abandoned write, because a
  // write that wrote nothing and said nothing in the log cannot be told from
  // one that landed while somebody is debugging it. `cause` is absent on the
  // refusals that never reached a request: there is no error object behind a
  // locked key, and passing `undefined` would print one.
  #report(reason: string, cause?: unknown): void {
    if (cause === undefined) {
      console.error(`Transactions: ${reason}`);

      return;
    }

    console.error(`Transactions: ${reason}`, cause);
  }
}
