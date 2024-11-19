import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { authActions } from '@app-state/authentication/authentication.actions';
import { Store } from '@ngrx/store';

@Component({
  imports: [MatButtonModule],
  selector: 'app-login',
  standalone: true,
  template: `
    <button mat-stroked-button (click)="signInWithGoogle()">
      Login with Google
    </button>
    <button mat-stroked-button (click)="signOut()">Logout</button>
  `,
})
export class LoginComponent {
  private readonly store = inject(Store);

  public signInWithGoogle(): void {
    this.store.dispatch(authActions.login());
  }

  public signOut(): void {
    this.store.dispatch(authActions.logout());
  }
}
