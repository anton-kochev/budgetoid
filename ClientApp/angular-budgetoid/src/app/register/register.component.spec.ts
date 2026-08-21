// The screen the whole flow is, driven end to end through its own DOM.
//
// `register.service.spec.ts` owns what crosses the wire and what never may;
// each step's spec owns what a person reads on it. This file owns the only
// thing neither of them can see: the wiring. It presses the controls a person
// presses, in the order a person presses them, and it is the only place where
// "the acknowledgement gate refuses the press" is a statement about the button
// that is actually on the screen rather than about a method on a component.
//
// The real `RegisterService` runs, over real WebCrypto, with two seams stubbed
// and nothing else — the authenticator, which this runner does not implement,
// and the router, which has no `/app` route to accept a navigation. Stubbing the
// service would stub away every behaviour below.
//
// The fixture is deliberately a **copy** of the one in `register.service.spec.ts`
// rather than an import from it. Lifting the shared parts would mean editing a
// shipped spec to make room for this one, and the duplication is the DAMP trade
// the house makes in tests: a reader opening this file sees the whole ceremony
// answer without opening another.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
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
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RegisterComponent } from './register.component';
import { RegisterService } from './register.service';

const API_BASE_URL = 'https://api.test';
const OPTIONS_URL = `${API_BASE_URL}/api/registration/options`;
const REGISTRATION_URL = `${API_BASE_URL}/api/registration`;
const OWNER_EMAIL = 'owner@budgetoid.test';

// The controls, by the names a screen reader announces them under. Each is
// pinned in its own step's spec as well; restated here because this file is the
// one that presses them, and a control renamed on one side of that pair has to
// redden something.
const CONTINUE_BUTTON = 'Continue';
const CREATE_PASSKEY_BUTTON = 'Create a passkey';
const CREATE_ACCOUNT_BUTTON = 'Create account';
const RESTART_BUTTON = 'Start again';

// **The two sentences this screen exists to keep apart**, and the reason
// `refused` and `unknown` are separate words in `RegisterFailure`.
//
// A 400, a 401, a 403 and a 409 are judgements: the request was read, every one
// of them leaves the handler before a row is written, so nothing was created and
// the ten codes on screen open nothing. Saying so is a kindness — it tells the
// person to throw away a piece of paper that is worthless.
//
// A status 0, a timeout and a 5xx are not. The request may have arrived,
// committed all thirty rows and had its 201 lost coming back. Telling *that*
// person their codes are dead is telling them to discard the only way into an
// account that exists, and this client has no caller for
// `POST /api/me/recovery-codes`, so there is no second chance behind the
// sentence.
const REFUSED =
  'Your account wasn’t created and nothing was saved. The ten codes you were just shown open nothing — start again to get a new set.';
const UNKNOWN =
  'Budgetoid didn’t get an answer, so we can’t tell you whether your account was created. Keep the ten codes you saved: if it was, they’re part of the only way back into it.';

// **And the third pair, which is one 409 read two ways.**
//
// The server sends four distinct sentences under an identical title in
// `ProblemDetails.Detail`, with no machine-readable code, so the client cannot
// tell them apart and must not try to match on the text.
// `mayHaveCreatedAccount()` is the only discriminant it has, and it is enough,
// because the two readings that matter differ by exactly what an earlier POST
// ended as. Not by whether `Start again` was pressed: that control is offered
// from two failure states, and forking on the press told somebody whose first
// attempt was *refused* that it had created their account.
//
// While no request from this browser has ended without an answer, a 409 means
// what it says: somebody who already has an account walked back into the front
// door. Nothing was created here and the ten codes on screen open nothing.
//
// After a POST that ended `unknown` it means something else entirely, and it is
// the worst case in the flow. The person's *first* POST committed all thirty
// rows and lost its 201 coming back; they were correctly told Budgetoid could
// not tell and to keep their codes; they pressed `Start again`; and the 409 is
// that first account answering. So the account exists, the passkey that opens
// it is the one made on the first attempt, and the live codes are the first
// attempt's ten — not the ten that were on this screen a moment ago. Rendering
// {@link CONFLICT} here tells that person the opposite of every one of those
// facts, and somebody who then throws the first card away holds a passkey, no
// codes, and no way to make more: `POST /api/me/recovery-codes` has no caller
// in this client.
const CONFLICT =
  'An account already exists for this Google address. Nothing was created here, and the ten codes you were just shown open nothing — sign in from the Budgetoid home page instead.';
// New copy, because the state it belongs to renders nothing today. Written to
// `docs/design/voice.md`: the subject of the sentence is the system, the fact
// comes before the instruction, and the loss is stated plainly with no label
// telling the reader to brace.
const CONFLICT_AFTER_RESTART =
  'Your first attempt did create your account — its answer just didn’t reach this browser. Sign in with the passkey you made on that attempt. The ten codes you were shown a moment ago open nothing; the ten from the first attempt are the ones that work.';

// Characters per printed group, restated rather than imported for the reason
// `register.service.spec.ts` gives: the grouping is module-private to the codes
// step, and an *independent* rendering is what lets an assertion about what is
// on the screen mean something.
const GROUP_SIZE = 4;

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

// One navigation, and the session's reading at the instant it was asked for.
// The reading is captured *here* rather than after the fact because the order is
// the requirement: published second, `authGuard` judges `/app` against a stale
// `anonymous` and bounces the person out of the account they have just created.
interface Navigation {
  readonly url: string;
  readonly status: SessionStatus;
}

describe('RegisterComponent', () => {
  let fixture: ComponentFixture<RegisterComponent>;
  let host: HTMLElement;
  let http: HttpTestingController;
  let session: SessionService;
  let service: RegisterService;
  let navigations: Navigation[];
  let forgetProviderToken: Mock<AuthService['forgetProviderToken']>;
  let keyEncryptionKey: CryptoKey;

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    navigations = [];
    forgetProviderToken = vi.fn<AuthService['forgetProviderToken']>();

    // The one seam that has to exist: `available()` and `createPasskey()` both
    // touch `navigator.credentials`, which this runner does not implement. The
    // willing answer, because every test in this file is about what happens
    // after the device has agreed — the refusals are the passkey step's spec.
    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'createPasskey'
    > = {
      available: () => true,
      createPasskey: (): Promise<
        PasskeyCeremonyResult<PasskeyRegistrationCeremony>
      > =>
        Promise.resolve({
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
        // Overriding the router the line above provides rather than declaring
        // routes: the flow navigates to `/app` on the 201, and a real router
        // with no matching route rejects that navigation into a promise nothing
        // awaits. Recording the address and the session's reading at that
        // instant also gives the tests below a way to say that nothing
        // navigated.
        {
          provide: Router,
          useValue: {
            navigateByUrl: (url: string): Promise<boolean> => {
              navigations.push({ url, status: session.status() });

              return Promise.resolve(true);
            },
          },
        },
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => true,
            forgetProviderToken,
            // The address the introduction shows back and the reason the
            // `Continue` control is on the screen at all — with `null` here the
            // first step offers provider sign-in instead, which is its own
            // spec's subject.
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
    session = TestBed.inject(SessionService);
    fixture = TestBed.createComponent(RegisterComponent);
    host = fixture.nativeElement as HTMLElement;
    service = fixture.debugElement.injector.get(RegisterService);
    fixture.detectChanges();
  });

  // No `http.verify()` teardown, for the reason `register.service.spec.ts`
  // gives: several tests below end with the registration request outstanding on
  // purpose, because the request *is* the subject.

  it('creates nothing before the acknowledgement', async () => {
    // Arrange
    await driveToCodes();

    // Act
    press(CREATE_ACCOUNT_BUTTON);

    // Assert
    // The single highest-stakes assertion on this screen. The control renders
    // with `disabledInteractive`, which sets `aria-disabled` and the unavailable
    // appearance while leaving the DOM `disabled` property `false` — that is
    // what keeps it in the tab order so a keyboard user can reach it and
    // discover what it is waiting on. The cost is that the browser delivers the
    // click exactly as if nothing were disabled: Material's own click-halt is
    // installed on anchors only. So the attribute is presentation and the guard
    // in the handler is the rule, and this is the only test in the system that
    // says so **about the request** rather than about an emitted event.
    //
    // What a failure here costs is not a stray request. It is an account created
    // for somebody who never said they had kept the ten codes, on a flow whose
    // entire premise is that they had.
    expect(http.match(REGISTRATION_URL)).toHaveLength(0);
  });

  it('creates the account once the person acknowledges', async () => {
    // Arrange
    await driveToCodes();

    // Act
    acknowledge();
    press(CREATE_ACCOUNT_BUTTON);

    // Assert
    // The positive half, and each of this pair is the other's control: without
    // it, a screen whose create control is wired to nothing at all passes the
    // test above perfectly.
    expect(http.match(REGISTRATION_URL)).toHaveLength(1);
  });

  it('says nothing was created when the request is refused', async () => {
    // Arrange
    const request = await driveToRegistration();
    const codes = mintedCodes();

    // Act
    request.flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    // Assert
    expect(elementSaying(host, REFUSED)).not.toBeNull();
    // And the codes are off the screen, because they are dead: nothing was
    // written, so the eleven envelopes they were minted with belong to no
    // account and never will. Leaving them up is leaving ten worthless secrets
    // in front of somebody who is about to be handed ten real ones.
    expect(codes).toHaveLength(10);
    expectCodesAbsent(codes);
  });

  it('says it cannot tell when the server never answered', async () => {
    // Arrange
    const request = await driveToRegistration();

    // Act
    request.error(new ProgressEvent('error'), {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();

    // Assert
    // The pair this and the test above make is the whole reason `refused` and
    // `unknown` are separate words. A request that got no answer says nothing
    // about whether thirty rows were committed, and the sentence must not
    // pretend otherwise — a person told their codes are worthless will throw
    // away the only key to an account they cannot make more codes for.
    expect(elementSaying(host, UNKNOWN)).not.toBeNull();
    expect(elementSaying(host, REFUSED)).toBeNull();
    // Stated as an assertion rather than left to the reader, because the two
    // constants above are the requirement and a copy edit that collapsed them
    // into one sentence would leave every other line in both tests green.
    expect(UNKNOWN).not.toBe(REFUSED);
  });

  it('shows a different set of codes after a restart', async () => {
    // Arrange
    const request = await driveToRegistration();
    const abandoned = mintedCodes();
    request.flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    // Act
    press(RESTART_BUTTON);
    await runCeremony();

    // Assert
    // Everything is re-drawn: a new challenge, a new passkey, new account keys,
    // ten new codes and eleven new factor identifiers. Nothing from the
    // abandoned attempt is reused and nothing could be — the challenge is spent
    // and the keys were wiped. Asserted here, at the screen, and not only at the
    // service, because the failure this catches is a rendering one: a step that
    // held the first set in a local, or a list that re-rendered from a stale
    // input, shows a person ten codes that unlock nothing while the account
    // being created is locked with ten others.
    expect(abandoned).toHaveLength(10);
    expect(mintedCodes()).toHaveLength(10);
    expectCodesAbsent(abandoned);
    // The control: the *new* set really is on the screen, so the absences above
    // are a different set rather than an empty one.
    expectCodesPresent(mintedCodes());
  });

  it('says an account already exists when the first attempt was refused', async () => {
    // Arrange
    const request = await driveToRegistration();

    // Act
    request.flush(null, { status: 409, statusText: 'Conflict' });
    await settle();

    // Assert
    // One POST, and it was judged, so this 409 is the plain one: somebody who
    // already has an account walked back into the front door. Nothing was
    // created here, the ten codes open nothing, and the way forward is to go and
    // sign in. Asserted with the signal the sentence forks on, because this test
    // is the control for the one below — without it a screen that renders the
    // *first attempt worked* sentence for every 409 alike is green on the pair.
    expect(service.mayHaveCreatedAccount()).toBe(false);
    expect(elementSaying(host, CONFLICT)).not.toBeNull();
    expect(elementSaying(host, CONFLICT_AFTER_RESTART)).toBeNull();
  });

  it('says the earlier attempt worked when a restart is refused', async () => {
    // Arrange
    // The first POST gets no answer at all, so the person is correctly told
    // Budgetoid cannot tell and to keep their ten codes. Asserted rather than
    // assumed: everything below is only true of somebody standing in *that*
    // state, and a flow that had already said "nothing was created" would make
    // the rest of this test a statement about a different screen.
    const first = await driveToRegistration();
    const firstAttemptCodes = mintedCodes();
    first.error(new ProgressEvent('error'), {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();
    expect(elementSaying(host, UNKNOWN)).not.toBeNull();

    // Act
    // They press the one control on offer, run a whole second ceremony, and post
    // again — and the 409 is the account their *first* attempt created, which
    // committed all thirty rows and lost its 201 coming back.
    press(RESTART_BUTTON);
    await runCeremony();
    const second = await acknowledgeAndCreate();
    second.flush(null, { status: 409, statusText: 'Conflict' });
    await settle();

    // Assert
    // `mayHaveCreatedAccount` is the only discriminant available — the server
    // sends four distinct 409 sentences under one title with no machine-readable
    // code, so the client cannot tell them apart and must not read the text.
    // What opened the question is the *first* POST getting no answer, not the
    // press that followed it, and it is one-directional: this second POST was
    // judged, and a judgement cannot close a question an unanswered request
    // opened, because the account it may have created does not stop existing.
    expect(service.mayHaveCreatedAccount()).toBe(true);
    expect(elementSaying(host, CONFLICT_AFTER_RESTART)).not.toBeNull();
    // **The pair is the test.** A screen that renders one sentence for both
    // readings of a 409 passes any assertion that only looks for a phrase both
    // would contain, so the plain sentence has to be *absent* here and the two
    // constants have to be different strings — stated outright, because a copy
    // edit that collapsed them into one would otherwise leave every other line
    // in both tests green.
    expect(elementSaying(host, CONFLICT)).toBeNull();
    expect(CONFLICT_AFTER_RESTART).not.toBe(CONFLICT);
    // And the second attempt's ten are off the screen, because they are the ones
    // that open nothing. The account exists; it is locked with the first card.
    expect(firstAttemptCodes).toHaveLength(10);
    expectCodesAbsent(mintedCodes());
  });

  it('signs the person into the app', async () => {
    // Arrange
    const request = await driveToRegistration();

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });
    await settle();

    // Assert
    // One navigation, to `/app`, and the session already published when it was
    // asked for. The order is the requirement and the reading captured at the
    // instant of the call is the only way to see it: publish the session second
    // and `authGuard` judges `/app` against a stale `anonymous`, bouncing the
    // person straight back out of the account they have just created — a bug
    // that reproduces every time and looks like a routing problem.
    expect(navigations).toEqual([{ url: '/app', status: 'authenticated' }]);
    expect(session.status()).toBe('authenticated');
  });

  it('forgets the provider token when the flow ends', async () => {
    // Arrange
    const request = await driveToRegistration();

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });
    await settle();

    // Assert
    // The id token has done the one job it was obtained for. From here the
    // `__Host-budgetoid-session` cookie authenticates every request, and a
    // bearer this client keeps carrying is a second credential it has no use for
    // and every reason to stop holding. Discarded **after** the session is
    // published and **before** the screen goes away, which is why it is asserted
    // on the same 201 as the navigation rather than on a separate act.
    expect(forgetProviderToken).toHaveBeenCalledOnce();
  });

  it('keeps one top-level heading', async () => {
    // Act & Assert
    // `accessibility.md` asks for one `h1` per screen, and a flow that renders
    // three steps behind one address is three chances to get it wrong in two
    // directions: a heading on the host *and* on each step gives every step two,
    // and a heading on the host only leaves each step titled by the one before
    // it. The post-request states are counted too — they replace the codes step
    // rather than sitting under it, so a document with none of them titled is a
    // document with no `h1` at all.
    expectOneHeading('the introduction');

    press(CONTINUE_BUTTON);
    expectOneHeading('the passkey step');

    await runCeremony();
    expectOneHeading('the codes step');

    const request = await acknowledgeAndCreate();
    request.flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();
    expectOneHeading('the refusal');
  });

  // Presses a control by the name it is announced under, and refuses to be
  // helpful about a missing one: a `?.click()` on `null` is a test that passes
  // because nothing happened, which is exactly the failure every assertion in
  // this file is looking for.
  function press(name: string): void {
    const button = buttonNamed(host, name);

    if (button === null) {
      throw new Error(
        `The registration screen has no control named "${name}".`,
      );
    }

    button.click();
    fixture.detectChanges();
  }

  function acknowledge(): void {
    const checkbox = host.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );

    if (checkbox === null) {
      throw new Error('The codes step renders no acknowledgement checkbox.');
    }

    checkbox.click();
    fixture.detectChanges();
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  // From the passkey step to ten codes on the screen. The ceremony, the account
  // keys, the card and the eleven wraps all happen in here, on real WebCrypto.
  async function runCeremony(): Promise<void> {
    press(CREATE_PASSKEY_BUTTON);

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);

    await eventually(
      (): readonly RecoveryCode[] | null => service.codes(),
      'the minted recovery codes to be published',
    );
    await settle();
  }

  async function driveToCodes(): Promise<void> {
    press(CONTINUE_BUTTON);
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

  // The whole flow, with the request left outstanding: answering it is what the
  // tests that use this differ on.
  async function driveToRegistration(): Promise<TestRequest> {
    await driveToCodes();

    return acknowledgeAndCreate();
  }

  // The set the flow has minted, read off the one public signal that carries it.
  // The screen's own rendering is grouped, so every assertion about what is on
  // the display goes through {@link grouped} rather than through these strings.
  function mintedCodes(): readonly RecoveryCode[] {
    const codes = service.codes();

    if (codes === null) {
      throw new Error('The flow published no recovery codes.');
    }

    return codes;
  }

  function expectCodesPresent(codes: readonly RecoveryCode[]): void {
    const text = collapse(host.textContent ?? '');

    for (const code of codes) {
      expect(
        text.includes(grouped(code)),
        `${code} is not on the screen.`,
      ).toBe(true);
    }
  }

  // Both spellings, because a screen that stopped grouping would still be
  // showing the code.
  function expectCodesAbsent(codes: readonly RecoveryCode[]): void {
    const text = collapse(host.textContent ?? '');

    for (const code of codes) {
      expect(text.includes(code), `${code} is still on the screen.`).toBe(
        false,
      );
      expect(
        text.includes(grouped(code)),
        `${code} is still on the screen, grouped.`,
      ).toBe(false);
    }
  }

  function expectOneHeading(where: string): void {
    expect(
      host.querySelectorAll('h1').length,
      `${where} does not carry exactly one top-level heading.`,
    ).toBe(1);
  }
});

// The passkey factor's key-encryption key, imported rather than derived, because
// the derivation needs a PRF output and no authenticator exists here.
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

// One code as the codes step prints it: groups of four joined by hyphens, with
// the two-character tail 26 characters leave over.
function grouped(code: string): string {
  const groups: string[] = [];

  for (let start = 0; start < code.length; start += GROUP_SIZE) {
    groups.push(code.slice(start, start + GROUP_SIZE));
  }

  return groups.join('-');
}

// The flow is driven by `void` methods over WebCrypto, so there is no promise to
// await from outside — the observable effects arrive some number of microtasks
// later. Polling a public reading is the honest way to wait for them: it makes
// no claim about how many awaits the implementation happens to contain today,
// and a flow that never gets there fails with a sentence naming what never
// arrived rather than with a null dereference.
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

function buttonNamed(
  host: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  const buttons = Array.from(
    host.querySelectorAll<HTMLButtonElement>('button'),
  );

  return (
    buttons.find(
      (button) =>
        collapse(
          button.getAttribute('aria-label') ?? button.textContent ?? '',
        ) === name,
    ) ?? null
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
