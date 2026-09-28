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
 *   gate runs. Both mean this request erased nothing, and the next press's
 *   unmarked challenge lets `sessionExpiryInterceptor` handle a dead session.
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
