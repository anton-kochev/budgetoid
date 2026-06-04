import { createActionGroup, emptyProps } from '@ngrx/store';

export const accountActions = createActionGroup({
  source: 'account',
  events: {
    fetchAccounts: emptyProps(),
    fetchAccountsSuccess: emptyProps(),
    fetchAccountsFailure: emptyProps(),
  },
});
