// The flow behind the erasure dialog's commit: the typed word, the challenge,
// the passkey, the one erasing request, and the way off the screen after it.
// See docs/design/components.md, "Erasure dialog", and
// docs/business-logic/erasure.md.
//
// It is `RotationFlowService`'s shape — a phase, one word for why the last press
// ended, and one predicate read by both the attribute and the handler — because
// the two acts on this screen that ask the person's device for a passkey the
// server checks answer the same questions. What differs is what follows the
// ceremony, and here that is one request whose retry is the most dangerous
// thing in the product.
//
// **Not `providedIn: 'root'`.** An attempt abandoned on a screen dies with the
// screen: `SettingsComponent` provides this beside `RotationFlowService` and
// opens the dialog with its own view container, so the dialog's content reaches
// this instance and no other. Dying is not only being dropped: the screen's
// teardown aborts a press that has not posted — the device's prompt comes down
// and nothing more is asked or sent — and so does {@link abandon}, which the
// screen calls when the overlay is closed from outside while the screen stays.
// Once the erasing request is out there is nothing left to abandon.
//
// **Nothing secret lives on this instance.** The ceremony result is a local of
// one method, and only its `payload` is ever read — the key-encryption key
// beside it is never named here, so no line in this class could post it, park
// it on a field or hand it anywhere. The state below is a phase and a word,
// which is what makes it safe to render.
//
// **The order is the property**, and every sentence the dialog shows depends on
// it: the browser's ability first, then the challenge, then the ceremony, then
// the one erasing request. Every word above `refused` is raised before that
// request exists, so the dialog's *nothing was erased* is a fact about this
// client rather than a guess about the server.
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
import { Router } from '@angular/router';
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
import { confirmsErasure } from './erasure-confirmation';
import {
  challengeFailureOf,
  erasureFailureOf,
  type ChallengeRequestFailure,
  type ErasureRequestFailure,
} from './erasure-outcome';

/**
 * Where a press is.
 *
 * * `idle` — nothing is running; the last press, if any, has ended.
 * * `asserting` — the challenge is being fetched or the device is being asked.
 *   Nothing has been posted.
 * * `erasing` — the erasing request is out and nothing can recall it. It stays
 *   here while a `401` on it is being told apart from an ended session.
 * * `erased` — it answered `204`. Terminal: the tab is on its way to Welcome,
 *   and no press is accepted in the window before the screen's teardown closes
 *   the dialog.
 */
export type ErasurePhase = 'idle' | 'asserting' | 'erasing' | 'erased';

/**
 * Why the last press did not erase the account, one word per sentence the
 * dialog can say.
 *
 * The ceremony's words first, raised before anything is posted. `failed` is
 * renamed `ceremony-failed`, because on this surface it would read as "erasing
 * failed", and `duplicate` folds into it: that is an authenticator declining a
 * credential named in an exclusion list, and an assertion carries none. Then
 * the challenge request's own — `unstarted`, and `unrecognised`, which it
 * shares with the erasing request — and the erasing request's three. See
 * `erasure-outcome.ts`, which owns both readings.
 *
 * **It is not `RotationCeremonyFailure` imported.** Each flow states what *it*
 * can report, so the day one gains a word the other does not silently gain it.
 */
export type ErasureFailure =
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | ChallengeRequestFailure
  | ErasureRequestFailure;

// How the challenge leg ended: with the server's options, or with the word the
// press ends on — `null` when the flow has nothing to say.
type ChallengeOutcome =
  | { readonly ok: true; readonly options: PasskeyRequestOptionsJson }
  | { readonly ok: false; readonly failure: ChallengeRequestFailure | null };

@Injectable()
export class ErasureFlowService {
  private readonly reauthentication = inject(ReauthenticationApiService);
  private readonly me = inject(MeApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  private readonly phaseSignal: WritableSignal<ErasurePhase> =
    signal<ErasurePhase>('idle');
  private readonly failureSignal: WritableSignal<ErasureFailure | null> =
    signal<ErasureFailure | null>(null);

  // The press in flight's handle, until it posts or ends. One per press, so an
  // abort reaches the attempt that is running and never a later one.
  #press: AbortController | null = null;

  public readonly phase: Signal<ErasurePhase> = this.phaseSignal.asReadonly();
  public readonly failure: Signal<ErasureFailure | null> =
    this.failureSignal.asReadonly();

  /**
   * Whether a press is in flight — or has erased the account, which no press
   * can follow. Read by the dialog for the host's `disableClose`, for the
   * commit's `aria-busy`, and for Cancel's inert state.
   */
  public readonly working: Signal<boolean> = computed(
    () => this.phaseSignal() !== 'idle',
  );

  constructor() {
    // The screen that provides this is going. A press that has not posted is
    // abandoned with it; one that has is left to finish, because a `204` still
    // has to end the session.
    inject(DestroyRef).onDestroy(() => this.abandon());
  }

  /**
   * Whether a press of the commit with `typed` in the field would start
   * anything: the word matches, nothing is running, and the commit has not been
   * withdrawn.
   *
   * **One predicate with one owner.** The commit's `disabledInteractive`, the
   * dialog's click handler and {@link erase}'s own guard all read this, because
   * Material's click-halt is applied to anchors only: on a `<button>` the press
   * arrives whatever `aria-disabled` says, and a gate written in two places is
   * two gates that can disagree.
   *
   * **`undetermined` closes it for the screen's life.** Erasure is not
   * idempotent to the caller: a second erasing request after a lost `204` is
   * answered `401` — the session went with the account — and would read as
   * *nothing was erased* over an account that is gone. Nothing clears the word,
   * {@link reset} included, so no dialog on this screen can offer that request
   * again. The way forward is a reload, which the sentence names: it asks the
   * server who this is from the start.
   */
  public pressable(typed: string): boolean {
    return (
      confirmsErasure(typed) &&
      !this.working() &&
      this.failureSignal() !== 'undetermined'
    );
  }

  /**
   * Puts the flow back at rest for a new dialog — clearing every word but
   * `undetermined` — or does nothing while a press is running or has erased
   * the account.
   *
   * **A new dialog is a fresh attempt.** A refusal from the last one is about a
   * press made in a dialog already dismissed, and the new dialog's region is
   * empty; this instance outlives any one dialog, so `SettingsComponent` calls
   * this before it opens one.
   *
   * **`undetermined` holds for the screen's life.** A new dialog that offered
   * the commit again would stake the account on the next challenge failing —
   * and a session that outlived the erasure it could not see is exactly the
   * case where it does not. So every later dialog opens withdrawn, and the way
   * forward is a reload.
   *
   * **Inert while {@link working}.** Mid-ceremony, a reset would put the flow
   * at rest while the device is still being asked, with the commit live for a
   * second press; after `erased`, it would reopen the commit over a deleted
   * session.
   */
  public reset(): void {
    if (this.working()) {
      return;
    }

    this.phaseSignal.set('idle');

    if (this.failureSignal() !== 'undetermined') {
      this.failureSignal.set(null);
    }
  }

  /**
   * Abandons a press that has not posted: the device's prompt is aborted, and
   * the press ends with nothing asked, sent or said after it. Does nothing once
   * the erasing request is out — that request cannot be recalled, and its `204`
   * still has to end the session.
   *
   * Called from the screen's teardown, and by the screen when the overlay is
   * closed from outside mid-ceremony — the CDK disposes it on the browser's
   * Back whatever `disableClose` says — while the screen itself stays.
   */
  public abandon(): void {
    this.#press?.abort();
    this.#press = null;
  }

  /**
   * Runs the whole act for one press: the challenge, the passkey, and the one
   * erasing request — and on its `204`, the way off the screen.
   *
   * Handed the field's text as typed, and re-checked here against the same
   * predicate the commit is drawn from.
   */
  public erase(typed: string): void {
    // A press this refuses is not a press that started anything, so the last
    // refusal stays where it was: *a press clears the previous line as it
    // starts, and nothing else clears it*.
    if (!this.pressable(typed)) {
      return;
    }

    this.failureSignal.set(null);

    // **Before the challenge**, which is the only position that costs nothing
    // — `SignInService`'s argument. A re-authentication challenge is a nonce
    // the server persisted, and a browser that was never going to finish the
    // ceremony would otherwise spend one on its way to this same sentence.
    if (!this.ceremony.available()) {
      this.failureSignal.set('unsupported');

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
      this.abandoned();

      return;
    }

    if (!challenge.ok) {
      this.end(challenge.failure);

      return;
    }

    const ceremony = await this.assert(challenge.options, abort);

    // **The last point at which abandoning means anything.** Past this line
    // the erasing request is sent, and there is no branch after it.
    if (abort.aborted) {
      this.abandoned();

      return;
    }

    this.#press = null;

    if (!ceremony.ok) {
      this.end(ErasureFlowService.failureOf(ceremony.failure));

      return;
    }

    this.phaseSignal.set('erasing');

    try {
      // **Only the payload**, read out of the ceremony in the argument that
      // hands it on. `eraseAccount` projects the five members it names; the
      // key-encryption key beside them is never read by this class at all.
      //
      // `defaultValue`, because a `204` answered with no emission would
      // otherwise reject as an `EmptyError` and be read as `undetermined` over
      // an erasure that landed.
      await firstValueFrom(this.me.eraseAccount(ceremony.value.payload), {
        defaultValue: undefined,
      });
    } catch (error: unknown) {
      // **No retry, automatic or otherwise.** See {@link pressable} and
      // {@link reset}.
      const failure = erasureFailureOf(error);

      this.end(
        failure === 'refused' && (await this.sessionHasEnded())
          ? null
          : failure,
      );

      return;
    }

    this.leave();
  }

  // The server's own options, or the word the press ends on.
  private async challenge(): Promise<ChallengeOutcome> {
    try {
      return {
        ok: true,
        options: await firstValueFrom(
          this.reauthentication.getRequestOptions(),
        ),
      };
    } catch (error: unknown) {
      // **A 401 here is chosen, not inherited: the flow says nothing.** This
      // request is unmarked, so a 401 on it is `sessionExpiryInterceptor`'s to
      // judge. On a verdict that ends the session it takes the tab to Welcome,
      // and the screen's teardown takes the dialog with it. On one the judge
      // kept, the tab stays and the press ends silently, with the commit live
      // again — known and logged as a backlog item, not fixed here. Ending the
      // session here would be a second owner of that fact, and two owners
      // drift. `challengeFailureOf` owns the reading.
      return { ok: false, failure: challengeFailureOf(error) };
    }
  }

  // The ceremony, with a rejection read as the device not finishing. Its
  // contract is to answer with a result; a throw out of it has still posted
  // nothing, so *nothing was erased* stays true.
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

  // Whether a `401` on the erasing request was a session that had already
  // ended, rather than the gate declining the assertion. The erasing request
  // is marked, so the interceptor heard neither reading; this probe is
  // **unmarked**, so on the first reading its own `401` is the interceptor's to
  // judge, and the flow says nothing whatever the verdict. One that ends the
  // session takes the tab to Welcome. One the judge kept leaves the tab here
  // and the press ended silently, the commit live again — known and logged as
  // a backlog item, not fixed here. A `200`, or a probe that cannot answer,
  // leaves `refused`:
  // the erasing request's `401` already proved it erased nothing, and the next
  // press's unmarked challenge catches a session that really has gone.
  private async sessionHasEnded(): Promise<boolean> {
    try {
      await firstValueFrom(this.me.getMe());

      return false;
    } catch (error: unknown) {
      return error instanceof HttpErrorResponse && error.status === 401;
    }
  }

  // Ends a press that did not erase anything — or may have, for
  // `undetermined` — back at rest with its word.
  private end(failure: ErasureFailure | null): void {
    this.#press = null;
    this.phaseSignal.set('idle');
    this.failureSignal.set(failure);
  }

  // Ends an abandoned press at rest, saying nothing: whoever abandoned it has
  // stopped listening. The word was cleared when the press started.
  private abandoned(): void {
    this.phaseSignal.set('idle');
  }

  // The `204`, and **this order is the property**.
  //
  // The phase first, so no press is accepted from here on. Then
  // `SessionService.ended()` — the single owner of clearing the account's keys
  // from this tab — and **only then the router**. `guestGuard` reads the
  // session the moment it is asked, so a navigation made first is judged
  // against a stale `authenticated` and sent back into an account that no
  // longer exists. Sign out's order, for Sign out's reason.
  private leave(): void {
    this.phaseSignal.set('erased');
    this.session.ended();
    void this.router.navigateByUrl('/welcome');
  }

  // The ceremony's five words, mapped onto this flow's four. A `switch` over
  // the closed union, so a sixth word added there fails to compile here instead
  // of arriving as `undefined` on the dialog.
  private static failureOf(failure: PasskeyCeremonyFailure): ErasureFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'cancelled';
      case 'no-prf':
        return 'no-prf';
      case 'duplicate':
      case 'failed':
        return 'ceremony-failed';
    }
  }
}
