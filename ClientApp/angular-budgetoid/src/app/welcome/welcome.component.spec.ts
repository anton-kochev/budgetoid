import { TestBed } from '@angular/core/testing';
import { Store } from '@ngrx/store';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WelcomeComponent } from './welcome.component';

// Minimal MediaQueryList stub — the component only reads `.matches`.
function stubMatchMedia(matches: boolean): void {
  window.matchMedia = vi.fn().mockReturnValue({
    matches,
    media: '(prefers-reduced-motion: reduce)',
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  });
}

describe('WelcomeComponent', () => {
  const store = { dispatch: vi.fn() };
  const originalMatchMedia = window.matchMedia;

  beforeEach(async () => {
    store.dispatch.mockClear();
    await TestBed.configureTestingModule({
      imports: [WelcomeComponent],
      providers: [{ provide: Store, useValue: store }],
    }).compileComponents();
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
  });

  it('renders the headline, mission lead, and Google button', () => {
    // Arrange
    stubMatchMedia(true);

    // Act
    const fixture = TestBed.createComponent(WelcomeComponent);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    // Assert
    expect(text).toContain('Always watching. Never judging.');
    expect(text).toContain(
      'One simple idea: decide what your money is for before you spend it.',
    );
    expect(text).toContain('Continue with Google');

    fixture.destroy();
  });
});
