// The vocabulary both halves of the harness share: the Node driver that owns
// Chrome and the bundle that runs inside it. Imported by both, so a fixture
// renamed on one side is a compile error on the other rather than a table row
// that quietly stops matching the thing it measured.
//
// Nothing here imports the app. The shapes are numbers and words.

/**
 * One synthetic ledger page: how many rows, and how many distinct rows each of
 * the four foreign names is drawn from.
 *
 * The cardinalities are the whole point of the fixture. A screenful of 200
 * transactions carries 1000 sealed columns, but a budget names the same four
 * accounts and the same forty payees over and over, so the number of *distinct*
 * opens is far smaller — and that gap is exactly what `openNarrativeBatch`
 * exists to collect.
 */
export interface FixtureShape {
  readonly name: string;
  /** Why this shape is in the table, printed beside its cardinalities. */
  readonly note: string;
  readonly rows: number;
  readonly accounts: number;
  readonly payees: number;
  readonly categories: number;
  readonly groups: number;
}

/**
 * The three shapes measured, in the order they are reported.
 *
 * *typical* and *heavy* are budgets; *pathological* is not a shape a budget
 * has. It is here so the failure mode is on paper: every one of the 1000 sealed
 * columns distinct, which is the most opens 200 rows can possibly need.
 */
export const FIXTURE_SHAPES = [
  {
    name: 'typical',
    note: 'a budget a person actually has',
    rows: 200,
    accounts: 4,
    payees: 40,
    categories: 25,
    groups: 8,
  },
  {
    name: 'heavy',
    note: 'a long-running budget with a wide payee list',
    rows: 200,
    accounts: 10,
    payees: 200,
    categories: 40,
    groups: 10,
  },
  {
    name: 'pathological',
    note: 'every column distinct — not a budget, a ceiling',
    rows: 200,
    accounts: 200,
    payees: 200,
    categories: 200,
    groups: 200,
  },
] as const satisfies readonly FixtureShape[];

/**
 * Which of the two reading paths a measurement ran.
 *
 * `batch` is what the product does: collect the screenful's requests, open the
 * distinct ones through {@link openNarrativeBatch}, hand the mappers an opener
 * backed by that map. `per-row` is the same 200 rows opened a row at a time,
 * with no de-duplication and no frame budget — the shape the code had before,
 * kept so the table can say what the change bought.
 */
export type MeasuredPath = 'batch' | 'per-row';

/** Both paths, in the order they are reported. */
export const MEASURED_PATHS = [
  'batch',
  'per-row',
] as const satisfies readonly MeasuredPath[];

/** One `longtask` entry the page observed, relative to the run's start. */
export interface LongTask {
  readonly startMs: number;
  readonly durationMs: number;
}

/** Everything one measured run produced. */
export interface RunSample {
  /** Wall-clock from the first request being collected to the last view. */
  readonly totalMs: number;
  /**
   * The largest gap between two consecutive `requestAnimationFrame` callbacks
   * over the run. Only meaningful against the baseline gap measured in the same
   * conditions with no crypto running — see {@link RunSample} usage in the
   * report, and the baseline row printed above the table.
   */
  readonly worstFrameGapMs: number;
  readonly longTasks: readonly LongTask[];
}

/** What one prepared fixture turned out to be. */
export interface FixtureInfo {
  readonly name: string;
  readonly rows: number;
  /** Sealed columns across the page — five per row. */
  readonly totalOpens: number;
  /** Distinct (table, column, row, wire) pairs among them. */
  readonly distinctOpens: number;
}

/** The facts a number is not comparable without. */
export interface Environment {
  readonly userAgent: string;
  readonly hardwareConcurrency: number;
  /** `crypto.subtle` is unavailable without this, so it is checked, not assumed. */
  readonly secureContext: boolean;
  /** Whether the frame yield used `scheduler.postTask` or the channel fallback. */
  readonly schedulerPostTask: boolean;
}
