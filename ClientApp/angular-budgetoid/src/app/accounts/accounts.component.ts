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
// **A failed read has a line of its own.** The list is `null` at rest, in
// flight **and** after a failure, so a screen reading the list and the running
// flag alone rendered nothing whatever over a read that never landed — a form
// on top and silence beneath it, which is what an account with no rows in it
// looks like. `AccountsService.failed` is the fourth state that tells them
// apart, and `loading` still outranks it so that one failed read does not put
// the sentence on screen for every reload after it.
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
// **The non-blank validator is what the removed `.trim()` was accidentally
// doing.** The service may not alter what it seals — a trimmed seal beside an
// untrimmed index keys a row to a value nothing looks up — so the rule moved
// here, where refusing is all it does. `Validators.required` admits `'   '` on
// its own, so it needs the neighbour below.
//
// **A refused write is never silent and it never costs a keystroke**, which is
// `docs/design/components.md`, "A write that does not happen", and it is two
// rules rather than one. {@link AccountsComponent.save} **awaits** the outcome
// and clears the form on `recorded` and on nothing else: a clear written on the
// line after the call runs before any answer exists, so it emptied the field on
// every outcome the chapter is about — including a refusal whose sentence is
// then a message about a value no longer on screen, which is worse than
// silence. And every other outcome renders: the server's own sentences beneath
// the controls it keyed them to, everything else as a line in the region this
// screen already has.
//
// **A row's delete is a write too, and it renders through the same region.** It
// clears the last report as it starts, awaits its word and renders it — but
// through `rowActReportOf`, because every sentence the form's copy reaches for
// says *what you typed* and a delete holds none. Nothing lands on a control and
// nothing takes focus: there is no field a person could correct. The design
// book left this write with a classification and nowhere to put it, and
// `+shared/write-outcome-report.ts` is where the sentences that closed the gap
// live.
//
// **The region is the one that was already here.** Shared with the read's two
// lines and counted once per state, because a second `role="status"` is
// announced twice and is invisible to whichever branch did not create it. It
// stays `status` and is never raised to `alert`: politeness is a property of
// the node rather than of the sentence, so taking `accessibility.md`'s
// assertive carve-out here would take the loading line and the locked notice
// with it — and the carve-out is for a save that fails out of sight of the
// press, which this is not.
//
// **Which of the region's lines shows is one word off
// {@link AccountsComponent.regionState}**, so the read's account of itself and
// the write's are exclusive by structure. The order inside it is the chapter's
// with one addition it does not spell out: a read *in flight* wins, because
// that request is running now and this one has answered; a write's refusal then
// outranks a **finished** read's failure, because the form is still holding the
// text that was refused and only the next write may clear that sentence — so a
// refusal made before a slow read reappears when the read answers, which the
// chapter names as intent rather than as a leak.
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
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { AccountType } from '@app-core/api/account-api.service';
import type { WriteOutcome } from '@app-core/api/write-outcome';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import { LockedAccountNoticeComponent } from '@app-shared/components/locked-account-notice/locked-account-notice.component';
import { NarrativeValueComponent } from '@app-shared/components/narrative-value/narrative-value.component';
import { NARRATIVE_NAME_CHARACTERS } from '@app-shared/narrative-field-caps';
import {
  clearFieldMessages,
  markFieldMessages,
  rowActReportOf,
  writeReportOf,
  type WriteReport,
} from '@app-shared/write-outcome-report';
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

/**
 * The wire keys this form can place a server's sentence on, and the control
 * each goes beneath.
 *
 * **A `Map` and never an object literal**, per the chapter: `constructor`,
 * `toString` and `valueOf` hit on a literal and place a message under a control
 * that does not exist.
 *
 * **Built per press rather than declared once, because the answer changes.**
 * `currencyCode` is immutable, so the control leaves the DOM for the whole of
 * an edit — and a key the form has but is not currently rendering is a **miss**
 * by this chapter's rule, since the question is whether a message can be seen
 * rather than whether the name is one the form recognises.
 */
function placeableKeys(editing: boolean): ReadonlyMap<string, string> {
  const keys = new Map<string, string>([
    ['Name', 'name'],
    ['Type', 'type'],
    ['OpeningBalance', 'openingBalance'],
  ]);

  if (!editing) {
    keys.set('CurrencyCode', 'currencyCode');
  }

  return keys;
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

    /*
      A refused write's line in the shared region. --bud-over, and colour is
      never the message: every sentence in the chapter's table reads the same
      with this declaration removed.
    */
    .refusal {
      margin: 0;
      color: var(--bud-over);
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
        <!--
          The cap is bound rather than typed, so this attribute and the
          validator below cannot drift apart, and so the number stays beside
          the byte cap it protects — @app-shared/narrative-field-caps holds
          the whole argument.
        -->
        <input
          matInput
          formControlName="name"
          [attr.maxlength]="nameCharacters"
        />
        <!--
          The server's sentence, rendered verbatim beneath the control it was
          keyed to. The client writes no copy for a field-keyed refusal and
          holds no table of its own: a client-authored lookup would have to be
          total over every string the API can send, and its fallback would be a
          generic sentence standing exactly where somebody is trying to make a
          correction. Rendering what arrived is total by construction.

          mat-error rather than a paragraph of this screen's own, because the
          form field is what binds the message to the input with
          aria-describedby and colours the border with it — one node, one
          relationship, both of them the book's.
        -->
        @for (message of fieldMessages()?.get('name') ?? []; track $index) {
          <mat-error>{{ message }}</mat-error>
        }
      </mat-form-field>

      <mat-form-field>
        <mat-label>Type</mat-label>
        <mat-select formControlName="type">
          @for (type of accountTypes; track type) {
            <mat-option [value]="type">{{ type }}</mat-option>
          }
        </mat-select>
        @for (message of fieldMessages()?.get('type') ?? []; track $index) {
          <mat-error>{{ message }}</mat-error>
        }
      </mat-form-field>

      <mat-form-field>
        <mat-label>Opening balance</mat-label>
        <input
          matInput
          type="number"
          step="0.01"
          formControlName="openingBalance"
        />
        @for (
          message of fieldMessages()?.get('openingBalance') ?? [];
          track $index
        ) {
          <mat-error>{{ message }}</mat-error>
        }
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
          @for (
            message of fieldMessages()?.get('currencyCode') ?? [];
            track $index
          ) {
            <mat-error>{{ message }}</mat-error>
          }
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
      are exclusive by structure rather than by the order somebody happened to
      write the branches in. One outcome may take more than one line — an
      errors map keyed to members this form cannot place sends one line per
      entry — and that is still one answer to one write.
    -->
    <div role="status">
      @if (regionState() === 'loading') {
        <p class="reason">Reading your accounts…</p>
      } @else if (regionState() === 'refused') {
        @for (sentence of refusals(); track $index) {
          <p class="refusal">{{ sentence }}</p>
        }
      } @else if (regionState() === 'failed') {
        <p class="reason">
          We couldn’t read your accounts. Check your connection and reload the
          page.
        </p>
      }
    </div>
  `,
})
export class AccountsComponent implements OnInit {
  protected readonly accounts = inject(AccountsService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly currencyApi = inject(CurrencyApiService);
  private readonly custody = inject(AccountKeyCustodyService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  // Where the last write's answer renders, or `null` where there is no answer
  // to render. Cleared when the **next write starts**, and by nothing else —
  // not by a read, not by a keystroke, not by a cancel — which is what makes a
  // refusal survive a slow read that displaced it.
  //
  // `null` rather than an empty report held as a module constant:
  // `transactions.component.ts` measured a class field initialised from
  // another module's constant evaluating before that module's body under the
  // unit-test builder's chunking, and this is the same shape.
  readonly #write = signal<WriteReport | null>(null);

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
   */
  protected readonly readState = computed<'loading' | 'failed' | null>(() => {
    if (this.locked() || this.accounts.accounts() !== null) {
      return null;
    }

    if (this.accounts.loading()) {
      return 'loading';
    }

    return this.accounts.failed() ? 'failed' : null;
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
   *
   * One sentence for a conflict or an unanswered request; one per `errors`
   * entry this form could not place, in the order the map sent them.
   */
  protected readonly refusals = computed(() => this.#write()?.lines ?? []);

  /**
   * The one line the shared region carries, as a single word.
   *
   * **A read in flight outranks everything**, because that request is running
   * now and the write has already answered. **A refused write then outranks a
   * finished read's failure**: the form is still holding the text that was
   * refused, the sentence is as true as when it was written, and the chapter
   * gives its removal to the next write alone — so clearing it here would leave
   * a form full of unsaved text with nothing on screen saying why.
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

  protected readonly accountTypes: AccountType[] = [
    'Checking',
    'Savings',
    'Cash',
    'CreditCard',
  ];
  /**
   * The cap the template's `maxlength` reads, and the same value the validator
   * below is built from.
   *
   * `@app-shared/narrative-field-caps` argues it: this is a UX ceiling in
   * UTF-16 code units, and what makes it safe is that 200 units cannot seal
   * past `accounts.name`'s byte cap even when every one of them is a
   * three-byte character.
   *
   * **A getter and not a field**, for the reason
   * `TransactionsComponent.recordingSentence` states in full at its own copy: a
   * class field whose initialiser is *nothing but* an imported name is the one
   * position the test runner's module transform snapshots rather than reads
   * live, and under this builder the snapshot is taken before the owning module
   * has assigned anything. Every other position stays live — including the
   * `Validators.maxLength` argument a few lines down, which is why that one is
   * safe as it stands.
   */
  protected get nameCharacters(): number {
    return NARRATIVE_NAME_CHARACTERS;
  }

  protected readonly editingId = signal<string | null>(null);
  protected readonly currencies = signal<CurrencyDto[]>([]);

  // The cap reaches the validator as a **call argument** and is deliberately
  // not lifted into a field of its own: an argument keeps the live import,
  // which is the half of `nameCharacters`' paragraph that applies here. It is
  // worth saying rather than assuming, because a cap lost at this site is
  // silent — `Validators.maxLength(undefined)` neither throws nor refuses
  // anything, measured — and the only thing that would notice is the pair of
  // cases in this file's spec that push a name one unit past the cap and
  // exactly to it.
  protected readonly form = this.formBuilder.nonNullable.group({
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

  protected async save(): Promise<void> {
    // The gate is in the handler as well as in the attribute. A disabled form's
    // status is `DISABLED` and its `invalid` is therefore `false`, so the check
    // below would wave a locked submit through on its own — and Material's
    // click-halt is applied to anchors only, so a `<button>` can still receive
    // the press that gets here.
    if (!this.writable() || this.form.invalid) {
      return;
    }

    this.#beginWrite();

    const value = this.form.getRawValue();
    const request = {
      name: value.name,
      type: value.type,
      openingBalance: value.openingBalance,
    };
    const editingId = this.editingId();
    // **Awaited, and this is the whole of the chapter's first rule.** The clear
    // below belongs to a `201` or a `204`, never to a press: written on the
    // line after the dispatch it ran before any answer existed and emptied the
    // form on every outcome — including the ones the region is here to render,
    // where the sentence would then be about a value no longer on screen.
    const outcome = editingId
      ? await this.accounts.update(editingId, request)
      : await this.accounts.add({
          ...request,
          currencyCode: value.currencyCode,
        });

    this.#render(outcome, editingId !== null);

    if (outcome.state !== 'recorded') {
      // Nothing navigates and nothing collapses either: a refused write leaves
      // the form exactly as the press found it, still in edit mode if it was.
      return;
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

  /**
   * Deletes one row, and renders whatever the delete answered.
   *
   * **A delete is a write and takes the same three steps a save does**: the
   * previous report comes down as this one starts, the outcome is awaited, and
   * the word is rendered. What it does not share is the copy —
   * {@link rowActReportOf} holds the sentences for an act that has no typed
   * text in it — and it moves no focus, because no control can carry a message
   * for a press made on a row.
   */
  protected async remove(account: AccountView): Promise<void> {
    this.#beginWrite();

    const outcome = await this.accounts.remove(account.id);

    // No `markFieldMessages`: the report's `fields` is empty on every word,
    // because a delete has no form to place a key on. Nothing navigates and
    // nothing is taken off the list either — the service filters the row inside
    // its `tap`, so a refusal leaves the row exactly where the press found it,
    // which is what the sentence beside it says.
    this.#write.set(rowActReportOf(outcome, 'removal'));
  }

  // The one thing that clears the last write's account of itself. Before the
  // request rather than after the answer, so that the region is silent for as
  // long as a write is unanswered instead of carrying a sentence about the
  // press before it. Every write on this screen begins here, a delete
  // included: two accounts of two presses on screen at once is what the one
  // region refuses.
  #beginWrite(): void {
    this.#write.set(null);
    clearFieldMessages(this.form);
  }

  // Publishes one write's answer, and moves focus where the chapter puts it.
  //
  // **Focus moves to the first control carrying a message, and only then.** A
  // message bound by `aria-describedby` is announced when its control takes
  // focus rather than when it appears, and the field may be off screen
  // besides. Where no control carries one, focus stays where the press left it:
  // moving a keyboard user into a region takes them away from the control they
  // are about to press again.
  #render(outcome: WriteOutcome, editing: boolean): void {
    const report = writeReportOf(outcome, placeableKeys(editing));

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
}
