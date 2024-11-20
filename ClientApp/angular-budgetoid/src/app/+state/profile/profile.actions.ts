import { createActionGroup, props } from '@ngrx/store';

export const profileActions = createActionGroup({
  source: 'user profile',
  events: {
    setUserProfile: props<{
      name: string;
      email: string;
      picture: string;
    }>(),
  },
});
