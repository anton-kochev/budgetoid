import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { LoginComponent } from '../+shared/components/login/login.component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, LoginComponent],
  standalone: true,
  styleUrls: ['./welcome.component.scss'],
  templateUrl: './welcome.component.html',
})
export class WelcomeComponent {}
