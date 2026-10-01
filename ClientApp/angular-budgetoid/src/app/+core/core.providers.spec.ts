import { ApplicationInitStatus, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { KeyRotationService } from '@app-core/security/key-rotation.service';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { afterEach, describe, expect, it } from 'vitest';
import { provideAppCore } from './core.providers';

const API_BASE_URL = 'https://api.budgetoid.app';

interface Boot {
  // Every call the initializer made, in the order it made them. The order is
  // the subject of this file, so it is recorded rather than asserted per stub.
  readonly calls: readonly string[];
  readonly initialized: Promise<unknown>;
}

interface BootOptions {
  readonly load?: () => Promise<boolean>;
  readonly probe?: () => Promise<void>;
  // What the probe leaves behind, which is the condition on the rotation read.
  // `authenticated` is the default because it is the state that exercises every
  // step; the anonymous case names its own.
  readonly status?: SessionStatus;
  readonly readStagedRotation?: () => Promise<void>;
  // Which trip, if any, the identity provider is redirecting back from, as
  // `AuthService.providerReturn()` answers it. `null` is the default because
  // it is every cold load but two: the provider is none of this initializer's
  // business unless a registration or an email change is coming back from it.
  readonly providerReturn?: 'registration' | 'email-change' | null;
  readonly initialize?: () => Promise<void>;
}

// A macrotask, so every microtask an initializer chained has had its turn. A
// bare `await Promise.resolve()` only drains one link of the chain and would
// let "has not finished" pass on an initializer that simply had two awaits.
function afterPendingWork(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The real provider list with only its four dependencies swapped, because the
// real ones fetch `app-config.json`, `GET /api/me` and the staged rotation over
// the network the moment the module is finalized — and the real `AuthService`
// decides from the runner's own address whether a registration is coming back,
// which is the one input these cases have to choose for themselves.
function bootstrap(options: BootOptions = {}): Boot {
  const {
    load = () => Promise.resolve(true),
    probe = () => Promise.resolve(),
    status = 'authenticated',
    readStagedRotation = () => Promise.resolve(),
    providerReturn = null,
    initialize = () => Promise.resolve(),
  } = options;

  TestBed.resetTestingModule();

  const calls: string[] = [];
  const configuration: Pick<ConfigurationService, 'getConfig' | 'load'> = {
    getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
    load: () => {
      calls.push('config.load');

      return load();
    },
  };
  const auth: Pick<
    AuthService,
    'initialize' | 'providerReturn' | 'dropEmailChangeReturn'
  > = {
    initialize: () => {
      calls.push('auth.initialize');

      return initialize();
    },
    providerReturn: () => providerReturn,
    dropEmailChangeReturn: () => {
      calls.push('auth.dropEmailChangeReturn');
    },
  };
  // The probe's answer as well as the probe, because the rotation read is
  // conditional on it. The status is a signal the way the real one is — the
  // initializer reads it, and a plain value here would let a reader that never
  // called it pass.
  const session: Pick<SessionService, 'probe' | 'status'> = {
    probe: () => {
      calls.push('session.probe');

      return probe();
    },
    status: signal<SessionStatus>(status).asReadonly(),
  };
  const rotations: Pick<KeyRotationService, 'readStagedRotation'> = {
    readStagedRotation: () => {
      calls.push('rotations.readStagedRotation');

      return readStagedRotation();
    },
  };

  TestBed.configureTestingModule({
    providers: [
      provideAppCore(),
      // After the spread, so these win over the classes `provideAppCore`
      // registers itself.
      { provide: ConfigurationService, useValue: configuration },
      { provide: AuthService, useValue: auth },
      { provide: SessionService, useValue: session },
      { provide: KeyRotationService, useValue: rotations },
    ],
  });

  // `TestBed.inject` finalizes the test module, and finalizing it is what runs
  // the `APP_INITIALIZER`s — the same fact `app.config.spec.ts` relies on.
  return {
    calls,
    initialized: TestBed.inject(ApplicationInitStatus).donePromise,
  };
}

describe('provideAppCore', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  // The probe has one owner and runs once. Without this registration the answer
  // is computed by whoever asks first — which is a guard, mid-activation, with
  // nothing to await — and every guard in the app becomes asynchronous.
  it('asks the server who the visitor is while the application starts', async () => {
    // Arrange
    const boot = bootstrap();

    // Act
    await boot.initialized;

    // Assert
    expect(boot.calls).toContain('session.probe');
  });

  // The config holds `apiBaseUrl: ''` until `load()` resolves, and a request
  // addressed with `''` is a same-origin one whenever it is made. A probe that
  // started beside the config load would therefore ask `/api/me` on this app's
  // own origin — answered **200 with `index.html`** by the static host's
  // navigation fallback, unparseable under `responseType: 'json'`, and so read
  // as unreachable on every cold load — while the credentials interceptor would
  // classify it as somebody else's origin and strip the cookie anyway.
  //
  // Not a construction-order argument any more: `BaseApiService` resolves the
  // base URL per request, which `base-api.service.spec.ts` pins. The ordering
  // stands on when the request is *made*.
  it('asks only after the configuration has loaded', async () => {
    // Arrange
    let release = (): void => undefined;
    const loaded = new Promise<boolean>((resolve) => {
      release = () => resolve(true);
    });
    const boot = bootstrap({ load: () => loaded });

    // Act
    await afterPendingWork();
    const beforeConfigResolved = [...boot.calls];
    release();
    await boot.initialized;

    // Assert
    expect(beforeConfigResolved).toContain('config.load');
    expect(beforeConfigResolved).not.toContain('session.probe');
    expect(boot.calls).toContain('session.probe');
  });

  // The half that makes every guard in the app synchronous: the initializer
  // *awaits* the probe, so bootstrapping — and therefore the first route
  // activation — cannot happen while the answer is still outstanding. Pinned by
  // withholding the answer, which is as close to "before the first activation"
  // as a spec can honestly get without bootstrapping the whole application and
  // its router. A `void this.session.probe()` inside the initializer passes the
  // test above and fails this one.
  it('does not finish starting the application while the probe is outstanding', async () => {
    // Arrange
    const boot = bootstrap({ probe: () => new Promise<void>(() => undefined) });

    // Act
    await afterPendingWork();

    // Assert
    expect(boot.calls).toContain('session.probe');
    expect(TestBed.inject(ApplicationInitStatus).done).toBe(false);
  });

  // Whether a key rotation is in flight is server state, and a browser that has
  // just loaded knows of none — so it is read here, beside the probe, or the
  // three content screens draw a list of half em dashes after every reload made
  // during a run.
  describe('the staged rotation', () => {
    it('is read for a visitor the server recognised, after the probe', async () => {
      // Arrange
      const boot = bootstrap({ status: 'authenticated' });

      // Act
      await boot.initialized;

      // Assert — the order is the subject: the condition on this read is the
      // probe's own answer, so a read made beside it would be reading a status
      // nobody has written yet.
      expect(boot.calls).toEqual([
        'config.load',
        'session.probe',
        'rotations.readStagedRotation',
      ]);
    });

    it('is not read for a visitor with no session', async () => {
      // Arrange — the route is authenticated, so an anonymous visitor asking it
      // is answered 401; `sessionExpiryInterceptor` is the single owner of "the
      // session ended" and acts on 401 alone, so an unconditional read reports
      // a session ending to somebody who never had one — on every cold load of
      // the welcome screen. An anonymous cold start pays nothing.
      const boot = bootstrap({ status: 'anonymous' });

      // Act
      await boot.initialized;

      // Assert
      expect(boot.calls).not.toContain('rotations.readStagedRotation');
    });

    it('is not read when the probe could not reach the server', async () => {
      // Arrange — the fourth status, and the one a reader collapses into the
      // third. Nothing is known about this visitor, so nothing authenticated is
      // asked on their behalf.
      const boot = bootstrap({ status: 'unreachable' });

      // Act
      await boot.initialized;

      // Assert
      expect(boot.calls).not.toContain('rotations.readStagedRotation');
    });

    it('finishes starting the application with the read in the chain', async () => {
      // Arrange — the shape a failed read really has. `readStagedRotation()`
      // publishes `null` and **resolves**: the six refusal words each say what
      // became of a *run*, and a read made before anybody pressed anything has
      // no run to have become anything. So a server that is down costs this
      // application one wrongly-drawn screen and never a blank page.
      //
      // A stub that *rejected* is deliberately not the case under test, because
      // the method has no rejecting path for one to stand in for: it is a bare
      // `try`/`catch` that sets `null` and resolves — over a refusal and over a
      // read that answers nothing at all alike — so a stub built to reject
      // would pin a contract the real service does not have. Nothing here
      // re-handles what the service already handles — the probe next to it is
      // not wrapped either, for the same reason — and the swallow is pinned
      // where it lives, in `key-rotation.service.spec.ts`.
      const boot = bootstrap({
        readStagedRotation: () => Promise.resolve(),
        status: 'authenticated',
      });

      // Act
      await boot.initialized;

      // Assert
      expect(boot.calls).toContain('rotations.readStagedRotation');
      expect(TestBed.inject(ApplicationInitStatus).done).toBe(true);
    });
  });

  // NFR-025: the identity provider is contacted only while an account is being
  // created. Configuring the client and fetching Google's discovery document
  // here, for everybody, told Google the address and time of every cold load of
  // the product — anonymous or signed in, on any screen. The exchange's
  // outbound leg starts the provider on demand from the registration screen
  // (`AuthService.signIn`); what is left for this initializer is the return leg
  // alone.
  describe('the identity provider', () => {
    it.each<SessionStatus>(['anonymous', 'authenticated', 'unreachable'])(
      'is not contacted for a visitor who is %s',
      async (status) => {
        // Arrange
        const boot = bootstrap({ status });

        // Act
        await boot.initialized;

        // Assert — the control first: without it an initializer that never ran
        // would satisfy the second expectation.
        expect(boot.calls).toContain('session.probe');
        expect(boot.calls).not.toContain('auth.initialize');
      },
    );

    // The provider redirects back to `/register` with its answer on the URL,
    // and the answer has to be read **before the router's first navigation**.
    // The library clears the fragment once it has read it; a route resolver
    // doing the same work runs inside a navigation whose target already holds
    // that fragment, and the router writes its target back to the address bar
    // after resolvers have run — so the tokens would come straight back.
    it('completes a registration coming back from the provider before the application finishes starting', async () => {
      // Arrange
      const boot = bootstrap({
        status: 'anonymous',
        providerReturn: 'registration',
        initialize: () => new Promise<void>(() => undefined),
      });

      // Act
      await afterPendingWork();

      // Assert
      expect(boot.calls).toEqual([
        'config.load',
        'session.probe',
        'auth.initialize',
      ]);
      expect(TestBed.inject(ApplicationInitStatus).done).toBe(false);
    });

    // `guestGuard` turns a visitor holding a session away from `/register`, so
    // completing an exchange for them contacts the provider for a screen they
    // will never see.
    it('is not contacted for a visitor the registration screen will turn away', async () => {
      // Arrange
      const boot = bootstrap({
        status: 'authenticated',
        providerReturn: 'registration',
      });

      // Act
      await boot.initialized;

      // Assert
      expect(boot.calls).toContain('session.probe');
      expect(boot.calls).not.toContain('auth.initialize');
    });
  });

  // The email change comes back to a tab that holds a session, and the probe's
  // authenticated arm discards the provider's tokens — nonce and marker with
  // them. So the answer is read before the probe asks anything, and the
  // hand-off is what survives the discard.
  describe('an email change coming back from the provider', () => {
    // T4. The order is the fix: the nonce the answer is checked against exists
    // only until the probe's discard.
    it('is read before the server is asked who the visitor is', async () => {
      // Arrange
      const boot = bootstrap({
        status: 'authenticated',
        providerReturn: 'email-change',
      });

      // Act
      await boot.initialized;

      // Assert
      expect(boot.calls).toEqual([
        'config.load',
        'auth.initialize',
        'session.probe',
        'rotations.readStagedRotation',
      ]);
    });

    // Awaited, not merely started first: a probe that runs while the answer is
    // still being read discards the nonce under it.
    it('holds the probe back until the answer has been read', async () => {
      // Arrange
      const boot = bootstrap({
        status: 'authenticated',
        providerReturn: 'email-change',
        initialize: () => new Promise<void>(() => undefined),
      });

      // Act
      await afterPendingWork();

      // Assert
      expect(boot.calls).toEqual(['config.load', 'auth.initialize']);
      expect(TestBed.inject(ApplicationInitStatus).done).toBe(false);
    });

    it('is not read on a signed-in boot the provider is not answering', async () => {
      // Arrange
      const boot = bootstrap({ status: 'authenticated', providerReturn: null });

      // Act
      await boot.initialized;

      // Assert — the control first, as above.
      expect(boot.calls).toContain('session.probe');
      expect(boot.calls).not.toContain('auth.initialize');
    });

    // T5. Nobody signed in means nobody to change an address for, and an
    // answer carried on to `/welcome` or `/register` reads as a registration
    // somebody never asked for.
    it('is dropped once the probe finds nobody signed in', async () => {
      // Arrange
      const boot = bootstrap({
        status: 'anonymous',
        providerReturn: 'email-change',
      });

      // Act
      await boot.initialized;

      // Assert — one read, before the probe, and the drop after it.
      expect(boot.calls).toEqual([
        'config.load',
        'auth.initialize',
        'session.probe',
        'auth.dropEmailChangeReturn',
      ]);
    });

    // Only `anonymous` says nobody is signed in. A server that could not be
    // reached, or an answer nobody has read, is not that.
    it.each<SessionStatus>(['authenticated', 'unreachable', 'unknown'])(
      'is kept when the probe answers %s',
      async (status) => {
        // Arrange
        const boot = bootstrap({ status, providerReturn: 'email-change' });

        // Act
        await boot.initialized;

        // Assert — the control first: an initializer that never read the
        // answer has nothing to drop either.
        expect(boot.calls).toContain('auth.initialize');
        expect(boot.calls).not.toContain('auth.dropEmailChangeReturn');
      },
    );
  });
});
