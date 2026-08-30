// The client-minted identifier a recovery factor's two wrapped account-key
// envelopes are bound to, minted here and nowhere else.
//
// A factor is not a credential: one passkey is one factor, and a set of ten
// recovery codes is ten, because each code derives a key-encryption key of its
// own and a person redeems whichever one they still have. So a registration
// draws eleven of these values in one go, and each of them ends up as the
// associated data of the pair of envelopes it names.
//
// **This module mints; it does not fold.** `account-keys.ts` tolerates several
// spellings and folds them, and its comment there explains why that tolerance
// stays. This is the other end of the same rule: the server refuses every
// spelling but one — the lower-case 36-character hyphenated form, no
// surrounding whitespace — and a minter that only ever emits that form leaves
// nothing for anybody downstream to fold. Consistency between the two is not
// duplication; folding is a defence against values arriving from elsewhere,
// while emitting the canonical form is a property of the values this client
// creates.
//
// What the spelling costs when it slips: the value is what both envelopes were
// sealed against, and associated data is re-supplied from where an envelope was
// found rather than carried inside it. A client that binds to one spelling and
// hands back another rebuilds associated data that cannot reproduce either
// seal, so **both** envelopes stop opening — permanently, with no error
// anywhere naming the cause. There is no repair path from there and no
// diagnostic that points at this line.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so two functions are the whole of it.

// The canonical spelling and nothing else: eight, four, four, four and twelve
// lower-case hex digits, anchored.
//
// Deliberately no check of the version or variant nibbles, for the reason
// `account-keys.ts` gives at its own fold: this layer is not the authority on
// which UUIDs the server may mint, and a rule invented here starts refusing
// valid factor ids the day that authority changes its mind.
//
// The anchors are load-bearing and JavaScript's are the strict kind — `$`
// without the `m` flag matches the end of the input, not the position before a
// terminal newline the way it does in some other regular-expression dialects.
// A trailing newline is whitespace the server would refuse, so the predicate
// has to refuse it too.
const CANONICAL_FACTOR_ID =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/**
 * Mints a factor id in the one spelling the server accepts.
 *
 * Randomness comes from `crypto.randomUUID` and from nowhere else. It is
 * specified to draw from a cryptographically secure source, which is the same
 * requirement `account-keys.ts` and `recovery-codes.ts` each state about their
 * own draws: `Math.random` passes every shape-based check — a well-formed
 * identifier of the right length — while being seeded from a value the page
 * does not control and shared with every other caller on it. A factor id is not
 * a secret, but it is the primary key of a row unique across the whole table,
 * so a predictable draw is a cross-account collision waiting for two clients to
 * start at the same seed.
 *
 * `randomUUID` rather than a hand-rolled `getRandomValues` and a hex format for
 * the same reason the fold in `account-keys.ts` prefers a rendering over a
 * pattern: the platform's formatting cannot drift from itself, while a local
 * one has to be maintained against a shape this code does not own.
 *
 * **The `toLowerCase` is not redundant**, though `randomUUID` is specified to
 * emit lower-case hex. It costs one call, and it is the single line standing
 * between this client and the permanent, unnamed lockout above, on a platform
 * contract this module does not own and cannot test on every engine the app
 * will ever run in. A guarantee made by somebody else is worth exactly as much
 * as the cost of not relying on it.
 *
 * `toLowerCase`, never `toLocaleLowerCase`, for the reason `canonicalFactorId`
 * states in `account-keys.ts` at its own fold: the locale-aware form maps `I` to
 * `ı` under a Turkish locale, which would fold one factor id to two different
 * bindings on two phones.
 */
export function mintFactorId(): string {
  return crypto.randomUUID().toLowerCase();
}

/**
 * Answers whether `id` is *exactly* the canonical spelling — not whether it
 * parses as a UUID.
 *
 * This is the client-side twin of the server's ordinal comparison against
 * `parsed.ToString("D")`, and the distinction between the two questions is the
 * whole of it. Upper-case hex, a leading or trailing space, the bare 32-digit
 * form and the brace- and parenthesis-wrapped forms all name the same UUID and
 * are all refused here, because each of them is a different value on the wire
 * and the row hands back only one of them.
 *
 * Two things it deliberately does not answer. It is not a fold: it reports, and
 * a caller that wants a canonical value should mint one rather than repair
 * whatever it was given. And it says nothing about the all-zero UUID, which is
 * canonically spelled and refused by the server for a reason that is not about
 * spelling at all — nothing this module mints can produce it, and a caller
 * validating a value from elsewhere gets that refusal from the write path.
 *
 * **More than the factor-id grammar asks it, so what it admits reaches past this
 * file's subject.** `narrative-cipher.ts` imports it under a row-id name to
 * refuse the row a narrative field is bound to — a value a server rendered
 * rather than one this module minted — and states there why that is one
 * predicate and not two. Relaxing this by a character therefore widens a second
 * grammar that has no say here, and whose envelopes stop opening in both
 * directions when a spelling slips.
 */
export function isCanonicalFactorId(id: string): boolean {
  return CANONICAL_FACTOR_ID.test(id);
}
