import { Routes } from '@angular/router';
import { authGuard } from '@app-core/guards/auth.guard';
import { guestGuard } from '@app-core/guards/guest.guard';
import { releaseGuard } from '@app-core/guards/release.guard';

export const routes: Routes = [
  {
    path: 'welcome',
    // prettier-ignore
    loadComponent: () => import('./welcome/welcome.component').then(x => x.WelcomeComponent),
    canActivate: [guestGuard],
  },
  // The signed-in surface, and the navigation is a component of this route rather
  // than of the root shell. Which screens carry a bar is a fact about the route
  // table: everything under `app` sits behind `authGuard`, and `welcome`,
  // `register` and `release` are siblings of it, so a layout mounted here draws
  // over the one and cannot reach the others. The alternative — a root shell
  // asking `SessionService` whether to draw a bar — answers a question about
  // identity where one about position was asked, and would paint a signed-in
  // navigation over the welcome screen for as long as the status said the
  // visitor held a session.
  {
    path: 'app',
    // prettier-ignore
    loadComponent: () => import('./shell/shell.component').then(x => x.ShellComponent),
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
  // factors' envelopes live in a service the screen provides and that dies with it — so
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
  // The one screen a locked session reaches: authGuard and guestGuard both send
  // `locked-session` here, and releaseGuard sends a full session on to /app.
  //
  // **A sibling of `app`, never a child of it**, which is what keeps the navigation bar
  // off it: every destination the bar offers draws budget content, and a locked session
  // displays none of any kind (FR-113). No `children` either — the screen's states replace
  // each other on one route.
  {
    path: 'release',
    // prettier-ignore
    loadComponent: () => import('./release/release.component').then(x => x.ReleaseComponent),
    canActivate: [releaseGuard],
  },
  // Nothing lands here on purpose any more — the provider's redirect goes to /register — so the
  // root is only what somebody types or has bookmarked, and it hands them straight to the app.
  // It decides nothing about who may be there: authGuard on the screens below is what turns an
  // anonymous visitor away to /welcome and a locked session to /release.
  { path: '', redirectTo: 'app', pathMatch: 'full' },
  { path: '**', redirectTo: 'welcome' },
];
