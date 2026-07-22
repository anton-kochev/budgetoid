import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';

import { AuthService } from '@app-core/services/auth-service';
import { guestGuard } from './guest.guard';

function runGuard(): boolean | UrlTree {
  return TestBed.runInInjectionContext(() =>
    guestGuard({} as never, {} as never),
  ) as boolean | UrlTree;
}

describe('guestGuard', () => {
  it('allows activation for an anonymous user', () => {
    // Arrange
    const authService = { isAuthenticated: () => false };
    const router = { parseUrl: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: authService },
        { provide: Router, useValue: router },
      ],
    });

    // Act
    const result = runGuard();

    // Assert
    expect(result).toBe(true);
    expect(router.parseUrl).not.toHaveBeenCalled();
  });

  it('redirects an authenticated user to /app', () => {
    // Arrange
    const appUrlTree = {} as UrlTree;
    const authService = { isAuthenticated: () => true };
    const router = { parseUrl: vi.fn().mockReturnValue(appUrlTree) };
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: authService },
        { provide: Router, useValue: router },
      ],
    });

    // Act
    const result = runGuard();

    // Assert
    expect(result).toBe(appUrlTree);
    expect(router.parseUrl).toHaveBeenCalledWith('/app');
  });
});
