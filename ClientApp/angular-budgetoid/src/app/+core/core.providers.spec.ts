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
}

// A macrotask, so every microtask an initializer chained has had its turn. A
// bare `await Promise.resolve()` only drains one link of the chain and would
// let "has not finished" pass on an initializer that simply had two awaits.
function afterPendingWork(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The real provider list with only its three dependencies swapped, because the
// real trio fetches `app-config.json`, Google's discovery document and
// `GET /api/me` over the network the moment the module is finalized.
function bootstrap(options: BootOptions = {}): Boot {
  const {
    load = () => Promise.resolve(true),
    probe = () => Promise.resolve(),
    status = 'authenticated',
    readStagedRotation = () => Promise.resolve(),
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
  const auth: Pick<AuthService, 'initialize'> = {
    initialize: () => {
      calls.push('auth.initialize');

      return Promise.resolve();
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
        'auth.initialize',
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
      expect(boot.calls).toContain('auth.initialize');
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

      // Assert — nothing after the read is conditional on it.
      expect(boot.calls).toContain('rotations.readStagedRotation');
      expect(boot.calls).toContain('auth.initialize');
      expect(TestBed.inject(ApplicationInitStatus).done).toBe(true);
    });
  });
});
