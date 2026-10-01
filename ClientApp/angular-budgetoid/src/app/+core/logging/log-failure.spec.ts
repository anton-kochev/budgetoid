import { HttpErrorResponse } from '@angular/common/http';
import { FactorManifestWireError } from '@app-core/security/factor-manifest';
import { KeyRotationMaterialError } from '@app-core/security/key-rotation-material';
import { NarrativeFieldMisuseError } from '@app-core/security/narrative-cipher';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  expectOneErrorLine,
  spyOnEveryConsoleMethod,
  type ConsoleSpies,
} from '../../../testing/console-spies';
import { logFailure } from './log-failure';

// The three things no log record may carry (FR-034/FR-035): an email, a
// credential subject identifier, and a narrative value. Every hostile cause in
// this file is built out of them, and no case searches the output for them —
// each asserts the exact arguments instead, so a leak through a channel nobody
// thought of is a red bar rather than a missed substring.
const EMAIL = 'alice@example.test';
const SUBJECT = '109876543210987654321';
const NARRATIVE = 'Rent for Alice';

// An API failure carrying all three wherever a response can: the body, the
// URL and the status text.
function hostileResponse(status: number): HttpErrorResponse {
  return new HttpErrorResponse({
    error: { detail: `${NARRATIVE} for ${EMAIL}`, email: EMAIL, sub: SUBJECT },
    status,
    statusText: `${NARRATIVE} ${EMAIL}`,
    url: `https://api.budgetoid.app/api/accounts?email=${EMAIL}&sub=${SUBJECT}`,
  });
}

describe('logFailure', () => {
  let spies: ConsoleSpies;

  beforeEach(() => {
    spies = spyOnEveryConsoleMethod();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('an HTTP failure', () => {
    it('prints the reason and the status, and nothing the response carried', () => {
      // Arrange
      const cause = hostileResponse(404);

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', {
        kind: 'http',
        status: 404,
      });
    });

    it('keeps a status of 0, the network failure, as a status', () => {
      // Arrange — `0` is falsy, so a projection that writes `status || …` or
      // drops a falsy member loses the one number that says "never reached".
      const cause = hostileResponse(0);

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', {
        kind: 'http',
        status: 0,
      });
    });
  });

  describe('an Error', () => {
    it('drops a name that is not on the allow-list', () => {
      // Arrange — `name` is writable on any Error, so it is a free-text field
      // like `message`, and whatever wrote it may have written an address.
      const cause = new Error(`${NARRATIVE} ${SUBJECT}`);
      cause.name = EMAIL;

      // Act
      logFailure('Accounts load failed', cause);

      // Assert — no `name` key at all, not `name: undefined`.
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'error' });
    });

    it('drops the name of a plain Error, which the allow-list does not carry', () => {
      // Arrange
      const cause = new Error(EMAIL);

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'error' });
    });

    it.each([
      ['TypeError', (): Error => new TypeError(EMAIL)],
      [
        'NotAllowedError',
        (): Error => new DOMException(EMAIL, 'NotAllowedError'),
      ],
      [
        'OperationError',
        (): Error => new DOMException(EMAIL, 'OperationError'),
      ],
      ['AbortError', (): Error => new DOMException(EMAIL, 'AbortError')],
      ['TimeoutError', (): Error => new DOMException(EMAIL, 'TimeoutError')],
      [
        'NarrativeFieldMisuseError',
        (): Error => new NarrativeFieldMisuseError(NARRATIVE),
      ],
      [
        'FactorManifestWireError',
        (): Error => new FactorManifestWireError(SUBJECT),
      ],
      [
        'KeyRotationMaterialError',
        (): Error => new KeyRotationMaterialError('inconsistent', EMAIL),
      ],
    ])('keeps the allow-listed name %s', (name, make) => {
      // Arrange
      const cause = make();

      // Act
      logFailure('Accounts load failed', cause);

      // Assert — the name and nothing else; the message carried a sentinel.
      expectOneErrorLine(spies, 'Accounts load failed', {
        kind: 'error',
        name,
      });
    });

    it('prints the name it checked, not a second read of it', () => {
      // Arrange — a getter that answers an allow-listed name once and an
      // address ever after. A projection that checks one read and prints
      // another prints the address.
      const cause = new Error(NARRATIVE);
      let reads = 0;
      Object.defineProperty(cause, 'name', {
        get: (): string => (reads++ === 0 ? 'TypeError' : EMAIL),
      });

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', {
        kind: 'error',
        name: 'TypeError',
      });
    });

    it('drops the name HttpErrorResponse on an Error, which only a real response may answer to', () => {
      // Arrange — a real `HttpErrorResponse` is not an `Error` (it extends
      // `HttpResponseBase`) and is projected by status before names are read,
      // so the only thing that name could still let through is an Error that
      // was given it.
      const cause = new Error(EMAIL);
      cause.name = 'HttpErrorResponse';

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'error' });
    });

    it('drops a DOMException name that is not on the allow-list', () => {
      // Arrange — the constructor takes any string as the name.
      const cause = new DOMException('x', 'AliceSmithError');

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'error' });
    });
  });

  describe('anything else', () => {
    it.each([
      { label: 'a thrown string', make: (): unknown => `${EMAIL} ${SUBJECT}` },
      {
        label: 'a plain object shaped like an allow-listed error',
        make: (): unknown => ({ message: EMAIL, name: 'TypeError' }),
      },
      { label: 'null', make: (): unknown => null },
      // Only a real `HttpErrorResponse` is an HTTP failure. A projection that
      // recognises one by its shape prints whatever sits under `status`.
      {
        label: 'a plain object shaped like an HTTP failure',
        make: (): unknown => ({ status: EMAIL, url: SUBJECT }),
      },
    ])('projects $label as a non-error', ({ make }) => {
      // Arrange
      const cause = make();

      // Act
      logFailure('Accounts load failed', cause);

      // Assert
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'non-error' });
    });

    it('survives an Error whose name getter throws, and calls it a non-error', () => {
      // Arrange — the getter's own exception carries a sentinel too, so a
      // projection that catches and prints it is as red as one that rethrows.
      const cause = new Error(EMAIL);
      Object.defineProperty(cause, 'name', {
        get: (): never => {
          throw new Error(SUBJECT);
        },
      });

      // Act
      const act = (): void => logFailure('Accounts load failed', cause);

      // Assert
      expect(act).not.toThrow();
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'non-error' });
    });

    it('survives a revoked Proxy, and calls it a non-error', () => {
      // Arrange — every operation on a revoked proxy throws a TypeError,
      // `instanceof` included.
      const { proxy, revoke } = Proxy.revocable(new Error(EMAIL), {});
      revoke();

      // Act
      const act = (): void => logFailure('Accounts load failed', proxy);

      // Assert
      expect(act).not.toThrow();
      expectOneErrorLine(spies, 'Accounts load failed', { kind: 'non-error' });
    });
  });

  describe('the reason', () => {
    it('is the only argument when there is no cause', () => {
      // Act
      logFailure('Accounts load failed');

      // Assert — one argument, not `(reason, undefined)`.
      expectOneErrorLine(spies, 'Accounts load failed');
    });
  });
});

// A reason widened to `string` does not compile. A `string` can be built from
// anything, an address included; only a literal is known to be the author's
// own words. This is a compile-time pin and deliberately not a case: nothing at
// run time can observe a type, and a case around it would pass on any body. The
// unit-test builder type-checks every spec, so the day the signature accepts
// `string` this directive goes unused and the build fails on it.
function widenedReasonIsRefused(widened: string): void {
  // @ts-expect-error a widened string is not a reason
  logFailure(widened);
}
void widenedReasonIsRefused;

// The same hole through a template. A template with a `string` hole infers the
// pattern type `Accounts failed for ${string}`, which is not `string`, so a
// check written only against `string` lets it through — with the address, or
// whatever `widened` holds, spliced into the printed line.
function templatedReasonIsRefused(widened: string): void {
  // @ts-expect-error a reason built from a string is not a reason
  logFailure(`Accounts failed for ${widened}`);
}
void templatedReasonIsRefused;

// And through a union that holds one such member beside a literal. A check
// that looks at the union as a whole finds the literal's key and lets the
// pattern ride along with it.
function mixedReasonIsRefused(
  mixed: 'Accounts load failed' | `Accounts failed for ${string}`,
): void {
  // @ts-expect-error one built member is enough to refuse the reason
  logFailure(mixed);
}
void mixedReasonIsRefused;
