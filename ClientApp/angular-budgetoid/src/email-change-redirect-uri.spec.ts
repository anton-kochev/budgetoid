import { readFileSync } from 'node:fs';
import { basename, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// Where Google sends a browser back to once the person has chosen the account
// whose address they want: the settings screen, which reads the answer before
// the first route draws and offers **Confirm with your passkey**. See
// docs/design/components.md, "Changing the email address".
//
// **No test in this repository can see the other half of this.** The same URI
// has to be listed among the authorized redirect URIs of the Google Cloud
// console's OAuth client, and a mismatch is refused by Google with
// `redirect_uri_mismatch` before a single line of this application runs: the
// browser never comes back, so nothing here is reached to fail. Changing this
// value without changing the console entry takes the email change down for
// everybody, and the only thing that goes red is a person's browser.
//
// **A second address, never the registration one.** The two returns are told
// apart by the address the provider lands on as well as by the tab's marker —
// `AuthService.providerReturn()` requires both — so a config naming one path
// for both trips would make every email change read as nobody's return.
//
// Read out of the emitted build rather than out of `public/assets/`, for the
// reason `registration-redirect-uri.spec.ts` gives: the shipped
// `app-config.json` is what the browser fetches, and a source file the build
// never copies would prove nothing.
//
// Requires a production build: `npm run build && npm test`.

const SETTINGS_PATH = '/app/settings';

// `app-config.local.json` is a gitignored dev override, checked here too
// whenever it is present, exactly as the registration pin checks it: a
// developer's browser follows that file, so an email change it cannot return
// from is broken on the machine it is being written on. Its absence on a clean
// checkout is not a failure.
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

function emailChangeUri(path: string): string | null {
  return googleUri(path, 'emailChangeRedirectUri');
}

function registrationUri(path: string): string | null {
  return googleUri(path, 'redirectUri');
}

function originOf(uri: string | null): string | null {
  return uri !== null && URL.canParse(uri) ? new URL(uri).origin : null;
}

describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  // Without this the assertions below would also pass on a configuration whose
  // key was renamed or dropped — every one of them filters on a value.
  it('emits an OAuth configuration that declares an email-change redirect uri', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const missing = configs
      .filter((path) => emailChangeUri(path) === null)
      .map((path) => relative(browserDir, path));

    // Assert
    expect(configs.length).toBeGreaterThan(0);
    expect(missing).toEqual([]);
  });

  it("sends the provider's email-change redirect to the settings screen", () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const elsewhere = configs
      .map((path) => ({ path, uri: emailChangeUri(path) }))
      .filter(
        ({ uri }) =>
          uri === null ||
          !URL.canParse(uri) ||
          new URL(uri).pathname !== SETTINGS_PATH,
      )
      .map(({ path, uri }) => `${relative(browserDir, path)}: ${uri}`);

    // Assert
    expect(elsewhere).toEqual([]);
  });

  // The same application answers both trips, so both addresses sit on one
  // origin. One that differs is a return to some other host's settings screen.
  it('returns the email change to the origin the registration returns to', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const mismatched = configs
      .filter((path) => {
        const emailChange = originOf(emailChangeUri(path));

        return (
          emailChange === null ||
          emailChange !== originOf(registrationUri(path))
        );
      })
      .map((path) => relative(browserDir, path));

    // Assert
    expect(mismatched).toEqual([]);
  });

  it('keeps the email-change redirect apart from the registration one', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const shared = configs
      .filter((path) => emailChangeUri(path) === registrationUri(path))
      .map((path) => relative(browserDir, path));

    // Assert
    expect(shared).toEqual([]);
  });
});
