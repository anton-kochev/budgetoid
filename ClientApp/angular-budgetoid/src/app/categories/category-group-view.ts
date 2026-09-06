// One category group as the categories screen may know it, and the transform
// that turns the row the API sent into it.
//
// **This row carries two envelopes bound to one identifier, and the pair is
// what tells them apart.** `category_groups.name` and
// `category_groups.description` are both sealed under this group's own row id,
// so the binding that distinguishes them is the *column* — and the codec looks
// a binding up as a pair, never as two membership tests. A mapper that reached
// for the name's binding to open the note gets an authentication failure rather
// than garbage: the tag check fails, the value comes back `unreadable`, and
// `unreadable` is the same word a genuinely damaged column produces. Nothing on
// the server can see it either; it holds no key and cannot tell a value that
// opened from one that did not.
//
// **`category_groups.description` is the first *free-text* narrative column in
// the product and it carries a rule no name column needed: it is nullable, and
// `null` is not a fourth word.** Whether the column held anything is known
// *before* any key is involved, so this mapper answers that question first and
// asks the opener nothing about a column that was empty. `null` on the outside
// is what keeps "nobody wrote a note" apart from "a note is stored and these
// bytes did not authenticate" — two facts, two next steps for a person, and the
// first template written against a folded union renders a failure over a field
// somebody simply left blank. `narrative-text.ts` argues the union; this is the
// first mapper the ordering is a description of rather than a rule for.
//
// **There is deliberately no blind index over the description, and there never
// may be one.** A description is never looked up, and an index over it would
// publish a deterministic per-account fingerprint of somebody's free text.
// `BLIND_INDEXED_FIELDS` lists four pairs and this is not one of them, so a
// call would be refused — but the reason is worth stating here, where somebody
// adding a "search your notes" feature will look first.
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
// reproduce — and none of it is a claim about what is stored in either column.
// There is therefore no `try` in this file, and the read path that drives it
// uses `Promise.all` and not `allSettled`, which would swallow the same
// rejection one layer up.
//
// **The capability arrives as a function and never as the service that owns
// one.** `NarrativeOpener` is exactly one power; `AccountKeyCustodyService` is
// that power beside `unlock`, `lock` and `adopt`. Handed the service, a
// row-shaped transform could end a session on its way past and would need a
// `TestBed` to be exercised at all. `account-view.ts` was the first caller and
// argues it at greater length.
//
// **The members are listed rather than spread.** `{ ...dto, name }` is shorter
// and carries the day's DTO through for free — and the day this row gains a
// third sealed column it would carry that column's ciphertext into a view as
// though it were text, with nothing red.
import type { CategoryGroupDto } from '@app-core/api/category-groups-api.service';
import type {
  BlindIndexBinding,
  BlindIndexedField,
} from '@app-core/security/blind-index';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';

/**
 * The one blind-indexed pair a category group keys on.
 *
 * Written as a pair and looked up as one: `category_groups` is a table and
 * `name` is a column and neither is a membership test the other can stand in
 * for. The `satisfies` is what ties it to the codec's list — a pair this client
 * stopped indexing reddens here rather than at the call.
 *
 * It carries **no row id**, the deliberate inverse of
 * {@link categoryGroupNameBinding}: an index has to be *equal* for equal names
 * across rows, which is exactly what a per-row binding would destroy.
 */
export const CATEGORY_GROUP_NAME_FIELD = {
  table: 'category_groups',
  column: 'name',
} as const satisfies BlindIndexedField;

/**
 * The binding one category group's name is sealed under and opened against.
 *
 * `rowId` is the group's own identifier in the canonical spelling — the value
 * the envelope's associated data was built from. A disagreement stops the field
 * opening, permanently, with no error naming the cause, which is why every
 * caller passes an id it read off a row rather than one it made.
 *
 * On a category this is the **group's** id and never the category's;
 * `category-view.ts` is where that mistake is available and where it is argued.
 */
export function categoryGroupNameBinding(rowId: string): NarrativeFieldBinding {
  return { ...CATEGORY_GROUP_NAME_FIELD, rowId };
}

/**
 * The binding one group's name is keyed under for a lookup or a column.
 *
 * A budget and no row, which is the inverse of {@link categoryGroupNameBinding}
 * in both halves: equal names across rows must key alike or the server's unique
 * index enforces nothing, and two budgets of one account must not, or an
 * operator reading both sees which words they share. `blind-index.ts` argues it.
 *
 * `budgetId` is what `GET /api/me` said, in the spelling it said it; the codec
 * refuses any other and nothing here folds one.
 */
export function categoryGroupNameIndexBinding(
  budgetId: string,
): BlindIndexBinding {
  return { ...CATEGORY_GROUP_NAME_FIELD, budgetId };
}

/**
 * The binding one category group's note is sealed under and opened against.
 *
 * The same row id as {@link categoryGroupNameBinding} and a different column,
 * which is the whole of what tells the two envelopes on this row apart. There
 * is no indexed twin of this function, and the head of the file says why there
 * may never be one.
 */
export function categoryGroupDescriptionBinding(
  rowId: string,
): NarrativeFieldBinding {
  return { table: 'category_groups', column: 'description', rowId };
}

/** One category group as a template may render it: two words, not two strings. */
export interface CategoryGroupView {
  readonly id: string;
  /** The opened name, or the reason there is none. Never `''` and never `'—'`. */
  readonly name: NarrativeText;
  /** The opened note, or `null` when nobody wrote one. Never a fourth word. */
  readonly description: NarrativeText | null;
  readonly position: number;
}

/**
 * One narrative value that opened — the only arm of {@link NarrativeText} that
 * carries text.
 *
 * Written as an `Extract` rather than restated as an object literal, so the day
 * `text` gains a member this alias follows instead of silently describing an
 * older shape. `category-view.ts` imports it rather than declaring a second.
 */
export type OpenedText = Extract<NarrativeText, { state: 'text' }>;

/** A group whose own words this browser can read — the shape an edit prefills from. */
export type ReadableCategoryGroup = CategoryGroupView & {
  readonly name: OpenedText;
  readonly description: OpenedText | null;
};

/**
 * Whether this group may be renamed: both of its own values are readable, or
 * absent.
 *
 * `docs/design/components.md`, "A name that cannot be read cannot be renamed",
 * is the rule, and this row has a half the accounts screen never had. `PUT
 * /api/category-groups/{id}` carries the note **beside** the name, so an edit
 * started over a note that did not open prefills that field empty and the save
 * posts `null` — clearing a note still sitting in the column, over a name that
 * rendered perfectly. Every symptom of that is invisible: a 204, a legal row,
 * and every later read agreeing the person never wrote one. Deleting the row
 * stays available, because removing is not rewriting.
 *
 * **A `null` description admits rather than refuses.** An empty column is not a
 * value that failed to open, and refusing it would make every group without a
 * note permanently unrenameable.
 *
 * **It is a *type* predicate, and that is what holds the handler's half of the
 * gate.** On the accounts screen the compiler held that rule for free — reading
 * `.value` off a `NarrativeText` does not type-check until the state is
 * narrowed — and a screen with a second, nullable column loses it the moment
 * somebody writes `description?.state === 'text' ? … : ''` inline. Narrowing
 * here puts it back: a handler that drops this guard does not compile.
 */
export function categoryGroupIsReadable(
  view: CategoryGroupView,
): view is ReadableCategoryGroup {
  return (
    view.name.state === 'text' &&
    (view.description === null || view.description.state === 'text')
  );
}

/**
 * Turns one row the API sent into the row a template renders, opening both of
 * its sealed members under this group's own identifier.
 *
 * Rejects on whatever `open` rejects on — a binding the codec refuses, a
 * content key whose bytes can be read back out — because those are facts about
 * the call and not about the row. The head of this file argues why that stays a
 * rejection.
 */
export async function toCategoryGroupView(
  dto: CategoryGroupDto,
  open: NarrativeOpener,
): Promise<CategoryGroupView> {
  // Started together and awaited together, and `Promise.all` rather than
  // `allSettled` so that a `NarrativeFieldMisuseError` — a defect in this
  // client — reaches the caller instead of being filed as one member that did
  // not open. The `null` arm is answered here, before any key is involved: an
  // empty column is not a value that failed to open, so nothing is asked about
  // it.
  const [name, description] = await Promise.all([
    open(categoryGroupNameBinding(dto.id), dto.name),
    dto.description == null
      ? null
      : open(categoryGroupDescriptionBinding(dto.id), dto.description),
  ]);

  return {
    description,
    id: dto.id,
    name,
    position: dto.position,
  };
}
