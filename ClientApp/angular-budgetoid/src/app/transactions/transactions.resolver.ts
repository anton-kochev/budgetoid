import { inject } from '@angular/core';
import { ResolveFn } from '@angular/router';
import { TransactionsApiService } from '@app-core/api/transactions-api.service';
import { guid } from 'app/+common/guid';
import { map } from 'rxjs';

export const transactionsResolver: ResolveFn<boolean> = (route, state) => {
  const transactionsApiService = inject(TransactionsApiService);
  const userId = guid('00000000-0000-0000-0000-000000000001');

  return transactionsApiService
    .getUserTransactions(userId)
    .pipe(map(() => true));
};
