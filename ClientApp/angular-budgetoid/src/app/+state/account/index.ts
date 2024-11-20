import { authActions } from '@app-state/authentication/authentication.actions';
import { createReducer, on } from '@ngrx/store';
import { guid, Guid } from 'app/+common/guid';
import { produce } from 'immer';
import { accountActions } from './account.actions';

export interface AccountState {
  email: string;
  id: Guid;
  picture: string;
  userName: string;
}

export const initialState: AccountState = {
  email: '',
  id: guid('00000000-0000-0000-0000-000000000001'),
  picture: '',
  userName: '',
};

export const accountReducer = createReducer(
  initialState,
  on(
    accountActions.setAccountInformation,
    produce((state, { name, email, picture }) => {
      state.email = email;
      state.picture = picture;
      state.userName = name;
    }),
  ),
  on(authActions.logout, () => initialState),
);
