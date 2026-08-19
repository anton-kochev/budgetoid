import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import type { RecoveryCode } from '@app-core/security/recovery-codes';
import { ClipboardService } from '@app-core/services/clipboard.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import { recoveryCodesFilename } from '../recovery-codes-filename';

// The one screen in the system that shows a recovery code, and the only time
// each of these ten will ever be on a display: nothing stores them, nothing can
// re-derive them, and the server has no member one could travel back in.
// Everything else about this component follows from that single fact.
//
// **It has a flow now, and still no state of its own.** `RegisterService` mints
// the set, wraps the account keys under each code and commits the account;
// `register.component` renders this step as the last of three. The codes still
// arrive through a required input and `create` is still an output, and this
// component still mints nothing, posts nothing and navigates nowhere. Do not
// "finish" it by calling `mintRecoveryCodeSet` in here: a set minted by the
// screen that displays it would be re-minted by every re-render of the step,
// and the codes a person wrote down would stop being the codes the account was
// created with. That rule is now satisfied by the flow owning the mint rather
// than by there being no flow at all, which makes it easier to break, not
// harder.

// The outcomes a press can have.
//
// A union of literals rather than three booleans, because the three are
// mutually exclusive by nature and three booleans can encode a state that is
// not — `Copied.` and `Couldn't copy them.` in the same region at the same
// time, which is a screen that has said both and meant neither. `null` is the
// fourth state and the one the screen opens in: nothing has been pressed yet.
type CodesStepOutcome = 'copied' | 'copy-failed' | 'saved';

// Characters per printed group, and the whole of the grouping rule.
//
// Twenty-six characters divide as six fours and a two. That tail is not an
// accident to be tidied away — a group size that divided 26 evenly would be 13
// or 2, and neither is a chunk a person can hold in their head while their eyes
// travel to a sheet of paper. Four is the size a card number, a licence key and
// a sort code all settled on.
//
// The grouping is free: `canonicalRecoveryCode` strips hyphens and whitespace
// before anything derives from a code, so what is displayed here and what is
// typed back reduce to the same text. `groups a code back to exactly what was
// minted` is what holds that claim, and it holds it through the shipped
// canonical function rather than a local fold, so this cannot drift from the
// redemption path.
const GROUP_SIZE = 4;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatCheckboxModule],
  selector: 'app-codes-step',
  styleUrls: ['./codes-step.component.scss'],
  templateUrl: './codes-step.component.html',
})
export class CodesStepComponent {
  /**
   * The freshly minted set, in the order it was minted.
   *
   * Required rather than defaulted: an empty default would render a screen
   * promising ten codes and showing none, and the failure would look like a
   * styling problem rather than like a flow that forgot to mint.
   */
  public readonly codes = input.required<readonly RecoveryCode[]>();

  /**
   * Raised once the person has acknowledged the consequence and pressed the
   * final control. It carries nothing — the codes are already the caller's, and
   * handing them back out of here would put a second reference to ten secrets
   * into a flow that has no use for it.
   */
  public readonly create = output<void>();

  // Component state, and deliberately not a service: it is one boolean and one
  // outcome, both dead the moment the step is left. A service would give them a
  // lifetime longer than the only screen that can read them.
  protected readonly acknowledged = signal(false);
  protected readonly outcome = signal<CodesStepOutcome | null>(null);

  private readonly clipboard = inject(ClipboardService);
  private readonly downloads = inject(FileDownloadService);

  // The codes as they are shown, grouped, in mint order. One computed feeding
  // the list, the file and the clipboard, because the three have to be the same
  // text: a person who saved a file and a person who pasted from the clipboard
  // are checking their copy against the same screen, and two of the three
  // agreeing is a backup that fails on the one day it is needed.
  protected readonly groupedCodes = computed<readonly string[]>(() =>
    this.codes().map(groupCode),
  );

  // What is written to disk and what is put on the clipboard, and nothing else.
  //
  // Built from the grouped codes rather than from the rendered list, which is
  // the trap the printed positions create: a payload assembled from each `<li>`
  // carries `7 ` in front of the seventh code, produces a file that looks
  // perfectly right — ten lines, every code present, the grouping intact — and
  // every code pasted out of it derives a verifier matching no row, because the
  // digit and the space go through `canonicalRecoveryCode` as part of the code.
  //
  // **Bare, and it stays bare.** No header, no caption, no product name, no
  // date, no instructions. A line reading "Budgetoid recovery codes" would
  // label the secret for whoever finds the file — a stranger with the disk, a
  // backup service, a shared downloads folder — and turn ten anonymous strings
  // into ten keys with a name on them. The filename pays that cost once because
  // a person has to be able to find the file again; once is the most it can be
  // paid, and the contents do not repeat it. Do not caption this file.
  //
  // The trailing newline is the POSIX text-file convention and is why every
  // reader of this payload filters empty lines rather than trusting the split.
  private readonly payload = computed(
    () => `${this.groupedCodes().join('\n')}\n`,
  );

  protected save(): void {
    // `text/plain` and not `application/octet-stream`: the person is being
    // asked to check the file against the screen, which means they have to be
    // able to open it by double-clicking it.
    this.downloads.save(
      new Blob([this.payload()], { type: 'text/plain;charset=utf-8' }),
      // The instant is read here and passed in, never read inside the name —
      // that separation is the only reason the format is testable at all, and
      // it is why the runner's zone is pinned away from UTC.
      recoveryCodesFilename(new Date()),
    );
    // The browser writes the file with no visible act of its own, so a screen
    // that says nothing leaves the person having pressed a button that produced
    // no observable effect. That reads as broken and invites the second press.
    this.outcome.set('saved');
  }

  protected copy(): void {
    // `.then`/`.catch` rather than `async`, so this returns `void` to the
    // template: an `async` handler hands the template a promise nobody awaits,
    // and a rejection out of one is an unhandled rejection rather than a
    // sentence on the screen.
    //
    // **Both branches say something.** `writeText` rejects routinely and for
    // reasons that have nothing to do with this app — a permissions policy, an
    // iframe, Safari deciding the press was not a user gesture — and silence
    // after a press leaves somebody believing ten secrets are on their
    // clipboard when nothing is. The failure sentence names the other route
    // rather than offering "try again", because the other route is the one that
    // works. The codes stay on screen throughout: this is the one screen where
    // "we'll show you again later" does not exist.
    this.clipboard
      .write(this.payload())
      .then(() => this.outcome.set('copied'))
      .catch(() => this.outcome.set('copy-failed'));
  }

  protected createAccount(): void {
    // **The gate, and the highest-stakes line in this component.**
    //
    // The control is rendered with `disabledInteractive`, which sets
    // `aria-disabled` and the disabled appearance while leaving the DOM
    // `disabled` property `false` — that is what keeps it in the tab order so a
    // keyboard user can reach it and discover what it is waiting on. The cost
    // is that the browser delivers the click to this method exactly as if
    // nothing were disabled: Material's own click-halt is installed on anchors
    // only (`MatButtonBase._setupAsAnchor`), never on a `<button>`.
    //
    // So the attribute is presentation and this line is the rule. Without it
    // the screen creates an account for somebody who acknowledged nothing, on a
    // flow whose entire premise is that the acknowledgement happened.
    if (!this.acknowledged()) {
      return;
    }

    this.create.emit();
  }
}

// One code, grouped for transcription. A module-level function rather than a
// method: it reads no state and belongs to no instance.
//
// Written as a slice loop rather than as `code.match(/.{1,4}/g)`, which returns
// `RegExpMatchArray | null` and would force either a non-null assertion or a
// fallback branch that no input can reach — a line no test can cover, standing
// in for a case that cannot happen.
function groupCode(code: RecoveryCode): string {
  const groups: string[] = [];

  for (let start = 0; start < code.length; start += GROUP_SIZE) {
    groups.push(code.slice(start, start + GROUP_SIZE));
  }

  return groups.join('-');
}
