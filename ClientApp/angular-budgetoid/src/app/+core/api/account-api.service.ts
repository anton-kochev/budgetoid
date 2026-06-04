import { Injectable } from '@angular/core';
import { Guid } from 'app/+common/guid';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

@Injectable()
export class AccountApiService extends BaseApiService {
  public getAll(userId: Guid): Observable<unknown> {
    return this.get(`api/accounts`);
  }
}
