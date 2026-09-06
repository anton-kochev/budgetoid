// The other half of the front door, and the half that never leaves this
// product: a returning person proves who they are to their own authenticator,
// and the identity provider is not asked anything at all.
//
// Until this commit the only way into an account was through Google — the
// welcome screen offered one control and it redirected off-site. That is the
// claim `signs a returning visitor in without touching the identity provider`
// exists to make executable, and it is the reason the whole story is worth
// shipping: an account that can only be opened by a third party is an account
// that third party can close.
//
// Both legs are **anonymous**, which is not a relaxation but a definition —
// sign-in is the exchange that runs before anybody is signed in. That single
// fact shapes three of the tests below: the requests carry
// `EXPECTS_UNAUTHENTICATED` so a 401 is read as this route's verdict rather
// than as a session ending, the 401 is answered with one word whatever caused
// it, and no request may leave for any origin but the API's.
//
// Driven through the real `HttpClient` over the testing backend, with one stub:
// `WebauthnCeremonyService`, because `available()` and `assertPasskey()` both
// touch `navigator.credentials`, which this runner does not implement. Nothing
// below that seam is replaced.
//
// **`AccountKeyCustodyService` is deliberately *not* the second stub.** It is
// the real root-provided instance with a spy laid over `unlock` that calls
// through, and the difference decides whether one of the tests below is worth
// anything. A stub would swallow the request custody makes — and that request
// is exactly what `signs a returning visitor in without touching the identity
// provider` has to keep counting, because a census that stops seeing a request
// the flow makes is no longer a census. Spied and called through, the same
// object answers both questions: what the flow handed over, and what left the
// browser because of it. Only the two cases that need `unlock` to *fail* or to
// be *counted alone* override the implementation, and each says so where it
// does it.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { isSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import {
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
  type MockInstance,
} from 'vitest';
// The two halves of the retention walk that cannot be written as a value walk.
// Shared with `account-unlock.service.spec.ts`, which holds the same rule about
// the other flow that touches a key-encryption key — see the module's own
// header for why one copy and not two.
import { moduleSurface, ownFunctionsOf } from '../../testing/retention-walk';
import { SignInService } from './sign-in.service';
// The module itself, so the retention walk can look at what it exports and at
// the statics of what it exports. A field is not the only place a key can be
// parked.
import * as signInModule from './sign-in.service';

const API_ORIGIN = 'https://api.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/assertion/options`;
const ASSERTION_URL = `${API_ORIGIN}/api/passkeys/assertion`;
// The third address a sign-in reaches, and the newest. It is not part of
// authenticating anybody: the session is already established by the time this
// leaves, and it is `AccountKeyCustodyService` going to fetch the envelopes the
// key-encryption key derived a moment ago is the only thing that can open.
const ACCOUNT_KEYS_URL = `${API_ORIGIN}/api/me/account-keys`;
// The fourth, and it is nothing to do with authenticating anybody either.
// `SessionService.established()` reads the budget this browser is operating
// inside, because it is the fourth field of every blind-index message and
// nothing in the assertion's answer carries it — without it every write on
// every content screen answers `unreachable` for the rest of a session that
// began with a sign-in rather than with a cold load. It is not awaited, so the
// navigation below does not wait for it.
const ME_URL = `${API_ORIGIN}/api/me`;

// What `POST /api/passkeys/assertion/options` answers with: four members and
// deliberately not a fifth. **There is no `allowCredentials` and none may be
// added** — a populated one would turn this endpoint into an
// account-enumeration oracle, answering "which passkeys does this browser's
// owner have here?" to anybody who asks. `satisfies` rather than an
// annotation, so a member read out of this fixture is the value written here
// rather than `string`.
const REQUEST_OPTIONS = {
  challenge: 'QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8',
  rpId: 'budgetoid.app',
  timeout: 120_000,
  userVerification: 'required',
} satisfies PasskeyRequestOptionsJson;

// What the ceremony hands back, member for member as `CompleteAssertionCommand`
// declares it. Nothing here is under test: these five travel to the server
// unchanged, and `sends the credential the authenticator signed with` is the
// test that says so.
const ASSERTION_PAYLOAD = {
  credentialId: 'AQIDBAUGBwgJCgsMDQ4PEA',
  clientDataJson:
    'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0IiwiY2hhbGxlbmdlIjoiUUVGQ1EwUkZSa2RJ' +
    'U1VwTFRFMU9UMUJSVWxOVVZWWlhXRmxhVzF4ZFhsOCIsIm9yaWdpbiI6Imh0dHBz' +
    'Oi8vYnVkZ2V0b2lkLmFwcCIsImNyb3NzT3JpZ2luIjpmYWxzZX0',
  authenticatorData: 'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8',
  signature: 'MEUCIQD-YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNA',
  userHandle: 'EBESExQVFhcYGRobHB0eHw',
} satisfies PasskeyAssertionPayload;

// The body the 200 carries. Declared realistically and read by nothing: the
// `Set-Cookie` on this response is what authenticates every later request, and
// a client that published a session off a JSON member would be re-deciding,
// from a body it cannot verify, a fact the cookie has already settled.
const SESSION_BODY = {
  kind: 'full',
  expiresAtUtc: '2026-08-20T09:00:00Z',
};

// A cause the server would never actually send, planted in the 401 body so that
// `says one thing for every refusal` has something to look for. The real
// endpoint answers one fixed sentence and no cause at all
// (`PasskeyVerificationExceptionHandler`), which is precisely why this fixture
// is the *opposite* of the contract: if a distinguishing cause ever did arrive,
// nothing on this service may keep it or hand it on.
const REFUSAL_CAUSE = 'no credential is registered for that user handle';

// Every member of the provider service, so `calledMembersOf` can name which one
// was reached rather than only that one was. Listed rather than derived: the
// point of the census is to be a list somebody has to extend deliberately when
// `AuthService` grows a member.
type ProviderStub = Readonly<Record<string, Mock>>;

function providerStub(): ProviderStub {
  return {
    initialize: vi.fn(),
    isAuthenticated: vi.fn(),
    providerEmail: vi.fn(),
    signIn: vi.fn(),
    forgetProviderToken: vi.fn(),
    signOut: vi.fn(),
  };
}

function calledMembersOf(provider: ProviderStub): readonly string[] {
  return Object.entries(provider)
    .filter(([, member]) => member.mock.calls.length > 0)
    .map(([name]) => name);
}

// The key the account's wrapped envelopes open under, imported rather than
// derived because deriving one needs a PRF output and no authenticator exists
// here. `extractable: false` matches what `keyEncryptionKeyFromPasskey`
// produces, so nothing above that seam can start depending on reading its
// bytes.
function importKeyEncryptionKey(): Promise<CryptoKey> {
  // Structured rather than zero-filled, so a byte that ends up somewhere it
  // should not be is visible in a failure.
  const bytes = new Uint8Array(32);

  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = 0x20 + index;
  }

  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// The flow is driven by a `void` method over a promise, so there is no promise
// to await from outside — the observable effects arrive some number of
// microtasks later. Polling a public reading is the honest way to wait for
// them: it makes no claim about how many awaits the implementation happens to
// contain today, and a flow that never gets there fails with a sentence naming
// what never arrived rather than with a null dereference.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Timed out waiting for ${what}.`);
}

// A plain object or an array, and nothing else. The walk below runs over a
// service instance whose own properties include the collaborators it was
// injected with — an `HttpClient`, a `Router`, the testing backend — and
// descending into those would be walking most of Angular, with cycles.
// Recursing into data and stopping at anything with a prototype of its own is
// the line that keeps the walk about *this* service's state.
function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const prototype: unknown = Object.getPrototypeOf(value);

  return prototype === Object.prototype || prototype === null;
}

// Every place in a value graph where something the predicate recognises is
// sitting, with the path it was found at so a failure names where rather than
// only that.
//
// Signals are **called**, because a value held in one is as reachable as a
// value held in a field — one `effect()` away from a log line. Other functions
// are not: a class method lives on the prototype and never reaches this walk,
// but calling an arbitrary own function would be running production code the
// test did not mean to run.
function findings(
  value: unknown,
  path: string,
  matches: (candidate: unknown) => boolean,
): readonly string[] {
  if (matches(value)) {
    return [path];
  }

  if (isSignal(value)) {
    return findings(value(), `${path}()`, matches);
  }

  if (Array.isArray(value)) {
    return value.flatMap((entry: unknown, index) =>
      findings(entry, `${path}[${index}]`, matches),
    );
  }

  if (isPlainObject(value)) {
    return Object.entries(value).flatMap(([key, entry]: [string, unknown]) =>
      findings(entry, `${path}.${key}`, matches),
    );
  }

  return [];
}

// A brand check, never `instanceof CryptoKey`. It is the rule
// `webauthn-encoding.ts` states for `isArrayBuffer` and it applies here for the
// same reason: realms differ across an iframe, a worker and this test runner,
// and a narrowing that silently goes false would report "no key held" on every
// device alike — which is the answer this test is looking for and therefore the
// worst possible failure mode.
function isKeyLike(value: unknown): boolean {
  return (
    typeof value === 'object' &&
    value !== null &&
    'algorithm' in value &&
    'extractable' in value &&
    'type' in value &&
    'usages' in value
  );
}

// Anything shaped like an HTTP answer. A retained `HttpErrorResponse` is a
// class instance, so the walk stops at it rather than descending — which is
// exactly why "is this thing an answer?" has to be a predicate and not a search
// for a string inside one.
function isResponseLike(value: unknown): boolean {
  return (
    typeof value === 'object' &&
    value !== null &&
    'status' in value &&
    'statusText' in value
  );
}

function saying(needle: string): (candidate: unknown) => boolean {
  return (candidate: unknown) =>
    typeof candidate === 'string' && candidate.includes(needle);
}

// The service's own state, as a plain object the walk can descend into. Spread
// rather than passed, because the instance carries a prototype and the walk
// deliberately stops at those.
//
// **This cannot see a `#private` field and does not claim to.** What it holds is
// the shape of the state: TypeScript's `private` is a compile-time word, so an
// ordinary field survives into the runtime as an own property and is found
// here — which covers the way a retained value would actually be written.
function stateOf(service: SignInService): Record<string, unknown> {
  return { ...service };
}

// **The closure half of the walk and the module half both live in
// `src/testing/retention-walk.ts`**, imported at the top of this file rather
// than written out here.
//
// They were written out here, byte for byte, and in
// `account-unlock.service.spec.ts` as well — two copies of one walk, with two
// different comments explaining them. The drift that costs something is not the
// comments: it is a walk that stops reaching a shape in one file while its twin
// still reaches it, in two suites that never meet and with nothing anywhere
// going red. What stays here is this file's own **positive control**, in
// `parks the key in no closure and no module binding` below, because a walk is
// worth something only to a suite that has watched it find a planted value —
// and that is a fact about a suite rather than about the function.

describe('SignInService', () => {
  let http: HttpTestingController;
  let service: SignInService;
  let session: SessionService;
  // The real service, spied where the flow touches it. What the spy adds over
  // reading `custody.status()` is *which key* and *when*: the status word says
  // an attempt started, and the whole of the rule below is that the attempt
  // started from the object the authenticator derived and started after the
  // server said yes.
  let custody: AccountKeyCustodyService;
  let unlock: MockInstance<AccountKeyCustodyService['unlock']>;
  let provider: ProviderStub;
  // The key-encryption key the authenticator would derive. Held where the tests
  // can reach it so that `asks the authenticator for the value that opens the
  // account` can plant it as a control: a walk that finds nothing anywhere
  // would pass that test while looking at nothing.
  let keyEncryptionKey: CryptoKey;
  // What the device answers, read from the test rather than written into the
  // stub, because four of the tests below are about what happens when it says
  // no. Both are set to the willing answer before every test, so a test that
  // touches neither sees the fixture unchanged.
  let ceremonyAvailable: boolean;
  let ceremonyOutcome: PasskeyCeremonyResult<PasskeyAssertionCeremony>;
  // The options the ceremony was handed, in call order. The server's options
  // are the challenge this assertion is signed over, so "was the authenticator
  // asked" and "was it asked the right thing" are one recording.
  let asserted: PasskeyRequestOptionsJson[];
  let navigations: string[];
  // Every request the backend saw, in order. `HttpTestingController` keeps no
  // log of answered requests, so the census that says "nothing left for any
  // other origin" has to be built as the flow is driven.
  let seen: string[];

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    ceremonyAvailable = true;
    ceremonyOutcome = {
      ok: true,
      value: { payload: ASSERTION_PAYLOAD, keyEncryptionKey },
    };
    asserted = [];
    navigations = [];
    seen = [];
    provider = providerStub();

    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'assertPasskey'
    > = {
      available: () => ceremonyAvailable,
      assertPasskey: (
        options: PasskeyRequestOptionsJson,
      ): Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>> => {
        asserted.push(options);

        return Promise.resolve(ceremonyOutcome);
      },
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // Overriding the router the line above provides, rather than declaring
        // routes: the flow navigates to `/app` on the 200, and a real router
        // with no matching route rejects that navigation into a promise nothing
        // awaits. Recording the address also gives the tests below a way to say
        // that nothing navigated.
        {
          provide: Router,
          useValue: {
            navigateByUrl: (url: string): Promise<boolean> => {
              navigations.push(url);

              return Promise.resolve(true);
            },
          },
        },
        // The provider service, present so that it *can* be reached and
        // recorded if anything ever reaches for it. Nothing in this flow should
        // — which makes this a standing negative rather than a stub with a
        // behaviour, and the reason it is listed here at all is that a service
        // that started calling it would otherwise fail on an unresolved
        // dependency and be "fixed" by providing one.
        { provide: AuthService, useValue: provider },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        // Component-provided in production — the welcome screen owns it, so the
        // flow and anything it holds die with the screen — which is why it is
        // listed here rather than resolved from the root injector.
        SignInService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
    // Resolved before the service under test, though it need not be: the flow
    // holds the *instance* and the spy is laid over that instance's method, so
    // the order of these three lines changes nothing. Written this way because
    // a reader should not have to work that out.
    custody = TestBed.inject(AccountKeyCustodyService);
    // Calling through, which is the default and is load-bearing here rather
    // than incidental — see the header. `mockImplementation` appears twice
    // below and nowhere else.
    unlock = vi.spyOn(custody, 'unlock');
    service = TestBed.inject(SignInService);
  });

  // The press, and the challenge that comes back from it.
  async function driveToOptions(): Promise<TestRequest> {
    service.signIn();

    const request = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the assertion options',
    );
    seen.push(request.request.urlWithParams);

    return request;
  }

  // The same, through the ceremony, up to the request that carries the
  // assertion. Left outstanding: answering it is what each test does next, and
  // the answer is the thing most of them are about.
  async function driveToAssertion(): Promise<TestRequest> {
    const options = await driveToOptions();
    options.flush(REQUEST_OPTIONS);

    const request = await eventually(
      () => http.match(ASSERTION_URL)[0] ?? null,
      'the assertion request',
    );
    seen.push(request.request.urlWithParams);

    return request;
  }

  // The whole point of the story, made executable. Everything else in this file
  // is a rule about how the flow behaves when something goes wrong; this is the
  // one assertion that says what the flow is *for*.
  //
  // An account whose only door is somebody else's identity service is an
  // account that service can close — by suspending the address, by changing
  // what it charges for, by going away. After this commit a returning person
  // holds their own authenticator and Budgetoid holds their passkey, and the
  // provider is not part of the exchange at all: it is contacted once, at
  // creation, to confirm an email address, and never again.
  it('signs a returning visitor in without touching the identity provider', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(SESSION_BODY);

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // **The third and fourth requests, and this census moved to account for
    // each of them rather than relaxing to stop seeing them.** Two requests were
    // the whole of a sign-in until custody was wired, and the line below said
    // so. The 200 now hands the key-encryption key to
    // `AccountKeyCustodyService`, which goes and reads this session's wrapped
    // envelopes; and `SessionService.established()` reads the budget every
    // blind-index message folds in. Both leave this browser, so both are ones
    // this file owes an entry for. The alternative on offer each time was to stub
    // the collaborator away, which would have made the old line pass again by
    // making the census blind to a request the flow makes. Matched, named and put
    // through the same origin check as the other two; left outstanding on
    // purpose, because whether either is ever answered is
    // `does not wait for the keys to sign anybody in`'s business.
    const accountKeys = await eventually(
      () => http.match(ACCOUNT_KEYS_URL)[0] ?? null,
      "the read of this session's wrapped account keys",
    );
    seen.push(accountKeys.request.urlWithParams);

    const budget = await eventually(
      () => http.match(ME_URL)[0] ?? null,
      'the read of the budget this session is operating inside',
    );
    seen.push(budget.request.urlWithParams);

    // Assert
    // Four requests left this browser and all four went to Budgetoid's own
    // API. Origins, not prefixes: `https://api.test.attacker.example` is a name
    // anybody can register and `startsWith` admits it, which is the rule
    // `apiCredentialsInterceptor` states one layer down.
    //
    // Compared as a set rather than in order, because the last two are started
    // by two collaborators one statement apart and neither waits for the other:
    // pinning which of them reaches the backend first would be a red bar over a
    // scheduling detail nothing depends on.
    expect([...seen].sort()).toEqual(
      [OPTIONS_URL, ASSERTION_URL, ACCOUNT_KEYS_URL, ME_URL].sort(),
    );

    for (const url of seen) {
      expect(new URL(url).origin, `${url} is not this API's origin.`).toBe(
        API_ORIGIN,
      );
    }

    // And nothing else left at all. `match` removes what it returns, so an
    // empty list here is every request the flow made beyond the three above —
    // a discovery document, a token endpoint, a profile picture.
    expect(http.match(() => true)).toHaveLength(0);

    // The provider service, untouched. The other way the provider gets
    // contacted is not a request this client composes but a redirect it asks
    // `AuthService` for, and a census over its members is the only thing that
    // can see one.
    expect(calledMembersOf(provider)).toEqual([]);

    // And the visitor is in. The session is published before the navigation for
    // the reason registration states: navigate first and `authGuard` judges
    // `/app` against a stale `'anonymous'` and bounces the person straight back
    // out of the account they just opened.
    expect(session.status()).toBe('authenticated');
    expect(navigations).toEqual(['/app']);
    expect(service.failure()).toBeNull();
    expect(service.busy()).toBe(false);
  });

  // Two halves of one decision, and each is invisible without the other.
  //
  // The cheaper sign-in — authenticate, derive nothing — is the one a reader
  // will propose, because the assertion the server verifies needs no PRF output
  // at all. It would authenticate the person and leave every row on the account
  // unreadable the day encryption lands: the wrapped account keys open under
  // exactly this value and under nothing else.
  //
  // And having derived it, this service must not keep it. Nothing on this
  // screen has a use for the key yet, so holding it is holding the account's
  // master key for no reason, in a place one `effect()` or one devtools panel
  // can read.
  it('asks the authenticator for the value that opens the account', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(SESSION_BODY);

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // Asked once, and asked with the server's own options — the challenge this
    // assertion is signed over. A ceremony run against options this client
    // invented is an assertion no server would accept.
    expect(asserted).toEqual([REQUEST_OPTIONS]);

    // And nothing reachable from outside is a key. The walk descends into
    // signals and plain data and stops at collaborators, so what it covers is
    // this service's own state.
    expect(findings(stateOf(service), 'state', isKeyLike)).toEqual([]);

    // The two controls, and they are why the assertion above means anything: a
    // walk that found nothing anywhere would satisfy it in silence. Both shapes
    // a retained key would actually take — a field, and a signal a template
    // could render — are planted and both are found.
    expect(findings({ held: keyEncryptionKey }, 'probe', isKeyLike)).toEqual([
      'probe.held',
    ]);
    expect(
      findings({ held: signal(keyEncryptionKey) }, 'probe', isKeyLike),
    ).toEqual(['probe.held()']);
  });

  // The two shapes the test above cannot see, closed here rather than left as a
  // gap the reader has to know about.
  //
  // **The residue, stated as a bound rather than as a reassurance.** Between
  // this test and the one above, what is reached is: own fields, the current
  // value of own signals, plain data nested in either, every export of this
  // module, the enumerable statics on those exports, and the *container* an
  // instance-held closure would need. What is **not** reached is: a value
  // captured by a closure held somewhere other than this instance — a pending
  // promise's frame, a timer the platform holds, a callback handed to a
  // collaborator; a plain `const` local, which dies with the frame that
  // declared it and is therefore not retention at all; a `#private` field,
  // which is unreachable by the language; a module-level `let` that is **not
  // exported**, because an ES module namespace object exposes exports only; and
  // anything inside the collaborators, which the walk stops at deliberately. So
  // the claim is "no retention through the shapes this service could be written
  // to use", and it is **not** "no retention is possible".
  it('parks the key in no closure and no module binding', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(SESSION_BODY);

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    expect(
      ownFunctionsOf(service),
      'the service holds a function that could have captured the key.',
    ).toEqual([]);
    expect(
      findings(moduleSurface(signInModule), 'module', isKeyLike),
      'the module is holding a key.',
    ).toEqual([]);

    // The controls, and they are why the two assertions above mean anything.
    // Each plants the value exactly where its own defect would put it.
    expect(ownFunctionsOf({ retry: () => keyEncryptionKey })).toEqual([
      'retry',
    ]);
    // And the exclusion that keeps the closure check usable: a signal is a
    // callable and is not the container this is looking for.
    expect(ownFunctionsOf({ busy: signal(false) })).toEqual([]);
    expect(
      findings(
        // A function value carrying an enumerable own property, which is what
        // a class with a `static lastKey` is at runtime.
        moduleSurface({
          exportedClass: Object.assign(() => undefined, {
            held: keyEncryptionKey,
          }),
        }),
        'probe',
        isKeyLike,
      ),
    ).toEqual(['probe.exportedClass.held']);
  });

  // The other end of the same sentence. The test above says this service keeps
  // no key; this one says it does not simply drop it either — the key travels,
  // once, to the one place in the client entitled to hold it, and it travels at
  // the one instant that is correct.
  it('hands the key the authenticator derived to custody, and not a moment earlier', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // The ceremony has finished and the key exists. **This is the instant a
    // reader would hand it over and the instant that would be wrong.** A
    // verified assertion sitting in an outgoing request body is not a session:
    // the server has not looked at it yet and can still answer 401. Custody
    // taken here leaves the account's keys held on a root-provided singleton
    // inside a browser the server then refused — for the life of the tab, with
    // `refused` on the screen and nothing anywhere going red.
    expect(unlock).not.toHaveBeenCalled();

    // Act
    assertion.flush(SESSION_BODY);

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // Once, and the count is not pedantry. A second call runs `#forget` again,
    // which drops both keys and republishes `'unlocking'` — so an account that
    // was open a moment ago closes and reopens, and every read taken in that
    // window is taken against a locked account.
    expect(unlock).toHaveBeenCalledTimes(1);

    // **The same object, by identity, and `toBe` rather than
    // `toHaveBeenCalledWith`.** Deep equality would compare a `CryptoKey`'s
    // three readable members — algorithm, extractability, usages — and every
    // key this door produces agrees on all three, so a service that derived a
    // *second* key, or re-imported one, or handed over the one a previous press
    // left behind, would satisfy the friendlier matcher exactly. Identity is
    // all that is checkable here, because non-extractability means no API in
    // the platform reads a key's bytes back out, and it is also all that
    // matters: this account's envelopes open under the value the
    // authenticator's PRF produced and under nothing else, so the wrong key is
    // an authenticated person whose every row is unreadable, permanently, with
    // no error naming the cause.
    expect(unlock.mock.calls[0]?.[0]).toBe(keyEncryptionKey);
  });

  it('hands nothing to custody when the server refuses the assertion', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(null, { status: 401, statusText: 'Unauthorized' });

    await eventually(() => service.failure(), 'the refusal to be published');

    // Assert
    expect(unlock).not.toHaveBeenCalled();

    // And no read went looking for envelopes, which is the half that a caller
    // reaching for `/api/me/account-keys` itself would still get wrong. A
    // browser the server has just refused holds no session it would honour, so
    // the read comes back 401 — on a request carrying no
    // `EXPECTS_UNAUTHENTICATED`, which is `sessionExpiryInterceptor` reading a
    // refused sign-in as a session that ended and navigating to the screen the
    // person is already standing on.
    expect(http.match(ACCOUNT_KEYS_URL)).toHaveLength(0);
    expect(custody.status()).toBe('locked');
  });

  // **A key that will not open is not a sign-in that failed**, and the two
  // statements the flow makes after handing custody the key are what that
  // sentence costs.
  //
  // The failure mode is not hypothetical and it is not caught by the `error`
  // callback sitting six lines away. An exception thrown out of an RxJS `next`
  // handler is **not** routed to the `error` callback beside it — it is
  // reported out of band as an unhandled error, and `next()` returns to the
  // producer as if nothing happened. What it does do is what any throw does:
  // the statements after it in the same handler never run. So an unguarded
  // custody call that threw would leave somebody holding a valid session cookie
  // stranded on `/welcome`, with `busy` already cleared and the screen saying
  // nothing at all, because as far as this service is concerned the sign-in
  // succeeded. That is the whole justification for the `try` in production, and
  // this is the test that holds it there.
  //
  // Swallowed and deliberately not published: this screen says one thing
  // however a sign-in was refused, and a sentence that varied by whether a key
  // opened would rebuild the credential-enumeration oracle the server answers
  // one byte-identical 401 to avoid being.
  it('signs a visitor in even when custody blows up', async () => {
    // Arrange
    // One of the two places in this file that replaces the implementation
    // rather than watching it, and the reason is that no arrangement of the
    // *real* service throws: `unlock` catches every way an attempt can end and
    // publishes it as a state. The rule is about the seam and not about today's
    // implementation of the far side of it.
    unlock.mockImplementation(() => {
      throw new Error('the wrapped keys could not be read');
    });

    const assertion = await driveToAssertion();

    // Act
    assertion.flush(SESSION_BODY);

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // It really did throw, which is what stops this passing over a mock that
    // was never reached.
    expect(unlock).toHaveBeenCalledTimes(1);
    expect(unlock.mock.results[0]?.type).toBe('throw');

    // And the sign-in is untouched by it, in all four of the ways it could have
    // been damaged: the person is in the app, the client agrees they are signed
    // in, the screen says nothing new, and the button is live again.
    expect(navigations).toEqual(['/app']);
    expect(session.status()).toBe('authenticated');
    expect(service.failure()).toBeNull();
    expect(service.busy()).toBe(false);
  });

  // `unlock` returns `void` so that there is nothing to await, and this is what
  // that buys. Awaited, a round trip would sit on the path between a verified
  // assertion and the app — and one refactor later the `await` grows a `catch`,
  // at which point a key that did not open is an authentication that failed.
  // Only `anonymous` may bounce anybody out of an account.
  it('does not wait for the keys to sign anybody in', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(SESSION_BODY);

    const read = await eventually(
      () => http.match(ACCOUNT_KEYS_URL)[0] ?? null,
      "the read of this session's wrapped account keys",
    );

    // Assert
    // **The read is never answered.** Everything below is asserted with it
    // still outstanding, which is the only arrangement that can tell "did not
    // wait" from "waited and the answer was quick".
    expect(read.request.method).toBe('GET');
    expect(custody.status()).toBe('unlocking');

    // And the person is already inside the app, with the flow finished.
    expect(navigations).toEqual(['/app']);
    expect(session.status()).toBe('authenticated');
    expect(service.busy()).toBe(false);
    expect(service.failure()).toBeNull();

    // The signature half of it, read off the call rather than off the type. A
    // `Promise` here is a value somebody can await, and the first thing anybody
    // awaiting it would write is the `catch` the paragraph above argues must
    // not exist.
    expect(unlock.mock.results[0]?.value).toBeUndefined();
  });

  // One word, whatever happened.
  //
  // `PasskeyVerificationExceptionHandler` makes every refusal on this route
  // byte-identical on purpose: a caller able to tell "no such credential" from
  // "wrong signature" can discover which user handles are registered, one guess
  // at a time, without ever holding a credential. The server refusing to make
  // that distinction is only half of it — a client that kept a cause it *was*
  // handed, and rendered it, would put the oracle back on the screen. So this
  // test flushes a cause the real server would never send and then goes looking
  // for it.
  it('says one thing for every refusal', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    assertion.flush(
      { title: 'Unauthorized', detail: REFUSAL_CAUSE },
      { status: 401, statusText: 'Unauthorized' },
    );

    await eventually(() => service.failure(), 'the refusal to be published');

    // Assert
    expect(service.failure()).toBe('refused');
    expect(service.busy()).toBe(false);
    expect(session.status()).not.toBe('authenticated');
    expect(navigations).toEqual([]);

    // Nothing anywhere in this service's state repeats what the server said
    // about the cause…
    expect(
      findings(stateOf(service), 'state', saying(REFUSAL_CAUSE)),
      'the cause the server gave is still readable from the service.',
    ).toEqual([]);

    // …and nothing holds the answer itself, which is the shape the cause
    // actually arrives in. An `HttpErrorResponse` parked in a field is one
    // template binding away from being read out loud.
    expect(
      findings(stateOf(service), 'state', isResponseLike),
      'the service is still holding the answer it was refused with.',
    ).toEqual([]);

    // The controls for both walks.
    expect(
      findings({ held: REFUSAL_CAUSE }, 'probe', saying(REFUSAL_CAUSE)),
    ).toEqual(['probe.held']);
    expect(
      findings(
        { held: { status: 401, statusText: 'Unauthorized' } },
        'probe',
        isResponseLike,
      ),
    ).toEqual(['probe.held']);
  });

  // The inverse pairing to the registration flow's, and the two flows split the
  // same line for opposite reasons.
  //
  // There, the question was whether an account had been created; here nothing
  // is created either way, so a reader will ask why the two readings are still
  // separate. Because the next step differs and nothing else can tell a person
  // which one they are in. A refusal means *this passkey does not work here* —
  // the credential was never registered, or it is not this account's — and the
  // way forward is another way in, a recovery code or another device. An answer
  // that never came means the server is unreachable and the way forward is to
  // try the same thing again in a minute. Collapsing them sends somebody
  // hunting for a card of recovery codes over a network that blinked, and sends
  // somebody with the wrong passkey around a loop that can only ever refuse
  // them.
  describe('does not tell a refusal from an answer that never came', () => {
    it('reads an answer that never arrived as unknown', async () => {
      // Arrange
      const assertion = await driveToAssertion();

      // Act
      assertion.error(new ProgressEvent('error'), {
        status: 0,
        statusText: 'Unknown Error',
      });

      await eventually(() => service.failure(), 'the failure to be published');

      // Assert
      expect(service.failure()).not.toBe('refused');
      expect(service.failure()).toBe('unknown');
      expect(navigations).toEqual([]);
      expect(session.status()).not.toBe('authenticated');
    });

    it('reads a server that failed as unknown', async () => {
      // Arrange
      const assertion = await driveToAssertion();

      // Act
      assertion.flush(null, {
        status: 500,
        statusText: 'Internal Server Error',
      });

      await eventually(() => service.failure(), 'the failure to be published');

      // Assert
      expect(service.failure()).not.toBe('refused');
      expect(service.failure()).toBe('unknown');
      expect(navigations).toEqual([]);
      expect(session.status()).not.toBe('authenticated');
    });
  });

  it('does not let a refusal end the session', async () => {
    // Arrange
    const caught: TestRequest[] = [];

    // Act
    const options = await driveToOptions();
    caught.push(options);
    options.flush(REQUEST_OPTIONS);

    const assertion = await eventually(
      () => http.match(ASSERTION_URL)[0] ?? null,
      'the assertion request',
    );
    caught.push(assertion);

    // Assert
    // Both legs, because both are made by a browser holding no session of this
    // product's. Without the token, `sessionExpiryInterceptor` reads a 401 from
    // either as a session lapsing, declares it over and navigates to
    // `/welcome` — which is the screen the person is already standing on,
    // wondering why pressing the button reloaded the page and said nothing.
    expect(caught).toHaveLength(2);

    for (const request of caught) {
      expect(
        request.request.context.get(EXPECTS_UNAUTHENTICATED),
        `${request.request.url} does not declare itself unauthenticated.`,
      ).toBe(true);
    }
  });

  it('asks for no challenge when the browser cannot run a ceremony', () => {
    // Arrange
    ceremonyAvailable = false;

    // Act
    service.signIn();

    // Assert
    expect(service.failure()).toBe('unsupported');
    expect(service.busy()).toBe(false);

    // Not even the options leg, which is the only position that costs nothing.
    // A challenge is a nonce the server persisted, so a browser that was never
    // going to finish would otherwise spend one on its way to being told
    // exactly what it is told here for free.
    expect(http.match(() => true)).toHaveLength(0);
    expect(asserted).toEqual([]);
    expect(navigations).toEqual([]);
  });

  // Three words from the device, three words on the screen. The union carries
  // no `duplicate`: that refusal is an authenticator declining a credential
  // named in an exclusion list, and an assertion has no exclusion list to
  // decline against.
  //
  // Folded into one word, this screen tells somebody who closed the system
  // sheet that their device cannot hold the account's keys, and tells somebody
  // whose device genuinely cannot to try again on the same device forever.
  describe('says what the device did when it refuses', () => {
    it.each([
      {
        word: 'cancelled' as const,
        failure: 'cancelled' as const,
        why: 'the person closed the sheet',
      },
      {
        word: 'no-prf' as const,
        failure: 'no-prf' as const,
        why: 'the authenticator cannot derive the account keys',
      },
      {
        word: 'failed' as const,
        failure: 'ceremony-failed' as const,
        why: 'the ceremony ended in nothing usable',
      },
    ])('says $failure when $why', async ({ word, failure }) => {
      // Arrange
      ceremonyOutcome = { ok: false, failure: word };

      // Act
      const options = await driveToOptions();
      options.flush(REQUEST_OPTIONS);

      await eventually(() => service.failure(), 'the refusal to be published');

      // Assert
      expect(service.failure()).toBe(failure);
      expect(service.busy()).toBe(false);

      // Nothing was posted. A device that refused signed nothing, so there is
      // no assertion to send and a request here would be one the server can
      // only answer with the same undifferentiated 401.
      expect(http.match(ASSERTION_URL)).toHaveLength(0);
      expect(navigations).toEqual([]);
      expect(session.status()).not.toBe('authenticated');
    });
  });

  it('sends the credential the authenticator signed with', async () => {
    // Arrange
    const assertion = await driveToAssertion();

    // Act
    const body: unknown = assertion.request.body;

    // Assert
    // Deep equality, member for member. The server verifies the signature over
    // the authenticator data and a hash of the client data, so a member dropped
    // or renamed on the way out is refused with the one sentence every other
    // refusal uses — a 401 naming nothing, on a request that was correct in
    // every way a person could see.
    expect(body).toStrictEqual({ ...ASSERTION_PAYLOAD });

    // And the member census, which is the half `toStrictEqual` proves but does
    // not name. A body carrying a member the payload does not declare is the
    // shape a leak takes here: a spread of the raw credential, or the
    // extension results, or a key-encryption key parked beside the signature
    // "for the encryption epic".
    expect(Object.keys(body as object).sort()).toEqual(
      Object.keys(ASSERTION_PAYLOAD).sort(),
    );
  });
});
