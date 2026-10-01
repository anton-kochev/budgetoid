import {
  DOCUMENT,
  Injectable,
  inject,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';

// The one owner of "this page is leaving for the identity provider", and the
// one place the page leaves from.
//
// **Only `AuthService` writes it.** A press raises `departing` with `begin`
// before its first await, the provider client's `openUri` is `depart`, and
// `settle` lowers it when a press does not leave or a restore from the
// back-forward cache brings the page back. A screen reads `departing` and keeps
// no copy of its own: a copy is a second fact that the restore never reaches,
// so the screen would go on saying it is leaving on a page that came back.
//
// **It is its own class, not a signal on `AuthService`**, so a screen can read
// the flag without injecting `AuthService` — which would make the screen an
// identity-provider caller in the NFR-025 census
// (`identity-provider-callers.spec.ts`).
//
// Root-provided, so the press that raises the flag and the screen that reads it
// see one instance. See docs/design/components.md, "Changing the email
// address".
@Injectable({ providedIn: 'root' })
export class ProviderDepartureService {
  private readonly document = inject(DOCUMENT);

  private readonly departingSignal: WritableSignal<boolean> = signal(false);

  /** Whether the page is on its way to the provider. A reading only. */
  public readonly departing: Signal<boolean> =
    this.departingSignal.asReadonly();

  /** A press has started a trip; there is no address to open yet. */
  public begin(): void {
    this.departingSignal.set(true);
  }

  /** Leaves for `uri` as a top-level navigation. */
  public depart(uri: string): void {
    // **Raised first.** The page may be gone the moment the address is
    // assigned, and anything read after that point has to say it is leaving.
    this.departingSignal.set(true);
    this.document.location.assign(uri);
  }

  /** The page is not leaving after all, or it came back. */
  public settle(): void {
    this.departingSignal.set(false);
  }
}
