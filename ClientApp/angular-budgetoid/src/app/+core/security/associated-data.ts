// One join, and every associated-data grammar this client seals under goes
// through it.
//
// The separator and the join were private to `account-keys.ts`, where the
// wrapped-key grammar was the only caller. A second grammar — the narrative
// fields of a later story — is what makes one shared join worth having: two
// copies of "the fields are joined by 0x1F in UTF-8" drift, and drift here is
// silent. Associated data is not carried inside an envelope; it is re-supplied
// from wherever the envelope was found, so a byte that moves makes every
// envelope already written unopenable with the same failure a corrupted key
// gives, and nothing anywhere names the cause.
//
// **This module states nothing about any grammar.** No prefix, no field name
// and no ordering lives here — what a grammar is made of belongs to the module
// that seals under it, and each of those pins its own vector over the whole
// string. So a failure here names the join and a failure there names the
// format, and neither has to be read to understand the other.
//
// **It deliberately does not check that a field omits the separator.** That is
// a claim about the *fields*, and only a grammar is in a position to hold it:
// the wrapped-key one because its fields are a literal, a canonical UUID and
// one of two words; the narrative one because its fields are a literal, two
// closed unions and a UUID. A check here could only refuse a value this module
// cannot describe, or repair it — and the repair is the worse of the two, since
// it would silently change the bytes a caller believed it had chosen.
//
// For the same reason it folds nothing: no trim, no case change, no Unicode
// normalisation, and no field dropped for being empty. Every such favour is a
// second spelling of a value already sealed under the first, and a grammar that
// *refuses* a non-canonical field needs it to arrive as it was written or the
// refusal is decorative.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it.

/**
 * The byte between two fields — ASCII's unit separator, U+001F.
 *
 * Spelled by its code point rather than typed. A literal control character is
 * invisible in every tool a reviewer would read this file in, which is the one
 * property a byte of a frozen format cannot afford.
 *
 * Exported so that a caller reasoning about whether one of its own fields can
 * contain the byte — the argument this module leaves to each grammar — has
 * something to name.
 */
export const UNIT_SEPARATOR = String.fromCharCode(0x1f);

// Module-level because a `TextEncoder` holds no buffer between calls: `encode`
// allocates its answer, which is what keeps the freshness rule below true
// without this module owning any scratch space.
const utf8 = new TextEncoder();

/**
 * Joins `fields` with {@link UNIT_SEPARATOR} and returns the UTF-8 of it.
 *
 * The separator goes *between* the fields, never around them: one field is
 * that field alone, and no fields is no bytes. A trailing separator is
 * invisible in every rendering of the value and changes the bytes of
 * everything sealed under it.
 *
 * Every field is kept, empty ones included. Dropping an empty field seals two
 * different field lists to the same bytes, which is the one ambiguity a
 * separator exists to remove.
 *
 * Fresh bytes on every call, over no buffer this module keeps — and a fresh
 * array per call even for fields it has already been handed, so a caller that
 * wipes or overwrites what it was given cannot reach into the next caller's
 * answer. A caller that built two associated-data values and then sealed two
 * envelopes would otherwise get both bound to whichever was built last — two
 * envelopes bound to the same thing, neither of them wrong-looking.
 */
export function buildAssociatedData(...fields: readonly string[]): Uint8Array {
  return utf8.encode(fields.join(UNIT_SEPARATOR));
}
