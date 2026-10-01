// One category as the categories screen may know it, and the transform that
// turns the row the API sent into it.
//
// **This row carries three envelopes and one of them belongs to another row.**
// `name` and `description` are the category's own; `categoryGroupName` is a
// copy of the group's name, denormalized onto this row so a list can render
// without a second request per category. Associated data is rebuilt from
// wherever a ciphertext was found rather than carried inside the envelope, so
// **the group's name is opened under the group's binding, rebuilt from
// `categoryGroupId`** — which is why that identifier crosses the wire beside
// it.
//
// **Opening it under this category's id is the defect this file exists to
// prevent, and it is silent.** The tag check fails, the value comes back
// `unreadable`, and `unreadable` is the same word a genuinely damaged column
// produces: a screen draws an em dash, no error names the cause, and nothing on
// the server can see it — it holds no key and cannot tell a value that opened
// from one that did not. The category's own id is sitting right there on the
// DTO, one member away, which is what makes the mistake available; there is no
// "close enough" here, so the mapper never reaches for a default binding.
// `transaction-view.ts` makes the same argument about four foreign names and
// this file is the second instance, not a restatement.
//
// **The two bindings that already have owners are imported rather than
// restated.** `categoryGroupNameBinding` belongs to `category-group-view.ts`; a
// second spelling of it is a second definition of the value an envelope was
// sealed against. Both category bindings live here because this is the row they
// name.
//
// **A column holding no value is `NarrativeText | null`, and the null arm is
// answered before any key is involved.** Whether a column is null is known
// before whether it opens is, and "nobody wrote a note" is not "we could not
// read this": a fourth word would file the first fact inside a union about the
// second, and the first template written against it renders a failure over a
// field somebody simply left empty.
//
// **There is deliberately no blind index over the description**, and there
// never may be one: a description is never looked up, and an index over it
// would publish a deterministic per-account fingerprint of somebody's free
// text. `category-group-view.ts` argues it once for both tables.
//
// **Nothing here collapses a word into a string** — no branch turns `locked` or
// `unreadable` into `''` or `'—'`, the rule `docs/design/components.md` states
// under "The locked account" — and **a misuse rejection propagates** rather
// than becoming a word, which is why the read path that drives this uses
// `Promise.all` and not `allSettled`.
//
// **The members are listed rather than spread.** `{ ...dto, name }` is shorter
// and would carry a fourth sealed column into a view as though it were text the
// day this row gains one, with nothing red.
import type { CategoryDto } from '@app-core/api/categories-api.service';
import type {
  BlindIndexBinding,
  BlindIndexedField,
} from '@app-core/security/blind-index';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import {
  categoryGroupNameBinding,
  type OpenedText,
} from './category-group-view';

/**
 * The one blind-indexed pair a category keys on.
 *
 * Written as a pair and looked up as one, and it carries **no row id** — the
 * deliberate inverse of {@link categoryNameBinding}: an index has to be *equal*
 * for equal names across rows, which is exactly what a per-row binding would
 * destroy. The `satisfies` ties it to the codec's list, so a pair this client
 * stopped indexing reddens here rather than at the call.
 */
export const CATEGORY_NAME_FIELD = {
  table: 'categories',
  column: 'name',
} as const satisfies BlindIndexedField;

/**
 * The binding one category's name is sealed under and opened against.
 *
 * `rowId` is the category's own identifier in the canonical spelling — the
 * value the envelope's associated data was built from.
 */
export function categoryNameBinding(rowId: string): NarrativeFieldBinding {
  return { ...CATEGORY_NAME_FIELD, rowId };
}

/**
 * The binding one category's name is keyed under for a lookup or a column.
 *
 * A budget and no row, which is the inverse of {@link categoryNameBinding} in
 * both halves: equal names across rows must key alike or the server's unique
 * index enforces nothing, and two budgets of one account must not, or an
 * operator reading both sees which words they share. `blind-index.ts` argues it.
 *
 * `budgetId` is what `GET /api/me` said, in the spelling it said it; the codec
 * refuses any other and nothing here folds one.
 */
export function categoryNameIndexBinding(budgetId: string): BlindIndexBinding {
  return { ...CATEGORY_NAME_FIELD, budgetId };
}

/**
 * The binding one category's note is sealed under and opened against.
 *
 * The same row id as {@link categoryNameBinding} and a different column, which
 * is the whole of what tells this row's two own envelopes apart. There is no
 * indexed twin of this function and there may never be one.
 */
export function categoryDescriptionBinding(
  rowId: string,
): NarrativeFieldBinding {
  return { table: 'categories', column: 'description', rowId };
}

/** One category as a template may render it: three words, not three strings. */
export interface CategoryView {
  readonly id: string;
  /** The opened name, or the reason there is none. Never `''` and never `'—'`. */
  readonly name: NarrativeText;
  /** The opened note, or `null` when nobody wrote one. Never a fourth word. */
  readonly description: NarrativeText | null;
  readonly categoryGroupId: string;
  /** The group's name, opened under the **group's** binding. */
  readonly categoryGroupName: NarrativeText;
  readonly position: number;
}

/** A category whose own words this browser can read — the shape an edit prefills from. */
export type ReadableCategory = CategoryView & {
  readonly name: OpenedText;
  readonly description: OpenedText | null;
};

/**
 * Whether this category may be renamed: both of **its own** values are
 * readable, or absent.
 *
 * `category-group-view.ts` argues the rule and the type-predicate form once for
 * both tables. What differs here is what the predicate deliberately does *not*
 * ask about: `categoryGroupName` is the **group's** column denormalized onto
 * this row, and `PUT /api/categories/{id}` binds three members of which that is
 * not one. A rename cannot touch it, so refusing on it would strand every
 * category in a group whose name is damaged, over a write that could never have
 * made things worse.
 */
export function categoryIsReadable(
  view: CategoryView,
): view is ReadableCategory {
  return (
    view.name.state === 'text' &&
    (view.description === null || view.description.state === 'text')
  );
}

/**
 * Turns one row the API sent into the row a template renders, opening each of
 * its three sealed members under the binding of the row that member belongs to.
 *
 * Rejects on whatever `open` rejects on — a binding the codec refuses, a
 * content key whose bytes can be read back out — because those are facts about
 * the call and not about the row.
 */
export async function toCategoryView(
  dto: CategoryDto,
  open: NarrativeOpener,
): Promise<CategoryView> {
  // Started together and awaited together: three independent AEAD opens, and
  // `Promise.all` rather than `allSettled` so that a
  // `NarrativeFieldMisuseError` — a defect in this client — reaches the caller
  // instead of being filed as one member that did not open. The `null` arm is
  // answered here, before any key is involved.
  const [name, description, categoryGroupName] = await Promise.all([
    open(categoryNameBinding(dto.id), dto.name),
    dto.description == null
      ? null
      : open(categoryDescriptionBinding(dto.id), dto.description),

    // `dto.categoryGroupId`, and the mistake one member away is `dto.id`. The
    // head of this file argues why that mistake is invisible everywhere.
    open(categoryGroupNameBinding(dto.categoryGroupId), dto.categoryGroupName),
  ]);

  return {
    categoryGroupId: dto.categoryGroupId,
    categoryGroupName,
    description,
    id: dto.id,
    name,
    position: dto.position,
  };
}
