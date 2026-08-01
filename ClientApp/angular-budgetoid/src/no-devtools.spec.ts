import { readFileSync } from 'node:fs';
import { relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// FR-032: the production build registers no state-inspection or developer-tooling
// provider. This test reads the production build rather than the sources, because only
// the emitted bundle can prove absence — a provider can look absent in `src/` and still
// be linked into the shipped JavaScript, and the bundle is what a browser executes.
//
// Requires a production build: `npm run build && npm test`.
// See docs/engineering/no-third-party-origins.md.

// Both markers are verified present in today's bundle and survive minification, so
// their absence is a real signal rather than an artifact of renaming. `store-devtools`
// appears inside NgRx action-type string literals (`@ngrx/store-devtools/recompute` and
// friends); `__REDUX_DEVTOOLS_EXTENSION__` is a `window` property name.
const devtoolsMarkers = ['store-devtools', '__REDUX_DEVTOOLS_EXTENSION__'];

describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  it('registers no state-inspection or developer-tooling provider', () => {
    // Arrange
    const bundles = emittedFiles(['.js']);

    // Act
    const registered = bundles.flatMap((path) => {
      const source = readFileSync(path, 'utf8');

      return devtoolsMarkers
        .filter((marker) => source.includes(marker))
        .map((marker) => `${relative(browserDir, path)}: ${marker}`);
    });

    // Assert
    expect(registered).toEqual([]);
  });
});
