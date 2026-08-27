// **Outside specs, `crypto.subtle.importKey` is written in two files, and each
// of the two has a reason.**
//
//   * `account-keys.ts` — the two doors. `importAesGcmKey` and
//     `importHmacSha256Key` each hold five decisions in one place: the
//     algorithm, the width, the usage list, the non-extractability, and the
//     death of the raw bytes.
//   * `hkdf.ts` — **not** a door. It imports input keying material for a
//     derivation and hands back a key whose only usage is `deriveBits`, so
//     nothing can seal, sign or export under it and nothing can mistake it for a
//     key the account uses.
//
// **The rule is not "exactly one file", and it never was.** Two files is what is
// true, and the sentence worth defending is not a number but the absence of a
// third writer: a hand-written `crypto.subtle.importKey` beside the caller that
// needed it. That is four lines, it compiles, and it hands back a perfectly good
// `CryptoKey` — while holding **none** of the five. Of the five, only a wrong
// algorithm is ever mentioned by anything, and it is mentioned at the first call
// rather than at the import, by which time the material has been wiped or not
// according to nobody's rule. A width silently downgraded, a usage list widened
// to `wrapKey`, an extractable key, and a copy of the bytes left on the heap all
// work, forever, and are wrong for the life of the account.
//
// **Specs are exempt, and the exemption is not a convenience.** Both doors are
// observed at the platform boundary — `account-keys.spec.ts` spies on
// `crypto.subtle.importKey` and calls through, because a non-extractable key has
// no other witness and a wipe cannot be seen from outside the module at all — so
// every spec that pins a door has to name that function. Six more specs import a
// reference key to hold a derivation against a second opinion. A rule that
// refused those would be refusing the only technique that can check the doors
// exist. `field-label-single-source.spec.ts` reaches the opposite conclusion
// about its own value, and the difference is the whole argument: nothing there
// has any reason to spell a literal that can simply be imported, and here there
// is no importing a platform function under another name.
//
// **No runtime assertion can hold this.** A hand-written import returns a key
// object indistinguishable from a door's on the day it is written — same class,
// same `type`, and the four decisions it dropped are dropped silently. What is
// checkable is provenance, and provenance is a fact about the source text. So
// this spec reads source files, for the reason `field-label-single-source.spec.ts`
// and `no-devtools.spec.ts` read theirs: the claim is about the shape of what was
// written, and nothing that runs can observe it.
//
// It reads `src/` and needs no build. `src/` is what a reviewer reads and what
// the rule is about; the emitted bundle inlines and renames, and a minifier that
// aliased the member would answer this question wrongly whichever way it
// answered it.
//
// Three limits, stated rather than papered over.
//
//   * It catches the member access, not a call assembled at runtime out of
//     pieces — `crypto.subtle['import' + 'Key']`, or a destructured
//     `const { importKey } = crypto.subtle` — and nothing short of parsing the
//     TypeScript would.
//   * It is a rule *between* files. Inside an owner the scan is blind by
//     construction, so a **third door written inside `account-keys.ts`** passes
//     here. That one is caught elsewhere and only if it is exported: the export
//     census in `account-keys.spec.ts` names every function the module offers,
//     and a new one reddens it. An unexported third import inside an owner is
//     caught by nothing, which is why the call-site count is documented in that
//     file's prose and deliberately **not** pinned here — pinning it would redden
//     on the legitimate change (a fourth door, argued for) and stay green on the
//     one that matters (a hand-written import in a seventh file).
//   * It says nothing about *what* an owner's imports do. That is the door specs'
//     work, and they do it by running the doors rather than by reading them.
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

// The two files that may write it, each with its standing named. A `Map` rather
// than a `Set` because the reason is the point: a rule whose owners are a bare
// list invites a red result to be answered by appending a line, and appending a
// line is a diff nobody reads. A reason has to be written, and a reason somebody
// has to write is a reason somebody has to mean.
//
// The fix for a red result is almost never an entry here. It is an import from
// `account-keys.ts`, which is what the one caller outside that module —
// `account-key-custody.service.ts`, which needs both doors in one statement —
// already does.
const owners = new Map<string, string>([
  [
    join('app', '+core', 'security', 'account-keys.ts'),
    'the two doors: algorithm, width, usage list, non-extractability, and the death of the bytes',
  ],
  [
    join('app', '+core', 'security', 'hkdf.ts'),
    'HKDF input keying material, whose only usage is deriveBits — nothing can seal, sign or export under it',
  ],
]);

// The member access itself. There is no constant to derive it from — it is a
// platform API, not a value this codebase declares — so it is written out, which
// is the one thing `field-label-single-source.spec.ts` refuses to do about its
// own needle. The difference is that a literal can be imported and a platform
// member cannot, and a needle that named itself would match nothing at all.
const needle = 'crypto.subtle.importKey';

// Every TypeScript file under `root` that writes it, specs excluded.
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
function modulesImportingKeys(root: string): string[] {
  return listFiles(root)
    .filter((path) => path.endsWith('.ts'))
    .filter((path) => !path.endsWith('.spec.ts'))
    .filter((path) => readFileSync(path, 'utf8').includes(needle))
    .map((path) => relative(root, path))
    .sort();
}

// The exemption, as one function rather than a `filter` written out in each of
// the two cases that need it — `field-label-single-source.spec.ts`'s argument,
// and it applies here unchanged. The last case exists to prove *this* exemption
// reports a stranger, and a second copy of it would mean that case is
// controlling its own copy while the case that matters runs another.
function strangersAmong(carriers: readonly string[]): string[] {
  return carriers.filter((path) => !owners.has(path));
}

describe('crypto.subtle.importKey', () => {
  it('is written in both of the files that have standing to write it', () => {
    // Arrange, Act
    const carriers = modulesImportingKeys(sourceDir);

    // Assert
    // The positive half, and a pure absence check cannot say it. Without this, a
    // `sourceDir` that moved, a `.ts` filter letting nothing through, a needle
    // with a typo in it, or a spec exclusion that swallowed the whole tree
    // reports the case below perfectly clean.
    //
    // Each owner is named with its reason in the message, so a red bar here says
    // which file stopped importing and why anybody thought it should.
    for (const [owner, standing] of owners) {
      expect(
        carriers,
        `${owner} no longer writes ${needle} — ${standing}`,
      ).toContain(owner);
    }
  });

  it('is written in no other module', () => {
    // Arrange, Act
    const strangers = strangersAmong(modulesImportingKeys(sourceDir));

    // Assert
    // Named, not counted. A boolean or a length tells whoever broke this that
    // something is wrong and not which file to open, and this is a rule about
    // *where* a call was written — the location is the whole of the finding.
    expect(
      strangers,
      `${needle} is written outside the two doors, in: ${strangers.join(', ')}`,
    ).toEqual([]);
  });

  it('would be reported by path if a stranger beside a door carried it', () => {
    // Arrange
    // A planted tree, because both cases above are green over an empty
    // repository and this one must not be. The first case proves the needle and
    // `sourceDir` reach real files; what it cannot prove is the half that
    // matters in the other direction — that a file which is *not* an owner
    // survives the exemption instead of being filtered away beside it.
    //
    // Built outside `src/`, so the runner is never asked to watch a file appear
    // inside the tree it is compiling.
    const root = mkdtempSync(join(tmpdir(), 'key-import-scan-'));

    // **Beside an owner, in the owner's own directory.** That placement is the
    // case. An exemption keyed on the *directory* — `+core/security` is where
    // both owners live, and "the security folder may import keys" is exactly the
    // shape a later widening takes — reports a stranger planted a folder away
    // just as happily as the right rule does, so the whole file passes on the
    // broken shape. Beside an owner it does not. It is also where a copy would
    // really be written: the next module that needs a key object is a neighbour
    // of the one holding the doors, and it was one — `account-key-custody.service.ts`
    // sits in this directory and reaches for both doors by importing them.
    const stranger = join(dirname(firstOwner()), 'copy.ts');

    // A **spec** carrying the needle, and it must come back unreported. This is
    // the opposite polarity to `field-label-single-source.spec.ts`'s planted
    // spec, and deliberately: there, no spec has any reason to spell the value,
    // so a spec carrying it is an offender. Here every spec that pins a door has
    // to name this function, so a scan that reported them would be red on the
    // day it was written and would be answered by deleting the rule.
    const strangerSpec = join(dirname(firstOwner()), 'copy.spec.ts');

    try {
      // A copy of each owner at its own relative path, so the exemption has
      // something real to exempt, three directories down, so the recursion is
      // exercised rather than assumed.
      for (const owner of owners.keys()) {
        mkdirSync(join(root, dirname(owner)), { recursive: true });
        writeFileSync(
          join(root, owner),
          `await ${needle}('raw', bytes, 'AES-GCM', false, []);\n`,
        );
      }

      writeFileSync(
        join(root, stranger),
        `const key = await ${needle}('raw', bytes, 'AES-GCM', true, ['encrypt']);\n`,
      );
      writeFileSync(
        join(root, strangerSpec),
        `const spy = vi.spyOn(crypto.subtle, 'importKey');\nconst reference = await ${needle}('raw', bytes, 'AES-GCM', false, []);\n`,
      );

      // Two decoys. A module carrying nothing, and markup carrying the needle: a
      // scan that read every file, or that read none of them, disagrees with the
      // first expectation below rather than passing it by luck.
      writeFileSync(
        join(root, dirname(firstOwner()), 'quiet.ts'),
        'export const n = 1;\n',
      );
      writeFileSync(
        join(root, dirname(firstOwner()), 'markup.html'),
        `<p>${needle}</p>\n`,
      );

      // Act
      const carriers = modulesImportingKeys(root);
      const strangers = strangersAmong(carriers);

      // Assert
      // Exactly the owners and the one non-spec stranger: the planted spec is
      // not here, and neither decoy is.
      expect(carriers).toEqual([...owners.keys(), stranger].sort());

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
// so a door that moves carries this control with it instead of leaving it
// planting strangers beside a path nothing occupies. It is a function rather
// than a `const` because `owners` is a `Map` and reading its first key is the
// kind of line that reads as an accident when it is inline.
function firstOwner(): string {
  const [first] = [...owners.keys()];

  // `?? ''` rather than a `!`, for the reason the neighbouring rule's spec gives
  // about indexed reads: the project does not compile with
  // `noUncheckedIndexedAccess` today, and the day it does the shortest way back
  // to green is a non-null assertion over a collection this file itself built.
  // An empty string here would make the plants land at the root of the temporary
  // tree, where the last case's own expectations refuse them.
  return first ?? '';
}
