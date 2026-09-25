// The identifier of a row whose narrative fields are encrypted, minted on the
// client because there is no other moment it could be minted in.
//
// **The spelling is checked through `isCanonicalFactorId`, imported, and never
// through a regular expression written here.** That predicate is what
// `narrative-cipher.ts` refuses a binding with, under a row-id alias, so it is
// the definition this value has to satisfy — a second pattern in this file would
// be a second definition of "canonical", and the copy that drifted would go on
// passing its own spec while the module it is supposed to feed refused the
// output.
//
// **Two of the cases below exist because nothing else in this product can see
// what they check.** ADR 0022 says the version-4-versus-7 choice is held by
// review, and that sentence is about *stored* ids: no column type, check
// constraint or policy can see a version nibble, and `isCanonicalFactorId`
// deliberately inspects neither the version nor the variant. It is not a
// statement about what the minter emits, which a spec can hold exactly — so
// character 14 is pinned here, and a `crypto.randomUUID` shortcut is refused by
// name.
//
// **The endianness case is the one with no other symptom at all.** A
// little-endian timestamp still produces a well-formed, unique, canonically
// spelled, version-7 UUID that passes every other case in this file and every
// predicate the client and the server own. What it loses is index locality,
// which is the single property the whole choice of version 7 was made for, and
// which nothing anywhere measures. Two stubbed clocks and fixed bytes are the
// only way to see it: **fixed** because a random tail lets a wrong
// implementation pass on roughly half of all draws, which is a flake that reads
// as an unrelated failure.
import { afterEach, describe, expect, it, vi } from 'vitest';

import { isCanonicalFactorId } from './factor-id';
import { mintNarrativeRowId } from './narrative-row-id';

// Enough draws that a per-draw property holding by accident is not credible, and
// small enough that the file stays under a second.
const MANY = 500;

// The canonical spelling puts the version nibble at index 14 and the variant
// nibble at index 19 of `xxxxxxxx-xxxx-Mxxx-Nxxx-xxxxxxxxxxxx`. Written as
// offsets rather than as a pattern, because a pattern here would be the second
// definition of the spelling this file refuses to keep.
const VERSION_INDEX = 14;
const VARIANT_INDEX = 19;

// The 48-bit timestamp occupies the first six bytes, which is the first twelve
// hex characters — the first group of eight and the whole of the second group.
const TIMESTAMP_HEAD = 8;
const TIMESTAMP_TAIL_START = 9;
const TIMESTAMP_TAIL_END = 13;

function timestampHexOf(id: string): string {
  return `${id.slice(0, TIMESTAMP_HEAD)}${id.slice(
    TIMESTAMP_TAIL_START,
    TIMESTAMP_TAIL_END,
  )}`;
}

// Fills whatever the platform was asked to fill with one repeated byte, so the
// random tail of an id is a value this file chose. The view is rebuilt over the
// caller's buffer rather than assumed to be a `Uint8Array`, matching the shape
// `account-keys.spec.ts` uses at the same boundary.
function stubRandomBytesWith(fill: number): void {
  vi.spyOn(crypto, 'getRandomValues').mockImplementation(
    <T extends ArrayBufferView | null>(buffer: T): T => {
      const bytes = new Uint8Array(
        (buffer as ArrayBufferView).buffer,
        (buffer as ArrayBufferView).byteOffset,
        (buffer as ArrayBufferView).byteLength,
      );
      bytes.fill(fill);

      return buffer;
    },
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// The spelling.

describe('mintNarrativeRowId', () => {
  it('mints the canonical spelling every time, by the imported predicate', () => {
    // Arrange
    // The value is what the associated data of every narrative field on the row
    // is built from, so a spelling that slipped makes the row's own ciphertext
    // unopenable — permanently, in both directions, with no error anywhere
    // naming the cause.

    // Act
    const ids = Array.from({ length: MANY }, () => mintNarrativeRowId());

    // Assert
    expect(ids.every((id) => isCanonicalFactorId(id))).toBe(true);
    expect(ids.every((id) => id.length === 36)).toBe(true);
    expect(ids.every((id) => id === id.toLowerCase())).toBe(true);
  });

  it('draws distinct ids', () => {
    // Arrange
    // Uniqueness rests on 74 random bits rather than on the clock, which is why
    // the absence of a monotonic counter costs nothing here. A minter that
    // repeated itself would collide on a primary key.

    // Act
    const ids = Array.from({ length: MANY }, () => mintNarrativeRowId());

    // Assert
    expect(new Set(ids).size).toBe(MANY);
  });
});

// ---------------------------------------------------------------------------
// The version and variant nibbles.

describe('the version and variant fields', () => {
  it('writes 7 at character fourteen, which nothing else in the product sees', () => {
    // Arrange
    // **This is the case that separates the minter from `crypto.randomUUID`.**
    // ADR 0022 records that the choice is held by review, and that sentence is
    // about ids already stored — no column type, check constraint or policy can
    // read a version nibble, and `isCanonicalFactorId` inspects neither the
    // version nor the variant, on purpose and with an argument at its own
    // declaration. A minter reaching for `crypto.randomUUID()` would satisfy
    // every one of those and quietly give up the only property this function
    // exists for. What the minter emits, though, is checkable, and this is the
    // check.

    // Act
    const versions = new Set(
      Array.from({ length: MANY }, () => mintNarrativeRowId()[VERSION_INDEX]),
    );

    // Assert
    expect([...versions]).toEqual(['7']);
  });

  it('writes the RFC 9562 variant at character nineteen', () => {
    // Arrange
    // The top two bits of byte 8 are `0b10`, which renders as one of four hex
    // digits. All four are legal, so the assertion is membership rather than a
    // value; an implementation that left the byte as drawn would produce the
    // other twelve.
    const legal = new Set(['8', '9', 'a', 'b']);

    // Act
    const variants = Array.from(
      { length: MANY },
      () => mintNarrativeRowId()[VARIANT_INDEX],
    );

    // Assert
    expect(variants.every((digit) => legal.has(digit))).toBe(true);
    // And every one of the four is reachable, so a constant would not pass.
    expect(new Set(variants).size).toBeGreaterThan(1);
  });
});

// ---------------------------------------------------------------------------
// The timestamp, and the endianness nobody else can see.

describe('the timestamp', () => {
  it('renders the millisecond big-endian in the first twelve characters', () => {
    // Arrange
    // A clock chosen so every one of the six bytes differs from every other:
    // 0x0123456789AB. A byte order that was reversed, rotated or swapped in
    // pairs all produce different strings from this one, where a round number
    // would let several of them agree.
    const clock = 0x0123456789ab;
    vi.spyOn(Date, 'now').mockReturnValue(clock);
    stubRandomBytesWith(0x00);

    // Act
    const id = mintNarrativeRowId();

    // Assert
    expect(timestampHexOf(id)).toBe('0123456789ab');
    // The little-endian rendering of the same millisecond, named so the case
    // says what it is here to catch rather than only what it expects.
    expect(timestampHexOf(id)).not.toBe('ab8967452301');
    expect(isCanonicalFactorId(id)).toBe(true);
  });

  it('sorts an earlier millisecond before a later one under worst-case bytes', () => {
    // Arrange
    // **255 and 256, because 1 and 2 would not separate anything.** A
    // little-endian write of 1 and 2 still sorts correctly — the difference
    // lands in the low byte either way. 255 crosses a byte boundary: big-endian
    // gives `0000000000ff` and `000000000100`, which sort in clock order;
    // little-endian gives `ff0000000000` and `000100000000`, which sort
    // backwards.
    //
    // **Fixed bytes, not a draw.** The earlier id gets an all-ones tail and the
    // later one an all-zeros tail, which is the worst case for the property: if
    // ordering came from the random bits rather than from the clock, this is the
    // pair that exposes it. With a real generator a wrong implementation passes
    // about half the time.
    const earlierClock = 255;
    const laterClock = 256;

    const clock = vi.spyOn(Date, 'now').mockReturnValue(earlierClock);
    stubRandomBytesWith(0xff);
    const earlier = mintNarrativeRowId();

    clock.mockReturnValue(laterClock);
    stubRandomBytesWith(0x00);
    const later = mintNarrativeRowId();

    // Act
    const sorted = [later, earlier].sort();

    // Assert
    expect(sorted).toEqual([earlier, later]);
    expect(timestampHexOf(earlier)).toBe('0000000000ff');
    expect(timestampHexOf(later)).toBe('000000000100');
    expect(isCanonicalFactorId(earlier)).toBe(true);
    expect(isCanonicalFactorId(later)).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// Where the randomness comes from.

describe('the source of the random bits', () => {
  it('draws from crypto.getRandomValues and from nothing else', () => {
    // Arrange
    // `Math.random` passes every shape assertion in this file while being seeded
    // from a value the page does not control and shared with every other caller
    // on it. A row id is not a secret, but it is a primary key that has to be
    // unique across an account, and a predictable draw is a collision waiting
    // for two clients to start at the same seed.
    //
    // `crypto.randomUUID` is the other shortcut, and it is refused for a
    // different reason: it mints version 4 only, which is the one thing this
    // function exists not to do. The version case above would catch it; this
    // names it, so the failure says which mistake was made.
    const insecure = vi.spyOn(Math, 'random');
    const versionFour = vi.spyOn(crypto, 'randomUUID');
    const secure = vi.spyOn(crypto, 'getRandomValues');

    // Act
    const id = mintNarrativeRowId();

    // Assert
    expect(secure).toHaveBeenCalled();
    expect(insecure).not.toHaveBeenCalled();
    expect(versionFour).not.toHaveBeenCalled();
    expect(isCanonicalFactorId(id)).toBe(true);
  });

  it('fills the whole sixteen bytes before the fixed fields are written over', () => {
    // Arrange
    // The order is fill-then-overwrite, so a field the function forgot to set is
    // still random rather than a constant. With every drawn byte at zero the
    // only non-zero characters an id can carry are the ones written afterwards —
    // the timestamp, the version and the variant — so the tail after the variant
    // group is all zeros exactly when the draw covered it.
    vi.spyOn(Date, 'now').mockReturnValue(0);
    stubRandomBytesWith(0x00);

    // Act
    const zeroed = mintNarrativeRowId();

    // Assert
    expect(zeroed).toBe('00000000-0000-7000-8000-000000000000');

    // And with every drawn byte at 0xff, everything the fixed fields do not own
    // is f — which is what says the draw reached all sixteen bytes rather than
    // only the last ten.
    stubRandomBytesWith(0xff);
    expect(mintNarrativeRowId()).toBe('00000000-0000-7fff-bfff-ffffffffffff');
  });
});
