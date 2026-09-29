import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { basename, join } from 'node:path';
import type * as typescriptModule from 'typescript';
import { describe, expect, it } from 'vitest';
import { emittedFiles, expectProductionBuild } from './production-bundle';

// Loaded at run time rather than imported, so the compiler is not bundled into
// the spec. Only its types are imported.
const ts = createRequire(join(process.cwd(), 'package.json'))(
  'typescript',
) as typeof typescriptModule;

// A site is reported by its own text, cut short: a minified right-hand side can
// be a whole template literal, and the file name already says where to look.
const SNIPPET_LENGTH = 160;

interface CookieSites {
  readonly writes: readonly string[];
  readonly reads: readonly string[];
}

// `x.name` or `x["name"]`. Both are property names, which a minifier never
// renames, so the spelling in the bundle is the spelling in the source.
function isMemberNamed(
  node: typescriptModule.Node,
  name: string,
): node is
  | typescriptModule.PropertyAccessExpression
  | typescriptModule.ElementAccessExpression {
  if (ts.isPropertyAccessExpression(node)) {
    return node.name.text === name;
  }

  return (
    ts.isElementAccessExpression(node) &&
    ts.isStringLiteralLike(node.argumentExpression) &&
    node.argumentExpression.text === name
  );
}

// `=`, `+=`, `||=` and every other operator that stores into its left side.
function isAssignment(
  node: typescriptModule.Node,
): node is typescriptModule.BinaryExpression {
  const kind = ts.isBinaryExpression(node) ? node.operatorToken.kind : null;

  return (
    kind !== null &&
    kind >= ts.SyntaxKind.FirstAssignment &&
    kind <= ts.SyntaxKind.LastAssignment
  );
}

// The Cookie Store API: the global, or the same object reached through
// `window`, `self` or any other holder of it.
function isCookieStore(node: typescriptModule.Expression): boolean {
  return (
    (ts.isIdentifier(node) && node.text === 'cookieStore') ||
    isMemberNamed(node, 'cookieStore')
  );
}

function isCookieStoreWrite(node: typescriptModule.Node): boolean {
  if (!ts.isCallExpression(node)) {
    return false;
  }
  const callee = node.expression;

  return (
    (isMemberNamed(callee, 'set') || isMemberNamed(callee, 'delete')) &&
    isCookieStore(callee.expression)
  );
}

// Every place a script stores a cookie, and every place it reads one. The
// receiver of `.cookie` is not checked: a minifier hands `document` to a local
// as readily as anything else, so `e.cookie = …` is a write whatever `e` is.
function cookieSites(fileName: string, text: string): CookieSites {
  const source = ts.createSourceFile(
    fileName,
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const writes: string[] = [];
  const reads: string[] = [];
  const snippet = (node: typescriptModule.Node): string =>
    node.getText(source).slice(0, SNIPPET_LENGTH);
  const visit = (node: typescriptModule.Node): void => {
    if (isAssignment(node) && isMemberNamed(node.left, 'cookie')) {
      writes.push(snippet(node));
    } else if (isCookieStoreWrite(node)) {
      writes.push(snippet(node));
    } else if (
      isMemberNamed(node, 'cookie') &&
      !(isAssignment(node.parent) && node.parent.left === node)
    ) {
      reads.push(snippet(node));
    }
    ts.forEachChild(node, visit);
  };
  visit(source);

  return { writes, reads };
}

// Every emitted script's cookie sites, each prefixed with its chunk's name.
function sitesInBundle(): CookieSites {
  const writes: string[] = [];
  const reads: string[] = [];
  for (const path of emittedFiles(['.js'])) {
    const sites = cookieSites(path, readFileSync(path, 'utf8'));
    const name = basename(path);
    writes.push(...sites.writes.map((site) => `${name}: ${site}`));
    reads.push(...sites.reads.map((site) => `${name}: ${site}`));
  }

  return { writes, reads };
}

describe('the production bundle', () => {
  it('is read from a production build', () => {
    expectProductionBuild();
  });

  it('reads a cookie somewhere', () => {
    // Arrange — Angular reads cookies in two places today: platform-browser's
    // getCookie and HttpClient's XSRF token reader. A scan that finds neither
    // walked nothing, and a clean write list from it would prove nothing. The
    // count is not pinned, because it is Angular's to change.

    // Act
    const { reads } = sitesInBundle();

    // Assert
    expect(reads.length).toBeGreaterThanOrEqual(1);
  });

  it('sets no cookie from any shipped script', () => {
    // Act
    const { writes } = sitesInBundle();

    // Assert
    expect(writes).toEqual([]);
  });
});

describe('a cookie write', () => {
  it("is taken for an assignment to any object's cookie", () => {
    // Arrange
    const writes = ['document.cookie="a";', 'e.cookie="a";', 'e.cookie+="a";'];

    // Act
    const found = writes.flatMap((text) => cookieSites('a.js', text).writes);

    // Assert
    expect(found).toEqual([
      'document.cookie="a"',
      'e.cookie="a"',
      'e.cookie+="a"',
    ]);
  });

  it('is taken for a string-keyed assignment', () => {
    // Act
    const found = cookieSites('a.js', 'e["cookie"]="a";').writes;

    // Assert
    expect(found).toEqual(['e["cookie"]="a"']);
  });

  it('is taken for a cookieStore call', () => {
    // Arrange
    const writes = [
      'cookieStore.set("t","1");',
      'window.cookieStore.delete("t");',
    ];

    // Act
    const found = writes.flatMap((text) => cookieSites('a.js', text).writes);

    // Assert
    expect(found).toEqual([
      'cookieStore.set("t","1")',
      'window.cookieStore.delete("t")',
    ]);
  });

  it('is not taken for a read or a name that only spells it', () => {
    // Arrange
    const spellings = [
      'x=document.cookie;',
      'e.cookie||"";',
      'cookieName=d(N);',
      '({cookie:1});',
      'lastCookieString;',
    ];

    // Act
    const found = spellings.flatMap((text) => cookieSites('a.js', text).writes);

    // Assert
    expect(found).toEqual([]);
  });
});
