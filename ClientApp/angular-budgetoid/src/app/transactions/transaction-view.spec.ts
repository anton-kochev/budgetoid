// The transaction mapper's own spec, standing it up with one recorded function
// and no `TestBed`.
//
// **Every case here is about which binding a value was opened under**, because
// that is the one defect on this row that produces no error anywhere. Four of
// the five sealed members belong to *other* rows, and opening one of them under
// this transaction's own id fails the tag check: the value comes back
// `unreadable`, which is the same word a genuinely damaged column produces, so
// a screen shows an em dash and nothing on either side ever names the cause.
// The opener is therefore recorded rather than merely answered, and the
// assertions read the bindings.
import type { TransactionDto } from '@app-core/api/transactions-api.service';
import type { NarrativeRequest } from '@app-core/security/narrative-batch';
import {
  NarrativeFieldMisuseError,
  type NarrativeFieldBinding,
} from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import {
  toTransactionView,
  transactionNarrativeRequests,
} from './transaction-view';

// Five canonical lower-case hyphenated UUIDs, one per row a member belongs to.
// They differ in their last character so a binding that reached for the wrong
// one is legible in a failure message.
const TRANSACTION_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000001';
const ACCOUNT_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000002';
const PAYEE_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000003';
const CATEGORY_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000004';
const GROUP_ID = '0199c3d4-5f6a-7b8c-9d0e-000000000005';

const sealedTransaction: TransactionDto = {
  id: TRANSACTION_ID,
  amount: -20.5,
  date: '2026-07-14',
  description: 'sealed-description',
  createdAtUtc: '2026-07-14T10:00:00Z',
  accountId: ACCOUNT_ID,
  accountName: 'sealed-account-name',
  currencyCode: 'USD',
  currencySymbol: '$',
  payeeId: PAYEE_ID,
  payeeName: 'sealed-payee-name',
  categoryId: CATEGORY_ID,
  categoryName: 'sealed-category-name',
  categoryGroupId: GROUP_ID,
  categoryGroupName: 'sealed-group-name',
};

// A transaction naming nothing but its account: no note, no payee, no category.
// Every one of those is a legal row, and each of the four members is `null` on
// the wire beside a `null` identifier.
const bareTransaction: TransactionDto = {
  ...sealedTransaction,
  description: null,
  payeeId: null,
  payeeName: null,
  categoryId: null,
  categoryName: null,
  categoryGroupId: null,
  categoryGroupName: null,
};

// The same row with the six optional members *absent* rather than null. Four
// of the five sealed members are `?:` on `TransactionDto`, so `undefined` is a
// shape the wire really produces, and the both-halves rule is written with `==`
// so that it reads the same absence either way.
const noOptionalMembers: TransactionDto = {
  id: TRANSACTION_ID,
  amount: -20.5,
  date: '2026-07-14',
  description: 'sealed-description',
  createdAtUtc: '2026-07-14T10:00:00Z',
  accountId: ACCOUNT_ID,
  accountName: 'sealed-account-name',
  currencyCode: 'USD',
  currencySymbol: '$',
};

// Answers with the wire value's own name so a view can be read back to the
// member it came from, and records the binding each open was made under.
function recordingOpener(): {
  readonly open: NarrativeOpener;
  readonly calls: { binding: NarrativeFieldBinding; wire: string }[];
} {
  const calls: { binding: NarrativeFieldBinding; wire: string }[] = [];

  return {
    calls,
    open: (binding, wire) => {
      calls.push({ binding, wire });

      return Promise.resolve<NarrativeText>({ state: 'text', value: wire });
    },
  };
}

describe('toTransactionView', () => {
  it('opens the note under this row’s own description binding', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    const view = await toTransactionView(sealedTransaction, opener.open);

    // Assert — `transactions.description` is the one member bound to the
    // transaction, which is exactly why the other four cannot be.
    expect(opener.calls).toContainEqual({
      binding: {
        table: 'transactions',
        column: 'description',
        rowId: TRANSACTION_ID,
      },
      wire: 'sealed-description',
    });
    expect(view.description).toEqual({
      state: 'text',
      value: 'sealed-description',
    });
  });

  it('opens the account name under the account’s row id', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'accounts', column: 'name', rowId: ACCOUNT_ID },
      wire: 'sealed-account-name',
    });
  });

  it('opens the payee name under the payee’s row id', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'payees', column: 'name', rowId: PAYEE_ID },
      wire: 'sealed-payee-name',
    });
  });

  it('opens the category name under the category’s row id', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: { table: 'categories', column: 'name', rowId: CATEGORY_ID },
      wire: 'sealed-category-name',
    });
  });

  it('opens the group name under the group’s row id', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(opener.calls).toContainEqual({
      binding: {
        table: 'category_groups',
        column: 'name',
        rowId: GROUP_ID,
      },
      wire: 'sealed-group-name',
    });
  });

  it('makes five opens and no more', async () => {
    // Arrange — the census beside the five cases above. Together they say the
    // five bindings are these five and that nothing was opened twice: a mapper
    // that opened one member under two bindings to "try both" would satisfy
    // every `toContainEqual` above on its own.
    const opener = recordingOpener();

    // Act
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(opener.calls).toHaveLength(5);
  });

  it('carries the scalars through and words for the sealed members', async () => {
    // Arrange
    const opener = recordingOpener();

    // Act
    const view = await toTransactionView(sealedTransaction, opener.open);

    // Assert — every identifier survives, because each is the associated data
    // its neighbour was sealed against and a later write re-seals under it.
    expect(view).toEqual({
      accountId: ACCOUNT_ID,
      accountName: { state: 'text', value: 'sealed-account-name' },
      amount: -20.5,
      categoryGroupId: GROUP_ID,
      categoryGroupName: { state: 'text', value: 'sealed-group-name' },
      categoryId: CATEGORY_ID,
      categoryName: { state: 'text', value: 'sealed-category-name' },
      createdAtUtc: '2026-07-14T10:00:00Z',
      currencyCode: 'USD',
      currencySymbol: '$',
      date: '2026-07-14',
      description: { state: 'text', value: 'sealed-description' },
      id: TRANSACTION_ID,
      payeeId: PAYEE_ID,
      payeeName: { state: 'text', value: 'sealed-payee-name' },
    });
  });

  it('leaves an absent member null and opens nothing for it', async () => {
    // Arrange — a column holding no value is `NarrativeText | null` at the edge
    // that reads the row, never a fourth word: whether a column is null is
    // known before any key is involved, and "nobody filed a note" is not "we
    // could not read this".
    const opener = recordingOpener();

    // Act
    const view = await toTransactionView(bareTransaction, opener.open);

    // Assert
    expect(view.description).toBeNull();
    expect(view.payeeName).toBeNull();
    expect(view.categoryName).toBeNull();
    expect(view.categoryGroupName).toBeNull();
    expect(opener.calls).toHaveLength(1);
  });

  it('never opens a foreign name under a substitute binding', async () => {
    // Arrange — a name arriving without the identifier it was sealed against.
    // The server does not send this shape, and the point is what happens if it
    // ever does: the transaction's own id is sitting right there and using it
    // produces `unreadable` — a sentence about damaged data over a row that is
    // perfectly fine.
    const opener = recordingOpener();
    const orphanedName: TransactionDto = {
      ...bareTransaction,
      payeeName: 'sealed-payee-name',
    };

    // Act
    const view = await toTransactionView(orphanedName, opener.open);

    // Assert
    expect(view.payeeName).toBeNull();
    expect(opener.calls).toHaveLength(1);
  });

  it('carries locked and unreadable through without collapsing them', async () => {
    // Arrange — the two words a mapper is most tempted to turn into `''` or a
    // dash. `locked` is a fact about this tab and `unreadable` a fact about
    // this value; a screen handed a string for either can never tell them apart
    // again.
    const open: NarrativeOpener = (binding) =>
      Promise.resolve<NarrativeText>(
        binding.table === 'payees'
          ? { state: 'locked' }
          : { state: 'unreadable' },
      );

    // Act
    const view = await toTransactionView(sealedTransaction, open);

    // Assert
    expect(view.payeeName).toEqual({ state: 'locked' });
    expect(view.description).toEqual({ state: 'unreadable' });
    expect(view.accountName).toEqual({ state: 'unreadable' });
  });

  it('lets a misuse rejection propagate instead of reporting unreadable', async () => {
    // Arrange — a refusal the codec made about the *call*, which says nothing
    // whatever about what is stored. Rendered as damaged text it is a bug
    // wearing a UI, in front of somebody who can do nothing about it.
    const refused: NarrativeOpener = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));

    // Act
    const mapping = toTransactionView(sealedTransaction, refused);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });
});

// A stable spelling of one request, so two lists can be compared without either
// one's order being asserted. All four fields, for the reason `narrative-batch`
// keys on all four: three of them say which row's envelope this is, and the
// wire value says which read of it. Kept out of that module deliberately — its
// key grammar is private, and a spec reaching for it would be a second opinion
// about which requests are the same request.
function requestKey(request: NarrativeRequest): string {
  return [
    request.binding.table,
    request.binding.column,
    request.binding.rowId,
    request.wire,
  ].join('|');
}

function sortedRequests(
  requests: readonly NarrativeRequest[],
): readonly NarrativeRequest[] {
  return [...requests].sort((left, right) =>
    requestKey(left).localeCompare(requestKey(right)),
  );
}

// The batch hint beside the mapper, and **every case here spells its expected
// binding out rather than reading one back from the thing under test.**
//
// That is the whole shape of this block, and it is a reaction to a measurement
// rather than a preference. The obvious spec — run the mapper with a recording
// opener, run the collector, assert the two agree — was written and then run
// against a mutant with `payeeNameBinding(dto.id)` substituted into *both*
// halves, which is exactly the defect `transaction-view.ts` says it exists to
// prevent and exactly the way a copy-paste drifts. The comparison reported a
// match on every DTO shape it was given. It could not even see that the mutant
// changed the *arity* of the answer — `dto.id` is always present, so the
// both-halves rule stops firing and a row with a payee name and no payee id
// grows a fifth request out of nothing.
//
// So the five pairings are held by five independent literals below, each naming
// its own row's identifier and, for the four foreign ones, naming this
// transaction's id as the wrong answer. The differential check is kept at the
// bottom as a drift detector and is labelled as one.
describe('transactionNarrativeRequests', () => {
  it('pairs the note with this row’s own description binding', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert — `transactions.description` is the one member bound to the
    // transaction, which is the whole reason the other four cannot be.
    expect(
      requests.filter((request) => request.binding.table === 'transactions'),
    ).toEqual([
      {
        binding: {
          table: 'transactions',
          column: 'description',
          rowId: TRANSACTION_ID,
        },
        wire: 'sealed-description',
      },
    ]);
  });

  it('pairs the account name with the account’s row id, never this row’s', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert
    const account = requests.filter(
      (request) => request.binding.table === 'accounts',
    );

    expect(account).toEqual([
      {
        binding: { table: 'accounts', column: 'name', rowId: ACCOUNT_ID },
        wire: 'sealed-account-name',
      },
    ]);
    expect(account[0].binding.rowId).not.toBe(TRANSACTION_ID);
  });

  it('pairs the payee name with the payee’s row id, never this row’s', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert
    const payee = requests.filter(
      (request) => request.binding.table === 'payees',
    );

    expect(payee).toEqual([
      {
        binding: { table: 'payees', column: 'name', rowId: PAYEE_ID },
        wire: 'sealed-payee-name',
      },
    ]);
    expect(payee[0].binding.rowId).not.toBe(TRANSACTION_ID);
  });

  it('pairs the category name with the category’s row id, never this row’s', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert
    const category = requests.filter(
      (request) => request.binding.table === 'categories',
    );

    expect(category).toEqual([
      {
        binding: { table: 'categories', column: 'name', rowId: CATEGORY_ID },
        wire: 'sealed-category-name',
      },
    ]);
    expect(category[0].binding.rowId).not.toBe(TRANSACTION_ID);
  });

  it('pairs the group name with the group’s row id, never this row’s', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert
    const group = requests.filter(
      (request) => request.binding.table === 'category_groups',
    );

    expect(group).toEqual([
      {
        binding: { table: 'category_groups', column: 'name', rowId: GROUP_ID },
        wire: 'sealed-group-name',
      },
    ]);
    expect(group[0].binding.rowId).not.toBe(TRANSACTION_ID);
  });

  it('asks for five requests and no more', () => {
    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);

    // Assert — the census beside the five literals above. Each of those names
    // one pairing; together with this they say the five are these five and that
    // no member was asked for twice. Sorted, because the order a batch is
    // handed its requests in is nothing this may pin.
    expect(requests.map((request) => request.binding.table).sort()).toEqual([
      'accounts',
      'categories',
      'category_groups',
      'payees',
      'transactions',
    ]);
  });

  it('asks nothing for an identifier whose wire value is missing', () => {
    // Arrange — a payee this row names, with no sealed name beside it. There is
    // nothing to open, so there is nothing to ask for.
    const noPayeeName: TransactionDto = {
      ...sealedTransaction,
      payeeName: null,
    };

    // Act
    const requests = transactionNarrativeRequests(noPayeeName);

    // Assert
    expect(
      requests.filter((request) => request.binding.table === 'payees'),
    ).toEqual([]);
    expect(requests).toHaveLength(4);
  });

  it('asks nothing for a wire value whose identifier is missing', () => {
    // Arrange — the shape the server does not send, and the one a collector
    // reaching for a substitute would answer with this row's own id. The count
    // is half of what this case holds: a substitute binding cannot decline,
    // because `dto.id` is always there, so the request appears instead of
    // vanishing.
    const orphanedPayeeName: TransactionDto = {
      ...sealedTransaction,
      payeeId: null,
    };

    // Act
    const requests = transactionNarrativeRequests(orphanedPayeeName);

    // Assert
    expect(
      requests.filter((request) => request.binding.table === 'payees'),
    ).toEqual([]);
    expect(requests).toHaveLength(4);
  });

  it('asks for the note and the account name when every optional member is absent', () => {
    // Act
    const requests = transactionNarrativeRequests(noOptionalMembers);

    // Assert — `undefined` is the same absence as `null` here, and the two
    // members that are never optional are what is left.
    expect(requests.map((request) => request.binding.table).sort()).toEqual([
      'accounts',
      'transactions',
    ]);
  });

  it('asks only for the account name when the note is null as well', () => {
    // Act
    const requests = transactionNarrativeRequests(bareTransaction);

    // Assert — one request, spelled out whole: a row naming nothing but its
    // account.
    expect(requests).toEqual([
      {
        binding: { table: 'accounts', column: 'name', rowId: ACCOUNT_ID },
        wire: 'sealed-account-name',
      },
    ]);
  });

  it('asks for exactly what the mapper opens', async () => {
    // Arrange — a drift detector, and only that. **It cannot see a defect that
    // is present in both halves**: a wrong pairing copy-pasted into the mapper
    // and into the collector agrees with itself, and this stays green — which
    // was measured, not assumed. What it does catch is one half moving while
    // the other stands still, which is the other way this pair goes wrong.
    const opener = recordingOpener();

    // Act
    const requests = transactionNarrativeRequests(sealedTransaction);
    await toTransactionView(sealedTransaction, opener.open);

    // Assert
    expect(sortedRequests(requests)).toEqual(sortedRequests(opener.calls));
  });
});
