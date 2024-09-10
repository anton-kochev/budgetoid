import { HttpBackend, HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom, map, tap } from 'rxjs';

interface Configuration {
  api: string;
}

@Injectable()
export class ConfigurationService {
  private config: Configuration;
  private readonly httpClient: HttpClient;
  private readonly required: (keyof Configuration)[];

  constructor(handler: HttpBackend) {
    this.config = { api: '' };
    this.required = ['api'];
    this.httpClient = new HttpClient(handler);
  }

  public getConfig(): Configuration {
    return this.config;
  }

  public load(): Promise<boolean> {
    return firstValueFrom(
      this.httpClient.get<Configuration>('./assets/app-config.json').pipe(
        tap(config => this.setConfig(config)),
        map(() => true),
      ),
    );
  }

  private validateConfiguration(config: unknown): void {
    const missing = Object.keys(config as Configuration).filter(
      attr => this.required.includes(attr as keyof Configuration) === false,
    );

    if (missing.length > 0) {
      throw new Error(
        `Configuration is missing required fields: ${missing.join(', ')}`,
      );
    }
  }

  private setConfig(config: unknown): void {
    // Check and throw an error if config.json lacks the required keys.
    // There is no type-safe between json and interface describes its structure,
    // so we have to check it manually for typos and mistakes
    this.validateConfiguration(config);

    const { api } = config as Configuration;

    this.config = {
      api,
    };
  }
}
