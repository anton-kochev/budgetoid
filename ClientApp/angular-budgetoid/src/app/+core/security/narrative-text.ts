// The answers a screen is entitled to about one narrative field: what came back
// when it was read, what to store when it is written, and what to key on when a
// name is looked up.
//
// Types and nothing else, and the emptiness is the point. Sealing, opening and
// the blind index all live on `AccountKeyCustodyService`, because the account's
// keys never leave it — a module with a function body here would need one of
// them in its hands, and there is exactly one place in this client allowed to
// hold one. What lives in this file is the vocabulary those sides agree on,
// importing no key, no service and no framework.
//
// **Read has three words and write has two, and the asymmetry is a decision.** A
// read can fail against a ciphertext: the wrong key, the wrong binding, altered
// bytes, a value the strict decoder refuses — one indistinguishable symptom by
// design, and that symptom is `unreadable`. A write has no ciphertext to fail
// against. Sealing under a key this client is already holding cannot produce a
// damaged value, so an `unreadable` member on {@link SealedField} would be a
// state no branch could reach while every `@switch` over the union still had to
// carry a case for it — and a case nothing can produce is not caution, it is a
// branch somebody eventually fills in with a guess.
//
// **{@link BlindIndexValue} joins the write side of that asymmetry rather than
// restating it.** Computing an index has no ciphertext to fail against either:
// the input is a name the caller already holds and the output is a MAC over it,
// so nothing was opened and there is no `unreadable` to have. Two words there for
// the reason there are two here, and the word is `computed` rather than `sealed`
// because a keyed digest is not an envelope — its own declaration says why.
//
// **`unlocking` is a word in none of these unions, and a caller that asks
// mid-unlock is told `locked` by every one of them.** What a screen can do is
// identical in both: no text and no index to key on, and the way forward is a
// factor. The difference is real and worth showing, which is why
// `AccountKeyCustodyService.status` publishes it — to the one screen whose job is
// to say so. Repeating it here would state one fact in two vocabularies and
// invite a template to draw a spinner over a description while the account's keys
// open. It is `AccountKeyStatus`'s own "three words and no fourth" one layer
// down: a failed unlock is `locked` again there, and a field read during one —
// or a name indexed during one — is `locked` here.
//
// **A column holding no value is `NarrativeText | null`, never a fourth member.**
// An `absent` member would file a fact about the *row* — nobody typed a
// description — inside a union about the *key*, and the first template written
// against it renders "we could not read this" over a field that was simply never
// filled in. The two facts are also known at different moments: whether a column
// is null is known before any key is involved, and whether it opens is known only
// after one is. `null` on the outside keeps them apart, and a mapper written
// against it answers the row's question before it asks this one — no such mapper
// exists yet, so that ordering is a rule for the one that arrives rather than a
// description of anything running.
//
// **Which is the same reason {@link NarrativeText} itself is not `null`.**
// Collapsed to `string | null`, `locked` and `unreadable` become one word — and
// they are two different next steps for a person. `locked` says present a factor
// and the whole screen comes back. `unreadable` says this one value is damaged
// and the rest of the row is fine, and no ceremony anybody runs will change it.
// Sent down the wrong one, somebody re-presents a factor over a single corrupted
// column, or waits for a screen to recover that never can. It is the split
// `SessionService` keeps between `anonymous` and `unreachable`, the one
// `SignInService` keeps between `refused` and `unknown`, and the one
// `AccountKeyCustodyService` keeps across `unopened`, `unreachable` and
// `unauthenticated` — the same rule a fourth time, because the mistake is
// available at every one of them.
//
// **{@link NarrativeOpener} and {@link NarrativeIndexer} are the shapes a
// view-model mapper is handed, written down ahead of the mapper.** A mapper
// handed the service would need a `TestBed` to be exercised at all, could reach
// `unlock`, `lock` and `adopt` on the way past, and would tie a row-shaped
// transform to Angular's injector for the sake of one capability. A mapper
// handed one of these functions takes exactly that capability — open this wire
// value under this binding, key this name for a lookup — and a spec stands it
// up in two lines.
//
// **Both are now pinned assignable, and both pins call through the value.**
// `account-key-custody.service.spec.ts` types `openField` as the first and
// `blindIndex` as the second and then *uses* each one, which is the half a bare
// assignment does not have: `openField` reads a `#` field, so handing it over
// as `custody.openField` instead of as an arrow type-checks perfectly and
// answers every call with a `TypeError` on the wrong receiver — and
// `@typescript-eslint/unbound-method` is switched off for specs, so nothing but
// a call finds it.
//
// **What the pins do not hold is that a mapper takes the narrow type rather
// than the service**, which no compiler can say and which stays an argument for
// review. There is still no mapper: these are the shape the day one arrives.
//
// **There is deliberately no `NarrativeSealer`.** Nothing seals in a mapper —
// sealing happens on the way *out* of a screen, in a service that has already
// injected custody to save with, and a row-shaped transform has nothing to
// write. A third function type here would be exactly the thing the paragraph
// above says these two stopped being: named nowhere, assignable from nothing,
// symmetry standing in for a caller.
import type { BlindIndexedField } from './blind-index';
import type { NarrativeFieldBinding } from './narrative-cipher';

/**
 * One narrative field as a screen may know it: the text, or the reason there is
 * none.
 *
 *   * `text` — the value opened, byte for byte what was sealed.
 *   * `locked` — this browser is holding no content key. The way forward is a
 *     factor, and it brings every other field on the screen back with it.
 *   * `unreadable` — a key was there and this value did not open under it. The
 *     way forward is not another ceremony; the rest of the row is fine.
 *
 * A column holding no value is `NarrativeText | null` at the edge that reads the
 * row, never a fourth member here.
 */
export type NarrativeText =
  | { readonly state: 'text'; readonly value: string }
  | { readonly state: 'locked' }
  | { readonly state: 'unreadable' };

/**
 * One narrative field on its way to a column: the wire value to store, or the
 * reason there is nothing to store.
 *
 * Two words, because sealing has no ciphertext to fail against — the head of
 * this file argues why a third would be a branch nothing can reach.
 *
 * `wire` is the unpadded base64url of the envelope, exactly as
 * `sealNarrativeField` renders it, and it goes into the column unchanged.
 */
export type SealedField =
  | { readonly state: 'sealed'; readonly wire: string }
  | { readonly state: 'locked' };

/**
 * One name on its way to a lookup or a column: the blind index to key on, or the
 * reason there is none.
 *
 * Two words, like {@link SealedField} and for that same reason — the head of this
 * file argues it once and this union joins it. Computing an index has no
 * ciphertext to fail against: the input is a name the caller already holds and
 * the output is a MAC over it, so there is no `unreadable` member here because
 * nothing was opened.
 *
 * **`computed`, not `sealed`, because the thing is different and not just the
 * word.** A blind index is a keyed digest, not an envelope: no version byte, no
 * nonce, no tag, and nothing about it can ever be opened again. A shared word
 * would invite a reader to look for the other half of a round trip that does not
 * exist.
 *
 * `locked` means what it means everywhere else in this file: this browser is
 * holding no key, and the way forward is a factor. The key is the account's
 * *index* key here rather than its content key, and that distinction is invisible
 * to a caller by design — the two are held and dropped together, so a screen that
 * could seal can always index.
 *
 * `value` is the unpadded base64url `computeBlindIndex` renders — 43 characters,
 * always — and it goes into the column, or the query that looks a name up,
 * unchanged.
 */
export type BlindIndexValue =
  | { readonly state: 'computed'; readonly value: string }
  | { readonly state: 'locked' };

/**
 * The one capability a view-model mapper needs: open this wire value under this
 * binding. Nothing is typed as this today; the head of the file says what that
 * costs.
 *
 * Rejects on a binding the codec refuses: a table and column that are not one
 * of the pairs it publishes — asked as a pair, so a real table beside a column
 * belonging to another one is refused as well, where two membership tests would
 * wave it through — or a row id in any spelling but the canonical one. Every
 * one of those rejections carries `NarrativeFieldMisuseError`, and that type is
 * the whole of what lets a caller tell *you asked for something impossible*
 * from *this stored value did not open*: the second is a fact about a column,
 * the first a fact about the call, saying nothing whatever about what is
 * stored.
 *
 * **That it stays a rejection is the load-bearing half.** As a fourth
 * {@link NarrativeText} member it would reach a template, and a caller's defect
 * rendered as a sentence about damaged text is a bug wearing a UI — put in
 * front of somebody who can do nothing whatever about it, over a row that is
 * perfectly fine. Only the refusals a person can act on become a word this type
 * hands to a screen.
 */
export type NarrativeOpener = (
  binding: NarrativeFieldBinding,
  wire: string,
) => Promise<NarrativeText>;

/**
 * The other capability a view-model mapper needs: key this name so a lookup can
 * find the rows that share it.
 *
 * **It exists because no read hands a blind index back.** A payee crosses the
 * wire with no `name_key` on it and no route in the product returns one, so a
 * mapper that has just opened a name and wants the value a query keys on has to
 * compute it — and the only thing that can is the account's index key, which
 * one class holds and no member gives out. A mapper cannot be handed the key,
 * so it is handed this.
 *
 * Takes a field and **no row id**, which is the whole shape of the operation
 * and the deliberate inverse of {@link NarrativeOpener}'s binding: an index has
 * to be *equal* for equal names across rows, where a narrative value must be
 * bound to exactly one. `blind-index.ts` argues it at the message itself.
 *
 * Rejects on a pair the codec refuses — a table and a column that are not one
 * of the four it lists, asked as a pair, so a real table beside a column
 * belonging to another one is refused too. That it stays a rejection is the
 * load-bearing half, for the reason {@link NarrativeOpener} gives: a caller's
 * defect turned into a word a screen renders is a bug wearing a UI, shown to
 * somebody who can do nothing whatever about it.
 */
export type NarrativeIndexer = (
  field: BlindIndexedField,
  plaintext: string,
) => Promise<BlindIndexValue>;
