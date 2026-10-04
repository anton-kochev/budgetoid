import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import {
  MeApiService,
  type MeDto,
  type SessionDto,
} from '@app-core/api/me-api.service';
import { logFailure } from '@app-core/logging/log-failure';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { AuthService } from '@app-core/services/auth-service';
import { firstValueFrom } from 'rxjs';

// Five states, and `unreachable` is the one a reader will collapse into
// `anonymous`. The session cookie is `HttpOnly`, so nothing in the browser can
// read it and the only way to learn who the visitor is is to ask the server —
// which makes every value here a reading of an *answer*, and `unreachable` the
// reading of an answer that never came. A request that got no answer is not
// evidence about the visitor: read as a refusal it signs a person holding a
// perfectly good session out of their own account, over a network that blinked
// once during the cold load, and drops them on a page served by the same server
// they could not reach. It is `SettingsService`'s "never collapse `null` to `0`"
// rule one screen over — a claim about the account made out of a failure to ask.
//
// `unknown` is the same argument before the first ask rather than after a failed
// one: the initializer resolves the probe before the first route activates, so
// nothing should see it, and it exists so that a deleted initializer is a
// redundant state rather than every visitor bounced on every cold load.
//
// `locked-session` is a session opened by a federated credential: it reads no
// budget content of any kind and reaches one screen, `/release`. Spelled
// `locked-session` and never `locked`, which the account-key status already
// spells for a different fact — an account whose keys this tab does not hold.
export type SessionStatus =
  | 'unknown'
  | 'authenticated'
  | 'locked-session'
  | 'anonymous'
  | 'unreachable';

// The account's scheduled erasure as this tab last learned it. Three values and
// never two: `'unread'` is "nobody has said", `null` is "the server said nothing
// is scheduled". Collapsing them claims an answer out of a failure to ask.
export type ScheduledErasure =
  | { readonly takesEffectAtUtc: string }
  | null
  | 'unread';

declare const sessionTokenBrand: unique symbol;

/**
 * Which visit of this tab a request was sent in: opaque, compared only for
 * identity, and minted by {@link SessionService} alone. See
 * {@link SessionService.sessionToken}.
 */
export type SessionToken = number & { readonly [sessionTokenBrand]: true };

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly api = inject(MeApiService);
  // **The dependency runs one way and must keep doing so.** This class reaches
  // for custody; custody reaches for nothing on this class, and says so in its
  // own header — a key that will not open is not a session that ended, so
  // publishing `anonymous` from there would sign somebody out of an account
  // they are demonstrably inside. That asymmetry is what keeps the two modules
  // out of an import cycle: the edge exists here and nowhere in the other
  // direction, and `account-key-custody.service.spec.ts` provides a
  // `SessionService` stub purely so a call that appeared would land somewhere
  // countable.
  private readonly custody = inject(AccountKeyCustodyService);
  // **One way too, and for the same reason.** This class tells the provider
  // service to drop its tokens the moment a session is published; the provider
  // service must never inject this class. It is asked from the
  // `APP_INITIALIZER` beside the probe, and an edge back would be an import
  // cycle between the two things bootstrap awaits.
  private readonly auth = inject(AuthService);

  private readonly statusSignal = signal<SessionStatus>('unknown');

  // The budget this browser is operating inside, or `null` while nothing has
  // said. The head of {@link budgetId} argues what it is for and why it is a
  // second signal rather than a member of the status.
  private readonly budgetSignal = signal<string | null>(null);

  public readonly status: Signal<SessionStatus> =
    this.statusSignal.asReadonly();

  /**
   * The budget the requests this browser makes are scoped by, or `null` while
   * this browser has not been told.
   *
   * **It is here because the blind index needs it and nothing else does.** No
   * screen renders it. `GET /api/me` is the one route that says it — the value
   * is resolved from the session cookie on the server and named in no request —
   * and it is the fourth field of every blind-index message, which is what stops
   * two budgets of one account keying one value to one digest. `blind-index.ts`
   * argues the grammar; this is where the value the browser folds into it is
   * read.
   *
   * **A signal beside the status and never a member of it.** The two are learned
   * from one answer and are not the same fact: a browser can know who it is and
   * not know which budget it is in — that is exactly the window between an
   * establishing leg and the read below — and a status union carrying the
   * identifier would make every screen that reads `'authenticated'` re-derive
   * that distinction for itself.
   *
   * **`null` is never a value a caller may substitute for.** A write that finds
   * it `null` does not happen and answers `unreachable`: no factor can supply a
   * budget, so `locked`'s advice — present one — cannot come true of this, and
   * sending somebody through a ceremony that changes nothing is the collapse
   * this codebase refuses in five other places. What can come true is a reload,
   * which is `unreachable`'s.
   */
  public readonly budgetId: Signal<string | null> =
    this.budgetSignal.asReadonly();

  private readonly scheduledErasureSignal = signal<ScheduledErasure>('unread');

  /**
   * The account's scheduled erasure: `'unread'` while nothing has said, `null`
   * when the server said nothing is scheduled, or the instant it takes effect.
   *
   * Written by the probe, by the two locked-session arms — the locked sign-in's
   * answer and a schedule request's — by a cancellation's `204`, and by the
   * schedule reads {@link established} and {@link refreshSchedule} make. It is
   * returned to `'unread'` when the session ends, because the value is a fact
   * about that session's account and the next occupant of this tab is owed a
   * fresh read.
   */
  public readonly scheduledErasure: Signal<ScheduledErasure> =
    this.scheduledErasureSignal.asReadonly();

  // **Moves on every write of the status or the schedule that is not a
  // schedule read's own publication**, and a schedule read publishes only if
  // it has not moved since the read was sent. One counter rather than a status
  // comparison, because a session can end and a new one begin while a read is
  // out — `'authenticated'` again, the same value, about a different visit —
  // and because a cancellation's `204` changes no status at all: a read sent
  // before it describes the account as it was, and published last it would
  // put the notice back over a schedule that is gone.
  #generation = 0;

  // **Moves when a visit begins or ends, and on nothing a visit does.** The
  // generation answers "did anything write since this read went out"; this
  // answers "is this still the same visit", which is the question a `204`
  // sent before a sign-out and a sign-in must be judged by. Kept apart because
  // the two disagree on exactly the writes that matter: a schedule request's
  // `200` moves the generation inside one visit, and a `204` sent in that
  // visit must still publish.
  #visit = 0;

  // How many schedule reads are out. {@link refreshSchedule} sends none while
  // any is: a tab switched back and forth would otherwise stack reads against
  // an API that scales to zero.
  #scheduleReads = 0;

  // Resolves however the reads end, and never rejects. The `APP_INITIALIZER`
  // awaits this promise, so a rejection is not a failed probe — it is an
  // application that never finishes bootstrapping and a browser left on a blank
  // page. The failure is published as a state, which is the handling.
  //
  // **Two questions, asked in order and never in parallel.** First what kind of
  // session this is, if any; then, only for a full one, whose budget it is.
  // `GET /api/me` is budget-scoped and answers a locked session `403`, which
  // reads as no session at all — so asked first, or beside the first, it would
  // sign a locked tab out on every reload, and for a locked session it would
  // only ever fetch a refusal. The second question is worth asking once the
  // first has said there is a full session to ask it of.
  public async probe(): Promise<void> {
    let session: SessionDto;

    try {
      // `getSession()` carries `EXPECTS_UNAUTHENTICATED`, so the 401 this call
      // went to fetch reaches the `catch` below and nothing else — without it
      // `sessionExpiryInterceptor` reads the answer as a session ending and
      // navigates to `/welcome` from inside the `APP_INITIALIZER`, before the
      // router has activated anything, which is every anonymous visitor's deep
      // link. The reading of that 401 belongs to the `catch` below, and is made
      // once.
      session = await firstValueFrom(this.api.getSession());
    } catch (error: unknown) {
      // Dropped beside the status, because it is a claim about a read that did
      // not land. A stale identifier left standing here would be keyed into
      // values written by whoever comes back next. The schedule is left as it
      // is: a probe that learned nothing about the session learned nothing
      // about its schedule either.
      this.budgetSignal.set(null);
      this.publishStatus(SessionService.readingOf(error));

      return;
    }

    // Read off the answer as `unknown` rather than trusted from the type,
    // which is the decoder's claim and not a check this class made. A kind
    // this bundle does not know is **exactly `unreachable`**: read as
    // `authenticated` it hands budget screens to a session the server may
    // refuse on every one of them; read as `locked-session` it sends a full
    // session to the release screen, whose one act is erasing the account. A
    // reload is the act that changes it.
    const kind: unknown = session.kind;

    if (kind === 'locked') {
      this.beginVisit();
      this.budgetSignal.set(null);
      this.publishSchedule(SessionService.scheduleOf(session));
      this.publishStatus('locked-session');
      // A session beginning, so the provider's tokens go — and outside any
      // `try`, for the reason the full arm's discard is.
      this.forgetProviderToken();

      return;
    }

    if (kind !== 'full') {
      this.budgetSignal.set(null);
      this.publishStatus('unreachable');

      return;
    }

    // **Published before the owner read, and kept whatever it answers.** The
    // session read has already said there is a full session; the owner read
    // failing afterwards is a fact about the budget, not about the visitor, and
    // reading it as `unreachable` would sign a confirmed session out over a
    // second request blinking. `null` is the honest budget meanwhile: signed in,
    // tenancy unknown, every write refused with a word whose remedy is a reload.
    this.beginVisit();
    this.budgetSignal.set(null);
    this.publishSchedule(SessionService.scheduleOf(session));
    this.publishStatus('authenticated');

    // **Never inside a `try` that could unpublish the session, and only on the
    // two arms that publish one.** Never on `anonymous` or `unreachable`: on a
    // registration return this probe runs *before* `auth.initialize()` reads
    // the answer off the URL, and a discard there takes the library's nonce
    // with the tokens, so the answer no longer validates and registration
    // cannot complete. On an email-change return the order is the other way
    // round: `initialize()` runs before this probe and has already run
    // `logOut(true)`, so the discard here finds nothing to take.
    this.forgetProviderToken();

    // **The budget rides on the probe the initializer already awaits**, so
    // every screen behind `authGuard` starts with the identifier its writes
    // need. A failure leaves the `null` set above standing.
    await this.readBudget();
  }

  // The mid-visit transition, called by `sessionExpiryInterceptor` when the API
  // answers 401 to a request the visitor did not expect to be refused. It is a
  // set rather than a re-probe: the server has just said what it thinks, and
  // asking it again over a network that may itself be the problem would replace
  // an answer with a guess.
  public ended(): void {
    this.beginVisit();
    this.publishStatus('anonymous');

    // **Dropped beside the keys, and for the same reason they are.** The
    // identifier is a fact about the session that just ended, and a browser that
    // kept it would fold the previous occupant's tenancy into the first value
    // the next one writes — through the one door the server cannot see, since it
    // holds no index key and can never recompute a digest to check it against.
    // It is one line here rather than one line in each of the paths that end a
    // session, for the reason `custody.lock()` is: a third path will be added by
    // somebody thinking about sign-out, and put here it costs them nothing.
    this.budgetSignal.set(null);

    // The schedule is a fact about the account whose session just ended, for
    // the budget's reason above.
    this.publishSchedule('unread');

    // **Custody ends where the session does, and it ends here rather than at
    // each caller.** Four paths end a session today —
    // `sessionExpiryInterceptor` on a 401, `SettingsService.leave()`,
    // `ErasureFlowService` on the erasing request's 204, and
    // `ReleaseFlowService.leave()` — and the last two are the case this
    // placement was for: each was added by somebody thinking about erasure or
    // release rather than about key material, and each clears the account's
    // keys without a line of its own. Put in the callers instead, a fifth would
    // not, and the symptom is an ended session whose content key is still
    // readable from the root injector for the life of the tab. Nothing goes red
    // about it either way.
    //
    // **Not an `effect()` over {@link status}, and the temptation is real** —
    // one reaction beside the signal reads tidier than a call inside a method.
    // It fires on construction, so whether it wipes a set that has already been
    // adopted is decided by injection order, which nothing here controls. And
    // the only honest predicate it could carry is "lock on `'anonymous'`":
    // locking on `'unreachable'` destroys both keys over one blinked request
    // and demands a full WebAuthn ceremony to get them back, which is exactly
    // the failure `auth.guard.ts` and `guest.guard.ts` exist to prevent,
    // reappearing one layer down. That asymmetry is already written twice, in
    // the guards and in {@link readingOf}; writing it a third time is how the
    // third copy drifts.
    //
    // No establishing arm touches custody. {@link established} discards the
    // provider's token and nothing else: a session beginning says nothing about
    // which factor opened it, and the two paths that know — registration and
    // sign-in — hand the keys over themselves. {@link establishedLocked} finds
    // custody empty, for the reasons written over it, so ending custody stays
    // this method's alone.
    this.custody.lock();
  }

  // The mirror of `ended()`, called when a leg that establishes a session has
  // just answered — registration is the first. A set rather than a re-probe for
  // the reason stated four lines above: the server has said what it thinks, and
  // asking again replaces an answer with a guess. Here it would also cost a
  // round trip at the happiest moment of the flow and could come back
  // `unreachable`, which is a third reading of a fact the server has already
  // stated in the same breath as the cookie it set.
  //
  // **The budget is the one thing that is asked for, and it is not the same
  // shape of question.** The status is a fact the answering leg already stated;
  // the identifier is a fact nothing in that answer carries and nothing in this
  // browser can derive, so a read is not a guess replacing an answer — it is the
  // only source there is. It touches the status not at all, and it is not
  // awaited: `established()` is called from a subscriber with a navigation on
  // the line after it, and an `await` there would put a round trip between a
  // verified credential and the app for the sake of a value only a *write*
  // needs. Until it lands, {@link budgetId} is `null` and a write says so —
  // `unreachable`, and the remedy is the same press a moment later.
  //
  // **The schedule is asked for beside it, for the same reason and on the same
  // terms.** Neither establishing leg's answer carries it, and the tab may have
  // probed anonymous on its cold load, which leaves `'unread'` — so without
  // this read an owner who signs in with a surviving passkey would see no
  // notice of an erasure a Google sign-in filed until a reload. Not awaited,
  // publishing the schedule and nothing else, and a failure publishes nothing:
  // a 401 here is a cookie that had not landed, never a session ending.
  public established(): void {
    this.beginVisit();
    this.publishStatus('authenticated');
    this.forgetProviderToken();

    void this.readBudget();
    void this.readSchedule();
  }

  // The mirror of {@link established} for the one leg that opens a locked
  // session: `POST /api/locked-session` answering `200`. A set rather than a
  // re-probe, for the same reason — the server has just said what it thinks,
  // and its answer carries the schedule, so nothing is asked afterwards.
  //
  // **Synchronous, and it drops the budget because the status it publishes owns
  // none.** A locked session reads no budget content of any kind (FR-113), so
  // an identifier left standing would be keyed into a write that session may
  // not make.
  //
  // **It says nothing to custody, because custody holds nothing here.** The
  // provider hand-off this leg spends is taken only on a fresh load and dropped
  // on any published session, and the server refuses a locked sign-in over a
  // full session with a `409`, so no tab that unlocked keys reaches this `200`.
  // Ending custody stays {@link ended}'s alone; a lock here would be a second
  // owner of that act, guarding a state that cannot occur.
  //
  // The provider's tokens go last and through the guarded discard, so a throw
  // there cannot unpublish the session set first.
  public establishedLocked(answer: SessionDto): void {
    this.beginVisit();
    this.publishStatus('locked-session');
    this.budgetSignal.set(null);
    this.publishSchedule(SessionService.scheduleOf(answer));
    this.forgetProviderToken();
  }

  // The schedule request's `200` names the instant the server stored, the same
  // instant on every repeat, and the release screen renders it from here.
  public erasureScheduled(takesEffectAtUtc: string): void {
    this.publishSchedule({ takesEffectAtUtc });
  }

  /**
   * The visit this tab is in, for a request whose answer will be published
   * here later. Read it just before the request goes out and hand it back
   * with the answer; {@link erasureCancelled} publishes only if it is still
   * the current one.
   *
   * A method and not a signal: nothing renders it, and a reader reacting to
   * it would be reacting to sign-in and sign-out under another name.
   */
  public sessionToken(): SessionToken {
    return this.#visit as SessionToken;
  }

  // A cancellation's `204`: the server saying nothing is scheduled. `null` and
  // never `'unread'`, which would claim nobody had said. It moves the
  // generation, so a schedule read sent before the `204` cannot resurrect the
  // notice when its older answer lands.
  //
  // **Published only into the visit it was sent in.** The session can end, and
  // another begin, while the cancelling request is out; its answer describes
  // the account as it was then, and published over the new visit it would
  // take down a notice about an erasure nobody withdrew — or tell the next
  // occupant of the tab, owed `'unread'`, that nothing is scheduled. Judged
  // against the visit and never the generation, which a schedule request's
  // `200` moves inside the same visit.
  public erasureCancelled(sentUnder: SessionToken): void {
    if (sentUnder !== this.sessionToken()) {
      return;
    }

    this.publishSchedule(null);
  }

  // Asks the server for the schedule again, for a tab that has just come back
  // into view — `ShellComponent` calls it on `visibilitychange`. A Google
  // sign-in on another device can file an erasure at any moment of a visit,
  // and this is how a tab left open learns of it without a reload.
  //
  // **Not a probe, and not a timer.** It publishes the schedule and nothing
  // else, and a failure publishes nothing: the read carries
  // `EXPECTS_UNAUTHENTICATED`, so the interceptor never hears its 401, and
  // reading that 401 as `anonymous` here would sign a tab out because it came
  // back into view. No polling, because the API scales to zero and a timer in
  // every open tab would keep it awake for nobody.
  //
  // Nothing is asked for a tab holding no session, and nothing while another
  // schedule read is out.
  public refreshSchedule(): void {
    const status = this.statusSignal();

    if (status !== 'authenticated' && status !== 'locked-session') {
      return;
    }

    if (this.#scheduleReads > 0) {
      return;
    }

    void this.readSchedule();
  }

  // Drops the provider's tokens once this tab has published a session, from
  // every arm that publishes one — the probe's full and locked answers,
  // {@link established} and {@link establishedLocked}.
  //
  // **Owned here rather than by the paths that establish a session**, for the
  // reason {@link ended} owns `custody.lock()`: a third establishing path will
  // be written by somebody thinking about sign-in rather than about the id
  // token sitting in `sessionStorage`, and put here it discards for free.
  //
  // **Correct only while every flow that uses the provider reads its answer
  // before a session is published.** Registration and the locked sign-in spend
  // the token on the request that opens the session; the email change reads
  // its answer before the probe. A tab holding a session then has no use for
  // it. A future flow that needs a provider token *after* a session stands
  // breaks that, and this call with it.
  //
  // **Guarded, because it is housekeeping and the session is the fact.** A
  // throw out of the library — `sessionStorage` refused in a locked-down
  // browser, a quota error — must not unpublish a session the server just
  // confirmed, nor throw out of a subscriber with a navigation on the next
  // line. It is logged through the funnel instead, reason and projection only.
  private forgetProviderToken(): void {
    try {
      this.auth.forgetProviderToken();
    } catch (error: unknown) {
      logFailure('Provider token discard failed', error);
    }
  }

  // A visit begins or ends: on {@link ended}, {@link established},
  // {@link establishedLocked} and the probe's full and locked arms — never on
  // the probe's anonymous or unreachable arms, which learned of no visit, and
  // never on a schedule write, which is a visit's own.
  private beginVisit(): void {
    this.#visit += 1;
  }

  // The two writers every published status and every schedule not read by
  // {@link readSchedule} goes through, so neither can be written without
  // moving the generation that read is judged against.
  private publishStatus(status: SessionStatus): void {
    this.#generation += 1;
    this.statusSignal.set(status);
  }

  private publishSchedule(schedule: ScheduledErasure): void {
    this.#generation += 1;
    this.scheduledErasureSignal.set(schedule);
  }

  // Reads the schedule alone, publishing nothing else and never rejecting.
  //
  // **Two conditions on publishing, and each guards against a true answer
  // becoming a false claim.** The generation has not moved since the read was
  // sent — the session did not end or begin again, and nothing wrote the
  // schedule meanwhile, a cancellation's `204` above all. And the answer's
  // kind is the kind of session this tab holds: a body describing a locked
  // session to a full one is not an answer about the session here.
  //
  // `getSession()` carries `EXPECTS_UNAUTHENTICATED`, so a 401 lands in the
  // `catch` and nowhere else; see {@link refreshSchedule} and
  // {@link established} for why each caller wants exactly that.
  private async readSchedule(): Promise<void> {
    const sentUnder = this.#generation;

    this.#scheduleReads += 1;

    try {
      const answer = await firstValueFrom(this.api.getSession());

      if (
        sentUnder === this.#generation &&
        SessionService.statusOfKind(answer.kind) === this.statusSignal()
      ) {
        this.scheduledErasureSignal.set(SessionService.scheduleOf(answer));
      }
    } catch {
      // Nothing: a read that did not land learned nothing about the schedule.
    } finally {
      this.#scheduleReads -= 1;
    }
  }

  // Reads the budget alone, publishing nothing else and never rejecting.
  //
  // Called from {@link established} and, for a full session, from the probe.
  //
  // **An `EXPECTS_UNAUTHENTICATED` method**, for the reason `me-api.service.ts`
  // writes out over `getAccountKeys`: this request is made by a browser that
  // has just been handed a session, or told on a cold load that it holds one,
  // and a 401 to it is a cookie that had not landed rather than a session
  // ending. Unmarked, it routes
  // its own refusal into `sessionExpiryInterceptor` — the single owner of "the
  // session ended" — which navigates to `/welcome` from underneath a screen that
  // has just succeeded, through an edge no import graph shows.
  //
  // A failure publishes nothing: `null` is already what the signal holds when
  // nothing has said — the probe sets it before calling — and setting it here
  // would be one more writer of a value that has enough.
  private async readBudget(): Promise<void> {
    try {
      const me = await firstValueFrom(this.api.getSessionOwner());

      this.budgetSignal.set(SessionService.budgetOf(me));
    } catch {
      // Nothing. See above.
    }
  }

  // The identifier the answer carried, or `null` for a body that carried none.
  //
  // **A boundary check and deliberately not a refusal.** `me-api.service.ts`
  // argues why a body of the wrong shape is refused there rather than coerced;
  // this member is the exception the same argument produces, because refusing it
  // would take the *status* down with it. A response missing this member still
  // says there is a session and whose it is, and reading that as `unreachable`
  // signs somebody out over a version skew. `null` is the honest reading: signed
  // in, tenancy unknown, writes refused with a word whose remedy is a reload.
  //
  // **It folds nothing.** A spelling that is not the canonical one is passed
  // through as it arrived and refused by the codec that owns the grammar — the
  // rule `blind-index.ts` keeps at its own door, because a repair made here
  // would invent a second spelling of a value that has one, at the writing end,
  // where every row keyed under the invented one is a row no lookup reproduces.
  // `''` is the one value that becomes `null`, and that is not a fold: it is the
  // absence of an answer wearing the type of one.
  private static budgetOf(me: MeDto): string | null {
    const answered: unknown = me.budgetId;

    return typeof answered === 'string' && answered !== '' ? answered : null;
  }

  // The schedule an answer carried, copied off the wire object rather than
  // held by reference to it.
  private static scheduleOf(answer: SessionDto): ScheduledErasure {
    return answer.erasure === null
      ? null
      : { takesEffectAtUtc: answer.erasure.takesEffectAtUtc };
  }

  // The status a session of this kind is published as. Read off the answer as
  // `unknown`, for the probe's reason: the decoder's type is its claim.
  private static statusOfKind(kind: unknown): SessionStatus | null {
    if (kind === 'full') {
      return 'authenticated';
    }

    return kind === 'locked' ? 'locked-session' : null;
  }

  private static readingOf(error: unknown): SessionStatus {
    // 401 is the server saying it knows who is asking and the answer is nobody;
    // 403 is the first-party refusal of a request missing `X-Budgetoid-Client`.
    // A locked session is not among them any more — the session route answers
    // it `200` with its kind. Neither describes a session and neither has a
    // next step that differs from the other's, so the two collapse — the one
    // collapse here that is correct.
    if (
      error instanceof HttpErrorResponse &&
      (error.status === 401 || error.status === 403)
    ) {
      return 'anonymous';
    }

    // Everything else: status `0` for a request that never reached a server, a
    // 500 from a server that is up and broken, a timeout, a body that failed to
    // parse. None of them is a statement about who is asking, and an
    // implementation reading "not 200" as "not signed in" is the defect
    // `'unreachable'`, one of the five states, exists to prevent.
    return 'unreachable';
  }
}
