import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { guestGuard } from './guest.guard';

interface GuardRun {
  readonly result: boolean | UrlTree;
  readonly redirect: UrlTree;
  readonly redirectedTo: string | null;
}

// The mirror of `auth.guard.spec.ts`, down to the harness: both guards now read
// one answer computed once by `SessionService`, which is what keeps them from
// disagreeing about the same visitor and bouncing them between two screens.
function runGuard(status: SessionStatus): GuardRun {
  TestBed.resetTestingModule();

  const redirect = {} as UrlTree;
  const parseUrl = vi.fn((url: string): UrlTree => redirect);
  const session: Pick<SessionService, 'status'> = {
    status: signal(status).asReadonly(),
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: SessionService, useValue: session },
      { provide: Router, useValue: { parseUrl } },
    ],
  });

  const result = TestBed.runInInjectionContext(() =>
    guestGuard({} as never, {} as never),
  ) as boolean | UrlTree;
  const [call] = parseUrl.mock.calls;

  return {
    result,
    redirect,
    redirectedTo: call === undefined ? null : call[0],
  };
}

describe('guestGuard', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('allows activation for an anonymous user', () => {
    // Arrange & Act
    const run = runGuard('anonymous');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  it('redirects an authenticated user to /app', () => {
    // Arrange & Act
    const run = runGuard('authenticated');

    // Assert
    expect(run.result).toBe(run.redirect);
    expect(run.redirectedTo).toBe('/app');
  });

  // A visitor whose probe never got an answer is shown the welcome screen they
  // asked for. The alternative is worse than it looks: this guard's redirect
  // sends them to `/app`, where `authGuard` reads the same `unreachable` and
  // admits them — so a guard that redirected here would hand somebody who may
  // well be signed out a screen that can load nothing.
  it('allows activation when the server could not be reached', () => {
    // Arrange & Act
    const run = runGuard('unreachable');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  // Unobservable in the shipped app, because the initializer resolves the probe
  // before the first activation. Admitted anyway, for the same reason its twin
  // in `auth.guard.spec.ts` is: the guard must not depend on the initializer
  // being there.
  it('allows activation before the probe has answered', () => {
    // Arrange & Act
    const run = runGuard('unknown');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });
});
