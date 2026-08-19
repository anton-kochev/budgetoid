// The other half of the front door: a returning person proves who they are to
// their own authenticator, and the identity provider is not asked anything at
// all. It is the point of the whole story — an account that can only be opened
// by a third party is an account that third party can close.
//
// **Not `providedIn: 'root'`, for the reason `register.service.ts` states.** The
// welcome screen provides this service, so the flow and everything it derives
// die with the screen. Held at the root, the failure of an abandoned sign-in
// would still be readable from the injector on an unrelated screen an hour
// later, and a second screen pressing "sign in" would join a ceremony it did not
// start.
//
// **Nothing secret lives on this instance.** The assertion is a local, the
// key-encryption key is dropped where it is received, and the two signals below
// hold a boolean and one of seven words. That is what makes the state of this
// service safe to render.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { SignInApiService } from '@app-core/api/sign-in-api.service';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyFailure,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { SessionService } from '@app-core/session/session.service';

/**
 * Why the sign-in did not get further, in the words the screen says out loud.
 *
 * The first four are the ceremony's, one for one — `PasskeyCeremonyFailure`
 * argues why none of them is a synonym of another, and only `failed` is renamed,
 * to `ceremony-failed`, because `failed` on this surface would read as "signing
 * in failed" rather than "the device did not finish". The ceremony's fifth word,
 * `duplicate`, has no member here: it is an authenticator declining a credential
 * named in an exclusion list, and an assertion carries no exclusion list to
 * decline against.
 *
 * The last three are this flow's own:
 *
 *   * `start-failed` — the server never issued a challenge. Nothing was
 *     attempted on the device.
 *   * `refused` — the server read the assertion and said no.
 *   * `unknown` — no answer, or an answer that says nothing about what happened.
 *     It is **not** a synonym of `refused`; see {@link failureOf}.
 */
export type SignInFailure =
  | 'unsupported'
  | 'cancelled'
  | 'no-prf'
  | 'ceremony-failed'
  | 'start-failed'
  | 'refused'
  | 'unknown';

@Injectable()
export class SignInService {
  private readonly api = inject(SignInApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  private readonly busySignal = signal(false);
  private readonly failureSignal = signal<SignInFailure | null>(null);

  public readonly busy: Signal<boolean> = this.busySignal.asReadonly();
  public readonly failure: Signal<SignInFailure | null> =
    this.failureSignal.asReadonly();

  /**
   * Runs the whole exchange: a challenge, the authenticator, the assertion, and
   * the app.
   *
   * The guard is in this method as well as in the screen's `disabledInteractive`
   * attribute, and that is not belt and braces: Material's click-halt applies to
   * anchors only, so on a `<button>` the DOM `disabled` property stays `false`
   * and the second press arrives here. Without this line it would spend a second
   * challenge and raise a second system sheet over the first.
   */
  public signIn(): void {
    if (this.busySignal()) {
      return;
    }

    // Cleared when an act *starts*, which is the rule every act in this client
    // follows. Cleared only where one is set, the sentence from a cancelled
    // ceremony survives the press that retries it, and the reader is told what
    // went wrong last time while this time is still running.
    this.failureSignal.set(null);

    // **Before the options call**, which is the only position that costs
    // nothing. A challenge is a nonce the server persisted, so a browser that
    // was never going to finish the ceremony would otherwise spend one on its
    // way to being told exactly what it is told here for free.
    if (!this.ceremony.available()) {
      this.failureSignal.set('unsupported');

      return;
    }

    this.busySignal.set(true);
    this.api.getRequestOptions().subscribe({
      next: (options) => {
        void this.assertUnder(options);
      },
      error: () => {
        // One word for every way the options leg can end badly. There is
        // nothing to tell apart: no challenge exists, so the device was never
        // asked anything and the next step is the same for all of them.
        this.busySignal.set(false);
        this.failureSignal.set('start-failed');
      },
    });
  }

  // The ceremony and the assertion that follows it.
  private async assertUnder(options: PasskeyRequestOptionsJson): Promise<void> {
    try {
      // The server's own options, unchanged. They carry the challenge this
      // assertion is signed over, and a ceremony run against options this client
      // invented is an assertion no server would accept.
      const ceremony = await this.ceremony.assertPasskey(options);

      if (!ceremony.ok) {
        this.busySignal.set(false);
        this.failureSignal.set(
          SignInService.ceremonyFailureOf(ceremony.failure),
        );

        return;
      }

      // **The key-encryption key is taken and dropped, and both halves of that
      // are decisions.**
      //
      // `assertPasskey` asks for PRF and derives a key because the account's
      // wrapped envelopes open under exactly that value and under nothing else:
      // a sign-in that derived nothing would authenticate the person and leave
      // every row on the account unreadable the day encryption lands. The
      // cheaper assertion is the one a reader will propose, because the
      // signature the server verifies needs no PRF output at all.
      //
      // And nothing on this screen has a use for the key yet, so holding it is
      // holding the account's master key for no reason, in a place one
      // `effect()` or one devtools panel can read. A reader will want to park it
      // on a field "for the encryption epic": the epic that needs it will run
      // its own assertion or unwrap from a session that did, the way every other
      // caller will have to. Reading only `payload` off the result is what makes
      // the drop structural rather than a habit — the key is never given a name
      // here that a later line could assign from.
      this.post(ceremony.value.payload);
    } catch {
      // Nothing above throws in the ordinary course: the ceremony answers with a
      // result rather than an exception. A rejection here is therefore something
      // nobody predicted, which is what `unknown` is the word for — and
      // swallowing it would leave the screen on its waiting line forever.
      this.busySignal.set(false);
      this.failureSignal.set('unknown');
    }
  }

  // The last leg, and the only one that can end with somebody inside the app.
  private post(payload: PasskeyAssertionPayload): void {
    this.api.assert(payload).subscribe({
      next: () => {
        this.busySignal.set(false);

        // **This order, and the pair is the reason.** Publishing the session
        // first means `authGuard` reads `'authenticated'` when the navigation
        // below asks it — navigate first and the guard judges `/app` against a
        // stale `'anonymous'` and bounces the person straight back out of the
        // account they just opened. `register.service.ts` states the same rule
        // at the same point in its own flow.
        this.session.established();
        void this.router.navigateByUrl('/app');
      },
      error: (error: unknown) => {
        this.busySignal.set(false);
        this.failureSignal.set(SignInService.failureOf(error));
      },
    });
  }

  // The ceremony's five words, mapped one for one. A `switch` over the closed
  // union rather than a lookup object, so a sixth word added there fails to
  // compile here instead of arriving as `undefined` on a screen.
  private static ceremonyFailureOf(
    failure: PasskeyCeremonyFailure,
  ): SignInFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'cancelled';
      case 'no-prf':
        return 'no-prf';
      // Unreachable on this leg and handled anyway, because the union is the
      // ceremony's and not this flow's: `duplicate` is raised by an
      // authenticator declining a credential named in `excludeCredentials`, and
      // an assertion has no such list. It folds into `ceremony-failed` rather
      // than getting a word of its own — a sentence about an exclusion list on
      // the sign-in screen would describe a thing that did not happen.
      case 'duplicate':
      case 'failed':
        return 'ceremony-failed';
    }
  }

  // **`refused` is not `unknown`, and the split is the whole of this method.**
  //
  // Nothing is created either way here, which is what makes a reader ask why the
  // two readings are still separate. Because the next step differs and nothing
  // else can tell a person which one they are in. A 401 means *this passkey does
  // not work here* — the credential was never registered, or it is not this
  // account's — and the way forward is another way in, another device. An answer
  // that never came means the server is unreachable and the way forward is to
  // try the same thing again in a minute. Collapsed, one person hunts for
  // another device over a network that blinked, and the other is sent around a
  // loop that can only ever refuse them.
  //
  // The 401 is also the *only* thing the server says. Every refusal on this
  // route is byte-identical (`PasskeyVerificationExceptionHandler.cs`), so there
  // is no cause to read, and nothing here reads the body: an error kept on this
  // instance is one template binding away from putting the enumeration oracle
  // the server refuses to be back on the screen.
  private static failureOf(error: unknown): SignInFailure {
    if (error instanceof HttpErrorResponse && error.status === 401) {
      return 'refused';
    }

    // Status `0` for a request that never reached a server, every 5xx, a
    // timeout, a body that failed to parse, and anything a proxy invents. None
    // of them is a statement about this passkey.
    return 'unknown';
  }
}
