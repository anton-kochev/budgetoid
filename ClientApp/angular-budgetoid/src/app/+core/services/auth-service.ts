import { inject, Injectable } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { filter, map, Observable } from 'rxjs';
import { ConfigurationService } from './configuration.service';

interface Profile {
  email: string;
  name: string;
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly config = inject(ConfigurationService);
  private readonly oAuth = inject(OAuthService);

  public readonly userProfile$: Observable<Profile>;

  constructor() {
    // This observable will emit the user profile information
    // when the user is authenticated.
    this.userProfile$ = this.oAuth.events.pipe(
      filter(
        (e) =>
          (e.type === 'discovery_document_loaded' ||
            e.type === 'token_received') &&
          this.oAuth.hasValidAccessToken() &&
          this.oAuth.hasValidIdToken(),
      ),
      map(() => this.oAuth.getIdentityClaims() as Record<string, unknown>),
      map((claims) => ({
        email: this.getStringClaim(claims, 'email'),
        name: this.getStringClaim(claims, 'name'),
      })),
    );
  }

  // Configure OAuth from the (now-loaded) app config, process any redirect-back
  // token, and start silent refresh. Driven by an APP_INITIALIZER after the
  // config has loaded — see core.providers.ts — so config values are present.
  public async initialize(): Promise<void> {
    const { auth } = this.config.getConfig();

    this.oAuth.configure({
      clientId: auth.google?.clientId,
      issuer: 'https://accounts.google.com',
      redirectUri: auth.google?.redirectUri,
      strictDiscoveryDocumentValidation: false,
      scope: auth.google?.scope,
    });
    await this.oAuth.loadDiscoveryDocumentAndTryLogin();
    this.oAuth.setupAutomaticSilentRefresh();
  }

  private getStringClaim(claims: Record<string, unknown>, key: string): string {
    const value = claims[key];

    return typeof value === 'string' ? value : '';
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
