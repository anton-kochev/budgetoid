import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  OnInit,
  signal,
} from '@angular/core';

// Kinetic timing (ms), matching the concept.
const TYPE_INTERVAL_MS = 36; // typewriter cadence per character
const VERDICT_DELAY_MS = 380; // pause after the fear finishes before the verdict
const ADVANCE_DELAY_MS = 3600; // dwell after finishing before the next pair

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-kinetic-sentence',
  styleUrls: ['./kinetic-sentence.component.scss'],
  templateUrl: './kinetic-sentence.component.html',
})
export class KineticSentenceComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  // The kinetic sentence pairs [fear, verdict] driving the animation.
  public readonly lines =
    input.required<readonly (readonly [fear: string, verdict: string])[]>();

  // Kinetic-sentence state consumed by the template.
  protected readonly fear = signal('');
  protected readonly verdict = signal('');
  protected readonly verdictOn = signal(false);

  private lineIndex = 0;
  private typeTimer: ReturnType<typeof setInterval> | undefined;
  private readonly pendingTimeouts = new Set<ReturnType<typeof setTimeout>>();

  public ngOnInit(): void {
    if (this.lines().length === 0) {
      return;
    }

    if (this.prefersReducedMotion()) {
      // Static render of the first pair, no timers.
      const [fear, verdict] = this.lines()[0];
      this.fear.set(fear);
      this.verdict.set(verdict);
      this.verdictOn.set(true);
    } else {
      this.playLine();
    }

    this.destroyRef.onDestroy(() => this.clearTimers());
  }

  private playLine(): void {
    const [fearText, verdictText] =
      this.lines()[this.lineIndex % this.lines().length];
    this.lineIndex += 1;

    // Reset for the new pair.
    this.fear.set('');
    this.verdict.set('');
    this.verdictOn.set(false);

    const chars = [...fearText];
    let typed = 0;

    this.typeTimer = setInterval(() => {
      typed += 1;
      this.fear.set(chars.slice(0, typed).join(''));

      if (typed >= chars.length) {
        this.clearTypeTimer();
        this.schedule(() => {
          this.verdict.set(verdictText);
          this.verdictOn.set(true);
        }, VERDICT_DELAY_MS);
        this.schedule(() => this.playLine(), ADVANCE_DELAY_MS);
      }
    }, TYPE_INTERVAL_MS);
  }

  private schedule(callback: () => void, delayMs: number): void {
    const handle = setTimeout(() => {
      this.pendingTimeouts.delete(handle);
      callback();
    }, delayMs);
    this.pendingTimeouts.add(handle);
  }

  private clearTypeTimer(): void {
    if (this.typeTimer !== undefined) {
      clearInterval(this.typeTimer);
      this.typeTimer = undefined;
    }
  }

  private clearTimers(): void {
    this.clearTypeTimer();
    for (const handle of this.pendingTimeouts) {
      clearTimeout(handle);
    }
    this.pendingTimeouts.clear();
  }

  private prefersReducedMotion(): boolean {
    if (typeof window === 'undefined' || !window.matchMedia) {
      return false;
    }

    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }
}
