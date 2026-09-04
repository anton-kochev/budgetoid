// The blind index — a keyed, deterministic fingerprint of a name, computed so
// that a server holding none of the plaintext can still find the rows that share
// one.
//
// `HMAC-SHA-256(indexKey, prefix || 0x1F || table || 0x1F || column || 0x1F ||
// normalized name)`, rendered as unpadded base64url. The key is the account's
// index key, one per account, wrapped beside the content key in every factor's
// row — `account-keys.ts` states why it is derived per account and never per
// credential, and `importHmacSha256Key` is the one door its bytes come through.
//
// **Deterministic, and that is the whole trade.** The same name under the same
// account and the same field always produces the same 43 characters, which is
// what makes a uniqueness constraint and an equality lookup work over data the
// operator cannot read. What it costs is that the operator learns *which rows
// share a name* — the shape of the account's payee distribution, and a
// dictionary attack on any name an attacker can guess, once they hold the index
// key. Neither is a defect to fix here; both are the reason the index is keyed
// per account rather than global, so nothing learned about one account transfers
// to another.
//
// **Two modules, not one, and the defining rules are exact opposites.** The
// narrative grammar next door binds a ciphertext to its *row*, precisely so that
// a value moved between rows fails to authenticate. This one must be **equal
// across rows** or it indexes nothing. The two grammars therefore read alike,
// share a separator and a join, and can never share a message — which is why
// they are neighbours rather than one file with a flag.
//
// **One caller, and screens behind it now.** `AccountKeyCustodyService` holds
// the account's index key, and the operation that delegates to it — `blindIndex`
// — is what calls `computeBlindIndex`, with `refuseUnindexedField` beside it.
// That operation is still this module's only caller, and it is no longer the end
// of the chain: `accounts.service.ts` and `categories.service.ts` key the name
// on every create and rename they send, and `transactions.service.ts` keys a
// payee's name both to send and to *match* — `matchPayeeByIndex` compares index
// values and never text, so a change to this message decides which counterparty
// a transaction is filed against. The order the module was built in stays worth
// recording: the value is a cross-client contract, so it was pinned against
// answers computed outside this codebase —
// `docs/business-logic/vectors/blind-index-v1.json` — before a single row was
// written under it, which is far cheaper than agreeing on a format after it has
// data. Do not relax any of it to make a later screen easier.
//
// **The key crosses as a parameter and is never held.** Same rule as
// `narrative-cipher.ts`: custody owns the index key and its lifetime, this module
// owns the message and the wire form and owns no key at all — no field, no
// module-level value, no cache. It is part of what lets custody promise that a
// page reload locks the account.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it.
import { buildAssociatedData } from './associated-data';
import { encodeBase64Url } from './base64url';
import { normalizeNameForIndex } from './name-normalization';
import type { NarrativeField } from './narrative-cipher';

/**
 * The literal that opens the message of every blind index.
 *
 * Part of the definition of every value already computed, and versioned for the
 * same reason {@link NARRATIVE_FIELD_AAD_PREFIX} is: a second grammar can exist
 * later without this one becoming ambiguous. It also keeps the two grammars
 * apart at the first field — a message that began with the narrative prefix
 * could otherwise collide with one of its associated-data values, and an index
 * equal to somebody's associated data is a fact neither side would ever notice.
 */
export const BLIND_INDEX_MESSAGE_PREFIX = 'budgetoid/blind-index/v1';

/**
 * Every table and column whose contents carry a blind index, and no others.
 *
 * FR-070's four, all of them a `name`. The pairs are written out rather than
 * filtered, and the `satisfies` is what ties them to their neighbour — see the
 * comment on the declaration for exactly what that does and does not hold.
 */
// **Derived from `NARRATIVE_FIELDS` by constraint, not by filter, and the
// difference is which mistake is audible.**
//
// The four indexed fields are a subset of the eight narrative ones — a column
// can only be blind-indexed if it is encrypted, or the index would be a keyed
// fingerprint of text sitting in the clear one column over. `satisfies readonly
// NarrativeField[]` is that subset relation stated to the compiler: `NarrativeField`
// is the union derived from `NARRATIVE_FIELDS`, so a pair here that is not one of
// the eight is a compile error, and a pair *removed* from the eight reddens this
// file rather than leaving it pointing at a column nothing encrypts. The pair is
// checked as a pair, not as two memberships — `{ table: 'payees', column:
// 'description' }` names a real table and a real column and is refused, for the
// reason `refuseInvalidBinding` gives next door.
//
// **The rejected alternative is a runtime `filter` over `NARRATIVE_FIELDS`,
// which looks like stronger derivation and is weaker.** It would take its values
// from the source, so dropping `payees.name` from the eight would leave this
// array three entries long — silently, with nothing red, and every index already
// written under that field simply never computed again. A constraint fails loudly
// where a filter fails quietly, and this is a list where a missing entry has no
// symptom at the time it goes missing. The filter also cannot keep the literal
// types the derived {@link BlindIndexedField} is built out of without an `as`,
// which would be an assertion standing exactly where the checking is supposed to
// happen.
//
// **What it does not hold, stated so nobody assumes otherwise.** It says these
// four are *legal*, never that they are *the* four: a fifth encrypted pair added
// here compiles, and a member bolted onto {@link BlindIndexedField} past this
// array reddens nothing, because the array still has four entries — the same
// limit `NARRATIVE_FIELDS` names about itself. Both are held by review, for the
// reason `CLAUDE.md` gives about a second account-creating path: one line that
// would redden no build. What the spec can hold, and this cannot, is the census —
// the four pairs against FR-070's list, in both directions.
export const BLIND_INDEXED_FIELDS = [
  { table: 'payees', column: 'name' },
  { table: 'accounts', column: 'name' },
  { table: 'categories', column: 'name' },
  { table: 'category_groups', column: 'name' },
] as const satisfies readonly NarrativeField[];

/** One blind-indexed table-and-column pair, derived from {@link BLIND_INDEXED_FIELDS}. */
export type BlindIndexedField = (typeof BLIND_INDEXED_FIELDS)[number];

/**
 * Builds the message a blind index is taken over:
 *
 * ```text
 * {@link BLIND_INDEX_MESSAGE_PREFIX} || 0x1F || <table> || 0x1F || <column> || 0x1F || <normalized name>
 * ```
 *
 * in UTF-8, joined through `associated-data.ts` — **one separator between
 * fields, none at either end, and never a second definition of the byte.** The
 * separator is `UNIT_SEPARATOR` and the join is `buildAssociatedData`, exactly as
 * the narrative grammar uses them; restating either here would be a second copy
 * of "the fields are joined by 0x1F in UTF-8", and the copy that drifted would go
 * on computing indexes that key perfectly, never collide, and match nothing a
 * second client wrote.
 *
 * The last field is `normalizeNameForIndex(plaintext)` and enters the join as
 * bytes rather than as text — the module owning FR-076's four steps hands back
 * bytes for that reason, and no step of it is repeated here.
 *
 * **It takes no row id, and the omission is the entire point of the function.**
 * The index has to be *equal* for equal names across rows: that is what a
 * uniqueness constraint over payee names, and a lookup that finds the payee
 * somebody just typed, are both asking of it. A row in the message makes every
 * value unique by construction — it would still compute, still be stable, still
 * look exactly like a working blind index, and would answer no query anybody
 * ever writes. This is the exact inverse of `narrativeFieldAssociatedData`, which
 * carries the row precisely so two rows can never share a value, and the two
 * rules being opposites is why the grammars are two modules rather than one with
 * a parameter.
 *
 * **The column separates nothing today, and it is in the message anyway.** All
 * four of {@link BLIND_INDEXED_FIELDS} carry the value in `name`, so the field is
 * a constant and dropping it would change no value this product can currently
 * compute. It stays for the case that is one migration away: a second indexed
 * column on one of those tables — a payee's alias beside its name — would
 * otherwise collide with the first, and two different fields answering one
 * lookup is a defect with no symptom until somebody notices a search returning
 * rows from a column they did not ask about. Adding the field later is not
 * available: it changes every value already written.
 *
 * The table, by contrast, separates today and the frozen vectors prove it —
 * `Trader Joe's` under `payees`, `categories` and `accounts` is three unrelated
 * values.
 *
 * The pair is looked up in {@link BLIND_INDEXED_FIELDS} at runtime, through
 * {@link refuseUnindexedField} and never inline, for the reason
 * `refuseInvalidBinding` states: the closed union is a fact about callers the
 * compiler assembled, and a table name arriving as data through one assertion
 * in a mapper has been through nothing. Unlike there, the refusal carries no
 * type of its own — there is no ciphertext in this operation, so a caller can
 * never confuse "you asked for something impossible" with "this stored value
 * did not open".
 */
// The buffer is spelled out rather than left as a bare `Uint8Array`, which is a
// view over either kind of buffer: `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and this is what {@link computeBlindIndex} hands straight
// to `subtle.sign`. The narrowing is true here rather than asserted — the array
// is allocated three lines down, on a buffer nothing outside this call has ever
// named — which is the shape `key-envelope.ts` argues for at its own crossing.
export function blindIndexMessage(
  field: BlindIndexedField,
  plaintext: string,
): Uint8Array<ArrayBuffer> {
  refuseUnindexedField(field);

  // The three leading fields plus a fourth that is deliberately empty, which is
  // how the join puts a separator *after* the column without this file naming
  // the byte a second time: `buildAssociatedData` keeps every field, empty ones
  // included, and puts one separator between each pair. What follows the last
  // separator is the name — as bytes, because the module that owns FR-076's four
  // steps hands back bytes and no step of it is repeated here.
  const head = buildAssociatedData(
    BLIND_INDEX_MESSAGE_PREFIX,
    field.table,
    field.column,
    '',
  );
  const name = normalizeNameForIndex(plaintext);

  const message = new Uint8Array(head.length + name.length);
  message.set(head, 0);
  message.set(name, head.length);

  return message;
}

/**
 * Computes the blind index of `plaintext` for `field` under the account's index
 * key, and returns it as unpadded base64url — **43 characters, always**, because
 * HMAC-SHA-256 is 32 bytes and unpadded base64url of 32 bytes is 43.
 *
 * The width is worth stating because it is the only shape check anything
 * downstream can make. A truncated or re-encoded value is otherwise
 * indistinguishable from a correct one: it is stable, it never collides, and it
 * is wrong for the life of the account. `importHmacSha256Key` makes the matching
 * argument one layer down about the key's own width, where the shared
 * `requireAccountKeyWidth` is the only guard there is.
 *
 * `indexKey` is an HMAC-SHA-256 key with `['sign']` and nothing else — `verify`
 * is deliberately not a usage it holds, and `account-keys.ts` argues why at the
 * import door. A key imported for AES-GCM cannot be substituted: measured there,
 * the platform refuses `sign` under one with `InvalidAccessError`.
 *
 * Rejects rather than throwing synchronously, on a pair that is not one of
 * {@link BLIND_INDEXED_FIELDS} and on whatever the platform refuses the key or
 * the signature with. A synchronous throw out of a function whose signature
 * promises a `Promise` escapes past every caller's `catch` on the result — the
 * rule `narrative-cipher.ts` keeps at its own doors.
 *
 * There is nothing to zero-fill here and no `finally`. The message is a second
 * copy of a string the caller already holds, and a JavaScript string cannot be
 * wiped; the same argument `sealNarrativeField` makes about its plaintext bytes
 * applies unchanged.
 */
// `async` is what turns the pair refusal below into a rejection: a `throw` out
// of an async function is a rejected promise, where the same `throw` out of a
// function that merely returned one would escape past every caller's `catch` on
// the result. It is not decoration around a single `await`.
export async function computeBlindIndex(
  indexKey: CryptoKey,
  field: BlindIndexedField,
  plaintext: string,
): Promise<string> {
  // **The door, stated at the top of the operation** rather than left to what
  // the builder below happens to do on the way past, which is how the neighbour
  // writes `sealNarrativeField`. The pair is judged a second time inside
  // {@link blindIndexMessage}, which is deliberate and free — that function owes
  // the same refusal to its own callers, and a scan of four short pairs is not a
  // cost anybody can measure against a `subtle.sign`. What it buys is that
  // neither refusal can be removed by editing the other, and that the exported
  // refusal is the one this path applies rather than a copy of it.
  //
  // **The limit of that, measured rather than reasoned: deleting this line
  // reddens nothing** — the builder one statement on refuses the same pair, and
  // every case that can see a refusal sees that one. It was run. So the line is
  // held by review, which is why the paragraph above is written out instead of
  // being left as a shape somebody is expected to recognise.
  //
  // Both calls sit inside the async frame, so an illegal pair rejects rather
  // than throwing where the caller cannot catch it.
  refuseUnindexedField(field);

  const message = blindIndexMessage(field, plaintext);

  // `indexKey` crosses as a parameter and is not held: no field, no
  // module-level value, no cache. Custody owns the key and its lifetime, and
  // that is what lets custody promise a page reload locks the account.
  const tag = await crypto.subtle.sign('HMAC', indexKey, message);

  return encodeBase64Url(new Uint8Array(tag));
}

/**
 * Refuses a table and column this module cannot compute an index over, and
 * returns nothing.
 *
 * The pair is looked up at run time as well as by the closed union, for the
 * reason {@link refuseInvalidBinding} states next door: a table name arriving as
 * data — off a response, out of a configuration, through one `as` in a mapper —
 * has been through the compiler's check not at all.
 *
 * **A lookup of the pair and never two membership tests.** `payees` is a real
 * table and `description` is a real column of it, and `payees.description` is
 * not one of the four; a check asking the two questions separately answers yes
 * to it and computes an index over a column nothing encrypts.
 *
 * **Named, exported and returning `void`, rather than left as a discarded call
 * to {@link blindIndexMessage}.** A caller that has to know whether a field is
 * usable before it reaches this module's real work — custody judges its field
 * *before* it looks at whether a key is held, so that a caller's defect is
 * reported the same way whether or not a factor has been presented — wants the
 * *refusal* and not the bytes, and building a message in order to throw it away
 * is a statement whose only visible effect is a throw. That is the shape a
 * reader deletes: it reads as a leftover, it survives no tidy-up of the lines
 * around it, and the day it goes the check goes with it in silence, because
 * every index computed for a pair taken out of {@link BLIND_INDEXED_FIELDS}
 * still comes out right. Asked for by name, the refusal cannot be mistaken for
 * a value nobody used. The argument is the neighbour's, and it is repeated here
 * rather than pointed at: two modules doing one job in opposite shapes is worse
 * than either shape, and the shape a reader copies is the one in front of them.
 *
 * **One thing differs from the neighbour in what is refused, and it is the
 * whole of that difference** — what is *thrown* differs too, and the paragraph
 * below is about that. {@link refuseInvalidBinding} refuses a *spelling* as
 * well as a membership, because its binding carries a row id — a value that is
 * legal in more than one spelling, chosen by the caller and read off a row, so
 * there is a canonical form to insist on. This one refuses membership and
 * nothing else. A field is chosen from a list this module publishes, so the
 * only question available is whether the pair is one of the four; there is no
 * second spelling of `payees` to fold, and no field here whose value a caller
 * supplies.
 *
 * It throws a plain `Error` and declares no type of its own. There is no
 * ciphertext in this operation, so a caller can never need to tell "you asked
 * for something impossible" from "this stored value did not open" — the
 * distinction `NarrativeFieldMisuseError` exists for next door.
 */
export function refuseUnindexedField(field: BlindIndexedField): void {
  if (
    !BLIND_INDEXED_FIELDS.some(
      (candidate) =>
        candidate.table === field.table && candidate.column === field.column,
    )
  ) {
    throw new Error(
      'A blind index can only be computed for a table and column this module lists as a pair.',
    );
  }
}
