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
import { isSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  ACCOUNT_KEY_BYTES,
  importAesGcmKey,
  importHmacSha256Key,
  keyEncryptionKeyFromRecoveryCode,
  unwrapAccountKeys,
  type WrappedAccountKeys,
} from '@app-core/security/account-keys';
import { decodeBase64Url, encodeBase64Url } from '@app-core/security/base64url';
import { isCanonicalFactorId } from '@app-core/security/factor-id';
import {
  ENVELOPE_NONCE_BYTES,
  ENVELOPE_TAG_BYTES,
  ENVELOPE_VERSION,
} from '@app-core/security/key-envelope';
import { canonicalRecoveryCode } from '@app-core/security/recovery-code-canonical';
import {
  RECOVERY_CODE_SET_SIZE,
  recoveryCodeVerifier,
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
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RegisterService } from './register.service';

// **The account keys' zero-filling has no witness in this runner, and it is not
// for want of a seam.** All four `fill(0)` calls in `mintUnder` can be deleted
// with a green suite, and nothing in this file changes that.
//
// The obvious seam does not exist here. `vi.mock` over
// `@app-core/security/account-keys` — by its alias or by a relative path —
// intercepts **nothing** under `@angular/build:unit-test`: the builder
// pre-bundles the application with esbuild, so `register.service.ts`'s import
// of `generateAccountKeys` is resolved before Vitest's module registry is ever
// consulted, and a mocked module is not what either side receives. This was
// measured, not assumed: a factory returning a replaced export leaves both the
// production call site and this file's own import bound to the original. Do not
// re-add one.
//
// The buffers themselves are out of reach for a reason no test can route
// around. `keys.contentKey` and `keys.indexKey` are locals of `mintUnder`, and
// every consumer downstream is handed a **copy** — `sealEnvelope` calls
// `overOwnBuffer`, which is `Uint8Array.from`, so even a spy on
// `crypto.subtle.encrypt` holds a different object from the one that gets
// wiped. Wiping a buffer nobody else references has, by construction, no
// observable effect.
//
// What would make it checkable is a production change and therefore not this
// file's to make: the draw has to arrive through a seam the register screen
// provides — an injectable that hands `mintUnder` its pair — at which point the
// spec supplies the buffers and reads them back after the flow settles, on the
// happy path and on a wrap that rejects halfway. Every other copy of the key
// material named in `account-keys.ts` is a separate gap with a separate fix.
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
  // The verifier the server will find this code by. The passkey's factor sits
  // at the top level of the body and carries none, so this is `null` there —
  // `null` rather than absent, because the assertion that one code's four
  // members were built from one code has to be able to say "this factor carried
  // no verifier at all" instead of comparing `undefined` against a string and
  // reporting a mismatch that names the wrong thing.
  readonly verifier: string | null;
  readonly wrapped: WrappedAccountKeys;
}

// How a failure names one of the eleven. Body order is the order they were
// written in — the passkey, then one factor per code in card order — so an
// index here is a claim about *which* code, which is the whole reason a failure
// is worth naming rather than counting.
function factorName(index: number): string {
  return index === 0 ? 'the passkey' : `the code at index ${index - 1}`;
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
    const verifier: unknown = entry['verifier'];
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

    return {
      factorId,
      verifier: typeof verifier === 'string' ? verifier : null,
      wrapped: { wrappedContentKey, wrappedIndexKey },
    };
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

// A key-encryption key, branded rather than tested with `instanceof`. It is the
// rule `webauthn-encoding.ts` states for `isArrayBuffer` and it matters more
// here: realms differ between this runner, a worker and an iframe, and a
// narrowing that silently goes false would report "nothing parked" for every
// key alike — a refusal that passes because it looked at nothing.
function isCryptoKey(value: unknown): boolean {
  return Object.prototype.toString.call(value) === '[object CryptoKey]';
}

// A record this service assembled, as opposed to one it was injected with.
// The walk below recurses into the first and stops at the second: `pending` is
// an object literal and `codes` is an array of them, which is every shape
// production stores, while stepping into an injected collaborator would walk
// the application graph and report findings about somebody else's field.
function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (!isRecord(value)) {
    return false;
  }

  const prototype: unknown = Object.getPrototypeOf(value);

  return prototype === Object.prototype || prototype === null;
}

// Everything the instance holds, under the names it holds it under.
//
// **TypeScript's `private` is a compile-time rule and nothing else.** It is
// erased before a browser ever sees the class, so `pending` and all five
// signals are ordinary own properties here — which is what makes "the eleven
// key-encryption keys never touch `this`" checkable instead of only stated.
// Symbol-keyed properties are read too; a `#name` field would not be, and no
// reflective API can reach one.
function ownState(target: object): Record<string, unknown> {
  const state: Record<string, unknown> = {};

  for (const name of Object.getOwnPropertyNames(target)) {
    state[name] = Reflect.get(target, name);
  }

  for (const symbol of Object.getOwnPropertySymbols(target)) {
    state[String(symbol)] = Reflect.get(target, symbol);
  }

  return state;
}

// Every place on a walk where the account's key material could be sitting: a
// key-encryption key, a byte view or buffer {@link ACCOUNT_KEY_BYTES} wide, or
// a string this client would decode to that width. Four shapes and not one,
// because the answer to "is the key reachable from here" must not depend on
// which of them somebody happened to park it as. Paths are collected rather
// than a boolean returned, so a finding names where it is.
//
// **A `CryptoKey` is opaque and has no width**, so the three width tests cannot
// see one — and it is the single most valuable thing this flow holds, because
// one of them opens both envelopes of its factor without any account key ever
// being in the clear. It is a finding wherever it is found, with no width to
// argue about.
function custodyFindings(
  value: unknown,
  path: string,
  member: string,
  seen = new WeakSet<object>(),
): readonly string[] {
  if (isCryptoKey(value)) {
    return [path];
  }

  // A signal is a function, so it has to be read before the walk drops it as
  // one. This is what puts the four public readings and the five private
  // writable signals behind them under the same walk, and `email` — a
  // `computed` — with them.
  if (isSignal(value)) {
    return custodyFindings(value(), `${path}()`, member, seen);
  }

  // Before the record branch: a `Uint8Array` is `typeof 'object'`, and read as
  // one it walks into index keys holding numbers and reports nothing at all.
  if (ArrayBuffer.isView(value)) {
    return value.byteLength === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (value instanceof ArrayBuffer) {
    return value.byteLength === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (typeof value === 'string') {
    // `verifier` is the one member legitimately this wide, and the exemption is
    // the same one the wire test carries: `RECOVERY_CODE_VERIFIER_BYTES` and
    // `ACCOUNT_KEY_BYTES` are independently chosen numbers that both happen to
    // be 32, and a verifier is an HKDF output on a branch that unwraps nothing.
    // Ten of them sit inside `pending` between the wrapping and the POST, on
    // purpose. It is exempted **by name and by nothing else**, and deliberately
    // not extended to the two byte branches above — a verifier reaches this
    // instance only as the base64url string the body carries, so one arriving
    // as raw bytes is a new thing and should be looked at.
    if (member === 'verifier') {
      return [];
    }

    const bytes = decodedOrNull(value);

    return bytes !== null && bytes.length === ACCOUNT_KEY_BYTES ? [path] : [];
  }

  if (Array.isArray(value)) {
    if (seen.has(value)) {
      return [];
    }

    seen.add(value);

    return value.flatMap((entry: unknown, index) =>
      custodyFindings(entry, `${path}[${index}]`, member, seen),
    );
  }

  if (isPlainObject(value)) {
    if (seen.has(value)) {
      return [];
    }

    seen.add(value);

    return Object.entries(value).flatMap(([key, entry]: [string, unknown]) =>
      custodyFindings(entry, `${path}.${key}`, key, seen),
    );
  }

  return [];
}

// Where the account's keys go once the account exists, replaced by three
// counters.
//
// **Stubbed rather than spied-and-called-through**, unlike
// `sign-in.service.spec.ts`, and the difference is that `adopt` makes no
// request: there is nothing for a census of outgoing traffic to lose by
// replacing it. What the stub buys is the two `CryptoKey` objects themselves,
// held where the fingerprints below can interrogate them.
//
// All three members, because two of the rules here are about what did *not*
// happen. `unlock` from this flow would be a browser that just wrote eleven
// envelopes going back to read them, and `lock` would be a registration that
// ended by throwing away the keys it had just created — neither is a call a
// spy on `adopt` alone could see.
class CustodyStub {
  public adopt = vi.fn<(contentKey: CryptoKey, indexKey: CryptoKey) => void>();
  public unlock = vi.fn<(keyEncryptionKey: CryptoKey) => void>();
  public lock = vi.fn<() => void>();
}

function touchedMembersOf(custody: CustodyStub): readonly string[] {
  return (['adopt', 'unlock', 'lock'] as const).filter(
    (name) => custody[name].mock.calls.length > 0,
  );
}

// **How an opaque key is identified, and the only way one can be.** A
// `CryptoKey` out of either door is non-extractable, so no API in the platform
// reads its bytes back — which is the property the whole design rests on and
// also, for a moment, the reason nothing could check *which* pair reached
// custody.
//
// What is left is that both primitives are deterministic. AES-GCM under a fixed
// key, nonce and plaintext produces one ciphertext and only ever that one;
// HMAC-SHA-256 over a fixed message produces one tag. So a key can be
// fingerprinted by using it, and two fingerprints agree exactly when the two
// keys hold the same bytes. Neither constant below is a secret and neither is
// reused for anything: the nonce is spent on one plaintext per key, in a test,
// against material that exists for the length of a test.
const FINGERPRINT_NONCE = new Uint8Array(ENVELOPE_NONCE_BYTES).fill(0x5a);
const FINGERPRINT_MESSAGE = new TextEncoder().encode(
  'budgetoid/spec/which-pair-reached-custody',
);

// The content key, used as a content key. It comes out of `importAesGcmKey`
// with `encrypt` among its usages, so this is the key doing the one thing it
// exists to do.
async function contentFingerprint(key: CryptoKey): Promise<string> {
  const sealed = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv: FINGERPRINT_NONCE },
    key,
    FINGERPRINT_MESSAGE,
  );

  return encodeBase64Url(new Uint8Array(sealed));
}

// The index key, used as an index key. A blind index is an HMAC, so signing is
// exactly what the key was imported for — and a key that came through the wrong
// door cannot reach this function at all, which is a second assertion the two
// tests below get for free.
async function indexFingerprint(key: CryptoKey): Promise<string> {
  const mac = await crypto.subtle.sign('HMAC', key, FINGERPRINT_MESSAGE);

  return encodeBase64Url(new Uint8Array(mac));
}

// The hash a key's algorithm names, or `null` for an algorithm that names none.
//
// Narrowed rather than asserted. `key.algorithm` is typed `KeyAlgorithm`, which
// declares a name and nothing else, and `as HmacKeyAlgorithm` would *claim* the
// shape at exactly the point where the interesting answer is that the shape is
// something else — a content key sent through the wrong door has no `hash` at
// all, and the assertion would turn that into an undefined dereference instead
// of a finding.
function hashNameOf(key: CryptoKey): string | null {
  const algorithm: unknown = key.algorithm;

  if (typeof algorithm !== 'object' || algorithm === null) {
    return null;
  }

  if (!('hash' in algorithm)) {
    return null;
  }

  const hash: unknown = algorithm.hash;

  if (typeof hash !== 'object' || hash === null || !('name' in hash)) {
    return null;
  }

  return typeof hash.name === 'string' ? hash.name : null;
}

// The pair one factor's two envelopes actually carry, as keys of the same two
// kinds custody is handed. This is the account's real pair, recovered the way
// the product will recover it — off the wire, under the key-encryption key that
// factor derives — and it is what every claim about *which* pair is measured
// against.
async function pairFrom(
  factor: BodyFactor,
  keyEncryptionKey: CryptoKey,
): Promise<{ readonly contentKey: CryptoKey; readonly indexKey: CryptoKey }> {
  const opened = await unwrapAccountKeys(
    keyEncryptionKey,
    factor.wrapped,
    factor.factorId,
  );

  // Through the production doors, which wipe the material they are handed on
  // the way past. Nothing here needs the bytes afterwards, and a fixture that
  // kept them would be this file holding the account's whole keyspace for the
  // rest of the run.
  return {
    contentKey: await importAesGcmKey(opened.contentKey),
    indexKey: await importHmacSha256Key(opened.indexKey),
  };
}

describe('RegisterService', () => {
  let http: HttpTestingController;
  let service: RegisterService;
  let custody: CustodyStub;
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
  // Every set of creation options the ceremony was handed, in order.
  //
  // It exists for one assertion and would be padding without it. `Continue` now
  // fetches the challenge, so the press after it must issue **no** options
  // request — and an absence of requests is exactly what a flow that stopped
  // dead also looks like. This is the positive half: the ceremony really ran,
  // once, against the challenge the earlier press fetched.
  let ceremonyChallenges: PasskeyCreationOptionsJson[];
  let navigations: string[];

  beforeEach(async () => {
    keyEncryptionKey = await importKeyEncryptionKey();
    custody = new CustodyStub();
    ceremonyAvailable = true;
    ceremonyOutcome = {
      ok: true,
      value: { payload: REGISTRATION_PAYLOAD, keyEncryptionKey },
    };
    ceremonyChallenges = [];
    navigations = [];

    // The one stub that has to exist: `available()` and `createPasskey()` both
    // touch `navigator.credentials`, which this runner does not implement, so
    // the seam that owns them is replaced and nothing below it is.
    const ceremony: Pick<
      WebauthnCeremonyService,
      'available' | 'createPasskey'
    > = {
      available: () => ceremonyAvailable,
      createPasskey: (
        options: PasskeyCreationOptionsJson,
      ): Promise<PasskeyCeremonyResult<PasskeyRegistrationCeremony>> => {
        ceremonyChallenges.push(options);

        return Promise.resolve(ceremonyOutcome);
      },
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
        // Root-provided in production, and listed here anyway — overriding the
        // real one rather than resolving it. The real service would take the
        // keys perfectly well and then hold them where nothing in this file can
        // ask it anything about them, because no public member of it returns a
        // key and none ever will. The stub is the only seam through which
        // "which pair reached custody" is a question at all.
        { provide: AccountKeyCustodyService, useValue: custody },
        // Component-provided in production, so it is listed rather than
        // resolved from the root injector.
        RegisterService,
      ],
    });

    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(RegisterService);
  });

  // `Continue`, on the introduction. **This is where the options request leaves
  // now**, and the split below exists because of it: the press that fetches the
  // challenge and the press that spends it are two acts on two screens, and a
  // helper that ran both would make "the second press asks for nothing" an
  // unaskable question.
  async function pressContinue(): Promise<void> {
    service.begin();

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);
  }

  // `Create a passkey`, with the challenge already in hand — **flushing
  // nothing**, because nothing is asked for. The ceremony, the account keys,
  // the card and the eleven wraps all happen in here, on real WebCrypto.
  function runCeremony(): Promise<readonly RecoveryCode[]> {
    service.createPasskey();

    return eventually(
      (): readonly RecoveryCode[] | null => service.codes(),
      'the minted recovery codes to be published',
    );
  }

  // The same press with no challenge in hand, which is the shape every path
  // that re-enters the passkey step takes: a restart and every `Try again`
  // there. The prefetched nonce was spent by the ceremony that ran before them,
  // so this leg fetches its own.
  async function runCeremonyFetchingAChallenge(): Promise<
    readonly RecoveryCode[]
  > {
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

  // From the introduction to ten codes on screen. Every test below starts here
  // because that is where this flow's secrets exist — the account keys have
  // been drawn, wrapped eleven times and wiped, and the body that will be
  // posted is assembled — and none of them can reach any of it any earlier.
  async function driveToCodes(): Promise<readonly RecoveryCode[]> {
    await pressContinue();

    return runCeremony();
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

  // The key one factor's pair was sealed under: the passkey's at index zero,
  // and after it the key each code derives for itself. Body order is the order
  // the eleven were written in, so this function *is* the pairing claim — hand
  // it an index and it produces the only key that factor's envelopes may open
  // under.
  function keyOf(
    index: number,
    codes: readonly RecoveryCode[],
  ): Promise<CryptoKey> {
    return index === 0
      ? Promise.resolve(keyEncryptionKey)
      : keyEncryptionKeyFromRecoveryCode(codes[index - 1]);
  }

  // No `http.verify()` teardown, and the omission is deliberate: this test ends
  // with the registration request outstanding on purpose. The request *is* the
  // subject, and answering it would drive the flow on into whatever a created
  // account does next — a navigation, a session probe — for no gain here.

  it('sends no recovery code to the server', async () => {
    // Arrange
    // Spelled out rather than driven through the helpers, because this is the
    // one test in the flow that has to be readable end to end without opening
    // anything else. The two presses are in the order a person makes them, and
    // the challenge arrives on the first of them.
    service.begin();

    const options = await eventually(
      () => http.match(OPTIONS_URL)[0] ?? null,
      'the request for the creation options',
    );
    options.flush(CREATION_OPTIONS);

    // Act
    service.createPasskey();

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
    // **All eleven, not a sample of three.** The passkey, the first code and
    // the last were opened here before and factors two through nine were opened
    // by nothing in the system — so a per-factor draw that skipped the ends was
    // invisible, and so was every mistake confined to the middle of the card.
    // Eleven unwraps cost milliseconds; the alternative is discovering it from
    // somebody who redeemed one of the eight months later.
    const opened = await Promise.all(
      factors.map(async (factor, index) => {
        try {
          return await unwrapAccountKeys(
            await keyOf(index, codes),
            factor.wrapped,
            factor.factorId,
          );
        } catch {
          // GCM refuses without a word about why, so the name is the whole of
          // what a reader gets: which of the eleven could not open its own pair.
          throw new Error(
            `${factorName(index)} cannot open its own envelopes.`,
          );
        }
      }),
    );

    // Assert
    expect(opened).toHaveLength(FACTOR_COUNT);

    const [passkey] = opened;
    expect(passkey.contentKey).toHaveLength(ACCOUNT_KEY_BYTES);
    expect(passkey.indexKey).toHaveLength(ACCOUNT_KEY_BYTES);

    opened.forEach((keys, index) => {
      expect(
        sameBytes(passkey.contentKey, keys.contentKey),
        `${factorName(index)} opens a different content key from the ` +
          "passkey's.",
      ).toBe(true);
      expect(
        sameBytes(passkey.indexKey, keys.indexKey),
        `${factorName(index)} opens a different index key from the passkey's.`,
      ).toBe(true);
    });
  });

  // **One code's four members are built in one scope, from one code** — the
  // rule `register.service.ts:350-366` argues, executed. The obvious
  // implementation derives ten key-encryption keys into an array, wraps ten
  // times into a second, and zips those against the verifiers at post time. A
  // mispairing there satisfies every type, every count, every round trip and
  // every constraint the server has: the set validates, the account is created,
  // a session is handed over, and it is discovered by somebody who redeemed a
  // code months later and found the account still locked.
  //
  // Both halves are here and neither implies the other. A verifier zipped from
  // a parallel array leaves the envelopes opening perfectly under the code they
  // sit beside; a key-encryption key zipped from one leaves the verifier
  // matching perfectly. Only checking each code against both members catches
  // either.
  it('gives every code its own verifier and its own envelopes', async () => {
    // Arrange
    const { request, codes } = await driveToRegistration();
    const factors = factorsOf(objectBodyOf(request));

    // Act
    // Derived here from the codes the screen published, on the same branch
    // `mintRecoveryCodeSet` used — a deterministic HKDF over the code, so an
    // equal verifier is the same code and nothing else.
    const expected = await Promise.all(codes.map(recoveryCodeVerifier));

    // Assert
    expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);
    // The passkey's factor is not a code's and carries no verifier. A body
    // where it did would mean a code's four members had been spread across the
    // wrong entry entirely, which every count below would still pass.
    expect(factors[0].verifier).toBeNull();

    for (const [index, code] of codes.entries()) {
      const factor = factors[index + 1];

      // The value the server finds this code by. Paired with another code's,
      // the code the person kept redeems as nothing at all.
      expect(
        factor.verifier,
        `The code at index ${index} was submitted under another code's ` +
          'verifier.',
      ).toBe(expected[index]);

      // And the two envelopes filed beside it, opened by the key that same code
      // derives. Paired with another code's, the code redeems and then unwraps
      // nothing — an account that authenticates and stays unreadable.
      await expect(
        unwrapAccountKeys(
          await keyEncryptionKeyFromRecoveryCode(code),
          factor.wrapped,
          factor.factorId,
        ),
        `The code at index ${index} does not open the envelopes filed beside ` +
          'it.',
      ).resolves.toBeDefined();
    }
  });

  it('binds every envelope to its own factor', async () => {
    // Arrange
    const { request, codes } = await driveToRegistration();
    const factors = factorsOf(objectBodyOf(request));

    // Act & Assert
    // **Every factor, each against the next one round the ring.** Three were
    // checked here before — the passkey, the first code and the last — which
    // left the eight in the middle bound by nothing. Going round the ring makes
    // each of the eleven both a subject and somebody else's neighbour, so none
    // of them is left out of either role.
    expect(factors).toHaveLength(FACTOR_COUNT);

    for (const [index, factor] of factors.entries()) {
      const kek = await keyOf(index, codes);
      const neighbour = factors[(index + 1) % FACTOR_COUNT];

      // The success is half the pair and proves only that the envelope is well
      // formed. Alone it would pass just as happily against envelopes bound to
      // nothing at all.
      await expect(
        unwrapAccountKeys(kek, factor.wrapped, factor.factorId),
        `${factorName(index)} cannot open its own envelopes.`,
      ).resolves.toBeDefined();

      // The same key, a neighbouring factor's id. The only thing that changed
      // is the associated data, so the refusal is the binding and nothing else
      // — which is what makes a copy lifted into another factor's row fail
      // rather than open there.
      await expect(
        unwrapAccountKeys(kek, factor.wrapped, neighbour.factorId),
        `${factorName(index)}'s envelopes open under another factor's id.`,
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

  it('parks no key material on the instance', async () => {
    // Arrange
    const codes = await driveToCodes();

    // Act
    // **The instance's own properties, not the declared surface.** TypeScript's
    // `private` is erased at compile time — `stepSignal`, `codesSignal` and
    // `pending` are all present by name in the emitted JavaScript — so the one
    // custody rule that had nothing but a comment behind it is checkable here:
    // the eleven key-encryption keys are expressions inside `mintUnder` and
    // never fields, and the account keys are locals of the same method.
    //
    // Read at the moment of maximum exposure: `pending` assembled, ten codes
    // published, the wrapping just finished.
    //
    // **What it still cannot reach** is anything the instance never stores — a
    // closure variable of `mintUnder`, a module-level `let`, a field on an
    // injected collaborator, and a `#name` field, which no reflective API can
    // see. It is a statement about `this` and about nothing else.
    const state = ownState(service);

    // Assert
    // The flow got somewhere, which is what stops this passing over a run that
    // published nothing at all.
    expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);
    // Deliberately not a count: a walk over seventeen own properties naming
    // where a key is found is worth more in a failure than `false`.
    expect(custodyFindings(state, 'service', 'service')).toEqual([]);

    // The control, and it is the reason the line above means anything: a walk
    // that found nothing anywhere would satisfy a refusal silently. The four
    // members stand for the four mutations this test exists to catch — a
    // key-encryption key parked in the wrap loop, the account keys held back
    // "for the encryption epic", and a raw key published under a member nobody
    // would have thought to list. `verifier` is in here to hold the exemption
    // to exactly one member: widen it and `stray` stops being reported.
    const probe = {
      kek: keyEncryptionKey,
      keys: { contentKey: new Uint8Array(ACCOUNT_KEY_BYTES) },
      verifier: encodeBase64Url(new Uint8Array(ACCOUNT_KEY_BYTES)),
      stray: encodeBase64Url(new Uint8Array(ACCOUNT_KEY_BYTES)),
    };

    expect(custodyFindings(probe, 'probe', 'probe')).toEqual([
      'probe.kek',
      'probe.keys.contentKey',
      'probe.stray',
    ]);
  });

  // **The keys reach custody on the 201 and at no earlier instant.**
  //
  // They have existed since the ceremony: drawn once, wrapped eleven times,
  // imported through their two doors, and sitting on the instance while ten
  // codes are on screen. That is where a reader will move the hand-over to,
  // because it is where the values are. Moving it there needs three clearing
  // sites — the restart, the failed POST, and a destroy hook this service does
  // not have — and the one that gets forgotten leaves the keys of an account
  // that was never created on a root-provided singleton for the life of the
  // tab, with nothing on screen and nothing red.
  //
  // The other half of the case is *which* pair arrived, and it is checkable at
  // all only because both keys are deterministic when used. See the
  // fingerprints above.
  it('hands the account keys to custody when the account is created', async () => {
    // Arrange
    const { request, codes } = await driveToRegistration();
    const factors = factorsOf(objectBodyOf(request));

    // The whole card is minted, the body is assembled, the request is in
    // flight — and nothing has been created. Custody has been told nothing.
    expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(touchedMembersOf(custody)).toEqual([]);

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // Once, and nothing else was said. `unlock` here would be a browser going
    // back to read envelopes it wrote thirty milliseconds ago — the round trip
    // `adopt` exists to make unnecessary — and `lock` would be a registration
    // that ended by throwing away what it had just created.
    expect(touchedMembersOf(custody)).toEqual(['adopt']);
    expect(custody.adopt).toHaveBeenCalledTimes(1);

    const [contentKey, indexKey] = custody.adopt.mock.calls[0];

    // **Two keys of two kinds, and the second one is the assertion a reader
    // will delete.** The content key encrypts, so it comes through the AES-GCM
    // door. The index key is what a blind index is computed under, and a blind
    // index is HMAC-SHA-256 — so sending it through `importAesGcmKey` because
    // that call is already written a line above produces an object that cannot
    // sign a single index, cannot be corrected afterwards, and is wrong in a
    // way nothing else in this suite looks at. It costs one line to say so.
    expect(contentKey.algorithm.name).toBe('AES-GCM');
    expect(indexKey.algorithm.name).toBe('HMAC');
    expect(hashNameOf(indexKey)).toBe('SHA-256');

    // Non-extractable, both, which is what stops a later caller reading the
    // account's whole keyspace out of the service holding it.
    expect(contentKey.extractable).toBe(false);
    expect(indexKey.extractable).toBe(false);

    // And they are the account's own pair — the one the eleven envelopes in the
    // body were sealed over — rather than two keys of the right shape. Measured
    // against the *first recovery code's* factor rather than the passkey's,
    // because the passkey's key-encryption key is a fixture this file holds
    // across every attempt while a code's is derived from a code that was minted
    // for this attempt alone. Opening under it proves the pair came from this
    // drawing and no other.
    const account = await pairFrom(factors[1], await keyOf(1, codes));

    expect(
      await contentFingerprint(contentKey),
      'Custody was handed a content key the account’s envelopes do not ' +
        'carry.',
    ).toBe(await contentFingerprint(account.contentKey));
    expect(
      await indexFingerprint(indexKey),
      'Custody was handed an index key the account’s envelopes do not ' +
        'carry.',
    ).toBe(await indexFingerprint(account.indexKey));
  });

  // **The sharpest case in the file, and the one whose failure names nothing.**
  //
  // An abandoned attempt drew its own pair and wrapped it under eleven factor
  // identifiers that died with it. Carried forward, that pair is adopted on the
  // *second* attempt's 201 — and the account is then created under one drawing
  // and unlocked with another. Every envelope in `wrapped_account_keys` belongs
  // to the second pair; the browser is holding the first. Nothing on either
  // side of the wire can see it: the set validates, the account is created, a
  // session is handed over, the screen goes to `/app`. What is broken is every
  // read the account will ever do, permanently, with no error naming the cause.
  //
  // The fingerprints are what make this checkable rather than merely stated:
  // both attempts hand custody two `CryptoKey` objects of identical shape, and
  // shape is the whole of what an opaque key reveals.
  it('adopts the pair the account was created under, never an abandoned one', async () => {
    // Arrange
    const abandoned = await driveToCodes();

    // The restart lands on the passkey step, so the next press fetches its own
    // challenge: the one `Continue` prefetched was spent by the ceremony that
    // has just run.
    service.restart();

    const kept = await runCeremonyFetchingAChallenge();

    service.create();

    const request = await eventually(
      () => http.match(REGISTRATION_URL)[0] ?? null,
      'the registration request',
    );
    const factors = factorsOf(objectBodyOf(request));

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // Two attempts really happened, which is what stops everything below
    // passing over a run that restarted into nothing. Twenty distinct codes:
    // the second draw shares not one value with the first.
    expect(abandoned).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(kept).toHaveLength(RECOVERY_CODE_SET_SIZE);
    expect(new Set<string>([...abandoned, ...kept]).size).toBe(
      2 * RECOVERY_CODE_SET_SIZE,
    );

    expect(custody.adopt).toHaveBeenCalledTimes(1);

    const [contentKey, indexKey] = custody.adopt.mock.calls[0];

    // The pair the *posted* body carries, opened under a code from the set the
    // person was left holding. If the abandoned attempt's keys had been carried
    // forward, both fingerprints would differ and neither would say why —
    // which is exactly the failure mode in production, and the reason the
    // messages below have to name it.
    const account = await pairFrom(factors[1], await keyOf(1, kept));

    expect(
      await contentFingerprint(contentKey),
      'The account was created under one drawing of the keys and unlocked ' +
        'with another.',
    ).toBe(await contentFingerprint(account.contentKey));
    expect(
      await indexFingerprint(indexKey),
      'The account was created under one drawing of the keys and unlocked ' +
        'with another.',
    ).toBe(await indexFingerprint(account.indexKey));
  });

  // **Nothing is adopted for an account that was not created**, and the two
  // statuses are here for opposite reasons.
  //
  // A 400 is a judgement: the request was read and refused, nothing was
  // written, and there is no account these keys could belong to. A 500 leaves
  // the question open — the thirty rows may have committed and had the answer
  // lost coming back — and keeping the keys "just in case" is the tempting
  // edit. It is wrong twice over: nothing here can adopt keys for an account it
  // cannot confirm exists, and the way back into an account that may have been
  // created is a passkey assertion on `/welcome`, which derives its own
  // key-encryption key and unwraps from the row this attempt would have
  // written. Custody taken on a guess is custody nobody can verify or clear.
  describe('adopts nothing from a registration the server did not create', () => {
    it.each([
      {
        status: 400,
        statusText: 'Bad Request',
        why: 'the request was judged and refused',
      },
      {
        status: 500,
        statusText: 'Internal Server Error',
        why: 'the answer says nothing about what was written',
      },
    ])('adopts nothing when $why', async ({ status, statusText }) => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status, statusText });

      await eventually(() => service.failure(), 'the failure to be published');

      // Assert
      expect(touchedMembersOf(custody)).toEqual([]);
      expect(navigations).toEqual([]);
    });
  });

  // The same argument `sign-in.service.spec.ts` makes over its own custody
  // call, on the other flow that makes one — and here the stakes are higher,
  // because two statements follow the hand-over rather than one.
  //
  // An exception thrown out of an RxJS `next` handler is **not** routed to the
  // `error` callback beside it: it is reported out of band as an unhandled
  // error, and `next()` returns to the producer as though nothing happened.
  // What it does do is what any throw does — the statements after it never run.
  // Unguarded, a custody that threw would leave the provider's bearer token
  // still held and the navigation never made: somebody standing on the register
  // screen holding a created account and a card of live codes, with the screen
  // saying nothing, because as far as this service is concerned the
  // registration succeeded.
  it('finishes the registration even when custody blows up', async () => {
    // Arrange
    custody.adopt.mockImplementation(() => {
      throw new Error('the account keys could not be taken into custody');
    });

    const { request } = await driveToRegistration();

    // Act
    request.flush(null, { status: 201, statusText: 'Created' });

    await eventually(
      () => navigations[0] ?? null,
      'the navigation into the app',
    );

    // Assert
    // It really did throw, which is what stops this passing over a mock that
    // was never reached.
    expect(custody.adopt).toHaveBeenCalledTimes(1);
    expect(custody.adopt.mock.results[0].type).toBe('throw');

    // And the flow is untouched by it: the person is in the app, the screen
    // says nothing new, and the button is live again.
    expect(navigations).toEqual(['/app']);
    expect(service.failure()).toBeNull();
    expect(service.busy()).toBe(false);
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
    // A restart lands on the passkey step, so the next press is `Create a
    // passkey` and it fetches its own challenge: the one `Continue` prefetched
    // was spent by the ceremony that ran a moment ago, and `restart()` drops it
    // for that reason.
    service.restart();
    const second = await runCeremonyFetchingAChallenge();

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

    // **And the restart on its own says nothing about the server.** No POST has
    // left this browser, so nothing can have been written and the question of
    // whether an account exists is not open. Only an answer that never arrived
    // opens it — a fact about the request that ended, never about a button
    // being pressed, which is what the four tests below hold one status at a
    // time. Forking a 409's two readings on the press instead told somebody
    // whose first attempt was *refused* that it had created their account.
    expect(service.mayHaveCreatedAccount()).toBe(false);
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
  //
  // The same split decides `mayHaveCreatedAccount`, which is why every test here
  // asserts both: `unknown` is the one word under which anything may have been
  // written, and the screen's two readings of a 409 fork on it. The asymmetry is
  // the whole point — a judgement is the server having looked and said no.
  describe('does not tell a lost answer from a refusal', () => {
    it('reads a judged request as refused', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 400, statusText: 'Bad Request' });

      // Assert
      expect(service.failure()).toBe('refused');
      // A 400 leaves the handler before a row is written, so the question of
      // whether an account exists is not open and never was.
      expect(service.mayHaveCreatedAccount()).toBe(false);
    });

    it('reads an account that already exists as a conflict', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 409, statusText: 'Conflict' });

      // Assert
      expect(service.failure()).toBe('conflict');
      // A 409 refuses the request it answers, so nothing exists that did not
      // exist before it. This is the plain reading of a 409, and the flag being
      // false here is what the screen renders it by.
      expect(service.mayHaveCreatedAccount()).toBe(false);
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
      // A status 0 says nothing about whether thirty rows were committed and a
      // 201 was lost coming back, so the question stays open for the rest of the
      // visit and nothing later closes it.
      expect(service.mayHaveCreatedAccount()).toBe(true);
    });

    it('reads a server that failed as unknown', async () => {
      // Arrange
      const { request } = await driveToRegistration();

      // Act
      request.flush(null, { status: 500, statusText: 'Internal Server Error' });

      // Assert
      expect(service.failure()).not.toBe('refused');
      expect(service.failure()).toBe('unknown');
      // A 5xx is not a judgement either: the write may have gone through and the
      // failure be everything after it.
      expect(service.mayHaveCreatedAccount()).toBe(true);
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
      // The challenge, fetched by the press before this one. It has to be in
      // hand before the ceremony is asked for anything: `createPasskey()`
      // no-ops while the options request is in flight, so the two presses run
      // together reach the authenticator not at all.
      await pressContinue();

      // Act
      service.createPasskey();

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

    // The introduction still moves on, and it moves on with nothing in hand.
    // That is what keeps `unsupported` the passkey step's word: read here
    // instead, a browser that cannot run WebAuthn would be refused on the
    // introduction and told nothing about why the way out — an assertion on
    // `/welcome` — needs the same WebAuthn it does not have.
    expect(service.step()).toBe('passkey');
    expect(service.busy()).toBe(false);
    expect(service.failure()).toBeNull();

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

  // The options leg has two words, and the split is not the POST leg's split
  // read again on a different route.
  //
  // `POST /api/registration/options` refuses with a 409 when the provider
  // identity already has an account, and it refuses *before* it issues a
  // challenge — which is the whole reason it is worth telling apart here.
  // Nothing is minted, nothing is spent, and the browser never runs
  // `navigator.credentials.create()`, so the person is not left with a stray
  // passkey on their device for an account that was never created. Every other
  // answer on this leg is `start-failed`: the server never issued a challenge.
  //
  // **`unknown` is the word that must never appear here**, which is why the
  // second test exists at all. On the POST leg it means "thirty rows may have
  // been committed and the answer lost coming back"; on this leg nothing is
  // ever created, so it would be a lie — and it is the lie that decides what
  // the screen tells somebody to do with ten codes. Nothing is minted on this
  // leg either way, so `mayHaveCreatedAccount` stays false through both.
  describe('tells an account that already exists from a start that failed', () => {
    // **The third answer this leg has, and the one the flow had no word for.**
    // Both registration routes are declared on the provider scheme and nothing
    // else, so a 401 here is the API refusing the bearer this browser attached:
    // an id token that lapsed while the screen was open, one already spent, one
    // minted for another audience. `start-failed` said "Budgetoid couldn't reach
    // the server", which is wrong twice — the server answered, and the `Try
    // again` beside it re-sends the same dead token forever. Nothing on the
    // screen could fix it: the one control that can is the provider's, and it
    // renders only for a browser holding no token at all.
    //
    // Measured rather than reasoned about: `/register` loaded with an id token
    // 71 minutes past its expiry answered `401 Bearer error="invalid_token",
    // error_description="The token expired at ..."`.
    it('reads a refused provider token as its own word', async () => {
      // Arrange
      service.begin();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the creation options',
      );

      // Act
      options.flush(null, { status: 401, statusText: 'Unauthorized' });

      // Assert
      // **Not `start-failed`, which is the whole of the change.** The two words
      // differ in what they ask of the reader: one says press again, the other
      // says go and sign in with Google — and pressing again is the one act
      // guaranteed to end up here again.
      expect(service.failure()).toBe('provider-token-refused');
      // Nothing was created and nothing is open. The request never reached a
      // handler — the provider scheme refuses it above the endpoint — so no
      // challenge was issued and no row could have been written.
      expect(service.mayHaveCreatedAccount()).toBe(false);
      expect(service.codes()).toBeNull();
      // And the reader has not moved. The sentence is about the Google sign-in
      // named on this step, and the way out of it is the control this step
      // already has for a browser holding no token.
      expect(service.step()).toBe('intro');
      expect(service.busy()).toBe(false);
      expect(http.match(REGISTRATION_URL)).toHaveLength(0);
    });

    // The same answer to the refetch, which is the other place this leg is
    // asked. A token lives an hour and this flow can sit on the passkey step
    // for longer: a restart, a closed system sheet, a device that did not
    // finish. Nothing has been minted on that path either — `restart()` drops
    // the codes and a failed ceremony never made any — so the word is the same
    // and the step it lands on is the only difference.
    it('reads a refused provider token on the refetch too', async () => {
      // Arrange
      await pressContinue();
      await runCeremony();
      service.restart();
      service.createPasskey();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the refetched request for the creation options',
      );

      // Act
      options.flush(null, { status: 401, statusText: 'Unauthorized' });

      // Assert
      expect(service.failure()).toBe('provider-token-refused');
      expect(service.step()).toBe('passkey');
      expect(service.busy()).toBe(false);
      expect(service.codes()).toBeNull();
      expect(service.mayHaveCreatedAccount()).toBe(false);
    });

    // **A 403 is not a token problem and must not borrow the word.** It is
    // `FirstPartyRequestMiddleware` refusing a request that carried no
    // `X-Budgetoid-Client` header — a defect in this client, answered to a
    // browser whose provider token is perfectly good. Telling that person to
    // sign in with Google again sends them through an exchange that changes
    // nothing and returns them to the same refusal.
    it('reads a client refused by the CSRF control as a start that failed', async () => {
      // Arrange
      service.begin();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the creation options',
      );

      // Act
      options.flush(null, { status: 403, statusText: 'Forbidden' });

      // Assert
      expect(service.failure()).toBe('start-failed');
      expect(service.mayHaveCreatedAccount()).toBe(false);
      expect(service.step()).toBe('intro');
    });

    it('reads a 409 before the challenge as a conflict', async () => {
      // Arrange
      // One press, and it is `Continue`. The refusal belongs to the
      // introduction now: the server answers 409 above its own `IssueAsync`,
      // so the answer exists at the first press and reading it at the second
      // means somebody was promised an account under an address, sent through
      // a screen about authenticators, and refused there.
      service.begin();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the creation options',
      );

      // Act
      options.flush(null, { status: 409, statusText: 'Conflict' });

      // Assert
      expect(service.failure()).toBe('conflict');
      // The refusal is about the account and not about the request that met
      // it: this browser has posted nothing, so the question of whether it
      // created anything is not open and never was. That flag is what the
      // screen's two readings of a 409 fork on, and a leg that set it here
      // would tell somebody Budgetoid could not tell — on the one path where
      // it can.
      expect(service.mayHaveCreatedAccount()).toBe(false);
      // Refused above the challenge, so nothing downstream of it ran.
      expect(service.codes()).toBeNull();
      // **And the reader has not moved.** A refusal published against the
      // passkey step is one somebody has to be walked back from, and this
      // sentence — this address already has an account — only makes sense
      // beside the address, which is on the introduction. `begin()` leaves the
      // step alone on its error branch for exactly that.
      expect(service.step()).toBe('intro');
      expect(service.busy()).toBe(false);
      expect(http.match(REGISTRATION_URL)).toHaveLength(0);
    });

    it.each([
      {
        answer: (request: TestRequest): void => {
          request.flush(null, {
            status: 500,
            statusText: 'Internal Server Error',
          });
        },
        what: 'a server that failed',
      },
      {
        answer: (request: TestRequest): void => {
          request.error(new ProgressEvent('error'), {
            status: 0,
            statusText: 'Unknown Error',
          });
        },
        what: 'an answer that never arrived',
      },
    ])('reads $what as a start that failed', async ({ answer }) => {
      // Arrange
      service.begin();

      const options = await eventually(
        () => http.match(OPTIONS_URL)[0] ?? null,
        'the request for the creation options',
      );

      // Act
      answer(options);

      // Assert
      // The control that stops the 409 above from becoming "every options
      // error is a conflict", and the one that stops this leg from borrowing
      // the POST leg's mapper — which would answer `unknown` to both of these.
      expect(service.failure()).toBe('start-failed');
      expect(service.mayHaveCreatedAccount()).toBe(false);
      expect(service.codes()).toBeNull();
      // The same standstill the 409 above holds, and it is the half of this
      // leg's contract a mapper cannot express: `startFailureOf` decides the
      // *word*, and leaving the step where it was is what makes the word land
      // on a screen the sentence is true of. `Try again` on the introduction is
      // another `Continue`; the same press one step on would spend a challenge.
      expect(service.step()).toBe('intro');
      expect(service.busy()).toBe(false);
      expect(http.match(REGISTRATION_URL)).toHaveLength(0);
    });
  });

  // What `Continue` became, and it is a change of act rather than of copy: the
  // press that used to move a reader on now asks the server a question first.
  //
  // `POST /api/registration/options` answers 409 when the provider subject
  // already holds an account, and it answers **above** its own
  // `challengeStore.IssueAsync` — so nothing is minted, nothing is spent, and
  // the answer already exists at the first press. Asked only from the passkey
  // step, it reached somebody who had read "your account will be created under
  // <address>", pressed `Continue`, read a screen about authenticators and
  // pressed again. The server knew the whole time.
  //
  // The four tests below are the four things that press can do. The two
  // refusals are held one status at a time in the describe above, on the same
  // request; these are the ones about *where the reader is standing* while it
  // happens.
  describe('asks whether an account exists before it moves anybody on', () => {
    it('makes the request and stays on the introduction', () => {
      // Act
      service.begin();

      // Assert
      // The request left, which is the whole of the change.
      expect(http.match(OPTIONS_URL)).toHaveLength(1);
      // And nothing else did. Moving the reader on and asking at the same time
      // would put the answer back where it was: on a screen whose sentences —
      // "this address already has an account", "the server never answered" —
      // are about the address the introduction shows and this one does not.
      expect(service.step()).toBe('intro');
      // Said out loud, because this press now waits on a network round trip and
      // the introduction had nothing to wait on before. A control that renders
      // unchanged for a second reads as one that did nothing, which is what
      // invites the second press.
      expect(service.busy()).toBe(true);
      expect(service.failure()).toBeNull();
      // Nothing downstream: no ceremony, no card, no account.
      expect(ceremonyChallenges).toEqual([]);
      expect(service.codes()).toBeNull();
      expect(service.mayHaveCreatedAccount()).toBe(false);
    });

    it('moves on once the challenge is in hand', async () => {
      // Act
      await pressContinue();

      // Assert
      expect(service.step()).toBe('passkey');
      expect(service.busy()).toBe(false);
      expect(service.failure()).toBeNull();
      // The step moved and nothing secret was made getting there. What the
      // passkey step is for is still ahead of the reader.
      expect(ceremonyChallenges).toEqual([]);
      expect(service.codes()).toBeNull();
    });

    it('asks once however often Continue is pressed', () => {
      // Arrange
      service.begin();

      // Act
      // The second press, while the first request is still out. A person who
      // gets no visible answer presses again — it is the most ordinary thing
      // that happens to a slow control.
      service.begin();

      // Assert
      // One challenge. A challenge is a nonce the server persisted, and on this
      // route it is also the value the account identifier is derived from, so a
      // second request strands the first: the ceremony would run against
      // whichever answer landed last and the id derived from the other is an
      // account nobody can ever sign in to.
      expect(http.match(OPTIONS_URL)).toHaveLength(1);
      expect(service.step()).toBe('intro');
    });

    // **The pair that says the prefetch is real.** Either half alone is
    // satisfied by a broken flow: an absence of requests is what a press that
    // did nothing looks like, and a ceremony that ran is what a second fetch
    // also produces.
    it('spends the prefetched challenge instead of asking for another', async () => {
      // Arrange
      await pressContinue();

      // Act
      const codes = await runCeremony();

      // Assert
      // Nothing was asked for. A second options request would spend a second
      // nonce and leave the first one stranded on the server — and the
      // `Continue` that fetched it would have bought the reader nothing but a
      // round trip.
      expect(
        http.match(OPTIONS_URL),
        'a second challenge was asked for on the passkey step.',
      ).toHaveLength(0);
      // And the ceremony ran anyway, once, against exactly the challenge that
      // arrived on the press before it. Deep equality rather than identity: the
      // options came back through `HttpClient` and are a parsed copy.
      expect(ceremonyChallenges).toEqual([CREATION_OPTIONS]);
      expect(codes).toHaveLength(RECOVERY_CODE_SET_SIZE);
      expect(service.step()).toBe('codes');
    });

    it('asks for a fresh challenge after a restart', async () => {
      // Arrange
      await pressContinue();
      await runCeremony();

      // Act
      service.restart();
      service.createPasskey();

      // Assert
      // The nonce `Continue` fetched was consumed by the ceremony that ran
      // before the restart — `RegisterAccountHandler` calls `ConsumeAsync`
      // above its verifier — so a copy kept here would be handed to the next
      // ceremony and refused by a server that has already seen it, with a
      // message naming nothing. `restart()` drops it for that reason.
      const options = http.match(OPTIONS_URL);
      expect(
        options,
        'the restart re-used the challenge the first ceremony spent.',
      ).toHaveLength(1);

      options[0].flush(CREATION_OPTIONS);

      const second = await eventually(
        (): readonly RecoveryCode[] | null => service.codes(),
        'the second set of recovery codes to be published',
      );

      // The control: the fresh challenge really was carried into a ceremony,
      // so the request above is a flow continuing rather than one that asked
      // for something and stopped.
      expect(ceremonyChallenges).toHaveLength(2);
      expect(second).toHaveLength(RECOVERY_CODE_SET_SIZE);
    });
  });
});
