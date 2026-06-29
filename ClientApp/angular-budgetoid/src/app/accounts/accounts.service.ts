import { Injectable, inject, signal } from '@angular/core';
import {
  AccountApiService,
  AccountDto,
  CreateAccountRequest,
  UpdateAccountRequest,
} from '@app-core/api/account-api.service';
import { EMPTY, catchError, finalize, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class AccountsService {
  private readonly api = inject(AccountApiService);
  private readonly accountsSignal = signal<AccountDto[]>([]);
  private readonly loadingSignal = signal(false);

  public readonly accounts = this.accountsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load(): void {
    this.loadingSignal.set(true);
    this.api
      .getAccounts()
      .pipe(
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe((response) => this.accountsSignal.set(response.items));
  }

  public add(request: CreateAccountRequest): void {
    this.loadingSignal.set(true);
    this.api
      .createAccount(request)
      .pipe(
        tap((created) =>
          this.accountsSignal.update((accounts) =>
            [...accounts, created].sort((a, b) => a.name.localeCompare(b.name)),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public update(id: string, request: UpdateAccountRequest): void {
    this.loadingSignal.set(true);
    this.api
      .updateAccount(id, request)
      .pipe(
        // PUT returns 204 No Content, so reconstruct the updated account from
        // the existing entry plus the request payload. Currency is immutable,
        // so the existing currency fields are preserved.
        tap(() =>
          this.accountsSignal.update((accounts) =>
            accounts
              .map((account) =>
                account.id === id
                  ? {
                      ...account,
                      name: request.name,
                      type: request.type,
                      openingBalance: request.openingBalance,
                    }
                  : account,
              )
              .sort((a, b) => a.name.localeCompare(b.name)),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  public remove(id: string): void {
    this.loadingSignal.set(true);
    this.api
      .deleteAccount(id)
      .pipe(
        tap(() =>
          this.accountsSignal.update((accounts) =>
            accounts.filter((account) => account.id !== id),
          ),
        ),
        catchError((error) => this.handleError(error)),
        finalize(() => this.loadingSignal.set(false)),
      )
      .subscribe();
  }

  // TODO: surface API errors to the user (e.g. a snackbar) once the app has an
  // error-notification convention. For now we swallow the error so it does not
  // become an unhandled rejection; `loading` is reset by each pipe's finalize.
  private handleError(error: unknown): typeof EMPTY {
    console.error('Accounts API request failed', error);
    return EMPTY;
  }
}
