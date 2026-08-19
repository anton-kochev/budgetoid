// The spelling of a factor id is the whole of what this module owns, and both
// halves of that are here: what `mintFactorId` emits, and what
// `isCanonicalFactorId` refuses.
//
// The cost of getting it wrong is not a validation message. A factor id is the
// associated data both of that factor's wrapped-key envelopes were sealed
// against, and associated data is re-supplied from where an envelope was found
// rather than carried inside it. A client that seals under one spelling and
// hands back another rebuilds associated data that reproduces neither seal, so
// **both** envelopes stop opening — permanently, on an account whose only other
// way in is the ten codes that were shown once. Nothing anywhere names the
// cause. So the tests below are about text, and every one of them is about key
// custody.
import { describe, expect, it } from 'vitest';
import { isCanonicalFactorId, mintFactorId } from './factor-id';

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
    // The bare 32-digit form. `account-keys.ts` folds this one on the way in;
    // this module reports rather than folds, so the two are not in conflict —
    // and the server refuses it outright.
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
