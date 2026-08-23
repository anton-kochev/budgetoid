import {
  APP_INITIALIZER,
  EnvironmentProviders,
  makeEnvironmentProviders,
} from '@angular/core';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { AccountApiService } from './api/account-api.service';
import { PayeesApiService } from './api/payees-api.service';
import { TransactionsApiService } from './api/transactions-api.service';

export const provideAppCore = (): EnvironmentProviders =>
  makeEnvironmentProviders([
    // API services
    AccountApiService,
    PayeesApiService,
    TransactionsApiService,
    // Configuration
    ConfigurationService,
    {
      provide: APP_INITIALIZER,
      useFactory:
        (
          config: ConfigurationService,
          session: SessionService,
          auth: AuthService,
        ) =>
        async () => {
          await config.load();
          // After the config and never beside it. `ConfigurationService` holds
          // `apiBaseUrl: ''` until `load()` resolves, and `''` + `/api/me` is a
          // same-origin path — so a probe started in parallel asks this app's
          // own origin who the visitor is. That is answered **200 with
          // `index.html`**, not 404: the dev server's history fallback and Azure
          // Static Web Apps' `navigationFallback` both serve the shell for any
          // unmatched path. Under `responseType: 'json'` the shell fails to
          // parse, the read is published as `unreachable`, both guards admit
          // `unreachable`, and the visitor is shown a screen whose every request
          // then 401s. Quiet enough to survive a long time, which it did.
          //
          // Construction order is no longer what makes this ordering necessary.
          // `BaseApiService` resolves the base URL per request rather than
          // copying it in a constructor, so a service built early can no longer
          // hold a stale copy — that was the defect, and it is fixed there and
          // pinned by `base-api.service.spec.ts`. What survives is the plain
          // fact above: a request made before the configuration has loaded is
          // addressed with `''` whenever it is made, so the probe must be made
          // after.
          //
          // Awaited, and that is what makes every guard in the application
          // synchronous: bootstrapping cannot finish while the answer is
          // outstanding, so no route activates against `'unknown'`. A
          // `void session.probe()` here still asks, and still leaves the first
          // guard reading a status nobody has answered yet.
          //
          // Before `auth.initialize()` rather than after it. That call reaches
          // Google's discovery document, and the session this probe asks about
          // is a first-party cookie the identity provider knows nothing about;
          // ordered the other way, a browser that cannot reach Google never
          // learns who its own server thinks it is.
          await session.probe();
          await auth.initialize();
        },
      deps: [ConfigurationService, SessionService, AuthService],
      multi: true,
    },
  ]);
