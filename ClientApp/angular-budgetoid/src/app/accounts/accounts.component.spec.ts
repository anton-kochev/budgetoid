// The accounts screen, driven against two hand-written stubs.
//
// **Custody is stubbed for its `status` alone, and the stub's status is its own
// settable signal.** The real service reaches `locked` only by never having
// been unlocked or by a failed ceremony, neither of which this runner can
// stage; and a stub that *derived* the reading from something else would be a
// second copy of the predicate, pinning nothing about which object the screen
// asked.
//
// **`implements Pick<S, keyof S>` on both stubs** is the compiler's own census
// of what each service publishes — `keyof` over a class yields the public
// surface only — so a member the screen starts reaching for is an error here
// rather than an `is not a function` during change detection.
//
// Four behaviours, and each of them is silent when broken. A whitespace-only
// name now reaches the server because `Validators.required` admits `'   '` and
// the `.trim()` that used to catch it is gone — the client may not alter what
// it seals. A form that stays enabled while the account is locked submits,
// gets refused where nobody can see it, and leaves somebody unable to tell a
// limitation from a failure. A list rendered over a locked account is a column
// of em dashes where the way out is one press on another screen. And an Edit
// control left live on a row whose name did not open prefills an empty field
// and seals a blank over a name that is still sitting in the column.
//
// **`unlocking` gets its own two cases, because it is where the two predicates
// disagree.** The form follows "anything but `unlocked`" and the notice
// follows `locked` exactly, so a screen written with one predicate passes half
// of what is here and fails the other half whichever way it was written.
// Nothing in this product reaches `unlocking` on this route today — the
// ceremony runs from Settings — so these two cases are the whole of what holds
// that split.
import { signal, type Signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormGroup } from '@angular/forms';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import {
  CurrencyApiService,
  type CurrencyListResponse,
} from '@app-core/api/currency-api.service';
import {
  AccountKeyCustodyService,
  type AccountKeyStatus,
  type UnlockFailure,
} from '@app-core/security/account-key-custody.service';
import { Observable, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountView } from './account-view';
import { AccountsComponent } from './accounts.component';
import { AccountsService } from './accounts.service';

const everyday: AccountView = {
  createdAtUtc: '2026-01-02T03:04:05Z',
  currencyCode: 'USD',
  currencyMinorUnit: 2,
  currencyName: 'US Dollar',
  currencySymbol: '$',
  id: '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e',
  name: { state: 'text', value: 'Everyday' },
  openingBalance: 0,
  type: 'Checking',
};

// A row whose name is this account's and did not open under this account's key
// — reachable on an unlocked screen, where every other row is fine.
const damaged: AccountView = {
  ...everyday,
  id: '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6f',
  name: { state: 'unreadable' },
};

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
    throw new Error('the accounts screen may not unlock the account');
  }

  public adopt(): void {
    throw new Error('the accounts screen may not adopt account keys');
  }

  public lock(): void {
    throw new Error('the accounts screen may not lock the account');
  }

  public sealField(): never {
    throw new Error('the accounts screen may not seal — the service does');
  }

  public openField(): never {
    throw new Error('the accounts screen may not open — the mapper does');
  }

  public blindIndex(): never {
    throw new Error('the accounts screen may not index — the service does');
  }
}

class CurrencyApiStub implements Pick<CurrencyApiService, 'getCurrencies'> {
  public getCurrencies = vi.fn(
    (): Observable<CurrencyListResponse> =>
      of({
        items: [{ code: 'USD', name: 'US Dollar', symbol: '$', minorUnit: 2 }],
      }),
  );
}

// The screen's own members are `protected`, which is right for a template and
// leaves a spec nothing to hold. The same cast `transactions.component.spec.ts`
// uses, in one place, so the reach is visible rather than scattered.
function exposed(component: AccountsComponent): {
  form: FormGroup;
  save: () => void;
  edit: (account: AccountView) => void;
} {
  return component as unknown as {
    form: FormGroup;
    save: () => void;
    edit: (account: AccountView) => void;
  };
}

describe('AccountsComponent', () => {
  let accounts: AccountsServiceStub;
  let custody: CustodyStub;
  let fixture: ComponentFixture<AccountsComponent>;

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  // The per-row Edit controls, in list order. Found by their label rather than
  // by a class, so a stylesheet change cannot silently empty this list.
  function editButtons(): HTMLButtonElement[] {
    return Array.from(
      host().querySelectorAll<HTMLButtonElement>('mat-list-item button'),
    ).filter((button) => (button.textContent ?? '').trim() === 'Edit');
  }

  beforeEach(async () => {
    accounts = new AccountsServiceStub();
    custody = new CustodyStub();
    await TestBed.configureTestingModule({
      imports: [AccountsComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: AccountsService, useValue: accounts },
        { provide: AccountKeyCustodyService, useValue: custody },
        { provide: CurrencyApiService, useValue: new CurrencyApiStub() },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AccountsComponent);
    fixture.detectChanges();
  });

  it('refuses a whitespace-only name', () => {
    // Arrange — `Validators.required` admits this, and the `.trim()` that used
    // to swallow it is gone: the client may not alter what it seals.
    const screen = exposed(fixture.componentInstance);

    screen.form.setValue({
      name: '   ',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });

    // Act
    screen.save();

    // Assert
    expect(screen.form.invalid).toBe(true);
    expect(accounts.add).not.toHaveBeenCalled();
  });

  it('accepts a name that holds text', () => {
    // Arrange — the positive control: a refusal test alone passes just as well
    // against a form nothing can ever satisfy.
    const screen = exposed(fixture.componentInstance);

    screen.form.setValue({
      name: '  Everyday  ',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });

    // Act
    screen.save();

    // Assert — untrimmed on the way past, because the service seals exactly
    // what was typed.
    expect(accounts.add).toHaveBeenCalledWith({
      name: '  Everyday  ',
      type: 'Checking',
      openingBalance: 0,
      currencyCode: 'USD',
    });
  });

  it('disables the form with a reason while the account is locked', () => {
    // Arrange
    custody.setStatus('locked');

    // Act
    fixture.detectChanges();

    // Assert — disabled in the DOM, not merely dimmed: an enabled form
    // submits, the service refuses because it cannot seal, and nothing
    // happens, which reads as a failure rather than as a limitation.
    const name = host().querySelector<HTMLInputElement>(
      'input[formcontrolname="name"]',
    );
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );
    const reason = host().querySelector('form p')?.textContent ?? '';

    expect(name?.disabled).toBe(true);
    expect(submit?.disabled).toBe(true);
    expect(reason).toContain('Unlock');
    expect(reason).toContain('Settings');
  });

  it('leaves the form enabled while the account is unlocked', () => {
    // Arrange — the control for the case above.

    // Act
    fixture.detectChanges();

    // Assert
    const name = host().querySelector<HTMLInputElement>(
      'input[formcontrolname="name"]',
    );

    expect(name?.disabled).toBe(false);
    expect(host().querySelector('form p')).toBeNull();
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
    expect(host().textContent ?? '').toContain('Everyday');
  });

  it('disables the form while the account is unlocking', () => {
    // Arrange — the third word. A predicate written `!== 'locked'` leaves the
    // form live for the whole ceremony, and every save made in that window is
    // refused where nobody can see it. The rule is that the form is usable
    // only when the status is `unlocked`.
    custody.setStatus('unlocking');

    // Act
    fixture.detectChanges();

    // Assert
    const name = host().querySelector<HTMLInputElement>(
      'input[formcontrolname="name"]',
    );
    const submit = host().querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );

    expect(name?.disabled).toBe(true);
    expect(submit?.disabled).toBe(true);
    expect(host().querySelector('form p')?.textContent ?? '').toContain(
      'Unlock',
    );
  });

  it('keeps the list on screen while the account is unlocking', () => {
    // Arrange — the notice follows `locked` alone, where the form follows
    // "anything but unlocked". Two questions, two predicates: the notice's
    // sentence tells somebody to go and press Unlock, and that advice is
    // already wrong for a person whose unlock is running.
    custody.setStatus('unlocking');

    // Act
    fixture.detectChanges();

    // Assert
    expect(host().querySelector('app-locked-account-notice')).toBeNull();
    expect(host().querySelector('mat-list')).not.toBeNull();
  });

  it('refuses to edit a row whose name did not open', () => {
    // Arrange — rewriting a value nobody can read is not an edit, it is a
    // deletion wearing an edit's clothes: the field would prefill empty and
    // the save would seal a blank over a name that is still there.
    accounts.accountsSignal.set([damaged]);
    const screen = exposed(fixture.componentInstance);

    // Act
    fixture.detectChanges();
    screen.edit(damaged);

    // Assert — disabled in the DOM, with the reason in the row, and the gate
    // repeated in the handler because Material's click-halt is anchors only.
    const edit = editButtons().at(0);

    expect(edit?.disabled).toBe(true);
    expect(host().textContent ?? '').toContain('can’t be renamed');
    expect(screen.form.getRawValue()).toMatchObject({ name: '' });
  });

  it('allows editing a row whose name opened', () => {
    // Arrange — the control for the case above.

    // Act
    fixture.detectChanges();
    exposed(fixture.componentInstance).edit(everyday);

    // Assert
    expect(editButtons().at(0)?.disabled).toBe(false);
    expect(exposed(fixture.componentInstance).form.getRawValue()).toMatchObject(
      { name: 'Everyday' },
    );
  });
});
