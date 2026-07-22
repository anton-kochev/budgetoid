import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { CategoriesApiService } from './categories-api.service';
import { CategoryGroupsApiService } from './category-groups-api.service';

describe('category APIs', () => {
  let http: HttpTestingController;
  let groups: CategoryGroupsApiService;
  let categories: CategoriesApiService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: 'https://api.test' }) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    groups = TestBed.inject(CategoryGroupsApiService);
    categories = TestBed.inject(CategoriesApiService);
  });

  afterEach(() => http.verify());

  it('sends a typed group position patch as application/json', () => {
    // Act
    groups.moveCategoryGroup('group-1', { position: 2 }).subscribe();
    const request = http.expectOne(
      'https://api.test/api/category-groups/group-1/position',
    );

    // Assert
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ position: 2 });
    expect(request.request.headers.get('Content-Type')).toBe(
      'application/json',
    );
    request.flush(null);
  });

  it('sends a typed category placement patch', () => {
    // Act
    categories
      .placeCategory('category-1', {
        categoryGroupId: 'group-2',
        position: 0,
      })
      .subscribe();
    const request = http.expectOne(
      'https://api.test/api/categories/category-1/placement',
    );

    // Assert
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({
      categoryGroupId: 'group-2',
      position: 0,
    });
    request.flush(null);
  });
});
