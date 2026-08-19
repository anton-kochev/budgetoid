// The registration flow: one act, and the only place in this client where the
// account's keys exist in the clear.
//
// **Not `providedIn: 'root'`, and that is a custody decision rather than a
// lifetime preference.** The register screen provides this service, so it is
// discarded with the screen — and the account keys, the eleven key-encryption
// keys and the ten recovery codes are discarded with it. Held at the root, the
// codes of an abandoned registration would still be readable from the injector
// on an unrelated screen an hour later, and there is nowhere in this product
// they could legitimately be read from.
//
// **A value is a signal only if a template renders it.** Everything else is a
// local or a private field. A signal on an injectable is one `effect()` away
// from being logged by somebody who wanted to debug a re-render, and these are
// the values that unlock the account. That rule is why the key-encryption keys
// never touch `this` at all, why the account keys are wiped inside the method
// that draws them, and why the ten codes — which the codes step must render —
// are the one secret published here.
//
// The ordering rules below are each load-bearing and each argued at the line
// that implements them. `RegisterAccountHandler.cs` is the other half of most of
// them; where a rule is really the server's, the comment points at it.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  RegistrationApiService,
  type RecoveryCodeSubmissionBody,
  type RegistrationRequestBody,
} from '@app-core/api/registration-api.service';
import {
  generateAccountKeys,
  keyEncryptionKeyFromRecoveryCode,
  wrapAccountKeys,
} from '@app-core/security/account-keys';
import { mintFactorId } from '@app-core/security/factor-id';
import {
  mintRecoveryCodeSet,
  type RecoveryCode,
} from '@app-core/security/recovery-codes';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyFailure,
} from '@app-core/security/webauthn-ceremony.service';
import type { PasskeyCreationOptionsJson } from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { SessionService } from '@app-core/session/session.service';

/**
 * Where the reader is.
 *
 * Three places and not a state machine over outcomes: `busy` and `failure` are
 * separate readings on purpose, because a step is *where somebody is standing*
 * and those two are *what is happening there*. Folded into this union, every
 * failure would move the person somewhere, and the screen would have to move
 * them back.
 */
export type RegisterStep = 'intro' | 'passkey' | 'codes';

/**
 * Why the flow did not get further, in the words the screen says out loud.
 *
 * The first five are the ceremony's, one for one — `PasskeyCeremonyFailure`
 * argues why none of them is a synonym of another, and only `failed` is renamed,
 * to `ceremony-failed`, because `failed` on this surface would read as "the
 * registration failed" rather than "the device did not finish".
 *
 * The last four are this flow's own:
 *
 *   * `start-failed` — the server never issued a challenge. Nothing was minted.
 *   * `refused` — the server judged the request and said no. Nothing was
 *     created and the codes on screen are dead.
 *   * `conflict` — an account already exists for this provider identity.
 *   * `unknown` — no answer, or an answer that says nothing about what
 *     happened. It is **not** a synonym of `refused`; see {@link create}.
 */
export type RegisterFailure =
  | 'unsupported'
  | 'cancelled'
  | 'duplicate'
  | 'no-prf'
  | 'ceremony-failed'
  | 'start-failed'
  | 'refused'
  | 'conflict'
  | 'unknown';

@Injectable()
export class RegisterService {
  private readonly api = inject(RegistrationApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  private readonly stepSignal = signal<RegisterStep>('intro');
  private readonly busySignal = signal(false);
  private readonly failureSignal = signal<RegisterFailure | null>(null);
  // The one secret in a signal, because the codes step renders it and there is
  // no other way to hand ten values to a template. It reaches no storage, no
  // route state and no `history.state`; `null` means "not minted", which is a
  // different thing from an empty set and is never collapsed into one.
  private readonly codesSignal = signal<readonly RecoveryCode[] | null>(null);
  private readonly restartedSignal = signal(false);

  public readonly step: Signal<RegisterStep> = this.stepSignal.asReadonly();
  public readonly busy: Signal<boolean> = this.busySignal.asReadonly();
  public readonly failure: Signal<RegisterFailure | null> =
    this.failureSignal.asReadonly();
  public readonly codes: Signal<readonly RecoveryCode[] | null> =
    this.codesSignal.asReadonly();
  /**
   * The address the provider asserted, for the screen to show back.
   *
   * A `computed` over a claim rather than a stored copy: the claim is the one
   * fact this flow has about who is registering, and reading it where it lives
   * keeps this service from holding a second, older copy of it. It has no signal
   * dependency, so it settles on first read and stays — which is what is wanted
   * here, because {@link create} discards the provider token moments before the
   * screen goes away and a re-reading value would blank the header on the way
   * out.
   */
  public readonly email: Signal<string | null> = computed(() =>
    this.auth.providerEmail(),
  );
  public readonly restarted: Signal<boolean> =
    this.restartedSignal.asReadonly();

  // The assembled request body, and the reason it is a field rather than an
  // argument to `create()`: what a person acknowledges on the codes step is
  // that they have kept *these* codes, and the body that is posted must be the
  // one whose envelopes those codes open. Passing it back in through the screen
  // would put a rendered value between the two.
  //
  // Cleared on the 201, on any failure of the POST, by `restart()`, and with the
  // screen. It carries no code and no key — only verifiers and sealed envelopes
  // — but it is the request that creates an account, and one that outlives its
  // outcome is one somebody will eventually re-send.
  private pending: RegistrationRequestBody | null = null;

  /**
   * Leaves the introduction for the passkey step.
   *
   * `failure` is cleared here rather than only where one is set, which is the
   * rule every act in this service follows: a failure is cleared when an act
   * *starts*. Cleared only on failure, the sentence from a cancelled ceremony
   * survives the press that retries it, and the reader is told what went wrong
   * last time while this time is still running.
   */
  public begin(): void {
    this.failureSignal.set(null);
    this.stepSignal.set('passkey');
  }

  /**
   * Registers the passkey, draws the account's keys, mints the card of codes,
   * and wraps the keys under all eleven factors.
   *
   * The order of the first three steps is the whole of this method's design and
   * each is argued where it happens. Nothing is posted here: what this produces
   * is {@link pending} and ten codes on screen, and the account is created by
   * {@link create} once the person says they have kept them.
   */
  public createPasskey(): void {
    if (this.busySignal()) {
      return;
    }

    this.failureSignal.set(null);

    // **Before the options call**, which is the only position that costs
    // nothing. A browser that was never going to finish the ceremony would
    // otherwise spend a challenge on its way to being told so, and a challenge
    // is a nonce the server persisted — on this route it is also the value the
    // account identifier is derived from.
    if (!this.ceremony.available()) {
      this.failureSignal.set('unsupported');

      return;
    }

    this.busySignal.set(true);
    this.api.getCreationOptions().subscribe({
      next: (options) => {
        void this.mintUnder(options);
      },
      error: () => {
        // One word for every way the options leg can end badly. There is
        // nothing to tell apart: no challenge exists, so nothing was minted,
        // nothing was spent and the next step is the same for all of them.
        this.busySignal.set(false);
        this.failureSignal.set('start-failed');
      },
    });
  }

  /**
   * Posts the assembled registration and, on 201, hands the visitor to the app.
   *
   * **There is no retry, only {@link restart}.** The challenge is consumed
   * before anything is verified (`RegisterAccountHandler.cs:152-165`), so a
   * second POST of the same body meets the undifferentiated challenge refusal
   * with certainty. A retry button here would look like a way out and be a way
   * to be told no twice.
   */
  public create(): void {
    const body = this.pending;

    if (body === null || this.busySignal()) {
      return;
    }

    this.failureSignal.set(null);
    this.busySignal.set(true);

    // **The subscription is deliberately not torn down with the screen**, for
    // the reason `settings.service.ts` gives about the export: the request has
    // been made and an account is being created by it, so the three statements
    // below are how this application stops lying about who the visitor is. A
    // subscription cancelled on destroy would leave a created account behind a
    // client still calling itself anonymous.
    this.api.register(body).subscribe({
      next: () => {
        this.pending = null;
        this.busySignal.set(false);

        // **This order, and each pair of neighbours is the reason.** Publishing
        // the session first means `authGuard` reads `'authenticated'` when the
        // navigation below asks it — navigate first and the guard judges `/app`
        // against a stale `'anonymous'` and bounces the person out of the
        // account they just created. Forgetting the provider token last, for the
        // mirror of that: dropped before the navigation, it is dropped while a
        // request may still be leaving with a bearer attached to it.
        this.session.established();
        this.auth.forgetProviderToken();
        void this.router.navigateByUrl('/app');
      },
      error: (error: unknown) => {
        this.pending = null;
        this.busySignal.set(false);
        this.failureSignal.set(RegisterService.failureOf(error));
      },
    });
  }

  /**
   * Throws this attempt away and starts a new one from the passkey step.
   *
   * Everything is re-drawn: a new challenge, a new passkey, new account keys,
   * new codes and eleven new factor identifiers. Nothing from the previous
   * attempt is reused, and nothing could be — the challenge is spent and the
   * keys were wiped.
   *
   * `restarted` is set and never cleared. It is what lets the screen say that
   * the codes somebody may have written down a minute ago belong to nothing,
   * and that stays true for the rest of the visit whatever happens next.
   */
  public restart(): void {
    if (this.busySignal()) {
      return;
    }

    this.pending = null;
    this.codesSignal.set(null);
    this.failureSignal.set(null);
    this.restartedSignal.set(true);
    this.stepSignal.set('passkey');
  }

  // The ceremony, the keys, the codes and the eleven wraps — the whole of what
  // has to happen between a challenge arriving and a person being shown ten
  // codes.
  private async mintUnder(options: PasskeyCreationOptionsJson): Promise<void> {
    try {
      // **The device agrees first, and this departs from the story's written
      // plan on purpose.** The plan minted the card before running the
      // ceremony. Cancelling the system passkey sheet is the most common thing
      // that happens on this screen, and minting first leaves that person's
      // browser holding ten recovery codes for a flow that ended — secrets
      // created for an account that does not exist, which is
      // `codes-step.component.ts:22-31`'s rule read in the other direction. In
      // this order nothing secret is created until the authenticator has
      // agreed. It costs nothing: the challenge is already spent either way.
      const ceremony = await this.ceremony.createPasskey(options);

      if (!ceremony.ok) {
        this.busySignal.set(false);
        this.failureSignal.set(
          RegisterService.ceremonyFailureOf(ceremony.failure),
        );

        return;
      }

      // **Once, here, outside every loop.** The account owns one content key
      // and one index key and every factor wraps *those*; a pair drawn per
      // factor passes every round trip and every constraint, and gives the
      // second factor a second, incompatible account — see `account-keys.ts`.
      const keys = generateAccountKeys();

      try {
        const set = await mintRecoveryCodeSet();

        const passkeyFactorId = mintFactorId();
        const passkeyKeys = await wrapAccountKeys(
          ceremony.value.keyEncryptionKey,
          keys,
          passkeyFactorId,
        );

        // **One code's four members are produced in one scope, from one code.**
        // The obvious implementation derives ten key-encryption keys into an
        // array, wraps ten times into a second, and zips those against the
        // verifiers at post time. That satisfies every type, every count and
        // every round trip — and pairs one code's verifier with another code's
        // envelopes the first time anybody reorders anything. Nothing on either
        // side of the wire can see it: the set validates, the account is
        // created, a session is handed over, and it is discovered by somebody
        // who redeemed a code months later and found the account still locked.
        // It is the client-side twin of the argument at
        // `RegisterAccountHandler.cs:303-307`.
        //
        // The factor id is minted inside the loop for the same reason, rather
        // than eleven at a time up front: an array of ten identifiers is a
        // third thing to zip. Nothing checks the eleven for distinctness —
        // a `randomUUID` collision is not a case a branch can honestly cover,
        // and the server refuses one (`RegisterAccountHandler.cs:248-253`).
        const codes: RecoveryCodeSubmissionBody[] = [];

        for (let index = 0; index < set.codes.length; index += 1) {
          const code = set.codes[index];
          const verifier = set.verifiers[index];
          const factorId = mintFactorId();
          const wrapped = await wrapAccountKeys(
            await keyEncryptionKeyFromRecoveryCode(code),
            keys,
            factorId,
          );

          // The key-encryption key above is an expression and never a field:
          // eleven of them exist during this method and none of them outlives
          // it, whatever a later reader adds to this class. They are
          // non-extractable by construction as well (`account-keys.ts:345-357`),
          // so neither half of that rests on the other.
          codes.push({ verifier, factorId, ...wrapped });
        }

        // **The account keys are wiped here and kept for nothing.** A reader
        // will want to hold them "for the encryption epic": nothing on this
        // client encrypts anything yet, every path that retries re-draws them,
        // and the epic that needs them will unwrap them from an envelope the
        // way every other session will have to. Keeping 64 bytes of the
        // account's whole keyspace alive for a feature that does not exist is
        // the one decision available here with no upside at all. They are also
        // locals rather than a field, which is the same rule the eleven
        // key-encryption keys follow — a value that never lives on the instance
        // cannot outlive the call.
        keys.contentKey.fill(0);
        keys.indexKey.fill(0);

        this.pending = {
          // Spread, and safe **only** because `toRegistrationPayload` projects
          // the client's extension results into a fresh `{prf:{enabled}}`
          // rather than forwarding them (`webauthn-encoding.ts:342-352`). A
          // spread of a raw credential's results would ship `prf.results.first`
          // — the PRF output itself, which is the value the passkey factor's
          // key-encryption key is derived from.
          ...ceremony.value.payload,
          factorId: passkeyFactorId,
          ...passkeyKeys,
          codes,
        };

        this.codesSignal.set(set.codes);
        this.stepSignal.set('codes');
        this.busySignal.set(false);
      } finally {
        // However this ended. A wrap that rejected halfway leaves the account
        // keys in two buffers the runtime still holds, and that is the path
        // where something has already gone wrong.
        keys.contentKey.fill(0);
        keys.indexKey.fill(0);
      }
    } catch {
      // Nothing above throws in the ordinary course: the ceremony answers with
      // a result rather than an exception, and the crypto is the platform's. A
      // rejection here is therefore something nobody predicted, which is what
      // `unknown` is the word for — and swallowing it would leave the screen on
      // a spinner forever.
      this.busySignal.set(false);
      this.failureSignal.set('unknown');
    }
  }

  // The ceremony's five words, mapped one for one. A `switch` over the closed
  // union rather than a lookup object, so a sixth word added there fails to
  // compile here instead of arriving as `undefined` on a screen.
  private static ceremonyFailureOf(
    failure: PasskeyCeremonyFailure,
  ): RegisterFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'cancelled';
      case 'duplicate':
        return 'duplicate';
      case 'no-prf':
        return 'no-prf';
      case 'failed':
        return 'ceremony-failed';
    }
  }

  // **`refused` and `conflict` are not `unknown`, and this is the most
  // consequential branch in the file.**
  //
  // Every 400 leaves `RegisterAccountHandler` by exception before `RegisterAsync`
  // is reached, a 401 and a 403 are refused before the handler is entered at
  // all, and a 409 refuses *this* request against an account that already
  // exists. All four are certainly-not-created, so the codes on screen are
  // certainly dead and the honest sentence is to throw them away and start
  // again.
  //
  // A network failure, a timeout and a 5xx are not. The request may have
  // arrived, committed all thirty rows and had its 201 lost on the way back.
  // Collapsing the two readings means telling somebody whose account *was*
  // created that the only codes it has are worthless — and there is no way for
  // them to make more, because `POST /api/me/recovery-codes` has no caller in
  // this client. It is `session.service.ts:58-76`'s four-valued reading, and
  // `SettingsService`'s rule about never collapsing `null` into `0`, on the one
  // screen where the cost is an account nobody can ever open again.
  private static failureOf(error: unknown): RegisterFailure {
    if (!(error instanceof HttpErrorResponse)) {
      return 'unknown';
    }

    switch (error.status) {
      case 400:
      case 401:
      case 403:
        return 'refused';
      case 409:
        return 'conflict';
      default:
        // Status `0` for a request that never reached a server, every 5xx, and
        // anything else a proxy invents. None of them is a statement about
        // whether the account exists.
        return 'unknown';
    }
  }
}
