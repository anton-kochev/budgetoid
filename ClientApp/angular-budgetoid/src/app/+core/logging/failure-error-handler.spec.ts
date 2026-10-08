import { HttpErrorResponse } from '@angular/common/http';
import { afterEach, beforeEach, describe, it, vi } from 'vitest';
import {
  expectOneErrorLine,
  spyOnEveryConsoleMethod,
  type ConsoleSpies,
} from '../../../testing/console-spies';
import { FailureErrorHandler } from './failure-error-handler';

const EMAIL = 'alice@example.test';
const SUBJECT = '109876543210987654321';
const NARRATIVE = 'Rent for Alice';

describe('FailureErrorHandler', () => {
  let spies: ConsoleSpies;

  beforeEach(() => {
    spies = spyOnEveryConsoleMethod();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('prints one projected line for an unhandled error, never Angular’s default dump', () => {
    // Arrange — Angular's own handler prints `('ERROR', error)`, which hands
    // the whole response, body and URL included, to the console.
    const handler = new FailureErrorHandler();
    const error = new HttpErrorResponse({
      error: { detail: NARRATIVE, email: EMAIL, sub: SUBJECT },
      status: 500,
      statusText: `${NARRATIVE} ${EMAIL}`,
      url: `https://api.budgetoid.app/api/me?email=${EMAIL}&sub=${SUBJECT}`,
    });

    // Act
    handler.handleError(error);

    // Assert
    expectOneErrorLine(spies, 'Unhandled error', { kind: 'http', status: 500 });
  });
});
