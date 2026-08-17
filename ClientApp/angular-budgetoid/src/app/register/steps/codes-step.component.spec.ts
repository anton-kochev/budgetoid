// What this file cannot see, stated once so nobody reads a silence here as a
// claim the screen is fully covered.
//
// jsdom applies no stylesheet and computes no layout, so nothing below can
// measure a rendered value. The type treatment on the codes — `tabular-nums`,
// the widened letter-spacing and Inter — is therefore uncovered, and
// deliberately so: it is real (a proportional font makes `0`/`O` and `1`/`l`
// argue with each other on the one screen where a misread character costs an
// account) but the only mechanism that could assert it is a spec that reads the
// production bundle, the way `no-external-origins.spec.ts` does, and a
// whole-bundle read is too much machinery for three declarations. A reviewer
// holds them. The same limit is why the 48px touch target is pinned as a class
// name rather than a height, which the test that does so says in place.
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { canonicalRecoveryCode } from '@app-core/security/recovery-code-canonical';
import {
  RECOVERY_CODE_ALPHABET,
  RECOVERY_CODE_LENGTH,
  RECOVERY_CODE_SET_SIZE,
  type RecoveryCode,
} from '@app-core/security/recovery-codes';
import { ClipboardService } from '@app-core/services/clipboard.service';
import { FileDownloadService } from '@app-core/services/file-download.service';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { CodesStepComponent } from './codes-step.component';

// The copy is pinned as whole sentences, not fragments, for the reason
// `settings.component.spec.ts` gives: a fragment assertion survives a rewrite
// that changes what the sentence promises, which is the only thing these lines
// exist to protect. On this screen that argument is at its strongest — the
// sentence below marked NFR-020 is the whole warning a person gets before the
// only copy of their account keys leaves the screen forever.
const HEADING = 'Save your recovery codes';
const LEAD =
  'Your browser made these ten codes. Budgetoid never receives one, and this is the only time they’re shown.';
// NFR-020. Its own block, above the checkbox, and never the checkbox's label —
// see `does not make the consequence the checkbox’s accessible name`.
const CONSEQUENCE =
  'Your passkey and these ten codes are the only ways into this account. Budgetoid keeps no copy of either, so if you lose the passkey and every code, everything you record here stays locked — to you, and to us. There’s no way back, and no one to ask.';
const ACKNOWLEDGEMENT = 'I’ve saved these codes somewhere I can get to them.';
const STANDING_LINE = 'Nothing is saved until the last step.';
const COPY_COST =
  'Copying puts them on your clipboard, where other apps on this device can read them.';
const SAVE_BUTTON = 'Save to file';
const COPY_BUTTON = 'Copy';
const CREATE_BUTTON = 'Create account';
const SAVED = 'Saved.';
const COPIED = 'Copied.';
// `navigator.clipboard.writeText` rejects routinely and for reasons that have
// nothing to do with this app — a permissions policy, an iframe, Safari
// deciding the press was not a user gesture. Silence after a press is the
// defect the settings screen argues against for the export, and it is worse
// here: the person believes ten codes are on their clipboard, closes the
// screen, and has nothing. The sentence names the other route rather than
// offering "try again", because the other route is the one that works.
const COPY_FAILED = 'Couldn’t copy them. Save them to a file instead.';

// What Material's `mat-flat-button`, `mat-stroked-button` and `mat-button`
// render as. The class rather than the attribute selector, because the class is
// what carries the treatment into the DOM and what the theme styles. The three
// are mutually exclusive in Material's own appearance map, which is what lets
// the assertions below read a missing class as "not that treatment" rather than
// as "possibly both".
const OUTLINE_CLASS = 'mat-mdc-outlined-button';
const FILLED_CLASS = 'mat-mdc-unelevated-button';
const GHOST_CLASS = 'mat-mdc-button';
// The class this screen's own stylesheet hangs `min-height:
// var(--bud-touch-target)` on, because Material's M3 button is shorter than the
// 48px minimum `accessibility.md` sets. jsdom applies no stylesheet, so no spec
// here can measure a rendered height; the class is the seam between the two
// halves, and it is the half that goes missing — a control written without it
// looks correct in every screenshot and is under the minimum on every phone.
const TOUCH_TARGET_CLASS = 'r-button';
// Material's checkbox ships its own 48px target as an element, sized by
// `--mat-checkbox-touch-target-size`, so the checkbox earns the minimum without
// this stylesheet saying anything — but only while that element is rendered.
const CHECKBOX_TOUCH_TARGET = '.mat-mdc-checkbox-touch-target';

// The two halves of a list item. They are separate elements rather than one
// text node because the number and the code are two different things: the
// number is for the person counting how many of ten they have written down, and
// the code is the secret. A file or a clipboard payload assembled from the item
// as a whole carries both, which is the trap the numbering creates and the one
// `keeps the number beside a code out of the file and the clipboard` exists to
// spring.
const CODE_INDEX_SELECTOR = '.r-code-index';
const CODE_TEXT_SELECTOR = '.r-code';

// Every shape that makes a node a live region, not only the one this screen
// uses. The rule being enforced is "the codes are announced by nothing", and a
// list moved into a `role="log"` or an `aria-live="polite"` div is exactly as
// bad as one moved into the `role="status"` — the reader hears ten secrets read
// out as ten events.
const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

// The instant the file's name is taken from. Late enough in the UTC day that an
// implementation reading the local getters at the pinned zone (UTC+14) would
// name the file for the following day, which is the defect
// `recovery-codes-filename.spec.ts` pins on its own and this one would
// otherwise not notice.
const SAVED_AT = new Date(Date.UTC(2026, 7, 9, 23, 30, 15));
const SAVED_FILENAME = 'budgetoid-recovery-codes-20260809T233015Z.txt';

// The brand is a phantom type with no constructor — `recovery-codes.ts` puts it
// on with an assertion of its own, so a fixture has to as well. These are
// **fixtures and not minted codes**: they are walks along the alphabet, chosen
// so a reader can see at a glance that the grouping below regroups them rather
// than rewriting them. Nothing here is a statement about how a real code is
// drawn — that is `recovery-codes.spec.ts`, and it is the only file in the
// system that can make one.
function fixtureCode(text: string): RecoveryCode {
  return text as RecoveryCode;
}

const CODES: readonly RecoveryCode[] = [
  '0123456789ABCDEFGHJKMNPQRS',
  '123456789ABCDEFGHJKMNPQRST',
  '23456789ABCDEFGHJKMNPQRSTV',
  '3456789ABCDEFGHJKMNPQRSTVW',
  '456789ABCDEFGHJKMNPQRSTVWX',
  '56789ABCDEFGHJKMNPQRSTVWXY',
  '6789ABCDEFGHJKMNPQRSTVWXYZ',
  '789ABCDEFGHJKMNPQRSTVWXYZ0',
  '89ABCDEFGHJKMNPQRSTVWXYZ01',
  '9ABCDEFGHJKMNPQRSTVWXYZ012',
].map(fixtureCode);

// Six groups of four and a tail of two: 6 × 4 + 2 = 26, which is
// `RECOVERY_CODE_LENGTH`. The character class is deliberately wider than the
// alphabet — what this pattern pins is the *grouping*, and the content is
// pinned exactly, character for character, by the round-trip test below.
const GROUPED = /^[0-9A-Z]{4}(?:-[0-9A-Z]{4}){5}-[0-9A-Z]{2}$/;

describe('CodesStepComponent', () => {
  let fixture: ComponentFixture<CodesStepComponent>;
  let host: HTMLElement;
  let save: Mock<FileDownloadService['save']>;
  let write: Mock<ClipboardService['write']>;
  let created: Mock<() => void>;

  beforeEach(async () => {
    save = vi.fn<FileDownloadService['save']>();
    // Resolved, because the real clipboard is a promise and the outcome
    // sentence is what the component says once it settles.
    write = vi.fn<ClipboardService['write']>().mockResolvedValue(undefined);
    created = vi.fn();

    const downloads: Pick<FileDownloadService, 'save'> = { save };
    const clipboard: Pick<ClipboardService, 'write'> = { write };

    await TestBed.configureTestingModule({
      imports: [CodesStepComponent],
      providers: [
        provideNoopAnimations(),
        // Both are the seams over browser APIs jsdom does not implement:
        // `URL.createObjectURL` for one, `navigator.clipboard` for the other.
        { provide: FileDownloadService, useValue: downloads },
        { provide: ClipboardService, useValue: clipboard },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CodesStepComponent);
    fixture.componentRef.setInput('codes', CODES);
    fixture.componentInstance.create.subscribe(() => created());
    host = fixture.nativeElement as HTMLElement;
    // Exactly one, so that every test below starts from a bare render and an
    // interaction is genuinely the first thing that has happened.
    fixture.detectChanges();
  });

  afterEach(() => {
    // Harmless when nothing was faked, and the one thing that stops the
    // filename test from leaving a frozen clock behind for every file after it.
    vi.useRealTimers();
  });

  // NFR-020, and the single most important assertion in this commit. Everything
  // else on this screen is a control or a convenience; this sentence is the one
  // thing standing between a person and an account they will never open again.
  it('states that losing the passkey and every code locks the records for good', () => {
    // Act
    const statement = elementSaying(host, CONSEQUENCE);

    // Assert
    // The whole sentence, and as **one element's own text**, which is the half a
    // `toContain` over the host cannot see: the requirement is a statement a
    // reader meets as a statement, and prose broken across the screen — a clause
    // in the lead, a clause on the button, a clause in the checkbox label — says
    // the same words and is not the same warning.
    expect(statement).not.toBeNull();
    // And it is not something the screen announced and moved on from. A
    // consequence rendered into a live region is read once, to whoever happened
    // to be listening, and is furniture to everybody else.
    expect(statement?.closest(LIVE_REGION_SELECTOR)).toBeNull();
  });

  it('does not make the consequence the checkbox’s accessible name', () => {
    // Act
    const checkbox = checkboxInput(host);
    const name = accessibleNameOf(host, checkbox);

    // Assert
    // The tempting shape — the consequence *is* the label, so nobody can tick
    // the box without having been shown it — is the one this forbids. A label
    // is read every time focus lands on the control and is re-read by every
    // subsequent announcement of its state, so a three-sentence label is three
    // sentences between a keyboard user and knowing whether the box is ticked.
    // The acknowledgement is a short first-person sentence; the consequence is
    // its own block above it.
    expect(name).toBe(ACKNOWLEDGEMENT);
    expect(name).not.toContain(CONSEQUENCE);
    // Reached through the accessible name rather than through the `<label>`
    // alone, because `aria-labelledby` pointing at the consequence block is the
    // same defect written a different way and moves no text on the screen.
    expect(checkbox.getAttribute('aria-labelledby')).toBeNull();
  });

  it('keeps the create control unavailable until the acknowledgement is given', () => {
    // Act
    const create = buttonNamed(host, CREATE_BUTTON);
    create?.click();

    // Assert
    // Marked unavailable to assistive technology *and* refusing the press.
    // Neither half implies the other, and the second half is the one that
    // matters: `disabledInteractive` leaves the DOM `disabled` property `false`
    // — Material's own click-halt is on anchors only — so the browser delivers
    // the click to the component's handler exactly as if nothing were disabled.
    // The attribute is presentation. **The gate is the handler**, and a
    // component that renders the state without guarding the handler creates an
    // account for somebody who acknowledged nothing: the second `expect` below
    // is the only thing in this repository that says so.
    expect(create?.getAttribute('aria-disabled')).toBe('true');
    expect(created).not.toHaveBeenCalled();
  });

  it('keeps the create control reachable by keyboard while it is unavailable', () => {
    // Act
    const create = buttonNamed(host, CREATE_BUTTON);
    const checkbox = checkboxInput(host);

    // Assert
    // A third case the book does not yet have: not busy, not unavailable for
    // the life of the screen, but waiting on the person one tab stop away. A
    // plain `[disabled]` button leaves the tab order entirely, so a keyboard
    // user tabbing through the screen never meets the control, is never told it
    // is waiting on anything, and has no way to discover that the gate is the
    // checkbox they just passed. Asserted through what is observable in the DOM
    // rather than through Material's input, because the requirement is about
    // the tab order, not about which API produced it.
    expect(create?.disabled).toBe(false);
    expect(create?.getAttribute('tabindex')).not.toBe('-1');
    // And the gate is genuinely one stop away rather than somewhere above the
    // fold: the acknowledgement precedes the control it releases, so a reader
    // moving forward meets the condition before the thing it conditions.
    expect(precedes(checkbox, create)).toBe(true);
  });

  it('enables the create control once the acknowledgement is given', () => {
    // Arrange
    const checkbox = checkboxInput(host);

    // Act
    checkbox.click();
    fixture.detectChanges();
    const create = buttonNamed(host, CREATE_BUTTON);
    create?.click();

    // Assert
    // The mirror of the test above, and each is the other's control: a screen
    // that never gates passes the first half of this one, and a screen that
    // gates permanently passes the whole of the other.
    expect(checkbox.checked).toBe(true);
    expect(create?.getAttribute('aria-disabled')).not.toBe('true');
    expect(created).toHaveBeenCalledOnce();
  });

  // NFR-020's other half, and the claim the client could not have made before
  // this screen existed: the codes are minted in the browser, so the sentence
  // saying so is true here and was a lie anywhere earlier.
  it('states that the browser made the codes and that Budgetoid never receives one', () => {
    // Act
    const lead = elementSaying(host, LEAD);

    // Assert
    expect(lead).not.toBeNull();
    // Above the codes, not beneath them: somebody who reads to the first thing
    // that looks like a secret and stops has already been told where it came
    // from and that it will not be shown again.
    expect(precedes(lead, codeList(host))).toBe(true);
  });

  it('holds the fixtures to the shape a minted code has', () => {
    // Assert
    // A guard on the guard, in the shape `export-filename.spec.ts` uses for the
    // time zone. The grouping pattern below is written for exactly 26
    // characters — six groups of four and a tail of two — so a change to
    // `RECOVERY_CODE_LENGTH` has to fail *here*, with a sentence saying why,
    // rather than as ten opaque regex mismatches that read as a broken
    // component.
    expect(RECOVERY_CODE_LENGTH).toBe(26);
    // And the set size is pinned as a literal for a reason the length is not.
    // Two sentences on this screen spell **"ten"** as a word — `LEAD` ("these
    // ten codes") and `CONSEQUENCE` ("these ten codes are the only ways into
    // this account") — because interpolating the count would buy plural
    // branches and worse prose for a number the server fixes at ten as well.
    // So when `RECOVERY_CODE_SET_SIZE` moves, the red bar starts here, and the
    // work it points at is: **go re-read `LEAD` and `CONSEQUENCE` at the top of
    // this file and rewrite both sentences.** Nothing else in the system will
    // notice the prose went stale.
    expect(RECOVERY_CODE_SET_SIZE).toBe(10);
    expect(CODES.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(new Set(CODES).size).toBe(RECOVERY_CODE_SET_SIZE);

    for (const code of CODES) {
      expect(code.length).toBe(RECOVERY_CODE_LENGTH);
      // Drawn only from the alphabet, which is what makes the round-trip test
      // below discriminate. A fixture holding an `O` or an `L` would be folded
      // to `0` or `1` by `canonicalRecoveryCode`, so the round trip would fail
      // on a component that was regrouping perfectly — a fixture defect wearing
      // an implementation defect's clothes.
      for (const character of code) {
        expect(RECOVERY_CODE_ALPHABET).toContain(character);
      }

      expect(canonicalRecoveryCode(code)).toBe(code);
    }
  });

  it('shows ten codes', () => {
    // Act
    const items = codeItems(host);

    // Assert
    // Read as `ol[role="list"] > li` rather than as "some elements exist". The
    // explicit `role` is what keeps Safari announcing "list, 10 items" once
    // `list-style: none` drops the semantics with the markers, and `ol` rather
    // than `ul` because the position of a code in the set is real information —
    // see the numbering test below, which is what earns the element.
    expect(items.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(codeList(host)?.tagName).toBe('OL');
  });

  it('numbers every code where a reader can see the number', () => {
    // Act
    const items = codeItems(host);

    // Assert
    // The number is rendered as content, not left to a marker. Two things turn
    // on that. The `ol` earns its element only if the order is visible —
    // markers styled off and no printed index makes it a `ul` wearing a
    // different tag — and, the point of the whole thing, a person transcribing
    // ten 26-character strings by hand needs to be able to put the pen down and
    // say "I have written down seven of ten". Without a printed number they are
    // counting rows on a screen full of near-identical noise.
    expect(items.length).toBe(RECOVERY_CODE_SET_SIZE);

    items.forEach((item, position) => {
      // Digits only, so a muted `7.` or `7)` passes and a missing, duplicated
      // or zero-based number does not. The formatting is the book's business;
      // *which* number it is, is this file's.
      expect(indexTextOf(item).replace(/\D/g, '')).toBe(String(position + 1));
      // Content, not an attribute a stylesheet could be hiding: `hidden` is the
      // one form of invisibility jsdom can see, and the whole point of the
      // number is that it is on the screen. Stated plainly — nothing here can
      // see `display: none`, and a reviewer holds that half.
      expect(
        item.querySelector(CODE_INDEX_SELECTOR)?.hasAttribute('hidden'),
      ).toBe(false);
      // And hidden from assistive technology, which is not a contradiction:
      // a screen reader already announces "7 of 10" from the list semantics
      // pinned above, so exposing the printed number as well has it read the
      // position twice before every code. The number is for eyes; the list
      // role is for ears; they say the same thing once each.
      expect(
        item.querySelector(CODE_INDEX_SELECTOR)?.getAttribute('aria-hidden'),
      ).toBe('true');
    });
  });

  it('groups every code so it can be transcribed', () => {
    // Act
    const items = codeItems(host);

    // Assert
    // Twenty-six unbroken characters is a string a person loses their place in
    // halfway through, on the one occasion they will ever be asked to copy it
    // by hand. The grouping is free: `recovery-code-canonical.ts` strips
    // hyphens and whitespace before anything derives from a code.
    expect(items.length).toBe(RECOVERY_CODE_SET_SIZE);

    for (const item of items) {
      // The code's own element, never the item as a whole — the item now also
      // holds the position, and a pattern run over both would be matching a
      // string no user of a code ever sees.
      expect(codeTextOf(item)).toMatch(GROUPED);
    }
  });

  it('groups a code back to exactly what was minted', () => {
    // Act
    const rendered = shownCodes(host);

    // Assert
    // The half the pattern above cannot see, and the one that carries real
    // risk: a grouping that dropped a character, repeated one, or reordered the
    // groups matches `GROUPED` perfectly and hands the person a code that
    // derives a verifier matching no row — a key to an account, wrong in a way
    // nothing on screen can show and nothing on the server can explain. Read
    // through the shipped canonical form rather than a local `replaceAll`, so
    // this is the same fold the redemption path will apply.
    expect(rendered.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(rendered.map(canonicalRecoveryCode)).toEqual([...CODES]);
  });

  it('saves the codes to a file named for the instant', async () => {
    // Arrange
    // Only `Date` is faked. The clock is what the filename is taken from, and
    // faking the timers as well would stop the promise the copy path awaits
    // from ever settling.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(SAVED_AT);

    // Act
    buttonNamed(host, SAVE_BUTTON)?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // Assert
    expect(save).toHaveBeenCalledOnce();
    const [, filename] = save.mock.calls[0];
    // The whole name. `toMatch(/\.txt$/)` would be green on a file named for
    // the export beside it, and the stamp is what tells two of these apart in a
    // downloads folder if somebody generates a second set.
    expect(filename).toBe(SAVED_FILENAME);
    // The browser writes the file with no visible act of its own, so a screen
    // that says nothing leaves the person having pressed a button that produced
    // no observable effect — which reads as broken and invites the second press.
    expect(normalize(outcomeRegion(host))).toBe(SAVED);
  });

  it('writes the file exactly as the codes are shown', async () => {
    // Arrange
    const shown = shownCodes(host);

    // Act
    buttonNamed(host, SAVE_BUTTON)?.click();
    await fixture.whenStable();
    const [blob] = save.mock.calls[0];
    const written = (await blob.text()).split('\n').filter(Boolean);

    // Assert
    // Exactly the ten grouped lines that are on the screen, in the order they
    // are on it, and **nothing else** — no header, no caption, no ungrouped
    // second copy, no JSON wrapper. Two reasons, and the second is the one a
    // future reader will try to "improve" away:
    //
    // The person is being asked to check the file against what they can see,
    // and the whole value of that check is that the two are the same text; the
    // grouping specifically has to survive, because a file holding the raw
    // 26-character runs is a file nobody can transcribe from, saved from a
    // screen that took care to make them transcribable.
    //
    // And a header line naming the product — "Budgetoid recovery codes" — would
    // **label the secret** for whoever finds the file: a stranger with the disk,
    // a backup service, a shared downloads folder. Ten anonymous grouped strings
    // are ten anonymous grouped strings. The filename already pays that cost
    // once, and once is the most it can be paid; the contents do not repeat it.
    // The file stays bare. Do not caption it.
    expect(shown.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(written).toEqual([...shown]);
    expect(blob.type).toContain('text/plain');
  });

  it('neither saves nor copies on render', () => {
    // Assert
    // Control for the two tests above: a component calling either seam from
    // `ngOnInit` — or from a template expression, which re-fires on every change
    // detection pass — satisfies "was called once after a click" without the
    // click having done anything, and puts ten secrets on the clipboard of
    // somebody who never asked for them.
    expect(save).not.toHaveBeenCalled();
    expect(write).not.toHaveBeenCalled();
  });

  it('says what copying costs, beside the control that costs it', () => {
    // Act
    const cost = elementSaying(host, COPY_COST);
    const copy = buttonNamed(host, COPY_BUTTON);

    // Assert
    // The clipboard is readable by every other app on the device and survives
    // the screen, so the cheapest control here is the one with the consequence
    // nobody expects. The sentence states it as a fact rather than as a
    // warning — `voice.md` bans that word as a label — and it is visible prose,
    // never a tooltip, which reaches neither a keyboard nor a touch user.
    expect(cost).not.toBeNull();
    expect(copy?.getAttribute('title')).toBeNull();
    // Placed with the two file-and-clipboard controls rather than at the top of
    // the screen or stranded under the create button. Stated honestly: document
    // order is all this can see, so it pins the sentence between the first of
    // those controls and the screen's final action, not which side of Copy it
    // falls on. A reviewer holds the rest.
    expect(precedes(buttonNamed(host, SAVE_BUTTON), cost)).toBe(true);
    expect(precedes(cost, buttonNamed(host, CREATE_BUTTON))).toBe(true);
  });

  it('copies the codes exactly as they are shown', async () => {
    // Arrange
    const shown = shownCodes(host);

    // Act
    buttonNamed(host, COPY_BUTTON)?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // Assert
    // The same text as the file, for the same reason: somebody who pastes into
    // a password manager and somebody who opens the saved file have to end up
    // holding the same ten strings, or one of the two has codes that unwrap
    // nothing.
    expect(write).toHaveBeenCalledOnce();
    expect(write.mock.calls[0][0].split('\n').filter(Boolean)).toEqual([
      ...shown,
    ]);
    expect(normalize(outcomeRegion(host))).toBe(COPIED);
  });

  it('keeps the number beside a code out of the file and the clipboard', async () => {
    // Arrange
    const items = codeItems(host);
    // What each `<li>` reads as in full — the position and then the code. Kept
    // only as the control below: it is the string the obvious implementation
    // reaches for, because it is one `textContent` away and looks exactly like
    // the line the person can see. Nothing here compares a payload against it,
    // for the reason spelled out at the assertions.
    const wholeItems = items.map(normalize);
    const codes = items.map(codeTextOf);

    // Act
    buttonNamed(host, SAVE_BUTTON)?.click();
    buttonNamed(host, COPY_BUTTON)?.click();
    await fixture.whenStable();
    const savedLines = payloadLines(await save.mock.calls[0][0].text());
    const copiedLines = payloadLines(write.mock.calls[0][0]);

    // Assert
    // The trap the numbering creates, and the only reason it needs its own
    // test: a file or a clipboard payload built from the rendered line carries
    // `7` in front of the seventh code. That file looks right — it has ten
    // lines, the codes are all there, the grouping survived — and every code
    // pasted out of it derives a verifier matching no row, because the leading
    // digit and space go through `canonicalRecoveryCode` as part of the code.
    // The person is holding a backup that will fail on the one day they need
    // it, and nothing they can see says so.
    //
    // First a control: the item text and the code text really do differ here,
    // so the assertions below are refusing something rather than passing on a
    // screen that renders no numbers at all.
    expect(wholeItems).not.toEqual(codes);

    // The invariant is stated as **the payload's own shape**, never as the
    // absence of a string rebuilt from the DOM. That earlier shape was the
    // reason this test could not spring its own trap: Angular's default
    // `preserveWhitespaces: false` drops the whitespace-only text node between
    // the position and the code, so `li.textContent` collapses to `1.CODE`, and
    // a payload built as `` `${i + 1}. ${code}` `` — the spelling a person
    // actually writes — contains no such substring and slipped straight past.
    // Anchored per line, nothing derived from the printed position can be
    // spelled in a way that survives: a leading `7`, `7.`, `7)`, `7 - `, a
    // trailing `(7)`, or a bare number on a line of its own all shift or add
    // characters that the pattern refuses. Every line is a grouped code and
    // nothing else.
    expect(savedLines.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(copiedLines.length).toBe(RECOVERY_CODE_SET_SIZE);

    for (const line of [...savedLines, ...copiedLines]) {
      expect(line).toMatch(GROUPED);
    }

    // And the payloads are not ten *other* grouped strings — they carry these
    // codes, bare, each as a whole line rather than merely somewhere inside
    // one.
    for (const code of codes) {
      expect(savedLines).toContain(code);
      expect(copiedLines).toContain(code);
    }
  });

  it('says a copy failed and leaves the codes where they are', async () => {
    // Arrange
    // The rejection is not exotic: a permissions policy, an iframe, or Safari
    // deciding the press did not count as a user gesture all land here, on
    // browsers a person would call working.
    write.mockRejectedValue(new Error('clipboard write refused'));
    const shown = shownCodes(host);

    // Act
    buttonNamed(host, COPY_BUTTON)?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // Assert
    // The failure is said, in the same region the success is said in — one
    // region, so the outcome of the last press is in one place and a person
    // does not have to know where to look for bad news versus good.
    expect(shown.length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(normalize(outcomeRegion(host))).toBe(COPY_FAILED);
    // And the success sentence is nowhere on the screen. Both halves are
    // needed: a region that appends rather than replaces reads `Copied.
    // Couldn't copy them.` and satisfies neither a reader nor a person.
    expect(elementSaying(host, COPIED)).toBeNull();
    // The codes survive the failure. A component that clears the list on the
    // way into an async call, or that treats the copy as the moment the secrets
    // stop being needed, takes away the person's only view of them in exchange
    // for nothing — and this is the one screen where "shown again later" does
    // not exist.
    expect(shownCodes(host)).toEqual([...shown]);
  });

  it('carries the outcome region before anything has happened', () => {
    // Act
    const region = outcomeRegion(host);

    // Assert
    // Both halves are load-bearing, as on every region in this app. Presence
    // alone is satisfied by a region that always renders a line — a screen
    // telling somebody who has done nothing that their codes are saved —
    // and emptiness alone by no region at all, which is a live region created
    // at the moment it gains content and therefore announced by nothing.
    expect(region).not.toBeNull();
    expect(normalize(region)).toBe('');
    // `status`, never `alert`: these are confirmations of acts the person asked
    // for, and assertive is reserved for a failure to save something they typed.
    expect(region?.getAttribute('role')).toBe('status');
  });

  it('announces nothing while the codes are on screen', () => {
    // Act
    const list = codeList(host);
    const regions = Array.from(host.querySelectorAll(LIVE_REGION_SELECTOR));

    // Assert
    // The codes really are rendered, so the negatives below say *not
    // announced* rather than *nothing to announce*.
    expect(codeItems(host).length).toBe(RECOVERY_CODE_SET_SIZE);
    expect(list).not.toBeNull();
    // One region on the screen — the outcome region — and the list is outside
    // it. `components.md` states the rule for the credential list and it is
    // sharper here: a `role="status"` that gained a list narrates every entry
    // as an event, and ten secrets read aloud into a speech buffer is not
    // accessibility. The codes are content, in normal reading order, met by
    // reading.
    expect(regions.length).toBe(1);
    expect(regions[0]?.contains(list)).toBe(false);
    // Reached from the list's side as well, because the two catch different
    // edits: the count above catches a second region being added around the
    // codes, and this catches the existing one being moved to wrap them.
    expect(list?.closest(LIVE_REGION_SELECTOR)).toBeNull();

    for (const item of codeItems(host)) {
      expect(item.closest(LIVE_REGION_SELECTOR)).toBeNull();
    }
  });

  it('holds every control on the screen to the touch target', () => {
    // Act
    const buttons = [
      buttonNamed(host, SAVE_BUTTON),
      buttonNamed(host, COPY_BUTTON),
      buttonNamed(host, CREATE_BUTTON),
    ];

    // Assert
    // Built by name so a missing control fails here rather than shrinking the
    // loop to nothing — a census satisfied by "three controls appeared" is
    // satisfied by any three.
    expect(buttons.length).toBe(3);

    for (const button of buttons) {
      expect(button).not.toBeNull();
      expect(button?.classList.contains(TOUCH_TARGET_CLASS)).toBe(true);
    }

    // The checkbox is a control on this screen too, and it is the one control
    // whose target this stylesheet does not set: Material renders its own 48px
    // element for it. Asserted rather than assumed, because it is rendered
    // conditionally on Material's side and a future `--mat-checkbox-touch-
    // target-display: none` would take it away silently.
    expect(host.querySelector(CHECKBOX_TOUCH_TARGET)).not.toBeNull();
  });

  it('keeps one top-level heading on the screen', () => {
    // Assert
    // A step of a flow is still a screen: a second `h1` leaves the document
    // with two competing titles for a reader navigating by heading level.
    expect(host.querySelectorAll('h1').length).toBe(1);
    expect(normalize(host.querySelector('h1'))).toBe(HEADING);
  });

  it('offers exactly one primary control', () => {
    // Act
    const create = buttonNamed(host, CREATE_BUTTON);
    const saveButton = buttonNamed(host, SAVE_BUTTON);

    // Assert
    // One Primary per view, and it is the act that ends the flow. The negative
    // halves are not redundant: the filled treatment arriving on Save or Copy
    // makes two of three buttons read as the thing to press next — on the one
    // screen where pressing the wrong one loses the codes.
    expect(create?.classList.contains(FILLED_CLASS)).toBe(true);
    expect(host.querySelectorAll(`.${FILLED_CLASS}`).length).toBe(1);
    // Save is Outline: secondary, but a real route out with the codes.
    expect(saveButton?.classList.contains(OUTLINE_CLASS)).toBe(true);
    expect(saveButton?.classList.contains(FILLED_CLASS)).toBe(false);
  });

  it('gives the clipboard the lightest treatment on the screen', () => {
    // Act
    const copy = buttonNamed(host, COPY_BUTTON);

    // Assert
    // Ghost, the book's tertiary variant — one step below the Outline on Save,
    // not level with it. The sentence beside these two controls says the
    // clipboard is readable by every other app on the device; rendering both at
    // the same weight tells the eye the two routes are equivalent while the
    // copy underneath says they are not, and the eye is faster. The treatment
    // has to agree with the prose or one of them is decoration.
    expect(copy?.classList.contains(GHOST_CLASS)).toBe(true);
    expect(copy?.classList.contains(OUTLINE_CLASS)).toBe(false);
    expect(copy?.classList.contains(FILLED_CLASS)).toBe(false);
  });

  it('says that nothing is saved until the last step', () => {
    // Act
    const standing = elementSaying(host, STANDING_LINE);

    // Assert
    // A standing fact about the flow, not an event, so it is content rather
    // than something the region announces. Without it a person who saved the
    // file and walked away believes they have an account.
    expect(standing).not.toBeNull();
    expect(standing?.closest(LIVE_REGION_SELECTOR)).toBeNull();
  });

  it('finds no control for a name the screen does not carry', () => {
    // Assert
    // This tests the helper, not the component, and it is here on purpose: it
    // is the control for every `buttonNamed` assertion in this file. Without
    // it, a helper that ignored the name and handed back the first button it
    // found would make every one of those tests green on any screen that
    // rendered three buttons of any kind. `Skip` is a name this flow could
    // plausibly have grown and does not have, so a hit here is a helper
    // failure and never a copy change.
    expect(buttonNamed(host, 'Skip')).toBeNull();
  });

  function codeList(root: HTMLElement): HTMLElement | null {
    return root.querySelector<HTMLElement>('ol[role="list"]');
  }

  function codeItems(root: HTMLElement): readonly HTMLElement[] {
    return Array.from(
      root.querySelectorAll<HTMLElement>('ol[role="list"] > li'),
    );
  }

  // The ten codes as a person reads them off the screen — the code halves only,
  // with the printed positions left behind. Every payload comparison in this
  // file goes through here rather than through the item's own text, which is
  // the distinction the numbering introduced and the one the file and the
  // clipboard have to keep.
  function shownCodes(root: HTMLElement): readonly string[] {
    return codeItems(root).map(codeTextOf);
  }

  function outcomeRegion(root: HTMLElement): Element | null {
    return root.querySelector('[role="status"]');
  }
});

// Collapses the whitespace an HTML template introduces. Without it every
// whole-sentence assertion above is hostage to where Prettier wrapped the line.
function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

function normalize(element: Element | null): string {
  return collapse(element?.textContent ?? '');
}

// A payload — the saved file's bytes or the clipboard's string — cut into the
// lines a person reads out of it. Deliberately **not** collapsed and **not**
// trimmed: this is the one reading in the file that has to see a payload
// exactly as it is, because every way of smuggling a printed position into it
// is a matter of the characters around the code. Empty lines are dropped so a
// trailing newline stays a formatting choice rather than a failure, matching
// what `writes the file exactly as the codes are shown` allows.
function payloadLines(payload: string): readonly string[] {
  return payload.split('\n').filter(Boolean);
}

// The printed 1-based position inside one `<li>`, handed back as it is
// rendered — the caller strips the punctuation, because whether a muted `7.`
// or `7)` reads better is the book's business and *which* number it is, is
// this file's. Thrown rather than returned as `''`, for `checkboxInput`'s
// reason: an empty string is also what an element holding no text reads as, so
// a screen that stopped printing numbers altogether would fail the numbering
// test with `expected '' to be '1'` — a message that sends a reader off to
// look at the formatting of a number that is not on the screen at all.
function indexTextOf(item: Element): string {
  const index = item.querySelector(CODE_INDEX_SELECTOR);

  if (index === null) {
    throw new Error('A code is rendered with no printed position beside it.');
  }

  return normalize(index);
}

// The code's own text inside one `<li>`, deliberately never the item's. The
// item also holds the printed position, so its text reads `7 789A-BCDE-…` — a
// string no user of a code ever sees, which is exactly why every payload
// comparison in this file has to be built from this half and not from the line
// as a whole. Thrown rather than returned as `''` for `checkboxInput`'s
// reason, and the argument is sharper here than anywhere else in this block:
// `toContain('')` is true of every string, so a missing code element would
// leave `keeps the number beside a code out of the file and the clipboard`
// green on a file and a clipboard holding nothing at all.
function codeTextOf(item: Element): string {
  const code = item.querySelector(CODE_TEXT_SELECTOR);

  if (code === null) {
    throw new Error('A list item renders no code of its own.');
  }

  return normalize(code);
}

// Finds a button the way a screen reader announces it, so a control renamed in
// the DOM but not in the copy stops being found. `aria-label` wins over the
// text node, matching how the accessible name is computed for the shapes this
// screen uses.
function buttonNamed(
  host: HTMLElement,
  name: string,
): HTMLButtonElement | null {
  const buttons = Array.from(
    host.querySelectorAll<HTMLButtonElement>('button'),
  );

  return (
    buttons.find(
      (button) =>
        collapse(
          button.getAttribute('aria-label') ?? button.textContent ?? '',
        ) === name,
    ) ?? null
  );
}

// The one checkbox on the screen. Thrown rather than returned as `null`,
// because every test that reaches for it is about what the control says or
// gates, and a nullable here would turn a missing checkbox into a page of
// assertions that pass on `undefined`.
function checkboxInput(host: HTMLElement): HTMLInputElement {
  const input = host.querySelector<HTMLInputElement>('input[type="checkbox"]');

  if (input === null) {
    throw new Error('The codes step renders no acknowledgement checkbox.');
  }

  return input;
}

// The accessible name, computed the way the browser computes it for the shapes
// this screen uses: `aria-label` first, then `aria-labelledby`, then the
// associated `<label>`. All three, and not just the `<label>`, because
// `aria-labelledby` pointing at the consequence block is the same defect
// written a different way and moves no text on the screen.
function accessibleNameOf(host: HTMLElement, control: HTMLElement): string {
  const label = control.getAttribute('aria-label');

  if (label !== null) {
    return collapse(label);
  }

  const labelledBy = control.getAttribute('aria-labelledby');

  if (labelledBy !== null) {
    return collapse(
      labelledBy
        .split(/\s+/)
        .map((id) => host.querySelector(`#${id}`)?.textContent ?? '')
        .join(' '),
    );
  }

  const id = control.getAttribute('id');
  const associated =
    id === null ? null : host.querySelector(`label[for="${id}"]`);

  return normalize(associated ?? control.closest('label'));
}

// The element whose own text *is* the sentence — the paragraph carrying it,
// rather than every ancestor that contains it. Document order, so a wrapper
// that happened to hold nothing else would be found before its child; that is
// the same position for the comparisons above, which is all this is used for.
function elementSaying(root: Element | null, sentence: string): Element | null {
  const elements = Array.from(root?.querySelectorAll('*') ?? []);

  return elements.find((element) => normalize(element) === sentence) ?? null;
}

// Document order, which is the order a screen reader reads and the order the
// page is laid out in — not source order in the template and not visual order
// under CSS, but the one both of those have to agree with to mean anything.
function precedes(first: Element | null, second: Element | null): boolean {
  if (first === null || second === null) {
    return false;
  }

  return (
    (first.compareDocumentPosition(second) &
      Node.DOCUMENT_POSITION_FOLLOWING) !==
    0
  );
}
