// Bundles the page's entry with the esbuild the Angular toolchain builds with.
//
// Same bundler, same target, same minifier the application build uses — so the
// code under measurement is the code that ships, module graph and all. Nothing
// is written to disk: the bundle goes straight into the in-memory server.
//
// **The version in `devDependencies` is exact, and it is exact on purpose.**
// `@angular/build` depends on one pinned esbuild, and this declaration names
// that same version so npm resolves a single copy and the harness bundles with
// the toolchain's bundler rather than one of its own. Keep the two in step: a
// caret here would let them drift, and the drift would be invisible in a number.
//
// It was previously imported without being declared anywhere, and resolved only
// because npm happened to hoist `@angular/build`'s copy to the top of
// `node_modules` — so `npm run perf:narrative` died on a bare
// module-not-found under pnpm or `--install-strategy=nested`, on the tool whose
// exit code is supposed to mean the measurement did not happen.
import esbuild from 'esbuild';
import { fileURLToPath } from 'node:url';

/** Bundles `browser/entry.ts` and returns the script the page loads. */
export async function bundleBrowserEntry(): Promise<string> {
  const here = fileURLToPath(new URL('.', import.meta.url));
  const src = fileURLToPath(new URL('../../src', import.meta.url));
  const result = await esbuild.build({
    // The three aliases the application compiles with. Every current import
    // through them is type-only and would be erased anyway; they are here so a
    // value import added to a view module later fails to *resolve* rather than
    // silently pulling a second copy of something out of node_modules.
    alias: {
      '@app-core': `${src}/app/+core`,
      '@app-shared': `${src}/app/+shared`,
      '@app-state': `${src}/app/+state`,
    },
    bundle: true,
    entryPoints: [`${here}browser/entry.ts`],
    format: 'iife',
    globalName: 'narrativePerf',
    // Minified for the same reason the alias list is here — this is meant to be
    // the shipped shape. `keepNames` keeps a thrown stack readable.
    keepNames: true,
    logLevel: 'silent',
    minify: true,
    platform: 'browser',
    sourcemap: false,
    target: 'es2022',
    write: false,
  });
  const file = result.outputFiles?.[0];

  if (file === undefined) {
    throw new Error('esbuild produced no output for the harness page.');
  }

  return file.text;
}

/** The page itself: a title, and the bundle as a classic script. */
export function harnessPage(): string {
  return [
    '<!doctype html>',
    '<html lang="en">',
    '<head>',
    '<meta charset="utf-8">',
    '<title>narrative-perf</title>',
    '</head>',
    '<body>',
    '<p>narrative field measurement harness</p>',
    '<script src="/narrative-perf.js"></script>',
    '</body>',
    '</html>',
    '',
  ].join('\n');
}
