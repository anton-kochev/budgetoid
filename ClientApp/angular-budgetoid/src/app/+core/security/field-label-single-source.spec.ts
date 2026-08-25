// **`NARRATIVE_FIELD_AAD_PREFIX` is declared in `narrative-cipher.ts` and typed
// nowhere else.**
//
// It is named below and never spelled out — not in a needle, not in an
// assertion, not in this comment. Every string this file searches for is built
// from the imported constant, which is what keeps the file hunting second
// copies from being one, and is why it appears in no exemption below: a spec
// that had to exempt itself from its own rule would be carrying the one
// exemption nobody would ever question. `passkey-label-single-source.spec.ts`
// says the same thing about its own labels, and for the same reason.
//
// The prefix opens the associated data of every narrative field. Associated
// data is not carried inside the envelope — it is rebuilt from wherever the
// ciphertext was found — so a second, hand-typed copy that later drifts from
// the first makes every value sealed under the old one unopenable, permanently,
// with the same indistinguishable failure a corrupted key gives and no error
// anywhere naming the cause.
//
// **Nothing outside this repository would notice.** The frozen vectors in
// `docs/business-logic/vectors/narrative-field-v1.json` carry the prefix's
// bytes inside an 80-byte associated-data value, so what the literal *is* is
// held there and not here; the requirements fix the binding property and not
// the grammar, so these letters are ours alone and this file is the only thing
// standing over where they may be written.
//
// **No runtime assertion can hold that.** A caller that types the letters out
// agrees with the constant byte for byte on the day it is written, so every
// frozen vector, every round trip, and every comparison against the imported
// constant in `narrative-cipher.spec.ts` — the strongest comparison there is —
// passes while being unable to see where the value came from. What is checkable
// is provenance, and provenance is a fact about the source text. So this spec
// reads source files, for the reason `no-external-origins.spec.ts` and
// `no-devtools.spec.ts` read the build output: the claim is about the shape of
// what was written, and nothing that runs can observe it.
//
// It reads `src/` and needs no build. `src/` is what a reviewer reads and what
// the rule is about; the bundler inlines a `const` string into each of its use
// sites, so the emitted JavaScript cannot tell a copy from an import and would
// answer this question wrongly whichever way it answered it.
//
// Two limits, stated rather than papered over. This catches the literal, not a
// value assembled at runtime out of pieces — `'budgetoid/' + …` split across
// two expressions — and nothing short of parsing the TypeScript would. And it
// is a rule *between* files: inside the owner the scan is blind by
// construction, because the owner is the one file it exempts. Nothing in
// `narrative-cipher.ts` spells the prefix today except the declaration itself —
// the JSDoc that draws the grammar points at the constant with `{@link}` rather
// than quoting it — and the first case below is written the way it is to keep
// that true.
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
import { NARRATIVE_FIELD_AAD_PREFIX } from './narrative-cipher';

const sourceDir = join(process.cwd(), 'src');

// The one module that may carry the literal, as a single path rather than a set
// of them — and the choice is the argument.
//
// A collection is the right shape for the passkey rule next door, where two
// files have standing: the module that declares the labels, and the spec that
// pins their exact text and is the only thing that would notice one being
// edited. Here nothing else has any. `narrative-cipher.spec.ts` imports this
// constant and never quotes it, and the value itself is already pinned by a
// vector computed outside this codebase, so there is no second file with a
// reason to spell it.
//
// A `Set` or a `Map` would invite the fix this rule exists to refuse — a red
// result answered by appending a line, which is a diff nobody reads. A lone
// constant makes a second owner an edit that changes the shape of this file and
// cannot pass as housekeeping. The fix for a red result is never an entry here:
// it is an import from `narrative-cipher.ts`, which is what every caller in the
// system already does.
const owner = join('app', '+core', 'security', 'narrative-cipher.ts');

// The grammar's namespace, taken off the constant rather than typed out.
//
// Derived twice over. The needle cannot go on matching letters the module has
// moved off, so a rename carries this file with it instead of leaving it green
// while watching a string nothing uses. And it is the namespace rather than the
// whole value on purpose: it catches a copy typed at *any* version, including
// one written on the day the constant is bumped to `v2` — which is precisely
// the day two copies exist and only one of them moves.
const grammarPrefix = `${NARRATIVE_FIELD_AAD_PREFIX.split('/')
  .slice(0, 2)
  .join('/')}/`;

// The whole declaration, assembled from the constant it declares.
const declaration =
  'export const NARRATIVE_FIELD_AAD_PREFIX = ' +
  `'${NARRATIVE_FIELD_AAD_PREFIX}';`;

// Every TypeScript file under `root` that types the grammar out. Templates and
// stylesheets are not read: a prefix reaching an AES-GCM call has to be a value
// in TypeScript, and a string that appears only in markup is doing nothing at
// all.
//
// It takes its root as an argument so the negative control below can point it
// somewhere it is guaranteed to find something. A function closed over
// `sourceDir` can only ever be checked against the tree it is asserting about,
// which is the tree that must come back clean.
function modulesTypingThePrefix(root: string): string[] {
  return listFiles(root)
    .filter((path) => path.endsWith('.ts'))
    .filter((path) => readFileSync(path, 'utf8').includes(grammarPrefix))
    .map((path) => relative(root, path))
    .sort();
}

// The exemption, as one function rather than a `filter` written out in each of
// the two cases that need it.
//
// Test code is normally DAMP and a repeated one-line predicate would be
// unremarkable — but not this one. The last case exists to prove *this*
// exemption reports a stranger, and a second copy of it means that case is
// controlling its own copy while the case that matters runs another. Somebody
// widening the exemption to quieten a red result would edit one of the two, and
// the control would stay green about the wrong one. The file argues that a
// value with two spellings has none; the same holds for its own rule.
function strangersAmong(carriers: readonly string[]): string[] {
  return carriers.filter((path) => path !== owner);
}

describe('the narrative-field associated-data prefix', () => {
  it('is declared in the module that owns it', () => {
    // Arrange
    const source = readFileSync(join(sourceDir, owner), 'utf8');

    // Act
    const declaredHere = source.includes(declaration);
    const carriers = modulesTypingThePrefix(sourceDir);

    // Assert
    // A control on the read before any claim about what it contains. A path
    // that moved reads some other file perfectly well, and `includes` over the
    // wrong file is a wrong answer rather than an error.
    expect(source).toContain('export function narrativeFieldAssociatedData(');

    // The **whole declaration**, never the letters somewhere in the file — the
    // lesson `associated-data.spec.ts` learned the hard way. An unanchored
    // `source.includes(grammarPrefix)` is a search over the file, so it is
    // satisfied by a *mention*: one comment drawing the grammar in the prefix's
    // own letters keeps it green while the initialiser reads `''`, and every
    // other case in this file stays green with it.
    //
    // Nothing in the owner mentions the prefix today — the JSDoc that draws the
    // grammar points at the constant with `{@link}` — and this assertion is
    // most of the reason it can stay that way. But a mention is not a hostile
    // edit, it is the *natural* one: writing the grammar out in a comment is
    // the first thing anybody documenting this module reaches for, and it was
    // in this very file until the copy it made was noticed and removed. The
    // needle is anchored so that the day it comes back is a day this case can
    // still tell a declaration from a description of one.
    //
    // Measured rather than argued, on a module built for the measurement — the
    // constant emptied and the grammar written out in a comment beside it:
    // `includes(grammarPrefix)` answers true and `includes(declaration)`
    // answers false. The anchoring is the whole of the difference.
    //
    // The needle rides along in the message. A bare `expected false to be
    // true` is what this assertion reads as otherwise, and it is the least
    // useful sentence a red bar can carry: whoever is looking at it has to open
    // this file to find out what was being looked for.
    expect(declaredHere, `${owner} does not carry: ${declaration}`).toBe(true);

    // And the scan agrees over the real tree — the positive half a pure absence
    // check cannot say. Without it a `sourceDir` that moved, a `.ts` filter
    // letting nothing through, or a needle derived wrongly reports the clean
    // tree in the next case perfectly.
    expect(carriers).toContain(owner);
  });

  it('is typed out in no other module', () => {
    // Arrange, Act
    const strangers = strangersAmong(modulesTypingThePrefix(sourceDir));

    // Assert
    // Named, not counted. A boolean or a length tells whoever broke this that
    // something is wrong and not which file to open, and this is a rule about
    // where a string is written — the location is the whole of the finding.
    expect(
      strangers,
      `the prefix is typed out in: ${strangers.join(', ')}`,
    ).toEqual([]);
  });

  it('would be reported by path if a stranger carried it', () => {
    // Arrange
    // A planted tree, because both cases above are green over an empty
    // repository and this one must not be. The owner assertion up there proves
    // the needle and `sourceDir` reach a real file; what it cannot prove is the
    // half that matters in the other direction — that a file which is *not* the
    // owner survives the exemption instead of being filtered away beside it. An
    // exemption keyed on the directory, or on a suffix, is green on every
    // assertion in this file except the last one below.
    //
    // Built outside `src/`, so the runner is never asked to watch a file
    // appear inside the tree it is compiling.
    const root = mkdtempSync(join(tmpdir(), 'narrative-field-scan-'));

    // **Beside the owner, in the owner's own directory, and that placement is
    // the case.** A stranger planted in some unrelated folder is reported by an
    // exemption keyed on the *directory* just as happily as by one keyed on the
    // path, so the whole case passes on the broken shape — measured, not
    // reasoned: it was planted a directory away first, and swapping
    // `path !== owner` for `!path.startsWith(dirname(owner))` left all three
    // cases green. Beside the owner, that swap swallows it and reddens. It is
    // also where a copy would really be written: the next module to seal a
    // narrative field is a neighbour of the one that declares the prefix.
    const stranger = join(dirname(owner), 'copy.ts');

    // A second stranger, and it is a **spec**. Excluding `*.spec.ts` from a scan
    // like this is a natural reflex — a spec is where quoting a value is
    // normally allowed — and with only the module above planted, that exclusion
    // passes every case in this file while leaving every spec in the repository
    // free to type the prefix out. The passkey rule can afford specs; this one
    // cannot, because no spec here has any reason to spell the letters.
    const strangerSpec = join(dirname(owner), 'copy.spec.ts');

    try {
      // A copy of the owner at the owner's own relative path, so the exemption
      // has something real to exempt, three directories down, so the recursion
      // is exercised rather than assumed. Every plant is written from the
      // imported constant: one typed by hand would be a second copy of the
      // literal living in this file, which is the thing being hunted.
      mkdirSync(join(root, dirname(owner)), { recursive: true });
      writeFileSync(join(root, owner), `${declaration}\n`);
      writeFileSync(
        join(root, stranger),
        `const copied = '${NARRATIVE_FIELD_AAD_PREFIX}';\n`,
      );
      writeFileSync(
        join(root, strangerSpec),
        `const alsoCopied = '${NARRATIVE_FIELD_AAD_PREFIX}';\n`,
      );

      // Two decoys. A module carrying nothing, and markup carrying the prefix:
      // a scan that read every file, or that read none of them, disagrees with
      // the first expectation below rather than passing it by luck.
      writeFileSync(
        join(root, dirname(owner), 'quiet.ts'),
        'export const n = 1;\n',
      );
      writeFileSync(
        join(root, dirname(owner), 'markup.html'),
        `<p>${NARRATIVE_FIELD_AAD_PREFIX}</p>\n`,
      );

      // Act
      const carriers = modulesTypingThePrefix(root);
      const strangers = strangersAmong(carriers);

      // Assert
      // Exactly the three TypeScript files that carry it, and neither decoy.
      expect(carriers).toEqual([strangerSpec, stranger, owner]);

      // And the offenders come back without the owner, by path — through the
      // same `strangersAmong` the case above runs. This is the assertion the
      // case exists for: it is the only one in the file that can tell an
      // exemption which keeps a stranger out of the report from one that does
      // not, and it can only be made where a stranger is known to exist.
      expect(strangers).toEqual([strangerSpec, stranger]);
    } finally {
      // In a `finally`, so a failed expectation above leaves nothing behind in
      // the temporary directory.
      rmSync(root, { recursive: true, force: true });
    }
  });
});
