// The narrative fields — the free text a person types into a ledger — sealed
// under the account's content key, one envelope per field of one row.
//
// The envelope is `key-envelope.ts`'s and is stated there: version (1) || nonce
// (12) || ciphertext || tag (16), AES-256-GCM. What this module owns is the
// **binding** — which table, which column, which row a ciphertext belongs to —
// and the wire form the column stores, unpadded base64url.
//
// **One caller, and no screen behind it.** `AccountKeyCustodyService` holds the
// account's content key, and the two of its operations that reach for that key
// — `sealField` and `openField` — are what call `sealNarrativeField` and
// `openNarrativeField`. Nothing calls *those*: no column in this product holds
// an envelope, so the only thing reaching that pair today is its spec. The
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
// **The blind index is the other grammar, and what separates the two is the row
// id rather than which of them exists.** It is custody's third operation, it is
// written, and `blind-index.ts` makes the same claim about its own standing
// that the paragraph above makes here — one caller, no screen behind it — for
// the same reason, against vectors of its own. What is worth stating on this
// side is the rule that keeps them two modules rather than one with a flag: a
// narrative ciphertext is bound to its **row**, which is exactly what makes a
// value moved between rows fail to authenticate, while an index must be
// **equal across rows** or it answers no lookup anybody writes. The two
// requirements are exact opposites, so a parameter choosing between them would
// decide, invisibly, whether a value defends the row it sits in or answers the
// query it was computed for — and both settings produce a value that looks
// right.
//
// **Associated data is not carried inside the envelope.** It is rebuilt from
// wherever the ciphertext was found — this table, this column, this row — which
// is exactly what makes a ciphertext moved to another row, another column or
// another table fail to authenticate rather than decrypt into something. Wrong
// key, wrong binding and altered bytes are one indistinguishable failure by
// design: a caller learns the value is unusable and learns nothing about why,
// because anything finer is an oracle over data the caller was not given.
//
// **All three fields of a binding are checked at runtime, and not because the
// compiler is untrusted.** The table and the column are looked up as a *pair* —
// the eight entries are pairs and not a cross product — and the row id is
// refused, never folded, which is deliberately the opposite of
// `wrappedKeyAssociatedData` next door. Both arguments are at the checks
// themselves, below, so that whoever arrives to make them consistent with each
// other, or with the neighbouring grammar, reads them before editing anything.
// What the checks are for is a caller the compiler never saw: a table name that
// arrives as data, through one `as NarrativeFieldBinding` in a mapper.
//
// **Two kinds of failure leave this module and they are told apart by type.** A
// refusal made *before* a cipher is reached — a binding this grammar cannot be
// built over, a key that may never touch the plaintext — is a fact about the
// call, and it arrives as `NarrativeFieldMisuseError`. Everything else is a
// ciphertext that did not open, and stays the one indistinguishable failure the
// paragraph above argues for. The split is not a nicety: a caller that wraps an
// open in a `catch` has to answer "is this stored value damaged?", and the
// answer is *no* whenever the throw was about the call. Without a type the only
// way to keep the two apart is for each caller to re-apply this module's own
// pre-cipher checks above its `catch` — a second copy of every rule here, in
// every caller, drifting quietly from the copy the cipher path runs.
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

/**
 * Every refusal this module makes **about its caller**, before a cipher is
 * reached: a binding it cannot build this grammar over, or a key it may never
 * touch the plaintext with.
 *
 * **What it is for is a distinction a caller cannot otherwise make.** A
 * rejection out of {@link openNarrativeField} means one of two entirely
 * different things — *you asked for something impossible*, or *this stored value
 * did not open* — and only the second is a state a person can be shown and can
 * act on. The first is a defect in the call, which no ceremony, no retry and no
 * recovery factor fixes; rendered as damaged text it is a bug wearing a UI, put
 * in front of somebody over a row that is perfectly fine.
 *
 * **It is never thrown for a value that failed to authenticate.** A wrong key, a
 * ciphertext presented under another row, column or table, altered bytes, a wire
 * value the strict decoder refuses and bytes that authenticate but are not UTF-8
 * all keep arriving as whatever the platform or the decoder threw, and stay one
 * indistinguishable failure by design — anything finer is an oracle over data
 * the caller was not given. This type says nothing whatever about a ciphertext.
 * It says the call was not one this module could make.
 *
 * **A class extending `Error` with a stable {@link name}**, rather than a
 * sentinel message or a marker property on a plain `Error`. `instanceof` is the
 * check every caller in this bundle makes, and it is the one to reach for. The
 * `name` is the second answer, for the case `instanceof` cannot see: two copies
 * of this module loaded into one page are two different class objects, so an
 * error from the far one is `instanceof` nothing a caller holds while still
 * reading `'NarrativeFieldMisuseError'`.
 *
 * Written as a class *field* rather than left on the prototype, so it is an own,
 * enumerable property. Measured on Node 22: `Object.keys` gives `['name']` and
 * `JSON.stringify` gives `{"name":"NarrativeFieldMisuseError"}` — a plain
 * `name` on the prototype is in neither. **The limit of that, measured on the
 * same run and worth naming because a reader will assume otherwise**:
 * `structuredClone` does *not* carry it. The clone comes back with
 * `name === 'Error'` and an empty `JSON.stringify`, because the platform records
 * only the handful of names it recognises. Nothing crosses a worker or a
 * message port in this client today; the day something does, the type has not
 * travelled with it.
 *
 * Matching on message text is the remaining alternative, and it would make every
 * sentence in this file part of the contract — quietly, with a rewording as the
 * thing that breaks a caller.
 */
export class NarrativeFieldMisuseError extends Error {
  public override readonly name = 'NarrativeFieldMisuseError';
}

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
 * Refuses a binding this module cannot seal or open against, and returns
 * nothing.
 *
 * All three fields, and each for its own reason. The table and the column are
 * looked up **as a pair** in {@link NARRATIVE_FIELDS}; the row id is required in
 * the canonical spelling, refused rather than folded. Both arguments are at the
 * checks themselves, because a reader arriving to make them consistent is
 * looking at two different questions.
 *
 * The table and the column are closed unions this module declares, so a caller
 * the compiler assembled has already been through it for those two. That is not
 * the caller this check is for: a table name arriving as data — out of a
 * configuration, off a response, through one `as NarrativeFieldBinding` in a
 * view-model mapper — has been through nothing.
 *
 * **Named, exported and returning `void`, rather than left as a discarded call
 * to {@link narrativeFieldAssociatedData}.** A caller that has to know whether a
 * binding is usable — before a `catch` that would otherwise read its own defect
 * as a damaged stored value — wants the *refusal* and not the bytes, and
 * building associated data in order to throw it away is a statement whose only
 * visible effect is a throw. That is the shape a reader deletes: it reads as a
 * leftover, it survives no tidy-up of the lines around it, and the day it goes
 * the check goes with it in silence, because every round trip that client makes
 * against ciphertext it wrote itself still passes. Asked for by name, the
 * refusal cannot be mistaken for a value nobody used.
 *
 * It throws {@link NarrativeFieldMisuseError} and nothing else, which is what
 * lets a caller tell it from a ciphertext that did not open.
 */
export function refuseInvalidBinding(binding: NarrativeFieldBinding): void {
  // **A lookup of the pair, and never two membership tests.** The eight entries
  // are pairs and not a cross product: `transactions` is a real table, `name` is
  // a real column, and `transactions.name` does not exist. "Is the table one of
  // the eight tables" and "is the column one of the eight columns" both answer
  // yes for it, so a check written that way waves through a binding pointing at
  // nothing — and a mapper that takes its table from one place and its column
  // from another is exactly the caller that produces it. Both fields have to be
  // read off the **same** entry, which is what the single predicate below does;
  // splitting it into two `some` calls is the same mistake with a different
  // shape.
  //
  // Refusing it is right for the reason the two absent-value cases are refused:
  // nothing can ever be read back out of a column that does not exist, so text
  // sealed under that binding is sealed under a grammar no read of that row will
  // ever rebuild. It is not a narrower binding. It is a binding naming nothing.
  //
  // **A scan of eight entries rather than a prebuilt `Set` of joined keys.** A
  // set needs a separator, and a separator here is a second grammar over the
  // same two fields sitting next to the one that reaches envelopes — one that
  // would have to be argued not to collide with it. Eight comparisons of two
  // short strings is not a cost anybody can measure against a `subtle.encrypt`.
  //
  // **The pair is looked up where the row id is refused, and those are different
  // questions — do not make them consistent.** The row id is asked *what
  // spelling* a value has, which is a question about a value that is legal
  // either way and could therefore be folded; the argument below is about why it
  // is not. The pair is asked *whether the thing exists at all*, which nothing
  // can repair, so there is no fold to refuse and no choice being made. One
  // check declines to be tolerant; the other has nothing to be tolerant of.
  //
  // **It runs before the cipher, and that is held twice over — by construction,
  // and now by a test as well.** The pair check is inside the function the
  // row-id check is already inside, so it is reached from the same two call
  // sites, above the same `sealEnvelope` and `openEnvelope` — there is no
  // arrangement of these lines in which one refusal is pre-cipher and the other
  // is not. What holds the *pair's* ordering is therefore exactly what holds the
  // row id's. What used to stand on that alone now stands on the seam
  // `refuseExtractableKey`'s cases already used: spies on
  // `crypto.subtle.encrypt` and `crypto.subtle.decrypt`, calling through, over
  // both binding refusals and both operations — four orderings, each one edit
  // away from the other three. The spec asserts neither cipher was reached, and
  // carries positive controls in the same case, because a spy on a method
  // nothing reaches reports "never called" perfectly.
  //
  // The type alone was never enough to hold this, which is why the spies are
  // worth their length: a function that sealed first and threw on the way out
  // rejects identically, so a case asking only *which* error arrived cannot see
  // the difference between a refusal and a cipher that ran before one.
  if (
    !NARRATIVE_FIELDS.some(
      (field) =>
        field.table === binding.table && field.column === binding.column,
    )
  ) {
    throw new NarrativeFieldMisuseError(
      'A narrative field can only be bound to a table and column this module lists as a pair.',
    );
  }

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
  // The row id is the one field whose *value* a caller chooses — the same line
  // `wrappedKeyAssociatedData` draws between its factor id and its purpose. The
  // pair above is drawn from a list this module owns, which is what carries the
  // claim that no field of the associated data can contain the separator: with
  // the lookup in place that claim is a runtime fact for two of the three fields
  // rather than a property of the type alone, and the third cannot hold a 0x1F
  // and still be a canonical uuid.
  if (!isCanonicalRowId(binding.rowId)) {
    throw new NarrativeFieldMisuseError(
      'A narrative field can only be bound to a row id in the canonical spelling.',
    );
  }
}

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
 * Throws {@link NarrativeFieldMisuseError} on a table and column that are not
 * one of {@link NARRATIVE_FIELDS}' pairs, and on a row id in any spelling but
 * the canonical one. It never returns a repaired value: see
 * {@link refuseInvalidBinding} for why this grammar refuses where its neighbour
 * folds.
 */
export function narrativeFieldAssociatedData(
  binding: NarrativeFieldBinding,
): Uint8Array {
  // Through the named refusal, so the rule has one definition. A copy of the
  // predicate here would be a second opinion about which spelling is legal,
  // agreeing with the first on every value anybody happens to test and
  // disagreeing somewhere nobody looked — and both halves would go on sealing
  // and opening everything they had written themselves.
  refuseInvalidBinding(binding);

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
 * Rejects with {@link NarrativeFieldMisuseError} on an extractable key, on a
 * table and column that are not one of {@link NARRATIVE_FIELDS}' pairs, and on a
 * row id in any spelling but the canonical one, before the cipher is reached in
 * every case.
 */
export async function sealNarrativeField(
  contentKey: CryptoKey,
  plaintext: string,
  binding: NarrativeFieldBinding,
): Promise<string> {
  // **Both doors, stated together and at the top.** The claim this module makes
  // — that everything it refuses about the *call* is refused before a cipher
  // runs — is then readable in one place on each operation, rather than resting
  // on what a builder further down happens to do on the way past. The binding
  // is judged a second time inside `narrativeFieldAssociatedData` below, which
  // is deliberate and free: that function owes the same refusal to its own
  // callers, and one predicate over one string is not a cost anybody can
  // measure. What it buys is that neither refusal can be removed by editing the
  // other.
  //
  // **The limit of that, measured rather than reasoned: deleting the
  // `refuseInvalidBinding` call below reddens nothing** — not here, not on the
  // reading side, not anywhere in the suite — because the builder a few
  // statements on refuses the same binding, and every case that can see a
  // refusal sees that one. It was run. So the call is held by review, which is
  // why the paragraph above is written out rather than left as a shape somebody
  // is expected to recognise.
  refuseExtractableKey(contentKey);
  refuseInvalidBinding(binding);

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
 * Rejects on an extractable key, on a table and column that are not one of
 * {@link NARRATIVE_FIELDS}' pairs, on a row id in any spelling but the canonical
 * one, on a wire value the strict base64url decoder refuses, on an envelope
 * whose version or length is not this format's, on associated data other than
 * what the ciphertext was sealed under, and on bytes that authenticate but are
 * not UTF-8.
 *
 * The first three are {@link NarrativeFieldMisuseError} and are refused before
 * the cipher; the rest are one indistinguishable failure to a caller, on purpose,
 * and carry no type of this module's. Nothing finer is available within either
 * group, and the boundary between them is the only distinction a caller is
 * entitled to.
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
  // The same two doors, in the same order, for the reason `sealNarrativeField`
  // states — and they carry more here, because this is the side a caller wraps
  // in a `catch`. Every refusal above this line is a `NarrativeFieldMisuseError`
  // and none of it is a claim about the value stored in that column.
  refuseExtractableKey(contentKey);
  refuseInvalidBinding(binding);

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
//
// **`NarrativeFieldMisuseError`, the same type the binding refusal throws**, and
// it is the same kind of fact: a key whose bytes can be read back out is a
// statement about what the caller handed over, not about the value in the
// column. Local rather than exported, unlike `refuseInvalidBinding`, because
// `CryptoKey.extractable` is a boolean the platform owns and a caller asking the
// question for itself cannot get a *different* answer — the argument that makes
// a shared predicate load-bearing for the spelling does not hold here.
function refuseExtractableKey(contentKey: CryptoKey): void {
  if (contentKey.extractable) {
    throw new NarrativeFieldMisuseError(
      'A narrative field can only be sealed or opened under a non-extractable content key.',
    );
  }
}
