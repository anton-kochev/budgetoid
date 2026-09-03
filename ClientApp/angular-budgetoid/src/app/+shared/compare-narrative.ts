// How a list of narrative values is ordered once the names in it are no longer
// strings.
//
// Every screen that lists rows sorts them by a name, and every one of those
// names is about to stop being a `string`: `accounts.name`, `payees.name` and
// both of a category group's are sealed columns, so what a mapper hands a
// template is a {@link NarrativeText} — the text, or the reason there is none.
// `accounts.service.ts` sorts on `a.name.localeCompare(b.name)` today, and that
// line does not compile against the union. This is what it becomes.
//
// **Three words, and the order is `text`, then `unreadable`, then `locked`.**
// The values a person can read come first, because a list is sorted for the
// person reading it and a name they can see is the only entry the ordering can
// say anything useful about. `unreadable` follows: one damaged value in a row
// that is otherwise fine, sitting below the readable entries where it can be
// noticed without pushing anything a person wants down the page. `locked` is
// last, because it is not a fact about the row at all — it is this browser
// holding no key, and the way out of it is a factor rather than anything on the
// list.
//
// **A list mixing the three is reachable, so the order is total rather than
// nominal.** Lockedness is account-wide, so the obvious reading is that a
// screen sees either all text or all `locked` and never a mixture. Custody can
// move while a page's reads are in flight — a `lock()` between the first row's
// open and the last one's, which is exactly what the read side's generation
// check publishes — and the list that renders then holds both words. Every pair
// therefore has an answer.
//
// **On a locked account every comparison is `0`, and a stable sort leaves the
// rows in the order they arrived.** That is worth reading twice because it is a
// property to *keep*, not a case to write: `Array.prototype.sort` is stable by
// specification, so a comparator answering `0` to every pair is the same list
// back. A screen therefore shows whatever order the API sent, which is a real
// order — insertion, or a position column — rather than a shuffle. There is no
// branch below for it, and adding one would be the way to lose it.
//
// **`localeCompare` reads the host locale, and nothing in this app provides
// `LOCALE_ID`.** So the collation two opened names are compared under is
// whatever the browser is set to, and two people looking at one budget can see
// its accounts in two orders. Written down rather than fixed: the alternative
// is a locale this module names for itself, which would be a second opinion
// about a fact the browser already holds, and the sort is a presentation
// decision with nothing stored under it. It is the same reason
// `credential-registration-date.ts` formats a day the way it does. What must
// *not* happen is a retreat to `<` or `>` on the strings: those compare UTF-16
// code units, which puts every capitalised name above every lower-case one and
// reads as two alphabets stacked on top of each other.
//
// Nothing here is a service and nothing here is injected: no state, no
// dependency, no framework — the one import is a type, and it is erased. A
// function is the whole of it.
import type { NarrativeText } from '@app-core/security/narrative-text';

// The three words as positions, written out rather than derived from the order
// of the union's members — a union is a set and the order its arms happen to be
// declared in is not a fact anything may read.
//
// `satisfies` is what ties it to the union: a fourth word added to
// {@link NarrativeText} leaves this object missing a key and reddens here,
// where a `Record<string, number>` annotation would let it through and sort the
// new word to `undefined` — every comparison against it `NaN`, and a sort that
// silently keeps its input. `as const` is what keeps the values literal through
// the check.
const NARRATIVE_ORDER = {
  text: 0,
  unreadable: 1,
  locked: 2,
} as const satisfies Record<NarrativeText['state'], number>;

/**
 * Orders two narrative values: `text` before `unreadable` before `locked`, and
 * two opened names by `localeCompare`.
 *
 * Written to be handed straight to `Array.prototype.sort`, which is where the
 * two properties worth stating come from. It is **antisymmetric** — every pair
 * answered one way answers the other way round with the opposite sign — and it
 * answers `0` for two values of one word other than `text`, which on a locked
 * account is every pair on the screen. A stable sort over all-zero comparisons
 * is the input order, so a locked list keeps whatever order the row arrived in
 * instead of being shuffled into one nobody chose.
 *
 * The head of this file argues the ordering, and says what `localeCompare`
 * costs here and why it is not paid off by naming a locale.
 */
export function compareNarrative(
  left: NarrativeText,
  right: NarrativeText,
): number {
  // The one pair with something to compare beyond its word. Asked first, so the
  // rank subtraction below is left with the cases where the two words differ —
  // and with the equal-word cases it answers `0` to, which is the branch a
  // locked account takes and the reason there is no branch for it.
  if (left.state === 'text' && right.state === 'text') {
    return left.value.localeCompare(right.value);
  }

  return NARRATIVE_ORDER[left.state] - NARRATIVE_ORDER[right.state];
}
