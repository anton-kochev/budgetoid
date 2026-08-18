import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authGuard } from './auth.guard';

interface GuardRun {
  readonly result: boolean | UrlTree;
  // The tree `Router.parseUrl` was made to hand back, so a redirect can be
  // asserted by identity rather than by shape.
  readonly redirect: UrlTree;
  readonly redirectedTo: string | null;
}

// The guard reads `SessionService`, never `AuthService`: the identity provider
// knows nothing about a first-party session cookie, and after sign-in leaves it
// there is no token in the browser for `isAuthenticated()` to look at.
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
    authGuard({} as never, {} as never),
  ) as boolean | UrlTree;
  const [call] = parseUrl.mock.calls;

  return {
    result,
    redirect,
    redirectedTo: call === undefined ? null : call[0],
  };
}

describe('authGuard', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('admits a visitor the server recognises', () => {
    // Arrange & Act
    const run = runGuard('authenticated');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  // The only status that is evidence the visitor is not signed in, and so the
  // only one that may bounce them. Control for the three tests below: a guard
  // that admitted everything would pass all of them and hand the app's screens
  // to anybody who types the URL.
  it('sends a visitor the server refused to the welcome screen', () => {
    // Arrange & Act
    const run = runGuard('anonymous');

    // Assert
    expect(run.result).toBe(run.redirect);
    expect(run.redirectedTo).toBe('/welcome');
  });

  // The defect this whole four-valued shape exists to prevent, seen from the
  // screen it happens on. A server that could not be reached has said nothing
  // about who the visitor is; treated as a refusal, one blinked request during
  // the cold load throws a signed-in person out of their own account and onto a
  // marketing page. Nothing they can do from there fixes it, because the page
  // they land on is behind the same server.
  it('keeps a visitor in the app when the server could not be reached', () => {
    // Arrange & Act
    const run = runGuard('unreachable');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });

  // The initializer resolves the probe before the first route activates, so a
  // guard should never see this. It is admitted anyway, on the same argument as
  // `unreachable` and because a guard that redirected here would be one deleted
  // initializer away from bouncing every visitor on every cold load. The
  // redundancy is deliberate, not dead.
  it('keeps a visitor in the app before the probe has answered', () => {
    // Arrange & Act
    const run = runGuard('unknown');

    // Assert
    expect(run.result).toBe(true);
    expect(run.redirectedTo).toBeNull();
  });
});
