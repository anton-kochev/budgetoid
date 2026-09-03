// One transaction as the transactions screen may know it, and the transform
// that turns the row the API sent into it.
//
// **This row carries five envelopes and four of them belong to somebody else.**
// `description` is the transaction's own note; `accountName`, `payeeName`,
// `categoryName` and `categoryGroupName` are copies of four other rows' names,
// denormalized onto this one so the list can render without four more requests.
// Associated data is rebuilt from wherever a ciphertext was found rather than
// carried inside the envelope, so **each foreign name is opened under the
// foreign row's binding, rebuilt from the identifier already on the DTO** —
// which is why all four identifiers cross the wire beside them.
//
// **Opening one of them under this transaction's id is the defect this file
// exists to prevent, and it is silent.** The tag check fails, the value comes
// back `unreadable`, and `unreadable` is the same word a genuinely damaged
// column produces: a screen draws an em dash, no error names the cause, and
// nothing on the server can see it — it holds no key and cannot tell a value
// that opened from one that did not. The argument gets *stronger* with each
// member rather than weaker, which is why the mapper never reaches for a
// default binding: there is no "close enough" here.
//
// **The two bindings that already have owners are imported rather than
// restated.** `accountNameBinding` belongs to the accounts screen and
// `payeeNameBinding` to `payee-view.ts`, and a second spelling of either is a
// second definition of the value an envelope was sealed against. The two
// categories bindings are declared here because nothing owns them yet — the
// categories screens are unwired and have no view model — and they move to that
// module the day it exists.
//
// **A column holding no value is `NarrativeText | null`, and the null arm is
// answered before any key is involved.** Whether a column is null is known
// before whether it opens is, and "nobody filed a note" is not "we could not
// read this": a fourth word would file the first fact inside a union about the
// second, and the first template written against it renders a failure over a
// field somebody simply left empty.
//
// **A name arriving without its identifier is `null`, never opened under a
// substitute.** The server does not send that shape; the rule is what happens
// if it ever does, because the transaction's own id is sitting right there and
// using it turns a fine row into a damaged-looking one.
//
// **Nothing here collapses a word into a string** — no branch turns `locked` or
// `unreadable` into `''` or `'—'`, the rule `docs/design/components.md` states
// under "The locked account" — and **a misuse rejection propagates** rather
// than becoming a word, which is why the read path that drives this uses
// `Promise.all` and not `allSettled`.
//
// **The members are listed rather than spread.** `{ ...dto, description }` is
// shorter and would carry a sixth sealed column into a view as though it were
// text the day this row gains one, with nothing red.
import type { TransactionDto } from '@app-core/api/transactions-api.service';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { accountNameBinding } from '../accounts/account-view';
import { payeeNameBinding } from './payee-view';

/** The binding this transaction's own note is sealed under. */
export function transactionDescriptionBinding(
  rowId: string,
): NarrativeFieldBinding {
  return { table: 'transactions', column: 'description', rowId };
}

/**
 * The binding one category's name is sealed under.
 *
 * Declared here because the categories screens are unwired and own no view
 * model; it moves to that module the day one exists, rather than being
 * duplicated into it.
 */
export function categoryNameBinding(rowId: string): NarrativeFieldBinding {
  return { table: 'categories', column: 'name', rowId };
}

/** The binding one category group's name is sealed under. See above. */
export function categoryGroupNameBinding(rowId: string): NarrativeFieldBinding {
  return { table: 'category_groups', column: 'name', rowId };
}

/** One transaction as a template may render it: five words, not five strings. */
export interface TransactionView {
  readonly id: string;
  readonly amount: number;
  readonly date: string;
  /** The opened note, or `null` when nobody filed one. */
  readonly description: NarrativeText | null;
  readonly createdAtUtc: string;
  readonly accountId: string;
  /** The account's name, opened under the **account's** binding. */
  readonly accountName: NarrativeText;
  readonly currencyCode: string;
  readonly currencySymbol: string;
  readonly payeeId: string | null;
  /** The payee's name, opened under the **payee's** binding, or `null`. */
  readonly payeeName: NarrativeText | null;
  readonly categoryId: string | null;
  /** The category's name, opened under the **category's** binding, or `null`. */
  readonly categoryName: NarrativeText | null;
  readonly categoryGroupId: string | null;
  /** The group's name, opened under the **group's** binding, or `null`. */
  readonly categoryGroupName: NarrativeText | null;
}

// One foreign name: opened under the binding built from its own row id, or
// `null` when either half is missing. Both halves, or nothing — a wire value
// with no identifier has no binding this client may honestly build, and the
// identifier with no wire value has nothing to open.
function openForeignName(
  open: NarrativeOpener,
  binding: (rowId: string) => NarrativeFieldBinding,
  rowId: string | null | undefined,
  wire: string | null | undefined,
): Promise<NarrativeText> | null {
  return rowId == null || wire == null ? null : open(binding(rowId), wire);
}

// `null` stays `null` and a pending open is awaited. Written once rather than
// four times, because four copies of a conditional await is where the fifth
// one loses its null arm.
async function settleName(
  pending: Promise<NarrativeText> | null,
): Promise<NarrativeText | null> {
  return pending === null ? null : await pending;
}

/**
 * Turns one row the API sent into the row a template renders, opening each of
 * its five sealed members under the binding of the row that member belongs to.
 *
 * Rejects on whatever `open` rejects on — a binding the codec refuses, a
 * content key whose bytes can be read back out — because those are facts about
 * the call and not about the row.
 */
export async function toTransactionView(
  dto: TransactionDto,
  open: NarrativeOpener,
): Promise<TransactionView> {
  // Started together and awaited together: five independent AEAD opens, and
  // `Promise.all` rather than `allSettled` so that a `NarrativeFieldMisuseError`
  // — a defect in this client — reaches the caller instead of being filed as
  // one member that did not open.
  const [description, accountName, payeeName, categoryName, categoryGroupName] =
    await Promise.all([
      settleName(
        openForeignName(
          open,
          transactionDescriptionBinding,
          dto.id,
          dto.description,
        ),
      ),
      open(accountNameBinding(dto.accountId), dto.accountName),
      settleName(
        openForeignName(open, payeeNameBinding, dto.payeeId, dto.payeeName),
      ),
      settleName(
        openForeignName(
          open,
          categoryNameBinding,
          dto.categoryId,
          dto.categoryName,
        ),
      ),
      settleName(
        openForeignName(
          open,
          categoryGroupNameBinding,
          dto.categoryGroupId,
          dto.categoryGroupName,
        ),
      ),
    ]);

  return {
    accountId: dto.accountId,
    accountName,
    amount: dto.amount,
    categoryGroupId: dto.categoryGroupId ?? null,
    categoryGroupName,
    categoryId: dto.categoryId ?? null,
    categoryName,
    createdAtUtc: dto.createdAtUtc,
    currencyCode: dto.currencyCode,
    currencySymbol: dto.currencySymbol,
    date: dto.date,
    description,
    id: dto.id,
    payeeId: dto.payeeId ?? null,
    payeeName,
  };
}
