// The factor manifest — one authenticated blob per account naming every
// recovery factor and the public key it holds, sealed under the account's
// content key.
//
// **What it buys is that the *set* is unforgeable.** A factor's own row already
// carries its wrapped private key and the account's keys encapsulated to its
// public half; what no row can say is which factors an account has. There is
// deliberately no per-row public key column, because a server that could add a
// row could add a factor — and a rotation stages one seal per factor, so a set
// with a stranger in it hands the account's keys to whoever owns the stranger.
// The manifest is that set, written by the client under a key the server does
// not hold. The server enforces presence, framing and epoch, and can never read
// a byte of it.
//
// **The count leads, so a truncated plaintext is a mismatch rather than a
// smaller set.** Without it, chopping the tail off a manifest produces a shorter
// manifest that parses — and the entry it drops is the authenticator somebody
// still has. With it, the reader that compares the named set against the served
// set is comparing against a value that says up front how many entries it owes.
// It is decimal text and not a byte: eleven factors — a passkey and a card of
// ten, which is what a registration writes — is the two characters `11`, and a
// byte would put a hard ceiling at 255 into a format that has no other reason
// for one. The same argument applies to the rotation epoch in the associated
// data below, which is why it is written the same way.
//
// **Entries ascend by the canonical spelling of the factor identifier.** The
// bytes have to be reproducible from a set, and a set has no order; insertion
// order would make two clients holding the same factors write two different
// plaintexts. The sort is over the *text*, never over the identifier's bytes:
// .NET's `Guid.ToByteArray()` is mixed-endian, so a sort over raw UUID bytes
// agrees with this one on most sets and disagrees on some — and the frozen
// three-factor vector is one of the ones it disagrees on, which is the only
// reason that disagreement is visible anywhere.
//
// **Reading one back answers the set it names.** {@link openFactorManifest}
// verifies the tag and then walks the plaintext into entries, refusing every
// shape this grammar forbids — so one call both confirms that the content key a
// client has just obtained really is the account's content key, and says which
// factors the account names. **The walk is folded into that function rather than
// exported beside it**: a parser a caller has to remember to call is a gate that
// looks like it works, and the symptom of forgetting it is a manifest nobody
// checked. Folded in, a caller that never looked at the entries would still have
// paid for every refusal — and the caller this module has does look: the unlock
// gate in `account-key-custody.service.ts` compares the named set against the
// factor rows the route served, in both directions. That comparison belongs
// there and not here, because this module is handed one blob and never the rows
// beside it, and it is the only thing that answers a manifest naming nobody —
// which parses cleanly here, on purpose.
//
// **Nothing here is secret and nothing here is wiped.** A manifest's plaintext
// is public keys and identifiers — material that is already handed to peers by
// construction — so there is nothing in it to clear, and a `fill(0)` written for
// symmetry with the modules next door would suggest the opposite. It is sealed
// because the *set* must be unforgeable and because the server has no business
// reading which authenticators an account has, not because a point is a secret.
//
// **Two kinds of failure leave the reading side and they are told apart by
// type.** A value this browser could not read at all — a wire string that is not
// unpadded base64url, or a sealed width outside the window — arrives as
// {@link FactorManifestWireError}. Everything from the cipher onwards — a tag
// that does not verify, and every refusal the grammar makes on an authenticated
// plaintext — stays a bare `Error`, because those are statements about the
// account's key material: a plaintext that authenticated was written by somebody
// holding the content key, and a plaintext this grammar forbids is this client's
// own writer being wrong.
//
// **The split is the caller's and not a tidiness here.** The unlock gate in
// `account-key-custody.service.ts` wraps the whole call in one `catch` and has to
// answer a person with a next step: *reload this tab* for a body it could not
// read, which a newer bundle fixes, against *nothing you hold will change this*
// for an account whose material disagrees with itself. Told to a version skew,
// the second sentence is a permanent lockout over a state no ceremony touches.
// Without a type the only way to keep the two apart is for the gate to re-apply
// this module's decoder and width rules above its own `catch` — a second, weaker
// definition of what a manifest is, drifting from the one this file runs.
//
// Nothing here is a service and nothing here is injected: no state, no
// configuration, no dependency, so two functions are the whole of it.
import { UNIT_SEPARATOR, joinFields } from './associated-data';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import { canonicalFactorId, isCanonicalFactorId } from './factor-id';
import {
  FACTOR_KEYPAIR_LABEL,
  FACTOR_KEYPAIR_VERSION,
  FACTOR_PUBLIC_KEY_BYTES,
  requireUncompressedPoint,
} from './factor-keypair';
import { openEnvelope, sealEnvelope } from './key-envelope';

/**
 * The shortest thing that can be a sealed manifest: the AEAD envelope's own
 * floor — a version, a nonce and a tag with no ciphertext between them.
 *
 * **The server does enforce it, and the layer that does is the API edge.** Every
 * request carrying a manifest — a registration, an added passkey, a regenerated
 * card, a revoked passkey — hands its member to one decoder there, and that
 * decoder measures the decoded bytes against this framing's floor, its version
 * byte, and the entity's ceiling. Both ends of the window below are that
 * decoder's, to the byte, so a value this module would refuse to write is a value
 * that request would be refused for.
 *
 * **The stored rule is wider at the bottom, and that is a division of labour
 * rather than a disagreement.** The column's check admits anything from one byte
 * up, because "this `bytea` is not empty" is the whole of what a column can say
 * about a blob declaratively — and the entity restates exactly that, plus the
 * ceiling, and nothing narrower. A one-byte value is not a short manifest; it is
 * not an envelope at all, and the rule that knows so is this framing's, applied
 * where the bytes arrive. Moving that floor down into the catalog was the
 * alternative and buys nothing: it is a schema change for a bound already refused
 * one ring out, and it would leave the format's floor with two owners and one of
 * them a migration.
 *
 * What the number is is the AEAD envelope's own floor, inherited to the byte from
 * the module that computes it, and written out here so that both ends of this
 * window read as one rule with one owner. The cost of the literal is that it goes
 * stale in silence the day an envelope's overhead changes — and it goes stale
 * without disagreeing with anything visible from here, because the edge derives
 * its floor from the same three widths and would move with them.
 *
 * Nothing this module writes can be this short, so the floor only ever fires on
 * the reading side.
 */
export const FACTOR_MANIFEST_MIN_BYTES = 29;

/**
 * The widest sealed manifest the server stores.
 *
 * A ceiling rather than a limit on factors: entries are 103 bytes each — a
 * separator, a 36-character identifier, a separator and a 65-byte point — so
 * this admits 39 factors and refuses 40, measured rather than divided. A client
 * that sealed past it would be told by a 400 on a request it cannot retry, at
 * the end of a ceremony that has already drawn keys.
 */
export const FACTOR_MANIFEST_MAX_BYTES = 4096;

/**
 * A served manifest this browser could not read at all: a wire string that is not
 * the encoding this client decodes, or a sealed value whose width is not one this
 * format admits.
 *
 * **Both refusals happen before a cipher runs, and neither is a claim about the
 * account's key material.** No key has been touched when either fires, so nothing
 * about the factor a person just presented, the keys the account holds or the set
 * the manifest names has been observed — which is exactly what makes them the
 * wrong thing to report as an inconsistency. The way forward for a body in a
 * shape this bundle does not read is to reload the tab, because a reload is the
 * only act in this product that fetches a different copy of this JavaScript.
 *
 * **It is never thrown for anything the cipher touched.** A tag that does not
 * verify, and every refusal {@link namedFactors} makes on an authenticated
 * plaintext, keep arriving as whatever was thrown for them. Those bytes came from
 * somebody holding the content key, so a manifest that breaks this grammar is
 * this client's own writer disagreeing with this client's own reader — a state no
 * reload and no recovery factor changes, and the one this type must not be
 * stretched to cover. A split that routed every throw here would be a rename
 * rather than a distinction, and what it would do in production is tell somebody
 * whose account really is inconsistent to reload, forever.
 *
 * **Named for what was observed and not for a cause.** It says a value arrived in
 * a shape this browser cannot read; it does not say a server changed, a peer's
 * encoder drifted or a deployment went out mid-session, because this module
 * observes none of those and a name that guessed would be read as a diagnosis.
 * `…MisuseError`, the neighbouring grammar's word, was the other candidate and is
 * wrong in the same direction: misuse is a defect in the *call*, and a manifest
 * is not something this module's caller chose. `…UnreadableError` is wrong in the
 * more expensive direction — `unreadable` is already the custody word for a
 * ciphertext that did not open, which is precisely the category this type
 * excludes.
 *
 * **A class extending `Error` with a stable {@link name}**, the shape
 * `narrative-cipher.ts` argues for in full. `instanceof` is the check the gate
 * makes; the `name` is the second answer, for two copies of this module in one
 * page, where an error from the far one is `instanceof` nothing the near one
 * holds. Written as a class *field* so it is an own, enumerable property — with
 * the same measured limit that file records, that `structuredClone` does not
 * carry it. Matching on message text is the remaining alternative, and it would
 * quietly make every sentence in this file part of the contract, with a rewording
 * as the thing that breaks the gate.
 *
 * **What refused the value is kept as the `cause`.** The decoder next door says
 * which of its rules a string broke, in words this module could not improve on
 * and has no business restating; discarding it would leave a reader of a console
 * with a sentence about base64url and no idea which character offended.
 */
export class FactorManifestWireError extends Error {
  public override readonly name = 'FactorManifestWireError';
}

/** One factor of an account's set, as the manifest names it. */
export interface FactorPublicKey {
  /** Folded to the canonical spelling before it is written. */
  readonly factorId: string;
  /** Raw and uncompressed: {@link FACTOR_PUBLIC_KEY_BYTES} bytes leading 0x04. */
  readonly publicKey: Uint8Array;
}

const utf8 = new TextEncoder();

/**
 * Seals `factors` as the account's manifest at `rotationEpoch`, or rejects.
 *
 * The answer is unpadded base64url over the AEAD envelope — the wire form the
 * server's own decoder accepts — and the caller owes it to whichever path is
 * moving the factor set.
 *
 * The entries are ordered here and not by the caller: every path that promotes a
 * manifest holds a set, and a set arriving in the order somebody happened to
 * build it is the normal case rather than the exception.
 */
export async function sealFactorManifest(
  contentKey: CryptoKey,
  factors: readonly FactorPublicKey[],
  rotationEpoch: number,
): Promise<string> {
  const sealed = await sealEnvelope(
    contentKey,
    manifestPlaintext(factors),
    manifestAssociatedData(rotationEpoch),
  );

  // Measured on the sealed value, because that is the value the column holds.
  // Predicting it from the number of factors would be a second description of
  // the framing, kept true by nobody, and it would have to be re-derived the
  // day an envelope's overhead changes.
  //
  // **`'written'`, and that word is the whole of the difference between this
  // call and the one on the reading side.** A value too wide here is a set this
  // client assembled and a defect in this client; nothing arrived from anywhere,
  // so there is no body for {@link FactorManifestWireError} to be about and no
  // reload that would change the answer. See {@link requireManifestWidth}.
  requireManifestWidth(sealed.length, 'written');

  return encodeBase64Url(sealed);
}

/**
 * Opens a sealed manifest under `contentKey` at `rotationEpoch`, or rejects.
 *
 * Hands back **the factors it names**, in the order the plaintext names them,
 * which this grammar makes the only order a set can be written in. An empty list
 * is a manifest naming nobody: well formed here, and turned away one layer up by
 * the unlock gate that compares it against the account's live factor set.
 *
 * Rejects on a wire string that is not unpadded base64url, on anything outside
 * this module's width window, on a version byte this code does not know, on a
 * single flipped bit anywhere, on every plaintext the grammar forbids — see
 * {@link namedFactors} for that list and for why none of it is repaired — and on
 * an epoch other than the one the manifest was sealed at.
 *
 * **The first two are {@link FactorManifestWireError} and the rest carry no type
 * of this module's**, which is the only distinction a caller is offered and the
 * only one it is entitled to. The two are the refusals made before a cipher runs,
 * over a value that reached this browser in a shape it cannot read; everything
 * after them is a statement about the account's key material, and the head of
 * this file argues why flattening the two costs somebody a way back in.
 *
 * **What that last refusal is and is not.** It *binds* the epoch a manifest was
 * sealed at to the epoch it is read under, so the two cannot be recombined: a
 * manifest and an epoch that were never sealed together do not open. It is
 * **not** rollback detection, and reading it as such would be believing this
 * client holds something it does not. The only source of `rotationEpoch` here is
 * the very response that carried the manifest, so whoever can replay one can
 * replay the other, and the pair verifies exactly as it did the day it was
 * written. Detecting that the account has moved on since needs a value this
 * client keeps for itself and compares against, which is what
 * `rotation-epoch-record.ts` is: a per-device high-water mark that the unlock
 * gate reads, beside that gate's refusal of a response carrying no manifest at
 * all. Neither of those could move in here — this function is handed one blob
 * and one number out of the same response and nothing else — and neither makes
 * this binding redundant: a binding is a statement about one manifest, and a
 * high-water mark is a statement about an account over time.
 *
 * `async` is load-bearing: the refusals below are `throw`s, and a synchronous
 * throw from a function whose signature promises a `Promise` escapes past every
 * caller's `catch` on the result.
 */
export async function openFactorManifest(
  contentKey: CryptoKey,
  wire: string,
  rotationEpoch: number,
): Promise<readonly FactorPublicKey[]> {
  // The strict decoder, which refuses padding, the standard alphabet's `+` and
  // `/`, and a final group no encoder would emit. A lenient reading here would
  // accept a value the server's own decoder rejects.
  //
  // Wrapped, for the reason {@link decodeManifestWire} gives: the decoder's
  // refusal is a shared module's and would leave here as a type that says nothing
  // about which value was refused.
  const sealed = decodeManifestWire(wire);

  // **Before the cipher, and in this module's own words.** The floor is the
  // envelope's floor to the byte, so `openEnvelope` would turn a short value
  // away on its own — but it would say "not an envelope" about a value that
  // came out of the manifest column, and it has nothing at all to say about the
  // ceiling. Checking here is what makes both ends of the window one rule with
  // one owner, and what lets a reader of the failure know which column the
  // value came from.
  //
  // **`'arrived'`, which is what makes this refusal a
  // {@link FactorManifestWireError} while the identical comparison on the writing
  // side is not.** The width is the same number either way; what differs is whose
  // value it describes.
  requireManifestWidth(sealed.length, 'arrived');

  // The tag first and the grammar second. Nothing below looks at a byte the
  // cipher has not authenticated, so every refusal in the walk is a statement
  // about a plaintext somebody holding the content key wrote — never about
  // whatever the network happened to deliver.
  return namedFactors(
    await openEnvelope(
      contentKey,
      sealed,
      manifestAssociatedData(rotationEpoch),
    ),
  );
}

// `count ‖ 0x1F ‖ factorId ‖ 0x1F ‖ publicKey ‖ 0x1F ‖ …`, entries ascending by
// the canonical spelling of the identifier.
//
// The join is `associated-data.ts`'s, which is the one definition of the `0x1F`
// rule in this client — separators between the fields and none at either end,
// every field kept, nothing folded. A private copy here would be a second
// spelling of it, and the defect that copy would reintroduce is not
// hypothetical: the shared one was just fixed for a leading empty field, whose
// symptom was a value of the right width with the separator in the wrong place.
//
// The public keys go in raw and are never re-encoded. Pushed through UTF-8 a
// 65-byte point comes out 129 bytes long — measured — with no error anywhere,
// which is the whole reason this grammar joins bytes rather than strings.
function manifestPlaintext(
  factors: readonly FactorPublicKey[],
): Uint8Array<ArrayBuffer> {
  const entries = orderedEntries(factors);

  return joinFields(
    utf8.encode(String(entries.length)),
    ...entries.flatMap((entry) => [
      utf8.encode(entry.factorId),
      entry.publicKey,
    ]),
  );
}

// The entries in the one order this format has, with every identifier folded to
// the spelling it is ordered and written under.
//
// **Folded before it is sorted, never after.** The fold is the thing the order
// is defined over, so sorting first and folding second would order `A1B2…`
// before `a1b2…` and write them the other way round — two clients holding one
// set, writing two plaintexts, neither of them wrong-looking.
//
// **Compared as code units with `<`, never `localeCompare`** — and **nothing
// holds that line**, so it is stated rather than tested. Measured: over 200,000
// random pairs of canonical identifiers the collator and an ordinal comparison
// never disagreed, which is unsurprising once the fold above has left nothing
// but lower-case hex and hyphens. Swap one in and the suite agrees with you. It
// stays because a collator is locale- and implementation-dependent by
// specification while these bytes are frozen: the day this runs under a
// collation that orders them differently, two clients holding one set write two
// plaintexts, both open, and nothing anywhere names the cause. The server
// compares ordinally, and this is that comparison.
function orderedEntries(
  factors: readonly FactorPublicKey[],
): readonly FactorPublicKey[] {
  const folded = factors.map((factor) => {
    // IFR-019, and `factor-keypair.ts`'s guard rather than a length written
    // again here. A point of another width is not a shorter point: the entry
    // has no length prefix, so 64 bytes shift every byte after them and 65
    // bytes under the hybrid encoding look right and derive nothing. The rule
    // has one owner because a second copy is one edit away from admitting the
    // compressed form on the day some other screen finds it convenient.
    requireUncompressedPoint(factor.publicKey);

    return {
      factorId: canonicalFactorId(factor.factorId),
      publicKey: factor.publicKey,
    };
  });

  requireDistinct(folded);

  // A copy is sorted rather than the caller's array, which `map` above already
  // made: `sort` is in place, and re-ordering a set somebody else holds is a
  // side effect nothing at the call site would expect.
  return folded.sort((left, right) =>
    left.factorId < right.factorId
      ? -1
      : left.factorId > right.factorId
        ? 1
        : 0,
  );
}

// The separator as the byte the plaintext carries, derived from
// `associated-data.ts`'s spelling of it rather than written `0x1f` again here.
// That module owns the rule and states why the code point is spelled rather than
// typed: a literal control character is invisible in every tool a reviewer would
// read this file in, which is the one property a byte of a frozen format cannot
// afford.
const SEPARATOR_BYTE = UNIT_SEPARATOR.charCodeAt(0);

// Decimal digits, with a leading zero only when that is the whole of it.
//
// **Every coercion in the language is looser than this**, and each of them is
// looser in a way that costs something. `Number('')` is 0, so a plaintext
// leading with a separator would arrive as a manifest naming nobody rather than
// as a malformed one; `Number('+3')` and `parseInt('3abc')` are both 3, measured,
// so a reader that coerces accepts a count no writer produces; and `Number('03')`
// is 3, which gives one factor set two plaintexts — the exact property the
// decimal spelling exists to prevent.
const DECIMAL_COUNT = /^(?:0|[1-9][0-9]*)$/;

// The factor set a verified plaintext names, or a refusal. The inverse of
// {@link manifestPlaintext} and {@link orderedEntries}, and the reason it is a
// private function rather than an export is at the top of this file.
//
// **The walk is positional and can never be a split on the separator.** A 65-byte
// P-256 point contains a `0x1F` often enough that splitting shears entries in
// half, and that is measured rather than argued from probability: one of the
// three frozen points carries the byte and four of the eleven do, so a split sees
// 8 fields where there are 7 and 29 where there are 23. So the count is taken up
// to the first separator, and then each entry is an identifier up to the next
// separator, one separator, and **exactly 65 bytes taken by position**.
//
// **It refuses and never repairs, and the two most tempting repairs are the two
// that cost the format its meaning.** A reader that folded a non-canonical
// identifier, or sorted entries it found out of order, would accept a second
// plaintext for one set — and from that moment the manifest is no longer a
// function of the set it names, so no second implementation can reproduce these
// bytes. Both are refusals here and folds on the writing side, where a caller's
// spelling and a caller's order are inputs rather than the contract.
function namedFactors(plaintext: Uint8Array): readonly FactorPublicKey[] {
  const countEnd = plaintext.indexOf(SEPARATOR_BYTE);

  // **The count-only form gets its own path**, because it is the one well-formed
  // plaintext with no separator anywhere in it: `0` and nothing else, a manifest
  // naming nobody. That is not a refusal and filing it as one is how somebody
  // makes the parser reject it — an account midway through losing its last
  // factor could then not be read at all, at the one moment its owner most needs
  // to see what is left. What turns it away is the set equality the unlock gate
  // applies one layer up, against the factor rows the route served.
  // Everything else with no separator is refused by the two checks below: an
  // empty plaintext has a zero-length count field, and any other number here
  // declares entries that are not there.
  if (countEnd < 0) {
    requireCountAgrees(declaredCount(plaintext), 0);

    return [];
  }

  const declared = declaredCount(plaintext.subarray(0, countEnd));
  const entries: FactorPublicKey[] = [];

  // **Bounded by the buffer and never by the declared count.** Sizing anything
  // from a count that arrived with the data is how a forged `999999999999999`
  // becomes a `RangeError` before a single entry has been read — a refusal that
  // names an allocator rather than a manifest. The count is a value to compare
  // against once the walk is done, and nothing before then may act on it.
  let at = countEnd + 1;

  for (;;) {
    // **A separator is followed by an entry, and this is not the leftover check
    // further down.** After the last entry a trailing separator is consumed, `at`
    // reaches the end, and "every byte was read" holds — so a plaintext with a
    // separator hanging off the end passes that check and is turned away only
    // here. The grammar puts one separator between fields and none at either end.
    if (at >= plaintext.length) {
      throw new Error(
        'A factor manifest ends with a separator; a separator goes between fields and never at either end.',
      );
    }

    const identifierEnd = plaintext.indexOf(SEPARATOR_BYTE, at);

    if (identifierEnd < 0) {
      throw new Error('A factor manifest entry carries no public key.');
    }

    const factorId = asciiText(plaintext.subarray(at, identifierEnd));

    // **Refused, never folded** — `canonicalFactorId` here would accept the
    // upper-case spelling of an identifier and hand back the canonical one, which
    // is precisely the second plaintext this format cannot have. The predicate is
    // `factor-id.ts`'s, which owns both halves of that rule.
    if (!isCanonicalFactorId(factorId)) {
      throw new Error(
        `A factor manifest names ${factorId}, which is not the canonical spelling of a factor id.`,
      );
    }

    const publicKeyAt = identifierEnd + 1;
    // A copy and not a view. The entries outlive the plaintext, and a view would
    // hold the whole of it alive behind every point — and hand whoever holds one
    // entry the bytes on either side of it.
    const publicKey = Uint8Array.from(
      plaintext.subarray(publicKeyAt, publicKeyAt + FACTOR_PUBLIC_KEY_BYTES),
    );

    // **Checked after the take, because the take cannot fail.** `subarray` past
    // the end answers a short array rather than throwing, so a reader that took
    // 65 bytes and trusted the take reports a factor holding a 45-byte key. The
    // message is about the framing and not the encoding: these bytes are not a
    // malformed point, they are a plaintext that ran out.
    if (publicKey.length !== FACTOR_PUBLIC_KEY_BYTES) {
      throw new Error(
        `A factor manifest entry for ${factorId} carries ${publicKey.length} bytes where a public key needs ${FACTOR_PUBLIC_KEY_BYTES}.`,
      );
    }

    // IFR-019 and `factor-keypair.ts`'s guard, for `orderedEntries`' reason — and
    // on this side it is the *only* thing that can refuse a hybrid-encoded point.
    // It is the right width, so the walk takes it without noticing, and
    // `importKey` accepts it (measured), so nothing downstream notices either:
    // the account would name a point every other client re-derives a different
    // key from.
    requireUncompressedPoint(publicKey);

    entries.push({ factorId, publicKey });

    at = publicKeyAt + FACTOR_PUBLIC_KEY_BYTES;

    if (at === plaintext.length) {
      break;
    }

    // Every byte accounted for: what follows a point is either the end of the
    // plaintext or one separator. A reader that stopped after `count` entries
    // would leave a shaved entry on the end unread, and then the bytes it
    // authenticated and the bytes it read are not the same bytes — which is the
    // gap everything downstream is entitled to assume does not exist.
    if (plaintext[at] !== SEPARATOR_BYTE) {
      throw new Error(
        'A factor manifest carries bytes past an entry that are not a separator.',
      );
    }

    at += 1;
  }

  // Distinctness before order, and that ordering of the two checks is the whole
  // reason the first one is reachable — see {@link requireDistinct}.
  requireDistinct(entries);
  requireAscending(entries);
  requireCountAgrees(declared, entries.length);

  return entries;
}

// The count field as the number it spells, or a refusal.
//
// Read as text and validated against {@link DECIMAL_COUNT} rather than coerced,
// for the reasons given there. The refusal quotes the field, because a count
// nobody can see is a refusal nobody can locate in 310 bytes of hex.
function declaredCount(field: Uint8Array): number {
  const text = asciiText(field);

  if (!DECIMAL_COUNT.test(text)) {
    throw new Error(
      `A factor manifest counts its entries in decimal digits, not "${text}".`,
    );
  }

  return Number(text);
}

// A field as the text it spells, refusing any byte that is not ASCII *before*
// anything decodes it.
//
// **So that no decoder's semantics are load-bearing.** `TextDecoder` substitutes
// U+FFFD for bytes it cannot read and says nothing about having done it, and it
// strips a leading byte-order mark by default — this repository has been caught
// by that second one already. Both turn a plaintext this client cannot have
// written into one that reads as something else. Every field of this grammar is a
// decimal count or a canonical factor id, so every legal byte is ASCII, and the
// checks downstream are then reading the bytes that were there rather than a
// decoder's repair of them.
//
// The spread is bounded by the format and not by the input: the sealed value has
// already been through {@link requireManifestWidth}, so no field reaching here is
// wider than a few thousand bytes.
function asciiText(field: Uint8Array): string {
  for (const byte of field) {
    if (byte > 0x7f) {
      throw new Error(
        `A factor manifest field carries the byte 0x${byte.toString(16)}, which is not ASCII.`,
      );
    }
  }

  return String.fromCharCode(...field);
}

// Refuses entries that do not ascend by the canonical spelling of the identifier.
//
// **Strictly**, and the strictness is what makes the plaintext a function of the
// set: a set has no order, so this one is imposed to stop two clients holding the
// same factors from writing two different plaintexts. A reader that *sorted* what
// it found instead of refusing would accept every one of those spellings and look
// correct doing it, which is why the rule is observable on this side only as a
// refusal and never as the order that comes back.
//
// `<`, never `localeCompare`, for the reason {@link orderedEntries} gives at the
// sort this is the inverse of — and nothing holds that line here either.
//
// It names both identifiers. "Out of order" alone is a refusal nobody can act on.
function requireAscending(entries: readonly FactorPublicKey[]): void {
  for (let index = 1; index < entries.length; index += 1) {
    const before = entries[index - 1].factorId;
    const after = entries[index].factorId;

    if (!(before < after)) {
      throw new Error(
        `A factor manifest names ${before} before ${after}; entries ascend by the canonical spelling of the factor id.`,
      );
    }
  }
}

// Refuses a count that disagrees with the entries the walk actually found.
//
// **Compared at the end, and it is the only thing that can see truncation.** Low
// is the direction that costs somebody an authenticator: a reader that loops
// `count` times and stops never sees the entry it drops, and that entry is a
// factor still opening the account. High is the direction the field was added
// for — a reader that walks to the end of the buffer and never compares cannot
// see a chopped tail at all, because what is left is a shorter manifest that
// parses.
function requireCountAgrees(declared: number, found: number): void {
  if (declared !== found) {
    throw new Error(
      `A factor manifest counts ${declared} entries and carries ${found}.`,
    );
  }
}

/**
 * `label ‖ 0x1F ‖ version ‖ 0x1F ‖ rotationEpoch, decimal digits`.
 *
 * The label and the version byte are the factor-keypair grammar's own,
 * imported and never restated: this message is a third message of that grammar,
 * so a second copy of either constant here would be a second grammar that looked
 * like the first until one of them moved.
 *
 * **The epoch is the manifest's associated data and nothing else binds it.** The
 * server refuses anything that is not the stored epoch plus one, but it cannot
 * read what the client sealed — so a manifest that named the right set at the
 * wrong epoch would store, and would come back at a rotation that has since
 * happened. Authenticating the epoch is what turns that into a tag failure.
 */
function manifestAssociatedData(
  rotationEpoch: number,
): Uint8Array<ArrayBuffer> {
  // **`String` does not render every number as digits**, and the two it renders
  // otherwise are both reachable by arithmetic: a fraction comes out `1.5` and
  // anything past 1e21 comes out `1e+21`. Either is a well-formed message that
  // no server and no other client will ever rebuild, so a manifest sealed under
  // one stores and then never opens — the unnamed permanent lockout this whole
  // grammar is careful about. The epoch arrives from the server's stored value
  // plus one, so nothing today hands us such a number; this costs one
  // comparison and the alternative has no repair path.
  if (!Number.isSafeInteger(rotationEpoch) || rotationEpoch < 1) {
    throw new Error(
      `A rotation epoch is a whole number from 1 up, not ${rotationEpoch}.`,
    );
  }

  return joinFields(
    utf8.encode(FACTOR_KEYPAIR_LABEL),
    grammarVersion(),
    utf8.encode(String(rotationEpoch)),
  );
}

// Refuses a factor named twice. **One rule, both sides**, which is why it is a
// function rather than a line in each of them.
//
// **A manifest naming one factor twice is not a set**, and nothing downstream
// would say so — least of all the unlock gate one layer up, which reads both
// sides into sets and therefore absorbs a repeat instead of seeing one: the
// count leading the plaintext would agree with it, the framing would be well
// formed, and a rotation reading it would stage two seals for one factor and
// believe it had covered one more than it had. On the writing
// side the check is over the *folded* identifiers, because the two spellings of
// one factor are only equal after the fold — the same reason the fold happens
// before the sort. On the reading side there is no fold and none is wanted: a
// non-canonical spelling has already been refused by then, so the identifiers
// reaching here are the only spelling this format has.
//
// **On the reading side {@link requireAscending} would catch a repeat too, and
// this check is still not redundant.** Two equal identifiers are not ascending,
// so strict ascent refuses them first — until somebody relaxes that comparison
// to `<=`, which reads as a harmless tidy-up. From that moment this is the only
// thing between a forged manifest and a factor the account serves that nothing
// names, so it runs first and answers in its own words. That ordering is what
// makes it reachable at all.
//
// It names the offender. A set arrives from somewhere — a caller assembling a
// card of ten beside a passkey, or a server handing back a blob — and "a
// duplicate" without the value is a refusal nobody can act on.
function requireDistinct(entries: readonly FactorPublicKey[]): void {
  const seen = new Set<string>();

  for (const entry of entries) {
    if (seen.has(entry.factorId)) {
      throw new Error(
        `A factor manifest names ${entry.factorId} more than once; a set names each factor once.`,
      );
    }

    seen.add(entry.factorId);
  }
}

// Where the value being measured came from, which is the only thing that differs
// between this module's two width checks.
//
// **Two words rather than a boolean**, because a `boolean` parameter at a call
// site says nothing at all: `requireManifestWidth(sealed.length, true)` is a line
// a reader has to leave the statement to understand, and the meaning it carries
// here is the difference between a person being told to reload and a person being
// told nothing they hold will help.
type ManifestWidthOrigin = 'written' | 'arrived';

// Refuses a sealed manifest outside this module's window.
//
// One function for both ends and both directions, because they are one comparison
// between {@link FACTOR_MANIFEST_MIN_BYTES} and {@link FACTOR_MANIFEST_MAX_BYTES}
// bytes. On the writing side only the ceiling can fire — an envelope is never
// shorter than its own floor — and it is the one that matters there, since the
// alternative is a 400 at the end of a ceremony that has already drawn keys. On
// the reading side both can.
//
// **One comparison, two meanings, and `origin` is what tells them apart.** A
// sealed value this client just produced is too wide because this client
// assembled too large a set: a defect here, which no act of the person's touches
// and which {@link FactorManifestWireError} would misdescribe as a body that
// arrived unreadable. A sealed value that arrived too wide, or too short, is a
// fact about what was served — and the gate one layer up owes that person a
// reload rather than a permanent dead end. Splitting this into two functions was
// the alternative and would have put the window in two places: the number is one
// rule, and it is the *reading* of a failure that is two.
//
// The message is the same sentence in both cases on purpose. It describes the
// rule, which is identical; what a caller acts on is the type, never the words.
//
// The width is in the message. A caller told only that its set is too large
// cannot tell whether it is over by one factor or by ten.
function requireManifestWidth(
  width: number,
  origin: ManifestWidthOrigin,
): void {
  if (
    width >= FACTOR_MANIFEST_MIN_BYTES &&
    width <= FACTOR_MANIFEST_MAX_BYTES
  ) {
    return;
  }

  const message = `A sealed factor manifest is between ${FACTOR_MANIFEST_MIN_BYTES} and ${FACTOR_MANIFEST_MAX_BYTES} bytes, not ${width}.`;

  throw origin === 'arrived'
    ? new FactorManifestWireError(message)
    : new Error(message);
}

// The wire string as the bytes it spells, with the strict decoder's refusal
// re-thrown as this module's own.
//
// **`base64url.ts`'s error is not this module's to leak.** It is a shared module
// with callers all over this client, and a gate catching *it* by type would be
// catching every one of them — so the refusal has to leave here as a statement
// about a manifest or the gate cannot tell a manifest it could not read from
// anything else that failed to decode today.
//
// **The decoder's error is kept as the `cause` rather than discarded**, because
// this wrapper knows less than what it is wrapping: the decoder says whether the
// string carried padding, a character outside the alphabet, or a final group no
// encoder emits, and re-stating one of those here would be a second, vaguer
// opinion about a rule it does not own. `cause` is what a console prints beneath
// the message, and it costs nothing to carry.
function decodeManifestWire(wire: string): Uint8Array {
  try {
    return decodeBase64Url(wire);
  } catch (cause) {
    throw new FactorManifestWireError(
      'A sealed factor manifest arrives as unpadded base64url, and this value is not.',
      { cause },
    );
  }
}

// The grammar's version as the one raw byte it is. A fresh array per call, for
// `joinFields`' reason: a shared one is a buffer a caller could reach into and
// change under the next caller's message.
function grammarVersion(): Uint8Array {
  return Uint8Array.of(FACTOR_KEYPAIR_VERSION);
}
