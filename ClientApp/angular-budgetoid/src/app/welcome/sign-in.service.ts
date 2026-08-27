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
// key-encryption key is handed on in the statement it is read and never given a
// name this class could assign from, and the two signals below hold a boolean
// and one of seven words. That is what makes the state of this service safe to
// render.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { SignInApiService } from '@app-core/api/sign-in-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
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
  // Root-provided, unlike this service and unlike everything else it is
  // injected with, and the difference is the point: what this screen produces
  // is state of the **session**, and a session outlives the screen that opened
  // it. `account-key-custody.service.ts` argues the scope at length.
  private readonly custody = inject(AccountKeyCustodyService);
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

      // **The key-encryption key is taken and handed straight on, in one
      // statement, and both halves of that are decisions.**
      //
      // `assertPasskey` asks for PRF and derives a key because the account's
      // wrapped envelopes open under exactly that value and under nothing else:
      // a sign-in that derived nothing would authenticate the person and leave
      // every row on the account unreadable. The cheaper assertion is the one a
      // reader will propose, because the signature the server verifies needs no
      // PRF output at all.
      //
      // And having derived it, this service still may not *keep* it. It travels
      // as an argument — through here, into {@link post}, into
      // `AccountKeyCustodyService.unlock` — and is never assigned to a field, a
      // signal or a local of this method. Both members are read in the one
      // statement below for exactly that reason: there is no name here a later
      // line could assign from, which is what makes the rule structural rather
      // than a habit. A key parked on this instance is the account's master key
      // sitting one `effect()` or one devtools panel away from being read, on a
      // screen whose whole state is otherwise safe to render.
      //
      // **Custody is the session's, not this screen's**, which is why the key
      // goes to a root-provided service rather than being held here for the
      // rest of the visit: `/welcome` is discarded by the navigation this flow
      // ends with.
      this.post(ceremony.value.payload, ceremony.value.keyEncryptionKey);
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
  //
  // `keyEncryptionKey` is a parameter and reaches nothing but the call below.
  private post(
    payload: PasskeyAssertionPayload,
    keyEncryptionKey: CryptoKey,
  ): void {
    this.api.assert(payload).subscribe({
      next: () => {
        this.busySignal.set(false);

        // **This order, and each pair of neighbours is the reason.** Publishing
        // the session first means `authGuard` reads `'authenticated'` when the
        // navigation below asks it — navigate first and the guard judges `/app`
        // against a stale `'anonymous'` and bounces the person straight back out
        // of the account they just opened. `register.service.ts` states the same
        // rule at the same point in its own flow.
        this.session.established();
        this.unlock(keyEncryptionKey);
        void this.router.navigateByUrl('/app');
      },
      error: (error: unknown) => {
        this.busySignal.set(false);
        this.failureSignal.set(SignInService.failureOf(error));
      },
    });
  }

  // Opens the account's keys, and cannot do anything else.
  //
  // **Not awaited, and there is nothing to await**: `unlock` returns `void` on
  // purpose, and its own doc argues why. Awaited, a round trip would land
  // between a verified assertion and the app; one refactor later the `await`
  // grows a `catch`, and a key that did not open becomes an authentication that
  // failed. Only `anonymous` may bounce anybody out of an account, and a factor
  // that opened nothing is not that.
  //
  // **Not in the `APP_INITIALIZER` either.** A cold load holds no
  // key-encryption key, so a probe there would spend a round trip on every cold
  // load fetching envelopes it has nothing to open — on the one path that is
  // awaited and that every guard's synchronicity depends on. The moment a
  // browser holds the key is this one.
  //
  // **The `try` is what keeps the two lines around this call independent of it.**
  // `unlock` is documented not to throw, and this method does not take that on
  // trust: an exception out of the `next` handler is not routed to the `error`
  // callback beside it, it is reported as an unhandled rejection and the
  // statements after it never run — so a throwing custody would strand somebody
  // holding a valid session cookie on `/welcome`, with the screen saying
  // nothing because the sign-in did not fail. Swallowed, and deliberately not
  // published: this screen says one thing however a sign-in was refused, the
  // server answers every refusal with one byte-identical 401 on purpose, and a
  // sentence that varied by whether a key opened would rebuild the
  // credential-enumeration oracle the server refuses to be. What the failure is
  // readable from is `AccountKeyCustodyService.unlockFailure`, which is a fact
  // about a factor rather than about a sign-in.
  private unlock(keyEncryptionKey: CryptoKey): void {
    try {
      this.custody.unlock(keyEncryptionKey);
    } catch {
      // Nothing. See above.
    }
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
