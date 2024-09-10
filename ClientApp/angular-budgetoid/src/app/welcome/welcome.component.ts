import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: true,
  template: ` <h1>Welcome to the Welcome Module!</h1> `,
})
export class WelcomeComponent {}
