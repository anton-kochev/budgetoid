import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: true,
  template: ` <h1>Welcome to the Transactions Module!</h1> `,
})
export class TransactionsComponent {}
