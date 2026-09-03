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
import {
  NarrativeFieldMisuseError,
  type NarrativeFieldBinding,
} from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import { toTransactionView } from './transaction-view';

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
