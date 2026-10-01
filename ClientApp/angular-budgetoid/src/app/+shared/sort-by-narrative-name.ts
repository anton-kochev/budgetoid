// A list of rows put in name order, once for every screen that has one.
//
// **One implementation and not a copy per service, because a copy is a place a
// defect can hide from the tests of its twin.** Measured on the two copies this
// replaced: an identifier tiebreak added to the payee ordering reddened exactly
// one case, and the identical mutation to the accounts ordering reddened
// nothing at all. The accounts list was unguarded against precisely the defect
// the payee case was written to catch, for no reason anybody chose — the two
// bodies were the same three lines. One function is covered by whichever of its
// callers has the better case, which is what closed that asymmetry rather than a
// case somebody remembered to add.
//
// **The argument that split them does not survive.** It said a shared helper
// "would be reachable from a file that must not use it", meaning the categories
// service. But {@link compareNarrative} already lives in this folder and is
// already reachable from there, and so is every other export in `+shared/`;
// reachability was never the barrier and no arrangement of files can be one.
// What keeps a caller out is the paragraph below and a reviewer who has read it.
//
// **Categories and category groups may not be sorted through here, and they will
// type-check if they are tried.** `CategoryView` and `CategoryGroupView` each
// carry a `name` of the same {@link NarrativeText}, so nothing in the compiler
// refuses the call. What refuses it is that their order is not theirs to
// compute: the API owns a `position` column somebody arranged by dragging rows
// around, and `categories.service.ts` orders by the group's position, then the
// row's own, then the identifier — `sortCategories` there, with `moveGroupTo`
// echoing a move the server has already accepted. Sorted by name instead, a
// screen silently discards an arrangement a person made by hand, and the next
// read from the API puts it back — so the defect looks like the drag not
// sticking rather than like a sort. The tiebreak there is over the id and never
// over the name for the same reason this file exists at all: a name is a word,
// not a string, and may not be readable.
//
// **The copy is what makes the signature honest, and the compiler is what holds
// it.** `readonly T[]` is a promise about this function rather than a fact about
// what the caller is holding — it accepts a mutable array quite happily — so the
// parameter says *this list will not be reordered*, and a body that sorted in
// place would be saying something false with no assignment anywhere naming the
// change. **It is not defending a live defect, and saying so is better than
// inventing one**: all five call sites build their argument on the spot, out of
// a spread, a `map` or a `Promise.all`, so not one of them is still holding the
// array it hands over. The guard is the signature and nothing else — drop the
// spread, sort the parameter, and it is `TS2339`, `sort` is not a member of
// `readonly T[]`. That is why no test is spent on this and none needs to be.
//
// Nothing here is a service and nothing here is injected. A function is the
// whole of it, and the one type it imports is erased.
import type { NarrativeText } from '@app-core/security/narrative-text';

import { compareNarrative } from './compare-narrative';

/**
 * Copies `views` and returns it in the order {@link compareNarrative} puts the
 * name each row answers with.
 *
 * `nameOf` rather than a `{ name: NarrativeText }` constraint, so that a row
 * whose narrative name is spelled some other way is served by the same
 * function, and so that a caller states which of a row's words the list is
 * ordered by instead of the type picking one.
 *
 * On a locked account every comparison is `0` and the answer is the input order
 * — `compareNarrative` argues why that is a property to keep rather than a case
 * to write. Read that file for the ordering itself, for what `localeCompare`
 * costs here, and for why two opened names are compared trimmed.
 *
 * The head of this file says which lists may **not** be ordered through it.
 */
export function sortByNarrativeName<T>(
  views: readonly T[],
  nameOf: (view: T) => NarrativeText,
): T[] {
  return [...views].sort((left, right) =>
    compareNarrative(nameOf(left), nameOf(right)),
  );
}
