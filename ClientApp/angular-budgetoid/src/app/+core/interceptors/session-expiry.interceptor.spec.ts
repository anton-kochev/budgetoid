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
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MeApiService } from '@app-core/api/me-api.service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EXPECTS_UNAUTHENTICATED } from './expects-unauthenticated.token';
import { sessionExpiryInterceptor } from './session-expiry.interceptor';

// The same two origins `api-credentials.interceptor.spec.ts` uses, and for the
// same reason: the predicate that decides which of them this interceptor may
// act on is meant to be one predicate shared by both files, so the two specs
// have to be able to disagree about it.
const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;
// The third route this block reaches, and the only one whose 401 belongs to
// nobody: it is read by `AccountKeyCustodyService` and by nothing else.
const ACCOUNT_KEYS_URL = `${API_BASE_URL}/api/me/account-keys`;
const OTHER_ORIGIN_URL =
  'https://accounts.google.com/.well-known/openid-configuration';

const WELCOME = 'welcome';

interface RunOptions {
  // What the rest of the chain answers with. An `HttpErrorResponse` is thrown
  // to the interceptor; anything else is delivered as a response.
  readonly answer?: HttpErrorResponse | HttpResponse<unknown>;
  readonly apiBaseUrl?: string;
}

interface Outcome {
  // Whether the session was declared over, and where the browser was sent.
  // Both, on every test, because the two halves fail apart: an interceptor that
  // navigates without calling `ended()` leaves the guard on `/welcome` reading
  // `'authenticated'` and bouncing the visitor straight back.
  readonly ended: boolean;
  readonly destination: string | null;
  readonly errors: readonly unknown[];
  readonly events: readonly HttpEvent<unknown>[];
}

function refusal(status: number, url: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, url });
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
function outcomeOf(
  request: HttpRequest<unknown>,
  options: RunOptions = {},
): Outcome {
  const { answer = refusal(401, request.url), apiBaseUrl = API_BASE_URL } =
    options;

  // Reset first, so a test may run the interceptor more than once: the first
  // `runInInjectionContext` instantiates the injector, after which a second
  // `configureTestingModule` throws.
  TestBed.resetTestingModule();

  const ended = vi.fn((): void => undefined);
  const navigateByUrl = vi.fn(
    (url: string): Promise<boolean> => Promise.resolve(true),
  );
  const navigate = vi.fn(
    (commands: readonly string[]): Promise<boolean> => Promise.resolve(true),
  );
  const configuration: Pick<ConfigurationService, 'getConfig'> = {
    getConfig: () => ({ apiBaseUrl, auth: {} }),
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: ConfigurationService, useValue: configuration },
      { provide: SessionService, useValue: { ended } },
      { provide: Router, useValue: { navigateByUrl, navigate } },
    ],
  });

  const next: HttpHandlerFn = () =>
    answer instanceof HttpErrorResponse ? throwError(() => answer) : of(answer);

  const events: HttpEvent<unknown>[] = [];
  const errors: unknown[] = [];

  TestBed.runInInjectionContext(() =>
    sessionExpiryInterceptor(request, next),
  ).subscribe({
    next: (event) => events.push(event),
    error: (error: unknown) => errors.push(error),
  });

  const [byUrl] = navigateByUrl.mock.calls;
  const [byCommands] = navigate.mock.calls;
  const destination =
    byUrl !== undefined
      ? pathOf(byUrl[0])
      : byCommands !== undefined
        ? pathOf(byCommands[0])
        : null;

  return {
    ended: ended.mock.calls.length > 0,
    destination,
    errors,
    events,
  };
}

function apiGet(context?: HttpContext): HttpRequest<unknown> {
  return new HttpRequest<unknown>('GET', API_URL, context ? { context } : {});
}

describe('sessionExpiryInterceptor', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  // The one thing this interceptor exists for. Without it a session that lapsed
  // mid-visit leaves the browser on a screen whose every read now fails, with
  // `SessionService` still saying `'authenticated'` and nothing on the page
  // saying why the numbers stopped arriving.
  it('ends the session and leaves for the welcome screen when the API answers 401', () => {
    // Arrange
    const request = apiGet();

    // Act
    const outcome = outcomeOf(request);

    // Assert
    expect(outcome.ended).toBe(true);
    expect(outcome.destination).toBe(WELCOME);
  });

  // An observer, not a handler. Swallowed here, a 401 reaches no caller's
  // `catchError`, so the screen that made the request renders neither its
  // outcome nor its failure — it sits on its loading line forever, under a
  // navigation that may itself be cancelled by a guard.
  it('re-throws the error rather than swallowing it', () => {
    // Arrange
    const answer = refusal(401, API_URL);

    // Act
    const outcome = outcomeOf(apiGet(), { answer });

    // Assert
    expect(outcome.errors).toEqual([answer]);
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
  it('leaves a request that expects a refusal alone', () => {
    // Arrange
    const context = new HttpContext().set(EXPECTS_UNAUTHENTICATED, true);
    const request = apiGet(context);

    // Act
    const outcome = outcomeOf(request);

    // Assert
    expect(outcome.ended).toBe(false);
    expect(outcome.destination).toBeNull();
    // Still re-thrown: the ceremony's own handler is what renders "that code
    // didn't match", and it only ever sees the error if this passes it on.
    expect(outcome.errors).toHaveLength(1);
  });

  // The negative control for the shared predicate. This app talks to the
  // identity provider through the same `HttpClient`, and a 401 from Google's
  // discovery endpoint is a statement about a token this product does not
  // issue. Signing somebody out of Budgetoid over it is a sign-out caused by a
  // third party.
  it('signs nobody out when another origin answers 401', () => {
    // Arrange
    const request = new HttpRequest<unknown>('GET', OTHER_ORIGIN_URL);
    const answer = refusal(401, OTHER_ORIGIN_URL);

    // Act
    const outcome = outcomeOf(request, { answer });

    // Assert
    expect(outcome.ended).toBe(false);
    expect(outcome.destination).toBeNull();
    expect(outcome.errors).toHaveLength(1);
  });

  // 403 is the CSRF refusal — a request that arrived without the client header
  // — and the locked-session refusal. Both are answered to a browser whose
  // session is intact, so acting on one ends a live session over a bug in the
  // request builder.
  it('leaves a 403 alone', () => {
    // Arrange
    const answer = refusal(403, API_URL);

    // Act
    const outcome = outcomeOf(apiGet(), { answer });

    // Assert
    expect(outcome.ended).toBe(false);
    expect(outcome.destination).toBeNull();
  });

  // The control for every test above: an interceptor that called `ended()` on
  // its way past each response would satisfy the 401 case perfectly and sign
  // out every visitor on their first successful read.
  it('leaves a successful response alone', () => {
    // Arrange
    const answer = new HttpResponse<unknown>({ status: 200, url: API_URL });

    // Act
    const outcome = outcomeOf(apiGet(), { answer });

    // Assert
    expect(outcome.ended).toBe(false);
    expect(outcome.destination).toBeNull();
    expect(outcome.events).toEqual([answer]);
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
    refuse();
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
    refuse();
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
  it('ends the session and leaves for the welcome screen when the settings read of the same route is refused', () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    wiring.meApi.getMe().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    refuse();

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
  it('navigates nowhere and ends no session when the account-key read is refused', () => {
    // Arrange
    const refusals: unknown[] = [];

    // Act
    wiring.meApi.getAccountKeys().subscribe({
      error: (error: unknown) => refusals.push(error),
    });
    refuseAt(ACCOUNT_KEYS_URL);

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
