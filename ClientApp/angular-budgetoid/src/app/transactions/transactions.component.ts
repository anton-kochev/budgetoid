import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { PayeeDto } from '@app-core/api/payees-api.service';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatSelectModule } from '@angular/material/select';
import { AccountsService } from '../accounts/accounts.service';
import { TransactionsService } from './transactions.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [provideNativeDateAdapter()],
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatDatepickerModule,
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
  `,
  template: `
    <h1>Transactions</h1>

    <form [formGroup]="form" (ngSubmit)="add()">
      <mat-form-field>
        <mat-label>Amount</mat-label>
        <input matInput type="number" step="0.01" formControlName="amount" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Date</mat-label>
        <input matInput [matDatepicker]="picker" formControlName="date" />
        <mat-datepicker-toggle matIconSuffix [for]="picker" />
        <mat-datepicker #picker />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Account</mat-label>
        <mat-select formControlName="accountId">
          @for (account of accounts.accounts(); track account.id) {
            <mat-option [value]="account.id">{{ account.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      @if (!accounts.loading() && accounts.accounts().length === 0) {
        <p>Create an account before adding transactions.</p>
      }

      <mat-form-field>
        <mat-label>Description</mat-label>
        <input matInput formControlName="description" maxlength="500" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Payee</mat-label>
        <input
          matInput
          formControlName="payee"
          maxlength="200"
          [matAutocomplete]="payeeAutocomplete"
        />
        <mat-autocomplete #payeeAutocomplete="matAutocomplete">
          @for (payee of filteredPayees(); track payee.id) {
            <mat-option [value]="payee.name">{{ payee.name }}</mat-option>
          }
        </mat-autocomplete>
      </mat-form-field>

      <mat-form-field>
        <mat-label>Group</mat-label>
        <mat-select formControlName="groupId">
          <mat-option [value]="''">None</mat-option>
          @for (group of transactions.groups(); track group.id) {
            <mat-option [value]="group.id">{{ group.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <button
        mat-flat-button
        color="primary"
        type="submit"
        [disabled]="form.invalid || accounts.accounts().length === 0"
      >
        Add transaction
      </button>
    </form>

    <mat-list>
      @for (transaction of transactions.transactions(); track transaction.id) {
        <mat-list-item>
          <span matListItemTitle>{{ transaction.description }}</span>
          <span matListItemLine>
            {{ transaction.accountName }} ·
            {{ transaction.payeeName ? transaction.payeeName + ' · ' : ''
            }}{{ transaction.groupName ? transaction.groupName + ' · ' : ''
            }}{{ transaction.date }} · {{ transaction.currencySymbol
            }}{{ transaction.amount }}
          </span>
        </mat-list-item>
      } @empty {
        <mat-list-item>No transactions yet.</mat-list-item>
      }
    </mat-list>
  `,
})
export class TransactionsComponent implements OnInit {
  protected readonly transactions = inject(TransactionsService);
  protected readonly accounts = inject(AccountsService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly form = this.formBuilder.nonNullable.group({
    amount: [0, [Validators.required]],
    date: [new Date(), [Validators.required]],
    accountId: ['', [Validators.required]],
    description: ['', [Validators.maxLength(500)]],
    payee: ['', [Validators.maxLength(200)]],
    groupId: [''],
  });

  public ngOnInit(): void {
    this.accounts.load();
    this.transactions.load();
    this.transactions.loadPayees();
    this.transactions.loadGroups();
  }

  protected filteredPayees(): PayeeDto[] {
    const filter = this.form.controls.payee.value.trim().toLocaleLowerCase();

    if (!filter) {
      return this.transactions.payees();
    }

    return this.transactions
      .payees()
      .filter((payee) => payee.name.toLocaleLowerCase().includes(filter));
  }

  protected add(): void {
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();

    const payeeName = value.payee.trim();
    const groupId = value.groupId;

    this.transactions.add({
      amount: value.amount,
      date: this.toDateOnlyString(value.date),
      accountId: value.accountId,
      description: value.description,
      ...(payeeName ? { payeeName } : {}),
      ...(groupId ? { groupId } : {}),
    });
    this.form.reset({
      amount: 0,
      date: new Date(),
      accountId: '',
      description: '',
      payee: '',
      groupId: '',
    });
  }

  private toDateOnlyString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');

    return `${year}-${month}-${day}`;
  }
}
