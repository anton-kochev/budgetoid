// The transactions screen, driven against three hand-written stubs.
//
// **Custody is stubbed for its `status` alone, and the stub's status is its own
// settable signal.** The real service reaches `locked` only by never having
// been unlocked or by a failed ceremony, neither of which this runner can
// stage; and a stub that *derived* the reading from something else would be a
// second copy of the predicate, pinning nothing about which object the screen
// asked.
//
// **`implements Pick<S, keyof S>` on the two service stubs** is the compiler's
// own census of what each publishes — `keyof` over a class yields the public
// surface only — so a member the screen starts reaching for is an error here
// rather than an `is not a function` during change detection.
//
// **`unlocking` gets its own two cases, because it is where the two predicates
// disagree.** The form follows `=== 'unlocked'` and the notice follows
// `=== 'locked'`, so a screen written with one predicate passes half of what is
// here and fails the other half whichever way it was written.
//
// **The rename rule the accounts screen carries has no surface here**, and that
// is a fact about this screen rather than an omission in this file: a
// transaction row offers no Edit and no Delete, so there is no control to
// disable on a row whose values did not open. It arrives with the edit screen.
//
// **The row is read by class and no longer by Material's element names.** It is
// a plain semantic list now — `docs/design/components.md` gives "Transaction
// row" the M3 base "none" — so the list is `.transactions`, a row is
// `.transaction-row`, and its four cells are `.lead`, `.amount`, `.meta` and
// `.date`. The empty state is `.no-rows` deliberately and not a row, or every
// count of `.transaction-row` would answer one over an empty list — and there
// is a case standing on that now, where for a while there was only the reason.
//
// **Nothing here holds the row's *presentation*, and that is measured rather
// than assumed.** Deleting the component's whole `styles` block —
// `TestBed.overrideComponent(TransactionsComponent, { set: { styles: [] } })`,
// applied at both places this file builds the component — leaves **all
// sixty-three cases green**. So the grid, the 64px floor, the gutter, the
// hairline inset to it, the press layer, the right alignment and the tabular
// figures are held by `docs/design/components.md` and by a browser, not by
// anything below. Every selector this file reads is a hook, and a class that
// stopped carrying its declarations would go unnoticed here. Do not read a
// green run as evidence the row looks right.
//
// **`listText()` and never `host().textContent`, on anything the row must not
// show.** The form above the list holds a category picker, so a group's name is
// on this screen twice over and only one of the two is a defect.
//
// **The figures block provides `TRANSACTION_ROW_LOCALE`, and that is the only
// place a locale is named.** Nothing in this application configures
// `LOCALE_ID`, and `Intl` with no locale falls back to the host's, so an
// expectation on a formatted figure written without that provider is true on
// the machine it was written on and unproven anywhere else — the runner pins
// the time zone and not the locale. The one figure read outside that block
// asserts only that it is **not empty**, for the same reason: a row's currency
// renders differently on every machine, and "the composition did not throw" is
// all that case is entitled to claim.
//
// **No expectation reads a date with `toContain`.** `createdAtUtc` is on the
// row's model and *begins* with the ten characters `date` holds, so a substring
// match against either passes over the other. Both date expectations are the
// whole cell.
import { signal, type Signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormGroup } from '@angular/forms';
import { MatSelect } from '@angular/material/select';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import type { CategoryGroupView } from '../categories/category-group-view';
import type { CategoryView } from '../categories/category-view';
import type { WriteOutcome } from '@app-core/api/write-outcome';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import {
  NARRATIVE_DESCRIPTION_CHARACTERS,
  NARRATIVE_NAME_CHARACTERS,
} from '@app-shared/narrative-field-caps';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountView } from '../accounts/account-view';
import { AccountsService } from '../accounts/accounts.service';
import type { PayeeView } from './payee-view';
import type { TransactionView } from './transaction-view';
import {
  TRANSACTION_ROW_LOCALE,
  TransactionsComponent,
} from './transactions.component';
import { TransactionsService } from './transactions.service';

const TRANSACTION_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const ACCOUNT_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const PAYEE_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000004';
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000005';

// What a missing `maxlength` most likely means, said in the failure rather than
// left for the next person to rediscover. The caps reach the template through
// class fields initialised from another module's constants, and that shape has
// already been measured **in this file's own component** reading `undefined`
// under the test builder's chunking — `[attr.maxlength]="undefined"` renders no
// attribute at all, so the typing limit silently is not there.
// `TransactionsComponent.recordingSentence` is the same hazard, found earlier and
// fixed by reading the value at render time.
const CAP_ABSENT =
  'the field states no maxlength: a cap reaching the template through a class ' +
  'field initialised from another module’s constant reads undefined under the ' +
  'test builder’s chunking — read it at render time instead';

const everyday: AccountView = {
  createdAtUtc: '2026-01-02T03:04:05Z',
  currencyCode: 'USD',
  currencyMinorUnit: 2,
  currencyName: 'US Dollar',
  currencySymbol: '$',
  id: ACCOUNT_ID,
  name: { state: 'text', value: 'Everyday' },
  openingBalance: 0,
  type: 'Checking',
};

const weeklyShop: TransactionView = {
  accountId: ACCOUNT_ID,
  accountName: { state: 'text', value: 'Everyday' },
  amount: -20.5,
  categoryGroupId: GROUP_ID,
  categoryGroupName: { state: 'text', value: 'Essentials' },
  categoryId: CATEGORY_ID,
  categoryName: { state: 'text', value: 'Groceries' },
  createdAtUtc: '2026-07-14T10:00:00Z',
  currencyCode: 'USD',
  currencySymbol: '$',
  date: '2026-07-14',
  description: { state: 'text', value: 'Weekly shop' },
  id: TRANSACTION_ID,
  payeeId: PAYEE_ID,
  payeeName: { state: 'text', value: 'Corner Shop' },
};

const cornerShop: PayeeView = {
  id: PAYEE_ID,
  name: { state: 'text', value: 'Corner Shop' },
  nameKey: 'index-corner-shop',
};

// A second readable payee, so that "the filter kept this one" and "the filter
// kept everything" are two different answers. With one payee on the list they
// are the same array.
const bakery: PayeeView = {
  id: '0199c3d4-5f6a-7b8c-9d0e-00000000000e',
  name: { state: 'text', value: 'Bakery' },
  nameKey: 'index-bakery',
};

// A payee this browser could not read. There is no text to type-ahead against,
// so it is not offered as a suggestion — which is a filter and not a collapse:
// nothing turns its word into a string.
const unreadablePayee: PayeeView = {
  id: '0199c3d4-5f6a-7b8c-9d0e-00000000000f',
  name: { state: 'unreadable' },
  nameKey: null,
};

class TransactionsServiceStub
  implements Pick<TransactionsService, keyof TransactionsService>
{
  public readonly transactionsSignal = signal<
    readonly TransactionView[] | null
  >([weeklyShop]);
  public readonly payeesSignal = signal<readonly PayeeView[] | null>([
    cornerShop,
  ]);
  public readonly categoryGroupsSignal = signal<readonly CategoryGroupView[]>([
    {
      description: null,
      id: GROUP_ID,
      name: { state: 'text', value: 'Essentials' },
      position: 0,
    },
  ]);
  public readonly categoriesSignal = signal<readonly CategoryView[]>([
    {
      categoryGroupId: GROUP_ID,
      categoryGroupName: { state: 'text', value: 'Essentials' },
      description: null,
      id: CATEGORY_ID,
      name: { state: 'text', value: 'Groceries' },
      position: 0,
    },
  ]);
  public readonly loadingSignal = signal(false);
  public readonly failedSignal = signal(false);

  public readonly transactions = this.transactionsSignal.asReadonly();
  public readonly payees = this.payeesSignal.asReadonly();
  public readonly categoryGroups = this.categoryGroupsSignal.asReadonly();
  public readonly categories = this.categoriesSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();
  public readonly failed = this.failedSignal.asReadonly();

  public load = vi.fn();
  public loadPayees = vi.fn();
  public loadCategories = vi.fn();
  public categoriesForGroup = vi.fn((): readonly CategoryView[] =>
    this.categoriesSignal(),
  );
  // Answers `recorded` by default, because that is the path a case that says
  // nothing about the outcome means.
  public add = vi.fn(
    (): Promise<WriteOutcome> => Promise.resolve({ state: 'recorded' }),
  );
}

class AccountsServiceStub
  implements Pick<AccountsService, keyof AccountsService>
{
  public readonly accountsSignal = signal<AccountView[] | null>([everyday]);
  public readonly loadingSignal = signal(false);
  // Present because the compiler's census demands it, and read by nothing on
  // this screen: the accounts read feeds a picker here, and the sentence a
  // failed accounts read earns belongs to `/app/accounts`.
  public readonly failedSignal = signal(false);

  public readonly accounts = this.accountsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();
  public readonly failed = this.failedSignal.asReadonly();

  public load = vi.fn();
  public add = vi.fn(
    (): Promise<WriteOutcome> => Promise.resolve({ state: 'recorded' }),
  );
  public update = vi.fn(
    (): Promise<WriteOutcome> => Promise.resolve({ state: 'recorded' }),
  );
  public remove = vi.fn();
}

class CustodyStub
  implements Pick<AccountKeyCustodyService, keyof AccountKeyCustodyService>
{
  readonly #status = signal<AccountKeyStatus>('unlocked');

  public readonly status: Signal<AccountKeyStatus> = this.#status.asReadonly();
  public readonly unlockFailure: Signal<UnlockFailure | null> =
    signal<UnlockFailure | null>(null).asReadonly();

  public setStatus(status: AccountKeyStatus): void {
    this.#status.set(status);
  }

  public unlock(): void {
    throw new Error('the transactions screen may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the transactions screen may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the transactions screen may not lock the account');
  }

  public sealField(): never {
    throw new Error('the transactions screen may not seal — the service does');
  }

  public openField(): never {
    throw new Error('the transactions screen may not open — the mapper does');
  }

  public blindIndex(): never {
    throw new Error('the transactions screen may not index — the service does');
  }
}

// The screen's own members are `protected`, which is right for a template and
// leaves a spec nothing to hold. One cast, in one place, so the reach is
// visible rather than scattered.
function exposed(component: TransactionsComponent): {
  form: FormGroup;
  add: () => void;
} {
  return component as unknown as { form: FormGroup; add: () => void };
}

// The submit, as the promise it now is. Kept apart from {@link exposed} so
// that the cases which only press the button go on ignoring the result and
// stay free of a floating promise.
function pressAdd(component: TransactionsComponent): Promise<void> {
  return (component as unknown as { add: () => Promise<void> }).add();
}

// The suggestions the autocomplete renders. Read through the same one cast,
// and read as a call whether it is a method or a computed signal.
function suggestions(
  component: TransactionsComponent,
): readonly { id: string; name: string }[] {
  return (
    component as unknown as {
      filteredPayees: () => readonly { id: string; name: string }[];
    }
  ).filteredPayees();
}

// A run of whitespace written as the code points it is made of.
//
// **Rendered, because every character this exists to tell apart is the same
// shape on screen and in a diff.** What shipped was U+0020 followed by U+00A0,
// and a failure message that printed the string back would show that pair as
// one ordinary space; U+00A0 on its own is likewise indistinguishable from the
// U+0020 that does not hold a separator to the word in front of it. So the
// expectations below are written in escapes and this file holds neither
// character as a literal. `\s` is the right class rather than `' '` — U+00A0 is
// whitespace to the regexp engine and a space to the layout engine, and it is
// exactly half of the defect.
//
// **Nothing here normalizes, and nothing may.** A case that ran `.trim()`, a
// `.replace(/\s+/gu, ' ')` or a comparison against a normalized expectation
// would be doing to the text precisely what the browser does not do, and would
// go green over the defect it was written for.
function spell(whitespace: string): string {
  return Array.from(whitespace)
    .map(
      (character) =>
        `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`,
    )
    .join('');
}

// The one spelling line 2 may hold beside its separator, named so that an
// expectation reads as a sentence instead of as four hex digits.
//
// **Built from a character code rather than from a literal**, and that is the
// same argument as `spell`'s one line up: a no-break space written out here is
// an invisible character in a source file, so the day somebody "tidies" it into
// an ordinary one this constant quietly becomes ` `, the no-break rule
// below stops being a rule, and a template that lost its `&nbsp;` goes green.
const NO_BREAK_SPACE = spell(String.fromCharCode(0xa0));

// Every `·` in the line with the whitespace run on each side of it, each run
// spelled out.
//
// This is the whole of "exactly one space either side of every separator", and
// then one thing more: a side that lost its space renders as an empty run, a
// side that gained one renders as two code points, and a no-break space quietly
// demoted to an ordinary one renders as a **different** code point. The last is
// what a rule counting characters cannot see, and it is the entire reason the
// `&nbsp;` is written there — demoted, the separator is free to wrap onto the
// next line by itself.
//
// The trailing run is taken through a lookahead so that it is not consumed:
// consumed, two separators that ended up adjacent would report the second as
// having nothing in front of it.
function separatorSpacing(text: string): readonly string[] {
  return Array.from(text.matchAll(/(\s*)·(?=(\s*))/gu), (match) => {
    const [, before = '', after = ''] = match;

    return `${spell(before)}·${spell(after)}`;
  });
}

// Any whitespace the line begins or ends with, which no rule about separators
// can see: a single space left inside the last span is a run of one, sits
// beside no `·`, and renders as nothing anybody can point at.
function edgeWhitespace(text: string): readonly string[] {
  const leading = /^\s+/u.exec(text)?.[0] ?? '';
  const trailing = /\s+$/u.exec(text)?.[0] ?? '';

  return [
    ...(leading === '' ? [] : [`start:${spell(leading)}`]),
    ...(trailing === '' ? [] : [`end:${spell(trailing)}`]),
  ];
}

describe('TransactionsComponent', () => {
  let transactions: TransactionsServiceStub;
  let custody: CustodyStub;
  let fixture: ComponentFixture<TransactionsComponent>;

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  // The first row's second line, as the browser renders it — text taken off
  // the one element, never off `host()`, whose text carries the form above it.
  // It throws rather than answering `''` when the line is missing, so a
  // selector that stopped matching reddens as a selector rather than passing as
  // a string with no whitespace in it.
  function metadataLine(): string {
    const line = host().querySelector('.transaction-row .meta');

    if (line === null) {
      throw new Error('no metadata line to read on the first transaction row');
    }

    return line.textContent ?? '';
  }

  // The first row's figure, and the first row's date, each read off its own
  // cell. Both throw for the reason above.
  function amountCell(): HTMLElement {
    const amount = host().querySelector<HTMLElement>(
      '.transaction-row .amount',
    );

    if (amount === null) {
      throw new Error('no amount to read on the first transaction row');
    }

    return amount;
  }

  function dateCell(): string {
    const date = host().querySelector('.transaction-row .date');

    if (date === null) {
      throw new Error('no date to read on the first transaction row');
    }

    return date.textContent ?? '';
  }

  // The rows, and nothing above them. Read off the list rather than off
  // `host()`, whose text carries the form — a name this screen must keep off
  // the row is on screen twice over, once in a picker where it belongs.
  function listText(): string {
    return host().querySelector('.transactions')?.textContent ?? '';
  }

  // The first row's lead — line 1 of the two-line grid, in the content column.
  // It throws rather than answering `''` when the cell is missing, so a
  // selector that stopped matching reddens as a selector rather than passing
  // as an empty string.
  function leadLine(): string {
    const lead = host().querySelector('.transaction-row .lead');

    if (lead === null) {
      throw new Error('no lead to read on the first transaction row');
    }

    return lead.textContent ?? '';
  }

  function fill(values: {
    description?: string;
    payee?: string;
    categoryId?: string;
  }): void {
    exposed(fixture.componentInstance).form.setValue({
      amount: -20.5,
      date: new Date(2026, 6, 14),
      accountId: ACCOUNT_ID,
      description: values.description ?? 'Weekly shop',
      payee: values.payee ?? '',
      categoryId: values.categoryId ?? '',
    });
  }

  beforeEach(async () => {
    transactions = new TransactionsServiceStub();
    custody = new CustodyStub();
    TestBed.configureTestingModule({
      imports: [TransactionsComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: TransactionsService, useValue: transactions },
        { provide: AccountsService, useClass: AccountsServiceStub },
        { provide: AccountKeyCustodyService, useValue: custody },
      ],
    });
    await TestBed.compileComponents();
    fixture = TestBed.createComponent(TransactionsComponent);
    fixture.detectChanges();
  });

  // Opens the category picker and hands back its panel. The panel renders into
  // the overlay container on `document`, not into the fixture host, so nothing
  // about it can be read off `host()`.
  function openCategoryPicker(): HTMLElement | null {
    const picker = fixture.debugElement
      .queryAll(By.directive(MatSelect))
      .find(
        (candidate) =>
          (candidate.nativeElement as HTMLElement).getAttribute(
            'formcontrolname',
          ) === 'categoryId',
      );

    (picker?.componentInstance as MatSelect | undefined)?.open();
    fixture.detectChanges();

    return document.querySelector<HTMLElement>('.mat-mdc-select-panel');
  }

  it('renders the picker’s names as words and never as ciphertext', () => {
    // Arrange — this picker printed base64url until the categories screen owned
    // a view model, which is the one thing the previous phase wrote down and
    // did not fix.

    // Act
    const panel = openCategoryPicker();

    // Assert
    expect(panel?.textContent ?? '').toContain('Essentials');
    expect(panel?.textContent ?? '').toContain('Groceries');
    expect(panel?.textContent ?? '').not.toContain('[object Object]');
  });

  it('puts the group’s name in the optgroup label and leaves the options outside it', () => {
    // Arrange — `mat-optgroup`'s `label` takes a `string`, and a group's name
    // is a word, so the value goes in as **content** instead. That works
    // because `MatOptgroup` projects its default slot inside the label element
    // and selects `mat-option, ng-container` into a second slot outside it.
    // This case is the measurement of that claim: without the second slot the
    // options would render *inside* the label and the picker would be a single
    // unusable line.

    // Act
    const panel = openCategoryPicker();
    const label = panel?.querySelector('.mat-mdc-optgroup-label');

    // Assert
    expect(label?.textContent ?? '').toContain('Essentials');
    expect(label?.querySelector('mat-option')).toBeNull();
    expect(panel?.querySelectorAll('mat-option')).toHaveLength(2);
  });

  it('draws a marker for a group name that did not open and collapses nothing', () => {
    // Arrange — a picker is not exempt from "The locked account": a name that
    // did not open is a marker with an accessible name, never `''` and never a
    // dash chosen here.
    transactions.categoryGroupsSignal.set([
      {
        description: null,
        id: GROUP_ID,
        name: { state: 'locked' },
        position: 0,
      },
    ]);

    // Act
    const panel = openCategoryPicker();
    const label = panel?.querySelector('.mat-mdc-optgroup-label');

    // Assert
    expect(
      label?.querySelector('[role="img"]')?.getAttribute('aria-label'),
    ).toBe('Locked');
  });

  it('starts every read the screen draws from, on init', () => {
    // Arrange — the widest gap this file had. Every other case feeds the
    // stub's signals directly, so a component that dropped a call in
    // `ngOnInit` renders **nothing** in a browser and left the other
    // sixty-two of them green — measured, by replacing `ngOnInit` with a
    // no-op: one case red, sixty-two green. The accounts read is named here
    // for the same reason and is
    // not the same read: it fills the form's picker, so losing it takes the
    // Add control down while the list above it stays perfect.
    //
    // `loadCategories` is the one with an argument of its own — the grouped
    // picker printed base64url until the categories screen owned a view model,
    // and this is the call that fills it.
    const accounts = TestBed.inject(
      AccountsService,
    ) as unknown as AccountsServiceStub;

    // Assert — the fixture's own `beforeEach` is the act: `ngOnInit` has
    // already run by the time a case body starts. Four assertions and one
    // concept, so a failure names the call that went missing.
    expect(transactions.load).toHaveBeenCalledOnce();
    expect(transactions.loadPayees).toHaveBeenCalledOnce();
    expect(transactions.loadCategories).toHaveBeenCalledOnce();
    expect(accounts.load).toHaveBeenCalledOnce();
  });

  it('hands over the typed text untouched and no payee when none was typed', () => {
    // Arrange — the service seals exactly what was typed, so the screen may not
    // trim on the way past.
    fill({ description: '  Weekly shop  ', payee: '', categoryId: '' });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert — `categoryId` is `null` and never `''`: the route binds a
    // `Guid?`, and the empty string is the picker's own word for "none".
    expect(transactions.add).toHaveBeenCalledWith({
      accountId: ACCOUNT_ID,
      amount: -20.5,
      categoryId: null,
      date: '2026-07-14',
      description: '  Weekly shop  ',
      payee: '',
    });
  });

  it('hands over the typed payee and category when both were given', () => {
    // Arrange — the positive control for the case above.
    fill({ payee: '  Corner Shop  ', categoryId: CATEGORY_ID });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(transactions.add).toHaveBeenCalledWith(
      expect.objectContaining({
        categoryId: CATEGORY_ID,
        payee: '  Corner Shop  ',
      }),
    );
  });

  it('refuses a whitespace-only note', () => {
    // Arrange — the seal decides "no note" on `=== ''` exactly, because the
    // client may not alter what it seals, so `'   '` would be sealed and stored
    // as a note of three spaces.
    fill({ description: '   ' });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(exposed(fixture.componentInstance).form.invalid).toBe(true);
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('refuses a whitespace-only payee', () => {
    // Arrange — and this one is the load-bearing half: the normalization the
    // index runs trims, so `'   '` keys to the index of the *empty* name. Every
    // blank payee in the budget would then be one counterparty, and the row
    // this browser created for it holds three spaces nobody can search for.
    fill({ payee: '   ' });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(exposed(fixture.componentInstance).form.invalid).toBe(true);
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('accepts an empty note and an empty payee', () => {
    // Arrange — the positive control: a pair of refusal cases alone passes just
    // as well against a form nothing can ever satisfy, and both fields are
    // optional.
    fill({ description: '', payee: '' });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(transactions.add).toHaveBeenCalledWith(
      expect.objectContaining({ description: '', payee: '' }),
    );
  });

  // What a value longer than its column can hold does, and the two mechanisms
  // that stop it.
  //
  // **The validator and the attribute are held apart on purpose, because
  // nothing else holds either.** `+shared/narrative-field-caps.spec.ts` reads
  // the source tree and reports a `matInput` stating no cap at all — markup,
  // and it says so about itself: it cannot see `Validators.maxLength`, which is
  // behaviour. Measured by a reviewer before these cases existed: delete every
  // `Validators.maxLength(…)` and every `[attr.maxlength]` from this screen and
  // the whole suite stayed green. What that costs is not a tidy error message —
  // a note of the cap in a three-byte script seals past `DescriptionBytes`, so
  // the refusal arrives as a 400 from a server the form said nothing about, and
  // only for people writing in some languages.
  //
  // **This is the one screen holding a field of each class, and the split is
  // the point.** The note is `transactions.description` and the counterparty is
  // `payees.name`, so the two controls take different caps from constants
  // declared one line apart. Crossing them refuses three fifths of a legal note
  // or admits two and a half times a legal name, and nothing on either screen
  // says so.
  //
  // **The caps are imported and never typed.** A number written here would say
  // nothing about the byte cap it protects, and the day the Domain's constant
  // moves this file would go on asserting the old one.
  it('refuses a note one unit past the cap', () => {
    // Arrange — one unit over, which is the only length that tells a cap of
    // `NARRATIVE_DESCRIPTION_CHARACTERS` from a cap of anything larger.
    fill({ description: 'e'.repeat(NARRATIVE_DESCRIPTION_CHARACTERS + 1) });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert — the error key as well as the refusal. `nonBlankWhenPresent` also
    // makes this form invalid, so a case reading `invalid` alone would go green
    // with the length rule deleted.
    expect(
      exposed(fixture.componentInstance)
        .form.get('description')
        ?.hasError('maxlength'),
    ).toBe(true);
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('accepts a note of exactly the cap', () => {
    // Arrange — the other side of the boundary, and it is not decoration: a
    // case testing the long value alone passes against `maxLength(0)`, against
    // a validator that refuses everything, and against a form nobody can
    // satisfy. It is also the case that catches a note wired to the *name*
    // cap — 500 units is over that one and under this one.
    fill({ description: 'e'.repeat(NARRATIVE_DESCRIPTION_CHARACTERS) });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(
      exposed(fixture.componentInstance)
        .form.get('description')
        ?.hasError('maxlength'),
    ).toBe(false);
    expect(transactions.add).toHaveBeenCalledOnce();
  });

  it('refuses a counterparty one unit past the cap', () => {
    // Arrange — the **name** cap on this one, because a payee is a row in
    // `payees` and its name column is bounded like every other name in the
    // product.
    fill({ payee: 'e'.repeat(NARRATIVE_NAME_CHARACTERS + 1) });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(
      exposed(fixture.componentInstance)
        .form.get('payee')
        ?.hasError('maxlength'),
    ).toBe(true);
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('accepts a counterparty of exactly the cap', () => {
    // Arrange — the boundary's other side, and the case that reddens if the
    // counterparty is wired to the description cap: 201 units would then be
    // admitted here and refused by the server that has to store them.
    fill({ payee: 'e'.repeat(NARRATIVE_NAME_CHARACTERS) });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(
      exposed(fixture.componentInstance)
        .form.get('payee')
        ?.hasError('maxlength'),
    ).toBe(false);
    expect(transactions.add).toHaveBeenCalledOnce();
  });

  it('binds the note’s maxlength to the description cap and the counterparty’s to the name cap', () => {
    // Arrange — the second mechanism, and the one a person meets first: the
    // attribute is what stops the typing before there is anything to refuse.
    // Both are read in one case because the claim is the *pair* — the two
    // constants are four lines apart in the component and both are in scope in
    // the template, so a crossed binding renders a cap, is reported clean by
    // the sibling census, and is wrong in whichever direction it was crossed.

    // Act
    fixture.detectChanges();

    // Assert — the **value**, never merely the presence. The census asks only
    // whether a cap is stated at all, so it passes over both crossings.
    expect(
      host()
        .querySelector<HTMLInputElement>('input[formcontrolname="description"]')
        ?.getAttribute('maxlength'),
      CAP_ABSENT,
    ).toBe(String(NARRATIVE_DESCRIPTION_CHARACTERS));
    expect(
      host()
        .querySelector<HTMLInputElement>('input[formcontrolname="payee"]')
        ?.getAttribute('maxlength'),
      CAP_ABSENT,
    ).toBe(String(NARRATIVE_NAME_CHARACTERS));
  });

  it('disables the form with a reason while the account is locked', () => {
    // Arrange
    custody.setStatus('locked');

    // Act
    fixture.detectChanges();

    // Assert — disabled in the DOM, not merely dimmed: an enabled form submits,
    // the service refuses because it cannot seal, and nothing happens, which
    // reads as a failure rather than as a limitation.
    const amount = host().querySelector<HTMLInputElement>(
      'input[formcontrolname="amount"]',
    );
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );
    const reason = host().querySelector('form p')?.textContent ?? '';

    expect(amount?.disabled).toBe(true);
    expect(submit?.disabled).toBe(true);
    expect(reason).toContain('Unlock');
    expect(reason).toContain('Settings');
  });

  it('leaves the form enabled while the account is unlocked', () => {
    // Arrange — the control for the case above.

    // Act
    fixture.detectChanges();

    // Assert
    const amount = host().querySelector<HTMLInputElement>(
      'input[formcontrolname="amount"]',
    );

    expect(amount?.disabled).toBe(false);
    expect(host().querySelector('form p')).toBeNull();
  });

  it('refuses to submit while the account is locked', () => {
    // Arrange — the gate is in the handler as well as on the control. A
    // disabled form's status is `DISABLED`, so `form.invalid` answers **false**
    // and a control gated on validity alone comes back to life exactly when it
    // should not; Material's click-halt is anchors only, so the press arrives.
    fill({ description: 'Weekly shop' });
    custody.setStatus('locked');
    fixture.detectChanges();

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(exposed(fixture.componentInstance).form.invalid).toBe(false);
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('renders the locked notice in place of the list while the account is locked', () => {
    // Arrange — rows are present, so this is the notice replacing a list rather
    // than filling an empty one.
    custody.setStatus('locked');

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelector('app-locked-account-notice')).not.toBeNull();
    expect(host().querySelector('.transactions')).toBeNull();
  });

  it('renders the list and no notice while the account is unlocked', () => {
    // Arrange — the control for the case above.

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelector('app-locked-account-notice')).toBeNull();
    expect(host().querySelector('.transactions')).not.toBeNull();
  });

  it('says the list is empty rather than drawing an empty list', () => {
    // Arrange — an answered read of zero rows, which is neither the failure
    // nor the read in flight. The empty state is a `.no-rows` item and
    // deliberately **not** a `.transaction-row`: every count of rows in this
    // file would otherwise answer one over an empty list. That argument is in
    // the header and nothing exercised it, so the `@empty` branch could have
    // been deleted with the suite green.
    transactions.transactionsSignal.set([]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelectorAll('.transaction-row')).toHaveLength(0);
    expect(host().querySelector('.no-rows')).not.toBeNull();
    expect(listText()).toContain('No transactions yet.');
  });

  it('disables the form while the account is unlocking', () => {
    // Arrange — the third word. A predicate written `!== 'locked'` leaves the
    // form live for the whole ceremony, and every save made in that window is
    // refused where nobody can see it.
    custody.setStatus('unlocking');

    // Act
    fixture.detectChanges();

    // Assert
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );

    expect(submit?.disabled).toBe(true);
    expect(host().querySelector('form p')?.textContent ?? '').toContain(
      'Unlock',
    );
  });

  it('keeps the list on screen while the account is unlocking', () => {
    // Arrange — the notice follows `locked` alone, where the form follows
    // "anything but unlocked". The notice's sentence is advice, and that advice
    // is already wrong for somebody whose unlock is running.
    custody.setStatus('unlocking');

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelector('app-locked-account-notice')).toBeNull();
    expect(host().querySelector('.transactions')).not.toBeNull();
  });

  it('renders every sealed member the row still carries through the narrative marker', () => {
    // Arrange — the row is two lines and five things: the payee leads with the
    // amount right of it, and the second line is category · account with the
    // date set right. Three of the five are narrative values, and none of them
    // is a string this template built — a member interpolated straight into
    // the row prints `[object Object]` at best and a collapsed dash at worst.

    // Act
    fixture.detectChanges();

    // Assert
    const rendered = host().querySelectorAll(
      '.transaction-row app-narrative-value',
    );

    expect(rendered).toHaveLength(3);
    expect(listText()).toContain('Corner Shop');
    expect(listText()).toContain('Groceries');
    expect(listText()).toContain('Everyday');
  });

  it('leads the row with the counterparty', () => {
    // Arrange — line 1 is the payee, in `--bud-text`, and the note is not on
    // the row at all where there is one.

    // Act
    fixture.detectChanges();

    // Assert
    expect(leadLine()).toContain('Corner Shop');
    expect(listText()).not.toContain('Weekly shop');
  });

  it('leads the row with the note when the row names no counterparty', () => {
    // Arrange — the parenthesis in the chapter's line 1. `null` is a payee
    // column holding nothing, which the client can see without any key.
    transactions.transactionsSignal.set([
      { ...weeklyShop, payeeId: null, payeeName: null },
    ]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(leadLine()).toContain('Weekly shop');
  });

  it('leads with a counterparty whose name did not open, rather than falling back to the note', () => {
    // Arrange — the two facts this row must keep apart. A row *has* a payee
    // whose name this tab could not read; falling through to the note there
    // would put a different value under the same heading depending on whether
    // a key happened to be held, and nothing on screen would say so.
    transactions.transactionsSignal.set([
      { ...weeklyShop, payeeName: { state: 'unreadable' } },
    ]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(leadLine()).not.toContain('Weekly shop');
    expect(
      host()
        .querySelector('.transaction-row .lead [role="img"]')
        ?.getAttribute('aria-label'),
    ).toBe('Couldn’t be read');
  });

  it('keeps the category group off the row', () => {
    // Arrange — the group is a fifth sealed name the chapter names nowhere,
    // and it shipped on the row. `weeklyShop` carries one, so a template that
    // still drew it would put `Essentials` in the list.

    // Act
    fixture.detectChanges();

    // Assert
    expect(listText()).not.toContain('Essentials');
  });

  it('renders a marker rather than a blank for a value that did not open', () => {
    // Arrange — a row whose account name did not open is a fact about that
    // value, and the rest of the row is fine. A mapper or a template that
    // collapsed it to `''` would make the screen claim the account has no name.
    transactions.transactionsSignal.set([
      { ...weeklyShop, accountName: { state: 'unreadable' } },
    ]);

    // Act
    fixture.detectChanges();

    // Assert — the accessible name is what tells the two dashes apart, and the
    // **whole list** rather than a membership test: `toContain` here passes
    // over a template that drew a second marker somewhere else on the row, and
    // its neighbour one case down already reads the same way.
    const markers = Array.from(
      host().querySelectorAll('.transaction-row [role="img"]'),
    ).map((marker) => marker.getAttribute('aria-label'));

    expect(markers).toEqual(['Couldn’t be read']);
  });

  it('composes a row whose every narrative value failed to authenticate', () => {
    // Arrange — the chapter's totality rule: whatever the wiring does with a
    // value that fails to authenticate, it may not throw while composing the
    // row. Every word on this one is a marker and the note is absent besides.
    transactions.transactionsSignal.set([
      {
        ...weeklyShop,
        accountName: { state: 'unreadable' },
        categoryName: { state: 'unreadable' },
        description: null,
        payeeName: { state: 'unreadable' },
      },
    ]);

    // Act
    fixture.detectChanges();

    // Assert — the row is on screen with its figure and its date intact. The
    // date is the **whole cell** and not a substring of it, for the reason its
    // own case gives: `createdAtUtc` begins with the same ten characters, so a
    // `toContain` here would pass over the wrong member. The figure is asserted
    // only to be non-empty, because no locale is named in this block and the
    // rendered currency is the host machine's answer.
    //
    // **The markers are counted**, and that is the half "the row is on screen"
    // cannot see: all three of this row's narrative members failed, so a
    // template that drew two markers and swallowed the third composes a row
    // that renders, reads as complete, and is missing a value.
    expect(host().querySelectorAll('.transaction-row')).toHaveLength(1);
    expect(
      host().querySelectorAll('.transaction-row [role="img"]'),
    ).toHaveLength(3);
    expect(amountCell().textContent ?? '').not.toBe('');
    expect(dateCell().trim()).toBe('2026-07-14');
  });

  it('leaves out a member the row does not carry', () => {
    // Arrange — a transaction naming no payee and no category. `null` is the
    // column holding nothing, which is not a value that failed to open, so
    // there is no marker to draw for it.
    transactions.transactionsSignal.set([
      {
        ...weeklyShop,
        categoryGroupId: null,
        categoryGroupName: null,
        categoryId: null,
        categoryName: null,
        payeeId: null,
        payeeName: null,
      },
    ]);

    // Act
    fixture.detectChanges();

    // Assert — the note leading and the account name, and nothing standing in
    // for the three that are absent.
    expect(
      host().querySelectorAll('.transaction-row app-narrative-value'),
    ).toHaveLength(2);
  });

  it('says “No category” when the row carries none', () => {
    // Arrange — the branch turns on the category being **absent**, which is a
    // null the client can still see. A plain fact, muted, not a warning.
    transactions.transactionsSignal.set([
      {
        ...weeklyShop,
        categoryGroupId: null,
        categoryGroupName: null,
        categoryId: null,
        categoryName: null,
      },
    ]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(metadataLine()).toContain('No category');
  });

  it('draws a marker, never “No category”, for a category name that did not open', () => {
    // Arrange — the other half of the same rule, and the one a reader folds
    // into the first. This row **has** a category; what failed is reading its
    // name. Saying "No category" here is the screen claiming something about
    // the budget when the truth is about this tab.
    transactions.transactionsSignal.set([
      { ...weeklyShop, categoryName: { state: 'unreadable' } },
    ]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(metadataLine()).not.toContain('No category');
    expect(
      Array.from(
        host().querySelectorAll('.transaction-row .meta [role="img"]'),
      ).map((marker) => marker.getAttribute('aria-label')),
    ).toEqual(['Couldn’t be read']);
  });

  it('puts the date in the second line’s figures slot and nowhere else', () => {
    // Arrange — the date left the metadata run and took the right of line 2,
    // which is the half of the grid the shipped row did not have.

    // Act
    fixture.detectChanges();

    // Assert — the whole cell and not a substring of it: `createdAtUtc` is on
    // this row too and it *begins* with the same ten characters, so a
    // `toContain` here passes over the wrong member.
    expect(dateCell().trim()).toBe('2026-07-14');
    expect(metadataLine()).not.toContain('2026-07-14');
    expect(leadLine()).not.toContain('2026-07-14');
  });

  it('puts exactly one space either side of the separator on line 2 and none at its ends', () => {
    // Arrange — `weeklyShop` carries a category and an account, so the one
    // separator line 2 can draw renders. The defect this pins is a span that
    // opened and then broke the line: the leading newline collapses to a
    // rendered space that the `&nbsp;` after it then doubles.
    //
    // **The expectation is the separator's *spelling*, not a count of the
    // characters around it**, and the difference is the reason it is written
    // this way. A count cannot tell U+00A0 from U+0020, so an `&nbsp;` demoted
    // to an ordinary space passes a counting rule while the separator it was
    // holding becomes free to wrap onto a line of its own — which is the whole
    // job of that entity and not a detail of it.

    // Act
    fixture.detectChanges();
    const line = metadataLine();

    // Assert — the anchor first, so a case that read the wrong node says so
    // instead of passing on an empty string.
    expect(line).toContain('Groceries');
    expect(separatorSpacing(line)).toEqual([
      `${NO_BREAK_SPACE}·${NO_BREAK_SPACE}`,
    ]);
    // A tail no separator rule can reach: one space left inside a span is a
    // run of one, sits beside no `·`, and shows up nowhere on screen.
    expect(edgeWhitespace(line)).toEqual([]);
  });

  it('puts exactly one space either side of the separator on a row with no category and none at its ends', () => {
    // Arrange — the doubling is not a consequence of the category branch, and
    // this is the case that says so: the "No category" arm is a different node
    // with its own whitespace, and it sits beside the same separator.
    transactions.transactionsSignal.set([
      {
        ...weeklyShop,
        categoryGroupId: null,
        categoryGroupName: null,
        categoryId: null,
        categoryName: null,
      },
    ]);

    // Act
    fixture.detectChanges();
    const line = metadataLine();

    // Assert
    expect(line).toContain('No category');
    expect(separatorSpacing(line)).toEqual([
      `${NO_BREAK_SPACE}·${NO_BREAK_SPACE}`,
    ]);
    expect(edgeWhitespace(line)).toEqual([]);
  });

  // Money display — `docs/design/patterns.md`.
  //
  // **The locale is pinned here and in no other place**, which is the shape
  // `credential-registration-date.ts` already argued: production passes
  // `undefined` and gets the reader's own, and the token is the seam a spec
  // provides so that an expectation is a string rather than a claim about the
  // machine the suite happens to run on. Nothing configures `LOCALE_ID` in this
  // application, and `Intl` with no locale falls back to the host's — so an
  // assertion on a formatted figure written without this token is true for
  // whoever wrote it and false for the next person.
  //
  // **Two locales, not one**, and that is the whole guard against hand-assembly:
  // a symbol concatenated onto digits can be made to equal `$20.50`, and there
  // is no way to make it equal `20,50 $`.
  describe('the figure', () => {
    // A fresh injector per case, because the locale is read when the component
    // is built.
    async function figureIn(
      locale: string,
      row: TransactionView,
    ): Promise<HTMLElement> {
      TestBed.resetTestingModule();
      transactions = new TransactionsServiceStub();
      transactions.transactionsSignal.set([row]);
      custody = new CustodyStub();
      TestBed.configureTestingModule({
        imports: [TransactionsComponent],
        providers: [
          provideNoopAnimations(),
          provideRouter([]),
          { provide: TransactionsService, useValue: transactions },
          { provide: AccountsService, useClass: AccountsServiceStub },
          { provide: AccountKeyCustodyService, useValue: custody },
          { provide: TRANSACTION_ROW_LOCALE, useValue: locale },
        ],
      });
      await TestBed.compileComponents();
      fixture = TestBed.createComponent(TransactionsComponent);
      fixture.detectChanges();

      return amountCell();
    }

    it('drops the sign on an expense and keeps the ink neutral', async () => {
      // Arrange — the domain stores a negative; spending is the normal case,
      // and a list of expenses in ink reads as a record rather than a rebuke.

      // Act
      const figure = await figureIn('en-US', weeklyShop);

      // Assert — the two minus glyphs a formatter can emit, neither of them
      // welcome. `−` (U+2212) is what several locales use for a negative.
      expect(figure.textContent ?? '').toBe('$20.50');
      expect(figure.textContent ?? '').not.toMatch(/[-−]/u);
      expect(figure.classList.contains('income')).toBe(false);
    });

    it('marks income with a plus and the positive ink', async () => {
      // Arrange — income is the marked case.

      // Act
      const figure = await figureIn('en-US', { ...weeklyShop, amount: 20.5 });

      // Assert
      expect(figure.textContent ?? '').toBe('+$20.50');
      expect(figure.classList.contains('income')).toBe(true);
    });

    it('leaves a zero unsigned and unmarked', async () => {
      // Arrange — zero is a legal amount and neither of the two marked cases:
      // a purchase a voucher covered in full is a record worth keeping.

      // Act
      const figure = await figureIn('en-US', { ...weeklyShop, amount: 0 });

      // Assert
      expect(figure.textContent ?? '').toBe('$0.00');
      expect(figure.classList.contains('income')).toBe(false);
    });

    it('formats in the reader’s locale rather than assembling a symbol and digits', async () => {
      // Arrange — `currencySymbol` is on the row and printing it in front of
      // the number is what shipped. It cannot produce this.

      // Act
      const figure = await figureIn('de-DE', weeklyShop);

      // Assert — comma for the decimal, and the symbol trailing.
      expect(figure.textContent ?? '').toMatch(/^20,50\s/u);
    });

    it('follows the currency for minor units', async () => {
      // Arrange — cents are always shown, and how many there are is the
      // currency's answer, not two.

      // Act
      const figure = await figureIn('ja-JP', {
        ...weeklyShop,
        amount: -20,
        currencyCode: 'JPY',
        currencySymbol: '￥',
      });

      // Assert
      expect(figure.textContent ?? '').toBe('￥20');
    });

    it('draws a figure rather than taking the screen down when the currency code is one the platform refuses', async () => {
      // Arrange — `Intl.NumberFormat` throws a `RangeError` on a code that is
      // not three letters, and this construction happens inside change
      // detection: the throw would abandon the pass, so every section declared
      // after the list stops rendering and no `try` around a template binding
      // can contain it. The same argument `credential-registration-date.ts`
      // makes for a malformed instant.

      // Act
      const figure = await figureIn('en-US', {
        ...weeklyShop,
        currencyCode: 'XX',
      });

      // Assert — the row is there, the figure is a number, and the sign is
      // still gone.
      expect(host().querySelectorAll('.transaction-row')).toHaveLength(1);
      expect(figure.textContent ?? '').toContain('20.50');
      expect(figure.textContent ?? '').not.toMatch(/[-−]/u);
    });
  });

  it('keeps what was typed while the write is still in flight', () => {
    // Arrange — the reset used to run on the line after the call, so the text
    // was gone before the outcome existed. Four of the service's five exits
    // write nothing, and one of them — a payee whose name does not open —
    // abandons **every** write naming that counterparty, forever. Whoever
    // typed it would watch the form empty each time and be told nothing.
    transactions.add.mockImplementation(() => new Promise(() => undefined));
    fill({ description: 'Weekly shop', payee: 'Corner Shop' });

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    const form = exposed(fixture.componentInstance).form;

    expect(form.value).toEqual(
      expect.objectContaining({
        description: 'Weekly shop',
        payee: 'Corner Shop',
        accountId: ACCOUNT_ID,
      }),
    );
  });

  it('keeps what was typed when the write is abandoned', async () => {
    // Arrange — the four silent exits of the service, from the screen's side.
    // Nothing was written and nothing can be said yet, so the least this form
    // can do is still be holding the entry when the person looks back at it.
    transactions.add.mockResolvedValue({ state: 'unreachable' });
    fill({ description: 'Weekly shop', payee: 'Corner Shop' });

    // Act
    await pressAdd(fixture.componentInstance);

    // Assert
    expect(exposed(fixture.componentInstance).form.value).toEqual(
      expect.objectContaining({
        description: 'Weekly shop',
        payee: 'Corner Shop',
      }),
    );
  });

  it('empties the form once the write has landed', async () => {
    // Arrange — the positive control. A form that never cleared would pass the
    // two cases above and make every second entry a duplicate of the first.
    fill({ description: 'Weekly shop', payee: 'Corner Shop' });

    // Act
    await pressAdd(fixture.componentInstance);

    // Assert
    expect(exposed(fixture.componentInstance).form.value).toEqual(
      expect.objectContaining({
        accountId: '',
        description: '',
        payee: '',
      }),
    );
  });

  it('says the read failed rather than rendering nothing', () => {
    // Arrange — null list, no running flag, and before this branch existed
    // that state drew a form and silence. "You have no transactions" and "we
    // could not ask" are two different next steps for a person.
    transactions.transactionsSignal.set(null);
    transactions.loadingSignal.set(false);
    transactions.failedSignal.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().textContent ?? '').toContain('couldn’t read your');
    expect(host().querySelector('.transactions')).toBeNull();
  });

  it('draws neither the list nor a failure while the read is running', () => {
    // Arrange — the control for the case above, and for the branch order: a
    // failure that outranked the running line would put the sentence on screen
    // during every reload after one failed read.
    transactions.transactionsSignal.set(null);
    transactions.loadingSignal.set(true);
    transactions.failedSignal.set(false);

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().textContent ?? '').toContain('Reading your transactions');
    expect(host().textContent ?? '').not.toContain('couldn’t read your');
  });

  // **Where the two lines land is a property the cases above cannot see.**
  // Each of them asserts the text is somewhere in the host, and a screen that
  // draws each sentence in a `role="status"` created at the moment it gains
  // content passes every one of them while announcing nothing: assistive
  // technology has to have been watching the node *before* the text arrived.
  // So the node is taken while it is still empty and the later text is
  // asserted to arrive **in that same node** — `docs/design/components.md`,
  // "A value read from the network".
  function statusRegion(): HTMLElement | null {
    return host().querySelector<HTMLElement>('[role="status"]');
  }

  it('holds an empty status region from first paint', () => {
    // Arrange — the fixture's own default: a list that answered, nothing
    // running and nothing failed, so there is deliberately nothing to say.

    // Act
    fixture.detectChanges();

    // Assert — present and silent. `status` and never `assertive`, which is
    // reserved for a failure to save something a person typed.
    expect(statusRegion()).not.toBeNull();
    expect((statusRegion()?.textContent ?? '').trim()).toBe('');
    expect(
      host().querySelector('[role="alert"], [aria-live="assertive"]'),
    ).toBeNull();
  });

  it('carries exactly one status region in every state it renders', () => {
    // Arrange — the **count**, which no case above can see. `querySelector`
    // takes the first match in document order, so a second region added after
    // this one leaves every one of them green while the screen announces its
    // reads twice — and which of the two a person hears is then decided by the
    // order the template happens to be written in. Counted in each state the
    // section renders, because a region added inside a branch is invisible
    // from any other one; **where** the region sits is deliberately not
    // asserted, that being a layout decision `docs/design/components.md` owns.
    const states = [
      { apply: () => undefined, name: 'a list on screen' },
      {
        apply: () => {
          transactions.transactionsSignal.set(null);
          transactions.loadingSignal.set(true);
        },
        name: 'a read in flight',
      },
      {
        apply: () => {
          transactions.transactionsSignal.set(null);
          transactions.loadingSignal.set(false);
          transactions.failedSignal.set(true);
        },
        name: 'a read that failed',
      },
      {
        apply: () => {
          transactions.transactionsSignal.set(null);
          custody.setStatus('locked');
        },
        name: 'a locked account',
      },
    ];

    for (const state of states) {
      // Act
      state.apply();
      fixture.detectChanges();

      // Assert — wrapped with the state's name so a failure says which one
      // grew the second region.
      expect({
        regions: host().querySelectorAll('[role="status"]').length,
        state: state.name,
      }).toEqual({ regions: 1, state: state.name });
    }
  });

  it('announces the loading line from the region that was already there', () => {
    // Arrange — taken while it is still empty, which is the whole point of
    // taking it here rather than after the act.
    const region = statusRegion();

    // Act
    transactions.transactionsSignal.set(null);
    transactions.loadingSignal.set(true);
    fixture.detectChanges();

    // Assert — the same element, not a second one that arrived with its text.
    expect(statusRegion()).toBe(region);
    expect(region?.textContent ?? '').toContain('Reading your transactions');
  });

  it('announces the failure sentence from that same region', () => {
    // Arrange
    const region = statusRegion();

    // Act
    transactions.transactionsSignal.set(null);
    transactions.loadingSignal.set(false);
    transactions.failedSignal.set(true);
    fixture.detectChanges();

    // Assert
    expect(statusRegion()).toBe(region);
    expect(region?.textContent ?? '').toContain('couldn’t read your');
  });

  it('says nothing at all while the account is locked', () => {
    // Arrange — the state a lock actually leaves behind: `TransactionsService`
    // destroys the list and leaves **both** flags standing — it clears neither
    // the running one nor `failed`, unlike its two neighbours, which clear
    // `failed` — so a region reading those flags alone tells somebody a read is
    // in flight, or that one failed, beside a notice saying this tab cannot
    // read the account. The chain this region replaced answered that by putting
    // `locked` first, and the predicate has to keep doing it.
    transactions.transactionsSignal.set(null);
    transactions.loadingSignal.set(true);
    custody.setStatus('locked');

    // Act
    fixture.detectChanges();

    // Assert — still in the DOM, with nothing to say. The notice is what
    // speaks for this state.
    expect(statusRegion()).not.toBeNull();
    expect((statusRegion()?.textContent ?? '').trim()).toBe('');
  });

  it('says it is reading, not that it failed, when both flags are up', () => {
    // Arrange — reachable, and not by contrivance: only `load()` clears
    // `failed`, so a failed read followed by a press on Add leaves the list
    // null, `failed` true and the running flag true at once. Ordered the other
    // way the screen tells somebody to check their connection while a request
    // of theirs is in flight. The text cases above each set one flag, so none
    // of them can see this.
    transactions.transactionsSignal.set(null);
    transactions.failedSignal.set(true);
    transactions.loadingSignal.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    expect(statusRegion()?.textContent ?? '').toContain(
      'Reading your transactions',
    );
    expect(statusRegion()?.textContent ?? '').not.toContain(
      'couldn’t read your',
    );
  });

  it('says nothing while a write runs over a list already on screen', () => {
    // Arrange — `loading` is set by every **write** as well as by the read,
    // and the list stays up throughout one. The chain this region replaced put
    // the list ahead of the loading line, so a save never drew "Reading your
    // transactions…" under the rows; a region reading the running flag alone
    // brings that back, and the rule is the book's — a section renders at most
    // one of the value, the loading line and the failure.
    transactions.loadingSignal.set(true);

    // Act
    fixture.detectChanges();

    // Assert — the value is on screen, so the region has nothing to add.
    expect(host().querySelector('.transactions')).not.toBeNull();
    expect((statusRegion()?.textContent ?? '').trim()).toBe('');
  });

  it('disables the submit while a write is running', () => {
    // Arrange — the write is **two** round trips, and the running flag is what
    // stands between a double press and a duplicate entry. What blocks the
    // second press today is an accident: the form reset clears a `required`
    // account and the button falls invalid, which evaporates the day the reset
    // keeps the account selected — and the second POST carries a fresh
    // client-minted id that collides with nothing.
    fill({});
    transactions.loadingSignal.set(true);

    // Act
    fixture.detectChanges();

    // Assert
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );

    expect(submit?.disabled).toBe(true);
  });

  it('refuses a second press while a write is running', () => {
    // Arrange — the gate is in the handler as well as on the control, for the
    // reason this screen gives three times already: Material's click-halt is
    // applied to anchors only, so a `<button>` still receives the press.
    fill({});
    transactions.loadingSignal.set(true);
    fixture.detectChanges();

    // Act
    exposed(fixture.componentInstance).add();

    // Assert
    expect(transactions.add).not.toHaveBeenCalled();
  });

  it('shows no opened name in the form while the account is locked', () => {
    // Arrange — the account picker, the payee field and the category picker
    // sit inside the form rather than inside the notice's branch, so a lock
    // that lands over a filled form disables the controls and leaves the names
    // it opened on screen. The notice renders **in place of** account content;
    // a disabled control still displaying it is the same claim by another
    // route.
    fill({ categoryId: CATEGORY_ID });
    fixture.detectChanges();
    expect(host().querySelector('form')?.textContent ?? '').toContain(
      'Everyday',
    );

    // Act
    custody.setStatus('locked');
    fixture.detectChanges();

    // Assert
    const form = host().querySelector('form')?.textContent ?? '';

    expect(form).not.toContain('Everyday');
    expect(form).not.toContain('Groceries');
  });

  it('offers no payee suggestions while the account is locked', () => {
    // Arrange — the same rule on the third control. A suggestion list is
    // account content whatever the field around it can do.
    transactions.payeesSignal.set([cornerShop, bakery]);
    custody.setStatus('locked');

    // Act
    fixture.detectChanges();

    // Assert
    expect(suggestions(fixture.componentInstance)).toEqual([]);
  });

  it('reads a null account list as no answer yet, in the sentence and on the button alike', () => {
    // Arrange — `?.length === 0` and `(… ?? 0) === 0` disagree about `null`,
    // and they disagreed toward silence: a failed accounts read hid the
    // sentence *and* disabled the button, where the two used to arrive
    // together. Both read `null` as "no answer yet" now — saying "create an
    // account" over a read that never landed is a claim about the budget
    // rather than about the request, and the account control is `required`, so
    // nothing becomes pressable that a person could not have filled in.
    const accounts = TestBed.inject(
      AccountsService,
    ) as unknown as AccountsServiceStub;

    accounts.accountsSignal.set(null);
    accounts.loadingSignal.set(false);
    fill({});

    // Act
    fixture.detectChanges();

    // Assert
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );

    expect(host().textContent ?? '').not.toContain('Create an account');
    expect(submit?.disabled).toBe(false);
  });

  it('still asks for an account when the budget has none', () => {
    // Arrange — the positive control for the case above: an answered read of
    // zero rows is a claim about the budget, and it is this screen's to make.
    const accounts = TestBed.inject(
      AccountsService,
    ) as unknown as AccountsServiceStub;

    accounts.accountsSignal.set([]);
    accounts.loadingSignal.set(false);
    fill({});

    // Act
    fixture.detectChanges();

    // Assert
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );

    expect(host().textContent ?? '').toContain('Create an account');
    expect(submit?.disabled).toBe(true);
  });

  it('offers only the payees matching what has been typed', () => {
    // Arrange — the filter's only case read it with an **empty** filter, so an
    // implementation that dropped the body and returned everything passed. Two
    // payees and a filter that admits one of them is what tells the two apart.
    transactions.payeesSignal.set([cornerShop, bakery]);
    fill({ payee: 'bak' });

    // Act
    fixture.detectChanges();

    // Assert — case-folded on both sides: what is typed is not what was
    // stored.
    expect(
      suggestions(fixture.componentInstance).map((payee) => payee.id),
    ).toEqual([bakery.id]);
  });

  it('suggests only the payees it could read', () => {
    // Arrange — an autocomplete offers text to put into a text field, and a row
    // with no text has nothing to offer. Filtering is not collapsing: no word
    // is turned into a string, the row is simply not a suggestion.
    transactions.payeesSignal.set([cornerShop, unreadablePayee]);

    // Act
    fixture.detectChanges();

    // Assert
    expect(
      suggestions(fixture.componentInstance).map((payee) => payee.id),
    ).toEqual([PAYEE_ID]);
  });

  // A write that does not happen — `docs/design/components.md`.
  //
  // **This screen already kept what was typed; what it could not do was say
  // anything.** `accounts.component.spec.ts` holds the shared rules. Two things
  // are this screen's own: it is the one surface whose write is more than one
  // request, so it is the one that narrates a write in progress; and
  // `duplicate-name` reaches a person here and nowhere else in the product,
  // because the payee create is the only write that answers a repeated name
  // with a 409 and the form's own re-read resolves the ordinary case silently.
  describe('a write that does not happen', () => {
    function regionText(): string {
      return statusRegion()?.textContent ?? '';
    }

    function fieldErrors(): string[] {
      return Array.from(host().querySelectorAll('mat-error')).map((error) =>
        (error.textContent ?? '').trim(),
      );
    }

    it('narrates a write that is more than one request, and stops when it answers', async () => {
      // Arrange — the one row of the table with a copy that is not a refusal:
      // `body` `--bud-text`, because nothing has gone wrong. Read off the
      // screen's own flag and never off `TransactionsService.loading`, which is
      // raised by the three reads this screen starts as well.
      let land = (): void => undefined;

      transactions.add.mockImplementation(
        () =>
          new Promise((resolve) => {
            land = () => resolve({ state: 'recorded' });
          }),
      );
      fill({ payee: 'Corner Shop' });

      // Act
      const write = pressAdd(fixture.componentInstance);

      fixture.detectChanges();
      const running = regionText();

      land();
      await write;
      fixture.detectChanges();

      // Assert
      expect(running).toContain('Recording');
      expect(regionText().trim()).toBe('');
    });

    it('sends somebody to the list when a counterparty of that name cannot be read', async () => {
      // Arrange — `duplicate-name`'s one source. The row is in the list
      // wearing the unreadable marker, so choosing it is the remedy and Unlock
      // is not: this form is reachable only on an unlocked account.
      transactions.add.mockResolvedValue({ state: 'duplicate-name' });
      fill({ payee: 'Corner Shop' });

      // Act
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText()).toContain('already exists under a name this tab');
      expect(regionText()).toContain('Choose it from the list');
      expect(host().querySelectorAll('[role="status"]')).toHaveLength(1);
      expect(
        host().querySelector('[role="alert"], [aria-live="assertive"]'),
      ).toBeNull();
    });

    it('tells a server that failed from one that judged', async () => {
      // Arrange — two next steps, two sentences. Folding them sends somebody
      // to press the same button until they give up.
      transactions.add.mockResolvedValue({ state: 'unreachable' });
      fill({});

      // Act
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();
      const silence = regionText();

      transactions.add.mockResolvedValue({ state: 'unreadable' });
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(silence).toContain('try again in a minute');
      expect(regionText()).toContain('copy it, then reload the page');
      expect(regionText()).not.toContain('try again');
    });

    it('puts a counterparty’s sentence beneath the field it was typed into', async () => {
      // Arrange — `PayeeId` is a member no control carries, and the payee name
      // field is the only control its value was ever resolved from, so that is
      // where a correction is made.
      transactions.add.mockResolvedValue({
        errors: new Map([['PayeeId', ['No such payee.']]]),
        state: 'invalid',
      });
      fill({ payee: 'Corner Shop' });

      // Act
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(fieldErrors()).toEqual(['No such payee.']);
      expect(regionText().trim()).toBe('');
      expect(document.activeElement).toBe(
        host().querySelector('input[formcontrolname="payee"]'),
      );
    });

    it('puts a key this form cannot place into the region instead', async () => {
      // Arrange — the row identifier is minted in the browser and no control
      // carries it, so there is no field to hang a message on. The wire key
      // itself is not printed: `Id` is a member name rather than a label
      // anybody recognises.
      transactions.add.mockResolvedValue({
        errors: new Map([['Id', ['Malformed identifier.']]]),
        state: 'invalid',
      });
      fill({});

      // Act
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText()).toContain('Malformed identifier.');
      expect(fieldErrors()).toEqual([]);
    });

    it('says nothing at all when the write never left the browser', async () => {
      // Arrange — `locked` has no row in the chapter's table: the locked
      // notice is already the account of it, and a second sentence is the
      // duplicate the region refuses.
      transactions.add.mockResolvedValue({ state: 'locked' });
      fill({});

      // Act
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText().trim()).toBe('');
      expect(fieldErrors()).toEqual([]);
    });

    it('gives a read in flight the region and hands it back when the read answers', async () => {
      // Arrange — the rule a reader will "fix". Only the **next write** clears
      // a write's sentence, so a refusal made before a slow read reappears the
      // moment the read answers. The form is still holding the text that was
      // refused, so the sentence is as true as when it was written.
      transactions.add.mockResolvedValue({ state: 'unreachable' });
      fill({});
      await pressAdd(fixture.componentInstance);
      fixture.detectChanges();

      // Act
      transactions.transactionsSignal.set(null);
      transactions.loadingSignal.set(true);
      fixture.detectChanges();
      const duringRead = regionText();

      transactions.loadingSignal.set(false);
      transactions.failedSignal.set(true);
      fixture.detectChanges();

      // Assert
      expect(duringRead).toContain('Reading your transactions');
      expect(duringRead).not.toContain('couldn’t reach the server');
      expect(regionText()).toContain('couldn’t reach the server');
      expect(regionText()).not.toContain('Check your connection');
    });
  });
});
