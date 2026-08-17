// The translation layer between the API's JSON and the shapes WebAuthn's own
// calls take. Every member that is bytes on the wire is base64url text in JSON
// and a `BufferSource` in the browser, in both directions, and this module is
// the one place that crossing happens.
//
// Three of the rules below are silent when broken, which is why they are pinned
// here rather than left to a caller:
//
//   * **The PRF evaluation input is the client's to supply.** The server sends
//     `{"prf":{}}` — a capability question with no input — so if this module
//     does not merge `eval.first`, the ceremony completes, the authenticator
//     reports `prf.enabled: true`, the registration is accepted, and no key is
//     ever derived. If it merges the *wrong* input, every account that wrapped
//     its keys under the old value is locked out by a passkey that goes on
//     authenticating perfectly and simply hands back different bytes.
//
//   * **There is no `allowCredentials` on the request options.** Sending one
//     would mean the server first decided which credentials belong to the person
//     signing in, and an endpoint that answers "here are that account's
//     credentials" for one address and nothing for another is an
//     account-enumeration oracle. `PasskeyRequestOptions.cs` argues it at
//     length; the assertion here is that the absence survives translation.
//
//   * **The decoder refuses and never repairs.** `decodeBase64Url` is strict on
//     purpose — padding, `+`, `/` and a final group no encoder would emit are
//     each rejected — and the reason is that the peer reading these bytes is a
//     different decoder in a different language written to reject exactly what a
//     lenient reading here would wave through. So the refusal has to *propagate*
//     out of this module, not be caught and softened; and no second decoder may
//     appear beside it, which the surface pin at the bottom is what watches.
//
// The credentials below are plain objects rather than real `PublicKeyCredential`
// instances: jsdom implements none of WebAuthn, so there is no class to be an
// instance of. That is a constraint on the implementation as much as on this
// spec — a narrowing written as `response instanceof AuthenticatorAttestationResponse`
// would be a `ReferenceError` under the runner, not a failed check.
import { describe, expect, it } from 'vitest';

import { PASSKEY_PRF_EVAL_INPUT } from './account-keys';
import { decodeBase64Url, encodeBase64Url } from './base64url';
import {
  toAssertionPayload,
  toCreationOptions,
  toRegistrationPayload,
  toRequestOptions,
} from './webauthn-encoding';
import * as webauthnEncoding from './webauthn-encoding';

const utf8 = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

// Reads a `BufferSource` back as bytes without caring which of the two shapes it
// is. The implementation may hand back either — both are `BufferSource` and both
// are what `navigator.credentials.create()` accepts — so an assertion that
// insisted on one would be pinning a detail nobody decided.
//
// An absent value is refused in a sentence rather than cast away: every member
// this is pointed at is optional in the DOM's own types, and a member that is
// missing is the failure these tests are here to catch.
function bytesOf(source: BufferSource | undefined): Uint8Array {
  if (source === undefined) {
    throw new Error('the member under test carried no bytes at all');
  }

  return source instanceof ArrayBuffer
    ? new Uint8Array(source)
    : new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
}

// An `ArrayBuffer` of its own, which is what a real authenticator response
// carries. Handing the module a `Uint8Array` here would let an implementation
// that forgets to view the buffer pass by accident.
function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.slice().buffer;
}

// The first three bytes encode to `-_--`: every character standard base64 would
// have spelled `+` or `/`. A decoder that quietly accepted the standard alphabet
// would still round-trip these, which is why the refusals below test the other
// direction as well.
const CHALLENGE_BYTES = Uint8Array.from([
  0xfb, 0xff, 0xbe, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
  0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
  0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c,
]);

// Sixteen bytes, because the server sends the account's own id and nothing else
// — see `PasskeyUser`, which refuses to mint a second handle.
const USER_HANDLE_BYTES = Uint8Array.from([
  0x9a, 0x0b, 0x1c, 0x2d, 0x3e, 0x4f, 0x50, 0x61, 0x72, 0x83, 0x94, 0xa5, 0xb6,
  0xc7, 0xd8, 0xe9,
]);

const EXCLUDED_ONE_BYTES = Uint8Array.from([
  0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
]);

const EXCLUDED_TWO_BYTES = Uint8Array.from([
  0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9,
]);

// What `POST /api/passkeys/registration/options` answers with, member for member
// — `PasskeyCreationOptions.cs`, camel-cased by the API's serializer. `prf` is
// the empty object the server sends: the capability question, with no input.
const SERVER_CREATION_OPTIONS = {
  challenge: encodeBase64Url(CHALLENGE_BYTES),
  rp: { id: 'budgetoid.app', name: 'Budgetoid' },
  user: {
    id: encodeBase64Url(USER_HANDLE_BYTES),
    name: 'someone@example.test',
    displayName: 'someone@example.test',
  },
  pubKeyCredParams: [
    { type: 'public-key', alg: -7 },
    { type: 'public-key', alg: -257 },
  ],
  timeout: 120000,
  attestation: 'none',
  authenticatorSelection: {
    residentKey: 'required',
    requireResidentKey: true,
    userVerification: 'required',
  },
  excludeCredentials: [
    { type: 'public-key', id: encodeBase64Url(EXCLUDED_ONE_BYTES) },
    { type: 'public-key', id: encodeBase64Url(EXCLUDED_TWO_BYTES) },
  ],
  extensions: { prf: {} },
};

// What `POST /api/passkeys/assertion/options` answers with —
// `PasskeyRequestOptions.cs`, which has four members and deliberately not a
// fifth.
const SERVER_REQUEST_OPTIONS = {
  challenge: encodeBase64Url(CHALLENGE_BYTES),
  rpId: 'budgetoid.app',
  timeout: 120000,
  userVerification: 'required',
};

// Thirty-two bytes, so its base64url would carry one `=` if anything padded it.
const CLIENT_DATA_BYTES = utf8.encode('{"type":"webauthn.create","x":1}');

// Four bytes, so its base64url would carry two. Between them the two lengths
// cover both padded cases; a third length would prove nothing new.
const ATTESTATION_BYTES = Uint8Array.from([0xa1, 0x63, 0x66, 0x6d]);

const CREDENTIAL_ID_BYTES = Uint8Array.from([
  0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22, 0x33, 0x44,
]);

// What a browser puts in `id`: its own **rendering** of the bytes in `rawId`,
// spelled here with the standard base64 alphabet — one character away from the
// base64url the payload must carry, because the bytes above were chosen to
// encode a `-`.
//
// The divergence is the whole point of the constant. Rendered identically, the
// two members are interchangeable in this fixture and a payload built from
// either one passes, so "the credential id comes from `rawId`" is stated in a
// comment and tested by nothing. Every browser shipping today renders `id` as
// base64url and this fixture is therefore not a browser — which is the honest
// position: a fixture that could only be wrong on a platform that already
// exists is a fixture that finds the bug after somebody ships it.
const RENDERED_CREDENTIAL_ID = encodeBase64Url(CREDENTIAL_ID_BYTES)
  .replaceAll('-', '+')
  .replaceAll('_', '/');

const AUTHENTICATOR_DATA_BYTES = Uint8Array.from([
  0x49, 0x96, 0x0d, 0xe5, 0x88, 0x0e, 0x8c, 0x68, 0x74, 0x34,
]);

const SIGNATURE_BYTES = Uint8Array.from([0x30, 0x45, 0x02, 0x20, 0x7f]);

const USER_HANDLE_RESPONSE_BYTES = USER_HANDLE_BYTES;

// The PRF output as an authenticator hands it back. It appears in this file for
// exactly one reason: to be looked for in a payload it must never reach.
const PRF_OUTPUT_BYTES = Uint8Array.from([
  0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c,
  0x4d, 0x4e, 0x4f, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
  0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0x5f,
]);

function registrationCredential(
  extensionResults: AuthenticationExtensionsClientOutputs,
): PublicKeyCredential {
  return {
    id: encodeBase64Url(CREDENTIAL_ID_BYTES),
    rawId: toArrayBuffer(CREDENTIAL_ID_BYTES),
    type: 'public-key',
    authenticatorAttachment: 'platform',
    response: {
      clientDataJSON: toArrayBuffer(CLIENT_DATA_BYTES),
      attestationObject: toArrayBuffer(ATTESTATION_BYTES),
    },
    getClientExtensionResults: (): AuthenticationExtensionsClientOutputs =>
      extensionResults,
  } as unknown as PublicKeyCredential;
}

function assertionCredential(
  userHandle: ArrayBuffer | null,
): PublicKeyCredential {
  return {
    id: RENDERED_CREDENTIAL_ID,
    rawId: toArrayBuffer(CREDENTIAL_ID_BYTES),
    type: 'public-key',
    authenticatorAttachment: 'platform',
    response: {
      clientDataJSON: toArrayBuffer(CLIENT_DATA_BYTES),
      authenticatorData: toArrayBuffer(AUTHENTICATOR_DATA_BYTES),
      signature: toArrayBuffer(SIGNATURE_BYTES),
      userHandle,
    },
    getClientExtensionResults:
      (): AuthenticationExtensionsClientOutputs => ({}),
  } as unknown as PublicKeyCredential;
}

describe('the creation options handed to navigator.credentials.create', () => {
  it('asks the authenticator to evaluate the account’s own PRF input', () => {
    // Arrange
    // The server sends `{"prf":{}}` and nothing more — a question about the
    // credential's capability, carrying no value to evaluate. The input is the
    // client's to supply, so if this merge is missing the whole ceremony still
    // succeeds: the authenticator answers `enabled: true`, the server accepts
    // the registration, and there is no PRF output to derive a key from. If the
    // merge supplies a *different* string, it is worse than missing — every
    // account whose keys were wrapped under the old value is locked out by a
    // passkey that still authenticates and simply returns different bytes.
    expect(SERVER_CREATION_OPTIONS.extensions.prf).toEqual({});

    // Act
    const created = toCreationOptions(SERVER_CREATION_OPTIONS);

    // Assert
    // Read off `account-keys.ts` rather than typed again here. A literal in this
    // file would agree with a literal in the module for exactly as long as
    // nobody edited one of them, and the failure that follows is silent.
    const first = created.extensions?.prf?.eval?.first;
    expect(first).toBeDefined();
    expect(toHex(bytesOf(first))).toBe(
      toHex(utf8.encode(PASSKEY_PRF_EVAL_INPUT)),
    );
  });

  it('decodes the challenge, the user handle and every excluded credential', () => {
    // Arrange, Act
    const created = toCreationOptions(SERVER_CREATION_OPTIONS);

    // Assert
    // Every one of these is base64url text in JSON and bytes in the browser. A
    // member left as a string does not fail loudly in every case: the browser
    // will happily take a string where a `BufferSource` was declared and hash
    // its UTF-16 code units, which produces a challenge that verifies against
    // nothing and an excluded-credential list that excludes nothing.
    expect(toHex(bytesOf(created.challenge))).toBe(toHex(CHALLENGE_BYTES));
    expect(toHex(bytesOf(created.user.id))).toBe(toHex(USER_HANDLE_BYTES));

    // The URL-safe alphabet is really being read as such: the first three bytes
    // of the challenge spell `-_--`, which a decoder wired to the standard
    // alphabet would refuse and a lenient one would misread.
    expect(SERVER_CREATION_OPTIONS.challenge.startsWith('-_--')).toBe(true);

    // `excludeCredentials` is what makes an authenticator that already holds a
    // credential for this account decline instead of enrolling a second one —
    // the `InvalidStateError` the ceremony reports as a duplicate. Dropped or
    // left as text, the second enrolment succeeds and the account acquires two
    // factors where the person made one.
    const excluded = created.excludeCredentials ?? [];
    expect(excluded).toHaveLength(2);
    expect(excluded.map((descriptor) => descriptor.type)).toEqual([
      'public-key',
      'public-key',
    ]);
    expect(excluded.map((descriptor) => toHex(bytesOf(descriptor.id)))).toEqual(
      [toHex(EXCLUDED_ONE_BYTES), toHex(EXCLUDED_TWO_BYTES)],
    );
  });

  it('carries the relying party, the algorithms and the selection through unchanged', () => {
    // Arrange, Act
    const created = toCreationOptions(SERVER_CREATION_OPTIONS);

    // Assert
    // These are the server's decisions, and this module has no opinion about any
    // of them. `rp.id` in particular is the value hashed into every credential an
    // authenticator stores — a client that "helpfully" defaulted it to
    // `location.hostname` would mint passkeys under a name no later ceremony can
    // reproduce, with no migration back.
    expect(created.rp).toEqual({ id: 'budgetoid.app', name: 'Budgetoid' });
    expect(created.pubKeyCredParams).toEqual([
      { type: 'public-key', alg: -7 },
      { type: 'public-key', alg: -257 },
    ]);
    expect(created.timeout).toBe(120000);
    expect(created.attestation).toBe('none');
    expect(created.authenticatorSelection).toEqual({
      residentKey: 'required',
      requireResidentKey: true,
      userVerification: 'required',
    });

    // The user's own two text members ride along beside the decoded handle,
    // because an authenticator lists the account under them.
    expect(created.user.name).toBe('someone@example.test');
    expect(created.user.displayName).toBe('someone@example.test');
  });

  it('lets the strict decoder’s refusal of a padded challenge out', () => {
    // Arrange
    // Padding is the repair a lenient decoder makes first and the one that hides
    // best: `AAAA=` is not something the server's encoder can emit, so a client
    // that accepts it is accepting a value some other layer mangled. The refusal
    // has to leave this module — caught and softened into a default challenge,
    // it becomes a ceremony bound to bytes nobody chose.
    const padded = { ...SERVER_CREATION_OPTIONS, challenge: 'AAAA=' };

    // Act, Assert
    expect(() => toCreationOptions(padded)).toThrow();
  });

  it('lets the refusal of a standard-alphabet challenge out', () => {
    // Arrange
    // `+` and `/` are "the same bits" and are refused anyway, for the reason
    // `base64url.ts` states: the peer is a different decoder in a different
    // language, written to reject what a lenient reading here would wave through.
    const standardAlphabet = {
      ...SERVER_CREATION_OPTIONS,
      challenge: 'a+b/cd',
    };

    // Act, Assert
    expect(() => toCreationOptions(standardAlphabet)).toThrow();
  });

  it('lets the refusal of a malformed user handle or excluded id out', () => {
    // Arrange
    // The same rule on the members a reader is likelier to translate in a
    // hurry. A user handle silently repaired is 15 bytes where the account's id
    // is 16, and an excluded credential silently repaired excludes a credential
    // that does not exist.
    const badUser = {
      ...SERVER_CREATION_OPTIONS,
      user: { ...SERVER_CREATION_OPTIONS.user, id: 'not base64url!' },
    };
    const badExclusion = {
      ...SERVER_CREATION_OPTIONS,
      excludeCredentials: [{ type: 'public-key', id: 'AAAA=' }],
    };

    // Act, Assert
    expect(() => toCreationOptions(badUser)).toThrow();
    expect(() => toCreationOptions(badExclusion)).toThrow();
  });
});

describe('the request options handed to navigator.credentials.get', () => {
  it('carries no allowCredentials', () => {
    // Arrange, Act
    const request = toRequestOptions(SERVER_REQUEST_OPTIONS);

    // Assert
    // The **absence** is the assertion, and it is not a tidiness point. A
    // populated `allowCredentials` means the server first decided which
    // credentials belong to the person signing in, which means the request had to
    // name them — and an endpoint answering "here are that account's
    // credentials" for one address and nothing for another is an
    // account-enumeration oracle. `PasskeyRequestOptions.cs` therefore has no
    // such member at all, so the only way one can appear is if this module
    // invents it: an empty array "for completeness", or a list built from
    // something the client happens to know. Both are caught here, which is why
    // the property is checked for existence and not merely for emptiness — `[]`
    // is falsy-looking and is still the wrong shape to start growing entries in.
    expect(Object.hasOwn(request, 'allowCredentials')).toBe(false);
    expect(request.allowCredentials).toBeUndefined();
  });

  it('decodes the challenge and carries the relying party, timeout and verification', () => {
    // Arrange, Act
    const request = toRequestOptions(SERVER_REQUEST_OPTIONS);

    // Assert
    expect(toHex(bytesOf(request.challenge))).toBe(toHex(CHALLENGE_BYTES));
    expect(request.rpId).toBe('budgetoid.app');
    expect(request.timeout).toBe(120000);
    // `required` is what makes the assertion a second factor rather than a
    // possession check. Downgraded to `preferred`, every device that can skip
    // the biometric does, and nothing anywhere reports it.
    expect(request.userVerification).toBe('required');
  });

  it('lets the strict decoder’s refusal of a malformed challenge out', () => {
    // Arrange
    const padded = { ...SERVER_REQUEST_OPTIONS, challenge: 'AAAA=' };

    // Act, Assert
    expect(() => toRequestOptions(padded)).toThrow();
  });
});

describe('the registration payload sent to the server', () => {
  it('never carries the PRF output the authenticator returned', () => {
    // Arrange
    // The single most expensive mistake this module could make. A registration
    // response's extension results carry `enabled` **and**, on some
    // authenticators, `results.first` — the very bytes the account's
    // key-encryption key is derived from. Copied through into the request body,
    // the operator receives the key that unwraps the account's whole keyspace,
    // and every guarantee the client-side derivation exists for is gone. The
    // server wants `enabled` and only `enabled`: see
    // `PasskeyClientExtensionResults`, which declares one nullable member.
    const credential = registrationCredential({
      prf: {
        enabled: true,
        results: { first: toArrayBuffer(PRF_OUTPUT_BYTES) },
      },
    });

    // Act
    const payload = toRegistrationPayload(credential);

    // Assert
    const prf = payload.clientExtensionResults?.prf ?? null;
    expect(prf).toEqual({ enabled: true });
    expect(Object.hasOwn(prf ?? {}, 'results')).toBe(false);

    // And the bytes are nowhere else in the body either — not under a member
    // somebody added beside the extension results, and not base64url'd into one
    // of the two binary members by a copy-paste.
    const wire = JSON.stringify(payload);
    expect(wire).not.toContain(encodeBase64Url(PRF_OUTPUT_BYTES));
    expect(wire).not.toContain(toHex(PRF_OUTPUT_BYTES));
  });

  it('re-encodes the client data and the attestation as unpadded base64url', () => {
    // Arrange
    // Both lengths are chosen so that a padding encoder would show: 32 bytes
    // pads with one `=`, 4 bytes with two. `PasskeyEncoding.TryDecode` on the
    // server accepts the unpadded form and refuses padding, so a padded member
    // here is a 400 on every registration — loud, but only in production,
    // because nothing else in this client would notice.
    const credential = registrationCredential({ prf: { enabled: true } });

    // Act
    const payload = toRegistrationPayload(credential);

    // Assert
    expect(payload.clientDataJson).toBe(encodeBase64Url(CLIENT_DATA_BYTES));
    expect(payload.attestationObject).toBe(encodeBase64Url(ATTESTATION_BYTES));
    expect(payload.clientDataJson).not.toContain('=');
    expect(payload.attestationObject).not.toContain('=');

    // Byte-exact, not merely well-formed. The client data is what the signature
    // is over, so a member that re-encodes to the right *shape* and the wrong
    // bytes fails verification on the server with nothing naming the cause.
    expect(toHex(decodeBase64Url(payload.clientDataJson))).toBe(
      toHex(CLIENT_DATA_BYTES),
    );
    expect(toHex(decodeBase64Url(payload.attestationObject))).toBe(
      toHex(ATTESTATION_BYTES),
    );
  });

  it('reports no prf result when the client reported none', () => {
    // Arrange
    // An authenticator that answered nothing about the extension. The server
    // refuses this registration, and that refusal is the product rule
    // `passkeys.md` argues for — but it has to be reached honestly. Inventing
    // `enabled: false` would be a claim the client did not make, and inventing
    // `enabled: true` would turn a device that cannot hold the account's keys
    // into one the server accepted.
    const credential = registrationCredential({});

    // Act
    const payload = toRegistrationPayload(credential);

    // Assert
    expect(payload.clientExtensionResults?.prf ?? null).toBeNull();
  });
});

describe('the assertion payload sent to the server', () => {
  it('re-encodes every binary member as unpadded base64url', () => {
    // Arrange
    const credential = assertionCredential(
      toArrayBuffer(USER_HANDLE_RESPONSE_BYTES),
    );

    // Act
    const payload = toAssertionPayload(credential);

    // Assert
    // Five members, four of which the signature check on the server depends on
    // byte for byte. The credential id comes from `rawId` rather than from `id`:
    // they are the same bytes in the same encoding on every browser shipping
    // today, and `id` is whatever the browser chose to *render* them as, while
    // `rawId` is the bytes themselves.
    //
    // That distinction is tested and not merely asserted: this credential's
    // `id` spells the same bytes in the standard alphabet, so a payload built
    // from `id` — the member a reader reaches for first, because it is already
    // a string — comes back one character different instead of identical. The
    // first expectation guards the fixture, so the day those bytes are edited
    // into something that renders the same either way, this test says so
    // instead of quietly stopping.
    expect(RENDERED_CREDENTIAL_ID).not.toBe(
      encodeBase64Url(CREDENTIAL_ID_BYTES),
    );
    expect(payload.credentialId).not.toBe(RENDERED_CREDENTIAL_ID);

    expect(payload.credentialId).toBe(encodeBase64Url(CREDENTIAL_ID_BYTES));
    expect(payload.clientDataJson).toBe(encodeBase64Url(CLIENT_DATA_BYTES));
    expect(payload.authenticatorData).toBe(
      encodeBase64Url(AUTHENTICATOR_DATA_BYTES),
    );
    expect(payload.signature).toBe(encodeBase64Url(SIGNATURE_BYTES));
    expect(payload.userHandle).toBe(
      encodeBase64Url(USER_HANDLE_RESPONSE_BYTES),
    );

    for (const member of Object.values(payload)) {
      expect(String(member)).not.toContain('=');
    }
  });

  it('omits the user handle when the authenticator returned none', () => {
    // Arrange
    // A conforming authenticator may omit it, which is why
    // `CompleteAssertionCommand.UserHandle` is nullable. The failure mode worth
    // guarding is not the omission but the substitute: an empty string is a
    // *present* handle of zero bytes on the wire, and the server's rule is that
    // a present handle has to name the account the credential belongs to. So
    // "absent" and "empty" must not be the same value here.
    const credential = assertionCredential(null);

    // Act
    const payload = toAssertionPayload(credential);

    // Assert
    expect(payload.userHandle ?? null).toBeNull();
    expect(payload.userHandle).not.toBe('');
  });
});

describe('the module surface', () => {
  it('exports these five functions and no others', () => {
    // Arrange
    // The pin exists for one hazard in particular: a second base64url decoder
    // appearing beside `base64url.ts`'s. The strict one refuses padding, the
    // standard alphabet and a final group no encoder emits — and a lenient
    // second copy, written in a hurry because "the browser wants bytes", accepts
    // key material the server's own decoder rejects. The symptom never arrives
    // on the malformed input; it arrives months later as an envelope that will
    // not open. A new exported name here is a red test and a conversation
    // instead of a diff nobody read.
    //
    // **`isArrayBuffer` is the conversation, and its answer is written down
    // here rather than in the commit that moved this number.** A fifth export
    // is not, on its own, the risk: what the pin watches for is a *duplicate
    // reading of bytes* appearing beside the one the rest of the system agrees
    // with, and this export is the opposite move. The predicate existed twice —
    // once here and once in `webauthn-ceremony.service.ts` — and only this copy
    // was held by anything: reverting this definition to `instanceof
    // ArrayBuffer` reddens twelve specs, while reverting the twin reddened
    // nothing, because that spec and that module share a realm under this
    // runner. Exporting it deletes the unheld copy and puts the more expensive
    // call site — the one where a wrong answer derives the account's
    // key-encryption key from zero bytes, silently — behind the definition
    // these tests do hold. The count went up because the number of readings
    // went down.
    //
    // So the question the next name has to answer is not "is five allowed" but
    // the one this list was always asking: does this name add a second way to
    // read the same bytes, or does it remove one?
    const expected = [
      'isArrayBuffer',
      'toAssertionPayload',
      'toCreationOptions',
      'toRegistrationPayload',
      'toRequestOptions',
    ].sort();

    // Act
    const exported = Object.entries(webauthnEncoding)
      .filter(([, value]) => typeof value === 'function')
      .map(([name]) => name)
      .sort();

    // Assert
    expect(exported).toEqual(expected);
  });
});
