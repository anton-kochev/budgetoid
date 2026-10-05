import {
  HttpErrorResponse,
  type HttpInterceptorFn,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { catchError, concatMap, from, throwError } from 'rxjs';
import { isApiRequest } from './api-credentials.interceptor';
import { EXPECTS_UNAUTHENTICATED } from './expects-unauthenticated.token';

// An observer of the response, not a handler of it. A session ending is an
// application-wide fact — every screen's reads start failing at once — so it is
// noticed once here rather than in each caller's `catchError`.
export const sessionExpiryInterceptor: HttpInterceptorFn = (request, next) => {
  const { apiBaseUrl } = inject(ConfigurationService).getConfig();
  const session = inject(SessionService);
  const router = inject(Router);
  // Read as the request leaves, not when its 401 lands: a visit that began
  // while the request was out is not the one its 401 can speak about.
  const sentUnder = session.sessionToken();

  return next(request).pipe(
    catchError((error: unknown) => {
      const lapsed =
        error instanceof HttpErrorResponse &&
        // 401 only. 403 is the CSRF refusal — a request that arrived without
        // the client header — and the locked-session refusal, and both are
        // answered to a browser whose session is intact. Acting on one ends a
        // live session over a bug in the request builder.
        error.status === 401 &&
        !request.context.get(EXPECTS_UNAUTHENTICATED) &&
        // Another origin's 401 is not this product's. The app talks to the
        // identity provider through the same `HttpClient`, and a 401 from
        // Google's discovery endpoint is a statement about a token this product
        // does not issue; signing somebody out of Budgetoid over it is a
        // sign-out caused by a third party. The predicate is imported rather
        // than restated, so this and the credentials interceptor cannot
        // disagree about which requests are ours.
        isApiRequest(request.url, apiBaseUrl);

      // Always re-thrown, and never retried. Swallowed, the error reaches no
      // caller's `catchError`, so the screen that made the request renders
      // neither its outcome nor its failure and sits on its loading line
      // forever — under a navigation a guard may itself cancel.
      if (!lapsed) {
        return throwError(() => error);
      }

      // **Judged, and the ending is the judge's.** Since a sign-in displaces
      // the session the old cookie named, a 401 can be this request losing a
      // race to another tab while the jar already holds a good cookie.
      // `judgeRefusal` re-reads whose session that is and calls `ended()`
      // itself when it is not this tab's; calling it here as well would be a
      // second owner of the transition.
      //
      // The navigation hangs off the verdict rather than off this pipe, so a
      // caller that unsubscribes while the verdict is out cannot leave an
      // ended session on a screen that no longer loads. The error waits for
      // it: the flows that probe `sessionHasEnded()` in their own
      // `catchError` must read a judged session.
      const judged = session.judgeRefusal(sentUnder).then((verdict) => {
        if (verdict === 'ended') {
          void router.navigateByUrl('/welcome');
        }
      });

      return from(judged).pipe(concatMap(() => throwError(() => error)));
    }),
  );
};
