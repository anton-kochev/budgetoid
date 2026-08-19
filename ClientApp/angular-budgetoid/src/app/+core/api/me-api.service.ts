import { Injectable } from '@angular/core';
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

@Injectable({ providedIn: 'root' })
export class MeApiService extends BaseApiService {
  public getMe(): Observable<MeDto> {
    return this.get<MeDto>('api/me');
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
  // reason has moved. `POST /api/me/recovery-codes` takes six members: the five
  // of a fresh WebAuthn assertion, which this client *can* now produce —
  // `webauthn-ceremony.service.ts` runs one — and ten whole code submissions,
  // each carrying its own wrapped copy of the account's content key and index
  // key. It is that sixth member nothing here can build: wrapping the account's
  // keys needs them unwrapped, and no route hands `wrapped_account_keys` back.
  // A method for it would be API surface no test could execute — a signature
  // that compiles, is called by nothing, and is wrong in a way nothing on the
  // screen would show.
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
