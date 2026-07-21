import { computed, Injectable, signal, Signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'budgetoid-theme';

const TOGGLE_ORDER: readonly ThemeMode[] = ['system', 'light', 'dark'];

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly modeSignal = signal<ThemeMode>(this.readStoredMode());
  private readonly prefersDark = signal<boolean>(this.matchesPrefersDark());

  public readonly mode: Signal<ThemeMode> = this.modeSignal.asReadonly();

  public readonly resolvedTheme: Signal<'light' | 'dark'> = computed(() => {
    const mode = this.modeSignal();

    if (mode !== 'system') {
      return mode;
    }

    return this.prefersDark() ? 'dark' : 'light';
  });

  constructor() {
    this.applyColorScheme(this.modeSignal());
    this.watchSystemPreference();
  }

  public setMode(mode: ThemeMode): void {
    this.modeSignal.set(mode);
    this.applyColorScheme(mode);
    this.persistMode(mode);
  }

  public toggle(): void {
    const currentIndex = TOGGLE_ORDER.indexOf(this.modeSignal());
    const nextIndex = (currentIndex + 1) % TOGGLE_ORDER.length;

    this.setMode(TOGGLE_ORDER[nextIndex]);
  }

  private applyColorScheme(mode: ThemeMode): void {
    document.documentElement.style.colorScheme = mode === 'system' ? '' : mode;
  }

  private persistMode(mode: ThemeMode): void {
    // Safari private mode (and quota-exhausted storage) throws on setItem;
    // theme selection must never crash the caller over a persistence failure.
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // Intentionally swallowed: persistence is best-effort.
    }
  }

  private readStoredMode(): ThemeMode {
    const stored = localStorage.getItem(STORAGE_KEY);

    if (stored === 'light' || stored === 'dark' || stored === 'system') {
      return stored;
    }

    return 'system';
  }

  private matchesPrefersDark(): boolean {
    return this.prefersDarkQuery()?.matches ?? false;
  }

  private prefersDarkQuery(): MediaQueryList | undefined {
    if (typeof window === 'undefined' || !window.matchMedia) {
      return undefined;
    }

    return window.matchMedia('(prefers-color-scheme: dark)');
  }

  private watchSystemPreference(): void {
    const query = this.prefersDarkQuery();

    query?.addEventListener('change', (event) => {
      this.prefersDark.set(event.matches);
    });
  }
}
