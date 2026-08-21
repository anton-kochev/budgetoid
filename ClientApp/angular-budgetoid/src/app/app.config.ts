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
    // **Registered holding nothing, on purpose.** The last thing that used
    // NgRx was the provider sign-in button's action chain, and it left with
    // the button — no reducer, no effect and no selector is left in the
    // application, so these two calls are the whole of the store today.
    //
    // Kept rather than removed because this is where the decision now reads:
    // the store is what the next state slice reaches for, and taking it out
    // means taking out `devtools.providers.ts`, the `fileReplacements` entry
    // in `angular.json` that swaps it for an empty module in production, and
    // `no-devtools.spec.ts`, which is what proves a state-inspection provider
    // never reaches the bundle. That guard is worth more standing than the
    // two idle calls cost. Do not read an empty `provideEffects()` as an
    // oversight, and do not delete it as dead code.
    provideStore(),
    provideEffects(),
    ...devtoolsProviders,
    provideAnimationsAsync('noop'),
    provideAppCore(),
  ],
};
