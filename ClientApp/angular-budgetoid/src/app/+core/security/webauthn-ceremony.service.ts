// The one seam that touches `navigator.credentials`, and an injectable for the
// reason `file-download.service.ts` is one: the browser APIs behind it do not
// exist under the test runner, so confining them to a class lets everything
// above stub the service instead of the platform. Everything below the seam is
// real — the translation is `webauthn-encoding.ts`'s, the derivation is
// `account-keys.ts`'s, and neither is re-implemented here.
//
// **The PRF output never crosses this module's boundary.** It is the value the
// account's key-encryption key is derived from, and therefore the value that
// unwraps the account's whole keyspace. What leaves is a non-extractable
// `CryptoKey` and never bytes, and the raw output is zero-filled the moment the
// derivation has consumed it. `account-keys.ts`'s header argues the shape of
// that at length and it is not re-argued here.
//
// **The PRF output is obtained by two routes and refused only after both.**
// `create()` is called with `eval.first` set; a great many platform
// authenticators answer `enabled: true` and return no output until the first
// *assertion*, so a client that gave up after creation would turn away exactly
// the devices this product is built around, and would do it with a message
// blaming the authenticator. When no output arrives at creation, one **local**
// assertion is run against the credential just created and then discarded.
//
// **Both members are reached now.** `register.service.ts` runs the creation
// ceremony on the passkey step of the registration flow, and `sign-in.service.ts`
// runs the assertion from the welcome screen. What still has no caller is a
// *fresh* assertion taken to authorise something else — generating a
// recovery-code set behind one is a later story. The spec remains the only place
// the custody rules above can be observed at all: a non-extractable key has no
// other witness, and "the bytes were cleared" is a claim about a buffer nobody
// else can hold.
//
// It takes no `HttpClient` and no other dependency. That is structural rather
// than tidy: a ceremony holding no way to reach the network cannot post the
// local assertion by accident, today or after somebody adds a convenience
// method next year.
import { Injectable } from '@angular/core';

import {
  PASSKEY_PRF_EVAL_INPUT,
  keyEncryptionKeyFromPasskey,
} from './account-keys';
import {
  isArrayBuffer,
  toAssertionPayload,
  toCreationOptions,
  toRegistrationPayload,
  toRequestOptions,
  type PasskeyAssertionPayload,
  type PasskeyCreationOptionsJson,
  type PasskeyRegistrationPayload,
  type PasskeyRequestOptionsJson,
} from './webauthn-encoding';

/**
 * Why a ceremony did not produce a result.
 *
 * Five words, none of which is a synonym of another, because each one is a
 * different sentence to somebody holding a device:
 *
 *   * `unsupported` — this browser cannot run the ceremony at all. Nothing was
 *     attempted.
 *   * `cancelled` — the person closed the sheet, or it timed out. Nothing is
 *     wrong and nothing needs reporting.
 *   * `duplicate` — the authenticator already holds a credential for this
 *     account and declined. That is the exclusion list working.
 *   * `no-prf` — the ceremony succeeded and the device cannot derive the
 *     account's keys. It is the one refusal that is about the *authenticator*.
 *   * `failed` — anything else, including a ceremony that resolved nothing.
 */
export type PasskeyCeremonyFailure =
  | 'unsupported'
  | 'cancelled'
  | 'duplicate'
  | 'no-prf'
  | 'failed';

/**
 * The outcome of a ceremony.
 *
 * A result rather than a thrown error, because every failure above is an
 * ordinary thing for a person to do to a device and none of them is
 * exceptional. A caller that forgets to look at `ok` cannot reach `value`.
 */
export type PasskeyCeremonyResult<TValue> =
  | { readonly ok: true; readonly value: TValue }
  | { readonly ok: false; readonly failure: PasskeyCeremonyFailure };

/** A completed registration: what to send, and what to wrap the keys under. */
export interface PasskeyRegistrationCeremony {
  readonly payload: PasskeyRegistrationPayload;
  /**
   * Non-extractable, and therefore the only shape the derived key can leave in.
   * See `account-keys.ts` for why there is no `Uint8Array` beside it.
   */
  readonly keyEncryptionKey: CryptoKey;
}

/** A completed sign-in: what to send, and what unwraps the account's keys. */
export interface PasskeyAssertionCeremony {
  readonly payload: PasskeyAssertionPayload;
  readonly keyEncryptionKey: CryptoKey;
}

// The width of the challenge the local assertion is signed over. It is never
// verified by anything — the assertion is discarded — so this number is not a
// protocol constant but the floor below which "fresh" stops meaning anything.
// Thirty-two matches what the server mints for the ceremonies that are checked.
const LOCAL_CHALLENGE_BYTES = 32;

const utf8 = new TextEncoder();

@Injectable({ providedIn: 'root' })
export class WebauthnCeremonyService {
  /**
   * Whether this browser can run a WebAuthn ceremony at all.
   *
   * Two questions and not one, because the two failures are unrelated: a
   * browser that never implemented WebAuthn serves the page over https
   * perfectly well, and a browser that implements all of it does nothing over
   * plain http. Without the secure-context half the failure is not even clean —
   * `navigator.credentials` is present in the page, so the call is made and
   * rejects with something {@link createPasskey} would report as `failed`, and
   * the person is told their device did not work when what did not work is the
   * address they loaded the page from.
   */
  public available(): boolean {
    return (
      isSecureContext &&
      'credentials' in navigator &&
      typeof PublicKeyCredential !== 'undefined'
    );
  }

  /**
   * Registers a passkey and derives the key-encryption key that factor holds.
   *
   * The returned payload is the *registration's*, byte for byte. The caller
   * completes it with the factor id it minted and the two envelopes it wrapped
   * under {@link PasskeyRegistrationCeremony.keyEncryptionKey}; this method
   * knows nothing about either, and deliberately: the account's keys are
   * generated once and wrapped once per factor, one layer up.
   */
  public async createPasskey(
    options: PasskeyCreationOptionsJson,
  ): Promise<PasskeyCeremonyResult<PasskeyRegistrationCeremony>> {
    if (!this.available()) {
      return { ok: false, failure: 'unsupported' };
    }

    try {
      // The translation is inside the `try` on purpose. `toCreationOptions`
      // throws when the server's base64url is not base64url, and that refusal
      // is the strict decoder's whole point — but this method's contract is to
      // answer with a result rather than to throw, so it becomes `failed` here
      // instead of escaping past every caller's check on the result.
      const creation = toCreationOptions(options);
      const created = await navigator.credentials.create({
        publicKey: creation,
      });

      // `create()` is typed as resolving `Credential | null`, and a null left
      // unchecked becomes a `TypeError` reading `.response` off nothing — an
      // uncaught rejection out of a method whose whole contract is a result.
      if (!(created instanceof PublicKeyCredential)) {
        return { ok: false, failure: 'failed' };
      }

      // The second route is reached only when the first produced nothing, which
      // is what keeps an authenticator that derives at creation from showing
      // the person two prompts for one registration.
      const prfOutput =
        prfOutputOf(created.getClientExtensionResults()) ??
        (await this.localPrfOutput(created, creation));

      if (prfOutput === null) {
        return { ok: false, failure: 'no-prf' };
      }

      return {
        ok: true,
        value: {
          payload: toRegistrationPayload(created),
          keyEncryptionKey: await keyEncryptionKeyFrom(prfOutput),
        },
      };
    } catch (error: unknown) {
      if (isDomException(error, 'NotAllowedError')) {
        return { ok: false, failure: 'cancelled' };
      }

      // The authenticator declining because it already holds a credential named
      // in `excludeCredentials`. Nothing is wrong and there is nothing to retry,
      // which is why it is named rather than folded into `failed` — and why it
      // is checked here and not on the assertion leg, where WebAuthn gives the
      // same word no such meaning.
      if (isDomException(error, 'InvalidStateError')) {
        return { ok: false, failure: 'duplicate' };
      }

      return { ok: false, failure: 'failed' };
    }
  }

  /**
   * Signs in with a passkey and derives the key-encryption key it holds.
   *
   * **A sign-in derives a key-encryption key, and that was decided rather than
   * inherited.** The cheaper sign-in — authenticate, derive nothing — is the
   * one a reader will propose, because the assertion the server verifies needs
   * no PRF output at all. It would authenticate the person and leave every row
   * on the account unreadable the day encryption lands: the wrapped account
   * keys are opened under exactly this value and under nothing else, so a
   * session holding no key-encryption key holds a decrypted view of nothing.
   * The registration leg's custody rule therefore applies here unchanged — a
   * non-extractable key out, no bytes anywhere, and the raw output cleared
   * behind it. Do not "simplify" this leg back to a plain assertion.
   *
   * **The PRF evaluation input is merged on here**, and not by
   * `toRequestOptions`, which is a translation of what the server sent and
   * carries no extensions.
   *
   * The server's options carry no `allowCredentials` and none is added — see
   * `toRequestOptions` for the enumeration-oracle argument. The spread below
   * preserves that absence; it does not restate it.
   */
  public async assertPasskey(
    options: PasskeyRequestOptionsJson,
  ): Promise<PasskeyCeremonyResult<PasskeyAssertionCeremony>> {
    if (!this.available()) {
      return { ok: false, failure: 'unsupported' };
    }

    try {
      // Inside the `try` for the reason {@link createPasskey} states: the
      // strict decoder's refusal is a result on this boundary, not a throw out
      // of a method whose whole contract is to answer with one.
      const request = toRequestOptions(options);
      const asserted = await navigator.credentials.get({
        publicKey: {
          ...request,
          extensions: { prf: { eval: { first: prfEvalInput() } } },
        },
      });

      if (!(asserted instanceof PublicKeyCredential)) {
        return { ok: false, failure: 'failed' };
      }

      // No second route here — an assertion *is* the route — so the refusal is
      // immediate. It is still `no-prf` and not `failed`: the ceremony
      // succeeded and the device simply cannot hold the account's keys.
      const prfOutput = prfOutputOf(asserted.getClientExtensionResults());

      if (prfOutput === null) {
        return { ok: false, failure: 'no-prf' };
      }

      return {
        ok: true,
        value: {
          payload: toAssertionPayload(asserted),
          keyEncryptionKey: await keyEncryptionKeyFrom(prfOutput),
        },
      };
    } catch (error: unknown) {
      if (isDomException(error, 'NotAllowedError')) {
        return { ok: false, failure: 'cancelled' };
      }

      return { ok: false, failure: 'failed' };
    }
  }

  // The second route: one assertion against the credential that was just
  // created, run on the device and **never sent anywhere**. Its client data,
  // its authenticator data and its signature are over a challenge this client
  // invented, which no server issued and none would accept; sending them would
  // be sending a forged sign-in alongside a registration. Nothing it produced
  // reaches the payload, and the service holds no way to send it in any case.
  private async localPrfOutput(
    created: PublicKeyCredential,
    creation: PublicKeyCredentialCreationOptions,
  ): Promise<Uint8Array<ArrayBuffer> | null> {
    const asserted = await navigator.credentials.get({
      publicKey: {
        // Fresh, and drawn from the platform's generator. The registration
        // challenge is the only other one in scope and replaying it is the
        // shape this mistake takes: an assertion signed over a value the server
        // has already consumed. Nothing here would notice, because the
        // assertion is discarded — what would notice is the day somebody
        // decides to send it.
        challenge: freshChallenge(),
        rpId: creation.rp.id,
        timeout: creation.timeout,
        // The server's word, taken off the options the credential was just
        // created under rather than invented here. Several authenticators will
        // not evaluate the extension without user verification, and this
        // ceremony is in no position to relax what registration demanded.
        userVerification: creation.authenticatorSelection?.userVerification,
        // **`allowCredentials` names the new credential, and must.** WebAuthn
        // refuses `evalByCredential` outright when the list is empty, with a
        // `NotSupportedError` that says nothing about the cause.
        //
        // This does not contradict the rule that the *server's* request options
        // carry none. That rule is about an endpoint anybody can call: a server
        // that names an account's credentials answers "here are that account's
        // credentials" for one address and nothing for another, which is an
        // enumeration oracle. This list is built on the device, out of a
        // credential the device made a moment ago, and never leaves it.
        allowCredentials: [
          { type: 'public-key', id: new Uint8Array(created.rawId) },
        ],
        // `evalByCredential` and not `eval`: keyed on anything else, the
        // authenticator derives for whatever credential it happened to offer,
        // and the account's keys end up wrapped under a key the new passkey
        // cannot reproduce.
        extensions: {
          prf: {
            evalByCredential: { [created.id]: { first: prfEvalInput() } },
          },
        },
      },
    });

    return asserted instanceof PublicKeyCredential
      ? prfOutputOf(asserted.getClientExtensionResults())
      : null;
  }
}

// Derives the key-encryption key and then clears the bytes it derived from,
// whatever happened.
//
// The `finally` is the whole of it: a derivation that rejects leaves the PRF
// output in a buffer the platform still holds a reference to, and the one path
// that would skip the wipe is the one where something already went wrong.
async function keyEncryptionKeyFrom(
  prfOutput: Uint8Array<ArrayBuffer>,
): Promise<CryptoKey> {
  try {
    return await keyEncryptionKeyFromPasskey(prfOutput);
  } finally {
    prfOutput.fill(0);
  }
}

// The PRF output as a **view over the platform's own buffer**, never a copy.
//
// That is the point rather than a shortcut: {@link keyEncryptionKeyFrom} wipes
// what it is handed, and a copy would leave the buffer the browser produced
// holding the account's key-encryption material after the wipe reported
// success. The wipe would pass its own test and clear nothing that mattered.
//
// The buffer type is spelled out because `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either. Narrowing
// it here is what lets the derivation take these bytes without a copy — and a
// copy is the one thing this function must not make.
//
// **The narrowing below is imported, and that is what holds it.** Read what a
// `false` on genuine bytes does here: the view branch reads `.buffer`,
// `.byteOffset` and `.byteLength` off an `ArrayBuffer`, which carries none of
// them, so `new Uint8Array(undefined, undefined, undefined)` yields an **empty**
// array and nothing throws. The account's key-encryption key is then derived
// from zero bytes, identically on every device, and `keyEncryptionKeyFrom`'s
// `fill(0)` clears that empty view while the platform's buffer keeps the PRF
// output. Both halves of this module's custody rule fail silently, at once.
// A local copy of the predicate guarded exactly that and was held by nothing —
// this spec and this module share a realm under the test runner, so reverting a
// copy to `instanceof` reddens no test. The import puts this call site behind
// the one definition twelve specs do hold; `webauthn-encoding.ts` argues the
// check itself.
function prfOutputOf(
  results: AuthenticationExtensionsClientOutputs,
): Uint8Array<ArrayBuffer> | null {
  const first = results.prf?.results?.first;

  if (first === undefined) {
    return null;
  }

  return isArrayBuffer(first)
    ? new Uint8Array(first)
    : new Uint8Array(first.buffer, first.byteOffset, first.byteLength);
}

// The value the extension is evaluated against, encoded fresh per call and read
// off `account-keys.ts` rather than typed again. A second copy of the string is
// a second place for it to drift, and the day it drifts every account that
// wrapped its keys under the old value is locked out by a passkey that still
// authenticates perfectly and simply hands back different bytes.
function prfEvalInput(): Uint8Array<ArrayBuffer> {
  return utf8.encode(PASSKEY_PRF_EVAL_INPUT);
}

// From `crypto.getRandomValues` and from nowhere else, for the reason
// `account-keys.ts` gives for the account's own keys: `Math.random` passes every
// shape-based check while being seeded from a value the page does not control
// and short enough to walk.
function freshChallenge(): Uint8Array<ArrayBuffer> {
  const challenge = new Uint8Array(LOCAL_CHALLENGE_BYTES);
  crypto.getRandomValues(challenge);

  return challenge;
}

// `instanceof` first, and the name second. An implementation that assumed every
// rejection is a `DOMException` would read `.name` off a `TypeError`, find
// `"TypeError"`, and fall through to whatever its last branch happened to be.
function isDomException(error: unknown, name: string): boolean {
  return error instanceof DOMException && error.name === name;
}
