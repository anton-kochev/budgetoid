import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { LoginFacade } from '../login/login.facade';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [LoginFacade],
  selector: 'app-google-sign-in-button',
  styleUrls: ['./google-sign-in-button.component.scss'],
  templateUrl: './google-sign-in-button.component.html',
})
export class GoogleSignInButtonComponent {
  private readonly loginFacade = inject(LoginFacade);

  protected signIn(): void {
    this.loginFacade.login();
  }
}
