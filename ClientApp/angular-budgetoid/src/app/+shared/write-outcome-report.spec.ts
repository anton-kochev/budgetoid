// Where one write's answer renders, driven directly over the function that
// decides it.
//
// **The copy is asserted against written-out strings and never against the
// constants the module exports.** A case reading `UNREACHABLE_SENTENCE` back
// out of the module it is testing asserts that a string equals itself: rewrite
// the sentence and every such case stays green while the product says something
// the design book does not. `docs/design/components.md`'s state table says of
// itself that the copy is the specification, so the specification is what is
// written here.
//
// **The lookup cases are the reason this file exists at all.** Three screens
// call this, and the split between a message under a control and a line in the
// region is the whole of what the chapter asks for — so the interesting cases
// are a map with keys on both sides of it, a key that hits `Object.prototype`,
// and two wire members landing on one control.
import type { WriteOutcome } from '@app-core/api/write-outcome';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { describe, expect, it } from 'vitest';
import {
  SILENT_WRITE_REPORT,
  clearFieldMessages,
  markFieldMessages,
  rowActReportOf,
  writeReportOf,
} from './write-outcome-report';

// The wire keys a form of this shape can place, built from pairs. The API's
// keys are C# member names and this project's lint rule demands camelCase of an
// object literal's properties, so a literal cannot spell what the wire sends.
const FORM_KEYS: ReadonlyMap<string, string> = new Map([
  ['Name', 'name'],
  ['OpeningBalance', 'openingBalance'],
]);

function invalid(
  ...entries: readonly (readonly [string, readonly string[]])[]
): WriteOutcome {
  return { errors: new Map(entries), state: 'invalid' };
}

describe('writeReportOf', () => {
  it('says nothing at all about a write that landed', () => {
    // Arrange

    // Act
    const report = writeReportOf({ state: 'recorded' }, FORM_KEYS);

    // Assert — no field carries a message and the region carries no sentence
    // of this chapter's, which is the table's first row.
    expect(report.fields.size).toBe(0);
    expect(report.lines).toEqual([]);
  });

  it('says nothing about a write that never left the browser', () => {
    // Arrange — `locked` has no row in the table, and the omission is the
    // rule: the screen's locked notice is already the account of it, and a
    // second sentence is the duplicate the shared region refuses.

    // Act
    const report = writeReportOf({ state: 'locked' }, FORM_KEYS);

    // Assert
    expect(report.fields.size).toBe(0);
    expect(report.lines).toEqual([]);
  });

  it('tells somebody their entry is already saved when the identifier collided', () => {
    // Arrange — a create's answer and never a rename's. The sentence offers no
    // second press, because the same press sends the same client-minted id and
    // collects the same 409.

    // Act
    const report = writeReportOf({ state: 'duplicate-identifier' }, FORM_KEYS);

    // Assert
    expect(report.lines).toEqual([
      'This entry is already saved. Reload the page to see it.',
    ]);
    expect(report.fields.size).toBe(0);
  });

  it('sends somebody to the list when a payee of that name cannot be read', () => {
    // Arrange — the payee create is `duplicate-name`'s one source in the
    // product, and the sentence is for the case the form's own re-read could
    // not resolve.

    // Act
    const report = writeReportOf({ state: 'duplicate-name' }, FORM_KEYS);

    // Assert
    expect(report.lines).toEqual([
      'This payee already exists under a name this tab can’t read. Choose it ' +
        'from the list, or use a different name.',
    ]);
  });

  it('promises a retry only where a minute is a real remedy', () => {
    // Arrange — the reassurance clause is the whole difference from the
    // Account keys section's sentence: somebody is looking at a form holding
    // text they typed.

    // Act
    const report = writeReportOf({ state: 'unreachable' }, FORM_KEYS);

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t reach the server. Nothing you typed has been lost ' +
        '— try again in a minute.',
    ]);
  });

  it('offers a reload and never a retry when the server judged', () => {
    // Arrange — the distinction a reader collapses. A 400 with no usable map
    // is the server looking and saying no, and a minute changes nothing; the
    // sentence therefore ends in **copy it, then reload**, in that order,
    // because a reload is the one act that discards the typed value.

    // Act
    const report = writeReportOf({ state: 'unreadable' }, FORM_KEYS);

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t save this, and didn’t say why. What you typed is ' +
        'still here — copy it, then reload the page.',
    ]);
  });

  it('never advises a retry on a judgement', () => {
    // Arrange — the two sentences above read alike to a skim and the whole
    // point is that they do not read alike to a person. *Try again in a
    // minute* fits everywhere and is false of a refusal.

    // Act
    const judged = writeReportOf({ state: 'unreadable' }, FORM_KEYS);
    const silent = writeReportOf({ state: 'unreachable' }, FORM_KEYS);

    // Assert
    expect(judged.lines.join('')).not.toContain('try again');
    expect(silent.lines.join('')).toContain('try again in a minute');
  });

  it('puts a keyed sentence under the control it names', () => {
    // Arrange — verbatim, and the client holds no copy of its own: a
    // client-authored lookup would need to be total over every string the API
    // can send.

    // Act
    const report = writeReportOf(
      invalid(['Name', ['Account name must be unique.']]),
      FORM_KEYS,
    );

    // Assert
    expect(report.fields.get('name')).toEqual(['Account name must be unique.']);
    expect(report.lines).toEqual([]);
  });

  it('puts a sentence this form cannot place into the region instead', () => {
    // Arrange — `Id` is a member the browser mints and no control carries, so
    // there is no field to hang a message on. A dropped entry would be this
    // chapter's own defect with a better excuse.

    // Act
    const report = writeReportOf(invalid(['Id', ['Malformed.']]), FORM_KEYS);

    // Assert
    expect(report.lines).toEqual(['Malformed.']);
    expect(report.fields.size).toBe(0);
  });

  it('renders one answer in two places rather than choosing between them', () => {
    // Arrange — the rule a reader will undo: exclusivity is over **outcomes**,
    // not sentences, so a map with keys on both sides puts messages under the
    // controls it names *and* lines in the region for the ones it does not.
    // Order is the map's, on both sides.

    // Act
    const report = writeReportOf(
      invalid(
        ['Id', ['Malformed.']],
        ['Name', ['Account name must be unique.']],
        ['CurrencyCode', ['Unknown currency.']],
      ),
      FORM_KEYS,
    );

    // Assert
    expect(report.fields.get('name')).toEqual(['Account name must be unique.']);
    expect(report.lines).toEqual(['Malformed.', 'Unknown currency.']);
  });

  it('places nothing on a key that only hits Object.prototype', () => {
    // Arrange — the argument for the `Map`. On an object literal
    // `keys['constructor']` answers a function, the entry is placed under a
    // control that does not exist, and the sentence is never seen.

    // Act
    const report = writeReportOf(
      invalid(['constructor', ['Nice try.']]),
      FORM_KEYS,
    );

    // Assert
    expect(report.fields.size).toBe(0);
    expect(report.lines).toEqual(['Nice try.']);
  });

  it('keeps both sentences when two wire members share one control', () => {
    // Arrange — reachable the day a form maps two members onto one field, and
    // silent when broken: a `set` that replaced would drop a sentence the
    // server sent, which is the defect this whole chapter is about one layer
    // in.
    const shared: ReadonlyMap<string, string> = new Map([
      ['Name', 'name'],
      ['NameKey', 'name'],
    ]);

    // Act
    const report = writeReportOf(
      invalid(['Name', ['Too long.']], ['NameKey', ['Malformed.']]),
      shared,
    );

    // Assert
    expect(report.fields.get('name')).toEqual(['Too long.', 'Malformed.']);
  });
});

// The five writes that hold no typed text: a row's delete, and a row's move.
//
// **The copy is written out here too, for the reason the head of this file
// gives.** These four sentences are the whole of what closes the gap
// `docs/design/components.md` left open, so a case reading them back out of the
// module would assert that a string equals itself.
describe('rowActReportOf', () => {
  it('says the server was not reached, and that nothing has been deleted', () => {
    // Arrange — the reassurance clause is this act's own. The form's sentence
    // says *nothing you typed has been lost*, and a delete holds nothing
    // anybody typed; what a person needs to know is that the row they pressed
    // Delete on is still theirs.

    // Act
    const report = rowActReportOf({ state: 'unreachable' }, 'removal');

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t reach the server. Nothing has been deleted — try ' +
        'again in a minute.',
    ]);
    expect(report.fields.size).toBe(0);
  });

  it('offers neither a retry nor a reload when a delete was judged', () => {
    // Arrange — the asymmetry against the form's unreadable sentence, and it
    // is the point rather than an oversight. No retry, because the server
    // judged and the same press collects the same judgement; and no *copy it,
    // then reload*, because nothing was typed and the screen is already
    // telling the truth — the row is still on it.

    // Act
    const report = rowActReportOf({ state: 'unreadable' }, 'removal');

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t delete this, and didn’t say why. The row is still ' +
        'here.',
    ]);
    expect(report.lines.join('')).not.toContain('try again');
    expect(report.lines.join('')).not.toContain('copy it');
  });

  it('says the server was not reached, and that nothing has moved', () => {
    // Arrange — a placement's own reassurance: the row is where the drag
    // started, because the list is mutated on success and on nothing else.

    // Act
    const report = rowActReportOf({ state: 'unreachable' }, 'placement');

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t reach the server. Nothing has moved — try again in ' +
        'a minute.',
    ]);
  });

  it('offers no retry when a move was judged', () => {
    // Arrange — the same asymmetry, in the sentence a move gets.

    // Act
    const report = rowActReportOf({ state: 'unreadable' }, 'placement');

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t move this, and didn’t say why. Everything is where ' +
        'it was.',
    ]);
    expect(report.lines.join('')).not.toContain('try again');
  });

  it('reads a conflict answering a delete as an answer it cannot read', () => {
    // Arrange — not a fudge, and this is where the reason is asserted rather
    // than only written down. `duplicate-identifier` is a *create's* answer:
    // a 409 arriving over a delete is an answer this client genuinely has no
    // reading for, so it lands on the unreadable sentence by meaning and not
    // by falling through.

    // Act
    const report = rowActReportOf({ state: 'duplicate-identifier' }, 'removal');

    // Assert — the delete's own sentence, and never the form's *this entry is
    // already saved*, which would tell somebody their deletion was recorded.
    expect(report.lines).toEqual([
      'Budgetoid couldn’t delete this, and didn’t say why. The row is still ' +
        'here.',
    ]);
    expect(report.lines.join('')).not.toContain('already saved');
  });

  it('reads a duplicate name answering a move as an answer it cannot read', () => {
    // Arrange — `duplicate-name` is the payee create's word and reaches a
    // placement never. The same reading, on the other act.

    // Act
    const report = rowActReportOf({ state: 'duplicate-name' }, 'placement');

    // Assert
    expect(report.lines).toEqual([
      'Budgetoid couldn’t move this, and didn’t say why. Everything is where ' +
        'it was.',
    ]);
  });

  it('sends every keyed sentence to the region, because there is no form', () => {
    // Arrange — `Name` is a key a *form* on either screen would place under a
    // control. There is no form here: the press was a Delete on a row, so the
    // only place a sentence can land is the region.

    // Act
    const report = rowActReportOf(
      invalid(['Name', ['Taken.']], ['Id', ['Malformed.']]),
      'removal',
    );

    // Assert
    expect(report.fields.size).toBe(0);
    expect(report.lines).toEqual(['Taken.', 'Malformed.']);
  });

  it('says nothing at all about a delete that landed', () => {
    // Arrange — the positive control the four sentences need: a mapping that
    // spoke on every word would pass all of them.

    // Act
    const report = rowActReportOf({ state: 'recorded' }, 'removal');

    // Assert
    expect(report.lines).toEqual([]);
    expect(report.fields.size).toBe(0);
  });

  it('says nothing about a word these paths cannot reach', () => {
    // Arrange — `locked` is unreachable here in practice: neither a delete nor
    // a placement seals anything, so no request is ever refused before it is
    // sent. It is answered rather than handled twice, because the table has to
    // be total over the type.

    // Act
    const report = rowActReportOf({ state: 'locked' }, 'placement');

    // Assert
    expect(report.lines).toEqual([]);
  });
});

describe('markFieldMessages and clearFieldMessages', () => {
  function form(): FormGroup {
    return new FormBuilder().nonNullable.group({
      name: ['Everyday', [Validators.required]],
      openingBalance: [0],
    });
  }

  it('puts the named control into its error state and answers the first', () => {
    // Arrange — the error is what switches Material's form field into showing
    // its `mat-error` children and binding them with `aria-describedby`; the
    // answer is what the chapter's *focus moves to the first such control* is
    // built on.
    const group = form();
    const report = writeReportOf(
      invalid(['Name', ['Account name must be unique.']]),
      FORM_KEYS,
    );

    // Act
    const first = markFieldMessages(group, report);

    // Assert
    expect(first).toBe('name');
    expect(group.controls['name']?.hasError('server')).toBe(true);
    expect(group.controls['openingBalance']?.errors).toBeNull();
  });

  it('answers null where no control carries a message', () => {
    // Arrange — the half that keeps focus where the press left it. A handler
    // that focused something anyway would take a keyboard user off the control
    // they are about to press again.
    const group = form();

    // Act
    const first = markFieldMessages(
      group,
      writeReportOf({ state: 'unreachable' }, FORM_KEYS),
    );

    // Assert
    expect(first).toBeNull();
    expect(group.valid).toBe(true);
  });

  it('refuses a report naming a control the form does not have', () => {
    // Arrange — a caller defect rather than a state: the report was built from
    // the caller's own lookup, so a name that misses here is a sentence the
    // server sent rendering nowhere at all. It throws rather than becoming a
    // result, because a caught throw rendered as a sentence is a bug wearing a
    // UI.
    const group = form();
    const report = writeReportOf(
      invalid(['Type', ['Unknown.']]),
      new Map([['Type', 'type']]),
    );

    // Act, Assert
    expect(() => markFieldMessages(group, report)).toThrow('type');
  });

  it('takes the server’s sentence off a control on the next write', () => {
    // Arrange
    const group = form();

    markFieldMessages(
      group,
      writeReportOf(invalid(['Name', ['Taken.']]), FORM_KEYS),
    );

    // Act
    clearFieldMessages(group);

    // Assert
    expect(group.controls['name']?.hasError('server')).toBe(false);
    expect(group.valid).toBe(true);
  });

  it('leaves a control that is invalid on its own terms invalid', () => {
    // Arrange — the reason the clear re-runs the validators rather than
    // calling `setErrors(null)`. Cleared the other way, a field that is also
    // empty comes back valid and the form submits something it had already
    // refused.
    const group = form();

    group.controls['name']?.setValue('');
    markFieldMessages(
      group,
      writeReportOf(invalid(['Name', ['Taken.']]), FORM_KEYS),
    );

    // Act
    clearFieldMessages(group);

    // Assert
    expect(group.controls['name']?.hasError('server')).toBe(false);
    expect(group.controls['name']?.hasError('required')).toBe(true);
  });
});

describe('SILENT_WRITE_REPORT', () => {
  it('holds nothing to render', () => {
    // Arrange — the value a screen sits at before its first write and after
    // one that landed.

    // Act, Assert
    expect(SILENT_WRITE_REPORT.fields.size).toBe(0);
    expect(SILENT_WRITE_REPORT.lines).toEqual([]);
  });
});
