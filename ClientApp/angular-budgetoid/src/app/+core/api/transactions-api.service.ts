import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CreateTransactionRequest {
  amount: number;
  date: string;
  description: string;
  payeeName?: string;
}

export interface TransactionDto {
  id: string;
  amount: number;
  date: string;
  description: string;
  createdAtUtc: string;
  payeeId?: string | null;
  payeeName?: string | null;
}

export interface TransactionListResponse {
  items: TransactionDto[];
}

@Injectable({ providedIn: 'root' })
export class TransactionsApiService extends BaseApiService {
  public getTransactions(): Observable<TransactionListResponse> {
    return this.get<TransactionListResponse>('api/transactions');
  }

  public createTransaction(
    request: CreateTransactionRequest,
  ): Observable<TransactionDto> {
    return this.post<TransactionDto>('api/transactions', request);
  }
}
