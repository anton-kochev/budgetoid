// The categories screen, and the last surface in this product to render values
// it had to open and to refuse writing ones it cannot seal. After this, nothing
// in the client sends plaintext.
//
// **The locked treatment is `docs/design/components.md`, "The locked account",
// and it is the same three things the accounts and transactions screens
// ship.** The hierarchy is replaced by `locked-account-notice` — not hidden,
// and not a route guard, which would be synchronous against a fact with no
// resolution on the navigation path and would put key state where
// `AccountUnlockService` is built to keep it out of. **Both** forms are
// DOM-disabled, because an enabled form submits, the service refuses because it
// cannot seal, and nothing happens — which reads as a failure rather than as a
// limitation. And the reason is a sentence beside each form.
//
// **Two predicates over one status, and they are not the same question.**
// {@link CategoriesComponent.writable} is `=== 'unlocked'`, written
// **positively** so that `unlocking` and any word added later arrive disabled —
// loud and harmless — rather than live and silent.
// {@link CategoriesComponent.locked} is `=== 'locked'` exactly, because the
// notice's sentence is *advice* and that advice is already wrong for somebody
// whose unlock is running. Disable when unsure; do not advise when unsure.
//
// **The lock is named three times per form and none of them is redundant.** A
// disabled form's status is `DISABLED`, which excludes it from validation and
// makes `form.invalid` answer **false** — so `[disabled]="form.invalid"` alone
// *enables* the submit button the moment the form is switched off. It is named
// on the form (the `effect`), again on the control, and again in the handler,
// because Material's click-halt is applied to anchors only and a `<button>`
// still receives the press.
//
// **A row whose words did not open cannot be renamed, and this screen has a
// half the accounts screen never had.** Both `PUT` routes carry the note
// **beside** the name, so an edit started over a note that did not open
// prefills that field empty and the save posts `null` — clearing a note still
// sitting in the column, over a name that rendered perfectly, with a 204 and a
// legal row and nothing anywhere to see. `categoryGroupIsReadable` and
// `categoryIsReadable` are *type* predicates for exactly that reason: on the
// accounts screen the compiler held the handler's half for free, and a screen
// with a second nullable column loses it the moment somebody writes
// `description?.state === 'text' ? … : ''` inline. Deleting such a row stays
// available: removing is not rewriting.
//
// **A category's group name is not part of that gate.** It is the group's
// column denormalized onto the row, and a category's `PUT` cannot touch it.
//
// **The non-blank validator is on the name and deliberately not on the note.**
// The name is blind-indexed and the index normalizes by trimming, so `'   '`
// keys to the index of the **empty** name — every blank-named row in the budget
// collides on the column whose whole purpose is that equal names collide. The
// note is indexed by nothing, so a note of three spaces costs nobody anything
// and is a note somebody typed: the service seals it as typed, and the
// `normalizeDescription` that used to fold it onto `null` is gone, because the
// client may not alter what it seals.
import {
  CdkDrag,
  CdkDragDrop,
  CdkDragHandle,
  CdkDropList,
} from '@angular/cdk/drag-drop';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import {
  FormBuilder,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import {
  categoryGroupIsReadable,
  type CategoryGroupView,
} from './category-group-view';
import { categoryIsReadable, type CategoryView } from './category-view';
import { CategoriesService } from './categories.service';

/**
 * Refuses a value that is entirely whitespace.
 *
 * It trims **to judge** and never to alter: what the service seals is the
 * control's own value, character for character, and a validator that wrote a
 * trimmed value back would reintroduce the defect it exists to close.
 */
function nonBlank(control: AbstractControl): ValidationErrors | null {
  return typeof control.value === 'string' && control.value.trim().length === 0
    ? { blank: true }
    : null;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    LockedAccountNoticeComponent,
    NarrativeValueComponent,
  ],
  styles: `
    :host {
      display: block;
      padding: 1.5rem 2rem;
    }

    .editors {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
      gap: 2rem;
      margin-bottom: 2rem;
    }

    form {
      display: grid;
      align-content: start;
      gap: 1rem;
    }

    .actions,
    .group-heading,
    .category-row {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .group-heading {
      justify-content: space-between;
    }

    .reason {
      margin: 0;
      color: var(--bud-text-muted);
    }

    .category-groups {
      display: grid;
      gap: 1rem;
    }

    .category-group {
      border: 1px solid color-mix(in srgb, currentColor 20%, transparent);
      border-radius: 0.5rem;
      padding: 1rem;
      background: var(--mat-sys-surface, Canvas);
    }

    .category-list {
      min-height: 3rem;
      display: grid;
      gap: 0.5rem;
      padding-top: 0.5rem;
    }

    .category-row {
      border: 1px solid color-mix(in srgb, currentColor 12%, transparent);
      border-radius: 0.25rem;
      padding: 0.5rem;
      background: var(--mat-sys-surface-container-low, Canvas);
    }

    .category-copy {
      flex: 1;
      display: grid;
    }

    .empty {
      opacity: 0.7;
      padding: 0.75rem;
    }

    .cdk-drag-preview {
      box-sizing: border-box;
      border-radius: 0.5rem;
      box-shadow: 0 5px 10px rgb(0 0 0 / 20%);
    }

    .cdk-drag-placeholder {
      opacity: 0.25;
    }
  `,
  template: `
    <h1>Categories</h1>

    <div class="editors">
      <form [formGroup]="groupForm" (ngSubmit)="saveGroup()">
        <h2>
          {{ editingGroupId() ? 'Edit category group' : 'Add category group' }}
        </h2>
        @if (!writable()) {
          <!--
            The reason, beside the form rather than on it. A disabled control
            whose explanation is a tooltip is an explanation nobody hears, and
            this is a capability the tab has temporarily lost rather than one
            the product does not have — so the sentence names the press that
            returns it.
          -->
          <p class="reason">
            Adding and editing are off while this tab can’t read your account.
            Press Unlock in Settings to turn them back on.
          </p>
        }
        <mat-form-field>
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" maxlength="200" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Description</mat-label>
          <textarea
            matInput
            formControlName="description"
            maxlength="500"
          ></textarea>
        </mat-form-field>
        <div class="actions">
          <!--
            !writable() first in the disabled expression, and it is not
            redundant: a disabled form's status is DISABLED, so form.invalid
            answers false and this control would stay pressable over a form
            nobody can type into.
          -->
          <button
            mat-flat-button
            color="primary"
            type="submit"
            [disabled]="
              !writable() || groupForm.invalid || categories.loading()
            "
          >
            {{ editingGroupId() ? 'Save group' : 'Add group' }}
          </button>
          @if (editingGroupId()) {
            <button mat-button type="button" (click)="cancelGroupEdit()">
              Cancel
            </button>
          }
        </div>
      </form>

      <form [formGroup]="categoryForm" (ngSubmit)="saveCategory()">
        <h2>{{ editingCategoryId() ? 'Edit category' : 'Add category' }}</h2>
        @if (!writable()) {
          <p class="reason">
            Adding and editing are off while this tab can’t read your account.
            Press Unlock in Settings to turn them back on.
          </p>
        }
        <mat-form-field>
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" maxlength="200" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Description</mat-label>
          <textarea
            matInput
            formControlName="description"
            maxlength="500"
          ></textarea>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Category group</mat-label>
          <mat-select formControlName="categoryGroupId">
            @for (group of categories.groups() ?? []; track group.id) {
              <!--
                The group's name is a word rather than a string, so the option
                renders it through the component that knows the four shapes one
                comes in. A member interpolated straight in here prints an
                object, and one collapsed to '' or a dash on the way past makes
                the picker claim something about the account when the truth is
                about this tab.
              -->
              <mat-option [value]="group.id">
                <app-narrative-value [value]="group.name" />
              </mat-option>
            }
          </mat-select>
          @if (editingCategoryId()) {
            <mat-hint>Move an existing category by dragging it below.</mat-hint>
          }
        </mat-form-field>
        <div class="actions">
          <button
            mat-flat-button
            color="primary"
            type="submit"
            [disabled]="
              !writable() || categoryForm.invalid || categories.loading()
            "
          >
            {{ editingCategoryId() ? 'Save category' : 'Add category' }}
          </button>
          @if (editingCategoryId()) {
            <button mat-button type="button" (click)="cancelCategoryEdit()">
              Cancel
            </button>
          }
        </div>
      </form>
    </div>

    @if (locked()) {
      <!--
        In place of the hierarchy, never over it and never as a redirect.
        Settings holds the way out, so nothing here may take a person off this
        screen.
      -->
      <app-locked-account-notice />
    } @else if (categories.groups(); as groups) {
      <div
        class="category-groups"
        cdkDropList
        [cdkDropListData]="groups"
        (cdkDropListDropped)="dropGroup($event)"
      >
        @for (group of groups; track group.id) {
          <section class="category-group" cdkDrag [cdkDragData]="group">
            <header class="group-heading">
              <div>
                <h2><app-narrative-value [value]="group.name" /></h2>
                @if (group.description; as description) {
                  <p><app-narrative-value [value]="description" /></p>
                }
                @if (!groupIsReadable(group)) {
                  <!--
                    The reason in the row, not in a tooltip: an explanation
                    nobody hears is not an explanation. Rewriting values that
                    cannot be read would seal a blank over words still sitting
                    in the columns, so the control is off rather than merely
                    unhelpful.
                  -->
                  <p class="reason">
                    This group’s words can’t be read here, so it can’t be
                    renamed.
                  </p>
                }
              </div>
              <div class="actions">
                <button mat-button type="button" cdkDragHandle>
                  Move group
                </button>
                <button
                  mat-button
                  type="button"
                  [disabled]="!groupIsReadable(group)"
                  (click)="editGroup(group)"
                >
                  Edit
                </button>
                <!--
                  Deleting stays available on the same row. A person looking at
                  a row they cannot read is entitled to remove it, and removing
                  is not rewriting.
                -->
                <button mat-button type="button" (click)="removeGroup(group)">
                  Delete
                </button>
              </div>
            </header>

            <div
              class="category-list"
              cdkDropList
              [id]="categoryListId(group.id)"
              [cdkDropListData]="categories.categoriesForGroup(group.id)"
              [cdkDropListConnectedTo]="categoryListIds()"
              (cdkDropListDropped)="dropCategory($event, group.id)"
            >
              @for (
                category of categories.categoriesForGroup(group.id);
                track category.id
              ) {
                <div class="category-row" cdkDrag [cdkDragData]="category">
                  <button mat-button type="button" cdkDragHandle>Move</button>
                  <span class="category-copy">
                    <strong>
                      <app-narrative-value [value]="category.name" />
                    </strong>
                    @if (category.description; as description) {
                      <small>
                        <app-narrative-value [value]="description" />
                      </small>
                    }
                    @if (!categoryIsReadable(category)) {
                      <small class="reason">
                        This category’s words can’t be read here, so it can’t be
                        renamed.
                      </small>
                    }
                  </span>
                  <button
                    mat-button
                    type="button"
                    [disabled]="!categoryIsReadable(category)"
                    (click)="editCategory(category)"
                  >
                    Edit
                  </button>
                  <button
                    mat-button
                    type="button"
                    (click)="removeCategory(category)"
                  >
                    Delete
                  </button>
                </div>
              } @empty {
                <span class="empty">Drop or add a category here.</span>
              }
            </div>
          </section>
        } @empty {
          <p>No category groups yet. Add one before creating categories.</p>
        }
      </div>
    } @else if (categories.loading()) {
      <!--
        The list is null at rest, in flight and after a failure, so the loading
        line is read off the published running state rather than off the absent
        value. "No category groups yet" belongs to a server that answered.
      -->
      <p class="reason">Reading your categories…</p>
    }
  `,
})
export class CategoriesComponent implements OnInit {
  protected readonly categories = inject(CategoriesService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly custody = inject(AccountKeyCustodyService);

  /**
   * Whether this screen may write.
   *
   * **Positive on purpose, and never `!== 'locked'`** — the head of this file
   * argues it. `unlocking` and any word added later are not `unlocked`, so they
   * arrive disabled, which is the direction a state nobody thought about has to
   * fail in.
   */
  protected readonly writable = computed(
    () => this.custody.status() === 'unlocked',
  );

  /**
   * Whether the notice replaces the hierarchy.
   *
   * `locked` exactly, and deliberately not {@link writable}'s complement: the
   * notice's way forward is "press Unlock in Settings", which is already wrong
   * for somebody whose unlock is running.
   */
  protected readonly locked = computed(
    () => this.custody.status() === 'locked',
  );

  protected readonly editingGroupId = signal<string | null>(null);
  protected readonly editingCategoryId = signal<string | null>(null);
  protected readonly categoryListIds = computed(() =>
    (this.categories.groups() ?? []).map((group) =>
      this.categoryListId(group.id),
    ),
  );

  protected readonly groupForm = this.formBuilder.nonNullable.group({
    // `nonBlank` beside `required`, not instead of it: `required` refuses an
    // empty control and admits `'   '`, and the trim that used to catch the
    // second is gone from the service on purpose.
    name: ['', [Validators.required, nonBlank, Validators.maxLength(200)]],
    // No `nonBlank` here, and that is the decision this screen makes: the note
    // is indexed by nothing, so a note of three spaces collides with nothing
    // and is a note somebody typed.
    description: ['', [Validators.maxLength(500)]],
  });

  protected readonly categoryForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, nonBlank, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(500)]],
    categoryGroupId: ['', [Validators.required]],
  });

  constructor() {
    // Disabled through the forms themselves, because Material's click-halt is
    // applied to anchors only: on a `<button>`, `disabledInteractive` leaves
    // the DOM `disabled` false and the click still arrives.
    effect(() => {
      if (this.writable()) {
        this.groupForm.enable({ emitEvent: false });
        this.categoryForm.enable({ emitEvent: false });
      } else {
        this.groupForm.disable({ emitEvent: false });
        this.categoryForm.disable({ emitEvent: false });
      }
    });
  }

  public ngOnInit(): void {
    this.categories.load();
  }

  protected saveGroup(): void {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here.
    if (!this.writable() || this.groupForm.invalid) {
      return;
    }

    // Handed over exactly as typed. The service seals this text and indexes the
    // same string; a `.trim()` on this line would make the two disagree, and
    // the `normalizeDescription` that used to fold a whitespace-only note onto
    // `null` is gone rather than moved one layer up.
    const value = this.groupForm.getRawValue();
    const id = this.editingGroupId();

    if (id === null) {
      void this.categories.addGroup(value);
    } else {
      void this.categories.updateGroup(id, value);
    }

    this.cancelGroupEdit();
  }

  protected editGroup(group: CategoryGroupView): void {
    // The gate is in the handler as well as on the control, the rule the
    // recovery-code hand-off states about its own acknowledgement: Material's
    // click-halt is applied to anchors only, so a disabled `<button>` still
    // receives the press that arrives here. Nothing is prefilled and nothing is
    // put into edit mode — a row with no text to show has no edit to start, and
    // the alternative is a blank field that seals over words still sitting in
    // the columns. The narrowing below is what the compiler needs, which is why
    // dropping this line does not compile rather than merely reddening.
    if (!categoryGroupIsReadable(group)) {
      return;
    }

    this.editingGroupId.set(group.id);
    this.groupForm.setValue({
      description: group.description === null ? '' : group.description.value,
      name: group.name.value,
    });
  }

  protected cancelGroupEdit(): void {
    this.editingGroupId.set(null);
    this.groupForm.reset({ name: '', description: '' });
  }

  protected removeGroup(group: CategoryGroupView): void {
    this.categories.removeGroup(group.id);
  }

  protected saveCategory(): void {
    if (!this.writable() || this.categoryForm.invalid) {
      return;
    }

    const value = this.categoryForm.getRawValue();
    const id = this.editingCategoryId();

    if (id === null) {
      void this.categories.addCategory(value);
    } else {
      void this.categories.updateCategory(id, {
        description: value.description,
        name: value.name,
      });
    }

    this.cancelCategoryEdit();
  }

  protected editCategory(category: CategoryView): void {
    if (!categoryIsReadable(category)) {
      return;
    }

    this.editingCategoryId.set(category.id);
    this.categoryForm.setValue({
      categoryGroupId: category.categoryGroupId,
      description:
        category.description === null ? '' : category.description.value,
      name: category.name.value,
    });
    this.categoryForm.controls.categoryGroupId.disable();
  }

  protected cancelCategoryEdit(): void {
    this.editingCategoryId.set(null);
    this.categoryForm.controls.categoryGroupId.enable();
    this.categoryForm.reset({
      name: '',
      description: '',
      categoryGroupId: '',
    });
  }

  protected removeCategory(category: CategoryView): void {
    this.categories.removeCategory(category.id);
  }

  protected dropGroup(
    event: CdkDragDrop<
      readonly CategoryGroupView[],
      readonly CategoryGroupView[],
      CategoryGroupView
    >,
  ): void {
    this.categories.moveGroup(event.item.data.id, event.currentIndex);
  }

  protected dropCategory(
    event: CdkDragDrop<
      readonly CategoryView[],
      readonly CategoryView[],
      CategoryView
    >,
    categoryGroupId: string,
  ): void {
    this.categories.placeCategory(
      event.item.data.id,
      categoryGroupId,
      event.currentIndex,
    );
  }

  protected categoryListId(categoryGroupId: string): string {
    return `category-list-${categoryGroupId}`;
  }

  // The two rename gates, re-exported for the template because a template
  // cannot import. Each is the imported predicate and never a second copy of
  // its condition: a copy here would be the one that drifts, and the drift is
  // silent in the direction that loses a note.
  protected groupIsReadable(group: CategoryGroupView): boolean {
    return categoryGroupIsReadable(group);
  }

  protected categoryIsReadable(category: CategoryView): boolean {
    return categoryIsReadable(category);
  }
}
