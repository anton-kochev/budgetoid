import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface PayeeDto {
  id: string;
  name: string;
}

export interface PayeeListResponse {
  items: PayeeDto[];
}

@Injectable({ providedIn: 'root' })
export class PayeesApiService extends BaseApiService {
  public getPayees(): Observable<PayeeListResponse> {
    return this.get<PayeeListResponse>('api/payees');
  }
}
