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
});
