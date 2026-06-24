import { Injectable, inject, signal } from '@angular/core';
import { PayeeDto, PayeesApiService } from '@app-core/api/payees-api.service';
import {
  CreateTransactionRequest,
  TransactionDto,
  TransactionsApiService,
} from '@app-core/api/transactions-api.service';
import { finalize, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class TransactionsService {
  private readonly api = inject(TransactionsApiService);
  private readonly payeesApi = inject(PayeesApiService);
  private readonly transactionsSignal = signal<TransactionDto[]>([]);
  private readonly payeesSignal = signal<PayeeDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly transactions = this.transactionsSignal.asReadonly();
  public readonly payees = this.payeesSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load(): void {
    this.loadingSignal.set(true);
    this.api
      .getTransactions()
      .pipe(finalize(() => this.loadingSignal.set(false)))
      .subscribe((response) => this.transactionsSignal.set(response.items));
  }

  public loadPayees(): void {
    this.payeesApi
      .getPayees()
      .subscribe((response) => this.payeesSignal.set(response.items));
  }

  public add(request: CreateTransactionRequest): void {
    this.loadingSignal.set(true);
    this.api
      .createTransaction(request)
      .pipe(
        tap(() => {
          this.load();
          this.loadPayees();
        }),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }
}
