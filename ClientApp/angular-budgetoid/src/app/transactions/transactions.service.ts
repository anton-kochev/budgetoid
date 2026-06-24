import { Injectable, inject, signal } from '@angular/core';
import {
  CreateTransactionRequest,
  TransactionDto,
  TransactionsApiService,
} from '@app-core/api/transactions-api.service';
import { finalize, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class TransactionsService {
  private readonly api = inject(TransactionsApiService);
  private readonly transactionsSignal = signal<TransactionDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly transactions = this.transactionsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load(): void {
    this.loadingSignal.set(true);
    this.api
      .getTransactions()
      .pipe(finalize(() => this.loadingSignal.set(false)))
      .subscribe((response) => this.transactionsSignal.set(response.items));
  }

  public add(request: CreateTransactionRequest): void {
    this.loadingSignal.set(true);
    this.api
      .createTransaction(request)
      .pipe(
        tap(() => this.load()),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }
}
