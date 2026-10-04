// The **Scheduled erasure** section at the top of `/app/settings`, and the
// holds that run both ways between its one control and the four passkey checks
// and one trip already on the screen. See docs/design/components.md, "Holds in
// both directions", and docs/business-logic/erasure.md.
//
// Kept beside `settings.component.spec.ts` rather than in it, because it mounts
// the screen differently. That file replaces the component's whole provider
// array; this one removes only the five providers it stubs, so the
// **cancellation flow the screen provides is the real one**, built on the real
// `MeApiService` and `ReauthenticationApiService` over the testing backend.
// Two consequences: this file imports nothing that does not exist yet — every
// case fails on an assertion until the section ships — and "no challenge was
// fetched" is read off the wire, which a stubbed flow could not say.
//
// The seams replaced are the ones that are not this section's:
// - `SessionService`, reduced to the schedule signal, the token the flow reads
//   before it posts, and the one write the cancellation's 204 makes;
// - `WebauthnCeremonyService`, which reaches `navigator.credentials`;
// - the screen's other flows and services, each a stub of real signals set one
//   at a time, `settings.component.spec.ts`'s discipline.
// `ProviderDepartureService` is the real root one: *departing* is its
// `begin()`.
//
// **The hold sentences are the design book's pattern applied to the new
// control**; the chapter carries no rows for it yet and gains them in the same
// commit. They are pinned whole, so the book and the screen are written from
// one table.
//
// **Vitest spies persist across cases** (`restoreMocks` is unset), so every
// stub and spy is built fresh inside `beforeEach`.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { computed, signal, type WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import type { CredentialSummary } from '@app-core/api/me-api.service';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type CustodyHolding,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import {
  KeyRotationService,
  type KeyRotationFailure,
  type KeyRotationNameCollision,
  type KeyRotationPhase,
  type KeyRotationProgress,
  type KeyRotationRenameRefusal,
  type StagedRotation,
} from '@app-core/security/key-rotation.service';
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
import { ConfigurationService } from '@app-core/services/configuration.service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import {
  SessionService,
  type ScheduledErasure,
  type SessionToken,
} from '@app-core/session/session.service';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import {
  AccountUnlockService,
  type UnlockCeremonyFailure,
} from './account-unlock.service';
import {
  EmailChangeFlowService,
  type EmailChangePhase,
  type EmailChangeWord,
} from './email-change-flow.service';
import {
  ErasureFlowService,
  type ErasureFailure,
  type ErasurePhase,
} from './erasure-flow.service';
import {
  RotationFlowService,
  type RotationCeremonyFailure,
} from './rotation-flow.service';
import { SettingsComponent } from './settings.component';
import { SettingsService, type ExportFailure } from './settings.service';

const API_ORIGIN = 'https://api.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/reauthentication/options`;
const CANCELLATION_URL = `${API_ORIGIN}/api/me/erasure/schedule/cancellation`;

const INSTANT = '2026-10-09T10:30:00Z';

const SECTION_HEADING = 'Scheduled erasure';
const CANCEL = 'Cancel the erasure';
const UNLOCK = 'Unlock';
const ROTATE = 'Rotate keys';
const ERASE = 'Erase everything';
const CONFIRM = 'Confirm with your passkey';
const CHANGE = 'Change email address';
const OUTLINE_CLASS = 'mat-mdc-outlined-button';

// The plan's copy, word for word.
const SECTION_PROSE =
  'This account is scheduled to be erased. Cancelling needs a passkey ' +
  'registered to this account — signing in with Google again doesn’t cancel it.';
const ASKING_LINE = 'Waiting for your passkey.';
const CANCELLING_LINE = 'Cancelling the erasure…';
const RESULT =
  'The erasure is cancelled. Nothing is scheduled for this account.';
const LINES = {
  refused:
    'Budgetoid didn’t accept that passkey, so the erasure is still ' +
    'scheduled. Try again with a passkey registered to this account.',
  dismissed:
    'The passkey check didn’t finish, so the erasure is still scheduled. ' +
    'Try again whenever you’re ready.',
  unstarted:
    'Budgetoid couldn’t start the passkey check, so the erasure is still ' +
    'scheduled. Try again in a minute.',
  unrecognised:
    'Budgetoid couldn’t read this request, so the erasure is still ' +
    'scheduled. Reload the page and try again.',
  undetermined:
    'Budgetoid didn’t hear back, so the erasure may already be cancelled. ' +
    'Press again to check — cancelling twice changes nothing.',
} as const;

const STILL_SCHEDULED = 'so the erasure is still scheduled';

// Why Cancel is off, one sentence per term. The book's pattern: name the
// control, name the other check, and give the one real reason.
const ONE_CHECK =
  'because your browser runs one passkey check at a time. It comes back ' +
  'when that check ends.';
const CANCEL_OFF = {
  departing:
    'Cancelling is off while this tab goes to Google, because if the page ' +
    'left part-way through, nothing could tell you whether the erasure was ' +
    'cancelled.',
  emailAsking: `Cancelling is off while this tab asks your passkey to confirm your email change, ${ONE_CHECK}`,
  unlockAsking: `Cancelling is off while this tab unlocks your account, ${ONE_CHECK}`,
  rotationAsking: `Cancelling is off while this tab asks your passkey for the key rotation, ${ONE_CHECK}`,
} as const;

// What every control the cancellation's passkey check holds says, each naming
// itself and sharing the one reason.
const OFF_WHILE_CANCELLING = {
  unlock: `Unlock is off while this tab asks your passkey to cancel the erasure, ${ONE_CHECK}`,
  rotate: `Rotating keys is off while this tab asks your passkey to cancel the erasure, ${ONE_CHECK}`,
  erase: `Erasing is off while this tab asks your passkey to cancel the erasure, ${ONE_CHECK}`,
  confirm: `Confirming is off while this tab asks your passkey to cancel the erasure, ${ONE_CHECK}`,
  change:
    'Changing your email address is off while this tab cancels the erasure, ' +
    'because the trip to Google would cut it short. It comes back when that ' +
    'ends.',
} as const;

// The two existing sentences a departing tab puts above Unlock, so the
// departing-first case has something to find.
const UNLOCK_OFF_DEPARTING =
  'Unlock is off while this tab goes to Google, because coming back reloads ' +
  'the page and would lock your account again.';

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

const ASSERTION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'The passkey could not be verified.',
  status: 401,
  refusal: 'assertion',
};

// The screen's address row, export and lists — `settings.component.spec.ts`'s
// stub, trimmed to what the template reads.
class SettingsServiceStub {
  public readonly email = signal<string | null>('owner@budgetoid.test');
  public readonly emailFailed = signal(false);
  public readonly exporting = signal(false);
  public readonly exported = signal(false);
  public readonly exportFailure = signal<ExportFailure | null>(null);
  public readonly ready = signal(true);
  public readonly pressable = computed(() => this.ready() && !this.exporting());
  public readonly exportBlock = signal<
    'rotating' | 'locked' | 'departing' | null
  >(null);
  public readonly credentials = signal<readonly CredentialSummary[] | null>(
    null,
  );
  public readonly credentialsFailed = signal(false);
  public readonly recoveryRemaining = signal<number | null>(null);
  public readonly recoveryFailed = signal(false);
  public readonly recoveryLoading = signal(false);
  public loadEmail = vi.fn(
    (): Promise<'loaded' | 'failed'> => new Promise(() => undefined),
  );
  public loadCredentials = vi.fn();
  public loadRecoveryCodes = vi.fn();
  public export = vi.fn();
  public signOut = vi.fn();
}

type AccountKeyCustodySurface = Pick<
  AccountKeyCustodyService,
  keyof AccountKeyCustodyService
>;

class AccountKeyCustodyStub implements AccountKeyCustodySurface {
  public readonly status = signal<AccountKeyStatus>('locked');
  public readonly unlockFailure = signal<UnlockFailure | null>(null);
  public readonly holding = computed((): CustodyHolding | null =>
    this.status() === 'unlocked'
      ? (Object.freeze({}) as unknown as CustodyHolding)
      : null,
  );
  public unlock = vi.fn();
  public adopt = vi.fn();
  public adoptRotated = vi.fn();
  public lock = vi.fn();
  public sealField = vi.fn();
  public openField = vi.fn();
  public blindIndex = vi.fn();
}

type AccountUnlockSurface = Pick<
  AccountUnlockService,
  keyof AccountUnlockService
>;

class AccountUnlockStub implements AccountUnlockSurface {
  public readonly busy = signal(false);
  public readonly failure = signal<UnlockCeremonyFailure | null>(null);
  public readonly working = signal(false);
  public readonly asking = signal(false);
  public unlock = vi.fn();
}

type KeyRotationSurface = Pick<KeyRotationService, keyof KeyRotationService>;

class KeyRotationStub implements KeyRotationSurface {
  public readonly phase = signal<KeyRotationPhase>('idle');
  public readonly progress = signal<KeyRotationProgress>({
    resealed: 0,
    records: 0,
  });
  public readonly failure = signal<KeyRotationFailure | null>(null);
  public readonly staged = signal<StagedRotation | null>(null);
  public readonly collision = signal<KeyRotationNameCollision | null>(null);
  public readonly renameRefusal = signal<KeyRotationRenameRefusal | null>(null);
  public readonly running = signal(false);
  public readonly walking = signal(false);
  public begin = vi.fn(async () => Promise.resolve());
  public resume = vi.fn(async () => Promise.resolve());
  public readStagedRotation = vi.fn(async () => Promise.resolve());
}

type RotationFlowSurface = Pick<RotationFlowService, keyof RotationFlowService>;

class RotationFlowStub implements RotationFlowSurface {
  public readonly busy = signal(false);
  public readonly failure = signal<RotationCeremonyFailure | null>(null);
  public readonly working = signal(false);
  public readonly asking = signal(false);
  public rotate = vi.fn();
  public renameAndFinish = vi.fn();
}

type ErasureFlowSurface = Pick<ErasureFlowService, keyof ErasureFlowService>;

class ErasureFlowStub implements ErasureFlowSurface {
  public readonly phase = signal<ErasurePhase>('idle');
  public readonly failure = signal<ErasureFailure | null>(null);
  public readonly working = signal(false);
  public pressable = vi.fn((): boolean => false);
  public erase = vi.fn();
  public reset = vi.fn();
  public abandon = vi.fn();
}

// Not `implements` a `Pick` of the real class: its two hold readings gain a
// word each in this change, and a stub typed to today's union could not carry
// it. Real signals, set one at a time.
class EmailChangeFlowStub {
  public readonly phase = signal<EmailChangePhase>('rest');
  public readonly word = signal<EmailChangeWord | null>(null);
  public readonly address = signal<string | null>(null);
  public readonly sessionsEnded = signal<number | null>(null);
  public readonly changeHold = signal<string | null>(null);
  public readonly changePressable = signal(true);
  public readonly confirmPressable = signal(false);
  public readonly asking = signal(false);
  public readonly confirmHold = signal<string | null>(null);
  public change = vi.fn();
  public confirm = vi.fn();
}

// One press of the ceremony, answered by hand. `held` keeps the call pending
// until `settle()`, which is the window in which the cancellation is *asking*.
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
}

interface SessionStub {
  readonly status: WritableSignal<'authenticated'>;
  readonly budgetId: WritableSignal<string | null>;
  readonly scheduledErasure: WritableSignal<ScheduledErasure>;
  readonly sessionToken: Mock<() => SessionToken>;
  readonly erasureCancelled: Mock<(sentUnder: SessionToken) => void>;
  readonly ended: Mock<() => void>;
  readonly refreshSchedule: Mock<() => void>;
}

function normalize(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function buttonNamed(
  root: Element | null,
  name: string,
): HTMLButtonElement | null {
  return (
    Array.from(root?.querySelectorAll<HTMLButtonElement>('button') ?? []).find(
      (button) => normalize(button) === name,
    ) ?? null
  );
}

// The innermost element whose whole text is `sentence`.
function elementSaying(root: Element | null, sentence: string): Element | null {
  const matches = Array.from(root?.querySelectorAll('*') ?? []).filter(
    (element) => normalize(element) === sentence,
  );

  return matches.at(-1) ?? null;
}

// The texts of every element a control's `aria-describedby` names.
function describedBy(control: Element | null): string[] {
  return (control?.getAttribute('aria-describedby') ?? '')
    .split(/\s+/)
    .filter((id) => id !== '')
    .map((id) => normalize(document.getElementById(id)));
}

function precedes(first: Element | null, second: Element | null): boolean {
  if (first === null || second === null) {
    return false;
  }

  return (
    (first.compareDocumentPosition(second) &
      Node.DOCUMENT_POSITION_FOLLOWING) !==
    0
  );
}

describe('SettingsComponent and the scheduled erasure', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<SettingsComponent>;
  let host: HTMLElement;
  let session: SessionStub;
  let ceremony: CeremonyStub;
  let settings: SettingsServiceStub;
  let custody: AccountKeyCustodyStub;
  let unlock: AccountUnlockStub;
  let rotations: KeyRotationStub;
  let rotationFlow: RotationFlowStub;
  let erasureFlow: ErasureFlowStub;
  let emailFlow: EmailChangeFlowStub;

  beforeEach(async () => {
    const keyEncryptionKey = await crypto.subtle.generateKey(
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );
    const scheduledErasure = signal<ScheduledErasure>({
      takesEffectAtUtc: INSTANT,
    });

    session = {
      status: signal('authenticated'),
      budgetId: signal<string | null>('3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471'),
      scheduledErasure,
      // One visit for the whole case: what the token guards is the class's
      // own, pinned in `session.service.spec.ts`.
      sessionToken: vi.fn<() => SessionToken>(() => 1 as SessionToken),
      // The real member's contract: the 204 is the server saying nothing is
      // scheduled.
      erasureCancelled: vi.fn<(sentUnder: SessionToken) => void>(() =>
        scheduledErasure.set(null),
      ),
      ended: vi.fn(),
      refreshSchedule: vi.fn(),
    };
    ceremony = new CeremonyStub({ payload: PAYLOAD, keyEncryptionKey });
    settings = new SettingsServiceStub();
    custody = new AccountKeyCustodyStub();
    unlock = new AccountUnlockStub();
    rotations = new KeyRotationStub();
    rotationFlow = new RotationFlowStub();
    erasureFlow = new ErasureFlowStub();
    emailFlow = new EmailChangeFlowStub();

    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: SessionService, useValue: session },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        { provide: AccountKeyCustodyService, useValue: custody },
        { provide: KeyRotationService, useValue: rotations },
        // The five the component provides itself, stubbed here and removed
        // from the component below so the lookup walks up to these. Whatever
        // else the component provides — the cancellation flow — stays real.
        { provide: SettingsService, useValue: settings },
        { provide: AccountUnlockService, useValue: unlock },
        { provide: RotationFlowService, useValue: rotationFlow },
        { provide: ErasureFlowService, useValue: erasureFlow },
        { provide: EmailChangeFlowService, useValue: emailFlow },
      ],
    });
    TestBed.overrideComponent(SettingsComponent, {
      remove: {
        providers: [
          SettingsService,
          AccountUnlockService,
          RotationFlowService,
          ErasureFlowService,
          EmailChangeFlowService,
        ],
      },
    });
    await TestBed.compileComponents();
  });

  afterEach(() => {
    fixture.destroy();
  });

  function render(): void {
    fixture = TestBed.createComponent(SettingsComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    http = TestBed.inject(HttpTestingController);
  }

  function redraw(): void {
    fixture.detectChanges();
  }

  function section(): HTMLElement | null {
    const heading = Array.from(host.querySelectorAll('h2')).find(
      (candidate) => normalize(candidate) === SECTION_HEADING,
    );

    return heading?.closest('section') ?? null;
  }

  function region(): Element | null {
    return section()?.querySelector('[role="status"]') ?? null;
  }

  function cancelControl(): HTMLButtonElement | null {
    return buttonNamed(section(), CANCEL);
  }

  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(
      () => {
        const found = http.match(url);

        if (found.length > 1) {
          throw new Error(`${found.length} requests to ${url}, not one.`);
        }

        return found[0] ?? null;
      },
      `a request to ${url}`,
      redraw,
    );
  }

  // Presses Cancel and answers the challenge, with the device holding: the
  // window in which the cancellation is asking the passkey.
  async function cancellationAsks(): Promise<void> {
    ceremony.held = true;
    cancelControl()?.click();
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);
    await eventually(
      () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
      'the ceremony to start',
      redraw,
    );
    redraw();
  }

  // Presses Cancel and answers the challenge and the ceremony, up to the
  // cancelling request.
  async function reachTheCancellingRequest(): Promise<TestRequest> {
    cancelControl()?.click();
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);

    return requestTo(CANCELLATION_URL);
  }

  async function settle(): Promise<void> {
    for (let turn = 0; turn < 5; turn += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      redraw();
    }
  }

  describe('the section', () => {
    it('opens the screen while an erasure is scheduled', () => {
      // Act
      render();

      // Assert
      // At the top: it is the one thing on the screen with a date on it that
      // the person did not choose.
      const headings = Array.from(host.querySelectorAll('h2')).map(normalize);

      expect(headings[0]).toBe(SECTION_HEADING);
      expect(headings[1]).toBe('Account');
    });

    it('says what cancelling takes, and offers one Outline control', () => {
      // Act
      render();

      // Assert
      const prose = Array.from(section()?.querySelectorAll('p') ?? []).map(
        normalize,
      );

      expect(prose).toContain(SECTION_PROSE);
      expect(
        Array.from(section()?.querySelectorAll('button') ?? []).map(normalize),
      ).toEqual([CANCEL]);
      expect(cancelControl()?.classList.contains(OUTLINE_CLASS)).toBe(true);
      expect(cancelControl()?.getAttribute('type')).toBe('button');
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
    });

    it.each([
      { label: 'nothing is scheduled', value: null },
      { label: 'nothing has been read', value: 'unread' as const },
    ])('is not drawn while $label', ({ value }) => {
      // Arrange
      session.scheduledErasure.set(value);

      // Act
      render();

      // Assert
      // `'unread'` is not a schedule: offering a cancellation of something
      // nobody has said exists would spend a nonce on nothing.
      expect(section()).toBeNull();
      expect(buttonNamed(host, CANCEL)).toBeNull();
    });

    it('asks the server for nothing until the control is pressed', () => {
      // Act
      render();

      // Assert
      expect(cancelControl(), 'the section draws no Cancel').not.toBeNull();
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(http.match(CANCELLATION_URL)).toEqual([]);
    });
  });

  describe('a press', () => {
    it('says it is waiting for the passkey, then that it is cancelling', async () => {
      // Arrange
      render();
      ceremony.held = true;

      // Act
      cancelControl()?.click();
      const options = await requestTo(OPTIONS_URL);
      redraw();

      // Assert
      expect(normalize(region())).toBe(ASKING_LINE);

      // Act
      options.flush(OPTIONS);
      await eventually(
        () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
        'the ceremony to start',
        redraw,
      );
      ceremony.settle();
      const cancelling = await requestTo(CANCELLATION_URL);
      redraw();

      // Assert
      expect(normalize(region())).toBe(CANCELLING_LINE);

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    // **The section outlives the schedule it was drawn for**, for one reason:
    // the result sentence. The 204 publishes `null`, which on its own would
    // take the section — and the sentence saying what happened — off the
    // screen in the same pass.
    it('says the erasure is cancelled, keeps the section and takes the control away', async () => {
      // Arrange
      render();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      const result = await eventually(
        () => elementSaying(section(), RESULT),
        'the result sentence',
        redraw,
      );

      // Assert
      expect(session.erasureCancelled).toHaveBeenCalledTimes(1);
      expect(session.scheduledErasure()).toBeNull();
      expect(result).not.toBeNull();
      expect(cancelControl()).toBeNull();
    });

    // The control the press was made on has gone, so focus would fall to
    // `<body>`; it goes to the sentence that says what happened instead.
    it('moves focus to the result sentence', async () => {
      // Arrange
      render();
      cancelControl()?.focus();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      const result = await eventually(
        () => elementSaying(section(), RESULT),
        'the result sentence',
        redraw,
      );
      await settle();

      // Assert
      expect(result.getAttribute('tabindex')).toBe('-1');
      expect(document.activeElement).toBe(result);
    });

    // **Cancel is off for the whole press**, by the flow's own `pressable`
    // under the screen's holds — and here no other flow holds it, so only the
    // flow's term can. `disabledInteractive` renders that as `aria-disabled`
    // and keeps the native `disabled` off, so the control keeps its tab stop.
    // `aria-busy` says the control is the one doing the work, and is absent
    // at rest rather than `false`.
    it('holds Cancel off and busy while it asks the passkey and while it cancels', async () => {
      // Arrange
      render();
      expect(cancelControl()?.getAttribute('aria-busy')).toBeNull();

      // Act
      await cancellationAsks();

      // Assert
      expect(normalize(region())).toBe(ASKING_LINE);
      expect(cancelControl()?.getAttribute('aria-disabled')).toBe('true');
      expect(cancelControl()?.hasAttribute('disabled')).toBe(false);
      expect(cancelControl()?.getAttribute('aria-busy')).toBe('true');
      expect(describedBy(cancelControl())).toEqual([]);

      // Act
      ceremony.settle();
      const cancelling = await requestTo(CANCELLATION_URL);
      redraw();

      // Assert
      expect(normalize(region())).toBe(CANCELLING_LINE);
      expect(cancelControl()?.getAttribute('aria-disabled')).toBe('true');
      expect(cancelControl()?.getAttribute('aria-busy')).toBe('true');

      cancelling.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert — the refusal hands the control back, idle and not busy.
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
      expect(cancelControl()?.getAttribute('aria-busy')).toBeNull();
    });

    // **Focus reads the result, and nothing else may.** The sentence takes
    // focus on arrival; inside a live region, or one itself, it would be
    // announced a second time. Walked up to the component's host, so a region
    // wrapped around the section counts as well as one on the sentence.
    it('keeps the result sentence out of every live region', async () => {
      // Arrange
      render();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      const result = await eventually(
        () => elementSaying(section(), RESULT),
        'the result sentence',
        redraw,
      );
      await settle();

      // Assert
      const live: string[] = [];

      for (
        let element: Element | null = result;
        element !== null && element !== host;
        element = element.parentElement
      ) {
        const role = element.getAttribute('role');
        const politeness = element.getAttribute('aria-live');

        if (role === 'status' || role === 'alert' || role === 'log') {
          live.push(`${element.tagName} role=${role}`);
        }

        if (politeness !== null && politeness !== 'off') {
          live.push(`${element.tagName} aria-live=${politeness}`);
        }
      }

      expect(live).toEqual([]);
      expect(region()?.contains(result)).toBe(false);
    });

    // **Once, on arrival.** The sentence takes focus when the 204 lands and
    // never again: a person who has moved on is not pulled back each time the
    // screen redraws around a result that has not changed. An export starting
    // redraws the screen's own blocks, which is what makes the result's query
    // answer again.
    it('moves focus to the result sentence once, not on every later redraw', async () => {
      // Arrange
      render();
      const cancelling = await reachTheCancellingRequest();
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      const result = await eventually(
        () => elementSaying(section(), RESULT),
        'the result sentence',
        redraw,
      );
      await settle();
      expect(document.activeElement).toBe(result);
      const elsewhere = buttonNamed(host, UNLOCK);
      elsewhere?.focus();
      expect(document.activeElement).toBe(elsewhere);

      // Act
      settings.exporting.set(true);
      await settle();
      settings.exporting.set(false);
      await settle();

      // Assert
      expect(elementSaying(section(), RESULT)).not.toBeNull();
      expect(document.activeElement).toBe(elsewhere);
    });

    // **Drawn for the press as well as for the schedule.** A refresh landing
    // mid-press can answer nothing scheduled — cancelled from another tab —
    // and the section must not take the control and its region away from a
    // check the device is still running.
    it('keeps the section through a press when a read answers nothing scheduled', async () => {
      // Arrange
      render();
      await cancellationAsks();
      expect(normalize(region())).toBe(ASKING_LINE);

      // Act
      session.scheduledErasure.set(null);
      redraw();

      // Assert
      expect(section()).not.toBeNull();
      expect(cancelControl()).not.toBeNull();
      expect(normalize(region())).toBe(ASKING_LINE);

      ceremony.settle();
      (await requestTo(CANCELLATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await settle();
    });

    // A refusal leaves the control where it is, so focus stays where the
    // press left it; the region says the rest.
    it('says a declined passkey in the region and moves no focus', async () => {
      // Arrange
      render();
      const control = cancelControl();
      control?.focus();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(normalize(region())).toBe(LINES.refused);
      expect(document.activeElement).toBe(control);
      expect(cancelControl()).toBe(control);
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
    });

    it('says a passkey check that did not finish in the region', async () => {
      // Arrange
      render();
      ceremony.answer = { ok: false, failure: 'cancelled' };

      // Act
      cancelControl()?.click();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      expect(normalize(region())).toBe(LINES.dismissed);
    });

    it('says it could not start when the challenge fails', async () => {
      // Arrange
      render();

      // Act
      cancelControl()?.click();
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 500,
        statusText: 'Failed',
      });
      await settle();

      // Assert
      expect(normalize(region())).toBe(LINES.unstarted);
    });

    it.each([
      { word: 'unrecognised' as const, status: 403 },
      { word: 'undetermined' as const, status: 500 },
    ])(
      'says $word when the cancelling request answers $status',
      async ({ word, status }) => {
        // Arrange
        render();
        const cancelling = await reachTheCancellingRequest();

        // Act
        cancelling.flush(null, { status, statusText: 'Refused' });
        await settle();

        // Assert
        expect(normalize(region())).toBe(LINES[word]);
        // Cancelling is idempotent, so neither word withdraws the control.
        expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
      },
    );

    // The erasure dialog's three ceremony sentences, each saying the erasure is
    // still scheduled rather than that nothing was erased.
    it.each([
      {
        failure: 'no-prf' as const,
        opens: 'Your device couldn’t finish the passkey check',
      },
      {
        failure: 'failed' as const,
        opens: 'Your device couldn’t finish the passkey check',
      },
    ] satisfies readonly {
      readonly failure: PasskeyCeremonyFailure;
      readonly opens: string;
    }[])(
      'says a ceremony that ended $failure the dialog’s way, still scheduled',
      async ({ failure, opens }) => {
        // Arrange
        render();
        ceremony.answer = { ok: false, failure };

        // Act
        cancelControl()?.click();
        (await requestTo(OPTIONS_URL)).flush(OPTIONS);
        await settle();

        // Assert
        const said = normalize(region());

        expect(said.startsWith(opens)).toBe(true);
        expect(said).toContain(STILL_SCHEDULED);
        expect(said).not.toContain('nothing was erased');
      },
    );

    it('says a browser that cannot check a passkey the dialog’s way, still scheduled', async () => {
      // Arrange
      render();
      ceremony.supported = false;

      // Act
      cancelControl()?.click();
      await settle();

      // Assert
      const said = normalize(region());

      expect(said.startsWith('This browser can’t check a passkey')).toBe(true);
      expect(said).toContain(STILL_SCHEDULED);
      expect(http.match(OPTIONS_URL)).toEqual([]);
    });
  });

  // **A cancel is not the end of the section.** A Google sign-in can file a
  // new erasure after this one was withdrawn, and a read brings it to this
  // screen. The result sentence is true only while nothing is scheduled, so it
  // gives way to the prose and a live Cancel, and comes back if the new
  // schedule is withdrawn too.
  describe('a schedule filed again after a cancel', () => {
    // Presses Cancel through to its 204 and waits for the result sentence.
    async function cancelled(): Promise<Element> {
      const cancelling = await reachTheCancellingRequest();

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      const result = await eventually(
        () => elementSaying(section(), RESULT),
        'the result sentence',
        redraw,
      );
      await settle();

      return result;
    }

    // A read finding a schedule filed since the cancel.
    async function refiled(): Promise<void> {
      session.scheduledErasure.set({ takesEffectAtUtc: INSTANT });
      await settle();
    }

    it('drops the result and draws the prose and a pressable Cancel', async () => {
      // Arrange
      render();
      await cancelled();

      // Act
      await refiled();

      // Assert
      // *Nothing is scheduled for this account* is false the moment a
      // schedule stands, and the standing prose is true again.
      const prose = Array.from(section()?.querySelectorAll('p') ?? []).map(
        normalize,
      );

      expect(elementSaying(section(), RESULT)).toBeNull();
      expect(prose).toContain(SECTION_PROSE);
      expect(cancelControl(), 'the section draws no Cancel').not.toBeNull();
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
    });

    // **The second press owes the arrival move as the first did.** Pressed
    // without taking focus — what a click does in a browser that does not
    // focus buttons, and what `click()` does here — so nothing but that move
    // can put focus on the result: no focused element leaves with Cancel.
    it('cancels again on a second press and moves focus to the result', async () => {
      // Arrange
      render();
      await cancelled();
      await refiled();
      expect(cancelControl(), 'the section draws no Cancel').not.toBeNull();
      expect(document.activeElement).not.toBe(cancelControl());

      // Act
      const result = await cancelled();

      // Assert
      // A whole second act: a fresh challenge and ceremony.
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(2);
      expect(session.erasureCancelled).toHaveBeenCalledTimes(2);
      expect(document.activeElement).toBe(result);
    });

    // **The arrival move belongs to a press, and to the render it lands in.**
    // A result that comes back because another tab withdrew the new schedule
    // arrives under nobody's hand, and focus stays where the person put it.
    it('moves no focus when the result comes back without a press', async () => {
      // Arrange
      render();
      await cancelled();
      await refiled();
      expect(elementSaying(section(), RESULT)).toBeNull();
      const elsewhere = buttonNamed(host, UNLOCK);
      elsewhere?.focus();
      expect(document.activeElement).toBe(elsewhere);

      // Act
      session.scheduledErasure.set(null);
      await settle();

      // Assert
      expect(elementSaying(section(), RESULT)).not.toBeNull();
      expect(document.activeElement).toBe(elsewhere);
    });

    // **The move owed by a 204 is dropped when no result is drawn for it.** A
    // 204 that spoke for an older visit publishes nothing, so the schedule
    // stands and no result is drawn; a read withdrawing it later draws the
    // result under nobody's hand, and a move still owed from the 204 would
    // pull focus to it.
    it('moves no focus to a result first drawn after a 204 that published nothing', async () => {
      // Arrange
      // The real member's no-op for a token from another visit.
      session.erasureCancelled.mockImplementation(() => undefined);
      render();
      const cancelling = await reachTheCancellingRequest();
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      expect(session.erasureCancelled).toHaveBeenCalledTimes(1);
      expect(elementSaying(section(), RESULT)).toBeNull();
      const elsewhere = buttonNamed(host, UNLOCK);
      elsewhere?.focus();
      expect(document.activeElement).toBe(elsewhere);

      // Act
      session.scheduledErasure.set(null);
      await settle();

      // Assert
      expect(elementSaying(section(), RESULT)).not.toBeNull();
      expect(document.activeElement).toBe(elsewhere);
    });

    // **Focus leaving with Cancel is rescued to the sentence that replaced
    // it.** The new schedule withdrawn elsewhere takes Cancel out from under
    // focus and draws the result in its place.
    it('moves focus to the result when Cancel leaves under a schedule cancelled elsewhere', async () => {
      // Arrange
      render();
      await cancelled();
      await refiled();
      cancelControl()?.focus();
      expect(document.activeElement).toBe(cancelControl());

      // Act
      session.scheduledErasure.set(null);
      await settle();

      // Assert
      const result = elementSaying(section(), RESULT);

      expect(result, 'the section draws no result').not.toBeNull();
      expect(cancelControl()).toBeNull();
      expect(document.activeElement).toBe(result);
    });
  });

  // **Focus never falls to `<body>` because the section left.** A read can
  // take the section away under the control that holds focus — the schedule
  // withdrawn from another tab — and with no result to land on, focus goes to
  // the screen's heading, which takes it without becoming a tab stop. Focus
  // the person put anywhere else is theirs and is not touched.
  describe('focus when the section leaves', () => {
    function heading(): HTMLHeadingElement | null {
      return host.querySelector('h1');
    }

    it('moves focus to the heading when the section leaves while Cancel holds focus', async () => {
      // Arrange
      render();
      cancelControl()?.focus();
      expect(document.activeElement).toBe(cancelControl());

      // Act
      session.scheduledErasure.set(null);
      await settle();

      // Assert
      expect(section()).toBeNull();
      expect(heading()?.getAttribute('tabindex')).toBe('-1');
      expect(document.activeElement).toBe(heading());
    });

    // The section is kept through the press, so it leaves only when the
    // refusal hands the press back with nothing scheduled.
    it('moves focus to the heading when a refused press ends after a read answered nothing scheduled', async () => {
      // Arrange
      render();
      cancelControl()?.focus();
      await cancellationAsks();
      session.scheduledErasure.set(null);
      redraw();
      expect(document.activeElement).toBe(cancelControl());
      ceremony.settle();
      const cancelling = await requestTo(CANCELLATION_URL);

      // Act
      cancelling.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(section()).toBeNull();
      expect(document.activeElement).toBe(heading());
    });

    it('leaves focus where it is when the section leaves while focus is elsewhere', async () => {
      // Arrange
      render();
      // Cancel held focus once, so a rescue that remembered it and ignored
      // where focus is now would fire here.
      cancelControl()?.focus();
      const elsewhere = buttonNamed(host, UNLOCK);
      elsewhere?.focus();
      expect(document.activeElement).toBe(elsewhere);

      // Act
      session.scheduledErasure.set(null);
      await settle();

      // Assert
      expect(section()).toBeNull();
      expect(document.activeElement).toBe(elsewhere);
    });
  });

  // **Cancel is held by four terms and nothing else.** Departing — the page is
  // leaving for Google and would cut the check short. And the three other
  // passkey checks on this screen, each its own flow's *asking*: the browser
  // runs one at a time.
  describe('Cancel held off', () => {
    const TERMS = [
      {
        term: 'departing',
        hold: (): void => TestBed.inject(ProviderDepartureService).begin(),
        sentence: CANCEL_OFF.departing,
      },
      {
        term: 'the email change’s passkey check',
        hold: (): void => emailFlow.asking.set(true),
        sentence: CANCEL_OFF.emailAsking,
      },
      {
        term: 'the unlock’s passkey check',
        hold: (): void => {
          unlock.asking.set(true);
          unlock.working.set(true);
        },
        sentence: CANCEL_OFF.unlockAsking,
      },
      {
        term: 'a rotation’s passkey check',
        hold: (): void => {
          rotationFlow.asking.set(true);
          rotationFlow.working.set(true);
        },
        sentence: CANCEL_OFF.rotationAsking,
      },
    ];

    it('is live at rest with no sentence naming it', () => {
      // Act
      render();

      // Assert
      // The control for every case below.
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
      expect(cancelControl()?.hasAttribute('aria-describedby')).toBe(false);
      for (const sentence of Object.values(CANCEL_OFF)) {
        expect(elementSaying(host, sentence)).toBeNull();
      }
    });

    it.each(TERMS)(
      'is off while $term, and says why above itself',
      ({ hold, sentence }) => {
        // Arrange
        render();

        // Act
        hold();
        redraw();

        // Assert
        const said = elementSaying(section(), sentence);

        expect(cancelControl()?.getAttribute('aria-disabled')).toBe('true');
        // `disabledInteractive`: it keeps its tab stop.
        expect(cancelControl()?.disabled).toBe(false);
        expect(said, `the section does not say: ${sentence}`).not.toBeNull();
        expect(precedes(said, cancelControl())).toBe(true);
        expect(region()?.contains(said)).toBe(false);
        expect(describedBy(cancelControl())).toContain(sentence);
      },
    );

    it.each(TERMS)(
      'fetches no challenge and asks the device nothing when pressed while $term',
      async ({ hold }) => {
        // Arrange
        render();
        hold();
        redraw();
        expect(cancelControl(), 'the section draws no Cancel').not.toBeNull();

        // Act
        // Material halts the click on anchors only; on a `<button>` the press
        // arrives, and the handler reads the same predicate the attribute
        // does.
        cancelControl()?.click();
        await settle();

        // Assert
        expect(http.match(OPTIONS_URL)).toEqual([]);
        expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      },
    );

    it('says departing’s sentence and only that one when departing and another term hold', () => {
      // Arrange
      render();

      // Act
      TestBed.inject(ProviderDepartureService).begin();
      emailFlow.asking.set(true);
      redraw();

      // Assert
      // Departing ends the screen and every other sentence on it.
      expect(elementSaying(section(), CANCEL_OFF.departing)).not.toBeNull();
      expect(elementSaying(section(), CANCEL_OFF.emailAsking)).toBeNull();
      expect(describedBy(cancelControl())).not.toContain(
        CANCEL_OFF.emailAsking,
      );
    });

    // **Then the email change's, the unlock's and the rotation's, in that
    // order** — production's, and the order the plan lists Cancel's holds in;
    // the plan says only "one sentence at a time, departing first" about which
    // wins. Adjacent pairs, which with departing's case above pin the order
    // whole.
    it.each([
      {
        pair: 'the email change’s and the unlock’s checks',
        hold: (): void => {
          emailFlow.asking.set(true);
          unlock.asking.set(true);
        },
        said: CANCEL_OFF.emailAsking,
        unsaid: CANCEL_OFF.unlockAsking,
      },
      {
        pair: 'the unlock’s and a rotation’s checks',
        hold: (): void => {
          unlock.asking.set(true);
          rotationFlow.asking.set(true);
        },
        said: CANCEL_OFF.unlockAsking,
        unsaid: CANCEL_OFF.rotationAsking,
      },
    ])(
      'says one sentence when $pair both hold, the first of the two',
      ({ hold, said, unsaid }) => {
        // Arrange
        render();

        // Act
        hold();
        redraw();

        // Assert
        expect(elementSaying(section(), said)).not.toBeNull();
        expect(elementSaying(section(), unsaid)).toBeNull();
        expect(describedBy(cancelControl())).toEqual([said]);
      },
    );

    // Everything else on the screen that works asks the device for nothing —
    // or, for the erasure dialog, is modal over this control.
    it.each([
      { label: 'an export', arrange: (): void => settings.exporting.set(true) },
      {
        label: 'the unlock’s account-key read',
        arrange: (): void => unlock.working.set(true),
      },
      {
        label: 'a rotation’s walk',
        arrange: (): void => rotationFlow.working.set(true),
      },
      {
        label: 'the email change’s changing request',
        arrange: (): void => emailFlow.phase.set('changing'),
      },
      {
        label: 'custody opening the account',
        arrange: (): void => custody.status.set('unlocking'),
      },
    ])('is not held by $label', async ({ arrange }) => {
      // Arrange
      render();
      arrange();
      redraw();

      // Act
      cancelControl()?.click();

      // Assert
      expect(cancelControl()?.getAttribute('aria-disabled')).not.toBe('true');
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 500,
        statusText: 'Failed',
      });
      await settle();
    });
  });

  // **The hold runs the other way.** While the cancellation asks the passkey,
  // every other passkey check on the screen is held — and Change, which would
  // leave the page, is held for the cancellation's whole run.
  describe('controls the cancellation holds off', () => {
    const ASKING_HOLDS = [
      {
        control: UNLOCK,
        sentence: OFF_WHILE_CANCELLING.unlock,
        find: (): HTMLButtonElement | null => buttonNamed(host, UNLOCK),
        pressed: (): boolean => unlock.unlock.mock.calls.length > 0,
      },
      {
        control: ROTATE,
        sentence: OFF_WHILE_CANCELLING.rotate,
        find: (): HTMLButtonElement | null =>
          buttonNamed(host.querySelector('app-key-rotation-section'), ROTATE),
        pressed: (): boolean => rotationFlow.rotate.mock.calls.length > 0,
      },
      {
        control: ERASE,
        sentence: OFF_WHILE_CANCELLING.erase,
        find: (): HTMLButtonElement | null => buttonNamed(host, ERASE),
        // `openErasure` resets the flow before it opens anything.
        pressed: (): boolean => erasureFlow.reset.mock.calls.length > 0,
      },
    ];

    // The rotation control waits on its acknowledgement; ticked, the
    // cancellation is the only thing holding it.
    function acknowledgeRotation(): void {
      host
        .querySelector<HTMLInputElement>(
          'app-key-rotation-section input[type="checkbox"]',
        )
        ?.click();
      redraw();
    }

    it.each(ASKING_HOLDS)(
      'holds $control while the cancellation asks the passkey, and says why',
      async ({ sentence, find }) => {
        // Arrange
        render();
        acknowledgeRotation();
        expect(find()?.getAttribute('aria-disabled')).not.toBe('true');

        // Act
        await cancellationAsks();

        // Assert
        const said = elementSaying(host, sentence);

        expect(find()?.getAttribute('aria-disabled')).toBe('true');
        expect(said, `the screen does not say: ${sentence}`).not.toBeNull();
        expect(precedes(said, find())).toBe(true);
        expect(describedBy(find())).toContain(sentence);

        ceremony.settle();
        (await requestTo(CANCELLATION_URL)).flush(null, {
          status: 204,
          statusText: 'No Content',
        });
        await settle();
      },
    );

    it.each(ASKING_HOLDS)(
      'refuses a press of $control while the cancellation asks the passkey',
      async ({ find, pressed }) => {
        // Arrange
        render();
        acknowledgeRotation();
        await cancellationAsks();

        // Act
        find()?.click();
        redraw();

        // Assert
        expect(pressed()).toBe(false);

        ceremony.settle();
        (await requestTo(CANCELLATION_URL)).flush(null, {
          status: 204,
          statusText: 'No Content',
        });
        await settle();
      },
    );

    // Asking is the check and nothing wider: once the cancelling request is
    // out, the device is asked nothing and the three come back.
    it.each(ASKING_HOLDS)(
      'gives $control back once the passkey check ends',
      async ({ sentence, find }) => {
        // Arrange
        render();
        acknowledgeRotation();
        await cancellationAsks();

        // Act
        ceremony.settle();
        const cancelling = await requestTo(CANCELLATION_URL);
        redraw();

        // Assert
        expect(elementSaying(host, sentence)).toBeNull();
        expect(find()?.getAttribute('aria-disabled')).not.toBe('true');

        cancelling.flush(null, { status: 204, statusText: 'No Content' });
        await settle();
      },
    );

    it('says departing’s sentence above Unlock, not the cancellation’s, when both hold', async () => {
      // Arrange
      render();
      await cancellationAsks();

      // Act
      TestBed.inject(ProviderDepartureService).begin();
      redraw();

      // Assert
      expect(elementSaying(host, UNLOCK_OFF_DEPARTING)).not.toBeNull();
      expect(elementSaying(host, OFF_WHILE_CANCELLING.unlock)).toBeNull();

      ceremony.settle();
      (await requestTo(CANCELLATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await settle();
    });

    // Confirm and Change read the email change's own readings, which that
    // flow composes from the cancellation (see
    // `email-change-flow.service.cancellation.spec.ts`); here, the sentence
    // each reading draws.
    it('says why Confirm is off while the cancellation asks the passkey', () => {
      // Arrange
      emailFlow.phase.set('waiting');
      emailFlow.address.set('new.owner@budgetoid.test');
      emailFlow.confirmHold.set('cancellation');
      emailFlow.confirmPressable.set(false);

      // Act
      render();

      // Assert
      const confirm = buttonNamed(host, CONFIRM);
      const said = elementSaying(host, OFF_WHILE_CANCELLING.confirm);

      expect(said).not.toBeNull();
      expect(precedes(said, confirm)).toBe(true);
      expect(describedBy(confirm)).toContain(OFF_WHILE_CANCELLING.confirm);
      expect(confirm?.getAttribute('aria-disabled')).toBe('true');
    });

    it('says why Change is off while the cancellation works', () => {
      // Arrange
      emailFlow.changeHold.set('cancelling');
      emailFlow.changePressable.set(false);

      // Act
      render();

      // Assert
      const change = buttonNamed(host, CHANGE);
      const said = elementSaying(host, OFF_WHILE_CANCELLING.change);

      expect(said).not.toBeNull();
      expect(precedes(said, change)).toBe(true);
      expect(describedBy(change)).toEqual([OFF_WHILE_CANCELLING.change]);
      expect(change?.getAttribute('aria-disabled')).toBe('true');
    });
  });
});

// Waits for a public reading to arrive, redrawing between tries.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
  between: () => void = () => undefined,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
    between();
  }

  throw new Error(`Timed out waiting for ${what}.`);
}
