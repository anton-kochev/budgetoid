import { Routes } from '@angular/router';
import { authGuard } from '@app-core/guards/auth.guard';
import * as authenticationEffects from '@app-state/authentication/authentication.effects';
import { profileFeatureKey, profileReducer } from '@app-state/profile';
import { provideEffects } from '@ngrx/effects';
import { provideState } from '@ngrx/store';
import { accountsResolver } from './accounts/accounts.resolver';
import { transactionsResolver } from './transactions/transactions.resolver';

export const routes: Routes = [
  {
    path: '',
    providers: [
      provideState({ name: profileFeatureKey, reducer: profileReducer }),
      provideEffects(authenticationEffects),
    ],
    children: [
      // { path: 'home', component: HomeComponent },
      {
        path: 'welcome',
        // prettier-ignore
        loadComponent: () => import('./welcome/welcome.component').then(x => x.WelcomeComponent),
      },
      {
        path: 'home',
        // prettier-ignore
        loadComponent: () => import('./home/home.component').then(x => x.HomeComponent),
        canActivate: [authGuard],
      },
      {
        path: 'transactions',
        // prettier-ignore
        loadComponent: () => import('./transactions/transactions.component').then(x => x.TransactionsComponent),
        resolve: { transactions: transactionsResolver },
        canActivate: [authGuard],
      },
      {
        path: 'accounts',
        // prettier-ignore
        loadComponent: () => import('./accounts/accounts.component').then(x => x.AccountsComponent),
        resolve: { accounts: accountsResolver },
        canActivate: [authGuard],
      },
      { path: '', redirectTo: '/home', pathMatch: 'full' },
    ],
  },
];
