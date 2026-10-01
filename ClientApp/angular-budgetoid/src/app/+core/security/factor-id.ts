// The client-minted identifier a recovery factor's two envelopes — its wrapped
// private key, and the account's keys encapsulated to that keypair's public
// half — are bound to, minted here and nowhere else.
//
// A factor is not a credential: one passkey is one factor, and a set of ten
// recovery codes is ten, because each code derives a key-encryption key of its
// own and a person redeems whichever one they still have. So a registration
// draws eleven of these values in one go, and each of them ends up as the
// associated data of the pair of envelopes it names.
//
// **Minting and folding are two ends of one rule, and both ends live here.**
// The server refuses every spelling but one — the lower-case 36-character
// hyphenated form, no surrounding whitespace — so a minter that only ever emits
// that form leaves nothing for anybody downstream to fold. The fold is not
// therefore redundant: it is a defence against values arriving from elsewhere,
// where emitting the canonical form is a property of the values this client
// creates. Two halves of one subject, which is why they are one file.
//
// The fold was written twice before it was written here — privately in
// `account-keys.ts` for the wrapped-key grammar and again privately in
// `factor-keypair.ts` for the keypair grammar — and **nothing failed when two
// copies of one rule drifted**, because each grammar was self-consistent. What
// that costs is spelled out below: one factor's envelopes bound under two
// spellings, and no error anywhere.
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
// configuration and no dependency, so three functions are the whole of it.

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

// The spellings `canonicalFactorId` accepts, captured group by group so the
// answer is rebuilt from the digits rather than patched in place. Two patterns
// and not one with an optional hyphen: a single pattern with `-?` between the
// groups accepts a value hyphenated in some places and not others, which is a
// spelling neither this client nor any server ever produces.
//
// They are separate from `CANONICAL_FACTOR_ID` above, which is anchored at the
// canonical form alone, because the two questions are different — what may be
// folded, and what is already correct — and a single pattern serving both would
// have to answer the looser one.
const HYPHENATED_UUID =
  /^([0-9a-f]{8})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{12})$/;
const BARE_UUID =
  /^([0-9a-f]{8})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{12})$/;

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
 * the reason {@link canonicalFactorId} prefers a rendering over a pattern: the
 * platform's formatting cannot drift from itself, while a local one has to be
 * maintained against a shape this code does not own.
 *
 * **The `toLowerCase` is not redundant**, though `randomUUID` is specified to
 * emit lower-case hex. It costs one call, and it is the single line standing
 * between this client and the permanent, unnamed lockout above, on a platform
 * contract this module does not own and cannot test on every engine the app
 * will ever run in. A guarantee made by somebody else is worth exactly as much
 * as the cost of not relying on it.
 *
 * `toLowerCase`, never `toLocaleLowerCase`, for the reason
 * {@link canonicalFactorId} states at its own fold: the locale-aware form maps
 * `I` to `ı` under a Turkish locale, which would fold one factor id to two
 * different bindings on two phones.
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
 * a caller holding a value that has to *become* canonical wants
 * {@link canonicalFactorId} — a different question, which is why the two are
 * separate functions over separate patterns even though they now share a file.
 * And it says nothing about the all-zero UUID, which is
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

/**
 * Folds `id` to the one spelling every grammar binds under, or throws.
 *
 * **One owner, because two copies of this rule cannot stay agreed.** It was
 * written privately in `account-keys.ts` and again privately in
 * `factor-keypair.ts`; each grammar was self-consistent, so a divergence between
 * them would have reddened nothing while binding one factor's envelopes under
 * two different spellings. Associated data is re-supplied from where an
 * envelope was found rather than carried inside it, so the half that
 * drifted stops opening — permanently, on an account whose only other way in is
 * ten codes shown once.
 *
 * **The hazard is case, not braces.** The client mints these identifiers (ADR
 * 0018 §4), so nothing upstream hands a caller a normalised value and this is the
 * only place one is made. The server keeps a uuid and renders it back lower-case
 * hyphenated, so a client that binds an upper-case rendering seals against
 * associated data nothing will ever rebuild. The brace, parenthesis and bare
 * 32-digit tolerances are the lesser half: today's server refuses each of those
 * with a 400 whatever this folded them to, so they are dead against that
 * contract — they stay because this is the half of the rule the client can hold
 * on its own, before any server has seen the value.
 *
 * `toLowerCase`, never `toLocaleLowerCase`: the locale-aware form maps `I` to
 * `ı` under a Turkish locale, which would fold one factor id to two different
 * bindings on two phones. Note what does *and does not* hold that — no hex digit
 * is case-mapped differently by any locale, so on every input the patterns admit
 * the two spellings agree, and no test can tell them apart. It is a rule about
 * what this line must not become, kept by reading rather than by running.
 *
 * Deliberately no check of the version or variant nibbles, for
 * {@link isCanonicalFactorId}'s reason: this layer is not the authority on which
 * UUIDs the server may mint, and a rule invented here starts refusing valid
 * factor ids the day that authority changes its mind.
 *
 * It throws rather than returning a fallback. Every caller is about to seal
 * something under the answer, and a plausible-looking one is discovered months
 * later by somebody who cannot get in. The message names the value's shape and
 * not the caller's grammar — the two copies this replaces each named their own,
 * which is the one thing a shared owner cannot know.
 */
export function canonicalFactorId(id: string): string {
  const lowered = id.toLowerCase();

  // The wrappers come off before the shape is checked, and both halves have to
  // match: a value opened with `{` and closed with `)` is not a wrapped uuid,
  // and slicing it anyway hands the patterns a 35-character string they refuse
  // for the wrong reason.
  const unwrapped =
    (lowered.startsWith('{') && lowered.endsWith('}')) ||
    (lowered.startsWith('(') && lowered.endsWith(')'))
      ? lowered.slice(1, -1)
      : lowered;

  const groups = HYPHENATED_UUID.exec(unwrapped) ?? BARE_UUID.exec(unwrapped);

  if (groups === null) {
    throw new Error('A factor id must be a UUID.');
  }

  // Rebuilt from the captured groups rather than returned as matched, which is
  // what makes the bare 32-digit form come back hyphenated.
  return groups.slice(1).join('-');
}
