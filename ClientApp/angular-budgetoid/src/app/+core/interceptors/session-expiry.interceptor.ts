import {
  HttpErrorResponse,
  type HttpInterceptorFn,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { catchError, throwError } from 'rxjs';
import { isApiRequest } from './api-credentials.interceptor';
import { EXPECTS_UNAUTHENTICATED } from './expects-unauthenticated.token';

// An observer of the response, not a handler of it. A session ending is an
// application-wide fact — every screen's reads start failing at once — so it is
// noticed once here rather than in each caller's `catchError`.
export const sessionExpiryInterceptor: HttpInterceptorFn = (request, next) => {
  const { apiBaseUrl } = inject(ConfigurationService).getConfig();
  const session = inject(SessionService);
  const router = inject(Router);

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

      if (lapsed) {
        // Both halves, always. Navigating without declaring the session over
        // leaves the guard on `/welcome` reading `'authenticated'` and bouncing
        // the visitor straight back into screens that no longer load.
        session.ended();
        void router.navigateByUrl('/welcome');
      }

      // Always re-thrown. Swallowed, the error reaches no caller's `catchError`,
      // so the screen that made the request renders neither its outcome nor its
      // failure and sits on its loading line forever — under a navigation a
      // guard may itself cancel.
      return throwError(() => error);
    }),
  );
};
