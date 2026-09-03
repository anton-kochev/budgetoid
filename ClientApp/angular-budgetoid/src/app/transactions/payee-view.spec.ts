// The payee mapper's own spec. Like `account-view.spec.ts` it stands the mapper
// up with two functions and no `TestBed`, which is the whole reason
// `toPayeeView` takes a {@link NarrativeOpener} and a {@link NarrativeIndexer}
// rather than the service that owns both.
//
// **The indexer is recorded as well as answered**, because "was it asked at
// all" is half of what this file holds: a name that did not open must produce
// no index, and an implementation that indexed the *wire* value instead — or
// indexed `''` for a locked row — would still hand back a `nameKey`-shaped
// string and match a payee it has never read.
import type { PayeeDto } from '@app-core/api/payees-api.service';
import type { BlindIndexedField } from '@app-core/security/blind-index';
import {
  NarrativeFieldMisuseError,
  type NarrativeFieldBinding,
} from '@app-core/security/narrative-cipher';
import type {
  BlindIndexValue,
  NarrativeIndexer,
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import { matchPayeeByIndex, toPayeeView, type PayeeView } from './payee-view';

// A canonical lower-case hyphenated UUID — the one spelling the codec accepts,
// and the spelling `System.Text.Json` renders every `Guid` in.
const ROW_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e';

const sealedPayee: PayeeDto = {
  id: ROW_ID,
  name: 'AQIDBAUGBwgJCgsMDQ4PEA',
};

function recordingOpener(answer: NarrativeText): {
  readonly open: NarrativeOpener;
  readonly calls: { binding: NarrativeFieldBinding; wire: string }[];
} {
  const calls: { binding: NarrativeFieldBinding; wire: string }[] = [];

  return {
    calls,
    open: (binding, wire) => {
      calls.push({ binding, wire });

      return Promise.resolve(answer);
    },
  };
}

function recordingIndexer(answer: BlindIndexValue): {
  readonly index: NarrativeIndexer;
  readonly calls: { field: BlindIndexedField; plaintext: string }[];
} {
  const calls: { field: BlindIndexedField; plaintext: string }[] = [];

  return {
    calls,
    index: (field, plaintext) => {
      calls.push({ field, plaintext });

      return Promise.resolve(answer);
    },
  };
}

describe('toPayeeView', () => {
  it('opens the name under the payees name binding for the row’s own id', async () => {
    // Arrange
    const opener = recordingOpener({ state: 'text', value: 'Corner Shop' });
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert
    expect(opener.calls).toEqual([
      {
        binding: { table: 'payees', column: 'name', rowId: ROW_ID },
        wire: sealedPayee.name,
      },
    ]);
    expect(view.name).toEqual({ state: 'text', value: 'Corner Shop' });
  });

  it('keys the opened text under the payees name field and no row id', async () => {
    // Arrange — the deliberate inverse of the opener's binding: an index has to
    // be *equal* for equal names across rows, so a row id in the message would
    // make every payee's key unique and the lookup would never match anything.
    const opener = recordingOpener({ state: 'text', value: 'Corner Shop' });
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert — the *opened* text, never the wire value beside it.
    expect(indexer.calls).toEqual([
      { field: { table: 'payees', column: 'name' }, plaintext: 'Corner Shop' },
    ]);
    expect(view.nameKey).toBe('index-1');
  });

  it('has no key and asks for none when the name did not open', async () => {
    // Arrange — `unreadable`: the key was there and these bytes did not
    // authenticate. There is no text to key, so there is nothing to ask.
    const opener = recordingOpener({ state: 'unreadable' });
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert — `null` rather than a key over `''`, which would match every
    // other payee whose name did not open and reuse one of them.
    expect(view.nameKey).toBeNull();
    expect(view.name).toEqual({ state: 'unreadable' });
    expect(indexer.calls).toEqual([]);
  });

  it('has no key and asks for none while the account is locked', async () => {
    // Arrange — a browser holding no content key holds no index key either, so
    // the second call would answer `locked` and buys nothing but a round of
    // work.
    const opener = recordingOpener({ state: 'locked' });
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert
    expect(view.nameKey).toBeNull();
    expect(view.name).toEqual({ state: 'locked' });
    expect(indexer.calls).toEqual([]);
  });

  it('keeps the opened text when the index answers locked', async () => {
    // Arrange — reachable: a read compares the generation counter, so custody
    // moving between the open and the index drops the second alone.
    const opener = recordingOpener({ state: 'text', value: 'Corner Shop' });
    const indexer = recordingIndexer({ state: 'locked' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert — the text is still true and still renders; only matching is off.
    expect(view.name).toEqual({ state: 'text', value: 'Corner Shop' });
    expect(view.nameKey).toBeNull();
  });

  it('carries the identifier through untouched', async () => {
    // Arrange — the id is the associated data the name was sealed against, so
    // it is also what a write re-seals under.
    const opener = recordingOpener({ state: 'text', value: 'Corner Shop' });
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const view = await toPayeeView(sealedPayee, opener.open, indexer.index);

    // Assert
    expect(view).toEqual({
      id: ROW_ID,
      name: { state: 'text', value: 'Corner Shop' },
      nameKey: 'index-1',
    });
  });

  it('lets a misuse rejection from the opener propagate', async () => {
    // Arrange — the codec's word for a refusal it made about the *call*. It
    // says nothing about the column, so it may not become `unreadable`.
    const refused: NarrativeOpener = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));
    const indexer = recordingIndexer({ state: 'computed', value: 'index-1' });

    // Act
    const mapping = toPayeeView(sealedPayee, refused, indexer.index);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });

  it('lets a misuse rejection from the indexer propagate', async () => {
    // Arrange — the same argument on the other capability: a pair the index
    // codec does not publish is this client's defect, not a fact about a row.
    const opener = recordingOpener({ state: 'text', value: 'Corner Shop' });
    const refused: NarrativeIndexer = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));

    // Act
    const mapping = toPayeeView(sealedPayee, opener.open, refused);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });
});

describe('matchPayeeByIndex', () => {
  const cornerShop: PayeeView = {
    id: ROW_ID,
    name: { state: 'text', value: 'Corner Shop' },
    nameKey: 'index-corner-shop',
  };

  // A payee this browser could not read. Its key is `null` by construction, and
  // that is the whole of what keeps it out of every match.
  const unreadable: PayeeView = {
    id: '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6f',
    name: { state: 'unreadable' },
    nameKey: null,
  };

  it('answers the payee whose key equals the one asked for', () => {
    // Arrange

    // Act
    const match = matchPayeeByIndex(
      [unreadable, cornerShop],
      'index-corner-shop',
    );

    // Assert
    expect(match).toBe(cornerShop);
  });

  it('answers null when no key matches', () => {
    // Arrange — the miss is what mints a row and posts a create, so it has to
    // be a value the caller can branch on rather than an exception.

    // Act
    const match = matchPayeeByIndex([cornerShop], 'index-somebody-else');

    // Assert
    expect(match).toBeNull();
  });

  it('never matches a payee whose name did not open', () => {
    // Arrange — such a row can never be reused: this browser cannot tell what
    // name it holds, and reusing it would file a transaction against a
    // counterparty nobody chose. It will produce a create the server's unique
    // index may refuse, which is the loud outcome and the intended one.

    // Act
    const match = matchPayeeByIndex([unreadable], 'index-corner-shop');

    // Assert
    expect(match).toBeNull();
  });

  it('never matches on a null key asked for by a caller', () => {
    // Arrange — a defensive pin over the one shape that would let two
    // unreadable payees match each other if the key were ever widened.

    // Act
    const match = matchPayeeByIndex([unreadable], '');

    // Assert
    expect(match).toBeNull();
  });
});
