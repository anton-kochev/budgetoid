// The narrative fields — the free text a person types into a ledger — sealed
// under the account's content key, one envelope per field of one row.
//
// The envelope is `key-envelope.ts`'s and is stated there: version (1) || nonce
// (12) || ciphertext || tag (16), AES-256-GCM. What this module owns is the
// **binding** — which table, which column, which row a ciphertext belongs to —
// and the wire form the column stores, unpadded base64url.
//
// **One caller, and no screen behind it.** `AccountKeyCustodyService` holds the
// account's content key, and the two operations that delegate to it —
// `sealField` and `openField` — are what call `sealNarrativeField` and
// `openNarrativeField`. Nothing calls *those*: no column in this product holds
// an envelope, so the only thing reaching custody's pair today is its spec. The
// claim this module makes about its own standing is therefore a smaller one
// than it used to be — called, but not yet from anywhere a person can walk to —
// and the order is still deliberate rather than a module left behind: the
// format is a cross-client contract, so it can be pinned against an answer
// computed outside this codebase —
// `docs/business-logic/vectors/narrative-field-v1.json` — before a single
// column holds an envelope, and a format is far cheaper to agree on before it
// has data written under it than after.
//
// **The key crosses as a parameter, and never the other way round.** Custody
// owns the content key and its lifetime; this module owns the binding and the
// wire form and owns no key at all. Every function here is handed the key for
// the length of one call and keeps it on no field, in no module-level value and
// in no cache, which is part of what lets custody promise that a page reload
// locks the account: there is no second place a key could still be sitting. The
// edge runs one way for the same reason — custody imports these functions, and
// nothing here imports custody.
//
// **What remains genuinely uncalled is the blind index**, and an absent
// operation reads as an oversight unless somebody says so. It is the third
// operation custody will grow and it is not written anywhere yet: the grammar
// its values are computed over waits on a revision of the specification, so the
// index key custody holds is still read by nothing.
//
// **Associated data is not carried inside the envelope.** It is rebuilt from
// wherever the ciphertext was found — this table, this column, this row — which
// is exactly what makes a ciphertext moved to another row, another column or
// another table fail to authenticate rather than decrypt into something. Wrong
// key, wrong binding and altered bytes are one indistinguishable failure by
// design: a caller learns the value is unusable and learns nothing about why,
// because anything finer is an oracle over data the caller was not given.
//
// **The row id is refused, never folded, and that is deliberately the opposite
// of `wrappedKeyAssociatedData` next door.** The argument is at the refusal
// itself, below, so that whoever arrives to make the two consistent reads it
// before editing either.
//
// **No length rule lives here.** Nothing refuses a long string, and the omission
// is a decision: a cap on narrative text is a product rule of its own, and the
// number a column has to hold is a fact about the *envelope*, which AES-GCM
// makes exactly 29 bytes longer than the UTF-8 of the text. A cap invented at
// this layer would measure the wrong number and would arrive as a rejected
// promise carrying the same error a corrupted key gives, where what a person
// needs is to be told, in the screen they typed it in, that the text is too
// long.
//
// **Nothing is normalised and nothing is trimmed.** What was typed is what is
// sealed, NFD included, and what comes back is the same code points rather than
// the ones they render as — for every well-formed string, which is the one
// qualification this claim needs and which `openNarrativeField` states in full.
// The normalisation this product does need belongs to the blind index, where the
// transform is a different one — case folding over a compatibility form — and
// folding the two together here would store text nobody wrote.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it.
import { buildAssociatedData } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
// The same question the wrapped-key grammar's factor id is asked — "is this
// *exactly* the canonical spelling", not "does this parse as a UUID" — so it is
// the same predicate, imported rather than restated. A second regular
// expression here would be a second definition of one spelling, and the half
// that drifted would still seal envelopes and still open the ones it wrote. The
// alias is because a row id is not a factor id; the shape they have to be is.
import { isCanonicalFactorId as isCanonicalRowId } from './factor-id';
import { openEnvelope, sealEnvelope } from './key-envelope';

/**
 * Every table and column whose contents are sealed as a narrative field, and no
 * others.
 *
 * A runtime value, with {@link NarrativeField} *derived* from it. The other way
 * round — a hand-written union beside a hand-written list — can be widened
 * without any value moving, and the only check available against that is a
 * type-level assertion, which looks identical whether it is asserting or has
 * quietly stopped.
 *
 * The *argument* for each pair lives in the spec, keyed by the pair, and the two
 * lists are pinned against each other in both directions: a ninth pair arriving
 * here with nobody able to say why it is encrypted reddens, and a reason left
 * behind for a pair dropped from here reddens too. Keeping the reasons beside
 * these literals would put the list in two places and pin nothing.
 *
 * One limit, measured rather than assumed and named in the spec: a member bolted
 * onto the derived *type* past this array reddens nothing, because the array
 * still has eight entries. That shape is held by review, for the same reason
 * `CLAUDE.md` gives about a second account-creating path — it is one line that
 * would redden no build.
 */
export const NARRATIVE_FIELDS = [
  { table: 'transactions', column: 'description' },
  { table: 'payees', column: 'name' },
  { table: 'accounts', column: 'name' },
  { table: 'categories', column: 'name' },
  { table: 'categories', column: 'description' },
  { table: 'category_groups', column: 'name' },
  { table: 'category_groups', column: 'description' },
  { table: 'budgets', column: 'name' },
] as const satisfies readonly NarrativeFieldShape[];

/** One legal table-and-column pair, derived from {@link NARRATIVE_FIELDS}. */
export type NarrativeField = (typeof NARRATIVE_FIELDS)[number];

/** A legal pair together with the row it names. */
export type NarrativeFieldBinding = NarrativeField & {
  /** The canonical lower-case 36-character hyphenated UUID, and nothing else. */
  readonly rowId: string;
};

/**
 * The literal that opens the associated data of every narrative field.
 *
 * Part of the definition of every envelope already written — associated data is
 * rebuilt from where a ciphertext was found rather than carried inside it, so a
 * changed prefix makes every stored field unopenable with the same failure a
 * corrupted key gives. It is versioned so that a second grammar can exist later
 * without this one becoming ambiguous.
 */
export const NARRATIVE_FIELD_AAD_PREFIX = 'budgetoid/field/v1';

// The shape each entry has to have, so an entry that lost its column is a
// compile error rather than a pair whose associated data is one field short.
// Local: the exported surface is the list and the type derived from it, and a
// second exported type describing the same thing is one more place a ninth
// field could be declared.
interface NarrativeFieldShape {
  readonly table: string;
  readonly column: string;
}

const utf8 = new TextEncoder();

// `fatal: true`, and it is load-bearing rather than tidy. A lenient decoder
// turns bytes that are not UTF-8 into U+FFFD, which reads as damaged text a
// person typed, is indistinguishable from it, and gets written straight back on
// the next save. Bytes that authenticated but are not UTF-8 mean a writer put
// something else in that column, which is a fact worth an error.
const strictUtf8 = new TextDecoder('utf-8', { fatal: true });

/**
 * Builds the associated data one narrative field is bound to:
 *
 * ```text
 * {@link NARRATIVE_FIELD_AAD_PREFIX} || 0x1F || <table> || 0x1F || <column> || 0x1F || <rowId>
 * ```
 *
 * in UTF-8.
 *
 * All four fields are load-bearing and each catches a different swap. Without
 * the column, a category's name and its description are interchangeable
 * ciphertexts and swapping them is a silent, successful decryption. Without the
 * table, a payee's name opens as a category's, since the two share the column
 * word. Without the row, every row in a column is interchangeable with every
 * other.
 *
 * Throws on a row id in any spelling but the canonical one. It never returns a
 * repaired value: see the refusal below for why this grammar refuses where its
 * neighbour folds.
 */
export function narrativeFieldAssociatedData(
  binding: NarrativeFieldBinding,
): Uint8Array {
  // **Refused, never folded — deliberately unlike `wrappedKeyAssociatedData`,
  // which folds case, braces, parentheses and the bare 32-digit form.** The two
  // are not inconsistent, and making them consistent would break one of them.
  //
  // There, the factor id is *minted by this client* before any server has seen
  // it — `factor-id.ts`'s `mintFactorId`, over `crypto.randomUUID`. Nothing
  // upstream hands `wrappedKeyAssociatedData` a canonical value, so the fold it
  // makes through `canonicalFactorId` is the only place one is made: folding is
  // a defence against a value arriving from elsewhere, and emitting one
  // spelling is a property of what a client creates — the argument stated at
  // that mint.
  //
  // Here nothing is chosen. The row id is whatever the row the client just read
  // handed back, in the one spelling the server renders a uuid as. There is
  // nothing to be tolerant of, so a fold could only invent a second spelling of
  // a value that has one — and it would invent it at the sealing end, which is
  // precisely where the damage is unrecoverable: associated data is rebuilt from
  // where a ciphertext was found, so text sealed under a spelling no later read
  // reproduces stops opening in both directions, permanently, with no error
  // anywhere naming the cause. A refusal costs a caller one bug report; a fold
  // costs a person their ledger.
  //
  // Only the row id is checked. The table and the column are closed unions this
  // module declares, which is what carries the claim that no field can contain
  // the separator, so the check is on the one field whose value a caller
  // chooses — the same line `wrappedKeyAssociatedData` draws between its factor
  // id and its purpose.
  if (!isCanonicalRowId(binding.rowId)) {
    throw new Error(
      'A narrative field can only be bound to a row id in the canonical spelling.',
    );
  }

  // The join and the separator are `associated-data.ts`'s, shared with the one
  // other grammar this client seals under. What stays here is what this grammar
  // is made of: these four fields, in this order.
  return buildAssociatedData(
    NARRATIVE_FIELD_AAD_PREFIX,
    binding.table,
    binding.column,
    binding.rowId,
  );
}

/**
 * Seals `plaintext` under the account's content key, bound to `binding`, and
 * returns the wire form the column stores: unpadded base64url over the
 * envelope.
 *
 * A fresh nonce per call, drawn by {@link sealEnvelope}. The text crosses into
 * bytes as UTF-8 and is otherwise untouched — not normalised, not trimmed, not
 * capped — so the ciphertext is exactly as long as the UTF-8 of what was typed.
 * The one exception is the crossing itself: an unpaired surrogate is replaced by
 * U+FFFD here, permanently, and {@link openNarrativeField} argues why.
 *
 * Rejects on an extractable key and on a row id in any spelling but the
 * canonical one, before the cipher is reached in either case.
 */
export async function sealNarrativeField(
  contentKey: CryptoKey,
  plaintext: string,
  binding: NarrativeFieldBinding,
): Promise<string> {
  refuseExtractableKey(contentKey);

  // Through `narrativeFieldAssociatedData` and never inline. The four fields
  // joined here by hand would produce byte-identical associated data and skip
  // the refusal above, which passes every case a round trip can see and seals
  // under a spelling no later read of that row can reproduce.
  const associatedData = narrativeFieldAssociatedData(binding);

  // Not zero-filled, and that is a decision rather than an oversight. These
  // bytes are a second copy of a string the caller holds, and a JavaScript
  // string cannot be wiped: clearing the copy while the original waits for the
  // collector buys nothing, and doing it anyway would suggest this module had
  // taken custody of narrative text the way `account-keys.ts` takes custody of
  // key material — where the bytes are the only copy there is, which is what
  // makes the wipes there worth their `finally`.
  const envelope = await sealEnvelope(
    contentKey,
    utf8.encode(plaintext),
    associatedData,
  );

  return encodeBase64Url(envelope);
}

/**
 * Opens `wire` under the account's content key and `binding`, or rejects.
 *
 * Rejects on an extractable key, on a row id in any spelling but the canonical
 * one, on a wire value the strict base64url decoder refuses, on an envelope
 * whose version or length is not this format's, on associated data other than
 * what the ciphertext was sealed under, and on bytes that authenticate but are
 * not UTF-8. Every one of them is one indistinguishable failure to a caller, on
 * purpose.
 *
 * Returns every **well-formed** string byte for byte, NFD included. Nothing is
 * normalised on the way out: doing it would hand a caller text that no longer
 * matches what it stored, and the next save would write the folded form over the
 * original.
 *
 * **A lone surrogate does not survive, and the loss happens before the cipher
 * rather than here.** `TextEncoder.encode` substitutes U+FFFD for an unpaired
 * surrogate — measured: `'café \uD83D'` comes back `'café �'` — so what is
 * sealed is already the replacement, and this function returns exactly what was
 * sealed. The strict decoder cannot catch it either: those bytes *are* valid
 * UTF-8, which is the whole reason the substitution is invisible. And it is
 * permanent, because the next save re-seals the U+FFFD; nothing later can tell
 * that a surrogate pair was ever there.
 *
 * The concrete path is worth naming, because this module deliberately holds no
 * length rule (see the head of the file): a caller that slices narrative text to
 * fit a column or a preview is exactly the caller that can split a surrogate pair
 * — `'lunch 🍕'.slice(0, 7)` seals and returns `'lunch �'`. A caller that
 * has to shorten text must do it by code point, not by UTF-16 unit.
 */
export async function openNarrativeField(
  contentKey: CryptoKey,
  wire: string,
  binding: NarrativeFieldBinding,
): Promise<string> {
  refuseExtractableKey(contentKey);

  // Inline here would be the same mistake with the opposite symptom: a reader
  // that accepted a spelling the seal refuses goes on working perfectly against
  // its own ciphertext and against nobody else's.
  const associatedData = narrativeFieldAssociatedData(binding);
  const opened = await openEnvelope(
    contentKey,
    decodeBase64Url(wire),
    associatedData,
  );

  return strictUtf8.decode(opened);
}

// The account's content key may not be one whose bytes can be read back out,
// and this is the door. There is no API that undoes an extractable import, so a
// key that arrives extractable is one that can already be logged, posted to a
// crash reporter or written to `localStorage`, whatever this module does with
// it afterwards.
//
// **Called before the cipher, in both directions, and the ordering carries more
// on the reading side than the writing one.** A reader that refused after
// calling `decrypt` has already put the account's narrative text in memory under
// a key that was never allowed to touch it, and then reported a failure — it did
// the thing the refusal exists to prevent. A rejection cannot tell the two
// apart, which is why the spec watches `crypto.subtle.encrypt` and
// `crypto.subtle.decrypt` themselves.
//
// Synchronous, and called from `async` functions so the throw becomes a rejected
// promise: a synchronous throw out of a function whose signature promises a
// `Promise` escapes past every caller's `catch` on the result.
function refuseExtractableKey(contentKey: CryptoKey): void {
  if (contentKey.extractable) {
    throw new Error(
      'A narrative field can only be sealed or opened under a non-extractable content key.',
    );
  }
}
