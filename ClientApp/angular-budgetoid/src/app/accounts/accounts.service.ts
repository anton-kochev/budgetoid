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
// **The index is keyed inside a budget, and a browser that does not know which
// one writes nothing and says `unreachable`.** The identifier comes from
// `SessionService`, which reads it off the answer the `APP_INITIALIZER` already
// awaits, so on every ordinary visit it is there before this screen exists. When
// it is not — a probe that never landed, the moment between an establishing leg
// and its own read — the refusal is `unreachable` and deliberately not `locked`:
// no factor can supply a budget, so `locked`'s advice cannot come true of it and
// would send somebody through a ceremony that changes nothing, while
// `unreachable`'s — try again in a minute — is exactly right. It is asked
// **before** the seal for that reason too: reporting the recoverable fact first
// is what keeps the advice honest on an account that is both locked and
// unplaced.
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
//
// **A write says how it ended, and the two form writes hand the word back.**
// `docs/design/components.md`, "A write that does not happen", is the
// authority: a pipeline that logs a refusal and completes with no value does
// not make that chapter hard to implement, it makes it unreachable — there is
// nothing for a template to branch on. So `add` and `update` answer a
// {@link WriteOutcome}, classified by `writeOutcomeOf` out of the **problem
// document** rather than out of the status, and the screen decides what to do
// with the word: `accounts.component.ts` puts the server's field-keyed
// sentences beneath the controls they name, gives every other refusal a line in
// the one `role="status"` region it already had, and clears the form on
// `recorded` and on no other word. None of that is this service's to do — it
// writes no copy and renders nothing — because a sentence chosen here could not
// be placed under the control the server keyed it to, which is a fact only a
// form knows about itself.
//
// **The create's row id is drawn once per form and redrawn only by a write that
// landed, which is why this service now holds a field it did not.**
// `docs/design/components.md` states it under "A write that does not happen":
// an id minted per press turns a lost answer into two rows wearing two
// legitimate identifiers, and `duplicate-identifier` — the outcome whose whole
// job is to make a lost `201` legible — becomes unreachable from this client.
// So `add` draws `#draftId` on the first press that needs one and keeps it
// through every refusal; the create's success is the one thing that clears it.
//
// **What that costs, said rather than mitigated away.** A service with a field
// is a service with state, and this one is `providedIn: 'root'`, so the draft
// outlives the screen: type a name, lose the answer, walk away, come back and
// add a *different* account, and that second create carries the first attempt's
// id. If the first attempt had in fact landed, the person is told their entry is
// already saved — true of the id and false of what is now in the form. It is
// accepted because the alternative is worse in the commoner direction: a
// per-press id silently writes the row twice, and nothing on either side can
// see it afterwards. **The component may still not hold it** — the id is the
// associated data the name was sealed against, so the one place it can be
// dropped is the one place it must not be, and a screen that reset it on a
// cancel would be exactly that place.
//
// **A lock does not clear it**, deliberately: a locked write sent nothing, and
// redrawing over a ceremony would put the two-row defect back on the far side
// of every unlock.
//
// **`remove` answers the same word, and the channel it was missing is a return
// value like its neighbours'.** It had the classification and nowhere to put
// it: the copy for a refused delete was not written, because the design book's
// state table is written for a form holding typed text — every sentence in it
// says *what you typed* — and a delete holds none. `+shared/write-outcome-report.ts`
// is where the four sentences that closed that gap live, and `rowActReportOf`
// is what a screen calls with the word this method now hands back. Nothing here
// chooses one: a service writes no copy and renders nothing.
//
// **The list is filtered inside the `tap`, which is what makes the screen's
// sentence true.** *The row is still here* is a claim about the list, and it
// holds because a refusal reaches `catchError` without ever reaching the
// update. Moving the filter above it would leave the screen saying one thing
// and showing another.
import { Injectable, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AccountApiService,
  type AccountType,
} from '@app-core/api/account-api.service';
import { writeOutcomeOf, type WriteOutcome } from '@app-core/api/write-outcome';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
} from '@app-core/security/account-key-custody.service';
import { mintNarrativeRowId } from '@app-core/security/narrative-row-id';
import type { NarrativeOpener } from '@app-core/security/narrative-text';
import { SessionService } from '@app-core/session/session.service';
import { compareNarrative } from '@app-shared/compare-narrative';
import {
  Observable,
  Subject,
  catchError,
  finalize,
  firstValueFrom,
  from,
  map,
  of,
  switchMap,
  tap,
} from 'rxjs';
import {
  accountNameBinding,
  accountNameIndexBinding,
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

// The name pair on its way to a column: both halves, or the word the whole write
// ends on.
//
// A union rather than `SealedName | null`, because there are now two ways to
// have nothing to post and they are two different next steps for a person: a
// browser holding no keys is `locked` and wants a factor, a browser that does
// not know its budget is `unreachable` and wants a moment. `null` for both would
// make the caller invent one of them.
type NameForColumn =
  | { readonly state: 'sealed'; readonly wire: string; readonly key: string }
  | { readonly state: 'refused'; readonly outcome: WriteOutcome };

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
  // The one reader of the tenancy in this file. It is injected here rather than
  // reached for from custody, which holds no such value on purpose: the edge
  // from custody into the session module closes a cycle and puts the rule that
  // class is built on one call from being undone.
  readonly #session = inject(SessionService);
  readonly #accounts = signal<AccountView[] | null>(null);
  readonly #loading = signal(false);
  readonly #failed = signal(false);
  readonly #loads = new Subject<void>();

  // The identifier the *next* create will carry, or `null` when none has been
  // drawn. The head of this file argues the lifetime: drawn on the first press
  // that needs one, kept through every refusal, cleared by a create that
  // landed.
  #draftId: string | null = null;

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

  /**
   * Creates one account, and answers how the write ended.
   *
   * **A word rather than `void`, because the caller has a decision to make on
   * it**: `docs/design/components.md` gives the clear to the *answer* and never
   * to the press, so a screen that empties its form on the line after this call
   * destroys somebody's text on every outcome the chapter exists to render.
   * `recorded` is the one word that permits a clear.
   */
  public async add(account: NewAccount): Promise<WriteOutcome> {
    // Drawn once and used twice — as the binding the name is sealed against,
    // and as the `id` on the wire. The two must be the same value, which is why
    // there is one `const`; and it is `??=` rather than a fresh mint, because a
    // press that follows a refusal has to carry the id the refused press did.
    const id = (this.#draftId ??= mintNarrativeRowId());
    const name = await this.#sealName(id, account.name);

    if (name.state === 'refused') {
      // Nothing was sent, so there is no answer to classify — and the word is
      // the one the step that refused chose, because only it knows whether the
      // browser was holding no keys or had not been told which budget it is in.
      // The account's own locked notice is the screen's account of the first,
      // which is why the chapter's table gives that state no sentence of its
      // own.
      return name.outcome;
    }

    this.#loading.set(true);

    return firstValueFrom(
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
          tap((view) => {
            // Spent: the row exists under this id, so the next create draws a
            // new one. Inside the `tap` rather than beside the `recorded`
            // below, so that the clear is a consequence of the server's answer
            // and of nothing else — which is the same rule the chapter states
            // about the form.
            this.#draftId = null;
            this.#accounts.update((accounts) =>
              // A `null` list is "no answer yet", and appending to it would
              // fabricate a list of one over a read that never landed.
              accounts === null ? accounts : byName([...accounts, view]),
            );
          }),
          map((): WriteOutcome => ({ state: 'recorded' })),
          // **Inside the pipe rather than around the promise**, so the opening
          // of the 201's own name is covered too: a body this client cannot
          // read is a write that landed and an answer nobody here can use, and
          // `writeOutcomeOf` has a word for exactly that.
          catchError((error: unknown) => of(writeOutcomeOf(error))),
          finalize(() => this.#loading.set(false)),
        ),
    );
  }

  /** Renames or retypes one account, and answers how the write ended. */
  public async update(
    id: string,
    account: EditedAccount,
  ): Promise<WriteOutcome> {
    // The row's **existing** identifier. Nothing is minted on this path.
    const name = await this.#sealName(id, account.name);

    if (name.state === 'refused') {
      return name.outcome;
    }

    this.#loading.set(true);

    return firstValueFrom(
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
          // the text under a key it holds, so the value it would read back is
          // the text it sent. Currency is immutable and is carried through.
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
          map((): WriteOutcome => ({ state: 'recorded' })),
          catchError((error: unknown) => of(writeOutcomeOf(error))),
          finalize(() => this.#loading.set(false)),
        ),
    );
  }

  /**
   * Deletes one account, and answers how the write ended.
   *
   * **The row is taken off the list inside the `tap`, so a refusal changes
   * nothing on screen** — which is what the screen's sentence for this act is
   * allowed to say. Reordered above the `catchError` it would empty a row out
   * of the list over a delete the server never accepted, and the sentence
   * beside it would then be false.
   */
  public async remove(id: string): Promise<WriteOutcome> {
    this.#loading.set(true);

    return firstValueFrom(
      this.#api.deleteAccount(id).pipe(
        tap(() =>
          this.#accounts.update((accounts) =>
            accounts === null
              ? accounts
              : accounts.filter((view) => view.id !== id),
          ),
        ),
        map((): WriteOutcome => ({ state: 'recorded' })),
        catchError((error: unknown) => of(writeOutcomeOf(error))),
        finalize(() => this.#loading.set(false)),
      ),
    );
  }

  // Both halves or neither. The order is budget, then seal, then index — a
  // locked seal returns before an index is asked for, because a browser holding
  // no content key holds no index key either, so the second call would answer
  // `locked` too and buys nothing but a round of work.
  //
  // **The tenancy is asked for first, and that order decides which advice a
  // person is given when both are missing.** A factor cannot supply a budget, so
  // on an account that is locked *and* unplaced the `locked` reading sends
  // somebody through a ceremony and lands them back here; the `unreachable` one
  // asks for the press again in a minute, which is the remedy that can work.
  async #sealName(rowId: string, plaintext: string): Promise<NameForColumn> {
    const budgetId = this.#session.budgetId();

    if (budgetId === null) {
      return { state: 'refused', outcome: { state: 'unreachable' } };
    }

    const sealed = await this.#custody.sealField(
      accountNameBinding(rowId),
      plaintext,
    );

    if (sealed.state === 'locked') {
      return { state: 'refused', outcome: { state: 'locked' } };
    }

    // The **same text** the seal ran over, never a trimmed or folded copy of
    // it. Folding is the index codec's own job and it does it inside.
    const indexed = await this.#custody.blindIndex(
      accountNameIndexBinding(budgetId),
      plaintext,
    );

    if (indexed.state === 'locked') {
      return { state: 'refused', outcome: { state: 'locked' } };
    }

    return { state: 'sealed', key: indexed.value, wire: sealed.wire };
  }
}
