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
// A fourth piece was added when a review found the byte caps holding nothing:
// they are transcriptions of two C# constants, no production module imports
// either, and a digit typed wrong left every case above green while measuring
// its headroom against a ceiling the server has never heard of. So a case
// **reads the other language's source** — `NarrativeFieldLimits.cs`, out of the
// backend tree, parsed for the declaration the compiler sees — and compares the
// two numbers. Nothing builds both projects and nothing will; what closes the
// gap is a spec that opens the file, and a parse that misses reddens by name
// rather than comparing nothing to nothing.
//
// What that still does not hold: that the C# constant is the number the
// column's check constraints are built from. This side can only see that the
// transcription matches its original — the original's authority over the
// database is the backend suite's own pin.
//
// A fifth piece sits at the bottom, and it is the *positive* half of the cap
// census: every narrative text control states a cap, rather than merely no
// control stating one as a digit.
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

// Where the server keeps the two numbers this module transcribes.
//
// Resolved from `process.cwd()`, which is the Angular project root under both
// `npm test` and the watcher; the two `..` climb out of
// `ClientApp/angular-budgetoid` and into the solution beside it. A path held as
// a constant rather than searched for, so that a moved file is a red bar naming
// a path somebody can look at instead of a scan that quietly finds nothing.
const SERVER_LIMITS_FILE = join(
  process.cwd(),
  '..',
  '..',
  'BudgetoidApp',
  'Domain',
  'Security',
  'NarrativeFieldLimits.cs',
);

/**
 * What the C# declares for one `NarrativeFieldLimits` constant, or `null` where
 * it declares no such constant in a shape the compiler would accept.
 *
 * **One function taking the constant's name**, so that a third field class —
 * the specification has had two for as long as there have been narrative
 * columns, and a third is one product decision away — arrives as one more call
 * rather than as a second parser to keep in step with this one.
 *
 * **`public const int` spelled out and anchored to the start of a line**,
 * because that file names both constants in prose as well: `<see
 * cref="NameBytes"/>` sits in the XML doc of the other one today. A pattern
 * matching the identifier alone reads that, or reads a declaration somebody
 * commented out, and reports a number no compiler ever saw — which is the one
 * failure mode a check like this cannot afford, since its whole job is to
 * disagree with a wrong number.
 */
function serverCap(source: string, constant: string): number | null {
  const declaration = new RegExp(
    `^\\s*public const int ${constant}\\b\\s*=\\s*(\\d+)\\s*;`,
    'm',
  );
  const digits = declaration.exec(source)?.[1];

  return digits === undefined ? null : Number(digits);
}

describe('a narrative byte cap', () => {
  // The two cases below hold one thing and it is worth being exact about
  // which: that the transcription in `narrative-field-caps.ts` still matches
  // its original. They do **not** hold that the original is the number the
  // column's `length(...) <= …` check constraints are built from — nothing on
  // this side of the repository can see a migration — and that half is pinned
  // by the backend suite over the same constants.
  //
  // Until these existed the caps were held by nothing whatever: no production
  // module imports either of them, so a wrong digit changed no behaviour this
  // suite could reach and left the headroom arithmetic above measuring against
  // a ceiling the server does not have.
  it('says for a name what the server says', () => {
    // Arrange
    const source = readFileSync(SERVER_LIMITS_FILE, 'utf8');

    // Act
    const declared = serverCap(source, 'NameBytes');

    // Assert — the parse before the comparison, and never the comparison
    // alone. A rename or a reformat makes `declared` null, and a case that
    // went straight to equality would redden with a message about numbers
    // instead of about the file it could not read; had both sides been read
    // the same way it would have compared nothing to nothing and gone green.
    expect(
      declared,
      `NameBytes is not declared as \`public const int\` in ${SERVER_LIMITS_FILE}`,
    ).toBeTypeOf('number');
    expect(declared).toBe(NARRATIVE_NAME_ENVELOPE_BYTES);
  });

  it('says for a description what the server says', () => {
    // Arrange
    const source = readFileSync(SERVER_LIMITS_FILE, 'utf8');

    // Act
    const declared = serverCap(source, 'DescriptionBytes');

    // Assert
    expect(
      declared,
      `DescriptionBytes is not declared as \`public const int\` in ${SERVER_LIMITS_FILE}`,
    ).toBeTypeOf('number');
    expect(declared).toBe(NARRATIVE_DESCRIPTION_ENVELOPE_BYTES);
  });

  it('is read out of the C# and not out of the TypeScript beside it', () => {
    // Arrange — a planted file declaring numbers this repository holds
    // nowhere, which is the whole of the control: a parser that echoed
    // `NARRATIVE_NAME_ENVELOPE_BYTES`, or one that read no file at all,
    // answers 1024 and 2560 here and disagrees with the expectation. Three
    // decoys sit in front of the real declarations — the constants named in
    // prose the way the shipped file names them, a declaration commented out,
    // and a longer identifier starting with the same word — because each is a
    // number the compiler never sees and each is one a looser pattern reports.
    const root = mkdtempSync(join(tmpdir(), 'narrative-cap-server-'));
    const planted = join(root, 'NarrativeFieldLimits.cs');

    try {
      writeFileSync(
        planted,
        [
          'namespace Domain.Security;',
          '',
          '/// <summary>Larger than <see cref="NameBytes"/>.</summary>',
          'public static class NarrativeFieldLimits',
          '{',
          '    // public const int NameBytes = 4096;',
          '    public const int NameBytesLegacy = 512;',
          '    public const int NameBytes = 111;',
          '    public const int DescriptionBytes = 222;',
          '}',
          '',
        ].join('\n'),
      );
      const source = readFileSync(planted, 'utf8');

      // Act
      const name = serverCap(source, 'NameBytes');
      const description = serverCap(source, 'DescriptionBytes');
      const absent = serverCap(source, 'AttachmentBytes');

      // Assert — the planted numbers, and `null` for a constant the file does
      // not declare. The third is what makes a miss representable: without it
      // nothing says the function can answer anything but a number, and the
      // two cases above would be asserting against a value that cannot fail.
      expect(name).toBe(111);
      expect(description).toBe(222);
      expect(absent).toBeNull();
    } finally {
      // In a `finally`, so a failed expectation leaves nothing behind in the
      // temporary directory.
      rmSync(root, { recursive: true, force: true });
    }
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

// An `<input>` or a `<textarea>`, opening tag only. Templates are inline
// `template:` literals inside the component `.ts` files, so this reads raw
// source rather than a parsed DOM; `.html` is scanned too, because two shells
// in this application use `templateUrl` and a form could arrive in one.
//
// `[^>]*` ends the tag at the first `>`, which a binding like
// `[disabled]="a > b"` would cut short. That truncation can only *hide* a
// `maxlength` written after it, so the failure it produces is a control
// reported as uncapped when it is not — loud, at a named site, and fixed by
// moving the binding. The opposite mistake is the one this census may not make.
const TEXT_ELEMENT = /<(?:input|textarea)\b[^>]*>/g;

// Material's directive, which is what makes an element one of this
// application's form fields rather than a plain control.
const MAT_INPUT = /\bmatInput\b/;

// Any spelling of the cap: the plain attribute, `[maxlength]`, and the
// `[attr.maxlength]` this product actually uses. Deliberately not anchored to a
// binding syntax — the question here is only whether a cap is stated at all,
// and the case above is what refuses one stated as a digit.
const CAP_BINDING = /\bmaxlength\b/i;

const CONTROL_NAME = /\bformControlName="([^"]*)"/;

// The controls a cap does not apply to, each with the reason it does not.
//
// **This set is the mechanism, and its polarity is the point.** The census
// above is negative — it finds a cap written badly — so a control carrying no
// cap whatsoever matches neither spelling and is reported clean. Measured by a
// reviewer: strip every `Validators.maxLength` and every `[attr.maxlength]`
// from the three narrative screens and the whole suite stays green. This one is
// positive, so a text control added tomorrow with nothing on it fails by
// default, and exempting it costs somebody an entry here and a sentence beside
// it. That is the same choice `CLAUDE.md` argues for `AcceptsEndedSession`
// being opt-in while `AllowsLockedSession` is opt-out: the polarity follows
// from which mistake is audible, and a forgotten cap is silent until somebody
// writing a long name in a three-byte script takes a 400 nothing warned them
// about.
//
// Keyed on the control's name and not on the site, which is a known looseness:
// a future narrative field called `date` would be exempted by this entry
// without anybody deciding so. It is kept because the alternative — a
// file-and-control pair — reddens on every rename and move of a file that had
// nothing to do with caps, and because the three names below are each a
// non-text input type that a character cap is not expressible over.
const CAP_EXEMPT_CONTROLS: ReadonlyMap<string, string> = new Map([
  [
    'amount',
    // The transaction form's figure: `type="number"`, bound to a number, and
    // stored in a numeric column that holds no envelope. `maxlength` does
    // nothing on a number input, so a cap here would be a comment.
    'a number input on a numeric column',
  ],
  [
    'openingBalance',
    // The accounts form's figure, the same in every respect.
    'a number input on a numeric column',
  ],
  [
    'date',
    // The transaction form's datepicker input. Its value is a `Date` the
    // picker writes, the column is a date, and a character cap over a
    // locale-formatted date string would refuse valid dates in some locales
    // and no invalid one in any.
    'a datepicker input on a date column',
  ],
]);

// The name of the control an element is bound to, or a stand-in for an element
// bound to none.
//
// The stand-in is deliberately not exemptible — there is no name to write in
// the map — so a `matInput` with no `formControlName` and no cap is reported
// and stays reported until it gains one or the other. There is no such element
// in the tree today; the alternative, skipping it, is a hole this census could
// not see itself having.
function controlNameOf(element: string): string {
  return CONTROL_NAME.exec(element)?.[1] ?? 'an unnamed control';
}

// Every `matInput` under `root` that states no cap, named by control and file.
//
// Takes its root for the reason the scanner above does: closed over `src/` it
// could only ever be checked against the tree that must come back empty.
function textControlsWithoutACap(root: string): string[] {
  return listFiles(root)
    .filter((path) => path.endsWith('.ts') || path.endsWith('.html'))
    .filter((path) => !path.endsWith('.spec.ts'))
    .flatMap((path) => {
      const source = readFileSync(path, 'utf8');
      const file = relative(root, path);

      return [...source.matchAll(TEXT_ELEMENT)]
        .map((match) => match[0])
        .filter(
          (element) => MAT_INPUT.test(element) && !CAP_BINDING.test(element),
        )
        .map(controlNameOf)
        .filter((control) => !CAP_EXEMPT_CONTROLS.has(control))
        .map((control) => `${control} in ${file}`);
    })
    .sort();
}

describe('a narrative text control', () => {
  // What this census holds and what it does not, because the two are one word
  // apart. It reads the **attribute**, so it holds the half that stops the
  // typing — a person cannot put more into the field than the column can take.
  // It says nothing about `Validators.maxLength`, which is behaviour rather
  // than markup and is held by each component's own spec. A control carrying
  // the attribute and no validator would pass here and still accept an
  // over-long value pasted past the attribute in some browsers, which is
  // exactly why the other half is somebody else's case and not an implication
  // of this one.
  it('states a cap, or is exempt for a reason written down', () => {
    // Arrange, Act
    const uncapped = textControlsWithoutACap(join(process.cwd(), 'src'));

    // Assert — named by control and by file, because the fix is a binding at
    // that one site. Adding a name to `CAP_EXEMPT_CONTROLS` is a fix too, and
    // the more expensive one on purpose: it asks for a sentence saying why a
    // person may type past what the column can store.
    expect(
      uncapped,
      `a narrative text control states no cap: ${uncapped.join('; ')}`,
    ).toEqual([]);
  });

  it('would be reported by control and file if one stated none', () => {
    // Arrange — a planted tree, because the case above is green over an empty
    // directory and this one must not be. Four elements and one spec: a capped
    // control, an uncapped one, an uncapped one whose name is exempt, and a
    // plain input carrying no `matInput` at all — so the scan is checked
    // against something it must find, two things it must forgive, and one it
    // must not look at.
    const root = mkdtempSync(join(tmpdir(), 'narrative-cap-census-'));
    const formSite = join('app', 'ledger', 'ledger.component.ts');
    const plantedSpec = join('app', 'ledger', 'ledger.component.spec.ts');

    try {
      mkdirSync(join(root, 'app', 'ledger'), { recursive: true });
      writeFileSync(
        join(root, formSite),
        [
          '<input matInput formControlName="note" [attr.maxlength]="cap" />',
          '<input matInput formControlName="memo" />',
          '<input matInput type="number" formControlName="amount" />',
          '<input type="hidden" formControlName="rowId" />',
          '',
        ].join('\n'),
      );
      // The spec decoy holds the exemption to files: a case measuring a
      // control has to be able to write one without a cap on it, and this is
      // what stops that exemption growing into a directory.
      writeFileSync(
        join(root, plantedSpec),
        '<input matInput formControlName="ghost" />\n',
      );

      // Act
      const uncapped = textControlsWithoutACap(root);

      // Assert — the one control that states nothing, and nothing else.
      expect(uncapped).toEqual([`memo in ${formSite}`]);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
