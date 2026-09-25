// A screenful of sealed transactions, built with the product's own codec.
//
// Every wire value here comes out of `sealNarrativeField`, under the binding
// the matching view module builds — so what the measurement opens is what a
// read of the real API would hand the real mapper. Nothing is transcribed.
//
// **A foreign name is sealed once and its wire value is shared by every row
// that references it.** That is not a shortcut; it is the fact the whole
// measurement rests on. AES-GCM draws a fresh nonce per seal, so sealing one
// payee's name per row would produce 200 different ciphertexts for one payee
// and de-duplication would collect nothing — the fixture would quietly measure
// the pathological shape under the typical shape's name. One row, one stored
// ciphertext, which is also what the database holds.
import type { TransactionDto } from '../../../src/app/+core/api/transactions-api.service';
import { accountNameBinding } from '../../../src/app/accounts/account-view';
import { categoryGroupNameBinding } from '../../../src/app/categories/category-group-view';
import { categoryNameBinding } from '../../../src/app/categories/category-view';
import { payeeNameBinding } from '../../../src/app/transactions/payee-view';
import { sealNarrativeField } from '../../../src/app/+core/security/narrative-cipher';
import type { NarrativeFieldBinding } from '../../../src/app/+core/security/narrative-cipher';
import { transactionDescriptionBinding } from '../../../src/app/transactions/transaction-view';
import type { FixtureInfo, FixtureShape } from '../shapes.ts';

/** A prepared page: the rows to map, and what they turned out to contain. */
export interface Fixture {
  readonly info: FixtureInfo;
  readonly rows: readonly TransactionDto[];
}

/**
 * Mints the account content key the whole run seals and opens under.
 *
 * Non-extractable, because `narrative-cipher.ts` refuses an extractable key
 * before it reaches a cipher — measuring under a key the product would refuse
 * would be measuring a path that cannot happen.
 */
export async function createContentKey(): Promise<CryptoKey> {
  return await crypto.subtle.generateKey(
    { length: 256, name: 'AES-GCM' },
    false,
    ['encrypt', 'decrypt'],
  );
}

/** Seals one page of rows to `shape`'s cardinalities. */
export async function buildFixture(
  contentKey: CryptoKey,
  shape: FixtureShape,
): Promise<Fixture> {
  const accounts = await sealEntities(
    contentKey,
    shape.accounts,
    accountNameBinding,
    (index) => `${ACCOUNT_WORDS[index % ACCOUNT_WORDS.length]} ${index + 1}`,
  );
  const payees = await sealEntities(
    contentKey,
    shape.payees,
    payeeNameBinding,
    (index) => `${PAYEE_WORDS[index % PAYEE_WORDS.length]} ${index + 1}`,
  );
  const categories = await sealEntities(
    contentKey,
    shape.categories,
    categoryNameBinding,
    (index) => `${CATEGORY_WORDS[index % CATEGORY_WORDS.length]} ${index + 1}`,
  );
  const groups = await sealEntities(
    contentKey,
    shape.groups,
    categoryGroupNameBinding,
    (index) => `${GROUP_WORDS[index % GROUP_WORDS.length]} ${index + 1}`,
  );

  const rows: TransactionDto[] = [];

  for (let index = 0; index < shape.rows; index++) {
    const id = crypto.randomUUID();
    const account = pick(accounts, index);
    const payee = pick(payees, index);
    const category = pick(categories, index);
    const group = pick(groups, index);

    rows.push({
      accountId: account.rowId,
      accountName: account.wire,
      amount: -((index % 97) + 1) - 0.25,
      categoryGroupId: group.rowId,
      categoryGroupName: group.wire,
      categoryId: category.rowId,
      categoryName: category.wire,
      createdAtUtc: '2026-01-15T09:30:00.000Z',
      currencyCode: 'USD',
      currencySymbol: '$',
      date: dateFor(index),
      description: await sealNarrativeField(
        contentKey,
        describe(index),
        transactionDescriptionBinding(id),
      ),
      id,
      payeeId: payee.rowId,
      payeeName: payee.wire,
    });
  }

  return {
    info: {
      distinctOpens: countDistinct(rows),
      name: shape.name,
      rows: rows.length,
      totalOpens: rows.length * SEALED_COLUMNS_PER_ROW,
    },
    rows,
  };
}

/** Five sealed columns per transaction: its note and four foreign names. */
const SEALED_COLUMNS_PER_ROW = 5;

interface SealedEntity {
  readonly rowId: string;
  readonly wire: string;
}

async function sealEntities(
  contentKey: CryptoKey,
  count: number,
  binding: (rowId: string) => NarrativeFieldBinding,
  name: (index: number) => string,
): Promise<readonly SealedEntity[]> {
  const entities: SealedEntity[] = [];

  for (let index = 0; index < count; index++) {
    const rowId = crypto.randomUUID();

    entities.push({
      rowId,
      wire: await sealNarrativeField(contentKey, name(index), binding(rowId)),
    });
  }

  return entities;
}

function pick(entities: readonly SealedEntity[], index: number): SealedEntity {
  const entity = entities[index % entities.length];

  if (entity === undefined) {
    throw new Error('A fixture cardinality of zero has nothing to reference.');
  }

  return entity;
}

// Reported, never used by the code under test. It is the same four fields
// `narrative-batch.ts` keys on, spelled here only so the table can say how many
// opens the shape leaves after de-duplication.
function countDistinct(rows: readonly TransactionDto[]): number {
  const seen = new Set<string>();

  for (const row of rows) {
    seen.add(`transactions|description|${row.id}|${row.description ?? ''}`);
    seen.add(`accounts|name|${row.accountId}|${row.accountName}`);
    seen.add(`payees|name|${row.payeeId ?? ''}|${row.payeeName ?? ''}`);
    seen.add(
      `categories|name|${row.categoryId ?? ''}|${row.categoryName ?? ''}`,
    );
    seen.add(
      `category_groups|name|${row.categoryGroupId ?? ''}|${row.categoryGroupName ?? ''}`,
    );
  }

  return seen.size;
}

function dateFor(index: number): string {
  const day = (index % 28) + 1;

  return `2026-01-${day.toString().padStart(2, '0')}`;
}

function describe(index: number): string {
  return `${DESCRIPTION_WORDS[index % DESCRIPTION_WORDS.length]} — receipt ${index + 1}`;
}

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
