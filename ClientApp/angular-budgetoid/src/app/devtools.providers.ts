import { EnvironmentProviders, Provider } from '@angular/core';
import { provideStoreDevtools } from '@ngrx/store-devtools';

// Development-only providers. The production configuration in `angular.json` replaces this
// file with `devtools.providers.prod.ts`, which is what takes the `@ngrx/store-devtools`
// import out of the module graph so the package is tree-shaken out of the bundle. A runtime
// `isDevMode()` branch would not: the import survives, and so do the devtools code and its
// `__REDUX_DEVTOOLS_EXTENSION__` string literal.
//
// FR-032: the production build registers no state-inspection or developer-tooling provider.
// See docs/engineering/no-third-party-origins.md.
export const devtoolsProviders: readonly (Provider | EnvironmentProviders)[] = [
  provideStoreDevtools({ maxAge: 25 }),
];
