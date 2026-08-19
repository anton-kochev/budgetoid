import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '@app-core/services/auth-service';
import { RegisterService } from '../register.service';

// The first of the three steps, and the only one that asks for nothing: it
// states which account is about to be created and offers the way on.
//
// It reads one signal and calls one method, which is why it holds no state of
// its own — the flow's state lives in `RegisterService`, provided by the shell
// and discarded with it.
//
// **The provider control is this component's own button rather than
// `<app-google-sign-in-button />`.** That button dispatches an NgRx action
// through a `LoginFacade` it provides itself, so placing it here would drag the
// store into a screen that otherwise touches neither NgRx nor the network, and
// would route this press through the auth effects rather than through the
// service the rest of this flow already depends on. The copy is deliberately
// identical to the welcome screen's: a person bounced here from there meets the
// control they already pressed once.

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

  protected signIn(): void {
    // Straight to the provider, without touching the flow. There is nothing to
    // start yet: the exchange leaves this page entirely and comes back to it,
    // and a step advanced on the way out would be a step nothing came back to.
    this.auth.signIn();
  }
}
