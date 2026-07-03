import { Injectable, inject, signal } from '@angular/core';
import {
  CreateGroupRequest,
  GroupDto,
  GroupsApiService,
  UpdateGroupRequest,
} from '@app-core/api/groups-api.service';
import { EMPTY, catchError, finalize, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class GroupsService {
  private readonly api = inject(GroupsApiService);
  private readonly groupsSignal = signal<GroupDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly groups = this.groupsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load(): void {
    this.loadingSignal.set(true);
    this.api
      .getGroups()
      .pipe(
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe((response) => this.groupsSignal.set(response.items));
  }

  public add(request: CreateGroupRequest): void {
    this.loadingSignal.set(true);
    this.api
      .createGroup(request)
      .pipe(
        tap((created) =>
          this.groupsSignal.update((groups) =>
            [...groups, created].sort((a, b) => a.name.localeCompare(b.name)),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public update(id: string, request: UpdateGroupRequest): void {
    this.loadingSignal.set(true);
    this.api
      .updateGroup(id, request)
      .pipe(
        // PUT returns 204 No Content, so reconstruct the updated group from the
        // existing entry plus the request payload.
        tap(() =>
          this.groupsSignal.update((groups) =>
            groups
              .map((group) =>
                group.id === id
                  ? {
                      ...group,
                      name: request.name,
                      description: request.description,
                    }
                  : group,
              )
              .sort((a, b) => a.name.localeCompare(b.name)),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public remove(id: string): void {
    this.loadingSignal.set(true);
    this.api
      .deleteGroup(id)
      .pipe(
        tap(() =>
          this.groupsSignal.update((groups) =>
            groups.filter((group) => group.id !== id),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  // TODO: surface API errors to the user (e.g. a snackbar) once the app has an
  // error-notification convention. For now we swallow the error so it does not
  // become an unhandled rejection; `loading` is reset by each pipe's finalize.
  private handleError(error: unknown): typeof EMPTY {
    console.error('Groups API request failed', error);
    return EMPTY;
  }
}
