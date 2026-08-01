import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

// FR-030/FR-031: the app loads nothing from an origin other than its own. This test
// reads the production build rather than the sources, because the sources are not what
// the browser fetches — the builder inlines the `@font-face` CSS it finds into
// `index.html`, so a CDN font link disappears from `index.html` as authored and
// reappears there as an inlined `fonts.gstatic.com` URL.
//
// Requires a production build: `npm run build && npm test`.
// See docs/engineering/no-third-party-origins.md.

// Only the emitted browser directory is served. `3rdpartylicenses.txt` and
// `prerendered-routes.json` sit one level above it and never reach a browser.
const browserDir = join(process.cwd(), 'dist', 'angular-budgetoid', 'browser');

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

// `assets/app-config*.json` carries the API base URL and the OAuth redirect URI. Both
// are runtime configuration — an XHR target and a navigation target — not resources the
// document loads, so the JSON is out of scope here.
const scannedExtensions = ['.html', '.css', '.js', '.svg'];

const urlPattern = /https?:\/\/[^\s"'`)\\<>]+/g;

function listFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);

    return statSync(path).isDirectory() ? listFiles(path) : [path];
  });
}

function scannedFiles(): string[] {
  return listFiles(browserDir).filter((path) =>
    scannedExtensions.some((extension) => path.endsWith(extension)),
  );
}

function urlsIn(path: string): string[] {
  return readFileSync(path, 'utf8').match(urlPattern) ?? [];
}

function originOf(url: string): string {
  return new URL(url).origin;
}

describe('production build', () => {
  it('is present and is a production build', () => {
    // Arrange
    const message = `${browserDir} is missing — run \`npm run build\` first`;

    // Act
    const built = existsSync(browserDir);

    // Assert
    expect(built, message).toBe(true);
    // `outputHashing: all` is production-only, so an unhashed entry point means a
    // development build is sitting in dist/ and this suite would pass too easily.
    expect(readFileSync(join(browserDir, 'index.html'), 'utf8')).toMatch(
      /main-[A-Z0-9]+\.js/,
    );
  });

  it('references no external origin from the document or its stylesheets', () => {
    // Arrange
    const documents = scannedFiles().filter(
      (path) => path.endsWith('.html') || path.endsWith('.css'),
    );

    // Act
    const external = documents.flatMap((path) =>
      urlsIn(path).map((url) => `${relative(browserDir, path)}: ${url}`),
    );

    // Assert
    expect(external).toEqual([]);
  });

  it('reaches no unreviewed origin from its scripts and images', () => {
    // Arrange
    const bundles = scannedFiles().filter(
      (path) => path.endsWith('.js') || path.endsWith('.svg'),
    );

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
