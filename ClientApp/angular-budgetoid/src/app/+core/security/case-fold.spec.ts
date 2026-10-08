// Full Unicode case folding, over the table this product ships rather than the
// one the host happens to have.
//
// The module's own header argues the case for shipping data; these are the
// assertions that make the argument checkable. Three of them carry the weight.
//
// **Every known answer below is a case a `toLowerCase` implementation fails**,
// and each row asserts both halves — what the fold gives and what
// `toLowerCase` gives — so a case cannot quietly become vacuous. If a future
// runtime ever made `toLowerCase` agree, the row would redden and a reader would
// be told, rather than the file continuing to claim a separation it no longer
// tests. The five shapes the brief names are all here: 'ß'/'ẞ', the two sigmas,
// the micro sign, the long s, and Cherokee — which folds *upward*, so the
// lowercase spelling gives the other value and a lowercasing transform can only
// ever go the wrong way.
//
// **The whole-space cross-check is one-directional, and the caveat matters.**
// `/\p{Changes_When_Casefolded}/u` reads the *engine's* Unicode data, which on
// this runner is ICU's 16.0 while the table is 17.0. So the sweep can find code
// points the engine folds and the table misses — under-coverage of what the host
// already knows — and can never find a disagreement with 17.0. Measured on this
// runner (Node 22.14.0, `process.versions.unicode` reporting 16.0): zero misses,
// and 52 code points the table folds that the engine does not. That surplus is
// the reason this table ships instead of a platform call, so the second case
// asserts it is non-empty rather than pinning 52 — a routine ICU bump should not
// redden a security spec, but the surplus vanishing entirely would mean the
// table had stopped being ahead of the host.
//
// **The locale case would prove nothing without `I` and `i` in it.** Turkish is
// the one locale where the ASCII pair diverges, so it is the only input that can
// tell a `toLocaleLowerCase()` implementation from a table lookup. The
// simulation replaces the two locale-sensitive prototype methods so that a call
// naming no locale behaves as `tr`, which is exactly what a Turkish host would
// do to them, and a negative control asserts the replacement took before
// anything is concluded from it.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

import { CASE_FOLDING_UNICODE_VERSION, foldCase } from './case-fold';
import { CASE_FOLD_MULTI, CASE_FOLD_RUNS } from './case-fold-table';

// ---------------------------------------------------------------------------
// The frozen vectors.

// Read from `docs/` rather than transcribed, for the reason
// `narrative-cipher.spec.ts` gives at its own reader: a value copied into a spec
// is a second copy of the contract, and the copy that drifts still passes its
// own file. From a spec, `process.cwd()` is the Angular project directory.
const VECTOR_FILE_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'blind-index-v1.json',
);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requireString(source: Record<string, unknown>, key: string): string {
  const value = source[key];

  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`The vector file carries no ${key}.`);
  }

  return value;
}

interface TableContract {
  readonly unicodeVersion: string;
  readonly caseFoldTableSha256: string;
}

function readTableContract(text: string): TableContract {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The vector file is not an object.');
  }

  return {
    unicodeVersion: requireString(parsed, 'unicodeVersion'),
    caseFoldTableSha256: requireString(parsed, 'caseFoldTableSha256'),
  };
}

const TABLE_CONTRACT = readTableContract(
  readFileSync(VECTOR_FILE_PATH, 'utf8'),
);

const utf8 = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

async function sha256Hex(text: string): Promise<string> {
  return toHex(
    new Uint8Array(await crypto.subtle.digest('SHA-256', utf8.encode(text))),
  );
}

// ---------------------------------------------------------------------------
// The version and the data behind it.

describe('the case-folding table this product ships', () => {
  it('names the Unicode version the frozen vectors were computed under', () => {
    // Arrange
    // Both sides of this comparison are the product's choice rather than the
    // host's, which is the whole reason the constant is exported: the vectors
    // and the table have to agree on a version, and nothing but this can say so.

    // Act
    const declared = CASE_FOLDING_UNICODE_VERSION;

    // Assert
    expect(declared).toBe('17.0');
    expect(declared).toBe(TABLE_CONTRACT.unicodeVersion);
  });

  it('hashes to the digest the frozen vectors record for it', async () => {
    // Arrange
    // **How the digest is composed, written down because a wrong guess looks
    // like corrupted data rather than a wrong test.** It is
    // `SHA-256(CASE_FOLD_RUNS ‖ "\n" ‖ CASE_FOLD_MULTI)` over the UTF-8 of the
    // two *joined* literals — the `+` concatenations in `case-fold-table.ts` are
    // line wrapping and are already gone by the time the module exports them, so
    // this is a fact about the data and not about how the file happens to be
    // wrapped. One `\n` between the two, none at either end.
    const composed = `${CASE_FOLD_RUNS}\n${CASE_FOLD_MULTI}`;

    // Act
    const digest = await sha256Hex(composed);

    // Assert
    expect(digest).toBe(TABLE_CONTRACT.caseFoldTableSha256);
  });
});

// ---------------------------------------------------------------------------
// Known answers, every one of them a `toLowerCase` failure.

interface FoldRow {
  readonly why: string;
  readonly input: string;
  readonly folds: string;
  readonly lowercases: string;
}

// `folds` is what the table gives; `lowercases` is what the platform's
// lowercase *mapping* gives, measured on this runner. Both are asserted: the
// first is the contract, the second is what makes the row a separation rather
// than a coincidence.
const DIVERGENT_ROWS: readonly FoldRow[] = [
  {
    why: 'sharp s expands, and lowercasing leaves it alone',
    input: 'ß',
    folds: 'ss',
    lowercases: 'ß',
  },
  {
    why: 'capital sharp s expands too, where lowercasing gives the small one',
    input: 'ẞ',
    folds: 'ss',
    lowercases: 'ß',
  },
  {
    why: 'final sigma unifies with the medial one; lowercasing keeps it final',
    input: 'ς',
    folds: 'σ',
    lowercases: 'ς',
  },
  {
    why: 'and in context, where lowercasing is the transform that chooses final',
    input: 'ΟΔΟΣ',
    folds: 'οδοσ',
    lowercases: 'οδος',
  },
  {
    why: 'the micro sign folds to Greek mu; lowercasing leaves the sign',
    input: 'µ',
    folds: 'μ',
    lowercases: 'µ',
  },
  {
    why: 'long s folds to s; lowercasing leaves it, being already lower case',
    input: 'ſ',
    folds: 's',
    lowercases: 'ſ',
  },
  {
    why: 'Cherokee folds upward, so the lowercase shape gives the other value',
    input: 'ꭰ',
    folds: 'Ꭰ',
    lowercases: 'ꭰ',
  },
  {
    why: 'and the uppercase shape folds to itself, which lowercasing undoes',
    input: 'Ꭰ',
    folds: 'Ꭰ',
    lowercases: 'ꭰ',
  },
  {
    why: 'the fi ligature decomposes under the fold, never under lowercasing',
    input: 'ﬁ',
    folds: 'fi',
    lowercases: 'ﬁ',
  },
  {
    why: 'the Armenian ech-yiwn ligature, the rest of the multi-output shape',
    input: 'և',
    folds: 'եւ',
    lowercases: 'և',
  },
];

describe('foldCase', () => {
  it.each(DIVERGENT_ROWS)(
    'folds $input to $folds, where toLowerCase gives $lowercases — $why',
    ({ input, folds, lowercases }) => {
      // Arrange
      // The lowercase half is asserted first so that a runtime whose
      // `toLowerCase` had changed reddens here, naming the reason, rather than
      // leaving the row silently proving nothing.

      // Act
      const folded = foldCase(input);

      // Assert
      expect(input.toLowerCase()).toBe(lowercases);
      expect(folded).toBe(folds);
      expect(folded).not.toBe(lowercases);
    },
  );

  it('folds the astral entries, which a UTF-16-unit loop cannot see', () => {
    // Arrange
    // 307 of the table's entries live above U+FFFF. A `charCodeAt` loop over one
    // of them sees two surrogates, finds neither in the table, and hands back
    // the input unfolded — a well-formed string of the right length whose only
    // symptom is a name in Deseret, Osage or Adlam that stops matching its own
    // other spelling.
    const deseret = '\u{10400}';
    const osage = '\u{104b0}';
    const adlam = '\u{1e900}';

    // Act
    const folded = [deseret, osage, adlam].map((one) => foldCase(one));

    // Assert
    expect(folded).toEqual(['\u{10428}', '\u{104d8}', '\u{1e922}']);
  });

  it('returns a code point the table does not name as itself', () => {
    // Arrange
    // The table holds no identity entries, so "absent" and "folds to itself" are
    // one answer. ASCII that is already lower case, digits, punctuation, an
    // emoji and the dotless i — which carries no C or F folding — all pass
    // through.

    // Act
    const folded = foldCase('budget 2026 \u{1f4b0} ı');

    // Assert
    expect(folded).toBe('budget 2026 \u{1f4b0} ı');
  });

  it('lengthens the answer where an entry has more than one output', () => {
    // Arrange
    // **The one case here that asserts the fold makes a string longer**, which
    // is the multi-output half of the table and the reason this returns a
    // string rather than mapping in place: three code points in, six out,
    // because each of the three expands to two. No lowercase *mapping* and no
    // implementation assuming a one-to-one map can reach that answer — both
    // hand back three code points — so the relation is asserted alongside the
    // exact value rather than left for a reader to work out from two numbers.
    const input = 'ßẞﬁ';

    // Act
    const folded = foldCase(input);

    // Assert
    expect([...input]).toHaveLength(3);
    expect(folded).toBe('ssssfi');
    expect([...folded]).toHaveLength(6);
    expect([...folded].length).toBeGreaterThan([...input].length);
  });
});

// ---------------------------------------------------------------------------
// The whole-space cross-check.

// One sweep, both directions, computed once because the two cases below read
// opposite halves of it and 1.1 million fold calls is 165 ms measured.
interface Sweep {
  readonly missedByTable: readonly number[];
  readonly surplusOverEngine: readonly number[];
  readonly engineFolds: number;
}

function sweepWholeSpace(): Sweep {
  // `\p{Changes_When_Casefolded}` is the engine's property data, at whatever
  // Unicode version the host shipped. That is the asymmetry the caveat above is
  // about and it cannot be closed from inside the runner.
  const changesWhenCasefolded = /\p{Changes_When_Casefolded}/u;
  const missedByTable: number[] = [];
  const surplusOverEngine: number[] = [];
  let engineFolds = 0;

  for (let codePoint = 0; codePoint <= 0x10ffff; codePoint += 1) {
    // Lone surrogates are not code points a name can carry, and the encoder
    // downstream substitutes U+FFFD for them anyway.
    if (codePoint >= 0xd800 && codePoint <= 0xdfff) {
      continue;
    }

    const character = String.fromCodePoint(codePoint);
    const engineSaysFolds = changesWhenCasefolded.test(character);
    const tableFolds = foldCase(character) !== character;

    if (engineSaysFolds) {
      engineFolds += 1;
    }

    if (engineSaysFolds && !tableFolds) {
      missedByTable.push(codePoint);
    }

    if (tableFolds && !engineSaysFolds) {
      surplusOverEngine.push(codePoint);
    }
  }

  return { missedByTable, surplusOverEngine, engineFolds };
}

function asCodePointNames(codePoints: readonly number[]): readonly string[] {
  return codePoints.map(
    (codePoint) => `U+${codePoint.toString(16).toUpperCase().padStart(4, '0')}`,
  );
}

describe('the table against the whole of Unicode', () => {
  it('misses no code point the engine says folds', () => {
    // Arrange
    // **One-directional, and the direction is the caveat.** The engine's
    // property data is its own Unicode version — 16.0 on this runner against
    // the table's 17.0 — so this finds under-coverage of what the host already
    // knows and can never find a disagreement with 17.0. A code point the table
    // dropped in a regeneration is exactly the mistake it does catch, and the
    // symptom it would otherwise have is a name that stops matching its own
    // other spelling with nothing anywhere going red.

    // Act
    const { missedByTable, engineFolds } = sweepWholeSpace();

    // Assert
    // The engine count is asserted too, so a `\p{…}` that stopped matching
    // anything cannot make the first assertion pass by having nothing to find.
    expect(engineFolds).toBeGreaterThan(1000);
    expect(asCodePointNames(missedByTable)).toEqual([]);
  });

  it('folds code points the engine does not, which is why it ships', () => {
    // Arrange
    // Measured on this runner: 52 of them — U+01F0, the Greek
    // circumflex-and-iota cluster around U+1FB6, U+A7CE and the Medefaidrin
    // block at U+16EA0. Each is a code point a platform call would leave
    // unfolded, so two clients asking their hosts would key one name to two
    // values: a duplicate that never merges, on a column whose whole purpose is
    // that equal names collide.
    //
    // Asserted as non-empty rather than as 52. Pinning the count would redden
    // this file on a routine ICU bump, which says nothing about the product;
    // the surplus reaching zero would say the table had stopped being ahead of
    // the host, which is the claim worth holding.

    // Act
    const { surplusOverEngine } = sweepWholeSpace();

    // Assert
    expect(surplusOverEngine.length).toBeGreaterThan(0);
  });
});

// ---------------------------------------------------------------------------
// Locale independence.

// Spelled by escape and never typed. U+0307 is a combining dot above: in a
// diff, a terminal and most editors it renders on top of the letter before it,
// so a spec that typed it would be asserting a value no reviewer can read and
// no ordinary tooling reliably round-trips — the rule `narrative-cipher.spec.ts`
// keeps about U+001F, for the same reason.
const DOTTED_CAPITAL_I_FOLDED = `i${String.fromCharCode(0x0307)}`;
const DOTLESS_I = String.fromCharCode(0x0131);

// Captured before anything is replaced, and used by the replacements, so the
// simulation is the real Turkish mapping rather than a hand-written one.
const REAL_TO_LOCALE_LOWER_CASE = String.prototype.toLocaleLowerCase;
const REAL_TO_LOCALE_UPPER_CASE = String.prototype.toLocaleUpperCase;

// A Turkish *host*, not a Turkish argument. The methods that consult the host's
// default locale are exactly `toLocaleLowerCase` and `toLocaleUpperCase` called
// with no argument, and the runner's default locale is `en-US` and cannot be
// changed after start-up — so the two methods are replaced with versions that
// ignore what they were passed and answer as `tr`. An implementation reaching
// for either sees Turkish; a table lookup sees nothing at all.
function installTurkishDefaultLocale(): void {
  Object.defineProperty(String.prototype, 'toLocaleLowerCase', {
    configurable: true,
    writable: true,
    value: function turkishLower(this: string): string {
      return REAL_TO_LOCALE_LOWER_CASE.call(this, 'tr');
    },
  });
  Object.defineProperty(String.prototype, 'toLocaleUpperCase', {
    configurable: true,
    writable: true,
    value: function turkishUpper(this: string): string {
      return REAL_TO_LOCALE_UPPER_CASE.call(this, 'tr');
    },
  });
}

function restoreDefaultLocale(): void {
  Object.defineProperty(String.prototype, 'toLocaleLowerCase', {
    configurable: true,
    writable: true,
    value: REAL_TO_LOCALE_LOWER_CASE,
  });
  Object.defineProperty(String.prototype, 'toLocaleUpperCase', {
    configurable: true,
    writable: true,
    value: REAL_TO_LOCALE_UPPER_CASE,
  });
}

describe('foldCase under a Turkish locale', () => {
  it('answers identically for I and i, which is the only pair that can tell', () => {
    // Arrange
    // **`I` and `i` are in this case explicitly, because without them it proves
    // nothing.** Turkish is the one locale where the ASCII pair diverges — `I`
    // lowercases to U+0131 and `i` uppercases to U+0130 — and status T of
    // `CaseFolding.txt`, the Turkic conditional pair, is precisely what the
    // table was generated without. So the fold's answer for `I` is `i` on every
    // host, and an implementation that reached for the platform's locale-aware
    // mapping would answer U+0131 here and nowhere else.
    const inputs = ['I', 'i', 'İ', 'ı', 'ISTANBUL', 'istanbul'];

    // The negative control, asserted *before* anything is folded. Without the
    // replacement having taken, this case compares two identical runs of the
    // same environment and passes whatever `foldCase` did — so the control is
    // checked first, and a failure here says "the simulation is broken" instead
    // of being mistaken for a verdict on the module.
    installTurkishDefaultLocale();

    let under: readonly string[];

    try {
      expect('I'.toLocaleLowerCase()).toBe(DOTLESS_I);
      under = inputs.map((input) => foldCase(input));
    } finally {
      restoreDefaultLocale();
    }

    // Act
    const before = inputs.map((input) => foldCase(input));

    // Assert
    expect('I'.toLocaleLowerCase()).toBe('i');
    expect(under).toEqual(before);
    // U+0130 folds to two code points and the second of them is a combining
    // dot, invisible in every editor — so it is spelled by escape rather than
    // typed, the rule `narrative-cipher.spec.ts` keeps about U+001F.
    expect(before).toEqual([
      'i',
      'i',
      DOTTED_CAPITAL_I_FOLDED,
      DOTLESS_I,
      'istanbul',
      'istanbul',
    ]);
  });
});
