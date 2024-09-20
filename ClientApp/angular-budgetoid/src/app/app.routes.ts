import { Routes } from '@angular/router';
import { accountsResolver } from './accounts/accounts.resolver';
import { transactionsResolver } from './transactions/transactions.resolver';

export const routes: Routes = [
  // { path: 'home', component: HomeComponent },
  {
    path: 'welcome',
    // prettier-ignore
    loadComponent: () => import('./welcome/welcome.component').then(x => x.WelcomeComponent),
  },
  {
    path: 'transactions',
    // prettier-ignore
    loadComponent: () => import('./transactions/transactions.component').then(x => x.TransactionsComponent),
    resolve: {
      transactions: transactionsResolver,
    },
  },
  {
    path: 'accounts',
    // prettier-ignore
    loadComponent: () => import('./accounts/accounts.component').then(x => x.AccountsComponent),
    resolve: {
      accounts: accountsResolver,
    },
  },
  { path: '', redirectTo: '/welcome', pathMatch: 'full' },
];
