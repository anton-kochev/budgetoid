// The one-shot fact the erasure dialog hands to Welcome on its way out. See
// docs/design/components.md, "Erasure dialog", *How the overlay ends*.
//
// **In memory, never in the address.** A `/welcome?erased` is a URL anybody can
// open or be sent, and navigation state comes back with the browser's Back
// button; either lets Welcome announce an erasure that did not happen. So the
// fact lives here, on a root-provided holder, and a reload drops it — correctly,
// since a reloaded Welcome has not watched anything happen.
//
// **`providedIn: 'root'` and listed nowhere.** A holder that needed providing
// could be provided twice — by a route, by a component — and then the flow
// marks one copy and Welcome reads the other, with nothing going red.
//
// **One writer of each half.** `ErasureFlowService` marks it after the `204`
// and before it navigates to Welcome — the one order that matters, because
// Welcome reads it when it is constructed. Welcome renders the line only after
// its own first render, so its status region exists, empty, before the line
// lands in it and the line is announced. Welcome also clears it when a
// sign-in press starts and when the screen is left. Nothing else touches it.
import { Injectable, signal, type Signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class ErasureNotice {
  readonly #erased = signal(false);

  /** Whether this tab has just erased its account and Welcome has yet to move on. */
  public readonly erased: Signal<boolean> = this.#erased.asReadonly();

  /** Records that this tab's erasure answered `204`. */
  public mark(): void {
    this.#erased.set(true);
  }

  /** Forgets it: the moment it described has passed. */
  public clear(): void {
    this.#erased.set(false);
  }
}
