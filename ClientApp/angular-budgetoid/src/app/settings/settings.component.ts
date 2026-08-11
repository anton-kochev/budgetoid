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
//
// `registeredOn` is empty and `storedInstant` is null for an entry whose stored
// date cannot be read — see `toRow` — so the two always move together, and the
// template drops the whole caption line when they do.
//
// `revokeLabel` is null for a row nothing can ever revoke, and that null is the
// whole statement: the template renders no button rather than a disabled one.
// Every other dead control on this screen is a promise that the ceremony will
// land and it will start working; on a row that can never be revoked the same
// button makes a promise it cannot keep, which is the worse of the two lies.
interface CredentialRow {
  readonly id: string;
  readonly type: string;
  readonly dateCaption: string;
  readonly registeredOn: string;
  readonly storedInstant: string | null;
  readonly revokeLabel: string | null;
}

// The three facts a row needs about a kind, kept together per kind rather than
// in three parallel maps, so that what a **Google** row says and what it offers
// can be read on one line instead of assembled from three places.
interface KindPresentation {
  readonly label: string;
  // `Registered` for a thing that was attached, `Generated` for a set that was
  // issued. The instant behind a set moves every time it is replaced, so the
  // word a passkey's row uses would say the wrong thing on a set's second
  // issue. The date itself is formatted by the one shared formatter — a second
  // date path is a second place for a UTC day to leak back in.
  readonly dateCaption: string;
  // Whether anything can ever revoke this row, not whether it can be revoked
  // today. A passkey's Revoke is disabled because the ceremony is missing; a
  // set has no revocation at all — the route behind one is scoped to passkeys
  // by type, so pointing it at a set answers the 404 an unknown id answers, and
  // a set is *replaced* by generating again. A federated credential is likewise
  // replaced by an email change rather than removed.
  readonly revocable: boolean;
}

// `satisfies` rather than a type annotation: the map keeps its literal value
// types *and* fails to compile the day `CredentialKind` gains a fourth member,
// which is the point. A `switch` with a `default` would silently render that
// fourth kind as whatever the fallback said — and would silently give it an
// action, which is the half that cannot be taken back.
//
// `federated` reads as **Google** because that is the button the person pressed
// and the only provider there is. When a second one lands, the server has to say
// which — the word cannot be guessed from `federated` — and this map becomes a
// lookup on that field instead.
const CREDENTIAL_KINDS = {
  passkey: { label: 'Passkey', dateCaption: 'Registered', revocable: true },
  federated: { label: 'Google', dateCaption: 'Registered', revocable: false },
  // Disabled for this key alone, deliberately, and not by widening the rule.
  // `recovery_codes` is a **discriminant value** the schema owns, not a
  // property name this codebase gets to spell: the key has to be the string
  // that arrives on the wire or the lookup misses. Adding `snake_case` to the
  // `objectLiteralProperty` formats would let the same spelling into every DTO
  // in the app, where the camelCase serialization contract does hold, and the
  // config's existing `requiresQuotes` carve-out does not reach here because
  // this token is a valid identifier and quoting it changes nothing.
  // eslint-disable-next-line @typescript-eslint/naming-convention
  recovery_codes: {
    label: 'Recovery codes',
    dateCaption: 'Generated',
    revocable: false,
  },
} as const satisfies Record<CredentialKind, KindPresentation>;

// Null for an unrevocable row, so the template has nothing to render rather
// than a name for a control it must not draw.
function revokeLabelFor(
  kind: KindPresentation,
  registeredOn: string,
): string | null {
  if (!kind.revocable) {
    return null;
  }

  // With no date to name, the clause is dropped rather than left empty:
  // `Revoke Passkey, registered ` is read out exactly as written, and two
  // entries that cannot be told apart is the state the row is already honest
  // about for two passkeys registered on one day.
  if (registeredOn === '') {
    return `Revoke ${kind.label}`;
  }

  // Begins with the visible label so voice control still reaches the control by
  // what it can see, and carries the row's own facts because two buttons named
  // "Revoke" cannot be told apart by anyone who is not looking at the screen.
  return `Revoke ${kind.label}, registered ${registeredOn}`;
}

function toRow(credential: CredentialSummary): CredentialRow {
  const kind = CREDENTIAL_KINDS[credential.type];
  // No locale argument: production asks the runtime for the reader's own.
  // Empty when the stored value cannot be read — the formatter is total on
  // purpose, because this call sits inside a computed the template reads and a
  // throw here would abandon the change detection pass rather than spoil a row.
  const registeredOn = credentialRegistrationDate(credential.createdAtUtc);

  return {
    id: credential.id,
    type: kind.label,
    dateCaption: kind.dateCaption,
    registeredOn,
    // Withheld together with the visible date rather than passed through raw. A
    // `datetime` attribute exists to be parsed; one carrying a value no parser
    // accepts is a worse answer than an absent element, and it would also put
    // the unreadable stored value on screen for anyone reading the markup.
    storedInstant: registeredOn === '' ? null : credential.createdAtUtc,
    revokeLabel: revokeLabelFor(kind, registeredOn),
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
    this.settings.loadRecoveryCodes();
  }
}
