import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CategoryGroupDto } from '@app-core/api/category-groups-api.service';
import { CategoryDto } from '@app-core/api/categories-api.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CategoriesComponent } from './categories.component';
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

class CategoriesServiceStub {
  public readonly groups = signal([essentials, lifestyle]).asReadonly();
  public readonly categories = signal([groceries]).asReadonly();
  public readonly loading = signal(false).asReadonly();
  public load = vi.fn();
  public addGroup = vi.fn();
  public updateGroup = vi.fn();
  public moveGroup = vi.fn();
  public removeGroup = vi.fn();
  public addCategory = vi.fn();
  public updateCategory = vi.fn();
  public placeCategory = vi.fn();
  public removeCategory = vi.fn();
  public categoriesForGroup = vi.fn((groupId: string) =>
    groupId === essentials.id ? [groceries] : [],
  );
}

describe('CategoriesComponent', () => {
  let service: CategoriesServiceStub;
  let component: CategoriesComponent;

  beforeEach(async () => {
    service = new CategoriesServiceStub();
    await TestBed.configureTestingModule({
      imports: [CategoriesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CategoriesService, useValue: service },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CategoriesComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads the hierarchy on initialization', () => {
    // Assert
    expect(service.load).toHaveBeenCalledOnce();
  });

  it('persists group drag-and-drop position', () => {
    // Arrange
    const event = {
      item: { data: lifestyle },
      previousIndex: 1,
      currentIndex: 0,
    } as unknown as CdkDragDrop<CategoryGroupDto[]>;

    // Act
    invoke(component, 'dropGroup', event);

    // Assert
    expect(service.moveGroup).toHaveBeenCalledWith(lifestyle.id, 0);
  });

  it('persists category drag-and-drop placement', () => {
    // Arrange
    const event = {
      item: { data: groceries },
      currentIndex: 0,
    } as unknown as CdkDragDrop<CategoryDto[]>;

    // Act
    invoke(component, 'dropCategory', event, lifestyle.id);

    // Assert
    expect(service.placeCategory).toHaveBeenCalledWith(
      groceries.id,
      lifestyle.id,
      0,
    );
  });
});

function invoke(
  component: CategoriesComponent,
  method: 'dropGroup' | 'dropCategory',
  ...args: unknown[]
): void {
  const callable = component as unknown as Record<
    'dropGroup' | 'dropCategory',
    (...methodArgs: unknown[]) => void
  >;
  callable[method](...args);
}
