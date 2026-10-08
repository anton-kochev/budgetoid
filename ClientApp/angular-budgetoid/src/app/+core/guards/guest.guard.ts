import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from '@app-core/session/session.service';

// The mirror of `auth.guard.ts`: `authenticated` goes to `/app`, and
// `locked-session` goes to `/release`, its one screen — admitted to
// `/register` it would spend a provider trip and a passkey ceremony on an
// address the server already holds, and sent to `/app`, `authGuard` would
// bounce it again. Why the other three do not redirect — and why `unreachable`
// in particular is not a refusal — is argued there once; this guard adds only
// the half that is its own. Sending an `unreachable` visitor to `/app` would
// hand somebody who may well be signed out a screen that can load nothing,
// because `authGuard` reads the same status and admits them.
export const guestGuard: CanActivateFn = () => {
  switch (inject(SessionService).status()) {
    case 'authenticated':
      return inject(Router).parseUrl('/app');
    case 'locked-session':
      return inject(Router).parseUrl('/release');
    default:
      return true;
  }
};
