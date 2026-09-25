// FR-076, in one place and in order: trim, NFKC, full case fold, UTF-8.
//
// **Its own module rather than a private helper of the blind index, and the
// reason is not tidiness.** This is the step a second client — the mobile app,
// a future importer, anything that ever writes a row this product will search —
// has to reproduce *exactly*, and it is the one most likely to be got wrong,
// because every one of the four steps has a plausible near-miss: `toLowerCase`
// for the fold, NFC for NFKC, a trim that also collapses interior runs, an
// encoding that is not UTF-8. Buried inside the index function those four
// decisions are lines in the middle of a longer one; named here they are a thing
// a reviewer can search for, a spec can address on its own, and a second
// implementation can be diffed against.
//
// **Nothing else in this client may call it, and one caller in particular must
// not.** Narrative *sealing* does not normalise — `narrative-cipher.ts` argues
// that at length, and the short form is that what was typed is what is stored,
// NFD included. Folding the two together would mean a person's payee is written
// to the database in a spelling nobody typed: `Straße` stored as `strasse`,
// `ﬁlm` stored as `film`, and no way back, because the transform here is lossy
// in every step but the first. The index is a *key over* the text, never the
// text. Two transforms, two modules, and the distance between them is the point.
//
// **The order is the contract and not a preference.** NFKC before the fold, and
// the frozen vectors carry the case that proves it: `ﬁlm`, `FILM` and `film` are
// one name only if the ligature is decomposed *before* anything is folded —
// folding first leaves U+FB01 alone, and the two spellings key to two rows for
// the life of the account.
//
// **The fold is this product's; NFKC is still the platform's, and that asymmetry
// is deliberate rather than unfinished.** `case-fold-table.ts` exists because the
// version a host folds under is a property of the host. Normalization does not
// carry the same exposure: Unicode's normalization stability policy fixes the
// normal form of every code point once it is assigned, so two hosts can disagree
// only about characters one of them has never heard of — and an unassigned code
// point normalizes to itself. That is a much smaller surface than the fold's, and
// it is the whole justification for shipping one table and not two.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it.
import { foldCase } from './case-fold';

// Module-level for the reason `associated-data.ts` gives about its own: a
// `TextEncoder` keeps no buffer between calls, so one instance is not shared
// state and `encode` allocates its answer.
const utf8 = new TextEncoder();

/**
 * Normalizes `text` into the bytes a blind index is taken over: trim, NFKC,
 * {@link foldCase}, UTF-8 — in that order, all four, and nothing else.
 *
 * **Bytes and not a string, because the next thing that happens to the answer is
 * a MAC.** Handing back text would leave the fourth step owing, and a caller
 * that owes an encoding is a caller that can pick a different one; it would also
 * put a `string` in front of somebody who will eventually add one more
 * `.normalize()` to it, which is precisely the mistake the paragraph below is
 * about.
 *
 * **No trailing re-normalisation, and the temptation to add one is the reason
 * this paragraph is long.** The fold's output is not always NFKC — measured over
 * the whole plane against the shipped table, 26 code points `x` have
 * `foldCase(NFKC(x))` come back in a form NFKC would still rewrite (U+0390,
 * U+1E96, the Greek circumflex-and-iota cluster around U+1FB6, and the rest).
 * Seeing that, a reader reaches for a second `.normalize('NFKC')` at the end. It
 * would be wrong twice over. It changes the bytes, so every index already
 * written stops matching the names that produced it, permanently and with no
 * error naming the cause — a blind index cannot be recomputed, because the
 * plaintext it was taken over is encrypted. And it buys nothing: NFKC runs
 * *before* the fold on every input, so two spellings of one name are already the
 * same string by the time the fold sees them, and the fold is a deterministic
 * per-code-point map — equal in, equal out. The output is bytes fed to a MAC,
 * not text anybody reads, so "is it a normal form" is a question with no
 * consumer.
 *
 * **`String.prototype.trim`, at the ends only.** Interior whitespace is part of
 * the name: `Trader Joe's` and `TraderJoe's` are two payees, and collapsing runs
 * would merge them. The trim runs first, before NFKC, which is FR-076's order.
 *
 * **That order changes bytes on this host, today — this paragraph used to claim
 * otherwise and the claim was wrong.** It measured the right thing and drew the
 * wrong conclusion from it: no code point does survive `trim` and then become
 * *entirely* whitespace under NFKC — measured over the whole plane, zero of them
 * — but that was never the case that separates the two orders. NFKC can put
 * whitespace at the *edge* of a longer result without the whole result being
 * whitespace, and 50 code points do exactly that. U+00A8, the diaeresis, is the
 * plainest: NFKC gives it a leading space and a combining mark, so `  ¨  ` is
 * `20cc88` under trim-then-NFKC and `cc88` under NFKC-then-trim. Two spellings
 * of one name, on this runner, with nothing anywhere reporting which one a client
 * used.
 *
 * So the order is not a contract kept against hosts that might disagree later.
 * It is load-bearing here, and swapping the two lines below silently rekeys every
 * name whose first or last character is one of those 50.
 *
 * **No length rule and no refusal of an empty result.** A name that trims away
 * to nothing yields zero bytes and a perfectly well-defined index, which is the
 * right answer for a caller holding a column that is empty rather than null.
 * Whether an empty name should be indexed at all is a product rule about that
 * field, and it belongs at the screen that knows which field it is — the same
 * line `narrative-cipher.ts` draws about a cap on narrative text.
 *
 * One loss is inherited and worth naming, because it happens here rather than
 * downstream: `TextEncoder.encode` substitutes U+FFFD for an unpaired surrogate,
 * the measurement `narrative-cipher.ts` records at its own crossing. So two names
 * differing only in a lone surrogate index alike. There is nothing to do about
 * it at this layer — the substitution is the platform's and the alternative is a
 * refusal that no caller could act on — and it is stated so the next reader does
 * not go looking for a check that is missing.
 */
export function normalizeNameForIndex(text: string): Uint8Array {
  // The four steps, written as one expression so that the order is read in one
  // place and nothing can be inserted between two of them without being
  // obvious. Innermost first: `trim`, then `NFKC`, then the fold, then UTF-8.
  //
  // **`'NFKC'` and never `'NFC'`, and the argument is one input long.** NFC
  // leaves `Ⅻ` — U+216B, a compatibility character — as itself, so it folds to
  // U+217B and keys to a row that `XII` never reaches. Every frozen vector in
  // `blind-index-v1.json` passes under NFC, and under no normalization at all,
  // because the ligature that looks like it proves the point carries a folding
  // entry of its own.
  //
  // **Nothing follows the fold.** There is no second `.normalize` here, and the
  // paragraph above says at length why adding one changes bytes already written
  // and buys nothing.
  //
  // **`Uint8Array.from` is not decoration, and it is not a fifth step.** It
  // copies bytes onto a buffer built from *this* realm's `Uint8Array` and
  // changes not one of them. Measured under the test runner (jsdom through
  // `@angular/build:unit-test`, Node 22.14.0): `new TextEncoder().encode('abc')`
  // comes back with `instanceof Uint8Array` **false** and a prototype that is
  // not `Uint8Array.prototype`, because the encoder the environment supplies
  // lives in another realm. The value still indexes, still `set`s and still
  // signs, so nothing downstream would ever complain — an `instanceof` guard, a
  // `structuredClone`, or a library that branches on the array's type would
  // simply take the wrong branch, and this function's whole promise is that what
  // it hands back is bytes. The copy also narrows the buffer to `ArrayBuffer`,
  // which is what `BufferSource` accepts, for the reason `key-envelope.ts`
  // argues at its own crossing.
  return Uint8Array.from(utf8.encode(foldCase(text.trim().normalize('NFKC'))));
}
