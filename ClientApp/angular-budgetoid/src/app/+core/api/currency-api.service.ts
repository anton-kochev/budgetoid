import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface CurrencyDto {
  code: string;
  name: string;
  symbol: string;
  minorUnit: number;
}

export interface CurrencyListResponse {
  items: CurrencyDto[];
}

@Injectable({ providedIn: 'root' })
export class CurrencyApiService extends BaseApiService {
  public getCurrencies(): Observable<CurrencyListResponse> {
    return this.get<CurrencyListResponse>('api/currencies');
  }
}
