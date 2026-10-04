import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { PROVIDER_CREDENTIAL } from '@app-core/interceptors/provider-credential.token';
import type { PasskeyAssertionPayload } from '@app-core/security/webauthn-encoding';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  AccountKeyResponseError,
  MeApiService,
  type AccountKeyCustodyDto,
  type AccountKeyEntry,
  type MeDto,
  type SessionDto,
} from './me-api.service';

const ACCOUNT_KEYS_URL = 'https://api.test/api/me/account-keys';

// ---------------------------------------------------------------------------
// The wire contract, read from the artifact both suites read.
//
// **This is the response half of the only thing binding this client to the
// server.** There is no OpenAPI document here, no generated client and no
// captured fixture, so the shape of `GET /api/me/account-keys` is asserted
// twice — once by a C# record and once by the interface in `me-api.service.ts`
// — and until this file existed the two assertions never met. The backend moved
// a factor to its own keypair, both suites stayed green, and the client went on
// reading members no route bound.
//
// TypeScript's own types cannot close it: `AccountKeyCustodyDto` is erased
// before a line of this spec runs, so nothing here can reflect over it. What
// *is* observable at runtime is what the boundary check DEMANDS — so the guard
// is what gets bound, one flushed body per named member, and a member renamed
// in the service reddens because the rename moves what `getAccountKeys` refuses.
//
// A missing or malformed artifact throws here, at import, and takes the file
// with it. It must never skip: a contract test that quietly stops running is
// indistinguishable from one that passes.
const WIRE_CONTRACT_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'account-keys-wire-v1.json',
);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// One message's whole set of wire spellings, parsed rather than trusted.
// Duplicates are refused as well as non-strings: every comparison below is over
// a set, and a set absorbs a repeat — so a list that named `factorId` twice
// would compare equal to one that named it once, and the case driven from it
// would run twice against the same member while looking like two.
function wireMembers(file: unknown, message: string): readonly string[] {
  if (!isRecord(file)) {
    throw new Error('The account-key wire contract is not an object.');
  }

  const messages = file['messages'];

  if (!isRecord(messages)) {
    throw new Error('The account-key wire contract names no messages.');
  }

  const entry = messages[message];

  if (!isRecord(entry)) {
    throw new Error(`The wire contract carries no ${message} message.`);
  }

  const listed = entry['members'];

  if (!Array.isArray(listed) || listed.length === 0) {
    throw new Error(`The wire contract's ${message} lists no members.`);
  }

  const members: string[] = [];

  for (const member of listed) {
    if (typeof member !== 'string' || member.length === 0) {
      throw new Error(`The wire contract's ${message} lists a blank member.`);
    }

    members.push(member);
  }

  if (new Set(members).size !== members.length) {
    throw new Error(`The wire contract's ${message} names a member twice.`);
  }

  return members;
}

const WIRE_CONTRACT: unknown = JSON.parse(
  readFileSync(WIRE_CONTRACT_PATH, 'utf8'),
);

const RESPONSE_MEMBERS = wireMembers(WIRE_CONTRACT, 'accountKeysResponse');
const ENTRY_MEMBERS = wireMembers(WIRE_CONTRACT, 'accountKeyEntry');

// One row of `wrapped_account_keys` as it crosses the wire: the identifier the
// factor's keypair was bound to, the wrapped private half, and the account's two
// keys encapsulated to the public half. The two envelopes are not real ones and
// nothing here opens them: this file is about the boundary check, and what the
// check reads is the *presence and type* of three members. Width, version byte
// and alphabet belong to `decodeBase64Url` and `openFactorKeypair`, and a second
// copy of them at this boundary would be a second definition of what an envelope
// is.
const ENTRY = {
  factorId: 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6',
  wrappedPrivateKey: 'AQIDBAUGBwgJCgsMDQ4PEA',
  encapsulatedAccountKeys: 'EBESExQVFhcYGRobHB0eHw',
} satisfies AccountKeyEntry;

const SECOND_ENTRY = {
  factorId: '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0',
  wrappedPrivateKey: 'ICEiIyQlJicoKSorLC0uLw',
  encapsulatedAccountKeys: 'MDEyMzQ1Njc4OTo7PD0-Pw',
} satisfies AccountKeyEntry;

// The account's own two facts, which the wrapper is what gives a place to live.
// A manifest is one sealed blob per account and an epoch numbers the generation
// of the set it authenticates, so both are true once however many factors the
// account holds.
const MANIFEST = 'AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw';

// `satisfies`, like the two entries above, and for their reason: the fixture is
// flushed and then compared against with `toEqual`, so a mistyped or misspelled
// member here would be written, read back and matched — the assertion passing
// against a shape the service's own type never had.
const CUSTODY = {
  manifest: MANIFEST,
  rotationEpoch: 1,
  factors: [ENTRY, SECOND_ENTRY],
} satisfies AccountKeyCustodyDto;

// ---------------------------------------------------------------------------
// Bodies assembled from the contract's own member list.
//
// **A value per name, and the body is built by walking the file rather than by
// writing an object out.** An object literal here would be a second copy of the
// list — greenable by editing this spec alone, which is exactly the arrangement
// the artifact exists to replace. Walking the file means a member added there
// and nowhere else has no value to carry and stops this file at import, which is
// the reddening the acceptance criterion asks for.
const RESPONSE_VALUES: Record<string, unknown> = {
  manifest: MANIFEST,
  rotationEpoch: 1,
  factors: [ENTRY],
};

const ENTRY_VALUES: Record<string, unknown> = { ...ENTRY };

function fromContract(
  members: readonly string[],
  values: Record<string, unknown>,
  what: string,
): Record<string, unknown> {
  const built: Record<string, unknown> = {};

  for (const member of members) {
    if (!(member in values)) {
      throw new Error(
        `The wire contract's ${what} names ${member}, which this spec has ` +
          'no value for. Teach it one rather than dropping the member.',
      );
    }

    built[member] = values[member];
  }

  return built;
}

// One member away from a body the boundary accepts. `delete` is avoided for
// `@typescript-eslint/no-dynamic-delete`'s reason and because a rebuild states
// the intent: what is flushed is every OTHER member the contract names.
function without(
  source: Record<string, unknown>,
  member: string,
): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(source).filter(([key]) => key !== member),
  );
}

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

  it('requests the export as text rather than parsed JSON', () => {
    // Arrange
    // A money lexeme `JSON.parse` would rewrite: `-0.0100` comes back out of a
    // parse-and-stringify as `-0.01`. The body is what `ExportDocument.cs`
    // writes, trimmed to the one member that can tell a pipe from a parser.
    const body = '{"schemaVersion":1,"amount":-0.0100}';
    let received: string | undefined;

    // Act
    api.getExport().subscribe((value) => (received = value));
    const request = http.expectOne('https://api.test/api/me/export');

    // Assert
    expect(request.request.method).toBe('GET');
    // **Text, and no longer bytes.** The client now opens every name and note
    // before it saves, so it has to read the document — but the read belongs to
    // `decodeExportDocument`, which is strict about the shape and exact about
    // the money column, and not to HttpClient, whose `json` response type would
    // parse the body with no shape check at all and hand the decoder an object
    // it can no longer refuse as `unrecognised`. Text keeps the one parse in the
    // one place that owns it. See docs/design/components.md, "Export section".
    expect(request.request.responseType).toBe('text');

    request.flush(body);
    // The string as served, character for character: a service that parsed
    // the body and re-serialised it would hand back `-0.01`.
    expect(received).toBe(body);
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
    // Observable<string> is caught here even if a future refactor loosens the
    // responseType assertion. A bodyless GET has no content to type.
    expect(request.request.headers.get('Content-Type')).toBeNull();

    request.flush('{}');
  });

  it('requests the account record as parsed JSON', () => {
    // Arrange
    let received: MeDto | undefined;

    // Act
    api.getMe().subscribe((value) => (received = value));
    const request = http.expectOne('https://api.test/api/me');

    // Assert
    expect(request.request.method).toBe('GET');
    // The sibling of the text pin, and the half that makes it mean something:
    // a service that set `responseType: 'text'` on *every* request would
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

  // `GET /api/me/account-keys` answers an **object** now — the account's
  // manifest, the generation that manifest is in, and one entry per factor —
  // and the bare array it used to answer is the shape this block refuses.
  //
  // The two swap places in one step rather than one of them being tolerated
  // "for now", because a boundary that takes either has stopped saying which
  // server it is talking to: a client reading the old spelling off the new body
  // finds `undefined` where the list should be, and a client reading the new
  // spelling off the old one finds `undefined` where the manifest should be.
  // Both are silent, and both end with somebody told their account cannot be
  // opened.
  //
  // What is at stake is worse than a wrong pixel. Every envelope member is a
  // string about to be fed to a decoder and an AEAD open, and an absent one
  // reaches `decodeBase64Url` as `undefined` — which throws *inside*
  // `AccountKeyCustodyService`'s trial loop, where a throw already means "this
  // factor is not the one, try the next". So a body this client should have
  // refused is read instead as the person having presented the wrong factor,
  // and the account is declared unopenable by its own key custody with nothing
  // anywhere naming the cause.
  it("reads the account's manifest, its epoch and every factor as they arrived", () => {
    // Arrange
    let received: AccountKeyCustodyDto | undefined;
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

    request.flush(CUSTODY);

    // Member for member, and both entries in the order the server sent them.
    // The list is the server's statement and nothing in this client sorts,
    // filters or appends to it — and the *order* matters to nobody, which is
    // exactly why a boundary that quietly reordered would never be noticed.
    expect(received).toEqual(CUSTODY);
    expect(failure).toBeUndefined();
  });

  it('reads an account with no manifest and no factors as an answer rather than a failure', () => {
    // Arrange
    // The control for every refusal below, and the body most likely to be
    // refused by an over-eager guard. `manifest: null` beside `rotationEpoch: 0`
    // and an empty list is a normal 200 and never a 404: it is what the route
    // gives for a session it cannot see, and what an account registered before
    // the manifest landed answers forever. `AccountKeyCustodyService` reads it
    // as `unopened` — "present another factor" — so a boundary that threw here
    // would turn a real answer into a refusal whose advice is "reload the page",
    // for a state no reload will change.
    let received: AccountKeyCustodyDto | undefined;
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
    http
      .expectOne(ACCOUNT_KEYS_URL)
      .flush({ manifest: null, rotationEpoch: 0, factors: [] });

    // Assert
    expect(received).toEqual({
      manifest: null,
      rotationEpoch: 0,
      factors: [],
    });
    expect(failure).toBeUndefined();
  });

  it('refuses an account-key response that arrived as a bare list of factors', () => {
    // Arrange
    // **The retired shape, and the case that used to assert the opposite.** The
    // previous server answered this array and this client accepted it; both
    // halves moved, and a client left accepting the old body would read
    // `undefined` for the manifest and for the epoch off an array that has
    // neither — then hand both to a gate that would blame the account's content
    // key for it.
    let received: AccountKeyCustodyDto | undefined;
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
    http.expectOne(ACCOUNT_KEYS_URL).flush([ENTRY, SECOND_ENTRY]);

    // Assert
    // **The message, and not merely that something threw.** Deleting this
    // refusal does not stop the request failing — `'manifest' in body` on an
    // array is `false` one line later and lands in the same `error` callback —
    // so `toBeInstanceOf(Error)` alone pins nothing here. What the refusals
    // exist for is that they say different things to whoever reads them: a body
    // that is not an object is a route or a proxy answering something else
    // entirely, or a server still on the old shape, while a malformed member is
    // a version skew on a route that *is* the right one.
    expect(failure).toBeInstanceOf(AccountKeyResponseError);
    expect(String(failure)).toContain("account's key custody");
    expect(received).toBeUndefined();
  });

  it.each([
    {
      why: 'the body is not an object at all',
      body: 'not-a-body',
      says: "account's key custody",
    },
    {
      why: 'the body is null',
      body: null,
      says: "account's key custody",
    },
    {
      // **The one refusal the server argues for from its own side.** An empty
      // string is a legal base64url rendering of zero bytes, so `''` would make
      // "there is no manifest" indistinguishable from "there is one and it
      // authenticates nobody" — and the two have different answers. The server
      // skips its encoder rather than emitting `''`, so this is the spelling it
      // refuses to send and this client refuses to read.
      why: 'an absent manifest arrived spelled as an empty string',
      body: { manifest: '', rotationEpoch: 1, factors: [ENTRY] },
      says: 'manifest',
    },
    {
      why: 'the manifest member is missing altogether',
      body: { rotationEpoch: 1, factors: [ENTRY] },
      says: 'manifest',
    },
    {
      why: 'the manifest arrived as something that is not a string',
      body: { manifest: 42, rotationEpoch: 1, factors: [ENTRY] },
      says: 'manifest',
    },
    {
      why: 'the rotation epoch is negative',
      body: { manifest: MANIFEST, rotationEpoch: -1, factors: [ENTRY] },
      says: 'rotation epoch',
    },
    {
      why: 'the rotation epoch is fractional',
      body: { manifest: MANIFEST, rotationEpoch: 1.5, factors: [ENTRY] },
      says: 'rotation epoch',
    },
    {
      why: 'the rotation epoch arrived as something that is not a number',
      body: { manifest: MANIFEST, rotationEpoch: '1', factors: [ENTRY] },
      says: 'rotation epoch',
    },
    {
      why: 'the epoch member is missing altogether',
      body: { manifest: MANIFEST, factors: [ENTRY] },
      says: 'rotation epoch',
    },
    {
      why: 'the factors member is missing altogether',
      body: { manifest: MANIFEST, rotationEpoch: 1 },
      says: 'list of factors',
    },
    {
      why: 'the factors member did not arrive as a list',
      body: { manifest: MANIFEST, rotationEpoch: 1, factors: ENTRY },
      says: 'list of factors',
    },
  ])('refuses an account-key response when $why', ({ body, says }) => {
    // Arrange
    // A refusal and deliberately not a repair. A coercion — `?? null` for a
    // manifest, `?? 0` for an epoch, `[]` for a member that is not a list —
    // lands on the screen as a fact about the account, indistinguishable from
    // an answer.
    let received: AccountKeyCustodyDto | undefined;
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
    http.expectOne(ACCOUNT_KEYS_URL).flush(body);

    // Assert
    // The message each refusal owns, for the reason the case above gives: the
    // sentences are what tell a version skew apart from a proxy answering
    // something else, and a check that only asked whether *something* threw
    // would accept any one of them in place of any other.
    expect(failure).toBeInstanceOf(AccountKeyResponseError);
    expect(String(failure)).toContain(says);
    expect(received).toBeUndefined();
  });

  // **The two members are read together as well as apart, and nothing above
  // does that.** `isManifestWire` admits any non-empty string and
  // `isRotationEpoch` admits `0`, so a body pairing a real manifest with `0` —
  // or `null` with a stored generation — satisfies every refusal in this file
  // and is handed on as an answer. The docstring on `AccountKeyCustodyDto`
  // states the invariant the pair carries: `0` is the epoch of an account with
  // no manifest row, because a stored generation starts at 1.
  //
  // What the skew costs is not a wrong pixel. A manifest beside `0` reaches
  // `openFactorManifest`, whose associated data refuses an epoch below 1 — a
  // throw the gate in `AccountKeyCustodyService` catches and turns into a
  // failure about the person's *factor*, sending somebody to hunt for another
  // passkey over two members of a body that disagreed with each other. Refused
  // here it is what it is: an answer this client cannot read.
  it.each([
    {
      why: 'a manifest arrived beside the epoch of an account that has none',
      body: { manifest: MANIFEST, rotationEpoch: 0, factors: [ENTRY] },
    },
    {
      why: 'an account with no manifest arrived at a stored generation',
      body: { manifest: null, rotationEpoch: 1, factors: [ENTRY] },
    },
  ])('refuses an account-key response when $why', ({ body }) => {
    // Arrange
    let received: AccountKeyCustodyDto | undefined;
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
    http.expectOne(ACCOUNT_KEYS_URL).flush(body);

    // Assert
    // **`AccountKeyResponseError`, the same type every other refusal here
    // throws, and that is the decision rather than the default.** It is what
    // lands the failure on custody's word for a body this browser could not
    // read — a reload — rather than on its word for key material that does not
    // line up, which says no factor will ever help. Neither member is damaged;
    // the two were served in a combination no account can be in.
    expect(failure).toBeInstanceOf(AccountKeyResponseError);
    expect(String(failure)).toContain('disagreed with itself');
    expect(received).toBeUndefined();
  });

  it.each([
    {
      why: 'the identifier the envelopes were bound to is missing',
      entry: {
        wrappedPrivateKey: ENTRY.wrappedPrivateKey,
        encapsulatedAccountKeys: ENTRY.encapsulatedAccountKeys,
      },
    },
    {
      why: 'the wrapped private key is missing',
      entry: {
        factorId: ENTRY.factorId,
        encapsulatedAccountKeys: ENTRY.encapsulatedAccountKeys,
      },
    },
    {
      why: 'the encapsulated account keys are missing',
      entry: {
        factorId: ENTRY.factorId,
        wrappedPrivateKey: ENTRY.wrappedPrivateKey,
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
    let received: AccountKeyCustodyDto | undefined;
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
    http.expectOne(ACCOUNT_KEYS_URL).flush({
      manifest: MANIFEST,
      rotationEpoch: 1,
      factors: [ENTRY, entry],
    });

    // Assert
    expect(failure).toBeInstanceOf(AccountKeyResponseError);
    expect(String(failure)).toContain('envelopes');
    expect(received).toBeUndefined();
  });

  // -------------------------------------------------------------------------
  // The contract, both directions.
  //
  // **Three cases, and each holds one half of a set equality that a subset
  // check would leave open.** The two loops say "the client demands every
  // member the file names"; the case beneath them says "and no member it does
  // not". Either alone passes on a side that has grown a member the other does
  // not bind, which is the live failure mode here rather than a tidiness rule:
  // `accountKeyEntry` is specified never to grow a fourth member, and a
  // one-directional check is precisely what would not notice one arriving.
  //
  // These deliberately overlap the hand-written refusals above — "the manifest
  // member is missing altogether" and its two siblings say the same thing about
  // today's three members. Neither is redundant. The cases above pin the
  // *message* each refusal owns, which is what tells a version skew apart from a
  // proxy answering something else; these pin the *set*, against a file the
  // server's own test reads. Nothing above was weakened to make room.
  it.each(RESPONSE_MEMBERS.map((member) => ({ member })))(
    'refuses an account-key response with no $member, which the wire contract names',
    ({ member }) => {
      // Arrange
      const body = without(
        fromContract(RESPONSE_MEMBERS, RESPONSE_VALUES, 'accountKeysResponse'),
        member,
      );
      let received: AccountKeyCustodyDto | undefined;
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
      http.expectOne(ACCOUNT_KEYS_URL).flush(body);

      // Assert
      expect(
        failure,
        `A body carrying every member but ${member} was read as an answer.`,
      ).toBeInstanceOf(AccountKeyResponseError);
      expect(received).toBeUndefined();
    },
  );

  it.each(ENTRY_MEMBERS.map((member) => ({ member })))(
    'refuses an account-key entry with no $member, which the wire contract names',
    ({ member }) => {
      // Arrange
      // The maimed entry sits *behind* a well-formed one, as in the case above:
      // a guard that judged only the head of the list would pass every row of
      // this loop. The list travels through the value map rather than being
      // pasted over the built body, so it lands only if the contract still
      // names `factors` — a rename there reddens instead of being papered over.
      const broken = without(
        fromContract(ENTRY_MEMBERS, ENTRY_VALUES, 'accountKeyEntry'),
        member,
      );
      const body = fromContract(
        RESPONSE_MEMBERS,
        { ...RESPONSE_VALUES, factors: [ENTRY, broken] },
        'accountKeysResponse',
      );
      let received: AccountKeyCustodyDto | undefined;
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
      http.expectOne(ACCOUNT_KEYS_URL).flush(body);

      // Assert
      expect(
        failure,
        `A factor carrying every member but ${member} was read as a factor.`,
      ).toBeInstanceOf(AccountKeyResponseError);
      expect(received).toBeUndefined();
    },
  );

  // The other direction, and it is the half that is easy to leave out. Every
  // case above removes something; not one of them would notice this client
  // demanding a member the contract does not name — a fourth member on an entry,
  // a `credentialId`, a per-row public key — because a body missing it refuses
  // for the reason those cases were already expecting. So one body built from
  // exactly the contract's members, and nothing else, has to be an answer.
  it('reads a body of exactly the members the wire contract names', () => {
    // Arrange
    const entry = fromContract(ENTRY_MEMBERS, ENTRY_VALUES, 'accountKeyEntry');
    const body = fromContract(
      RESPONSE_MEMBERS,
      { ...RESPONSE_VALUES, factors: [entry] },
      'accountKeysResponse',
    );
    let received: AccountKeyCustodyDto | undefined;
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
    http.expectOne(ACCOUNT_KEYS_URL).flush(body);

    // Assert
    expect(failure).toBeUndefined();
    expect(received).toEqual(body);
  });

  // **Every refusal on this route is an `AccountKeyResponseError`, and the type
  // is the behaviour rather than the decoration.**
  //
  // `AccountKeyCustodyService` turns a failed read into a word somebody acts
  // on, and it can only tell "this browser cannot parse what came back" from
  // "the network blinked" by the type it catches. Thrown as a bare `Error`,
  // every one of these lands on `unreachable`, whose copy is "try again in a
  // minute" — a loop that can never succeed, because nothing about the next
  // minute changes which JavaScript this tab is running. So one case walks the
  // whole refusal surface and asks the one question a `catch` will ask.
  it.each([
    { why: 'the body is the retired bare array', body: [ENTRY] },
    { why: 'the body is not an object', body: 7 },
    {
      why: 'an absent manifest is spelled as an empty string',
      body: { manifest: '', rotationEpoch: 0, factors: [] },
    },
    {
      why: 'the epoch is not a whole number',
      body: { manifest: MANIFEST, rotationEpoch: 0.5, factors: [] },
    },
    {
      why: 'the factor list is not a list',
      body: { manifest: MANIFEST, rotationEpoch: 1, factors: 'none' },
    },
    {
      why: 'a factor is missing an envelope',
      body: {
        manifest: MANIFEST,
        rotationEpoch: 1,
        factors: [{ factorId: ENTRY.factorId }],
      },
    },
  ])('refuses with a type a consumer can act on when $why', ({ body }) => {
    // Arrange
    let failure: unknown;

    // Act
    api.getAccountKeys().subscribe({
      error: (error: unknown) => {
        failure = error;
      },
    });
    http.expectOne(ACCOUNT_KEYS_URL).flush(body);

    // Assert
    // Both halves, because the second is what a `catch` branches on and the
    // first is what keeps it catchable by anything that only knows `Error`.
    expect(failure).toBeInstanceOf(Error);
    expect(failure).toBeInstanceOf(AccountKeyResponseError);
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

    request.flush({ manifest: null, rotationEpoch: 0, factors: [] });
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

  // `GET /api/me/session`: what kind of session this browser holds, when it
  // ends, and the account's scheduled erasure or `null`. It is the probe's first
  // question on every cold load, and the only read a locked session can make
  // that tells it what it is — `GET /api/me` answers a locked session `403`,
  // which reads as no session at all.
  describe('getSession', () => {
    const SESSION_URL = 'https://api.test/api/me/session';

    const FULL = {
      kind: 'full',
      expiresAtUtc: '2026-10-17T08:00:00Z',
      erasure: null,
    } satisfies SessionDto;

    const LOCKED_SCHEDULED = {
      kind: 'locked',
      expiresAtUtc: '2026-10-17T08:00:00Z',
      erasure: { takesEffectAtUtc: '2026-10-10T08:00:00Z' },
    } satisfies SessionDto;

    // Subscribes, flushes `body` as a 200, and reports what came out either
    // side. `failure` stays `undefined` when nothing failed.
    function readAnswer(body: Parameters<TestRequest['flush']>[0]): {
      readonly received: SessionDto | undefined;
      readonly failure: unknown;
    } {
      let received: SessionDto | undefined;
      let failure: unknown;

      api.getSession().subscribe({
        next: (value) => {
          received = value;
        },
        error: (error: unknown) => {
          failure = error;
        },
      });
      http.expectOne(SESSION_URL).flush(body);

      return { received, failure };
    }

    it('reads the session route', () => {
      // Act
      api.getSession().subscribe();
      const request = http.expectOne(SESSION_URL);

      // Assert
      expect(request.request.method).toBe('GET');
      expect(request.request.responseType).toBe('json');

      request.flush(FULL);
    });

    // The probe's own question, asked by a browser that cannot read its
    // `HttpOnly` cookie and so holds no local evidence at all: a 401 here is the
    // answer it went to fetch. Unmarked, `sessionExpiryInterceptor` reads that
    // answer as a session ending and navigates to `/welcome` from inside the
    // `APP_INITIALIZER`, before the router has activated anything — every
    // anonymous deep link in the product, gone.
    it('marks the session read as one whose refusal is not a session ending', () => {
      // Act
      api.getSession().subscribe();
      const request = http.expectOne(SESSION_URL);

      // Assert
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

      request.flush(FULL);
    });

    it('reads a full session with nothing scheduled as itself', () => {
      // Act
      const { received, failure } = readAnswer(FULL);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(FULL);
    });

    it('reads a locked session with a scheduled erasure as itself', () => {
      // Act
      const { received, failure } = readAnswer(LOCKED_SCHEDULED);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(LOCKED_SCHEDULED);
    });

    // The spellings an instant may arrive in and still say which instant it
    // is. The fractional row is the server's own: `System.Text.Json` writes a
    // UTC `DateTime` with up to seven fractional digits, and a decoder that
    // demanded whole seconds would refuse every real answer.
    it.each([
      { why: 'a Z designator', instant: '2026-10-17T08:00:00Z' },
      {
        why: 'seven fractional digits and a Z',
        instant: '2026-10-17T08:00:00.1234567Z',
      },
      { why: 'a positive offset', instant: '2026-10-17T10:00:00+02:00' },
      { why: 'a negative offset', instant: '2026-10-17T03:00:00-05:00' },
    ])('accepts an instant written with $why', ({ instant }) => {
      // Arrange
      const body = {
        kind: 'locked',
        expiresAtUtc: instant,
        erasure: { takesEffectAtUtc: instant },
      };

      // Act
      const { received, failure } = readAnswer(body);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(body);
    });

    // **Refused, never coerced.** Each of these is a body a version skew or a
    // proxy produces, and each would be published as a fact about the session.
    // An unknown kind is the sharpest: read as either known kind it either
    // hands budget screens to a session the server refuses on every one of
    // them, or sends a full session to the release screen. An offset-less
    // instant is the quietest: `Date` parses it as *local* time, so the
    // erasure date shown would move by the reader's offset — fourteen hours in
    // the runner's own zone — with nothing anywhere saying so. A missing
    // `erasure` is not `null`: `null` claims nothing is scheduled, which a body
    // that never mentioned schedules has not said.
    it.each([
      { why: 'a list', body: [] },
      { why: 'null', body: null },
      { why: 'a string', body: 'full' },
      {
        why: 'a kind this client does not know',
        body: { ...FULL, kind: 'admin' },
      },
      {
        why: 'a kind spelled in another case',
        body: { ...FULL, kind: 'Full' },
      },
      { why: 'no kind', body: without(FULL, 'kind') },
      { why: 'no expiry', body: without(FULL, 'expiresAtUtc') },
      {
        why: 'an expiry that is a number',
        body: { ...FULL, expiresAtUtc: 1792224000 },
      },
      {
        why: 'an expiry with no offset',
        body: { ...FULL, expiresAtUtc: '2026-10-17T08:00:00' },
      },
      {
        why: 'an expiry that is not an instant',
        body: { ...FULL, expiresAtUtc: 'tomorrowZ' },
      },
      { why: 'no erasure member', body: without(FULL, 'erasure') },
      {
        why: 'an erasure that is a string',
        body: { ...FULL, erasure: '2026-10-10T08:00:00Z' },
      },
      { why: 'an erasure that is a list', body: { ...FULL, erasure: [] } },
      { why: 'an erasure naming no instant', body: { ...FULL, erasure: {} } },
      {
        why: 'an erasure instant with no offset',
        body: { ...FULL, erasure: { takesEffectAtUtc: '2026-10-10T08:00:00' } },
      },
      {
        why: 'an erasure instant that is a number',
        body: { ...FULL, erasure: { takesEffectAtUtc: 1791619200 } },
      },
      // A member this bundle does not know is a server it was not written
      // against, at either level of the body.
      {
        why: 'a member this client does not know',
        body: { ...FULL, scope: 'budget' },
      },
      {
        why: 'an erasure carrying a member this client does not know',
        body: {
          ...LOCKED_SCHEDULED,
          erasure: { takesEffectAtUtc: '2026-10-10T08:00:00Z', reason: 'x' },
        },
      },
      // Spelled like an instant and naming none: `Date.parse` would roll each
      // forward into a real one rather than refuse it.
      {
        why: 'an expiry on a day the month does not have',
        body: { ...FULL, expiresAtUtc: '2026-02-30T10:00:00Z' },
      },
      {
        why: 'an expiry at an hour the day does not have',
        body: { ...FULL, expiresAtUtc: '2026-10-09T25:00:00Z' },
      },
      {
        why: 'an expiry at a minute the hour does not have',
        body: { ...FULL, expiresAtUtc: '2026-10-09T10:61:00Z' },
      },
      {
        why: 'an erasure instant on a day the month does not have',
        body: {
          ...LOCKED_SCHEDULED,
          erasure: { takesEffectAtUtc: '2026-02-30T10:00:00Z' },
        },
      },
      {
        why: 'an erasure instant at an hour the day does not have',
        body: {
          ...LOCKED_SCHEDULED,
          erasure: { takesEffectAtUtc: '2026-10-09T25:00:00Z' },
        },
      },
      {
        why: 'an erasure instant at a minute the hour does not have',
        body: {
          ...LOCKED_SCHEDULED,
          erasure: { takesEffectAtUtc: '2026-10-09T10:61:00Z' },
        },
      },
      {
        why: 'an expiry ending in a lowercase z',
        body: { ...FULL, expiresAtUtc: '2026-10-17T08:00:00z' },
      },
      // The pattern admits any two digits in an offset, so the range check
      // is the one refusal each of these meets.
      {
        why: 'an expiry at an offset hour a day does not have',
        body: { ...FULL, expiresAtUtc: '2026-10-17T10:00:00+24:00' },
      },
      {
        why: 'an expiry at an offset minute an hour does not have',
        body: { ...FULL, expiresAtUtc: '2026-10-17T10:00:00+00:60' },
      },
    ])('refuses a body with $why', ({ body }) => {
      // Act
      const { received, failure } = readAnswer(body);

      // Assert
      expect(received).toBeUndefined();
      expect(failure).toBeInstanceOf(Error);
      // Not dressed as an HTTP refusal: the probe reads a 401 or 403 as
      // `anonymous`, and a body it could not read says nothing about who is
      // asking — it is `unreachable`'s, whose remedy is a reload.
      expect(failure).not.toBeInstanceOf(HttpErrorResponse);
    });

    // The probe tells `anonymous` from `unreachable` by the status on this
    // error, so a service that caught it, or rethrew it as something else,
    // would turn every signed-out visitor into one the server could not reach.
    it('hands a refusal to the caller with its status', () => {
      // Arrange
      let status: number | null = null;

      // Act
      api.getSession().subscribe({
        error: (error: unknown) => {
          status = error instanceof HttpErrorResponse ? error.status : -1;
        },
      });
      http
        .expectOne(SESSION_URL)
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      // Assert
      expect(status).toBe(401);
    });
  });

  // There is deliberately no test for a generate/POST method: the service has
  // no such method, because `POST /api/me/recovery-codes` takes five WebAuthn
  // assertion members this client cannot produce.

  // The erasing request. See docs/design/components.md, "Erasure dialog".
  describe('eraseAccount', () => {
    const ERASURE_URL = 'https://api.test/api/me/erasure';

    // A fresh assertion, member for member as `PasskeyAssertionPayload`
    // declares it, with a user handle the authenticator did return.
    const ASSERTION: PasskeyAssertionPayload = {
      credentialId: 'AQIDBAUGBwgJCgsMDQ4PEA',
      clientDataJson: 'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0In0',
      authenticatorData: 'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8',
      signature: 'MEUCIQD-YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNA',
      userHandle: 'EBESExQVFhcYGRobHB0eHw',
    };

    it('posts to the erasure route', () => {
      // Act
      api.eraseAccount(ASSERTION).subscribe();
      const request = http.expectOne(ERASURE_URL);

      // Assert
      // `POST /api/me/erasure`, and never `DELETE /api/me`: the server removed
      // that route so the token-only erasure path is closed, and a request
      // carrying a proof in a DELETE body is one intermediaries may strip.
      expect(request.request.method).toBe('POST');

      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends the assertion and nothing else', () => {
      // Arrange
      // Handed an object carrying a sixth member, the way a caller that passed
      // the whole ceremony value spread flat would. A `CryptoKey` serializes
      // as `{}`, so the empty object here is what a leaked key-encryption key
      // looks like on the wire. A service that forwarded its argument whole
      // would put it there; one that projects the five members cannot.
      const withExtra = {
        ...ASSERTION,
        keyEncryptionKey: {},
      } as PasskeyAssertionPayload;

      // Act
      api.eraseAccount(withExtra).subscribe();
      const request = http.expectOne(ERASURE_URL);

      // Assert
      // Read from the serialized body, which is what crosses the wire, rather
      // than from the object handed to HttpClient. The body names no account —
      // the account erased is whichever one the session is — so a member
      // beyond these five is either an identifier the server must ignore or
      // key material that must never leave the tab.
      const sent = sentJson(request);

      expect(Object.keys(sent).sort()).toEqual(
        [
          'authenticatorData',
          'clientDataJson',
          'credentialId',
          'signature',
          'userHandle',
        ].sort(),
      );
      expect(sent).toEqual(ASSERTION);

      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends an absent user handle as null rather than leaving it out', () => {
      // Arrange
      const withoutHandle: PasskeyAssertionPayload = {
        ...ASSERTION,
        userHandle: null,
      };

      // Act
      api.eraseAccount(withoutHandle).subscribe();
      const request = http.expectOne(ERASURE_URL);

      // Assert
      // `JSON.stringify` drops a member whose value is `undefined`, so a
      // service that wrote `userHandle ?? undefined` — or built the body from
      // the truthy members — would send four members, and the server's record
      // binds a missing member to null by accident rather than by contract.
      const sent = sentJson(request);

      expect(sent).toHaveProperty('userHandle', null);

      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    // **The erasing request expects a 401 and is marked so.** A 401 from it may
    // be the gate declining the assertion — or a session that had already
    // ended, turned away by the fallback authorization policy before the gate —
    // and the dialog says either as `refused`, *nothing was erased*. Unmarked,
    // `sessionExpiryInterceptor` reads every one as a session ending and takes
    // the tab to `/welcome` over a sentence the dialog never got to say.
    it('marks the erasing request as one whose refusal is not a session ending', () => {
      // Act
      api.eraseAccount(ASSERTION).subscribe({ error: () => undefined });
      const request = http.expectOne(ERASURE_URL);

      // Assert
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('completes on a 204 with nothing to read', () => {
      // Arrange
      let completed = false;
      let failed = false;

      // Act
      api.eraseAccount(ASSERTION).subscribe({
        complete: () => (completed = true),
        error: () => (failed = true),
      });
      http
        .expectOne(ERASURE_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      // Assert
      // A body would have to be assembled from an account that no longer
      // exists; a caller that waited for one would never hear the erasure
      // land.
      expect(completed).toBe(true);
      expect(failed).toBe(false);
    });

    it('hands a refusal to the caller rather than swallowing it', () => {
      // Arrange
      let status: number | null = null;

      // Act
      api.eraseAccount(ASSERTION).subscribe({
        error: (error: unknown) => {
          status = error instanceof HttpErrorResponse ? error.status : -1;
        },
      });
      http
        .expectOne(ERASURE_URL)
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      // Assert
      // The flow above this reads the status to choose between `refused`,
      // `unrecognised` and `undetermined`; a service that caught it here and
      // completed would turn every refusal into an apparent success.
      expect(status).toBe(401);
    });
  });

  // `POST /api/me/email-change`: a fresh passkey assertion in the body and the
  // provider token the email-change return handed over on the request's
  // context, where the credentials interceptor turns it into the bearer.
  describe('changeEmail', () => {
    const EMAIL_CHANGE_URL = 'https://api.test/api/me/email-change';
    const PROVIDER_TOKEN = 'provider.token.one';

    const ASSERTION: PasskeyAssertionPayload = {
      credentialId: 'AQIDBAUGBwgJCgsMDQ4PEA',
      clientDataJson: 'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0In0',
      authenticatorData: 'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8',
      signature: 'MEUCIQD-YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNA',
      userHandle: 'EBESExQVFhcYGRobHB0eHw',
    };

    it('posts to the email-change route', () => {
      // Act
      api.changeEmail(PROVIDER_TOKEN, ASSERTION).subscribe();
      const request = http.expectOne(EMAIL_CHANGE_URL);

      // Assert
      expect(request.request.method).toBe('POST');

      request.flush({ sessionsEnded: 0 });
    });

    // The five assertion members and nothing else: no address and no subject,
    // because the server reads both off the token, and above all not the token
    // itself, which travels as the bearer.
    it('sends the assertion and nothing else', () => {
      // Arrange
      const withExtra = {
        ...ASSERTION,
        keyEncryptionKey: {},
      } as PasskeyAssertionPayload;

      // Act
      api.changeEmail(PROVIDER_TOKEN, withExtra).subscribe();
      const request = http.expectOne(EMAIL_CHANGE_URL);

      // Assert
      const sent = sentJson(request);

      expect(Object.keys(sent).sort()).toEqual(
        [
          'authenticatorData',
          'clientDataJson',
          'credentialId',
          'signature',
          'userHandle',
        ].sort(),
      );
      expect(sent).toEqual(ASSERTION);
      expect(request.request.serializeBody()).not.toContain(PROVIDER_TOKEN);

      request.flush({ sessionsEnded: 0 });
    });

    it('sends an absent user handle as null rather than leaving it out', () => {
      // Arrange
      const withoutHandle: PasskeyAssertionPayload = {
        ...ASSERTION,
        userHandle: null,
      };

      // Act
      api.changeEmail(PROVIDER_TOKEN, withoutHandle).subscribe();
      const request = http.expectOne(EMAIL_CHANGE_URL);

      // Assert
      expect(sentJson(request)).toHaveProperty('userHandle', null);

      request.flush({ sessionsEnded: 0 });
    });

    // The token is handed to the interceptor on the context, and the service
    // writes no header itself: a header written here would skip the
    // interceptor's origin check.
    it('carries the provider token on the request context and writes no bearer itself', () => {
      // Act
      api.changeEmail(PROVIDER_TOKEN, ASSERTION).subscribe();
      const request = http.expectOne(EMAIL_CHANGE_URL);

      // Assert
      expect(request.request.context.get(PROVIDER_CREDENTIAL)).toBe(
        PROVIDER_TOKEN,
      );
      expect(request.request.headers.has('Authorization')).toBe(false);

      request.flush({ sessionsEnded: 0 });
    });

    // A 401 here is the route's verdict on this request — a refused assertion
    // or a refused provider token — and the screen says so. Unmarked,
    // `sessionExpiryInterceptor` reads it as the session ending.
    it('marks the request as one whose refusal is not a session ending', () => {
      // Act
      api.changeEmail(PROVIDER_TOKEN, ASSERTION).subscribe();
      const request = http.expectOne(EMAIL_CHANGE_URL);

      // Assert
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

      request.flush({ sessionsEnded: 0 });
    });

    it('reads how many other sessions the change ended', () => {
      // Arrange
      let received: unknown;

      // Act
      api
        .changeEmail(PROVIDER_TOKEN, ASSERTION)
        .subscribe((value) => (received = value));
      http.expectOne(EMAIL_CHANGE_URL).flush({ sessionsEnded: 3 });

      // Assert
      expect(received).toEqual({ sessionsEnded: 3 });
    });

    // A list is not the answer this route gives, whatever is in it: refused
    // here, it reaches the flow as a 200 that does not read — `undetermined`.
    it('refuses a 200 whose body is a list', () => {
      // Arrange
      let received: unknown;
      let failure: unknown;

      // Act
      api.changeEmail(PROVIDER_TOKEN, ASSERTION).subscribe({
        next: (value) => {
          received = value;
        },
        error: (error: unknown) => {
          failure = error;
        },
      });
      http.expectOne(EMAIL_CHANGE_URL).flush([]);

      // Assert
      expect(failure).toBeInstanceOf(Error);
      expect(received).toBeUndefined();
    });
  });

  // The locked sign-in: `POST /api/locked-session`, authenticated by the
  // provider token the release flow was handed and nothing else. It answers a
  // Google sign-in on an account with no factors with a locked session, and
  // its body is a session description of exactly one kind.
  describe('openLockedSession', () => {
    const LOCKED_SESSION_URL = 'https://api.test/api/locked-session';
    const PROVIDER_TOKEN = 'provider.token.locked';

    const LOCKED = {
      kind: 'locked',
      expiresAtUtc: '2026-10-17T08:00:00Z',
      erasure: null,
    } as const;

    const LOCKED_SCHEDULED = {
      kind: 'locked',
      expiresAtUtc: '2026-10-17T08:00:00Z',
      erasure: { takesEffectAtUtc: '2026-10-10T08:00:00Z' },
    } as const;

    function readAnswer(body: Parameters<TestRequest['flush']>[0]): {
      readonly received: unknown;
      readonly failure: unknown;
    } {
      let received: unknown;
      let failure: unknown;

      api.openLockedSession(PROVIDER_TOKEN).subscribe({
        next: (value) => {
          received = value;
        },
        error: (error: unknown) => {
          failure = error;
        },
      });
      http.expectOne(LOCKED_SESSION_URL).flush(body);

      return { received, failure };
    }

    it('posts to the locked sign-in route with no body', () => {
      // Act
      api.openLockedSession(PROVIDER_TOKEN).subscribe();
      const request = http.expectOne(LOCKED_SESSION_URL);

      // Assert
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toBeNull();

      request.flush(LOCKED);
    });

    // The token rides on the context for the interceptor to send once it has
    // settled the origin; a header written here would skip that check, and a
    // token in the body would be one more place for it to be recorded.
    it('carries the provider token on the request context and writes no bearer itself', () => {
      // Act
      api.openLockedSession(PROVIDER_TOKEN).subscribe();
      const request = http.expectOne(LOCKED_SESSION_URL);

      // Assert
      expect(request.request.context.get(PROVIDER_CREDENTIAL)).toBe(
        PROVIDER_TOKEN,
      );
      expect(request.request.headers.has('Authorization')).toBe(false);
      expect(request.request.serializeBody()).toBeNull();

      request.flush(LOCKED);
    });

    // A 401 here is the provider token refused — the release screen has its
    // own sentence for it — and never a session ending: the browser asking has
    // no session to end yet.
    it('marks the request as one whose refusal is not a session ending', () => {
      // Act
      api.openLockedSession(PROVIDER_TOKEN).subscribe();
      const request = http.expectOne(LOCKED_SESSION_URL);

      // Assert
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

      request.flush(LOCKED);
    });

    it('reads a locked session with nothing scheduled as itself', () => {
      // Act
      const { received, failure } = readAnswer(LOCKED);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(LOCKED);
    });

    it('reads a locked session with a scheduled erasure as itself', () => {
      // Act
      const { received, failure } = readAnswer(LOCKED_SCHEDULED);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(LOCKED_SCHEDULED);
    });

    // The session probe's instant rules, unchanged: an offset-less instant is
    // read by `Date` as the reader's local time, and the release screen renders
    // the erasure date from it.
    it.each([
      {
        why: 'a full session, which this route never opens',
        body: { ...LOCKED, kind: 'full' },
      },
      {
        why: 'a kind this client does not know',
        body: { ...LOCKED, kind: 'x' },
      },
      { why: 'a list', body: [] },
      { why: 'null', body: null },
      { why: 'no erasure member', body: without(LOCKED, 'erasure') },
      { why: 'no expiry', body: without(LOCKED, 'expiresAtUtc') },
      {
        why: 'an expiry with no offset',
        body: { ...LOCKED, expiresAtUtc: '2026-10-17T08:00:00' },
      },
      {
        why: 'an erasure instant with no offset',
        body: {
          ...LOCKED,
          erasure: { takesEffectAtUtc: '2026-10-10T08:00:00' },
        },
      },
      {
        why: 'an expiry on a day the month does not have',
        body: { ...LOCKED, expiresAtUtc: '2026-02-30T10:00:00Z' },
      },
      {
        why: 'a member this client does not know',
        body: { ...LOCKED, budgetId: 'b' },
      },
    ])('refuses a body with $why', ({ body }) => {
      // Act
      const { received, failure } = readAnswer(body);

      // Assert
      expect(received).toBeUndefined();
      expect(failure).toBeInstanceOf(Error);
      expect(failure).not.toBeInstanceOf(HttpErrorResponse);
    });

    // `no_account` is the one refusal the release screen answers with its own
    // sentence and a way to create an account, so it has to reach the caller
    // as itself — status and body — and not as a generic failure.
    it('hands a no-account refusal to the caller with its status and its refusal', () => {
      // Arrange
      let failure: unknown;

      // Act
      api.openLockedSession(PROVIDER_TOKEN).subscribe({
        error: (error: unknown) => {
          failure = error;
        },
      });
      http
        .expectOne(LOCKED_SESSION_URL)
        .flush(
          { refusal: 'no_account' },
          { status: 404, statusText: 'Not Found' },
        );

      // Assert
      expect(failure).toBeInstanceOf(HttpErrorResponse);
      expect(failure instanceof HttpErrorResponse ? failure.status : -1).toBe(
        404,
      );
      expect(
        failure instanceof HttpErrorResponse ? failure.error : null,
      ).toEqual({ refusal: 'no_account' });
    });

    it.each([401, 403, 500])(
      'hands a %i to the caller with its status',
      (status) => {
        // Arrange
        let seen: number | null = null;

        // Act
        api.openLockedSession(PROVIDER_TOKEN).subscribe({
          error: (error: unknown) => {
            seen = error instanceof HttpErrorResponse ? error.status : -1;
          },
        });
        http
          .expectOne(LOCKED_SESSION_URL)
          .flush(null, { status, statusText: 'Refused' });

        // Assert
        expect(seen).toBe(status);
      },
    );
  });

  // The one act a locked session offers. See docs/design/components.md,
  // "Releasing an account", and docs/business-logic/erasure.md.
  describe('scheduleErasure', () => {
    const SCHEDULE_URL = 'https://api.test/api/me/erasure/schedule';
    const SCHEDULED = { takesEffectAtUtc: '2026-10-16T08:00:00Z' } as const;

    function readAnswer(body: Parameters<TestRequest['flush']>[0]): {
      readonly received: unknown;
      readonly failure: unknown;
    } {
      let received: unknown;
      let failure: unknown;

      api.scheduleErasure().subscribe({
        next: (value) => {
          received = value;
        },
        error: (error: unknown) => {
          failure = error;
        },
      });
      http.expectOne(SCHEDULE_URL).flush(body);

      return { received, failure };
    }

    it('posts to the schedule route with an empty body', () => {
      // Act
      api.scheduleErasure().subscribe();
      const request = http.expectOne(SCHEDULE_URL);

      // Assert
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toBeNull();
      expect(request.request.serializeBody()).toBeNull();

      request.flush(SCHEDULED);
    });

    // **Unmarked, and that is the decision.** The route judges nothing but the
    // session it was sent with, so its 401 is a session that ended — the fact
    // `sessionExpiryInterceptor` owns. Marked, the release screen would sit on
    // an ended session saying nothing.
    it('leaves the schedule request unmarked, so its 401 is a session ending', () => {
      // Act
      api.scheduleErasure().subscribe();
      const request = http.expectOne(SCHEDULE_URL);

      // Assert
      expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
      expect(request.request.context.get(PROVIDER_CREDENTIAL)).toBeFalsy();

      request.flush(SCHEDULED);
    });

    it('reads the instant the server stored as itself', () => {
      // Act
      const { received, failure } = readAnswer(SCHEDULED);

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual(SCHEDULED);
    });

    it.each([
      { why: 'a Z designator', instant: '2026-10-16T08:00:00Z' },
      {
        why: 'seven fractional digits and a Z',
        instant: '2026-10-16T08:00:00.1234567Z',
      },
      { why: 'a positive offset', instant: '2026-10-16T10:00:00+02:00' },
      { why: 'a negative offset', instant: '2026-10-16T03:00:00-05:00' },
    ])('accepts an instant written with $why', ({ instant }) => {
      // Act
      const { received, failure } = readAnswer({ takesEffectAtUtc: instant });

      // Assert
      expect(failure).toBeUndefined();
      expect(received).toEqual({ takesEffectAtUtc: instant });
    });

    // The session probe's instant rules, unchanged. An offset-less instant is
    // the sharpest: `Date` reads it as the reader's local time, and the
    // release screen would render a date hours wrong — fourteen in the
    // runner's own zone.
    it.each([
      { why: 'a list', body: [] },
      { why: 'null', body: null },
      { why: 'a bare string', body: '2026-10-16T08:00:00Z' },
      { why: 'no instant', body: {} },
      {
        why: 'an instant with no offset',
        body: { takesEffectAtUtc: '2026-10-16T08:00:00' },
      },
      {
        why: 'an instant that is a number',
        body: { takesEffectAtUtc: 1792137600 },
      },
      { why: 'an instant that is null', body: { takesEffectAtUtc: null } },
      {
        why: 'an instant on a day the month does not have',
        body: { takesEffectAtUtc: '2026-02-30T10:00:00Z' },
      },
      {
        why: 'an instant at an hour the day does not have',
        body: { takesEffectAtUtc: '2026-10-16T25:00:00Z' },
      },
      {
        why: 'an instant ending in a lowercase z',
        body: { takesEffectAtUtc: '2026-10-16T08:00:00z' },
      },
      {
        why: 'a member this client does not know',
        body: { ...SCHEDULED, cancellable: true },
      },
    ])('refuses a body with $why', ({ body }) => {
      // Act
      const { received, failure } = readAnswer(body);

      // Assert
      expect(received).toBeUndefined();
      expect(failure).toBeInstanceOf(Error);
      // Not dressed as an HTTP refusal: the flow reads a 403 as `unrecognised`
      // and everything that is not an `HttpErrorResponse` as `undetermined`.
      expect(failure).not.toBeInstanceOf(HttpErrorResponse);
    });

    it.each([401, 403, 500])(
      'hands a %i to the caller with its status',
      (status) => {
        // Arrange
        let seen: number | null = null;

        // Act
        api.scheduleErasure().subscribe({
          error: (error: unknown) => {
            seen = error instanceof HttpErrorResponse ? error.status : -1;
          },
        });
        http
          .expectOne(SCHEDULE_URL)
          .flush(null, { status, statusText: 'Refused' });

        // Assert
        expect(seen).toBe(status);
      },
    );
  });
});

// The body as it crosses the wire: HttpClient's own serialization of what the
// service handed it, parsed back. Refuses anything that is not JSON text, so a
// service that sent `FormData` or a `Blob` fails here by name.
function sentJson(request: TestRequest): Record<string, unknown> {
  const raw = request.request.serializeBody();

  if (typeof raw !== 'string') {
    throw new Error('The request body was not serialized as JSON text.');
  }

  const parsed: unknown = JSON.parse(raw);

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('The request body is not a JSON object.');
  }

  return parsed as Record<string, unknown>;
}

// `POST /api/me/erasure/schedule/cancellation`: withdraws the account's
// scheduled erasure, authorized by a fresh passkey assertion over a
// re-authentication challenge. The body is the immediate erasure's, member for
// member, and so is the refusal of a declined assertion.
//
// **Called by name**, so a service that does not have the method yet fails on
// an assertion naming it rather than taking this whole file down with a
// compile error.
describe('MeApiService.cancelScheduledErasure', () => {
  const CANCELLATION_URL =
    'https://api.test/api/me/erasure/schedule/cancellation';

  const ASSERTION: PasskeyAssertionPayload = {
    credentialId: 'AQIDBAUGBwgJCgsMDQ4PEA',
    clientDataJson: 'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0In0',
    authenticatorData: 'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8',
    signature: 'MEUCIQD-YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNA',
    userHandle: 'EBESExQVFhcYGRobHB0eHw',
  };

  // What the server's `PasskeyVerificationExceptionHandler` writes for every
  // declined assertion in the product, this route's included
  // (`CancelScheduledErasureEndpointTests.Cancel_WithoutAnAssertion_Is401_…`).
  const ASSERTION_REFUSAL = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
    title: 'The passkey could not be verified.',
    status: 401,
    refusal: 'assertion',
  };

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

  function cancel(assertion: PasskeyAssertionPayload): Observable<void> {
    const member: unknown = Reflect.get(api, 'cancelScheduledErasure');

    if (typeof member !== 'function') {
      throw new Error('MeApiService has no "cancelScheduledErasure" method.');
    }

    return Reflect.apply(member, api, [assertion]) as Observable<void>;
  }

  it('posts to the cancellation route', () => {
    // Act
    cancel(ASSERTION).subscribe();
    const request = http.expectOne(CANCELLATION_URL);

    // Assert
    expect(request.request.method).toBe('POST');

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends the five assertion members and nothing beside them', () => {
    // Arrange
    // A sixth member the way a caller that spread the whole ceremony value
    // would hand one over. A `CryptoKey` serializes as `{}`, so this is what a
    // leaked key-encryption key looks like on the wire.
    const withExtra = {
      ...ASSERTION,
      keyEncryptionKey: {},
    } as PasskeyAssertionPayload;

    // Act
    cancel(withExtra).subscribe();
    const request = http.expectOne(CANCELLATION_URL);

    // Assert
    const sent = sentJson(request);

    expect(Object.keys(sent).sort()).toEqual(
      [
        'authenticatorData',
        'clientDataJson',
        'credentialId',
        'signature',
        'userHandle',
      ].sort(),
    );
    expect(sent).toEqual(ASSERTION);

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends an absent user handle as null rather than leaving it out', () => {
    // Arrange
    const withoutHandle: PasskeyAssertionPayload = {
      ...ASSERTION,
      userHandle: null,
    };

    // Act
    cancel(withoutHandle).subscribe();
    const request = http.expectOne(CANCELLATION_URL);

    // Assert
    // `JSON.stringify` drops an `undefined` member, so `?? undefined` or a
    // body built from the truthy members would send four.
    expect(sentJson(request)).toHaveProperty('userHandle', null);

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  // A 401 here is usually the gate declining the assertion — the screen's own
  // sentence — so the request is marked, or the interceptor takes the tab to
  // Welcome over a sentence the section never got to say.
  it('marks the request as one whose refusal is not a session ending', () => {
    // Act
    cancel(ASSERTION).subscribe({ error: () => undefined });
    const request = http.expectOne(CANCELLATION_URL);

    // Assert
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);
    expect(request.request.context.get(PROVIDER_CREDENTIAL)).toBeFalsy();

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('completes on a 204 with nothing to read', () => {
    // Arrange
    let completed = false;
    let failed = false;

    // Act
    cancel(ASSERTION).subscribe({
      complete: () => (completed = true),
      error: () => (failed = true),
    });
    http
      .expectOne(CANCELLATION_URL)
      .flush(null, { status: 204, statusText: 'No Content' });

    // Assert
    // A decoder demanding a body would turn every cancellation that landed
    // into an error the flow reads as `undetermined`.
    expect(completed).toBe(true);
    expect(failed).toBe(false);
  });

  it('hands a refused assertion to the caller with its status and body untouched', () => {
    // Arrange
    let refusal: unknown = null;

    // Act
    cancel(ASSERTION).subscribe({
      error: (error: unknown) => {
        refusal = error;
      },
    });
    http
      .expectOne(CANCELLATION_URL)
      .flush(ASSERTION_REFUSAL, { status: 401, statusText: 'Unauthorized' });

    // Assert
    // The flow reads `refusal` off the body to say `refused` without a
    // probe; a service that mapped the error or dropped the body would leave
    // it nothing to read.
    expect(refusal).toBeInstanceOf(HttpErrorResponse);
    expect((refusal as HttpErrorResponse).status).toBe(401);
    expect((refusal as HttpErrorResponse).error).toEqual(ASSERTION_REFUSAL);
  });

  it.each([400, 403, 500])('hands a %i to the caller', (status) => {
    // Arrange
    let received: number | null = null;

    // Act
    cancel(ASSERTION).subscribe({
      error: (error: unknown) => {
        received = error instanceof HttpErrorResponse ? error.status : -1;
      },
    });
    http
      .expectOne(CANCELLATION_URL)
      .flush(null, { status, statusText: 'Refused' });

    // Assert
    expect(received).toBe(status);
  });
});
