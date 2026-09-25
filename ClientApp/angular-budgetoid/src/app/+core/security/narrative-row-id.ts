// The identifier of a row whose narrative fields are encrypted, minted on the
// client because there is no other moment it could be minted in.
//
// ADR 0022 is the decision and states the context: a narrative field is sealed
// under the account's content key and bound to where it lives — this table, this
// column, **this row** — and associated data is rebuilt from where a ciphertext
// was found rather than carried inside it. On an insert the sealing happens
// before the row exists, so a server-minted id arrives too late. Dropping the row
// from the grammar is the other way out and is not available: without it every
// row in a column is interchangeable with every other, and a shuffle is a silent,
// successful decryption.
//
// **A new function rather than a second caller of `mintFactorId`, and the
// difference is scale rather than taste.** `factor-id.ts` mints with
// `crypto.randomUUID`, which is version 4, and that is right *there*: a factor id
// is a primary key with an index, but an account holds eleven factors in its
// whole life, so insert locality is a property nobody can measure on it. An
// account holds thousands of transactions, payees and categories. The same
// trade-off, weighed at two scales, lands on two answers — so the two minters sit
// beside each other, agreeing on the spelling and disagreeing on the version, and
// neither is a mistake to correct into the other.
//
// **Nothing anywhere can tell the two versions apart, so this half is held by
// review.** ADR 0022 says so outright, and the list is worth repeating because a
// reader will go looking for the check: no column type, check constraint or
// policy can see a version nibble; `isCanonicalFactorId` deliberately inspects
// neither the version nor the variant, and argues for that at its own
// declaration; and the server's canonical parse compares a *spelling*. A minter
// reaching for `crypto.randomUUID()` here would satisfy every one of them and
// quietly give up the only property this function exists for.
//
// **Nothing calls it yet.** No route accepts a client-supplied row id and no path
// seals a narrative field for a row it just created. ADR 0022's scope section
// says to read the decision as the rule those changes will be built to.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so a function is the whole of it. In particular
// there is no counter and no last-timestamp field — see the note on monotonicity
// below.

// RFC 9562's layout, named rather than spelled inline at the four places it is
// read: sixteen bytes, the first six of them the timestamp, the version nibble
// in byte 6 and the variant bits in byte 8.
const UUID_BYTES = 16;
const TIMESTAMP_BYTES = 6;
const VERSION_BYTE = 6;
const VARIANT_BYTE = 8;

/**
 * Mints a version-7 UUID in the canonical lower-case 36-character hyphenated
 * spelling, and nothing else.
 *
 * **The layout is RFC 9562's and every field of it is load-bearing.** Sixteen
 * bytes: the 48-bit Unix millisecond timestamp in the first six, **big-endian**;
 * `0x7` in the high nibble of byte 6; `0b10` in the top two bits of byte 8; 74
 * random bits in what is left. Rendered as lower-case hex in the 8-4-4-4-12
 * grouping.
 *
 * **Big-endian is the one detail with no symptom when it is wrong.** A
 * little-endian timestamp still produces a well-formed, unique, canonically
 * spelled UUID that passes every predicate this client and this server own — and
 * it scatters across the index exactly like a version-4 draw, which is the single
 * property the whole choice of version 7 was made for. There is no test the
 * database can offer for it and no error anybody will ever see; only a spec that
 * mints two ids a millisecond apart and compares them as byte strings can catch
 * it.
 *
 * **`crypto.getRandomValues` and nowhere else.** `crypto.randomUUID` is the
 * obvious shortcut and it mints version 4 only, which is the thing this function
 * exists not to do. `Math.random` is the other and is refused for the reason
 * `factor-id.ts`, `account-keys.ts` and `recovery-codes.ts` each state about
 * their own draws: it passes every shape-based check while being seeded from a
 * value the page does not control and shared with every other caller on it. A row
 * id is not a secret, but it is a primary key that has to be unique across an
 * account, and a predictable draw is a collision waiting for two clients to start
 * at the same seed.
 *
 * The draw fills **the whole sixteen bytes** before the timestamp, version and
 * variant are written over it. Nothing is left at zero on any path, so a field
 * this function forgot to set would still be random rather than a constant — the
 * cheap version of the argument, and the reason the order is fill-then-overwrite
 * rather than the other way round.
 *
 * **No monotonic counter, and the omission is a decision.** RFC 9562 offers
 * several ways to keep ids minted inside one millisecond in order, and every one
 * of them needs state — a remembered timestamp, a sequence — carried across
 * calls. What that state would buy is ordering *within* a millisecond, which
 * nothing in this product asks for: the locality that version 7 is here for is at
 * the millisecond scale, and uniqueness rests on 74 random bits rather than on
 * the clock. What it would cost is a module with a lifetime, in a file whose
 * whole shape is that it has none. A clock stepping backwards therefore produces
 * an out-of-order id; it costs locality on a handful of rows and nothing else.
 *
 * **The spelling is the same contract `mintFactorId` keeps**, and for the same
 * consequence when it slips: the value is what the associated data was built
 * from, so a client that seals under one spelling and rebuilds another finds its
 * own ciphertext unopenable — permanently, in both directions, with no error
 * anywhere naming the cause. Lower-case hex, hyphens in the four places, no
 * surrounding whitespace, no braces. That much *is* checkable: the output has to
 * satisfy `isCanonicalFactorId`, which `narrative-cipher.ts` imports under a
 * row-id alias to refuse a binding, so a spelling that slipped would be refused
 * at the seal by the module that consumes this value.
 */
export function mintNarrativeRowId(): string {
  // Fill first, overwrite after. Every one of the sixteen bytes is drawn before
  // any fixed field is written over it, so a field this function forgot to set
  // is still random rather than a constant zero.
  const bytes = new Uint8Array(UUID_BYTES);
  crypto.getRandomValues(bytes);

  const milliseconds = Date.now();

  // **Big-endian: the most significant byte of the millisecond goes at index 0.**
  // Writing it the other way round produces a well-formed, unique, canonically
  // spelled version-7 UUID that scatters across the index exactly like a
  // version-4 draw — the one property the whole choice of version 7 was made for
  // — with no error and no predicate anywhere able to see it.
  //
  // Divided rather than shifted. A millisecond since the epoch is past 2^40, and
  // JavaScript's bitwise operators coerce to 32 bits: `milliseconds >>> 8` is a
  // different number from `milliseconds / 256` for every clock this will ever
  // run under, and the difference is a silently wrong timestamp rather than an
  // error.
  for (let index = 0; index < TIMESTAMP_BYTES; index += 1) {
    const shift = TIMESTAMP_BYTES - 1 - index;

    bytes[index] = Math.floor(milliseconds / 256 ** shift) % 256;
  }

  // `0x7` in the high nibble of byte 6, and `0b10` in the top two bits of byte 8
  // — RFC 9562's version and variant. The low bits of both bytes stay as drawn,
  // which is why the version nibble is a constant and the variant nibble is one
  // of four.
  bytes[VERSION_BYTE] = (bytes[VERSION_BYTE] & 0x0f) | 0x70;
  bytes[VARIANT_BYTE] = (bytes[VARIANT_BYTE] & 0x3f) | 0x80;

  const hex = Array.from(bytes, (byte) =>
    byte.toString(16).padStart(2, '0'),
  ).join('');

  // Lower-case hex — `toString(16)` emits it and nothing here undoes that — in
  // the 8-4-4-4-12 grouping, no braces and no surrounding whitespace. The output
  // has to satisfy `isCanonicalFactorId`, which `narrative-cipher.ts` imports
  // under a row-id alias to refuse a binding, so a spelling that slipped is
  // refused at the seal by the module that consumes this value.
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join('-');
}
