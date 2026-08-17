// **The two passkey labels are declared in `account-keys.ts` and typed nowhere
// else.**
//
// They are referred to below by name and never spelled out — not even in this
// comment. A file explaining the rule in the prefix's own letters is a file
// carrying one more copy of it, and would have to exempt itself from the thing
// it checks: the one exemption nobody would question. This paragraph was
// written the other way first, and the test below caught it.
//
// `PASSKEY_PRF_EVAL_INPUT` is the value a passkey's `prf` extension is evaluated
// against, and `PASSKEY_KEY_ENCRYPTION_KEY_INFO` is the HKDF `info` that turns
// what comes back into a key-encryption key. Both are part of the definition of
// every wrapped account key already written: an account's keys open under a
// key-encryption key derived through exactly these strings, so a second copy
// that drifts from the first locks out every account wrapped under the old
// value — with a passkey that goes on authenticating perfectly and simply hands
// back different bytes, and no error anywhere naming the cause.
//
// **No runtime assertion can hold this.** A caller that types the literal out
// agrees with the constant byte for byte on the day it is written, so every
// test comparing bytes to bytes passes — including the ones in
// `webauthn-encoding.spec.ts` and `webauthn-ceremony.service.spec.ts` that
// compare against the imported constant, which is the strongest comparison
// there is and still cannot see where the value came from. What is checkable is
// provenance, and provenance is a fact about the source text. So this spec reads
// source files, for the reason `no-external-origins.spec.ts`,
// `no-devtools.spec.ts` and `focus-ring.spec.ts` read the build output: the
// claim is about the shape of what was written, and nothing that runs can
// observe it.
//
// It needs no build. `src/` is what a reviewer reads and what the rule is
// about; the bundler inlines a `const` string into each of its use sites, so the
// emitted JavaScript cannot tell a copy from an import and would answer this
// question wrongly whichever way it answered it.
//
// The limit, stated rather than papered over: this catches the literal. It does
// not catch a value assembled at runtime out of pieces — `'budgetoid/' +
// 'passkey/…'` split across two expressions — and nothing short of parsing the
// TypeScript would. What it does catch is every shape a copy is actually
// written in, which is the shape a reader in a hurry reaches for.
import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

import { listFiles } from '../../../production-bundle';
import { PASSKEY_PRF_EVAL_INPUT } from './account-keys';

const sourceDir = join(process.cwd(), 'src');

// The prefix both labels open with, taken off the constant rather than typed
// out here.
//
// That is load-bearing twice, and the second half is the one worth reading.
// Derived, the needle cannot go on matching a prefix `account-keys.ts` has
// moved off: the day the labels are renamed, this file follows them for free
// instead of passing while watching a string nothing uses.
const labelPrefix = `${PASSKEY_PRF_EVAL_INPUT.split('/')
  .slice(0, 2)
  .join('/')}/`;

// The two files that may carry one, each for a reason that is not "it was there
// already".
const owners = new Map<string, string>([
  [
    join('app', '+core', 'security', 'account-keys.ts'),
    'declares both labels — this is the single source',
  ],
  [
    join('app', '+core', 'security', 'account-keys.spec.ts'),
    'pins their exact text, and is the only thing in the system that would ' +
      'notice one of them being edited',
  ],
]);

// Every TypeScript file under `src/`. Templates and stylesheets are not read: a
// label reaching an authenticator or an HKDF call has to be a value in
// TypeScript, and a string that appears only in markup is doing nothing at all.
function filesCarryingALabel(): string[] {
  return listFiles(sourceDir)
    .filter((path) => path.endsWith('.ts'))
    .filter((path) => readFileSync(path, 'utf8').includes(labelPrefix))
    .map((path) => relative(sourceDir, path))
    .sort();
}

describe('the passkey labels', () => {
  it('are found where they are declared and where they are pinned', () => {
    // Arrange
    // The negative control, and the reason the test below is worth reading. A
    // scan that matches nothing reports "no second copy" perfectly: a needle
    // derived wrongly, a directory that moved, a filter that lets no file
    // through. Each turns this file into a green test of nothing at all.
    const expected = [...owners.keys()].sort();

    // Act
    const carriers = filesCarryingALabel();

    // Assert
    expect(carriers).toEqual(expect.arrayContaining(expected));
  });

  it('are typed out in no other module', () => {
    // Arrange, Act
    const strangers = filesCarryingALabel().filter((path) => !owners.has(path));

    // Assert
    // A new name here is a red test and a conversation, not a diff nobody read.
    // The fix is never an entry in `owners`: it is an import from
    // `account-keys.ts`, which is what every caller in the system already does
    // and what makes "the label agrees with itself" a fact rather than a
    // coincidence that has held so far.
    expect(strangers).toEqual([]);
  });
});
