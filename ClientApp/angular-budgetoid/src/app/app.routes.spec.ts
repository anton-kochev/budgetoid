import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { describe, expect, it } from 'vitest';
import { routes } from './app.routes';

// No `<router-outlet>` is rendered here, so navigation resolves and guards run but no
// routed component is instantiated — nothing reaches the network.
//
// The status is stubbed rather than the identity provider: both guards read the
// one answer `SessionService` holds, and the probe that fills it is an
// `APP_INITIALIZER` this module does not register. The two statuses below are
// the two these routes discriminate on; the other two admit everywhere and are
// pinned in each guard's own spec.
function routerFor(status: SessionStatus): Router {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      provideLocationMocks(),
      {
        provide: SessionService,
        useValue: { status: signal(status).asReadonly() },
      },
    ],
  });

  return TestBed.inject(Router);
}

describe('app routes', () => {
  it('lands a signed-in visitor on the transactions screen', async () => {
    // Arrange
    const router = routerFor('authenticated');

    // Act
    await router.navigateByUrl('/app');

    // Assert
    expect(router.url).toBe('/app/transactions');
  });

  // The removed path matches nothing, so it falls through to the catch-all redirect to
  // /welcome, where guestGuard sends a signed-in visitor back into the app.
  it('no longer resolves the removed home route', async () => {
    // Arrange
    const router = routerFor('authenticated');

    // Act
    await router.navigateByUrl('/app/home');

    // Assert
    expect(router.url).toBe('/app/transactions');
  });

  it('sends an anonymous visitor to the welcome screen', async () => {
    // Arrange
    const router = routerFor('anonymous');

    // Act
    await router.navigateByUrl('/app');

    // Assert
    expect(router.url).toBe('/welcome');
  });

  // NFR-021 counts the address bar too: the settings screen has to be reachable
  // by one navigation, not by landing somewhere else and drilling in.
  it('reaches the settings screen in one navigation', async () => {
    // Arrange
    const router = routerFor('authenticated');

    // Act
    await router.navigateByUrl('/app/settings');

    // Assert
    expect(router.url).toBe('/app/settings');
  });

  it('sends an anonymous visitor from settings to the welcome screen', async () => {
    // Arrange
    const router = routerFor('anonymous');

    // Act
    await router.navigateByUrl('/app/settings');

    // Assert
    // Control for the test above: a settings route registered without
    // `canActivate: [authGuard]` passes "reaches the settings screen"
    // perfectly, and hands the account, export and erasure controls to anyone
    // who types the URL.
    expect(router.url).toBe('/welcome');
  });

  it('keeps a signed-in visitor off the registration screen', async () => {
    // Arrange
    // The control, and without it this test is green on an application that has
    // no registration screen at all: `/register` matching nothing falls through
    // to `**` → `/welcome`, where `guestGuard` sends this same visitor to this
    // same address for a completely different reason. Read as a route-table
    // statement rather than as a second navigation, because `routerFor`
    // instantiates the testing module and a test cannot configure two.
    const router = routerFor('authenticated');

    // Act
    await router.navigateByUrl('/register');

    // Assert
    expect(routes.map((route) => route.path)).toContain('register');
    // Somebody holding a session has an account, so the registration flow has
    // nothing to offer them — and it would spend a challenge and a passkey
    // finding that out. `/app` resolves on to the transactions screen, which is
    // where `guestGuard`'s redirect lands.
    expect(router.url).toBe('/app/transactions');
  });

  // A pin rather than a discovery: it is green the moment the register route
  // exists, and it exists to fail on the edit that would follow. The mutation
  // it refuses is a `children: [{ path: 'codes', … }]` array on that route —
  // the obvious way to give each step an address. Two things break at once. A
  // step is in-memory state, so Back lands on `/register/passkey` with the
  // service that held the ceremony's result already gone; and `/register/codes`
  // becomes a link somebody can open, or be sent, on a screen whose entire
  // premise is that ten codes were minted moments ago and are on it.
  it('does not resolve a step as a route of its own', async () => {
    // Arrange
    const router = routerFor('anonymous');

    // Act
    await router.navigateByUrl('/register/codes');

    // Assert
    expect(router.url).toBe('/welcome');
  });
});
