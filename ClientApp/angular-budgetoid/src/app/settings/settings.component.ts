import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import type {
  CredentialKind,
  CredentialSummary,
} from '@app-core/api/me-api.service';
import { credentialRegistrationDate } from './credential-registration-date';
import { SettingsService } from './settings.service';

// What a row shows, already in the words it shows them in. Composed here rather
// than in the template so the date is formatted once per credential instead of
// once per change detection pass, and so the revoke button's accessible name
// and the visible date cannot drift apart — they are the same string.
interface CredentialRow {
  readonly id: string;
  readonly type: string;
  readonly registeredOn: string;
  readonly storedInstant: string;
  readonly revokeLabel: string;
}

// `satisfies` rather than a type annotation: the map keeps its literal value
// types *and* fails to compile the day `CredentialKind` gains a third member,
// which is the point. A `switch` with a `default` would silently render that
// third kind as whatever the fallback said.
//
// `federated` reads as **Google** because that is the button the person pressed
// and the only provider there is. When a second one lands, the server has to say
// which — the word cannot be guessed from `federated` — and this map becomes a
// lookup on that field instead.
const TYPE_LABELS = {
  passkey: 'Passkey',
  federated: 'Google',
} as const satisfies Record<CredentialKind, string>;

function toRow(credential: CredentialSummary): CredentialRow {
  const type = TYPE_LABELS[credential.type];
  // No locale argument: production asks the runtime for the reader's own.
  const registeredOn = credentialRegistrationDate(credential.createdAtUtc);

  return {
    id: credential.id,
    type,
    registeredOn,
    storedInstant: credential.createdAtUtc,
    // Begins with the visible label so voice control still reaches the control
    // by what it can see, and carries the row's own facts because two buttons
    // named "Revoke" cannot be told apart by anyone who is not looking at the
    // screen.
    revokeLabel: `Revoke ${type}, registered ${registeredOn}`,
  };
}

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
  protected readonly credentialRows = computed<readonly CredentialRow[] | null>(
    () => {
      const credentials = this.settings.credentials();

      return credentials === null ? null : credentials.map(toRow);
    },
  );

  public ngOnInit(): void {
    // The only work the screen starts on its own. The export is never begun
    // here — it writes a file to the user's disk, so it waits for the click.
    this.settings.loadEmail();
    this.settings.loadCredentials();
  }
}
