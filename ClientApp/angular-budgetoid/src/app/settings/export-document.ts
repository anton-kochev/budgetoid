// The export document as the client reads it, opens it and writes it out: the
// server's text in, a file a person can read without a key out.
//
// Three functions, and nothing kept between them. There is no module-level
// binding here holding a document, an opened value or an opener: opened text
// exists to be written to the file, and a copy left behind in a module would
// outlive both the screen that asked for it and the key that opened it.
//
// **The decoder is strict because the server moving is the case it is for.**
// Every object must carry exactly the members `ExportDocument.cs` declares —
// none missing, none extra — with the JSON type each one serializes as, and
// `null` only where that record declares a nullable member. The extra-member
// refusal is the loud direction, and it is the FR-015 guard: a column the server
// starts shipping is, until somebody looks at it, a column nobody decided
// whether to open. Passed through, a new sealed column would land on disk as
// ciphertext under a file that claims to be readable, and a new blind index
// would land as the per-account fingerprint the server's records argue out.
// Refused, the export stops working on the day the shape changes and says so.
//
// **Money is read with plain `JSON.parse`, and that is exact for this column.**
// `numeric(14,4)` holds at most fourteen significant digits and a double
// round-trips fifteen, so every stored value comes back out of
// `JSON.stringify` as the same decimal — measured over 3.24M values, the edges
// of the range included, with no mismatch. What it does lose is trailing zeros
// (`12.5000` is written `12.5`), which changes no value and is the accepted
// cost. That argument holds only while the column is fourteen digits wide, so
// the decoder is a tripwire on both of the column's dimensions: a magnitude at
// or past 1e10 is refused, and so is a value finer than four decimals. A column
// widened either way would otherwise start losing digits in silence, in a file
// whose whole purpose is to be a faithful copy; and a fifth decimal is a body
// `numeric(14,4)` cannot have produced, which the file would carry as though
// the ledger held it.
//
// **A narrative member is refused here when it cannot be an envelope at all.**
// A wire string the strict decoder refuses, or one that decodes to fewer bytes
// than the envelope floor, is a body this client could not read: the refusal
// comes before any cipher and observes no key material, so it is
// `unrecognised`. From the version byte and the tag onward the question is the
// opener's, and a failure there is `unreadable` — the keys were here and the
// bytes were not theirs, which is a different next step for a person.
import { decodeBase64Url } from '@app-core/security/base64url';
import { MINIMUM_ENVELOPE_BYTES } from '@app-core/security/key-envelope';
import { openNarrativeBatch } from '@app-core/security/narrative-batch';
import type {
  FrameBudget,
  NarrativeRequest,
} from '@app-core/security/narrative-batch';
import {
  NarrativeFieldMisuseError,
  refuseInvalidBinding,
} from '@app-core/security/narrative-cipher';
import type { NarrativeFieldBinding } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';

/** The account itself, as `ExportedUser` serializes it. */
export interface ExportedUser {
  readonly id: string;
  readonly email: string;
  readonly createdAtUtc: string;
}

/**
 * The document with every narrative member typed `N` — the wire value while
 * sealed, the text once opened. One shape for both, so the two cannot drift.
 */
export interface ExportDocumentOf<N> {
  readonly schemaVersion: 1;
  readonly user: ExportedUser;
  readonly budgets: readonly ExportedBudgetOf<N>[];
}

export interface ExportedBudgetOf<N> {
  readonly id: string;
  readonly userId: string;
  readonly name: N | null;
  readonly baseCurrencyCode: string | null;
  readonly createdAtUtc: string;
  readonly accounts: readonly ExportedAccountOf<N>[];
  readonly categoryGroups: readonly ExportedCategoryGroupOf<N>[];
  readonly categories: readonly ExportedCategoryOf<N>[];
  readonly payees: readonly ExportedPayeeOf<N>[];
  readonly transactions: readonly ExportedTransactionOf<N>[];
}

export interface ExportedAccountOf<N> {
  readonly id: string;
  readonly budgetId: string;
  readonly name: N;
  readonly type: string;
  readonly openingBalance: number;
  readonly currencyCode: string;
  readonly createdAtUtc: string;
}

export interface ExportedCategoryGroupOf<N> {
  readonly id: string;
  readonly budgetId: string;
  readonly name: N;
  readonly description: N | null;
  readonly position: number;
  readonly createdAtUtc: string;
}

export interface ExportedCategoryOf<N> {
  readonly id: string;
  readonly budgetId: string;
  readonly categoryGroupId: string;
  readonly name: N;
  readonly description: N | null;
  readonly position: number;
  readonly createdAtUtc: string;
}

export interface ExportedPayeeOf<N> {
  readonly id: string;
  readonly budgetId: string;
  readonly name: N;
  readonly createdAtUtc: string;
}

export interface ExportedTransactionOf<N> {
  readonly id: string;
  readonly budgetId: string;
  readonly accountId: string;
  readonly amount: number;
  readonly date: string;
  readonly description: N | null;
  readonly payeeId: string | null;
  readonly categoryId: string | null;
  readonly createdAtUtc: string;
}

// Type-only marks: neither exists at runtime. They make the decoder the one way
// to hold a sealed document and `openExportDocument` the one way to hold an
// opened one — so a sealed document cannot be handed to the serializer and
// saved as though it were the readable copy.
declare const sealedMark: unique symbol;
declare const openedMark: unique symbol;

/** A document the decoder accepted: every narrative member still a wire value. */
export type SealedExportDocument = ExportDocumentOf<string> & {
  readonly [sealedMark]: true;
};

/** A document with every narrative member opened, and nothing else changed. */
export type OpenedExportDocument = ExportDocumentOf<string> & {
  readonly [openedMark]: true;
};

export type ExportDocumentDecoding =
  | { readonly kind: 'decoded'; readonly doc: SealedExportDocument }
  | { readonly kind: 'unrecognised' };

export type ExportDocumentOpening =
  | { readonly kind: 'opened'; readonly doc: OpenedExportDocument }
  | { readonly kind: 'locked' }
  | { readonly kind: 'unreadable' };

/**
 * Reads the server's export text, or refuses it as `unrecognised`.
 *
 * Refused: text that is not JSON, a `schemaVersion` other than the number `1`,
 * any object whose member set is not exactly the declared one, a member of the
 * wrong JSON type or `null` where none is declared, money at or past 1e10 or
 * finer than four decimals, a narrative member whose row id is not the
 * canonical spelling, and a narrative member whose wire string the strict
 * base64url decoder refuses or which decodes to fewer bytes than an envelope's
 * floor. The row id is refused here rather than left for the opener, where the
 * same id would be a `NarrativeFieldMisuseError` — a defect in this client —
 * when what it actually is, is a body this client cannot read. The wire string
 * is refused here for the reason at the head of the file.
 */
export function decodeExportDocument(text: string): ExportDocumentDecoding {
  let parsed: unknown;

  try {
    parsed = JSON.parse(text);
  } catch {
    return { kind: 'unrecognised' };
  }

  if (!isDocumentShape(parsed)) {
    return { kind: 'unrecognised' };
  }

  try {
    const doc = mapNarrative(parsed, (binding, wire) => {
      refuseInvalidBinding(binding);
      refuseNonEnvelopeWire(wire);
      return wire;
    });

    return { kind: 'decoded', doc: doc as SealedExportDocument };
  } catch (error: unknown) {
    if (
      error instanceof NarrativeFieldMisuseError ||
      error instanceof NotAnEnvelopeError
    ) {
      return { kind: 'unrecognised' };
    }
    throw error;
  }
}

// A wire string that cannot be an envelope, told apart from every other throw
// by type. Module-private: it never leaves `decodeExportDocument`, which turns
// it into `unrecognised`.
class NotAnEnvelopeError extends Error {
  public override readonly name = 'NotAnEnvelopeError';
}

// The repository's one strict decoder and the envelope's own floor — never a
// second decoder here. `decodeBase64Url` throws a bare `Error`, so the `catch`
// wraps that one call and nothing else: whatever it throws is its refusal, and
// no message is read to decide so.
function refuseNonEnvelopeWire(wire: string): void {
  let bytes: Uint8Array;

  try {
    bytes = decodeBase64Url(wire);
  } catch (cause: unknown) {
    throw new NotAnEnvelopeError(
      'A narrative member arrives as unpadded base64url, and this value is not.',
      { cause },
    );
  }

  if (bytes.length < MINIMUM_ENVELOPE_BYTES) {
    throw new NotAnEnvelopeError(
      'A narrative member is too short to hold a version, a nonce and a tag.',
    );
  }
}

/**
 * Opens every narrative member of `doc` through `open`, in one batch.
 *
 * **Complete or nothing.** A document with one member missing is not the copy
 * the person asked for, and a file holding it would say nothing about the gap,
 * so any member that does not come back as text means no document at all.
 *
 * **`locked` wins over `unreadable`,** whichever member was asked first.
 * `locked` has a way forward — present a factor and every member comes back —
 * so it is the word worth showing while it is true; `unreadable` is only worth
 * reporting once the key is there and a value still did not open.
 *
 * `null` members stay `null` and never reach `open`: "no description" is a fact
 * about the row, known before any key is involved. Each member is bound to its
 * own row's id — `budgets.name` to the budget's — so a value moved to another
 * row, column or table comes back `unreadable` rather than as somebody else's
 * text. A rejection from `open` propagates: it is a defect in the call, not a
 * value that failed to open.
 */
export async function openExportDocument(
  doc: SealedExportDocument,
  open: NarrativeOpener,
  budget?: FrameBudget,
): Promise<ExportDocumentOpening> {
  // Each narrative member is replaced by its index in `requests`, so the second
  // walk below puts every answer back where its request came from without
  // restating a single binding.
  const requests: NarrativeRequest[] = [];
  const indexed = mapNarrative(
    doc,
    (binding, wire) => requests.push({ binding, wire }) - 1,
  );

  const read = await openNarrativeBatch(requests, open, budget);
  const texts = await Promise.all(
    requests.map(({ binding, wire }) => read(binding, wire)),
  );

  if (texts.some((text) => text.state === 'locked')) {
    return { kind: 'locked' };
  }

  if (!texts.every(isText)) {
    return { kind: 'unreadable' };
  }

  const opened = mapNarrative(indexed, (binding, index) => texts[index].value);

  return { kind: 'opened', doc: opened as OpenedExportDocument };
}

/** The opened document as the text of the file a person keeps. */
export function serializeExportDocument(doc: OpenedExportDocument): string {
  return JSON.stringify(doc, null, 2);
}

function isText(
  text: NarrativeText,
): text is Extract<NarrativeText, { state: 'text' }> {
  return text.state === 'text';
}

// The one place that says which members are narrative and what each is bound
// to. Every member is replaced through `f` with the binding of the row it sits
// on, `null` is passed through without a call, and the spread keeps every other
// member — and the server's member order — exactly as it was.
function mapNarrative<A, B>(
  doc: ExportDocumentOf<A>,
  f: (binding: NarrativeFieldBinding, value: A) => B,
): ExportDocumentOf<B> {
  const orNull = (binding: NarrativeFieldBinding, value: A | null): B | null =>
    value === null ? null : f(binding, value);

  return {
    ...doc,
    budgets: doc.budgets.map((budget) => ({
      ...budget,
      name: orNull(
        { table: 'budgets', column: 'name', rowId: budget.id },
        budget.name,
      ),
      accounts: budget.accounts.map((row) => ({
        ...row,
        name: f({ table: 'accounts', column: 'name', rowId: row.id }, row.name),
      })),
      categoryGroups: budget.categoryGroups.map((row) => ({
        ...row,
        name: f(
          { table: 'category_groups', column: 'name', rowId: row.id },
          row.name,
        ),
        description: orNull(
          { table: 'category_groups', column: 'description', rowId: row.id },
          row.description,
        ),
      })),
      categories: budget.categories.map((row) => ({
        ...row,
        name: f(
          { table: 'categories', column: 'name', rowId: row.id },
          row.name,
        ),
        description: orNull(
          { table: 'categories', column: 'description', rowId: row.id },
          row.description,
        ),
      })),
      payees: budget.payees.map((row) => ({
        ...row,
        name: f({ table: 'payees', column: 'name', rowId: row.id }, row.name),
      })),
      transactions: budget.transactions.map((row) => ({
        ...row,
        description: orNull(
          { table: 'transactions', column: 'description', rowId: row.id },
          row.description,
        ),
      })),
    })),
  };
}

// Shape checks. Each member table is written against its interface with
// `satisfies`, so a member added to one and not the other fails to compile.
type Check = (value: unknown) => boolean;

// The first magnitude `numeric(14,4)` cannot hold; see the head of the file.
const MONEY_CEILING = 1e10;

// Ten-thousandths: the column's four decimals.
const MONEY_SCALE = 1e4;

const isString: Check = (value) => typeof value === 'string';
const isInteger: Check = (value) => Number.isSafeInteger(value);

// Below the ceiling, `round(x · 10⁴)` is an integer under 2^53 and so exact,
// and dividing it by 10⁴ is correctly rounded: the result is the double
// nearest the decimal with at most four places, which is `x` exactly when `x`
// was parsed from such a decimal. Measured over 3.24M `numeric(14,4)` values —
// random across the range, every step in [-2, 2], ±50k steps around ±1e9, the
// top 100k below either edge, every power of ten — with 0 refused, where
// `Number.isInteger(x · 10⁴)` refuses 386,368 of the same set. Over 1M
// five-to-eight-decimal values it accepted 4,658, none of them five-decimal,
// and every one parsed to the very same double as its four-decimal neighbour:
// a sixth or later decimal can be finer than a double resolves near the top of
// the range, so no check on the parsed number can see it, and it reads as
// that neighbour.
const hasMoneyScale = (value: number): boolean =>
  Math.round(value * MONEY_SCALE) / MONEY_SCALE === value;

const isMoney: Check = (value) =>
  typeof value === 'number' &&
  Math.abs(value) < MONEY_CEILING &&
  hasMoneyScale(value);

function orNullCheck(check: Check): Check {
  return (value) => value === null || check(value);
}

function isArrayOf(check: Check): Check {
  return (value) => Array.isArray(value) && value.every(check);
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// Exactly these members, own and no others. A `__proto__` member in the text
// is an own property after `JSON.parse`, so it is counted and refused like any
// other stranger.
function isObjectOf(members: Readonly<Record<string, Check>>): Check {
  const declared = Object.entries(members);

  return (value) =>
    isRecord(value) &&
    Object.keys(value).length === declared.length &&
    declared.every(
      ([member, check]) => Object.hasOwn(value, member) && check(value[member]),
    );
}

const isUser = isObjectOf({
  id: isString,
  email: isString,
  createdAtUtc: isString,
} satisfies Record<keyof ExportedUser, Check>);

const isAccount = isObjectOf({
  id: isString,
  budgetId: isString,
  name: isString,
  type: isString,
  openingBalance: isMoney,
  currencyCode: isString,
  createdAtUtc: isString,
} satisfies Record<keyof ExportedAccountOf<string>, Check>);

const isCategoryGroup = isObjectOf({
  id: isString,
  budgetId: isString,
  name: isString,
  description: orNullCheck(isString),
  position: isInteger,
  createdAtUtc: isString,
} satisfies Record<keyof ExportedCategoryGroupOf<string>, Check>);

const isCategory = isObjectOf({
  id: isString,
  budgetId: isString,
  categoryGroupId: isString,
  name: isString,
  description: orNullCheck(isString),
  position: isInteger,
  createdAtUtc: isString,
} satisfies Record<keyof ExportedCategoryOf<string>, Check>);

const isPayee = isObjectOf({
  id: isString,
  budgetId: isString,
  name: isString,
  createdAtUtc: isString,
} satisfies Record<keyof ExportedPayeeOf<string>, Check>);

const isTransaction = isObjectOf({
  id: isString,
  budgetId: isString,
  accountId: isString,
  amount: isMoney,
  date: isString,
  description: orNullCheck(isString),
  payeeId: orNullCheck(isString),
  categoryId: orNullCheck(isString),
  createdAtUtc: isString,
} satisfies Record<keyof ExportedTransactionOf<string>, Check>);

const isBudget = isObjectOf({
  id: isString,
  userId: isString,
  name: orNullCheck(isString),
  baseCurrencyCode: orNullCheck(isString),
  createdAtUtc: isString,
  accounts: isArrayOf(isAccount),
  categoryGroups: isArrayOf(isCategoryGroup),
  categories: isArrayOf(isCategory),
  payees: isArrayOf(isPayee),
  transactions: isArrayOf(isTransaction),
} satisfies Record<keyof ExportedBudgetOf<string>, Check>);

const isDocument = isObjectOf({
  schemaVersion: (value) => value === 1,
  user: isUser,
  budgets: isArrayOf(isBudget),
} satisfies Record<keyof ExportDocumentOf<string>, Check>);

// The one point where a checked value is trusted as the type: everything above
// has walked it member by member against the tables that mirror that type.
function isDocumentShape(value: unknown): value is ExportDocumentOf<string> {
  return isDocument(value);
}
