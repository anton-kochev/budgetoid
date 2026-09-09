// The harness: bundle the real modules, serve them to a real Chrome, calibrate
// the machine against a reference device, and record what opening a screenful
// of narrative fields costs.
//
// **It never fails on a threshold.** The exit code answers one question — did
// the measurement happen — and the answers to that question are a missing
// Chrome, a bundle that would not build, a page that threw. A number is
// recorded and never asserted, and this tool does not run in CI. A perf gate
// that goes red on a noisy neighbour gets retried, then widened, then ignored.
//
// **A hang is the one way that exit code can lie, so nothing here waits without
// a deadline.** Every DevTools command carries one, the launch gives up the
// moment Chrome exits without announcing an endpoint, and the release below runs
// on a signal as well as on the way out — a process still holding a headless
// Chrome has answered nothing.
//
// Knobs, all optional:
//   CHROME_PATH                  which browser to drive
//   NARRATIVE_PERF_RUNS          samples per cell, default 9
//   NARRATIVE_PERF_HEADFUL       set to 1 to watch it happen in a window
//   NARRATIVE_PERF_EXTRA_RATES   extra nominal rates, e.g. 20 — for reproducing
//                                an older number taken at a hard-coded rate
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { bundleBrowserEntry, harnessPage } from './bundle.ts';
import { calibrate, BENCHMARK_DURATION_MS } from './calibrate.ts';
import type { Calibration, CalibratedProfile } from './calibrate.ts';
import { connect, evaluate, stringProperty, waitFor, type Cdp } from './cdp.ts';
import { launchChrome } from './chrome.ts';
import { renderReport } from './report.ts';
import type {
  Measurement,
  ProfileDerivation,
  ProfileReport,
  Report,
} from './report.ts';
import { startStaticServer, type ServedFile } from './server.ts';
import {
  FIXTURE_SHAPES,
  MEASURED_PATHS,
  type Environment,
  type FixtureInfo,
  type RunSample,
} from './shapes.ts';

/** How long the frame baseline watches an idle page, per window. */
const BASELINE_MS = 500;

/** How many idle windows the baseline band is drawn from, per profile. */
const BASELINE_WINDOWS = 5;

/** How long to wait for the page to publish its API. */
const PAGE_READY_TIMEOUT_MS = 20_000;

async function main(): Promise<void> {
  // Nine rather than five. The unthrottled cells finish in single-digit
  // milliseconds, so one stray frame boundary moves their median enough to make
  // a throttled cell look *faster* than 1x — which it then prints as a stretch
  // factor below one. More samples is the cheapest fix; every cell is short.
  const runs = Number.parseInt(process.env['NARRATIVE_PERF_RUNS'] ?? '9', 10);
  const headless = process.env['NARRATIVE_PERF_HEADFUL'] !== '1';
  const extraRates = readExtraRates();

  if (!Number.isInteger(runs) || runs < 1) {
    throw new Error('NARRATIVE_PERF_RUNS must be a positive integer.');
  }

  process.stderr.write(
    'bundling the real modules with the toolchain esbuild…\n',
  );

  const files = new Map<string, ServedFile>([
    ['/', { body: harnessPage(), contentType: 'text/html; charset=utf-8' }],
    [
      '/narrative-perf.js',
      {
        body: await bundleBrowserEntry(),
        contentType: 'text/javascript; charset=utf-8',
      },
    ],
  ]);
  // Everything acquired is registered for release as it is acquired, rather
  // than reconstructed from `undefined`s in a `finally`. The order is the point:
  // a listening socket and an open WebSocket each keep Node's event loop alive,
  // so a failure to *launch* — the very case this tool is allowed to exit
  // non-zero on — would otherwise set the exit code and then hang forever
  // instead of using it.
  const teardown = createTeardown();

  releaseOnSignal(teardown);

  const site = await startStaticServer(files);

  teardown.add(() => site.close());

  try {
    const userDataDir = await mkdtemp(join(tmpdir(), 'narrative-perf-'));

    teardown.add(() => rm(userDataDir, { force: true, recursive: true }));

    const chrome = await launchChrome({ headless, userDataDir });

    // Registered after the profile directory so it is released before it: the
    // directory cannot go while a browser is still writing into it.
    teardown.add(() => chrome.kill());

    process.stderr.write(`driving ${chrome.executable}\n`);

    const cdp = await connect(chrome.webSocketDebuggerUrl);

    teardown.add(() => cdp.close());

    process.stdout.write(
      renderReport(
        await measureEverything(cdp, site.origin, {
          extraRates,
          headless,
          runs,
        }),
      ),
    );
  } finally {
    await teardown.run();
  }
}

/** Releases, in reverse order of acquisition and at most once. */
interface Teardown {
  add(release: () => Promise<void> | void): void;
  run(): Promise<void>;
}

// **A list rather than the `finally` this used to be, because `finally` does not
// cover a signal.** Ctrl-C during a five-minute run ends this process and leaves
// a headless Chrome and a temp profile directory behind it, and nothing in the
// language runs on the way out. The same list therefore serves both endings, and
// runs at most once because a signal can arrive while the `finally` is already
// unwinding.
function createTeardown(): Teardown {
  const releases: (() => Promise<void> | void)[] = [];
  let done = false;

  return {
    add(release): void {
      releases.push(release);
    },
    async run(): Promise<void> {
      if (done) {
        return;
      }

      done = true;

      for (const release of [...releases].reverse()) {
        try {
          await release();
        } catch (error: unknown) {
          // One release that fails may not cost the ones under it: a profile
          // directory left on disk is a nuisance, a live headless Chrome is a
          // leak. Reported rather than swallowed, because a release that keeps
          // failing is worth knowing about.
          process.stderr.write(
            `\ncleanup step failed: ${error instanceof Error ? error.message : String(error)}\n`,
          );
        }
      }
    },
  };
}

/** The conventional exit codes for a run a signal ended. */
const SIGINT_EXIT_CODE = 130;
const SIGTERM_EXIT_CODE = 143;

// Read by the top-level catch, which would otherwise report Ctrl-C as a defect.
let interrupted = false;

// The two signals a person or a supervisor sends. Both are handled the same way
// — release, then exit on the conventional code, because the exit code of this
// tool answers whether the measurement happened and an interrupted run is a run
// that did not.
function releaseOnSignal(teardown: Teardown): void {
  for (const signal of ['SIGINT', 'SIGTERM'] as const) {
    process.once(signal, () => {
      interrupted = true;
      process.stderr.write(
        `\n${signal} — releasing the browser and the profile directory…\n`,
      );

      const code = signal === 'SIGINT' ? SIGINT_EXIT_CODE : SIGTERM_EXIT_CODE;

      void teardown.run().then(
        () => process.exit(code),
        () => process.exit(1),
      );
    });
  }
}

/** What one invocation was asked to measure. */
interface RunOptions {
  readonly runs: number;
  readonly headless: boolean;
  readonly extraRates: readonly number[];
}

// Everything between an open connection and a finished report. Split out from
// `main` so the acquisition and release of Chrome, the profile directory and
// the socket read as one block rather than being interleaved with four hundred
// measurements.
async function measureEverything(
  cdp: Cdp,
  origin: string,
  options: RunOptions,
): Promise<Report> {
  const { extraRates, headless, runs } = options;
  const chromeVersion = stringProperty(
    await cdp.send('Browser.getVersion'),
    'product',
  );
  const sessionId = await openPage(cdp, origin);
  const environment = await evaluate<Environment>(
    cdp,
    sessionId,
    'narrativePerf.environment()',
  );

  if (!environment.secureContext) {
    throw new Error(
      'The page is not a secure context, so crypto.subtle is unavailable.',
    );
  }

  const throttle = async (rate: number): Promise<void> => {
    await cdp.send('Emulation.setCPUThrottlingRate', { rate }, sessionId);
  };

  process.stderr.write('calibrating against the reference devices…\n');

  const calibration = await calibrate(async (rate) => {
    await throttle(rate);

    return await evaluate<number>(
      cdp,
      sessionId,
      `narrativePerf.benchmarkIndex(${BENCHMARK_DURATION_MS})`,
    );
  });

  await throttle(1);
  process.stderr.write('sealing the fixtures…\n');

  const fixtures = await evaluate<readonly FixtureInfo[]>(
    cdp,
    sessionId,
    `narrativePerf.prepare(${JSON.stringify(FIXTURE_SHAPES)})`,
  );
  const profiles: ProfileReport[] = [];
  const measurements: Measurement[] = [];

  for (const profile of profileOrder(calibration, extraRates)) {
    process.stderr.write(
      `measuring ${profile.label} at a nominal ${profile.nominalRate}x…\n`,
    );
    await throttle(profile.nominalRate);

    // A fixed rate has no calibration behind it, so its effective factor is
    // measured here rather than looked up. The whole point of carrying the
    // column is that the two numbers differ.
    const scoreAtRate =
      profile.scoreAtRate ??
      (await evaluate<number>(
        cdp,
        sessionId,
        `narrativePerf.benchmarkIndex(${BENCHMARK_DURATION_MS})`,
      ));

    profiles.push({
      baseline: await evaluate<readonly RunSample[]>(
        cdp,
        sessionId,
        `narrativePerf.frameBaseline(${BASELINE_MS}, ${BASELINE_WINDOWS})`,
      ),
      derivation: profile.derivation,
      device: profile.device,
      effectiveOnBenchmark: calibration.actualScore / scoreAtRate,
      label: profile.label,
      nominalRate: profile.nominalRate,
      scoreAtRate,
      targetScore: profile.targetScore,
    });

    for (const shape of FIXTURE_SHAPES) {
      const fixture = fixtures.find((info) => info.name === shape.name);

      if (fixture === undefined) {
        throw new Error(`The page prepared no fixture named ${shape.name}.`);
      }

      for (const path of MEASURED_PATHS) {
        measurements.push({
          fixture,
          path,
          profile: profile.label,
          samples: await evaluate<readonly RunSample[]>(
            cdp,
            sessionId,
            `narrativePerf.measure(${JSON.stringify(shape.name)}, ${JSON.stringify(path)}, ${runs})`,
          ),
          shape,
        });
      }
    }
  }

  await throttle(1);

  return {
    calibration,
    chromeVersion,
    environment,
    headless,
    measurements,
    profiles,
    runs,
  };
}

interface PlannedProfile {
  readonly label: string;
  readonly device: string;
  readonly derivation: ProfileDerivation;
  readonly nominalRate: number;
  readonly targetScore: number | null;
  /** Known already where calibration measured it; measured on the spot if not. */
  readonly scoreAtRate: number | null;
}

// 1x first so every throttled cell has something to be a multiple of, then the
// two derived devices from fast to slow, then any fixed rate the caller asked
// for — those are last because a fixed rate is a *rate*, not a device, and
// reading it above the calibrated rows invites exactly the confusion the
// effective-factor column exists to prevent.
function profileOrder(
  calibration: Calibration,
  extraRates: readonly number[],
): readonly PlannedProfile[] {
  const unthrottled: PlannedProfile = {
    derivation: 'unthrottled',
    device: 'this machine',
    label: '1x',
    nominalRate: 1,
    scoreAtRate: calibration.actualScore,
    targetScore: null,
  };
  const derived = ['mid-tier', 'low-tier'].flatMap((label) =>
    calibration.profiles
      .filter((profile) => profile.label === label)
      .map(
        (profile: CalibratedProfile): PlannedProfile => ({
          derivation: 'calibrated',
          device: profile.device,
          label: profile.label,
          nominalRate: profile.nominalRate,
          scoreAtRate: profile.scoreAtRate,
          targetScore: profile.targetScore,
        }),
      ),
  );
  const fixed = extraRates.map(
    (rate): PlannedProfile => ({
      derivation: 'fixed',
      device: 'none — a bare rate',
      label: `nominal-${rate}x`,
      nominalRate: rate,
      scoreAtRate: null,
      targetScore: null,
    }),
  );

  return [unthrottled, ...derived, ...fixed];
}

// A comma-separated list of nominal rates to measure beside the calibrated
// profiles. It exists so an older number taken at a hard-coded rate can be
// reproduced and compared, and for nothing else.
function readExtraRates(): readonly number[] {
  const raw = process.env['NARRATIVE_PERF_EXTRA_RATES'];

  if (raw === undefined || raw.trim() === '') {
    return [];
  }

  return raw.split(',').map((part) => {
    const rate = Number.parseFloat(part.trim());

    if (!Number.isFinite(rate) || rate < 1) {
      throw new Error(
        `NARRATIVE_PERF_EXTRA_RATES holds ${part}, which is not a throttling rate of 1 or more.`,
      );
    }

    return rate;
  });
}

async function openPage(cdp: Cdp, origin: string): Promise<string> {
  const target = await cdp.send('Target.createTarget', { url: origin });
  const attached = await cdp.send('Target.attachToTarget', {
    flatten: true,
    targetId: stringProperty(target, 'targetId'),
  });
  const sessionId = stringProperty(attached, 'sessionId');

  await waitFor(
    cdp,
    sessionId,
    'typeof narrativePerf === "object" && narrativePerf !== null',
    PAGE_READY_TIMEOUT_MS,
  );

  return sessionId;
}

try {
  await main();
} catch (error: unknown) {
  // **An interrupted run is not a harness failure, and it throws like one.**
  // Releasing the connection while a command is in flight rejects that command,
  // so Ctrl-C arrives here as `The DevTools connection closed` with a stack
  // under it — which reads as a defect to whoever pressed the keys. The exit
  // code still says the measurement did not happen; that is the whole contract.
  if (interrupted) {
    process.exitCode = SIGINT_EXIT_CODE;
  } else {
    process.exitCode = 1;
    process.stderr.write(
      `\nharness failure: ${error instanceof Error ? (error.stack ?? error.message) : String(error)}\n`,
    );
  }
}
