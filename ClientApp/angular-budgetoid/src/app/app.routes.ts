import { Routes } from '@angular/router';
import { authGuard } from '@app-core/guards/auth.guard';
import { guestGuard } from '@app-core/guards/guest.guard';

export const routes: Routes = [
  {
    path: 'welcome',
    // prettier-ignore
    loadComponent: () => import('./welcome/welcome.component').then(x => x.WelcomeComponent),
    canActivate: [guestGuard],
  },
  {
    path: 'app',
    children: [
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
        canActivate: [authGuard],
      },
      {
        path: 'accounts',
        // prettier-ignore
        loadComponent: () => import('./accounts/accounts.component').then(x => x.AccountsComponent),
        canActivate: [authGuard],
      },
      {
        path: 'categories',
        // prettier-ignore
        loadComponent: () => import('./categories/categories.component').then(x => x.CategoriesComponent),
        canActivate: [authGuard],
      },
      { path: 'groups', redirectTo: 'categories', pathMatch: 'full' },
      { path: '', redirectTo: 'home', pathMatch: 'full' },
    ],
  },
  // Root is the OAuth post-login landing spot; authGuard bounces anonymous visitors to /welcome.
  { path: '', redirectTo: 'app', pathMatch: 'full' },
  { path: '**', redirectTo: 'welcome' },
];
