// The front door, and the commit where it stops being one door.
//
// Until now this screen offered a single call to action and it left the site:
// the only way into a Budgetoid account was through Google, which makes the
// account something a third party can close. From here the screen offers two —
// **Create account**, which goes to the registration flow, and **Sign in with a
// passkey**, which runs the assertion ceremony against this product's own API
// and asks the identity provider nothing at all.
//
// That second control is why this file drives a real `HttpClient` over the
// testing backend rather than stubbing `SignInService`: the service is
// **component-provided**, so a spec that listed it in `TestBed` would stay green
// on a component that had dropped its own `providers` array and started sharing
// one flow — and its failure state — with every other screen in the app. One
// seam is replaced, `WebauthnCeremonyService`, because `available()` and
// `assertPasskey()` both reach `navigator.credentials`, which this runner does
// not implement. Nothing below that seam is stubbed.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import type { Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter, type UrlTree } from '@angular/router';
import {
  WebauthnCeremonyService,
  type PasskeyAssertionCeremony,
  type PasskeyCeremonyResult,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { Store } from '@ngrx/store';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SignInService, type SignInFailure } from './sign-in.service';
import { WelcomeComponent } from './welcome.component';

const API_ORIGIN = 'https://api.test';
const OPTIONS_URL = `${API_ORIGIN}/api/passkeys/assertion/options`;
const ASSERTION_URL = `${API_ORIGIN}/api/passkeys/assertion`;

// The one configuration this file uses, at module scope because both blocks
// below need it and two copies of an origin drift. `getConfig()` is the whole of
// what the services under this screen read.
const CONFIGURATION_STUB = {
  provide: ConfigurationService,
  useValue: { getConfig: () => ({ apiBaseUrl: API_ORIGIN }) },
} satisfies Provider;

// `SessionService` discards the provider's token whenever it publishes a
// session, so it injects `AuthService` — and the real one needs
// `provideOAuthClient()`. Nothing on this screen is about the identity provider,
// so the one member it reaches is a spy. A factory rather than one instance, so
// two blocks cannot share a spy.
const PROVIDER_SERVICE_STUB: Provider = {
  provide: AuthService,
  useFactory: (): Pick<AuthService, 'forgetProviderToken'> => ({
    forgetProviderToken: vi.fn(),
  }),
};

// The two controls, by the names a screen reader announces them under. Buttons
// are verbs and sentence case, per `voice.md`.
const CREATE_ACCOUNT_BUTTON = 'Create account';
const SIGN_IN_BUTTON = 'Sign in with a passkey';
const REGISTER_ROUTE = '/register';
// Where a sign-in that worked ends, and the address `authGuard` judges against
// whatever `SessionService` is saying at the instant it is asked.
const APP_ROUTE = '/app';

// The sentence this screen ships today, and it stops being true the moment the
// passkey control lands: the Google account is *not* what signs anybody in any
// more. Pinned as the whole string because the test is that this exact
// sentence is gone.
const PROVIDER_LINE_TODAY = 'Your Google account is used only to sign you in.';

// What replaces it. Declared here so the component author has one place to copy
// from, and marketing voice rather than product voice — this is the one screen
// in the system that is still selling.
//
// Three facts, in this order, because the order is the reassurance: an account
// starts at Google, only to check an address, and signing in afterwards is the
// passkey's — and two exceptions, stated rather than hidden: changing that
// address asks Google again, and so does releasing an account nobody can open.
// The book says "the copy is the specification", so this is matched whole.
const PROVIDER_ROLE =
  'Creating an account starts with Google, to check your email address. ' +
  'After that you sign in with your passkey; Google is asked again only if you change that address, ' +
  'or release an account you can no longer open.';

// The load-bearing halves of it. The whole sentence is asserted as well, now
// that the book calls the copy the specification; these are kept so a failure
// names the half that went missing, and a version that drops any of these
// five says something else. Each is free of punctuation that a template author
// would reasonably write as an entity (`&mdash;`, `&rsquo;`), so a phrase is
// matched against what the browser renders rather than against what the file
// happens to contain.
const PROVIDER_ROLE_PHRASES = [
  'Creating an account starts with Google',
  'to check your email address',
  'sign in with your passkey',
  'Google is asked again only if you change that address',
  'or release an account you can no longer open',
] as const;

// The promise the email change broke, kept only as a negative: a screen that
// still said it would be telling a cautious person something false about the
// one other moment the provider hears from them.
const RETIRED_NEVER_AGAIN_CLAIM = 'Google is never asked again';

// The one-exception line, retired the day `/release` took its own trip to
// Google: it names the address change as the only other moment the provider
// hears from a person, and that is no longer true. The full stop is the point —
// the line that replaces it continues past "that address" with a comma, so this
// matches the old ending and never the new one.
const RETIRED_ONE_EXCEPTION_LINE =
  'Google is asked again only if you change that address.';

// The third thing on the screen a person can press, and the only one that is
// not an act: a question somebody answers about themselves. Copied from
// docs/design/components.md, "The welcome screen", rather than imported, so a
// reworded template cannot carry this file along with it.
const RELEASE_LINK = 'Lost every passkey and recovery code?';
const RELEASE_ROUTE = '/release';

// The word this screen once said after an erasure, named only so its absence
// can be asserted.
const ERASED = 'Erased.';

// What `POST /api/passkeys/assertion/options` answers with, member for member as
// `sign-in.service.spec.ts` declares it, and deliberately without an
// `allowCredentials`: a populated one turns this endpoint into an
// account-enumeration oracle.
const REQUEST_OPTIONS = {
  challenge: 'QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8',
  rpId: 'budgetoid.app',
  timeout: 120_000,
  userVerification: 'required',
} satisfies PasskeyRequestOptionsJson;

// What the ceremony hands back. Nothing on this screen reads it — it travels to
// the server unchanged, and `sign-in.service.spec.ts` is where that is asserted.
const ASSERTION_PAYLOAD = {
  credentialId: 'AQIDBAUGBwgJCgsMDQ4PEA',
  clientDataJson:
    'eyJ0eXBlIjoid2ViYXV0aG4uZ2V0IiwiY2hhbGxlbmdlIjoiUUVGQ1EwUkZSa2RJ' +
    'U1VwTFRFMU9UMUJSVWxOVVZWWlhXRmxhVzF4ZFhsOCIsIm9yaWdpbiI6Imh0dHBz' +
    'Oi8vYnVkZ2V0b2lkLmFwcCIsImNyb3NzT3JpZ2luIjpmYWxzZX0',
  authenticatorData: 'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8',
  signature: 'MEUCIQD-YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNA',
  userHandle: 'EBESExQVFhcYGRobHB0eHw',
} satisfies PasskeyAssertionPayload;

// Two causes the real server would never send, planted in two 401 bodies so
// that `says one thing however a sign-in was refused` has something to look
// for. Different from each other on purpose: the test is that the screen cannot
// tell them apart.
const REFUSAL_CAUSES = [
  'no credential is registered for that user handle',
  'the signature over the client data did not verify',
] as const;

// What Material's `mat-flat-button` and `mat-stroked-button` render as. The
// class rather than the attribute, because the class is what carries the
// treatment into the DOM and what the theme styles, and the two are mutually
// exclusive in Material's own appearance map — which is what lets a missing
// class read as "not that treatment" rather than as "possibly both".
const FILLED_CLASS = 'mat-mdc-unelevated-button';
const OUTLINE_CLASS = 'mat-mdc-outlined-button';
// Ghost — Material's text button, `mat-button`. The same class
// `codes-step.component.spec.ts` reads for its own Ghost control.
const GHOST_CLASS = 'mat-mdc-button';

// Every word the sign-in flow can end on. `satisfies` with no widening, so a
// word added to `SignInFailure` fails to compile here until somebody says how
// to reach it — and the release link is then held identical after that one too.
const EVERY_OUTCOME = {
  unsupported: true,
  cancelled: true,
  'no-prf': true,
  'ceremony-failed': true,
  'start-failed': true,
  refused: true,
  unknown: true,
} satisfies Record<SignInFailure, true>;

const OUTCOMES = Object.keys(EVERY_OUTCOME) as readonly SignInFailure[];

// Every shape that makes a node a live region, not only the one this screen
// uses. The rule is "the outcome is announced", and a sentence moved into a
// `role="log"` or an `aria-live` div satisfies it as well as `role="status"`
// does; a sentence moved out of all of them satisfies none.
const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

// Minimal MediaQueryList stub — the component only reads `.matches`.
function stubMatchMedia(matches: boolean): void {
  window.matchMedia = vi.fn().mockReturnValue({
    matches,
    media: '(prefers-reduced-motion: reduce)',
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  });
}

function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

// The key the account's wrapped envelopes open under, imported rather than
// derived because deriving one needs a PRF output and no authenticator exists
// here. `extractable: false` matches what `keyEncryptionKeyFromPasskey`
// produces.
function importKeyEncryptionKey(): Promise<CryptoKey> {
  const bytes = new Uint8Array(32);

  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = 0x20 + index;
  }

  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// The flow is driven by a `void` method over a promise, so there is no promise
// to await from outside. Polling a public reading is the honest way to wait: it
// claims nothing about how many awaits the implementation contains today, and a
// flow that never arrives fails with a sentence naming what never came rather
// than with a null dereference.
//
// `between` runs after each wait — a render, for a reading that only a later
// pass can produce.
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

// Finds a control the way a screen reader announces it, over both element types
// a call to action is written as: a `<button>` with a handler, and an `<a>`
// carrying a `routerLink`. A lookup that only knew about one of them would read
// "the screen offers no such control" for a screen that offers it in the other
// shape.
function controlsNamed(
  host: HTMLElement,
  name: string,
): readonly HTMLElement[] {
  const controls = Array.from(host.querySelectorAll<HTMLElement>('button, a'));

  return controls.filter(
    (control) =>
      collapse(
        control.getAttribute('aria-label') ?? control.textContent ?? '',
      ) === name,
  );
}

function controlNamed(host: HTMLElement, name: string): HTMLElement | null {
  return controlsNamed(host, name)[0] ?? null;
}

function liveRegion(host: HTMLElement): Element | null {
  return host.querySelector(LIVE_REGION_SELECTOR);
}

describe('WelcomeComponent', () => {
  const store = { dispatch: vi.fn() };
  const originalMatchMedia = window.matchMedia;

  beforeEach(async () => {
    store.dispatch.mockClear();
    await TestBed.configureTestingModule({
      imports: [WelcomeComponent],
      providers: [
        { provide: Store, useValue: store },
        // The template carries a `routerLink` to /release, which needs a router.
        provideRouter([]),
        // This screen used to render static marketing copy and now owns a
        // sign-in flow: it provides `SignInService`, and the template reads
        // `busy()` and `failure()` on first paint, so building the component
        // constructs that flow — and with it `SignInApiService` and
        // `SessionService`, both of which reach `ConfigurationService` for the
        // `apiBaseUrl` they address requests with. The three providers
        // below are what the flow needs to *exist*; nothing in this block
        // presses anything, so none of them is exercised, and the testing
        // backend is here so a screen that started calling out at first paint
        // would be caught rather than served.
        provideHttpClient(),
        provideHttpClientTesting(),
        CONFIGURATION_STUB,
        PROVIDER_SERVICE_STUB,
      ],
    }).compileComponents();
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
  });

  it('renders the headline and mission lead, and no provider button', () => {
    // Arrange
    stubMatchMedia(true);

    // Act
    const fixture = TestBed.createComponent(WelcomeComponent);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    // Assert
    expect(text).toContain('Always watching. Never judging.');
    expect(text).toContain(
      'One simple idea: decide what your money is for before you spend it.',
    );
    expect(text).not.toContain('Continue with Google');

    fixture.destroy();
  });
});

// The headline and the mission lead are covered by the test above, which ships
// already and is left exactly as it is; nothing below repeats them.
describe('WelcomeComponent, as the way into an account', () => {
  const store = { dispatch: vi.fn() };
  const originalMatchMedia = window.matchMedia;

  let http: HttpTestingController;
  let fixture: ComponentFixture<WelcomeComponent> | null = null;
  // Where the router was asked to go, in order. A `routerLink` and a
  // programmatic `navigate` both land here — `RouterLink` calls
  // `navigateByUrl` itself — so the recording says where the screen goes
  // without saying how the author wrote the control.
  let navigations: string[];
  // What the device answers. Read from the test rather than written into the
  // stub, because two of the tests below are about what happens when it says
  // no.
  let ceremonyOutcome: PasskeyCeremonyResult<PasskeyAssertionCeremony>;
  // Whether this browser can run a ceremony at all — the `unsupported` outcome
  // is the one reached before any request.
  let ceremonyAvailable: boolean;

  beforeEach(async () => {
    const keyEncryptionKey = await importKeyEncryptionKey();

    store.dispatch.mockClear();
    navigations = [];
    ceremonyAvailable = true;
    ceremonyOutcome = {
      ok: true,
      value: { payload: ASSERTION_PAYLOAD, keyEncryptionKey },
    };

    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'assertPasskey'
    > = {
      available: () => ceremonyAvailable,
      assertPasskey: (): Promise<
        PasskeyCeremonyResult<PasskeyAssertionCeremony>
      > => Promise.resolve(ceremonyOutcome),
    };

    await TestBed.configureTestingModule({
      imports: [WelcomeComponent],
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Store, useValue: store },
        CONFIGURATION_STUB,
        PROVIDER_SERVICE_STUB,
        { provide: WebauthnCeremonyService, useValue: ceremony },
        // `SignInService` is deliberately **not** listed. The component
        // provides it, so the flow and everything it derives die with the
        // screen; listing it here would resolve it from the root injector and
        // quietly keep this file green on a component that had dropped its own
        // `providers`.
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);

    const router = TestBed.inject(Router);

    // The real router, with its one outward call recorded rather than run: a
    // router with no declared routes rejects `/register` into a promise nothing
    // awaits, and the rejection surfaces as an unrelated failure three tests
    // later.
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
    fixture?.destroy();
    fixture = null;
    window.matchMedia = originalMatchMedia;
    vi.restoreAllMocks();
  });

  // No `http.verify()` teardown: two tests below end with a request outstanding
  // on purpose, because the answer to it is the subject.

  it('keeps one top-level heading', () => {
    // Arrange
    render();

    // Act
    const headings = host().querySelectorAll('h1');

    // Assert
    // Exactly one, and today there are none: the hero's line is an `<h2>` with
    // a `w-h1` class, which is a type style wearing a heading's name.
    // `accessibility.md` asks every screen for one `h1`, and this is the screen
    // a person meets first — the one place where a document with no top-level
    // heading is most likely to be somebody's first impression of the product.
    expect(
      headings.length,
      'the welcome screen does not carry exactly one top-level heading.',
    ).toBe(1);
  });

  it('offers creating an account as the one primary action', async () => {
    // Arrange
    render();

    // Act
    const primaries = Array.from(
      host().querySelectorAll<HTMLElement>(`.${FILLED_CLASS}`),
    );
    const create = controlNamed(host(), CREATE_ACCOUNT_BUTTON);

    press(CREATE_ACCOUNT_BUTTON);
    await settle();

    // Assert
    // The presence *and* the count. One primary per view is the buttons
    // chapter's rule, and a screen invites the failure the moment it grows a
    // second call to action: two filled buttons side by side ask the person to
    // choose between two things the design has already decided between.
    expect(
      primaries.length,
      'the welcome screen offers more than one primary action.',
    ).toBe(1);
    expect(create).not.toBeNull();
    expect(create?.classList.contains(FILLED_CLASS)).toBe(true);

    // And it goes to the registration flow rather than to the provider. This is
    // the half that changed: creating an account no longer starts by leaving
    // the site.
    expect(navigations).toEqual([REGISTER_ROUTE]);
  });

  it('offers a returning visitor a way in that is not the provider', () => {
    // Arrange
    render();

    // Act
    const signIn = controlNamed(host(), SIGN_IN_BUTTON);

    // Assert
    // Outline, not Primary — the screen's one primary belongs to the act it is
    // selling, and a returning person is looking for a control rather than
    // being persuaded by one.
    expect(
      signIn,
      `the welcome screen offers no control named "${SIGN_IN_BUTTON}".`,
    ).not.toBeNull();
    expect(signIn?.classList.contains(OUTLINE_CLASS)).toBe(true);
    expect(signIn?.classList.contains(FILLED_CLASS)).toBe(false);
  });

  it('takes a returning visitor into the passkey ceremony', () => {
    // Arrange
    render();

    // The flow as the component owns it. Resolved from the node injector, so
    // deleting the component's `providers` array cannot leave this green: with
    // nothing providing `SignInService` above it, this line is the failure.
    const service = signInService();
    const signIn = vi
      .spyOn(service, 'signIn')
      .mockImplementation(() => undefined);

    // Act
    press(SIGN_IN_BUTTON);

    // Assert
    expect(signIn).toHaveBeenCalledOnce();
  });

  it('publishes the session before it leaves the screen', async () => {
    // Arrange
    // The real `SessionService`, from the root injector the component's own
    // provider sits under — the same instance `SignInService` resolves. A stub
    // whose `established()` set nothing would make every assertion below true of
    // a flow that never published anything.
    const session = TestBed.inject(SessionService);
    const router = TestBed.inject(Router);
    // The reading has to be taken **at the moment the navigation is requested**,
    // and that is the whole mechanism. Read afterwards, both statements have
    // already run whichever order they are in, and the recording cannot tell the
    // two orders apart — which is why swapping them left the suite green.
    // `register.component.spec.ts` records the same pair at the same instant for
    // the same reason.
    const asked: { readonly url: string; readonly status: SessionStatus }[] =
      [];

    // Replaces the recorder installed in `beforeEach` for this test only: the
    // testing module is rebuilt per test, so this router — and this spy — die
    // with it.
    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (url: string | UrlTree): Promise<boolean> => {
        asked.push({
          url: typeof url === 'string' ? url : router.serializeUrl(url),
          status: session.status(),
        });

        return Promise.resolve(true);
      },
    );

    render();

    // Nothing has asked the server who this is, so the guard would refuse right
    // now. Stated outright so the assertion at the end is a change rather than a
    // reading that was already true before the flow ran.
    expect(session.status()).toBe('unknown');

    // Act
    // The whole exchange: a challenge, a willing authenticator, and the 200 that
    // carries the `__Host-budgetoid-session` cookie.
    press(SIGN_IN_BUTTON);

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the assertion options',
    );
    options.flush(REQUEST_OPTIONS);

    const assertion = await eventually(
      () => http.match(ASSERTION_URL)[0] ?? null,
      'the assertion request',
    );
    // Flushed empty on purpose. The body the server sends is read by nothing —
    // the cookie on this response is what authenticates every later request, and
    // a client that published a session off a JSON member would be re-deciding a
    // fact the cookie has already settled.
    assertion.flush(null);

    await eventually(() => asked[0] ?? null, 'the navigation into the app');
    await settle();

    // Assert
    // One navigation, to `/app`, and the session already published when the
    // router was asked to make it. **The order is the requirement.** Navigate
    // first and `authGuard` judges `/app` against a stale `'anonymous'`: the
    // person is returned to `/welcome` by the guard on the very screen they just
    // used, holding a valid session cookie the whole time, with nothing anywhere
    // reporting why — not the server, which answered 200, not the screen, which
    // says nothing because the sign-in did not fail, and not the console. It
    // reproduces every time and reads as a routing bug.
    expect(
      asked,
      'the welcome screen left for /app before the session was published.',
    ).toEqual([{ url: APP_ROUTE, status: 'authenticated' }]);
    expect(session.status()).toBe('authenticated');
  });

  it('says what happened when signing in fails', async () => {
    // Arrange
    render();

    const atRest = liveRegion(host());

    // Assert (the half that is about the screen at rest)
    // In the DOM from first paint and empty. A region created at the moment it
    // gains content is announced unreliably or not at all — which is the whole
    // failure this test exists to catch — and a region that always holds a line
    // tells somebody who has pressed nothing that something went wrong.
    expect(
      atRest,
      'the welcome screen renders no live region before anything happens.',
    ).not.toBeNull();
    expect(collapse(atRest?.textContent ?? '')).toBe('');
    expect(atRest?.getAttribute('role')).toBe('status');
    // `status`, never `alert` or `aria-live="assertive"`: nothing is typed on
    // this screen, and every sentence that lands here is the outcome of an act
    // the person asked for. Assertive interrupts whatever the reader was in the
    // middle of.
    expect(host().querySelector('[role="alert"]')).toBeNull();
    expect(host().querySelector('[aria-live="assertive"]')).toBeNull();

    // Act
    // The device declines — the sheet is closed, nothing is signed, and the
    // flow never reaches the server's verdict.
    ceremonyOutcome = { ok: false, failure: 'cancelled' };

    const service = signInService();

    press(SIGN_IN_BUTTON);

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the assertion options',
    );
    options.flush(REQUEST_OPTIONS);

    await eventually(() => service.failure(), 'the refusal to be published');
    await settle();

    // Assert
    // Something is said, in the region that was empty a moment ago. What it
    // says is copy; that it is said at all, and said where a screen reader
    // hears it, is the rule. Silence after a press is indistinguishable from a
    // control wired to nothing.
    expect(
      collapse(liveRegion(host())?.textContent ?? ''),
      'the welcome screen said nothing when the sign-in failed.',
    ).not.toBe('');
  });

  it('says one thing however a sign-in was refused', async () => {
    // Arrange
    // `PasskeyVerificationExceptionHandler` answers every refusal on this route
    // with one fixed 401 carrying no cause, and it does so on purpose: a caller
    // able to tell "no such credential" from "wrong signature" can discover
    // which user handles are registered, one guess at a time, without ever
    // holding a credential. The server refusing to make that distinction is
    // only half of it — a screen that rendered a cause it *was* handed would
    // put the oracle back in front of the person. So both refusals below carry
    // a cause the real server would never send, and the screen has to be
    // unable to tell them apart.
    const screens: string[] = [];
    const regions: string[] = [];

    // Act
    for (const cause of REFUSAL_CAUSES) {
      render();

      const service = signInService();

      press(SIGN_IN_BUTTON);

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the assertion options',
      );
      options.flush(REQUEST_OPTIONS);

      const assertion = await eventually(
        () => http.match(ASSERTION_URL)[0] ?? null,
        'the assertion request',
      );
      assertion.flush(
        { title: 'Unauthorized', detail: cause },
        { status: 401, statusText: 'Unauthorized' },
      );

      await eventually(() => service.failure(), 'the refusal to be published');
      await settle();

      screens.push(collapse(host().textContent ?? ''));
      regions.push(collapse(liveRegion(host())?.textContent ?? ''));
    }

    // Assert
    // One sentence, said twice, and not the empty string — a screen that says
    // nothing at all also says the same thing however it was refused.
    expect(regions[0]).not.toBe('');
    expect(
      regions[1],
      'the welcome screen varies what it says by the cause the server sent.',
    ).toBe(regions[0]);
    // And the whole screen with it, because a cause could be rendered anywhere:
    // beside the control, in a footnote, in a `title` the region never sees.
    expect(screens[1]).toBe(screens[0]);

    // Neither cause is on the display, in either run. This is the assertion
    // that would still fail if both refusals happened to render the same
    // *template* while echoing the server's `detail` into it.
    for (const shown of screens) {
      for (const cause of REFUSAL_CAUSES) {
        expect(
          shown.includes(cause),
          `the welcome screen repeats what the server said: "${cause}".`,
        ).toBe(false);
      }
    }
  });

  it('says the provider starts an account and is asked again only for an address change or a release', () => {
    // Arrange
    render();

    // Act
    const shown = collapse(host().textContent ?? '');

    // Assert
    // The sentence that ships today is false the moment the passkey control
    // lands: the provider is no longer what signs anybody in. Leaving it is
    // worse than leaving nothing, because it is the sentence a cautious person
    // reads before deciding whether to hand over an address at all.
    expect(
      shown.includes(PROVIDER_LINE_TODAY),
      'the welcome screen still says the Google account is what signs you in.',
    ).toBe(false);

    // And the promise the email change retired.
    expect(
      shown.includes(RETIRED_NEVER_AGAIN_CLAIM),
      'the welcome screen still says Google is never asked again.',
    ).toBe(false);

    // And the one-exception line, which `/release` made false: it names the
    // address change as the only other trip to Google, and releasing an account
    // is a second one. This expectation used to be the phrase loop's last row
    // read as a positive; the line it described is now a negative.
    expect(
      shown.includes(RETIRED_ONE_EXCEPTION_LINE),
      'the welcome screen still says the address change is the only other trip to Google.',
    ).toBe(false);

    // What replaces it says three things and two exceptions. Each phrase first,
    // so a failure names the half that went missing ...
    for (const phrase of PROVIDER_ROLE_PHRASES) {
      expect(
        shown.includes(phrase),
        `the welcome screen does not say "${phrase}". The copy to ship is: ${PROVIDER_ROLE}`,
      ).toBe(true);
    }

    // ... then the whole sentence. This used to be phrases only, so the wording
    // could be improved; the book now says the copy is the specification, and
    // the word *release* is chosen to match the title of the screen the link
    // opens, which a reworded line would quietly break.
    expect(
      shown.includes(PROVIDER_ROLE),
      `the welcome screen does not carry the provider line verbatim: ${PROVIDER_ROLE}`,
    ).toBe(true);
  });

  it('offers a Ghost link, not a button, to releasing an account', () => {
    // Arrange
    render();

    // Act
    const links = controlsNamed(host(), RELEASE_LINK);
    const link = links[0] ?? null;

    // Assert
    // Exactly one, and an anchor: it goes to an address and nothing else — no
    // request, no ceremony, no provider. A `<button>` navigating
    // programmatically would pass a lookup by name and fail this.
    expect(
      links.length,
      `the welcome screen does not carry exactly one control named "${RELEASE_LINK}".`,
    ).toBe(1);
    expect(link?.tagName).toBe('A');
    expect(link?.getAttribute('role')).toBeNull();
    // The rendered `href`, which is what a new tab and a screen reader's link
    // list both read. A `routerLink` writes it; a click handler on an `<a>`
    // with no `href` is not a link at all.
    expect(link?.getAttribute('href')).toBe(RELEASE_ROUTE);

    // Ghost, and neither of the two button treatments: quieter than both, so
    // Create account stays the one Primary and the link ranks no act.
    expect(link?.classList.contains(GHOST_CLASS)).toBe(true);
    expect(link?.classList.contains(FILLED_CLASS)).toBe(false);
    expect(link?.classList.contains(OUTLINE_CLASS)).toBe(false);
    expect(host().querySelectorAll(`.${FILLED_CLASS}`).length).toBe(1);

    // Its visible text is its whole accessible name.
    expect(link?.getAttribute('aria-label')).toBeNull();
    expect(collapse(link?.textContent ?? '')).toBe(RELEASE_LINK);
  });

  it('sets the release link apart below the outcome region', () => {
    // Arrange
    render();

    // Act
    const link = controlNamed(host(), RELEASE_LINK);
    const region = liveRegion(host());
    const create = controlNamed(host(), CREATE_ACCOUNT_BUTTON);
    const signIn = controlNamed(host(), SIGN_IN_BUTTON);
    const providerLines = Array.from(
      host().querySelectorAll<HTMLElement>('p'),
    ).filter((paragraph) =>
      collapse(paragraph.textContent ?? '').includes(PROVIDER_ROLE_PHRASES[0]),
    );

    // Assert
    expect(link, `no control named "${RELEASE_LINK}".`).not.toBeNull();
    expect(region).not.toBeNull();

    // Outside every live region: a link in one is a control narrated as news.
    expect(link?.closest(LIVE_REGION_SELECTOR) ?? null).toBeNull();

    // Below the region in document order — the order a screen reader and a
    // keyboard both walk.
    expect(
      region !== null &&
        link !== null &&
        (region.compareDocumentPosition(link) &
          Node.DOCUMENT_POSITION_FOLLOWING) !==
          0,
      'the release link does not come after the outcome region.',
    ).toBe(true);

    // Not in the row of actions: no element that holds both buttons holds the
    // link as well.
    const row = create?.parentElement ?? null;
    expect(row).not.toBeNull();
    expect(row?.contains(signIn ?? null)).toBe(true);
    expect(
      row?.contains(link),
      'the release link sits in the row of actions.',
    ).toBe(false);

    // And not folded into the provider line, which would turn the one sentence
    // a cautious person reads closely into a menu.
    for (const line of providerLines) {
      expect(
        line.contains(link),
        'the release link is folded into the provider line.',
      ).toBe(false);
    }
  });

  it('takes a person who follows the release link to /release', async () => {
    // Arrange
    render();

    // Act
    press(RELEASE_LINK);
    await settle();

    // Assert
    // One navigation, to the release screen, and nothing else: no request
    // leaves this screen for it.
    expect(navigations).toEqual([RELEASE_ROUTE]);
    expect(http.match(() => true)).toEqual([]);
  });

  it('keeps the release link identical while the ceremony runs', async () => {
    // Arrange
    render();

    const atRest = releaseLinkAsDrawn();
    const service = signInService();

    // Present at rest, or "identical" compares two absences and passes.
    expect(atRest.count, `no control named "${RELEASE_LINK}" at rest.`).toBe(1);
    // Outside every live region at rest, or "identical" holds for a link that
    // is announced with every outcome.
    expect(atRest.insideRegion, 'the release link sits in a live region.').toBe(
      false,
    );

    // Act
    // The options request is left unanswered, so the flow stays busy and the
    // region carries the waiting line.
    press(SIGN_IN_BUTTON);
    await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the assertion options',
    );
    current().detectChanges();

    // Assert
    expect(service.busy()).toBe(true);
    expect(collapse(liveRegion(host())?.textContent ?? '')).not.toBe('');
    expect(
      releaseLinkAsDrawn(),
      'the release link changed while the ceremony ran.',
    ).toEqual(atRest);
  });

  it.each(OUTCOMES)(
    'keeps the release link identical after a sign-in ends %s',
    async (outcome) => {
      // Arrange
      render();

      const atRest = releaseLinkAsDrawn();
      const service = signInService();

      // Present at rest, or "identical" compares two absences and passes.
      expect(atRest.count, `no control named "${RELEASE_LINK}" at rest.`).toBe(
        1,
      );
      // Outside every live region at rest, or "identical" holds for a link
      // that is announced with every outcome.
      expect(
        atRest.insideRegion,
        'the release link sits in a live region.',
      ).toBe(false);

      // Act
      await driveTo(outcome);

      await eventually(() => service.failure(), 'the outcome to be published');
      await settle();

      // Assert
      // The outcome landed — the region says something, and it is the word this
      // case drove — so "identical" is a comparison across a change rather than
      // two readings of a screen nothing happened to.
      expect(service.failure()).toBe(outcome);
      expect(collapse(liveRegion(host())?.textContent ?? '')).not.toBe('');

      // Not revealed, reworded or moved by any outcome. A link shown or
      // changed by a refusal would tell somebody their passkey failed
      // *because* they have lost everything.
      expect(
        releaseLinkAsDrawn(),
        `the release link changed after a sign-in ended ${outcome}.`,
      ).toEqual(atRest);

      // And still exactly one Primary.
      expect(host().querySelectorAll(`.${FILLED_CLASS}`).length).toBe(1);
    },
  );

  // The link as a reader meets it: its markup whole, where it sits among the
  // screen's controls, and which side of the region it is on. Taken twice and
  // compared, so "identical" means the same element in the same place — not
  // merely a link with the same words somewhere.
  function releaseLinkAsDrawn(): {
    readonly count: number;
    readonly markup: string;
    readonly href: string | null;
    readonly position: number;
    readonly insideRegion: boolean;
    readonly followsRegion: boolean;
  } {
    const links = controlsNamed(host(), RELEASE_LINK);
    const link = links[0] ?? null;
    const region = liveRegion(host());
    const controls = Array.from(
      host().querySelectorAll<HTMLElement>('button, a'),
    );

    return {
      count: links.length,
      markup: link?.outerHTML ?? '',
      href: link?.getAttribute('href') ?? null,
      position: link === null ? -1 : controls.indexOf(link),
      insideRegion: (link?.closest(LIVE_REGION_SELECTOR) ?? null) !== null,
      followsRegion:
        region !== null &&
        link !== null &&
        (region.compareDocumentPosition(link) &
          Node.DOCUMENT_POSITION_FOLLOWING) !==
          0,
    };
  }

  // Ends a sign-in on the word asked for, by the real route to it: the
  // ceremony stub for what the device says, the testing backend for what the
  // server says. Nothing here sets the flow's state directly.
  async function driveTo(outcome: SignInFailure): Promise<void> {
    switch (outcome) {
      case 'unsupported':
        ceremonyAvailable = false;
        press(SIGN_IN_BUTTON);
        return;
      case 'cancelled':
      case 'no-prf':
      case 'ceremony-failed':
        ceremonyOutcome = {
          ok: false,
          failure: outcome === 'ceremony-failed' ? 'failed' : outcome,
        };
        press(SIGN_IN_BUTTON);
        await answerOptions();
        return;
      case 'start-failed': {
        press(SIGN_IN_BUTTON);
        const options = await eventually(
          () => http.match(OPTIONS_URL)[0] ?? null,
          'the request for the assertion options',
        );
        options.flush(null, {
          status: 503,
          statusText: 'Service Unavailable',
        });
        return;
      }
      case 'refused':
      case 'unknown': {
        press(SIGN_IN_BUTTON);
        await answerOptions();
        const assertion = await eventually(
          () => http.match(ASSERTION_URL)[0] ?? null,
          'the assertion request',
        );
        assertion.flush(
          null,
          outcome === 'refused'
            ? { status: 401, statusText: 'Unauthorized' }
            : { status: 500, statusText: 'Internal Server Error' },
        );
        return;
      }
    }
  }

  async function answerOptions(): Promise<void> {
    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the assertion options',
    );
    options.flush(REQUEST_OPTIONS);
  }

  // The current fixture, and a failure that names the mistake rather than a
  // property read off `null` three lines later.
  function current(): ComponentFixture<WelcomeComponent> {
    if (fixture === null) {
      throw new Error('No welcome screen has been rendered.');
    }

    return fixture;
  }

  function host(): HTMLElement {
    return current().nativeElement as HTMLElement;
  }

  // A fresh screen. `render()` twice is deliberate in one test below — the
  // failure state belongs to the screen's own service instance, so two refusals
  // are two screens rather than two presses on one.
  function render(): void {
    fixture?.destroy();
    // Reduced motion, so the kinetic sentence settles instead of running a
    // timer for the length of the suite.
    stubMatchMedia(true);
    fixture = TestBed.createComponent(WelcomeComponent);
    current().detectChanges();
  }

  function signInService(): SignInService {
    return current().debugElement.injector.get(SignInService);
  }

  // Presses a control by the name it is announced under, and refuses to be
  // helpful about a missing one: a `?.click()` on `null` is a test that passes
  // because nothing happened, which is exactly the failure these assertions are
  // looking for.
  function press(name: string): void {
    const control = controlNamed(host(), name);

    if (control === null) {
      throw new Error(`The welcome screen has no control named "${name}".`);
    }

    control.click();
    current().detectChanges();
  }

  async function settle(): Promise<void> {
    await current().whenStable();
    current().detectChanges();
  }
});

// Where the erasure flow lands a tab after a 204: it ends the session and asks
// the router for Welcome, and hands the screen nothing else. So Welcome has no
// word for an erasure — a tab that watched one arrives exactly as a signed-out
// tab does. See docs/design/components.md, "The welcome screen" and "Erasure
// dialog".
describe('WelcomeComponent, after an erasure', () => {
  const store = { dispatch: vi.fn() };
  const originalMatchMedia = window.matchMedia;

  let fixture: ComponentFixture<WelcomeComponent> | null = null;

  beforeEach(async () => {
    store.dispatch.mockClear();

    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'assertPasskey'
    > = {
      available: () => true,
      assertPasskey: (): Promise<
        PasskeyCeremonyResult<PasskeyAssertionCeremony>
      > => Promise.resolve({ ok: false, failure: 'cancelled' }),
    };

    await TestBed.configureTestingModule({
      imports: [WelcomeComponent],
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Store, useValue: store },
        CONFIGURATION_STUB,
        PROVIDER_SERVICE_STUB,
        { provide: WebauthnCeremonyService, useValue: ceremony },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);

    vi.spyOn(router, 'navigateByUrl').mockImplementation(
      (): Promise<boolean> => Promise.resolve(true),
    );
  });

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    window.matchMedia = originalMatchMedia;
  });

  it('says nothing about an erasure on arrival', async () => {
    // Arrange
    // What the erasure flow does on a 204, in its order: the session ends,
    // then the router is asked for Welcome. The real `SessionService`, so the
    // screen meets the same `anonymous` it would after a real erasure.
    TestBed.inject(SessionService).ended();

    // Act
    render();
    await current().whenStable();
    current().detectChanges();

    // Assert
    // The region is there and empty: no busy line, because nothing was
    // pressed, and no refusal, because nothing was refused. And the word the
    // screen used to say is nowhere on it — the product decided an erased
    // account arrives here as any signed-out tab does.
    const region = liveRegion(host());
    expect(region).not.toBeNull();
    expect(collapse(region?.textContent ?? '')).toBe('');
    expect(collapse(host().textContent ?? '')).not.toContain(ERASED);
  });

  function current(): ComponentFixture<WelcomeComponent> {
    if (fixture === null) {
      throw new Error('No welcome screen has been rendered.');
    }

    return fixture;
  }

  function host(): HTMLElement {
    return current().nativeElement as HTMLElement;
  }

  function render(): void {
    fixture?.destroy();
    stubMatchMedia(true);
    fixture = TestBed.createComponent(WelcomeComponent);
    current().detectChanges();
  }
});
