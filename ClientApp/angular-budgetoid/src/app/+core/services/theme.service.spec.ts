import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';

import { ThemeService } from './theme.service';

const STORAGE_KEY = 'budgetoid-theme';

function mockMatchMedia(matches: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn().mockReturnValue({
      matches,
      media: '(prefers-color-scheme: dark)',
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }),
  );
}

function createService(): ThemeService {
  return TestBed.inject(ThemeService);
}

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    mockMatchMedia(false);
    document.documentElement.style.colorScheme = '';
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('defaults to "system" when nothing is stored', () => {
    // Arrange
    // (localStorage cleared in beforeEach)

    // Act
    const service = createService();

    // Assert
    expect(service.mode()).toBe('system');
  });

  it('reads a stored mode on construction', () => {
    // Arrange
    localStorage.setItem(STORAGE_KEY, 'dark');

    // Act
    const service = createService();

    // Assert
    expect(service.mode()).toBe('dark');
  });

  it('ignores an invalid stored value and falls back to "system"', () => {
    // Arrange
    localStorage.setItem(STORAGE_KEY, 'chartreuse');

    // Act
    const service = createService();

    // Assert
    expect(service.mode()).toBe('system');
  });

  it('forces the color-scheme on the document when set to dark', () => {
    // Arrange
    const service = createService();

    // Act
    service.setMode('dark');

    // Assert
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });

  it('clears the color-scheme override when set to system', () => {
    // Arrange
    const service = createService();
    service.setMode('dark');

    // Act
    service.setMode('system');

    // Assert
    expect(document.documentElement.style.colorScheme).toBe('');
  });

  it('persists the chosen mode to localStorage', () => {
    // Arrange
    const service = createService();

    // Act
    service.setMode('light');

    // Assert
    expect(localStorage.getItem(STORAGE_KEY)).toBe('light');
  });

  it('cycles system -> light -> dark -> system on toggle', () => {
    // Arrange
    const service = createService();

    // Act & Assert
    expect(service.mode()).toBe('system');
    service.toggle();
    expect(service.mode()).toBe('light');
    service.toggle();
    expect(service.mode()).toBe('dark');
    service.toggle();
    expect(service.mode()).toBe('system');
  });

  it('resolves an explicit mode directly', () => {
    // Arrange
    const service = createService();

    // Act
    service.setMode('dark');

    // Assert
    expect(service.resolvedTheme()).toBe('dark');
  });

  it('resolves "system" via the OS preference', () => {
    // Arrange
    mockMatchMedia(true);
    const service = createService();

    // Act
    service.setMode('system');

    // Assert
    expect(service.resolvedTheme()).toBe('dark');
  });

  it('does not throw when localStorage is unavailable', () => {
    // Arrange
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError');
    });
    const service = createService();

    // Act & Assert
    expect(() => service.setMode('dark')).not.toThrow();
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });
});
