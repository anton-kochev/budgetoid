// The one seam that touches `navigator.credentials`, and an injectable for the
// reason `file-download.service.ts` is one: the browser APIs behind it do not
// exist under the test runner, so confining them to a class lets everything
// above stub the service instead of the platform. Below the seam, nothing is
// stubbed — `crypto.subtle` is real here, so the key derivation these tests
// observe is the derivation that ships.
//
// **The PRF output never crosses this module's boundary.** It is the value the
// account's key-encryption key is derived from, and therefore the value that
// unwraps the account's whole keyspace; what comes back out is a non-extractable
// `CryptoKey` and never bytes, and the raw output is zero-filled once the key is
// derived. `account-keys.ts` argues the shape of that at length. The assertions
// here are the enforcement: the surface is walked for the bytes, the key is
// checked for a way out, and the buffer the ceremony was handed is checked for
// having been cleared.
//
// **The PRF output is obtained by two routes and refused only after both.**
// `create()` is called with `eval.first` set; many platform authenticators
// answer `enabled: true` and return no output until the first *assertion*, so a
// client that gave up after creation would turn away capable devices. When no
// output arrives at creation, one **local** assertion is run against the
// credential just created, with `evalByCredential` keyed on it, and that
// assertion is discarded — it exists to make the authenticator derive, and
// nothing about it is ever sent anywhere. Only when neither route produces
// output is `no-prf` raised.
//
// **Nothing here reaches the network, and that is asserted rather than argued
// from the service's dependency list.** The dependency claim cannot be written
// as a test: `HttpClient` is `providedIn: 'root'`, so `TestBed.inject` resolves
// one whether or not this service ever asked for it, and an expectation that
// the injection throws can never fire. What *is* checkable is the thing the
// dependency rule exists for — that the leg holding the PRF output sends
// nothing anywhere — and it is the wider claim of the two, because it also
// catches a bare `fetch()` no injector was ever asked about. The instrument
// carries a negative control: a test that walks up to each watched door and
// fails if the watch missed it.
//
// jsdom implements no WebAuthn at all, so the platform is faked: a
// `PublicKeyCredential` class, a credentials container, and a secure context.
// The class is real enough to be the target of an `instanceof`, which is the one
// narrowing an implementation could reasonably write and a plain object would
// silently fail.
import { TestBed } from '@angular/core/testing';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';

import {
  PASSKEY_PRF_EVAL_INPUT,
  keyEncryptionKeyFromPasskey,
} from './account-keys';
import { encodeBase64Url } from './base64url';
import { sealEnvelope } from './key-envelope';
import { WebauthnCeremonyService } from './webauthn-ceremony.service';

const utf8 = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(
    '',
  );
}

// Reads a `BufferSource` back as bytes, and refuses an absent one in a sentence.
// Every member this is pointed at is optional in the DOM's own types, so the
// alternative is a cast that asserts something nothing checked — and a missing
// PRF input or a missing credential descriptor is exactly the failure these
// tests exist to catch, not a detail to assert away.
function bytesOf(source: BufferSource | undefined): Uint8Array {
  if (source === undefined) {
    throw new Error('the member under test carried no bytes at all');
  }

  return source instanceof ArrayBuffer
    ? new Uint8Array(source)
    : new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.slice().buffer;
}

// The buffer a `BufferSource` is a window onto, and a sentence for anything
// else. Identity is the whole point of the callers below, so this returns the
// buffer itself rather than a reading of its contents: two buffers holding the
// same bytes are exactly the case being told apart.
function bufferBehind(source: unknown): ArrayBufferLike {
  if (source instanceof ArrayBuffer) {
    return source;
  }

  if (ArrayBuffer.isView(source)) {
    return source.buffer;
  }

  throw new Error('the derivation was handed something that is not bytes');
}

// What each HKDF import was handed, in call order and **by reference**.
//
// `hkdf.ts` passes its `ikm` argument straight to
// `crypto.subtle.importKey('raw', ikm, 'HKDF', …)`, so this is the last point
// the PRF output can be observed before it stops being bytes — and the first
// point at which a copy taken anywhere upstream has become visible, because a
// copy is a different buffer however equal its contents.
//
// Filtered to `'HKDF'` deliberately. `importAesGcmKey` calls the same platform
// method a moment later and **does** copy, by design: `Uint8Array.from(material)`
// onto a buffer no caller names. That copy is of *derived* material, not of the
// PRF output, and folding the two imports together here would make this
// instrument report a wipe-safe design as a leak.
//
// The spy calls through — the derivation these tests observe is the derivation
// that ships, and a mocked `importKey` would leave every seal below comparing
// values nothing produced.
function watchDerivationInputs(): () => readonly ArrayBufferLike[] {
  const importDoor = vi.spyOn(crypto.subtle, 'importKey');

  return (): readonly ArrayBufferLike[] =>
    importDoor.mock.calls
      .filter((call) => call[2] === 'HKDF')
      .map((call) => bufferBehind(call[1]));
}

// Everything reachable from a value, rendered as text: strings as themselves,
// bytes as hex, objects walked. A `CryptoKey` contributes nothing — its
// properties live on the prototype and its material lives nowhere JavaScript can
// read — which is exactly the property being relied on.
function reachableText(value: unknown, seen = new Set<object>()): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }

  if (value instanceof ArrayBuffer) {
    return toHex(new Uint8Array(value));
  }

  if (ArrayBuffer.isView(value)) {
    return toHex(
      new Uint8Array(value.buffer, value.byteOffset, value.byteLength),
    );
  }

  if (typeof value !== 'object' || seen.has(value)) {
    return '';
  }

  seen.add(value);

  return Object.values(value)
    .map((member) => reachableText(member, seen))
    .join('|');
}

// The fake platform. A class rather than an object literal so that an
// implementation narrowing with `instanceof PublicKeyCredential` — the one
// narrowing available in a real browser — is exercised rather than defeated.
class StubPublicKeyCredential {
  public readonly type = 'public-key';
  public readonly authenticatorAttachment = 'platform';

  constructor(
    public readonly id: string,
    public readonly rawId: ArrayBuffer,
    public readonly response: unknown,
    private readonly extensionResults: AuthenticationExtensionsClientOutputs,
  ) {}

  public getClientExtensionResults(): AuthenticationExtensionsClientOutputs {
    return this.extensionResults;
  }
}

const CHALLENGE_BYTES = Uint8Array.from([
  0xfb, 0xff, 0xbe, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
  0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
  0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c,
]);

const USER_HANDLE_BYTES = Uint8Array.from([
  0x9a, 0x0b, 0x1c, 0x2d, 0x3e, 0x4f, 0x50, 0x61, 0x72, 0x83, 0x94, 0xa5, 0xb6,
  0xc7, 0xd8, 0xe9,
]);

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
  excludeCredentials: [],
  extensions: { prf: {} },
};

const SERVER_REQUEST_OPTIONS = {
  challenge: encodeBase64Url(CHALLENGE_BYTES),
  rpId: 'budgetoid.app',
  timeout: 120000,
  userVerification: 'required',
};

const NEW_CREDENTIAL_ID_BYTES = Uint8Array.from([
  0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22, 0x33, 0x44,
]);

// What a browser puts in `id`: its own **rendering** of the bytes that are in
// `rawId`, spelled here with the standard base64 alphabet — one character away
// from the base64url the payload must carry, because the bytes above were
// chosen to encode a `-`.
//
// The divergence is the whole point of the constant. Rendered identically, `id`
// and `rawId` are interchangeable in this fixture and a payload built from
// either one passes, so the rule that the payload comes from the *bytes* is
// stated in a comment and tested by nothing. Every browser shipping today
// renders `id` as base64url and this fixture is therefore not a browser — which
// is the honest position: a fixture that could only be wrong on a platform that
// exists is a fixture that finds the bug after somebody ships it.
const RENDERED_CREDENTIAL_ID = encodeBase64Url(NEW_CREDENTIAL_ID_BYTES)
  .replaceAll('-', '+')
  .replaceAll('_', '/');

// Registration's own bytes, and the local assertion's, deliberately disjoint.
// The payload the server receives has to be built from the first set only, and
// two sets that shared a byte pattern would let a mix-up pass.
const CREATION_CLIENT_DATA_BYTES = utf8.encode(
  '{"type":"webauthn.create","challenge":"aaa"}',
);
const ATTESTATION_BYTES = Uint8Array.from([0xa1, 0x63, 0x66, 0x6d, 0x74]);

const LOCAL_CLIENT_DATA_BYTES = utf8.encode(
  '{"type":"webauthn.get","challenge":"zzz"}',
);
const LOCAL_AUTHENTICATOR_DATA_BYTES = Uint8Array.from([
  0x77, 0x77, 0x77, 0x77, 0x77, 0x77,
]);
const LOCAL_SIGNATURE_BYTES = Uint8Array.from([0x66, 0x66, 0x66, 0x66]);

// Two distinguishable PRF outputs, one per route, so a failure names which route
// the key came from instead of merely saying the bytes were wrong.
const CREATION_PRF_BYTES = Uint8Array.from([
  0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c,
  0x4d, 0x4e, 0x4f, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
  0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0x5f,
]);

const ASSERTION_PRF_BYTES = Uint8Array.from([
  0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c,
  0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99,
  0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f,
]);

// The frozen inputs of the envelope comparison. A non-extractable key has no
// other witness: it is observed by sealing something under it and comparing what
// comes out, exactly as `account-keys.spec.ts` observes the same keys.
const GOLDEN_NONCE_BYTES = Uint8Array.from([
  0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xbb,
]);
const GOLDEN_PLAINTEXT_BYTES = Uint8Array.from([
  0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
]);
const GOLDEN_SPEC_ASSOCIATED_DATA = utf8.encode(
  'budgetoid/webauthn-ceremony/spec/v1',
);

// Seals under `key` with a fixed nonce and returns the envelope as hex, which is
// the only way two `CryptoKey`s that nothing can read can be compared.
//
// The nonce mock is installed immediately before the seal and restored
// immediately after, never for the length of a test: the ceremony under test
// draws from the same generator for its local challenge, and a mock left
// standing across it would hand that challenge twelve constant bytes and quietly
// pass the freshness test below.
async function sealedUnder(key: CryptoKey): Promise<string> {
  const fixedNonce = vi
    .spyOn(crypto, 'getRandomValues')
    .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
      const bytes = new Uint8Array(
        (buffer as ArrayBufferView).buffer,
        (buffer as ArrayBufferView).byteOffset,
        (buffer as ArrayBufferView).byteLength,
      );
      bytes.set(GOLDEN_NONCE_BYTES.subarray(0, bytes.length));

      return buffer;
    });

  try {
    return toHex(
      await sealEnvelope(
        key,
        GOLDEN_PLAINTEXT_BYTES,
        GOLDEN_SPEC_ASSOCIATED_DATA,
      ),
    );
  } finally {
    fixedNonce.mockRestore();
  }
}

// A PRF output as the platform hands one over — an `ArrayBuffer` — with a view
// kept beside it so the spec can look at the same bytes after the ceremony has
// had its way with them.
function prfOutput(source: Uint8Array): {
  readonly buffer: ArrayBuffer;
  readonly view: Uint8Array;
} {
  const copy = source.slice();

  return { buffer: copy.buffer, view: copy };
}

// The credential a registration produced. Its `id` is base64url and agrees with
// `rawId`, deliberately: this one's `id` is read as a **key** — it names the
// credential in the local assertion's `evalByCredential` map, where WebAuthn's
// own definition of the key is the browser's rendering. That is the one place
// the rendering is the right value to use, so the fixture does not push it
// away from the bytes.
function creationCredential(
  extensionResults: AuthenticationExtensionsClientOutputs,
): PublicKeyCredential {
  return new StubPublicKeyCredential(
    encodeBase64Url(NEW_CREDENTIAL_ID_BYTES),
    toArrayBuffer(NEW_CREDENTIAL_ID_BYTES),
    {
      clientDataJSON: toArrayBuffer(CREATION_CLIENT_DATA_BYTES),
      attestationObject: toArrayBuffer(ATTESTATION_BYTES),
    },
    extensionResults,
  ) as unknown as PublicKeyCredential;
}

// The credential an assertion produced, and the one whose `id` diverges from
// its `rawId` — see {@link RENDERED_CREDENTIAL_ID}. What the server receives has
// to be built from the bytes, and `id` is the member a reader reaches for first
// because it is already a string.
function assertionCredential(
  extensionResults: AuthenticationExtensionsClientOutputs,
  userHandle: ArrayBuffer | null = toArrayBuffer(USER_HANDLE_BYTES),
): PublicKeyCredential {
  return new StubPublicKeyCredential(
    RENDERED_CREDENTIAL_ID,
    toArrayBuffer(NEW_CREDENTIAL_ID_BYTES),
    {
      clientDataJSON: toArrayBuffer(LOCAL_CLIENT_DATA_BYTES),
      authenticatorData: toArrayBuffer(LOCAL_AUTHENTICATOR_DATA_BYTES),
      signature: toArrayBuffer(LOCAL_SIGNATURE_BYTES),
      userHandle,
    },
    extensionResults,
  ) as unknown as PublicKeyCredential;
}

// Unwraps a result, or fails naming the refusal. A bare `result.ok` assertion
// followed by a cast would report "cannot read property of undefined" three
// lines later and say nothing about which refusal happened.
function ceremonyValue<T>(
  result: { ok: true; value: T } | { ok: false; failure: string },
): T {
  if (!result.ok) {
    throw new Error(`the ceremony refused with "${result.failure}"`);
  }

  return result.value;
}

function ceremonyFailure(
  result: { ok: true } | { ok: false; failure: string },
): string {
  if (result.ok) {
    throw new Error('the ceremony succeeded where a refusal was expected');
  }

  return result.failure;
}

// Every door out of a page that this runner has, watched at once, each behind
// the name a failure should print.
//
// `fetch` and `XMLHttpRequest` are the two an `HttpClient` ends up on, whichever
// backend is registered; `sendBeacon` is the quiet one — no response, no promise
// and nothing in the network panel to notice — which is exactly the shape an
// exfiltration takes. A door is watched by its call count and not by its
// arguments: what is forbidden is the request, not a particular payload, and an
// assertion about arguments would let the same bytes out under a member nobody
// thought to look at.
//
// The limit, stated rather than papered over: this watches the doors this
// environment has. A ceremony could still hand the bytes to something that
// keeps them in the page — `localStorage`, a global — and that is the other
// tests' job, not this one's.
function watchNetworkDoors(): ReadonlyMap<string, () => number> {
  const fetchDoor = vi.fn(() =>
    Promise.reject(new Error('a ceremony reached for the network')),
  );
  const beaconDoor = vi.fn(() => false);
  const openDoor = vi.spyOn(XMLHttpRequest.prototype, 'open');

  vi.stubGlobal('fetch', fetchDoor);
  // jsdom implements no `sendBeacon`, so it is assigned on rather than spied on
  // and taken back off in afterEach.
  Object.defineProperty(navigator, 'sendBeacon', {
    configurable: true,
    writable: true,
    value: beaconDoor,
  });

  return new Map([
    ['fetch', (): number => fetchDoor.mock.calls.length],
    ['navigator.sendBeacon', (): number => beaconDoor.mock.calls.length],
    ['XMLHttpRequest.open', (): number => openDoor.mock.calls.length],
  ]);
}

function doorsUsed(doors: ReadonlyMap<string, () => number>): string[] {
  return [...doors].filter(([, calls]) => calls() > 0).map(([name]) => name);
}

describe('WebauthnCeremonyService', () => {
  let service: WebauthnCeremonyService;
  let create: Mock<
    (options?: CredentialCreationOptions) => Promise<Credential | null>
  >;
  let get: Mock<
    (options?: CredentialRequestOptions) => Promise<Credential | null>
  >;

  function creationRequest(): PublicKeyCredentialCreationOptions {
    const publicKey = create.mock.calls[0]?.[0]?.publicKey;

    if (publicKey === undefined) {
      throw new Error('create() was never called with a publicKey member');
    }

    return publicKey;
  }

  function localAssertionRequest(): PublicKeyCredentialRequestOptions {
    const publicKey = get.mock.calls[0]?.[0]?.publicKey;

    if (publicKey === undefined) {
      throw new Error('get() was never called with a publicKey member');
    }

    return publicKey;
  }

  beforeEach(() => {
    // The default for both is a ceremony that resolved nothing, so a test that
    // forgets to arrange one is a refusal rather than a `TypeError` from inside
    // the service.
    create = vi.fn<
      (options?: CredentialCreationOptions) => Promise<Credential | null>
    >(() => Promise.resolve(null));
    get = vi.fn<
      (options?: CredentialRequestOptions) => Promise<Credential | null>
    >(() => Promise.resolve(null));

    // jsdom implements none of this, so it is assigned on rather than spied on,
    // and taken back off in afterEach.
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('PublicKeyCredential', StubPublicKeyCredential);
    Object.defineProperty(navigator, 'credentials', {
      configurable: true,
      writable: true,
      value: { create, get },
    });

    TestBed.configureTestingModule({});
    service = TestBed.inject(WebauthnCeremonyService);
  });

  afterEach(() => {
    Reflect.deleteProperty(navigator, 'credentials');
    Reflect.deleteProperty(navigator, 'sendBeacon');
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('runs a second, local assertion when the creation returned no PRF output', async () => {
    // Arrange
    // The default behaviour of a great many platform authenticators: `create()`
    // answers `enabled: true` — this credential *can* derive — and returns no
    // output until the first assertion. A client that raised `no-prf` here would
    // turn away exactly the devices the product is built around, and would do it
    // with a message blaming the authenticator.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const ceremony = ceremonyValue(
      await service.createPasskey(SERVER_CREATION_OPTIONS),
    );

    // Assert
    expect(get).toHaveBeenCalledTimes(1);

    // And the key is the one that second route produced. Sealing under it and
    // under a key derived independently from the same bytes is the only
    // comparison a non-extractable key admits.
    const expected = await keyEncryptionKeyFromPasskey(ASSERTION_PRF_BYTES);
    expect(await sealedUnder(ceremony.keyEncryptionKey)).toBe(
      await sealedUnder(expected),
    );
  });

  it('derives from the creation’s own PRF output and runs no second assertion', async () => {
    // Arrange
    // The other authenticator, and the control for the test above: an
    // implementation that always ran the local assertion would pass that one and
    // fail this, having shown the person two prompts for one registration.
    const creationPrf = prfOutput(CREATION_PRF_BYTES);
    create.mockResolvedValue(
      creationCredential({
        prf: { enabled: true, results: { first: creationPrf.buffer } },
      }),
    );

    // Act
    const ceremony = ceremonyValue(
      await service.createPasskey(SERVER_CREATION_OPTIONS),
    );

    // Assert
    expect(get).not.toHaveBeenCalled();

    const expected = await keyEncryptionKeyFromPasskey(CREATION_PRF_BYTES);
    expect(await sealedUnder(ceremony.keyEncryptionKey)).toBe(
      await sealedUnder(expected),
    );

    // And it is the *creation's* output, not the assertion's, which the two
    // disjoint byte patterns are what make checkable.
    const other = await keyEncryptionKeyFromPasskey(ASSERTION_PRF_BYTES);
    expect(await sealedUnder(ceremony.keyEncryptionKey)).not.toBe(
      await sealedUnder(other),
    );
  });

  it('never hands the PRF output back, in any shape', async () => {
    // Arrange
    // The value the account's whole keyspace hangs on. Handed back as bytes it
    // can be logged, serialised into a request body, put in `localStorage` or
    // swept up by a crash reporter — by any code holding the object, without
    // anybody deciding to. So the only thing that leaves is a key nothing can
    // read, and the raw output is cleared once it has been used.
    const creationPrf = prfOutput(CREATION_PRF_BYTES);
    create.mockResolvedValue(
      creationCredential({
        prf: { enabled: true, results: { first: creationPrf.buffer } },
      }),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);
    const ceremony = ceremonyValue(result);

    // Assert
    // Non-extractable, which is what makes the bytes unreachable by
    // construction rather than by discipline.
    expect(ceremony.keyEncryptionKey.extractable).toBe(false);
    await expect(
      crypto.subtle.exportKey('raw', ceremony.keyEncryptionKey),
    ).rejects.toThrow();

    // Nothing reachable from the whole result holds them either — not a member
    // added beside the key "for the caller to check", not a copy left on the
    // payload.
    const surface = reachableText(result);
    expect(surface).not.toContain(toHex(CREATION_PRF_BYTES));
    expect(surface).not.toContain(encodeBase64Url(CREATION_PRF_BYTES));

    // And the buffer the ceremony was handed has been cleared, so the copy the
    // browser produced does not outlive the derivation either.
    expect(toHex(creationPrf.view)).toBe(
      '00'.repeat(CREATION_PRF_BYTES.length),
    );
  });

  it('discards the local assertion — none of it reaches the payload', async () => {
    // Arrange
    // The second assertion exists to make the authenticator derive, and for
    // nothing else. Its client data, its authenticator data and its signature are
    // over a challenge this client invented, which no server ever issued and no
    // server would accept; sending them would be sending a forged sign-in
    // alongside a registration.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);
    const ceremony = ceremonyValue(result);

    // Assert
    // The payload is the registration's, byte for byte.
    expect(ceremony.payload.clientDataJson).toBe(
      encodeBase64Url(CREATION_CLIENT_DATA_BYTES),
    );
    expect(ceremony.payload.attestationObject).toBe(
      encodeBase64Url(ATTESTATION_BYTES),
    );

    // And nothing the local assertion produced is anywhere in what came back.
    const surface = reachableText(result);
    for (const bytes of [
      LOCAL_CLIENT_DATA_BYTES,
      LOCAL_AUTHENTICATOR_DATA_BYTES,
      LOCAL_SIGNATURE_BYTES,
    ]) {
      expect(surface).not.toContain(encodeBase64Url(bytes));
      expect(surface).not.toContain(toHex(bytes));
    }
  });

  it('sends nothing anywhere: no ceremony of the three touches the network', async () => {
    // Arrange
    // The other half of "discarded", and the half that is about the bytes
    // rather than about the payload. All three legs are run — the registration
    // one through its second, local assertion, and the unlock, which is a whole
    // ceremony nobody ever sends — while every door out of the page is watched.
    //
    // **A census that stops seeing a leg is no longer a census.** This is the
    // only runtime witness that `deriveKeyFromLocalAssertion` never talks to a
    // server, and it is a witness only because the leg is driven here: a third
    // method left out of this body would be covered by the reassuring name of
    // this test and by nothing else. So it is edited rather than copied — a
    // second census beside this one would let the two disagree about which legs
    // exist.
    //
    // This is deliberately not a claim about the service's constructor.
    // `HttpClient` is `providedIn: 'root'`, so no injector can be asked to
    // prove it was never wanted; and a service that injected one and never
    // called it would send nothing either. What the product needs is that
    // nothing leaves, which is what is asserted, and it holds against a bare
    // `fetch` as well as against an injected client.
    const doors = watchNetworkDoors();
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    ceremonyValue(await service.createPasskey(SERVER_CREATION_OPTIONS));
    get.mockResolvedValue(
      assertionCredential({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    );
    ceremonyValue(await service.assertPasskey(SERVER_REQUEST_OPTIONS));
    get.mockResolvedValue(
      assertionCredential({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    );
    ceremonyValue(await service.deriveKeyFromLocalAssertion());

    // Assert
    // Named rather than counted, so a failure says which door was opened.
    expect(doorsUsed(doors)).toEqual([]);
  });

  it('watches doors a request would really be seen through', async () => {
    // Arrange
    // The negative control for the test above, and the reason that one is worth
    // reading. An instrument watching nothing reports silence perfectly: a
    // misspelled global, a spy installed after the module captured the
    // reference, a door this runner does not implement — each turns the
    // assertion above into a green test of nothing at all. So each watch is
    // walked up to and made to fire.
    const doors = watchNetworkDoors();

    // Act
    await expect(fetch('https://example.test/api')).rejects.toThrow();
    navigator.sendBeacon('https://example.test/api');
    new XMLHttpRequest().open('GET', 'https://example.test/api');

    // Assert
    expect(doorsUsed(doors).sort()).toEqual([...doors.keys()].sort());
  });

  it('refuses an authenticator that produced no PRF output by either route', async () => {
    // Arrange
    // Both routes tried and neither answered. This device cannot hold the
    // account's keys, and the refusal has to name that rather than fall through
    // as a generic failure — the person is holding the authenticator, and "this
    // device cannot do it" is a different sentence from "something went wrong".
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(assertionCredential({ prf: {} }));

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('no-prf');
    // Both routes were genuinely tried, which is what separates this refusal
    // from an implementation that gave up after `create()`.
    expect(create).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('claims PRF worked when only the local assertion derived', async () => {
    // Arrange
    // **The client and the server have to mean the same thing by "PRF
    // worked", and today they do not.** This module decides by the presence of
    // an *output* and will take one from either route; the payload's claim is
    // built from `create()`'s results alone, so it reports `null` whenever the
    // creation carried no `prf` member at all. The server gates on the word:
    // `RegisterAccountHandler.cs` refuses anything that is not
    // `{ Enabled: true }`.
    //
    // The device below is not a corner case. It is the one the second route
    // exists for, written at its most literal: `create()` returns nothing about
    // the extension, and the first assertion derives. Such a client runs the
    // whole ceremony, draws the account's keys, seals twenty-two envelopes,
    // shows a person ten codes they are told to write down — and then posts a
    // payload the server refuses with a 400, which this flow reads as `refused`
    // and renders as "throw the codes away and start again". Every retry on
    // that device ends the same way, so the account can never be created at
    // all.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({}));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const ceremony = ceremonyValue(
      await service.createPasskey(SERVER_CREATION_OPTIONS),
    );

    // Assert
    // The claim is about what this client *did*, which is derive a
    // key-encryption key through the extension — not about which of the two
    // routes the authenticator chose to answer on, a distinction the server has
    // no member for and no interest in.
    //
    // Deep equality rather than a member check, for the reason
    // `webauthn-encoding.ts` builds this object fresh instead of forwarding
    // one: the results carry `prf.results.first`, which is the PRF output
    // itself, and a spread or a filtered copy satisfies every check that only
    // asks whether `enabled` is `true`.
    expect(ceremony.payload.clientExtensionResults).toStrictEqual({
      prf: { enabled: true },
    });

    // The control. Without it this passes on an implementation that hard-codes
    // `true` on every registration, including one where nothing derived
    // anything — which would send the server a claim no device ever made.
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('takes an authenticator at its word when it says the extension is off', async () => {
    // Arrange
    // The other end of the same disagreement. `enabled: false` is the
    // authenticator answering the question directly: this credential does not
    // evaluate the extension, and no assertion against it ever will. The client
    // currently ignores the word entirely — the local route runs whenever no
    // output is present, which includes here — so the person is shown a second
    // system prompt, made to authenticate again, and refused anyway.
    //
    // A refusal is owed to them either way; what is not owed is the second
    // prompt. And the refusal is `no-prf` rather than `failed`: the ceremony
    // succeeded, and what this device cannot do is hold the account's keys.
    create.mockResolvedValue(creationCredential({ prf: { enabled: false } }));
    // Deliberately willing, and it must never be reached. An authenticator that
    // said no and then derived on the next breath is not a device this client
    // is entitled to argue with, and a payload built out of that assertion
    // would claim `enabled: true` over a credential that reported the opposite.
    get.mockResolvedValue(
      assertionCredential({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('no-prf');
    expect(
      get,
      'the person was asked for a second assertion after the authenticator ' +
        'had already said the extension is off.',
    ).not.toHaveBeenCalled();
  });

  it('asks the authenticator to evaluate the account’s own PRF input', async () => {
    // Arrange
    const creationPrf = prfOutput(CREATION_PRF_BYTES);
    create.mockResolvedValue(
      creationCredential({
        prf: { enabled: true, results: { first: creationPrf.buffer } },
      }),
    );

    // Act
    await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    // Read off `account-keys.ts`, never typed again. The value is the input the
    // account's key-encryption key is derived through, so a second copy of the
    // string is a second place for it to drift — and the day it drifts, every
    // account that wrapped its keys under the old one is locked out by a passkey
    // that still authenticates and simply hands back different bytes.
    const first = creationRequest().extensions?.prf?.eval?.first;
    expect(first).toBeDefined();
    expect(toHex(bytesOf(first))).toBe(
      toHex(utf8.encode(PASSKEY_PRF_EVAL_INPUT)),
    );
  });

  it('evaluates by the new credential on the local assertion, and names it', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    const request = localAssertionRequest();
    const credentialId = encodeBase64Url(NEW_CREDENTIAL_ID_BYTES);

    // `evalByCredential` and not `eval`: this assertion may only derive for the
    // credential that was just created, and keyed on anything else it derives for
    // whatever the authenticator happened to offer.
    const byCredential = request.extensions?.prf?.evalByCredential;
    expect(Object.keys(byCredential ?? {})).toEqual([credentialId]);
    expect(toHex(bytesOf(byCredential?.[credentialId]?.first))).toBe(
      toHex(utf8.encode(PASSKEY_PRF_EVAL_INPUT)),
    );

    // And `allowCredentials` names it. This is not a contradiction of the rule
    // that the *server's* request options carry none — that rule is about an
    // enumeration oracle on an endpoint anybody can call, and this list is built
    // on the device out of a credential the device just made, and is never sent.
    // It is also required: WebAuthn refuses `evalByCredential` when
    // `allowCredentials` is empty.
    expect(request.allowCredentials).toHaveLength(1);
    expect(toHex(bytesOf(request.allowCredentials?.[0]?.id))).toBe(
      toHex(NEW_CREDENTIAL_ID_BYTES),
    );
    expect(request.rpId).toBe('budgetoid.app');
  });

  it('draws a fresh challenge for the local assertion instead of replaying one', async () => {
    // Arrange
    // The registration challenge was minted by the server and spent by
    // `create()`. Replaying it is the shape this mistake takes — it is the only
    // challenge in scope — and it produces an assertion signed over a value the
    // server has already consumed. Nothing here would notice, because the
    // assertion is discarded; what would notice is the day somebody decides to
    // send it.
    const insecure = vi.spyOn(Math, 'random');
    const secure = vi.spyOn(crypto, 'getRandomValues');
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.createPasskey(SERVER_CREATION_OPTIONS);
    const firstChallenge = toHex(bytesOf(localAssertionRequest().challenge));

    get.mockResolvedValue(
      assertionCredential({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    );
    await service.createPasskey(SERVER_CREATION_OPTIONS);
    const secondPublicKey = get.mock.calls[1]?.[0]?.publicKey;
    // No empty-array fallback: a second assertion that never happened has to
    // fail this test by name, not quietly compare two empty strings and pass.
    const secondChallenge = toHex(bytesOf(secondPublicKey?.challenge));

    // Assert
    expect(firstChallenge).not.toBe(toHex(CHALLENGE_BYTES));
    expect(firstChallenge.length / 2).toBeGreaterThanOrEqual(16);
    expect(secondChallenge).not.toBe(firstChallenge);

    // Drawn from the platform's generator and not from a source somebody can
    // walk, the same rule `account-keys.ts` holds for the account's own keys.
    expect(secure).toHaveBeenCalled();
    expect(insecure).not.toHaveBeenCalled();
  });

  it('reports a dismissed prompt as cancelled, not as an incapable device', async () => {
    // Arrange
    // `NotAllowedError` is what a browser raises when the person closes the
    // sheet, and also when the ceremony times out. Neither says anything about
    // the authenticator, so folding it into `no-prf` would tell somebody their
    // perfectly capable phone is unsupported — and folding it into `failed`
    // would show an error to somebody who chose to stop.
    create.mockRejectedValue(
      new DOMException('The operation was aborted.', 'NotAllowedError'),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('cancelled');
  });

  it('reports an already-registered authenticator as duplicate', async () => {
    // Arrange
    // `InvalidStateError` is the authenticator declining because it already
    // holds a credential named in `excludeCredentials` — which is the exclusion
    // list working, not a failure. It is a different sentence from every other
    // one here: nothing is wrong, and there is nothing to retry.
    create.mockRejectedValue(
      new DOMException(
        'The authenticator is already registered.',
        'InvalidStateError',
      ),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    // Named explicitly against the two it is likeliest to be folded into: a
    // browser surfaces it through the same rejection channel as a dismissal, and
    // a catch-all maps it to `failed`.
    expect(ceremonyFailure(result)).toBe('duplicate');
  });

  it('reports anything else as failed', async () => {
    // Arrange
    create.mockRejectedValue(
      new TypeError('something the browser did not name'),
    );

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    // The catch-all has to stay a catch-all. An implementation that assumed
    // every rejection is a `DOMException` would read `.name` off a `TypeError`,
    // find `"TypeError"`, and fall through to whatever its last branch was.
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('reads a refusal off a DOMException and not off anything wearing the name', async () => {
    // Arrange
    // The guard the test above cannot reach. A `TypeError` is named
    // `"TypeError"`, so it lands on `failed` whether the branches ask
    // `instanceof DOMException` first or only compare `.name` — which leaves
    // the `instanceof` half held by nothing. What discriminates is a value that
    // is *not* a `DOMException` and carries the name anyway.
    //
    // Anything may reject, and something eventually will: a WebAuthn shim on a
    // wrapped web view hands back its native error reserialised as a plain
    // object, and an interceptor that logs and rethrows can do the same. The
    // cost of getting it wrong is quiet in the worst way — a genuine breakage
    // is reported as `cancelled`, the branch whose entire meaning is "nothing
    // went wrong, say nothing to anybody".
    create.mockRejectedValue({
      name: 'NotAllowedError',
      message: 'not a DOMException, and not this ceremony’s to interpret',
    });

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('reports a ceremony that resolved no credential as failed', async () => {
    // Arrange
    // **This holds the outward contract and not the narrowing, and the two are
    // separated here because a reader will otherwise credit it with both.**
    // `navigator.credentials.create()` is typed as resolving `Credential |
    // null`. Delete the `instanceof` check entirely and this test still passes:
    // `getClientExtensionResults()` on nothing throws a `TypeError`, the catch
    // turns it into the same `failed`, and the two routes are
    // indistinguishable from out here. What is left, and what is worth having,
    // is that a resolved null leaves as a **result** — never as a rejection out
    // of a method whose whole contract is a result — and that the word is
    // `failed` rather than `no-prf` or `cancelled`, which is what
    // `PasskeyCeremonyFailure` says it is: "anything else, including a ceremony
    // that resolved nothing".
    //
    // The narrowing itself is held by the sibling below, which is the one that
    // separates `instanceof` from a truthiness check, and by its mirror on the
    // assertion leg.
    create.mockResolvedValue(null);

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('refuses a resolved credential that is not a PublicKeyCredential', async () => {
    // Arrange
    // The null above does not, on its own, hold the narrowing that catches it:
    // a plain `if (!created)` refuses the same null, and so does no check at
    // all — reading a member off `null` throws a `TypeError` that the catch
    // turns into `failed` by another route. All three agree, so all three pass.
    //
    // What separates them is a resolved value that is truthy and complete and
    // still not the thing: `create()` is typed as resolving `Credential`, of
    // which `PublicKeyCredential` is one kind, and this object answers every
    // member the happy path reads. Under `instanceof` it is refused; under a
    // truthiness check the registration succeeds and the account acquires a
    // factor built out of whatever resolved.
    create.mockResolvedValue({
      id: RENDERED_CREDENTIAL_ID,
      rawId: toArrayBuffer(NEW_CREDENTIAL_ID_BYTES),
      type: 'public-key',
      response: {
        clientDataJSON: toArrayBuffer(CREATION_CLIENT_DATA_BYTES),
        attestationObject: toArrayBuffer(ATTESTATION_BYTES),
      },
      getClientExtensionResults: () => ({
        prf: {
          enabled: true,
          results: { first: prfOutput(CREATION_PRF_BYTES).buffer },
        },
      }),
    } as unknown as Credential);

    // Act
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('reports itself unavailable outside a secure context', async () => {
    // Arrange
    // WebAuthn is gated on a secure context, and the failure mode without this
    // check is not a clean error: `navigator.credentials` is present in the
    // page, so the call is made and rejects with something the branches above
    // would report as `failed`. The person is then told their device did not
    // work, when what did not work is the address they loaded the page from.
    vi.stubGlobal('isSecureContext', false);

    // Act
    const available = service.available();
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(available).toBe(false);
    expect(ceremonyFailure(result)).toBe('unsupported');
    // And nothing was attempted, which is what makes this a refusal rather than
    // a rescue after the fact.
    expect(create).not.toHaveBeenCalled();
  });

  it('reports itself unavailable where the browser has no credentials container', async () => {
    // Arrange
    // The other half of the same question, and the reason `available()` cannot
    // be a single check: a browser that never implemented WebAuthn serves the
    // page over https perfectly well.
    Reflect.deleteProperty(navigator, 'credentials');

    // Act
    const available = service.available();
    const result = await service.createPasskey(SERVER_CREATION_OPTIONS);

    // Assert
    expect(available).toBe(false);
    expect(ceremonyFailure(result)).toBe('unsupported');
  });

  it('builds the assertion payload out of what the authenticator signed', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const ceremony = ceremonyValue(
      await service.assertPasskey(SERVER_REQUEST_OPTIONS),
    );

    // Assert
    // The four members the server's signature check depends on, byte for byte,
    // plus the handle. `CompleteAssertionCommand` trusts none of them until the
    // signature over them verifies — which is exactly why a member re-encoded
    // wrongly here fails with nothing naming the cause.
    expect(ceremony.payload.credentialId).toBe(
      encodeBase64Url(NEW_CREDENTIAL_ID_BYTES),
    );

    // From `rawId`, and this fixture is what makes that checkable rather than
    // merely stated: the credential's own `id` renders the same bytes in the
    // standard alphabet, so a payload built from `id` comes back one character
    // different instead of identical. The first expectation guards the fixture
    // — the day those bytes are edited into something that renders the same
    // either way, this test says so instead of quietly stopping.
    expect(RENDERED_CREDENTIAL_ID).not.toBe(
      encodeBase64Url(NEW_CREDENTIAL_ID_BYTES),
    );
    expect(ceremony.payload.credentialId).not.toBe(RENDERED_CREDENTIAL_ID);

    expect(ceremony.payload.clientDataJson).toBe(
      encodeBase64Url(LOCAL_CLIENT_DATA_BYTES),
    );
    expect(ceremony.payload.authenticatorData).toBe(
      encodeBase64Url(LOCAL_AUTHENTICATOR_DATA_BYTES),
    );
    expect(ceremony.payload.signature).toBe(
      encodeBase64Url(LOCAL_SIGNATURE_BYTES),
    );
    expect(ceremony.payload.userHandle).toBe(
      encodeBase64Url(USER_HANDLE_BYTES),
    );
  });

  it('asks a sign-in for the PRF input too, and keeps its output in a key', async () => {
    // Arrange
    // A sign-in that produced no key-encryption key would authenticate the
    // person and leave every row on the account unreadable, because the wrapped
    // account keys are opened under exactly this value. The server's request
    // options carry no extensions — `PasskeyRequestOptions.cs` has four members
    // — so the input is this module's to add here, exactly as it is on the
    // creation leg.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const result = await service.assertPasskey(SERVER_REQUEST_OPTIONS);
    const ceremony = ceremonyValue(result);

    // Assert
    const publicKey = get.mock.calls[0]?.[0]?.publicKey;
    const first = publicKey?.extensions?.prf?.eval?.first;
    expect(first).toBeDefined();
    expect(toHex(bytesOf(first))).toBe(
      toHex(utf8.encode(PASSKEY_PRF_EVAL_INPUT)),
    );

    // Same custody rule as the registration leg: a non-extractable key out, no
    // bytes anywhere, and the buffer cleared behind it.
    const expected = await keyEncryptionKeyFromPasskey(ASSERTION_PRF_BYTES);
    expect(await sealedUnder(ceremony.keyEncryptionKey)).toBe(
      await sealedUnder(expected),
    );
    expect(ceremony.keyEncryptionKey.extractable).toBe(false);
    expect(reachableText(result)).not.toContain(toHex(ASSERTION_PRF_BYTES));
    expect(toHex(assertionPrf.view)).toBe(
      '00'.repeat(ASSERTION_PRF_BYTES.length),
    );
  });

  it('refuses a sign-in whose authenticator produced no PRF output', async () => {
    // Arrange
    // There is no second route here — an assertion is already the route — so the
    // refusal is immediate. It is still `no-prf` rather than `failed`: the
    // ceremony succeeded and the device simply cannot hold the account's keys.
    get.mockResolvedValue(assertionCredential({}));

    // Act
    const result = await service.assertPasskey(SERVER_REQUEST_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('no-prf');
  });

  it('reports a dismissed sign-in prompt as cancelled', async () => {
    // Arrange
    get.mockRejectedValue(
      new DOMException('The operation was aborted.', 'NotAllowedError'),
    );

    // Act
    const result = await service.assertPasskey(SERVER_REQUEST_OPTIONS);

    // Assert
    // The same mapping on this leg, written out rather than assumed: every
    // ceremony here has its own `catch`, so a branch added to one is not a
    // branch added to any of the others.
    expect(ceremonyFailure(result)).toBe('cancelled');
  });

  it('refuses a resolved sign-in credential that is not a PublicKeyCredential', async () => {
    // Arrange
    // The mirror of the registration leg's narrowing, and written out for the
    // same reason the `cancelled` mapping above is: **a branch added to one leg
    // is not a branch added to the other.** `assertPasskey` has its own
    // `navigator.credentials.get()`, its own check and its own catch, and no
    // test of `createPasskey` can speak for any of them.
    //
    // The object below is the discriminating one: `get()` is typed as resolving
    // `Credential`, of which `PublicKeyCredential` is one kind, and this answers
    // every member the happy path reads — a `rawId`, a complete assertion
    // response, and extension results carrying a PRF output. Under `instanceof`
    // it is refused. Under a truthiness check, or under no check at all, the
    // sign-in **succeeds**: a payload is built out of whatever resolved and the
    // account's key-encryption key is derived from bytes that arrived with it.
    // That is the failure worth a test — not a crash, but a session that looks
    // exactly like a real one.
    get.mockResolvedValue({
      id: RENDERED_CREDENTIAL_ID,
      rawId: toArrayBuffer(NEW_CREDENTIAL_ID_BYTES),
      type: 'public-key',
      response: {
        clientDataJSON: toArrayBuffer(LOCAL_CLIENT_DATA_BYTES),
        authenticatorData: toArrayBuffer(LOCAL_AUTHENTICATOR_DATA_BYTES),
        signature: toArrayBuffer(LOCAL_SIGNATURE_BYTES),
        userHandle: toArrayBuffer(USER_HANDLE_BYTES),
      },
      getClientExtensionResults: () => ({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    } as unknown as Credential);

    // Act
    const result = await service.assertPasskey(SERVER_REQUEST_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
    // And `no-prf` in particular is the wrong answer here, which is what makes
    // this a check on the narrowing rather than on the extension results: this
    // object carries a PRF output, so a leg that got as far as reading one
    // would either succeed or refuse for a reason that is not true of it.
    expect(ceremonyFailure(result)).not.toBe('no-prf');
  });

  it('reports a sign-in that resolved no credential as failed', async () => {
    // Arrange
    // The mirror of the registration leg's null, and it holds the same half:
    // the **outward contract**, not the narrowing. Delete the `instanceof`
    // check and this still passes by the other route — reading extension
    // results off nothing throws a `TypeError` the catch turns into the same
    // word. What it pins is that a `get()` resolving nothing leaves as a
    // result rather than as a rejection, and that the word is `failed` and not
    // `no-prf`: nothing was learned about the authenticator, because nothing
    // was returned by it.
    get.mockResolvedValue(null);

    // Act
    const result = await service.assertPasskey(SERVER_REQUEST_OPTIONS);

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('derives the key the account’s envelopes already open under', async () => {
    // Arrange
    // The acceptance criterion for the third leg, and the only claim about it
    // that can be stated without reference to its implementation: somebody whose
    // page reloaded presents the same authenticator and gets back the key their
    // wrapped envelopes were sealed under. Nothing on the server verifies this
    // ceremony and nothing needs to — a factor that is not this account's
    // derives a key that opens none of the account's envelopes, so the envelopes
    // are the proof and the assertion is not.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const keyEncryptionKey = ceremonyValue(
      await service.deriveKeyFromLocalAssertion(),
    );

    // Assert
    // Sealing under both keys is the only comparison a non-extractable key
    // admits, and it catches the three failures worth catching here: a
    // derivation re-implemented beside this leg, an HKDF `info` that drifted off
    // `PASSKEY_KEY_ENCRYPTION_KEY_INFO`, and a branch that derived from the
    // wrong bytes. Each of the three produces a perfectly good key that opens
    // nothing, on a device that authenticated perfectly.
    const expected = await keyEncryptionKeyFromPasskey(ASSERTION_PRF_BYTES);
    expect(await sealedUnder(keyEncryptionKey)).toBe(
      await sealedUnder(expected),
    );

    // One line rather than a test of its own: non-extractability is a property
    // of the key this leg hands back, not a behaviour of the leg, and it travels
    // the same derivation both other legs do.
    expect(keyEncryptionKey.extractable).toBe(false);

    // **One ceremony, and this line is the only thing that says so on the
    // succeeding path.** The count is asserted on the `no-prf` case below, which
    // leaves the happy path open — and measured, an implementation that ran a
    // second, redundant `get()` after deriving passed every other case on this
    // list. What that costs is paid in biometric prompts: two gestures for one
    // unlock, on the screen a person reaches by doing nothing worse than
    // reloading the page.
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('asks a local unlock to evaluate the account’s own PRF input', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    // Read off `account-keys.ts` and never typed again, for the reason the
    // registration leg's twin states: the value decides what every authenticator
    // hands back, so a second copy is a second place for it to drift and the
    // drift locks out every account that wrapped under the old one.
    const request = localAssertionRequest();
    const first = request.extensions?.prf?.eval?.first;
    expect(first).toBeDefined();
    expect(toHex(bytesOf(first))).toBe(
      toHex(utf8.encode(PASSKEY_PRF_EVAL_INPUT)),
    );

    // `eval` and not `evalByCredential`, which is the registration leg's shape
    // and wrong here: that map is keyed on a credential the device made a moment
    // earlier, and this leg holds no credential at all — the whole point is that
    // the authenticator chooses which passkey answers. Keyed on a guess, the
    // extension is evaluated for a credential that may not be the one presented,
    // or is refused outright.
    expect(request.extensions?.prf?.evalByCredential).toBeUndefined();
  });

  it('names no relying party, so the browser answers for the page it is on', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    // **`in`, and not `toBeUndefined()`.** The wrong implementation this test
    // exists for is the tidy one: route the leg through `assertPasskey` with a
    // hand-built options object, which passes almost every other case on this
    // list. `toRequestOptions` always *sets* `rpId`, so under
    // `toBeUndefined()` that implementation ships — the member is present and
    // holds whatever the hand-built object put there, which is a relying-party
    // id invented on the client. There is no such value here to invent from:
    // `passkey-relying-party-id` is the server's, frozen at `budgetoid.app`, and
    // a client that guesses it wrong runs a ceremony no credential answers.
    // Omitted, the browser fills it in from the page's own origin, which is the
    // only source that cannot be wrong.
    expect('rpId' in localAssertionRequest()).toBe(false);
  });

  it('names no credential, so the authenticator chooses which passkey answers', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    // A separate test from the relying party's, because two different wrong
    // implementations sit behind the two absences. This one is the registration
    // leg's `localPrfOutput` copied down here: that leg *must* name a
    // credential, because it holds one it made a moment ago and `evalByCredential`
    // is refused without a list. This leg holds none — it is a discoverable
    // assertion, as the sign-in's is, and the authenticator is what chooses
    // which passkey answers. A list built from anything the client could reach
    // for would narrow the ceremony to one credential and refuse the person
    // their other ones, which is the same mistake `GET /api/me/account-keys` was
    // narrowed by and had to be widened back out of.
    //
    // `in` for the shape rather than for the value, and **not** for the reason
    // the relying party's twin gives — measured, an implementation routing
    // through `assertPasskey` does not fail this case at all, because
    // `toRequestOptions` has four members and no `allowCredentials` among them.
    // Only the `rpId` test catches that one. What this form catches is the
    // options object assembled by spreading a shared base and blanking the
    // member — `{ ...base, allowCredentials: undefined }` — where the browser
    // is handed a present member and `toBeUndefined()` reports the absence this
    // leg needs as though it were there.
    expect('allowCredentials' in localAssertionRequest()).toBe(false);
  });

  it('demands user verification, which no server told it to', async () => {
    // Arrange
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    // A literal, and the one member of the three this leg invents rather than
    // omits. Every other ceremony in the product takes the word off options the
    // server minted; there are no options here, so leaving it out is not
    // neutral — WebAuthn's default is `'preferred'`, and every authenticator
    // that can skip the biometric then does, silently. The person gets their
    // account's content key back for a gesture that proved nothing about who was
    // holding the device, which is the entire event this leg exists to require.
    expect(localAssertionRequest().userVerification).toBe('required');
  });

  it('carries three members and no fourth', async () => {
    // Arrange
    // The two absences above are named one at a time because each has its own
    // wrong implementation behind it. This case holds the **class** they are
    // instances of, and it was added because the class was open: measured, an
    // options object carrying a `timeout` passed every other case on this list.
    // A number there is a third copy of a value the server owns on the other two
    // legs — it drifts against them silently, and the day it is shorter than the
    // real one somebody's unlock times out on a device that was working.
    //
    // Read whole rather than asserted member by member, so the next member
    // somebody adds has to be argued into this list instead of arriving unseen.
    // A ceremony whose parameters this client invents is exactly where a
    // fourth one would look harmless.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(Object.keys(localAssertionRequest()).sort()).toEqual([
      'challenge',
      'extensions',
      'userVerification',
    ]);
  });

  it('signs over a thirty-two-byte challenge drawn from the platform’s generator', async () => {
    // Arrange
    // Width and source in one case, because they are one decision: this
    // challenge is verified by nothing — the assertion is discarded — so it is
    // not a protocol constant but the floor below which "fresh" stops meaning
    // anything, and a value drawn from a source somebody can walk is no fresher
    // than a constant. The same rule `account-keys.ts` holds for the account's
    // own keys, for the same reason: `Math.random` passes every shape-based
    // check while being seeded from a value the page does not control and short
    // enough to enumerate.
    const insecure = vi.spyOn(Math, 'random');
    const secure = vi.spyOn(crypto, 'getRandomValues');
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(bytesOf(localAssertionRequest().challenge)).toHaveLength(32);
    expect(secure).toHaveBeenCalled();
    expect(insecure).not.toHaveBeenCalled();
  });

  it('draws a new challenge on every attempt', async () => {
    // Arrange
    // The control on the test above, and not a restatement of it: a challenge
    // drawn once into a module constant is thirty-two bytes from the platform's
    // generator on the first unlock and a replay on every one after it. Nothing
    // in this leg would notice — the assertion is discarded, so no verifier ever
    // sees the value twice — which is exactly why it is worth a test now rather
    // than the day somebody decides this assertion is worth sending.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();
    get.mockResolvedValue(
      assertionCredential({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    );
    await service.deriveKeyFromLocalAssertion();

    // Assert
    // No empty-array fallback on the second read: an attempt that never happened
    // has to fail this test by name rather than quietly compare two empty
    // strings and pass.
    const first = toHex(bytesOf(localAssertionRequest().challenge));
    const second = toHex(bytesOf(get.mock.calls[1]?.[0]?.publicKey?.challenge));
    expect(second).not.toBe(first);
  });

  it('refuses an authenticator that derived nothing', async () => {
    // Arrange
    // **There is deliberately no second route here, and nobody may add one.**
    // The registration leg has two because `create()` is not an assertion and
    // many authenticators derive only on the first one — this leg *is* an
    // assertion, so a second would be the same ceremony run twice, raising a
    // second system prompt on the way to the answer it already has. What it
    // would cost is paid by the person: two biometric prompts to be told their
    // device cannot open their account.
    //
    // And the word is `no-prf` rather than `failed`: the ceremony succeeded and
    // the authenticator answered. What it cannot do is hold this account's keys,
    // which is a sentence about the device and not about the request.
    get.mockResolvedValue(assertionCredential({}));

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(ceremonyFailure(result)).toBe('no-prf');
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('refuses a resolved unlock credential that is not a PublicKeyCredential', async () => {
    // Arrange
    // **The third copy of the same narrowing, and it is written out here for
    // the reason the other two are written out for each other: a branch added
    // to one leg is not a branch added to another.** This method has its own
    // `navigator.credentials.get()`, its own check and its own catch, and no
    // test of `createPasskey` or `assertPasskey` speaks for any of them. Its
    // doc says "five outcomes and no sixth", and until this case existed the
    // sixth — a resolved value that is not a credential — left by a route
    // nothing had ever executed.
    //
    // The object below is the discriminating one, mirroring the sign-in leg's:
    // `get()` is typed as resolving `Credential`, of which `PublicKeyCredential`
    // is one kind, and this answers every member the happy path reads,
    // extension results carrying a PRF output included. Under `instanceof` it
    // is refused. Under a truthiness check, or under no check at all, **the
    // unlock succeeds** — the account's key-encryption key is derived from
    // bytes that arrived with whatever resolved, handed to custody, and used to
    // try the account's envelopes. Nothing crashes. What a person sees is a
    // ceremony that worked and an account that will not open, which this leg
    // reports as `unopened`: a fact about their authenticator that is not true
    // of it.
    get.mockResolvedValue({
      id: RENDERED_CREDENTIAL_ID,
      rawId: toArrayBuffer(NEW_CREDENTIAL_ID_BYTES),
      type: 'public-key',
      response: {
        clientDataJSON: toArrayBuffer(LOCAL_CLIENT_DATA_BYTES),
        authenticatorData: toArrayBuffer(LOCAL_AUTHENTICATOR_DATA_BYTES),
        signature: toArrayBuffer(LOCAL_SIGNATURE_BYTES),
        userHandle: toArrayBuffer(USER_HANDLE_BYTES),
      },
      getClientExtensionResults: () => ({
        prf: { results: { first: prfOutput(ASSERTION_PRF_BYTES).buffer } },
      }),
    } as unknown as Credential);

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');

    // And `no-prf` in particular is the wrong answer here, which is what makes
    // this a check on the narrowing rather than on the extension results: the
    // object carries a PRF output, so a leg that got as far as reading one
    // would either succeed or refuse for a reason that is not true of it.
    expect(ceremonyFailure(result)).not.toBe('no-prf');

    // One `get()`, and it is not decoration on this leg: the refusal must be
    // immediate. A leg that answered a non-credential by running the ceremony
    // again would raise a second system prompt on the way to the same word.
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('reports a dismissed prompt as cancelled', async () => {
    // Arrange
    // `NotAllowedError` is what a browser raises when the person closes the
    // sheet, and also when the ceremony times out. Neither says anything about
    // the authenticator, and this leg is the one place the distinction is most
    // visible to somebody: a locked screen that reported `failed` for a sheet
    // they dismissed on purpose tells them something is wrong with the app.
    //
    // Written out on this leg rather than inherited from the other two, for the
    // reason the assertion leg's twin gives: a branch added to one leg is not a
    // branch added to another, and this method has its own `get`, its own
    // narrowing and its own catch.
    get.mockRejectedValue(
      new DOMException('The operation was aborted.', 'NotAllowedError'),
    );

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(ceremonyFailure(result)).toBe('cancelled');
  });

  it('reports anything else as failed', async () => {
    // Arrange
    // A separate test from the dismissal, because the two words are two
    // different sentences and a catch-all that answered `cancelled` for
    // everything would pass that one perfectly. `cancelled` means nothing went
    // wrong and nothing needs saying; reporting it over a genuine breakage is
    // the quiet failure — a locked account, a screen that reports the person's
    // own choice back to them, and nothing anywhere naming what actually broke.
    //
    // A `TypeError` also holds the other half: it is *named* `"TypeError"`, so
    // an implementation reading `.name` without asking `instanceof DOMException`
    // first lands here by luck rather than by rule.
    get.mockRejectedValue(new TypeError('something the browser did not name'));

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(ceremonyFailure(result)).toBe('failed');
  });

  it('reports itself unavailable where the browser cannot run a ceremony', async () => {
    // Arrange
    // Without the check the failure is not even clean: `navigator.credentials`
    // is present in an insecure context, so the call is made and rejects with
    // something the catch above reports as `failed` — and the person is told
    // their account could not be unlocked when what did not work is the address
    // they loaded the page from.
    vi.stubGlobal('isSecureContext', false);

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(ceremonyFailure(result)).toBe('unsupported');
    // And nothing was attempted, which is what makes this a refusal rather than
    // a rescue after the fact.
    expect(get).not.toHaveBeenCalled();
  });

  it('clears the PRF output once the key is derived', async () => {
    // Arrange
    // The retained view is what makes this observable at all: `prfOutput` hands
    // the ceremony the platform's own buffer and keeps a view over the same
    // bytes, so the spec can look at what the browser produced after the
    // derivation has had its way with it. A ceremony that copied the bytes
    // before deriving would wipe its copy, report success, and leave this view
    // holding the account's key-encryption material — which is the failure this
    // fixture is shaped to catch rather than a detail of how it is written.
    //
    // The custody rule is `account-keys.ts`'s and is not re-argued here. What is
    // this leg's own is that it is the third place the rule has to hold, and the
    // one with no server round trip after it to make a leak conspicuous.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    await service.deriveKeyFromLocalAssertion();

    // Assert
    expect(toHex(assertionPrf.view)).toBe(
      '00'.repeat(ASSERTION_PRF_BYTES.length),
    );
  });

  it('hands back nothing the assertion produced', async () => {
    // Arrange
    // The assertion is signed over a challenge this client invented, which no
    // server issued and none would accept. Returning any of it beside the key is
    // what a reader will do the first time a caller wants "a bit more" — and
    // what it produces is a forged sign-in in the hand of every caller of an
    // unlock, in a shape that looks like an ordinary result object.
    //
    // The type is what should stop it: the return is
    // `PasskeyCeremonyResult<CryptoKey>` over a bare key rather than a
    // one-member object, so there is nowhere for a payload to land without
    // somebody widening the signature on purpose. This test is what notices when
    // they do.
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    const result = await service.deriveKeyFromLocalAssertion();

    // Assert
    // Walked rather than named member by member, because the hazard is a member
    // nobody thought to look at. A `CryptoKey` contributes nothing to this text
    // — its properties live on the prototype and its material lives nowhere
    // JavaScript can read — which is exactly the property being relied on.
    const surface = reachableText(result);
    for (const bytes of [
      LOCAL_CLIENT_DATA_BYTES,
      LOCAL_AUTHENTICATOR_DATA_BYTES,
      LOCAL_SIGNATURE_BYTES,
    ]) {
      expect(surface).not.toContain(encodeBase64Url(bytes));
      expect(surface).not.toContain(toHex(bytes));
    }
  });

  // **The three legs derive from the platform's own buffer, never from a copy of
  // it**, and the three cases below are one rule about the class rather than
  // three facts about three methods. They are together because the rule is, and
  // apart from each other because a leg that stopped obeying it has to fail by
  // name.
  //
  // The wipe tests cannot see this and are not weakened by saying so. Each of
  // them asserts that the buffer the platform handed over ends up zeroed, and it
  // does — under
  //
  //   const copy = prfOutput.slice();
  //   …derive from copy…;
  //   prfOutput.fill(0);
  //
  // as faithfully as under the real thing. Every wipe assertion stays green
  // while a live copy of the account's key-encryption material sits on the heap
  // for the life of the tab, reachable by anything that runs in the page. What
  // tells the two apart is **identity**: a copy is a different buffer however
  // equal its contents, so these compare by reference and never by value.
  //
  // **The bound, stated so this does not read as "no copy can exist".** What is
  // checked is that the bytes reaching the HKDF import are the platform's own.
  // A copy taken *beside* the derivation — stashed somewhere while the original
  // still travels to `importKey` — is invisible here, and so is anything the
  // page does with the buffer after this call. Those need the module-state scan
  // this spec declines to make; see the header's note on `localStorage` and
  // globals. This closes the copy that is *on the path*, which is the one a
  // reader introduces while trying to be careful with a wipe.
  //
  // **Do not take the runner's advice here.** On a copy these fail with
  // `Received: serializes to the same string` and the printed hint "If it should
  // pass with deep equality, replace `toBe` with `toEqual`" — the runner reading
  // a byte-for-byte match as an argument that the two values are
  // interchangeable. That match *is* the defect. `toEqual` turns each of these
  // into a check that the copy holds the right bytes, which a copy always does,
  // and the whole set goes permanently green.

  it('derives an unlock from the platform’s own buffer, never from a copy', async () => {
    // Arrange
    const derivations = watchDerivationInputs();
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    ceremonyValue(await service.deriveKeyFromLocalAssertion());

    // Assert
    // The instrument's own control, and it is not decoration: a filter that
    // matched nothing would leave every identity check below comparing
    // `undefined` against a buffer, which fails — but for the wrong reason and
    // with a message naming nothing. One derivation, and exactly one.
    expect(derivations()).toHaveLength(1);
    expect(
      derivations()[0],
      'the unlock derived from a copy of the PRF output rather than from the ' +
        'buffer the platform handed back, so a live copy of the account’s ' +
        'key-encryption material outlives the wipe.',
    ).toBe(assertionPrf.buffer);
  });

  it('derives a registration from the platform’s own buffer, never from a copy', async () => {
    // Arrange
    // Both of this leg's routes, because a copy can be introduced on either side
    // of the `??` that chooses between them and neither side covers the other.
    // The creation route first, then the local-assertion route — whose bytes are
    // the ones a reader is likeliest to copy, since everything else that
    // ceremony produced is thrown away a line later.
    const derivations = watchDerivationInputs();
    const creationPrf = prfOutput(CREATION_PRF_BYTES);
    create.mockResolvedValue(
      creationCredential({
        prf: { enabled: true, results: { first: creationPrf.buffer } },
      }),
    );

    // Act
    ceremonyValue(await service.createPasskey(SERVER_CREATION_OPTIONS));

    // Assert
    expect(derivations()).toHaveLength(1);
    expect(
      derivations()[0],
      'the creation route derived from a copy of the PRF output.',
    ).toBe(creationPrf.buffer);

    // Act
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    create.mockResolvedValue(creationCredential({ prf: { enabled: true } }));
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );
    ceremonyValue(await service.createPasskey(SERVER_CREATION_OPTIONS));

    // Assert
    expect(derivations()).toHaveLength(2);
    expect(
      derivations()[1],
      'the local-assertion route derived from a copy of the PRF output — the ' +
        'route where a copy is likeliest, because everything else that ' +
        'ceremony produced is discarded a line later.',
    ).toBe(assertionPrf.buffer);
  });

  it('derives a sign-in from the platform’s own buffer, never from a copy', async () => {
    // Arrange
    const derivations = watchDerivationInputs();
    const assertionPrf = prfOutput(ASSERTION_PRF_BYTES);
    get.mockResolvedValue(
      assertionCredential({ prf: { results: { first: assertionPrf.buffer } } }),
    );

    // Act
    ceremonyValue(await service.assertPasskey(SERVER_REQUEST_OPTIONS));

    // Assert
    expect(derivations()).toHaveLength(1);
    expect(
      derivations()[0],
      'the sign-in derived from a copy of the PRF output.',
    ).toBe(assertionPrf.buffer);
  });
});
