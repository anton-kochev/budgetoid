// The confirmation for erasing an account, and the first dialog or sheet the
// product builds. See docs/design/components.md, "Erasure dialog".
//
// **Opened here the way Settings opens it — through `MatDialog` and through
// `MatBottomSheet` — rather than mounted with `TestBed.createComponent`.** Two
// of the rules this file holds are about the *host*: Cancel, Escape and the
// backdrop do nothing while the act runs, which is the host's `disableClose`,
// and the dismiss closes whichever host it is in. A component mounted bare has
// no host, and a component that only knew about one host would pass every case
// run under that one. The host-sensitive cases therefore run under both.
//
// The flow is stubbed, and its `working` is **an independent signal**, not
// composed from `phase` — the reason `settings.component.spec.ts` gives for its
// unlock stub: the dialog must read the flow's one predicate, and a stub that
// agreed with every reassembly of it would pin nothing. `pressable` is a spy
// that reads the stub's signals, plus one knob (`refuse`) that can put it in a
// state no word-matching can explain — which is how a dialog that gated on its
// own reading of the word is told apart from one that asks the flow.
//
// **Vitest spies persist across cases** (`restoreMocks` is unset), so the stub
// is rebuilt in every `beforeEach`.
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  MatBottomSheet,
  type MatBottomSheetRef,
} from '@angular/material/bottom-sheet';
import {
  MatDialog,
  MatDialogState,
  type MatDialogRef,
} from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { EraseDialogComponent } from './erase-dialog.component';
import { confirmsErasure } from './erasure-confirmation';
import {
  ErasureFlowService,
  type ErasureFailure,
  type ErasurePhase,
} from './erasure-flow.service';

// The copy is the specification, not an example of it — every string below is
// the book's, typographic apostrophes and ellipsis included.
const TITLE = 'Erase everything';
const COMMIT = 'Erase everything';
const CANCEL = 'Cancel';
const CLOSE = 'Close';
const FIELD_LABEL = 'Type erase to confirm';
const CONSEQUENCE =
  'This erases your account and everything in it — every budget, account, category, payee and transaction. There is no undo.';
const WHAT_HAPPENS_NEXT =
  'After you press Erase everything, your device asks for your passkey, and Budgetoid checks it before erasing anything.';
// Kept only as a negative. The retention figure is decided by a value in
// another project, and a second copy of it is a second place for the number to
// drift; the section above the trigger says it once.
const BACKUP_SENTENCE_FRAGMENT = 'point-in-time database backups';

const WAITING = 'Waiting for your passkey.';
const ERASING = 'Erasing…';

// One sentence per failure word. `no-prf` and `ceremony-failed` share one by
// the book's decision — the device couldn't finish, and the person's next
// step is the same — and the guard below holds that pair equal and every other
// pair apart.
const FAILURE_SENTENCES = {
  unsupported:
    'This browser can’t check a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can — nothing was erased.',
  cancelled:
    'The passkey check was cancelled or timed out. Try again whenever you’re ready — nothing was erased.',
  'no-prf':
    'Your device couldn’t finish the passkey check. Try again, or choose another passkey — nothing was erased.',
  'ceremony-failed':
    'Your device couldn’t finish the passkey check. Try again, or choose another passkey — nothing was erased.',
  unstarted:
    'Budgetoid couldn’t reach the server to start. Try again in a minute — nothing was erased.',
  refused:
    'Budgetoid didn’t accept that passkey for this account. Try again with a passkey you made for it — nothing was erased.',
  unrecognised:
    'Budgetoid couldn’t read this request. Reload the page and try again — nothing was erased.',
  undetermined:
    'Budgetoid can’t tell whether your account was erased. Reload the page to find out.',
} as const satisfies Record<ErasureFailure, string>;

// What Material's three button appearances render as. Mutually exclusive in
// Material's own appearance map, so a missing class reads as "not that
// treatment". `mat-button` — the Ghost — is the bare `mat-mdc-button`.
const FILLED_CLASS = 'mat-mdc-unelevated-button';
const OUTLINE_CLASS = 'mat-mdc-outlined-button';
const TEXT_CLASS = 'mat-mdc-button';

type ErasureFlowSurface = Pick<ErasureFlowService, keyof ErasureFlowService>;

class ErasureFlowStub implements ErasureFlowSurface {
  public readonly phase = signal<ErasurePhase>('idle');
  public readonly failure = signal<ErasureFailure | null>(null);
  public readonly working = signal(false);
  // Puts `pressable` in a state the word cannot explain.
  public readonly refuse = signal(false);
  public readonly pressable: Mock<(typed: string) => boolean> = vi.fn(
    (typed: string) =>
      !this.refuse() &&
      confirmsErasure(typed) &&
      !this.working() &&
      this.failure() !== 'undetermined',
  );
  public readonly erase: Mock<(typed: string) => void> = vi.fn();
  // Settings calls it when it opens the overlay; the dialog never does.
  public readonly reset: Mock<() => void> = vi.fn();
}

// The two hosts, behind one shape: open, read `disableClose`, and learn whether
// the host has been asked to close.
interface HostedDialog {
  readonly disableClose: () => boolean | undefined;
  readonly closed: () => boolean;
}

interface Host {
  readonly name: string;
  readonly open: () => HostedDialog;
}

const HOSTS: readonly Host[] = [
  {
    name: 'a centred dialog',
    open: (): HostedDialog => {
      const ref: MatDialogRef<EraseDialogComponent> =
        TestBed.inject(MatDialog).open(EraseDialogComponent);
      let closed = false;
      ref.afterClosed().subscribe(() => (closed = true));

      return {
        disableClose: () => ref.disableClose,
        closed: () => closed || ref.getState() !== MatDialogState.OPEN,
      };
    },
  },
  {
    name: 'a bottom sheet',
    open: (): HostedDialog => {
      const ref: MatBottomSheetRef<EraseDialogComponent> =
        TestBed.inject(MatBottomSheet).open(EraseDialogComponent);
      let closed = false;
      ref.afterDismissed().subscribe(() => (closed = true));

      return {
        disableClose: () => ref.disableClose,
        closed: () => closed,
      };
    },
  },
];

describe('EraseDialogComponent', () => {
  let flow: ErasureFlowStub;

  beforeEach(() => {
    flow = new ErasureFlowStub();
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        // At the root, which is where a dialog opened without a view container
        // resolves from. Settings opens it with its own view container, so the
        // content finds the screen's flow; `settings.component.spec.ts` pins
        // that half.
        { provide: ErasureFlowService, useValue: flow },
      ],
    });
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
    TestBed.inject(MatBottomSheet).dismiss();
  });

  describe('opened in a centred dialog', () => {
    beforeEach(() => {
      HOSTS[0]?.open();
      render();
    });

    it('draws the commit Destructive and the dismiss Ghost', () => {
      // Act
      const commit = commitButton();
      const cancel = buttonNamed(pane(), CANCEL);

      // Assert
      // The fill is spent once on this path, on the press that destroys
      // something. The dismiss is Ghost per Dialogs and sheets: a dismiss
      // drawn Outline beside a filled commit reads as a second option of
      // equal weight.
      expect(commit?.classList.contains(FILLED_CLASS)).toBe(true);
      expect(commit?.classList.contains(OUTLINE_CLASS)).toBe(false);
      expect(cancel).not.toBeNull();
      expect(cancel?.classList.contains(TEXT_CLASS)).toBe(true);
      expect(cancel?.classList.contains(FILLED_CLASS)).toBe(false);
      expect(cancel?.classList.contains(OUTLINE_CLASS)).toBe(false);
    });

    it('fills the commit with the over colour rather than the primary one', () => {
      // Arrange
      // Destructive and Primary are both `mat-flat-button`, and the only thing
      // telling them apart is the fill — so a commit carrying the filled class
      // with no token override is a Primary button on the most destructive
      // control in the product. Read off the rendered element, so the
      // declaration has to reach *this* button: jsdom does not resolve the
      // `var()`, but it does cascade the component's own stylesheet onto the
      // element it matches, which a rule moved onto the wrong selector fails.
      // The declaration is the one `key-rotation-section.component.scss`
      // makes for the same variant.
      const commit = commitButton();

      // Act
      const fill =
        commit === null
          ? ''
          : getComputedStyle(commit)
              .getPropertyValue('--mat-button-filled-container-color')
              .trim();

      // Assert
      expect(commit).not.toBeNull();
      expect(fill).toBe('var(--bud-over)');
    });

    it('says what erasing costs, and then what happens next', () => {
      // Act
      const text = normalize(pane());
      const heading = pane().querySelector('h1, h2, h3, [role="heading"]');

      // Assert
      // The title, the section heading, the trigger and the commit say the
      // same two words, so the act has one name wherever it is met.
      expect(normalize(heading)).toBe(TITLE);
      expect(text).toContain(CONSEQUENCE);
      expect(text).toContain(WHAT_HAPPENS_NEXT);
      // The backup window is said once, on the screen, above the trigger.
      expect(text).not.toContain(BACKUP_SENTENCE_FRAGMENT);
    });

    it('keeps the consequence out of the field’s label and the commit’s', () => {
      // Act
      const label = accessibleLabel(field());

      // Assert
      // A label is read every time focus lands on its control, and a
      // consequence heard that way is noise a reader learns to skip.
      expect(label).toBe(FIELD_LABEL);
      expect(normalize(commitButton())).toBe(COMMIT);
    });

    it('asks for the word in a field that fights nothing a phone does', () => {
      // Act
      const input = field();

      // Assert
      // A keyboard that capitalises the first letter or offers a correction is
      // fighting the one word the field wants.
      expect(input?.value).toBe('');
      expect(input?.getAttribute('autocomplete')).toBe('off');
      expect(input?.getAttribute('autocapitalize')).toBe('off');
      expect(input?.getAttribute('spellcheck')).toBe('false');
    });

    it('carries an empty status region from the moment it opens', () => {
      // Act
      const region = statusRegion();

      // Assert
      // Present and empty, both halves: a region created together with its
      // first line is announced by nothing, and one that always holds a line
      // reports an event nobody caused. Polite, never assertive — the person
      // is looking at the press that produced every line.
      expect(region).not.toBeNull();
      expect(normalize(region)).toBe('');
      expect(pane().querySelector('[role="alert"]')).toBeNull();
      expect(pane().querySelector('[aria-live="assertive"]')).toBeNull();
    });

    it('holds the commit inert but in the tab order until the word matches', () => {
      // Arrange
      const commit = commitButton();

      // Assert (at rest)
      // `disabledInteractive`: the press has an answer one tab stop away, so a
      // control that left the tab order would hide the gate from the only
      // person who can open it.
      expect(commit?.getAttribute('aria-disabled')).toBe('true');
      expect(commit?.disabled).toBe(false);
      expect(commit?.getAttribute('tabindex')).not.toBe('-1');

      // Act
      type('eras');

      // Assert
      expect(commitButton()?.getAttribute('aria-disabled')).toBe('true');

      // Act
      type(' Erase ');

      // Assert
      expect(commitButton()?.getAttribute('aria-disabled')).not.toBe('true');
      expect(flow.pressable).toHaveBeenCalledWith(' Erase ');
    });

    it('names the field’s label from the inert commit', () => {
      // Act
      const described = describedText(commitButton());

      // Assert
      // Somebody who tabs past the field onto the commit hears what opens it.
      // It adds no visible sentence: the label is on screen already.
      expect(described).toContain(FIELD_LABEL);
    });

    it('stops naming the field’s label once the word opens the commit', () => {
      // Act
      type('erase');
      const commit = commitButton();

      // Assert
      // A live commit described by *Type erase to confirm* tells somebody who
      // has already typed it that something is still missing.
      expect(commit?.getAttribute('aria-disabled')).not.toBe('true');
      expect(commit?.getAttribute('aria-describedby')).toBeNull();
    });

    it('holds the commit on the flow’s predicate, not on its own reading of the word', () => {
      // Arrange
      type('erase');
      expect(commitButton()?.getAttribute('aria-disabled')).not.toBe('true');

      // Act
      // A state the word cannot explain: the word matches and the flow still
      // says no.
      flow.refuse.set(true);
      render();
      commitButton()?.click();
      render();

      // Assert
      // A dialog that computed its own gate from `confirmsErasure` would stay
      // live here and hand the press on. One predicate with one owner.
      expect(commitButton()?.getAttribute('aria-disabled')).toBe('true');
      expect(flow.erase).not.toHaveBeenCalled();
    });

    it('refuses a press the word does not open, in the handler, and sends focus to the field', () => {
      // Arrange
      type('eras');

      // Act
      // The click still arrives: Material's click-halt is applied to anchors
      // only, so on a `<button>` the attribute alone refuses nothing.
      commitButton()?.click();
      render();

      // Assert
      expect(flow.erase).not.toHaveBeenCalled();
      expect(document.activeElement).toBe(field());
    });

    it('hands the flow the word exactly as typed', () => {
      // Arrange
      type(' Erase ');

      // Act
      commitButton()?.click();
      render();

      // Assert
      // The flow re-checks the same predicate, so it has to be given what the
      // field holds rather than a word this dialog normalised on its behalf.
      expect(flow.erase).toHaveBeenCalledTimes(1);
      expect(flow.erase).toHaveBeenCalledWith(' Erase ');
    });

    it('moves nothing when a press is refused while the act runs', () => {
      // Arrange
      type('erase');
      flow.phase.set('asserting');
      flow.working.set(true);
      render();
      const commit = commitButton();
      commit?.focus();

      // Act
      commit?.click();
      render();

      // Assert
      // Focus stays on the commit, where the next attempt starts. Sent to the
      // field instead, a keyboard user mid-ceremony is thrown back to a word
      // they already typed, and a screen reader re-announces its label over
      // *Waiting for your passkey*.
      expect(flow.erase).not.toHaveBeenCalled();
      expect(commit).not.toBeNull();
      expect(document.activeElement).toBe(commit);
    });

    it.each([
      ['asserting', WAITING],
      ['erasing', ERASING],
      // Terminal, and it keeps the erasing line rather than emptying the
      // region: the tab is on its way to Welcome, which says *Erased.*, and a
      // region that went blank in between would announce nothing true.
      ['erased', ERASING],
    ] satisfies readonly (readonly [ErasurePhase, string])[])(
      'says what is happening while %s',
      (phase, line) => {
        // Act
        flow.phase.set(phase);
        flow.working.set(true);
        render();

        // Assert
        expect(normalize(statusRegion())).toBe(line);
      },
    );

    it('keeps the commit in place, busy and named the same, while the act runs', () => {
      // Arrange
      type('erase');

      // Act
      flow.phase.set('erasing');
      flow.working.set(true);
      render();
      const commit = commitButton();

      // Assert
      // Its label does not change — the region says what is happening.
      expect(commit).not.toBeNull();
      expect(commit?.getAttribute('aria-disabled')).toBe('true');
      expect(commit?.getAttribute('aria-busy')).toBe('true');
      expect(commit?.disabled).toBe(false);
    });

    it('holds Cancel inert without calling it busy while the act runs', () => {
      // Act
      flow.phase.set('asserting');
      flow.working.set(true);
      render();
      const cancel = buttonNamed(pane(), CANCEL);

      // Assert
      // `disabledInteractive` for the busy reason, and no `aria-busy`: it is
      // doing no work itself.
      expect(cancel?.getAttribute('aria-disabled')).toBe('true');
      expect(cancel?.disabled).toBe(false);
      expect(cancel?.getAttribute('aria-busy')).toBeNull();
    });

    it.each(Object.entries(FAILURE_SENTENCES) as [ErasureFailure, string][])(
      'says %s in its own words',
      (failure, sentence) => {
        // Act
        flow.failure.set(failure);
        render();

        // Assert
        expect(normalize(statusRegion())).toBe(sentence);
      },
    );

    it('keeps the failure sentences apart, except the one pair the book joins', () => {
      // Arrange
      // The guard that makes the case above able to fail on a pasted
      // sentence. Compared as substrings in both directions: a sentence that
      // contains another is the same defect with extra words on the end.
      const entries = Object.entries(FAILURE_SENTENCES) as [
        ErasureFailure,
        string,
      ][];
      const joined: readonly ErasureFailure[] = ['no-prf', 'ceremony-failed'];

      // Assert
      for (const [word, sentence] of entries) {
        for (const [otherWord, other] of entries) {
          if (word === otherWord) {
            continue;
          }

          const bothJoined =
            joined.includes(word) && joined.includes(otherWord);

          expect(
            sentence.includes(other),
            bothJoined
              ? `${word} and ${otherWord} should share one sentence.`
              : `${word} and ${otherWord} say the same thing.`,
          ).toBe(bothJoined);
        }
      }

      // And every line but one answers the question somebody who pressed the
      // most destructive control in the product asks first.
      for (const [word, sentence] of entries) {
        expect(sentence.endsWith('nothing was erased.')).toBe(
          word !== 'undetermined',
        );
      }
    });

    it('withdraws the commit and offers Close once it cannot tell what happened', async () => {
      // Arrange
      type('erase');

      // Act
      flow.failure.set('undetermined');
      render();

      // Assert
      // Out of the DOM rather than disabled: a control that will never be
      // enabled again makes a promise it cannot keep. *Cancel* promises that
      // nothing happened, which is the one thing this state cannot say.
      expect(commitButton()).toBeNull();
      expect(buttonNamed(pane(), CANCEL)).toBeNull();
      const close = buttonNamed(pane(), CLOSE);
      expect(close).not.toBeNull();
      // Focus moves to the dismiss, because the control it stood on has left.
      await eventually(
        () => (document.activeElement === close ? true : null),
        'focus to land on Close',
      );
    });
  });

  describe.each(HOSTS)('opened in $name', (host) => {
    let hosted: HostedDialog;

    beforeEach(() => {
      hosted = host.open();
      render();
    });

    it('lets Cancel close it at rest, posting nothing', async () => {
      // Act
      buttonNamed(pane(), CANCEL)?.click();
      render();

      // Assert
      await eventually(() => (hosted.closed() ? true : null), 'the close');
      expect(flow.erase).not.toHaveBeenCalled();
    });

    it('leaves the host free to close at rest', () => {
      // Assert
      // Escape and the backdrop close an idle confirmation, which is
      // Material's default and the person's expectation.
      expect(hosted.disableClose()).not.toBe(true);
    });

    it('holds the host open while the act runs, and lets go when it ends', () => {
      // Act
      flow.phase.set('asserting');
      flow.working.set(true);
      render();

      // Assert
      // Waiting for the passkey, a closed overlay leaves a system sheet up with
      // nothing on screen to receive its answer — and an answer that arrives
      // erases the account from a dialog nobody can see.
      expect(hosted.disableClose()).toBe(true);

      // Act
      flow.phase.set('idle');
      flow.working.set(false);
      flow.failure.set('cancelled');
      render();

      // Assert
      expect(hosted.disableClose()).not.toBe(true);
    });

    it('ignores Cancel and Escape while the act runs', async () => {
      // Arrange
      flow.phase.set('erasing');
      flow.working.set(true);
      render();

      // Act
      buttonNamed(pane(), CANCEL)?.click();
      document.body.dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'Escape',
          code: 'Escape',
          keyCode: 27,
          bubbles: true,
        }),
      );
      render();
      await settle();

      // Assert
      // Erasing, the request is already out and nothing can recall it;
      // closing would only hide the one sentence that matters.
      expect(hosted.closed()).toBe(false);
      expect(pane()).not.toBeNull();
    });

    it('lets Close end it once it cannot tell what happened', async () => {
      // Arrange
      flow.failure.set('undetermined');
      render();

      // Act
      buttonNamed(pane(), CLOSE)?.click();
      render();

      // Assert
      await eventually(() => (hosted.closed() ? true : null), 'the close');
    });
  });

  function render(): void {
    TestBed.tick();
  }

  async function settle(): Promise<void> {
    for (let turn = 0; turn < 5; turn += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    render();
  }

  // The overlay pane holding the content. A failure that names the mistake
  // rather than a property read off `null` three lines later.
  function pane(): HTMLElement {
    const found = document.querySelector<HTMLElement>('.cdk-overlay-pane');

    if (found === null) {
      throw new Error('No overlay is open.');
    }

    return found;
  }

  function commitButton(): HTMLButtonElement | null {
    return buttonNamed(pane(), COMMIT);
  }

  function field(): HTMLInputElement | null {
    return pane().querySelector<HTMLInputElement>('input');
  }

  function statusRegion(): Element | null {
    return pane().querySelector('[role="status"]');
  }

  function type(text: string): void {
    const input = field();

    if (input === null) {
      throw new Error('The dialog has no field to type into.');
    }

    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    render();
  }
});

function normalize(element: Element | null): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function buttonNamed(
  root: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  const buttons = Array.from(
    root.querySelectorAll<HTMLButtonElement>('button'),
  );

  return (
    buttons.find(
      (button) =>
        (button.getAttribute('aria-label') ?? normalize(button)) === name,
    ) ?? null
  );
}

// The text of every element an IDREF list names, joined — how `aria-labelledby`
// and `aria-describedby` are read out.
function textOfIds(ids: string | null): string {
  return (ids ?? '')
    .split(/\s+/)
    .filter((id) => id !== '')
    .map((id) => normalize(document.getElementById(id)))
    .join(' ')
    .trim();
}

// The field's programmatic label, by the three routes a label can take.
// Material's `mat-label` renders a `<label for>`, which `labels` reads.
function accessibleLabel(input: HTMLInputElement | null): string {
  if (input === null) {
    return '';
  }

  const labelledBy = textOfIds(input.getAttribute('aria-labelledby'));

  if (labelledBy !== '') {
    return labelledBy;
  }

  const ariaLabel = input.getAttribute('aria-label');

  if (ariaLabel !== null) {
    return ariaLabel.trim();
  }

  return Array.from(input.labels ?? [])
    .map((label) => normalize(label))
    .join(' ')
    .trim();
}

function describedText(element: Element | null): string {
  return textOfIds(element?.getAttribute('aria-describedby') ?? null);
}

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
    TestBed.tick();
  }

  throw new Error(`Timed out waiting for ${what}.`);
}
