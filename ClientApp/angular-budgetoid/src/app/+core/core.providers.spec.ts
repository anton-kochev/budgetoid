import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
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
  const session: Pick<SessionService, 'probe'> = {
    probe: () => {
      calls.push('session.probe');

      return probe();
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
});
