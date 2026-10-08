import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import type * as typescriptModule from 'typescript';
import { describe, expect, it } from 'vitest';
import { emittedFiles, expectProductionBuild } from './production-bundle';

// FR-034/FR-035: no console line carries an email, a credential subject or a
// narrative value. `no-console-outside-funnel.spec.ts` holds the application's
// half by reading `src/`. This holds the libraries' half by reading what ships:
// zone.js, Angular, the CDK and the OAuth library each reach the console on
// their own, and an upgrade that adds `console.log(token)` to one of them
// changes nothing in `src/`.
//
// Each site is keyed by its source text with every renameable identifier
// replaced by `_`, so a rebuild that renames `t` to `n` keeps the key and a new
// call does not. The key drops the chunk name, which is hashed. It does not
// follow the console through a property: `this.console.warn(...)` is the
// router calling core's `Console` service, which is keyed at its own
// `console.warn(_)`.
//
// Requires a production build: `npm run build && npm test`.

// Loaded at run time rather than imported, so the compiler is not bundled into
// the spec. Only its types are imported.
const ts = createRequire(join(process.cwd(), 'package.json'))(
  'typescript',
) as typeof typescriptModule;

const printer = ts.createPrinter({ removeComments: true });

const GLOBAL = 'console';

// Where a site is keyed: the nearest call around the reference, or failing
// that the nearest statement. A class member and a variable declaration also
// stop the walk, because the statement above them is the whole class or the
// whole `var` list, which a minifier makes one line of an entire module.
function siteOf(reference: typescriptModule.Node): typescriptModule.Node {
  let at = reference.parent;
  while (
    !ts.isCallExpression(at) &&
    !ts.isStatement(at) &&
    !ts.isClassElement(at) &&
    !ts.isVariableDeclaration(at) &&
    !ts.isSourceFile(at)
  ) {
    at = at.parent;
  }

  return at;
}

// A name that belongs to an object rather than to a scope: `x.name`,
// `{ name: 1 }`, `class { name = 1 }`, `{ name: local } = x`. A minifier never
// renames these, so they stay in the key.
function isPropertyName(node: typescriptModule.Identifier): boolean {
  const parent = node.parent;

  return (
    (ts.isPropertyAccessExpression(parent) && parent.name === node) ||
    (ts.isPropertyAssignment(parent) && parent.name === node) ||
    (ts.isShorthandPropertyAssignment(parent) && parent.name === node) ||
    (ts.isBindingElement(parent) && parent.propertyName === node) ||
    (ts.isClassElement(parent) && parent.name === node)
  );
}

// An identifier that names something rather than reading a binding: a
// property, or a declaration's own name, which is every node whose `name` it
// is. `{ console }` is the exception, because the shorthand reads the binding
// it spells.
function isOwnName(node: typescriptModule.Identifier): boolean {
  const parent = node.parent;
  if (ts.isShorthandPropertyAssignment(parent)) {
    return false;
  }

  return isPropertyName(node) || ('name' in parent && parent.name === node);
}

// Every identifier a minifier may rename becomes `_`. Literals, property
// names and `console` itself are kept.
function keyOf(
  site: typescriptModule.Node,
  source: typescriptModule.SourceFile,
): string {
  const anonymise: typescriptModule.TransformerFactory<
    typescriptModule.Node
  > = (context) => {
    const visit = (node: typescriptModule.Node): typescriptModule.Node =>
      ts.isIdentifier(node) && node.text !== GLOBAL && !isPropertyName(node)
        ? ts.factory.createIdentifier('_')
        : ts.visitEachChild(node, visit, context);

    return (node) => visit(node);
  };
  const [anonymised] = ts.transform(site, [anonymise]).transformed;

  return printer
    .printNode(ts.EmitHint.Unspecified, anonymised ?? site, source)
    .replace(/\s*\n\s*/g, ' ');
}

// One key per reference to the global `console` in a script.
function consoleSiteKeys(fileName: string, text: string): string[] {
  const source = ts.createSourceFile(
    fileName,
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const keys: string[] = [];
  const visit = (node: typescriptModule.Node): void => {
    if (ts.isIdentifier(node) && node.text === GLOBAL && !isOwnName(node)) {
      keys.push(keyOf(siteOf(node), source));
    }
    ts.forEachChild(node, visit);
  };
  visit(source);

  return keys;
}

interface ReviewedSite {
  readonly count: number;
  readonly reason: string;
}

// Every place the shipped scripts reach the global `console`, keyed by
// `consoleSiteKeys` and counted. Each reason says what the site prints and why
// that cannot be an email, a subject or a narrative value today. A site that
// could is not tabled here; it is fixed. A new key, or a second copy of a
// tabled one, is meant to fail this case and be read.
//
// Several reasons lean on a pin elsewhere: the funnel replacing Angular's
// `ErrorHandler`, the OAuth library's logger and zone.js's rejection printer
// is held by `app.config.spec.ts`, and the implicit flow with no refresh is
// held by `no-console-outside-funnel.spec.ts`.
const reviewedSites = new Map<string, ReviewedSite>([
  // The funnel, `log-failure.ts`.
  [
    'console.error(_, _(_[0]))',
    {
      count: 1,
      reason:
        'log-failure: a literal reason and the closed projection of the cause',
    },
  ],
  [
    'console.error(_)',
    {
      count: 6,
      reason:
        'log-failure: the literal reason alone. ' +
        'CDK MediaMatcher: the insertRule error for a CDK breakpoint constant. ' +
        'OAuth processIdToken: the literal "Token has expired". ' +
        'OAuth fetchAndProcessToken: the id-token rejection, which can name ' +
        'two subjects, reached only under the code flow or a refresh. ' +
        'OAuth initAuthorizationCodeFlow: reached only under the code flow. ' +
        'zone.js: the whole rejection, printed only while ' +
        'ignoreConsoleErrorUncaughtError is unset, which the funnel sets',
    },
  ],

  // Angular core and router.
  [
    '_console = console;',
    {
      count: 1,
      reason:
        "core's default ErrorHandler, which prints the whole error; never " +
        'constructed, because the funnel provides the ErrorHandler',
    },
  ],
  [
    'console.log(_)',
    {
      count: 1,
      reason:
        "core's Console.log; its one importer, the router, calls only warn",
    },
  ],
  [
    'console.warn(_)',
    {
      count: 1,
      reason:
        "core's Console.warn; the router's one call prints the bare code " +
        'NG04018 for an unparseable URL, never the URL',
    },
  ],
  [
    'console.warn(_(953, !1))',
    {
      count: 1,
      reason: 'core: the bare code NG0953, an output emitting after destroy',
    },
  ],

  // zone.js.
  [
    'console.error("Unhandled Promise rejection:", _ instanceof _ ? _.message : _, "; Zone:", _.zone.name, "; Task:", _.task && _.task.source, "; Value:", _, _ instanceof _ ? _.stack : void 0)',
    {
      count: 1,
      reason:
        'the whole rejection, printed only while ' +
        'ignoreConsoleErrorUncaughtError is unset, which the funnel sets',
    },
  ],

  // angular-oauth2-oidc.
  [
    'return console;',
    {
      count: 1,
      reason:
        "the library's default OAuthLogger factory; the funnel's logger " +
        'is registered after it and wins',
    },
  ],
  [
    'console.error("No OAuthStorage provided and cannot access default (sessionStorage).Consider providing a custom OAuthStorage implementation in your module.", _)',
    {
      count: 1,
      reason: "the browser's own exception for a refused sessionStorage read",
    },
  ],
  [
    'console.error("wrong origin requested silent refresh!")',
    { count: 1, reason: 'a literal; nothing sets up a silent refresh' },
  ],
  [
    'console.log("false event firing")',
    { count: 1, reason: 'a literal, from the popup login flow' },
  ],
  [
    'console.warn("sessionChecksEnabled is activated but there is no sessionCheckIFrameUrl")',
    { count: 1, reason: 'a literal; session checks are not enabled' },
  ],
  [
    'console.warn("sessionChecksEnabled is activated but there is no session_state")',
    { count: 1, reason: 'a literal; session checks are not enabled' },
  ],
  [
    'console.error("Error in initImplicitFlow", _)',
    {
      count: 1,
      reason:
        'the whole createLoginUrl rejection; initLoginFlow() passes no ' +
        'login_hint, so nothing it builds or throws names the person',
    },
  ],
  [
    'console.warn("No PKCE verifier found in oauth storage!")',
    { count: 1, reason: 'a literal, from the code flow' },
  ],
  [
    'console.error("Error validating tokens")',
    { count: 1, reason: 'a literal, from the code flow or a refresh' },
  ],
  [
    'console.error("Error getting token", _)',
    {
      count: 1,
      reason:
        "the token endpoint's raw failure, reached only under the code " +
        'flow or a refresh',
    },
  ],
  [
    'console.error("Validating access_token failed, wrong state/nonce.", _, _)',
    {
      count: 1,
      reason:
        'the stored nonce, which the library mints at random, and the ' +
        'returned state, which is that nonce alone because the application ' +
        'passes no additional state',
    },
  ],
  [
    'console.error({ now: _, issuedAtMSec: _, expiresAtMSec: _ })',
    {
      count: 1,
      reason: 'processIdToken: three timestamps, in milliseconds',
    },
  ],
  [
    'console.error("Error in initAuthorizationCodeFlow")',
    { count: 1, reason: 'a literal, from the code flow' },
  ],
]);

// How many times each key occurs across every emitted script.
function sitesInBundle(): Map<string, number> {
  const counts = new Map<string, number>();
  for (const path of emittedFiles(['.js'])) {
    for (const key of consoleSiteKeys(path, readFileSync(path, 'utf8'))) {
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  }

  return counts;
}

describe('the console in the production bundle', () => {
  it('is read from a production build', () => {
    expectProductionBuild();
  });

  it('is reached from the reviewed sites and nowhere else', () => {
    // Arrange
    const expected = Object.fromEntries(
      [...reviewedSites].map(([key, { count }]) => [key, count]),
    );

    // Act
    const found = Object.fromEntries(sitesInBundle());

    // Assert
    expect(found).toEqual(expected);
  });
});

describe('a console site key', () => {
  it('survives a minifier rename and tells two calls apart', () => {
    // Act
    const first = consoleSiteKeys('a.js', 'console.error(a,b);');
    const renamed = consoleSiteKeys('b.js', 'console.error(x,y);');
    const other = consoleSiteKeys('c.js', 'console.log(t);');

    // Assert
    expect(renamed).toEqual(first);
    expect(other).not.toEqual(first);
  });

  it('is taken for the global and not for a name that only spells it', () => {
    // Arrange — the router keeps an injected `console` field and calls
    // `this.console.warn`; neither is the global.
    const spellings = [
      'this.console.warn(x);',
      'x.console;',
      'class A { console = d(); }',
      'const o = { console: 1 };',
      'var console = 1;',
      'function console() {}',
      'function f(console) {}',
      'const { console: c } = x;',
    ];

    // Act
    const keys = spellings.flatMap((text) => consoleSiteKeys('a.js', text));

    // Assert
    expect(keys).toEqual([]);
  });

  it('is taken wherever the global is read, called or not', () => {
    // Act
    const keys = [
      'function f() { return console; }',
      'class A { c = console; }',
      'f({ console });',
    ].flatMap((text) => consoleSiteKeys('a.js', text));

    // Assert
    expect(keys).toEqual(['return console;', 'c = console;', '_({ console })']);
  });
});
