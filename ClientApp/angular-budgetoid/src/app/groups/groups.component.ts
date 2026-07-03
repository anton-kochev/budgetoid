import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { GroupDto } from '@app-core/api/groups-api.service';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { GroupsService } from './groups.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatListModule,
  ],
  styles: `
    form {
      display: grid;
      gap: 1rem;
      max-width: 32rem;
      margin-bottom: 2rem;
    }

    .actions {
      display: flex;
      gap: 0.5rem;
    }
  `,
  template: `
    <h1>Groups</h1>

    <form [formGroup]="form" (ngSubmit)="save()">
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
          [disabled]="form.invalid || groups.loading()"
        >
          {{ editingId() ? 'Save group' : 'Add group' }}
        </button>
        @if (editingId()) {
          <button mat-button type="button" (click)="cancelEdit()">
            Cancel
          </button>
        }
      </div>
    </form>

    <mat-list>
      @for (group of groups.groups(); track group.id) {
        <mat-list-item>
          <span matListItemTitle>{{ group.name }}</span>
          @if (group.description) {
            <span matListItemLine>{{ group.description }}</span>
          }
          <span matListItemMeta class="actions">
            <button mat-button type="button" (click)="edit(group)">Edit</button>
            <button mat-button type="button" (click)="remove(group)">
              Delete
            </button>
          </span>
        </mat-list-item>
      } @empty {
        <mat-list-item>No groups yet.</mat-list-item>
      }
    </mat-list>
  `,
})
export class GroupsComponent implements OnInit {
  protected readonly groups = inject(GroupsService);
  private readonly formBuilder = inject(FormBuilder);
  protected readonly editingId = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(500)]],
  });

  public ngOnInit(): void {
    this.groups.load();
  }

  protected save(): void {
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();
    const description = value.description.trim();
    const request = {
      name: value.name,
      description: description ? description : null,
    };
    const editingId = this.editingId();

    if (editingId) {
      this.groups.update(editingId, request);
    } else {
      this.groups.add(request);
    }

    this.cancelEdit();
  }

  protected edit(group: GroupDto): void {
    this.editingId.set(group.id);
    this.form.setValue({
      name: group.name,
      description: group.description ?? '',
    });
  }

  protected cancelEdit(): void {
    this.editingId.set(null);
    this.form.reset({
      name: '',
      description: '',
    });
  }

  protected remove(group: GroupDto): void {
    this.groups.remove(group.id);
  }
}
