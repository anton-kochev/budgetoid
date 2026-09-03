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
// **The lock is named three times and none of them is redundant.** A disabled
// form's status is `DISABLED`, which excludes it from validation and makes
// `form.invalid` answer **false** — so `[disabled]="form.invalid"` alone
// *enables* the submit button the moment the form is switched off. It is named
// on the form (the `effect`), again on the control, and again in the handler,
// because Material's click-halt is applied to anchors only and a `<button>`
// still receives the press.
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
} from '@angular/core';
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

      <!--
        \`?.length === 0\` and never \`(… ?? []).length === 0\`: a null list is
        "no answer yet", and saying "create an account" over a read that never
        landed is a claim about the budget rather than about the request.
      -->
      @if (!accounts.loading() && accounts.accounts()?.length === 0) {
        <p>Create an account before adding transactions.</p>
      }

      <mat-form-field>
        <mat-label>Description</mat-label>
        <input matInput formControlName="description" maxlength="500" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Payee</mat-label>
        <input
          matInput
          formControlName="payee"
          maxlength="200"
          [matAutocomplete]="payeeAutocomplete"
        />
        <mat-autocomplete #payeeAutocomplete="matAutocomplete">
          @for (payee of filteredPayees(); track payee.id) {
            <mat-option [value]="payee.name">{{ payee.name }}</mat-option>
          }
        </mat-autocomplete>
      </mat-form-field>

      <mat-form-field>
        <mat-label>Category</mat-label>
        <mat-select formControlName="categoryId">
          <mat-option [value]="''">None</mat-option>
          @for (group of transactions.categoryGroups(); track group.id) {
            <!--
              No label binding, and that is forced rather than chosen: the input
              takes a string and a group's name is a **word** — the text, or the
              reason there is none — so binding it would mean collapsing a
              locked or unreadable value into a string on the way in, which is
              the one thing docs/design/components.md forbids under "The locked
              account". MatOptgroup's template interpolates its label input and
              then projects its **default** slot inside the same label element,
              with a second slot selecting mat-option for the options — so the
              marker lands in the label and the options stay where they were.
              Measured against the shipped template, not assumed.
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

      <!--
        !writable() first in the disabled expression, and it is not redundant: a
        disabled form's status is DISABLED, so form.invalid answers false and
        this control would stay pressable over a form nobody can type into.
      -->
      <button
        mat-flat-button
        color="primary"
        type="submit"
        [disabled]="
          !writable() ||
          form.invalid ||
          (accounts.accounts()?.length ?? 0) === 0
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
              <span>
                &nbsp;· {{ transaction.date }} · {{ transaction.currencySymbol
                }}{{ transaction.amount }}
              </span>
            </span>
          </mat-list-item>
        } @empty {
          <mat-list-item>No transactions yet.</mat-list-item>
        }
      </mat-list>
    } @else if (transactions.loading()) {
      <!--
        The list is null at rest, in flight and after a failure, so the loading
        line is read off the published running state rather than off the absent
        value. "No transactions yet" belongs to a server that answered.
      -->
      <p class="reason">Reading your transactions…</p>
    }
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

  protected readonly form = this.formBuilder.nonNullable.group({
    amount: [0, [Validators.required]],
    date: [new Date(), [Validators.required]],
    accountId: ['', [Validators.required]],
    // `nonBlankWhenPresent` beside the length cap and not instead of it: both
    // fields are optional, so `''` has to pass, and `'   '` must not.
    description: ['', [Validators.maxLength(500), nonBlankWhenPresent]],
    payee: ['', [Validators.maxLength(200), nonBlankWhenPresent]],
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
  }

  public ngOnInit(): void {
    this.accounts.load();
    this.transactions.load();
    this.transactions.loadPayees();
    this.transactions.loadCategories();
  }

  protected filteredPayees(): readonly PayeeSuggestion[] {
    const filter = this.form.controls.payee.value.trim().toLocaleLowerCase();
    const suggestions = (this.transactions.payees() ?? []).flatMap((payee) =>
      payee.name.state === 'text'
        ? [{ id: payee.id, name: payee.name.value }]
        : [],
    );

    if (!filter) {
      return suggestions;
    }

    return suggestions.filter((payee) =>
      payee.name.toLocaleLowerCase().includes(filter),
    );
  }

  protected add(): void {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here.
    if (!this.writable() || this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();

    // Handed over exactly as typed. The service seals this text and indexes the
    // same string; a `.trim()` on this line would make the two disagree.
    void this.transactions.add({
      accountId: value.accountId,
      amount: value.amount,
      // `null` and never `''`: the route binds a `Guid?`, and the empty string
      // is only the picker's own word for "none".
      categoryId: value.categoryId === '' ? null : value.categoryId,
      date: this.toDateOnlyString(value.date),
      description: value.description,
      payee: value.payee,
    });
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
