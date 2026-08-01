import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from 'vitest';

// Scaffolding shared by the specs that assert over the emitted production build rather
// than over `src/`. The sources are not what the browser runs: the builder inlines,
// rewrites and tree-shakes on the way out, so a rule about what ships can only be proven
// against the build output. Node-only — nothing here is reachable from `src/main.ts`.

// Only the emitted browser directory is served. `3rdpartylicenses.txt` and
// `prerendered-routes.json` sit one level above it and never reach a browser.
export const browserDir = join(
  process.cwd(),
  'dist',
  'angular-budgetoid',
  'browser',
);

export function listFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);

    return statSync(path).isDirectory() ? listFiles(path) : [path];
  });
}

export function emittedFiles(extensions: readonly string[]): string[] {
  return listFiles(browserDir).filter((path) =>
    extensions.some((extension) => path.endsWith(extension)),
  );
}

// Every spec that reads the build shares this precondition, so it lives here instead of
// being restated — and drifting — in each of them.
export function expectProductionBuild(): void {
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
}
