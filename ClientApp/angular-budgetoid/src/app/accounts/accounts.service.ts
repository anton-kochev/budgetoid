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
// **The list is dropped when the account locks, and this service is where that
// rule lives rather than in whoever ended the session.** `providedIn: 'root'`
// means no injector destroys this object and no navigation clears it, so an
// opened list outlives `custody.lock()`, `SessionService.ended()` and every
// route change: sign out on `/app/accounts` and the previous account's names
// are still readable from the root injector for the life of the tab. That is
// the failure `SessionService.ended()`'s own comment argues about a key, one
// layer up and over plaintext rather than over the key that produced it.
//
// **`SessionService` may not do it, and the reason is the dependency
// direction.** It lives in `+core`, this file is a feature, and a `+core`
// module importing three feature services inverts the edge the whole folder
// layout exists to state — and would grow a fourth import the day a fourth
// screen opens a narrative column, which is the list somebody forgets. The
// custody status is already an edge this file has: every screen and every
// narrative service reads it. So the reaction sits beside the state it clears,
// where a service that stops clearing is a diff in the file holding the
// plaintext.
//
// **`locked` exactly, and never {@link AccountsService.loading}'s complement or
// "anything but unlocked".** `unlocking` is a state whose resolution *restores*
// the keys, and the screens deliberately keep the list on screen through a
// ceremony — clearing there empties a list somebody is looking at and puts
// nothing in its place. Custody has already dropped both keys by then, so a
// read starting during `unlocking` publishes `locked` words rather than text;
// what survives is a list opened before it, for as long as the ceremony runs.
//
// **The list is read again when the account is unlocked, and that arm is a
// *transition* rather than a value.** A lock leaves the list `null` with
// nothing loading and nothing failed, which is the one combination that renders
// blank — no list, no sentence, no notice — and after an unlock on a screen
// that stayed mounted there is no `ngOnInit` left to ask for it. This is not a
// second reader of custody: it is a second arm of the reader that already
// exists, one file away from the clear it undoes, and what a person expects
// from pressing Unlock is their names back rather than a navigation they have
// to think of. **Not the value**, or a service built into an account that is
// already open reads a list nobody asked it for, on the first run of the
// effect, once per service the injector happens to build. **Any word but
// `unlocked` counts as the far side**, never `locked` alone: effects are
// glitch-free rather than replayed, so whether `unlocking` is observed between
// the two ends is decided by when the flush lands, and a reaction keyed on
// `locked` restores the list on one schedule and leaves it empty on the other.
// **The hole it closes is unreachable and the arm itself is not, and the two
// are different sentences.** No live path reaches an unlock with one of these
// screens mounted: the only caller of `lock()` is `SessionService.ended()`,
// whose two callers both navigate to `/welcome`. What *is* live is the other
// road to `locked` — a **failed** unlock reaches it through custody's own
// `#fail`, which navigates nowhere — so somebody who visited a ledger screen,
// walked to Settings and unlocked there is a transition these services see with
// their screens unmounted, and the cost is one round of reads nobody is looking
// at. That is accepted rather than overlooked: the only way to spend less is to
// know whether a screen is mounted, which is a fact about components that a
// root-provided service is not entitled to hold, and the screens re-read on
// `ngOnInit` anyway.
//
// One thing the paragraph below does **not** say twice: `adopt()` is invisible
// to the clearing arm and is not invisible to this one. It publishes `unlocked`
// over a first run that saw `locked`, which is a transition, so a service built
// before a registration finished would read a list here. That costs one read of
// an account whose lists are empty, and it needs a tab that reached a ledger
// screen before registering — which no route table allows, because `/app` is
// behind `authGuard` and `/register` is behind `guestGuard`.
//
// **`adopt()` is not covered and cannot be, from a status.** It forgets and
// holds in one synchronous pair, so the signal never publishes `locked` and an
// effect sees `unlocked` throughout. Registration is its only caller and holds
// no list at that moment; a second adopter would need a stronger signal than
// this one, and this comment is where that reader should start.
//
// **`openField` is handed to the mapper as an arrow and never as a bare method
// reference.** It reads a `#` field, so `this.#custody.openField` on its own
// type-checks perfectly and answers every call with a `TypeError` on the wrong
// receiver. `account-view.ts` argues it at greater length; this is the call
// site the argument is about.
import { Injectable, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AccountApiService,
  type AccountType,
} from '@app-core/api/account-api.service';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
} from '@app-core/security/account-key-custody.service';
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
  readonly #failed = signal(false);
  readonly #loads = new Subject<void>();

  // The arrow the head of this file argues for. Never `this.#custody.openField`.
  readonly #open: NarrativeOpener = (binding, wire) =>
    this.#custody.openField(binding, wire);

  public readonly accounts = this.#accounts.asReadonly();
  public readonly loading = this.#loading.asReadonly();
  /**
   * Whether the last read of the list came back a failure.
   *
   * A fourth state the screen needs and could not infer: the list is `null` at
   * rest, in flight **and** after a failure, so a screen reading the list and
   * the running flag alone renders nothing at all over a read that failed — no
   * sentence, and no way for a person to tell that from an account with nothing
   * in it.
   */
  public readonly failed = this.#failed.asReadonly();

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
        this.#failed.set(outcome.state === 'failed');

        if (outcome.state === 'loaded') {
          this.#accounts.set(byName(outcome.views));
        }
      });

    // The word this effect saw last, and `null` until it has run at all. A
    // local rather than a field, so that the one reaction allowed to hold it is
    // the one it is declared beside: the *unlock* arm below is a transition and
    // is not a value, and nothing outside this closure has any business
    // deciding what "the previous status" was.
    let seen: AccountKeyStatus | null = null;

    // The one reader of custody's status in this file, and the head of the file
    // argues every half of it: why the reaction lives here and not in whoever
    // ended the session, why the word is `locked` exactly on the clearing arm,
    // and why the arm that reads the list again is written as a transition out
    // of any word but `unlocked`.
    //
    // It runs once on construction, which is harmless here and is not harmless
    // everywhere — `SessionService` refuses this shape for exactly that reason.
    // The difference is what the first run does: there it would wipe a key set
    // that may already have been adopted, and injection order decides which. A
    // list is `null` until something loads one, so the first run clears nothing
    // whatever the order was — and it reads nothing either, because a first run
    // has no transition behind it.
    effect(() => {
      const status = this.#custody.status();
      const previous = seen;

      seen = status;

      if (status === 'locked') {
        this.#accounts.set(null);

        // **Cleared beside the list, and this is the one place the reason is
        // written — `categories.service.ts` and `transactions.service.ts` do
        // the same thing and point here.** `failed` is a claim about the
        // **last read**, and after this line there is no read it can be a
        // claim about: the list is empty because this service emptied it, not
        // because a request came back wrong. Left standing it advises somebody
        // to check their connection over a list nothing asked the server for,
        // and the two situations are the same `null` from outside, so no
        // caller can tell them apart. Each of the three services carries a
        // case for it, because each owns its own effect and a shared argument
        // pins nothing: the transactions service shipped without this line
        // while its neighbours had it, and nothing reddened — its screen's own
        // `locked()` masks the word one layer up, which is a coincidence
        // rather than a guard.
        this.#failed.set(false);

        return;
      }

      // The far side of a ceremony, and the reason the head of this file gives
      // for each half of the condition: a first run has no transition behind
      // it, and every word but `unlocked` is a lawful near side. `unlocked`
      // exactly on this side, never "not locked" — the other word left here is
      // `unlocking`, where custody has already dropped both keys and a read
      // started now publishes `locked` words over a list somebody is looking
      // at.
      if (
        status === 'unlocked' &&
        previous !== null &&
        previous !== 'unlocked'
      ) {
        this.load();
      }
    });
  }

  public load(): void {
    // Set before the subject is pushed, so that a screen reading these three
    // synchronously after `load()` sees the state of the load it just started.
    this.#loading.set(true);
    this.#failed.set(false);
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
