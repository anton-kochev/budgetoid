import { inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { ConfigurationService } from './configuration.service';

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);

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
