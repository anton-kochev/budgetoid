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
// Five behaviours, and each of them is silent when broken. A whitespace-only
// name now reaches the server because `Validators.required` admits `'   '` and
// the `.trim()` that used to catch it is gone — the client may not alter what
// it seals. A form that stays enabled while the account is locked submits,
// gets refused where nobody can see it, and leaves somebody unable to tell a
// limitation from a failure. A list rendered over a locked account is a column
// of em dashes where the way out is one press on another screen. An Edit
// control left live on a row whose name did not open prefills an empty field
// and seals a blank over a name that is still sitting in the column. And the
// list is `null` at rest, in flight **and** after a failure, so a screen
// reading the list and the running flag alone draws a form and silence over a
// read that never landed — which reads as an account with nothing in it.
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
import type { WriteOutcome } from '@app-core/api/write-outcome';
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
  public readonly failedSignal = signal(false);

  public readonly accounts = this.accountsSignal.asReadonly();
  public readonly loading = this.loadingSignal.asReadonly();
  public readonly failed = this.failedSignal.asReadonly();

  public load = vi.fn();
  // Answers `recorded` by default, because that is the path a case saying
  // nothing about the outcome means.
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
//
// **`editingId` is reached for because nothing else can see the mode change.**
// The form starts holding `name: ''`, so a gate replaced by
// `name: account.name.state === 'text' ? account.name.value : ''` writes the
// value the prefill assertion already expects and passes it — the refusal has
// to be observed on the state the press was supposed to change.
interface Exposed {
  editingId: Signal<string | null>;
  form: FormGroup;
  save: () => void;
  edit: (account: AccountView) => void;
}

function exposed(component: AccountsComponent): Exposed {
  return component as unknown as Exposed;
}

// The submit, as the promise it now is. Kept apart from {@link exposed} so that
// the cases which only press the control go on ignoring the result and stay
// free of a floating promise — the same split `transactions.component.spec.ts`
// makes for the same reason.
function pressSave(component: AccountsComponent): Promise<void> {
  return (component as unknown as { save: () => Promise<void> }).save();
}

// A validation refusal, built from pairs. The API's keys are C# member names
// and this project's lint rule demands camelCase of an object literal's
// properties, so a literal cannot spell what the wire actually sends.
function invalid(
  ...entries: readonly (readonly [string, readonly string[]])[]
): WriteOutcome {
  return { errors: new Map(entries), state: 'invalid' };
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

  // The form's Cancel, which the template renders only while an edit is
  // running. It is the DOM's own statement about the mode, and the second
  // discriminator the edit-gate cases needed.
  function cancelButton(): HTMLButtonElement | null {
    return (
      Array.from(
        host().querySelectorAll<HTMLButtonElement>('form button'),
      ).find((button) => (button.textContent ?? '').trim() === 'Cancel') ?? null
    );
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
    //
    // **The prefill assertion cannot see that on its own**, and the two mode
    // assertions below are what this case was missing. The form *starts*
    // holding `name: ''`, so a gate replaced by
    // `name: account.name.state === 'text' ? account.name.value : ''` writes
    // exactly the value the prefill check expects — measured — and the two DOM
    // assertions used to read markup rendered before the press. `editingId` and
    // the Cancel control are the state the press was supposed to change, so a
    // handler that entered edit mode over an unreadable row reddens on them.
    accounts.accountsSignal.set([damaged]);
    const screen = exposed(fixture.componentInstance);

    // Act
    fixture.detectChanges();
    screen.edit(damaged);
    fixture.detectChanges();

    // Assert — disabled in the DOM, with the reason in the row, and the gate
    // repeated in the handler because Material's click-halt is anchors only.
    expect(editButtons().at(0)?.disabled).toBe(true);
    expect(host().textContent ?? '').toContain('can’t be renamed');
    expect(screen.form.getRawValue()).toMatchObject({ name: '' });
    expect(screen.editingId()).toBeNull();
    expect(cancelButton()).toBeNull();
  });

  describe('a list with no answer', () => {
    it('says it is reading while a load is in flight', () => {
      // Arrange — the list is null at rest, in flight and after a failure, so
      // the loading line is read off the published running state rather than
      // off the absent value.
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('Reading your accounts');
      expect(host().textContent ?? '').not.toContain('No accounts yet');
    });

    it('says there are none only once a server has answered', () => {
      // Arrange — `[]` is the sentence *you have no accounts*, which is a claim
      // only a server that answered may make.
      accounts.accountsSignal.set([]);
      accounts.loadingSignal.set(false);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('No accounts yet');
    });

    it('says the read failed rather than rendering nothing', () => {
      // Arrange — null list, no running flag, and before this branch existed
      // that state drew a form and silence beneath it, which reads as an
      // account with nothing in it. "You have no accounts" and "we couldn't
      // ask" are two different next steps for a person, and a screen that
      // renders neither sentence has collapsed them into a blank.
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(false);
      accounts.failedSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('couldn’t read your');
      expect(host().querySelector('mat-list')).toBeNull();
    });

    it('draws neither the list nor a failure while the read is running', () => {
      // Arrange — the control for the case above, and for the branch order: a
      // failure that outranked the running line would put the sentence on
      // screen during every reload after one failed read.
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(true);
      accounts.failedSignal.set(false);

      // Act
      fixture.detectChanges();

      // Assert
      expect(host().textContent ?? '').toContain('Reading your accounts');
      expect(host().textContent ?? '').not.toContain('couldn’t read your');
    });

    // **Where the two lines land is a property the four cases above cannot
    // see.** Every one of them asserts the text is somewhere in the host, and
    // a screen that draws each sentence in a `role="status"` created at the
    // moment it gains content passes all four while announcing nothing:
    // assistive technology has to have been watching the node *before* the
    // text arrived. So the node is taken while it is still empty and the later
    // text is asserted to arrive **in that same node** — `docs/design/
    // components.md`, "A value read from the network".
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
      // Arrange — the **count**, which no case above can see.
      // `querySelector` takes the first match in document order, so a second
      // region added after this one leaves every one of them green while the
      // screen announces its reads twice — and which of the two a person hears
      // is then decided by the order the template happens to be written in.
      // Counted in each state the section renders, because a region added
      // inside a branch is invisible from any other one; **where** the region
      // sits is deliberately not asserted, that being a layout decision
      // `docs/design/components.md` owns.
      const states = [
        { apply: () => undefined, name: 'a list on screen' },
        {
          apply: () => {
            accounts.accountsSignal.set(null);
            accounts.loadingSignal.set(true);
          },
          name: 'a read in flight',
        },
        {
          apply: () => {
            accounts.accountsSignal.set(null);
            accounts.loadingSignal.set(false);
            accounts.failedSignal.set(true);
          },
          name: 'a read that failed',
        },
        {
          apply: () => {
            accounts.accountsSignal.set(null);
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
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(true);
      fixture.detectChanges();

      // Assert — the same element, not a second one that arrived with its
      // text.
      expect(statusRegion()).toBe(region);
      expect(region?.textContent ?? '').toContain('Reading your accounts');
    });

    it('announces the failure sentence from that same region', () => {
      // Arrange
      const region = statusRegion();

      // Act
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(false);
      accounts.failedSignal.set(true);
      fixture.detectChanges();

      // Assert
      expect(statusRegion()).toBe(region);
      expect(region?.textContent ?? '').toContain('couldn’t read your');
    });

    it('says nothing at all while the account is locked', () => {
      // Arrange — the state a lock actually leaves behind: `AccountsService`
      // destroys the list and clears `failed`, and it does **not** clear the
      // running flag, so a region reading the load alone tells somebody a read
      // is in flight beside a notice saying this tab cannot read the account.
      // The chain this region replaced answered that by putting `locked`
      // first, and the predicate has to keep doing it.
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(true);
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
      // null, `failed` true and the running flag true at once. Ordered the
      // other way the screen tells somebody to check their connection while a
      // request of theirs is in flight. The four text cases above each set one
      // flag, so none of them can see this.
      accounts.accountsSignal.set(null);
      accounts.failedSignal.set(true);
      accounts.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert
      expect(statusRegion()?.textContent ?? '').toContain(
        'Reading your accounts',
      );
      expect(statusRegion()?.textContent ?? '').not.toContain(
        'couldn’t read your',
      );
    });

    it('says nothing while a write runs over a list already on screen', () => {
      // Arrange — `loading` is set by every **write** as well as by the read,
      // and the list stays up throughout one. The chain this region replaced
      // put the list ahead of the loading line, so a save never drew "Reading
      // your accounts…" under the rows; a region reading the running flag
      // alone brings that back, and the rule is the book's — a section renders
      // at most one of the value, the loading line and the failure.
      accounts.accountsSignal.set([everyday]);
      accounts.loadingSignal.set(true);

      // Act
      fixture.detectChanges();

      // Assert — the value is on screen, so the region has nothing to add.
      expect(host().querySelector('mat-list')).not.toBeNull();
      expect((statusRegion()?.textContent ?? '').trim()).toBe('');
    });
  });

  // A write that does not happen — `docs/design/components.md`.
  //
  // **Every case here is silent when broken.** A form cleared on the press
  // destroys what somebody typed before the outcome exists, and the screen then
  // has nothing to say and nothing to say it about. A refusal rendered nowhere
  // is a screen that looks correct while a person presses the same button until
  // they give up. And the *placement* is half the rule rather than a detail: a
  // sentence about a field belongs beneath that field, and a sentence about the
  // attempt belongs in the region, because the second kind has no field
  // anybody could correct.
  describe('a write that does not happen', () => {
    function statusRegion(): HTMLElement | null {
      return host().querySelector<HTMLElement>('[role="status"]');
    }

    function regionText(): string {
      return statusRegion()?.textContent ?? '';
    }

    function fieldErrors(): string[] {
      return Array.from(host().querySelectorAll('mat-error')).map((error) =>
        (error.textContent ?? '').trim(),
      );
    }

    function fill(name = 'Everyday'): void {
      exposed(fixture.componentInstance).form.setValue({
        name,
        type: 'Checking',
        openingBalance: 0,
        currencyCode: 'USD',
      });
    }

    it('keeps what was typed when the write is refused', async () => {
      // Arrange — the clear used to run on the line after the call, so the
      // text was gone before the outcome existed. A message beneath an empty
      // field is worse than silence: it names a problem with a value that is no
      // longer on screen.
      accounts.add.mockResolvedValue({ state: 'unreachable' });
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(
        exposed(fixture.componentInstance).form.getRawValue(),
      ).toMatchObject({ name: 'Everyday' });
    });

    it('empties the form once the write has landed', async () => {
      // Arrange — the positive control. A form that never cleared would pass
      // the case above and make every second entry a duplicate of the first.
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(
        exposed(fixture.componentInstance).form.getRawValue(),
      ).toMatchObject({ name: '' });
    });

    it('stays in edit mode when a rename is refused', async () => {
      // Arrange — nothing navigates either: a refused write does not close a
      // dialog, collapse the form or reset the control it was submitted from.
      // The form's Cancel is the DOM's own statement about the mode, so a
      // handler that fell out of the edit would redden here as well as on the
      // signal.
      accounts.update.mockResolvedValue({ state: 'unreachable' });
      exposed(fixture.componentInstance).edit(everyday);
      fixture.detectChanges();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(exposed(fixture.componentInstance).editingId()).toBe(everyday.id);
      expect(cancelButton()).not.toBeNull();
    });

    it('says the server was not reached, and does not claim nothing was written', async () => {
      // Arrange — the sentence is the Account keys section's with one clause
      // added, and the clause is the whole difference: somebody is looking at a
      // form holding text they typed. It may not claim the row was not
      // written, because the request may have arrived and lost its response.
      accounts.add.mockResolvedValue({ state: 'unreachable' });
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText()).toContain('couldn’t reach the server');
      expect(regionText()).toContain('Nothing you typed has been lost');
      expect(regionText()).toContain('try again in a minute');
    });

    it('offers a reload and no retry when the server judged', async () => {
      // Arrange — the pair a reader collapses. A minute is a real remedy for
      // silence and false of a refusal, and *try again in a minute* is the
      // sentence a writer reaches for because it fits everywhere.
      accounts.add.mockResolvedValue({ state: 'unreadable' });
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText()).toContain('copy it, then reload the page');
      expect(regionText()).not.toContain('try again');
    });

    it('announces a refusal from the region that was already there', async () => {
      // Arrange — taken while it is still empty, which is the whole point of
      // taking it here rather than after the act: a live region created
      // together with its text is announced by nothing.
      const region = statusRegion();

      accounts.add.mockResolvedValue({ state: 'duplicate-identifier' });
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert — the same element, still `status` and never raised to
      // `alert`: politeness belongs to the node, so the carve-out would take
      // the loading line and the locked notice with it.
      expect(statusRegion()).toBe(region);
      expect(region?.textContent ?? '').toContain('already saved');
      expect(host().querySelectorAll('[role="status"]')).toHaveLength(1);
      expect(
        host().querySelector('[role="alert"], [aria-live="assertive"]'),
      ).toBeNull();
    });

    it('says nothing at all when the write never left the browser', async () => {
      // Arrange — `locked` has no row in the chapter's table, and the omission
      // is the rule: the screen's locked notice is already the account of it,
      // and a second sentence is the duplicate the region refuses.
      accounts.add.mockResolvedValue({ state: 'locked' });
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText().trim()).toBe('');
      expect(fieldErrors()).toEqual([]);
    });

    it('puts the server’s sentence beneath the control it names', async () => {
      // Arrange — verbatim, and beneath the field: this is the only outcome
      // that touches one at all. A `mat-error` rather than a paragraph of this
      // screen's own, because the form field is what binds the message to the
      // input and colours the border with it.
      accounts.add.mockResolvedValue(
        invalid(['Name', ['Account name must be unique.']]),
      );
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(fieldErrors()).toEqual(['Account name must be unique.']);
      expect(regionText().trim()).toBe('');
    });

    it('moves focus to the first control carrying a message', async () => {
      // Arrange — a message bound by `aria-describedby` is announced when its
      // control takes focus rather than when it appears, and the field may be
      // off screen besides.
      accounts.add.mockResolvedValue(invalid(['Name', ['Too long.']]));
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(document.activeElement).toBe(
        host().querySelector('input[formcontrolname="name"]'),
      );
    });

    it('leaves focus where the press left it when no control carries one', async () => {
      // Arrange — the control for the case above. Moving a keyboard user into
      // a region takes them away from the control they are about to press
      // again.
      accounts.add.mockResolvedValue({ state: 'unreachable' });
      fill();
      // Rendered before the control is focused: the submit is disabled over an
      // empty form, and `focus()` on a disabled button does nothing at all.
      fixture.detectChanges();

      const submit = host().querySelector<HTMLButtonElement>(
        'button[type="submit"]',
      );

      submit?.focus();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(document.activeElement).toBe(submit);
    });

    it('puts a key this form cannot place into the region instead', async () => {
      // Arrange — `Id` is minted in the browser and no control carries it, so
      // there is no field to hang a message on. A dropped entry would be this
      // chapter's own defect with a better excuse.
      accounts.add.mockResolvedValue(
        invalid(['Id', ['Malformed identifier.']]),
      );
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert — and the wire key itself is **not** printed: `Id` is a member
      // name rather than a label anybody recognises.
      expect(regionText()).toContain('Malformed identifier.');
      expect(regionText()).not.toContain('Id');
      expect(fieldErrors()).toEqual([]);
    });

    it('renders one answer in two places rather than choosing between them', async () => {
      // Arrange — exclusivity is over **outcomes**, not sentences. A map with
      // keys on both sides puts a message under each control it names *and* a
      // line in the region for every key it does not, which is one answer to
      // one write.
      accounts.add.mockResolvedValue(
        invalid(['Name', ['Taken.']], ['Id', ['Malformed identifier.']]),
      );
      fill();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(fieldErrors()).toEqual(['Taken.']);
      expect(regionText()).toContain('Malformed identifier.');
    });

    it('treats a control it is not currently rendering as a miss', async () => {
      // Arrange — the currency picker leaves the DOM for the whole of an edit,
      // because currency is immutable. The question the chapter asks is
      // whether a message can be **placed**, not whether the name is one the
      // form recognises, so during an edit `CurrencyCode` takes the region
      // path.
      accounts.update.mockResolvedValue(
        invalid(['CurrencyCode', ['Unknown currency.']]),
      );
      exposed(fixture.componentInstance).edit(everyday);
      fixture.detectChanges();

      // Act
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert
      expect(regionText()).toContain('Unknown currency.');
      expect(fieldErrors()).toEqual([]);
    });

    it('gives a read in flight the region and hands it back when the read answers', async () => {
      // Arrange — the rule a reader will "fix". The read's line wins for as
      // long as the read runs, and only the **next write** clears the write's
      // sentence — so a refusal made before a slow read reappears the moment
      // the read answers. It is not a string somebody forgot to clear: the
      // form is still holding the text that was refused.
      accounts.add.mockResolvedValue({ state: 'unreachable' });
      fill();
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();
      expect(regionText()).toContain('couldn’t reach the server');

      // Act — a read starts, then fails.
      accounts.accountsSignal.set(null);
      accounts.loadingSignal.set(true);
      fixture.detectChanges();
      const duringRead = regionText();

      accounts.loadingSignal.set(false);
      accounts.failedSignal.set(true);
      fixture.detectChanges();

      // Assert — one account at a time, and the write's comes back.
      expect(duringRead).toContain('Reading your accounts');
      expect(duringRead).not.toContain('couldn’t reach the server');
      expect(regionText()).toContain('couldn’t reach the server');
      expect(regionText()).not.toContain('Check your connection');
    });

    it('clears the last refusal when the next write starts', async () => {
      // Arrange — the one thing that takes a write's sentence down. A screen
      // that cleared on a keystroke or on a cancel would take the sentence
      // away while the text it is about is still in the box.
      accounts.add.mockResolvedValue({ state: 'unreachable' });
      fill();
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Act
      accounts.add.mockImplementation(() => new Promise(() => undefined));
      fill('Rainy day');
      void pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert — silent while this write is unanswered, rather than carrying
      // the press before it.
      expect(regionText().trim()).toBe('');
    });

    it('leaves a field message on screen until the field is corrected', async () => {
      // Arrange — the shape of a one-form screen, said as the case it is
      // rather than as the one it is not.
      //
      // **There is no "the next write takes this field's message down" case
      // here, and that is deliberate.** A refused field makes the form invalid,
      // and the handler refuses an invalid form, so the next write on *this*
      // screen cannot start until somebody edits the field — and the edit
      // re-runs the control's validators, which takes the marker off by
      // itself. Written as a case it would pass with `clearFieldMessages`
      // deleted; measured. The screen that can start a second write over a
      // still-refused field is `/app/categories`, which has two forms, and
      // that is where the clear is held.
      accounts.add.mockResolvedValue(invalid(['Name', ['Taken.']]));
      fill();
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();
      expect(fieldErrors()).toEqual(['Taken.']);

      // Act — a second press with nothing corrected.
      accounts.add.mockClear();
      await pressSave(fixture.componentInstance);
      fixture.detectChanges();

      // Assert — refused before it began, and the sentence is still under the
      // field it is about.
      expect(accounts.add).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual(['Taken.']);
    });
  });

  it('allows editing a row whose name opened', () => {
    // Arrange — the control for the case above, and it carries the same two
    // discriminators the other way round: a gate that refused every row would
    // otherwise pass everything asserted up there.
    const screen = exposed(fixture.componentInstance);

    // Act
    fixture.detectChanges();
    screen.edit(everyday);
    fixture.detectChanges();

    // Assert
    expect(screen.editingId()).toBe(everyday.id);
    expect(cancelButton()).not.toBeNull();
    expect(editButtons().at(0)?.disabled).toBe(false);
    expect(screen.form.getRawValue()).toMatchObject({ name: 'Everyday' });
  });
});
