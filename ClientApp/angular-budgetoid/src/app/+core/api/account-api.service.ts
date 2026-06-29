import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export type AccountType = 'Checking' | 'Savings' | 'Cash' | 'CreditCard';

export interface AccountDto {
  id: string;
  name: string;
  type: AccountType;
  openingBalance: number;
  createdAtUtc: string;
  currencyCode: string;
  currencyName: string;
  currencySymbol: string;
  currencyMinorUnit: number;
}

export interface AccountListResponse {
  items: AccountDto[];
}

export interface CreateAccountRequest {
  name: string;
  type: AccountType;
  openingBalance: number;
  currencyCode: string;
}

export interface UpdateAccountRequest {
  name: string;
  type: AccountType;
  openingBalance: number;
}

@Injectable({ providedIn: 'root' })
export class AccountApiService extends BaseApiService {
  public getAccounts(): Observable<AccountListResponse> {
    return this.get<AccountListResponse>('api/accounts');
  }

  public createAccount(request: CreateAccountRequest): Observable<AccountDto> {
    return this.post<AccountDto>('api/accounts', request);
  }

  public updateAccount(
    id: string,
    request: UpdateAccountRequest,
  ): Observable<void> {
    return this.put<void>(`api/accounts/${id}`, request);
  }

  public deleteAccount(id: string): Observable<void> {
    return this.delete<void>(`api/accounts/${id}`);
  }
}
