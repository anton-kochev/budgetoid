import { inject } from '@angular/core';
import { ResolveFn } from '@angular/router';
import { AccountApiService } from '@app-core/api/account-api.service';
import { guid } from 'app/+common/guid';
import { map } from 'rxjs';

export const accountsResolver: ResolveFn<boolean> = (route, state) => {
  const accounts = inject(AccountApiService);
  const userId = guid('00000000-0000-0000-0000-000000000001');

  return accounts.getAll(userId).pipe(map(() => true));
};
