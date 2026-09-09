// Turns the samples into the block a documentation page can paste.
//
// **Nothing here compares a number to a budget and fails.** NFR-003's 100 ms is
// printed beside the measured median as a recorded observation, because a
// performance gate that goes red on a noisy neighbour gets retried, then
// widened, then ignored, and the fourth state is a gate nobody believes. The
// harness records; a person decides.
import type { Calibration } from './calibrate.ts';
import type {
  Environment,
  FixtureInfo,
  FixtureShape,
  MeasuredPath,
  RunSample,
} from './shapes.ts';

/** Where a measured profile's rate came from. */
export type ProfileDerivation = 'unthrottled' | 'calibrated' | 'fixed';

/** One profile as it was measured, with its own no-crypto frame baseline. */
export interface ProfileReport {
  readonly label: string;
  readonly device: string;
  readonly derivation: ProfileDerivation;
  /** What `Emulation.setCPUThrottlingRate` was told. */
  readonly nominalRate: number;
  /** The reference device's BenchmarkIndex, where the rate was derived from one. */
  readonly targetScore: number | null;
  readonly scoreAtRate: number;
  /** Unthrottled score over throttled score: what the loop actually felt. */
  readonly effectiveOnBenchmark: number;
  /** Several idle windows, so the baseline is a band and not a constant. */
  readonly baseline: readonly RunSample[];
}

/** One cell of the table: a fixture read down one path under one profile. */
export interface Measurement {
  readonly profile: string;
  readonly shape: FixtureShape;
  readonly fixture: FixtureInfo;
  readonly path: MeasuredPath;
  readonly samples: readonly RunSample[];
}

/** Everything one invocation of the harness learned. */
export interface Report {
  readonly chromeVersion: string;
  readonly environment: Environment;
  readonly headless: boolean;
  readonly calibration: Calibration;
  readonly profiles: readonly ProfileReport[];
  readonly measurements: readonly Measurement[];
  readonly runs: number;
}

/** The budget NFR-003 states, recorded against and never asserted. */
export const NFR_003_BUDGET_MS = 100;

/** Renders the whole report. */
export function renderReport(report: Report): string {
  return [
    ...renderEnvironment(report),
    '',
    ...renderCalibration(report),
    '',
    ...renderBaselines(report),
    '',
    ...renderTable(report),
    '',
    ...renderStretch(report),
    '',
    ...renderLongTasks(report),
    '',
    ...renderBudget(report),
    '',
  ].join('\n');
}

function renderEnvironment(report: Report): readonly string[] {
  const { environment: env } = report;

  return [
    'narrative-perf — NFR-003 measurement, recorded not asserted',
    '',
    `  chrome                   ${report.chromeVersion}${report.headless ? ' (headless)' : ' (headed)'}`,
    `  user agent               ${env.userAgent}`,
    `  hardwareConcurrency      ${env.hardwareConcurrency}`,
    `  secure context           ${env.secureContext ? 'yes' : 'no'}`,
    `  frame yield              ${env.schedulerPostTask ? 'scheduler.postTask' : 'MessageChannel fallback'}`,
    `  runs per cell            ${report.runs} (after one discarded warm-up)`,
    '  fixtures                 200 rows, 5 sealed columns each = 1000 sealed columns',
  ];
}

const CALIBRATION_WIDTHS = [12, 26, 9, 14, 12, 12] as const;

function renderCalibration(report: Report): readonly string[] {
  const lines = [
    'Calibration — DevTools’ own procedure, reproduced (Lighthouse BenchmarkIndex,',
    '250 ms per iteration, bisect on the rate, truncated to hundredths).',
    '',
    `  BenchmarkIndex at 1x     ${report.calibration.actualScore.toFixed(0)}  (this machine, unthrottled)`,
    '',
    padRow([
      ['profile', 12],
      ['reference device', 26],
      ['target', 9],
      ['nominal rate', 14],
      ['scored', 12],
      ['effective', 12],
    ]),
    divider(CALIBRATION_WIDTHS),
  ];

  for (const profile of report.profiles) {
    lines.push(
      padRow([
        [profile.label, 12],
        [profile.device, 26],
        [profile.targetScore?.toString() ?? '-', 9],
        [`${profile.nominalRate.toFixed(2)}x`, 14],
        [profile.scoreAtRate.toFixed(0), 12],
        [`${profile.effectiveOnBenchmark.toFixed(2)}x`, 12],
      ]),
    );
  }

  lines.push(
    '',
    '  target    — the BenchmarkIndex of the reference device. It is the constant;',
    '              the rate is what this machine needed to reach it.',
    '  scored    — what the benchmark actually scored at that nominal rate.',
    '  effective — unthrottled score over throttled score. This is the factor the',
    '              benchmark loop felt, and it is not the nominal rate. Never quote',
    '              the nominal number as though it were the device.',
  );

  return lines;
}

function renderBaselines(report: Report): readonly string[] {
  const lines = [
    'Frame baseline — the renderer’s own rAF tick period under each profile, no',
    'crypto at all, over several idle windows. It comes out flat on every profile,',
    'unthrottled included: a tick period, not frames the rig missed. A frame gap in',
    'the table below is readable against this band and against nothing else, and the',
    'band is one tick — so one whole tick is the resolution, and “1.0x the idle',
    'worst” below means only that no tick was missed.',
    '',
    padRow([
      ['profile', 12],
      ['best gap', 12],
      ['median gap', 13],
      ['worst gap', 12],
    ]),
    divider([12, 12, 13, 12]),
  ];

  for (const profile of report.profiles) {
    const gaps = profile.baseline.map((sample) => sample.worstFrameGapMs);

    lines.push(
      padRow([
        [profile.label, 12],
        [`${min(gaps).toFixed(1)} ms`, 12],
        [`${median(gaps).toFixed(1)} ms`, 13],
        [`${max(gaps).toFixed(1)} ms`, 12],
      ]),
    );
  }

  return lines;
}

const TABLE_WIDTHS = [12, 14, 9, 10, 8, 9, 11, 9, 8, 17, 20] as const;

function renderTable(report: Report): readonly string[] {
  const lines = [
    'Measurements — total ms is the whole read: collect the requests, open them,',
    'build 200 view models.',
    '',
    padRow([
      ['profile', 12],
      ['fixture', 14],
      ['path', 9],
      ['distinct', 10],
      ['opens', 8],
      ['min ms', 9],
      ['median ms', 11],
      ['max ms', 9],
      ['x 1x', 8],
      ['worst frame gap', 17],
      ['long tasks', 20],
    ]),
    divider(TABLE_WIDTHS),
  ];

  for (const measurement of report.measurements) {
    const totals = measurement.samples.map((sample) => sample.totalMs);
    const tasks = measurement.samples.flatMap((sample) => sample.longTasks);
    const worstTask = tasks.reduce(
      (worst, task) => Math.max(worst, task.durationMs),
      0,
    );

    lines.push(
      padRow([
        [measurement.profile, 12],
        [measurement.fixture.name, 14],
        [measurement.path, 9],
        [measurement.fixture.distinctOpens.toString(), 10],
        [opensRun(measurement).toString(), 8],
        [min(totals).toFixed(1), 9],
        [median(totals).toFixed(1), 11],
        [max(totals).toFixed(1), 9],
        [stretchOf(report, measurement), 8],
        [
          `${max(measurement.samples.map((sample) => sample.worstFrameGapMs)).toFixed(1)} ms`,
          17,
        ],
        [
          tasks.length === 0
            ? 'none'
            : `${tasks.length}, worst ${worstTask.toFixed(0)} ms`,
          20,
        ],
      ]),
    );
  }

  lines.push(
    '',
    '  distinct  — distinct (table, column, row, wire) pairs among the 1000 columns.',
    '  opens     — AEAD opens actually performed: the distinct count on the batch',
    '              path, all 1000 on the per-row path.',
    '  x 1x      — this cell’s median over the same cell’s median at 1x: the factor',
    '              the real workload felt, as opposed to the nominal rate. Read it',
    '              as an order of magnitude and no finer: the 1x cells finish in',
    '              single-digit to low-teens milliseconds, where one task boundary',
    '              moves a median by a whole frame.',
    '',
    ...report.measurements
      .map((measurement) => measurement.shape)
      .filter(
        (shape, index, shapes) =>
          shapes.findIndex((other) => other.name === shape.name) === index,
      )
      .map(
        (shape) =>
          `  ${shape.name.padEnd(14)}${shape.accounts} accounts, ${shape.payees} payees, ` +
          `${shape.categories} categories, ${shape.groups} groups — ${shape.note}`,
      ),
  );

  return lines;
}

function renderStretch(report: Report): readonly string[] {
  return [
    'What de-duplication bought — batch median over per-row median, same profile',
    'and fixture.',
    '',
    padRow([
      ['profile', 12],
      ['fixture', 14],
      ['batch ms', 12],
      ['per-row ms', 12],
      ['saved', 10],
    ]),
    divider([12, 14, 12, 12, 10]),
    ...report.measurements
      .filter((measurement) => measurement.path === 'batch')
      .map((batch) => {
        const perRow = report.measurements.find(
          (other) =>
            other.path === 'per-row' &&
            other.profile === batch.profile &&
            other.fixture.name === batch.fixture.name,
        );
        const batchMs = median(batch.samples.map((sample) => sample.totalMs));
        const perRowMs =
          perRow === undefined
            ? Number.NaN
            : median(perRow.samples.map((sample) => sample.totalMs));

        return padRow([
          [batch.profile, 12],
          [batch.fixture.name, 14],
          [batchMs.toFixed(1), 12],
          [Number.isNaN(perRowMs) ? '-' : perRowMs.toFixed(1), 12],
          [
            Number.isNaN(perRowMs)
              ? '-'
              : `${(perRowMs - batchMs).toFixed(1)} ms`,
            10,
          ],
        ]);
      }),
  ];
}

function renderLongTasks(report: Report): readonly string[] {
  const lines = [
    'Long tasks — every `longtask` entry observed inside a measured window, as a',
    'start offset from the beginning of the run. A small negative offset is the',
    'window’s own edge: the run yields the task before starting its clock, and a',
    '`longtask` start time is coarsened.',
    '',
  ];
  let any = false;

  for (const measurement of report.measurements) {
    measurement.samples.forEach((sample, run) => {
      for (const task of sample.longTasks) {
        any = true;
        lines.push(
          `  ${measurement.profile.padEnd(12)}${measurement.fixture.name.padEnd(14)}` +
            `${measurement.path.padEnd(9)}run ${(run + 1).toString().padStart(2)}  ` +
            `${offset(task.startMs)} for ${task.durationMs.toFixed(1)} ms`,
        );
      }
    });
  }

  for (const profile of report.profiles) {
    profile.baseline.forEach((sample, window) => {
      for (const task of sample.longTasks) {
        any = true;
        lines.push(
          `  ${profile.label.padEnd(12)}${'(baseline)'.padEnd(14)}${'-'.padEnd(9)}` +
            `run ${(window + 1).toString().padStart(2)}  ${offset(task.startMs)} for ${task.durationMs.toFixed(1)} ms`,
        );
      }
    });
  }

  if (!any) {
    lines.push('  none, in any cell.');
  }

  return lines;
}

function renderBudget(report: Report): readonly string[] {
  const lines = [
    `NFR-003 — recorded against ${NFR_003_BUDGET_MS} ms on the low-tier profile, batch path.`,
    'This is an observation. The harness exits zero whatever it says.',
    '',
  ];
  const lowTier = report.measurements.filter(
    (measurement) =>
      measurement.profile === 'low-tier' && measurement.path === 'batch',
  );

  if (lowTier.length === 0) {
    return [...lines, '  no low-tier profile was derived on this machine.'];
  }

  for (const measurement of lowTier) {
    const totals = measurement.samples.map((sample) => sample.totalMs);
    const worst = max(totals);
    const margin = NFR_003_BUDGET_MS - median(totals);

    lines.push(
      `  ${measurement.fixture.name.padEnd(14)}median ${median(totals).toFixed(1)} ms, worst ${worst.toFixed(1)} ms — ` +
        `${margin >= 0 ? `${margin.toFixed(1)} ms under` : `${(-margin).toFixed(1)} ms over`} the budget at the median`,
    );
  }

  const lowTierBaseline = report.profiles.find(
    (profile) => profile.label === 'low-tier',
  );

  if (lowTierBaseline !== undefined) {
    const band = lowTierBaseline.baseline.map(
      (sample) => sample.worstFrameGapMs,
    );

    lines.push(
      '',
      '  Blocking the main thread — worst rAF gap on the low-tier profile, against',
      `  that profile’s idle band of ${min(band).toFixed(1)}–${max(band).toFixed(1)} ms.`,
      '',
    );

    for (const measurement of report.measurements.filter(
      (candidate) => candidate.profile === 'low-tier',
    )) {
      const worst = max(
        measurement.samples.map((sample) => sample.worstFrameGapMs),
      );

      lines.push(
        `  ${measurement.fixture.name.padEnd(14)}${measurement.path.padEnd(9)}` +
          `${worst.toFixed(1)} ms — ${(worst / max(band)).toFixed(1)}x the idle worst`,
      );
    }
  }

  for (const note of report.calibration.notes) {
    lines.push(`  note: ${note}`);
  }

  return lines;
}

// Signed, because a long task can begin a fraction of a millisecond before the
// window opens: the run yields the task first, but the clock starts a few
// statements into the task that follows, and `longtask` start times are
// coarsened. A small negative offset is the window's own edge, not work from an
// earlier run.
function offset(startMs: number): string {
  return `${startMs < 0 ? '' : '+'}${startMs.toFixed(1)} ms`;
}

function opensRun(measurement: Measurement): number {
  return measurement.path === 'batch'
    ? measurement.fixture.distinctOpens
    : measurement.fixture.totalOpens;
}

function stretchOf(report: Report, measurement: Measurement): string {
  if (measurement.profile === '1x') {
    return '1.00x';
  }

  const unthrottled = report.measurements.find(
    (other) =>
      other.profile === '1x' &&
      other.path === measurement.path &&
      other.fixture.name === measurement.fixture.name,
  );

  if (unthrottled === undefined) {
    return '-';
  }

  const base = median(unthrottled.samples.map((sample) => sample.totalMs));
  const here = median(measurement.samples.map((sample) => sample.totalMs));

  return `${(here / base).toFixed(2)}x`;
}

function padRow(cells: readonly (readonly [string, number])[]): string {
  return `  ${cells.map(([text, width]) => text.padEnd(width)).join('')}`.trimEnd();
}

function divider(widths: readonly number[]): string {
  return `  ${widths.map((width) => '-'.repeat(width - 1).padEnd(width)).join('')}`.trimEnd();
}

function min(values: readonly number[]): number {
  return values.reduce((lowest, value) => Math.min(lowest, value), Infinity);
}

function max(values: readonly number[]): number {
  return values.reduce((highest, value) => Math.max(highest, value), 0);
}

function median(values: readonly number[]): number {
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);

  if (sorted.length === 0) {
    return Number.NaN;
  }

  return sorted.length % 2 === 1
    ? (sorted[middle] ?? Number.NaN)
    : ((sorted[middle - 1] ?? Number.NaN) + (sorted[middle] ?? Number.NaN)) / 2;
}
