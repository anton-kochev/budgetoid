// The step that spends the challenge, and the only screen in the flow that has
// seven different ways to end badly.
//
// Every one of those seven is `RegisterFailure`'s word for it, and the whole of
// this file is the claim that the seven are not interchangeable. Five of them
// come from the ceremony one for one — `webauthn-ceremony.service.ts` argues why
// none is a synonym of another — `start-failed` is the flow's own word for a
// challenge that was never issued, and `unknown` is what the flow's outermost
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
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RegisterService, type RegisterFailure } from '../register.service';
import { PasskeyStepComponent } from './passkey-step.component';

const STEP_CAPTION = 'Step 2 of 3';
// At rest, and after a refusal that is worth another press. The same control
// under two names rather than two controls, so a screen showing a refusal offers
// exactly one way forward and a census can count it.
const CREATE_BUTTON = 'Create a passkey';
const RETRY_BUTTON = 'Try again';

// One refusal: the word the service publishes, the sentence this screen says,
// and whether pressing again could possibly help.
//
// The sentences are pinned whole. On this screen that is not a style rule — the
// difference between two of them *is* the requirement, and a fragment assertion
// (`toContain('passkey')`) is satisfied by four of the seven.
interface Refusal {
  readonly failure: RegisterFailure;
  readonly sentence: string;
  readonly offersRetry: boolean;
}

// The browser cannot run the ceremony at all — no WebAuthn, or the page is not
// in a secure context. Nothing was attempted and nothing will be, so pressing
// again is an invitation to be told the same thing on the same device.
const UNSUPPORTED: Refusal = {
  failure: 'unsupported',
  sentence:
    'This browser can’t create a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can.',
  offersRetry: false,
};

// The person closed the system sheet, or it timed out. Nothing is wrong, nothing
// needs reporting, and the sentence says so rather than treating an ordinary act
// as an error.
const CANCELLED: Refusal = {
  failure: 'cancelled',
  sentence:
    'The passkey wasn’t created. Nothing has been saved, and nothing was sent — try again whenever you’re ready.',
  offersRetry: true,
};

// The authenticator declined because it already holds a credential named in the
// exclusion list. Another device is the way through, so the retry is real.
const DUPLICATE: Refusal = {
  failure: 'duplicate',
  sentence:
    'This device already holds a passkey Budgetoid can’t reuse. Try again with a different device or security key.',
  offersRetry: true,
};

// Anything else the ceremony ended in, including one that resolved nothing.
const CEREMONY_FAILED: Refusal = {
  failure: 'ceremony-failed',
  sentence:
    'Your device didn’t finish creating the passkey. Nothing has been saved.',
  offersRetry: true,
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
};

const REFUSALS: readonly Refusal[] = [
  UNSUPPORTED,
  CANCELLED,
  DUPLICATE,
  CEREMONY_FAILED,
  START_FAILED,
  UNKNOWN,
  NO_PRF,
];

describe('PasskeyStepComponent', () => {
  let fixture: ComponentFixture<PasskeyStepComponent>;
  let host: HTMLElement;
  let failure: ReturnType<typeof signal<RegisterFailure | null>>;
  let busy: ReturnType<typeof signal<boolean>>;
  let createPasskey: Mock<RegisterService['createPasskey']>;

  beforeEach(async () => {
    // Writable in the fixture and read-only on the stub. Every test here moves
    // the one signal this screen branches on, and the TestBed refuses a second
    // `configureTestingModule` once the module has been instantiated.
    failure = signal<RegisterFailure | null>(null);
    busy = signal(false);
    createPasskey = vi.fn<RegisterService['createPasskey']>();

    const service: Pick<RegisterService, 'busy' | 'failure' | 'createPasskey'> =
      {
        busy: busy.asReadonly(),
        failure: failure.asReadonly(),
        createPasskey,
      };

    await TestBed.configureTestingModule({
      imports: [PasskeyStepComponent],
      providers: [
        provideNoopAnimations(),
        { provide: RegisterService, useValue: service },
      ],
    }).compileComponents();

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
    // of the seven where that is the subject. The ceremony worked: the person
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
  });

  it.each(REFUSALS.filter((refusal) => refusal !== NO_PRF))(
    'says its own sentence and no other when the flow ends in $failure',
    ({ failure: word, sentence, offersRetry }: Refusal) => {
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

      // And none of the other six. Without this half every one of these tests
      // passes on a screen that renders all seven sentences at once, or on one
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
      // default. Five of the seven are worth another press — a closed sheet, a
      // device already holding a credential, a ceremony that did not finish, a
      // server that did not answer, and a rejection nobody predicted. The two
      // that are not come through here with `offersRetry` false — and `no-prf`
      // through a test of its own besides — because offering a retry on either
      // is offering somebody a button that cannot ever do anything but repeat
      // itself.
      expect(controls).toHaveLength(offersRetry ? 1 : 0);
      expect(createPasskey).toHaveBeenCalledTimes(offersRetry ? 1 : 0);
    },
  );

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
function ceremonyControls(host: HTMLElement): readonly HTMLButtonElement[] {
  return [CREATE_BUTTON, RETRY_BUTTON].flatMap((name) => {
    const button = buttonNamed(host, name);

    return button === null ? [] : [button];
  });
}

// Finds a button the way a screen reader announces it, so a control renamed in
// the DOM but not in the copy stops being found.
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
