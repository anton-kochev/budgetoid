import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { RegisterService } from './register.service';
import { CodesStepComponent } from './steps/codes-step.component';
import { IntroStepComponent } from './steps/intro-step.component';
import { PasskeyStepComponent } from './steps/passkey-step.component';

// The registration screen: one step at a time, and the only thing in this
// client that holds the account's keys in the clear.
//
// **There is no `canDeactivate` and no `beforeunload`, and that absence is a
// decision rather than an oversight.** Abandoning this flow costs nothing:
// until the last press nothing has been created, and the ten codes on screen
// are inert verifiers no server has ever seen. A confirmation dialog on the way
// out would imply the opposite — that something is being lost — and the copy on
// every step already says the true thing instead ("Nothing is saved until the
// last step."). After the last press there is nothing to guard either: the
// screen navigates itself to `/app`.

// The three words the registration request can answer with, and the only
// failures this shell renders. Every other word in `RegisterFailure` is
// published while a step is showing, and that step says its own sentence.
type PostRequestFailure = 'refused' | 'conflict' | 'unknown';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CodesStepComponent,
    IntroStepComponent,
    MatButtonModule,
    PasskeyStepComponent,
  ],
  // **The custody decision, not a lifetime preference.** The account keys, the
  // eleven key-encryption keys and the ten recovery codes live in this service,
  // so providing it here is what makes them die with the screen. Held at the
  // root, the codes of an abandoned registration would still be readable from
  // the injector on an unrelated screen an hour later, and there is nowhere in
  // this product they could legitimately be read from. `register.service.ts`
  // argues the same rule from its own end, and neither half works alone.
  providers: [RegisterService],
  styleUrls: ['./register.component.scss'],
  templateUrl: './register.component.html',
})
export class RegisterComponent {
  protected readonly register = inject(RegisterService);

  // The shell's own, and not the flow's. `RegisterService` navigates on the
  // 201 because that navigation is part of the act it committed; leaving a dead
  // end for the screen that can sign somebody in is a decision about what this
  // screen offers, and it creates nothing, posts nothing and ends no flow.
  private readonly router = inject(Router);

  // A `switch` over the closed union rather than a set membership test, so a
  // tenth word added to `RegisterFailure` fails to compile here and has to be
  // filed on one side of this line deliberately. The side matters: a word
  // landing here replaces the codes step, and a word landing on the other side
  // is rendered by the step that is showing.
  protected readonly postRequestFailure = computed<PostRequestFailure | null>(
    () => {
      const failure = this.register.failure();

      switch (failure) {
        case 'refused':
          return 'refused';
        case 'conflict':
          return 'conflict';
        case 'unknown':
          return 'unknown';
        case null:
        case 'unsupported':
        case 'cancelled':
        case 'duplicate':
        case 'no-prf':
        case 'ceremony-failed':
        case 'start-failed':
          return null;
      }
    },
  );

  // Both readings of a 409 tell the reader to go and sign in, and this is the
  // only address in the application where that is possible: `/welcome` runs the
  // passkey assertion, and nothing else does. It is offered from the conflict
  // state alone — `refused` and `unknown` have no account to sign in to, or no
  // way to know whether they have one.
  protected goToSignIn(): void {
    void this.router.navigateByUrl('/welcome');
  }
}
