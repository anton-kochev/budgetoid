import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  type AuthConfig,
  OAuthEvent,
  OAuthService,
  provideOAuthClient,
} from 'angular-oauth2-oidc';
import { isObservable, Observable, Subject } from 'rxjs';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  type Mock,
  type MockInstance,
  vi,
} from 'vitest';
import { AuthService } from './auth-service';
import { ConfigurationService } from './configuration.service';

// FR-086: no image supplied by the identity provider is displayed. This pins something
// stronger than dropping the picture claim — the app reads no ID-token claim at all. A
// claim the app never holds is a claim no component can render, and reading none of them
// is what makes asking Google for only `openid email` safe to keep: a claim nobody
// consumes is a scope nobody needs.
//
// The check subscribes to every observable AuthService exposes, because a claim read
// inside a cold observable stays invisible until something subscribes.
// The mark `AuthService.signIn` leaves in this tab's `sessionStorage` just
// before it sends the person to the provider. Spelled here rather than
// imported: it is module-private in the subject, and a rename there is a
// change to what every open tab holds.
const EXCHANGE_MARKER = 'budgetoid-provider-exchange';

const PROVIDER_ANSWER =
  'https://budgetoid.app/register#access_token=a&id_token=b&state=c';

function exposedObservables(service: AuthService): Observable<unknown>[] {
  const members = service as unknown as Record<string, unknown>;

  return Object.keys(members)
    .map((key) => members[key])
    .filter(isObservable);
}

describe('AuthService', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  afterEach(() => {
    sessionStorage.clear();
  });

  it('reads no claim from the ID token', () => {
    // Arrange
    const events = new Subject<OAuthEvent>();
    const getIdentityClaims = vi.fn(() => ({
      email: 'someone@example.com',
      name: 'Someone',
      picture: 'https://lh3.googleusercontent.com/a/photo',
    }));
    const oAuth = {
      events,
      getIdentityClaims,
      hasValidAccessToken: () => true,
      hasValidIdToken: () => true,
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        { provide: ConfigurationService, useValue: {} },
      ],
    });
    const service = TestBed.inject(AuthService);
    const subscriptions = exposedObservables(service).map((observable) =>
      observable.subscribe(),
    );

    // Act
    events.next({ type: 'discovery_document_loaded' });
    events.next({ type: 'token_received' });
    subscriptions.forEach((subscription) => {
      subscription.unsubscribe();
    });

    // Assert
    expect(getIdentityClaims).not.toHaveBeenCalled();
  });

  // `core.providers.ts` awaits this method inside the `APP_INITIALIZER` when a
  // registration comes back from the provider, so a rejection here is not a
  // degraded registration — it is an application that never finishes
  // bootstrapping and a browser left on a blank page. The discovery document
  // lives on `accounts.google.com`, which an outage, a blocked host, a captive
  // portal or a corporate proxy each make unreachable, and none of those says
  // anything about the first-party session cookie the rest of the app runs on.
  it('finishes initializing when the provider cannot be reached', async () => {
    // Arrange
    const oAuth = {
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() =>
        Promise.reject(new Error('The discovery document is unreachable.')),
      ),
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: '', auth: {} }) },
        },
      ],
    });
    const service = TestBed.inject(AuthService);

    // Act & Assert
    await expect(service.initialize()).resolves.toBeUndefined();
  });

  // **Nothing schedules a background renewal of the provider token.** A
  // registration's token is used on the registration screen and discarded the
  // moment this tab learns it holds a session — `SessionService` owns the
  // discard; an email change's is handed over in memory and its stored copy
  // discarded on the return. Every request after either authenticates from the
  // first-party session cookie, so nothing reads a stored one again.
  // Scheduling a renewal — which is what `setupAutomaticSilentRefresh()` does —
  // plants a hidden iframe pointed at `accounts.google.com` and re-runs it on a
  // timer for as long as the tab is open: a third-party request on every page
  // of the product, forever, to keep alive a credential nobody reads, in an
  // application that contacts the provider on two acts and at no other time —
  // creating an account, and changing its email address.
  //
  // The assertion has to sit on the **success** path. On the failure path the
  // scheduling could never have run anyway — it followed the line that throws —
  // so an assertion there discriminates nothing and would pass over a restored
  // call.
  //
  // `src/no-external-origins.spec.ts` holds the spelling: it scans the
  // production bundle and refuses a `setupAutomaticSilentRefresh` call by name.
  // Its origin check could not have — a restored renewal adds no origin the
  // bundle does not already carry. This case holds the behaviour of the one
  // path that used to make the call, against a stub.
  it('schedules no background renewal of the provider token', async () => {
    // Arrange
    const loadDiscoveryDocumentAndTryLogin = vi.fn(() => Promise.resolve(true));
    const setupAutomaticSilentRefresh = vi.fn();
    const oAuth = {
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin,
      setupAutomaticSilentRefresh,
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: '', auth: {} }) },
        },
      ],
    });
    const service = TestBed.inject(AuthService);

    // Act
    await service.initialize();

    // Assert
    // The first expectation is not decoration: without it an `initialize()`
    // that had been emptied out entirely would satisfy the second one.
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledOnce();
    expect(setupAutomaticSilentRefresh).not.toHaveBeenCalled();
  });

  // **The client runs the implicit flow, and that is chosen by leaving
  // `responseType` out.** Under `responseType: 'code'` the library exchanges
  // the code itself, and that exchange writes the token endpoint's raw failure
  // and an id-token rejection naming two subjects to the console directly,
  // past the funnel's logger. `no-console-outside-funnel.spec.ts` holds the
  // spelling in the source; this holds what reaches `configure`.
  it('configures the client without choosing a response type', async () => {
    // Arrange
    const configure = vi.fn();
    const service = authServiceOver({
      configure,
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
    });

    // Act
    await service.initialize();

    // Assert
    expect(configure).toHaveBeenCalledOnce();
    expect(configure.mock.calls[0]?.[0]).not.toHaveProperty('responseType');
  });

  // NFR-025. The key set, not a list of refusals: every contact this client
  // could make on its own is switched on by a key, and the ones that matter are
  // not the ones anybody would think to refuse by name. `sessionChecksEnabled`
  // plants an iframe polling the provider's session endpoint, `useSilentRefresh`
  // and `silentRefreshRedirectUri` arm the hidden-iframe renewal, `openUri`
  // replaces how the client leaves the page, `responseType` swaps the flow for
  // one that posts to the token endpoint. Equality refuses any key passed to
  // `configure` beyond these, named here or not. It cannot see a library
  // default that turns a contact on with no key passed, nor a direct property
  // write on `OAuthService` that bypasses `configure`.
  it('configures the client with exactly the keys it needs', async () => {
    // Arrange
    const configure = vi.fn();
    const service = authServiceOver({
      configure,
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
    });

    // Act
    await service.initialize();

    // Assert
    expect(configure).toHaveBeenCalledOnce();
    const keys = Object.keys(
      (configure.mock.calls[0]?.[0] ?? {}) as Record<string, unknown>,
    ).sort();
    expect(keys).toEqual([
      'clientId',
      'issuer',
      'redirectUri',
      'scope',
      'strictDiscoveryDocumentValidation',
    ]);
  });

  // NFR-025 is about how often the provider hears from this browser, so a second
  // ask — the return leg's initializer and then a press of the provider button
  // on the same page, or two presses — must not be a second discovery fetch.
  it('fetches the discovery document once however many times it is asked', async () => {
    // Arrange
    const loadDiscoveryDocumentAndTryLogin = vi.fn(() => Promise.resolve(true));
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin,
    });

    // Act
    await Promise.all([service.initialize(), service.initialize()]);
    await service.initialize();

    // Assert
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledOnce();
  });

  // The memo holds a success, never a failure. Held, one unreachable moment
  // would leave the provider button dead until a reload, with nothing on the
  // screen saying why a press does nothing.
  it('asks the provider again after it could not be reached', async () => {
    // Arrange
    const loadDiscoveryDocumentAndTryLogin = vi
      .fn<() => Promise<boolean>>()
      .mockRejectedValueOnce(
        new Error('The discovery document is unreachable.'),
      )
      .mockResolvedValueOnce(true);
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin,
    });
    await service.initialize();

    // Act
    await service.initialize();

    // Assert
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledTimes(2);
  });

  // Nothing configures the client at bootstrap any more, so the press that
  // starts the exchange is the first moment anything knows the provider's
  // login endpoint — which lives in the discovery document.
  it('loads the discovery document before starting the exchange', async () => {
    // Arrange
    const reached: string[] = [];
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => {
        reached.push('loadDiscoveryDocumentAndTryLogin');

        return Promise.resolve(true);
      }),
      initLoginFlow: vi.fn(() => {
        reached.push('initLoginFlow');
      }),
    });

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(reached).toEqual([
      'loadDiscoveryDocumentAndTryLogin',
      'initLoginFlow',
    ]);
  });

  it('starts the exchange without a second fetch once the client is prepared', async () => {
    // Arrange
    const loadDiscoveryDocumentAndTryLogin = vi.fn(() => Promise.resolve(true));
    const initLoginFlow = vi.fn();
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin,
      initLoginFlow,
    });
    await service.initialize();

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(initLoginFlow).toHaveBeenCalledOnce();
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledOnce();
  });

  // With no discovery document there is no login endpoint to send anybody to;
  // the library would throw from inside a promise nothing awaits.
  it('starts no exchange when the provider could not be reached', async () => {
    // Arrange
    const initLoginFlow = vi.fn();
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() =>
        Promise.reject(new Error('The discovery document is unreachable.')),
      ),
      initLoginFlow,
    });

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(initLoginFlow).not.toHaveBeenCalled();
  });

  // **The marker is what makes a later page load the provider coming back**,
  // so it has to be in place by the time the page leaves — and the page leaves
  // inside `initLoginFlow`. Read at that moment, not afterwards.
  it('marks the tab as mid-exchange before sending anybody to the provider', async () => {
    // Arrange
    const markerAtDeparture: (string | null)[] = [];
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
      initLoginFlow: vi.fn(() => {
        markerAtDeparture.push(sessionStorage.getItem(EXCHANGE_MARKER));
      }),
    });

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(markerAtDeparture).toHaveLength(1);
    expect(markerAtDeparture[0]).not.toBeNull();
  });

  // A press that goes nowhere starts no round trip, so it leaves nothing that
  // would make a later page load look like one coming back.
  it('leaves no marker when the provider could not be reached', async () => {
    // Arrange
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() =>
        Promise.reject(new Error('The discovery document is unreachable.')),
      ),
      initLoginFlow: vi.fn(),
    });

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // The passkey step's re-press on a page that came back from the provider:
  // `initialize()` has already consumed the marker, and the second trip needs
  // its own.
  it('marks the tab again on a press after the return leg consumed the marker', async () => {
    // Arrange
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
      initLoginFlow: vi.fn(),
    });
    await service.initialize();

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).not.toBeNull();
  });

  // Without the marker the answer would be refused on the way back, so the
  // trip is not worth starting: a press that does nothing beats a round trip
  // to Google that ends on a screen reading as if nothing happened.
  it('starts no exchange when the marker cannot be written', async () => {
    // Arrange
    const initLoginFlow = vi.fn();
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
      initLoginFlow,
    });
    const setItem = vi
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(() => {
        throw new DOMException('The quota has been exceeded.');
      });

    try {
      // Act
      service.signIn();
      await afterPendingWork();
    } finally {
      setItem.mockRestore();
    }

    // Assert
    expect(initLoginFlow).not.toHaveBeenCalled();
  });

  // **Consumed once the return leg has read the answer, whatever the answer
  // was.** Left behind, a reload of `/register` — whose fragment the library
  // has already cleared, but a bookmark or history entry may not have — would
  // prepare the client again and again.
  it('removes the marker once initializing has succeeded', async () => {
    // Arrange
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
    });
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');

    // Act
    await service.initialize();

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  it('removes the marker when initializing could not reach the provider', async () => {
    // Arrange
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() =>
        Promise.reject(new Error('The discovery document is unreachable.')),
      ),
    });
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');

    // Act
    await service.initialize();

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // **The `APP_INITIALIZER` awaits this**, so a marker the browser refuses to
  // remove must not reject it: that is a blank page over a key whose survival
  // costs only the residual `providerReturn` names. The removal runs after
  // the preparation settles, so the spy stays in place until `initialize()`
  // has settled too.
  it('finishes initializing when the marker cannot be removed', async () => {
    // Arrange
    const service = authServiceOver({
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() => Promise.resolve(true)),
    });
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const removeItem = vi
      .spyOn(Storage.prototype, 'removeItem')
      .mockImplementation(() => {
        throw new DOMException('Access is denied.', 'SecurityError');
      });

    // Act
    let outcome: PromiseSettledResult<void> | undefined;
    let removals = 0;
    try {
      [outcome] = await Promise.allSettled([service.initialize()]);
      removals = removeItem.mock.calls.length;
    } finally {
      removeItem.mockRestore();
    }

    // Assert
    // The count is what makes the settlement discriminate: an `initialize()`
    // that never reached the removal would resolve too.
    expect(removals).toBeGreaterThan(0);
    expect(outcome).toEqual({ status: 'fulfilled', value: undefined });
  });

  // The provider's answer arrives on the configured redirect address, in the
  // fragment where the implicit flow puts it, and in a tab that started an
  // exchange — that is the only cold load on which the bootstrap may contact
  // the provider. Every case below but the last group seeds the marker, so
  // each is a statement about the address alone.
  it.each([
    {
      shape: 'an implicit-flow answer in the fragment',
      href: 'https://budgetoid.app/register#access_token=a&id_token=b&state=c',
    },
    {
      shape: 'a refusal in the fragment',
      href: 'https://budgetoid.app/register#error=access_denied&state=c',
    },
    // Google's implicit-flow refusal carries no state, and the library rejects
    // on it without asking for one — so neither does this.
    {
      shape: 'a refusal in the fragment without a state',
      href: 'https://budgetoid.app/register#error=access_denied',
    },
  ])(
    'providerReturn recognises $shape as a registration coming back',
    ({ href }) => {
      // Arrange
      sessionStorage.setItem(EXCHANGE_MARKER, 'started');
      const service = authServiceOver({}, { href });

      // Act & Assert
      expect(service.providerReturn()).toBe('registration');
    },
  );

  it.each([
    // Somebody opening the registration screen has not been to the provider
    // yet; the press on the screen is what contacts it.
    {
      shape: 'the redirect address carrying nothing',
      href: 'https://budgetoid.app/register',
    },
    {
      shape: 'another screen',
      href: 'https://budgetoid.app/welcome#access_token=a&id_token=b&state=c',
    },
    {
      shape: 'the same path on another origin',
      href: 'https://budgetoid.example/register#access_token=a&id_token=b&state=c',
    },
    // Something after the path that is not an answer the library acts on. A
    // campaign link or an in-page anchor is still somebody opening the screen.
    {
      shape: 'a campaign parameter in the query',
      href: 'https://budgetoid.app/register?utm_source=newsletter',
    },
    {
      shape: 'an in-page anchor',
      href: 'https://budgetoid.app/register#section',
    },
    // An anchor named error is not a refusal anybody sent; the library would
    // reject it, but only after fetching the discovery document.
    {
      shape: 'an in-page anchor named error',
      href: 'https://budgetoid.app/register#error',
    },
    {
      shape: 'a fragment carrying only a state',
      href: 'https://budgetoid.app/register#state=c',
    },
    {
      shape: 'a fragment missing the id token',
      href: 'https://budgetoid.app/register#access_token=a&state=c',
    },
    // Both tokens without the state that binds them to a request: the library
    // validates only when all three keys are there.
    {
      shape: 'a fragment carrying both tokens and no state',
      href: 'https://budgetoid.app/register#access_token=a&id_token=b',
    },
    // A key that is present but empty is not a token. Asking only whether the
    // key exists would read this as an answer.
    {
      shape: 'a fragment naming an empty access token',
      href: 'https://budgetoid.app/register#access_token=&id_token=b&state=c',
    },
    // The library reads an implicit-flow answer from the fragment only.
    {
      shape: 'an implicit-flow answer in the query',
      href: 'https://budgetoid.app/register?access_token=a&id_token=b&state=c',
    },
    // Nothing in the query is an answer. This client runs the implicit flow,
    // `tryLogin` never reaches the code-flow parser without `responseType:
    // 'code'`, and the key-set pin above refuses that key — so even a complete
    // code-flow answer is somebody opening the screen.
    {
      shape: 'a code-flow answer in the query',
      href: 'https://budgetoid.app/register?code=a&state=c',
    },
    {
      shape: 'a refusal in the query',
      href: 'https://budgetoid.app/register?error=x&state=c',
    },
    {
      shape: 'a code in the query without a state',
      href: 'https://budgetoid.app/register?code=a',
    },
    {
      shape: 'a refusal in the query without a state',
      href: 'https://budgetoid.app/register?error=x',
    },
    // The library reads a code-flow answer from the query only.
    {
      shape: 'a code-flow answer in the fragment',
      href: 'https://budgetoid.app/register#code=a&state=c',
    },
    {
      shape: 'a fragment refusal naming no error',
      href: 'https://budgetoid.app/register#error=',
    },
  ])(
    'providerReturn does not read $shape as the provider coming back',
    ({ href }) => {
      // Arrange
      sessionStorage.setItem(EXCHANGE_MARKER, 'started');
      const service = authServiceOver({}, { href });

      // Act & Assert
      expect(service.providerReturn()).toBeNull();
    },
  );

  it('providerReturn reads nothing as the provider coming back when no redirect address is configured', () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const service = authServiceOver(
      {},
      {
        href: 'https://budgetoid.app/register#access_token=a',
        redirectUri: null,
      },
    );

    // Act & Assert
    expect(service.providerReturn()).toBeNull();
  });

  // **An answer-shaped address is not enough on its own.** Anybody can craft
  // one into a link; only a tab that pressed the provider button is waiting
  // for an answer, and a crafted link opened anywhere else must cost no
  // request to Google.
  it('providerReturn does not read a full answer as the provider coming back in a tab that started no exchange', () => {
    // Arrange
    const service = authServiceOver({}, { href: PROVIDER_ANSWER });

    // Act & Assert
    expect(service.providerReturn()).toBeNull();
  });

  // The library writes its `nonce` before the trip too, but it is the
  // library's key and outlives the exchange it was written for. The marker is
  // this service's own, so its lifetime is this service's to decide.
  it("providerReturn does not take the library's nonce for the marker", () => {
    // Arrange
    sessionStorage.setItem('nonce', 'library-nonce');
    const service = authServiceOver({}, { href: PROVIDER_ANSWER });

    // Act & Assert
    expect(service.providerReturn()).toBeNull();
  });

  // Storage a browser refuses to read is a tab that cannot show it started an
  // exchange. `APP_INITIALIZER` asks this, so a throw here is a blank page.
  it('providerReturn reads storage it cannot open as no exchange, without throwing', () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const service = authServiceOver({}, { href: PROVIDER_ANSWER });
    const getItem = vi
      .spyOn(Storage.prototype, 'getItem')
      .mockImplementation(() => {
        throw new DOMException('Access is denied.', 'SecurityError');
      });

    // Act
    let answer: ReturnType<AuthService['providerReturn']> | undefined;
    try {
      answer = service.providerReturn();
    } finally {
      getItem.mockRestore();
    }

    // Assert
    expect(answer).toBeNull();
  });

  // A question, not a consumption: the initializer asks it and then prepares,
  // and the removal belongs to the preparation that settles.
  it('providerReturn answers the same when asked twice, and leaves the marker in place', () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const service = authServiceOver({}, { href: PROVIDER_ANSWER });

    // Act
    const answers = [service.providerReturn(), service.providerReturn()];

    // Assert
    expect(answers).toEqual(['registration', 'registration']);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBe('started');
  });

  // The `email` claim and nothing else. `openid email` already carries it, so
  // reading it widens no scope — and the assertion that no *other* claim is
  // read is what keeps that true: `name` costs nothing to add and `picture` is
  // an image from another origin, which this application does not load at all.
  it('reads the address the provider asserted, and nothing else', () => {
    // Arrange
    // A proxy rather than a plain object, because the question is which claims
    // were *touched*. `'email' in claims` goes through the `has` trap, so only
    // a genuine read of a member's value is recorded here.
    const read: string[] = [];
    const claims = new Proxy(
      {
        email: 'owner@budgetoid.test',
        name: 'Owner',
        picture: 'https://lh3.googleusercontent.com/a/photo',
        sub: '1234567890',
      },
      {
        get(target, property, receiver): unknown {
          if (typeof property === 'string') {
            read.push(property);
          }

          return Reflect.get(target, property, receiver);
        },
      },
    );
    const service = authServiceReading(() => claims);

    // Act
    const email = service.providerEmail();

    // Assert
    expect(email).toBe('owner@budgetoid.test');
    expect(read).toEqual(['email']);
  });

  // **The claim outlives the token, and reading it without asking is what put a
  // dead `Continue` on the registration screen.** `getIdentityClaims()` answers
  // out of storage and keeps answering for as long as the browser holds the
  // decoded token — an hour after it expired, a day after. The registration
  // screen renders "your account will be created under <address>" from this
  // method, and both legs behind that press are declared on the provider scheme
  // and nothing else: a stale claim therefore promises an account, offers a
  // control, and reaches a 401 that says nothing a person can act on.
  //
  // The address is not the only thing that goes with the token. The `sub` the
  // account is created under is read off the principal the *server* resolves, so
  // an expired token has no identity behind it at all — which is why `null` here
  // is the honest answer rather than a cautious one.
  it('reads no address from an expired id token', () => {
    // Arrange
    const service = authServiceReading(
      () => ({ email: 'owner@budgetoid.test' }),
      false,
    );

    // Act & Assert
    expect(service.providerEmail()).toBeNull();
  });

  it.each([
    // Present but blank is not an address. A screen rendering one shows a label
    // with nothing after it, which reads as a bug rather than as absence.
    { shape: 'an empty address', claims: (): object => ({ email: '' }) },
    { shape: 'a claims object with no address', claims: (): object => ({}) },
    {
      shape: 'a claims object carrying other members only',
      claims: (): object => ({ name: 'Owner', sub: '1234567890' }),
    },
    // What `getIdentityClaims()` answers before any exchange has completed.
    // Typed `object` and null at runtime, which is why every step down to a
    // `string` in the subject is checked rather than asserted.
    { shape: 'absent claims', claims: (): object => null as unknown as object },
  ])('reads no address from $shape', ({ claims }) => {
    // Arrange
    const service = authServiceReading(claims);

    // Act & Assert
    expect(service.providerEmail()).toBeNull();
  });

  // **A discard, never a sign-out.** `logOut()` with no argument is the
  // library's sign-out: once a discovery document has named an end-session
  // endpoint, it navigates the whole page to Google and ends the person's
  // Google session on their behalf. `logOut(true)` is the local-discard
  // overload, and the flag is the entire difference between the two — so it
  // is asserted as the argument, not as "`logOut` was reached".
  it('forgetProviderToken discards locally and does not redirect', () => {
    // Arrange
    const logOut = vi.fn();
    const service = authServiceOver({ logOut });

    // Act
    service.forgetProviderToken();

    // Assert
    expect(logOut).toHaveBeenCalledOnce();
    expect(logOut).toHaveBeenCalledWith(true);
  });

  // `SessionService` calls this as it publishes a session, so a marker the
  // browser refuses to remove must neither throw into that publication nor
  // cost the discard: the marker is removed first, and a throw there would
  // leave the provider's tokens in place.
  it('forgetProviderToken still discards when the marker cannot be removed', () => {
    // Arrange
    const logOut = vi.fn();
    const service = authServiceOver({ logOut });
    const removeItem = vi
      .spyOn(Storage.prototype, 'removeItem')
      .mockImplementation(() => {
        throw new DOMException('Access is denied.', 'SecurityError');
      });

    // Act
    let thrown: unknown = null;
    let removals = 0;
    try {
      service.forgetProviderToken();
    } catch (error: unknown) {
      thrown = error;
    } finally {
      removals = removeItem.mock.calls.length;
      removeItem.mockRestore();
    }

    // Assert
    expect(removals).toBeGreaterThan(0);
    expect(thrown).toBeNull();
    expect(logOut).toHaveBeenCalledOnce();
    expect(logOut).toHaveBeenCalledWith(true);
  });
});

// A macrotask, so every microtask `signIn` chained has had its turn.
function afterPendingWork(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// One `AuthService` over a stubbed provider, configured with the production
// redirect address and sitting at whatever address the case names. The
// document is a stub rather than the runner's, because the address is the
// input under test and jsdom's cannot leave its own origin.
function authServiceOver(
  oAuth: Partial<Record<keyof OAuthService, unknown>>,
  {
    href = 'https://budgetoid.app/welcome',
    redirectUri = 'https://budgetoid.app/register',
  }: { readonly href?: string; readonly redirectUri?: string | null } = {},
): AuthService {
  const google =
    redirectUri === null
      ? undefined
      : { clientId: 'client', redirectUri, scope: 'openid email' };

  TestBed.configureTestingModule({
    providers: [
      AuthService,
      { provide: OAuthService, useValue: oAuth },
      {
        provide: ConfigurationService,
        useValue: { getConfig: () => ({ apiBaseUrl: '', auth: { google } }) },
      },
      { provide: DOCUMENT, useValue: { location: { href } } },
    ],
  });

  return TestBed.inject(AuthService);
}

// One `AuthService` over a stubbed provider whose claims the caller decides,
// and whose token is live unless the caller says otherwise.
//
// **The validity is a parameter rather than a fixture constant**, because it is
// the one input `providerEmail` has besides the claims themselves: the claims
// outlive the token, so every case below is really a pair — these claims, this
// answer to "is the token still good?" — and defaulting it to `true` is what
// keeps each of the four shapes below a statement about the claims alone.
//
// Declared below the suite it serves rather than above it: the first test in
// this file builds its own stub on purpose — it is asserting that *nothing* is
// read — and routing it through a shared helper would put a second reader
// between it and the claim it is watching.
function authServiceReading(
  getIdentityClaims: () => object,
  hasValidIdToken = true,
): AuthService {
  const oAuth = {
    getIdentityClaims,
    hasValidIdToken: () => hasValidIdToken,
  } as unknown as OAuthService;

  TestBed.configureTestingModule({
    providers: [
      AuthService,
      { provide: OAuthService, useValue: oAuth },
      { provide: ConfigurationService, useValue: {} },
    ],
  });

  return TestBed.inject(AuthService);
}

// The same discard against the real library rather than a stub, because what
// the discard *removes* is the library's business and not this file's to list:
// a stub proves `logOut(true)` was asked for, and only the real client proves
// that asking for it empties `sessionStorage` of the provider's material.
//
// **On a service nothing initialized**, which is the ordinary case and not an
// edge. `SessionService` discards on every probe that finds a session, and a
// cold load that is not the provider coming back never calls `initialize()` —
// so the client has no configuration, no discovery document and no end-session
// endpoint. The discard must work there, and it must not reach for the
// provider to make up for what it was never told.
describe('AuthService against the real provider client', () => {
  // Every key `logOut` in angular-oauth2-oidc 17 removes from its storage, seeded
  // so each is there to be removed. `nonce` and `PKCE_verifier` are the reason
  // the discard must never run on the provider-return leg: they are what the
  // answer on the URL is checked against.
  const LIBRARY_KEYS = [
    'access_token',
    'id_token',
    'refresh_token',
    'nonce',
    'PKCE_verifier',
    'expires_at',
    'id_token_claims_obj',
    'id_token_expires_at',
    'id_token_stored_at',
    'access_token_stored_at',
    'granted_scopes',
    'session_state',
  ] as const;

  // Not the library's, and not this service's to touch. A discard written as
  // `sessionStorage.clear()` empties the provider's keys too and passes every
  // assertion on them; this key is what refuses it.
  const FOREIGN_KEY = 'budgetoid-foreign-key';

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideOAuthClient(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: '', auth: {} }) },
        },
      ],
    });
  });

  afterEach(() => {
    sessionStorage.clear();
  });

  it("forgetProviderToken removes the provider's keys and nothing else, without a request", () => {
    // Arrange
    for (const key of LIBRARY_KEYS) {
      sessionStorage.setItem(key, `${key}-value`);
    }
    sessionStorage.setItem(FOREIGN_KEY, 'kept');
    const service = TestBed.inject(AuthService);
    const http = TestBed.inject(HttpTestingController);

    // Act
    service.forgetProviderToken();

    // Assert
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
    expect(sessionStorage.getItem(FOREIGN_KEY)).toBe('kept');
    // No discovery fetch, no token endpoint, nothing on the wire at all.
    http.verify();
  });

  // A session has begun, so no exchange is outstanding in this tab; a marker
  // left behind would make an answer-shaped link opened here later cost a
  // discovery fetch.
  it('forgetProviderToken removes the exchange marker and nothing else', () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    sessionStorage.setItem(FOREIGN_KEY, 'kept');
    const service = TestBed.inject(AuthService);

    // Act
    service.forgetProviderToken();

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
    expect(sessionStorage.getItem(FOREIGN_KEY)).toBe('kept');
  });
});

// The email change's trip to the provider (`docs/design/components.md`,
// "Changing the email address"). The same client, the same one discovery
// fetch per page load, a second redirect address, and a marker whose value
// says which trip this tab is on — `'started'` still means registration.

const EMAIL_CHANGE_MARKER = 'email-change';

// A stub of the provider client whose `configure` behaves like the library's
// on the one point these cases turn on: it copies every key onto the client,
// so `redirectUri` is a property a later write can change. Each departure
// records what the client held at the moment the page would have left.
interface Departure {
  readonly redirectUri: unknown;
  readonly marker: string | null;
  readonly state: unknown;
  readonly params: unknown;
}

function emailChangeServiceOver({
  discovery = (): Promise<boolean> => Promise.resolve(true),
  href = 'https://budgetoid.app/app/settings',
  refuseDeparture = false,
  emailChangeRedirectUri = 'https://budgetoid.app/app/settings',
}: {
  readonly discovery?: () => Promise<boolean>;
  readonly href?: string;
  readonly refuseDeparture?: boolean;
  // `null` leaves the key out of the configuration altogether.
  readonly emailChangeRedirectUri?: string | null;
} = {}): {
  readonly service: AuthService;
  readonly departures: Departure[];
  readonly loadDiscoveryDocumentAndTryLogin: Mock<() => Promise<boolean>>;
} {
  const departures: Departure[] = [];
  const loadDiscoveryDocumentAndTryLogin = vi.fn(discovery);
  const client: Record<string, unknown> = {
    configure: vi.fn((config: object) => {
      Object.assign(client, config);
    }),
    loadDiscoveryDocumentAndTryLogin,
    initLoginFlow: vi.fn((state?: unknown, params?: unknown) => {
      if (refuseDeparture) {
        throw new Error('The login endpoint is refused.');
      }
      departures.push({
        redirectUri: client['redirectUri'],
        marker: sessionStorage.getItem(EXCHANGE_MARKER),
        state,
        params,
      });
    }),
  };

  TestBed.configureTestingModule({
    providers: [
      AuthService,
      { provide: OAuthService, useValue: client },
      {
        provide: ConfigurationService,
        useValue: {
          getConfig: () => ({
            apiBaseUrl: '',
            auth: {
              google: {
                clientId: 'client',
                redirectUri: 'https://budgetoid.app/register',
                ...(emailChangeRedirectUri === null
                  ? {}
                  : { emailChangeRedirectUri }),
                scope: 'openid email',
              },
            },
          }),
        },
      },
      { provide: DOCUMENT, useValue: { location: { href } } },
    ],
  });

  return {
    service: TestBed.inject(AuthService),
    departures,
    loadDiscoveryDocumentAndTryLogin,
  };
}

describe('AuthService email change', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  afterEach(() => {
    sessionStorage.clear();
  });

  it('startEmailChange leaves for the settings screen with the account chooser, marked as an email change', async () => {
    // Arrange
    const { service, departures } = emailChangeServiceOver();

    // Act
    const outcome = await service.startEmailChange();

    // Assert
    expect(outcome).toBe('leaving');
    expect(departures).toEqual([
      {
        redirectUri: 'https://budgetoid.app/app/settings',
        marker: EMAIL_CHANGE_MARKER,
        state: '',
        params: { prompt: 'select_account' },
      },
    ]);
  });

  // T7. The redirect address is a property on one shared client, so the
  // email change's write outlives its own trip unless something puts the
  // registration address back. A registration press after it must still come
  // back to `/register`, marked as a registration.
  it('signIn after startEmailChange on the same page load still returns to the registration screen', async () => {
    // Arrange
    const { service, departures } = emailChangeServiceOver();
    await service.startEmailChange();

    // Act
    service.signIn();
    await afterPendingWork();

    // Assert
    expect(departures).toHaveLength(2);
    expect(departures[1]?.redirectUri).toBe('https://budgetoid.app/register');
    expect(departures[1]?.marker).toBe('started');
  });

  // A departure the library refuses synchronously leaves the page where it is,
  // so the marker it was about to rely on must not stay behind.
  it('startEmailChange answers unavailable and leaves no marker when the departure throws', async () => {
    // Arrange
    const { service } = emailChangeServiceOver({ refuseDeparture: true });

    // Act
    const [outcome] = await Promise.allSettled([service.startEmailChange()]);

    // Assert
    expect(outcome).toEqual({ status: 'fulfilled', value: 'unavailable' });
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // With nowhere to come back to there is no trip worth starting, and nobody
  // to contact on the way to finding that out.
  it('startEmailChange answers unavailable, contacts nobody and leaves no marker when no redirect address is configured', async () => {
    // Arrange
    const { service, departures, loadDiscoveryDocumentAndTryLogin } =
      emailChangeServiceOver({ emailChangeRedirectUri: null });

    // Act
    const outcome = await service.startEmailChange();

    // Assert
    expect(outcome).toBe('unavailable');
    expect(loadDiscoveryDocumentAndTryLogin).not.toHaveBeenCalled();
    expect(departures).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  it('startEmailChange answers unavailable and leaves no marker when the provider cannot be reached', async () => {
    // Arrange
    const { service, departures } = emailChangeServiceOver({
      discovery: () =>
        Promise.reject(new Error('The discovery document is unreachable.')),
    });

    // Act
    const outcome = await service.startEmailChange();

    // Assert
    expect(outcome).toBe('unavailable');
    expect(departures).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  it('startEmailChange reuses the preparation a registration return already made', async () => {
    // Arrange
    const { service, loadDiscoveryDocumentAndTryLogin } =
      emailChangeServiceOver();
    await service.initialize();

    // Act
    await service.startEmailChange();

    // Assert
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledOnce();
  });

  // T6. The marker says which trip this tab is on, the address says where the
  // provider landed, and a provider answer has to be there too. Every one of
  // the three is required; a crossed pair is nobody's return.
  it.each([
    {
      shape: 'an email-change marker and an answer on the settings screen',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings#access_token=a&id_token=b&state=c',
      expected: 'email-change',
    },
    {
      shape: 'an email-change marker and a refusal on the settings screen',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings#error=access_denied',
      expected: 'email-change',
    },
    {
      shape: 'a registration marker and an answer on the settings screen',
      marker: 'started',
      href: 'https://budgetoid.app/app/settings#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'an email-change marker and an answer on the registration screen',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/register#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'an email-change marker and a settings answer on another origin',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.example/app/settings#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape:
        'a registration marker and a registration answer on another origin',
      marker: 'started',
      href: 'https://budgetoid.example/register#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'an email-change marker and an answer under a longer path',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/x/app/settings#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'a registration marker and an answer under a longer path',
      marker: 'started',
      href: 'https://budgetoid.app/x/register#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'no marker and an answer on the settings screen',
      marker: null,
      href: 'https://budgetoid.app/app/settings#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'an email-change marker and the settings screen carrying nothing',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings',
      expected: null,
    },
    {
      shape:
        'an email-change marker and an in-page anchor on the settings screen',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings#section',
      expected: null,
    },
    {
      shape:
        'an email-change marker and a partial answer on the settings screen',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings#access_token=a&state=c',
      expected: null,
    },
    // Equality, never a prefix: a sibling path after the real one and a host
    // that extends the real one are both somewhere else.
    {
      shape: 'an email-change marker and an answer below the settings path',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settings/x#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape:
        'an email-change marker and an answer on a path extending settings',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app/app/settingsX#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape:
        'an email-change marker and an answer on a host extending the real one',
      marker: EMAIL_CHANGE_MARKER,
      href: 'https://budgetoid.app.example/app/settings#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'a registration marker and an answer on a path extending register',
      marker: 'started',
      href: 'https://budgetoid.app/registerX#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape:
        'a registration marker and an answer on a host extending the real one',
      marker: 'started',
      href: 'https://budgetoid.app.example/register#access_token=a&id_token=b&state=c',
      expected: null,
    },
    {
      shape: 'a registration marker and an answer on the registration screen',
      marker: 'started',
      href: 'https://budgetoid.app/register#access_token=a&id_token=b&state=c',
      expected: 'registration',
    },
  ])(
    'providerReturn reads $shape as $expected',
    ({ marker, href, expected }) => {
      // Arrange
      if (marker !== null) {
        sessionStorage.setItem(EXCHANGE_MARKER, marker);
      }
      const { service } = emailChangeServiceOver({ href });

      // Act
      const answer = service.providerReturn();

      // Assert
      expect(answer).toBe(expected);
    },
  );
});

// The same flow against the real library, because the premise under it is the
// library's: that a client which has read an answer and discarded its tokens
// can start a second trip on the same page load, and that what a return
// leaves in storage is gone once the token is handed over.
describe('AuthService email change against the real provider client', () => {
  const DISCOVERY_URL =
    'https://accounts.google.com/.well-known/openid-configuration';
  const KEY_SET_URL = 'https://www.googleapis.com/oauth2/v3/certs';

  // The keys angular-oauth2-oidc 17 writes to its storage on an implicit-flow
  // return, or before a trip.
  const LIBRARY_KEYS = [
    'access_token',
    'id_token',
    'refresh_token',
    'nonce',
    'PKCE_verifier',
    'expires_at',
    'id_token_claims_obj',
    'id_token_expires_at',
    'id_token_stored_at',
    'access_token_stored_at',
    'granted_scopes',
    'session_state',
  ] as const;

  interface RealClient {
    readonly service: AuthService;
    readonly http: HttpTestingController;
    readonly configure: MockInstance<(config: AuthConfig) => void>;
    readonly opened: string[];
  }

  // jsdom's own origin, because the library reads the answer off the
  // runner's `window.location` and nothing else.
  function realClient(): RealClient {
    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideOAuthClient(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: {
            getConfig: () => ({
              apiBaseUrl: '',
              auth: {
                google: {
                  clientId: 'client',
                  redirectUri: `${location.origin}/register`,
                  emailChangeRedirectUri: `${location.origin}/app/settings`,
                  scope: 'openid email',
                },
              },
            }),
          },
        },
      ],
    });
    const oAuth = TestBed.inject(OAuthService);
    const opened: string[] = [];
    // The departure is captured by adding `openUri` on the way into the real
    // `configure`; the page itself cannot leave jsdom.
    const original = oAuth.configure.bind(oAuth);
    const configure = vi
      .spyOn(oAuth, 'configure')
      .mockImplementation((config: AuthConfig) => {
        original({
          ...config,
          openUri: (uri: string) => {
            opened.push(uri);
          },
        });
      });

    return {
      service: TestBed.inject(AuthService),
      http: TestBed.inject(HttpTestingController),
      configure,
      opened,
    };
  }

  // Answers the one discovery fetch and the key-set fetch behind it.
  async function answerDiscovery(http: HttpTestingController): Promise<void> {
    await afterPendingWork();
    http.expectOne(DISCOVERY_URL).flush({
      issuer: 'https://accounts.google.com',
      // The provider's own spelling.
      // eslint-disable-next-line @typescript-eslint/naming-convention
      authorization_endpoint: 'https://accounts.google.com/o/oauth2/v2/auth',
      // eslint-disable-next-line @typescript-eslint/naming-convention
      jwks_uri: KEY_SET_URL,
    });
    await afterPendingWork();
    http.expectOne(KEY_SET_URL).flush({ keys: [] });
  }

  function base64Url(value: object): string {
    return btoa(JSON.stringify(value))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
  }

  // The address Google asserts for the account chosen at the provider.
  const NEW_ADDRESS = 'moved.owner@budgetoid.test';

  // An id token the library accepts under its default `NullValidationHandler`:
  // the claims it checks are right and the signature is not checked. The
  // address claim is `{ email: NEW_ADDRESS }` unless the case hands its own:
  // spread as given, so `{}` mints a token with no `email` claim at all.
  // An object and not an optional parameter, because a parameter's default
  // also fills an explicit `undefined` — which silently put the claim back.
  function idTokenFor(
    nonce: string,
    addressClaim: { readonly email?: unknown } = { email: NEW_ADDRESS },
  ): string {
    const now = Math.floor(Date.now() / 1000);

    return [
      base64Url({ alg: 'RS256', typ: 'JWT' }),
      base64Url({
        iss: 'https://accounts.google.com',
        aud: 'client',
        sub: 'subject-one',
        iat: now,
        exp: now + 3600,
        nonce,
        // eslint-disable-next-line @typescript-eslint/naming-convention
        at_hash: 'hash-one',
        ...addressClaim,
      }),
      'signature-one',
    ].join('.');
  }

  function landOn(path: string): void {
    history.replaceState(null, '', path);
  }

  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
  });

  afterEach(() => {
    sessionStorage.clear();
    localStorage.clear();
    landOn('/');
    vi.restoreAllMocks();
  });

  // T1. The page load that came back from a registration read the answer and
  // then discarded the provider's tokens — nonce included. A press on that
  // same load must start a whole new trip from the memoized preparation.
  it('startEmailChange after a read and discarded registration answer leaves once, with a fresh nonce and no second fetch', async () => {
    // Arrange
    const { service, http, configure, opened } = realClient();
    sessionStorage.setItem('nonce', 'earlier-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const token = idTokenFor('earlier-nonce');
    landOn(`/register#access_token=at&id_token=${token}&state=earlier-nonce`);
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;
    service.forgetProviderToken();

    // Act
    const outcome = await service.startEmailChange();
    await afterPendingWork();

    // Assert
    expect(outcome).toBe('leaving');
    expect(http.match(DISCOVERY_URL)).toEqual([]);
    http.verify();
    expect(configure).toHaveBeenCalledOnce();
    expect(opened).toHaveLength(1);
    const departure = new URL(opened[0] ?? 'https://nothing.invalid/');
    expect(departure.searchParams.get('redirect_uri')).toBe(
      `${location.origin}/app/settings`,
    );
    expect(departure.searchParams.get('prompt')).toBe('select_account');
    const nonce = departure.searchParams.get('nonce');
    expect(nonce).not.toBeNull();
    expect(nonce).not.toBe('earlier-nonce');
    expect(nonce).toBe(sessionStorage.getItem('nonce'));
  });

  // T2. The token is handed over in memory, once, and nothing the library
  // wrote for the trip survives the hand-over.
  it('an email-change return hands the id token over once and leaves none of the library keys behind', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    landOn(`/app/settings#access_token=at&id_token=${token}&state=trip-nonce`);
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    const first = service.takeEmailChangeReturn();
    const second = service.takeEmailChangeReturn();

    // Assert
    expect(first).toEqual({
      kind: 'answered',
      idToken: token,
      email: NEW_ADDRESS,
    });
    expect(second).toBeNull();
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // Memory only: neither the token nor the address it asserts may be parked
  // anywhere a reload could read.
  it('an email-change return writes the id token and the address to no storage', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    landOn(`/app/settings#access_token=at&id_token=${token}&state=trip-nonce`);

    // Act
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Assert
    const holding = [sessionStorage, localStorage].flatMap((storage) =>
      Object.keys(storage).filter((key) => {
        const value = storage.getItem(key) ?? '';

        return value.includes(token) || value.includes(NEW_ADDRESS);
      }),
    );
    expect(holding).toEqual([]);
  });

  // The waiting state names the address Google sent back, so a validated
  // answer asserting no address is nothing the screen could confirm.
  it.each([
    { shape: 'no address claim', addressClaim: {} },
    {
      shape: 'an address claim that is not a string',
      addressClaim: { email: 42 },
    },
    // Present but blank is not an address, as `providerEmail()` holds for
    // registration: the waiting state would name nobody.
    { shape: 'an empty address claim', addressClaim: { email: '' } },
  ])(
    'an email-change return whose token carries $shape is unconfirmed',
    async ({ addressClaim }) => {
      // Arrange
      const { service, http } = realClient();
      sessionStorage.setItem('nonce', 'trip-nonce');
      sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
      const token = idTokenFor('trip-nonce', addressClaim);
      landOn(
        `/app/settings#access_token=at&id_token=${token}&state=trip-nonce`,
      );
      const initialized = service.initialize();
      await answerDiscovery(http);
      await initialized;

      // Act
      const handedOver = service.takeEmailChangeReturn();

      // Assert
      expect(handedOver).toEqual({ kind: 'unconfirmed' });
      const left = LIBRARY_KEYS.filter(
        (key) => sessionStorage.getItem(key) !== null,
      );
      expect(left).toEqual([]);
    },
  );

  it('an email-change return whose nonce does not match is unconfirmed', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'stored-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('forged-nonce');
    landOn(
      `/app/settings#access_token=at&id_token=${token}&state=forged-nonce`,
    );
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    const handedOver = service.takeEmailChangeReturn();

    // Assert
    expect(handedOver).toEqual({ kind: 'unconfirmed' });
    // The nonce above all: a nonce left by a failed return is what a crafted
    // answer would need.
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // m1. A registration abandoned in this tab left a validated token and its
  // claims in storage. An email-change return the library refuses must not be
  // answered out of those: their address is somebody's old answer to another
  // question, and handing it over would put it on the waiting state.
  it('an email-change return refused on its nonce is unconfirmed even beside an abandoned registration token', async () => {
    // Arrange
    const { service, http } = realClient();
    const abandoned = idTokenFor('abandoned-nonce', {
      email: 'abandoned.owner@budgetoid.test',
    });
    sessionStorage.setItem('id_token', abandoned);
    sessionStorage.setItem(
      'id_token_claims_obj',
      JSON.stringify({
        iss: 'https://accounts.google.com',
        aud: 'client',
        sub: 'subject-one',
        email: 'abandoned.owner@budgetoid.test',
      }),
    );
    sessionStorage.setItem(
      'id_token_expires_at',
      String(Date.now() + 60 * 60 * 1000),
    );
    sessionStorage.setItem('id_token_stored_at', String(Date.now()));
    sessionStorage.setItem('nonce', 'stored-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('forged-nonce');
    landOn(
      `/app/settings#access_token=at&id_token=${token}&state=forged-nonce`,
    );
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    const handedOver = service.takeEmailChangeReturn();

    // Assert
    expect(handedOver).toEqual({ kind: 'unconfirmed' });
  });

  it('an email-change return carrying a provider refusal is unconfirmed', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    landOn('/app/settings#error=access_denied&state=trip-nonce');
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    const handedOver = service.takeEmailChangeReturn();

    // Assert
    expect(handedOver).toEqual({ kind: 'unconfirmed' });
    // The nonce above all: a nonce left by a failed return is what a crafted
    // answer would need.
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
  });

  it('an email-change return on which the provider cannot be reached is unconfirmed', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    landOn(`/app/settings#access_token=at&id_token=${token}&state=trip-nonce`);
    const initialized = service.initialize();
    await afterPendingWork();
    http
      .expectOne(DISCOVERY_URL)
      .error(new ProgressEvent('error'), { status: 0, statusText: '' });
    await initialized;

    // Act
    const handedOver = service.takeEmailChangeReturn();

    // Assert
    expect(handedOver).toEqual({ kind: 'unconfirmed' });
    // The nonce above all: a nonce left by a failed return is what a crafted
    // answer would need.
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
  });

  // A registration return is read for the registration screen, which reads
  // the token through `providerEmail()`; nothing about it is an email change.
  it('a registration return hands nothing over to the email change', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'earlier-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const token = idTokenFor('earlier-nonce');
    landOn(`/register#access_token=at&id_token=${token}&state=earlier-nonce`);
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    const handedOver = service.takeEmailChangeReturn();

    // Assert
    expect(handedOver).toBeNull();
  });

  // T3. The discard is about the library's storage; the hand-off is this
  // service's memory, and a session being published must not take it.
  it('forgetProviderToken leaves a captured email-change answer in place', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    landOn(`/app/settings#access_token=at&id_token=${token}&state=trip-nonce`);
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    service.forgetProviderToken();

    // Assert
    expect(service.takeEmailChangeReturn()).toEqual({
      kind: 'answered',
      idToken: token,
      email: NEW_ADDRESS,
    });
  });

  // The drop is what the bootstrap calls when the probe finds nobody signed
  // in: the answer must not reach `/welcome` or `/register`.
  it('dropEmailChangeReturn leaves nothing for a later take', async () => {
    // Arrange
    const { service, http } = realClient();
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    landOn(`/app/settings#access_token=at&id_token=${token}&state=trip-nonce`);
    const initialized = service.initialize();
    await answerDiscovery(http);
    await initialized;

    // Act
    service.dropEmailChangeReturn();

    // Assert
    expect(service.takeEmailChangeReturn()).toBeNull();
  });
});
