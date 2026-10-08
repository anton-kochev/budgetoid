import { HttpErrorResponse } from '@angular/common/http';
import type { OAuthLogger } from 'angular-oauth2-oidc';
import { afterEach, beforeEach, describe, it, vi } from 'vitest';
import {
  expectOneErrorLine,
  expectSilenceExcept,
  spyOnEveryConsoleMethod,
  type ConsoleSpies,
} from '../../../testing/console-spies';
import { FailureOAuthLogger } from './failure-oauth-logger';

const EMAIL = 'alice@example.test';
const SUBJECT = '109876543210987654321';

// Shaped like what the library hands its logger: `tryLogin` debugs the parsed
// redirect fragment, raw `id_token` and all, and the token's payload is where
// the subject and the address live. Built from entries because the wire's
// snake_case keys are what matter and the naming rule refuses them as literals.
const PARSED_REDIRECT: Readonly<Record<string, string>> = Object.fromEntries([
  ['access_token', `token-for-${SUBJECT}`],
  ['id_token', `header.${EMAIL}.${SUBJECT}.signature`],
  ['state', SUBJECT],
]);

describe('FailureOAuthLogger', () => {
  let spies: ConsoleSpies;

  beforeEach(() => {
    spies = spyOnEveryConsoleMethod();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it.each(['debug', 'info', 'log'] as const)(
    'prints nothing for %s',
    (level) => {
      // Arrange — typed as the library holds it: these levels take no
      // parameters here, and the library still hands them the redirect.
      const logger: OAuthLogger = new FailureOAuthLogger();

      // Act
      logger[level]('parsed url', PARSED_REDIRECT);

      // Assert — silence on every channel, not only the one of the same name.
      expectSilenceExcept(spies);
    },
  );

  it('prints a fixed reason alone for a warning that is only a sentence', () => {
    // Arrange — the library's own wording puts a token's claim in the
    // sentence (`'Wrong audience: ' + claims.aud`), so the sentence is the one
    // thing that cannot be printed. The value here is only a stand-in for
    // something that must not reach the console.
    const logger = new FailureOAuthLogger();

    // Act
    logger.warn(`Wrong audience: ${SUBJECT}`);

    // Assert — a sentence is not a cause, so there is nothing to project.
    expectOneErrorLine(spies, 'OAuth warning');
  });

  it('prints a fixed reason for an error, and the error as its projection', () => {
    // Arrange
    const logger = new FailureOAuthLogger();

    // Act
    logger.error(new Error(EMAIL), PARSED_REDIRECT);

    // Assert — a plain `Error`'s name is not on the allow-list, and the second
    // argument is not printed at all.
    expectOneErrorLine(spies, 'OAuth error', { kind: 'error' });
  });

  it('projects the HTTP status the library hands over after its sentence', () => {
    // Arrange — the library's own call shape: a fixed sentence first, the
    // failed response second (the discovery-document load, for one).
    const logger = new FailureOAuthLogger();

    // Act
    logger.error(
      'error loading discovery document',
      new HttpErrorResponse({ status: 503, url: EMAIL }),
    );

    // Assert
    expectOneErrorLine(spies, 'OAuth error', { kind: 'http', status: 503 });
  });

  it('projects the first argument that is not a sentence, not the last', () => {
    // Arrange
    const logger = new FailureOAuthLogger();

    // Act
    logger.error(
      'error loading discovery document',
      new HttpErrorResponse({ status: 503, url: EMAIL }),
      new Error(EMAIL),
    );

    // Assert
    expectOneErrorLine(spies, 'OAuth error', { kind: 'http', status: 503 });
  });
});
