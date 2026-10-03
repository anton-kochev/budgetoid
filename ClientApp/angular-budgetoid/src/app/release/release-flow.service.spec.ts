// The flow behind the release screen: the locked sign-in sent the moment a
// Google answer is taken, the trip that fetches one, the schedule, and the way
// off the screen. See docs/design/components.md, "Releasing an account".
//
// It drives a real `HttpClient` over the testing backend and the real
// `MeApiService`, because three facts this file pins live on the wire and
// nowhere else: *how many* locked sign-ins one answer sends, *which* route the
// commit reaches (the schedule, never the immediate erasure), and that a refused
// commit sends nothing at all.
//
// Three seams are replaced. `AuthService` hands over the Google answer and
// starts the trip; the real one needs the OAuth library. `SessionService` is a
// fake whose calls are recorded, so "published before any state" and "ended
// before the navigation" can be read at the instant they happen. The router is
// the real one with its outward call recorded, as `erasure-flow.service.spec.ts`
// records it. `ProviderDepartureService` is the real one: `departing` is its
// fact and the flow only reads it.
//
// **Vitest spies persist across cases here** (`restoreMocks` is unset), so every
// spy is built inside `beforeEach` and `afterEach` restores them.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, type UrlTree } from '@angular/router';
import type { SessionDto } from '@app-core/api/me-api.service';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { PROVIDER_CREDENTIAL } from '@app-core/interceptors/provider-credential.token';
import {
  AuthService,
  type LockedSignInReturn,
} from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import {
  SessionService,
  type ScheduledErasure,
  type SessionStatus,
} from '@app-core/session/session.service';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { ReleaseFlowService } from './release-flow.service';

const API_ORIGIN = 'https://api.budgetoid.test';
const LOCKED_SESSION_URL = `${API_ORIGIN}/api/locked-session`;
const SCHEDULE_URL = `${API_ORIGIN}/api/me/erasure/schedule`;
const REVOCATION_URL = `${API_ORIGIN}/api/me/session/revocation`;
// The immediate erasure, named only so its absence can be asserted: a locked
// session is refused there, and this screen's one act is the schedule.
const ERASURE_URL = `${API_ORIGIN}/api/me/erasure`;
const WELCOME_ROUTE = '/welcome';

const ID_TOKEN = 'provider.token.release';

const LOCKED_NOTHING_SCHEDULED = {
  kind: 'locked',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: null,
} as const satisfies SessionDto;

const LOCKED_SCHEDULED = {
  kind: 'locked',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: { takesEffectAtUtc: '2026-10-09T10:30:00Z' },
} as const satisfies SessionDto;

const SCHEDULED_INSTANT = '2026-10-16T08:00:00Z';

// What the flow was saying at the instant the session was told something.
interface Publication {
  readonly call: 'establishedLocked' | 'erasureScheduled' | 'ended';
  readonly argument: unknown;
  readonly signingIn: boolean;
  readonly signInFailure: unknown;
}

interface Navigation {
  readonly url: string;
  readonly endedCalls: number;
}

// The session as the flow sees it: a status to read, three methods to call.
// The methods move the status the way the real ones do, so a gate reading the
// status after a call reads what production would.
class SessionFake {
  public readonly statusSignal: WritableSignal<SessionStatus> =
    signal<SessionStatus>('anonymous');
  public readonly status = this.statusSignal.asReadonly();
  public readonly scheduledErasureSignal: WritableSignal<ScheduledErasure> =
    signal<ScheduledErasure>('unread');
  public readonly scheduledErasure = this.scheduledErasureSignal.asReadonly();
  public readonly publications: Publication[] = [];
  public reader: () => Pick<Publication, 'signingIn' | 'signInFailure'> =
    () => ({ signingIn: false, signInFailure: null });

  public readonly establishedLocked: Mock<(answer: SessionDto) => void> = vi.fn(
    (answer: SessionDto) => {
      this.record('establishedLocked', answer);
      this.statusSignal.set('locked-session');
      this.scheduledErasureSignal.set(answer.erasure);
    },
  );

  public readonly erasureScheduled: Mock<(takesEffectAtUtc: string) => void> =
    vi.fn((takesEffectAtUtc: string) => {
      this.record('erasureScheduled', takesEffectAtUtc);
      this.scheduledErasureSignal.set({ takesEffectAtUtc });
    });

  public readonly ended: Mock<() => void> = vi.fn(() => {
    this.record('ended', undefined);
    this.statusSignal.set('anonymous');
    this.scheduledErasureSignal.set('unread');
  });

  private record(call: Publication['call'], argument: unknown): void {
    this.publications.push({ call, argument, ...this.reader() });
  }
}

describe('ReleaseFlowService', () => {
  let http: HttpTestingController;
  let session: SessionFake;
  let departure: ProviderDepartureService;
  let navigations: Navigation[];
  let handOff: LockedSignInReturn | null;
  let tripAnswer: 'leaving' | 'unavailable';
  let auth: {
    readonly takeLockedSignInReturn: Mock<() => LockedSignInReturn | null>;
    readonly startLockedSignIn: Mock<() => Promise<'leaving' | 'unavailable'>>;
    readonly forgetProviderToken: Mock<() => void>;
  };

  beforeEach(() => {
    session = new SessionFake();
    navigations = [];
    handOff = null;
    tripAnswer = 'leaving';
    auth = {
      takeLockedSignInReturn: vi.fn(() => handOff),
      startLockedSignIn: vi.fn(() => Promise.resolve(tripAnswer)),
      forgetProviderToken: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: AuthService, useValue: auth },
        { provide: SessionService, useValue: session },
        ReleaseFlowService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    departure = TestBed.inject(ProviderDepartureService);

    const router = TestBed.inject(Router);

    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        navigations.push({
          url: typeof url === 'string' ? url : router.serializeUrl(url),
          endedCalls: session.ended.mock.calls.length,
        });

        return Promise.resolve(true);
      },
    );
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      vi.restoreAllMocks();
      TestBed.resetTestingModule();
    }
  });

  // Builds the flow with whatever the hand-off holds, and wires the session
  // fake to read the flow at the instant it is called.
  function start(): ReleaseFlowService {
    const flow = TestBed.inject(ReleaseFlowService);

    session.reader = () => ({
      signingIn: flow.signingIn(),
      signInFailure: flow.signInFailure(),
    });

    return flow;
  }

  // A flow on a locked session with nothing scheduled and no answer to send:
  // the surface the commit lives on.
  function startLocked(): ReleaseFlowService {
    session.statusSignal.set('locked-session');
    session.scheduledErasureSignal.set(null);

    return start();
  }

  // One request to `url`, waited for. `match` removes what it finds, so a
  // second request in the same sweep is reported rather than lost.
  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(() => {
      const found = http.match(url);

      if (found.length > 1) {
        throw new Error(`${found.length} requests to ${url}, not one.`);
      }

      return found[0] ?? null;
    }, `a request to ${url}`);
  }

  // Lets every queued task run, then reports how many requests to `url` are
  // open. A retry scheduled on a timer of zero, or on a promise, is out by then.
  async function openRequestsAfterQuiet(url: string): Promise<number> {
    for (let turn = 0; turn < 20; turn += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    return http.match(url).length;
  }

  async function signInAnswered(
    answer: (request: TestRequest) => void,
  ): Promise<ReleaseFlowService> {
    handOff = { kind: 'answered', idToken: ID_TOKEN };
    const flow = start();
    answer(await requestTo(LOCKED_SESSION_URL));

    return flow;
  }

  describe('taking the Google answer', () => {
    it('takes the hand-off once, when it is built', () => {
      // Act
      start();

      // Assert
      expect(auth.takeLockedSignInReturn).toHaveBeenCalledTimes(1);
    });

    // C9: the Continue press, made under prose saying what the trip is for,
    // is the consent. A second press after the return asks the question twice.
    it('sends the locked sign-in at once with the token it was handed', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };

      // Act
      start();
      const request = await requestTo(LOCKED_SESSION_URL);

      // Assert
      expect(request.request.method).toBe('POST');
      expect(request.request.context.get(PROVIDER_CREDENTIAL)).toBe(ID_TOKEN);
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

      request.flush(LOCKED_NOTHING_SCHEDULED);
    });

    it('reads as signing in while the sign-in is out', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };

      // Act
      const flow = start();
      const request = await requestTo(LOCKED_SESSION_URL);

      // Assert
      expect(flow.signingIn()).toBe(true);
      expect(flow.continuePressable()).toBe(false);

      request.flush(LOCKED_NOTHING_SCHEDULED);
    });

    it('sends nothing when the load carried no answer', async () => {
      // Act
      const flow = start();

      // Assert
      expect(await openRequestsAfterQuiet(LOCKED_SESSION_URL)).toBe(0);
      expect(flow.signInFailure()).toBeNull();
      expect(flow.signingIn()).toBe(false);
    });

    it('reads an unconfirmed return as unconfirmed and sends nothing', async () => {
      // Arrange
      handOff = { kind: 'unconfirmed' };

      // Act
      const flow = start();

      // Assert
      expect(await openRequestsAfterQuiet(LOCKED_SESSION_URL)).toBe(0);
      expect(flow.signInFailure()).toBe('unconfirmed');
      expect(session.publications).toEqual([]);
    });
  });

  describe("the locked sign-in's answer", () => {
    it.each([
      { why: 'nothing scheduled', body: LOCKED_NOTHING_SCHEDULED },
      { why: 'a schedule', body: LOCKED_SCHEDULED },
    ])(
      'publishes a 200 carrying $why as a locked session, with the answer as it arrived',
      async ({ body }) => {
        // Act
        const flow = await signInAnswered((request) => request.flush(body));
        await eventually(
          () => (session.publications.length > 0 ? true : null),
          'the session to be told',
        );

        // Assert
        expect(session.establishedLocked).toHaveBeenCalledTimes(1);
        expect(session.establishedLocked).toHaveBeenCalledWith(body);
        expect(flow.signInFailure()).toBeNull();
      },
    );

    // The screen reads the session status to pick its state. A flow that
    // dropped `signingIn` first would draw "before the trip" for one render
    // between the waiting line and the locked surface.
    it('tells the session before it publishes anything of its own', async () => {
      // Act
      const flow = await signInAnswered((request) =>
        request.flush(LOCKED_NOTHING_SCHEDULED),
      );
      await eventually(
        () => (flow.signingIn() ? null : true),
        'the sign-in to finish',
      );

      // Assert
      expect(session.publications).toEqual([
        {
          call: 'establishedLocked',
          argument: LOCKED_NOTHING_SCHEDULED,
          signingIn: true,
          signInFailure: null,
        },
      ]);
    });

    // The status stays what it was: choosing another Google account is a
    // real way forward for somebody who has two.
    it('reads a no-account refusal as no-account and leaves the session alone', async () => {
      // Act
      const flow = await signInAnswered((request) =>
        request.flush(
          { refusal: 'no_account' },
          { status: 404, statusText: 'Not Found' },
        ),
      );
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(flow.signInFailure()).toBe('no-account');
      expect(session.publications).toEqual([]);
      expect(session.status()).toBe('anonymous');
      expect(flow.signingIn()).toBe(false);
    });

    it.each([
      {
        why: 'a 401',
        status: 401,
        body: null,
        word: 'provider-refused',
      },
      { why: 'a 403', status: 403, body: null, word: 'unrecognised' },
      // The word comes from the member, never from the status alone.
      {
        why: 'a 404 naming no refusal',
        status: 404,
        body: null,
        word: 'undetermined',
      },
      {
        why: 'a 404 naming another refusal',
        status: 404,
        body: { refusal: 'something_else' },
        word: 'undetermined',
      },
      { why: 'a 409', status: 409, body: null, word: 'undetermined' },
      { why: 'a 500', status: 500, body: null, word: 'undetermined' },
      { why: 'a 503', status: 503, body: null, word: 'undetermined' },
    ])('reads $why as $word', async ({ status, body, word }) => {
      // Act
      const flow = await signInAnswered((request) =>
        request.flush(body, { status, statusText: 'Refused' }),
      );
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(flow.signInFailure()).toBe(word);
      expect(session.publications).toEqual([]);
      expect(flow.signingIn()).toBe(false);
    });

    it('reads a request that got no answer as undetermined', async () => {
      // Act
      const flow = await signInAnswered((request) =>
        request.error(new ProgressEvent('error')),
      );
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(flow.signInFailure()).toBe('undetermined');
      expect(session.publications).toEqual([]);
    });

    // A 200 the client cannot read may still have set the cookie; publishing
    // a session from it would claim what nothing confirmed, and the reload the
    // sentence names is what asks the server.
    it('reads a 200 that does not read as undetermined and publishes nothing', async () => {
      // Act
      const flow = await signInAnswered((request) =>
        request.flush({ ...LOCKED_NOTHING_SCHEDULED, kind: 'full' }),
      );
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(flow.signInFailure()).toBe('undetermined');
      expect(session.establishedLocked).not.toHaveBeenCalled();
    });

    // One answer, one request — not by the flow and not on a timer. A second
    // locked sign-in after a lost 200 is harmless to the server, but the book
    // says the next request is the person's.
    it.each([
      {
        why: 'no answer',
        answer: (r: TestRequest) => r.error(new ProgressEvent('error')),
      },
      {
        why: 'a 500',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 500, statusText: 'Server Error' }),
      },
      { why: 'an unreadable 200', answer: (r: TestRequest) => r.flush([]) },
    ])('sends exactly one locked sign-in after $why', async ({ answer }) => {
      // Act
      const flow = await signInAnswered(answer);
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(await openRequestsAfterQuiet(LOCKED_SESSION_URL)).toBe(0);
    });
  });

  describe('continue', () => {
    it('starts the locked sign-in trip on a press', async () => {
      // Arrange
      const flow = start();

      // Act
      flow.continue();

      // Assert
      await eventually(
        () => (auth.startLockedSignIn.mock.calls.length > 0 ? true : null),
        'the trip to start',
      );
      expect(auth.startLockedSignIn).toHaveBeenCalledTimes(1);
    });

    it('reads a trip that could not start as unavailable', async () => {
      // Arrange
      tripAnswer = 'unavailable';
      const flow = start();

      // Act
      flow.continue();
      await eventually(() => flow.signInFailure(), 'a sign-in word');

      // Assert
      expect(flow.signInFailure()).toBe('unavailable');
    });

    it('reads a trip that is leaving as no failure', async () => {
      // Arrange
      const flow = start();

      // Act
      flow.continue();
      await openRequestsAfterQuiet(LOCKED_SESSION_URL);

      // Assert
      expect(flow.signInFailure()).toBeNull();
    });

    // Material halts a click on a disabled-interactive anchor only; on a
    // `<button>` the press arrives whatever the attribute says, and an ungated
    // press starts a second trip under the first.
    it('refuses a press while the page is departing', async () => {
      // Arrange
      const flow = start();
      departure.begin();

      // Act
      flow.continue();
      await openRequestsAfterQuiet(LOCKED_SESSION_URL);

      // Assert
      expect(flow.continuePressable()).toBe(false);
      expect(auth.startLockedSignIn).not.toHaveBeenCalled();
    });

    it('refuses a press while a sign-in is out', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const flow = start();
      const request = await requestTo(LOCKED_SESSION_URL);

      // Act
      flow.continue();
      await new Promise((resolve) => setTimeout(resolve, 0));

      // Assert
      expect(auth.startLockedSignIn).not.toHaveBeenCalled();

      request.flush(LOCKED_NOTHING_SCHEDULED);
    });

    it('is pressable at rest', () => {
      // Act
      const flow = start();

      // Assert
      expect(flow.continuePressable()).toBe(true);
    });
  });

  describe('commit', () => {
    it('is not pressable until the acknowledgement is ticked', () => {
      // Act
      const flow = startLocked();

      // Assert
      expect(flow.acknowledged()).toBe(false);
      expect(flow.commitPressable()).toBe(false);
    });

    it('is pressable once the acknowledgement is ticked on a locked session', () => {
      // Arrange
      const flow = startLocked();

      // Act
      flow.acknowledge(true);

      // Assert
      expect(flow.acknowledged()).toBe(true);
      expect(flow.commitPressable()).toBe(true);
    });

    it('is not pressable again once the acknowledgement is unticked', () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.acknowledge(false);

      // Assert
      expect(flow.commitPressable()).toBe(false);
    });

    // The handler refuses what the attribute draws as refused.
    it('sends nothing on a press without the acknowledgement', async () => {
      // Arrange
      const flow = startLocked();

      // Act
      flow.commit();

      // Assert
      expect(await openRequestsAfterQuiet(SCHEDULE_URL)).toBe(0);
    });

    // The moment after Sign out or an ended session: the press would send a
    // schedule from a screen that no longer holds a session.
    it.each<SessionStatus>([
      'anonymous',
      'unknown',
      'unreachable',
      'authenticated',
    ])('sends nothing on a press when the status is %s', async (status) => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);
      session.statusSignal.set(status);

      // Act
      flow.commit();

      // Assert
      expect(flow.commitPressable()).toBe(false);
      expect(await openRequestsAfterQuiet(SCHEDULE_URL)).toBe(0);
    });

    it('sends no second schedule while the first is out', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);
      flow.commit();
      const request = await requestTo(SCHEDULE_URL);

      // Act
      flow.commit();

      // Assert
      expect(flow.scheduling()).toBe(true);
      expect(flow.commitPressable()).toBe(false);
      expect(await openRequestsAfterQuiet(SCHEDULE_URL)).toBe(0);

      request.flush({ takesEffectAtUtc: SCHEDULED_INSTANT });
    });

    it('posts the schedule with an empty body, and never the immediate erasure', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.commit();
      const request = await requestTo(SCHEDULE_URL);

      // Assert
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toBeNull();
      expect(http.match(ERASURE_URL)).toEqual([]);

      request.flush({ takesEffectAtUtc: SCHEDULED_INSTANT });
    });

    it('tells the session the instant the server stored', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.commit();
      (await requestTo(SCHEDULE_URL)).flush({
        takesEffectAtUtc: SCHEDULED_INSTANT,
      });
      await eventually(
        () => (session.erasureScheduled.mock.calls.length > 0 ? true : null),
        'the schedule to be published',
      );

      // Assert
      expect(session.erasureScheduled).toHaveBeenCalledTimes(1);
      expect(session.erasureScheduled).toHaveBeenCalledWith(SCHEDULED_INSTANT);
      expect(flow.scheduleFailure()).toBeNull();
      expect(flow.scheduling()).toBe(false);
    });

    // A 401 is an ended session, and that is `sessionExpiryInterceptor`'s:
    // it ends the session and takes the tab to Welcome. The screen says
    // nothing over it, and a flow that ended the session itself would make
    // two owners of one fact.
    it('says nothing and ends nothing on a 401', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.commit();
      (await requestTo(SCHEDULE_URL)).flush(null, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await eventually(
        () => (flow.scheduling() ? null : true),
        'the schedule to finish',
      );

      // Assert
      expect(flow.scheduleFailure()).toBeNull();
      expect(session.erasureScheduled).not.toHaveBeenCalled();
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
    });

    it('reads a 403 as unrecognised', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.commit();
      (await requestTo(SCHEDULE_URL)).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });
      await eventually(() => flow.scheduleFailure(), 'a schedule word');

      // Assert
      expect(flow.scheduleFailure()).toBe('unrecognised');
      expect(session.erasureScheduled).not.toHaveBeenCalled();
    });

    it.each([
      {
        why: 'no answer',
        answer: (r: TestRequest) => r.error(new ProgressEvent('error')),
      },
      {
        why: 'a 500',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 500, statusText: 'Server Error' }),
      },
      {
        why: 'a 409',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 409, statusText: 'Conflict' }),
      },
      {
        why: 'a 200 whose instant has no offset',
        answer: (r: TestRequest) =>
          r.flush({ takesEffectAtUtc: '2026-10-16T08:00:00' }),
      },
      {
        why: 'a 200 naming no instant',
        answer: (r: TestRequest) => r.flush({}),
      },
    ])('reads $why as undetermined', async ({ answer }) => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);

      // Act
      flow.commit();
      answer(await requestTo(SCHEDULE_URL));
      await eventually(() => flow.scheduleFailure(), 'a schedule word');

      // Assert
      expect(flow.scheduleFailure()).toBe('undetermined');
      expect(session.erasureScheduled).not.toHaveBeenCalled();
    });

    // C6, and the erasure dialog's rule inverted on purpose: a schedule is
    // idempotent, so another press is how the person finds out — and the press
    // is theirs, never a timer's.
    it('keeps the commit live after undetermined and sends nothing until pressed', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);
      flow.commit();
      (await requestTo(SCHEDULE_URL)).flush(null, {
        status: 500,
        statusText: 'Server Error',
      });
      await eventually(() => flow.scheduleFailure(), 'a schedule word');

      // Act
      const sentUnpressed = await openRequestsAfterQuiet(SCHEDULE_URL);

      // Assert
      expect(sentUnpressed).toBe(0);
      expect(flow.commitPressable()).toBe(true);
    });

    it('sends exactly one more schedule on the next press after undetermined', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);
      flow.commit();
      (await requestTo(SCHEDULE_URL)).error(new ProgressEvent('error'));
      await eventually(() => flow.scheduleFailure(), 'a schedule word');

      // Act
      flow.commit();
      const second = await requestTo(SCHEDULE_URL);
      second.flush({ takesEffectAtUtc: SCHEDULED_INSTANT });
      await eventually(
        () => (session.erasureScheduled.mock.calls.length > 0 ? true : null),
        'the schedule to be published',
      );

      // Assert
      expect(session.erasureScheduled).toHaveBeenCalledWith(SCHEDULED_INSTANT);
      expect(await openRequestsAfterQuiet(SCHEDULE_URL)).toBe(0);
    });
  });

  describe('sign out', () => {
    // Settings' semantics: the revocation, then `ended()`, then `/welcome`.
    // Navigate first and the guard on `/welcome` reads a stale status.
    it('posts the revocation, ends the session, then leaves for welcome', async () => {
      // Arrange
      const flow = startLocked();

      // Act
      flow.signOut();
      (await requestTo(REVOCATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (navigations.length > 0 ? true : null),
        'a navigation',
      );

      // Assert
      expect(session.ended).toHaveBeenCalledTimes(1);
      expect(navigations).toEqual([{ url: WELCOME_ROUTE, endedCalls: 1 }]);
    });

    it.each([
      {
        why: 'a 500',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 500, statusText: 'Server Error' }),
      },
      {
        why: 'no answer',
        answer: (r: TestRequest) => r.error(new ProgressEvent('error')),
      },
      {
        why: 'a 401',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 401, statusText: 'Unauthorized' }),
      },
    ])('still signs out after $why', async ({ answer }) => {
      // Arrange
      const flow = startLocked();

      // Act
      flow.signOut();
      answer(await requestTo(REVOCATION_URL));
      await eventually(
        () => (navigations.length > 0 ? true : null),
        'a navigation',
      );

      // Assert
      expect(session.ended).toHaveBeenCalledTimes(1);
      expect(navigations).toEqual([{ url: WELCOME_ROUTE, endedCalls: 1 }]);
    });

    // Never held, including while a schedule is out.
    it('posts the revocation while a schedule is out', async () => {
      // Arrange
      const flow = startLocked();
      flow.acknowledge(true);
      flow.commit();
      const schedule = await requestTo(SCHEDULE_URL);

      // Act
      flow.signOut();
      const revocation = await requestTo(REVOCATION_URL);

      // Assert
      expect(revocation.request.method).toBe('POST');

      revocation.flush(null, { status: 204, statusText: 'No Content' });
      schedule.flush({ takesEffectAtUtc: SCHEDULED_INSTANT });
    });
  });
});

async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Timed out waiting for ${what}.`);
}
