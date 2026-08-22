import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { RegisterService, type RegisterFailure } from '../register.service';

// The step that spends the challenge, and the only screen in the flow with
// eight different ways to end badly.
//
// It reads two signals and calls one method; it holds no state of its own. The
// eight sentences below are the whole of its design, and none of them is a
// synonym of another — a screen that folded them into one would tell somebody
// whose browser cannot run WebAuthn at all to try again, and tell somebody who
// simply closed the system sheet that their device is unsupported.

// One refusal as this screen renders it: what it says, and whether pressing
// again could possibly help.
//
// `offersRetry` is a property of the word rather than a default, and `false` is
// not a smaller version of `true`. Three of the eight have no next step on this
// device — the browser cannot run the ceremony, the authenticator cannot derive
// the value the account's keys are wrapped under, or the account already exists
// — and a control that can only repeat itself is worse than no control: it
// reads as a way forward, costs another system sheet or another request to
// disprove, and ends in the same sentence.
//
// **No retry is not the same as no control**, which is what the second flag is
// for: one of those three — the account already exists — has somewhere real to
// go, and telling that person to sign in from a screen with nothing to press is
// the same dead end read from the other end.
interface PasskeyRefusal {
  readonly sentence: string;
  readonly offersRetry: boolean;
  // Whether this refusal has somewhere to send the reader, and it is **not**
  // the negation of `offersRetry`. All three words that offer no retry are
  // equally stuck on this device; exactly one of them — `conflict` — is stuck
  // because the account already exists, which is a state `/welcome` can act on.
  // Deriving this from "no retry" would offer a sign-in to a browser that
  // cannot run WebAuthn and to a device that cannot hold the account's keys,
  // neither of which can complete one either.
  readonly offersSignIn: boolean;
}

// What the ceremony control is at this instant, and `none` is a state rather
// than an absence of one: it is the answer for the three refusals above.
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

  // This step's own, and the flow's business in neither direction: it creates
  // nothing, posts nothing and ends no flow. `RegisterService` navigates on the
  // 201 because that navigation is part of the act it committed; leaving a dead
  // end for the one screen that can sign somebody in is a decision about what
  // *this* screen offers, which is the same division the shell draws for its
  // own `goToSignIn`.
  private readonly router = inject(Router);

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

  // Read off the refusal rather than judging the failure word a second time, so
  // the one place a word is judged stays the `switch` below.
  protected readonly offersSignIn = computed<boolean>(
    () => this.refusal()?.offersSignIn ?? false,
  );

  // `/welcome` is the one address in this application that runs a passkey
  // assertion, which is exactly what the sentence beside this control tells the
  // reader to go and do. It is written here as well as in the shell on purpose
  // — see the `conflict` arm below — and both copies are pressed by a spec that
  // records where the router was asked to go, so a drift on either side is red.
  protected goToSignIn(): void {
    void this.router.navigateByUrl('/welcome');
  }
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
        // No retry and no way out either: a browser that cannot run the
        // ceremony cannot run an assertion on `/welcome` either, so a sign-in
        // control here would be the same dead end one screen further on.
        offersSignIn: false,
      };
    // The system sheet was closed, or it timed out. Nothing is wrong and
    // nothing needs reporting, and the sentence says so rather than treating an
    // ordinary act as an error.
    case 'cancelled':
      return {
        sentence:
          'The passkey wasn’t created. Nothing has been saved, and nothing was sent — try again whenever you’re ready.',
        offersRetry: true,
        offersSignIn: false,
      };
    // The authenticator declined because it already holds a credential named in
    // the exclusion list. Another device is the way through, so the retry is
    // real and the sentence says which kind of press to make.
    case 'duplicate':
      return {
        sentence:
          'This device already holds a passkey Budgetoid can’t reuse. Try again with a different device or security key.',
        offersRetry: true,
        offersSignIn: false,
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
        // The second of the three without a retry, and the second with nowhere
        // to go: this person has no account to sign in to, and the device that
        // could not derive the key would be asked to derive it again.
        offersSignIn: false,
      };
    case 'ceremony-failed':
      return {
        sentence:
          'Your device didn’t finish creating the passkey. Nothing has been saved.',
        offersRetry: true,
        offersSignIn: false,
      };
    // The one refusal on this screen that is about the server rather than the
    // device, and the sentence has to say so: a person told their device failed
    // will go and buy a security key for a problem a reload would have fixed.
    case 'start-failed':
      return {
        sentence:
          'Budgetoid couldn’t reach the server to start. Nothing has been saved.',
        offersRetry: true,
        offersSignIn: false,
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
        offersSignIn: false,
      };
    // **Reachable here, and the sentence is not the shell's.** The options leg
    // answers 409 when the provider identity already has an account, above its
    // own challenge — so this word now lands while *this* step is showing, with
    // nothing minted and no codes anywhere. The shell's two conflict sentences
    // both say "the ten codes you were just shown open nothing", which is false
    // on this leg in the one clause a person acts on, and neither is reachable
    // from here anyway: the shell renders them in place of the codes step.
    //
    // No retry, and the reason is the sharpest of the three: this is the one
    // refusal on the screen that a second press cannot change *by definition* —
    // the account exists, and pressing again spends another request to be told
    // so. It is `false` for the same reason `no-prf` and `unsupported` are.
    //
    // **The way out is here, and this is the only refusal on the screen that
    // has one.** The sentence sends somebody to the home page to sign in, and
    // `/register` has no navigation of its own — the app shell is a bare
    // `<router-outlet />` — so without a control this is a dead end whatever
    // the copy says, which is the defect the shell's own conflict block was
    // fixed for. `offersSignIn` is `true` here and `false` on the other two
    // words without a retry: `unsupported` and `no-prf` are stuck on this
    // device with no account at the far end, and a control offered on all three
    // alike would promise a way forward to two people who have none.
    //
    // **This step navigates itself rather than raising an output the shell
    // handles**, and the "a control written twice is one somebody eventually
    // forgets" rule does not decide against it. That rule is about the
    // *control*, and the control is written twice under either shape: the
    // shell's conflict block renders in place of the codes step, this one
    // renders while step 2 is showing, and neither can render the other's
    // markup. What an output would save is the *address* — bought at the price
    // of a button that does nothing unless the shell remembers to bind it.
    // `<app-passkey-step />` carries no bindings today, an unbound output
    // compiles, lints, and leaves this step's own spec green while the person
    // pressing it is the one who has already been refused; a silent dead
    // control is the failure this arm exists to remove, not one to reintroduce
    // one layer up. The step is not a presentational child either — it injects
    // the flow, reads its signals and calls its methods, where `codes-step`
    // deliberately does none of that — and navigating creates nothing, posts
    // nothing and ends no flow. The address is therefore written twice, and
    // each copy is pinned by a spec that presses the control and reads where
    // the router was asked to go.
    case 'conflict':
      return {
        sentence:
          'An account already exists for this Google address. Nothing was created and no passkey was made — sign in from the Budgetoid home page instead.',
        offersRetry: false,
        offersSignIn: true,
      };
    // The one word the registration request answers with that this screen never
    // sees. The POST is only made from the codes step and the shell renders
    // this in place of it, so saying something here would be inventing a
    // sentence for a state this screen cannot be in. `unknown` is the other
    // word this screen shares, because the flow's own catch publishes it here
    // too; it has its own sentence above, and the two say different things on
    // purpose.
    case 'refused':
      return null;
  }
}
