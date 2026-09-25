import { Injectable } from '@angular/core';
import { OAuthLogger } from 'angular-oauth2-oidc';
import { logFailure } from './log-failure';

// The OAuth library's logger. Its default is `console`, and what it logs is
// the redirect it parsed — raw tokens, whose payload is the subject and the
// address. The chatty levels print nothing; the two failure levels print a
// fixed reason and a projection of their first argument, and never the rest.
@Injectable()
export class FailureOAuthLogger extends OAuthLogger {
  public debug(...args: unknown[]): void {
    return;
  }

  public info(...args: unknown[]): void {
    return;
  }

  public log(...args: unknown[]): void {
    return;
  }

  public warn(first?: unknown, ...rest: unknown[]): void {
    logFailure('OAuth warning', first);
  }

  public error(first?: unknown, ...rest: unknown[]): void {
    logFailure('OAuth error', first);
  }
}
