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
import {
  NarrativeFieldMisuseError,
  openNarrativeField,
  refuseInvalidBinding,
} from '../../../src/app/+core/security/narrative-cipher';
import type { NarrativeOpener } from '../../../src/app/+core/security/narrative-text';
import {
  decodeExportDocument,
  openExportDocument,
  serializeExportDocument,
} from '../../../src/app/settings/export-document';
import {
  toTransactionView,
  transactionNarrativeRequests,
} from '../../../src/app/transactions/transaction-view';
import type {
  Environment,
  ExportFixtureInfo,
  ExportSample,
  ExportShape,
  FixtureInfo,
  FixtureShape,
  LongTask,
  MeasuredPath,
  RunSample,
} from '../shapes.ts';
import { computeBenchmarkIndex } from './benchmark-index.ts';
import { buildExportFixture, type ExportFixture } from './export-fixture.ts';
import { buildFixture, createContentKey, type Fixture } from './fixture.ts';

/** Lighthouse's BenchmarkIndex, re-exported so the driver can calibrate. */
export function benchmarkIndex(durationMs: number): number {
  return computeBenchmarkIndex(durationMs);
}

/** The facts a recorded number is not comparable without. */
export function environment(): Environment {
  return {
    hardwareConcurrency: navigator.hardwareConcurrency,
    performanceMemory:
      typeof (performance as { memory?: unknown }).memory === 'object',
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

/**
 * Seals one export-sized account and keeps its text for the rest of the run.
 *
 * One shape per call, so the driver's per-command deadline is spent on one
 * account at a time rather than on all three — the largest is tens of
 * thousands of seals. Like {@link prepare}, this is setup and is never timed.
 *
 * Under a content key of its own, minted on the first call: the export cell
 * and the read cells share no state, so either can run without the other.
 */
export async function prepareExport(
  shape: ExportShape,
): Promise<ExportFixtureInfo> {
  exportsPrepared ??= {
    contentKey: await createContentKey(),
    fixtures: new Map(),
  };

  const fixture = await buildExportFixture(exportsPrepared.contentKey, shape);

  exportsPrepared.fixtures.set(shape.name, fixture);

  return fixture.info;
}

/**
 * One export, the way `SettingsService.write` runs it: decode the served text,
 * open every member through the real `openExportDocument` with its default
 * frame budget, serialize — back to back, with no task boundary put between the
 * phases that the product does not have.
 *
 * One run per call, so the driver can collect garbage between two of them and
 * so a slow run spends one command's deadline rather than a cell's. The warm-up
 * is the driver's: it discards the first call.
 *
 * Throws on anything but a saved file. A run that came back `unrecognised`,
 * `locked` or `unreadable` did less work than an export does, and timing it
 * would record a figure for something that is not the export.
 */
export async function measureExport(
  fixtureName: string,
): Promise<ExportSample> {
  const fixture = exportsPrepared?.fixtures.get(fixtureName);

  if (exportsPrepared === undefined || fixture === undefined) {
    throw new Error(`No export named ${fixtureName} has been prepared.`);
  }

  const { contentKey } = exportsPrepared;
  const counted = { lastSettledAt: 0, opens: 0 };
  const custodyOpen = custodyEquivalentOpener(contentKey);
  const open: NarrativeOpener = async (binding, wire) => {
    const text = await custodyOpen(binding, wire);

    counted.opens++;
    counted.lastSettledAt = performance.now();

    return text;
  };
  const longTasks: LongTask[] = [];
  const observer = new PerformanceObserver((list) => {
    for (const entry of list.getEntries()) {
      longTasks.push({ durationMs: entry.duration, startMs: entry.startTime });
    }
  });

  // The same window `sampleOnce` opens, for the same reasons it gives: a task
  // boundary first, then the observer and the frame loop.
  await endTheCurrentTask();
  observer.observe({ entryTypes: ['longtask'] });

  const frames = await watchFrames();
  // Started last, directly above the `try` whose `finally` stops it, so no
  // failure above can leave its interval running.
  const heap = watchHeap();

  try {
    const beforeBytes = heap.sample();
    const startedAt = performance.now();

    const decoded = decodeExportDocument(fixture.text);
    const decodedAt = performance.now();
    const afterDecodeBytes = heap.sample();

    if (decoded.kind !== 'decoded') {
      throw new Error(
        `The ${fixtureName} export decoded as ${decoded.kind}; the fixture has drifted from the wire shape.`,
      );
    }

    const opening = openExportDocument(decoded.doc, open);
    const prologueEndedAt = performance.now();
    const opened = await opening;
    const openedAt = performance.now();
    const afterOpenBytes = heap.sample();

    if (opened.kind !== 'opened') {
      throw new Error(
        `The ${fixtureName} export opened as ${opened.kind}; a member is bound differently from the row it was sealed on.`,
      );
    }

    const file = serializeExportDocument(opened.doc);
    const serializedAt = performance.now();
    const afterSerializeBytes = heap.sample();
    const endedAt = serializedAt;
    // Waited on for its closing frame, not for its number: the gaps that
    // matter here are the ones inside the open, read from the stamps below.
    await frames.stop(performance.now());

    const stamps = frames.stamps();

    await new Promise<void>((resolve) => {
      globalThis.setTimeout(resolve, LONG_TASK_SETTLE_MS);
    });

    const relative = (at: number): number => at - startedAt;
    const interior = frameStretch(
      stamps,
      prologueEndedAt,
      counted.lastSettledAt,
    );

    return {
      heap: {
        afterDecodeBytes,
        afterOpenBytes,
        afterSerializeBytes,
        beforeBytes,
        peakBytes: heap.peak(),
        readings: heap.readings(),
      },
      interiorFrames: interior.frames,
      interiorWorstFrameGapMs: interior.worstGapMs,
      longTasks: longTasks
        .filter(
          (task) =>
            task.startMs + task.durationMs >= startedAt &&
            task.startMs <= endedAt,
        )
        .map((task) => ({
          durationMs: task.durationMs,
          startMs: relative(task.startMs),
        })),
      openEpilogueMs: openedAt - counted.lastSettledAt,
      openPrologueMs: prologueEndedAt - decodedAt,
      opens: counted.opens,
      outputChars: file.length,
      phases: [
        { endMs: relative(decodedAt), phase: 'decode', startMs: 0 },
        {
          endMs: relative(openedAt),
          phase: 'open',
          startMs: relative(decodedAt),
        },
        {
          endMs: relative(serializedAt),
          phase: 'serialize',
          startMs: relative(openedAt),
        },
      ],
      totalMs: relative(endedAt),
    };
  } finally {
    heap.stop();
    frames.abandon();
    observer.disconnect();
  }
}

/**
 * The page's JavaScript heap right now, for the driver to read after it has
 * collected garbage — what an export left behind once nothing holds it.
 */
export function heapUsedBytes(): number {
  return readHeapBytes();
}

interface Prepared {
  readonly fixtures: Map<string, Fixture>;
  readonly open: NarrativeOpener;
}

interface ExportsPrepared {
  readonly contentKey: CryptoKey;
  readonly fixtures: Map<string, ExportFixture>;
}

let exportsPrepared: ExportsPrepared | undefined;

// What `AccountKeyCustodyService.openField` does with a held key, minus the
// generation compare that decides whether an answer is still the current
// custody's: refuse a bad binding before the cipher, open, and turn a failed
// open into `unreadable` while a defect in the call keeps throwing. The cipher
// and the strict decoder inside it are the real ones — their cost is the point.
function custodyEquivalentOpener(contentKey: CryptoKey): NarrativeOpener {
  return async (binding, wire) => {
    refuseInvalidBinding(binding);

    try {
      return {
        state: 'text',
        value: await openNarrativeField(contentKey, wire, binding),
      };
    } catch (error: unknown) {
      if (error instanceof NarrativeFieldMisuseError) {
        throw error;
      }

      return { state: 'unreadable' };
    }
  };
}

/** How often the heap poller asks, when the main thread lets it. */
const HEAP_POLL_MS = 4;

interface HeapWatch {
  /** Reads the heap now, folds it into the peak, and returns it. */
  sample(): number;
  peak(): number;
  readings(): number;
  stop(): void;
}

// A poller plus explicit reads at the phase boundaries. The poller is a timer,
// so it runs only between tasks — between the batch's chunks, in practice —
// and never inside a synchronous `JSON.parse` or `JSON.stringify`. The boundary
// reads catch what each phase left live; nothing here can catch what a single
// call allocated and dropped before returning.
function watchHeap(): HeapWatch {
  let peak = 0;
  let readings = 0;
  const sample = (): number => {
    const used = readHeapBytes();

    peak = Math.max(peak, used);
    readings++;

    return used;
  };
  const poller = globalThis.setInterval(sample, HEAP_POLL_MS);

  return {
    peak: () => peak,
    readings: () => readings,
    sample,
    stop(): void {
      globalThis.clearInterval(poller);
    },
  };
}

// `performance.memory` is Chrome's and non-standard, so it is absent from the
// DOM lib and read through a narrowing rather than a declaration. Without
// `--enable-precise-memory-info` Chrome quantises it and refreshes it rarely;
// `chrome.ts` passes that flag.
function readHeapBytes(): number {
  const memory: unknown = (performance as { memory?: unknown }).memory;
  const used =
    typeof memory === 'object' && memory !== null
      ? (memory as { usedJSHeapSize?: unknown }).usedJSHeapSize
      : undefined;

  if (typeof used !== 'number') {
    throw new Error(
      'performance.memory.usedJSHeapSize is unavailable in this browser.',
    );
  }

  return used;
}

// The longest stretch inside [from, to] with no rAF callback in it, the window's
// own edges counting as boundaries, and how many callbacks fell inside. A window
// with no callback at all reports its whole length.
function frameStretch(
  stamps: readonly number[],
  from: number,
  to: number,
): { readonly frames: number; readonly worstGapMs: number } {
  const inside = stamps.filter((stamp) => stamp > from && stamp <= to);
  const edges = [from, ...inside, to];
  let worst = 0;

  for (let index = 1; index < edges.length; index++) {
    worst = Math.max(worst, (edges[index] ?? 0) - (edges[index - 1] ?? 0));
  }

  return { frames: inside.length, worstGapMs: worst };
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
  /** Every rAF timestamp the loop has recorded so far, oldest first. */
  stamps(): readonly number[];
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
    stamps(): readonly number[] {
      return [...stamps];
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
