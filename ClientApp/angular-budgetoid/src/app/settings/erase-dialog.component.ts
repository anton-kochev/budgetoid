import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import {
  ErasureFlowService,
  type ErasureFailure,
} from './erasure-flow.service';

/**
 * The ids the overlay is named and described by: the title, and the
 * consequence block. The opener hands them to the host's config, whose
 * container binds `aria-labelledby` and `aria-describedby` from them, so the
 * cost is announced with the name on open. Fixed rather than drawn, because the
 * config is written before this component exists — one erasure dialog is open
 * at a time, and nothing else on the screen underneath uses either.
 */
export const ERASE_DIALOG_TITLE_ID = 'erase-dialog-title';
export const ERASE_DIALOG_CONSEQUENCE_ID = 'erase-dialog-consequence';

/**
 * The ids of the two controls the opener may send focus to: the field, and the
 * dismiss. The opener hands one to the host's `autoFocus` as a selector — the
 * field for an ordinary dialog, the dismiss for one opened withdrawn — so focus
 * moves once, straight there, and the host restores it to the trigger on close.
 * Fixed for the reason the two above are.
 */
export const ERASE_DIALOG_FIELD_ID = 'erase-dialog-field';
export const ERASE_DIALOG_DISMISS_ID = 'erase-dialog-dismiss';

// The field's cap. **Not a narrative cap** — the word is never stored or sent,
// so no column's byte limit stands behind it — but every text control in this
// application states one (`narrative-field-caps.spec.ts`), and here it bounds a
// paste rather than a person. Room for the word with a phone keyboard's padding
// either side and then some: the match trims, and a cap at the word's own
// length would truncate ` Erase ` into a word that no longer matches.
const CONFIRMATION_FIELD_CHARACTERS = 32;

interface StatusLine {
  readonly text: string;
  readonly refusal: boolean;
}

// The confirmation for erasing an account, and the first overlay the product
// builds. See docs/design/components.md, "Erasure dialog" and "Dialogs and
// sheets".
//
// **Content only, and host-agnostic.** Settings chooses the host — a centred
// `MatDialog` on expanded widths, a `MatBottomSheet` on compact — and opens this
// with its own view container, so the `ErasureFlowService` injected here is the
// screen's instance. Everything the host has to do differently is done here
// against whichever of the two refs is present, so the component behaves the
// same under either and a host that was never exercised cannot drift.
//
// **The flow owns every predicate.** The commit's attribute, its handler and the
// flow's own guard read one `pressable`; Cancel, Escape and the backdrop read
// one `working`. The only state here is the text in the field, which is the one
// thing the flow cannot know until it is handed it.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.e-sheet]': 'inSheet',
  },
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule],
  selector: 'app-erase-dialog',
  styleUrls: ['./erase-dialog.component.scss'],
  templateUrl: './erase-dialog.component.html',
})
export class EraseDialogComponent {
  protected readonly flow = inject(ErasureFlowService);

  private readonly dialogRef = inject<MatDialogRef<EraseDialogComponent>>(
    MatDialogRef,
    { optional: true },
  );
  private readonly sheetRef = inject<MatBottomSheetRef<EraseDialogComponent>>(
    MatBottomSheetRef,
    { optional: true },
  );

  // `read: ElementRef` on both: `#dismissButton` sits on a `mat-button`, whose
  // component instance is what the query answers by default.
  private readonly field = viewChild.required<
    string,
    ElementRef<HTMLInputElement>
  >('field', { read: ElementRef });
  private readonly dismissButton = viewChild.required<
    string,
    ElementRef<HTMLButtonElement>
  >('dismissButton', { read: ElementRef });

  protected readonly fieldCap = CONFIRMATION_FIELD_CHARACTERS;

  protected readonly inSheet = this.sheetRef !== null;

  protected readonly titleId = ERASE_DIALOG_TITLE_ID;
  protected readonly consequenceId = ERASE_DIALOG_CONSEQUENCE_ID;
  protected readonly fieldId = ERASE_DIALOG_FIELD_ID;
  protected readonly dismissId = ERASE_DIALOG_DISMISS_ID;
  protected readonly labelId = 'erase-dialog-label';

  // Whether this dialog has rendered once. The region's line waits for it, so
  // the region is in the DOM and empty before anything lands in it — a region
  // created together with its first line is announced by nothing. That matters
  // most for a dialog opened over `undetermined`, whose line is there from the
  // start.
  readonly #painted = signal(false);

  // What the field holds, as typed. Starts empty in every instance, and every
  // open is a new instance — a word half typed into a dialog somebody cancelled
  // is not a word typed into this one.
  protected readonly typed = signal('');

  protected readonly pressable = computed(() =>
    this.flow.pressable(this.typed()),
  );

  // **Withdrawn on `undetermined`, and never back on this screen.** Derived
  // rather than latched, because the flow cannot leave `undetermined` at all:
  // `pressable` is false from there on, so no press starts and clears it, and
  // `ErasureFlowService.reset` keeps it. So a dialog opened after one that
  // could not tell opens withdrawn — no commit, *Close*, the line — and asks
  // for nothing. A latch here would be a second record of a fact the flow
  // already holds.
  protected readonly withdrawn = computed(
    () => this.flow.failure() === 'undetermined',
  );

  // The region's one line, and whether it is a refusal — which is only the
  // colour; every line reads the same with `--bud-over` removed. At most one
  // line at a time: a press clears the previous word as it starts, so a running
  // phase and a word are never both there to choose between. Nothing until the
  // first render is done, for the reason `#painted` gives.
  protected readonly line = computed<StatusLine | null>(() => {
    if (!this.#painted()) {
      return null;
    }

    switch (this.flow.phase()) {
      case 'asserting':
        return { text: 'Waiting for your passkey.', refusal: false };
      // `erased` keeps the erasing line rather than emptying the region: the
      // tab is already on its way to Welcome, and a region that went blank in
      // between would announce nothing true.
      case 'erasing':
      case 'erased':
        return { text: 'Erasing…', refusal: false };
      case 'idle': {
        const failure = this.flow.failure();

        return failure === null
          ? null
          : { text: sentenceOf(failure), refusal: true };
      }
    }
  });

  constructor() {
    // **Cancel, Escape and the backdrop do nothing while the act runs.** Both
    // refs read `disableClose` at the moment of the keydown or the click, so
    // one reaction keeps it in step with the flow. Waiting for the passkey, a
    // closed overlay leaves a system sheet up with nothing on screen to receive
    // its answer — and an answer that arrives erases the account from a dialog
    // nobody can see.
    effect(() => {
      const working = this.flow.working();

      if (this.dialogRef !== null) {
        this.dialogRef.disableClose = working;
      }

      if (this.sheetRef !== null) {
        this.sheetRef.disableClose = working;
      }
    });

    // The region's line lands on the pass after this one.
    afterNextRender(() => this.#painted.set(true));

    // **Focus on open is the host's, aimed by id.** The opener passes
    // `autoFocus` as a selector for {@link ERASE_DIALOG_FIELD_ID} — the word is
    // the next thing asked for, and a trap that opened on the commit would put
    // a keyboard user one press from the ceremony — or, for a dialog opened
    // withdrawn, for {@link ERASE_DIALOG_DISMISS_ID}, the one control left.
    // Named rather than left to `first-tabbable`, which reaches the field only
    // because of the order of the markup. One move, straight there: focusing
    // from here as well would be a second move the host's own could land
    // before or after.
    //
    // **When `undetermined` arrives mid-dialog, focus moves to the dismiss**,
    // because the control it stood on has left the DOM. Opened withdrawn, this
    // lands on the element the host already focused, which moves nothing.
    afterRenderEffect(() => {
      if (this.withdrawn()) {
        this.dismissButton().nativeElement.focus();
      }
    });
  }

  protected onInput(): void {
    this.typed.set(this.field().nativeElement.value);
  }

  // **The gate is here as well as in the attribute**, and it is the flow's
  // gate: Material's click-halt is applied to anchors only, so on a `<button>`
  // the press arrives whatever `aria-disabled` says. A press the gate refuses
  // at rest moves focus to the field and does nothing else — the word is what
  // opens it. One refused while the act is running moves nothing: focus stays
  // on the commit, where the next attempt starts.
  protected commit(): void {
    const typed = this.typed();

    if (!this.flow.pressable(typed)) {
      if (!this.flow.working()) {
        this.field().nativeElement.focus();
      }

      return;
    }

    this.flow.erase(typed);
  }

  // Closes whichever host this is in. Inert while the act runs, for the reason
  // the `disableClose` reaction above gives — the handler checks too, because
  // `disabledInteractive` leaves the click arriving.
  protected dismiss(): void {
    if (this.flow.working()) {
      return;
    }

    this.dialogRef?.close();
    this.sheetRef?.dismiss();
  }
}

// One sentence per word, and the copy is the specification. Every line but
// `undetermined`'s ends on whether anything was erased, which is the question
// somebody who pressed this asks first; `no-prf` and `ceremony-failed` share one
// by the book's decision. A `switch` over the closed union, so a new word fails
// to compile here instead of rendering an empty region.
function sentenceOf(failure: ErasureFailure): string {
  switch (failure) {
    case 'unsupported':
      return 'This browser can’t check a passkey. Open Budgetoid in a different browser, or on a phone or laptop that can — nothing was erased.';
    case 'cancelled':
      return 'The passkey check was cancelled or timed out. Try again whenever you’re ready — nothing was erased.';
    case 'no-prf':
    case 'ceremony-failed':
      return 'Your device couldn’t finish the passkey check. Try again, or choose another passkey — nothing was erased.';
    case 'unstarted':
      return 'Budgetoid couldn’t start the passkey check. Try again in a minute — nothing was erased.';
    case 'refused':
      return 'Budgetoid didn’t accept that passkey for this account. Try again with a passkey you made for it — nothing was erased.';
    case 'unrecognised':
      return 'Budgetoid couldn’t read this request. Reload the page and try again — nothing was erased.';
    // The one line that cannot say what happened, and it says exactly that. A
    // lost response, a `5xx` and a network failure after the request left all
    // mean the erasure may have committed.
    case 'undetermined':
      return 'Budgetoid can’t tell whether your account was erased. Reload the page to find out.';
  }
}
