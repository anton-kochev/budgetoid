// The wire format of every secret this client derives, in one place, in both
// directions: unpadded base64url.
//
// The encoder is the generalisation of a private helper `recovery-codes.ts`
// already carries, and it has to stay byte-for-byte identical to it. `btoa`
// emits standard base64, so the two substitutions and the padding strip are the
// whole of the difference. Every verifier already issued was derived through
// that encoding and is pinned by a frozen golden vector, because a changed
// encoding is silent: a verifier of the right shape that matches no row,
// refused with a `401` deliberately indistinguishable from a mistyped code.
//
// The decoder is new. Nothing in production decoded base64url before — the
// account's wrapped keys arrive as an envelope this client has to *read*, so
// the decoding half stops being a test fixture and becomes shipped code.
//
// **The decoder refuses; it never repairs.** There is an obvious lenient
// reading of base64url — strip padding it was not supposed to get, accept `+`
// and `/` because they are "the same bits", skip a character it does not
// recognise — and every one of those is wrong here for one reason. These bytes
// are key material and an AEAD envelope, and the peer that reads them is a
// different decoder in a different language, written to reject exactly what a
// lenient reading here would wave through. So a lenient decoder accepts a
// wrapped key the server's own decoder rejects, and the symptom does not arrive
// on the malformed input: it arrives months later, on somebody else's request,
// as a key that will not unwrap. Skipping an unrecognised character is the
// worst of them, because it shortens the output silently — an envelope keeps
// its shape and loses a byte.
//
// That stance is why the decoder does not stop at the checks it can make on the
// text. `atob` has a lenience of its own, quieter than any of the above, and
// the decoder below closes it rather than documenting it; the argument is at
// the check itself.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it. The same
// argument `recovery-codes.ts` makes about a class holding a secret alive past
// the render that showed it applies with equal force to key material.

// The URL-safe alphabet and nothing else. Anchored, and permitting the empty
// string: zero bytes is a legitimate value — an envelope with an empty
// associated-data field — not an error.
//
// `+`, `/` and `=` are refused by omission rather than by a rule of their own,
// which is what makes the refusal total: a character no base64 alphabet
// contains at all is rejected by the same test, so there is no branch a new
// dialect could slip past.
const BASE64URL_PATTERN = /^[A-Za-z0-9_-]*$/;

const BASE64_GROUP_LENGTH = 4;

/**
 * Encodes bytes as unpadded base64url.
 *
 * Byte-for-byte what a recovery code verifier already crosses the wire as. That
 * is a constraint on this function, not an observation about it: the encoding is
 * part of the definition of every verifier the server holds a hash of, so a
 * change here invalidates codes people have already written down and printed.
 */
export function encodeBase64Url(bytes: Uint8Array): string {
  let binary = '';

  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary)
    .replaceAll('+', '-')
    .replaceAll('/', '_')
    .replaceAll('=', '');
}

/**
 * Decodes unpadded base64url, or throws.
 *
 * Throws on padding, on the standard alphabet's `+` and `/`, on any character
 * outside `A–Z a–z 0–9 - _`, on a length that no base64url encoding can have,
 * and on a final group carrying bits no encoder would have set. It repairs
 * nothing — see the note at the top of this file for why a lenient decoder is
 * the expensive kind of wrong here.
 *
 * Every string this accepts is exactly {@link encodeBase64Url} applied to the
 * bytes it returns, which is the strongest form that refusal can take: the
 * round trip is the identity on the whole accepted set, not only on the part of
 * it that happens to be well formed.
 */
export function decodeBase64Url(text: string): Uint8Array {
  // The two checks below are what produce a legible message; the canonicality
  // check further down would catch an alphabet swap on its own, but only by
  // reporting it as the wrong thing. The length check has to come first for a
  // harder reason: `atob` is handed a padded string, and a group of one
  // character padded to four is not something it can be asked about at all.
  if (!BASE64URL_PATTERN.test(text)) {
    throw new Error(
      'Not base64url: the input holds a character outside the URL-safe alphabet.',
    );
  }

  // Unpadded base64url has a length of 0, 2, 3 or 0 modulo four, never 1. A
  // single leftover character carries six bits — not a byte and not nothing —
  // so such a string is truncated rather than ambiguous. Both available repairs
  // (invent the missing bits, or drop the character) turn a transmission error
  // into a plausible-looking key.
  if (text.length % BASE64_GROUP_LENGTH === 1) {
    throw new Error(
      'Not base64url: the input has a length no base64url encoding can have.',
    );
  }

  // `atob` wants the standard alphabet and wants the padding the format above
  // deliberately does not carry, so both are put back here — the inverse of the
  // encoder, applied to input the two checks above have already accepted.
  const standard = text.replaceAll('-', '+').replaceAll('_', '/');
  const padded = standard.padEnd(
    Math.ceil(standard.length / BASE64_GROUP_LENGTH) * BASE64_GROUP_LENGTH,
    '=',
  );
  const bytes = Uint8Array.from(atob(padded), (character) =>
    character.charCodeAt(0),
  );

  // `atob` is forgiving in the one place this must not be. A final group of two
  // or three characters carries four or two bits more than the bytes it stands
  // for, and `atob` discards them instead of refusing them — so `AA` and `AB`
  // both decode to a single zero byte, and only one of them is an encoding of
  // it. That is a repair, quieter than any of the ones named above and of the
  // same kind: an input no encoder could have produced is accepted, and what
  // comes out is a plausible key.
  //
  // Re-encoding and comparing closes it by defining acceptance as "is what the
  // encoder above would have emitted", which needs no table of trailing-bit
  // masks and cannot be wrong about one. Nothing legible is lost by it: those
  // bits are zero in everything any encoder emits, so a group carrying them is
  // corruption or a hand-written value, never something the peer sent.
  if (encodeBase64Url(bytes) !== text) {
    throw new Error(
      'Not base64url: the input has a final group no encoding would produce.',
    );
  }

  return bytes;
}
