import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from '@app-core/session/session.service';

// The guard on `/release`, and only `authenticated` redirects. A full session
// can open its account, so the screen whose one act is erasing an account
// nobody can open has nothing for it.
//
// `locked-session` is admitted because this is its screen: `authGuard` and
// `guestGuard` both send it here, so turning it away would be a loop.
// `anonymous` is admitted because somebody signed out reaches the screen from
// Welcome, before the trip to Google. `unreachable` and `unknown` are admitted
// on `auth.guard.ts`'s argument — a server that has not answered may move
// nobody.
export const releaseGuard: CanActivateFn = () => {
  if (inject(SessionService).status() === 'authenticated') {
    return inject(Router).parseUrl('/app');
  }

  return true;
};
