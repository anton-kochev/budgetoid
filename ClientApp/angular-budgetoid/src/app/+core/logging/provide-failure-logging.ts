import {
  ErrorHandler,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
  provideBrowserGlobalErrorListeners,
  type EnvironmentProviders,
} from '@angular/core';
import { OAuthLogger } from 'angular-oauth2-oidc';
import { FailureErrorHandler } from './failure-error-handler';
import { FailureOAuthLogger } from './failure-oauth-logger';
import { logFailure } from './log-failure';

// zone.js keeps two things on the `Zone` constructor that decide what happens
// to a rejection nobody handled, and both are taken over here:
//
// - `ignoreConsoleErrorUncaughtError` — unset, zone.js prints the rejection
//   itself, message and all, bypassing every handler registered here.
// - `unhandledPromiseRejectionHandler` — what zone.js calls for a rejection no
//   zone claimed, which is every one raised outside Angular's zone (inside it,
//   Angular's zone claims the rejection and hands it to the `ErrorHandler`, and
//   this is never called). Its default re-dispatches a `PromiseRejectionEvent`
//   built with `promise: undefined`, because zone.js hands over the rejection
//   itself rather than its wrapper; the constructor refuses a missing
//   `promise`, zone.js swallows that throw, and the rejection vanishes without
//   a line. zone.js calls it with that one value, which is what is projected.
//
// Both keys are spelled the way zone.js's `__symbol__` spells them — a
// page-set `__Zone_symbol_prefix` when there is one, its default otherwise —
// so they land where zone.js reads them. Set when the environment is created,
// not by an app initializer: environment initializers run first, and a failure
// between the two would otherwise meet zone.js's defaults.
function claimZoneUnhandledRejections(): void {
  const zone: unknown = Reflect.get(globalThis, 'Zone');
  if (typeof zone !== 'function') {
    return;
  }

  const configured: unknown = Reflect.get(globalThis, '__Zone_symbol_prefix');
  const prefix =
    typeof configured === 'string' && configured !== ''
      ? configured
      : '__zone_symbol__';
  Reflect.set(zone, `${prefix}ignoreConsoleErrorUncaughtError`, true);
  Reflect.set(
    zone,
    `${prefix}unhandledPromiseRejectionHandler`,
    (rejection: unknown): void => {
      logFailure('Unhandled rejection', rejection);
    },
  );
}

/**
 * Routes every failure the application does not catch — Angular's, the OAuth
 * library's, the window's and zone.js's — through `logFailure`.
 */
export function provideFailureLogging(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: ErrorHandler, useClass: FailureErrorHandler },
    { provide: OAuthLogger, useClass: FailureOAuthLogger },
    // Claims the window's `error` and `unhandledrejection` events, so the
    // browser prints nothing of its own and the handler above sees them.
    provideBrowserGlobalErrorListeners(),
    provideEnvironmentInitializer(claimZoneUnhandledRejections),
  ]);
}
