// **No sentence in this client may say that nothing a person records is
// encrypted.**
//
// Eight narrative columns hold AEAD ciphertext sealed under keys derived from
// the person's own passkey, and unlocking is the difference between a screen
// they can read and one they cannot. A sentence saying otherwise is not stale
// decoration: it tells somebody who has just watched their authenticator answer
// that the answer bought them nothing, and it tells somebody reading the
// privacy disclosure that the operator can read names and notes it can no
// longer read. Both are false in the direction that costs the reader something.
//
// **Why a source-text scan and not an assertion on the new sentence.** The
// screen's own spec already pins the copy the account-keys section renders, and
// that pin was exactly as green over the false sentence as it will be over the
// true one — a pin says *this text is on screen*, never *this claim is true*.
// Rewrite the copy wrongly a year from now and the pin moves with it in the same
// commit, silently. What is checkable is that a known-false claim is written
// nowhere, and that is a fact about the source text rather than about anything
// that runs. So this file reads source files, for the reason
// `field-label-single-source.spec.ts` and `key-import-single-source.spec.ts`
// read theirs.
//
// It reads `src/` and needs no build, unlike `no-external-origins.spec.ts` and
// `no-devtools.spec.ts` next door. Those two ask what the browser *fetches*,
// which only the emitted bundle can answer; this one asks what somebody *wrote*,
// and the bundle inlines, renames and rewraps on the way out.
//
// **Specs are in scope, and that is the opposite of the exemption
// `key-import-single-source.spec.ts` grants its own neighbours.** The difference
// is whether a file has a reason to spell the value. A spec pinning a door has
// to name `crypto.subtle.importKey`; no spec here has any reason to spell a
// sentence the product may not say — a copy pin quotes what is on screen, so a
// spec still carrying one of these phrases is either pinning copy that must
// change or arguing from a premise that is dead. Both are findings. The one
// exemption is this file, which cannot hunt a phrase without writing it.
//
// **The scan is whitespace-blind, and that is load-bearing rather than
// defensive.** Prettier wraps a template at 80 columns, so
// `Nothing is\n    encrypted with a key only you hold` is not an evasion anybody
// designed — it is what the formatter does to an ordinary paragraph, and a
// literal `includes` over the raw file reports it clean. Every file is lowered
// and collapsed to single spaces before the phrases are looked for, and the
// non-breaking space entity is collapsed with them.
//
// Four limits, stated rather than papered over.
//
//   * It catches a phrase, not a paraphrase. *Your records are stored in the
//     clear* says the same false thing and passes here. Nothing short of reading
//     the copy can catch that, which is what review and the design book are for;
//     what this holds is that the three spellings that shipped cannot come back.
//   * It does not strip markup, so `nothing is <em>encrypted</em>` passes. A
//     tag-stripper was considered and refused: it joins adjacent elements, so
//     `<p>…nothing is</p><p>encrypted…</p>` would be reported as a sentence
//     nobody wrote, and a guard with false positives is answered by deleting the
//     guard.
//   * It reads `.html`, `.ts` and `.scss` under `src/` and nothing else. Prose a
//     person reads is written in a template or in a TypeScript string; `.scss`
//     is in because a `content:` declaration is a cheap door to leave open, not
//     because anything writes copy there today. `public/` is out of scope — no
//     copy is authored there — and so is `docs/`, which is the design book's own
//     to keep true.
//   * It is blind to a phrase assembled at runtime out of pieces, and nothing
//     short of parsing the TypeScript would see one.
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

import { listFiles } from './production-bundle';

const sourceDir = join(process.cwd(), 'src');

// The three spellings of the dead premise, lowered and space-collapsed like the
// text they are looked for in — a needle carrying a capital or a line break
// would match nothing at all and this whole file would report a clean tree
// forever.
//
// A plain list rather than the `Map` of reasons `key-import-single-source.spec.ts`
// keeps, and the difference is the polarity. That file lists who is *allowed*,
// so an entry appended to quieten a red bar widens the rule and a written reason
// is what makes the widening deliberate. This one lists what is *refused*, where
// appending an entry can only narrow what the product may say — the diff nobody
// reads is the safe direction here, and demanding a paragraph for each spelling
// would make hardening the rule the expensive move.
const denials = [
  'nothing is encrypted',
  'not encrypted yet',
  'nothing you record is encrypted',
] as const;

// This file's own path under `src/`, and the only exemption there is. Written as
// a lone constant rather than as a collection for the reason
// `field-label-single-source.spec.ts` gives about its owner: a set invites a red
// result to be answered by appending a path, and there is no second file with
// standing to write these sentences.
const owner = 'no-encryption-denials.spec.ts';

// Where copy can be authored. Everything else under `src/` — assets, JSON, the
// frozen vectors — is either not prose or not this repository's to word.
const scannedExtensions = ['.html', '.ts', '.scss'] as const;

// Characters either side of a match carried into the message. A phrase alone
// says which rule broke and not which paragraph to open, and in a 700-line
// template those are different questions.
const contextMargin = 40;

interface Denial {
  readonly file: string;
  readonly phrase: string;
  readonly context: string;
}

function normalize(text: string): string {
  return (
    text
      .toLowerCase()
      // Before the collapse, because `&nbsp;` is not whitespace until it is
      // decoded and the entity is what a template author actually types.
      .replaceAll('&nbsp;', ' ')
      .replaceAll('&#160;', ' ')
      .replace(/\s+/gu, ' ')
  );
}

// Every file under `root` whose text is in scope, by path relative to `root`.
//
// It takes its root as an argument so the planted control below can point it
// somewhere a denial is guaranteed to exist. A function closed over `sourceDir`
// can only ever be checked against the tree it is asserting about, which is the
// tree that must come back clean.
function scannedFiles(root: string): string[] {
  return listFiles(root)
    .filter((path) =>
      scannedExtensions.some((extension) => path.endsWith(extension)),
    )
    .map((path) => relative(root, path))
    .filter((path) => path !== owner)
    .sort();
}

function contextAround(text: string, phrase: string): string {
  const at = text.indexOf(phrase);

  return text
    .slice(Math.max(0, at - contextMargin), at + phrase.length + contextMargin)
    .trim();
}

function denialsIn(root: string): Denial[] {
  return scannedFiles(root).flatMap((file) => {
    const text = normalize(readFileSync(join(root, file), 'utf8'));

    return denials
      .filter((phrase) => text.includes(phrase))
      .map((phrase) => ({
        file,
        phrase,
        context: contextAround(text, phrase),
      }));
  });
}

function formatDenial({ file, phrase, context }: Denial): string {
  return `${file} says "${phrase}" — …${context}…`;
}

describe('a sentence denying that anything is encrypted', () => {
  it('is looked for in the files the client writes copy in', () => {
    // Arrange, Act
    const scanned = scannedFiles(sourceDir);

    // Assert
    // The control on the read, and it is the half a pure absence check cannot
    // say. A `sourceDir` that moved, an extension filter letting nothing
    // through, or an exemption that swallowed the tree all report the case below
    // perfectly clean — a scan pointed at the wrong directory is worse than no
    // scan, because it is a green bar somebody trusts.
    //
    // Two named files rather than a count alone: these are where the false
    // sentences were written, so they are the two the rule was born over.
    expect(scanned).toContain(
      join('app', 'settings', 'settings.component.html'),
    );
    expect(scanned).toContain(
      join('app', 'settings', 'settings.component.spec.ts'),
    );

    // And a loose floor under the whole walk, so a recursion that stopped at the
    // first directory is caught too. Loose on purpose — a tight count reddens on
    // every file anybody adds, and this case is about the scan reaching the tree
    // rather than about the tree's size.
    expect(scanned.length).toBeGreaterThan(50);
  });

  it('is written nowhere in the client', () => {
    // Arrange, Act
    const found = denialsIn(sourceDir);

    // Assert
    // Named with their context, never counted. This is a rule about *where* a
    // sentence was written, so the location is the whole of the finding, and a
    // length tells whoever broke it that something is wrong without saying which
    // file to open.
    expect(found.map(formatDenial)).toEqual([]);
  });

  it('would be reported by path, phrase and context wherever it was written', () => {
    // Arrange
    // A planted tree, because the case above is green over an empty repository
    // and this one must not be. The control above proves the scan reaches real
    // files; what it cannot prove is that a file carrying a denial *survives* the
    // exemption instead of being filtered away beside this one.
    //
    // Built outside `src/`, so the runner is never asked to watch a file appear
    // inside the tree it is compiling.
    const root = mkdtempSync(join(tmpdir(), 'encryption-denial-scan-'));

    const wrapped = join('app', 'settings', 'settings.component.html');
    const pinned = join('app', 'settings', 'settings.component.spec.ts');
    const shouted = join('app', 'settings', 'upper.ts');
    const corrected = join('app', 'settings', 'corrected.html');

    try {
      mkdirSync(join(root, dirname(wrapped)), { recursive: true });

      // **Wrapped mid-phrase, which is the case this plant exists for.** It is
      // how Prettier leaves an ordinary paragraph at 80 columns, and a scan that
      // matched raw text would report this file clean — the failure the
      // whitespace collapse is there to refuse.
      writeFileSync(
        join(root, wrapped),
        '<p class="s-prose">\n  Today that also includes the names and notes you type. Nothing is\n  encrypted with a key only you hold &mdash; not yet.\n</p>\n',
      );

      // A spec pinning the copy. It must be reported: no spec has standing to
      // spell one of these sentences, and a copy pin carrying one is pinning
      // text the product may not say.
      writeFileSync(
        join(root, pinned),
        "const HONESTY = 'Nothing you record is encrypted yet, so unlocking changes nothing you can see today.';\n",
      );

      // Shouted, so the lowering is exercised rather than assumed.
      writeFileSync(
        join(root, shouted),
        "export const NOTICE = 'YOUR RECORDS ARE NOT ENCRYPTED YET.';\n",
      );

      // **The corrected copy, and it must come back unreported.** Without this
      // plant the whole file is satisfied by a scan that reports every template
      // it reads, which is a guard that can never go green and would be answered
      // by deleting it. This is the assertion that makes the rule discriminate
      // rather than always-fail.
      writeFileSync(
        join(root, corrected),
        '<p class="s-prose">\n  Your passkey holds the keys your records are encrypted with. Budgetoid\n  never sees them, and this browser forgets them every time the page\n  reloads.\n</p>\n<p class="s-prose">\n  Unlocking is what lets this tab read the names and notes on your accounts,\n  categories and transactions.\n</p>\n',
      );

      // This file's own twin, carrying every phrase, at this file's own relative
      // path. It must come back unreported, and it is the only thing that can
      // tell the one-file exemption from an exemption that grew a suffix: swap
      // `path !== owner` for `!path.endsWith('.spec.ts')` and the plant above
      // disappears with it while every other expectation here still passes.
      writeFileSync(
        join(root, owner),
        `const needles = ${JSON.stringify(denials)};\n`,
      );

      // Two decoys. Prose in a file type nothing renders, and a module carrying
      // nothing: a scan that read every file, or that read none of them,
      // disagrees with the expectation below rather than passing it by luck.
      writeFileSync(join(root, 'notes.md'), 'Nothing is encrypted yet.\n');
      writeFileSync(join(root, 'quiet.ts'), 'export const n = 1;\n');

      // Act
      const found = denialsIn(root);

      // Assert
      // Exactly the three offenders, each under the phrase that found it, in
      // path order. The corrected copy, this file's twin and both decoys are
      // absent, and each absence is a different way the scan could have been
      // wrong.
      expect(found.map(({ file, phrase }) => `${file}: ${phrase}`)).toEqual([
        `${wrapped}: nothing is encrypted`,
        `${pinned}: nothing you record is encrypted`,
        `${shouted}: not encrypted yet`,
      ]);

      // And the context is real text off the file rather than the phrase echoed
      // back. Asserted by what surrounds the match, not by an exact slice, so
      // the margin can be widened without rewriting this case.
      expect(found[0]?.context).toContain('with a key only you hold');
    } finally {
      // In a `finally`, so a failed expectation above leaves nothing behind in
      // the temporary directory.
      rmSync(root, { recursive: true, force: true });
    }
  });
});
