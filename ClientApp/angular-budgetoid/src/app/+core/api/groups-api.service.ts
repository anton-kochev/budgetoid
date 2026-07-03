import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface GroupDto {
  id: string;
  name: string;
  description?: string | null;
}

export interface GroupListResponse {
  items: GroupDto[];
}

export interface CreateGroupRequest {
  name: string;
  description?: string | null;
}

export interface UpdateGroupRequest {
  name: string;
  description?: string | null;
}

@Injectable({ providedIn: 'root' })
export class GroupsApiService extends BaseApiService {
  public getGroups(): Observable<GroupListResponse> {
    return this.get<GroupListResponse>('api/groups');
  }

  public createGroup(request: CreateGroupRequest): Observable<GroupDto> {
    return this.post<GroupDto>('api/groups', request);
  }

  public updateGroup(
    id: string,
    request: UpdateGroupRequest,
  ): Observable<void> {
    return this.put<void>(`api/groups/${id}`, request);
  }

  public deleteGroup(id: string): Observable<void> {
    return this.delete<void>(`api/groups/${id}`);
  }
}
