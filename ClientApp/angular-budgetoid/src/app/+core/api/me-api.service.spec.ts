import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MeApiService, type MeDto } from './me-api.service';

describe('MeApiService', () => {
  let http: HttpTestingController;
  let api: MeApiService;

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
    api = TestBed.inject(MeApiService);
  });

  afterEach(() => http.verify());

  it('requests the export as bytes rather than parsed JSON', () => {
    // Act
    api.getExport().subscribe();
    const request = http.expectOne('https://api.test/api/me/export');

    // Assert
    expect(request.request.method).toBe('GET');
    // The load-bearing assertion of this file. Export amounts ship as JSON
    // numbers at numeric(14,4) scale, and a JSON responseType hands them to
    // JSON.parse, whose IEEE-754 doubles do not cover that range — the file
    // would be silently degraded on its way to disk. Without this line,
    // `getExport(): Observable<Blob> { return this.get<Blob>(...); }` — a
    // parsed body merely *typed* as a Blob — passes the method-and-URL check
    // above. See docs/business-logic/export.md.
    expect(request.request.responseType).toBe('blob');

    request.flush(new Blob(['{}'], { type: 'application/json' }));
  });

  it('sends no Content-Type on the export request', () => {
    // Act
    api.getExport().subscribe();
    const request = http.expectOne('https://api.test/api/me/export');

    // Assert
    // Control for the responseType pin above. BaseApiService hard-codes
    // `Content-Type: application/json` on every request it makes, including
    // bodyless GETs, so this header is the fingerprint of the JSON path: an
    // implementation that routed through `get<T>()` and only *declared*
    // Observable<Blob> is caught here even if a future refactor loosens the
    // responseType assertion. A bodyless GET has no content to type.
    expect(request.request.headers.get('Content-Type')).toBeNull();

    request.flush(new Blob(['{}'], { type: 'application/json' }));
  });

  it('requests the account record as parsed JSON', () => {
    // Arrange
    let received: MeDto | undefined;

    // Act
    api.getMe().subscribe((value) => (received = value));
    const request = http.expectOne('https://api.test/api/me');

    // Assert
    expect(request.request.method).toBe('GET');
    // The sibling of the blob pin, and the half that makes it mean something:
    // a service that set `responseType: 'blob'` on *every* request would
    // satisfy the export test perfectly. Only the pair proves the response
    // type is a per-call decision rather than a service-wide setting.
    expect(request.request.responseType).toBe('json');

    request.flush({ email: 'owner@budgetoid.test' });
    expect(received).toEqual({ email: 'owner@budgetoid.test' });
  });
});
