import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { SharedModule } from '../+shared/shared.module';
import { WelcomeComponent } from './welcome.component';

@NgModule({
  declarations: [WelcomeComponent],
  imports: [
    RouterModule.forChild([{ path: '', component: WelcomeComponent }]),
    SharedModule,
  ],
})
export class WelcomeModule {}
