import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject } from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { Observable } from 'rxjs';

type ContentType = 'json';

export abstract class BaseApiService {
  private readonly http: HttpClient;
  private readonly baseUrl: string;

  constructor() {
    this.http = inject(HttpClient);
    this.baseUrl = inject(ConfigurationService).getConfig().apiBaseUrl;
  }

  protected get<T>(path: string): Observable<T> {
    const opts = { headers: BaseApiService.headers() };

    return this.http.get<T>(`${this.baseUrl}/${path}`, opts);
  }

  // Deliberately not routed through `get<T>()`: that path sets a JSON
  // responseType, which hands the body to JSON.parse, and it sends a
  // `Content-Type: application/json` request header that a bodyless GET has
  // nothing to describe and that costs a CORS preflight. The other verbs keep
  // that header — correcting them is a separate change, not a side effect of
  // adding this one.
  protected getBlob(path: string): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${path}`, { responseType: 'blob' });
  }

  protected patch<T = unknown>(path: string, patch: unknown): Observable<T> {
    const opts = { headers: BaseApiService.headers() };

    return this.http.patch<T>(`${this.baseUrl}/${path}`, patch, opts);
  }

  protected post<T = unknown>(path: string, body: unknown): Observable<T> {
    const opts = { headers: BaseApiService.headers() };

    return this.http.post<T>(`${this.baseUrl}/${path}`, body, opts);
  }

  protected put<T = unknown>(path: string, body: unknown): Observable<T> {
    const opts = { headers: BaseApiService.headers() };

    return this.http.put<T>(`${this.baseUrl}/${path}`, body, opts);
  }

  protected delete<T = unknown>(path: string): Observable<T> {
    const opts = { headers: BaseApiService.headers() };

    return this.http.delete<T>(`${this.baseUrl}/${path}`, opts);
  }

  private static contentTypeHeader(contentType: ContentType): {
    'Content-Type': string;
  } {
    switch (contentType) {
      case 'json':
        return { 'Content-Type': 'application/json' };
      default:
        return { 'Content-Type': 'application/json' };
    }
  }

  private static headers(contentType: ContentType = 'json'): HttpHeaders {
    return new HttpHeaders({
      ...BaseApiService.contentTypeHeader(contentType),
    });
  }
}
