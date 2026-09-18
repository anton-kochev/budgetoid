// Where the account's two keys live once a factor has opened them, and the one
// place in this client that holds them past the ceremony that produced them.
//
// The account owns one content key and one index key; every recovery factor
// holds a keypair that leads back to both — its private half wrapped under the
// key-encryption key that factor derives, and the two keys encapsulated to its
// public half. A browser that has just proved a factor holds a key-encryption
// key and nothing else, so this service does the only thing left: it reads that
// credential's envelopes, tries each in turn, and keeps what came out as two
// `CryptoKey` objects. Three operations delegate to what it
// holds — seal this field, open that one, index a value so a lookup can key on
// it — and they are the whole reason no caller has any occasion to ask for a
// key. All three have production callers now — one service per screen that
// renders rows a person typed, and each of those services uses all three: it
// opens what a read brought back, seals what a write is about to send, and
// keys a value so the server's unique index has bytes it can judge. So a
// change to any of the three operations is a change to what those screens show
// and what they store, and the suite that answers for it is theirs as much as
// this file's. **Which screens they are is deliberately not written here**: the
// describe at the foot of this file's spec refuses every table and column of
// the codec's pairs anywhere in this source, comments included, and a caller
// list spelled out in prose is exactly where a second copy of that list
// starts.
//
// **`providedIn: 'root'`, and that breaks the habit of the two services beside it
// deliberately.** `RegisterService` and `SignInService` are provided on their
// screens, and their argument is exactly right for them: an attempt that was
// abandoned should die with the screen that abandoned it, rather than being
// readable from an injector an hour later. These keys are not an attempt. They
// are state of the **session**, and a session outlives every screen in the
// product — the whole point is that a person unlocks once and stays unlocked
// while they move around the app.
//
// **The near miss is route-providing on `app`, and it must not be taken.** It
// reads as the tidier answer: the keys would then belong to the part of the
// route table that renders budget content, and would be dropped on the way out.
// What it actually does is hand their lifetime to the router. `guestGuard`
// bounces an authenticated visitor off `/welcome`, and that bounce destroys and
// recreates the `app` injector — so a person who lands on `/welcome` by a back
// button, a bookmark or a redirect loses both keys, silently, and the account
// locks. Nothing goes red. The visible symptom is an account that was readable
// a moment ago and is not now, and the price of getting back in is a factor
// presented all over again. The settings screen's **Unlock** is a way out of
// that state and not a reason to tolerate it: it raises a system prompt in
// front of somebody whose only act was to press Back, and the screen can give
// no reason for asking, because nothing about the navigation was a refusal.
//
// **Ending custody is `lock()`, and it is a method rather than a lifetime**,
// which is the trade root-providing makes: an injector nobody destroys cannot
// forget anything on its own, so whoever ends the session has to say so. That is
// the honest shape anyway — the browser can end a session in ways no injector
// observes, and a page reload ends custody whatever this class does, because
// nothing here is written anywhere a reload survives.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import {
  AccountKeyResponseError,
  MeApiService,
  type AccountKeyCustodyDto,
  type AccountKeyEntry,
  type MeDto,
} from '@app-core/api/me-api.service';
import { firstValueFrom } from 'rxjs';

import { importAesGcmKey, importHmacSha256Key } from './account-keys';
// The read half of the factor keypair, and the only operation this class runs
// over an entry. It takes the entry itself — `AccountKeyEntry` is composed from
// the interface this function's parameter is typed by — so there is one spelling
// of the two envelopes from the wire to the open.
import { openFactorKeypair } from './factor-keypair';
// The gate that confirms a content key really is the content key, and the
// account's own statement of which factors exist.
//
// **The set it hands back is read, and that is now exactly this class's
// business.** It was not while the manifest was a self-check on this client's
// encapsulation order: the only question then was whether the open *succeeded*,
// and walking the entries would have been this class learning something it had
// no use for. What changed is that the rows beside the manifest are not
// evidence — an operator who can write the database can add a row of their own,
// wrapped under a key-encryption key they chose, and every envelope in it is
// correctly framed and opens perfectly under it. The blob is sealed under a key
// this server has never held, so the set it declares is the only unforgeable
// statement of which factors the account has, and comparing that statement
// against what was served cannot be done without reading one of them.
//
// **What it learns is factor *identifiers* and nothing else** — no credential
// id, no kind, no instant — and only in order to compare two sets that arrived
// in one response. The points travel back beside the identifiers and nothing
// here reads one.
//
// **`FactorManifestWireError` comes across beside it, as a value, because
// `instanceof` is the check.** Two of that function's refusals are made before
// any cipher runs — a wire string its strict decoder will not read, and a sealed
// width outside the window that module reads by, both ends of which are the API
// edge's to the byte and whose floor the stored rule sits below on purpose, as
// `manifestRefusal` argues further down — and neither has observed a byte of
// the account's key material. Those two leave as this type and everything
// from the tag onwards does not, which is the whole of what the gate below is
// offered and the whole of what it needs: one word for a body that arrived in a
// shape this bundle cannot read, another for material that disagrees with
// itself.
//
// **On the type and never on a message**, which is `failureOf`'s rule further
// down applied to the other boundary this class reads. Several sentences are
// thrown for those two refusals and more will be written; a reading pinned to
// one of them stops covering the rest in silence, and a rewording somewhere else
// becomes the edit that turns a reload into a permanent dead end.
import {
  FactorManifestWireError,
  openFactorManifest,
  type FactorPublicKey,
} from './factor-manifest';
// This device's memory of how far an account has already moved, which is the
// one thing on this path that no value carried by a response can supply.
//
// Whoever can replay a manifest can replay the epoch beside it, and the pair
// verifies exactly as it did the day it was written — so the only party that can
// tell a stale `(manifest, epoch)` from a current one is a party that watched
// the account pass it, and on a browser that party is a device. Two functions
// and no service: there is no state here beyond the store.
import {
  highestRotationEpochSeen,
  recordRotationEpochSeen,
} from './rotation-epoch-record';
// The index grammar: the operation the third member delegates to, the refusal
// it opens with, and the binding as a **type — and never the list of four**.
// Which pair a value belongs to, and which tenancy it is keyed inside, are the
// caller's facts, exactly as the binding below is: this class is handed one
// already assembled and has no business enumerating them or asking anybody
// which tenancy this is, and a `type` specifier inside the clause crosses
// nothing into the bundle that a branch here could read.
//
// **`refuseInvalidIndexBinding` is called for itself and never for a value**,
// which is the shape `refuseInvalidBinding` has next door and the shape both of
// them were given for one reason: a caller that wants the refusal and not the
// bytes asks the codec for the refusal, rather than calling a builder and
// dropping what it built. The older form is a statement whose only visible
// effect is a throw — what a reader deletes on the next tidy-up with every round
// trip in the suite still green.
//
// So all three operations below judge their argument through a refusal the
// codec that owns the grammar exports for the purpose, and none of them judges
// it in a shape of its own. The alternative was always a copy of the check
// here, refused for the reason the clause below gives about the eight: it would
// put the four pairs inside the one file that must be able to say it lists none
// of them. What makes an exported refusal safe to lean on is that the module's
// own computing path calls the same function, so the check this class applies
// cannot drift from the check an index is actually computed under.
import {
  computeBlindIndex,
  refuseInvalidIndexBinding,
  type BlindIndexBinding,
} from './blind-index';
// The two operations, the binding they take, and the two members that let this
// class judge a caller — and deliberately not `NARRATIVE_FIELDS`. Which table
// and which column a value belongs to is the caller's fact; this class is
// handed one already assembled and has no business enumerating them — importing
// the list would put the eight pairs inside the one file that must be able to
// say it names none of them.
//
// `refuseInvalidBinding` is called for itself, and it replaced a discarded call
// to `narrativeFieldAssociatedData`: a builder called for a throw is a statement
// whose only visible effect is a throw, which is the shape a reader deletes on
// the next tidy-up with every round trip in the suite still green. It also
// covers the whole binding rather than the row id alone, and it stops this file
// building associated data the codec then builds again.
//
// `NarrativeFieldMisuseError` is the type `openField`'s `catch` re-throws on,
// which is the only thing that keeps a caller's own defect out of `unreadable`.
import {
  NarrativeFieldMisuseError,
  openNarrativeField,
  refuseInvalidBinding,
  sealNarrativeField,
  type NarrativeFieldBinding,
} from './narrative-cipher';
import type {
  BlindIndexValue,
  NarrativeText,
  SealedField,
} from './narrative-text';

/**
 * Whether the account's keys are held, and whether an attempt to hold them is in
 * flight.
 *
 * Three words and no fourth: a failed attempt is `'locked'` again, with
 * {@link AccountKeyCustodyService.unlockFailure} saying why. A `'failed'` member
 * here would be a state a template has to handle beside `'locked'` while meaning
 * exactly the same thing about what the app can read.
 */
export type AccountKeyStatus = 'locked' | 'unlocking' | 'unlocked';

/**
 * Why an unlock did not end in custody, and the five are **never** collapsed.
 *
 *   * `unopened` — the keys were read and none of them opened under the factor
 *     presented. The way forward is another factor.
 *   * `unreachable` — no usable answer came back at all: a network that never
 *     reached a server, a 5xx, a timeout. The way forward is the same factor
 *     again in a minute.
 *   * `unauthenticated` — the server *answered*, and the answer was that this
 *     browser may not read these envelopes: a 401, or the 403 a locked session
 *     and the CSRF control give. The way forward is neither of the other two —
 *     signing in again is the only thing that changes it.
 *   * `unrecognised` — the server answered and this browser did not recognise
 *     the answer. Like `unauthenticated` it is a statement about **this read**
 *     and not about the factor or the network, and its way forward is to
 *     **reload this tab**: a reload is the one act in the product that fetches a
 *     different copy of this JavaScript from the static host, so it is the only
 *     thing that can change the answer.
 *   * `inconsistent` — a factor opened, and what came out of it does not agree
 *     with the account's own manifest. It is the only one of the five that no
 *     act of the person's can clear: every factor of an account encapsulates the
 *     same two keys, so another passkey and all ten recovery codes produce the
 *     same pair, in every browser and after every reload.
 *
 * Collapsed, one person is sent hunting for a recovery card over a network that
 * blinked, and the other is sent around a loop that can only ever refuse them.
 * It is `SignInService`'s `refused`/`unknown` split one layer down, and
 * `SessionService`'s `anonymous`/`unreachable` split one layer up — the same
 * rule three times, because the mistake is available at all three.
 *
 * **The third word arrived with `EXPECTS_UNAUTHENTICATED` on the read**, and it
 * is that change's other half rather than an extra state. While the request was
 * unmarked, a 401 was answered by `sessionExpiryInterceptor` navigating to
 * `/welcome`, so whatever this class published about it was never read by
 * anybody. Marked, this is the only place that answer is read at all — and
 * `unreachable` is false about it by that word's own definition, since a 401 is
 * a usable answer from a server that was reached. A rule its own vocabulary
 * contradicts is one somebody eventually "corrects" in the wrong direction.
 *
 * **The fourth word is that same sentence applied to the refusal
 * `getAccountKeys` makes about a body.** It used to fall to `unreachable`, and
 * `unreachable` is false about it in exactly the way it is false about a 401:
 * the server was reached and it answered. What the person was told instead was
 * *try again in a minute*, which cannot ever succeed — the next minute runs the
 * same bundle against the same route, and it will refuse the same body. The two
 * are indistinguishable from inside a `catch` and are told apart only by the
 * type that boundary throws, which is why {@link AccountKeyResponseError} is a
 * type at all rather than a message.
 *
 * **That fourth word is `unrecognised` and was `outdated`, and the rename is
 * the difference between reporting and diagnosing.** The other four say what
 * was *observed* — nothing opened, nothing answered, the answer was a refusal,
 * the two halves disagree. `outdated` named a **cause**, and named it from
 * evidence that cannot carry it: the boundary also refuses the retired bare
 * array, which is a *newer* bundle reading an *older* route. There "this tab is
 * running an older version" is false, and the remedy it offers — reload, get
 * this same bundle again — is a loop. A reload is still the first thing to try,
 * because it is the only act that can change the answer at all; what the word
 * no longer does is assert which side is stale.
 *
 * **The fifth word is the one that had no way to exist.** A manifest that did
 * not open was reported as `unopened`, whose remedy is *present another factor*
 * — advice that cannot work, over a state that survives every reload in every
 * browser. One altered byte in a stored manifest was therefore a permanent
 * lockout with a screen telling the person to go and find another passkey.
 *
 * **The word is about the material and not about the factor, and that is what
 * let its four neighbours land on it rather than growing this union.** A
 * response carrying no manifest at all, a manifest whose tag did not verify, a
 * served factor set disagreeing with the set the manifest declares, and an epoch
 * below one this device has already watched the account pass are one observation
 * — the account's material does not agree with itself — and they share one next
 * step, which is that nothing this person holds changes it. Five words for five
 * next steps, and these four are one.
 *
 * **Where the fourth word and the fifth meet is the manifest, and the line
 * between them is whether a cipher ran.** A manifest that arrived in a shape
 * this bundle cannot read — a wire value its decoder refuses, a sealed width
 * outside the window this bundle reads by, both ends of which are the API
 * edge's and whose floor the stored rule sits below, as the gate below sets out
 * layer by layer — is `unrecognised` like every other body
 * this client could not read: no key was touched, so nothing whatever about the
 * account's material was observed, and a reload is the one act that changes the
 * answer. Everything from the tag onwards is `inconsistent`, because a plaintext
 * that authenticated was written by somebody holding the content key, and a
 * refusal over it is this client's own writer disagreeing with this client's own
 * reader. Told to a version skew, *nothing you hold will change this* is a
 * permanent lockout over a state one reload clears, which is the same mistake
 * the `unopened`/`inconsistent` split was made to stop one layer up. What holds
 * the line is a type the manifest module throws, never a sentence either side
 * agrees to keep.
 */
export type UnlockFailure =
  | 'unopened'
  | 'unreachable'
  | 'unauthenticated'
  | 'unrecognised'
  | 'inconsistent';

// What one entry hands back when it is the factor: the account's two keys, as
// key objects and never as material.
//
// It is not exported and never will be. The pair travels from the open, through
// the gate that confirms it, to the two fields — three frames inside this file —
// and a type anybody outside could write down is the first half of a member that
// hands one out.
interface HeldKeys {
  readonly contentKey: CryptoKey;
  readonly indexKey: CryptoKey;
}

// What the gate over the account's material answers with, where `null` is the
// whole of "it agrees with itself".
//
// **A boolean is what stood here, and it could not carry this.** Two of
// `openFactorManifest`'s refusals happen before a cipher runs and are facts about
// the answer this read brought back rather than about the account's keys, so the
// gate has two words to give and a predicate has one. Returning the word itself,
// rather than a second boolean beside the first, is what keeps the caller from
// re-deriving a reading the gate already made.
//
// **Two of the five and never the other three, taken from `UnlockFailure` with
// `Extract` rather than written out again.** A member renamed over there leaves
// this alias with nothing to answer and every return below stops compiling,
// where a second spelling would be a union that quietly stopped agreeing with
// the one the service publishes. The three it leaves out are answers this gate is
// in no position to give: nothing about the factor a person presented, the
// network, or a request the server refused is visible from inside a body that has
// already arrived.
type ManifestRefusal = Extract<UnlockFailure, 'inconsistent' | 'unrecognised'>;

@Injectable({ providedIn: 'root' })
export class AccountKeyCustodyService {
  // **ECMAScript `#` fields, not TypeScript `private`, and this is the first of
  // the two places in `src/` that use them** — `register.service.ts` is the
  // other, and holds the same pair between the wrapping and the 201 for the
  // same reason. `private` is a compile-time annotation and
  // nothing else: it is erased on the way out, so `(custody as never)['contentKey']`
  // reads the field at runtime with the compiler's blessing, and so does any
  // devtools panel, any `JSON.stringify` of the instance and any structured
  // clone of it. `#contentKey` is unreachable from outside the class body by the
  // language — not by review, not by a lint rule, and not by anybody's
  // discipline.
  //
  // Everywhere else in this client that distinction costs more than it buys and
  // the project's `private` is right. Here it is the whole point: this is the
  // one class whose fields hold the account's master keys, and it is worth being
  // the one file that reads differently.
  //
  // Applied to *every* field rather than to the two that matter, because a class
  // with two spellings of "private" invites a reader to think the difference is
  // stylistic and pick either. It is not, and the way to say so is to use one.
  readonly #api = inject(MeApiService);

  // **Two `CryptoKey` objects, and no field anywhere in this class is typed
  // `Uint8Array`.** `openFactorKeypair` hands back bytes; those bytes are a local
  // that dies inside the import in the same statement that produces a key, and
  // the import zero-fills them on its way past. A field holding them would keep
  // the plaintext of both account keys alive for the life of the tab, on an
  // object every injector in the app can reach — and it would work perfectly,
  // which is why nothing would ever notice.
  //
  // **Both are read now, and no suppression is left on either.** `#contentKey`
  // has `sealField` and `openField`; `#indexKey` has the third operation, and
  // the directive that stood over it while that body was a throw went out with
  // the throw. That is not tidying: `reportUnusedDisableDirectives` is on, so a
  // directive kept past its reason is itself an error, which is what makes a
  // suppression here a thing with an expiry rather than a thing with a habit.
  //
  // It is worth recording what the suppression was for, because the next field
  // this class grows will arrive read by nothing and the question will come back.
  // The one edit that satisfies the linter on its own terms is an accessor,
  // which is precisely the member `#forget` argues must never exist: a getter
  // turns a key nobody can serialise into a key anybody can decrypt a whole
  // ledger with. So the answer is a suppression carrying its reason, never a
  // getter — and the reason expires the day the field's operation acquires a
  // caller, exactly as this one's did.
  #contentKey: CryptoKey | null = null;
  #indexKey: CryptoKey | null = null;

  // **No signal holds a key, and the shape of this class is that sentence.**
  // What a template renders is lockedness, so lockedness is what is a signal;
  // the keys are ordinary fields because nothing renders them and nothing may.
  // A signal on an injectable is one `effect()` away from being logged by
  // somebody debugging a re-render, and the value that would be logged here
  // opens the account's whole keyspace. It is the rule `register.service.ts`
  // keeps about its eleven key-encryption keys, stated as a field declaration
  // instead of as care.
  readonly #status = signal<AccountKeyStatus>('locked');
  readonly #failure = signal<UnlockFailure | null>(null);

  // The generation an in-flight attempt belongs to. Bumped by everything that
  // changes custody, so an attempt that resolves after a `lock()` — or after a
  // second `unlock()` under a different factor — finds the world moved and drops
  // what it opened instead of publishing it. Without it, signing out while an
  // unlock is in flight leaves the account unlocked a few hundred milliseconds
  // later, by a promise nobody is holding.
  #generation = 0;

  public readonly status: Signal<AccountKeyStatus> = this.#status.asReadonly();

  public readonly unlockFailure: Signal<UnlockFailure | null> =
    this.#failure.asReadonly();

  /**
   * Reads this session's factor keypairs and opens one under
   * `keyEncryptionKey`.
   *
   * **It returns `void`, and that is enforcement rather than a signature that
   * happens to be convenient.** A `Promise<void>` is awaitable, and every caller
   * has an obvious place to put the `await`: each of them arrives here straight
   * out of a ceremony an authenticator has just agreed to, with a screen to move
   * on to. So the round trip would land between a factor that worked and
   * whatever follows it, and one refactor later that `await` grows a `catch` —
   * at which point a key that did not open has become a ceremony that failed.
   * On the way into the account that reads as an authentication failure, which
   * must never happen, because only `anonymous` may bounce anybody out of an
   * account. On a screen inside the account it reads as a device that did not
   * work, and sends somebody off to retry an authenticator that was never the
   * problem. A caller beyond the first strengthens that argument rather than
   * weakening it: the rule has to hold at every call site, and the only way to
   * hold it at all of them is to leave nothing there to await. Unreturned, the
   * attempt can only be observed through {@link status} and
   * {@link unlockFailure}, which are exactly the two facts a caller is entitled
   * to.
   *
   * The key-encryption key is a **parameter and never a field**. Retained, this
   * service could re-unlock with no factor presented at all, which destroys the
   * property the whole design rests on: a page reload locks the account, and
   * getting back in costs a ceremony. It is not stored, not copied and not named
   * anywhere but this call's own frame.
   */
  public unlock(keyEncryptionKey: CryptoKey): void {
    // Custody ends the moment an unlock starts, so "status is not `unlocked`"
    // implies "this instance holds no key" at every instant. The alternative —
    // keep the old keys until the new ones arrive — is a service that reports
    // `unlocking`, fails, reports `locked`, and is still holding both keys.
    const generation = this.#forget('unlocking');

    void this.#attempt(keyEncryptionKey, generation);
  }

  /**
   * Takes custody of keys the caller already holds, without a round trip.
   *
   * Registration is the case and for now the only one: it draws the account's
   * keys itself, so a browser that has just created an account is holding the
   * two values this class exists to hold, and asking the server to hand back
   * envelopes it has only just written — to open them under a key-encryption key
   * it has only just derived — would be a round trip whose whole purpose is to
   * arrive back where it started.
   *
   * The keys arrive as `CryptoKey`, never as bytes, so the caller has already
   * been through the two doors in `account-keys.ts` and this class has no way to
   * be handed material of the wrong width or the wrong extractability.
   */
  public adopt(contentKey: CryptoKey, indexKey: CryptoKey): void {
    // Bumped through `#forget`, so an unlock already in flight cannot land on
    // top of keys a caller has just handed over.
    this.#forget('locked');
    this.#hold(contentKey, indexKey);
  }

  /**
   * Ends custody. The keys are dropped and the account is locked until a factor
   * opens it again.
   *
   * Idempotent, and called for its effect rather than for a state change: a
   * sign-out on a locked account is not a mistake, and a `lock()` that only did
   * something when `unlocked` would be one branch away from a `lock()` that
   * skipped an in-flight attempt.
   */
  public lock(): void {
    this.#forget('locked');
  }

  /**
   * Seals `plaintext` under the account's content key, bound to `binding`, and
   * hands back the wire value the column stores.
   *
   * Answers `locked` when this browser is holding no content key, and when the
   * account's content key was **replaced** while the cipher ran — but not when
   * custody merely ended, where the wire is kept; the guard below says why.
   *
   * Rejects on a binding the codec refuses — a table and column that are not one
   * of its pairs, a row id in any spelling but the canonical one — because that
   * is a caller's mistake about a value it read off a row and not a state
   * anybody can be told about. **Only the refusals a person can act on become a
   * result**; a caught throw rendered as a sentence is a bug wearing a UI, shown
   * to somebody who can do nothing whatever with it.
   */
  public async sealField(
    binding: NarrativeFieldBinding,
    plaintext: string,
  ): Promise<SealedField> {
    // **The argument is judged before custody is, on all three of this class's
    // operations, and in each of them the judgement is a refusal the owning
    // codec exports.** Ordered the other way, the one caller defect
    // that is unrecoverable — a row id in a spelling no later read of that row
    // reproduces — would be reported to an unlocked tab and swallowed by a
    // locked one, which is to say reported exactly where nobody is looking for
    // it. Whether a factor has been presented is not a fact about whether the
    // caller assembled its binding correctly.
    //
    // Called for itself, so what is wanted is the refusal and not a value
    // nobody used. It judges all three fields of the binding — the pair, and the
    // row id's spelling — and this file holds no second definition of any of
    // them: a copy here would pass every case a round trip can see, because the
    // half that drifted would still seal and still open everything it had
    // written itself.
    refuseInvalidBinding(binding);

    const contentKey = this.#contentKey;

    if (contentKey === null) {
      // No cipher is reached and nothing is asked of the API. A `sealField`
      // that read the route to find out whether it could seal would work
      // perfectly and would put a round trip, and a 401, behind every save in
      // the product.
      return { state: 'locked' };
    }

    const wire = await sealNarrativeField(contentKey, plaintext, binding);

    // **What is compared on the way out of a seal is the key, and never the
    // generation — the counter is the wrong instrument here, and that is the
    // argument rather than a check written differently on one side.**
    //
    // A ciphertext is not plaintext. What an open publishes into a tab whose
    // keys were dropped mid-cipher is narrative content the tab is no longer
    // entitled to, which is why the read below compares the counter and drops
    // both of its answers. A wire value is entitled to nobody: it is readable
    // only under the key it was sealed under, and the question this frame has to
    // answer is the narrower one of *whose* key that now is.
    //
    // Two interruptions, and they must not answer alike. A `lock()` mid-seal
    // drops the account's keys and puts nothing in their place — the wire is
    // still that account's, so handing it back discards no work and misleads
    // nobody, and answering `locked` over it would silently throw away text
    // somebody had just typed while signing out. An `adopt()` mid-seal publishes
    // **another account's** keys, and the wire in flight was sealed under keys
    // this tab no longer holds: returned, it invites the caller to write one
    // account's ciphertext into a row belonging to the next, where nothing in
    // the product will ever open it and nothing on the server can see that it
    // happened.
    //
    // The generation counter cannot tell those apart — it is bumped by both, by
    // design, because everything that changes custody bumps it. So the
    // comparison is about **key identity**: keep the answer while the account
    // holds the very key object this seal ran under, or holds none at all; drop
    // it only when the key was replaced. Object identity is the right test and
    // not an approximation of one — `#hold` is the only writer, `CryptoKey` is
    // opaque, and re-adopting the same object is the same account.
    const held = this.#contentKey;

    if (held !== null && held !== contentKey) {
      return { state: 'locked' };
    }

    return { state: 'sealed', wire };
  }

  /**
   * Opens `wire` under the account's content key and `binding`.
   *
   * Answers `locked` when this browser is holding no content key — including
   * when custody ended while the cipher was running — and `unreadable` when a
   * key was there and this value did not open under it. Rejects on a binding the
   * codec refuses and on an extractable content key, for the reason
   * {@link sealField} gives: this class turns into a result only the refusals a
   * person can act on, and everything else keeps throwing.
   */
  public async openField(
    binding: NarrativeFieldBinding,
    wire: string,
  ): Promise<NarrativeText> {
    // The same gate `sealField` opens with, and here it is also *outside* the
    // `try` below. Everything the codec refuses before it reaches a cipher is a
    // caller's defect, and a defect caught and rendered is a bug wearing a UI:
    // it puts a sentence about damaged text in front of somebody who can do
    // nothing about it, over a row that is perfectly fine. Only a ciphertext
    // that really did not open may become a word this class hands to a screen.
    refuseInvalidBinding(binding);

    const contentKey = this.#contentKey;

    if (contentKey === null) {
      // **`locked`, never `unreadable`.** Nothing was judged, so nothing may be
      // blamed: the value is very probably fine and the only thing missing is a
      // factor, which brings every other field on the screen back with it.
      return { state: 'locked' };
    }

    // **Where the two pre-cipher refusals now sit, and what holds each there.**
    // The binding refusal is above, over the held-key check, and its ordering is
    // held **by construction**: it is a statement in this body, so there is no
    // arrangement of these lines in which a locked tab reaches the cipher
    // without passing it — and it is pinned besides, by the case that feeds a
    // refused spelling to an account holding nothing. The extractable-key
    // refusal cannot sit here at all, and that is not an omission: when no key
    // is held this frame has already returned, so there is no key to judge. It
    // necessarily arrives from **inside** the codec, which is where the one
    // definition of it lives, and the `catch` below is what stops it landing as
    // a sentence about damaged text.
    //
    // Read before the cipher, so that what is compared afterwards is the world
    // this answer was computed in.
    const generation = this.#generation;
    let opened: NarrativeText;

    try {
      opened = {
        state: 'text',
        value: await openNarrativeField(contentKey, wire, binding),
      };
    } catch (error: unknown) {
      // **A caller's defect keeps throwing, and everything else is one silent
      // symptom.** `NarrativeFieldMisuseError` is the codec's word for a refusal
      // it made *about the call* before any cipher ran — a binding this grammar
      // cannot be built over, a content key whose bytes can be read back out —
      // and none of it is a claim about the value stored in that column.
      // Swallowed into `unreadable`, such a defect arrives on screen as a
      // sentence about damaged text: a bug wearing a UI, in front of somebody
      // who can do nothing whatever about it, over a row that is perfectly fine.
      //
      // **Re-thrown on the type rather than re-checked above the `try`.** A copy
      // of a refusal here covers the one case somebody thought of and no other;
      // the type covers the class, including the refusals this codec grows next,
      // and it cannot drift from what the codec actually refuses because it *is*
      // what the codec refused.
      if (error instanceof NarrativeFieldMisuseError) {
        throw error;
      }

      // Total and silent, for the reason `#open`'s catch is: a wrong key, a
      // ciphertext bound to another row, altered bytes, a wire value the strict
      // decoder refuses and bytes that authenticated but are not UTF-8 are one
      // symptom by design. The caller learns the value is unusable and learns
      // nothing else, because anything finer is an oracle over data it was never
      // given — and there is no branch here that could act on the difference
      // even if the platform offered one.
      opened = { state: 'unreadable' };
    }

    // **Checked after the cipher and before either answer is published**, the
    // way `#attempt` checks it before publishing a pair of keys, and against the
    // same counter rather than a second mechanism. A `lock()` that lands while
    // the platform is working leaves this frame holding text a tab is no longer
    // entitled to, and returning it would hand narrative content to a browser
    // whose keys were dropped before the answer arrived. `unreadable` is dropped
    // by the same line: the account is not open, so a word that claims the rest
    // of the row is fine would be a claim about a state this frame has left.
    return generation === this.#generation ? opened : { state: 'locked' };
  }

  /**
   * Computes the blind index of `plaintext` for `binding` under the account's
   * index key, and hands back the value a lookup or a column keys on.
   *
   * **It takes a binding carrying no row id, and that omission is the whole
   * operation.** The index has to be *equal* for equal values across rows — that
   * is what a uniqueness constraint over a column, and a lookup that finds the
   * row somebody has just typed, are both asking of it — so a row inside the
   * message would make every value unique by construction: still stable, still
   * the same width, still looking exactly like a working index, and an answer to
   * no query anybody ever writes. It is the deliberate inverse of what
   * {@link sealField} and {@link openField} require, where the row is carried
   * precisely so that two rows can never share a value, and the two rules being
   * opposites is why the codec for this is a module of its own rather than the
   * one beside it with a parameter.
   *
   * **What the binding does carry is the tenancy, and this class is told it
   * rather than asking.** The index key is drawn once per account, so two
   * tenancies of one account would otherwise key one value to one digest and let
   * anybody holding both rows see that they hold the same word. The identifier
   * comes down the argument because the alternative is an injected collaborator
   * that knows which tenancy this is — an edge into the session module, which
   * closes a cycle and puts the rule this whole class is built on one call away
   * from being undone by somebody reusing what was already there. Nothing here
   * learns it, holds it or has a default for it.
   *
   * Reads the account's **index** key where those two read the content key, and
   * answers `locked` on the same terms: this browser is holding no key, and the
   * way forward is a factor. Which of the two was missing is invisible to a
   * caller by design — the pair is taken in one `adopt` or one unlock and
   * dropped in one `lock`, so a screen that can seal can always index, and there
   * is no state in which it can do one and not the other.
   *
   * **Answers `locked` as well when custody moved while the MAC ran, for either
   * reason — where {@link sealField} keeps its answer after a plain `lock()`.**
   * The guard below says why the two neighbours need opposite instruments; it is
   * stated there rather than here because it is the one thing about this member
   * a reader is most likely to get backwards.
   *
   * Rejects on a binding the codec refuses — a pair that is not one of the four
   * it lists, a tenancy in any spelling but the canonical one — because that is
   * a caller's mistake about what it is asking for, and not a state anybody can
   * be told about. **Only the refusals a person can act on become a result**,
   * the rule {@link sealField} states and this one inherits.
   */
  public async blindIndex(
    binding: BlindIndexBinding,
    plaintext: string,
  ): Promise<BlindIndexValue> {
    // **The binding is judged before custody is, the way it is on both
    // operations above and for the reason `sealField` gives there.** Ordered the
    // other way, a caller's defect is reported to an unlocked tab and swallowed
    // by a locked one — reported, that is, exactly where nobody is looking for
    // it — and whether a factor has been presented is not a fact about whether
    // the caller assembled its argument correctly. The judgement is the codec's
    // in all three cases, and this file holds no second copy of the four pairs,
    // and no copy of the tenancy spelling, to make it with.
    //
    // Called for itself, so what is wanted is the refusal and there is no value
    // to drop: the module that owns this grammar exports the refusal, so nothing
    // here builds a message in order to throw it away. `async` is what turns
    // that refusal into a rejection — the same rule both codecs keep at their
    // own doors, and the reason this word is not decoration around a single
    // `await`.
    refuseInvalidIndexBinding(binding);

    const indexKey = this.#indexKey;

    if (indexKey === null) {
      // No MAC is reached and nothing is asked of the API. The second is the one
      // worth stating: an operation that read the route to find out whether it
      // could index would work perfectly, and would put a round trip and a
      // possible 401 behind every lookup in the product.
      return { state: 'locked' };
    }

    // Read before the MAC, so that what is compared afterwards is the world this
    // answer was computed in.
    const generation = this.#generation;
    const value = await computeBlindIndex(indexKey, binding, plaintext);

    // **The generation, and never the key identity `sealField` compares. The two
    // neighbours need opposite instruments, and a reader who assumes the three
    // operations behave alike will get this backwards — so it is written out
    // rather than left to be inferred from either body.**
    //
    // A seal interrupted by a plain `lock()` **keeps** its answer. A wire value
    // is readable only under the key it was sealed under, so it stays this
    // account's whatever the tab does next: handing it back discards no text
    // somebody had just typed and misleads nobody. It drops only on a
    // *replacement*, where the value in flight belongs to an account this tab no
    // longer holds — which is why identity is the right test there and the
    // counter, bumped by `lock()` and `adopt()` alike by design, cannot make the
    // distinction at all.
    //
    // An index **drops in both cases**, because of what the value is *for*
    // rather than how it was made. It is not something the caller keeps beside
    // the key that produced it; it goes straight into a query or a column. One
    // computed under a key the account no longer holds matches no row — so the
    // lookup comes back **empty rather than failing**, a silence over data that
    // is all still sitting there, with nothing on either side of the wire able
    // to see that it happened. Both interruptions must therefore answer alike,
    // and the counter is the instrument that treats them alike.
    return generation === this.#generation
      ? { state: 'computed', value }
      : { state: 'locked' };
  }

  // The two reads and the trial, off the main path because `unlock` returns
  // nothing.
  //
  // **It never calls anything on `SessionService`.** A key that will not open is
  // not a session that ended: the person is signed in, the server answered, and
  // what failed is the factor they presented. Publishing `anonymous` from here
  // would sign somebody out of an account they are demonstrably inside — and on
  // the `unreachable` branch it would do it over one blinked request, which is
  // precisely the reading that class's fourth state exists to refuse. The
  // failure is published as a state, and the state is the handling.
  //
  // **Which account this is arrives from the API, and the identity read is a
  // step of the gate rather than an errand beside it.** The epoch record below
  // is filed per account, and the only per-account identifier a browser ever
  // holds is the budget the session is scoped by. `SessionService` publishes it
  // as a signal four characters away, and reading that signal is the tempting
  // edge this whole class is built on the other side of — that class injects
  // this one, so the edge closes a cycle. It would also be wrong on its own
  // terms: the signal is `null` at the instant `unlock()` is called on the
  // sign-in path, because `established()` starts its own read without awaiting
  // it and sign-in unlocks on the line after, so the first unlock of every
  // session would be filed under nothing.
  async #attempt(
    keyEncryptionKey: CryptoKey,
    generation: number,
  ): Promise<void> {
    let answers: readonly [AccountKeyCustodyDto, MeDto];

    try {
      // **Two reads, one `try`, one reading of what went wrong — and the
      // sharing is the property rather than a saving of three lines.** A second
      // `catch` written over the identity read, for a log line or a message of
      // its own, would answer a refused identity read with whatever word that
      // catch happened to pick — and `unreachable` is the one it would most
      // plausibly pick, false by that word's own definition over a server that
      // was reached and answered.
      //
      // **Cases pin that mapping over the identity read specifically, which is
      // what makes a split here go red rather than quiet**: a pair over 401 and
      // 403 asserting `unauthenticated`, beside one over a request that never
      // arrived asserting `unreachable`. What the sharing buys on top of them is
      // the rest of the reading, which no case can reach from this side. Routed
      // through `failureOf`, "a 401 or a 403 is `unauthenticated`, a body this
      // client cannot read is `unrecognised`, and everything else is
      // `unreachable`" is true of both reads **by construction**: there is one
      // reading of a failed read in this class, and both reads go through it.
      //
      // Together rather than in series, because neither answer is an input to
      // the other request and somebody is waiting in front of a screen for both.
      answers = await Promise.all([
        firstValueFrom(this.#api.getAccountKeys()),
        firstValueFrom(this.#api.getSessionOwner()),
      ]);
    } catch (error: unknown) {
      // Never `unopened`: nothing about a read that did not come back says
      // anything about the factor the person presented, and telling them to go
      // and find their recovery card over a body nobody could read is the worse
      // of the two wrong answers. A refused *body* is `unrecognised` — the
      // server answered and this client did not recognise the answer, which is
      // a fact about this read and not about the factor or the network.
      this.#fail(AccountKeyCustodyService.failureOf(error), generation);

      return;
    }

    const [custody, owner] = answers;
    // Read as `unknown`, because a declared response type is an assertion about
    // JSON rather than a check of it, and `''` is the absence of an answer
    // wearing the type of one.
    const budgetId: unknown = owner.budgetId;

    // **A 200 carrying no budget is `unrecognised`, and it is refused here
    // rather than shrugged off.** `SessionService` reads the same absence as
    // `null` and carries on, deliberately: a body that says there is a session
    // and whose it is still says that with one member missing, and taking the
    // session status down over a version skew would sign somebody out. This
    // path cannot make that trade. An unlock that does not learn which account
    // it is opening can record nothing, and an unlock that records nothing
    // cannot refuse a replay — so shrugging here would hand an operator a way
    // to switch the rollback refusal off for every browser at once, by dropping
    // one member from a response nothing else on this path reads.
    //
    // `unrecognised` and not `unreachable`, by that word's own definition: the
    // server was reached and it answered. What was observed is an answer this
    // client could not read, and a reload is the one act that changes it.
    if (typeof budgetId !== 'string' || budgetId.length === 0) {
      this.#fail('unrecognised', generation);

      return;
    }

    // **Every entry, in turn, each under its own `factorId`.** An account
    // holding one passkey and nothing else is answered with one entry, so the
    // list of one is what a reader optimises into `entries[0]` — and it works,
    // forever, on that kind of account. The route answers the whole account
    // rather than the session's own credential, so an ordinary account — a
    // passkey beside a card of ten codes — is answered with **eleven**, of
    // which exactly one opens under the factor just presented. What `entries[0]`
    // does there is read some other factor's envelopes under this factor's
    // key-encryption key: the open fails to authenticate, the loop that would
    // have found the right pair is not there, and somebody who presented a
    // valid factor is told their account cannot be opened.
    //
    // The associated data is rebuilt from `entry.factorId` and never from
    // anything this client remembers, because that identifier *is* what the two
    // envelopes were sealed against. Exactly one entry opens; the rest fail to
    // authenticate, which is the AEAD doing what the binding is for rather than
    // an error worth telling apart. Two AEAD opens and one key agreement per
    // entry is a cost nobody can measure.
    let opened: HeldKeys | null = null;

    for (const entry of custody.factors) {
      opened = await this.#open(keyEncryptionKey, entry);

      if (opened !== null) {
        break;
      }
    }

    if (opened === null) {
      // **An empty list lands here, and it is `unopened` rather than an error.**
      // The route answers no factors both for a session it cannot see and for an
      // account carrying none, indistinguishably and on purpose — so there is
      // nothing to tell apart, and inventing a fifth word would mean claiming a
      // difference this client was never told. The next step is the same one
      // every other `unopened` has: present another factor.
      this.#fail('unopened', generation);

      return;
    }

    // **The gate, and it runs once, here, after the loop and never inside it.**
    //
    // Inside the loop it would be a second reason an entry can be skipped, and
    // the two would be indistinguishable in the answer: "no factor opened"
    // (`unopened`, present another one) and "a factor opened and the account's
    // material does not agree with itself" (nothing another factor can help
    // with) would arrive at the same place by the same road. Out here they are
    // two branches with two causes.
    //
    // **The cost of that order, written down rather than left to be
    // discovered.** Somebody who presents the wrong factor to a response
    // somebody else has shaped is told `unopened` — *present another factor* —
    // because the loop runs first and the loop's verdict about that factor is
    // true. It is the cheaper of the two mistakes. The alternative announces
    // that this account's material is not to be trusted to a browser that has
    // not yet shown it holds anything of the account's at all, over a factor
    // nothing has judged.
    // **The word comes back from the gate rather than being decided here, and
    // that is the change a boolean could not carry.** Almost everything it
    // judges is `inconsistent`, and that is a word of its own because the remedy
    // is the opposite of `unopened`'s. `unopened` says *present another factor*.
    // Every factor of this account encapsulates the same two keys, so a pair
    // that will not open this manifest will not open it under any of them: no
    // passkey, none of the ten recovery codes, no browser and no number of
    // reloads. The same is true of the three refusals beside that one — a
    // response carrying no manifest, a served set that disagrees with the
    // declared one, an epoch this device has already watched the account pass —
    // because none of them is a fact about the person's authenticator either.
    // Reported as `unopened`, any of the four sends somebody to spend their
    // whole recovery card on a door that cannot open, with the screen telling
    // them to keep going.
    //
    // **The one answer that is not that word is a manifest no cipher ever
    // reached**, where `inconsistent`'s *nothing you hold will change this* is a
    // sentence about a state this browser never observed, told to somebody a
    // reload would have let straight in. The gate owns which of the two it is,
    // because it is the only frame that sees what the manifest module threw; all
    // this frame may do is publish it.
    const refusal = await AccountKeyCustodyService.manifestRefusal(
      opened,
      custody,
      budgetId,
    );

    if (refusal !== null) {
      this.#fail(refusal, generation);

      return;
    }

    // **The epoch is recorded here: after every refusal above, and before the
    // generation check below. Both halves are decisions.**
    //
    // **After the refusals**, because a record written from a body nothing has
    // judged is an oracle rather than an observation. An operator answering
    // `rotationEpoch: 9999` beside anything at all would push this device's
    // high-water mark past every epoch the account will ever reach, and this
    // browser would then refuse the account's own genuine manifest forever,
    // with copy that offers the person nothing to do. One request, and that
    // browser is finished with that account.
    //
    // **Before the generation check**, because the two answer different
    // questions. That check is about whether *this attempt* may publish keys
    // into a world that has moved on from it; this line is about what the
    // **device** observed, and the device observed this account at this epoch
    // whatever happened next. The record only ever rises, so writing it costs a
    // later attempt nothing — while the other order would let anybody able to
    // time a `lock()` against an unlock in flight suppress the observation, and
    // suppressing observations is the whole of the attack this memory answers.
    recordRotationEpochSeen(budgetId, custody.rotationEpoch);

    // Checked *after* every cipher and before the keys are published, because
    // the world can have moved while they ran.
    if (generation !== this.#generation) {
      return;
    }

    this.#hold(opened.contentKey, opened.indexKey);
  }

  // Which word the account's material earns, or `null` when it agrees with
  // itself: the manifest against the content key an entry just handed over,
  // against what this device has already watched this account pass, and against
  // the rows served beside it.
  //
  // **Four refusals, one word, and they are ordered rather than arranged.** Each
  // reads something the one before it proved: there is a manifest, it opened, so
  // the epoch it was sealed at is the epoch of a blob somebody holding the
  // content key really wrote, and the set it declares is that same blob's.
  //
  // **A fifth reading sits inside the second and earns the other word, and it is
  // not a fifth refusal.** Before any cipher runs, `openFactorManifest` turns
  // away a wire string its strict decoder will not read and a sealed value
  // outside **its own** width window.
  //
  // **Both ends of that window are the API edge's, to the byte.** Four handlers
  // reach that edge — the one that creates an account, the one that finishes a
  // passkey registration, the one that regenerates the code card, and the one
  // that revokes a passkey — and each hands its manifest member to a single
  // decoder, which measures the *decoded* bytes against this framing's floor of
  // 29, its version byte, and the entity's ceiling of 4096. The ceiling is
  // exact rather than slack: that decoder measures a second time once the
  // buffer exists, because a gate over the encoded length overshoots by a byte
  // or two. So a width this module would refuse is a width the request would be
  // refused for, and 29 **is** a number the server enforces. What a sentence
  // about it owes a reader is *which* layer.
  //
  // **The stored rule is wider at the bottom, and that is a division of labour
  // rather than a disagreement.** The column admits anything from one byte up —
  // `length(manifest) between 1 and 4096` — and the entity behind it restates
  // that floor and that ceiling and nothing narrower, because what a check over
  // a blob can state declaratively is that the bytes are there, never what they
  // frame. A one-byte value is not a short manifest; it is not an envelope at
  // all, and the floor that knows so is this framing's, applied where the bytes
  // arrive. Nothing sealed is involved on either side of that: 29 is a *total
  // length*, so the edge measures it without reading a byte of what the account
  // encrypted.
  //
  // Both shorthands cost more than the clause they save — the window written as
  // the column's, and 29 written as a number no server could hold. A reader who
  // checks one sentence in a file like this against the layer it points at and
  // finds it false stops trusting every other sentence in it, including the
  // ones holding the split below.
  //
  // Neither refusal has touched a key, so neither is a statement about this
  // account's material at all — they are facts about the shape of the answer
  // this read brought back, which is `unrecognised`'s own definition, and the
  // way forward is the reload that word asks for. Filed under `inconsistent`
  // they are a permanent dead end with advice that can never work, over a state
  // a different bundle clears on its first try.
  //
  // **Told apart by the type the module throws, and never by what it says.** The
  // alternative is this function re-applying that module's decoder and width
  // rules above its own `try` — a second, weaker definition of what a manifest
  // is, sitting one file from the one that runs, drifting the day either moves.
  // A reading matched on message text is worse again: it pins prose, and there is
  // more than one sentence on each side of the line.
  //
  // **The four that are `inconsistent` stay one observation, and this change may
  // not grow a second word among them.** An absent manifest, a tag that did not
  // verify, an epoch rolled back and a set disagreement are told apart by nobody
  // here on purpose — they share one next step, and a person offered four
  // shades of *nothing you hold will change this* is being handed a diagnosis
  // this client cannot make.
  //
  // **1. A response carrying no manifest is refused, and this branch let such a
  // body straight through until the story that closed it.** The bypass was right
  // while the manifest was a
  // self-check on *this client's own* encapsulation order: an adversary who
  // could shape the response lost nothing by switching a check off that only
  // ever caught a build of this bundle, and refusing `null` would have locked
  // out rows written before the first manifest landed — a permanent lockout no
  // factor, no reload and no sign-in clears, over a defect the frozen keypair
  // vectors catch in the suite anyway. Both halves of that are now false, and
  // neither quietly. The three refusals below are the ones an operator has to
  // get past, and **every one of them is switched off by a body answering
  // `null`** — so the bypass was not one branch of this gate, it was the gate.
  //
  // **What holds the refusal is not that the server would decline to send a
  // `null`; it sends one quite readily.** The read behind this body answers 200
  // carrying a `null` manifest, a zero epoch and an empty factor list for an
  // account that holds neither, and that route's own prose spends a paragraph
  // arguing a 404 there would be the mistake. A missing manifest row is a 500
  // only where one is being *promoted* to the next epoch — another route,
  // another question, and not the read this gate is judging. What is true
  // instead is that no account this product brings into existence can be in
  // that state: registration files the first manifest at epoch 1 in the same
  // save as the session, and each of the four paths that move a factor set
  // carries one. So a `null` arriving here cannot be describing an account the
  // product made; it is a response somebody shaped, and a response somebody
  // shaped is the one thing this gate exists to refuse.
  //
  // What that costs, plainly: a row that genuinely predates manifests is now
  // permanently unopenable in every browser — no factor, no reload and no
  // sign-in changes it, and nothing the person does either. That is accepted
  // because no such row can exist in a product that has never stood a
  // production environment up, and because the alternative leaves the gate
  // switchable off by the very party it is pointed at.
  //
  // **2. A manifest that reached the cipher and did not open**, which is the
  // check that proves the content key is the content key — and the refusals ahead
  // of the cipher, which prove nothing of the sort, are the paragraph above.
  // A decapsulation that succeeds proves the 64
  // bytes are what was encapsulated to this factor and **nothing whatever**
  // about which half of them is which: the two keys sit in one plaintext, told
  // apart by position alone and with no separator, so a client that
  // encapsulated them the other way round produces a value of exactly the right
  // width, leading with exactly the right version byte, which stores, reads back
  // and opens. The manifest is sealed *under the content key*, so opening it is
  // the one runtime check anywhere that a reversed pair fails.
  //
  // **3. An epoch below one this device has already watched this account
  // reach.** Every check above passes on a replayed `(manifest, epoch)` pair,
  // because it is not forged — it is correctly sealed, correctly framed, opens
  // under the content key and declares a set that really was this account's, and
  // it is simply from before. Whoever can replay the manifest can replay the
  // epoch beside it, so no value in the response can tell; only a party that
  // watched the account move past it can, and on a browser that party is a
  // device. Not-lower rather than strictly-higher: an account that has not
  // rotated answers the same epoch on every unlock.
  //
  // **4. A served set that is not exactly the set the manifest declares, both
  // directions.** Opening the manifest says nothing about *who else* can open
  // this account. The rows beside it are not evidence — an operator who can
  // write the database can add one of their own, wrapped under a key-encryption
  // key they chose — while the blob is sealed under a key the server has never
  // held, so the set it declares is the only unforgeable statement of which
  // factors exist. The two directions are two different events and neither is
  // the other's mirror: a **served** factor the manifest does not account for is
  // a row somebody added, and a **declared** factor that was not served is a row
  // somebody removed or a set this client is being shown half of. Checked one
  // way only, the other event passes cleanly.
  //
  // `static` because it reads nothing off the instance: what it is handed is the
  // key this attempt opened and the body this attempt read, and neither has been
  // published anywhere yet. Reading `#contentKey` instead would be reading a
  // field that is still `null` at this point in the attempt.
  //
  // **`private static` and not `static #`, which the class header's rule would
  // otherwise ask for.** A `static` ECMAScript private member on a decorated
  // class is TS18036 — the decorator and the identifier cannot both be there —
  // so the choice is this spelling or no `static` at all. `failureOf` below is
  // spelled the same way for the same reason, and neither of them holds anything
  // worth hiding: the two key fields are what `#` is for here, and both are
  // instance fields.
  private static async manifestRefusal(
    opened: HeldKeys,
    custody: AccountKeyCustodyDto,
    budgetId: string,
  ): Promise<ManifestRefusal | null> {
    if (custody.manifest === null) {
      return 'inconsistent';
    }

    let declared: readonly FactorPublicKey[];

    try {
      declared = await openFactorManifest(
        opened.contentKey,
        custody.manifest,
        custody.rotationEpoch,
      );
    } catch (error: unknown) {
      // **The one distinction this `catch` draws, and it is drawn on the type.**
      // A value this browser could not read at all was refused before the cipher
      // ran, so nothing about the account's keys was observed — which is what
      // makes it a report about this read and not about the material, and what
      // makes a reload the act that can change it.
      if (error instanceof FactorManifestWireError) {
        return 'unrecognised';
      }

      // Silent and total past that line, for `#open`'s reason. A reversed pair, a
      // manifest sealed at another epoch, a replayed one from before a rotation,
      // a single flipped bit and every plaintext the grammar forbids are one
      // symptom by design: every one of them came off bytes somebody holding the
      // content key wrote, and there is no branch here that could act on the
      // difference between them.
      return 'inconsistent';
    }

    const seen = highestRotationEpochSeen(budgetId);

    // A device holding no record of this account answers `null`, and that is
    // first-visit rather than a refusal — it cannot detect a rollback at all
    // (ASM-016). This memory narrows the window; it does not close it.
    if (seen !== null && custody.rotationEpoch < seen) {
      return 'inconsistent';
    }

    // **Two `every`s over two sets, never two lists walked in step.** The
    // manifest's own order is fixed by its grammar, but the response's is not:
    // the read behind this body sorts on the factor id today and says outright
    // that what it owes is that determinism and nothing about the particular
    // sequence. A positional comparison passes on any response whose rows happen
    // to arrive in the manifest's own order — which is exactly what today's sort
    // hands it, so nothing short of that sort changing would ever surface the
    // defect — and then refuses whoever it lands on with a word telling the
    // person nothing they hold can help.
    //
    // **Set equality and not a count, and what that admits is stated rather
    // than papered over.** Two served rows carrying one identifier satisfy both
    // directions. That is the right answer rather than a hole: the manifest
    // holds one public key per identifier and every path that stages a seal
    // keys on the identifier, so a duplicate row buys whoever wrote it nothing
    // it did not already have.
    //
    // **A rule does forbid the duplicate, so the tempting justification — that a
    // count would refuse a response nothing forbids — is not available.** The
    // factor identifier is the primary key of the table these rows come from,
    // table-wide rather than scoped per owner, and the read projects one row per
    // stored record: two served rows under one identifier is a response that
    // store cannot produce. Only the argument changes. A count is refused
    // because it measures cardinality where the rule is about a set, and the
    // sentence above carries that on its own.
    const declaredIds = new Set(declared.map((factor) => factor.factorId));
    const servedIds = new Set(custody.factors.map((entry) => entry.factorId));

    return declared.every((factor) => servedIds.has(factor.factorId)) &&
      custody.factors.every((entry) => declaredIds.has(entry.factorId))
      ? null
      : 'inconsistent';
  }

  // One entry's keypair, opened and imported, or `null` if this is not the
  // factor.
  //
  // The `catch` is deliberately total and deliberately silent. A wrong factor, a
  // private half that did not unwrap, an ephemeral point the curve refuses, a
  // corrupted envelope and a value that is not base64url are one symptom by
  // design — the client learns the entry is not usable and learns nothing about
  // why — and there is no branch here that could act on the difference even if
  // the platform offered one.
  async #open(
    keyEncryptionKey: CryptoKey,
    entry: AccountKeyEntry,
  ): Promise<HeldKeys | null> {
    try {
      // **The entry itself, not two members picked off it.** `AccountKeyEntry`
      // is composed from the interface this parameter is typed by, so the row
      // that arrived is the argument — one spelling of the two envelopes from
      // the wire to the open, and no call site that can pass them in the wrong
      // order. The `factorId` beside it is the associated data both envelopes
      // were bound with; it comes off this entry and never off anything this
      // client remembered.
      const keys = await openFactorKeypair(
        keyEncryptionKey,
        entry.factorId,
        entry,
      );

      // **The bytes die inside the imports, in the statement that produces the
      // keys.** Both doors zero-fill the material they are handed, and both are
      // called before this `await` resolves — so there is no path on which
      // `keys` survives this statement holding anything but zeroes, including
      // the path where one of the two imports rejects. Nothing between the
      // unwrap and here names those bytes, and nothing after it can.
      //
      // **Two doors, because the two keys are two different keys.** The content
      // key encrypts, so it goes through the AES-GCM door. The index key is what
      // the blind index is computed under, and a blind index is HMAC-SHA-256 —
      // so it goes through the HMAC door, and taking the shorter route through
      // `importAesGcmKey` would produce a key object that cannot sign, cannot be
      // re-imported, and would leave the index key on this service as **bytes**,
      // which is the one thing this class refuses to hold.
      const [contentKey, indexKey] = await Promise.all([
        importAesGcmKey(keys.contentKey),
        importHmacSha256Key(keys.indexKey),
      ]);

      return { contentKey, indexKey };
    } catch {
      return null;
    }
  }

  // Drops both keys, publishes `status`, clears the failure, and returns the
  // generation the caller now owns.
  //
  // **No public member returns a key, and none ever will.** Non-extractability
  // stops the *bytes* leaving; it does nothing about a caller that holds the key
  // object and decrypts a whole ledger into a log line. What this class grows
  // instead is *operations* that delegate — `sealField` and `openField` are the
  // first two, the blind index is the third — and never an accessor. That is why
  // the two fields below are read by nothing outside this file: there is no
  // getter to read them with, and adding one is the change this paragraph exists
  // to argue against.
  #forget(status: AccountKeyStatus): number {
    this.#contentKey = null;
    this.#indexKey = null;
    this.#failure.set(null);
    this.#status.set(status);
    this.#generation += 1;

    return this.#generation;
  }

  #hold(contentKey: CryptoKey, indexKey: CryptoKey): void {
    this.#contentKey = contentKey;
    this.#indexKey = indexKey;
    this.#failure.set(null);
    this.#status.set('unlocked');
  }

  // How a failed read is read, and the two branches are the only two this
  // class can tell apart.
  //
  // **It is written here rather than borrowed from `SessionService`**, which
  // makes the same 401/403 reading four lines away. Importing it is the one
  // thing this class may not do: the dependency runs one way, and the moment
  // custody reaches into the session module the two are in a cycle and the rule
  // the whole class is built on — a key that will not open is not a session
  // that ended — is one `readingOf` call away from being broken by somebody
  // reusing what is already there. Two small copies that cannot reach each
  // other are the cheaper mistake than one shared reading that closes the edge.
  //
  // The words differ from `SessionService`'s on purpose, too, which is what
  // stops the copies being folded together later: that class reads a refusal as
  // `anonymous`, a statement about *who is asking*. This one reads it as
  // `unauthenticated`, a statement about *this read* — published while the
  // session status is untouched, because only the interceptor and the sign-out
  // path may move that.
  private static failureOf(error: unknown): UnlockFailure {
    if (
      error instanceof HttpErrorResponse &&
      (error.status === 401 || error.status === 403)
    ) {
      return 'unauthenticated';
    }

    // **The refusal `getAccountKeys` makes over a body it could not read, and it
    // is checked on the type rather than on a message.** It used to fall through
    // to `unreachable` below, where the copy is "try again in a minute" — advice
    // that cannot ever work, because the next minute runs this same bundle
    // against that same route and gets refused the same way. A reload is the one
    // act that can change the answer, and `unrecognised` is the word that stays
    // true whichever side of the wire moved: this boundary refuses a body from a
    // newer server *and* the retired bare array from an older one, so a word
    // naming this tab as the stale side would be false half the time.
    //
    // The type and not a `message.includes(…)`: a message is prose, six of them
    // are written at that boundary and more will be, and a reading that matched
    // one of them would quietly stop covering the rest.
    if (error instanceof AccountKeyResponseError) {
      return 'unrecognised';
    }

    // Status `0` for a request that never reached a server, every 5xx, a
    // timeout, and a `404` this route never gives.
    return 'unreachable';
  }

  #fail(failure: UnlockFailure, generation: number): void {
    // A failure from an attempt the world has moved past says nothing about the
    // state it moved to, and publishing it would lock an account a later unlock
    // had already opened.
    if (generation !== this.#generation) {
      return;
    }

    this.#failure.set(failure);
    this.#status.set('locked');
  }
}
