import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CategoryGroupDto {
  id: string;
  /**
   * The **sealed** name as unpadded base64url, not a name.
   *
   * The column is an AEAD envelope the server cannot open, so what arrives here
   * is that envelope. `toCategoryGroupView` is the one thing that turns it into
   * something a template may render; nothing else may read this member as text.
   */
  name: string;
  /**
   * The **sealed** note as unpadded base64url, or `null` where the group holds
   * none.
   *
   * `null` is a note nobody wrote. It is never `''`, which is not a legal
   * envelope — the server refuses one and this client has no path that produces
   * one, so "cleared" and "never filled" are one thing from this browser even
   * though the column keeps them as two rows.
   */
  description: string | null;
  position: number;
}

export interface CategoryGroupListResponse {
  items: CategoryGroupDto[];
}

/**
 * The body of `POST /api/category-groups`.
 *
 * `id` is minted by this client, because both narrative members are sealed
 * against it and the sealing happens before the row exists. `name` is that
 * envelope and `nameKey` the blind index over the same text — two members
 * because they are two columns, and the server can check neither against the
 * other.
 *
 * **`description` is required and nullable rather than optional**, and the
 * difference is the whole point. The route carries
 * `[JsonUnmappedMemberHandling(Disallow)]` precisely because a *misspelled*
 * member binds identically to an absent one under the default handling — a 201
 * with a note that never arrived. An optional member here would let a caller
 * make that same omission from this side, in TypeScript, where nothing would
 * say so; required and nullable makes "this group files no note" a decision
 * every call site has to write down.
 */
export interface CreateCategoryGroupRequest {
  id: string;
  name: string;
  nameKey: string;
  description: string | null;
}

/**
 * The body of `PUT /api/category-groups/{id}` — three members and no `id`,
 * which the route carries. An update re-seals against the row's **existing**
 * identifier.
 *
 * `description` is required and nullable for the reason
 * {@link CreateCategoryGroupRequest} gives, and the cost of getting it wrong is
 * higher here: on this verb an absent note is a **204 having cleared a note
 * nobody asked to remove**.
 */
export interface UpdateCategoryGroupRequest {
  name: string;
  nameKey: string;
  description: string | null;
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
