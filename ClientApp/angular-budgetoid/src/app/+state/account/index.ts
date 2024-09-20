import { createReducer, on } from '@ngrx/store';
import { guid, Guid } from 'app/+common/guid';
import { produce } from 'immer';
import { accountActions } from './account.actions';

export interface AccountState {
  email: string;
  id: Guid;
  userName: string;
}

export const initialState: AccountState = {
  email: '',
  id: guid('00000000-0000-0000-0000-000000000001'),
  userName: '',
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
