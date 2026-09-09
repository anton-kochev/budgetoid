// One screenful of narrative fields opened together: a list of bindings and
// wire values in, an opener out, and nothing kept once the caller has let that
// opener go.
//
// **Nothing here is injectable, and nothing here is module-level state.** There
// is no `@Injectable`, no `providedIn`, no cache, no map declared beside these
// functions — the map is created inside one call to
// {@link openNarrativeBatch}, is captured by the opener that call returns, and
// is unreachable from this module the moment the promise settles. That is not a
// stylistic preference about plain functions; it is the property the rest of
// the client is relying on, twice over.
//
// The first is CON-005: opened narrative text is for a screen to render, and a
// budgeting computation may not reach it. A module-level map here would be a
// place where every description and every payee name a person had looked at
// this session sat, reachable by importing one symbol, with nothing in the
// compiler between it and arithmetic — and it would be reachable long after the
// screen that asked for it had gone. What a caller holds instead is a function
// that answers one field at a time: there is no collection to enumerate, no
// `.values()` to sum over, and the whole of it dies with the closure.
//
// The second is that `SessionService.ended()` is the single owner of clearing
// opened material — the rule `account-keys.md` argues and the reason custody
// clears from there rather than from an `effect()`. Anything this module kept
// between calls would be a second thing to clear, and it would need a clearer
// of its own: `ended()` cannot reach a `const` in a module it does not import,
// so the two would be wired together by somebody remembering, and a `Map` that
// nobody remembered would outlive the session that filled it. There is nothing
// to clear here because there is nothing to keep.
//
// **A rejection propagates rather than becoming a word.** The opens run under
// `Promise.all` and never `allSettled`, for the reason `transaction-view.ts`
// states at length: `NarrativeFieldMisuseError` is a defect in this client — a
// binding the codec refuses, a key whose bytes can be read back out — and it is
// a fact about the *call*, saying nothing whatever about any stored value.
// Filed as one member that did not open, it reaches a template as `unreadable`
// and is drawn as an em dash in front of somebody who can do nothing about it,
// over a row that is perfectly fine. So it reaches the caller instead, and the
// first refusal ends the batch. The one thing that does *not* propagate is a
// failed yield, and {@link openNarrativeBatch} argues that exception where it
// swallows it.
//
// **The key grammar is private, and no caller can ask for it.** A caller never
// builds a key, never reads a map and is never handed one: it is handed an
// opener and calls it with the same binding and wire the mapper names. Two
// spellings of one key would be two opinions about which requests are the same
// request, and — de-duplication being decided on that key — two opinions about
// which opens may be skipped, the half that drifted still answering perfectly
// for every value anybody happened to test.
//
// **A duplicate is one open, and a `locked` answer is de-duplicated with every
// other.** The rule custody is held under is that `locked` is never cached —
// only the two answers a cipher actually produced are kept — and that rule is
// about a store which outlives the moment it was filled: the account unlocks,
// and something still answering `locked` keeps a screen dark forever with
// nothing left to trigger a re-read. Nothing here outlives that moment. And
// re-opening a duplicate mid-batch repairs nothing while costing something:
// skipping a duplicate has to be decided *before* the open, or the duplicates
// serialise behind the answer they are waiting to inspect, trading away the
// concurrency this file is built around. The cost also runs the wrong way. A
// locked account is exactly the case where every field on the screen has the
// same answer, so "re-open the locked ones" spends the most opens in the one
// state where not a single one of them can succeed.
//
// **What that buys is one answer per distinct value, and not one snapshot per
// screen.** The narrower claim is the true one, and it is the whole of the
// justification above. The wider one is false here: two *distinct* fields still
// straddle an unlock, because `AccountKeyCustodyService.openField` reads the
// content key at the moment it is called, so a batch can answer `locked` for
// the fields it reached before an unlock landed and `text` for the ones after
// it — one payee's name drawn as an em dash on one row and as a name on the row
// under it. **The yields in this file are what opened that window.** The path
// this replaced dispatched every open in one synchronous burst, where no unlock
// could land between them. It does not bite in the product, and the reason is
// one layer away and is a coincidence rather than a guard: `TransactionsService`
// calls `load()` on the transition into `unlocked`, and the `switchMap` over
// its loads discards whatever the read that was in flight was going to publish.
// Nothing in this module holds that, and a second caller written without that
// effect would see the straddle.
//
// **The loop holding the list is the thing that yields.** A yield inside the
// opener under one outer `Promise.all` chunks nothing: every open body starts
// in the same synchronous burst, so each one past the budget awaits the same
// yield and all of them resume in the same task. Only something holding the
// whole list can decide how many opens run before it steps aside.
//
// **The frame goes back through `postTask`, and `scheduler.yield()` is refused
// despite having measured cheapest — refused for what it *is* rather than for
// what it cost.** The ordering is argued here from what each mechanism does,
// because that is the half a reader can check. `scheduler.yield()` resumes the
// continuation *ahead* of the browser's own rendering work, so a run chunked
// with it draws no frame and buys precisely nothing — it is the one that looks
// best and the one that does not work, so it may not go back on the strength of
// its number. `postTask` at `user-visible` and a `MessageChannel` message are
// both ordinary tasks, and that is the whole requirement: the browser gets to
// render before the continuation runs. `setTimeout(…, 0)` is a task too and is
// a fallback rather than a choice, because it is the one on the list the
// platform *clamps* — HTML floors a timer nested more than five deep at 4 ms,
// and a batch's yields are nested by construction, so most of a frame goes on
// waiting rather than on working. `requestAnimationFrame` followed by a
// `setTimeout` waits for a frame nobody has asked for yet, which is longer
// still. So the fallback is `MessageChannel`: a real task, no clamp, and
// available wherever `scheduler` is not.
//
// **The millisecond figures that ranked those are recorded in
// `docs/engineering/frontend-performance.md`, and they cannot be re-taken from
// this tree.** They come from one probe — Chrome, a low-tier mobile CPU
// throttle — run outside this repository. `tools/narrative-perf` is not it:
// that rig measures the read path end to end and records which mechanism the
// frame went back through, but it never puts two mechanisms side by side. No
// browser version and no calibration was recorded beside them either, which is
// the provenance that chapter asks of every figure it keeps — so they are cited
// from there rather than quoted here, where the next reader would take them for
// something current, and a reader who needs one of them to be true today has to
// measure again. None of them is what holds the refusal above: the case for
// that is in this folder, and what it watches is `scheduler.yield` never being
// called.
//
// **Eight milliseconds, and the clock is read at a chunk boundary rather than
// after every open.** Eight leaves the rest of a 16 ms frame for the layout and
// paint this work is competing with; twelve was tried in the same probe and
// finishes a batch sooner by spending that headroom, which is the wrong way
// round for work nobody is waiting on a deadline for.
//
// **The number is held to a band rather than to a value**, by a case running a
// known workload over a clock only an open moves. Below about a quarter of a
// frame the batch yields at every chunk and spends more on handing the frame
// back than on opening; above about a frame it never yields at all and the
// chunking is decoration. So a re-measurement may honestly move eight, and `1`,
// `200` and `5000` each redden that case — where before it, `200` was a
// one-character edit that deleted the frame-holding this module exists for with
// the whole suite still green. The clock is read once
// per `OPENS_PER_CLOCK_CHECK` opens because the opens inside a chunk have to
// start together — the batch is concurrent, and a clock read between two of them
// would be a clock read between two things that are meant to be in flight at
// once. Sixteen asks the question several times inside one budget while
// bounding the overshoot at a single chunk, and it means a second thing that
// its declaration spells out.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no key, so one exported function is the whole of it.
import { UNIT_SEPARATOR } from './associated-data';
import type { NarrativeFieldBinding } from './narrative-cipher';
import type { NarrativeOpener, NarrativeText } from './narrative-text';

/** One sealed column to open: where it was found, and what was found there. */
export interface NarrativeRequest {
  /** The binding of the row the value belongs to, never a substitute. */
  readonly binding: NarrativeFieldBinding;
  /** The column's stored value, unpadded base64url over the envelope. */
  readonly wire: string;
}

/**
 * A frame's worth of time, and the way to give the frame back.
 *
 * Resolves once the browser has had a task of its own. What it must *not* be is
 * a mechanism that resumes ahead of rendering — the head of this file has the
 * measurements and names the one that looks cheapest and draws no frame.
 */
export type FrameYield = () => Promise<void>;

/** How long the caller is willing to hold the frame, and how to hand it back. */
export interface FrameBudget {
  /** Milliseconds of work before the frame should be given back. */
  readonly frameBudgetMs: number;
  /** Resolves once the browser has had the frame. */
  readonly yield: FrameYield;
}

/**
 * Opens every request in `requests` and hands back the opener a caller gives
 * its mappers.
 *
 * Two requests naming the same binding *and* the same wire are one open: a
 * screen naming one payee on thirty rows asks the account's key to do the work
 * once. What that costs is nothing a caller can observe, because what it is
 * handed answers on the same four fields the duplicate was found by.
 *
 * **The returned opener answers from the batch, and opens inline on a miss.**
 * That fall-through is the whole reason this returns a function rather than the
 * map it used to. What a batch was asked for is a *guess*: a collector walks the
 * rows ahead of the mappers and lists the fields it expects them to open, and
 * the mappers remain the only authority on which binding a value belongs to. The
 * miss is what keeps those two from being two opinions — a member the collector
 * forgot, or paired with the wrong row, is opened under the binding the *mapper*
 * names, so collector drift costs an open and can never cost a rendered value.
 * Where a wrong binding comes back `unreadable` in silence, that asymmetry is
 * the whole argument for letting a second list of bindings sit beside a mapper
 * at all.
 *
 * **It is composed here because a caller composing it is a caller that can skip
 * it.** This was two exports and a line at the call site pairing them, and
 * replacing that line's fallback with one answering `unreadable` reddened
 * nothing in the suite: a collector and a mapper that agree on every member
 * never reach the miss, so the half that carries the property is invisible from
 * every spec that exercises the pair honestly. Folded in, there is no second
 * argument to get wrong and no composition to leave out, and the miss can be
 * watched from this module's own spec by asking the returned opener for a pair
 * the request list never carried.
 *
 * Opens run concurrently in chunks, and between two chunks the frame goes back
 * to the browser whenever `budget` has been spent since it last did. Inside a
 * chunk every open starts before the first one settles, bounded by
 * {@link OPENS_PER_CLOCK_CHECK}. Omitting `budget` takes the module's own —
 * this runs on a main thread by default, so a caller has to opt *out* of
 * keeping a frame rather than into it.
 *
 * Rejects on whatever `open` rejects on — the first refusal ends the batch and
 * reaches the caller, because a `NarrativeFieldMisuseError` is a defect in this
 * client rather than a column that did not open. The opener handed back rejects
 * the same way, on the inline open a miss costs.
 */
export async function openNarrativeBatch(
  requests: readonly NarrativeRequest[],
  open: NarrativeOpener,
  budget: FrameBudget = defaultFrameBudget(),
): Promise<NarrativeOpener> {
  // De-duplication is decided here, before a single open starts, which is what
  // lets the duplicates stay concurrent: nothing waits to inspect an answer
  // before deciding whether to ask for it.
  const work = new Map<string, NarrativeRequest>();

  for (const request of requests) {
    work.set(narrativeKey(request.binding, request.wire), request);
  }

  const pending = [...work];
  const opened = new Map<string, NarrativeText>();
  let frameStartedAt = performance.now();

  for (let from = 0; from < pending.length; from += OPENS_PER_CLOCK_CHECK) {
    const chunk = pending.slice(from, from + OPENS_PER_CLOCK_CHECK);

    // The key is already built, so `open` is called on every request in the
    // chunk before the first `await` suspends anything: a chunk is one set of
    // concurrent opens rather than a chain of them.
    const entries = await Promise.all(
      chunk.map(
        async ([key, request]): Promise<readonly [string, NarrativeText]> => [
          key,
          await open(request.binding, request.wire),
        ],
      ),
    );

    for (const [key, text] of entries) {
      opened.set(key, text);
    }

    // Nothing is bought by handing the frame back after the last chunk: the
    // work it would have interrupted is over.
    const remaining = pending.length - from - chunk.length;
    const spent = performance.now() - frameStartedAt >= budget.frameBudgetMs;

    if (remaining > 0 && spent) {
      try {
        await budget.yield();
      } catch {
        // **A frame that could not be handed back may not empty a screen.**
        // Every way this throws is a fact about the *scheduler* and none about
        // the values being opened: a `postTask` rejecting with an `AbortError`,
        // a `scheduler` an extension replaced with something carrying the
        // member and not the contract, an environment refusing to construct a
        // `MessageChannel`. It is also thrown *between* two chunks, where the
        // opens on either side have already succeeded or are still to come.
        // Let it out and it leaves through `TransactionsService.#openList` into
        // that pipeline's `catchError`, and the screen says the transactions
        // could not be read over a screenful of rows that were read perfectly.
        // A mechanism whose entire job is smoothness cannot be allowed to cost
        // more than smoothness, so the batch keeps the frame and finishes.
        //
        // This is the only swallow in this file, and it is not the rule for the
        // opens: a refusal from `open` is a defect in this client and
        // propagates, for the reason the head gives at length.
      }

      // Reset whether or not the frame actually went back. It did not, so the
      // budget starts again from here and the next boundary asks once more —
      // one cheap attempt per budget's worth of work, rather than a doomed one
      // at every chunk for the rest of the list.
      frameStartedAt = performance.now();
    }
  }

  return async (binding, wire) =>
    opened.get(narrativeKey(binding, wire)) ?? (await open(binding, wire));
}

/**
 * Opens started together before the clock is read again.
 *
 * **It is a hard ceiling on concurrency as well, which is the half the name
 * does not say.** A chunk is awaited whole, so this is the most opens that are
 * ever in flight — whatever the budget, and however long the list. On a machine
 * reporting eleven cores the ceiling measured as costing nothing against an
 * unchunked run, and no test here re-runs that; anybody tuning this number for
 * the clock is tuning the other thing at the same time.
 */
const OPENS_PER_CLOCK_CHECK = 16;

/** Milliseconds of opening before the frame is owed back. */
const FRAME_BUDGET_MS = 8;

// Built per call rather than held as a `const` beside the functions, for the
// same reason nothing else here is: a shared object in this module is a shared
// object in this module, and the head says why there are none. It costs one
// object literal per batch.
function defaultFrameBudget(): FrameBudget {
  return { frameBudgetMs: FRAME_BUDGET_MS, yield: handBackTheFrame };
}

// The `postTask` half of the platform's scheduling API, described here because
// it is absent from the DOM lib this project compiles against and absent from
// some of the browsers this client supports. Feature-detected, never assumed —
// and, the detection being a member check rather than a contract check, its
// failures are caught at the call site rather than here.
interface PostTaskScheduler {
  postTask(
    callback: () => void,
    options: { priority: 'user-visible' },
  ): Promise<void>;
}

function isPostTaskScheduler(value: unknown): value is PostTaskScheduler {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as Partial<PostTaskScheduler>).postTask === 'function'
  );
}

function handBackTheFrame(): Promise<void> {
  const candidate = (globalThis as { scheduler?: unknown }).scheduler;

  return isPostTaskScheduler(candidate)
    ? candidate.postTask(() => undefined, { priority: 'user-visible' })
    : handBackTheFrameByMessage();
}

// A channel per yield rather than one kept beside the functions. A cached
// `MessageChannel` is precisely the module-level binding this file refuses, and
// it would be one holding a live port for the life of the tab; a fresh pair
// closed on arrival costs a small fraction of the frame it hands back, and the
// figure is in the chapter the head of this file cites.
//
// **This branch is covered, and it was not.** A case stubs `scheduler` absent
// and counts constructions, so replacing this body with the `setTimeout(…, 0)`
// the head rejects reddens it.
function handBackTheFrameByMessage(): Promise<void> {
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

// Four fields, and two of them are load-bearing.
//
// The **wire value** is: one row's column can be read twice in one batch across
// a write, and keying on the binding alone answers the second read with the
// first read's text — the stale description a person has just changed, drawn
// over the row they changed it on. The **row id** is, the other way round: two
// rows can hold byte-identical wire values, and keying on the wire alone
// answers the second row with the first row's text. Each of those has a case
// that fails without it.
//
// **The table and the column are here because the binding carries them, and
// nothing can tell whether they are.** Dropping either leaves the whole suite
// green, and not by an oversight anybody can fix: a row id is a UUID, so two of
// them do not collide across two tables, and a case that proved otherwise would
// have to invent an id no `narrative-row-id.ts` mints. The
// all-four-because-all-four-choose argument belongs to `narrative-cipher.ts`
// and is true where it is written — there the four are *associated data*, and
// dropping one lets a ciphertext moved between two columns open under its new
// binding. Copied onto a cache key it is not true. Keeping them costs a joined
// string and keeps this key spelled like the thing it is keyed on; that is the
// whole of the reason, and it is written down as untrue rather than left for
// the next reader to spend an afternoon failing to write the case for.
//
// **The separator is `associated-data.ts`'s, imported rather than spelled
// again.** The requirement is the one that module's byte already satisfies — a
// character none of the joined fields can contain — and the fields here are a
// closed union, a closed union, a canonical UUID and unpadded base64url, so
// none of them can hold a 0x1F. A second `String.fromCharCode(0x1f)` in this
// folder would be a second definition of one byte, invisible in every tool a
// reviewer reads it in, and the copy that drifted would still key everything it
// had written itself.
//
// **What is imported is the byte and not the join**, because
// `buildAssociatedData` returns UTF-8 and this key is a string that is never
// sealed, never stored and never crosses a wire. It lives as long as one map
// does, so its spelling is a private matter between these two functions and
// nothing already written depends on it.
function narrativeKey(binding: NarrativeFieldBinding, wire: string): string {
  return [binding.table, binding.column, binding.rowId, wire].join(
    UNIT_SEPARATOR,
  );
}
