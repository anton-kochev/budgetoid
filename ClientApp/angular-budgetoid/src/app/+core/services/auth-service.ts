import { DestroyRef, DOCUMENT, inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { ConfigurationService } from './configuration.service';
import { ProviderDepartureService } from './provider-departure.service';

// The mark `signIn` leaves in this tab's `sessionStorage` immediately before
// it sends the person to the provider, and the second half of what makes a
// page load the provider coming back — see `providerReturn`. Its own key,
// not the library's `nonce`: that one outlives the exchange it was written
// for, and this one's lifetime is this service's to decide.
//
// `sessionStorage` because it is per tab and survives the top-level round
// trip to the provider, which is exactly the span an exchange is outstanding.
// Read through the global, not the injected document's window: every access
// is guarded anyway, and a storage the browser refuses reads as no exchange.
//
// **The value says which trip this tab is on.** One key, because a tab is on
// at most one trip at a time, and `'started'` still means registration so a
// tab that left under the previous bundle comes back recognised.
const EXCHANGE_MARKER = 'budgetoid-provider-exchange';
const EXCHANGE_VALUES = {
  registration: 'started',
  'email-change': 'email-change',
} as const satisfies Record<ProviderTrip, string>;

/** Which of the two trips to the provider a page load is coming back from. */
export type ProviderTrip = 'registration' | 'email-change';

/**
 * What an email-change return left for the settings screen: the id token the
 * provider answered with, or the fact that the trip came back unconfirmed.
 */
export type EmailChangeReturn =
  | {
      readonly kind: 'answered';
      readonly idToken: string;
      readonly email: string;
    }
  | { readonly kind: 'unconfirmed' };

// The `email` member of an id token's claims, or `null`. **Narrowed, never
// asserted**: `getIdentityClaims()` is typed `object` and holds whatever the
// provider put there. Present-but-blank folds to `null`, because a screen
// naming it would name nobody. Reads that one member and no other.
function assertedEmail(claims: unknown): string | null {
  if (typeof claims !== 'object' || claims === null || !('email' in claims)) {
    return null;
  }

  const email: unknown = claims.email;

  return typeof email === 'string' && email.length > 0 ? email : null;
}

// Whether a fragment is shaped like a provider answer: a non-empty
// `access_token`, `id_token` and `state` together, or a non-empty `error` on
// its own. **Defined once**, because two questions ask it and must agree:
// `providerReturn` — is the provider answering this page load? — and
// `discardUnreadAnswer` — is there an answer on the address nobody read? A
// second spelling that drifted would either leave an answer standing that the
// return legs recognise, or take an in-page anchor that is somebody's link.
function answerShaped(fragment: URLSearchParams): boolean {
  const present = (key: string): boolean =>
    (fragment.get(key) ?? '').length > 0;

  return (
    (present('access_token') && present('id_token') && present('state')) ||
    present('error')
  );
}

// Resolves on the next macrotask, by which time every microtask queued before
// it has run. See `AuthService.leave` for why that is the right deadline.
function nextMacrotask(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function exchangeMark(): string | null {
  try {
    return sessionStorage.getItem(EXCHANGE_MARKER);
  } catch {
    // Unreadable storage is a tab that cannot show it started an exchange.
    // Asked from the `APP_INITIALIZER`, so a throw here is a blank page.
    return null;
  }
}

function markExchange(trip: ProviderTrip): boolean {
  try {
    sessionStorage.setItem(EXCHANGE_MARKER, EXCHANGE_VALUES[trip]);

    return true;
  } catch {
    return false;
  }
}

function unmarkExchange(): void {
  try {
    sessionStorage.removeItem(EXCHANGE_MARKER);
  } catch {
    // Swallowed: no caller may fail over a mark — `initialize` on the
    // `APP_INITIALIZER`, `forgetProviderToken` on a session being published,
    // and a trip being abandoned, which still has to put `departing` back. A
    // marker that survives costs what the residual in `providerReturn` already
    // names.
  }
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);
  private readonly document = inject(DOCUMENT);
  private readonly departure = inject(ProviderDepartureService);

  // The one preparation of the client this page load makes, shared by every
  // caller. Resolves `true` once the discovery document has loaded, `false`
  // when the provider could not be reached.
  //
  // **It loads the discovery document and reads no answer.** Reading the
  // answer is the return leg's alone — {@link initialize} calls `tryLogin`
  // after this resolves. Folded in here, every press would read the fragment
  // again: on a page whose answer was refused once, a press meets the same
  // stale answer, the library refuses it the same way, and Change answers
  // unavailable on every press until a reload.
  //
  // **Memoized here, and not at either caller, because this service is the
  // only thing both legs share.** The return leg asks from the
  // `APP_INITIALIZER`, the outbound leg asks from a button press — on the
  // registration screen or the settings screen — and on a page that came back
  // from the provider both happen: a person whose token lapsed presses
  // **Continue with Google** again, or one whose email change came back
  // unconfirmed presses **Change email address** again.
  // A flag at either caller cannot see the other; this field sees both, so the
  // provider hears from this page load at most once.
  //
  // **A success is held and a failure is not.** The field is cleared when the
  // load rejects, so the next press asks again. Held, one unreachable moment
  // would leave the provider button dead until a reload, with nothing on the
  // screen saying why a press does nothing. **A refused answer is not a
  // failure to prepare**: the document loaded, and forgetting it would cost the
  // next press a second fetch (NFR-025).
  private ready: Promise<boolean> | null = null;

  // How many times the client has opened an address, counted by the `openUri`
  // handed to `configure`. {@link leave} compares it across a press to tell a
  // departure from a press the library dropped without saying so.
  #departures = 0;

  // What an email-change return left for the settings screen, taken once.
  // **Memory only, and a `#` field**: the id token is a credential, and a copy
  // in any storage would outlive the page load that read it — a reload would
  // hand it over a second time. The library's own copy is discarded the moment
  // this one is taken; see {@link initialize}.
  #emailChangeReturn: EmailChangeReturn | null = null;

  // **A page restored from the back-forward cache is a trip abandoned, and
  // `pageshow` with `persisted` is the one moment this page learns it.** Back
  // from Google brings the page back with its memory as it left: the library
  // still believes an implicit flow is running and silently drops the next
  // `initLoginFlow`, the marker still says a trip is out, and `departing`
  // still holds the screen in "Taking you to Google…". All three are put back
  // by {@link abandonTrip}. A `pageshow` that restored nothing — a first load,
  // or a page the cache did not keep — has nothing stale in memory, and a trip
  // just started is not abandoned, so it is ignored.
  //
  // On the injected document's window, and only when it has one: a document
  // without a view has no page to restore. Removed with the injector, so a
  // torn-down one cannot reach into a later page's storage.
  constructor() {
    const view: Window | null | undefined = this.document.defaultView;

    if (view === null || view === undefined) {
      return;
    }

    const restored = (event: PageTransitionEvent): void => {
      if (event.persisted) {
        this.abandonTrip();
      }
    };

    view.addEventListener('pageshow', restored);
    inject(DestroyRef).onDestroy(() => {
      view.removeEventListener('pageshow', restored);
    });
  }

  /**
   * Configures the client from the loaded app config, fetches the provider's
   * discovery document and reads any answer the provider left on the URL.
   *
   * **This is what contacts the identity provider, so it runs only where a
   * trip needs it** (NFR-025): from the `APP_INITIALIZER` when
   * {@link providerReturn} says the provider is redirecting back, and from
   * {@link signIn} and {@link startEmailChange} before a trip starts. Run on
   * every cold load, it would tell Google the address and time of every visit
   * to the product, anonymous or signed in.
   *
   * The discovery fetch is made at most once per page load; see
   * {@link ready}. **Only this method reads the answer** — `tryLogin`, after
   * preparing — and a refusal there is the answer refused, never the provider
   * unreachable, so it leaves the prepared client in place.
   *
   * **Removes the answer from the address bar on every outcome, in place.**
   * The library is told not to clear it (`preventClearHashAfterLogin`),
   * because it clears by assigning `location.hash`, which pushes a history
   * entry and leaves the token-bearing one behind for Back to return to.
   * `history.replaceState` rewrites the entry the page is on, so a reload, a
   * bookmark or a copied link never carries a provider token. In `finally`,
   * because a refusal and an unreachable provider leave the answer standing
   * just as a success would.
   *
   * **Consumes the exchange marker once preparation has settled, whatever it
   * settled as.** By then any answer on the URL has been read, so no
   * exchange is outstanding in this tab. Left behind, the marker would make a
   * reload of an answer-shaped address — a history entry, a bookmark — prepare
   * the client again on every load. Not removed any earlier: until preparation
   * settles, the page is still the one the provider answered.
   *
   * **On an email-change return it also hands the answer over and discards
   * everything the library wrote for the trip.** The id token is kept in
   * memory for {@link takeEmailChangeReturn} only if the library validated it
   * — the token it stored is the one on this URL — and anything else is
   * `unconfirmed`: a refusal, a nonce that does not match, an answer
   * `tryLogin` rejected, a provider that could not be reached. Either way `logOut(true)` runs, because a nonce left
   * behind by a failed return is exactly what a crafted answer would need.
   *
   * **The address is read from the claims the library decoded, after it
   * validated the token and before `logOut(true)` discards them** — never
   * from the raw fragment, which anybody can write. A validated answer
   * asserting no non-empty address is `unconfirmed`: the settings screen names
   * that address, and could confirm nothing without one. The token and the
   * address are all that is kept; no other claim.
   */
  public async initialize(): Promise<void> {
    // Asked before anything is read: the `finally` below removes the fragment,
    // and the trip is judged by the page as it landed.
    const trip = this.providerReturn();
    const answered = trip === 'email-change' ? this.idTokenOnUrl() : null;

    try {
      const read = (await this.whenReady()) && (await this.readAnswer());

      if (trip === 'email-change') {
        const validated =
          read && answered !== null && this.oAuth.getIdToken() === answered;
        const email = validated
          ? assertedEmail(this.oAuth.getIdentityClaims())
          : null;

        this.#emailChangeReturn =
          validated && email !== null
            ? { kind: 'answered', idToken: answered, email }
            : { kind: 'unconfirmed' };
      }
    } finally {
      if (trip === 'email-change') {
        this.discardLibraryKeys();
      }
      unmarkExchange();
      this.removeFragment();
    }
  }

  // Whether the library read the answer on the address without refusing it.
  // A rejection — a nonce that does not match, a token that does not validate —
  // is swallowed, because the `APP_INITIALIZER` awaits {@link initialize}, and
  // is not written into {@link ready}: the discovery document still loaded.
  private async readAnswer(): Promise<boolean> {
    try {
      // The library leaves the fragment alone; see {@link initialize}.
      await this.oAuth.tryLogin({ preventClearHashAfterLogin: true });

      return true;
    } catch {
      return false;
    }
  }

  /**
   * Removes a provider answer nobody read from the address bar, and leaves any
   * other fragment where it is.
   *
   * The `APP_INITIALIZER`'s last step, after every leg that might have read the
   * answer: a registration answer reaching a signed-in visitor, whose leg is
   * skipped, or an answer in a tab that started no trip. Each would otherwise
   * stay in the address bar, and a reload, a
   * bookmark or a copied link would carry it on. A leg that did read the answer
   * has already removed it, so this finds nothing.
   *
   * **Answer-shaped only — the same predicate {@link providerReturn} asks.** An
   * in-page anchor is somebody's link, not a credential.
   */
  public discardUnreadAnswer(): void {
    if (answerShaped(this.fragment())) {
      this.removeFragment();
    }
  }

  // Rewrites the current history entry to the same address without its
  // fragment. **`replaceState`, never `pushState` or a `location.hash` write**:
  // both of those add an entry and leave the one holding the token behind for
  // Back. The entry's existing state is carried over, because the router keeps
  // its navigation id there.
  //
  // **Guarded on the document's view, and swallowed.** A document without a
  // view has no history to rewrite. A throw — browsers throttle a page that
  // calls `replaceState` too often, and it refuses an address on another
  // origin, which only a document whose location is not its view's could build
  // — must not reject the `APP_INITIALIZER`, which awaits both callers.
  private removeFragment(): void {
    const view: Window | null | undefined = this.document.defaultView;

    if (view === null || view === undefined) {
      return;
    }

    const { origin, pathname, search } = this.document.location;

    try {
      view.history.replaceState(
        view.history.state,
        '',
        origin + pathname + search,
      );
    } catch {
      // Swallowed; see above. The answer stays on the address, which is where
      // it was before this ran.
    }
  }

  // The local-discard overload; see {@link forgetProviderToken}. Swallowed,
  // because the `APP_INITIALIZER` awaits the one caller.
  private discardLibraryKeys(): void {
    try {
      this.oAuth.logOut(true);
    } catch {
      // Carried on past: the marker removal in `initialize` still follows,
      // and without the marker a later load is not read as a return.
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
      // **How the client leaves the page, handed to the departure service.**
      // It raises `departing` and assigns the address — the same top-level
      // navigation the library's default makes — so the screen and a restore
      // from the back-forward cache share one flag. Counted on the way, for
      // {@link leave}. An arrow, because the library calls it unbound.
      openUri: (uri: string): void => {
        this.#departures += 1;
        this.departure.depart(uri);
      },
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
    // as usual. Only registration and the email change are unavailable, and
    // they were unavailable anyway — the provider both depend on is the thing
    // that could not be reached.
    //
    // Nothing is re-thrown and nothing is published. Registration reads
    // `providerEmail()`, which already answers `null` for a browser that never
    // completed an exchange; an email-change return reads the `false` in
    // {@link initialize} and hands over `unconfirmed`. The `false` is otherwise
    // for {@link signIn} and {@link startEmailChange}, which must not send
    // anybody to a login endpoint nobody has learned.
    //
    // **Nothing schedules a silent refresh, and the omission is the rule.**
    // `setupAutomaticSilentRefresh()` used to sit on the next line; it plants a
    // hidden iframe pointed at `accounts.google.com` and re-runs it on a timer
    // for as long as the tab is open. A provider token is now used **once per
    // trip**: registration's on the registration screen, discarded by
    // `forgetProviderToken()` whenever `SessionService` publishes a session;
    // the email change's handed over in memory by {@link initialize}, which
    // discards the library's copy itself. Every other request authenticates
    // from the first-party session cookie. Refreshing either would be
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
      await this.oAuth.loadDiscoveryDocument();

      return true;
    } catch {
      // Intentionally swallowed; see above. Forgotten, so the next ask retries
      // — see {@link ready}.
      this.ready = null;

      return false;
    }
  }

  /**
   * Which trip this page load is the provider redirecting back from, or `null`.
   *
   * **Three things, all required, and none is enough alone:**
   *
   * - **This tab started that trip.** {@link signIn} and
   *   {@link startEmailChange} each leave a marker in `sessionStorage`
   *   immediately before leaving for the provider, its value naming the trip,
   *   and {@link initialize} consumes it once preparation settles. An
   *   answer-shaped address is something anybody can put in a link; only a
   *   tab that pressed a provider button is waiting for one, so a crafted
   *   link opened anywhere else costs no request to Google (NFR-025).
   * - **That trip's redirect address — origin and path, compared for
   *   equality** — `redirectUri` for registration, `emailChangeRedirectUri`
   *   for the email change. A marker and an address from two different trips
   *   are nobody's return.
   * - **An answer in the fragment**, parsed by key and never matched as a
   *   substring: a
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
   * `null` rather than throwing, because the `APP_INITIALIZER` asks this.
   *
   * Read from the document rather than the router: this is asked by the
   * `APP_INITIALIZER`, before the router has navigated anywhere — see
   * `core.providers.ts` for why it has to be that early.
   */
  public providerReturn(): ProviderTrip | null {
    const google = this.config.getConfig().auth.google;

    if (!answerShaped(this.fragment())) {
      return null;
    }

    const mark = exchangeMark();

    if (
      mark === EXCHANGE_VALUES['email-change'] &&
      this.landedOn(google?.emailChangeRedirectUri)
    ) {
      return 'email-change';
    }

    if (
      mark === EXCHANGE_VALUES.registration &&
      this.landedOn(google?.redirectUri)
    ) {
      return 'registration';
    }

    return null;
  }

  // Whether the page sits at `address`'s origin and path, exactly. Origins,
  // not a `startsWith`: `https://budgetoid.app.example` starts with
  // `https://budgetoid.app`.
  private landedOn(address: string | undefined): boolean {
    if (address === undefined || !URL.canParse(address)) {
      return false;
    }

    const expected = new URL(address);
    const landed = new URL(this.document.location.href);

    return (
      landed.origin === expected.origin && landed.pathname === expected.pathname
    );
  }

  private fragment(): URLSearchParams {
    // `URLSearchParams` drops one leading `?` itself, but not a `#`.
    return new URLSearchParams(
      new URL(this.document.location.href).hash.slice(1),
    );
  }

  private idTokenOnUrl(): string | null {
    const idToken = this.fragment().get('id_token');

    return idToken !== null && idToken.length > 0 ? idToken : null;
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

    return assertedEmail(this.oAuth.getIdentityClaims());
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
   * then** — see {@link providerReturn}. Not at the press: a press that
   * could not reach the provider starts no round trip and must leave nothing
   * that makes a later load look like one coming back. A marker the browser
   * will not store means the answer would be refused on the way back, so the
   * trip is not started either: a press that does nothing beats a round trip
   * to Google that lands on a screen reading as if nothing happened.
   *
   * **Puts the registration redirect address back before leaving.** The
   * address is a property on the one shared client, and
   * {@link startEmailChange} writes its own there; without this, a
   * registration press after it would come back to the settings screen.
   *
   * Raises and lowers `departing` exactly as {@link startEmailChange} does —
   * see {@link startTrip}. Nothing on the registration screen reads it today;
   * it is raised here because this service is its one writer, and a writer
   * that raised it for one trip and not the other would leave the flag
   * meaning "an email change is leaving" under a name that says otherwise.
   */
  public signIn(): void {
    void this.startTrip('registration', () => {
      this.oAuth.initLoginFlow();
    });
  }

  /**
   * Starts the email change's trip: a top-level navigation to the provider's
   * account chooser, which redirects back to the settings screen.
   *
   * **The same client and the same preparation as {@link signIn}**, so a page
   * load makes one discovery fetch however many trips it starts. The redirect
   * address is written as a property after preparing, never through a second
   * `configure()`: that call resets the login endpoint the discovery document
   * taught the client, and preparing again runs `configure()` itself.
   *
   * **Marks the tab as on an email change only once the provider has been
   * reached**, for {@link signIn}'s reason. Answers `'unavailable'` — and has
   * contacted nobody when the address or the scope is not configured —
   * whenever the page is not about to leave, so the caller can say so; it never
   * rejects. **`'leaving'` means the client opened the address**, not that it
   * was asked to; see {@link leave}.
   */
  public startEmailChange(): Promise<'leaving' | 'unavailable'> {
    return this.startTrip('email-change', () => {
      this.oAuth.initLoginFlow('', { prompt: 'select_account' });
    });
  }

  // The one way a press leaves for the provider, for both trips.
  //
  // **The configuration is checked before anything else, and before
  // `departing` is raised.** Without a redirect address there is nowhere to
  // come back to, and without a scope the library builds the address with
  // `scope.match(…)` inside a promise, rejects, and prints the rejection to the
  // console itself — past `logFailure` — on a press that goes nowhere. Neither
  // is worth a discovery fetch to find out.
  //
  // **`departing` is raised before the first await**, so the screen's control
  // goes off in the same turn it was pressed and a second press in that turn
  // finds it off. From there every way out that does not leave goes through
  // {@link abandonTrip}, which lowers it again — or the screen stays held off
  // by a departure that never happened.
  //
  // The `catch` is for `start` throwing synchronously — the library refuses a
  // login endpoint it will not use — and for nothing else that can fail here:
  // {@link whenReady} and the mark never reject.
  private async startTrip(
    trip: ProviderTrip,
    start: () => void,
  ): Promise<'leaving' | 'unavailable'> {
    const google = this.config.getConfig().auth.google;
    const redirectUri =
      trip === 'registration'
        ? google?.redirectUri
        : google?.emailChangeRedirectUri;

    if (redirectUri === undefined || google?.scope === undefined) {
      return 'unavailable';
    }

    this.departure.begin();

    try {
      if ((await this.whenReady()) && markExchange(trip)) {
        this.oAuth.redirectUri = redirectUri;

        if (await this.leave(start)) {
          return 'leaving';
        }
      }
    } catch {
      // Abandoned below, like every other press that did not leave.
    }

    this.abandonTrip();

    return 'unavailable';
  }

  // Whether `start` made the client open an address.
  //
  // **Asked, because `initLoginFlow` returning says nothing.** On the implicit
  // flow it returns at once and builds the address through a chain of
  // promises, calling `openUri` at the end of it, and it drops a press two ways
  // without throwing: a rejection anywhere in that chain — storage refusing
  // the library's nonce, a missing scope — which it only prints; and a flow it
  // believes is already running, which returns before building anything. A
  // press read as leaving on either sits in "Taking you to Google…" forever.
  //
  // **One macrotask is the deadline**, because every microtask queued before
  // it runs first, and the chain is promises over synchronous work —
  // `createNonce` draws from `crypto.getRandomValues`. The code flow's PKCE
  // digest is real asynchronous work that could miss it, which is one more
  // reason that flow stays off; the configure key-set pin in
  // `auth-service.spec.ts` refuses its `responseType`.
  //
  // A count rather than a one-shot hook: a hook a later press replaced would
  // read the earlier press's departure as its own, and abandon a trip that left.
  private async leave(start: () => void): Promise<boolean> {
    const before = this.#departures;

    start();
    await nextMacrotask();

    return this.#departures !== before;
  }

  // Puts back everything a trip that did not leave — or a page restored from
  // the back-forward cache — would otherwise leave standing. **All three, on
  // every such path**: the marker, or a later answer-shaped link reads as a
  // return; the library's running flow, or it silently drops the next press;
  // and `departing`, or the screen stays held off. Lowered last, so the screen
  // never offers a press the first two would still drop.
  private abandonTrip(): void {
    unmarkExchange();
    this.oAuth.resetImplicitFlow();
    this.departure.settle();
  }

  /**
   * What the email-change return on this page load left, handed over once:
   * a second call answers `null`.
   */
  public takeEmailChangeReturn(): EmailChangeReturn | null {
    const handedOver = this.#emailChangeReturn;
    this.#emailChangeReturn = null;

    return handedOver;
  }

  /** Discards what an email-change return left, untaken. */
  public dropEmailChangeReturn(): void {
    this.#emailChangeReturn = null;
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
   * **Never on an `anonymous` or `unreachable` probe.** On a registration
   * return the probe runs before {@link initialize} reads the answer off the
   * URL, and a discard there takes the library's nonce with the tokens, so the
   * answer no longer validates. The arm is chosen in `SessionService`; this
   * method only discards.
   *
   * On an email-change return the order is the other way round:
   * {@link initialize} runs before the probe and has already run
   * `logOut(true)` and removed the marker, so the authenticated arm's discard
   * finds nothing left to take.
   *
   * **Not a sign-out.** A discard ends nothing the person can see; a sign-out
   * ends their visit, and it is first-party — the settings screen ends the
   * session on this application's own API, which never involves the provider.
   * This service offers no provider sign-out at all: the argument-less
   * `logOut()` navigates to the provider's end-session endpoint whenever the
   * library knows one, and a contact with Google on a person's own action is
   * outside every moment NFR-025 permits — of its three, this product builds
   * registration's exchange and the email change's trip, and neither is a
   * sign-out. Google's discovery document publishes
   * no `end_session_endpoint` today, so against this configuration the two
   * overloads happen to behave alike; the `true` is what keeps this a discard
   * whatever the provider publishes next.
   *
   * **Also removes the exchange marker, and only that key.** A session has
   * begun, so no exchange is outstanding in this tab; a marker left behind
   * would make an answer-shaped link opened here later cost a discovery fetch.
   * Removed first, so a library that throws cannot leave it behind.
   *
   * **Never touches the email-change hand-off.** That is this service's memory,
   * not the library's storage, and a session being published is no reason for
   * the settings screen to lose an answer it has not read yet.
   */
  public forgetProviderToken(): void {
    unmarkExchange();
    this.oAuth.logOut(true);
  }
}
