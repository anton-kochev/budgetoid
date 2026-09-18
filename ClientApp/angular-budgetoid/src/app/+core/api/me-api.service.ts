import { HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import type { FactorKeypairEnvelopes } from '@app-core/security/factor-keypair';
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

  // The same route, asked the opposite question: *is* there a session, whose,
  // and which budget it is scoped by — the third rides along because the answer
  // already carries it, and because the browser cannot key a blind index without
  // it. It is the request `SessionService.probe()` makes on every cold load,
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
  // reason has moved twice. `POST /api/me/recovery-codes` takes eight members:
  // the five of a fresh WebAuthn assertion, which this client *can* now
  // produce — `webauthn-ceremony.service.ts` runs one — ten whole code
  // submissions, each carrying a factor id, that code's wrapped private key and
  // the account's two keys encapsulated to its public half, and — because ten
  // factors leave and ten arrive — the account's factor manifest beside the
  // rotation epoch it is written under. The submissions are no longer blocked
  // on the *server*: `getAccountKeys` below reads the envelopes, and
  // `account-key-custody.service.ts` opens them on every passkey sign-in. They
  // are blocked on what custody keeps — two non-extractable `CryptoKey` objects
  // behind no accessor — while an encapsulation takes the account's keys as
  // bytes. Getting those bytes means opening a factor again under a
  // key-encryption key derived from one somebody presents there and then, which
  // is a ceremony — but not the one this route wants.
  //
  // **That distinction is what is left, and it is narrower than "the settings
  // screen runs no ceremony".** It runs one: the **Unlock** control asserts a
  // passkey. That assertion is minted in the browser, over a challenge the
  // browser chose, and is discarded unsent — nothing verifies it and nothing
  // needs to, because the account's own envelopes are what judge the factor.
  // The five assertion members here are the other kind: a challenge the
  // *server* issued and a signature it checks. To the person holding the device
  // the two are one system prompt, and what they authorize is not comparable —
  // one opens envelopes this browser is already entitled to, the other replaces
  // the account's whole recovery card. So the ceremony this route needs is
  // still one nothing on that screen runs, and a method for it would be API
  // surface no test could execute — a signature that compiles, is called by
  // nothing, and is wrong in a way nothing on the screen would show.
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
