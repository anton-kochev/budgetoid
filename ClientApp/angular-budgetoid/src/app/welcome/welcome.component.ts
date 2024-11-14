import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: true,
  styles: [
    `
      :host {
        display: grid;
        grid-template-rows: auto 1fr auto;
        padding: 2rem;
      }

      .welcome {
        &__about {
          flex-grow: 1;
          margin-bottom: 2rem;
          text-align: center;
        }
        &__title {
          font-size: 4rem;
          font-weight: 400;
          text-align: center;
          width: 100%;
        }
        &__login {
          text-transform: uppercase;
          justify-self: center;
        }
      }
    `,
  ],
  template: `
    <h1 class="welcome__title">BUDGETOID</h1>
    <p class="welcome__about">
      Welcome to Budgetoid, the budgeting app that helps you manage your
      finances!
    </p>
    <button mat-stroked-button class="welcome__login">Login</button>
  `,
  imports: [MatButtonModule],
})
export class WelcomeComponent {}
