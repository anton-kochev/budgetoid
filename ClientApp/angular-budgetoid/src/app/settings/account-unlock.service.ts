// The flow behind the **Unlock** control on `/app/settings`.
//
// A page reload leaves the browser holding no account keys — nothing about them
// survives a document — while the session cookie is intact and there is nothing
// to re-authenticate. The person presents their passkey, the authenticator
// derives the key-encryption key, and `AccountKeyCustodyService` opens the
// account's wrapped envelopes with it.
//
// **It injects the ceremony and custody, and nothing else. In particular it
// injects neither `SessionService` nor `Router`, and that absence is the
// structural half of "a refused unlock leaves the session intact".** A service
// holding no way to end a session and no way to navigate cannot do either by
// accident — not today, and not after somebody adds a convenience method next
// year. The reading it defends against is the plausible one: a device that
// refused looks like a person who failed to prove who they are, and the next
// line a reader would write is `session.ended()`. It is wrong at every branch.
// The server was never asked anything here, said nothing, and the cookie it
// issued is untouched; what failed is a factor, and a factor is not a session.
// `AccountKeyCustodyService` states the same rule from the other side and holds
// it the same way, by holding no reference it could break it through.
//
// **Not `providedIn: 'root'`.** This is an *attempt*, and an attempt abandoned
// on a screen should die with the screen — `RegisterService` and
// `SignInService` are provided the same way for the same reason. What the
// attempt produces is not held here at all: it goes to custody, which is
// root-provided because the keys are state of the **session**.
//
// **Nothing secret lives on this instance.** The key is read and handed on in
// the statement it is read in, never given a name this class could assign from,
// and the two signals below hold a boolean and one of five words. That is what
// makes the state of this service safe to render.
import { Injectable, Signal, inject, signal } from '@angular/core';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyFailure,
} from '@app-core/security/webauthn-ceremony.service';

/**
 * Why the unlock did not get as far as a key, in the words the screen says out
 * loud.
 *
 * Four of the five are the ceremony's, one for one — `PasskeyCeremonyFailure`
 * argues why none of them is a synonym of another — and only `failed` is
 * renamed, to `ceremony-failed`, because `failed` on this surface would read as
 * "unlocking failed" rather than "the device did not finish". The ceremony's
 * fifth word, `duplicate`, has no member here: it is an authenticator declining
 * a credential named in an exclusion list, and an assertion carries no such
 * list to decline against.
 *
 * `unknown` is this flow's own, and covers only a rejection out of a method
 * whose contract is to answer with a result. It is **not** the word for a key
 * that opened nothing — that is a fact about a factor, published by
 * `AccountKeyCustodyService.unlockFailure`, and this union deliberately has no
 * member a reader could file it under.
 */
export type UnlockCeremonyFailure =
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | 'unknown';

@Injectable()
export class AccountUnlockService {
  private readonly ceremony = inject(WebauthnCeremonyService);
  // Root-provided, unlike this service, and the difference is the point: what
  // this screen produces is state of the **session**, and a session outlives
  // the screen that unlocked it. `account-key-custody.service.ts` argues the
  // scope at length.
  private readonly custody = inject(AccountKeyCustodyService);

  private readonly busySignal = signal(false);
  private readonly failureSignal = signal<UnlockCeremonyFailure | null>(null);

  public readonly busy: Signal<boolean> = this.busySignal.asReadonly();
  public readonly failure: Signal<UnlockCeremonyFailure | null> =
    this.failureSignal.asReadonly();

  /**
   * Runs the ceremony and hands what it derived to custody.
   *
   * The guard is in this method as well as in the screen's
   * `disabledInteractive` attribute, and that is not belt and braces:
   * Material's click-halt applies to anchors only, so on a `<button>` the DOM
   * `disabled` property stays `false` and the second press arrives here.
   * Without this line it would raise a second system sheet over the first.
   *
   * **There is no `available()` check here, and the omission is the rule.**
   * `SignInService` has one and its own comment says why — a challenge is a
   * nonce the server persisted, so a browser that was never going to finish the
   * ceremony must not spend one on its way to being told what it can be told
   * for free. That is an argument from *ordering*, and it does not transfer:
   * this flow spends nothing. There is no options leg, no nonce and no round
   * trip before the ceremony, so there is nothing a check placed earlier could
   * save. `WebauthnCeremonyService` already guards `available()` itself and
   * answers `unsupported`, which arrives here as the same word by the mapping
   * below — so a copy would be a second enforcement with no observable
   * difference, which is what
   * [ADR 0002](../../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md)
   * refuses.
   */
  public unlock(): void {
    if (this.busySignal()) {
      return;
    }

    // Cleared when the act *starts*, which is the rule every act in this client
    // follows. Cleared only where one is set, the sentence from a cancelled
    // ceremony survives the press that retries it, and the reader is told what
    // went wrong last time while this time is still running.
    this.failureSignal.set(null);
    this.busySignal.set(true);

    void this.derive();
  }

  // The ceremony, and the hand-over that follows it.
  private async derive(): Promise<void> {
    try {
      const ceremony = await this.ceremony.deriveKeyFromLocalAssertion();

      if (!ceremony.ok) {
        this.busySignal.set(false);
        this.failureSignal.set(
          AccountUnlockService.failureOf(ceremony.failure),
        );

        return;
      }

      // **The key is read and handed on in one statement, and that is the whole
      // of this service's custody rule.** It travels as an argument — through
      // here, into {@link hand}, into `AccountKeyCustodyService.unlock` — and
      // is never assigned to a field, a signal or a local of this method. There
      // is no name here a later line could assign from, which is what makes the
      // rule structural rather than a habit. A key parked on this instance is
      // the account's master key sitting one `effect()` or one devtools panel
      // away from being read, on a screen whose state is otherwise safe to
      // render.
      //
      // **This order, and the pair is the reason.** Custody is handed the key
      // *before* `busy` clears, so there is no frame in which both in-flight
      // states are false: cleared first, the screen would flash back to its
      // resting `Unlock` line for the moment between this method finishing and
      // custody publishing `'unlocking'`, and a person pressing again in that
      // frame would raise a second system sheet on top of an attempt already
      // running.
      this.hand(ceremony.value);
      this.busySignal.set(false);
    } catch {
      // Nothing above throws in the ordinary course: the ceremony answers with
      // a result rather than an exception. A rejection here is therefore
      // something nobody predicted, which is what `unknown` is the word for —
      // and swallowing it would leave the screen on its waiting line forever.
      this.busySignal.set(false);
      this.failureSignal.set('unknown');
    }
  }

  // Opens the account's keys, and cannot do anything else.
  //
  // `keyEncryptionKey` is a parameter and reaches nothing but the call below.
  //
  // **The `try` is what keeps this call from being read as a ceremony that
  // failed.** `unlock` is documented not to throw, and this method does not
  // take that on trust — the mechanism here is different from the one
  // `SignInService` guards against and the argument has to be made again for
  // it. There, the call sits in an RxJS `next` handler, and a throw out of one
  // is reported out of band rather than routed to the `error` callback beside
  // it. Here the call sits inside an `async` method, so a throw would **reject
  // that method's promise** and land in {@link derive}'s own `catch` — which
  // would publish `unknown`. A key that did not open would have become a device
  // that did not work: the screen would tell somebody holding a perfectly good
  // authenticator that the ceremony failed, and the retry it invites can only
  // ever end the same way. What the failure really was is readable from
  // `AccountKeyCustodyService.unlockFailure`, which is a fact about a factor
  // and is published there whether this call throws or not.
  private hand(keyEncryptionKey: CryptoKey): void {
    try {
      this.custody.unlock(keyEncryptionKey);
    } catch {
      // Nothing. See above.
    }
  }

  // The ceremony's five words, mapped onto this flow's four. A `switch` over
  // the closed union rather than a lookup object, so a sixth word added there
  // fails to compile here instead of arriving as `undefined` on a screen.
  private static failureOf(
    failure: PasskeyCeremonyFailure,
  ): UnlockCeremonyFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'cancelled';
      case 'no-prf':
        return 'no-prf';
      // Unreachable on this leg and handled anyway, because the union is the
      // ceremony's and not this flow's: `duplicate` is raised by an
      // authenticator declining a credential named in `excludeCredentials`, and
      // an assertion has no such list. It folds into `ceremony-failed` rather
      // than getting a word of its own — a sentence about an exclusion list on
      // the settings screen would describe a thing that did not happen.
      case 'duplicate':
      case 'failed':
        return 'ceremony-failed';
    }
  }
}
