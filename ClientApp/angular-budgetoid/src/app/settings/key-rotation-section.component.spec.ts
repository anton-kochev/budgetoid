// The section that gives a rotation a way in, and the first control in the book
// to take the Destructive fill without deleting anything.
//
// Both collaborators are stubbed, and for the same reason the settings screen's
// own file stubs custody: the real `KeyRotationService` is `providedIn: 'root'`
// and reaches eight API services, each of which extends `BaseApiService` and
// injects a `ConfigurationService` nothing here provides — so the component dies
// at construction before one assertion is reached. Both stubs `implement` a
// surface derived from the real class with `Pick<S, keyof S>`, which is the
// compiler's own census of what a template can reach: a member added to either
// service is a compile error naming it, rather than a `TypeError` during change
// detection that kills every test in the file on one message.
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import {
  KeyRotationService,
  type KeyRotationFailure,
  type KeyRotationPhase,
  type KeyRotationProgress,
  type StagedRotation,
} from '@app-core/security/key-rotation.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KeyRotationSectionComponent } from './key-rotation-section.component';
import {
  RotationFlowService,
  type RotationCeremonyFailure,
} from './rotation-flow.service';

// **The copy is the specification, not an example of it**, so every sentence
// this section says is pinned here as the exact string
// `docs/design/components.md` carries — typographic apostrophes, em dashes and
// ellipsis included. A table spelling `’` as `'` specifies a string no screen
// renders and nothing catches it.
const STANDING_PROSE =
  'Rotating gives your account new keys and re-encrypts every name and note ' +
  'under them. Every passkey and recovery code you have keeps working — the ' +
  'old keys stop opening anything.';

const CONSEQUENCE =
  'This rewrites every record in the account, and nothing can put the old ' +
  'keys back. If this tab closes part-way through, the rotation stops where ' +
  'it is and this section offers to finish it — a rotation picked up again ' +
  'starts over from the first record.';

const ACKNOWLEDGEMENT = 'I’ll leave this tab open until it finishes.';

const ROTATE = 'Rotate keys';
const FINISH = 'Finish rotating';

const PHASES = {
  collecting: 'Reading your records.',
  resealing: 'Re-encrypting your records.',
  finishing: 'Finishing.',
} as const satisfies Partial<Record<KeyRotationPhase, string>>;

// The run's six, each saying what became of the *run* — which is what separates
// them from the Account keys section's lines, which have no run to say anything
// about.
const RUN_REFUSALS = {
  unreachable:
    'Budgetoid couldn’t reach the server. The rotation stopped where it is — ' +
    'try again in a minute and it picks up from there.',
  unauthenticated:
    'Budgetoid stopped accepting this rotation from this browser. Sign out ' +
    'and sign in again, then finish it from here.',
  unrecognised:
    'Budgetoid couldn’t work with what the server sent back. Reload the page ' +
    '— that’s the one thing here that can change the answer.',
  inconsistent:
    'Something about this account’s keys doesn’t line up — no passkey or ' +
    'recovery code will change it.',
  unfinished:
    'Something else changed this account while it was being re-encrypted. ' +
    'Close any other Budgetoid tab, then finish the rotation from here.',
  'factors-moved':
    'The passkeys and recovery codes on this account changed while the ' +
    'rotation was running. Start it again from here — the records already ' +
    're-encrypted stay that way.',
} as const satisfies Record<KeyRotationFailure, string>;

// The ceremony's five, from the Account keys chapter's table — four verbatim,
// and `unknown` with its act renamed, because it is the only one of the five
// that names one.
// The borrowing is honest because of a constraint on the flow rather than a
// judgement about the words: the ceremony is the first thing either press does
// and nothing is posted until it answers, so *Nothing has changed.* is true on a
// begin and on a resume alike.
const CEREMONY_REFUSALS = {
  unsupported:
    'This browser can’t check a passkey. Open Budgetoid in a different ' +
    'browser, or on a phone or laptop that can.',
  cancelled:
    'The passkey check was cancelled. Nothing has changed — try again ' +
    'whenever you’re ready.',
  'no-prf':
    'This device can’t open your account’s keys. Try the device that holds ' +
    'the passkey you made this account with.',
  'ceremony-failed':
    'Your device didn’t finish the passkey check. Nothing has changed.',
  unknown:
    'Budgetoid couldn’t finish rotating your keys. Nothing has changed — try ' +
    'again.',
} as const satisfies Record<RotationCeremonyFailure, string>;

type KeyRotationSurface = Pick<KeyRotationService, keyof KeyRotationService>;

class KeyRotationStub implements KeyRotationSurface {
  public readonly phase = signal<KeyRotationPhase>('idle');
  public readonly progress = signal<KeyRotationProgress>({
    resealed: 0,
    records: 0,
  });
  public readonly failure = signal<KeyRotationFailure | null>(null);
  public readonly staged = signal<StagedRotation | null>(null);
  public readonly running = signal(false);
  public begin = vi.fn(async () => Promise.resolve());
  public resume = vi.fn(async () => Promise.resolve());
  public readStagedRotation = vi.fn(async () => Promise.resolve());
}

type RotationFlowSurface = Pick<RotationFlowService, keyof RotationFlowService>;

// **`working` is an independent signal and deliberately not composed out of the
// two facts behind it.** It is the one predicate the control's `disabled`, its
// `aria-busy` and the guard all read; a stub deriving it would agree with any
// reassembly a template made for itself and would pin nothing. Driven by hand it
// can be put in a state the flow itself cannot reach, and there a second
// spelling parts company with the first.
class RotationFlowStub implements RotationFlowSurface {
  public readonly busy = signal(false);
  public readonly failure = signal<RotationCeremonyFailure | null>(null);
  public readonly working = signal(false);
  public rotate = vi.fn();
}

// Collapses the whitespace a template's line wrapping introduces, so a pinned
// sentence is compared against what a reader sees rather than against the
// markup's indentation.
function textOf(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function buttonNamed(host: HTMLElement, label: string): HTMLElement | null {
  return (
    Array.from(host.querySelectorAll('button')).find(
      (button) => textOf(button) === label,
    ) ?? null
  );
}

describe('KeyRotationSectionComponent', () => {
  let rotations: KeyRotationStub;
  let flow: RotationFlowStub;
  let fixture: ComponentFixture<KeyRotationSectionComponent>;
  let host: HTMLElement;

  const region = (): HTMLElement | null =>
    host.querySelector('[role="status"]');
  const bar = (): HTMLElement | null =>
    host.querySelector('[role="progressbar"]');
  const checkbox = (): HTMLInputElement | null =>
    host.querySelector('input[type="checkbox"]');
  const control = (): HTMLElement | null =>
    buttonNamed(host, ROTATE) ?? buttonNamed(host, FINISH);

  const tick = (): void => {
    fixture.detectChanges();
  };

  const acknowledge = (): void => {
    checkbox()?.click();
    tick();
  };

  beforeEach(async () => {
    rotations = new KeyRotationStub();
    flow = new RotationFlowStub();
    TestBed.configureTestingModule({
      imports: [KeyRotationSectionComponent],
      providers: [
        provideNoopAnimations(),
        { provide: KeyRotationService, useValue: rotations },
        { provide: RotationFlowService, useValue: flow },
      ],
    });
    await TestBed.compileComponents();
    fixture = TestBed.createComponent(KeyRotationSectionComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  it('asks whether there is a run to finish before it draws a control', () => {
    // Assert
    // A rotation that was interrupted survives only as server state — a staging
    // row and one seal per factor, and nothing in the browser — so the section
    // cannot know which of two controls to offer without asking.
    expect(rotations.readStagedRotation).toHaveBeenCalled();
  });

  it('offers the rotation control under both blocks of standing prose', () => {
    // Assert
    const prose = Array.from(host.querySelectorAll('p')).map(textOf);

    expect(prose).toContain(STANDING_PROSE);
    expect(prose).toContain(CONSEQUENCE);
    expect(buttonNamed(host, ROTATE)).not.toBeNull();
  });

  it('keeps the consequence out of the acknowledgement’s label', () => {
    // Assert
    // A label is read every time focus lands on the control and re-read by every
    // announcement of its state, so a four-sentence label puts four sentences
    // between a keyboard user and knowing whether the box is ticked. The
    // consequence is its own block, above.
    const label = textOf(host.querySelector('mat-checkbox'));

    expect(label).toBe(ACKNOWLEDGEMENT);
    expect(label).not.toContain('rewrites every record');
  });

  it('arrives unticked, including over a run there is already to finish', () => {
    // Arrange
    rotations.staged.set({ startedAtUtc: '2026-07-14T09:30:00Z' });
    tick();

    // Assert
    // A box that arrives ticked acknowledges nothing, and a resumed run
    // rewrites the account exactly as the first press did.
    expect(checkbox()?.checked).toBe(false);
    expect(control()?.getAttribute('aria-disabled')).toBe('true');
  });

  it('refuses a press made while the box is unticked', () => {
    // Arrange
    // The attribute is the half that does not hold: Material's click-halt is
    // installed on anchors only, so on a `<button>` the DOM `disabled` property
    // stays `false` and the click reaches the component whatever the attribute
    // says.

    // Act
    buttonNamed(host, ROTATE)?.click();

    // Assert
    // A run begun by somebody who acknowledged nothing writes to every row they
    // own.
    expect(flow.rotate).not.toHaveBeenCalled();
  });

  it('runs a rotation once the acknowledgement is given', () => {
    // Arrange
    acknowledge();

    // Act
    buttonNamed(host, ROTATE)?.click();

    // Assert
    expect(flow.rotate).toHaveBeenCalledTimes(1);
    expect(control()?.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('refuses a second press while a run is in flight', () => {
    // Arrange
    acknowledge();
    flow.working.set(true);
    tick();

    // Act
    buttonNamed(host, ROTATE)?.click();

    // Assert
    // Two concurrent presses would fight over the two generations the driver
    // holds for the length of a run.
    expect(flow.rotate).not.toHaveBeenCalled();
  });

  it('holds the control busy on the reading the flow publishes', () => {
    // Arrange
    acknowledge();

    // Act
    flow.working.set(true);
    tick();

    // Assert
    // One predicate with one owner: the attribute, the busy state and the guard
    // read the same signal, so a template assembling its own answer out of the
    // phase would be the drift the Unlock control already paid for once.
    expect(control()?.getAttribute('aria-disabled')).toBe('true');
    expect(control()?.getAttribute('aria-busy')).toBe('true');
  });

  it('asserts nothing about work at rest', () => {
    // Assert
    // `aria-busy` resolves to `null` rather than to `'false'`, so the attribute
    // is absent instead of stating that no work is happening.
    expect(control()?.hasAttribute('aria-busy')).toBe(false);
  });

  it('offers Rotate keys when there is no run, and never both controls', () => {
    // Assert
    expect(buttonNamed(host, ROTATE)).not.toBeNull();
    expect(buttonNamed(host, FINISH)).toBeNull();
  });

  it('offers Finish rotating over a staged run, with the date it began', () => {
    // Arrange
    // 22:00 UTC, which is the *next* day in this runner's pinned zone. The
    // section states the reader's own calendar day, never the UTC one — they are
    // different days for fourteen hours out of every twenty-four at UTC+14, and
    // an implementation reading the UTC day looks right to whoever wrote it.
    rotations.staged.set({ startedAtUtc: '2026-07-13T22:00:00Z' });

    // Act
    tick();

    // Assert
    // Two controls would ask a person to choose between starting over and
    // continuing, which is a choice with a wrong answer.
    expect(buttonNamed(host, FINISH)).not.toBeNull();
    expect(buttonNamed(host, ROTATE)).toBeNull();
    expect(textOf(host.querySelector('.k-started'))).toBe('July 14, 2026');
  });

  it('draws no bar while a run is still reading records', () => {
    // Arrange
    rotations.phase.set('collecting');

    // Act
    tick();

    // Assert
    // A determinate bar sitting at zero while five list reads run says that
    // nothing is happening. The phase word says what is.
    expect(textOf(region())).toBe(PHASES.collecting);
    expect(bar()).toBeNull();
  });

  it('draws a determinate line from what the server has accepted', () => {
    // Arrange
    rotations.phase.set('resealing');
    rotations.progress.set({ resealed: 40, records: 160 });

    // Act
    tick();

    // Assert
    // The numerator counts rows carried by a chunk the server answered 204 —
    // never rows collected, never rows sealed, never rows queued.
    expect(textOf(region())).toBe(PHASES.resealing);
    expect(bar()?.getAttribute('aria-valuenow')).toBe('25');
    expect(bar()?.getAttribute('aria-valuemax')).toBe('100');
    expect(bar()?.getAttribute('aria-label')).not.toBeNull();
    expect(textOf(host.querySelector('.k-count'))).toBe('40 of 160 records');
  });

  it('leaves the line where it stands while the run finishes', () => {
    // Arrange
    rotations.phase.set('finishing');
    rotations.progress.set({ resealed: 160, records: 160 });

    // Act
    tick();

    // Assert
    expect(textOf(region())).toBe(PHASES.finishing);
    expect(bar()?.getAttribute('aria-valuenow')).toBe('100');
  });

  it('keeps the bar out of the live region', () => {
    // Arrange
    rotations.phase.set('resealing');
    rotations.progress.set({ resealed: 1, records: 400 });

    // Act
    tick();

    // Assert
    // A determinate bar whose value moves once per accepted chunk, inside a live
    // region, narrates a number several hundred times over one run. The region
    // holds the phase sentence, which changes three times.
    expect(bar()).not.toBeNull();
    expect(region()?.contains(bar())).toBe(false);
  });

  it('is polite and never assertive', () => {
    // Assert
    // The person asked for this, and the carve-out for `alert` is for a failure
    // that lands after attention has moved on.
    expect(region()?.getAttribute('role')).toBe('status');
    expect(host.querySelector('[role="alert"]')).toBeNull();
  });

  it.each(
    Object.entries(RUN_REFUSALS) as readonly (readonly [
      KeyRotationFailure,
      string,
    ])[],
  )('says what became of the run when it stopped on %s', (word, sentence) => {
    // Arrange
    rotations.failure.set(word);

    // Act
    tick();

    // Assert
    expect(textOf(region())).toBe(sentence);
  });

  it.each(
    Object.entries(CEREMONY_REFUSALS) as readonly (readonly [
      RotationCeremonyFailure,
      string,
    ])[],
  )('says what became of a press when it stopped on %s', (word, sentence) => {
    // Arrange
    flow.failure.set(word);

    // Act
    tick();

    // Assert
    expect(textOf(region())).toBe(sentence);
  });

  it('says one thing when a refused ceremony follows a run that stopped', () => {
    // Arrange
    // Both are readable at once and the state is ordinary rather than
    // contrived: a run stops on `unreachable`, then the retry is cancelled at
    // the system sheet — which never reaches the driver, so its word from the
    // previous press is still standing.
    rotations.failure.set('unreachable');
    flow.failure.set('cancelled');

    // Act
    tick();

    // Assert
    // Rendered together the section gives two answers to one question and marks
    // neither as the older.
    expect(textOf(region())).toBe(CEREMONY_REFUSALS.cancelled);
  });

  it('says nothing at rest and still holds the region open', () => {
    // Assert
    // In the DOM from first paint and empty at rest: a live region inserted
    // together with its text is announced by nothing.
    expect(region()).not.toBeNull();
    expect(textOf(region())).toBe('');
    expect(bar()).toBeNull();
  });

  it('puts the section under one labelled heading at level two', () => {
    // Assert
    const section = host.querySelector('section');
    const heading = host.querySelector('h2');

    expect(heading?.textContent?.trim()).toBe('Key rotation');
    expect(section?.getAttribute('aria-labelledby')).toBe(heading?.id);
  });
});
