// The flow behind the **Rotate keys** control on `/app/settings`, and the one
// place "a run is in flight" is decided.
//
// It drives a real `HttpClient` over the testing backend rather than stubbing
// the re-authentication options leg, because *which nonce pool the ceremony is
// signed against* is the whole of what separates this press from an unlock: an
// unlock mints its challenge in the browser and throws the assertion away, and
// a begin posts an assertion the server verifies. A stub over that call would
// stay green on a flow that had quietly gone back to the local ceremony.
//
// Two seams are replaced. `WebauthnCeremonyService` reaches
// `navigator.credentials`, which this runner does not implement.
// `KeyRotationService` is the driver, and it is stubbed so that `running` can be
// put in states the flow itself cannot reach — which is the only way to pin that
// the attribute, the busy reading and the guard read one predicate rather than
// three spellings of it.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  KeyRotationService,
  type KeyRotationFailure,
  type KeyRotationNameCollision,
  type KeyRotationRenameRefusal,
  type KeyRotationPhase,
  type KeyRotationProgress,
  type StagedRotation,
} from '@app-core/security/key-rotation.service';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type { PasskeyRequestOptionsJson } from '@app-core/security/webauthn-encoding';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RotationFlowService } from './rotation-flow.service';

const API_ORIGIN = 'https://api.budgetoid.test';

// What the re-authentication leg answers with. Only the members the ceremony
// stub below never reads, so nothing here pretends to be a real challenge.
const OPTIONS: PasskeyRequestOptionsJson = {
  challenge: 'Y2hhbGxlbmdl',
  rpId: 'budgetoid.app',
  timeout: 60000,
  userVerification: 'required',
};

// A ceremony that succeeded. The key is a plain object: nothing in this flow
// looks inside it, which is the point — it travels as an argument into the
// driver and is never assigned to a field.
const CEREMONY: PasskeyAssertionCeremony = {
  payload: {
    credentialId: 'Y3JlZGVudGlhbA',
    clientDataJson: 'Y2xpZW50',
    authenticatorData: 'YXV0aA',
    signature: 'c2ln',
    userHandle: null,
  },
  // Nothing in this flow looks inside it, which is the property being pinned:
  // it travels as an argument into the driver and is never assigned to a field
  // of this class.
  keyEncryptionKey: {} as CryptoKey,
};

// **`implements` a surface derived from the real class, and that clause is not
// decoration.** `keyof` over a class type yields its public members only, so
// `Pick<S, keyof S>` is the compiler's own census of what this flow can reach.
// The driver's surface grew twice while this section was being built; each time
// the omission was a compile error naming the missing member rather than a
// `TypeError` during a run.
type KeyRotationSurface = Pick<KeyRotationService, keyof KeyRotationService>;

class KeyRotationStub implements KeyRotationSurface {
  public readonly phase = signal<KeyRotationPhase>('idle');
  public readonly progress = signal<KeyRotationProgress>({
    resealed: 0,
    records: 0,
  });
  public readonly failure = signal<KeyRotationFailure | null>(null);
  public readonly staged = signal<StagedRotation | null>(null);
  public readonly collision = signal<KeyRotationNameCollision | null>(null);
  public readonly renameRefusal = signal<KeyRotationRenameRefusal | null>(null);
  // **An independent signal, deliberately not composed out of `phase`.** The
  // flow's `working` is the one predicate the control's `disabled`, its
  // `aria-busy` and the guard all read; a stub deriving it from the phase would
  // agree with every reading of it and pin nothing. Driven by hand, it can be
  // put in a state the driver itself cannot reach, and there a second spelling
  // parts company with the first.
  public readonly running = signal(false);
  public begin = vi.fn(async () => Promise.resolve());
  public resume = vi.fn(async () => Promise.resolve());
  public readStagedRotation = vi.fn(async () => Promise.resolve());
}

// The name a person typed into the rename block, and the pair it answers.
const TYPED_NAME = 'Corner shop';

const COLLISION: KeyRotationNameCollision = {
  arm: 'payees',
  renamed: { id: 'b8d1c0de-0000-4000-8000-000000000002', name: 'BAKERY' },
  kept: { id: 'b8d1c0de-0000-4000-8000-000000000001', name: 'Bakery' },
};

class CeremonyStub {
  public supported = true;
  public answer: PasskeyCeremonyResult<PasskeyAssertionCeremony> = {
    ok: true,
    value: CEREMONY,
  };

  public available = vi.fn(() => this.supported);
  public assertPasskey = vi.fn(
    async (): Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>> =>
      Promise.resolve(this.answer),
  );
}

describe('RotationFlowService', () => {
  let rotations: KeyRotationStub;
  let ceremony: CeremonyStub;
  let flow: RotationFlowService;
  let http: HttpTestingController;

  // Answers the one request a press makes before it reaches the authenticator,
  // then lets both the request's own promise and the ceremony's settle.
  const answerTheOptionsLeg = async (): Promise<void> => {
    const request = http.expectOne(
      `${API_ORIGIN}/api/passkeys/reauthentication/options`,
    );

    request.flush(OPTIONS);

    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
  };

  beforeEach(() => {
    rotations = new KeyRotationStub();
    ceremony = new CeremonyStub();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: KeyRotationService, useValue: rotations },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        RotationFlowService,
      ],
    });
    flow = TestBed.inject(RotationFlowService);
    http = TestBed.inject(HttpTestingController);
  });

  it('asks the server for a challenge and begins a rotation with what the authenticator signed', async () => {
    // Arrange
    // Nothing staged, so this press is a begin.

    // Act
    flow.rotate();
    await answerTheOptionsLeg();

    // Assert
    // The assertion the driver posts has to be one the *server* minted the
    // challenge for. A locally minted ceremony would run perfectly here and be
    // refused by the begin route, which is a failure no spec over this class
    // would otherwise see.
    expect(ceremony.assertPasskey).toHaveBeenCalledWith(OPTIONS);
    expect(rotations.begin).toHaveBeenCalledWith(CEREMONY);
    expect(rotations.resume).not.toHaveBeenCalled();
    http.verify();
  });

  it('finishes the staged run rather than beginning a second one', async () => {
    // Arrange
    rotations.staged.set({ startedAtUtc: '2026-07-14T09:30:00Z' });

    // Act
    flow.rotate();
    await answerTheOptionsLeg();

    // Assert
    // A begin over a staged run overwrites the staged seals in place, and every
    // row the interrupted run already rewrote would then open under nothing.
    expect(rotations.resume).toHaveBeenCalledWith(CEREMONY);
    expect(rotations.begin).not.toHaveBeenCalled();
  });

  it('refuses a second press while a run is in flight', async () => {
    // Arrange
    // The state the driver is in for the whole length of a run, and the one it
    // publishes no re-entrancy guard of its own for: two concurrent presses
    // would fight over the driver's two key fields.
    rotations.running.set(true);

    // Act
    flow.rotate();

    // Assert
    // Not one request, not one system sheet, not one call.
    http.expectNone(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    expect(rotations.begin).not.toHaveBeenCalled();
    await Promise.resolve();
  });

  it('refuses a second press while its own ceremony is still up', () => {
    // Arrange
    flow.rotate();

    // Act
    // The driver has not been reached yet, so `running` is still false: this is
    // the half of `working` that the driver cannot publish.
    flow.rotate();

    // Assert
    // One options request, not two. A second would spend a second nonce and
    // raise a second system sheet over the first.
    http.expectOne(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
  });

  it('reports a run as in flight from its own half and from the driver’s', () => {
    // Arrange
    // Read as one predicate by the control's `disabled`, its `aria-busy` and
    // the guard above. Two spellings drift, and the drift is silent in both
    // directions.

    // Act
    flow.rotate();

    // Assert
    expect(flow.busy()).toBe(true);
    expect(flow.working()).toBe(true);

    // Act
    rotations.running.set(true);
    http
      .expectOne(`${API_ORIGIN}/api/passkeys/reauthentication/options`)
      .flush(OPTIONS);

    // Assert
    // Still in flight once the driver has taken over, whatever this flow's own
    // half says.
    expect(flow.working()).toBe(true);
  });

  it('spends no challenge on a browser that cannot check a passkey', () => {
    // Arrange
    ceremony.supported = false;

    // Act
    flow.rotate();

    // Assert
    // A nonce the server persisted, spent on its way to being told for free
    // exactly what this browser was going to be told anyway.
    http.expectNone(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
    expect(flow.failure()).toBe('unsupported');
    expect(flow.working()).toBe(false);
  });

  it.each([
    ['cancelled', 'cancelled'],
    ['no-prf', 'no-prf'],
    ['failed', 'ceremony-failed'],
    ['duplicate', 'ceremony-failed'],
  ] satisfies readonly (readonly [PasskeyCeremonyFailure, string])[])(
    'publishes %s as %s and posts nothing',
    async (refused, word) => {
      // Arrange
      ceremony.answer = { ok: false, failure: refused };

      // Act
      flow.rotate();
      await answerTheOptionsLeg();

      // Assert
      expect(flow.failure()).toBe(word);
      expect(rotations.begin).not.toHaveBeenCalled();
      expect(flow.working()).toBe(false);
    },
  );

  it('publishes unknown when the challenge never arrives', async () => {
    // Arrange
    flow.rotate();

    // Act
    http
      .expectOne(`${API_ORIGIN}/api/passkeys/reauthentication/options`)
      .error(new ProgressEvent('error'), { status: 0, statusText: '' });
    await Promise.resolve();
    await Promise.resolve();

    // Assert
    // There is no sixth word for an options leg: the five this flow publishes
    // are the ceremony's, and a leg that answered nothing is a rejection out of
    // a method whose contract is to answer with a result.
    expect(flow.failure()).toBe('unknown');
    expect(flow.working()).toBe(false);
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
  });

  it('clears the previous word when a press starts', async () => {
    // Arrange
    ceremony.answer = { ok: false, failure: 'cancelled' };
    flow.rotate();
    await answerTheOptionsLeg();
    expect(flow.failure()).toBe('cancelled');
    ceremony.answer = { ok: true, value: CEREMONY };

    // Act
    flow.rotate();

    // Assert
    // Cleared when the act *starts*. Cleared anywhere else and the sentence
    // from a cancelled ceremony stands over the press that retried it.
    expect(flow.failure()).toBeNull();
    http
      .expectOne(`${API_ORIGIN}/api/passkeys/reauthentication/options`)
      .flush(OPTIONS);
  });

  it('hands nothing to the driver when the ceremony for a finish fails', async () => {
    // Arrange
    // A run that stopped on the pair: still staged, its word and its pair
    // standing on the driver.
    rotations.staged.set({ startedAtUtc: '2026-07-14T09:30:00Z' });
    rotations.failure.set('same-name');
    rotations.collision.set(COLLISION);
    ceremony.answer = { ok: false, failure: 'cancelled' };

    // Act
    flow.rotate();
    await answerTheOptionsLeg();

    // Assert
    // A ceremony that fails reaches no press, so the driver's pair — and the
    // block drawn from it — is left as it stood.
    expect(flow.failure()).toBe('cancelled');
    expect(flow.working()).toBe(false);
    expect(rotations.resume).not.toHaveBeenCalled();
    expect(rotations.begin).not.toHaveBeenCalled();
  });

  describe('Rename and finish', () => {
    beforeEach(() => {
      rotations.failure.set('same-name');
      rotations.collision.set(COLLISION);
    });

    it.each<{ readonly label: string; readonly staged: StagedRotation | null }>(
      [
        {
          label:
            'nothing staged, because the begin that stopped never staged one',
          staged: null,
        },
        {
          label: 'a staged run',
          staged: { startedAtUtc: '2026-07-14T09:30:00Z' },
        },
      ],
    )(
      'runs the ceremony first and then finishes the run carrying the name, over $label',
      async ({ staged }) => {
        // Arrange
        rotations.staged.set(staged);

        // Act
        flow.renameAndFinish(TYPED_NAME);

        // Assert
        // Nothing reaches the driver before the ceremony has answered: the
        // ceremony sentences each end *Nothing has changed.*
        expect(rotations.resume).not.toHaveBeenCalled();

        await answerTheOptionsLeg();

        expect(ceremony.assertPasskey).toHaveBeenCalledWith(OPTIONS);
        // A resume and never a begin, whatever `staged` says: a begin-press
        // that ended same-name never set it, and a begin over the stopped run
        // would re-stage rather than rename.
        expect(rotations.resume).toHaveBeenCalledTimes(1);
        expect(rotations.resume).toHaveBeenCalledWith(CEREMONY, {
          name: TYPED_NAME,
        });
        expect(rotations.begin).not.toHaveBeenCalled();
        expect(ceremony.assertPasskey.mock.invocationCallOrder[0]).toBeLessThan(
          rotations.resume.mock.invocationCallOrder[0],
        );
      },
    );

    it('refuses a press while a run is in flight', () => {
      // Arrange
      rotations.running.set(true);

      // Act
      flow.renameAndFinish(TYPED_NAME);

      // Assert
      http.expectNone(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(rotations.resume).not.toHaveBeenCalled();
    });

    it('refuses a second press while its own ceremony is still up', () => {
      // Arrange
      flow.renameAndFinish(TYPED_NAME);

      // Act
      flow.renameAndFinish(TYPED_NAME);

      // Assert
      // One options request, not two: a second would spend a second nonce and
      // raise a second system sheet over the first.
      http.expectOne(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
    });

    it('hands the driver the name exactly as typed, surrounding spaces included', async () => {
      // Arrange
      // What the driver seals is what the person wrote, character for
      // character — the ordinary name fields' rule. The field trims only to
      // judge a blank.
      const typed = '  Bakery 2 ';

      // Act
      flow.renameAndFinish(typed);
      await answerTheOptionsLeg();

      // Assert
      expect(rotations.resume).toHaveBeenCalledWith(CEREMONY, { name: typed });
    });

    it('spends no challenge on a browser that cannot check a passkey', () => {
      // Arrange
      ceremony.supported = false;

      // Act
      flow.renameAndFinish(TYPED_NAME);

      // Assert
      // The same check, in the same position, as a Rotate keys press: a
      // re-authentication nonce is persisted server-side, so one spent on a
      // browser that was never going to finish the ceremony is spent for
      // nothing.
      http.expectNone(`${API_ORIGIN}/api/passkeys/reauthentication/options`);
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(rotations.resume).not.toHaveBeenCalled();
      expect(flow.failure()).toBe('unsupported');
      expect(flow.working()).toBe(false);
    });

    it('hands nothing to the driver when the ceremony fails', async () => {
      // Arrange
      ceremony.answer = { ok: false, failure: 'cancelled' };

      // Act
      flow.renameAndFinish(TYPED_NAME);
      await answerTheOptionsLeg();

      // Assert
      expect(flow.failure()).toBe('cancelled');
      expect(flow.working()).toBe(false);
      expect(rotations.resume).not.toHaveBeenCalled();
      expect(rotations.begin).not.toHaveBeenCalled();
    });
  });
});
