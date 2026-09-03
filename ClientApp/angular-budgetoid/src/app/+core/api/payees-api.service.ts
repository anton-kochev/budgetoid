import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface PayeeDto {
  id: string;
  /**
   * The **sealed** name as unpadded base64url, not a name.
   *
   * The column is an AEAD envelope the server cannot open, so what arrives here
   * is that envelope. `toPayeeView` is the one thing that turns it into
   * something a template may render; nothing else may read this member as text.
   */
  name: string;
}

export interface PayeeListResponse {
  items: PayeeDto[];
}

/**
 * The body of `POST /api/payees` — the one path that creates a payee.
 *
 * `id` is minted by this client, because the name is sealed against it and the
 * sealing happens before the row exists. `name` is that envelope and `nameKey`
 * the blind index over the same text — two members because they are two
 * columns, and the server can check neither against the other.
 *
 * **A duplicate `nameKey` answers 409 and a duplicate `id` answers 409 too**,
 * with a different sentence: the first says this budget already holds the
 * counterparty and the caller's list was stale, the second that the row wearing
 * this id may hold an entirely different name. Only the first has a resolution
 * the client can carry out on its own, which is why `TransactionsService`
 * re-reads the list once and abandons rather than retrying with a fresh id.
 */
export interface CreatePayeeRequest {
  id: string;
  name: string;
  nameKey: string;
}

@Injectable({ providedIn: 'root' })
export class PayeesApiService extends BaseApiService {
  public getPayees(): Observable<PayeeListResponse> {
    return this.get<PayeeListResponse>('api/payees');
  }

  public createPayee(request: CreatePayeeRequest): Observable<PayeeDto> {
    return this.post<PayeeDto>('api/payees', request);
  }
}
