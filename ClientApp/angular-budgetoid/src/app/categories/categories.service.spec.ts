import { TestBed } from '@angular/core/testing';
import {
  CategoryGroupDto,
  CategoryGroupListResponse,
  CategoryGroupsApiService,
} from '@app-core/api/category-groups-api.service';
import {
  CategoriesApiService,
  CategoryDto,
  CategoryListResponse,
} from '@app-core/api/categories-api.service';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CategoriesService } from './categories.service';

const essentials: CategoryGroupDto = {
  id: 'group-1',
  name: 'Essentials',
  description: null,
  position: 0,
};
const lifestyle: CategoryGroupDto = {
  id: 'group-2',
  name: 'Lifestyle',
  description: null,
  position: 1,
};
const groceries: CategoryDto = {
  id: 'category-1',
  name: 'Groceries',
  description: null,
  categoryGroupId: essentials.id,
  categoryGroupName: essentials.name,
  position: 0,
};

class CategoryGroupsApiStub {
  public getCategoryGroups = vi.fn(
    (): Observable<CategoryGroupListResponse> => of({ items: [] }),
  );
  public createCategoryGroup = vi.fn(
    (): Observable<CategoryGroupDto> => of(lifestyle),
  );
  public updateCategoryGroup = vi.fn(() => of(undefined));
  public moveCategoryGroup = vi.fn(() => of(undefined));
  public deleteCategoryGroup = vi.fn(() => of(undefined));
}

class CategoriesApiStub {
  public getCategories = vi.fn(
    (): Observable<CategoryListResponse> => of({ items: [] }),
  );
  public createCategory = vi.fn((): Observable<CategoryDto> => of(groceries));
  public updateCategory = vi.fn(() => of(undefined));
  public placeCategory = vi.fn(() => of(undefined));
  public deleteCategory = vi.fn(() => of(undefined));
}

describe('CategoriesService', () => {
  let service: CategoriesService;
  let groupApi: CategoryGroupsApiStub;
  let categoryApi: CategoriesApiStub;

  beforeEach(() => {
    groupApi = new CategoryGroupsApiStub();
    categoryApi = new CategoriesApiStub();
    TestBed.configureTestingModule({
      providers: [
        CategoriesService,
        { provide: CategoryGroupsApiService, useValue: groupApi },
        { provide: CategoriesApiService, useValue: categoryApi },
      ],
    });
    service = TestBed.inject(CategoriesService);
  });

  it('loads groups and categories in persisted API order', () => {
    // Arrange
    groupApi.getCategoryGroups.mockReturnValue(
      of({ items: [lifestyle, essentials] }),
    );
    categoryApi.getCategories.mockReturnValue(of({ items: [groceries] }));

    // Act
    service.load();

    // Assert
    expect(service.groups()).toEqual([lifestyle, essentials]);
    expect(service.categories()).toEqual([groceries]);
    expect(service.loading()).toBe(false);
  });

  it('appends a created group without alphabetically sorting it', () => {
    // Arrange
    groupApi.getCategoryGroups.mockReturnValue(of({ items: [essentials] }));
    groupApi.createCategoryGroup.mockReturnValue(of(lifestyle));
    service.load();

    // Act
    service.addGroup({ name: lifestyle.name, description: null });

    // Assert
    expect(service.groups()).toEqual([essentials, lifestyle]);
  });

  it('moves a category across groups and reindexes both groups locally', () => {
    // Arrange
    const utilities: CategoryDto = {
      ...groceries,
      id: 'category-2',
      name: 'Utilities',
      position: 1,
    };
    const dining: CategoryDto = {
      ...groceries,
      id: 'category-3',
      name: 'Dining Out',
      categoryGroupId: lifestyle.id,
      categoryGroupName: lifestyle.name,
      position: 0,
    };
    groupApi.getCategoryGroups.mockReturnValue(
      of({ items: [essentials, lifestyle] }),
    );
    categoryApi.getCategories.mockReturnValue(
      of({ items: [groceries, utilities, dining] }),
    );
    service.load();

    // Act
    service.placeCategory(groceries.id, lifestyle.id, 0);

    // Assert
    expect(categoryApi.placeCategory).toHaveBeenCalledWith(groceries.id, {
      categoryGroupId: lifestyle.id,
      position: 0,
    });
    expect(service.categories()).toEqual([
      { ...utilities, position: 0 },
      {
        ...groceries,
        categoryGroupId: lifestyle.id,
        categoryGroupName: lifestyle.name,
        position: 0,
      },
      { ...dining, position: 1 },
    ]);
  });

  it('reorders within one group without duplicating categories', () => {
    // Arrange
    const utilities: CategoryDto = {
      ...groceries,
      id: 'category-2',
      name: 'Utilities',
      position: 1,
    };
    groupApi.getCategoryGroups.mockReturnValue(of({ items: [essentials] }));
    categoryApi.getCategories.mockReturnValue(
      of({ items: [groceries, utilities] }),
    );
    service.load();

    // Act
    service.placeCategory(utilities.id, essentials.id, 0);

    // Assert
    expect(service.categories()).toEqual([
      { ...utilities, position: 0 },
      { ...groceries, position: 1 },
    ]);
  });

  it('resets loading when a request fails', () => {
    // Arrange
    const pending = new Subject<{ items: CategoryGroupDto[] }>();
    groupApi.getCategoryGroups.mockReturnValue(pending.asObservable());
    categoryApi.getCategories.mockReturnValue(
      throwError(() => new Error('failed')),
    );
    vi.spyOn(console, 'error').mockImplementation(() => undefined);

    // Act
    service.load();

    // Assert
    expect(service.loading()).toBe(false);
  });
});
