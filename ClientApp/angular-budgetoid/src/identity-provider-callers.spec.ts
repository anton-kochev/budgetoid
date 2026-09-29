import { createRequire } from 'node:module';
import { join, relative, sep } from 'node:path';
import type * as typescriptModule from 'typescript';
import { describe, expect, it } from 'vitest';

// NFR-025: the identity provider is contacted only while an account is being
// created, "and at no other time". This census holds the call-site half of
// that: which shipped file reaches which member of the provider client.
//
// The provider client is two classes. `AuthService`
// (`src/app/+core/services/auth-service.ts`) is the only thing that talks to
// the library, and angular-oauth2-oidc's `OAuthService` is the library.
// Every member access whose receiver is typed as either class is recorded as
// `path: member`, called or not. An element access over either is recorded as
// `path: [computed]`, and an object destructure of either as
// `path: {destructured}`, because both hide the member name from this list.
// A member reached through a narrowed type such as `Pick<AuthService, …>` is
// recorded under its own name too. `auth-service.ts` itself is not walked: it
// is the one file that holds the client, and its internals are what the
// other specs pin.
//
// The receiver is resolved by the type checker over the program
// `tsconfig.app.json` builds, plus every non-spec file under `src/app` so
// that a production file replacement is walked too. A text search cannot do
// this: `welcome.component.ts` calls a `signIn()` on another service, and
// `core.providers.ts` reaches `AuthService` through a factory parameter, not
// through `inject()`.
//
// What this does not hold:
// - What a member does. That is `auth-service.spec.ts` and the two
//   `core.providers` specs.
// - When an allowed site calls. `intro-step.component.ts` may call `signIn`;
//   whether it does so only on a press is its own spec's business.
// - A receiver cast to `any` or `unknown` first, a subclass of either class,
//   or an object spread of either. The checker sees none of these as the
//   client.
// - A component template. A template can only reach a non-private field, and
//   every holder of the client today keeps it private; a field made
//   `protected` and called from a template would not be seen here.
// - Anything inside `auth-service.ts`, including a direct property write on
//   `OAuthService` that bypasses `configure`. That is on the hardening backlog.

// Loaded at run time rather than imported, so the compiler is not bundled into
// the spec. Only its types are imported.
const ts = createRequire(join(process.cwd(), 'package.json'))(
  'typescript',
) as typeof typescriptModule;

const clientRoot = process.cwd();
const applicationDir = join(clientRoot, 'src', 'app');
const authServicePath = join(
  applicationDir,
  '+core',
  'services',
  'auth-service.ts',
);
const libraryDir = `${sep}node_modules${sep}angular-oauth2-oidc${sep}`;

// Paths relative to the client root with `/` separators, whatever the host.
function relativePath(fileName: string): string {
  return relative(clientRoot, fileName).split(sep).join('/');
}

function isUnder(fileName: string, directory: string): boolean {
  return join(fileName).startsWith(directory);
}

function parsedAppConfig(): typescriptModule.ParsedCommandLine {
  const parsed = ts.getParsedCommandLineOfConfigFile(
    join(clientRoot, 'tsconfig.app.json'),
    {},
    {
      ...ts.sys,
      onUnRecoverableConfigFileDiagnostic: (diagnostic) => {
        throw new Error(
          ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'),
        );
      },
    },
  );
  if (parsed === undefined) {
    throw new Error('tsconfig.app.json could not be read');
  }

  return parsed;
}

function applicationSources(): string[] {
  return ts.sys
    .readDirectory(applicationDir, ['.ts'])
    .filter((path) => !path.endsWith('.spec.ts') && !path.endsWith('.d.ts'));
}

// Shipped source files in `program`: under `src/`, not a spec, not a
// declaration, and not in `src/testing/`.
function isShippedSource(file: typescriptModule.SourceFile): boolean {
  const sourceDir = join(clientRoot, 'src');

  return (
    isUnder(file.fileName, sourceDir + sep) &&
    !isUnder(file.fileName, join(sourceDir, 'testing') + sep) &&
    !file.isDeclarationFile &&
    !file.fileName.endsWith('.spec.ts')
  );
}

interface Walk {
  readonly walked: readonly string[];
  readonly reaches: readonly string[];
}

// Every reach of the provider client in the files `program` holds that
// `include` admits, as `path: member` sorted and de-duplicated.
function providerReaches(
  program: typescriptModule.Program,
  include: (file: typescriptModule.SourceFile) => boolean,
): Walk {
  const checker = program.getTypeChecker();
  const authServiceFile = program.getSourceFile(authServicePath);
  const authServiceClass = authServiceFile?.statements.find(
    (statement): statement is typescriptModule.ClassDeclaration =>
      ts.isClassDeclaration(statement) &&
      statement.name?.text === 'AuthService',
  );
  if (authServiceClass === undefined) {
    throw new Error(`no AuthService class in ${authServicePath}`);
  }

  const isClientClass = (declaration: typescriptModule.Declaration): boolean =>
    declaration === authServiceClass ||
    (ts.isClassDeclaration(declaration) &&
      declaration.name?.text === 'OAuthService' &&
      join(declaration.getSourceFile().fileName).includes(libraryDir));

  const isClientType = (type: typescriptModule.Type): boolean => {
    const nonNull = checker.getNonNullableType(type);
    const parts = nonNull.isUnionOrIntersection() ? nonNull.types : [nonNull];

    return parts.some(
      (part) => part.getSymbol()?.declarations?.some(isClientClass) ?? false,
    );
  };

  // The member's own declaration sits in either class — which is what finds
  // `narrowed.signIn()` when `narrowed` is a `Pick<AuthService, 'signIn'>`.
  const isClientMember = (name: typescriptModule.MemberName): boolean => {
    const symbol = checker.getSymbolAtLocation(name);
    if (symbol === undefined) {
      return false;
    }

    return checker
      .getRootSymbols(symbol)
      .some(
        (root) =>
          root.declarations?.some(
            (declaration) =>
              declaration.parent !== undefined &&
              isClientClass(declaration.parent as typescriptModule.Declaration),
          ) ?? false,
      );
  };

  const walked: string[] = [];
  const reaches: string[] = [];
  for (const file of program.getSourceFiles()) {
    if (join(file.fileName) === authServicePath || !include(file)) {
      continue;
    }
    const path = relativePath(file.fileName);
    walked.push(path);

    const visit = (node: typescriptModule.Node): void => {
      if (
        ts.isPropertyAccessExpression(node) &&
        (isClientType(checker.getTypeAtLocation(node.expression)) ||
          isClientMember(node.name))
      ) {
        reaches.push(`${path}: ${node.name.text}`);
      } else if (
        ts.isElementAccessExpression(node) &&
        isClientType(checker.getTypeAtLocation(node.expression))
      ) {
        reaches.push(`${path}: [computed]`);
      } else if (
        ts.isObjectBindingPattern(node) &&
        isClientType(checker.getTypeAtLocation(node))
      ) {
        reaches.push(`${path}: {destructured}`);
      } else if (
        ts.isBinaryExpression(node) &&
        node.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isObjectLiteralExpression(node.left) &&
        isClientType(checker.getTypeAtLocation(node.right))
      ) {
        reaches.push(`${path}: {destructured}`);
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
  }

  return { walked: walked.sort(), reaches: [...new Set(reaches)].sort() };
}

// Each allowed reach, with why that file may make it.
const allowedReaches = new Map<string, string>([
  [
    'src/app/+core/core.providers.ts: isProviderReturn',
    'the return leg: the initializer asks whether this page load is the ' +
      'provider redirecting back, which reads the address and contacts nothing',
  ],
  [
    'src/app/+core/core.providers.ts: initialize',
    'the return leg: prepares the client only when the answer above is yes',
  ],
  [
    'src/app/+core/session/session.service.ts: forgetProviderToken',
    'a local discard of the provider tokens when a session is published; ' +
      'contacts nothing',
  ],
  [
    'src/app/register/register.service.ts: providerEmail',
    'reads the address off the id token in storage; contacts nothing',
  ],
  [
    'src/app/register/steps/intro-step.component.ts: signIn',
    'the press on /register that starts the provider exchange',
  ],
  [
    'src/app/register/steps/passkey-step.component.ts: signIn',
    'the press on /register that restarts the exchange for a lapsed token',
  ],
  [
    'src/app/+core/interceptors/api-credentials.interceptor.ts: getIdToken',
    'reads the id token from storage for the two registration routes; ' +
      'contacts nothing',
  ],
]);

// A fake component, fed through the same walker, carrying one of each shape
// the census has to find. Not a file on disk: the host serves it from memory
// beside the real project, so it type-checks against the real classes.
const fixturePath = join(
  applicationDir,
  'identity-provider-callers.fixture.ts',
);
const fixtureSource = `
import { Component, inject } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { AuthService } from './+core/services/auth-service';

@Component({ selector: 'app-fixture', template: '' })
export class FixtureComponent {
  private readonly auth = inject(AuthService);

  constructor() {
    inject(AuthService).initialize();
    const { signIn } = inject(AuthService);
    const key = 'providerEmail';
    this.auth[key]();
    inject(OAuthService).initLoginFlow();
    const narrowed: Pick<AuthService, 'isAuthenticated'> = this.auth;
    narrowed.isAuthenticated();
    const held = this.auth.forgetProviderToken;
    const factory = (auth: AuthService): boolean => auth.isProviderReturn();
    void [signIn, held, factory];
  }
}
`;

function fixtureProgram(
  base: typescriptModule.Program,
  options: typescriptModule.CompilerOptions,
): typescriptModule.Program {
  const host = ts.createCompilerHost(options);
  const isFixture = (fileName: string): boolean =>
    join(fileName) === fixturePath;
  const { getSourceFile, fileExists, readFile } = host;

  host.getSourceFile = (fileName, languageVersion, ...rest) =>
    isFixture(fileName)
      ? ts.createSourceFile(fileName, fixtureSource, languageVersion, true)
      : getSourceFile.call(host, fileName, languageVersion, ...rest);
  host.fileExists = (fileName) =>
    isFixture(fileName) || fileExists.call(host, fileName);
  host.readFile = (fileName) =>
    isFixture(fileName) ? fixtureSource : readFile.call(host, fileName);

  return ts.createProgram({
    rootNames: [fixturePath],
    options,
    host,
    oldProgram: base,
  });
}

describe('the identity provider client', () => {
  const parsed = parsedAppConfig();
  const program = ts.createProgram({
    rootNames: [...new Set([...parsed.fileNames, ...applicationSources()])],
    options: parsed.options,
  });
  const census = providerReaches(program, isShippedSource);

  it('is looked for in enough files for the census to mean something', () => {
    // Assert — a moved `src/`, a filter that lets nothing through or a
    // program that loaded only its entry point would leave the pin below
    // looking at nothing.
    expect(census.walked.length).toBeGreaterThanOrEqual(100);
    expect(census.walked).toContain('src/main.ts');
    expect(census.walked).toContain('src/app/devtools.providers.prod.ts');
    expect(census.walked).toContain(
      'src/app/register/steps/intro-step.component.ts',
    );
    expect(census.reaches.length).toBeGreaterThanOrEqual(1);
  });

  it('is reached only from the sites registration needs', () => {
    // Arrange
    const allowed = [...allowedReaches.keys()].sort();

    // Act
    const reaches = census.reaches;

    // Assert
    expect(reaches).toEqual(allowed);
  });

  it('is found in every shape the census names', () => {
    // Arrange
    const withFixture = fixtureProgram(program, parsed.options);

    // Act
    const found = providerReaches(
      withFixture,
      (file) => join(file.fileName) === fixturePath,
    );

    // Assert
    const path = relativePath(fixturePath);
    expect(found.walked).toEqual([path]);
    expect(found.reaches).toEqual(
      [
        `${path}: initialize`,
        `${path}: {destructured}`,
        `${path}: [computed]`,
        `${path}: initLoginFlow`,
        `${path}: isAuthenticated`,
        `${path}: forgetProviderToken`,
        `${path}: isProviderReturn`,
      ].sort(),
    );
  });
});
