// The translation layer between the API's JSON and the shapes WebAuthn's own
// calls take. Every member that is bytes on the wire is base64url text in JSON
// and a `BufferSource` in the browser, in both directions, and this module is
// the one place that crossing happens.
//
// **The registration payload projects the client extension results; it never
// passes them through.** The argument is at the projection itself, because that
// is where a reader would otherwise write the one line that hands the operator
// every account key in the product.
//
// **The decoder is `base64url.ts`'s and there is no second one.** It refuses
// padding, the standard alphabet's `+` and `/`, and a final group no encoder
// would emit, and those refusals *propagate* out of this module rather than
// being caught and softened into a default. A lenient second decoder written
// here in a hurry — "the browser wants bytes" — would accept a value the
// server's own decoder rejects, and the symptom never arrives on the malformed
// input: it arrives months later, on somebody else's request, as an envelope
// that will not open.
//
// **The words WebAuthn declares as closed sets cross as the server's words.**
// That is the one place this module asserts rather than checks, and the reason
// is at {@link serverWord} together with the cost, which is not the one a reader
// expects.
//
// This module ships with a spec and **no caller**, exactly as `account-keys.ts`
// and `recovery-codes.ts` do, and for the same reason: the screens that run
// these ceremonies — registering a passkey, signing in with one — are later
// stories. Its spec is meanwhile the only place several of these rules can be
// checked at all.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so a function is the whole of it; the seam
// that touches `navigator.credentials` is a service next door, and it is one
// because the platform it calls does not exist under the test runner, not
// because a translation needs an object to live in.
import { PASSKEY_PRF_EVAL_INPUT } from './account-keys';
import { decodeBase64Url, encodeBase64Url } from './base64url';

/**
 * What `POST /api/passkeys/registration/options` answers with.
 *
 * `PasskeyCreationOptions.cs`, member for member, camel-cased by the API's
 * serializer. The members WebAuthn declares as closed sets of words are `string`
 * here because that is what JSON carries and what the server owns — see
 * {@link serverWord}.
 */
export interface PasskeyCreationOptionsJson {
  readonly challenge: string;
  readonly rp: { readonly id: string; readonly name: string };
  readonly user: {
    readonly id: string;
    readonly name: string;
    readonly displayName: string;
  };
  readonly pubKeyCredParams: readonly {
    readonly type: string;
    readonly alg: number;
  }[];
  readonly timeout: number;
  readonly attestation: string;
  readonly authenticatorSelection: {
    readonly residentKey: string;
    readonly requireResidentKey: boolean;
    readonly userVerification: string;
  };
  readonly excludeCredentials: readonly {
    readonly type: string;
    readonly id: string;
  }[];
  /**
   * The server's `{"prf":{}}` — a question about the credential's capability,
   * carrying no value to evaluate.
   *
   * Declared so this contract mirrors the one the server publishes, and
   * deliberately not read: there is nothing inside it to preserve, and
   * {@link toCreationOptions} supplies the evaluation input itself. The day the
   * server sends an extension carrying a value, the merge goes there.
   */
  readonly extensions: { readonly prf: unknown };
}

/**
 * What `POST /api/passkeys/assertion/options` answers with.
 *
 * `PasskeyRequestOptions.cs`, which has four members and deliberately not a
 * fifth — there is no `allowCredentials`, and its absence is the design.
 */
export interface PasskeyRequestOptionsJson {
  readonly challenge: string;
  readonly rpId: string;
  readonly timeout: number;
  readonly userVerification: string;
}

/**
 * What the client says the `prf` extension did, and the whole of what it says.
 *
 * `PasskeyPrfResults(bool? Enabled)` on the server. `null` is "the authenticator
 * answered nothing about the extension", which is a different statement from
 * `false` and is why the member is nullable on both sides.
 */
export interface PasskeyPrfResultsJson {
  readonly enabled: boolean | null;
}

/** The extension results a registration payload may carry. */
export interface PasskeyClientExtensionResultsJson {
  readonly prf: PasskeyPrfResultsJson | null;
}

/**
 * The three members of `CompleteRegistrationCommand` this module can build.
 *
 * The other three — `factorId`, `wrappedContentKey` and `wrappedIndexKey` — are
 * the caller's: they come from `account-keys.ts` and from a factor identifier
 * the client mints, and neither is anything an authenticator response contains.
 */
export interface PasskeyRegistrationPayload {
  readonly clientDataJson: string;
  readonly attestationObject: string;
  readonly clientExtensionResults: PasskeyClientExtensionResultsJson;
}

/** `CompleteAssertionCommand`, member for member. */
export interface PasskeyAssertionPayload {
  readonly credentialId: string;
  readonly clientDataJson: string;
  readonly authenticatorData: string;
  readonly signature: string;
  /** Absent when the authenticator returned none, and never an empty string. */
  readonly userHandle: string | null;
}

const utf8 = new TextEncoder();

/**
 * Translates the server's creation options into the `publicKey` member of a
 * `navigator.credentials.create()` call.
 *
 * **The PRF evaluation input is added here and comes from `account-keys.ts`.**
 * The server sends `{"prf":{}}`, which asks the authenticator whether it can
 * derive without asking it to derive anything, so the input is the client's to
 * supply. Missing, the whole ceremony still succeeds: the authenticator answers
 * `enabled: true`, the server accepts the registration, and there is no PRF
 * output to derive a key from. It is read off `account-keys.ts` and never typed
 * again here, because a second copy of the string agrees with the first for
 * exactly as long as nobody edits one of them — and the day they part, every
 * account whose keys were wrapped under the old value is locked out by a passkey
 * that still authenticates perfectly and simply hands back different bytes.
 *
 * Throws whatever `decodeBase64Url` throws. Every binary member is decoded and
 * none is repaired: a challenge quietly padded out is a ceremony bound to bytes
 * nobody chose, a user handle repaired is 15 bytes where the account's id is 16,
 * and an excluded credential repaired excludes a credential that does not exist.
 */
export function toCreationOptions(
  options: PasskeyCreationOptionsJson,
): PublicKeyCredentialCreationOptions {
  return {
    challenge: decodedBytes(options.challenge),
    rp: { id: options.rp.id, name: options.rp.name },
    user: {
      id: decodedBytes(options.user.id),
      name: options.user.name,
      displayName: options.user.displayName,
    },
    pubKeyCredParams: options.pubKeyCredParams.map((parameter) => ({
      type: serverWord<PublicKeyCredentialType>(parameter.type),
      alg: parameter.alg,
    })),
    timeout: options.timeout,
    attestation: serverWord<AttestationConveyancePreference>(
      options.attestation,
    ),
    authenticatorSelection: {
      residentKey: serverWord<ResidentKeyRequirement>(
        options.authenticatorSelection.residentKey,
      ),
      requireResidentKey: options.authenticatorSelection.requireResidentKey,
      userVerification: serverWord<UserVerificationRequirement>(
        options.authenticatorSelection.userVerification,
      ),
    },
    // The exclusion list is what makes an authenticator that already holds a
    // credential for this account decline instead of enrolling a second one.
    // Dropped, or left as text the browser hashes as UTF-16 code units, the
    // second enrolment succeeds and the account acquires two factors where the
    // person made one.
    excludeCredentials: options.excludeCredentials.map((descriptor) => ({
      type: serverWord<PublicKeyCredentialType>(descriptor.type),
      id: decodedBytes(descriptor.id),
    })),
    extensions: {
      prf: { eval: { first: utf8.encode(PASSKEY_PRF_EVAL_INPUT) } },
    },
  };
}

/**
 * Translates the server's request options into the `publicKey` member of a
 * `navigator.credentials.get()` call.
 *
 * **No `allowCredentials`, and the absence survives translation.** A populated
 * one means the server first decided which credentials belong to the person
 * signing in, which means the request had to name them — and an endpoint
 * answering "here are that account's credentials" for one address and nothing
 * for another is an account-enumeration oracle. `PasskeyRequestOptions.cs` has
 * no such member at all, so the only way one can appear is if this function
 * invents it: an empty array "for completeness", or a list built from something
 * the client happens to know. Neither is added, and the member is left off
 * rather than set to `undefined`.
 *
 * **No `prf` extension either, and that is a decision rather than an
 * oversight.** This function is a translation of what the server sent, and the
 * server sends no extensions on this leg. The evaluation input a sign-in needs
 * in order to unwrap the account's keys is merged on top by
 * `webauthn-ceremony.service.ts`, where the derivation that consumes the output
 * lives — so a secret is derived only where something is about to use it, and a
 * caller that wants an assertion without deriving anything still has one. The
 * day a second caller needs the input, it merges it the same way; this function
 * stays a translation.
 *
 * Throws whatever `decodeBase64Url` throws, for the reason
 * {@link toCreationOptions} does.
 */
export function toRequestOptions(
  options: PasskeyRequestOptionsJson,
): PublicKeyCredentialRequestOptions {
  return {
    challenge: decodedBytes(options.challenge),
    rpId: options.rpId,
    timeout: options.timeout,
    // `required` is what makes the assertion a second factor rather than a
    // possession check. Downgraded to `preferred` — by a default invented here,
    // or by a member left off — every device that can skip the biometric does,
    // and nothing anywhere reports it.
    userVerification: serverWord<UserVerificationRequirement>(
      options.userVerification,
    ),
  };
}

/**
 * Which of two things the payload's `prf.enabled` is a report of.
 *
 * `'as-reported'` is the creation response's own word, verbatim. `'derived'` is
 * the caller's statement that it holds a PRF output for this credential — by
 * whichever route it obtained one.
 */
export type PasskeyPrfClaim = 'as-reported' | 'derived';

/**
 * Builds the registration payload out of what the authenticator produced.
 *
 * The two binary members are re-encoded as unpadded base64url, byte for byte:
 * the client data is what the attestation signature is over, so a member that
 * re-encodes to the right *shape* and the wrong bytes fails on the server with
 * nothing naming the cause, and `PasskeyEncoding.TryDecode` refuses padding
 * outright.
 *
 * The extension results are **projected and never passed through** — see
 * {@link prfResults}, which is where the one line that would leak every account
 * key in the product would otherwise be written.
 *
 * **`claim` says what the payload's `prf.enabled` is a report of, and it exists
 * because `create()`'s own word is not the answer to the question the server
 * asks.** `'derived'` asserts one thing: the caller obtained a PRF output for
 * this credential — from the creation response itself, or from an assertion it
 * ran locally against the credential it just made. The server's gate is about
 * the account's keys being derivable, and an output in hand is exactly that
 * evidence; `create()`'s `enabled` is a different, weaker sentence about what
 * the authenticator was willing to say at enrolment time.
 *
 * **Only the ceremony can make that claim, which is why it is a parameter and
 * not a rule here.** This module sees one response and never learns whether a
 * second call followed it. The ceremony is the layer that knows which route
 * derived, so it is the layer that gets to say so; reading it off the response
 * here would be this function guessing at a fact it has no access to.
 *
 * **Reporting `create()`'s word alone turned working devices away.** Many
 * platform authenticators derive only from the *first* assertion and answer a
 * registration with `enabled: true` and no output — or with no `prf` member at
 * all. The ceremony handles that: it runs one local, discarded `get()` and comes
 * back holding the output. But a payload built from the creation results reports
 * `null` for exactly those devices, and the server answers 400. By then the
 * account's keys are sealed into twenty-two envelopes and ten recovery codes are
 * on screen, so the person is told to throw away codes that were never live —
 * and every retry ends the same way, because the device's answer never changes.
 *
 * **The default is `'as-reported'` and is not a convenience.** A caller holding
 * no output must not be able to claim one by leaving an argument off, and every
 * existing call site means the creation response's word.
 *
 * `'derived'` still builds the member from nothing rather than editing the
 * results — the projection rule at {@link prfResults} is the reason this is a
 * literal. Copying the object and overwriting `enabled` would carry
 * `prf.results.first` to the server, which is the leak that argument exists to
 * prevent.
 *
 * Throws on a response that is not a registration response.
 */
export function toRegistrationPayload(
  credential: PublicKeyCredential,
  claim: PasskeyPrfClaim = 'as-reported',
): PasskeyRegistrationPayload {
  const response = credential.response;

  // Narrowed by the members it carries, never by `instanceof
  // AuthenticatorAttestationResponse`: the test runner implements no WebAuthn at
  // all, so that class is not a value there and the check would be a
  // `ReferenceError` rather than a failed check.
  if (!('attestationObject' in response)) {
    throw new Error(
      'Not a registration response: it carries no attestation object.',
    );
  }

  return {
    clientDataJson: encodeBase64Url(bytesOf(response.clientDataJSON)),
    attestationObject: encodeBase64Url(bytesOf(response.attestationObject)),
    clientExtensionResults: {
      prf:
        claim === 'derived'
          ? { enabled: true }
          : prfResults(credential.getClientExtensionResults()),
    },
  };
}

/**
 * Builds the assertion payload out of what the authenticator signed.
 *
 * The credential id is read from `rawId` rather than from `id`: they are the
 * same bytes in the same encoding today, and `id` is whatever the browser chose
 * to render them as while `rawId` is the bytes themselves.
 *
 * Throws on a response that is not an assertion response.
 */
export function toAssertionPayload(
  credential: PublicKeyCredential,
): PasskeyAssertionPayload {
  const response = credential.response;

  if (!('authenticatorData' in response)) {
    throw new Error(
      'Not an assertion response: it carries no authenticator data.',
    );
  }

  if (!('signature' in response)) {
    throw new Error('Not an assertion response: it carries no signature.');
  }

  const userHandle = 'userHandle' in response ? response.userHandle : null;

  return {
    credentialId: encodeBase64Url(bytesOf(credential.rawId)),
    clientDataJson: encodeBase64Url(bytesOf(response.clientDataJSON)),
    authenticatorData: encodeBase64Url(bytesOf(response.authenticatorData)),
    signature: encodeBase64Url(bytesOf(response.signature)),
    // Absent and empty are different values, and the difference is the server's
    // rule rather than a nicety: a present handle has to name the account the
    // credential belongs to, and an empty string is a *present* handle of zero
    // bytes. Encoding whatever was there would send one.
    userHandle:
      userHandle === null || userHandle === undefined
        ? null
        : encodeBase64Url(bytesOf(userHandle)),
  };
}

// **The projection, and the reason this module has one.**
//
// `getClientExtensionResults()` carries `prf.enabled` and, on a great many
// authenticators, `prf.results.first` — which *is* the PRF output, the value the
// account's key-encryption key is derived from and therefore the value that
// unwraps the account's whole keyspace. Re-encoding that object and sending it
// would hand the operator every account key in the product, in a request that
// would look perfectly ordinary in a log, in a proxy and in a review.
//
// So the results are not passed through, not filtered and not deleted from: one
// boolean is read out and a new object is built. A `delete results.prf.results`
// or a spread with the member omitted both start from the value they are trying
// to be rid of, and the next member an authenticator invents arrives inside
// them. This starts from nothing and adds the one member the server declared.
//
// `enabled` is reported exactly as the client reported it. Inventing `false`
// would be a claim the client did not make, and inventing `true` would turn a
// device that cannot hold the account's keys into one the server accepted.
function prfResults(
  results: AuthenticationExtensionsClientOutputs,
): PasskeyPrfResultsJson | null {
  const prf = results.prf;

  if (prf === undefined) {
    return null;
  }

  return { enabled: prf.enabled ?? null };
}

// WebAuthn declares several of these members as closed sets of words, and JSON
// carries only strings. The crossing needs one of two things: an allow-list
// here, or the server's word taken as the server's word. This is the second,
// deliberately.
//
// Declaring the member as the union instead of as `string` does not avoid the
// question, it hides it: what arrives is whatever the server sent, so a union on
// the boundary is the same assertion written where nothing checks it — and one
// that reads as a fact about the response rather than as a decision somebody
// made.
//
// An allow-list here would be a *third* opinion about a value the server decides
// and the browser reads, and it would be written against `lib.dom.d.ts`, which
// trails the specification. The day the server asks for an attestation
// conveyance the published IDL has carried for a year and this TypeScript has
// not, a list written here refuses a ceremony both other layers understand, from
// the layer with the least authority over the question.
//
// **The cost of the pass-through is real, and it is not the one a reader
// expects.** These members are `DOMString` in WebAuthn's own IDL — deliberately,
// not by omission — and a client platform is *required* to ignore a value it
// does not recognise and fall back to the default. So a wrong word here does not
// fail by name in front of the person who started the ceremony; it disappears.
// `userVerification` is the member where that matters: ignored, it defaults to
// `preferred`, and every device that can skip the biometric does.
//
// What catches that is the server, over bytes the authenticator signed rather
// than over a word it was handed: `PasskeyAssertionVerifier` and
// `PasskeyRegistrationVerifier` both refuse authenticator data whose
// user-verified flag is clear. That is the layer holding the evidence, which is
// why this one stays a translation — a check here could only restate the
// server's own constant back at it, and would buy error quality rather than
// enforcement.
function serverWord<TWord extends string>(word: string): TWord {
  return word as TWord;
}

// Decoded by the one strict decoder, then copied onto a buffer WebCrypto and
// WebAuthn will accept: `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either. The copy
// is the point rather than an accident of it — `key-envelope.ts`'s
// `overOwnBuffer` makes the whole argument, and it is not restated here.
function decodedBytes(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(decodeBase64Url(text));
}

// Bytes out of a member the DOM types as an `ArrayBuffer` and a runtime hands
// back as whatever its own realm built. Refused rather than coerced: a member
// that is not bytes at all would otherwise be encoded through some
// stringification into a value of exactly the right shape and no meaning, on a
// request whose signature check is the only thing that would notice.
function bytesOf(value: unknown): Uint8Array {
  if (isArrayBuffer(value)) {
    return new Uint8Array(value);
  }

  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  }

  throw new Error(
    'The authenticator response carried a member that is not bytes.',
  );
}

/**
 * Whether `value` is an `ArrayBuffer`, asked in the one way that survives a
 * realm boundary.
 *
 * **`value instanceof ArrayBuffer` is what a reader writes first, and it is
 * wrong here in the quiet direction.** `instanceof` walks the prototype chain
 * looking for *this* realm's `ArrayBuffer.prototype`, so a buffer built in
 * another one answers `false` while nothing about it looks wrong: it renders as
 * `[object ArrayBuffer]` and its constructor is named `ArrayBuffer`. Realms are
 * crossed in ordinary places — an iframe, a worker, and this test runner between
 * two modules it loaded separately.
 *
 * The tag read here is one every realm's `ArrayBuffer.prototype` carries, so it
 * answers the same whichever realm built the buffer, and it still excludes a
 * `SharedArrayBuffer` — which renders as `[object SharedArrayBuffer]` and is not
 * something an authenticator response can hold.
 *
 * `ArrayBuffer.isView` beside it needs none of this care and is deliberately
 * left as it is: it is defined on the view's own internal slot rather than on
 * its prototype, and already answers for a typed array from any realm.
 *
 * **The two callers fail differently, and the second one is why this is
 * exported rather than copied.** Here, a `false` on genuine bytes reaches
 * {@link bytesOf}'s refusal, which reports "the response carried a member that
 * is not bytes" — a sentence naming the wrong thing entirely about a
 * registration that was perfectly good, but a sentence. In
 * `webauthn-ceremony.service.ts` the same `false` sends the PRF output down the
 * view branch, where `.buffer`, `.byteOffset` and `.byteLength` are all
 * `undefined` on an `ArrayBuffer`: the result is an **empty** array and nothing
 * throws, the account's key-encryption key is derived from zero bytes
 * identically on every device, and the `fill(0)` that is meant to clear the
 * secret clears the empty view while the platform's buffer keeps it.
 *
 * That is exactly the point where a second copy is worst. A copy is not held by
 * anything: revert *this* definition to `instanceof` and twelve specs redden;
 * revert a copy in the service and nothing does, because that spec and that
 * module happen to share a realm under this runner. So the copy would be
 * decoration guarding the most expensive failure in the product. One definition
 * is one thing tests can hold, which is the whole reason it is exported.
 */
export function isArrayBuffer(value: unknown): value is ArrayBuffer {
  return Object.prototype.toString.call(value) === '[object ArrayBuffer]';
}
