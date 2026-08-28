import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  MeApiService,
  type AccountKeyEntry,
  type MeDto,
} from './me-api.service';

const ACCOUNT_KEYS_URL = 'https://api.test/api/me/account-keys';

// One row of `wrapped_account_keys` as it crosses the wire. The two envelopes
// are not real ones and nothing here opens them: this file is about the
// boundary check, and what the check reads is the *presence and type* of three
// members. Width, version byte and alphabet belong to `decodeBase64Url` and
// `openEnvelope`, and a second copy of them at this boundary would be a second
// definition of what an envelope is.
const ENTRY = {
  factorId: 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6',
  wrappedContentKey: 'AQIDBAUGBwgJCgsMDQ4PEA',
  wrappedIndexKey: 'EBESExQVFhcYGRobHB0eHw',
} satisfies AccountKeyEntry;

const SECOND_ENTRY = {
  factorId: '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0',
  wrappedContentKey: 'ICEiIyQlJicoKSorLC0uLw',
  wrappedIndexKey: 'MDEyMzQ1Njc4OTo7PD0-Pw',
} satisfies AccountKeyEntry;

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

  // `GET /api/me/account-keys` had no case at all until this block, and the
  // hole was not academic: replacing the whole of the guard below with a cast
  // left the suite green. The method's own comment argues hard for refusing a
  // malformed body rather than repairing it, and nothing held the argument.
  //
  // What is at stake here is worse than a wrong pixel. Every member is a string
  // about to be fed to a decoder and an AEAD open, and an absent one reaches
  // `decodeBase64Url` as `undefined` — which throws *inside*
  // `AccountKeyCustodyService`'s trial loop, where a throw already means "this
  // factor is not the one, try the next". So a body this client should have
  // refused is read instead as the person having presented the wrong factor,
  // and the account is declared unopenable by its own key custody with nothing
  // anywhere naming the cause.
  it('reads the wrapped keys of every factor as they arrived', () => {
    // Arrange
    let received: readonly AccountKeyEntry[] | undefined;
    let failure: unknown;

    // Act
    api.getAccountKeys().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    const request = http.expectOne(ACCOUNT_KEYS_URL);

    // Assert
    expect(request.request.method).toBe('GET');

    request.flush([ENTRY, SECOND_ENTRY]);

    // Both entries, in the order the server sent them, member for member. The
    // list is the server's statement and nothing in this client sorts, filters
    // or appends to it — and the *order* matters to nobody, which is exactly
    // why a boundary that quietly reordered would never be noticed.
    expect(received).toEqual([ENTRY, SECOND_ENTRY]);
    expect(failure).toBeUndefined();
  });

  it('reads a session with no factors as an empty list rather than a failure', () => {
    // Arrange
    // The control for every refusal below, and the one value most likely to be
    // refused by an over-eager guard. `[]` is the answer the route gives for a
    // session it cannot see *and* for a credential carrying no factors,
    // indistinguishably and on purpose. `AccountKeyCustodyService` reads it as
    // `unopened` — "present another factor" — so a boundary that threw here
    // would turn a real answer into `unreachable`, whose advice is "try the
    // same factor again in a minute", for a state that will never change on its
    // own.
    let received: readonly AccountKeyEntry[] | undefined;
    let failure: unknown;

    // Act
    api.getAccountKeys().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http.expectOne(ACCOUNT_KEYS_URL).flush([]);

    // Assert
    expect(received).toEqual([]);
    expect(failure).toBeUndefined();
  });

  it('refuses an account-key response that is not a list of factors', () => {
    // Arrange
    let received: readonly AccountKeyEntry[] | undefined;
    let failure: unknown;

    // Act
    api.getAccountKeys().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    http.expectOne(ACCOUNT_KEYS_URL).flush({ factors: [ENTRY] });

    // Assert
    // **The message, and not merely that something threw.** Deleting this
    // refusal does not stop the request failing — `entries.every` on an object
    // throws a `TypeError` one line later and lands in the same `error`
    // callback — so `toBeInstanceOf(Error)` alone pins nothing here. What the
    // two refusals exist for is that they say different things to whoever reads
    // them: a body that is not a list is a route or a proxy answering something
    // else entirely, while a malformed entry is a version skew on the right
    // route. That distinction is the behaviour, so it is what is asserted.
    expect(failure).toBeInstanceOf(Error);
    expect(String(failure)).toContain('list of factors');
    expect(received).toBeUndefined();
  });

  it.each([
    {
      why: 'the identifier the envelopes were sealed against is missing',
      entry: {
        wrappedContentKey: ENTRY.wrappedContentKey,
        wrappedIndexKey: ENTRY.wrappedIndexKey,
      },
    },
    {
      why: 'the content key envelope is missing',
      entry: {
        factorId: ENTRY.factorId,
        wrappedIndexKey: ENTRY.wrappedIndexKey,
      },
    },
    {
      why: 'the index key envelope is missing',
      entry: {
        factorId: ENTRY.factorId,
        wrappedContentKey: ENTRY.wrappedContentKey,
      },
    },
    {
      why: 'a member arrived as something that is not a string',
      entry: { ...ENTRY, factorId: 42 },
    },
    {
      why: 'an entry is not an object at all',
      entry: null,
    },
  ])('refuses an account-key entry when $why', ({ entry }) => {
    // Arrange
    // Per entry and not merely over the collection, unlike `getCredentials`. A
    // credential row is total in what it renders, so a malformed *entry* there
    // spoils one row; here an entry has no partial use at all — two thirds of a
    // factor opens nothing.
    let received: readonly AccountKeyEntry[] | undefined;
    let failure: unknown;

    // Act
    api.getAccountKeys().subscribe({
      next: (value) => {
        received = value;
      },
      error: (error: unknown) => {
        failure = error;
      },
    });
    // The malformed entry sits *behind* a well-formed one, so a guard that
    // judged only the head of the list passes nothing here.
    http.expectOne(ACCOUNT_KEYS_URL).flush([ENTRY, entry]);

    // Assert
    // The entry refusal's own sentence, for the reason the case above gives:
    // the two messages are what tell a version skew apart from a proxy
    // answering something else, and a check that only asked whether *something*
    // threw would accept either in place of the other.
    expect(failure).toBeInstanceOf(Error);
    expect(String(failure)).toContain('envelopes');
    expect(received).toBeUndefined();
  });

  // **The account-key read carries `EXPECTS_UNAUTHENTICATED`, and the reason is
  // custody's own rule read from the outside.**
  //
  // `AccountKeyCustodyService` never calls anything on `SessionService`,
  // because a key that will not open is not a session that ended. Unmarked,
  // this request routes its own 401 into `sessionExpiryInterceptor` — the
  // single owner of "the session ended" — which calls `session.ended()` and
  // navigates to `/welcome`. On the sign-in path that navigation races the one
  // to `/app` and wins, being later: a person whose assertion the server just
  // accepted lands anonymous on the welcome screen, with the screen saying
  // nothing at all because the sign-in did not fail. A deterministic 401 there
  // is a loop.
  //
  // Suppressed, the fact is not lost — it is deferred to a request that can
  // say something about it. If the session really has ended, the next read the
  // person makes answers 401 from a screen that renders its own failure line.
  it('marks the account-key read as one whose refusal is not a session ending', () => {
    // Act
    api.getAccountKeys().subscribe({ error: () => undefined });
    const request = http.expectOne(ACCOUNT_KEYS_URL);

    // Assert
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

    request.flush([]);
  });

  it('leaves the account record read unmarked', () => {
    // Arrange
    // The control, and the half that makes the pin above mean something. A
    // token set on `BaseApiService.get` — or on this service — satisfies the
    // case above perfectly while suppressing every genuine session ending in
    // the product. The Settings screen reads `GET /api/me` from a browser that
    // believes it holds a session, so a 401 there *is* the session having
    // ended, and the bounce is the correct answer.
    //
    // `getSessionOwner()` reads the same route and is marked, which is the
    // whole reason the two methods exist: only the request can tell two
    // callers of one route apart.
    // Act
    api.getMe().subscribe({ error: () => undefined });
    const unmarked = http.expectOne('https://api.test/api/me');

    // Assert
    expect(unmarked.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);

    unmarked.flush({ email: 'owner@budgetoid.test' });

    api.getSessionOwner().subscribe({ error: () => undefined });
    const marked = http.expectOne('https://api.test/api/me');

    expect(marked.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

    marked.flush({ email: 'owner@budgetoid.test' });
  });

  // There is deliberately no test for a generate/POST method: the service has
  // no such method, because `POST /api/me/recovery-codes` takes five WebAuthn
  // assertion members this client cannot produce.
});
