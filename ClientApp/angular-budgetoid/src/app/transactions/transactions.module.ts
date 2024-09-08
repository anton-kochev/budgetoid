import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { SharedModule } from '../+shared/shared.module';
import { TransactionsComponent } from './transactions.component';

@NgModule({
  declarations: [],
  imports: [
    RouterModule.forChild([{ path: '', component: TransactionsComponent }]),
    SharedModule,
  ],
})
export class TransactionsModule {}
