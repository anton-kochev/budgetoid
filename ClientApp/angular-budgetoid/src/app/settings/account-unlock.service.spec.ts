// The flow behind the **Unlock** control on `/app/settings`, and the one
// ceremony in this product that is run for its side effect rather than for
// anything it sends. A page reload leaves the browser holding no account keys —
// nothing about them survives a document — while the session cookie is intact
// and there is nothing to re-authenticate. The person presents their passkey,
// the authenticator derives the key-encryption key, and custody opens the
// account's wrapped envelopes with it.
//
// Driven the way `sign-in.service.spec.ts` drives its flow, and for the same
// reasons. One stub, `WebauthnCeremonyService`, because the platform behind it
// does not exist under this runner. **`AccountKeyCustodyService` is deliberately
// not the second stub**: it is the real root-provided instance with a spy laid
// over `unlock` that calls through, and the difference is what makes the census
// below worth anything — a stub would swallow the request custody makes, and
// that request is exactly what `reaches the account-key route and no other
// address` counts. `mockImplementation` appears twice, in the two tests that
// need `unlock` to *throw* or to be *watched from the inside*, and each says so
// where it does it.
//
// `Router` and `SessionService` are provided as **standing negatives**. Nothing
// in this flow may reach either, and the service under test injects neither, so
// they are here to be recorded rather than to be used. A service that started
// reaching for one would otherwise fail on an unresolved dependency and be
// "fixed" by providing it.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { isSignal, signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import {
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type MockInstance,
} from 'vitest';
// The two halves of the retention walk that cannot be written as a value walk.
// Shared with `sign-in.service.spec.ts`, which holds the same rule about the
// other flow that touches a key-encryption key — see the module's own header
// for why one copy and not two.
import { moduleSurface, ownFunctionsOf } from '../../testing/retention-walk';
import { AccountUnlockService } from './account-unlock.service';
// The module itself, so the retention walk can look at what it exports and at
// the statics of what it exports. A field is not the only place a key can be
// parked.
import * as unlockModule from './account-unlock.service';

const API_ORIGIN = 'https://api.test';
// The one address this flow reaches, and it does not reach it itself: custody
// does, because the flow handed it a key. The ceremony sends nothing at all —
// its challenge is this client's own and no server ever sees it.
const ACCOUNT_KEYS_URL = `${API_ORIGIN}/api/me/account-keys`;

// Every own property the service carries, named once so the structural census
// has something to be red against. Listed rather than derived: the point of a
// pin is to be a list somebody has to extend deliberately.
//
// **`IN_FLIGHT_READING` is on this list because the reading it names now
// exists**, and the entry was added by hand on the day it did — which is the
// pin having done its job rather than a line of upkeep. The reading the service
// owes its caller — see `publishes one reading of whether either half is
// running` below — is a `computed`, so it arrives as a seventh own property and
// reddened `injects exactly two collaborators` the moment it was written. An
// eighth will do the same, and the answer is to think about it and then extend
// the list, never to derive it.
const OWN_PROPERTIES = [
  'busy',
  'busySignal',
  'ceremony',
  'custody',
  'failure',
  'failureSignal',
  'working',
];

// What the service publishes for "either half is running" — the design book's
// phrase for the busy treatment, and the name the screen's own template local
// now takes its value from.
//
// **One name, in one place, because the defect was that there were two.** The
// screen used to or custody's `unlocking` together with the flow's `busy` while
// the handler guarded on `busy` alone. The two disagreed for the whole length
// of the custody read, and the disagreement was a control that looked disabled
// and was not. The template reads this signal now; that the template reads it
// rather than reassembling it is held in `settings.component.spec.ts`, which is
// the only file that can see a template.
const IN_FLIGHT_READING = 'working';

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
    bytes[index] = 0x40 + index;
  }

  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// A promise the test releases by hand, so that "while one attempt is running"
// is an arrangement rather than a race.
interface Gate {
  readonly promise: Promise<void>;
  readonly release: () => void;
}

function gate(): Gate {
  let release: () => void = () => undefined;
  const promise = new Promise<void>((resolve) => {
    release = (): void => resolve();
  });

  return { promise, release };
}

function openGate(): Gate {
  const created = gate();
  created.release();

  return created;
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

// A member read off an instance **by name** rather than through its type.
//
// Written this way deliberately, and only for the one member this file expects
// the service to grow. Naming an absent member in TypeScript does not fail a
// test — it fails the *file*, taking every other case in it down with a
// compiler error that says nothing about which behaviour is missing. Read
// dynamically, the same case fails on an assertion that names the member, and
// then goes on failing on the behaviour once the member exists.
function memberNamed(instance: object, name: string): unknown {
  return (instance as unknown as Record<string, unknown>)[name];
}

// Lets every microtask an attempt would need run, so that "refused the press"
// is told apart from "has not got there yet".
//
// `eventually` is the wrong instrument for this and is not a substitute: it
// waits for something to arrive, and what is being waited on here is something
// that must never arrive. Twenty turns of the macrotask queue is far past the
// two awaits a second attempt needs to reach the ceremony and custody —
// measured against the failing case, which reaches the ceremony on the first.
async function settled(): Promise<void> {
  for (let turn = 0; turn < 20; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

// A plain object or an array, and nothing else. The walk below runs over a
// service instance whose own properties include the collaborators it was
// injected with, and descending into those would be walking most of Angular,
// with cycles. Recursing into data and stopping at anything with a prototype of
// its own is the line that keeps the walk about *this* service's state.
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
// are not: calling an arbitrary own function would be running production code
// the test did not mean to run, which is what `ownFunctionsOf` exists to
// address instead.
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

// **The closure half of the walk and the module half both live in
// `src/testing/retention-walk.ts`**, imported at the top of this file rather
// than written out here.
//
// They were written out here, byte for byte, and in `sign-in.service.spec.ts`
// as well — two copies of one walk, with two different comments explaining
// them. The drift that costs something is not the comments: it is a walk that
// stops reaching a shape in one file while its twin still reaches it, in two
// suites that never meet and with nothing anywhere going red. What stays here
// is this file's own **positive control**, in `keeps no key anywhere on itself`
// below, because a walk is worth something only to a suite that has watched it
// find a planted value — and that is a fact about a suite rather than about the
// function.

// Which of a spied service's members were reached, named rather than counted so
// a failure says what the flow touched.
function calledMembersOf(
  spies: Readonly<Record<string, MockInstance>>,
): readonly string[] {
  return Object.entries(spies)
    .filter(([, spy]) => spy.mock.calls.length > 0)
    .map(([name]) => name);
}

// Everything the instance was handed, as opposed to everything it made. The
// split is `isSignal`: the four signals below are this service's own state, and
// what is left is what it was injected with.
//
// Widened to `object` so the control in `injects exactly two collaborators` can
// be an ordinary literal rather than a cast.
function collaboratorsOf(instance: object): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(instance).filter(
      ([, member]: [string, unknown]) => !isSignal(member),
    ),
  );
}

// The service's own state, as a plain object the walk can descend into. Spread
// rather than passed, because the instance carries a prototype and the walk
// deliberately stops at those.
function stateOf(service: AccountUnlockService): Record<string, unknown> {
  return { ...service };
}

describe('AccountUnlockService', () => {
  let http: HttpTestingController;
  let service: AccountUnlockService;
  let session: SessionService;
  let custody: AccountKeyCustodyService;
  let unlock: MockInstance<AccountKeyCustodyService['unlock']>;
  // **Every member custody offers, spied and calling through, because "hands
  // custody nothing" is a statement about the whole surface and not about one
  // method.** `lock()` is the near miss and it is not hypothetical: it
  // publishes exactly the state a locked account is already in, so a refusal
  // branch that called it satisfies every other assertion in this file — while
  // dropping both keys of an account that was *open*. Somebody raises the
  // sheet, closes it, and the account they were reading a moment ago is shut,
  // with nothing anywhere going red. Listed rather than derived, so a member
  // added to custody is one somebody has to add here deliberately.
  let custodySpies: Record<'unlock' | 'lock' | 'adopt', MockInstance>;
  // The real method, captured before the spy is laid over it, so a test that
  // replaces the implementation can still let the flow run for real underneath
  // whatever it is watching for.
  let handOver: (keyEncryptionKey: CryptoKey) => void;
  // The stub the service is injected with, held where the structural census can
  // compare against it by identity.
  let ceremonyStub: Pick<
    WebauthnCeremonyService,
    | 'available'
    | 'assertPasskey'
    | 'createPasskey'
    | 'deriveKeyFromLocalAssertion'
  >;
  // The key-encryption key the authenticator would derive. Held where the tests
  // can reach it so that the retention walk can plant it as a control: a walk
  // that finds nothing anywhere would pass while looking at nothing.
  let keyEncryptionKey: CryptoKey;
  // What the device answers, read from the test rather than written into the
  // stub, because most of the tests below are about what happens when it says
  // no.
  let ceremonyOutcome: PasskeyCeremonyResult<CryptoKey>;
  // A rejection out of the ceremony, which no arrangement of the real service
  // produces — it answers with a result. The seam is what is under test, not
  // today's implementation of the far side of it.
  let ceremonyRejection: Error | null;
  // Held open by the tests that need an attempt to still be running.
  let ceremonyGate: Gate;
  // Which ceremonies were run, in order. Three exist and exactly one of them
  // belongs to this flow.
  let ceremonies: string[];
  let navigations: string[];
  // Every request the backend saw, in order. `HttpTestingController` keeps no
  // log of answered requests, so the census has to be built as the flow is
  // driven.
  let seen: string[];

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    ceremonyOutcome = { ok: true, value: keyEncryptionKey };
    ceremonyRejection = null;
    ceremonyGate = openGate();
    ceremonies = [];
    navigations = [];
    seen = [];

    ceremonyStub = {
      // Recorded and answered truthfully. Nothing in this flow may ask — the
      // ceremony guards `available()` itself and answers `unsupported` — and
      // the census in `runs the local ceremony…` is what says so.
      available: () => {
        ceremonies.push('available');

        return true;
      },
      // The two ceremonies a server sees. Both reject loudly rather than
      // answering, so a flow that reached for one fails with a sentence naming
      // the rule instead of passing quietly under a fixture.
      assertPasskey: () => {
        ceremonies.push('assertPasskey');

        return Promise.reject(
          new Error('The unlock flow must not run a ceremony the server sees.'),
        );
      },
      createPasskey: () => {
        ceremonies.push('createPasskey');

        return Promise.reject(
          new Error('The unlock flow must not run a ceremony the server sees.'),
        );
      },
      deriveKeyFromLocalAssertion: async () => {
        ceremonies.push('deriveKeyFromLocalAssertion');

        await ceremonyGate.promise;

        if (ceremonyRejection !== null) {
          throw ceremonyRejection;
        }

        return ceremonyOutcome;
      },
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // Overriding the router the line above provides, so that "nothing
        // navigated" is a recording rather than an absence nobody watched.
        {
          provide: Router,
          useValue: {
            navigateByUrl: (url: string): Promise<boolean> => {
              navigations.push(url);

              return Promise.resolve(true);
            },
          },
        },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremonyStub },
        // Component-provided in production — the settings screen owns it, so an
        // abandoned attempt dies with the screen — which is why it is listed
        // here rather than resolved from the root injector.
        AccountUnlockService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
    custody = TestBed.inject(AccountKeyCustodyService);
    handOver = custody.unlock.bind(custody);
    // Calling through, which is the default and is load-bearing here rather
    // than incidental — see the header.
    unlock = vi.spyOn(custody, 'unlock');
    custodySpies = {
      unlock,
      lock: vi.spyOn(custody, 'lock'),
      adopt: vi.spyOn(custody, 'adopt'),
    };
    service = TestBed.inject(AccountUnlockService);
  });

  // One press, driven to the point where the flow has finished with it. `busy`
  // is set synchronously by `unlock()`, so waiting for it to clear is not
  // waiting on a reading that was already false.
  async function press(): Promise<void> {
    service.unlock();

    await eventually(
      () => (service.busy() ? null : true),
      'the attempt to finish',
    );
  }

  // The read custody makes once it has been handed a key. Left outstanding by
  // every caller but the ones that answer it deliberately.
  async function accountKeysRead(): Promise<TestRequest> {
    const request = await eventually(
      () => http.match(ACCOUNT_KEYS_URL)[0] ?? null,
      "the read of this account's wrapped keys",
    );
    seen.push(request.request.urlWithParams);

    return request;
  }

  // **The behavioural half of "a refused unlock leaves the session intact".**
  //
  // Every ending is driven, because the temptation is not spread evenly: the
  // reading a future author will reach for is that a device which refused means
  // the person is no longer who they said they were, and the branch they would
  // reach for it on is one of these.
  //
  // The *structural* half is `injects exactly two collaborators` below, and the
  // two are not redundant. This one catches a call; that one catches the
  // capability, which is what stops a reader wiring `SessionService` in for a
  // reading and leaving somebody a one-line edit from ending a session with it.
  it('leaves the session untouched however the unlock ends', async () => {
    // Arrange
    // Somebody signed in, whose page then reloaded. That is the whole situation
    // this screen's control exists for, and `'authenticated'` is the reading
    // that must survive every branch below.
    session.established();

    const words: readonly PasskeyCeremonyFailure[] = [
      'unsupported',
      'cancelled',
      'no-prf',
      'duplicate',
      'failed',
    ];

    // Act
    for (const word of words) {
      ceremonyOutcome = { ok: false, failure: word };

      await press();

      // Assert
      expect(session.status(), `${word} moved the session.`).toBe(
        'authenticated',
      );
      expect(navigations, `${word} navigated.`).toEqual([]);
    }

    // The ceremony rejecting outright, which is the branch `unknown` is the
    // word for and the one furthest from anything a reader has thought about.
    ceremonyRejection = new Error('the platform threw');

    await press();

    expect(service.failure()).toBe('unknown');
    expect(session.status()).toBe('authenticated');
    expect(navigations).toEqual([]);

    // And the ending that is not a refusal at all: the device did everything
    // right, and the key opened nothing. **This is the branch that matters
    // most.** A person holding a valid session and the wrong authenticator is
    // exactly who a "then they are not signed in" reading would throw out of
    // their own account, and the server never said anything of the kind.
    ceremonyRejection = null;
    ceremonyOutcome = { ok: true, value: keyEncryptionKey };

    await press();

    const read = await accountKeysRead();
    read.flush([]);

    await eventually(
      () => custody.unlockFailure(),
      'custody to report that nothing opened',
    );

    expect(custody.unlockFailure()).toBe('unopened');
    expect(session.status()).toBe('authenticated');
    expect(navigations).toEqual([]);
    expect(unlock).toHaveBeenCalledTimes(1);
  });

  // **The structural half, and it is a pin that is green by construction.**
  //
  // The test above catches a *call* to `session.ended()`. Nothing in it catches
  // injecting `SessionService` and merely reading `status()` — or injecting
  // `Router` and never using it — and those are the edits that make the call a
  // one-liner for the next author. "This service can reach neither the session
  // nor the router" is a statement about the shape of the instance, so this is
  // the census that holds it: a third dependency is a third own property, and
  // it reddens here on its own.
  //
  // Both halves are asserted, because the two shapes of the mistake are
  // different. A collaborator arrives as an ordinary field and is caught by the
  // first; a dependency *unwrapped* on the way in — `inject(SessionService).status`
  // — is a signal, slips past `collaboratorsOf`, and is caught only by the
  // whole-instance list.
  it('injects exactly two collaborators', () => {
    // Arrange
    const collaborators = collaboratorsOf(service);

    // Act
    const names = Object.keys(collaborators).sort();

    // Assert
    expect(
      names,
      'the service was injected with something beyond the ceremony and custody.',
    ).toEqual(['ceremony', 'custody']);

    // By identity, so a field holding *a* ceremony-shaped thing is not enough.
    expect(collaborators['ceremony']).toBe(ceremonyStub);
    expect(collaborators['custody']).toBe(custody);

    // And the whole instance, signals included.
    expect(
      Object.keys(service).sort(),
      'the shape of this service changed.',
    ).toEqual(OWN_PROPERTIES);

    // The control. A walk that classified nothing as a collaborator would
    // satisfy the first assertion in silence.
    expect(
      collaboratorsOf({
        ceremony: {},
        custody: {},
        router: {},
        busy: signal(false),
      }),
    ).toEqual({ ceremony: {}, custody: {}, router: {} });
  });

  // The key travels, once, to the one place in the client entitled to hold it.
  it('hands the key the authenticator derived straight to custody', async () => {
    // Arrange
    // Act
    await press();

    // Assert
    // Once, and the count is not pedantry. A second call runs custody's
    // `#forget` again, which drops both keys and republishes `'unlocking'` — so
    // an account that was open a moment ago closes and reopens, and every read
    // taken in that window is taken against a locked account.
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
    // authenticator's PRF produced and under nothing else.
    expect(unlock.mock.calls[0]?.[0]).toBe(keyEncryptionKey);

    // And `unlock` is the only thing custody was asked for. `adopt` is the
    // near miss on this branch — it takes the two account keys directly, which
    // is registration's case and not this one, and a flow that reached for it
    // would be claiming to hold keys it has never opened.
    expect(calledMembersOf(custodySpies)).toEqual(['unlock']);
  });

  // **`busy` is still true at the instant the key changes hands, and the order
  // is what the screen is made of.**
  //
  // Every other test in this file waits for `busy` to fall and only then reads
  // the spy, so swapping the two statements — clear `busy`, then hand over —
  // passes all of them. What it does on the screen is open a frame in which
  // neither in-flight state is true: this service says it is finished and
  // custody has not yet published `'unlocking'`, so the control flashes back to
  // its resting `Unlock` line, and a person pressing in that frame raises a
  // second system sheet on top of an attempt already running. The only place
  // that frame is observable is inside the call itself.
  it('is still busy at the moment it hands the key over', async () => {
    // Arrange
    // The second of the two places in this file that replaces the
    // implementation. It calls through afterwards, so the flow underneath is
    // the real one and only the reading is added.
    const busyAtHandOver: boolean[] = [];

    unlock.mockImplementation((key: CryptoKey) => {
      busyAtHandOver.push(service.busy());
      handOver(key);
    });

    // Act
    await press();

    // Assert
    // Two assertions, because the two ways this fails are different sentences:
    // a hand-over that never happened, and one that happened a statement too
    // late.
    expect(busyAtHandOver, 'custody was never handed anything.').toHaveLength(
      1,
    );
    expect(
      busyAtHandOver[0],
      'busy had already fallen when the key changed hands.',
    ).toBe(true);

    // And it did fall afterwards, which is what stops this passing over a
    // service that simply never clears it.
    expect(service.busy()).toBe(false);
  });

  // **The other end of the same sentence, and the one test in this file whose
  // reach has to be stated rather than assumed.**
  //
  // Three walks, because a key can be parked in three shapes and only one of
  // them is what a reader pictures:
  //
  //   1. an own property — `this.lastKey = key`, or a signal a template could
  //      render. `findings` reaches both, calling signals on the way past.
  //   2. a module binding or a static — `AccountUnlockService.lastKey = key`.
  //      `moduleSurface` reaches the exports and the enumerable statics on them.
  //   3. **a closure** — `this.retry = () => custody.unlock(key)`. Nothing in
  //      JavaScript can read a captured binding: no property walk reaches it,
  //      `JSON.stringify` does not see it, and `Function.prototype.toString`
  //      gives back source text rather than values. The *container* is
  //      reachable though, and on this instance the only container is an own
  //      function property. `ownFunctionsOf` refuses those, excluding signals,
  //      which are callable and are already looked inside.
  //
  // **The residue, stated as a bound rather than as a reassurance.** What these
  // three reach is: own fields, the current value of own signals, plain data
  // nested in either, every export of this module, the enumerable statics on
  // those exports, and the container an instance-held closure would need. What
  // they do **not** reach is: a value captured by a closure held somewhere
  // other than this instance — a timer the platform holds, a callback handed to
  // a collaborator; a plain `const` local, which
  // dies with the frame that declared it and is therefore not retention at all;
  // a `#private` field, which is unreachable by the language; a module-level
  // `let` that is **not exported**, because an ES module namespace object
  // exposes exports only; and anything inside the two collaborators, which the
  // walk stops at deliberately. So the claim this test makes is "no retention
  // through the shapes this service could be written to use", and it is **not**
  // "no retention is possible".
  //
  // One shape is caught by a *neighbour* rather than by any of the three walks,
  // and it is worth naming because it looks like a hole from here: parking the
  // in-flight promise — `this.pending = this.derive()` — keeps the frame that
  // holds the key alive, and no walk here can see inside it. `injects exactly
  // two collaborators` reddens on it, because the promise is a third own
  // property. Measured, not reasoned.
  it('keeps no key anywhere on itself', async () => {
    // Arrange
    // Act
    await press();

    // Assert
    expect(
      findings(stateOf(service), 'state', isKeyLike),
      'the service is holding a key.',
    ).toEqual([]);
    expect(
      findings(moduleSurface(unlockModule), 'module', isKeyLike),
      'the module is holding a key.',
    ).toEqual([]);
    expect(
      ownFunctionsOf(service),
      'the service holds a function that could have captured the key.',
    ).toEqual([]);

    // The controls, and they are why the three assertions above mean anything:
    // a walk that found nothing anywhere would satisfy all three in silence.
    // Each plants the value exactly where its own defect would put it.
    expect(findings({ held: keyEncryptionKey }, 'probe', isKeyLike)).toEqual([
      'probe.held',
    ]);
    expect(
      findings({ held: signal(keyEncryptionKey) }, 'probe', isKeyLike),
    ).toEqual(['probe.held()']);
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
    expect(ownFunctionsOf({ retry: () => keyEncryptionKey })).toEqual([
      'retry',
    ]);
    // And the exclusion that keeps the closure check usable: a signal is a
    // callable and is not the container this is looking for.
    expect(ownFunctionsOf({ busy: signal(false) })).toEqual([]);
  });

  // **Without this, a flow calling `assertPasskey` with a hand-built options
  // object passes every other test in this file** while silently spending a
  // server challenge from the anonymous pool on every press — and the sign-in
  // route's challenges are exactly the ones nobody is watching here.
  //
  // The census also covers `available()`, which is the second enforcement ADR
  // 0002 refuses: the ceremony guards it and answers `unsupported`, and a copy
  // in the flow would make no observable difference while looking prudent.
  it('runs the local ceremony and neither of the ceremonies that talk to a server', async () => {
    // Arrange
    // Act
    await press();

    // Assert
    expect(ceremonies).toEqual(['deriveKeyFromLocalAssertion']);
    expect(ceremonies).not.toContain('assertPasskey');
    expect(ceremonies).not.toContain('createPasskey');
  });

  // Four words from the device, four words on the screen, and a fifth folded
  // in. Folded further, this screen tells somebody who closed the system sheet
  // that their device cannot hold the account's keys, and tells somebody whose
  // device genuinely cannot to try again on the same device forever.
  describe('maps each ceremony refusal to its own word', () => {
    it.each([
      { word: 'unsupported' as const, failure: 'unsupported' as const },
      { word: 'cancelled' as const, failure: 'cancelled' as const },
      { word: 'no-prf' as const, failure: 'no-prf' as const },
      // The fold, and the only one. `duplicate` is an authenticator declining a
      // credential named in an exclusion list, and an assertion carries no such
      // list — so a word of its own here would describe a thing that cannot
      // happen. It is handled rather than omitted because the union is the
      // ceremony's, and a `switch` is what makes a sixth word a compile error
      // instead of an `undefined` on a screen.
      { word: 'duplicate' as const, failure: 'ceremony-failed' as const },
      { word: 'failed' as const, failure: 'ceremony-failed' as const },
    ])(
      'says $failure when the device says $word',
      async ({ word, failure }) => {
        // Arrange
        ceremonyOutcome = { ok: false, failure: word };

        // Act
        await press();

        // Assert
        expect(service.failure()).toBe(failure);
        expect(service.busy()).toBe(false);
        expect(unlock).not.toHaveBeenCalled();
      },
    );
  });

  // **An attempt has two halves and the guard has to cover both, so it is two
  // tests.**
  //
  // The guard is in the handler as well as in the template attribute, and that
  // is not belt and braces: Material's click-halt applies to anchors only, so
  // on a `<button>` the DOM `disabled` property stays `false` and the second
  // press arrives here — measured on Angular Material 21.2.14, where
  // `_getDisabledAttribute()` returns `null` whenever `disabledInteractive` is
  // set and the only `preventDefault()` in the chunk runs for `tagName === 'A'`.
  //
  // What follows from that is the split below. The press travels through a
  // ceremony this service runs and then through a read **custody** runs, and
  // the two windows are refused by two different readings — so a test that
  // covered one of them was named for both. This one is the first window and
  // the sibling below is the second; neither speaks for the other, and the
  // second is the one that is open today.
  it('ignores a second press while the ceremony is still running', async () => {
    // Arrange
    ceremonyGate = gate();

    // Act
    service.unlock();

    expect(service.busy()).toBe(true);

    service.unlock();
    service.unlock();

    // Assert
    // One ceremony, from three presses, judged before the gate opens — which is
    // the only arrangement that can tell "refused the second press" from
    // "finished before the second press arrived".
    expect(ceremonies).toEqual(['deriveKeyFromLocalAssertion']);

    ceremonyGate.release();

    await eventually(
      () => (service.busy() ? null : true),
      'the attempt to finish',
    );

    expect(ceremonies).toEqual(['deriveKeyFromLocalAssertion']);
    expect(unlock).toHaveBeenCalledTimes(1);
  });

  // **The second window, and the longer of the two.** The ceremony is over —
  // the device answered, the key is in hand — and the account is not open yet,
  // because custody is reading the wrapped envelopes the key has to open. The
  // person is looking at *Opening your account…* and a control drawn disabled.
  //
  // Nothing about that window is contrived: the service hands custody the key
  // **before** it clears `busy`, on purpose and with its own test, so `busy`
  // has fallen and `custody.status()` reads `'unlocking'` for every millisecond
  // of a network round trip. A guard that consults `busy` alone lets a press
  // straight through it.
  //
  // What that press costs is two things and the second is the worse. A second
  // system sheet is raised over an unlock the person has already completed —
  // and `custody.unlock` runs `#forget` again, which drops both keys and
  // republishes `'unlocking'`, discarding the read the first press was about to
  // finish. The account they had just opened is shut, by a button that was
  // drawn as though it could not be pressed.
  it('ignores a second press while the account keys are being read', async () => {
    // Arrange
    // The gate is open, so one press runs all the way to the hand-over. The
    // read custody starts is deliberately left unanswered: this window *is* the
    // request being in flight.
    await press();

    // The window, established rather than assumed. Without these four lines a
    // service that refused the press for the wrong reason — because it never
    // got there, because custody was never handed anything — would pass the
    // assertions below in silence.
    expect(
      service.busy(),
      'the flow was still busy, so this test is not standing in the window it is about.',
    ).toBe(false);
    expect(
      custody.status(),
      'custody was not reading the account keys, so this test is not standing in the window it is about.',
    ).toBe('unlocking');
    expect(unlock).toHaveBeenCalledTimes(1);
    expect(ceremonies).toEqual(['deriveKeyFromLocalAssertion']);

    // Act
    service.unlock();

    // Judged after every microtask a second attempt would need, rather than on
    // the next line: refusing a press and not having reached the ceremony yet
    // look identical one statement later.
    await settled();

    // Assert
    expect(
      ceremonies,
      'a second press raised a second system sheet while the first press’s read of the account keys was still in flight.',
    ).toEqual(['deriveKeyFromLocalAssertion']);
    expect(
      unlock,
      'custody was handed a second key while it was still opening the first — `#forget` drops both keys and republishes `unlocking`, so the read the first press was about to complete is discarded and the account closes.',
    ).toHaveBeenCalledTimes(1);

    // And one read, which is the same sentence said where it is visible from
    // outside the service. `match` removes what it returns, so this is every
    // account-key request the two presses produced between them.
    expect(
      http.match(ACCOUNT_KEYS_URL),
      'the account keys were read more than once for one unlock.',
    ).toHaveLength(1);
  });

  // **"Is either half running" has one owner, and the two readings of it that
  // used to exist were the defect.**
  //
  // The screen ored `custody.status() === 'unlocking'` together with this
  // service's `busy`; the handler guarded on `busy` alone. They agree
  // everywhere except the window the test above stands in — which is the whole
  // length of a network round trip — and there the screen drew a control as
  // unpressable while the handler accepted the press. Neither spelling was
  // wrong on its own terms; having two of them was, and the fix is one reading
  // the template reads and the handler guards on.
  //
  // Asserted as a **reading a caller can take**, not as an implementation: what
  // this case pins is that the answer is published, that it is a signal, and
  // that it is true across both halves and false outside them. How it is
  // composed is the service's business.
  it('publishes one reading of whether either half is running', async () => {
    // Arrange
    ceremonyGate = gate();

    const published = memberNamed(service, IN_FLIGHT_READING);

    expect(
      typeof published === 'function' && isSignal(published),
      `AccountUnlockService publishes no "${IN_FLIGHT_READING}" signal, so every caller has to assemble "either half is running" out of two objects for itself — and the two places that do it today disagree for the length of the custody read.`,
    ).toBe(true);

    const running = published as Signal<boolean>;

    // Assert
    // At rest, before anything has been pressed.
    expect(
      running(),
      'the service reports work in flight before anything was pressed.',
    ).toBe(false);

    // Act
    // The first half: the system sheet is up and the device has not answered.
    service.unlock();

    // Assert
    expect(
      running(),
      'the service reports nothing in flight while the ceremony is still running.',
    ).toBe(true);

    // Act
    // The second half: the device answered, the key changed hands, and custody
    // is reading the envelopes it has to open.
    ceremonyGate.release();

    await eventually(
      () => (service.busy() ? null : true),
      'the ceremony to finish',
    );

    // Assert
    // The window, established rather than assumed — the same four lines the
    // test above opens with, for the same reason.
    expect(service.busy()).toBe(false);
    expect(custody.status()).toBe('unlocking');
    expect(
      running(),
      'the service reports nothing in flight while custody is reading the account keys — the window in which a second press reaches the handler.',
    ).toBe(true);

    // Act
    // And the read comes back. `[]` is the ordinary "none of these envelopes
    // opened" answer; what matters here is only that custody has finished, so
    // neither half is running any more.
    const read = await accountKeysRead();
    read.flush([]);

    await eventually(
      () => custody.unlockFailure(),
      'custody to finish the read',
    );

    // Assert
    expect(
      running(),
      'the service still reports work in flight after both halves have finished, so the control never comes back.',
    ).toBe(false);
  });

  // Cleared when the act *starts*, which is the rule every act in this client
  // follows. Cleared only where one is set, the sentence from a cancelled
  // ceremony survives the press that retries it, and the reader is told what
  // went wrong last time while this time is still running.
  it('clears the previous refusal when an attempt starts', async () => {
    // Arrange
    ceremonyOutcome = { ok: false, failure: 'cancelled' };

    await press();

    expect(service.failure()).toBe('cancelled');

    // Act
    // Held open, so the reading below is taken while the second attempt is
    // still running rather than after it has finished and cleared the word on
    // its way past.
    ceremonyGate = gate();
    ceremonyOutcome = { ok: true, value: keyEncryptionKey };
    service.unlock();

    // Assert
    expect(service.failure()).toBeNull();
    expect(service.busy()).toBe(true);

    ceremonyGate.release();

    await eventually(
      () => (service.busy() ? null : true),
      'the attempt to finish',
    );

    expect(service.failure()).toBeNull();
  });

  // The census. One request left this browser and it was not this flow's — it
  // is custody going for the envelopes the key it was handed can open.
  it('reaches the account-key route and no other address', async () => {
    // Arrange
    // Act
    await press();

    const read = await accountKeysRead();

    // Assert
    expect(seen).toEqual([ACCOUNT_KEYS_URL]);

    // Origins, not prefixes: `https://api.test.attacker.example` is a name
    // anybody can register and `startsWith` admits it, which is the rule
    // `apiCredentialsInterceptor` states one layer down.
    for (const url of seen) {
      expect(new URL(url).origin, `${url} is not this API's origin.`).toBe(
        API_ORIGIN,
      );
    }

    // And nothing else left at all. `match` removes what it returns, so an
    // empty list here is every request the flow made beyond the one above — a
    // profile read, an options leg, a challenge. Mapped to addresses rather
    // than counted, because a census that fails with "expected 1 to be 0" makes
    // the reader go and find out what the one was.
    expect(
      http.match(() => true).map((request) => request.request.urlWithParams),
      'something besides the account-key read left this browser.',
    ).toEqual([]);

    // The read is left unanswered on purpose. Whether it is ever answered is
    // custody's business, and the flow is already finished with it.
    expect(read.request.method).toBe('GET');
    expect(custody.status()).toBe('unlocking');
  });

  // **A key that will not open is not a device that did not work**, and the
  // `try` around the hand-over is what that sentence costs.
  //
  // The mechanism differs from `SignInService`'s and the argument has to be
  // made again for it. There the call sits in an RxJS `next` handler, and a
  // throw out of one is reported out of band rather than routed to the `error`
  // callback beside it. Here the call sits inside an `async` method, so an
  // unguarded throw would reject that method's promise and land in the flow's
  // own `catch` — which publishes `unknown`. The screen would then tell
  // somebody holding a perfectly good authenticator that the ceremony failed,
  // and the retry it invites can only ever end the same way. What the failure
  // really was is readable from `AccountKeyCustodyService.unlockFailure`.
  it('does not report a key that would not open as a device that did not work', async () => {
    // Arrange
    // The first of the two places in this file that replaces the implementation
    // rather than watching it, and the reason is that no arrangement of the
    // *real* service throws: `unlock` catches every way an attempt can end and
    // publishes it as a state. The rule is about the seam and not about today's
    // implementation of the far side of it.
    unlock.mockImplementation(() => {
      throw new Error('the wrapped keys could not be read');
    });

    // Act
    await press();

    // Assert
    // It really did throw, which is what stops this passing over a mock that
    // was never reached.
    expect(unlock).toHaveBeenCalledTimes(1);
    expect(unlock.mock.results[0]?.type).toBe('throw');

    // And the screen says nothing about the device, because the device was
    // fine.
    expect(service.failure()).toBeNull();
    expect(service.busy()).toBe(false);
  });

  // Nothing is handed over when the ceremony refused, and `not.toHaveBeenCalled`
  // is what catches the shape this goes wrong in: a flow that fell through to
  // `custody.unlock(undefined as never)` on the refusal branch. Custody would
  // then `#forget`, publish `'unlocking'`, read the envelopes and fail to open
  // any of them — so a cancelled sheet would lock an account that was open, and
  // report `unopened` about a factor nobody presented.
  it('hands custody nothing when the ceremony refused', async () => {
    // Arrange
    ceremonyOutcome = { ok: false, failure: 'cancelled' };

    // Act
    await press();

    // Assert
    // **Nothing on custody, not merely no `unlock`.** `expect(unlock).not
    // .toHaveBeenCalled()` passed against an implementation that answered a
    // refusal with `custody.lock()` — measured — and so did every other test
    // in this file, because `lock()` publishes the state the assertions below
    // already expect. What it does to somebody whose account is *open* is drop
    // both keys because they closed a system sheet.
    expect(
      calledMembersOf(custodySpies),
      'the refused ceremony reached custody.',
    ).toEqual([]);
    expect(http.match(ACCOUNT_KEYS_URL)).toHaveLength(0);
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBeNull();
  });

  // **`unlock()` returns nothing, and that is enforcement rather than a
  // signature that happens to be convenient.** It is the rule
  // `AccountKeyCustodyService.unlock` keeps about itself, one layer up: a
  // `Promise<void>` is awaitable, and the first thing anybody awaiting it would
  // write is the `catch` that turns a key which did not open into an unlock
  // that failed — on a screen whose whole argument is that those are different
  // things. Checked nowhere else in this file, because every other test drives
  // the flow through `press()` and never looks at what the call gave back.
  it('returns nothing', () => {
    // Arrange
    // Act
    const returned: unknown = service.unlock();

    // Assert
    expect(returned).toBeUndefined();
  });
});
