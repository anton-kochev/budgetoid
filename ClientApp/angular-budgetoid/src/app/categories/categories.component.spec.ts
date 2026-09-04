// The categories screen, driven against two hand-written stubs.
//
// **Custody is stubbed for its `status` alone, and the stub's status is its own
// settable signal.** The real service reaches `locked` only by never having
// been unlocked or by a failed ceremony, neither of which this runner can
// stage; and a stub that *derived* the reading from something else would be a
// second copy of the predicate, pinning nothing about which object the screen
// asked.
//
// **`implements Pick<S, keyof S>` on both stubs** is the compiler's own census
// of what each service publishes — `keyof` over a class yields the public
// surface only — so a member the screen starts reaching for is an error here
// rather than an `is not a function` during change detection.
//
// **This screen carries two forms and two lists, so every rule is asked twice.**
// That is not padding: the two halves are written separately in the component
// and a screen that got one right and the other wrong is exactly what shipped
// last time somebody copied a form.
//
// **`unlocking` gets its own two cases, because it is where the two predicates
// disagree.** The form follows "anything but `unlocked`" and the notice follows
// `locked` exactly, so a screen written with one predicate passes half of what
// is here and fails the other half whichever way it was written.
import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { signal, type Signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormGroup, type AbstractControl } from '@angular/forms';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CategoryGroupView } from './category-group-view';
import type { CategoryView } from './category-view';
import { CategoriesComponent } from './categories.component';
import { CategoriesService } from './categories.service';

const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const OTHER_GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';

const essentials: CategoryGroupView = {
  description: { state: 'text', value: 'The bills' },
  id: GROUP_ID,
  name: { state: 'text', value: 'Essentials' },
  position: 0,
};

const lifestyle: CategoryGroupView = {
  description: null,
  id: OTHER_GROUP_ID,
  name: { state: 'text', value: 'Lifestyle' },
  position: 1,
};

const groceries: CategoryView = {
  categoryGroupId: GROUP_ID,
  categoryGroupName: { state: 'text', value: 'Essentials' },
  description: { state: 'text', value: 'Food and drink' },
  id: CATEGORY_ID,
  name: { state: 'text', value: 'Groceries' },
  position: 0,
};

class CategoriesServiceStub
  implements Pick<CategoriesService, keyof CategoriesService>
{
  public readonly groupsSignal = signal<readonly CategoryGroupView[] | null>([
    essentials,
    lifestyle,
  ]);
  public readonly categoriesSignal = signal<readonly CategoryView[] | null>([
    groceries,
  ]);
  public readonly loadingSignal = signal(false);
  public readonly failedSignal = signal(false);

  public readonly groups = this.groupsSignal.asReadonly();
  public readonly categories = this.categoriesSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();
  public readonly failed = this.failedSignal.asReadonly();

  public load = vi.fn();
  public addGroup = vi.fn((): Promise<void> => Promise.resolve());
  public updateGroup = vi.fn((): Promise<void> => Promise.resolve());
  public moveGroup = vi.fn();
  public removeGroup = vi.fn();
  public addCategory = vi.fn((): Promise<void> => Promise.resolve());
  public updateCategory = vi.fn((): Promise<void> => Promise.resolve());
  public placeCategory = vi.fn();
  public removeCategory = vi.fn();
  public categoriesForGroup = vi.fn(
    (categoryGroupId: string): readonly CategoryView[] =>
      (this.categoriesSignal() ?? []).filter(
        (category) => category.categoryGroupId === categoryGroupId,
      ),
  );
}

class CustodyStub
  implements Pick<AccountKeyCustodyService, keyof AccountKeyCustodyService>
{
  readonly #status = signal<AccountKeyStatus>('unlocked');

  public readonly status: Signal<AccountKeyStatus> = this.#status.asReadonly();
  public readonly unlockFailure: Signal<UnlockFailure | null> =
    signal<UnlockFailure | null>(null).asReadonly();

  public setStatus(status: AccountKeyStatus): void {
    this.#status.set(status);
  }

  public unlock(): void {
    throw new Error('the categories screen may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the categories screen may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the categories screen may not lock the account');
  }

  public sealField(): never {
    throw new Error('the categories screen may not seal — the service does');
  }

  public openField(): never {
    throw new Error('the categories screen may not open — the mapper does');
  }

  public blindIndex(): never {
    throw new Error('the categories screen may not index — the service does');
  }
}

// The screen's own members are `protected`, which is right for a template and
// leaves a spec nothing to hold. One cast, in one place, so the reach is
// visible rather than scattered.
interface Exposed {
  groupForm: FormGroup;
  categoryForm: FormGroup;
  saveGroup: () => void;
  saveCategory: () => void;
  editGroup: (group: CategoryGroupView) => void;
  editCategory: (category: CategoryView) => void;
  cancelCategoryEdit: () => void;
  dropGroup: (event: CdkDragDrop<readonly CategoryGroupView[]>) => void;
  dropCategory: (
    event: CdkDragDrop<readonly CategoryView[]>,
    categoryGroupId: string,
  ) => void;
}

function exposed(component: CategoriesComponent): Exposed {
  return component as unknown as Exposed;
}

describe('CategoriesComponent', () => {
  let categories: CategoriesServiceStub;
  let custody: CustodyStub;
  let fixture: ComponentFixture<CategoriesComponent>;

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function screen(): Exposed {
    return exposed(fixture.componentInstance);
  }

  // Controls found by their label rather than by a class, so a stylesheet
  // change cannot silently empty these lists.
  function buttonsLabelled(
    selector: string,
    label: string,
  ): HTMLButtonElement[] {
    return Array.from(
      host().querySelectorAll<HTMLButtonElement>(selector),
    ).filter((button) => (button.textContent ?? '').trim() === label);
  }

  function nameInputs(): HTMLInputElement[] {
    return Array.from(
      host().querySelectorAll<HTMLInputElement>(
        'input[formcontrolname="name"]',
      ),
    );
  }

  // Everything inside the two forms, and nothing from the hierarchy below
  // them. The locked notice replaces the hierarchy, so a whole-host text search
  // would pass for a screen that still had an opened name sitting on a dead
  // control in the form.
  function editorsText(): string {
    return host().querySelector('.editors')?.textContent ?? '';
  }

  function submitButtons(): HTMLButtonElement[] {
    return Array.from(
      host().querySelectorAll<HTMLButtonElement>('button[type="submit"]'),
    );
  }

  beforeEach(async () => {
    categories = new CategoriesServiceStub();
    custody = new CustodyStub();
    await TestBed.configureTestingModule({
      imports: [CategoriesComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: CategoriesService, useValue: categories },
        { provide: AccountKeyCustodyService, useValue: custody },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CategoriesComponent);
    fixture.detectChanges();
  });

  it('loads the hierarchy on initialization', () => {
    // Assert
    expect(categories.load).toHaveBeenCalledOnce();
  });

  // Both of these used to supply `currentIndex: 0` and expect position `0`,
  // which is the one value a hard-coded literal also produces: replacing
  // `event.currentIndex` with `0` passed the pair. The index below is non-zero
  // for that reason and no other.
  it('persists group drag-and-drop position', () => {
    // Arrange
    const event = {
      item: { data: lifestyle },
      previousIndex: 0,
      currentIndex: 2,
    } as unknown as CdkDragDrop<readonly CategoryGroupView[]>;

    // Act
    screen().dropGroup(event);

    // Assert
    expect(categories.moveGroup).toHaveBeenCalledWith(OTHER_GROUP_ID, 2);
  });

  it('persists category drag-and-drop placement', () => {
    // Arrange
    const event = {
      item: { data: groceries },
      previousIndex: 0,
      currentIndex: 3,
    } as unknown as CdkDragDrop<readonly CategoryView[]>;

    // Act
    screen().dropCategory(event, OTHER_GROUP_ID);

    // Assert
    expect(categories.placeCategory).toHaveBeenCalledWith(
      CATEGORY_ID,
      OTHER_GROUP_ID,
      3,
    );
  });

  describe('what a form may send', () => {
    it('refuses a whitespace-only name on both forms', () => {
      // Arrange — `Validators.required` admits this, and the `.trim()` that used
      // to swallow it is gone: the client may not alter what it seals. On a
      // blind-indexed column it is worse than untidy — the index normalizes by
      // trimming, so `'   '` keys to the index of the **empty** name.
      screen().groupForm.setValue({ name: '   ', description: '' });
      screen().categoryForm.setValue({
        name: '   ',
        description: '',
        categoryGroupId: GROUP_ID,
      });

      // Act
      screen().saveGroup();
      screen().saveCategory();

      // Assert
      expect(screen().groupForm.invalid).toBe(true);
      expect(screen().categoryForm.invalid).toBe(true);
      expect(categories.addGroup).not.toHaveBeenCalled();
      expect(categories.addCategory).not.toHaveBeenCalled();
    });

    it('accepts a name that holds text and hands it over untrimmed', () => {
      // Arrange — the positive control: a refusal test alone passes just as
      // well against a form nothing can ever satisfy.
      screen().groupForm.setValue({
        name: '  Essentials  ',
        description: '',
      });

      // Act
      screen().saveGroup();

      // Assert — untrimmed on the way past, because the service seals exactly
      // what was typed and indexes the same string.
      expect(categories.addGroup).toHaveBeenCalledWith({
        name: '  Essentials  ',
        description: '',
      });
    });

    it('hands a whitespace-only note over as typed', () => {
      // Arrange — the removed `normalizeDescription` folded this onto `null`,
      // and re-importing that fold one layer up is the mistake this case
      // exists to catch. `''` is how the screen says "no note"; `'   '` is a
      // note somebody typed, and the client may not alter what it seals. Note
      // there is deliberately **no** non-blank validator on this control: the
      // note is indexed by nothing, so it collides with nothing.
      screen().groupForm.setValue({ name: 'Essentials', description: '   ' });

      // Act
      screen().saveGroup();

      // Assert
      expect(categories.addGroup).toHaveBeenCalledWith({
        name: 'Essentials',
        description: '   ',
      });
    });

    it('sends a category’s group and text on a create and only the text on a rename', () => {
      // Arrange — a rename binds three members and the group is not one of
      // them; the picker is disabled during an edit and a category moves group
      // by being dragged.
      screen().categoryForm.setValue({
        name: 'Groceries',
        description: 'Food and drink',
        categoryGroupId: GROUP_ID,
      });

      // Act
      screen().saveCategory();
      screen().editCategory(groceries);
      screen().saveCategory();

      // Assert
      expect(categories.addCategory).toHaveBeenCalledWith({
        name: 'Groceries',
        description: 'Food and drink',
        categoryGroupId: GROUP_ID,
      });
      expect(categories.updateCategory).toHaveBeenCalledWith(CATEGORY_ID, {
        name: 'Groceries',
        description: 'Food and drink',
      });
    });
  });

  describe('the locked account', () => {
    it('disables both forms with a reason while the account is locked', () => {
      // Arrange
      custody.setStatus('locked');

      // Act
      fixture.detectChanges();

      // Assert — disabled in the DOM, not merely dimmed: an enabled form
      // submits, the service refuses because it cannot seal, and nothing
      // happens, which reads as a failure rather than as a limitation.
      const reasons = Array.from(host().querySelectorAll('form p.reason')).map(
        (element) => element.textContent ?? '',
      );

      expect(nameInputs()).toHaveLength(2);
      expect(nameInputs().every((input) => input.disabled)).toBe(true);
      expect(reasons).toHaveLength(2);
      expect(reasons.every((reason) => reason.includes('Unlock'))).toBe(true);
      expect(reasons.every((reason) => reason.includes('Settings'))).toBe(true);
    });

    it('keeps both submit buttons disabled while the account is locked', () => {
      // Arrange — the trap this case exists for: a disabled form's status is
      // `DISABLED`, which excludes it from validation and makes `form.invalid`
      // answer **false**, so `[disabled]="form.invalid"` alone *enables* the
      // button the moment the form is switched off. The lock has to be named
      // again on the control.
      custody.setStatus('locked');

      // Act
      fixture.detectChanges();

      // Assert
      expect(submitButtons()).toHaveLength(2);
      expect(submitButtons().every((button) => button.disabled)).toBe(true);
    });

    it('refuses a submit that reaches the handler while the account is locked', () => {
      // Arrange — the third naming of the lock. Material's click-halt is
      // applied to anchors only, so a `<button>` still receives the press that
      // arrives here, and the form is valid because it is disabled.
      screen().groupForm.setValue({ name: 'Essentials', description: '' });
      custody.setStatus('locked');
      fixture.detectChanges();

      // Act
      screen().saveGroup();
      screen().saveCategory();

      // Assert
      expect(categories.addGroup).not.toHaveBeenCalled();
      expect(categories.addCategory).not.toHaveBeenCalled();
    });

    it('leaves both forms enabled while the account is unlocked', () => {
      // Arrange — the control for the cases above.

      // Act
      fixture.detectChanges();

      // Assert — `some` and never `every`. `.every(disabled) === false` is
      // satisfied by **either** form being live, which is exactly the state
      // this screen shipped once, and the locked case next door is written
      // correctly (`every` plus a length) so the pair looked symmetrical while
      // only one half held. `.some(disabled) === false` says every input is
      // live, and the length says there are two of them to be live.
      expect(nameInputs()).toHaveLength(2);
      expect(nameInputs().some((input) => input.disabled)).toBe(false);
      expect(host().querySelector('form p.reason')).toBeNull();
    });

    it('renders the locked notice in place of the hierarchy while locked', () => {
      // Arrange — rows are present, so this is the notice replacing a list
      // rather than filling an empty one.
      custody.setStatus('locked');

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().querySelector('app-locked-account-notice')).not.toBeNull();
      expect(host().querySelector('.category-groups')).toBeNull();
    });

    it('renders the hierarchy and no notice while the account is unlocked', () => {
      // Arrange — the control for the case above.

      // Act
      fixture.detectChanges();

      // Assert — the names are rendered through `narrative-value`, so what
      // lands in the DOM is text rather than `[object Object]`.
      expect(host().querySelector('app-locked-account-notice')).toBeNull();
      expect(host().querySelector('.category-groups')).not.toBeNull();
      expect(host().textContent ?? '').toContain('Essentials');
      expect(host().textContent ?? '').toContain('The bills');
      expect(host().textContent ?? '').toContain('Groceries');
      expect(host().textContent ?? '').not.toContain('[object Object]');
    });

    it('disables both forms while the account is unlocking', () => {
      // Arrange — the third word. A predicate written `!== 'locked'` leaves the
      // forms live for the whole ceremony, and every save made in that window
      // is refused by a service that cannot seal — silently, where nobody is
      // looking. The rule is that a form is usable only when the status is
      // `unlocked`.
      custody.setStatus('unlocking');

      // Act
      fixture.detectChanges();

      // Assert
      expect(nameInputs().every((input) => input.disabled)).toBe(true);
      expect(submitButtons().every((button) => button.disabled)).toBe(true);
    });

    it('takes the group picker out of the DOM while the account is locked', () => {
      // Arrange — the picker sits in the **category form**, which is outside
      // the `@if (locked())` that replaces the hierarchy, so a group name this
      // browser opened went on being rendered beside a notice saying this tab
      // cannot read the account.
      //
      // **Emptying the option list is not enough, and that was measured in
      // round one:** a `mat-select` goes on displaying the option it had
      // selected after the option is gone. The control has to leave the DOM,
      // and the assertion below is written so that the weaker fix fails it —
      // it reads the text on screen, not the length of an option list.
      screen().categoryForm.patchValue({ categoryGroupId: GROUP_ID });
      fixture.detectChanges();
      expect(editorsText()).toContain('Essentials');

      // Act
      custody.setStatus('locked');
      fixture.detectChanges();

      // Assert
      expect(editorsText()).not.toContain('Essentials');
      expect(
        host().querySelector('mat-select[formcontrolname="categoryGroupId"]'),
      ).toBeNull();
    });

    it('keeps the hierarchy on screen while the account is unlocking', () => {
      // Arrange — the notice follows `locked` alone, where the forms follow
      // "anything but unlocked". Two questions, two predicates: the notice's
      // sentence tells somebody to go and press Unlock, and that advice is
      // already wrong for a person whose unlock is running.
      custody.setStatus('unlocking');

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().querySelector('app-locked-account-notice')).toBeNull();
      expect(host().querySelector('.category-groups')).not.toBeNull();
    });
  });

  // Three places used to set this one control's enabled state — the form-wide
  // effect, `editCategory` and `cancelCategoryEdit` — and each of the two cases
  // below is one pair of them disagreeing. Both symptoms are silent, which is
  // why the fix is that the effect owns the state and derives it, rather than a
  // fourth call put somewhere to compensate.
  describe('who owns the group picker’s enabled state', () => {
    function picker(): AbstractControl | null {
      return screen().categoryForm.get('categoryGroupId');
    }

    it('turns the picker off for an edit and on again for the next create', () => {
      // Arrange — a rename binds three members and the group is not one of
      // them; a category moves group by being dragged, so the picker is off
      // while an edit is running. This is the control for the two cases below:
      // an owner that never enabled anything would pass them both.

      // Act
      screen().editCategory(groceries);
      fixture.detectChanges();
      const duringEdit = picker()?.disabled;

      screen().cancelCategoryEdit();
      fixture.detectChanges();

      // Assert
      expect(duringEdit).toBe(true);
      expect(picker()?.disabled).toBe(false);
    });

    it('keeps the picker off through a lock and an unlock during an edit', () => {
      // Arrange — the first silent symptom. The form-wide effect calls
      // `enable()` on the **group**, which enables every child including this
      // one, so a lock and an unlock landing mid-edit hand the picker back
      // live. Somebody then changes the group, presses Save, and the edit
      // branch sends `{description, name}` — the API answers 204 and the
      // category has not moved.
      screen().editCategory(groceries);
      fixture.detectChanges();
      expect(picker()?.disabled).toBe(true);

      // Act
      custody.setStatus('locked');
      fixture.detectChanges();
      custody.setStatus('unlocked');
      fixture.detectChanges();

      // Assert
      expect(picker()?.disabled).toBe(true);
    });

    it('leaves the whole category form disabled when an edit is cancelled while locked', () => {
      // Arrange — the second. Cancel is a plain `<button>`, unaffected by the
      // FormGroup's disabled state, so the press arrives; `cancelCategoryEdit`
      // then enabled this control unconditionally. A group is `DISABLED` only
      // while **every** child is, so one live control flips the whole form's
      // status back — on a screen whose whole point is that it cannot write.
      screen().editCategory(groceries);
      fixture.detectChanges();
      custody.setStatus('locked');
      fixture.detectChanges();

      // Act
      screen().cancelCategoryEdit();
      fixture.detectChanges();

      // Assert
      expect(picker()?.disabled).toBe(true);
      expect(screen().categoryForm.disabled).toBe(true);
    });
  });

  describe('a row that cannot be read cannot be renamed', () => {
    it('refuses to edit a group whose name did not open', () => {
      // Arrange
      categories.groupsSignal.set([
        { ...essentials, name: { state: 'unreadable' } },
      ]);

      // Act
      fixture.detectChanges();
      screen().editGroup({ ...essentials, name: { state: 'unreadable' } });

      // Assert — disabled in the DOM, with the reason in the row, and the gate
      // repeated in the handler because Material's click-halt is anchors only.
      expect(
        buttonsLabelled('.group-heading button', 'Edit').at(0)?.disabled,
      ).toBe(true);
      expect(host().textContent ?? '').toContain('can’t be renamed');
      expect(screen().groupForm.getRawValue()).toMatchObject({ name: '' });
    });

    it('refuses to edit a group whose note did not open even though its name did', () => {
      // Arrange — the half the accounts screen never had. The `PUT` carries the
      // note beside the name, so an edit started here prefills the note field
      // empty and the save posts `null` — clearing a note still sitting in the
      // column, over a name that rendered perfectly, with a 204 and a legal row
      // and nothing anywhere to see.
      const damagedNote: CategoryGroupView = {
        ...essentials,
        description: { state: 'unreadable' },
      };

      categories.groupsSignal.set([damagedNote]);

      // Act
      fixture.detectChanges();
      screen().editGroup(damagedNote);

      // Assert
      expect(
        buttonsLabelled('.group-heading button', 'Edit').at(0)?.disabled,
      ).toBe(true);
      expect(screen().groupForm.getRawValue()).toMatchObject({ name: '' });
    });

    it('leaves Delete available on a row it will not rename', () => {
      // Arrange — the asymmetry is the point: somebody looking at a row they
      // cannot read may still decide it should not exist, and removing a row is
      // not rewriting its contents.
      categories.groupsSignal.set([
        { ...essentials, name: { state: 'locked' } },
      ]);

      // Act
      fixture.detectChanges();
      buttonsLabelled('.group-heading button', 'Delete').at(0)?.click();

      // Assert
      expect(categories.removeGroup).toHaveBeenCalledWith(GROUP_ID);
    });

    it('allows editing a group whose name and note both opened', () => {
      // Arrange — the control for the cases above.

      // Act
      fixture.detectChanges();
      screen().editGroup(essentials);

      // Assert
      expect(
        buttonsLabelled('.group-heading button', 'Edit').at(0)?.disabled,
      ).toBe(false);
      expect(screen().groupForm.getRawValue()).toMatchObject({
        description: 'The bills',
        name: 'Essentials',
      });
    });

    it('allows editing a group that holds no note at all', () => {
      // Arrange — `null` is a column nobody filled in, not a value that failed
      // to open. Refusing it would strand every group without a description.

      // Act
      fixture.detectChanges();
      screen().editGroup(lifestyle);

      // Assert
      expect(screen().groupForm.getRawValue()).toMatchObject({
        description: '',
        name: 'Lifestyle',
      });
    });

    it('refuses to edit a category whose note did not open', () => {
      // Arrange
      const damagedNote: CategoryView = {
        ...groceries,
        description: { state: 'unreadable' },
      };

      categories.categoriesSignal.set([damagedNote]);

      // Act
      fixture.detectChanges();
      screen().editCategory(damagedNote);

      // Assert
      expect(
        buttonsLabelled('.category-row button', 'Edit').at(0)?.disabled,
      ).toBe(true);
      expect(screen().categoryForm.getRawValue()).toMatchObject({ name: '' });
    });

    it('allows editing a category whose group name did not open', () => {
      // Arrange — the case that keeps this gate about the row's **own** words.
      // `categoryGroupName` is the group's column denormalized onto this row,
      // and `PUT /api/categories/{id}` cannot touch it, so refusing here would
      // strand every category in a group whose name is damaged over a write
      // that could never have made things worse.
      const damagedGroupName: CategoryView = {
        ...groceries,
        categoryGroupName: { state: 'unreadable' },
      };

      categories.categoriesSignal.set([damagedGroupName]);

      // Act
      fixture.detectChanges();
      screen().editCategory(damagedGroupName);

      // Assert
      expect(
        buttonsLabelled('.category-row button', 'Edit').at(0)?.disabled,
      ).toBe(false);
      expect(screen().categoryForm.getRawValue()).toMatchObject({
        name: 'Groceries',
      });
    });
  });

  describe('a list with no answer', () => {
    it('says it is reading while a load is in flight', () => {
      // Arrange — the list is null at rest, in flight and after a failure, so
      // the loading line is read off the published running state rather than
      // off the absent value.
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('Reading your categories');
      expect(host().textContent ?? '').not.toContain('No category groups yet');
    });

    it('says there are none only once a server has answered', () => {
      // Arrange — `[]` is the sentence *you have no category groups*, which is
      // a claim only a server that answered may make.
      categories.groupsSignal.set([]);
      categories.loadingSignal.set(false);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('No category groups yet');
    });

    it('says the read failed rather than rendering nothing', () => {
      // Arrange — a failed load leaves the list `null` and `loading` false, and
      // the previous answer is not restored. This case used to assert that the
      // screen said **nothing**, which is what it did: two forms on top and
      // silence beneath them, indistinguishable from an account with no
      // categories in it. A read that failed and an empty account are two
      // different next steps for a person — the distinction `SessionService`
      // keeps between `anonymous` and `unreachable` — and a screen that renders
      // neither sentence has collapsed them into a blank.
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(false);
      categories.failedSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('couldn’t read your');
      expect(host().textContent ?? '').not.toContain('Reading your categories');
      expect(host().textContent ?? '').not.toContain('No category groups yet');
    });

    it('draws neither the hierarchy nor a failure while the read is running', () => {
      // Arrange — the control for the case above, and for the branch order: a
      // failure that outranked the running line would put the sentence on
      // screen during every reload after one failed read.
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(true);
      categories.failedSignal.set(false);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('Reading your categories');
      expect(host().textContent ?? '').not.toContain('couldn’t read your');
    });

    // **Where the two lines land is a property the four cases above cannot
    // see.** Every one of them asserts the text is somewhere in the host, and
    // a screen that draws each sentence in a `role="status"` created at the
    // moment it gains content passes all four while announcing nothing:
    // assistive technology has to have been watching the node *before* the
    // text arrived. So the node is taken while it is still empty and the later
    // text is asserted to arrive **in that same node** — `docs/design/
    // components.md`, "A value read from the network".
    function statusRegion(): HTMLElement | null {
      return host().querySelector<HTMLElement>('[role="status"]');
    }

    it('holds an empty status region from first paint', () => {
      // Arrange — the fixture's own default: lists that answered, nothing
      // running and nothing failed, so there is deliberately nothing to say.

      // Act
      fixture.detectChanges();

      // Assert — present and silent. `status` and never `assertive`, which is
      // reserved for a failure to save something a person typed.
      expect(statusRegion()).not.toBeNull();
      expect((statusRegion()?.textContent ?? '').trim()).toBe('');
      expect(
        host().querySelector('[role="alert"], [aria-live="assertive"]'),
      ).toBeNull();
    });

    it('announces the loading line from the region that was already there', () => {
      // Arrange — taken while it is still empty, which is the whole point of
      // taking it here rather than after the act.
      const region = statusRegion();

      // Act
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(true);
      fixture.detectChanges();

      // Assert — the same element, not a second one that arrived with its
      // text.
      expect(statusRegion()).toBe(region);
      expect(region?.textContent ?? '').toContain('Reading your categories');
    });

    it('announces the failure sentence from that same region', () => {
      // Arrange
      const region = statusRegion();

      // Act
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(false);
      categories.failedSignal.set(true);
      fixture.detectChanges();

      // Assert
      expect(statusRegion()).toBe(region);
      expect(region?.textContent ?? '').toContain('couldn’t read your');
    });

    it('says nothing at all while the account is locked', () => {
      // Arrange — the state a lock actually leaves behind: `CategoriesService`
      // destroys both lists and clears `failed`, and it does **not** clear the
      // running flag, so a region reading the load alone tells somebody a read
      // is in flight beside a notice saying this tab cannot read the account.
      // The chain this region replaced answered that by putting `locked`
      // first, and the predicate has to keep doing it.
      categories.groupsSignal.set(null);
      categories.loadingSignal.set(true);
      custody.setStatus('locked');

      // Act
      fixture.detectChanges();

      // Assert — still in the DOM, with nothing to say. The notice is what
      // speaks for this state.
      expect(statusRegion()).not.toBeNull();
      expect((statusRegion()?.textContent ?? '').trim()).toBe('');
    });

    it('says it is reading, not that it failed, when both flags are up', () => {
      // Arrange — reachable, and not by contrivance: only `load()` clears
      // `failed`, so a failed read followed by a press on either Add leaves
      // both lists null, `failed` true and the running flag true at once.
      // Ordered the other way the screen tells somebody to check their
      // connection while a request of theirs is in flight. The four text cases
      // above each set one flag, so none of them can see this.
      categories.groupsSignal.set(null);
      categories.failedSignal.set(true);
      categories.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(statusRegion()?.textContent ?? '').toContain(
        'Reading your categories',
      );
      expect(statusRegion()?.textContent ?? '').not.toContain(
        'couldn’t read your',
      );
    });

    it('says nothing while a write runs over a hierarchy already on screen', () => {
      // Arrange — `loading` is set by every **write** as well as by the read,
      // and the hierarchy stays up throughout one. The chain this region
      // replaced put the groups ahead of the loading line, so a save never
      // drew "Reading your categories…" under them; a region reading the
      // running flag alone brings that back, and the rule is the book's — a
      // section renders at most one of the value, the loading line and the
      // failure.
      categories.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert — the value is on screen, so the region has nothing to add.
      expect(host().querySelector('.category-groups')).not.toBeNull();
      expect((statusRegion()?.textContent ?? '').trim()).toBe('');
    });
  });
});
