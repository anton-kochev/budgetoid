import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CategoryGroupDto {
  id: string;
  name: string;
  description?: string | null;
  position: number;
}

export interface CategoryGroupListResponse {
  items: CategoryGroupDto[];
}

export interface CreateCategoryGroupRequest {
  name: string;
  description?: string | null;
}

export interface UpdateCategoryGroupRequest {
  name: string;
  description?: string | null;
}

export interface MoveCategoryGroupRequest {
  position: number;
}

@Injectable({ providedIn: 'root' })
export class CategoryGroupsApiService extends BaseApiService {
  public getCategoryGroups(): Observable<CategoryGroupListResponse> {
    return this.get<CategoryGroupListResponse>('api/category-groups');
  }

  public createCategoryGroup(
    request: CreateCategoryGroupRequest,
  ): Observable<CategoryGroupDto> {
    return this.post<CategoryGroupDto>('api/category-groups', request);
  }

  public updateCategoryGroup(
    id: string,
    request: UpdateCategoryGroupRequest,
  ): Observable<void> {
    return this.put<void>(`api/category-groups/${id}`, request);
  }

  public moveCategoryGroup(
    id: string,
    request: MoveCategoryGroupRequest,
  ): Observable<void> {
    return this.patch<void>(`api/category-groups/${id}/position`, request);
  }

  public deleteCategoryGroup(id: string): Observable<void> {
    return this.delete<void>(`api/category-groups/${id}`);
  }
}
