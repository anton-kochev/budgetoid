import { Injectable } from '@angular/core';
import { Guid } from 'app/+common/guid';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

@Injectable()
export class TransactionsApiService extends BaseApiService {
  public getUserTransactions(userId: Guid): Observable<unknown> {
    return this.get(`api/user/${userId}/transactions`);
  }
}
