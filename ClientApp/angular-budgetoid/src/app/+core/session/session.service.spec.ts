import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MeApiService, type MeDto } from '@app-core/api/me-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionService } from './session.service';

// The session cookie is `HttpOnly`, so script cannot read it and a cold load
// has no local evidence at all about who the visitor is. Asking the server is
// the only way to find out, which makes every one of these tests a test about
// how an *answer that did not arrive* is read.
const ME: MeDto = { email: 'owner@budgetoid.test' };

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
class MeApiStub {
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

describe('SessionService', () => {
  let service: SessionService;
  let api: MeApiStub;
  let custody: CustodyStub;

  beforeEach(() => {
    api = new MeApiStub();
    custody = new CustodyStub();
    TestBed.configureTestingModule({
      providers: [
        { provide: MeApiService, useValue: api },
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

  // The state every guard on the site reads before a route activates, and the
  // one the initializer exists to make unobservable. Asserted at rest *and*
  // while the read is in flight: an implementation that seeded `'anonymous'`
  // and only moved off it on an answer would pass a rest-only assertion while
  // bouncing every visitor whose network is slow.
  it('says nothing about the visitor until an answer arrives', () => {
    // Arrange
    const pending = new Subject<MeDto>();
    api.getSessionOwner.mockReturnValue(pending);

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

  // 401 is the server saying it knows who is asking and the answer is nobody.
  // That is evidence, and it is the only kind this class treats as evidence.
  it('answers anonymous when the server refuses the read as unauthenticated', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(401)));

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
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(403)));

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
    api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

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
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(500)));

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
    api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

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

  // The mirror of the transition above, and the half a reader will implement as
  // a re-probe. Arranged from `'anonymous'` reached by a real refusal, because
  // an implementation that only ever sets `'authenticated'` from `'unknown'`
  // would pass a test that started at rest.
  it('publishes an established session without asking again', async () => {
    // Arrange
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(401)));
    await service.probe();
    expect(service.status()).toBe('anonymous');
    api.getSessionOwner.mockClear();

    // Act
    service.established();

    // Assert
    expect(service.status()).toBe('authenticated');
    // The no-request half is the point rather than an incidental. The 201 that
    // established the session set the cookie in the same breath, so a re-probe
    // asks the server to restate a fact it has just stated — at a round trip's
    // cost, at the happiest moment of the flow, and with `'unreachable'` among
    // its answers. A person who just created an account would then be shown a
    // client that is not sure they exist.
    expect(api.getSessionOwner).not.toHaveBeenCalled();
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
    api.getSessionOwner.mockReturnValue(throwError(() => refusal(401)));
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
    api.getSessionOwner.mockReturnValue(throwError(() => NETWORK_FAILURE));

    // Act
    await service.probe();

    // Assert
    expect(service.status()).toBe('unreachable');
    expect(touchedMembersOf(custody)).toEqual([]);
  });
});
