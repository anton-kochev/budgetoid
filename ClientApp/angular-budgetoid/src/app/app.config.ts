import {
  provideHttpClient,
  withFetch,
  withInterceptorsFromDi,
} from '@angular/common/http';
import {
  ApplicationConfig,
  isDevMode,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter } from '@angular/router';
import { provideAppCore } from '@app-core/core.providers';
import * as authenticationEffects from '@app-state/authentication/authentication.effects';
import { profileFeatureKey, profileReducer } from '@app-state/profile';
import { provideEffects } from '@ngrx/effects';
import { provideState, provideStore } from '@ngrx/store';
import { provideStoreDevtools } from '@ngrx/store-devtools';
import { provideOAuthClient } from 'angular-oauth2-oidc';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(withFetch(), withInterceptorsFromDi()),
    provideRouter(routes),
    provideOAuthClient(),
    provideStore(),
    provideState({ name: profileFeatureKey, reducer: profileReducer }),
    provideEffects(authenticationEffects),
    provideStoreDevtools({ maxAge: 25, logOnly: !isDevMode() }),
    provideAnimationsAsync('noop'),
    provideAppCore(),
  ],
};
