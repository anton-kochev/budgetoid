import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from '@app-core/session/session.service';

// Reads `SessionService`, not `AuthService`. The session cookie is `HttpOnly`,
// so nothing in the browser can look at it, and once sign-in leaves the
// identity provider there is no token here for `isAuthenticated()` to read
// either. The one answer both guards act on is computed once, by the probe the
// `APP_INITIALIZER` awaits, which is also what keeps this function synchronous.
//
// `anonymous` is the only status that may bounce anybody, and that asymmetry is
// the point rather than an omission. `unreachable` is not a refusal: a server
// that could not be reached has said nothing about who the visitor is, and
// reading its silence as "signed out" throws a person holding a perfectly good
// session out of their own account over one blinked request — onto `/welcome`,
// a page served by the same server they could not reach, where nothing they do
// can fix it. `unknown` is the same argument before the first ask rather than
// after a failed one: the initializer resolves the probe before the first
// activation so nothing should see it, and admitting it means a deleted
// initializer costs a redundant state rather than every visitor on every cold
// load. `guest.guard.ts` mirrors this and argues from here.
export const authGuard: CanActivateFn = () => {
  if (inject(SessionService).status() === 'anonymous') {
    return inject(Router).parseUrl('/welcome');
  }

  return true;
};
