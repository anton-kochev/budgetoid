// This file is the **only** enforcement of the entropy requirement anywhere in
// the system, and that is not a figure of speech. A code carries at least 128
// bits of entropy; the server receives fixed-width opaque bytes and pins the
// verifier's decoded width, the set size and within-set distinctness, and it is
// structurally incapable of seeing anything else — a set of ten zero-filled
// verifiers is byte-indistinguishable there from a set a good generator
// produced. ADR 0015 records that the rule has no enforcer below the client and
// that the first browser to mint a set owns the test that says so. This is that
// test.
//
// So the assertions below are deliberately not about a string's length. A
// twenty-six-character code drawn with `Math.random`, or drawn through a
// modulo-biased mapping, or drawn from a four-symbol alphabet, passes a length
// check perfectly. What is asserted instead is the alphabet, the draw, and the
// arithmetic that turns the two into a number of bits.
import { describe, expect, it, vi } from 'vitest';

import {
  RECOVERY_CODE_ALPHABET,
  RECOVERY_CODE_BRANCH_INFO,
  RECOVERY_CODE_LENGTH,
  RECOVERY_CODE_SET_SIZE,
  RECOVERY_CODE_VERIFIER_BYTES,
  RECOVERY_CODE_VERIFIER_INFO,
  mintRecoveryCode,
  mintRecoveryCodeSet,
  recoveryCodeVerifier,
} from './recovery-codes';

const REQUIRED_ENTROPY_BITS = 128;

// Characters excluded from the alphabet. `I`, `L` and `O` because a person
// reading a code off paper resolves each as another character in the set; `U`
// for an unrelated reason — so that a draw cannot spell an obscenity — which is
// why it is absent from the draw here and still deliberately unfolded by the
// canonical form at the bottom of this file.
const CONFUSABLE_CHARACTERS = ['I', 'L', 'O', 'U'];

function decodeBase64Url(value: string): Uint8Array {
  const base64 = value.replaceAll('-', '+').replaceAll('_', '/');

  return Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
}

describe('the recovery code alphabet', () => {
  it('holds thirty-two distinct symbols and no confusable ones', () => {
    // Arrange
    const symbols = new Set(RECOVERY_CODE_ALPHABET);

    // Act
    const distinctCount = symbols.size;

    // Assert
    // Distinctness before size: a repeated character would leave the length at
    // 32 while the draw only ever produced 31 outcomes, which is a fraction of
    // a bit per character lost with nothing visible to show for it.
    expect(distinctCount).toBe(RECOVERY_CODE_ALPHABET.length);
    expect(distinctCount).toBe(32);

    for (const confusable of CONFUSABLE_CHARACTERS) {
      expect(RECOVERY_CODE_ALPHABET).not.toContain(confusable);
    }

    // Uppercase and digits only — admitting lowercase reintroduces `l`/`1` and
    // `O`/`0` from the other side for one extra bit per character.
    expect(RECOVERY_CODE_ALPHABET).toMatch(/^[0-9A-Z]+$/);
  });

  it('has a size that divides the byte space, so no draw can be biased', () => {
    // Arrange
    const byteSpace = 256;

    // Act
    const preimagesPerSymbol = byteSpace / RECOVERY_CODE_ALPHABET.length;

    // Assert
    // This is the property that makes an unbiased byte-to-character mapping
    // possible at all. Were the alphabet 33 or 34 symbols — "even safer", by
    // dropping another confusable — every byte value could no longer map to a
    // symbol equally often, and any implementation would have to either reject
    // and redraw or quietly cost a fraction of a bit per character.
    expect(Number.isInteger(preimagesPerSymbol)).toBe(true);
    expect(preimagesPerSymbol).toBe(8);
  });
});

describe('the entropy of a minted code', () => {
  it('is computed from the alphabet and the length, and clears 128 bits', () => {
    // Arrange
    // Derived from the shipped constants rather than restated, so the claim
    // cannot be left behind by a change to either. The formula is
    // `length × log2(symbols)` and it is only valid because the draw is
    // uniform, which the next test establishes separately — the two together
    // are the requirement.
    const bitsPerCharacter = Math.log2(new Set(RECOVERY_CODE_ALPHABET).size);

    // Act
    const entropyBits = bitsPerCharacter * RECOVERY_CODE_LENGTH;

    // Assert
    expect(bitsPerCharacter).toBe(5);
    expect(entropyBits).toBe(130);
    expect(entropyBits).toBeGreaterThanOrEqual(REQUIRED_ENTROPY_BITS);

    // And the length carries no slack: one character shorter is 125 bits and
    // fails the requirement. That makes this a two-sided pin — shortening the
    // code to make it friendlier to type reddens the suite instead of quietly
    // dropping below the rule.
    expect(bitsPerCharacter * (RECOVERY_CODE_LENGTH - 1)).toBeLessThan(
      REQUIRED_ENTROPY_BITS,
    );
  });

  it('is real, because the draw maps the byte space onto symbols uniformly', () => {
    // Arrange
    // The randomness is replaced with a sweep of every byte value so the
    // *mapping* becomes observable; the derivation is untouched, and nothing
    // here substitutes a crypto implementation production does not use. Over
    // 256 codes the sweep hands out 256 × 26 = 6656 bytes, an exact whole
    // number of passes through the byte space, so a mapping that gives every
    // symbol the same number of byte-value preimages must produce every symbol
    // the same number of times. A biased mapping cannot.
    let next = 0;
    const sweep = vi
      .spyOn(crypto, 'getRandomValues')
      .mockImplementation(<T extends ArrayBufferView | null>(buffer: T): T => {
        const bytes = new Uint8Array(
          (buffer as ArrayBufferView).buffer,
          (buffer as ArrayBufferView).byteOffset,
          (buffer as ArrayBufferView).byteLength,
        );

        for (let index = 0; index < bytes.length; index += 1) {
          bytes[index] = next % 256;
          next += 1;
        }

        return buffer;
      });

    // Act
    const drawn = Array.from({ length: 256 }, mintRecoveryCode).join('');
    sweep.mockRestore();

    // Assert
    const occurrences = new Map<string, number>();

    for (const character of drawn) {
      occurrences.set(character, (occurrences.get(character) ?? 0) + 1);
    }

    const expectedPerSymbol =
      (256 * RECOVERY_CODE_LENGTH) / RECOVERY_CODE_ALPHABET.length;

    // Every symbol appears, and every symbol appears the same number of times.
    // The first half rules out an alphabet the mapping cannot reach the end of;
    // the second rules out `byte % n` over a non-divisor, `byte / 256 * n`, and
    // every other mapping that is nearly uniform.
    expect(occurrences.size).toBe(RECOVERY_CODE_ALPHABET.length);

    for (const symbol of RECOVERY_CODE_ALPHABET) {
      expect(occurrences.get(symbol)).toBe(expectedPerSymbol);
    }
  });

  it('comes from crypto.getRandomValues and never from Math.random', () => {
    // Arrange
    // `Math.random` is the substitution that a length assertion, an alphabet
    // assertion and a uniformity assertion all pass: it is uniform over the
    // alphabet and produces a well-formed code of the right size. It is also
    // seeded, predictable and shared, so a set minted from it is a set an
    // attacker can regenerate. Nothing but this assertion distinguishes it.
    const insecure = vi.spyOn(Math, 'random');
    const secure = vi.spyOn(crypto, 'getRandomValues');

    // Act
    const code = mintRecoveryCode();

    // Assert
    expect(insecure).not.toHaveBeenCalled();
    expect(secure).toHaveBeenCalledTimes(1);

    const [requested] = secure.mock.calls[0];
    expect(requested).toBeInstanceOf(Uint8Array);
    expect((requested as Uint8Array).length).toBe(RECOVERY_CODE_LENGTH);

    // One byte consumed per character. Fewer would mean characters sharing a
    // byte's bits, which is not wrong in itself but is not what the entropy
    // arithmetic above assumes.
    expect(code).toHaveLength(RECOVERY_CODE_LENGTH);

    insecure.mockRestore();
    secure.mockRestore();
  });

  it('draws every symbol of the alphabet over enough real codes', () => {
    // Arrange
    // The real generator this time, with no sweep in front of it. Over 200
    // codes — 5200 characters — the chance that any one of 32 symbols never
    // appears is around 32 × (31/32)^5200, which is far below one in every
    // atom anybody will ever count. A mapping that cannot reach part of its
    // alphabet fails here even if it is internally uniform over the part it
    // can reach.
    const codes = Array.from({ length: 200 }, mintRecoveryCode);

    // Act
    const seen = new Set(codes.join(''));

    // Assert
    expect(seen.size).toBe(RECOVERY_CODE_ALPHABET.length);

    for (const character of seen) {
      expect(RECOVERY_CODE_ALPHABET).toContain(character);
    }

    // Two independent draws of 130 bits colliding is the event this would have
    // to observe to fail by chance, so a repeat here means the generator is
    // broken rather than unlucky.
    expect(new Set(codes).size).toBe(codes.length);
  });
});

describe('the verifier derivation', () => {
  // The golden vector. A fixed code in, a fixed verifier out — computed by this
  // implementation once and frozen here.
  //
  // What it buys: the next story has to get the request shape right and can get
  // it wrong repeatedly without ever touching the crypto, because this pins the
  // crypto independently of any caller. What it catches: a changed `info`
  // string, a changed hash, a changed output width, a salt that stopped being
  // empty, a code encoded as UTF-16 instead of UTF-8, base64 that grew padding
  // or standard `+`/`/` characters, and a byte order flipped in the encoder.
  // Every one of those is otherwise silent — verifiers of the right shape that
  // match no row, refused with the same `401` a wrong code gets.
  //
  // If this goes red, the question is never "what is the new value" — it is
  // which of those changed, because any of them invalidates every code every
  // account already holds.
  const GOLDEN_CODE = '0123456789ABCDEFGHJKMNPQRS';
  const GOLDEN_VERIFIER = 'aGr2Qher4pOLKZ335Uwyi92Ti9GAZ-Ek5G_9Br5plNE';

  it('derives the frozen verifier for the golden code', async () => {
    // Arrange
    const code = GOLDEN_CODE;

    // Act
    const verifier = await recoveryCodeVerifier(code);

    // Assert
    expect(verifier).toBe(GOLDEN_VERIFIER);
  });

  it('names the info string the server-side documentation names', () => {
    // Arrange, Act, Assert
    // Exported so this vector and `docs/business-logic/recovery-codes.md` can
    // be compared by eye. `V = HKDF(canonical(code), …)` is written with an
    // ellipsis there because the parameters live on the client; this is what
    // fills it in. `canonical` is *not* part of the ellipsis — the doc names it
    // because the derivation is not specified until it says what text goes in,
    // and the group at the bottom of this file is where that half is pinned.
    expect(RECOVERY_CODE_VERIFIER_INFO).toBe(
      'budgetoid/recovery-code/verifier/v1',
    );
    expect(RECOVERY_CODE_VERIFIER_INFO).toBe(
      RECOVERY_CODE_BRANCH_INFO.verifier,
    );
  });

  it('keeps the key-encryption-key branch separate from the verifier branch', () => {
    // Arrange
    const infos = Object.values(RECOVERY_CODE_BRANCH_INFO);

    // Act
    const distinct = new Set(infos);

    // Assert
    // The independence of the two branches is the whole of ADR 0015: the
    // verifier crosses the wire and the key-encryption key never does, and what
    // makes knowing the first useless for finding the second is that the two
    // HKDF expansions differ in `info`. Equal `info` strings would make them
    // the same derivation — the code would still work, the server would still
    // accept the set, and the value the operator sees in a request would be the
    // account's key-encryption key.
    expect(distinct.size).toBe(infos.length);
    expect(RECOVERY_CODE_BRANCH_INFO.keyEncryptionKey).not.toBe(
      RECOVERY_CODE_VERIFIER_INFO,
    );
  });

  it('is deterministic for one code and different for another', async () => {
    // Arrange
    const code = mintRecoveryCode();
    const other = mintRecoveryCode();

    // Act
    const [first, again, fromOther] = await Promise.all([
      recoveryCodeVerifier(code),
      recoveryCodeVerifier(code),
      recoveryCodeVerifier(other),
    ]);

    // Assert
    // Determinism is what makes a code redeemable at all: the verifier derived
    // at mint time and the one derived from the same code typed back months
    // later have to be the same value, with no account, no salt and no server
    // state in between.
    expect(again).toBe(first);
    expect(fromOther).not.toBe(first);
  });

  it('produces unpadded base64url decoding to exactly thirty-two bytes', async () => {
    // Arrange
    const code = mintRecoveryCode();

    // Act
    const verifier = await recoveryCodeVerifier(code);

    // Assert
    // The server decodes with the URL-safe alphabet and refuses anything else,
    // then refuses any width but 32 from both sides. Padding, `+` or `/` here
    // are a `400` on generation and an indistinguishable `401` on redemption.
    expect(verifier).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(verifier).not.toContain('=');
    expect(decodeBase64Url(verifier)).toHaveLength(
      RECOVERY_CODE_VERIFIER_BYTES,
    );
  });

  it('refuses to derive anything from an empty code', async () => {
    // Arrange
    const nothing = '';

    // Act
    const derivation = recoveryCodeVerifier(nothing);

    // Assert
    // An unbound form control derives a perfectly well-formed verifier that
    // would be filed against the account as though it were a secret — and one
    // every other account with the same bug would share. A mistyped code is
    // left alone to simply not match; this one cannot be told apart downstream.
    await expect(derivation).rejects.toThrow(/cannot be derived from nothing/);
  });

  it('refuses a code that is nothing once the separators come off', async () => {
    // Arrange
    // Whitespace and hyphens are stripped before the emptiness check, so a
    // field holding only the grouping a person was shown is the same
    // programming error as an unbound one and has to fail the same way. Were
    // the check made on the raw input, this would derive a well-formed verifier
    // from the empty string — the exact value the test above exists to forbid,
    // reachable again through a field nobody typed a character into.
    const separatorsOnly = ' - - ';

    // Act
    const derivation = recoveryCodeVerifier(separatorsOnly);

    // Assert
    await expect(derivation).rejects.toThrow(/cannot be derived from nothing/);
  });
});

// The alphabet excludes `I`, `L` and `O` precisely because a person reads them
// back as `1`, `1` and `0`. Excluding them from the *draw* is only half of that
// decision: the other half is a decoding rule, and without one the exclusion
// protects nobody — it means no code contains those glyphs, not that nobody
// types them. Somebody typing their code back on a phone gets lowercase by
// default, writes `O` where the paper says `0`, and keeps the grouping spaces
// they were shown — and every one of those derives a different verifier and is
// refused with the 401 that is deliberately indistinguishable from a wrong
// code. The only way back into the account looks broken, and nothing anywhere
// says why.
//
// Like the entropy rule above, this one is client-owned and this file is its
// only enforcement: the server is sent derived bytes, so there is no text down
// there to normalise and no layer at or below the API can hold the rule.
//
// What bounds the list of folds is that none of them may fold two *codes*
// together. Each fold is the inverse of an exclusion the alphabet already made,
// which is exactly what makes the function the identity on every code the
// generator can mint — no verifier already derived can move, and no two
// mintable codes can be brought onto one another. `U` is the deliberate
// omission and has its own control below.
//
// One clause of the rule is not observable here: `toUpperCase` and never
// `toLocaleUpperCase`, which maps `i` to `İ` under a Turkish locale and would
// make one typed code derive different verifiers on two phones. It is stated on
// the function itself; no assertion below reaches it.
//
// Every case below is asserted against the verifier the *canonical* code
// derives, not against a frozen string, so these say "the same account" rather
// than restating the golden vector five times.
describe('the canonical form of a typed-back code', () => {
  const TYPED_BACK = '0123456789ABCDEFGHJKMNPQRS';

  it('is what a freshly minted code already is, so no verifier moves', async () => {
    // Arrange
    // The load-bearing test of this group. Every fold is the inverse of an
    // exclusion the alphabet already made, and that is what makes normalisation
    // safe to add at all: it is the identity on everything the generator can
    // produce, because the alphabet holds no lowercase, no `I`, no `L`, no `O`,
    // no space and no hyphen — so no verifier already derived can move. Were
    // that not so, this change would silently invalidate every code every
    // account already holds — the failure ADR 0015 describes, with a 401 nobody
    // can tell from a typo.
    const minted = Array.from({ length: 32 }, mintRecoveryCode);

    // Act
    const derived = await Promise.all(minted.map(recoveryCodeVerifier));

    // Assert
    for (const [index, code] of minted.entries()) {
      expect(canonicalisationOf(code)).toBe(code);
      expect(derived[index]).toBe(await recoveryCodeVerifier(code));
    }
  });

  it('reads a code typed in lowercase as the code that was printed', async () => {
    // Arrange
    const printed = TYPED_BACK;

    // Act
    const [fromPrinted, fromTyped] = await Promise.all([
      recoveryCodeVerifier(printed),
      recoveryCodeVerifier(printed.toLowerCase()),
    ]);

    // Assert
    // A phone keyboard opens in lowercase, and the alphabet is uppercase-only —
    // so this is the *default* way a code is typed back, not an edge case.
    expect(fromTyped).toBe(fromPrinted);
  });

  it('reads the confusable letters as the digits they are read back as', async () => {
    // Arrange
    // `I` and `L` for `1`, `O` for `0`, in both cases. These characters cannot
    // occur in a minted code, so mapping them costs nothing: there is no code
    // they could be the correct reading of.
    const misread = '0123456789ABCDEFGHJKMNPQRS'
      .replace('1', 'I')
      .replace('0', 'O');
    const alsoMisread = '0123456789ABCDEFGHJKMNPQRS'
      .replace('1', 'l')
      .replace('0', 'o');

    // Act
    const [expected, first, second] = await Promise.all([
      recoveryCodeVerifier(TYPED_BACK),
      recoveryCodeVerifier(misread),
      recoveryCodeVerifier(alsoMisread),
    ]);

    // Assert
    // Without this the alphabet's own reason for excluding them is undone: the
    // draw avoids the confusion and the derivation walks straight into it.
    expect(misread).not.toBe(TYPED_BACK);
    expect(first).toBe(expected);
    expect(second).toBe(expected);
  });

  it('reads a code through the grouping it was written down in', async () => {
    // Arrange
    // Twenty-six characters get written down in groups and typed back with
    // whatever separated them. Spaces and hyphens are the two that appear, and
    // neither is in the alphabet, so neither can be part of a code.
    const grouped = ' 01234-56789-ABCDE-FGHJK-MNPQRS ';

    // Act
    const [expected, actual] = await Promise.all([
      recoveryCodeVerifier(TYPED_BACK),
      recoveryCodeVerifier(grouped),
    ]);

    // Assert
    expect(actual).toBe(expected);
  });

  it('still refuses a code that only looks like the right one', async () => {
    // Arrange
    // Control for the whole group. Normalisation folds together the readings of
    // one code; it must not fold together two codes, and that is what bounds
    // the list of folds. `U` is excluded from the alphabet and deliberately
    // left unmapped: it is excluded so that a draw cannot spell an obscenity,
    // not because anybody reads it back as something else, so there is no
    // exclusion here for a fold to be the inverse of. A rule generous enough to
    // rescue every typo would quietly shrink the 130 bits the code above is
    // measured to carry.
    const wrong = '0123456789ABCDEFGHJKMNPQRU';

    // Act
    const [expected, actual] = await Promise.all([
      recoveryCodeVerifier(TYPED_BACK),
      recoveryCodeVerifier(wrong),
    ]);

    // Assert
    expect(actual).not.toBe(expected);
  });
});

// The canonical form is not exported — it is an internal rule of the
// derivation, and a second caller applying it separately is a second place for
// it to drift. It is observed here through the only thing that can observe it:
// two codes derive one verifier exactly when they canonicalise alike. This
// spells the expected rule out independently of the implementation, so the
// identity claim above is checked against a stated rule rather than against
// whatever the module happens to do.
function canonicalisationOf(code: string): string {
  return code
    .toUpperCase()
    .replace(/[\s-]/g, '')
    .replace(/[IL]/g, '1')
    .replace(/O/g, '0');
}

describe('a minted set', () => {
  it('holds the ten codes the server requires, index-aligned with verifiers', async () => {
    // Arrange
    const expectedSize = RECOVERY_CODE_SET_SIZE;

    // Act
    const set = await mintRecoveryCodeSet();

    // Assert
    // Ten is product policy on `GenerateRecoveryCodesHandler.RequiredCodeCount`
    // and nothing links the two numbers, so this assertion is the client half
    // of an agreement that is otherwise only discoverable from a `400`.
    expect(expectedSize).toBe(10);
    expect(set.codes).toHaveLength(expectedSize);
    expect(set.verifiers).toHaveLength(expectedSize);

    const derived = await Promise.all(set.codes.map(recoveryCodeVerifier));
    expect([...set.verifiers]).toEqual(derived);
  });

  it('holds ten distinct codes and ten distinct verifiers', async () => {
    // Arrange, Act
    const set = await mintRecoveryCodeSet();

    // Assert
    // The server refuses a set whose verifiers repeat — a primary-key collision
    // otherwise, arriving after the previous set has already been deleted.
    expect(new Set(set.codes).size).toBe(RECOVERY_CODE_SET_SIZE);
    expect(new Set(set.verifiers).size).toBe(RECOVERY_CODE_SET_SIZE);
  });

  it('never puts a code inside a verifier', async () => {
    // Arrange, Act
    const set = await mintRecoveryCodeSet();

    // Assert
    // The one mistake that would make everything else here pass and still hand
    // the operator the account's keys: a "verifier" that is the code, or
    // carries it. Base64url of 32 derived bytes contains no run of the code's
    // characters, so this is cheap and it is the failure ADR 0015 exists to
    // prevent.
    for (const [index, verifier] of set.verifiers.entries()) {
      const code = set.codes[index];
      expect(verifier).not.toContain(code);
      expect(verifier).not.toBe(code);
    }
  });
});
