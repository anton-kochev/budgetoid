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
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
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
    NarrativeValueComponent,
  ],
  styles: `
    :host {
      display: block;
      padding: 1.5rem 2rem;
    }

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
          @for (account of accounts.accounts() ?? []; track account.id) {
            <mat-option [value]="account.id">
              <app-narrative-value [value]="account.name" />
            </mat-option>
          }
        </mat-select>
      </mat-form-field>

      <!--
        \`?.length === 0\` and never \`(… ?? []).length === 0\`: a null list is
        "no answer yet", and saying "create an account" over a read that never
        landed is a claim about the budget rather than about the request.
      -->
      @if (!accounts.loading() && accounts.accounts()?.length === 0) {
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
        <mat-label>Category</mat-label>
        <mat-select formControlName="categoryId">
          <mat-option [value]="''">None</mat-option>
          @for (group of transactions.categoryGroups(); track group.id) {
            <mat-optgroup [label]="group.name">
              @for (
                category of transactions.categoriesForGroup(group.id);
                track category.id
              ) {
                <mat-option [value]="category.id">
                  {{ category.name }}
                </mat-option>
              }
            </mat-optgroup>
          }
        </mat-select>
      </mat-form-field>

      <button
        mat-flat-button
        color="primary"
        type="submit"
        [disabled]="form.invalid || (accounts.accounts()?.length ?? 0) === 0"
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
            }}{{
              transaction.categoryGroupName && transaction.categoryName
                ? transaction.categoryGroupName +
                  ' · ' +
                  transaction.categoryName +
                  ' · '
                : ''
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
    categoryId: [''],
  });

  public ngOnInit(): void {
    this.accounts.load();
    this.transactions.load();
    this.transactions.loadPayees();
    this.transactions.loadCategories();
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
    const categoryId = value.categoryId;

    this.transactions.add({
      amount: value.amount,
      date: this.toDateOnlyString(value.date),
      accountId: value.accountId,
      description: value.description,
      ...(payeeName ? { payeeName } : {}),
      ...(categoryId ? { categoryId } : {}),
    });
    this.form.reset({
      amount: 0,
      date: new Date(),
      accountId: '',
      description: '',
      payee: '',
      categoryId: '',
    });
  }

  private toDateOnlyString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');

    return `${year}-${month}-${day}`;
  }
}
