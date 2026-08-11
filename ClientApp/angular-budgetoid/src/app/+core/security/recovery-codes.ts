// Recovery codes are minted here and **only** here, because here is the only
// place they are ever allowed to exist. The browser generates a code, derives a
// verifier `V = HKDF(canonical(code), …)` from it, and the client sends only
// `V`; the server stores `SHA-256(V)` and has no member a code could travel in.
// `canonical` is part of that definition rather than a step in front of it —
// the derivation is not specified until it says what text goes in, and the rule
// lives on `canonicalRecoveryCode` below. The
// reason is not tidiness about secrets: the account's key-encryption key is
// derived from the same code on an independent HKDF branch, so a code reaching
// the server would hand the operator that key for an account whose content it
// is otherwise structurally unable to read. See
// `docs/business-logic/recovery-codes.md` and ADR 0015.
//
// This module ships with a spec and **no caller**, on purpose: generating a set
// is gated behind a fresh WebAuthn assertion and this client cannot run a
// WebAuthn ceremony yet, so nothing can present these verifiers. It follows the
// shape of `export-filename.ts` and `credential-registration-date.ts` — a pure
// module tested in place.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so a function is the whole of it; a class
// would only add a way to hold a code alive past the render that shows it.

// Phantom brands. Declared as `unique symbol`s so no object literal can
// accidentally satisfy either type and nothing can read the marker at runtime —
// both are erased entirely, and both values are plain strings.
declare const recoveryCodeBrand: unique symbol;
declare const recoveryCodeVerifierBrand: unique symbol;

/**
 * A code as it is written down and typed back. It is never transmitted, never
 * logged and never stored.
 *
 * The brand exists so `codes` cannot be handed to something declaring
 * `RecoveryCodeVerifier[]` — the request body's type is the last place the
 * mistake can be caught for free.
 */
export type RecoveryCode = string & { readonly [recoveryCodeBrand]: true };

/**
 * A verifier as it crosses the wire: base64url, unpadded, decoding to exactly
 * {@link RECOVERY_CODE_VERIFIER_BYTES} bytes.
 */
export type RecoveryCodeVerifier = string & {
  readonly [recoveryCodeVerifierBrand]: true;
};

/** One issued set: what the person keeps, and what the server is told. */
export interface RecoveryCodeSet {
  /** Shown once and never sent. Index-aligned with {@link verifiers}. */
  readonly codes: readonly RecoveryCode[];
  /** Sent and never shown. Index-aligned with {@link codes}. */
  readonly verifiers: readonly RecoveryCodeVerifier[];
}

// Crockford's Base32 alphabet — the digits plus the uppercase letters, less
// `I`, `L`, `O` and `U`.
//
// What was excluded and why:
//   - `I` and `L` are read back as `1` in most hands and most typefaces.
//   - `O` is read back as `0`.
//   - `U` is excluded so that a random draw cannot spell an obscenity, which is
//     Crockford's own reason and a real one for a string a person is asked to
//     print and keep.
// Lowercase is excluded wholesale: `l`/`1`, `O`/`0` and `rn`/`m` confusions all
// come back the moment both cases are in play, and a mixed-case code doubles
// the transcription surface for one extra bit per character.
//
// The size is the load-bearing part, not the aesthetics. Thirty-two divides
// 256, so the low five bits of a uniform byte are a *uniform* symbol — see
// `mintRecoveryCode`. An alphabet of 33 or 34 "even safer" symbols would force
// either rejection sampling or a modulo, and a modulo over a non-divisor is a
// silent, invisible loss of entropy that every length-based test passes.
export const RECOVERY_CODE_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/**
 * Characters per code.
 *
 * The requirement is **at least 128 bits of entropy per code**, and this client
 * is the only enforcer that can exist — the server receives fixed-width opaque
 * bytes and is structurally incapable of measuring it (ADR 0015).
 *
 * The arithmetic, which `recovery-codes.spec.ts` recomputes rather than
 * restates: the alphabet holds 32 symbols drawn uniformly, so each character
 * carries `log2(32) = 5` bits exactly, and `26 × 5 = 130 ≥ 128`. Twenty-five
 * characters would be 125 bits and would fail, so 26 is the shortest length
 * that holds — every character here is doing work.
 *
 * A shorter alphabet or a longer code both stay correct; a shorter code does
 * not, and nothing outside this repository would notice.
 */
export const RECOVERY_CODE_LENGTH = 26;

/**
 * Codes per issued set.
 *
 * **This client and the server agree on ten, and nothing executes that
 * agreement.** Ten is product policy living on
 * `GenerateRecoveryCodesHandler.RequiredCodeCount`, which refuses any other
 * count with a `400`. There is no shared schema, no generated client and no
 * contract test between the two numbers, so changing either alone is a runtime
 * failure discovered by a person clicking the button — not a red build.
 */
export const RECOVERY_CODE_SET_SIZE = 10;

/**
 * Decoded width of a verifier.
 *
 * `RecoveryCodeHash.VerifierLength` refuses anything else from both sides,
 * short and long alike. Numerically equal to SHA-256's output width and not the
 * same bound: this is the HKDF output length, chosen independently of what the
 * server then hashes it with.
 */
export const RECOVERY_CODE_VERIFIER_BYTES = 32;

// Every HKDF branch a recovery code feeds, written out together so the
// separation between them is a value rather than a promise.
//
// The `info` parameter is the *only* thing that distinguishes one branch from
// another: same input keying material, same salt, same hash. That is what makes
// them independent — HKDF's expand step is a keyed PRF over `info`, so knowing
// the output of one branch says nothing about the other, which is precisely the
// property ADR 0015 rests on. A database holding `SHA-256(V)` is two one-way
// steps and a different `info` away from the key-encryption key.
//
// `keyEncryptionKey` is **declared and not derived here.** The next epic wraps
// the account's keys under a key off that branch and owns the derivation, its
// output width and its use; what this module owns is that the two `info`
// strings can never silently become one — the spec pins them distinct, so the
// collision that would quietly fold the branches together fails a test instead
// of shipping. Do not add a derivation for it in this file to "finish" it.
export const RECOVERY_CODE_BRANCH_INFO = {
  verifier: 'budgetoid/recovery-code/verifier/v1',
  keyEncryptionKey: 'budgetoid/recovery-code/key-encryption-key/v1',
} as const satisfies Record<string, `budgetoid/recovery-code/${string}`>;

/** The branches a recovery code feeds. */
export type RecoveryCodeBranch = keyof typeof RECOVERY_CODE_BRANCH_INFO;

type RecoveryCodeBranchInfo =
  (typeof RECOVERY_CODE_BRANCH_INFO)[RecoveryCodeBranch];

/**
 * The `info` string of the branch that produces a verifier.
 *
 * Exported so the golden vector in the spec and the server-side documentation
 * name the same value by eye. The `/v1` suffix is not decoration: a change to
 * this string, to the hash, or to the output width changes every verifier the
 * client derives, and the symptom is silent — codes already issued simply stop
 * matching, with a `401` that is deliberately indistinguishable from a wrong
 * code. Such a change is a new version, minted alongside the old, not an edit.
 */
export const RECOVERY_CODE_VERIFIER_INFO = RECOVERY_CODE_BRANCH_INFO.verifier;

// RFC 5869 permits an empty salt, and an empty one is what this has to use. A
// redemption arrives carrying a verifier and no identity at all, so the client
// deriving `V` from a typed-back code has no account, no credential and no
// row to take a salt from — anything per-account would be a value the
// derivation cannot know until after the lookup it is needed for. A fixed
// non-secret salt would be domain separation spelled twice, and `info` already
// spells it. See ADR 0015's rejection of per-row salting for the server-side
// half of the same argument.
const EMPTY_SALT = new Uint8Array(0);

const HKDF_HASH = 'SHA-256';

const utf8 = new TextEncoder();

/**
 * Mints one code.
 *
 * Randomness comes from `crypto.getRandomValues` and from nothing else: no
 * package sits on the path that mints a secret, and `Math.random` is a
 * non-cryptographic generator whose output is indistinguishable from this one
 * to every test that only measures a string's length.
 *
 * The mapping from bytes to characters is **unbiased by construction**. One
 * uniform byte produces one character, and `byte & 31` keeps its low five bits
 * — 256 is 8 × 32, so each of the 32 symbols is the image of exactly 8 of the
 * 256 byte values. The obvious alternatives are not equivalent:
 * `byte % alphabet.length` over an alphabet that does not divide 256 makes the
 * first symbols likelier than the last, and `Math.floor((byte / 256) * n)` is
 * the same bias wearing a different hat. Either costs a fraction of a bit per
 * character, is invisible in the output, and is exactly what the entropy
 * requirement is about.
 */
export function mintRecoveryCode(): RecoveryCode {
  const draw = new Uint8Array(RECOVERY_CODE_LENGTH);
  crypto.getRandomValues(draw);

  let code = '';

  for (const byte of draw) {
    code += RECOVERY_CODE_ALPHABET[byte & (RECOVERY_CODE_ALPHABET.length - 1)];
  }

  return code as RecoveryCode;
}

// The decoding half of the alphabet's own decision, and it lives here because
// here is the only place a code is ever turned into anything.
//
// Excluding `I`, `L` and `O` from the *draw* protects nobody on its own: it
// means no code contains them, not that nobody types them. The exclusion is a
// statement that a reader resolves those glyphs as `1`, `1` and `0` — so the
// derivation has to resolve them the same way, or the confusion the alphabet
// was chosen to avoid comes back on the only path that matters. Leaving this to
// a future caller is worse than leaving it out: a redemption screen would have
// to rediscover which characters fold into which, and the symptom of getting it
// wrong is a `401` that is deliberately indistinguishable from a wrong code —
// the only way back into the account, looking broken, saying nothing.
//
// Each rule is the inverse of an exclusion the alphabet already made, so none
// of them can fold two *codes* together: no minted code contains a lowercase
// letter, an `I`, an `L`, an `O`, a space or a hyphen, which is exactly why
// this is the identity on everything the generator produces and why no verifier
// already derived can move. `U` is excluded too and is deliberately **not**
// mapped — it is excluded so a draw cannot spell an obscenity, not because it
// is read back as something else, and a rule generous enough to rescue every
// typo would quietly shrink the code's 130 bits.
//
// `toUpperCase`, never `toLocaleUpperCase`: the latter maps `i` to `İ` under a
// Turkish locale, which would make the same typed code derive different
// verifiers on two phones.
function canonicalRecoveryCode(code: string): string {
  return code
    .toUpperCase()
    .replace(/[\s-]/g, '')
    .replace(/[IL]/g, '1')
    .replace(/O/g, '0');
}

/**
 * Derives the verifier for a code: `V = HKDF-SHA-256(canonical(code), …)`, 32
 * bytes, rendered as unpadded base64url.
 *
 * Takes a plain `string` rather than a {@link RecoveryCode} because the other
 * caller this will have is a redemption screen, where the code is whatever a
 * person typed. The input is canonicalised first — upper-cased, stripped of the
 * spaces and hyphens it was grouped with, and with the confusable letters
 * resolved to the digits the alphabet excluded them in favour of. A code that
 * is genuinely wrong after that is left alone to match no row, which is the
 * refusal the server is specified to give and is indistinguishable from every
 * other refusal on that route.
 */
export async function recoveryCodeVerifier(
  code: string,
): Promise<RecoveryCodeVerifier> {
  const canonical = canonicalRecoveryCode(code);

  // An empty code is a programming error — an unbound form control, a field
  // read before it was set — and it derives a perfectly well-formed verifier
  // that would be filed against the account as if it were a secret. Everything
  // else is left to match or not match, but this one cannot be told from a real
  // code by anything downstream.
  //
  // Checked *after* canonicalisation, so a field holding only the grouping the
  // person was shown is the same error rather than a well-formed verifier for
  // the empty string.
  if (canonical.length === 0) {
    throw new Error('A recovery code verifier cannot be derived from nothing.');
  }

  const bytes = await deriveBranch(
    canonical,
    RECOVERY_CODE_VERIFIER_INFO,
    RECOVERY_CODE_VERIFIER_BYTES,
  );

  return toBase64Url(bytes);
}

/**
 * Mints a whole set: ten codes and the ten verifiers derived from them,
 * index-aligned.
 *
 * Distinctness is not enforced here and does not need to be. Two equal draws
 * from 130 bits will not happen, and if the generator were broken badly enough
 * to repeat one, a de-duplicating loop would hide exactly the failure worth
 * seeing — the server refuses a set with a repeated verifier, and that refusal
 * is the symptom check ADR 0015 describes.
 */
export async function mintRecoveryCodeSet(): Promise<RecoveryCodeSet> {
  const codes = Array.from(
    { length: RECOVERY_CODE_SET_SIZE },
    mintRecoveryCode,
  );
  const verifiers = await Promise.all(codes.map(recoveryCodeVerifier));

  return { codes, verifiers };
}

// One derivation, parameterised by the branch and nothing else. The `info`
// parameter is typed to the declared branches, so a caller cannot introduce a
// third branch by passing a string literal at a call site.
async function deriveBranch(
  code: string,
  info: RecoveryCodeBranchInfo,
  bytes: number,
): Promise<Uint8Array> {
  const material = await crypto.subtle.importKey(
    'raw',
    utf8.encode(code),
    'HKDF',
    // Not extractable, and the key is discarded with the call. The code is
    // already in memory as a string; this at least keeps the derived keying
    // material out of anything that could export it.
    false,
    ['deriveBits'],
  );

  const derived = await crypto.subtle.deriveBits(
    {
      name: 'HKDF',
      hash: HKDF_HASH,
      salt: EMPTY_SALT,
      info: utf8.encode(info),
    },
    material,
    bytes * 8,
  );

  return new Uint8Array(derived);
}

// Unpadded base64url, matching the alphabet the server decodes with. `btoa`
// emits standard base64, so the two substitutions and the padding strip are the
// whole of the difference; `+` and `/` reaching a URL-safe decoder are a
// refusal, and `=` is what the server's decoder is written to reject rather
// than tolerate.
function toBase64Url(bytes: Uint8Array): RecoveryCodeVerifier {
  let binary = '';

  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary)
    .replaceAll('+', '-')
    .replaceAll('/', '_')
    .replaceAll('=', '') as RecoveryCodeVerifier;
}
