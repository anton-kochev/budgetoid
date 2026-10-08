// The flow behind **Cancel the erasure** on `/app/settings`: the browser's
// ability, the challenge, the passkey, and the one cancelling request. See
// docs/business-logic/erasure.md and docs/design/components.md.
//
// It is `ErasureFlowService`'s shape — a phase, one word for why the last press
// ended, and one predicate read by both the attribute and the handler — and
// the same order, for the same reason: every word raised before the cancelling
// request exists makes *the erasure is still scheduled* a fact about this
// client. Two things differ, and each is a property of the route rather than a
// preference:
//
// - **A declined assertion answers `refusal: "assertion"`**, so it is `refused`
//   without a probe; only a 401 without that word is read against one unmarked
//   `GET /api/me`. `erasure-cancellation-outcome.ts` owns the reading.
// - **The cancellation is idempotent** — a second one after a lost `204`
//   answers `204` again — so `undetermined` keeps the control live instead of
//   withdrawing it. Nothing is retried for the person either way.
//
// **Not `providedIn: 'root'`.** `SettingsComponent` provides it, so an attempt
// abandoned on the screen dies with the screen, and the screen's teardown
// aborts a press that has not posted. A request already out is left to land:
// its `204` still tells `SessionService` nothing is scheduled, so the notice on
// every other screen goes with it.
//
// **It injects none of the screen's other flows.** `EmailChangeFlowService`
// reads this one for its two holds, and the screen composes the rest; an edge
// back would be a cycle.
//
// **Nothing secret lives on this instance.** The ceremony result is a local of
// one method and only its `payload` is read; the key-encryption key beside it
// is never named here.
import { HttpErrorResponse } from '@angular/common/http';
import {
  DestroyRef,
  Injectable,
  computed,
  inject,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { MeApiService } from '@app-core/api/me-api.service';
import { ReauthenticationApiService } from '@app-core/api/reauthentication-api.service';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type { PasskeyRequestOptionsJson } from '@app-core/security/webauthn-encoding';
import { SessionService } from '@app-core/session/session.service';
import { firstValueFrom } from 'rxjs';
import {
  cancellationFailureOf,
  type CancellationRequestFailure,
} from './erasure-cancellation-outcome';
import {
  challengeFailureOf,
  type ChallengeRequestFailure,
} from './erasure-outcome';

/**
 * Where a press is.
 *
 * * `idle` — nothing is running; the last press, if any, has ended.
 * * `asserting` — the challenge is being fetched or the device is being asked.
 *   Nothing has been posted.
 * * `cancelling` — the cancelling request is out. It stays here while a `401`
 *   on it is being told apart from an ended session.
 * * `cancelled` — the last press was answered `204`. Left only by a press,
 *   which {@link ErasureCancellationFlowService.pressable} allows once a
 *   schedule stands again — a Google sign-in can file a new one after this one
 *   was withdrawn.
 */
export type ErasureCancellationPhase =
  | 'idle'
  | 'asserting'
  | 'cancelling'
  | 'cancelled';

/**
 * Why the last press did not cancel the erasure, one word per sentence the
 * section can say.
 *
 * The ceremony's words first, raised before anything is posted. `cancelled` is
 * renamed `dismissed`, because beside a control named *Cancel the erasure* the
 * ceremony's word reads as the act having happened; `failed` is
 * `ceremony-failed` and `duplicate` folds into it, the erasure flow's reasons.
 * Then the challenge request's, then the cancelling request's three — `probe`
 * is an instruction to this flow, never a word.
 */
export type ErasureCancellationFailure =
  | 'unsupported'
  | 'dismissed'
  | 'no-prf'
  | 'ceremony-failed'
  | ChallengeRequestFailure
  | Exclude<CancellationRequestFailure, 'probe'>;

// How the challenge leg ended: with the server's options, or with the word the
// press ends on — `null` when the flow has nothing to say.
type ChallengeOutcome =
  | { readonly ok: true; readonly options: PasskeyRequestOptionsJson }
  | { readonly ok: false; readonly failure: ChallengeRequestFailure | null };

@Injectable()
export class ErasureCancellationFlowService {
  private readonly reauthentication = inject(ReauthenticationApiService);
  private readonly me = inject(MeApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);

  private readonly phaseSignal: WritableSignal<ErasureCancellationPhase> =
    signal<ErasureCancellationPhase>('idle');
  private readonly failureSignal: WritableSignal<ErasureCancellationFailure | null> =
    signal<ErasureCancellationFailure | null>(null);

  // The press in flight's handle, until it posts or ends. One per press, so an
  // abort reaches the attempt that is running and never a later one.
  #press: AbortController | null = null;

  public readonly phase: Signal<ErasureCancellationPhase> =
    this.phaseSignal.asReadonly();
  public readonly failure: Signal<ErasureCancellationFailure | null> =
    this.failureSignal.asReadonly();

  /**
   * Whether a press is running: the challenge, the ceremony or the cancelling
   * request. False once cancelled — nothing is running then. Read by the
   * control's `aria-busy`, and by the email change, whose trip to Google
   * would cut any of it short.
   */
  public readonly working: Signal<boolean> = computed(() => {
    const phase = this.phaseSignal();

    return phase === 'asserting' || phase === 'cancelling';
  });

  /**
   * The passkey check is running — the challenge and the ceremony, and not the
   * cancelling request after them. Other controls on the screen read it as a
   * hold, because the browser runs one passkey check at a time.
   */
  public readonly asking: Signal<boolean> = computed(
    () => this.phaseSignal() === 'asserting',
  );

  /**
   * Whether a press would start anything: nothing is running, and either the
   * last press did not cancel or a schedule stands again since it did. **One
   * predicate with one owner** — the screen composes its holds on top of it,
   * and {@link cancel} refuses on it again, because Material's click-halt is
   * applied to anchors only.
   *
   * After a `204` it opens only on an instant, never on `null` or `'unread'`:
   * a press with nothing said to be scheduled spends a nonce on nothing.
   *
   * `undetermined` leaves it open, unlike the erasure: the route is
   * idempotent, so pressing again is how somebody finds out.
   */
  public readonly pressable: Signal<boolean> = computed(() => {
    const phase = this.phaseSignal();

    if (phase === 'idle') {
      return true;
    }

    const scheduled = this.session.scheduledErasure();

    return (
      phase === 'cancelled' && scheduled !== null && scheduled !== 'unread'
    );
  });

  constructor() {
    // The screen that provides this is going. A press that has not posted is
    // abandoned with it; one that has is left to land.
    inject(DestroyRef).onDestroy(() => {
      this.#press?.abort();
      this.#press = null;
    });
  }

  /**
   * Runs the whole act for one press: the challenge, the passkey, and the one
   * cancelling request.
   */
  public cancel(): void {
    // A press this refuses started nothing, so the last word stays where it
    // was: a press clears the previous line as it starts, and nothing else
    // clears it.
    if (!this.pressable()) {
      return;
    }

    this.failureSignal.set(null);

    // **Before the challenge**, the only position that costs nothing: a
    // re-authentication challenge is a nonce the server persisted. Through
    // `end`, so a press from `cancelled` lands at rest like one from `idle` —
    // the section draws a word only at rest, and left on `cancelled` this one
    // would say nothing.
    if (!this.ceremony.available()) {
      this.end('unsupported');

      return;
    }

    this.phaseSignal.set('asserting');

    const press = new AbortController();

    this.#press = press;

    void this.run(press.signal);
  }

  private async run(abort: AbortSignal): Promise<void> {
    const challenge = await this.challenge();

    if (abort.aborted) {
      this.phaseSignal.set('idle');

      return;
    }

    if (!challenge.ok) {
      this.end(challenge.failure);

      return;
    }

    const ceremony = await this.assert(challenge.options, abort);

    // **The last point at which abandoning means anything.** Past this line
    // the cancelling request is sent.
    if (abort.aborted) {
      this.phaseSignal.set('idle');

      return;
    }

    this.#press = null;

    if (!ceremony.ok) {
      this.end(ErasureCancellationFlowService.failureOf(ceremony.failure));

      return;
    }

    this.phaseSignal.set('cancelling');

    // **The visit this request is sent in, read now** — after the ceremony,
    // which can outlast a sign-out and a sign-in, and before the request,
    // whose answer describes the account as it was when it went out.
    // `SessionService` publishes the `204` only into this visit.
    const sentUnder = this.session.sessionToken();

    try {
      // Only the payload; `cancelScheduledErasure` projects the five members
      // it names. `defaultValue`, because a `204` answered with no emission
      // would otherwise reject as an `EmptyError` and read as `undetermined`
      // over a cancellation that landed.
      await firstValueFrom(
        this.me.cancelScheduledErasure(ceremony.value.payload),
        { defaultValue: undefined },
      );
    } catch (error: unknown) {
      // **No retry, automatic or otherwise.**
      const reading = cancellationFailureOf(error);

      if (reading !== 'probe') {
        this.end(reading);

        return;
      }

      this.end((await this.sessionHasEnded()) ? null : 'undetermined');

      return;
    }

    // The phase on any `204`, then the one owner of the schedule this tab
    // knows — whether or not the screen is still here, and whether or not that
    // owner still publishes it: a `204` from a visit that has ended changes
    // nothing there, and the screen draws its result only once nothing is
    // scheduled.
    this.phaseSignal.set('cancelled');
    this.session.erasureCancelled(sentUnder);
  }

  // The server's own options, or the word the press ends on. Unmarked, so a
  // `401` is the interceptor's to judge, and the flow says nothing over it
  // whatever the verdict. One that ends the session takes the tab to Welcome;
  // on one the judge kept the tab stays, the press ends silently and Cancel is
  // live again — known and logged as a backlog item, not fixed here.
  private async challenge(): Promise<ChallengeOutcome> {
    try {
      return {
        ok: true,
        options: await firstValueFrom(
          this.reauthentication.getRequestOptions(),
        ),
      };
    } catch (error: unknown) {
      return { ok: false, failure: challengeFailureOf(error) };
    }
  }

  // The ceremony, with a rejection read as the device not finishing: a throw
  // out of it has still posted nothing.
  private async assert(
    options: PasskeyRequestOptionsJson,
    abort: AbortSignal,
  ): Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>> {
    try {
      return await this.ceremony.assertPasskey(options, abort);
    } catch {
      return { ok: false, failure: 'failed' };
    }
  }

  // Whether a `401` without the assertion word was a session that had already
  // ended. The cancelling request is marked, so the interceptor heard nothing;
  // this probe is **unmarked**, so its own `401` is the interceptor's to judge,
  // and the flow says nothing whatever the verdict — on a kept one that is the
  // silent end {@link challenge} names. Anything else leaves the `401`
  // unexplained:
  // nothing judged the passkey, so `refused` would be false.
  private async sessionHasEnded(): Promise<boolean> {
    try {
      await firstValueFrom(this.me.getMe());

      return false;
    } catch (error: unknown) {
      return error instanceof HttpErrorResponse && error.status === 401;
    }
  }

  // Ends a press that did not cancel anything — or may have, for
  // `undetermined` — back at rest with its word.
  private end(failure: ErasureCancellationFailure | null): void {
    this.#press = null;
    this.phaseSignal.set('idle');
    this.failureSignal.set(failure);
  }

  // The ceremony's five words, mapped onto this flow's four. A `switch` over
  // the closed union, so a sixth word there fails to compile here.
  private static failureOf(
    failure: PasskeyCeremonyFailure,
  ): ErasureCancellationFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'dismissed';
      case 'no-prf':
        return 'no-prf';
      case 'duplicate':
      case 'failed':
        return 'ceremony-failed';
    }
  }
}
