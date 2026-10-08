// The flow behind **Cancel the erasure** on `/app/settings`: the browser's
// ability, the challenge, the passkey, and the one cancelling request. See
// docs/business-logic/erasure.md and docs/design/components.md.
//
// It is `ErasureFlowService`'s shape and it drives a real `HttpClient` over the
// testing backend for that file's reason: which nonce pool the challenge comes
// from, whether a request is marked and what the cancelling request carries
// live on the wire and nowhere else. Two differences from the erasure carry
// most of the cases below. A declined assertion answers `refusal:
// "assertion"`, so it is `refused` without a probe; and the cancellation is
// idempotent, so `undetermined` keeps the control live instead of withdrawing
// it.
//
// Two seams are replaced. `WebauthnCeremonyService` reaches
// `navigator.credentials`, which this runner does not implement.
// `SessionService` is reduced to what the flow reads and the census needs:
// `sessionToken`, read just before the cancelling request goes out;
// `scheduledErasure`, which reopens a cancelled press once a schedule stands
// again; `erasureCancelled`, which the 204 must call with that token; and
// `ended`, which nothing here may call — ending a session is
// `sessionExpiryInterceptor`'s. The schedule is a signal the case sets by
// hand: this stub's `erasureCancelled` writes nothing, so a case that wants
// the real member's `null` says so.
//
// **Vitest spies persist across cases** (`restoreMocks` is unset), so every spy
// is built fresh inside `beforeEach`.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  signal,
  type WritableSignal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MeDto } from '@app-core/api/me-api.service';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/expects-unauthenticated.token';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyFailure,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { ConfigurationService } from '@app-core/services/configuration.service';
import {
  SessionService,
  type ScheduledErasure,
  type SessionToken,
} from '@app-core/session/session.service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { ErasureCancellationFlowService } from './erasure-cancellation-flow.service';

const API_ORIGIN = 'https://api.budgetoid.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/reauthentication/options`;
// The sign-in pool, named only so its absence can be asserted: the gate
// refuses an assertion signed over one of its challenges.
const SIGN_IN_OPTIONS_URL = `${API_ORIGIN}/api/passkeys/assertion/options`;
const CANCELLATION_URL = `${API_ORIGIN}/api/me/erasure/schedule/cancellation`;
// The probe a 401 without the assertion word is read against.
const ME_URL = `${API_ORIGIN}/api/me`;

const ME: MeDto = {
  email: 'owner@example.test',
  budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
};

const OPTIONS: PasskeyRequestOptionsJson = {
  challenge: 'Y2hhbGxlbmdl',
  rpId: 'budgetoid.app',
  timeout: 60000,
  userVerification: 'required',
};

const PAYLOAD: PasskeyAssertionPayload = {
  credentialId: 'Y3JlZGVudGlhbA',
  clientDataJson: 'Y2xpZW50',
  authenticatorData: 'YXV0aA',
  signature: 'c2ln',
  userHandle: 'dXNlcg',
};

// One problem body per reading, never one fixture with a member swapped.
const ASSERTION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'The passkey could not be verified.',
  status: 401,
  refusal: 'assertion',
};
// The fallback policy's 401 for a session that had already ended.
const SESSION_REFUSAL = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
  title: 'Unauthorized',
  status: 401,
};

// One press of the ceremony, answered by hand — `erasure-flow.service.spec.ts`'s
// stub. `held` keeps the call pending until `settle()`.
class CeremonyStub {
  public supported = true;
  public answer: PasskeyCeremonyResult<PasskeyAssertionCeremony>;
  public held = false;
  public rejects = false;
  #release: (() => void) | null = null;

  public readonly available: Mock<() => boolean> = vi.fn(() => this.supported);
  public readonly assertPasskey: Mock<
    (
      options: PasskeyRequestOptionsJson,
      signal?: AbortSignal,
    ) => Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>>
  > = vi.fn(async () => {
    if (this.held) {
      await new Promise<void>((resolve) => (this.#release = resolve));
    }

    if (this.rejects) {
      throw new Error('outside the contract');
    }

    return this.answer;
  });

  constructor(ceremony: PasskeyAssertionCeremony) {
    this.answer = { ok: true, value: ceremony };
  }

  public settle(): void {
    this.#release?.();
    this.#release = null;
  }
}

// The tokens a case hands out, one per visit. Opaque on the real class; a
// number here because the stub is the one minting them.
const FIRST_VISIT = 1 as SessionToken;
const SECOND_VISIT = 2 as SessionToken;
const THIRD_VISIT = 3 as SessionToken;

const SCHEDULED: ScheduledErasure = {
  takesEffectAtUtc: '2026-10-09T10:30:00Z',
};

interface SessionCensus {
  readonly sessionToken: Mock<() => SessionToken>;
  readonly scheduledErasure: WritableSignal<ScheduledErasure>;
  readonly erasureCancelled: Mock<(sentUnder: SessionToken) => void>;
  readonly ended: Mock<() => void>;
}

describe('ErasureCancellationFlowService', () => {
  let http: HttpTestingController;
  let flow: ErasureCancellationFlowService;
  let ceremony: CeremonyStub;
  let session: SessionCensus;

  beforeEach(async () => {
    // A real non-extractable key, so the ceremony value is the shape the real
    // service returns — and the shape that must never reach the wire.
    const keyEncryptionKey = await crypto.subtle.generateKey(
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );

    ceremony = new CeremonyStub({ payload: PAYLOAD, keyEncryptionKey });
    session = {
      sessionToken: vi.fn(() => FIRST_VISIT),
      scheduledErasure: signal<ScheduledErasure>(SCHEDULED),
      erasureCancelled: vi.fn(),
      ended: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        { provide: SessionService, useValue: session },
        ErasureCancellationFlowService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    flow = TestBed.inject(ErasureCancellationFlowService);
  });

  // Waits for a request to go out and hands it back; throws on two, because
  // "one request, not two" is a claim several cases make.
  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(() => {
      const found = http.match(url);

      if (found.length > 1) {
        throw new Error(`${found.length} requests to ${url}, not one.`);
      }

      return found[0] ?? null;
    }, `a request to ${url}`);
  }

  async function reachTheCancellingRequest(): Promise<TestRequest> {
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);

    return requestTo(CANCELLATION_URL);
  }

  async function theCeremonyStarts(): Promise<void> {
    await eventually(
      () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
      'the ceremony to start',
    );
  }

  // Lets every pending promise in the flow run.
  async function settle(): Promise<void> {
    for (let turn = 0; turn < 5; turn += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  }

  // Asserts that nothing more went out after the press ended: no retry, no
  // second challenge, no probe the case did not ask for.
  function nothingMoreSent(): void {
    expect(http.match(OPTIONS_URL)).toEqual([]);
    expect(http.match(CANCELLATION_URL)).toEqual([]);
    expect(http.match(ME_URL)).toEqual([]);
  }

  describe('a 204', () => {
    it('publishes nothing scheduled and ends cancelled', async () => {
      // Arrange
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await eventually(
        () => (flow.phase() === 'cancelled' ? true : null),
        'the cancelled phase',
      );

      // Assert
      // `SessionService` is the one owner of the schedule this tab knows, so
      // the notice on every screen and the section here read one fact.
      expect(session.erasureCancelled).toHaveBeenCalledTimes(1);
      expect(flow.failure()).toBeNull();
      expect(flow.working()).toBe(false);
      expect(session.ended).not.toHaveBeenCalled();
      nothingMoreSent();
    });

    // **The token read when the request went out**, never one read at the
    // press or when the answer lands. The session can end and another begin
    // during the ceremony or while the request is out; the `204` describes the
    // visit it was sent in, and `SessionService` drops it unless that visit is
    // still the one standing.
    it('hands erasureCancelled the token read when the cancelling request went out', async () => {
      // Arrange
      let current = FIRST_VISIT;
      session.sessionToken.mockImplementation(() => current);
      ceremony.held = true;
      flow.cancel();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();
      current = SECOND_VISIT;
      ceremony.settle();
      const cancelling = await requestTo(CANCELLATION_URL);
      current = THIRD_VISIT;

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await eventually(
        () => (session.erasureCancelled.mock.calls.length > 0 ? true : null),
        'the 204 to be published',
      );

      // Assert
      expect(session.erasureCancelled).toHaveBeenCalledTimes(1);
      expect(session.erasureCancelled).toHaveBeenCalledWith(SECOND_VISIT);
    });

    // **A cancelled press reopens once a schedule stands again.** A Google
    // sign-in can file a new erasure after this one was withdrawn, and the
    // screen learns of it from a read; a press then runs the whole act again,
    // because the first nonce was spent and the schedule is a new one.
    it('accepts a whole new press from cancelled once a schedule stands again', async () => {
      // Arrange
      flow.cancel();
      (await reachTheCancellingRequest()).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (flow.phase() === 'cancelled' ? true : null),
        'the cancelled phase',
      );
      // What the real `erasureCancelled` publishes, then a read finding a
      // schedule filed since.
      session.scheduledErasure.set(null);
      session.scheduledErasure.set(SCHEDULED);
      expect(flow.pressable()).toBe(true);

      // Act
      flow.cancel();

      // Assert
      expect(flow.phase()).toBe('asserting');
      expect(flow.failure()).toBeNull();
      const cancelling = await reachTheCancellingRequest();
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(2);

      // Act
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await eventually(
        () => (session.erasureCancelled.mock.calls.length > 1 ? true : null),
        'the second 204 to be published',
      );

      // Assert
      expect(flow.phase()).toBe('cancelled');
      nothingMoreSent();
    });

    // The control for the case above: a press with nothing scheduled would
    // spend a nonce on a schedule that is gone.
    it('accepts no press from cancelled while nothing is scheduled', async () => {
      // Arrange
      flow.cancel();
      (await reachTheCancellingRequest()).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (flow.phase() === 'cancelled' ? true : null),
        'the cancelled phase',
      );
      session.scheduledErasure.set(null);

      // Act
      flow.cancel();
      await settle();

      // Assert
      expect(flow.pressable()).toBe(false);
      expect(flow.phase()).toBe('cancelled');
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
      nothingMoreSent();
    });

    // **The refusal lands on `idle`**, as it does from rest: the section draws
    // a word only at rest, so a refusal left on `cancelled` would say nothing.
    it('ends a press from cancelled idle and unsupported on a browser that cannot check a passkey', async () => {
      // Arrange
      flow.cancel();
      (await reachTheCancellingRequest()).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (flow.phase() === 'cancelled' ? true : null),
        'the cancelled phase',
      );
      session.scheduledErasure.set(null);
      session.scheduledErasure.set(SCHEDULED);
      ceremony.supported = false;

      // Act
      flow.cancel();

      // Assert
      expect(flow.phase()).toBe('idle');
      expect(flow.failure()).toBe('unsupported');
      expect(flow.working()).toBe(false);
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
      await settle();
      nothingMoreSent();
    });
  });

  describe('what goes on the wire', () => {
    it('signs the ceremony over a re-authentication challenge', async () => {
      // Act
      flow.cancel();
      const options = await requestTo(OPTIONS_URL);
      options.flush(OPTIONS);
      const cancelling = await requestTo(CANCELLATION_URL);

      // Assert
      expect(options.request.method).toBe('POST');
      expect(http.match(SIGN_IN_OPTIONS_URL)).toEqual([]);
      expect(ceremony.assertPasskey).toHaveBeenCalledWith(
        OPTIONS,
        expect.any(AbortSignal),
      );

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('marks the cancelling request and leaves the challenge unmarked', async () => {
      // Act
      flow.cancel();
      const options = await requestTo(OPTIONS_URL);
      options.flush(OPTIONS);
      const cancelling = await requestTo(CANCELLATION_URL);

      // Assert
      // A 401 on the challenge is a session that has ended — the
      // interceptor's. A 401 on the cancelling request is usually the gate's
      // verdict — this flow's sentence.
      expect(options.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
      expect(cancelling.request.method).toBe('POST');
      expect(cancelling.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(
        true,
      );

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends the five assertion members and nothing of the ceremony beside them', async () => {
      // Act
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Assert
      // The ceremony answers with the payload *and* the key-encryption key;
      // posting the ceremony value, or spreading it, puts the key (or a
      // `payload` wrapper) on the wire.
      const sent = cancelling.request.body as Record<string, unknown>;

      expect(Object.keys(sent).sort()).toEqual(
        [
          'authenticatorData',
          'clientDataJson',
          'credentialId',
          'signature',
          'userHandle',
        ].sort(),
      );
      expect(sent).toEqual(PAYLOAD);

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('posts nothing until the passkey has answered', async () => {
      // Arrange
      ceremony.held = true;
      flow.cancel();

      // Act
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();
      await settle();

      // Assert
      // Every sentence above `refused` says *the erasure is still scheduled*,
      // which is a fact about this client only because nothing is posted
      // before the ceremony answers.
      expect(http.match(CANCELLATION_URL)).toEqual([]);
      expect(flow.phase()).toBe('asserting');

      // Act
      ceremony.settle();
      const cancelling = await requestTo(CANCELLATION_URL);

      // Assert
      expect(flow.phase()).toBe('cancelling');

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  describe('a refusal of the cancelling request', () => {
    // **No probe.** The assertion word is the gate's own verdict; a session
    // that had ended would have been turned away before the gate ran.
    it('says refused for a declined assertion, and asks nothing more', async () => {
      // Arrange
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Act
      cancelling.flush(ASSERTION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(flow.failure()).toBe('refused');
      expect(flow.phase()).toBe('idle');
      expect(flow.pressable()).toBe(true);
      expect(session.erasureCancelled).not.toHaveBeenCalled();
      expect(session.ended).not.toHaveBeenCalled();
      nothingMoreSent();
    });

    it.each([400, 403])(
      'says a %i is a request this client could not use',
      async (status) => {
        // Arrange
        flow.cancel();
        const cancelling = await reachTheCancellingRequest();

        // Act
        cancelling.flush(null, { status, statusText: 'Refused' });
        await settle();

        // Assert
        expect(flow.failure()).toBe('unrecognised');
        expect(flow.pressable()).toBe(true);
        expect(session.erasureCancelled).not.toHaveBeenCalled();
        nothingMoreSent();
      },
    );

    it.each([
      { label: 'no answer at all', status: 0 },
      { label: 'a server error', status: 500 },
      { label: 'a proxy that gave up', status: 504 },
    ])('cannot tell after $label, and stays pressable', async ({ status }) => {
      // Arrange
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Act
      if (status === 0) {
        cancelling.error(new ProgressEvent('error'), {
          status: 0,
          statusText: 'Unknown Error',
        });
      } else {
        cancelling.flush(null, { status, statusText: 'Failed' });
      }
      await settle();

      // Assert
      // **Unlike the erasure, the control stays live.** A second cancellation
      // after a lost 204 answers 204 again — the route is idempotent — so
      // pressing again is how somebody finds out. Nothing is retried for
      // them, and nothing is published: the 204 may or may not have landed.
      expect(flow.failure()).toBe('undetermined');
      expect(flow.pressable()).toBe(true);
      expect(session.erasureCancelled).not.toHaveBeenCalled();
      nothingMoreSent();
    });

    it('runs a whole new press after undetermined', async () => {
      // Arrange
      flow.cancel();
      (await reachTheCancellingRequest()).flush(null, {
        status: 502,
        statusText: 'Bad Gateway',
      });
      await settle();
      expect(flow.failure()).toBe('undetermined');

      // Act
      flow.cancel();

      // Assert
      // A fresh challenge and a fresh ceremony: a nonce is single use, and
      // the first was spent whether or not the 204 was lost.
      expect(flow.failure()).toBeNull();
      const cancelling = await reachTheCancellingRequest();
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(2);

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  // A 401 without the assertion word is a session that may have ended before
  // the gate ran. The cancelling request is marked, so the interceptor heard
  // nothing; one **unmarked** `GET /api/me` lets it hear the probe's own 401.
  describe('after a 401 that is not a declined assertion', () => {
    async function refuseWithoutTheWord(): Promise<TestRequest> {
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      cancelling.flush(SESSION_REFUSAL, {
        status: 401,
        statusText: 'Unauthorized',
      });

      return requestTo(ME_URL);
    }

    it('asks who this is, once and unmarked, before saying anything', async () => {
      // Act
      const probe = await refuseWithoutTheWord();

      // Assert
      // Still cancelling while it is out: no sentence has been earned, and
      // the control must not come back for a second press over an answer
      // nobody has read.
      expect(probe.request.method).toBe('GET');
      expect(probe.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
      expect(flow.phase()).toBe('cancelling');
      expect(flow.failure()).toBeNull();
      expect(flow.pressable()).toBe(false);

      probe.flush(ME);
      await settle();
    });

    it('says nothing once the probe finds the session gone', async () => {
      // Arrange
      const probe = await refuseWithoutTheWord();

      // Act
      probe.flush(null, { status: 401, statusText: 'Unauthorized' });
      await settle();

      // Assert
      // The probe's 401 is unmarked, so `sessionExpiryInterceptor` ends the
      // session and leaves for Welcome; a flow that did either itself would be
      // a second owner of that fact.
      expect(flow.failure()).toBeNull();
      expect(flow.phase()).toBe('idle');
      expect(session.ended).not.toHaveBeenCalled();
      expect(session.erasureCancelled).not.toHaveBeenCalled();
      nothingMoreSent();
    });

    it('cannot tell once the probe finds the session still there', async () => {
      // Arrange
      const probe = await refuseWithoutTheWord();

      // Act
      probe.flush(ME);
      await settle();

      // Assert
      // Nothing judged the passkey — a declined one carries the word — so
      // *didn't accept that passkey* would be false. What the 401 was is
      // unknown, and so is whether the schedule stands.
      expect(flow.failure()).toBe('undetermined');
      expect(flow.pressable()).toBe(true);
      nothingMoreSent();
    });

    it.each([
      { label: 'no answer at all', status: 0 },
      { label: 'a server error', status: 500 },
    ])('cannot tell when the probe meets $label', async ({ status }) => {
      // Arrange
      const probe = await refuseWithoutTheWord();

      // Act
      if (status === 0) {
        probe.error(new ProgressEvent('error'), {
          status: 0,
          statusText: 'Unknown Error',
        });
      } else {
        probe.flush(null, { status, statusText: 'Failed' });
      }
      await settle();

      // Assert
      expect(flow.failure()).toBe('undetermined');
      nothingMoreSent();
    });
  });

  describe('before anything is posted', () => {
    it('spends no challenge on a browser that cannot check a passkey', () => {
      // Arrange
      ceremony.supported = false;

      // Act
      flow.cancel();

      // Assert
      // A re-authentication nonce is persisted server-side, and one spent by a
      // browser that was never going to finish is spent for nothing.
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(flow.failure()).toBe('unsupported');
      expect(flow.working()).toBe(false);
    });

    it.each([
      ['unsupported', 'unsupported'],
      ['cancelled', 'dismissed'],
      ['no-prf', 'no-prf'],
      ['failed', 'ceremony-failed'],
      ['duplicate', 'ceremony-failed'],
    ] satisfies readonly (readonly [PasskeyCeremonyFailure, string])[])(
      'says a ceremony that ended %s as %s and posts nothing',
      async (refused, word) => {
        // Arrange
        ceremony.answer = { ok: false, failure: refused };
        flow.cancel();

        // Act
        (await requestTo(OPTIONS_URL)).flush(OPTIONS);
        await settle();

        // Assert
        // `cancelled` is renamed `dismissed`, because on a control named
        // *Cancel the erasure* the ceremony's word reads as the act having
        // happened.
        expect(flow.failure()).toBe(word);
        expect(flow.phase()).toBe('idle');
        nothingMoreSent();
      },
    );

    it('reads a ceremony that throws as one that did not finish', async () => {
      // Arrange
      ceremony.rejects = true;
      flow.cancel();

      // Act
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      expect(flow.failure()).toBe('ceremony-failed');
      expect(flow.working()).toBe(false);
      nothingMoreSent();
    });

    it('leaves a 401 on the challenge to the session interceptor', async () => {
      // Arrange
      flow.cancel();

      // Act
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await settle();

      // Assert
      expect(flow.failure()).toBeNull();
      expect(flow.phase()).toBe('idle');
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(session.ended).not.toHaveBeenCalled();
      nothingMoreSent();
    });

    it.each([400, 403])(
      'reads a %i on the challenge as unrecognised',
      async (status) => {
        // Arrange
        flow.cancel();

        // Act
        (await requestTo(OPTIONS_URL)).flush(null, {
          status,
          statusText: 'Refused',
        });
        await settle();

        // Assert
        expect(flow.failure()).toBe('unrecognised');
        expect(ceremony.assertPasskey).not.toHaveBeenCalled();
        nothingMoreSent();
      },
    );

    it.each([
      { label: 'no answer at all', status: 0 },
      { label: 'a server error', status: 500 },
      { label: 'a proxy that gave up', status: 502 },
    ])(
      'says it could not start when the challenge meets $label',
      async ({ status }) => {
        // Arrange
        flow.cancel();
        const options = await requestTo(OPTIONS_URL);

        // Act
        if (status === 0) {
          options.error(new ProgressEvent('error'), {
            status: 0,
            statusText: 'Unknown Error',
          });
        } else {
          options.flush(null, { status, statusText: 'Failed' });
        }
        await settle();

        // Assert
        expect(flow.failure()).toBe('unstarted');
        expect(ceremony.assertPasskey).not.toHaveBeenCalled();
        nothingMoreSent();
      },
    );
  });

  describe('pressable, working and asking', () => {
    it('is pressable at rest, and neither working nor asking', () => {
      // Assert
      expect(flow.phase()).toBe('idle');
      expect(flow.pressable()).toBe(true);
      expect(flow.working()).toBe(false);
      expect(flow.asking()).toBe(false);
    });

    it('is asking while the challenge is fetched', async () => {
      // Act
      flow.cancel();
      const options = await requestTo(OPTIONS_URL);

      // Assert
      expect(flow.asking()).toBe(true);
      expect(flow.working()).toBe(true);
      expect(flow.pressable()).toBe(false);

      options.flush(null, { status: 500, statusText: 'Failed' });
      await settle();
    });

    it('is asking while the device is asked', async () => {
      // Arrange
      ceremony.held = true;
      flow.cancel();

      // Act
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Assert
      expect(flow.asking()).toBe(true);
      expect(flow.working()).toBe(true);

      ceremony.settle();
      (await requestTo(CANCELLATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
    });

    // **Asking is the passkey check and nothing wider.** Other controls on the
    // screen read it as a hold because the browser runs one passkey check at a
    // time; the cancelling request asks the device for nothing.
    it('is working but not asking while the cancelling request is out', async () => {
      // Act
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Assert
      expect(flow.phase()).toBe('cancelling');
      expect(flow.asking()).toBe(false);
      expect(flow.working()).toBe(true);
      expect(flow.pressable()).toBe(false);

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('makes one challenge and one cancelling request however often it is pressed', async () => {
      // Arrange
      flow.cancel();

      // Act
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();
      flow.cancel();
      await settle();

      // Assert
      // The handler refuses on the same predicate the attribute is drawn
      // from: Material's click-halt is applied to anchors only.
      expect(http.match(OPTIONS_URL)).toEqual([]);
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
      expect(http.match(CANCELLATION_URL)).toEqual([]);

      cancelling.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('clears the last word the moment a new press starts', async () => {
      // Arrange
      ceremony.answer = { ok: false, failure: 'cancelled' };
      flow.cancel();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await eventually(() => flow.failure(), 'the refusal');

      // Act
      flow.cancel();

      // Assert
      // Synchronously: cleared any later and *The passkey check didn't
      // finish* stands over the press that retried it.
      expect(flow.failure()).toBeNull();
      expect(flow.phase()).toBe('asserting');

      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 500,
        statusText: 'Failed',
      });
      await settle();
    });

    it('keeps the last word when a press is refused', async () => {
      // Arrange
      ceremony.held = true;
      ceremony.answer = { ok: false, failure: 'cancelled' };
      flow.cancel();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      // Refused: a press is running. Nothing has said anything yet, and the
      // refused press must not start a second run that would.
      flow.cancel();
      ceremony.settle();
      await eventually(() => flow.failure(), 'the refusal');

      // Assert
      expect(flow.failure()).toBe('dismissed');
      expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
    });
  });

  // The flow dies with the screen that provides it. Provided here in an
  // environment injector the case can destroy, which is what the screen's
  // teardown is to a component-provided service.
  describe('when the screen goes', () => {
    function provideOnAScreen(): EnvironmentInjector {
      const screen = createEnvironmentInjector(
        [ErasureCancellationFlowService],
        TestBed.inject(EnvironmentInjector),
      );

      flow = screen.get(ErasureCancellationFlowService);

      return screen;
    }

    it('asks the device nothing when the screen goes before the challenge answers', async () => {
      // Arrange
      const screen = provideOnAScreen();
      flow.cancel();
      const challenge = await requestTo(OPTIONS_URL);

      // Act
      screen.destroy();
      if (!challenge.cancelled) {
        challenge.flush(OPTIONS);
      }
      await settle();

      // Assert
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(http.match(CANCELLATION_URL)).toEqual([]);
    });

    it('cancels the device’s prompt and posts nothing when the screen goes mid-ceremony', async () => {
      // Arrange
      ceremony.held = true;
      const screen = provideOnAScreen();
      flow.cancel();
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();
      const abort = ceremony.assertPasskey.mock.calls[0]?.[1];
      expect(abort, 'the ceremony was handed no abort signal').toBeInstanceOf(
        AbortSignal,
      );

      // Act
      screen.destroy();

      // Assert
      // The system sheet comes down with the screen rather than asking for a
      // passkey on behalf of nothing.
      expect(abort?.aborted).toBe(true);

      // Act
      ceremony.settle();
      await settle();

      // Assert
      expect(http.match(CANCELLATION_URL)).toEqual([]);
      expect(session.erasureCancelled).not.toHaveBeenCalled();
    });

    // The request is out and nothing can recall it. If it lands, the schedule
    // is gone whatever became of the screen, and the notice on every other
    // screen must say so.
    it('still publishes nothing scheduled when the 204 arrives after the screen went', async () => {
      // Arrange
      const screen = provideOnAScreen();
      flow.cancel();
      const cancelling = await reachTheCancellingRequest();

      // Act
      screen.destroy();
      cancelling.flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // Assert
      expect(session.erasureCancelled).toHaveBeenCalledTimes(1);
    });
  });
});

// Waits for a public reading to arrive; see `welcome.component.spec.ts`.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Timed out waiting for ${what}.`);
}
