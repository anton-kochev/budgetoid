// The first of the three steps, and the only one that asks for nothing: it
// states which account is about to be created and offers the way on.
//
// It is driven against a stubbed `RegisterService` rather than the real one,
// because nothing this step does reaches the network, the authenticator or the
// crypto — it renders one address and calls one method. `register.service.spec`
// owns the flow; `register.component.spec` owns the wiring between the steps;
// this file owns what a person reads here.
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { AuthService } from '@app-core/services/auth-service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RegisterService } from '../register.service';
import { IntroStepComponent } from './intro-step.component';

// The copy is pinned as whole sentences and whole control names, for the reason
// `settings.component.spec.ts` gives: a fragment assertion survives a rewrite
// that changes what the sentence promises.
const OWNER_EMAIL = 'owner@budgetoid.test';
// The caption that tells a person how much of this is left. Three steps, and
// the number is the whole of the promise: somebody being asked to register a
// passkey and then transcribe ten codes needs to know before they start that
// there is an end to it.
const STEP_CAPTION = 'Step 1 of 3';
const CONTINUE_BUTTON = 'Continue';
// The same words the welcome screen uses for the same act, deliberately: a
// person who has been bounced here from there is meeting the control they
// already pressed once.
const PROVIDER_BUTTON = 'Continue with Google';

describe('IntroStepComponent', () => {
  let fixture: ComponentFixture<IntroStepComponent>;
  let host: HTMLElement;
  let email: ReturnType<typeof signal<string | null>>;
  let begin: Mock<RegisterService['begin']>;
  let signIn: Mock<AuthService['signIn']>;

  beforeEach(async () => {
    // Writable in the fixture and read-only on the stub, so a test can move the
    // one value this screen branches on without a second `configureTestingModule`
    // — which the TestBed refuses once the module has been instantiated.
    email = signal<string | null>(OWNER_EMAIL);
    begin = vi.fn<RegisterService['begin']>();
    signIn = vi.fn<AuthService['signIn']>();

    const service: Pick<RegisterService, 'email' | 'begin'> = {
      email: email.asReadonly(),
      begin,
    };
    const auth: Pick<AuthService, 'signIn'> = { signIn };

    await TestBed.configureTestingModule({
      imports: [IntroStepComponent],
      providers: [
        provideNoopAnimations(),
        // Provided rather than the real one for the reason the header states:
        // the real service reaches the identity provider, the authenticator and
        // WebCrypto, and this step touches none of the three.
        { provide: RegisterService, useValue: service },
        { provide: AuthService, useValue: auth },
      ],
    }).compileComponents();

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
});

// Collapses the whitespace an HTML template introduces. Without it every
// assertion above is hostage to where Prettier wrapped the line.
function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
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
