import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { MeApiService } from '@app-core/api/me-api.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { EMPTY, catchError, finalize } from 'rxjs';
import { exportFilename } from './export-filename';

export type ExportFailure = 'unauthenticated' | 'failed';

@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly api = inject(MeApiService);
  private readonly download = inject(FileDownloadService);

  private readonly emailSignal = signal<string | null>(null);
  private readonly emailFailedSignal = signal(false);
  private readonly exportingSignal = signal(false);
  private readonly exportedSignal = signal(false);
  private readonly exportFailureSignal = signal<ExportFailure | null>(null);

  public readonly email: Signal<string | null> = this.emailSignal.asReadonly();
  public readonly emailFailed: Signal<boolean> =
    this.emailFailedSignal.asReadonly();
  public readonly exporting: Signal<boolean> =
    this.exportingSignal.asReadonly();
  public readonly exported: Signal<boolean> = this.exportedSignal.asReadonly();
  public readonly exportFailure: Signal<ExportFailure | null> =
    this.exportFailureSignal.asReadonly();

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
