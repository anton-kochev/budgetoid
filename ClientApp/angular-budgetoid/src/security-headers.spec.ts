import { existsSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  browserDir,
  expectProductionBuild,
  listFiles,
} from './production-bundle';

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
// The whole policy is held by one reviewed table rather than by a test per directive. A
// test per directive reads only the directives somebody thought to name, which leaves
// three ways to widen the policy in silence: add a directive nobody reads
// (`script-src-attr 'unsafe-inline'` re-permits exactly the inline handlers this app
// disabled critical-CSS inlining to refuse, and `report-uri` is an exfiltration channel
// spelled as a diagnostic), widen a directive nobody reads (`base-uri`, `form-action`,
// `object-src`), or repeat a directive so the two readers disagree — CSP Level 3 has the
// browser keep the *first* occurrence, so a parser where the last wins reads a policy the
// browser does not enforce. The table closes all three: the shipped directive *names* must
// equal its keys, each key's sources must equal its list, no name may appear twice, and
// the parser is first-wins for the same reason the browser is. Widening anything here
// costs a written justification, the same friction `allowedConnectSources` already
// applies to an origin.
//
// Requires a production build: `npm run build && npm test`.

const configPath = join(browserDir, 'staticwebapp.config.json');
const indexPath = join(browserDir, 'index.html');
const appConfigPath = join(browserDir, 'assets', 'app-config.json');
const prepaintPath = join(browserDir, 'theme-prepaint.js');
const themeServicePath = join(
  process.cwd(),
  'src',
  'app',
  '+core',
  'services',
  'theme.service.ts',
);

// Every header `globalHeaders` may name, each with the reason it is there — the same
// written justification `allowedConnectSources` demands of an origin, and for a sharper
// reason: `globalHeaders` is applied to every response the host serves from content, so an
// entry added here is applied site-wide by whoever adds it. A table of names to sentences
// is what stops a new header being greened by appending to an expected array; the editor
// has to compose a defence of it. Nothing about that is limited to headers that look like
// security: `Access-Control-Allow-Origin` does not, and is the one this closes.
//
// One table, two rules, and the second is why the names are lower-cased. Azure unions a
// route's `headers` with `globalHeaders` and lets the route win per header name — and
// route rules are not applied at all to a request that ends up in `navigationFallback`. A
// header named here and re-declared on a route is therefore a hole shaped like a deep
// link: it silently replaces the global value on the paths it matches and is absent on the
// paths it does not. A second list of the same names could disagree with this one, so
// there is one: a header joins both rules at once, or leaves both at once.
const securityHeaders: ReadonlyMap<string, string> = new Map([
  [
    'content-security-policy',
    'the origin boundary the browser enforces; the directives inside it carry their own ' +
      'reviewed table in `policy`, and this entry is what keeps the header itself from ' +
      'leaving',
  ],
  [
    'strict-transport-security',
    'the transport half of that boundary — without it a first request over `http://` is ' +
      'answered before any policy the response carries can matter',
  ],
  [
    'referrer-policy',
    'an origin is still an identifier, and this app has no reason to tell any other ' +
      'origin where a reader came from',
  ],
  [
    'x-content-type-options',
    'governs what a response is allowed to become, which no CSP directive does: a ' +
      "sniffed same-origin response runs as a script `script-src 'self'` permits",
  ],
]);

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

// The whole reviewed policy: every directive the header may name, the exact sources it may
// name, and why. Adding a key, removing one, or changing a source list is a security
// decision and reads like one — the `why` is what the next editor has to overwrite, and an
// entry nobody can defend in a sentence does not belong in the policy.
const policy: ReadonlyMap<
  string,
  { readonly sources: readonly string[]; readonly why: string }
> = new Map([
  [
    'default-src',
    {
      sources: ["'self'"],
      why:
        'what makes the directives below a boundary rather than a coincidence: a fetch ' +
        'destination none of them covers — a worker, a media element, a manifest — ' +
        'falls back here instead of being unconstrained',
    },
  ],
  [
    'script-src',
    {
      sources: ["'self'"],
      why:
        "`'unsafe-inline'`, `'unsafe-eval'`, `'unsafe-hashes'`, a hash, a nonce or a " +
        'host each re-opens the injection path the header exists to close',
    },
  ],
  [
    'style-src',
    {
      sources: ["'self'", "'unsafe-inline'"],
      why:
        "`'unsafe-inline'` is deliberate: Angular Material writes component styles " +
        "into the document at runtime, so dropping it breaks the app's appearance in " +
        'production and nothing else in the suite would notice',
    },
  ],
  [
    'img-src',
    {
      sources: ["'self'"],
      why:
        '`data:` is the source a reader adds to make one inlined icon work; it also ' +
        'admits every attacker-controlled byte string as an image',
    },
  ],
  [
    'font-src',
    {
      sources: ["'self'"],
      why: 'typefaces are served from `public/fonts` — see docs/engineering/no-third-party-origins.md',
    },
  ],
  [
    'connect-src',
    {
      sources: [...allowedConnectSources.keys()],
      why:
        'each origin carries its own written justification in `allowedConnectSources`; ' +
        'widening the list means writing one there',
    },
  ],
  [
    'frame-ancestors',
    {
      sources: ["'none'"],
      why:
        'it has no `default-src` fallback, so removing it restores framing by any ' +
        'origin, and a second source is a clickjacking surface',
    },
  ],
  [
    'base-uri',
    {
      sources: ["'self'"],
      why:
        'an injected `<base href>` repoints every relative URL in the document, ' +
        "including the script sources `script-src 'self'` was meant to pin",
    },
  ],
  [
    'form-action',
    {
      sources: ["'self'"],
      why:
        'a form posted to another origin exfiltrates whatever the reader typed, ' +
        'whatever `connect-src` says',
    },
  ],
  [
    'object-src',
    {
      sources: ["'none'"],
      why: 'a plugin document is a script execution context `script-src` does not police',
    },
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
      .filter(([header]) => securityHeaders.has(header))
      .map(([header]) => `${name}: ${header}`);
  });
}

// A directive is a name and a source list separated by whitespace; directives are
// separated by `;`. The name is matched case-insensitively, as the grammar says. The
// sources are not lower-cased: a host source is case-sensitive, and every keyword this
// app uses is written lower-case, so folding case here would hide a policy that says
// something subtly different from what it appears to say.
//
// The header in declaration order, repeats included — the only reading from which
// `names no directive twice` can see a repeat at all.
function policyDirectives(): readonly {
  readonly name: string;
  readonly sources: readonly string[];
}[] {
  const header = globalHeaders().get('content-security-policy') ?? '';

  return header
    .split(';')
    .map((directive) => directive.trim())
    .filter((directive) => directive.length > 0)
    .map((directive) => {
      const [name, ...sources] = directive.split(/\s+/);

      return { name: name.toLowerCase(), sources } as const;
    });
}

// First occurrence wins, because that is what CSP Level 3 says a browser does with a
// repeated directive. A map built the other way round reads the value the browser
// ignores, which turns every assertion below into a statement about a policy nobody
// enforces.
function contentSecurityPolicy(): ReadonlyMap<string, ReadonlySet<string>> {
  const enforced = new Map<string, ReadonlySet<string>>();

  for (const { name, sources } of policyDirectives()) {
    if (!enforced.has(name)) {
      enforced.set(name, new Set(sources));
    }
  }

  return enforced;
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

// Every `<script>` start tag the document opens, counted whether or not it declares a
// `src`. This is the positive control for `scriptSources`: a `src` pattern that stopped
// matching a form the document uses reports no source for that tag and therefore no
// unresolved source either, which is indistinguishable from a document whose scripts all
// resolve. Counting the tags gives the pattern something it has to account for.
function scriptStartTagCount(html: string): number {
  return [...html.matchAll(/<script\b/gi)].length;
}

// Every `src` a `<script>` element in the document declares, in document order.
//
// It refuses to read only the quoted forms. `<script src=/theme-prepaint.js>` is valid
// HTML — the attribute value needs no quotes until it contains whitespace — and while the
// builder emits quotes, `index.html` is hand-edited and was hand-edited in the change this
// test shipped with. A pattern blind to the unquoted form does not report a broken `src`;
// it reports no `src` at all, which is why the count above exists.
//
// `(?<![-\w])` is load-bearing in the other direction: a bare `\bsrc` also matches the tail
// of `data-src`, so on a tag carrying both, an unrelated attribute could supply the value
// checked against the emitted files and redden a document that is correct.
function scriptSources(html: string): readonly string[] {
  return [
    ...html.matchAll(
      /<script\b[^>]*?(?<![-\w])src\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))/gi,
    ),
  ].map(
    ([, doubleQuoted, singleQuoted, unquoted]) =>
      doubleQuoted ?? singleQuoted ?? unquoted ?? '',
  );
}

// The emitted file a `src` names, or `undefined` when the build emitted none. An absolute
// or scheme-relative URL resolves to nothing by construction — this build emits files, not
// origins — so it fails here as well as in `no-external-origins.spec.ts`, which is the
// direction to fail in.
//
// It refuses to ask the file system. `existsSync` answers a question the deploy target does
// not: macOS matches a name case-insensitively, so `<script src="/Theme-Prepaint.js">`
// against an emitted `theme-prepaint.js` is green here and a 404 on Azure Static Web Apps —
// a guard that only works on Linux CI is no guard on the machine it runs on every day.
// Comparing against the names the build actually emitted is case-sensitive on every
// platform, and subsumes the two checks it replaces: `listFiles` yields files only, all of
// them under the browser directory, so a `..` climbing out of the output matches nothing.
function emittedFileFor(src: string): string | undefined {
  const [path] = src.split(/[?#]/);

  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(path) || path.startsWith('//')) {
    return undefined;
  }

  // The deployed root is the browser directory, so a root-relative `src` is resolved
  // against it and a document-relative one against the document beside it — the same
  // place, since `index.html` sits at that root.
  const resolved = resolve(browserDir, path.replace(/^\/+/, ''));
  const emitted = new Set(listFiles(browserDir));

  return emitted.has(resolved) ? resolved : undefined;
}

// The one storage key a file names, so that two files naming it can be compared instead of
// both being retyped here. A file that names none throws rather than returning a default:
// a silent `undefined` on both sides would compare equal and assert nothing.
//
// A file that names it more than once throws too, rather than taking the first match. The
// first match is not the call the browser makes, it is the first *text* that looks like
// one — and `theme-prepaint.js` already carries a ten-line comment block whose most natural
// edit is to mention the key. Quote it there, rename the real `getItem`, and a first-match
// read compares the comment against the service and passes while the pre-paint reads a dead
// key. The same holds for a second `const STORAGE_KEY` in the service. Every occurrence is
// collected regardless of the flags the caller passed, so the rule cannot be lost at a call
// site by omitting `g`.
function storageKeyIn(path: string, pattern: RegExp, expected: string): string {
  const source = existsSync(path) ? readFileSync(path, 'utf8') : '';
  const everywhere = pattern.flags.includes('g')
    ? pattern
    : new RegExp(pattern.source, `${pattern.flags}g`);
  const keys = [...source.matchAll(everywhere)].map(([, key]) => key);

  if (keys.length === 0) {
    throw new Error(`${path}: ${expected}`);
  }

  if (keys.length > 1) {
    const named = keys.map((key) => `'${key}'`).join(', ');

    throw new Error(
      `${path}: names ${keys.length} keys (${named}) where the comparison needs exactly one`,
    );
  }

  return keys[0];
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

  // Set equality over the header *names*, which is the assertion the per-header tests
  // below cannot make: each of them reads one name, so nothing they do constrains an
  // *extra* entry. `globalHeaders` is applied to every response the host serves from
  // content, so `"Access-Control-Allow-Origin": "*"` added beside the four would be
  // applied site-wide and redden nothing — the header set has the same shape of hole as
  // the directive list one level down, and `names exactly the reviewed directives` closes
  // that one for the same reason: a policy is widened by addition, not only by edit.
  //
  // It catches a header going missing too, which is how `Referrer-Policy` would leave —
  // there loudly rather than silently, since `sends no referrer` reddens beside it.
  it('names exactly the reviewed global headers', () => {
    // Arrange
    const location = relative(browserDir, configPath);
    const reviewed = [...securityHeaders.keys()].sort();

    // Act
    const shipped = [...globalHeaders().keys()].sort();

    // Assert
    expect(
      shipped,
      `${location}: every global header needs a reviewed entry in \`securityHeaders\``,
    ).toEqual(reviewed);
  });

  // The condition is derived from the shipped policy rather than assumed: an inline
  // `<script>` or an `on*=` attribute is refused only because `script-src` withholds
  // `'unsafe-inline'`, so this reads the emitted policy and holds the document to what it
  // actually says. A policy that admitted `'unsafe-inline'` would leave nothing here to
  // check, and that is deliberately not guarded a second time — `admits exactly the
  // reviewed sources for script-src` reddens on exactly that change, first and by name. A
  // policy naming no `script-src` at all is scanned: it inherits `default-src`, and
  // reading an absent directive as permission is the one reading that would ship a broken
  // page.
  //
  // `script-src-attr` would shadow this: `script-src-attr 'unsafe-inline'` re-permits
  // every inline handler while `script-src` still reads `'self'`, so the condition below
  // would go on scanning a document the browser no longer refuses. Nothing here can see
  // that — what prevents it is `names exactly the reviewed directives`, because
  // `script-src-attr` is not a key of the table.
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

  // A `<script src>` pointing at nothing is a 404 in `<head>` that no policy assertion can
  // see: the policy permits `'self'`, and a file the build never emitted is still `'self'`.
  // Deleting `public/theme-prepaint.js` is the concrete case — the document keeps
  // referencing it, the browser fetches nothing, and the only symptom is a theme flash.
  it('loads every script from a file it emitted', () => {
    // Arrange
    const location = relative(browserDir, indexPath);
    const html = readFileSync(indexPath, 'utf8');

    // Act
    const tags = scriptStartTagCount(html);
    const sources = scriptSources(html);
    const unresolved = sources
      .filter((src) => emittedFileFor(src) === undefined)
      .map(
        (src) =>
          `${location}: <script src="${src}"> resolves to no emitted file`,
      );

    // Assert
    expect(
      sources.length,
      `${location}: loads no script at all`,
    ).toBeGreaterThan(0);
    // Every `<script>` in this document has a `src` — `contains nothing the script policy
    // would block` forbids the other kind — so a tag whose `src` went unread is a tag this
    // test silently stopped checking.
    expect(
      sources.length,
      `${location}: ${tags} <script> start tags, ${sources.length} of them with a src this test could read`,
    ).toBe(tags);
    expect(unresolved).toEqual([]);
  });

  // The pre-paint exists to read the theme before Angular runs, which means it reads
  // `localStorage` with a key `ThemeService` writes — the same literal in two files, in two
  // languages, with nothing but this reconciling them. Rename it in the service and the
  // pre-paint reads a dead key: no error, no failing build, just the flash of the wrong
  // mode the script was added to beat. The key is read out of the service rather than
  // retyped here, so this test cannot drift from it either.
  it('pre-paints the theme from the key the theme service persists', () => {
    // Arrange
    const location = relative(browserDir, prepaintPath);
    const persisted = storageKeyIn(
      themeServicePath,
      /const STORAGE_KEY = '([^']*)'/,
      "declares no `const STORAGE_KEY = '…'` for the pre-paint to agree with",
    );

    // Act
    const prepainted = storageKeyIn(
      prepaintPath,
      /localStorage\.getItem\(\s*'([^']*)'\s*\)/,
      'reads no key from localStorage',
    );

    // Assert
    expect(
      prepainted,
      `${location}: pre-paints from '${prepainted}', ThemeService persists '${persisted}'`,
    ).toBe(persisted);
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
    const enforced = contentSecurityPolicy();
    const empty = [...enforced]
      .filter(([, sources]) => sources.size === 0)
      .map(([name]) => `${location}: ${name} names no source`);

    // Assert
    expect(
      enforced.size,
      `${location}: declares no Content-Security-Policy`,
    ).toBeGreaterThan(0);
    expect(empty).toEqual([]);
  });

  // Set equality over the *names*, which is the assertion a per-directive test cannot
  // make: a directive nobody thought to read is read here by existing. It catches the
  // shadowing family — `script-src-elem`, `script-src-attr`, `style-src-elem`,
  // `style-src-attr`, `frame-src`, `child-src`, `worker-src`, `manifest-src`,
  // `media-src`, `prefetch-src`, `fenced-frame-src` — each of which overrides or fills in
  // for a directive asserted below, and `report-uri`/`report-to`, which are an
  // exfiltration channel in their own right. It also catches a directive going missing,
  // which is how `base-uri` would leave.
  it('names exactly the reviewed directives', () => {
    // Arrange
    const location = relative(browserDir, configPath);
    const reviewed = [...policy.keys()].sort();

    // Act
    const shipped = [
      ...new Set(policyDirectives().map(({ name }) => name)),
    ].sort();

    // Assert
    expect(
      shipped,
      `${location}: every directive needs a reviewed entry in \`policy\``,
    ).toEqual(reviewed);
  });

  // One case per reviewed directive, so a failure names the directive and prints the
  // justification the editor is overwriting. Set equality in both directions: a source
  // added in place — `base-uri 'self' https://evil.example` — reddens exactly like a
  // source removed, which a `toContain` assertion cannot do.
  it.each([...policy].map(([name, reviewed]) => ({ name, ...reviewed })))(
    'admits exactly the reviewed sources for $name',
    ({ name, sources, why }) => {
      // Arrange
      const location = relative(browserDir, configPath);
      const reviewed = [...sources].sort();

      // Act
      const shipped = sourceListOf(name);

      // Assert
      expect(shipped, `${location}: ${name} — ${why}`).toEqual(reviewed);
    },
  );

  // Read from the raw header, not from the map: a map has one entry per name whichever
  // occurrence it keeps, so a repeat is invisible there by construction. A repeated
  // directive is how a policy says two different things at once — the browser enforces the
  // first, a reader reads the last, and every assertion above becomes a statement about
  // whichever one the parser happened to keep.
  it('names no directive twice', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const names = policyDirectives().map(({ name }) => name);
    const repeated = [
      ...new Set(names.filter((name, index) => names.indexOf(name) !== index)),
    ].map((name) => `${location}: Content-Security-Policy repeats ${name}`);

    // Assert
    expect(repeated).toEqual([]);
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

describe('content type options', () => {
  // The policy governs what the document may load; this governs what a response is
  // allowed to become. Without it a browser may sniff a response whose `Content-Type` it
  // distrusts and execute a user-supplied upload or a JSON body as a script — a path
  // `script-src 'self'` permits, because a same-origin response is `'self'` whatever its
  // bytes say.
  it('refuses content type sniffing', () => {
    // Arrange
    const location = relative(browserDir, configPath);

    // Act
    const declared = globalHeaders().get('x-content-type-options');

    // Assert
    expect(declared, `${location}: X-Content-Type-Options`).toBe('nosniff');
  });
});
