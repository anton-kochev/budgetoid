import {
  ApplicationConfig,
  isDevMode,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideStore } from '@ngrx/store';

import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideAppCore } from '@app-core/core.providers';
import { metaReducers, reducers } from '@app-state/index';
import { provideStoreDevtools } from '@ngrx/store-devtools';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(withFetch()),
    provideRouter(routes),
    provideStore(reducers, { metaReducers: metaReducers }),
    provideStoreDevtools({ maxAge: 25, logOnly: !isDevMode() }),
    provideAnimationsAsync('noop'),
    provideAppCore(),
  ],
};
