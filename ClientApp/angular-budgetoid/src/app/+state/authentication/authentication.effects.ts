import { inject } from '@angular/core';
import { AuthService } from '@app-core/services/auth-service';
import { accountActions } from '@app-state/account/account.actions';
import { Actions, createEffect, ofType } from '@ngrx/effects';
import { map, tap } from 'rxjs';
import { authActions } from './authentication.actions';

export const userProfileInformation = createEffect(
  (authService = inject(AuthService)) => {
    return authService.userProfile$.pipe(
      map(profile => accountActions.setAccountInformation(profile)),
    );
  },
  { functional: true },
);

export const login = createEffect(
  (actions$ = inject(Actions), authService = inject(AuthService)) => {
    return actions$.pipe(
      ofType(authActions.login),
      tap(() => {
        authService.signIn();
      }),
    );
  },
  { dispatch: false, functional: true },
);

export const logout = createEffect(
  (actions$ = inject(Actions), authService = inject(AuthService)) => {
    return actions$.pipe(
      ofType(authActions.logout),
      tap(() => {
        authService.signOut();
      }),
    );
  },
  { dispatch: false, functional: true },
);
