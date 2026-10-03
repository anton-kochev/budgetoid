import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
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
import { Observable, Subject, TimeoutError, of, throwError } from 'rxjs';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { SessionService } from './session.service';

// The session cookie is `HttpOnly`, so script cannot read it and a cold load
// has no local evidence at all about who the visitor is. Asking the server is
// the only way to find out, which makes every one of these tests a test about
// how an *answer that did not arrive* is read.
const BUDGET_ID = '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471';
const ME: MeDto = {
  budgetId: BUDGET_ID,
  email: 'owner@budgetoid.test',
};

// What `GET /api/me/session` answers, one per kind. The expiry is never read by
// the class under test; it is here because the answer always carries it.
const FULL_SESSION: SessionDto = {
  kind: 'full',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: null,
};

const LOCKED_SESSION: SessionDto = {
  kind: 'locked',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: null,
};

// An account whose erasure is already on file, seven days out.
const TAKES_EFFECT_AT_UTC = '2026-10-10T08:00:00Z';
const SCHEDULED: SessionDto['erasure'] = {
  takesEffectAtUtc: TAKES_EFFECT_AT_UTC,
};

// What Angular hands a subscriber when the request never reached a server: the
// backend rejects, and `HttpClient` reports that as status `0` with the
// browser's own error event in `error`. There is no response here to read a
// status off — which is the whole reason it must not be read as a refusal.
const NETWORK_FAILURE = new HttpErrorResponse({
  status: 0,
  statusText: 'Unknown Error',
  error: new ProgressEvent('error'),
});

function refusal(status: number): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    url: 'https://api.budgetoid.app/api/me',
  });
}

// `getSessionOwner` and not `getMe`, which is the same route asked a different
// question. The probe's 401 is this call's own answer, so it carries
// `EXPECTS_UNAUTHENTICATED` and `sessionExpiryInterceptor` leaves it alone; the
// Settings screen's read of `/api/me` carries nothing and a 401 there is the
// session ending. Which of the two `probe()` calls is the split, and it is
// pinned as an interaction in `session-expiry.interceptor.spec.ts` — this stub
// only has to name the same method the service reaches for.
//
// `getSession` is the probe's first question — what kind of session, if any —
// and it answers a full session here by default, so every case below that is
// about the owner read reaches it the way a cold load does.
class MeApiStub {
  public getSession = vi.fn((): Observable<SessionDto> => of(FULL_SESSION));
  public getSessionOwner = vi.fn((): Observable<MeDto> => of(ME));
}

// The account's keys, replaced by three counters. **Every member, not just
// `lock`**, because the two rules below are a matched pair — one says this class
// must reach for custody and the other says it must not — and only a census can
// state the second one as "nothing at all happened" rather than as "the member I
// happened to think of was not called". A `SessionService` that grew an
// `unlock` or an `adopt` call would slip past a spy on `lock` alone.
class CustodyStub {
  public unlock = vi.fn((): void => undefined);
  public adopt = vi.fn((): void => undefined);
  public lock = vi.fn((): void => undefined);
}

// Which of the three was reached, by name. A failure that says
// `[ 'lock' ]` where `[]` was expected names the mutation; `toHaveBeenCalled`
// would only say `true`.
function touchedMembersOf(custody: CustodyStub): readonly string[] {
  return (['unlock', 'adopt', 'lock'] as const).filter(
    (name) => custody[name].mock.calls.length > 0,
  );
}

// Every member of the provider service, so a census can name which one was
// reached rather than only that one was. Listed rather than derived, as
// `sign-in.service.spec.ts` lists its own: the census is a list somebody has to
// extend deliberately when `AuthService` grows a member. What it exists to
// refuse is a member wrapping the argument-less `logOut()`: that overload
// navigates to the provider's end-session endpoint whenever the library knows
// one, `logOut(true)` never does, and the two read the same in a diff.
type ProviderStub = Readonly<Record<keyof AuthService, Mock>>;

function providerStub(): ProviderStub {
  return {
    initialize: vi.fn(),
    providerEmail: vi.fn(),
    signIn: vi.fn(),
    forgetProviderToken: vi.fn(),
    providerReturn: vi.fn(),
    startEmailChange: vi.fn(),
    takeEmailChangeReturn: vi.fn(),
    dropEmailChangeReturn: vi.fn(),
    discardUnreadAnswer: vi.fn(),
  };
}

function calledProviderMembersOf(provider: ProviderStub): readonly string[] {
  return Object.entries(provider)
    .filter(([, member]) => member.mock.calls.length > 0)
    .map(([name]) => name);
}

describe('SessionService', () => {
  let service: SessionService;
  let api: MeApiStub;
  let custody: CustodyStub;
  let provider: ProviderStub;

  beforeEach(() => {
    api = new MeApiStub();
    custody = new CustodyStub();
    provider = providerStub();
    TestBed.configureTestingModule({
      providers: [
        { provide: MeApiService, useValue: api },
        // A census stub, built fresh per case: no `restoreMocks` is configured,
        // so a spy shared across cases would answer from an earlier case's
        // history.
        { provide: AuthService, useValue: provider },
        // Stubbed rather than real, unlike `sign-in.service.spec.ts`, and for
        // the opposite reason: nothing here is interested in what custody
        // *does*, only in whether this class told it to. The real service would
        // answer both questions and would also go and read
        // `/api/me/account-keys` over an `MeApiService` stub that has no such
        // member, which is a failure about the fixture rather than about the
        // rule.
        { provide: AccountKeyCustodyService, useValue: custody },
      ],
    });
    service = TestBed.inject(SessionService);
  });

  // Every stub above is built fresh per case, so nothing here needs restoring
  // today; this is for the `vi.spyOn` a later case adds, which — with no
  // `restoreMocks` configured — would otherwise outlive its case.
  afterEach(() => {
    vi.restoreAllMocks();
  });

  // The state every guard on the site reads before a route activates, and the
  // one the initializer exists to make unobservable. Asserted at rest *and*
  // while the read is in flight: an implementation that seeded `'anonymous'`
  // and only moved off it on an answer would pass a rest-only assertion while
  // bouncing every visitor whose network is slow.
  it('says nothing about the visitor until an answer arrives', () => {
    // Arrange
    const pending = new Subject<SessionDto>();
    api.getSession.mockReturnValue(pending);

    // Act
    expect(service.status()).toBe('unknown');
    void service.probe();

    // Assert
    expect(service.status()).toBe('unknown');
  });

  it('answers authenticated when the server answers the read', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(of(ME));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('authenticated');
  });

  // **The budget rides on the answer the probe already asks for.** It is the
  // fourth field of every blind-index message, so without it no name can be
  // written to a blind-indexed column at all — and it is read here rather than
  // by a request of its own precisely because the initializer awaits this
  // promise, which is what puts the identifier in place before the first route
  // activates.
  it('publishes the budget the read named', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(of(ME));

    // The guard that keeps the assertion honest: nothing was holding this value
    // before the read, so what is asserted below came out of the answer.
    expect(service.budgetId()).toBeNull();

    // Act
    await service.probe();

    // Assert
    expect(service.budgetId()).toBe(BUDGET_ID);
  });

  // **The spelling is passed through and never repaired.** The codec that owns
  // the grammar refuses anything but the canonical form, and a fold made here
  // would invent a second spelling of a value that has one — at the writing end,
  // where every row keyed under the invented spelling is a row no later lookup
  // reproduces, with nothing on either side of the wire able to see it.
  it('publishes the budget in the spelling the read used', async () => {
    // Arrange
    const shouted = BUDGET_ID.toUpperCase();
    api.getSessionOwner.mockReturnValue(of({ ...ME, budgetId: shouted }));

    // Act
    await service.probe();

    // Assert
    expect(service.budgetId()).toBe(shouted);
  });

  // **A body carrying no budget is not a failed probe**, and that is the one
  // place this file departs from `me-api.service.ts`'s "refuse, never coerce"
  // rule — because a refusal here would take the *status* down with it. The
  // answer still says there is a session and whose it is; reading it as
  // `unreachable` signs somebody out over a version skew. `null` is the honest
  // reading: signed in, tenancy unknown, and every write says so.
  it.each([
    { why: 'a body naming no budget', body: {} },
    { why: 'a budget that is not a spelling at all', body: { budgetId: 42 } },
    { why: 'an empty budget', body: { budgetId: '' } },
  ])('stays authenticated and holds no budget for $why', async ({ body }) => {
    // Arrange
    api.getSessionOwner.mockReturnValue(
      of({ email: ME.email, ...body } as MeDto),
    );

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('authenticated');
    expect(service.budgetId()).toBeNull();
  });

  // A read that did not land is not a claim about which budget anybody is in,
  // and a stale identifier left standing would be keyed into the values written
  // by whoever comes back next.
  it('holds no budget when the read fails', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValueOnce(of(ME));
    await service.probe();
    // The first probe really did publish one. Without this the assertion below
    // holds on a service that never publishes a budget at all.
    expect(service.budgetId()).toBe(BUDGET_ID);
    api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

    // Act
    await service.probe();

    // Assert
    expect(service.budgetId()).toBeNull();
  });

  // 401 is the server saying it knows who is asking and the answer is nobody.
  // That is evidence, and it is the only kind this class treats as evidence.
  it('answers anonymous when the server refuses the read as unauthenticated', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(401)));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('anonymous');
  });

  // The CSRF refusal and the locked-session refusal both land here. Neither is
  // an authenticated visitor, and neither has a next step that differs from a
  // 401's, so the two collapse deliberately — this is the one collapse in the
  // file that is correct.
  it('answers anonymous when the server refuses the read outright', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(403)));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('anonymous');
  });

  // The most important test here, and the one a future reader will delete while
  // simplifying four states into two. A request that never got an answer is not
  // evidence about the visitor; read as one, it signs a person holding a
  // perfectly good session out of their own account and drops them on a
  // marketing page because the network blinked once during the cold load. It is
  // `SettingsService`'s "never collapse `null` to `0`" rule, one screen over: a
  // claim about the account made out of a failure to ask.
  it('does not sign a visitor out because the read never reached the server', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => NETWORK_FAILURE));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).not.toBe('anonymous');
    expect(service.status()).toBe('unreachable');
  });

  // The same argument reached from a server that is up and broken. A 500 says
  // nothing about who is asking either, and an implementation that treats
  // "not 200" as "not signed in" passes every test above this line.
  it('answers unreachable when the server fails to answer the read', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(500)));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('unreachable');
  });

  // The initializer awaits this promise, so a rejection is not a failed probe —
  // it is an application that never finishes bootstrapping and a browser left
  // on a blank page. Every failing case above already awaits `probe()` and so
  // would catch this, but none of them says so, and the next person to add a
  // `throw` inside the handler needs a test whose name names the consequence.
  it('resolves rather than rejecting when the read fails', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => NETWORK_FAILURE));

    // Act & Assert
    await expect(service.probe()).resolves.toBeUndefined();
  });

  // Arranged through a real probe rather than by seeding the signal: the
  // transition that matters is the mid-visit one, from a session that was
  // genuinely established to one the server has stopped honouring, and an
  // implementation that only ever sets `'anonymous'` from `'unknown'` would
  // pass a test that started at rest.
  it('moves an authenticated visitor to anonymous when the session ends', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(of(ME));
    await service.probe();
    expect(service.status()).toBe('authenticated');

    // Act
    service.ended();

    // Assert
    expect(service.status()).toBe('anonymous');
  });

  // **Custody ends where the session does, and it ends in this method rather
  // than at each caller.** Two paths end a session today —
  // `sessionExpiryInterceptor` on a 401 and `SettingsService.leave()` — and a
  // third will be written by somebody thinking about sign-out rather than about
  // key material. Owned here, that third path clears the account's keys for
  // free. Owned by the callers, it does not, and the symptom is an ended
  // session whose content key is still sitting on the root injector for the
  // life of the tab, readable by anything with an injector — with nothing on
  // screen and nothing red to say so.
  //
  // Arranged from a session that was genuinely established rather than from
  // rest, for the same reason the test above is: the transition that matters is
  // the mid-visit one.
  it('drops the account keys when the session ends', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(of(ME));
    await service.probe();
    expect(touchedMembersOf(custody)).toEqual([]);

    // Act
    service.ended();

    // Assert
    expect(custody.lock).toHaveBeenCalledTimes(1);

    // And it locked rather than doing anything else with them. `unlock` here
    // would be this class asking for a factor nobody presented; `adopt` would
    // be it handing over keys it does not have and could not have.
    expect(touchedMembersOf(custody)).toEqual(['lock']);
  });

  // **The budget is dropped beside the keys, and for the same reason.** It is a
  // fact about the session that just ended, and a browser that kept it would
  // fold the previous occupant's tenancy into the first value the next one
  // writes — through the one door the server cannot see, since it holds no index
  // key and can never recompute a digest to check against.
  it('drops the budget when the session ends', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(of(ME));
    await service.probe();
    // The identifier really was there. Without this the assertion below holds
    // on a service that never publishes one.
    expect(service.budgetId()).toBe(BUDGET_ID);

    // Act
    service.ended();

    // Assert
    expect(service.budgetId()).toBeNull();
  });

  // The mirror of the transition above, and the half a reader will implement as
  // a re-probe. Arranged from `'anonymous'` reached by a real refusal, because
  // an implementation that only ever sets `'authenticated'` from `'unknown'`
  // would pass a test that started at rest.
  it('publishes an established session without waiting to be told again', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(401)));
    await service.probe();
    expect(service.status()).toBe('anonymous');
    // A read that never answers, so anything this method does with one cannot
    // be what publishes the status below.
    api.getSessionOwner.mockReturnValue(new Subject<MeDto>());

    // Act
    service.established();

    // Assert
    // **Synchronously, and that is the point rather than an incidental.** The
    // 201 that established the session set the cookie in the same breath, so
    // asking the server to restate the fact costs a round trip at the happiest
    // moment of the flow and has `'unreachable'` among its answers — a person
    // who just created an account shown a client that is not sure they exist.
    // The caller navigates on the line after this one, so the status has to be
    // true before any answer can arrive.
    expect(service.status()).toBe('authenticated');
  });

  // **The one thing that *is* asked for, and it is not the same shape of
  // question.** The status is a fact the establishing leg already stated; the
  // budget is a fact nothing in that answer carries and nothing in this browser
  // can derive, so reading it is not a guess replacing an answer — it is the
  // only source there is. Without it, every write on every content screen
  // answers `unreachable` for the rest of a session that began with a sign-in
  // rather than with a cold load, and nothing anywhere says why.
  it('reads the budget when a session is established', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(401)));
    await service.probe();
    expect(service.budgetId()).toBeNull();
    api.getSessionOwner.mockClear();
    api.getSessionOwner.mockReturnValue(of(ME));

    // Act
    service.established();
    // The read is not awaited by the method — the caller navigates on the next
    // line — so the microtask queue is what this case waits on instead.
    await Promise.resolve();

    // Assert
    expect(api.getSessionOwner).toHaveBeenCalledTimes(1);
    expect(service.budgetId()).toBe(BUDGET_ID);
  });

  // **The read may not move the status, in either direction.** It carries
  // `EXPECTS_UNAUTHENTICATED` for the reason `me-api.service.ts` writes out over
  // `getAccountKeys`: a 401 to it is a cookie that had not landed rather than a
  // session ending, and a failure that published `anonymous` or `unreachable`
  // here would navigate somebody off a screen that has just succeeded.
  it('keeps an established session when the budget read fails', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(401)));

    // Act
    service.established();
    await Promise.resolve();

    // Assert
    expect(service.status()).toBe('authenticated');
    expect(service.budgetId()).toBeNull();
  });

  // **The asymmetry is the point, and it is the half a reader will "finish".**
  // `ended()` locks; `established()` deliberately does nothing to custody, and
  // pairing them up — one method clears, so surely its mirror should too —
  // wipes exactly the keys that were just handed over.
  //
  // The timing is what makes it fatal rather than merely wasteful. Both
  // establishing paths call `established()` and hand keys over in the same
  // breath, one statement apart: registration adopts the pair it drew, and
  // sign-in unlocks under the key the authenticator derived. A `lock()` here
  // lands either immediately before that hand-over — bumping the generation, so
  // an unlock already in flight resolves into a world that has moved and drops
  // what it opened — or immediately after it, destroying the adopted pair
  // outright. Both leave a signed-in person locked out of their own content the
  // instant they were let into it, with nothing on screen saying why and a
  // factor to present all over again before anything is readable — and neither
  // reddens anything that exists without this test.
  it('keeps the account keys when a session is established', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => refusal(401)));
    await service.probe();
    expect(service.status()).toBe('anonymous');

    // Act
    service.established();

    // Assert
    expect(service.status()).toBe('authenticated');

    // Nothing whatever was said to custody. Written as the census rather than
    // as `expect(custody.lock).not.toHaveBeenCalled()`, because the rule is
    // that a session beginning says nothing about which factor opened it — the
    // two paths that know hand the keys over themselves — and that rule refuses
    // every member equally.
    expect(touchedMembersOf(custody)).toEqual([]);
  });

  // The same rule reached from the state that most tempts a reflex. A probe
  // that could not reach the server has learned nothing about the visitor, so
  // it may not act on their behalf either — and locking here would destroy both
  // keys over one blinked request and demand a full WebAuthn ceremony to get
  // them back. It is `auth.guard.ts`'s and `guest.guard.ts`'s refusal to bounce
  // anybody on `'unreachable'`, one layer down, where the cost is higher.
  it('does not drop the account keys because a probe never reached the server', async () => {
    // Arrange
    api.getSession.mockReturnValue(throwError(() => NETWORK_FAILURE));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('unreachable');
    expect(touchedMembersOf(custody)).toEqual([]);
  });
  // **The provider's tokens go the moment this tab learns it holds a session,
  // and this class is where it learns that.** Two arms publish
  // `'authenticated'` — the probe's answer and `established()` — and both
  // discard. Owned here rather than by the paths that establish a session, for
  // the reason `ended()` owns `custody.lock()`: a third establishing path will be
  // written by somebody thinking about sign-in rather than about the id token
  // sitting in `sessionStorage`, and put here it discards for free.
  describe("the provider's token", () => {
    // A tab that comes back to the product with a live session cookie and an id
    // token left over from a registration abandoned — or finished — in an
    // earlier visit. The cookie is what authenticates every request from here;
    // the bearer is a second credential nothing reads.
    it("discards the provider's token when the probe finds a session", async () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(of(ME));

      // Act
      await service.probe();

      // Assert
      expect(service.status()).toBe('authenticated');
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
    });

    // **The pin a reader will break by "discarding on every probe".** The
    // provider-return leg runs the probe *before* `auth.initialize()` reads the
    // answer off the URL, and the person on that leg holds no session yet.
    // Discarded here, the library's `nonce` goes with the tokens, the answer on
    // the URL no longer validates, and registration — the one act that creates
    // an account — cannot complete.
    it.each([
      { why: 'unauthenticated', status: 401 },
      { why: 'refused outright', status: 403 },
    ])(
      "keeps the provider's token when the probe finds no session ($why)",
      async ({ status }) => {
        // Arrange
        api.getSession.mockReturnValue(throwError(() => refusal(status)));

        // Act
        await service.probe();

        // Assert
        expect(service.status()).toBe('anonymous');
        expect(calledProviderMembersOf(provider)).toEqual([]);
      },
    );

    // The fourth state's rule, applied to the token. A read that never got an
    // answer has learned nothing about the visitor — which includes whether the
    // provider exchange on the URL is still theirs to finish.
    it.each([
      { why: 'a network failure', error: NETWORK_FAILURE },
      { why: 'a server error', error: refusal(500) },
      { why: 'a timeout', error: new TimeoutError() },
    ])(
      "keeps the provider's token when the probe never reached the server ($why)",
      async ({ error }) => {
        // Arrange
        api.getSession.mockReturnValue(throwError(() => error));

        // Act
        await service.probe();

        // Assert
        expect(service.status()).toBe('unreachable');
        expect(calledProviderMembersOf(provider)).toEqual([]);
      },
    );

    // The registration 201 and the sign-in 200 both come through here. Arranged
    // over a budget read that never answers, so a discard that waited on that
    // read — or rode inside it — is not what this sees.
    it("discards the provider's token when a session is established", () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(new Subject<MeDto>());

      // Act
      service.established();

      // Assert
      expect(service.status()).toBe('authenticated');
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
    });

    // **A discard, never a sign-out.** The argument-less `logOut()` is a
    // top-level navigation to the provider's end-session endpoint whenever the
    // library knows one — it would end the person's provider session on their
    // behalf and take them off the screen that just let them in — while
    // `logOut(true)` never navigates. The two read alike in a diff, so the rule is
    // stated as a census — exactly one member, by name — rather than as a spy on
    // the member that ought to be called.
    it.each([
      {
        arm: 'a session is established',
        act: (session: SessionService): Promise<void> => {
          session.established();

          return Promise.resolve();
        },
      },
      {
        arm: 'the probe finds a session',
        act: (session: SessionService): Promise<void> => session.probe(),
      },
    ])(
      'asks the provider service for nothing but the discard when $arm',
      async ({ act }) => {
        // Arrange
        api.getSessionOwner.mockReturnValue(of(ME));

        // Act
        await act(service);
        await Promise.resolve();

        // Assert
        expect(calledProviderMembersOf(provider)).toEqual([
          'forgetProviderToken',
        ]);
      },
    );

    // The discard is housekeeping on a credential nothing reads any more; the
    // session is the fact. A throw out of the library — `sessionStorage` refused
    // in a locked-down browser, a quota error — must not turn a verified sign-in
    // into `'anonymous'`, and out of the probe it must not become
    // `'unreachable'`: the server answered.
    it('a discard that throws does not unpublish an established session', () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(new Subject<MeDto>());
      provider.forgetProviderToken.mockImplementation(() => {
        throw new Error('sessionStorage is unavailable.');
      });

      // Act
      const act = (): void => {
        service.established();
      };

      // Assert
      expect(act).not.toThrow();
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
      expect(service.status()).toBe('authenticated');
    });

    it('a discard that throws does not unpublish the session the probe found', async () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(of(ME));
      provider.forgetProviderToken.mockImplementation(() => {
        throw new Error('sessionStorage is unavailable.');
      });

      // Act
      await service.probe();

      // Assert
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
      expect(service.status()).toBe('authenticated');
      expect(service.budgetId()).toBe(BUDGET_ID);
    });
  });

  // **The probe asks what kind of session first, and only a full one is asked
  // whose budget it is.** `GET /api/me` answers a locked session `403`, which
  // reads as no session at all, so asking it first would sign a locked tab out
  // on every reload. Sequential rather than parallel: the second question is
  // only worth asking once the first has said there is a full session to ask
  // it of.
  describe('the kind of session', () => {
    it('asks what kind of session before asking whose budget it is', async () => {
      // Arrange
      const kind = new Subject<SessionDto>();
      api.getSession.mockReturnValue(kind);

      // Act
      const probed = service.probe();
      await Promise.resolve();

      // Assert — nothing asked of the owner while the kind is unanswered.
      expect(api.getSession).toHaveBeenCalledOnce();
      expect(api.getSessionOwner).not.toHaveBeenCalled();

      kind.next(FULL_SESSION);
      kind.complete();
      await probed;

      expect(api.getSessionOwner).toHaveBeenCalledOnce();
      expect(service.status()).toBe('authenticated');
      expect(service.budgetId()).toBe(BUDGET_ID);
    });

    // AC1 on the client: a session established from a federated credential is
    // a session, and a different one. `'locked-session'` and never `'locked'`,
    // which the account-key status already spells.
    it('answers locked-session when the server says the session is locked', async () => {
      // Arrange
      api.getSession.mockReturnValue(of(LOCKED_SESSION));

      // Act
      await service.probe();

      // Assert
      expect(service.status()).toBe('locked-session');
    });

    // The owner read is a budget route, and a locked session is refused on
    // every one of those — so asking it would only fetch a `403`, and a
    // locked session has no budget to learn in any case.
    it('does not ask whose budget a locked session is', async () => {
      // Arrange
      api.getSession.mockReturnValue(of(LOCKED_SESSION));

      // Act
      await service.probe();

      // Assert
      expect(api.getSessionOwner).not.toHaveBeenCalled();
    });

    // Arranged from a full session that really did publish a budget: a
    // locked session reads no budget content of any kind (FR-113), and a
    // budget left standing from an earlier answer would be keyed into
    // whatever this tab writes next.
    it('holds no budget for a locked session', async () => {
      // Arrange
      await service.probe();
      expect(service.budgetId()).toBe(BUDGET_ID);
      api.getSession.mockReturnValue(of(LOCKED_SESSION));

      // Act
      await service.probe();

      // Assert
      expect(service.budgetId()).toBeNull();
    });

    // **Exactly `unreachable`**, and neither of the two readings a reader
    // reaches for. Read as `authenticated` it hands budget screens to a session
    // the server may refuse on every one; read as `locked-session` it sends a
    // full session to the release screen, whose one act is erasing the
    // account. A kind this bundle does not know is a statement about the
    // bundle, and a reload is the act that changes it.
    it.each([
      {
        why: 'a kind this bundle does not know',
        answer: (): Observable<SessionDto> =>
          of({ ...FULL_SESSION, kind: 'admin' } as unknown as SessionDto),
      },
      {
        why: 'a body the boundary refused',
        answer: (): Observable<SessionDto> =>
          throwError(
            () => new Error('The session response named no known kind.'),
          ),
      },
    ])('answers exactly unreachable for $why', async ({ answer }) => {
      // Arrange
      api.getSession.mockReturnValue(answer());

      // Act
      await service.probe();

      // Assert
      expect(service.status()).toBe('unreachable');
      expect(service.budgetId()).toBeNull();
      expect(api.getSessionOwner).not.toHaveBeenCalled();
    });

    // **A semantic change, and deliberate.** The session read already said
    // there is a full session; the owner read failing afterwards is a fact
    // about the budget, not about the visitor. Before the session route
    // existed the owner read *was* the probe and its failure read
    // `unreachable` — kept, that would sign a confirmed session out over a
    // second request blinking. Budget `null` is the honest reading: signed in,
    // tenancy unknown, every write refused with a word whose remedy is a
    // reload.
    it.each([
      { why: 'unauthenticated', error: refusal(401) },
      { why: 'refused outright', error: refusal(403) },
      { why: 'a server error', error: refusal(500) },
      { why: 'a network failure', error: NETWORK_FAILURE },
      { why: 'a timeout', error: new TimeoutError() },
    ])(
      'stays authenticated with no budget when the owner read fails ($why)',
      async ({ error }) => {
        // Arrange
        api.getSessionOwner.mockReturnValue(throwError(() => error));

        // Act
        await service.probe();

        // Assert
        expect(service.status()).toBe('authenticated');
        expect(service.budgetId()).toBeNull();
      },
    );

    it('resolves rather than rejecting when the owner read fails', async () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

      // Act & Assert
      await expect(service.probe()).resolves.toBeUndefined();
    });
  });

  // The account's scheduled erasure, as this tab last learned it. Three values
  // and never two: `'unread'` is "nobody has said", `null` is "the server said
  // nothing is scheduled". Collapsing them claims an answer out of a failure to
  // ask — the release screen would offer the commit as if nothing were on file.
  describe('the scheduled erasure', () => {
    it('is unread before anything has been asked', () => {
      // Assert
      expect(service.scheduledErasure()).toBe('unread');
    });

    it('holds nothing scheduled when the session read says so', async () => {
      // Arrange
      api.getSession.mockReturnValue(of(FULL_SESSION));

      // Act
      await service.probe();

      // Assert
      expect(service.scheduledErasure()).toBeNull();
    });

    it('holds the instant the session read named', async () => {
      // Arrange
      api.getSession.mockReturnValue(
        of({ ...LOCKED_SESSION, erasure: SCHEDULED }),
      );

      // Act
      await service.probe();

      // Assert
      expect(service.scheduledErasure()).toEqual({
        takesEffectAtUtc: TAKES_EFFECT_AT_UTC,
      });
    });

    // A probe that learned nothing about the session learned nothing about
    // its schedule either. `null` here would claim the server said nothing is
    // on file.
    it.each([
      { why: 'refused', error: refusal(401) },
      { why: 'never reached the server', error: NETWORK_FAILURE },
    ])('stays unread when the session read is $why', async ({ error }) => {
      // Arrange
      api.getSession.mockReturnValue(throwError(() => error));

      // Act
      await service.probe();

      // Assert
      expect(service.scheduledErasure()).toBe('unread');
    });

    // Arranged from a schedule that really was read: the value is a fact about
    // the session that just ended, and the next occupant of this tab is owed
    // a fresh read rather than the last one's date.
    it('is unread again once the session ends', async () => {
      // Arrange
      api.getSession.mockReturnValue(
        of({ ...LOCKED_SESSION, erasure: SCHEDULED }),
      );
      await service.probe();
      expect(service.scheduledErasure()).not.toBe('unread');

      // Act
      service.ended();

      // Assert
      expect(service.scheduledErasure()).toBe('unread');
    });

    // The schedule request's `200` names the instant the server stored, and
    // the release screen renders it from here.
    it('holds the instant a schedule request answered', async () => {
      // Arrange
      api.getSession.mockReturnValue(of(LOCKED_SESSION));
      await service.probe();
      expect(service.scheduledErasure()).toBeNull();

      // Act
      service.erasureScheduled(TAKES_EFFECT_AT_UTC);

      // Assert
      expect(service.scheduledErasure()).toEqual({
        takesEffectAtUtc: TAKES_EFFECT_AT_UTC,
      });
    });
  });

  // The mirror of `established()` for the one establishing leg that opens a
  // locked session: `POST /api/locked-session` answering `200`. A set rather
  // than a re-probe, for `established()`'s reason — the server has just said
  // what it thinks.
  describe('establishedLocked', () => {
    it('publishes a locked session without waiting to be told again', async () => {
      // Arrange
      api.getSession.mockReturnValue(throwError(() => refusal(401)));
      await service.probe();
      expect(service.status()).toBe('anonymous');

      // Act
      service.establishedLocked(LOCKED_SESSION);

      // Assert
      expect(service.status()).toBe('locked-session');
    });

    // The cookie the locked sign-in set **replaces** whatever this tab held.
    // Arranged from a full session holding a budget, because that is the case
    // the replacement makes real: a budget left standing would be keyed into
    // a write the locked session may not make, and keys left in custody would
    // be readable from the root injector by a session that reads no budget
    // content of any kind.
    it('drops the budget and the account keys a full session held', async () => {
      // Arrange
      await service.probe();
      expect(service.status()).toBe('authenticated');
      expect(service.budgetId()).toBe(BUDGET_ID);
      expect(touchedMembersOf(custody)).toEqual([]);

      // Act
      service.establishedLocked(LOCKED_SESSION);

      // Assert
      expect(service.budgetId()).toBeNull();
      expect(touchedMembersOf(custody)).toEqual(['lock']);
      expect(custody.lock).toHaveBeenCalledOnce();
    });

    // C10: the sign-in's `200` carries `erasure`, so the answer is the
    // schedule and nothing is asked afterwards.
    it.each([
      { why: 'nothing scheduled', erasure: null, expected: null },
      {
        why: 'an erasure on file',
        erasure: SCHEDULED,
        expected: { takesEffectAtUtc: TAKES_EFFECT_AT_UTC },
      },
    ])(
      'holds the schedule the answer carried ($why) and asks nothing more',
      async ({ erasure, expected }) => {
        // Act
        service.establishedLocked({ ...LOCKED_SESSION, erasure });
        await Promise.resolve();

        // Assert
        expect(service.scheduledErasure()).toEqual(expected);
        expect(api.getSession).not.toHaveBeenCalled();
        expect(api.getSessionOwner).not.toHaveBeenCalled();
      },
    );
  });

  // **A session beginning discards the provider's tokens, and the locked arms
  // are session beginnings.** A locked session is opened by a Google ID token,
  // and once the cookie stands nothing in this tab reads that token again.
  // Never on `anonymous` or `unreachable`: a provider-return leg can probe
  // before the answer is read, and a discard there takes the nonce with it.
  describe("the provider's token on a locked session", () => {
    it("discards the provider's token when a locked session is established", () => {
      // Act
      service.establishedLocked(LOCKED_SESSION);

      // Assert
      expect(calledProviderMembersOf(provider)).toEqual([
        'forgetProviderToken',
      ]);
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
    });

    it("discards the provider's token when the probe finds a locked session", async () => {
      // Arrange
      api.getSession.mockReturnValue(of(LOCKED_SESSION));

      // Act
      await service.probe();

      // Assert
      expect(calledProviderMembersOf(provider)).toEqual([
        'forgetProviderToken',
      ]);
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
    });

    // The session read said there is a full session; the owner read failing
    // after it does not make that less true, so the discard still runs.
    it("discards the provider's token for a full session whose owner read failed", async () => {
      // Arrange
      api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

      // Act
      await service.probe();

      // Assert
      expect(service.status()).toBe('authenticated');
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
    });

    it("keeps the provider's token when the session read finds no session", async () => {
      // Arrange
      api.getSession.mockReturnValue(throwError(() => refusal(401)));

      // Act
      await service.probe();

      // Assert
      expect(service.status()).toBe('anonymous');
      expect(calledProviderMembersOf(provider)).toEqual([]);
    });

    it('a discard that throws does not unpublish an established locked session', () => {
      // Arrange
      provider.forgetProviderToken.mockImplementation(() => {
        throw new Error('sessionStorage is unavailable.');
      });

      // Act
      const act = (): void => {
        service.establishedLocked(LOCKED_SESSION);
      };

      // Assert
      expect(act).not.toThrow();
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
      expect(service.status()).toBe('locked-session');
    });

    it('a discard that throws does not unpublish the locked session the probe found', async () => {
      // Arrange
      api.getSession.mockReturnValue(of(LOCKED_SESSION));
      provider.forgetProviderToken.mockImplementation(() => {
        throw new Error('sessionStorage is unavailable.');
      });

      // Act
      await service.probe();

      // Assert
      expect(provider.forgetProviderToken).toHaveBeenCalledOnce();
      expect(service.status()).toBe('locked-session');
    });
  });
});

// The same probe over the real `MeApiService` and `HttpClient`, with only the
// backend swapped. The stubbed cases above can say that `getSessionOwner()` was
// not *called*; only the wire can say that `GET /api/me` was not *sent* — by
// any member, under any name.
describe('SessionService on the wire', () => {
  const SESSION_URL = 'https://api.test/api/me/session';
  const ME_URL = 'https://api.test/api/me';

  let http: HttpTestingController;
  let service: SessionService;

  // A macrotask, so the probe's next request has been made before a case
  // looks for it.
  function afterPendingWork(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: 'https://api.test' }) },
        },
        { provide: AuthService, useValue: providerStub() },
        { provide: AccountKeyCustodyService, useValue: new CustodyStub() },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SessionService);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('sends no GET /api/me for a locked session', async () => {
    // Arrange
    const probed = service.probe();
    await afterPendingWork();

    // Act
    http.expectOne(SESSION_URL).flush(LOCKED_SESSION);
    await probed;
    await afterPendingWork();

    // Assert
    http.expectNone(ME_URL);
    expect(service.status()).toBe('locked-session');
  });

  // The control: without it, the case above passes against a probe that never
  // asks `GET /api/me` for anybody — and every blind index written after a
  // cold load would be keyed without a budget.
  it('sends GET /api/me after the session read for a full session', async () => {
    // Arrange
    const probed = service.probe();
    await afterPendingWork();

    // Act
    http.expectOne(SESSION_URL).flush(FULL_SESSION);
    await afterPendingWork();
    http.expectOne(ME_URL).flush(ME);
    await probed;

    // Assert
    expect(service.status()).toBe('authenticated');
    expect(service.budgetId()).toBe(BUDGET_ID);
  });
});
