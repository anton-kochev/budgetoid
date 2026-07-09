import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideHttpClient, withFetch } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ConfigurationService } from './configuration.service';

const LOCAL_CONFIG_URL = './assets/app-config.local.json';
const PROD_CONFIG_URL = './assets/app-config.json';

const validConfig = {
  apiBaseUrl: 'https://api.example.test',
  auth: {
    google: {
      clientId: 'client-id',
      redirectUri: 'https://app.example.test/callback',
      scope: 'openid profile email',
    },
  },
};

describe('ConfigurationService', () => {
  let service: ConfigurationService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ConfigurationService,
        provideHttpClient(withFetch()),
        provideHttpClientTesting(),
      ],
    });

    service = TestBed.inject(ConfigurationService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('loads the dev override, stores it, and resolves true', async () => {
    // Arrange
    const promise = service.load();

    // Act
    httpMock.expectOne(LOCAL_CONFIG_URL).flush(validConfig);

    // Assert
    await expect(promise).resolves.toBe(true);
    expect(service.getConfig()).toEqual(validConfig);
  });

  it('falls back to the production config when the local override 404s and resolves true', async () => {
    // Arrange
    const promise = service.load();

    // Act
    httpMock
      .expectOne(LOCAL_CONFIG_URL)
      .flush('Not Found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(PROD_CONFIG_URL).flush(validConfig);

    // Assert
    await expect(promise).resolves.toBe(true);
    expect(service.getConfig()).toEqual(validConfig);
  });

  it('rejects when the config is missing apiBaseUrl', async () => {
    // Arrange
    const promise = service.load();

    // Act
    httpMock.expectOne(LOCAL_CONFIG_URL).flush({ auth: {} });

    // Assert
    await expect(promise).rejects.toThrow('missing "apiBaseUrl"');
  });

  it('rejects when the config is missing auth', async () => {
    // Arrange
    const promise = service.load();

    // Act
    httpMock
      .expectOne(LOCAL_CONFIG_URL)
      .flush({ apiBaseUrl: 'https://api.example.test' });

    // Assert
    await expect(promise).rejects.toThrow('missing "auth"');
  });

  it('rejects when the auth.google block is incomplete', async () => {
    // Arrange
    const promise = service.load();

    // Act
    httpMock.expectOne(LOCAL_CONFIG_URL).flush({
      apiBaseUrl: 'https://api.example.test',
      auth: { google: { clientId: 'client-id' } },
    });

    // Assert
    await expect(promise).rejects.toThrow(
      'Google auth configuration is required',
    );
  });

  it('returns the empty default config before load is called', () => {
    // Arrange
    // (fresh service instance from beforeEach, load not called)

    // Act
    const config = service.getConfig();

    // Assert
    expect(config).toEqual({ apiBaseUrl: '', auth: {} });
  });
});
