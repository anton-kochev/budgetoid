// One join, shared by every associated-data grammar this client seals under.
//
// The separator and the join were private to `account-keys.ts`, where the
// wrapped-key grammar is the only caller. A second grammar — the narrative
// fields of the next story — is what makes them worth extracting: two copies of
// "the fields are joined by 0x1F in UTF-8" drift, and drift here is silent.
// Associated data is not carried inside an envelope; it is re-supplied from
// wherever the envelope was found, so a byte that moves makes every envelope
// already written unopenable with the same failure a corrupted key gives, and
// nothing anywhere names the cause.
//
// **Nothing below states anything about a grammar. Every case is about the
// join.** No prefix, no factor id, no purpose and no field name appears in this
// file, and no vector is reproduced from anywhere. What a grammar is made of is
// guarded where it belongs: by an independently-computed vector over the whole
// string — the frozen 69-byte wrapped-key one already in `account-keys.spec.ts`
// and the 80-byte narrative one arriving with the grammar that needs it, both
// computed outside this codebase. So a failure here names the join, a failure
// there names the format, and neither has to be read to understand the other.
//
// One case reads the module's own source instead of calling it. That is still a
// claim about the join and not about a grammar — it is about how the separator
// is *written down*, which no value can report on and nothing that runs can
// see.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

import { UNIT_SEPARATOR, buildAssociatedData } from './associated-data';

// `src/` rather than the build output, for the reason
// `passkey-label-single-source.spec.ts` gives: `src/` is what a reviewer reads
// and what the rule is about, and reading it needs no prior build.
const modulePath = join(
  process.cwd(),
  'src',
  'app',
  '+core',
  'security',
  'associated-data.ts',
);

// Local on purpose. A helper imported from another spec would make this file's
// answers depend on a file it has no reason to be coupled to, and a hex
// rendering is four lines.
function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

describe('the shared associated-data join', () => {
  it('joins its fields with the unit separator, in UTF-8', () => {
    // Arrange
    // Two one-byte fields, so the whole answer is three bytes and every one of
    // them is load-bearing: the order of the fields, the separator sitting
    // between them rather than around them, and the encoding — all pinned at
    // once, with nothing in the expectation that could be right by accident.
    const first = 'a';
    const second = 'b';

    // Act
    const associatedData = buildAssociatedData(first, second);

    // Assert
    // The hex, not the length. A length assertion passes on a join that
    // separated with a space, or that reversed the fields.
    expect(toHex(associatedData)).toBe('611f62');
  });

  it('spells the separator as the ASCII unit separator', () => {
    // Arrange, Act
    // Nothing to arrange: the constant is the subject. It is exported so that
    // a caller reasoning about whether one of its fields can contain the byte
    // has something to name, and so that this assertion has somewhere to point.
    const code = UNIT_SEPARATOR.charCodeAt(0);

    // Assert
    // 0x1F is what makes "no length prefix is needed" true: it cannot occur in
    // any of the fields either grammar separates, so the fields cannot run into
    // one another. That is an argument about the fields, and it is worth
    // nothing if the character here is not the one the argument was made about.
    expect(code).toBe(0x1f);

    // One character, not a string that happens to start with the right one. A
    // separator of any other width changes the bytes of every value sealed
    // under it.
    expect(UNIT_SEPARATOR).toHaveLength(1);
  });

  it('spells that byte by code point in its source, not as the character', () => {
    // Arrange
    // The case above reads the *value*, and a value cannot report how it was
    // written. Typing a raw U+001F between two quotes leaves `charCodeAt(0)` at
    // `0x1f` and the length at one, so it and every other case in this file stay
    // green — while costing the property the module argues for in its own words.
    // A raw control character renders as nothing at all in a diff, in a terminal
    // and in most editors, so the one line a reviewer of a frozen format has to
    // check becomes the line they cannot see. It is also nearly uneditable: a
    // search for it matches nothing a person can type.
    const source = readFileSync(modulePath, 'utf8');

    // Act
    // The needle is the **whole declaration**, not the call on its own, and the
    // difference is the test. `source.includes('String.fromCharCode(0x1f)')`
    // is a search over the file, so it is satisfied by the text appearing
    // anywhere in it — in a comment, in a JSDoc block, in a dead constant —
    // while the initialiser that actually runs is `''`. Both assertions below
    // stay green in that state and the property is gone, and the route into it
    // is helpful rather than hostile: the JSDoc over the constant already
    // explains in words that it is spelled by code point, and quoting the
    // spelling it describes is the most natural next edit anybody could make.
    const spelledByCodePoint = source.includes(
      'export const UNIT_SEPARATOR = String.fromCharCode(0x1f);',
    );
    const carriesTheCharacter = source.includes(UNIT_SEPARATOR);

    // Assert
    // Two negative controls first, because the second assertion below is the
    // vacuous kind — "the character is not in there" is reported perfectly by a
    // path that moved, a file read as the wrong thing, or a needle that can
    // never match. The first control says the text read is this module and not
    // some other file; the second says the search finds the character when the
    // character is there.
    expect(source).toContain('export function buildAssociatedData(');
    expect(`x${UNIT_SEPARATOR}y`).toContain(UNIT_SEPARATOR);

    expect(spelledByCodePoint).toBe(true);
    expect(carriesTheCharacter).toBe(false);
  });

  it('encodes fields as UTF-8, not UTF-16', () => {
    // Arrange
    // Written with `\u` escapes rather than as literal characters. A literal
    // would be at the mercy of whatever normalisation an editor, a shell or a
    // patch tool applied on its way into this file: U+00E9 composed is one code
    // point and two UTF-8 bytes, while its NFD form is two code points and
    // three, and the source would look identical either way.
    //
    // U+20AC, the euro sign, is what separates UTF-8 from UTF-16 by count.
    // Both encode U+00E9 in two bytes, so an accented letter alone proves
    // nothing here; U+20AC is three bytes in UTF-8 and two in UTF-16.
    const field = '\u00e9\u20ac';

    // Act
    const associatedData = buildAssociatedData(field);

    // Assert
    // Two code points in, five bytes out — and the exact five, so a third
    // encoding that happened to agree on the count would still be caught.
    expect(field).toHaveLength(2);
    expect(associatedData).toHaveLength(5);
    expect(toHex(associatedData)).toBe('c3a9e282ac');
  });

  it('joins a single field to just that field', () => {
    // Arrange
    // The degenerate case, and the one a join written as "append the field,
    // then append a separator" gets wrong while passing the two-field test
    // above. A trailing separator is invisible in every rendering of the value
    // and changes the bytes of everything sealed under it.
    const only = 'only';

    // Act
    const associatedData = buildAssociatedData(only);

    // Assert
    expect(toHex(associatedData)).toBe('6f6e6c79');
  });

  it('joins zero fields to no bytes', () => {
    // Arrange, Act
    // Pinned rather than left to whatever `Array.join` happens to do with an
    // empty list. No grammar calls it this way today, which is exactly why the
    // answer needs writing down: an undefined corner is one a later caller
    // discovers by shipping it.
    const associatedData = buildAssociatedData();

    // Assert
    expect(associatedData).toHaveLength(0);
    expect(toHex(associatedData)).toBe('');
  });

  it('keeps an empty field, rather than dropping it from the join', () => {
    // Arrange
    // A join written as `fields.filter(Boolean).join(sep)` — the shape a
    // filter-before-join reflex produces — is green on every case above,
    // because no case above has an empty field. It also seals two different
    // field lists to the same bytes, which is the one ambiguity the separator
    // exists to remove. No grammar passes an empty field today, and that is
    // exactly why the hole would survive until the day one does.

    // Act
    const holed = buildAssociatedData('a', '', 'b');
    const solid = buildAssociatedData('a', 'b');

    // Assert
    // Two separators for three fields, the second adjacent to the first.
    expect(toHex(holed)).toBe('611f1f62');

    // And the collision named outright, so a reader can see what the assertion
    // above buys without counting bytes to work it out.
    expect(toHex(holed)).not.toBe(toHex(solid));
  });

  it('joins three fields and four, not only the two above', () => {
    // Arrange
    // Every case above uses nought, one or two fields, while the one grammar
    // that exists today joins three — and the one arriving next is wider still.
    // An implementation that reads `fields[0]` and `fields[1]` and stops is
    // green on all of them and silently drops whatever a grammar put after
    // them. That is arity, which this file claims as its own, and not format,
    // which it does not.

    // Act
    const three = buildAssociatedData('a', 'b', 'c');
    const four = buildAssociatedData('a', 'b', 'c', 'd');

    // Assert
    expect(toHex(three)).toBe('611f621f63');
    expect(toHex(four)).toBe('611f621f631f64');
  });

  it('passes every field through unchanged, folding nothing', () => {
    // Arrange
    // Two fields, each carrying something a helpful join would quietly repair.
    // The first has leading and trailing spaces and mixed case, which `.trim()`
    // or `.toLowerCase()` would take off; the second is `e` followed by a
    // combining acute, which `.normalize('NFC')` would fold to one code point
    // and two bytes where there were three.
    //
    // Nothing above notices any of it — every field up there is already
    // trimmed, already lower case and already composed. And the repair is the
    // wrong favour to do here: a grammar that *refuses* a non-canonical field
    // needs it to arrive as it was written, or the refusal is decorative.
    //
    // Escaped rather than typed, for the reason the UTF-8 case gives. A
    // combining mark is invisible in a diff, and the decomposed spelling is one
    // normalising editor away from silently becoming the composed one.
    const untrimmed = '  Mixed Case  ';
    const decomposed = 'e\u0301';

    // Act
    const associatedData = buildAssociatedData(untrimmed, decomposed);

    // Assert
    expect(toHex(associatedData)).toBe('20204d69786564204361736520201f65cc81');

    // And the composed spelling of that same letter is different bytes, which
    // is the whole of what "folds nothing" means.
    expect(toHex(buildAssociatedData(decomposed))).not.toBe(
      toHex(buildAssociatedData('\u00e9')),
    );
  });

  it('returns fresh bytes each call, over no buffer it keeps', () => {
    // Arrange
    // A module-level scratch buffer written through `encodeInto` is a natural
    // shape for this function and is green on every case above, because every
    // case above asserts its answer before calling again. A caller that built
    // two associated-data values and then sealed two envelopes would get both
    // bound to whichever was built last — two envelopes bound to the same
    // thing, neither of them wrong-looking. `key-envelope.ts` already pays for
    // the same caution, handing the cipher a buffer it owns rather than the
    // caller's.

    // Act
    const first = buildAssociatedData('a');
    const second = buildAssociatedData('b', 'c');

    // Assert
    // The first value is read *after* the second call, which is the only
    // ordering that can catch a buffer the two share.
    expect(toHex(first)).toBe('61');
    expect(toHex(second)).toBe('621f63');
    expect(first.buffer).not.toBe(second.buffer);
  });

  it('returns a fresh array even for fields it has already been handed', () => {
    // Arrange
    // The case above calls with *different* fields, so what it catches is a
    // shared module buffer and nothing else. A cache keyed on the fields —
    // handing back the same instance for the same input — is green on it and on
    // every other case in this file.
    //
    // What that costs is specific rather than theoretical. Zero-filling a
    // buffer once it has been used is ordinary practice in this folder:
    // `key-envelope.ts` wipes the plaintext copy it hands the cipher, and
    // `account-keys.ts` wipes the key material it imports. One such wipe over a
    // cached answer, and the next caller asking for the same fields is handed
    // zeros, seals under them, and nothing anywhere reddens.

    // Act
    const first = buildAssociatedData('a', 'b');
    const second = buildAssociatedData('a', 'b');

    // Assert
    // Equal bytes, and neither the array nor the buffer shared.
    expect(toHex(first)).toBe(toHex(second));
    expect(first).not.toBe(second);
    expect(first.buffer).not.toBe(second.buffer);

    // What follows guards nothing, and is here to be read rather than to catch:
    // it acts out the consequence the two assertions above are for. It cannot
    // fail on its own — for a wipe to reach the next answer the two calls must
    // share memory, and sharing memory means either the same array or the same
    // buffer, both of which redden a line above before this one runs. Measured,
    // not reasoned: under a cache handing back the same instance the `not.toBe`
    // goes first, and under a cache handing back a fresh view over one buffer
    // the `first.buffer` line does.
    first.fill(0);

    expect(toHex(buildAssociatedData('a', 'b'))).toBe('611f62');
  });
});
