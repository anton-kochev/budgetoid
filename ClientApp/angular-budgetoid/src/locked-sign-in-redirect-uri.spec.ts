import { readFileSync } from 'node:fs';
import { basename, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// Where Google sends a browser back to once the person has chosen the account
// they created a lost account with: the release screen, whose bootstrap reads
// the answer before the first route draws and posts it as the locked sign-in.
// See docs/design/components.md, "Releasing an account".
//
// **No test in this repository can see the other half of this.** The same URI
// has to be listed among the authorized redirect URIs of the Google Cloud
// console's OAuth client, and a mismatch is refused by Google with
// `redirect_uri_mismatch` before a line of this application runs. Changing
// this value without changing the console entry takes the release valve down
// for everybody who needs it, and nothing here goes red.
//
// **A third address, never either of the other two.** The returns are told
// apart by the address the provider lands on as well as by the tab's marker —
// `AuthService.providerReturn()` requires both — so a config naming one path
// for two trips would make one of them nobody's return.
//
// Read out of the emitted build, for the reason
// `registration-redirect-uri.spec.ts` gives.
//
// Requires a production build: `npm run build && npm test`.

const RELEASE_PATH = '/release';

// `app-config.local.json` is checked too whenever it is present, as the other
// two redirect pins check it: a developer's browser follows that file.
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

function googleUri(path: string, key: string): string | null {
  const config: unknown = JSON.parse(readFileSync(path, 'utf8'));
  const uri = property(property(property(config, 'auth'), 'google'), key);

  return typeof uri === 'string' && uri.length > 0 ? uri : null;
}

function lockedSignInUri(path: string): string | null {
  return googleUri(path, 'lockedSignInRedirectUri');
}

function registrationUri(path: string): string | null {
  return googleUri(path, 'redirectUri');
}

function emailChangeUri(path: string): string | null {
  return googleUri(path, 'emailChangeRedirectUri');
}

function originOf(uri: string | null): string | null {
  return uri !== null && URL.canParse(uri) ? new URL(uri).origin : null;
}

describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  // Without this the assertions below would also pass on a configuration whose
  // key was renamed or dropped — each of them filters on a value.
  it('emits an OAuth configuration that declares a locked sign-in redirect uri', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const missing = configs
      .filter((path) => lockedSignInUri(path) === null)
      .map((path) => relative(browserDir, path));

    // Assert
    expect(configs.length).toBeGreaterThan(0);
    expect(missing).toEqual([]);
  });

  it("sends the provider's locked sign-in redirect to the release screen", () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const elsewhere = configs
      .map((path) => ({ path, uri: lockedSignInUri(path) }))
      .filter(
        ({ uri }) =>
          uri === null ||
          !URL.canParse(uri) ||
          new URL(uri).pathname !== RELEASE_PATH,
      )
      .map(({ path, uri }) => `${relative(browserDir, path)}: ${uri}`);

    // Assert
    expect(elsewhere).toEqual([]);
  });

  it('returns the locked sign-in to the origin the registration returns to', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const mismatched = configs
      .filter((path) => {
        const locked = originOf(lockedSignInUri(path));

        return locked === null || locked !== originOf(registrationUri(path));
      })
      .map((path) => relative(browserDir, path));

    // Assert
    expect(mismatched).toEqual([]);
  });

  it('keeps the locked sign-in redirect apart from the other two', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const shared = configs
      .filter((path) => {
        const locked = lockedSignInUri(path);

        return (
          locked === registrationUri(path) || locked === emailChangeUri(path)
        );
      })
      .map((path) => relative(browserDir, path));

    // Assert
    expect(shared).toEqual([]);
  });
});
