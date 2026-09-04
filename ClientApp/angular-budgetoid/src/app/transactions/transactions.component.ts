// The transactions screen, and the second surface in this product to render
// values it had to open and to refuse writing ones it cannot seal.
//
// **The locked treatment is `docs/design/components.md`, "The locked account",
// and it is the same three things the accounts screen ships.** The list is
// replaced by `locked-account-notice` — not hidden, and not a route guard. The
// form is **DOM-disabled**, because an enabled form submits, the service
// refuses because it cannot seal, and nothing happens, which reads as a failure
// rather than as a limitation. And the reason is a sentence beside the form,
// because a disabled control with no explanation is a dead end.
//
// **Two predicates over one status, and they are not the same question.**
// {@link TransactionsComponent.writable} is `=== 'unlocked'`, written
// **positively** so that `unlocking` and any word added later arrive disabled —
// loud and harmless — rather than live and silent.
// {@link TransactionsComponent.locked} is `=== 'locked'` exactly, because the
// notice's sentence is *advice* and that advice is already wrong for somebody
// whose unlock is running. Disable when unsure; do not advise when unsure.
//
// **The lock is named four times and none of them is redundant.** A disabled
// form's status is `DISABLED`, which excludes it from validation and makes
// `form.invalid` answer **false** — so `[disabled]="form.invalid"` alone
// *enables* the submit button the moment the form is switched off. It is named
// on the form (the `effect`), again on the control, and again in the handler,
// because Material's click-halt is applied to anchors only and a `<button>`
// still receives the press. The fourth is the three lists this form renders:
// disabling a control does not stop it displaying what it already opened, and
// a `mat-select` goes on showing the selected option's text however dead it
// is, so the pickers and the suggestions are emptied by `locked()` as well.
//
// **The running flag is on the submit and in the handler, and it is the
// duplicate-press guard the docs promise.** The write is two round trips, and
// a second press mints a fresh row id that collides with nothing — the server
// cannot tell what lands from a deliberate second entry. What used to block it
// was an accident of the reset clearing a `required` account in the same tick,
// which is gone now that the reset waits for an outcome.
//
// **The form is emptied only by a write that landed, and a failed read has a
// line of its own under it.** `TransactionsService.add` answers a word: five of
// its paths write nothing, and clearing the fields on the line after the call
// destroys what somebody typed before the outcome exists. The line is the same
// argument on the read: the list is `null` at rest, in flight and after a
// failure, so a failed read used to render nothing whatever — a form on top
// and silence beneath, which reads as an account with no entries.
//
// **Both that line and the loading one live in a `role="status"` region that is
// in the DOM from first paint**, the rule `docs/design/components.md` states
// under "A value read from the network", and the reason it has to be *from
// first paint* is that a live region created together with its text is
// announced by nothing — assistive technology has to have been watching the
// node already. The two sentences used to be the last two branches of the chain
// below, which meant each of them arrived with its own node and neither was
// ever announced. What stops that coming back is a spec case that takes the
// node while it is silent and asserts the later text lands in that same
// element, because a case asserting only that the sentence is *somewhere* on
// screen passes either way.
//
// **The rename rule the accounts screen carries has no surface here.** A
// transaction row offers no Edit and no Delete, so there is no control to
// disable on a row whose values did not open; that rule arrives with the edit
// screen rather than being anticipated by a control nobody can press.
//
// **Two non-blank validators, and the payee's is the load-bearing one.** The
// service decides "no note" and "no payee" on `=== ''` exactly, because the
// client may not alter what it seals — a trimmed seal beside an untrimmed index
// keys a row to a value nothing looks up. So the rule that a value is not just
// spaces moved here, where refusing is all it does. On the note, `'   '` would
// be stored as a note of three spaces: untidy. On the payee it is worse and
// silent: the index normalization trims, so `'   '` keys to the index of the
// **empty** name — every blank payee in the budget becomes one counterparty,
// and the row this browser created for it holds three spaces nobody will ever
// search for.
//
// **The autocomplete suggests only payees whose names opened, and that is a
// filter rather than a collapse.** An autocomplete offers text to put into a
// text field and a row with no text has nothing to offer; no word is turned
// into a string anywhere, the row is simply not a suggestion.
//
// **The category picker renders words now, through the categories screen's own
// view models.** It used to print base64url, because `category_groups.name` and
// `categories.name` are sealed columns and nothing owned a mapper for either.
// The fix was never a transform belonging to this folder: a second one would be
// a second definition of the bindings those envelopes were sealed against, and
// `categories.categoryGroupName` is opened under the **group's** identifier,
// which is the mistake a local copy makes first. `TransactionsService` imports
// `toCategoryGroupView` and `toCategoryView` and declares neither.
//
// **The group label carries no `[label]` binding, and that is forced.** The
// input takes a `string`, and a group's name is a word — collapsing it to one
// on the way in is the thing "The locked account" forbids. `MatOptgroup`
// projects its default slot inside the label element, so the value goes in as
// content.
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  FormBuilder,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatSelectModule } from '@angular/material/select';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import {
  NARRATIVE_DESCRIPTION_CHARACTERS,
  NARRATIVE_NAME_CHARACTERS,
} from '@app-shared/narrative-field-caps';
import { AccountsService } from '../accounts/accounts.service';
import { TransactionsService } from './transactions.service';

/** One payee offered for completion: a row whose name this browser could read. */
interface PayeeSuggestion {
  readonly id: string;
  readonly name: string;
}

/**
 * Refuses a value that is present and entirely whitespace, and admits an empty
 * one.
 *
 * It trims **to judge** and never to alter: what the service seals is the
 * control's own value, character for character, and a validator that wrote a
 * trimmed value back would reintroduce the defect it exists to close. Empty is
 * admitted because both fields it guards are optional — `''` is how this screen
 * says "no note" and "no payee", and refusing it would make an optional field
 * mandatory.
 */
function nonBlankWhenPresent(
  control: AbstractControl,
): ValidationErrors | null {
  const value: unknown = control.value;

  return typeof value === 'string' &&
    value.length > 0 &&
    value.trim().length === 0
    ? { blank: true }
    : null;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [provideNativeDateAdapter()],
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatListModule,
    MatSelectModule,
    LockedAccountNoticeComponent,
    NarrativeValueComponent,
  ],
  styles: `
    :host {
      display: block;
      padding: 1.5rem 2rem;
    }

    form {
      display: grid;
      gap: 1rem;
      max-width: 32rem;
      margin-bottom: 2rem;
    }

    .reason {
      margin: 0;
      color: var(--bud-text-muted);
    }
  `,
  template: `
    <h1>Transactions</h1>

    <form [formGroup]="form" (ngSubmit)="add()">
      @if (!writable()) {
        <!--
          The reason, beside the form rather than on it. A disabled control
          whose explanation is a tooltip is an explanation nobody hears, and
          this is a capability the tab has temporarily lost rather than one the
          product does not have — so the sentence names the press that returns
          it.
        -->
        <p class="reason">
          Adding is off while this tab can’t read your account. Press Unlock in
          Settings to turn it back on.
        </p>
      }

      <mat-form-field>
        <mat-label>Amount</mat-label>
        <input matInput type="number" step="0.01" formControlName="amount" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Date</mat-label>
        <input matInput [matDatepicker]="picker" formControlName="date" />
        <mat-datepicker-toggle matIconSuffix [for]="picker" />
        <mat-datepicker #picker />
      </mat-form-field>

      <!--
        The three controls that render opened names, and they leave the DOM
        while the account is locked rather than merely being switched off.
        Emptying their lists is not enough and that is measured: a mat-select
        goes on displaying the option it had selected after the option is
        gone, so a lock landing over a filled form left an account's name on a
        dead control while the notice underneath said this tab cannot read the
        account. The notice renders **in place of** account content, and a
        disabled control still showing it is the same claim by another route.
        locked() exactly, matching the notice rather than !writable(): an
        unlock in flight is not a reason to take a screen away from somebody
        who is looking at it.
      -->
      @if (!locked()) {
        <mat-form-field>
          <mat-label>Account</mat-label>
          <mat-select formControlName="accountId">
            @for (account of accounts.accounts() ?? []; track account.id) {
              <mat-option [value]="account.id">
                <app-narrative-value [value]="account.name" />
              </mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

      <!--
        \`?.length === 0\` and never \`(… ?? []).length === 0\`: a null list is
        "no answer yet", and saying "create an account" over a read that never
        landed is a claim about the budget rather than about the request. The
        submit's own clause reads it the same way, deliberately — the two used
        to disagree about \`null\`, and they disagreed toward silence: a failed
        accounts read hid this sentence and disabled the button, so the screen
        went quiet and dead at once. Nothing becomes pressable that a person
        could not have filled in: the account control is \`required\`, so an
        empty picker keeps the form invalid on its own.
      -->
      @if (!accounts.loading() && accounts.accounts()?.length === 0) {
        <p>Create an account before adding transactions.</p>
      }

      <mat-form-field>
        <mat-label>Description</mat-label>
        <!--
          Both caps are bound rather than typed, so an attribute and the
          validator beside it cannot drift apart, and so the numbers stay beside
          the byte caps they protect — @app-shared/narrative-field-caps holds
          the whole argument. The two differ here because the note is a
          description column and the counterparty is a name column, which is the
          same split the server's byte caps are written over.
        -->
        <input
          matInput
          formControlName="description"
          [attr.maxlength]="descriptionCharacters"
        />
      </mat-form-field>

      @if (!locked()) {
        <mat-form-field>
          <mat-label>Payee</mat-label>
          <input
            matInput
            formControlName="payee"
            [attr.maxlength]="nameCharacters"
            [matAutocomplete]="payeeAutocomplete"
          />
          <mat-autocomplete #payeeAutocomplete="matAutocomplete">
            @for (payee of filteredPayees(); track payee.id) {
              <mat-option [value]="payee.name">{{ payee.name }}</mat-option>
            }
          </mat-autocomplete>
        </mat-form-field>
      }

      @if (!locked()) {
        <mat-form-field>
          <mat-label>Category</mat-label>
          <mat-select formControlName="categoryId">
            <mat-option [value]="''">None</mat-option>
            @for (group of transactions.categoryGroups(); track group.id) {
              <!--
                No label binding, and that is forced rather than chosen: the
                input takes a string and a group's name is a **word** — the
                text, or the reason there is none — so binding it would mean
                collapsing a locked or unreadable value into a string on the
                way in, which is the one thing docs/design/components.md
                forbids under "The locked account". MatOptgroup's template
                interpolates its label input and then projects its **default**
                slot inside the same label element, with a second slot
                selecting mat-option for the options — so the marker lands in
                the label and the options stay where they were. Measured
                against the shipped template, not assumed.
              -->
              <mat-optgroup>
                <app-narrative-value [value]="group.name" />
                @for (
                  category of transactions.categoriesForGroup(group.id);
                  track category.id
                ) {
                  <mat-option [value]="category.id">
                    <app-narrative-value [value]="category.name" />
                  </mat-option>
                }
              </mat-optgroup>
            }
          </mat-select>
        </mat-form-field>
      }

      <!--
        !writable() first in the disabled expression, and it is not redundant: a
        disabled form's status is DISABLED, so form.invalid answers false and
        this control would stay pressable over a form nobody can type into.

        transactions.loading() is the duplicate-press guard, and it is not
        cosmetic: this write is two round trips, and the second press mints a
        fresh row id that collides with nothing, so what lands is a second
        transaction the server has no way to tell from a deliberate one. What
        blocked it before was an accident — the form reset cleared a required
        account in the same tick — and the accident is gone now that the reset
        waits for an outcome.
      -->
      <button
        mat-flat-button
        color="primary"
        type="submit"
        [disabled]="
          !writable() ||
          form.invalid ||
          transactions.loading() ||
          accounts.accounts()?.length === 0
        "
      >
        Add transaction
      </button>
    </form>

    @if (locked()) {
      <!--
        In place of the list, never over it and never as a redirect. Settings
        holds the way out, so nothing here may take a person off this screen.
      -->
      <app-locked-account-notice />
    } @else if (transactions.transactions(); as list) {
      <mat-list>
        @for (transaction of list; track transaction.id) {
          <mat-list-item>
            <span matListItemTitle>
              <app-narrative-value [value]="transaction.description" />
            </span>
            <span matListItemLine>
              <!--
                Each member is rendered by the component that knows the four
                shapes a narrative value comes in. A member interpolated
                straight into this row prints an object, and a member collapsed
                to '' or a dash on the way here makes the screen claim
                something about the account when the truth is about this tab.
              -->
              <app-narrative-value [value]="transaction.accountName" />
              @if (transaction.payeeName) {
                <span>&nbsp;·&nbsp;</span>
                <app-narrative-value [value]="transaction.payeeName" />
              }
              @if (transaction.categoryGroupName) {
                <span>&nbsp;·&nbsp;</span>
                <app-narrative-value [value]="transaction.categoryGroupName" />
              }
              @if (transaction.categoryName) {
                <span>&nbsp;·&nbsp;</span>
                <app-narrative-value [value]="transaction.categoryName" />
              }
              <!--
                The angle brackets hug the text at both ends, which is not a
                formatting quirk: whitespace inside this span is **rendered**.
                Angular collapses a run of whitespace to one space rather than
                dropping it, so an opening tag followed by a newline puts a
                space in front of the &amp;nbsp; and this separator arrives
                twice as wide as its three neighbours — on every row, with a
                category and without. Prettier owns the wrapping here and will
                re-break a long line; this is the form it settles on, so the
                fix survives npm run format rather than being undone by it.
              -->
              <span
                >&nbsp;· {{ transaction.date }} · {{ transaction.currencySymbol
                }}{{ transaction.amount }}</span
              >
            </span>
          </mat-list-item>
        } @empty {
          <mat-list-item>No transactions yet.</mat-list-item>
        }
      </mat-list>
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

      Which of the two lines it carries is one word off readState(), never two
      conditions compared here, so loading and failure are exclusive by
      structure rather than by the order somebody happened to write the
      branches in.
    -->
    <div role="status">
      @if (readState() === 'loading') {
        <p class="reason">Reading your transactions…</p>
      } @else if (readState() === 'failed') {
        <p class="reason">
          We couldn’t read your transactions. Check your connection and reload
          the page.
        </p>
      }
    </div>
  `,
})
export class TransactionsComponent implements OnInit {
  protected readonly transactions = inject(TransactionsService);
  protected readonly accounts = inject(AccountsService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly custody = inject(AccountKeyCustodyService);

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
   * Whether the notice replaces the list.
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
   * `null` while the account is locked and `null` while a list is on screen:
   * the notice and the list are this section's value, and the region speaks
   * only for a read with no value to show. `loading` outranks `failed` for the
   * reason the branch order used to carry: a reload started after one failed
   * read would otherwise keep the failure sentence up throughout it.
   *
   * It speaks for the **transactions** read alone. This screen makes a second
   * read — the accounts the form's picker is filled from — and that one says
   * nothing here: its only sentence is the *Create an account first* line
   * beside the form, which is a fact about the budget rather than about a
   * request. Folding the two would put one line in front of somebody over two
   * different requests, and the wrong one of them.
   */
  protected readonly readState = computed<'loading' | 'failed' | null>(() => {
    if (this.locked() || this.transactions.transactions() !== null) {
      return null;
    }

    if (this.transactions.loading()) {
      return 'loading';
    }

    return this.transactions.failed() ? 'failed' : null;
  });

  // What has been typed into the payee field, as a signal. Fed from
  // `valueChanges` rather than read off the control, because a control is not
  // reactive and a `computed` over one never recomputes.
  readonly #payeeFilter = signal('');

  /**
   * The payees offered for completion: the rows this browser could read, that
   * match what has been typed, and none at all while the account is locked.
   *
   * A `computed` over a signal fed from the control, and not a method: a
   * method call in a template runs on every change-detection tick and hands
   * back a fresh array each time, so nothing downstream can tell one answer
   * from the next.
   *
   * **The lock is named here as well as on the control's `@if`, and the two
   * are not one rule written twice.** The `@if` is what takes the field out of
   * the DOM; this is what keeps the *list* empty, and it is the half a spec
   * can read — the panel renders into an overlay only once the field is
   * focused, which a disabled or absent control never is.
   */
  protected readonly filteredPayees = computed<readonly PayeeSuggestion[]>(
    () => {
      if (this.locked()) {
        return [];
      }

      const suggestions = (this.transactions.payees() ?? []).flatMap((payee) =>
        payee.name.state === 'text'
          ? [{ id: payee.id, name: payee.name.value }]
          : [],
      );
      const filter = this.#payeeFilter().trim().toLocaleLowerCase();

      if (!filter) {
        return suggestions;
      }

      return suggestions.filter((payee) =>
        payee.name.toLocaleLowerCase().includes(filter),
      );
    },
  );

  /**
   * The caps the template's `maxlength` attributes read, and the same values
   * the two validators below are built from.
   *
   * `@app-shared/narrative-field-caps` argues them: these are UX ceilings in
   * UTF-16 code units, and what makes each safe is that it cannot seal past its
   * column's byte cap even when every unit is a three-byte character. The note
   * takes the description cap and the counterparty the name one, because
   * `transactions.description` and `payees.name` are fields of those two
   * classes.
   */
  protected readonly nameCharacters = NARRATIVE_NAME_CHARACTERS;
  protected readonly descriptionCharacters = NARRATIVE_DESCRIPTION_CHARACTERS;

  protected readonly form = this.formBuilder.nonNullable.group({
    amount: [0, [Validators.required]],
    date: [new Date(), [Validators.required]],
    accountId: ['', [Validators.required]],
    // `nonBlankWhenPresent` beside the length cap and not instead of it: both
    // fields are optional, so `''` has to pass, and `'   '` must not.
    description: [
      '',
      [
        Validators.maxLength(NARRATIVE_DESCRIPTION_CHARACTERS),
        nonBlankWhenPresent,
      ],
    ],
    payee: [
      '',
      [Validators.maxLength(NARRATIVE_NAME_CHARACTERS), nonBlankWhenPresent],
    ],
    categoryId: [''],
  });

  constructor() {
    // Disabled through the form itself, because Material's click-halt is
    // applied to anchors only: on a `<button>`, `disabledInteractive` leaves
    // the DOM `disabled` false and the click still arrives.
    effect(() => {
      if (this.writable()) {
        this.form.enable({ emitEvent: false });
      } else {
        this.form.disable({ emitEvent: false });
      }
    });

    // The one writer of the filter. `emitEvent: false` on the two calls above
    // is what keeps enabling and disabling the form out of it; a reset does
    // emit, which is right — the field is empty again and the list is the
    // whole of it.
    this.form.controls.payee.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe((typed) => this.#payeeFilter.set(typed));
  }

  public ngOnInit(): void {
    this.accounts.load();
    this.transactions.load();
    this.transactions.loadPayees();
    this.transactions.loadCategories();
  }

  protected async add(): Promise<void> {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here. The running flag is named for the same reason
    // and answers a different question: this write is two round trips, and a
    // second one mints a fresh row id that collides with nothing.
    if (!this.writable() || this.form.invalid || this.transactions.loading()) {
      return;
    }

    const value = this.form.getRawValue();

    // Handed over exactly as typed. The service seals this text and indexes the
    // same string; a `.trim()` on this line would make the two disagree.
    const outcome = await this.transactions.add({
      accountId: value.accountId,
      amount: value.amount,
      // `null` and never `''`: the route binds a `Guid?`, and the empty string
      // is only the picker's own word for "none".
      categoryId: value.categoryId === '' ? null : value.categoryId,
      date: this.toDateOnlyString(value.date),
      description: value.description,
      payee: value.payee,
    });

    // **Only a write that landed empties the form.** Five of the service's
    // paths end with nothing on the server, four of them without any request
    // at all, and one of those — a counterparty whose own name does not open —
    // abandons every write naming it, permanently. Clearing the field on the
    // way past destroys what somebody typed before the outcome exists, and the
    // screen has nothing to tell them with.
    if (outcome.state !== 'recorded') {
      return;
    }

    this.form.reset({
      amount: 0,
      date: new Date(),
      accountId: '',
      description: '',
      payee: '',
      categoryId: '',
    });
  }

  private toDateOnlyString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');

    return `${year}-${month}-${day}`;
  }
}
