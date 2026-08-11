import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { toCredentialRow, type CredentialRow } from './credential-row';
import { SettingsService } from './settings.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  // The service's lifetime is this screen's. Provided here rather than at the
  // root so an export outcome cannot survive a navigation away and reappear as
  // a claim about a visit that has exported nothing.
  providers: [SettingsService],
  styleUrls: ['./settings.component.scss'],
  templateUrl: './settings.component.html',
})
export class SettingsComponent implements OnInit {
  // Exposed to the template rather than re-signalled here: the service already
  // owns every piece of state this screen renders, and a second copy would only
  // be able to drift from it.
  protected readonly settings = inject(SettingsService);

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
