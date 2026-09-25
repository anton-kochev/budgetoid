import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export type AccountType = 'Checking' | 'Savings' | 'Cash' | 'CreditCard';

export interface AccountDto {
  id: string;
  /**
   * The **sealed** name as unpadded base64url, not a name.
   *
   * The column is an AEAD envelope the server cannot open, so what arrives here
   * is that envelope. `toAccountView` is the one thing that turns it into
   * something a template may render; nothing else may read this member as text.
   */
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

/**
 * The body of `POST /api/accounts`.
 *
 * `id` is minted by this client, because the name is sealed against it and the
 * sealing happens before the row exists. `name` is that envelope and `nameKey`
 * the blind index over the same text — two members because they are two
 * columns, and the server can check neither against the other.
 */
export interface CreateAccountRequest {
  id: string;
  name: string;
  nameKey: string;
  type: AccountType;
  openingBalance: number;
  currencyCode: string;
}

/**
 * The body of `PUT /api/accounts/{id}` — four members and no `id`, which the
 * route carries. An update re-seals against the row's **existing** identifier.
 */
export interface UpdateAccountRequest {
  name: string;
  nameKey: string;
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
