// Where the account's two keys live once a factor has opened them, and the one
// place in this client that holds them past the ceremony that produced them.
//
// The account owns one content key and one index key; every recovery factor
// stores its own wrapped copy of both. A browser that has just proved a factor
// holds a key-encryption key and nothing else, so this service does the only
// thing left: it reads that credential's envelopes, tries each in turn, and keeps
// what came out as two `CryptoKey` objects. Nothing here encrypts anything —
// nothing in this product is encrypted yet. What is built is the custody, and the
// operations that will delegate to it arrive with the epic that needs them.
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
// locks with no ceremony on screen to unlock it again. Nothing goes red. The
// only visible symptom is an account that was readable a moment ago and is not
// now.
//
// **Ending custody is `lock()`, and it is a method rather than a lifetime**,
// which is the trade root-providing makes: an injector nobody destroys cannot
// forget anything on its own, so whoever ends the session has to say so. That is
// the honest shape anyway — the browser can end a session in ways no injector
// observes, and a page reload ends custody whatever this class does, because
// nothing here is written anywhere a reload survives.
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
 * Why an unlock did not end in custody, and the two are **never** collapsed.
 *
 *   * `unopened` — the keys were read and none of them opened under the factor
 *     presented. The way forward is another factor.
 *   * `unreachable` — no usable answer came back at all: a network that never
 *     reached a server, a 5xx, a timeout, a body this client refused. The way
 *     forward is the same factor again in a minute.
 *
 * Collapsed, one person is sent hunting for a recovery card over a network that
 * blinked, and the other is sent around a loop that can only ever refuse them.
 * It is `SignInService`'s `refused`/`unknown` split one layer down, and
 * `SessionService`'s `anonymous`/`unreachable` split one layer up — the same
 * rule three times, because the mistake is available at all three.
 */
export type UnlockFailure = 'unopened' | 'unreachable';

@Injectable({ providedIn: 'root' })
export class AccountKeyCustodyService {
  // **ECMAScript `#` fields, not TypeScript `private`, and this is the one place
  // in `src/` that uses them.** `private` is a compile-time annotation and
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
  // **Nothing reads them yet, and the two suppressions below are the price of
  // the rule three paragraphs down.** Both are written and neither is read,
  // because the operations that will read them — seal this field, compute this
  // index — arrive with the encryption epic, and nothing in this product is
  // encrypted today. The one edit that would satisfy the linter on its own terms
  // is an accessor, which is precisely the member `#forget` argues must never
  // exist: a getter turns a key nobody can serialise into a key anybody can
  // decrypt a whole budget with. So the rule is suppressed here rather than
  // obeyed, in the two places it applies, with the reason beside it — the same
  // shape as every other capability this client has landed one commit ahead of
  // its caller.
  // eslint-disable-next-line no-unused-private-class-members -- read by the operations the encryption epic brings; an accessor is the forbidden alternative
  #contentKey: CryptoKey | null = null;
  // eslint-disable-next-line no-unused-private-class-members -- read by the operations the encryption epic brings; an accessor is the forbidden alternative
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
   * happens to be convenient.** A `Promise<void>` is awaitable, and the caller
   * this will have is a sign-in: somebody would await it, and a round trip would
   * land on the path between a verified assertion and the app. One refactor
   * later that `await` grows a `catch`, and a key that did not open becomes an
   * authentication that failed — which must never happen, because only
   * `anonymous` may bounce anybody out of an account. Unreturned, the attempt
   * can only be observed through {@link status} and {@link unlockFailure}, which
   * are exactly the two facts a caller is entitled to.
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
    } catch {
      // Every way the read can end badly is one word, and it is not `unopened`.
      // A refused body belongs here too, and deliberately: this client could not
      // read what came back, which says nothing whatever about the factor the
      // person presented, and telling them to go and find their recovery card
      // over a version skew is the worse of the two wrong answers.
      this.#fail('unreachable', generation);

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
  // object and decrypts a whole budget into a log line. When encryption lands,
  // this class grows *operations* that delegate — seal this field, compute this
  // index — and never an accessor. That is why the two fields below are read by
  // nothing outside this file today: there is no getter to read them with, and
  // adding one is the change this paragraph exists to argue against.
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
