// The pure half of `same-name`: finding two records in one list whose names a
// rotation would re-seal onto one incoming blind index, and deciding which of
// the two a person's new name goes to.
//
// **Every index here is the incoming one**, computed by the driver for the pass
// it is about to send. Two rows can only share one when a tab holding the
// outgoing keys gave a row a name already stored under the incoming keys
// elsewhere, so the pair these cases model is one row under the outgoing
// generation (`'current'`) and one under the incoming (`'next'`). A `'next'`
// row is usually one a chunk re-sealed, but not always one the run visited: a
// rename made during the run writes under the incoming keys and clears the
// row's stamp. The server's unique index sees the two stored values as
// different, which is why nothing refused the write. A pair sharing one
// generation has a fallback in the module and no case here.
//
// **The row opened under the outgoing generation is the one renamed**, because
// its name is the one that arrived second — see "Renaming one of two records
// with one name" in docs/design/components.md. The cases put the pair in both
// list orders, so an implementation that picks by position fails one of them.
//
// **The comparison is within a list and never across lists.** The four name
// indexes are per table, so an account and a payee called the same thing are
// two different questions and neither is a collision.
//
// **The typed-name check reaches every record in the list**, including rows the
// run has not visited yet, because the server cannot compare an incoming index
// against a row still under the outgoing key.
//
// The strings standing in for blind indexes are opaque on purpose: this module
// compares values and computes none, so real HMAC output would add nothing but
// the crypto the module must not import.
import { describe, expect, it } from 'vitest';

import {
  findNameCollision,
  holderOf,
  type NameArm,
  type NameArms,
  type NamedRowIndex,
} from './rotation-name-collision';

const INDEX_CASH = 'aW5kZXgtY2FzaA';
const INDEX_GROCERIES = 'aW5kZXgtZ3JvY2VyaWVz';
const INDEX_RENT = 'aW5kZXgtcmVudA';
const INDEX_SAVINGS = 'aW5kZXgtc2F2aW5ncw';

function row(
  id: string,
  nameKey: string,
  openedUnder: NamedRowIndex['openedUnder'],
): NamedRowIndex {
  return { id, nameKey, openedUnder };
}

// Four arms with unique indexes in each, and the same values repeated across
// arms, so a case that plants a pair in one arm changes nothing else.
function uniqueArms(): NameArms {
  return {
    accounts: [
      row('acc-1', INDEX_CASH, 'next'),
      row('acc-2', INDEX_SAVINGS, 'current'),
    ],
    payees: [
      row('pay-1', INDEX_CASH, 'current'),
      row('pay-2', INDEX_GROCERIES, 'next'),
    ],
    categoryGroups: [
      row('grp-1', INDEX_RENT, 'next'),
      row('grp-2', INDEX_GROCERIES, 'current'),
    ],
    categories: [
      row('cat-1', INDEX_RENT, 'current'),
      row('cat-2', INDEX_SAVINGS, 'next'),
    ],
  };
}

const ARMS: readonly NameArm[] = [
  'accounts',
  'payees',
  'categoryGroups',
  'categories',
];

describe('findNameCollision', () => {
  it.each(ARMS)('finds two rows of %s that share one incoming index', (arm) => {
    // Arrange
    const arms: NameArms = {
      ...uniqueArms(),
      [arm]: [
        row('visited', INDEX_GROCERIES, 'next'),
        row('bystander', INDEX_RENT, 'next'),
        row('late', INDEX_GROCERIES, 'current'),
      ],
    };

    // Act
    const collision = findNameCollision(arms);

    // Assert
    expect(collision).not.toBeNull();
    expect(collision?.arm).toBe(arm);
    expect([collision?.renamed.id, collision?.kept.id].sort()).toEqual([
      'late',
      'visited',
    ]);
  });

  it('answers null when every index in every arm is unique', () => {
    // Arrange
    const arms = uniqueArms();

    // Act
    const collision = findNameCollision(arms);

    // Assert
    expect(collision).toBeNull();
  });

  it('does not compare an account with a payee that carries the same index', () => {
    // Arrange
    const arms: NameArms = {
      accounts: [row('acc-cash', INDEX_CASH, 'next')],
      payees: [row('pay-cash', INDEX_CASH, 'current')],
      categoryGroups: [],
      categories: [],
    };

    // Act
    const collision = findNameCollision(arms);

    // Assert
    expect(collision).toBeNull();
  });

  it('renames the row opened under the outgoing generation when it is listed second', () => {
    // Arrange
    const arms: NameArms = {
      ...uniqueArms(),
      payees: [
        row('visited', INDEX_GROCERIES, 'next'),
        row('late', INDEX_GROCERIES, 'current'),
      ],
    };

    // Act
    const collision = findNameCollision(arms);

    // Assert
    expect(collision?.renamed.id).toBe('late');
    expect(collision?.kept.id).toBe('visited');
  });

  // The id here sorts after its partner where the case above sorts before, so
  // an implementation that picks by id order fails one of the two as surely as
  // one that picks by position.
  it('renames the row opened under the outgoing generation when it is listed first', () => {
    // Arrange
    const arms: NameArms = {
      ...uniqueArms(),
      payees: [
        row('written-late', INDEX_GROCERIES, 'current'),
        row('visited', INDEX_GROCERIES, 'next'),
      ],
    };

    // Act
    const collision = findNameCollision(arms);

    // Assert
    expect(collision?.renamed.id).toBe('written-late');
    expect(collision?.kept.id).toBe('visited');
  });
});

describe('holderOf', () => {
  it('finds a third row holding the typed index, not only the two of the pair', () => {
    // Arrange
    const payees: readonly NamedRowIndex[] = [
      row('visited', INDEX_GROCERIES, 'next'),
      row('late', INDEX_GROCERIES, 'current'),
      row('unvisited', INDEX_RENT, 'current'),
    ];

    // Act
    const holder = holderOf(payees, INDEX_RENT, 'late');

    // Assert
    expect(holder?.id).toBe('unvisited');
  });

  it('does not count the row being renamed as holding its own name', () => {
    // Arrange
    const payees: readonly NamedRowIndex[] = [
      row('late', INDEX_CASH, 'current'),
      row('visited', INDEX_GROCERIES, 'next'),
    ];

    // Act
    const holder = holderOf(payees, INDEX_CASH, 'late');

    // Assert
    expect(holder).toBeNull();
  });

  it('answers null when no row in the list holds the typed index', () => {
    // Arrange
    const payees: readonly NamedRowIndex[] = [
      row('visited', INDEX_GROCERIES, 'next'),
      row('late', INDEX_GROCERIES, 'current'),
    ];

    // Act
    const holder = holderOf(payees, INDEX_SAVINGS, 'late');

    // Assert
    expect(holder).toBeNull();
  });
});
