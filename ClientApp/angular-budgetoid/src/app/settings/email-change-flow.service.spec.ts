// The flow behind **Change email address** and **Confirm with your passkey** on
// `/app/settings`. See docs/design/components.md, "Changing the email address".
//
// It drives a real `HttpClient` over the testing backend, as
// `erasure-flow.service.spec.ts` does, because the facts that matter most live
// on the wire: which nonce pool the challenge comes from, whether a request is
// marked, what the changing request carries, and that nothing is sent before
// the press. The seams replaced are the ones that are not this flow's:
// - `AuthService`, whose hand-off and trip to Google are pinned in its own spec;
// - `ProviderDepartureService`, whose `departing` is what `leaving` is read
//   from — raised by the trip the way the real `AuthService` raises it, before
//   its first await, and lowered when the trip answers `unavailable` or the
//   page comes back from the back-forward cache;
// - `WebauthnCeremonyService`, which reaches `navigator.credentials`;
// - the three screen flows the Change gate reads, each reduced to the one
//   signal the design book names;
// - `SettingsService`, reduced to the address row and the two re-reads.
//
// **Vitest spies persist across cases here** (`restoreMocks` is unset), so
// every spy is built fresh inside `beforeEach`, and the console spies are
// restored after each case.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  isSignal,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MeDto } from '@app-core/api/me-api.service';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { PROVIDER_CREDENTIAL } from '@app-core/interceptors/provider-credential.token';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { spyOnEveryConsoleMethod } from '../../testing/console-spies';
import { AccountUnlockService } from './account-unlock.service';
import {
  EmailChangeFlowService,
  type EmailChangeWord,
} from './email-change-flow.service';
import { ErasureCancellationFlowService } from './erasure-cancellation-flow.service';
import { RotationFlowService } from './rotation-flow.service';
import { SettingsService } from './settings.service';

const API_ORIGIN = 'https://api.budgetoid.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/reauthentication/options`;
// The sign-in pool, named only so its absence can be asserted.
const SIGN_IN_OPTIONS_URL = `${API_ORIGIN}/api/passkeys/assertion/options`;
const EMAIL_CHANGE_URL = `${API_ORIGIN}/api/me/email-change`;
// The probe a 401 on the changing request is read against.
const ME_URL = `${API_ORIGIN}/api/me`;

// What Google sent back. Neither string contains a word this file asserts.
const PROVIDER_TOKEN = 'eyJhbGciOiJSUzI1NiJ9.provider-token-body.sig';
const NEW_ADDRESS = 'new.owner@budgetoid.test';

const ME: MeDto = {
  email: 'current.owner@budgetoid.test',
  budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
};

const OPTIONS: PasskeyRequestOptionsJson = {
  challenge: 'Y2hhbGxlbmdl',
  rpId: 'budgetoid.app',
  timeout: 60000,
  userVerification: 'required',
};

const PAYLOAD: PasskeyAssertionPayload = {
  credentialId: 'Y3JlZGVudGlhbA',
  clientDataJson: 'Y2xpZW50',
  authenticatorData: 'YXV0aA',
  signature: 'c2ln',
  userHandle: 'dXNlcg',
};

// One problem body per refusal and conflict kind, never one shared fixture
// with a member swapped: a shared one lets a mapping that reads the wrong
// member pass on whichever value the fixture happened to carry.
const PROVIDER_TOKEN_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
  refusal: 'provider_token',
};
const EMAIL_UNVERIFIED_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
  refusal: 'email_unverified',
};
const ASSERTION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
  refusal: 'assertion',
};
const UNKNOWN_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
  refusal: 'a_refusal_this_bundle_has_never_seen',
};
const NO_REFUSAL_MEMBER = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
};
const EMAIL_ALREADY_LINKED = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  title: 'Conflict',
  status: 409,
  conflictKind: 'email_already_linked',
};
const PROVIDER_IDENTITY_IN_USE = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  title: 'Conflict',
  status: 409,
  conflictKind: 'provider_identity_in_use',
};
const ACCOUNT_IDENTITY_MOVED = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  title: 'Conflict',
  status: 409,
  conflictKind: 'account_identity_moved',
};
const UNKNOWN_CONFLICT = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  title: 'Conflict',
  status: 409,
  conflictKind: 'a_conflict_this_bundle_has_never_seen',
};
const NO_CONFLICT_MEMBER = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  title: 'Conflict',
  status: 409,
};

type HandOff = ReturnType<AuthService['takeEmailChangeReturn']>;
type Trip = Awaited<ReturnType<AuthService['startEmailChange']>>;

const ANSWERED: HandOff = {
  kind: 'answered',
  idToken: PROVIDER_TOKEN,
  email: NEW_ADDRESS,
};

// One press of the ceremony, answered by hand — `erasure-flow.service.spec.ts`'s
// stub. `held` keeps the call pending until `settle()`.
class CeremonyStub {
  public supported = true;
  public answer: PasskeyCeremonyResult<PasskeyAssertionCeremony>;
  public held = false;
  #release: (() => void) | null = null;

  public readonly available: Mock<() => boolean> = vi.fn(() => this.supported);
  public readonly assertPasskey: Mock<
    (
      options: PasskeyRequestOptionsJson,
      signal?: AbortSignal,
    ) => Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>>
  > = vi.fn(async () => {
    if (this.held) {
      await new Promise<void>((resolve) => (this.#release = resolve));
    }

    return this.answer;
  });

  constructor(ceremony: PasskeyAssertionCeremony) {
    this.answer = { ok: true, value: ceremony };
  }

  public settle(): void {
    this.#release?.();
    this.#release = null;
  }

  public fail(failure: PasskeyCeremonyFailure): void {
    this.answer = { ok: false, failure };
  }
}

interface Screen {
  readonly rotating: WritableSignal<boolean>;
  readonly exporting: WritableSignal<boolean>;
  readonly unlocking: WritableSignal<boolean>;
  // The two passkey checks that hold Confirm, each the narrow reading its own
  // flow publishes — never the flow's whole `working`, which the three above
  // stand for.
  readonly unlockAsking: WritableSignal<boolean>;
  readonly rotationAsking: WritableSignal<boolean>;
  readonly email: WritableSignal<string | null>;
  readonly emailFailed: WritableSignal<boolean>;
  readonly loadEmail: Mock<() => Promise<'loaded' | 'failed'>>;
  // Settles the most recent `loadEmail` call's promise.
  readonly answerRead: (outcome: 'loaded' | 'failed') => void;
  readonly loadCredentials: Mock<() => void>;
}

// Polls until `find` answers something, so a case claims nothing about how
// many awaits the flow contains.
async function eventually<T>(find: () => T | null, what: string): Promise<T> {
  for (let turn = 0; turn < 50; turn += 1) {
    const found = find();

    if (found !== null) {
      return found;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Waited for ${what}, and it never came.`);
}

// Lets every pending promise in the flow run.
async function settle(): Promise<void> {
  for (let turn = 0; turn < 8; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  TestBed.tick();
}

describe('EmailChangeFlowService', () => {
  let http: HttpTestingController;
  let ceremony: CeremonyStub;
  let takeEmailChangeReturn: Mock<() => HandOff>;
  let startEmailChange: Mock<() => Promise<Trip>>;
  let departing: WritableSignal<boolean>;
  let screen: Screen;

  beforeEach(async () => {
    const keyEncryptionKey = await crypto.subtle.generateKey(
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );

    ceremony = new CeremonyStub({ payload: PAYLOAD, keyEncryptionKey });
    takeEmailChangeReturn = vi.fn<() => HandOff>(() => null);
    startEmailChange = vi.fn<() => Promise<Trip>>(() =>
      Promise.resolve('leaving'),
    );
    departing = signal(false);

    const email = signal<string | null>(ME.email);
    let pendingRead: ((outcome: 'loaded' | 'failed') => void) | null = null;
    const emailFailed = signal(false);

    screen = {
      rotating: signal(false),
      exporting: signal(false),
      unlocking: signal(false),
      unlockAsking: signal(false),
      rotationAsking: signal(false),
      email,
      emailFailed,
      // Shaped like the real read: the row clears when the read starts, and
      // the read's own promise is what says it ended. A case settles it with
      // `answerRead`; the row alone never does.
      loadEmail: vi.fn((): Promise<'loaded' | 'failed'> => {
        email.set(null);
        emailFailed.set(false);

        return new Promise<'loaded' | 'failed'>((resolve) => {
          pendingRead = resolve;
        });
      }),
      answerRead: (outcome) => {
        pendingRead?.(outcome);
        pendingRead = null;
      },
      loadCredentials: vi.fn(),
    };
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // Builds the flow the way `SettingsComponent` provides it, with the hand-off
  // the page load left. Construction is where the hand-off is taken, so the
  // case decides it before the flow exists.
  function flowWith(handOff: HandOff): EmailChangeFlowService {
    configureScreen(handOff);

    return TestBed.inject(EmailChangeFlowService);
  }

  // The same flow provided in an environment injector the case can destroy,
  // which is what the screen's teardown is to a component-provided service —
  // `erasure-flow.service.spec.ts`'s shape.
  function flowOnAScreen(handOff: HandOff): {
    readonly flow: EmailChangeFlowService;
    readonly settingsScreen: EnvironmentInjector;
  } {
    configureScreen(handOff);

    const settingsScreen = createEnvironmentInjector(
      [EmailChangeFlowService],
      TestBed.inject(EnvironmentInjector),
    );

    return { flow: settingsScreen.get(EmailChangeFlowService), settingsScreen };
  }

  function configureScreen(handOff: HandOff): void {
    takeEmailChangeReturn.mockReturnValue(handOff);

    const departure: Pick<
      ProviderDepartureService,
      'departing' | 'begin' | 'depart' | 'settle'
    > = {
      departing: departing.asReadonly(),
      begin: () => departing.set(true),
      depart: () => departing.set(true),
      settle: () => departing.set(false),
    };
    // The trip as the real `AuthService` runs it around the answer a case
    // chooses: departing is raised before the first await, and lowered by the
    // trip itself when it answers `unavailable`. A rejection — outside the
    // contract — lowers nothing; the flow is what puts Change back then.
    const auth: Pick<
      AuthService,
      'takeEmailChangeReturn' | 'startEmailChange'
    > = {
      takeEmailChangeReturn,
      startEmailChange: async (): Promise<Trip> => {
        departure.begin();
        const trip = await startEmailChange();
        if (trip === 'unavailable') {
          departure.settle();
        }

        return trip;
      },
    };
    const settings: Pick<
      SettingsService,
      'exporting' | 'email' | 'emailFailed' | 'loadEmail' | 'loadCredentials'
    > = {
      exporting: screen.exporting,
      email: screen.email,
      emailFailed: screen.emailFailed,
      loadEmail: screen.loadEmail,
      loadCredentials: screen.loadCredentials,
    };
    // `asking` beside `working`, each a signal of its own and neither composed
    // from the other, so a flow reading the wrong one parts company with the
    // case. Typed as an intersection rather than a `Pick` of `asking`, so this
    // file still compiles while the flows do not publish it yet.
    const unlock: Pick<AccountUnlockService, 'working'> & {
      readonly asking: Signal<boolean>;
    } = {
      working: screen.unlocking,
      asking: screen.unlockAsking,
    };
    const rotation: Pick<RotationFlowService, 'working'> & {
      readonly asking: Signal<boolean>;
    } = {
      working: screen.rotating,
      asking: screen.rotationAsking,
    };
    // The erasure cancellation, at rest: it holds nothing in this file. Its two
    // holds are `email-change-flow.service.cancellation.spec.ts`'s.
    const cancellation: Pick<
      ErasureCancellationFlowService,
      'asking' | 'working'
    > = {
      asking: signal(false),
      working: signal(false),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN, auth: {} }) },
        },
        { provide: AuthService, useValue: auth },
        { provide: ProviderDepartureService, useValue: departure },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        { provide: SettingsService, useValue: settings },
        { provide: AccountUnlockService, useValue: unlock },
        { provide: RotationFlowService, useValue: rotation },
        { provide: ErasureCancellationFlowService, useValue: cancellation },
        EmailChangeFlowService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(() => {
      const found = http.match(url);

      if (found.length > 1) {
        throw new Error(`${found.length} requests to ${url}, not one.`);
      }

      return found[0] ?? null;
    }, `a request to ${url}`);
  }

  // Presses Confirm and answers the challenge, up to the changing request.
  async function reachTheChangingRequest(
    flow: EmailChangeFlowService,
  ): Promise<TestRequest> {
    flow.confirm();
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);

    return requestTo(EMAIL_CHANGE_URL);
  }

  // The changing request answered with a 401, and the probe after it answered
  // with the session still there.
  async function refusedWith(
    flow: EmailChangeFlowService,
    body: object,
  ): Promise<void> {
    const changing = await reachTheChangingRequest(flow);

    changing.flush(body, { status: 401, statusText: 'Unauthorized' });
    (await requestTo(ME_URL)).flush(ME);
    await settle();
  }

  async function conflictedWith(
    flow: EmailChangeFlowService,
    body: object,
  ): Promise<void> {
    const changing = await reachTheChangingRequest(flow);

    changing.flush(body, { status: 409, statusText: 'Conflict' });
    await settle();
  }

  describe('the return', () => {
    it('waits for the press with the address Google sent back, and asks the server nothing', async () => {
      // Act
      const flow = flowWith(ANSWERED);
      await settle();

      // Assert
      expect(flow.phase()).toBe('waiting');
      expect(flow.address()).toBe(NEW_ADDRESS);
      expect(flow.word()).toBeNull();
      expect(flow.confirmPressable()).toBe(true);
      // The design's "minting the challenge on the return" mistake: nothing is
      // sent before the press, not even the challenge.
      http.verify();
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    });

    // At construction and not on the first read: the answer is read before the
    // first route draws, so the first render is already the waiting state.
    it('takes the hand-off once, when it is built', async () => {
      // Act
      const flow = flowWith(ANSWERED);
      const takenAtConstruction = takeEmailChangeReturn.mock.calls.length;
      flow.phase();
      flow.address();
      flow.confirmPressable();
      await settle();

      // Assert
      expect(takenAtConstruction).toBe(1);
      expect(takeEmailChangeReturn).toHaveBeenCalledOnce();
    });

    it('offers nothing and posts nothing when the page load brought no return', async () => {
      // Arrange
      const flow = flowWith(null);

      // Act
      flow.confirm();
      await settle();

      // Assert
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
      expect(flow.word()).toBeNull();
      expect(flow.confirmPressable()).toBe(false);
      http.verify();
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    });

    it('says unconfirmed and posts nothing when Google sent back no sign-in', async () => {
      // Arrange
      const flow = flowWith({ kind: 'unconfirmed' });

      // Act
      flow.confirm();
      await settle();

      // Assert
      expect(flow.word()).toBe('unconfirmed');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
      expect(flow.confirmPressable()).toBe(false);
      expect(flow.changePressable()).toBe(true);
      http.verify();
    });
  });

  describe('the confirm press', () => {
    // The re-authentication pool, unmarked: a 401 on it is a session that
    // really has ended, which is the interceptor's to act on.
    it('fetches the challenge on the press, from the re-authentication pool, unmarked', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      expect(http.match(OPTIONS_URL)).toEqual([]);

      // Act
      flow.confirm();
      const challenge = await requestTo(OPTIONS_URL);

      // Assert
      expect(challenge.request.method).toBe('POST');
      expect(challenge.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(
        false,
      );
      expect(http.match(SIGN_IN_OPTIONS_URL)).toEqual([]);
      expect(flow.phase()).toBe('asserting');
    });

    it('posts nothing until the passkey has answered', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.held = true;

      // Act
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      expect(ceremony.assertPasskey).toHaveBeenCalledOnce();
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);

      ceremony.settle();
      (await requestTo(EMAIL_CHANGE_URL)).flush({ sessionsEnded: 0 });
    });

    it('signs over the challenge it fetched and sends the handed token and the assertion', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      const changing = await reachTheChangingRequest(flow);

      // Assert
      expect(ceremony.assertPasskey.mock.calls[0]?.[0]).toEqual(OPTIONS);
      expect(changing.request.method).toBe('POST');
      expect(changing.request.context.get(PROVIDER_CREDENTIAL)).toBe(
        PROVIDER_TOKEN,
      );
      expect(changing.request.body).toEqual(PAYLOAD);
      expect(flow.phase()).toBe('changing');

      changing.flush({ sessionsEnded: 0 });
    });

    // Before the challenge, which is the only position that costs nothing: a
    // browser that cannot run the ceremony would spend a nonce on the way to
    // the same sentence.
    it('says unsupported before any challenge, drops the answer and keeps Change', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.supported = false;

      // Act
      flow.confirm();
      await settle();

      // Assert
      expect(flow.word()).toBe('unsupported');
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
      expect(flow.confirmPressable()).toBe(false);
      expect(flow.changePressable()).toBe(true);
    });

    // The gate is in the handler: Material halts the click on anchors only, so
    // a press on a `<button>` drawn inert still arrives.
    it('makes one challenge however often it is pressed while its press is in flight', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.held = true;
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Act
      expect(flow.confirmPressable()).toBe(false);
      flow.confirm();
      await settle();

      // Assert
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(ceremony.assertPasskey).toHaveBeenCalledOnce();

      ceremony.settle();
      const changing = await requestTo(EMAIL_CHANGE_URL);
      flow.confirm();
      await settle();
      expect(http.match(OPTIONS_URL)).toEqual([]);
      changing.flush({ sessionsEnded: 0 });
    });

    // Confirm's predicate is its own: Change's reasons describe a moment it
    // never exists in.
    it.each([
      { reason: 'a rotation this tab is walking', term: 'rotating' as const },
      { reason: 'an export in flight', term: 'exporting' as const },
      { reason: 'an unlock running', term: 'unlocking' as const },
    ])('is not held off by $reason', async ({ term }) => {
      // Arrange
      const flow = flowWith(ANSWERED);
      screen[term].set(true);

      // Act
      flow.confirm();
      await requestTo(OPTIONS_URL);

      // Assert
      expect(flow.phase()).toBe('asserting');
    });

    it('says unstarted and keeps waiting when the challenge cannot be fetched', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 503,
        statusText: 'Service Unavailable',
      });
      await settle();

      // Assert
      expect(flow.word()).toBe('unstarted');
      expect(flow.phase()).toBe('waiting');
      expect(flow.address()).toBe(NEW_ADDRESS);
      expect(flow.confirmPressable()).toBe(true);
    });

    it('says nothing when the challenge finds the session ended', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(flow.word()).toBeNull();
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
    });

    // The four ceremony words keep the waiting state: the Google answer is
    // still good, and another press is a genuinely different attempt.
    it.each<{ failure: PasskeyCeremonyFailure; word: EmailChangeWord }>([
      { failure: 'cancelled', word: 'cancelled' },
      { failure: 'no-prf', word: 'no-prf' },
      { failure: 'failed', word: 'ceremony-failed' },
      { failure: 'duplicate', word: 'ceremony-failed' },
    ])(
      'reads the ceremony ending $failure as $word, posts nothing and keeps waiting',
      async ({ failure, word }) => {
        // Arrange
        const flow = flowWith(ANSWERED);
        ceremony.fail(failure);

        // Act
        flow.confirm();
        (await requestTo(OPTIONS_URL)).flush(OPTIONS);
        await settle();

        // Assert
        expect(flow.word()).toBe(word);
        expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
        expect(flow.phase()).toBe('waiting');
        expect(flow.address()).toBe(NEW_ADDRESS);
        expect(flow.confirmPressable()).toBe(true);
      },
    );

    // A press clears the previous line as it starts: the last attempt's
    // sentence must not stand over this one while it runs.
    it('clears the last word when confirm is pressed again, before the challenge answers', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.fail('cancelled');
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();
      expect(flow.word()).toBe('cancelled');

      // Act
      flow.confirm();
      const challenge = await requestTo(OPTIONS_URL);

      // Assert
      expect(flow.word()).toBeNull();
      expect(flow.phase()).toBe('asserting');

      challenge.flush(OPTIONS);
    });

    // The ceremony's contract is to answer with a result. A rejection out of it
    // has still posted nothing, so the answer stays good.
    it('reads a ceremony that rejects as ceremony-failed and keeps waiting', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.assertPasskey.mockRejectedValueOnce(
        new Error('The platform threw.'),
      );

      // Act
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      expect(flow.word()).toBe('ceremony-failed');
      expect(flow.phase()).toBe('waiting');
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
    });

    it('reads the ceremony finding no passkey support as unsupported and drops the answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.fail('unsupported');

      // Act
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      expect(flow.word()).toBe('unsupported');
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
    });
  });

  describe('a 200', () => {
    // The success line states the result, so it waits for the re-read.
    // The lead line is drawn from `address()` for as long as the phase is
    // `changing`, so the answer stays until the re-read ends the waiting
    // state — dropped earlier, the screen reads "Google sent back ." while the
    // change is still landing.
    it('keeps the address Google sent back while the re-read is outstanding', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush({ sessionsEnded: 1 });
      await settle();

      // Assert
      expect(flow.phase()).toBe('changing');
      expect(flow.address()).toBe(NEW_ADDRESS);
    });

    it('stays on changing until the address row has been read again', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush({ sessionsEnded: 2 });
      await settle();

      // Assert
      expect(screen.loadEmail).toHaveBeenCalledOnce();
      expect(screen.loadCredentials).toHaveBeenCalledOnce();
      expect(flow.phase()).toBe('changing');
      expect(flow.word()).toBeNull();
    });

    it('says changed with the count once the re-read lands, and ends the waiting state', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);
      changing.flush({ sessionsEnded: 2 });
      await settle();

      // Act
      screen.email.set(NEW_ADDRESS);
      screen.answerRead('loaded');
      await settle();

      // Assert
      expect(flow.word()).toBe('changed');
      expect(flow.sessionsEnded()).toBe(2);
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
      expect(flow.confirmPressable()).toBe(false);
    });

    // P2. A read that answers before the flow could see the row go blank —
    // synchronously, or with a cached row — still ends the press: the read's
    // promise says it ended, not the row's round trip through null.
    it('says changed when the re-read answers at once', async () => {
      // Arrange
      screen.loadEmail.mockImplementation(() => {
        screen.email.set(NEW_ADDRESS);

        return Promise.resolve('loaded');
      });
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush({ sessionsEnded: 1 });
      await settle();

      // Assert
      expect(flow.word()).toBe('changed');
      expect(flow.phase()).toBe('rest');
    });

    // P4. An earlier read of the row landing after the re-read began puts the
    // OLD address on it. That is not this read answering, so it concludes
    // nothing; only this read's promise does.
    it('concludes nothing from an address that lands before the re-read answers', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);
      changing.flush({ sessionsEnded: 1 });
      await settle();

      // Act
      screen.email.set(ME.email);
      await settle();
      const beforeTheRead = { word: flow.word(), phase: flow.phase() };
      screen.email.set(NEW_ADDRESS);
      screen.answerRead('loaded');
      await settle();

      // Assert
      expect(beforeTheRead).toEqual({ word: null, phase: 'changing' });
      expect(flow.word()).toBe('changed');
    });

    it('says the re-read failed in its own word when the address row cannot be read back', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);
      changing.flush({ sessionsEnded: 0 });
      await settle();

      // Act
      screen.emailFailed.set(true);
      screen.answerRead('failed');
      await settle();

      // Assert
      expect(flow.word()).toBe('changed-unread');
      expect(flow.phase()).toBe('rest');
    });

    // A 200 that reads but carries no count this bundle can read: the change
    // happened, and the clause-free line claims nothing about other browsers.
    // A count of sessions is a whole number and never negative. `NaN` has no
    // JSON spelling — a raw `NaN` token fails the parse and is the 200 that
    // does not read at all, below — so it is handed over as a value here.
    it.each([
      { shape: 'a string', body: { sessionsEnded: 'two' } },
      { shape: 'a negative count', body: { sessionsEnded: -1 } },
      { shape: 'a fractional count', body: { sessionsEnded: 1.5 } },
      { shape: 'not a number', body: { sessionsEnded: Number.NaN } },
      { shape: 'no count member', body: {} },
    ])(
      'says changed with no count when the count is $shape',
      async ({ body }) => {
        // Arrange
        const flow = flowWith(ANSWERED);
        const changing = await reachTheChangingRequest(flow);
        changing.flush(body);
        await settle();

        // Act
        screen.email.set(NEW_ADDRESS);
        screen.answerRead('loaded');
        await settle();

        // Assert
        expect(flow.word()).toBe('changed');
        expect(flow.sessionsEnded()).toBeNull();
      },
    );

    // Zero is a count, and the line for it takes no clause — but it is not
    // "unreadable", and a truthiness check would make it one.
    it('reads a count of zero as zero', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);
      changing.flush({ sessionsEnded: 0 });
      await settle();

      // Act
      screen.email.set(NEW_ADDRESS);
      screen.answerRead('loaded');
      await settle();

      // Assert
      expect(flow.word()).toBe('changed');
      expect(flow.sessionsEnded()).toBe(0);
    });

    it('reads a 200 that does not read at all as undetermined', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush(null);
      await settle();

      // Assert
      expect(flow.word()).toBe('undetermined');
      expect(flow.confirmPressable()).toBe(false);
    });
  });

  describe('a 401', () => {
    it('asks once, unmarked, whether the session is still there', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      const probe = await requestTo(ME_URL);

      // Assert
      expect(probe.request.method).toBe('GET');
      expect(probe.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);

      probe.flush(ME);
    });

    // An ended session is the interceptor's: it takes the tab to Welcome, and
    // this flow says nothing over it.
    it('says nothing when the probe finds the session ended', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      (await requestTo(ME_URL)).flush(null, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(flow.word()).toBeNull();
    });

    it('reads a refused provider token as provider-refused and drops the answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await refusedWith(flow, PROVIDER_TOKEN_REFUSAL);

      // Assert
      expect(flow.word()).toBe('provider-refused');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
      expect(flow.changePressable()).toBe(true);
    });

    it('reads an unverified address as unverified and drops the answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await refusedWith(flow, EMAIL_UNVERIFIED_REFUSAL);

      // Assert
      expect(flow.word()).toBe('unverified');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
    });

    it('reads a refused passkey as assertion-refused and keeps waiting', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await refusedWith(flow, ASSERTION_REFUSAL);

      // Assert
      expect(flow.word()).toBe('assertion-refused');
      expect(flow.phase()).toBe('waiting');
      expect(flow.address()).toBe(NEW_ADDRESS);
      expect(flow.confirmPressable()).toBe(true);
    });

    it.each([
      { shape: 'a refusal this bundle cannot name', body: UNKNOWN_REFUSAL },
      { shape: 'no refusal member', body: NO_REFUSAL_MEMBER },
    ])('reads $shape as failed, never undetermined', async ({ body }) => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await refusedWith(flow, body);

      // Assert
      expect(flow.word()).toBe('failed');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
    });

    // A probe that cannot answer is not a session that ended; the member the
    // server sent still decides the word.
    it('lets the member decide when the probe cannot answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      (await requestTo(ME_URL)).flush(null, {
        status: 503,
        statusText: 'Service Unavailable',
      });
      await settle();

      // Assert
      expect(flow.word()).toBe('assertion-refused');
    });
  });

  describe('a 409', () => {
    it('reads an address already linked as address-taken and drops the answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await conflictedWith(flow, EMAIL_ALREADY_LINKED);

      // Assert
      expect(flow.word()).toBe('address-taken');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
    });

    it('reads a Google account in use as google-account-taken and drops the answer', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await conflictedWith(flow, PROVIDER_IDENTITY_IN_USE);

      // Assert
      expect(flow.word()).toBe('google-account-taken');
      expect(flow.phase()).toBe('rest');
      expect(flow.address()).toBeNull();
    });

    // The one refusal-coloured line that says something changed, so the row it
    // is about is read again — and only that row.
    it('reads an identity moved elsewhere as moved and re-reads the address row alone', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await conflictedWith(flow, ACCOUNT_IDENTITY_MOVED);

      // Assert
      expect(flow.word()).toBe('moved');
      expect(flow.phase()).toBe('rest');
      expect(screen.loadEmail).toHaveBeenCalledOnce();
      expect(screen.loadCredentials).not.toHaveBeenCalled();
    });

    it.each([
      { shape: 'a conflict this bundle cannot name', body: UNKNOWN_CONFLICT },
      { shape: 'no conflict member', body: NO_CONFLICT_MEMBER },
    ])('reads $shape as failed', async ({ body }) => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await conflictedWith(flow, body);

      // Assert
      expect(flow.word()).toBe('failed');
      expect(screen.loadEmail).not.toHaveBeenCalled();
    });
  });

  describe('every other answer', () => {
    // The server looked and refused: nothing changed, and the line says so.
    it.each([
      { status: 400, statusText: 'Bad Request' },
      { status: 403, statusText: 'Forbidden' },
      { status: 422, statusText: 'Unprocessable Content' },
    ])(
      'reads a judged $status as failed and drops the answer',
      async ({ status, statusText }) => {
        // Arrange
        const flow = flowWith(ANSWERED);
        const changing = await reachTheChangingRequest(flow);

        // Act
        changing.flush({ title: statusText, status }, { status, statusText });
        await settle();

        // Assert
        expect(flow.word()).toBe('failed');
        expect(flow.phase()).toBe('rest');
        expect(flow.address()).toBeNull();
        expect(http.match(ME_URL)).toEqual([]);
      },
    );

    // No judgement observed: the request may have committed. One answer sends
    // one changing request, so the confirm control leaves.
    it.each([
      { status: 500, statusText: 'Internal Server Error' },
      { status: 503, statusText: 'Service Unavailable' },
    ])(
      'reads a $status as undetermined, withdraws the confirm and keeps Change',
      async ({ status, statusText }) => {
        // Arrange
        const flow = flowWith(ANSWERED);
        const changing = await reachTheChangingRequest(flow);

        // Act
        changing.flush(null, { status, statusText });
        await settle();

        // Assert
        expect(flow.word()).toBe('undetermined');
        expect(flow.phase()).toBe('rest');
        expect(flow.address()).toBeNull();
        expect(flow.confirmPressable()).toBe(false);
        expect(flow.changePressable()).toBe(true);
      },
    );

    it('reads a response that never arrived as undetermined', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);

      // Act
      changing.error(new ProgressEvent('error'), {
        status: 0,
        statusText: '',
      });
      await settle();

      // Assert
      expect(flow.word()).toBe('undetermined');
      expect(flow.confirmPressable()).toBe(false);
    });

    // One answer sends one changing request — not automatically, and not on a
    // later press either.
    it('never sends the changing request a second time', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      const changing = await reachTheChangingRequest(flow);
      changing.flush(null, { status: 503, statusText: 'Service Unavailable' });
      await settle();

      // Act
      flow.confirm();
      await settle();

      // Assert
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
    });
  });

  describe('the change press', () => {
    it('leaves for Google and says so until the page goes', async () => {
      // Arrange
      let resolveTrip: (trip: Trip) => void = () => undefined;
      startEmailChange.mockReturnValue(
        new Promise<Trip>((resolve) => (resolveTrip = resolve)),
      );
      const flow = flowWith(null);

      // Act
      flow.change();

      // Assert
      expect(startEmailChange).toHaveBeenCalledOnce();
      expect(flow.phase()).toBe('leaving');
      expect(flow.changePressable()).toBe(false);

      resolveTrip('leaving');
      await settle();
      expect(flow.phase()).toBe('leaving');
    });

    // **Back from Google restores this page from the back-forward cache**,
    // still saying it is leaving. `AuthService` hears the restore and lowers
    // `departing`; the flow reads `leaving` from that one flag, so Change
    // comes back with it — and with no line, because nothing failed.
    it('a restore from the cache puts Change back with no line', async () => {
      // Arrange
      const flow = flowWith(null);
      flow.change();
      await settle();
      const before = flow.phase();

      // Act
      departing.set(false);

      // Assert — the control first: a press that never left proves nothing.
      expect(before).toBe('leaving');
      expect(flow.phase()).toBe('rest');
      expect(flow.changePressable()).toBe(true);
      expect(flow.word()).toBeNull();
    });

    // The press dropped the answer the page held, so the restore cannot bring
    // back the waiting state: Confirm would be drawn over no token at all.
    it('a restore from the cache after a press that dropped an answer puts Change back, not Confirm', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      flow.change();
      await settle();

      // Act
      departing.set(false);

      // Assert
      expect(flow.phase()).toBe('rest');
      expect(flow.confirmPressable()).toBe(false);
      expect(flow.address()).toBeNull();
    });

    // One flag, read rather than copied: a page departing for Google by any
    // press is a page on its way there, and Change says so.
    it('reads leaving from the departure, not from a copy of its own', () => {
      // Arrange
      const flow = flowWith(null);

      // Act
      departing.set(true);

      // Assert
      expect(flow.phase()).toBe('leaving');
      expect(flow.changePressable()).toBe(false);
    });

    it('asks for one trip however often it is pressed while leaving', async () => {
      // Arrange
      startEmailChange.mockReturnValue(new Promise<Trip>(() => undefined));
      const flow = flowWith(null);
      flow.change();

      // Act
      flow.change();
      flow.change();
      await settle();

      // Assert
      expect(startEmailChange).toHaveBeenCalledOnce();
    });

    it('clears the last word when Change is pressed again', async () => {
      // Arrange
      startEmailChange.mockResolvedValueOnce('unavailable');
      const flow = flowWith(null);
      flow.change();
      await settle();
      expect(flow.word()).toBe('unavailable');
      startEmailChange.mockReturnValueOnce(new Promise<Trip>(() => undefined));

      // Act
      flow.change();

      // Assert
      expect(flow.word()).toBeNull();
      expect(flow.phase()).toBe('leaving');
    });

    // A rejection is outside the trip's contract, so nothing promises the
    // trip lowered `departing` on its way out. The page has not left, which
    // the flow knows, so the flow settles the departure itself.
    it('says unavailable when the trip rejects, and offers Change again', async () => {
      // Arrange
      startEmailChange.mockRejectedValueOnce(new Error('The trip threw.'));
      const flow = flowWith(null);

      // Act
      flow.change();
      await settle();

      // Assert
      expect(flow.word()).toBe('unavailable');
      expect(flow.phase()).toBe('rest');
      expect(flow.changePressable()).toBe(true);
      expect(departing()).toBe(false);
    });

    // A held answer is for this load's trip; starting another one replaces it.
    it('drops a held answer when Change is pressed', () => {
      // Arrange
      startEmailChange.mockReturnValue(new Promise<Trip>(() => undefined));
      const flow = flowWith(ANSWERED);

      // Act
      flow.change();

      // Assert
      expect(flow.address()).toBeNull();
    });

    it('says unavailable when Google cannot be reached, and offers Change again', async () => {
      // Arrange
      startEmailChange.mockResolvedValue('unavailable');
      const flow = flowWith(null);

      // Act
      flow.change();
      await settle();

      // Assert
      expect(flow.word()).toBe('unavailable');
      expect(flow.phase()).toBe('rest');
      expect(flow.changePressable()).toBe(true);
    });

    // Each gate is in the handler: a press on a control drawn off still
    // arrives, and an ungated one reloads the tab under the work it names.
    it.each([
      { reason: 'a rotation this tab is walking', term: 'rotating' as const },
      { reason: 'an export in flight', term: 'exporting' as const },
      { reason: 'an unlock running', term: 'unlocking' as const },
    ])('is held off by $reason, in the handler', async ({ term }) => {
      // Arrange
      const flow = flowWith(null);
      screen[term].set(true);

      // Act
      flow.change();
      await settle();

      // Assert
      expect(flow.changePressable()).toBe(false);
      expect(startEmailChange).not.toHaveBeenCalled();
      expect(flow.phase()).toBe('rest');
    });

    it('is held off while a confirm press is in flight, in the handler', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      ceremony.held = true;
      flow.confirm();
      await requestTo(OPTIONS_URL);

      // Act
      flow.change();
      await settle();

      // Assert
      expect(flow.changePressable()).toBe(false);
      expect(startEmailChange).not.toHaveBeenCalled();
    });

    // One sentence at a time, in the order of what a reload would cost.
    it.each([
      {
        held: { rotating: true, exporting: true, unlocking: true },
        reason: 'rotating',
      },
      {
        held: { rotating: false, exporting: true, unlocking: true },
        reason: 'exporting',
      },
      {
        held: { rotating: false, exporting: false, unlocking: true },
        reason: 'unlocking',
      },
      {
        held: { rotating: false, exporting: false, unlocking: false },
        reason: null,
      },
    ])('names $reason as the reason Change is off', ({ held, reason }) => {
      // Arrange
      const flow = flowWith(null);

      // Act
      screen.rotating.set(held.rotating);
      screen.exporting.set(held.exporting);
      screen.unlocking.set(held.unlocking);

      // Assert
      expect(flow.changeHold()).toBe(reason);
    });

    // Busy is not a reason: the flow's own line says it, in the region.
    it('names no reason while it is on its way to Google', () => {
      // Arrange
      startEmailChange.mockReturnValue(new Promise<Trip>(() => undefined));
      const flow = flowWith(null);

      // Act
      flow.change();

      // Assert
      expect(flow.changePressable()).toBe(false);
      expect(flow.changeHold()).toBeNull();
    });
  });

  // **The flow holds other controls as well as being held by them**
  // (docs/design/components.md, "Holds in both directions"). Two readings go
  // out — departing, which `ProviderDepartureService` already publishes, and
  // *asking*, Confirm's passkey check — and two come in: Confirm is held off by
  // the unlock's passkey check and by a rotation's, because the browser runs
  // one passkey check at a time. Both are passkey checks and nothing wider.
  //
  // `asking` and `confirmHold` are read by name, so a flow that does not
  // publish them yet fails on an assertion naming the member rather than
  // taking the file down with a compile error.
  describe('holds in both directions', () => {
    function readingOf<T>(
      flow: EmailChangeFlowService,
      name: string,
    ): Signal<T> {
      const member = (flow as unknown as Record<string, unknown>)[name];

      expect(
        typeof member === 'function' && isSignal(member),
        `EmailChangeFlowService publishes no "${name}" signal.`,
      ).toBe(true);

      return member as Signal<T>;
    }

    describe('asking', () => {
      it('is not asking at rest', () => {
        // Act
        const flow = flowWith(null);

        // Assert
        expect(flow.phase()).toBe('rest');
        expect(readingOf<boolean>(flow, 'asking')()).toBe(false);
      });

      it('is not asking while it waits for the press', async () => {
        // Act
        const flow = flowWith(ANSWERED);
        await settle();

        // Assert
        expect(flow.phase()).toBe('waiting');
        expect(readingOf<boolean>(flow, 'asking')()).toBe(false);
      });

      it('is asking while the challenge is fetched', async () => {
        // Arrange
        const flow = flowWith(ANSWERED);

        // Act
        flow.confirm();
        await requestTo(OPTIONS_URL);

        // Assert
        expect(flow.phase()).toBe('asserting');
        expect(readingOf<boolean>(flow, 'asking')()).toBe(true);
      });

      it('is asking while the device is asked', async () => {
        // Arrange
        const flow = flowWith(ANSWERED);
        ceremony.held = true;

        // Act
        flow.confirm();
        (await requestTo(OPTIONS_URL)).flush(OPTIONS);
        await settle();

        // Assert
        expect(ceremony.assertPasskey).toHaveBeenCalledOnce();
        expect(readingOf<boolean>(flow, 'asking')()).toBe(true);

        ceremony.settle();
        (await requestTo(EMAIL_CHANGE_URL)).flush({ sessionsEnded: 0 });
      });

      // The changing request after the check is not part of it: the device is
      // asked nothing while it is out.
      it('is not asking while the changing request is out', async () => {
        // Arrange
        const flow = flowWith(ANSWERED);

        // Act
        const changing = await reachTheChangingRequest(flow);

        // Assert
        expect(flow.phase()).toBe('changing');
        expect(readingOf<boolean>(flow, 'asking')()).toBe(false);

        changing.flush({ sessionsEnded: 0 });
      });

      // Departing is the other reading, and it is not this one.
      it('is not asking while the page leaves for Google', () => {
        // Arrange
        startEmailChange.mockReturnValue(new Promise<Trip>(() => undefined));
        const flow = flowWith(null);

        // Act
        flow.change();

        // Assert
        expect(flow.phase()).toBe('leaving');
        expect(readingOf<boolean>(flow, 'asking')()).toBe(false);
      });
    });

    describe('Confirm held by another passkey check', () => {
      const CHECKS = [
        { check: 'the unlock’s passkey check', term: 'unlockAsking' as const },
        {
          check: 'a rotation’s passkey check',
          term: 'rotationAsking' as const,
        },
      ];

      it.each(CHECKS)(
        'is not pressable while $check runs',
        async ({ term }) => {
          // Arrange
          const flow = flowWith(ANSWERED);
          await settle();
          // Waiting, established: the only other thing that holds Confirm off.
          expect(flow.phase()).toBe('waiting');

          // Act
          screen[term].set(true);

          // Assert
          expect(flow.confirmPressable()).toBe(false);
        },
      );

      it.each(CHECKS)(
        'fetches no challenge and asks the device nothing when pressed while $check runs',
        async ({ term }) => {
          // Arrange
          const flow = flowWith(ANSWERED);
          await settle();
          screen[term].set(true);

          // Act
          // The handler's gate: Material halts the click on anchors only, so
          // a press on a Confirm drawn off still arrives — and an ungated one
          // raises a second system sheet over the first, which the browser
          // refuses or uses to cut the first one off.
          flow.confirm();
          await settle();

          // Assert
          expect(http.match(OPTIONS_URL)).toEqual([]);
          expect(ceremony.assertPasskey).not.toHaveBeenCalled();
          expect(flow.phase()).toBe('waiting');
          expect(flow.address()).toBe(NEW_ADDRESS);
          expect(flow.word()).toBeNull();
        },
      );

      it.each(CHECKS)(
        'is pressable again, and its press runs, once $check ends',
        async ({ term }) => {
          // Arrange
          // Control for the two cases above: a hold that latched would pass
          // both and never let a held answer be confirmed.
          const flow = flowWith(ANSWERED);
          await settle();
          screen[term].set(true);
          flow.confirm();
          await settle();

          // Act
          screen[term].set(false);
          flow.confirm();

          // Assert
          expect(flow.confirmPressable()).toBe(false);
          expect(flow.phase()).toBe('asserting');
          await requestTo(OPTIONS_URL);
        },
      );

      // Narrower than `working`, on both sides: the unlock's account-key read
      // and a rotation's walk ask the device for nothing. The existing
      // `is not held off by $reason` cases hold the same width from the
      // `working` side; these hold it with `asking` false beside it.
      it.each([
        {
          reason: 'the unlock’s account-key read',
          term: 'unlocking' as const,
        },
        { reason: 'a rotation’s walk', term: 'rotating' as const },
      ])('is not held by $reason', async ({ term }) => {
        // Arrange
        const flow = flowWith(ANSWERED);
        await settle();

        // Act
        screen[term].set(true);

        // Assert
        expect(flow.confirmPressable()).toBe(true);
        expect(readingOf<string | null>(flow, 'confirmHold')()).toBeNull();
      });

      // One sentence at a time. **When both are true the rotation's renders**,
      // the order Change's table uses, so the screen ranks the two the same way
      // wherever both are read.
      it.each([
        { unlock: false, rotation: false, hold: null },
        { unlock: true, rotation: false, hold: 'unlock' },
        { unlock: false, rotation: true, hold: 'rotation' },
        { unlock: true, rotation: true, hold: 'rotation' },
      ])(
        'names $hold as the reason Confirm is off (unlock $unlock, rotation $rotation)',
        async ({ unlock, rotation, hold }) => {
          // Arrange
          const flow = flowWith(ANSWERED);
          await settle();

          // Act
          screen.unlockAsking.set(unlock);
          screen.rotationAsking.set(rotation);

          // Assert
          expect(readingOf<string | null>(flow, 'confirmHold')()).toBe(hold);
        },
      );

      // Its own press is busy, not held: the region says that, and a sentence
      // above the control would say the same thing twice.
      it('names no reason while its own press runs', async () => {
        // Arrange
        const flow = flowWith(ANSWERED);

        // Act
        flow.confirm();
        await requestTo(OPTIONS_URL);

        // Assert
        expect(flow.confirmPressable()).toBe(false);
        expect(readingOf<string | null>(flow, 'confirmHold')()).toBeNull();
      });
    });
  });

  // The flow dies with the screen that provided it, and a press abandoned
  // there asks and sends nothing more.
  // Memory only: the token and the address it asserts are held for one page
  // load, so no web storage may carry either — not while the answer waits,
  // not after the change lands, not after a refusal keeps it waiting.
  describe('web storage', () => {
    function storageHolding(): string[] {
      return [sessionStorage, localStorage].flatMap((storage) =>
        Object.keys(storage).filter((key) => {
          const value = storage.getItem(key) ?? '';

          return value.includes(PROVIDER_TOKEN) || value.includes(NEW_ADDRESS);
        }),
      );
    }

    beforeEach(() => {
      sessionStorage.clear();
      localStorage.clear();
    });

    afterEach(() => {
      sessionStorage.clear();
      localStorage.clear();
    });

    it('holds neither the token nor the address through a press that lands', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      const whileWaiting = storageHolding();

      // Act
      const changing = await reachTheChangingRequest(flow);
      const whileChanging = storageHolding();
      changing.flush({ sessionsEnded: 1 });
      await settle();
      screen.email.set(NEW_ADDRESS);
      screen.answerRead('loaded');
      await settle();

      // Assert
      expect(flow.word()).toBe('changed');
      expect(whileWaiting).toEqual([]);
      expect(whileChanging).toEqual([]);
      expect(storageHolding()).toEqual([]);
    });

    it('holds neither the token nor the address through a refusal that keeps waiting', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);

      // Act
      await refusedWith(flow, ASSERTION_REFUSAL);

      // Assert
      expect(flow.word()).toBe('assertion-refused');
      expect(flow.address()).toBe(NEW_ADDRESS);
      expect(storageHolding()).toEqual([]);
    });
  });

  describe('when the screen goes', () => {
    async function theCeremonyStarts(): Promise<void> {
      await eventually(
        () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
        'the ceremony to start',
      );
    }

    it('cancels the device’s prompt when the screen goes', async () => {
      // Arrange
      ceremony.held = true;
      const { flow, settingsScreen } = flowOnAScreen(ANSWERED);
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();
      const abort = ceremony.assertPasskey.mock.calls[0]?.[1];
      expect(abort, 'the ceremony was handed no abort signal').toBeInstanceOf(
        AbortSignal,
      );
      expect(abort?.aborted).toBe(false);

      // Act
      settingsScreen.destroy();

      // Assert
      expect(abort?.aborted).toBe(true);

      ceremony.settle();
    });

    it('sends no changing request when the screen goes while the device is asked', async () => {
      // Arrange
      ceremony.held = true;
      const { flow, settingsScreen } = flowOnAScreen(ANSWERED);
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      settingsScreen.destroy();
      ceremony.settle();
      await settle();

      // Assert
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
    });

    // The change landed, the re-read is out, and the screen goes before it
    // answers. Nothing is published after that: no word for a region nobody
    // can see, and above all no `changed` claimed by a flow already gone.
    it('publishes nothing when the re-read answers after the screen went', async () => {
      // Arrange
      const { flow, settingsScreen } = flowOnAScreen(ANSWERED);
      const words: (EmailChangeWord | null)[] = [];
      flow.confirm();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      (await requestTo(EMAIL_CHANGE_URL)).flush({ sessionsEnded: 1 });
      await settle();
      expect(screen.loadEmail).toHaveBeenCalledOnce();
      expect(flow.phase()).toBe('changing');

      // Act
      settingsScreen.destroy();
      screen.email.set(NEW_ADDRESS);
      screen.answerRead('loaded');
      for (let turn = 0; turn < 8; turn += 1) {
        await new Promise((resolve) => setTimeout(resolve, 0));
        words.push(flow.word());
      }

      // Assert
      expect(flow.word()).toBeNull();
      expect(words).not.toContain('changed');
      expect(flow.sessionsEnded()).toBeNull();
    });

    it('asks the device nothing when the screen goes before the challenge answers', async () => {
      // Arrange
      const { flow, settingsScreen } = flowOnAScreen(ANSWERED);
      flow.confirm();
      const challenge = await requestTo(OPTIONS_URL);

      // Act
      settingsScreen.destroy();
      if (!challenge.cancelled) {
        challenge.flush(OPTIONS);
      }
      await settle();

      // Assert
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(http.match(EMAIL_CHANGE_URL)).toEqual([]);
    });
  });

  // Nothing printed may carry the provider token or the address it asserts,
  // on any path the changing request can end on. Each body carries both, so
  // a flow that printed the error it caught would print them.
  it.each([
    {
      path: 'a 500',
      status: 500,
      statusText: 'Internal Server Error',
      body: { detail: `${NEW_ADDRESS} ${PROVIDER_TOKEN}` },
      word: 'undetermined',
    },
    {
      path: 'a 401 refusing the provider token',
      status: 401,
      statusText: 'Unauthorized',
      body: {
        ...PROVIDER_TOKEN_REFUSAL,
        detail: `${NEW_ADDRESS} ${PROVIDER_TOKEN}`,
      },
      word: 'provider-refused',
    },
    {
      path: 'a 409',
      status: 409,
      statusText: 'Conflict',
      body: {
        ...EMAIL_ALREADY_LINKED,
        detail: `${NEW_ADDRESS} ${PROVIDER_TOKEN}`,
      },
      word: 'address-taken',
    },
  ])(
    'prints neither the token nor the address on $path',
    async ({ status, statusText, body, word }) => {
      // Arrange
      const spies = spyOnEveryConsoleMethod();
      const flow = flowWith(ANSWERED);

      // Act
      const changing = await reachTheChangingRequest(flow);
      changing.flush(body, { status, statusText });
      if (status === 401) {
        (await requestTo(ME_URL)).flush(ME);
      }
      await settle();

      // Assert
      const printed = [...spies.all.values()]
        .flatMap((spy) => spy.mock.calls)
        .map((call) =>
          JSON.stringify(call, (key, value: unknown) =>
            value instanceof Error ? `${value.name}: ${value.message}` : value,
          ),
        );
      const leaking = printed.filter(
        (line) => line.includes(PROVIDER_TOKEN) || line.includes(NEW_ADDRESS),
      );
      // The control first: a press that never reached this path prints
      // nothing either.
      expect(flow.word()).toBe(word);
      expect(leaking).toEqual([]);
    },
  );
});
