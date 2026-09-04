// What can honestly be held about two UX ceilings, and what cannot.
//
// The caps themselves are product decisions and no test may pin a product
// decision to a number — a case asserting `NARRATIVE_NAME_CHARACTERS === 200`
// reddens on every legitimate change and catches none of the illegitimate ones.
// What is checkable is the *relationship* the module is written from, and it
// comes in three pieces:
//
//   * the worst case is three UTF-8 bytes per UTF-16 code unit, which is
//     measured here over every code point there is rather than reasoned about;
//   * each chosen cap sits under what its byte cap can always carry, so raising
//     one past the cliff reddens instead of failing later, for CJK users only,
//     as a 400 from a server the form said nothing about;
//   * neither number is written anywhere else, because a cap copied to a fourth
//     site is a cap that will be raised at one of them.
//
// What no case here holds: that the byte caps still match the server's. They are
// C# constants and this is TypeScript, nothing builds both, and the module says
// so at the constants themselves.
import { MINIMUM_ENVELOPE_BYTES } from '@app-core/security/key-envelope';
import { Validators } from '@angular/forms';
import { FormControl } from '@angular/forms';
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { listFiles } from '../../production-bundle';
import {
  MAX_UTF8_BYTES_PER_UTF16_UNIT,
  NARRATIVE_DESCRIPTION_CHARACTERS,
  NARRATIVE_DESCRIPTION_ENVELOPE_BYTES,
  NARRATIVE_NAME_CHARACTERS,
  NARRATIVE_NAME_ENVELOPE_BYTES,
  charactersAlwaysFitting,
} from './narrative-field-caps';

const encoder = new TextEncoder();

// The last code point there is, and the one this scan stops at.
const LAST_CODE_POINT = 0x10ffff;

interface WorstCase {
  /** The most UTF-8 bytes any one UTF-16 code unit turned into. */
  readonly bytesPerUnit: number;
  /** A string of one code point that costs exactly that. */
  readonly character: string;
}

// Every code point, including the unpaired surrogates — `String.fromCodePoint`
// hands those back as lone units and `TextEncoder` replaces each with U+FFFD,
// which is three bytes for one unit and therefore part of the worst case rather
// than an exception to it.
//
// Run once and shared, because it is the arrange of three cases and takes
// roughly a sixth of a second.
function worstCase(): WorstCase {
  let bytesPerUnit = 0;
  let character = '';

  for (let codePoint = 0; codePoint <= LAST_CODE_POINT; codePoint++) {
    const candidate = String.fromCodePoint(codePoint);
    const ratio = encoder.encode(candidate).length / candidate.length;

    if (ratio > bytesPerUnit) {
      bytesPerUnit = ratio;
      character = candidate;
    }
  }

  return { bytesPerUnit, character };
}

const worst = worstCase();

// The longest thing a form holding `characters` units can hand over: that many
// copies of the widest unit there is.
function worstEnvelopeBytes(characters: number): number {
  const text = worst.character.repeat(characters);

  return encoder.encode(text).length + MINIMUM_ENVELOPE_BYTES;
}

describe('the narrative character caps', () => {
  it('is built on the widest code unit there is, measured over all of them', () => {
    // Arrange, Act — the scan above.

    // Assert — three, not four. A four-byte astral character costs **two**
    // units, so it is two bytes per unit and cheaper than the three-byte BMP
    // characters a great many people write their names in. A constant raised to
    // four here would be safe and wrong; one lowered to two would be neither.
    expect(worst.bytesPerUnit).toBe(MAX_UTF8_BYTES_PER_UTF16_UNIT);
    expect(worst.character.length).toBe(1);
  });

  it('lets a name of the widest characters there are fit its byte cap', () => {
    // Arrange, Act
    const sealed = worstEnvelopeBytes(NARRATIVE_NAME_CHARACTERS);

    // Assert — both directions of one claim: the measured worst case fits, and
    // the cap sits under the ceiling the arithmetic gives. The second is what
    // says *by how much*, and it is the one that reddens the moment somebody
    // raises the cap towards the cliff.
    expect(sealed).toBeLessThanOrEqual(NARRATIVE_NAME_ENVELOPE_BYTES);
    expect(NARRATIVE_NAME_CHARACTERS).toBeLessThanOrEqual(
      charactersAlwaysFitting(NARRATIVE_NAME_ENVELOPE_BYTES),
    );
  });

  it('lets a description of the widest characters there are fit its byte cap', () => {
    // Arrange, Act
    const sealed = worstEnvelopeBytes(NARRATIVE_DESCRIPTION_CHARACTERS);

    // Assert
    expect(sealed).toBeLessThanOrEqual(NARRATIVE_DESCRIPTION_ENVELOPE_BYTES);
    expect(NARRATIVE_DESCRIPTION_CHARACTERS).toBeLessThanOrEqual(
      charactersAlwaysFitting(NARRATIVE_DESCRIPTION_ENVELOPE_BYTES),
    );
  });

  it('counts UTF-16 code units and not characters', () => {
    // Arrange — the platform claim the whole derivation is written against. An
    // astral character is one character and **two** units, so a cap of N
    // refuses N of them. If this ever stopped being true the worst case would
    // move to the four-byte astral characters and every number in the module
    // would be measuring something else.
    const astral = '\u{1d11e}';
    const control = new FormControl(astral.repeat(NARRATIVE_NAME_CHARACTERS));
    const validate = Validators.maxLength(NARRATIVE_NAME_CHARACTERS);

    // Act
    const refused = validate(control);
    const admitted = validate(
      new FormControl(worst.character.repeat(NARRATIVE_NAME_CHARACTERS)),
    );

    // Assert — the same count of *characters*, one refused and one admitted.
    expect(astral.length).toBe(2);
    expect(refused).not.toBeNull();
    expect(admitted).toBeNull();
  });
});

// A cap written as a number, wherever it is written. Two spellings because a
// form states the same limit twice — once for the validator that refuses a save
// and once for the attribute that stops the typing — and the attribute is
// spelled both ways depending on whether it is bound.
//
// **A digit anywhere in the argument, not only at its start**, because
// `maxLength(NARRATIVE_NAME_CHARACTERS * 2)` is the same defect one operator
// along: it names a cap and then quietly stops being it. The price is that a
// constant with a digit in its name would be reported here, which is a rename
// away and a message that says which file.
const capSpellings: readonly RegExp[] = [
  /maxLength\([^)]*\d/,
  /maxlength\]?\s*=\s*"[^"]*\d/,
];

// Every file under `root` that writes a cap as a literal, specs excluded.
//
// Specs are exempt because a case measuring the validator has to be able to
// hand it a number; the planted spec in the last case is what holds that
// exemption to files and stops it growing into a directory.
//
// It takes its root as an argument so the control below can point it somewhere
// it is guaranteed to find something: a scanner closed over `src/` can only be
// checked against the tree that must come back clean.
function modulesWritingALiteralCap(root: string): string[] {
  return listFiles(root)
    .filter((path) => path.endsWith('.ts') || path.endsWith('.html'))
    .filter((path) => !path.endsWith('.spec.ts'))
    .filter((path) => {
      const source = readFileSync(path, 'utf8');

      return capSpellings.some((spelling) => spelling.test(source));
    })
    .map((path) => relative(root, path))
    .sort();
}

describe('a character cap in a form', () => {
  it('is named everywhere it is written, and typed nowhere', () => {
    // Arrange, Act
    const offenders = modulesWritingALiteralCap(join(process.cwd(), 'src'));

    // Assert — named, not counted: this is a rule about *where* something was
    // written, so the path is the whole of the finding. The fix for a red bar
    // is an import from `narrative-field-caps.ts`, never an exemption — a
    // number beside a control says nothing about the byte cap it protects, and
    // the failure it buys is a 400 that only speakers of some languages ever
    // see.
    expect(
      offenders,
      `a character cap is written as a number in: ${offenders.join(', ')}`,
    ).toEqual([]);
  });

  it('would be reported by path if one were written beside a form', () => {
    // Arrange — a planted tree, because the case above is green over an empty
    // directory and this one must not be. It holds the two halves nothing else
    // can: that both spellings are found at all, and that the spec exemption
    // takes the spec and leaves the component beside it.
    const root = mkdtempSync(join(tmpdir(), 'narrative-cap-scan-'));
    const validatorSite = join('app', 'ledger', 'ledger.component.ts');
    const attributeSite = join('app', 'ledger', 'ledger.component.html');
    const boundSite = join('app', 'ledger', 'bound.component.html');
    const plantedSpec = join('app', 'ledger', 'ledger.component.spec.ts');

    try {
      mkdirSync(join(root, 'app', 'ledger'), { recursive: true });
      writeFileSync(
        join(root, validatorSite),
        'name: ["", [Validators.maxLength(200)]],\n',
      );
      writeFileSync(
        join(root, attributeSite),
        '<input matInput formControlName="name" maxlength="200" />\n',
      );
      writeFileSync(
        join(root, boundSite),
        '<input matInput [attr.maxlength]="200" />\n',
      );
      writeFileSync(
        join(root, plantedSpec),
        'const validate = Validators.maxLength(200);\n',
      );

      // Two decoys: a module that names its cap the right way, and one whose
      // only number has nothing to do with a cap. A scan that read every file,
      // or none of them, disagrees with the expectation below rather than
      // passing it by luck.
      writeFileSync(
        join(root, 'app', 'ledger', 'quiet.component.ts'),
        'name: ["", [Validators.maxLength(NARRATIVE_NAME_CHARACTERS)]],\n',
      );
      writeFileSync(
        join(root, 'app', 'ledger', 'status.ts'),
        'export const accepted = 200;\n',
      );

      // Act
      const offenders = modulesWritingALiteralCap(root);

      // Assert — the three sites, by path, and neither the planted spec nor
      // either decoy.
      expect(offenders).toEqual(
        [validatorSite, attributeSite, boundSite].sort(),
      );
    } finally {
      // In a `finally`, so a failed expectation leaves nothing behind in the
      // temporary directory.
      rmSync(root, { recursive: true, force: true });
    }
  });
});
