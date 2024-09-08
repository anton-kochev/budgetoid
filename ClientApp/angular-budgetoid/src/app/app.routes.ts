import { Routes } from '@angular/router';

export const routes: Routes = [
  // { path: 'home', component: HomeComponent },
  {
    path: 'welcome',
    // prettier-ignore
    loadChildren: () => import('./welcome/welcome.module').then(m => m.WelcomeModule),
  },
  {
    path: 'transactions',
    // prettier-ignore
    loadChildren: () => import('./transactions/transactions.module').then(m => m.TransactionsModule),
  },
  { path: '', redirectTo: '/welcome', pathMatch: 'full' },
];
