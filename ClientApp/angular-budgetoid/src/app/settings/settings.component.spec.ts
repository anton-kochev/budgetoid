import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { beforeEach, describe, expect, it, vi } from 'vitest';
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

// Real signals, not readonly wrappers: each test drives one state by setting
// them, so the component is exercised through its inputs rather than through
// the network the service would otherwise reach for.
class SettingsServiceStub {
  public readonly email = signal<string | null>(null);
  public readonly emailFailed = signal(false);
  public readonly exporting = signal(false);
  public readonly exported = signal(false);
  public readonly exportFailure = signal<ExportFailure | null>(null);
  public loadEmail = vi.fn();
  public export = vi.fn();
}

describe('SettingsComponent', () => {
  let service: SettingsServiceStub;
  let fixture: ComponentFixture<SettingsComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    service = new SettingsServiceStub();
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SettingsService, useValue: service },
      ],
    }).compileComponents();
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

  it('leaves the export control enabled', () => {
    // Assert
    // Control for the test above: a template that disabled every button — or a
    // component that guarded the whole page behind a loading flag — passes
    // "erase is disabled" and takes export down with it.
    expect(buttonNamed(host, EXPORT_BUTTON)?.disabled).toBe(false);
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
    const text = normalize(host);

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
    const text = normalize(host);

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
