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
          // parse, the read is published as `unreachable`, every guard admits
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
          // **Which trip the provider is answering, if any, is read once,
          // here** — after the config, because the redirect addresses are in
          // it, and before the probe, because the probe's session arms discard
          // the provider's tokens and the exchange marker with them. Every leg
          // below decides from this one answer.
          const returning = auth.providerReturn();

          // **An email change and a locked sign-in are read before the probe,
          // and awaited.** An email change comes back to a tab that holds a
          // session, so the probe answers `authenticated` and its discard takes
          // the library's nonce — the value the answer is checked against. A
          // locked sign-in usually comes back to a tab holding none, but one
          // that does would lose its nonce the same way. Read after it, either
          // would come back unconfirmed. `initialize()` hands the id token over
          // in memory and discards the library's copy itself.
          if (returning === 'email-change' || returning === 'locked-sign-in') {
            await auth.initialize();
          }

          await session.probe();

          const status = session.status();

          // **An email-change answer is dropped when the probe found no full
          // session**: nobody signed in has no account to change an address
          // for, and a locked session may not change one. Carried on, the
          // answer would sit in memory for a screen that never takes it.
          // `unreachable` and `unknown` are not "nobody" — the status names
          // them apart so that they are never collapsed into `anonymous`.
          if (
            returning === 'email-change' &&
            (status === 'anonymous' || status === 'locked-session')
          ) {
            auth.dropEmailChangeReturn();
          }

          // **A locked sign-in's answer is dropped when the probe found a
          // session already open, full or locked.** The server refuses a
          // locked sign-in beside a live full session with a `409` whose
          // `conflictKind` is `full_session`, so this drop no longer guards
          // a downgrade. It stays so a load that knows its session sends
          // nothing the server would refuse, and so a locked tab is not
          // swapped to another account's locked session — over a live
          // locked cookie, posting the answer replaces it. Only the probe's
          // answer knows, so the drop is decided after it, and before the
          // first route can take the hand-off. `unreachable` and `unknown`
          // keep it: neither says a session is open, and nobody signed in is
          // who this trip is for.
          if (
            returning === 'locked-sign-in' &&
            (status === 'authenticated' || status === 'locked-session')
          ) {
            auth.dropLockedSignInReturn();
          }

          // **Whether a key rotation is in flight, and it is asked here for the
          // same reason the probe is.** A run that lost its tab survives as
          // server state alone, so a browser that has just loaded knows of no
          // run — and the three content screens draw their lists from rows a
          // chunk may already have re-sealed under the generation replacing the
          // one custody holds. Read late, or not at all, a reload lands on a
          // list that is part names and part em dashes with nothing on screen
          // saying why.
          //
          // **After the probe has answered, and only for a full session.** This
          // route is budget-scoped: a locked session is answered 403, and an
          // anonymous visitor asking it is answered 401 — and
          // `sessionExpiryInterceptor` is the
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
          if (status === 'authenticated') {
            await rotations.readStagedRotation();
          }

          // **The identity provider is contacted here only when it is
          // redirecting a trip back to the tab that started it** (NFR-025) —
          // a registration here, an email change or a locked sign-in above.
          // Every other cold load — anonymous or signed in, on any screen —
          // makes no request to Google at all; each outbound leg prepares the
          // client itself, on the press that starts it (`AuthService.signIn`,
          // `startEmailChange`, `startLockedSignIn`). An unconditional call
          // here told Google the address and time of every visit.
          //
          // **Here, and not in a resolver on `/register`, because the answer
          // has to be read before the router's first navigation.** The provider
          // puts its tokens in the fragment and `initialize()` removes the
          // fragment once the library has read them — but a resolver runs
          // inside a navigation whose target already holds that fragment, and
          // the router writes its target back to the address bar *after*
          // resolvers have run, so the tokens would come straight back.
          // Awaited for the same reason the probe is: `/register` renders the
          // address off the token this reads, and must not render before it.
          //
          // After the probe, and skipped for a visitor holding a session, full
          // or locked: `guestGuard` turns a full session away from `/register`
          // and sends a locked one to `/release`, so completing an exchange for
          // either contacts the provider for a screen they will never see.
          // `unreachable` and `unknown` still complete it — neither says a
          // session is open. Probing first is also why a browser that cannot
          // reach Google still learns who its own server thinks it is. Decided
          // from `returning`, not asked again, so an email-change or locked
          // sign-in boot can never reach `initialize()` a second time through
          // this leg.
          //
          // Asked again rather than reusing `status`: the rotation read above
          // is awaited, and a session it found ended is no longer open.
          const statusNow = session.status();

          if (
            returning === 'registration' &&
            statusNow !== 'authenticated' &&
            statusNow !== 'locked-session'
          ) {
            await auth.initialize();
          }

          // **An answer nobody read leaves the address bar before the first
          // route draws**, for the reason a read one does: a reload, a bookmark
          // or a copied link would carry it on. Each leg above removes the
          // answer it read; this takes the ones no leg read — a registration
          // answer reaching a visitor holding a session, whose leg is skipped,
          // or an
          // answer in a tab that started no trip, where `returning` is `null`.
          // Answer-shaped fragments only, so an in-page anchor survives.
          //
          // **Last, after every leg that might read the answer.** Moved above
          // any of them, it removes the answer that leg was about to read. The
          // router's first navigation then reads the address as this leaves it.
          auth.discardUnreadAnswer();
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
