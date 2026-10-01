// A Chrome DevTools Protocol client in about a hundred lines, over Node 22's
// global `WebSocket`.
//
// **This is why the harness has no dependencies.** `puppeteer` and `playwright`
// each download a browser at install time, and this repository already refuses
// third-party origins at runtime; arguing about whether CON-010 reaches
// devDependencies is an argument nobody has to have if the tool speaks the
// protocol itself. Everything the harness needs is four commands — attach,
// navigate, throttle, evaluate — and none of them need a driver.
import { setTimeout as delay } from 'node:timers/promises';

/**
 * An open protocol connection. Commands are correlated by id, events ignored,
 * and every command carries the deadline {@link COMMAND_TIMEOUT_MS} sets.
 */
export interface Cdp {
  send(
    method: string,
    params?: Readonly<Record<string, unknown>>,
    sessionId?: string,
  ): Promise<unknown>;
  close(): void;
}

/** Opens the browser-level endpoint Chrome printed on its stderr. */
export async function connect(endpoint: string): Promise<Cdp> {
  const socket = new WebSocket(endpoint);
  const pending = new Map<number, Pending>();
  let nextId = 1;
  let closed = false;

  await new Promise<void>((resolve, reject) => {
    socket.addEventListener('open', () => {
      resolve();
    });
    socket.addEventListener('error', () => {
      reject(new Error(`Could not open a DevTools connection to ${endpoint}.`));
    });
  });

  socket.addEventListener('message', (event: MessageEvent<unknown>) => {
    if (typeof event.data !== 'string') {
      return;
    }

    const message: unknown = JSON.parse(event.data);
    const id = numberAt(message, 'id');

    if (id === undefined) {
      return;
    }

    const waiting = pending.get(id);

    pending.delete(id);

    if (waiting === undefined) {
      return;
    }

    const failure = propertyAt(message, 'error');

    if (failure !== undefined) {
      waiting.reject(
        new Error(stringAt(failure, 'message') ?? 'Unknown protocol error.'),
      );

      return;
    }

    waiting.resolve(propertyAt(message, 'result'));
  });

  socket.addEventListener('close', () => {
    closed = true;

    for (const waiting of pending.values()) {
      waiting.reject(new Error('The DevTools connection closed.'));
    }

    pending.clear();
  });

  return {
    close(): void {
      closed = true;
      socket.close();
    },
    async send(method, params, sessionId): Promise<unknown> {
      if (closed) {
        throw new Error(`The DevTools connection closed before ${method}.`);
      }

      const id = nextId++;
      const answer = new Promise<unknown>((resolve, reject) => {
        pending.set(id, { reject, resolve });
      });

      socket.send(
        JSON.stringify(
          sessionId === undefined
            ? { id, method, params: params ?? {} }
            : { id, method, params: params ?? {}, sessionId },
        ),
      );

      return await withDeadline(answer, () => {
        // Dropped from `pending` as well, so an answer that arrives after the
        // deadline is ignored rather than resolving a promise nobody is holding.
        pending.delete(id);

        return new Error(
          `Chrome did not answer ${method} within ${COMMAND_TIMEOUT_MS} ms.`,
        );
      });
    },
  };
}

/**
 * How long any one command may wait for Chrome.
 *
 * **Every command has one, and the reason is the ones that are not `waitFor`.**
 * A command resolves only when Chrome answers, and {@link evaluate} sends
 * `awaitPromise`, so a renderer that has stopped scheduling tasks under a 10×
 * throttle never answers — and the socket and the harness's own listening server
 * each keep Node's event loop alive, so the process would sit there forever
 * rather than exiting non-zero. That is the one thing this tool's exit code is
 * for: whether the measurement happened.
 *
 * Two minutes, which is nowhere near any legitimate command. The longest single
 * `Runtime.evaluate` in a run is a pathological fixture sampled nine times under
 * the low-tier throttle — single-digit seconds — and the whole invocation is
 * minutes only because there are some four hundred of them.
 */
const COMMAND_TIMEOUT_MS = 120_000;

// Rejects with `giveUp()` if `answer` has not settled in time, and disposes of
// the timer either way: four hundred live two-minute timers would keep the
// process alive long past its last command.
async function withDeadline<T>(
  answer: Promise<T>,
  giveUp: () => Error,
): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;

  try {
    return await Promise.race([
      answer,
      new Promise<never>((resolve, reject) => {
        timer = setTimeout(() => {
          reject(giveUp());
        }, COMMAND_TIMEOUT_MS);
        timer.unref();
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Evaluates `expression` in the page and returns the value it produced.
 *
 * The one place in the harness that asserts a type over data crossing a wire.
 * Everything the page returns is built by `browser/entry.ts` out of the very
 * types named here, so the assertion restates a fact the two files already
 * share through `shapes.ts` — but the protocol hands it over as `unknown`, and
 * a runtime re-validation of a shape both ends compile against would be a
 * second definition of it.
 */
export async function evaluate<T>(
  cdp: Cdp,
  sessionId: string,
  expression: string,
): Promise<T> {
  const answer = await cdp.send(
    'Runtime.evaluate',
    { awaitPromise: true, expression, returnByValue: true },
    sessionId,
  );
  const failure = propertyAt(answer, 'exceptionDetails');

  if (failure !== undefined) {
    const thrown = propertyAt(failure, 'exception');

    throw new Error(
      `The page threw evaluating ${expression}: ${
        stringAt(thrown, 'description') ??
        stringAt(failure, 'text') ??
        'no detail'
      }`,
    );
  }

  return propertyAt(propertyAt(answer, 'result'), 'value') as T;
}

/** Waits for `expression` to answer `true`, or gives up. */
export async function waitFor(
  cdp: Cdp,
  sessionId: string,
  expression: string,
  timeoutMs: number,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (await evaluate<boolean>(cdp, sessionId, expression)) {
      return;
    }

    await delay(50);
  }

  throw new Error(`The page never satisfied ${expression}.`);
}

/** Reads a string off a protocol result, or says which one was missing. */
export function stringProperty(value: unknown, key: string): string {
  const found = stringAt(value, key);

  if (found === undefined) {
    throw new Error(`The protocol answer carried no ${key}.`);
  }

  return found;
}

interface Pending {
  reject(reason: Error): void;
  resolve(value: unknown): void;
}

function propertyAt(value: unknown, key: string): unknown {
  if (typeof value !== 'object' || value === null) {
    return undefined;
  }

  const record: Record<string, unknown> = { ...value };

  return record[key];
}

function stringAt(value: unknown, key: string): string | undefined {
  const found = propertyAt(value, key);

  return typeof found === 'string' ? found : undefined;
}

function numberAt(value: unknown, key: string): number | undefined {
  const found = propertyAt(value, key);

  return typeof found === 'number' ? found : undefined;
}
