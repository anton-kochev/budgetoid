import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { MeApiService, type MeDto } from '@app-core/api/me-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { firstValueFrom } from 'rxjs';

// Four states, and the fourth is the one a reader will collapse into the third.
// The session cookie is `HttpOnly`, so nothing in the browser can read it and
// the only way to learn who the visitor is is to ask the server — which makes
// every value here a reading of an *answer*, and `unreachable` the reading of an
// answer that never came. A request that got no answer is not evidence about the
// visitor: read as a refusal it signs a person holding a perfectly good session
// out of their own account, over a network that blinked once during the cold
// load, and drops them on a page served by the same server they could not reach.
// It is `SettingsService`'s "never collapse `null` to `0`" rule one screen over —
// a claim about the account made out of a failure to ask.
//
// `unknown` is the same argument before the first ask rather than after a failed
// one: the initializer resolves the probe before the first route activates, so
// nothing should see it, and it exists so that a deleted initializer is a
// redundant state rather than every visitor bounced on every cold load.
export type SessionStatus =
  | 'unknown'
  | 'authenticated'
  | 'anonymous'
  | 'unreachable';

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly api = inject(MeApiService);
  // **The dependency runs one way and must keep doing so.** This class reaches
  // for custody; custody reaches for nothing on this class, and says so in its
  // own header — a key that will not open is not a session that ended, so
  // publishing `anonymous` from there would sign somebody out of an account
  // they are demonstrably inside. That asymmetry is what keeps the two modules
  // out of an import cycle: the edge exists here and nowhere in the other
  // direction, and `account-key-custody.service.spec.ts` provides a
  // `SessionService` stub purely so a call that appeared would land somewhere
  // countable.
  private readonly custody = inject(AccountKeyCustodyService);

  private readonly statusSignal = signal<SessionStatus>('unknown');

  // The budget this browser is operating inside, or `null` while nothing has
  // said. The head of {@link budgetId} argues what it is for and why it is a
  // second signal rather than a member of the status.
  private readonly budgetSignal = signal<string | null>(null);

  public readonly status: Signal<SessionStatus> =
    this.statusSignal.asReadonly();

  /**
   * The budget the requests this browser makes are scoped by, or `null` while
   * this browser has not been told.
   *
   * **It is here because the blind index needs it and nothing else does.** No
   * screen renders it. `GET /api/me` is the one route that says it — the value
   * is resolved from the session cookie on the server and named in no request —
   * and it is the fourth field of every blind-index message, which is what stops
   * two budgets of one account keying one value to one digest. `blind-index.ts`
   * argues the grammar; this is where the value the browser folds into it is
   * read.
   *
   * **A signal beside the status and never a member of it.** The two are learned
   * from one answer and are not the same fact: a browser can know who it is and
   * not know which budget it is in — that is exactly the window between an
   * establishing leg and the read below — and a status union carrying the
   * identifier would make every screen that reads `'authenticated'` re-derive
   * that distinction for itself.
   *
   * **`null` is never a value a caller may substitute for.** A write that finds
   * it `null` does not happen and answers `unreachable`: no factor can supply a
   * budget, so `locked`'s advice — present one — cannot come true of this, and
   * sending somebody through a ceremony that changes nothing is the collapse
   * this codebase refuses in five other places. What can come true is a reload,
   * which is `unreachable`'s.
   */
  public readonly budgetId: Signal<string | null> =
    this.budgetSignal.asReadonly();

  // Resolves however the read ends, and never rejects. The `APP_INITIALIZER`
  // awaits this promise, so a rejection is not a failed probe — it is an
  // application that never finishes bootstrapping and a browser left on a blank
  // page. The failure is published as a state, which is the handling.
  public async probe(): Promise<void> {
    try {
      // `getSessionOwner()` and not `getMe()`, which is the same route. That
      // method carries `EXPECTS_UNAUTHENTICATED`, so the 401 this call went to
      // fetch reaches the `catch` below and nothing else — without it
      // `sessionExpiryInterceptor` reads the answer as a session ending and
      // navigates to `/welcome` from inside the `APP_INITIALIZER`, before the
      // router has activated anything, which is every anonymous visitor's deep
      // link. The reading of that 401 belongs to the `catch` below, and is made
      // once.
      //
      // **The budget rides on the answer this call already makes**, which is
      // the whole reason nothing was added to the initializer: it is awaited
      // before the first route activates, so every screen behind `authGuard`
      // starts with the identifier its writes need, at the cost of no round
      // trip at all.
      const me = await firstValueFrom(this.api.getSessionOwner());

      this.budgetSignal.set(SessionService.budgetOf(me));
      this.statusSignal.set('authenticated');
    } catch (error: unknown) {
      // Dropped beside the status, because it is a claim about a read that did
      // not land. A stale identifier left standing here would be keyed into
      // values written by whoever comes back next.
      this.budgetSignal.set(null);
      this.statusSignal.set(SessionService.readingOf(error));
    }
  }

  // The mid-visit transition, called by `sessionExpiryInterceptor` when the API
  // answers 401 to a request the visitor did not expect to be refused. It is a
  // set rather than a re-probe: the server has just said what it thinks, and
  // asking it again over a network that may itself be the problem would replace
  // an answer with a guess.
  public ended(): void {
    this.statusSignal.set('anonymous');

    // **Dropped beside the keys, and for the same reason they are.** The
    // identifier is a fact about the session that just ended, and a browser that
    // kept it would fold the previous occupant's tenancy into the first value
    // the next one writes — through the one door the server cannot see, since it
    // holds no index key and can never recompute a digest to check it against.
    // It is one line here rather than one line in each of the paths that end a
    // session, for the reason `custody.lock()` is: a third path will be added by
    // somebody thinking about sign-out, and put here it costs them nothing.
    this.budgetSignal.set(null);

    // **Custody ends where the session does, and it ends here rather than at
    // each caller.** Two paths end a session today —
    // `sessionExpiryInterceptor` on a 401 and `SettingsService.leave()` — and a
    // third will be added by somebody thinking about sign-out rather than about
    // key material. Put in this method, that third path clears the account's
    // keys for free; put in the two callers, it does not, and the symptom is an
    // ended session whose content key is still readable from the root injector
    // for the life of the tab. Nothing goes red about it either way.
    //
    // **Not an `effect()` over {@link status}, and the temptation is real** —
    // one reaction beside the signal reads tidier than a call inside a method.
    // It fires on construction, so whether it wipes a set that has already been
    // adopted is decided by injection order, which nothing here controls. And
    // the only honest predicate it could carry is "lock on `'anonymous'`":
    // locking on `'unreachable'` destroys both keys over one blinked request
    // and demands a full WebAuthn ceremony to get them back, which is exactly
    // the failure `auth.guard.ts` and `guest.guard.ts` exist to prevent,
    // reappearing one layer down. That asymmetry is already written twice, in
    // the guards and in {@link readingOf}; writing it a third time is how the
    // third copy drifts.
    //
    // {@link established} deliberately clears nothing. A session beginning says
    // nothing about which factor opened it, and the two paths that know —
    // registration and sign-in — hand the keys over themselves.
    this.custody.lock();
  }

  // The mirror of `ended()`, called when a leg that establishes a session has
  // just answered — registration is the first. A set rather than a re-probe for
  // the reason stated four lines above: the server has said what it thinks, and
  // asking again replaces an answer with a guess. Here it would also cost a
  // round trip at the happiest moment of the flow and could come back
  // `unreachable`, which is a third reading of a fact the server has already
  // stated in the same breath as the cookie it set.
  //
  // **The budget is the one thing that is asked for, and it is not the same
  // shape of question.** The status is a fact the answering leg already stated;
  // the identifier is a fact nothing in that answer carries and nothing in this
  // browser can derive, so a read is not a guess replacing an answer — it is the
  // only source there is. It touches the status not at all, and it is not
  // awaited: `established()` is called from a subscriber with a navigation on
  // the line after it, and an `await` there would put a round trip between a
  // verified credential and the app for the sake of a value only a *write*
  // needs. Until it lands, {@link budgetId} is `null` and a write says so —
  // `unreachable`, and the remedy is the same press a moment later.
  public established(): void {
    this.statusSignal.set('authenticated');

    void this.readBudget();
  }

  // Reads the budget alone, publishing nothing else and never rejecting.
  //
  // **The same `EXPECTS_UNAUTHENTICATED` method the probe uses**, for the reason
  // `me-api.service.ts` writes out over `getAccountKeys`: this request is made
  // by a browser that has just been handed a session, and a 401 to it is a
  // cookie that had not landed rather than a session ending. Unmarked, it routes
  // its own refusal into `sessionExpiryInterceptor` — the single owner of "the
  // session ended" — which navigates to `/welcome` from underneath a screen that
  // has just succeeded, through an edge no import graph shows.
  //
  // A failure publishes nothing: `null` is already what the signal holds when
  // nothing has said, and setting it here would be a second writer of a value
  // whose only other writers are the probe and the end of a session.
  private async readBudget(): Promise<void> {
    try {
      const me = await firstValueFrom(this.api.getSessionOwner());

      this.budgetSignal.set(SessionService.budgetOf(me));
    } catch {
      // Nothing. See above.
    }
  }

  // The identifier the answer carried, or `null` for a body that carried none.
  //
  // **A boundary check and deliberately not a refusal.** `me-api.service.ts`
  // argues why a body of the wrong shape is refused there rather than coerced;
  // this member is the exception the same argument produces, because refusing it
  // would take the *status* down with it. A response missing this member still
  // says there is a session and whose it is, and reading that as `unreachable`
  // signs somebody out over a version skew. `null` is the honest reading: signed
  // in, tenancy unknown, writes refused with a word whose remedy is a reload.
  //
  // **It folds nothing.** A spelling that is not the canonical one is passed
  // through as it arrived and refused by the codec that owns the grammar — the
  // rule `blind-index.ts` keeps at its own door, because a repair made here
  // would invent a second spelling of a value that has one, at the writing end,
  // where every row keyed under the invented one is a row no lookup reproduces.
  // `''` is the one value that becomes `null`, and that is not a fold: it is the
  // absence of an answer wearing the type of one.
  private static budgetOf(me: MeDto): string | null {
    const answered: unknown = me.budgetId;

    return typeof answered === 'string' && answered !== '' ? answered : null;
  }

  private static readingOf(error: unknown): SessionStatus {
    // 401 is the server saying it knows who is asking and the answer is nobody;
    // 403 is the CSRF refusal and the locked-session refusal. Neither describes
    // an authenticated visitor and neither has a next step that differs from the
    // other's, so the two collapse — the one collapse here that is correct.
    if (
      error instanceof HttpErrorResponse &&
      (error.status === 401 || error.status === 403)
    ) {
      return 'anonymous';
    }

    // Everything else: status `0` for a request that never reached a server, a
    // 500 from a server that is up and broken, a timeout, a body that failed to
    // parse. None of them is a statement about who is asking, and an
    // implementation reading "not 200" as "not signed in" is the defect the
    // fourth state exists to prevent.
    return 'unreachable';
  }
}
