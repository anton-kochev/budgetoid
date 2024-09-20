import { Injectable } from '@angular/core';
import { Guid } from 'app/+common/guid';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

@Injectable()
export class AccountApiService extends BaseApiService {
  public getAll(userId: Guid): Observable<unknown> {
    return this.get(
      `api/accounts/${userId}?code=mqry4PP1irUaJw6k5bNfsb3zPKdEpUXNTnsl-bQ1kEVbAzFufWIk_A%3D%3D`,
    );
  }
}
