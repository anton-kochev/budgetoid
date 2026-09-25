import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  MeApiService,
  type CredentialSummary,
} from '@app-core/api/me-api.service';
import {
  AccountKeyCustodyService,
  type CustodyHolding,
} from '@app-core/security/account-key-custody.service';
import { KeyRotationService } from '@app-core/security/key-rotation.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { SessionService } from '@app-core/session/session.service';
import { EMPTY, catchError, finalize, from, switchMap } from 'rxjs';
import {
  decodeExportDocument,
  openExportDocument,
  serializeExportDocument,
} from './export-document';
import { exportFilename } from './export-filename';

// Four words, because they are four different next steps for a person, and each
// of them means nothing was saved.
//
// `failed` is the request: nobody answered, or the server refused to build the
// document, and trying again later can change that. A 401 is not a fifth word —
// `sessionExpiryInterceptor` owns "the session ended", and has taken the browser
// to `/welcome` before this service's `catchError` runs.
//
// The other three are the body, judged in this tab. `unrecognised` is a document
// this bundle could not read — the strict decoder refused its shape or an
// envelope's wire string, before any cipher ran — and a reload is the one act
// that can change it. `locked` is custody letting go of the keys before the file
// was handed to the browser. `unreadable` is a value that did not open under
// keys this tab *did* hold, which nothing on this screen changes. They are told
// apart by the result `export-document.ts` returns, never by a message.
export type ExportFailure = 'failed' | 'unrecognised' | 'locked' | 'unreadable';

// Which sentence stands above an Export that is off. `null` beside it means
// none: a ready control, and one that is off while custody is mid-ceremony.
export type ExportBlock = 'rotating' | 'locked';

// What one export ends as, once the server has answered.
type ExportOutcome = 'exported' | Exclude<ExportFailure, 'failed'>;

// Deliberately not `providedIn: 'root'`: every signal below is the state of one
// visit to the settings screen, not of the application, so `SettingsComponent`
// provides it and the state is discarded with the screen. Held at the root, an
// outcome outlives its screen — export, navigate away, come back, and the
// second visit renders the first one's `Exported.` or its failure.
//
// Rejected: clearing the outcome from `ngOnInit`. That needs a public method
// whose only job is to compensate for a lifetime mismatch, and it heals only
// the signals someone remembered to list. `exporting` has no right answer —
// clearing it reopens the guard against a duplicate request, leaving it set
// strands `Preparing your file…` on screen forever after navigating mid-export.
@Injectable()
export class SettingsService {
  private readonly api = inject(MeApiService);
  private readonly download = inject(FileDownloadService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  // Both root-provided and both read, never written: custody is where the
  // export's opener comes from, whose status decides readiness and whose
  // holding decides the hand-over, and the rotation driver is read for
  // `running` and `staged` — a run this tab knows is staged — and nothing else.
  private readonly custody = inject(AccountKeyCustodyService);
  private readonly rotations = inject(KeyRotationService);

  private readonly emailSignal = signal<string | null>(null);
  private readonly emailFailedSignal = signal(false);
  private readonly exportingSignal = signal(false);
  private readonly exportedSignal = signal(false);
  private readonly exportFailureSignal = signal<ExportFailure | null>(null);
  // `null` is not an empty list and the two are never collapsed: it means the
  // answer has not arrived, and the screen renders that as a loading line
  // instead of as the sentence saying nothing is attached to this account.
  private readonly credentialsSignal = signal<
    readonly CredentialSummary[] | null
  >(null);
  private readonly credentialsFailedSignal = signal(false);
  // `null` is not `0`, and the two are never collapsed. `null` means the answer
  // has not arrived; `0` is a fact about the account — it has no codes left —
  // and the screen renders them as a blank line box and a sentence
  // respectively. A `catchError` that set this to `0` is the specific defect
  // this type exists to make unrepresentable: it would tell somebody whose
  // request failed that they have nothing to sign in with.
  private readonly recoveryRemainingSignal = signal<number | null>(null);
  private readonly recoveryFailedSignal = signal(false);
  // In flight, published rather than inferred from `recoveryRemaining === null`
  // — the third member the neighbouring reads do without, and the book's
  // recovery-codes chapter is why: its six states are "no two of them
  // interchangeable". The count is absent at rest, absent while the read runs
  // **and** absent after the read has failed, so a section inferring its
  // loading line from that absence has one predicate covering three states —
  // and it would go on saying *loading* to somebody whose request has already
  // given up. Published, the flag is set when the read starts and cleared
  // however the read ends, which makes the exclusivity structural rather than
  // dependent on the order the template's branches happen to be written in.
  // This is `exporting`'s shape, on the section whose specification asks for
  // it.
  private readonly recoveryLoadingSignal = signal(false);
  // In flight, and deliberately not published. Every other flag on this service
  // is read by the template; this one is not, because the screen it would be
  // rendered on is about to be replaced by `/welcome` either way. What it is
  // for is the second press: `export`'s guard argues the same case for a
  // request the server pays for, and here a second press would cost a second
  // POST, a second `ended()` and a second navigation to a screen the first one
  // is already on its way to.
  private readonly signingOutSignal = signal(false);

  public readonly email: Signal<string | null> = this.emailSignal.asReadonly();
  public readonly emailFailed: Signal<boolean> =
    this.emailFailedSignal.asReadonly();
  public readonly exporting: Signal<boolean> =
    this.exportingSignal.asReadonly();
  public readonly exported: Signal<boolean> = this.exportedSignal.asReadonly();
  public readonly exportFailure: Signal<ExportFailure | null> =
    this.exportFailureSignal.asReadonly();
  public readonly credentials: Signal<readonly CredentialSummary[] | null> =
    this.credentialsSignal.asReadonly();
  public readonly credentialsFailed: Signal<boolean> =
    this.credentialsFailedSignal.asReadonly();
  public readonly recoveryRemaining: Signal<number | null> =
    this.recoveryRemainingSignal.asReadonly();
  public readonly recoveryFailed: Signal<boolean> =
    this.recoveryFailedSignal.asReadonly();
  public readonly recoveryLoading: Signal<boolean> =
    this.recoveryLoadingSignal.asReadonly();

  // A key rotation is in flight: a run this tab is walking, or one this tab
  // knows is staged. Knows, not is: `staged` is what the rotation driver last
  // read — at start-up, and again when this screen's rotation section loads —
  // and a read that failed publishes none, so a run staged since by another
  // device, or behind a read that did not answer, is not a term here. Both
  // terms, read the way the content screens read them — either alone
  // half-works, and the half that fails is the quiet one.
  private readonly rotating = computed(
    () => this.rotations.running() || this.rotations.staged() !== null,
  );

  // Ready and not busy — **the one predicate the control's `disabled` and
  // `export()`'s guard both read**, so the attribute and the handler cannot
  // cover different states.
  //
  // Ready is written positively: custody says `unlocked` **and** no run is in
  // flight. `locked`, `unlocking` and any word custody grows later arrive not
  // ready; written `!== 'locked'`, the control goes live mid-ceremony and opens
  // nothing. The run term is not caution: a staged run has re-sealed part of
  // the account under keys custody does not hold, so an export over it would
  // end `unreadable` for a reason that has a remedy.
  public readonly pressable: Signal<boolean> = computed(
    () =>
      this.custody.status() === 'unlocked' &&
      !this.rotating() &&
      !this.exportingSignal(),
  );

  // The sentence above an Export that is off, on the notice's terms rather than
  // the form's. The run's whenever a run is in flight, whatever custody says —
  // Unlock pressed mid-run gets the generation on its way out, so the locked
  // sentence's advice is false during one. The lock's on `locked` alone:
  // `unlocking` is off and silent, because the Account keys region is already
  // saying so and the locked sentence would tell somebody to press a button
  // they are holding down. Busy is not a reason either; the region says that.
  public readonly exportBlock: Signal<ExportBlock | null> = computed(() => {
    if (this.rotating()) {
      return 'rotating';
    }

    return this.custody.status() === 'locked' ? 'locked' : null;
  });

  public loadEmail(): void {
    this.emailFailedSignal.set(false);
    // Cleared when the read *starts*, for the reason `loadRecoveryCodes` gives
    // below at length: an address from a previous answer that survives a load
    // which gives up leaves the row saying `Email address: owner@…` under
    // "Couldn't load your email address. Reload the page." — a reader told the
    // address could not be loaded, and told an address. Clearing it only in the
    // `catchError` moves the same pair of sentences one state earlier, onto the
    // retry itself.
    //
    // This value has a way of being *wrong* that the neighbouring two do not,
    // which is why it clears rather than being the one section that keeps its
    // last answer: an email change lands, this read refreshes it, and a refresh
    // that fails would leave the previous address on screen as the answer to
    // the only question this row exists to answer.
    //
    // `null`, never `''` — the screen reads `null` as "not loaded" and renders
    // a blank value beside the sentence. An empty string is the same render
    // arrived at by a different claim, and nothing downstream could tell the
    // two apart again.
    this.emailSignal.set(null);
    this.api
      .getMe()
      .pipe(
        catchError(() => {
          this.emailFailedSignal.set(true);
          return EMPTY;
        }),
      )
      .subscribe((me) => this.emailSignal.set(me.email));
  }

  // Shaped after `loadEmail` rather than after `export`: this is a read the
  // screen starts on its own, not an act the user asks for, so it needs no
  // in-flight guard and no confirmation.
  //
  // One boolean, not an `ExportFailure`-style union. The distinction there earns
  // its keep because its four words are four different next steps; here every
  // way this can fail — 401, 500, offline — ends in the same next step, which is
  // to load the page again. A discriminant nothing
  // discriminates on is a second thing to keep in step with the template.
  public loadCredentials(): void {
    this.credentialsFailedSignal.set(false);
    // Cleared when the read *starts*, the same line in the same position as in
    // `loadEmail` above and `loadRecoveryCodes` below, and for the same reason:
    // rows from a previous answer that survive a load which gives up are listed
    // under "Couldn't load your ways to sign in. Reload the page." Cleared only
    // on failure, they are still listed *during* the retry, beside the loading
    // line that says they have not arrived.
    //
    // `null`, and emphatically not `[]` — the distinction this whole path is
    // shaped around. An empty array renders "Nothing is attached to your
    // account yet.", which would answer a failed request with a claim about the
    // account, on a page the reader is signed in to. `null` renders the loading
    // line while the read runs and nothing at all once it has failed, because
    // the template checks the failure first.
    this.credentialsSignal.set(null);
    this.api
      .getCredentials()
      .pipe(
        catchError(() => {
          this.credentialsFailedSignal.set(true);
          return EMPTY;
        }),
      )
      // The only place a list is ever published, and nothing is published on
      // failure — the list is left where the clearing above put it. An empty
      // array published here, or returned from the `catchError`, would render
      // the sentence saying this account has no way of signing in, which is a
      // claim about the account rather than about the request.
      .subscribe((credentials) => this.credentialsSignal.set(credentials));
  }

  // Shaped after `loadCredentials`, down to the single boolean: this is a read
  // the screen starts on its own, every way it can fail ends in the same next
  // step — load the page again — and a discriminant nothing discriminates on is
  // a second thing to keep in step with the template.
  //
  // The one line to read twice is the `catchError`: it publishes **nothing**,
  // so the count stays `null`. Returning `of(0)` here, or setting the signal in
  // the handler, is the tempting simplification and it is the bug — it would
  // put "You have no recovery codes." on the screen of somebody whose request
  // never got an answer, which is a claim about their account made out of a
  // network failure.
  public loadRecoveryCodes(): void {
    this.recoveryFailedSignal.set(false);
    // Cleared when the read *starts*, not only when it fails, and this is the
    // line that keeps the section's six states exclusive. Left in place, the
    // count from a previous answer survives a load that gives up, and the
    // section renders "Couldn't load your recovery codes. Reload the page."
    // above "You have 10 recovery codes left." — a reader told the count could
    // not be loaded, and told the count. Observed in a browser, not reasoned
    // about.
    //
    // The clearing belongs here rather than in the `catchError` for the same
    // reason `export` clears its outcome before the request rather than after
    // it: a count that is only cleared on failure is still on screen *during*
    // the retry, beside the loading line, which is the same pair of
    // incompatible sentences one state earlier. A number returns with an
    // answer or not at all.
    //
    // `null`, never `0` — the distinction this whole path is shaped around. A
    // zero here would tell somebody whose request is still running that they
    // have no way back into their account.
    this.recoveryRemainingSignal.set(null);
    this.recoveryLoadingSignal.set(true);
    this.api
      .getRecoveryCodes()
      .pipe(
        catchError(() => {
          this.recoveryFailedSignal.set(true);
          return EMPTY;
        }),
        // Runs after `catchError`, so a failure ends in failed *and* not
        // loading. The template checks the failure first regardless — a load
        // that gave up is not still running, and saying both would ask the
        // reader to keep waiting on a request that has already answered.
        finalize(() => this.recoveryLoadingSignal.set(false)),
      )
      .subscribe((remaining) => this.recoveryRemainingSignal.set(remaining));
  }

  public export(): void {
    // The gate the control's `disabled` draws, and it has to be here as well:
    // `disabledInteractive` leaves the DOM `disabled` property false and
    // Material halts the click on anchors only, so a press on a control drawn
    // off still arrives. Its busy half is what stops a double-click costing
    // the server two whole-document builds.
    if (!this.pressable()) {
      return;
    }

    // Which custody the press was made under, taken in the press's own task and
    // before the request exists — so nothing that lands while it is out can be
    // mistaken for the keys the person pressed with. `write` compares against
    // it at the hand-over. Never `null` here: `pressable` has just read
    // `unlocked`, and a holding is absent only off `unlocked`.
    const holding = this.custody.holding();

    this.exportingSignal.set(true);
    this.exportedSignal.set(false);
    this.exportFailureSignal.set(null);

    this.api
      .getExport()
      .pipe(
        // The work after the answer is asynchronous — every field opens on the
        // platform's cipher — so it rides this stream rather than an `async`
        // wrapper around it: the request still leaves in the press's own task,
        // and a refused one still reaches `catchError` synchronously.
        switchMap((text) => from(this.write(text, holding))),
        // The request, and a throw out of `write` — which is a defect in the
        // call rather than a value that failed to open, since every refusal a
        // person can act on comes back as a word. Either way nothing was saved.
        catchError(() => {
          this.exportFailureSignal.set('failed');
          return EMPTY;
        }),
        finalize(() => this.exportingSignal.set(false)),
      )
      // Not torn down with the screen, and that is the point: navigating away
      // mid-export drops this service, but `FileDownloadService` is root-scoped
      // and this subscription runs to completion, so the file the user asked
      // for still lands on disk. Adding `takeUntilDestroyed` here for tidiness
      // would silently cancel that download — the request the user already paid
      // the server's whole-document build for.
      .subscribe((outcome) => {
        if (outcome === 'exported') {
          this.exportedSignal.set(true);
        } else {
          this.exportFailureSignal.set(outcome);
        }
      });
  }

  // The server's text in, a saved file or the word for why not out. **The
  // opened document lives in this frame and nowhere else** — no signal, no
  // field, nothing returned — so no copy of a name outlives the one export
  // that opened it, or the keys that opened it.
  //
  // Whole file or nothing: `openExportDocument` answers `opened` only when
  // every name and note came back as text, and anything else saves nothing.
  // A file carrying a dash where a name was looks complete in a downloads
  // folder years later.
  private async write(
    text: string,
    pressedUnder: CustodyHolding | null,
  ): Promise<ExportOutcome> {
    const decoded = decodeExportDocument(text);

    if (decoded.kind === 'unrecognised') {
      return 'unrecognised';
    }

    // An arrow and not `this.custody.openField`: the method reads custody's
    // `#` fields, so a detached reference type-checks and answers every open
    // with a `TypeError` on the wrong receiver.
    const opened = await openExportDocument(decoded.doc, (binding, wire) =>
      this.custody.openField(binding, wire),
    );

    if (opened.kind !== 'opened') {
      return opened.kind;
    }

    // **The load-bearing hand-over check**: the custody held now must be the
    // custody the press was made under, compared by identity, in the same
    // synchronous block as the save so nothing can land between the two.
    //
    // For a document with nothing to open — a just-registered account, one
    // budget, no name — this is the **only** check between the press and the
    // save, and that window crosses macrotasks: the response lands in a task of
    // its own, and a sign-out followed by a second account's unlock can land
    // before it. Where opens did run, each answers `locked` for a change of
    // hands while its cipher runs, and after the last one only microtasks
    // remain — narrow, not empty.
    //
    // Compared on the holding and never on `status()`: a lock followed by an
    // adoption reads `unlocked` on both sides, and a status check would save a
    // file opened — or, with nothing to open, merely requested — under one
    // custody while another is held.
    if (pressedUnder === null || this.custody.holding() !== pressedUnder) {
      return 'locked';
    }

    this.download.save(
      new Blob([serializeExportDocument(opened.doc)], {
        type: 'application/json',
      }),
      exportFilename(new Date()),
    );

    return 'exported';
  }

  /**
   * Ends the session and leaves for `/welcome`.
   *
   * **A failed sign-out still signs the person out locally, and the two branches
   * below are identical on purpose.** The route is idempotent, and the cookie it
   * clears is `HttpOnly` — this browser cannot read it, cannot clear it, and
   * cannot tell whether it is still live — so there is nothing the client could
   * do differently with the knowledge that the request did not land. What it can
   * do is stop claiming to be signed in, and leave. The alternative is somebody
   * stranded on a signed-in screen pressing a button that keeps failing, in
   * front of whoever is at the keyboard.
   *
   * This is **not** the four-valued reading `SessionService` applies to its own
   * probe, and a reader will try to make it one. That rule refuses to read
   * silence as a *refusal*, because silence is not evidence about the visitor.
   * Here silence is not evidence about the visitor either — but the visitor has
   * already said what they want, so there is no verdict to withhold.
   */
  public signOut(): void {
    if (this.signingOutSignal()) {
      return;
    }

    this.signingOutSignal.set(true);
    this.api.endSession().subscribe({
      next: () => this.leave(),
      error: () => this.leave(),
    });
  }

  // **This order, and the pair is the reason.** Ending the session first is what
  // lets the guard on `/welcome` judge the navigation below against
  // `'anonymous'`. Navigate first and it reads a stale `'authenticated'` and
  // sends the person straight back into the app they just left — holding a
  // cookie the server has already revoked, so their next request is a 401 and
  // the screen they land on says nothing, because nothing failed.
  // `sign-in.service.ts` states the same rule in the other direction.
  private leave(): void {
    this.session.ended();
    void this.router.navigateByUrl('/welcome');
  }

  // `failureFor` used to sit here and read the status off the error. It is gone
  // rather than reduced to a function returning one constant: every way the
  // export *request* can fail — a refused build, a 500, an unreachable server —
  // ends in `failed`, and the one status that ended somewhere else is answered
  // before `export`'s `catchError` runs. The three other words come from the
  // body, and `write` names them.
  //
  // The error itself is still not logged, departing from the neighbouring
  // `accounts.service.ts`, which writes the error object to the console before
  // swallowing it. This route answers with the user's own data, and in
  // Development its error body carries a stack trace; copying either into the
  // console puts it somewhere with a different audience than the response.
  // Setting the signal is the handling.
}
