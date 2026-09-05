// The one place a refusal is turned into a word, and the one place that word
// can be pinned without a screen.
//
// **The controls that matter here are the pairs, not the singles.** A case
// asserting `duplicate-name` on its own passes a classifier that answers
// `duplicate-name` for every 409, which is exactly the implementation this file
// exists to retire; a case asserting `unreachable` on a 500 passes one that
// answers `unreachable` for every refusal. So each pair that shares a status or
// shares a shape is asserted **together and asserted unequal** — the property
// `docs/business-logic/payees.md` names when it argues for `conflictKind` at
// all, brought across the wire.
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { writeOutcomeOf, type WriteOutcome } from './write-outcome';

function refusal(status: number, error: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error, statusText: 'Refused' });
}

// A validation problem document, built from pairs rather than written as a
// literal. The API's keys are the C# member names — `Name`, `Amount`,
// `OpeningBalance` — and this project's lint rule reaches into object literals
// and demands camelCase of them, so a literal cannot spell what the wire
// actually sends. Entries are also nearer the truth: this is a parsed body.
function withErrors(
  ...entries: readonly (readonly [string, readonly string[]])[]
): Record<string, unknown> {
  return { errors: Object.fromEntries(entries) };
}

// The map an outcome carries, as plain pairs, so a case can compare it whole.
function placed(
  outcome: WriteOutcome,
): readonly (readonly [string, readonly string[]])[] {
  return outcome.state === 'invalid' ? [...outcome.errors] : [];
}

describe('writeOutcomeOf', () => {
  it('reads a duplicate name and a duplicate identifier as two different words', () => {
    // Arrange — the defect this file was written for. Both are 409s, both
    // carry the same title and no `errors` map, and their remedies are
    // opposites: adopt the row that already exists, versus the row you meant is
    // already saved. Asserted as a pair and asserted unequal, because a
    // classifier stamping one word on every 409 satisfies either half alone.
    const name = refusal(409, { conflictKind: 'duplicate_name' });
    const identifier = refusal(409, { conflictKind: 'duplicate_identifier' });

    // Act
    const fromName = writeOutcomeOf(name);
    const fromIdentifier = writeOutcomeOf(identifier);

    // Assert
    expect(fromName).toEqual({ state: 'duplicate-name' });
    expect(fromIdentifier).toEqual({ state: 'duplicate-identifier' });
    expect(fromName.state).not.toBe(fromIdentifier.state);
  });

  it('reads a duplicate name off the document whichever status carried it', () => {
    // Arrange — the same rule, "a name this budget already holds", answers 409
    // on the payee create and **400 keyed on `Name`** on every rename and on
    // both halves of the categories screen. A classifier keyed on the status
    // gets three resources right and the fourth wrong, and the fourth is the
    // one inside the transaction write.
    const onCreate = refusal(409, { conflictKind: 'duplicate_name' });
    const onRename = refusal(
      400,
      withErrors(['Name', ['Payee name must be unique.']]),
    );

    // Act
    const fromCreate = writeOutcomeOf(onCreate);
    const fromRename = writeOutcomeOf(onRename);

    // Assert — two different next steps, so two different words: one is a row
    // to adopt and the other is a field to correct.
    expect(fromCreate).toEqual({ state: 'duplicate-name' });
    expect(fromRename.state).toBe('invalid');
    expect(placed(fromRename)).toEqual([
      ['Name', ['Payee name must be unique.']],
    ]);
  });

  it('keeps every entry of an errors map, in the order it arrived', () => {
    // Arrange — the chapter renders unplaceable entries "in the order the map
    // sends them", so the order is part of the contract rather than an
    // accident of the parse.
    const error = refusal(
      400,
      withErrors(
        ['Name', ['Account name must be unique.', 'And it must be shorter.']],
        ['OpeningBalance', ['Enter a number.']],
      ),
    );

    // Act
    const outcome = writeOutcomeOf(error);

    // Assert
    expect(placed(outcome)).toEqual([
      ['Name', ['Account name must be unique.', 'And it must be shorter.']],
      ['OpeningBalance', ['Enter a number.']],
    ]);
  });

  it('hands the errors map over as a Map, so a wire key cannot reach a prototype', () => {
    // Arrange — `constructor` is a key the server may legally send and a key
    // that hits on an object literal, which places its message under a control
    // that does not exist. Killing it here rather than at every caller is what
    // makes the design book's "never an object literal indexed by the wire key"
    // a property of the value instead of a rule people remember.
    const error = refusal(400, { errors: { constructor: ['Nice try.'] } });

    // Act
    const outcome = writeOutcomeOf(error);

    // Assert
    expect(outcome.state).toBe('invalid');
    expect(placed(outcome)).toEqual([['constructor', ['Nice try.']]]);
    expect(
      outcome.state === 'invalid' ? outcome.errors.get('toString') : 'missing',
    ).toBeUndefined();
  });

  it('drops an entry with no message in it and keeps its neighbours', () => {
    // Arrange — an empty `mat-error` is a red border with no words in it,
    // which is colour as the message. The entry that cannot be rendered goes;
    // the ones that can are still the server's sentences and still arrive.
    const error = refusal(
      400,
      withErrors(
        ['Name', []],
        ['OpeningBalance', ['   ']],
        ['Type', ['Choose a type.']],
      ),
    );

    // Act
    const outcome = writeOutcomeOf(error);

    // Assert
    expect(placed(outcome)).toEqual([['Type', ['Choose a type.']]]);
  });

  it('hands a message on exactly as it arrived, whitespace and all', () => {
    // Arrange — the control on the case above. `trim` there decides only
    // whether a string is a message; a codec that trimmed the message *itself*
    // would pass every other case in this file, because no sentence the API
    // sends today has an edge worth trimming. The client writes none of this
    // copy and may not edit it either: it is the server's, under
    // `docs/design/voice.md`, and a browser quietly rewriting it is the second
    // definition the design book refuses.
    const error = refusal(400, withErrors(['Name', [' Pick another name. ']]));

    // Act
    const outcome = writeOutcomeOf(error);

    // Assert
    expect(placed(outcome)).toEqual([['Name', [' Pick another name. ']]]);
  });

  it('reads a 400 with nothing renderable in it as an answer it cannot read', () => {
    // Arrange — three shapes a 400 can arrive in that are not validation: no
    // `errors` member, an empty one, and one whose only entry has no message.
    // Each is the unreadable sentence rather than a field turning red with
    // nothing to say — and none of them may become `unreachable`, whose advice
    // is to wait for a server that has already answered.
    const noMember = refusal(400, { title: 'Bad Request' });
    const emptyMap = refusal(400, { errors: {} });
    const emptyEntry = refusal(400, withErrors(['Name', []]));

    // Act & Assert
    expect(writeOutcomeOf(noMember)).toEqual({ state: 'unreadable' });
    expect(writeOutcomeOf(emptyMap)).toEqual({ state: 'unreadable' });
    expect(writeOutcomeOf(emptyEntry)).toEqual({ state: 'unreadable' });
  });

  it('reads a conflict with no kind, and one it has never heard of, as unreadable', () => {
    // Arrange — the chapter is explicit: a 409 whose kind is missing or
    // unrecognised takes that sentence rather than a guess. Guessing here is
    // how a browser running an older bundle than the API tells somebody to
    // reload a row that was never saved.
    const noKind = refusal(409, { title: 'Conflict' });
    const unknownKind = refusal(409, { conflictKind: 'last_passkey' });

    // Act & Assert
    expect(writeOutcomeOf(noKind)).toEqual({ state: 'unreadable' });
    expect(writeOutcomeOf(unknownKind)).toEqual({ state: 'unreadable' });
  });

  it('separates a server that failed from a server that judged', () => {
    // Arrange — the pair the design book says a reader will collapse. A dead
    // connection and a 500 are the server failing to answer, and a minute is a
    // real remedy for both; a 403 or a 404 is the server looking and saying no,
    // where a minute changes nothing. Asserted together and asserted unequal,
    // because one word for all four passes either half alone.
    const dead = refusal(0, null);
    const broken = refusal(500, 'nope');
    const forbidden = refusal(403, 'nope');
    const gone = refusal(404, 'nope');

    // Act
    const outcomes = [dead, broken, forbidden, gone].map(writeOutcomeOf);

    // Assert
    expect(outcomes).toEqual([
      { state: 'unreachable' },
      { state: 'unreachable' },
      { state: 'unreadable' },
      { state: 'unreadable' },
    ]);
    expect(outcomes[0]?.state).not.toBe(outcomes[2]?.state);
  });

  it('reads anything that is not an HTTP answer as unreadable', () => {
    // Arrange — a `NarrativeFieldMisuseError` or a `TypeError` from a mapper
    // reaches the same `catchError` as a refusal, and the request behind it may
    // well have landed. So the word is the one whose remedy is a reload rather
    // than the one that promises a retry: the row, if it exists, is what a
    // reload would show.
    const defect = new TypeError('the mapper threw');

    // Act & Assert
    expect(writeOutcomeOf(defect)).toEqual({ state: 'unreadable' });
    expect(writeOutcomeOf(undefined)).toEqual({ state: 'unreadable' });
  });

  it('answers no recorded and no locked, whatever it is handed', () => {
    // Arrange — the negative control on the classifier's range. `recorded` is
    // the caller's own knowledge that a request succeeded and `locked` is a
    // refusal taken before any request existed; a classifier that could invent
    // either would let a refused write clear a form.
    const everything: unknown[] = [
      refusal(200, { conflictKind: 'duplicate_name' }),
      refusal(204, null),
      refusal(0, null),
      refusal(400, withErrors(['Name', ['No.']])),
      refusal(409, { conflictKind: 'duplicate_identifier' }),
      refusal(500, 'nope'),
      new TypeError('the mapper threw'),
    ];

    // Act
    const states = everything.map((error) => writeOutcomeOf(error).state);

    // Assert
    expect(states).not.toContain('recorded');
    expect(states).not.toContain('locked');
  });
});
