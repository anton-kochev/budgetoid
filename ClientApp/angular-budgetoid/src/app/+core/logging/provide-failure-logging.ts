import {
  ErrorHandler,
  makeEnvironmentProviders,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  type EnvironmentProviders,
} from '@angular/core';
import { OAuthLogger } from 'angular-oauth2-oidc';
import { FailureErrorHandler } from './failure-error-handler';
import { FailureOAuthLogger } from './failure-oauth-logger';

// zone.js prints an error escaping a zone task itself, bypassing every handler
// registered here, unless this flag is set on the `Zone` constructor. The key
// is spelled the way zone.js's `__symbol__` spells it — a page-set
// `__Zone_symbol_prefix` when there is one, its default otherwise — so the
// flag lands where zone.js reads it.
function silenceZoneUncaughtErrorPrint(): void {
  const zone: unknown = Reflect.get(globalThis, 'Zone');
  if (typeof zone !== 'function') {
    return;
  }

  const prefix: unknown = Reflect.get(globalThis, '__Zone_symbol_prefix');
  const key = `${typeof prefix === 'string' && prefix !== '' ? prefix : '__zone_symbol__'}ignoreConsoleErrorUncaughtError`;
  Reflect.set(zone, key, true);
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
    provideAppInitializer(silenceZoneUncaughtErrorPrint),
  ]);
}
