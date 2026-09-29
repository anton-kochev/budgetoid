import { DOCUMENT, inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { ConfigurationService } from './configuration.service';

// The mark `signIn` leaves in this tab's `sessionStorage` immediately before
// it sends the person to the provider, and the second half of what makes a
// page load the provider coming back — see `isProviderReturn`. Its own key,
// not the library's `nonce`: that one outlives the exchange it was written
// for, and this one's lifetime is this service's to decide.
//
// `sessionStorage` because it is per tab and survives the top-level round
// trip to the provider, which is exactly the span an exchange is outstanding.
// Read through the global, not the injected document's window: every access
// is guarded anyway, and a storage the browser refuses reads as no exchange.
const EXCHANGE_MARKER = 'budgetoid-provider-exchange';
const EXCHANGE_STARTED = 'started';

function exchangeMarked(): boolean {
  try {
    return sessionStorage.getItem(EXCHANGE_MARKER) !== null;
  } catch {
    // Unreadable storage is a tab that cannot show it started an exchange.
    // Asked from the `APP_INITIALIZER`, so a throw here is a blank page.
    return false;
  }
}

function markExchange(): boolean {
  try {
    sessionStorage.setItem(EXCHANGE_MARKER, EXCHANGE_STARTED);

    return true;
  } catch {
    return false;
  }
}

function unmarkExchange(): void {
  try {
    sessionStorage.removeItem(EXCHANGE_MARKER);
  } catch {
    // Swallowed: both callers — `initialize` on the `APP_INITIALIZER` and
    // `forgetProviderToken` on a session being published — must not fail
    // over a mark. A marker that survives costs what the residual in
    // `isProviderReturn` already names.
  }
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);
  private readonly document = inject(DOCUMENT);

  // The one preparation of the client this page load makes, shared by every
  // caller. Resolves `true` once the discovery document has loaded and any
  // answer on the URL has been read, `false` when the provider could not be
  // reached.
  //
  // **Memoized here, and not at either caller, because this service is the
  // only thing both legs share.** The return leg asks from the
  // `APP_INITIALIZER`, the outbound leg asks from a button press on the
  // registration screen, and on a page that came back from the provider both
  // happen — a person whose token lapsed presses **Continue with Google** again.
  // A flag at either caller cannot see the other; this field sees both, so the
  // provider hears from this page load at most once.
  //
  // **A success is held and a failure is not.** The field is cleared when the
  // load rejects, so the next press asks again. Held, one unreachable moment
  // would leave the provider button dead until a reload, with nothing on the
  // screen saying why a press does nothing.
  private ready: Promise<boolean> | null = null;

  /**
   * Configures the client from the loaded app config, fetches the provider's
   * discovery document and reads any answer the provider left on the URL.
   *
   * **This is what contacts the identity provider, so it runs only where
   * registration needs it** (NFR-025): from the `APP_INITIALIZER` when
   * {@link isProviderReturn} says the provider is redirecting back, and from
   * {@link signIn} before the exchange starts. Run on every cold load, it
   * would tell Google the address and time of every visit to the product,
   * anonymous or signed in.
   *
   * At most once per page load; see {@link ready}.
   *
   * **Consumes the exchange marker once preparation has settled, whatever it
   * settled as.** By then the library has read any answer off the URL, so no
   * exchange is outstanding in this tab. Left behind, the marker would make a
   * reload of an answer-shaped address — a history entry, a bookmark — prepare
   * the client again on every load. Not removed any earlier: until preparation
   * settles, the page is still the one the provider answered.
   */
  public async initialize(): Promise<void> {
    try {
      await this.whenReady();
    } finally {
      unmarkExchange();
    }
  }

  private whenReady(): Promise<boolean> {
    this.ready ??= this.prepare();

    return this.ready;
  }

  private async prepare(): Promise<boolean> {
    const { auth } = this.config.getConfig();

    this.oAuth.configure({
      clientId: auth.google?.clientId,
      issuer: 'https://accounts.google.com',
      redirectUri: auth.google?.redirectUri,
      strictDiscoveryDocumentValidation: false,
      scope: auth.google?.scope,
    });
    // **Guarded, because the `APP_INITIALIZER` awaits this method** on a page
    // load the provider redirected back to. The discovery document lives on
    // `accounts.google.com`; a browser that cannot reach it — an outage, a
    // blocked host, a captive portal, a corporate proxy — would reject here,
    // take the initializer down with it and leave the person on a blank page.
    // `session.probe()`, earlier in `core.providers.ts`, is written never to
    // reject for exactly this reason, and it buys nothing while this call can
    // still do it.
    //
    // What a failure degrades to is a *working* application whose provider
    // exchange does not work: the first-party session cookie was already
    // probed, and every screen that does not need the identity provider renders
    // as usual. Only registration is unavailable, and it was unavailable anyway
    // — the provider it depends on is the thing that could not be reached.
    //
    // Nothing is re-thrown and nothing is published. This service holds no
    // state a screen reads, and registration reads `providerEmail()`, which
    // already answers `null` for a browser that never completed an exchange. The `false` this resolves to is for {@link signIn}
    // alone, which must not send anybody to a login endpoint nobody has
    // learned.
    //
    // **Nothing schedules a silent refresh, and the omission is the rule.**
    // `setupAutomaticSilentRefresh()` used to sit on the next line; it plants a
    // hidden iframe pointed at `accounts.google.com` and re-runs it on a timer
    // for as long as the tab is open. The provider token is now used **once**,
    // on the registration screen, and discarded by `forgetProviderToken()`
    // whenever `SessionService` publishes a session — every request after that
    // authenticates from the first-party session cookie. Refreshing it would be
    // a third-party request on every page of the product, forever, to keep alive
    // a credential nothing reads. Adding it back is a change to what this
    // application loads from another origin, not a convenience.
    //
    // This guard is a deliberate scope departure in the commit that added it —
    // that commit is about registration, and this is a bootstrap fix. It is
    // here because the same commit made the provider exchange load-bearing for
    // creating an account, which is what turned an unreachable Google from a
    // degraded sign-in into a blank page. Read it as argued, not as a drive-by.
    try {
      await this.oAuth.loadDiscoveryDocumentAndTryLogin();

      return true;
    } catch {
      // Intentionally swallowed; see above. Forgotten, so the next ask retries
      // — see {@link ready}.
      this.ready = null;

      return false;
    }
  }

  /**
   * Whether this page load is the provider redirecting back with its answer.
   *
   * **Two things, both required, and neither is enough alone:**
   *
   * - **This tab started an exchange.** {@link signIn} leaves a marker in
   *   `sessionStorage` immediately before it leaves for the provider, and
   *   {@link initialize} consumes it once preparation settles. An
   *   answer-shaped address is something anybody can put in a link; only a
   *   tab that pressed the provider button is waiting for one, so a crafted
   *   link opened anywhere else costs no request to Google (NFR-025).
   * - **The configured redirect address — origin and path — with an answer in
   *   the fragment**, parsed by key and never matched as a substring: a
   *   non-empty `access_token`, `id_token` and `state` together, or a
   *   non-empty `error` on its own. The refusal needs no `state` because
   *   Google's documented implicit-flow refusal, `#error=access_denied`, may
   *   carry none.
   *
   * **The query is not read.** This client runs the implicit flow, and
   * angular-oauth2-oidc 17.0.2's `tryLogin` reads that flow's answer from the
   * fragment alone; it reaches the code-flow parser only under `responseType:
   * 'code'`, a key the configure key-set pin in `auth-service.spec.ts`
   * refuses. So a `code`, `state` or `error` in the query is somebody opening
   * the screen, the same as a campaign parameter, an in-page anchor or a
   * partial answer in the fragment — and preparing the client for any of them
   * is a contact with the provider that ends in nothing.
   *
   * The fragment check is a copy of the library's rule, not the library's
   * parser. The library decodes the whole fragment before splitting it, reads
   * past a `?` inside it, strips a leading `/` from a key and keeps the last of
   * a repeated key, so a hand-built fragment can make the two disagree in
   * either direction. The marker is what keeps that disagreement to tabs that
   * started an exchange.
   *
   * **The residual, accepted:** a tab that pressed the provider button and
   * then abandoned the exchange at Google keeps its marker until it closes or
   * a session begins in it ({@link forgetProviderToken}). An answer-shaped
   * link opened in that tab costs one discovery-document and key-set fetch,
   * and the library refuses the answer on its nonce.
   *
   * A pure question: it reads the marker and never consumes it, so asking
   * twice answers the same. Storage the browser refuses to read answers
   * `false` rather than throwing, because the `APP_INITIALIZER` asks this.
   *
   * Read from the document rather than the router: this is asked by the
   * `APP_INITIALIZER`, before the router has navigated anywhere — see
   * `core.providers.ts` for why it has to be that early.
   */
  public isProviderReturn(): boolean {
    const redirectUri = this.config.getConfig().auth.google?.redirectUri;

    if (redirectUri === undefined || !URL.canParse(redirectUri)) {
      return false;
    }

    const expected = new URL(redirectUri);
    const landed = new URL(this.document.location.href);

    if (
      landed.origin !== expected.origin ||
      landed.pathname !== expected.pathname
    ) {
      return false;
    }

    // `URLSearchParams` drops one leading `?` itself, but not a `#`.
    const fragment = new URLSearchParams(landed.hash.slice(1));
    const present = (key: string): boolean =>
      (fragment.get(key) ?? '').length > 0;

    const answer =
      (present('access_token') && present('id_token') && present('state')) ||
      present('error');

    return answer && exchangeMarked();
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

  /**
   * Starts the provider exchange: a top-level navigation to the provider,
   * which redirects back to `/register`.
   *
   * **Prepares the client first**, because nothing prepared it at bootstrap and
   * the login endpoint this navigates to is learned from the discovery
   * document. On a page load that came back from the provider the preparation
   * is already done and costs nothing.
   *
   * **Returns `void` and settles its own promise**, because the two callers are
   * click handlers with nothing to do after the page has left: the success path
   * ends in a navigation away, and the failure path — the provider could not be
   * reached — is a press that does nothing, which is also what the library's own
   * `initLoginFlow()` does without a login endpoint, minus the unhandled error.
   *
   * **Marks the tab as mid-exchange immediately before leaving, and only
   * then** — see {@link isProviderReturn}. Not at the press: a press that
   * could not reach the provider starts no round trip and must leave nothing
   * that makes a later load look like one coming back. A marker the browser
   * will not store means the answer would be refused on the way back, so the
   * trip is not started either: a press that does nothing beats a round trip
   * to Google that lands on a screen reading as if nothing happened.
   */
  public signIn(): void {
    void this.whenReady().then((ready) => {
      if (ready && markExchange()) {
        this.oAuth.initLoginFlow();
      }
    });
  }

  /**
   * Discards the provider's tokens locally, without visiting the provider.
   *
   * `logOut(true)` is the local-discard overload: it clears this application's
   * copy of the id and access tokens from `sessionStorage` and performs **no**
   * redirect to Google's end-session endpoint. That is the whole point — the
   * one caller is `SessionService`, each time it publishes `authenticated`:
   * from `established()` (the registration 201, a passkey sign-in) and from a
   * start-up probe that finds a session. A first-party session cookie has taken
   * over by then, so the id token is a credential this application has no
   * further use for and every reason to stop carrying. Ending the person's
   * Google session on their behalf is not something this application was asked
   * to do, and the redirect would also take them off a screen mid-flow.
   *
   * **Never on an `anonymous` or `unreachable` probe.** On the provider-return
   * leg the probe runs before {@link initialize} reads the answer off the URL,
   * and a discard there takes the library's nonce with the tokens, so the
   * answer no longer validates. The arm is chosen in `SessionService`; this
   * method only discards.
   *
   * **Not a sign-out.** A discard ends nothing the person can see; a sign-out
   * ends their visit, and it is first-party — the settings screen ends the
   * session on this application's own API, which never involves the provider.
   * This service offers no provider sign-out at all: the argument-less
   * `logOut()` navigates to the provider's end-session endpoint whenever the
   * library knows one, and a contact with Google on a person's own action is
   * outside every moment NFR-025 permits — of its three, this product builds
   * only the account-creation exchange. Google's discovery document publishes
   * no `end_session_endpoint` today, so against this configuration the two
   * overloads happen to behave alike; the `true` is what keeps this a discard
   * whatever the provider publishes next.
   *
   * **Also removes the exchange marker, and only that key.** A session has
   * begun, so no exchange is outstanding in this tab; a marker left behind
   * would make an answer-shaped link opened here later cost a discovery fetch.
   * Removed first, so a library that throws cannot leave it behind.
   */
  public forgetProviderToken(): void {
    unmarkExchange();
    this.oAuth.logOut(true);
  }
}
