// Everything the harness runs inside the page. esbuild bundles this file as an
// IIFE under the global name `narrativePerf`, and the Node driver drives it
// entirely through `Runtime.evaluate`.
//
// **It imports the real modules.** `narrative-cipher.ts`, `key-envelope.ts`,
// `base64url.ts`, `associated-data.ts`, `transaction-view.ts` and
// `narrative-batch.ts` arrive through the same relative graph the application
// compiles, not through a transcription. That is the whole point of measuring
// at this tier: an earlier evidence base transcribed a *lenient* base64url
// decoder beside the ciphers and came out roughly 60% low, because the
// repository's decoder is strict and the strictness costs real time per call.
//
// The page holds one non-extractable content key and the prepared fixtures for
// the length of a run and nothing else. It is a measuring rig, not a client:
// nothing here is imported by the app, and nothing here ships.
import { openNarrativeBatch } from '../../../src/app/+core/security/narrative-batch';
import type { NarrativeRequest } from '../../../src/app/+core/security/narrative-batch';
import { openNarrativeField } from '../../../src/app/+core/security/narrative-cipher';
import type { NarrativeOpener } from '../../../src/app/+core/security/narrative-text';
import {
  toTransactionView,
  transactionNarrativeRequests,
} from '../../../src/app/transactions/transaction-view';
import type {
  Environment,
  FixtureInfo,
  FixtureShape,
  LongTask,
  MeasuredPath,
  RunSample,
} from '../shapes.ts';
import { computeBenchmarkIndex } from './benchmark-index.ts';
import { buildFixture, createContentKey, type Fixture } from './fixture.ts';

/** Lighthouse's BenchmarkIndex, re-exported so the driver can calibrate. */
export function benchmarkIndex(durationMs: number): number {
  return computeBenchmarkIndex(durationMs);
}

/** The facts a recorded number is not comparable without. */
export function environment(): Environment {
  return {
    hardwareConcurrency: navigator.hardwareConcurrency,
    schedulerPostTask:
      typeof (globalThis as { scheduler?: { postTask?: unknown } }).scheduler
        ?.postTask === 'function',
    secureContext: globalThis.isSecureContext,
    userAgent: navigator.userAgent,
  };
}

/**
 * Seals every fixture once, at whatever throttle is currently set, and keeps
 * them for the rest of the run.
 *
 * Sealing is deliberately outside the measured window. NFR-003 is about what a
 * *read* costs; the writes that produced the ciphertext happened on other days.
 */
export async function prepare(
  shapes: readonly FixtureShape[],
): Promise<readonly FixtureInfo[]> {
  const contentKey = await createContentKey();
  const open: NarrativeOpener = async (binding, wire) => ({
    state: 'text',
    value: await openNarrativeField(contentKey, wire, binding),
  });

  prepared = { fixtures: new Map(), open };

  for (const shape of shapes) {
    const fixture = await buildFixture(contentKey, shape);

    prepared.fixtures.set(shape.name, fixture);
  }

  return [...prepared.fixtures.values()].map((fixture) => fixture.info);
}

/**
 * Runs one fixture down one path `runs` times and returns every sample.
 *
 * One unrecorded warm-up run first, so the first sample is not paying for JIT
 * on code every later sample runs warm.
 */
export async function measure(
  fixtureName: string,
  path: MeasuredPath,
  runs: number,
): Promise<readonly RunSample[]> {
  const { fixture, open } = requirePrepared(fixtureName);

  await readPage(fixture, path, open);

  const samples: RunSample[] = [];

  for (let run = 0; run < runs; run++) {
    samples.push(await sampleOnce(() => readPage(fixture, path, open)));
  }

  return samples;
}

/**
 * The renderer's own rAF tick period, in the conditions a measurement runs in,
 * with no crypto anywhere.
 *
 * Without this a frame-gap number is unreadable: a gap is readable against the
 * band beside it and against nothing else. The band comes back flat on every
 * profile, the unthrottled one included — 16.7-16.8 ms on a 60 Hz renderer,
 * 33.4-33.5 ms on a 30 Hz one — so it is a tick period, not frames the rig
 * missed, and a perfect 16.7 ms is the expected reading rather than a
 * degenerate one.
 *
 * **One whole tick is the instrument's resolution.** "1.0x the idle worst" in
 * the report means only that no tick was missed. It says nothing about how much
 * of a frame the work held, and no arithmetic on that ratio recovers it.
 *
 * **Several windows, not one.** A single window cannot show whether the period
 * is steady on the rig in hand, which is the whole of what the band claims.
 */
export async function frameBaseline(
  durationMs: number,
  windows: number,
): Promise<readonly RunSample[]> {
  const samples: RunSample[] = [];

  for (let window = 0; window < windows; window++) {
    samples.push(
      await sampleOnce(async () => {
        await new Promise<void>((resolve) => {
          globalThis.setTimeout(resolve, durationMs);
        });
      }),
    );
  }

  return samples;
}

interface Prepared {
  readonly fixtures: Map<string, Fixture>;
  readonly open: NarrativeOpener;
}

let prepared: Prepared | undefined;

function requirePrepared(fixtureName: string): {
  readonly fixture: Fixture;
  readonly open: NarrativeOpener;
} {
  const fixture = prepared?.fixtures.get(fixtureName);

  if (prepared === undefined || fixture === undefined) {
    throw new Error(`No fixture named ${fixtureName} has been prepared.`);
  }

  return { fixture, open: prepared.open };
}

// The two paths, and the only difference between them is which opener the
// mappers get. `batch` is what `transactions.service.ts` does; `per-row` hands
// every mapper the raw opener, so the same payee is opened once per row and
// nothing ever yields the frame.
async function readPage(
  fixture: Fixture,
  path: MeasuredPath,
  open: NarrativeOpener,
): Promise<number> {
  if (path === 'per-row') {
    const views = await Promise.all(
      fixture.rows.map(async (row) => await toTransactionView(row, open)),
    );

    return views.length;
  }

  const requests: NarrativeRequest[] = [];

  for (const row of fixture.rows) {
    requests.push(...transactionNarrativeRequests(row));
  }

  const fromBatch = await openNarrativeBatch(requests, open);
  const views = await Promise.all(
    fixture.rows.map(async (row) => await toTransactionView(row, fromBatch)),
  );

  return views.length;
}

// One measured window: a rAF loop and a `longtask` observer around the work.
//
// **The window opens on a task boundary.** Without the yield, a run and the
// warm-up before it share one task, and the long task the observer reports
// starts before the clock did — an entry with a negative offset, covering work
// nobody asked about. One `MessageChannel` message ends the current task for a
// tenth of a millisecond, which is the same mechanism `narrative-batch.ts`
// falls back to and for the same reason.
//
// **The clock stops before the settle waits, and the frame watcher does not.**
// The gap that matters is the one that *spans* the work, and its closing edge
// is a callback that cannot fire until the main thread is free — which is after
// the work has finished. Reading the stamps synchronously at that moment loses
// exactly the frame the measurement is about, and reports a suspiciously tidy
// zero. Long-task entries arrive late for the same reason, so the observer
// stays connected across a short settle too.
//
// **Both of those are released in a `finally`, and neither was.** A `work()`
// that rejects — a fixture that will not seal, an opener that refuses — used to
// leave the rAF loop re-arming for the life of the page and the observer
// connected beside it, once per failed sample, on the page that is about to be
// asked for a timing. The failure itself still propagates: this rig records and
// never rescues.
async function sampleOnce(work: () => Promise<unknown>): Promise<RunSample> {
  const longTasks: LongTask[] = [];
  const observer = new PerformanceObserver((list) => {
    for (const entry of list.getEntries()) {
      longTasks.push({
        durationMs: entry.duration,
        startMs: entry.startTime,
      });
    }
  });

  await endTheCurrentTask();
  observer.observe({ entryTypes: ['longtask'] });

  const frames = await watchFrames();

  try {
    const startedAt = performance.now();

    await work();

    const totalMs = performance.now() - startedAt;
    const worstFrameGapMs = await frames.stop(performance.now());

    await new Promise<void>((resolve) => {
      globalThis.setTimeout(resolve, LONG_TASK_SETTLE_MS);
    });

    return {
      longTasks: longTasks
        .filter(
          (task) =>
            task.startMs + task.durationMs >= startedAt &&
            task.startMs <= startedAt + totalMs,
        )
        .map((task) => ({
          durationMs: task.durationMs,
          startMs: task.startMs - startedAt,
        })),
      totalMs,
      worstFrameGapMs,
    };
  } finally {
    // On the way out of a successful sample both of these have already
    // happened, which is the point of them being idempotent rather than
    // conditional: the happy path is unchanged and nothing waits twice for a
    // closing frame.
    frames.abandon();
    observer.disconnect();
  }
}

/** How long to wait for `longtask` entries after the work has finished. */
const LONG_TASK_SETTLE_MS = 60;

/** How long to wait for the frame that closes the measured window. */
const CLOSING_FRAME_TIMEOUT_MS = 400;

/** How long one wait for that frame lasts before it is retried. */
const CLOSING_FRAME_POLL_MS = 50;

// A frame, or a timeout if this browser is producing none. Racing them matters:
// a headless renderer with nothing to composite can stop scheduling frames
// altogether, and a bare `await requestAnimationFrame` would then hang the run
// rather than record that no frame arrived.
function nextFrameOrTimeout(timeoutMs: number): Promise<void> {
  return new Promise<void>((resolve) => {
    let settled = false;
    const done = (): void => {
      if (!settled) {
        settled = true;
        resolve();
      }
    };

    requestAnimationFrame(done);
    globalThis.setTimeout(done, timeoutMs);
  });
}

function endTheCurrentTask(): Promise<void> {
  return new Promise<void>((resolve) => {
    const channel = new MessageChannel();

    channel.port1.onmessage = (): void => {
      channel.port1.close();
      channel.port2.close();
      resolve();
    };
    channel.port2.postMessage(undefined);
  });
}

interface FrameWatch {
  /**
   * Waits for the first callback after `endedAt`, then reports the worst gap
   * between two consecutive callbacks that straddled the measured window.
   */
  stop(endedAt: number): Promise<number>;
  /**
   * Stops the loop re-arming and waits for nothing.
   *
   * For the failure path: there is no gap to report and no closing frame worth
   * waiting on, and the loop has to stop all the same. Idempotent, so the
   * successful path can call it after {@link stop} without a second wait.
   */
  abandon(): void;
}

// Resolves once the loop has its first stamp, so the window opens on a known
// frame rather than somewhere inside one.
async function watchFrames(): Promise<FrameWatch> {
  const stamps: number[] = [];
  let running = true;

  const tick = (timestamp: number): void => {
    stamps.push(timestamp);

    if (running) {
      requestAnimationFrame(tick);
    }
  };

  await new Promise<void>((resolve) => {
    let opened = false;
    const open = (timestamp?: number): void => {
      if (opened) {
        return;
      }

      opened = true;

      if (timestamp !== undefined) {
        tick(timestamp);
      } else {
        requestAnimationFrame(tick);
      }

      resolve();
    };

    requestAnimationFrame(open);
    globalThis.setTimeout(() => {
      open();
    }, CLOSING_FRAME_POLL_MS);
  });

  return {
    abandon(): void {
      running = false;
    },
    async stop(endedAt: number): Promise<number> {
      const deadline = performance.now() + CLOSING_FRAME_TIMEOUT_MS;

      while (
        (stamps[stamps.length - 1] ?? 0) < endedAt &&
        performance.now() < deadline
      ) {
        await nextFrameOrTimeout(CLOSING_FRAME_POLL_MS);
      }

      running = false;

      let worst = 0;

      for (let index = 1; index < stamps.length; index++) {
        const opened = stamps[index - 1] ?? 0;

        // Gaps that begin after the work ended belong to the settle, not to the
        // measurement.
        if (opened <= endedAt) {
          worst = Math.max(worst, (stamps[index] ?? 0) - opened);
        }
      }

      return worst;
    },
  };
}
