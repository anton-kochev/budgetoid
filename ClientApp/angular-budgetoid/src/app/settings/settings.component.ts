import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { AccountUnlockService } from './account-unlock.service';
import { toCredentialRow, type CredentialRow } from './credential-row';
import { SettingsService } from './settings.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  // Both services' lifetime is this screen's. Provided here rather than at the
  // root so an export outcome cannot survive a navigation away and reappear as
  // a claim about a visit that has exported nothing — and so an *abandoned*
  // unlock attempt dies with the screen it was started on.
  //
  // `AccountKeyCustodyService` is deliberately **not** in this list. What the
  // attempt produces is state of the **session**, which outlives every screen,
  // so custody is root-provided and read from there; route-providing it on
  // `app` is the near miss `account-keys.md` refuses, because `guestGuard`
  // bouncing an authenticated visitor off `/welcome` destroys that injector and
  // discards the keys with nothing on screen going red.
  providers: [SettingsService, AccountUnlockService],
  styleUrls: ['./settings.component.scss'],
  templateUrl: './settings.component.html',
})
export class SettingsComponent implements OnInit {
  // Exposed to the template rather than re-signalled here: the service already
  // owns every piece of state this screen renders, and a second copy would only
  // be able to drift from it.
  protected readonly settings = inject(SettingsService);

  // The Account keys section reads **two** collaborators by name, and the two
  // names are the design.
  //
  // `custody` is the *session's* state: whether this tab holds the account's
  // keys. `unlocking` is *this screen's* state: whether a ceremony is running
  // and how the last one was refused. Two objects, two lifetimes, two
  // questions.
  //
  // **Two shapes were rejected and both look tidier.** Re-exporting custody's
  // signals off the flow as pass-throughs would read as one object owning
  // lockedness, and the next person adds a local `unlocked` signal to it —
  // a second copy of a fact only custody can know. Folding both into one
  // `computed()` screen-state enum is that same second copy written down, kept
  // in step with two independent classes by hand: the moment custody publishes
  // a state the enum has no arm for, the section renders whichever arm happens
  // to be last. The template composes them instead, on the two rules written
  // out at the point they are implemented.
  protected readonly custody = inject(AccountKeyCustodyService);
  protected readonly unlocking = inject(AccountUnlockService);

  // `null` all the way through, never flattened to an empty array: "the answer
  // has not arrived" and "nothing is attached to this account" are different
  // facts and the template renders them as different sentences.
  //
  // Everything this reads is total over what a 200 can carry, and that is a
  // requirement of the position rather than a nicety: a throw in here is a
  // throw during change detection, which Angular caches on the signal and
  // rethrows on every later read, so the failure is the whole screen below this
  // list for the rest of the visit rather than one spoiled row. The shape of
  // the *body* is refused a layer earlier, at the API boundary, where a failure
  // still has a sentence waiting for it.
  protected readonly credentialRows = computed<readonly CredentialRow[] | null>(
    () => {
      const credentials = this.settings.credentials();

      return credentials === null ? null : credentials.map(toCredentialRow);
    },
  );

  public ngOnInit(): void {
    // The only work the screen starts on its own. The export is never begun
    // here — it writes a file to the user's disk, so it waits for the click.
    this.settings.loadEmail();
    this.settings.loadCredentials();
    this.settings.loadRecoveryCodes();
  }
}
