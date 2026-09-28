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
// this instance and no other.
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
import { ErasureNotice } from '@app-core/session/erasure-notice';
import { SessionService } from '@app-core/session/session.service';
import { firstValueFrom } from 'rxjs';
import { confirmsErasure } from './erasure-confirmation';
import {
  erasureFailureOf,
  type ErasureRequestFailure,
} from './erasure-outcome';

/**
 * Where a press is.
 *
 * * `idle` — nothing is running; the last press, if any, has ended.
 * * `asserting` — the challenge is being fetched or the device is being asked.
 *   Nothing has been posted.
 * * `erasing` — the erasing request is out and nothing can recall it.
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
 * credential named in an exclusion list, and an assertion carries none.
 * `unstarted` is a challenge that never arrived. The last three are the erasing
 * request's own — see `erasure-outcome.ts`, which owns that reading.
 *
 * **It is not `RotationCeremonyFailure` imported.** Each flow states what *it*
 * can report, so the day one gains a word the other does not silently gain it.
 */
export type ErasureFailure =
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | 'unstarted'
  | ErasureRequestFailure;

@Injectable()
export class ErasureFlowService {
  private readonly reauthentication = inject(ReauthenticationApiService);
  private readonly me = inject(MeApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly notice = inject(ErasureNotice);

  private readonly phaseSignal: WritableSignal<ErasurePhase> =
    signal<ErasurePhase>('idle');
  private readonly failureSignal: WritableSignal<ErasureFailure | null> =
    signal<ErasureFailure | null>(null);

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
   * **`undetermined` closes it until {@link reset}, which is to say for the
   * rest of the dialog that saw it.** Erasure is not idempotent to the caller:
   * a second erasing request after a lost `204` is answered `401` — the session
   * went with the account — and would read as *nothing was erased* over an
   * account that is gone. Nothing inside the dialog clears the word, so the
   * dialog cannot offer that request again. Opening a new dialog does clear it,
   * through {@link reset}, and that is safe for the reason given there.
   */
  public pressable(typed: string): boolean {
    return (
      confirmsErasure(typed) &&
      !this.working() &&
      this.failureSignal() !== 'undetermined'
    );
  }

  /**
   * Puts the flow back at rest with no word, for a new dialog — or does
   * nothing while a press is running or has erased the account.
   *
   * **A new dialog is a fresh attempt.** The design book scopes the commit's
   * withdrawal to the dialog, not to the screen, and the dialog's region is
   * empty from the moment it opens; this instance outlives any one dialog, so
   * `SettingsComponent` calls this before it opens one.
   *
   * **Safe after `undetermined`**, because the first request a fresh attempt
   * makes is the re-authentication challenge, and that request is unmarked. If
   * the lost request did erase the account, the session went with it: the
   * challenge answers `401`, `sessionExpiryInterceptor` ends the session and
   * takes the tab to Welcome, and no ceremony runs and no erasing request is
   * sent.
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
    this.failureSignal.set(null);
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

    void this.run();
  }

  private async run(): Promise<void> {
    const options = await this.challenge();

    if (options === null) {
      return;
    }

    const ceremony = await this.assert(options);

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
      this.end(erasureFailureOf(error));

      return;
    }

    this.leave();
  }

  // The server's own options, unchanged — or `null` when the press ended here.
  private async challenge(): Promise<PasskeyRequestOptionsJson | null> {
    try {
      return await firstValueFrom(this.reauthentication.getRequestOptions());
    } catch (error: unknown) {
      // **A 401 here is chosen, not inherited: the flow says nothing.** This
      // request is unmarked, so a 401 on it is a session that really has ended
      // — `sessionExpiryInterceptor`'s fact. It ends the session, takes the
      // tab to Welcome, and the screen's teardown takes the dialog with it.
      // `unstarted` would claim the server could not be reached when it
      // answered; ending the session here would be a second owner of that
      // fact, and two owners drift.
      const sessionEnded =
        error instanceof HttpErrorResponse && error.status === 401;

      this.end(sessionEnded ? null : 'unstarted');

      return null;
    }
  }

  // The ceremony, with a rejection read as the device not finishing. Its
  // contract is to answer with a result; a throw out of it has still posted
  // nothing, so *nothing was erased* stays true.
  private async assert(
    options: PasskeyRequestOptionsJson,
  ): Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>> {
    try {
      return await this.ceremony.assertPasskey(options);
    } catch {
      return { ok: false, failure: 'failed' };
    }
  }

  // Ends a press that did not erase anything — or may have, for
  // `undetermined` — back at rest with its word.
  private end(failure: ErasureFailure | null): void {
    this.phaseSignal.set('idle');
    this.failureSignal.set(failure);
  }

  // The `204`, and **this order is the property**.
  //
  // The phase first, so no press is accepted from here on. The notice and
  // `SessionService.ended()` next, in either order between themselves — but
  // both before the router. The notice, because Welcome renders *Erased.* from
  // it on its first paint, and marked after the navigation the word lands on a
  // screen that has already rendered. `ended()`, which is the single owner of
  // clearing the account's keys from this tab, because `guestGuard` reads the
  // session the moment it is asked, and a navigation made first is judged
  // against a stale `authenticated` and sent back into an account that no
  // longer exists. Sign out's order, for Sign out's reason.
  private leave(): void {
    this.phaseSignal.set('erased');
    this.notice.mark();
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
