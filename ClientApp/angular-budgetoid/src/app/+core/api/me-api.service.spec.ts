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

  it('reads the remaining recovery-code count', () => {
    // Arrange
    let received: number | undefined;

    // Act
    api.getRecoveryCodes().subscribe((value) => (received = value));
    const request = http.expectOne('https://api.test/api/me/recovery-codes');

    // Assert
    expect(request.request.method).toBe('GET');

    request.flush({ remaining: 7 });
    // The count itself, not the envelope. `{"remaining": n}` is a shape only
    // the wire has a use for, and unwrapping at this boundary is what keeps it
    // out of the service that renders the sentence.
    expect(received).toBe(7);
  });

  it('reads an account with no set as zero rather than as no answer', () => {
    // Arrange
    let received: number | undefined;

    // Act
    api.getRecoveryCodes().subscribe((value) => (received = value));
    const request = http.expectOne('https://api.test/api/me/recovery-codes');

    // Assert
    // The route never answers 404 — an account with no set has zero codes
    // left, which is an answer. Without this half, an implementation reading
    // `body.remaining ?? null`, or one treating a falsy count as a missing
    // one, passes the test above and hands the screen a `null` that means
    // "still loading" for an account that has already answered.
    request.flush({ remaining: 0 });
    expect(received).toBe(0);
  });

  // Every case below is a **200** whose body is not the shape the declared type
  // promises. That declaration is an assertion about JSON, not a check of it,
  // and nothing between the socket and the screen validates a DTO — so each of
  // these reaches the signal, and from there the template, exactly as written.
  //
  // The failure has to be raised *here* rather than repaired downstream. Every
  // consumer already has a `catchError` that says an honest sentence and
  // publishes nothing, so a throw at this boundary lands in the state the
  // screen was built for; a value coerced here instead — `?? 0`, `Number(…)` —
  // arrives indistinguishable from an answer the server actually gave.
  it('refuses a recovery-code count with no count in it', () => {
    // Arrange
    let received: number | undefined;
    let failure: unknown;

    // Act
    api.getRecoveryCodes().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http
      .expectOne('https://api.test/api/me/recovery-codes')
      .flush({ notTheCount: 7 });

    // Assert
    // Unvalidated, `body.remaining` is `undefined` and the signal is typed
    // `number | null`: the template's last branch tests `remaining !== null`,
    // which `undefined` satisfies, and Angular interpolates it as nothing. The
    // reader is told "You have  recovery codes left." — a sentence with a hole
    // in it where the only number the section exists to state should be.
    expect(failure).toBeInstanceOf(Error);
    expect(received).toBeUndefined();
  });

  it('refuses a recovery-code count of null', () => {
    // Arrange
    let received: number | undefined;
    let failure: unknown;

    // Act
    api.getRecoveryCodes().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http
      .expectOne('https://api.test/api/me/recovery-codes')
      .flush({ remaining: null });

    // Assert
    // Worse than the case above, because `null` is a value the whole path
    // already means something by: it is *no answer yet*. A published `null`
    // gives the section a seventh state the design book does not have — region
    // empty, count empty, nothing loading, nothing failed, forever — and it is
    // the one state no test asks about because it is not supposed to exist.
    expect(failure).toBeInstanceOf(Error);
    expect(received).toBeUndefined();
  });

  it('refuses a recovery-code count that is not a number', () => {
    // Arrange
    let received: number | undefined;
    let failure: unknown;

    // Act
    api.getRecoveryCodes().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http
      .expectOne('https://api.test/api/me/recovery-codes')
      .flush({ remaining: '7' });

    // Assert
    // A quoted number is the shape a serializer change produces, and it is the
    // one bad value that would *look* right on screen: `'7'` fails the `=== 0`
    // and `=== 1` branches and interpolates as `7`, so the plural sentence is
    // rendered for a count of one. Coercing it here would hide the change
    // rather than surface it.
    expect(failure).toBeInstanceOf(Error);
    expect(received).toBeUndefined();
  });

  it('refuses a recovery-code count that could not be a number of codes', () => {
    // Arrange
    const impossible = [-1, 1.5, Number.NaN];
    const failures: unknown[] = [];

    // Act
    for (const remaining of impossible) {
      api.getRecoveryCodes().subscribe({
        next: () => failures.push(null),
        error: (e: unknown) => failures.push(e),
      });
      http
        .expectOne('https://api.test/api/me/recovery-codes')
        .flush({ remaining });
    }

    // Assert
    // A count of codes is a whole number of them and cannot be negative. None
    // of these is reachable from the shipped API, which is the point: each
    // means something between the handler and here is no longer sending a
    // count, and a screen that renders "You have -1 recovery codes left."
    // reports that as a fact about the account.
    expect(failures).toHaveLength(impossible.length);
    for (const failure of failures) {
      expect(failure).toBeInstanceOf(Error);
    }
  });

  it('reads a body-less recovery-code response as a failure', () => {
    // Arrange
    let failure: unknown;

    // Act
    api.getRecoveryCodes().subscribe({ error: (e: unknown) => (failure = e) });
    http
      .expectOne('https://api.test/api/me/recovery-codes')
      .flush(null, { status: 204, statusText: 'No Content' });

    // Assert
    // This one already failed before the validation existed — reading
    // `.remaining` off `null` throws a TypeError inside the `map` and lands in
    // the same place. Pinned so the guard cannot be written in a way that
    // *rescues* it: an empty body is not a count of zero.
    expect(failure).toBeInstanceOf(Error);
  });

  it('reads a well-formed count as itself', () => {
    // Arrange
    // Control for the four refusals above. A validator written as
    // `throw new Error()` unconditionally passes every one of them, and this is
    // what says the boundary still lets an answer through — together with the
    // zero case above, which is the value most likely to be refused by an
    // over-eager truthiness check.
    let received: number | undefined;
    let failure: unknown;

    // Act
    api.getRecoveryCodes().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http
      .expectOne('https://api.test/api/me/recovery-codes')
      .flush({ remaining: 10 });

    // Assert
    expect(received).toBe(10);
    expect(failure).toBeUndefined();
  });

  it('refuses a credential list that is not a list', () => {
    // Arrange
    let received: readonly unknown[] | undefined;
    let failure: unknown;

    // Act
    api.getCredentials().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http
      .expectOne('https://api.test/api/me/credentials')
      .flush({ credentials: [] });

    // Assert
    // The same hole as the count, on the route whose consumer calls `.map` on
    // what arrives: an object body throws `credentials.map is not a function`
    // inside the `computed` the template reads, which abandons the change
    // detection pass and takes every section below the list off the screen.
    // Refusing here turns that into the sentence the section already has for a
    // list it could not load.
    expect(failure).toBeInstanceOf(Error);
    expect(received).toBeUndefined();
  });

  it('reads a well-formed credential list as itself', () => {
    // Arrange
    // Control for the refusal above, and specifically for the empty list: `[]`
    // is a real answer — an account with nothing attached — and a guard written
    // on truthiness or on length would refuse it and claim the request failed.
    const listed = [
      { id: 'a', type: 'passkey', createdAtUtc: '2026-03-11T22:00:00Z' },
    ];
    const bodies: unknown[] = [];

    // Act
    for (const body of [listed, []]) {
      api.getCredentials().subscribe({ next: (v) => bodies.push(v) });
      http.expectOne('https://api.test/api/me/credentials').flush(body);
    }

    // Assert
    expect(bodies).toEqual([listed, []]);
  });

  // There is deliberately no test for a generate/POST method: the service has
  // no such method, because `POST /api/me/recovery-codes` takes five WebAuthn
  // assertion members this client cannot produce.
});
