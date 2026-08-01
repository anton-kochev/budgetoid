import { EnvironmentProviders, Provider } from '@angular/core';

// Not dead code: the `fileReplacements` entry in the production configuration of
// `angular.json` swaps `src/app/devtools.providers.ts` for this file, so nothing imports it
// by name. Registering nothing here is the point — an empty array is what leaves
// `@ngrx/store-devtools` out of the production module graph entirely.
export const devtoolsProviders: readonly (Provider | EnvironmentProviders)[] =
  [];
