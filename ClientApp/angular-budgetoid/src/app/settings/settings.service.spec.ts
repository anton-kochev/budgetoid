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

  it('reports a lapsed session as unauthenticated', () => {
    // Arrange
    api.getExport.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 401 })),
    );

    // Act
    service.export();

    // Assert
    // Paired with the 500 case below: each is the other's control, because a
    // service returning one constant failure passes exactly one of them. The
    // distinction is load-bearing for the screen — a lapsed session sends the
    // user to sign in again, a refused build does not.
    expect(service.exportFailure()).toBe('unauthenticated');
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

  it('publishes the credentials in the order the server sent them', () => {
    // Arrange
    api.getCredentials.mockReturnValue(of([FEDERATED, PASSKEY]));

    // Act
    service.loadCredentials();

    // Assert
    // The server orders ascending by registration instant and the screen shows
    // that order. Sorting again here would be a second opinion about a fact the
    // server already settled, and would diverge from it the moment either side
    // changed its mind.
    expect(service.credentials()).toEqual([FEDERATED, PASSKEY]);
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
});
