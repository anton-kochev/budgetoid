// A whole account's export, as the server's text, built with the product's own
// codec.
//
// Every narrative member comes out of `sealNarrativeField` under the binding of
// the row it sits on — the same four fields `export-document.ts` rebuilds when
// it opens them — so a member sealed under the wrong binding would come back
// `unreadable` and the run would throw rather than time a document the product
// cannot open. Nothing is transcribed.
//
// **Unlike the read's fixture, no wire value is shared.** A foreign name on a
// transaction row is a join the read path carries; the export carries each
// entity once, in its own list, and every member is bound to its own row. So
// every non-null member is its own ciphertext and its own open — which is what
// the server's `ExportDocument` holds, not a pessimistic choice.
//
// **The text is written the way .NET writes it,** and that is checked against
// nothing here but the decoder: compact, camelCase, members in record order,
// `decimal` money with its four stored places (`12.5000`, which `JSON.stringify`
// cannot produce from a number), `DateOnly` as a bare date. The decoder is
// strict — an extra member, a missing one or a fifth decimal is `unrecognised`
// — so a fixture that drifted from the wire shape fails the run on its first
// decode rather than measuring something else.
import type { NarrativeFieldBinding } from '../../../src/app/+core/security/narrative-cipher';
import { sealNarrativeField } from '../../../src/app/+core/security/narrative-cipher';
import type { ExportFixtureInfo, ExportShape } from '../shapes.ts';

/** A prepared export: the text the server would have sent, and its counts. */
export interface ExportFixture {
  readonly info: ExportFixtureInfo;
  readonly text: string;
}

/** Seals one account to `shape`'s cardinalities and writes it out as text. */
export async function buildExportFixture(
  contentKey: CryptoKey,
  shape: ExportShape,
): Promise<ExportFixture> {
  const counts = { nullMembers: 0, sealedMembers: 0 };
  const seal = async (
    plaintext: string,
    binding: NarrativeFieldBinding,
  ): Promise<string> => {
    counts.sealedMembers++;

    return await sealNarrativeField(contentKey, plaintext, binding);
  };
  const sealOrNull = async (
    plaintext: string | null,
    binding: NarrativeFieldBinding,
  ): Promise<string | null> => {
    if (plaintext === null) {
      counts.nullMembers++;

      return null;
    }

    return await seal(plaintext, binding);
  };

  const userId = crypto.randomUUID();
  const budgetId = crypto.randomUUID();

  const accounts = [];

  for (let index = 0; index < shape.accounts; index++) {
    const id = crypto.randomUUID();

    accounts.push({
      id,
      budgetId,
      name: await seal(
        `${ACCOUNT_WORDS[index % ACCOUNT_WORDS.length]} ${index + 1}`,
        { table: 'accounts', column: 'name', rowId: id },
      ),
      type: ACCOUNT_TYPES[index % ACCOUNT_TYPES.length],
      openingBalance: money(1250.5 * (index + 1)),
      currencyCode: 'USD',
      createdAtUtc: timestamp(index),
    });
  }

  const categoryGroups = [];

  for (let index = 0; index < shape.groups; index++) {
    const id = crypto.randomUUID();

    categoryGroups.push({
      id,
      budgetId,
      name: await seal(
        `${GROUP_WORDS[index % GROUP_WORDS.length]} ${index + 1}`,
        { table: 'category_groups', column: 'name', rowId: id },
      ),
      description: await sealOrNull(
        index % shape.describedCategoryEvery === 0
          ? `What goes under group ${index + 1}`
          : null,
        { table: 'category_groups', column: 'description', rowId: id },
      ),
      position: index,
      createdAtUtc: timestamp(index),
    });
  }

  const categories = [];

  for (let index = 0; index < shape.categories; index++) {
    const id = crypto.randomUUID();
    const group = at(categoryGroups, index);

    categories.push({
      id,
      budgetId,
      categoryGroupId: group.id,
      name: await seal(
        `${CATEGORY_WORDS[index % CATEGORY_WORDS.length]} ${index + 1}`,
        { table: 'categories', column: 'name', rowId: id },
      ),
      description: await sealOrNull(
        index % shape.describedCategoryEvery === 0
          ? `What counts as category ${index + 1}`
          : null,
        { table: 'categories', column: 'description', rowId: id },
      ),
      position: Math.floor(index / categoryGroups.length),
      createdAtUtc: timestamp(index),
    });
  }

  const payees = [];

  for (let index = 0; index < shape.payees; index++) {
    const id = crypto.randomUUID();

    payees.push({
      id,
      budgetId,
      name: await seal(
        `${PAYEE_WORDS[index % PAYEE_WORDS.length]} ${index + 1}`,
        { table: 'payees', column: 'name', rowId: id },
      ),
      createdAtUtc: timestamp(index),
    });
  }

  const transactions = [];

  for (let index = 0; index < shape.transactions; index++) {
    const id = crypto.randomUUID();

    transactions.push({
      id,
      budgetId,
      accountId: at(accounts, index).id,
      amount: money(-(((index * 7919) % 99_991) / 100) - 0.5),
      date: dateFor(index),
      description: await sealOrNull(
        index % shape.undescribedEvery === shape.undescribedEvery - 1
          ? null
          : describe(index),
        { table: 'transactions', column: 'description', rowId: id },
      ),
      payeeId:
        index % shape.payeelessEvery === shape.payeelessEvery - 1
          ? null
          : at(payees, index).id,
      categoryId:
        index % shape.uncategorisedEvery === shape.uncategorisedEvery - 1
          ? null
          : at(categories, index).id,
      createdAtUtc: timestamp(index),
    });
  }

  // Members in `ExportDocument.cs`'s declaration order: the positional record
  // members first, then the `init` lists, which is the order System.Text.Json
  // writes them in. The decoder does not care about order; a reader diffing
  // this against a real export would.
  const doc = {
    schemaVersion: 1,
    user: {
      id: userId,
      email: 'someone@example.test',
      createdAtUtc: timestamp(0),
    },
    budgets: [
      {
        id: budgetId,
        userId,
        name: await seal('Household', {
          table: 'budgets',
          column: 'name',
          rowId: budgetId,
        }),
        baseCurrencyCode: null,
        createdAtUtc: timestamp(0),
        accounts,
        categoryGroups,
        categories,
        payees,
        transactions,
      },
    ],
  };
  const text = JSON.stringify(doc).replace(MONEY_PATTERN, '$1');

  return {
    info: {
      name: shape.name,
      nullMembers: counts.nullMembers,
      sealedMembers: counts.sealedMembers,
      textChars: text.length,
      transactions: shape.transactions,
    },
    text,
  };
}

// `decimal` is written with every stored place, and `JSON.stringify` drops
// trailing zeros from a number. So money travels as a marked string and the
// quotes come off after serialisation — the trick `export-document.spec.ts`
// uses for the same reason.
const MONEY_MARKER = '@@money:';
const MONEY_PATTERN = /"@@money:([^"]*)"/g;

function money(value: number): string {
  return `${MONEY_MARKER}${value.toFixed(4)}`;
}

function at<T>(rows: readonly T[], index: number): T {
  const row = rows[index % rows.length];

  if (row === undefined) {
    throw new Error('An export cardinality of zero has nothing to reference.');
  }

  return row;
}

// Seven fractional digits and a `Z`, as a .NET `DateTime` of kind UTC writes.
function timestamp(index: number): string {
  const instant = Date.UTC(2021, 0, 1) + index * 3_600_000;

  return `${new Date(instant).toISOString().slice(0, 19)}.${(index % 10_000_000).toString().padStart(7, '0')}Z`;
}

function dateFor(index: number): string {
  return new Date(Date.UTC(2021, 0, 1) + (index % 2000) * 86_400_000)
    .toISOString()
    .slice(0, 10);
}

function describe(index: number): string {
  return `${DESCRIPTION_WORDS[index % DESCRIPTION_WORDS.length]} — receipt ${index + 1}`;
}

const ACCOUNT_TYPES = ['Checking', 'Savings', 'Cash', 'CreditCard'];

const ACCOUNT_WORDS = ['Everyday current', 'Joint savings', 'Travel card'];

const PAYEE_WORDS = [
  'Corner Market',
  'Kesko Neighbourhood',
  'City Transit Authority',
  'Riverside Pharmacy',
  'Nordic Hardware',
];

const CATEGORY_WORDS = [
  'Groceries',
  'Household repairs',
  'Public transport',
  'Health and medicine',
];

const GROUP_WORDS = ['Everyday spending', 'Home', 'Getting around'];

const DESCRIPTION_WORDS = [
  'Weekly shop and a jar of coffee',
  'Replacement filter for the tap',
  'Monthly travel pass top-up',
  'Prescription collected on the way home',
];
