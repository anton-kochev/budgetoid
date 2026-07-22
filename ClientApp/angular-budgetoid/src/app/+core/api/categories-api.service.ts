import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CategoryDto {
  id: string;
  name: string;
  description?: string | null;
  categoryGroupId: string;
  categoryGroupName: string;
  position: number;
}

export interface CategoryListResponse {
  items: CategoryDto[];
}

export interface CreateCategoryRequest {
  name: string;
  description?: string | null;
  categoryGroupId: string;
}

export interface UpdateCategoryRequest {
  name: string;
  description?: string | null;
}

export interface PlaceCategoryRequest {
  categoryGroupId: string;
  position: number;
}

@Injectable({ providedIn: 'root' })
export class CategoriesApiService extends BaseApiService {
  public getCategories(): Observable<CategoryListResponse> {
    return this.get<CategoryListResponse>('api/categories');
  }

  public createCategory(
    request: CreateCategoryRequest,
  ): Observable<CategoryDto> {
    return this.post<CategoryDto>('api/categories', request);
  }

  public updateCategory(
    id: string,
    request: UpdateCategoryRequest,
  ): Observable<void> {
    return this.put<void>(`api/categories/${id}`, request);
  }

  public placeCategory(
    id: string,
    request: PlaceCategoryRequest,
  ): Observable<void> {
    return this.patch<void>(`api/categories/${id}/placement`, request);
  }

  public deleteCategory(id: string): Observable<void> {
    return this.delete<void>(`api/categories/${id}`);
  }
}
