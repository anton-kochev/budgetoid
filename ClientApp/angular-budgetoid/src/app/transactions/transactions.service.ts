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
// **A 409 buys exactly one re-read, and then the write is abandoned.** The
// conflict says this budget already holds the counterparty and the list this
// browser had was stale, so re-reading and matching again is the resolution the
// server is asking for. What must not follow is a loop: a payee whose name did
// not open can *never* match, so a second attempt posts the same name and 409s
// again, forever. Retrying with a fresh id is worse than pointless — it fixes
// an astronomically unlikely id collision and loses to the duplicate name in
// the case that actually happens. And reading the 409 as success is worse still:
// it would file the transaction against a payee this client never confirmed.
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
// **The read is `switchMap` and never `mergeMap`.** Decryption widens the
// overlap between two loads from one round trip to one round trip plus five
// AEAD opens per row, so a slow first load can finish after a fast second and
// silently revert the list to rows the person has already replaced.
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
// **`openField` and `blindIndex` are handed over as arrows and never as bare
// method references.** Each reads a `#` field, so `this.#custody.openField` on
// its own type-checks perfectly and answers every call with a `TypeError` on
// the wrong receiver.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { PayeesApiService } from '@app-core/api/payees-api.service';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
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
type ResolvedPayee =
  | { readonly state: 'none' }
  | { readonly state: 'resolved'; readonly id: string }
  | { readonly state: 'abandoned' };

// A conflict is the only failure a re-read can resolve: it says the row already
// exists. Every other status says something a second look at the list cannot
// answer.
function isConflict(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 409;
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
  readonly #loads = new Subject<void>();

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
              this.#report(error);

              return of({ state: 'failed' });
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.#loading.set(false);

        if (outcome.state === 'loaded') {
          // Published in the order the server sent, which is an order — there
          // is no client-side sort here to impose a second one.
          this.#transactions.set(outcome.views);
        }
      });
  }

  public load(): void {
    // Set before the subject is pushed, so a screen reading these two
    // synchronously after `load()` sees the state of the load it just started.
    this.#loading.set(true);
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
   */
  public loadCategories(): void {
    forkJoin({
      groups: this.#categoryGroupsApi.getCategoryGroups(),
      categories: this.#categoriesApi.getCategories(),
    })
      .pipe(
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
        catchError((error: unknown) => {
          this.#report(error);

          return EMPTY;
        }),
      )
      .subscribe(([groups, categories]) => {
        this.#categoryGroups.set(groups);
        this.#categories.set(categories);
      });
  }

  public categoriesForGroup(categoryGroupId: string): readonly CategoryView[] {
    return this.#categories().filter(
      (category) => category.categoryGroupId === categoryGroupId,
    );
  }

  public async add(transaction: NewTransaction): Promise<void> {
    // Minted here and used twice — as the binding the note is sealed against,
    // and as the `id` on the wire. The two must be the same value, which is why
    // there is one `const`.
    const id = mintNarrativeRowId();

    this.#loading.set(true);

    try {
      // Sealed before the counterparty is resolved, and the order is the rule:
      // resolving may *create* a payee row, and the app role holds no `DELETE`
      // on that table, so a note that turns out to be unsealable after one was
      // created leaves a payee nothing names and nothing can remove.
      const note = await this.#sealNote(id, transaction.description);

      if (note.state === 'locked') {
        return;
      }

      const payee = await this.#resolvePayee(transaction.payee);

      if (payee.state === 'abandoned') {
        return;
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
      this.#report(error);

      return;
    } finally {
      this.#loading.set(false);
    }

    // The create's own answer is dropped rather than spliced in: this screen
    // has no ordering of its own, so the server's is the only one there is.
    this.load();
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

    // The index first, because it is what decides the match *and* what a create
    // would have to carry. A browser holding no index key cannot tell whether
    // this counterparty is already on the list, and creating one anyway is how
    // a budget grows a second row for a name it already has.
    const indexed = await this.#index(PAYEE_NAME_FIELD, plaintext);

    if (indexed.state === 'locked') {
      return { state: 'abandoned' };
    }

    const held = this.#payees() ?? [];
    const match = matchPayeeByIndex(held, indexed.value);

    return match === null
      ? await this.#createPayee(plaintext, indexed.value)
      : { state: 'resolved', id: match.id };
  }

  async #createPayee(
    plaintext: string,
    nameKey: string,
  ): Promise<ResolvedPayee> {
    const id = mintNarrativeRowId();
    const sealed = await this.#custody.sealField(
      payeeNameBinding(id),
      plaintext,
    );

    if (sealed.state === 'locked') {
      return { state: 'abandoned' };
    }

    try {
      const created = await firstValueFrom(
        this.#payeesApi.createPayee({ id, name: sealed.wire, nameKey }),
      );

      // Held locally rather than re-read: this browser sealed that text under a
      // key it holds and computed that index itself, so what it would read back
      // is what it just sent. Without this, the next transaction naming the
      // same counterparty pays a conflict and a re-read for something already
      // known.
      this.#payees.update((payees) => [
        ...(payees ?? []),
        { id: created.id, name: { state: 'text', value: plaintext }, nameKey },
      ]);

      return { state: 'resolved', id: created.id };
    } catch (error: unknown) {
      this.#report(error);

      if (!isConflict(error)) {
        return { state: 'abandoned' };
      }

      // Exactly one re-read, and no second create whatever it finds. The head
      // of this file argues why a loop cannot terminate and why a fresh id
      // would not help.
      const refreshed = await this.#readPayees();

      if (refreshed === null) {
        return { state: 'abandoned' };
      }

      const match = matchPayeeByIndex(refreshed, nameKey);

      return match === null
        ? { state: 'abandoned' }
        : { state: 'resolved', id: match.id };
    }
  }

  // Answers the list as well as publishing it, because the conflict path needs
  // the value and the screen needs the signal, and re-reading twice for one
  // question would be two answers that can disagree.
  async #readPayees(): Promise<readonly PayeeView[] | null> {
    try {
      const response = await firstValueFrom(this.#payeesApi.getPayees());
      const views = await Promise.all(
        response.items.map((dto) => toPayeeView(dto, this.#open, this.#index)),
      );

      this.#payees.set(views);

      return views;
    } catch (error: unknown) {
      this.#report(error);

      return null;
    }
  }

  // TODO: surface API errors to the user (e.g. a snackbar) once the app has an
  // error-notification convention. For now the error is reported so it does not
  // become an unhandled rejection.
  #report(error: unknown): void {
    console.error('Transactions API request failed', error);
  }
}
