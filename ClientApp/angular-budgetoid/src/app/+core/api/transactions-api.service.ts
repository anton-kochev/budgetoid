import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

/**
 * The body of `POST /api/transactions`.
 *
 * **Seven members, all of them sent, and the shape refuses what it was not
 * asked for.** The route binds `(Id, Amount, Date, AccountId, Description,
 * PayeeId, CategoryId)` under `[JsonUnmappedMemberHandling(Disallow)]`, so a
 * member this API retired — `payeeName`, which every build of this client sent
 * until now — is a **400** rather than a 201 with the counterparty silently
 * dropped. Nothing may be added here that the route does not bind.
 *
 * `id` is minted by this client because the note is sealed against it, and it
 * crosses as text in the canonical lower-case hyphenated spelling: bound as a
 * `Guid`, the server would fold the spellings before any handler saw them and
 * the check that keeps the associated data reproducible would be unwritable.
 *
 * `description` is the **sealed** note as unpadded base64url, or `null` for a
 * transaction with no note. `''` is neither, and is a 400: the empty string is
 * not a legal envelope.
 *
 * `payeeId` is a row this client created through `POST /api/payees` before
 * making this request — the server can no longer resolve a name to a payee,
 * because `payees.name` is an envelope drawn under a fresh nonce and the digest
 * that is stable is taken under a key that lives in a browser.
 */
export interface CreateTransactionRequest {
  id: string;
  amount: number;
  date: string;
  accountId: string;
  description: string | null;
  payeeId: string | null;
  categoryId: string | null;
}

/**
 * One transaction as every transaction-reading route hands it back.
 *
 * **Five members hold ciphertext and four of them belong to another row.**
 * `description` is this transaction's own note, bound to `id`; `accountName`
 * belongs to `accountId`, `payeeName` to `payeeId`, `categoryName` to
 * `categoryId` and `categoryGroupName` to `categoryGroupId`. Associated data is
 * rebuilt from wherever a ciphertext was found rather than carried inside it,
 * so each of the four has to be opened under the binding of the row it came
 * from — which is why all four identifiers are on the wire beside them.
 * Opening one under this transaction's id fails to authenticate, permanently,
 * with nothing naming the cause. `toTransactionView` is the one thing that
 * reads any of them.
 */
export interface TransactionDto {
  id: string;
  amount: number;
  date: string;
  /** The **sealed** note as unpadded base64url, or `null` for none. */
  description: string | null;
  createdAtUtc: string;
  accountId: string;
  /** The **sealed** account name, bound to `accountId` and never to `id`. */
  accountName: string;
  currencyCode: string;
  currencySymbol: string;
  payeeId?: string | null;
  /** The **sealed** payee name, bound to `payeeId`. */
  payeeName?: string | null;
  categoryId?: string | null;
  /** The **sealed** category name, bound to `categoryId`. */
  categoryName?: string | null;
  categoryGroupId?: string | null;
  /** The **sealed** group name, bound to `categoryGroupId`. */
  categoryGroupName?: string | null;
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
