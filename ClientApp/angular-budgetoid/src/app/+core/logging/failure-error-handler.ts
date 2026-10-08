import { ErrorHandler, Injectable } from '@angular/core';
import { logFailure } from './log-failure';

// Replaces Angular's own handler, which prints `('ERROR', error)` — the whole
// error, response body and URL included. Deliberately no `super` call: that is
// the print being replaced.
@Injectable()
export class FailureErrorHandler implements ErrorHandler {
  public handleError(error: unknown): void {
    logFailure('Unhandled error', error);
  }
}
