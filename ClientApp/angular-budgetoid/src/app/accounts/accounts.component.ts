import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AccountDto, AccountType } from '@app-core/api/account-api.service';
import {
  CurrencyApiService,
  CurrencyDto,
} from '@app-core/api/currency-api.service';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatSelectModule } from '@angular/material/select';
import { AccountsService } from './accounts.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatListModule,
    MatSelectModule,
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
    <h1>Accounts</h1>

    <form [formGroup]="form" (ngSubmit)="save()">
      <mat-form-field>
        <mat-label>Name</mat-label>
        <input matInput formControlName="name" maxlength="200" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Type</mat-label>
        <mat-select formControlName="type">
          @for (type of accountTypes; track type) {
            <mat-option [value]="type">{{ type }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <mat-form-field>
        <mat-label>Opening balance</mat-label>
        <input
          matInput
          type="number"
          step="0.01"
          formControlName="openingBalance"
        />
      </mat-form-field>

      @if (!editingId()) {
        <mat-form-field>
          <mat-label>Currency</mat-label>
          <mat-select formControlName="currencyCode" required>
            @for (currency of currencies(); track currency.code) {
              <mat-option [value]="currency.code">
                {{ currency.code }} · {{ currency.name }} ({{
                  currency.symbol
                }})
              </mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

      <div class="actions">
        <button
          mat-flat-button
          color="primary"
          type="submit"
          [disabled]="form.invalid || accounts.loading()"
        >
          {{ editingId() ? 'Save account' : 'Add account' }}
        </button>
        @if (editingId()) {
          <button mat-button type="button" (click)="cancelEdit()">
            Cancel
          </button>
        }
      </div>
    </form>

    <mat-list>
      @for (account of accounts.accounts(); track account.id) {
        <mat-list-item>
          <span matListItemTitle>{{ account.name }}</span>
          <span matListItemLine>
            {{ account.type }} · {{ account.currencyCode }} · Opening:
            {{ account.currencySymbol }}{{ account.openingBalance }}
          </span>
          <span matListItemMeta class="actions">
            <button mat-button type="button" (click)="edit(account)">
              Edit
            </button>
            <button mat-button type="button" (click)="remove(account)">
              Delete
            </button>
          </span>
        </mat-list-item>
      } @empty {
        <mat-list-item>No accounts yet.</mat-list-item>
      }
    </mat-list>
  `,
})
export class AccountsComponent implements OnInit {
  protected readonly accounts = inject(AccountsService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly currencyApi = inject(CurrencyApiService);
  protected readonly accountTypes: AccountType[] = [
    'Checking',
    'Savings',
    'Cash',
    'CreditCard',
  ];
  protected readonly editingId = signal<string | null>(null);
  protected readonly currencies = signal<CurrencyDto[]>([]);

  protected readonly form = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    type: ['Checking' as AccountType, [Validators.required]],
    openingBalance: [0, [Validators.required]],
    currencyCode: ['USD', [Validators.required]],
  });

  public ngOnInit(): void {
    this.accounts.load();
    this.currencyApi
      .getCurrencies()
      .subscribe((response) => this.currencies.set(response.items));
  }

  protected save(): void {
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();
    const request = {
      name: value.name,
      type: value.type,
      openingBalance: value.openingBalance,
    };
    const editingId = this.editingId();

    if (editingId) {
      this.accounts.update(editingId, request);
    } else {
      this.accounts.add({ ...request, currencyCode: value.currencyCode });
    }

    this.cancelEdit();
  }

  protected edit(account: AccountDto): void {
    this.editingId.set(account.id);
    this.form.setValue({
      name: account.name,
      type: account.type,
      openingBalance: account.openingBalance,
      currencyCode: account.currencyCode,
    });
  }

  protected cancelEdit(): void {
    this.editingId.set(null);
    this.form.reset({
      name: '',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
  }

  protected remove(account: AccountDto): void {
    this.accounts.remove(account.id);
  }
}
