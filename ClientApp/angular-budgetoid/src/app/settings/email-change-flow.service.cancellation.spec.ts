// The email change's two holds against the erasure cancellation, the other half
// of "holds run both ways" (docs/design/components.md, "Holds in both
// directions"). Kept beside `email-change-flow.service.spec.ts` rather than in
// it, because every case here needs one more collaborator that file's screen
// does not build.
//
// - **Confirm is held by the cancellation's passkey check** — its `asking`, the
//   challenge and the ceremony, and nothing wider: the browser runs one passkey
//   check at a time.
// - **Change is held by the cancellation's whole `working`** — the ceremony
//   *and* the cancelling request — because the trip to Google reloads the page
//   and would cut either short.
//
// The cancellation flow is reduced to those two signals, each set on its own
// and neither composed from the other, so a flow reading the wrong one parts
// company with the case. The other seams are `email-change-flow.service.spec.ts`'s.
//
// **Vitest spies persist across cases** (`restoreMocks` is unset), so every spy
// is built fresh inside `beforeEach`.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal, type Signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
import { AccountUnlockService } from './account-unlock.service';
import { EmailChangeFlowService } from './email-change-flow.service';
import { ErasureCancellationFlowService } from './erasure-cancellation-flow.service';
import { RotationFlowService } from './rotation-flow.service';
import { SettingsService } from './settings.service';

const API_ORIGIN = 'https://api.budgetoid.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/reauthentication/options`;

const PROVIDER_TOKEN = 'eyJhbGciOiJSUzI1NiJ9.provider-token-body.sig';
const NEW_ADDRESS = 'new.owner@budgetoid.test';

const PAYLOAD: PasskeyAssertionPayload = {
  credentialId: 'Y3JlZGVudGlhbA',
  clientDataJson: 'Y2xpZW50',
  authenticatorData: 'YXV0aA',
  signature: 'c2ln',
  userHandle: 'dXNlcg',
};

type HandOff = ReturnType<AuthService['takeEmailChangeReturn']>;
type Trip = Awaited<ReturnType<AuthService['startEmailChange']>>;

const ANSWERED: HandOff = {
  kind: 'answered',
  idToken: PROVIDER_TOKEN,
  email: NEW_ADDRESS,
};

// Lets every pending promise in the flow run.
async function settle(): Promise<void> {
  for (let turn = 0; turn < 8; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  TestBed.tick();
}

describe('EmailChangeFlowService held by the erasure cancellation', () => {
  let http: HttpTestingController;
  let assertPasskey: Mock<
    (
      options: PasskeyRequestOptionsJson,
      signal?: AbortSignal,
    ) => Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>>
  >;
  let startEmailChange: Mock<() => Promise<Trip>>;
  let rotationAsking: WritableSignal<boolean>;
  let rotating: WritableSignal<boolean>;
  let exporting: WritableSignal<boolean>;
  let unlockAsking: WritableSignal<boolean>;
  let cancellationAsking: WritableSignal<boolean>;
  let cancellationWorking: WritableSignal<boolean>;

  beforeEach(async () => {
    const keyEncryptionKey = await crypto.subtle.generateKey(
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );

    assertPasskey = vi.fn(() =>
      Promise.resolve({
        ok: true as const,
        value: { payload: PAYLOAD, keyEncryptionKey },
      }),
    );
    startEmailChange = vi.fn<() => Promise<Trip>>(() =>
      Promise.resolve('leaving'),
    );
    rotationAsking = signal(false);
    rotating = signal(false);
    exporting = signal(false);
    unlockAsking = signal(false);
    cancellationAsking = signal(false);
    cancellationWorking = signal(false);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // Builds the flow the way `SettingsComponent` provides it, with the hand-off
  // the page load left.
  function flowWith(handOff: HandOff): EmailChangeFlowService {
    const departing = signal(false);
    const departure: Pick<
      ProviderDepartureService,
      'departing' | 'begin' | 'depart' | 'settle'
    > = {
      departing: departing.asReadonly(),
      begin: () => departing.set(true),
      depart: () => departing.set(true),
      settle: () => departing.set(false),
    };
    const auth: Pick<
      AuthService,
      'takeEmailChangeReturn' | 'startEmailChange'
    > = {
      takeEmailChangeReturn: () => handOff,
      startEmailChange: async (): Promise<Trip> => {
        departure.begin();

        return startEmailChange();
      },
    };
    const settings: Pick<
      SettingsService,
      'exporting' | 'email' | 'emailFailed' | 'loadEmail' | 'loadCredentials'
    > = {
      exporting,
      email: signal<string | null>('current.owner@budgetoid.test'),
      emailFailed: signal(false),
      loadEmail: () => new Promise<'loaded' | 'failed'>(() => undefined),
      loadCredentials: () => undefined,
    };
    const unlock: Pick<AccountUnlockService, 'working' | 'asking'> = {
      working: signal(false),
      asking: unlockAsking,
    };
    const rotation: Pick<RotationFlowService, 'working' | 'asking'> = {
      working: rotating,
      asking: rotationAsking,
    };
    // Typed by shape rather than as a `Pick` of the real class, so this file
    // states what the email change may read of it and nothing more.
    const cancellation: {
      readonly asking: Signal<boolean>;
      readonly working: Signal<boolean>;
    } = {
      asking: cancellationAsking,
      working: cancellationWorking,
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
        {
          provide: WebauthnCeremonyService,
          useValue: { available: () => true, assertPasskey },
        },
        { provide: SettingsService, useValue: settings },
        { provide: AccountUnlockService, useValue: unlock },
        { provide: RotationFlowService, useValue: rotation },
        { provide: ErasureCancellationFlowService, useValue: cancellation },
        EmailChangeFlowService,
      ],
    });

    http = TestBed.inject(HttpTestingController);

    return TestBed.inject(EmailChangeFlowService);
  }

  describe('Confirm', () => {
    it('is held while the cancellation asks the passkey', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      expect(flow.phase()).toBe('waiting');
      expect(flow.confirmPressable()).toBe(true);

      // Act
      cancellationAsking.set(true);
      cancellationWorking.set(true);

      // Assert
      expect(flow.confirmHold()).toBe('cancellation');
      expect(flow.confirmPressable()).toBe(false);
    });

    it('fetches no challenge and asks the device nothing when pressed while the cancellation asks', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      cancellationAsking.set(true);
      cancellationWorking.set(true);

      // Act
      // The handler's gate: a press on a Confirm drawn off still arrives on a
      // `<button>`, and an ungated one raises a second system sheet over the
      // first.
      flow.confirm();
      await settle();

      // Assert
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(assertPasskey).not.toHaveBeenCalled();
      expect(flow.phase()).toBe('waiting');
      expect(flow.word()).toBeNull();
    });

    // **Narrower than `working`.** The cancelling request after the check asks
    // the device for nothing, so it holds nothing here.
    it('is not held while only the cancelling request is out', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      // The control: the same flow is held while the check asks.
      cancellationAsking.set(true);
      cancellationWorking.set(true);
      expect(flow.confirmHold()).toBe('cancellation');

      // Act
      cancellationAsking.set(false);
      cancellationWorking.set(true);

      // Assert
      expect(flow.confirmHold()).toBeNull();
      expect(flow.confirmPressable()).toBe(true);
    });

    it('is pressable again, and its press runs, once the cancellation’s check ends', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();
      cancellationAsking.set(true);
      expect(flow.confirmPressable()).toBe(false);
      flow.confirm();
      await settle();
      expect(http.match(OPTIONS_URL)).toEqual([]);

      // Act
      cancellationAsking.set(false);
      flow.confirm();
      await settle();

      // Assert
      expect(flow.phase()).toBe('asserting');
      expect(http.match(OPTIONS_URL)).toHaveLength(1);
    });

    // One sentence at a time; the rotation's first, the order this flow already
    // ranks its holds in.
    it('names the rotation’s check before the cancellation’s when both ask', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();

      // Act
      rotationAsking.set(true);
      cancellationAsking.set(true);

      // Assert
      expect(flow.confirmHold()).toBe('rotation');
    });

    // And the unlock's before the cancellation's: production's order, which
    // the plan leaves open beyond "one sentence at a time".
    it('names the unlock’s check before the cancellation’s when both ask', async () => {
      // Arrange
      const flow = flowWith(ANSWERED);
      await settle();

      // Act
      unlockAsking.set(true);
      cancellationAsking.set(true);

      // Assert
      expect(flow.confirmHold()).toBe('unlock');
    });
  });

  describe('Change', () => {
    it.each([
      { label: 'asks the passkey', asking: true },
      { label: 'has its request out', asking: false },
    ])('is held while the cancellation $label', ({ asking }) => {
      // Arrange
      const flow = flowWith(null);
      expect(flow.changePressable()).toBe(true);

      // Act
      cancellationAsking.set(asking);
      cancellationWorking.set(true);

      // Assert
      // The trip to Google reloads the page; leaving mid-check or
      // mid-request cuts the cancellation short with nothing to say whether
      // it landed.
      expect(flow.changeHold()).toBe('cancelling');
      expect(flow.changePressable()).toBe(false);
    });

    it('starts no trip when pressed while the cancellation works', async () => {
      // Arrange
      const flow = flowWith(null);
      cancellationWorking.set(true);

      // Act
      flow.change();
      await settle();

      // Assert
      expect(startEmailChange).not.toHaveBeenCalled();
    });

    it('is pressable again once the cancellation has finished working', () => {
      // Arrange
      const flow = flowWith(null);
      cancellationWorking.set(true);
      expect(flow.changePressable()).toBe(false);

      // Act
      cancellationWorking.set(false);

      // Assert
      expect(flow.changeHold()).toBeNull();
      expect(flow.changePressable()).toBe(true);
    });

    // A rotation walking costs the most to break, and it keeps first place.
    it('names the rotation before the cancellation when both work', () => {
      // Arrange
      const flow = flowWith(null);

      // Act
      rotating.set(true);
      cancellationWorking.set(true);

      // Assert
      expect(flow.changeHold()).toBe('rotating');
    });

    // **The cancellation outranks an export**, production's order: a
    // cancellation cut short leaves nothing to say whether the account is
    // still to be erased, where an export cut short loses a file that can be
    // written again.
    it('names the cancellation before the export when both work', () => {
      // Arrange
      const flow = flowWith(null);

      // Act
      exporting.set(true);
      cancellationWorking.set(true);

      // Assert
      expect(flow.changeHold()).toBe('cancelling');
    });

    // The control for the case above: the export, alone, is a reason of its
    // own.
    it('names the export while only the export works', () => {
      // Arrange
      const flow = flowWith(null);

      // Act
      exporting.set(true);

      // Assert
      expect(flow.changeHold()).toBe('exporting');
    });
  });
});
