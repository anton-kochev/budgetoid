import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { KineticSentenceComponent } from './kinetic-sentence.component';

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

// The component keeps its kinetic-sentence signals `protected`; this exposes
// them for assertions without loosening the production visibility.
function exposeKineticState(component: KineticSentenceComponent): {
  fear: () => string;
  verdict: () => string;
  verdictOn: () => boolean;
} {
  return component as unknown as {
    fear: () => string;
    verdict: () => string;
    verdictOn: () => boolean;
  };
}

// Kinetic sentence pairs [fear, verdict]; apostrophes are typographic (’).
const TEST_LINES: readonly (readonly [fear: string, verdict: string])[] = [
  ['December 1st. Insurance due.', 'It’s ready.'],
  ['This August. Two weeks away.', 'It’s paid.'],
];

describe('KineticSentenceComponent', () => {
  const originalMatchMedia = window.matchMedia;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [KineticSentenceComponent],
    }).compileComponents();
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
  });

  it('renders the first pair statically when reduced motion is preferred', () => {
    // Arrange
    stubMatchMedia(true);

    // Act
    const fixture = TestBed.createComponent(KineticSentenceComponent);
    fixture.componentRef.setInput('lines', TEST_LINES);
    fixture.detectChanges();
    const state = exposeKineticState(fixture.componentInstance);
    const verdictEl = (fixture.nativeElement as HTMLElement).querySelector(
      '.kin-verdict',
    );

    // Assert
    expect(state.fear()).toBe('December 1st. Insurance due.');
    expect(state.verdict()).toBe('It’s ready.');
    expect(state.verdictOn()).toBe(true);
    expect(verdictEl?.classList.contains('on')).toBe(true);

    fixture.destroy();
  });

  it('starts the kinetic sentence empty when motion is allowed', () => {
    // Arrange — fake timers keep the started interval from firing before teardown.
    vi.useFakeTimers();
    stubMatchMedia(false);

    // Act
    const fixture = TestBed.createComponent(KineticSentenceComponent);
    fixture.componentRef.setInput('lines', TEST_LINES);
    fixture.detectChanges();
    const state = exposeKineticState(fixture.componentInstance);

    // Assert — only the initial state, no timer advancement.
    expect(state.fear()).toBe('');
    expect(state.verdictOn()).toBe(false);

    fixture.destroy();
    vi.useRealTimers();
  });

  it('renders nothing and does not crash when lines are empty', () => {
    // Arrange
    stubMatchMedia(false);

    // Act
    const fixture = TestBed.createComponent(KineticSentenceComponent);
    fixture.componentRef.setInput('lines', []);
    fixture.detectChanges();
    const state = exposeKineticState(fixture.componentInstance);

    // Assert
    expect(state.fear()).toBe('');
    expect(state.verdictOn()).toBe(false);

    fixture.destroy();
  });
});
