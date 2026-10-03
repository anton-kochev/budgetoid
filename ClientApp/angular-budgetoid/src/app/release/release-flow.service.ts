// The flow behind the release screen: the locked sign-in sent the moment a
// Google answer is taken, the trip that fetches one, the schedule, and the way
// off the screen. See docs/design/components.md, "Releasing an account".
//
// **Not `providedIn: 'root'`.** `ReleaseComponent` provides it, so the Google
// answer is taken by the screen that renders what it led to, and a word from
// one visit cannot stand on the next. Requests already out are not cancelled
// when the screen goes: a locked sign-in's `200` has set a cookie whether or
// not anybody is looking, and the session has to hear about it.
//
// **One predicate per control, read by the attribute and by the handler.**
// Material's click-halt is applied to anchors only, so on a `<button>` the
// press arrives whatever the attribute draws.
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
import { AuthService } from '@app-core/services/auth-service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import { SessionService } from '@app-core/session/session.service';

/**
 * Why the last sign-in did not open a locked session, one word per sentence
 * the screen can say.
 *
 * * `no-account` — `404` with `refusal: "no_account"`: no Budgetoid account
 *   was created with that Google account.
 * * `provider-refused` — `401`: the server did not accept the Google answer.
 * * `unrecognised` — `403`: the request was not one the server reads.
 * * `full-session` — `409` with `conflictKind: "full_session"`: this browser
 *   already holds a full session, and the server changed nothing. A reload
 *   opens that account; the request is not retried and nothing is probed.
 * * `undetermined` — no answer, a `5xx`, a `200` that does not read, or any
 *   answer not listed. A lost `200` may have set the cookie, so this is the
 *   one word that names a reload.
 * * `unconfirmed` — the return carried no confirmed answer; nothing was sent.
 * * `unavailable` — the trip could not start; nothing was sent.
 */
export type ReleaseSignInFailure =
  | 'no-account'
  | 'provider-refused'
  | 'unrecognised'
  | 'full-session'
  | 'undetermined'
  | 'unconfirmed'
  | 'unavailable';

/**
 * Why the last press of the commit filed nothing this tab can show.
 *
 * * `unrecognised` — `403`.
 * * `undetermined` — anything else but a `401`, which is the interceptor's.
 *   The schedule is idempotent, so the commit stays live and another press is
 *   how the person finds out.
 */
export type ReleaseScheduleFailure = 'unrecognised' | 'undetermined';

@Injectable()
export class ReleaseFlowService {
  private readonly auth = inject(AuthService);
  private readonly api = inject(MeApiService);
  private readonly session = inject(SessionService);
  private readonly departure = inject(ProviderDepartureService);
  private readonly router = inject(Router);

  private readonly signingInSignal: WritableSignal<boolean> = signal(false);
  private readonly schedulingSignal: WritableSignal<boolean> = signal(false);
  private readonly acknowledgedSignal: WritableSignal<boolean> = signal(false);
  private readonly signInFailureSignal: WritableSignal<ReleaseSignInFailure | null> =
    signal(null);
  private readonly scheduleFailureSignal: WritableSignal<ReleaseScheduleFailure | null> =
    signal(null);

  /** The locked sign-in is out. */
  public readonly signingIn: Signal<boolean> =
    this.signingInSignal.asReadonly();

  /** The schedule request is out. */
  public readonly scheduling: Signal<boolean> =
    this.schedulingSignal.asReadonly();

  /** The acknowledgement is ticked. */
  public readonly acknowledged: Signal<boolean> =
    this.acknowledgedSignal.asReadonly();

  public readonly signInFailure: Signal<ReleaseSignInFailure | null> =
    this.signInFailureSignal.asReadonly();

  public readonly scheduleFailure: Signal<ReleaseScheduleFailure | null> =
    this.scheduleFailureSignal.asReadonly();

  /**
   * Whether a press of Continue starts a trip: the page is not already leaving,
   * and no sign-in is out. An ungated press would start a second trip under
   * the first.
   */
  public readonly continuePressable: Signal<boolean> = computed(
    () => !this.departure.departing() && !this.signingInSignal(),
  );

  /**
   * Whether a press of the commit sends the schedule: the acknowledgement is
   * ticked, nothing is running, and the session is still a locked one. The last
   * term is for the moment after Sign out or an ended session, when the press
   * would otherwise send a schedule from a screen holding no session.
   */
  public readonly commitPressable: Signal<boolean> = computed(
    () =>
      this.acknowledgedSignal() &&
      !this.schedulingSignal() &&
      this.session.status() === 'locked-session',
  );

  constructor() {
    // **Taken once, here, and sent at once.** The press of Continue was made
    // under prose saying what the trip is for, and that press is the consent;
    // a second press after the return would ask the same question twice. A
    // reload holds no answer, so it cannot send a second request.
    const handedOver = this.auth.takeLockedSignInReturn();

    if (handedOver?.kind === 'answered') {
      this.openLockedSession(handedOver.idToken);
    } else if (handedOver?.kind === 'unconfirmed') {
      this.signInFailureSignal.set('unconfirmed');
    }
  }

  public acknowledge(checked: boolean): void {
    this.acknowledgedSignal.set(checked);
  }

  /** Starts the trip to Google, unless the control is held. */
  public continue(): void {
    if (!this.continuePressable()) {
      return;
    }

    this.signInFailureSignal.set(null);

    void this.auth.startLockedSignIn().then((outcome) => {
      if (outcome === 'unavailable') {
        this.signInFailureSignal.set('unavailable');
      }
    });
  }

  /**
   * Sends the schedule, unless the commit is held. Never retried: after
   * `undetermined` the next request is the person's next press.
   */
  public commit(): void {
    if (!this.commitPressable()) {
      return;
    }

    this.schedulingSignal.set(true);
    this.scheduleFailureSignal.set(null);

    this.api.scheduleErasure().subscribe({
      next: ({ takesEffectAtUtc }) => {
        this.session.erasureScheduled(takesEffectAtUtc);
        this.schedulingSignal.set(false);
      },
      error: (error: unknown) => {
        this.scheduleFailureSignal.set(scheduleFailureOf(error));
        this.schedulingSignal.set(false);
      },
    });
  }

  /**
   * The Settings control's semantics: the revocation, then `ended()`, then
   * `/welcome`, in that order — navigate first and the guard on `/welcome`
   * reads a stale status. A failed request signs the person out anyway. Never
   * held, including while a schedule is out.
   */
  public signOut(): void {
    this.api.endSession().subscribe({
      next: () => this.leave(),
      error: () => this.leave(),
    });
  }

  private leave(): void {
    this.session.ended();
    void this.router.navigateByUrl('/welcome');
  }

  // **One answer, one request, never retried** — not here and not by any
  // interceptor; the request is marked as expecting a `401`, so
  // `sessionExpiryInterceptor` leaves the refusal to this screen.
  //
  // **The session hears first**, and `signingIn` drops after: the screen picks
  // its state off the session status, and dropping `signingIn` first would draw
  // the screen before the trip for one render between the waiting line and the
  // locked surface.
  private openLockedSession(idToken: string): void {
    this.signingInSignal.set(true);

    this.api.openLockedSession(idToken).subscribe({
      next: (answer) => {
        this.session.establishedLocked(answer);
        this.signingInSignal.set(false);
      },
      error: (error: unknown) => {
        this.signInFailureSignal.set(signInFailureOf(error));
        this.signingInSignal.set(false);
      },
    });
  }
}

// The words come from members of the answer, never from its prose, and a
// status alone never names `no-account` or `full-session`: a `404` without
// that refusal, or a `409` without that conflict kind, is a route or a proxy
// this client was not written against.
function signInFailureOf(error: unknown): ReleaseSignInFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'undetermined';
  }

  switch (error.status) {
    case 401:
      return 'provider-refused';
    case 403:
      return 'unrecognised';
    case 404:
      return refusalOf(error.error) === 'no_account'
        ? 'no-account'
        : 'undetermined';
    case 409:
      return conflictKindOf(error.error) === 'full_session'
        ? 'full-session'
        : 'undetermined';
    default:
      return 'undetermined';
  }
}

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

// `null` for a `401`: the session ended, `sessionExpiryInterceptor` acts on it,
// and this screen says nothing over it.
function scheduleFailureOf(error: unknown): ReleaseScheduleFailure | null {
  if (!(error instanceof HttpErrorResponse)) {
    return 'undetermined';
  }

  switch (error.status) {
    case 401:
      return null;
    case 403:
      return 'unrecognised';
    default:
      return 'undetermined';
  }
}
