// The act that gives an account new keys and rewrites every sealed value in it
// under them, and the way in to it.
//
// **Its own component rather than two hundred more lines of
// `settings.component.html`**, and the reason is not file length: this is the
// one section on the screen with a *gate* in it, and the gate is half attribute
// and half handler. Living inside the screen, the handler's half would be a
// method on a component that also owns an export, a credential list and an
// unlock, and the acknowledgement it reads would be one more signal in that
// crowd. Here the component is the section, and everything it holds is about
// one press.
//
// **It reads two collaborators by name, and the two names are the design.**
// `rotations` is the *account's* state: whether there is a run to finish, where
// a run has got to, and how one stopped. `flow` is *this screen's* state:
// whether a press is in the air, and how a device refused the last one. Two
// objects, two lifetimes, two questions — and the same composition the Account
// keys section makes out of custody and its own flow. Folding them into one
// screen-state enum was rejected there for a reason that holds here: an enum is
// a second copy of two independent classes' facts, kept in step by hand, and the
// moment either publishes a state it has no arm for the section renders whichever
// arm happens to be last.
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { KeyRotationService } from '@app-core/security/key-rotation.service';
import { credentialRegistrationDate } from './credential-registration-date';
import { RotationFlowService } from './rotation-flow.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatCheckboxModule, MatProgressBarModule],
  selector: 'app-key-rotation-section',
  styleUrls: ['./key-rotation-section.component.scss'],
  templateUrl: './key-rotation-section.component.html',
})
export class KeyRotationSectionComponent implements OnInit {
  // Root-provided: a run is state of the **account**, it survives the tab that
  // began it as server state, and nothing about it belongs to a render of this
  // section.
  protected readonly rotations = inject(KeyRotationService);
  // Screen-provided by `SettingsComponent`, so an abandoned press dies with the
  // screen it was made on.
  protected readonly flow = inject(RotationFlowService);

  /**
   * Whether the person has said they will leave the tab open.
   *
   * **Component state, and deliberately not the flow's.** It is one boolean,
   * dead the moment the section leaves the screen, and putting it on a service
   * would give it a lifetime longer than the only surface that can read it —
   * which is precisely how a box arrives ticked.
   *
   * **It starts `false` on every construction, including over a run there is
   * already to finish.** A box that arrives ticked acknowledges nothing, and a
   * resumed run rewrites the account exactly as the first press did.
   */
  protected readonly acknowledged = signal(false);

  /**
   * Where the bar stands, as a percentage of what this run has to carry.
   *
   * `0` over an empty denominator rather than a division: a run that has
   * published no inventory yet has nothing to be a fraction of, and `NaN`
   * reaches `aria-valuenow` as the string `NaN`.
   */
  protected readonly percentage = computed(() => {
    const { resealed, records } = this.rotations.progress();

    return records === 0 ? 0 : Math.round((resealed / records) * 100);
  });

  /**
   * The day the staged run began, in the reader's own calendar and the reader's
   * own zone.
   *
   * **`credentialRegistrationDate` imported rather than reimplemented**, and the
   * name is the one thing wrong with doing so. What it holds is worth the
   * mismatch: it reads the UTC instant into the *reader's* day rather than the
   * UTC day — different days for fourteen hours out of every twenty-four at
   * UTC+14 — it goes through `Intl` rather than `DatePipe`, which would render
   * every reader's date in American convention because nothing here provides a
   * `LOCALE_ID`, and it is **total**, answering the empty string for an instant
   * it cannot read. That last property is what makes it safe here: this is read
   * inside a `computed` a template binds, and a `RangeError` escaping change
   * detection takes every section below this one down for the rest of the visit.
   */
  protected readonly startedOn = computed(() => {
    const staged = this.rotations.staged();

    return staged === null
      ? ''
      : credentialRegistrationDate(staged.startedAtUtc);
  });

  public ngOnInit(): void {
    // **The read the section makes before it draws anything.** A rotation that
    // was interrupted survives only as server state — a staging row, a staged
    // epoch and one seal per factor, and nothing in the browser — so which of
    // the two controls there is to offer is a question only the server can
    // answer. It posts nothing, opens nothing and touches no key material, and
    // a read that did not happen publishes "nothing to finish" rather than a
    // word: there is no run here to have become anything.
    void this.rotations.readStagedRotation();
  }

  /**
   * The press.
   *
   * **The acknowledgement gate is here as well as in the attribute, and that is
   * not belt and braces.** Material's click-halt is installed on anchors only
   * (`MatButtonBase._setupAsAnchor`), so on a `<button>` the DOM `disabled`
   * property stays `false` and the browser delivers the click to this method
   * exactly as if nothing were disabled. The attribute is presentation; this
   * line is the rule. Without it the screen begins a run for somebody who
   * acknowledged nothing — and a run writes to every row they own.
   *
   * **And the in-flight half is here too, because a guard backstopping an
   * attribute has to be at least as wide as that attribute.** The control is
   * drawn unpressable on `working || !acknowledged()`; a handler covering only
   * the second of those is narrower than what the screen promised, and every
   * press landing in the gap is one the section drew as impossible. That is not
   * a second definition of "a run is in flight" — there is exactly one, and it
   * is `RotationFlowService.working`. This line *reads* it, as the `disabled`
   * binding and `aria-busy` do, which is what keeps the three the same width
   * when any of them moves. The flow guards on the same signal again at its own
   * entry point, where it is the driver's re-entrancy guard: `begin()` has none
   * of its own, and two concurrent runs would fight over the two generations a
   * run holds while it walks an account.
   */
  protected rotate(): void {
    if (this.flow.working() || !this.acknowledged()) {
      return;
    }

    this.flow.rotate();
  }
}
