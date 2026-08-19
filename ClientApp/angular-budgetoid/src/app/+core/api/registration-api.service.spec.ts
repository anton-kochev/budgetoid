// Two things about these two requests that nothing else in the client can
// hold: the context token that keeps a refusal from ending a session nobody
// has, and the spelling of the member the card of codes travels in.
//
// Both are invisible when broken. A missing token costs a person mid-flow their
// screen of ten recovery codes, on a 401 that was never about a session; a
// renamed member is refused by the server's request-surface census with a 400
// that says nothing about spelling.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/session-expiry.interceptor';
import { mintRecoveryCodeSet } from '@app-core/security/recovery-codes';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  RegistrationApiService,
  type RegistrationRequestBody,
} from './registration-api.service';

const API_BASE_URL = 'https://api.test';
const OPTIONS_URL = `${API_BASE_URL}/api/registration/options`;
const REGISTRATION_URL = `${API_BASE_URL}/api/registration`;

// A body of the right shape, built from a real minted set rather than from
// casts. The verifier is a branded string, so the alternative is an `as` per
// entry — which asserts the shape this fixture is supposed to be an example of.
// Nothing here is under test: the service forwards the body untouched, and what
// these tests read is the request around it.
async function registrationBody(): Promise<RegistrationRequestBody> {
  const set = await mintRecoveryCodeSet();

  return {
    clientDataJson: 'Y2xpZW50RGF0YQ',
    attestationObject: 'YXR0ZXN0YXRpb24',
    clientExtensionResults: { prf: { enabled: true } },
    factorId: '3f2504e0-4f89-41d3-9a0c-0305e82c3301',
    wrappedContentKey: 'AQIDBAUGBwgJCgsMDQ4PEA',
    wrappedIndexKey: 'EA8ODQwLCgkIBwYFBAMCAQ',
    codes: set.verifiers.map((verifier, index) => ({
      verifier,
      factorId: `3f2504e0-4f89-41d3-9a0c-0305e82c33${String(index).padStart(2, '0')}`,
      wrappedContentKey: `AQIDBAUGBwgJCgsMDQ4PE${index}`,
      wrappedIndexKey: `EA8ODQwLCgkIBwYFBAMCA${index}`,
    })),
  };
}

// A narrowing rather than an assertion: `as Record<string, unknown>` would
// claim the shape instead of checking it, and a body that is not an object at
// all would then reach the member checks as `undefined`s that quietly pass.
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function objectBodyOf(request: TestRequest): Record<string, unknown> {
  const body: unknown = request.request.body;

  if (!isRecord(body)) {
    throw new Error('The registration request carried no JSON object body.');
  }

  return body;
}

// Every key of a JSON value, at every depth. A member name is a fact about the
// whole body rather than about its top level: `recoveryCodes` nested one level
// down is the same token the server's census refuses.
function keysOf(value: unknown): readonly string[] {
  if (Array.isArray(value)) {
    return value.flatMap((entry: unknown) => keysOf(entry));
  }

  if (typeof value === 'object' && value !== null) {
    return Object.entries(value).flatMap(([key, entry]: [string, unknown]) => [
      key,
      ...keysOf(entry),
    ]);
  }

  return [];
}

describe('RegistrationApiService', () => {
  let http: HttpTestingController;
  let api: RegistrationApiService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_BASE_URL }) },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(RegistrationApiService);
  });

  it('does not let a refusal end the session', async () => {
    // Arrange
    const body = await registrationBody();
    const caught: TestRequest[] = [];

    // Act
    api.getCreationOptions().subscribe();
    caught.push(http.expectOne(OPTIONS_URL));

    api.register(body).subscribe();
    caught.push(http.expectOne(REGISTRATION_URL));

    // Assert
    // Both legs, because both are made by a browser holding a provider token
    // and no session of this product's. Without the token,
    // `sessionExpiryInterceptor` reads a 401 from either as a session ending,
    // declares it over and navigates to `/welcome` — which on the second leg
    // means walking away from a screen showing ten recovery codes somebody may
    // already have written down, with no way back to them. A provider id token
    // lives an hour and a person can sit on the codes step for longer, so the
    // 401 is reachable rather than theoretical.
    expect(caught).toHaveLength(2);

    for (const request of caught) {
      expect(
        request.request.context.get(EXPECTS_UNAUTHENTICATED),
        `${request.request.url} does not declare itself unauthenticated.`,
      ).toBe(true);
    }
  });

  it('posts the set under the member the server accepts', async () => {
    // Arrange
    const body = await registrationBody();

    // Act
    api.register(body).subscribe();
    const request = http.expectOne(REGISTRATION_URL);
    const sent = objectBodyOf(request);

    // Assert
    expect(Array.isArray(sent['codes'])).toBe(true);
    expect(sent['codes']).toHaveLength(body.codes.length);

    // `recoveryCodes` is the spelling a reader reaches for, and the one the
    // server's request-surface census refuses outright: it admits
    // `recovery_code` only directly in front of `hash`, so any member able to
    // hold key material has to be looked at by a person. Renaming the member
    // here would be refused there, with a 400 naming nothing about spelling.
    expect(keysOf(sent)).not.toContain('recoveryCodes');
    expect(JSON.stringify(sent)).not.toContain('recoveryCodes');
  });
});
