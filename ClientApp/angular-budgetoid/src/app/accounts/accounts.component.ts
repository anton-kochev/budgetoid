// The accounts screen, and the first surface in this product to render a value
// it had to open and to refuse writing one it cannot seal.
//
// **The locked treatment is `docs/design/components.md`, "The locked account",
// and it is three separate things.** The list is replaced by
// `locked-account-notice` — not hidden, and not a route guard, which would be
// synchronous against a fact with no resolution on the navigation path and
// would put key state where `AccountUnlockService` is built to keep it out of.
// The form is **DOM-disabled**, because an enabled form submits, the service
// refuses because it cannot seal, and nothing happens — which reads as a
// failure rather than as a limitation. And the reason is a sentence beside the
// form, because a disabled control with no explanation is a dead end.
//
// **The predicate is `custody.status()` and never the views.** Derived from the
// rows, it could not answer before a load landed and would say nothing at all
// about an account with no rows in it.
//
// **There are two predicates over that one status, and they are not the same
// question.** `AccountKeyStatus` has three words — `locked`, `unlocking`,
// `unlocked` — and each render picks its own.
//
//   * The **form** follows {@link AccountsComponent.writable}, written
//     **positively** as `=== 'unlocked'` and never as `!== 'locked'`. The
//     negative form leaves the form live for the whole ceremony, and every
//     save made in that window is refused by a service that cannot seal —
//     silently, where nobody is looking. The positive form is fail-safe in the
//     direction that matters: a fourth word added later arrives **disabled**,
//     which is loud and harmless, where the negative form would arrive enabled
//     and quiet. That asymmetry is the whole argument, and it is why this
//     predicate may not be "simplified" into its complement.
//   * The **notice** follows {@link AccountsComponent.locked}, which is
//     `=== 'locked'` exactly. Its sentence tells somebody to go and press
//     Unlock, and that advice is already wrong for a person whose unlock is
//     running — and would be a guess for any state nobody has thought of yet.
//     During `unlocking` the list stays, and every name in it renders as the
//     `locked` marker `narrative-value` draws, which is true rather than
//     advisory.
//
// Written as two computeds rather than one, because folding them would make
// one of the two mistakes above unavoidable.
//
// **A row whose name did not open cannot be edited.** The field would prefill
// empty — there is no text to put in it — and the save would seal a blank over
// a name that is still sitting in the column. That is a deletion wearing an
// edit's clothes, so the control is disabled with the reason beside it, the way
// the form is under a locked account. Deleting such a row stays available: a
// person who can see the row is entitled to remove it, and removing is not
// rewriting.
//
// **The non-blank validator is what the removed `.trim()` was accidentally
// doing.** The service may not alter what it seals — a trimmed seal beside an
// untrimmed index keys a row to a value nothing looks up — so the rule moved
// here, where refusing is all it does. `Validators.required` admits `'   '` on
// its own, so it needs the neighbour below.
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import {
  FormBuilder,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { AccountType } from '@app-core/api/account-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import type { AccountView } from './account-view';
import {
  CurrencyApiService,
  CurrencyDto,
} from '@app-core/api/currency-api.service';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatSelectModule } from '@angular/material/select';
import { AccountsService } from './accounts.service';

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

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
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

    .actions {
      display: flex;
      gap: 0.5rem;
    }

    .reason {
      margin: 0;
      color: var(--bud-text-muted);
    }
  `,
  template: `
    <h1>Accounts</h1>

    <form [formGroup]="form" (ngSubmit)="save()">
      @if (!writable()) {
        <!--
          The reason, beside the form rather than on it. A disabled control
          whose explanation is a tooltip is an explanation nobody hears, and
          this is a capability the tab has temporarily lost rather than one the
          product does not have — so the sentence names the press that returns
          it.
        -->
        <p class="reason">
          Adding and editing are off while this tab can’t read your account.
          Press Unlock in Settings to turn them back on.
        </p>
      }

      <mat-form-field>
        <mat-label>Name</mat-label>
        <input matInput formControlName="name" maxlength="200" />
      </mat-form-field>

      <mat-form-field>
        <mat-label>Type</mat-label>
        <mat-select formControlName="type">
          @for (type of accountTypes; track type) {
            <mat-option [value]="type">{{ type }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <mat-form-field>
        <mat-label>Opening balance</mat-label>
        <input
          matInput
          type="number"
          step="0.01"
          formControlName="openingBalance"
        />
      </mat-form-field>

      @if (!editingId()) {
        <mat-form-field>
          <mat-label>Currency</mat-label>
          <mat-select formControlName="currencyCode" required>
            @for (currency of currencies(); track currency.code) {
              <mat-option [value]="currency.code">
                {{ currency.code }} · {{ currency.name }} ({{
                  currency.symbol
                }})
              </mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

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
          [disabled]="!writable() || form.invalid || accounts.loading()"
        >
          {{ editingId() ? 'Save account' : 'Add account' }}
        </button>
        @if (editingId()) {
          <button mat-button type="button" (click)="cancelEdit()">
            Cancel
          </button>
        }
      </div>
    </form>

    @if (locked()) {
      <!--
        In place of the list, never over it and never as a redirect. Settings
        holds the way out, so nothing here may take a person off this screen.
      -->
      <app-locked-account-notice />
    } @else if (accounts.accounts(); as list) {
      <mat-list>
        @for (account of list; track account.id) {
          <mat-list-item>
            <span matListItemTitle>
              <app-narrative-value [value]="account.name" />
            </span>
            <span matListItemLine>
              {{ account.type }} · {{ account.currencyCode }} · Opening:
              {{ account.currencySymbol }}{{ account.openingBalance }}
            </span>
            @if (account.name.state !== 'text') {
              <!--
                The reason in the row, not in a tooltip: an explanation nobody
                hears is not an explanation. Rewriting a value that cannot be
                read would seal a blank over a name still sitting in the
                column, so the control is off rather than merely unhelpful.
              -->
              <span matListItemLine class="reason">
                This name can’t be read here, so it can’t be renamed.
              </span>
            }
            <span matListItemMeta class="actions">
              <!--
                Deleting stays available on the same row. A person looking at a
                row they cannot read is entitled to remove it, and removing is
                not rewriting.
              -->
              <button
                mat-button
                type="button"
                [disabled]="account.name.state !== 'text'"
                (click)="edit(account)"
              >
                Edit
              </button>
              <button mat-button type="button" (click)="remove(account)">
                Delete
              </button>
            </span>
          </mat-list-item>
        } @empty {
          <mat-list-item>No accounts yet.</mat-list-item>
        }
      </mat-list>
    } @else if (accounts.loading()) {
      <!--
        The list is null at rest, in flight and after a failure, so the
        loading line is read off the published running state rather than off
        the absent value. "No accounts yet" belongs to a server that answered.
      -->
      <p class="reason">Reading your accounts…</p>
    }
  `,
})
export class AccountsComponent implements OnInit {
  protected readonly accounts = inject(AccountsService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly currencyApi = inject(CurrencyApiService);
  private readonly custody = inject(AccountKeyCustodyService);

  /**
   * Whether this screen may write.
   *
   * **Positive on purpose, and never `!== 'locked'`** — the head of this file
   * argues it. `unlocking` and any word added later are not `unlocked`, so
   * they arrive disabled, which is the direction a state nobody thought about
   * has to fail in.
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
  protected readonly accountTypes: AccountType[] = [
    'Checking',
    'Savings',
    'Cash',
    'CreditCard',
  ];
  protected readonly editingId = signal<string | null>(null);
  protected readonly currencies = signal<CurrencyDto[]>([]);

  protected readonly form = this.formBuilder.nonNullable.group({
    // `nonBlank` beside `required`, not instead of it: `required` refuses an
    // empty control and admits `'   '`, and the trim that used to catch the
    // second is gone from the service on purpose.
    name: ['', [Validators.required, nonBlank, Validators.maxLength(200)]],
    type: ['Checking' as AccountType, [Validators.required]],
    openingBalance: [0, [Validators.required]],
    currencyCode: ['USD', [Validators.required]],
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
    this.currencyApi
      .getCurrencies()
      .subscribe((response) => this.currencies.set(response.items));
  }

  protected save(): void {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here.
    if (!this.writable() || this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();
    const request = {
      name: value.name,
      type: value.type,
      openingBalance: value.openingBalance,
    };
    const editingId = this.editingId();

    if (editingId) {
      void this.accounts.update(editingId, request);
    } else {
      void this.accounts.add({ ...request, currencyCode: value.currencyCode });
    }

    this.cancelEdit();
  }

  protected edit(account: AccountView): void {
    // The gate is in the handler as well as on the control, the rule the
    // recovery-code hand-off states about its own acknowledgement: Material's
    // click-halt is applied to anchors only, so a disabled `<button>` still
    // receives the press that arrives here. Nothing is prefilled and nothing
    // is put into edit mode — a name with no text to show has no edit to
    // start, and the alternative is a blank field that seals over a name still
    // sitting in the column.
    if (account.name.state !== 'text') {
      return;
    }

    this.editingId.set(account.id);
    this.form.setValue({
      name: account.name.value,
      type: account.type,
      openingBalance: account.openingBalance,
      currencyCode: account.currencyCode,
    });
  }

  protected cancelEdit(): void {
    this.editingId.set(null);
    this.form.reset({
      name: '',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
  }

  protected remove(account: AccountView): void {
    this.accounts.remove(account.id);
  }
}
