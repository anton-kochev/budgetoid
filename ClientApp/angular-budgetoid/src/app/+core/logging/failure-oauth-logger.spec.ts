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
      // Arrange
      const logger = new FailureOAuthLogger();

      // Act
      logger[level]('parsed url', PARSED_REDIRECT);

      // Assert — silence on every channel, not only the one of the same name.
      expectSilenceExcept(spies);
    },
  );

  it('prints a fixed reason for a warning, and the text it was given as a non-error', () => {
    // Arrange — the library's own wording puts the token's subject in the
    // sentence, so the sentence is the one thing that cannot be printed.
    const logger = new FailureOAuthLogger();

    // Act
    logger.warn(`Wrong audience: ${SUBJECT}`);

    // Assert
    expectOneErrorLine(spies, 'OAuth warning', { kind: 'non-error' });
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
});
