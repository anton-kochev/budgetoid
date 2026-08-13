// The canonical form of a recovery code is the text every branch of the HKDF
// tree is derived from, and this is the module that owns it.
//
// It did not start here. The rule lived as a private function inside
// `recovery-codes.ts`, where `recovery-codes.spec.ts` says in as many words that
// it is deliberately not exported, "because a second caller applying it
// separately is a second place for it to drift". That reasoning is still right,
// and it is exactly why the next caller cannot reach into that file: the
// key-encryption-key branch derives from a recovery code too, and it must use
// the identical text or the wrapped account keys unwrap for a code the verifier
// branch would have refused — and vice versa. The other way out is closed in
// writing as well: the header of `recovery-codes.ts` says not to add the
// key-encryption-key derivation there to "finish" it.
//
// So the rule moves down into a module of its own that both callers import.
// One definition, no second place to drift, and no derivation added to
// `recovery-codes.ts`. What this file has to prove is that the move is not a
// rewrite: the rule stated here is the rule that shipped, character for
// character, because a code already written on paper derives its verifier
// through it.
//
// The expectations below are written as literal input and output pairs rather
// than by applying the same transformations the module applies. A test that
// mirrors the implementation agrees with any bug the implementation has; these
// are an independent statement of the rule, and the last group ties that
// statement back to the derivation that is already live.
import { describe, expect, it } from 'vitest';

import { canonicalRecoveryCode } from './recovery-code-canonical';
import {
  RECOVERY_CODE_ALPHABET,
  RECOVERY_CODE_LENGTH,
  recoveryCodeVerifier,
} from './recovery-codes';

// The golden code from `recovery-codes.spec.ts`, written the way a person hands
// it back: grouped in fives, typed on a keyboard that opened in lower case, with
// the printed `1` read off as an `l` and the printed `0` as an `o`.
const PRINTED_CODE = '0123456789ABCDEFGHJKMNPQRS';
const TYPED_BACK_CODE = ' ol234-56789-abcde-fghjk-mnpqrs ';

describe('the canonical form of a recovery code', () => {
  it('is the identity on every code the generator can mint', () => {
    // Arrange
    // This is the property the whole extraction rests on, and the reason a
    // canonical form could be introduced at all: every fold is the inverse of
    // an exclusion `RECOVERY_CODE_ALPHABET` already made, so nothing the
    // generator can produce is touched by any of them. Were that not true, the
    // rule would silently rewrite codes that have already been issued, every
    // verifier already filed against an account would stop matching, and the
    // symptom would be a 401 nobody can tell from a typo — the only way back
    // into the account, looking broken, saying nothing.
    //
    // The codes are drawn from the shipped alphabet rather than written out, so
    // a symbol added to it is covered here the day it is added. Single symbols
    // catch a fold that only fires on one character; the long strings catch one
    // that only fires in company.
    const symbols = [...RECOVERY_CODE_ALPHABET];
    const mintable = [
      ...symbols,
      RECOVERY_CODE_ALPHABET,
      [...symbols].reverse().join(''),
      RECOVERY_CODE_ALPHABET.slice(0, RECOVERY_CODE_LENGTH),
    ];

    // Act
    const canonicalised = mintable.map(canonicalRecoveryCode);

    // Assert
    expect(canonicalised).toEqual(mintable);
  });

  it('reads a code typed in lower case as the code that was printed', () => {
    // Arrange
    // A phone keyboard opens in lower case and the alphabet is upper-case only,
    // so this is the default way a code comes back, not an edge case.
    const typed = 'abcdefghjkmnpqrstvwxyz';

    // Act
    const canonical = canonicalRecoveryCode(typed);

    // Assert
    expect(canonical).toBe('ABCDEFGHJKMNPQRSTVWXYZ');
  });

  it('strips the spaces and hyphens a code is grouped with', () => {
    // Arrange
    // Twenty-six characters are unreadable in one run, so they are shown in
    // groups and typed back with whatever separated them. Neither separator is
    // in the alphabet, so neither can ever be part of a code. Tabs and newlines
    // come with a paste out of a document and are stripped for the same reason.
    const grouped = ' 01234-56789-ABCDE-FGHJK-MNPQRS ';
    const pasted = '01234\t56789\nABCDE FGHJK-MNPQRS';

    // Act
    const fromGrouped = canonicalRecoveryCode(grouped);
    const fromPasted = canonicalRecoveryCode(pasted);

    // Assert
    expect(fromGrouped).toBe('0123456789ABCDEFGHJKMNPQRS');
    expect(fromPasted).toBe('0123456789ABCDEFGHJKMNPQRS');
  });

  it('reads I and L as the 1 they are printed in place of', () => {
    // Arrange
    // `I` and `L` are excluded from the draw precisely because a reader resolves
    // both as `1`. Excluding them protects nobody on its own — it means no code
    // contains them, not that nobody types them — so the fold is the other half
    // of that decision, and it costs nothing: there is no code either character
    // could be the correct reading of.
    const misreadUpper = 'I2345L789';
    const misreadLower = 'i2345l789';

    // Act
    const fromUpper = canonicalRecoveryCode(misreadUpper);
    const fromLower = canonicalRecoveryCode(misreadLower);

    // Assert
    expect(fromUpper).toBe('123451789');
    expect(fromLower).toBe('123451789');
  });

  it('reads O as the 0 it is printed in place of', () => {
    // Arrange
    // The same argument as `I` and `L`, one glyph over: `O` is read back as
    // `0`, is excluded from the alphabet for that reason, and folds to it.
    const misreadUpper = 'O123456789';
    const misreadLower = 'o123456789';

    // Act
    const fromUpper = canonicalRecoveryCode(misreadUpper);
    const fromLower = canonicalRecoveryCode(misreadLower);

    // Assert
    expect(fromUpper).toBe('0123456789');
    expect(fromLower).toBe('0123456789');
  });

  it('leaves U alone, because U was never a misreading', () => {
    // Arrange
    // `U` is the deliberate omission and the control on the whole list of
    // folds. It is missing from the alphabet for an unrelated reason — so that
    // a random draw cannot spell an obscenity, which is Crockford's reason and
    // a real one for a string somebody is asked to print and keep — not because
    // anybody reads it back as another character. There is therefore no
    // exclusion here for a fold to be the inverse of.
    //
    // What bounds the list is that no fold may bring two distinct codes onto
    // one another. A rule generous enough to rescue every typo would do exactly
    // that, and it would shrink the 130 bits a code is measured to carry — the
    // one number the server is structurally incapable of checking.
    const withU = 'ABCUDEF';
    const lowerU = 'abcudef';

    // Act
    const fromUpper = canonicalRecoveryCode(withU);
    const fromLower = canonicalRecoveryCode(lowerU);

    // Assert
    expect(fromUpper).toBe('ABCUDEF');
    expect(fromLower).toBe('ABCUDEF');
  });

  it('upper-cases without a locale, so i becomes 1 and never a dotted I', () => {
    // Arrange
    // `toUpperCase`, never `toLocaleUpperCase`. The locale-sensitive variant
    // maps `i` to `İ` — capital I with a dot — under a Turkish locale, and
    // `İ` is in neither the alphabet nor the fold list, so it would survive
    // canonicalisation and change the text the HKDF input is built from. One
    // code typed on two phones would then derive two different verifiers, and
    // the phone whose locale differed from the one that minted the set would be
    // refused with the 401 a wrong code gets.
    //
    // Asserted on the output rather than on the call, because the hazard is the
    // value, and the value is observable whatever locale this runner has.
    const turkishHazard = 'i';
    const dottedCapitalI = 'İ';

    // Act
    const canonical = canonicalRecoveryCode(turkishHazard);

    // Assert
    expect(canonical).toBe('1');
    expect(canonical).not.toContain(dottedCapitalI);
    expect(canonical).not.toBe(turkishHazard.toLocaleUpperCase('tr'));
  });

  it('is idempotent, so a second pass changes nothing', () => {
    // Arrange
    // Canonicalising twice has to equal canonicalising once, or the text fed to
    // HKDF would depend on how many layers of the client happened to normalise
    // on the way in. Both callers will apply it, and neither can know whether
    // the other already did.
    const typed = TYPED_BACK_CODE;

    // Act
    const once = canonicalRecoveryCode(typed);
    const twice = canonicalRecoveryCode(once);

    // Assert
    expect(once).toBe(PRINTED_CODE);
    expect(twice).toBe(once);
  });

  it('is total: nothing in, nothing out', () => {
    // Arrange
    // Refusing an empty result is the **caller's** job, not this function's.
    // `recoveryCodeVerifier` already throws on it, because an empty code
    // derives a perfectly well-formed verifier that would be filed against the
    // account as though it were a secret — and one every account with the same
    // bug would share. That check belongs where a derivation happens; a
    // canonical form that threw would force every caller into a try, including
    // the ones that only want to compare two strings.
    const nothing = '';
    const separatorsOnly = ' - - ';

    // Act
    const fromNothing = canonicalRecoveryCode(nothing);
    const fromSeparators = canonicalRecoveryCode(separatorsOnly);

    // Assert
    expect(fromNothing).toBe('');
    expect(fromSeparators).toBe('');
  });

  it('agrees with the verifier derivation that already ships', async () => {
    // Arrange
    // The pin that matters most, and the only one that can catch the extraction
    // silently diverging from the shipped rule. `recoveryCodeVerifier` folds a
    // typed-back code itself today; after this module exists it will fold it
    // through this module. Either way, the verifier derived from the raw text
    // and the verifier derived from its canonical form have to be one value —
    // that is what "identical canonical form" means, stated in the only terms
    // an account cares about.
    const typed = TYPED_BACK_CODE;

    // Act
    const [fromTyped, fromCanonical, fromPrinted] = await Promise.all([
      recoveryCodeVerifier(typed),
      recoveryCodeVerifier(canonicalRecoveryCode(typed)),
      recoveryCodeVerifier(PRINTED_CODE),
    ]);

    // Assert
    // Not a vacuous pin: the typed text is genuinely different from its
    // canonical form, so an identity function would fail here rather than pass
    // by doing nothing.
    expect(canonicalRecoveryCode(typed)).not.toBe(typed);
    expect(fromCanonical).toBe(fromTyped);
    expect(fromPrinted).toBe(fromTyped);
  });
});
