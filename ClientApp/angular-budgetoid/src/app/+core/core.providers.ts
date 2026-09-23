import {
  APP_INITIALIZER,
  EnvironmentProviders,
  makeEnvironmentProviders,
} from '@angular/core';
import { KeyRotationService } from '@app-core/security/key-rotation.service';
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
          rotations: KeyRotationService,
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
          await session.probe();

          // **Whether a key rotation is in flight, and it is asked here for the
          // same reason the probe is.** A run that lost its tab survives as
          // server state alone, so a browser that has just loaded knows of no
          // run — and the three content screens draw their lists from rows a
          // chunk may already have re-sealed under the generation replacing the
          // one custody holds. Read late, or not at all, a reload lands on a
          // list that is part names and part em dashes with nothing on screen
          // saying why.
          //
          // **After the probe has answered, and only for a visitor the server
          // recognised.** This route is authenticated, so an anonymous visitor
          // asking it is answered 401 — and `sessionExpiryInterceptor` is the
          // single owner of "the session ended" and acts on 401 alone, so an
          // unconditional read would report a session ending to somebody who
          // never had one, on every cold load of `/welcome`. Sequential rather
          // than beside the probe for exactly that: the condition is the
          // probe's answer. An anonymous cold start therefore pays nothing.
          //
          // **Awaited, and a failed read does not stop the application.**
          // `readStagedRotation()` publishes `null` rather than rejecting —
          // the seven refusal words each say what became of a *run*, and there is
          // no run here to have become anything — so this can no more break
          // bootstrapping than the probe can, and awaiting it is what keeps a
          // screen from drawing a list before the answer that would have
          // replaced it.
          if (session.status() === 'authenticated') {
            await rotations.readStagedRotation();
          }

          // **The identity provider is contacted here only when it is
          // redirecting a registration back** (NFR-025). Every other cold load
          // — anonymous or signed in, on any screen — makes no request to
          // Google at all; the outbound leg prepares the client itself, on the
          // press that starts it (`AuthService.signIn`). An unconditional call
          // here told Google the address and time of every visit.
          //
          // **Here, and not in a resolver on `/register`, because the answer
          // has to be read before the router's first navigation.** The provider
          // puts its tokens in the fragment and the library clears the fragment
          // once it has read them — but a resolver runs inside a navigation
          // whose target already holds that fragment, and the router writes its
          // target back to the address bar *after* resolvers have run, so the
          // tokens would come straight back. Awaited for the same reason the
          // probe is: `/register` renders the address off the token this reads,
          // and must not render before it.
          //
          // After the probe, and skipped for a visitor it recognised:
          // `guestGuard` turns a session away from `/register`, so completing
          // an exchange for them contacts the provider for a screen they will
          // never see. It is also why a browser that cannot reach Google still
          // learns who its own server thinks it is.
          if (session.status() !== 'authenticated' && auth.isProviderReturn()) {
            await auth.initialize();
          }
        },
      deps: [
        ConfigurationService,
        SessionService,
        AuthService,
        KeyRotationService,
      ],
      multi: true,
    },
  ]);
