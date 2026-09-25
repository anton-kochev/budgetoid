// What the screen does around the one press that creates an account.
//
// `register.component.spec.ts` owns the flow end to end and owns the two
// sentences a refusal and a lost answer must never be collapsed into. This file
// owns three things beside them that a review found missing, each needing a
// fixture that one does not have:
//
//   * **the 409 after a *refused* first attempt.** It is not the 409 after a
//     lost one, and the screen currently cannot tell them apart: it forks on
//     whether `Start again` was pressed, and `Start again` is offered from two
//     states. A 400 is a judgement — every 400, 401 and 403 leaves
//     `RegisterAccountHandler` before a row is written — so after one, nothing
//     was created, the passkey the device made was never seen by the server,
//     and the sentence claiming the first attempt worked is false in every
//     clause. Somebody told it goes looking for an account that does not exist,
//     with a passkey the server will refuse with a byte-identical 401 and no
//     sentence anywhere naming the cause.
//
//   * **the wait while the account is being created.** ~30 rows across nine
//     relations in one save, and the longest wait in the flow. It is also the
//     only press in this client with nothing behind it: step 2 announces its
//     wait and so does the welcome screen, and this one — the press that
//     matters most — renders unchanged while the request is out.
//
//   * **a way off both conflict states.** Each tells the reader to go and sign
//     in, and `/register` has no navigation of any kind; `app.component.html`
//     is a bare `<router-outlet />`. `Start again` is genuinely the wrong
//     control there — it spends another challenge to be refused again — but
//     that argument reaches "not this control", never "no control".
//
// The router is the **real** one with its single outward call recorded, rather
// than the object stub `register.component.spec.ts` provides. That is what lets
// the third subject be checked without being decided: a way out written as an
// `<a routerLink>` and one written as a `<button>` calling `navigateByUrl` land
// in the same recording, so these tests say where the screen goes and leave how
// it is written to whoever writes it.
//
// One further test here pins a busy state that already ships — the passkey
// step's — and it is not padding: deleting that step's whole busy branch was
// measured to leave the suite green, its own spec included. So "a press that
// starts something says so" is enforced nowhere in this flow, and the test
// below is the first place it is. The rest of the fixture is a deliberate copy
// of the sibling file's, which is the DAMP trade the house makes in tests and
// the reason a reader can start here.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, UrlTree, provideRouter } from '@angular/router';
import type { RecoveryCode } from '@app-core/security/recovery-codes';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyResult,
  type PasskeyRegistrationCeremony,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyCreationOptionsJson,
  PasskeyRegistrationPayload,
} from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RegisterComponent } from './register.component';
import { RegisterService } from './register.service';

const API_BASE_URL = 'https://api.test';
const OPTIONS_URL = `${API_BASE_URL}/api/registration/options`;
const REGISTRATION_URL = `${API_BASE_URL}/api/registration`;
const OWNER_EMAIL = 'owner@budgetoid.test';

// The screen that can actually sign a returning person in, and the address both
// conflict sentences send them to in words. `/welcome` runs the passkey
// assertion; it is the only route in this application that does.
const WELCOME_URL = '/welcome';

// The controls, by the names a screen reader announces them under.
const CONTINUE_BUTTON = 'Continue';
const CREATE_PASSKEY_BUTTON = 'Create a passkey';
const CREATE_ACCOUNT_BUTTON = 'Create account';
const RESTART_BUTTON = 'Start again';

// The two readings of one 409, restated here because this file is where the
// third path into them is driven. The server sends four distinct sentences in
// `ProblemDetails.Detail` under an identical title with no machine-readable
// code, so the client cannot tell them apart and must never match on the text —
// it forks on what it knows about its own attempts, and on nothing else.
const CONFLICT =
  'An account already exists for this Google address. Nothing was created here, and the ten codes you were just shown open nothing — sign in from the Budgetoid home page instead.';
const CONFLICT_AFTER_RESTART =
  'Your first attempt did create your account — its answer just didn’t reach this browser. Sign in with the passkey you made on that attempt. The ten codes you were shown a moment ago open nothing; the ten from the first attempt are the ones that work.';

// A realistic answer from `POST /api/registration/options`. Every binary member
// is unpadded base64url over bytes a server could actually have sent.
const CREATION_OPTIONS = {
  challenge: 'QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8',
  rp: { id: 'budgetoid.app', name: 'Budgetoid' },
  user: {
    id: 'EBESExQVFhcYGRobHB0eHw',
    name: OWNER_EMAIL,
    displayName: OWNER_EMAIL,
  },
  pubKeyCredParams: [
    { type: 'public-key', alg: -7 },
    { type: 'public-key', alg: -257 },
  ],
  timeout: 120_000,
  attestation: 'none',
  authenticatorSelection: {
    residentKey: 'required',
    requireResidentKey: true,
    userVerification: 'required',
  },
  excludeCredentials: [],
  extensions: { prf: {} },
} satisfies PasskeyCreationOptionsJson;

const REGISTRATION_PAYLOAD = {
  clientDataJson:
    'eyJ0eXBlIjoid2ViYXV0aG4uY3JlYXRlIiwiY2hhbGxlbmdlIjoiUUVGQ1EwUkZSa2' +
    'RJU1VwTFRFMU9UMUJSVWxOVVZWWlhXRmxhVzF4ZFhsOCIsIm9yaWdpbiI6Imh0dHBz' +
    'Oi8vYnVkZ2V0b2lkLmFwcCIsImNyb3NzT3JpZ2luIjpmYWxzZX0',
  attestationObject:
    'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp-goaKjpKWmp6ipqqusra6v',
  clientExtensionResults: { prf: { enabled: true } },
} satisfies PasskeyRegistrationPayload;

// What a conflict state offers, and which of those controls reaches the screen
// that can sign somebody in. Both halves are carried because they fail
// differently and a reader needs to know which happened: an empty `offered` is
// a dead end, and a non-empty `offered` with an empty `reaching` is a screen
// with controls that go somewhere else.
interface WayOut {
  readonly offered: readonly string[];
  readonly reaching: readonly string[];
}

describe('RegisterComponent, around the press that creates the account', () => {
  let fixture: ComponentFixture<RegisterComponent> | null = null;
  let host: HTMLElement;
  let http: HttpTestingController;
  let service: RegisterService;
  // Where the router was asked to go, in order. A `routerLink` and a
  // programmatic `navigate` both land here — `RouterLink` calls `navigateByUrl`
  // itself — so the recording says where the screen goes without saying how the
  // control was written.
  let navigations: string[];
  let keyEncryptionKey: CryptoKey;
  // Whether the authenticator is still thinking about it.
  //
  // **The only way left to hold the passkey step busy.** The wait used to be
  // held open by leaving the options request unanswered, and there is no such
  // request on that press any more: `Continue` fetched the challenge one screen
  // earlier. What the step is actually waiting for is the system sheet, so this
  // is the honest instrument for it as well — a promise that never settles is
  // exactly a sheet nobody has answered yet.
  let ceremonyPending: boolean;

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    navigations = [];
    ceremonyPending = false;

    // The one seam that has to exist: `available()` and `createPasskey()` both
    // touch `navigator.credentials`, which this runner does not implement. The
    // willing answer, because every test in this file is about what happens
    // after the device has agreed.
    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'createPasskey'
    > = {
      available: () => true,
      createPasskey: (): Promise<
        PasskeyCeremonyResult<PasskeyRegistrationCeremony>
      > =>
        ceremonyPending
          ? new Promise<PasskeyCeremonyResult<PasskeyRegistrationCeremony>>(
              () => undefined,
            )
          : Promise.resolve({
              ok: true,
              value: { payload: REGISTRATION_PAYLOAD, keyEncryptionKey },
            }),
    };

    await TestBed.configureTestingModule({
      imports: [RegisterComponent],
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => true,
            forgetProviderToken: () => undefined,
            // The address the introduction shows back, and the reason the
            // `Continue` control is on the screen at all — with `null` here the
            // first step offers provider sign-in instead.
            providerEmail: () => OWNER_EMAIL,
            signIn: () => undefined,
          },
        },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_BASE_URL }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        // `RegisterService` is deliberately **not** listed. The component
        // provides it, so it dies with the screen and takes the account keys,
        // the eleven key-encryption keys and the ten codes with it; listing it
        // here would resolve it from the root injector and quietly make this
        // file green on a component that had dropped its own `providers`.
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);

    const router = TestBed.inject(Router);

    // The real router, with its one outward call recorded rather than run: a
    // router with no declared routes rejects `/app` into a promise nothing
    // awaits, and the rejection surfaces as an unrelated failure three tests
    // later. Recording it is also the whole instrument for the two dead-end
    // tests below, which must not care whether the way out is an anchor or a
    // button.
    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        navigations.push(
          typeof url === 'string' ? url : router.serializeUrl(url),
        );

        return Promise.resolve(true);
      },
    );

    render();
  });

  // No `http.verify()` teardown, for the reason `register.component.spec.ts`
  // gives: two tests below end with a request outstanding on purpose, because
  // the outstanding request *is* the subject.

  it('says an account already exists when the first attempt was refused', async () => {
    // Arrange
    // A 400, then a restart, then a 409 — and every step of it is ordinary.
    // Somebody who already has an account sits on the system sheet past the
    // challenge's lifetime, is refused, presses the one control on offer, runs
    // a clean second ceremony, and meets the account they have had all along.
    await driveToConflictAfterARefusal();

    // Assert
    // **The predicate is not "did they press restart".** A 400 leaves the
    // handler before `RegisterAsync` is reached, so the first attempt wrote
    // nothing: no account, no credential, and no record anywhere of the passkey
    // the authenticator made. Saying the first attempt worked sends that person
    // to sign in with a passkey the server has never seen, which answers a
    // byte-identical 401 — the same 401 an unknown credential, a bad signature
    // and a spent challenge get, deliberately — and leaves them in a closed
    // loop with no sentence naming the cause.
    //
    // What the fork needs to be is whether the **previous POST ended as
    // `unknown`**, which is the only state in which anything may have been
    // written.
    expect(elementSaying(host, CONFLICT)).not.toBeNull();
    expect(elementSaying(host, CONFLICT_AFTER_RESTART)).toBeNull();
    // Stated outright, because a copy edit that collapsed the two sentences
    // into one would leave every other line in this file green.
    expect(CONFLICT).not.toBe(CONFLICT_AFTER_RESTART);
  });

  it('says the earlier attempt worked when a lost answer is followed by a conflict', async () => {
    // Arrange
    // The other half of the same fork, and the case the after-restart sentence
    // was written for: the first POST committed its rows and lost the 201
    // coming back. It is held in `register.component.spec.ts` too — restated
    // here because the test above is only meaningful beside it. A screen that
    // answered the plain sentence to every 409 alike would pass that one on its
    // own.
    await driveToConflictAfterALostAnswer();

    // Assert
    expect(elementSaying(host, CONFLICT_AFTER_RESTART)).not.toBeNull();
    expect(elementSaying(host, CONFLICT)).toBeNull();
  });

  it('says the account is being created while the request is in flight', async () => {
    // Arrange
    await driveToCodes();
    // The control for the assertion below: the region is in the DOM from first
    // paint and empty at rest, so a screen that always held a line would pass
    // the next assertion having announced nothing.
    expect(
      liveRegionText(),
      'the codes step is already announcing something before the press.',
    ).toBe('');

    // Act
    acknowledge();
    press(CREATE_ACCOUNT_BUTTON);

    const request = await eventually(
      () => http.match(REGISTRATION_URL)[0] ?? null,
      'the registration request',
    );
    await settle();

    // Assert
    // **Something is said, and it is said where it is heard.** This is the
    // longest wait in the product — ~30 rows across nine relations in one save,
    // over a request that may cross a continent — and the press before it is
    // the one that cannot be taken back. A screen that renders unchanged for
    // several seconds reads as a control that did nothing, which is what
    // invites the second press. The words are the author's; a live region is
    // not, because a person who cannot see the screen is told by nothing else,
    // and both neighbouring screens already announce their wait in one.
    expect(
      liveRegionText(),
      'nothing on the screen says the account is being created.',
    ).not.toBe('');

    // And the press cannot be made twice. The control may be gone altogether —
    // a busy state that replaces the step is a shape this test deliberately
    // permits — but if it is on the screen it must say it is unavailable, and
    // either way no second request may leave.
    const create = controlNamed(host, CREATE_ACCOUNT_BUTTON);

    if (create !== null) {
      expect(
        unavailable(create),
        'the create control is still offered as pressable while the account ' +
          'is being created.',
      ).toBe(true);
      create.click();
      fixture?.detectChanges();
    }

    expect(
      http.match(REGISTRATION_URL),
      'a second registration was posted while the first was outstanding.',
    ).toHaveLength(0);
    // The first one is still out, which is what makes every line above a
    // statement about a request in flight rather than about one that ended.
    expect(request.cancelled).toBe(false);
  });

  it('offers a way to sign in when an account already exists', async () => {
    // Arrange & Act
    const wayOut = await waysOut(driveToPlainConflict);

    // Assert
    // The sentence in this branch ends "sign in from the Budgetoid home page
    // instead", and `/register` has no navigation of any kind — the shell is a
    // bare `<router-outlet />`. So the person is told to go somewhere with
    // nothing to press, which is a dead end whatever the copy says.
    //
    // `Start again` is the wrong control here and this does not ask for it: it
    // would spend another challenge and another passkey to meet the same 409.
    // What is asked for is any control that reaches the one screen that can
    // sign a returning person in.
    expect(
      wayOut.offered,
      'the conflict state renders no control at all.',
    ).not.toEqual([]);
    expect(
      wayOut.reaching,
      `none of ${JSON.stringify(wayOut.offered)} reaches ${WELCOME_URL}.`,
    ).not.toEqual([]);
  });

  it('offers a way to sign in when the earlier attempt created the account', async () => {
    // Arrange & Act
    const wayOut = await waysOut(driveToConflictAfterALostAnswer);

    // Assert
    // The harder of the two, and the one where the dead end costs most. This
    // person holds an account, a passkey that opens it and ten codes that do —
    // all made on an attempt they were told Budgetoid could not vouch for — and
    // the screen's own sentence tells them to go and use the passkey. Sending
    // them to look for the door themselves, from a screen with no navigation,
    // is the last step of a flow that has already asked a great deal of them.
    expect(
      wayOut.offered,
      'the after-restart conflict state renders no control at all.',
    ).not.toEqual([]);
    expect(
      wayOut.reaching,
      `none of ${JSON.stringify(wayOut.offered)} reaches ${WELCOME_URL}.`,
    ).not.toEqual([]);
  });

  it('says the device is being waited on while the ceremony runs', async () => {
    // Arrange
    // A pin on a busy state that already ships, and it is here because nothing
    // held it: a review deleted the passkey step's whole busy branch and the
    // suite stayed green. What that branch buys is the reason the wait is
    // bearable at all — a system sheet is about to appear, and the screen has
    // to admit it is waiting for one.
    //
    // **The wait is held open by the ceremony and not by a request**, which is
    // the half this test had to be rebuilt for. It used to hold the step busy
    // by leaving the options request unanswered; that request now leaves on
    // `Continue`, one screen earlier, so a press waiting on it here would be
    // waiting on nothing and the busy branch would never render.
    ceremonyPending = true;
    await pressContinue();
    expect(liveRegionText()).toBe('');

    // Act
    press(CREATE_PASSKEY_BUTTON);
    await settle();

    // Assert
    expect(
      liveRegionText(),
      'nothing on the screen says the device is being waited on.',
    ).not.toBe('');

    // **And this press asked for nothing.** The challenge is the one `Continue`
    // fetched: a second request here spends a second nonce the server has
    // persisted — on this route it is also the value the account identifier is
    // derived from — and strands the first, which is a passkey bound to an
    // account id nobody will ever be able to sign in under.
    expect(
      http.match(OPTIONS_URL),
      'the passkey step asked for a challenge it was already holding.',
    ).toHaveLength(0);

    const create = controlNamed(host, CREATE_PASSKEY_BUTTON);

    if (create !== null) {
      expect(unavailable(create)).toBe(true);
      create.click();
      fixture?.detectChanges();
    }

    // And a second press cannot start a second ceremony, or spend a challenge
    // on the way to one.
    expect(
      http.match(OPTIONS_URL),
      'a second challenge was asked for while the first ceremony was running.',
    ).toHaveLength(0);
    expect(
      service.codes(),
      'the ceremony finished while the device was still being waited on.',
    ).toBeNull();
  });

  // The codes step's own `disabledInteractive` gets no test here, and that is a
  // measurement rather than an oversight: deleting the attribute from
  // `codes-step.component.html` reddens two tests in that step's own spec —
  // `keeps the create control unavailable until the acknowledgement is given`
  // and `keeps the create control reachable by keyboard while it is
  // unavailable` — which is the rule already held, at the layer that owns it. A
  // third copy here would only add a place for it to drift.

  // A fresh screen and, because the service is component-provided, a fresh
  // flow: new account keys, new codes, new factor identifiers and a
  // `mayHaveCreatedAccount` back at false — nothing this browser did in an
  // earlier fixture is carried in. {@link waysOut} needs one per candidate
  // control.
  function render(): void {
    fixture?.destroy();
    fixture = TestBed.createComponent(RegisterComponent);
    host = fixture.nativeElement as HTMLElement;
    service = fixture.debugElement.injector.get(RegisterService);
    fixture.detectChanges();
  }

  // Presses a control by the name it is announced under, and refuses to be
  // helpful about a missing one: a `?.click()` on `null` is a test that passes
  // because nothing happened.
  function press(name: string): void {
    const control = controlNamed(host, name);

    if (control === null) {
      throw new Error(
        `The registration screen has no control named "${name}".`,
      );
    }

    control.click();
    fixture?.detectChanges();
  }

  function acknowledge(): void {
    const checkbox = host.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );

    if (checkbox === null) {
      throw new Error('The codes step renders no acknowledgement checkbox.');
    }

    checkbox.click();
    fixture?.detectChanges();
  }

  async function settle(): Promise<void> {
    await fixture?.whenStable();
    fixture?.detectChanges();
  }

  // Every live region on the screen, read as one string. One reading rather
  // than a search for a particular element, because what is being asserted is
  // that *something* is announced — the words, the element and the number of
  // regions are the author's.
  function liveRegionText(): string {
    return Array.from(
      host.querySelectorAll('[role="status"], [role="alert"], [aria-live]'),
    )
      .map((region) => collapse(region.textContent ?? ''))
      .join(' ')
      .trim();
  }

  // The names of every control on the screen, in document order. Both element
  // types a call to action is written as, because a lookup that knew only about
  // `<button>` would read "the screen offers nothing" for a screen offering an
  // `<a routerLink>`.
  function controlNames(): readonly string[] {
    return Array.from(host.querySelectorAll<HTMLElement>('button, a'))
      .map((control) => accessibleName(control))
      .filter((name) => name !== '');
  }

  // `Continue`, on the introduction — **the press the options request now
  // leaves on**. The step does not move until the answer arrives, because the
  // server refuses a subject that already holds an account above its own
  // challenge and that answer belongs on the screen showing the address.
  async function pressContinue(): Promise<void> {
    press(CONTINUE_BUTTON);

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);
    await settle();
  }

  // `Create a passkey`, with the challenge already in hand — **flushing
  // nothing**. The ceremony, the account keys, the card and the eleven wraps
  // all happen in here, on real WebCrypto.
  function runCeremony(): Promise<void> {
    press(CREATE_PASSKEY_BUTTON);

    return settleOnCodes();
  }

  // The same press with no challenge in hand, which is what a restart leaves
  // behind: the prefetched nonce was spent by the ceremony that ran before it.
  async function runCeremonyFetchingAChallenge(): Promise<void> {
    press(CREATE_PASSKEY_BUTTON);

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);
    await settleOnCodes();
  }

  async function settleOnCodes(): Promise<void> {
    await eventually(
      (): readonly RecoveryCode[] | null => service.codes(),
      'the minted recovery codes to be published',
    );
    await settle();
  }

  async function driveToCodes(): Promise<void> {
    await pressContinue();
    await runCeremony();
  }

  async function acknowledgeAndCreate(): Promise<TestRequest> {
    acknowledge();
    press(CREATE_ACCOUNT_BUTTON);

    return eventually(
      () => http.match(REGISTRATION_URL)[0] ?? null,
      'the registration request',
    );
  }

  async function driveToRegistration(): Promise<TestRequest> {
    await driveToCodes();

    return acknowledgeAndCreate();
  }

  // The plain 409: the first thing this browser posts meets an account that
  // already exists.
  async function driveToPlainConflict(): Promise<void> {
    const request = await driveToRegistration();
    request.flush(null, { status: 409, statusText: 'Conflict' });
    await settle();
  }

  // The 409 after a **lost answer**. The first POST committed its thirty rows
  // and lost the 201 coming back, the person was correctly told Budgetoid could
  // not tell and to keep their codes, they pressed `Start again`, and this 409
  // is that first account answering.
  async function driveToConflictAfterALostAnswer(): Promise<void> {
    const first = await driveToRegistration();
    first.error(new ProgressEvent('error'), {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();

    press(RESTART_BUTTON);
    await runCeremonyFetchingAChallenge();

    const second = await acknowledgeAndCreate();
    second.flush(null, { status: 409, statusText: 'Conflict' });
    await settle();
  }

  // The 409 after a **refusal**, which is the same three presses and the
  // opposite fact. A 400 is a judgement — the request was read and every 400,
  // 401 and 403 leaves the handler before a row is written — so the first
  // attempt created nothing, and this 409 is an account that predates the whole
  // visit.
  async function driveToConflictAfterARefusal(): Promise<void> {
    const first = await driveToRegistration();
    first.flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    press(RESTART_BUTTON);
    await runCeremonyFetchingAChallenge();

    const second = await acknowledgeAndCreate();
    second.flush(null, { status: 409, statusText: 'Conflict' });
    await settle();
  }

  // Which of a state's controls reach the sign-in screen, found by pressing
  // each of them on a screen of its own.
  //
  // One press per fixture, rather than a loop over the controls of one screen:
  // the first press may replace the DOM the rest were read from, and an element
  // pressed after its component was destroyed answers nothing at all — a test
  // that then reports "no way out" for a screen that offers one, depending on
  // the order the author happened to write the controls in.
  async function waysOut(drive: () => Promise<void>): Promise<WayOut> {
    await drive();

    const offered = controlNames();
    const reaching: string[] = [];

    for (const name of offered) {
      render();
      await drive();
      navigations.length = 0;
      press(name);
      await settle();

      if (navigations.includes(WELCOME_URL)) {
        reaching.push(name);
      }
    }

    return { offered, reaching };
  }
});

// The passkey factor's key-encryption key, imported rather than derived,
// because the derivation needs a PRF output and no authenticator exists here.
// `extractable: false` matches what `keyEncryptionKeyFromPasskey` produces.
function importKeyEncryptionKey(): Promise<CryptoKey> {
  const bytes = new Uint8Array(32);

  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = 0x20 + index;
  }

  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// The flow is driven by `void` methods over WebCrypto, so there is no promise
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

function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

function accessibleName(control: HTMLElement): string {
  return collapse(
    control.getAttribute('aria-label') ?? control.textContent ?? '',
  );
}

// Both element types a call to action is written as. A control found by name
// rather than by class or test id, because the name is what a person is offered
// and a control nobody can name is a control nobody can be told to press.
function controlNamed(host: HTMLElement, name: string): HTMLElement | null {
  const controls = Array.from(host.querySelectorAll<HTMLElement>('button, a'));

  return controls.find((control) => accessibleName(control) === name) ?? null;
}

// Unavailable to everyone, by either of the two shapes that say so. A truly
// disabled button and a `disabledInteractive` one are both refusals; they
// differ on whether the control keeps its place in the tab order, which is a
// separate question with its own test above.
function unavailable(control: HTMLElement): boolean {
  return (
    control.getAttribute('aria-disabled') === 'true' ||
    (control instanceof HTMLButtonElement && control.disabled)
  );
}

// The element whose own text *is* the sentence — the paragraph carrying it,
// rather than every ancestor that contains it.
function elementSaying(root: Element | null, sentence: string): Element | null {
  const elements = Array.from(root?.querySelectorAll('*') ?? []);

  return (
    elements.find(
      (element) => collapse(element.textContent ?? '') === sentence,
    ) ?? null
  );
}
