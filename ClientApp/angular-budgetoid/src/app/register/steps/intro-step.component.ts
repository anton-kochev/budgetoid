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
// **This is the only provider control in the product, and there is no shared
// component behind it.** There used to be one — a button dispatching an NgRx
// action through a facade it provided itself — and it was deleted along with the
// welcome screen's provider button, because a screen that touches neither the
// store nor the network has no reason to reach the identity provider through
// two indirections. Nothing else offers this press, so nothing here has to match
// another screen's copy: the provider is contacted once, on this step, and the
// sentence about meeting a control already pressed on the welcome screen went
// with the control it described.

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
