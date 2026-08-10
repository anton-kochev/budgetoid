import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import {
  MeApiService,
  type CredentialSummary,
} from '@app-core/api/me-api.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { of, throwError, type Observable } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { credentialRegistrationDate } from './credential-registration-date';
import { SettingsComponent } from './settings.component';
import { SettingsService, type ExportFailure } from './settings.service';

// The copy is pinned as whole sentences, not fragments. A fragment assertion
// (`toContain('7 days')`, `toContain('passkey')`) survives a rewrite that
// changes what the sentence promises, which is the only thing these lines
// exist to protect.
const EMAIL_LABEL = 'Email address';
const ERASE_BUTTON = 'Erase everything';
const EXPORT_BUTTON = 'Export';
const BACKUP_WINDOW =
  'Erased data stays in point-in-time database backups for up to 7 days, and in no other place.';
const PASSKEY_EXPLANATION =
  'Erasing has to be confirmed with a passkey, and Budgetoid can’t register passkeys yet. The button stays off until it can.';
const EXPORT_BUILD_FAILURE =
  'The export couldn’t be built, so nothing was saved — Budgetoid sends the whole file or none of it. Try again in a few minutes.';
const EXPORT_SESSION_FAILURE =
  'Your session has ended. Reload the page to sign in again.';
const EXPORT_RUNNING = 'Preparing your file…';
const EXPORT_CONFIRMED = 'Exported.';
const EMAIL_FAILURE = 'Couldn’t load your email address. Reload the page.';
const OPERATOR_READABLE =
  'Budgetoid’s operators can read everything you record: amounts, dates, currency codes, account types, the order you arrange things in, the timestamps on every row, the identifiers behind them, and your email address. Today that also includes the names and notes you type. Nothing is encrypted with a key only you hold — not yet.';
const CREDENTIALS_HEADING = 'Ways to sign in';
const REGISTER_BUTTON = 'Register a passkey';
const REVOKE_BUTTON = 'Revoke';
const CEREMONY_EXPLANATION =
  'Registering and revoking both have to be confirmed with a passkey, and Budgetoid can’t run a passkey check in the browser yet. The buttons stay off until it can.';
// The class the screen's own stylesheet hangs `min-height:
// var(--bud-touch-target)` on, because Material's M3 button is shorter than the
// 48px minimum `accessibility.md` sets and `components.md` restates. jsdom
// applies no stylesheet, so a spec here cannot measure the rendered height; the
// class is the seam between the two halves, and it is the half that goes missing
// — a control written without it looks correct in every screenshot and is under
// the minimum on every phone.
const TOUCH_TARGET_CLASS = 's-button';
// What Material's `mat-stroked-button` and `mat-flat-button` render as. The
// class rather than the attribute selector, because the class is what carries
// the treatment into the DOM and what the theme styles.
const OUTLINE_CLASS = 'mat-mdc-outlined-button';
const FILLED_CLASS = 'mat-mdc-unelevated-button';
const CREDENTIALS_LOADING = 'Loading your ways to sign in…';
const CREDENTIALS_FAILURE =
  'Couldn’t load your ways to sign in. Reload the page.';
const CREDENTIALS_EMPTY = 'Nothing is attached to your account yet.';
const PASSKEY_TYPE = 'Passkey';
const FEDERATED_TYPE = 'Google';

// Two entries far enough apart to be told apart on screen, which the section's
// own rules make a requirement rather than a convenience: the row shows the
// type and the date and nothing else, so two entries of the same type on the
// same local day are genuinely indistinguishable — the design chapter calls
// that the honest maximum — and any test about *which* row it is has to give
// them different days.
//
// The dates are the ones a reader at the pinned zone sees, not the UTC days in
// the wire values: 22:00Z on the 11th is already the 12th at UTC+14.
const PASSKEY: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000a1',
  type: 'passkey',
  createdAtUtc: '2026-03-11T22:00:00Z',
};
const FEDERATED: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000b2',
  type: 'federated',
  createdAtUtc: '2026-01-12T08:30:00Z',
};
// A 200 carrying one field the screen cannot read. Every *network* failure on
// this screen is caught and rendered as a sentence; a successful response with a
// bad value in it is the one path with nothing between it and the template — and
// the formatting happens inside a `computed` the template reads, so what would
// otherwise be one wrong row is a throw during change detection that stops the
// pass on the spot.
const UNREADABLE: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000c3',
  type: 'passkey',
  createdAtUtc: 'the twelfth of March',
};
const PASSKEY_DATE = 'March 12, 2026';
const FEDERATED_DATE = 'January 12, 2026';
// The one word that introduces the date on a row. Pinned as a fragment rather
// than as a whole sentence — the exception the rest of this list is the rule for
// — because the defect it catches *is* the fragment: a row that kept the caption
// after dropping the date it introduces renders exactly this word and nothing
// after it.
const REGISTERED_CAPTION = 'Registered';
// The visible label first, so voice control still reaches the control by what
// it can see; the rest is what tells two buttons named "Revoke" apart.
const REVOKE_PASSKEY = 'Revoke Passkey, registered March 12, 2026';
const REVOKE_FEDERATED = 'Revoke Google, registered January 12, 2026';
// The same name with the clause the row cannot fill left off, rather than
// `Revoke Passkey, registered ` — a sentence that stops mid-word is read out
// loud exactly as written.
const REVOKE_DATELESS = 'Revoke Passkey';

// Real signals, not readonly wrappers: each test drives one state by setting
// them, so the component is exercised through its inputs rather than through
// the network the service would otherwise reach for.
class SettingsServiceStub {
  public readonly email = signal<string | null>(null);
  public readonly emailFailed = signal(false);
  public readonly exporting = signal(false);
  public readonly exported = signal(false);
  public readonly exportFailure = signal<ExportFailure | null>(null);
  // Starts `null`, like the real service: "not asked yet" is a third state
  // beside "here they are" and "you have none", and a stub seeded with `[]`
  // would put the screen's first paint in a state the real one never reaches.
  public readonly credentials = signal<readonly CredentialSummary[] | null>(
    null,
  );
  public readonly credentialsFailed = signal(false);
  public loadEmail = vi.fn();
  public loadCredentials = vi.fn();
  public export = vi.fn();
}

describe('SettingsComponent', () => {
  let service: SettingsServiceStub;
  let fixture: ComponentFixture<SettingsComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    service = new SettingsServiceStub();
    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [provideNoopAnimations()],
    });
    // The stub is installed on the component, not on the module. A module-level
    // provider is shadowed the moment the component declares one of its own —
    // which is how the screen stops carrying state between visits — and every
    // test here would then instantiate the real service with no HttpClient
    // behind it. `set` replaces the component's providers array, so this holds
    // whether the component provides SettingsService or leaves it to the root.
    TestBed.overrideComponent(SettingsComponent, {
      set: { providers: [{ provide: SettingsService, useValue: service }] },
    });
    await TestBed.compileComponents();
    fixture = TestBed.createComponent(SettingsComponent);
    host = fixture.nativeElement as HTMLElement;
    // Exactly one — the NFR-021 test below depends on this being the whole of
    // the interaction that precedes it.
    fixture.detectChanges();
  });

  it('loads the account email on initialization', () => {
    // Assert
    // Without this the email row is furniture: a template binding to a signal
    // nobody ever fills renders an empty value forever and every other
    // assertion here still passes.
    expect(service.loadEmail).toHaveBeenCalledOnce();
  });

  // NFR-021. The behavioural form of "nothing has to be opened first": if both
  // controls are already present after a bare render, then activating one is
  // interaction two, and there is no interaction one to spend on a disclosure.
  //
  // Limit, stated honestly: this catches a *lazily rendered* disclosure — a
  // `matExpansionPanelContent` body, a lazy tab — because those keep their
  // children out of the DOM until opened. It does not catch a CSS-collapsed
  // one, and it does not catch `<details>`, which renders its children into
  // the DOM whether or not it is open. A reviewer holds that half.
  it('offers export and erasure with no prior interaction', () => {
    // Act
    const exportButton = buttonNamed(host, EXPORT_BUTTON);
    const eraseButton = buttonNamed(host, ERASE_BUTTON);

    // Assert
    expect(exportButton).not.toBeNull();
    expect(eraseButton).not.toBeNull();
  });

  it('finds no control for a name the screen does not carry', () => {
    // Assert
    // Control for the test above. Without it a helper that ignored the name
    // and handed back the first button it found would be green on any page
    // that happened to render two buttons.
    expect(buttonNamed(host, 'Sign out')).toBeNull();
  });

  it('exports when the export control is activated', () => {
    // Arrange
    const exportButton = buttonNamed(host, EXPORT_BUTTON);
    expect(exportButton).not.toBeNull();

    // Act
    exportButton?.click();

    // Assert
    expect(service.export).toHaveBeenCalledOnce();
  });

  it('does not export on render', () => {
    // Assert
    // Control for the test above: a component calling `export()` from
    // `ngOnInit` — or from a template expression — satisfies "was called once
    // after a click" without the click having done anything.
    expect(service.export).not.toHaveBeenCalled();
  });

  // The confirmation is a requirement, not decoration. The browser saves the
  // file without a visible act of its own — no dialog, no page change, and on
  // most configurations not even a prompt — so a screen that says nothing
  // leaves the user having clicked a button that produced no observable
  // effect, which reads as broken and invites the second click the service
  // already refuses.
  it('confirms a finished export in place', () => {
    // Arrange
    service.exporting.set(false);
    service.exported.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(sectionFor(host, 'export-heading'))).toContain(
      EXPORT_CONFIRMED,
    );
  });

  it('confirms nothing before an export has finished', () => {
    // Assert
    // Control for the test above: a template rendering the confirmation
    // unconditionally passes "confirms a finished export" and tells a user who
    // has exported nothing that their file is ready.
    expect(normalize(sectionFor(host, 'export-heading'))).not.toContain(
      EXPORT_CONFIRMED,
    );
  });

  it('says the export is running while it runs', () => {
    // Arrange
    service.exporting.set(true);
    service.exported.set(false);

    // Act
    fixture.detectChanges();
    const section = normalize(sectionFor(host, 'export-heading'));

    // Assert
    // The two states are each other's control, which is why the negative half
    // is here rather than in a test of its own: a template that showed both
    // lines at once passes a presence-only check while telling the user the
    // file is being prepared and already exported in the same breath.
    expect(section).toContain(EXPORT_RUNNING);
    expect(section).not.toContain(EXPORT_CONFIRMED);
  });

  it('says nothing is running before an export starts', () => {
    // Assert
    // Control for the test above: a status line rendered unconditionally
    // claims a file is being prepared on a screen that has done nothing.
    expect(normalize(sectionFor(host, 'export-heading'))).not.toContain(
      EXPORT_RUNNING,
    );
  });

  // A live region is only announced if the assistive technology was watching it
  // before the text arrived; one inserted into the DOM together with its
  // content is announced by nothing. The template says so in a comment and
  // nothing else held it, so the obvious tidy-up — wrapping `.s-outcome` in an
  // `@if` so an empty div does not render — keeps every other test on this
  // screen green while silently ending every announcement the export makes.
  it('carries the export outcome region before anything has happened', () => {
    // Act
    const region = sectionFor(host, 'export-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    expect(region).not.toBeNull();
    // Both halves are load-bearing. Presence alone is satisfied by a region
    // that always renders a line of text, which is a screen telling a user who
    // has done nothing that something happened; emptiness alone is satisfied
    // by no region at all.
    expect(normalize(region ?? null)).toBe('');
  });

  it('keeps the erasure control inert', () => {
    // Act
    const eraseButton = buttonNamed(host, ERASE_BUTTON);
    const section = sectionFor(host, 'erase-heading');

    // Assert
    expect(eraseButton?.disabled).toBe(true);
    // The disabled attribute alone leaves a dead control with no account of
    // itself; the sentence is what makes the state legible.
    expect(normalize(section)).toContain(PASSKEY_EXPLANATION);
  });

  it('leaves the export control available before an export starts', () => {
    // Act
    const exportButton = buttonNamed(host, EXPORT_BUTTON);

    // Assert
    // Control for the test above: a template that disabled every button — or a
    // component that guarded the whole page behind a loading flag — passes
    // "erase is disabled" and takes export down with it.
    //
    // The accessible state is asserted alongside the DOM property because the
    // DOM property alone stops being able to fail. A button held with
    // `[disabled]` plus `[disabledInteractive]` never sets the `disabled`
    // property at all — it stays focusable and says so through
    // `aria-disabled` — so `.disabled === false` would be green on a control
    // that is unavailable for the whole life of the screen.
    expect(exportButton?.disabled).toBe(false);
    expect(exportButton?.getAttribute('aria-disabled')).not.toBe('true');
    expect(exportButton?.getAttribute('aria-busy')).not.toBe('true');
  });

  // A second click during an export is already refused by the service, but the
  // refusal is invisible: the click is swallowed and the screen answers with
  // nothing. These three pin the states that make the guard legible, and they
  // are separate tests because they can regress independently — a control can
  // announce that it is busy while still accepting the press, and one that is
  // marked unavailable can lose focus doing it.
  it('marks the export control busy while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(buttonNamed(host, EXPORT_BUTTON)?.getAttribute('aria-busy')).toBe(
      'true',
    );
  });

  it('marks the export control unavailable while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    // Busy alone says work is happening; it does not say the control will
    // refuse a press. Without this a screen reader announces a button that
    // reads as pressable and answers a press with silence.
    expect(
      buttonNamed(host, EXPORT_BUTTON)?.getAttribute('aria-disabled'),
    ).toBe('true');
  });

  it('keeps the export control focusable while an export runs', () => {
    // Arrange
    service.exporting.set(true);

    // Act
    fixture.detectChanges();
    const exportButton = buttonNamed(host, EXPORT_BUTTON);

    // Assert
    // A button that takes the DOM `disabled` property under the finger drops
    // focus to <body>, so someone who started the export from the keyboard
    // loses their place and has to tab the page from the top to hear the
    // outcome. The control has to stay in the tab order and refuse the press
    // through `aria-disabled` instead — which is what `disabledInteractive`
    // renders, and what the two tests above would otherwise be satisfied by a
    // plain `disabled` binding.
    expect(exportButton?.disabled).toBe(false);
    expect(exportButton?.getAttribute('tabindex')).not.toBe('-1');
  });

  // FR-023.
  it('states the backup window in the erasure section', () => {
    // Act
    const section = sectionFor(host, 'erase-heading');

    // Assert
    // Two controls in one assertion, and both are needed. The *full sentence*
    // is the control against a substring match: `toContain('7')` — or even
    // `toContain('7 days')` — goes green on any page that happens to carry a
    // seven, and on a sentence whose meaning has been reversed around it.
    // *Scoping to the section* is the second half: FR-023 requires the
    // statement to precede the erasure, so a sentence that drifted under
    // "Account" is no longer where the requirement puts it and must go red,
    // which a whole-page `textContent` assertion would not notice.
    expect(normalize(section)).toContain(BACKUP_WINDOW);
  });

  it('shows the account email once it loads', () => {
    // Arrange
    service.email.set('owner@budgetoid.test');

    // Act
    fixture.detectChanges();

    // Assert
    expect(emailValue(host)).toBe('owner@budgetoid.test');
  });

  it('shows nothing where the email would be before it loads', () => {
    // Arrange
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above: a hardcoded address in the template, or a
    // placeholder like "you@example.com", passes "shows the email" and never
    // reads the signal at all.
    expect(emailValue(host)).toBe('');
    // The label stays regardless — this test is about the value, not about the
    // row disappearing.
    expect(normalize(sectionFor(host, 'account-heading'))).toContain(
      EMAIL_LABEL,
    );
  });

  // An empty value and a value that failed to arrive look identical on screen
  // and are not the same fact. Without the sentence the row reads as an
  // account with no email address on it.
  it('explains an email it could not load', () => {
    // Arrange
    service.emailFailed.set(true);
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    expect(normalize(sectionFor(host, 'account-heading'))).toContain(
      EMAIL_FAILURE,
    );
  });

  // The email arrives after the first paint, and so does the sentence that says
  // it did not. Rendered outside a live region that was already being watched,
  // that sentence is announced to nobody: a screen reader user hears an empty
  // value where their address should be and is never told why.
  it('carries the account outcome region before the email resolves', () => {
    // Act
    const region = sectionFor(host, 'account-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    expect(region).not.toBeNull();
    // Same pairing as the export region: a region that always renders text
    // satisfies presence, and no region at all satisfies emptiness.
    expect(normalize(region ?? null)).toBe('');
  });

  it('announces an email it could not load', () => {
    // Arrange
    service.emailFailed.set(true);
    service.email.set(null);

    // Act
    fixture.detectChanges();
    const region = sectionFor(host, 'account-heading')?.querySelector(
      '[role="status"]',
    );

    // Assert
    // The sentence has to land *inside* the watched region, not merely
    // somewhere in the section. `explains an email it could not load` above
    // pins the copy and passes wherever the paragraph sits; this pins where it
    // sits and would go red if the region were rendered empty beside it.
    expect(normalize(region ?? null)).toContain(EMAIL_FAILURE);
  });

  it('explains nothing while the email is merely absent', () => {
    // Arrange
    service.emailFailed.set(false);
    service.email.set(null);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above: a template rendering the failure line
    // unconditionally passes "explains an email it could not load" and accuses
    // the network on every first paint, before a request has had time to
    // answer.
    expect(normalize(sectionFor(host, 'account-heading'))).not.toContain(
      EMAIL_FAILURE,
    );
  });

  it('explains a failed export build in place', () => {
    // Arrange
    service.exportFailure.set('failed');

    // Act
    fixture.detectChanges();
    // Scoped to the section the name claims: "in place" means beside the
    // control that failed. Read off the whole page, both of these stay green
    // when the outcome block drifts under Account or to the bottom of the
    // screen, which is the one thing their names promise.
    const text = normalize(sectionFor(host, 'export-heading'));

    // Assert
    // Paired with the unauthenticated case below: each is the other's control,
    // because one rendered constant cannot satisfy both. Without the negative
    // half, a template that printed every failure sentence at once — or that
    // ignored the discriminant — would pass both positives.
    expect(text).toContain(EXPORT_BUILD_FAILURE);
    expect(text).not.toContain(EXPORT_SESSION_FAILURE);
  });

  it('explains a lapsed session in place', () => {
    // Arrange
    service.exportFailure.set('unauthenticated');

    // Act
    fixture.detectChanges();
    // Same scope as the case above, for the same reason.
    const text = normalize(sectionFor(host, 'export-heading'));

    // Assert
    expect(text).toContain(EXPORT_SESSION_FAILURE);
    expect(text).not.toContain(EXPORT_BUILD_FAILURE);
  });

  // NFR-026.
  it('states what the operator can read', () => {
    // Act
    const section = sectionFor(host, 'readable-heading');

    // Assert
    // Same reasoning as the FR-023 pin: the paragraph is asserted whole
    // because a fragment — `toContain('encrypted')` — survives the softening
    // edit this requirement exists to prevent, and the scope is the section
    // because the admission belongs under its own heading rather than
    // scattered through the page.
    expect(normalize(section)).toContain(OPERATOR_READABLE);
  });

  // The section renders the reader's own calendar day, computed from the stored
  // instant in the reader's zone, and production passes no locale — it asks the
  // runtime for the reader's own, because pinning one would be the `DatePipe`
  // defect (`LOCALE_ID` is provided nowhere, so every reader would get `en-US`)
  // written by hand. The literals below are therefore statements about the
  // runner's locale as much as about the component.
  it('runs in the locale the dates below are written in', () => {
    // Act
    const rendered = credentialRegistrationDate(PASSKEY.createdAtUtc);

    // Assert
    // Not a test of the component. It turns a locale difference on some future
    // machine into one failure that says so, instead of a dozen assertions
    // that look like the section stopped rendering dates. The format itself is
    // pinned by `credential-registration-date.spec.ts`.
    expect(rendered).toBe(PASSKEY_DATE);
  });

  it('offers a section for the ways to sign in', () => {
    // Act
    const section = sectionFor(host, 'credentials-heading');

    // Assert
    expect(section).not.toBeNull();
    expect(
      normalize(section?.querySelector('#credentials-heading') ?? null),
    ).toBe(CREDENTIALS_HEADING);
  });

  it('keeps one top-level heading on the screen', () => {
    // Assert
    // Control for the test above: a section introduced with a second `h1`
    // renders the same words and passes it, while leaving the document with
    // two competing titles for a reader navigating by heading level.
    expect(host.querySelectorAll('h1').length).toBe(1);
    expect(normalize(host.querySelector('h1'))).toBe('Settings');
  });

  it('loads the ways to sign in on initialization', () => {
    // Assert
    // The list is the section's whole content; without the call it renders its
    // loading line forever and every state test below still passes, because
    // each one sets the signals itself.
    expect(service.loadCredentials).toHaveBeenCalledOnce();
  });

  it('does not load them again when the screen re-renders', () => {
    // Act
    fixture.detectChanges();
    fixture.detectChanges();

    // Assert
    // Control for the test above. `toHaveBeenCalledOnce` after a single render
    // is equally satisfied by a load started from a template expression, which
    // re-fires on every change detection pass — a request per keystroke
    // elsewhere on the page, and a list that flickers back to loading.
    expect(service.loadCredentials).toHaveBeenCalledOnce();
  });

  it('lists one row per credential', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // `ul[role="list"] > li` rather than "some elements exist": the explicit
    // role is what keeps Safari announcing "list, 2 items" once the bullets are
    // removed, and without it the rows are read as loose paragraphs.
    expect(credentialRows().length).toBe(2);
  });

  it('names a passkey in words', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // Paired with the provider case below: each is the other's control,
    // because a template printing one constant word satisfies exactly one of
    // them, and a template printing the raw `type` value renders `passkey` and
    // `federated` — the second of which is a word no reader of this screen has
    // any way to connect to the button they signed in with.
    expect(row).toContain(PASSKEY_TYPE);
    expect(row).not.toContain(FEDERATED_TYPE);
  });

  it('names a provider sign-in for the provider', () => {
    // Arrange
    service.credentials.set([FEDERATED]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    expect(row).toContain(FEDERATED_TYPE);
    expect(row).not.toContain(PASSKEY_TYPE);
  });

  it('states when each credential was registered', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    expect(normalize(rows[0] ?? null)).toContain(FEDERATED_DATE);
    expect(normalize(rows[1] ?? null)).toContain(PASSKEY_DATE);
  });

  it('states the date in words rather than as a stamp', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const row = normalize(credentialRows()[0] ?? null);

    // Assert
    // Control for the test above: `{{ credential.createdAtUtc }}` renders the
    // stored value straight into the row and passes any assertion that only
    // asks whether a date is present. The machine-readable form belongs in the
    // `datetime` attribute, which the next test reads.
    expect(row).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    expect(row).not.toContain(PASSKEY.createdAtUtc);
  });

  it('carries the stored instant on the date it renders', () => {
    // Arrange
    service.credentials.set([PASSKEY]);

    // Act
    fixture.detectChanges();
    const time = credentialRows()[0]?.querySelector('time');

    // Assert
    // The rendered day is the reader's; the attribute is the record. A `<time>`
    // wrapping a value with no `datetime` on it says nothing a plain span
    // would not.
    expect(time?.getAttribute('datetime')).toBe(PASSKEY.createdAtUtc);
    expect(normalize(time ?? null)).toBe(PASSKEY_DATE);
  });

  it('dates two entries by the day the reader had, not the UTC day', () => {
    // Arrange
    // One UTC day — the 11th — but two local days at the pinned zone: 23:00 on
    // the 11th and 01:00 on the 12th.
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-03-11T09:00:00Z' },
      { ...PASSKEY, id: 'b', createdAtUtc: '2026-03-11T11:00:00Z' },
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // An implementation formatting in UTC — `slice(0, 10)`, `getUTCDate`,
    // `toISOString` — renders these two as the same day and merges two
    // registrations the reader made on either side of their own midnight.
    expect(normalize(rows[0] ?? null)).toContain('March 11, 2026');
    expect(normalize(rows[1] ?? null)).toContain('March 12, 2026');
  });

  it('dates two entries from either side of UTC midnight as one day', () => {
    // Arrange
    // The mirror: two UTC days, one local day at the pinned zone — 12:00 and
    // 16:00 on the 12th.
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-03-11T22:00:00Z' },
      { ...PASSKEY, id: 'b', createdAtUtc: '2026-03-12T02:00:00Z' },
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // Without this half the test above is satisfied by any implementation that
    // renders two different strings for two different instants, including one
    // that prints the raw stamp.
    expect(normalize(rows[0] ?? null)).toContain('March 12, 2026');
    expect(normalize(rows[1] ?? null)).toContain('March 12, 2026');
  });

  it('tells two credentials of one type apart', () => {
    // Arrange
    service.credentials.set([
      { ...PASSKEY, id: 'a', createdAtUtc: '2026-01-12T08:30:00Z' },
      PASSKEY,
    ]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();

    // Assert
    // Two passkeys carry the same word, so the date is the only thing on the
    // row that distinguishes them — which is why the section shows one at all,
    // and why a row that dropped it would leave a person revoking blind.
    expect(normalize(rows[0] ?? null)).toContain(FEDERATED_DATE);
    expect(normalize(rows[1] ?? null)).toContain(PASSKEY_DATE);
    expect(normalize(rows[0] ?? null)).not.toBe(normalize(rows[1] ?? null));
  });

  // The date is formatted inside a `computed`, and the template reads that
  // computed, so a throw in it is a throw *during change detection*. Angular
  // abandons the pass where it fails: the credentials section is declared before
  // Export, so one unreadable field leaves the list stuck on its loading line
  // and Export silent for the rest of the visit — the button renders, the click
  // is handled, and no status, confirmation or failure sentence ever appears
  // beside it. Reloading does not help, because the same payload arrives.
  it('keeps the whole screen rendering when a date cannot be read', () => {
    // Arrange
    service.credentials.set([UNREADABLE, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // The rest of the section survives the bad entry rather than being replaced
    // by it: both rows are drawn and the readable one still states its date.
    expect(credentialRows().length).toBe(2);
    expect(normalize(credentialRows()[1] ?? null)).toContain(PASSKEY_DATE);
    // The list has arrived, so the loading line is gone — the measured symptom
    // was this line staying on screen forever.
    expect(normalize(credentialsRegion())).toBe('');
    // And the sections declared after this one still render. Asserted through
    // the export control rather than through the heading because that is the
    // half a user notices: a button that answers every press with silence.
    expect(buttonNamed(host, EXPORT_BUTTON)).not.toBeNull();
    expect(normalize(sectionFor(host, 'export-heading'))).toContain(
      EXPORT_BUTTON,
    );
  });

  it('states no date it could not read', () => {
    // Arrange
    // Two entries, the unreadable one first. Every assertion below names the row
    // it is about, and the readable sibling is what makes the negative halves
    // discriminate: on a list of one, `not.toContain('Registered')` and "no
    // `<time>` here" are equally satisfied by a section that stopped rendering
    // the date for every row on the screen.
    service.credentials.set([UNREADABLE, PASSKEY]);

    // Act
    fixture.detectChanges();
    const rows = credentialRows();
    const row = rows[0];
    const readableRow = rows[1];

    // Assert
    // Both rows are drawn, so the `?.` below are reading rows that exist rather
    // than passing on an absent one.
    expect(rows.length).toBe(2);
    // Control for the test above: surviving by printing whatever was to hand is
    // not the same as surviving. The row states the type, which the server did
    // send, and states nothing about a day nobody can work out — not the raw
    // stored value, and not the `Invalid Date` a bare `String(new Date(…))`
    // would put there.
    expect(normalize(row ?? null)).toContain(PASSKEY_TYPE);
    expect(normalize(row ?? null)).not.toContain(UNREADABLE.createdAtUtc);
    expect(normalize(row ?? null)).not.toContain('Invalid Date');
    // The caption goes with the date it introduces. `Registered` with nothing
    // after it is a sentence that stops after one word — a screen reader says it
    // exactly like that — and on screen it reads as a date that failed to arrive
    // rather than one the row is deliberately silent about.
    expect(normalize(row ?? null)).not.toContain(REGISTERED_CAPTION);
    // And the element goes with it. `<time>`'s whole contract is a
    // machine-readable instant: an empty one carrying no `datetime` is an
    // element announcing a time it does not have, which is a worse answer than
    // no element. This supersedes the narrower pair it replaces — `datetime`
    // absent and the text empty — because an empty `<time>` satisfied both while
    // still being the thing the rule forbids, and because withholding the
    // attribute is also what keeps the unreadable stored value out of the markup
    // that `shows no identifier for any credential` reads.
    expect(row?.querySelector('time')).toBeNull();
    // The sibling row proves the two negatives above are about *this* row: a
    // template that dropped the caption or the element everywhere would pass
    // them while taking the date off a list whose rows are told apart by nothing
    // else.
    expect(normalize(readableRow ?? null)).toContain(REGISTERED_CAPTION);
    expect(readableRow?.querySelector('time')).not.toBeNull();
  });

  it('names the revoke control without the clause it cannot fill', () => {
    // Arrange
    service.credentials.set([UNREADABLE]);

    // Act
    fixture.detectChanges();

    // Assert
    // The accessible name is composed from the same date, so it fails the same
    // way: `Revoke Passkey, registered ` is a sentence that stops mid-word, and
    // a screen reader reads it exactly as written.
    expect(buttonNamed(host, REVOKE_DATELESS)).not.toBeNull();
    expect(revokeButtons().length).toBe(1);
  });

  it('shows no identifier for any credential', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    // `outerHTML`, not `textContent`. Text is where an id is *least* likely to
    // arrive: the plausible route is an attribute — a `data-testid` someone adds
    // to make a test easier to write, a `title`, an `aria-label` composed from
    // the wrong field — and none of those move the section's text by a
    // character. Reading the markup covers both, and covers the `datetime`
    // attribute and the `track` expression while it is there.
    const section = sectionFor(host, 'credentials-heading')?.outerHTML ?? '';

    // Assert
    // The row shows what the server holds *and* what tells one entry from
    // another; an identifier is neither, and putting one on screen invites it
    // into a screenshot or a support message where it is a handle on the
    // account. The whole section is read, not only the rows, because the id is
    // just as exposed in a heading or a caption.
    expect(section).not.toBe('');
    expect(section).not.toContain(PASSKEY.id);
    expect(section).not.toContain(FEDERATED.id);
  });

  it('says the list is loading before it arrives', () => {
    // Act
    const region = credentialsRegion();

    // Assert
    // The section's first paint *is* this state — nothing has been set — so
    // unlike the account and export regions this one is legitimately occupied
    // here, and the emptiness assertion the other two make at first paint moves
    // to the resolved state below.
    expect(normalize(region)).toContain(CREDENTIALS_LOADING);
    expect(credentialRows().length).toBe(0);
  });

  it('claims nothing about the account while the list is loading', () => {
    // Assert
    // Control for the test above: a template treating "no rows" as "no
    // credentials" tells a user mid-load that nothing can sign them in, on a
    // page they are signed in to.
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_EMPTY,
    );
  });

  it('says nothing once the list has arrived', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const region = credentialsRegion();

    // Assert
    // Both halves, as on the other two regions: presence alone is satisfied by
    // a region that always holds a line — a screen still saying it is loading
    // over a list it has already drawn — and emptiness alone by no region at
    // all, which is a failure sentence announced to nobody.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe('');
  });

  it('explains a list it could not load', () => {
    // Arrange
    service.credentialsFailed.set(true);
    service.credentials.set(null);

    // Act
    fixture.detectChanges();
    const region = normalize(credentialsRegion());

    // Assert
    // Inside the region, not merely inside the section: the sentence arrives
    // after the first paint, and a live region the assistive technology was not
    // already watching announces nothing.
    expect(region).toContain(CREDENTIALS_FAILURE);
    // A failed load is not an account with nothing attached, and it is not
    // still loading. Rendering either would answer a question the screen does
    // not have the answer to.
    expect(region).not.toContain(CREDENTIALS_LOADING);
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_EMPTY,
    );
  });

  it('explains nothing while the list is merely absent', () => {
    // Assert
    // Control for the test above: a failure sentence rendered unconditionally
    // accuses the network on every first paint, before a request has had time
    // to answer.
    expect(normalize(sectionFor(host, 'credentials-heading'))).not.toContain(
      CREDENTIALS_FAILURE,
    );
  });

  it('states plainly when nothing is attached to the account', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // The empty case is content, not an event: it states a fact about the
    // account rather than reporting something that just happened, so it sits
    // outside the live region — which stays present and empty beside it.
    expect(normalize(sectionFor(host, 'credentials-heading'))).toContain(
      CREDENTIALS_EMPTY,
    );
    expect(normalize(credentialsRegion())).toBe('');
    expect(credentialRows().length).toBe(0);
  });

  it('does not call an empty list a loading one', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the test above, and the reason `null` and `[]` are kept
    // apart all the way from the service: a template testing `credentials()?.
    // length` collapses them and leaves an account with nothing attached
    // waiting on a request that already answered.
    expect(normalize(credentialsRegion())).not.toContain(CREDENTIALS_LOADING);
  });

  it('keeps the registration control inert', () => {
    // Act
    const registerButton = buttonNamed(host, REGISTER_BUTTON);
    const section = sectionFor(host, 'credentials-heading');

    // Assert
    // Present, so the section is honest about what it will eventually do, and
    // disabled, because registering a passkey needs a ceremony this client
    // cannot run. The sentence is what makes that state legible; the two tests
    // below hold the parts of it that this assertion cannot — how many times it
    // is said, and where.
    expect(registerButton).not.toBeNull();
    expect(registerButton?.disabled).toBe(true);
    expect(normalize(section)).toContain(CEREMONY_EXPLANATION);
  });

  it('says the ceremony explanation once, not once per row', () => {
    // Arrange
    // Two rows, because the mutation this catches is the tempting one: moving
    // the sentence beside each control it explains. With the list empty there
    // is nothing to duplicate it into and the count cannot discriminate.
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const section = normalize(sectionFor(host, 'credentials-heading'));

    // Assert
    // Exactly one, not "at least one". A sentence repeated per row is read
    // once per entry by a screen reader in browse mode and is three paragraphs
    // of the same words on an account with three credentials — which is the
    // reason it sits above the list, and which a `toContain` cannot see.
    expect(occurrencesOf(section, CEREMONY_EXPLANATION)).toBe(1);
  });

  it('says it before the controls it explains', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const section = sectionFor(host, 'credentials-heading');
    const explanation = elementSaying(section, CEREMONY_EXPLANATION);
    const firstInert = firstInertControl(section);

    // Assert
    // Order is the other half a `toContain` cannot see, and it is load-bearing
    // in both reading orders: someone moving linearly through the section meets
    // the explanation before the dead control rather than after it, and someone
    // who reaches the control first has already been told why it is off. Below
    // the buttons the sentence is an apology; above them it is an instruction.
    expect(explanation).not.toBeNull();
    expect(firstInert).not.toBeNull();
    expect(precedes(explanation, firstInert)).toBe(true);
  });

  it('hangs no description on the disabled controls', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const controls = [buttonNamed(host, REGISTER_BUTTON), ...revokeButtons()];

    // Assert
    // Control for the tests above: the explanation is legible only while it is
    // somewhere every reader meets, and a description hung on the control is
    // not that. A disabled button *is* announced — it stays in the
    // accessibility tree and browse mode reads it, with its
    // `aria-describedby` — but only to someone who arrives at it, and a
    // `title` reaches neither a keyboard nor a touch user at all. The sentence
    // has to be visible prose because it is as much for the sighted reader
    // looking at a dead button as for anyone else.
    for (const control of controls) {
      expect(control?.getAttribute('title')).toBeNull();
      expect(control?.getAttribute('aria-describedby')).toBeNull();
    }
  });

  it('keeps every revoke control inert', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const buttons = revokeButtons();

    // Assert
    expect(buttons.length).toBe(2);
    for (const button of buttons) {
      expect(button.disabled).toBe(true);
    }
  });

  it('holds every control on the screen to the touch target', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const controls = [
      buttonNamed(host, REGISTER_BUTTON),
      buttonNamed(host, EXPORT_BUTTON),
      buttonNamed(host, ERASE_BUTTON),
      ...revokeButtons(),
    ];

    // Assert
    // Every control, not only the ones this section added: the minimum is a
    // rule about controls, and a screen that holds four of five to it has a
    // control someone misses on a phone. The list is built by name so a missing
    // control fails here rather than shrinking the loop to nothing.
    expect(controls.length).toBe(5);
    for (const control of controls) {
      expect(control?.classList.contains(TOUCH_TARGET_CLASS)).toBe(true);
    }
  });

  it('gives the credential controls the outline treatment', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const controls = [buttonNamed(host, REGISTER_BUTTON), ...revokeButtons()];

    // Assert
    // Outline, and specifically *not* filled. Revoking is destructive, but the
    // destructive treatment is spent on the control that commits the act and
    // there is no confirmation behind this one yet — the book's rule for a
    // destructive action without its confirmation is Outline and disabled,
    // never Destructive. The negative half is not redundant: a control can
    // carry both classes, and it is the filled treatment arriving that makes a
    // dead button read as the section's primary action. Export is deliberately
    // filled and is deliberately not in this list.
    expect(controls.length).toBe(3);
    for (const control of controls) {
      expect(control?.classList.contains(OUTLINE_CLASS)).toBe(true);
      expect(control?.classList.contains(FILLED_CLASS)).toBe(false);
    }
  });

  it('names each revoke control for its own row', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();

    // Assert
    // Two buttons whose accessible name is "Revoke" cannot be told apart by
    // anyone driving the screen by voice or by screen reader, and this is a
    // destructive action — the one place where reaching the wrong control is
    // unrecoverable. `buttonNamed` matches the accessible name, so a shared
    // visible label with no `aria-label` fails here.
    expect(buttonNamed(host, REVOKE_PASSKEY)).not.toBeNull();
    expect(buttonNamed(host, REVOKE_FEDERATED)).not.toBeNull();
  });

  it('keeps the visible label on every revoke control', () => {
    // Arrange
    service.credentials.set([FEDERATED, PASSKEY]);

    // Act
    fixture.detectChanges();
    const buttons = revokeButtons();

    // Assert
    // Control for the test above: an accessible name that does not begin with
    // what is printed on the button — or a button printing the whole composed
    // name — breaks voice control, which matches what it can see. `Revoke` is
    // read off the DOM text here precisely because the other test reads the
    // accessible name.
    expect(buttons.length).toBe(2);
    expect(buttonNamed(host, REVOKE_PASSKEY)?.getAttribute('aria-label')).toBe(
      REVOKE_PASSKEY,
    );
  });

  it('offers no revoke control when nothing is attached', () => {
    // Arrange
    service.credentials.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    // Control for the tests above: a revoke button rendered outside the list —
    // or a row rendered for an empty list — leaves a destructive control on a
    // screen with nothing for it to act on.
    expect(revokeButtons().length).toBe(0);
  });

  // Reads the rows the way the design chapter specifies them, so a list that
  // lost its `role` or its `<li>` structure stops being found rather than
  // quietly passing every content assertion above.
  function credentialRows(): readonly HTMLElement[] {
    return Array.from(
      sectionFor(host, 'credentials-heading')?.querySelectorAll<HTMLElement>(
        'ul[role="list"] > li',
      ) ?? [],
    );
  }

  // Matched on the *visible* label, unlike `buttonNamed`: these buttons carry
  // an `aria-label` that differs per row, and the point here is to find them
  // all regardless of it.
  function revokeButtons(): readonly HTMLButtonElement[] {
    const buttons =
      sectionFor(
        host,
        'credentials-heading',
      )?.querySelectorAll<HTMLButtonElement>('button') ?? [];

    return Array.from(buttons).filter(
      (button) => normalize(button) === REVOKE_BUTTON,
    );
  }

  function credentialsRegion(): Element | null {
    return (
      sectionFor(host, 'credentials-heading')?.querySelector(
        '[role="status"]',
      ) ?? null
    );
  }
});

// A visit is not the same thing as a page load. The user exports, walks off to
// the transactions screen, and comes back: nothing about that second arrival is
// an export, and the screen must not claim one — neither a confirmation nor a
// failure the previous visit hit.
//
// These run against the **real** SettingsService with only its edges stubbed,
// because the defect is a lifetime mismatch and a stub cannot have one: the
// signal stub above is built fresh in every `beforeEach`, which is exactly the
// bug's absence. The assertions say what the second screen shows, not how the
// state is scoped, so they hold whether the fix narrows the service's lifetime
// to the component or clears the outcome when the screen initializes.
describe('SettingsComponent on a second visit', () => {
  async function configureWith(
    getExport: () => Observable<Blob>,
  ): Promise<void> {
    // Only the edges are replaced — the HTTP calls and the disk write. The
    // service under test is the shipped one.
    //
    // `getCredentials` is in the `Pick` because the screen loads it on init and
    // the real service is the one running here: a stub missing the method fails
    // every test in this block with `getCredentials is not a function` before a
    // single assertion about the export is reached. The `Pick` is over the real
    // `MeApiService`, so this list is also what stops it from drifting into a
    // shape the service no longer has.
    const api: Pick<MeApiService, 'getMe' | 'getExport' | 'getCredentials'> = {
      getMe: () => of({ email: 'owner@budgetoid.test' }),
      getExport,
      getCredentials: () => of([FEDERATED, PASSKEY]),
    };
    const downloads: Pick<FileDownloadService, 'save'> = { save: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MeApiService, useValue: api },
        { provide: FileDownloadService, useValue: downloads },
      ],
    }).compileComponents();
  }

  // One arrival at the screen. The TestBed is not reset between calls, so
  // anything the application keeps outside the component survives from one to
  // the next — which is the whole point.
  function visit(): ComponentFixture<SettingsComponent> {
    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();

    return fixture;
  }

  function exportSection(fixture: ComponentFixture<SettingsComponent>): string {
    const element = fixture.nativeElement as HTMLElement;

    return normalize(sectionFor(element, 'export-heading'));
  }

  it('does not confirm an export the previous visit finished', async () => {
    // Arrange
    await configureWith(() =>
      of(new Blob(['{"schemaVersion":1}'], { type: 'application/json' })),
    );
    const first = visit();
    buttonNamed(first.nativeElement as HTMLElement, EXPORT_BUTTON)?.click();
    first.detectChanges();
    // The first visit really did confirm. Without this line the test is green
    // on a screen that confirms nothing at all, ever.
    expect(exportSection(first)).toContain(EXPORT_CONFIRMED);
    first.destroy();

    // Act
    const second = visit();

    // Assert
    expect(exportSection(second)).not.toContain(EXPORT_CONFIRMED);
  });

  it('does not explain a failure the previous visit hit', async () => {
    // Arrange
    await configureWith(() =>
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    const first = visit();
    buttonNamed(first.nativeElement as HTMLElement, EXPORT_BUTTON)?.click();
    first.detectChanges();
    // Same control as above, in the failing direction: a screen that never
    // renders the failure sentence would otherwise pass this test by default.
    expect(exportSection(first)).toContain(EXPORT_BUILD_FAILURE);
    first.destroy();

    // Act
    const second = visit();

    // Assert
    expect(exportSection(second)).not.toContain(EXPORT_BUILD_FAILURE);
  });
});

// Collapses the whitespace an HTML template introduces. Without it every
// whole-sentence assertion above is hostage to where Prettier wrapped the
// line: the same sentence split across two source lines yields a newline and
// an indent inside `textContent`, and `toContain` fails on copy that is
// byte-correct.
function normalize(element: Element | null): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

// Finds a button the way a screen reader announces it, so a control renamed in
// the DOM but not in the copy stops being found. `aria-label` wins over the
// text node, matching how the accessible name is computed for the shapes this
// screen uses.
function buttonNamed(
  host: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  const buttons = Array.from(
    host.querySelectorAll<HTMLButtonElement>('button'),
  );

  return (
    buttons.find((button) => {
      const label = button.getAttribute('aria-label');
      return (
        (label ?? button.textContent ?? '').replace(/\s+/g, ' ').trim() === name
      );
    }) ?? null
  );
}

// How many times a sentence appears in already-normalized text. `toContain` is
// satisfied by one occurrence and by fifty, and the difference between those is
// the whole argument for where this screen puts its explanation.
function occurrencesOf(text: string, sentence: string): number {
  return text.split(sentence).length - 1;
}

// The element whose own text *is* the sentence — the paragraph carrying it,
// rather than every ancestor that contains it. Document order, so a wrapper that
// happened to hold nothing else would be found before its child; that is the
// same position for the comparison below, which is all this is used for.
function elementSaying(root: Element | null, sentence: string): Element | null {
  const elements = Array.from(root?.querySelectorAll('*') ?? []);

  return elements.find((element) => normalize(element) === sentence) ?? null;
}

// The first control in document order that a press will be refused by, however
// the refusal is expressed: `disabled` on the row buttons, `aria-disabled` on
// anything held with `disabledInteractive`.
function firstInertControl(root: Element | null): Element | null {
  return (
    root?.querySelector('button[disabled], button[aria-disabled="true"]') ??
    null
  );
}

// Document order, which is the order a screen reader reads and the order the
// page is laid out in — not source order in the template and not visual order
// under CSS, but the one both of those have to agree with to mean anything.
function precedes(first: Element | null, second: Element | null): boolean {
  if (first === null || second === null) {
    return false;
  }

  return (
    (first.compareDocumentPosition(second) &
      Node.DOCUMENT_POSITION_FOLLOWING) !==
    0
  );
}

function sectionFor(host: HTMLElement, headingId: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(
    `section[aria-labelledby="${headingId}"]`,
  );
}

// The email is rendered as a description list under the Account heading:
// `<dt>Email address</dt><dd>…</dd>`. The value is read through `dd` rather
// than through the section's whole text so that the label alone cannot satisfy
// the assertion.
function emailValue(host: HTMLElement): string {
  return normalize(
    sectionFor(host, 'account-heading')?.querySelector('dd') ?? null,
  );
}
