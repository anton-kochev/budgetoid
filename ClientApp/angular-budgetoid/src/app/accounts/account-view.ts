// One account as the accounts screen may know it, and the transform that turns
// the row the API sent into it.
//
// `accounts.name` is a sealed column: what crosses the wire is an AEAD envelope
// in unpadded base64url, and the only thing in this client that can open it is
// the account's content key, which `AccountKeyCustodyService` holds and no
// member of it gives out. So the row a template renders is not the row the API
// sent, and this file is the seam between them.
//
// **It takes the capability as a function and never the service that owns
// one.** `NarrativeOpener` is exactly one power — open this wire value under
// this binding — where `AccountKeyCustodyService` is that power beside
// `unlock`, `lock` and `adopt`. Handed the service, a row-shaped transform
// could end a session on its way past, and it would need a `TestBed` to be
// exercised at all; handed the function, its spec stands it up in two lines and
// there is nothing else in reach. `narrative-text.ts` wrote both seam types
// down ahead of any caller and argues the same thing at greater length; this is
// the first caller.
//
// **The trap in wiring it is mechanical and it is not a compile error.** The
// capability has to be handed over as an arrow — `(binding, wire) =>
// custody.openField(binding, wire)` — and never as the bare method reference
// `custody.openField`. `openField` reads a `#` field, so the bare reference
// type-checks perfectly and answers every call with a `TypeError` on the wrong
// receiver, and `@typescript-eslint/unbound-method` is off for specs. Only a
// call finds it, which is why the service's spec drives its custody stub
// through a `#` field of its own.
//
// **Nothing here collapses a word into a string.** No branch turns `locked` or
// `unreadable` into `''` or `'—'`: the moment it does, the screen is making a
// claim about the *account* when the truth is about this *tab*, and nothing
// downstream can tell the two apart again. `docs/design/components.md`, "The
// locked account", states the rule and `narrative-value` is what renders each
// word.
//
// **A misuse rejection propagates rather than becoming `unreadable`.**
// `NarrativeFieldMisuseError` is the codec's word for a refusal it made about
// the *call* — a pair it does not publish, a row id in a spelling it cannot
// reproduce — and none of it is a claim about what is stored in that column.
// Caught and rendered, a caller's defect arrives on screen as a sentence about
// damaged text: a bug wearing a UI, in front of somebody who can do nothing
// whatever about it, over a row that is perfectly fine. There is therefore no
// `try` in this file, and the read path that drives it uses `Promise.all` and
// not `allSettled` — the second would swallow the same rejection one layer up.
//
// **`accounts` has no nullable narrative column, so the null arm is a rule
// rather than a branch.** A column holding no value is `NarrativeText | null`
// at the edge that reads the row — never a fourth word — and that question is
// answered *before* any key is involved, because whether a column is null is
// known before whether it opens is. `accounts.name` is `NOT NULL`, so this
// mapper has nothing to answer it about; the first mapper over
// `categories.description` will, and the ordering is what it inherits. A branch
// here for a value this row cannot carry would be a case nothing can produce,
// which is the thing `narrative-text.ts` refuses at its own union.
//
// **The members are listed rather than spread, and that is a decision.**
// `{ ...dto, name }` is shorter and carries the day's DTO through for free —
// and the day `accounts` gains a second sealed column it would carry that
// column's ciphertext into a view as though it were text, with nothing red. A
// list means a new column has to be named here before it can be rendered.
import type {
  AccountDto,
  AccountType,
} from '@app-core/api/account-api.service';
import type { BlindIndexedField } from '@app-core/security/blind-index';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';

/**
 * The one blind-indexed pair this screen keys on.
 *
 * Written as a pair and looked up as one: `accounts` is a table and `name` is a
 * column and neither is a membership test the other can stand in for. The
 * `satisfies` is what ties it to the codec's list — a pair this client stopped
 * indexing reddens here rather than at the call.
 */
export const ACCOUNT_NAME_FIELD = {
  table: 'accounts',
  column: 'name',
} as const satisfies BlindIndexedField;

/**
 * The binding one account's name is sealed under and opened against.
 *
 * `rowId` is the row's own identifier in the canonical spelling — the value the
 * envelope's associated data was built from. A disagreement stops the field
 * opening, permanently, with no error naming the cause, which is why the caller
 * passes the id it read off the row rather than one it made.
 */
export function accountNameBinding(rowId: string): NarrativeFieldBinding {
  return { ...ACCOUNT_NAME_FIELD, rowId };
}

/** One account as a template may render it: the name is a word, not a string. */
export interface AccountView {
  readonly id: string;
  /** The opened name, or the reason there is none. Never `''` and never `'—'`. */
  readonly name: NarrativeText;
  readonly type: AccountType;
  readonly openingBalance: number;
  readonly createdAtUtc: string;
  readonly currencyCode: string;
  readonly currencyName: string;
  readonly currencySymbol: string;
  readonly currencyMinorUnit: number;
}

/**
 * Turns one row the API sent into the row a template renders, opening its name
 * under the account's content key.
 *
 * Rejects on whatever `open` rejects on — a binding the codec refuses, a
 * content key whose bytes can be read back out — because those are facts about
 * the call and not about the column. The head of this file argues why that
 * stays a rejection.
 */
export async function toAccountView(
  dto: AccountDto,
  open: NarrativeOpener,
): Promise<AccountView> {
  const name = await open(accountNameBinding(dto.id), dto.name);

  return {
    createdAtUtc: dto.createdAtUtc,
    currencyCode: dto.currencyCode,
    currencyMinorUnit: dto.currencyMinorUnit,
    currencyName: dto.currencyName,
    currencySymbol: dto.currencySymbol,
    id: dto.id,
    name,
    openingBalance: dto.openingBalance,
    type: dto.type,
  };
}
