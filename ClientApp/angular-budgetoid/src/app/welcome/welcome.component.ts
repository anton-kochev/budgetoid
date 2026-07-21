import { ChangeDetectionStrategy, Component } from '@angular/core';
import { BrandLockupComponent } from '@app-shared/components/brand-lockup/brand-lockup.component';
import { GoogleSignInButtonComponent } from '@app-shared/components/google-sign-in-button/google-sign-in-button.component';
import { KineticSentenceComponent } from '@app-shared/components/kinetic-sentence/kinetic-sentence.component';

// Apostrophes are typographic (’) on purpose.
const KINETIC_LINES: readonly (readonly [fear: string, verdict: string])[] = [
  ['December 1st. Insurance due.', 'It’s ready.'],
  ['This August. Two weeks away.', 'It’s paid.'],
  ['At the till. That jacket.', 'Go ahead.'],
  ['Out of nowhere. A car repair.', 'Expected.'],
  ['Friday night. Pizza with everyone.', 'Covered.'],
  ['Payday morning. Salary lands.', 'All of it gets a job.'],
];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BrandLockupComponent,
    GoogleSignInButtonComponent,
    KineticSentenceComponent,
  ],
  styleUrls: ['./welcome.component.scss'],
  templateUrl: './welcome.component.html',
})
export class WelcomeComponent {
  protected readonly kineticLines = KINETIC_LINES;
}
