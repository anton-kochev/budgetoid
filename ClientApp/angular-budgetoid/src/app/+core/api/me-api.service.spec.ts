import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  AccountKeyResponseError,
  MeApiService,
  type AccountKeyCustodyDto,
  type AccountKeyEntry,
  type MeDto,
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

  // There is deliberately no test for a generate/POST method: the service has
  // no such method, because `POST /api/me/recovery-codes` takes five WebAuthn
  // assertion members this client cannot produce.
});
