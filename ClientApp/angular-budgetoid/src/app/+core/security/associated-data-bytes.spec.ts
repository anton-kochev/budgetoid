// The byte spelling of the one join, and the equivalence that makes it one.
//
// `buildAssociatedData` joins strings and encodes the result; `joinFields` joins
// bytes. They are not two rules: the first is the second under an encoder, and
// this file is where that sentence is checked rather than asserted in a comment.
// Two grammars already seal under the string spelling and a third — the
// factor-keypair one, whose fields are a raw version byte and two 65-byte curve
// points — cannot use it at all, since a point pushed through UTF-8 comes out
// 129 bytes long.
//
// **Its own file, beside `associated-data.spec.ts` rather than inside it.** That
// file is the regression anchor for the string join: every one of its cases was
// written before this refactor and passes unedited afterwards, which is the
// whole of the evidence that re-expressing `buildAssociatedData` on top of
// `joinFields` changed no byte of any grammar already sealed. Cases added to it
// would have been written after the change and could not say that.
//
// What is checked here, in order: the byte join's own rule — separator between
// and never around, every field kept, nothing folded, fresh bytes each call —
// and then the equivalence, over inputs chosen to break it if it can be broken.
import { describe, expect, it } from 'vitest';

import {
  UNIT_SEPARATOR,
  buildAssociatedData,
  joinFields,
} from './associated-data';

const utf8 = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

// The fields as bytes, so a case can be written in the letters it is about.
function encoded(...fields: readonly string[]): readonly Uint8Array[] {
  return fields.map((field) => utf8.encode(field));
}

describe('the shared join over bytes', () => {
  it('joins its fields with the unit separator, between them', () => {
    // Arrange
    // Two one-byte fields, so all three bytes of the answer are load-bearing:
    // the order, the separator between rather than around, and the byte itself.
    const fields = encoded('a', 'b');

    // Act
    const joined = joinFields(...fields);

    // Assert
    expect(toHex(joined)).toBe('611f62');
  });

  it('joins a single field to just that field, on a fresh array', () => {
    // Arrange
    // The degenerate case a join written as "append the field, then append a
    // separator" gets wrong while passing the case above — and the one where the
    // obvious shortcut is to hand the caller's own array straight back. That
    // shortcut is the same hazard `associated-data.spec.ts` names about a
    // cached answer: zero-filling a used buffer is ordinary practice in this
    // folder, and a wipe over a returned view reaches into the caller's field.
    const field = utf8.encode('only');

    // Act
    const joined = joinFields(field);

    // Assert
    expect(toHex(joined)).toBe('6f6e6c79');
    expect(joined).not.toBe(field);
    expect(joined.buffer).not.toBe(field.buffer);
  });

  it('joins zero fields to no bytes', () => {
    // Arrange, Act
    // Pinned rather than left to whatever an arithmetic over an empty list
    // happens to do — a separator count of `length - 1` is `-1` here, and a
    // width computed from it is either a throw or a silently short array.
    const joined = joinFields();

    // Assert
    expect(joined).toHaveLength(0);
    expect(toHex(joined)).toBe('');
  });

  it('keeps an empty field, rather than dropping it from the join', () => {
    // Arrange, Act
    // Dropping one seals two different field lists to the same bytes, which is
    // the one ambiguity a separator exists to remove. No grammar passes an
    // empty field today, which is exactly why the hole would survive until one
    // does.
    const holed = joinFields(...encoded('a', '', 'b'));
    const solid = joinFields(...encoded('a', 'b'));

    // Assert
    expect(toHex(holed)).toBe('611f1f62');
    expect(toHex(holed)).not.toBe(toHex(solid));
  });

  it('joins three fields and five, not only the two above', () => {
    // Arrange, Act
    // Arity, which this file claims, and not format, which it does not. An
    // implementation reading `fields[0]` and `fields[1]` and stopping is green
    // on every case above; five is the width the factor-keypair grammar's HKDF
    // info actually uses.
    const three = joinFields(...encoded('a', 'b', 'c'));
    const five = joinFields(...encoded('a', 'b', 'c', 'd', 'e'));

    // Assert
    expect(toHex(three)).toBe('611f621f63');
    expect(toHex(five)).toBe('611f621f631f641f65');
  });

  it('passes a field containing the separator through unchanged', () => {
    // Arrange
    // **Deliberately not refused and deliberately not escaped**, for the reason
    // the module states: whether a field can contain the byte is a claim about
    // the *fields*, and only a grammar is in a position to hold it. A repair
    // here would silently change bytes a caller believed it had chosen; a
    // refusal here would refuse a value this module cannot describe.
    //
    // It is also the case the bytes spelling makes reachable in a way the string
    // one does not: the factor-keypair grammar's points are raw bytes, and 0x1F
    // occurs inside a 65-byte point often enough that this is a real input
    // rather than a hostile one. What makes that message unambiguous is the
    // fixed widths of its fields, which is a fact about that grammar.
    const smuggled = Uint8Array.of(0x61, 0x1f, 0x62);

    // Act
    const joined = joinFields(smuggled, utf8.encode('c'));

    // Assert
    // Four separator-shaped bytes' worth of ambiguity, reproduced exactly: the
    // join added one byte and changed none.
    expect(toHex(joined)).toBe('611f621f63');
    expect(toHex(joinFields(...encoded('a', 'b', 'c')))).toBe(toHex(joined));
  });

  it('leaves the arrays it was handed alone', () => {
    // Arrange
    // A join that wrote its separator into the caller's field to save an
    // allocation — or that used the first field as its accumulator — passes
    // every case above and corrupts the next message built from the same point.
    // The factor-keypair grammar hands this function the *same* public key twice
    // in one message, so one mutation there is two wrong fields.
    const first = utf8.encode('ab');
    const second = utf8.encode('cd');

    // Act
    joinFields(first, second);

    // Assert
    expect(toHex(first)).toBe('6162');
    expect(toHex(second)).toBe('6364');
  });

  it('returns fresh bytes each call, over no buffer it keeps', () => {
    // Arrange, Act
    // A module-level scratch buffer is a natural shape for this function and is
    // green on every case above, because every case above reads its answer
    // before calling again. Two messages built and then two values sealed would
    // both be bound to whichever was built last — neither of them wrong-looking.
    const first = joinFields(...encoded('a'));
    const second = joinFields(...encoded('b', 'c'));
    const again = joinFields(...encoded('a'));

    // Assert
    // The first value is read *after* the later calls, which is the only
    // ordering that can catch a shared buffer.
    expect(toHex(first)).toBe('61');
    expect(toHex(second)).toBe('621f63');
    expect(first.buffer).not.toBe(second.buffer);

    // And a fresh array for fields it has already been handed, so that a caller
    // wiping one answer cannot hand the next caller zeros.
    expect(again).not.toBe(first);
    expect(again.buffer).not.toBe(first.buffer);

    first.fill(0);

    expect(toHex(joinFields(...encoded('a')))).toBe('61');
  });
});

// The definition `buildAssociatedData` had before it was expressed on
// `joinFields`, quoted here as the reference every row below is measured
// against.
//
// **Comparing the two exported functions would be vacuous and looked fine.**
// `buildAssociatedData` *is* `joinFields` under an encoder now, so a row
// asserting they agree asserts that one function equals itself — green on every
// input, including the ones where the refactor is wrong. Measured: with the join
// written to skip its separator whenever nothing has been emitted yet, the
// exported pair agreed on all nine rows and this reference disagreed on two of
// them. So the thing a refactor has to be checked against is the definition it
// replaced, written out where a reader can see it is the old one.
const previouslyBuiltAssociatedData = (
  ...fields: readonly string[]
): Uint8Array => utf8.encode(fields.join(UNIT_SEPARATOR));

// The re-derivation. Each row is a field list, and the claim is that both of
// today's spellings reproduce the definition above byte for byte — which is what
// makes this a refactor rather than a change to every value already sealed.
//
// The rows are the places they could come apart. Most reduce to one question: is
// UTF-8 of a concatenation the concatenation of the UTF-8s? It is, **because the
// separator is a BMP non-surrogate code point**, so no pairing can happen across
// it — and that is what the surrogate rows are for. The empty-field rows ask a
// different question, and it is the one that actually bit: `Array.join` counts
// fields, so a byte join that counts *bytes written* instead puts no separator
// after a leading empty field.
describe('both spellings reproduce the join they replaced', () => {
  it.each([
    { why: 'plain ASCII fields', fields: ['a', 'b', 'c'] },
    { why: 'an empty field between two others', fields: ['a', '', 'b'] },
    { why: 'empty fields at both ends', fields: ['', 'a', ''] },
    { why: 'no fields at all', fields: [] },
    // A high surrogate ending one field and a low surrogate starting the next.
    // **This is the row the whole equivalence turns on.** Concatenated with
    // nothing between them the two would pair into one astral code point and
    // four bytes; with the separator between them neither can pair, and each
    // encodes to the three-byte replacement character on both paths.
    {
      why: 'a lone high surrogate either side of the separator',
      fields: ['a\ud800', '\udc00b'],
    },
    // The same two code units inside one field, where they *do* pair — so the
    // row above is a difference of two bytes' encoding rather than of a rule.
    { why: 'a matched astral pair inside one field', fields: ['😀'] },
    // A lone surrogate all by itself, the shape a truncated string arrives as.
    { why: 'a field that is one lone surrogate', fields: ['\udfff', 'b'] },
    // Characters on both sides of U+00FF, where Latin-1 and UTF-8 disagree
    // without truncating and where a UTF-16 count differs from a UTF-8 one.
    { why: 'characters above and below U+00FF', fields: ['é€', 'a'] },
    // A field carrying the separator itself. Neither spelling escapes it and
    // neither refuses it, so both must produce the same ambiguous message.
    {
      why: 'a field containing the separator',
      fields: [`a${UNIT_SEPARATOR}b`, 'c'],
    },
  ])('agrees on $why', ({ fields }) => {
    // Arrange
    const expected = toHex(previouslyBuiltAssociatedData(...fields));

    // Act
    const asStrings = buildAssociatedData(...fields);
    const asBytes = joinFields(...encoded(...fields));

    // Assert
    // Hex rather than `toEqual`, so a failure prints the bytes that differ. Both
    // spellings are named against the reference rather than against each other,
    // for the reason the reference's own note gives.
    expect(toHex(asStrings)).toBe(expected);
    expect(toHex(asBytes)).toBe(expected);
  });
});
