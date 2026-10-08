import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PayeesApiService } from './payees-api.service';

// The rename a key rotation makes when two payees share one name.
describe('PayeesApiService', () => {
  let http: HttpTestingController;
  let payees: PayeesApiService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: 'https://api.test' }) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    payees = TestBed.inject(PayeesApiService);
  });

  afterEach(() => {
    http.verify();
  });

  it('renamePayee sends a PATCH to the payee carrying exactly the name and its key', () => {
    // Arrange
    const request = { name: 'c2VhbGVk', nameKey: 'a2V5ZWQ' };

    // Act
    payees.renamePayee('payee-1', request).subscribe();
    const sent = http.expectOne('https://api.test/api/payees/payee-1');

    // Assert
    expect(sent.request.method).toBe('PATCH');
    // Two members and no third: the route binds strictly, and an `id` in the
    // body would be a second place the row is named.
    expect(sent.request.body).toEqual({
      name: 'c2VhbGVk',
      nameKey: 'a2V5ZWQ',
    });
    sent.flush(null);
  });
});
