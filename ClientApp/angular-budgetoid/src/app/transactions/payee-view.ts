// One payee as this client may know it, and the two things the transaction
// write needs from it: the name a person reads, and the value a match is
// decided on.
//
// **It lives beside the transaction screen because that is its only caller.**
// A payee has no screen of its own — `POST /api/payees` exists so that writing
// a transaction can name a counterparty, and the resolution that calls this
// mapper is in `transactions.service.ts`. A `payees/` folder holding one file
// and no component would be a home for a module nobody imports, which is the
// shape this phase was formed to avoid. It moves the day a payee screen
// arrives.
//
// **The view carries a `nameKey` and the read that produced it does not.**
// `PayeeDto` is `(id, name)` and no route in the product returns a blind index
// — deliberately, because the index column is a deterministic per-budget
// fingerprint of every counterparty a person deals with, and handing it back
// would let anybody who saw two responses tell which names they had in common
// with no key anywhere in the exchange. So a client that wants to match a name
// has to *recompute* the index from the name it just opened, under a key one
// class holds and no member gives out. That is the whole reason
// {@link NarrativeIndexer} exists, and this is its first caller.
//
// **"Per budget" is true of the value as well as of the column now.** The index
// key is drawn once per account, so until the tenancy entered the message two
// budgets of one account produced one digest for one name — the correlation
// NFR-014 refuses. The identifier is therefore an argument of this mapper, and
// required: the whole migration was carried out over the compile errors a
// required member produces at every call site.
//
// **A payee whose name did not open has `nameKey: null`, and it can never
// match anything.** Two wrong shapes are available here and both are quiet. The
// first is keying the *wire* value: it produces a real 43-character string that
// is stable across reads, so two rows nobody can read would match each other
// and a transaction would be filed against a counterparty this browser has
// never seen. The second is keying `''` for a name that did not open, which
// does the same thing by another road and additionally collides with a payee
// genuinely named nothing. `null` is neither: it is not a key, so no comparison
// against a real key can succeed, and the write falls through to a create that
// the server's unique index judges — loudly, on bytes, which is the one party
// left that can judge it.
//
// **A browser that has not been told its budget takes the same `null`, and it
// is the same claim rather than a new one.** There is no key to compute, so
// there is no key, and the two substitutes above are exactly as wrong here. It
// costs nothing that a write can act on: a write from that browser does not
// happen at all — `transactions.service.ts` refuses it with `unreachable` before
// any of this is consulted — so a list of unmatched rows never reaches a create.
//
// **Nothing here collapses a word into a string**, the rule
// `docs/design/components.md` states under "The locked account": no branch
// turns `locked` or `unreadable` into `''` or `'—'`, because the first is a
// fact about this tab and the second a fact about this row and a screen that
// prints one for the other can never tell them apart again.
//
// **A misuse rejection propagates rather than becoming a word.** Both
// capabilities can reject with `NarrativeFieldMisuseError` — a pair the codec
// does not publish, a row id in a spelling it cannot reproduce — and every one
// of those is a defect in this client rather than a fact about a row. There is
// therefore no `try` in this file, and the read path that drives it uses
// `Promise.all` and not `allSettled`.
import type { PayeeDto } from '@app-core/api/payees-api.service';
import type {
  BlindIndexBinding,
  BlindIndexedField,
} from '@app-core/security/blind-index';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeIndexer,
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';

/**
 * The blind-indexed pair a payee is matched on.
 *
 * Written as a pair and looked up as one, and it carries **no row id** — the
 * deliberate inverse of {@link payeeNameBinding}. An index has to be *equal*
 * for equal names across rows, which is exactly what a per-row binding would
 * destroy: every payee would key to its own unique value and no lookup would
 * ever match.
 */
export const PAYEE_NAME_FIELD = {
  table: 'payees',
  column: 'name',
} as const satisfies BlindIndexedField;

/**
 * The binding one payee's name is sealed under and opened against.
 *
 * `rowId` is the payee's own identifier in the canonical spelling — the value
 * the envelope's associated data was built from. On a transaction that is the
 * `payeeId` beside the sealed name and never the transaction's own id.
 */
export function payeeNameBinding(rowId: string): NarrativeFieldBinding {
  return { ...PAYEE_NAME_FIELD, rowId };
}

/**
 * The binding a payee's name is keyed under, for the match and for the column.
 *
 * A budget and no row — the inverse of {@link payeeNameBinding} in both halves.
 * Equal names across rows must key alike, or `matchPayeeByIndex` finds nothing
 * and the server's unique index enforces nothing a person can observe; two
 * budgets of one account must not, or an operator reading both sees which
 * counterparties they share. `blind-index.ts` argues it at the message.
 *
 * `budgetId` is what `GET /api/me` said, in the spelling it said it; the codec
 * refuses any other and nothing here folds one.
 */
export function payeeNameIndexBinding(budgetId: string): BlindIndexBinding {
  return { ...PAYEE_NAME_FIELD, budgetId };
}

/** One payee as this client may know it. */
export interface PayeeView {
  readonly id: string;
  /** The opened name, or the reason there is none. Never `''` and never `'—'`. */
  readonly name: NarrativeText;
  /**
   * The blind index over the opened name, or `null` when there is nothing to
   * key it with: the row did not open, this browser is holding no index key, or
   * it has not been told which budget it is in.
   *
   * `null` is not a key, so a row carrying it matches nothing — the head of
   * this file argues why the two available substitutes are both silent.
   */
  readonly nameKey: string | null;
}

/**
 * Turns one payee the API sent into the row this client matches and renders.
 *
 * `budgetId` is the tenancy the index is keyed inside, or `null` when this
 * browser has not been told which one it is in. It is a parameter rather than a
 * value this file reaches for, so a mapper still takes exactly the capabilities
 * it needs and a `TestBed` is still not one of them — and it is **required**,
 * because the compile error a required member produces at every call site is the
 * whole net the tenancy was added over.
 *
 * Rejects on whatever `open` or `index` rejects on, because those are facts
 * about the call and not about the row.
 */
export async function toPayeeView(
  dto: PayeeDto,
  budgetId: string | null,
  open: NarrativeOpener,
  index: NarrativeIndexer,
): Promise<PayeeView> {
  const name = await open(payeeNameBinding(dto.id), dto.name);

  // Asked before the index, and the early return is not an optimisation: there
  // is no plaintext to key, and every value that could stand in for one — the
  // wire bytes, the empty string — produces a key that matches something. An
  // unknown tenancy joins the same arm for the same reason: there is no key to
  // compute, and the head of this file argues why that costs a write nothing.
  if (name.state !== 'text' || budgetId === null) {
    return { id: dto.id, name, nameKey: null };
  }

  const indexed = await index(payeeNameIndexBinding(budgetId), name.value);

  return {
    id: dto.id,
    name,
    nameKey: indexed.state === 'computed' ? indexed.value : null,
  };
}

/**
 * Finds the payee already holding a name, by the blind index over that name.
 *
 * **It takes the index and never the text, and the signature is the rule.**
 * Case folding comes free this way: `trader joe's` and `Trader Joe's` normalize
 * to one message and key to one value, which is the behaviour the server's
 * `case_insensitive` collation used to provide and gave up when the column
 * became `bytea`. Comparing decrypted text instead agrees with this on almost
 * every name a person types and disagrees on exactly the ones where the
 * difference decides a match — and the symptom is a second payee for one
 * counterparty, on the column whose entire purpose is that equal names collide.
 *
 * It is also the same bytes the database decides uniqueness on, so a match here
 * and a refusal there can never disagree about what "the same name" means.
 *
 * **A row carrying no key matches nothing, and that is checked rather than
 * implied by the parameter's type.** The rows on the list are `string | null`
 * by design, so the one shape that turns this function into the opposite of
 * itself is an asked key that is also `null`: `null === null` reuses a payee
 * this browser cannot read, and the transaction is filed against a
 * counterparty nobody chose. The signature forbids it and the signature is not
 * a runtime, so the comparison names the null it is defending against.
 */
export function matchPayeeByIndex(
  payees: readonly PayeeView[],
  nameKey: string,
): PayeeView | null {
  return (
    payees.find(
      (payee) => payee.nameKey !== null && payee.nameKey === nameKey,
    ) ?? null
  );
}
