// The category-group mapper's own spec, standing the mapper up with two lines
// and no `TestBed` — which is the whole reason `toCategoryGroupView` takes a
// {@link NarrativeOpener} rather than the service that owns one.
//
// **This is the first mapper in the product over a row with a nullable
// narrative column**, so half the cases below are about a distinction no
// earlier view model had to make: a note nobody wrote is `null` and a note that
// did not open is `unreadable`, and neither may become the other.
import type { CategoryGroupDto } from '@app-core/api/category-groups-api.service';
import { NarrativeFieldMisuseError } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import {
  categoryGroupIsReadable,
  toCategoryGroupView,
  type CategoryGroupView,
} from './category-group-view';

// A canonical lower-case hyphenated UUID — the one spelling the codec accepts,
// and the spelling `System.Text.Json` renders every `Guid` in, so this is what a
// read really hands the client back.
const ROW_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e';

const sealedGroup: CategoryGroupDto = {
  id: ROW_ID,
  name: 'AQIDBAUGBwgJCgsMDQ4PEA',
  description: 'AgMEBQYHCAkKCwwNDg8QEQ',
  position: 3,
};

// Answers per binding, so a case can say *this member was opened under that
// pair* rather than inferring it from a single answer. An unexpected pair
// answers `unreadable`, which is what a real codec does with a binding the
// envelope was not sealed against.
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

function opensTo(
  name: NarrativeText,
  description: NarrativeText,
): ReturnType<typeof openerFor> {
  return openerFor([
    { table: 'category_groups', column: 'name', rowId: ROW_ID, answer: name },
    {
      table: 'category_groups',
      column: 'description',
      rowId: ROW_ID,
      answer: description,
    },
  ]);
}

describe('toCategoryGroupView', () => {
  it('opens the name under the category_groups name binding for the row’s own id', async () => {
    // Arrange
    const opener = opensTo(
      { state: 'text', value: 'Essentials' },
      { state: 'text', value: 'The bills' },
    );

    // Act
    const view = await toCategoryGroupView(sealedGroup, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'category_groups', column: 'name', rowId: ROW_ID },
      wire: sealedGroup.name,
    });
    expect(view.name).toEqual({ state: 'text', value: 'Essentials' });
  });

  it('opens the description under its own column and the same row id', async () => {
    // Arrange — a second column on one row, so the pair is what distinguishes
    // the two bindings. Looked up as a pair by the codec, which is why an
    // assertion over the whole binding is the only honest one.
    const opener = opensTo(
      { state: 'text', value: 'Essentials' },
      { state: 'text', value: 'The bills' },
    );

    // Act
    const view = await toCategoryGroupView(sealedGroup, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: {
        table: 'category_groups',
        column: 'description',
        rowId: ROW_ID,
      },
      wire: sealedGroup.description,
    });
    expect(view.description).toEqual({ state: 'text', value: 'The bills' });
  });

  it('carries the identifier and the position through untouched', async () => {
    // Arrange
    const opener = opensTo(
      { state: 'text', value: 'Essentials' },
      { state: 'text', value: 'The bills' },
    );

    // Act
    const view = await toCategoryGroupView(sealedGroup, opener.open);

    // Assert — the members are listed rather than spread, so an exact
    // comparison is what says a ninth member has not been carried in.
    expect(view).toEqual({
      description: { state: 'text', value: 'The bills' },
      id: ROW_ID,
      name: { state: 'text', value: 'Essentials' },
      position: 3,
    });
  });

  it('answers null for a column that held nothing and asks the opener nothing about it', async () => {
    // Arrange — whether a column is null is known *before* any key is involved,
    // so the question is answered before the one about opening.
    const opener = opensTo(
      { state: 'text', value: 'Essentials' },
      { state: 'text', value: 'unreachable' },
    );

    // Act
    const view = await toCategoryGroupView(
      { ...sealedGroup, description: null },
      opener.open,
    );

    // Assert
    expect(view.description).toBeNull();
    expect(opener.calls).toHaveLength(1);
  });

  it('keeps a description that did not open as unreadable and never as null', async () => {
    // Arrange — the two facts this row can carry about a note, and they are
    // different next steps for a person: `null` is nobody wrote one, and
    // `unreadable` is one is stored and these bytes did not authenticate.
    // Folded together, the second is rendered as the first and a damaged note
    // looks like a note that was never filed.
    const opener = opensTo(
      { state: 'text', value: 'Essentials' },
      { state: 'unreadable' },
    );

    // Act
    const view = await toCategoryGroupView(sealedGroup, opener.open);

    // Assert
    expect(view.description).toEqual({ state: 'unreadable' });
  });

  it('answers locked for both members when the opener answers locked', async () => {
    // Arrange
    const opener = opensTo({ state: 'locked' }, { state: 'locked' });

    // Act
    const view = await toCategoryGroupView(sealedGroup, opener.open);

    // Assert — the word, never `''` and never a dash. A mapper that collapsed
    // it would have the screen claim something about the account when the truth
    // is about this tab.
    expect(view.name).toEqual({ state: 'locked' });
    expect(view.description).toEqual({ state: 'locked' });
  });

  it('lets a misuse rejection propagate instead of reporting unreadable', async () => {
    // Arrange — the codec's word for a refusal it made about the *call*, before
    // any cipher ran. It says nothing about what is stored in either column, so
    // turning it into `unreadable` puts a sentence about damaged text in front
    // of somebody who can do nothing about it, over a row that is fine.
    const refused: NarrativeOpener = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));

    // Act
    const mapping = toCategoryGroupView(sealedGroup, refused);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });
});

// The rule `docs/design/components.md` states under "A name that cannot be read
// cannot be renamed": a row whose words this browser cannot read has no edit to
// start, because the field would prefill empty and the save would seal a blank
// over values still sitting in the columns. This is the predicate the screen
// asks, and it is a *type* predicate so that the handler's half of the gate is
// held by the compiler — reading `.value` off a `NarrativeText` does not
// type-check until the state has been narrowed.
describe('categoryGroupIsReadable', () => {
  const opened: CategoryGroupView = {
    description: { state: 'text', value: 'The bills' },
    id: ROW_ID,
    name: { state: 'text', value: 'Essentials' },
    position: 0,
  };

  it('admits a row whose name and note both opened', () => {
    // Assert — the positive control: a refusal test alone passes just as well
    // against a predicate that admits nothing.
    expect(categoryGroupIsReadable(opened)).toBe(true);
  });

  it('admits a row that holds no note at all', () => {
    // Arrange — `null` is a column nobody filled in, which is not a value that
    // failed to open. Refusing it would make every group without a description
    // permanently unrenameable.

    // Assert
    expect(categoryGroupIsReadable({ ...opened, description: null })).toBe(
      true,
    );
  });

  it('refuses a row whose name did not open', () => {
    // Assert
    expect(
      categoryGroupIsReadable({ ...opened, name: { state: 'unreadable' } }),
    ).toBe(false);
    expect(
      categoryGroupIsReadable({ ...opened, name: { state: 'locked' } }),
    ).toBe(false);
  });

  it('refuses a row whose note did not open even though its name did', () => {
    // Arrange — the case this screen has and the accounts screen does not.
    // `PUT /api/category-groups/{id}` carries the note beside the name, so an
    // edit started here prefills the note field empty and the save posts
    // `null` — clearing a note that is still in the column, over a name that
    // rendered perfectly. Every symptom of that is invisible: 204, a legal row,
    // and every later read agreeing the person never wrote one.

    // Assert
    expect(
      categoryGroupIsReadable({
        ...opened,
        description: { state: 'unreadable' },
      }),
    ).toBe(false);
  });
});
