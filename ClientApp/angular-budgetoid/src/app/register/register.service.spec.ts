// The one assertion this flow cannot be shipped without.
//
// A recovery code is never transmitted — `+core/security/recovery-codes.ts`
// says so in its first paragraph and ADR 0015 argues it — and the reason is
// stronger than "secrets should not travel". The account's key-encryption key
// is derived from the same code on an independent HKDF branch, so a code in a
// request body hands the operator the value that unwraps an account whose
// content it is otherwise structurally unable to read. The server has no member
// a code could arrive in; that is a fact about the server and not a guard on
// this client, because a code sent under any member name is already on the wire
// by the time the server refuses it.
//
// So the leak is checked where it happens: over `JSON.stringify` of the request
// body the `HttpTestingController` caught, which is literally the text that
// goes out. Three forms per code, because the payload can be built from any of
// them and only the first is the one a reader would think to look for — the
// raw 26 characters, the grouped rendering the codes step displays (the obvious
// mistake is a payload assembled from the rendered line), and the canonical
// fold every derivation off a code applies.
//
// The whole flow below runs on real crypto: `mintRecoveryCodeSet`,
// `generateAccountKeys` and `wrapAccountKeys` are production's, unstubbed, over
// Node's WebCrypto — `+core/security/account-keys.spec.ts` already shows that
// works under this runner. Stubbing them would be stubbing away the values this
// test is looking for.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import {
  ACCOUNT_KEY_BYTES,
  keyEncryptionKeyFromRecoveryCode,
  unwrapAccountKeys,
  type AccountKeys,
  type WrappedAccountKeys,
} from '@app-core/security/account-keys';
import { decodeBase64Url } from '@app-core/security/base64url';
import { isCanonicalFactorId } from '@app-core/security/factor-id';
import {
  ENVELOPE_NONCE_BYTES,
  ENVELOPE_TAG_BYTES,
  ENVELOPE_VERSION,
} from '@app-core/security/key-envelope';
import { canonicalRecoveryCode } from '@app-core/security/recovery-code-canonical';
import {
  RECOVERY_CODE_SET_SIZE,
  type RecoveryCode,
} from '@app-core/security/recovery-codes';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyResult,
  type PasskeyRegistrationCeremony,
} from '@app-core/security/webauthn-ceremony.service';
import type {
  PasskeyCreationOptionsJson,
  PasskeyRegistrationPayload,
} from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { beforeEach, describe, expect, it } from 'vitest';
import { RegisterService } from './register.service';

const API_BASE_URL = 'https://api.test';
const OPTIONS_URL = `${API_BASE_URL}/api/registration/options`;
const REGISTRATION_URL = `${API_BASE_URL}/api/registration`;

// Characters per printed group. Restated here rather than imported because the
// grouping lives inside `codes-step.component.ts` as a module-private rule —
// and this spec wants an *independent* rendering anyway: importing the same
// function the screen uses would make a payload built from the screen's own
// output invisible to the assertion that exists to catch it.
const GROUP_SIZE = 4;

// Factors per registration: the passkey, plus one per recovery code, because a
// factor is not a credential and a card of ten codes is ten factors. Computed
// rather than typed as eleven — the number is a consequence of the set size and
// a literal here would be a second, silent copy of it.
const FACTOR_COUNT = 1 + RECOVERY_CODE_SET_SIZE;

// The version byte occupies one byte by definition of the envelope layout.
// `key-envelope.ts` keeps that width as a module-private constant on purpose —
// it is the arithmetic the format is made of rather than a setting — so it is
// the one number below that cannot be imported. The other three are the shipped
// constants: a literal `61` here would be a second copy of the format, agreeing
// with the first until one of them moves.
const VERSION_BYTES = 1;
const ENVELOPE_BYTES =
  VERSION_BYTES + ENVELOPE_NONCE_BYTES + ACCOUNT_KEY_BYTES + ENVELOPE_TAG_BYTES;

// A realistic answer from `POST /api/registration/options`. Every binary member
// is unpadded base64url over bytes a server could actually have sent — the
// challenge is 32 bytes, the user handle 16, which is the width of the account
// id the assertion path compares byte-for-byte. `excludeCredentials` is empty
// because no account exists yet, so there is nothing to exclude.
//
// `satisfies` rather than an annotation, here and on the payload below: both
// check the shape, and only this one leaves the literals in place, so a member
// read out of the fixture is the value written above rather than `string`.
const CREATION_OPTIONS = {
  challenge: 'QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8',
  rp: { id: 'budgetoid.app', name: 'Budgetoid' },
  user: {
    id: 'EBESExQVFhcYGRobHB0eHw',
    name: 'owner@budgetoid.test',
    displayName: 'owner@budgetoid.test',
  },
  pubKeyCredParams: [
    { type: 'public-key', alg: -7 },
    { type: 'public-key', alg: -257 },
  ],
  timeout: 120_000,
  attestation: 'none',
  authenticatorSelection: {
    residentKey: 'required',
    requireResidentKey: true,
    userVerification: 'required',
  },
  excludeCredentials: [],
  extensions: { prf: {} },
} satisfies PasskeyCreationOptionsJson;

// What the ceremony hands back. Fixed, because nothing here is under test: the
// three members travel to the server unchanged and this file is looking for
// values that must *not* travel.
const REGISTRATION_PAYLOAD = {
  clientDataJson:
    'eyJ0eXBlIjoid2ViYXV0aG4uY3JlYXRlIiwiY2hhbGxlbmdlIjoiUUVGQ1EwUkZSa2' +
    'RJU1VwTFRFMU9UMUJSVWxOVVZWWlhXRmxhVzF4ZFhsOCIsIm9yaWdpbiI6Imh0dHBz' +
    'Oi8vYnVkZ2V0b2lkLmFwcCIsImNyb3NzT3JpZ2luIjpmYWxzZX0',
  attestationObject:
    'gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp-goaKjpKWmp6ipqqusra6v',
  clientExtensionResults: { prf: { enabled: true } },
} satisfies PasskeyRegistrationPayload;

// The passkey factor's key-encryption key, imported by this spec rather than
// derived, because the derivation needs a PRF output and no authenticator
// exists here. `extractable: false` matches what
// `keyEncryptionKeyFromPasskey` produces, so nothing above this seam can start
// depending on reading its bytes; `encrypt` and `decrypt` are both granted so a
// later test in this file can open what this one wrapped.
function importKeyEncryptionKey(): Promise<CryptoKey> {
  // Structured rather than zero-filled, so a byte that ends up somewhere it
  // should not be is visible in a failure.
  const bytes = new Uint8Array(32);

  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = 0x20 + index;
  }

  return crypto.subtle.importKey('raw', bytes, 'AES-GCM', false, [
    'encrypt',
    'decrypt',
  ]);
}

// One code as the codes step prints it: groups of four joined by hyphens, with
// the two-character tail 26 characters leave over.
function grouped(code: string): string {
  const groups: string[] = [];

  for (let start = 0; start < code.length; start += GROUP_SIZE) {
    groups.push(code.slice(start, start + GROUP_SIZE));
  }

  return groups.join('-');
}

// The flow is driven by `void` methods over WebCrypto, so there is no promise to
// await from outside — the observable effects arrive some number of microtasks
// later. Polling a public reading is the honest way to wait for them: it makes
// no claim about how many awaits the implementation happens to contain today,
// and a flow that never gets there fails with a sentence naming what never
// arrived rather than with a null dereference.
async function eventually<TValue>(
  read: () => TValue | null | undefined,
  what: string,
): Promise<TValue> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    const value = read();

    if (value !== null && value !== undefined) {
      return value;
    }

    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  throw new Error(`Timed out waiting for ${what}.`);
}

// A narrowing rather than an assertion: `as Record<string, unknown>` would
// claim the shape instead of checking it, and the one body this file cares
// about is the one that is not what it should be.
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

// The request body as an object, refused rather than coerced. A body that is
// not an object at all would otherwise reach the member checks below as
// `undefined`s that quietly pass.
function objectBodyOf(request: TestRequest): Record<string, unknown> {
  const body: unknown = request.request.body;

  if (!isRecord(body)) {
    throw new Error('The registration request carried no JSON object body.');
  }

  return body;
}

// One factor as the request body carries it: the identifier its two envelopes
// were sealed against, and the two envelopes.
interface BodyFactor {
  readonly factorId: string;
  readonly wrapped: WrappedAccountKeys;
}

// The eleven factors, read out of the body rather than asserted into shape. A
// member that is missing or of the wrong type fails here with a sentence naming
// which factor and which member, instead of arriving at a crypto call as
// `undefined`.
// `Array.isArray` narrows `unknown` to `any[]`, which is the one narrowing that
// leaves things worse than it found them: every element read off the result is
// then `any`, and the checks below would stop checking anything. This says the
// same thing and keeps the elements `unknown`.
function isUnknownArray(value: unknown): value is readonly unknown[] {
  return Array.isArray(value);
}

function factorsOf(body: Record<string, unknown>): readonly BodyFactor[] {
  const codes: unknown = body['codes'];

  if (!isUnknownArray(codes)) {
    throw new Error('The registration request carried no set of codes.');
  }

  // The passkey's pair sits at the top level and each code's beside its
  // verifier, which is the shape the server reads. Concatenating them here is
  // what lets every assertion below say "every factor" rather than "the passkey
  // and also the codes".
  const entries: readonly unknown[] = [body, ...codes];

  return entries.map((entry, index) => {
    if (!isRecord(entry)) {
      throw new Error(`Factor ${index} is not an object.`);
    }

    const factorId: unknown = entry['factorId'];
    const wrappedContentKey: unknown = entry['wrappedContentKey'];
    const wrappedIndexKey: unknown = entry['wrappedIndexKey'];

    if (
      typeof factorId !== 'string' ||
      typeof wrappedContentKey !== 'string' ||
      typeof wrappedIndexKey !== 'string'
    ) {
      throw new Error(
        `Factor ${index} carries no factor id and pair of envelopes.`,
      );
    }

    return { factorId, wrapped: { wrappedContentKey, wrappedIndexKey } };
  });
}

// Both envelopes of every factor, in body order.
function envelopesOf(factors: readonly BodyFactor[]): readonly string[] {
  return factors.flatMap((factor) => [
    factor.wrapped.wrappedContentKey,
    factor.wrapped.wrappedIndexKey,
  ]);
}

// One string in the body, and the member it arrived under.
interface BodyString {
  readonly path: string;
  readonly member: string;
  readonly value: string;
}

// Every string in a JSON value, at every depth. A walk rather than a list of
// members, because the member a future leak arrives on is by definition one
// nobody listed — an array of raw keys beside the envelopes, a debugging field,
// a member added by a later story. The path is carried so a failure names where
// the value was rather than only that one exists.
function stringsOf(value: unknown, path: string, member: string): BodyString[] {
  if (typeof value === 'string') {
    return [{ path, member, value }];
  }

  if (Array.isArray(value)) {
    return value.flatMap((entry: unknown, index) =>
      stringsOf(entry, `${path}[${index}]`, member),
    );
  }

  if (isRecord(value)) {
    return Object.entries(value).flatMap(([key, entry]: [string, unknown]) =>
      stringsOf(entry, `${path}.${key}`, key),
    );
  }

  return [];
}

// Decodes, or answers `null` for a string that is not base64url at all. A
// refusal is not a finding here: a value that cannot be decoded cannot be a key
// that was encoded, and `decodeBase64Url` throws rather than repairing.
function decodedOrNull(value: string): Uint8Array | null {
  try {
    return decodeBase64Url(value);
  } catch {
    return null;
  }
}

function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
  return (
    left.length === right.length &&
    left.every((byte, index) => byte === right[index])
  );
}

// Opens one recovery factor's pair, deriving that code's key-encryption key the
// way a redemption screen will have to.
async function openUnder(
  code: RecoveryCode,
  factor: BodyFactor,
): Promise<AccountKeys> {
  return unwrapAccountKeys(
    await keyEncryptionKeyFromRecoveryCode(code),
    factor.wrapped,
    factor.factorId,
  );
}

// Every place on a walk where {@link ACCOUNT_KEY_BYTES} bytes could be sitting:
// a byte view, a buffer, or a string this client would decode. Three shapes and
// not one, because the answer to "is the key readable from here" must not depend
// on which of them somebody happened to publish it as. Paths are collected
// rather than a boolean returned, so a finding names where it is.
function keySizedFindings(value: unknown, path: string): readonly string[] {
  // Before the record branch: a `Uint8Array` is `typeof 'object'`, and read as
  // one it walks into index keys holding numbers and reports nothing at all.
  if (ArrayBuffer.isView(value)) {
    return value.byteLength === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (value instanceof ArrayBuffer) {
    return value.byteLength === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (typeof value === 'string') {
    const bytes = decodedOrNull(value);

    return bytes !== null && bytes.length === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (Array.isArray(value)) {
    return value.flatMap((entry: unknown, index) =>
      keySizedFindings(entry, `${path}[${index}]`),
    );
  }

  if (isRecord(value)) {
    return Object.entries(value).flatMap(([key, entry]: [string, unknown]) =>
      keySizedFindings(entry, `${path}.${key}`),
    );
  }

  return [];
}

describe('RegisterService', () => {
  let http: HttpTestingController;
  let service: RegisterService;
  // The passkey factor's key-encryption key, held where the tests can reach it.
  // A wrap is only observable by opening it, and this is the key the passkey
  // factor's pair was sealed under — there is nothing else to look at.
  let keyEncryptionKey: CryptoKey;
  // What the device answers, read from the test rather than written into the
  // stub, because two of this flow's rules are about what happens when it says
  // no. Both are set to the willing answer before every test, so a test that
  // touches neither sees the fixture unchanged.
  let ceremonyAvailable: boolean;
  let ceremonyOutcome: PasskeyCeremonyResult<PasskeyRegistrationCeremony>;
  let navigations: string[];

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    ceremonyAvailable = true;
    ceremonyOutcome = {
      ok: true,
      value: { payload: REGISTRATION_PAYLOAD, keyEncryptionKey },
    };
    navigations = [];

    // The one stub that has to exist: `available()` and `createPasskey()` both
    // touch `navigator.credentials`, which this runner does not implement, so
    // the seam that owns them is replaced and nothing below it is.
    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'createPasskey'
    > = {
      available: () => ceremonyAvailable,
      createPasskey: (): Promise<
        PasskeyCeremonyResult<PasskeyRegistrationCeremony>
      > => Promise.resolve(ceremonyOutcome),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Router and the provider-sign-in service are here so that the
        // injector can never be the reason this file is red. Nothing below
        // asserts anything about either.
        provideRouter([]),
        // Overriding the router the line above provides, rather than declaring
        // routes: the flow navigates to `/app` on the 201, and a real router
        // with no matching route rejects that navigation into a promise nothing
        // awaits. Recording the address also gives the tests below a way to say
        // that nothing navigated.
        {
          provide: Router,
          useValue: {
            navigateByUrl: (url: string): Promise<boolean> => {
              navigations.push(url);

              return Promise.resolve(true);
            },
          },
        },
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => true,
            // Both are reached only once the account exists — the token is
            // discarded on the 201 and the address is read by the screen's
            // header — and a stub missing either throws inside a subscriber,
            // where the failure surfaces as an unhandled rejection naming
            // nothing.
            forgetProviderToken: () => undefined,
            providerEmail: () => null,
          },
        },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: API_BASE_URL }) },
        },
        { provide: WebauthnCeremonyService, useValue: ceremony },
        // Component-provided in production, so it is listed rather than
        // resolved from the root injector.
        RegisterService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(RegisterService);
  });

  // From the introduction to ten codes on screen. Every test below starts here
  // because that is where this flow's secrets exist — the account keys have
  // been drawn, wrapped eleven times and wiped, and the body that will be
  // posted is assembled — and none of them can reach any of it any earlier.
  async function driveToCodes(): Promise<readonly RecoveryCode[]> {
    service.begin();
    service.createPasskey();

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);

    return eventually(
      (): readonly RecoveryCode[] | null => service.codes(),
      'the minted recovery codes to be published',
    );
  }

  // The same, with the acknowledgement pressed. The request is left outstanding
  // — answering it drives the flow on into what a created account does next,
  // which only two tests below have any use for.
  async function driveToRegistration(): Promise<{
    readonly request: TestRequest;
    readonly codes: readonly RecoveryCode[];
  }> {
    const codes = await driveToCodes();
    service.create();

    const request = await eventually(
      () => http.match(REGISTRATION_URL)[0] ?? null,
      'the registration request',
    );

    return { request, codes };
  }

  // No `http.verify()` teardown, and the omission is deliberate: this test ends
  // with the registration request outstanding on purpose. The request *is* the
  // subject, and answering it would drive the flow on into whatever a created
  // account does next — a navigation, a session probe — for no gain here.

  it('sends no recovery code to the server', async () => {
    // Arrange
    service.begin();

    // Act
    service.createPasskey();

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);

    const codes = await eventually(
      (): readonly RecoveryCode[] | null => service.codes(),
      'the minted recovery codes to be published',
    );

    service.create();

    const registration = await eventually(
      () => http.match(REGISTRATION_URL)[0] ?? null,
      'the registration request',
    );

    // This string is what goes on the wire, character for character.
    const serialised = JSON.stringify(registration.request.body);

    // Assert
    // First, that there is anything to look for. Ten codes read off the public
    // signal — the same values the screen renders — because a loop over an
    // empty array passes all thirty assertions below while proving nothing, and
    // an empty set is exactly what a half-built flow publishes.
    expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);

    codes.forEach((code, index) => {
      // The raw code. The plain leak: an array of codes handed to the member
      // the verifiers belong in, which the brands in `recovery-codes.ts` make a
      // compile error and which a single `as` restores.
      expect(
        serialised.includes(code),
        `The raw code at index ${index} is in the request body.`,
      ).toBe(false);

      // The grouped rendering. The likelier leak, because it is the form the
      // codes step already holds: a payload assembled from what was displayed,
      // saved or copied carries the hyphens with it.
      expect(
        serialised.includes(grouped(code)),
        `The grouped rendering of the code at index ${index} is in the ` +
          'request body.',
      ).toBe(false);

      // The canonical fold. Identical to the raw code for anything
      // `mintRecoveryCode` produces — the alphabet is upper-case and excludes
      // every character the fold rewrites — so this is a guard against a future
      // in which the flow canonicalises before building the body, which would
      // slip past the first assertion the day the fold stops being the
      // identity.
      expect(
        serialised.includes(canonicalRecoveryCode(code)),
        `The canonical form of the code at index ${index} is in the request ` +
          'body.',
      ).toBe(false);
    });

    // And the member is `codes`, carrying one entry per code. The count is read
    // from the constant rather than typed as `10`: this client and the server
    // agree on ten with nothing executing the agreement, and a literal here
    // would be a third, silent copy of that number.
    const body = objectBodyOf(registration);
    expect(Array.isArray(body['codes'])).toBe(true);
    expect(body['codes']).toHaveLength(RECOVERY_CODE_SET_SIZE);

    // `recoveryCodes` is the spelling a reader reaches for and the one the
    // server's request-surface census refuses. Checked on the keys and in the
    // serialised text, because a nested member carries the same token.
    expect(Object.keys(body)).not.toContain('recoveryCodes');
    expect(serialised).not.toContain('recoveryCodes');
  });

  // The test above says no code travels. This one says nothing else the server
  // could open travels either — the PRF output the passkey factor's key is
  // derived from, and the account keys themselves. The two together are the
  // whole custody claim this flow makes on the wire.
  it('sends nothing the server could open', async () => {
    // Arrange
    const { request } = await driveToRegistration();

    // Act
    const body = objectBodyOf(request);
    const factors = factorsOf(body);
    const envelopes = envelopesOf(factors);
    const strings = stringsOf(body, 'body', 'body');
    const passkey = factors[0];
    const account = await unwrapAccountKeys(
      keyEncryptionKey,
      passkey.wrapped,
      passkey.factorId,
    );

    // Assert
    // Deep equality, not a member check. `getClientExtensionResults()` carries
    // `prf.results.first` — the PRF output itself, which is the value the
    // passkey factor's key-encryption key is derived from — and a forwarded or
    // spread copy of that object satisfies every check that only asks whether
    // `enabled` is `true`.
    expect(body['clientExtensionResults']).toStrictEqual({
      prf: { enabled: true },
    });

    expect(envelopes).toHaveLength(2 * FACTOR_COUNT);

    for (const [index, envelope] of envelopes.entries()) {
      const bytes = decodedOrNull(envelope);

      if (bytes === null) {
        throw new Error(`Envelope ${index} is not base64url at all.`);
      }

      // A sealed envelope and not a key: the width is the layout's, computed
      // from the shipped constants, and the version byte is the one the
      // envelope format's reader will refuse anything else in place of.
      expect(
        bytes.length,
        `Envelope ${index} is not an envelope's width.`,
      ).toBe(ENVELOPE_BYTES);
      expect(bytes[0], `Envelope ${index} leads with an unknown version.`).toBe(
        ENVELOPE_VERSION,
      );
    }

    // The control the walk needs. Every assertion below is a refusal, and a
    // walk that returned nothing would satisfy all of them while looking at
    // nothing — so this says it reached the strings nested two levels down
    // inside the set, which is where a leaked key would be.
    expect(strings.map((found) => found.value)).toEqual(
      expect.arrayContaining([...envelopes]),
    );

    for (const { path, member, value } of strings) {
      const bytes = decodedOrNull(value);

      if (bytes === null) {
        continue;
      }

      // Byte for byte against the account's own keys, recovered by opening the
      // passkey factor's pair. This is the half with no false negative: a key
      // that travelled under any member, at any depth, in the encoding this
      // client itself uses, is found here whatever it was called.
      expect(
        sameBytes(bytes, account.contentKey),
        `${path} carries the account's content key.`,
      ).toBe(false);
      expect(
        sameBytes(bytes, account.indexKey),
        `${path} carries the account's index key.`,
      ).toBe(false);

      // And the width, which catches key material this test cannot recognise:
      // a second draw, another account's key, a key belonging to a factor whose
      // envelope is not in this body. `verifier` is the one member legitimately
      // this wide — `RECOVERY_CODE_VERIFIER_BYTES` and `ACCOUNT_KEY_BYTES` are
      // independently chosen numbers that both happen to be 32, and a verifier
      // is an HKDF output on a branch that unwraps nothing. It is exempted by
      // name, and nothing else is.
      if (member !== 'verifier') {
        expect(
          bytes.length,
          `${path} decodes to ${ACCOUNT_KEY_BYTES} bytes.`,
        ).not.toBe(ACCOUNT_KEY_BYTES);
      }
    }

    // Twenty-two envelopes and twenty-two nonces. Two equal envelopes mean one
    // nonce was reused under one key, which under GCM gives up the
    // authentication subkey and ends the guarantee for every envelope that key
    // ever wrote — or, more prosaically, that one wrap was copied into another
    // factor's row and one of the two factors opens nothing.
    expect(new Set(envelopes).size).toBe(envelopes.length);
  });

  it('wraps the account keys once for the passkey and once per code', async () => {
    // Arrange
    const { request } = await driveToRegistration();

    // Act
    const body = objectBodyOf(request);
    const factors = factorsOf(body);

    // Assert
    // The passkey's own pair, at the top level. Wrapping only inside the loop
    // is the half-implementation the count alone would hide: it satisfies "one
    // per code" and leaves the authenticator the person just registered unable
    // to open the account it was registered for.
    expect(typeof body['factorId']).toBe('string');
    expect(typeof body['wrappedContentKey']).toBe('string');
    expect(typeof body['wrappedIndexKey']).toBe('string');

    expect(body['codes']).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(factors).toHaveLength(FACTOR_COUNT);
    expect(envelopesOf(factors)).toHaveLength(2 * FACTOR_COUNT);
  });

  // The test that catches `generateAccountKeys()` moved inside the loop. A
  // per-factor draw passes every round trip, every count and every constraint
  // the server has — and loses the account's whole history the first time the
  // other factor is used, on a day nobody will connect to this line.
  it('draws the account keys once for the whole set', async () => {
    // Arrange
    const { request, codes } = await driveToRegistration();
    const factors = factorsOf(objectBodyOf(request));

    // Act
    // Three factors sharing nothing but the account: the passkey, the first
    // code and the last. Any one of them alone opens perfectly under a
    // per-factor draw.
    const [passkey, first, last] = await Promise.all([
      unwrapAccountKeys(
        keyEncryptionKey,
        factors[0].wrapped,
        factors[0].factorId,
      ),
      openUnder(codes[0], factors[1]),
      openUnder(codes[RECOVERY_CODE_SET_SIZE - 1], factors[FACTOR_COUNT - 1]),
    ]);

    // Assert
    expect(passkey.contentKey).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(passkey.indexKey).toHaveLength(ACCOUNT_KEY_BYTES);

    for (const [name, keys] of [
      ['the first code', first],
      ['the last code', last],
    ] as const) {
      expect(
        sameBytes(passkey.contentKey, keys.contentKey),
        `${name} opens a different content key from the passkey's.`,
      ).toBe(true);
      expect(
        sameBytes(passkey.indexKey, keys.indexKey),
        `${name} opens a different index key from the passkey's.`,
      ).toBe(true);
    }
  });

  it('binds every envelope to its own factor', async () => {
    // Arrange
    const { request, codes } = await driveToRegistration();
    const factors = factorsOf(objectBodyOf(request));
    const cases = [
      {
        name: 'the passkey',
        kek: keyEncryptionKey,
        factor: factors[0],
        neighbour: factors[1],
      },
      {
        name: 'the first code',
        kek: await keyEncryptionKeyFromRecoveryCode(codes[0]),
        factor: factors[1],
        neighbour: factors[2],
      },
      {
        name: 'the last code',
        kek: await keyEncryptionKeyFromRecoveryCode(
          codes[RECOVERY_CODE_SET_SIZE - 1],
        ),
        factor: factors[FACTOR_COUNT - 1],
        neighbour: factors[0],
      },
    ];

    // Act & Assert
    for (const { name, kek, factor, neighbour } of cases) {
      // The success is half the pair and proves only that the envelope is well
      // formed. Alone it would pass just as happily against envelopes bound to
      // nothing at all.
      await expect(
        unwrapAccountKeys(kek, factor.wrapped, factor.factorId),
        `${name} cannot open its own envelopes.`,
      ).resolves.toBeDefined();

      // The same key, a neighbouring factor's id. The only thing that changed
      // is the associated data, so the refusal is the binding and nothing else
      // — which is what makes a copy lifted into another factor's row fail
      // rather than open there.
      await expect(
        unwrapAccountKeys(kek, factor.wrapped, neighbour.factorId),
        `${name}'s envelopes open under another factor's id.`,
      ).rejects.toThrow();
    }
  });

  it('mints eleven canonical factor identifiers', async () => {
    // Arrange
    const { request } = await driveToRegistration();

    // Act
    const ids = factorsOf(objectBodyOf(request)).map(
      (factor) => factor.factorId,
    );

    // Assert
    expect(ids).toHaveLength(FACTOR_COUNT);

    for (const id of ids) {
      // The value both of that factor's envelopes were sealed against, and the
      // spelling the row hands back. Bind to any other and both envelopes stop
      // opening permanently, with nothing anywhere naming the cause.
      expect(isCanonicalFactorId(id), `${id} is not canonically spelled.`).toBe(
        true,
      );
    }

    // Distinct, because the server refuses a repeated one and because two
    // factors sharing an id share their associated data — which is the binding
    // the test above exists to prove is not shared.
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('holds no unwrapped account key once the wrapping is done', async () => {
    // Arrange
    const codes = await driveToCodes();

    // Act
    // The public surface, read the way a template, an `effect()` or a devtools
    // panel reads it. **This cannot observe a private field and does not claim
    // to.** What it holds is the shape of the surface: that nothing reachable
    // from outside this service is key material. The wipe itself is held by the
    // account keys being locals of the method that draws them, wiped in a
    // `finally` — a fact no test in this runner can see.
    const surface: readonly unknown[] = [
      service.step(),
      service.busy(),
      service.failure(),
      service.codes(),
      service.email(),
      service.restarted(),
    ];

    // Assert
    // The surface is not empty, which is what stops this passing over a flow
    // that published nothing at all.
    expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(keySizedFindings(surface, 'surface')).toEqual([]);

    // The control, and this assertion is the reason the one above means
    // anything: a walk that found nothing anywhere would pass it silently.
    expect(
      keySizedFindings(
        [...surface, new Uint8Array(ACCOUNT_KEY_BYTES)],
        'probe',
      ),
    ).toEqual([`probe[${surface.length}]`]);
  });

  it('refuses to post the same registration twice', async () => {
    // Arrange
    const { request } = await driveToRegistration();

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });
    service.create();

    // Assert
    // The challenge is consumed before anything is verified, so a second POST
    // of this body meets the undifferentiated challenge refusal with certainty.
    // A flow that kept the assembled body would offer a way out that is only a
    // way to be told no twice — and it would re-send a whole card of key
    // custody to do it.
    expect(http.match(REGISTRATION_URL)).toHaveLength(0);
    expect(navigations).toEqual(['/app']);
  });

  it('starts over with a different set of codes', async () => {
    // Arrange
    const first = await driveToCodes();

    // Act
    service.restart();
    const second = await driveToCodes();

    // Assert
    expect(first).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(second).toHaveLength(RECOVERY_CODE_SET_SIZE);

    // Nothing is reused and nothing could be: the challenge is spent, the keys
    // were wiped, and a code carried over from the abandoned attempt would be a
    // code whose envelopes belong to an account that was never created.
    const abandoned = new Set<string>(first);

    for (const code of second) {
      expect(
        abandoned.has(code),
        'A code from the abandoned attempt was published again.',
      ).toBe(false);
    }

    // Set and never cleared, because the codes somebody may have written down a
    // minute ago belong to nothing and that stays true for the rest of the
    // visit.
    expect(service.restarted()).toBe(true);
  });

  // Four answers and three words, and the split is the most consequential
  // branch in the service. A 400 and a 409 are judgements: the request was
  // read, nothing was created, and the codes on screen are certainly dead.
  //
  // A status 0 and a 500 are not. The request may have arrived, committed all
  // thirty rows and had its 201 lost coming back. Telling that person their
  // codes are worthless is telling them to discard the only key to an account
  // they cannot make more codes for — this client has no caller for
  // `POST /api/me/recovery-codes`, so there is no second chance behind the
  // sentence.
  describe('does not tell a lost answer from a refusal', () => {
    it('reads a judged request as refused', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 400, statusText: 'Bad Request' });

      // Assert
      expect(service.failure()).toBe('refused');
    });

    it('reads an account that already exists as a conflict', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 409, statusText: 'Conflict' });

      // Assert
      expect(service.failure()).toBe('conflict');
    });

    it('reads an answer that never arrived as unknown', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.error(new ProgressEvent('error'), {
        status: 0,
        statusText: 'Unknown Error',
      });

      // Assert
      expect(service.failure()).not.toBe('refused');
      expect(service.failure()).toBe('unknown');
    });

    it('reads a server that failed as unknown', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 500, statusText: 'Internal Server Error' });

      // Assert
      expect(service.failure()).not.toBe('refused');
      expect(service.failure()).toBe('unknown');
    });
  });

  // The ordering departure, held here and nowhere else. The written plan minted
  // the card before running the ceremony; cancelling the system passkey sheet
  // is the most common thing that happens on this screen, and minting first
  // leaves that browser holding ten recovery codes for a flow that ended —
  // secrets created for an account that does not exist.
  it.each([
    { failure: 'no-prf' as const, why: 'the authenticator cannot derive keys' },
    { failure: 'cancelled' as const, why: 'the person closed the sheet' },
  ])(
    'mints nothing when the device refuses because $why',
    async ({ failure }) => {
      // Arrange
      ceremonyOutcome = { ok: false, failure };

      // Act
      service.begin();
      service.createPasskey();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the creation options',
      );
      options.flush(CREATION_OPTIONS);

      await eventually(() => service.failure(), 'the refusal to be published');

      // Assert
      expect(service.failure()).toBe(failure);
      // `null` is "not minted", which is a different thing from an empty set and
      // is never collapsed into one.
      expect(service.codes()).toBeNull();
      expect(service.step()).toBe('passkey');
      expect(http.match(REGISTRATION_URL)).toHaveLength(0);
    },
  );

  it('asks for no challenge when the browser cannot run a ceremony', () => {
    // Arrange
    ceremonyAvailable = false;

    // Act
    service.begin();
    service.createPasskey();

    // Assert
    expect(service.failure()).toBe('unsupported');
    expect(service.codes()).toBeNull();
    // Not even the options leg, which is the only position that costs nothing.
    // A challenge is a nonce the server persisted, and on this route it is also
    // the value the account identifier is derived from — so a browser that was
    // never going to finish would otherwise spend one on its way to being told
    // exactly what it is told here for free.
    expect(http.match(() => true)).toHaveLength(0);
  });
});
