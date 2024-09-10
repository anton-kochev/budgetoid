import { Routes } from '@angular/router';

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
  },
  { path: '', redirectTo: '/welcome', pathMatch: 'full' },
];
