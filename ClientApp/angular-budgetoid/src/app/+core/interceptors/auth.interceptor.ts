import {
  HttpEvent,
  HttpHandler,
  HttpInterceptor,
  HttpRequest,
} from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { OAuthService } from 'angular-oauth2-oidc';
import { Observable } from 'rxjs';

@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  private readonly oAuthService = inject(OAuthService);
  private readonly configurationService = inject(ConfigurationService);

  public intercept(
    request: HttpRequest<unknown>,
    next: HttpHandler,
  ): Observable<HttpEvent<unknown>> {
    const apiBaseUrl = this.configurationService.getConfig().apiBaseUrl;
    const idToken = this.oAuthService.getIdToken();

    if (!idToken || !this.isApiRequest(request.url, apiBaseUrl)) {
      return next.handle(request);
    }

    return next.handle(
      request.clone({
        headers: request.headers.set('Authorization', `Bearer ${idToken}`),
      }),
    );
  }

  private isApiRequest(url: string, apiBaseUrl: string): boolean {
    if (!apiBaseUrl) {
      return false;
    }

    return url.startsWith(apiBaseUrl);
  }
}
