import {
  HttpClient,
  HttpHeaders,
  type HttpContext,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { Observable } from 'rxjs';

type ContentType = 'json';

export abstract class BaseApiService {
  private readonly http = inject(HttpClient);
  private readonly configuration = inject(ConfigurationService);

  // Read on every request and deliberately never snapshotted. A field
  // initializer here would copy whatever the configuration held at the moment
  // this service was built, and *when* that is depends on who injects it: a
  // service reached from the `APP_INITIALIZER`'s `deps` is built to make the
  // factory's arguments, which is before the factory body has awaited
  // `config.load()`. That copy is `''` — the value `ConfigurationService`
  // starts at — and `'' + '/api/me'` is a same-origin path, so the request goes
  // to the static host, which answers **200 with `index.html`** rather than a
  // 404. Under `responseType: 'json'` that body fails to parse and the read is
  // published as `unreachable`, which both guards admit, so the visitor sees a
  // screen whose every later call is refused. Nothing about that is loud, and
  // nothing about it is dev-only: Azure Static Web Apps' `navigationFallback`
  // answers the same way.
  //
  // A getter costs one object read per request and buys the property that no
  // construction order can be wrong. `base-api.service.spec.ts` builds this
  // class the way the initializer does — before the configuration resolves —
  // and pins where the request went.
  private get baseUrl(): string {
    return this.configuration.getConfig().apiBaseUrl;
  }

  // The optional `HttpContext` is the one parameter on this shared path that
  // almost every call site must never pass, and it is here because a *route*
  // can be read by two callers asking two different questions. `GET /api/me` is
  // the case: `SessionService.probe()` reads it to find out whether there is a
  // session at all, so its 401 is the answer it went to fetch and must carry
  // `EXPECTS_UNAUTHENTICATED`; the Settings screen reads the same route to
  // render the account's email, from a browser that believes it holds a
  // session, where a 401 does mean the session ended and the bounce is correct.
  // A token that rides on the request is the only thing that can tell two calls
  // to one method apart, so it belongs on the call rather than on the service.
  //
  // Only `get` takes one, and `post` deliberately still does not: both anonymous
  // POST surfaces build their own requests instead of extending this class, for
  // the reason `registration-api.service.ts` argues at its own class.
  protected get<T>(path: string, context?: HttpContext): Observable<T> {
    // `context: undefined` is what a caller that passes nothing produces, and
    // `HttpRequest` replaces it with a fresh `HttpContext` — so the request an
    // existing caller makes is byte-for-byte the one it made before.
    const opts = { context, headers: BaseApiService.headers() };

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
