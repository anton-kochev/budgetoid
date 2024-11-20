import { inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { filter, map, Observable } from 'rxjs';
import { ConfigurationService } from './configuration.service';

interface Profile {
  email: string;
  name: string;
  picture: string;
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);

  public readonly userProfile$: Observable<Profile>;

  constructor() {
    const { auth } = this.config.getConfig();

    this.oAuth.configure({
      clientId: auth.google?.clientId,
      issuer: 'https://accounts.google.com',
      redirectUri: auth.google?.redirectUri,
      strictDiscoveryDocumentValidation: false,
      scope: auth.google?.scope,
      // showDebugInformation: true,
    });
    this.oAuth.loadDiscoveryDocumentAndTryLogin();
    this.oAuth.setupAutomaticSilentRefresh();

    // This observable will emit the user profile information
    // when the user is authenticated.
    this.userProfile$ = this.oAuth.events.pipe(
      filter(
        e =>
          (e.type === 'discovery_document_loaded' ||
            e.type === 'token_received') &&
          this.oAuth.hasValidAccessToken() &&
          this.oAuth.hasValidIdToken(),
      ),
      map(() => this.oAuth.getIdentityClaims()),
      map(claims => ({
        email: claims['email'],
        name: claims['name'],
        picture: claims['picture'],
      })),
    );
  }

  public isAuthenticated(): boolean {
    return this.oAuth.hasValidAccessToken() && this.oAuth.hasValidIdToken();
  }

  public signIn(): void {
    this.oAuth.initLoginFlow();
  }

  public signOut(): void {
    this.oAuth.logOut();
  }
}
