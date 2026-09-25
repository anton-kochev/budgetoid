import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BrandLockupComponent } from './brand-lockup.component';

describe('BrandLockupComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BrandLockupComponent],
    }).compileComponents();
  });

  it('renders an accessible svg lockup labelled Budgetoid', () => {
    // Arrange
    const fixture = TestBed.createComponent(BrandLockupComponent);

    // Act
    fixture.detectChanges();
    const svg = (fixture.nativeElement as HTMLElement).querySelector(
      'svg[role="img"]',
    );

    // Assert
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute('aria-label')).toBe('Budgetoid');

    fixture.destroy();
  });

  it('wires the masked circle to its own mask id', () => {
    // Arrange
    const fixture = TestBed.createComponent(BrandLockupComponent);

    // Act
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const maskId = host.querySelector('mask')?.getAttribute('id');
    const maskedCircle = host.querySelector('svg circle[mask]');

    // Assert
    expect(maskId).toBeTruthy();
    expect(maskedCircle?.getAttribute('mask')).toBe(`url(#${maskId})`);

    fixture.destroy();
  });

  it('assigns distinct mask ids to separate instances', () => {
    // Arrange
    const first = TestBed.createComponent(BrandLockupComponent);
    const second = TestBed.createComponent(BrandLockupComponent);

    // Act
    first.detectChanges();
    second.detectChanges();
    const firstMaskId = (first.nativeElement as HTMLElement)
      .querySelector('mask')
      ?.getAttribute('id');
    const secondMaskId = (second.nativeElement as HTMLElement)
      .querySelector('mask')
      ?.getAttribute('id');

    // Assert
    expect(firstMaskId).toBeTruthy();
    expect(secondMaskId).toBeTruthy();
    expect(firstMaskId).not.toBe(secondMaskId);

    first.destroy();
    second.destroy();
  });

  it('draws the wordmark by default and drops it for the mark', () => {
    // Arrange
    const fixture = TestBed.createComponent(BrandLockupComponent);

    // Act
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const lockupViewBox = host.querySelector('svg')?.getAttribute('viewBox');
    const lockupPaths = host.querySelectorAll('svg > path').length;

    fixture.componentRef.setInput('variant', 'mark');
    fixture.detectChanges();
    const markViewBox = host.querySelector('svg')?.getAttribute('viewBox');
    const markPaths = host.querySelectorAll('svg > path').length;

    // Assert
    // The rail asks for the mark by name: an 88px column has no room for the
    // wordmark. Cropping the viewBox alone is the tempting half of this — it
    // frames the coin and leaves the wordmark in the document, where anything
    // sizing the drawing by its bounding box still has to reckon with a shape
    // at x=134. Both halves are asserted, in both directions, so neither the
    // crop nor the removal can be dropped on its own.
    expect(lockupViewBox).toBe('0 0 450 96');
    expect(lockupPaths).toBe(1);
    expect(markViewBox).toBe('0 0 96 96');
    expect(markPaths).toBe(0);

    fixture.destroy();
  });
});
