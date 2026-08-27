import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { MeApiService } from '@app-core/api/me-api.service';
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

  public readonly status: Signal<SessionStatus> =
    this.statusSignal.asReadonly();

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
      await firstValueFrom(this.api.getSessionOwner());
      this.statusSignal.set('authenticated');
    } catch (error: unknown) {
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
  public established(): void {
    this.statusSignal.set('authenticated');
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
