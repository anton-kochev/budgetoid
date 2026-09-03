import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CategoryDto {
  id: string;
  /**
   * The **sealed** name as unpadded base64url, not a name.
   *
   * `toCategoryView` is the one thing that turns it into something a template
   * may render; nothing else may read this member as text.
   */
  name: string;
  /**
   * The **sealed** note as unpadded base64url, or `null` where the category
   * holds none. Never `''`, which is not a legal envelope.
   */
  description: string | null;
  categoryGroupId: string;
  /**
   * The group's **sealed** name, denormalized onto this row — and it is sealed
   * against {@link categoryGroupId}, never against {@link id}.
   *
   * Associated data is rebuilt from wherever a ciphertext was found, so opening
   * this member under the category's own identifier fails the tag check and
   * answers `unreadable` — the same word a genuinely damaged column produces,
   * with nothing anywhere naming the cause. `category-view.ts` is where that is
   * got right and where the argument lives.
   */
  categoryGroupName: string;
  position: number;
}

export interface CategoryListResponse {
  items: CategoryDto[];
}

/**
 * The body of `POST /api/categories`.
 *
 * `id` is minted by this client, because both of the category's own narrative
 * members are sealed against it and the sealing happens before the row exists.
 * `categoryGroupId` is a foreign key read back off a list and is associated
 * data for nothing.
 *
 * **`description` is required and nullable rather than optional.** The route
 * carries `[JsonUnmappedMemberHandling(Disallow)]` precisely because a
 * *misspelled* member binds identically to an absent one under the default
 * handling — a 201 with a note that never arrived. An optional member here
 * would let a caller make that same omission from this side, in TypeScript,
 * where nothing would say so.
 */
export interface CreateCategoryRequest {
  id: string;
  name: string;
  nameKey: string;
  description: string | null;
  categoryGroupId: string;
}

/**
 * The body of `PUT /api/categories/{id}` — three members and no `id`, which the
 * route carries. An update re-seals against the row's **existing** identifier.
 *
 * `description` is required and nullable for the reason
 * {@link CreateCategoryRequest} gives, and the cost of getting it wrong is
 * higher here: on this verb an absent note is a **204 having cleared a note
 * nobody asked to remove**.
 */
export interface UpdateCategoryRequest {
  name: string;
  nameKey: string;
  description: string | null;
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
