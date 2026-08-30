// The two answers a screen is entitled to about one narrative field: what came
// back when it was read, and what to store when it is written.
//
// Types and nothing else, and the emptiness is the point. Sealing and opening
// live on `AccountKeyCustodyService`, because the account's content key never
// leaves it — a module with a function body here would need that key in its
// hands, and there is exactly one place in this client allowed to hold one. What
// lives in this file is the vocabulary the two sides agree on, importing no key,
// no service and no framework.
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
// **`unlocking` is not a fourth word, and a caller that asks mid-unlock is told
// `locked`.** What a screen can render is identical in both: no text, and the way
// forward is a factor. The difference is real and worth showing, which is why
// `AccountKeyCustodyService.status` publishes it — to the one screen whose job is
// to say so. Repeating it here would state one fact in two vocabularies and
// invite a template to draw a spinner over a description while the account's keys
// open. It is `AccountKeyStatus`'s own "three words and no fourth" one layer
// down: a failed unlock is `locked` again there, and a field read during one is
// `locked` here.
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
// **Which is the same reason the union itself is not `null`.** Collapsed to
// `string | null`, `locked` and `unreadable` become one word — and they are two
// different next steps for a person. `locked` says present a factor and the whole
// screen comes back. `unreadable` says this one value is damaged and the rest of
// the row is fine, and no ceremony anybody runs will change it. Sent down the
// wrong one, somebody re-presents a factor over a single corrupted column, or
// waits for a screen to recover that never can. It is the split `SessionService`
// keeps between `anonymous` and `unreachable`, the one `SignInService` keeps
// between `refused` and `unknown`, and the one `AccountKeyCustodyService` keeps
// across `unopened`, `unreachable` and `unauthenticated` — the same rule a fourth
// time, because the mistake is available at every one of them.
//
// **{@link NarrativeOpener} is the shape a view-model mapper should be handed,
// written down one commit ahead of the mapper.** A mapper handed the service
// would need a `TestBed` to be exercised at all, could reach `unlock`, `lock`
// and `adopt` on the way past, and would tie a row-shaped transform to Angular's
// injector for the sake of one capability. A mapper handed this function takes
// exactly that one — open this wire value under this binding — and a spec stands
// it up in two lines. **What the narrowness does not do is enforce any of it.**
// No mapper exists, this type is named nowhere outside its own declaration, and
// nothing pins that `AccountKeyCustodyService.openField` is even assignable to
// it — so the first mapper written can still be handed the whole service with
// nothing going red. It is an argument for what to write the day that caller
// arrives, and the assignment is written down on that day.
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
