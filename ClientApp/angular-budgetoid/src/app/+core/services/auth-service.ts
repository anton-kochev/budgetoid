import { inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { authConfig } from 'assets/auth-config';

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly oAuth = inject(OAuthService);

  constructor() {
    this.oAuth.configure(authConfig);
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
