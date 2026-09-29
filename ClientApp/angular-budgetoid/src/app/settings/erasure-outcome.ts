// How an erasing request that did not answer 204 is read. See
// docs/design/components.md, "Erasure dialog", *The status region, and what
// each line says*.
import { HttpErrorResponse } from '@angular/common/http';

/**
 * The three words a refused or unanswered erasing request can end on.
 *
 * * `refused` — a `401` from the erasing request itself. Usually the gate
 *   declined the assertion before the transaction opened; its refusals are
 *   byte-identical by design, so this is one word for an expired challenge, a
 *   bad signature and another account's passkey. The route also sits behind the
 *   fallback authorization policy, so a session that had already ended —
 *   expired, revoked, or erased from another tab — answers `401` before the
 *   gate runs. Both mean this request erased nothing, and the flow tells them
 *   apart before it publishes anything, with one **unmarked** `GET /api/me`: a
 *   `401` there means the session had already ended, `sessionExpiryInterceptor`
 *   takes over, and the flow says nothing; a `200` — or a probe that cannot
 *   answer — means the gate declined the assertion, and the word is `refused`.
 * * `unrecognised` — a `400` or `403`, raised before the handler is entered.
 *   Nothing about the account was judged, so nothing was erased.
 * * `undetermined` — everything else. The request may have reached the server
 *   and committed.
 */
export type ErasureRequestFailure = 'refused' | 'unrecognised' | 'undetermined';

/**
 * Reads what an erasing request failed with.
 *
 * **The default is `undetermined`, and that is the rule rather than a
 * fallback.** It is the only word whose sentence does not say *nothing was
 * erased*, and a lost response, a `5xx`, a gateway that gave up on a commit
 * still running behind it, and a status-`0` failure after the request left all
 * mean the erasure may have happened. So a status earns `refused` or
 * `unrecognised` only by being one of the three the server is known to answer
 * *before* it erases anything; every other answer — including a status nobody
 * listed here — cannot say this request erased nothing.
 *
 * **An `HttpErrorResponse` or nothing.** A plain object carrying `status: 401`
 * is not the gate's answer, and reading `.status` off anything handed in would
 * tell somebody whose account may be gone that nothing happened, on the
 * strength of a shape.
 */
export function erasureFailureOf(error: unknown): ErasureRequestFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'undetermined';
  }

  switch (error.status) {
    case 401:
      return 'refused';
    case 400:
    case 403:
      return 'unrecognised';
    default:
      return 'undetermined';
  }
}

/**
 * The two words a failed re-authentication challenge request can end on, or
 * `null` when the flow has nothing to say.
 *
 * * `null` — a `401`. The challenge is unmarked, so a `401` on it is a session
 *   that really has ended, and that fact is `sessionExpiryInterceptor`'s.
 * * `unrecognised` — a `400` or `403`, refused before any handler ran. A `403`
 *   is chiefly the missing `X-Budgetoid-Client` header, for which *reload* is
 *   the right next step.
 * * `unstarted` — everything else, including anything that is not an
 *   `HttpErrorResponse`. **It names no cause**: a status-`0` failure and a
 *   `5xx` have the same next step — try again in a minute — so two sentences
 *   would be a distinction nobody can act on.
 */
export type ChallengeRequestFailure = 'unstarted' | 'unrecognised';

/** Reads what a re-authentication challenge request failed with. */
export function challengeFailureOf(
  error: unknown,
): ChallengeRequestFailure | null {
  if (!(error instanceof HttpErrorResponse)) {
    return 'unstarted';
  }

  switch (error.status) {
    case 401:
      return null;
    case 400:
    case 403:
      return 'unrecognised';
    default:
      return 'unstarted';
  }
}
