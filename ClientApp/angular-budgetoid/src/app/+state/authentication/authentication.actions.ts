import { createActionGroup, emptyProps } from '@ngrx/store';

export const authActions = createActionGroup({
  source: 'authentication',
  events: {
    login: emptyProps(),
    logout: emptyProps(),
  },
});
