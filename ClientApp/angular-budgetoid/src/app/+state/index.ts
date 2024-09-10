import { isDevMode } from '@angular/core';
import { ActionReducerMap, MetaReducer } from '@ngrx/store';
import { accountReducer, AccountState } from './account';

export interface State {
  account: AccountState;
}

export const reducers: ActionReducerMap<State> = {
  account: accountReducer,
};

export const metaReducers: MetaReducer<State>[] = isDevMode() ? [] : [];
