import { createActionGroup, props } from '@ngrx/store';

export const accountActions = createActionGroup({
  source: 'user account',
  events: {
    setAccountInformation: props<{ userName: string; email: string }>(),
  },
});
