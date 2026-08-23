import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MeApiService, type MeDto } from '@app-core/api/me-api.service';
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

describe('SessionService', () => {
  let service: SessionService;
  let api: MeApiStub;

  beforeEach(() => {
    api = new MeApiStub();
    TestBed.configureTestingModule({
      providers: [{ provide: MeApiService, useValue: api }],
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
});
