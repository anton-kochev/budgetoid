// The wire format of every secret this client derives, in one place. Until now
// `recovery-codes.ts` carried a private encoder and nothing carried a decoder at
// all — `recovery-codes.spec.ts` had to grow its own `decodeBase64Url` helper
// just to measure a verifier's width. That is why this module exists: the
// account's wrapped keys arrive as an envelope the client has to *read*, so the
// decoding half stops being a test fixture and becomes shipped code.
//
// The assertions below are about refusal at least as much as about arithmetic.
// A base64url decoder has an obvious lenient reading — strip the padding it was
// not supposed to get, accept `+` and `/` because they are "the same bits", skip
// a character it does not recognise — and every one of those is wrong here for
// the same reason. These bytes are key material and an envelope, and the peer
// that reads them is a different decoder in a different language. A lenient
// decoder accepts a wrapped key the server's own decoder rejects, and the
// symptom arrives later, on somebody else's request, as a key that cannot be
// unwrapped rather than as an input that was malformed. Refusal means it throws.
//
// One pin here is not about this module at all: the encoding must stay
// byte-for-byte what `recovery-codes.ts` already produces. Verifiers have a
// frozen golden vector because a change to any of them silently invalidates
// every code every account already holds; extracting the encoder is exactly the
// kind of change that could move one.
import { describe, expect, it } from 'vitest';

import { decodeBase64Url, encodeBase64Url } from './base64url';
import {
  RECOVERY_CODE_VERIFIER_BYTES,
  recoveryCodeVerifier,
} from './recovery-codes';

// Every byte value exactly once. Long enough that its encoding uses all
// sixty-four symbols of the alphabet, which is what makes the round-trip below
// a statement about the whole mapping rather than about the ASCII range.
const EVERY_BYTE_VALUE = Uint8Array.from(Array(256).keys());

// Three bytes chosen so the four six-bit groups they split into are 62, 0, 3
// and 63 — the last two symbols of the alphabet are the ones the URL-safe
// variant renames, so this input is the only kind that can tell the two
// alphabets apart. `0xf8 >> 2` is 62 and `0xff & 63` is 63; standard base64
// spells those `+` and `/`.
const QUADRANT_FOUR_BYTES = new Uint8Array([0xf8, 0x00, 0xff]);

// The golden pair frozen in `recovery-codes.spec.ts`, restated rather than
// imported because the two constants are deliberately private to that file. It
// is not trusted as a copy: the test that uses it re-derives the verifier
// through the shipped `recoveryCodeVerifier` first, so a stale restatement here
// fails instead of quietly pinning nothing.
const GOLDEN_CODE = '0123456789ABCDEFGHJKMNPQRS';
const GOLDEN_VERIFIER = 'aGr2Qher4pOLKZ335Uwyi92Ti9GAZ-Ek5G_9Br5plNE';

// The URL-safe alphabet in index order — symbol `n` is the encoding of the
// six-bit value `n`. Written out rather than derived through the encoder,
// because the trailing-group tests below are statements about which six-bit
// values a final group may carry: symbols taken from the module under test would
// let a wrong mapping define its own expectation.
const BASE64URL_ALPHABET =
  'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

// Standard base64, computed here rather than taken from the module under test,
// so the claim "this input demands the URL-safe symbols" is checked instead of
// assumed.
function standardBase64(bytes: Uint8Array): string {
  let binary = '';

  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary);
}

// What one decode did, kept as a value so a whole family of inputs can be driven
// in the Act phase and judged together in the Assert phase. A refusal that lost
// its message would leave the trailing-group tests unable to say *which* check
// turned an input down, which is the entire difference they are measuring.
type DecodeOutcome =
  | {
      readonly text: string;
      readonly status: 'decoded';
      readonly bytes: Uint8Array;
    }
  | {
      readonly text: string;
      readonly status: 'refused';
      readonly message: string;
    };

function attemptDecode(text: string): DecodeOutcome {
  try {
    return { text, status: 'decoded', bytes: decodeBase64Url(text) };
  } catch (error: unknown) {
    return {
      text,
      status: 'refused',
      message: error instanceof Error ? error.message : String(error),
    };
  }
}

function textsRefusedIn(outcomes: readonly DecodeOutcome[]): readonly string[] {
  return outcomes
    .filter((outcome) => outcome.status === 'refused')
    .map((outcome) => outcome.text);
}

describe('the base64url encoder', () => {
  it('round-trips every byte value, at every length the encoding treats differently', () => {
    // Arrange
    // Base64 works in groups of three bytes, so a length is only interesting
    // modulo three: the final group is whole, one byte short, or two bytes
    // short, and those three cases are where an encoder loses a byte or a
    // decoder invents one. 255, 256 and 254 cover the three remainders over the
    // full byte range; the short lengths cover the same three where there is no
    // whole group in front of them to hide a mistake.
    const lengths = [0, 1, 2, 3, 4, 5, 254, 255, 256];

    // Act
    const restored = lengths.map((length) =>
      decodeBase64Url(encodeBase64Url(EVERY_BYTE_VALUE.slice(0, length))),
    );

    // Assert
    for (const [index, length] of lengths.entries()) {
      expect(restored[index]).toEqual(EVERY_BYTE_VALUE.slice(0, length));
    }

    // And the input was not accidentally narrow. Sixty-four distinct output
    // characters means every symbol of the alphabet was produced and decoded
    // back — the uppercase, lowercase, digit and URL-safe quadrants all — so a
    // mapping that is wrong for one quadrant cannot pass by never being asked.
    expect(new Set(encodeBase64Url(EVERY_BYTE_VALUE)).size).toBe(64);
  });

  it('emits no padding, whatever the last group leaves over', () => {
    // Arrange
    // Padding exists to make a base64 string a multiple of four characters. It
    // carries no information, the server's decoder is written to reject it
    // rather than tolerate it, and it is `=` — a character that has to be
    // escaped in a query string and a form body both.
    const lengths = [0, 1, 2, 3, 4, 5, 6, 32, 255, 256];

    // Act
    const encoded = lengths.map((length) =>
      encodeBase64Url(EVERY_BYTE_VALUE.slice(0, length)),
    );

    // Assert
    for (const [index, length] of lengths.entries()) {
      expect(encoded[index]).not.toContain('=');

      // Four characters per three bytes, rounded up — which is the same as
      // saying the padding was left off rather than trimmed to a fixed width.
      expect(encoded[index]).toHaveLength(Math.ceil((length * 4) / 3));
    }
  });

  it('spells the last two symbols URL-safely where the input demands them', () => {
    // Arrange
    // Asserting "no `+` and no `/`" over arbitrary bytes is a decoration: most
    // inputs never reach either symbol, so the assertion passes on an encoder
    // that does no substitution at all. This input reaches both.
    const demanding = QUADRANT_FOUR_BYTES;

    // Act
    const encoded = encodeBase64Url(demanding);

    // Assert
    // The control first: these bytes really do produce both of the characters
    // the URL-safe alphabet renames.
    expect(standardBase64(demanding)).toBe('+AD/');

    expect(encoded).toBe('-AD_');
    expect(encoded).toContain('-');
    expect(encoded).toContain('_');
    expect(encoded).not.toContain('+');
    expect(encoded).not.toContain('/');

    // And over the whole byte range, where both symbols occur many times.
    const sweep = encodeBase64Url(EVERY_BYTE_VALUE);
    expect(sweep).toMatch(/^[A-Za-z0-9_-]+$/);
  });

  it('encodes the empty array to the empty string, and decodes it back', () => {
    // Arrange
    const nothing = new Uint8Array(0);

    // Act
    const encoded = encodeBase64Url(nothing);
    const decoded = decodeBase64Url('');

    // Assert
    // Zero bytes is a legitimate value, not an error: an envelope with an empty
    // associated-data field encodes to nothing and has to decode back to
    // nothing. The pair is stated in both directions because an encoder that
    // returns `''` and a decoder that refuses `''` are individually defensible
    // and jointly a value that cannot survive a round trip.
    expect(encoded).toBe('');
    expect(decoded).toHaveLength(0);
    expect(decoded).toEqual(nothing);
  });
});

describe('the base64url decoder', () => {
  it('refuses padded input instead of stripping the padding', () => {
    // Arrange
    // `AA==` is a perfectly well-formed *standard* base64 encoding of one byte,
    // which is exactly what makes it dangerous: a decoder that strips `=` will
    // read it, so the malformed value travels on and is refused by the peer
    // that receives whatever was built from it.
    const padded = 'AA==';

    // Act
    const decoding = (): Uint8Array => decodeBase64Url(padded);

    // Assert
    expect(decoding).toThrow();
  });

  it('refuses the standard alphabet in place of the URL-safe one', () => {
    // Arrange
    // Both directions of the substitution, each on an input that is otherwise a
    // valid encoding — `+AD/` is the standard spelling of the same three bytes
    // as `-AD_`. Accepting it would mean this decoder reads a dialect the
    // encoder above never emits and the server's decoder rejects.
    const withPlus = '+AD_';
    const withSolidus = '-AD/';

    // Act
    const decodingPlus = (): Uint8Array => decodeBase64Url(withPlus);
    const decodingSolidus = (): Uint8Array => decodeBase64Url(withSolidus);

    // Assert
    expect(decodingPlus).toThrow();
    expect(decodingSolidus).toThrow();
  });

  it('refuses a character that is in no base64 alphabet at all', () => {
    // Arrange
    // A stray space from a copy-paste, and a character no encoding produces.
    // Skipping either is the repair with the widest blast radius: it shortens
    // the decoded output silently, so an envelope keeps its shape and loses a
    // byte.
    const withSpace = 'AA A';
    const withPunctuation = 'AA.A';

    // Act
    const decodingSpace = (): Uint8Array => decodeBase64Url(withSpace);
    const decodingPunctuation = (): Uint8Array =>
      decodeBase64Url(withPunctuation);

    // Assert
    expect(decodingSpace).toThrow();
    expect(decodingPunctuation).toThrow();
  });

  it('refuses a length that no base64url encoding can have', () => {
    // Arrange
    // Unpadded base64url has a length of 0, 2, 3 or 0 modulo four — never 1. A
    // single leftover character carries six bits, which is not a byte and not
    // nothing, so a string of length 1 modulo 4 is truncated rather than
    // ambiguous. Inventing a byte for it, or dropping it, both turn a
    // transmission error into a plausible-looking key.
    const oneCharacter = 'A';
    const fiveCharacters = 'AAAAA';

    // Act
    const decodingOne = (): Uint8Array => decodeBase64Url(oneCharacter);
    const decodingFive = (): Uint8Array => decodeBase64Url(fiveCharacters);

    // Assert
    expect(decodingOne).toThrow();
    expect(decodingFive).toThrow();
  });

  it('refuses a two-character final group carrying bits no encoder would set', () => {
    // Arrange
    // Two characters carry twelve bits and stand for one byte, so the low four
    // bits of the second symbol are surplus — and `atob` discards them instead
    // of refusing them. `AA` and `AB` therefore both come back as a single zero
    // byte, and only `AA` is an encoding of it. That is the quietest repair of
    // all the ones this decoder is written to refuse: nothing about the input
    // looks wrong, the output is the right width, and what comes out is a
    // plausible key.
    //
    // Every symbol of the alphabet is driven rather than one example, so this is
    // the whole rule: the four symbols whose value is a multiple of sixteen are
    // the only ones with those four bits clear.
    const groups = [...BASE64URL_ALPHABET].map((symbol) => `A${symbol}`);
    const canonical = groups.filter(
      (group, sixBitValue) => sixBitValue % 16 === 0,
    );
    const nonCanonical = groups.filter(
      (group, sixBitValue) => sixBitValue % 16 !== 0,
    );

    // Act
    const outcomes = groups.map(attemptDecode);

    // Assert
    // Set equality in both directions: every non-canonical group is refused, and
    // no canonical one is. A decoder that threw on all sixty-four would satisfy
    // the first half alone while refusing values the encoder actually emits.
    expect(textsRefusedIn(outcomes)).toEqual(nonCanonical);
    expect(nonCanonical).toHaveLength(60);
    expect(canonical).toEqual(['AA', 'AQ', 'Ag', 'Aw']);

    // Refused by the canonicality check and not by an earlier one. Both other
    // checks would also throw here, and a length or alphabet rule that started
    // catching these would be catching them for a reason that is not true of
    // them — so the message is what says which rule fired.
    for (const outcome of outcomes.filter(
      (entry) => entry.status === 'refused',
    )) {
      expect(outcome.message).toMatch(/final group/);
    }

    // And the accepted four really are one byte that re-encodes to itself, which
    // is the identity this decoder's contract claims over its whole accepted set.
    for (const group of canonical) {
      const bytes = decodeBase64Url(group);

      expect(bytes).toHaveLength(1);
      expect(encodeBase64Url(bytes)).toBe(group);
    }
  });

  it('refuses a three-character final group carrying bits no encoder would set', () => {
    // Arrange
    // The other short group, and it has to be stated separately because the
    // surplus is a different width: three characters carry eighteen bits and
    // stand for two bytes, so only the low *two* bits of the third symbol are
    // over. A decoder that masked the two-character case with a hard-coded
    // four-bit rule passes the test above and waves every input here through.
    const groups = [...BASE64URL_ALPHABET].map((symbol) => `AA${symbol}`);
    const canonical = groups.filter(
      (group, sixBitValue) => sixBitValue % 4 === 0,
    );
    const nonCanonical = groups.filter(
      (group, sixBitValue) => sixBitValue % 4 !== 0,
    );

    // Act
    const outcomes = groups.map(attemptDecode);

    // Assert
    expect(textsRefusedIn(outcomes)).toEqual(nonCanonical);
    expect(nonCanonical).toHaveLength(48);
    expect(canonical).toHaveLength(16);

    for (const outcome of outcomes.filter(
      (entry) => entry.status === 'refused',
    )) {
      expect(outcome.message).toMatch(/final group/);
    }

    for (const group of canonical) {
      const bytes = decodeBase64Url(group);

      expect(bytes).toHaveLength(2);
      expect(encodeBase64Url(bytes)).toBe(group);
    }
  });
});

describe('the encoding a recovery code verifier already crosses the wire in', () => {
  it('is byte for byte the encoding this module produces', async () => {
    // Arrange
    // The pin that keeps the extraction from moving an already-issued verifier.
    // `recovery-codes.ts` had its own private encoder, and its golden vector is
    // frozen because a changed encoding is silent — verifiers of the right
    // shape that match no row, refused with a 401 nobody can tell from a typo.
    // If this goes red the question is never "what is the new value".
    const verifier = await recoveryCodeVerifier(GOLDEN_CODE);

    // Act
    const reencoded = encodeBase64Url(decodeBase64Url(verifier));

    // Assert
    // The restated constant is checked against the shipped module first, so
    // this file cannot pin a value that has already drifted.
    expect(verifier).toBe(GOLDEN_VERIFIER);

    // Then the round trip through the new pair is the identity on it. This
    // catches a padding strip, an alphabet swap and a byte order flip in one
    // assertion, because any of them changes the string.
    expect(reencoded).toBe(GOLDEN_VERIFIER);
    expect(encodeBase64Url(decodeBase64Url(GOLDEN_VERIFIER))).toBe(
      GOLDEN_VERIFIER,
    );

    // And the decoder reads the width the server refuses anything but, from
    // both sides — recomputed from the shipped constant rather than restated.
    expect(decodeBase64Url(GOLDEN_VERIFIER)).toHaveLength(
      RECOVERY_CODE_VERIFIER_BYTES,
    );
  });
});
