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
import { ConfigurationService } from '@app-core/services/configuration.service';
import {
  SessionService,
  type SessionStatus,
} from '@app-core/session/session.service';
import { Store } from '@ngrx/store';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SignInService } from './sign-in.service';
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
// starts at Google, it happens once and only to check an address, and signing
// in afterwards never goes near it.
const PROVIDER_ROLE =
  'Creating an account starts with Google once, to check your email address. ' +
  'After that you sign in with your passkey, and Google is never asked again.';

// The load-bearing halves of it, and not every word: the wording above is a
// starting point somebody may improve, but a version that drops any of these
// four says something else. Each is free of punctuation that a template author
// would reasonably write as an entity (`&mdash;`, `&rsquo;`), so a phrase is
// matched against what the browser renders rather than against what the file
// happens to contain.
const PROVIDER_ROLE_PHRASES = [
  'Creating an account starts with Google once',
  'to check your email address',
  'sign in with your passkey',
  'Google is never asked again',
] as const;

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

  beforeEach(async () => {
    const keyEncryptionKey = await importKeyEncryptionKey();

    store.dispatch.mockClear();
    navigations = [];
    ceremonyOutcome = {
      ok: true,
      value: { payload: ASSERTION_PAYLOAD, keyEncryptionKey },
    };

    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'assertPasskey'
    > = {
      available: () => true,
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

  it('says the provider is used once, at creation', () => {
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

    // What replaces it says three things: the account starts at Google, that
    // happens once and only to check an address, and signing in afterwards
    // never goes near it. Phrases rather than the whole sentence, so the wording
    // can be improved without this test standing in the way — but a version that
    // drops one of these is making a different promise.
    for (const phrase of PROVIDER_ROLE_PHRASES) {
      expect(
        shown.includes(phrase),
        `the welcome screen does not say "${phrase}". The copy to ship is: ${PROVIDER_ROLE}`,
      ).toBe(true);
    }
  });

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
