import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, relative, sep } from 'node:path';
import type * as typescriptModule from 'typescript';
import { describe, expect, it } from 'vitest';
import { listFiles } from './production-bundle';

// FR-034/FR-035: no console line carries an email, a credential subject or a
// narrative value. `log-failure.ts` is the one module that prints, and it prints
// a literal reason and a closed projection of the cause. This census holds the
// "one module" half by reading `src/` and the scripts in `public/`.
// `no-console-in-bundle.spec.ts` holds the libraries' half.
//
// Lint is not enough on its own. `no-console` is switched off by one inline
// `eslint-disable-next-line`, and nothing in the lint run reports that the
// escape was used. A census over the source text has no inline escape. It is
// also what holds `main.ts` and the transactions write paths: both used to
// print the cause itself, and the lint rules only arrived with the funnel.
//
// The needle runs over the source with its comments removed, so prose that
// names the console does not count. Strings are kept, so
// `Reflect.get(globalThis, 'console')` still counts. The comments are removed
// by the TypeScript printer, not a regex, because a regex cannot tell `//`
// inside a string, template or regular expression from a comment. The last case
// checks the stripper keeps a real token.
//
// Specs and `src/testing/` are left out: they spy on the console to hold this
// rule, and they never ship.

const sourceDir = join(process.cwd(), 'src');
const testingDir = join(sourceDir, 'testing');
const publicDir = join(process.cwd(), 'public');

// Loaded at run time rather than imported, so the compiler is not bundled into
// the spec. Only its types are imported.
const ts = createRequire(join(process.cwd(), 'package.json'))(
  'typescript',
) as typeof typescriptModule;

const printer = ts.createPrinter({ removeComments: true });

function withoutComments(fileName: string, text: string): string {
  const source = ts.createSourceFile(
    fileName,
    text,
    ts.ScriptTarget.Latest,
    false,
    fileName.endsWith('.js') ? ts.ScriptKind.JS : ts.ScriptKind.TS,
  );

  return printer.printFile(source);
}

interface ShippedSource {
  readonly path: string;
  readonly code: string;
}

function shippedSource(path: string): ShippedSource {
  return {
    path: relative(process.cwd(), path).split(sep).join('/'),
    code: withoutComments(path, readFileSync(path, 'utf8')),
  };
}

// Every shipped `.ts` file under `src/`, and every script under `public/`,
// which the builder copies into the bundle as written — `theme-prepaint.js`
// runs before the application does. Paths are relative to the client root
// with `/` separators.
function shippedSources(): ShippedSource[] {
  const application = listFiles(sourceDir)
    .filter((path) => path.endsWith('.ts') && !path.endsWith('.spec.ts'))
    .filter((path) => !path.startsWith(testingDir + sep));
  const copied = listFiles(publicDir).filter((path) => path.endsWith('.js'));

  return [...application, ...copied].map(shippedSource);
}

function filesMatching(
  sources: readonly ShippedSource[],
  needle: RegExp,
): string[] {
  return sources
    .filter(({ code }) => needle.test(code))
    .map(({ path }) => path)
    .sort();
}

const CONSOLE = /\bconsole\b/;

describe('the console', () => {
  const sources = shippedSources();

  it('is read from enough files for the census to mean something', () => {
    // Assert — a moved `src/`, a filter that lets nothing through or a walk
    // that stops at the top level would leave every case below green.
    expect(sources.length).toBeGreaterThanOrEqual(80);
    expect(sources.map(({ path }) => path)).toContain('src/main.ts');
    expect(sources.map(({ path }) => path)).toContain(
      'public/theme-prepaint.js',
    );
  });

  it('is named by no shipped file but the funnel', () => {
    // Act
    const naming = filesMatching(sources, CONSOLE);

    // Assert
    expect(naming).toEqual(['src/app/+core/logging/log-failure.ts']);
  });

  it('is kept from the OAuth library by one registration', () => {
    // Arrange — `provideOAuthClient()` registers the library's own logger,
    // which is `console`. `app.config.ts` places the funnel's logger after it,
    // and `app.config.spec.ts` pins that order. A second call anywhere else is
    // a second place the library's console logger can win.

    // Act
    const registering = filesMatching(sources, /\bprovideOAuthClient\s*\(/);

    // Assert
    expect(registering).toEqual(['src/app/app.config.ts']);
  });

  it.each([
    {
      call: 'setupAutomaticSilentRefresh',
      needle: /\bsetupAutomaticSilentRefresh\b/,
    },
    { call: 'silentRefresh(', needle: /\bsilentRefresh\s*\(/ },
    { call: 'refreshToken(', needle: /\brefreshToken\s*\(/ },
  ])('is not reached through the OAuth refresh path: $call', ({ needle }) => {
    // Arrange — the refresh path can reach the console without passing
    // through `OAuthLogger`, so replacing the logger does not cover it.
    // `silentRefresh()` records the current id token's `sub` claim as
    // `silentRefreshSubject`. When the token that comes back names another
    // subject, `processIdToken` rejects with a sentence carrying both
    // subjects, but it builds that sentence only when `sessionChecksEnabled`
    // and `silentRefreshSubject` are both set. `sessionChecksEnabled` is off
    // by default and this application never sets it. Under
    // `responseType: 'code'` that rejection lands in `fetchAndProcessToken`'s
    // `catch`, which writes it with `console.error(reason)`, and the same
    // method's error callback writes the token endpoint's raw failure with
    // `console.error('Error getting token', err)`. Under the implicit flow
    // configured today the rejection goes through the logger instead. So the
    // subject-bearing leak needs the code flow, `sessionChecksEnabled` and a
    // `silentRefresh()` call, not one config line.
    // `setupAutomaticSilentRefresh()` schedules the refresh, and `refreshToken()` is its other arm. The
    // application refreshes nothing (see `auth-service.ts`), so all three stay
    // out whichever flow is configured.

    // Act
    const calling = filesMatching(sources, needle);

    // Assert
    expect(calling).toEqual([]);
  });

  it('is not reached through the OAuth code flow', () => {
    // Arrange — the library chooses the code flow from `responseType`. Its
    // code exchange writes the token endpoint's raw failure with
    // `console.error('Error getting token', err)` and an id-token rejection,
    // which can name two subjects, with `console.error(reason)`, both
    // directly and past the funnel's logger. The one legitimate spelling is
    // the HttpClient option in the base API service. The word alone is the
    // needle, not `responseType:`, so an assignment such as
    // `oAuth.responseType = 'code'` counts too.

    // Act
    const naming = filesMatching(sources, /\bresponseType\b/);

    // Assert
    expect(naming).toEqual(['src/app/+core/api/base-api.service.ts']);
  });

  it('is not reached through a computed read of a global object', () => {
    // Arrange — `globalThis['con' + 'sole']` and
    // `Reflect.get(window, name)` spell the console with no `console` token,
    // and the lint rules see neither. The one legitimate reader is the
    // funnel's own provider, which takes zone.js's two unhandled-rejection
    // keys off `Zone` with `Reflect.get(globalThis, 'Zone')`.
    const computed =
      /\b(?:globalThis|window|self)\s*(?:\?\.)?\[|\bReflect\s*\.\s*\w+\s*\(\s*(?:globalThis|window|self)\b/;

    // Act
    const reading = filesMatching(sources, computed);

    // Assert
    expect(reading).toEqual([
      'src/app/+core/logging/provide-failure-logging.ts',
    ]);
  });

  it('is still found once the comments are gone', () => {
    // Arrange — every shape a naive stripper eats: `//` inside a string, a
    // template and a regular expression, and `/*` inside a template. Each line
    // ends in a real `console` token that has to survive, and the comment-only
    // lines are the control.
    const real = [
      "const url = 'https://example.test'; console.log(url);",
      'const t = `/* ${url} */`; console.warn(t);',
      'const r = /\\/\\//; console.info(r);',
      "const key = Reflect.get(globalThis, 'console');",
    ];
    const prose = [
      '// console.log(1);',
      '/* console.log(1); */',
      '/** Writes to the console. */ export const x = 1;',
    ];

    // Act
    const kept = real.map((line) => withoutComments('real.ts', line));
    const stripped = prose.map((line) => withoutComments('prose.ts', line));

    // Assert
    for (const code of kept) {
      expect(code).toMatch(CONSOLE);
    }
    for (const code of stripped) {
      expect(code).not.toMatch(CONSOLE);
    }
  });
});
