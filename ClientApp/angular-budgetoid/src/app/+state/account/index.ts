import { createReducer, on } from '@ngrx/store';
import { produce } from 'immer';
import { accountActions } from './account.actions';

export interface AccountState {
  userName: string;
  email: string;
}

export const initialState: AccountState = {
  userName: '',
  email: '',
};

export const accountReducer = createReducer(
  initialState,
  on(
    accountActions.setAccountInformation,
    produce((state, { userName, email }) => {
      state.userName = userName;
      state.email = email;
    }),
  ),
);
