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
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { CategoryGroupDto } from '@app-core/api/category-groups-api.service';
import { CategoryDto } from '@app-core/api/categories-api.service';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CategoriesService } from './categories.service';

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
          <button
            mat-flat-button
            color="primary"
            type="submit"
            [disabled]="groupForm.invalid || categories.loading()"
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
            @for (group of categories.groups(); track group.id) {
              <mat-option [value]="group.id">{{ group.name }}</mat-option>
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
            [disabled]="categoryForm.invalid || categories.loading()"
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

    <div>
      <div
        class="category-groups"
        cdkDropList
        [cdkDropListData]="categories.groups()"
        (cdkDropListDropped)="dropGroup($event)"
      >
        @for (group of categories.groups(); track group.id) {
          <section class="category-group" cdkDrag [cdkDragData]="group">
            <header class="group-heading">
              <div>
                <h2>{{ group.name }}</h2>
                @if (group.description) {
                  <p>{{ group.description }}</p>
                }
              </div>
              <div class="actions">
                <button mat-button type="button" cdkDragHandle>
                  Move group
                </button>
                <button mat-button type="button" (click)="editGroup(group)">
                  Edit
                </button>
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
                    <strong>{{ category.name }}</strong>
                    @if (category.description) {
                      <small>{{ category.description }}</small>
                    }
                  </span>
                  <button
                    mat-button
                    type="button"
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
    </div>
  `,
})
export class CategoriesComponent implements OnInit {
  protected readonly categories = inject(CategoriesService);
  private readonly formBuilder = inject(FormBuilder);
  protected readonly editingGroupId = signal<string | null>(null);
  protected readonly editingCategoryId = signal<string | null>(null);
  protected readonly categoryListIds = computed(() =>
    this.categories.groups().map((group) => this.categoryListId(group.id)),
  );

  protected readonly groupForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(500)]],
  });

  protected readonly categoryForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(500)]],
    categoryGroupId: ['', [Validators.required]],
  });

  public ngOnInit(): void {
    this.categories.load();
  }

  protected saveGroup(): void {
    if (this.groupForm.invalid) {
      return;
    }

    const value = this.groupForm.getRawValue();
    const request = {
      name: value.name.trim(),
      description: this.normalizeDescription(value.description),
    };
    const id = this.editingGroupId();
    if (id) {
      this.categories.updateGroup(id, request);
    } else {
      this.categories.addGroup(request);
    }
    this.cancelGroupEdit();
  }

  protected editGroup(group: CategoryGroupDto): void {
    this.editingGroupId.set(group.id);
    this.groupForm.setValue({
      name: group.name,
      description: group.description ?? '',
    });
  }

  protected cancelGroupEdit(): void {
    this.editingGroupId.set(null);
    this.groupForm.reset({ name: '', description: '' });
  }

  protected removeGroup(group: CategoryGroupDto): void {
    this.categories.removeGroup(group.id);
  }

  protected saveCategory(): void {
    if (this.categoryForm.invalid) {
      return;
    }

    const value = this.categoryForm.getRawValue();
    const request = {
      name: value.name.trim(),
      description: this.normalizeDescription(value.description),
    };
    const id = this.editingCategoryId();
    if (id) {
      this.categories.updateCategory(id, request);
    } else {
      this.categories.addCategory({
        ...request,
        categoryGroupId: value.categoryGroupId,
      });
    }
    this.cancelCategoryEdit();
  }

  protected editCategory(category: CategoryDto): void {
    this.editingCategoryId.set(category.id);
    this.categoryForm.setValue({
      name: category.name,
      description: category.description ?? '',
      categoryGroupId: category.categoryGroupId,
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

  protected removeCategory(category: CategoryDto): void {
    this.categories.removeCategory(category.id);
  }

  protected dropGroup(
    event: CdkDragDrop<
      CategoryGroupDto[],
      CategoryGroupDto[],
      CategoryGroupDto
    >,
  ): void {
    this.categories.moveGroup(event.item.data.id, event.currentIndex);
  }

  protected dropCategory(
    event: CdkDragDrop<CategoryDto[], CategoryDto[], CategoryDto>,
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

  private normalizeDescription(description: string): string | null {
    const normalized = description.trim();
    return normalized || null;
  }
}
