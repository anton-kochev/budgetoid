import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RegisterService, type RegisterFailure } from '../register.service';

// The step that spends the challenge, and the only screen in the flow with
// seven different ways to end badly.
//
// It reads two signals and calls one method; it holds no state of its own. The
// seven sentences below are the whole of its design, and none of them is a
// synonym of another — a screen that folded them into one would tell somebody
// whose browser cannot run WebAuthn at all to try again, and tell somebody who
// simply closed the system sheet that their device is unsupported.

// One refusal as this screen renders it: what it says, and whether pressing
// again could possibly help.
//
// `offersRetry` is a property of the word rather than a default, and `false` is
// not a smaller version of `true`. Two of the seven have no next step on this
// device — the browser cannot run the ceremony, or the authenticator cannot
// derive the value the account's keys are wrapped under — and a control that
// can only repeat itself is worse than no control: it reads as a way forward,
// costs another system sheet to disprove, and ends in the same sentence.
interface PasskeyRefusal {
  readonly sentence: string;
  readonly offersRetry: boolean;
}

// What the ceremony control is at this instant, and `none` is a state rather
// than an absence of one: it is the answer for the two refusals above.
type CeremonyControl = 'create' | 'busy' | 'retry' | 'none';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  selector: 'app-passkey-step',
  styleUrls: ['./passkey-step.component.scss'],
  templateUrl: './passkey-step.component.html',
})
export class PasskeyStepComponent {
  protected readonly register = inject(RegisterService);

  protected readonly refusal = computed<PasskeyRefusal | null>(() => {
    const failure = this.register.failure();

    return failure === null ? null : refusalOf(failure);
  });

  // One reading for the whole control, so the template branches once. Busy
  // wins over a refusal because the flow clears the failure when an act starts:
  // the two cannot both be true, and asking the template to know that is asking
  // it to restate a rule the service already keeps.
  protected readonly control = computed<CeremonyControl>(() => {
    if (this.register.busy()) {
      return 'busy';
    }

    const refusal = this.refusal();

    if (refusal === null) {
      return 'create';
    }

    return refusal.offersRetry ? 'retry' : 'none';
  });
}

// The flow's nine words, mapped to what this screen says about each. A `switch`
// over the closed union rather than a lookup object, so a tenth word added to
// `RegisterFailure` fails to compile here instead of arriving as `undefined` on
// a screen that then renders an empty region.
function refusalOf(failure: RegisterFailure): PasskeyRefusal | null {
  switch (failure) {
    // The browser cannot run the ceremony at all — no WebAuthn, or the page is
    // not in a secure context. Nothing was attempted and nothing will be, so
    // the sentence names another browser rather than another press.
    case 'unsupported':
      return {
        sentence:
          'This browser can’t create a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can.',
        offersRetry: false,
      };
    // The system sheet was closed, or it timed out. Nothing is wrong and
    // nothing needs reporting, and the sentence says so rather than treating an
    // ordinary act as an error.
    case 'cancelled':
      return {
        sentence:
          'The passkey wasn’t created. Nothing has been saved, and nothing was sent — try again whenever you’re ready.',
        offersRetry: true,
      };
    // The authenticator declined because it already holds a credential named in
    // the exclusion list. Another device is the way through, so the retry is
    // real and the sentence says which kind of press to make.
    case 'duplicate':
      return {
        sentence:
          'This device already holds a passkey Budgetoid can’t reuse. Try again with a different device or security key.',
        offersRetry: true,
      };
    // The one refusal about the authenticator rather than about the person or
    // the network: the ceremony *succeeded* and the device cannot derive the
    // key-encryption key the account's keys are wrapped under. Pressing again on
    // the same device produces the same success and the same missing output.
    case 'no-prf':
      return {
        sentence:
          'This device can’t hold your account’s keys, and Budgetoid won’t create an account it can’t lock. Try a different phone, laptop or security key.',
        offersRetry: false,
      };
    case 'ceremony-failed':
      return {
        sentence:
          'Your device didn’t finish creating the passkey. Nothing has been saved.',
        offersRetry: true,
      };
    // The one refusal on this screen that is about the server rather than the
    // device, and the sentence has to say so: a person told their device failed
    // will go and buy a security key for a problem a reload would have fixed.
    case 'start-failed':
      return {
        sentence:
          'Budgetoid couldn’t reach the server to start. Nothing has been saved.',
        offersRetry: true,
      };
    // Reachable here, and only from the flow's own catch: something nobody
    // predicted rejected between the challenge arriving and the codes being
    // published. Nothing was posted on this step, so the sentence can say
    // plainly that nothing was created — which is what makes it a different
    // sentence from the shell's `unknown`, where a request really did leave.
    case 'unknown':
      return {
        sentence:
          'Budgetoid didn’t finish, and nothing has been saved. Try again.',
        offersRetry: true,
      };
    // Two of the three words the registration request answers with, and the
    // shell renders both in place of the codes step. They cannot be published
    // while this step is showing — the POST is only made from the codes step —
    // so this screen says nothing about them rather than inventing an eighth
    // sentence for a state it cannot be in. `unknown` is the third of the
    // three and is the one this screen shares, because the flow's own catch
    // publishes it here too; it has its own sentence above, and the two say
    // different things on purpose.
    case 'refused':
    case 'conflict':
      return null;
  }
}
