import { TestBed } from '@angular/core/testing';
import { OAuthEvent, OAuthService } from 'angular-oauth2-oidc';
import { isObservable, Observable, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth-service';
import { ConfigurationService } from './configuration.service';

// FR-086: no image supplied by the identity provider is displayed. This pins something
// stronger than dropping the picture claim — the app reads no ID-token claim at all. A
// claim the app never holds is a claim no component can render, and reading none of them
// is what makes asking Google for only `openid email` safe to keep: a claim nobody
// consumes is a scope nobody needs.
//
// The check subscribes to every observable AuthService exposes, because a claim read
// inside a cold observable stays invisible until something subscribes.
function exposedObservables(service: AuthService): Observable<unknown>[] {
  const members = service as unknown as Record<string, unknown>;

  return Object.keys(members)
    .map((key) => members[key])
    .filter(isObservable);
}

describe('AuthService', () => {
  it('reads no claim from the ID token', () => {
    // Arrange
    const events = new Subject<OAuthEvent>();
    const getIdentityClaims = vi.fn(() => ({
      email: 'someone@example.com',
      name: 'Someone',
      picture: 'https://lh3.googleusercontent.com/a/photo',
    }));
    const oAuth = {
      events,
      getIdentityClaims,
      hasValidAccessToken: () => true,
      hasValidIdToken: () => true,
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        { provide: ConfigurationService, useValue: {} },
      ],
    });
    const service = TestBed.inject(AuthService);
    const subscriptions = exposedObservables(service).map((observable) =>
      observable.subscribe(),
    );

    // Act
    events.next({ type: 'discovery_document_loaded' });
    events.next({ type: 'token_received' });
    subscriptions.forEach((subscription) => {
      subscription.unsubscribe();
    });

    // Assert
    expect(getIdentityClaims).not.toHaveBeenCalled();
  });
});
