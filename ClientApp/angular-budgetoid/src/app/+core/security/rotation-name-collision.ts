// Two records in one list whose names a rotation would re-seal onto one
// incoming blind index, and which of the two a person's new name goes to.
//
// **Pure, and it holds no name.** A row here is its identifier, the incoming
// index over its name and the generation it opened under. The plaintext stays
// with the pass that opened it, so nothing this module is handed outlives that
// pass's own read; the driver computes every index and this module only
// compares them.
//
// **Every index is the incoming one.** Two rows can only share one when a tab
// still holding the outgoing keys gave a row a name another row already holds
// under the incoming keys: the two stored indexes were taken under different
// keys, so the server's unique index saw two values and refused nothing. A pair
// is therefore one row still under the outgoing generation and one already
// under the incoming — re-sealed by a chunk of this run, or written under the
// incoming keys by a rename made during it, which no chunk has visited. See
// "Renaming one of two records with one name" in docs/design/components.md.

/** The four lists whose name carries a blind index, as the chunk route spells them. */
export type NameArm = 'accounts' | 'payees' | 'categoryGroups' | 'categories';

/** One row's name as a rotation pass sees it: never the name itself. */
export interface NamedRowIndex {
  readonly id: string;
  /** The blind index over the row's name, under the incoming index key. */
  readonly nameKey: string;
  /**
   * The generation the row's stored name opened under: `'current'` is the one
   * the run is replacing, `'next'` the one it is re-sealing onto.
   */
  readonly openedUnder: 'current' | 'next';
}

/** Every indexed list of one pass, one entry per arm. */
export type NameArms = Readonly<Record<NameArm, readonly NamedRowIndex[]>>;

interface NameCollision {
  readonly arm: NameArm;
  /** The row the person's new name goes to. */
  readonly renamed: NamedRowIndex;
  /** The row that keeps the name it had first. */
  readonly kept: NamedRowIndex;
}

// Walked in the chunk route's order, so the pair a person is shown is the one
// the next send would have reached first.
const ARMS: readonly NameArm[] = [
  'accounts',
  'payees',
  'categoryGroups',
  'categories',
];

/**
 * The first two rows of one list sharing an incoming index, or `null` when
 * every index is unique within its own list.
 *
 * **Within a list and never across lists**: the four unique indexes are per
 * table, so an account and a payee with one name are not a collision.
 */
export function findNameCollision(arms: NameArms): NameCollision | null {
  for (const arm of ARMS) {
    const firstHolder = new Map<string, NamedRowIndex>();

    for (const row of arms[arm]) {
      const earlier = firstHolder.get(row.nameKey);

      if (earlier === undefined) {
        firstHolder.set(row.nameKey, row);
        continue;
      }

      return { arm, ...renamedAndKept(earlier, row) };
    }
  }

  return null;
}

/**
 * The row in `rows` other than `renamedId` whose incoming index is `nameKey`,
 * or `null` when none holds it.
 *
 * **Every row in the list, visited or not**, because the server cannot compare
 * an incoming index against a row still stored under the outgoing key. The row
 * being renamed is skipped: keeping its own name is not taking somebody else's.
 */
export function holderOf(
  rows: readonly NamedRowIndex[],
  nameKey: string,
  renamedId: string,
): NamedRowIndex | null {
  return (
    rows.find((row) => row.id !== renamedId && row.nameKey === nameKey) ?? null
  );
}

// **The row opened under the outgoing generation is renamed**, because its name
// is the one that arrived second — the other was already under the incoming
// keys, re-sealed or renamed by this run, when that name was written. Never by list position or by identifier: the two
// records look the same on every screen, so the generation is the only fact
// that says which name came first.
//
// A pair sharing a generation cannot reach here — under one key an equal index
// is one the server's unique index already refused — and if one ever did, no
// fact says which name came first, so the later-listed row is renamed.
function renamedAndKept(
  earlier: NamedRowIndex,
  later: NamedRowIndex,
): Pick<NameCollision, 'renamed' | 'kept'> {
  return earlier.openedUnder === 'current' && later.openedUnder === 'next'
    ? { renamed: earlier, kept: later }
    : { renamed: later, kept: earlier };
}
