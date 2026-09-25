// Every console channel, silenced and watched at once, for the specs that hold
// the logging rule: no record carries an email, a credential subject or a
// narrative value.
//
// **Every method, not only `error`.** A funnel that moved its detail from
// `console.error` to `console.debug`, `console.group` or `console.count` would
// leave an `error`-only spy green and the leak in the browser's console, so a
// case that owes one line owes silence on every other method too. "Every" is
// every function-valued key the runner's `console` has, found by walking it,
// not a list written here: a list is only as long as its author remembered.
//
// **Cleared on install.** Nothing in this project configures `restoreMocks`,
// and `vi.spyOn` on a method an earlier case already spied returns that spy,
// history and all — so without the clear an exact-count assertion reads calls
// some other case made. Each consumer still owes `vi.restoreAllMocks()` in an
// `afterEach`; the clear covers the neighbours that do not.
//
// This file is deliberately **not** a `*.spec.ts`: a spec imported by a spec
// registers its own cases inside the importer's suite.
import { expect, vi, type MockInstance } from 'vitest';

type ConsoleSpy = MockInstance<(...data: unknown[]) => void>;

export interface ConsoleSpies {
  /** The one channel `logFailure` writes to. */
  readonly error: ConsoleSpy;
  /** Every spied method, `error` included, by name. */
  readonly all: ReadonlyMap<string, ConsoleSpy>;
}

// The walk's floor. These are the methods any console has, so a walk that
// missed one of them — or found nothing at all — is broken, and a spec
// asserting silence over its result would pass on every body.
const EXPECTED_AT_LEAST = [
  'log',
  'info',
  'warn',
  'error',
  'debug',
  'trace',
  'dir',
  'table',
] as const;

// Own and inherited, because a console may keep its methods on a prototype
// rather than on itself. `Object.prototype` is where the walk stops: its
// methods are not channels. A leading underscore is skipped: the runner's
// console is a Node `Console`, which keeps `_stdoutErrorHandler` and
// `_stderrErrorHandler` on itself — its streams' error callbacks, not channels
// a caller writes to.
function functionValuedKeys(target: object): string[] {
  const names = new Set<string>();
  for (
    let level: object | null = target;
    level !== null && level !== Object.prototype;
    level = Reflect.getPrototypeOf(level)
  ) {
    for (const name of Object.getOwnPropertyNames(level)) {
      if (name !== 'constructor' && !name.startsWith('_')) {
        names.add(name);
      }
    }
  }

  return [...names]
    .filter((name) => typeof Reflect.get(target, name) === 'function')
    .sort();
}

/** Replaces every console method with a silent spy holding no history. */
export function spyOnEveryConsoleMethod(): ConsoleSpies {
  // Viewed as a record of methods: every name below came from walking this
  // object and passed a `typeof … === 'function'` check, which is what the
  // view claims. `Console`'s declared type cannot say it, because the walk
  // finds keys the declaration does not list.
  const channels = console as unknown as Record<
    string,
    (...data: unknown[]) => void
  >;
  const all = new Map<string, ConsoleSpy>();
  for (const name of functionValuedKeys(console)) {
    const installed = vi
      .spyOn(channels, name)
      .mockImplementation(() => undefined);
    installed.mockClear();
    all.set(name, installed);
  }

  expect([...all.keys()], 'the console methods spied').toEqual(
    expect.arrayContaining([...EXPECTED_AT_LEAST]),
  );

  const error = all.get('error');
  if (error === undefined) {
    throw new Error('console.error was not spied.');
  }

  return { error, all };
}

/**
 * Asserts the console received exactly one line, on `console.error`, whose
 * arguments are exactly `args` — the count, the values, and the absence of any
 * key `args` does not spell (`toHaveBeenCalledWith` alone treats a present
 * `undefined` as absent, which is the one difference that matters for a
 * projection that must not carry a `name`).
 */
export function expectOneErrorLine(
  spies: ConsoleSpies,
  ...args: readonly unknown[]
): void {
  expect(spies.error).toHaveBeenCalledTimes(1);
  expect(spies.error).toHaveBeenCalledWith(...args);
  expect(spies.error.mock.calls[0]).toStrictEqual(args);
  expectSilenceExcept(spies, 'error');
}

/** Asserts no console method but the ones named received anything. */
export function expectSilenceExcept(
  spies: ConsoleSpies,
  ...except: readonly (keyof Console)[]
): void {
  for (const [name, spy] of spies.all) {
    if (!except.some((excepted) => excepted === name)) {
      expect(spy, `console.${name}`).toHaveBeenCalledTimes(0);
    }
  }
}
