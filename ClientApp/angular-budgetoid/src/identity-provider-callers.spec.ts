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
// An instance of either class handed to a position the checker types as
// something else — an argument, an annotated variable, field or element, a
// declared return type, an `as` cast including `as any` and `as unknown` — is
// recorded as `path: {escaped}`, because every member reached past that point
// is out of the checker's sight. So is a provider that makes another token
// resolve to the client through `useExisting`, `useClass` or `useFactory`.
// The class itself is not the client: `inject(AuthService)`, `deps` and
// `provide` take it and record nothing.
//
// The census is only as good as the checker's view, so every file it walks,
// and every fixture below, has to compile clean: a name the checker cannot
// resolve has no type, and a reach through it is recorded as nothing.
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
// - A subclass of either class, or an object spread of either. The checker
//   sees neither as the client.
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

  const partsOf = (type: typescriptModule.Type): typescriptModule.Type[] => {
    const nonNull = checker.getNonNullableType(type);

    return nonNull.isUnionOrIntersection() ? nonNull.types : [nonNull];
  };

  const isClientType = (type: typescriptModule.Type): boolean =>
    partsOf(type).some(
      (part) => part.getSymbol()?.declarations?.some(isClientClass) ?? false,
    );

  // The client class `type` names, as an instance or as the class itself.
  // `typeof AuthService` is what `inject()`, `deps` and `provide` take, and
  // handing the class around hands nobody the client.
  const clientClassOf = (
    type: typescriptModule.Type,
    as: 'instance' | 'class',
  ): typescriptModule.Symbol | undefined =>
    partsOf(type)
      .map((part) => ({ part, symbol: part.getSymbol() }))
      .find(
        ({ part, symbol }) =>
          symbol !== undefined &&
          (symbol.declarations?.some(isClientClass) ?? false) &&
          (checker.getDeclaredTypeOfSymbol(symbol) === part) ===
            (as === 'instance'),
      )?.symbol;

  const isClientInstance = (type: typescriptModule.Type): boolean =>
    clientClassOf(type, 'instance') !== undefined;

  // An instance of the client, in a position the checker types as something
  // else — a parameter, an annotated variable, field or element, a declared
  // return type, a cast. Past that point the value is no longer the client to
  // the checker, so every member reached through it would go unrecorded; the
  // hand-off is recorded instead. A position with no type of its own, such as
  // an unannotated `const`, keeps the client's type and is not a hand-off.
  const escapes = (node: typescriptModule.Node): boolean => {
    if (
      !ts.isExpression(node) ||
      !isClientInstance(checker.getTypeAtLocation(node))
    ) {
      return false;
    }
    const parent = node.parent;
    // `satisfies` checks the value and hands it on unchanged; where it goes
    // is the outer expression's position, which is walked in its own turn.
    if (ts.isSatisfiesExpression(parent) && parent.expression === node) {
      return false;
    }
    const contextual = checker.getContextualType(node);

    return contextual !== undefined && !isClientInstance(contextual);
  };

  // A provider literal that makes another token resolve to the client:
  // `useExisting` or `useClass` naming either class, or a `useFactory`
  // returning an instance of one. Providing the class as itself is not one.
  const providesTheClient = (
    literal: typescriptModule.ObjectLiteralExpression,
  ): boolean => {
    const member = (name: string): typescriptModule.Expression | undefined =>
      literal.properties.find(
        (property): property is typescriptModule.PropertyAssignment =>
          ts.isPropertyAssignment(property) &&
          (ts.isIdentifier(property.name) ||
            ts.isStringLiteral(property.name)) &&
          property.name.text === name,
      )?.initializer;
    const provide = member('provide');
    if (provide === undefined) {
      return false;
    }
    const byClass = member('useExisting') ?? member('useClass');
    const factory = member('useFactory');
    const produced =
      byClass !== undefined
        ? clientClassOf(checker.getTypeAtLocation(byClass), 'class')
        : factory !== undefined
          ? checker
              .getTypeAtLocation(factory)
              .getCallSignatures()
              .map((signature) =>
                clientClassOf(signature.getReturnType(), 'instance'),
              )
              .find((symbol) => symbol !== undefined)
          : undefined;

    return (
      produced !== undefined &&
      checker.getTypeAtLocation(provide).getSymbol() !== produced
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
      if (
        escapes(node) ||
        (ts.isObjectLiteralExpression(node) && providesTheClient(node))
      ) {
        reaches.push(`${path}: {escaped}`);
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
      'provider redirecting back, which reads the address and the exchange ' +
      "marker in this tab's sessionStorage, and contacts nothing",
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

// A fake source file, fed through the same walker. Not a file on disk: the
// host serves it from memory beside the real project, so it type-checks
// against the real classes.
const fixturePath = join(
  applicationDir,
  'identity-provider-callers.fixture.ts',
);
const fixtureFile = relativePath(fixturePath);

function isFixture(file: typescriptModule.SourceFile): boolean {
  return join(file.fileName) === fixturePath;
}

// The imports every fixture below starts from, and the one type they hand the
// client to. `Starter` names `signIn` and nothing else: a value typed as it is
// the client with its type forgotten.
const fixtureImports = `
import {
  APP_INITIALIZER,
  Component,
  InjectionToken,
  inject,
  makeEnvironmentProviders,
  Provider,
} from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { AuthService } from './+core/services/auth-service';

interface Starter {
  signIn(): void;
}

const STARTER = new InjectionToken<Starter>('starter');
void [APP_INITIALIZER, Component, STARTER, makeEnvironmentProviders];
`;

// One of each shape the census has to find.
const everyShapeSource = `${fixtureImports}
@Component({ selector: 'app-fixture', template: '' })
export class FixtureComponent {
  private readonly auth = inject(AuthService);

  constructor() {
    inject(AuthService).initialize();
    const { signIn } = inject(AuthService);
    const key = 'providerEmail';
    this.auth[key]();
    inject(OAuthService).initLoginFlow();
    const narrowed: Pick<AuthService, 'providerEmail'> = this.auth;
    narrowed.providerEmail();
    const held = this.auth.forgetProviderToken;
    const factory = (auth: AuthService): boolean => auth.isProviderReturn();
    void [signIn, held, factory];
  }
}
`;

// Each hands the client to a position typed as something else, where every
// later member access is out of the checker's sight. The one reach recorded
// is the hand-off.
const escapes: readonly (readonly [shape: string, body: string])[] = [
  [
    'an argument to a parameter typed as something else',
    `
function start(starter: { signIn(): void }): void {
  starter.signIn();
}
export function run(): void {
  start(inject(AuthService));
}`,
  ],
  [
    'an annotated const',
    `
export function run(): void {
  const starter: Starter = inject(AuthService);
  void starter;
}`,
  ],
  [
    'an annotated field',
    `
export class Holder {
  private readonly starter: Starter = inject(AuthService);
  public held(): Starter {
    return this.starter;
  }
}`,
  ],
  [
    'an arrow body under a declared return type',
    `
export const starter = (): Starter => inject(AuthService);`,
  ],
  [
    'a return under a declared return type',
    `
export function starter(): Starter {
  return inject(AuthService);
}`,
  ],
  [
    'an as-cast to another type',
    `
export const starter = () => inject(AuthService) as Starter;`,
  ],
  [
    'an as-cast to unknown',
    `
export const starter = () => inject(AuthService) as unknown;`,
  ],
  [
    'an as-cast to any',
    `
export const starter = () => inject(AuthService) as any;`,
  ],
  [
    'an assignment to a variable typed as something else',
    `
export function run(): void {
  let starter: Starter;
  starter = inject(AuthService);
  void starter;
}`,
  ],
  [
    'an element of an annotated array',
    `
export function run(): void {
  const starters: Starter[] = [inject(AuthService)];
  void starters;
}`,
  ],
  [
    'a member of an annotated object literal',
    `
export function run(): void {
  const holder: { starter: Starter } = { starter: inject(AuthService) };
  void holder;
}`,
  ],
  [
    'an argument to an unknown parameter',
    `
function keep(value: unknown): void {
  void value;
}
export function run(): void {
  keep(inject(AuthService));
}`,
  ],
  [
    'a provider aliasing another token to the client',
    `
export const providers: Provider[] = [
  { provide: STARTER, useExisting: AuthService },
];`,
  ],
  [
    'a provider whose factory returns the client',
    `
export const providers: Provider[] = [
  { provide: STARTER, useFactory: () => inject(AuthService) },
];`,
  ],
];

// Each keeps the client typed as the client, so the member it reaches is
// recorded by name and nothing escapes.
const kept: readonly (readonly [
  shape: string,
  body: string,
  member: string,
])[] = [
  [
    'an unannotated field called through this',
    `
export class Holder {
  private readonly auth = inject(AuthService);
  public start(): void {
    this.auth.signIn();
  }
}`,
    'signIn',
  ],
  [
    'the core.providers factory, taking the class as a dependency',
    `
export const providers = makeEnvironmentProviders([
  {
    provide: APP_INITIALIZER,
    useFactory: (auth: AuthService) => () => auth.isProviderReturn(),
    deps: [AuthService],
    multi: true,
  },
]);`,
    'isProviderReturn',
  ],
  [
    'the library class, called directly',
    `
export const token = () => inject(OAuthService).getIdToken();`,
    'getIdToken',
  ],
  [
    'an argument to a parameter typed as the client',
    `
function start(auth: AuthService): void {
  auth.signIn();
}
export function run(): void {
  start(inject(AuthService));
}`,
    'signIn',
  ],
  [
    'a satisfies check, which hands the value on unchanged',
    `
export function run(): void {
  (inject(AuthService) satisfies Starter).signIn();
}`,
    'signIn',
  ],
];

function fixtureProgram(
  base: typescriptModule.Program,
  options: typescriptModule.CompilerOptions,
  source: string,
): typescriptModule.Program {
  const host = ts.createCompilerHost(options);
  const isFixturePath = (fileName: string): boolean =>
    join(fileName) === fixturePath;
  const { getSourceFile, fileExists, readFile } = host;

  host.getSourceFile = (fileName, languageVersion, ...rest) =>
    isFixturePath(fileName)
      ? ts.createSourceFile(fileName, source, languageVersion, true)
      : getSourceFile.call(host, fileName, languageVersion, ...rest);
  host.fileExists = (fileName) =>
    isFixturePath(fileName) || fileExists.call(host, fileName);
  host.readFile = (fileName) =>
    isFixturePath(fileName) ? source : readFile.call(host, fileName);

  return ts.createProgram({
    rootNames: [fixturePath],
    options,
    host,
    oldProgram: base,
  });
}

// Every error-category diagnostic in the files `program` holds that `include`
// admits, as `path: message`.
function compileErrors(
  program: typescriptModule.Program,
  include: (file: typescriptModule.SourceFile) => boolean,
): string[] {
  return program
    .getSourceFiles()
    .filter(include)
    .flatMap((file) => [
      ...program.getSyntacticDiagnostics(file),
      ...program.getSemanticDiagnostics(file),
    ])
    .filter((diagnostic) => diagnostic.category === ts.DiagnosticCategory.Error)
    .map(
      (diagnostic) =>
        `${relativePath(diagnostic.file?.fileName ?? '')}: ` +
        ts.flattenDiagnosticMessageText(diagnostic.messageText, ' '),
    );
}

describe('the identity provider client', () => {
  const parsed = parsedAppConfig();
  const program = ts.createProgram({
    rootNames: [...new Set([...parsed.fileNames, ...applicationSources()])],
    options: parsed.options,
  });
  const census = providerReaches(program, isShippedSource);
  const reachesIn = (source: string): Walk =>
    providerReaches(fixtureProgram(program, parsed.options, source), isFixture);

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

  // A full type check of every shipped file is seconds of work — past the
  // default five under a whole-suite run's load — so this case waits longer.
  it('is looked for in files that compile, shipped and fixture alike', () => {
    // Arrange — a name the checker cannot resolve has no type, so a reach
    // through it is recorded as nothing at all: a census over a broken file,
    // or a fixture naming a member that no longer exists, passes by seeing
    // less.
    const fixtures = [
      everyShapeSource,
      ...escapes.map(([, body]) => fixtureImports + body),
      ...kept.map(([, body]) => fixtureImports + body),
    ];

    // Act
    const errors = [
      ...compileErrors(program, isShippedSource),
      ...fixtures.flatMap((source) =>
        compileErrors(
          fixtureProgram(program, parsed.options, source),
          isFixture,
        ),
      ),
    ];

    // Assert
    expect(errors).toEqual([]);
  }, 60_000);

  it('is reached only from the sites registration needs', () => {
    // Arrange
    const allowed = [...allowedReaches.keys()].sort();

    // Act
    const reaches = census.reaches;

    // Assert
    expect(reaches).toEqual(allowed);
  });

  it('is found in every shape the census names', () => {
    // Act
    const found = reachesIn(everyShapeSource);

    // Assert
    expect(found.walked).toEqual([fixtureFile]);
    expect(found.reaches).toEqual(
      [
        `${fixtureFile}: initialize`,
        `${fixtureFile}: {destructured}`,
        `${fixtureFile}: [computed]`,
        `${fixtureFile}: initLoginFlow`,
        `${fixtureFile}: {escaped}`,
        `${fixtureFile}: providerEmail`,
        `${fixtureFile}: forgetProviderToken`,
        `${fixtureFile}: isProviderReturn`,
      ].sort(),
    );
  });

  it.each(escapes)('is found escaping as %s', (shape, body) => {
    // Act
    const found = reachesIn(fixtureImports + body);

    // Assert
    expect(found.reaches).toEqual([`${fixtureFile}: {escaped}`]);
  });

  it.each(kept)('is not escaping as %s', (shape, body, member) => {
    // Act
    const found = reachesIn(fixtureImports + body);

    // Assert
    expect(found.reaches).toEqual([`${fixtureFile}: ${member}`]);
  });
});
