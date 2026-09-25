import { readFileSync } from 'node:fs';
import { basename, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// Where Google sends a browser back to once the person has consented.
//
// It lands on the site root today, which is the address `authGuard` reads as
// "somebody arriving with a session", and a visitor who has just consented in
// order to *create an account* has no session to be read. The registration flow
// is the only screen that can do anything with a fresh provider token — it
// reads the asserted address off the id token, and both of its legs authenticate
// as the provider scheme and nothing else — so the callback has to arrive there.
//
// **No test in this repository can see the other half of this change.** The same
// URI has to be added to the authorized redirect URIs of the Google Cloud
// console's OAuth client, and a mismatch is refused by Google with
// `redirect_uri_mismatch` before a single line of this application runs: the
// browser never comes back, so nothing here is reached to fail. Changing this
// value without changing that one takes provider sign-in down for everybody, and
// the only thing that goes red is a person's browser. Read the console entry as
// part of this change rather than as a follow-up.
//
// Read out of the emitted build rather than out of `public/assets/`, for the
// reason `no-profile-scope.spec.ts` gives: the shipped `app-config.json` is what
// the browser fetches and hands to the OAuth client, and a source file the build
// never copies would prove nothing.
//
// Requires a production build: `npm run build && npm test`.

// The path the provider must come back to. A suffix and not the whole URL,
// because the origin differs per environment — `https://budgetoid.app` in
// production, `http://localhost:4200` in a developer's browser — and the origin
// is not what this rule is about.
const REGISTRATION_PATH = '/register';

// `app-config.local.json` is a gitignored dev override, checked here too
// whenever it is present, exactly as the scope spec beside this one checks it: a
// developer's browser follows that file, so a redirect left pointing at the site
// root there is a registration flow that is broken on the machine it is being
// written on. Its absence on a clean checkout is not a failure.
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

function redirectUri(path: string): string | null {
  const config: unknown = JSON.parse(readFileSync(path, 'utf8'));
  const uri = property(
    property(property(config, 'auth'), 'google'),
    'redirectUri',
  );

  return typeof uri === 'string' && uri.length > 0 ? uri : null;
}

describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  // Without this, the assertion below would also pass on a configuration whose
  // `redirectUri` key was renamed or dropped — a green test over a flow the
  // provider can never return from at all.
  it('emits an OAuth configuration that declares a redirect uri', () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const missing = configs
      .filter((path) => redirectUri(path) === null)
      .map((path) => relative(browserDir, path));

    // Assert
    expect(configs.length).toBeGreaterThan(0);
    expect(missing).toEqual([]);
  });

  it("sends the provider's redirect to the registration screen", () => {
    // Arrange
    const configs = emittedConfigs();

    // Act
    const elsewhere = configs
      .map((path) => ({ path, uri: redirectUri(path) }))
      .filter(({ uri }) => uri === null || !uri.endsWith(REGISTRATION_PATH))
      .map(({ path, uri }) => `${relative(browserDir, path)}: ${uri}`);

    // Assert
    expect(elsewhere).toEqual([]);
  });
});
