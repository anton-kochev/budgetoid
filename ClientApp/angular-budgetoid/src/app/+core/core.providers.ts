import {
  APP_INITIALIZER,
  EnvironmentProviders,
  makeEnvironmentProviders,
} from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { AccountApiService } from './api/account-api.service';
import { TransactionsApiService } from './api/transactions-api.service';

export const provideAppCore = (): EnvironmentProviders =>
  makeEnvironmentProviders([
    // API services
    AccountApiService,
    TransactionsApiService,
    // Configuration
    ConfigurationService,
    {
      provide: APP_INITIALIZER,
      useFactory: (config: ConfigurationService) => () => config.load(),
      deps: [ConfigurationService],
      multi: true,
    },
  ]);
