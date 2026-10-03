import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from '@app-core/session/session.service';

// Reads `SessionService`, not `AuthService`. The session cookie is `HttpOnly`,
// so nothing in the browser can look at it, and the identity provider's token
// is used once, on the leg that opens a session, and discarded when a session
// begins — so there is no provider token here to read either. The one answer
// all three guards act on is computed once, by the probe the `APP_INITIALIZER`
// awaits, which is also what keeps this function synchronous.
//
// Only a status the server has *answered* may move anybody, and that asymmetry
// is the point rather than an omission. `anonymous` goes to `/welcome`.
// `locked-session` goes to `/release`: it reads no budget content of any kind
// (FR-113) and every screen under `app` draws some, and `/welcome` offers
// nothing a person holding a session can use. `unreachable` is not a refusal: a
// server that could not be reached has said nothing about who the visitor is,
// and reading its silence as "signed out" throws a person holding a perfectly
// good session out of their own account over one blinked request — onto
// `/welcome`, a page served by the same server they could not reach, where
// nothing they do can fix it. `unknown` is the same argument before the first
// ask rather than after a failed one: the initializer resolves the probe before
// the first activation so nothing should see it, and admitting it means a
// deleted initializer costs a redundant state rather than every visitor on
// every cold load. `guest.guard.ts` and `release.guard.ts` mirror this and
// argue from here.
export const authGuard: CanActivateFn = () => {
  switch (inject(SessionService).status()) {
    case 'anonymous':
      return inject(Router).parseUrl('/welcome');
    case 'locked-session':
      return inject(Router).parseUrl('/release');
    default:
      return true;
  }
};
