import { HttpBackend, HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom, map, tap } from 'rxjs';

interface Configuration {
  apiBaseUrl: string;
  auth: Partial<{ google: GoogleAuthConfig }>;
}

interface GoogleAuthConfig {
  clientId: string;
  redirectUri: string;
  scope: string;
}

@Injectable()
export class ConfigurationService {
  private config: Configuration;
  private readonly httpClient: HttpClient;

  constructor(handler: HttpBackend) {
    this.config = { apiBaseUrl: '', auth: {} };
    this.httpClient = new HttpClient(handler);
  }

  public getConfig(): Configuration {
    return this.config;
  }

  public load(): Promise<boolean> {
    return firstValueFrom(
      this.httpClient.get<Configuration>('./assets/app-config.local.json').pipe(
        tap(config => this.setConfig(config)),
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
