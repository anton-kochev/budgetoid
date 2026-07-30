import { TestBed } from '@angular/core/testing';
import { CategoryGroupsApiService } from '@app-core/api/category-groups-api.service';
import { CategoriesApiService } from '@app-core/api/categories-api.service';
import { PayeesApiService } from '@app-core/api/payees-api.service';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TransactionsService } from './transactions.service';

class TransactionsApiStub {
  public getTransactions = vi.fn(() => of({ items: [] }));
  public createTransaction = vi.fn();
}

class PayeesApiStub {
  public getPayees = vi.fn(() => of({ items: [] }));
}

class CategoryGroupsApiStub {
  public getCategoryGroups = vi.fn(() =>
    of({
      items: [
        {
          id: 'group-1',
          name: 'Essentials',
          description: null,
          position: 0,
        },
      ],
    }),
  );
}

class CategoriesApiStub {
  public getCategories = vi.fn(() =>
    of({
      items: [
        {
          id: 'category-1',
          name: 'Groceries',
          description: null,
          categoryGroupId: 'group-1',
          categoryGroupName: 'Essentials',
          position: 0,
        },
      ],
    }),
  );
}

describe('TransactionsService', () => {
  let service: TransactionsService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        TransactionsService,
        { provide: TransactionsApiService, useClass: TransactionsApiStub },
        { provide: PayeesApiService, useClass: PayeesApiStub },
        { provide: CategoryGroupsApiService, useClass: CategoryGroupsApiStub },
        { provide: CategoriesApiService, useClass: CategoriesApiStub },
      ],
    });
    service = TestBed.inject(TransactionsService);
  });

  it('loads ordered category groups and categories for the picker', () => {
    // Act
    service.loadCategories();

    // Assert
    expect(service.categoryGroups().map((group) => group.name)).toEqual([
      'Essentials',
    ]);
    expect(service.categories().map((category) => category.name)).toEqual([
      'Groceries',
    ]);
  });

  it('returns categories belonging to a selected group', () => {
    // Arrange
    service.loadCategories();

    // Act
    const categories = service.categoriesForGroup('group-1');

    // Assert
    expect(categories.map((category) => category.name)).toEqual(['Groceries']);
  });
});
