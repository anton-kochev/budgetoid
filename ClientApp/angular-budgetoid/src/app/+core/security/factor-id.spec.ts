// The spelling of a factor id is the whole of what this module owns, and every
// part of that is here: what `mintFactorId` emits, what `isCanonicalFactorId`
// refuses, and what `canonicalFactorId` folds a value from elsewhere into.
//
// The cost of getting it wrong is not a validation message. A factor id is the
// associated data both of that factor's envelopes — its wrapped private key and
// the account's keys encapsulated to it — were sealed against, and associated
// data is re-supplied from where an envelope was found
// rather than carried inside it. A client that seals under one spelling and
// hands back another rebuilds associated data that reproduces neither seal, so
// **both** envelopes stop opening — permanently, on an account whose only other
// way in is the ten codes that were shown once. Nothing anywhere names the
// cause. So the tests below are about text, and every one of them is about key
// custody.
import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

import { listFiles } from '../../../production-bundle';
import {
  canonicalFactorId,
  isCanonicalFactorId,
  mintFactorId,
} from './factor-id';

// Enough draws that a minter emitting the canonical form only *most* of the
// time is caught, and few enough that the file stays instant. `randomUUID` is
// the platform's, so this is not sampling a distribution — it is sampling a
// contract this module deliberately does not trust (see the `toLowerCase` note
// in the subject).
const DRAWS = 256;

// One identifier, written out in the one spelling the server accepts. Every row
// of the table below names *this* UUID and is refused anyway, which is the
// distinction the subject exists to hold: `isCanonicalFactorId` answers "is this
// exactly the canonical spelling", not "does this parse as a UUID".
const CANONICAL = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

describe('mintFactorId', () => {
  it('mints the lower-case hyphenated spelling and no other', () => {
    // Arrange
    const minted = new Set<string>();

    // Act
    for (let draw = 0; draw < DRAWS; draw += 1) {
      minted.add(mintFactorId());
    }

    // Assert
    // Distinctness first: a minter returning one constant satisfies the
    // spelling check on every draw, and is the shape a stubbed-out
    // implementation takes.
    expect(minted.size).toBe(DRAWS);

    for (const id of minted) {
      expect(isCanonicalFactorId(id), `${id} is not canonically spelled.`).toBe(
        true,
      );
    }
  });
});

describe('isCanonicalFactorId', () => {
  // The control the table needs: without it, a predicate that answered `false`
  // to everything would pass every row below and refuse every factor id this
  // client mints.
  it('admits the canonical spelling', () => {
    // Act & Assert
    expect(isCanonicalFactorId(CANONICAL)).toBe(true);
  });

  it.each([
    // Upper-case hex. The spelling `Guid.ToString()` produces in several .NET
    // formats and the one a client that round-trips through a native UUID type
    // is most likely to hand back.
    { spelling: 'upper-case hex', id: CANONICAL.toUpperCase() },
    // One nibble in the other case is enough — a fold applied to part of the
    // value is the shape a partial normalisation takes.
    { spelling: 'mixed case', id: '3F2504e0-4f89-41d3-9a0c-0305e82c3301' },
    // The three whitespace rows are separate because the ways they arrive
    // differ: a copied value picks up a leading space, a form field a trailing
    // one, and a file read line by line a trailing newline. JavaScript's `$`
    // without the `m` flag matches the end of the input rather than the
    // position before a terminal newline, which is what makes the third row
    // catchable here at all.
    { spelling: 'a leading space', id: ` ${CANONICAL}` },
    { spelling: 'a trailing space', id: `${CANONICAL} ` },
    { spelling: 'a trailing newline', id: `${CANONICAL}\n` },
    // The bare 32-digit form. `canonicalFactorId`, one describe block down,
    // folds this one on the way in; this predicate reports rather than folds, so
    // the two are not in conflict — and the server refuses it outright.
    { spelling: 'the bare 32-digit form', id: CANONICAL.replaceAll('-', '') },
    { spelling: 'the brace-wrapped form', id: `{${CANONICAL}}` },
    { spelling: 'the parenthesis-wrapped form', id: `(${CANONICAL})` },
  ])('refuses $spelling of the same identifier', ({ id }) => {
    // Act & Assert
    // Each row names the same UUID and is a different value on the wire. The
    // row a later read hands back is the canonical one and only that one, so
    // anything else sealed against here can never be rebuilt.
    expect(isCanonicalFactorId(id)).toBe(false);
  });
});

// The fold, which this module now owns outright.
//
// It was written twice — privately in `account-keys.ts` and again privately in
// `factor-keypair.ts` — and two copies of one rule drift with nothing failing.
// What drift would have cost is the whole account: a factor id folded one way
// by the wrapped-key grammar, deleted since, and another way by the keypair
// grammar that replaced it would have bound one factor's envelopes under two
// spellings, and associated data is re-supplied from where an envelope was
// found rather than carried inside it, so the half that drifted stops opening
// permanently with nothing naming the cause.
//
// It joins `mintFactorId` and `isCanonicalFactorId` because the three are one
// subject read three ways — what this client emits, what it accepts as already
// canonical, and what it makes canonical — and the module's own note about
// minting versus folding is the argument for keeping them together.
describe('canonicalFactorId', () => {
  it('returns the canonical spelling unchanged', () => {
    // Arrange, Act
    // The identity case, and the control the table below needs: a fold that
    // returned a constant, or one that mangled an already-correct value, passes
    // nothing here.
    const folded = canonicalFactorId(CANONICAL);

    // Assert
    expect(folded).toBe(CANONICAL);
    expect(isCanonicalFactorId(folded)).toBe(true);
  });

  it.each([
    // The spellings `isCanonicalFactorId` refuses and this folds — which is the
    // distinction the two functions exist to draw, and the reason they can live
    // in one file without contradicting each other: one reports on a value that
    // must already be canonical, the other makes one out of a value that
    // arrived from elsewhere.
    { spelling: 'upper-case hex', id: CANONICAL.toUpperCase() },
    { spelling: 'mixed case', id: '3F2504e0-4f89-41d3-9a0c-0305e82c3301' },
    { spelling: 'the bare 32-digit form', id: CANONICAL.replaceAll('-', '') },
    // The bare form in upper case, which is the row a fold written as "strip
    // the braces, then hyphenate" gets wrong by lowering only part of the
    // value — and neither single-fault row above can see it.
    {
      spelling: 'the bare form in upper case',
      id: CANONICAL.replaceAll('-', '').toUpperCase(),
    },
    { spelling: 'the brace-wrapped form', id: `{${CANONICAL}}` },
    { spelling: 'the parenthesis-wrapped form', id: `(${CANONICAL})` },
    {
      spelling: 'the brace-wrapped bare form',
      id: `{${CANONICAL.replaceAll('-', '')}}`,
    },
  ])('folds $spelling to the canonical one', ({ id }) => {
    // Act
    const folded = canonicalFactorId(id);

    // Assert
    // The value, not merely its shape. `isCanonicalFactorId(folded)` alone is
    // satisfied by a fold that hands back some *other* canonical id, which is a
    // factor bound to an identifier nobody holds.
    expect(folded).toBe(CANONICAL);
  });

  it.each([
    // Whitespace is refused rather than trimmed, in all three of the ways it
    // arrives. A fold that trimmed would be inventing a second spelling of a
    // value that has one, at the sealing end, which is where the damage cannot
    // be undone.
    { why: 'a leading space', id: ` ${CANONICAL}` },
    { why: 'a trailing space', id: `${CANONICAL} ` },
    { why: 'a trailing newline', id: `${CANONICAL}\n` },
    // A free-form label — the thing a factor id would become if this refusal
    // were dropped, and the one that reintroduces every ambiguity the
    // separators exist to remove.
    { why: 'a human label', id: 'the passkey on my phone' },
    { why: 'nothing at all', id: '' },
    // One digit short and one digit over, so a regular expression that lost an
    // anchor or a repetition count is caught in both directions.
    { why: 'one digit short', id: CANONICAL.slice(0, -1) },
    { why: 'one digit too many', id: `${CANONICAL}0` },
    // A non-hex letter in an otherwise perfectly shaped value.
    { why: 'a non-hex digit', id: '3f2504e0-4f89-41d3-9a0c-0305e82c330g' },
    // Half-wrapped, which a fold that tests only the opening character accepts
    // and then hyphenates into a value nothing will reproduce.
    { why: 'a lone opening brace', id: `{${CANONICAL}` },
    { why: 'mismatched wrappers', id: `{${CANONICAL})` },
  ])('refuses $why rather than repairing it', ({ id }) => {
    // Act & Assert
    // A throw, not a fallback. Every caller of this fold is about to seal
    // something under its answer, and a plausible-looking answer is exactly the
    // failure that is discovered months later by somebody who cannot get in.
    expect(() => canonicalFactorId(id)).toThrow();
  });

  it('is defined in this module and in no other', () => {
    // Arrange
    // **The point of the move, and nothing that runs can observe it.** Two
    // copies of this fold agree on every input on the day the second is
    // written; what they cannot do is stay agreed. So the claim is about the
    // source text — the same technique, and the same argument,
    // `key-import-single-source.spec.ts` makes about a platform call.
    //
    // The needle is the definition rather than the name: callers import it, and
    // three modules' worth of prose discusses it, so a scan for the bare name
    // would report every one of them.
    const sourceDir = join(process.cwd(), 'src');
    const needle = 'function canonicalFactorId(';

    // Act
    const definers = listFiles(sourceDir)
      .filter((path) => path.endsWith('.ts'))
      .filter((path) => !path.endsWith('.spec.ts'))
      .filter((path) => readFileSync(path, 'utf8').includes(needle))
      .map((path) => relative(sourceDir, path))
      .sort();

    // Assert
    // Named rather than counted, so a red bar says which file grew a second
    // copy instead of saying that some file did.
    expect(definers).toEqual([
      join('app', '+core', 'security', 'factor-id.ts'),
    ]);
  });
});
