// The categories screen, and the last surface in this product to render values
// it had to open and to refuse writing ones it cannot seal. After this, nothing
// in the client sends plaintext.
//
// **The locked treatment is `docs/design/components.md`, "The locked account",
// and it is the same three things the accounts and transactions screens
// ship.** The hierarchy is replaced by `locked-account-notice` — not hidden,
// and not a route guard, which would be synchronous against a fact with no
// resolution on the navigation path and would put key state where
// `AccountUnlockService` is built to keep it out of. **Both** forms are
// DOM-disabled, because an enabled form submits, the service refuses because it
// cannot seal, and nothing happens — which reads as a failure rather than as a
// limitation. And the reason is a sentence beside each form.
//
// **Two predicates over one status, and they are not the same question.**
// {@link CategoriesComponent.writable} is `=== 'unlocked'`, written
// **positively** so that `unlocking` and any word added later arrive disabled —
// loud and harmless — rather than live and silent.
// {@link CategoriesComponent.locked} is `=== 'locked'` exactly, because the
// notice's sentence is *advice* and that advice is already wrong for somebody
// whose unlock is running. Disable when unsure; do not advise when unsure.
//
// **The lock is named three times per form and none of them is redundant, and
// a fourth time on the one control that renders a value somebody opened.** A
// disabled form's status is `DISABLED`, which excludes it from validation and
// makes `form.invalid` answer **false** — so `[disabled]="form.invalid"` alone
// *enables* the submit button the moment the form is switched off. It is named
// on the form (the `effect`), again on the control, and again in the handler,
// because Material's click-halt is applied to anchors only and a `<button>`
// still receives the press. The fourth is the category form's group picker,
// which **leaves the DOM** rather than being switched off: it sits outside the
// `@if (locked())` that replaces the hierarchy, and disabling a `mat-select`
// does not stop it displaying the option it had selected — measured — so a lock
// landing over a filled form left a group's opened name beside a notice saying
// this tab cannot read the account.
//
// **The group picker's enabled state has one owner and it is the `effect`.** It
// is off for the whole of an edit — a rename binds three members and the group
// is not one of them, so a category moves group by being dragged — and that was
// once set by `editCategory` and unset by `cancelCategoryEdit`, which gave one
// control three writers and two silent failures. `categoryForm.enable()`
// reaches every child, so a lock and an unlock mid-edit handed the picker back
// live and a Save then sent `{description, name}`: a 204, and a category that
// did not move. And Cancel, a plain `<button>` unaffected by the FormGroup's
// disabled state, enabled it unconditionally — one live control on a locked
// form, which flips the form's own status out of `DISABLED`, because a group is
// `DISABLED` only while every child is. Derived from `writable()` and
// `editingCategoryId()` in one place, neither is reachable.
//
// **A failed read has a line of its own.** Both lists are `null` at rest, in
// flight **and** after a failure, so a screen reading a list and the running
// flag alone rendered nothing whatever over a read that never landed — two
// forms and silence, which is what an account with no categories looks like.
//
// **Both lines live in a `role="status"` region that is in the DOM from first
// paint**, the rule `docs/design/components.md` states under "A value read
// from the network", and the reason it has to be *from first paint* is that a
// live region created together with its text is announced by nothing —
// assistive technology has to have been watching the node already. The two
// sentences used to be the last two branches of the chain below, which meant
// each of them arrived with its own node and neither was ever announced. What
// stops that coming back is a spec case that takes the node while it is silent
// and asserts the later text lands in that same element, because a case
// asserting only that the sentence is *somewhere* on screen passes either way.
//
// **A row whose words did not open cannot be renamed, and this screen has a
// half the accounts screen never had.** Both `PUT` routes carry the note
// **beside** the name, so an edit started over a note that did not open
// prefills that field empty and the save posts `null` — clearing a note still
// sitting in the column, over a name that rendered perfectly, with a 204 and a
// legal row and nothing anywhere to see. `categoryGroupIsReadable` and
// `categoryIsReadable` are *type* predicates for exactly that reason: on the
// accounts screen the compiler held the handler's half for free, and a screen
// with a second nullable column loses it the moment somebody writes
// `description?.state === 'text' ? … : ''` inline. Deleting such a row stays
// available: removing is not rewriting.
//
// **A category's group name is not part of that gate.** It is the group's
// column denormalized onto the row, and a category's `PUT` cannot touch it.
//
// **The non-blank validator is on the name and deliberately not on the note.**
// The name is blind-indexed and the index normalizes by trimming, so `'   '`
// keys to the index of the **empty** name — every blank-named row in the budget
// collides on the column whose whole purpose is that equal names collide. The
// note is indexed by nothing, so a note of three spaces costs nobody anything
// and is a note somebody typed: the service seals it as typed, and the
// `normalizeDescription` that used to fold it onto `null` is gone, because the
// client may not alter what it seals.
//
// **A refused write is never silent and it never costs a keystroke**, which is
// `docs/design/components.md`, "A write that does not happen".
// `accounts.component.ts` argues the shared half at its own copy: both handlers
// **await** the outcome and clear on `recorded` alone, the server's sentences
// render beneath the controls they were keyed to, everything else takes a line
// in the region this screen already has, and the region stays `status`.
//
// **What is this screen's own is that it has two writing surfaces and one
// region.** So the last write is held as a *pair* — which form it was, and what
// it answered — and the field messages of a refused group write render under
// the group form and nowhere else. The alternative is the defect this shape
// exists to refuse: both forms carry a control called `name`, so a report keyed
// on the control alone paints one server sentence under two fields, one of them
// about text nobody submitted. Holding the surface beside the report is also
// what keeps the region's exclusivity — a second write replaces the first
// whichever form it came from, so the screen never shows two accounts of what
// happened.
//
// **Four of this screen's writes belong to no form at all**, and they take the
// same three steps with two differences. A group's move, a group's delete, a
// category's placement and a category's delete each clear the last report,
// await their word and render it — through `rowActReportOf`, whose sentences
// are written for an act holding no typed text, and filed under a `null`
// surface, so neither form can show a `mat-error` for a press made on a row.
// Nothing focuses, because there is no control the sentence is about. The
// design book left these four with a classification and nowhere to put it; the
// copy that closed the gap lives in `+shared/write-outcome-report.ts`.
import {
  CdkDrag,
  CdkDragDrop,
  CdkDragHandle,
  CdkDropList,
} from '@angular/cdk/drag-drop';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import {
  FormBuilder,
  FormGroup,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import {
  NARRATIVE_DESCRIPTION_CHARACTERS,
  NARRATIVE_NAME_CHARACTERS,
} from '@app-shared/narrative-field-caps';
import {
  clearFieldMessages,
  markFieldMessages,
  rowActReportOf,
  writeReportOf,
  type RowAct,
  type WriteReport,
} from '@app-shared/write-outcome-report';
import type { WriteOutcome } from '@app-core/api/write-outcome';
import {
  categoryGroupIsReadable,
  type CategoryGroupView,
} from './category-group-view';
import { categoryIsReadable, type CategoryView } from './category-view';
import { CategoriesService } from './categories.service';

/**
 * Refuses a value that is entirely whitespace.
 *
 * It trims **to judge** and never to alter: what the service seals is the
 * control's own value, character for character, and a validator that wrote a
 * trimmed value back would reintroduce the defect it exists to close.
 */
function nonBlank(control: AbstractControl): ValidationErrors | null {
  return typeof control.value === 'string' && control.value.trim().length === 0
    ? { blank: true }
    : null;
}

/** Which of this screen's two forms a write came from. */
type WritingSurface = 'group' | 'category';

/** The last write on this screen: which form it was, and what it answered. */
interface SurfaceWrite {
  /**
   * The form that wrote, or `null` for a write **no form made** — a row's
   * delete, or a row's move.
   *
   * **`null` is what keeps a `mat-error` off both forms structurally**, rather
   * than by the coincidence that a delete's report carries no fields today:
   * {@link CategoriesComponent.groupMessages} and its neighbour compare this
   * against their own name, and `null` is neither of them. A delete filed under
   * one of the two words would read as a claim about a form nobody submitted.
   */
  readonly surface: WritingSurface | null;
  readonly report: WriteReport;
}

/**
 * The wire keys each form can place a server's sentence on, and the control it
 * goes beneath.
 *
 * **A `Map` and never an object literal**, per the chapter: `constructor`,
 * `toString` and `valueOf` hit on a literal and place a message under a control
 * that does not exist.
 *
 * The category form's group picker is included unconditionally, and that is a
 * statement rather than an oversight: it leaves the DOM only while the account
 * is locked, which is a state in which this form cannot write at all — so at
 * every moment a message could arrive, the control is on screen to take it.
 */
function placeableKeys(surface: WritingSurface): ReadonlyMap<string, string> {
  const keys = new Map<string, string>([
    ['Name', 'name'],
    ['Description', 'description'],
  ]);

  if (surface === 'category') {
    keys.set('CategoryGroupId', 'categoryGroupId');
  }

  return keys;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    LockedAccountNoticeComponent,
    NarrativeValueComponent,
  ],
  styles: `
    :host {
      display: block;
      padding: 1.5rem 2rem;
    }

    .editors {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
      gap: 2rem;
      margin-bottom: 2rem;
    }

    form {
      display: grid;
      align-content: start;
      gap: 1rem;
    }

    .actions,
    .group-heading,
    .category-row {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .group-heading {
      justify-content: space-between;
    }

    .reason {
      margin: 0;
      color: var(--bud-text-muted);
    }

    /*
      A refused write's line in the shared region. --bud-over, and colour is
      never the message: every sentence in the chapter's table reads the same
      with this declaration removed.
    */
    .refusal {
      margin: 0;
      color: var(--bud-over);
    }

    .category-groups {
      display: grid;
      gap: 1rem;
    }

    .category-group {
      border: 1px solid color-mix(in srgb, currentColor 20%, transparent);
      border-radius: 0.5rem;
      padding: 1rem;
      background: var(--mat-sys-surface, Canvas);
    }

    .category-list {
      min-height: 3rem;
      display: grid;
      gap: 0.5rem;
      padding-top: 0.5rem;
    }

    .category-row {
      border: 1px solid color-mix(in srgb, currentColor 12%, transparent);
      border-radius: 0.25rem;
      padding: 0.5rem;
      background: var(--mat-sys-surface-container-low, Canvas);
    }

    .category-copy {
      flex: 1;
      display: grid;
    }

    .empty {
      opacity: 0.7;
      padding: 0.75rem;
    }

    .cdk-drag-preview {
      box-sizing: border-box;
      border-radius: 0.5rem;
      box-shadow: 0 5px 10px rgb(0 0 0 / 20%);
    }

    .cdk-drag-placeholder {
      opacity: 0.25;
    }
  `,
  template: `
    <h1>Categories</h1>

    <div class="editors">
      <!--
        data-surface names which of the two forms this is. It is read by the
        component when a refusal has to move focus to a control, because both
        forms carry one called **name** and a host-wide lookup would land on
        the group's every time.
      -->
      <form
        [formGroup]="groupForm"
        data-surface="group"
        (ngSubmit)="saveGroup()"
      >
        <h2>
          {{ editingGroupId() ? 'Edit category group' : 'Add category group' }}
        </h2>
        @if (!writable()) {
          <!--
            The reason, beside the form rather than on it. A disabled control
            whose explanation is a tooltip is an explanation nobody hears, and
            this is a capability the tab has temporarily lost rather than one
            the product does not have — so the sentence names the press that
            returns it.
          -->
          <p class="reason">
            Adding and editing are off while this tab can’t read your account.
            Press Unlock in Settings to turn them back on.
          </p>
        }
        <mat-form-field>
          <mat-label>Name</mat-label>
          <!--
            Both caps are bound rather than typed, so an attribute and the
            validator beside it cannot drift apart, and so the numbers stay
            beside the byte caps they protect —
            @app-shared/narrative-field-caps holds the whole argument. This
            screen states each of them twice, once per form, which is the
            reason they are named here rather than written out.
          -->
          <input
            matInput
            formControlName="name"
            [attr.maxlength]="nameCharacters"
          />
          <!--
            The server's sentence, rendered verbatim beneath the control it was
            keyed to, and only where the *group* form is the one that was
            refused — both forms carry a control called name, so a lookup on
            the control alone would paint one sentence under two fields.
          -->
          @for (message of groupMessages()?.get('name') ?? []; track $index) {
            <mat-error>{{ message }}</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>Description</mat-label>
          <textarea
            matInput
            formControlName="description"
            [attr.maxlength]="descriptionCharacters"
          ></textarea>
          @for (
            message of groupMessages()?.get('description') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
        </mat-form-field>
        <div class="actions">
          <!--
            !writable() first in the disabled expression, and it is not
            redundant: a disabled form's status is DISABLED, so form.invalid
            answers false and this control would stay pressable over a form
            nobody can type into.
          -->
          <button
            mat-flat-button
            color="primary"
            type="submit"
            [disabled]="
              !writable() || groupForm.invalid || categories.loading()
            "
          >
            {{ editingGroupId() ? 'Save group' : 'Add group' }}
          </button>
          @if (editingGroupId()) {
            <button mat-button type="button" (click)="cancelGroupEdit()">
              Cancel
            </button>
          }
        </div>
      </form>

      <form
        [formGroup]="categoryForm"
        data-surface="category"
        (ngSubmit)="saveCategory()"
      >
        <h2>{{ editingCategoryId() ? 'Edit category' : 'Add category' }}</h2>
        @if (!writable()) {
          <p class="reason">
            Adding and editing are off while this tab can’t read your account.
            Press Unlock in Settings to turn them back on.
          </p>
        }
        <mat-form-field>
          <mat-label>Name</mat-label>
          <input
            matInput
            formControlName="name"
            [attr.maxlength]="nameCharacters"
          />
          @for (
            message of categoryMessages()?.get('name') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>Description</mat-label>
          <textarea
            matInput
            formControlName="description"
            [attr.maxlength]="descriptionCharacters"
          ></textarea>
          @for (
            message of categoryMessages()?.get('description') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
        </mat-form-field>
        <!--
          The one control on this screen that renders a value somebody had to
          open, and it leaves the DOM while the account is locked rather than
          merely being switched off. It sits in a form, which is **outside** the
          @if (locked()) that replaces the hierarchy, so a group name opened a
          moment earlier went on being displayed beside a notice saying this tab
          cannot read the account.

          Emptying the option list is not enough and that is measured: a
          mat-select goes on displaying the option it had selected after the
          option is gone. locked() exactly, matching the notice rather than
          !writable(): an unlock in flight is not a reason to take a control
          away from somebody who is looking at it.
        -->
        @if (!locked()) {
          <mat-form-field>
            <mat-label>Category group</mat-label>
            <mat-select formControlName="categoryGroupId">
              @for (group of categories.groups() ?? []; track group.id) {
                <!--
                  The group's name is a word rather than a string, so the option
                  renders it through the component that knows the four shapes
                  one comes in. A member interpolated straight in here prints an
                  object, and one collapsed to '' or a dash on the way past
                  makes the picker claim something about the account when the
                  truth is about this tab.
                -->
                <mat-option [value]="group.id">
                  <app-narrative-value [value]="group.name" />
                </mat-option>
              }
            </mat-select>
            @if (editingCategoryId()) {
              <mat-hint>
                Move an existing category by dragging it below.
              </mat-hint>
            }
            @for (
              message of categoryMessages()?.get('categoryGroupId') ?? [];
              track $index
            ) {
              <mat-error>{{ message }}</mat-error>
            }
          </mat-form-field>
        }
        <div class="actions">
          <button
            mat-flat-button
            color="primary"
            type="submit"
            [disabled]="
              !writable() || categoryForm.invalid || categories.loading()
            "
          >
            {{ editingCategoryId() ? 'Save category' : 'Add category' }}
          </button>
          @if (editingCategoryId()) {
            <button mat-button type="button" (click)="cancelCategoryEdit()">
              Cancel
            </button>
          }
        </div>
      </form>
    </div>

    @if (locked()) {
      <!--
        In place of the hierarchy, never over it and never as a redirect.
        Settings holds the way out, so nothing here may take a person off this
        screen.
      -->
      <app-locked-account-notice />
    } @else if (categories.groups(); as groups) {
      <div
        class="category-groups"
        cdkDropList
        [cdkDropListData]="groups"
        (cdkDropListDropped)="dropGroup($event)"
      >
        @for (group of groups; track group.id) {
          <section class="category-group" cdkDrag [cdkDragData]="group">
            <header class="group-heading">
              <div>
                <h2><app-narrative-value [value]="group.name" /></h2>
                @if (group.description; as description) {
                  <p><app-narrative-value [value]="description" /></p>
                }
                @if (!groupIsReadable(group)) {
                  <!--
                    The reason in the row, not in a tooltip: an explanation
                    nobody hears is not an explanation. Rewriting values that
                    cannot be read would seal a blank over words still sitting
                    in the columns, so the control is off rather than merely
                    unhelpful.
                  -->
                  <p class="reason">
                    This group’s words can’t be read here, so it can’t be
                    renamed.
                  </p>
                }
              </div>
              <div class="actions">
                <button mat-button type="button" cdkDragHandle>
                  Move group
                </button>
                <button
                  mat-button
                  type="button"
                  [disabled]="!groupIsReadable(group)"
                  (click)="editGroup(group)"
                >
                  Edit
                </button>
                <!--
                  Deleting stays available on the same row. A person looking at
                  a row they cannot read is entitled to remove it, and removing
                  is not rewriting.
                -->
                <button mat-button type="button" (click)="removeGroup(group)">
                  Delete
                </button>
              </div>
            </header>

            <div
              class="category-list"
              cdkDropList
              [id]="categoryListId(group.id)"
              [cdkDropListData]="categories.categoriesForGroup(group.id)"
              [cdkDropListConnectedTo]="categoryListIds()"
              (cdkDropListDropped)="dropCategory($event, group.id)"
            >
              @for (
                category of categories.categoriesForGroup(group.id);
                track category.id
              ) {
                <div class="category-row" cdkDrag [cdkDragData]="category">
                  <button mat-button type="button" cdkDragHandle>Move</button>
                  <span class="category-copy">
                    <strong>
                      <app-narrative-value [value]="category.name" />
                    </strong>
                    @if (category.description; as description) {
                      <small>
                        <app-narrative-value [value]="description" />
                      </small>
                    }
                    @if (!categoryIsReadable(category)) {
                      <small class="reason">
                        This category’s words can’t be read here, so it can’t be
                        renamed.
                      </small>
                    }
                  </span>
                  <button
                    mat-button
                    type="button"
                    [disabled]="!categoryIsReadable(category)"
                    (click)="editCategory(category)"
                  >
                    Edit
                  </button>
                  <button
                    mat-button
                    type="button"
                    (click)="removeCategory(category)"
                  >
                    Delete
                  </button>
                </div>
              } @empty {
                <span class="empty">Drop or add a category here.</span>
              }
            </div>
          </section>
        } @empty {
          <p>No category groups yet. Add one before creating categories.</p>
        }
      </div>
    }

    <!--
      **In the DOM from first paint and empty until there is something to
      say**, which is docs/design/components.md under "A value read from the
      network". A live region created at the moment it gains content is
      announced unreliably — assistive technology has to have been watching the
      node before the text landed — so a template that wrapped each sentence in
      its own role="status" would render identically and say nothing to
      anybody. It is status and never assertive: these are results of a read
      this screen started on its own, and assertive is reserved for a failure
      to save something a person typed.

      Which line it carries is one word off regionState(), never several
      conditions compared here, so the read's account of itself and the write's
      are exclusive by structure — and so are the two forms', because one
      report replaces the other whichever of them wrote.
    -->
    <div role="status">
      @if (regionState() === 'loading') {
        <p class="reason">Reading your categories…</p>
      } @else if (regionState() === 'refused') {
        @for (sentence of refusals(); track $index) {
          <p class="refusal">{{ sentence }}</p>
        }
      } @else if (regionState() === 'failed') {
        <p class="reason">
          We couldn’t read your categories. Check your connection and reload the
          page.
        </p>
      }
    </div>
  `,
})
export class CategoriesComponent implements OnInit {
  protected readonly categories = inject(CategoriesService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly custody = inject(AccountKeyCustodyService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  // The last write on this screen, as the pair the head of this file argues
  // for. Cleared when the **next write starts** — on either form — and by
  // nothing else, which is what makes a refusal survive a slow read that
  // displaced it and what keeps one report on screen at a time.
  readonly #write = signal<SurfaceWrite | null>(null);

  /**
   * Whether this screen may write.
   *
   * **Positive on purpose, and never `!== 'locked'`** — the head of this file
   * argues it. `unlocking` and any word added later are not `unlocked`, so they
   * arrive disabled, which is the direction a state nobody thought about has to
   * fail in.
   */
  protected readonly writable = computed(
    () => this.custody.status() === 'unlocked',
  );

  /**
   * Whether the notice replaces the hierarchy.
   *
   * `locked` exactly, and deliberately not {@link writable}'s complement: the
   * notice's way forward is "press Unlock in Settings", which is already wrong
   * for somebody whose unlock is running.
   */
  protected readonly locked = computed(
    () => this.custody.status() === 'locked',
  );

  /**
   * The one line the status region carries, or `null` when it has nothing to
   * say.
   *
   * **A published word rather than two conditions compared in the template**,
   * which is what makes loading and failure exclusive by *structure* — one
   * value can only be one of them — instead of by the order the branches were
   * written in.
   *
   * `null` while the account is locked and `null` while the hierarchy is on
   * screen: the notice and the groups are this section's value, and the region
   * speaks only for a read with no value to show. `loading` outranks `failed`
   * for the reason the branch order used to carry: a reload started after one
   * failed read would otherwise keep the failure sentence up throughout it.
   *
   * It reads `groups()` and not `categories()`, matching the render below —
   * the hierarchy is drawn from the groups, and a category list that arrived
   * without one has nowhere to be drawn.
   */
  protected readonly readState = computed<'loading' | 'failed' | null>(() => {
    if (this.locked() || this.categories.groups() !== null) {
      return null;
    }

    if (this.categories.loading()) {
      return 'loading';
    }

    return this.categories.failed() ? 'failed' : null;
  });

  /**
   * The server's sentences for the group form, keyed by the control each goes
   * beneath — and empty whenever the last write was the *other* form's.
   */
  protected readonly groupMessages = computed(() => this.#messagesFor('group'));

  /** The same, for the category form. */
  protected readonly categoryMessages = computed(() =>
    this.#messagesFor('category'),
  );

  /**
   * The lines the region carries for the last write, in the order they were
   * decided. Whichever form it came from: there is one region.
   */
  protected readonly refusals = computed(
    () => this.#write()?.report.lines ?? [],
  );

  /**
   * The one line the shared region carries, as a single word.
   *
   * **A read in flight outranks everything**, because that request is running
   * now and the write has already answered. **A refused write then outranks a
   * finished read's failure**: the form is still holding the text that was
   * refused, and the chapter gives that sentence's removal to the next write
   * alone. `accounts.component.ts` argues it at greater length.
   */
  protected readonly regionState = computed<
    'loading' | 'refused' | 'failed' | null
  >(() => {
    if (this.readState() === 'loading') {
      return 'loading';
    }

    if (this.refusals().length > 0) {
      return 'refused';
    }

    return this.readState();
  });

  /**
   * The caps the templates' `maxlength` attributes read, and the same values
   * the four validators below are built from.
   *
   * `@app-shared/narrative-field-caps` argues them: these are UX ceilings in
   * UTF-16 code units, and what makes each safe is that it cannot seal past its
   * column's byte cap even when every unit is a three-byte character. Two
   * numbers over field *classes* and not four over fields, which is the shape
   * `NarrativeFieldLimits` already gives the byte caps.
   *
   * **Getters and not fields**, for the reason
   * `TransactionsComponent.recordingSentence` states in full at its own copy: a
   * class field whose initialiser is *nothing but* an imported name is the one
   * position the test runner's module transform snapshots rather than reads
   * live, and under this builder the snapshot is taken before the owning module
   * has assigned anything. Every other position stays live — including the four
   * `Validators.maxLength` arguments below, which is why those are safe as they
   * stand.
   */
  protected get nameCharacters(): number {
    return NARRATIVE_NAME_CHARACTERS;
  }

  protected get descriptionCharacters(): number {
    return NARRATIVE_DESCRIPTION_CHARACTERS;
  }

  protected readonly editingGroupId = signal<string | null>(null);
  protected readonly editingCategoryId = signal<string | null>(null);
  protected readonly categoryListIds = computed(() =>
    (this.categories.groups() ?? []).map((group) =>
      this.categoryListId(group.id),
    ),
  );

  // Both forms below take their caps as **call arguments** and deliberately not
  // through fields of their own: an argument keeps the live import, which is
  // the half of `nameCharacters`' paragraph that applies here. It is worth
  // saying rather than assuming, because a cap lost at these sites is silent —
  // `Validators.maxLength(undefined)` neither throws nor refuses anything,
  // measured — and the only thing that would notice is the pairs of cases in
  // this file's spec that push a value one unit past each cap and exactly to
  // it.
  protected readonly groupForm = this.formBuilder.nonNullable.group({
    // `nonBlank` beside `required`, not instead of it: `required` refuses an
    // empty control and admits `'   '`, and the trim that used to catch the
    // second is gone from the service on purpose.
    name: [
      '',
      [
        Validators.required,
        nonBlank,
        Validators.maxLength(NARRATIVE_NAME_CHARACTERS),
      ],
    ],
    // No `nonBlank` here, and that is the decision this screen makes: the note
    // is indexed by nothing, so a note of three spaces collides with nothing
    // and is a note somebody typed.
    description: ['', [Validators.maxLength(NARRATIVE_DESCRIPTION_CHARACTERS)]],
  });

  protected readonly categoryForm = this.formBuilder.nonNullable.group({
    name: [
      '',
      [
        Validators.required,
        nonBlank,
        Validators.maxLength(NARRATIVE_NAME_CHARACTERS),
      ],
    ],
    description: ['', [Validators.maxLength(NARRATIVE_DESCRIPTION_CHARACTERS)]],
    categoryGroupId: ['', [Validators.required]],
  });

  constructor() {
    // **One owner for both forms' enabled state, and the group picker's second
    // rule is derived here rather than set from the handlers.** Disabled
    // through the forms themselves, because Material's click-halt is applied to
    // anchors only: on a `<button>`, `disabledInteractive` leaves the DOM
    // `disabled` false and the click still arrives.
    //
    // The picker used to be switched by `editCategory` and switched back by
    // `cancelCategoryEdit`, which gave one control three owners and two silent
    // failures. `categoryForm.enable()` enables the **group**, so every child
    // goes live with it: a lock and an unlock landing mid-edit handed the
    // picker back, somebody changed the group, pressed Save, and the edit
    // branch sent `{description, name}` — a 204, and a category that did not
    // move. And `cancelCategoryEdit` enabled it unconditionally, so a Cancel
    // pressed on a locked screen — a plain `<button>`, unaffected by the
    // FormGroup's disabled state — left one live control on a form nobody may
    // write through, and a group is `DISABLED` only while **every** child is,
    // so the form's own status flipped back with it.
    effect(() => {
      const writable = this.writable();

      if (writable) {
        this.groupForm.enable({ emitEvent: false });
        this.categoryForm.enable({ emitEvent: false });
      } else {
        this.groupForm.disable({ emitEvent: false });
        this.categoryForm.disable({ emitEvent: false });
      }

      // **After the form-wide call and never before it**, because the call
      // above reaches every child: ordered the other way this line is undone by
      // its own neighbour. A rename binds three members and the group is not
      // one of them — a category moves group by being dragged — so the picker
      // is off for the whole of an edit and on for a create.
      const picker = this.categoryForm.controls.categoryGroupId;

      if (writable && this.editingCategoryId() === null) {
        picker.enable({ emitEvent: false });
      } else {
        picker.disable({ emitEvent: false });
      }
    });
  }

  public ngOnInit(): void {
    this.categories.load();
  }

  protected async saveGroup(): Promise<void> {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here.
    if (!this.writable() || this.groupForm.invalid) {
      return;
    }

    this.#beginWrite();

    // Handed over exactly as typed. The service seals this text and indexes the
    // same string; a `.trim()` on this line would make the two disagree, and
    // the `normalizeDescription` that used to fold a whitespace-only note onto
    // `null` is gone rather than moved one layer up.
    const value = this.groupForm.getRawValue();
    const id = this.editingGroupId();
    // **Awaited, and this is the whole of the chapter's first rule.** The clear
    // below belongs to a `201` or a `204`, never to a press: written on the
    // line after the dispatch it ran before any answer existed and emptied the
    // form on every outcome, including the ones the region is here to render.
    const outcome =
      id === null
        ? await this.categories.addGroup(value)
        : await this.categories.updateGroup(id, value);

    this.#render('group', this.groupForm, outcome);

    if (outcome.state !== 'recorded') {
      // Nothing navigates and nothing collapses: a refused write leaves the
      // form exactly as the press found it, still in edit mode if it was.
      return;
    }

    this.cancelGroupEdit();
  }

  protected editGroup(group: CategoryGroupView): void {
    // The gate is in the handler as well as on the control, the rule the
    // recovery-code hand-off states about its own acknowledgement: Material's
    // click-halt is applied to anchors only, so a disabled `<button>` still
    // receives the press that arrives here. Nothing is prefilled and nothing is
    // put into edit mode — a row with no text to show has no edit to start, and
    // the alternative is a blank field that seals over words still sitting in
    // the columns. The narrowing below is what the compiler needs, which is why
    // dropping this line does not compile rather than merely reddening.
    if (!categoryGroupIsReadable(group)) {
      return;
    }

    this.editingGroupId.set(group.id);
    this.groupForm.setValue({
      description: group.description === null ? '' : group.description.value,
      name: group.name.value,
    });
  }

  protected cancelGroupEdit(): void {
    this.editingGroupId.set(null);
    this.groupForm.reset({ name: '', description: '' });
  }

  protected async removeGroup(group: CategoryGroupView): Promise<void> {
    this.#beginWrite();
    this.#renderRowAct(await this.categories.removeGroup(group.id), 'removal');
  }

  protected async saveCategory(): Promise<void> {
    if (!this.writable() || this.categoryForm.invalid) {
      return;
    }

    this.#beginWrite();

    const value = this.categoryForm.getRawValue();
    const id = this.editingCategoryId();
    const outcome =
      id === null
        ? await this.categories.addCategory(value)
        : await this.categories.updateCategory(id, {
            description: value.description,
            name: value.name,
          });

    this.#render('category', this.categoryForm, outcome);

    if (outcome.state !== 'recorded') {
      return;
    }

    this.cancelCategoryEdit();
  }

  protected editCategory(category: CategoryView): void {
    if (!categoryIsReadable(category)) {
      return;
    }

    // No `disable()` here and no `enable()` in the cancel below: the effect in
    // the constructor derives the picker's state from this signal and owns it
    // alone. Setting it here as well is what put three writers on one control.
    this.editingCategoryId.set(category.id);
    this.categoryForm.setValue({
      categoryGroupId: category.categoryGroupId,
      description:
        category.description === null ? '' : category.description.value,
      name: category.name.value,
    });
  }

  protected cancelCategoryEdit(): void {
    this.editingCategoryId.set(null);
    this.categoryForm.reset({
      name: '',
      description: '',
      categoryGroupId: '',
    });
  }

  protected async removeCategory(category: CategoryView): Promise<void> {
    this.#beginWrite();
    this.#renderRowAct(
      await this.categories.removeCategory(category.id),
      'removal',
    );
  }

  // **The two drag handlers are `async`, which is the one thing a reader will
  // find surprising here.** A CDK drop handler answering a promise is
  // ordinary — the event is dispatched and nothing waits on the result — and
  // the alternative is the shape this whole change exists to remove: a write
  // whose refusal reaches nowhere.
  protected async dropGroup(
    event: CdkDragDrop<
      readonly CategoryGroupView[],
      readonly CategoryGroupView[],
      CategoryGroupView
    >,
  ): Promise<void> {
    this.#beginWrite();
    this.#renderRowAct(
      await this.categories.moveGroup(event.item.data.id, event.currentIndex),
      'placement',
    );
  }

  protected async dropCategory(
    event: CdkDragDrop<
      readonly CategoryView[],
      readonly CategoryView[],
      CategoryView
    >,
    categoryGroupId: string,
  ): Promise<void> {
    this.#beginWrite();
    this.#renderRowAct(
      await this.categories.placeCategory(
        event.item.data.id,
        categoryGroupId,
        event.currentIndex,
      ),
      'placement',
    );
  }

  protected categoryListId(categoryGroupId: string): string {
    return `category-list-${categoryGroupId}`;
  }

  // The two rename gates, re-exported for the template because a template
  // cannot import. Each is the imported predicate and never a second copy of
  // its condition: a copy here would be the one that drifts, and the drift is
  // silent in the direction that loses a note.
  protected groupIsReadable(group: CategoryGroupView): boolean {
    return categoryGroupIsReadable(group);
  }

  protected categoryIsReadable(category: CategoryView): boolean {
    return categoryIsReadable(category);
  }

  // The last write's field messages, but only for the form that made it. Empty
  // for the other, which is what stops one server sentence appearing under both
  // `name` controls at once.
  #messagesFor(
    surface: WritingSurface,
  ): ReadonlyMap<string, readonly string[]> | null {
    const write = this.#write();

    return write?.surface === surface ? write.report.fields : null;
  }

  // The one thing that clears the last write's account of itself, and it clears
  // **both** forms: the region and the two mat-error sets are one report, so a
  // group write starting has to take a category write's messages down with it.
  #beginWrite(): void {
    this.#write.set(null);
    clearFieldMessages(this.groupForm);
    clearFieldMessages(this.categoryForm);
  }

  // Publishes the answer to a write no form made.
  //
  // **Filed under no surface, and nothing is marked on a control.** The report
  // carries no fields on any word — `rowActReportOf` has no lookup to place one
  // with — and the `null` surface is what says so about the *press* rather than
  // about this particular answer. No focus moves either: there is no control
  // carrying a message to move it to, and taking a keyboard user off the row
  // they are working on would be the opposite of what the chapter asks.
  #renderRowAct(outcome: WriteOutcome, act: RowAct): void {
    this.#write.set({ report: rowActReportOf(outcome, act), surface: null });
  }

  // Publishes one write's answer, and moves focus where the chapter puts it:
  // to the first control carrying a message, and nowhere at all when none
  // does. `accounts.component.ts` argues both halves at its own copy.
  #render(
    surface: WritingSurface,
    form: FormGroup,
    outcome: WriteOutcome,
  ): void {
    const report = writeReportOf(outcome, placeableKeys(surface));

    this.#write.set({ report, surface });

    const first = markFieldMessages(form, report);

    if (first === null) {
      return;
    }

    // **Scoped to the form that was written, through the surface name the two
    // `<form>` elements carry.** Both forms hold a control called `name`, so a
    // host-wide lookup focuses whichever comes first in the document — which is
    // the group's, on every refusal of the category form. The attribute is what
    // makes the two tellable apart without a positional selector that the next
    // layout change would silently invert.
    this.host.nativeElement
      .querySelector<HTMLElement>(
        `[data-surface="${surface}"] [formcontrolname="${first}"]`,
      )
      ?.focus();
  }
}
