// How an erasing request that did not answer 204 is read, and the three words
// the dialog says it in. See docs/design/components.md, "Erasure dialog", *The
// status region, and what each line says*.
//
// **The table is the point, and one row of it is dangerous in a direction a
// reader will not expect.** `undetermined` is the only word whose sentence does
// not say *nothing was erased*, because a lost response, a `5xx` and a network
// failure after the request left all mean the erasure may have committed. A
// mapping that filed any of those under `refused` or `unrecognised` would tell a
// person whose account is gone that nothing happened — so every shape of
// "we can't know" is pinned to `undetermined` below, including the ones a
// `status >= 500` check misses.
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { erasureFailureOf } from './erasure-outcome';

function httpError(status: number): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'test' });
}

describe('erasureFailureOf', () => {
  it('reads a 401 as refused', () => {
    // Act
    const failure = erasureFailureOf(httpError(401));

    // Assert
    // The gate's refusals are byte-identical by design, so this is one word
    // for an expired challenge, a bad signature and another account's passkey.
    // It also covers a session that had already ended — expired, revoked,
    // erased elsewhere — which the route's fallback authorization policy
    // answers 401 before the gate. None of them erased anything through this
    // request.
    expect(failure).toBe('refused');
  });

  it.each([400, 403])(
    'reads a %i as a request this client could not use',
    (status) => {
      // Act
      const failure = erasureFailureOf(httpError(status));

      // Assert
      // Raised before the handler is entered — nothing about the account was
      // judged, so nothing was erased, and a reload is the remedy.
      expect(failure).toBe('unrecognised');
    },
  );

  it.each([500, 502, 503, 504, 599])(
    'reads a %i as not knowing whether the account is gone',
    (status) => {
      // Act
      const failure = erasureFailureOf(httpError(status));

      // Assert
      // A gateway timeout in particular is the erasure committing behind a
      // proxy that gave up waiting for it.
      expect(failure).toBe('undetermined');
    },
  );

  it.each([404, 405, 408, 409, 413, 429])(
    'reads a %i, which the gate never answers, as not knowing',
    (status) => {
      // Act
      const failure = erasureFailureOf(httpError(status));

      // Assert
      // Only the three statuses the server is known to answer *before* it
      // erases anything earn a sentence ending *nothing was erased*. A mapping
      // that read the whole `4xx` band as a refusal — `status < 500` — would
      // say it over a proxy's `408` or `429`, which prove nothing about
      // whether the request reached the handler.
      expect(failure).toBe('undetermined');
    },
  );

  it('reads a request that got no answer at all as not knowing', () => {
    // Arrange
    // Status 0 is the browser's word for "no response arrived" — the request
    // may well have reached the server and committed.
    const error = new HttpErrorResponse({
      error: new ProgressEvent('error'),
      status: 0,
      statusText: 'Unknown Error',
    });

    // Act
    const failure = erasureFailureOf(error);

    // Assert
    expect(failure).toBe('undetermined');
  });

  it('reads a rejection that is not an HTTP answer as not knowing', () => {
    // Arrange
    // Anything thrown between the request leaving and its answer being read —
    // a timeout operator, a parser, a bug. None of it says the erasure did not
    // happen.
    const error = new Error('something nobody predicted');

    // Act
    const failure = erasureFailureOf(error);

    // Assert
    expect(failure).toBe('undetermined');
  });

  it.each([null, undefined, 'a string', { status: 401 }])(
    'reads %s as not knowing, never as a refusal',
    (error) => {
      // Act
      const failure = erasureFailureOf(error);

      // Assert
      // `{ status: 401 }` is the near miss: a plain object carrying a status
      // is not the gate's answer, and a mapping that read `.status` off
      // anything it was handed would call it `refused` — *nothing was erased*
      // — on the strength of a shape.
      expect(failure).toBe('undetermined');
    },
  );
});
