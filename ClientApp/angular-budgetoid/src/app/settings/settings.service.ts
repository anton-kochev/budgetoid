import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import {
  MeApiService,
  type CredentialSummary,
} from '@app-core/api/me-api.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { EMPTY, catchError, finalize } from 'rxjs';
import { exportFilename } from './export-filename';

export type ExportFailure = 'unauthenticated' | 'failed';

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
  // interchangeable", and its `role="status"` region is "empty **at rest**".
  // Inferred loading collapses at rest into loading, because both are a count
  // that has not arrived, and the region would then hold a line from first
  // paint instead of gaining one. This is `exporting`'s shape, on the section
  // whose specification asks for it.
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
    this.api
      .getCredentials()
      .pipe(
        catchError(() => {
          this.credentialsFailedSignal.set(true);
          return EMPTY;
        }),
      )
      // On failure nothing is published, so the list stays `null`. That is
      // deliberate: an empty array here would render the sentence saying this
      // account has no way of signing in, which is a claim about the account
      // rather than about the request.
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
        catchError((error: unknown) => {
          this.exportFailureSignal.set(SettingsService.failureFor(error));
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

  // A discriminant, not a sentence: the copy belongs to the template, and only
  // the two cases the screen acts on differently are distinguished. A lapsed
  // session sends the user to sign in again; nothing else does. The server
  // cannot say more than this even if we wanted it to — the two 500s reachable
  // on this route are indistinguishable to a client, and so are the two 401s.
  //
  // Nothing is logged here, departing from the neighbouring
  // `accounts.service.ts`, which writes the error object to the console before
  // swallowing it. This route answers with the user's own data, and in
  // Development its error body carries a stack trace; copying either into the
  // console puts it somewhere with a different audience than the response.
  // Setting the signal is the handling.
  private static failureFor(error: unknown): ExportFailure {
    if (error instanceof HttpErrorResponse && error.status === 401) {
      return 'unauthenticated';
    }

    return 'failed';
  }
}
