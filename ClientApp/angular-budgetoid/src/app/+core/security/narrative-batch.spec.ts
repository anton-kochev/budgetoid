// What `openNarrativeBatch` owes its callers.
//
// The module it covers de-duplicates and it yields: two requests naming the
// same binding and the same wire are one call to `open`, and a long list hands
// the frame back between chunks rather than holding it until the last row is
// answered. Neither is visible in what comes back — an opener answers one pair
// at a time and counts nothing — so the cases holding them watch the opener the
// batch was given and the yield, never the answer.
//
// **Not every wrong implementation fails a case here, and one of them cannot be
// made to.** Dropping `table` or `column` from the module's key leaves every
// case below green: a row id is a UUID, so two of them do not collide across
// two tables, and a case that proved otherwise would have to invent an id
// `narrative-row-id.ts` cannot mint. `narrative-batch.ts` says the same thing at
// the key itself, and it is written down here as untrue rather than left for the
// next reader to spend an afternoon failing to write the case for.
//
// The wrong implementations that *can* be caught are caught, and three of the
// assertions below are shaped against one a weaker form would wave through:
//
// - The de-duplication cases count calls on the opener. Counting answers would
//   prove nothing whatever — three identical requests are one question however
//   many times it was asked, so any count of what came back is green against an
//   implementation that skips nothing. The call count is the only observation
//   that separates the two, and it is how every claim about de-duplication in
//   this file is made.
// - The zero-budget case asserts a *range*, not "at least once". An
//   implementation handing the frame back after every single open satisfies
//   `>= 1` while costing a yield per row, so the upper bound is the half that
//   carries the case.
// - The default-budget case asserts a **band** on the module's own
//   `FRAME_BUDGET_MS`, over a clock a fake opener moves by a known amount per
//   open. A case tolerant of any budget at all was there before it, and under
//   it `8` could be edited to `200` — a budget no measured shape ever spends,
//   which deletes the frame-holding this module exists for — with the whole
//   suite still green. It is a band rather than a value because the number is a
//   measurement and not a constant: pinning `8` would forbid a re-tune, and
//   pinning nothing forbids nothing. The case argues its own bounds.
//
// **No case here spells a key, and none can.** The grammar is private to the
// module and there is no map to read it against: what comes back is a function
// of the same two arguments a mapper already holds. A spec that rebuilt a key
// would be the second opinion about which requests are the same request that
// the module's own head warns about, and — de-duplication being decided on that
// key — a second opinion about which opens may be skipped.
//
// **Every case builds its own opener.** The runner configures no
// `restoreMocks`, so a spy shared across cases carries its call history into
// the next one and `toHaveBeenCalledTimes` starts answering about work done in
// a case that has already passed.
//
// **The opener that comes back is covered here because it cannot be covered
// where it is used.** The half of it that matters is the fall-through from the
// batch to an inline open, and a collector and a mapper that agree on every
// member never reach it — so replacing that half with one answering
// `unreadable` reddens nothing over the calling service's own spec or over any
// other spec in its folder. That was verified against the shape this module had
// before, where the fall-through was a line at the call site. The cases in the
// second block below ask the returned opener for a pair the request list never
// carried, which is the only way anything can watch it happen, and they assert
// the *identity* of what the inline open was handed: a reconstruction carries
// the same three fields and passes a structural match while being a second
// opinion about which row a value belongs to.
//
// The other half is that **a hit is a hit whatever word it carries**, and one
// case batches a single `locked` answer to say so. Every other case here opens
// nothing but `text`, so a lookup reading the *state* rather than the presence
// answers all of them correctly and re-opens a locked account row by row.
//
// **One opener now serves both sources**, because the signature takes one, so a
// case can no longer tell the batch from the inline open by handing in two
// different functions. `stagedOpener` restores that: it answers in one spelling
// while the batch runs and in another once a case has flipped it, so what a
// value says is again evidence about where it came from.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Mock } from 'vitest';
import { describe, expect, it, vi } from 'vitest';

import type { FrameBudget, NarrativeRequest } from './narrative-batch';
import { openNarrativeBatch } from './narrative-batch';
import type { NarrativeFieldBinding } from './narrative-cipher';
import { NarrativeFieldMisuseError } from './narrative-cipher';
import type { NarrativeOpener, NarrativeText } from './narrative-text';

// Canonical row ids, because the binding travels into a real key grammar and a
// spelling the codec would refuse is not the thing under test here.
function rowId(index: number): string {
  return `${index.toString(16).padStart(8, '0')}-1111-4111-8111-111111111111`;
}

function description(row: string): NarrativeFieldBinding {
  return { table: 'transactions', column: 'description', rowId: row };
}

function payeeName(row: string): NarrativeFieldBinding {
  return { table: 'payees', column: 'name', rowId: row };
}

// The value a fake open hands back, built from all four fields that choose
// which value this is. Two opens can only be told apart in an assertion if
// their answers differ, and they differ here on exactly what the module's key
// is built from — so a case comparing two opened values is comparing the thing
// the key claims to distinguish.
function textFor(
  binding: NarrativeFieldBinding,
  wire: string,
): NarrativeText & { readonly state: 'text' } {
  return {
    state: 'text',
    value: `${binding.table}.${binding.column}@${binding.rowId}<-${wire}`,
  };
}

// A spy over an opener that always succeeds. Fresh per case, never shared.
function countingOpener(): Mock<NarrativeOpener> {
  const opener: NarrativeOpener = (binding, wire) =>
    Promise.resolve(textFor(binding, wire));

  return vi.fn(opener);
}

// What an inline open answers with, marked so that it can never be mistaken for
// what the batch opened. The cases on the returned opener turn on *which of two
// sources* answered, and an inline open returning `textFor` would be
// indistinguishable from a hit — leaving a call count as the only witness, which
// says nothing about the value that reached the mapper.
function fallbackTextFor(
  binding: NarrativeFieldBinding,
  wire: string,
): NarrativeText & { readonly state: 'text' } {
  return {
    state: 'text',
    value: `fallback<-${textFor(binding, wire).value}`,
  };
}

// One opener with two voices, and the reason it exists is the signature: the
// batch and the miss are served by the same function now, so a case can no
// longer separate them by passing two. `openInline()` is called once the batch
// has settled, and everything opened after it answers in the second spelling —
// so a value carrying the first spelling was answered from the batch and one
// carrying the second was opened on the way past.
interface StagedOpener {
  /** The one opener, handed to `openNarrativeBatch` and nothing else. */
  readonly open: Mock<NarrativeOpener>;
  /** Everything opened from here on answers in the inline spelling. */
  readonly openInline: () => void;
}

// Fresh per case, never shared. `answer` is what the batch's own opens say; the
// `locked` case supplies its own.
function stagedOpener(
  answer: (
    binding: NarrativeFieldBinding,
    wire: string,
  ) => NarrativeText = textFor,
): StagedOpener {
  let inline = false;
  const opener: NarrativeOpener = (binding, wire) =>
    Promise.resolve(
      inline ? fallbackTextFor(binding, wire) : answer(binding, wire),
    );

  return {
    open: vi.fn(opener),
    openInline: (): void => {
      inline = true;
    },
  };
}

// `count` requests, no two of which share a binding or a wire, so nothing an
// implementation could reasonably de-duplicate is present. Used by the two
// cases about frames, where the workload has to be a known number of *opens*
// and not a number of requests that might collapse.
function distinctRequests(count: number): readonly NarrativeRequest[] {
  const requests: NarrativeRequest[] = [];

  for (let index = 0; index < count; index += 1) {
    requests.push({
      binding: description(rowId(index)),
      wire: `wire-${index}`,
    });
  }

  return requests;
}

describe('openNarrativeBatch', () => {
  it('opens one value per distinct binding and wire, however many rows ask for it', async () => {
    // Arrange
    // Two distinct values, asked for five times between them: three rows on a
    // screen naming one transaction's description and two naming another.
    // Five requests rather than three, and two distinct values rather than
    // one, because an implementation that opened the *first* request and
    // answered every later one from it would pass a three-identical-requests
    // case with a single call while being unable to open a second value at
    // all.
    const first = description(rowId(1));
    const second = description(rowId(2));
    const requests: readonly NarrativeRequest[] = [
      { binding: first, wire: 'envelope-one' },
      { binding: first, wire: 'envelope-one' },
      { binding: first, wire: 'envelope-one' },
      { binding: second, wire: 'envelope-two' },
      { binding: second, wire: 'envelope-two' },
    ];
    const open = countingOpener();

    // Act
    const opened = await openNarrativeBatch(requests, open);

    // Assert
    // The whole case, and it is about the opener rather than about what came
    // back. The naive implementation answers all five of these requests
    // correctly already, so nothing read out of the opener separates the two
    // shapes. What does is how many times the account's key was asked to work.
    expect(
      open,
      `opened ${open.mock.calls.length} times for 2 distinct values`,
    ).toHaveBeenCalledTimes(2);

    // And the saving is not made by answering fewer questions: both values are
    // in the batch, each carrying its own text. Without this, an implementation
    // that dropped every duplicate *request* — rather than every duplicate
    // *open* — passes the assertion above.
    expect(await opened(first, 'envelope-one')).toEqual(
      textFor(first, 'envelope-one'),
    );
    expect(await opened(second, 'envelope-two')).toEqual(
      textFor(second, 'envelope-two'),
    );

    // Both of those were answered from the batch rather than opened again on
    // the way out. Said as a count for the same reason the count above is the
    // case: the fall-through answers a hit's value exactly, so the two lines
    // above are green over an implementation that batched nothing and opened
    // each of them inline.
    expect(
      open,
      `opened ${open.mock.calls.length} times once both values had been read back`,
    ).toHaveBeenCalledTimes(2);
  });

  it('hands the frame back once the budget is spent', async () => {
    // Arrange
    // Sixty-four opens, none of them de-duplicable, and a budget of zero
    // milliseconds — so whatever moment an implementation checks the clock at,
    // the budget is already spent and a yield is owed. A generous budget over
    // a fake opener that resolves instantly would let a correct
    // implementation finish inside one frame and never yield, which would make
    // this case unfalsifiable in the wrong direction.
    const requests = distinctRequests(64);

    // One timeline rather than two counters, because *when* the frame went
    // back is half of what this case is about. A count alone is satisfied by
    // an implementation that hands the frame back once after the last open has
    // already resolved — a yield that buys nobody anything, since the work it
    // was supposed to interrupt is over.
    const timeline: ('open' | 'yield')[] = [];
    const opener: NarrativeOpener = (binding, wire) => {
      timeline.push('open');

      return Promise.resolve(textFor(binding, wire));
    };
    const open = vi.fn(opener);
    const frameYield = vi.fn(() => {
      timeline.push('yield');

      return Promise.resolve();
    });
    const budget: FrameBudget = { frameBudgetMs: 0, yield: frameYield };

    // A yield costs a frame — measured at 4.7 ms apiece — so an
    // implementation yielding after every open turns sixty-four rows into
    // three hundred milliseconds of handing the frame back and taking it
    // again. That shape satisfies "yields at least once" perfectly, which is
    // why the bound below is a ceiling as well as a floor. Four is the
    // smallest chunk this tolerates; a real one is larger.
    const mostYields = requests.length / 4;

    // Act
    await openNarrativeBatch(requests, open, budget);

    // Assert
    const yields = frameYield.mock.calls.length;

    expect(
      yields,
      `the frame was handed back ${yields} times for ${requests.length} opens`,
    ).toBeGreaterThanOrEqual(1);
    expect(
      yields,
      `the frame was handed back ${yields} times, at most ${mostYields} was expected`,
    ).toBeLessThanOrEqual(mostYields);

    // And the batch went on working afterwards. Deliberately one-sided: it
    // says opens follow a yield and says nothing about opens preceding one,
    // because an implementation that checks its clock *before* a chunk rather
    // than after it hands the frame back before the first open and is not
    // wrong for doing so.
    expect(
      timeline.lastIndexOf('open') > timeline.indexOf('yield'),
      `the frame went back at ${timeline.indexOf('yield')} of ${timeline.length}, with no open after it`,
    ).toBe(true);
  });

  it('does not carry opened values from one call into the next', async () => {
    // Arrange
    // The CON-005 guard. A `Map` declared beside these functions and captured
    // by every opener passes every single-call assertion in this file, so
    // nothing structural stands between a caller and a store of every
    // description a person looked at this session. Two calls are the only way
    // to see it.
    //
    // Nothing here compares the two openers as objects. A returned closure is
    // a fresh function on every implementation there is — including the one
    // holding a module-level map — so an identity check between them is green
    // whatever was kept, and it would read as a guard while being none.
    const firstBinding = description(rowId(1));
    const secondBinding = payeeName(rowId(2));
    const open = countingOpener();

    // Act
    await openNarrativeBatch(
      [{ binding: firstBinding, wire: 'envelope-one' }],
      open,
    );
    const second = await openNarrativeBatch(
      [{ binding: secondBinding, wire: 'envelope-two' }],
      open,
    );

    // Assert
    // Two calls, two opens, and nothing between them.
    const openedByTheTwoCalls = open.mock.calls.length;

    expect(openedByTheTwoCalls).toBe(2);

    // The second call's opener is asked for the first call's pair. It has to
    // pay an open for it, and the count is the only witness there is: a
    // surviving store answers with exactly the text an inline open would
    // produce, so the *value* is identical on both shapes and says nothing.
    const answer = await second(firstBinding, 'envelope-one');
    const carried = open.mock.calls.length === openedByTheTwoCalls;

    expect(
      carried,
      `the second call answered the first call's pair without opening it`,
    ).toBe(false);

    // A control on the reach: the pair really was answerable, so the line
    // above is about where the answer came from rather than about a refusal.
    expect(answer).toEqual(textFor(firstBinding, 'envelope-one'));
  });

  it('re-opens a value whose wire changed under the same binding', async () => {
    // Arrange
    // One row's column read twice across a write. Keyed on the binding alone,
    // the second read is answered with the first read's text — the stale
    // description a person just changed, drawn over the row they changed it
    // on.
    const binding = description(rowId(1));
    const requests: readonly NarrativeRequest[] = [
      { binding, wire: 'envelope-before' },
      { binding, wire: 'envelope-after' },
    ];
    const open = countingOpener();

    // Act
    const opened = await openNarrativeBatch(requests, open);

    // Assert
    // Two opens, which is what the wire being part of the key buys: keyed on
    // the binding alone these two requests are one, and one open is all this
    // batch pays.
    expect(
      open,
      `opened ${open.mock.calls.length} times for two reads of one column`,
    ).toHaveBeenCalledTimes(2);

    // And each read kept its own text, said before anything else can have
    // fallen through: the count above is still 2 below.
    expect(await opened(binding, 'envelope-before')).toEqual(
      textFor(binding, 'envelope-before'),
    );
    expect(await opened(binding, 'envelope-after')).toEqual(
      textFor(binding, 'envelope-after'),
    );
    expect(open).toHaveBeenCalledTimes(2);
  });

  it('keeps two rows apart when their wire values are identical', async () => {
    // Arrange
    // The inverse mistake. Two different rows can hold byte-identical wire
    // values — a nonce collision is not needed, two columns can simply have
    // been sealed from the same envelope in a fixture or a copy — and a key
    // built from the wire alone answers the second row with the first row's
    // text.
    const wire = 'envelope-shared';
    const first = description(rowId(1));
    const second = description(rowId(2));
    const open = countingOpener();

    // Act
    const opened = await openNarrativeBatch(
      [
        { binding: first, wire },
        { binding: second, wire },
      ],
      open,
    );

    // Assert
    // Two opens, which is what the row id being part of the key buys: keyed on
    // the wire alone these two rows are one question and one open answers both.
    expect(
      open,
      `opened ${open.mock.calls.length} times for two rows holding one envelope`,
    ).toHaveBeenCalledTimes(2);

    const firstText = await opened(first, wire);
    const secondText = await opened(second, wire);

    expect(firstText).toEqual(textFor(first, wire));
    expect(secondText).toEqual(textFor(second, wire));
    expect(open).toHaveBeenCalledTimes(2);

    // Distinct, stated rather than left to be inferred from the two lines
    // above: a fake returning one constant would satisfy them both.
    expect(firstText).not.toEqual(secondText);
  });

  it('rejects with the error the opener rejected with', async () => {
    // Arrange
    // Identity, not a message match. A wrapper carrying the same `name` — or
    // a re-thrown `new NarrativeFieldMisuseError(original.message)` — passes
    // `rejects.toThrow(/…/)` and `rejects.toThrowError(NarrativeFieldMisuseError)`
    // while having replaced the error a caller's `instanceof` chain and any
    // future `cause` would read.
    const refused = new NarrativeFieldMisuseError('binding refused');
    const binding = description(rowId(1));
    const opener: NarrativeOpener = (requestBinding, wire) =>
      wire === 'envelope-bad'
        ? Promise.reject(refused)
        : Promise.resolve(textFor(requestBinding, wire));
    const open = vi.fn(opener);

    // Act
    const rejection: unknown = await openNarrativeBatch(
      [
        { binding, wire: 'envelope-good' },
        { binding, wire: 'envelope-bad' },
      ],
      open,
    ).then(
      () => undefined,
      (error: unknown) => error,
    );

    // Assert
    // The same object, so nothing between the opener and the caller rebuilt
    // it. This is what tells a propagated refusal from a re-thrown copy.
    expect(rejection).toBe(refused);

    // And the type survived, which is the check every caller in the bundle
    // actually makes.
    expect(rejection).toBeInstanceOf(NarrativeFieldMisuseError);
  });

  it('starts every open before the first one settles', async () => {
    // Arrange
    // The concurrency pin, and the only case that can see it: a sequential
    // `for … await` loop returns an identical map and rejects identically, so
    // every other case in this file is green over one.
    //
    // Four requests, deliberately fewer than any plausible chunk, so this
    // stays true once yielding lands and the claim becomes "every open in a
    // chunk starts together".
    const requests = distinctRequests(4);
    let started = 0;
    let startedWhenFirstSettled: number | undefined;

    const opener: NarrativeOpener = (binding, wire) => {
      started += 1;
      const isFirst = started === 1;

      // One microtask, so "the moment the first settles" is a real moment
      // that a sequential implementation reaches with one open started and a
      // concurrent one reaches with all four.
      return Promise.resolve().then(() => {
        if (isFirst) {
          startedWhenFirstSettled = started;
        }

        return textFor(binding, wire);
      });
    };

    const open = vi.fn(opener);

    // Act
    await openNarrativeBatch(requests, open);

    // Assert
    expect(
      startedWhenFirstSettled,
      `${String(startedWhenFirstSettled)} of ${requests.length} opens had started`,
    ).toBe(requests.length);

    // A control on the workload: if the batch had opened fewer requests than
    // it was given, the count above would be about a batch nobody asked for.
    expect(open).toHaveBeenCalledTimes(requests.length);
  });

  it('answers every request even when the budget is exhausted immediately', async () => {
    // Arrange
    // Chunking is where a tail gets dropped: a loop that yields at a boundary
    // and then reads its cursor wrongly answers the first chunk and returns.
    // A budget of zero puts a boundary in front of every chunk, so this is the
    // workload most likely to lose one.
    const requests = distinctRequests(24);
    const frameYield = vi.fn(() => Promise.resolve());
    const budget: FrameBudget = { frameBudgetMs: 0, yield: frameYield };
    const open = countingOpener();

    // Act
    const opened = await openNarrativeBatch(requests, open, budget);

    // Assert
    // Every request was opened by the batch, which is what a dropped tail
    // takes away.
    const openedByTheBatch = open.mock.calls.length;

    expect(
      openedByTheBatch,
      `${openedByTheBatch} of ${requests.length} requests were opened`,
    ).toBe(requests.length);

    // And every one of them is in what came back. Said as "did this cost
    // another open" rather than as "was this undefined", because a request the
    // batch dropped is not missing from the opener — it falls through and
    // answers correctly, so only the count can see it.
    const reopened: NarrativeRequest[] = [];

    for (const request of requests) {
      const before = open.mock.calls.length;

      await opened(request.binding, request.wire);

      if (open.mock.calls.length !== before) {
        reopened.push(request);
      }
    }

    expect(
      reopened,
      `${reopened.length} of ${requests.length} requests were not in the batch`,
    ).toEqual([]);
  });

  it('holds the frame for a few milliseconds and not for a frame or two hundred', async () => {
    // Arrange
    // No `budget` argument, which is half of it. Every other case that omits
    // one passes few enough requests to finish inside a single chunk, and the
    // last chunk is never followed by a yield — so the default the signature
    // reaches for runs nowhere else in this file, and replacing its body with a
    // `throw` would redden nothing above this line. Somebody could put
    // `scheduler.yield()` back tomorrow and undo the measurement this module
    // exists to hold, in silence.
    //
    // The other half is the **number**, and this case pins it to a band. The
    // case that stood here moved its clock a second per read so that any budget
    // short of a few seconds was spent inside the workload — deliberately, to
    // stay ignorant of the constant. What that bought was that `8` could be
    // edited to `200` or to `5000` with the whole suite still green, and at 200
    // the batch never yields on any shape anybody measured: a one-character
    // edit deletes the frame-holding without a red bar anywhere.
    //
    // **A band rather than a value, because the number is a measurement.** Eight
    // was chosen against a 16 ms frame on a calibrated low-tier device, and a
    // re-measurement may honestly move it — `docs/engineering/frontend-performance.md`
    // owns the figures. What may not happen quietly is the budget leaving the
    // range where it means anything: below about a quarter of a frame it yields
    // per chunk and spends more on handing the frame back than on opening, and
    // above about a frame it never yields at all and the chunking is decoration.
    // So the bounds below are what the *band* implies, and a re-tune inside it
    // costs nothing.
    const NARROWEST_BUDGET_MS = 4;
    const WIDEST_BUDGET_MS = 16;

    // A clock only an open moves, and it moves by a sixteenth of a millisecond
    // — fine enough that a chunk of any plausible size costs a small fraction
    // of the narrow end of the band, so the bounds below are about the budget
    // and not about the chunk size the clock happens to be read at.
    const MS_PER_OPEN = 1 / 16;
    const requests = distinctRequests(1024);
    const workloadMs = requests.length * MS_PER_OPEN;

    // What the band implies, and the whole of the arithmetic: a run costing
    // `workloadMs` and handing the frame back every `budget` milliseconds
    // yields `workloadMs / budget` times. The narrow end of the band is
    // therefore the ceiling and the wide end the floor — inverted, because a
    // smaller budget yields more often.
    //
    // The floor is one short, which is the overshoot rather than a fudge: the
    // clock is read at a chunk boundary and the last boundary never yields, so
    // an exact division can lose one at either end.
    const mostYields = workloadMs / NARROWEST_BUDGET_MS;
    const fewestYields = workloadMs / WIDEST_BUDGET_MS - 1;

    let now = 0;
    const clock = vi.spyOn(performance, 'now').mockImplementation(() => now);
    const opener: NarrativeOpener = (binding, wire) => {
      now += MS_PER_OPEN;

      return Promise.resolve(textFor(binding, wire));
    };
    const open = vi.fn(opener);

    // The platform's scheduler, faked so the frame can be counted, and
    // carrying both members. `postTask` is the mechanism the measurements
    // chose; `yield` is the one they refuse, because it resumes the
    // continuation ahead of the browser's own rendering and a run chunked with
    // it drew no frame at all. Both are here so that swapping one for the
    // other is visible rather than invisible.
    const priorities: string[] = [];
    const postTask = vi.fn(
      (callback: () => void, options: { priority: string }) => {
        priorities.push(options.priority);
        callback();

        return Promise.resolve();
      },
    );
    const schedulerYield = vi.fn(() => Promise.resolve());

    vi.stubGlobal('scheduler', { postTask, yield: schedulerYield });

    try {
      // Act
      await openNarrativeBatch(requests, open);

      // Assert
      // The floor. A budget wider than the band never spends itself over
      // `workloadMs` of opens and hands the frame back never — which is what
      // `200` does to every shape this product has.
      const yields = postTask.mock.calls.length;

      expect(
        yields,
        `${workloadMs} ms of opens handed the frame back ${yields} times, at least ${fewestYields} was expected of a budget of ${WIDEST_BUDGET_MS} ms or less`,
      ).toBeGreaterThanOrEqual(fewestYields);

      // The ceiling, and the reason the zero-budget case gives: a yield costs
      // a frame, so a budget below the band spends the run handing the frame
      // back and taking it again.
      expect(
        yields,
        `${workloadMs} ms of opens handed the frame back ${yields} times, at most ${mostYields} was expected of a budget of ${NARROWEST_BUDGET_MS} ms or more`,
      ).toBeLessThanOrEqual(mostYields);

      // Through the priority the measurement was taken at, and never through
      // the mechanism that costs nothing because it buys nothing.
      expect(new Set(priorities)).toEqual(new Set(['user-visible']));
      expect(schedulerYield).not.toHaveBeenCalled();

      // A control on the workload, so the counts above are about a batch that
      // opened everything it was given.
      expect(open).toHaveBeenCalledTimes(requests.length);
    } finally {
      clock.mockRestore();
      vi.unstubAllGlobals();
    }
  });

  it('hands the frame back through a channel message where there is no scheduler', async () => {
    // Arrange
    // The sibling of the case above, and it exists because that one stubs
    // `scheduler`: with the member present the `MessageChannel` branch beneath
    // it is dead code under test, and swapping it for the `setTimeout(…, 0)`
    // the measurements rejected reddens nothing. That branch is not a detail —
    // the nested-timeout clamp costs 4.68 ms a yield against a channel
    // message's 0.10 ms, which is most of a frame spent waiting rather than
    // working, on exactly the browsers that have no `scheduler` to fall back
    // from.
    //
    // No browser and no wall clock: a constructor that counts, and a fake clock
    // an open moves far enough to owe a frame at the one boundary this
    // workload has.
    const requests = distinctRequests(32);
    const channels = vi.fn();

    class CountedMessageChannel extends MessageChannel {
      constructor() {
        super();
        channels();
      }
    }

    let now = 0;
    const clock = vi.spyOn(performance, 'now').mockImplementation(() => now);
    const opener: NarrativeOpener = (binding, wire) => {
      now += 100;

      return Promise.resolve(textFor(binding, wire));
    };
    const open = vi.fn(opener);

    // Absent rather than replaced, which is the shape being covered: a browser
    // without the API, not one with a broken one.
    vi.stubGlobal('scheduler', undefined);
    vi.stubGlobal('MessageChannel', CountedMessageChannel);

    try {
      // Act
      await openNarrativeBatch(requests, open);

      // Assert
      // A channel was opened, which no other mechanism on the list does — and
      // the batch went on to finish, so the message really did come back.
      expect(channels).toHaveBeenCalled();
      expect(open).toHaveBeenCalledTimes(requests.length);
    } finally {
      clock.mockRestore();
      vi.unstubAllGlobals();
    }
  });

  it('spends its budget as elapsed time and not as a count of opens', async () => {
    // Arrange
    // Every other case here passes `frameBudgetMs: 0`, which owes a yield at
    // every boundary and therefore cannot be told apart from "hand the frame
    // back every N opens". An implementation reading no clock at all passes
    // the lot of them, and so does one reading `Date.now()`. This case runs
    // one workload twice and compares the two counts.
    //
    // The workload is large so that the comparison survives the chunk size
    // being tuned: green from two opens per chunk to thirty-two. A chunk of
    // sixty-four would leave too few boundaries for one run to yield fewer
    // times than the other, and the answer to that is a longer workload here
    // rather than a weaker assertion below.
    const requests = distinctRequests(256);

    // A clock only an open moves. A fake opener resolving in a microtask
    // spends no milliseconds of anybody's budget, so a generous budget over
    // one is never spent and the comparison below would be between two zeroes.
    const MS_PER_OPEN = 1;
    let now = 0;
    const clock = vi.spyOn(performance, 'now').mockImplementation(() => now);

    function slowOpener(): Mock<NarrativeOpener> {
      const opener: NarrativeOpener = (binding, wire) => {
        now += MS_PER_OPEN;

        return Promise.resolve(textFor(binding, wire));
      };

      return vi.fn(opener);
    }

    // A quarter of the whole run's cost: generous enough that a clock-reading
    // implementation yields a handful of times, and small enough that it
    // yields at all.
    const generousMs = (requests.length * MS_PER_OPEN) / 4;

    // Roughly one yield per budget's worth of work, doubled for the chunk the
    // clock is read at rather than the open.
    const mostYields = (2 * requests.length * MS_PER_OPEN) / generousMs;

    async function yieldsUnder(frameBudgetMs: number): Promise<number> {
      now = 0;
      const frameYield = vi.fn(() => Promise.resolve());
      const open = slowOpener();

      await openNarrativeBatch(requests, open, {
        frameBudgetMs,
        yield: frameYield,
      });

      // A control on each run, so the counts are about a batch that opened
      // everything it was given.
      expect(open).toHaveBeenCalledTimes(requests.length);

      return frameYield.mock.calls.length;
    }

    try {
      // Act
      // Sequentially, because both runs move one clock.
      const underNoBudget = await yieldsUnder(0);
      const underGenerousBudget = await yieldsUnder(generousMs);

      // Assert
      // A control on the workload: with nothing budgeted every boundary owes a
      // yield, so there is something for the generous run to be fewer than.
      expect(underNoBudget).toBeGreaterThanOrEqual(1);

      // A clock is read, and it is the one fake time moves. An implementation
      // asking `Date.now()` sees a run that took no real time at all and hands
      // the frame back never.
      expect(
        underGenerousBudget,
        `a ${generousMs} ms budget over ${requests.length * MS_PER_OPEN} ms of opens yielded ${underGenerousBudget} times`,
      ).toBeGreaterThanOrEqual(1);

      // And the budget is a quantity rather than a boundary count: the same
      // workload under a budget it can afford hands the frame back fewer times
      // than under no budget at all. "Yield every N opens" makes these equal.
      expect(
        underGenerousBudget,
        `${underGenerousBudget} yields under a ${generousMs} ms budget against ${underNoBudget} under none`,
      ).toBeLessThan(underNoBudget);

      // The other half of the same claim, and the half that notices a frame
      // start that is never reset after a yield: from the first yield on,
      // every later boundary would be over budget by construction.
      expect(
        underGenerousBudget,
        `${underGenerousBudget} yields under a ${generousMs} ms budget, at most ${mostYields} was expected`,
      ).toBeLessThanOrEqual(mostYields);
    } finally {
      clock.mockRestore();
    }
  });
});

describe('the opener openNarrativeBatch hands back', () => {
  it('answers a pair the batch holds without opening it again', async () => {
    // Arrange
    // The hit, which is the only thing the collector is there to buy. The pair
    // is put there by the module rather than by a literal, because the key
    // grammar belongs to the module and a spelling written here would drift
    // from it while still answering perfectly for whatever anybody tested.
    const binding = description(rowId(1));
    const staged = stagedOpener();
    const opened = await openNarrativeBatch(
      [{ binding, wire: 'envelope-one' }],
      staged.open,
    );

    // Everything opened from here on says so, so the answer below carries
    // where it came from.
    staged.openInline();

    // Act
    const answer = await opened(binding, 'envelope-one');

    // Assert
    // The batch's value rather than the inline spelling, which is what
    // separates *answered from the batch* from *opened on the way out* in the
    // value itself.
    expect(answer).toEqual(textFor(binding, 'envelope-one'));

    // And no open was spent. Without this an implementation that batched and
    // then ignored what it batched — the collector reduced to a list nobody
    // reads — passes the line above wherever the two sources agree.
    expect(staged.open).toHaveBeenCalledTimes(1);
  });

  it('answers a locked pair the batch holds without opening it again', async () => {
    // Arrange
    // A batch whose one answer is `locked`, which is the one shape that can see
    // a lookup reading the *state* rather than the presence. `locked` is
    // exactly the case where every field on the screen has the same answer and
    // not one of them can succeed, so treating anything but `text` as a miss
    // spends an inline open per row in the one state where no open can work.
    //
    // **What this does not buy is that a screenful is one snapshot**, and the
    // module's own head says so. De-duplication promises one answer per
    // *identical* pair and nothing wider: two *distinct* fields still straddle
    // an unlock, because custody reads the content key at the moment it is
    // called, so a batch can answer `locked` for the fields it reached before
    // an unlock landed and `text` for the ones after it — and the yields are
    // what opened that window. It does not bite in the product for a reason one
    // layer away and coincidental: `TransactionsService` calls `load()` on the
    // transition into `unlocked` and the `switchMap` over its loads discards
    // the read that was in flight. Nothing here holds it.
    const binding = description(rowId(1));
    const staged = stagedOpener(() => ({ state: 'locked' }));
    const opened = await openNarrativeBatch(
      [{ binding, wire: 'envelope-one' }],
      staged.open,
    );

    staged.openInline();

    // Act
    const answer = await opened(binding, 'envelope-one');

    // Assert
    // The batch's word reached the caller unchanged.
    expect(answer).toEqual({ state: 'locked' });

    // And no open was spent on it. This is the half the `text` hit cannot
    // carry on the count alone: an inline open answers `text` here, so a
    // re-open would be visible in the value as well.
    expect(staged.open).toHaveBeenCalledTimes(1);
  });

  it('opens a pair the batch is missing inline', async () => {
    // Arrange
    // A member the collector forgot. The mapper still names it, and the
    // property this whole return value exists for is that it is opened anyway
    // rather than drawn as an em dash over a row that is perfectly fine. A pair
    // the request list never carried is the only way anything can watch that
    // happen: a collector and a mapper that agree on every member never reach
    // it, which is why replacing this half with `unreadable` reddened nothing
    // while the fall-through lived at the call site.
    const collected = description(rowId(1));
    const forgotten = payeeName(rowId(2));
    const staged = stagedOpener();
    const opened = await openNarrativeBatch(
      [{ binding: collected, wire: 'envelope-one' }],
      staged.open,
    );

    staged.openInline();

    // Act
    const answer = await opened(forgotten, 'envelope-two');

    // Assert
    // The inline open's own value reached the caller, so the miss did not
    // become `undefined`, an `unreadable`, or the collected pair's text on its
    // way out.
    expect(answer).toEqual(fallbackTextFor(forgotten, 'envelope-two'));

    // The batch's one open, and one more — a miss costs one open and not two.
    expect(staged.open).toHaveBeenCalledTimes(2);

    // And with exactly what it was asked for. Identity rather than
    // `toHaveBeenCalledWith`, which compares structurally: a binding rebuilt
    // here — spread, or parsed back out of a key — carries the same three
    // fields, passes a structural match, and is the second opinion about which
    // row a value belongs to that the module's head refuses. The mapper is the
    // only authority on the binding, so the object it named is the object the
    // opener must receive.
    expect(staged.open.mock.calls[1][0]).toBe(forgotten);
    expect(staged.open.mock.calls[1][1]).toBe('envelope-two');
  });

  it('rejects with the error the inline open rejected with', async () => {
    // Arrange
    // Identity, not a message match, for the reason the batch's own rejection
    // case gives: a wrapper carrying the same `name` — or a re-thrown
    // `new NarrativeFieldMisuseError(original.message)` — passes
    // `rejects.toThrow(/…/)` while having replaced the error a caller's
    // `instanceof` chain and any future `cause` would read.
    //
    // One opener that answers while the batch runs and refuses afterwards,
    // because the batch and the miss are served by the same function: an
    // opener that refused from the start would reject the batch itself and
    // this case would be the one above it.
    const refused = new NarrativeFieldMisuseError('binding refused');
    const collected = description(rowId(1));
    const forgotten = payeeName(rowId(2));
    let batched = false;
    const opener: NarrativeOpener = (binding, wire) =>
      batched
        ? Promise.reject(refused)
        : Promise.resolve(textFor(binding, wire));
    const open = vi.fn(opener);
    const opened = await openNarrativeBatch(
      [{ binding: collected, wire: 'envelope-one' }],
      open,
    );

    batched = true;

    // Act
    const rejection: unknown = await opened(forgotten, 'envelope-two').then(
      () => undefined,
      (error: unknown) => error,
    );

    // Assert
    // The same object, so nothing between the inline open and the mapper
    // rebuilt it.
    expect(rejection).toBe(refused);

    // And the type survived, which is the check every caller in the bundle
    // actually makes.
    expect(rejection).toBeInstanceOf(NarrativeFieldMisuseError);

    // A control on the case: the rejection came from the miss being opened,
    // rather than from anything the batch itself did.
    expect(open).toHaveBeenCalledTimes(2);
  });

  it('opens inline when the binding matches and the wire does not', async () => {
    // Arrange
    // The loose-key defect in miniature, on the reading side this time. A
    // lookup keyed on the binding alone answers this with the text opened for
    // `envelope-before` — the stale description a person has just changed,
    // drawn over the row they changed it on — and never opens anything at all.
    const binding = description(rowId(1));
    const staged = stagedOpener();
    const opened = await openNarrativeBatch(
      [{ binding, wire: 'envelope-before' }],
      staged.open,
    );

    staged.openInline();

    // Act
    const answer = await opened(binding, 'envelope-after');

    // Assert
    expect(answer).toEqual(fallbackTextFor(binding, 'envelope-after'));

    // Said the other way round as well, because it is the one wrong answer
    // this case exists to name.
    expect(answer).not.toEqual(textFor(binding, 'envelope-before'));

    expect(staged.open).toHaveBeenCalledTimes(2);
    expect(staged.open.mock.calls[1][1]).toBe('envelope-after');
  });
});

// The module's source, read from `src/` rather than from a bundle. The claim is
// about what was written — a decorator, a registration, a binding declared
// beside the functions — and none of it is observable from a spec that imports
// one function and calls it. `field-label-single-source.spec.ts` reads
// source for the same reason and is the shape this follows.
const modulePath = join(
  process.cwd(),
  'src',
  'app',
  '+core',
  'security',
  'narrative-batch.ts',
);

// Comments blanked rather than deleted, so line numbers in a finding are the
// line numbers in the file. Blanking also keeps this scan honest about the
// module as it stands today: the head of `narrative-batch.ts` *names*
// `@Injectable` and `providedIn` in prose, in the sentence promising neither is
// there, so a scan over the raw text reports the file for the comment that
// promises it is clean.
function codeWithoutComments(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (block) => block.replace(/[^\n]/g, ' '))
    .split('\n')
    .map((line) => line.replace(/(^|\s)\/\/.*$/, '$1'))
    .join('\n');
}

// Anchored at column zero, which is what makes this a rule about *module-level*
// state rather than about state at all. A `const entries = …` inside
// `openNarrativeBatch` is indented and is the correct implementation; the same
// declaration one level out is the thing CON-005 refuses.
function forbiddenDeclarations(source: string): readonly string[] {
  return codeWithoutComments(source)
    .split('\n')
    .map((line, index) => {
      const at = `line ${index + 1}`;

      if (line.includes('@Injectable')) {
        return `${at}: injectable — ${line.trim()}`;
      }

      if (line.includes('providedIn')) {
        return `${at}: injector registration — ${line.trim()}`;
      }

      if (/^(export\s+)?(let|var)\s/.test(line)) {
        return `${at}: module-level mutable binding — ${line.trim()}`;
      }

      if (
        /^(export\s+)?const\s+[\w$]+[^=]*=\s*new\s+(Map|WeakMap|Set|WeakSet)\b/.test(
          line,
        )
      ) {
        return `${at}: module-level collection — ${line.trim()}`;
      }

      return undefined;
    })
    .filter((finding): finding is string => finding !== undefined);
}

describe('the narrative batch module', () => {
  it('declares no injectable and no module-level state', () => {
    // Arrange
    const source = readFileSync(modulePath, 'utf8');
    const code = codeWithoutComments(source);

    // A source built to carry all four shapes, so the scan below is known to
    // be able to speak. Without it, a needle that matched nothing — or a
    // comment stripper that blanked the whole file — reports the real module
    // clean and this case passes for the wrong reason forever.
    // One shape per line, because a line reports at most one finding: the two
    // written together on a real decorator would be counted once and the
    // ordering below would be about a scan nobody wrote.
    const planted = [
      '@Injectable()',
      'export class NarrativeBatchService {',
      '  providedIn: "root",',
      '}',
      'let openedSoFar = 0;',
      'const cache = new Map<string, unknown>();',
    ].join('\n');

    // Act
    const findings = forbiddenDeclarations(source);
    const plantedFindings = forbiddenDeclarations(planted);

    // Assert
    // Controls on the read, before any claim about what it found. A path that
    // moved throws, but a stripper that ate the code, or one that ate nothing,
    // both hand a clean result to the assertion that matters.
    expect(code).toContain('export async function openNarrativeBatch(');
    expect(code).not.toContain('CON-005');

    // The pin. Named, not counted: this is a rule about what is written and
    // where, so the line is the whole of the finding.
    expect(
      findings,
      `narrative-batch.ts carries: ${findings.join(' | ')}`,
    ).toEqual([]);

    // And the same scan over the planted source finds each shape, so the
    // clean result above is an absence rather than a blind spot.
    expect(plantedFindings).toHaveLength(4);
    expect(plantedFindings[0]).toContain('injectable');
    expect(plantedFindings[1]).toContain('injector registration');
    expect(plantedFindings[2]).toContain('module-level mutable binding');
    expect(plantedFindings[3]).toContain('module-level collection');
  });
});
