// The first of the three steps, and no longer the one that asks for nothing: it
// states which account is about to be created, asks the server whether it may
// be, and offers the way on.
//
// **The change this file grew for is one press moving one screen earlier.**
// `POST /api/registration/options` answers 409 when the provider identity
// already holds an account, above its own challenge, so that answer exists at
// the first press. Read at the second one it reached somebody who had been
// promised an account under an address, sent through a screen about
// authenticators, and refused there — a promise made in the product's own words
// and broken two screens later. So this step now has three states beside its
// resting one, and what a person reads in each of them is this file's subject.
//
// It is driven against a stubbed `RegisterService` rather than the real one,
// because nothing this step does reaches the network, the authenticator or the
// crypto — it renders one address, calls one method and reads two signals.
// `register.service.spec` owns the flow; `register.component.spec` owns the
// wiring between the steps; this file owns what a person reads here.
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from '@app-core/services/auth-service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RegisterService, type RegisterFailure } from '../register.service';
import { IntroStepComponent } from './intro-step.component';

// The copy is pinned as whole sentences and whole control names, for the reason
// `settings.component.spec.ts` gives: a fragment assertion survives a rewrite
// that changes what the sentence promises. On this screen it is more than a
// style rule — the two refusals differ by whether the promise above them is
// still true, and a `toContain('account')` is satisfied by every line here.
const OWNER_EMAIL = 'owner@budgetoid.test';
// The caption that tells a person how much of this is left. Three steps, and
// the number is the whole of the promise: somebody being asked to register a
// passkey and then transcribe ten codes needs to know before they start that
// there is an end to it.
const STEP_CAPTION = 'Step 1 of 3';

// **The promise, and the one thing on this screen that can stop being true.**
// It is what the whole change exists to protect: an account *will* be created
// under this address, unless the server has just said one already was.
const PROMISE = `Your account will be created under ${OWNER_EMAIL}.`;
// What replaces it once the server has answered. The address stays on the
// screen — the refusal beside it says *this Google address*, and a sentence
// pointing at nothing is worse than the promise was — but it is no longer a
// promise about anything.
const SIGNED_IN_AS = `This browser is signed in to Google as ${OWNER_EMAIL}.`;

// What the live region says while the request is out. This step's own sentence
// and deliberately not the passkey step's "Waiting for your device.": no
// authenticator has been asked for anything here, and what this press waits on
// is the server.
const CHECKING = 'Checking your account.';

// The two refusals this step can be showing, pinned whole.
const CONFLICT =
  'An account already exists for this Google address. Nothing has been created — sign in from the Budgetoid home page instead.';
const START_FAILED =
  'Budgetoid couldn’t reach the server. Nothing has been created.';
// The third, and the one that names a cause on purpose. `provider-token-refused`
// is the word for a 401 and says only that the bearer was refused; the sentence
// may go further, because a person cannot act on "the token was refused" and can
// act on "sign in with Google again". It ends where the `@else` arm's sentence
// ends, deliberately: the exchange comes back to this page, which is what makes
// leaving it safe to do.
const PROVIDER_TOKEN_REFUSED =
  'Your Google sign-in has expired. Nothing has been created — continue with Google and you’ll come straight back to this page.';

const CONTINUE_BUTTON = 'Continue';
// Another `Continue` under a name that admits to being one, offered by the
// refusal a second press could actually get past.
const RETRY_BUTTON = 'Try again';
// The way *out*, and it belongs to one refusal rather than to the step.
const SIGN_IN_BUTTON = 'Go to sign in';
// The same words the welcome screen uses for the same act, deliberately: a
// person who has been bounced here from there is meeting the control they
// already pressed once.
const PROVIDER_BUTTON = 'Continue with Google';

// Where the sign-in control has to land. `/welcome` is the one address in this
// application that runs a passkey assertion, which is exactly what the conflict
// sentence tells the reader to go and do.
const WELCOME_URL = '/welcome';

describe('IntroStepComponent', () => {
  let fixture: ComponentFixture<IntroStepComponent>;
  let host: HTMLElement;
  let email: ReturnType<typeof signal<string | null>>;
  // The two readings this step branches on, writable in the fixture and
  // read-only on the stub. Every state below is reached by moving one of them,
  // because the TestBed refuses a second `configureTestingModule` once the
  // module has been instantiated.
  let busy: ReturnType<typeof signal<boolean>>;
  let failure: ReturnType<typeof signal<RegisterFailure | null>>;
  let begin: Mock<RegisterService['begin']>;
  let signIn: Mock<AuthService['signIn']>;
  // Where the router was asked to go, in order, recorded off the **real**
  // router. A `routerLink` lands here as well as a programmatic call —
  // `RouterLink` calls `navigateByUrl` itself — so these tests say where the
  // screen goes and leave to whoever writes it whether the way out is a button
  // or an anchor.
  let navigations: string[];

  beforeEach(async () => {
    email = signal<string | null>(OWNER_EMAIL);
    busy = signal(false);
    failure = signal<RegisterFailure | null>(null);
    begin = vi.fn<RegisterService['begin']>();
    signIn = vi.fn<AuthService['signIn']>();
    navigations = [];

    const service: Pick<
      RegisterService,
      'email' | 'busy' | 'failure' | 'begin'
    > = {
      email: email.asReadonly(),
      busy: busy.asReadonly(),
      failure: failure.asReadonly(),
      begin,
    };
    const auth: Pick<AuthService, 'signIn'> = { signIn };

    await TestBed.configureTestingModule({
      imports: [IntroStepComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        // Provided rather than the real one for the reason the header states:
        // the real service reaches the identity provider, the authenticator and
        // WebCrypto, and this step touches none of the three.
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

    fixture = TestBed.createComponent(IntroStepComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  it('names the address the account will be created under', () => {
    // Act
    const shown = collapse(host.textContent ?? '');

    // Assert
    // A person with two Google accounts — a personal one and a work one — has
    // no other way to find out which of them the browser is still signed in to,
    // and the cost of guessing wrong is not a wasted click: the next step spends
    // a challenge and asks an authenticator to create a passkey, and the account
    // that results is bound to whichever address the provider asserted. The
    // address is read off the id token by `AuthService.providerEmail`, so it is
    // the provider's claim rather than anything typed here.
    expect(shown).toContain(OWNER_EMAIL);
    expect(shown).toContain(PROMISE);
  });

  it('offers the provider when no token is held', () => {
    // Arrange
    // The control, and the half that makes the assertions below mean anything:
    // with an address in hand the screen offers the way on, so a screen that
    // offered the provider unconditionally would not pass this line.
    expect(buttonNamed(host, CONTINUE_BUTTON)).not.toBeNull();

    // Act
    email.set(null);
    fixture.detectChanges();
    buttonNamed(host, PROVIDER_BUTTON)?.click();

    // Assert
    // No token means no `sub`, no asserted address and nothing for the two
    // registration legs to authenticate as — both are declared on the provider
    // scheme and nothing else, so `Continue` from here reaches a 401 and a
    // screen that can say nothing useful about why. A browser arrives in this
    // state routinely: a bookmarked `/register`, a reload an hour later, a
    // provider exchange that never completed.
    expect(buttonNamed(host, PROVIDER_BUTTON)).not.toBeNull();
    expect(buttonNamed(host, CONTINUE_BUTTON)).toBeNull();
    expect(signIn).toHaveBeenCalledOnce();
    // And it does not quietly start the flow anyway.
    expect(begin).not.toHaveBeenCalled();
  });

  it('names its position in the flow', () => {
    // Act
    const shown = collapse(host.textContent ?? '');

    // Assert
    // Three steps, said out loud on each of them. The flow asks for a passkey
    // and then for ten codes to be written down by hand, and a person deciding
    // whether they have time for that is entitled to know how much of it there
    // is before they start rather than after the authenticator has fired.
    expect(shown).toContain(STEP_CAPTION);
  });

  it('says nothing while nothing has happened', () => {
    // Act
    const regions = liveRegions(host);

    // Assert
    // Both halves are load-bearing, as on every region in this app. Presence
    // alone is satisfied by a region that always holds a line, and emptiness
    // alone by no region at all — which is a live region created at the moment
    // it gains content and therefore announced unreliably or not at all.
    expect(regions).toHaveLength(1);
    expect(collapse(regions[0]?.textContent ?? '')).toBe('');
    // `status`, never `alert`: nothing is typed on this screen, and every
    // sentence that lands here is the outcome of a press the person made.
    // Assertive is reserved for a failure to save something they wrote.
    expect(host.querySelectorAll('[role="alert"]')).toHaveLength(0);
  });

  it('says the account is being checked while the request is out', () => {
    // Arrange
    // The control for both assertions below: at rest the region is empty and
    // the control is pressable, so neither of them is a statement about a
    // screen that always looked this way.
    expect(liveRegionText(host)).toBe('');
    expect(
      buttonNamed(host, CONTINUE_BUTTON)?.getAttribute('aria-disabled'),
    ).not.toBe('true');

    // Act
    busy.set(true);
    fixture.detectChanges();

    const control = buttonNamed(host, CONTINUE_BUTTON);

    control?.click();
    fixture.detectChanges();

    // Assert
    // Said where it is heard. This press waits on a network round trip and the
    // introduction had nothing to wait on before it; a screen that renders
    // unchanged reads as a control that did nothing, and a person who cannot
    // see the screen is told by nothing else at all.
    expect(liveRegionText(host)).toBe(CHECKING);
    // **The control keeps its place**, which is the busy case in the buttons
    // chapter rather than the acknowledgement case: a control that goes truly
    // `disabled` under the finger drops focus to `<body>`, and the region above
    // is what says why it cannot be pressed.
    expect(
      control,
      'the busy state removes the control from the screen.',
    ).not.toBeNull();
    expect(control?.getAttribute('aria-disabled')).toBe('true');
    expect(control?.disabled).toBe(false);
    expect(control?.getAttribute('tabindex')).not.toBe('-1');
    // And `disabledInteractive` is presentation, so the click arrives —
    // Material's own click-halt is installed on anchors only. Nothing may be
    // behind it: a second press while the first request is out asks the server
    // for a second challenge, and a challenge is a nonce it persisted.
    expect(begin).not.toHaveBeenCalled();
  });

  it('drops the promise when the account already exists', () => {
    // Arrange
    // **The control without which this test passes against a template that
    // never made the promise at all.** A refusal that removes a sentence can
    // only be checked against a screen that was saying it a moment ago.
    expect(collapse(host.textContent ?? '')).toContain(PROMISE);

    // Act
    failure.set('conflict');
    fixture.detectChanges();

    const shown = collapse(host.textContent ?? '');

    // Assert
    expect(elementSaying(host, CONFLICT)).not.toBeNull();
    // A promise standing beside its own refusal is the defect this whole change
    // exists to remove — one screen earlier than it used to be read.
    expect(shown).not.toContain(PROMISE);
    // And the address stays, under a lead that claims nothing. The sentence
    // above says *this Google address*, so taking it off the screen leaves a
    // refusal pointing at nothing — which is worse than the promise was.
    expect(shown).toContain(SIGNED_IN_AS);
    expect(shown).toContain(OWNER_EMAIL);
  });

  it('reaches the sign-in screen when the account already exists', () => {
    // Arrange
    // Two controls, both about this screen at rest. Without them the assertions
    // below pass on a step carrying a permanent link to `/welcome` in its
    // furniture, where the way out belongs to nobody in particular.
    expect(buttonNamed(host, SIGN_IN_BUTTON)).toBeNull();
    expect(navigations).toEqual([]);

    // Act
    failure.set('conflict');
    fixture.detectChanges();

    const control = buttonNamed(host, SIGN_IN_BUTTON);

    control?.click();

    // Assert
    // `/register` has no navigation of its own — the app shell is a bare
    // `<router-outlet />` — so a screen without this control is a dead end
    // whatever the copy says, and copy naming a door is the worse kind of dead
    // end: the person goes looking for something that is not there.
    expect(
      control,
      'the conflict state renders no way to sign in.',
    ).not.toBeNull();
    // Exactly where, and nowhere else. `/welcome` is the one address in this
    // application that runs an assertion.
    expect(navigations).toEqual([WELCOME_URL]);
    // And nothing to press through. The account exists, so a second `Continue`
    // spends another request to be told the same thing — which is why the
    // conflict replaces the control rather than sitting beside it.
    expect(buttonNamed(host, CONTINUE_BUTTON)).toBeNull();
    expect(buttonNamed(host, RETRY_BUTTON)).toBeNull();
    expect(begin).not.toHaveBeenCalled();
  });

  // **The measured defect, and it is two defects.** `/register` loaded with an
  // id token 71 minutes past its expiry showed the promise and a `Continue`;
  // pressing it sent the stale bearer and the API answered
  // `401 invalid_token`. The screen then said Budgetoid could not reach the
  // server — it had been reached and had answered — beside a `Try again` that
  // could only re-send the same dead token. The one control that fixes it,
  // `Continue with Google`, rendered on the arm reached when the browser holds
  // no token at all, so the person was stuck on a screen offering the two things
  // that cannot work and not the one that can.
  it('offers the provider again when the Google sign-in has expired', () => {
    // Arrange
    // The control, and the half that makes the assertions below mean anything: a
    // step that offered the provider unconditionally would not pass this line.
    expect(buttonNamed(host, PROVIDER_BUTTON)).toBeNull();
    expect(buttonNamed(host, CONTINUE_BUTTON)).not.toBeNull();

    // Act
    failure.set('provider-token-refused');
    fixture.detectChanges();

    const control = buttonNamed(host, PROVIDER_BUTTON);

    control?.click();

    // Assert
    expect(elementSaying(host, PROVIDER_TOKEN_REFUSED)).not.toBeNull();
    // The provider, and it is the only act that can clear this. A refused token
    // is refused for every later press alike.
    expect(
      control,
      'the refused token offers no way to sign in with the provider again.',
    ).not.toBeNull();
    expect(signIn).toHaveBeenCalledOnce();
    // And neither of the two controls that cannot work. `Continue` and `Try
    // again` are the same press, and that press attaches the same dead token; a
    // `Go to sign in` would send somebody with no account to a screen that can
    // only refuse them with a byte-identical 401.
    expect(buttonNamed(host, CONTINUE_BUTTON)).toBeNull();
    expect(buttonNamed(host, RETRY_BUTTON)).toBeNull();
    expect(buttonNamed(host, SIGN_IN_BUTTON)).toBeNull();
    expect(begin).not.toHaveBeenCalled();
    // Offered, not taken: pressing the provider control leaves this page for
    // Google, and nothing may move the browser before the person asks.
    expect(navigations).toEqual([]);
  });

  // **The promise survives this one, and the conflict's shape would be wrong
  // here for a reason that is about the other sentence rather than about this
  // one.** The server said nothing about the address — it refused a credential —
  // and the account still will be created under it once the exchange has been
  // made again. Dropping the promise renders the lead that replaces it, "this
  // browser is signed in to Google as <address>", which is the one statement on
  // the screen that a refused token makes false.
  it('keeps the promise when the Google sign-in has expired', () => {
    // Arrange
    expect(collapse(host.textContent ?? '')).toContain(PROMISE);

    // Act
    failure.set('provider-token-refused');
    fixture.detectChanges();

    const shown = collapse(host.textContent ?? '');

    // Assert
    expect(shown).toContain(PROMISE);
    expect(shown).not.toContain(SIGNED_IN_AS);
    // The address is on the screen either way, and it is what the person is
    // being asked to sign in as again.
    expect(shown).toContain(OWNER_EMAIL);
  });

  it('keeps the promise and offers another press when the start fails', () => {
    // Act
    failure.set('start-failed');
    fixture.detectChanges();

    const control = buttonNamed(host, RETRY_BUTTON);

    control?.click();

    // Assert
    expect(elementSaying(host, START_FAILED)).not.toBeNull();
    // **Not the conflict's shape, and the difference is the point.** The server
    // said nothing about the address — a status 0, a timeout, a 5xx — so the
    // promise above still holds and the next press is a real way forward.
    // Dropping it here would tell somebody whose network blinked that their
    // account is not going to be created under the address they are reading.
    expect(collapse(host.textContent ?? '')).toContain(PROMISE);
    // And no way out is offered, because there is nothing to go out to: this
    // person has no account, so `/welcome` would refuse the assertion it runs
    // there with a byte-identical 401 naming no cause.
    expect(buttonNamed(host, SIGN_IN_BUTTON)).toBeNull();
    expect(navigations).toEqual([]);
    // The retry is another `Continue` under a name that admits to being one, so
    // it asks the same question again rather than moving anybody on.
    expect(control, 'the failed start offers nothing to press.').not.toBeNull();
    expect(begin).toHaveBeenCalledOnce();
  });
});

// Collapses the whitespace an HTML template introduces. Without it every
// assertion above is hostage to where Prettier wrapped the line.
function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

// Every shape that makes a node a live region, not only the one this screen
// uses. The rule is "the wait and the refusal are announced", and a sentence
// moved into a `role="log"` or an `aria-live` div satisfies it as well as the
// `role="status"` does; a sentence moved out of all of them satisfies none.
const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

function liveRegions(host: HTMLElement): readonly Element[] {
  return Array.from(host.querySelectorAll(LIVE_REGION_SELECTOR));
}

// Every live region read as one string, so an assertion about what is announced
// does not have to know which of them holds it. One region is the shape today
// and the count is pinned by its own test above.
function liveRegionText(host: HTMLElement): string {
  return collapse(
    liveRegions(host)
      .map((region) => region.textContent ?? '')
      .join(' '),
  );
}

// Finds a button the way a screen reader announces it, so a control renamed in
// the DOM but not in the copy stops being found. `aria-label` wins over the text
// node, matching how the accessible name is computed for the shapes this screen
// uses. Restated here rather than shared with `codes-step.component.spec.ts`:
// the step specs are read one at a time and a helper module between them would
// buy four lines at the cost of every reader having to open a second file.
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
