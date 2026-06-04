import { inject, Injectable } from '@angular/core';
import { accountActions } from '@app-state/account/account.actions';
import { Store } from '@ngrx/store';

@Injectable()
export class AccountsFacade {
  private readonly store = inject(Store);

  public getAccounts(): void {
    this.store.dispatch(accountActions.fetchAccounts());
  }
}
