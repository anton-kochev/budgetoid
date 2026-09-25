import { HttpErrorResponse } from '@angular/common/http';

// The one module allowed to touch `console`, and the one line every failure in
// the application ends as. No record may carry an email, a credential subject
// or a narrative value, and every free-text field a failure can hold — a
// message, a URL, a response body, a status text, even an Error's `name` — is
// somewhere one of those can sit. So nothing free-text is printed: the reason
// is a literal the author wrote, and the cause is reduced to a closed shape
// whose every member is a number or a name this list vouches for.

// Names printed as they are. `name` is writable on any Error, so a name is
// kept only when it is on this list, never because it looks like a class name.
// `HttpErrorResponse` is not here: it is not an `Error`, and a real one is
// projected by its status before any name is read.
const PRINTABLE_ERROR_NAMES: ReadonlySet<string> = new Set([
  // The platform's name for a failed `fetch` and for a wrong-typed operation.
  'TypeError',
  // WebCrypto's DOMException name for a decrypt or unwrap that failed.
  'OperationError',
  // WebAuthn's DOMException name for a ceremony the person or browser refused.
  'NotAllowedError',
  // The platform's DOMException name for an aborted operation.
  'AbortError',
  // The platform's DOMException name for an operation that ran out of time.
  'TimeoutError',
  // Our class, `narrative-cipher.ts`: a codec refusal about the call itself.
  'NarrativeFieldMisuseError',
  // Our class, `factor-manifest.ts`: a manifest wire value the decoder refused.
  'FactorManifestWireError',
  // Our class, `key-rotation-material.ts`: rotation material that disagreed.
  'KeyRotationMaterialError',
  // Our class, `export-document.ts`: a wire string that cannot be an envelope.
  'NotAnEnvelopeError',
  // Our class, `key-rotation.service.ts`: a rotation the server refused.
  'KeyRotationRefusal',
  // Our class, `key-rotation.service.ts`: a rotation stopped by a name clash.
  'NameCollisionStop',
]);

/** What a console line says about its cause. Closed: nothing else is printed. */
export type FailureProjection =
  | { readonly kind: 'http'; readonly status: number }
  | { readonly kind: 'error'; readonly name?: string }
  | { readonly kind: 'non-error' };

// Never throws. A cause is anything that was thrown, a hostile getter or a
// revoked Proxy included, and a throw from here would land in the
// `ErrorHandler` that called it — which calls here again.
function project(cause: unknown): FailureProjection {
  try {
    // A real response only. Recognised by shape, a plain object would print
    // whatever it keeps under `status`.
    if (cause instanceof HttpErrorResponse) {
      const status: unknown = cause.status;

      return typeof status === 'number'
        ? { kind: 'http', status }
        : { kind: 'non-error' };
    }

    // In a browser the second check is redundant: WebIDL gives
    // `DOMException.prototype` the realm's `Error.prototype` as its prototype.
    // It is here for the test runner, whose global `DOMException` is jsdom's.
    // That one inherits from an `Error.prototype` of another realm, so
    // `instanceof Error` answers `false` for it (measured). Without this
    // check every WebCrypto and WebAuthn failure the specs build would
    // project as a `non-error`, and the allow-list could not be tested.
    if (cause instanceof Error || cause instanceof DOMException) {
      // Read once: a getter can answer the check one thing and the print
      // another, so the value checked is the value printed.
      const name: unknown = cause.name;

      return typeof name === 'string' && PRINTABLE_ERROR_NAMES.has(name)
        ? { kind: 'error', name }
        : { kind: 'error' };
    }
  } catch {
    // The thrown value is not read: it may carry what the cause did.
  }

  return { kind: 'non-error' };
}

// `R` when every member of it is a string literal, `never` otherwise.
//
// A type is a literal exactly when a record keyed on it has a required key: an
// empty object does not satisfy `Record<'a', 1>`, and it does satisfy
// `Record<string, 1>`, `Record<`x${string}`, 1>`, `Record<Uppercase<string>, 1>`
// and the record keyed on a branded `string`, all of which are index
// signatures or empty. The check distributes over a union, so one member
// that is not a literal empties the whole type. Undistributed, a union such as
// `'a' | `x${string}`` passes, because its `'a'` key alone makes the record
// refuse `{}`. `string extends R`, the check that stood here, caught only a
// bare `string`.
type LiteralReason<R extends string> = R extends unknown
  ? // eslint-disable-next-line @typescript-eslint/no-empty-object-type -- `{}` is the probe: the question is whether an object with no keys satisfies the record.
    {} extends Record<R, 1>
    ? never
    : R
  : never;

/**
 * Prints one failure line on `console.error`: the reason alone, or the reason
 * and the cause's {@link FailureProjection}.
 *
 * The reason has to be a string literal (or a union of them). A widened
 * `string`, a template with a `string` hole, or a union holding either is
 * refused, because a built string can carry anything. The cause is
 * a rest element rather than an optional parameter so that "no cause" and
 * "the cause was `undefined`" stay two things — a `throw undefined` still
 * prints as a `non-error` rather than vanishing.
 */
export function logFailure<R extends string>(
  reason: LiteralReason<R>,
  ...cause: [] | [unknown]
): void {
  if (cause.length === 0) {
    console.error(reason);

    return;
  }

  console.error(reason, project(cause[0]));
}
