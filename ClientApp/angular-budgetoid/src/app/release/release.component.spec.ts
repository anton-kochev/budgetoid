// The release screen: what somebody who has lost every passkey and every
// recovery code sees, before the trip to Google, while the sign-in is out, on
// a locked session, and once the erasure is scheduled. See
// docs/design/components.md, "Releasing an account", and docs/design/voice.md,
// "An account nobody can open".
//
// It drives the real `ReleaseFlowService` (component-provided, so a spec that
// listed it in `TestBed` would stay green on a component that dropped its own
// `providers`), the real `SessionService` and the real `MeApiService` over the
// testing backend, so the census below sees every request the screen causes.
// Two seams are replaced: `AuthService`, which hands over the Google answer and
// starts the trip and whose real self needs the OAuth library, and the router's
// outward call, which is recorded. `ProviderDepartureService` is the real one.
//
// **The copy is the specification, not an example of it.** Every string below
// is the design book's Copy table, character for character — curly
// apostrophes, the em dash and the ellipsis included.
import {
  provideHttpClient,
  withInterceptors,
  type HttpInterceptorFn,
} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import type { Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter, type UrlTree } from '@angular/router';
import type { SessionDto } from '@app-core/api/me-api.service';
import {
  AuthService,
  type LockedSignInReturn,
} from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import { SessionService } from '@app-core/session/session.service';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { ReleaseComponent } from './release.component';

const API_ORIGIN = 'https://api.test';
const SESSION_URL = `${API_ORIGIN}/api/me/session`;
const LOCKED_SESSION_URL = `${API_ORIGIN}/api/locked-session`;
const SCHEDULE_URL = `${API_ORIGIN}/api/me/erasure/schedule`;
const REVOCATION_URL = `${API_ORIGIN}/api/me/session/revocation`;

// Every request this screen may cause, as paths. A locked session reads no
// budget content of any kind (FR-113), and its one act is the schedule — never
// the immediate erasure, which refuses it anyway.
const ALLOWED_PATHS = [
  '/api/me/session',
  '/api/locked-session',
  '/api/me/erasure/schedule',
  '/api/me/session/revocation',
] as const;

const ID_TOKEN = 'provider.token.release';

// The design book's Copy table.
const TITLE = 'Release your account';
const BEFORE_FIRST =
  'Budgetoid keeps no copy of your passkeys or recovery codes, so if you’ve lost all of them, what you recorded here can’t be recovered — by you or by us.';
const BEFORE_SECOND =
  'You can still release the account, so its email address is free for a new one. Continuing takes you to Google to choose the account you created this one with, then brings you back here.';
const CONTINUE = 'Continue with Google';
const DEPARTING = 'Taking you to Google…';
const SIGNING_IN = 'Checking your Google sign-in…';
const NO_ACCOUNT =
  'There’s no Budgetoid account for that Google account. Nothing has changed.';
const CREATE_ACCOUNT = 'Create an account';
const PROVIDER_REFUSED =
  'Google didn’t confirm that account, so nothing has changed. Try again, or choose another Google account.';
const SIGN_IN_UNRECOGNISED =
  'Budgetoid couldn’t read this request. Reload the page and try again — nothing has changed.';
const SIGN_IN_UNDETERMINED =
  'Budgetoid can’t tell whether you’re signed in. Reload the page to find out.';
const UNCONFIRMED =
  'Signing in with Google didn’t finish. Nothing has changed — try again whenever you’re ready.';
const UNAVAILABLE =
  'Budgetoid couldn’t reach Google, so nothing has changed. Try again in a minute.';
// AC5 (CON-006): the data is already unrecoverable, and only the account and
// its address are released.
const STATEMENT =
  'Signing in with Google can’t open anything you recorded. It’s already unrecoverable: nothing you hold can open it, and Budgetoid keeps no copy. Erasing the account recovers nothing — it releases the account and its email address, so you can create a new account with that address.';
const CONSEQUENCE = 'The account is erased 7 days after you ask.';
const ACKNOWLEDGEMENT =
  'I’ve lost every passkey and every recovery code for this account.';
const COMMIT = 'Erase this account';
const SCHEDULING = 'Scheduling the erasure…';
// "This account will be erased on {date} at {time}. Its email address is free
// from then." — the two halves either side of the server's instant.
const RESULT_PREFIX = 'This account will be erased on ';
const RESULT_SUFFIX = '. Its email address is free from then.';
const SCHEDULE_UNRECOGNISED =
  'Budgetoid couldn’t read this request, so nothing was scheduled. Reload the page and try again.';
const SCHEDULE_UNDETERMINED =
  'Budgetoid didn’t hear back, so this may already be scheduled. Press again to check — asking twice never changes the date.';
const SIGN_OUT = 'Sign out';

// The locked account's words. Each tells the reader a press on this device
// opens something, and nothing this person holds does.
const BANNED = [
  { word: 'Unlock', pattern: /unlock/i },
  { word: 'locked', pattern: /\blocked\b/i },
  { word: 'this tab can’t read', pattern: /this tab can[’']t read/i },
] as const;

// What would mean the screen drew budget content, the shell, or the locked
// account's notice.
const FORBIDDEN_ELEMENTS = [
  'app-shell',
  'nav',
  'app-narrative-value',
  'app-locked-account-notice',
  'app-key-rotation-section',
] as const;

const FILLED_CLASS = 'mat-mdc-unelevated-button';
const OUTLINE_CLASS = 'mat-mdc-outlined-button';

const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

// Pinned in `src/test-setup.ts` to Pacific/Kiritimati, UTC+14: 10:30 UTC on
// the 9th is 00:30 on the 10th for this reader.
const SCHEDULED_INSTANT = '2026-10-09T10:30:00Z';

const LOCKED_NOTHING_SCHEDULED = {
  kind: 'locked',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: null,
} as const satisfies SessionDto;

const LOCKED_SCHEDULED = {
  kind: 'locked',
  expiresAtUtc: '2026-10-17T08:00:00Z',
  erasure: { takesEffectAtUtc: SCHEDULED_INSTANT },
} as const satisfies SessionDto;

type PreTripStatus = 'unknown' | 'anonymous' | 'unreachable';

describe('ReleaseComponent', () => {
  let http: HttpTestingController;
  let session: SessionService;
  let departure: ProviderDepartureService;
  let fixture: ComponentFixture<ReleaseComponent> | null;
  let requested: string[];
  let navigations: string[];
  let handOff: LockedSignInReturn | null;
  let auth: {
    readonly takeLockedSignInReturn: Mock<() => LockedSignInReturn | null>;
    readonly startLockedSignIn: Mock<() => Promise<'leaving' | 'unavailable'>>;
    readonly forgetProviderToken: Mock<() => void>;
  };

  beforeEach(async () => {
    fixture = null;
    requested = [];
    navigations = [];
    handOff = null;
    auth = {
      takeLockedSignInReturn: vi.fn(() => handOff),
      startLockedSignIn: vi.fn<() => Promise<'leaving' | 'unavailable'>>(() =>
        Promise.resolve('leaving'),
      ),
      forgetProviderToken: vi.fn(),
    };

    // Every request the screen causes, as it leaves.
    const recorder: HttpInterceptorFn = (request, next) => {
      requested.push(request.url);

      return next(request);
    };

    await TestBed.configureTestingModule({
      imports: [ReleaseComponent],
      providers: [
        provideHttpClient(withInterceptors([recorder])),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
        } satisfies Provider,
        { provide: AuthService, useValue: auth } satisfies Provider,
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
    departure = TestBed.inject(ProviderDepartureService);

    const router = TestBed.inject(Router);

    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        navigations.push(
          typeof url === 'string' ? url : router.serializeUrl(url),
        );

        return Promise.resolve(true);
      },
    );
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      fixture?.destroy();
      vi.restoreAllMocks();
      TestBed.resetTestingModule();
    }
  });

  async function render(): Promise<HTMLElement> {
    fixture = TestBed.createComponent(ReleaseComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function redraw(): void {
    fixture?.detectChanges();
  }

  async function arrangePreTrip(status: PreTripStatus): Promise<void> {
    if (status === 'anonymous') {
      session.ended();
    } else if (status === 'unreachable') {
      const probe = session.probe();
      http.expectOne(SESSION_URL).error(new ProgressEvent('error'));
      await probe;
    }

    expect(session.status()).toBe(status);
  }

  function arrangeLocked(answer: SessionDto): void {
    session.establishedLocked(answer);
  }

  // Waits for one request to `url`, redrawing between looks.
  async function requestTo(url: string): Promise<TestRequest> {
    return eventually(
      () => {
        const found = http.match(url);

        if (found.length > 1) {
          throw new Error(`${found.length} requests to ${url}, not one.`);
        }

        return found[0] ?? null;
      },
      `a request to ${url}`,
      redraw,
    );
  }

  async function regionReads(host: HTMLElement, line: string): Promise<void> {
    await eventually(
      () => (collapse(statusRegion(host)?.textContent) === line ? true : null),
      `the region to read "${line}"`,
      redraw,
    );
  }

  async function tickAcknowledgement(host: HTMLElement): Promise<void> {
    const box = checkboxNamed(host, ACKNOWLEDGEMENT);

    if (box === null) {
      throw new Error('No acknowledgement checkbox.');
    }

    box.click();
    redraw();
    await fixture?.whenStable();
    redraw();
  }

  describe('every state', () => {
    // One `h1`, the same in every state: a heading that changed with the state
    // would rename the page under the reader.
    it.each<PreTripStatus>(['unknown', 'anonymous', 'unreachable'])(
      'carries one heading and one empty status region before the trip, status %s',
      async (status) => {
        // Arrange
        await arrangePreTrip(status);

        // Act
        const host = await render();

        // Assert
        expect(headings(host)).toEqual([TITLE]);
        expect(host.querySelectorAll(LIVE_REGION_SELECTOR)).toHaveLength(1);
        expect(statusRegion(host)).not.toBeNull();
        expect(collapse(statusRegion(host)?.textContent)).toBe('');
      },
    );

    it.each([
      { state: 'nothing scheduled', answer: LOCKED_NOTHING_SCHEDULED },
      { state: 'scheduled', answer: LOCKED_SCHEDULED },
    ])(
      'carries one heading and one empty status region on a locked session, $state',
      async ({ answer }) => {
        // Arrange
        arrangeLocked(answer);

        // Act
        const host = await render();

        // Assert
        expect(headings(host)).toEqual([TITLE]);
        expect(host.querySelectorAll(LIVE_REGION_SELECTOR)).toHaveLength(1);
        expect(collapse(statusRegion(host)?.textContent)).toBe('');
      },
    );

    it('carries the same heading and one region while the sign-in is out', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };

      // Act
      const host = await render();
      const request = await requestTo(LOCKED_SESSION_URL);
      redraw();

      // Assert
      expect(headings(host)).toEqual([TITLE]);
      expect(host.querySelectorAll(LIVE_REGION_SELECTOR)).toHaveLength(1);

      request.flush(LOCKED_NOTHING_SCHEDULED);
    });

    it('is polite and never an alert', async () => {
      // Act
      const host = await render();

      // Assert
      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(host.querySelector('[aria-live="assertive"]')).toBeNull();
    });

    it('draws the brand lockup at the head, and not as a link', async () => {
      // Act
      const host = await render();
      const lockup = host.querySelector('app-brand-lockup');

      // Assert
      expect(lockup).not.toBeNull();
      expect(lockup?.closest('a')).toBeNull();
    });

    it.each([
      { state: 'before the trip', answer: null },
      { state: 'nothing scheduled', answer: LOCKED_NOTHING_SCHEDULED },
      { state: 'scheduled', answer: LOCKED_SCHEDULED },
    ])('never uses the locked account’s words, $state', async ({ answer }) => {
      // Arrange
      if (answer !== null) {
        arrangeLocked(answer);
      }

      // Act
      const host = await render();
      const said = spokenText(host);

      // Assert
      for (const { word, pattern } of BANNED) {
        expect(pattern.test(said), `the screen says "${word}"`).toBe(false);
      }
    });
  });

  describe('before the trip', () => {
    it.each<PreTripStatus>(['unknown', 'anonymous', 'unreachable'])(
      'draws the two standing paragraphs and Continue, status %s',
      async (status) => {
        // Arrange
        await arrangePreTrip(status);

        // Act
        const host = await render();

        // Assert
        expect(paragraphTexts(host)).toContain(BEFORE_FIRST);
        expect(paragraphTexts(host)).toContain(BEFORE_SECOND);
        expect(controlNames(host)).toEqual([CONTINUE]);
        expect(buttonNamed(host, CONTINUE)?.classList).toContain(FILLED_CLASS);
      },
    );

    // AC5 is the locked session's statement. Before the trip nobody has been
    // signed in, and the statement would describe a session that does not
    // exist.
    it.each<PreTripStatus>(['unknown', 'anonymous', 'unreachable'])(
      'carries none of the locked surface, status %s',
      async (status) => {
        // Arrange
        await arrangePreTrip(status);

        // Act
        const host = await render();
        const text = collapse(host.textContent);

        // Assert
        expect(text).not.toContain(STATEMENT);
        expect(text).not.toContain(CONSEQUENCE);
        expect(text).not.toContain(ACKNOWLEDGEMENT);
        expect(text).not.toContain(COMMIT);
        expect(text).not.toContain(RESULT_PREFIX.trim());
      },
    );

    it('starts the trip on a press of Continue', async () => {
      // Arrange
      const host = await render();

      // Act
      buttonNamed(host, CONTINUE)?.click();
      await eventually(
        () => (auth.startLockedSignIn.mock.calls.length > 0 ? true : null),
        'the trip to start',
        redraw,
      );

      // Assert
      expect(auth.startLockedSignIn).toHaveBeenCalledTimes(1);
    });

    it('keeps Continue offered and not busy at rest', async () => {
      // Act
      const host = await render();
      const button = buttonNamed(host, CONTINUE);

      // Assert
      expect(button?.getAttribute('aria-disabled')).not.toBe('true');
      expect(button?.getAttribute('aria-busy')).toBeNull();
    });

    // `disabledInteractive` keeps the tab stop; the handler gate is what stops
    // the press, because Material halts clicks on anchors only.
    it('holds Continue while the page is departing, in the attribute and in the handler', async () => {
      // Arrange
      const host = await render();
      departure.begin();
      redraw();

      // Act
      const button = buttonNamed(host, CONTINUE);
      button?.click();
      await fixture?.whenStable();
      redraw();

      // Assert
      expect(button).not.toBeNull();
      expect(button?.getAttribute('aria-disabled')).toBe('true');
      expect(button?.getAttribute('aria-busy')).toBe('true');
      expect(auth.startLockedSignIn).not.toHaveBeenCalled();
      await regionReads(host, DEPARTING);
    });

    it('reads a trip that could not start as unavailable', async () => {
      // Arrange
      auth.startLockedSignIn.mockImplementation(() =>
        Promise.resolve('unavailable' as const),
      );
      const host = await render();

      // Act
      buttonNamed(host, CONTINUE)?.click();

      // Assert
      await regionReads(host, UNAVAILABLE);
      expect(buttonNamed(host, CONTINUE)).not.toBeNull();
    });

    it('reads an unconfirmed return in the region and sends nothing', async () => {
      // Arrange
      handOff = { kind: 'unconfirmed' };

      // Act
      const host = await render();

      // Assert
      await regionReads(host, UNCONFIRMED);
      expect(http.match(LOCKED_SESSION_URL)).toEqual([]);
      expect(buttonNamed(host, CONTINUE)).not.toBeNull();
    });
  });

  describe('signing in', () => {
    it('draws the waiting line, no paragraphs and no controls', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };

      // Act
      const host = await render();
      const request = await requestTo(LOCKED_SESSION_URL);

      // Assert
      await regionReads(host, SIGNING_IN);
      expect(collapse(host.textContent)).not.toContain(BEFORE_FIRST);
      expect(collapse(host.textContent)).not.toContain(BEFORE_SECOND);
      expect(controlNames(host)).toEqual([]);

      request.flush(LOCKED_NOTHING_SCHEDULED);
    });

    it('becomes the locked surface on the same route when the sign-in answers 200', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const host = await render();

      // Act
      (await requestTo(LOCKED_SESSION_URL)).flush(LOCKED_NOTHING_SCHEDULED);
      await eventually(
        () => (checkboxNamed(host, ACKNOWLEDGEMENT) !== null ? true : null),
        'the locked surface',
        redraw,
      );

      // Assert
      expect(paragraphTexts(host)).toContain(STATEMENT);
      expect(navigations).toEqual([]);
    });

    it.each([
      {
        why: 'a no-account refusal',
        status: 404,
        body: { refusal: 'no_account' },
        line: NO_ACCOUNT,
      },
      { why: 'a 401', status: 401, body: null, line: PROVIDER_REFUSED },
      { why: 'a 403', status: 403, body: null, line: SIGN_IN_UNRECOGNISED },
      { why: 'a 500', status: 500, body: null, line: SIGN_IN_UNDETERMINED },
    ])(
      'reads $why in the region over the trip it leaves standing',
      async ({ status, body, line }) => {
        // Arrange
        handOff = { kind: 'answered', idToken: ID_TOKEN };
        const host = await render();

        // Act
        (await requestTo(LOCKED_SESSION_URL)).flush(body, {
          status,
          statusText: 'Refused',
        });

        // Assert
        await regionReads(host, line);
        expect(paragraphTexts(host)).toContain(BEFORE_FIRST);
        expect(buttonNamed(host, CONTINUE)).not.toBeNull();
      },
    );

    it('reads a sign-in that got no answer as undetermined', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const host = await render();

      // Act
      (await requestTo(LOCKED_SESSION_URL)).error(new ProgressEvent('error'));

      // Assert
      await regionReads(host, SIGN_IN_UNDETERMINED);
    });

    // The region carries no control; the link stands below it, and only
    // while that line stands.
    it('offers Create an account below the region after a no-account refusal, and only then', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const host = await render();
      expect(controlNamed(host, CREATE_ACCOUNT)).toBeNull();

      // Act
      (await requestTo(LOCKED_SESSION_URL)).flush(
        { refusal: 'no_account' },
        { status: 404, statusText: 'Not Found' },
      );
      await regionReads(host, NO_ACCOUNT);
      const link = controlNamed(host, CREATE_ACCOUNT);

      // Assert
      expect(link?.tagName).toBe('A');
      expect(link?.getAttribute('href')).toBe('/register');
      expect(statusRegion(host)?.contains(link ?? null)).toBe(false);
      expect(controlNames(host)).toEqual([CONTINUE, CREATE_ACCOUNT].sort());
    });

    it.each([
      { why: 'a 401', status: 401 },
      { why: 'a 500', status: 500 },
    ])('offers no Create an account after $why', async ({ status }) => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const host = await render();

      // Act
      (await requestTo(LOCKED_SESSION_URL)).flush(null, {
        status,
        statusText: 'Refused',
      });
      await eventually(
        () => (collapse(statusRegion(host)?.textContent) !== '' ? true : null),
        'a line in the region',
        redraw,
      );

      // Assert
      expect(controlNamed(host, CREATE_ACCOUNT)).toBeNull();
    });
  });

  describe('a locked session with nothing scheduled', () => {
    it('offers exactly the acknowledgement, the commit and Sign out', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);

      // Act
      const host = await render();

      // Assert
      expect(controlNames(host)).toEqual(
        [ACKNOWLEDGEMENT, COMMIT, SIGN_OUT].sort(),
      );
      expect(buttonNamed(host, COMMIT)?.classList).toContain(FILLED_CLASS);
      expect(buttonNamed(host, SIGN_OUT)?.classList).toContain(OUTLINE_CLASS);
    });

    // AC5, its own paragraph, never a label and never a heading.
    it('states the loss as already true, in a paragraph of its own', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);

      // Act
      const host = await render();
      const statement = paragraphNamed(host, STATEMENT);

      // Assert
      expect(statement).not.toBeNull();
      expect(statement?.closest('label, mat-checkbox, h1, h2, h3')).toBeNull();
    });

    // A label is announced every time focus lands on its control, and a
    // consequence heard that way is noise.
    it('puts the consequence in its own block above the checkbox, never in its label', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);

      // Act
      const host = await render();
      const consequence = paragraphNamed(host, CONSEQUENCE);
      const box = checkboxNamed(host, ACKNOWLEDGEMENT);

      // Assert
      expect(consequence).not.toBeNull();
      expect(consequence?.closest('label, mat-checkbox')).toBeNull();
      expect(box).not.toBeNull();
      expect(checkboxLabel(box)).toBe(ACKNOWLEDGEMENT);
      expect(
        consequence !== null &&
          box !== null &&
          (consequence.compareDocumentPosition(box) &
            Node.DOCUMENT_POSITION_FOLLOWING) !==
            0,
      ).toBe(true);
    });

    it.each(FORBIDDEN_ELEMENTS)('draws no %s', async (selector) => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);

      // Act
      const host = await render();

      // Assert
      expect(host.querySelector(selector)).toBeNull();
    });

    it('holds the commit until the acknowledgement is ticked', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);

      // Act
      const host = await render();
      const commit = buttonNamed(host, COMMIT);

      // Assert
      expect(commit?.getAttribute('aria-disabled')).toBe('true');
      expect(commit?.getAttribute('aria-busy')).toBeNull();
    });

    // The handler refuses what the attribute draws as refused.
    it('sends nothing on a press of the commit before the acknowledgement', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();

      // Act
      buttonNamed(host, COMMIT)?.click();
      await quiet();
      redraw();

      // Assert
      expect(http.match(SCHEDULE_URL)).toEqual([]);
      expect(collapse(statusRegion(host)?.textContent)).toBe('');
    });

    it('offers the commit once the acknowledgement is ticked', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();

      // Act
      await tickAcknowledgement(host);

      // Assert
      expect(buttonNamed(host, COMMIT)?.getAttribute('aria-disabled')).not.toBe(
        'true',
      );
    });

    it('keeps the commit in place and busy, its label unchanged, while the schedule is out', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);

      // Act
      buttonNamed(host, COMMIT)?.click();
      const request = await requestTo(SCHEDULE_URL);
      redraw();

      // Assert
      const commit = buttonNamed(host, COMMIT);
      expect(commit).not.toBeNull();
      expect(commit?.getAttribute('aria-busy')).toBe('true');
      expect(commit?.getAttribute('aria-disabled')).toBe('true');
      await regionReads(host, SCHEDULING);

      request.flush({ takesEffectAtUtc: SCHEDULED_INSTANT });
    });

    it('becomes scheduled on a 200: the commit, consequence and acknowledgement leave, the statement and Sign out stay', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);

      // Act
      buttonNamed(host, COMMIT)?.click();
      (await requestTo(SCHEDULE_URL)).flush({
        takesEffectAtUtc: SCHEDULED_INSTANT,
      });
      await eventually(
        () => resultSentence(host),
        'the result sentence',
        redraw,
      );

      // Assert
      expect(buttonNamed(host, COMMIT)).toBeNull();
      expect(checkboxNamed(host, ACKNOWLEDGEMENT)).toBeNull();
      expect(collapse(host.textContent)).not.toContain(CONSEQUENCE);
      expect(paragraphTexts(host)).toContain(STATEMENT);
      expect(controlNames(host)).toEqual([SIGN_OUT]);
    });

    // The control focus stood on has gone, so focus goes to what replaced it.
    it('moves focus to the result sentence when the commit leaves', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);
      const commit = buttonNamed(host, COMMIT);
      commit?.focus();

      // Act
      commit?.click();
      (await requestTo(SCHEDULE_URL)).flush({
        takesEffectAtUtc: SCHEDULED_INSTANT,
      });
      const sentence = await eventually(
        () => resultSentence(host),
        'the result sentence',
        redraw,
      );

      // Assert
      expect(sentence.getAttribute('tabindex')).toBe('-1');
      await eventually(
        () => (document.activeElement === sentence ? true : null),
        'focus on the result sentence',
        redraw,
      );
    });

    it('says nothing over a 401, which is the interceptor’s', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);

      // Act
      buttonNamed(host, COMMIT)?.click();
      (await requestTo(SCHEDULE_URL)).flush(null, {
        status: 401,
        statusText: 'Unauthorized',
      });
      await quiet();
      redraw();

      // Assert
      expect(collapse(statusRegion(host)?.textContent)).toBe('');
    });

    it('reads a 403 in the region and keeps nothing scheduled standing', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);

      // Act
      buttonNamed(host, COMMIT)?.click();
      (await requestTo(SCHEDULE_URL)).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });

      // Assert
      await regionReads(host, SCHEDULE_UNRECOGNISED);
      expect(buttonNamed(host, COMMIT)).not.toBeNull();
      expect(resultSentence(host)).toBeNull();
    });

    // C6: the commit stays live, because a repeat answers the stored instant.
    it.each([
      {
        why: 'a 500',
        answer: (r: TestRequest) =>
          r.flush(null, { status: 500, statusText: 'Server Error' }),
      },
      {
        why: 'no answer',
        answer: (r: TestRequest) => r.error(new ProgressEvent('error')),
      },
      {
        why: 'a 200 whose instant has no offset',
        answer: (r: TestRequest) =>
          r.flush({ takesEffectAtUtc: '2026-10-09T10:30:00' }),
      },
    ])(
      'reads $why as undetermined and keeps the commit live',
      async ({ answer }) => {
        // Arrange
        arrangeLocked(LOCKED_NOTHING_SCHEDULED);
        const host = await render();
        await tickAcknowledgement(host);

        // Act
        buttonNamed(host, COMMIT)?.click();
        answer(await requestTo(SCHEDULE_URL));

        // Assert
        await regionReads(host, SCHEDULE_UNDETERMINED);
        const commit = buttonNamed(host, COMMIT);
        expect(commit).not.toBeNull();
        expect(commit?.getAttribute('aria-disabled')).not.toBe('true');
        expect(commit?.getAttribute('aria-busy')).toBeNull();
        expect(resultSentence(host)).toBeNull();
      },
    );

    it('signs out: the revocation, then welcome', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();

      // Act
      buttonNamed(host, SIGN_OUT)?.click();
      (await requestTo(REVOCATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (navigations.length > 0 ? true : null),
        'a navigation',
        redraw,
      );

      // Assert
      expect(navigations).toEqual(['/welcome']);
      expect(session.status()).toBe('anonymous');
    });
  });

  describe('a locked session with a schedule', () => {
    it('offers Sign out and nothing else', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();

      // Assert
      expect(controlNames(host)).toEqual([SIGN_OUT]);
    });

    it('keeps the statement and draws the result sentence, not the consequence', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();

      // Assert
      expect(paragraphTexts(host)).toContain(STATEMENT);
      expect(collapse(host.textContent)).not.toContain(CONSEQUENCE);
      expect(collapse(host.textContent)).not.toContain(ACKNOWLEDGEMENT);
      expect(resultSentence(host)).not.toBeNull();
    });

    it.each(FORBIDDEN_ELEMENTS)('draws no %s', async (selector) => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();

      // Assert
      expect(host.querySelector(selector)).toBeNull();
    });

    // The sentence is content, not a region line: a live region at first
    // paint is announced unreliably, and the date is a standing fact.
    it('keeps the result sentence out of the status region', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();
      const sentence = resultSentence(host);

      // Assert
      expect(sentence).not.toBeNull();
      expect(statusRegion(host)?.contains(sentence)).toBe(false);
      expect(sentence?.closest(LIVE_REGION_SELECTOR)).toBeNull();
    });

    it('wraps the instant in a time element carrying it as it arrived', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();
      const time = resultSentence(host)?.querySelector('time');

      // Assert
      expect(time).not.toBeNull();
      expect(time?.getAttribute('datetime')).toBe(SCHEDULED_INSTANT);
    });

    // The guard on this file's ability to tell a local date from a UTC one.
    it('runs in a zone where a UTC rendering can be caught', () => {
      // Act
      const offset = new Date(SCHEDULED_INSTANT).getTimezoneOffset();

      // Assert
      expect(offset).toBe(-14 * 60);
    });

    // 10:30 UTC on the 9th is 00:30 on the 10th at UTC+14. A rendering in UTC
    // — `slice` on the wire string, `getUTCDate`, `timeZone: 'UTC'` — says
    // the 9th at 10:30. The format is the reader's locale's, so the pin is on
    // the day and the hour, not the spelling.
    it('renders the date and time in the reader’s own zone', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();
      const parts = resultParts(resultSentence(host));

      // Assert
      expect(wordsOf(parts.date)).toContain('10');
      expect(wordsOf(parts.date)).not.toContain('9');
      expect(wordsOf(parts.date)).not.toContain('09');
      expect(parts.date).toMatch(/oct/i);
      expect(parts.time).toMatch(/(^|\D)(00|12):30(\D|$)/);
      expect(parts.time).not.toMatch(/10:30/);
    });

    it('says an absolute date, never a countdown', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);

      // Act
      const host = await render();
      const text = collapse(resultSentence(host)?.textContent);

      // Assert
      expect(text).toContain(RESULT_PREFIX.trim());
      expect(text).not.toMatch(
        /\bin \d+ days?\b|\btomorrow\b|\bnext\b|\btoday\b/i,
      );
    });

    it('renders the answered instant after a press in the reader’s own zone', async () => {
      // Arrange
      arrangeLocked(LOCKED_NOTHING_SCHEDULED);
      const host = await render();
      await tickAcknowledgement(host);

      // Act
      buttonNamed(host, COMMIT)?.click();
      (await requestTo(SCHEDULE_URL)).flush({
        takesEffectAtUtc: SCHEDULED_INSTANT,
      });
      const sentence = await eventually(
        () => resultSentence(host),
        'the result sentence',
        redraw,
      );
      const parts = resultParts(sentence);

      // Assert
      expect(sentence.querySelector('time')?.getAttribute('datetime')).toBe(
        SCHEDULED_INSTANT,
      );
      expect(wordsOf(parts.date)).toContain('10');
      expect(parts.time).toMatch(/(^|\D)(00|12):30(\D|$)/);
    });

    // A load that finds a schedule made no press, so the document's top stays
    // where it is.
    it('moves no focus when the load finds a schedule', async () => {
      // Arrange
      arrangeLocked(LOCKED_SCHEDULED);
      (document.activeElement as HTMLElement | null)?.blur();

      // Act
      const host = await render();
      await quiet();
      redraw();

      // Assert
      expect(resultSentence(host)).not.toBeNull();
      expect(document.activeElement).not.toBe(resultSentence(host));
      expect(document.activeElement).toBe(document.body);
    });
  });

  // FR-113: a locked session reads no budget content of any kind. Across the
  // whole visit — the return's sign-in, the schedule and the sign-out — every
  // request is one of four, and never the immediate erasure.
  describe('the requests the screen causes', () => {
    it('reaches only the session, sign-in, schedule and revocation routes', async () => {
      // Arrange
      handOff = { kind: 'answered', idToken: ID_TOKEN };
      const host = await render();
      (await requestTo(LOCKED_SESSION_URL)).flush(LOCKED_NOTHING_SCHEDULED);
      await eventually(
        () => checkboxNamed(host, ACKNOWLEDGEMENT),
        'the locked surface',
        redraw,
      );

      // Act
      await tickAcknowledgement(host);
      buttonNamed(host, COMMIT)?.click();
      (await requestTo(SCHEDULE_URL)).flush({
        takesEffectAtUtc: SCHEDULED_INSTANT,
      });
      await eventually(
        () => resultSentence(host),
        'the result sentence',
        redraw,
      );
      buttonNamed(host, SIGN_OUT)?.click();
      (await requestTo(REVOCATION_URL)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await eventually(
        () => (navigations.length > 0 ? true : null),
        'a navigation',
        redraw,
      );
      await quiet();

      // Assert
      const paths = requested.map(pathOf);
      const allowed: readonly string[] = ALLOWED_PATHS;
      expect(paths.filter((path) => !allowed.includes(path))).toEqual([]);
      expect(paths).not.toContain('/api/me/erasure');
      expect(paths).toEqual([
        '/api/locked-session',
        '/api/me/erasure/schedule',
        '/api/me/session/revocation',
      ]);
    });

    it.each<PreTripStatus>(['unknown', 'anonymous'])(
      'causes no request at all when rendered before the trip, status %s',
      async (status) => {
        // Arrange
        await arrangePreTrip(status);

        // Act
        await render();
        await quiet();

        // Assert
        expect(requested).toEqual([]);
      },
    );

    it.each([
      { state: 'nothing scheduled', answer: LOCKED_NOTHING_SCHEDULED },
      { state: 'scheduled', answer: LOCKED_SCHEDULED },
    ])(
      'causes no request at all when a locked session is rendered, $state',
      async ({ answer }) => {
        // Arrange
        arrangeLocked(answer);

        // Act
        await render();
        await quiet();

        // Assert
        expect(requested).toEqual([]);
      },
    );
  });
});

function collapse(text: string | null | undefined): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

function pathOf(url: string): string {
  return new URL(url).pathname;
}

function wordsOf(text: string): readonly string[] {
  return text.split(/[^0-9A-Za-z]+/).filter((word) => word !== '');
}

async function quiet(): Promise<void> {
  for (let turn = 0; turn < 20; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

function statusRegion(host: HTMLElement): HTMLElement | null {
  return host.querySelector<HTMLElement>('[role="status"]');
}

function headings(host: HTMLElement): readonly string[] {
  return Array.from(host.querySelectorAll('h1, h2, h3, h4, h5, h6')).map(
    (heading) =>
      `${heading.tagName === 'H1' ? '' : heading.tagName + ':'}${collapse(heading.textContent)}`,
  );
}

function paragraphTexts(host: HTMLElement): readonly string[] {
  return Array.from(host.querySelectorAll('p')).map((paragraph) =>
    collapse(paragraph.textContent),
  );
}

function paragraphNamed(host: HTMLElement, text: string): HTMLElement | null {
  return (
    Array.from(host.querySelectorAll<HTMLElement>('p')).find(
      (paragraph) => collapse(paragraph.textContent) === text,
    ) ?? null
  );
}

// Every word a reader or a screen reader meets: the text, and the accessible
// names written as attributes.
function spokenText(host: HTMLElement): string {
  const labels = Array.from(
    host.querySelectorAll('[aria-label], [title], [alt]'),
  ).flatMap((element) => [
    element.getAttribute('aria-label') ?? '',
    element.getAttribute('title') ?? '',
    element.getAttribute('alt') ?? '',
  ]);

  return collapse([host.textContent ?? '', ...labels].join(' '));
}

function buttonNamed(
  host: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  return (
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(
      (button) =>
        collapse(button.getAttribute('aria-label') ?? button.textContent) ===
        name,
    ) ?? null
  );
}

function controlNamed(host: HTMLElement, name: string): HTMLElement | null {
  return (
    Array.from(host.querySelectorAll<HTMLElement>('button, a')).find(
      (control) =>
        collapse(control.getAttribute('aria-label') ?? control.textContent) ===
        name,
    ) ?? null
  );
}

function checkboxLabel(box: HTMLInputElement | null): string {
  if (box === null) {
    return '';
  }

  const byFor =
    box.id !== ''
      ? box.ownerDocument.querySelector(`label[for="${box.id}"]`)
      : null;

  return collapse(
    box.getAttribute('aria-label') ??
      byFor?.textContent ??
      box.closest('label, mat-checkbox')?.textContent,
  );
}

function checkboxNamed(
  host: HTMLElement,
  name: string,
): HTMLInputElement | null {
  return (
    Array.from(
      host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    ).find((box) => checkboxLabel(box) === name) ?? null
  );
}

// Every control a person can operate, by the name it is announced under,
// sorted. A checkbox is named by its label; everything else by its text.
function controlNames(host: HTMLElement): readonly string[] {
  return Array.from(
    host.querySelectorAll<HTMLElement>('button, a, input, select, textarea'),
  )
    .map((control) =>
      control instanceof HTMLInputElement && control.type === 'checkbox'
        ? checkboxLabel(control)
        : collapse(control.getAttribute('aria-label') ?? control.textContent),
    )
    .sort();
}

// The innermost element whose text is the whole result sentence.
function resultSentence(host: HTMLElement): HTMLElement | null {
  const matches = Array.from(host.querySelectorAll<HTMLElement>('*')).filter(
    (element) => {
      const text = collapse(element.textContent);

      return text.startsWith(RESULT_PREFIX) && text.endsWith(RESULT_SUFFIX);
    },
  );

  return matches[matches.length - 1] ?? null;
}

// The {date} and {time} halves of the result sentence.
function resultParts(sentence: HTMLElement | null): {
  readonly date: string;
  readonly time: string;
} {
  const text = collapse(sentence?.textContent);

  if (!text.startsWith(RESULT_PREFIX) || !text.endsWith(RESULT_SUFFIX)) {
    throw new Error(`Not the result sentence: "${text}"`);
  }

  const middle = text.slice(RESULT_PREFIX.length, -RESULT_SUFFIX.length);
  const split = middle.lastIndexOf(' at ');

  if (split < 0) {
    throw new Error(`No "{date} at {time}" in "${middle}"`);
  }

  return { date: middle.slice(0, split), time: middle.slice(split + 4) };
}

// The flow is driven by `void` methods over promises and observables, so there
// is nothing to await from outside. `between` redraws, for a reading only a
// later pass produces.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
  between: () => void = () => undefined,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
    between();
  }

  throw new Error(`Timed out waiting for ${what}.`);
}
