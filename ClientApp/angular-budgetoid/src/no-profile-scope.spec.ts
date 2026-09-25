import { readFileSync } from 'node:fs';
import { basename, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// FR-086: the app asks the identity provider for no claim it does not consume. Nothing
// on the client reads an ID-token claim, so the only scopes requested are `openid` (the
// protocol minimum) and `email` (the account identifier the backend stores). This test
// reads the emitted configuration rather than `public/assets/`, because the shipped
// `app-config.json` is what the browser fetches and hands to the OAuth client — a source
// file the build never copies would prove nothing.
//
// Requires a production build: `npm run build && npm test`.

// `app-config.local.json` is a gitignored dev override; it is checked here too whenever
// it is present, because a developer's browser follows it and it is the copy most easily
// forgotten. Its absence on a clean checkout is not a failure.
function emittedConfigs(): string[] {
  return emittedFiles(['.json']).filter((path) =>
    basename(path).startsWith('app-config'),
  );
}

function property(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null && key in value
    ? (value as Record<string, unknown>)[key]
    : undefined;
}

function requestedScopes(path: string): readonly string[] {
  const config: unknown = JSON.parse(readFileSync(path, 'utf8'));
  const scope = property(property(property(config, 'auth'), 'google'), 'scope');

  return typeof scope === 'string'
    ? scope.split(/\s+/).filter((token) => token.length > 0)
    : [];
}

describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  // Without this the absence check below would also pass on a config whose `scope` key
  // was renamed or dropped — a green test that proves nothing about what is requested.
  it('emits an OAuth configuration that declares its scopes', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const declared = configs.map(
      (path) =>
        `${relative(browserDir, path)}: ${requestedScopes(path).length}`,
    );

    // Assert
    expect(configs.length).toBeGreaterThan(0);
    expect(declared.filter((entry) => entry.endsWith(': 0'))).toEqual([]);
  });

  it('requests no identity-provider profile scope', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const unexpected = configs.flatMap((path) =>
      requestedScopes(path)
        .filter((scope) => scope !== 'openid' && scope !== 'email')
        .map((scope) => `${relative(browserDir, path)}: ${scope}`),
    );

    // Assert
    expect(unexpected).toEqual([]);
  });
});
