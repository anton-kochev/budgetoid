import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { browserDir, expectProductionBuild } from './production-bundle';

// FR-036: the browser itself enforces the app's origin boundary, so that a defect in the
// app cannot become an exfiltration channel. A `Content-Security-Policy` that admits only
// this origin is only worth having if the document it guards can actually load under it —
// an inline script or an inline event handler is refused by `script-src 'self'`, and the
// page breaks the moment the header ships.
//
// This test reads the emitted configuration and the emitted `index.html` rather than
// `public/` and `src/`, because the deployed configuration is the copy inside the
// directory the deploy workflow uploads, and the document the browser parses is the one
// the builder wrote: critical-CSS inlining adds markup to `index.html` that no source
// file contains.
//
// FR-037 adds `Strict-Transport-Security`, FR-038 the transport and framing halves of the
// policy, FR-039 `Referrer-Policy`. Every assertion over the policy compares *source
// sets*, never the header text: a string-equality assertion reddens on a harmless
// reordering, and the cheapest way to green it is to paste the actual value over the
// expected one — at which point the test asserts only that the header equals itself.
//
// Requires a production build: `npm run build && npm test`.

const configPath = join(browserDir, 'staticwebapp.config.json');
const indexPath = join(browserDir, 'index.html');
const appConfigPath = join(browserDir, 'assets', 'app-config.json');

// The header names a route rule may never carry, lower-cased for the same reason the
// header maps below are. Azure unions a route's `headers` with `globalHeaders` and lets
// the route win per header name — and route rules are not applied at all to a request
// that ends up in `navigationFallback`. A security header on a route is therefore a hole
// shaped like a deep link: it silently replaces the global value on the paths it matches
// and is absent on the paths it does not.
const securityHeaderNames: readonly string[] = [
  'content-security-policy',
  'strict-transport-security',
  'referrer-policy',
];

// Every source `connect-src` may name, each with the reason it is there. The value is a
// written justification rather than a label, following `no-external-origins.spec.ts`: an
// origin nobody can defend in a sentence does not belong in the policy, and an origin
// added by a library upgrade is meant to fail this test and be looked at.
const allowedConnectSources = new Map<string, string>([
  ["'self'", "the app's own origin, which serves the document and its assets"],
  [
    'https://api.budgetoid.app',
    'the API — the origin `assets/app-config.json` configures the app to call',
  ],
  [
    'https://accounts.google.com',
    'the OpenID discovery document for federated sign-in; removed with it',
  ],
  [
    'https://www.googleapis.com',
    'the JWKS the discovery document points at; removed with federated sign-in',
  ],
]);

function property(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null && key in value
    ? (value as Record<string, unknown>)[key]
    : undefined;
}

// Keyed by lower-cased name throughout: HTTP header names are case-insensitive, so a
// later casing change in the configuration must not turn a lookup here red.
function headerEntries(value: unknown): readonly (readonly [string, string])[] {
  if (typeof value !== 'object' || value === null) {
    return [];
  }

  return Object.entries(value).flatMap(([name, header]: [string, unknown]) =>
    typeof header === 'string' ? [[name.toLowerCase(), header] as const] : [],
  );
}

function readConfig(): unknown {
  return JSON.parse(readFileSync(configPath, 'utf8'));
}

function globalHeaders(): ReadonlyMap<string, string> {
  return new Map(headerEntries(property(readConfig(), 'globalHeaders')));
}

// Every security header a `routes[]` entry declares, named by the route that declares it.
function routeSecurityHeaders(): readonly string[] {
  const routes = property(readConfig(), 'routes');

  if (!Array.isArray(routes)) {
    return [];
  }

  return (routes as readonly unknown[]).flatMap((route) => {
    const path = property(route, 'route');
    const name = typeof path === 'string' ? path : '(unnamed route)';

    return headerEntries(property(route, 'headers'))
      .filter(([header]) => securityHeaderNames.includes(header))
      .map(([header]) => `${name}: ${header}`);
  });
}

// A directive is a name and a source list separated by whitespace; directives are
// separated by `;`. The name is matched case-insensitively, as the grammar says. The
// sources are not lower-cased: a host source is case-sensitive, and every keyword this
// app uses is written lower-case, so folding case here would hide a policy that says
// something subtly different from what it appears to say.
function contentSecurityPolicy(): ReadonlyMap<string, ReadonlySet<string>> {
  const header = globalHeaders().get('content-security-policy') ?? '';

  return new Map(
    header
      .split(';')
      .map((directive) => directive.trim())
      .filter((directive) => directive.length > 0)
      .map((directive) => {
        const [name, ...sources] = directive.split(/\s+/);

        return [name.toLowerCase(), new Set(sources)] as const;
      }),
  );
}

// Sorted rather than compared as a `Set` so a failure prints an ordered diff naming the
// source that appeared or went missing. A directive the policy does not name resolves to
// an empty list, which is never equal to any expectation below — an absent directive
// reddens exactly like a wrong one.
function sourceListOf(directive: string): readonly string[] {
  return [...(contentSecurityPolicy().get(directive) ?? [])].sort();
}

// `max-age=…` carries a value, `preload` does not; a valueless directive maps to the
// empty string so presence and value stay separate questions.
function strictTransportSecurity(): ReadonlyMap<string, string> {
  const header = globalHeaders().get('strict-transport-security') ?? '';

  return new Map(
    header
      .split(';')
      .map((directive) => directive.trim())
      .filter((directive) => directive.length > 0)
      .map((directive) => {
        const separator = directive.indexOf('=');

        return separator === -1
          ? ([directive.toLowerCase(), ''] as const)
          : ([
              directive.slice(0, separator).trim().toLowerCase(),
              directive.slice(separator + 1).trim(),
            ] as const);
      }),
  );
}

function apiOrigin(): string {
  const apiBaseUrl = property(
    JSON.parse(readFileSync(appConfigPath, 'utf8')),
    'apiBaseUrl',
  );

  if (typeof apiBaseUrl !== 'string') {
    throw new Error(
      `${relative(browserDir, appConfigPath)}: declares no apiBaseUrl`,
    );
  }

  return new URL(apiBaseUrl).origin;
}

function lineOf(html: string, index: number): number {
  return html.slice(0, index).split('\n').length;
}

function tagNameOf(tag: string): string {
  return (
    tag.match(/^<([a-zA-Z][a-zA-Z0-9-]*)/)?.[1]?.toLowerCase() ?? 'element'
  );
}

// A lexical scan, not a parse: it walks start tags and their attribute names and is
// deliberately over-eager, because a false positive here names a line to look at while a
// false negative ships a document the policy refuses.
//
// It reports what a `script-src` without `'unsafe-inline'` would refuse; whether that is
// a violation is the caller's question, decided by reading the shipped policy.
//
// An inline `<style>` element is deliberately not a violation: the policy carries
// `style-src 'unsafe-inline'`, so forbidding one here would forbid what the policy
// permits.
function scriptPolicyViolations(html: string): readonly string[] {
  const violations: string[] = [];

  for (const tag of html.matchAll(/<[a-zA-Z][^>]*>/g)) {
    const markup = tag[0];
    const name = tagNameOf(markup);
    const attributes = markup.slice(name.length + 1);
    const line = lineOf(html, tag.index ?? 0);

    if (name === 'script' && !/(^|[\s/])src\s*=/i.test(attributes)) {
      violations.push(`inline <script> element (line ${line})`);
    }

    for (const [, handler] of attributes.matchAll(
      /(?:^|[\s/])(on[a-zA-Z0-9-]+)\s*=/gi,
    )) {
      violations.push(
        `inline event handler ${handler}= on <${name}> (line ${line})`,
      );
    }
  }

  return violations;
}

describe('production build', () => {
  it('is emitted into the browser output as a production build', () => {
    expectProductionBuild();
  });

  // Without this, every case-insensitive header lookup a later test adds would pass
  // vacuously against a configuration that declares no global headers at all.
  it('declares global headers', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const declared = globalHeaders();

    // Assert
    expect(
      declared.size,
      `${location}: declares no global headers`,
    ).toBeGreaterThan(0);
  });

  // The condition is derived from the shipped policy rather than assumed: an inline
  // `<script>` or an `on*=` attribute is refused only because `script-src` withholds
  // `'unsafe-inline'`, so this reads the emitted policy and holds the document to what it
  // actually says. A policy that admitted `'unsafe-inline'` would leave nothing here to
  // check, and that is deliberately not guarded a second time — `admits only the app
  // origin as a script source` reddens on exactly that change, first and by name. A policy
  // naming no `script-src` at all is scanned: it inherits `default-src`, and reading an
  // absent directive as permission is the one reading that would ship a broken page.
  it('contains nothing the script policy would block', () => {
    // Arrange
    const html = readFileSync(indexPath, 'utf8');

    // Act
    const inlineScriptsPermitted =
      sourceListOf('script-src').includes("'unsafe-inline'");
    const blocked: readonly string[] = inlineScriptsPermitted
      ? []
      : scriptPolicyViolations(html).map(
          (violation) => `${relative(browserDir, indexPath)}: ${violation}`,
        );

    // Assert
    expect(blocked).toEqual([]);
  });

  // The API origin lives in `assets/app-config.json` and the permission to reach it lives
  // in the policy. Nothing but this test keeps the two in step, so moving the API without
  // widening `connect-src` breaks every request the app makes, in production only.
  it('permits a connection to the API origin the app is configured to call', () => {
    // Arrange
    const location = relative(browserDir, configPath);
    const origin = apiOrigin();

    // Act
    const sources = sourceListOf('connect-src');

    // Assert
    expect(sources, `${location}: connect-src omits ${origin}`).toContain(
      origin,
    );
  });

  it('reaches no unreviewed origin from connect-src', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('connect-src');
    const unreviewed = sources
      .filter((source) => !allowedConnectSources.has(source))
      .map((source) => `${location}: connect-src ${source}`);

    // Assert
    expect(
      sources.length,
      `${location}: declares no connect-src`,
    ).toBeGreaterThan(0);
    expect(unreviewed).toEqual([]);
  });

  it('sets no security header on a route rule', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const declared = routeSecurityHeaders().map(
      (declaration) => `${location}: ${declaration}`,
    );

    // Assert
    expect(declared).toEqual([]);
  });
});

describe('content security policy', () => {
  // The positive control. Every directive lookup below reads a policy that may not exist,
  // and an absent policy would let all of them agree with an empty source list forever.
  it('declares a policy with a source list for every directive it names', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const policy = contentSecurityPolicy();
    const empty = [...policy]
      .filter(([, sources]) => sources.size === 0)
      .map(([name]) => `${location}: ${name} names no source`);

    // Assert
    expect(
      policy.size,
      `${location}: declares no Content-Security-Policy`,
    ).toBeGreaterThan(0);
    expect(empty).toEqual([]);
  });

  // Exact set equality, so `'unsafe-inline'`, `'unsafe-eval'`, `'unsafe-hashes'`, a hash,
  // a nonce or a host all redden — each of them re-opens the injection path the header
  // exists to close.
  it('admits only the app origin as a script source', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('script-src');

    // Assert
    expect(sources, `${location}: script-src`).toEqual(["'self'"]);
  });

  // `'unsafe-inline'` is asserted by name and deliberately: Angular Material writes
  // component styles into the document at runtime, so dropping it breaks the app's
  // appearance in production and nothing else in the suite would notice.
  it('admits only the app origin and inline declarations as a style source', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('style-src');

    // Assert
    expect(
      sources,
      `${location}: style-src must keep 'unsafe-inline' — Angular Material ` +
        'injects component styles at runtime',
    ).toContain("'unsafe-inline'");
    expect(sources, `${location}: style-src`).toEqual([
      "'self'",
      "'unsafe-inline'",
    ]);
  });

  // `data:` is the source a reader adds to make one inlined icon work; it also admits
  // every attacker-controlled byte string as an image.
  it('admits only the app origin as an image source', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('img-src');

    // Assert
    expect(sources, `${location}: img-src`).toEqual(["'self'"]);
  });

  it('admits only the app origin as a font source', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('font-src');

    // Assert
    expect(sources, `${location}: font-src`).toEqual(["'self'"]);
  });

  // What makes the four directives above a boundary rather than a coincidence: without a
  // `default-src` of `'self'`, a fetch destination none of them covers — a worker, a
  // media element, a manifest — is unconstrained.
  it('falls back to the app origin for every directive it does not name', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('default-src');

    // Assert
    expect(sources, `${location}: default-src`).toEqual(["'self'"]);
  });

  // Its own test because `frame-ancestors` has no `default-src` fallback: removing it
  // restores framing by any origin and reddens nothing else here.
  it('forbids framing by any origin', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const sources = sourceListOf('frame-ancestors');

    // Assert
    expect(sources, `${location}: frame-ancestors`).toEqual(["'none'"]);
  });
});

describe('strict transport security', () => {
  // Parsed as a number rather than string-matched, because the requirement is a floor:
  // raising the value must stay green and dropping below a year must always redden.
  it('carries a max-age of at least one year', () => {
    // Arrange
    const location = relative(browserDir, configPath);
    const oneYearInSeconds = 31_536_000;

    // Act
    const declared = strictTransportSecurity().get('max-age');
    const maxAge = declared === undefined ? Number.NaN : Number(declared);

    // Assert
    expect(
      maxAge,
      `${location}: Strict-Transport-Security max-age is ${declared ?? 'absent'}`,
    ).toBeGreaterThanOrEqual(oneYearInSeconds);
  });

  // Preload-list submission is effectively irreversible and binds every future subdomain
  // of a domain that hosts no production environment yet. The presence check is what
  // stops this passing against a header that does not exist at all.
  it('does not enrol the domain in the preload list', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const declared = globalHeaders().has('strict-transport-security');
    const directives = [...strictTransportSecurity().keys()];

    // Assert
    expect(declared, `${location}: declares no Strict-Transport-Security`).toBe(
      true,
    );
    expect(directives, `${location}: Strict-Transport-Security`).not.toContain(
      'preload',
    );
  });
});

describe('referrer policy', () => {
  // `no-referrer`, not `strict-origin-when-cross-origin`: an origin is still an
  // identifier, and this app has no reason to tell any other origin where a reader came
  // from.
  it('sends no referrer', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const declared = globalHeaders().get('referrer-policy');

    // Assert
    expect(declared, `${location}: Referrer-Policy`).toBe('no-referrer');
  });
});
