import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
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
  KeyRotationApiService,
  type BeginRotationRequestBody,
  type CompleteRotationRequestBody,
  type KeyRotationBegunDto,
  type KeyRotationStateDto,
  type ResealChunkRequestBody,
  type ResealedDescribedRowBody,
  type ResealedNamedRowBody,
  type ResealedTransactionBody,
  type RotationInventoryDto,
  type RotationSealBody,
  type StagedRotationDto,
} from './key-rotation-api.service';

const ROTATION_URL = 'https://api.test/api/me/key-rotation';
const CHUNKS_URL = 'https://api.test/api/me/key-rotation/chunks';
const COMPLETION_URL = 'https://api.test/api/me/key-rotation/completion';

// ---------------------------------------------------------------------------
// The wire contract, read from the artifact both suites read.
//
// **Nothing else binds this client to the four rotation routes.** There is no
// OpenAPI document here, no generated client and no captured fixture, so the
// shape of every request and response below is asserted twice — once by a C#
// record and once by a type in `key-rotation-api.service.ts` — and the two
// assertions never meet. `account-keys-wire-v1.json` exists because exactly
// that gap shipped once: the server moved a factor to its own keypair, both
// suites stayed green, and the client went on sending members no route bound.
//
// A member set written out *in this file* would be greenable by one paste — the
// actual over the expected, in a diff that reads as a test being updated beside
// its code. Read from the artifact, the same paste has to move a file the
// server's own suite reads too.
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
  'key-rotation-wire-v1.json',
);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// One message's whole set of wire spellings, parsed rather than trusted, and
// sorted so that every comparison below is over a set rather than over an
// order. Duplicates are refused as well as blanks: a sorted-list comparison
// absorbs neither, but a list naming `factorId` twice would describe a
// three-member message as four and the case driven from it would read as a
// widening nobody made.
function wireMembers(file: unknown, message: string): readonly string[] {
  if (!isRecord(file)) {
    throw new Error('The key-rotation wire contract is not an object.');
  }

  const messages = file['messages'];

  if (!isRecord(messages)) {
    throw new Error('The key-rotation wire contract names no messages.');
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

  return [...members].sort();
}

const WIRE_CONTRACT: unknown = JSON.parse(
  readFileSync(WIRE_CONTRACT_PATH, 'utf8'),
);

const BEGIN_REQUEST_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'beginRotationRequest',
);
const SEAL_REQUEST_MEMBERS = wireMembers(WIRE_CONTRACT, 'rotationSealRequest');
const BEGUN_RESPONSE_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'keyRotationBegunResponse',
);
const INVENTORY_MEMBERS = wireMembers(WIRE_CONTRACT, 'rotationInventory');
const CHUNK_REQUEST_MEMBERS = wireMembers(WIRE_CONTRACT, 'resealChunkRequest');
const ACCOUNT_ENTRY_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'resealedAccountEntry',
);
const PAYEE_ENTRY_MEMBERS = wireMembers(WIRE_CONTRACT, 'resealedPayeeEntry');
const CATEGORY_GROUP_ENTRY_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'resealedCategoryGroupEntry',
);
const CATEGORY_ENTRY_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'resealedCategoryEntry',
);
const TRANSACTION_ENTRY_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'resealedTransactionEntry',
);
const COMPLETION_REQUEST_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'completeRotationRequest',
);
const STATE_RESPONSE_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'keyRotationStateResponse',
);
const STAGED_ROTATION_MEMBERS = wireMembers(
  WIRE_CONTRACT,
  'stagedRotationResponse',
);
const STAGED_SEAL_MEMBERS = wireMembers(WIRE_CONTRACT, 'stagedSealResponse');

// ---------------------------------------------------------------------------
// Fixtures, every one of them `satisfies` its declared type.
//
// **That keyword is what makes a member-set comparison say anything about the
// client.** A type is erased before a line of this file runs, so nothing here
// can reflect over one; what `satisfies` does instead is tie the literal to the
// type in both directions at compile time — a member the type gained and the
// literal lacks fails to compile, and so does a member the literal carries and
// the type does not. With that knot tied, comparing the literal's own keys
// against the artifact is comparing the *type's* members against the artifact.
//
// Nothing below is a real envelope and nothing opens one. This service holds no
// key and runs no cipher: what a fixture has to be is text of the right member,
// which is the whole of what a transport can be wrong about.
const SEAL = {
  factorId: 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6',
  encapsulatedAccountKeys: 'AQIDBAUGBwgJCgsMDQ4PEA',
} satisfies RotationSealBody;

const SECOND_SEAL = {
  factorId: '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0',
  encapsulatedAccountKeys: 'EBESExQVFhcYGRobHB0eHw',
} satisfies RotationSealBody;

const BEGIN_BODY = {
  rotationId: '7b3b6f2a-1c4d-4e5f-8a9b-0c1d2e3f4a5b',
  manifest: 'ICEiIyQlJicoKSorLC0uLw',
  rotationEpoch: 2,
  // Two, not one. A rotation stages one seal per factor the account holds, and
  // the server refuses a set that is not exactly that set in both directions —
  // so a service that forwarded the head of the array would answer every
  // begin with `factor_set_moved` and nothing here would say why.
  seals: [SEAL, SECOND_SEAL],
  credentialId: 'MDEyMzQ1Njc4OTo7PD0-Pw',
  clientDataJson: 'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0In0',
  authenticatorData: 'QUJDREVGR0hJSktMTU5PUA',
  signature: 'UVJTVFVWV1hZWmFiY2RlZg',
  userHandle: 'Z2hpamtsbW5vcHFyc3R1dg',
} satisfies BeginRotationRequestBody;

const ACCOUNT_ENTRY = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'YWNjb3VudC1uYW1lLWVudmVsb3Bl',
  nameKey: 'YWNjb3VudC1uYW1lLWluZGV4',
} satisfies ResealedNamedRowBody;

const PAYEE_ENTRY = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'cGF5ZWUtbmFtZS1lbnZlbG9wZQ',
  nameKey: 'cGF5ZWUtbmFtZS1pbmRleA',
} satisfies ResealedNamedRowBody;

const CATEGORY_GROUP_ENTRY = {
  id: '33333333-3333-4333-8333-333333333333',
  name: 'Z3JvdXAtbmFtZS1lbnZlbG9wZQ',
  nameKey: 'Z3JvdXAtbmFtZS1pbmRleA',
  description: 'Z3JvdXAtbm90ZS1lbnZlbG9wZQ',
} satisfies ResealedDescribedRowBody;

// The note-less half of the same arm, and it is a fixture rather than a
// flourish: `description: null` is the spelling this client sends for a row
// that holds no note, and it has to be a *present* member or the arm's member
// set would depend on which rows a chunk happened to name.
const CATEGORY_ENTRY = {
  id: '44444444-4444-4444-8444-444444444444',
  name: 'Y2F0ZWdvcnktbmFtZS1lbnZlbG9wZQ',
  nameKey: 'Y2F0ZWdvcnktbmFtZS1pbmRleA',
  description: null,
} satisfies ResealedDescribedRowBody;

const TRANSACTION_ENTRY = {
  id: '55555555-5555-4555-8555-555555555555',
  description: 'dHhuLW5vdGUtZW52ZWxvcGU',
} satisfies ResealedTransactionBody;

const CHUNK_BODY = {
  rotationId: BEGIN_BODY.rotationId,
  accounts: [ACCOUNT_ENTRY],
  payees: [PAYEE_ENTRY],
  categoryGroups: [CATEGORY_GROUP_ENTRY],
  categories: [CATEGORY_ENTRY],
  transactions: [TRANSACTION_ENTRY],
} satisfies ResealChunkRequestBody;

const COMPLETION_BODY = {
  rotationId: BEGIN_BODY.rotationId,
} satisfies CompleteRotationRequestBody;

const INVENTORY = {
  accounts: 3,
  payees: 41,
  categoryGroups: 5,
  categories: 22,
  transactions: 1904,
  budgets: 1,
} satisfies RotationInventoryDto;

const BEGUN = {
  inventory: INVENTORY,
  maxChunkBytes: 262144,
  startedAtUtc: '2026-09-22T11:04:59.123456Z',
} satisfies KeyRotationBegunDto;

const STAGED = {
  rotationId: BEGIN_BODY.rotationId,
  stagedRotationEpoch: 2,
  stagedManifest: BEGIN_BODY.manifest,
  startedAtUtc: '2026-09-22T11:04:59.1234567Z',
  inventory: INVENTORY,
  maxChunkBytes: 262144,
  seals: [SEAL, SECOND_SEAL],
} satisfies StagedRotationDto;

const STAGED_STATE = { rotation: STAGED } satisfies KeyRotationStateDto;

const NOTHING_STAGED = { rotation: null } satisfies KeyRotationStateDto;

// The body a request actually carried, as an object this file may read members
// off. `HttpRequest.body` is `unknown` to this spec, and a cast would let a
// service that posted a string, an array or `null` satisfy every member
// comparison below by comparing nothing at all.
function objectBodyOf(body: unknown, what: string): Record<string, unknown> {
  if (!isRecord(body)) {
    throw new Error(`The ${what} request carried no object body.`);
  }

  return body;
}

// One arm of a chunk, as objects, refusing anything that is not a list of them.
function entriesOf(
  body: Record<string, unknown>,
  arm: string,
): readonly Record<string, unknown>[] {
  const entries = body[arm];

  if (!Array.isArray(entries) || entries.length === 0) {
    throw new Error(`The chunk request carried no ${arm} entries.`);
  }

  return entries.map((entry: unknown, index) =>
    objectBodyOf(entry, `${arm}[${index}] of the chunk`),
  );
}

describe('KeyRotationApiService', () => {
  let http: HttpTestingController;
  let api: KeyRotationApiService;

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
    api = TestBed.inject(KeyRotationApiService);
  });

  afterEach(() => http.verify());

  // -------------------------------------------------------------------------
  // The begin.
  it('posts a begin to the rotation route', () => {
    // Arrange
    let received: KeyRotationBegunDto | undefined;

    // Act
    api.beginRotation(BEGIN_BODY).subscribe((value) => (received = value));
    const request = http.expectOne(ROTATION_URL);

    // Assert
    expect(request.request.method).toBe('POST');
    // The begin is made by a browser that believes it holds a session, so a 401
    // is that session having ended — the fact `sessionExpiryInterceptor` owns.
    // Marked `EXPECTS_UNAUTHENTICATED`, every one of these four routes would
    // swallow it, and somebody would sit on a rotation screen whose every call
    // is refused with nothing saying why.
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);

    request.flush(BEGUN);
    expect(received).toEqual(BEGUN);
  });

  it('posts the begin members the wire contract names and no others', () => {
    // Arrange
    api.beginRotation(BEGIN_BODY).subscribe();
    const request = http.expectOne(ROTATION_URL);

    // Act
    const body = objectBodyOf(request.request.body, 'begin');

    // Assert
    // Sorted lists on both sides, which is a set equality in both directions by
    // construction: it refuses a member this client posts and the artifact does
    // not name, *and* a member the artifact names and this client does not post.
    // A subset check would pass on a client that had grown a member the server
    // binds nowhere — which is the failure this artifact exists for.
    expect(
      Object.keys(body).sort(),
      'The begin body disagrees with the beginRotationRequest members in ' +
        'docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(BEGIN_REQUEST_MEMBERS);

    request.flush(BEGUN);
  });

  it('posts every seal under the members the wire contract names', () => {
    // Arrange
    api.beginRotation(BEGIN_BODY).subscribe();
    const request = http.expectOne(ROTATION_URL);
    const body = objectBodyOf(request.request.body, 'begin');

    // Act
    const seals = entriesOf(body, 'seals');

    // Assert
    // Both of them, and never the head alone: the seals are built in one loop
    // by whatever drives this service, and a census of entry zero is exactly
    // what would not see a member added conditionally further down it.
    expect(seals).toHaveLength(2);

    for (const [index, seal] of seals.entries()) {
      expect(
        Object.keys(seal).sort(),
        `Seal ${index} disagrees with the rotationSealRequest members in ` +
          'docs/business-logic/vectors/key-rotation-wire-v1.json.',
      ).toEqual(SEAL_REQUEST_MEMBERS);
    }

    request.flush(BEGUN);
  });

  it('reads a begun rotation under the members the wire contract names', () => {
    // Arrange
    let received: KeyRotationBegunDto | undefined;

    // Act
    api.beginRotation(BEGIN_BODY).subscribe((value) => (received = value));
    http.expectOne(ROTATION_URL).flush(BEGUN);

    // Assert
    expect(
      Object.keys(BEGUN).sort(),
      'KeyRotationBegunDto disagrees with the keyRotationBegunResponse ' +
        'members in docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(BEGUN_RESPONSE_MEMBERS);
    expect(
      Object.keys(INVENTORY).sort(),
      'RotationInventoryDto disagrees with the rotationInventory members in ' +
        'docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(INVENTORY_MEMBERS);
    // The inventory is the client's progress denominator, one bar per table. A
    // transport that lifted `maxChunkBytes` out and dropped the nesting would
    // satisfy both censuses above and hand its caller a rotation with no
    // denominator at all.
    expect(received).toEqual(BEGUN);
  });

  // -------------------------------------------------------------------------
  // The chunk.
  it('posts a chunk to the chunks route and reads its empty answer', () => {
    // Arrange
    let completed = false;
    let failure: unknown;

    // Act
    api.resealRows(CHUNK_BODY).subscribe({
      complete: () => (completed = true),
      error: (error: unknown) => (failure = error),
    });
    const request = http.expectOne(CHUNKS_URL);

    // Assert
    expect(request.request.method).toBe('POST');
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);

    // 204 and an empty body, which is what the route answers. A method typed
    // for a body would publish `null` as a value its caller could branch on.
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(failure).toBeUndefined();
    expect(completed).toBe(true);
  });

  it('posts the chunk members the wire contract names and no others', () => {
    // Arrange
    api.resealRows(CHUNK_BODY).subscribe();
    const request = http.expectOne(CHUNKS_URL);

    // Act
    const body = objectBodyOf(request.request.body, 'chunk');

    // Assert
    expect(
      Object.keys(body).sort(),
      'The chunk body disagrees with the resealChunkRequest members in ' +
        'docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(CHUNK_REQUEST_MEMBERS);

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it.each([
    { arm: 'accounts', members: ACCOUNT_ENTRY_MEMBERS },
    { arm: 'payees', members: PAYEE_ENTRY_MEMBERS },
    { arm: 'categoryGroups', members: CATEGORY_GROUP_ENTRY_MEMBERS },
    { arm: 'categories', members: CATEGORY_ENTRY_MEMBERS },
    { arm: 'transactions', members: TRANSACTION_ENTRY_MEMBERS },
  ])(
    'posts $arm entries under the members the wire contract names',
    ({ arm, members }) => {
      // Arrange
      api.resealRows(CHUNK_BODY).subscribe();
      const request = http.expectOne(CHUNKS_URL);

      // Act
      const entries = entriesOf(
        objectBodyOf(request.request.body, 'chunk'),
        arm,
      );

      // Assert
      // Five arms and five member sets, told apart rather than counted: a payee
      // carries a name and its index, a category a note beside them, a
      // transaction only the note. One shared entry type across all five would
      // post a `name` on a transaction the server binds nowhere and would drop
      // the note from a category that holds one.
      for (const [index, entry] of entries.entries()) {
        expect(
          Object.keys(entry).sort(),
          `${arm}[${index}] disagrees with its entry members in ` +
            'docs/business-logic/vectors/key-rotation-wire-v1.json.',
        ).toEqual(members);
      }

      request.flush(null, { status: 204, statusText: 'No Content' });
    },
  );

  // -------------------------------------------------------------------------
  // The completion.
  it('posts a completion naming the run and nothing else', () => {
    // Arrange
    let completed = false;

    // Act
    api
      .completeRotation(COMPLETION_BODY)
      .subscribe({ complete: () => (completed = true) });
    const request = http.expectOne(COMPLETION_URL);

    // Assert
    expect(request.request.method).toBe('POST');
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
    // One member, and the artifact is what holds that it stays one. A
    // `manifest`, a `rotationEpoch` or a `seals` array added here would bind on
    // the other side, be forwarded nowhere, and redden nothing — a second
    // statement of values the begin already put on file, able to disagree with
    // the staged one at the one moment a disagreement cannot be undone.
    expect(
      Object.keys(objectBodyOf(request.request.body, 'completion')).sort(),
      'The completion body disagrees with the completeRotationRequest ' +
        'members in docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(COMPLETION_REQUEST_MEMBERS);

    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(completed).toBe(true);
  });

  // -------------------------------------------------------------------------
  // The resume read.
  it('reads a staged run back from the rotation route', () => {
    // Arrange
    let received: KeyRotationStateDto | undefined;

    // Act
    api.getRotationState().subscribe((value) => (received = value));
    const request = http.expectOne(ROTATION_URL);

    // Assert
    expect(request.request.method).toBe('GET');
    expect(request.request.responseType).toBe('json');
    expect(request.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);

    request.flush(STAGED_STATE);
    expect(received).toEqual(STAGED_STATE);
  });

  it('reads the staged run under the members the wire contract names', () => {
    // Arrange
    let received: KeyRotationStateDto | undefined;

    // Act
    api.getRotationState().subscribe((value) => (received = value));
    http.expectOne(ROTATION_URL).flush(STAGED_STATE);

    // Assert
    expect(
      Object.keys(STAGED_STATE).sort(),
      'KeyRotationStateDto disagrees with the keyRotationStateResponse ' +
        'members in docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(STATE_RESPONSE_MEMBERS);
    expect(
      Object.keys(STAGED).sort(),
      'StagedRotationDto disagrees with the stagedRotationResponse members ' +
        'in docs/business-logic/vectors/key-rotation-wire-v1.json.',
    ).toEqual(STAGED_ROTATION_MEMBERS);

    for (const [index, seal] of STAGED.seals.entries()) {
      expect(
        Object.keys(seal).sort(),
        `Staged seal ${index} disagrees with the stagedSealResponse members ` +
          'in docs/business-logic/vectors/key-rotation-wire-v1.json.',
      ).toEqual(STAGED_SEAL_MEMBERS);
    }

    expect(received).toEqual(STAGED_STATE);
  });

  it('reads an account with nothing staged as a rotation of null', () => {
    // Arrange
    let received: KeyRotationStateDto | undefined;
    let failure: unknown;

    // Act
    api.getRotationState().subscribe({
      next: (value) => (received = value),
      error: (error: unknown) => (failure = error),
    });
    http.expectOne(ROTATION_URL).flush(NOTHING_STAGED);

    // Assert
    // The route answers 200 always and never 404, because every client beside
    // this one reads a failed read as "try again in a minute" — which for an
    // account that has simply never begun a rotation never succeeds. So
    // `rotation: null` is an ordinary answer and must not become a refusal on
    // the way past.
    expect(failure).toBeUndefined();
    expect(received).toEqual(NOTHING_STAGED);
    expect(received?.rotation).toBeNull();
  });

  // -------------------------------------------------------------------------
  // What this service is not.
  it('hands on a body it could not have read, judging nothing', () => {
    // Arrange
    const unreadable = { rotation: 'not a staged run' };
    let received: KeyRotationStateDto | undefined;
    let failure: unknown;

    // Act
    api.getRotationState().subscribe({
      next: (value) => (received = value),
      error: (error: unknown) => (failure = error),
    });
    http.expectOne(ROTATION_URL).flush(unreadable);

    // Assert
    // **This is a transport and the absence of a guard here is the decision.**
    // `MeApiService.getAccountKeys` refuses a body it cannot read because its
    // one consumer turns a failed read into a word somebody acts on, and a
    // malformed body reaching the trial loop there is read as a person
    // presenting the wrong factor. Nothing consumes this service yet, and a
    // guard written now would be a second definition of a rotation's shape
    // — written before the screen that has to say what a bad one means, and
    // therefore certain to disagree with it. What refuses key material is
    // already downstream: every envelope here is opened by
    // `openFactorKeypair` and every manifest by `openFactorManifest`, each
    // with its own alphabet, width and version.
    expect(failure).toBeUndefined();
    expect(received).toEqual(unreadable);
  });

  it('hands a refused begin to its caller unread', () => {
    // Arrange
    const problem = {
      conflictKind: 'factor_set_moved',
      title: 'The account’s factor set moved.',
    };
    let received: KeyRotationBegunDto | undefined;
    let failure: unknown;

    // Act
    api.beginRotation(BEGIN_BODY).subscribe({
      next: (value) => (received = value),
      error: (error: unknown) => (failure = error),
    });
    http
      .expectOne(ROTATION_URL)
      .flush(problem, { status: 409, statusText: 'Conflict' });

    // Assert
    // **The conflict is not interpreted here, and `write-outcome.ts` is why.**
    // That module is the one place in this client that reads a refusal out of
    // an API answer, it routes over the problem document rather than the
    // status, and its words are the design book's states. A second reading
    // taken at this boundary would be a second vocabulary for the same wire
    // token — and the one that drifts, because the screen renders the other.
    // So what a caller gets is the answer, whole.
    expect(received).toBeUndefined();
    expect(failure).toBeInstanceOf(HttpErrorResponse);
    expect((failure as HttpErrorResponse).status).toBe(409);
    expect((failure as HttpErrorResponse).error).toEqual(problem);
  });
});
