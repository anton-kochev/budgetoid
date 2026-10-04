// How a cancellation request that did not answer 204 is read. See
// docs/business-logic/erasure.md and docs/design/components.md.
import { HttpErrorResponse } from '@angular/common/http';

/**
 * What a refused or unanswered cancellation ends on.
 *
 * * `refused` — a `401` whose body carries `refusal: "assertion"`, the constant
 *   every declined passkey in the product answers. The gate judged the passkey
 *   and declined it, so the erasure is still scheduled; a session that had
 *   already ended would have been turned away before the gate, so no probe is
 *   owed.
 * * `probe` — any other `401`. Not a sentence but an instruction: nothing
 *   judged the passkey, so the flow asks one **unmarked** `GET /api/me` before
 *   it says anything. A `401` there is `sessionExpiryInterceptor`'s; anything
 *   else is `undetermined`.
 * * `unrecognised` — a `400` or `403`, raised before the handler is entered —
 *   a `403` is also a locked session at the fallback policy. Nothing was
 *   judged, so the erasure is still scheduled.
 * * `undetermined` — everything else. The request may have reached the handler
 *   and committed.
 */
export type CancellationRequestFailure =
  | 'refused'
  | 'unrecognised'
  | 'undetermined'
  | 'probe';

/**
 * Reads what a cancellation request failed with.
 *
 * **The default is `undetermined`**, the only word whose sentence does not say
 * *the erasure is still scheduled*. A status earns another word only by being
 * one the route is known to answer before it decides anything; a lost
 * response, a `5xx`, a proxy's `408` or `429` and a status nobody listed here
 * all stay `undetermined`.
 *
 * **An `HttpErrorResponse` or nothing.** A plain object shaped like the gate's
 * answer is not the gate's answer. And the refusal word is compared exactly,
 * read only as the body's own member: a case-folded or trimmed comparison, or a
 * lookup that reaches the prototype, would call something the gate never said
 * a declined passkey.
 */
export function cancellationFailureOf(
  error: unknown,
): CancellationRequestFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'undetermined';
  }

  switch (error.status) {
    case 401:
      return ownRefusalOf(error.error) === 'assertion' ? 'refused' : 'probe';
    case 400:
    case 403:
      return 'unrecognised';
    default:
      return 'undetermined';
  }
}

// The body's own `refusal` member, or `undefined` for a body without one. The
// `in` test narrows the type; `Object.hasOwn` is the one that decides.
function ownRefusalOf(body: unknown): unknown {
  return typeof body === 'object' &&
    body !== null &&
    'refusal' in body &&
    Object.hasOwn(body, 'refusal')
    ? body.refusal
    : undefined;
}
