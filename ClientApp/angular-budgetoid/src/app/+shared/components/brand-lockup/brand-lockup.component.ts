import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-brand-lockup',
  styleUrls: ['./brand-lockup.component.scss'],
  templateUrl: './brand-lockup.component.html',
})
export class BrandLockupComponent {
  // Per-instance counter so multiple lockups on one page get unique mask ids.
  private static nextId = 0;

  // Unique id for this instance's halo mask, avoiding cross-instance collisions.
  protected readonly maskId = `brand-lockup-halo-${BrandLockupComponent.nextId++}`;
}
