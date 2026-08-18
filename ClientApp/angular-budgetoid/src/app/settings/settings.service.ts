import { Injectable, Signal, inject, signal } from '@angular/core';
import {
  MeApiService,
  type CredentialSummary,
} from '@app-core/api/me-api.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { EMPTY, catchError, finalize } from 'rxjs';
import { exportFilename } from './export-filename';

// One member now that `sessionExpiryInterceptor` owns "the session ended" for
// every request the application makes: a 401 on the export is noticed there,
// declares the session over and takes the browser to `/welcome`, so by the time
// this service's `catchError` runs there is no screen left to say a second
// sentence on. What is left is the one failure that keeps the reader where they
// are — the server refused to build the file.
//
// `loadCredentials` below argues that a discriminant nothing discriminates on is
// a second thing to keep in step with the template, and by that argument this
// could now be a boolean. It stays a named literal because the change is a
// *removal*: dropping the arm the interceptor took over leaves every reader of
// `exportFailure` — the outcome region, its tests — reading the same state with
// one fewer value in it, where renaming the member to a flag would be a rewrite
// of the screen's outcome contract for no behaviour. A word also reads better
// than `true` at the point where a failure is recorded.
export type ExportFailure = 'failed';

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
  // its keep because a lapsed session sends the user somewhere and a refused
  // build does not; here every way this can fail — 401, 500, offline — ends in
  // the same next step, which is to load the page again. A discriminant nothing
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
    // The server builds the entire document per request, so a double-click on
    // the button must not cost two of them.
    if (this.exportingSignal()) {
      return;
    }

    this.exportingSignal.set(true);
    this.exportedSignal.set(false);
    this.exportFailureSignal.set(null);

    this.api
      .getExport()
      .pipe(
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
      .subscribe((blob) => {
        // The blob is handed on untouched — not read, not parsed, not rebuilt.
        this.download.save(blob, exportFilename(new Date()));
        this.exportedSignal.set(true);
      });
  }

  // `failureFor` used to sit here and read the status off the error. It is gone
  // rather than reduced to a function returning one constant: every way this
  // read can fail — a refused build, a 500, an unreachable server — now ends in
  // the same sentence, and the one status that ended somewhere else is answered
  // before this handler runs.
  //
  // The error itself is still not logged, departing from the neighbouring
  // `accounts.service.ts`, which writes the error object to the console before
  // swallowing it. This route answers with the user's own data, and in
  // Development its error body carries a stack trace; copying either into the
  // console puts it somewhere with a different audience than the response.
  // Setting the signal is the handling.
}
