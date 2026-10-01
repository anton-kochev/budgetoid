import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from '@app-core/session/session.service';

// The mirror of `auth.guard.ts`, and only `authenticated` redirects. Why the
// other three do not — and why `unreachable` in particular is not a refusal —
// is argued there once; this guard adds only the half that is its own. Sending
// an `unreachable` visitor to `/app` would hand somebody who may well be signed
// out a screen that can load nothing, because `authGuard` reads the same status
// and admits them.
export const guestGuard: CanActivateFn = () => {
  if (inject(SessionService).status() === 'authenticated') {
    return inject(Router).parseUrl('/app');
  }

  return true;
};
