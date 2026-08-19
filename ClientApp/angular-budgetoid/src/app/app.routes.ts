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
      {
        path: 'settings',
        // prettier-ignore
        loadComponent: () => import('./settings/settings.component').then(x => x.SettingsComponent),
        canActivate: [authGuard],
      },
      { path: 'groups', redirectTo: 'categories', pathMatch: 'full' },
      { path: '', redirectTo: 'transactions', pathMatch: 'full' },
    ],
  },
  // Where the provider's redirect lands: somebody who pressed "create an account" comes
  // back to the screen that creates one. guestGuard turns anybody already holding a
  // session away, since registration has nothing to offer them and would spend a
  // challenge and a passkey finding that out.
  //
  // The steps deliberately get no `children` array, and giving them one breaks two things
  // at once. A step is in-memory state — the account keys, the ten codes and the eleven
  // wrapped envelopes live in a service the screen provides and that dies with it — so
  // Back would land on a step whose state is already gone. And `/register/codes` would
  // become a link somebody can open, or be sent, on a screen whose whole premise is that
  // ten codes were minted moments ago and are on it. The step is a signal inside
  // register.component.ts, so no step URL exists to deep-link.
  {
    path: 'register',
    // prettier-ignore
    loadComponent: () => import('./register/register.component').then(x => x.RegisterComponent),
    canActivate: [guestGuard],
  },
  // Nothing lands here on purpose any more — the provider's redirect goes to /register — so the
  // root is only what somebody types or has bookmarked, and it hands them straight to the app.
  // It decides nothing about who may be there: authGuard on the screens below is what turns an
  // anonymous visitor away to /welcome.
  { path: '', redirectTo: 'app', pathMatch: 'full' },
  { path: '**', redirectTo: 'welcome' },
];
