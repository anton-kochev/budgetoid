// How a cancellation request that did not answer 204 is read. See
// docs/business-logic/erasure.md and docs/design/components.md.
//
// **The table differs from the erasing request's in one row, and the row is
// the point.** A 401 to the cancellation is two things: the gate declining
// the assertion, or a session that had already ended and was turned away by
// the fallback policy before the gate ran. The erasing request tells them
// apart with a probe. This one does not need to for the first: every declined
// passkey in the product answers `refusal: "assertion"` (the server's
// `PasskeyVerificationExceptionHandler`, verified by
// `CancelScheduledErasureEndpointTests.Cancel_WithoutAnAssertion_Is401_…`), so
// that body is `refused` outright, and only a 401 *without* it is handed to the
// probe — `'probe'` here is the instruction, not a sentence.
//
// The default is `undetermined`. Cancelling is idempotent, so that word keeps
// the control live; but it is still the only word that does not say *the
// erasure is still scheduled*, and every shape of "we can't know" is pinned to
// it below.
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { cancellationFailureOf } from './erasure-cancellation-outcome';

function httpError(status: number, body: unknown = null): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'test', error: body });
}

// The server's body for a declined assertion, as it reaches a subscriber.
const ASSERTION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'The passkey could not be verified.',
  status: 401,
  refusal: 'assertion',
};

// What the fallback policy answers an ended session with: a 401 carrying no
// `refusal` member at all.
const SESSION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
};

describe('cancellationFailureOf', () => {
  it('reads a 401 carrying the assertion refusal as refused', () => {
    // Act
    const reading = cancellationFailureOf(httpError(401, ASSERTION_REFUSAL));

    // Assert
    // The gate judged the passkey and declined it, so the erasure is still
    // scheduled — and there is no session question to ask, because a session
    // that had ended would have been turned away before the gate.
    expect(reading).toBe('refused');
  });

  it('hands a 401 with no refusal member to the probe', () => {
    // Act
    const reading = cancellationFailureOf(httpError(401, SESSION_REFUSAL));

    // Assert
    // *Didn't accept that passkey* would be false: nothing judged it. Only an
    // unmarked `GET /api/me` can say whether the session is still there.
    expect(reading).toBe('probe');
  });

  it('hands a 401 with no body to the probe', () => {
    // Act
    const reading = cancellationFailureOf(httpError(401));

    // Assert
    expect(reading).toBe('probe');
  });

  // The near misses on the member's value. `Assertion` is a mapping that
  // folded case; `toString` and `constructor` are a mapping that looked the
  // value up on a plain object, where every object answers them; the
  // provider's word is another route's refusal; an array is a body that only
  // stringifies to the right word.
  it.each([
    { label: 'a capitalised word', refusal: 'Assertion' },
    { label: 'a prototype key', refusal: 'toString' },
    { label: 'another prototype key', refusal: 'constructor' },
    { label: 'another route’s word', refusal: 'provider_token' },
    { label: 'the word padded', refusal: ' assertion' },
    { label: 'the word in an array', refusal: ['assertion'] },
    { label: 'no value', refusal: null },
  ])('hands a 401 whose refusal is $label to the probe', ({ refusal }) => {
    // Act
    const reading = cancellationFailureOf(
      httpError(401, { ...SESSION_REFUSAL, refusal }),
    );

    // Assert
    expect(reading).toBe('probe');
  });

  it.each([400, 403])(
    'reads a %i as a request this client could not use',
    (status) => {
      // Act
      const reading = cancellationFailureOf(httpError(status));

      // Assert
      // Refused before the handler ran: nothing was judged, so the erasure is
      // still scheduled, and a reload is the remedy. A 403 is also what a
      // locked session gets from the fallback policy.
      expect(reading).toBe('unrecognised');
    },
  );

  it('reads a 403 as unrecognised even when it carries the assertion word', () => {
    // Act
    const reading = cancellationFailureOf(
      httpError(403, { ...ASSERTION_REFUSAL, status: 403 }),
    );

    // Assert
    // The word earns `refused` only on the gate's own status.
    expect(reading).toBe('unrecognised');
  });

  it.each([
    404, 405, 406, 408, 409, 410, 413, 418, 422, 429, 500, 502, 503, 504,
  ])(
    'reads a %i, which the route never answers before deciding, as not knowing',
    (status) => {
      // Act
      const reading = cancellationFailureOf(httpError(status));

      // Assert
      // A `status < 500` mapping would say *still scheduled* over a proxy's
      // 408 or 429, which prove nothing about whether the request reached the
      // handler and committed. And an unlisted 4xx — a 406, a 410, a 422 —
      // is not a 400's cousin: only a status the route is known to answer
      // before deciding earns *still scheduled*.
      expect(reading).toBe('undetermined');
    },
  );

  it('reads a 500 carrying the assertion word as not knowing', () => {
    // Act
    const reading = cancellationFailureOf(
      httpError(500, { ...ASSERTION_REFUSAL, status: 500 }),
    );

    // Assert
    expect(reading).toBe('undetermined');
  });

  it('reads a request that got no answer at all as not knowing', () => {
    // Arrange
    const error = new HttpErrorResponse({
      error: new ProgressEvent('error'),
      status: 0,
      statusText: 'Unknown Error',
    });

    // Act
    const reading = cancellationFailureOf(error);

    // Assert
    expect(reading).toBe('undetermined');
  });

  // **An `HttpErrorResponse` or nothing.** A plain object shaped like the
  // gate's answer is not the gate's answer, and reading `.status` and
  // `.error.refusal` off anything handed in would tell somebody the erasure is
  // still scheduled on the strength of a shape.
  it.each([
    { label: 'an Error', error: new Error('something nobody predicted') },
    { label: 'null', error: null },
    { label: 'undefined', error: undefined },
    { label: 'a string', error: 'a string' },
    { label: 'a bare 401', error: { status: 401 } },
    {
      label: 'a plain object shaped like the refusal',
      error: { status: 401, error: { refusal: 'assertion' } },
    },
  ])('reads $label as not knowing', ({ error }) => {
    // Act
    const reading = cancellationFailureOf(error);

    // Assert
    expect(reading).toBe('undetermined');
  });
});
