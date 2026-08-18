import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { MeApiService } from '@app-core/api/me-api.service';
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

  private readonly statusSignal = signal<SessionStatus>('unknown');

  public readonly status: Signal<SessionStatus> =
    this.statusSignal.asReadonly();

  // Resolves however the read ends, and never rejects. The `APP_INITIALIZER`
  // awaits this promise, so a rejection is not a failed probe — it is an
  // application that never finishes bootstrapping and a browser left on a blank
  // page. The failure is published as a state, which is the handling.
  public async probe(): Promise<void> {
    try {
      await firstValueFrom(this.api.getMe());
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
