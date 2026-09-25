import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { BrandLockupComponent } from '@app-shared/components/brand-lockup/brand-lockup.component';
import { KineticSentenceComponent } from '@app-shared/components/kinetic-sentence/kinetic-sentence.component';
import { SignInService, type SignInFailure } from './sign-in.service';

// Apostrophes are typographic (’) on purpose.
const KINETIC_LINES: readonly (readonly [fear: string, verdict: string])[] = [
  ['December 1st. Insurance due.', 'It’s ready.'],
  ['This August. Two weeks away.', 'It’s paid.'],
  ['At the till. That jacket.', 'Go ahead.'],
  ['Out of nowhere. A car repair.', 'Expected.'],
  ['Friday night. Pizza with everyone.', 'Covered.'],
  ['Payday morning. Salary lands.', 'All of it gets a job.'],
];

// The front door, and from this commit it is not one door.
//
// Until now the screen offered a single call to action and it left the site: the
// only way into a Budgetoid account was through Google, which makes the account
// something a third party can close. It now offers two — creating one, which
// goes to the registration flow, and signing in, which runs the assertion
// ceremony against this product's own API and asks the provider nothing at all.
//
// The provider button is gone from here, and there is no shared component left
// behind it: the button, the facade it provided and the store chain it
// dispatched into went with it. The registration flow's introduction step draws
// its own control, and that step is the one place the provider is ever
// contacted — once, while an account is being created.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLockupComponent, KineticSentenceComponent, MatButtonModule],
  // **The custody decision, not a lifetime preference**, and the same one
  // `register.component.ts` makes. The flow dies with the screen, so a sign-in
  // somebody walked away from leaves nothing behind in the injector, and no
  // other screen shares this one's failure state. `sign-in.service.ts` argues
  // the same rule from its own end and neither half works alone.
  providers: [SignInService],
  styleUrls: ['./welcome.component.scss'],
  templateUrl: './welcome.component.html',
})
export class WelcomeComponent {
  private readonly router = inject(Router);

  protected readonly flow = inject(SignInService);
  protected readonly kineticLines = KINETIC_LINES;

  // What the screen says when a sign-in ended badly, and `null` when nothing
  // has. Computed off the flow's word rather than stored, so there is one
  // statement of the failure and the screen cannot go on showing an older one.
  protected readonly refusal = computed<string | null>(() => {
    const failure = this.flow.failure();

    return failure === null ? null : sentenceOf(failure);
  });

  // Navigated rather than written as a `routerLink`, so both calls to action on
  // this screen are the same kind of element and the buttons chapter's "one
  // primary per view" is a comparison between like things.
  protected createAccount(): void {
    void this.router.navigateByUrl('/register');
  }
}

// The flow's seven words, mapped to what this screen says about each. A `switch`
// over the closed union rather than a lookup object, so an eighth word added to
// `SignInFailure` fails to compile here instead of arriving as `undefined` on a
// screen that then announces an empty region.
//
// **One sentence for `refused`, and it names no cause.**
// `PasskeyVerificationExceptionHandler.cs` makes every refusal on that route
// byte-identical on purpose: a caller able to tell "no such credential" from
// "wrong signature" can discover which user handles are registered, one guess at
// a time, without ever holding a credential. A screen that rendered a cause it
// was handed would put that oracle back in front of the person.
function sentenceOf(failure: SignInFailure): string {
  switch (failure) {
    // The browser cannot run the ceremony at all — no WebAuthn, or the page is
    // not in a secure context. Nothing was attempted, so the sentence names
    // another browser rather than another press.
    case 'unsupported':
      return 'This browser can’t sign you in with a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can.';
    // The system sheet was closed, or it timed out. Nothing is wrong and nothing
    // needs reporting, and the sentence says so rather than treating an ordinary
    // act as an error.
    case 'cancelled':
      return 'Signing in didn’t finish. Nothing has changed — try again whenever you’re ready.';
    // The one refusal that is about the authenticator rather than the person or
    // the network: the ceremony *succeeded* and the device cannot derive the
    // value the account's keys are wrapped under. Another device is the way
    // through, and it is the device the account was created on.
    case 'no-prf':
      return 'This device can’t unlock your account’s keys. Try the phone, laptop or security key you created your account on.';
    case 'ceremony-failed':
      return 'Your device didn’t finish signing you in. Nothing has changed.';
    // About the server rather than the device, and the sentence has to say so: a
    // person told their device failed will go and buy a security key for a
    // problem a reload would have fixed.
    case 'start-failed':
      return 'Budgetoid couldn’t reach the server to start. Try again in a moment.';
    // The server read the assertion and said no, and that is the whole of what
    // it said. The way forward is another way in — not another press, which can
    // only ever be refused the same way.
    case 'refused':
      return 'Budgetoid couldn’t sign you in with that passkey. Try the device you created your account on, or another one you’ve signed in with.';
    // No answer, or an answer that says nothing. Deliberately not the sentence
    // above: this one is about a server that could not be reached, and the way
    // forward is the same press a minute later.
    case 'unknown':
      return 'Budgetoid didn’t hear back. Try again in a minute.';
  }
}
