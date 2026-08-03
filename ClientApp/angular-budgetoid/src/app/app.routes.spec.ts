import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService } from '@app-core/services/auth-service';
import { describe, expect, it } from 'vitest';
import { routes } from './app.routes';

// No `<router-outlet>` is rendered here, so navigation resolves and guards run but no
// routed component is instantiated — nothing reaches the network.
function routerFor(isAuthenticated: boolean): Router {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      provideLocationMocks(),
      {
        provide: AuthService,
        useValue: { isAuthenticated: () => isAuthenticated },
      },
    ],
  });

  return TestBed.inject(Router);
}

describe('app routes', () => {
  it('lands a signed-in visitor on the transactions screen', async () => {
    // Arrange
    const router = routerFor(true);

    // Act
    await router.navigateByUrl('/app');

    // Assert
    expect(router.url).toBe('/app/transactions');
  });

  // The removed path matches nothing, so it falls through to the catch-all redirect to
  // /welcome, where guestGuard sends a signed-in visitor back into the app.
  it('no longer resolves the removed home route', async () => {
    // Arrange
    const router = routerFor(true);

    // Act
    await router.navigateByUrl('/app/home');

    // Assert
    expect(router.url).toBe('/app/transactions');
  });

  it('sends an anonymous visitor to the welcome screen', async () => {
    // Arrange
    const router = routerFor(false);

    // Act
    await router.navigateByUrl('/app');

    // Assert
    expect(router.url).toBe('/welcome');
  });
});
