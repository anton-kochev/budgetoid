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
import { signal, type Signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormGroup } from '@angular/forms';
import { MatSelect } from '@angular/material/select';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import type { CategoryGroupView } from '../categories/category-group-view';
import type { CategoryView } from '../categories/category-view';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountView } from '../accounts/account-view';
import { AccountsService } from '../accounts/accounts.service';
import type { PayeeView } from './payee-view';
import type { TransactionView } from './transaction-view';
import { TransactionsComponent } from './transactions.component';
import { TransactionsService } from './transactions.service';

const TRANSACTION_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const ACCOUNT_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const PAYEE_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000004';
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000005';

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

  public readonly transactions = this.transactionsSignal.asReadonly();
  public readonly payees = this.payeesSignal.asReadonly();
  public readonly categoryGroups = this.categoryGroupsSignal.asReadonly();
  public readonly categories = this.categoriesSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load = vi.fn();
  public loadPayees = vi.fn();
  public loadCategories = vi.fn();
  public categoriesForGroup = vi.fn((): readonly CategoryView[] =>
    this.categoriesSignal(),
  );
  public add = vi.fn((): Promise<void> => Promise.resolve());
}

class AccountsServiceStub
  implements Pick<AccountsService, keyof AccountsService>
{
  public readonly accountsSignal = signal<AccountView[] | null>([everyday]);
  public readonly loadingSignal = signal(false);

  public readonly accounts = this.accountsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();

  public load = vi.fn();
  public add = vi.fn((): Promise<void> => Promise.resolve());
  public update = vi.fn((): Promise<void> => Promise.resolve());
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

describe('TransactionsComponent', () => {
  let transactions: TransactionsServiceStub;
  let custody: CustodyStub;
  let fixture: ComponentFixture<TransactionsComponent>;

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
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
    await TestBed.configureTestingModule({
      imports: [TransactionsComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: TransactionsService, useValue: transactions },
        { provide: AccountsService, useClass: AccountsServiceStub },
        { provide: AccountKeyCustodyService, useValue: custody },
      ],
    }).compileComponents();
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

  it('loads categories for the grouped picker', () => {
    // Assert
    expect(transactions.loadCategories).toHaveBeenCalledOnce();
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
    expect(host().querySelector('mat-list')).toBeNull();
  });

  it('renders the list and no notice while the account is unlocked', () => {
    // Arrange — the control for the case above.

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelector('app-locked-account-notice')).toBeNull();
    expect(host().querySelector('mat-list')).not.toBeNull();
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
    expect(host().querySelector('mat-list')).not.toBeNull();
  });

  it('renders every sealed member of a row through the narrative marker', () => {
    // Arrange — five values, five renders, and none of them a string this
    // template built. A member interpolated straight into the row prints
    // `[object Object]` at best and a collapsed dash at worst.

    // Act
    fixture.detectChanges();

    // Assert
    const rendered = host().querySelectorAll(
      'mat-list-item app-narrative-value',
    );

    expect(rendered).toHaveLength(5);
    expect(host().textContent ?? '').toContain('Weekly shop');
    expect(host().textContent ?? '').toContain('Corner Shop');
    expect(host().textContent ?? '').toContain('Essentials');
  });

  it('renders a marker rather than a blank for a value that did not open', () => {
    // Arrange — a row whose note did not open is a fact about that value, and
    // the rest of the row is fine. A mapper or a template that collapsed it to
    // `''` would make the screen claim nobody wrote a note.
    transactions.transactionsSignal.set([
      { ...weeklyShop, description: { state: 'unreadable' } },
    ]);

    // Act
    fixture.detectChanges();

    // Assert — the accessible name is what tells the two dashes apart.
    const markers = Array.from(
      host().querySelectorAll('mat-list-item [role="img"]'),
    ).map((marker) => marker.getAttribute('aria-label'));

    expect(markers).toContain('Couldn’t be read');
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

    // Assert — the note and the account name, and nothing standing in for the
    // three that are absent.
    expect(
      host().querySelectorAll('mat-list-item app-narrative-value'),
    ).toHaveLength(2);
  });

  it('suggests only the payees it could read', () => {
    // Arrange — an autocomplete offers text to put into a text field, and a row
    // with no text has nothing to offer. Filtering is not collapsing: no word
    // is turned into a string, the row is simply not a suggestion.
    transactions.payeesSignal.set([cornerShop, unreadablePayee]);

    // Act
    fixture.detectChanges();

    // Assert
    const suggestions = (
      fixture.componentInstance as unknown as {
        filteredPayees: () => readonly { id: string }[];
      }
    ).filteredPayees();

    expect(suggestions.map((payee) => payee.id)).toEqual([PAYEE_ID]);
  });
});
