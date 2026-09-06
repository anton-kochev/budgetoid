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
// **The row is `docs/design/components.md`, "Transaction row", and it is two
// lines and five things.** Line 1 is the counterparty with the figure set
// right; line 2 is category · account with the date set right. What that
// replaced drew one muted run of six members with the note as a title, so
// three things left the row and one arrived: the **category group** is gone —
// a fifth sealed name the book names nowhere — the **amount** and the **date**
// left that run for the figures column, and the counterparty stopped being one
// of six and became the row's lead.
//
// **The lead falls back to the note on an *absent* counterparty and never on
// an unreadable one.** `payeeName` is `null` where the column held nothing,
// which this browser can see with no key at all; a row whose payee name failed
// to open still *has* a payee, and falling through there would put a different
// value under the same heading depending on whether a key happened to be held,
// with nothing on screen saying which one arrived. It is the same split the
// "No category" branch makes one line down, which turns on `categoryId` being
// null and never on the name.
//
// **The figure goes through `Intl.NumberFormat`, and the locale is a token
// rather than a constant.** `docs/design/patterns.md` forbids the hand-assembly
// this row shipped — `currencySymbol` printed in front of a raw number, which
// showed `$-20.5` where the rule is an unsigned figure with the currency's own
// minor units. Nothing in this application configures `LOCALE_ID`, so
// production passes `undefined` and gets the reader's own locale, exactly as
// `credential-registration-date.ts` argues for dates; the token exists so a
// spec can name a locale and assert a string instead of asserting whatever the
// machine running it renders.
//
// **Constructing a formatter is inside change detection, so it may not
// throw.** `Intl.NumberFormat` answers a `RangeError` for a currency code that
// is not three letters, and a throw during a template binding abandons the
// whole pass — every section after the list stops rendering, and no `try`
// around a signal read can contain it. The fallback drops the currency style
// and keeps the sign rule.
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
//
// **A refused write is never silent, and this screen had the keeping half
// without the saying half.** `docs/design/components.md`, "A write that does
// not happen", is the authority and `accounts.component.ts` argues the shared
// shape: the server's sentences render beneath the controls they were keyed to,
// everything else takes a line in the region this screen already has, and the
// region stays `status` because politeness belongs to the node rather than to
// the sentence.
//
// **This is the one screen whose write is more than one request, so it is the
// one screen that narrates a write in progress.** The chapter's table gives
// "Recording…" to the payee-then-transaction pair and to nothing else today, in
// `body` `--bud-text` rather than `--bud-over`, because nothing has gone wrong.
// It is read off {@link TransactionsComponent.recording} — the component's own
// flag, raised across the `await` — and never off `TransactionsService.loading`,
// which is also true of every read this screen starts.
//
// **`duplicate-name` reaches a person here and nowhere else in the product.**
// The payee create is the one write that answers a repeated name with a 409,
// and the form's own re-read resolves the ordinary case silently — so the
// sentence is for the payee whose own name did not open, which can never match
// and can never be adopted.
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  InjectionToken,
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
import { MatSelectModule } from '@angular/material/select';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import type { NarrativeText } from '@app-core/security/narrative-text';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import {
  NARRATIVE_DESCRIPTION_CHARACTERS,
  NARRATIVE_NAME_CHARACTERS,
} from '@app-shared/narrative-field-caps';
import {
  RECORDING_SENTENCE,
  clearFieldMessages,
  markFieldMessages,
  writeReportOf,
  type WriteReport,
} from '@app-shared/write-outcome-report';
import type { WriteOutcome } from '@app-core/api/write-outcome';
import { AccountsService } from '../accounts/accounts.service';
import type { TransactionView } from './transaction-view';
import { TransactionsService } from './transactions.service';

/**
 * The locale the figures on this screen are formatted in.
 *
 * **`undefined` in production, which is what asks the runtime for the reader's
 * own.** Nothing in this application configures `LOCALE_ID`, so there is no
 * other honest source — `credential-registration-date.ts` makes the same
 * argument for dates and takes the same shape.
 *
 * It is a token rather than a constant so that a spec can state a locale and
 * assert a formatted string. Without that seam every expectation about a figure
 * is true on the machine it was written on and unproven anywhere else, because
 * the test runner pins the time zone and not the locale.
 */
export const TRANSACTION_ROW_LOCALE = new InjectionToken<string | undefined>(
  'the locale a transaction row formats its figure in',
  { factory: () => undefined, providedIn: 'root' },
);

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

/**
 * The wire keys this form can place a server's sentence on, and the control
 * each goes beneath.
 *
 * **A `Map` and never an object literal**, per the chapter: `constructor`,
 * `toString` and `valueOf` hit on a literal and place a message under a control
 * that does not exist.
 *
 * **`PayeeId` goes under the payee *name* field**, which is the only control
 * its value ever came from — the id is resolved from what was typed there, so
 * that is where a correction is made. `Id` is deliberately absent: the row
 * identifier is minted in the browser and there is no control for it, so a
 * sentence keyed on it is one for the region.
 */
const PLACEABLE_KEYS: ReadonlyMap<string, string> = new Map([
  ['Amount', 'amount'],
  ['Date', 'date'],
  ['AccountId', 'accountId'],
  ['Description', 'description'],
  ['PayeeId', 'payee'],
  ['CategoryId', 'categoryId'],
]);

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

    /*
      A refused write's line in the shared region. --bud-over, and colour is
      never the message: every sentence in the chapter's table reads the same
      with this declaration removed.
    */
    .refusal {
      margin: 0;
      color: var(--bud-over);
    }

    /*
      The line a write in progress carries, and the one region line that is not
      a refusal: body --bud-text, because nothing has gone wrong.
    */
    .recording {
      margin: 0;
      color: var(--bud-text);
    }

    .transactions {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    /*
      The anatomy docs/design/components.md opens "Transaction row" with: two
      lines, [content 1fr] [figures auto], and four cells falling into it in
      DOM order — which is also the order they are read out in. The whole row
      is one target well past the 48px floor.
    */
    .transaction-row {
      position: relative;
      display: grid;
      grid-template-columns: [content] 1fr [figures] auto;
      align-content: center;
      align-items: baseline;
      column-gap: var(--bud-space-4);
      row-gap: var(--bud-space-1);
      min-height: 64px;
      padding: var(--bud-gutter);
    }

    /*
      The hairline, inset to the gutter. A border on the row itself would run
      the full width of it; this is the same rule stopped where the padding
      starts, which is what "inset to the gutter" asks for.
    */
    .transaction-row:not(:last-child)::after {
      content: '';
      position: absolute;
      inset-inline: var(--bud-gutter);
      bottom: 0;
      border-bottom: 1px solid var(--bud-hairline);
    }

    /*
      The press layer the chapter names. There are no swipe actions — a
      transaction is immutable — and there is no edit screen for a press to
      open yet, so this is feedback and nothing more.
    */
    .transaction-row:active {
      background: var(--bud-state-pressed);
    }

    /* Line 1, content column: Inter 500 14 in --bud-text. */
    .lead {
      font-size: 0.875rem;
      font-weight: 500;
      color: var(--bud-text);
    }

    /*
      Line 1, figures column: the figure role, 15 and 500. Tabular comes from
      the global .bud-figures in the theming tokens rather than from a
      declaration here, so every column of digits in the product is aligned by
      one rule.
    */
    .amount {
      font-size: 0.9375rem;
      font-weight: 500;
      text-align: right;
    }

    /*
      Income is the marked case. Colour is not the message — the plus sign says
      it too — so a reader who cannot tell these two apart still reads the row
      correctly.
    */
    .amount.income {
      color: var(--bud-positive-text);
    }

    /* Line 2, content column: body-sm, muted. */
    .meta {
      font-size: 0.8125rem;
      color: var(--bud-text-muted);
    }

    /* Line 2, figures column: caption, muted. */
    .date {
      font-size: 0.75rem;
      color: var(--bud-text-muted);
      text-align: right;
    }

    .no-rows {
      padding: var(--bud-gutter);
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
        <!--
          The server's sentence, rendered verbatim beneath the control it was
          keyed to. The client writes no copy for a field-keyed refusal and
          holds no table of its own — a client-authored lookup would have to be
          total over every string the API can send, and its fallback would be a
          generic sentence standing exactly where somebody is making a
          correction. mat-error rather than a paragraph of this screen's own,
          because the form field is what binds the message to the input with
          aria-describedby and colours the border with it.
        -->
        @for (message of fieldMessages()?.get('amount') ?? []; track $index) {
          <mat-error>{{ message }}</mat-error>
        }
      </mat-form-field>

      <mat-form-field>
        <mat-label>Date</mat-label>
        <input matInput [matDatepicker]="picker" formControlName="date" />
        <mat-datepicker-toggle matIconSuffix [for]="picker" />
        <mat-datepicker #picker />
        @for (message of fieldMessages()?.get('date') ?? []; track $index) {
          <mat-error>{{ message }}</mat-error>
        }
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
          @for (
            message of fieldMessages()?.get('accountId') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
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
        @for (
          message of fieldMessages()?.get('description') ?? [];
          track $index
        ) {
          <mat-error>{{ message }}</mat-error>
        }
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
          <!--
            PayeeId's sentences land here, on the name field, because that is
            the only control the id was ever resolved from.
          -->
          @for (message of fieldMessages()?.get('payee') ?? []; track $index) {
            <mat-error>{{ message }}</mat-error>
          }
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
          @for (
            message of fieldMessages()?.get('categoryId') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
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
      <!--
        A plain semantic list, which is the base docs/design/components.md
        gives this row: "M3 base: none". role="list" is written out because
        list-style: none takes the list semantics away from some screen
        readers, and the whole point of the element is that it has them.
      -->
      <ul class="transactions" role="list">
        @for (transaction of list; track transaction.id) {
          <li class="transaction-row">
            <!--
              Every narrative member is rendered by the component that knows
              the four shapes a narrative value comes in. A member interpolated
              straight into this row prints an object, and a member collapsed
              to '' or a dash on the way here makes the screen claim something
              about the account when the truth is about this tab.
            -->
            <span class="lead">
              <app-narrative-value [value]="leadOf(transaction)" />
            </span>
            <span
              class="amount bud-figures"
              [class.income]="isIncome(transaction)"
              >{{ amountText(transaction) }}</span
            >
            <span class="meta">
              <!--
                "No category" turns on the category being **absent** — a null
                this browser can see with no key at all — and never on a name
                that failed to open, which is a fact about this tab and gets
                the marker in the other arm.
              -->
              @if (transaction.categoryId === null) {
                <span>No category</span>
              } @else {
                <app-narrative-value [value]="transaction.categoryName" />
              }
              <!--
                The angle brackets hug the text at both ends, which is not a
                formatting quirk: whitespace inside this span is **rendered**.
                Angular collapses a run of whitespace to one space rather than
                dropping it, so an opening tag followed by a newline puts a
                space in front of the &amp;nbsp; and the separator arrives
                twice as wide as it should — on every row, with a category and
                without. Prettier owns the wrapping here and will re-break a
                long line; this is the form it settles on, so the fix survives
                npm run format rather than being undone by it.
              -->
              <span>&nbsp;·&nbsp;</span>
              <app-narrative-value [value]="transaction.accountName" />
            </span>
            <span class="date bud-figures">{{ transaction.date }}</span>
          </li>
        } @empty {
          <li class="no-rows">No transactions yet.</li>
        }
      </ul>
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
      conditions compared here, so the read's account of itself, the write in
      progress and the write that answered are exclusive by structure rather
      than by the order somebody happened to write the branches in.
    -->
    <div role="status">
      @if (regionState() === 'loading') {
        <p class="reason">Reading your transactions…</p>
      } @else if (regionState() === 'recording') {
        <p class="recording">{{ recordingSentence }}</p>
      } @else if (regionState() === 'refused') {
        @for (sentence of refusals(); track $index) {
          <p class="refusal">{{ sentence }}</p>
        }
      } @else if (regionState() === 'failed') {
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
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly #locale = inject(TRANSACTION_ROW_LOCALE);

  // One formatter per currency **and per sign rule**, built once and kept.
  // `Intl.NumberFormat` is not cheap to construct and a row's figure is read on
  // every change-detection pass, so a formatter built at the call site is one
  // construction per row per tick.
  readonly #figures = new Map<string, Intl.NumberFormat>();

  // Where the last write's answer renders, or `null` where there is no answer
  // to render. Cleared when the **next write starts**, and by nothing else.
  //
  // `null` rather than an empty report held as a constant, for the reason the
  // getter below is a getter: a class field initialised from another module's
  // constant is a shape that has already been measured evaluating too early
  // here.
  readonly #write = signal<WriteReport | null>(null);

  // Whether this screen's own write is in flight. **Not
  // `TransactionsService.loading`**, which is raised by the three reads this
  // screen starts as well — narrating "Recording…" over a list refresh would
  // tell somebody their entry is being saved when nothing of theirs is in
  // flight.
  readonly #recording = signal(false);

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

  /**
   * The server's sentences for this write, keyed by the control each goes
   * beneath.
   *
   * Empty for every outcome but `invalid`, and empty for an `invalid` all of
   * whose keys this form had nowhere to put — those are in {@link refusals}
   * instead, which is the same answer rendered in the other place rather than a
   * second one.
   */
  protected readonly fieldMessages = computed(
    () => this.#write()?.fields ?? null,
  );

  /**
   * The lines the region carries for the last write, in the order they were
   * decided.
   */
  protected readonly refusals = computed(() => this.#write()?.lines ?? []);

  /**
   * The one line the shared region carries, as a single word.
   *
   * **A read in flight outranks everything**, because that request is running
   * now and the write has already answered. `recording` and `refused` cannot
   * meet — the next write clears the last one's account of itself before it
   * starts — but the order is written out all the same, so that adding a state
   * later is a decision somebody makes rather than one the branch order makes
   * for them. **A refused write outranks a finished read's failure**: the form
   * is still holding the text that was refused, and the chapter gives that
   * sentence's removal to the next write alone.
   */
  protected readonly regionState = computed<
    'loading' | 'recording' | 'refused' | 'failed' | null
  >(() => {
    if (this.readState() === 'loading') {
      return 'loading';
    }

    if (this.#recording()) {
      return 'recording';
    }

    if (this.refusals().length > 0) {
      return 'refused';
    }

    return this.readState();
  });

  /**
   * The copy for a write in progress, read from the one module that holds this
   * chapter's sentences rather than written out in the template beside it.
   *
   * **A getter and not a field, and the reason is a transform rather than a
   * race.** Vitest runs the built bundle through Vite's module-runner
   * transform, which turns every reference to an imported name into a live read
   * off the import namespace object — with one exception, written into
   * `moduleRunnerTransform` itself: where the name is the **whole** initialiser
   * of a class field, or an `extends` clause, the reference is left alone and a
   * module-scope `const` copy of it is hoisted above the statement instead.
   * That copy is taken when the *chunk* is evaluated, and this builder wraps
   * every module of the app in esbuild's lazy `__esm()` initialiser, so at that
   * moment the owning module's body has not run and its export is still an
   * unassigned `var`. The copy freezes `undefined` and never thaws: the field
   * held it, the template interpolated an empty string, and the region drew an
   * empty paragraph.
   *
   * **What that rules out is the reading a reader will reach for.** Nothing is
   * read too early at construction time — a sibling field reading the same
   * imported name one line down, as an object member or a call argument, gets
   * the real value in the same instant, because those positions keep the live
   * namespace read. Measured: the cap below rendered no `maxlength` while the
   * `Validators.maxLength` built from the same import in the next field refused
   * at exactly that cap. The two differ by **where in the syntax** the name
   * sits, not by when they run — which is also why the fix is any wrapping at
   * all, and a getter is simply the honest form for a value a template reads.
   *
   * It bites only once the two files land in different chunks: a single-file
   * run inlines the owning module into the spec bundle, leaving no import to
   * snapshot, so `--include` is green either way. The application build applies
   * no such transform, so nothing here was ever wrong in a browser. Do not
   * "simplify" this back into a field.
   */
  protected get recordingSentence(): string {
    return RECORDING_SENTENCE;
  }

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
   *
   * **Getters and not fields**, the same trap {@link recordingSentence} argues
   * in full a few members up: a class field whose initialiser is *nothing but*
   * an imported name is the one position the test runner's module transform
   * snapshots rather than reads live, and under this builder the snapshot is
   * taken before the owning module has assigned anything. Every other position
   * stays live — including the two `Validators.maxLength` arguments below,
   * which is why those are safe as they stand.
   */
  protected get nameCharacters(): number {
    return NARRATIVE_NAME_CHARACTERS;
  }

  protected get descriptionCharacters(): number {
    return NARRATIVE_DESCRIPTION_CHARACTERS;
  }

  // The caps reach the validators as **call arguments** and are deliberately
  // not lifted into fields of their own: an argument keeps the live import,
  // which is the half of {@link recordingSentence}'s paragraph that applies
  // here. It is worth saying rather than assuming, because a cap lost at these
  // sites is silent — `Validators.maxLength(undefined)` neither throws nor
  // refuses anything, measured — and the only thing that would notice is the
  // pairs of cases in this file's spec that push a value one unit past each cap
  // and exactly to it.
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

  /**
   * What line 1 says: the counterparty, or the note where the row names no
   * counterparty.
   *
   * **`?? ` over the *absent* payee and never over the unreadable one.**
   * `payeeName` is `null` exactly where the column held nothing, which this
   * browser knows without any key; a row whose payee name failed to open still
   * has a payee, and falling through to the note there would put a different
   * value under the same heading depending on whether a key happened to be
   * held, with nothing on screen saying which arrived. `NarrativeText | null`
   * out, never a string — the marker component is what decides how a word that
   * is not text renders.
   */
  protected leadOf(transaction: TransactionView): NarrativeText | null {
    return transaction.payeeName ?? transaction.description;
  }

  /**
   * Whether this row is the marked case.
   *
   * `> 0` and not `>= 0`: zero is a legal amount and neither income nor
   * spending — a purchase a voucher covered in full is a record worth keeping —
   * so it takes no sign and no colour.
   */
  protected isIncome(transaction: TransactionView): boolean {
    return transaction.amount > 0;
  }

  /**
   * The row's figure, per `docs/design/patterns.md`.
   *
   * The sign rule is the formatter's rather than this method's: an expense is
   * formatted with `signDisplay: 'never'`, which drops the stored minus without
   * anything here touching the number, and everything else with `exceptZero`,
   * which marks income with a `+` **where the locale puts one**. A `'+'`
   * concatenated on in front would be right for English and wrong wherever the
   * sign trails.
   */
  protected amountText(transaction: TransactionView): string {
    return this.#figure(
      transaction.currencyCode,
      transaction.amount < 0,
    ).format(transaction.amount);
  }

  #figure(currencyCode: string, unsigned: boolean): Intl.NumberFormat {
    const signDisplay = unsigned ? 'never' : 'exceptZero';
    // A separator no currency code contains, so two keys cannot collide.
    const key = `${currencyCode} ${signDisplay}`;
    const held = this.#figures.get(key);

    if (held !== undefined) {
      return held;
    }

    const built = this.#buildFigure(currencyCode, signDisplay);

    this.#figures.set(key, built);

    return built;
  }

  // **Total, and that is the load-bearing part.** `Intl.NumberFormat` answers a
  // `RangeError` for a currency code that is not three letters, and this runs
  // inside change detection: the throw escapes a template binding, Angular
  // abandons the pass, and every section declared after the list stops
  // rendering — no `try` around a signal read can contain it. The fallback
  // drops the currency style, keeps the sign rule and keeps the two decimals,
  // so a row with a damaged code still reads as money.
  #buildFigure(
    currencyCode: string,
    signDisplay: 'exceptZero' | 'never',
  ): Intl.NumberFormat {
    try {
      return new Intl.NumberFormat(this.#locale, {
        currency: currencyCode,
        signDisplay,
        style: 'currency',
      });
    } catch {
      return new Intl.NumberFormat(this.#locale, {
        maximumFractionDigits: 2,
        minimumFractionDigits: 2,
        signDisplay,
      });
    }
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

    // The one thing that clears the last write's account of itself. Before the
    // request rather than after the answer, so that the region carries this
    // write's progress instead of the previous press's refusal.
    this.#write.set(null);
    clearFieldMessages(this.form);
    this.#recording.set(true);

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

    this.#recording.set(false);
    this.#render(outcome);

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

  // Publishes one write's answer, and moves focus where the chapter puts it:
  // to the first control carrying a message, and nowhere at all when none
  // does. `accounts.component.ts` argues both halves at its own copy.
  #render(outcome: WriteOutcome): void {
    const report = writeReportOf(outcome, PLACEABLE_KEYS);

    this.#write.set(report);

    const first = markFieldMessages(this.form, report);

    if (first === null) {
      return;
    }

    // Found by the control name the form itself uses, so this cannot name a
    // control the report did not. A `mat-select` is the host element rather
    // than an input and is focusable in the same way.
    this.host.nativeElement
      .querySelector<HTMLElement>(`[formcontrolname="${first}"]`)
      ?.focus();
  }

  private toDateOnlyString(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');

    return `${year}-${month}-${day}`;
  }
}
