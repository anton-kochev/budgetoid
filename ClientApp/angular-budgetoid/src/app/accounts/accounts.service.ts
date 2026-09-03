// The accounts screen's state, and the first service in this product that seals
// what it writes and opens what it reads.
//
// **The service mints, seals and indexes; the component never does.** A screen
// hands over the text somebody typed and nothing else. Put the other way round,
// a component holding a row id it minted has to keep it alive across a form
// reset and a cancel, and the id is the associated data the envelope is sealed
// against — so the one place it can be dropped is the one place it must not be.
//
// **An update re-seals under the existing row id and mints none.** Re-minting
// is one line, it reddens nothing without a test, and what it produces is a
// name that never opens again: the envelope is bound to an identifier the row
// does not have, permanently, with no error anywhere naming the cause. The id
// on a read is a spelling this client's canonical check accepts — `Guid` is
// rendered lower-case hyphenated and folded to that on the way out — so sealing
// against what the API sent back is safe.
//
// **Both halves or neither.** `sealField` and `blindIndex` do not answer alike
// when custody moves mid-operation, and that is by design rather than by
// accident: a seal compares key **identity** and keeps its answer through a
// plain `lock()`, an index compares the **generation counter** and drops it. So
// `sealed` beside `locked` is a reachable pair, and posting it writes a name
// whose column and whose index disagree — through the one door the server
// cannot see, because it holds no index key and can never recompute one. If
// either half is `locked`, nothing is posted.
//
// **Nothing is trimmed.** The client is forbidden from altering what it seals:
// a `.trim()` here would seal one text while some later caller indexed another,
// and the row would key perfectly to a value nothing looks up. The non-blank
// rule it was accidentally enforcing moved to the form, where
// `Validators.required` no longer refuses `'   '` on its own.
//
// **The read is `switchMap` and never `mergeMap`, and that is a behaviour
// change rather than a style.** Decryption widens the overlap between two loads
// from one round trip to one round trip plus N AEAD opens, so a slow first load
// can finish after a fast second and silently revert the list to rows the
// person has already replaced. `switchMap` over a subject is what makes the
// newest load the only one that can publish.
//
// **The list is `AccountView[] | null` and clears to `null` when a load
// starts** — `docs/design/components.md`, "A value read from the network".
// `null` and never `[]`: an empty array is the sentence *you have no accounts*,
// which is a claim only a server that answered may make. A load that failed
// leaves it `null` rather than restoring the previous answer, because a reader
// cannot tell a kept answer from a fresh one.
//
// **`openField` is handed to the mapper as an arrow and never as a bare method
// reference.** It reads a `#` field, so `this.#custody.openField` on its own
// type-checks perfectly and answers every call with a `TypeError` on the wrong
// receiver. `account-view.ts` argues it at greater length; this is the call
// site the argument is about.
import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AccountApiService,
  type AccountType,
} from '@app-core/api/account-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { mintNarrativeRowId } from '@app-core/security/narrative-row-id';
import type { NarrativeOpener } from '@app-core/security/narrative-text';
import { compareNarrative } from '@app-shared/compare-narrative';
import {
  EMPTY,
  Observable,
  Subject,
  catchError,
  finalize,
  from,
  map,
  of,
  switchMap,
  tap,
} from 'rxjs';
import {
  ACCOUNT_NAME_FIELD,
  accountNameBinding,
  toAccountView,
  type AccountView,
} from './account-view';

/** What a screen hands over to create an account: text, and nothing sealed. */
export interface NewAccount {
  readonly name: string;
  readonly type: AccountType;
  readonly openingBalance: number;
  readonly currencyCode: string;
}

/** What a screen hands over to edit one. Currency is immutable, so it is absent. */
export interface EditedAccount {
  readonly name: string;
  readonly type: AccountType;
  readonly openingBalance: number;
}

// How a load ended, as a word rather than as the absence of a value. A failure
// that emitted nothing would leave the outer subscription unable to clear the
// loading line, and inferring "it failed" from "no value arrived" is the one
// predicate that covers three different states.
type LoadOutcome =
  | { readonly state: 'loaded'; readonly views: readonly AccountView[] }
  | { readonly state: 'failed' };

// The name pair on its way to a column: both halves, or nothing at all.
interface SealedName {
  readonly wire: string;
  readonly key: string;
}

function byName(views: readonly AccountView[]): AccountView[] {
  // A copy, because `sort` mutates and the array it is handed may be the one a
  // signal is already publishing. `compareNarrative` answers `0` for two values
  // of one word, so on a locked account a stable sort leaves the rows in the
  // order the API sent them — which is a property to keep rather than a case to
  // write.
  return [...views].sort((left, right) =>
    compareNarrative(left.name, right.name),
  );
}

@Injectable({ providedIn: 'root' })
export class AccountsService {
  readonly #api = inject(AccountApiService);
  readonly #custody = inject(AccountKeyCustodyService);
  readonly #accounts = signal<AccountView[] | null>(null);
  readonly #loading = signal(false);
  readonly #loads = new Subject<void>();

  // The arrow the head of this file argues for. Never `this.#custody.openField`.
  readonly #open: NarrativeOpener = (binding, wire) =>
    this.#custody.openField(binding, wire);

  public readonly accounts = this.#accounts.asReadonly();
  public readonly loading = this.#loading.asReadonly();

  constructor() {
    this.#loads
      .pipe(
        switchMap(() =>
          this.#api.getAccounts().pipe(
            switchMap((response) =>
              from(
                Promise.all(
                  // `Promise.all` and never `allSettled`: a
                  // `NarrativeFieldMisuseError` is a defect in this client and
                  // has to reach the failure branch, not be filed as one row
                  // that did not open.
                  response.items.map((dto) => toAccountView(dto, this.#open)),
                ),
              ),
            ),
            map((views): LoadOutcome => ({ state: 'loaded', views })),
            catchError((error: unknown): Observable<LoadOutcome> => {
              console.error('Accounts API request failed', error);

              return of({ state: 'failed' });
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.#loading.set(false);

        if (outcome.state === 'loaded') {
          this.#accounts.set(byName(outcome.views));
        }
      });
  }

  public load(): void {
    // Set before the subject is pushed, so that a screen reading these two
    // synchronously after `load()` sees the state of the load it just started.
    this.#loading.set(true);
    this.#accounts.set(null);
    this.#loads.next();
  }

  public async add(account: NewAccount): Promise<void> {
    // Minted here and used twice — as the binding the name is sealed against,
    // and as the `id` on the wire. The two must be the same value, which is why
    // there is one `const`.
    const id = mintNarrativeRowId();
    const name = await this.#sealName(id, account.name);

    if (name === null) {
      return;
    }

    this.#loading.set(true);
    this.#api
      .createAccount({
        currencyCode: account.currencyCode,
        id,
        name: name.wire,
        nameKey: name.key,
        openingBalance: account.openingBalance,
        type: account.type,
      })
      .pipe(
        switchMap((created) => from(toAccountView(created, this.#open))),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe((view) => {
        this.#accounts.update((accounts) =>
          // A `null` list is "no answer yet", and appending to it would
          // fabricate a list of one over a read that never landed.
          accounts === null ? accounts : byName([...accounts, view]),
        );
      });
  }

  public async update(id: string, account: EditedAccount): Promise<void> {
    // The row's **existing** identifier. Nothing is minted on this path.
    const name = await this.#sealName(id, account.name);

    if (name === null) {
      return;
    }

    this.#loading.set(true);
    this.#api
      .updateAccount(id, {
        name: name.wire,
        nameKey: name.key,
        openingBalance: account.openingBalance,
        type: account.type,
      })
      .pipe(
        // The route answers 204, so the row is patched from what was just
        // sealed. That is honest rather than optimistic: this browser sealed
        // the text under a key it holds, so the value it would read back is the
        // text it sent. Currency is immutable and is carried through.
        tap(() =>
          this.#accounts.update((accounts) =>
            accounts === null
              ? accounts
              : byName(
                  accounts.map((view) =>
                    view.id === id
                      ? {
                          ...view,
                          name: { state: 'text', value: account.name },
                          openingBalance: account.openingBalance,
                          type: account.type,
                        }
                      : view,
                  ),
                ),
          ),
        ),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public remove(id: string): void {
    this.#loading.set(true);
    this.#api
      .deleteAccount(id)
      .pipe(
        tap(() =>
          this.#accounts.update((accounts) =>
            accounts === null
              ? accounts
              : accounts.filter((view) => view.id !== id),
          ),
        ),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  // Both halves or neither. The order is seal then index, and a locked seal
  // returns before an index is asked for — a browser holding no content key
  // holds no index key either, so the second call would answer `locked` too and
  // buys nothing but a round of work.
  async #sealName(
    rowId: string,
    plaintext: string,
  ): Promise<SealedName | null> {
    const sealed = await this.#custody.sealField(
      accountNameBinding(rowId),
      plaintext,
    );

    if (sealed.state === 'locked') {
      return null;
    }

    // The **same text** the seal ran over, never a trimmed or folded copy of
    // it. Folding is the index codec's own job and it does it inside.
    const indexed = await this.#custody.blindIndex(
      ACCOUNT_NAME_FIELD,
      plaintext,
    );

    if (indexed.state === 'locked') {
      return null;
    }

    return { key: indexed.value, wire: sealed.wire };
  }

  // TODO: surface API errors to the user (e.g. a snackbar) once the app has an
  // error-notification convention. For now the error is swallowed so it does not
  // become an unhandled rejection; `loading` is reset by each pipe's finalize.
  #swallow(error: unknown): typeof EMPTY {
    console.error('Accounts API request failed', error);

    return EMPTY;
  }
}
