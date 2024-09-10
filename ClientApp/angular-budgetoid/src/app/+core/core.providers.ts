import {
  APP_INITIALIZER,
  EnvironmentProviders,
  makeEnvironmentProviders,
} from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';

export const provideAppCore = (): EnvironmentProviders =>
  makeEnvironmentProviders([
    ConfigurationService,
    {
      provide: APP_INITIALIZER,
      useFactory: (config: ConfigurationService) => () => config.load(),
      deps: [ConfigurationService],
      multi: true,
    },
  ]);
