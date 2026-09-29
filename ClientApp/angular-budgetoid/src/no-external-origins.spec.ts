import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { basename, join, relative } from 'node:path';
import type * as typescriptModule from 'typescript';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  emittedFiles,
  expectProductionBuild,
} from './production-bundle';

// FR-030/FR-031: the app loads nothing from an origin other than its own. This test
// reads the production build rather than the sources, because the sources are not what
// the browser fetches — the builder inlines the `@font-face` CSS it finds into
// `index.html`, so a CDN font link disappears from `index.html` as authored and
// reappears there as an inlined `fonts.gstatic.com` URL.
//
// It also holds the build-time half of NFR-025, that the identity provider is contacted
// only while an account is being created. What a build can show is how the client is
// able to address the provider — which origin it names, and which of the provider
// client's members it calls — not when it does. The when is held by four specs:
// - `src/app/+core/core.providers.cold-boot.spec.ts` — the boot against the real
//   library;
// - `src/app/+core/core.providers.spec.ts` — the initializer's decision over a stub;
// - `src/app/+core/services/auth-service.spec.ts` — the press, the once-per-page-load
//   preparation, and what counts as the provider coming back;
// - `src/identity-provider-callers.spec.ts` — which file may call which member of the
//   provider client.
//
// Requires a production build: `npm run build && npm test`.
// See docs/engineering/no-third-party-origins.md.

// Loaded at run time rather than imported, so the compiler is not bundled into
// the spec. Only its types are imported.
const ts = createRequire(join(process.cwd(), 'package.json'))(
  'typescript',
) as typeof typescriptModule;

// FR-030. Absolute URLs that may appear in a JavaScript bundle without any of them
// being fetched. Each entry is a deliberate exception, not an oversight; a new library
// that drags in a new origin is meant to fail this test and be looked at.
const neverFetchedOrigins = new Map<string, string>([
  [
    'http://www.w3.org',
    'XML namespace URIs in inline SVG — declarative, never fetched',
  ],
  ['https://angular.dev', 'documentation link inside an Angular error message'],
  ['https://ngrx.io', 'documentation link inside an NgRx error message'],
  ['https://github.com', 'documentation link inside a library error message'],
  ['https://bit.ly', 'documentation link inside a library error message'],
]);

// NFR-025. Origins the bundle names because it does contact them — at a moment the
// requirement bounds, which a build cannot see.
const identityProviderOrigins = new Map<string, string>([
  [
    'https://accounts.google.com',
    'the OpenID issuer configured in auth-service.ts, reached today during ' +
      "registration's provider exchange: the press on /register and the " +
      'redirect back to it. Each leg fetches the discovery document from here ' +
      'and then the key set from its jwks_uri on www.googleapis.com; the press ' +
      'then navigates here. When is held by core.providers.cold-boot.spec.ts, ' +
      'core.providers.spec.ts, auth-service.spec.ts and ' +
      'identity-provider-callers.spec.ts, not here',
  ],
]);

const reviewedOrigins = new Set([
  ...neverFetchedOrigins.keys(),
  ...identityProviderOrigins.keys(),
]);

const providerOrigin = 'https://accounts.google.com';

// The one form the provider origin is expected to take in the bundle: the `issuer`
// member of the object `AuthService` hands to `OAuthService.configure`. esbuild
// writes string literals with double quotes and object keys unquoted.
const issuerMember = `issuer:"${providerOrigin}"`;

// How much text either side of an unexpected occurrence is reported, so the
// failure says what the occurrence sits in.
const CONTEXT_LENGTH = 40;

// Members of angular-oauth2-oidc's `OAuthService` that put the application in
// standing contact with the provider, or send the person to it:
// `setupAutomaticSilentRefresh` plants a hidden iframe pointed at the provider and
// re-runs it on a timer for as long as the tab is open; `revokeTokenAndLogout`
// posts to the provider's revocation endpoint and then logs out through it.
// Refused under any argument count.
//
// Not listed, because a zero count is impossible: `silentRefresh`, `refreshToken`
// and `loadUserProfile`. The library calls each of them itself, inside its own
// code, so every build carries a call and the list would be red for a reason
// nobody here can change. `refreshToken` and `loadUserProfile` are also not a
// standing contact this bundle could make work: they reach hosts `connect-src`
// does not admit.
const refusedAtAnyArity = new Set([
  'setupAutomaticSilentRefresh',
  'revokeTokenAndLogout',
]);

// `logOut()` with no argument navigates the page to the provider's end-session
// endpoint whenever the library knows one — from the discovery document's
// `end_session_endpoint` or from configuration. Google publishes none today, so
// the refusal holds against what the provider may publish next, not against a
// redirect that happens now. Only a boolean `true` suppresses that redirect:
// `logOut(true)` is the local-discard overload — it clears this application's
// copy of the tokens and never navigates — and is what
// `AuthService.forgetProviderToken` calls. `logOut(false)` and `logOut({})`
// navigate exactly as the bare call does whenever the library knows a logout URL.
//
// A call carrying any argument is allowed anyway, because the library calls
// `logOut(e)` itself inside its own code, so a count of every argued call could
// never be zero. That is a known gap, not a safe case: `logOut(false)` in this
// application would pass here. It is on the hardening backlog.
const refusedBare = 'logOut';

// `AuthService.forgetProviderToken`'s own discard, as `providerCallsIn` records
// it. esbuild writes `true` as `!0`; either spelling is the local overload.
const localDiscard = /: forgetProviderToken\.logOut\((?:true|!0)\)$/;

const urlPattern = /https?:\/\/[^\s"'`)\\<>]+/g;

function urlsIn(path: string): string[] {
  return readFileSync(path, 'utf8').match(urlPattern) ?? [];
}

function originOf(url: string): string {
  return new URL(url).origin;
}

// Every occurrence of the provider origin in one file: `issuerMember` when it
// sits in that member, and the file name with the surrounding text otherwise.
function providerOccurrencesIn(path: string): string[] {
  const text = readFileSync(path, 'utf8');
  const occurrences: string[] = [];
  const prefix = 'issuer:"';
  for (
    let start = text.indexOf(providerOrigin);
    start !== -1;
    start = text.indexOf(providerOrigin, start + providerOrigin.length)
  ) {
    const end = start + providerOrigin.length;
    const member = text.slice(start - prefix.length, end + 1);
    occurrences.push(
      member === issuerMember
        ? member
        : `${relative(browserDir, path)}: ${text.slice(
            Math.max(0, start - CONTEXT_LENGTH),
            end + CONTEXT_LENGTH,
          )}`,
    );
  }

  return occurrences;
}

interface ProviderCalls {
  // Calls this spec refuses, as `file: member(argc)`.
  readonly refused: readonly string[];
  // `logOut` calls carrying an argument — allowed, and recorded as
  // `file: enclosing.logOut(arguments)` so a scan that never reached
  // `AuthService` cannot pass on an empty refusal list.
  readonly discards: readonly string[];
}

// The name of a called member: `x.name(…)` or `x["name"](…)`. Property names
// survive esbuild's minification, so the spelling in the bundle is the spelling
// in the source. The receiver is not checked, because the minifier renames the
// local that holds the `OAuthService`.
function calledMemberName(
  node: typescriptModule.CallExpression,
): string | null {
  const callee = node.expression;
  if (ts.isPropertyAccessExpression(callee)) {
    return callee.name.text;
  }
  if (
    ts.isElementAccessExpression(callee) &&
    ts.isStringLiteralLike(callee.argumentExpression)
  ) {
    return callee.argumentExpression.text;
  }

  return null;
}

// The name of the nearest named method or function around `node`, or
// `(top level)`. esbuild keeps method names, so `forgetProviderToken` in the
// source is `forgetProviderToken` in the chunk; an unnamed function is looked
// through.
function enclosingName(node: typescriptModule.Node): string {
  for (let at = node.parent; at !== undefined; at = at.parent) {
    if (
      (ts.isMethodDeclaration(at) ||
        ts.isFunctionDeclaration(at) ||
        ts.isFunctionExpression(at)) &&
      at.name !== undefined &&
      (ts.isIdentifier(at.name) || ts.isStringLiteral(at.name))
    ) {
      return at.name.text;
    }
  }

  return '(top level)';
}

function providerCallsIn(fileName: string, text: string): ProviderCalls {
  const source = ts.createSourceFile(
    fileName,
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const refused: string[] = [];
  const discards: string[] = [];
  const visit = (node: typescriptModule.Node): void => {
    if (ts.isCallExpression(node)) {
      const name = calledMemberName(node);
      const site = `${fileName}: ${name}(${node.arguments.length})`;
      if (name !== null && refusedAtAnyArity.has(name)) {
        refused.push(site);
      } else if (name === refusedBare && node.arguments.length === 0) {
        refused.push(site);
      } else if (name === refusedBare) {
        const argued = node.arguments.map((argument) => argument.getText());
        discards.push(
          `${fileName}: ${enclosingName(node)}.${name}(${argued.join(', ')})`,
        );
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);

  return { refused, discards };
}

// The tests below scan `.html`, `.css`, `.js` and `.svg`, and nothing else.
// `assets/app-config*.json` carries the API base URL and the OAuth redirect URI. Both
// are runtime configuration — an XHR target and a navigation target — not resources the
// document loads, so the JSON is out of scope here.
describe('production build', () => {
  it('is present and is a production build', () => {
    expectProductionBuild();
  });

  it('references no external origin from the document or its stylesheets', () => {
    // Arrange
    const documents = emittedFiles(['.html', '.css']);

    // Act
    const external = documents.flatMap((path) =>
      urlsIn(path).map((url) => `${relative(browserDir, path)}: ${url}`),
    );

    // Assert
    expect(external).toEqual([]);
  });

  // `www.googleapis.com`, where the provider's signing keys live, is on neither
  // list: the library learns that URL from the discovery document at run time, so
  // the bundle never carries it, and a build that did would fail here.
  it('reaches no unreviewed origin from its scripts and images', () => {
    // Arrange
    const bundles = emittedFiles(['.js', '.svg']);

    // Act
    const unreviewed = bundles.flatMap((path) =>
      urlsIn(path)
        .map(originOf)
        .filter((origin) => !reviewedOrigins.has(origin))
        .map((origin) => `${relative(browserDir, path)}: ${origin}`),
    );

    // Assert
    expect([...new Set(unreviewed)]).toEqual([]);
  });

  // The allow-list says the origin may appear; this says where. A second
  // occurrence — a hard-coded authorize or end-session URL, a second configured
  // client — would add no origin and pass the test above.
  it('names the identity provider once, as the issuer it configures', () => {
    // Arrange
    const files = emittedFiles(['.js', '.html', '.css', '.svg']);

    // Act
    const occurrences = files.flatMap(providerOccurrencesIn);

    // Assert
    expect(
      occurrences.length,
      'no occurrence at all — the scan read nothing, or the issuer moved',
    ).toBeGreaterThanOrEqual(1);
    expect(occurrences).toEqual([issuerMember]);
  });

  it("calls none of the provider client's standing-contact members", () => {
    // Arrange
    const scripts = emittedFiles(['.js']);

    // Act
    const calls = scripts.map((path) =>
      providerCallsIn(basename(path), readFileSync(path, 'utf8')),
    );
    const refused = calls.flatMap((found) => found.refused);
    const discards = calls.flatMap((found) => found.discards);

    // Assert — the floor is the application's own discard, not any argued
    // call: the library calls `logOut(e)` inside its own code, so a scan that
    // read the vendor chunk and missed `AuthService` would still count one.
    expect(
      discards.filter((site) => localDiscard.test(site)),
      'no logOut(true) inside forgetProviderToken — the scan did not reach AuthService',
    ).not.toEqual([]);
    expect(refused).toEqual([]);
  });
});
