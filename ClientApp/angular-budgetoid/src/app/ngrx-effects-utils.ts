import {
  APP_BOOTSTRAP_LISTENER,
  EnvironmentProviders,
  Inject,
  InjectionToken,
  makeEnvironmentProviders,
  Type,
} from '@angular/core';
import { EffectSources } from '@ngrx/effects';

/**
 * This set of utility functions defers ngrx Effects to be initialized
 * until APP_INITIALIZER is fulfilled and application configuration loaded.
 * This prevents dependent services (API services) to be initialized
 * because they are relying on the URLs provided by the JSON configuration file
 */

const BOOTSTRAP_EFFECTS = new InjectionToken('Bootstrap Effects');

function createInstances(...instances: unknown[]): unknown[] {
  return instances;
}

function bootstrapEffects(effects: Type<unknown>[], sources: EffectSources) {
  return (): void => {
    effects.forEach(effect => sources.addEffects(effect));
  };
}

export function provideBootstrapEffects(
  ...effects: Type<unknown>[]
): EnvironmentProviders {
  return makeEnvironmentProviders([
    effects,
    {
      provide: BOOTSTRAP_EFFECTS,
      useFactory: createInstances,
      deps: effects,
    },
    {
      provide: APP_BOOTSTRAP_LISTENER,
      multi: true,
      useFactory: bootstrapEffects,
      deps: [[new Inject(BOOTSTRAP_EFFECTS)], EffectSources],
    },
  ]);
}
