import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import {
  MeApiService,
  type CredentialSummary,
  type MeDto,
} from '@app-core/api/me-api.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SettingsService } from './settings.service';

const PASSKEY: CredentialSummary = {
  id: '019f0000-0000-7000-8000-000000000001',
  type: 'passkey',
  createdAtUtc: '2026-03-11T22:00:00Z',
};
const FEDERATED: CredentialSummary = {
  id: '019f0000-0000-7000-8000-000000000002',
  type: 'federated',
  createdAtUtc: '2026-01-12T08:30:00Z',
};

class MeApiStub {
  public getMe = vi.fn(
    (): Observable<MeDto> => of({ email: 'owner@budgetoid.test' }),
  );
  public getExport = vi.fn(
    (): Observable<Blob> => of(new Blob(['{"schemaVersion":1}'])),
  );
  public getCredentials = vi.fn(
    (): Observable<readonly CredentialSummary[]> => of([FEDERATED, PASSKEY]),
  );
  // A count rather than a zero, so the default answer is distinguishable from
  // both of the states the service holds apart: a stub answering `0` would make
  // "published the server's count" and "seeded a zero" the same observation.
  public getRecoveryCodes = vi.fn((): Observable<number> => of(4));
}

class FileDownloadStub {
  public save = vi.fn((blob: Blob, filename: string): void => undefined);
}

describe('SettingsService', () => {
  let service: SettingsService;
  let api: MeApiStub;
  let download: FileDownloadStub;

  beforeEach(() => {
    api = new MeApiStub();
    download = new FileDownloadStub();
    TestBed.configureTestingModule({
      providers: [
        SettingsService,
        { provide: MeApiService, useValue: api },
        { provide: FileDownloadService, useValue: download },
      ],
    });
    service = TestBed.inject(SettingsService);
  });

  it('saves the exported bytes under a client-minted name', () => {
    // Arrange
    const blob = new Blob(['{"schemaVersion":1}'], {
      type: 'application/json',
    });
    api.getExport.mockReturnValue(of(blob));

    // Act
    service.export();

    // Assert
    expect(download.save).toHaveBeenCalledTimes(1);
    // Identity, not equality — this is the whole reason the client is a pipe.
    // `toHaveBeenCalledWith(blob)` compares structurally and would go green on
    // a service that parsed the document and re-serialized it, which is
    // exactly the round-trip that degrades numeric(14,4) amounts.
    expect(download.save.mock.calls[0][0]).toBe(blob);
    // The exact format is pinned by export-filename.spec; a regex here keeps
    // this test off the clock while still proving the name is minted locally
    // rather than read from an unreadable cross-origin Content-Disposition.
    expect(download.save.mock.calls[0][1]).toMatch(
      /^budgetoid-export-\d{8}T\d{6}Z\.json$/,
    );
  });

  it('saves nothing when the export fails', () => {
    // Arrange
    api.getExport.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.export();

    // Assert
    // Control for the test above: a service that called save() from a
    // finalize — or from a catchError that fell through — passes the happy
    // path and writes an empty or undefined file on every failure.
    expect(download.save).not.toHaveBeenCalled();
  });

  it('reports a lapsed session as the ordinary failure', () => {
    // Arrange
    api.getExport.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 401 })),
    );

    // Act
    service.export();

    // Assert
    // The one status this service used to read, kept as an input precisely
    // because it is the one a reader will special-case again. 401 is
    // `sessionExpiryInterceptor`'s to answer — it declares the session over and
    // takes the browser to `/welcome` — so by the time this handler runs there
    // is no screen left to say a second sentence on, and the state it records
    // is the same one every other failure records. Deleting this test and
    // leaving the 500 case below would let a reinstated arm pass unnoticed.
    expect(service.exportFailure()).toBe('failed');
  });

  it('reports a refused export build as failed', () => {
    // Arrange
    api.getExport.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.export();

    // Assert
    expect(service.exportFailure()).toBe('failed');
  });

  it('reports no failure before the first export', () => {
    // Assert
    // Control for the clearing test below: an implementation that only ever
    // *sets* exportFailure to null passes "a later success clears it" without
    // a failure having been visible at any point.
    expect(service.exportFailure()).toBeNull();
    expect(service.exported()).toBe(false);
  });

  it('clears a previous failure once an export succeeds', () => {
    // Arrange
    api.getExport.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    service.export();
    expect(service.exportFailure()).toBe('failed');

    // Act
    service.export();

    // Assert
    expect(service.exportFailure()).toBeNull();
    expect(service.exported()).toBe(true);
  });

  it('ignores a second export while one is in flight', () => {
    // Arrange
    const gate = new Subject<Blob>();
    api.getExport.mockReturnValue(gate);

    // Act
    service.export();
    service.export();

    // Assert
    // A double-click on the button must not cost two exports; the server
    // builds the whole document per request.
    expect(api.getExport).toHaveBeenCalledTimes(1);
  });

  it('exports again once the first has finished', () => {
    // Arrange
    const gate = new Subject<Blob>();
    api.getExport.mockReturnValue(gate);
    service.export();
    gate.next(new Blob(['{"schemaVersion":1}']));
    gate.complete();

    // Act
    service.export();

    // Assert
    // Control for the test above: a service that latched `exporting` and
    // never released it exports exactly once per page load and is green on
    // the in-flight test alone.
    expect(api.getExport).toHaveBeenCalledTimes(2);
  });

  it('publishes the account email', () => {
    // Arrange
    api.getMe.mockReturnValue(of({ email: 'owner@budgetoid.test' }));

    // Act
    service.loadEmail();

    // Assert
    expect(service.email()).toBe('owner@budgetoid.test');
  });

  it('reports an email it could not load', () => {
    // Arrange
    api.getMe.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 401 })),
    );

    // Act
    service.loadEmail();

    // Assert
    // Control for the test above, and the reason both halves are asserted:
    // an implementation holding a placeholder string publishes something that
    // looks like an address and never admits the load failed. The screen must
    // be able to tell "no email yet" from "here is your email".
    expect(service.emailFailed()).toBe(true);
    expect(service.email()).toBeNull();
  });

  it('drops the previous email while a new load is running', () => {
    // Arrange
    api.getMe.mockReturnValueOnce(of({ email: 'first@budgetoid.test' }));
    service.loadEmail();
    expect(service.email()).toBe('first@budgetoid.test');
    const gate = new Subject<MeDto>();
    api.getMe.mockReturnValue(gate);

    // Act
    service.loadEmail();

    // Assert
    // The address answers the read that is running, so it is absent while one
    // is. Kept, it sits under `Email address` throughout the retry and then
    // beside "Couldn't load your email address. Reload the page." if the retry
    // gives up — a reader told the address could not be loaded, and told an
    // address.
    expect(service.email()).toBeNull();

    // And a *different* address comes back, so the clearing is not an address
    // lost — and a service that republished the first one would not pass.
    gate.next({ email: 'second@budgetoid.test' });
    gate.complete();
    expect(service.email()).toBe('second@budgetoid.test');
  });

  it('leaves no email behind when a reload fails', () => {
    // Arrange
    api.getMe.mockReturnValueOnce(of({ email: 'owner@budgetoid.test' }));
    service.loadEmail();
    // The first load really did publish an address. Without this the assertion
    // below holds on a service that never publishes one.
    expect(service.email()).toBe('owner@budgetoid.test');
    api.getMe.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadEmail();

    // Assert
    // `null`, and deliberately not `''`: the screen reads `null` as "not
    // loaded" and renders a blank value beside the failure sentence, which is
    // the same fact said once. An empty string is the same render arrived at by
    // a different claim, and the distinction is the one the whole signal type
    // exists for. `reports an email it could not load` above pins this from a
    // cold start; this pins it after an address has been on screen.
    expect(service.emailFailed()).toBe(true);
    expect(service.email()).toBeNull();
  });

  it('publishes the credentials in the order the server sent them', () => {
    // Arrange
    // **Descending**, and that is the whole test. The server orders ascending,
    // so every other fixture in this suite is already in the order a client-side
    // sort would produce — asserted against one of those, this test is green on
    // a service that sorts, on one that reverses and sorts, and on one that
    // passes the array through, which is to say it asserts nothing. Handed an
    // order the client would never choose for itself, only the last of those
    // three survives.
    api.getCredentials.mockReturnValue(of([PASSKEY, FEDERATED]));

    // Act
    service.loadCredentials();

    // Assert
    // The server orders by registration instant and the screen shows that
    // order. Sorting again here would be a second opinion about a fact the
    // server already settled, and would diverge from it the moment either side
    // changed its mind.
    expect(service.credentials()).toEqual([PASSKEY, FEDERATED]);
  });

  it('holds no credentials before the load is asked for', () => {
    // Assert
    // `null` is load-bearing and is not `[]`. "Not asked yet" and "you have no
    // way of signing in" are different facts and the screen renders them
    // differently; a service seeding an empty array tells a user mid-load that
    // nothing is attached to their account.
    expect(service.credentials()).toBeNull();
    expect(service.credentialsFailed()).toBe(false);
  });

  it('publishes an account with no credentials as an empty list', () => {
    // Arrange
    api.getCredentials.mockReturnValue(of([]));

    // Act
    service.loadCredentials();

    // Assert
    // Control for the test above, in the other direction: a service that left
    // the signal `null` on an empty response is indistinguishable from one that
    // never answered, and the screen would sit on its loading line forever.
    expect(service.credentials()).toEqual([]);
  });

  it('reports credentials it could not load', () => {
    // Arrange
    api.getCredentials.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadCredentials();

    // Assert
    // Both halves. Without the second, a service that swallowed the error into
    // an empty array would pass the flag assertion while the screen told the
    // user, in plain words, that they have no way of signing in — on a page
    // they are signed in to.
    expect(service.credentialsFailed()).toBe(true);
    expect(service.credentials()).toBeNull();
  });

  it('clears a previous failure when the load is asked for again', () => {
    // Arrange
    api.getCredentials.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    service.loadCredentials();
    expect(service.credentialsFailed()).toBe(true);

    // Act
    service.loadCredentials();

    // Assert
    // The failure describes the last attempt, not the screen. Left latched, a
    // reload that succeeded would still be accused of failing.
    expect(service.credentialsFailed()).toBe(false);
    expect(service.credentials()).toEqual([FEDERATED, PASSKEY]);
  });

  it('drops the previous credentials while a new load is running', () => {
    // Arrange
    api.getCredentials.mockReturnValueOnce(of([FEDERATED, PASSKEY]));
    service.loadCredentials();
    expect(service.credentials()).toEqual([FEDERATED, PASSKEY]);
    const gate = new Subject<readonly CredentialSummary[]>();
    api.getCredentials.mockReturnValue(gate);

    // Act
    service.loadCredentials();

    // Assert
    // `null`, and emphatically not `[]`. The list answers the read that is
    // running, so it is absent while one is — and absent is the state the
    // screen renders as "Loading your ways to sign in…". An empty array here
    // would tell somebody mid-retry, in plain words, that nothing is attached
    // to their account.
    expect(service.credentials()).toBeNull();

    // And a *different* list comes back, so the clearing is not a list lost —
    // and a service that republished the first one would not pass.
    gate.next([PASSKEY]);
    gate.complete();
    expect(service.credentials()).toEqual([PASSKEY]);
  });

  it('leaves no credentials behind when a reload fails', () => {
    // Arrange
    api.getCredentials.mockReturnValueOnce(of([FEDERATED, PASSKEY]));
    service.loadCredentials();
    // The first load really did publish a list. Without this the assertion
    // below holds on a service that never publishes one.
    expect(service.credentials()).toEqual([FEDERATED, PASSKEY]);
    api.getCredentials.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadCredentials();

    // Assert
    // `null`, never `[]` — the two are never collapsed anywhere on this path.
    // `reports credentials it could not load` above pins that from a cold
    // start, where the signal has never held anything else; this pins it after
    // a list has been on screen, which is the state a retry control reaches.
    expect(service.credentialsFailed()).toBe(true);
    expect(service.credentials()).toBeNull();
  });

  it('holds no recovery count before the load is asked for', () => {
    // Assert
    // `null` is load-bearing and is not `0`. "Not asked yet" and "you have no
    // codes left" are different facts and the screen renders them differently:
    // a blank line box that holds its height, and a sentence. A service seeding
    // `0` tells a user mid-load that they have nothing to fall back on.
    //
    // `recoveryLoading` is false here and that is the third fact: at rest is
    // not loading, which is what keeps the section's `role="status"` region
    // empty until a request is actually running.
    expect(service.recoveryRemaining()).toBeNull();
    expect(service.recoveryFailed()).toBe(false);
    expect(service.recoveryLoading()).toBe(false);
  });

  it('reports the recovery count as loading while the request runs', () => {
    // Arrange
    const gate = new Subject<number>();
    api.getRecoveryCodes.mockReturnValue(gate);

    // Act
    service.loadRecoveryCodes();

    // Assert
    // Held open deliberately: with a synchronous observable the flag is set and
    // cleared inside the call and nothing can observe it, so a service that
    // never set it at all would pass. The count is still absent, which is what
    // distinguishes this from the resolved states.
    expect(service.recoveryLoading()).toBe(true);
    expect(service.recoveryRemaining()).toBeNull();

    gate.next(4);
    gate.complete();
    expect(service.recoveryLoading()).toBe(false);
    expect(service.recoveryRemaining()).toBe(4);
  });

  it('stops loading when the count cannot be read', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadRecoveryCodes();

    // Assert
    // A load that gave up is not still running. Left latched, the section would
    // render its failure sentence and keep the reader waiting on the request
    // that produced it — the two states the book calls exclusive by structure.
    expect(service.recoveryLoading()).toBe(false);
    expect(service.recoveryFailed()).toBe(true);
  });

  it('publishes how many codes are left', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValue(of(7));

    // Act
    service.loadRecoveryCodes();

    // Assert
    expect(service.recoveryRemaining()).toBe(7);
  });

  it('publishes a spent set as zero rather than as no answer', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValue(of(0));

    // Act
    service.loadRecoveryCodes();

    // Assert
    // The other direction of the test above, and the reason the signal is
    // `number | null` rather than `number`: a service that left the signal
    // `null` on a zero — or a template testing the count for truthiness — is
    // indistinguishable from one that never answered, and the screen would sit
    // on its loading line forever for the very account that most needs telling.
    expect(service.recoveryRemaining()).toBe(0);
    expect(service.recoveryFailed()).toBe(false);
  });

  it('reports a count it could not load', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadRecoveryCodes();

    // Assert
    // Both halves. This is the defect the shape exists to prevent: a
    // `catchError` returning `of(0)` passes the flag assertion while telling
    // somebody whose request failed, in plain words, that they have no recovery
    // codes — a claim about their account manufactured out of a network
    // failure.
    expect(service.recoveryFailed()).toBe(true);
    expect(service.recoveryRemaining()).toBeNull();
  });

  it('clears a previous recovery failure when the load is asked for again', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    service.loadRecoveryCodes();
    expect(service.recoveryFailed()).toBe(true);

    // Act
    service.loadRecoveryCodes();

    // Assert
    // The failure describes the last attempt, not the screen.
    expect(service.recoveryFailed()).toBe(false);
    expect(service.recoveryRemaining()).toBe(4);
  });

  it('drops the previous count while a new load is running', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValueOnce(of(10));
    service.loadRecoveryCodes();
    expect(service.recoveryRemaining()).toBe(10);
    const gate = new Subject<number>();
    api.getRecoveryCodes.mockReturnValue(gate);

    // Act
    service.loadRecoveryCodes();

    // Assert
    // The count answers the read that is running, so it is absent while one is.
    // Kept, it sits beside the loading line — and then beside the failure
    // sentence if the read gives up, which is the pair a browser actually
    // rendered: "Couldn't load your recovery codes. Reload the page." above
    // "You have 10 recovery codes left."
    expect(service.recoveryLoading()).toBe(true);
    expect(service.recoveryRemaining()).toBeNull();

    // And it comes back with an answer, so the clearing is not a count lost.
    gate.next(9);
    gate.complete();
    expect(service.recoveryRemaining()).toBe(9);
  });

  it('leaves no count behind when a reload fails', () => {
    // Arrange
    api.getRecoveryCodes.mockReturnValueOnce(of(10));
    service.loadRecoveryCodes();
    // The first load really did publish a count. Without this the assertion
    // below holds on a service that never publishes one.
    expect(service.recoveryRemaining()).toBe(10);
    api.getRecoveryCodes.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    // Act
    service.loadRecoveryCodes();

    // Assert
    // `null`, and deliberately not `0`: the two are never collapsed anywhere on
    // this path, because a failed request must not tell somebody they have no
    // way back into their account. `reports a count it could not load` above
    // pins that from a cold start; this pins it after an answer has been on
    // screen, which is the state the browser was in.
    expect(service.recoveryFailed()).toBe(true);
    expect(service.recoveryRemaining()).toBeNull();
  });
});
