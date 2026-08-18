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
          // After the config and never beside it: `BaseApiService` reads
          // `apiBaseUrl` in its constructor and the config holds `''` until
          // `load()` resolves, so a probe started in parallel would send
          // `GET /api/me` to this app's own origin — a 404 from the static
          // host, read as `unreachable`, on every cold load.
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
