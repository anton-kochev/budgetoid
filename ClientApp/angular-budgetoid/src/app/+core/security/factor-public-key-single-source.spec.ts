// **FR-131 — a recovery factor's public key comes from the manifest, and never
// from a value the server supplies unsealed. This file holds the client half of
// that; the server half already ships and lives somewhere else.**
//
// **Where the other half is.** `me-api.service.spec.ts` carries
// `reads a body of exactly the members the wire contract names`, built from
// `docs/business-logic/vectors/account-keys-wire-v1.json`, whose
// `messages.accountKeyEntry.members` is exactly `factorId`, `wrappedPrivateKey`
// and `encapsulatedAccountKeys` — three members and no fourth, with a per-row
// public key refused by name in that file's own prose. The C# suite reads the
// same file (`AccountKeyWireContractTests`, through `WireContract.cs`), so a
// server that started publishing a per-factor point reddens both suites at once
// and this file is not where that is caught. **Nothing here re-implements it.**
// What is left over is the other direction: the *client* never builds a factor's
// public key from anywhere but the three places entitled to.
//
// **Why a census and not a behavioural test.** FR-131's only live consumer is a
// key rotation, which encapsulates the account's new keys to every factor's
// public point. **Rotation does not exist** — there is no client surface for it
// and no route reaches `BeginKeyRotationHandler` — so there is no behaviour to
// drive and nothing to assert a result about. What can be held today is the
// *absence*: no code path assembles a factor's public key anywhere but here.
//
// **Outside specs, a factor's public key is constructed in three files, and each
// of the three has a reason.**
//
//   * `factor-keypair.ts` — the origin of the bytes. `importFactorPrivateKey`
//     lifts the point out of the PKCS#8 it has *just imported*, at
//     `FACTOR_PUBLIC_KEY_OFFSET`, and hands it back on the same value as the
//     private half. That is FR-132, and it is what makes the point answer "which
//     point does *this* private key agree under" rather than "which point did
//     somebody say it was". Nothing stored and nothing transmitted is consulted.
//   * `factor-manifest.ts` — the reading side. `namedFactors` walks the *opened*
//     plaintext and copies 65 bytes per entry out of it. This is the half FR-131
//     names outright: the manifest is sealed under the account's content key, so
//     a point that came out of it came out of something this server cannot
//     write. **New in this story** — before it, the module only sealed a set a
//     caller already held.
//   * `register.service.ts` — the minting side. It pairs each of the eleven
//     factor ids it has just minted with the point `mintFactorKeypair` handed
//     back for that id, in the one scope that produced both. Nothing on that path
//     has been to the server yet, so there is nothing unsealed to take a point
//     from.
//
// **The rule is not "exactly one file".** Three is what is true, and the
// sentence worth defending is not a number but the absence of a fourth writer: a
// point read off an account-keys response, or off a passkey list, paired with the
// factor id sitting beside it, and handed to a rotation. That is two lines, it
// compiles, every envelope it produces opens, and it is a rotation sealed to keys
// the account's owner does not hold — discovered by whoever reaches for the
// factor it silently orphaned, which is by definition the moment they have lost
// the others.
//
// **The needle is the construction shape, and it is a conjunction. Neither half
// works alone.**
//
//   * `member` — the identifier `publicKey` written as an **object-literal
//     member**, in value position. Not the type. A type annotation catches
//     nothing worth catching: the file smuggling a server-supplied point writes
//     an object literal and never names `FactorPublicKey` at all, because the
//     type is structural and TypeScript will accept the literal at the call site
//     without a single import. So the regex takes `publicKey,` and `publicKey }`
//     (shorthand) and `publicKey: <expr>,`/`<expr> }`, and deliberately does
//     **not** take `readonly publicKey: Uint8Array;` — an interface declaring the
//     member builds nothing — nor `entry.publicKey`, which is a *read* of one
//     somebody else built. A planted decoy holds each of those two.
//   * `attribution` — the identifier `factorId` anywhere in the file. A loose 65
//     bytes is not a factor's public key; it becomes one when it is **attributed
//     to a factor**, and the attribution is a factor id. This is not a fudge to
//     quieten a neighbour: it is the difference between the two things this
//     codebase spells `publicKey`. `webauthn-ceremony.service.ts` writes four
//     `publicKey:` members and knows nothing of factor ids — they are the
//     `publicKey` member of `CredentialCreationOptions` and
//     `CredentialRequestOptions`, somebody else's word entirely, and a census
//     reporting them would be red on the day it was written and answered by
//     deleting the rule. It costs nothing on the side that matters, because the
//     smuggling FR-131 fears *necessarily* names `factorId`: the whole point of
//     the forgery is pairing a point with an identifier, and the identifier comes
//     from the response the point came from.
//
// And it is a conjunction rather than a single cleverer regex because the two
// halves are checkable at different scopes — a member is a fact about one
// expression, a file's business is a fact about the file — and squeezing them
// into one pattern would mean matching across lines, which is where a source-text
// scan starts guessing.
//
// **No runtime assertion can hold this.** A point forged from a response is a
// 65-byte `Uint8Array` leading `0x04`, indistinguishable from an honest one by
// every check this codebase owns: `requireUncompressedPoint` passes it,
// `crypto.subtle.importKey` accepts it, the manifest it goes into seals, stores
// and opens. What is checkable is provenance, and provenance is a fact about the
// source text. So this spec reads source files, for the reason
// `key-import-single-source.spec.ts`, `field-label-single-source.spec.ts` and
// `no-devtools.spec.ts` read theirs.
//
// It reads `src/` and needs no build. `src/` is what a reviewer reads and what
// the rule is about; the emitted bundle inlines, renames and flattens, and a
// minifier that shortened a member would answer this question wrongly whichever
// way it answered it.
//
// **Specs are excluded, deliberately, and that is not a convenience.** A spec
// constructing a fixture point is not a production path — `factor-manifest.spec.ts`
// builds synthetic factor sets by the dozen and must, because a manifest with no
// factors in it pins nothing, and `register.service.spec.ts` and
// `account-key-custody.service.spec.ts` each stand up a whole account's set to
// have anything to assert against. A rule that refused those would be refusing
// the only technique that can check the owners work at all. The exclusion is one
// `.spec.ts` suffix filter, it is the load-bearing line in this file, and the
// last case exists to prove it is doing its job rather than hiding a stranger
// behind it.
//
// Four limits, stated rather than papered over.
//
//   * It catches the member written out, not a value assembled at runtime by
//     property access — a `Object.assign(record, { [nameOf('publicKey')]: point })`,
//     or a spread of a wire entry into a record — and nothing short of parsing
//     the TypeScript would.
//   * It is a rule *between* files. Inside an owner the scan is blind by
//     construction, so a **fourth construction written inside
//     `factor-manifest.ts`** passes here. That one is caught by that module's own
//     specs, and only where it reaches an exported function.
//   * A point smuggled through a member **not** named `publicKey` — a record
//     shaped `{ factorId, point }`, converted to a `FactorPublicKey` one call
//     later — is invisible to this file. What holds that today is the absence of
//     a second spelling in the codebase, which is a fact and not a rule.
//   * It says nothing about *what* an owner does with the point. That is
//     `factor-keypair.spec.ts`' and `factor-manifest.spec.ts`' work, and they do
//     it by running the code rather than by reading it.
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

import { listFiles } from '../../../production-bundle';

const sourceDir = join(process.cwd(), 'src');

// The files that may build one, each with its standing named. A `Map` rather
// than a `Set` because the reason is the point: a rule whose owners are a bare
// list invites a red result to be answered by appending a line, and appending a
// line is a diff nobody reads. A reason has to be written, and a reason somebody
// has to write is a reason somebody has to mean.
//
// The fix for a red result is almost never an entry here. It is a call to
// `openFactorManifest`, which is what the one consumer outside these three —
// `account-key-custody.service.ts`, which compares the account's declared set
// against what the route served — already does, and which is why that file names
// `factorId` repeatedly and builds not one point. No count: the claim that
// carries this sentence is the second half, and a number written into the first
// goes stale the next time that file is edited with nothing going red.
const owners = new Map<string, string>([
  // FR-132, and the entry a reader is most likely to think belongs beside the
  // manifest's. It is the *origin* of the bytes rather than a reading of them:
  // the point is lifted from the PKCS#8 of a private key the platform has just
  // accepted, so the import is what makes `crypto.subtle` — and not this module
  // — the authority on the point it is about to read. The two values leave
  // together on one object precisely so no caller can pair a private half with
  // somebody else's point.
  [
    join('app', '+core', 'security', 'factor-keypair.ts'),
    'the point lifted out of the PKCS#8 the private half was just imported from — the point that private key really agrees under, and no second value to keep true (FR-132)',
  ],
  // FR-131's own sentence, and the entry that arrived in this story. Everything
  // this walk copies came out of a plaintext the account's content key opened, so
  // the server can neither write it nor move it without the seal failing first.
  // It is the only *reading* owner: the other two hold points that have never
  // been anywhere.
  [
    join('app', '+core', 'security', 'factor-manifest.ts'),
    'the entries walked out of an opened manifest plaintext — sealed under the account content key, so nothing unsealed can reach it (FR-131)',
  ],
  // The minting side. It is on this list rather than exempt from it because the
  // pairing is the fragile half: eleven ids and eleven points exist in this
  // method, the envelopes are bound to the id beside them, and a point zipped
  // against the wrong identifier is a manifest naming eleven real points with two
  // of them attached to the wrong factors — which every round trip, every
  // validation and the whole of registration accepts.
  [
    join('app', 'register', 'register.service.ts'),
    'the set assembled at registration from keypairs it has just minted, each id paired with its own point in the scope that produced both',
  ],
]);

// Half one: `publicKey` as an object-literal member, in value position.
//
// The optional `:[^;\n]*` is what separates a construction from a declaration.
// `readonly publicKey: Uint8Array;` ends in a semicolon and the class excludes
// one, so the annotation cannot reach the `[,}]`; a member in a literal ends in a
// comma or a closing brace and does. The lookbehind drops `entry.publicKey` and
// `factor.publicKey`, which are reads of a point somebody else built — the shape
// every legitimate consumer in this codebase is made of.
const member = /(?<![\w.$])publicKey\s*(?::[^;\n]*)?\s*[,}]/;

// Half two: the file is in the factor business at all. Written `factorId` and
// not `FactorId`, so `canonicalFactorId` and `mintFactorId` do not stand in for
// it — a file that only *mints* identifiers is not thereby entitled to attach
// points to them.
const attribution = /(?<![\w$])factorId(?![\w$])/;

// Every TypeScript file under `root` that builds one, specs excluded.
//
// The exclusion is by suffix and it is the load-bearing line in this file — it
// is what the planted spec in the last case exists to hold, because an exclusion
// that grew a directory, a prefix or a `__tests__` folder would swallow the
// impostor beside it and go on reporting nothing forever.
//
// It takes its root as an argument so the negative control can point it
// somewhere it is guaranteed to find something. A function closed over
// `sourceDir` can only ever be checked against the tree it is asserting about,
// which is the tree that must come back clean.
function modulesBuildingFactorPublicKeys(root: string): string[] {
  return listFiles(root)
    .filter((path) => path.endsWith('.ts'))
    .filter((path) => !path.endsWith('.spec.ts'))
    .filter((path) => {
      const source = readFileSync(path, 'utf8');

      return member.test(source) && attribution.test(source);
    })
    .map((path) => relative(root, path))
    .sort();
}

// The exemption, as one function rather than a `filter` written out in each of
// the two cases that need it — `key-import-single-source.spec.ts`' argument, and
// it applies here unchanged. The last case exists to prove *this* exemption
// reports a stranger, and a second copy of it would mean that case is
// controlling its own copy while the case that matters runs another.
function strangersAmong(builders: readonly string[]): string[] {
  return builders.filter((path) => !owners.has(path));
}

describe("a recovery factor's public key", () => {
  it('is built in every file that has standing to build one', () => {
    // Arrange, Act
    const builders = modulesBuildingFactorPublicKeys(sourceDir);

    // Assert
    // The positive half, and a pure absence check cannot say it. Without this, a
    // `sourceDir` that moved, a `.ts` filter letting nothing through, either
    // regex with a typo in it, or a spec exclusion that swallowed the whole tree
    // reports the case below perfectly clean.
    //
    // Each owner is named with its standing in the message, so a red bar here
    // says which file stopped building one and why anybody thought it should —
    // which is the question, because two of the three standings are a *source*
    // for the bytes and losing one means the source moved somewhere nothing
    // argued for.
    for (const [owner, standing] of owners) {
      expect(
        builders,
        `${owner} no longer builds a factor public key — ${standing}`,
      ).toContain(owner);
    }
  });

  it('is built in no other module', () => {
    // Arrange, Act
    const strangers = strangersAmong(
      modulesBuildingFactorPublicKeys(sourceDir),
    );

    // Assert
    // Named, not counted. A boolean or a length tells whoever broke this that
    // something is wrong and not which file to open, and this is a rule about
    // *where* a point was assembled — the location is the whole of the finding,
    // because the question a reviewer then asks is "what did that file have in
    // its hands", and only the path answers it.
    expect(
      strangers,
      `A factor public key is built where nothing gives it standing, in: ${strangers.join(', ')}`,
    ).toEqual([]);
  });

  it('would be reported by path if a stranger beside an owner built one', () => {
    // Arrange
    // A planted tree, because both cases above are green over an empty
    // repository and this one must not be. The first case proves the needle and
    // `sourceDir` reach real files; what it cannot prove is the half that
    // matters in the other direction — that a file which is *not* an owner
    // survives the exemption instead of being filtered away beside it.
    //
    // Built outside `src/`, so the runner is never asked to watch a file appear
    // inside the tree it is compiling.
    const root = mkdtempSync(join(tmpdir(), 'factor-public-key-scan-'));

    // **Beside an owner, in the owner's own directory.** That placement is the
    // case. An exemption keyed on the *directory* — `+core/security` holds two of
    // the three owners, and "the security folder may build points" is exactly the
    // shape a later widening takes — reports a stranger planted a folder away
    // just as happily as the right rule does, so the whole file passes on the
    // broken shape. Beside an owner it does not. It is also where such a file
    // would really be written: the module that would smuggle a server-supplied
    // point is the one already holding the response, and that is
    // `account-key-custody.service.ts`, a neighbour of the manifest.
    const stranger = join(dirname(firstOwner()), 'copy.ts');

    // A **spec** building one, and it must come back unreported. Every spec that
    // exercises a manifest has to stand up a factor set to have anything to seal,
    // and a scan that reported them would be red on the day it was written.
    const strangerSpec = join(dirname(firstOwner()), 'copy.spec.ts');

    try {
      // A copy of each owner at its own relative path, so the exemption has
      // something real to exempt, and two directories deep, so the recursion is
      // exercised rather than assumed.
      for (const owner of owners.keys()) {
        mkdirSync(join(root, dirname(owner)), { recursive: true });
        writeFileSync(
          join(root, owner),
          'entries.push({ factorId, publicKey });\n',
        );
      }

      writeFileSync(
        join(root, stranger),
        // The forgery, written the way it would really be written: no import of
        // `FactorPublicKey`, no type annotation anywhere, a point decoded
        // straight off an account-keys response and paired with the identifier
        // that arrived beside it.
        'const declared = response.factors.map((entry) => ({\n' +
          '  factorId: entry.factorId,\n' +
          '  publicKey: decodeBase64Url(entry.publicKey),\n' +
          '}));\n',
      );
      writeFileSync(
        join(root, strangerSpec),
        'const factors = [{ factorId: FACTOR_ID, publicKey: point }];\n',
      );

      // Four decoys, and each holds a different line of the needle.
      //
      // **The WebAuthn options member.** A `publicKey` member with no factor id
      // in the file — `webauthn-ceremony.service.ts`' real shape, four times
      // over. A scan that dropped `attribution` reports it, and reports it
      // forever, and the rule dies of it.
      writeFileSync(
        join(root, dirname(firstOwner()), 'ceremony.ts'),
        'await navigator.credentials.create({ publicKey: creation });\n',
      );

      // **The type annotation.** A file that declares the member and builds
      // nothing. This is the decoy that decides whether the needle is worth
      // anything: a census greping for the *type* matches here and misses the
      // stranger above, which names no type at all.
      writeFileSync(
        join(root, dirname(firstOwner()), 'declared.ts'),
        'export interface Entry {\n' +
          '  readonly factorId: string;\n' +
          '  readonly publicKey: Uint8Array;\n' +
          '}\n',
      );

      // **The consumer.** A file reading points off a set somebody else built —
      // `account-key-custody.service.ts`' real shape. Reading one is not
      // building one, and a needle that could not tell them apart would name
      // every legitimate consumer this rule exists to permit.
      writeFileSync(
        join(root, dirname(firstOwner()), 'consumer.ts'),
        'const ids = declared.map((factor) => factor.factorId);\n' +
          'requireUncompressedPoint(factor.publicKey);\n',
      );

      // **Markup carrying both halves.** A scan that read every file, or that
      // read none of them, disagrees with the first expectation below rather
      // than passing it by luck.
      writeFileSync(
        join(root, dirname(firstOwner()), 'markup.html'),
        '<p>{ factorId, publicKey }</p>\n',
      );

      // Act
      const builders = modulesBuildingFactorPublicKeys(root);
      const strangers = strangersAmong(builders);

      // Assert
      // Exactly the owners and the one non-spec stranger: the planted spec is
      // not here, and none of the four decoys is.
      expect(builders).toEqual([...owners.keys(), stranger].sort());

      // And the offender comes back alone and by path, through the same
      // `strangersAmong` the case above runs. This is the assertion the case
      // exists for: it is the only one in the file that can tell an exemption
      // which keeps a stranger out of the report from one that does not.
      expect(strangers).toEqual([stranger]);
    } finally {
      // In a `finally`, so a failed expectation above leaves nothing behind in
      // the temporary directory.
      rmSync(root, { recursive: true, force: true });
    }
  });
});

// The directory the plants go in, taken off the owner list rather than typed —
// so an owner that moves carries this control with it instead of leaving it
// planting strangers beside a path nothing occupies. It is a function rather
// than a `const` because `owners` is a `Map` and reading its first key is the
// kind of line that reads as an accident when it is inline.
function firstOwner(): string {
  // `?? ''` rather than a `!`, for the reason `key-import-single-source.spec.ts`
  // gives about indexed reads: the project does not compile with
  // `noUncheckedIndexedAccess` today, and the day it does the shortest way back
  // to green is a non-null assertion over a collection this file itself built.
  // An empty string here would make the plants land at the root of the temporary
  // tree, where the last case's own expectations refuse them.
  const [first] = [...owners.keys()];

  return first ?? '';
}
