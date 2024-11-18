import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '@app-core/services/auth-service';

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
  private readonly authService = inject(AuthService);

  public signInWithGoogle(): void {
    this.authService.signIn();
  }

  public signOut(): void {
    this.authService.signOut();
  }
}
