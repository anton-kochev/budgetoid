import { inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { ConfigurationService } from './configuration.service';

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);

  // Configure OAuth from the (now-loaded) app config and process any
  // redirect-back token. Driven by an APP_INITIALIZER after the config has
  // loaded — see core.providers.ts — so config values are present.
  public async initialize(): Promise<void> {
    const { auth } = this.config.getConfig();

    this.oAuth.configure({
      clientId: auth.google?.clientId,
      issuer: 'https://accounts.google.com',
      redirectUri: auth.google?.redirectUri,
      strictDiscoveryDocumentValidation: false,
      scope: auth.google?.scope,
    });
    // **Guarded, because the `APP_INITIALIZER` awaits this method.** The
    // discovery document lives on `accounts.google.com`; a browser that cannot
    // reach it — an outage, a blocked host, a captive portal, a corporate
    // proxy — would reject here, take the initializer down with it and leave
    // the person on a blank page. `session.probe()`, one line earlier in
    // `core.providers.ts`, is written never to reject for exactly this reason,
    // and it buys nothing while the next call can still do it.
    //
    // What a failure now degrades to is a *working* application whose provider
    // sign-in does not work: the configuration above has been applied, the
    // first-party session cookie was already probed, and every screen that does
    // not need the identity provider renders as usual. Only the sign-in and
    // registration paths are unavailable, and they were unavailable anyway —
    // the provider they depend on is the thing that could not be reached.
    //
    // Nothing is re-thrown and nothing is published. This service holds no
    // state a screen reads, and `isAuthenticated()` already answers `false` for
    // a browser that never completed an exchange, so a flag beside it would be
    // a second, weaker way of asking a question that is already answered.
    //
    // **Nothing schedules a silent refresh, and the omission is the rule.**
    // `setupAutomaticSilentRefresh()` used to sit on the next line; it plants a
    // hidden iframe pointed at `accounts.google.com` and re-runs it on a timer
    // for as long as the tab is open. The provider token is now used **once**,
    // on the registration screen, and discarded at the 201 by
    // `forgetProviderToken()` — every request after that authenticates from the
    // first-party session cookie. Refreshing it would be a third-party request
    // on every page of the product, forever, to keep alive a credential nothing
    // reads. Adding it back is a change to what this application loads from
    // another origin, not a convenience.
    //
    // This guard is a deliberate scope departure in the commit that added it —
    // that commit is about registration, and this is a bootstrap fix. It is
    // here because the same commit made the provider exchange load-bearing for
    // creating an account, which is what turned an unreachable Google from a
    // degraded sign-in into a blank page. Read it as argued, not as a drive-by.
    try {
      await this.oAuth.loadDiscoveryDocumentAndTryLogin();
    } catch {
      // Intentionally swallowed; see above.
    }
  }

  public isAuthenticated(): boolean {
    return this.oAuth.hasValidAccessToken() && this.oAuth.hasValidIdToken();
  }

  /**
   * The address the provider asserted about the person signing in, or `null`.
   *
   * **Read on demand and never held.** A copy on this service would be a
   * second, older answer to the one question this method exists for, and the
   * only caller — the registration screen's header — wants what the current
   * token says, not what it said when the service was constructed.
   *
   * **A claim from a token that has stopped being valid is not an address, and
   * that is the first thing this method asks.** `getIdentityClaims()` answers
   * out of storage: it keeps handing back the decoded token's members for as
   * long as the browser holds them, an hour after the token expired and a day
   * after. The registration screen renders "your account will be created under
   * <address>" from this method and offers a `Continue` beside it, and both legs
   * behind that press are declared on the provider scheme and nothing else — so
   * a stale claim promises an account, offers the control, and reaches a 401
   * whose reason nothing on the screen can name. Answered `null`, the same
   * screen falls through to the arm it already has for a browser holding no
   * token: it says so and offers the provider.
   *
   * The address is not the only thing that goes with the token. The `sub` the
   * account is created under is read off the principal the *server* resolves
   * from that same token, so an expired one has no identity behind it at all —
   * which is what makes `null` the honest answer here rather than a cautious
   * one. This is the prevention half; a token that lapses while the screen is
   * already open is the register flow's own `provider-token-refused`, published
   * from the 401 itself, and neither half covers the other's case.
   *
   * Nothing here schedules a renewal, and the omission is argued in
   * {@link initialize}.
   *
   * It asks the provider for **no new scope**: `openid email` already carries
   * this claim, which is what keeps `src/no-profile-scope.spec.ts` green, and
   * it is the identifier the backend stores anyway. Reading it is therefore not
   * a widening of what this application knows. It reads that one member and
   * nothing else — no `name`, and above all no `picture`: a profile image is
   * loaded from another origin, which this application does not do at all.
   *
   * **Narrowed, never asserted.** `getIdentityClaims()` is typed `object` and
   * what is inside it is whatever the provider put there, so every step down to
   * a `string` is checked. An empty address folds to `null` on the way out —
   * present-but-blank is not an address, and a screen rendering one shows a
   * label with nothing after it, which reads as a bug rather than as absence.
   */
  public providerEmail(): string | null {
    // Above the claims, so a token this application would not send is one whose
    // members it does not read either.
    if (!this.oAuth.hasValidIdToken()) {
      return null;
    }

    const claims: unknown = this.oAuth.getIdentityClaims();

    if (typeof claims !== 'object' || claims === null || !('email' in claims)) {
      return null;
    }

    const email: unknown = claims.email;

    return typeof email === 'string' && email.length > 0 ? email : null;
  }

  public signIn(): void {
    this.oAuth.initLoginFlow();
  }

  /**
   * Discards the provider's tokens locally, without visiting the provider.
   *
   * `logOut(true)` is the local-discard overload: it clears this application's
   * copy of the id and access tokens and performs **no** redirect to Google's
   * end-session endpoint. That is the whole point — this is called once the
   * account exists and a first-party session cookie has taken over, at which
   * point the id token is a credential this application has no further use for
   * and every reason to stop carrying. Ending the person's Google session on
   * their behalf is not something this application was asked to do, and the
   * redirect would also take them off a screen mid-flow.
   *
   * **Not a sign-out**, and {@link signOut} is deliberately left alone beside
   * it. A discard ends nothing the person can see; a sign-out ends their visit.
   * They read the same in a diff and are opposite acts from the seat of the
   * person using the product.
   */
  public forgetProviderToken(): void {
    this.oAuth.logOut(true);
  }

  public signOut(): void {
    this.oAuth.logOut();
  }
}
