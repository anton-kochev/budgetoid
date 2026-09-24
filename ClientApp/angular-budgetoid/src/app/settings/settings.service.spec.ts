import { HttpErrorResponse } from '@angular/common/http';
import { signal, type Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  MeApiService,
  type AccountKeyCustodyDto,
  type CredentialSummary,
  type MeDto,
} from '@app-core/api/me-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  KeyRotationService,
  type StagedRotation,
} from '@app-core/security/key-rotation.service';
import {
  NarrativeFieldMisuseError,
  sealNarrativeField,
} from '@app-core/security/narrative-cipher';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type { NarrativeText } from '@app-core/security/narrative-text';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';
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

// `GET /api/me` answers a budget beside the address. This screen renders only
// the address and no case below reads the identifier — what it is here for is
// that `MeDto` is the shape the route sends, and a fixture one member short
// describes a body the server does not write.
const BUDGET_ID = '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471';

function meDto(email: string): MeDto {
  return { budgetId: BUDGET_ID, email };
}

// Canonical lower-case hyphenated UUIDs, version 7 in shape, as the server's
// `Guid` serializes them. The binding refuses any other spelling, so a fixture
// id written any other way would make every case below an `unrecognised`.
const EXPORT_IDS = {
  user: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e01',
  budget: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e02',
  account: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e03',
  payee: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e08',
  transaction: '01927f3a-6b1c-7d2e-8f30-4a5b6c7d8e0a',
} as const;

const EXPORTED_USER = {
  id: EXPORT_IDS.user,
  email: 'owner@budgetoid.test',
  createdAtUtc: '2026-01-04T09:15:22.123456Z',
} as const;

// The smallest document the decoder accepts: the account and no budget, so
// there is nothing to open and the export can finish under any key. What the
// cases about the gate, the double press and the filename are handed.
const EMPTY_EXPORT_TEXT = JSON.stringify({
  schemaVersion: 1,
  user: EXPORTED_USER,
  budgets: [],
});

// The server's text with every narrative member a real envelope, sealed under
// `key` for its own table, column and row — so an open through the real custody
// service runs the real cipher and the real binding.
//
// `accountNameWire` replaces the account name's envelope with a value of the
// case's choosing, for the one case about a wire string the decoder refuses.
interface SealedExport {
  readonly text: string;
  readonly plaintexts: readonly string[];
  readonly wires: readonly string[];
}

async function sealedExport(
  key: CryptoKey,
  accountNameWire?: string,
): Promise<SealedExport> {
  const plaintexts: string[] = [];
  const wires: string[] = [];
  const seal = async (
    binding: NarrativeFieldBinding,
    plaintext: string,
  ): Promise<string> => {
    const wire = await sealNarrativeField(key, plaintext, binding);
    plaintexts.push(plaintext);
    wires.push(wire);
    return wire;
  };

  const budgetName = await seal(
    { table: 'budgets', column: 'name', rowId: EXPORT_IDS.budget },
    'Household ledger',
  );
  const accountName = await seal(
    { table: 'accounts', column: 'name', rowId: EXPORT_IDS.account },
    'Everyday current account',
  );
  const payeeName = await seal(
    { table: 'payees', column: 'name', rowId: EXPORT_IDS.payee },
    'Corner bakery on Elm',
  );
  const description = await seal(
    {
      table: 'transactions',
      column: 'description',
      rowId: EXPORT_IDS.transaction,
    },
    'Birthday cake for Mira',
  );

  const text = JSON.stringify({
    schemaVersion: 1,
    user: EXPORTED_USER,
    budgets: [
      {
        id: EXPORT_IDS.budget,
        userId: EXPORT_IDS.user,
        name: budgetName,
        // Null, as on every budget today: nothing sets a base currency
        // (docs/business-logic/budgets.md).
        baseCurrencyCode: null,
        createdAtUtc: '2026-01-04T09:15:23.000000Z',
        accounts: [
          {
            id: EXPORT_IDS.account,
            budgetId: EXPORT_IDS.budget,
            name: accountNameWire ?? accountName,
            type: 'checking',
            openingBalance: 125.5,
            currencyCode: 'EUR',
            createdAtUtc: '2026-01-05T10:00:00.000000Z',
          },
        ],
        categoryGroups: [],
        categories: [],
        payees: [
          {
            id: EXPORT_IDS.payee,
            budgetId: EXPORT_IDS.budget,
            name: payeeName,
            createdAtUtc: '2026-01-06T11:00:00.000000Z',
          },
        ],
        transactions: [
          {
            id: EXPORT_IDS.transaction,
            budgetId: EXPORT_IDS.budget,
            accountId: EXPORT_IDS.account,
            amount: -12.25,
            date: '2026-07-14',
            description,
            payeeId: EXPORT_IDS.payee,
            categoryId: null,
            createdAtUtc: '2026-07-14T12:00:00.000000Z',
          },
        ],
      },
    ],
  });

  return { text, plaintexts, wires };
}

function generateContentKey(): Promise<CryptoKey> {
  return crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, [
    'encrypt',
    'decrypt',
  ]);
}

function generateIndexKey(): Promise<CryptoKey> {
  return crypto.subtle.generateKey({ name: 'HMAC', hash: 'SHA-256' }, false, [
    'sign',
  ]);
}

class MeApiStub {
  public getMe = vi.fn(
    (): Observable<MeDto> => of(meDto('owner@budgetoid.test')),
  );
  public getExport = vi.fn((): Observable<string> => of(EMPTY_EXPORT_TEXT));
  public getCredentials = vi.fn(
    (): Observable<readonly CredentialSummary[]> => of([FEDERATED, PASSKEY]),
  );
  // A count rather than a zero, so the default answer is distinguishable from
  // both of the states the service holds apart: a stub answering `0` would make
  // "published the server's count" and "seeded a zero" the same observation.
  public getRecoveryCodes = vi.fn((): Observable<number> => of(4));
  // The two reads a real custody makes when an unlock starts, and here only so
  // a case can hold custody at `unlocking`: neither ever answers.
  public getAccountKeys = vi.fn((): Observable<AccountKeyCustodyDto> => NEVER);
  public getSessionOwner = vi.fn((): Observable<MeDto> => NEVER);
}

class FileDownloadStub {
  public save = vi.fn((blob: Blob, filename: string): void => undefined);
}

// The driver, replaced by the two signals that are "a run is in flight" on
// every screen that reads one — `running` for a run this tab is walking,
// `staged` for a run on file. The real one is root-provided and reaches eight
// API services. Narrowed to the pair, so a service reaching for anything else
// of the driver's dies loudly here rather than reading a stubbed default.
class KeyRotationStub
  implements Pick<KeyRotationService, 'running' | 'staged'>
{
  readonly #running = signal(false);
  readonly #staged = signal<StagedRotation | null>(null);

  public readonly running: Signal<boolean> = this.#running.asReadonly();
  public readonly staged: Signal<StagedRotation | null> =
    this.#staged.asReadonly();

  public setRunning(running: boolean): void {
    this.#running.set(running);
  }

  public setStaged(staged: StagedRotation | null): void {
    this.#staged.set(staged);
  }
}

// Waits for an export to end however it ends. The opens run on the platform's
// cipher, which settles on no fixed number of ticks, so this polls the one flag
// the service publishes for "still working" rather than counting microtasks.
async function exportSettled(service: SettingsService): Promise<void> {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    if (!service.exporting()) {
      // One more turn, so a save issued in the same task as the release has
      // landed before anything is asserted about it.
      await new Promise((resolve) => setTimeout(resolve, 0));
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error('Timed out waiting for the export to finish.');
}

describe('SettingsService', () => {
  let service: SettingsService;
  let api: MeApiStub;
  let download: FileDownloadStub;
  let custody: AccountKeyCustodyService;
  let rotations: KeyRotationStub;
  let contentKey: CryptoKey;

  // Custody is the **real** service, holding real non-extractable keys. It is
  // where the export's opener comes from, and a real one is the only kind that
  // catches a detached `custody.openField` — its body reads `#` fields, so an
  // unbound reference throws where a stub's would answer. Every case starts
  // ready: unlocked, no run. The cases about readiness move one term each.
  beforeEach(async () => {
    api = new MeApiStub();
    download = new FileDownloadStub();
    rotations = new KeyRotationStub();
    TestBed.configureTestingModule({
      providers: [
        SettingsService,
        { provide: MeApiService, useValue: api },
        { provide: FileDownloadService, useValue: download },
        { provide: KeyRotationService, useValue: rotations },
      ],
    });
    custody = TestBed.inject(AccountKeyCustodyService);
    contentKey = await generateContentKey();
    custody.adopt(contentKey, await generateIndexKey());
    service = TestBed.inject(SettingsService);
  });

  it('saves a JSON file under a client-minted name', async () => {
    // Act
    service.export();
    await exportSettled(service);

    // Assert
    expect(download.save).toHaveBeenCalledTimes(1);
    // A Blob the tab built, typed as what it holds. The served text is no
    // longer the file: the file is the opened document written back out, so
    // the identity pin this case used to carry would now pin the defect.
    const [blob, filename] = download.save.mock.calls[0];
    expect(blob.type).toBe('application/json');
    // The exact format is pinned by export-filename.spec; a regex here keeps
    // this test off the clock while still proving the name is minted locally
    // rather than read from an unreadable cross-origin Content-Disposition.
    expect(filename).toMatch(/^budgetoid-export-\d{8}T\d{6}Z\.json$/);
    expect(service.exported()).toBe(true);
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

  it('clears a previous failure once an export succeeds', async () => {
    // Arrange
    api.getExport.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    service.export();
    expect(service.exportFailure()).toBe('failed');

    // Act
    service.export();
    await exportSettled(service);

    // Assert
    expect(service.exportFailure()).toBeNull();
    expect(service.exported()).toBe(true);
  });

  it('ignores a second export while one is in flight', () => {
    // Arrange
    const gate = new Subject<string>();
    api.getExport.mockReturnValue(gate);

    // Act
    service.export();
    service.export();

    // Assert
    // A double-click on the button must not cost two exports; the server
    // builds the whole document per request.
    expect(api.getExport).toHaveBeenCalledTimes(1);
  });

  it('exports again once the first has finished', async () => {
    // Arrange
    const gate = new Subject<string>();
    api.getExport.mockReturnValue(gate);
    service.export();
    gate.next(EMPTY_EXPORT_TEXT);
    gate.complete();
    await exportSettled(service);

    // Act
    service.export();

    // Assert
    // Control for the test above: a service that latched `exporting` and
    // never released it exports exactly once per page load and is green on
    // the in-flight test alone.
    expect(api.getExport).toHaveBeenCalledTimes(2);
  });

  // **Ready is one predicate, written positively**: custody says `unlocked`
  // and no key rotation is in flight. Every other state — `locked`,
  // `unlocking`, a run, a word added later — arrives not ready. Pressable is
  // ready and not already exporting, and it is what both the control's
  // `disabled` and the guard inside `export()` read. These cases are the
  // guard's half: a press reaches `export()` whatever the attribute says,
  // because `disabledInteractive` leaves the button in the tab order and
  // Material halts the click on anchors only.
  // See docs/design/components.md, "Export section".
  describe('readiness', () => {
    it('is pressable when the account is unlocked and no run is in flight', () => {
      // Assert
      // Control for the three refusals below: a service that published `false`
      // everywhere passes every one of them and exports nothing, ever.
      expect(service.pressable()).toBe(true);
      expect(service.exportBlock()).toBeNull();
    });

    it('is not pressable while an export is already running', () => {
      // Arrange
      api.getExport.mockReturnValue(new Subject<string>());

      // Act
      service.export();

      // Assert
      // Busy, and not a reason to advise anybody: the region's in-flight line
      // says what is happening, so no not-ready sentence is owed.
      expect(service.pressable()).toBe(false);
      expect(service.exportBlock()).toBeNull();
    });

    it('does not request the export while the account is locked', async () => {
      // Arrange
      custody.lock();

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // No request and no outcome. The handler's gate, not the attribute's: a
      // service that only trusted the template to hold the control off sends
      // the request, opens nothing, and tells the person the file failed.
      expect(api.getExport).not.toHaveBeenCalled();
      expect(download.save).not.toHaveBeenCalled();
      expect(service.pressable()).toBe(false);
      expect(service.exportFailure()).toBeNull();
      expect(service.exported()).toBe(false);
    });

    it.each([
      {
        name: 'a run this tab is walking',
        arrange: (stub: KeyRotationStub): void => stub.setRunning(true),
      },
      {
        name: 'a staged run on file',
        arrange: (stub: KeyRotationStub): void =>
          stub.setStaged({ startedAtUtc: '2026-07-01T08:00:00Z' }),
      },
    ])(
      'does not request the export during $name, with the account unlocked',
      async ({ arrange }) => {
        // Arrange
        // Unlocked, so custody alone would let this through. A staged run has
        // re-sealed part of the account under keys custody does not hold, and
        // an export over it would end `unreadable` for a reason that has a
        // remedy. Both terms, because either alone half-works and the half
        // that fails is the quiet one.
        expect(custody.status()).toBe('unlocked');
        arrange(rotations);

        // Act
        service.export();
        await exportSettled(service);

        // Assert
        expect(api.getExport).not.toHaveBeenCalled();
        expect(download.save).not.toHaveBeenCalled();
        expect(service.pressable()).toBe(false);
      },
    );

    it('does not request the export while unlocking', async () => {
      // Arrange
      // An unlock whose reads never answer, so custody stays mid-ceremony.
      // `unlocking` is not `locked`, and this is the case that tells
      // `status() === 'unlocked'` from `status() !== 'locked'`: written the
      // second way, the export goes live mid-ceremony and opens nothing.
      custody.unlock(await generateContentKey());
      expect(custody.status()).toBe('unlocking');

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      expect(api.getExport).not.toHaveBeenCalled();
      expect(service.pressable()).toBe(false);
    });
  });

  // Which sentence the screen puts above an Export that is off: the notice's
  // terms, not the form's. The run's sentence whenever a run is in flight,
  // whatever custody says; the locked one on `locked` alone; nothing while
  // unlocking — **disable when unsure, but do not advise when unsure**.
  describe('the reason Export is off', () => {
    it('names the run when a run is in flight and the account is locked', () => {
      // Arrange
      custody.lock();
      rotations.setRunning(true);

      // Assert
      // The run wins: Unlock pressed mid-run gets the generation on its way
      // out, so the locked sentence's advice is false here.
      expect(service.exportBlock()).toBe('rotating');
    });

    it('names the run for a staged run on an unlocked account', () => {
      // Arrange
      rotations.setStaged({ startedAtUtc: '2026-07-01T08:00:00Z' });

      // Assert
      expect(service.exportBlock()).toBe('rotating');
    });

    it('names the lock when the account is locked and no run is in flight', () => {
      // Arrange
      custody.lock();

      // Assert
      expect(service.exportBlock()).toBe('locked');
    });

    it('names nothing while unlocking, though Export is off', async () => {
      // Arrange
      custody.unlock(await generateContentKey());
      expect(custody.status()).toBe('unlocking');

      // Assert
      // Off and silent at once, the pair a single predicate cannot produce.
      // Written `!== 'unlocked'`, the locked sentence tells somebody to press
      // the button they are already holding down.
      expect(service.pressable()).toBe(false);
      expect(service.exportBlock()).toBeNull();
    });
  });

  // **Whole file or nothing.** The file is the opened document — every name and
  // note opened in this tab under the account's content key — and any member
  // that does not open means no file at all.
  describe('outcomes', () => {
    it('saves the opened document, not the served text', async () => {
      // Arrange
      const served = await sealedExport(contentKey);
      api.getExport.mockReturnValue(of(served.text));

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      expect(download.save).toHaveBeenCalledTimes(1);
      const saved = await download.save.mock.calls[0][0].text();
      // Every plaintext is in the file and no envelope is. Both directions: a
      // service saving the served text carries every wire and no plaintext,
      // and one that appended the opened values beside the sealed ones carries
      // both.
      for (const plaintext of served.plaintexts) {
        expect(saved, `the saved file is missing "${plaintext}".`).toContain(
          plaintext,
        );
      }
      for (const wire of served.wires) {
        expect(
          saved,
          'the saved file carries a sealed envelope.',
        ).not.toContain(wire);
      }
      expect(service.exported()).toBe(true);
      expect(service.exportFailure()).toBeNull();
    });

    it('saves nothing and reports locked when custody lets go mid-export', async () => {
      // Arrange
      // The request is answered and custody lets go before any field can have
      // finished opening — the platform's cipher never settles in the same
      // task. Every open therefore answers `locked`, whether it started before
      // the lock or after it.
      const served = await sealedExport(contentKey);
      const gate = new Subject<string>();
      api.getExport.mockReturnValue(gate);
      service.export();

      // Act
      gate.next(served.text);
      gate.complete();
      custody.lock();
      await exportSettled(service);

      // Assert
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('locked');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports unreadable when a field does not open', async () => {
      // Arrange
      // Sealed under a key this tab does not hold, so every member is a value
      // that did not authenticate — the keys were here and the bytes were not
      // theirs.
      const served = await sealedExport(await generateContentKey());
      api.getExport.mockReturnValue(of(served.text));

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // Nothing on disk: a file with a dash where a name was looks complete in
      // a downloads folder years later.
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('unreadable');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports unrecognised for a body the decoder refuses', async () => {
      // Arrange
      // A member `ExportDocument.cs` does not declare — the loud direction of
      // the strict decoder, a column nobody decided whether to open.
      api.getExport.mockReturnValue(
        of(
          JSON.stringify({
            schemaVersion: 1,
            user: EXPORTED_USER,
            budgets: [],
            surplus: 'a member this client does not read',
          }),
        ),
      );

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // `unrecognised`, not `failed`: the server answered, and what it
      // answered is a body this bundle cannot read, which a reload can change
      // and a retry cannot.
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('unrecognised');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports unrecognised for a wire string the strict decoder refuses', async () => {
      // Arrange
      // Every other member a real envelope under the key custody holds, so the
      // one malformed value is the only thing wrong with the body. Refused
      // before any cipher runs, it is a body this client could not read — not
      // a value that failed to open under keys it held.
      // See docs/design/components.md, "Export section".
      const served = await sealedExport(contentKey, 'not*base64url!');
      api.getExport.mockReturnValue(of(served.text));

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('unrecognised');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports failed when the opener rejects', async () => {
      // Arrange
      // A rejection is a defect in the call, not a value that did not open, so
      // it is none of the three body words. The spy is on the real custody,
      // which this case alone needs to misbehave; the runner restores no mocks.
      const served = await sealedExport(contentKey);
      api.getExport.mockReturnValue(of(served.text));
      const openField = vi
        .spyOn(custody, 'openField')
        .mockRejectedValue(
          new NarrativeFieldMisuseError('A binding this call made up.'),
        );
      onTestFinished(() => openField.mockRestore());

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // The opener was reached, so the rejection is what ended the export.
      expect(openField).toHaveBeenCalled();
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('failed');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports locked when custody locks after the last open', async () => {
      // Arrange
      // Every open answers text, and the first one locks custody on its way
      // out — so the document opens whole and custody is no longer holding
      // the keys at the moment the file would be handed over.
      const served = await sealedExport(contentKey);
      api.getExport.mockReturnValue(of(served.text));
      const openField = vi
        .spyOn(custody, 'openField')
        .mockImplementation((): Promise<NarrativeText> => {
          custody.lock();
          return Promise.resolve({ state: 'text', value: 'opened' });
        });
      onTestFinished(() => openField.mockRestore());

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      expect(openField).toHaveBeenCalled();
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('locked');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports locked when custody is mid-unlock at the hand-over', async () => {
      // Arrange
      // Custody let go and started taking the keys back, with reads that
      // never answer, so it is `unlocking` when the file would be saved.
      // `unlocking` is not `locked`: this is the case that tells the check
      // `status() !== 'unlocked'` from `status() === 'locked'`, and written
      // the second way a file is saved with nobody holding the keys.
      const served = await sealedExport(contentKey);
      const kek = await generateContentKey();
      api.getExport.mockReturnValue(of(served.text));
      let letGo = false;
      const openField = vi
        .spyOn(custody, 'openField')
        .mockImplementation((): Promise<NarrativeText> => {
          if (!letGo) {
            letGo = true;
            custody.lock();
            custody.unlock(kek);
          }
          return Promise.resolve({ state: 'text', value: 'opened' });
        });
      onTestFinished(() => openField.mockRestore());

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      expect(custody.status()).toBe('unlocking');
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('locked');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports locked when another custody is held by the time a document with nothing to open lands', async () => {
      // Arrange
      // A just-registered account: one budget, no name, nothing in it — so no
      // open runs, and no open can answer `locked` on the way. Custody lets go
      // and takes up a different pair while the request is out, and reads
      // `unlocked` again by the time the text lands. Only a holding captured
      // at the press can tell the export it is no longer the same custody.
      const gate = new Subject<string>();
      api.getExport.mockReturnValue(gate);
      const freshContentKey = await generateContentKey();
      const freshIndexKey = await generateIndexKey();
      const justRegistered = JSON.stringify({
        schemaVersion: 1,
        user: EXPORTED_USER,
        budgets: [
          {
            id: EXPORT_IDS.budget,
            userId: EXPORT_IDS.user,
            name: null,
            baseCurrencyCode: null,
            createdAtUtc: '2026-01-04T09:15:23.000000Z',
            accounts: [],
            categoryGroups: [],
            categories: [],
            payees: [],
            transactions: [],
          },
        ],
      });
      service.export();
      custody.lock();
      custody.adopt(freshContentKey, freshIndexKey);

      // Act
      gate.next(justRegistered);
      gate.complete();
      await exportSettled(service);

      // Assert
      expect(custody.status()).toBe('unlocked');
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('locked');
      expect(service.exported()).toBe(false);
    });

    it('saves nothing and reports locked when custody changes hands after the last open', async () => {
      // Arrange
      // Every open answers text, and the first one lets go and takes up other
      // keys on its way out — so the document opens whole, and custody reads
      // `unlocked` at the hand-over under keys the export never started with.
      const served = await sealedExport(contentKey);
      api.getExport.mockReturnValue(of(served.text));
      const otherContentKey = await generateContentKey();
      const otherIndexKey = await generateIndexKey();
      let changedHands = false;
      const openField = vi
        .spyOn(custody, 'openField')
        .mockImplementation((): Promise<NarrativeText> => {
          if (!changedHands) {
            changedHands = true;
            custody.lock();
            custody.adopt(otherContentKey, otherIndexKey);
          }
          return Promise.resolve({ state: 'text', value: 'opened' });
        });
      onTestFinished(() => openField.mockRestore());

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // `unlocked` at the end is what makes this case: a check on the status
      // alone sees nothing wrong here and saves the file.
      expect(openField).toHaveBeenCalled();
      expect(custody.status()).toBe('unlocked');
      expect(download.save).not.toHaveBeenCalled();
      expect(service.exportFailure()).toBe('locked');
      expect(service.exported()).toBe(false);
    });

    it('reports only the failure when an export fails after one succeeded', async () => {
      // Arrange
      service.export();
      await exportSettled(service);
      expect(service.exported()).toBe(true);
      api.getExport.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 500 })),
      );

      // Act
      service.export();
      await exportSettled(service);

      // Assert
      // The success belongs to the previous press. Left standing, the region
      // says `Exported.` over a request that has just failed.
      expect(service.exportFailure()).toBe('failed');
      expect(service.exported()).toBe(false);
    });

    it.each([403, 500])(
      'saves nothing and reports failed for a %i',
      async (status) => {
        // Arrange
        api.getExport.mockReturnValue(
          throwError(() => new HttpErrorResponse({ status })),
        );

        // Act
        service.export();
        await exportSettled(service);

        // Assert
        expect(download.save).not.toHaveBeenCalled();
        expect(service.exportFailure()).toBe('failed');
        expect(service.exported()).toBe(false);
      },
    );

    it('clears the previous outcome when the next export starts', async () => {
      // Arrange
      const served = await sealedExport(await generateContentKey());
      api.getExport.mockReturnValueOnce(of(served.text));
      service.export();
      await exportSettled(service);
      expect(service.exportFailure()).toBe('unreadable');
      api.getExport.mockReturnValue(new Subject<string>());

      // Act
      service.export();

      // Assert
      // A press is the one thing that clears an outcome, and it clears it as
      // the export starts — so the region never holds a stale word beside the
      // in-flight line.
      expect(service.exporting()).toBe(true);
      expect(service.exportFailure()).toBeNull();
      expect(service.exported()).toBe(false);
    });
  });

  it('publishes the account email', () => {
    // Arrange
    api.getMe.mockReturnValue(of(meDto('owner@budgetoid.test')));

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
    api.getMe.mockReturnValueOnce(of(meDto('first@budgetoid.test')));
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
    gate.next(meDto('second@budgetoid.test'));
    gate.complete();
    expect(service.email()).toBe('second@budgetoid.test');
  });

  it('leaves no email behind when a reload fails', () => {
    // Arrange
    api.getMe.mockReturnValueOnce(of(meDto('owner@budgetoid.test')));
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
