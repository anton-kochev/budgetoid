import { TestBed } from '@angular/core/testing';
import { OAuthEvent, OAuthService } from 'angular-oauth2-oidc';
import { firstValueFrom, Subject } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { AuthService } from './auth-service';
import { ConfigurationService } from './configuration.service';

// FR-086: no image supplied by the identity provider is displayed. The boundary that
// enforces it is here, where the claims are mapped: an image the app never holds is an
// image no later component can render, and no request to the provider on every paint.
describe('AuthService', () => {
  it('drops the identity provider picture claim', async () => {
    // Arrange
    const events = new Subject<OAuthEvent>();
    const oAuth = {
      events,
      hasValidAccessToken: () => true,
      hasValidIdToken: () => true,
      getIdentityClaims: () => ({
        email: 'someone@example.com',
        name: 'Someone',
        picture: 'https://lh3.googleusercontent.com/a/photo',
      }),
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        { provide: ConfigurationService, useValue: {} },
      ],
    });
    const profile = firstValueFrom(TestBed.inject(AuthService).userProfile$);

    // Act
    events.next({ type: 'token_received' });

    // Assert
    expect(await profile).toEqual({
      email: 'someone@example.com',
      name: 'Someone',
    });
  });
});
