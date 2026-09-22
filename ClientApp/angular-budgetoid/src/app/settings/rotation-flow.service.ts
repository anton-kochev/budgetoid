// The flow behind the **Rotate keys** control on `/app/settings`, and the
// screen-side half of an act whose driver lives in `+core/security`.
//
// It is `AccountUnlockService` one section down, member for member — a boolean,
// one of five words, and the coarser reading laid over both — and the shape is
// deliberate rather than incidental: the two sections on this screen that ask
// the person's own device for something answer the same three questions, and a
// reader who has understood one has understood the other. What differs is the
// act behind them, and all of that is `KeyRotationService`'s.
//
// **It injects the options leg, the ceremony and the driver, and nothing else.**
// No `SessionService` and no `Router`, for the reason `account-unlock.service.ts`
// states at length and which is not restated here: a device that refused looks
// like a person who failed to prove who they are, and the next line a reader
// would write is `session.ended()`. It is wrong at every branch — the server was
// never asked anything by a ceremony, and what failed is a factor.
//
// **Not `providedIn: 'root'`.** This is an *attempt*, and an attempt abandoned
// on a screen should die with the screen. What the attempt produces is not held
// here at all: it goes to the driver, which is root-provided because a run
// outlives every render of the section that started it, and from there to
// `AccountKeyCustodyService`, which holds the keys because they are state of the
// session.
//
// **Nothing secret lives on this instance.** The ceremony is a local, the
// key-encryption key is handed on in the statement it is read in and never given
// a name this class could assign from, and the state below is a boolean, one of
// five words and a boolean derived from the first and the driver's. That is what
// makes the state of this service safe to render.
import {
  Injectable,
  Signal,
  computed,
  inject,
  signal,
  type WritableSignal,
} from '@angular/core';
import { ReauthenticationApiService } from '@app-core/api/reauthentication-api.service';
import { KeyRotationService } from '@app-core/security/key-rotation.service';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
} from '@app-core/security/webauthn-ceremony.service';
import { firstValueFrom } from 'rxjs';

/**
 * Why the press did not get as far as a run, in the words the screen says out
 * loud.
 *
 * The five the design chapter calls "the ceremony's", and they are rendered from
 * the Account keys section's table because a press has posted nothing when any
 * of them is raised: the ceremony is the first thing either entry point does and
 * the begin waits on it. Four of the five are the ceremony's own, one for one,
 * and only `failed` is renamed — to `ceremony-failed`, because `failed` on this
 * surface would read as "rotating failed" rather than "the device did not
 * finish". The ceremony's fifth word, `duplicate`, has no member here: it is an
 * authenticator declining a credential named in an exclusion list, and an
 * assertion carries no such list to decline against.
 *
 * **It is not `UnlockCeremonyFailure` imported, and the equality of the two is a
 * coincidence of what a ceremony can do rather than one being the other.**
 * `SignInService` owns a third union over the same five words for the same
 * reason. Each flow states what *it* can report, so the day one of them gains a
 * word the other two do not silently gain it too.
 *
 * `unknown` is this flow's own and covers a rejection out of a method whose
 * contract is to answer with a result — including the options leg answering
 * nothing at all. There is deliberately no sixth word for that leg: the six
 * words beside these five each say what became of a *run*, and a press that
 * never reached the authenticator has no run to have become anything.
 */
export type RotationCeremonyFailure =
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | 'unknown';

@Injectable()
export class RotationFlowService {
  private readonly api = inject(ReauthenticationApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  // Root-provided, unlike this service, and the difference is the point: a run
  // is state of the **account**, it survives the tab that began it as server
  // state, and nothing about it belongs to a render of this section.
  private readonly rotations = inject(KeyRotationService);

  private readonly busySignal = signal(false);
  private readonly failureSignal: WritableSignal<RotationCeremonyFailure | null> =
    signal<RotationCeremonyFailure | null>(null);

  public readonly busy: Signal<boolean> = this.busySignal.asReadonly();
  public readonly failure: Signal<RotationCeremonyFailure | null> =
    this.failureSignal.asReadonly();

  /**
   * Whether a rotation is in flight — the challenge and the ceremony this
   * service runs, or the run the driver is walking with what the ceremony
   * produced.
   *
   * **One predicate with one owner.** The Rotate control's `disabled`, its
   * `aria-busy` and {@link rotate}'s guard all bind this, and the three content
   * screens read the driver's half of it beside custody's lockedness. Two
   * spellings of one fact drift, and the drift is silent in both directions: a
   * template that narrows draws a live control over a run already going, and a
   * handler that narrows accepts the press behind it. It is the defect the
   * Unlock control paid for once, and it is not paid for twice.
   *
   * **It is also the whole of the driver's re-entrancy guard, and that is by
   * design rather than by omission.** `KeyRotationService.begin()` has none: two
   * concurrent presses would fight over the two key fields a run holds while it
   * is walking an account. The chapter specifies "a run is in flight" as one
   * predicate with one owner, so the owner is the thing that refuses the second
   * press, and it is here.
   */
  public readonly working: Signal<boolean> = computed(
    () => this.busySignal() || this.rotations.running(),
  );

  /**
   * Runs the ceremony and hands what it produced to the driver — beginning a
   * rotation, or finishing the one this account has staged.
   *
   * **One method for one control**, because the section draws one. Which of the
   * driver's two entry points it reaches is decided by whether there is a run to
   * finish, read at the moment of the press rather than passed in: a caller
   * holding that answer since the page loaded is one value able to disagree with
   * what is really staged.
   *
   * The guard is in this method as well as in the screen's `disabledInteractive`
   * attribute, and that is not belt and braces: Material's click-halt applies to
   * anchors only, so on a `<button>` the DOM `disabled` property stays `false`
   * and the press arrives whatever the attribute says. This line is therefore
   * the only thing that refuses it, and it refuses it by reading the same
   * {@link working} the attribute is bound to — so a guard and an attribute
   * covering different halves of a press is a state this pair has no version of.
   */
  public rotate(): void {
    if (this.working()) {
      return;
    }

    // Cleared when the act *starts*, which is the rule every act in this client
    // follows. Cleared anywhere else and the sentence from a cancelled ceremony
    // stands over the press that retried it, telling the reader what went wrong
    // last time while this time is still running.
    this.failureSignal.set(null);

    // **Before the options call**, which is the only position that costs
    // nothing — `SignInService`'s argument, and unlike the Unlock control's it
    // does transfer here. A re-authentication challenge is a nonce the server
    // persisted, so a browser that was never going to finish the ceremony would
    // otherwise spend one on its way to being told exactly what it is told here
    // for free. The Unlock control has no such check because it spends nothing:
    // its ceremony is minted in the browser.
    if (!this.ceremony.available()) {
      this.failureSignal.set('unsupported');

      return;
    }

    this.busySignal.set(true);

    void this.assert();
  }

  // The challenge, the ceremony, and the hand-over to the driver.
  private async assert(): Promise<void> {
    try {
      // The server's own options, unchanged. They carry the challenge this
      // assertion is signed over, and an assertion run against options this
      // client invented is one the begin route refuses.
      const options = await firstValueFrom(this.api.getRequestOptions());
      const ceremony = await this.ceremony.assertPasskey(options);

      if (!ceremony.ok) {
        this.busySignal.set(false);
        this.failureSignal.set(RotationFlowService.failureOf(ceremony.failure));

        return;
      }

      // **The ceremony is read and handed on in one statement**, and never
      // assigned to a field, a signal or a local of this method: there is no
      // name here a later line could assign from, which is what makes the rule
      // structural rather than a habit. A key-encryption key parked on this
      // instance is what wraps the account's factor keypair, sitting one
      // `effect()` or one devtools panel away from being read.
      //
      // **This order, and the pair is the reason.** The driver publishes its
      // first phase synchronously, before its own first `await`, so `busy`
      // clears into a `running` that is already true and no frame exists in
      // which both are false. Cleared first, the section would flash back to
      // its resting state for the moment between this statement and the
      // driver's first signal write, with a second press available in it.
      const run = this.drive(ceremony.value);

      this.busySignal.set(false);

      await run;
    } catch {
      // Nothing above throws in the ordinary course: the ceremony answers with
      // a result rather than an exception, and both driver entry points are
      // documented to resolve rather than reject. What does land here is the
      // options leg answering nothing — a server that is down, a network that
      // blinked — and a rejection nobody predicted. Swallowing either would
      // leave the section on its waiting line for ever.
      this.busySignal.set(false);
      this.failureSignal.set('unknown');
    }
  }

  // Which entry point this press is, and the read that decides it.
  //
  // **`begin` over a staged run is the repair and not a mistake.** That method
  // makes this same read again and carries the staged generation forward rather
  // than drawing one, so a press of **Rotate keys** over a run the section did
  // not know about picks that run up under its own identifier. What a resume
  // must never do is the other direction: a begin posted by a press offering to
  // *finish* would overwrite the staged seals in place, and every row the
  // interrupted run already re-sealed would open under nothing at all.
  private drive(ceremony: PasskeyAssertionCeremony): Promise<void> {
    return this.rotations.staged() === null
      ? this.rotations.begin(ceremony)
      : this.rotations.resume(ceremony);
  }

  // The ceremony's five words, mapped onto this flow's four. A `switch` over the
  // closed union rather than a lookup object, so a sixth word added there fails
  // to compile here instead of arriving as `undefined` on a screen.
  private static failureOf(
    failure: PasskeyCeremonyFailure,
  ): RotationCeremonyFailure {
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
