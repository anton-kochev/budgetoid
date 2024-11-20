import { isDevMode } from '@angular/core';
import { ActionReducerMap, MetaReducer } from '@ngrx/store';
import { profileReducer, ProfileState } from './profile';

export interface State {
  account: ProfileState;
}

export const reducers: ActionReducerMap<State> = {
  account: profileReducer,
};

export const metaReducers: MetaReducer<State>[] = isDevMode() ? [] : [];
