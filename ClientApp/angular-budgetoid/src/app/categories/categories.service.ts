// The categories screen's state, and the third service in this product that
// seals what it writes and opens what it reads. It is the last one: after this,
// no screen in the client sends plaintext.
//
// **The service mints, seals and indexes; the component never does.** A screen
// hands over the text somebody typed and nothing else. Put the other way round,
// a component holding a row id it minted has to keep it alive across a form
// reset and a cancel, and that id is the associated data **both** envelopes on
// the row are sealed against — so the one place it can be dropped is the one
// place it must not be.
//
// **An update re-seals under the existing row id and mints none.** Re-minting
// is one line, it reddens nothing without a test, and what it produces is a
// name *and a note* that never open again: both envelopes are bound to an
// identifier the row does not have, permanently, with no error anywhere naming
// the cause. The id on a read is a spelling this client's canonical check
// accepts — `Guid` is rendered lower-case hyphenated — so sealing against what
// the API sent back is safe.
//
// **Both halves of a name go, or neither does — and the note joins them.**
// `sealField` and `blindIndex` do not answer alike when custody moves
// mid-operation: a seal compares key **identity** and keeps its answer through
// a plain `lock()`, an index compares the **generation counter** and drops it.
// So `sealed` beside `locked` is a reachable pair, and posting it writes a name
// whose column and whose index disagree — through the one door the server
// cannot see, because it holds no index key and can never recompute one. The
// note is refused on the same terms and for a different reason: a group whose
// name arrived and whose note did not is a **legal row**, `NULL` is how the
// column says "nobody wrote one", and the write, the response and every later
// read would all agree with each other and be wrong.
//
// **An empty note posts `null` and seals nothing, and a whitespace-only one is
// a note.** `''` is not a legal envelope and answers 400, so `''` is how this
// screen says "no note". `'   '` is not `''`: the client may not alter what it
// seals, and the `normalizeDescription` that used to fold whitespace onto
// `null` is gone. Folded, somebody clears a note by typing spaces into it and
// the row goes on saying nobody ever wrote one. **The consequence is worth
// stating rather than discovering**: the column distinguishes a note somebody
// cleared (a twenty-nine-byte envelope over `''`) from one nobody ever filed
// (`NULL`), and **this client has no path that produces the first** — so the
// two are one thing from this browser while the server keeps them as two rows.
// That is a gap rather than a bug: nothing here writes the wrong value, there
// is a value it cannot write. Do not close it by sealing `''`; that is a 400.
//
// **Nothing is trimmed anywhere on this path.** A `.trim()` here would seal one
// text while the index was taken over another, and the row would key perfectly
// to a value nothing looks up. Folding is the index codec's own job and it does
// it inside, over the same string. The non-blank rule the old `.trim()` was
// accidentally enforcing moved to the form, where refusing is all it does.
//
// **The read is `switchMap` and never `mergeMap`, and that is a behaviour
// change rather than a style.** Decryption widens the overlap between two loads
// from two round trips to two round trips plus two AEAD opens per group and
// three per category, so a slow first load can finish after a fast second and
// silently revert both lists to rows the person has already replaced.
//
// **Both lists are `View[] | null` and clear to `null` when a load starts** —
// `docs/design/components.md`, "A value read from the network". `null` and
// never `[]`: an empty array is the sentence *you have no categories*, which is
// a claim only a server that answered may make. A load that failed leaves them
// `null` rather than restoring the previous answer, because a reader cannot
// tell a kept answer from a fresh one.
//
// **The two lists are published together or not at all.** A category carries
// its group's name and its group's position decides where it sorts, so a
// half-published pair is a list of categories filed under groups this screen
// has not got. One `forkJoin`, one outcome, one `set` of each.
//
// **Both lists are dropped when the account locks, and this service is where
// that rule lives rather than in whoever ended the session.**
// `accounts.service.ts` argues the whole of it: `providedIn: 'root'` means no
// injector destroys this object and no navigation clears it, so an opened list
// outlives `custody.lock()`, `SessionService.ended()` and every route change;
// `SessionService` may not reach for three feature services from `+core`; and
// the word is `locked` exactly, because `unlocking` resolves back into keys and
// the screen deliberately keeps the hierarchy up through a ceremony. What is
// this file's own is that the two lists go **together**, for the reason they
// are published together: a category carries its group's name, so a half-clear
// leaves the words on screen that the clear exists to destroy.
//
// **`openField` is handed to the mappers as an arrow and never as a bare method
// reference.** It reads a `#` field, so `this.#custody.openField` on its own
// type-checks perfectly and answers every call with a `TypeError` on the wrong
// receiver. `account-view.ts` argues it at greater length; this is a third call
// site the argument is about.
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import type { BlindIndexedField } from '@app-core/security/blind-index';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import { mintNarrativeRowId } from '@app-core/security/narrative-row-id';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import {
  EMPTY,
  Observable,
  Subject,
  catchError,
  finalize,
  forkJoin,
  from,
  map,
  of,
  switchMap,
  tap,
} from 'rxjs';
import {
  CATEGORY_GROUP_NAME_FIELD,
  categoryGroupDescriptionBinding,
  categoryGroupNameBinding,
  toCategoryGroupView,
  type CategoryGroupView,
} from './category-group-view';
import {
  CATEGORY_NAME_FIELD,
  categoryDescriptionBinding,
  categoryNameBinding,
  toCategoryView,
  type CategoryView,
} from './category-view';

/**
 * The text a screen hands over for one narrative row on this screen: a name and
 * a note, both exactly as typed and neither of them sealed.
 *
 * One type for a group and a category, because they hand over the same two
 * pieces of text — and two names for one shape would be interchangeable anyway
 * under structural typing, so the second name would buy nothing but the
 * impression of a distinction.
 */
export interface CategoryText {
  readonly name: string;
  /**
   * What was typed. `''` is a row filing no note, never an envelope over `''`;
   * `'   '` is a note of three spaces and is sealed as typed. The head of this
   * file argues both.
   */
  readonly description: string;
}

/** The text for a new category, and the group it joins. */
export interface NewCategory extends CategoryText {
  readonly categoryGroupId: string;
}

// How a load ended, as a word rather than as the absence of a value. A failure
// that emitted nothing would leave the outer subscription unable to clear the
// loading line, and inferring "it failed" from "no value arrived" is the one
// predicate that covers three different states.
type LoadOutcome =
  | {
      readonly state: 'loaded';
      readonly groups: readonly CategoryGroupView[];
      readonly categories: readonly CategoryView[];
    }
  | { readonly state: 'failed' };

// One narrative row on its way to its columns: the name pair and the note, or
// nothing at all. There is no half of this value.
interface SealedRow {
  readonly name: string;
  readonly nameKey: string;
  readonly description: string | null;
}

// What a screen just typed, as the word a list holds. `''` becomes `null`
// because that is what went on the wire — patching it to an empty word would
// render an empty element where the next read renders nothing at all.
function typedNote(description: string): NarrativeText | null {
  return description === '' ? null : { state: 'text', value: description };
}

// Groups in their own order, renumbered from zero. The API owns `position`, so
// this is the local echo of a move the server has already accepted.
function moveGroupTo(
  groups: readonly CategoryGroupView[],
  id: string,
  position: number,
): readonly CategoryGroupView[] {
  const ordered = [...groups].sort(
    (left, right) => left.position - right.position,
  );
  const currentIndex = ordered.findIndex((group) => group.id === id);

  if (currentIndex < 0) {
    return groups;
  }

  const [moved] = ordered.splice(currentIndex, 1);

  if (moved === undefined) {
    return groups;
  }

  ordered.splice(position, 0, moved);

  return ordered.map((group, groupPosition) => ({
    ...group,
    position: groupPosition,
  }));
}

// The one empty list every group holding no categories is answered with.
//
// A shared constant rather than a `[]` at the call site, and that is the half a
// reader will leave out: the lookup below is a `computed`, so a group that
// *has* rows gets one array for as long as the list stands — and a group that
// has none would get a fresh one on every call, which on this screen is every
// change-detection tick. That is the commoner case on a budget somebody has
// just started filling in, and it feeds `[cdkDropListData]` exactly as the full
// case does. Frozen because it is handed out to callers who have no business
// mutating it, and because a `push` onto it would file a category under every
// empty group at once.
const NO_CATEGORIES: readonly CategoryView[] = Object.freeze([]);

// Categories by their group's position, then by their own, then by identifier.
// The last tiebreak is what stops two rows sharing a position from swapping
// places between renders; it is over the id and never over the name, which is a
// word rather than a string and may not be readable at all.
function sortCategories(
  categories: readonly CategoryView[],
  groups: readonly CategoryGroupView[],
): readonly CategoryView[] {
  const groupPositions = new Map(
    groups.map((group) => [group.id, group.position]),
  );

  return [...categories].sort(
    (left, right) =>
      (groupPositions.get(left.categoryGroupId) ?? 0) -
        (groupPositions.get(right.categoryGroupId) ?? 0) ||
      left.position - right.position ||
      left.id.localeCompare(right.id),
  );
}

@Injectable({ providedIn: 'root' })
export class CategoriesService {
  readonly #groupsApi = inject(CategoryGroupsApiService);
  readonly #categoriesApi = inject(CategoriesApiService);
  readonly #custody = inject(AccountKeyCustodyService);
  readonly #groups = signal<readonly CategoryGroupView[] | null>(null);
  readonly #categories = signal<readonly CategoryView[] | null>(null);
  readonly #loading = signal(false);
  readonly #failed = signal(false);
  readonly #loads = new Subject<void>();

  // The arrow the head of this file argues for. Never `this.#custody.openField`.
  readonly #open: NarrativeOpener = (binding, wire) =>
    this.#custody.openField(binding, wire);

  // Every category filed under the group it names, in the order the published
  // list holds them. {@link categoriesForGroup} is the only reader and argues
  // why this is a `computed` rather than a filter at the call site.
  //
  // Written out rather than `Map.groupBy`: this project targets ES2022 and that
  // is ES2024, so the shorter spelling does not type-check here. A group with
  // no rows is deliberately **absent** from the map rather than present with an
  // empty array — the lookup answers `NO_CATEGORIES` for a miss, so one frozen
  // array serves every empty group instead of one being built per group per
  // load.
  readonly #byGroup = computed<ReadonlyMap<string, readonly CategoryView[]>>(
    () => {
      const grouped = new Map<string, CategoryView[]>();

      for (const category of this.#categories() ?? []) {
        const filed = grouped.get(category.categoryGroupId);

        if (filed === undefined) {
          grouped.set(category.categoryGroupId, [category]);
        } else {
          filed.push(category);
        }
      }

      return grouped;
    },
  );

  public readonly groups = this.#groups.asReadonly();
  public readonly categories = this.#categories.asReadonly();
  public readonly loading = this.#loading.asReadonly();
  /**
   * Whether the last read of the hierarchy came back a failure.
   *
   * A fourth state the screen needs and could not infer: both lists are `null`
   * at rest, in flight **and** after a failure, so a screen reading a list and
   * the running flag alone renders nothing at all over a read that failed — no
   * sentence, and no way for a person to tell that from an account with nothing
   * in it. One flag for the pair, because the pair is read, published and
   * cleared together.
   */
  public readonly failed = this.#failed.asReadonly();

  constructor() {
    this.#loads
      .pipe(
        switchMap(() =>
          forkJoin({
            groups: this.#groupsApi.getCategoryGroups(),
            categories: this.#categoriesApi.getCategories(),
          }).pipe(
            switchMap((response) =>
              from(
                // `Promise.all` and never `allSettled`, at both levels: a
                // `NarrativeFieldMisuseError` is a defect in this client and
                // has to reach the failure branch, not be filed as one member
                // that did not open.
                Promise.all([
                  Promise.all(
                    response.groups.items.map((dto) =>
                      toCategoryGroupView(dto, this.#open),
                    ),
                  ),
                  Promise.all(
                    response.categories.items.map((dto) =>
                      toCategoryView(dto, this.#open),
                    ),
                  ),
                ]),
              ),
            ),
            map(
              ([groups, categories]): LoadOutcome => ({
                categories,
                groups,
                state: 'loaded',
              }),
            ),
            catchError((error: unknown): Observable<LoadOutcome> => {
              this.#report(error);

              return of({ state: 'failed' });
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.#loading.set(false);
        this.#failed.set(outcome.state === 'failed');

        if (outcome.state === 'loaded') {
          // Published in the order the server sent, which is an order: both
          // rows carry a `position` column the person set by dragging.
          this.#groups.set(outcome.groups);
          this.#categories.set(outcome.categories);
        }
      });

    // The one reader of custody's status in this file; the head of the file and
    // `accounts.service.ts` argue why the reaction lives here and why the word
    // is `locked` exactly. It runs once on construction and clears two lists
    // that are `null` until something loads one, so the first run is a no-op
    // whatever the injection order was.
    effect(() => {
      if (this.#custody.status() === 'locked') {
        this.#groups.set(null);
        this.#categories.set(null);

        // Cleared beside the lists, for the reason `accounts.service.ts`
        // writes out at its own copy of this line: the word is a claim about
        // the last read, and after these two lines there is no read left for
        // it to be a claim about.
        this.#failed.set(false);
      }
    });
  }

  public load(): void {
    // Set before the subject is pushed, so that a screen reading these
    // synchronously after `load()` sees the state of the load it just started.
    this.#loading.set(true);
    this.#failed.set(false);
    this.#groups.set(null);
    this.#categories.set(null);
    this.#loads.next();
  }

  public async addGroup(group: CategoryText): Promise<void> {
    // Minted here and used three times — as the binding each of the two
    // envelopes is sealed against, and as the `id` on the wire. All three must
    // be the same value, which is why there is one `const`.
    const id = mintNarrativeRowId();
    const sealed = await this.#sealRow(
      categoryGroupNameBinding(id),
      CATEGORY_GROUP_NAME_FIELD,
      categoryGroupDescriptionBinding(id),
      group,
    );

    if (sealed === null) {
      return;
    }

    this.#loading.set(true);
    this.#groupsApi
      .createCategoryGroup({
        description: sealed.description,
        id,
        name: sealed.name,
        nameKey: sealed.nameKey,
      })
      .pipe(
        switchMap((created) => from(toCategoryGroupView(created, this.#open))),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe((view) => {
        this.#groups.update((groups) =>
          // A `null` list is "no answer yet", and appending to it would
          // fabricate a list of one over a read that never landed. Appended
          // rather than sorted: the server computed the position and appended
          // too.
          groups === null ? groups : [...groups, view],
        );
      });
  }

  public async updateGroup(id: string, group: CategoryText): Promise<void> {
    // The row's **existing** identifier. Nothing is minted on this path.
    const sealed = await this.#sealRow(
      categoryGroupNameBinding(id),
      CATEGORY_GROUP_NAME_FIELD,
      categoryGroupDescriptionBinding(id),
      group,
    );

    if (sealed === null) {
      return;
    }

    this.#loading.set(true);
    this.#groupsApi
      .updateCategoryGroup(id, {
        description: sealed.description,
        name: sealed.name,
        nameKey: sealed.nameKey,
      })
      .pipe(
        // The route answers 204, so the rows are patched from what was just
        // sealed. That is honest rather than optimistic: this browser sealed
        // the text under a key it holds, so the value it would read back is the
        // text it sent. The group's name is denormalized onto every category
        // filed under it, so both lists move together.
        tap(() => {
          const name: NarrativeText = { state: 'text', value: group.name };

          this.#groups.update((groups) =>
            groups === null
              ? groups
              : groups.map((view) =>
                  view.id === id
                    ? {
                        ...view,
                        description: typedNote(group.description),
                        name,
                      }
                    : view,
                ),
          );
          this.#categories.update((categories) =>
            categories === null
              ? categories
              : categories.map((view) =>
                  view.categoryGroupId === id
                    ? { ...view, categoryGroupName: name }
                    : view,
                ),
          );
        }),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public moveGroup(id: string, position: number): void {
    this.#loading.set(true);
    this.#groupsApi
      .moveCategoryGroup(id, { position })
      .pipe(
        tap(() => {
          const groups = this.#groups();

          if (groups === null) {
            return;
          }

          const reordered = moveGroupTo(groups, id, position);

          this.#groups.set(reordered);
          this.#categories.update((categories) =>
            categories === null
              ? categories
              : sortCategories(categories, reordered),
          );
        }),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public removeGroup(id: string): void {
    this.#loading.set(true);
    this.#groupsApi
      .deleteCategoryGroup(id)
      .pipe(
        tap(() =>
          this.#groups.update((groups) =>
            groups === null
              ? groups
              : groups
                  .filter((group) => group.id !== id)
                  .map((group, position) => ({ ...group, position })),
          ),
        ),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public async addCategory(category: NewCategory): Promise<void> {
    const id = mintNarrativeRowId();
    const sealed = await this.#sealRow(
      categoryNameBinding(id),
      CATEGORY_NAME_FIELD,
      categoryDescriptionBinding(id),
      category,
    );

    if (sealed === null) {
      return;
    }

    this.#loading.set(true);
    this.#categoriesApi
      .createCategory({
        categoryGroupId: category.categoryGroupId,
        description: sealed.description,
        id,
        name: sealed.name,
        nameKey: sealed.nameKey,
      })
      .pipe(
        // The 201 carries the group's sealed name, so the row that lands in the
        // list has been through the mapper rather than assembled here from what
        // was typed — which is the only way it can carry a group name this
        // screen has not been told.
        switchMap((created) => from(toCategoryView(created, this.#open))),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe((view) => {
        this.#categories.update((categories) =>
          categories === null
            ? categories
            : sortCategories([...categories, view], this.#groups() ?? []),
        );
      });
  }

  public async updateCategory(
    id: string,
    category: CategoryText,
  ): Promise<void> {
    // The row's **existing** identifier. Nothing is minted on this path.
    const sealed = await this.#sealRow(
      categoryNameBinding(id),
      CATEGORY_NAME_FIELD,
      categoryDescriptionBinding(id),
      category,
    );

    if (sealed === null) {
      return;
    }

    this.#loading.set(true);
    this.#categoriesApi
      .updateCategory(id, {
        description: sealed.description,
        name: sealed.name,
        nameKey: sealed.nameKey,
      })
      .pipe(
        tap(() =>
          this.#categories.update((categories) =>
            categories === null
              ? categories
              : categories.map((view) =>
                  view.id === id
                    ? {
                        ...view,
                        description: typedNote(category.description),
                        name: { state: 'text', value: category.name },
                      }
                    : view,
                ),
          ),
        ),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public placeCategory(
    id: string,
    categoryGroupId: string,
    position: number,
  ): void {
    this.#loading.set(true);
    this.#categoriesApi
      .placeCategory(id, { categoryGroupId, position })
      .pipe(
        tap(() => this.#place(id, categoryGroupId, position)),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  public removeCategory(id: string): void {
    this.#loading.set(true);
    this.#categoriesApi
      .deleteCategory(id)
      .pipe(
        tap(() => {
          const removed = this.#categories()?.find(
            (category) => category.id === id,
          );

          this.#categories.update((categories) => {
            if (categories === null) {
              return categories;
            }

            const remaining = categories.filter(
              (category) => category.id !== id,
            );

            if (removed === undefined) {
              return remaining;
            }

            let position = 0;

            return remaining.map((category) =>
              category.categoryGroupId === removed.categoryGroupId
                ? { ...category, position: position++ }
                : category,
            );
          });
        }),
        catchError((error: unknown) => this.#swallow(error)),
        finalize(() => this.#loading.set(false)),
      )
      .subscribe();
  }

  /**
   * The categories filed under one group, in their own order.
   *
   * `[]` for a list with no answer yet, which is not the same claim as `null`
   * on the list itself: this answers *which of the rows I hold belong to that
   * group*, and while there are no rows the answer is honestly none. The screen
   * reads {@link groups} for whether there is an answer at all.
   *
   * **The grouping is computed once per change to the list, not once per
   * call.** The template asks this **twice per group** — once for the rows and
   * once for `[cdkDropListData]` — and a template call runs on every
   * change-detection tick in a zone-based app, so the `.filter()` this used to
   * be handed the drop list a new array identity on every tick and re-allocated
   * every group's rows with it. The answer is stable for as long as the
   * underlying list is, which is what a template is entitled to assume of
   * something it is allowed to call.
   *
   * It stays a method rather than becoming a map the template reads, because a
   * template writing `byGroup().get(id) ?? []` puts the allocation back on the
   * empty branch — and this is a shape the screen, the spec and the docs
   * already name.
   */
  public categoriesForGroup(categoryGroupId: string): readonly CategoryView[] {
    return this.#byGroup().get(categoryGroupId) ?? NO_CATEGORIES;
  }

  // The local echo of a placement the server has already accepted. Split out of
  // the pipe because it is the one piece of arithmetic on this screen, and it
  // renumbers **both** groups when a category crosses between them.
  #place(id: string, categoryGroupId: string, position: number): void {
    const categories = this.#categories();
    const groups = this.#groups();

    if (categories === null || groups === null) {
      return;
    }

    const moved = categories.find((category) => category.id === id);
    const destinationGroup = groups.find(
      (group) => group.id === categoryGroupId,
    );

    if (moved === undefined || destinationGroup === undefined) {
      return;
    }

    if (moved.categoryGroupId === categoryGroupId) {
      const reordered = categories
        .filter(
          (category) =>
            category.categoryGroupId === categoryGroupId && category.id !== id,
        )
        .sort((left, right) => left.position - right.position);

      reordered.splice(position, 0, moved);

      const normalized = reordered.map((category, destinationPosition) => ({
        ...category,
        position: destinationPosition,
      }));
      const otherGroups = categories.filter(
        (category) => category.categoryGroupId !== categoryGroupId,
      );

      this.#categories.set(
        sortCategories([...otherGroups, ...normalized], groups),
      );

      return;
    }

    const source = categories
      .filter(
        (category) =>
          category.categoryGroupId === moved.categoryGroupId &&
          category.id !== id,
      )
      .sort((left, right) => left.position - right.position)
      .map((category, sourcePosition) => ({
        ...category,
        position: sourcePosition,
      }));
    const destination = categories
      .filter(
        (category) =>
          category.categoryGroupId === categoryGroupId && category.id !== id,
      )
      .sort((left, right) => left.position - right.position);

    destination.splice(position, 0, moved);

    // The destination group's **opened** name travels with every row that
    // lands in it, because that is what the next read would hand back — the
    // column is denormalized on the server too.
    const normalizedDestination = destination.map(
      (category, destinationPosition) => ({
        ...category,
        categoryGroupId,
        categoryGroupName: destinationGroup.name,
        position: destinationPosition,
      }),
    );
    const unaffected = categories.filter(
      (category) =>
        category.id !== id &&
        category.categoryGroupId !== moved.categoryGroupId &&
        category.categoryGroupId !== categoryGroupId,
    );

    this.#categories.set(
      sortCategories(
        [...unaffected, ...source, ...normalizedDestination],
        groups,
      ),
    );
  }

  // Both halves of the name, and the note beside them, or nothing at all. The
  // order is seal, index, seal — and a locked answer returns before the next
  // call, because a browser holding no content key holds no index key either,
  // so every later call would answer `locked` too and buy nothing but a round
  // of work.
  async #sealRow(
    nameBinding: NarrativeFieldBinding,
    nameField: BlindIndexedField,
    descriptionBinding: NarrativeFieldBinding,
    text: CategoryText,
  ): Promise<SealedRow | null> {
    const sealedName = await this.#custody.sealField(nameBinding, text.name);

    if (sealedName.state === 'locked') {
      return null;
    }

    // The **same text** the seal ran over, never a trimmed or folded copy of
    // it. Folding is the index codec's own job and it does it inside.
    const indexed = await this.#custody.blindIndex(nameField, text.name);

    if (indexed.state === 'locked') {
      return null;
    }

    // `''` exactly, and never `.trim()`: a note of spaces is a note somebody
    // typed, and the client may not alter what it seals. The form is where a
    // blank one is refused, because refusing is all a validator does.
    if (text.description === '') {
      return {
        description: null,
        name: sealedName.wire,
        nameKey: indexed.value,
      };
    }

    const sealedNote = await this.#custody.sealField(
      descriptionBinding,
      text.description,
    );

    if (sealedNote.state === 'locked') {
      return null;
    }

    return {
      description: sealedNote.wire,
      name: sealedName.wire,
      nameKey: indexed.value,
    };
  }

  // TODO: surface API errors to the user (e.g. a snackbar) once the app has an
  // error-notification convention. For now the error is swallowed so it does
  // not become an unhandled rejection; `loading` is reset by each pipe's
  // finalize.
  #swallow(error: unknown): typeof EMPTY {
    this.#report(error);

    return EMPTY;
  }

  #report(error: unknown): void {
    console.error('Categories API request failed', error);
  }
}
