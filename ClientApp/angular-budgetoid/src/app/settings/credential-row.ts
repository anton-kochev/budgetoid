// What a credential row shows, already in the words it shows them in. Composed
// here rather than in the template so the date is formatted once per credential
// instead of once per change detection pass, and so the revoke button's
// accessible name and the visible date cannot drift apart — they are the same
// string.
//
// A module of its own, beside `credential-registration-date.ts` and
// `export-filename.ts`, for the reason those are: it is pure, it is where two
// of this screen's rules actually live, and reaching them through the component
// means reaching them through a DOM. The seam matters — a row is composed from
// facts the *wire* supplies, and the wire is not bound by the types below.
import type {
  CredentialKind,
  CredentialSummary,
} from '@app-core/api/me-api.service';
import { credentialRegistrationDate } from './credential-registration-date';

// `registeredOn` is empty and `storedInstant` is null for an entry whose stored
// date cannot be read — see `credentialRegistrationDate` — so the two always
// move together, and the template drops the whole caption line when they do.
//
// `revokeLabel` is null for a row nothing can ever revoke, and that null is the
// whole statement: the template renders no button rather than a disabled one.
// Every other dead control on this screen is a promise that the ceremony will
// land and it will start working; on a row that can never be revoked the same
// button makes a promise it cannot keep, which is the worse of the two lies.
export interface CredentialRow {
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
//
// Exported because `revokeLabelFor` is: the two are one seam, and the spec
// needs to hand it a kind this application does not ship in order to test the
// rule rather than today's coincidence.
export interface KindPresentation {
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
// The fallback below does **not** relax this. It answers a kind that arrives
// from the network, not one that arrives in this source: a member added to the
// union still reddens the build here until someone decides all three of its
// facts. The two guards cover different failures and neither substitutes for
// the other.
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

// What a row says about a kind this bundle has never heard of.
//
// **The closed union is a compile-time guarantee about this source, not about a
// deployed client.** A browser holding yesterday's bundle against today's API is
// the ordinary way an unknown kind arrives, and nothing at compile time reaches
// it. Unguarded, the lookup returned `undefined`, the next property read threw,
// and the throw happened inside a `computed` the template reads — which Angular
// caches and rethrows, so the list stayed on its loading line and every section
// declared after it stopped updating for the rest of the visit. This is the
// same totality `credentialRegistrationDate` was given for the same reason; the
// kind was left partial.
//
// Rendered rather than dropped: the list is documented as *every* way into the
// account, so a filter would tell somebody auditing their credentials that one
// they cannot see does not exist — the worse of the two lies, and the one that
// leaves them nothing to act on.
//
// `Added` rather than `Registered` or `Generated`, because those two are
// precisely the claim this row cannot make. And **not revocable**: unknown is
// the row nobody has decided about, revocation is the one unrecoverable act on
// this screen, and the book already says an action nobody chose for a kind is
// the half that cannot be taken back.
const UNRECOGNISED_KIND: KindPresentation = {
  label: 'Sign-in method',
  dateCaption: 'Added',
  revocable: false,
};

// A `Map`, not the object literal indexed by a wire string. `CREDENTIAL_KINDS`
// inherits from `Object.prototype`, so `CREDENTIAL_KINDS['constructor']` *hits*
// — a `?? UNRECOGNISED_KIND` written over the literal never fires for it and
// hands the row a function whose `label` is undefined, which renders a blank
// type instead of a neutral one. A `Map` built from `Object.entries` holds the
// three keys and nothing else, so the miss is a miss for every string that is
// not one of them.
const KIND_PRESENTATIONS: ReadonlyMap<string, KindPresentation> = new Map(
  Object.entries(CREDENTIAL_KINDS),
);

/**
 * The presentation for a kind as it arrived on the wire — total, by
 * construction, over every string.
 */
function presentationFor(type: string): KindPresentation {
  return KIND_PRESENTATIONS.get(type) ?? UNRECOGNISED_KIND;
}

/**
 * The accessible name of a row's Revoke control, or `null` for a row nothing
 * can ever revoke — so the template has nothing to render rather than a name
 * for a control it must not draw.
 *
 * Exported for its spec. The rule it holds is only observable on a kind this
 * application does not currently ship, and a rule tested through today's single
 * revocable kind is a rule tested against a coincidence.
 */
export function revokeLabelFor(
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
  //
  // The clause word is the kind's **own** caption, lower-cased for its position
  // mid-sentence, and not the literal `registered`. A hard-coded word is the
  // claim that the accessible name and the visible caption cannot drift apart
  // made by a line that lets them: `Generated February 2, 2026` on screen and
  // "Revoke …, registered February 2, 2026" in the ear is a screen reader
  // describing a control by a date the sighted reader is not looking at. It
  // says the right thing today only because the one revocable kind happens to
  // be captioned `Registered`, which is a coincidence of the current kind list
  // rather than a rule anything holds. One `toLowerCase` keeps a single source
  // for the word instead of a fourth field that has to be kept in step with the
  // third.
  return `Revoke ${kind.label}, ${kind.dateCaption.toLowerCase()} ${registeredOn}`;
}

/**
 * Composes one row from one credential, in the words the row shows.
 *
 * Total in both of the ways a 200 can carry something this screen cannot use:
 * an unrecognised kind and an unreadable instant. Neither throws, because the
 * caller composes rows inside a `computed` that the template reads and no `try`
 * can be put around a signal read — a throw there is not one spoiled row, it is
 * the rest of the screen.
 */
export function toCredentialRow(credential: CredentialSummary): CredentialRow {
  const kind = presentationFor(credential.type);
  // No locale argument: production asks the runtime for the reader's own.
  // Empty when the stored value cannot be read.
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
