import { HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import {
  EMAIL_CHANGE_PATH,
  LOCKED_SESSION_PATH,
} from '@app-core/interceptors/api-credentials.interceptor';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import { PROVIDER_CREDENTIAL } from '@app-core/interceptors/provider-credential.token';
import type { FactorKeypairEnvelopes } from '@app-core/security/factor-keypair';
import type { PasskeyAssertionPayload } from '@app-core/security/webauthn-encoding';
import { map, Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface MeDto {
  email: string;
  /**
   * The budget this request is operating inside, in the hyphenated `D` form.
   *
   * **The one identifier this API publishes, and it is earned rather than
   * conceded.** No screen renders it and none is going to; what earns it is that
   * the browser cannot derive it and cannot finish a computation without it. The
   * blind index over a value is
   * `budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized name`,
   * taken under a key this server has never held — so the grammar and the pair
   * are this client's own constants, the text is what somebody typed, the index
   * key is in custody, and the tenancy is resolved server-side from the session
   * cookie and named in no request. Withheld, nothing can be written to a
   * blind-indexed column at all. A user id fails the same test and stays
   * unpublished, which is what makes it a rule rather than an opening;
   * `SignedInUser` on the other side is where the argument is kept in full.
   *
   * It is not a tenancy *parameter*: no route in this API takes a budget as a
   * path segment or a query member, so a client holding this value has nowhere
   * to spend it.
   */
  budgetId: string;
}

/**
 * What `GET /api/me/session` answers: which kind of session this browser
 * holds, when it ends, and the account's scheduled erasure or `null`.
 *
 * **No identifier of any kind, no budget, no address.** It is the one read a
 * locked session can make that tells it what it is — `GET /api/me` answers a
 * locked session `403` — so it carries only what a session of *either* kind may
 * be told about itself.
 *
 * `erasure` is `null` when nothing is scheduled, and the member is always
 * present: a body that never mentioned a schedule has not said that none is on
 * file, and `MeApiService.getSession` refuses it rather than reading it so.
 */
export interface SessionDto {
  readonly kind: 'full' | 'locked';
  readonly expiresAtUtc: string;
  readonly erasure: { readonly takesEffectAtUtc: string } | null;
}

// An instant as this client accepts one off the wire: a calendar date, a time
// of day, up to seven fractional digits — `System.Text.Json` writes a UTC
// `DateTime` with up to seven — and then **a `Z` or a `±hh:mm` offset, never
// neither**. An offset-less spelling is the one this pattern exists to refuse:
// `Date` parses it as the *reader's* local time, so a date rendered from it
// moves by the reader's offset with nothing anywhere saying so.
const INSTANT =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:Z|[+-](\d{2}):(\d{2}))$/;

// Leap years by the Gregorian rule, written out rather than borrowed from
// `Date.UTC`, which maps years 0–99 onto the twentieth century.
function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;

    return leap ? 29 : 28;
  }

  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

// The pattern above checks the spelling; this checks that the spelling names a
// date and a time that exist. `2026-02-30T25:61:00Z` matches the pattern and is
// no instant at all, and `Date.parse` would roll it forward into a real one
// rather than refuse it.
function isInstant(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false;
  }

  const match = INSTANT.exec(value);

  if (match === null) {
    return false;
  }

  const [year, month, day, hour, minute, second] = match
    .slice(1, 7)
    .map(Number);
  // Absent on a `Z` instant, so they read as `0`, which is in range.
  const offsetHour = Number(match[7] ?? 0);
  const offsetMinute = Number(match[8] ?? 0);

  if (
    year === undefined ||
    month === undefined ||
    day === undefined ||
    hour === undefined ||
    minute === undefined ||
    second === undefined
  ) {
    return false;
  }

  return (
    month >= 1 &&
    month <= 12 &&
    day >= 1 &&
    day <= daysInMonth(year, month) &&
    hour <= 23 &&
    minute <= 59 &&
    second <= 59 &&
    offsetHour <= 23 &&
    offsetMinute <= 59
  );
}

// Exactly the members named, in any order, and no others. A member this bundle
// does not know is a server this bundle was not written against, and reading
// the members it does know off that body would publish a fact about the
// session from a body this client cannot vouch for.
function hasExactly(
  body: object,
  members: readonly string[],
): body is Record<string, unknown> {
  const keys = Object.keys(body);

  return (
    keys.length === members.length &&
    members.every((member) => keys.includes(member))
  );
}

function isScheduledErasure(
  erasure: unknown,
): erasure is NonNullable<SessionDto['erasure']> {
  return (
    typeof erasure === 'object' &&
    erasure !== null &&
    !Array.isArray(erasure) &&
    hasExactly(erasure, ['takesEffectAtUtc']) &&
    isInstant(erasure['takesEffectAtUtc'])
  );
}

// The boundary check `isRecoveryCodeCount` argues for, on the body that decides
// who the visitor is. **A plain `Error`, never an `HttpErrorResponse`**: the
// probe reads a 401 or 403 as `anonymous`, and a body it could not read says
// nothing about who is asking — it is `unreachable`'s, whose remedy is a
// reload.
//
// `kind` is matched exactly, case and all. Read as either known kind, an
// unknown one either hands budget screens to a session the server refuses on
// every one of them or sends a full session to the release screen, whose one
// act is erasing the account.
function decodeSession(body: unknown): SessionDto {
  if (
    typeof body !== 'object' ||
    body === null ||
    Array.isArray(body) ||
    !hasExactly(body, ['kind', 'expiresAtUtc', 'erasure'])
  ) {
    throw new Error(
      'The session response did not arrive as a description of a session.',
    );
  }

  const { kind, expiresAtUtc, erasure } = body;

  if (kind !== 'full' && kind !== 'locked') {
    throw new Error('The session response named no kind this client knows.');
  }

  if (!isInstant(expiresAtUtc)) {
    throw new Error('The session response carried no readable expiry.');
  }

  if (erasure !== null && !isScheduledErasure(erasure)) {
    throw new Error(
      'The session response carried a scheduled erasure this client cannot read.',
    );
  }

  return {
    kind,
    expiresAtUtc,
    erasure:
      erasure === null ? null : { takesEffectAtUtc: erasure.takesEffectAtUtc },
  };
}

/**
 * What `POST /api/locked-session` answers: a session description of exactly
 * one kind. The route opens a locked session or refuses; it never opens a full
 * one, so the type says so and a caller has no `'full'` branch to write.
 */
export type LockedSessionDto = SessionDto & { readonly kind: 'locked' };

// `decodeSession`'s rules, then one more: the kind is `locked`. A `full` body
// from this route is a server this bundle was not written against — read as a
// full session, it would hand budget screens to a sign-in that proved nothing
// but a Google account. A plain `Error` for `decodeSession`'s reason: the
// caller tells a refusal of the request from a body it could not read by
// whether it is an `HttpErrorResponse`.
function decodeLockedSession(body: unknown): LockedSessionDto {
  const session = decodeSession(body);

  if (session.kind !== 'locked') {
    throw new Error(
      'The locked sign-in answered a kind of session it never opens.',
    );
  }

  return { ...session, kind: session.kind };
}

// One member, and the route will never grow another: no id, no issued instant,
// no total, and above all no hash. It is unwrapped at this boundary rather than
// carried inward, because the screen renders a number and a one-member envelope
// is a shape only the wire has a use for.
interface RecoveryCodeCountDto {
  remaining: number;
}

// A declared response type is an assertion about JSON, not a check of it.
// Nothing between the socket and the signal validates a body, so a 200 of the
// wrong shape is published as though the server had said it — and the two
// shapes that arrive from a version skew are the two the screen renders worst.
// A missing member puts `undefined` into a signal typed `number | null`, which
// the template's `remaining !== null` branch accepts and interpolates as
// nothing: "You have  recovery codes left." A `null` member is worse, because
// `null` is a value this whole path already means *no answer yet* by, and
// publishing one gives the section a state the design book does not have.
//
// So this is a boundary check and deliberately not a repair. Every consumer of
// these methods already has a `catchError` that publishes nothing and says an
// honest sentence; a refusal lands there. A coercion — `?? 0`, `Number(…)`,
// `[]` for a body that is not a list — lands on the screen as a fact about the
// account, indistinguishable from an answer.
function isRecoveryCodeCount(body: unknown): body is RecoveryCodeCountDto {
  return (
    typeof body === 'object' &&
    body !== null &&
    'remaining' in body &&
    typeof body.remaining === 'number' &&
    // Whole and not negative, because that is what a count of codes is.
    // `Number.isInteger` also refuses `NaN` and both infinities, each of which
    // is a JSON number to a parser and none of which is a number of codes.
    Number.isInteger(body.remaining) &&
    body.remaining >= 0
  );
}

// The three kinds of thing that can sign this account in, and the union is
// closed on purpose: a fourth kind is a change to what the screen must render,
// not a string that arrives one day and falls through a template. `federated`
// is the provider sign-in; the response carries no provider subject and never
// will, so there is nothing here to render but the kind and when it was
// attached.
//
// `recovery_codes` is the schema's own token and is deliberately not
// camel-cased. It is a discriminant *value*, not a property name: the naming
// policy that turns `CreatedAtUtc` into `createdAtUtc` applies to members, and
// `recoveryCodes` is what applying it here would produce — a spelling that
// agrees with nothing on either side of the wire. A set is listed by
// `GET /api/me/credentials` like any other way in, because redeeming a code
// opens a full session.
// What `POST /api/me/email-change` answered: the number of the account's other
// sessions the change ended, or `null` when the body carried no count this
// bundle can read. `null` is not `0` — zero is a count, and the screen's
// clause-free line for it claims something `null`'s does not.
export interface EmailChangeResultDto {
  readonly sessionsEnded: number | null;
}

// A count of sessions: whole and not negative, for `isRecoveryCodeCount`'s
// reason — `Number.isInteger` also refuses `NaN` and both infinities.
function isSessionCount(count: unknown): count is number {
  return typeof count === 'number' && Number.isInteger(count) && count >= 0;
}

export type CredentialKind = 'passkey' | 'federated' | 'recovery_codes';

export interface CredentialSummary {
  id: string;
  type: CredentialKind;
  // The stored instant, as the server wrote it, always carrying the `Z`
  // designator. It stays a string all the way to the `<time datetime>`
  // attribute; the reader's calendar day is computed from it separately by
  // `credential-registration-date.ts`, which is the only place that converts.
  createdAtUtc: string;
}

// One factor's row of `wrapped_account_keys`, as the three members cross the
// wire: the identifier the factor's keypair was bound to, its private half
// wrapped under the key-encryption key that factor derives, and the account's
// two keys encapsulated to its public half. Nothing else — no credential id, no
// user id, no registration instant — and the route is specified never to grow
// one. The manifest and the epoch are **not** here and must never be copied
// down: both are true of the account rather than of a factor, so they live on
// {@link AccountKeyCustodyDto} one level up, and a per-row copy would be eleven
// copies of one value kept consistent by nobody.
//
// **Composed from `FactorKeypairEnvelopes` rather than restating its two
// members**, which is the whole reason this file reaches into `+core/security`
// at all. That interface is what `openFactorKeypair` takes, so an entry read
// here is passed straight to it; two hand-written copies of `wrappedPrivateKey`
// and `encapsulatedAccountKeys` would let one be renamed while the other went on
// compiling against a body it no longer describes. The import is `import type`,
// so nothing of the crypto module reaches the bundle this file already sits in.
//
// An intersection rather than an `interface extends`: there is one member to
// add, and the composition says so without inventing a hierarchy.
export type AccountKeyEntry = FactorKeypairEnvelopes & {
  // The canonical lower-case hyphenated spelling the row was stored in. It **is**
  // the associated data both envelopes were bound with, so it travels beside
  // them and is never derived, normalised or prettified on the way past.
  readonly factorId: string;
};

/**
 * The whole account-key read: the account's manifest and the generation it is
 * in, then one entry per recovery factor.
 *
 * **`manifest` is `string | null` and never `''`.** An empty string is a legal
 * base64url rendering of zero bytes, so spelling an absent manifest that way
 * makes "there is no manifest" indistinguishable from "there is one and it
 * authenticates an empty set" — and the two have different answers. The server
 * skips its encoder rather than feeding it an empty span for exactly that
 * reason, so `''` is a spelling this route does not emit and this client
 * refuses; accepting it would let a later server start emitting it unnoticed.
 *
 * `rotationEpoch` is `0` when there is no manifest row. A stored generation
 * starts at 1 and the column refuses anything lower, so `0` is a number no row
 * can hold — which is what lets one integer say "there is nothing stored"
 * without a second member to disambiguate it.
 *
 * **That last sentence is a refusal and not only a description.** `manifest`
 * and `rotationEpoch` say the same thing twice, so a body in which they
 * disagree — a manifest beside `0`, or `null` beside a stored generation — is
 * refused by {@link MeApiService.getAccountKeys} rather than handed on. Each
 * member is well formed on its own, which is exactly why nothing else catches
 * it, and what it costs downstream is an account declared unopenable.
 */
export interface AccountKeyCustodyDto {
  readonly manifest: string | null;
  readonly rotationEpoch: number;
  readonly factors: readonly AccountKeyEntry[];
}

/**
 * What this boundary throws when the account-key body is not one it can read.
 *
 * **A type rather than a bare `Error`, because one consumer has to tell this
 * refusal apart from every other way the read can fail.**
 * `AccountKeyCustodyService` turns a failed read into a word a person acts on,
 * and a body this client cannot parse is a statement about *this browser's
 * bundle* — the remedy is a reload, not a retry and not another factor. Thrown
 * as an `Error`, it is indistinguishable from a network that blinked and lands
 * on `unreachable`, whose advice is "try again in a minute": a loop that can
 * never succeed, because nothing about the next minute changes which JavaScript
 * this tab is running.
 *
 * It carries no member of its own and never will. What the consumer branches on
 * is the type; what a reader needs is the message, and every throw below writes
 * its own.
 */
export class AccountKeyResponseError extends Error {}

// The account's manifest as the wire is allowed to spell it: bytes, or nothing
// at all.
//
// **`''` is refused, and that refusal is the whole function.** `null` and `''`
// are the two spellings of "no manifest" a reader will treat as equivalent, and
// they are not: an empty string decodes to zero bytes, which is a manifest of
// length zero, which is a value this client would then try to open and would
// blame the account's content key for. The server argues the same distinction
// from its own side and skips its encoder rather than handing back `''` — so
// refusing the spelling it refuses to emit costs nothing today and is the only
// thing that catches a later server that starts emitting it.
//
// It is deliberately **not** a check that the string is base64url, or of any
// width: that is `openFactorManifest`'s rule, and a second, weaker copy of it
// here would be a second definition of what a manifest is.
function isManifestWire(manifest: unknown): manifest is string | null {
  return (
    manifest === null || (typeof manifest === 'string' && manifest.length > 0)
  );
}

// Which generation of the manifest is in force — `0` when there is none.
//
// Whole and not negative, for `isRecoveryCodeCount`'s reason:
// `Number.isInteger` also refuses `NaN` and both infinities, each of which is a
// JSON number to a parser and none of which is a generation. A fractional or
// negative epoch reaches `openFactorManifest` as associated data, where it
// authenticates nothing and fails with a message about a manifest that did not
// open — a refusal three layers away from the member that was wrong.
function isRotationEpoch(epoch: unknown): epoch is number {
  return typeof epoch === 'number' && Number.isInteger(epoch) && epoch >= 0;
}

// The boundary check `isRecoveryCodeCount` argues for, on a body where a
// coercion is even quieter.
//
// Every member is a string that is about to be fed to a decoder and an AEAD
// open. A missing one arrives at `decodeBase64Url` as `undefined`, which throws
// *inside the trial loop* — where a throw already means "this factor is not the
// one, try the next" — so a malformed body would be read as a person presenting
// the wrong factor and answered with "present another factor". That is the
// failure this refusal exists to prevent: not a wrong pixel, but the account
// declared unopenable by its own key custody, with nothing naming the cause. A
// version skew is how it really arrives — a server that renamed a member, or
// added one this bundle does not know, is a bundle problem with a reload as its
// remedy, and it must not be reported as a factor problem with "present another
// one" as its remedy.
//
// So the check is per entry and not merely over the collection, unlike
// `getCredentials` — a credential row is total in what it renders and a
// malformed entry spoils one row, while here an entry has no partial use at all.
// It is deliberately **not** a check of the envelopes' shape: width, version
// byte and alphabet are `decodeBase64Url`'s and `openFactorKeypair`'s rules, and
// a second, weaker copy of them here would be a second definition of what an
// envelope is.
function isAccountKeyEntry(entry: unknown): entry is AccountKeyEntry {
  return (
    typeof entry === 'object' &&
    entry !== null &&
    'factorId' in entry &&
    typeof entry.factorId === 'string' &&
    'wrappedPrivateKey' in entry &&
    typeof entry.wrappedPrivateKey === 'string' &&
    'encapsulatedAccountKeys' in entry &&
    typeof entry.encapsulatedAccountKeys === 'string'
  );
}

@Injectable({ providedIn: 'root' })
export class MeApiService extends BaseApiService {
  // Read by a browser that believes it holds a session — the Settings screen
  // asking for the address to render. **No `EXPECTS_UNAUTHENTICATED`, and that
  // is the decision rather than the omission**: a 401 here is the session this
  // request carried having ended between the cold load and the screen, which is
  // exactly the fact `sessionExpiryInterceptor` owns, and suppressing it would
  // leave that person on a screen whose every read now fails with nothing
  // saying why.
  public getMe(): Observable<MeDto> {
    return this.get<MeDto>('api/me');
  }

  // **What kind of session this browser holds, if any** — the probe's first
  // question on every cold load, asked before the first route activates by a
  // browser that cannot read the `HttpOnly` cookie and so has no local evidence
  // at all. That makes a 401 this call's own answer rather than a session
  // ending, so it carries `EXPECTS_UNAUTHENTICATED` for `getSessionOwner()`'s
  // reason below: without the token every anonymous cold load ends in
  // `sessionExpiryInterceptor` navigating to `/welcome` from inside the
  // `APP_INITIALIZER`, and no deep link in the product is reachable while
  // signed out.
  //
  // **Its own route, because `GET /api/me` cannot answer a locked session.**
  // That route is budget-scoped and refuses a locked session `403`, which reads
  // as no session at all — so a probe that asked it first would sign a locked
  // tab out on every reload. This one answers both kinds.
  //
  // The body is decoded strictly by `decodeSession`; a refusal is a plain
  // `Error`, and a refusal of the request is handed on untouched, status and
  // all, because the probe tells `anonymous` from `unreachable` by it.
  public getSession(): Observable<SessionDto> {
    return this.get<unknown>(
      'api/me/session',
      new HttpContext().set(EXPECTS_UNAUTHENTICATED, true),
    ).pipe(map(decodeSession));
  }

  // The same route as `getMe()`, asked the opposite question: whose session
  // this is, and which budget it is scoped by — the second is the reason it is
  // asked at all, because the browser cannot key a blind index without it. It
  // is the request `SessionService.probe()` makes on a cold load **after**
  // `getSession()` has answered `full`, and never for a locked session, which
  // this route refuses. It is made by a browser that holds no local evidence of
  // its own, so a 401 is this call's own answer rather than a session ending —
  // the class `EXPECTS_UNAUTHENTICATED` names.
  //
  // Without the token, a refusal here ends in `sessionExpiryInterceptor`
  // navigating to `/welcome` from inside the `APP_INITIALIZER` — before the
  // router has activated anything. The probe reads this failure itself, as a
  // budget it could not learn, so what is suppressed is an interceptor acting
  // on a fact the caller has already handled.
  //
  // A second method rather than a token on `getMe()`, because the two callers
  // are asking different things of one route and only the request can tell them
  // apart. Named for the question and not for the path, so that a reader
  // choosing between the two is choosing between two meanings of a 401.
  public getSessionOwner(): Observable<MeDto> {
    return this.get<MeDto>(
      'api/me',
      new HttpContext().set(EXPECTS_UNAUTHENTICATED, true),
    );
  }

  // The export as the server's text, unparsed. The client does read the
  // document now — every name and note is opened in the tab before the file is
  // saved — but the one parse belongs to `decodeExportDocument`, which is strict
  // about the shape and argues why the money column survives it. A
  // `get<ExportDocument>()` would parse here with no shape check at all and
  // hand the decoder an object it could no longer refuse as `unrecognised`.
  // See docs/design/components.md, "Export section".
  public getExport(): Observable<string> {
    return this.getText('api/me/export');
  }

  // Ascending by `createdAtUtc`, as the server sends it. The array is `readonly`
  // from here down: nothing in the client sorts, filters or appends to it, and
  // saying so keeps a caller from reordering a list whose order is the server's
  // statement rather than the screen's preference.
  public getCredentials(): Observable<readonly CredentialSummary[]> {
    return this.get<unknown>('api/me/credentials').pipe(
      map((body) => {
        // The consumer calls `.map` on this inside a `computed` the template
        // reads, so a body that is not a list throws during change detection
        // and Angular abandons the pass — the list stays on its loading line
        // and every section below it stops updating for the rest of the visit.
        // Refused here, it is the sentence the section already has for a list
        // it could not load.
        //
        // Only the shape of the collection is checked, not of each entry. The
        // row is total in what it renders — an unrecognised kind and an
        // unreadable instant each have an answer — so a malformed *entry*
        // spoils one row, while a malformed *body* takes the screen.
        if (!Array.isArray(body)) {
          throw new Error(
            'The credential list did not arrive as a list of credentials.',
          );
        }

        return body as readonly CredentialSummary[];
      }),
    );
  }

  // How many codes are left, and nothing else. An account that has never
  // generated a set answers `0` rather than `404` — zero codes left is an
  // answer, and the two readings of it ("never generated" and "all spent")
  // share a next step, so nothing downstream needs them told apart. Whatever
  // consumes this must therefore keep `0` and "no answer yet" apart itself;
  // collapsing them is the one defect this whole path is shaped to prevent.
  //
  // There is deliberately **no** counterpart that generates a set, and the
  // reason has moved three times. `POST /api/me/recovery-codes` takes eight
  // members: the five of a fresh WebAuthn assertion, ten whole code
  // submissions, each carrying a factor id, that code's wrapped private key and
  // the account's two keys encapsulated to its public half, and — because ten
  // factors leave and ten arrive — the account's factor manifest beside the
  // rotation epoch it is written under.
  //
  // **Neither half of that is out of this client's reach any more.** A fresh
  // assertion over a challenge the *server* issued, and a signature it checks,
  // is what the Key rotation section and the erasure dialog both take on the
  // settings screen today — `reauthentication-api.service.ts` mints it and
  // {@link eraseAccount} carries one. And a rotation already opens a factor
  // under a key-encryption key presented there and then and encapsulates a
  // generation of the account's keys to public halves — the same kind of work
  // the ten submissions need (`key-rotation-material.ts`). So this is no longer
  // a missing ceremony or a missing capability.
  //
  // What is missing is the two surfaces a person would meet: a confirmation,
  // because replacing a set invalidates every code printed from the old one and
  // cannot be undone, and a place that shows the ten new codes once. The screen
  // says exactly that beside its disabled Generate control. A method here ahead
  // of those would be API surface no test could execute — a signature that
  // compiles, is called by nothing, and is wrong in a way nothing on the screen
  // would show.
  public getRecoveryCodes(): Observable<number> {
    return this.get<unknown>('api/me/recovery-codes').pipe(
      map((body) => {
        if (!isRecoveryCodeCount(body)) {
          throw new Error(
            'The recovery-code response carried no usable count of codes.',
          );
        }

        return body.remaining;
      }),
    );
  }

  // The account's key custody: its manifest, the generation that manifest is in,
  // and one entry per recovery factor — one for a passkey, ten for a card of
  // recovery codes, and an **empty `factors` array** for a session the server
  // cannot see. That last one is not an error and must never be turned into one
  // here: never established, already ended and belonging to somebody else are one
  // indistinguishable answer on purpose, and a caller that told them apart would
  // rebuild the enumeration oracle the route refuses to be. `manifest: null`
  // beside `rotationEpoch: 0` and no factors is a perfectly ordinary 200, and is
  // also what an account registered before the manifest landed answers forever.
  //
  // **It carries `EXPECTS_UNAUTHENTICATED`, and that is not `getMe()`'s case
  // turned around — it is custody's own rule, enforced from the outside.**
  //
  // `AccountKeyCustodyService` is the only caller, and it never calls anything
  // on `SessionService`, because a key that will not open is not a session that
  // ended. Unmarked, this request routes its own 401 into
  // `sessionExpiryInterceptor` — the single owner of "the session ended" —
  // which makes that call anyway, through an edge no import graph shows. On the
  // sign-in path the damage is immediate: the assertion answers 200,
  // `session.established()` runs, custody's read leaves, the router is sent to
  // `/app`, and a 401 on that read then publishes `anonymous` and navigates to
  // `/welcome`. Being later, it wins. The person lands anonymous on the welcome
  // screen holding a session cookie the server had just issued, with the screen
  // saying nothing — `SignInService` is component-provided, so its `failure()`
  // is a fresh `null` — and a 401 that reproduces is a loop.
  //
  // **The fact is deferred, not lost.** If the session has genuinely ended, the
  // next read the person makes — the Settings email, the credential list, the
  // export — answers 401 unmarked, and the interceptor acts then, from a screen
  // that renders its own failure line. What is given up is a few seconds of
  // knowing; what is bought is that nobody is thrown out of an account over a
  // cookie that had not landed yet.
  //
  // **The token rides on the method rather than on an `HttpContext` the caller
  // passes**, which is the opposite of `getMe()`/`getSessionOwner()` and for
  // the reason that pair exists: there, one route is read by two callers asking
  // two different questions, and only the request can tell them apart. Here
  // there is one caller and one question, and the meaning of a 401 is fixed by
  // the route — a key read made by a browser that already believes it is signed
  // in. A `context?` parameter would advertise the opposite, and the next
  // caller that omitted it would restore the defect silently.
  //
  // The factor list is `readonly` from here down for the reason
  // `getCredentials`'s is: its order is the server's statement, and nothing in
  // the client sorts, filters or appends to it. The consumer walks the whole of
  // it — see `account-key-custody.service.ts`, which tries each entry in turn
  // under its own `factorId`.
  //
  // **The body used to be the bare array `factors` now holds, and the client
  // reading the old spelling is what this method is.** A wrapper is refused and
  // an array is accepted exactly the wrong way round on a version skew, so the
  // two shapes swap places here in one edit rather than one of them being
  // tolerated "for now": a boundary that took either is a boundary that has
  // stopped saying which server it is talking to.
  public getAccountKeys(): Observable<AccountKeyCustodyDto> {
    return this.get<unknown>(
      'api/me/account-keys',
      new HttpContext().set(EXPECTS_UNAUTHENTICATED, true),
    ).pipe(
      map((body) => {
        // Six refusals rather than one, because they say six different things
        // to whoever reads the message. This first one is the route or a proxy
        // answering something else entirely — **and it is what the retired shape
        // now lands on**: a bare array is the answer the previous server gave, so
        // a client running against one says so here instead of reading `undefined`
        // off an array's `factors` property and calling the account empty.
        //
        // `Array.isArray` is part of the condition and not an afterthought: an
        // array *is* an object to `typeof`, so without it every one of the
        // member checks below would run against a list and refuse for the wrong
        // reason, three layers from the fact that matters.
        if (typeof body !== 'object' || body === null || Array.isArray(body)) {
          throw new AccountKeyResponseError(
            "The account-key response did not arrive as an account's key custody.",
          );
        }

        if (!('manifest' in body) || !isManifestWire(body.manifest)) {
          throw new AccountKeyResponseError(
            'The account-key response carried no readable manifest.',
          );
        }

        if (
          !('rotationEpoch' in body) ||
          !isRotationEpoch(body.rotationEpoch)
        ) {
          throw new AccountKeyResponseError(
            'The account-key response carried no usable rotation epoch.',
          );
        }

        // **The two read together, which neither of the refusals above can
        // do.** `isManifestWire` admits any non-empty string and
        // `isRotationEpoch` admits `0`; the invariant that ties them is on
        // {@link AccountKeyCustodyDto} — `0` is the epoch of an account with no
        // manifest row, because a stored generation starts at 1 and the column
        // refuses anything lower. So a manifest beside `0`, and `null` beside a
        // stored generation, are each well formed member by member and are a
        // pair no account can be in.
        //
        // **Refused here, three layers from where the skew would otherwise
        // land.** A manifest served beside `0` reaches `openFactorManifest`,
        // whose associated data refuses an epoch below 1 — and that throw is
        // caught by the gate in `AccountKeyCustodyService`, which turns it into
        // a statement about the account's *key material*. Somebody would be
        // told nothing they hold will ever open this account, over two members
        // of a body that merely disagreed with each other. The type thrown here
        // is what keeps it on this boundary's own word instead: the answer is
        // one this client cannot read, and a reload is the act that changes it.
        if ((body.manifest === null) !== (body.rotationEpoch === 0)) {
          throw new AccountKeyResponseError(
            'The account-key response disagreed with itself about whether the account has a manifest.',
          );
        }

        if (!('factors' in body) || !Array.isArray(body.factors)) {
          throw new AccountKeyResponseError(
            'The account-key response did not carry a list of factors.',
          );
        }

        // Re-typed to `readonly unknown[]` before the members are judged, and
        // the line is load-bearing rather than ceremony. `Array.isArray` over an
        // `unknown` narrows to `any[]`, and every element of an `any[]` is
        // assignable to anything — so the return below would compile with the
        // check underneath it deleted, and the only thing standing between a
        // malformed body and a caller would be a runtime guard nothing in the
        // types required. Named as `unknown[]`, the narrowing `every` performs
        // is what makes the return type true.
        const factors: readonly unknown[] = body.factors;

        if (!factors.every(isAccountKeyEntry)) {
          throw new AccountKeyResponseError(
            'The account-key response carried a factor missing its identifier or one of its two envelopes.',
          );
        }

        return {
          manifest: body.manifest,
          rotationEpoch: body.rotationEpoch,
          factors,
        };
      }),
    );
  }

  // Ends the caller's own session. There is nothing to send and nothing to
  // read: the session is named by the request's own cookie rather than by
  // anything in a body or a URL, and the answer is 204 with the `Set-Cookie`
  // that clears the handle. `Observable<void>` states that — a declared body
  // here would be a shape the route does not have, and one a caller could be
  // tempted to publish a session state from.
  //
  // **No `EXPECTS_UNAUTHENTICATED`, and that is a decision rather than an
  // omission.** The token marks a request whose 401 is the *route's own
  // verdict* on that request — a passkey that did not verify, a recovery code
  // that matched nothing, an erasure gate declining its assertion. This route
  // gives no such verdict: it judges nothing but the session it was sent with,
  // so a 401 means that session had already ended, which is exactly the fact
  // `sessionExpiryInterceptor` owns. Suppressing it would claim a verdict this
  // route never gives, and would suppress the one reading that is true.
  public endSession(): Observable<void> {
    return this.post<void>('api/me/session/revocation', null);
  }

  // Erases the account the session belongs to, authorized by a fresh passkey
  // assertion signed over a re-authentication challenge. See
  // docs/design/components.md, "Erasure dialog", and
  // docs/business-logic/erasure.md.
  //
  // `POST /api/me/erasure`, and never `DELETE /api/me`: the server removed that
  // route so the token-only path is closed, and content on a DELETE has no
  // generally defined semantics (RFC 9110 §9.3.5) — some implementations may
  // reject a request that carries it, and the proof has to travel as content.
  // The body names no account — the account erased is whichever one the
  // session is.
  //
  // **The body is the five assertion members, projected one by one here**, and
  // never the object handed in, spread or forwarded. What crosses the wire is
  // decided at the boundary that builds it: the ceremony's result carries the
  // key-encryption key beside the payload, and a `PasskeyAssertionPayload` is a
  // structural type, so an object with more members than it declares still
  // satisfies it. Named here, a sixth member cannot ride along — and
  // `userHandle` is written through as `null` when absent, because
  // `JSON.stringify` drops an `undefined` member and the server would then bind
  // a missing one to null by accident rather than by contract.
  //
  // **It carries `EXPECTS_UNAUTHENTICATED`, on a request made by a signed-in
  // browser.** A 401 here is one of two things. Usually it is the route's
  // verdict on this request — the gate declining the assertion before the
  // transaction opens. But the route sits behind the fallback authorization
  // policy, so a session that had already ended — expired, revoked, or erased
  // from another tab — is also answered 401, before the gate runs. Either way
  // this request erased nothing. Unmarked, `sessionExpiryInterceptor` reads
  // the verdict as a session ending and takes the tab to `/welcome` over a
  // sentence the dialog never got to say. A session that really had ended is
  // not lost by the mark: the flow resolves a 401 here with one unmarked
  // `GET /api/me` probe, whose own 401 the interceptor acts on — and only a
  // probe that does not find the session gone lets the dialog say `refused`,
  // *nothing was erased*. The token rides on the method rather than on a
  // parameter, for `getAccountKeys`'s reason: one caller, one question, one
  // meaning of a 401 to this caller. The challenge minted just before this is
  // the opposite case and stays unmarked — see
  // `reauthentication-api.service.ts`.
  //
  // `Observable<void>`: the answer is `204`. A body would have to be assembled
  // from an account that no longer exists. Refusals are handed to the caller
  // untouched, because the caller reads the status to tell `refused` from
  // `unrecognised` from `undetermined` — and there is **no retry** here or
  // anywhere this request passes: a second erasing request after a lost `204`
  // is answered `401`, which would read as *nothing was erased* over an account
  // that is gone.
  public eraseAccount(assertion: PasskeyAssertionPayload): Observable<void> {
    return this.post<void>(
      'api/me/erasure',
      {
        credentialId: assertion.credentialId,
        clientDataJson: assertion.clientDataJson,
        authenticatorData: assertion.authenticatorData,
        signature: assertion.signature,
        userHandle: assertion.userHandle ?? null,
      } satisfies PasskeyAssertionPayload,
      new HttpContext().set(EXPECTS_UNAUTHENTICATED, true),
    );
  }

  // Moves the account to the address the provider token names, authorized by
  // a fresh passkey assertion. The answer is how many *other* sessions the
  // change ended; this one survives it.
  //
  // **The token rides on the context, and this method writes no header.** A
  // header written here would reach the wire whatever origin the request went
  // to; handed to `apiCredentialsInterceptor` on `PROVIDER_CREDENTIAL`, it is
  // sent only once that interceptor has decided the request is ours and is for
  // `EMAIL_CHANGE_PATH`. The path is imported from there rather than spelled
  // again, so the two cannot drift apart; `slice(1)` drops its leading slash
  // because `post` joins with one.
  //
  // The body is the five assertion members projected one by one, for
  // `eraseAccount`'s reason — and the token is never among them. It carries
  // `EXPECTS_UNAUTHENTICATED` for the same reason too: a 401 here is the route
  // refusing the assertion or the token, which the screen has its own sentence
  // for, not the session ending.
  //
  // **Two readings of a 200, told apart here and never collapsed.** A body that
  // is not an object at all is refused with a throw: the change may or may not
  // have landed, and the caller reads anything that is not an
  // `HttpErrorResponse` as exactly that. A body that is an object but carries
  // no readable count is a change that happened, answered with `null` — the
  // screen's clause-free line then claims nothing about other browsers.
  public changeEmail(
    idToken: string,
    assertion: PasskeyAssertionPayload,
  ): Observable<EmailChangeResultDto> {
    return this.post<unknown>(
      EMAIL_CHANGE_PATH.slice(1),
      {
        credentialId: assertion.credentialId,
        clientDataJson: assertion.clientDataJson,
        authenticatorData: assertion.authenticatorData,
        signature: assertion.signature,
        userHandle: assertion.userHandle ?? null,
      } satisfies PasskeyAssertionPayload,
      new HttpContext()
        .set(PROVIDER_CREDENTIAL, idToken)
        .set(EXPECTS_UNAUTHENTICATED, true),
    ).pipe(
      map((body): EmailChangeResultDto => {
        if (typeof body !== 'object' || body === null || Array.isArray(body)) {
          throw new Error(
            'The email-change response did not arrive as an outcome.',
          );
        }

        const count = 'sessionsEnded' in body ? body.sessionsEnded : undefined;

        return { sessionsEnded: isSessionCount(count) ? count : null };
      }),
    );
  }

  // Opens a locked session on an account with no factors, authenticated by the
  // provider token the release flow was handed and nothing else. The answer
  // sets the session cookie; the body describes the session it opened.
  //
  // **The token rides on the context, and this method writes no header**, for
  // `changeEmail`'s reason: handed to `apiCredentialsInterceptor` on
  // `PROVIDER_CREDENTIAL`, it is sent only once that interceptor has decided
  // the request is ours and is for `LOCKED_SESSION_PATH` — and never replaced
  // by a stored token. The path is imported from there; `slice(1)` drops its
  // leading slash because `post` joins with one. The body is `null`: the token
  // is the whole of what this request presents.
  //
  // **It carries `EXPECTS_UNAUTHENTICATED`.** The browser asking holds no
  // session yet, so a 401 is the route refusing the provider token — the
  // release screen has its own sentence for that — never a session ending.
  //
  // Refusals are handed on untouched, status and body: `404` with
  // `refusal: "no_account"` is answered on the screen with its own sentence and
  // a way to create an account, and the rest are told apart by status. A body
  // this client cannot read is a plain `Error` from `decodeLockedSession`.
  public openLockedSession(idToken: string): Observable<LockedSessionDto> {
    return this.post<unknown>(
      LOCKED_SESSION_PATH.slice(1),
      null,
      new HttpContext()
        .set(PROVIDER_CREDENTIAL, idToken)
        .set(EXPECTS_UNAUTHENTICATED, true),
    ).pipe(map(decodeLockedSession));
  }
}
