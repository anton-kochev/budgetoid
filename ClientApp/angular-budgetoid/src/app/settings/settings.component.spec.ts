import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal, type Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter, type UrlTree } from '@angular/router';
import {
  MeApiService,
  type CredentialSummary,
  type MeDto,
} from '@app-core/api/me-api.service';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import { WebauthnCeremonyService } from '@app-core/security/webauthn-ceremony.service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { of, throwError, type Observable } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AccountUnlockService,
  type UnlockCeremonyFailure,
} from './account-unlock.service';
import { credentialRegistrationDate } from './credential-registration-date';
import { SettingsComponent } from './settings.component';
import { SettingsService, type ExportFailure } from './settings.service';

// The copy is pinned as whole sentences, not fragments. A fragment assertion
// (`toContain('7 days')`, `toContain('passkey')`) survives a rewrite that
// changes what the sentence promises, which is the only thing these lines
// exist to protect.
const EMAIL_LABEL = 'Email address';
const OWNER_EMAIL = 'owner@budgetoid.test';
const ERASE_BUTTON = 'Erase everything';
const EXPORT_BUTTON = 'Export';
const BACKUP_WINDOW =
  'Erased data stays in point-in-time database backups for up to 7 days, and in no other place.';
// The sentences this screen uses to explain a control it cannot offer yet, in
// the shape `voice.md`'s "not built yet" pattern sets: name the missing piece
// and what it waits on, in the same breath as the control it disables. Declared
// here so the component author has one place to copy from.
//
// **Four sites, and no two of them wait on the same thing.** The screen's
// disabled controls used to say the browser cannot run a passkey ceremony. It
// can: `/register` creates one and `/welcome` asserts one. Then they said the
// copy takes a passkey *this screen* does not ask for — and the Account keys
// section puts an **Unlock** on this very screen, so that clause has become the
// same defect one step removed, against a control the reader has just used.
// What is left is three different missing pieces:
//
//   - **Register a passkey** waits on the account's keys **as bytes**, and on
//     nothing else. A passkey is a factor, every factor stores its own wrapped
//     copy of the content key and the index key, and wrapping takes the keys
//     themselves rather than the ability to use them. Unlocking does not supply
//     them: `unlock` takes the key-encryption key as an argument and hands it
//     to custody in one statement, and custody keeps what it opened as two
//     non-extractable `CryptoKey` objects behind no accessor. Bytes would mean
//     unwrapping again under a key-encryption key *held long enough to wrap
//     with*, which is exactly what the unlock path refuses to do.
//   - **Generate recovery codes** waits on those bytes **and** on a passkey
//     assertion the *server* checks. A set is ten factors at once, so the first
//     half is common ground; the second belongs to that route alone, and the
//     unlock ceremony's assertion is minted in the browser and discarded, so a
//     person can press Unlock all afternoon without moving this control.
//   - **Revoke** and **Erase everything** wait on that checked assertion alone.
//     `POST /api/me/credentials/{id}/revocation` and `POST /api/me/erasure` are
//     both live and nothing about deleting rows needs a key unwrapped.
//
// Pasting any one of these over another puts a sentence on the screen that is
// true of a different control, and the tests below are shaped to refuse it.
// Revoke's and Erase's differ only in number — there is one Erase and one
// Revoke per row — and they are still two strings, which is why the census
// below runs over four and not three.
const REGISTRATION_EXPLANATION =
  'A new passkey needs its own copy of your account’s keys, and unlocking lets this browser use those keys without ever getting hold of them. The button stays off until that copy can be made.';
const GENERATION_EXPLANATION =
  'Ten new codes each need their own copy of your account’s keys, and replacing a set also has to be confirmed with a passkey Budgetoid checks itself — not the one unlocking asks for, which never leaves this device. The button stays off until this screen asks for both.';
const REVOCATION_EXPLANATION =
  'Revoking has to be confirmed with a passkey Budgetoid checks itself, and this screen doesn’t ask for one yet. Those buttons stay off until it does.';
const ERASURE_EXPLANATION =
  'Erasing has to be confirmed with a passkey Budgetoid checks itself, and this screen doesn’t ask for one yet. The button stays off until it does.';

// The load-bearing halves of each, and not every word. The wording above is a
// starting point somebody may improve; a version that drops any of these says
// something else. Matched against what the browser renders rather than against
// what the file happens to contain, so a template writing `&rsquo;` for `’` is
// the same string here.
//
// **Two lists where there was one**, because the two sites no longer share a
// sentence: the second phrase in each is precisely the clause that tells them
// apart, and a single list could only hold the half they still agree on.
const REGISTRATION_PHRASES = [
  'its own copy of your account’s keys',
  'unlocking lets this browser use those keys without ever getting hold of them',
] as const;
const GENERATION_PHRASES = [
  'their own copy of your account’s keys',
  'confirmed with a passkey Budgetoid checks itself',
] as const;
const ERASURE_PHRASES = [
  // The qualifier is the new half and is not decoration. Without it the
  // sentence says this screen asks for no passkey at all, which the Account
  // keys section makes false a few lines down the page.
  'has to be confirmed with a passkey Budgetoid checks itself',
  'this screen doesn’t ask for one yet',
] as const;

// The claims those sentences replace, kept as fragments on purpose — the defect
// *is* the fragment, and a rewrite that keeps any of these clauses inside a
// longer sentence is the same lie. Asserted absent rather than merely not
// asserted present: a template that left the old paragraph standing beside the
// new one satisfies every `toContain` in this file.
const STALE_CEREMONY_CLAIM = 'can’t run a passkey check in the browser';
const STALE_REGISTRATION_CLAIM = 'can’t register passkeys yet';
// The third and newest of them, retired by the Account keys section rather than
// by a route or a service: this screen asks for a passkey, in plain sight, so
// no control on it may explain itself by saying that it does not. It survives
// as a fragment because that is the shape it would come back in — the two
// sentences that still end at *this screen doesn’t ask for one yet* are true
// only because of the words in front of them.
const STALE_UNASKED_PASSKEY_CLAIM = 'a passkey this screen doesn’t ask for';

// The one control on this screen that works, and the route behind it. A verb in
// sentence case, per `voice.md`.
const SIGN_OUT_BUTTON = 'Sign out';
// The origin the sign-out block configures, and the two addresses derived from
// it. One origin, never two copies of a URL: a second literal drifts, and a
// drifted one makes `expectOne` report "no request" for a request that went out
// to the wrong place — the least informative way this can fail.
const API_ORIGIN = 'https://api.test';
const SIGN_OUT_URL = `${API_ORIGIN}/api/me/session/revocation`;
// Where a signed-out person lands. The screen `sessionExpiryInterceptor` sends
// anybody whose session ended, so a sign-out ending anywhere else would give
// the product two answers to one question.
const WELCOME_ROUTE = '/welcome';

// `ConfigurationService` is not `providedIn: 'root'`, and `BaseApiService`
// reads `apiBaseUrl` out of it on every request it sends. `getConfig()` is the
// whole of what anything under this screen reads.
const CONFIGURATION_STUB = {
  provide: ConfigurationService,
  useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
} satisfies Provider;
const EXPORT_BUILD_FAILURE =
  'The export couldn’t be built, so nothing was saved — Budgetoid sends the whole file or none of it. Try again in a few minutes.';
const EXPORT_SESSION_FAILURE =
  'Your session has ended. Reload the page to sign in again.';
const EXPORT_RUNNING = 'Preparing your file…';
const EXPORT_CONFIRMED = 'Exported.';
const EMAIL_FAILURE = 'Couldn’t load your email address. Reload the page.';
const OPERATOR_READABLE =
  'Budgetoid’s operators can read everything you record: amounts, dates, currency codes, account types, the order you arrange things in, the timestamps on every row, the identifiers behind them, and your email address. Today that also includes the names and notes you type. Nothing is encrypted with a key only you hold — not yet.';
const CREDENTIALS_HEADING = 'Ways to sign in';
const REGISTER_BUTTON = 'Register a passkey';
const REVOKE_BUTTON = 'Revoke';
// The class the screen's own stylesheet hangs `min-height:
// var(--bud-touch-target)` on, because Material's M3 button is shorter than the
// 48px minimum `accessibility.md` sets and `components.md` restates. jsdom
// applies no stylesheet, so a spec here cannot measure the rendered height; the
// class is the seam between the two halves, and it is the half that goes missing
// — a control written without it looks correct in every screenshot and is under
// the minimum on every phone.
const TOUCH_TARGET_CLASS = 's-button';
// What Material's `mat-stroked-button` and `mat-flat-button` render as. The
// class rather than the attribute selector, because the class is what carries
// the treatment into the DOM and what the theme styles.
const OUTLINE_CLASS = 'mat-mdc-outlined-button';
const FILLED_CLASS = 'mat-mdc-unelevated-button';
const CREDENTIALS_LOADING = 'Loading your ways to sign in…';
const CREDENTIALS_FAILURE =
  'Couldn’t load your ways to sign in. Reload the page.';
const CREDENTIALS_EMPTY = 'Nothing is attached to your account yet.';
const PASSKEY_TYPE = 'Passkey';
const FEDERATED_TYPE = 'Google';
const RECOVERY_SET_TYPE = 'Recovery codes';
// What a row says about a kind this bundle has never heard of. Neutral, and
// deliberately not an apology: the server has told us something can sign this
// account in, which is the fact the list exists to state. What it is called is
// the only part we do not know, and "Sign-in method" is true of every kind the
// union does carry as well as of every kind it might gain.
const UNKNOWN_KIND_TYPE = 'Sign-in method';
// And the caption word, which cannot be `Registered` or `Generated` because
// those are the two things it might be. `Added` is true either way.
const ADDED_CAPTION = 'Added';
const RECOVERY_HEADING = 'Recovery codes';
const GENERATE_BUTTON = 'Generate recovery codes';
const RECOVERY_LOADING = 'Loading your recovery codes…';
const RECOVERY_FAILURE = 'Couldn’t load your recovery codes. Reload the page.';
const RECOVERY_NONE = 'You have no recovery codes.';
const RECOVERY_ONE = 'You have 1 recovery code left.';
const RECOVERY_MANY = 'You have 5 recovery codes left.';
// The count the browser left stranded beside a failure sentence. A different
// number from `RECOVERY_MANY` on purpose: that one is set by hand on the stub,
// this one comes back from a stubbed response through the real service, and a
// shared constant would let a copy-paste between the two blocks pass unnoticed.
const RECOVERY_TEN = 'You have 10 recovery codes left.';

// The Account keys section. Every sentence below is `components.md`'s, taken
// from the state table in its chapter word for word — the copy there is the
// specification and not an example of it.
const ACCOUNT_KEYS_HEADING = 'Account keys';
// The heading id, which is the seam `sectionFor` reads and the one thing here
// the book does not name. It follows the convention the four sections already
// on this screen keep — the heading's words, hyphenated, plus `-heading` — and
// is deliberately not `account-heading`, which the Account section at the top of
// the page already answers to.
const ACCOUNT_KEYS_HEADING_ID = 'account-keys-heading';
const UNLOCK_BUTTON = 'Unlock';
// The section's two in-flight lines. **Two and not one flag**, because they are
// two different moments and a person can act on the difference: the first is the
// system sheet, and the thing to do is touch a sensor or pick a key up off the
// desk; the second is a request, and the thing to do is wait.
//
// The waiting line is deliberately **not** the registration step's *Waiting for
// your device.* There the device is about to *make* something and the person is
// waiting on a machine. Here they are being asked for an object they already
// own, often for a specific one, and the sentence names the thing to go and
// find.
const UNLOCK_WAITING = 'Waiting for your passkey.';
const UNLOCK_OPENING = 'Opening your account…';
const UNLOCK_HELD = 'Your account is unlocked in this tab.';
// The flow's five refusals — facts about a *device*, where custody's three are
// facts about a *read* and a *factor*. Five because each names a different next
// step: change browser, try again when ready, fetch the other device, retry the
// ceremony, retry the whole thing. The two unions are never derived from each
// other and the flow deliberately carries no member a key that opened nothing
// could be filed under.
const UNLOCK_UNSUPPORTED =
  'This browser can’t check a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can.';
const UNLOCK_CANCELLED =
  'The passkey check was cancelled. Nothing has changed — try again whenever you’re ready.';
const UNLOCK_NO_PRF =
  'This device can’t open your account’s keys. Try the device that holds the passkey you made this account with.';
const UNLOCK_CEREMONY_FAILED =
  'Your device didn’t finish the passkey check. Nothing has changed.';
const UNLOCK_UNKNOWN =
  'Budgetoid couldn’t finish unlocking. Nothing has changed — try again.';
// Custody's three — facts about a *read* and a *factor*, and three because a
// person's next move is three different things: present another factor, press
// again in a minute, sign in again. Collapsing any two sends somebody down a
// road that cannot help them, which is why the test over these compares them
// against each other before it looks at the screen.
const CUSTODY_UNOPENED =
  'Budgetoid couldn’t open your account’s keys with that passkey. If this account has another passkey, try again and choose that one.';
const CUSTODY_UNREACHABLE =
  'Budgetoid couldn’t reach the server. Try again in a minute.';
const CUSTODY_UNAUTHENTICATED =
  'Budgetoid wouldn’t hand your keys back to this browser. Sign out and sign in again.';
// The section's two standing paragraphs, from the chapter's *Honesty about
// today*. They are not decoration and they are not a preamble: nothing a person
// records is encrypted, so unlocking changes nothing they can see, and a section
// that stopped saying so would leave a reader who has just watched their
// authenticator answer looking for whatever it revealed. The register is the
// **What we can read** section's — a fact about the system, with no apology
// around it — which is why the second sentence names the gap outright instead of
// promising it will close.
const HONESTY_KEYS_HELD =
  'Your passkey holds the keys your records will be encrypted with. Budgetoid never sees them, and this browser forgets them every time the page reloads.';
const HONESTY_NOTHING_ENCRYPTED =
  'Nothing you record is encrypted yet, so unlocking changes nothing you can see today.';
const ACCOUNT_KEYS_HONESTY = [
  HONESTY_KEYS_HELD,
  HONESTY_NOTHING_ENCRYPTED,
] as const;
// The class the screen's own stylesheet hangs `min-height: 1lh` on, so the
// region's last line holds one line box open whether or not it has anything to
// say. Pinned as a class for the reason `TOUCH_TARGET_CLASS` is: jsdom applies
// no stylesheet, so a spec here cannot measure the reserved box, and the class is
// the seam between the two halves — it is the half that goes missing.
const HELD_LINE_CLASS = 's-held';
// The class every outcome region on this screen carries, and the one thing
// about the account-keys region that no assertion in this file could see.
//
// `accountKeysRegion` finds the region by `[role="status"]`, which is the
// *semantic* half — announced, and announced politely. `.s-outcome` is the
// other half: the screen's own stylesheet hangs the region's spacing and its
// place in the section's grid on this class, so a region that lost it is still
// found by every selector in this file, still announces all eleven of its
// sentences, and lands in the wrong place on the page with the whole suite
// green. It is pinned as a class for the reason `HELD_LINE_CLASS` and
// `FAILURE_CLASS` are: jsdom applies no stylesheet, so the layout itself cannot
// be measured here, and the class is the seam between the two halves — it is
// the half that goes missing.
const OUTCOME_REGION_CLASS = 's-outcome';
// The two treatments the section's eleven lines are drawn in. `.s-error` is what
// the screen's own stylesheet hangs `color: var(--bud-over)` on; `.s-prose`
// carries no colour at all. The state table in the design chapter assigns one per
// row — `--bud-over` for the eight refusals, `--bud-text` for the two in-flight
// lines — and nothing in this file could tell them apart before.
//
// jsdom applies no stylesheet, so a spec here cannot read the rendered colour;
// the class is the seam between the two halves and it is the half that goes
// missing. **The worse direction is not the one a reader expects.** A refusal
// that lost its colour still reads as a refusal — *colour is never the message*
// is the rule, and every one of these sentences obeys it. A **wait** that gained
// the failure colour is the defect: somebody watching a ceremony that is still
// running reads red text and stops waiting.
const FAILURE_CLASS = 's-error';
const PROSE_CLASS = 's-prose';

// Two entries far enough apart to be told apart on screen, which the section's
// own rules make a requirement rather than a convenience: the row shows the
// type and the date and nothing else, so two entries of the same type on the
// same local day are genuinely indistinguishable — the design chapter calls
// that the honest maximum — and any test about *which* row it is has to give
// them different days.
//
// The dates are the ones a reader at the pinned zone sees, not the UTC days in
// the wire values: 22:00Z on the 11th is already the 12th at UTC+14.
const PASSKEY: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000a1',
  type: 'passkey',
  createdAtUtc: '2026-03-11T22:00:00Z',
};
const FEDERATED: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000b2',
  type: 'federated',
  createdAtUtc: '2026-01-12T08:30:00Z',
};
// A set is a row of this list on the same argument a passkey is: redeeming a
// code opens a full session. It carries no action.
const RECOVERY_SET: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000d4',
  type: 'recovery_codes',
  createdAtUtc: '2026-02-02T05:00:00Z',
};
// A second *revocable* row, so that "each control is named for its own row" has
// two controls to tell apart. It cannot be the federated row any more — that
// row carries no Revoke — so telling two Revokes apart now means two passkeys.
const PASSKEY_OTHER: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000e5',
  type: 'passkey',
  createdAtUtc: '2026-04-20T02:00:00Z',
};
// A 200 carrying one field the screen cannot read. Every *network* failure on
// this screen is caught and rendered as a sentence; a successful response with a
// bad value in it is the one path with nothing between it and the template — and
// the formatting happens inside a `computed` the template reads, so what would
// otherwise be one wrong row is a throw during change detection that stops the
// pass on the spot.
const UNREADABLE: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000c3',
  type: 'passkey',
  createdAtUtc: 'the twelfth of March',
};
// A kind the union does not carry. **The assertion is the point, not a
// workaround for one**: `CredentialKind` is a closed union over what *this
// source* knows, which is a compile-time guarantee about our code and says
// nothing about what a deployed bundle is sent. A browser holding yesterday's
// bundle against today's API is the ordinary way this arrives, and no amount of
// closing the union at compile time reaches it — so a fixture that could be
// written without an assertion would not be this defect.
const UNKNOWN_KIND = {
  id: '019f4c0a-0000-7000-8000-0000000000f6',
  type: 'sms_one_time_code',
  createdAtUtc: '2026-05-04T22:00:00Z',
} as unknown as CredentialSummary;
// The same hole reached through a name every plain object already answers to.
// A lookup written as `MAP[type]` does not merely miss on this — it *hits*, on
// `Object.prototype.constructor`, and hands the row a function whose `.label`
// is undefined. A fallback written as `?? UNKNOWN` never runs, so the row
// renders blank rather than neutrally, and the guard the fix installs looks
// like it works everywhere it is tested.
const INHERITED_KIND = {
  id: '019f4c0a-0000-7000-8000-000000000f07',
  type: 'constructor',
  createdAtUtc: '2026-05-04T22:00:00Z',
} as unknown as CredentialSummary;
const UNKNOWN_KIND_DATE = 'May 5, 2026';
const PASSKEY_DATE = 'March 12, 2026';
const FEDERATED_DATE = 'January 12, 2026';
const RECOVERY_SET_DATE = 'February 2, 2026';
// The one word that introduces the date on a row. Pinned as a fragment rather
// than as a whole sentence — the exception the rest of this list is the rule for
// — because the defect it catches *is* the fragment: a row that kept the caption
// after dropping the date it introduces renders exactly this word and nothing
// after it.
const REGISTERED_CAPTION = 'Registered';
// The word a recovery-code set's caption uses instead of `Registered`. Pinned
// as a fragment for the same reason `REGISTERED_CAPTION` is: the defect is the
// word itself.
const GENERATED_CAPTION = 'Generated';
// The visible label first, so voice control still reaches the control by what
// it can see; the rest is what tells two buttons named "Revoke" apart.
const REVOKE_PASSKEY = 'Revoke Passkey, registered March 12, 2026';
const REVOKE_PASSKEY_OTHER = 'Revoke Passkey, registered April 20, 2026';
// Kept only as a negative. The federated row carries no Revoke at all, so this
// is the name of a control that must not be findable — a constant asserted to
// match nothing rather than something.
const REVOKE_FEDERATED = 'Revoke Google, registered January 12, 2026';
// The same name with the clause the row cannot fill left off, rather than
// `Revoke Passkey, registered ` — a sentence that stops mid-word is read out
// loud exactly as written.
const REVOKE_DATELESS = 'Revoke Passkey';

// Real signals, not readonly wrappers: each test drives one state by setting
// them, so the component is exercised through its inputs rather than through
// the network the service would otherwise reach for.
class SettingsServiceStub {
  public readonly email = signal<string | null>(null);
  public readonly emailFailed = signal(false);
  public readonly exporting = signal(false);
  public readonly exported = signal(false);
  public readonly exportFailure = signal<ExportFailure | null>(null);
  // Starts `null`, like the real service: "not asked yet" is a third state
  // beside "here they are" and "you have none", and a stub seeded with `[]`
  // would put the screen's first paint in a state the real one never reaches.
  public readonly credentials = signal<readonly CredentialSummary[] | null>(
    null,
  );
  public readonly credentialsFailed = signal(false);
  // Starts `null`, like the real service: at rest is a state of its own and is
  // not a zero, and a stub seeded with `0` would put the screen's first paint
  // in a state the real one never reaches.
  public readonly recoveryRemaining = signal<number | null>(null);
  public readonly recoveryFailed = signal(false);
  // False at first paint, so the stub's default render *is* the section's
  // at-rest state: region present and empty. The real service sets it inside
  // `loadRecoveryCodes`, which this stub deliberately does not do.
  public readonly recoveryLoading = signal(false);
  public loadEmail = vi.fn();
  public loadCredentials = vi.fn();
  public loadRecoveryCodes = vi.fn();
  public export = vi.fn();
}

// The account's keys, as the screen reads them. Real signals for the same reason
// `SettingsServiceStub`'s are: every state below is driven by setting one.
//
// **Stubbed rather than left to the root, and that is not a preference.** The
// real `AccountKeyCustodyService` is `providedIn: 'root'` and injects
// `MeApiService`, which extends `BaseApiService`, which injects
// `ConfigurationService` — and that one is a bare `@Injectable()` with no
// `providedIn`. The moment the component injects custody, every test in this
// block dies at construction with `NullInjectorError: No provider for
// ConfigurationService`, before a single assertion is reached. `CONFIGURATION_STUB`
// would close that too, but it would also put the real custody service on the
// screen: `status()` would be permanently `'locked'` and nothing here could
// drive the other two.
//
// **`implements` a surface derived from the real class, and that clause is not
// decoration.** `keyof` over a class type yields its *public* members only —
// `#api`, `#status` and the rest are not on it — so `Pick<S, keyof S>` is the
// compiler's own census of what a template can reach. The alternative was
// measured: the flow grew `working`, this file's stub did not, and the
// template's call to it threw during change detection — 118 of the 127 tests
// here died on one message, `ctx.unlocking.working is not a function`, naming
// neither the stub nor the member nor the behaviour. With the clause the same
// omission is a compile error naming the missing member, before anything runs.
type AccountKeyCustodySurface = Pick<
  AccountKeyCustodyService,
  keyof AccountKeyCustodyService
>;

class AccountKeyCustodyStub implements AccountKeyCustodySurface {
  public readonly status = signal<AccountKeyStatus>('locked');
  public readonly unlockFailure = signal<UnlockFailure | null>(null);
  public unlock = vi.fn();
  public adopt = vi.fn();
  public lock = vi.fn();
  // The three key-backed operations, here because the `Pick` census above did
  // exactly the job its own comment describes — twice now. The service's public
  // surface grew by `sealField` and `openField`, and then again by
  // `blindIndex`, and each time this stub was a compile error naming the
  // missing member before anything ran. Nothing on this screen calls any of
  // them.
  public sealField = vi.fn();
  public openField = vi.fn();
  public blindIndex = vi.fn();
}

type AccountUnlockSurface = Pick<
  AccountUnlockService,
  keyof AccountUnlockService
>;

// The ceremony half, which is a fact about a *device* where custody's is a fact
// about a *read*. Both are stubbed and both are read by the section, because the
// precedence between them is one of the things pinned below.
class AccountUnlockStub implements AccountUnlockSurface {
  public readonly busy = signal(false);
  public readonly failure = signal<UnlockCeremonyFailure | null>(null);
  // **A signal of its own, deliberately not composed out of the two beside it,
  // and that choice is the only thing in this file that can hold the rule the
  // flow was changed to establish.**
  //
  // `working` is "either half is running", and the flow owns it: the handler
  // guards on it and the template binds to it, so the attribute and the guard
  // are the same width by construction. A template that went back to assembling
  // `keys === 'unlocking' || unlocking.busy()` for itself would be the defect
  // that fix removed — and it is **invisible to a value assertion**, because a
  // correct `working` and a correct reassembly agree in every state the flow can
  // actually be in. Measured: with that local restored to the template and the
  // service still publishing a correct `working`, this file is 127/127 green.
  //
  // A stub composing `busy() || custody.status() === 'unlocking'` would be a
  // third copy of the predicate and would agree with both, so it would pin
  // nothing either. An independent signal can be put in a state the flow itself
  // cannot reach — running while `busy` is false and custody reads `locked` —
  // and there the two readings part company. `holds the unlock control on the
  // reading the flow publishes, not on one it reassembles` below stands in
  // exactly that state, and it is the only test here that does.
  //
  // The price is a discipline every other test in this file keeps: an
  // arrangement that puts the screen in an in-flight state sets this to what the
  // real flow would publish for it, or the fixture is telling the screen
  // something no running product would.
  public readonly working = signal(false);
  public unlock = vi.fn();
}

describe('SettingsComponent', () => {
  let service: SettingsServiceStub;
  let custody: AccountKeyCustodyStub;
  let unlock: AccountUnlockStub;
  let fixture: ComponentFixture<SettingsComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    service = new SettingsServiceStub();
    custody = new AccountKeyCustodyStub();
    unlock = new AccountUnlockStub();
    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        // Both at the module level, and `AccountUnlockService` deliberately so
        // even though the component provides it. `overrideComponent(…, { set:
        // { providers } })` below **replaces** the component's array rather
        // than adding to it, so whatever the component declares is gone by the
        // time anything is injected and the lookup walks up to here. A stub
        // written into the `set` array instead would work today and stop
        // working the day somebody moves the provider, which is the failure
        // mode this arrangement has no version of.
        { provide: AccountKeyCustodyService, useValue: custody },
        { provide: AccountUnlockService, useValue: unlock },
      ],
    });
    // The stub is installed on the component, not on the module. A module-level
    // provider is shadowed the moment the component declares one of its own —
    // which is how the screen stops carrying state between visits — and every
    // test here would then instantiate the real service with no HttpClient
    // behind it. `set` replaces the component's providers array, so this holds
    // whether the component provides SettingsService or leaves it to the root.
    TestBed.overrideComponent(SettingsComponent, {
      set: { providers: [{ provide: SettingsService, useValue: service }] },
    });
    await TestBed.compileComponents();
    fixture = TestBed.createComponent(SettingsComponent);
    host = fixture.nativeElement as HTMLElement;
    // Exactly one — the NFR-021 test below depends on this being the whole of
    // the interaction that precedes it.
    fixture.detectChanges();
  });

  it('loads the account email on initialization', () => {
    // Assert
    // Without this the email row is furniture: a template binding to a signal
    // nobody ever fills renders an empty value forever and every other
    // assertion here still passes.
    expect(service.loadEmail).toHaveBeenCalledOnce();
  });

  // NFR-021. The behavioural form of "nothing has to be opened first": if both
  // controls are already present after a bare render, then activating one is
  // interaction two, and there is no interaction one to spend on a disclosure.
  //
  // Limit, stated honestly: this catches a *lazily rendered* disclosure — a
  // `matExpansionPanelContent` body, a lazy tab — because those keep their
  // children out of the DOM until opened. It does not catch a CSS-collapsed
  // one, and it does not catch `<details>`, which renders its children into
  // the DOM whether or not it is open. A reviewer holds that half.
  it('offers export and erasure with no prior interaction', () => {
    // Act
    const exportButton = buttonNamed(host, EXPORT_BUTTON);
    const eraseButton = buttonNamed(host, ERASE_BUTTON);

    // Assert
    expect(exportButton).not.toBeNull();
    expect(eraseButton).not.toBeNull();
  });

  it('finds no control for a name the screen does not carry', () => {
    // Assert
    // Control for the test above. Without it a helper that ignored the name
    // and handed back the first button it found would be green on any page
    // that happened to render two buttons.
    //
    // The name is not idle. `Sign out` used to stand here and cannot any more —
    // this screen now carries one — so the absent control is the one
    // `erasure.md` says the product will never offer: nothing restores,
    // undeletes or reactivates an account, and no route on the server would
    // answer a button named this.
    expect(buttonNamed(host, 'Restore account')).toBeNull();
  });

  it('exports when the export control is activated', () => {
    // Arrange
    const exportButton = buttonNamed(host, EXPORT_BUTTON);
    expect(exportButton).not.toBeNull();

    // Act
    exportButton?.click();

    // Assert
    expect(service.export).toHaveBeenCalledOnce();
  });

  it('does not export on render', () => {
    // Assert
    // Control for the test above: a component calling `export()` from
    // `ngOnInit` — or from a template expression — satisfies "was called once
    // after a click" without the click having done anything.
    expect(service.export).not.toHaveBeenCalled();
  });

  // The confirmation is a requirement, not decoration. The browser saves the
  // file without a visible act of its own — no dialog, no page change, and on
  // most configurations not even a prompt — so a screen that says nothing
  // leaves the user having clicked a button that produced no observable
  // effect, which reads as broken and invites the second click the service
  // already refuses.
  it('confirms a finished export in place', () => {
    // Arrange
    service.exporting.set(false);
    service.exported.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(sectionFor(host, 'export-heading'))).toContain(
      EXPORT_CONFIRMED,
    );
  });

  it('confirms nothing before an export has finished', () => {
    // Assert
    // Control for the test above: a template rendering the confirmation
    // unconditionally passes "confirms a finished export" and tells a user who
    // has exported nothing that their file is ready.
    expect(normalize(sectionFor(host, 'export-heading'))).not.toContain(
      EXPORT_CONFIRMED,
    );
  });

  it('says the export is running while it runs', () => {
    // Arrange
    service.exporting.set(true);
    service.exported.set(false);

    // Act
    fixture.detectChanges();
    const section = normalize(sectionFor(host, 'export-heading'));

    // Assert
    // The two states are each other's control, which is why the negative half
    // is here rather than in a test of its own: a template that showed both
    // lines at once passes a presence-only check while telling the user the
    // file is being prepared and already exported in the same breath.
    expect(section).toContain(EXPORT_RUNNING);
    expect(section).not.toContain(EXPORT_CONFIRMED);
  });

  it('says nothing is running before an export starts', () => {
    // Assert
    // Control for the test above: a status line rendered unconditionally
    // claims a file is being prepared on a screen that has done nothing.
    expect(normalize(sectionFor(host, 'export-heading'))).not.toContain(
      EXPORT_RUNNING,
    );
  });

  // A live region is only announced if the assistive technology was watching it
  // before the text arrived; one inserted into the DOM together with its
  // content is announced by nothing. The template says so in a comment and
  // nothing else held it, so the obvious tidy-up — wrapping `.s-outcome` in an
  // `@if` so an empty div does not render — keeps every other test on this
  // screen green while silently ending every announcement the export makes.
  it('carries the export outcome region before anything has happened', () => {
    // Act
    const region = sectionFor(host, 'export-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    expect(region).not.toBeNull();
    // Both halves are load-bearing. Presence alone is satisfied by a region
    // that always renders a line of text, which is a screen telling a user who
    // has done nothing that something happened; emptiness alone is satisfied
    // by no region at all.
    expect(normalize(region ?? null)).toBe('');
  });

  it('keeps the erasure control inert', () => {
    // Act
    const eraseButton = buttonNamed(host, ERASE_BUTTON);
    const section = sectionFor(host, 'erase-heading');

    // Assert
    expect(eraseButton?.disabled).toBe(true);
    // The disabled attribute alone leaves a dead control with no account of
    // itself; the sentence is what makes the state legible.
    expect(normalize(section)).toContain(ERASURE_EXPLANATION);
    // And it is legible about the *right* thing. The browser registers a
    // passkey on `/register`, so the sentence this replaces was telling a
    // person their browser cannot do something it just did — and the honest
    // reason is narrower and less flattering: the route and the ceremony both
    // exist, and this screen has not been wired to them.
    expect(normalize(section)).not.toContain(STALE_REGISTRATION_CLAIM);
  });

  it("says the two key-wrapping controls wait on the account's keys as bytes", () => {
    // Act
    const credentials = normalize(sectionFor(host, 'credentials-heading'));
    const recovery = normalize(sectionFor(host, 'recovery-heading'));

    // Assert
    // These two both create a factor, every factor stores its own wrapped copy
    // of the account's content key and index key, and wrapping takes those keys
    // as **bytes**. That much is common ground and is why the pair is asserted
    // in one test.
    //
    // **What blocks them is narrower than "the browser cannot do it" and
    // narrower than "this screen asks for no passkey", and these pins are what
    // keep the sentences on the narrow reason.** The envelopes come back from
    // `GET /api/me/account-keys` and `AccountKeyCustodyService` opens both on
    // every passkey sign-in; the Account keys section runs a ceremony on this
    // very screen. What neither supplies is bytes: `unlock` takes the
    // key-encryption key as an argument and hands it on in one statement, and
    // custody keeps what it opened as non-extractable `CryptoKey` objects behind
    // no accessor. Changing the copy therefore moves these phrase lists and the
    // template together, in one commit; changing either alone reddens this test.
    //
    // **Two lists, because the two sentences part company on the second
    // clause.** Registering waits on the bytes alone; generating waits on the
    // bytes *and* on an assertion the server checks, which is strictly more.
    for (const phrase of REGISTRATION_PHRASES) {
      expect(
        credentials,
        `the credentials section does not say "${phrase}".`,
      ).toContain(phrase);
    }
    for (const phrase of GENERATION_PHRASES) {
      expect(
        recovery,
        `the recovery-codes section does not say "${phrase}".`,
      ).toContain(phrase);
    }

    // And neither says the browser cannot run a ceremony, because it can. Both
    // halves matter: without the negative, a section carrying the new sentence
    // *and* the old paragraph beside it passes the loop above while still
    // telling a person their browser cannot do what it did on the way in.
    expect(credentials).not.toContain(STALE_CEREMONY_CLAIM);
    expect(recovery).not.toContain(STALE_CEREMONY_CLAIM);
    expect(credentials).not.toContain(STALE_REGISTRATION_CLAIM);
    expect(recovery).not.toContain(STALE_REGISTRATION_CLAIM);
  });

  // The claim that outlived the two above it, and the one this screen's own
  // Unlock control makes false. Its own test rather than a line appended to the
  // one above, because it is a rule about **every** section: a sentence naming a
  // capability the reader cannot find is the defect, and the defect does not
  // care which control the sentence is under.
  it('has no section claiming this screen asks for no passkey', () => {
    // Act
    const sections = [
      ['ways to sign in', sectionFor(host, 'credentials-heading')],
      ['recovery codes', sectionFor(host, 'recovery-heading')],
      ['account keys', sectionFor(host, ACCOUNT_KEYS_HEADING_ID)],
      ['erase everything', sectionFor(host, 'erase-heading')],
    ] as const;

    // Assert
    // Absence, not the new wording's presence, and the difference is the whole
    // test: a template that left the old paragraph standing beside a new one
    // satisfies every `toContain` in this file. Each section is asserted present
    // first, or the section that has not been written yet passes this vacuously
    // — `normalize(null)` is the empty string, which contains nothing.
    for (const [name, section] of sections) {
      expect(
        section,
        `the settings screen carries no ${name} section.`,
      ).not.toBeNull();
      expect(
        normalize(section),
        `the ${name} section still says this screen asks for no passkey.`,
      ).not.toContain(STALE_UNASKED_PASSKEY_CLAIM);
    }
  });

  it('says erasing waits on a checked assertion, not that the browser cannot do it', () => {
    // Arrange
    // The guards that make the comparisons below able to fail. **Every pair, not
    // the one pair this used to check.** Four sites now carry four sentences,
    // and if a later edit collapsed any two of the constants into one value the
    // assertions after them would pass on a screen saying the same thing twice —
    // which is the exact implementation this test exists to refuse. The
    // sentences are compared as substrings in both directions rather than merely
    // for inequality: one sentence that *contains* another is the same defect
    // with two extra words on the end.
    const sentences = [
      ['registration', REGISTRATION_EXPLANATION],
      ['generation', GENERATION_EXPLANATION],
      ['revocation', REVOCATION_EXPLANATION],
      ['erasure', ERASURE_EXPLANATION],
    ] as const;

    for (const [name, sentence] of sentences) {
      for (const [otherName, other] of sentences) {
        if (name === otherName) {
          continue;
        }

        expect(
          sentence,
          `the ${name} sentence and the ${otherName} sentence are the same sentence.`,
        ).not.toContain(other);
      }
    }

    // Act
    const erase = normalize(sectionFor(host, 'erase-heading'));
    const recovery = normalize(sectionFor(host, 'recovery-heading'));

    // Assert
    // Erasure waits on an assertion the **server** checks, not on the browser
    // and no longer on "a passkey this screen doesn't ask for": this screen asks
    // for one, in the Account keys section, and the qualifier is what keeps the
    // sentence true in front of a reader who has just used it.
    // `POST /api/me/erasure` exists and the ceremony that authorizes it is one
    // this client can run; what is missing is the confirmation flow
    // `components.md` specifies and the wiring behind this button.
    for (const phrase of ERASURE_PHRASES) {
      expect(erase, `the erasure section does not say "${phrase}".`).toContain(
        phrase,
      );
    }
    expect(erase).not.toContain(STALE_REGISTRATION_CLAIM);
    expect(erase).not.toContain(STALE_CEREMONY_CLAIM);

    // The comparisons that are the test. The cheapest wrong implementation is
    // one sentence pasted at every site, and it passes every `toContain` on this
    // screen. The four do not wait on the same thing: registering waits on the
    // account's keys as bytes, generating on those bytes *and* on a checked
    // assertion, revoking and erasing on the checked assertion alone. Collapsing
    // any of them puts a sentence on the screen that is true of a different
    // control — and collapsing the first two in particular erases the answer to
    // "why can't I just add another way in".
    expect(
      erase,
      'the erasure section explains itself with the registration sentence.',
    ).not.toContain(REGISTRATION_EXPLANATION);
    expect(
      erase,
      'the erasure section explains itself with the recovery-codes sentence.',
    ).not.toContain(GENERATION_EXPLANATION);
    expect(
      recovery,
      'the recovery-codes section explains itself with the erasure sentence.',
    ).not.toContain(ERASURE_EXPLANATION);
    expect(
      recovery,
      'the recovery-codes section explains itself with the registration sentence.',
    ).not.toContain(REGISTRATION_EXPLANATION);
  });

  it('leaves the export control available before an export starts', () => {
    // Act
    const exportButton = buttonNamed(host, EXPORT_BUTTON);

    // Assert
    // Control for the test above: a template that disabled every button — or a
    // component that guarded the whole page behind a loading flag — passes
    // "erase is disabled" and takes export down with it.
    //
    // The accessible state is asserted alongside the DOM property because the
    // DOM property alone stops being able to fail. A button held with
    // `[disabled]` plus `[disabledInteractive]` never sets the `disabled`
    // property at all — it stays focusable and says so through
    // `aria-disabled` — so `.disabled === false` would be green on a control
    // that is unavailable for the whole life of the screen.
    expect(exportButton?.disabled).toBe(false);
    expect(exportButton?.getAttribute('aria-disabled')).not.toBe('true');
    // **Absent, not `'false'`** — and this was `not.toBe('true')`, which is the
    // same assertion with the defect let through. The binding resolves to `null`
    // at rest deliberately, so the attribute is not there at all: `aria-busy`
    // is a claim, and `'false'` asserts on every render that no work is
    // happening on a control nobody has touched. The template says so in a
    // comment on this very button, and nothing held it.
    expect(exportButton?.getAttribute('aria-busy')).toBeNull();
  });

  // A second click during an export is already refused by the service, but the
  // refusal is invisible: the click is swallowed and the screen answers with
  // nothing. These three pin the states that make the guard legible, and they
  // are separate tests because they can regress independently — a control can
  // announce that it is busy while still accepting the press, and one that is
  // marked unavailable can lose focus doing it.
  it('marks the export control busy while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(buttonNamed(host, EXPORT_BUTTON)?.getAttribute('aria-busy')).toBe(
      'true',
    );
  });

  it('marks the export control unavailable while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    // Busy alone says work is happening; it does not say the control will
    // refuse a press. Without this a screen reader announces a button that
    // reads as pressable and answers a press with silence.
    expect(
      buttonNamed(host, EXPORT_BUTTON)?.getAttribute('aria-disabled'),
    ).toBe('true');
  });

  it('keeps the export control focusable while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();
    const exportButton = buttonNamed(host, EXPORT_BUTTON);

    // Assert
    // A button that takes the DOM `disabled` property under the finger drops
    // focus to <body>, so someone who started the export from the keyboard
    // loses their place and has to tab the page from the top to hear the
    // outcome. The control has to stay in the tab order and refuse the press
    // through `aria-disabled` instead — which is what `disabledInteractive`
    // renders, and what the two tests above would otherwise be satisfied by a
    // plain `disabled` binding.
    expect(exportButton?.disabled).toBe(false);
    expect(exportButton?.getAttribute('tabindex')).not.toBe('-1');
  });

  // FR-023.
  it('states the backup window in the erasure section', () => {
    // Act
    const section = sectionFor(host, 'erase-heading');

    // Assert
    // Two controls in one assertion, and both are needed. The *full sentence*
    // is the control against a substring match: `toContain('7')` — or even
    // `toContain('7 days')` — goes green on any page that happens to carry a
    // seven, and on a sentence whose meaning has been reversed around it.
    // *Scoping to the section* is the second half: FR-023 requires the
    // statement to precede the erasure, so a sentence that drifted under
    // "Account" is no longer where the requirement puts it and must go red,
    // which a whole-page `textContent` assertion would not notice.
    expect(normalize(section)).toContain(BACKUP_WINDOW);
  });

  it('shows the account email once it loads', () => {
    // Arrange
    service.email.set('owner@budgetoid.test');

    // Act
    fixture.detectChanges();

    // Assert
    expect(emailValue(host)).toBe('owner@budgetoid.test');
  });

  it('shows nothing where the email would be before it loads', () => {
    // Arrange
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above: a hardcoded address in the template, or a
    // placeholder like "you@example.com", passes "shows the email" and never
    // reads the signal at all.
    expect(emailValue(host)).toBe('');
    // The label stays regardless — this test is about the value, not about the
    // row disappearing.
    expect(normalize(sectionFor(host, 'account-heading'))).toContain(
      EMAIL_LABEL,
    );
  });

  // An empty value and a value that failed to arrive look identical on screen
  // and are not the same fact. Without the sentence the row reads as an
  // account with no email address on it.
  it('explains an email it could not load', () => {
    // Arrange
    service.emailFailed.set(true);
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(sectionFor(host, 'account-heading'))).toContain(
      EMAIL_FAILURE,
    );
  });

  // The email arrives after the first paint, and so does the sentence that says
  // it did not. Rendered outside a live region that was already being watched,
  // that sentence is announced to nobody: a screen reader user hears an empty
  // value where their address should be and is never told why.
  it('carries the account outcome region before the email resolves', () => {
    // Act
    const region = sectionFor(host, 'account-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    expect(region).not.toBeNull();
    // Same pairing as the export region: a region that always renders text
    // satisfies presence, and no region at all satisfies emptiness.
    expect(normalize(region ?? null)).toBe('');
  });

  it('announces an email it could not load', () => {
    // Arrange
    service.emailFailed.set(true);
    service.email.set(null);

    // Act
    fixture.detectChanges();
    const region = sectionFor(host, 'account-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    // The sentence has to land *inside* the watched region, not merely
    // somewhere in the section. `explains an email it could not load` above
    // pins the copy and passes wherever the paragraph sits; this pins where it
    // sits and would go red if the region were rendered empty beside it.
    expect(normalize(region ?? null)).toContain(EMAIL_FAILURE);
  });

  it('explains nothing while the email is merely absent', () => {
    // Arrange
    service.emailFailed.set(false);
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above: a template rendering the failure line
    // unconditionally passes "explains an email it could not load" and accuses
    // the network on every first paint, before a request has had time to
    // answer.
    expect(normalize(sectionFor(host, 'account-heading'))).not.toContain(
      EMAIL_FAILURE,
    );
  });

  it('explains a failed export build in place', () => {
    // Arrange
    service.exportFailure.set('failed');

    // Act
    fixture.detectChanges();
    // Scoped to the section the name claims: "in place" means beside the
    // control that failed. Read off the whole page, both of these stay green
    // when the outcome block drifts under Account or to the bottom of the
    // screen, which is the one thing their names promise.
    const text = normalize(sectionFor(host, 'export-heading'));

    // Assert
    // The negative half is no longer this test's control — it is the pin that
    // the session sentence stays gone. `sessionExpiryInterceptor` owns "the
    // session ended" for every request the application makes, and answers it by
    // navigating to `/welcome`, so a screen that also said it would be talking
    // to a reader who is no longer there. Reinstating that arm here is the
    // tempting way to "explain" a 401 on the export, and this line refuses it.
    expect(text).toContain(EXPORT_BUILD_FAILURE);
    expect(text).not.toContain(EXPORT_SESSION_FAILURE);
  });

  // NFR-026.
  it('states what the operator can read', () => {
    // Act
    const section = sectionFor(host, 'readable-heading');

    // Assert
    // Same reasoning as the FR-023 pin: the paragraph is asserted whole
    // because a fragment — `toContain('encrypted')` — survives the softening
    // edit this requirement exists to prevent, and the scope is the section
    // because the admission belongs under its own heading rather than
    // scattered through the page.
    expect(normalize(section)).toContain(OPERATOR_READABLE);
  });

  // The section renders the reader's own calendar day, computed from the stored
  // instant in the reader's zone, and production passes no locale — it asks the
  // runtime for the reader's own, because pinning one would be the `DatePipe`
  // defect (`LOCALE_ID` is provided nowhere, so every reader would get `en-US`)
  // written by hand. The literals below are therefore statements about the
  // runner's locale as much as about the component.
  it('runs in the locale the dates below are written in', () => {
    // Act
    const rendered = credentialRegistrationDate(PASSKEY.createdAtUtc);

    // Assert
    // Not a test of the component. It turns a locale difference on some future
    // machine into one failure that says so, instead of a dozen assertions
    // that look like the section stopped rendering dates. The format itself is
    // pinned by `credential-registration-date.spec.ts`.
    expect(rendered).toBe(PASSKEY_DATE);
  });

  it('offers a section for the ways to sign in', () => {
    // Act
    const section = sectionFor(host, 'credentials-heading');

    // Assert
    expect(section).not.toBeNull();
    expect(
      normalize(section?.querySelector('#credentials-heading') ?? null),
    ).toBe(CREDENTIALS_HEADING);
  });

  it('keeps one top-level heading on the screen', () => {
    // Assert
    // Control for the test above: a section introduced with a second `h1`
    // renders the same words and passes it, while leaving the document with
    // two competing titles for a reader navigating by heading level.
    expect(host.querySelectorAll('h1').length).toBe(1);
    expect(normalize(host.querySelector('h1'))).toBe('Settings');
  });

  it('loads the ways to sign in on initialization', () => {
    // Assert
    // The list is the section's whole content; without the call it renders its
    // loading line forever and every state test below still passes, because
    // each one sets the signals itself.
    expect(service.loadCredentials).toHaveBeenCalledOnce();
  });

  it('does not load them again when the screen re-renders', () => {
    // Act
    fixture.detectChanges();
    fixture.detectChanges();

    // Assert
    // Control for the test above. `toHaveBeenCalledOnce` after a single render
    // is equally satisfied by a load started from a template expression, which
    // re-fires on every change detection pass — a request per keystroke
    // elsewhere on the page, and a list that flickers back to loading.
    expect(service.loadCredentials).toHaveBeenCalledOnce();
  });

  it('lists one row per credential', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // `ul[role="list"] > li` rather than "some elements exist": the explicit
    // role is what keeps Safari announcing "list, 2 items" once the bullets are
    // removed, and without it the rows are read as loose paragraphs.
    expect(credentialRows().length).toBe(2);
  });

  it('names a passkey in words', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // Paired with the provider case below: each is the other's control,
    // because a template printing one constant word satisfies exactly one of
    // them, and a template printing the raw `type` value renders `passkey` and
    // `federated` — the second of which is a word no reader of this screen has
    // any way to connect to the button they signed in with.
    expect(row).toContain(PASSKEY_TYPE);
    expect(row).not.toContain(FEDERATED_TYPE);
  });

  it('names a provider sign-in for the provider', () => {
    // Arrange
    service.credentials.set([FEDERATED]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    expect(row).toContain(FEDERATED_TYPE);
    expect(row).not.toContain(PASSKEY_TYPE);
  });

  // The kind is looked up in a map keyed by the union, inside the same
  // `computed` the date is formatted in — so a kind the map has no entry for
  // reads `undefined`, the next property access throws, and the throw happens
  // during change detection. Angular caches the failure on the signal and
  // rethrows it on every subsequent read, so this is not one bad pass: the list
  // is stuck on "Loading your ways to sign in…" for the rest of the visit and
  // every section declared after it — recovery codes, export, erasure — stops
  // updating with it. A section is killed by a defect in its neighbour.
  //
  // Commit 2b50ec6 made the *date* total for exactly this reason. The kind was
  // left partial.
  it('keeps the whole screen rendering when a kind is not recognised', () => {
    // Arrange
    service.credentials.set([UNKNOWN_KIND, PASSKEY]);
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();

    // Assert
    // The rest of the list survives the unknown entry rather than being
    // replaced by it, and the loading line is gone — the measured symptom was
    // that line staying on screen forever.
    expect(credentialRows().length).toBe(2);
    expect(normalize(credentialRows()[1] ?? null)).toContain(PASSKEY_DATE);
    expect(normalize(credentialsRegion())).toBe('');
    // And the two sections below it still render. The recovery count is the
    // section this defect actually took down — it is declared immediately
    // after the list, so a reader met a blank line where the count belongs and
    // an Export button that answered every press with silence.
    expect(recoveryCount()).toBe(RECOVERY_MANY);
    expect(buttonNamed(host, EXPORT_BUTTON)).not.toBeNull();
  });

  it('survives a kind that names something every object inherits', () => {
    // Arrange
    // Control for the test above, and the one it does not imply. A fallback
    // written over a plain object — `KINDS[type] ?? UNKNOWN` — is green on
    // `sms_one_time_code` and still throws here, because `constructor` is
    // *found* on the prototype chain and the `??` never fires. The lookup has
    // to miss on every string that is not a key, not merely on the ones nobody
    // thought of.
    service.credentials.set([INHERITED_KIND, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(credentialRows().length).toBe(2);
    expect(normalize(credentialRows()[0] ?? null)).toContain(UNKNOWN_KIND_TYPE);
    expect(normalize(credentialsRegion())).toBe('');
  });

  it('lists an unrecognised kind in neutral words rather than dropping it', () => {
    // Arrange
    service.credentials.set([UNKNOWN_KIND, PASSKEY]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // Listed, not skipped. This list is documented as *every* way into the
    // account, and a filter that quietly dropped what it could not name would
    // tell somebody auditing their account that a credential they cannot see
    // does not exist — the worse of the two lies, and the one that leaves them
    // nothing to act on.
    expect(credentialRows().length).toBe(2);
    // In words, and specifically not the wire token: a template falling back to
    // the raw `type` renders `sms_one_time_code`, a schema identifier on a
    // screen — the same defect the recovery-code row is pinned against.
    expect(row).toContain(UNKNOWN_KIND_TYPE);
    expect(row).not.toContain('sms_one_time_code');
    // The date is still the reader's own day, through the one shared formatter:
    // not knowing what a credential is called says nothing about when it
    // arrived, and the day is what tells two rows apart.
    expect(row).toContain(UNKNOWN_KIND_DATE);
    // But not the caption word of a kind we cannot identify. `Registered` and
    // `Generated` are claims about which of two things happened, and this row
    // is exactly the row that does not know.
    expect(row).toContain(ADDED_CAPTION);
    expect(row).not.toContain(REGISTERED_CAPTION);
    expect(row).not.toContain(GENERATED_CAPTION);
    // The sibling proves the negatives are about *this* row rather than about a
    // template that renamed the caption everywhere.
    expect(normalize(credentialRows()[1] ?? null)).toContain(
      REGISTERED_CAPTION,
    );
  });

  it('offers no revoke on a kind it cannot recognise', () => {
    // Arrange
    service.credentials.set([UNKNOWN_KIND, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // Unknown means not revocable, and the direction of that default is the
    // half that cannot be taken back. Revocation is the one act on this screen
    // that is unrecoverable, so a row nobody has decided about carries no
    // control — the same rule the book already applies to a row nothing can
    // ever revoke. The passkey's control is the discriminating half: a template
    // that dropped the button everywhere would pass the first assertion while
    // taking the action off the one row that is meant to get it.
    expect(revokeButtons().length).toBe(1);
    expect(buttonNamed(host, REVOKE_PASSKEY)).not.toBeNull();
  });

  it('states when each credential was registered', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    expect(normalize(rows[0] ?? null)).toContain(FEDERATED_DATE);
    expect(normalize(rows[1] ?? null)).toContain(PASSKEY_DATE);
  });

  it('states the date in words rather than as a stamp', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // Control for the test above: `{{ credential.createdAtUtc }}` renders the
    // stored value straight into the row and passes any assertion that only
    // asks whether a date is present. The machine-readable form belongs in the
    // `datetime` attribute, which the next test reads.
    expect(row).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    expect(row).not.toContain(PASSKEY.createdAtUtc);
  });

  it('carries the stored instant on the date it renders', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const time = credentialRows()[0]?.querySelector('time');

    // Assert
    // The rendered day is the reader's; the attribute is the record. A `<time>`
    // wrapping a value with no `datetime` on it says nothing a plain span
    // would not.
    expect(time?.getAttribute('datetime')).toBe(PASSKEY.createdAtUtc);
    expect(normalize(time ?? null)).toBe(PASSKEY_DATE);
  });

  it('dates two entries by the day the reader had, not the UTC day', () => {
    // Arrange
    // One UTC day — the 11th — but two local days at the pinned zone: 23:00 on
    // the 11th and 01:00 on the 12th.
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-03-11T09:00:00Z' },
      { ...PASSKEY, id: 'b', createdAtUtc: '2026-03-11T11:00:00Z' },
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // An implementation formatting in UTC — `slice(0, 10)`, `getUTCDate`,
    // `toISOString` — renders these two as the same day and merges two
    // registrations the reader made on either side of their own midnight.
    expect(normalize(rows[0] ?? null)).toContain('March 11, 2026');
    expect(normalize(rows[1] ?? null)).toContain('March 12, 2026');
  });

  it('dates two entries from either side of UTC midnight as one day', () => {
    // Arrange
    // The mirror: two UTC days, one local day at the pinned zone — 12:00 and
    // 16:00 on the 12th.
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-03-11T22:00:00Z' },
      { ...PASSKEY, id: 'b', createdAtUtc: '2026-03-12T02:00:00Z' },
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // Without this half the test above is satisfied by any implementation that
    // renders two different strings for two different instants, including one
    // that prints the raw stamp.
    expect(normalize(rows[0] ?? null)).toContain('March 12, 2026');
    expect(normalize(rows[1] ?? null)).toContain('March 12, 2026');
  });

  it('tells two credentials of one type apart', () => {
    // Arrange
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-01-12T08:30:00Z' },
      PASSKEY,
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // Two passkeys carry the same word, so the date is the only thing on the
    // row that distinguishes them — which is why the section shows one at all,
    // and why a row that dropped it would leave a person revoking blind.
    expect(normalize(rows[0] ?? null)).toContain(FEDERATED_DATE);
    expect(normalize(rows[1] ?? null)).toContain(PASSKEY_DATE);
    expect(normalize(rows[0] ?? null)).not.toBe(normalize(rows[1] ?? null));
  });

  // The date is formatted inside a `computed`, and the template reads that
  // computed, so a throw in it is a throw *during change detection*. Angular
  // abandons the pass where it fails: the credentials section is declared before
  // Export, so one unreadable field leaves the list stuck on its loading line
  // and Export silent for the rest of the visit — the button renders, the click
  // is handled, and no status, confirmation or failure sentence ever appears
  // beside it. Reloading does not help, because the same payload arrives.
  it('keeps the whole screen rendering when a date cannot be read', () => {
    // Arrange
    service.credentials.set([UNREADABLE, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // The rest of the section survives the bad entry rather than being replaced
    // by it: both rows are drawn and the readable one still states its date.
    expect(credentialRows().length).toBe(2);
    expect(normalize(credentialRows()[1] ?? null)).toContain(PASSKEY_DATE);
    // The list has arrived, so the loading line is gone — the measured symptom
    // was this line staying on screen forever.
    expect(normalize(credentialsRegion())).toBe('');
    // And the sections declared after this one still render. Asserted through
    // the export control rather than through the heading because that is the
    // half a user notices: a button that answers every press with silence.
    expect(buttonNamed(host, EXPORT_BUTTON)).not.toBeNull();
    expect(normalize(sectionFor(host, 'export-heading'))).toContain(
      EXPORT_BUTTON,
    );
  });

  it('states no date it could not read', () => {
    // Arrange
    // Two entries, the unreadable one first. Every assertion below names the row
    // it is about, and the readable sibling is what makes the negative halves
    // discriminate: on a list of one, `not.toContain('Registered')` and "no
    // `<time>` here" are equally satisfied by a section that stopped rendering
    // the date for every row on the screen.
    service.credentials.set([UNREADABLE, PASSKEY]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();
    const row = rows[0];
    const readableRow = rows[1];

    // Assert
    // Both rows are drawn, so the `?.` below are reading rows that exist rather
    // than passing on an absent one.
    expect(rows.length).toBe(2);
    // Control for the test above: surviving by printing whatever was to hand is
    // not the same as surviving. The row states the type, which the server did
    // send, and states nothing about a day nobody can work out — not the raw
    // stored value, and not the `Invalid Date` a bare `String(new Date(…))`
    // would put there.
    expect(normalize(row ?? null)).toContain(PASSKEY_TYPE);
    expect(normalize(row ?? null)).not.toContain(UNREADABLE.createdAtUtc);
    expect(normalize(row ?? null)).not.toContain('Invalid Date');
    // The caption goes with the date it introduces. `Registered` with nothing
    // after it is a sentence that stops after one word — a screen reader says it
    // exactly like that — and on screen it reads as a date that failed to arrive
    // rather than one the row is deliberately silent about.
    expect(normalize(row ?? null)).not.toContain(REGISTERED_CAPTION);
    // And the element goes with it. `<time>`'s whole contract is a
    // machine-readable instant: an empty one carrying no `datetime` is an
    // element announcing a time it does not have, which is a worse answer than
    // no element. This supersedes the narrower pair it replaces — `datetime`
    // absent and the text empty — because an empty `<time>` satisfied both while
    // still being the thing the rule forbids, and because withholding the
    // attribute is also what keeps the unreadable stored value out of the markup
    // that `shows no identifier for any credential` reads.
    expect(row?.querySelector('time')).toBeNull();
    // The sibling row proves the two negatives above are about *this* row: a
    // template that dropped the caption or the element everywhere would pass
    // them while taking the date off a list whose rows are told apart by nothing
    // else.
    expect(normalize(readableRow ?? null)).toContain(REGISTERED_CAPTION);
    expect(readableRow?.querySelector('time')).not.toBeNull();
  });

  it('names the revoke control without the clause it cannot fill', () => {
    // Arrange
    service.credentials.set([UNREADABLE]);

    // Act
    fixture.detectChanges();

    // Assert
    // The accessible name is composed from the same date, so it fails the same
    // way: `Revoke Passkey, registered ` is a sentence that stops mid-word, and
    // a screen reader reads it exactly as written.
    expect(buttonNamed(host, REVOKE_DATELESS)).not.toBeNull();
    expect(revokeButtons().length).toBe(1);
  });

  it('shows no identifier for any credential', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    // `outerHTML`, not `textContent`. Text is where an id is *least* likely to
    // arrive: the plausible route is an attribute — a `data-testid` someone adds
    // to make a test easier to write, a `title`, an `aria-label` composed from
    // the wrong field — and none of those move the section's text by a
    // character. Reading the markup covers both, and covers the `datetime`
    // attribute and the `track` expression while it is there.
    const section = sectionFor(host, 'credentials-heading')?.outerHTML ?? '';

    // Assert
    // The row shows what the server holds *and* what tells one entry from
    // another; an identifier is neither, and putting one on screen invites it
    // into a screenshot or a support message where it is a handle on the
    // account. The whole section is read, not only the rows, because the id is
    // just as exposed in a heading or a caption.
    expect(section).not.toBe('');
    expect(section).not.toContain(PASSKEY.id);
    expect(section).not.toContain(FEDERATED.id);
  });

  it('says the list is loading before it arrives', () => {
    // Act
    const region = credentialsRegion();

    // Assert
    // The section's first paint *is* this state — nothing has been set — so
    // unlike the account and export regions this one is legitimately occupied
    // here, and the emptiness assertion the other two make at first paint moves
    // to the resolved state below.
    expect(normalize(region)).toContain(CREDENTIALS_LOADING);
    expect(credentialRows().length).toBe(0);
  });

  it('claims nothing about the account while the list is loading', () => {
    // Assert
    // Control for the test above: a template treating "no rows" as "no
    // credentials" tells a user mid-load that nothing can sign them in, on a
    // page they are signed in to.
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_EMPTY,
    );
  });

  it('says nothing once the list has arrived', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const region = credentialsRegion();

    // Assert
    // Both halves, as on the other two regions: presence alone is satisfied by
    // a region that always holds a line — a screen still saying it is loading
    // over a list it has already drawn — and emptiness alone by no region at
    // all, which is a failure sentence announced to nobody.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe('');
  });

  it('explains a list it could not load', () => {
    // Arrange
    service.credentialsFailed.set(true);
    service.credentials.set(null);

    // Act
    fixture.detectChanges();
    const region = normalize(credentialsRegion());

    // Assert
    // Inside the region, not merely inside the section: the sentence arrives
    // after the first paint, and a live region the assistive technology was not
    // already watching announces nothing.
    expect(region).toContain(CREDENTIALS_FAILURE);
    // A failed load is not an account with nothing attached, and it is not
    // still loading. Rendering either would answer a question the screen does
    // not have the answer to.
    expect(region).not.toContain(CREDENTIALS_LOADING);
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_EMPTY,
    );
  });

  it('explains nothing while the list is merely absent', () => {
    // Assert
    // Control for the test above: a failure sentence rendered unconditionally
    // accuses the network on every first paint, before a request has had time
    // to answer.
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_FAILURE,
    );
  });

  it('states plainly when nothing is attached to the account', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // The empty case is content, not an event: it states a fact about the
    // account rather than reporting something that just happened, so it sits
    // outside the live region — which stays present and empty beside it.
    expect(normalize(sectionFor(host, 'credentials-heading'))).toContain(
      CREDENTIALS_EMPTY,
    );
    expect(normalize(credentialsRegion())).toBe('');
    expect(credentialRows().length).toBe(0);
  });

  it('does not call an empty list a loading one', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above, and the reason `null` and `[]` are kept
    // apart all the way from the service: a template testing `credentials()?.
    // length` collapses them and leaves an account with nothing attached
    // waiting on a request that already answered.
    expect(normalize(credentialsRegion())).not.toContain(CREDENTIALS_LOADING);
  });

  it('keeps the registration control inert', () => {
    // Act
    const registerButton = buttonNamed(host, REGISTER_BUTTON);
    const section = sectionFor(host, 'credentials-heading');

    // Assert
    // Present, so the section is honest about what it will eventually do, and
    // disabled, because registering a passkey has to hand the new factor its
    // own wrapped copy of the account's keys and nothing unwraps them yet. The
    // sentence is what makes that state legible; the two tests below hold the
    // parts of it that this assertion cannot — how many times it is said, and
    // where.
    expect(registerButton).not.toBeNull();
    expect(registerButton?.disabled).toBe(true);
    expect(
      normalize(section),
      sentenceMismatch(normalize(section), REGISTRATION_EXPLANATION),
    ).toContain(REGISTRATION_EXPLANATION);
    // The ceremony is no longer the blocker and the sentence may not say it is.
    // This client runs one on `/welcome`, another on `/register`, and a third in
    // the Account keys section of this screen.
    expect(normalize(section)).not.toContain(STALE_CEREMONY_CLAIM);
  });

  it('says the registration explanation once, not once per row', () => {
    // Arrange
    // Two rows, because the mutation this catches is the tempting one: moving
    // the sentence beside each control it explains. With the list empty there
    // is nothing to duplicate it into and the count cannot discriminate.
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const section = normalize(sectionFor(host, 'credentials-heading'));

    // Assert
    // Exactly one, not "at least one". A sentence repeated per row is read
    // once per entry by a screen reader in browse mode and is three paragraphs
    // of the same words on an account with three credentials — which is the
    // reason it sits above the list, and which a `toContain` cannot see.
    // The count alone fails as `expected +0 to be 1`, which names neither the
    // sentence nor the screen. Zero and many are different defects and read
    // differently.
    const said = occurrencesOf(section, REGISTRATION_EXPLANATION);
    expect(
      said,
      said === 0
        ? sentenceMismatch(section, REGISTRATION_EXPLANATION)
        : 'the registration sentence is repeated inside the credential list.',
    ).toBe(1);
  });

  it('says it before the controls it explains', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const section = sectionFor(host, 'credentials-heading');
    const explanation = elementSaying(section, REGISTRATION_EXPLANATION);
    const firstInert = firstInertControl(section);

    // Assert
    // Order is the other half a `toContain` cannot see, and it is load-bearing
    // in both reading orders: someone moving linearly through the section meets
    // the explanation before the dead control rather than after it, and someone
    // who reaches the control first has already been told why it is off. Below
    // the buttons the sentence is an apology; above them it is an instruction.
    //
    // A bare `expected null not to be null` says nothing about which sentence
    // went missing, and the two ways this can be null read differently: the
    // section may not carry the sentence at all, or it may carry it split
    // across elements so no single one *is* it.
    expect(
      explanation,
      sentenceMismatch(normalize(section), REGISTRATION_EXPLANATION) ||
        'no single element on this screen carries the registration sentence.',
    ).not.toBeNull();
    expect(firstInert).not.toBeNull();
    expect(precedes(explanation, firstInert)).toBe(true);
  });

  it('hangs no description on the disabled controls', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const controls = [
      buttonNamed(host, REGISTER_BUTTON),
      // The recovery section's control is the same shape — disabled, with
      // visible prose above it — and the same mistake is available on it.
      buttonNamed(host, GENERATE_BUTTON),
      ...revokeButtons(),
    ];

    // Assert
    // Control for the tests above: the explanation is legible only while it is
    // somewhere every reader meets, and a description hung on the control is
    // not that. A disabled button *is* announced — it stays in the
    // accessibility tree and browse mode reads it, with its
    // `aria-describedby` — but only to someone who arrives at it, and a
    // `title` reaches neither a keyboard nor a touch user at all. The sentence
    // has to be visible prose because it is as much for the sighted reader
    // looking at a dead button as for anyone else.
    for (const control of controls) {
      expect(control?.getAttribute('title')).toBeNull();
      expect(control?.getAttribute('aria-describedby')).toBeNull();
    }
  });

  it('keeps every revoke control inert', () => {
    // Arrange
    // Two passkeys, not a passkey and a provider sign-in: only a revocable row
    // draws a Revoke, so `[FEDERATED, PASSKEY]` leaves this loop one control to
    // walk and "every" stops being a claim about more than one thing.
    service.credentials.set([PASSKEY, PASSKEY_OTHER]);

    // Act
    fixture.detectChanges();
    const buttons = revokeButtons();
    const section = sectionFor(host, 'credentials-heading');

    // Assert
    expect(buttons.length).toBe(2);
    for (const button of buttons) {
      expect(button.disabled).toBe(true);
    }

    // And the section says why, in the erasure section's words with the number
    // changed, because there is one Revoke per row. **The qualifier is the
    // load-bearing part**: without *Budgetoid checks itself* the sentence says
    // this screen asks for no passkey at all, and somebody who has just watched
    // their authenticator answer an Unlock two sections down would be reading
    // that the screen cannot ask for what it asked for a moment ago.
    expect(
      normalize(section),
      sentenceMismatch(normalize(section), REVOCATION_EXPLANATION),
    ).toContain(REVOCATION_EXPLANATION);
  });

  it('holds every control on the screen to the touch target', () => {
    // Arrange
    // One row of every kind the list can render, and *two* of the one kind that
    // draws a control. Both halves matter: the unrevocable kinds are on screen
    // while the count is taken, so a template that started drawing a Revoke on
    // one of them is caught by the number; and the second passkey is what makes
    // the number move at all — with a single revocable row the census lands
    // back on the five it read before this section existed, which is a count
    // that proves nothing.
    service.credentials.set([FEDERATED, RECOVERY_SET, PASSKEY, PASSKEY_OTHER]);

    // Act
    fixture.detectChanges();
    // Named pairs rather than bare elements, so a control that is not on the
    // screen fails as *which* control rather than as `expected undefined to be
    // true`. `buttonNamed` answers `null` for a control the screen does not
    // carry, and a `null` in this list still has a length — the count below
    // therefore cannot catch a missing control, and the loop is what does.
    const controls = [
      [REGISTER_BUTTON, buttonNamed(host, REGISTER_BUTTON)],
      [GENERATE_BUTTON, buttonNamed(host, GENERATE_BUTTON)],
      // The Account keys control, drawn while the account is locked — which is
      // the stub's resting state and every tab's. It is the one live control
      // this census adds and the only one on the screen that is neither an
      // export nor disabled, so a template that forgot the class on it looks
      // right in every screenshot and is under 48px on every phone.
      [UNLOCK_BUTTON, buttonNamed(host, UNLOCK_BUTTON)],
      [EXPORT_BUTTON, buttonNamed(host, EXPORT_BUTTON)],
      [ERASE_BUTTON, buttonNamed(host, ERASE_BUTTON)],
      ...revokeButtons().map(
        (button, index) => [`${REVOKE_BUTTON} ${index + 1}`, button] as const,
      ),
    ] as const;

    // Assert
    // Every control, not only the ones this section added: the minimum is a
    // rule about controls, and a screen that holds six of seven to it has a
    // control someone misses on a phone. The list is built by name so a missing
    // control fails here rather than shrinking the loop to nothing — a census
    // satisfied by "seven controls appeared" is satisfied by any seven.
    expect(controls.length).toBe(7);
    for (const [name, control] of controls) {
      expect(
        control,
        `the settings screen offers no control named "${name}".`,
      ).not.toBeNull();
      expect(
        control?.classList.contains(TOUCH_TARGET_CLASS),
        `the "${name}" control is not held to the touch target.`,
      ).toBe(true);
    }
  });

  it('gives the credential, recovery and unlock controls the outline treatment', () => {
    // Arrange
    // The same arrangement as the touch-target census, for the same two
    // reasons.
    service.credentials.set([FEDERATED, RECOVERY_SET, PASSKEY, PASSKEY_OTHER]);

    // Act
    fixture.detectChanges();
    const controls = [
      [REGISTER_BUTTON, buttonNamed(host, REGISTER_BUTTON)],
      // Outline and specifically not filled, even though generating replaces an
      // existing set and invalidates every code printed from it: the
      // Destructive fill is a promise that a confirmation follows, and there is
      // no confirmation behind this control.
      [GENERATE_BUTTON, buttonNamed(host, GENERATE_BUTTON)],
      // **Outline, and the near miss is worth stating because a reader will
      // propose it**: a Primary *while locked* reads as the obvious move. It is
      // refused twice over. Export is this screen's one main action, and a
      // screen with two is a screen with none; and to anybody not tracking lock
      // state — which is everybody, since nothing on the page changes when it
      // flips — a Primary that comes and goes is just two Primary buttons on one
      // screen. Under both sits the honesty rule: nothing is encrypted, so a
      // Primary here promises a consequence that does not exist.
      [UNLOCK_BUTTON, buttonNamed(host, UNLOCK_BUTTON)],
      ...revokeButtons().map(
        (button, index) => [`${REVOKE_BUTTON} ${index + 1}`, button] as const,
      ),
    ] as const;

    // Assert
    // Outline, and specifically *not* filled. Revoking is destructive, but the
    // destructive treatment is spent on the control that commits the act and
    // there is no confirmation behind this one yet — the book's rule for a
    // destructive action without its confirmation is Outline and disabled,
    // never Destructive. The negative half is not redundant: a control can
    // carry both classes, and it is the filled treatment arriving that makes a
    // dead button read as the section's primary action. Export is deliberately
    // filled and is deliberately not in this list — it keeps the screen's one
    // Primary, which is the whole reason Unlock is in it.
    expect(controls.length).toBe(5);
    for (const [name, control] of controls) {
      expect(
        control,
        `the settings screen offers no control named "${name}".`,
      ).not.toBeNull();
      expect(
        control?.classList.contains(OUTLINE_CLASS),
        `the "${name}" control does not carry the outline treatment.`,
      ).toBe(true);
      expect(
        control?.classList.contains(FILLED_CLASS),
        `the "${name}" control carries the filled treatment.`,
      ).toBe(false);
    }
  });

  it('names each revoke control for its own row', () => {
    // Arrange
    // Two passkeys registered on different days. Two Revokes to tell apart can
    // only be two passkeys now: the provider row draws no control, so the pair
    // this test is about no longer exists in `[FEDERATED, PASSKEY]`, and the
    // rows are told apart by the date because nothing else is on them.
    service.credentials.set([PASSKEY, PASSKEY_OTHER]);

    // Act
    fixture.detectChanges();

    // Assert
    // Two buttons whose accessible name is "Revoke" cannot be told apart by
    // anyone driving the screen by voice or by screen reader, and this is a
    // destructive action — the one place where reaching the wrong control is
    // unrecoverable. `buttonNamed` matches the accessible name, so a shared
    // visible label with no `aria-label` fails here, and so does a name
    // composed from the wrong row.
    expect(buttonNamed(host, REVOKE_PASSKEY)).not.toBeNull();
    expect(buttonNamed(host, REVOKE_PASSKEY_OTHER)).not.toBeNull();
  });

  it('keeps the visible label on every revoke control', () => {
    // Arrange
    // The same pair, for the same reason: with one control on screen, "every"
    // is a claim about one thing.
    service.credentials.set([PASSKEY, PASSKEY_OTHER]);

    // Act
    fixture.detectChanges();
    const buttons = revokeButtons();

    // Assert
    // Control for the test above: an accessible name that does not begin with
    // what is printed on the button — or a button printing the whole composed
    // name — breaks voice control, which matches what it can see. `Revoke` is
    // read off the DOM text here precisely because the other test reads the
    // accessible name, and `revokeButtons` finds a control only when its own
    // text is exactly that word.
    expect(buttons.length).toBe(2);
    expect(buttonNamed(host, REVOKE_PASSKEY)?.getAttribute('aria-label')).toBe(
      REVOKE_PASSKEY,
    );
    expect(
      buttonNamed(host, REVOKE_PASSKEY_OTHER)?.getAttribute('aria-label'),
    ).toBe(REVOKE_PASSKEY_OTHER);
  });

  it('offers no revoke on a provider sign-in', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // A federated credential is replaced by an email change, never removed, so
    // the Revoke that shipped on this row was a control that will never be
    // enabled — the one lie worse than an inert control, because every other
    // disabled button on this screen is a promise that the ceremony lands and
    // it starts working. The passkey's Revoke is the discriminating half: a
    // template that dropped the button from every row passes the first
    // assertion while taking the action off the one row that will get it.
    expect(buttonNamed(host, REVOKE_FEDERATED)).toBeNull();
    expect(revokeButtons().length).toBe(1);
    expect(buttonNamed(host, REVOKE_PASSKEY)).not.toBeNull();
    // The row itself is still listed, and still says what it is: removing the
    // control is not removing the entry.
    expect(normalize(credentialRows()[0] ?? null)).toContain(FEDERATED_TYPE);
    expect(normalize(credentialRows()[0] ?? null)).toContain(FEDERATED_DATE);
  });

  it('lists a recovery-code set among the ways to sign in', () => {
    // Arrange
    service.credentials.set([RECOVERY_SET]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // In words, and specifically not the wire token: a template printing the
    // raw `type` renders `recovery_codes`, which is a schema identifier on a
    // screen. The list is documented as every way into the account and
    // redeeming a code opens a full session, so the row belongs here on the
    // same argument a passkey's does.
    expect(credentialRows().length).toBe(1);
    expect(row).toContain(RECOVERY_SET_TYPE);
    expect(row).not.toContain(RECOVERY_SET.type);
  });

  it('captions a set with the day it was generated, not registered', () => {
    // Arrange
    // Both rows, because the negative half needs a sibling: a template that
    // renamed the caption for every row would pass `not.toContain('Registered')`
    // while telling a passkey it was generated.
    service.credentials.set([RECOVERY_SET, PASSKEY]);

    // Act
    fixture.detectChanges();
    const set = normalize(credentialRows()[0] ?? null);
    const passkey = normalize(credentialRows()[1] ?? null);

    // Assert
    // A set's instant moves every time the set is replaced, so `Registered`
    // would say the wrong thing about it on its second issue.
    expect(set).toContain(GENERATED_CAPTION);
    expect(set).not.toContain(REGISTERED_CAPTION);
    expect(set).toContain(RECOVERY_SET_DATE);
    expect(passkey).toContain(REGISTERED_CAPTION);
    expect(passkey).not.toContain(GENERATED_CAPTION);
  });

  it('dates a set through the same formatter as a passkey', () => {
    // Arrange
    service.credentials.set([RECOVERY_SET]);

    // Act
    fixture.detectChanges();
    const time = credentialRows()[0]?.querySelector('time');

    // Assert
    // The same `<time>` carrying the stored instant, the same reader's-own-day
    // rule. A second date path is a second place for a UTC day to leak back in
    // — and the rendered day here is the reader's, not the stamp's: 05:00Z on
    // the 2nd is the 2nd at UTC+14 only because the offset does not carry it
    // over midnight, which the passkey fixtures prove it otherwise would.
    expect(time?.getAttribute('datetime')).toBe(RECOVERY_SET.createdAtUtc);
    expect(normalize(time ?? null)).toBe(RECOVERY_SET_DATE);
  });

  it('offers no revoke on a recovery-code set', () => {
    // Arrange
    service.credentials.set([RECOVERY_SET, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // A set is replaced by generating again, never revoked: the route behind
    // Revoke is scoped to passkeys by type, so pointing it at a set answers the
    // 404 an unknown id answers. The passkey's control is the discriminating
    // half.
    expect(revokeButtons().length).toBe(1);
    expect(buttonNamed(host, REVOKE_PASSKEY)).not.toBeNull();
  });

  it('offers no revoke control when nothing is attached', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the tests above: a revoke button rendered outside the list —
    // or a row rendered for an empty list — leaves a destructive control on a
    // screen with nothing for it to act on.
    expect(revokeButtons().length).toBe(0);
  });

  it('offers a section for the recovery codes', () => {
    // Act
    const section = sectionFor(host, 'recovery-heading');

    // Assert
    expect(section).not.toBeNull();
    expect(normalize(section?.querySelector('#recovery-heading') ?? null)).toBe(
      RECOVERY_HEADING,
    );
    // `h2` under the screen's one `h1`; no level skipped.
    expect(section?.querySelector('#recovery-heading')?.tagName).toBe('H2');
  });

  it('puts the recovery codes between the sign-in ways and the export', () => {
    // Act
    const credentials = sectionFor(host, 'credentials-heading');
    const recovery = sectionFor(host, 'recovery-heading');
    const exportSection = sectionFor(host, 'export-heading');

    // Assert
    // Export and Erase are a pair — the alternative offered beside the
    // destructive act — and nothing goes between them, which is what makes this
    // an ordering assertion rather than a preference.
    expect(precedes(credentials, recovery)).toBe(true);
    expect(precedes(recovery, exportSection)).toBe(true);
  });

  it('loads the recovery codes on initialization', () => {
    // Assert
    // Without the call the section renders its blank count line forever and
    // every state test below still passes, because each one sets the signals
    // itself.
    expect(service.loadRecoveryCodes).toHaveBeenCalledOnce();
  });

  it('does not load the recovery codes again when the screen re-renders', () => {
    // Act
    fixture.detectChanges();
    fixture.detectChanges();

    // Assert
    // Control for the test above: a load started from a template expression
    // re-fires on every change detection pass.
    expect(service.loadRecoveryCodes).toHaveBeenCalledOnce();
  });

  it('carries the recovery outcome region before anything has happened', () => {
    // Act
    const region = recoveryRegion();

    // Assert
    // Present and empty, both halves — the at-rest row of the book's table, and
    // the same pairing the account and export regions make. A live region
    // created at the moment it gains content is announced by nothing, and a
    // region that always holds a line is a screen reporting an event to
    // somebody who has not caused one.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe('');
    // At rest the count line is blank too, and blank is not zero.
    expect(recoveryCount()).toBe('');
  });

  it('says the recovery codes are loading while the request runs', () => {
    // Arrange
    service.recoveryLoading.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    // At rest and loading are different states and this is the pair that proves
    // it: a template inferring loading from an absent count renders this line
    // at rest as well, and the test above goes red instead.
    expect(normalize(recoveryRegion())).toContain(RECOVERY_LOADING);
    expect(recoveryCount()).toBe('');
  });

  it('stops saying the codes are loading once the count arrives', () => {
    // Arrange
    service.recoveryLoading.set(false);
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(recoveryRegion())).not.toContain(RECOVERY_LOADING);
  });

  it('holds the count line open before the count arrives', () => {
    // Act
    const line = sectionFor(host, 'recovery-heading')?.querySelector(
      '.s-count',
    );

    // Assert
    // Present and empty, both halves. The element is what reserves the line box
    // so the page does not shift when the number lands; a template that
    // rendered the paragraph only once it had something to say passes every
    // copy assertion here and moves the button under the reader's finger.
    expect(line).not.toBeNull();
    expect(normalize(line ?? null)).toBe('');
  });

  it('claims nothing about the count before one arrives', () => {
    // Assert
    // Control for the zero sentence below: a template treating `null` as `0` —
    // `remaining ?? 0`, or a truthiness test — renders it on every first paint
    // and tells somebody who has ten codes that they have none. The whole
    // section is read, not the count line, because the claim is just as false
    // wherever it is made.
    expect(normalize(sectionFor(host, 'recovery-heading'))).not.toContain(
      RECOVERY_NONE,
    );
  });

  it('explains recovery codes it could not load', () => {
    // Arrange
    service.recoveryFailed.set(true);
    service.recoveryRemaining.set(null);
    // The real service clears this in a `finalize`, so the two are never both
    // set; asserting from the state the service actually produces keeps this
    // test about the template rather than about a combination nothing reaches.
    service.recoveryLoading.set(false);

    // Act
    fixture.detectChanges();
    const region = normalize(recoveryRegion());

    // Assert
    // Inside the region, which was in the DOM from first paint: a live region
    // created at the moment it gains content is announced by nothing.
    expect(region).toContain(RECOVERY_FAILURE);
    // A load that failed is not still loading, and it is not an account with no
    // codes. Both would answer a question the screen cannot answer.
    expect(region).not.toContain(RECOVERY_LOADING);
    expect(normalize(sectionFor(host, 'recovery-heading'))).not.toContain(
      RECOVERY_NONE,
    );
    expect(recoveryCount()).toBe('');
  });

  it('explains nothing while the count is merely absent', () => {
    // Assert
    // Control for the test above: a failure sentence rendered unconditionally
    // accuses the network on every first paint.
    expect(normalize(sectionFor(host, 'recovery-heading'))).not.toContain(
      RECOVERY_FAILURE,
    );
  });

  it('states an account with no codes left as a fact, not as a shortage', () => {
    // Arrange
    service.recoveryRemaining.set(0);

    // Act
    fixture.detectChanges();

    // Assert
    // `0` covers "never generated" and "all spent" and the client cannot tell
    // them apart, so the sentence drops the word the other two branches carry.
    // "No recovery codes *left*" presupposes a set that once existed and tells
    // somebody who has never generated one that they have spent something.
    expect(recoveryCount()).toBe(RECOVERY_NONE);
    expect(recoveryCount()).not.toContain('left');
  });

  it('does not call a zero count a loading one', () => {
    // Arrange
    service.recoveryRemaining.set(0);

    // Act
    fixture.detectChanges();

    // Assert
    // The other direction of the at-rest tests, and the reason `null` and `0`
    // are held apart the whole way from the API service: a template testing the
    // count for truthiness leaves an account with no codes waiting on a request
    // that already answered.
    expect(normalize(recoveryRegion())).not.toContain(RECOVERY_LOADING);
    // The region now holds the answer rather than nothing, because the count
    // renders inside it. What this test is about is unchanged — the loading
    // line is gone — and the emptiness half moved to the at-rest test, which is
    // the only state where this region is empty.
    expect(normalize(recoveryRegion())).toBe(RECOVERY_NONE);
  });

  it('counts a single remaining code in the singular', () => {
    // Arrange
    service.recoveryRemaining.set(1);

    // Act
    fixture.detectChanges();

    // Assert
    // Paired with the plural below: each is the other's control, because one
    // rendered string cannot satisfy both. `I18nPluralPipe` is not available
    // for this — nothing provides `LOCALE_ID`, so it would silently pin every
    // count to en-US plural rules, the same trap
    // `credential-registration-date.ts` exists to avoid for dates.
    expect(recoveryCount()).toBe(RECOVERY_ONE);
  });

  it('counts several remaining codes in the plural', () => {
    // Arrange
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();

    // Assert
    expect(recoveryCount()).toBe(RECOVERY_MANY);
  });

  it('says only the count in the region once the count has arrived', () => {
    // Arrange
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();
    const region = recoveryRegion();

    // Assert
    // The region is where the answer lands, so once the answer is in it holds
    // the count and nothing else — not the line saying a request is running,
    // and not the sentence saying one failed.
    //
    // This replaces an assertion that the region is *empty* here. That was
    // right while the count rendered outside it and is wrong now: an empty
    // region at this point is a screen that announced a beginning and never an
    // end. Emptiness is still pinned, at rest, which is the one state it
    // belongs to.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe(RECOVERY_MANY);
  });

  // The count renders **inside** the `role="status"` region, and this is a
  // deliberate departure from the design book's "the count renders outside that
  // region" — recorded here because the argument is not obvious and a reader
  // will otherwise "fix" it back.
  //
  // The book's reasoning is right in isolation: a standing fact is not an
  // event, and announcing one is how a screen reader ends up narrating
  // furniture. It is wrong beside a loading line. `ngOnInit` sets loading
  // synchronously, before the component's first update pass, so this region has
  // held "Loading your recovery codes…" since first paint — and a live region
  // that already has text when assistive technology registers it is announced
  // unreliably, while text being *removed* is not announced at all. With the
  // count outside, a screen-reader user present during the request hears that
  // something started and never learns how it ended. Announcing a beginning
  // with no end is worse than either half of the rule.
  //
  // What the reader outside the request loses is nothing: the count is still in
  // normal reading order, in the same place on the page, and someone arriving
  // after the response meets it by reading.
  it('announces the count in the region that said it was loading', () => {
    // Arrange
    service.recoveryLoading.set(true);
    fixture.detectChanges();
    const announcing = recoveryRegion();
    // The region really did say it was loading. Without this the test passes on
    // a screen that never announces a beginning either.
    expect(normalize(announcing)).toContain(RECOVERY_LOADING);

    // Act
    service.recoveryLoading.set(false);
    service.recoveryRemaining.set(5);
    fixture.detectChanges();

    // Assert
    // The *same node*, not merely a region matching the same selector. A count
    // announced from a second region created when the answer arrived is
    // announced by nothing — the node has to have been watched before the text
    // landed, which is the entire reason this screen keeps its regions in the
    // DOM while empty.
    expect(recoveryRegion()).toBe(announcing);
    expect(normalize(announcing)).toBe(RECOVERY_MANY);
  });

  it('keeps the count line itself inside the region', () => {
    // Arrange
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();
    const region = recoveryRegion();
    const line = sectionFor(host, 'recovery-heading')?.querySelector(
      '.s-count',
    );

    // Assert
    // Structure, not text. The test above is satisfied by a second copy of the
    // sentence rendered inside the region beside the original outside it, which
    // is a count said twice to anyone reading the page and a value with two
    // places to go wrong. There is one count line and it lives in the region.
    expect(line).not.toBeNull();
    expect(region?.contains(line ?? null)).toBe(true);
    expect(
      sectionFor(host, 'recovery-heading')?.querySelectorAll('.s-count').length,
    ).toBe(1);
  });

  // Renamed to what it can actually pin.
  //
  // It was called `shows no code, hash or identifier in the recovery section`,
  // and two thirds of that title had no assertion under it and could not have
  // one: **no client signal holds a code, a verifier or a hash**, and none ever
  // will — the count is the only thing `GET /api/me/recovery-codes` returns and
  // the derivation that produces a verifier is called by nothing on this
  // screen. A negative over values that do not exist in this process is
  // vacuously true, and it reads as coverage of the rule that matters most.
  // That rule is the server's, and it is checked where the payload is built:
  // `RemainingCount_NeverCarriesAVerifierAHashOrAnId` in
  // `BudgetoidApp/tests/IntegrationTests/RecoveryCodeCountEndpointTests.cs`.
  //
  // What remains is a real client rule and is now stated so it can fail: the
  // generation day belongs to the set's row in Ways to sign in and is not
  // copied into this section, where it would be a second thing to keep in step.
  // The old form could not fail either — with both `Arrange` lines deleted the
  // whole suite stayed green, because a section that renders no credential data
  // satisfies every "does not contain" about a credential. The positive halves
  // below are what tie the negatives to a screen that is actually showing the
  // set: they say *not here*, rather than *nowhere, because nothing was
  // loaded*.
  it('keeps the set’s day on its row and out of the recovery section', () => {
    // Arrange
    service.recoveryRemaining.set(5);
    service.credentials.set([RECOVERY_SET]);

    // Act
    fixture.detectChanges();
    // `outerHTML`, not `textContent`: the plausible route for an identifier is
    // an attribute, which moves the section's text by not one character.
    const section = sectionFor(host, 'recovery-heading')?.outerHTML ?? '';
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // The fixture really is on the screen: the set has a row, that row carries
    // the day, and the recovery section is rendering its count. Without these
    // three the negatives below hold on an empty page.
    expect(row).toContain(RECOVERY_SET_TYPE);
    expect(row).toContain(RECOVERY_SET_DATE);
    expect(normalize(sectionFor(host, 'recovery-heading'))).toContain(
      RECOVERY_MANY,
    );
    // And the count is the whole of what the section says about the set.
    expect(section).not.toContain(RECOVERY_SET_DATE);
    expect(section).not.toContain(RECOVERY_SET.id);
  });

  it('keeps the generate control inert', () => {
    // Act
    const generate = buttonNamed(host, GENERATE_BUTTON);
    const section = sectionFor(host, 'recovery-heading');

    // Assert
    // Present, so the section is honest about what it will eventually do, and
    // plainly disabled — not `disabledInteractive`, whose carve-out is for a
    // busy control that comes back within the second, not for one unavailable
    // for the whole life of the screen.
    expect(generate).not.toBeNull();
    expect(generate?.disabled).toBe(true);
    // Generating a set is ten factors at once — each code derives its own
    // key-encryption key — so it waits on the account's keys as bytes exactly as
    // registering a passkey does. **And on one thing more**, which is why this
    // section stopped sharing the credential list's sentence: the route is gated
    // on an assertion the server verifies, and the unlock ceremony's is minted
    // in this browser and thrown away. A reader told the two controls wait on
    // the same thing is being told one waits on strictly more than it does.
    expect(
      normalize(section),
      sentenceMismatch(normalize(section), GENERATION_EXPLANATION),
    ).toContain(GENERATION_EXPLANATION);
    expect(normalize(section)).not.toContain(STALE_CEREMONY_CLAIM);
  });

  it('says why the generate control is off before offering it', () => {
    // Act
    const section = sectionFor(host, 'recovery-heading');
    const explanation = elementSaying(section, GENERATION_EXPLANATION);
    const generate = buttonNamed(host, GENERATE_BUTTON);

    // Assert
    // Below the button the sentence is an apology; above it, an instruction.
    expect(
      explanation,
      sentenceMismatch(normalize(section), GENERATION_EXPLANATION) ||
        'no single element on this screen carries the recovery-codes sentence.',
    ).not.toBeNull();
    expect(precedes(explanation, generate)).toBe(true);
    // And as visible prose, never hung on the control: a disabled button is out
    // of the tab order, so a title or aria-describedby on it is read to nobody.
    expect(generate?.getAttribute('title')).toBeNull();
    expect(generate?.getAttribute('aria-describedby')).toBeNull();
  });

  it('puts no count in the generate control', () => {
    // Arrange
    service.recoveryRemaining.set(5);

    // Act
    fixture.detectChanges();
    const generate = buttonNamed(host, GENERATE_BUTTON);

    // Assert
    // It is the only Generate on the screen, so there is nothing to tell it
    // apart from and no composed accessible name is needed. A label carrying
    // the count is a second place the number has to stay right.
    expect(generate?.getAttribute('aria-label')).toBeNull();
    expect(normalize(generate)).toBe(GENERATE_BUTTON);
  });

  // The Account keys section: the one place on this screen that asks the
  // person's own device for anything, and the only exit from a locked account
  // that does not go through Sign out.
  //
  // **A locked account is a browser holding no content key, and every tab starts
  // in one** — nothing about the keys survives a page load. It is not a locked
  // *session*: everybody reading this section is signed in, and what they are
  // missing is a key. No assertion below may be satisfied by a sentence saying
  // otherwise.

  it('puts the account keys between the recovery codes and the export', () => {
    // Act
    const recovery = sectionFor(host, 'recovery-heading');
    const accountKeys = sectionFor(host, ACCOUNT_KEYS_HEADING_ID);
    const exportSection = sectionFor(host, 'export-heading');

    // Assert
    // Present first, and identified by its heading rather than only by the id
    // the ordering assertion reads: `precedes` answers `false` for a section
    // that is not there, which would fail this test with `expected false to be
    // true` and name neither the section nor the reason.
    expect(
      accountKeys,
      'the settings screen carries no account keys section.',
    ).not.toBeNull();
    expect(
      normalize(
        accountKeys?.querySelector(`#${ACCOUNT_KEYS_HEADING_ID}`) ?? null,
      ),
    ).toBe(ACCOUNT_KEYS_HEADING);
    // `h2` under the screen's one `h1`; no level skipped.
    expect(
      accountKeys?.querySelector(`#${ACCOUNT_KEYS_HEADING_ID}`)?.tagName,
    ).toBe('H2');

    // Export and Erase are a pair — the alternative offered beside the
    // destructive act — and nothing goes between them, so everything that is
    // neither arrives above them both. That is what makes this an ordering
    // assertion rather than a preference.
    expect(precedes(recovery, accountKeys)).toBe(true);
    expect(precedes(accountKeys, exportSection)).toBe(true);
  });

  it('carries the account-keys outcome region before anything has happened', () => {
    // Act
    const region = accountKeysRegion();

    // Assert
    // Present and empty, both halves — the at-rest row of the book's table, and
    // the same pairing the account, export and recovery regions make. A live
    // region created at the moment it gains content is announced by nothing, and
    // a region that always holds a line is a screen reporting an event to
    // somebody who has not caused one. Every line this section says lands here:
    // both waits, all eight refusals, and the line saying the keys are held.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe('');
  });

  it('offers the unlock control while the account is locked', () => {
    // Act
    // `locked` is the stub's resting state because it is every tab's.
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);

    // Assert
    // Live, and both readings of it. Every other control in this half of the
    // screen is off and explains itself; this one waits on nothing, so it
    // carries no sentence beside it and must not be held inert by the pattern
    // its neighbours use. The `aria-disabled` half is not redundant: a button
    // held with `[disabled]` plus `[disabledInteractive]` — the Export control's
    // own treatment — never sets the DOM `disabled` property at all, so
    // `.disabled === false` is green on a control nothing can press.
    expect(
      unlockButton,
      `the settings screen offers no control named "${UNLOCK_BUTTON}".`,
    ).not.toBeNull();
    expect(unlockButton?.disabled).toBe(false);
    expect(unlockButton?.getAttribute('aria-disabled')).not.toBe('true');
    // **Absent, not `'false'`.** The chapter states it for this control in as
    // many words — `aria-busy` resolves to `null` at rest rather than to
    // `'false'`, so the attribute is absent instead of asserting that no work is
    // happening — and the three busy tests below are all satisfied by a binding
    // that writes `'false'` here. `toBeNull` rather than `not.toBe('true')`,
    // which is the assertion that let this through on the export control.
    expect(unlockButton?.getAttribute('aria-busy')).toBeNull();
    // No composed accessible name: it is the only Unlock on the screen, so
    // there is nothing to tell it apart from. The credential list composes its
    // Revoke names precisely because there is one per row.
    expect(unlockButton?.getAttribute('aria-label')).toBeNull();
    expect(normalize(unlockButton)).toBe(UNLOCK_BUTTON);
  });

  it('unlocks when the unlock control is activated', () => {
    // Arrange
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);
    expect(
      unlockButton,
      `the settings screen offers no control named "${UNLOCK_BUTTON}".`,
    ).not.toBeNull();

    // Act
    unlockButton?.click();

    // Assert
    // Without this the button is furniture: a control rendered with the right
    // label, the right treatment and the right target, wired to nothing, passes
    // every other assertion in this section.
    expect(unlock.unlock).toHaveBeenCalledOnce();
  });

  it('does not unlock on render', () => {
    // Assert
    // Control for the test above, and the same shape as `does not export on
    // render`. What it catches is worse here than an unwanted request: `unlock`
    // reached from a `computed` or an `effect` raises the platform's own passkey
    // sheet over the screen, so merely navigating to Settings would ask the
    // person for their authenticator.
    expect(unlock.unlock).not.toHaveBeenCalled();
  });

  it('says it is waiting for the passkey while the ceremony runs', () => {
    // Arrange
    // The flow is running and custody has been handed nothing yet, which is the
    // whole of the first in-flight moment: the platform's own sheet is up and
    // the person is being asked for an object.
    //
    // Both readings, because that is what the flow publishes here: `busy` is the
    // ceremony's own half, and `working` is the coarser "either half is running"
    // the screen draws its busy treatment from.
    unlock.busy.set(true);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const said = normalize(accountKeysRegion());

    // Assert
    expect(
      said,
      sentenceMismatch(said, UNLOCK_WAITING) ||
        'the account keys section says nothing while the ceremony runs.',
    ).toContain(UNLOCK_WAITING);
    // And not the other one. **Two sentences and not one flag**, because a
    // person can act on the difference — here the thing to do is touch a sensor,
    // there it is to wait — so a section that said the read was running would be
    // telling somebody staring at a system sheet to do nothing about it.
    expect(said).not.toContain(UNLOCK_OPENING);
  });

  it('says the account is being opened while the read runs', () => {
    // Arrange
    // Set **after** the first render, which `beforeEach` has already done. A
    // template that read the status once — off a plain field, or off a signal
    // dereferenced in the component's constructor — renders the resting state
    // here and never comes back, and a stub seeded before the first
    // `detectChanges` would hide exactly that.
    custody.status.set('unlocking');
    // What the flow publishes in this moment: the ceremony is over and the read
    // custody started is the half still running.
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const said = normalize(accountKeysRegion());

    // Assert
    // The second of the section's two in-flight lines. `unlocking` is checked
    // before the flow's own busy flag and the order is the rule: the flow hands
    // custody the key *before* it clears `busy`, so the two overlap on purpose,
    // and once the key has been handed over the truer sentence is about the read
    // that is running now rather than about the device that has already
    // answered.
    expect(said).toContain(UNLOCK_OPENING);
    // And not the line for the state after this one, which would tell somebody
    // watching a request that it had already finished.
    expect(said).not.toContain(UNLOCK_HELD);
  });

  it('says the read is running, not that it still waits, once the key is handed over', () => {
    // Arrange
    // **Both in-flight states are true at once, and that is not a contrived
    // combination — it is the ordinary one.** The flow hands custody the key
    // *before* it clears `busy`, deliberately, so that no frame exists in which
    // both are false and the section flashes back to its resting state with a
    // second press available. The overlap is the price of that, and something
    // has to break the tie.
    unlock.busy.set(true);
    custody.status.set('unlocking');
    // True across both halves, and here they are both true at once.
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const said = normalize(accountKeysRegion());

    // Assert
    // **`unlocking` is checked before the flow's own busy flag, and it is not
    // arbitrary which way.** Once the key has been handed over the ceremony is
    // finished, so the truer sentence is about the read that is running now
    // rather than about the device that has already answered. Read the other way
    // round, somebody watching a network request is told their passkey is still
    // being waited on — and the thing that sentence tells them to do, touch a
    // sensor, cannot help, because nothing is asking them for anything.
    //
    // A template that tests `busy` first passes every other assertion in this
    // section: both sentences exist, both render on their own, and only this
    // combination tells the two orders apart.
    expect(
      said,
      'the account keys section reports the ceremony while the read is what is running.',
    ).toContain(UNLOCK_OPENING);
    expect(
      said,
      'the account keys section answers one moment with two sentences.',
    ).not.toContain(UNLOCK_WAITING);
  });

  it('says the account is open, and offers nothing to press', () => {
    // Arrange
    custody.status.set('unlocked');

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(accountKeysRegion())).toContain(UNLOCK_HELD);
    // **A press on an already-unlocked account can only make things worse, so
    // it is never offered.** Custody drops both keys the instant `unlock`
    // starts, so a press made on an open account and then refused at any point
    // after the key was handed over leaves the account locked, having gained
    // nothing. A control whose best outcome is no change and whose ordinary
    // failure is a loss is not a control.
    expect(
      buttonNamed(host, UNLOCK_BUTTON),
      'the settings screen offers an unlock control on an account that is already open.',
    ).toBeNull();
  });

  it('says five different things for the five ways the device can fail', () => {
    // Arrange
    // A `Record` over the union rather than a list of pairs, so a sixth word
    // added to `UnlockCeremonyFailure` fails to compile here instead of arriving
    // on a screen with no sentence behind it.
    const sentences: Record<UnlockCeremonyFailure, string> = {
      unsupported: UNLOCK_UNSUPPORTED,
      cancelled: UNLOCK_CANCELLED,
      'no-prf': UNLOCK_NO_PRF,
      'ceremony-failed': UNLOCK_CEREMONY_FAILED,
      unknown: UNLOCK_UNKNOWN,
    };
    const failures = [
      'unsupported',
      'cancelled',
      'no-prf',
      'ceremony-failed',
      'unknown',
    ] as const satisfies readonly UnlockCeremonyFailure[];

    // The guard that makes the render assertions able to fail, and it comes
    // first. **These five exist because they are five different next steps for a
    // person** — open a different browser, try again when ready, go and get the
    // device that holds the passkey, run the ceremony again, run the whole thing
    // again — and a spec that let two of them be the same string would have
    // stopped holding the only rule they have. Compared as substrings in
    // **both** directions rather than for inequality: a sentence that contains
    // another is the same collapse with a few extra words on the end, and it
    // would satisfy every `toContain` below.
    //
    // Three of them end in *Nothing has changed*, which is exactly why the
    // comparison is over whole sentences and not over that clause.
    for (const failure of failures) {
      for (const other of failures) {
        if (failure === other) {
          continue;
        }

        expect(
          sentences[failure],
          `the ${failure} sentence and the ${other} sentence say the same thing.`,
        ).not.toContain(sentences[other]);
      }
    }

    // Act & Assert
    for (const failure of failures) {
      unlock.failure.set(failure);
      fixture.detectChanges();

      const said = normalize(accountKeysRegion());

      expect(
        said,
        sentenceMismatch(said, sentences[failure]) ||
          `the account keys section says nothing for a ${failure} ceremony.`,
      ).toContain(sentences[failure]);

      for (const other of failures) {
        if (other === failure) {
          continue;
        }

        expect(
          said,
          `a ${failure} ceremony makes the section say the ${other} sentence too.`,
        ).not.toContain(sentences[other]);
      }

      // And the control never leaves and never changes its name, however the
      // ceremony was refused. This section has nowhere else to send anybody: its
      // one control is the way out of the state the section exists for, so
      // removing it would leave an account locked with nothing on screen to
      // change that. `unsupported` is the one real dead end and its *sentence*
      // carries the way out — a different browser — rather than the control
      // doing it.
      expect(
        buttonNamed(host, UNLOCK_BUTTON),
        `the unlock control left the screen after a ${failure} ceremony.`,
      ).not.toBeNull();
    }
  });

  it('says three different things for the three ways a factor can fail to open', () => {
    // Arrange
    // A `Record` over the union rather than a list of pairs, so a fourth word
    // added to `UnlockFailure` fails to compile here instead of arriving on a
    // screen with no sentence behind it.
    const sentences: Record<UnlockFailure, string> = {
      unopened: CUSTODY_UNOPENED,
      unreachable: CUSTODY_UNREACHABLE,
      unauthenticated: CUSTODY_UNAUTHENTICATED,
    };
    const failures = [
      'unopened',
      'unreachable',
      'unauthenticated',
    ] as const satisfies readonly UnlockFailure[];

    // The guard that makes the render assertions able to fail, and it comes
    // first. **They are three because a person's next move is three different
    // things** — present another factor, press again in a minute, sign in again
    // — and collapsing any two sends somebody down a road that cannot help them.
    // Compared as substrings in **both** directions rather than for inequality:
    // one sentence that contains another is the same defect with two extra words
    // on the end, and it would satisfy every `toContain` below.
    for (const failure of failures) {
      for (const other of failures) {
        if (failure === other) {
          continue;
        }

        expect(
          sentences[failure],
          `the ${failure} sentence and the ${other} sentence say the same thing.`,
        ).not.toContain(sentences[other]);
      }
    }

    // Act & Assert
    for (const failure of failures) {
      custody.unlockFailure.set(failure);
      fixture.detectChanges();

      const said = normalize(accountKeysRegion());

      expect(
        said,
        sentenceMismatch(said, sentences[failure]) ||
          `the account keys section says nothing for a ${failure} read.`,
      ).toContain(sentences[failure]);

      for (const other of failures) {
        if (other === failure) {
          continue;
        }

        expect(
          said,
          `a ${failure} read makes the section say the ${other} sentence too.`,
        ).not.toContain(sentences[other]);
      }
    }
  });

  it('says one thing when a cancelled attempt follows one that opened nothing', () => {
    // Arrange
    // Both readings are live at once, and the state that produces it is ordinary
    // rather than contrived: press one is answered `unopened` — the envelopes
    // were read and none opened — then press two is cancelled at the system
    // sheet, which never reaches custody, so custody's answer from the previous
    // press is still standing.
    custody.unlockFailure.set('unopened');
    unlock.failure.set('cancelled');

    // Act
    fixture.detectChanges();
    const said = normalize(accountKeysRegion());

    // Assert
    // **The flow's failure wins, and custody's renders only when the flow
    // reports none.** Rendered together, the section gives two answers to one
    // question and marks neither as the older — and the older one is about a
    // press that has been superseded. This is the only witness of the
    // precedence rule: every other state below reaches one sentence by having
    // only one to choose from.
    expect(said).toContain(UNLOCK_CANCELLED);
    expect(
      said,
      'the account keys section answers one press with two sentences.',
    ).not.toContain(CUSTODY_UNOPENED);
  });

  // **The tail of every successful press, and the most serious hole this section
  // had.** The three tests below the first one hold the *treatment* of a busy
  // control; this one holds *which states count as busy*, and the two are not the
  // same rule.
  //
  // `AccountUnlockService.derive` hands custody the key and only then clears
  // `busy`, so `busy` false beside `custody.status() === 'unlocking'` is the
  // ordinary state of the second half of every press that worked. Gated on
  // `unlocking.busy()` alone the control is live throughout it — and the flow's
  // own guard inspects `busy` and nothing else, so the press is not stopped
  // there either. It raises a second system sheet, and
  // `AccountKeyCustodyService.unlock` drops both keys at the *start* of the
  // second attempt: the first press was finishing the read that would have
  // opened the account, and the second threw the result away. The book's rule is
  // "while **either half** is running".
  it('holds the unlock control once the key is handed over and the ceremony flag clears', () => {
    // Arrange
    // Set explicitly rather than left at the stub's default, because the state
    // this test is about is a *pair*: the flag being false is half of it.
    unlock.busy.set(false);
    custody.status.set('unlocking');
    // And the third reading, which is the one the control is bound to. It is
    // true here for the half that is running — the read — while `busy`, the
    // ceremony's own half, has already fallen. That gap is the window this test
    // is about, and the flow publishing one answer across both halves is what
    // closes it.
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);

    // Assert
    expect(
      unlockButton,
      `the settings screen offers no control named "${UNLOCK_BUTTON}" while the read runs.`,
    ).not.toBeNull();
    expect(
      unlockButton?.getAttribute('aria-disabled'),
      'the unlock control is still pressable while custody is reading the envelopes, so a second press drops the keys the first one was opening.',
    ).toBe('true');
    expect(
      unlockButton?.getAttribute('aria-busy'),
      'the unlock control reports no work while custody is reading the envelopes.',
    ).toBe('true');
  });

  // **"Is either half running" has one owner, and this is the only test in the
  // suite that can tell a screen reading it from a screen recomputing it.**
  //
  // The test above stands in the state the defect was found in and is satisfied
  // by *either* implementation: `custody.status() === 'unlocking'` is true
  // there, so a template assembling the predicate for itself reaches the same
  // answer the flow publishes. That is not a weakness in it — it is the point
  // of the fix that the two agree in every state the flow can reach — and it is
  // exactly why the rule needs a state the flow *cannot* reach to be visible at
  // all. Measured: restore `@let working = keys === 'unlocking' ||
  // unlocking.busy()` to the template while the service still publishes a
  // correct `working`, and every other test in this file stays green.
  //
  // So the arrangement below is deliberately impossible: the flow reports that
  // an attempt is running while its own ceremony flag is down and custody has
  // never been handed anything. Nothing in production produces it, and nothing
  // needs to — what is under test is not a state of the product but **which
  // object the screen asks**. A template that asks reads `true` and holds the
  // control; a template that answers for itself reads `false || false` and
  // draws a live button over an attempt already running, which is the press
  // that raises a second system sheet and throws away the read the first press
  // was about to finish.
  //
  // Both readers of the signal are asserted, because the template has two and
  // they can regress apart: the control's `disabled`/`aria-busy` bindings, and
  // the region's choice of in-flight sentence.
  it('holds the unlock control on the reading the flow publishes, not on one it reassembles', () => {
    // Arrange
    unlock.busy.set(false);
    unlock.failure.set(null);
    custody.status.set('locked');
    custody.unlockFailure.set(null);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);
    const said = normalize(accountKeysRegion());

    // Assert
    expect(
      unlockButton,
      `the settings screen offers no control named "${UNLOCK_BUTTON}".`,
    ).not.toBeNull();
    expect(
      unlockButton?.getAttribute('aria-disabled'),
      'the unlock control is pressable while the flow reports an attempt running, so the screen is deciding "either half is running" for itself instead of reading the one answer the flow publishes — the two definitions that drifted the first time.',
    ).toBe('true');
    expect(
      unlockButton?.getAttribute('aria-busy'),
      'the unlock control reports no work while the flow reports an attempt running.',
    ).toBe('true');
    // The region's half of the same reading. Inside the `locked` arm the only
    // half `working` can be true of is the flow's own, which is why the wait is
    // the sentence here — the template says so at the branch and the chapter
    // says so in the state table.
    expect(
      said,
      sentenceMismatch(said, UNLOCK_WAITING) ||
        'the account keys region says nothing while the flow reports an attempt running, so it is assembling its own answer rather than reading the flow’s.',
    ).toContain(UNLOCK_WAITING);
  });

  // The inverse of the flash-back-to-rest the overlap exists to prevent. A
  // control wrapped in `keys === 'locked'` leaves the document the instant
  // custody publishes `unlocking` and comes back when the read ends — the
  // section's one control blinking out from under the finger that pressed it, and
  // taking the keyboard's place in the document with it. The book holds the
  // control **busy** through `unlocking`; it removes it for exactly one state,
  // and that state is `unlocked`.
  it('keeps the same unlock control on screen while the read runs', () => {
    // Arrange
    // The node itself, not a matching name. A control removed and re-created
    // renders identically and passes every presence assertion in this section.
    const atRest = buttonNamed(host, UNLOCK_BUTTON);
    expect(
      atRest,
      `the settings screen offers no control named "${UNLOCK_BUTTON}".`,
    ).not.toBeNull();

    // Act
    custody.status.set('unlocking');
    unlock.working.set(true);
    fixture.detectChanges();

    // Assert
    expect(
      buttonNamed(host, UNLOCK_BUTTON),
      'the unlock control leaves the screen while custody is reading the envelopes.',
    ).toBe(atRest);
  });

  // The precedence rule, in flight. `says one thing when a cancelled attempt
  // follows one that opened nothing` holds the at-rest pair and is satisfied by
  // any structure that ranks the two failures against each other; it says
  // nothing about a section that ranks them correctly and then renders custody's
  // beside a *wait*.
  //
  // Only this half of the overlap is reachable: custody clears its own failure
  // at the top of `unlock`, so `unlocking` never coexists with an
  // `unlockFailure`. The flow clears its own on the same press, which is what
  // leaves custody's — set by the press before — standing alone beside the line
  // saying this one is still waiting.
  it('answers a press that is still running with the wait alone', () => {
    // Arrange
    // Press one was answered `unopened`: the envelopes were read and none
    // opened. Press two is at the system sheet, so it has reached the ceremony
    // and not custody.
    custody.unlockFailure.set('unopened');
    unlock.busy.set(true);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const said = normalize(accountKeysRegion());

    // Assert
    expect(
      said,
      sentenceMismatch(said, UNLOCK_WAITING) ||
        'the account keys section says nothing while the ceremony runs.',
    ).toContain(UNLOCK_WAITING);
    // The half that fails when the custody block is lifted out of the chain and
    // guarded on the flow's own failure alone. Rendered together, the section
    // asks somebody to touch their sensor and tells them their last attempt
    // opened nothing, in one breath, about two different presses.
    expect(
      said,
      'the account keys section reports the previous press’s failure while the next one is still running.',
    ).not.toContain(CUSTODY_UNOPENED);
  });

  // The Export control's three, for the Unlock control. Separate tests because
  // they regress independently — a control can announce that it is busy while
  // still accepting the press, and one marked unavailable can lose focus doing
  // it — and because a single `disabled` binding satisfies exactly one of the
  // three while breaking the other two.
  it('marks the unlock control busy while the ceremony runs', () => {
    // Arrange
    unlock.busy.set(true);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(buttonNamed(host, UNLOCK_BUTTON)?.getAttribute('aria-busy')).toBe(
      'true',
    );
  });

  it('marks the unlock control unavailable while the ceremony runs', () => {
    // Arrange
    unlock.busy.set(true);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    // Busy alone says work is happening; it does not say the control will refuse
    // a press. Without this a screen reader announces a button that reads as
    // pressable while the platform's own sheet is already up over the page.
    expect(
      buttonNamed(host, UNLOCK_BUTTON)?.getAttribute('aria-disabled'),
    ).toBe('true');
  });

  it('keeps the unlock control focusable while the ceremony runs', () => {
    // Arrange
    unlock.busy.set(true);
    unlock.working.set(true);

    // Act
    fixture.detectChanges();
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);

    // Assert
    // A button that takes the DOM `disabled` property under the finger drops
    // focus to <body>, so somebody who pressed Unlock from the keyboard loses
    // their place in the document at the exact moment the outcome is announced —
    // and the outcome is announced into a region they would then have to tab the
    // page from the top to reach. The control stays in the tab order and refuses
    // the press through `aria-disabled` instead, which is what
    // `disabledInteractive` renders and what the two tests above would otherwise
    // be satisfied by a plain `disabled` binding.
    expect(unlockButton?.disabled).toBe(false);
    expect(unlockButton?.getAttribute('tabindex')).not.toBe('-1');
  });

  it('says plainly that nothing is encrypted yet', () => {
    // Act
    const section = sectionFor(host, ACCOUNT_KEYS_HEADING_ID);
    const said = normalize(section);

    // Assert
    // Both paragraphs, each as one element's own text. Without them the section
    // stops saying that unlocking changes nothing anybody can see, and a reader
    // who has just presented a passkey is left to work out what it did for them —
    // which, on a product where nothing is encrypted, means going to look for a
    // change that is not there. `elementSaying` is the half `toContain` cannot
    // do: a sentence reassembled out of two paragraphs reads as two claims.
    for (const sentence of ACCOUNT_KEYS_HONESTY) {
      expect(said, sentenceMismatch(said, sentence)).toContain(sentence);
      expect(
        elementSaying(section, sentence),
        `no single element in the account keys section carries "${sentence}".`,
      ).not.toBeNull();
    }
  });

  it('reads as the honesty, then the outcome, then the control', () => {
    // Act
    const section = sectionFor(host, ACCOUNT_KEYS_HEADING_ID);
    const keysHeld = elementSaying(section, HONESTY_KEYS_HELD);
    const nothingEncrypted = elementSaying(section, HONESTY_NOTHING_ENCRYPTED);
    const region = accountKeysRegion();
    const unlockButton = buttonNamed(host, UNLOCK_BUTTON);

    // Assert
    // Each element present first, because `precedes` answers `false` for one
    // that is not there and would fail this as `expected false to be true`,
    // naming neither the element nor the reason.
    expect(keysHeld).not.toBeNull();
    expect(nothingEncrypted).not.toBeNull();
    expect(region).not.toBeNull();
    expect(
      unlockButton,
      `the settings screen offers no control named "${UNLOCK_BUTTON}".`,
    ).not.toBeNull();

    // The order the chapter fixes, and it is held by nothing else: every
    // `precedes` assertion on this screen is either between whole sections or
    // between an explanation and the control it explains in another section.
    //
    // **The prose is above the control** because the chapter's accessibility
    // rule says so outright — prose in reading order, above the control it
    // belongs to, never hung on it — and because these two paragraphs are what
    // tell a reader what pressing it will and will not do. **The region is above
    // the control** because the chapter reserves the region's last line box "so
    // nothing below it moves when an answer arrives", which names a fact only if
    // something is below it. Read in any other order the section reports an
    // outcome before the reader has met the act, or explains the act after they
    // have already performed it.
    expect(
      precedes(keysHeld, nothingEncrypted),
      'the account keys section states the gap before it states what the passkey holds.',
    ).toBe(true);
    expect(
      precedes(nothingEncrypted, region),
      'the account keys section puts its outcome region above the prose that explains the act.',
    ).toBe(true);
    expect(
      precedes(region, unlockButton),
      'the account keys section puts its control above the region that answers it.',
    ).toBe(true);
  });

  it('holds the held-keys line open before the keys are held', () => {
    // Arrange
    // The at-rest half, asserted here because the Act below is what makes the
    // *same node* claim mean anything. `announces the count in the region that
    // said it was loading` is arranged the same way for the same reason.
    //
    // The limit, stated rather than glossed: jsdom applies no stylesheet, so this
    // spec cannot measure `min-height: 1lh` and cannot prove the box is reserved.
    // What it can prove is the half that goes missing — that the element exists
    // at rest, is the region's last child, and is the very node the sentence
    // later lands in rather than one created to carry it.
    const region = accountKeysRegion();
    const line = region?.querySelector(`.${HELD_LINE_CLASS}`) ?? null;
    expect(
      line,
      'the account keys region reserves no line for the sentence saying the keys are held.',
    ).not.toBeNull();
    expect(
      normalize(line),
      'the account keys region says the keys are held before they are.',
    ).toBe('');
    expect(
      region?.lastElementChild,
      'the line reserving the region’s last box is not the region’s last child.',
    ).toBe(line);

    // Act
    custody.status.set('unlocked');
    fixture.detectChanges();

    // Assert
    // The same node, still last. A sentence rendered inside a branch of its own
    // passes every copy assertion in this section and moves the page under the
    // reader at the moment the answer lands — and a second element created to
    // hold it is a line the assistive technology was not watching, which is the
    // whole reason this region is in the DOM from first paint.
    expect(
      accountKeysRegion()?.lastElementChild,
      'the sentence saying the keys are held arrived in a node that was not on the screen before it.',
    ).toBe(line);
    expect(normalize(line)).toBe(UNLOCK_HELD);
  });

  // The state table assigns a treatment per row and nothing in this file could
  // read one. Both directions are pinned, and they are two tests because they
  // fail for two different reasons — a refusal drawn as prose has lost a signal,
  // a wait drawn as a failure has gained a false one.
  it('draws all eight refusals in the failure treatment', () => {
    // Arrange
    // `Record`s over the two unions rather than lists of pairs, so a word added
    // to either fails to compile here instead of arriving on the screen in
    // whichever treatment the template happens to give it.
    const ceremony: Record<UnlockCeremonyFailure, string> = {
      unsupported: UNLOCK_UNSUPPORTED,
      cancelled: UNLOCK_CANCELLED,
      'no-prf': UNLOCK_NO_PRF,
      'ceremony-failed': UNLOCK_CEREMONY_FAILED,
      unknown: UNLOCK_UNKNOWN,
    };
    const read: Record<UnlockFailure, string> = {
      unopened: CUSTODY_UNOPENED,
      unreachable: CUSTODY_UNREACHABLE,
      unauthenticated: CUSTODY_UNAUTHENTICATED,
    };
    const ceremonyFailures = [
      'unsupported',
      'cancelled',
      'no-prf',
      'ceremony-failed',
      'unknown',
    ] as const satisfies readonly UnlockCeremonyFailure[];
    const readFailures = [
      'unopened',
      'unreachable',
      'unauthenticated',
    ] as const satisfies readonly UnlockFailure[];

    // Act & Assert
    for (const failure of ceremonyFailures) {
      unlock.failure.set(failure);
      fixture.detectChanges();
      expectTreatment(ceremony[failure], FAILURE_CLASS, PROSE_CLASS);
    }

    // Custody's three render only once the flow reports none — the precedence
    // rule — so the flow's failure is cleared before this half runs.
    unlock.failure.set(null);

    for (const failure of readFailures) {
      custody.unlockFailure.set(failure);
      fixture.detectChanges();
      expectTreatment(read[failure], FAILURE_CLASS, PROSE_CLASS);
    }
  });

  it('draws the two in-flight lines and the held line as prose, not as failures', () => {
    // Arrange
    // Each of the three is reached by the state that produces it, rather than by
    // setting a flag the template does not read.
    //
    // `working` moves with them, because it is what the flow publishes for each
    // — true through both in-flight moments and false once the keys are held.
    const states = [
      [
        UNLOCK_WAITING,
        (): void => {
          unlock.busy.set(true);
          unlock.working.set(true);
        },
      ],
      [
        UNLOCK_OPENING,
        (): void => {
          unlock.busy.set(false);
          unlock.working.set(true);
          custody.status.set('unlocking');
        },
      ],
      [
        UNLOCK_HELD,
        (): void => {
          unlock.working.set(false);
          custody.status.set('unlocked');
        },
      ],
    ] as const;

    // Act & Assert
    // The direction that matters. A refusal that lost `--bud-over` still reads
    // as a refusal, because colour is never the message here; a **wait** that
    // gained it tells somebody whose ceremony is still running, in red, that
    // something went wrong — and the two in-flight lines are precisely the ones
    // whose advice is *keep going*. The held line is in this list on the same
    // argument: the sentence saying the account is open is the one line in the
    // section that reports success.
    for (const [sentence, reach] of states) {
      reach();
      fixture.detectChanges();
      expectTreatment(sentence, PROSE_CLASS, FAILURE_CLASS);
    }
  });

  it('announces the account keys politely and never assertively', () => {
    // Act
    const region = accountKeysRegion();
    const live = region?.getAttribute('aria-live') ?? null;

    // Assert
    // `accountKeysRegion` already selects on `role="status"`, so a region that
    // took `role="alert"` is caught by every assertion in this section going
    // null. **An `aria-live` beside the role is caught by none of them**: the
    // attribute wins over the role, so one word added here makes eleven
    // sentences — five device refusals, three read refusals, two waits and the
    // line saying the keys are held — interrupt whatever the reader is being
    // read, on a section whose whole content is the result of something they
    // asked for. `off` is refused for the opposite reason: it silences the
    // region while leaving every structural assertion green.
    expect(region).not.toBeNull();
    expect(
      live === null || live === 'polite',
      `the account keys region carries aria-live="${live}", which overrides the politeness role="status" would give it.`,
    ).toBe(true);
  });

  // **The layout half of the region, which every other assertion in this file
  // is blind to.**
  //
  // `accountKeysRegion` selects on `[role="status"]`, so the whole section is
  // held by its semantics: the region is found, its politeness is pinned, and
  // all eleven of its sentences are read out of it. Nothing in any of that
  // touches `.s-outcome`, which is what the screen's own stylesheet hangs the
  // region's spacing and its place in the section's grid on. Drop the class and
  // this file stays green from end to end while the section's outcome lands in
  // the wrong place on the page.
  //
  // The limit, stated rather than glossed: jsdom applies no stylesheet, so this
  // cannot measure a gap or a grid row and does not claim to. What it holds is
  // the seam between the two halves — the class is the half that goes missing,
  // and it is the half nothing else here would notice.
  it('carries the account keys outcome in the screen’s outcome region', () => {
    // Act
    const region = accountKeysRegion();

    // Assert
    expect(region).not.toBeNull();
    expect(
      region?.classList.contains(OUTCOME_REGION_CLASS),
      `the account keys region does not carry .${OUTCOME_REGION_CLASS}, so it is announced correctly and laid out as though it were ordinary prose.`,
    ).toBe(true);

    // **One region and no second, which is what stops the assertion above
    // passing on a region that is not the one every other test in this section
    // reads.** `accountKeysRegion` takes the first `[role="status"]` in the
    // section, so a second region added beside it — a wrapper carrying the
    // class, an outcome split in two — would be invisible to it and to every
    // sentence assertion here alike.
    expect(
      sectionFor(host, ACCOUNT_KEYS_HEADING_ID)?.querySelectorAll(
        '[role="status"]',
      ).length,
      'the account keys section carries more than one live region.',
    ).toBe(1);
  });

  // Reads the rows the way the design chapter specifies them, so a list that
  // lost its `role` or its `<li>` structure stops being found rather than
  // quietly passing every content assertion above.
  function credentialRows(): readonly HTMLElement[] {
    return Array.from(
      sectionFor(host, 'credentials-heading')?.querySelectorAll<HTMLElement>(
        'ul[role="list"] > li',
      ) ?? [],
    );
  }

  // Matched on the *visible* label, unlike `buttonNamed`: these buttons carry
  // an `aria-label` that differs per row, and the point here is to find them
  // all regardless of it.
  function revokeButtons(): readonly HTMLButtonElement[] {
    const buttons =
      sectionFor(
        host,
        'credentials-heading',
      )?.querySelectorAll<HTMLButtonElement>('button') ?? [];

    return Array.from(buttons).filter(
      (button) => normalize(button) === REVOKE_BUTTON,
    );
  }

  function credentialsRegion(): Element | null {
    return (
      sectionFor(host, 'credentials-heading')?.querySelector(
        '[role="status"]',
      ) ?? null
    );
  }

  function recoveryRegion(): Element | null {
    return (
      sectionFor(host, 'recovery-heading')?.querySelector('[role="status"]') ??
      null
    );
  }

  // The section's one region, which carries every line it says — both waits, all
  // eight refusals and the line saying the keys are held. `status` and never
  // `alert`: the person asked for this, and assertive is reserved for a failure
  // to save something they typed.
  function accountKeysRegion(): Element | null {
    return (
      sectionFor(host, ACCOUNT_KEYS_HEADING_ID)?.querySelector(
        '[role="status"]',
      ) ?? null
    );
  }

  // The treatment one of the section's eleven lines is drawn in, asserted in
  // both directions at once.
  //
  // The element is found by its own text being the sentence, so a line split
  // across two elements — or one wrapped in a `<span>` carrying the class while
  // the paragraph carries the other — is not found and fails here rather than
  // passing on the wrapper's classes. The negative half is not redundant: an
  // element can carry both classes, and it is the failure colour *arriving* that
  // turns a wait into a refusal.
  function expectTreatment(
    sentence: string,
    expected: string,
    refused: string,
  ): void {
    const line = elementSaying(accountKeysRegion(), sentence);

    expect(
      line,
      sentenceMismatch(normalize(accountKeysRegion()), sentence) ||
        `no single element in the account keys region carries "${sentence}".`,
    ).not.toBeNull();
    expect(
      line?.classList.contains(expected),
      `"${sentence}" is not drawn in the ${expected} treatment.`,
    ).toBe(true);
    expect(
      line?.classList.contains(refused),
      `"${sentence}" is drawn in the ${refused} treatment.`,
    ).toBe(false);
  }

  // The count line, read on its own rather than through the section's whole
  // text: the section also carries the explanation and the button, and an
  // assertion over all of it cannot tell a blank count from a missing one.
  function recoveryCount(): string {
    return normalize(
      sectionFor(host, 'recovery-heading')?.querySelector('.s-count') ?? null,
    );
  }
});

// Sign-out is the one control on this screen that does something, so this block
// drives a **real** `HttpClient` over the testing backend, the **real**
// `SessionService` and the **real** `Router` rather than stubbing the flow. The
// three tests below are each about the seam between two of those — the request
// that goes out, the order two of them are updated in, and what happens when the
// first never answers — and a stub in the middle would be the thing under test
// answering the question about itself.
//
// `SettingsService` is deliberately not replaced either: the screen provides it,
// and the block above shows what a module-level provider costs. The three reads
// `ngOnInit` starts are answered in `beforeEach` so nothing is mid-flight when
// the sign-out request is matched.
describe('SettingsComponent signing out', () => {
  let http: HttpTestingController;
  let session: SessionService;
  let fixture: ComponentFixture<SettingsComponent>;
  let host: HTMLElement;
  // Where the router was asked to go, and what the session was saying **at that
  // instant**. Recorded as a pair rather than read afterwards, and that is the
  // whole mechanism: read afterwards, both statements have already run
  // whichever order they are in, and the recording cannot tell the two orders
  // apart. `welcome.component.spec.ts` records the same pair at the same instant
  // for the same reason, and the reason it had to is that two other screens
  // replaced the router with a recorder that made the ordering unobservable.
  let asked: { readonly url: string; readonly status: SessionStatus }[];

  beforeEach(async () => {
    asked = [];

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        CONFIGURATION_STUB,
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);

    const router = TestBed.inject(Router);

    // The real router with its one outward call recorded rather than run: a
    // router with no declared routes rejects `/welcome` into a promise nothing
    // awaits, and the rejection surfaces as an unrelated failure two tests
    // later. `navigate` and `routerLink` both land here — each calls
    // `navigateByUrl` on this instance — so the recording says where the screen
    // goes without saying how the author wrote the control.
    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        asked.push({
          url: typeof url === 'string' ? url : router.serializeUrl(url),
          status: session.status(),
        });

        return Promise.resolve(true);
      },
    );

    fixture = TestBed.createComponent(SettingsComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    // The three reads the screen starts on its own, answered so that the only
    // request outstanding below is the one each test is about.
    for (const request of http.match(`${API_ORIGIN}/api/me`)) {
      request.flush({ email: OWNER_EMAIL } satisfies MeDto);
    }
    for (const request of http.match(`${API_ORIGIN}/api/me/credentials`)) {
      request.flush([PASSKEY]);
    }
    for (const request of http.match(`${API_ORIGIN}/api/me/recovery-codes`)) {
      request.flush({ remaining: 3 });
    }

    fixture.detectChanges();
  });

  it('signs out when the sign-out control is activated', () => {
    // Arrange
    const signOut = buttonNamed(host, SIGN_OUT_BUTTON);

    // Assert (the half that is about the control at rest)
    // Enabled, and both readings of it. Every other control on this screen is
    // off and says why; this one is the way out, and a person who cannot leave
    // an account is in a worse position than one who cannot register a second
    // passkey. The `aria-disabled` half is not redundant: a button held with
    // `[disabled]` plus `[disabledInteractive]` — the pattern the Export button
    // on this very screen uses — never sets the DOM `disabled` property at all,
    // so `.disabled === false` is green on a control nothing can press.
    expect(
      signOut,
      `the settings screen offers no control named "${SIGN_OUT_BUTTON}".`,
    ).not.toBeNull();
    expect(signOut?.disabled).toBe(false);
    expect(signOut?.getAttribute('aria-disabled')).not.toBe('true');

    // Act
    signOut?.click();
    fixture.detectChanges();

    // Assert
    // The route `sessions.md` names, and a POST. Asserted as a request over the
    // wire rather than as a call on a stubbed port, so the test says nothing
    // about which service the author puts it behind — and would catch a URL
    // built against the wrong base, which a port stub cannot see.
    const request = http.expectOne(SIGN_OUT_URL);
    expect(request.request.method).toBe('POST');

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('ends the session before it leaves the screen', async () => {
    // Arrange
    // Nothing has asked the server who this is, so the client is holding no
    // reading at all. Stated outright so the assertion at the end is a change
    // rather than something that was already true before the flow ran.
    expect(session.status()).toBe('unknown');

    // Act
    buttonNamed(host, SIGN_OUT_BUTTON)?.click();
    fixture.detectChanges();

    http
      .expectOne(SIGN_OUT_URL)
      .flush(null, { status: 204, statusText: 'No Content' });

    await eventually(() => asked[0] ?? null, 'the navigation to /welcome');
    await settle();

    // Assert
    // One navigation, to `/welcome`, and the session already ended when the
    // router was asked to make it. **The order is the requirement.** Navigate
    // first and the guard on `/welcome` judges it against a stale
    // `'authenticated'`, which sends the person straight back into the app they
    // just left — holding a cookie the server has already revoked, so the next
    // request they make is a 401 and the screen they land on says nothing,
    // because nothing failed.
    expect(
      asked,
      'the settings screen left for /welcome before the session was ended.',
    ).toEqual([{ url: WELCOME_ROUTE, status: 'anonymous' }]);
    expect(session.status()).toBe('anonymous');
  });

  it('signs out even when the server never answers', async () => {
    // Arrange
    expect(session.status()).toBe('unknown');

    // Act
    buttonNamed(host, SIGN_OUT_BUTTON)?.click();
    fixture.detectChanges();

    // Status `0`: nothing reached a server. The request that gets no answer is
    // the honest failure here — a 500 would do as well, and the point is that
    // neither is a statement the client can act on.
    http
      .expectOne(SIGN_OUT_URL)
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown' });

    await eventually(() => asked[0] ?? null, 'the navigation to /welcome');
    await settle();

    // Assert
    // The same outcome as the answered case, and deliberately so. A cookie the
    // server did not clear is `HttpOnly` — this browser cannot read it, cannot
    // clear it, and cannot tell whether it is still live — so there is nothing
    // the client could do differently with the knowledge. What it can do is
    // stop claiming to be signed in, and leave. The alternative is somebody
    // stranded on a signed-in screen, pressing a button that keeps failing,
    // because one request did not land.
    //
    // This is not the same as `SessionService`'s `unreachable` rule, which
    // refuses to read silence as a *refusal*. Here silence is not evidence
    // about the visitor at all: the visitor has already said what they want.
    expect(
      asked,
      'a sign-out whose request failed left the person on the settings screen.',
    ).toEqual([{ url: WELCOME_ROUTE, status: 'anonymous' }]);
    expect(session.status()).toBe('anonymous');
  });

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }
});

// A visit is not the same thing as a page load. The user exports, walks off to
// the transactions screen, and comes back: nothing about that second arrival is
// an export, and the screen must not claim one — neither a confirmation nor a
// failure the previous visit hit.
//
// These run against the **real** SettingsService with only its edges stubbed,
// because the defect is a lifetime mismatch and a stub cannot have one: the
// signal stub above is built fresh in every `beforeEach`, which is exactly the
// bug's absence. The assertions say what the second screen shows, not how the
// state is scoped, so they hold whether the fix narrows the service's lifetime
// to the component or clears the outcome when the screen initializes.
describe('SettingsComponent on a second visit', () => {
  async function configureWith(
    getExport: () => Observable<Blob>,
  ): Promise<void> {
    // Only the edges are replaced — the HTTP calls and the disk write. The
    // service under test is the shipped one.
    //
    // `getCredentials` and `getRecoveryCodes` are in the `Pick` because the
    // screen loads both on init and the real service is the one running here: a
    // stub missing either fails every test in this block with `… is not a
    // function` before a single assertion about the export is reached. The
    // `Pick` is over the real `MeApiService`, so this list is also what stops it
    // from drifting into a shape the service no longer has.
    const api: Pick<
      MeApiService,
      'getMe' | 'getExport' | 'getCredentials' | 'getRecoveryCodes'
    > = {
      getMe: () => of({ email: 'owner@budgetoid.test' }),
      getExport,
      getCredentials: () => of([FEDERATED, PASSKEY]),
      getRecoveryCodes: () => of(3),
    };
    const downloads: Pick<FileDownloadService, 'save'> = { save: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MeApiService, useValue: api },
        { provide: FileDownloadService, useValue: downloads },
      ],
    }).compileComponents();
  }

  // One arrival at the screen. The TestBed is not reset between calls, so
  // anything the application keeps outside the component survives from one to
  // the next — which is the whole point.
  function visit(): ComponentFixture<SettingsComponent> {
    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();

    return fixture;
  }

  function exportSection(fixture: ComponentFixture<SettingsComponent>): string {
    const element = fixture.nativeElement as HTMLElement;

    return normalize(sectionFor(element, 'export-heading'));
  }

  it('does not confirm an export the previous visit finished', async () => {
    // Arrange
    await configureWith(() =>
      of(new Blob(['{"schemaVersion":1}'], { type: 'application/json' })),
    );
    const first = visit();
    buttonNamed(first.nativeElement as HTMLElement, EXPORT_BUTTON)?.click();
    first.detectChanges();
    // The first visit really did confirm. Without this line the test is green
    // on a screen that confirms nothing at all, ever.
    expect(exportSection(first)).toContain(EXPORT_CONFIRMED);
    first.destroy();

    // Act
    const second = visit();

    // Assert
    expect(exportSection(second)).not.toContain(EXPORT_CONFIRMED);
  });

  it('does not explain a failure the previous visit hit', async () => {
    // Arrange
    await configureWith(() =>
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    const first = visit();
    buttonNamed(first.nativeElement as HTMLElement, EXPORT_BUTTON)?.click();
    first.detectChanges();
    // Same control as above, in the failing direction: a screen that never
    // renders the failure sentence would otherwise pass this test by default.
    expect(exportSection(first)).toContain(EXPORT_BUILD_FAILURE);
    first.destroy();

    // Act
    const second = visit();

    // Assert
    expect(exportSection(second)).not.toContain(EXPORT_BUILD_FAILURE);
  });
});

// The same lifetime rule, against the other service this screen provides — and
// the one place in this file that can see the component's `providers` array at
// all. The block above it deliberately provides `AccountUnlockService` at the
// module level, so every test there is answered by that provider whether or not
// the component declares one; nothing there, and nothing in
// `account-unlock.service.spec.ts`, can tell a component-provided flow from a
// root-provided one.
//
// **The scope is the specification here, not one way of reaching an outcome.**
// The flow holds an *attempt*, and an attempt abandoned on a screen should die
// with the screen — which is why it sits on the component beside
// `SettingsService` while `AccountKeyCustodyService`, which holds state of the
// **session**, is root-provided and read from there. Move the flow to the root
// and a refusal from a visit somebody walked away from is on the screen when
// they come back: a sentence about a ceremony they did not run, in a section
// whose only other content is a button.
//
// Written as what the second screen shows rather than as an instance
// comparison, for the reason the export block gives: the assertion then holds
// whether the fix scopes the service to the component or clears the failure when
// the screen initializes.
describe('SettingsComponent unlocking on a second visit', () => {
  it('does not explain an unlock refusal the previous visit hit', async () => {
    // Arrange
    // The real `AccountUnlockService` with only the platform edge replaced —
    // `WebauthnCeremonyService` calls `navigator.credentials`, which does not
    // exist under the test runner. `cancelled` rather than the `unsupported`
    // that a bare jsdom would produce on its own: refusing deliberately is what
    // makes this a test of the flow's lifetime rather than of the runner's
    // capabilities.
    const ceremony: Pick<
      WebauthnCeremonyService,
      'deriveKeyFromLocalAssertion'
    > = {
      deriveKeyFromLocalAssertion: () =>
        Promise.resolve({ failure: 'cancelled', ok: false }),
    };
    const api: MeApiEdges = {
      getCredentials: () => of([PASSKEY]),
      getExport: () => of(new Blob()),
      getMe: () => of({ email: OWNER_EMAIL }),
      getRecoveryCodes: () => of(3),
    };
    const downloads: Pick<FileDownloadService, 'save'> = { save: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MeApiService, useValue: api },
        { provide: FileDownloadService, useValue: downloads },
        { provide: WebauthnCeremonyService, useValue: ceremony },
      ],
    }).compileComponents();

    const first = TestBed.createComponent(SettingsComponent);
    first.detectChanges();
    buttonNamed(first.nativeElement as HTMLElement, UNLOCK_BUTTON)?.click();
    await first.whenStable();
    first.detectChanges();

    // The first visit really was refused. Without this the test is green on a
    // screen that never renders a refusal at all, which is the shape every
    // negative-only assertion in this file is written against.
    expect(
      normalize(
        sectionFor(first.nativeElement as HTMLElement, ACCOUNT_KEYS_HEADING_ID),
      ),
      'the first visit was not refused, so the assertion below cannot fail.',
    ).toContain(UNLOCK_CANCELLED);
    first.destroy();

    // Act
    // The TestBed is not reset between these two, so anything the application
    // keeps outside the component survives from one to the next — which is the
    // whole point.
    const second = TestBed.createComponent(SettingsComponent);
    second.detectChanges();

    // Assert
    expect(
      normalize(
        sectionFor(
          second.nativeElement as HTMLElement,
          ACCOUNT_KEYS_HEADING_ID,
        ),
      ),
      'the settings screen explains an unlock refusal from a visit the reader walked away from.',
    ).not.toContain(UNLOCK_CANCELLED);
  });
});

// Reproduced in a browser: the first load answered with ten, a second load
// failed, and the section rendered "Couldn't load your recovery codes. Reload
// the page." and "You have 10 recovery codes left." in adjacent lines — the
// reader told the count could not be loaded, and told the count.
//
// The same shape sits on the two reads either side of it, and the three blocks
// below pin all three. A second load is not something the shipped screen offers
// today: `ngOnInit` is the only caller of any of them. That is why these are
// pinned rather than left as review notes — "no two of the six states are
// interchangeable" is a property of a section, not of its current call sites,
// and all three of these sections say *Reload the page* on failure, which is an
// invitation to add exactly the retry control that reaches the combination.
//
// Driven through the **real** service, like the second-visit block above and for
// the same reason: the defect lives in the transition between two loads, and the
// signal stub cannot have one — every state it shows is set by hand, so a
// combination the service reaches on its own is invisible through it.
describe('SettingsComponent when the recovery count is loaded twice', () => {
  it('says only that the count could not be loaded when a reload fails', async () => {
    // Arrange
    const getRecoveryCodes = vi
      .fn(
        (): Observable<number> =>
          throwError(() => new HttpErrorResponse({ status: 500 })),
      )
      .mockReturnValueOnce(of(10));
    const fixture = await visitWithApi({ getRecoveryCodes });
    const element = fixture.nativeElement as HTMLElement;
    const region = sectionFor(element, 'recovery-heading')?.querySelector(
      '[role="status"]',
    );
    // The first load really did put a number on the screen. Without this the
    // assertions below hold on a section that never renders a count at all.
    expect(normalize(region ?? null)).toBe(RECOVERY_TEN);

    // Act
    fixture.debugElement.injector.get(SettingsService).loadRecoveryCodes();
    fixture.detectChanges();

    // Assert
    const said = normalize(region ?? null);
    expect(said).toContain(RECOVERY_FAILURE);
    // The defect itself, read as rendered text rather than off a flag: the two
    // sentences were on screen together, so what has to be absent is the
    // sentence, not a signal that happens to feed it. Stated twice on purpose —
    // the `not.toContain` names the pairing this exists to forbid, and the whole
    // -region equality catches a count that moved somewhere else in the region
    // rather than going away.
    expect(said).not.toContain('recovery codes left');
    expect(said).toBe(RECOVERY_FAILURE);
    // And the count line is blank rather than gone: it holds the line box open,
    // so a failure that removed it would shift the page under the reader.
    const count = sectionFor(element, 'recovery-heading')?.querySelector(
      '.s-count',
    );
    expect(count).not.toBeNull();
    expect(normalize(count ?? null)).toBe('');
  });
});

// The same defect on the account section: an address from an earlier answer
// still under `Email address` while the sentence above it says the address
// could not be loaded.
//
// Worth pinning here rather than only in the service, and worth clearing at all,
// because this value has a way of being *wrong* that a count does not: an email
// change lands, this read refreshes it, and a refresh that fails leaves the
// previous address on screen as the answer to "which address does this account
// hold" — the one question this row exists to answer.
describe('SettingsComponent when the email is loaded twice', () => {
  it('says only that the address could not be loaded when a reload fails', async () => {
    // Arrange
    const getMe = vi
      .fn(
        (): Observable<MeDto> =>
          throwError(() => new HttpErrorResponse({ status: 500 })),
      )
      .mockReturnValueOnce(of({ email: OWNER_EMAIL }));
    const fixture = await visitWithApi({ getMe });
    const host = fixture.nativeElement as HTMLElement;
    // The first load really did put an address on the screen. Without this the
    // assertions below hold on a section that never renders one at all.
    expect(emailValue(host)).toBe(OWNER_EMAIL);

    // Act
    fixture.debugElement.injector.get(SettingsService).loadEmail();
    fixture.detectChanges();

    // Assert
    const said = normalize(sectionFor(host, 'account-heading'));
    expect(said).toContain(EMAIL_FAILURE);
    // The defect itself, read as rendered text: the address and the sentence
    // denying it were on screen together.
    expect(said).not.toContain(OWNER_EMAIL);
    expect(emailValue(host)).toBe('');
    // And the row keeps its label, so the section does not reshape under the
    // reader — it is the value that goes, not the fact that an account has an
    // address.
    expect(said).toContain(EMAIL_LABEL);
  });
});

// And on the credential list: rows from an earlier answer still listed under
// the sentence saying the list could not be loaded.
//
// The one to read twice is the last assertion. The list clears to `null`, never
// to `[]` — "the answer has not arrived" and "nothing is attached to this
// account" are different facts rendered as different sentences, and an empty
// array here would replace a failure the reader can retry with a claim about
// their account that is worse than the defect being fixed.
describe('SettingsComponent when the ways to sign in are loaded twice', () => {
  it('says only that the list could not be loaded when a reload fails', async () => {
    // Arrange
    const getCredentials = vi
      .fn(
        (): Observable<readonly CredentialSummary[]> =>
          throwError(() => new HttpErrorResponse({ status: 500 })),
      )
      .mockReturnValueOnce(of([PASSKEY]));
    const fixture = await visitWithApi({ getCredentials });
    const host = fixture.nativeElement as HTMLElement;
    const section = sectionFor(host, 'credentials-heading');
    // The first load really did put a row on the screen.
    expect(section?.querySelectorAll('.s-credential')).toHaveLength(1);
    expect(normalize(section)).toContain(PASSKEY_TYPE);

    // Act
    fixture.debugElement.injector.get(SettingsService).loadCredentials();
    fixture.detectChanges();

    // Assert
    const said = normalize(section);
    expect(said).toContain(CREDENTIALS_FAILURE);
    // Read as rendered text and as rows, because the two catch different
    // regressions: the text catches a row that survived, the count catches rows
    // emptied of their type but left in the document as blank list items.
    expect(said).not.toContain(PASSKEY_TYPE);
    expect(section?.querySelectorAll('.s-credential')).toHaveLength(0);
    // Not the loading line — a load that gave up is not still running.
    expect(said).not.toContain(CREDENTIALS_LOADING);
    // And never the empty sentence. A list cleared to `[]` renders this, which
    // tells somebody whose request failed that they have no way of signing in
    // — on a page they are signed in to.
    expect(said).not.toContain(CREDENTIALS_EMPTY);
  });
});

// The reads the screen starts on its own, plus the one it starts on a click.
// The `Pick` is over the real `MeApiService`, so this list is what stops a stub
// below from drifting into a shape the service no longer has.
type MeApiEdges = Pick<
  MeApiService,
  'getMe' | 'getExport' | 'getCredentials' | 'getRecoveryCodes'
>;

// One arrival at the screen with only the edges replaced — the HTTP calls and
// the disk write. The service under test is the shipped one, which is the whole
// point for the three blocks that use this: each pins a defect living in the
// transition between two loads, and a hand-set signal stub has no transitions.
//
// Every read is defaulted so a block overrides only the one it is about. That is
// not tidiness: the screen loads all three on init, and a stub missing any of
// them fails with `… is not a function` before a single assertion is reached.
async function visitWithApi(
  overrides: Partial<MeApiEdges>,
): Promise<ComponentFixture<SettingsComponent>> {
  const api: MeApiEdges = {
    getMe: () => of({ email: OWNER_EMAIL }),
    getExport: () => of(new Blob()),
    getCredentials: () => of([PASSKEY]),
    getRecoveryCodes: () => of(3),
    ...overrides,
  };
  const downloads: Pick<FileDownloadService, 'save'> = { save: vi.fn() };

  await TestBed.configureTestingModule({
    imports: [SettingsComponent],
    providers: [
      provideNoopAnimations(),
      { provide: MeApiService, useValue: api },
      { provide: FileDownloadService, useValue: downloads },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(SettingsComponent);
  fixture.detectChanges();

  return fixture;
}

// Waits for a public reading to arrive. The sign-out flow is driven by a `void`
// method over a promise, so there is no promise to await from outside; polling a
// reading claims nothing about how many awaits the implementation contains
// today, and a flow that never arrives fails with a sentence naming what never
// came rather than with a null dereference.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Timed out waiting for ${what}.`);
}

// Collapses the whitespace an HTML template introduces. Without it every
// whole-sentence assertion above is hostage to where Prettier wrapped the
// line: the same sentence split across two source lines yields a newline and
// an indent inside `textContent`, and `toContain` fails on copy that is
// byte-correct.
function normalize(element: Element | null): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

// Finds a button the way a screen reader announces it, so a control renamed in
// the DOM but not in the copy stops being found. `aria-label` wins over the
// text node, matching how the accessible name is computed for the shapes this
// screen uses.
function buttonNamed(
  host: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  const buttons = Array.from(
    host.querySelectorAll<HTMLButtonElement>('button'),
  );

  return (
    buttons.find((button) => {
      const label = button.getAttribute('aria-label');
      return (
        (label ?? button.textContent ?? '').replace(/\s+/g, ' ').trim() === name
      );
    }) ?? null
  );
}

// How many times a sentence appears in already-normalized text. `toContain` is
// satisfied by one occurrence and by fifty, and the difference between those is
// the whole argument for where this screen puts its explanation.
function occurrencesOf(text: string, sentence: string): number {
  return text.split(sentence).length - 1;
}

// Where a pinned sentence and the screen part company, as a message rather than
// as two paragraphs to diff by eye. `toContain` on a sentence this long reports
// the whole expected string beside the whole section text and marks nothing, so
// a one-word rewrite fails as two near-identical blocks and the reader spends
// the next ten minutes finding the word. This walks the longest prefix of the
// pin the section still carries and prints the next few characters of each side
// of it. Empty when the section does carry the sentence, so a caller can fall
// back to a message about the *other* reason its assertion failed.
function sentenceMismatch(actual: string, expected: string): string {
  if (actual.includes(expected)) {
    return '';
  }

  let matched = 0;
  while (
    matched < expected.length &&
    actual.includes(expected.slice(0, matched + 1))
  ) {
    matched += 1;
  }

  const divergence = actual.indexOf(expected.slice(0, matched)) + matched;

  return [
    'the section does not carry the pinned sentence.',
    `agreed up to: …${expected.slice(Math.max(0, matched - 40), matched)}`,
    `pin then says: ${expected.slice(matched, matched + 40)}…`,
    `screen says:   ${actual.slice(divergence, divergence + 40)}…`,
  ].join('\n');
}

// The element whose own text *is* the sentence — the paragraph carrying it,
// rather than every ancestor that contains it. Document order, so a wrapper that
// happened to hold nothing else would be found before its child; that is the
// same position for the comparison below, which is all this is used for.
function elementSaying(root: Element | null, sentence: string): Element | null {
  const elements = Array.from(root?.querySelectorAll('*') ?? []);

  return elements.find((element) => normalize(element) === sentence) ?? null;
}

// The first control in document order that a press will be refused by, however
// the refusal is expressed: `disabled` on the row buttons, `aria-disabled` on
// anything held with `disabledInteractive`.
function firstInertControl(root: Element | null): Element | null {
  return (
    root?.querySelector('button[disabled], button[aria-disabled="true"]') ??
    null
  );
}

// Document order, which is the order a screen reader reads and the order the
// page is laid out in — not source order in the template and not visual order
// under CSS, but the one both of those have to agree with to mean anything.
function precedes(first: Element | null, second: Element | null): boolean {
  if (first === null || second === null) {
    return false;
  }

  return (
    (first.compareDocumentPosition(second) &
      Node.DOCUMENT_POSITION_FOLLOWING) !==
    0
  );
}

function sectionFor(host: HTMLElement, headingId: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(
    `section[aria-labelledby="${headingId}"]`,
  );
}

// The email is rendered as a description list under the Account heading:
// `<dt>Email address</dt><dd>…</dd>`. The value is read through `dd` rather
// than through the section's whole text so that the label alone cannot satisfy
// the assertion.
function emailValue(host: HTMLElement): string {
  return normalize(
    sectionFor(host, 'account-heading')?.querySelector('dd') ?? null,
  );
}
