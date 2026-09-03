// The category mapper's own spec, standing the mapper up with two lines and no
// `TestBed`.
//
// **The case this file exists for is the group's name opened under the
// category's identifier.** The row carries three envelopes and one of them
// belongs to another row; opening it under this category's id fails the tag
// check and answers `unreadable`, which is the same word a genuinely damaged
// column produces. Nothing anywhere names the cause, so a case comparing the
// whole binding is the only thing that can see it. The opener below therefore
// answers per binding rather than per call, so a wrong pair produces
// `unreadable` exactly as a real codec would.
import type { CategoryDto } from '@app-core/api/categories-api.service';
import { NarrativeFieldMisuseError } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import {
  categoryIsReadable,
  toCategoryView,
  type CategoryView,
} from './category-view';

// Two canonical lower-case hyphenated UUIDs, and they are different values on
// purpose: every claim in this file about *which* id a member was opened under
// is vacuous the moment the two agree.
const ROW_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e';
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6f';

const sealedCategory: CategoryDto = {
  id: ROW_ID,
  name: 'AQIDBAUGBwgJCgsMDQ4PEA',
  description: 'AgMEBQYHCAkKCwwNDg8QEQ',
  categoryGroupId: GROUP_ID,
  categoryGroupName: 'AwQFBgcICQoLDA0ODxAREg',
  position: 4,
};

// Answers per binding, so a case can say *this member was opened under that
// pair and that row id*. An unexpected binding answers `unreadable`, which is
// what a real codec does with an envelope sealed against something else.
function openerFor(
  answers: readonly {
    readonly table: string;
    readonly column: string;
    readonly rowId: string;
    readonly answer: NarrativeText;
  }[],
): {
  readonly open: NarrativeOpener;
  readonly calls: { binding: unknown; wire: string }[];
} {
  const calls: { binding: unknown; wire: string }[] = [];

  return {
    calls,
    open: (binding, wire) => {
      calls.push({ binding, wire });

      const match = answers.find(
        (candidate) =>
          candidate.table === binding.table &&
          candidate.column === binding.column &&
          candidate.rowId === binding.rowId,
      );

      return Promise.resolve<NarrativeText>(
        match?.answer ?? { state: 'unreadable' },
      );
    },
  };
}

function opensEverything(): ReturnType<typeof openerFor> {
  return openerFor([
    {
      table: 'categories',
      column: 'name',
      rowId: ROW_ID,
      answer: { state: 'text', value: 'Groceries' },
    },
    {
      table: 'categories',
      column: 'description',
      rowId: ROW_ID,
      answer: { state: 'text', value: 'Food and drink' },
    },
    {
      table: 'category_groups',
      column: 'name',
      rowId: GROUP_ID,
      answer: { state: 'text', value: 'Essentials' },
    },
  ]);
}

describe('toCategoryView', () => {
  it('opens the name under the categories name binding for the row’s own id', async () => {
    // Arrange
    const opener = opensEverything();

    // Act
    const view = await toCategoryView(sealedCategory, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'categories', column: 'name', rowId: ROW_ID },
      wire: sealedCategory.name,
    });
    expect(view.name).toEqual({ state: 'text', value: 'Groceries' });
  });

  it('opens the description under its own column and the category’s own id', async () => {
    // Arrange
    const opener = opensEverything();

    // Act
    const view = await toCategoryView(sealedCategory, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'categories', column: 'description', rowId: ROW_ID },
      wire: sealedCategory.description,
    });
    expect(view.description).toEqual({
      state: 'text',
      value: 'Food and drink',
    });
  });

  it('opens the group’s name under the group’s id and never under the category’s', async () => {
    // Arrange — the defect this mapper exists to prevent, and it is silent:
    // opened under `ROW_ID` the tag check fails, the value comes back
    // `unreadable`, a screen draws an em dash, and nothing on the server can
    // see it. The opener answers per binding, so the wrong id produces exactly
    // that symptom rather than a thrown error a weaker case would notice.
    const opener = opensEverything();

    // Act
    const view = await toCategoryView(sealedCategory, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'category_groups', column: 'name', rowId: GROUP_ID },
      wire: sealedCategory.categoryGroupName,
    });
    expect(view.categoryGroupName).toEqual({
      state: 'text',
      value: 'Essentials',
    });
  });

  it('carries the identifiers and the position through untouched', async () => {
    // Arrange
    const opener = opensEverything();

    // Act
    const view = await toCategoryView(sealedCategory, opener.open);

    // Assert — the members are listed rather than spread, so an exact
    // comparison is what says a further member has not been carried in.
    expect(view).toEqual({
      categoryGroupId: GROUP_ID,
      categoryGroupName: { state: 'text', value: 'Essentials' },
      description: { state: 'text', value: 'Food and drink' },
      id: ROW_ID,
      name: { state: 'text', value: 'Groceries' },
      position: 4,
    });
  });

  it('answers null for a note nobody wrote and asks the opener nothing about it', async () => {
    // Arrange — whether a column is null is known *before* any key is
    // involved, so the question is answered before the one about opening.
    const opener = opensEverything();

    // Act
    const view = await toCategoryView(
      { ...sealedCategory, description: null },
      opener.open,
    );

    // Assert — two opens, not three: the name and the group's name.
    expect(view.description).toBeNull();
    expect(opener.calls).toHaveLength(2);
  });

  it('keeps a note that did not open as unreadable and never as null', async () => {
    // Arrange — the two facts this row can carry about a note, and they are
    // different next steps for a person. Folded together, a damaged note looks
    // like a note that was never filed.
    const opener = openerFor([
      {
        table: 'categories',
        column: 'name',
        rowId: ROW_ID,
        answer: { state: 'text', value: 'Groceries' },
      },
      {
        table: 'category_groups',
        column: 'name',
        rowId: GROUP_ID,
        answer: { state: 'text', value: 'Essentials' },
      },
    ]);

    // Act
    const view = await toCategoryView(sealedCategory, opener.open);

    // Assert
    expect(view.description).toEqual({ state: 'unreadable' });
  });

  it('answers locked for every member when the opener answers locked', async () => {
    // Arrange
    const locked: NarrativeOpener = () =>
      Promise.resolve<NarrativeText>({ state: 'locked' });

    // Act
    const view = await toCategoryView(sealedCategory, locked);

    // Assert — the word, never `''` and never a dash.
    expect(view.name).toEqual({ state: 'locked' });
    expect(view.description).toEqual({ state: 'locked' });
    expect(view.categoryGroupName).toEqual({ state: 'locked' });
  });

  it('lets a misuse rejection propagate instead of reporting unreadable', async () => {
    // Arrange — the codec's word for a refusal it made about the *call*, before
    // any cipher ran. It says nothing about what is stored in any of the three
    // columns.
    const refused: NarrativeOpener = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));

    // Act
    const mapping = toCategoryView(sealedCategory, refused);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });
});

// The rule `docs/design/components.md` states under "A name that cannot be read
// cannot be renamed", asked about the two members a rename actually **writes**.
describe('categoryIsReadable', () => {
  const opened: CategoryView = {
    categoryGroupId: GROUP_ID,
    categoryGroupName: { state: 'text', value: 'Essentials' },
    description: { state: 'text', value: 'Food and drink' },
    id: ROW_ID,
    name: { state: 'text', value: 'Groceries' },
    position: 0,
  };

  it('admits a row whose name and note both opened', () => {
    // Assert — the positive control: a refusal test alone passes just as well
    // against a predicate that admits nothing.
    expect(categoryIsReadable(opened)).toBe(true);
  });

  it('admits a row that holds no note at all', () => {
    // Assert
    expect(categoryIsReadable({ ...opened, description: null })).toBe(true);
  });

  it('refuses a row whose name did not open', () => {
    // Assert
    expect(categoryIsReadable({ ...opened, name: { state: 'locked' } })).toBe(
      false,
    );
  });

  it('refuses a row whose note did not open even though its name did', () => {
    // Arrange — `PUT /api/categories/{id}` carries the note beside the name, so
    // an edit started here would post `null` and clear a note still sitting in
    // the column, invisibly.

    // Assert
    expect(
      categoryIsReadable({ ...opened, description: { state: 'unreadable' } }),
    ).toBe(false);
  });

  it('admits a row whose group name did not open, because a rename never writes it', () => {
    // Arrange — the case that separates this predicate from a rule about "the
    // row is readable". `categoryGroupName` is the **group's** column,
    // denormalized onto this row; the category's own `PUT` binds three members
    // and that is not one of them. Refusing here would strand every category in
    // a group whose name is damaged, over a rename that could not have touched
    // it.

    // Assert
    expect(
      categoryIsReadable({
        ...opened,
        categoryGroupName: { state: 'unreadable' },
      }),
    ).toBe(true);
  });
});
