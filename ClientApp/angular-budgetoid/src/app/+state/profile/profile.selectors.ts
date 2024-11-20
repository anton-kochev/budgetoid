import { createFeatureSelector, createSelector } from '@ngrx/store';
import { profileFeatureKey, ProfileState } from '.';

const selectFeature = createFeatureSelector<ProfileState>(profileFeatureKey);

export const selectUserName = createSelector(
  selectFeature,
  state => state.userName,
);
