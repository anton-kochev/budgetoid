import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { LoginFacade } from './login.facade';

@Component({
  imports: [MatButtonModule],
  providers: [LoginFacade],
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
  private readonly facade = inject(LoginFacade);

  public signInWithGoogle(): void {
    this.facade.login();
  }

  public signOut(): void {
    this.facade.logout();
  }
}
