import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormGroup } from '@angular/forms';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AccountsService } from '../accounts/accounts.service';
import { TransactionsComponent } from './transactions.component';
import { TransactionsService } from './transactions.service';

class AccountsServiceStub {
  public readonly accounts = signal([
    {
      id: 'account-1',
      name: 'Checking',
      type: 'Checking' as const,
      openingBalance: 0,
      currencyCode: 'USD',
      currencySymbol: '$',
    },
  ]).asReadonly();
  public readonly loading = signal(false).asReadonly();
  public load = vi.fn();
}

class TransactionsServiceStub {
  public readonly transactions = signal([
    {
      id: 'transaction-1',
      amount: -20,
      date: '2026-07-14',
      description: 'Food',
      createdAtUtc: '2026-07-14T10:00:00Z',
      accountId: 'account-1',
      accountName: 'Checking',
      currencyCode: 'USD',
      currencySymbol: '$',
      payeeId: null,
      payeeName: null,
      categoryId: 'category-1',
      categoryName: 'Groceries',
      categoryGroupId: 'group-1',
      categoryGroupName: 'Essentials',
    },
  ]).asReadonly();
  public readonly payees = signal([]).asReadonly();
  public readonly categoryGroups = signal([
    {
      id: 'group-1',
      name: 'Essentials',
      description: null,
      position: 0,
    },
  ]).asReadonly();
  public readonly categories = signal([
    {
      id: 'category-1',
      name: 'Groceries',
      description: null,
      categoryGroupId: 'group-1',
      categoryGroupName: 'Essentials',
      position: 0,
    },
  ]).asReadonly();
  public readonly loading = signal(false).asReadonly();
  public load = vi.fn();
  public loadPayees = vi.fn();
  public loadCategories = vi.fn();
  public categoriesForGroup = vi.fn(() => this.categories());
  public add = vi.fn();
}

describe('TransactionsComponent', () => {
  let transactionService: TransactionsServiceStub;
  let fixture: ComponentFixture<TransactionsComponent>;

  beforeEach(async () => {
    transactionService = new TransactionsServiceStub();
    await TestBed.configureTestingModule({
      imports: [TransactionsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: AccountsService, useClass: AccountsServiceStub },
        { provide: TransactionsService, useValue: transactionService },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(TransactionsComponent);
    fixture.detectChanges();
  });

  it('loads categories for the grouped picker', () => {
    // Assert
    expect(transactionService.loadCategories).toHaveBeenCalledOnce();
  });

  it('submits only the selected category id for categorization', () => {
    // Arrange
    const exposed = componentFixtureApi(fixture.componentInstance);
    exposed.form.setValue({
      amount: -20,
      date: new Date(2026, 6, 14),
      accountId: 'account-1',
      description: 'Food',
      payee: '',
      categoryId: 'category-1',
    });

    // Act
    exposed.add();

    // Assert
    expect(transactionService.add).toHaveBeenCalledWith({
      amount: -20,
      date: '2026-07-14',
      accountId: 'account-1',
      description: 'Food',
      categoryId: 'category-1',
    });
  });

  it('renders group and category names for categorized transactions', () => {
    // Act
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    // Assert
    expect(text).toContain('Essentials · Groceries');
  });
});

function componentFixtureApi(component: TransactionsComponent): {
  form: FormGroup;
  add: () => void;
} {
  return component as unknown as { form: FormGroup; add: () => void };
}
