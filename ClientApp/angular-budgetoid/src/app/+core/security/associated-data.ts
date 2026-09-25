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
// a claim about the *fields*, only a grammar is in a position to hold it, and
// the two grammars here answer it differently. The narrative one holds it: its
// fields are a literal, two closed unions and a UUID, and none of those can
// carry the byte. The factor-keypair one cannot make the claim at all — its
// fields are a literal, a raw version byte, a canonical uuid or a decimal
// epoch, a purpose word on one of its messages, and, in the HKDF info, two raw
// 65-byte points, which contain the separator often enough. What keeps those
// messages unambiguous is that each point is a fixed width and every other
// encoding of one is refused before a message is built; that its fields are
// bytes at all is why {@link joinFields} exists beside
// {@link buildAssociatedData}. A check here
// could only refuse a value this module cannot describe, or repair it — and the
// repair is the worse of the two, since it would silently change the bytes a
// caller believed it had chosen.
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

// The same decision as {@link UNIT_SEPARATOR}, read back as the byte the join
// writes rather than spelled `0x1f` a second time. The constant above is a
// string because a grammar naming it is reasoning about its own text fields;
// `joinFields` writes bytes, and one of the two readings has to be derived from
// the other or they are two literals that can drift.
const UNIT_SEPARATOR_BYTE = UNIT_SEPARATOR.charCodeAt(0);

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
 *
 * **Expressed on {@link joinFields} rather than beside it.** The rule is stated
 * once, in bytes, and this function is that rule under the encoder — which is
 * the one encoding decision it makes and the reason it still takes strings.
 * The two spellings are byte-identical because the separator is a BMP
 * non-surrogate code point, so no pair can form across it and the UTF-8 of a
 * join is the join of the UTF-8s; `associated-data-bytes.spec.ts` derives that
 * rather than assuming it, surrogates on both sides of the separator included.
 */
export function buildAssociatedData(...fields: readonly string[]): Uint8Array {
  return joinFields(...fields.map((field) => utf8.encode(field)));
}

/**
 * The same join over fields that are already bytes.
 *
 * **One rule, two spellings, and the reason for the second is that some fields
 * are not text.** The factor-keypair grammar's fields are a raw version byte and
 * two 65-byte elliptic-curve points; a point pushed through UTF-8 comes out 129
 * bytes long — measured — and a version byte composed as text is correct at 1
 * and silently wrong from 0x80 up. A grammar in that shape cannot use
 * {@link buildAssociatedData} at all, and the answer to that is not a second
 * join written next door.
 *
 * Everything {@link buildAssociatedData} promises is promised here and for the
 * same reasons, because this is where it is implemented: the separator goes
 * between the fields and never around them, no fields is no bytes, every field
 * is kept including empty ones, nothing is folded, and the answer is a fresh
 * array over a fresh buffer on every call.
 *
 * It also leaves the arrays it was handed alone. A join that wrote its separator
 * into a caller's field to save an allocation would corrupt the next message
 * built from the same value — and one grammar hands this function the same
 * public key twice in a single message.
 */
export function joinFields(
  ...fields: readonly Uint8Array[]
): Uint8Array<ArrayBuffer> {
  // `Math.max` because the count is `length - 1`, which is `-1` for no fields
  // at all — a width computed from that is either a throw or a silently short
  // array, and no grammar calls it this way today, which is exactly why the
  // corner needs an answer.
  const separators = Math.max(fields.length - 1, 0);
  const width = fields.reduce(
    (total, field) => total + field.length,
    separators,
  );
  const message = new Uint8Array(width);

  let at = 0;

  for (const [index, field] of fields.entries()) {
    // Between, never around: a separator before every field but the *first*, so
    // one field is that field alone and a trailing separator — invisible in
    // every rendering of the value — cannot happen.
    //
    // **The test is the field's position and never how many bytes have been
    // written.** `at > 0` reads identically and is wrong in one place: a leading
    // empty field leaves nothing written, so the second field is taken for the
    // first and its separator is dropped. That is a different value of the same
    // width, with a zero byte at the end where the separator should have been,
    // and it disagrees with `Array.join` — which counts fields, as this must.
    // Measured, not reasoned: `['', 'a', '']` joined that way is `61 1f 00`
    // where the string spelling gives `1f 61 1f`.
    if (index > 0) {
      message[at] = UNIT_SEPARATOR_BYTE;
      at += 1;
    }

    message.set(field, at);
    at += field.length;
  }

  return message;
}
