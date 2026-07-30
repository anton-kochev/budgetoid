import { HTTP_INTERCEPTORS } from '@angular/common/http';
import {
  APP_INITIALIZER,
  EnvironmentProviders,
  makeEnvironmentProviders,
} from '@angular/core';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { AccountApiService } from './api/account-api.service';
import { PayeesApiService } from './api/payees-api.service';
import { TransactionsApiService } from './api/transactions-api.service';
import { AuthInterceptor } from './interceptors/auth.interceptor';

export const provideAppCore = (): EnvironmentProviders =>
  makeEnvironmentProviders([
    // API services
    AccountApiService,
    PayeesApiService,
    TransactionsApiService,
    // Configuration
    ConfigurationService,
    {
      provide: APP_INITIALIZER,
      useFactory:
        (config: ConfigurationService, auth: AuthService) => async () => {
          await config.load();
          await auth.initialize();
        },
      deps: [ConfigurationService, AuthService],
      multi: true,
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: AuthInterceptor,
      multi: true,
    },
  ]);
