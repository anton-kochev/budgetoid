import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { AuthService } from '@app-core/services/auth-service';
import { RegisterService, type RegisterFailure } from '../register.service';

// The first of the three steps: it states which account is about to be created,
// asks the server whether it may be, and offers the way on.
//
// **It no longer asks for nothing, and that is the whole of this step's
// change.** `POST /api/registration/options` refuses with a 409 when the
// provider identity already holds an account, above its own challenge, so the
// answer exists at the first press. Read at the second one, somebody who
// already has an account is shown "your account will be created under
// <address>", presses `Continue`, reads a screen about authenticators, presses
// again, and only then is told no — a promise made in the product's own words
// and broken two screens later.
//
// It reads three signals and calls two methods, and holds no state of its own:
// the flow's state lives in `RegisterService`, provided by the shell and
// discarded with it. The live-region and busy-control rules below are the ones
// `passkey-step.component.html` argues at its own; what is written here is only
// what differs.
//
// **This is the only provider control in the product, and there is no shared
// component behind it.** There used to be one — a button dispatching an NgRx
// action through a facade it provided itself — and it was deleted along with the
// welcome screen's provider button, because a screen that touches neither the
// store nor the network has no reason to reach the identity provider through
// two indirections. Nothing else offers this press, so nothing here has to match
// another screen's copy: the provider is contacted once, on this step, and the
// sentence about meeting a control already pressed on the welcome screen went
// with the control it described.

// One refusal as this step renders it: what it says, whether the introduction's
// promise survives it, and what to press.
interface IntroRefusal {
  readonly sentence: string;
  // Whether "your account will be created under <address>" is still true. It is
  // not a restatement of {@link control}: `start-failed` leaves the promise
  // standing, because the server said nothing about the address and the next
  // press may well work, while `conflict` is the server having looked and
  // answered. Leaving the promise up beside its own refusal is the defect this
  // step was changed to remove, one screen earlier.
  readonly promiseHolds: boolean;
  // What the reader presses. `retry` is another `Continue` under a name that
  // admits to being one; `sign-in` is a way *out*, and `/welcome` is the one
  // address in this application that runs a passkey assertion. A refusal the
  // account already exists for cannot be pressed through — the account exists,
  // and pressing again spends another request to be told so.
  readonly control: 'retry' | 'sign-in';
}

// What the control is at this instant. `continue` is the state this step spends
// almost all of its life in.
type IntroControl = 'continue' | 'busy' | IntroRefusal['control'];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  selector: 'app-intro-step',
  styleUrls: ['./intro-step.component.scss'],
  templateUrl: './intro-step.component.html',
})
export class IntroStepComponent {
  // Exposed to the template rather than copied into local signals: the flow
  // already owns every value this step renders, and a second copy could only
  // drift from it.
  protected readonly register = inject(RegisterService);

  private readonly auth = inject(AuthService);

  // This step's own, and the flow's business in neither direction: it creates
  // nothing, posts nothing and ends no flow. The same division the shell and the
  // passkey step both draw for their own `goToSignIn`.
  private readonly router = inject(Router);

  protected readonly refusal = computed<IntroRefusal | null>(() => {
    const failure = this.register.failure();

    return failure === null ? null : refusalOf(failure);
  });

  // One reading for the whole control, so the template branches once. Busy wins
  // over a refusal because the flow clears the failure when an act starts: the
  // two cannot both be true, and asking the template to know that is asking it
  // to restate a rule the service already keeps.
  protected readonly control = computed<IntroControl>(() => {
    if (this.register.busy()) {
      return 'busy';
    }

    return this.refusal()?.control ?? 'continue';
  });

  // Read off the refusal rather than judging the failure word a second time, so
  // the one place a word is judged stays the `switch` below.
  protected readonly promiseHolds = computed<boolean>(
    () => this.refusal()?.promiseHolds ?? true,
  );

  protected goToSignIn(): void {
    void this.router.navigateByUrl('/welcome');
  }

  protected signIn(): void {
    // Straight to the provider, without touching the flow. There is nothing to
    // start yet: the exchange leaves this page entirely and comes back to it,
    // and a step advanced on the way out would be a step nothing came back to.
    this.auth.signIn();
  }
}

// The flow's nine words, and only two of them can land while this step is
// showing. A `switch` over the closed union rather than a lookup object or a
// set membership test, so a tenth word added to `RegisterFailure` fails to
// compile here and has to be filed on one side of this line deliberately — the
// rule `register.component.ts` keeps for the shell and `passkey-step` for step
// 2. The side matters: a word answered here renders on the introduction, and a
// word answered `null` is one this step is silent about while a later step says
// its own sentence.
function refusalOf(failure: RegisterFailure): IntroRefusal | null {
  switch (failure) {
    // The options leg answered 409: this Google address already holds an
    // account. Nothing was asked of the authenticator and no challenge was
    // issued — the server refuses above its own `IssueAsync` — so the sentence
    // is shorter than the passkey step's and deliberately not a copy of it.
    // That one ends "no passkey was made", which is worth saying where a system
    // sheet was on the screen a moment ago and says nothing at all here, where
    // no passkey was ever going to be made.
    case 'conflict':
      return {
        sentence:
          'An account already exists for this Google address. Nothing has been created — sign in from the Budgetoid home page instead.',
        // The one refusal on this step that closes the question. Leaving "your
        // account will be created under <address>" above this sentence puts a
        // promise and its own refusal on one screen, which is the defect the
        // whole change exists to remove.
        promiseHolds: false,
        control: 'sign-in',
      };
    // The server never answered at all — a status 0, a timeout, a 5xx, anything
    // a proxy invents. It said nothing about the address, so the promise above
    // still holds and another press is a real way forward.
    case 'start-failed':
      return {
        sentence:
          'Budgetoid couldn’t reach the server. Nothing has been created.',
        promiseHolds: true,
        control: 'retry',
      };
    // Everything else is published while a later step is showing, and that step
    // says its own sentence. The five ceremony words and `unsupported` belong to
    // the passkey step; `refused` and `unknown` are the registration request's
    // and the shell renders them in place of the codes step. Inventing a
    // sentence here for any of them would be writing copy for a state this step
    // cannot be in.
    case 'unsupported':
    case 'cancelled':
    case 'duplicate':
    case 'no-prf':
    case 'ceremony-failed':
    case 'refused':
    case 'unknown':
      return null;
  }
}
