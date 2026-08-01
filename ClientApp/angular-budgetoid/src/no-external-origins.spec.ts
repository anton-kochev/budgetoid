import { readFileSync } from 'node:fs';
import { relative } from 'node:path';
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
// Requires a production build: `npm run build && npm test`.
// See docs/engineering/no-third-party-origins.md.

// Absolute URLs that may appear in a JavaScript bundle without any of them being
// fetched. Each entry is a deliberate exception, not an oversight; a new library that
// drags in a new origin is meant to fail this test and be looked at.
const allowedOrigins = new Map<string, string>([
  [
    'http://www.w3.org',
    'XML namespace URIs in inline SVG — declarative, never fetched',
  ],
  ['https://angular.dev', 'documentation link inside an Angular error message'],
  ['https://ngrx.io', 'documentation link inside an NgRx error message'],
  ['https://github.com', 'documentation link inside a library error message'],
  ['https://bit.ly', 'documentation link inside a library error message'],
  [
    'https://accounts.google.com',
    'the OpenID issuer configured in auth-service.ts — a discovery request and a ' +
      'top-level navigation, not a subresource the page loads',
  ],
]);

const urlPattern = /https?:\/\/[^\s"'`)\\<>]+/g;

function urlsIn(path: string): string[] {
  return readFileSync(path, 'utf8').match(urlPattern) ?? [];
}

function originOf(url: string): string {
  return new URL(url).origin;
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

  it('reaches no unreviewed origin from its scripts and images', () => {
    // Arrange
    const bundles = emittedFiles(['.js', '.svg']);

    // Act
    const unreviewed = bundles.flatMap((path) =>
      urlsIn(path)
        .map(originOf)
        .filter((origin) => !allowedOrigins.has(origin))
        .map((origin) => `${relative(browserDir, path)}: ${origin}`),
    );

    // Assert
    expect([...new Set(unreviewed)]).toEqual([]);
  });
});
