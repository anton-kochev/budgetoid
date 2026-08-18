import {
  provideHttpClient,
  withFetch,
  withInterceptors,
} from '@angular/common/http';
import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter } from '@angular/router';
import { provideAppCore } from '@app-core/core.providers';
import { apiCredentialsInterceptor } from '@app-core/interceptors/api-credentials.interceptor';
import { sessionExpiryInterceptor } from '@app-core/interceptors/session-expiry.interceptor';
import * as authenticationEffects from '@app-state/authentication/authentication.effects';
import { provideEffects } from '@ngrx/effects';
import { provideStore } from '@ngrx/store';
import { provideOAuthClient } from 'angular-oauth2-oidc';
import { routes } from './app.routes';
import { devtoolsProviders } from './devtools.providers';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(
      withFetch(),
      // Ordered, not listed. Angular walks this array front to back on the
      // request and unwinds it back to front on the response, so the order
      // reads as the direction of travel: credentials go on the way **out**,
      // the expiry check reads what comes **back**.
      //
      // The property being relied on is the unwinding. Last in the array puts
      // `sessionExpiryInterceptor` closest to the backend, so the errors its
      // `catchError` sees are the ones the server actually answered — not
      // anything a later-added interceptor in front of it synthesised. Both
      // read the same request, neither reads the other's work, so today either
      // order behaves the same; a reader who reorders them will not rediscover
      // which half of that is luck.
      withInterceptors([apiCredentialsInterceptor, sessionExpiryInterceptor]),
    ),
    provideRouter(routes),
    provideOAuthClient(),
    provideStore(),
    provideEffects(authenticationEffects),
    ...devtoolsProviders,
    provideAnimationsAsync('noop'),
    provideAppCore(),
  ],
};
