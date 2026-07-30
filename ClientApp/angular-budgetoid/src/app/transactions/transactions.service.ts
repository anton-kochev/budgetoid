import { Injectable, inject, signal } from '@angular/core';
import {
  CategoryGroupDto,
  CategoryGroupsApiService,
} from '@app-core/api/category-groups-api.service';
import {
  CategoriesApiService,
  CategoryDto,
} from '@app-core/api/categories-api.service';
import { PayeeDto, PayeesApiService } from '@app-core/api/payees-api.service';
import {
  CreateTransactionRequest,
  TransactionDto,
  TransactionsApiService,
} from '@app-core/api/transactions-api.service';
import { finalize, forkJoin, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class TransactionsService {
  private readonly api = inject(TransactionsApiService);
  private readonly payeesApi = inject(PayeesApiService);
  private readonly categoryGroupsApi = inject(CategoryGroupsApiService);
  private readonly categoriesApi = inject(CategoriesApiService);
  private readonly transactionsSignal = signal<TransactionDto[]>([]);
  private readonly payeesSignal = signal<PayeeDto[]>([]);
  private readonly categoryGroupsSignal = signal<CategoryGroupDto[]>([]);
  private readonly categoriesSignal = signal<CategoryDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly transactions = this.transactionsSignal.asReadonly();
  public readonly payees = this.payeesSignal.asReadonly();
  public readonly categoryGroups = this.categoryGroupsSignal.asReadonly();
  public readonly categories = this.categoriesSignal.asReadonly();
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

  public loadCategories(): void {
    forkJoin({
      groups: this.categoryGroupsApi.getCategoryGroups(),
      categories: this.categoriesApi.getCategories(),
    }).subscribe(({ groups, categories }) => {
      this.categoryGroupsSignal.set(groups.items);
      this.categoriesSignal.set(categories.items);
    });
  }

  public categoriesForGroup(categoryGroupId: string): CategoryDto[] {
    return this.categoriesSignal().filter(
      (category) => category.categoryGroupId === categoryGroupId,
    );
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
