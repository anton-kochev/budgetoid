import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
} from '@angular/core';

/**
 * `lockup` is the mark beside the wordmark; `mark` is the coin on its own.
 *
 * They are one component rather than two because they are one drawing: the mark
 * is the first 96 units of the lockup's own viewBox, so a second component would
 * be a second copy of geometry `branding/BRAND.md` says never to edit.
 */
export type BrandLockupVariant = 'lockup' | 'mark';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-brand-lockup',
  styleUrls: ['./brand-lockup.component.scss'],
  templateUrl: './brand-lockup.component.html',
})
export class BrandLockupComponent {
  // Per-instance counter so multiple lockups on one page get unique mask ids.
  private static nextId = 0;

  public readonly variant = input<BrandLockupVariant>('lockup');

  // Cropping the viewBox to the mark's own square is the whole of the
  // difference: the wordmark starts at x=134, so 0 0 96 96 frames the coin and
  // nothing else. The paths are untouched either way.
  protected readonly viewBox = computed(() =>
    this.variant() === 'mark' ? '0 0 96 96' : '0 0 450 96',
  );

  // Unique id for this instance's halo mask, avoiding cross-instance collisions.
  protected readonly maskId = `brand-lockup-halo-${BrandLockupComponent.nextId++}`;
}
