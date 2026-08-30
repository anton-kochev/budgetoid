// Where the account's two keys live once a factor has opened them, and the one
// place in this client that holds them past the ceremony that produced them.
//
// The account owns one content key and one index key; every recovery factor
// stores its own wrapped copy of both. A browser that has just proved a factor
// holds a key-encryption key and nothing else, so this service does the only
// thing left: it reads that credential's envelopes, tries each in turn, and keeps
// what came out as two `CryptoKey` objects. Two operations delegate to what it
// holds — seal this field, open that one — and they are the whole reason no
// caller has any occasion to ask for a key. No column in this product holds an
// envelope yet, so nothing calls them but their spec; the blind index, the third
// operation, is not here at all.
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
  MeApiService,
  type AccountKeyEntry,
} from '@app-core/api/me-api.service';
import { firstValueFrom } from 'rxjs';

import {
  importAesGcmKey,
  importHmacSha256Key,
  unwrapAccountKeys,
} from './account-keys';
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
import type { NarrativeText, SealedField } from './narrative-text';

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
 * Why an unlock did not end in custody, and the three are **never** collapsed.
 *
 *   * `unopened` — the keys were read and none of them opened under the factor
 *     presented. The way forward is another factor.
 *   * `unreachable` — no usable answer came back at all: a network that never
 *     reached a server, a 5xx, a timeout, a body this client refused. The way
 *     forward is the same factor again in a minute.
 *   * `unauthenticated` — the server *answered*, and the answer was that this
 *     browser may not read these envelopes: a 401, or the 403 a locked session
 *     and the CSRF control give. The way forward is neither of the other two —
 *     signing in again is the only thing that changes it.
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
 */
export type UnlockFailure = 'unopened' | 'unreachable' | 'unauthenticated';

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
  // `Uint8Array`.** `unwrapAccountKeys` hands back bytes; those bytes are a local
  // that dies inside the import in the same statement that produces a key, and
  // the import zero-fills them on its way past. A field holding them would keep
  // the plaintext of both account keys alive for the life of the tab, on an
  // object every injector in the app can reach — and it would work perfectly,
  // which is why nothing would ever notice.
  //
  // **One of the two is read now, and the one suppression left is the price of
  // the rule three paragraphs down.** `#contentKey` has readers — `sealField`
  // and `openField` are the operations that delegate to it — so the rule it was
  // suppressed for no longer fires on it, and the directive went with the
  // reason for it. `#indexKey` is still written and read by nothing: the one
  // operation that will read it is the blind index, deferred to a later commit
  // because the grammar its values are computed over is blocked on a revision of
  // the specification. There is deliberately no placeholder for it here — a
  // method that answered something plausible would be an index nothing could
  // tell apart from a real one, and every value it keyed would have to be
  // rewritten once the grammar landed.
  //
  // The one edit that would satisfy the linter on its own terms is an accessor,
  // which is precisely the member `#forget` argues must never exist: a getter
  // turns a key nobody can serialise into a key anybody can decrypt a whole
  // ledger with. So the rule is suppressed here rather than obeyed, with the
  // reason beside it — the same shape as every other capability this client has
  // landed one commit ahead of its caller.
  #contentKey: CryptoKey | null = null;
  // eslint-disable-next-line no-unused-private-class-members -- read by the blind index, which waits on a grammar; an accessor is the forbidden alternative
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
   * Reads this session's wrapped keys and opens them under `keyEncryptionKey`.
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
    // **The binding is judged before custody is, on both operations, and the
    // judgement is the codec's.** Ordered the other way, the one caller defect
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

  // The read and the trial, off the main path because `unlock` returns nothing.
  //
  // **It never calls anything on `SessionService`.** A key that will not open is
  // not a session that ended: the person is signed in, the server answered, and
  // what failed is the factor they presented. Publishing `anonymous` from here
  // would sign somebody out of an account they are demonstrably inside — and on
  // the `unreachable` branch it would do it over one blinked request, which is
  // precisely the reading that class's fourth state exists to refuse. The
  // failure is published as a state, and the state is the handling.
  async #attempt(
    keyEncryptionKey: CryptoKey,
    generation: number,
  ): Promise<void> {
    let entries: readonly AccountKeyEntry[];

    try {
      entries = await firstValueFrom(this.#api.getAccountKeys());
    } catch (error: unknown) {
      // Never `unopened`: nothing about a read that did not come back says
      // anything about the factor the person presented, and telling them to go
      // and find their recovery card over a version skew is the worse of the
      // two wrong answers. A refused *body* is `unreachable` for exactly that
      // reason — this client could not read what came back.
      this.#fail(AccountKeyCustodyService.failureOf(error), generation);

      return;
    }

    // **Every entry, in turn, each under its own `factorId`.** A passkey session
    // is answered with one entry and a recovery-code session with ten, so the
    // list of one is the case a reader will optimise into `entries[0]` — and it
    // works, forever, on every passkey account in the product. What it does to
    // the other kind is read code #1's envelopes under code #7's key-encryption
    // key: the open fails to authenticate, the loop that would have found the
    // right pair is not there, and somebody who redeemed a valid code is told
    // their account cannot be opened. The loop is written now, against a list of
    // one, for exactly that reason.
    //
    // The associated data is rebuilt from `entry.factorId` and never from
    // anything this client remembers, because that identifier *is* what the two
    // envelopes were sealed against. Exactly one entry opens; the rest fail to
    // authenticate, which is the AEAD doing what the binding is for rather than
    // an error worth telling apart. Twenty attempts is a cost nobody can
    // measure.
    for (const entry of entries) {
      const held = await this.#open(keyEncryptionKey, entry);

      if (held === null) {
        continue;
      }

      // Checked *after* the open and before the keys are published, because the
      // world can have moved while the cipher ran.
      if (generation !== this.#generation) {
        return;
      }

      this.#hold(held.contentKey, held.indexKey);

      return;
    }

    // **An empty list lands here, and it is `unopened` rather than an error.**
    // The route answers `[]` both for a session it cannot see and for a
    // credential carrying no factors, indistinguishably and on purpose — so
    // there is nothing to tell apart, and inventing a third word would mean
    // claiming a difference this client was never told. The next step is the
    // same one every other `unopened` has: present another factor.
    this.#fail('unopened', generation);
  }

  // One entry's two envelopes, opened and imported, or `null` if this is not the
  // factor.
  //
  // The `catch` is deliberately total and deliberately silent. A wrong factor, a
  // swapped pair, a corrupted envelope and a value that is not base64url are one
  // symptom by design — the client learns the value is not usable and learns
  // nothing about why — and there is no branch here that could act on the
  // difference even if the platform offered one.
  async #open(
    keyEncryptionKey: CryptoKey,
    entry: AccountKeyEntry,
  ): Promise<{ contentKey: CryptoKey; indexKey: CryptoKey } | null> {
    try {
      const keys = await unwrapAccountKeys(
        keyEncryptionKey,
        entry,
        entry.factorId,
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

    // Status `0` for a request that never reached a server, every 5xx, a
    // timeout, a `404` this route never gives, and the refusal `getAccountKeys`
    // makes over a body it could not read — which is not an `HttpErrorResponse`
    // at all and lands here for that reason as much as for its meaning.
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
