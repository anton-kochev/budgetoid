// How much text a narrative form lets somebody type, and the number that
// decides it.
//
// **These are UX ceilings, and the rule they protect is the server's.** Every
// one of the eight narrative columns is bounded in *envelope bytes* —
// `NarrativeFieldLimits.NameBytes` and `DescriptionBytes` in the Domain — and
// a write past one is a 400 this client cannot soften. Nothing here counts
// bytes: a validator that did would refuse text a person can see is short, in
// units nobody types in, and it would be a second copy of a rule the server
// already enforces on the only length either side can agree about. What these
// two numbers are for is that a form refuses a name **before** a round trip
// rather than after one, and the whole of their correctness is that no string
// they admit can reach the server too long.
//
// **The arithmetic that makes them safe, because it is not the one a reader
// guesses.** `Validators.maxLength` and the HTML `maxlength` attribute both
// count **UTF-16 code units**, so the worst case is not the widest code point
// but the widest code point *per unit* — and that is a three-byte BMP
// character (CJK, most of Devanagari, and every lone surrogate, which encodes
// as U+FFFD), at three bytes for one unit. A four-byte astral character costs
// two units and is therefore *cheaper* per unit, at two. Measured
// exhaustively over every code point in `narrative-field-caps.spec.ts`, which
// is where {@link MAX_UTF8_BYTES_PER_UTF16_UNIT} stops being an assertion.
//
// **What that leaves, measured today**: a 200-unit name seals to at most
// 200 × 3 + 29 = 629 bytes against a cap of 1024, and a 500-unit note to at
// most 1529 against 2560 — roughly 39% and 40% of headroom. The ceilings the
// byte caps could actually carry are 331 and 843 units; {@link
// charactersAlwaysFitting} computes them, and the spec holds each chosen cap
// under its own.
//
// **The cliff is what this file exists to make visible.** It sits near 331 and
// not near 200, and it bites **by language and by nobody else**: raise the name
// cap to 400 and a 400-character Latin name still passes while a 350-character
// Chinese one takes a 400 from the server, on a screen that told the person
// their name was short enough. So the number may not be raised on its own —
// what admits a longer name is a larger byte cap, which is a decision in the
// Domain, and this file follows it rather than leading it.
//
// **The byte caps below are transcriptions, and what holds them is a spec that
// reads the other language's source text.** The server's constants are C# and
// this is TypeScript; no build reads both and none will. What closes the gap
// is a pair of cases in `narrative-field-caps.spec.ts` that open
// `Domain/Security/NarrativeFieldLimits.cs`, parse the declaration a compiler
// would see — `public const int`, anchored to the start of a line, so that the
// constant named in prose or a declaration commented out is not read as a
// number — and compare it to each constant here. A drifted digit reddens, and
// so does a rename or a reformat, by name and carrying the path it could not
// read the constant out of. What that still does not say is whether the C#
// constant is the number the columns' check constraints are built from: this
// side of the repository cannot see a migration, and the backend suite pins
// that half over the same two constants.
//
// **One rule about *how* these are consumed, which is not a style question.** A
// class field whose initialiser is nothing but one of these names reads
// `undefined` under the test runner and nowhere else — the position is what
// does it, not the timing. Vitest puts the built bundle through Vite's
// module-runner transform, which turns every reference to an imported name into
// a live read off the import namespace except in that one place, where it
// hoists a module-scope copy taken as the chunk is evaluated: before esbuild's
// lazy `__esm()` wrapper has run this file's body and assigned anything. A call
// argument, an object member, a method body and a getter all keep the live
// import. `TransactionsComponent.recordingSentence` states it in full, and it
// is why the three screens reading these caps expose them through getters. No
// guard is written here for it, deliberately: the failure it produces is a
// missing client-side ceiling on a value the server refuses anyway, and a
// throw from a component field initialiser would meet a person as a dead
// screen.
import { MINIMUM_ENVELOPE_BYTES } from '@app-core/security/key-envelope';

/**
 * The cap on a sealed name, in envelope bytes.
 *
 * `payees.name`, `accounts.name`, `categories.name`, `category_groups.name` and
 * `budgets.name`. Transcribed from
 * `Domain.Security.NarrativeFieldLimits.NameBytes`, and the transcription is
 * checked: `narrative-field-caps.spec.ts` parses that declaration out of the C#
 * and compares it to this number.
 */
export const NARRATIVE_NAME_ENVELOPE_BYTES = 1024;

/**
 * The cap on a sealed description, in envelope bytes.
 *
 * `transactions.description`, `categories.description` and
 * `category_groups.description`. Transcribed from
 * `Domain.Security.NarrativeFieldLimits.DescriptionBytes`, and checked against
 * that declaration the way its neighbour above is.
 */
export const NARRATIVE_DESCRIPTION_ENVELOPE_BYTES = 2560;

/**
 * The most UTF-8 bytes one UTF-16 code unit can turn into.
 *
 * Three, and the head of this file argues why it is not four. The spec measures
 * it over every code point there is rather than taking this constant's word for
 * it.
 */
export const MAX_UTF8_BYTES_PER_UTF16_UNIT = 3;

/**
 * The longest text, in UTF-16 code units, that always seals to `envelopeBytes`
 * or fewer — whatever anybody types.
 *
 * The framing comes from {@link MINIMUM_ENVELOPE_BYTES} rather than from a `29`
 * written here: AES-GCM ciphertext is exactly as long as its plaintext, so the
 * version byte, the nonce and the tag are the whole of what an envelope adds,
 * and a copy of that sum would be a second definition of the layout.
 */
export function charactersAlwaysFitting(envelopeBytes: number): number {
  return Math.floor(
    (envelopeBytes - MINIMUM_ENVELOPE_BYTES) / MAX_UTF8_BYTES_PER_UTF16_UNIT,
  );
}

/**
 * What a form accepts in a narrative **name**, in UTF-16 code units.
 *
 * Held under `charactersAlwaysFitting(NARRATIVE_NAME_ENVELOPE_BYTES)` by the
 * spec. It is a product ceiling and not that number: a name is a label somebody
 * scans a list of.
 */
export const NARRATIVE_NAME_CHARACTERS = 200;

/**
 * What a form accepts in a narrative **description**, in UTF-16 code units.
 *
 * Held under `charactersAlwaysFitting(NARRATIVE_DESCRIPTION_ENVELOPE_BYTES)` by
 * the spec. A note on a transaction or a sentence about a category, not a
 * document.
 */
export const NARRATIVE_DESCRIPTION_CHARACTERS = 500;
