// Derives the CPU throttling rates that make this machine behave like the
// devices DevTools calls low-tier and mid-tier mobile.
//
// **Reproduced, not hard-coded.** The constants here are a *device*: a
// BenchmarkIndex of 264 is a Moto G4 Power 2022 and 1000 is a Pixel 5. The
// *rate* is whatever it takes to drag this particular machine down to that
// score, so what this delivers is a reproducible *procedure* — every machine
// derives its rate the same way, where a pinned `20` would name a different
// device on each of them and say so nowhere. The algorithm — bisect on the
// rate, eight iterations, within ten points is a match, rates truncated to
// hundredths because the backend truncates there — is DevTools' own, from
// `front_end/panels/mobile_throttling/CalibrationController.ts`.
//
// **What it does not deliver is a number comparable between machines, and the
// error flatters.** Measured: the same harness under the same Chrome build and
// the same calibrated low-tier profile reads about 2.1× faster on a second
// machine. A slower machine calibrates to a *lower* rate to reach the reference
// index, and throttling does not slow AES the way it slows a JavaScript loop —
// so its crypto runs closer to full speed and it records the better number. A
// figure therefore carries its machine fingerprint, and a comparison requires
// the same machine; `docs/engineering/frontend-performance.md` has the pairs.
//
// **The nominal rate is not the effective one, and the harness prints both.**
// `cpuThrottlingRate` slows the renderer's task execution; it does not slow the
// machine. Work that leaves JavaScript — a `crypto.subtle` call handed to the
// platform's AES implementation — is stretched by a different factor from a
// tight loop, which is why the report's `x 1x` column is the one to argue from:
// this workload's own median against its unthrottled median, in the same
// invocation on the same machine. Presenting the nominal figure as though it
// were the device is the mistake this file exists to make hard.

/** The benchmark score of a low-tier device (a Moto G4 Power 2022). */
export const LOW_TIER_SCORE = 264;

/** The benchmark score of a mid-tier device (a Pixel 5). */
export const MID_TIER_SCORE = 1000;

/** How long one BenchmarkIndex iteration runs, matching DevTools. */
export const BENCHMARK_DURATION_MS = 250;

/** One derived profile. */
export interface CalibratedProfile {
  readonly label: string;
  readonly device: string;
  readonly targetScore: number;
  /** What is handed to `Emulation.setCPUThrottlingRate`. */
  readonly nominalRate: number;
  /** What the benchmark actually scored at that rate. */
  readonly scoreAtRate: number;
  /** Unthrottled score over throttled score: the factor the loop really felt. */
  readonly effectiveOnBenchmark: number;
}

/** Everything calibration learned about the measuring machine. */
export interface Calibration {
  readonly actualScore: number;
  readonly profiles: readonly CalibratedProfile[];
  /** Anything the run could not derive, said out loud rather than swallowed. */
  readonly notes: readonly string[];
}

/** Runs the benchmark at `rate`; the caller owns setting the throttle. */
export type ScoreAtRate = (rate: number) => Promise<number>;

/**
 * Warms V8, measures the machine, then bisects a rate for each target device.
 *
 * Roughly twenty benchmark iterations at 250 ms, so about five seconds — the
 * same budget DevTools quotes for its own calibration.
 */
export async function calibrate(
  scoreAtRate: ScoreAtRate,
): Promise<Calibration> {
  const cache = new Map<number, number>();
  const run = async (rate: number): Promise<number> => {
    const cached = cache.get(rate);

    if (cached !== undefined) {
      return cached;
    }

    const score = await scoreAtRate(rate);

    cache.set(rate, score);

    return score;
  };

  // Warm up, discarded: the first iteration pays for V8 optimising the loop.
  await scoreAtRate(1);

  const notes: string[] = [];
  const actualScore = await run(1);

  if (actualScore < LOW_TIER_SCORE) {
    return {
      actualScore,
      notes: [
        'This machine benchmarks below a low-tier phone, so neither profile can be emulated. Every measurement below ran unthrottled.',
      ],
      profiles: [],
    };
  }

  const profiles: CalibratedProfile[] = [];
  const lowRate = await bisect(
    run,
    LOW_TIER_SCORE,
    1,
    (actualScore / LOW_TIER_SCORE) * 1.5,
  );

  profiles.push(
    await describe(
      run,
      'low-tier',
      'Moto G4 Power 2022',
      LOW_TIER_SCORE,
      lowRate,
      actualScore,
    ),
  );

  if (actualScore < MID_TIER_SCORE) {
    notes.push(
      'This machine benchmarks below a mid-tier phone, so no mid-tier profile was derived.',
    );

    return { actualScore, notes, profiles };
  }

  // Bootstrapped off the low-tier answer the way DevTools does: the two devices
  // differ by a known ratio, so the mid-tier rate is bracketed around it rather
  // than searched from one.
  const around = lowRate / (MID_TIER_SCORE / LOW_TIER_SCORE);
  const midRate = await bisect(
    run,
    MID_TIER_SCORE,
    around - around / 4,
    around + around / 4,
  );

  profiles.push(
    await describe(
      run,
      'mid-tier',
      'Pixel 5',
      MID_TIER_SCORE,
      midRate,
      actualScore,
    ),
  );

  return { actualScore, notes, profiles };
}

/** Within ten points of the target score is a match. */
const SCORE_TOLERANCE = 10;

/** The bisect gives up after this many halvings. */
const MAX_ITERATIONS = 8;

async function describe(
  run: ScoreAtRate,
  label: string,
  device: string,
  targetScore: number,
  nominalRate: number,
  actualScore: number,
): Promise<CalibratedProfile> {
  const scoreAtRate = await run(nominalRate);

  return {
    device,
    effectiveOnBenchmark: actualScore / scoreAtRate,
    label,
    nominalRate,
    scoreAtRate,
    targetScore,
  };
}

async function bisect(
  run: ScoreAtRate,
  target: number,
  lowerRate: number,
  upperRate: number,
): Promise<number> {
  const lower = { rate: lowerRate, score: await run(lowerRate) };
  const upper = { rate: upperRate, score: await run(upperRate) };

  let rate = lowerRate;

  for (let iteration = 0; iteration < MAX_ITERATIONS; iteration++) {
    // Truncated to hundredths because the throttling agent truncates there, so
    // a finer candidate would be scored under a rate it was not asked for.
    rate = truncate((upper.rate + lower.rate) / 2);

    const score = await run(rate);

    if (Math.abs(target - score) < SCORE_TOLERANCE) {
      break;
    }

    if (score < target) {
      upper.rate = rate;
      upper.score = score;
    } else {
      lower.rate = rate;
      lower.score = score;
    }
  }

  return truncate(rate);
}

function truncate(value: number): number {
  return Number(value.toFixed(2));
}
