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

/**
 * Which families of cell one invocation runs.
 *
 * `read` is the transaction-list read the harness was built for; `export` is
 * the whole-account export — decode, open, serialize — over a synthetic
 * account. Selected with `NARRATIVE_PERF_CELLS`; both by default.
 */
export type CellFamily = 'read' | 'export';

/** Both families, in the order they are reported. */
export const CELL_FAMILIES = [
  'read',
  'export',
] as const satisfies readonly CellFamily[];

/**
 * One synthetic account for the export cell: one budget, and how many rows of
 * each owned table it carries.
 *
 * **The cardinalities are part of the measurement here too, and for a different
 * reason than on the read.** Every narrative member of an export is bound to
 * its own row, so no two members share a ciphertext and de-duplication collects
 * nothing — the open count is the number of non-null narrative members, and
 * that is decided by how many rows there are and how many of them carry a note.
 */
export interface ExportShape {
  readonly name: string;
  readonly transactions: number;
  readonly accounts: number;
  readonly payees: number;
  readonly categories: number;
  readonly groups: number;
  /** One transaction in this many has no description. */
  readonly undescribedEvery: number;
  /** One transaction in this many has no payee. */
  readonly payeelessEvery: number;
  /** One transaction in this many has no category. */
  readonly uncategorisedEvery: number;
  /** One category, and one group, in this many carries a description. */
  readonly describedCategoryEvery: number;
  /**
   * The most runs a *throttled* profile takes of this shape, or `null` for the
   * invocation's own count. The report prints the count each cell ran.
   */
  readonly throttledRunCap: number | null;
}

// The one cardinality every export size shares: a person's budget of several
// years, not a ceiling. Only the transaction count moves between the sizes, so
// a difference between two rows is a difference in volume and nothing else.
const EXPORT_ACCOUNT = {
  accounts: 5,
  payees: 150,
  categories: 40,
  groups: 8,
  undescribedEvery: 5,
  payeelessEvery: 10,
  uncategorisedEvery: 12,
  describedCategoryEvery: 2,
} as const;

/**
 * The three export sizes, in the order they are reported.
 *
 * 50k is capped under a throttle because one run of it there took about
 * thirteen seconds on the machine the cap was set on — ten recorded runs and a
 * warm-up would be over two minutes for one cell, where every other cell is
 * seconds. Its spread across runs was under 2% there, so three runs lose little.
 * The cap is printed beside the cell rather than the size being dropped.
 */
export const EXPORT_SHAPES = [
  { name: '1k', transactions: 1_000, ...EXPORT_ACCOUNT, throttledRunCap: null },
  {
    name: '10k',
    transactions: 10_000,
    ...EXPORT_ACCOUNT,
    throttledRunCap: null,
  },
  { name: '50k', transactions: 50_000, ...EXPORT_ACCOUNT, throttledRunCap: 3 },
] as const satisfies readonly ExportShape[];

/** What one prepared export document turned out to be. */
export interface ExportFixtureInfo {
  readonly name: string;
  readonly transactions: number;
  /** The served text's length in UTF-16 units — all ASCII, so also its bytes. */
  readonly textChars: number;
  /** Non-null narrative members: every one of them is one AEAD open. */
  readonly sealedMembers: number;
  /** Narrative members served as `null`, which never reach the opener. */
  readonly nullMembers: number;
}

/** The three phases of an export, in the order they run. */
export type ExportPhase = 'decode' | 'open' | 'serialize';

/** Where one phase sat on the run's clock. */
export interface PhaseSpan {
  readonly phase: ExportPhase;
  readonly startMs: number;
  readonly endMs: number;
}

/**
 * `performance.memory.usedJSHeapSize` at the points the page could read it.
 *
 * The V8 JavaScript heap only: an `ArrayBuffer`'s backing store lives outside
 * it and is not counted. And a sample is taken only when the main thread is
 * free to take one — at a phase boundary, or from a poller that runs between
 * the batch's chunks — so a transient peak *inside* one synchronous call, a
 * `JSON.parse` or a `JSON.stringify`, is invisible to it. The peak is a lower
 * bound on the true one.
 */
export interface HeapReading {
  readonly beforeBytes: number;
  readonly afterDecodeBytes: number;
  readonly afterOpenBytes: number;
  readonly afterSerializeBytes: number;
  readonly peakBytes: number;
  /** How many times the page read the heap over the run. */
  readonly readings: number;
}

/** Everything one export run produced inside the page. */
export interface ExportSample {
  readonly totalMs: number;
  readonly phases: readonly PhaseSpan[];
  /**
   * The synchronous part of `openExportDocument`: from the call to the promise
   * coming back. The member walk, the de-duplication map and the first chunk's
   * dispatch — all of it inside the task `decode` ran in.
   */
  readonly openPrologueMs: number;
  /**
   * From the last cipher settling to `openExportDocument` resolving: the last
   * chunk's bookkeeping, one opener call per member and the second walk that
   * puts every answer back. It runs in one task, and `serialize` continues it.
   */
  readonly openEpilogueMs: number;
  /** AEAD opens the opener actually performed. */
  readonly opens: number;
  /** `longtask` entries overlapping the run, relative to its start. */
  readonly longTasks: readonly LongTask[];
  /**
   * rAF callbacks that fired between the end of the open's prologue and the
   * last cipher settling — the stretch where the batch alone decides whether a
   * frame is drawn.
   */
  readonly interiorFrames: number;
  /**
   * The longest stretch inside that same window with no rAF callback, the
   * window's own edges counting as boundaries.
   */
  readonly interiorWorstFrameGapMs: number;
  readonly heap: HeapReading;
  /** The saved file's length, so a run that wrote nothing cannot pass as fast. */
  readonly outputChars: number;
}

/** The facts a number is not comparable without. */
export interface Environment {
  readonly userAgent: string;
  readonly hardwareConcurrency: number;
  /** `crypto.subtle` is unavailable without this, so it is checked, not assumed. */
  readonly secureContext: boolean;
  /** Whether the frame yield used `scheduler.postTask` or the channel fallback. */
  readonly schedulerPostTask: boolean;
  /** Whether Chrome's non-standard `performance.memory` exists on the page. */
  readonly performanceMemory: boolean;
}
