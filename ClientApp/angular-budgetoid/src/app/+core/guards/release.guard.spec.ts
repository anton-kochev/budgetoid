import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { releaseGuard } from './release.guard';

interface GuardRun {
  readonly result: boolean | UrlTree;
  readonly redirect: UrlTree;
  readonly redirectedTo: string | null;
}

// The harness `auth.guard.spec.ts` and `guest.guard.spec.ts` use, for their
// reason: all three guards read the one answer `SessionService` holds, which is
// what keeps them from disagreeing about the same visitor and bouncing them
// between two screens.
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
    releaseGuard({} as never, {} as never),
  ) as boolean | UrlTree;
  const [call] = parseUrl.mock.calls;

  return {
    result,
    redirect,
    redirectedTo: call === undefined ? null : call[0],
  };
}

describe('releaseGuard', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // The one status this guard turns away. A full session can open its
  // account, so the screen whose one act is erasing an account nobody can open
  // has nothing for it — and offering that act to somebody who could simply
  // sign in is the mistake the screen's first paragraph exists to prevent.
  it('sends a full session to the app', () => {
    // Arrange & Act
    const run = runGuard('authenticated');

    // Assert
    expect(run.result).toBe(run.redirect);
    expect(run.redirectedTo).toBe('/app');
  });

  // The screen's own session. Turned away from here, it would have nowhere to
  // go: `authGuard` and `guestGuard` both send it back, which is a loop.
  it('admits a locked session', () => {
    // Arrange & Act
    const run = runGuard('locked-session');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  // Somebody signed out reaches this screen from Welcome's link, before the
  // trip to Google.
  it('admits a visitor the server refused', () => {
    // Arrange & Act
    const run = runGuard('anonymous');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  // Never bounced, on `auth.guard.ts`'s argument: a server that could not be
  // reached has said nothing about who the visitor is, so it may move nobody.
  it('admits a visitor when the server could not be reached', () => {
    // Arrange & Act
    const run = runGuard('unreachable');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  it('admits a visitor before the probe has answered', () => {
    // Arrange & Act
    const run = runGuard('unknown');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });
});
