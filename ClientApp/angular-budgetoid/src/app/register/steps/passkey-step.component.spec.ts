// The step that spends the challenge, and the only screen in the flow that has
// eight different ways to end badly.
//
// Every one of those eight is `RegisterFailure`'s word for it, and the whole of
// this file is the claim that the eight are not interchangeable. Five of them
// come from the ceremony one for one — `webauthn-ceremony.service.ts` argues why
// none is a synonym of another — `start-failed` and `conflict` are the two
// answers the options leg has, and `unknown` is what the flow's outermost
// catch publishes for anything nobody predicted. A screen that folds them into
// one sentence tells somebody whose browser cannot run WebAuthn at all to try
// again, and tells somebody who simply closed the sheet that their device is
// unsupported.
//
// Driven against a stubbed `RegisterService`, because this step reads two
// signals and calls one method. The service's own spec owns what those words
// mean; this file owns what a person reads when one of them is published.
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from '@app-core/services/auth-service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RegisterService, type RegisterFailure } from '../register.service';
import { PasskeyStepComponent } from './passkey-step.component';

const STEP_CAPTION = 'Step 2 of 3';
// At rest, and after a refusal that is worth another press. The same control
// under two names rather than two controls, so a screen showing a refusal offers
// exactly one way forward and a census can count it.
const CREATE_BUTTON = 'Create a passkey';
const RETRY_BUTTON = 'Try again';
// The way out, and it belongs to one refusal rather than to the step. It is not
// a third name for the control above: pressing it runs no ceremony and spends no
// challenge, it leaves the flow for the one screen that can sign a returning
// person in.
const SIGN_IN_BUTTON = 'Go to sign in';
// The other way out, and it is not the one above under a different name: it
// leaves for the identity provider rather than for this application's own
// sign-in screen, and it comes back to `/register` with a token the two
// registration legs will accept. The same words the introduction's own
// token-less arm uses for the same act.
const PROVIDER_BUTTON = 'Continue with Google';

// Where that control has to land. `/welcome` is the one address in this
// application that runs a passkey assertion, which is what the sentence beside
// the control tells the reader to go and do.
const WELCOME_URL = '/welcome';

// One refusal: the word the service publishes, the sentence this screen says,
// whether pressing again could possibly help, and whether the person is being
// sent anywhere.
//
// The sentences are pinned whole. On this screen that is not a style rule — the
// difference between two of them *is* the requirement, and a fragment assertion
// (`toContain('passkey')`) is satisfied by four of the eight.
interface Refusal {
  readonly failure: RegisterFailure;
  readonly sentence: string;
  readonly offersRetry: boolean;
  // **Not the negation of `offersRetry`, and this is the flag the census exists
  // to keep honest.** Three of the eight offer no retry and only one of those
  // has anywhere to go: `unsupported` and `no-prf` are stuck on this device with
  // no account at the far end of a sign-in, so a control derived from "no retry"
  // would promise a way forward to two people who have none. Every constant
  // below answers it, and exactly one answers `true`.
  readonly offersSignIn: boolean;
  // The second way out, counted on every row for the reason the first one is.
  // It is a different door rather than a second name for the same one: this
  // control leaves for Google and comes back here, where the one above leaves
  // for this application's own sign-in screen and does not. Exactly one word
  // answers `true`, and no word answers `true` to both — a refusal offering two
  // ways out is a screen asking the reader to choose between doors it has not
  // explained.
  readonly offersProvider: boolean;
}

// The browser cannot run the ceremony at all — no WebAuthn, or the page is not
// in a secure context. Nothing was attempted and nothing will be, so pressing
// again is an invitation to be told the same thing on the same device.
const UNSUPPORTED: Refusal = {
  failure: 'unsupported',
  sentence:
    'This browser can’t create a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can.',
  offersRetry: false,
  // No retry and no way out either: a browser that cannot run the ceremony
  // cannot run an assertion on `/welcome` either, so a sign-in control here
  // would be the same dead end one screen further on.
  offersSignIn: false,
  offersProvider: false,
};

// The person closed the system sheet, or it timed out. Nothing is wrong, nothing
// needs reporting, and the sentence says so rather than treating an ordinary act
// as an error.
const CANCELLED: Refusal = {
  failure: 'cancelled',
  sentence:
    'The passkey wasn’t created. Nothing has been saved, and nothing was sent — try again whenever you’re ready.',
  offersRetry: true,
  offersSignIn: false,
  offersProvider: false,
};

// The authenticator declined because it already holds a credential named in the
// exclusion list. Another device is the way through, so the retry is real.
const DUPLICATE: Refusal = {
  failure: 'duplicate',
  sentence:
    'This device already holds a passkey Budgetoid can’t reuse. Try again with a different device or security key.',
  offersRetry: true,
  offersSignIn: false,
  offersProvider: false,
};

// Anything else the ceremony ended in, including one that resolved nothing.
const CEREMONY_FAILED: Refusal = {
  failure: 'ceremony-failed',
  sentence:
    'Your device didn’t finish creating the passkey. Nothing has been saved.',
  offersRetry: true,
  offersSignIn: false,
  offersProvider: false,
};

// The options leg never answered, so no challenge exists and nothing was minted.
// It is the one refusal on this screen that is about the server rather than the
// device, and the sentence has to say so — a person told their device failed
// will go and buy a security key for a problem a reload would have fixed.
const START_FAILED: Refusal = {
  failure: 'start-failed',
  sentence:
    'Budgetoid couldn’t reach the server to start. Nothing has been saved.',
  offersRetry: true,
  offersSignIn: false,
  offersProvider: false,
};

// Nothing that has a word of its own. `RegisterService.mintUnder` carries a
// catch at its outermost level and this is what that arm publishes: the ceremony
// answers with a result rather than throwing, and the crypto below it is the
// platform's, so a rejection reaching there is genuinely unforeseen. Swallowing
// it would leave this screen on a spinner for ever, which is why the arm exists
// and why the sentence has to read for somebody it can name no cause to.
//
// It is the one word this screen shares with the shell, and the two say
// different things on purpose: nothing is posted from this step, so here the
// sentence can say plainly that nothing was created.
const UNKNOWN: Refusal = {
  failure: 'unknown',
  sentence: 'Budgetoid didn’t finish, and nothing has been saved. Try again.',
  offersRetry: true,
  offersSignIn: false,
  offersProvider: false,
};

// The one refusal that is about the authenticator rather than about the person
// or the network: the ceremony *succeeded* and the device cannot derive the
// key-encryption key the account's keys are wrapped under. Pressing again on the
// same device produces the same success and the same missing output, so no retry
// is offered and the sentence names another device instead.
const NO_PRF: Refusal = {
  failure: 'no-prf',
  sentence:
    'This device can’t hold your account’s keys, and Budgetoid won’t create an account it can’t lock. Try a different phone, laptop or security key.',
  offersRetry: false,
  // The second word without a retry, and the second with nowhere to go. It
  // carries the same `false` as `unsupported` for a different reason: this
  // person has no account to sign in to, and the device that could not derive
  // the key would be asked to derive it again.
  offersSignIn: false,
  offersProvider: false,
};

// The other answer the options leg has, and the one refusal on this screen that
// a second press cannot change by definition: the provider identity already has
// an account, and the server says so above its own challenge — so nothing was
// minted, no ceremony ran and no passkey was made. It is the one word this
// screen shares with the shell's conflict block, and the two say different
// things because the shell's are about ten codes on screen and there are none
// here.
const CONFLICT: Refusal = {
  failure: 'conflict',
  sentence:
    'An account already exists for this Google address. Nothing was created and no passkey was made — sign in from the Budgetoid home page instead.',
  offersRetry: false,
  // The only `true` in the file. The sentence sends the reader to the home page
  // and `/register` has no navigation of its own — the app shell is a bare
  // `<router-outlet />` — so without a control here the copy names a door that
  // is not on the screen. That is the dead end the shell's own conflict block
  // was fixed for, one step over.
  offersSignIn: true,
  // The account exists and this browser's Google token is fine, so the provider
  // has nothing to offer: an exchange would come back with the same identity and
  // meet the same 409. The way in is a passkey assertion, one screen over.
  offersProvider: false,
};

// The third answer the options leg has, and the second refusal on this screen
// that is about the server rather than the device. A 401 there is the API
// refusing the bearer this browser attached — both registration routes are
// declared on the provider scheme and nothing else — and an id token lives an
// hour while this step can be sat on for longer: a restart, a closed system
// sheet, a device that did not finish.
//
// It is not `start-failed`. That sentence says the server could not be reached,
// and the server answered; its `Try again` re-runs a ceremony whose refetch
// attaches the same dead token. Nothing minted on either path that reaches this
// word, so the sentence can say plainly that nothing has been saved and the
// person can leave for Google without losing anything.
const PROVIDER_TOKEN_REFUSED: Refusal = {
  failure: 'provider-token-refused',
  sentence:
    'Your Google sign-in has expired. Nothing has been saved — continue with Google and you’ll come back to the first step.',
  // A retry here is a retry of the ceremony, and the ceremony is not what was
  // refused. It would spend a system sheet to reach the same refetch and the
  // same 401.
  offersRetry: false,
  // Not this door: this person has no account, so `/welcome` can only refuse the
  // assertion it runs with a byte-identical 401 naming no cause.
  offersSignIn: false,
  // The only `true` in the file, and it is the one act that changes the answer.
  // `/register` has no navigation of its own — the app shell is a bare
  // `<router-outlet />` — so without this control the sentence names a door that
  // is not on the screen, which is the dead end this whole change exists to
  // remove.
  offersProvider: true,
};

const REFUSALS: readonly Refusal[] = [
  UNSUPPORTED,
  CANCELLED,
  DUPLICATE,
  CEREMONY_FAILED,
  START_FAILED,
  UNKNOWN,
  NO_PRF,
  CONFLICT,
  PROVIDER_TOKEN_REFUSED,
];

describe('PasskeyStepComponent', () => {
  let fixture: ComponentFixture<PasskeyStepComponent>;
  let host: HTMLElement;
  let failure: ReturnType<typeof signal<RegisterFailure | null>>;
  let busy: ReturnType<typeof signal<boolean>>;
  let createPasskey: Mock<RegisterService['createPasskey']>;
  // The provider exchange, recorded rather than run: `signIn()` calls
  // `initLoginFlow()`, which sets `location.href` and takes the runner with it.
  let signIn: Mock<AuthService['signIn']>;
  // Where the router was asked to go, in order, recorded off the **real**
  // router. A `routerLink` lands here as well as a programmatic call —
  // `RouterLink` calls `navigateByUrl` itself — so these tests say where the
  // screen goes and leave to whoever writes it whether the way out is a button
  // or an anchor. A stub object in the router's place would pin the shape
  // instead of the destination, and would answer nothing at all for the anchor.
  let navigations: string[];

  beforeEach(async () => {
    navigations = [];

    // Writable in the fixture and read-only on the stub. Every test here moves
    // the one signal this screen branches on, and the TestBed refuses a second
    // `configureTestingModule` once the module has been instantiated.
    failure = signal<RegisterFailure | null>(null);
    busy = signal(false);
    createPasskey = vi.fn<RegisterService['createPasskey']>();
    signIn = vi.fn<AuthService['signIn']>();

    const service: Pick<RegisterService, 'busy' | 'failure' | 'createPasskey'> =
      {
        busy: busy.asReadonly(),
        failure: failure.asReadonly(),
        createPasskey,
      };
    // Stubbed rather than left to the root injector: the real one reaches
    // `OAuthService`, which nothing provides here, and constructing it would
    // make this file red for a reason that has nothing to do with the screen.
    const auth: Pick<AuthService, 'signIn'> = { signIn };

    await TestBed.configureTestingModule({
      imports: [PasskeyStepComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: RegisterService, useValue: service },
        { provide: AuthService, useValue: auth },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);

    // Recorded rather than run: this router declares no routes, so a real
    // navigation to `/welcome` rejects into a promise nothing awaits and the
    // rejection surfaces as an unrelated failure some tests later.
    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        navigations.push(
          typeof url === 'string' ? url : router.serializeUrl(url),
        );

        return Promise.resolve(true);
      },
    );

    fixture = TestBed.createComponent(PasskeyStepComponent);
    host = fixture.nativeElement as HTMLElement;
    // Exactly one, so every test below starts from a bare render and a published
    // refusal is genuinely the first thing that has happened.
    fixture.detectChanges();
  });

  it('refuses an authenticator that cannot hold the account keys and says so', () => {
    // Arrange
    // The control for the census below: at rest this screen offers exactly one
    // way to run the ceremony, so "no way to run it again" is a refusal rather
    // than a count of a screen that never had a control on it.
    expect(ceremonyControls(host)).toHaveLength(1);

    // Act
    failure.set('no-prf');
    fixture.detectChanges();

    // Assert
    // The sentence names what the *device* cannot do, and this is the only one
    // of the eight where that is the subject. The ceremony worked: the person
    // touched the sensor, the authenticator agreed, a credential exists — and it
    // cannot produce the value the account's content and index keys are wrapped
    // under, so an account created against it would be an account whose own
    // owner could never open it. A sentence blaming the browser, the network or
    // the person sends them to retry on the one device that is certain to fail.
    expect(elementSaying(host, NO_PRF.sentence)).not.toBeNull();
    // And nothing to press. Both names, because the offer is the same control
    // under either of them: leaving `Create a passkey` on the screen is a retry
    // that does not admit to being one.
    expect(ceremonyControls(host)).toHaveLength(0);
    // And no way out either, which is the half this test has to carry itself
    // because it is excluded from the census below. `no-prf` and `conflict` are
    // two of the three words that leave this screen with nothing to press, and
    // only one of them has an account at the far end of a sign-in: a control
    // offered here sends somebody who has no account to a screen that can only
    // refuse them, on the one device that is certain to fail the assertion.
    expect(signInControls(host)).toHaveLength(0);
    // Nor the other door. The provider token is not what refused anything here:
    // the ceremony succeeded and the device cannot derive the value the
    // account's keys are wrapped under, which no exchange changes.
    expect(providerControls(host)).toHaveLength(0);
  });

  it.each(REFUSALS.filter((refusal) => refusal !== NO_PRF))(
    'says its own sentence and no other when the flow ends in $failure',
    ({
      failure: word,
      sentence,
      offersRetry,
      offersSignIn,
      offersProvider,
    }: Refusal) => {
      // Act
      failure.set(word);
      fixture.detectChanges();
      const controls = ceremonyControls(host);
      controls[0]?.click();

      // Assert
      // Its own sentence, as one element's own text: prose broken across the
      // screen says the same words and is not the same statement.
      const said = elementSaying(host, sentence);
      expect(said).not.toBeNull();
      // In the region, because that is the whole reason the region is in the DOM
      // from first paint — a refusal that renders as ordinary content is one a
      // person using a screen reader has to go looking for.
      expect(said?.closest(LIVE_REGION_SELECTOR)).not.toBeNull();

      // And none of the other seven. Without this half every one of these tests
      // passes on a screen that renders all eight sentences at once, or on one
      // that renders a single sentence containing all of the words.
      const text = collapse(host.textContent ?? '');

      for (const other of REFUSALS) {
        if (other.sentence !== sentence) {
          expect(
            text.includes(other.sentence),
            `${word} also says the sentence for ${other.failure}.`,
          ).toBe(false);
        }
      }

      // Whether pressing again could help is a property of the word, not a
      // default. Five of the eight are worth another press — a closed sheet, a
      // device already holding a credential, a ceremony that did not finish, a
      // server that did not answer, and a rejection nobody predicted. The three
      // that are not come through here with `offersRetry` false — and `no-prf`
      // through a test of its own besides — because offering a retry on any of
      // them is offering somebody a button that cannot ever do anything but
      // repeat itself. `conflict` is the sharpest of the three: the account
      // exists, so another press spends another request to be told so.
      expect(controls).toHaveLength(offersRetry ? 1 : 0);
      expect(createPasskey).toHaveBeenCalledTimes(offersRetry ? 1 : 0);

      // And whether the person is sent anywhere is a second property of the
      // word, read from the table rather than from the line above it. **The
      // whole point of counting it on every row is that it is not `!offersRetry`
      // — it is `true` on `conflict` alone.** `unsupported` and `no-prf` carry
      // the same `false` for two further reasons of their own: neither has an
      // account at the far end of a sign-in, and neither device can complete the
      // assertion that would be asked for. A control attached to every word
      // without a retry passes every other assertion in this file.
      expect(
        signInControls(host),
        `${word} offers ${offersSignIn ? 'no' : 'a'} way to sign in.`,
      ).toHaveLength(offersSignIn ? 1 : 0);
      // And the third property, counted on every row for the same reason. It is
      // `true` on `provider-token-refused` alone — the one refusal that is about
      // the credential the request carried rather than about the device, the
      // network or the account. Offered anywhere else it sends somebody through
      // an exchange that changes nothing and returns them to the refusal they
      // started from; missing where it belongs, the screen names a door that is
      // not on it.
      expect(
        providerControls(host),
        `${word} offers ${offersProvider ? 'no' : 'a'} way to continue with ` +
          'the provider.',
      ).toHaveLength(offersProvider ? 1 : 0);
      // Offered, not taken: nothing pressed above is navigation, and a refusal
      // that moved the browser by itself would take the person off a screen
      // still holding the sentence explaining what happened. The provider
      // control is held to the same rule by its own test below — it leaves this
      // application entirely, which is the one departure no `navigations` list
      // can record.
      expect(navigations).toEqual([]);
      expect(signIn).not.toHaveBeenCalled();
    },
  );

  it('reaches the sign-in screen when an account already exists', () => {
    // Arrange
    // Two controls for one assertion, and both are about *this* screen at rest.
    // Without them the test below passes on a step that carries a permanent
    // link to `/welcome` in its furniture — which is a different screen, one
    // where the way out belongs to nobody in particular and the `conflict`
    // sentence is the only thing that makes it read as an answer.
    expect(signInControls(host)).toHaveLength(0);
    expect(navigations).toEqual([]);

    // Act
    failure.set('conflict');
    fixture.detectChanges();

    const [control] = signInControls(host);

    control?.click();

    // Assert
    // The sentence in this branch ends "sign in from the Budgetoid home page
    // instead", and `/register` has no navigation of its own — the app shell is
    // a bare `<router-outlet />`. So a screen without this control is a dead end
    // whatever the copy says, and the copy naming a door is what makes it the
    // worse kind: the person goes looking for something that is not there.
    expect(
      control,
      'the conflict state renders no way to sign in.',
    ).toBeDefined();
    // Exactly where, and nowhere else. `/welcome` is the one address in this
    // application that runs an assertion, so a control landing anywhere else
    // ends the same journey one screen further along.
    expect(navigations).toEqual([WELCOME_URL]);
    // And nothing to press beside it. `conflict` is the one refusal on this
    // screen that a second press cannot change by definition — the account
    // exists — so a retry offered here spends another challenge and another
    // system sheet to be told the same thing.
    expect(ceremonyControls(host)).toHaveLength(0);
    expect(createPasskey).not.toHaveBeenCalled();
  });

  // **The refetch's own 401, and the reason this step needs the control at
  // all.** The challenge is fetched again from here after a restart and after a
  // ceremony that failed, and a provider id token lives an hour — long enough to
  // lapse between the introduction's press and this one. What arrived before was
  // `start-failed`: a sentence saying the server could not be reached, beside a
  // `Try again` whose refetch attaches the same dead token, on a step with no
  // provider control anywhere.
  it('offers the provider again when the Google sign-in has expired', () => {
    // Arrange
    // Both about this screen at rest, and without them the assertions below pass
    // on a step carrying a permanent provider control in its furniture — where
    // the way out belongs to nobody in particular and the sentence is the only
    // thing making it read as an answer.
    expect(providerControls(host)).toHaveLength(0);
    expect(signIn).not.toHaveBeenCalled();

    // Act
    failure.set('provider-token-refused');
    fixture.detectChanges();

    const [control] = providerControls(host);

    control?.click();

    // Assert
    expect(
      control,
      'the refused token renders no way to continue with the provider.',
    ).toBeDefined();
    // Once, and only because it was pressed. The exchange leaves this
    // application for Google, which is a departure no router recording can see,
    // so a refusal that started one by itself would take somebody off the screen
    // still explaining what happened to them.
    expect(signIn).toHaveBeenCalledOnce();
    expect(navigations).toEqual([]);
    // And nothing else to press. The ceremony is not what was refused — a retry
    // spends a system sheet to reach the same refetch and the same 401 — and
    // `/welcome` runs an assertion this person has no account for.
    expect(ceremonyControls(host)).toHaveLength(0);
    expect(signInControls(host)).toHaveLength(0);
    expect(createPasskey).not.toHaveBeenCalled();
  });

  it('says nothing while nothing has happened', () => {
    // Act
    const region = liveRegion(host);

    // Assert
    // Both halves are load-bearing, as on every region in this app. Presence
    // alone is satisfied by a region that always holds a line — a screen telling
    // somebody who has pressed nothing that their device refused — and emptiness
    // alone by no region at all, which is a live region created at the moment it
    // gains content and therefore announced unreliably or not at all.
    expect(region).not.toBeNull();
    expect(collapse(region?.textContent ?? '')).toBe('');
    // `status`, never `alert`: every sentence that lands here is the outcome of
    // an act the person asked for, and assertive is reserved for a failure to
    // save something they typed.
    expect(region?.getAttribute('role')).toBe('status');
  });

  it('names its position in the flow', () => {
    // Act
    const shown = collapse(host.textContent ?? '');

    // Assert
    // The middle of three, and the step where the flow first asks the device for
    // something. A person who has just been shown a system sheet is entitled to
    // know that it is not the last thing being asked of them.
    expect(shown).toContain(STEP_CAPTION);
  });
});

// Every shape that makes a node a live region, not only the one this screen
// uses. The rule is "a refusal is announced", and a sentence moved into a
// `role="log"` or an `aria-live` div satisfies it as well as the `role="status"`
// does; a sentence moved out of all of them satisfies none.
const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

function liveRegion(host: HTMLElement): Element | null {
  return host.querySelector(LIVE_REGION_SELECTOR);
}

// Every control on the screen that would run the ceremony, under either of the
// names it goes by. A census rather than a lookup of one name, because "offers
// no retry" is a statement about the screen and not about a label: a screen that
// renamed the button, or that left the original beside a new one, would pass a
// single-name check while handing the person exactly what the rule refuses.
function ceremonyControls(host: HTMLElement): readonly HTMLElement[] {
  return controlsNamed(host, CREATE_BUTTON, RETRY_BUTTON);
}

// Every control on the screen that offers to leave for the screen that can sign
// somebody in. One name today and still a census, for the reason above and for
// one of its own: the count is asserted on all eight words, so "no way out" has
// to mean the screen offers none rather than that this one label is absent.
function signInControls(host: HTMLElement): readonly HTMLElement[] {
  return controlsNamed(host, SIGN_IN_BUTTON);
}

// Every control on the screen that offers to leave for the identity provider. A
// separate census from the one above rather than a widening of it: the two doors
// lead to different places and exactly one word opens each, so a helper that
// counted both together would let a screen offering the wrong one pass.
function providerControls(host: HTMLElement): readonly HTMLElement[] {
  return controlsNamed(host, PROVIDER_BUTTON);
}

function controlsNamed(
  host: HTMLElement,
  ...names: readonly string[]
): readonly HTMLElement[] {
  return names.flatMap((name) => {
    const control = controlNamed(host, name);

    return control === null ? [] : [control];
  });
}

// Finds a control the way a screen reader announces it, so one renamed in the
// DOM but not in the copy stops being found.
//
// Buttons **and** anchors, because what these tests say is what the screen
// offers and where pressing it lands — not which element was reached for. A
// lookup over `button` alone would report "no way out" for a way out written as
// an `<a routerLink>`, which navigates perfectly well and lands in the same
// recording.
function controlNamed(host: HTMLElement, name: string): HTMLElement | null {
  const controls = Array.from(host.querySelectorAll<HTMLElement>('button, a'));

  return (
    controls.find(
      (control) =>
        collapse(
          control.getAttribute('aria-label') ?? control.textContent ?? '',
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
