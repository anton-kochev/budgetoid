import { HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import type { WrappedAccountKeys } from '@app-core/security/account-keys';
import { map, Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface MeDto {
  email: string;
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
// wire: the identifier the two envelopes were sealed against, and the envelopes.
// Nothing else — no credential id, no user id, no registration instant — and the
// route is specified never to grow one.
//
// **Composed from `WrappedAccountKeys` rather than restating its two members**,
// which is the whole reason this file reaches into `+core/security` at all. That
// interface is what `unwrapAccountKeys` takes, so an entry read here is passed
// straight to it; two hand-written copies of `wrappedContentKey` and
// `wrappedIndexKey` would let one be renamed while the other went on compiling
// against a body it no longer describes. The import is `import type`, so nothing
// of the crypto module reaches the bundle this file already sits in.
//
// An intersection rather than an `interface extends`: there is one member to
// add, and the composition says so without inventing a hierarchy.
export type AccountKeyEntry = WrappedAccountKeys & {
  // The canonical lower-case hyphenated spelling the row was stored in. It **is**
  // the associated data both envelopes were sealed with, so it travels beside
  // them and is never derived, normalised or prettified on the way past.
  readonly factorId: string;
};

// The boundary check `isRecoveryCodeCount` argues for, on a body where a
// coercion is even quieter.
//
// Every member is a string that is about to be fed to a decoder and an AEAD
// open. A missing one arrives at `decodeBase64Url` as `undefined`, which throws
// *inside the trial loop* — where a throw already means "this factor is not the
// one, try the next" — so a malformed body would be read as a person presenting
// the wrong factor and answered with "present another factor". That is the
// failure this refusal exists to prevent: not a wrong pixel, but the account
// declared unopenable by its own key custody, with nothing naming the cause.
//
// So the check is per entry and not merely over the collection, unlike
// `getCredentials` — a credential row is total in what it renders and a
// malformed entry spoils one row, while here an entry has no partial use at all.
// It is deliberately **not** a check of the envelopes' shape: width, version
// byte and alphabet are `decodeBase64Url`'s and `openEnvelope`'s rules, and a
// second, weaker copy of them here would be a second definition of what an
// envelope is.
function isAccountKeyEntry(entry: unknown): entry is AccountKeyEntry {
  return (
    typeof entry === 'object' &&
    entry !== null &&
    'factorId' in entry &&
    typeof entry.factorId === 'string' &&
    'wrappedContentKey' in entry &&
    typeof entry.wrappedContentKey === 'string' &&
    'wrappedIndexKey' in entry &&
    typeof entry.wrappedIndexKey === 'string'
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

  // The same route, asked the opposite question: *is* there a session, and
  // whose. It is the request `SessionService.probe()` makes on every cold load,
  // before the first route activates, from a browser that cannot read the
  // `HttpOnly` cookie and so has no local evidence at all — which makes a 401
  // this call's own answer rather than a session ending. It is the purest
  // member of the class `EXPECTS_UNAUTHENTICATED` names: a request made by a
  // browser holding no session to lose.
  //
  // Without the token every anonymous cold load ends in
  // `sessionExpiryInterceptor` navigating to `/welcome` from inside the
  // `APP_INITIALIZER` — before the router has activated anything, so no deep
  // link in the product is reachable while signed out. `probe()` already
  // publishes `anonymous` from this refusal itself, so what is suppressed here
  // is a second, redundant statement of a fact the caller has already made.
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

  // The export is bytes, never a parsed document, and that is the whole point
  // of the feature rather than a stylistic choice. Amounts ship as JSON numbers
  // at `numeric(14,4)` scale, which is exact for the .NET writer but not for a
  // JavaScript reader: JSON.parse turns them into IEEE-754 doubles whose
  // significand does not cover that column's range. Anything that parses the
  // response and re-serializes it — including `get<ExportDocument>()`, which is
  // what this will look like it should have been — silently degrades the very
  // file the feature exists to hand over. The client is a pipe: it reads no
  // property of the document and writes the bytes it received to disk.
  // See docs/business-logic/export.md, "Money ships as JSON numbers".
  public getExport(): Observable<Blob> {
    return this.getBlob('api/me/export');
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
  // reason has moved twice. `POST /api/me/recovery-codes` takes six members:
  // the five of a fresh WebAuthn assertion, which this client *can* now
  // produce — `webauthn-ceremony.service.ts` runs one — and ten whole code
  // submissions, each carrying its own wrapped copy of the account's content
  // key and index key. That sixth member is no longer blocked on the *server*:
  // `getAccountKeys` below reads the envelopes, and
  // `account-key-custody.service.ts` opens them on every passkey sign-in. It is
  // blocked on what custody keeps — two non-extractable `CryptoKey` objects
  // behind no accessor — while a wrap takes bytes. Getting bytes means
  // unwrapping again under a key-encryption key derived from a factor somebody
  // presents, which is the same ceremony the other five members want and which
  // `/app/settings` does not run. A method for it would be API surface no test
  // could execute — a signature that compiles, is called by nothing, and is
  // wrong in a way nothing on the screen would show.
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

  // The wrapped account keys filed under the credential that opened this
  // session — one entry for a passkey, ten for a set of recovery codes, and an
  // **empty array** for a session the server cannot see. That last one is not an
  // error and must never be turned into one here: never established, already
  // ended and belonging to somebody else are one indistinguishable answer on
  // purpose, and a caller that told them apart would rebuild the enumeration
  // oracle the route refuses to be.
  //
  // **No `EXPECTS_UNAUTHENTICATED`, and that is the decision rather than the
  // omission** — `getMe()`'s case, one line for one line. This request is made
  // by a browser that believes it holds a session, so a 401 is that session
  // having ended, which is the one fact `sessionExpiryInterceptor` owns.
  // Marking it would suppress the only true reading and leave somebody on a
  // screen whose every later read fails with nothing saying why.
  //
  // The list is `readonly` from here down for the reason `getCredentials`'s is:
  // its order is the server's statement, and nothing in the client sorts,
  // filters or appends to it. The consumer walks the whole of it — see
  // `account-key-custody.service.ts`, which tries each entry in turn under its
  // own `factorId`.
  public getAccountKeys(): Observable<readonly AccountKeyEntry[]> {
    return this.get<unknown>('api/me/account-keys').pipe(
      map((body) => {
        // Two refusals rather than one, because the two say different things to
        // whoever reads the message: a body that is not a list is a route or a
        // proxy answering something else entirely, while a malformed entry is a
        // version skew on a route that *is* the right one.
        if (!Array.isArray(body)) {
          throw new Error(
            'The account-key response did not arrive as a list of factors.',
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
        const entries: readonly unknown[] = body;

        if (!entries.every(isAccountKeyEntry)) {
          throw new Error(
            'The account-key response carried a factor missing its identifier or one of its two envelopes.',
          );
        }

        return entries;
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
  // verdict* — a passkey that did not verify, a recovery code that matched
  // nothing — made by a browser holding no session to lose. This request is the
  // opposite: it is made by an authenticated person, so a 401 means the session
  // it carried had already ended, which is exactly the fact
  // `sessionExpiryInterceptor` owns. Suppressing it would claim a verdict this
  // route never gives, and would suppress the one reading that is true.
  public endSession(): Observable<void> {
    return this.post<void>('api/me/session/revocation', null);
  }
}
