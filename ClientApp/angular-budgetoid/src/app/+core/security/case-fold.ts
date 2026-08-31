// Full Unicode case folding, over a table this product ships rather than one the
// host happens to have.
//
// **The platform offers nothing that can stand here, and the two candidates fail
// for two different reasons.** `String.prototype.toLowerCase` is a lowercase
// *mapping* and not a fold at all — a different transform that agrees with this
// one on ASCII and diverges wherever the difference decides a match. Measured on
// this runner (Node 22.14.0, `process.versions.unicode` reporting ICU's Unicode
// 16.0): `'ß'.toLowerCase()` is `'ß'`, where the fold gives `'ss'`;
// `'ﬁlm'.toLowerCase()` keeps the ligature; `'\u{AB70}'.toLowerCase()` is
// itself, where the fold gives U+13A0, because Cherokee folds *upward* and a
// lowercasing transform can only ever go the other way. Of the table's 1585
// entries, `toLowerCase` produces a different answer for 239 and leaves 211
// completely untouched. `Intl.Collator` at a case-insensitive sensitivity is the
// other candidate and is not one either: it answers an *ordering*, and a blind
// index needs bytes to take a MAC over.
//
// **And every API that were a fold would read the host's Unicode data, which
// CON-009 forbids.** The version a browser folds under is a property of the
// browser, not of this product, so two clients keying one name would key it to
// two values — a duplicate that never merges, on a column whose whole purpose is
// that equal names collide. `case-fold-table.ts` is that data frozen at a
// version **the product** picks, and {@link CASE_FOLDING_UNICODE_VERSION} is the
// value it picked.
//
// **A map is the whole of the transform, and that is a property of the data
// rather than a simplification.** Full case folding — `CaseFolding.txt` statuses
// C and F, which is what the table was generated from — is per-code-point,
// unconditional and locale-independent: no context, no lookahead, no locale, no
// state carried from one code point to the next. Status T, the Turkic
// conditional pair, is exactly what was left out, and it is the only part that
// would have needed any of those. Contrast `toLowerCase`, which *is* contextual:
// `'ΟΔΟΣ'.toLowerCase()` ends in a final sigma, U+03C2, while a mid-word one
// lowercases to U+03C3 — so the same letter reaches two values depending on what
// follows it. The fold maps both sigmas to U+03C3 unconditionally, which is why
// `ΟΔΟΣ`, `οδος` and `οδοσ` are one name in the frozen vectors.
//
// **This module is not the normalization.** It is FR-076's third step and
// nothing else; the order — trim, NFKC, fold, UTF-8 — belongs to
// `name-normalization.ts`, which is the one caller. Folding on its own is not a
// name key and must not be used as one: applied without the NFKC that precedes
// it, `ﬁlm` and `film` stay two different names.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it. The decoded
// table below is not state either: it is built once, at module load, out of two
// frozen literals, and nothing ever writes to it — a cache would be the version
// of this that could go stale, and there is nothing here for it to go stale
// against.
import { CASE_FOLD_MULTI, CASE_FOLD_RUNS } from './case-fold-table';

/**
 * The Unicode version `case-fold-table.ts` was generated from — a value the
 * product chooses, not one it reads off the host.
 *
 * **Exported because the choice is the contract, and a contract nobody can name
 * cannot be pinned.** The frozen vectors in
 * `docs/business-logic/vectors/blind-index-v1.json` carry `unicodeVersion` and
 * `caseFoldTableSha256` beside every known answer, so a spec can hold this
 * constant, the two literals next door and the answers they produce against one
 * another. Measured: that digest is the SHA-256 of `CASE_FOLD_RUNS`, a single
 * `\n`, then `CASE_FOLD_MULTI`, in UTF-8 — the two literals with their
 * line-continuation joins removed, which is what makes it a fact about the data
 * rather than about how the file happens to be wrapped.
 *
 * **The decoder does not read it, and that is not an oversight.** A version
 * check at fold time could only compare this string against itself. What the
 * constant is for is the moment the table is regenerated: the version moves,
 * every pinned answer that changed moves with it, and a reviewer has one line
 * to look at rather than 691 runs.
 *
 * Raising it is a **format change**, not a dependency bump. Every blind index
 * already written was computed under the fold this version names, and a blind
 * index cannot be recomputed without the plaintext it was taken over — which
 * this product does not hold, because the plaintext is encrypted. So a name that
 * folds differently under a newer table becomes a row that no longer answers its
 * own search, silently, forever.
 */
export const CASE_FOLDING_UNICODE_VERSION = '17.0';

// Base 36 throughout, which is what `case-fold-table.ts` says about its own
// shape. `parseInt` is the whole of the parse, including the sign: a run's delta
// is negative wherever a letter folds *upward* — Cherokee's `-tzk` is the largest
// of them — and `Number('-3d')` is `NaN`, so the one obvious substitution here
// silently turns a folding entry into an identity one.
function fromBase36(text: string): number {
  return parseInt(text, 36);
}

// **Keyed by the character and never by its numeric code point.** The lookup in
// `foldCase` is then over exactly the values `for…of` yields, which are code
// points, so there is no `codePointAt` to get wrong and no place a surrogate
// half could be used as a key. The alternative — a `Map<number, string>` — needs
// an assertion at every read, because `codePointAt` is typed as possibly
// undefined and the compiler cannot see that the iterator never hands out an
// empty string.
//
// Both halves are decoded, and a runs-only decoder is the mistake to name: it
// would still fold almost every name correctly, and it would miss all 104 of the
// multi-output entries — 'ß' among them, which is the single most common case
// the whole shipped table exists for. The two are disjoint (measured: no code
// point appears in both), so the order they are loaded in decides nothing; the
// multi half is loaded second anyway, so that if a regeneration ever did overlap
// them the entry with more than one output is the one that survives.
function decodeCaseFoldTable(): ReadonlyMap<string, string> {
  const table = new Map<string, string>();

  for (const run of CASE_FOLD_RUNS.split(',')) {
    const [startText, countText, deltaText] = run.split(':');
    const start = fromBase36(startText);
    const count = fromBase36(countText);
    const delta = fromBase36(deltaText);

    for (let offset = 0; offset < count; offset += 1) {
      const codePoint = start + offset;

      table.set(
        String.fromCodePoint(codePoint),
        String.fromCodePoint(codePoint + delta),
      );
    }
  }

  for (const entry of CASE_FOLD_MULTI.split(',')) {
    const [fromText, ...toTexts] = entry.split(':');

    table.set(
      String.fromCodePoint(fromBase36(fromText)),
      toTexts
        .map((toText) => String.fromCodePoint(fromBase36(toText)))
        .join(''),
    );
  }

  return table;
}

// Built once at module load: 1585 entries, 1481 of them from the runs and 104
// from the multi-output half.
const CASE_FOLD = decodeCaseFoldTable();

/**
 * Folds `text` code point by code point through `case-fold-table.ts`, and
 * returns the result.
 *
 * Every code point the table names is replaced by what it names — one code point
 * for the 1481 entries the runs expand to, one *or more* for the 104 in
 * `CASE_FOLD_MULTI`, which is why the answer can be longer than the input and
 * why this returns a string rather than mapping in place. A code point the table
 * does not name is returned as itself; the table holds no identity entries, so
 * "absent" and "folds to itself" are the same answer and there is nothing to
 * tell apart.
 *
 * **Iteration is by code point and never by UTF-16 unit.** 307 of the entries
 * are astral — Deseret at U+10400, Adlam at U+1E900 and the rest of that range —
 * and a `charCodeAt` loop over one of them sees two surrogates, finds neither in
 * the table, and hands back the input unfolded. The failure is invisible: the
 * string is well-formed, the length is right, and the only symptom is a name in
 * one of those scripts that stops matching its own other spelling.
 *
 * **Nothing else in the transform lives here.** No trim, no normalization, no
 * encoding: this is the third of FR-076's four steps, and taking any of the
 * others on the way past would put the order in two places. It is also why the
 * output is deliberately allowed to be non-NFKC — see `name-normalization.ts`,
 * which argues at length why re-normalising afterwards is the tempting mistake.
 */
export function foldCase(text: string): string {
  let folded = '';

  // `for…of` over a string iterates by **code point**, which is the whole of the
  // astral argument above: a `for (let i = 0; i < text.length; i += 1)` loop, or
  // any of `charAt`, `charCodeAt` and `text[i]`, walks UTF-16 units and hands
  // this lookup two surrogate halves that no key in the table matches.
  for (const character of text) {
    // `??` and not `||`: an entry folding to the empty string would be dropped
    // by the second and returned by the first. The table holds none today, and
    // the difference is one character to keep it that way by choice rather than
    // by luck.
    folded += CASE_FOLD.get(character) ?? character;
  }

  return folded;
}
