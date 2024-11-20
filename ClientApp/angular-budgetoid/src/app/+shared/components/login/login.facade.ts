import { inject, Injectable } from '@angular/core';
import { authActions } from '@app-state/authentication/authentication.actions';
import { Store } from '@ngrx/store';

@Injectable()
export class LoginFacade {
  private readonly store = inject(Store);

  public login(): void {
    this.store.dispatch(authActions.login());
  }

  public logout(): void {
    this.store.dispatch(authActions.logout());
  }
}
