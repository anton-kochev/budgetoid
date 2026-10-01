import { Injectable } from '@angular/core';
import { OAuthLogger } from 'angular-oauth2-oidc';
import { logFailure } from './log-failure';

// The OAuth library's logger. Its default is `console`, and what it logs is
// the redirect it parsed — raw tokens, whose payload is the subject and the
// address. The chatty levels print nothing. The two failure levels print a
// fixed reason and never a string they were handed: the library leads with a
// sentence (`'error loading discovery document', err`), and some sentences
// are built from a token's claims. What they project is the first argument
// that is not a string — the failed response or error the sentence is about —
// and nothing at all when every argument is a sentence.
@Injectable()
export class FailureOAuthLogger extends OAuthLogger {
  public debug(): void {
    return;
  }

  public info(): void {
    return;
  }

  public log(): void {
    return;
  }

  public warn(...args: unknown[]): void {
    logWithCause('OAuth warning', args);
  }

  public error(...args: unknown[]): void {
    logWithCause('OAuth error', args);
  }
}

// An index rather than `find`, so an `undefined` argument is still a cause and
// prints as a `non-error`, the way `logFailure` keeps the two apart.
function logWithCause(
  reason: 'OAuth warning' | 'OAuth error',
  args: readonly unknown[],
): void {
  const index = args.findIndex((arg) => typeof arg !== 'string');
  if (index === -1) {
    logFailure(reason);

    return;
  }

  logFailure(reason, args[index]);
}
