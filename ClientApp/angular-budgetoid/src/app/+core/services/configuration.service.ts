import { HttpBackend, HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, firstValueFrom, map, tap } from 'rxjs';

interface Configuration {
  apiBaseUrl: string;
  auth: Partial<{ google: GoogleAuthConfig }>;
}

interface GoogleAuthConfig {
  clientId: string;
  redirectUri: string;
  // Where the email change's trip to the provider comes back. Optional, and
  // not required by `validateConfiguration`: a deployment without it has no
  // email change, and registration must not fail to boot over it.
  emailChangeRedirectUri?: string;
  // Where the locked sign-in's trip to the provider comes back — the release
  // screen. Optional for `emailChangeRedirectUri`'s reason: a deployment
  // without it has no locked sign-in, and nothing else may fail to boot over
  // it.
  lockedSignInRedirectUri?: string;
  scope: string;
}

@Injectable()
export class ConfigurationService {
  private config: Configuration = { apiBaseUrl: '', auth: {} };
  private readonly httpClient: HttpClient = new HttpClient(inject(HttpBackend));

  public getConfig(): Configuration {
    return this.config;
  }

  public load(): Promise<boolean> {
    // Prefer the gitignored dev override (localhost URLs); when it is absent
    // (e.g. a production build where the local file 404s), fall back to the
    // committed production config.
    return firstValueFrom(
      this.httpClient.get<Configuration>('./assets/app-config.local.json').pipe(
        catchError(() =>
          this.httpClient.get<Configuration>('./assets/app-config.json'),
        ),
        tap((config) => this.setConfig(config)),
        map(() => true),
      ),
    );
  }

  private validateConfiguration(config: unknown): void {
    // Check and throw an error if the auth object lacks the required keys.
    // There is no type-safe between json and interface describes its structure,
    // so we have to check it manually for typos and mistakes

    const { apiBaseUrl, auth } = config as Configuration;

    if (apiBaseUrl == null) {
      throw new Error('Configuration is missing "apiBaseUrl" field');
    }

    if (auth == null) {
      throw new Error('Configuration is missing "auth" field"');
    }

    if (auth?.google) {
      const { clientId, redirectUri, scope } = auth.google;

      if (clientId == null || redirectUri == null || scope == null) {
        throw new Error(
          'Google auth configuration is required to have "clientId", "redirectUri" and "scope" fields',
        );
      }
    }
  }

  private setConfig(config: unknown): void {
    // Check and throw an error if config.json lacks the required keys.
    // There is no type-safe between json and interface describes its structure,
    // so we have to check it manually for typos and mistakes
    this.validateConfiguration(config);

    const { apiBaseUrl, auth } = config as Configuration;

    this.config = {
      apiBaseUrl,
      auth,
    };
  }
}
