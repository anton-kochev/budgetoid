import type { Observable } from 'rxjs';
import { get } from './http.service';
import type { Transaction } from './models/transaction.model';

export function fetchUserTransactions(): Observable<Transaction[]> {
  return get<Transaction[]>(
    'http://localhost:7071/api/user/00000000-0000-0000-0000-000000000002/transactions',
  );
}
