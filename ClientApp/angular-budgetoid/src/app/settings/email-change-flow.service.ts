// The flow behind **Change email address** and **Confirm with your passkey** on
// `/app/settings`: the trip to Google, the answer it brings back, the challenge,
// the passkey, the one changing request, and the re-read after it. See
// docs/design/components.md, "Changing the email address".
//
// It is `ErasureFlowService`'s shape — a phase, one word for why the last press
// ended, and one predicate per control read by both the attribute and the
// handler — because the confirm press asks the device for a passkey the server
// checks, in the same order and for the same reason: every word raised before
// the changing request exists makes *nothing changed* a fact about this client.
//
// **Not `providedIn: 'root'`.** `SettingsComponent` provides it, so an answer
// abandoned on the screen dies with the screen, and the screen's teardown
// aborts a press that has not posted — the device's prompt comes down and
// nothing more is asked or sent.
//
// **The provider token is a `#` field and nothing else.** It is read by one
// line, the argument that hands it to `MeApiService.changeEmail`, and it is
// never published, logged or put in a signal a template could render. The
// ceremony result is a local of one method and only its `payload` is read.
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
import {
  MeApiService,
  type EmailChangeResultDto,
} from '@app-core/api/me-api.service';
import { ReauthenticationApiService } from '@app-core/api/reauthentication-api.service';
import { logFailure } from '@app-core/logging/log-failure';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type { PasskeyRequestOptionsJson } from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { firstValueFrom } from 'rxjs';
import { AccountUnlockService } from './account-unlock.service';
import { RotationFlowService } from './rotation-flow.service';
import { SettingsService } from './settings.service';

/**
 * Where the flow is.
 *
 * * `rest` — no answer from Google is held and nothing is running. Change is
 *   drawn.
 * * `leaving` — the page is on its way to Google. Terminal unless Google cannot
 *   be reached.
 * * `waiting` — this load brought a confirmed answer back, and Confirm is drawn
 *   in Change's place.
 * * `asserting` — the challenge is being fetched or the device is being asked.
 *   Nothing has been posted.
 * * `changing` — the changing request is out, and it stays here after a `200`
 *   until the address row has been read again, because the success line states
 *   the result.
 */
export type EmailChangePhase =
  | 'rest'
  | 'leaving'
  | 'waiting'
  | 'asserting'
  | 'changing';

/**
 * Why the last press ended, one word per line the region can say. The ceremony
 * words and `unstarted` are the erasure dialog's; the request's words are read
 * from the problem body's members, never from the status alone.
 */
export type EmailChangeWord =
  | 'changed'
  | 'changed-unread'
  | 'unavailable'
  | 'unconfirmed'
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | 'unstarted'
  | 'provider-refused'
  | 'unverified'
  | 'assertion-refused'
  | 'address-taken'
  | 'google-account-taken'
  | 'moved'
  | 'failed'
  | 'undetermined';

/** Why Change is off, one sentence each, in the order a reload would cost. */
export type EmailChangeHold = 'rotating' | 'exporting' | 'unlocking';

// How the challenge leg ended: with the server's options, or with the word the
// press ends on — `null` when the flow has nothing to say.
type ChallengeOutcome =
  | { readonly ok: true; readonly options: PasskeyRequestOptionsJson }
  | { readonly ok: false; readonly word: 'unstarted' | null };

// The design book's two tables, member value to word. A `Map` keyed on
// `unknown`, so whatever the body carries is looked up as it is and anything
// the table does not hold misses — never a prototype key, never a coercion.
const REFUSAL_WORDS: ReadonlyMap<unknown, EmailChangeWord> = new Map<
  unknown,
  EmailChangeWord
>([
  ['provider_token', 'provider-refused'],
  ['email_unverified', 'unverified'],
  ['assertion', 'assertion-refused'],
]);

const CONFLICT_WORDS: ReadonlyMap<unknown, EmailChangeWord> = new Map<
  unknown,
  EmailChangeWord
>([
  ['email_already_linked', 'address-taken'],
  ['provider_identity_in_use', 'google-account-taken'],
  ['account_identity_moved', 'moved'],
]);

// The words after which the Google answer is still good and Confirm is offered
// again. Every other word ends the waiting state and drops the answer.
const KEEPS_WAITING: ReadonlySet<EmailChangeWord> = new Set<EmailChangeWord>([
  'cancelled',
  'no-prf',
  'ceremony-failed',
  'unstarted',
  'assertion-refused',
]);

@Injectable()
export class EmailChangeFlowService {
  private readonly auth = inject(AuthService);
  private readonly reauthentication = inject(ReauthenticationApiService);
  private readonly me = inject(MeApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly settings = inject(SettingsService);
  private readonly unlock = inject(AccountUnlockService);
  private readonly rotation = inject(RotationFlowService);

  private readonly phaseSignal: WritableSignal<EmailChangePhase> =
    signal<EmailChangePhase>('rest');
  private readonly wordSignal: WritableSignal<EmailChangeWord | null> =
    signal<EmailChangeWord | null>(null);
  private readonly addressSignal: WritableSignal<string | null> = signal<
    string | null
  >(null);
  private readonly sessionsEndedSignal: WritableSignal<number | null> = signal<
    number | null
  >(null);

  // The screen that provides this has gone.
  #destroyed = false;

  // The token Google answered with, held while the answer is. See the header.
  #idToken: string | null = null;

  // The press in flight's handle, until it posts or ends. One per press, so an
  // abort reaches the attempt that is running and never a later one.
  #press: AbortController | null = null;

  public readonly phase: Signal<EmailChangePhase> =
    this.phaseSignal.asReadonly();
  public readonly word: Signal<EmailChangeWord | null> =
    this.wordSignal.asReadonly();
  /** The address Google asserted, while its answer is held. */
  public readonly address: Signal<string | null> =
    this.addressSignal.asReadonly();
  /** The other sessions a change ended, beside `changed`; `null` unread. */
  public readonly sessionsEnded: Signal<number | null> =
    this.sessionsEndedSignal.asReadonly();

  /**
   * The one sentence above an off Change, or `null`. The rotation's first, then
   * the export's, then the unlock's: what breaking each costs, largest first.
   * The flow's own busy state is not a reason — the region says that.
   */
  public readonly changeHold: Signal<EmailChangeHold | null> = computed(() => {
    if (this.rotation.working()) {
      return 'rotating';
    }

    if (this.settings.exporting()) {
      return 'exporting';
    }

    return this.unlock.working() ? 'unlocking' : null;
  });

  // A Confirm press is running: the challenge, the ceremony or the changing
  // request, including the re-read after its `200`.
  private readonly confirming: Signal<boolean> = computed(() => {
    const phase = this.phaseSignal();

    return phase === 'asserting' || phase === 'changing';
  });

  /**
   * Change's predicate, read by its attribute and by {@link change}. The last
   * term holds although Change is not drawn while a Confirm press runs: the
   * handler refuses a press that reaches it by any path.
   */
  public readonly changePressable: Signal<boolean> = computed(
    () =>
      this.changeHold() === null &&
      this.phaseSignal() !== 'leaving' &&
      !this.confirming(),
  );

  /**
   * Confirm's predicate, read by its attribute and by {@link confirm}. Narrower
   * than Change's: only its own press holds it off, because Change's reasons
   * describe a moment before a trip and Confirm exists only after one.
   */
  public readonly confirmPressable: Signal<boolean> = computed(
    () => this.phaseSignal() === 'waiting',
  );

  constructor() {
    // **Taken once, here**, not on a first read: the answer is read before the
    // first route draws, so the first render is already the waiting state, and
    // a second reader of this load finds nothing.
    const handOff = this.auth.takeEmailChangeReturn();

    if (handOff?.kind === 'answered') {
      this.#idToken = handOff.idToken;
      this.addressSignal.set(handOff.email);
      this.phaseSignal.set('waiting');
    } else if (handOff?.kind === 'unconfirmed') {
      this.wordSignal.set('unconfirmed');
    }

    // The screen that provides this is going. A press that has not posted is
    // abandoned with it; one that has cannot be recalled.
    // The re-read after a `200` is past the press's abort, so it is told
    // separately: nothing it answers is published.
    inject(DestroyRef).onDestroy(() => {
      this.#press?.abort();
      this.#press = null;
      this.#destroyed = true;
    });
  }

  /** Leaves for Google's account chooser, or says why it cannot. */
  public change(): void {
    if (!this.changePressable()) {
      return;
    }

    // A trip replaces whatever answer this load held.
    this.dropAnswer();
    this.wordSignal.set(null);
    this.sessionsEndedSignal.set(null);
    this.phaseSignal.set('leaving');

    void this.leave();
  }

  /**
   * Runs the confirm press: the browser's ability, the challenge from the
   * re-authentication pool, the passkey, and the one changing request.
   */
  public confirm(): void {
    const idToken = this.#idToken;

    if (!this.confirmPressable() || idToken === null) {
      return;
    }

    this.wordSignal.set(null);
    this.sessionsEndedSignal.set(null);

    // Before the challenge, the only position that costs nothing. A browser
    // that cannot run the ceremony cannot on the next press either, so the
    // answer goes and Change comes back.
    if (!this.ceremony.available()) {
      this.end('unsupported');

      return;
    }

    this.phaseSignal.set('asserting');

    const press = new AbortController();

    this.#press = press;

    void this.run(idToken, press.signal);
  }

  private async leave(): Promise<void> {
    let trip: 'leaving' | 'unavailable';

    try {
      trip = await this.auth.startEmailChange();
    } catch (error: unknown) {
      // Its contract is never to reject. A throw has not left the page, so it
      // reads as the press it is.
      logFailure('Email change trip could not start', error);
      trip = 'unavailable';
    }

    // `leaving` stays until the page goes.
    if (trip === 'unavailable') {
      this.end('unavailable');
    }
  }

  private async run(idToken: string, abort: AbortSignal): Promise<void> {
    const challenge = await this.challenge();

    if (abort.aborted) {
      return;
    }

    if (!challenge.ok) {
      this.#press = null;
      this.end(challenge.word);

      return;
    }

    const ceremony = await this.assert(challenge.options, abort);

    // **The last point at which abandoning means anything.** Past this line the
    // changing request is sent.
    if (abort.aborted) {
      return;
    }

    this.#press = null;

    if (!ceremony.ok) {
      this.end(EmailChangeFlowService.ceremonyWordOf(ceremony.failure));

      return;
    }

    this.phaseSignal.set('changing');

    let answered: EmailChangeResultDto;

    try {
      answered = await firstValueFrom(
        this.me.changeEmail(idToken, ceremony.value.payload),
      );
    } catch (error: unknown) {
      // **No retry, automatic or otherwise.** A word that drops the answer
      // leaves no token to send again.
      this.end(await this.wordFor(error));

      return;
    }

    // The token is spent, so nothing can send it again. The address stays
    // until the re-read ends the flow: Confirm is still drawn while
    // `changing`, and its lead line names it. `end` drops it with the rest.
    // Both rows go stale: the address, and the Google row in the credential
    // list.
    this.#idToken = null;

    if (this.#destroyed) {
      return;
    }

    this.settings.loadCredentials();

    // **The success line waits for this read's own answer**, never for the row
    // going blank and back: a read can answer before anybody sees the blank,
    // and an older read landing late would put the old address there. A
    // failed read is its own word — the row's own failure line renders beside
    // it.
    const reread = await this.settings.loadEmail();

    if (this.#destroyed) {
      return;
    }

    if (reread === 'loaded') {
      this.end('changed');
      this.sessionsEndedSignal.set(answered.sessionsEnded);
    } else {
      this.end('changed-unread');
    }
  }

  // The server's own options, or the word the press ends on. The request is
  // unmarked, so a `401` is a session that really ended — the interceptor's
  // fact — and the flow says nothing over it.
  private async challenge(): Promise<ChallengeOutcome> {
    try {
      return {
        ok: true,
        options: await firstValueFrom(
          this.reauthentication.getRequestOptions(),
        ),
      };
    } catch (error: unknown) {
      return {
        ok: false,
        word:
          error instanceof HttpErrorResponse && error.status === 401
            ? null
            : 'unstarted',
      };
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

  // What the changing request failed with, as the book's tables read it.
  //
  // **Every `4xx` is a judgement**, so a member missing or unknown to this
  // bundle is `failed` — nothing changed. **`undetermined` is no judgement
  // observed**: a `5xx`, a status `0`, and a `200` whose body did not read,
  // which `changeEmail` throws as something that is not an `HttpErrorResponse`.
  private async wordFor(error: unknown): Promise<EmailChangeWord | null> {
    if (!(error instanceof HttpErrorResponse)) {
      logFailure('Email change answer could not be read', error);

      return 'undetermined';
    }

    if (error.status === 401) {
      const refusal = refusalOf(error.error);

      // Read only after the probe, and the probe decides whether there is
      // anything to say at all.
      return (await this.sessionHasEnded())
        ? null
        : (REFUSAL_WORDS.get(refusal) ?? 'failed');
    }

    if (error.status === 409) {
      const word = CONFLICT_WORDS.get(conflictKindOf(error.error)) ?? 'failed';

      // The one refusal that says something changed, so its row is read again
      // — and only that row. Its outcome is the row's own line, not this
      // flow's, so it is not awaited.
      if (word === 'moved') {
        void this.settings.loadEmail();
      }

      return word;
    }

    if (error.status >= 400 && error.status < 500) {
      return 'failed';
    }

    logFailure('Email change request went unanswered', error);

    return 'undetermined';
  }

  // Whether a `401` on the changing request was a session that had already
  // ended. The changing request is marked, so the interceptor heard nothing;
  // this probe is **unmarked**, so its own `401` is the interceptor's to act on
  // and the flow says nothing. A `200`, or a probe that cannot answer, lets the
  // member decide.
  private async sessionHasEnded(): Promise<boolean> {
    try {
      await firstValueFrom(this.me.getMe());

      return false;
    } catch (error: unknown) {
      return error instanceof HttpErrorResponse && error.status === 401;
    }
  }

  // Ends a press, or a trip that could not start, with its word. A word that
  // keeps the answer puts the flow back to waiting; every other word drops the
  // answer and brings Change back. `null` — a session that ended — drops it
  // too, because the interceptor is taking the tab to Welcome.
  private end(word: EmailChangeWord | null): void {
    if (word !== null && KEEPS_WAITING.has(word) && this.#idToken !== null) {
      this.phaseSignal.set('waiting');
    } else {
      this.dropAnswer();
      this.phaseSignal.set('rest');
    }

    this.wordSignal.set(word);
  }

  private dropAnswer(): void {
    this.#idToken = null;
    this.addressSignal.set(null);
  }

  // The ceremony's five words mapped onto this flow's. A `switch` over the
  // closed union, so a sixth word there fails to compile here.
  private static ceremonyWordOf(
    failure: PasskeyCeremonyFailure,
  ): EmailChangeWord {
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

// The two members of a problem body the words are read from, or `undefined`.
// Narrowed, never asserted: the body is whatever arrived.
function refusalOf(body: unknown): unknown {
  return typeof body === 'object' && body !== null && 'refusal' in body
    ? body.refusal
    : undefined;
}

function conflictKindOf(body: unknown): unknown {
  return typeof body === 'object' && body !== null && 'conflictKind' in body
    ? body.conflictKind
    : undefined;
}
