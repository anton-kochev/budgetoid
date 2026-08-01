import { authActions } from '@app-state/authentication/authentication.actions';
import { createReducer, on } from '@ngrx/store';
import { guid, Guid } from 'app/+common/guid';
import { produce } from 'immer';
import { profileActions } from './profile.actions';

export const profileFeatureKey = 'account';

export interface ProfileState {
  email: string;
  id: Guid;
  userName: string;
}

export const initialState: ProfileState = {
  email: '',
  id: guid('00000000-0000-0000-0000-000000000001'),
  userName: '',
};

export const profileReducer = createReducer(
  initialState,
  on(
    profileActions.setUserProfile,
    produce((state, { name, email }) => {
      state.email = email;
      state.userName = name;
    }),
  ),
  on(authActions.logout, () => initialState),
);
