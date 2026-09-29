// The flow behind the erasure dialog's commit: the typed word, the challenge,
// the passkey, the one erasing request, and the way off the screen after it.
// See docs/design/components.md, "Erasure dialog".
//
// It drives a real `HttpClient` over the testing backend, as
// `rotation-flow.service.spec.ts` does, because two facts this file pins live
// on the wire and nowhere else: *which nonce pool* the challenge comes from, and
// *what* the erasing request carries. A stub over either API service would stay
// green on a flow posting the ceremony's key material or signing over a
// sign-in challenge.
//
// Three seams are replaced. `WebauthnCeremonyService` reaches
// `navigator.credentials`, which this runner does not implement.
// `SessionService` is a spy, so the order of `ended()` against the navigation
// can be read at the instant the router is asked. The router is the real one
// with its outward call recorded — `welcome.component.spec.ts` and the sign-out
// block of `settings.component.spec.ts` record the same pair the same way.
// `ErasureNotice` is the real root holder, because it is the thing Welcome
// reads.
//
// **Vitest spies persist across cases here** (`restoreMocks` is unset), which is
// why every spy below is built fresh inside `beforeEach` rather than at module
// scope.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, type UrlTree } from '@angular/router';
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
import { ErasureNotice } from '@app-core/session/erasure-notice';
import { SessionService } from '@app-core/session/session.service';
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import {
  ErasureFlowService,
  type ErasureFailure,
  type ErasurePhase,
} from './erasure-flow.service';

const API_ORIGIN = 'https://api.budgetoid.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/reauthentication/options`;
// The sign-in pool, named only so its absence can be asserted: an assertion
// signed over one of its challenges is one the erasure gate refuses.
const SIGN_IN_OPTIONS_URL = `${API_ORIGIN}/api/passkeys/assertion/options`;
const ERASURE_URL = `${API_ORIGIN}/api/me/erasure`;
// The probe a 401 on the erasing request is read against: *is* there still a
// session?
const ME_URL = `${API_ORIGIN}/api/me`;
const WELCOME_ROUTE = '/welcome';

const ME: MeDto = {
  email: 'owner@example.test',
  budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
};

const WORD = 'erase';

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

// One press of the ceremony, answered by hand. `settle` resolves the pending
// `assertPasskey` call with whatever the test decides, so "nothing is posted
// until the passkey has answered" has a moment to be observed in.
class CeremonyStub {
  public supported = true;
  public answer: PasskeyCeremonyResult<PasskeyAssertionCeremony>;
  public held = false;
  #release: (() => void) | null = null;

  public readonly available: Mock<() => boolean> = vi.fn(() => this.supported);
  // The optional second parameter is the caller's abort signal, which the real
  // service hands to the platform prompt. Read back from `mock.calls` by the
  // teardown cases.
  public readonly assertPasskey: Mock<
    (
      options: PasskeyRequestOptionsJson,
      signal?: AbortSignal,
    ) => Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>>
  > = vi.fn(async () => {
    if (this.held) {
      await new Promise<void>((resolve) => (this.#release = resolve));
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

// What the router was asked, and what the rest of the product was saying at
// that instant. Read afterwards, every statement has run whichever order it is
// in, and the recording cannot tell the orders apart.
interface Navigation {
  readonly url: string;
  readonly endedCalls: number;
  readonly erased: boolean;
  readonly phase: ErasurePhase;
}

describe('ErasureFlowService', () => {
  let http: HttpTestingController;
  let flow: ErasureFlowService;
  let notice: ErasureNotice;
  let ceremony: CeremonyStub;
  let session: { readonly ended: Mock<() => void> };
  let navigations: Navigation[];

  beforeEach(async () => {
    // A real non-extractable key, so the ceremony value is the shape the
    // real service returns. Nothing here looks inside it; the point is that
    // it travels no further than the flow.
    const keyEncryptionKey = await crypto.subtle.generateKey(
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );

    ceremony = new CeremonyStub({ payload: PAYLOAD, keyEncryptionKey });
    session = { ended: vi.fn() };
    navigations = [];

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        { provide: SessionService, useValue: session },
        ErasureFlowService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    flow = TestBed.inject(ErasureFlowService);
    notice = TestBed.inject(ErasureNotice);

    const router = TestBed.inject(Router);

    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        navigations.push({
          url: typeof url === 'string' ? url : router.serializeUrl(url),
          endedCalls: session.ended.mock.calls.length,
          erased: notice.erased(),
          phase: flow.phase(),
        });

        return Promise.resolve(true);
      },
    );
  });

  // Waits for a request to go out and hands it back. The flow is a `void`
  // method over a promise, so there is nothing to await from outside; polling
  // claims nothing about how many awaits it contains.
  //
  // `match` removes what it finds from the open list, so a second request
  // found in the same sweep would otherwise vanish unremarked — and "one
  // request, not two" is a claim several cases below make.
  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(() => {
      const found = http.match(url);

      if (found.length > 1) {
        throw new Error(`${found.length} requests to ${url}, not one.`);
      }

      return found[0] ?? null;
    }, `a request to ${url}`);
  }

  // Answers the challenge leg and waits until the erasing request is out.
  async function reachTheErasingRequest(): Promise<TestRequest> {
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);

    return requestTo(ERASURE_URL);
  }

  // Lets every pending promise in the flow run.
  async function settle(): Promise<void> {
    for (let turn = 0; turn < 5; turn += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  }

  it('ends the session before it asks to leave for Welcome', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Act
    erasing.flush(null, { status: 204, statusText: 'No Content' });
    await eventually(() => navigations[0] ?? null, 'the navigation to Welcome');

    // Assert
    // **The order is Sign out's, for Sign out's reason.** `ended()` is the
    // single owner of clearing the account's keys from this tab, and the guard
    // on the way out reads the session the moment the router is asked:
    // navigate first and `guestGuard` judges `/welcome` against a stale
    // `authenticated` and sends the person back into an account that no longer
    // exists.
    expect(navigations).toHaveLength(1);
    expect(navigations[0]?.url).toBe(WELCOME_ROUTE);
    expect(
      navigations[0]?.endedCalls,
      'the flow asked to leave for Welcome before it ended the session.',
    ).toBe(1);
  });

  it('has told Welcome about the erasure by the time it asks to go there', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Act
    erasing.flush(null, { status: 204, statusText: 'No Content' });
    await eventually(() => navigations[0] ?? null, 'the navigation to Welcome');

    // Assert
    // Welcome renders "Erased." from the notice on its first paint. Marked
    // after the navigation, the word lands on a screen that has already
    // rendered — or, once the router resolves synchronously, never at all.
    expect(navigations[0]?.erased).toBe(true);
    expect(navigations[0]?.phase).toBe('erased');
  });

  it('reads a 401 from the erasing request as refused, and stays', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Act
    erasing.flush(null, { status: 401, statusText: 'Unauthorized' });
    (await requestTo(ME_URL)).flush(ME);
    await settle();

    // Assert
    // A 401 here has two readings, and the probe after it tells them apart:
    // the session is still there, so the gate declined the assertion. That
    // erased nothing through this request. The flow neither ends the session
    // nor navigates, because that signs somebody out of an account they are
    // still inside and takes away the one sentence that says what happened.
    expect(flow.failure()).toBe('refused');
    expect(session.ended).not.toHaveBeenCalled();
    expect(navigations).toEqual([]);
    expect(notice.erased()).toBe(false);
    expect(flow.working()).toBe(false);
  });

  it('asks the server for nothing while the typed word does not match', () => {
    // Act
    flow.erase('nope');

    // Assert
    // The gate is in the method as well as on the button, because Material's
    // click-halt is applied to anchors only: on a `<button>` the press arrives
    // whatever `aria-disabled` says. Here an ungated press would spend a nonce
    // and raise a system sheet for somebody who typed the wrong word.
    expect(http.match(OPTIONS_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    expect(flow.phase()).toBe('idle');
    expect(flow.failure()).toBeNull();
  });

  it('accepts the word as a phone keyboard types it', async () => {
    // Act
    flow.erase(' Erase ');

    // Assert
    // The flow reads the same predicate the button does. A flow that compared
    // the raw string would leave a live-looking commit that refuses every
    // press from a phone.
    await requestTo(OPTIONS_URL);
    expect(flow.phase()).toBe('asserting');
  });

  it('makes one challenge and one erasing request however often it is pressed', async () => {
    // Arrange
    flow.erase(WORD);

    // Act
    // Pressed again while the challenge is in flight, and again once the
    // erasing request is out.
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();
    flow.erase(WORD);
    await settle();

    // Assert
    // A second challenge is a second nonce and a second system sheet over the
    // first; a second erasing request after a lost 204 is answered 401 and
    // rendered as *nothing was erased* over an account that is gone.
    expect(http.match(OPTIONS_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
    expect(http.match(ERASURE_URL)).toHaveLength(0);

    erasing.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('posts nothing until the passkey has answered', async () => {
    // Arrange
    ceremony.held = true;
    flow.erase(WORD);

    // Act
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);
    await eventually(
      () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
      'the ceremony to start',
    );
    await settle();

    // Assert
    // Every failure sentence above `refused` ends *nothing was erased*, and
    // that clause is a fact about this client only because nothing is posted
    // before the ceremony answers. A flow that posted early makes five
    // sentences false at once.
    expect(http.match(ERASURE_URL)).toHaveLength(0);
    expect(flow.phase()).toBe('asserting');
    expect(flow.working()).toBe(true);

    // Act
    ceremony.settle();
    const erasing = await requestTo(ERASURE_URL);

    // Assert
    expect(flow.phase()).toBe('erasing');
    expect(flow.working()).toBe(true);

    erasing.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('signs the ceremony over a re-authentication challenge', async () => {
    // Act
    flow.erase(WORD);
    const options = await requestTo(OPTIONS_URL);
    options.flush(OPTIONS);
    const erasing = await requestTo(ERASURE_URL);

    // Assert
    // The re-authentication pool, never the sign-in one: the erasure gate
    // refuses an assertion signed over an assertion-pool challenge. And the
    // server's options unchanged — an assertion run against options this
    // client invented is one the route refuses too.
    expect(options.request.method).toBe('POST');
    expect(http.match(SIGN_IN_OPTIONS_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).toHaveBeenCalledWith(
      OPTIONS,
      expect.any(AbortSignal),
    );

    erasing.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('marks the erasing request and leaves the challenge unmarked', async () => {
    // Act
    flow.erase(WORD);
    const options = await requestTo(OPTIONS_URL);
    options.flush(OPTIONS);
    const erasing = await requestTo(ERASURE_URL);

    // Assert
    // Two different handlings of a 401. On the challenge it is a session that
    // really has ended, which `sessionExpiryInterceptor` owns — so unmarked.
    // On the erasing request it may be the gate's verdict, which this flow says
    // as `refused` — so marked, or the interceptor takes the tab to Welcome
    // over a sentence the dialog never got to say.
    expect(options.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
    expect(erasing.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(true);

    erasing.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends the five assertion members and nothing of the ceremony beside them', async () => {
    // Act
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Assert
    // The ceremony answers with the payload *and* the key-encryption key. The
    // key must never leave the tab, and the cheapest wrong implementation —
    // posting the ceremony value, or spreading it into the body — puts it (or
    // a `payload` wrapper) on the wire. The key count is what catches that;
    // `toEqual` alone would not see an extra member serialized as `{}`.
    const sent = erasing.request.body as Record<string, unknown>;

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

    erasing.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('cannot tell whether the account is gone when the erasing request gets no answer', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Act
    erasing.error(new ProgressEvent('error'), {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();

    // Assert
    expect(flow.failure()).toBe('undetermined');
    expect(session.ended).not.toHaveBeenCalled();
    expect(navigations).toEqual([]);
    expect(notice.erased()).toBe(false);
  });

  it('never asks again once it cannot tell', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();
    erasing.error(new ProgressEvent('error'), {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();

    // Act
    flow.erase(WORD);
    await settle();

    // Assert
    // **Erasure is not idempotent to the caller.** A second request after a
    // lost 204 is answered 401 — the session was deleted with the account —
    // and would be rendered `refused`, *nothing was erased*, over an account
    // that no longer exists. So the commit is withdrawn and the press refused
    // before it reaches even the challenge. No retry is automatic either:
    // nothing else went out after the first request failed.
    expect(flow.pressable(WORD)).toBe(false);
    expect(http.match(OPTIONS_URL)).toHaveLength(0);
    expect(http.match(ERASURE_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
    expect(flow.failure()).toBe('undetermined');
  });

  it.each([500, 502, 504])(
    'cannot tell whether the account is gone on a %i',
    async (status) => {
      // Arrange
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();

      // Act
      erasing.flush(null, { status, statusText: 'Server Error' });
      await settle();

      // Assert
      expect(flow.failure()).toBe('undetermined');
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
    },
  );

  it.each([400, 403])(
    'says a %i is a request this client could not use',
    async (status) => {
      // Arrange
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();

      // Act
      erasing.flush(null, { status, statusText: 'Refused' });
      await settle();

      // Assert
      // Raised before the handler is entered, so nothing about the account
      // was judged and a later press is allowed — unlike `undetermined`.
      expect(flow.failure()).toBe('unrecognised');
      expect(flow.working()).toBe(false);
      expect(flow.pressable(WORD)).toBe(true);
      expect(session.ended).not.toHaveBeenCalled();
    },
  );

  it('spends no challenge on a browser that cannot check a passkey', () => {
    // Arrange
    ceremony.supported = false;

    // Act
    flow.erase(WORD);

    // Assert
    // `SignInService`'s reason: a re-authentication nonce is persisted
    // server-side, and one spent by a browser that was never going to finish
    // is spent for nothing.
    expect(http.match(OPTIONS_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    expect(flow.failure()).toBe('unsupported');
    expect(flow.working()).toBe(false);
  });

  it.each([
    { label: 'no answer at all', status: 0 },
    { label: 'a server error', status: 500 },
    { label: 'a proxy that gave up', status: 502 },
  ])(
    'says it could not start when the challenge meets $label',
    async ({ status }) => {
      // Arrange
      flow.erase(WORD);

      // Act
      const options = await requestTo(OPTIONS_URL);
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
      // Nothing was minted that this browser holds, so no ceremony runs and
      // nothing is posted — which is what makes *nothing was erased* true.
      expect(flow.failure()).toBe('unstarted');
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(http.match(ERASURE_URL)).toHaveLength(0);
      expect(flow.working()).toBe(false);
    },
  );

  it('leaves a 401 on the challenge to the session interceptor', async () => {
    // Arrange
    flow.erase(WORD);

    // Act
    (await requestTo(OPTIONS_URL)).flush(null, {
      status: 401,
      statusText: 'Unauthorized',
    });
    await settle();

    // Assert
    // **Chosen, not inherited.** A 401 on the challenge is a session that has
    // really ended, and `sessionExpiryInterceptor` owns that fact: it ends the
    // session, takes the tab to Welcome, and the screen's teardown takes the
    // overlay with it. So the flow says nothing — `unstarted` would claim the
    // server was unreachable when it answered — and does none of the
    // interceptor's work itself: a second owner of "the session ended" is how
    // the two drift.
    expect(flow.failure()).toBeNull();
    expect(flow.phase()).toBe('idle');
    expect(flow.working()).toBe(false);
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    expect(http.match(ERASURE_URL)).toHaveLength(0);
    expect(session.ended).not.toHaveBeenCalled();
    expect(navigations).toEqual([]);
  });

  it.each([
    ['unsupported', 'unsupported'],
    ['cancelled', 'cancelled'],
    ['no-prf', 'no-prf'],
    ['failed', 'ceremony-failed'],
    ['duplicate', 'ceremony-failed'],
  ] satisfies readonly (readonly [PasskeyCeremonyFailure, ErasureFailure])[])(
    'says a ceremony that ended %s as %s and posts nothing',
    async (refused, word) => {
      // Arrange
      ceremony.answer = { ok: false, failure: refused };
      flow.erase(WORD);

      // Act
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await settle();

      // Assert
      // `failed` is renamed because on this surface it would read as "erasing
      // failed". `duplicate` is an authenticator declining a credential in an
      // exclusion list, which an assertion carries none of, so it folds into
      // `ceremony-failed` rather than saying a thing that did not happen.
      expect(flow.failure()).toBe(word);
      expect(http.match(ERASURE_URL)).toHaveLength(0);
      expect(flow.working()).toBe(false);
      expect(flow.phase()).toBe('idle');
    },
  );

  it('clears the last refusal the moment a new press starts', async () => {
    // Arrange
    ceremony.answer = { ok: false, failure: 'cancelled' };
    flow.erase(WORD);
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);
    await eventually(() => flow.failure(), 'the refusal');

    // Act
    flow.erase(WORD);

    // Assert
    // Cleared when the act *starts*, synchronously. Cleared anywhere later and
    // *The passkey check was cancelled* stands over the press that retried it.
    expect(flow.failure()).toBeNull();
    expect(flow.phase()).toBe('asserting');

    (await requestTo(OPTIONS_URL)).flush(OPTIONS);
  });

  it('keeps a refusal standing while the word is being retyped', async () => {
    // Arrange
    ceremony.answer = { ok: false, failure: 'cancelled' };
    flow.erase(WORD);
    (await requestTo(OPTIONS_URL)).flush(OPTIONS);
    await eventually(() => flow.failure(), 'the refusal');

    // Act
    // A press the word gate refuses is not a press that started anything.
    flow.erase('eras');

    // Assert
    // *A press clears the previous line as it starts, and nothing else clears
    // it* — so a refused press leaves the last sentence where it was.
    expect(flow.failure()).toBe('cancelled');
  });

  it('refuses every press once the account is erased', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();
    erasing.flush(null, { status: 204, statusText: 'No Content' });
    await eventually(() => navigations[0] ?? null, 'the navigation to Welcome');

    // Act
    flow.erase(WORD);
    await settle();

    // Assert
    // The dialog lives until the screen's teardown closes it, and a press in
    // that window would ask a deleted session for a challenge.
    expect(flow.phase()).toBe('erased');
    expect(flow.working()).toBe(true);
    expect(flow.pressable(WORD)).toBe(false);
    expect(http.match(OPTIONS_URL)).toHaveLength(0);
    expect(ceremony.assertPasskey).toHaveBeenCalledTimes(1);
  });

  it('lets the person try again after a 401 from the erasing request', async () => {
    // Arrange
    flow.erase(WORD);
    const erasing = await reachTheErasingRequest();

    // Act
    erasing.flush(null, { status: 401, statusText: 'Unauthorized' });
    (await requestTo(ME_URL)).flush(ME);
    await settle();

    // Assert
    // A 401 there, with the session still answering the probe, is the gate
    // declining the assertion — it erased nothing. So the commit stays open and
    // the sentence says *Try again with a passkey you made for it*. Only
    // `undetermined` closes the commit; a flow that closed it on every
    // request-side word would make that sentence a lie.
    expect(flow.failure()).toBe('refused');
    expect(flow.pressable(WORD)).toBe(true);
  });

  it('reads a 403 on the challenge as unrecognised', async () => {
    // Arrange
    flow.erase(WORD);

    // Act
    (await requestTo(OPTIONS_URL)).flush(null, {
      status: 403,
      statusText: 'Forbidden',
    });
    await settle();

    // Assert
    // Refused before any handler ran: *try again in a minute* would send
    // somebody round the same refusal forever, where a reload is what can
    // change it. Still nothing minted, nothing asked, nothing posted.
    expect(flow.failure()).toBe('unrecognised');
    expect(ceremony.assertPasskey).not.toHaveBeenCalled();
    expect(http.match(ERASURE_URL)).toHaveLength(0);
    expect(flow.working()).toBe(false);
  });

  // A 401 on the erasing request has two readings: the gate declined the
  // assertion, or the session had already ended — expired, revoked, erased from
  // another tab — and the route's fallback policy turned the request away before
  // the gate. The erasing request is marked, so the interceptor hears neither.
  // The flow asks, **unmarked**, whether there is still a session, and lets the
  // answer decide: a session that is there makes it the gate's `refused`; one
  // that is not is the interceptor's to end, on the probe's own 401.
  describe('after a 401 on the erasing request', () => {
    async function refuseTheErasingRequest(): Promise<void> {
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();

      erasing.flush(null, { status: 401, statusText: 'Unauthorized' });
    }

    it('asks who this is, unmarked, before reading a 401 on the erasing request', async () => {
      // Act
      await refuseTheErasingRequest();
      const probe = await requestTo(ME_URL);

      // Assert
      // Unmarked, or its 401 is suppressed exactly as the erasing request's
      // was and the dead session is never ended. Still `erasing` while it is
      // out: no sentence has been earned yet, and the commit must not reopen
      // for a second press over an answer nobody has read.
      expect(probe.request.method).toBe('GET');
      expect(probe.request.context.get(EXPECTS_UNAUTHENTICATED)).toBe(false);
      expect(flow.phase()).toBe('erasing');
      expect(flow.failure()).toBeNull();
      expect(flow.pressable(WORD)).toBe(false);

      probe.flush(ME);
    });

    it('says refused once the probe finds the session still there', async () => {
      // Arrange
      await refuseTheErasingRequest();
      const probe = await requestTo(ME_URL);

      // Act
      probe.flush(ME);
      await eventually(() => flow.failure(), 'the refusal');

      // Assert
      expect(flow.failure()).toBe('refused');
      expect(flow.phase()).toBe('idle');
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
    });

    it('says nothing once the probe finds the session gone, and leaves ending it to the interceptor', async () => {
      // Arrange
      await refuseTheErasingRequest();
      const probe = await requestTo(ME_URL);

      // Act
      probe.flush(null, { status: 401, statusText: 'Unauthorized' });
      await settle();

      // Assert
      // *Didn't accept that passkey* would be false: nothing judged it. The
      // probe's own 401 is unmarked, so `sessionExpiryInterceptor` ends the
      // session and takes the tab to Welcome — and a flow that did either
      // itself is a second owner of that fact.
      expect(flow.failure()).toBeNull();
      expect(flow.phase()).toBe('idle');
      expect(flow.working()).toBe(false);
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
      expect(notice.erased()).toBe(false);
    });

    it.each([
      { label: 'no answer at all', status: 0 },
      { label: 'a server error', status: 500 },
    ])('says refused when the probe meets $label', async ({ status }) => {
      // Arrange
      await refuseTheErasingRequest();
      const probe = await requestTo(ME_URL);

      // Act
      if (status === 0) {
        probe.error(new ProgressEvent('error'), {
          status: 0,
          statusText: 'Unknown Error',
        });
      } else {
        probe.flush(null, { status, statusText: 'Failed' });
      }
      await eventually(() => flow.failure(), 'the refusal');

      // Assert
      // The erasing request's 401 already proved it erased nothing; a probe
      // that could not answer takes nothing away from that. `refused` is
      // true on either reading, and the retry's unmarked challenge catches
      // a session that really has gone.
      expect(flow.failure()).toBe('refused');
      expect(flow.phase()).toBe('idle');
      expect(session.ended).not.toHaveBeenCalled();
    });
  });

  // The flow dies with the screen that provided it, and an attempt abandoned
  // there asks and sends nothing more. Provided here in an environment
  // injector the case can destroy, which is what the screen's teardown is to a
  // component-provided service.
  describe('when the screen goes', () => {
    function provideOnAScreen(): EnvironmentInjector {
      const screen = createEnvironmentInjector(
        [ErasureFlowService],
        TestBed.inject(EnvironmentInjector),
      );

      // The navigation recorder reads `flow`, so it reads this one.
      flow = screen.get(ErasureFlowService);

      return screen;
    }

    async function theCeremonyStarts(): Promise<void> {
      await eventually(
        () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
        'the ceremony to start',
      );
    }

    it('asks the device nothing when the screen goes before the challenge answers', async () => {
      // Arrange
      const screen = provideOnAScreen();
      flow.erase(WORD);
      const challenge = await requestTo(OPTIONS_URL);

      // Act
      screen.destroy();
      // A flow may cancel the challenge as it goes; one that does has nothing
      // left to answer.
      if (!challenge.cancelled) {
        challenge.flush(OPTIONS);
      }
      await settle();

      // Assert
      // A system sheet raised over a screen that has gone has nothing on it
      // to receive its answer.
      expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      expect(http.match(ERASURE_URL)).toHaveLength(0);
    });

    it('sends no erasing request when the screen goes while the device is asked', async () => {
      // Arrange
      ceremony.held = true;
      const screen = provideOnAScreen();
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      screen.destroy();
      ceremony.settle();
      await settle();

      // Assert
      // The passkey answered for a dialog nobody can see any more. Posted
      // anyway, it erases the account from a screen the person already left,
      // with nothing to say it happened.
      expect(http.match(ERASURE_URL)).toHaveLength(0);
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
    });

    it('cancels the device’s prompt when the screen goes', async () => {
      // Arrange
      ceremony.held = true;
      const screen = provideOnAScreen();
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();
      const signal = ceremony.assertPasskey.mock.calls[0]?.[1];
      expect(signal, 'the ceremony was handed no abort signal').toBeInstanceOf(
        AbortSignal,
      );
      expect(signal?.aborted).toBe(false);

      // Act
      screen.destroy();

      // Assert
      // Ignoring the answer is not enough on its own: the system sheet stays
      // up over whatever screen came next, asking for a passkey on behalf of
      // nothing.
      expect(signal?.aborted).toBe(true);

      ceremony.settle();
    });

    it('still leaves for Welcome when the 204 arrives after the screen went', async () => {
      // Arrange
      const screen = provideOnAScreen();
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();

      // Act
      // The request is out and nothing can recall it. The account is gone the
      // moment it commits, whatever became of the screen.
      screen.destroy();
      erasing.flush(null, { status: 204, statusText: 'No Content' });
      await eventually(
        () => navigations[0] ?? null,
        'the navigation to Welcome',
      );

      // Assert
      // Abandoning covers what has not been sent. A flow that dropped this
      // answer too would leave a tab holding keys and a session for an account
      // that no longer exists.
      expect(notice.erased()).toBe(true);
      expect(session.ended).toHaveBeenCalledTimes(1);
      expect(navigations).toHaveLength(1);
      expect(navigations[0]?.url).toBe(WELCOME_ROUTE);
      expect(
        navigations[0]?.endedCalls,
        'the flow asked to leave for Welcome before it ended the session.',
      ).toBe(1);
    });

    it('sends no erasing request once abandoned mid-ceremony', async () => {
      // Arrange
      ceremony.held = true;
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      // What the screen calls when its overlay is closed from outside — the
      // CDK disposes it on the browser's Back whatever `disableClose` says —
      // while the screen itself stays.
      flow.abandon();
      ceremony.settle();
      await settle();

      // Assert
      expect(http.match(ERASURE_URL)).toHaveLength(0);
      expect(session.ended).not.toHaveBeenCalled();
      expect(navigations).toEqual([]);
    });

    it('comes to rest once abandoned mid-ceremony', async () => {
      // Arrange
      ceremony.held = true;
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      flow.abandon();
      ceremony.settle();
      await settle();

      // Assert
      // The screen stays, and so does the flow on it. Left `asserting`, the
      // flow reads as working for the screen's life: every later dialog opens
      // with the commit inert, Cancel inert and `disableClose` set, and
      // `reset()` refuses to clear it.
      expect(flow.phase()).toBe('idle');
      expect(flow.working()).toBe(false);
      expect(flow.failure()).toBeNull();
    });

    it('comes to rest when the screen goes mid-ceremony', async () => {
      // Arrange
      ceremony.held = true;
      const screen = provideOnAScreen();
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await theCeremonyStarts();

      // Act
      screen.destroy();
      ceremony.settle();
      await settle();

      // Assert
      expect(flow.phase()).toBe('idle');
      expect(flow.working()).toBe(false);
    });

    it.each([
      { label: 'no answer at all', status: 0 },
      { label: 'a 403', status: 403 },
    ])(
      'says nothing when the challenge meets $label after the press was abandoned',
      async ({ status }) => {
        // Arrange
        flow.erase(WORD);
        const challenge = await requestTo(OPTIONS_URL);

        // Act
        flow.abandon();
        if (status === 0) {
          challenge.error(new ProgressEvent('error'), {
            status: 0,
            statusText: 'Unknown Error',
          });
        } else {
          challenge.flush(null, { status, statusText: 'Forbidden' });
        }
        await settle();

        // Assert
        // Whoever abandoned the press has stopped listening. A word raised
        // now lands in the region of the next dialog this screen opens — a
        // sentence about a press made in one already gone.
        expect(flow.failure()).toBeNull();
        expect(flow.phase()).toBe('idle');
        expect(ceremony.assertPasskey).not.toHaveBeenCalled();
      },
    );
  });

  describe('reset', () => {
    it('clears a refusal and leaves the flow at rest', async () => {
      // Arrange
      ceremony.answer = { ok: false, failure: 'cancelled' };
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await eventually(() => flow.failure(), 'the refusal');

      // Act
      flow.reset();

      // Assert
      // Opening the dialog starts a fresh attempt, and a refusal from the
      // last one is about a press made in a dialog already dismissed.
      expect(flow.failure()).toBeNull();
      expect(flow.phase()).toBe('idle');
      expect(flow.working()).toBe(false);
      expect(flow.pressable(WORD)).toBe(true);
    });

    it('keeps undetermined across a reset, so no later dialog offers the commit', async () => {
      // Arrange
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();
      erasing.flush(null, { status: 504, statusText: 'Gateway Timeout' });
      await settle();
      expect(flow.failure()).toBe('undetermined');

      // Act
      flow.reset();

      // Assert
      // Latched for the screen's life. A new dialog that offered the commit
      // again would stake the account on the next challenge failing — and a
      // session that outlived the erasure it could not see is exactly the case
      // where it does not.
      expect(flow.failure()).toBe('undetermined');
      expect(flow.pressable(WORD)).toBe(false);
    });

    it.each([
      { word: 'cancelled', refused: 'cancelled' },
      { word: 'no-prf', refused: 'no-prf' },
      { word: 'ceremony-failed', refused: 'failed' },
    ] satisfies readonly {
      readonly word: ErasureFailure;
      readonly refused: PasskeyCeremonyFailure;
    }[])('clears any other word on reset: $word', async ({ word, refused }) => {
      // Arrange
      ceremony.answer = { ok: false, failure: refused };
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await eventually(() => flow.failure(), 'the refusal');
      expect(flow.failure()).toBe(word);

      // Act
      flow.reset();

      // Assert
      // Only `undetermined` survives: every other word is a press that
      // erased nothing, and the next dialog's region is empty.
      expect(flow.failure()).toBeNull();
      expect(flow.pressable(WORD)).toBe(true);
    });

    it('clears unstarted on reset', async () => {
      // Arrange
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(null, {
        status: 500,
        statusText: 'Server Error',
      });
      await eventually(() => flow.failure(), 'the refusal');
      expect(flow.failure()).toBe('unstarted');

      // Act
      flow.reset();

      // Assert
      expect(flow.failure()).toBeNull();
    });

    it('changes nothing while a press is in flight', async () => {
      // Arrange
      ceremony.held = true;
      flow.erase(WORD);
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await eventually(
        () => (ceremony.assertPasskey.mock.calls.length > 0 ? true : null),
        'the ceremony to start',
      );

      // Act
      flow.reset();

      // Assert
      // A reset mid-ceremony would put the flow at rest while the device is
      // still being asked, and an answer arriving then posts the erasing
      // request from a flow that reads idle — with a commit already live for
      // a second press.
      expect(flow.phase()).toBe('asserting');
      expect(flow.working()).toBe(true);
      expect(flow.pressable(WORD)).toBe(false);

      ceremony.settle();
      (await requestTo(ERASURE_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
    });

    it('changes nothing while the erasing request is out', async () => {
      // Arrange
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();
      expect(flow.phase()).toBe('erasing');

      // Act
      flow.reset();

      // Assert
      // The one window a guard on `asserting` and `erased` alone leaves open.
      // A reset here puts the flow at rest while a request nothing can recall
      // is still out, with the commit live for a second press over an act that
      // may already have erased the account.
      expect(flow.phase()).toBe('erasing');
      expect(flow.working()).toBe(true);

      erasing.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('changes nothing once the account is erased', async () => {
      // Arrange
      flow.erase(WORD);
      const erasing = await reachTheErasingRequest();
      erasing.flush(null, { status: 204, statusText: 'No Content' });
      await eventually(
        () => navigations[0] ?? null,
        'the navigation to Welcome',
      );

      // Act
      flow.reset();

      // Assert
      // `erased` is terminal: the tab is on its way to Welcome, and a reset
      // that put it back at rest would reopen the commit over a deleted
      // session.
      expect(flow.phase()).toBe('erased');
      expect(flow.working()).toBe(true);
    });
  });

  describe('pressable', () => {
    it('is true at rest for the word as a person types it', () => {
      // Assert
      expect(flow.pressable('erase')).toBe(true);
      expect(flow.pressable(' Erase ')).toBe(true);
    });

    it('is false for a word that does not match', () => {
      // Assert
      expect(flow.pressable('')).toBe(false);
      expect(flow.pressable('eras')).toBe(false);
      expect(flow.pressable('erase everything')).toBe(false);
    });

    it('is false while the act is running', () => {
      // Act
      flow.erase(WORD);

      // Assert
      // The attribute and the handler read this one predicate; a commit that
      // looked live while the ceremony ran would take a second press into a
      // second system sheet.
      expect(flow.working()).toBe(true);
      expect(flow.pressable(WORD)).toBe(false);
    });

    it('comes back once a refusal has ended the act', async () => {
      // Arrange
      ceremony.answer = { ok: false, failure: 'cancelled' };
      flow.erase(WORD);

      // Act
      (await requestTo(OPTIONS_URL)).flush(OPTIONS);
      await eventually(() => flow.failure(), 'the refusal');

      // Assert
      // Every refusal but `undetermined` is one the person can try again from,
      // and the book's copy says so — *Try again whenever you're ready*.
      expect(flow.pressable(WORD)).toBe(true);
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
