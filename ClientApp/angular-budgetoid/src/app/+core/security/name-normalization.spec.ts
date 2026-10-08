// FR-076's four steps, in order: trim, NFKC, full case fold, UTF-8.
//
// **Every assertion here is about hex and never about a JavaScript string**,
// which is the one decision that makes this file worth having. A self-consistent
// wrong encoder round-trips perfectly: normalize a name, index it, normalize the
// same name again, get the same answer, and every case built out of this
// module's own output passes while the bytes disagree with every other
// implementation of FR-076. Only a cross-implementation answer fixes that, so
// the values below are read out of
// `docs/business-logic/vectors/blind-index-v1.json` — computed outside this
// codebase — and are compared as bytes rendered to hex. A string comparison
// would also hide the encoding step entirely, since the step's whole content is
// which bytes a string becomes.
//
// **What the frozen vectors can and cannot separate, measured rather than
// assumed.** Running four plausible near-misses against every input in the file:
//
//   * `toLowerCase` in place of the fold fails six inputs — the sharp-s, sigma
//     and Cherokee rows. Caught by the vectors alone.
//   * skipping the trim fails one input. Caught by the vectors alone.
//   * **NFC in place of NFKC passes every frozen input**, and so does dropping
//     NFKC altogether. The ligature row does *not* separate them: U+FB01 is
//     itself a status-F entry in the shipped table and folds to `fi` with no
//     normalization in front of it at all. The order case below therefore uses a
//     **Roman numeral**, which is compatibility-decomposable and carries no
//     folding entry of its own — measured, U+2160 gives `i` under NFKC-then-fold
//     and U+2170 under either fold-first shape.
//   * **a trailing `.normalize('NFKC')` passes every frozen input too**, and is
//     caught only by the U+1E96 case at the end of this file.
//
// So three of the cases below exist because the frozen file, on its own, is
// blind to the mistake they name.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

import { normalizeNameForIndex } from './name-normalization';

// ---------------------------------------------------------------------------
// The frozen vectors.

// From a spec, `process.cwd()` is the Angular project directory, so the vectors
// live two levels up. Read from `docs/` rather than copied in here so that this
// client and any second implementation are checked against one artifact: a value
// transcribed into a spec is a second copy of the contract, and the copy that
// drifts still passes its own file.
const VECTOR_FILE_PATH = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'blind-index-v1.json',
);

interface FrozenVector {
  readonly why: string;
  readonly table: string;
  readonly column: string;
  readonly inputs: readonly string[];
  readonly normalizedUtf8Hex: string;
  readonly blindIndex: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requireString(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = source[key];

  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

function requireInputs(
  source: Record<string, unknown>,
  what: string,
): readonly string[] {
  const value = source['inputs'];

  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`${what} lists no inputs.`);
  }

  return value.map((input: unknown, index: number) => {
    if (typeof input !== 'string') {
      throw new Error(`${what} carries a non-string input at ${index}.`);
    }

    return input;
  });
}

function parseVector(value: unknown): FrozenVector {
  if (!isRecord(value)) {
    throw new Error('A vector is not an object.');
  }

  const why = requireString(value, 'why', 'A vector');

  return {
    why,
    table: requireString(value, 'table', why),
    column: requireString(value, 'column', why),
    inputs: requireInputs(value, why),
    normalizedUtf8Hex: requireString(value, 'normalizedUtf8Hex', why),
    blindIndex: requireString(value, 'blindIndex', why),
  };
}

// Separate from the read so the control case below can feed it text and see it
// refuse. A reader that accepted anything would report an empty vector list
// perfectly, and every case keyed on that list would pass by running nothing.
function parseVectorFile(text: string): readonly FrozenVector[] {
  const parsed: unknown = JSON.parse(text);

  if (!isRecord(parsed)) {
    throw new Error('The vector file is not an object.');
  }

  const vectors = parsed['vectors'];

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The vector file lists no vectors.');
  }

  return vectors.map((vector: unknown) => parseVector(vector));
}

const VECTORS = parseVectorFile(readFileSync(VECTOR_FILE_PATH, 'utf8'));

// Every `(vector, input)` pair flattened, so each frozen spelling is its own
// case rather than one case that stops at the first failure. Driven from the
// file: a vector added there is a case here with nothing edited.
const FROZEN_INPUTS = VECTORS.flatMap((vector) =>
  vector.inputs.map((input) => ({
    why: vector.why,
    input,
    normalizedUtf8Hex: vector.normalizedUtf8Hex,
  })),
);

const utf8 = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

// ---------------------------------------------------------------------------
// The frozen answers.

describe('normalizeNameForIndex against the frozen vectors', () => {
  it('reads a vector file that carries vectors', () => {
    // Arrange
    // The negative control on the reader. Without it every case below could
    // pass by running over an empty list, which is exactly what a wrong path or
    // a renamed member would produce.

    // Act
    const refusal = (): readonly FrozenVector[] =>
      parseVectorFile('{"vectors":[]}');

    // Assert
    expect(VECTORS.length).toBeGreaterThan(0);
    expect(FROZEN_INPUTS.length).toBeGreaterThan(VECTORS.length);
    expect(refusal).toThrow('lists no vectors');
  });

  it.each(FROZEN_INPUTS)(
    'normalizes $input to $normalizedUtf8Hex — $why',
    ({ input, normalizedUtf8Hex }) => {
      // Arrange
      // The expected value is hex from the file and never a string built here.

      // Act
      const normalized = normalizeNameForIndex(input);

      // Assert
      expect(toHex(normalized)).toBe(normalizedUtf8Hex);
    },
  );

  it('collapses every spelling in a group to one answer', () => {
    // Arrange
    // The per-input cases above would all pass if the function answered a
    // constant. This one says the *grouping* is what the file claims: each
    // vector's inputs agree with each other, and the groups disagree across the
    // file — nine vectors, six distinct byte strings, because three of them are
    // one name under three tables.

    // Act
    const perVector = VECTORS.map((vector) =>
      vector.inputs.map((input) => toHex(normalizeNameForIndex(input))),
    );

    // Assert
    for (const [index, answers] of perVector.entries()) {
      expect(new Set(answers).size).toBe(1);
      expect(answers[0]).toBe(VECTORS[index].normalizedUtf8Hex);
    }

    expect(new Set(perVector.map((answers) => answers[0])).size).toBe(
      new Set(VECTORS.map((vector) => vector.normalizedUtf8Hex)).size,
    );
  });

  it('hands back bytes rather than text', () => {
    // Arrange
    // The fourth step is the encoding, and a caller handed a `string` is a
    // caller that owes one — free to pick a different encoding, and standing
    // exactly where somebody adds one more `.normalize()`.

    // Act
    const normalized = normalizeNameForIndex("Trader Joe's");

    // Assert
    expect(normalized).toBeInstanceOf(Uint8Array);
  });
});

// ---------------------------------------------------------------------------
// The order of the four steps.

describe('the order of the steps', () => {
  it('runs NFKC before the fold, which a Roman numeral is what proves', () => {
    // Arrange
    // **The ligature does not prove this, and assuming it does is the trap.**
    // U+FB01 carries a status-F entry in the shipped table and folds to `fi` on
    // its own, so `ﬁlm`, `FILM` and `film` collapse under fold-first, NFC-first
    // and no-normalization-at-all alike — measured, all four shapes give
    // `66696c6d`.
    //
    // A Roman numeral separates them because it is compatibility-decomposable
    // and *also* carries a folding entry, and the two lead to different places:
    // NFKC turns U+2160 into `I`, which folds to `i`; the fold turns U+2160
    // into U+2170, which is where an implementation that folded first stops if
    // it never normalizes, and which NFC would never touch.
    const romanOne = 'Ⅰ';
    const romanTwelve = 'Ⅻ';

    // Act
    const one = toHex(normalizeNameForIndex(romanOne));
    const twelve = toHex(normalizeNameForIndex(romanTwelve));

    // Assert
    expect(one).toBe('69');
    expect(twelve).toBe('786969');

    // And explicitly not the fold-first answers, so the case names the mistake
    // it is here to catch: U+2170 is `e285b0`, U+217B is `e285bb`.
    expect(one).not.toBe('e285b0');
    expect(twelve).not.toBe('e285bb');
  });

  it('trims at the ends only and keeps interior whitespace', () => {
    // Arrange
    // `Trader Joe's` and `TraderJoe's` are two payees. A trim that also
    // collapsed interior runs would merge them, and the merge is silent: both
    // names key to one row and the second one simply never appears again.
    const padded = "\t\n  Trader Joe's  \n";
    const interior = "Trader  Joe's";
    const closed = "TraderJoe's";

    // Act
    const trimmed = toHex(normalizeNameForIndex(padded));
    const doubled = toHex(normalizeNameForIndex(interior));
    const joined = toHex(normalizeNameForIndex(closed));

    // Assert
    expect(trimmed).toBe('747261646572206a6f652773');
    expect(doubled).not.toBe(trimmed);
    expect(joined).not.toBe(trimmed);
  });

  it('yields no bytes for a name that trims away to nothing', () => {
    // Arrange
    // No length rule and no refusal: an empty result is a well-defined index for
    // a caller holding a column that is empty rather than null, and whether an
    // empty name should be indexed at all is a product rule about the field.

    // Act
    const empty = normalizeNameForIndex('   \t\n  ');

    // Assert
    expect(empty).toHaveLength(0);
  });
});

// ---------------------------------------------------------------------------
// No trailing re-normalisation.

describe('the absence of a second normalization', () => {
  it('leaves the fold output exactly as the fold produced it', () => {
    // Arrange
    // **Measured over the whole plane against the shipped table: 26 code points
    // fold to output NFKC would still rewrite.** U+1E96, LATIN SMALL LETTER H
    // WITH LINE BELOW, is one of them and the simplest — it is NFKC-stable
    // itself, so the third step receives it unchanged, and the fold gives `h`
    // followed by U+0331, which NFKC would recompose straight back to U+1E96.
    //
    // The two answers are three bytes each and differ completely: `68ccb1` is
    // what the fold produced, `e1ba96` is what a second `.normalize('NFKC')`
    // would turn it into. Every frozen vector in the file passes under either,
    // which is why this case exists.
    //
    // A trailing normalization is wrong twice over. It changes the bytes, so
    // every index already written stops matching the names that produced it —
    // permanently, with no error naming the cause, and with no way back, because
    // a blind index cannot be recomputed from plaintext this product does not
    // hold. And it buys nothing: NFKC runs before the fold on every input, so
    // two spellings of one name are already one string by the time the fold sees
    // them.
    const hWithLineBelow = 'ẖ';

    // Act
    const normalized = toHex(normalizeNameForIndex(hWithLineBelow));

    // Assert
    expect(normalized).toBe('68ccb1');
    expect(normalized).not.toBe('e1ba96');

    // The case says out loud that the output is not a normal form, so nobody
    // reads the assertion above as an accident. This is the assertion a reader
    // reaching for a second `.normalize` is about to break.
    expect(toHex(utf8.encode('ẖ'.normalize('NFKC')))).toBe('e1ba96');
  });
});

// ---------------------------------------------------------------------------
// The dotted capital I.

describe('the Turkish dotted capital I', () => {
  it('does not reach istanbul, and that is the contract', () => {
    // Arrange
    // **This is the contract and not a defect, and the comment is here so
    // nobody "fixes" it.** U+0130 folds to `i` plus U+0307 under statuses C and
    // F — status T, the Turkic conditional pair that would drop the dot, is
    // exactly what the table was generated without, and adding it would make the
    // fold locale-dependent, which is the thing the shipped table exists to
    // prevent.
    //
    // The consequence is that `İstanbul` and `istanbul` are two names and index
    // to two rows. The server's unique index over the blind index agrees with
    // that, because the server never sees either name — it sees only these
    // bytes. Making the two agree here, on one client, would disagree with the
    // server's index and with every other implementation of FR-076, and the
    // disagreement is silent in both directions.
    const dotted = 'İstanbul';
    const plain = 'istanbul';

    // Act
    const dottedHex = toHex(normalizeNameForIndex(dotted));
    const plainHex = toHex(normalizeNameForIndex(plain));

    // Assert
    expect(dottedHex).toBe('69cc877374616e62756c');
    expect(plainHex).toBe('697374616e62756c');
    expect(dottedHex).not.toBe(plainHex);
  });
});
