import { Injectable, inject, signal } from '@angular/core';
import {
  CategoryGroupDto,
  CategoryGroupsApiService,
  CreateCategoryGroupRequest,
  UpdateCategoryGroupRequest,
} from '@app-core/api/category-groups-api.service';
import {
  CategoriesApiService,
  CategoryDto,
  CreateCategoryRequest,
  UpdateCategoryRequest,
} from '@app-core/api/categories-api.service';
import { EMPTY, catchError, finalize, forkJoin, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class CategoriesService {
  private readonly categoryGroupsApi = inject(CategoryGroupsApiService);
  private readonly categoriesApi = inject(CategoriesApiService);
  private readonly groupsSignal = signal<CategoryGroupDto[]>([]);
  private readonly categoriesSignal = signal<CategoryDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly groups = this.groupsSignal.asReadonly();
  public readonly categories = this.categoriesSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load(): void {
    this.loadingSignal.set(true);
    forkJoin({
      groups: this.categoryGroupsApi.getCategoryGroups(),
      categories: this.categoriesApi.getCategories(),
    })
      .pipe(
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe(({ groups, categories }) => {
        this.groupsSignal.set(groups.items);
        this.categoriesSignal.set(categories.items);
      });
  }

  public addGroup(request: CreateCategoryGroupRequest): void {
    this.loadingSignal.set(true);
    this.categoryGroupsApi
      .createCategoryGroup(request)
      .pipe(
        tap((created) =>
          this.groupsSignal.update((groups) => [...groups, created]),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public updateGroup(id: string, request: UpdateCategoryGroupRequest): void {
    this.loadingSignal.set(true);
    this.categoryGroupsApi
      .updateCategoryGroup(id, request)
      .pipe(
        tap(() => {
          this.groupsSignal.update((groups) =>
            groups.map((group) =>
              group.id === id
                ? {
                    ...group,
                    name: request.name,
                    description: request.description,
                  }
                : group,
            ),
          );
          this.categoriesSignal.update((categories) =>
            categories.map((category) =>
              category.categoryGroupId === id
                ? { ...category, categoryGroupName: request.name }
                : category,
            ),
          );
        }),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public moveGroup(id: string, position: number): void {
    this.loadingSignal.set(true);
    this.categoryGroupsApi
      .moveCategoryGroup(id, { position })
      .pipe(
        tap(() => {
          const reordered = this.moveItem(this.groupsSignal(), id, position);
          this.groupsSignal.set(reordered);
          this.categoriesSignal.update((categories) =>
            this.sortCategories(categories, reordered),
          );
        }),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public removeGroup(id: string): void {
    this.loadingSignal.set(true);
    this.categoryGroupsApi
      .deleteCategoryGroup(id)
      .pipe(
        tap(() =>
          this.groupsSignal.update((groups) =>
            groups
              .filter((group) => group.id !== id)
              .map((group, position) => ({ ...group, position })),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public addCategory(request: CreateCategoryRequest): void {
    this.loadingSignal.set(true);
    this.categoriesApi
      .createCategory(request)
      .pipe(
        tap((created) =>
          this.categoriesSignal.update((categories) =>
            this.sortCategories([...categories, created], this.groupsSignal()),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public updateCategory(id: string, request: UpdateCategoryRequest): void {
    this.loadingSignal.set(true);
    this.categoriesApi
      .updateCategory(id, request)
      .pipe(
        tap(() =>
          this.categoriesSignal.update((categories) =>
            categories.map((category) =>
              category.id === id
                ? {
                    ...category,
                    name: request.name,
                    description: request.description,
                  }
                : category,
            ),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public placeCategory(
    id: string,
    categoryGroupId: string,
    position: number,
  ): void {
    this.loadingSignal.set(true);
    this.categoriesApi
      .placeCategory(id, { categoryGroupId, position })
      .pipe(
        tap(() => {
          const categories = this.categoriesSignal();
          const moved = categories.find((category) => category.id === id);
          const destinationGroup = this.groupsSignal().find(
            (group) => group.id === categoryGroupId,
          );
          if (!moved || !destinationGroup) {
            return;
          }

          if (moved.categoryGroupId === categoryGroupId) {
            const reordered = categories
              .filter(
                (category) =>
                  category.categoryGroupId === categoryGroupId &&
                  category.id !== id,
              )
              .sort((left, right) => left.position - right.position);
            reordered.splice(position, 0, moved);
            const normalized = reordered.map(
              (category, destinationPosition) => ({
                ...category,
                position: destinationPosition,
              }),
            );
            const otherGroups = categories.filter(
              (category) => category.categoryGroupId !== categoryGroupId,
            );
            this.categoriesSignal.set(
              this.sortCategories(
                [...otherGroups, ...normalized],
                this.groupsSignal(),
              ),
            );
            return;
          }

          const source = categories
            .filter(
              (category) =>
                category.categoryGroupId === moved.categoryGroupId &&
                category.id !== id,
            )
            .sort((left, right) => left.position - right.position)
            .map((category, sourcePosition) => ({
              ...category,
              position: sourcePosition,
            }));
          const destination = categories
            .filter(
              (category) =>
                category.categoryGroupId === categoryGroupId &&
                category.id !== id,
            )
            .sort((left, right) => left.position - right.position);
          destination.splice(position, 0, {
            ...moved,
            categoryGroupId,
            categoryGroupName: destinationGroup.name,
            position,
          });
          const normalizedDestination = destination.map(
            (category, destinationPosition) => ({
              ...category,
              categoryGroupId,
              categoryGroupName: destinationGroup.name,
              position: destinationPosition,
            }),
          );
          const unaffected = categories.filter(
            (category) =>
              category.id !== id &&
              category.categoryGroupId !== moved.categoryGroupId &&
              category.categoryGroupId !== categoryGroupId,
          );

          this.categoriesSignal.set(
            this.sortCategories(
              [...unaffected, ...source, ...normalizedDestination],
              this.groupsSignal(),
            ),
          );
        }),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public removeCategory(id: string): void {
    this.loadingSignal.set(true);
    this.categoriesApi
      .deleteCategory(id)
      .pipe(
        tap(() => {
          const removed = this.categoriesSignal().find(
            (category) => category.id === id,
          );
          this.categoriesSignal.update((categories) => {
            const remaining = categories.filter(
              (category) => category.id !== id,
            );
            if (!removed) {
              return remaining;
            }

            let position = 0;
            return remaining.map((category) =>
              category.categoryGroupId === removed.categoryGroupId
                ? { ...category, position: position++ }
                : category,
            );
          });
        }),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public categoriesForGroup(categoryGroupId: string): CategoryDto[] {
    return this.categoriesSignal().filter(
      (category) => category.categoryGroupId === categoryGroupId,
    );
  }

  private moveItem(
    groups: CategoryGroupDto[],
    id: string,
    position: number,
  ): CategoryGroupDto[] {
    const ordered = [...groups].sort(
      (left, right) => left.position - right.position,
    );
    const currentIndex = ordered.findIndex((group) => group.id === id);
    if (currentIndex < 0) {
      return groups;
    }

    const [moved] = ordered.splice(currentIndex, 1);
    ordered.splice(position, 0, moved);
    return ordered.map((group, groupPosition) => ({
      ...group,
      position: groupPosition,
    }));
  }

  private sortCategories(
    categories: CategoryDto[],
    groups: CategoryGroupDto[],
  ): CategoryDto[] {
    const groupPositions = new Map(
      groups.map((group) => [group.id, group.position]),
    );
    return [...categories].sort(
      (left, right) =>
        (groupPositions.get(left.categoryGroupId) ?? 0) -
          (groupPositions.get(right.categoryGroupId) ?? 0) ||
        left.position - right.position ||
        left.id.localeCompare(right.id),
    );
  }

  private handleError(error: unknown): typeof EMPTY {
    console.error('Categories API request failed', error);
    return EMPTY;
  }
}
