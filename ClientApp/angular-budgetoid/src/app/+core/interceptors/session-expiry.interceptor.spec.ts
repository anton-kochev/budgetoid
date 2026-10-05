import {
  HttpContext,
  HttpErrorResponse,
  HttpRequest,
  HttpResponse,
  provideHttpClient,
  withInterceptors,
  type HttpEvent,
  type HttpHandlerFn,
} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  MeApiService,
  type MeDto,
  type SessionDto,
} from '@app-core/api/me-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import {
  SessionService,
  type RefusalVerdict,
  type SessionToken,
} from '@app-core/session/session.service';
import { Router } from '@angular/router';
import { Subject, of, throwError, type Subscription } from 'rxjs';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
  type MockInstance,
} from 'vitest';
import { EXPECTS_UNAUTHENTICATED } from './expects-unauthenticated.token';
import { sessionExpiryInterceptor } from './session-expiry.interceptor';

// The same two origins `api-credentials.interceptor.spec.ts` uses, and for the
// same reason: the predicate that decides which of them this interceptor may
// act on is meant to be one predicate shared by both files, so the two specs
// have to be able to disagree about it.
const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;
// The probe's own question since a locked session needed one it could ask:
// `GET /api/me` answers a locked session `403`, which reads as no session.
const SESSION_URL = `${API_BASE_URL}/api/me/session`;
// The third route this block reaches, and the only one whose 401 belongs to
// nobody: it is read by `AccountKeyCustodyService` and by nothing else.
const ACCOUNT_KEYS_URL = `${API_BASE_URL}/api/me/account-keys`;
const OTHER_ORIGIN_URL =
  'https://accounts.google.com/.well-known/openid-configuration';

const WELCOME = 'welcome';

// Two visits of one tab. Opaque numbers to the interceptor, which only ever
// hands one back.
const SENT_UNDER = 7 as SessionToken;
const LATER_VISIT = 8 as SessionToken;

function refusal(status: number, url: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, url });
}

// A macrotask, so every promise the interceptor chains off a verdict has run.
function settled(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The three members of `SessionService` this interceptor may reach. `ended`
// is here so a call to it lands somewhere countable: since the 401 is judged,
// ending the session belongs to `judgeRefusal`, and the interceptor calling it
// as well would be a second owner of that transition.
interface SessionStub {
  readonly sessionToken: Mock<() => SessionToken>;
  readonly judgeRefusal: Mock<
    (sentUnder: SessionToken) => Promise<RefusalVerdict>
  >;
  readonly ended: Mock<() => void>;
}

interface ArrangeOptions {
  // What `judgeRefusal` answers. A promise the test holds, for a case about
  // what happens while the verdict is still out.
  readonly verdict?: RefusalVerdict | Promise<RefusalVerdict>;
  readonly apiBaseUrl?: string;
}

interface Harness {
  readonly session: SessionStub;
  // What reached the outside, in the order it did: `navigate:<path>` for a
  // navigation through either `Router` method, `error` for an error handed to
  // the caller, `event` for a response.
  readonly log: readonly string[];
  readonly errors: readonly unknown[];
  readonly events: readonly HttpEvent<unknown>[];
  readonly destinations: () => readonly string[];
  // Hands back the subscription, for a case about a caller that stops
  // listening.
  send(request: HttpRequest<unknown>, next: HttpHandlerFn): Subscription;
}

// What the rest of the chain answers with. An `HttpErrorResponse` is thrown
// to the interceptor; anything else is delivered as a response.
function answering(
  answer: HttpErrorResponse | HttpResponse<unknown>,
): HttpHandlerFn {
  return () =>
    answer instanceof HttpErrorResponse ? throwError(() => answer) : of(answer);
}

// Reads the destination out of whichever `Router` method the implementation
// reached for, normalized to a path with no leading slash. Deliberately not a
// pin on `navigateByUrl` over `navigate`: "the browser leaves for /welcome" is
// the behaviour, and which of the two states it is a choice this spec has no
// business making for the implementation.
function pathOf(argument: unknown): string {
  const raw = Array.isArray(argument) ? argument.join('/') : String(argument);

  return raw.replace(/^\/+/, '');
}

// A functional interceptor is a plain function, so it is called directly inside
// an injection context with a `next` that answers however the test asked, which
// is the harness `api-credentials.interceptor.spec.ts` already uses. It is the
// right one here for the extra reason that `HttpTestingController` runs the
// whole client, and the subject of these tests is what the interceptor does
// with an error on its way *back* through the chain.
//
// Every spy is built fresh per call: no `restoreMocks` is configured, so a spy
// shared across cases would answer `toHaveBeenCalled` from an earlier case.
function arrange(options: ArrangeOptions = {}): Harness {
  const { verdict = 'ended', apiBaseUrl = API_BASE_URL } = options;

  // Reset first: the first `runInInjectionContext` instantiates the injector,
  // after which a second `configureTestingModule` throws.
  TestBed.resetTestingModule();

  const log: string[] = [];
  const errors: unknown[] = [];
  const events: HttpEvent<unknown>[] = [];
  const destinations: string[] = [];

  function navigated(argument: unknown): Promise<boolean> {
    const path = pathOf(argument);

    destinations.push(path);
    log.push(`navigate:${path}`);

    return Promise.resolve(true);
  }

  const session: SessionStub = {
    sessionToken: vi.fn<() => SessionToken>(() => SENT_UNDER),
    judgeRefusal: vi.fn<(sentUnder: SessionToken) => Promise<RefusalVerdict>>(
      () => Promise.resolve(verdict),
    ),
    ended: vi.fn<() => void>(),
  };
  const router = {
    navigateByUrl: vi.fn((url: string): Promise<boolean> => navigated(url)),
    navigate: vi.fn(
      (commands: readonly string[]): Promise<boolean> => navigated(commands),
    ),
  };
  const configuration: Pick<ConfigurationService, 'getConfig'> = {
    getConfig: () => ({ apiBaseUrl, auth: {} }),
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: ConfigurationService, useValue: configuration },
      { provide: SessionService, useValue: session },
      { provide: Router, useValue: router },
    ],
  });

  return {
    session,
    log,
    errors,
    events,
    destinations: () => [...destinations],
    send(request: HttpRequest<unknown>, next: HttpHandlerFn): Subscription {
      return TestBed.runInInjectionContext(() =>
        sessionExpiryInterceptor(request, next),
      ).subscribe({
        next: (event) => {
          events.push(event);
          log.push('event');
        },
        error: (error: unknown) => {
          errors.push(error);
          log.push('error');
        },
      });
    },
  };
}

function apiGet(context?: HttpContext): HttpRequest<unknown> {
  return new HttpRequest<unknown>('GET', API_URL, context ? { context } : {});
}

describe('sessionExpiryInterceptor', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  // The one thing this interceptor exists for. Without it a session that lapsed
  // mid-visit leaves the browser on a screen whose every read now fails, with
  // `SessionService` still saying `'authenticated'` and nothing on the page
  // saying why the numbers stopped arriving.
  //
  // **Judged, and the ending is the judge's.** A 401 can be the request losing
  // a sign-in race in another tab: the cookie it carried was displaced, and the
  // jar already holds the new one. `judgeRefusal` tells the two apart and calls
  // `ended()` itself on the verdict that ends; the interceptor calling it as
  // well would be a second owner of that transition.
  it('leaves for the welcome screen when the refusal is judged to end the session', async () => {
    // Arrange
    const harness = arrange({ verdict: 'ended' });

    // Act
    harness.send(apiGet(), answering(refusal(401, API_URL)));
    await settled();

    // Assert
    expect(harness.destinations()).toEqual([WELCOME]);
    expect(harness.session.judgeRefusal).toHaveBeenCalledTimes(1);
    expect(harness.session.ended).not.toHaveBeenCalled();
  });

  // The defect. Tab A signs in and the server displaces the session tab B's
  // in-flight request carried; that 401 is about a cookie the jar no longer
  // holds. Read as the end of the session, it signs tab B out of an account it
  // is still inside and locks the keys it holds.
  it.each<{ readonly verdict: RefusalVerdict }>([
    { verdict: 'kept' },
    { verdict: 'stale' },
  ])(
    'navigates nowhere on a $verdict verdict and hands the caller the same error',
    async ({ verdict }) => {
      // Arrange
      const answer = refusal(401, API_URL);
      const harness = arrange({ verdict });

      // Act
      harness.send(apiGet(), answering(answer));
      await settled();

      // Assert
      expect(harness.destinations()).toEqual([]);
      expect(harness.session.ended).not.toHaveBeenCalled();
      // The same object, not an equal one: the caller's own `catchError` reads
      // the status off it, and the three flows probe `sessionHasEnded()` after.
      expect(harness.errors).toHaveLength(1);
      expect(harness.errors[0]).toBe(answer);
      expect(harness.session.judgeRefusal).toHaveBeenCalledTimes(1);
    },
  );

  // **The order is what three flows depend on.** The erasure, its withdrawal
  // and the email change each probe `sessionHasEnded()` in their own
  // `catchError`, so the verdict has to be in — and the navigation asked —
  // before the error reaches them. Handed over early, the caller reads a
  // session that has not been judged yet.
  it('hands the caller nothing until the verdict settles, and asks for the navigation first', async () => {
    // Arrange
    let settle: (verdict: RefusalVerdict) => void = () => undefined;
    const verdict = new Promise<RefusalVerdict>((resolve) => {
      settle = resolve;
    });
    const harness = arrange({ verdict });
    harness.send(apiGet(), answering(refusal(401, API_URL)));
    await settled();
    expect(harness.log).toEqual([]);

    // Act
    settle('ended');
    await settled();

    // Assert
    expect(harness.log).toEqual([`navigate:${WELCOME}`, 'error']);
  });

  // **The navigation does not belong to the caller's subscription.** A screen
  // torn down while the verdict is out — a route change, an overlay closed —
  // unsubscribes, and the session it judged has still ended. With the
  // navigation inside the returned observable, that tab would sit
  // `'anonymous'` on a screen nothing guards any more.
  it('still leaves for the welcome screen when the caller stops listening before the verdict', async () => {
    // Arrange
    let settle: (verdict: RefusalVerdict) => void = () => undefined;
    const verdict = new Promise<RefusalVerdict>((resolve) => {
      settle = resolve;
    });
    const harness = arrange({ verdict });
    const subscription = harness.send(
      apiGet(),
      answering(refusal(401, API_URL)),
    );
    await settled();
    expect(harness.session.judgeRefusal).toHaveBeenCalledTimes(1);

    // Act
    subscription.unsubscribe();
    settle('ended');
    await settled();

    // Assert
    expect(harness.destinations()).toEqual([WELCOME]);
    // Nobody is listening, so nothing is handed over.
    expect(harness.errors).toEqual([]);
  });

  // **Read when the request leaves, not when its 401 lands.** A sign-in in
  // this tab while the request is out moves the visit; judged against the
  // newer token, the old request's 401 would be read as a refusal of the
  // session that just began.
  it('judges the refusal against the visit the request was sent in', async () => {
    // Arrange
    const harness = arrange({ verdict: 'stale' });
    const response = new Subject<HttpEvent<unknown>>();
    harness.send(apiGet(), () => response);
    harness.session.sessionToken.mockReturnValue(LATER_VISIT);

    // Act
    response.error(refusal(401, API_URL));
    await settled();

    // Assert
    expect(harness.session.judgeRefusal.mock.calls).toEqual([[SENT_UNDER]]);
  });

  // An observer, not a handler. Swallowed here, a 401 reaches no caller's
  // `catchError`, so the screen that made the request renders neither its
  // outcome nor its failure — it sits on its loading line forever, under a
  // navigation that may itself be cancelled by a guard.
  it('re-throws the error rather than swallowing it', async () => {
    // Arrange
    const answer = refusal(401, API_URL);
    const harness = arrange({ verdict: 'ended' });

    // Act
    harness.send(apiGet(), answering(answer));
    await settled();

    // Assert
    expect(harness.errors).toEqual([answer]);
  });

  // **Never a retry, whatever the verdict.** A `'kept'` verdict says the
  // session stands, which reads like an invitation to send the request again
  // — and the erasure, its withdrawal and the email change each say no
  // interceptor may retry them, because a second send spends a nonce or
  // repeats an act the first may already have done.
  it.each<{ readonly verdict: RefusalVerdict }>([
    { verdict: 'kept' },
    { verdict: 'stale' },
    { verdict: 'ended' },
  ])('sends the request once on a $verdict verdict', async ({ verdict }) => {
    // Arrange
    const harness = arrange({ verdict });
    const next = vi.fn<HttpHandlerFn>(answering(refusal(401, API_URL)));

    // Act
    harness.send(apiGet(), next);
    await settled();

    // Assert
    expect(next).toHaveBeenCalledTimes(1);
  });

  // The anonymous ceremony routes answer 401 as their own verdict — a passkey
  // that did not verify, a recovery code that matched nothing — and none of
  // those is a session ending, because there is no session yet. Carried on the
  // request rather than in a list of URLs here: a URL list would be a second
  // definition of the anonymous surface, kept in the client, drifting from the
  // server's the first time a route moves.
  //
  // The services that set this token arrive in later commits, so the request is
  // built with it directly. That is the mechanism shipping one commit ahead of
  // its caller, which is deliberate.
  it('leaves a request that expects a refusal alone', async () => {
    // Arrange
    const context = new HttpContext().set(EXPECTS_UNAUTHENTICATED, true);
    const harness = arrange();

    // Act
    harness.send(apiGet(context), answering(refusal(401, API_URL)));
    await settled();

    // Assert
    expect(harness.session.judgeRefusal).not.toHaveBeenCalled();
    expect(harness.session.ended).not.toHaveBeenCalled();
    expect(harness.destinations()).toEqual([]);
    // Still re-thrown: the ceremony's own handler is what renders "that code
    // didn't match", and it only ever sees the error if this passes it on.
    expect(harness.errors).toHaveLength(1);
  });

  // The negative control for the shared predicate. This app talks to the
  // identity provider through the same `HttpClient`, and a 401 from Google's
  // discovery endpoint is a statement about a token this product does not
  // issue. Signing somebody out of Budgetoid over it is a sign-out caused by a
  // third party.
  it('signs nobody out when another origin answers 401', async () => {
    // Arrange
    const request = new HttpRequest<unknown>('GET', OTHER_ORIGIN_URL);
    const harness = arrange();

    // Act
    harness.send(request, answering(refusal(401, OTHER_ORIGIN_URL)));
    await settled();

    // Assert
    expect(harness.session.judgeRefusal).not.toHaveBeenCalled();
    expect(harness.session.ended).not.toHaveBeenCalled();
    expect(harness.destinations()).toEqual([]);
    expect(harness.errors).toHaveLength(1);
  });

  // 403 is the CSRF refusal — a request that arrived without the client header
  // — and the locked-session refusal. Both are answered to a browser whose
  // session is intact, so acting on one ends a live session over a bug in the
  // request builder.
  it('leaves a 403 alone', async () => {
    // Arrange
    const harness = arrange();

    // Act
    harness.send(apiGet(), answering(refusal(403, API_URL)));
    await settled();

    // Assert
    expect(harness.session.judgeRefusal).not.toHaveBeenCalled();
    expect(harness.session.ended).not.toHaveBeenCalled();
    expect(harness.destinations()).toEqual([]);
    expect(harness.errors).toHaveLength(1);
  });

  // The control for every test above: an interceptor that judged every
  // response on its way past would satisfy the 401 cases perfectly and send a
  // `GET /api/me` behind every successful read.
  it('leaves a successful response alone', async () => {
    // Arrange
    const answer = new HttpResponse<unknown>({ status: 200, url: API_URL });
    const harness = arrange();

    // Act
    harness.send(apiGet(), answering(answer));
    await settled();

    // Assert
    expect(harness.session.judgeRefusal).not.toHaveBeenCalled();
    expect(harness.session.ended).not.toHaveBeenCalled();
    expect(harness.destinations()).toEqual([]);
    expect(harness.events).toEqual([answer]);
  });
});

// `GET /api/me` is read by two callers asking two different questions, and the
// defect this block exists for is the interaction between them rather than
// anything either file does alone. `outcomeOf` above hands the interceptor a
// request this spec built, so it can only ever pin what the interceptor does
// with a context token — never whether the caller that needed one set it. Here
// the production `SessionService`, the production `MeApiService` and the
// production interceptor are wired to each other through the real `HttpClient`,
// and only the backend, the configuration and the `Router` are swapped.
//
// Measured in a browser before it was written: an anonymous cold load of
// `/register` landed on `/welcome`, with no `RegisterComponent` chunk in the
// network log, because the probe's own 401 was read as a session ending and
// `sessionExpiryInterceptor` navigated out of the `APP_INITIALIZER`.
describe('sessionExpiryInterceptor and the two readers of GET /api/me', () => {
  // Everything a test needs to say what the whole chain did, and deliberately
  // not the interceptor's own arguments: the subject here is which caller's
  // request carries the token, which is a fact about `MeApiService`.
  interface Wiring {
    readonly http: HttpTestingController;
    readonly session: SessionService;
    readonly meApi: MeApiService;
    // Called or not, on the real instance rather than a fake. A stub of
    // `SessionService` would have this file supply the very transition it
    // asserts on, and the status it publishes is what test two reads.
    readonly ended: () => boolean;
    // Every destination the implementation reached for, through either `Router`
    // method, normalized by `pathOf`. A list rather than a first call, because
    // "navigates nowhere" is an assertion about all of them.
    readonly destinations: () => readonly string[];
  }

  let wiring: Wiring;

  function wireTheRealChain(): Wiring {
    const navigateByUrl = vi.fn(
      (url: string): Promise<boolean> => Promise.resolve(true),
    );
    const navigate = vi.fn(
      (commands: readonly string[]): Promise<boolean> => Promise.resolve(true),
    );
    const configuration: Pick<ConfigurationService, 'getConfig'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionExpiryInterceptor])),
        provideHttpClientTesting(),
        { provide: ConfigurationService, useValue: configuration },
        { provide: Router, useValue: { navigateByUrl, navigate } },
        {
          // `SessionService` discards the provider's token whenever it
          // publishes a session, and the real `AuthService` needs
          // `provideOAuthClient()`. Nothing here is about the identity
          // provider, so the one member it reaches is a fresh spy.
          provide: AuthService,
          useValue: {
            forgetProviderToken: vi.fn(),
          } satisfies Pick<AuthService, 'forgetProviderToken'>,
        },
      ],
    });

    const session = TestBed.inject(SessionService);
    // `spyOn` and not a replacement: the real `ended()` still runs, so the
    // status a test reads afterwards is written by production code.
    const ended = vi.spyOn(session, 'ended');

    return {
      http: TestBed.inject(HttpTestingController),
      session,
      meApi: TestBed.inject(MeApiService),
      ended: () => ended.mock.calls.length > 0,
      destinations: () => [
        ...navigateByUrl.mock.calls.map(([url]) => pathOf(url)),
        ...navigate.mock.calls.map(([commands]) => pathOf(commands)),
      ],
    };
  }

  function refuseAt(url: string): void {
    wiring.http
      .expectOne(url)
      .flush(null, { status: 401, statusText: 'Unauthorized' });
  }

  function refuse(): void {
    refuseAt(API_URL);
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    wiring = wireTheRealChain();
  });

  afterEach(() => {
    wiring.http.verify();
    TestBed.resetTestingModule();
  });

  // The defect itself. The probe is the request that *asks* whether there is a
  // session, so its 401 is the answer it went to fetch — read as a session
  // ending it navigates every anonymous visitor to `/welcome` from inside the
  // `APP_INITIALIZER`, before the router has activated anything, which makes
  // every deep link in the product unreachable while signed out.
  it('navigates nowhere and ends no session when the probe is refused', async () => {
    // Arrange
    const probed = wiring.session.probe();

    // Act
    refuseAt(SESSION_URL);
    await probed;

    // Assert
    expect(wiring.ended()).toBe(false);
    expect(wiring.destinations()).toEqual([]);
  });

  // The control for the test above, which without it passes just as well
  // against a probe that stopped reading the answer at all — or one that never
  // asked. `probe()` already owns this status, which is why suppressing the
  // interceptor's second, redundant statement of it costs nothing.
  it('still publishes the refused probe as an anonymous visitor', async () => {
    // Arrange
    const probed = wiring.session.probe();

    // Act
    refuseAt(SESSION_URL);
    await probed;

    // Assert
    expect(wiring.session.status()).toBe('anonymous');
  });

  // The negative control for the split, and the reason the token goes on one
  // caller rather than on the service. The Settings screen reads the same route
  // to show the account's email, and it reads it from a browser that believes
  // it holds a session: there a 401 means the session ended between the cold
  // load and the screen, and the bounce is the correct answer. Marking
  // `getMe()` itself — the obvious simplification — would take this behaviour
  // away and leave that person on a screen whose every read now fails, with
  // nothing on the page saying why.
  //
  // The tab here has never probed, so its status is `'unknown'`: there is no
  // budget to compare a re-read against, and the refusal ends the session
  // without asking. Awaited, because the navigation and the re-throw now wait
  // for a verdict.
  it('ends the session and leaves for the welcome screen when the settings read of the same route is refused', async () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    wiring.meApi.getMe().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    refuse();
    await settled();

    // Assert
    expect(wiring.ended()).toBe(true);
    expect(wiring.destinations()).toEqual([WELCOME]);
    expect(wiring.session.status()).toBe('anonymous');
    // Still re-thrown, so the screen renders its own failure line rather than
    // sitting on a loading state under a navigation a guard may cancel.
    expect(refusals).toHaveLength(1);
  });

  // **The second defect of the same shape, on a third route, and it lands at
  // the worst possible moment.** `AccountKeyCustodyService` reads
  // `GET /api/me/account-keys` immediately after a sign-in: the assertion
  // answers 200, `SessionService.established()` publishes `authenticated`,
  // custody's read leaves, and the router is sent to `/app`. A 401 on that read
  // — a cookie that has not landed yet, Safari's storage rules, a session that
  // died between two requests — is read by this interceptor as a session
  // ending, so `ended()` publishes `anonymous` and a second navigation leaves
  // for `/welcome`. Being later, it wins.
  //
  // What the person sees is an anonymous welcome screen, holding a session
  // cookie the server issued a moment ago, saying **nothing**: `SignInService`
  // is provided on that screen, so the instance carrying `failure()` died with
  // the previous one and the fresh one has published nothing. A 401 that
  // reproduces is a loop with no exit and no sentence.
  //
  // The rule this restores is custody's own: it never calls anything on
  // `SessionService`, because a key that will not open is not a session that
  // ended. Unmarked, the request makes that call anyway, through an edge no
  // import graph shows.
  it('navigates nowhere and ends no session when the account-key read is refused', async () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    wiring.meApi.getAccountKeys().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    refuseAt(ACCOUNT_KEYS_URL);
    await settled();

    // Assert
    expect(wiring.ended()).toBe(false);
    expect(wiring.destinations()).toEqual([]);
    // And the client still believes what the server told it one request ago.
    // This is the half that says the suppression is not a sign-out written
    // quietly: nothing about the session moved.
    expect(wiring.session.status()).toBe('unknown');

    // Still re-thrown, because the request's own caller is entitled to know it
    // failed — `AccountKeyCustodyService` reads exactly this to publish a
    // failure word of its own.
    expect(refusals).toHaveLength(1);
  });
});

// **A 401 judged against the session this tab holds**, through the real
// `SessionService`, `MeApiService` and `HttpClient`. The race it is for: tab A
// signs in, the server displaces the session tab B's in-flight request
// carried, and tab B's cookie jar already holds A's new cookie. Only the wire
// can say how many re-reads went out and whether each was marked — an unmarked
// one would route its own 401 back into this interceptor.
describe('sessionExpiryInterceptor judging a 401 against the session', () => {
  const FIRST_BUDGET = '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471';
  const SECOND_BUDGET = '9c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f';
  const FULL_SESSION: SessionDto = {
    kind: 'full',
    expiresAtUtc: '2026-10-17T08:00:00Z',
    erasure: null,
  };

  let http: HttpTestingController;
  let session: SessionService;
  let meApi: MeApiService;
  let ended: MockInstance<() => void>;
  let lock: MockInstance<() => void>;
  let destinations: () => readonly string[];

  function ownerOf(budgetId: string): MeDto {
    return { budgetId, email: 'owner@budgetoid.test' };
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    const navigateByUrl = vi.fn(
      (url: string): Promise<boolean> => Promise.resolve(true),
    );
    const navigate = vi.fn(
      (commands: readonly string[]): Promise<boolean> => Promise.resolve(true),
    );
    const configuration: Pick<ConfigurationService, 'getConfig'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionExpiryInterceptor])),
        provideHttpClientTesting(),
        { provide: ConfigurationService, useValue: configuration },
        { provide: Router, useValue: { navigateByUrl, navigate } },
        {
          provide: AuthService,
          useValue: {
            forgetProviderToken: vi.fn(),
          } satisfies Pick<AuthService, 'forgetProviderToken'>,
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
    meApi = TestBed.inject(MeApiService);
    // `spyOn` and not a replacement, on fresh instances per case: the real
    // methods still run, and `vi.restoreAllMocks()` below takes the spies off
    // before the next case can read their history.
    ended = vi.spyOn(session, 'ended');
    lock = vi.spyOn(TestBed.inject(AccountKeyCustodyService), 'lock');
    destinations = () => [
      ...navigateByUrl.mock.calls.map(([url]) => pathOf(url)),
      ...navigate.mock.calls.map(([commands]) => pathOf(commands)),
    ];
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      vi.restoreAllMocks();
      TestBed.resetTestingModule();
    }
  });

  // A cold load that found a full session inside `budgetId`.
  async function signedInTo(budgetId: string): Promise<void> {
    const probed = session.probe();
    await settled();
    http.expectOne(SESSION_URL).flush(FULL_SESSION);
    await settled();
    http.expectOne(API_URL).flush(ownerOf(budgetId));
    await probed;
    expect(session.status()).toBe('authenticated');
    expect(session.budgetId()).toBe(budgetId);
  }

  // The Settings screen's read of `GET /api/me` — unmarked, so its 401 is the
  // interceptor's — refused. Returns what its caller was handed.
  function refuseTheSettingsRead(): unknown[] {
    const refusals: unknown[] = [];

    meApi.getMe().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    http
      .expectOne(
        (request) =>
          request.url === API_URL &&
          !request.context.get(EXPECTS_UNAUTHENTICATED),
      )
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    return refusals;
  }

  // The one re-read the judgement sends: `GET /api/me`, marked, so a 401 to it
  // is the judgement's own answer and never comes back through here.
  function theReRead(): TestRequest {
    const read = http.expectOne(API_URL);

    expect(read.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

    return read;
  }

  // The defect. The re-read names the budget this tab is already in, so the
  // 401 was about a cookie the jar no longer holds — the session stands.
  it('keeps the session when the re-read names the budget this tab is in', async () => {
    // Arrange
    await signedInTo(FIRST_BUDGET);

    // Act
    const refusals = refuseTheSettingsRead();
    await settled();
    theReRead().flush(ownerOf(FIRST_BUDGET));
    await settled();

    // Assert
    expect(destinations()).toEqual([]);
    expect(ended).not.toHaveBeenCalled();
    expect(lock).not.toHaveBeenCalled();
    expect(session.status()).toBe('authenticated');
    expect(session.budgetId()).toBe(FIRST_BUDGET);
    // The caller still hears its own 401: the read it made did fail.
    expect(refusals).toHaveLength(1);
    expect(refusals[0]).toBeInstanceOf(HttpErrorResponse);
    expect(refusals[0]).toMatchObject({ status: 401 });
  });

  // **Another account's session is not this tab's session.** The cookie
  // another tab set may belong to a different account; kept, this tab would
  // fold its old `budgetId` and keys into writes made under the new cookie.
  it('ends the session when the re-read names another budget', async () => {
    // Arrange
    await signedInTo(FIRST_BUDGET);

    // Act
    const refusals = refuseTheSettingsRead();
    await settled();
    theReRead().flush(ownerOf(SECOND_BUDGET));
    await settled();

    // Assert
    expect(destinations()).toEqual([WELCOME]);
    expect(session.status()).toBe('anonymous');
    expect(session.budgetId()).toBeNull();
    expect(lock).toHaveBeenCalledTimes(1);
    expect(refusals).toHaveLength(1);
  });

  // The re-read is marked, so its own 401 ends the session once, through the
  // judgement, and never reaches this interceptor to be judged again.
  it('ends the session once when the re-read is refused, and sends nothing more', async () => {
    // Arrange
    await signedInTo(FIRST_BUDGET);

    // Act
    const refusals = refuseTheSettingsRead();
    await settled();
    theReRead().flush(null, { status: 401, statusText: 'Unauthorized' });
    await settled();

    // Assert
    expect(ended).toHaveBeenCalledTimes(1);
    expect(destinations()).toEqual([WELCOME]);
    expect(session.status()).toBe('anonymous');
    expect(refusals).toHaveLength(1);
    http.expectNone(API_URL);
  });

  // **The judgement never rejects, even when ending the session throws.** The
  // interceptor waits on it inside a `catchError`, so a rejection would hand
  // the caller custody's error in place of its own 401 — and the three flows
  // that read the status off that 401 would read nothing. Custody's `lock` is
  // the last thing `ended()` does, after the status is already published, so
  // the session has still ended and the navigation is still owed.
  it('hands the caller its own 401 when ending the session after a re-read throws', async () => {
    // Arrange
    await signedInTo(FIRST_BUDGET);
    lock.mockImplementation(() => {
      throw new Error('Custody could not lock.');
    });

    // Act
    const refusals = refuseTheSettingsRead();
    await settled();
    theReRead().flush(ownerOf(SECOND_BUDGET));
    await settled();

    // Assert
    expect(refusals).toHaveLength(1);
    expect(refusals[0]).toBeInstanceOf(HttpErrorResponse);
    expect(refusals[0]).toMatchObject({ status: 401 });
    expect(destinations()).toEqual([WELCOME]);
    expect(session.status()).toBe('anonymous');
  });

  // The same on the path that sends no re-read: a tab holding no full session
  // ends at once, and that `ended()` throwing is the same rejection.
  it('hands the caller its own 401 when ending the session without a re-read throws', async () => {
    // Arrange
    expect(session.status()).toBe('unknown');
    lock.mockImplementation(() => {
      throw new Error('Custody could not lock.');
    });

    // Act
    const refusals = refuseTheSettingsRead();
    await settled();

    // Assert
    expect(refusals).toHaveLength(1);
    expect(refusals[0]).toBeInstanceOf(HttpErrorResponse);
    expect(refusals[0]).toMatchObject({ status: 401 });
    expect(destinations()).toEqual([WELCOME]);
    expect(session.status()).toBe('anonymous');
    http.expectNone(API_URL);
  });
});

// The locked sign-in's 401 is the provider token refused, answered to a browser
// that has no session yet — the release screen says so in its own sentence.
// Read as a session ending, it would send that person to `/welcome` from the
// one screen that can help them, over a session that never existed. Through
// the real `MeApiService`, because which request carries the mark is that
// service's fact.
//
// The schedule is the other way round, and the pair is the point: its request
// is unmarked, so a 401 there is the session ending — the interceptor's, and
// the release screen says nothing over it — while its 403 is the screen's own
// `unrecognised` and moves nobody.
describe('sessionExpiryInterceptor and the locked sign-in', () => {
  const LOCKED_SESSION_URL = `${API_BASE_URL}/api/locked-session`;
  const SCHEDULE_URL = `${API_BASE_URL}/api/me/erasure/schedule`;

  let http: HttpTestingController;
  let meApi: MeApiService;
  let ended: () => boolean;
  let destinations: () => readonly string[];

  beforeEach(() => {
    TestBed.resetTestingModule();
    const navigateByUrl = vi.fn(
      (url: string): Promise<boolean> => Promise.resolve(true),
    );
    const navigate = vi.fn(
      (commands: readonly string[]): Promise<boolean> => Promise.resolve(true),
    );
    const configuration: Pick<ConfigurationService, 'getConfig'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionExpiryInterceptor])),
        provideHttpClientTesting(),
        { provide: ConfigurationService, useValue: configuration },
        { provide: Router, useValue: { navigateByUrl, navigate } },
        {
          provide: AuthService,
          useValue: {
            forgetProviderToken: vi.fn(),
          } satisfies Pick<AuthService, 'forgetProviderToken'>,
        },
      ],
    });

    const session = TestBed.inject(SessionService);
    const endedSpy = vi.spyOn(session, 'ended');
    http = TestBed.inject(HttpTestingController);
    meApi = TestBed.inject(MeApiService);
    ended = () => endedSpy.mock.calls.length > 0;
    destinations = () => [
      ...navigateByUrl.mock.calls.map(([url]) => pathOf(url)),
      ...navigate.mock.calls.map(([commands]) => pathOf(commands)),
    ];
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      vi.restoreAllMocks();
      TestBed.resetTestingModule();
    }
  });

  it('navigates nowhere and ends no session when the locked sign-in is refused', async () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    meApi.openLockedSession('provider.token.locked').subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    http
      .expectOne(LOCKED_SESSION_URL)
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    await settled();

    // Assert
    expect(ended()).toBe(false);
    expect(destinations()).toEqual([]);
    // Still handed to the caller, which owes the person a sentence.
    expect(refusals).toHaveLength(1);
  });

  // Never probed, so `'unknown'`, and the refusal ends the session without a
  // re-read. Awaited, because the navigation now waits for the verdict.
  it('ends the session and leaves for the welcome screen when the schedule is refused 401', async () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    meApi.scheduleErasure().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    http
      .expectOne(SCHEDULE_URL)
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    await settled();

    // Assert
    expect(ended()).toBe(true);
    expect(destinations()).toEqual([WELCOME]);
    expect(refusals).toHaveLength(1);
  });

  it('navigates nowhere and ends no session when the schedule is refused 403', async () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    meApi.scheduleErasure().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    http
      .expectOne(SCHEDULE_URL)
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await settled();

    // Assert
    expect(ended()).toBe(false);
    expect(destinations()).toEqual([]);
    expect(refusals).toHaveLength(1);
  });
});
