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
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  NonNullableFormBuilder,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import type { ErrorStateMatcher } from '@angular/material/core';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import {
  KeyRotationService,
  type KeyRotationNameCollision,
  type KeyRotationRenameRefusal,
} from '@app-core/security/key-rotation.service';
import type { NameArm } from '@app-core/security/rotation-name-collision';
import { NARRATIVE_NAME_CHARACTERS } from '@app-shared/narrative-field-caps';
import { credentialRegistrationDate } from './credential-registration-date';
import { RotationFlowService } from './rotation-flow.service';

/** How the rename block's copy speaks of one list. */
interface ListNoun {
  /** *An* or *A*, capitalised: the lead line opens with it. */
  readonly article: string;
  readonly one: string;
  readonly many: string;
}

// The noun follows the list, with the article English gives it. Exhaustive over
// `NameArm` by `satisfies`, so a fifth list is a compile error here rather than
// a lead line reading "Two undefined are called".
const LIST_NOUNS = {
  accounts: { article: 'An', one: 'account', many: 'accounts' },
  payees: { article: 'A', one: 'payee', many: 'payees' },
  categoryGroups: {
    article: 'A',
    one: 'category group',
    many: 'category groups',
  },
  categories: { article: 'A', one: 'category', many: 'categories' },
} as const satisfies Record<NameArm, ListNoun>;

/**
 * Refuses a value that is entirely whitespace.
 *
 * The ordinary name fields' rule, and this is their function rather than a
 * variant of it: it trims **to judge** and never to alter, because what the
 * driver seals is what the person typed.
 */
function nonBlank(control: AbstractControl): ValidationErrors | null {
  return typeof control.value === 'string' && control.value.trim().length === 0
    ? { blank: true }
    : null;
}

// Which pair a name is typed for: the list and the two records, never the
// names. The driver hands over a fresh object on every stop, including a stop
// on the very pair the person was already shown, so the object is no identity.
function pairKey(pair: KeyRotationNameCollision | null): string | null {
  return pair === null
    ? null
    : `${pair.arm}:${pair.renamed.id}:${pair.kept.id}`;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatInputModule,
    MatProgressBarModule,
    ReactiveFormsModule,
  ],
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

  /**
   * **New name**, the rename block's one field.
   *
   * The cap and the whitespace-only refusal the ordinary name fields take, from
   * the same constant, so the rename cannot accept a name the lists refuse. The
   * cap reaches the validator as a call argument, which keeps the live import —
   * `AccountsComponent.nameCharacters` states why a bare field initialiser
   * would not.
   *
   * **Component state, like {@link acknowledged}**: a name typed for a pair is
   * dead the moment the section leaves the screen.
   */
  protected readonly nameControl = inject(NonNullableFormBuilder).control('', [
    Validators.required,
    nonBlank,
    Validators.maxLength(NARRATIVE_NAME_CHARACTERS),
  ]);

  readonly #nameStatus = toSignal(this.nameControl.statusChanges, {
    initialValue: this.nameControl.status,
  });

  /** Whether the field holds a name a press may carry. */
  protected readonly nameGiven = computed(() => this.#nameStatus() === 'VALID');

  /**
   * Whether the control is drawn unpressable, and the one reading its handler
   * refuses on.
   *
   * **One computed for the attribute and the handler**, so the two are the
   * same width by construction: in flight, unticked, or — while the rename
   * block stands — a field holding no name a press may carry. "In flight" is
   * still the flow's one predicate, read here rather than restated.
   */
  protected readonly held = computed(
    () =>
      this.flow.working() ||
      !this.acknowledged() ||
      (this.rotations.collision() !== null && !this.nameGiven()),
  );

  /** How the block's copy speaks of the pair's list, or `null` with no pair. */
  readonly #noun = computed(() => {
    const pair = this.rotations.collision();

    return pair === null ? null : LIST_NOUNS[pair.arm];
  });

  /**
   * The lead line naming the pair, and the field's accessible description.
   *
   * Two sentences, chosen by whether the two names are spelled alike **as
   * stored** — the names render the way every list renders them, and a pair
   * differing only in case is exactly the pair that needs saying out loud.
   *
   * **The renamed record is named by its state, never by when it was named.**
   * It is the one not yet re-encrypted, which the run can see; which of the two
   * was named last it cannot, because a pass re-seals what its collection saw
   * and can put a name back onto a record another tab renamed meanwhile.
   */
  protected readonly lead = computed(() => {
    const pair = this.rotations.collision();
    const noun = this.#noun();

    if (pair === null || noun === null) {
      return '';
    }

    return pair.kept.name === pair.renamed.name
      ? `Two ${noun.many} are called “${pair.kept.name}”. The new name goes ` +
          'to the one that isn’t re-encrypted yet.'
      : `${noun.article} ${noun.one} called “${pair.kept.name}” and one ` +
          `called “${pair.renamed.name}” count as the same name. The new ` +
          `name goes to “${pair.renamed.name}”, the one that isn’t ` +
          're-encrypted yet.';
  });

  /**
   * The chapter's own sentence for `taken`: the client's observation, made
   * before any request exists, so there is no server sentence to render.
   */
  protected readonly takenSentence = computed(() => {
    const noun = this.#noun();

    return noun === null
      ? ''
      : `Another ${noun.one} already has this name. Choose a different one.`;
  });

  /**
   * The field is in error exactly while a refusal of the last press's name
   * stands, and for nothing else.
   *
   * **Not Material's default matcher**, which would redden a touched blank
   * field: this block has no sentence for a blank or over-long name — the
   * control waits on the field instead — so a red border there would be colour
   * carrying a message nothing else says. And the refusals are the driver's
   * state, not a validator's: `setErrors` would be a second copy of them,
   * dropped by the next keystroke while the refusal still stands.
   */
  protected readonly refusalMatcher: ErrorStateMatcher = {
    isErrorState: () => this.rotations.renameRefusal() !== null,
  };

  private readonly nameField =
    viewChild<ElementRef<HTMLInputElement>>('nameField');

  // `read: ElementRef` because the button hosts `MatButton`, a component, and a
  // bare template reference on a component's host resolves to the instance.
  private readonly control = viewChild<string, ElementRef<HTMLButtonElement>>(
    'control',
    { read: ElementRef },
  );

  readonly #injector = inject(Injector);

  // What the driver had published when this render last looked, so that focus
  // moves on an *arrival* of a pair or a refusal and never on what was already
  // standing when the section was constructed.
  #shownPair: KeyRotationNameCollision | null = this.rotations.collision();
  #shownRefusal: KeyRotationRenameRefusal | null =
    this.rotations.renameRefusal();

  constructor() {
    // **A name typed for one pair answers a question nobody is asking now**, so
    // a different pair clears the field. Keyed on the pair's identity rather
    // than the object: a refusal of the typed name republishes the same pair,
    // and there the value is kept.
    const typedFor = computed(() => pairKey(this.rotations.collision()));

    effect(() => {
      typedFor();
      untracked(() => {
        this.nameControl.setValue('');
      });
    });

    // **Focus follows the outcome, never the edge.** A press that ends on
    // `same-name` publishes a pair, and one refused at the field publishes a
    // refusal; either puts the next act in the field. Both arrive only when a
    // press answers — the driver clears the refusal as a press starts and
    // replaces the pair when it ends — so a press whose ceremony failed, which
    // reaches the driver not at all, moves nothing, and neither does arriving
    // at the screen with the block already drawn.
    //
    // **And when the block goes while a press is working, focus moves to the
    // section's control** — the rename was saved, or somebody fixed the pair
    // elsewhere, and the run carries on under **Finish rotating**. The field
    // is read-only through a press and keeps focus, so a person who pressed
    // from the keyboard is standing in it when it leaves the DOM, and without
    // this they fall to the page at the moment the outcome is announced. Only
    // then: the effect runs before the render that removes the block, so
    // whether the field holds focus is read while it still exists, and focus
    // anywhere else is the person's own and stays where it is.
    effect(() => {
      const pair = this.rotations.collision();
      const refusal = this.rotations.renameRefusal();
      const arrived =
        (pair !== null && pair !== this.#shownPair) ||
        (refusal !== null && refusal !== this.#shownRefusal);
      const went =
        pair === null &&
        this.#shownPair !== null &&
        untracked(() => this.flow.working() && this.#fieldHoldsFocus());

      this.#shownPair = pair;
      this.#shownRefusal = refusal;

      if (arrived) {
        this.#focusAfterRender(() => this.nameField());
      } else if (went) {
        this.#focusAfterRender(() => this.control());
      }
    });
  }

  #fieldHoldsFocus(): boolean {
    const field = this.nameField()?.nativeElement;

    return field !== undefined && field.ownerDocument.activeElement === field;
  }

  #focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(
      () => {
        target()?.nativeElement.focus();
      },
      { injector: this.#injector },
    );
  }

  /**
   * The cap the field's `maxlength` reads — a getter and not a field, for the
   * reason `AccountsComponent.nameCharacters` states.
   */
  protected get nameCharacters(): number {
    return NARRATIVE_NAME_CHARACTERS;
  }

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
   * drawn unpressable on {@link held}, and this line refuses on that same
   * computed — in flight, unticked, or a rename with no name to carry — so a
   * handler narrower than what the screen promised is a state this pair has no
   * version of. That is not a second definition of "a run is in flight" —
   * there is exactly one, and it is `RotationFlowService.working`, which
   * `held` reads. The flow guards on the same signal again at its own
   * entry point, where it is the driver's re-entrancy guard: `begin()` has none
   * of its own, and two concurrent runs would fight over the two generations a
   * run holds while it walks an account.
   */
  protected press(): void {
    if (this.held()) {
      return;
    }

    // Which act the one control is, read at the press rather than at the last
    // render: **Rename and finish** while a pair stands, which wins over the
    // other two because that run cannot finish until a name changes.
    if (this.rotations.collision() === null) {
      this.flow.rotate();

      return;
    }

    // As typed: the driver seals what the person wrote, never a trimmed copy.
    this.flow.renameAndFinish(this.nameControl.value);
  }
}
