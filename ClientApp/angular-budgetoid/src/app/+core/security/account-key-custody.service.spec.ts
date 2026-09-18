// Where the account's two keys live once a factor has opened them, and the one
// class in this client that holds them past the ceremony that produced them.
//
// **Every case in this file pins code that already works.** Nothing here drove
// an implementation into existence; each one stands over a decision that is one
// tidying edit away from being undone, and every one of them was checked by
// making that edit and watching this file go red. The mutations are recorded in
// the report that accompanied them rather than here, because a list of edits in
// a comment goes stale the first time the module is refactored and nothing goes
// red about it.
//
// **No key can be observed, and that is the design rather than a limitation of
// the test.** No public member returns a key and none ever will:
// non-extractability stops the *bytes* leaving and does nothing about a caller
// holding the key object and decrypting a whole budget into a log line.
//
// **What a caller may see divides by kind and not by count**, which is what
// keeps this paragraph from going stale as the class grows. About custody's
// *state* it is entitled to `status` and `unlockFailure`, and to nothing else —
// anything further there is the accessor this file exists to refuse. About an
// operation it asked for it is entitled to that operation's own result, which
// says what became of the value it handed in and carries no key either; the
// operations are the reason no caller has occasion to ask for a key at all, and
// the class is expected to grow more of them. So "the right entry opened" is
// read here as "the service reports `unlocked`", and the arrangements are built
// so that a wrong implementation cannot reach that word.
import { HttpErrorResponse } from '@angular/common/http';
import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  AccountKeyResponseError,
  MeApiService,
  type AccountKeyCustodyDto,
  type AccountKeyEntry,
  type MeDto,
} from '@app-core/api/me-api.service';
import { SessionService } from '@app-core/session/session.service';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ACCOUNT_KEY_BYTES,
  generateAccountKeys,
  importAesGcmKey,
  importHmacSha256Key,
  type AccountKeys,
} from './account-keys';
import { AccountKeyCustodyService } from './account-key-custody.service';
// The one definition of the `0x1F` rule in this client, used here to assemble
// two messages by hand — a manifest plaintext and the associated data it is
// sealed against. Borrowed rather than restated for the reason `factor-manifest.
// ts` borrows it: a private join here would be a second spelling of the rule,
// and a message this file assembled differently would fail a tag rather than
// reach the refusal a case is about.
import { joinFields } from './associated-data';
// The wire form both halves of a manifest cross in. Needed here because the
// cases below hand the service values `sealFactorManifest` would never emit, so
// the encode cannot come from that module's own writer.
import { encodeBase64Url } from './base64url';
// The write half of the factor keypair, used here as a fixture builder and
// never stubbed: an entry that production's own mint produced is the only
// fixture that can prove production's own open reads it.
//
// The label and the version byte travel with it for the hand-sealed cases
// below. They are **this** module's constants — the ones `factor-manifest.ts`
// imports to build its own associated data — so what this file assembles is a
// second *assembly* of that message and never a second copy of either value.
import {
  FACTOR_KEYPAIR_LABEL,
  FACTOR_KEYPAIR_VERSION,
  mintFactorKeypair,
} from './factor-keypair';
// The write half of the manifest, for the gate that confirms a content key is
// the content key. Sealed here under the very key the fixtures encapsulate, so
// a case that reverses the pair has something real to fail against.
//
// **It stays the only symbol this file takes from that module, deliberately.**
// The refusals a manifest can make are exercised below through the *service*,
// by arranging a body and reading the word it publishes — never by naming a
// type or a constant of the module that throws. A spec that reached for one
// would be asserting how a split is spelled instead of which failure it
// reports, and an import that failed to resolve takes the whole suite down
// rather than one case with it: the unit-test builder type-checks the project
// before a single case runs.
import { sealFactorManifest } from './factor-manifest';
// The envelope layout, for the two hand-built values below: the version byte a
// reader accepts, and the floor a sealed value cannot go under.
//
// **The manifest's own floor is this one to the byte** — `factor-manifest.ts`
// says so where it declares it — so the cases that stand on either side of it
// take the number from the module that owns the arithmetic rather than from the
// module that restates it.
import {
  ENVELOPE_VERSION,
  MINIMUM_ENVELOPE_BYTES,
  sealEnvelope,
} from './key-envelope';
// The four indexed pairs as a **value**, which the service may not hold and this
// file must. Every blind-index case below is driven from this array rather than
// from a pair typed out here: a suite built from entry zero is passed by an
// implementation that only ever indexes `payees.name`, and the other three are
// refused with nothing going red.
//
// `computeBlindIndex` travels with them for one case: the seam pin at the foot
// of this file asks the codec for the same value under the same key, which is
// what separates *the service delegated* from *the service answered forty-three
// stable characters of its own*.
import {
  BLIND_INDEXED_FIELDS,
  type BlindIndexBinding,
  computeBlindIndex,
  type BlindIndexedField,
} from './blind-index';
// The codec's own predicate, under the alias `narrative-cipher.ts` gives it.
// Imported rather than restated, for the reason that module states at its own
// refusal: a second regular expression here would be a second definition of one
// spelling, and this file would be carrying the very copy the case below exists
// to hunt for in the service.
import { isCanonicalFactorId as isCanonicalRowId } from './factor-id';
import {
  NARRATIVE_FIELDS,
  openNarrativeField,
  sealNarrativeField,
  type NarrativeFieldBinding,
} from './narrative-cipher';
// The device's memory of how far an account has already moved, driven through
// its own two exports rather than through the store underneath them.
//
// **Arranged and read through the module, never through a key spelled here.**
// One key per account is that module's decision and `rotation-epoch-record.
// spec.ts` is where the spelling is pinned; a second copy in this file would
// agree with any key custody invented, including a single shared one — which is
// the defect the case about two accounts exists to catch.
import {
  highestRotationEpochSeen,
  recordRotationEpochSeen,
} from './rotation-epoch-record';
// The whole module as an object, for one rule and one only: which of the
// codec's exports are *data* rather than operations is a question about the
// codec, and answering it by hand here would put the very list this file
// forbids custody from holding into the file that forbids it.
import * as narrativeCipherModule from './narrative-cipher';
// The two result unions, and the two **function types** the seam pins at the
// foot of this file are about. Type-only, so nothing here crosses into the
// bundle — and the two openers are imported rather than written out, because a
// signature restated in this file is a signature that cannot disagree with the
// declaration a mapper will actually be typed against.
import type {
  BlindIndexValue,
  NarrativeIndexer,
  NarrativeOpener,
  SealedField,
} from './narrative-text';

// Three canonical factor ids, distinct and in the spelling the server renders.
// The identifier a row carries **is** the associated data its two envelopes were
// sealed with, so these are not labels — a wrong one here is an envelope that
// does not open, which is exactly the property two of the cases below turn on.
const FIRST_FACTOR_ID = 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6';
const SECOND_FACTOR_ID = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0';
const THIRD_FACTOR_ID = '7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d';
// A fourth, and it exists for exactly one arrangement: the substitution below
// needs a served set the same size as the manifest's, which takes three of the
// account's own and one that is not.
const FOURTH_FACTOR_ID = '2b3c4d5e-6f70-8192-a3b4-c5d6e7f80912';

// The account this browser is inside, as the identity read answers it.
//
// **A second budget identifier in this file, and it is not `BUDGET_ID`.** That
// one is the tenancy the frozen blind-index answers were computed in and is read
// off the vector file, so it moves the day that file does — and what it keys is
// a *value*, inside a message. This one is the key the epoch record is filed
// under, which is a fact about this session and about nothing in the codec. They
// are the same kind of identifier doing two unrelated jobs, and one constant
// serving both would let a change to a vector file silently move where a
// device's memory of an account lives.
//
// It is declared here rather than beside `BUDGET_ID` because the stub below
// closes over it.
const SESSION_BUDGET_ID = '01a05f2c-7b19-7c3d-8e4f-5a6b7c8d9e0f';

// A second account, for the rule that one device's memory of one account says
// nothing about another.
const OTHER_BUDGET_ID = '0192f4c1-6d2a-7e88-9b31-2c4d5e6f7a80';

const SESSION_OWNER: MeDto = {
  budgetId: SESSION_BUDGET_ID,
  email: 'owner@budgetoid.test',
};

class MeApiStub {
  // No manifest, epoch zero and no factors by default, which is the whole answer
  // the route gives for a session it cannot see. Every case that means something
  // else says so out loud.
  public getAccountKeys = vi.fn(
    (): Observable<AccountKeyCustodyDto> => of(custodyOf([])),
  );

  // **The identity read, and it is custody's own.** The epoch record is filed
  // per account, and the only per-account identifier a browser holds is the
  // budget this session is scoped by — which is `null` on the sign-in path at
  // the instant `unlock()` is called, because `SessionService.established()`
  // starts its own read without awaiting it and sign-in unlocks on the next
  // line. So custody asks rather than borrowing, and it asks *this* route: the
  // one member carrying `EXPECTS_UNAUTHENTICATED`, whose 401 is this call's own
  // answer rather than a session ending.
  public getSessionOwner = vi.fn((): Observable<MeDto> => of(SESSION_OWNER));
}

// **The one collaborator this service must never acquire.** It is provided so
// that a call to it would land somewhere countable: without the provider, an
// implementation that reached for `SessionService` would either be handed the
// real one — which would publish `anonymous` and sign somebody out of an account
// they are demonstrably inside — or fail to inject and redden for the wrong
// reason. Provided and spied, the assertion reads as what it means.
class SessionStub {
  public ended = vi.fn((): void => undefined);
  public established = vi.fn((): void => undefined);

  // **The member the identity read makes tempting, and the trap is only a trap
  // while it is here.** `SessionService` publishes the budget as a signal, so a
  // custody that injected it would read `session.budgetId()` and touch neither
  // counter above — the census would report nothing at all while the edge it
  // exists to refuse had been added. A signal is a call, so a spy shaped like
  // one records the read.
  public budgetId = vi.fn((): string | null => SESSION_BUDGET_ID);
}

// Flushes the turns a real WebCrypto call resolves on. Node's implementation
// hands some operations to a thread pool, so draining microtasks is not enough
// and a bare `await Promise.resolve()` loop would read a half-finished attempt.
async function flush(turns = 5): Promise<void> {
  for (let turn = 0; turn < turns; turn += 1) {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
  }
}

// Waits for an attempt to end, whichever way it ended.
//
// `unlock` returns nothing — which is the point of one of the cases below — so
// there is no promise to await and the only marker is the status leaving
// `'unlocking'`. A timeout here is a failure and not a hang: `vi.waitFor` gives
// up loudly, which is the right answer for an attempt that never resolved.
async function settled(custody: AccountKeyCustodyService): Promise<void> {
  await vi.waitFor(() => {
    expect(custody.status()).not.toBe('unlocking');
  });
}

// The service's own source, for the rules below that read what was written
// rather than what runs. `process.cwd()` is the project root under this
// runner, the same anchor `key-import-single-source.spec.ts` uses, and `src/`
// is read rather than the emitted bundle: the claim is about what a reviewer
// reads, and a minifier that renamed a `#` field would answer the question
// wrongly whichever way it answered it.
const CUSTODY_SOURCE = join(
  process.cwd(),
  'src',
  'app',
  '+core',
  'security',
  'account-key-custody.service.ts',
);

// The text of one method's body, or a throw.
//
// It brackets on the declaration line and on the first line that is exactly a
// closing brace at method indentation, which is what makes the result *this*
// method rather than the file — and the file is the failure mode that matters:
// a reader who "simplified" this to `source.includes(…)` would have written a
// guard that passes against the very edit it exists to catch, because the two
// assignments it looks for also appear, in another form, in `#hold`.
//
// The throw is deliberate and is not an error path. A method that was renamed
// or reshaped is a change to the thing being pinned, and the honest answer is a
// red bar naming it rather than a silent pass over a region that no longer
// exists.
function bodyOf(source: string, declaration: string): string {
  const opened = source.indexOf(declaration);

  if (opened === -1) {
    throw new Error(
      `account-key-custody.service.ts no longer declares \`${declaration}\`, so this rule is pinned against nothing.`,
    );
  }

  const rest = source.slice(opened + declaration.length);
  const closed = rest.indexOf('\n  }');

  if (closed === -1) {
    throw new Error(
      `\`${declaration}\` has no closing brace at method indentation, so its body could not be read.`,
    );
  }

  return rest.slice(0, closed);
}

// A key-encryption key of a stated seed, through the module's own door.
//
// Through `importAesGcmKey` rather than through a hand-written
// `crypto.subtle.importKey`, because `key-import-single-source.spec.ts` exempts
// specs from that rule and this file has no reason to take the exemption: the
// door is exported, it is what every production caller uses, and a fixture built
// the other way would be a fixture whose width and usages nothing checked.
function keyEncryptionKey(seed: number): Promise<CryptoKey> {
  return importAesGcmKey(new Uint8Array(ACCOUNT_KEY_BYTES).fill(seed));
}

// One row of `wrapped_account_keys` as it crosses the wire: the identifier the
// keypair was bound to, the private half wrapped under `kek`, and the account's
// two keys encapsulated to the public half.
//
// **Production's own writer, never a hand-built envelope.** `mintFactorKeypair`
// is what every path that enrols a factor calls, so an entry built here is the
// entry the product really stores — which is what makes "the service opens it"
// a claim about the product rather than about this file's idea of a row.
async function entryFor(
  kek: CryptoKey,
  factorId: string,
  keys: AccountKeys,
): Promise<AccountKeyEntry> {
  const { wrappedPrivateKey, encapsulatedAccountKeys } =
    await mintFactorKeypair(kek, factorId, keys);

  return { factorId, wrappedPrivateKey, encapsulatedAccountKeys };
}

// The same, and the factor's public point beside it — which the manifest names
// and no row carries.
async function factorFor(
  kek: CryptoKey,
  factorId: string,
  keys: AccountKeys,
): Promise<{ entry: AccountKeyEntry; publicKey: Uint8Array }> {
  const minted = await mintFactorKeypair(kek, factorId, keys);

  return {
    entry: {
      factorId,
      wrappedPrivateKey: minted.wrappedPrivateKey,
      encapsulatedAccountKeys: minted.encapsulatedAccountKeys,
    },
    publicKey: minted.publicKey,
  };
}

// The whole body, with **no manifest** — `manifest: null`, `rotationEpoch: 0`.
//
// That is the default every case which is not about the manifest gate uses, and
// deliberately so: an account registered before the manifest landed answers it
// forever, the service is specified to proceed on it, and a fixture carrying one
// everywhere would hide a gate that refused every account it was handed.
function custodyOf(factors: readonly AccountKeyEntry[]): AccountKeyCustodyDto {
  return { manifest: null, rotationEpoch: 0, factors };
}

// The body an account with a manifest answers: the blob sealed under the very
// content key the entries encapsulate, at the epoch it is in.
//
// The content key is imported from a **copy** of the bytes, because
// `importAesGcmKey` wipes what it is handed and the same material has to reach
// the entries.
async function custodyWith(
  keys: AccountKeys,
  factors: readonly { entry: AccountKeyEntry; publicKey: Uint8Array }[],
  rotationEpoch = 1,
  sealingKey?: Uint8Array,
): Promise<AccountKeyCustodyDto> {
  const contentKey = await importAesGcmKey(
    Uint8Array.from(sealingKey ?? keys.contentKey),
  );

  return {
    manifest: await sealFactorManifest(
      contentKey,
      factors.map(({ entry, publicKey }) => ({
        factorId: entry.factorId,
        publicKey,
      })),
      rotationEpoch,
    ),
    rotationEpoch,
    factors: factors.map(({ entry }) => entry),
  };
}

// A canonical row id, in the one spelling a uuid column hands back. Not a
// factor id, and deliberately not one of the three above: a binding names a row
// of a ledger table, and reusing a factor id here would read as though the two
// identifiers were interchangeable in some case.
const ROW_ID = '0192f8a1-7c3d-7e00-8b2a-3f4d5e6a7b8c';

// A second row of the same table and the same column, equally canonical. What
// it is for is the case a foreign wire cannot reach: a value this service
// really sealed, read back under a binding that differs in the row alone.
const OTHER_ROW_ID = '0192f8a1-7c3d-7e00-8b2a-3f4d5e6a7b8d';

// One legal binding, taken off the codec's own list rather than typed out. The
// pair that opens `NARRATIVE_FIELDS` is as good as any other — what matters is
// that this file does not become a second place where a table and a column are
// spelled, which is exactly the rule the last describe in this file pins on the
// service.
const BINDING: NarrativeFieldBinding = {
  ...NARRATIVE_FIELDS[0],
  rowId: ROW_ID,
};

// The same table and the same column, a different row.
const OTHER_ROW_BINDING: NarrativeFieldBinding = {
  ...NARRATIVE_FIELDS[0],
  rowId: OTHER_ROW_ID,
};

// Row-id spellings that all name the same value and that a caller could
// plausibly arrive with — spellings the codec's predicate documents as refused,
// plus the all-zero uuid, which it documents as *not* its business.
//
// Written as candidates and filtered below rather than asserted about
// directly, because which of them are refused is `isCanonicalRowId`'s answer
// and not this file's opinion.
//
// Deliberately not counted here, and deliberately not the predicate's whole
// documented list — the parenthesis-wrapped form is one it names and this array
// does not carry. A number written into this sentence goes stale the first time
// a candidate is added or dropped, and nothing goes red about it; the count that
// matters is derived below and asserted non-empty by the control that drives it.
const ROW_ID_SPELLINGS = [
  { how: 'upper-case hex', rowId: ROW_ID.toUpperCase() },
  { how: 'the braced form', rowId: `{${ROW_ID}}` },
  { how: 'a leading space', rowId: ` ${ROW_ID}` },
  { how: 'a trailing space', rowId: `${ROW_ID} ` },
  { how: 'the unhyphenated 32-digit form', rowId: ROW_ID.replaceAll('-', '') },
  { how: 'the all-zero uuid', rowId: '00000000-0000-0000-0000-000000000000' },
] as const;

// The subset the codec really refuses, derived by asking it.
//
// Derived and not typed out, which is the whole point of the case it drives:
// the rule under test is that the service lets the codec judge a row id instead
// of judging one itself, and a hand-written list here would be this file making
// the same mistake it is refusing.
const REFUSED_ROW_IDS = ROW_ID_SPELLINGS.filter(
  ({ rowId }) => !isCanonicalRowId(rowId),
);

// The frozen blind-index vectors, computed outside this codebase.
//
// **Read here as well as in `blind-index.spec.ts`, and the duplication is the
// point rather than an oversight.** That file pins the *codec* against the
// contract; this one pins that custody **reaches** the codec, and the only
// evidence that separates "it delegated" from "it reimplemented the grammar
// inline and got it right for the case I happened to write" is the frozen
// answer. A `blindIndex` that hashed the name under the index key and forgot the
// prefix, the table or the fold computes stable, unique, 43-character values
// forever and matches no second client and no row already written.
//
// The parser is small and throwing rather than shared: neither file exports one,
// and a fixture loader that returned `undefined` on a malformed file would drive
// a clean run over nothing.
const BLIND_INDEX_VECTOR_FILE = join(
  process.cwd(),
  '..',
  '..',
  'docs',
  'business-logic',
  'vectors',
  'blind-index-v1.json',
);

interface FrozenBlindIndexVector {
  readonly why: string;
  readonly table: string;
  readonly column: string;
  /** The tenancy this answer was computed inside — the file's, or its own. */
  readonly budgetId: string;
  readonly inputs: readonly string[];
  readonly blindIndex: string;
}

interface FrozenBlindIndexFile {
  readonly budgetId: string;
  readonly indexKeyHex: string;
  readonly vectors: readonly FrozenBlindIndexVector[];
}

function requireVectorString(
  source: Record<string, unknown>,
  key: string,
  what: string,
): string {
  const value = source[key];

  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`${what} carries no ${key}.`);
  }

  return value;
}

function parseBlindIndexVectors(text: string): FrozenBlindIndexFile {
  const parsed: unknown = JSON.parse(text);

  if (typeof parsed !== 'object' || parsed === null) {
    throw new Error('The blind-index vector file is not an object.');
  }

  const file = parsed as Record<string, unknown>;
  const vectors = file['vectors'];

  if (!Array.isArray(vectors) || vectors.length === 0) {
    throw new Error('The blind-index vector file lists no vectors.');
  }

  const fileBudgetId = requireVectorString(
    file,
    'budgetId',
    'The blind-index vector file',
  );

  return {
    budgetId: fileBudgetId,
    indexKeyHex: requireVectorString(
      file,
      'indexKeyHex',
      'The blind-index vector file',
    ),
    vectors: vectors.map((entry: unknown) => {
      if (typeof entry !== 'object' || entry === null) {
        throw new Error('A blind-index vector is not an object.');
      }

      const vector = entry as Record<string, unknown>;
      const why = requireVectorString(vector, 'why', 'A blind-index vector');
      const inputs = vector['inputs'];

      if (!Array.isArray(inputs) || inputs.length === 0) {
        throw new Error(`${why} lists no inputs.`);
      }

      // The one optional member in the file: absent means the file's tenancy,
      // present means this vector's own. Resolved here so that every case below
      // reads one member and the default is applied in exactly one place.
      const own = vector['budgetId'];

      if (own !== undefined && (typeof own !== 'string' || own.length === 0)) {
        throw new Error(`${why} carries a budgetId that is not a spelling.`);
      }

      return {
        why,
        table: requireVectorString(vector, 'table', why),
        column: requireVectorString(vector, 'column', why),
        budgetId: own ?? fileBudgetId,
        inputs: inputs.map((input: unknown, at: number) => {
          if (typeof input !== 'string') {
            throw new Error(`${why} carries a non-string input at ${at}.`);
          }

          return input;
        }),
        blindIndex: requireVectorString(vector, 'blindIndex', why),
      };
    }),
  };
}

const BLIND_INDEX_VECTORS = parseBlindIndexVectors(
  readFileSync(BLIND_INDEX_VECTOR_FILE, 'utf8'),
);

// Every `(vector, spelling)` pair flattened, so each frozen spelling is a case
// of its own rather than one case that stops at the first disagreement.
const FROZEN_BLIND_INDEX_CASES = BLIND_INDEX_VECTORS.vectors.flatMap((vector) =>
  vector.inputs.map((input) => ({
    why: vector.why,
    table: vector.table,
    column: vector.column,
    budgetId: vector.budgetId,
    input,
    blindIndex: vector.blindIndex,
  })),
);

// The tenancy every case that is not driven by a vector keys inside. Read off
// the frozen file rather than typed out, so nothing here can key under a
// spelling no answer was ever computed with.
const BUDGET_ID = BLIND_INDEX_VECTORS.budgetId;

// The buffer type is spelled out because `BufferSource` excludes a view over a
// `SharedArrayBuffer`, and a bare `Uint8Array` is a view over either.
function fromHex(text: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(text.match(/../g) ?? [], (pair) => parseInt(pair, 16));
}

// The index key the frozen answers were computed under, rebuilt from hex at
// every call. `importHmacSha256Key` zero-fills the material it is handed, so a
// single shared array would be all zeroes from the second import onwards — and
// an HMAC key of 32 zero bytes signs perfectly and matches no vector.
function frozenIndexKey(): Promise<CryptoKey> {
  return importHmacSha256Key(fromHex(BLIND_INDEX_VECTORS.indexKeyHex));
}

// The pair a frozen vector names, resolved to the value the closed union holds.
// Resolving rather than casting is what makes a vector naming a table this
// product does not index an error here, instead of a value that types as a legal
// pair and is not one.
function indexedFieldFor(table: string, column: string): BlindIndexedField {
  const found = BLIND_INDEXED_FIELDS.find(
    (candidate) => candidate.table === table && candidate.column === column,
  );

  if (found === undefined) {
    throw new Error(`${table}.${column} is not a blind-indexed field.`);
  }

  return found;
}

// A resolved pair plus the tenancy the value is keyed inside.
//
// Written here rather than reached for through one of the four view modules,
// because this file is about what custody does with the argument it is handed
// and not about which pair a screen means by it.
function indexBindingFor(
  table: string,
  column: string,
  budgetId: string = BUDGET_ID,
): BlindIndexBinding {
  return { ...indexedFieldFor(table, column), budgetId };
}

// The first indexed pair, keyed inside the file's tenancy. The cases that do not
// care which pair they use take this.
function firstIndexBinding(budgetId: string = BUDGET_ID): BlindIndexBinding {
  return { ...BLIND_INDEXED_FIELDS[0], budgetId };
}

// A real table beside a real column of it that is **not** its indexed pair —
// `categories.description` today, and derived rather than typed so that it is
// still the right shape after the two lists move.
//
// It is taken from `NARRATIVE_FIELDS` rather than invented, so it is a pair some
// caller could genuinely arrive holding: `categories` is one of the four indexed
// tables and `description` is a real, encrypted column of it. What must not be
// indexed is the *combination*, and a caller assembling a field from a row it
// read is exactly how the wrong combination arrives.
//
// **What this pair cannot separate, stated rather than assumed.** It does not
// tell a lookup of the pair from two independent membership tests, and no pair
// can today: all four of `BLIND_INDEXED_FIELDS` carry the value in `name`, so
// "the column is indexed" and "this table's column is indexed" are the same
// question, and a check written either way refuses `categories.description`
// alike. That distinction becomes reachable the day a second indexed column
// lands on one of the four tables — the migration `blind-index.ts` says the
// column field is in the message for — and a case for it belongs there and not
// before.
const UNINDEXED_PAIR = ((): {
  readonly table: string;
  readonly column: string;
} => {
  const found = NARRATIVE_FIELDS.find(
    (field) =>
      BLIND_INDEXED_FIELDS.some((indexed) => indexed.table === field.table) &&
      !BLIND_INDEXED_FIELDS.some(
        (indexed) =>
          indexed.table === field.table && indexed.column === field.column,
      ),
  );

  if (found === undefined) {
    throw new Error(
      'No narrative pair sits on an indexed table without being indexed itself, so the illegal-field cases are driven by nothing.',
    );
  }

  return found;
})();

// The whole public surface of the class, as a set.
//
// **A set compared to a set, so that moving a member up the file never reddens
// and only a new one does.** An array would pin the declaration order too, and
// a red bar over a reordering is a red bar a reader learns to answer by editing
// the expectation — at which point the census has stopped meaning anything.
const PUBLIC_SURFACE = new Set([
  'status',
  'unlockFailure',
  'unlock',
  'adopt',
  'lock',
  'sealField',
  'openField',
  'blindIndex',
]);

// The eight pairs' words — six tables and two columns, deduplicated by the
// `Set` — derived from the codec and never restated.
//
// Derived, because a hand-written copy here is a second declaration of which
// fields are encrypted: a ninth pair added to `NARRATIVE_FIELDS` would arrive
// with this rule silently not covering it, and the failure that follows is a
// table name leaking into custody with nothing going red.
const FORBIDDEN_WORDS = new Set(
  NARRATIVE_FIELDS.flatMap(({ table, column }) => [table, column]),
);

// The codec's exports that are **data** rather than operations, by name.
//
// Derived by asking the module and never typed out, for the reason
// `FORBIDDEN_WORDS` is derived: a name written here is one more place this rule
// can silently stop covering what it says it covers.
//
// `typeof` is the classifier, and it sorts a class into `'function'` — which is
// the right answer: an error type is an operation-shaped export and custody may
// import it. What is left is the field list and the associated-data prefix.
// **The list is the one that matters** — it *is* the eight pairs — and the
// prefix joins it because a custody holding the prefix is rebuilding the
// grammar, which is the same leak one step earlier.
const FORBIDDEN_VALUE_IMPORTS = new Set(
  Object.entries(narrativeCipherModule)
    .filter(([, value]) => typeof value !== 'function')
    .map(([name]) => name),
);

// Every `public` member the class declares, by name.
//
// It reads declarations and not behaviour, which is the whole technique: a
// getter over a key field is a *declaration*, and the four lines that add one
// are the edit this rule exists to catch.
function publicMembers(source: string): Set<string> {
  return new Set(
    [
      ...source.matchAll(
        /^ {2}public (?:(?:static|readonly|async|get|set) )*([A-Za-z][A-Za-z0-9]*)/gm,
      ),
    ].map(([, name]) => name),
  );
}

// The declaration text of one member: everything from `public` to whichever of
// `{`, `=` or `;` comes first, which is the body opener, the initialiser or the
// end of a bare field declaration respectively.
//
// It is bounded forwards rather than by the next member, so a private method
// sitting between two public ones can never be read as part of either.
function signatureAt(source: string, index: number): string {
  const rest = source.slice(index);
  const ends = ['{', '=', ';']
    .map((token) => rest.indexOf(token))
    .filter((at) => at !== -1);

  return rest.slice(0, ends.length === 0 ? rest.length : Math.min(...ends));
}

// Everything in the class that hands a key object back, as a set of findings.
//
// Two needles, because the mistake has two shapes and neither sees the other:
//
//   * a declaration whose **return position** names `CryptoKey` — the annotated
//     accessor, `public keyFor(id: string): CryptoKey`. Read after the last
//     `)`, so the two members that legitimately *take* a `CryptoKey` —
//     `unlock` and `adopt` — are not findings.
//   * a `return` of one of the two key fields, anywhere in the file and at any
//     accessibility, which is what an unannotated `public get contentKey() {
//     return this.#contentKey; }` looks like. A private helper that returned
//     one would be a finding too, and should be: it is one `public` away from
//     the thing being refused.
//
// Limits, stated rather than papered over. It cannot see a key handed back
// inside an object literal, through a callback parameter, or from a member
// annotated `unknown` — the census above catches all three from the other side,
// by reddening on the new member itself whatever it returns. And it says
// nothing about a key that leaves through a member that already exists, which
// no source scan could.
function keysHandedBack(source: string): Set<string> {
  const found = new Set<string>();

  for (const field of ['#contentKey', '#indexKey']) {
    if (new RegExp(`return this\\.${field}\\b`).test(source)) {
      found.add(field);
    }
  }

  for (const match of source.matchAll(
    /^ {2}public (?:(?:static|readonly|async|get|set) )*([A-Za-z][A-Za-z0-9]*)/gm,
  )) {
    const [, name] = match;
    const signature = signatureAt(source, match.index);
    const closed = signature.lastIndexOf(')');
    const returned = closed === -1 ? signature : signature.slice(closed);

    if (returned.includes('CryptoKey')) {
      found.add(name);
    }
  }

  return found;
}

// Which of the eight pairs' words `source` names, whole-word and case-blind.
//
// Whole words, or the rule is red on arrival for the wrong reason: `naming`,
// `named` and `names` all carry `name`, and the service's prose is full of
// them. `\b` refuses each of those and accepts `name`, `Name` and `'name'`.
//
// Limits. `payee`, `budget` and `transaction` in the singular pass, as does a
// word assembled at runtime; nothing short of a parser would catch either, and
// neither is the shape this leak takes — a caller pasting a pair in is pasting
// the plural the column list uses. Case-blindness means prose naming a ledger
// table reddens too, which is deliberate: an argument about which columns are
// encrypted belongs in `narrative-cipher.ts`, and a copy of it here is the
// first half of the same drift a copied literal would cause.
function narrativeWordsIn(source: string): Set<string> {
  return new Set(
    [...FORBIDDEN_WORDS].filter((word) =>
      new RegExp(`\\b${word}\\b`, 'i').test(source),
    ),
  );
}

// Every name `source` imports from the codec as a **value**, with the namespace
// form reported under a name of its own.
//
// **The word scan above has a hole exactly this shape.** Adding
// `import { NARRATIVE_FIELDS } from './narrative-cipher';` to custody and
// branching on `NARRATIVE_FIELDS[0].table` puts the eight pairs inside the class
// with **no forbidden word anywhere in the file** — the words are in the codec,
// and the class only says the identifier. The requirement is that this class
// does not learn the pairs, and a value it can index is learning them.
//
// It drops everything the compiler erases, which is the distinction the whole
// scanner turns on: a whole `import type { … }` statement crosses nothing into
// the bundle, and neither does a `type` specifier inside a mixed clause — which
// is the exact shape custody uses for the binding. A scanner that could not tell
// those apart would be red on arrival for a legal import and would be answered
// by deleting it.
//
// `* as codec` is a finding whatever it is called: it puts every export of the
// module one property access away, so a rule that only knew specifier names
// would report nothing while the list sat one dot from a branch.
//
// Limits, stated rather than papered over:
//
//   * it reads text and not the module graph, so it says nothing about a
//     dynamic `import()`, or about the list arriving through a third module
//     that re-exports it;
//   * it says nothing about the eight pairs written out as literals — that is
//     the word scan's half, and neither covers the other;
//   * the clause is bounded by the `;` of the previous statement, so an import
//     written with a semicolon inside a comment in its own clause would be read
//     wrongly. That is not a shape this formatter emits.
function valueImportsFromCodec(source: string): Set<string> {
  const found = new Set<string>();

  for (const [, clause] of source.matchAll(
    /import\s+([^;]*?)\s+from\s+'\.\/narrative-cipher';/g,
  )) {
    if (/^type\s/.test(clause)) {
      continue;
    }

    const namespace = /^\*\s+as\s+([A-Za-z_$][\w$]*)$/.exec(clause);

    if (namespace !== null) {
      found.add(`* as ${namespace[1]}`);
      continue;
    }

    const braced = /\{([\s\S]*)\}/.exec(clause);

    for (const specifier of (braced?.[1] ?? '').split(',')) {
      const trimmed = specifier.trim();

      if (trimmed.length === 0 || /^type\s/.test(trimmed)) {
        continue;
      }

      found.add(trimmed.split(/\s+/)[0]);
    }
  }

  return found;
}

describe('AccountKeyCustodyService', () => {
  let api: MeApiStub;
  let session: SessionStub;
  let custody: AccountKeyCustodyService;

  beforeEach(() => {
    // jsdom's `localStorage` is per test *file* and not per case, so a rotation
    // epoch a successful unlock recorded is an invisible fixture for every case
    // after it — and the cases below turn on what this device has and has not
    // seen. Cleared here rather than in the describes that record, because every
    // successful unlock in this file writes one.
    localStorage.clear();
    api = new MeApiStub();
    session = new SessionStub();
    TestBed.configureTestingModule({
      providers: [
        { provide: MeApiService, useValue: api },
        { provide: SessionService, useValue: session },
      ],
    });
    custody = TestBed.inject(AccountKeyCustodyService);
  });

  it('starts locked, holding nothing and blaming nobody', () => {
    // Arrange, Act, Assert
    // The floor every other case stands on. Without it, a service that reported
    // `unlocked` from the first instant would pass most of this file: the
    // arrangements below check the word after an attempt, and `unlocked` was
    // already there.
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBeNull();
  });

  it('tries every entry in turn and opens the one that is this factor', async () => {
    // Arrange
    // **Three entries, and the second is the one that opens.** A passkey session
    // is answered with one entry and a recovery-code session with ten, so the
    // list of one is the case a reader optimises into `entries[0]` — and it
    // works, forever, on every passkey account in the product. What it does to
    // the other kind is read code #1's envelopes under code #7's key-encryption
    // key: the open fails to authenticate, the loop that would have found the
    // right pair is not there, and somebody who redeemed a valid code is told
    // their account cannot be opened.
    //
    // Second rather than first, and with a third behind it, so neither "take the
    // head" nor "take the last" reaches this assertion.
    //
    // **The body carries a manifest naming all three**, which is what every
    // account this product can create answers. It is not what this case is
    // about — the gate has a describe of its own below — but a manifest-less
    // body is refused now, so a fixture without one would redden this case for
    // a reason it makes no claim about.
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x11);
    const otherKek = await keyEncryptionKey(0x22);

    const factors = [
      await factorFor(otherKek, FIRST_FACTOR_ID, keys),
      await factorFor(kek, SECOND_FACTOR_ID, keys),
      await factorFor(otherKek, THIRD_FACTOR_ID, keys),
    ];

    api.getAccountKeys.mockReturnValue(of(await custodyWith(keys, factors)));

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('unlocked');
    expect(custody.unlockFailure()).toBeNull();
  });

  it('rebuilds the associated data from the entry it is trying', async () => {
    // Arrange
    // The identifier a row carries is what its two envelopes were sealed
    // against, so the trial has to re-supply *that* one — not the first entry's,
    // not one this client remembered from the ceremony, not one it minted.
    //
    // The arrangement is what makes the difference visible: the entry that opens
    // is filed under a different factor id from the entry ahead of it, and the
    // entry ahead of it is one this key cannot open. An implementation that
    // rebuilt the associated data from `entries[0].factorId` — or from any value
    // fixed before the loop — hands GCM the wrong bytes on the entry that would
    // otherwise have opened, and the authentication fails with the same silence
    // a wrong key gives. The account is then declared unopenable by its own
    // custody, and nothing anywhere names the cause.
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x33);
    const otherKek = await keyEncryptionKey(0x44);

    const factors = [
      await factorFor(otherKek, FIRST_FACTOR_ID, keys),
      await factorFor(kek, SECOND_FACTOR_ID, keys),
    ];

    // The guard that keeps the arrangement honest: equal ids here would make the
    // case pass on the implementation it exists to refuse.
    expect(factors[0].entry.factorId).not.toBe(factors[1].entry.factorId);

    // A manifest naming both, for the reason the case above states: the body
    // this case is about is one an account really answers.
    api.getAccountKeys.mockReturnValue(of(await custodyWith(keys, factors)));

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('unlocked');
  });

  it('stays locked and says unopened when no entry is this factor', async () => {
    // Arrange
    // A key-encryption key that opens nothing — a person who presented a factor
    // this account does not hold, or a code from a card that has been replaced.
    const keys = generateAccountKeys();
    const otherKek = await keyEncryptionKey(0x55);
    const presented = await keyEncryptionKey(0x66);

    api.getAccountKeys.mockReturnValue(
      of(
        custodyOf([
          await entryFor(otherKek, FIRST_FACTOR_ID, keys),
          await entryFor(otherKek, SECOND_FACTOR_ID, keys),
        ]),
      ),
    );

    // Act
    custody.unlock(presented);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBe('unopened');

    // **And nothing on `SessionService` was touched.** This is the direct pin on
    // "a key failure is not an authentication failure". The person is signed in,
    // the server answered, and what failed is the factor they presented —
    // publishing `anonymous` from here would sign somebody out of an account
    // they are demonstrably inside. Zero calls, both ways: `established()` is in
    // the assertion too because the mistake is available in the happy direction
    // as well, and a service that announced a session on every successful unlock
    // would be making a claim about authentication out of a fact about a key.
    expect(session.ended).not.toHaveBeenCalled();
    expect(session.established).not.toHaveBeenCalled();
  });

  it('reads an empty list as unopened rather than as an error', async () => {
    // Arrange
    // The route answers `[]` both for a session it cannot see and for a
    // credential carrying no factors, indistinguishably and on purpose — so
    // there is nothing to tell apart, and a third word here would claim a
    // difference this client was never told. The next step is the same one every
    // other `unopened` has: present another factor.
    api.getAccountKeys.mockReturnValue(of(custodyOf([])));
    const kek = await keyEncryptionKey(0x77);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBe('unopened');
  });

  it('reads a network that never answered as unreachable', async () => {
    // Arrange
    // Status `0` — the request never reached a server. **Never `unopened`.**
    // Collapsed, a person is sent hunting for a recovery card over a network
    // that blinked, and the way forward they are given is the one thing that
    // cannot help them.
    api.getAccountKeys.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 0 })),
    );
    const kek = await keyEncryptionKey(0x78);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBe('unreachable');
    expect(session.ended).not.toHaveBeenCalled();
  });

  // **A body this client could not read is `unrecognised`, and it used to be
  // `unreachable`.** Both are statements about a read rather than about a
  // factor, which is why they were one word for a while — but `unreachable`
  // carries the advice *try again in a minute*, and a minute cannot help here:
  // the next attempt runs this same bundle against that same route and is
  // refused the same way. A reload is the one thing that can change it.
  //
  // **The word reports and does not diagnose.** It was `outdated`, which named
  // a cause — *this tab is running an older version* — from evidence that
  // cannot support it: this boundary also refuses the retired bare array, which
  // is a **newer** bundle reading an **older** route, and there the diagnosis
  // is false and the remedy it offers loops forever. What was observed is that
  // the answer came back in a shape this client does not recognise, and that is
  // what the word says.
  //
  // The two are indistinguishable from inside a `catch` and are told apart only
  // by the type the boundary throws, which is why `AccountKeyResponseError` is
  // a type rather than a message.
  it('reads a refused body as unrecognised, not as a network that blinked', async () => {
    // Arrange
    api.getAccountKeys.mockReturnValue(
      throwError(
        () =>
          new AccountKeyResponseError(
            "The account-key response did not arrive as an account's key custody.",
          ),
      ),
    );
    const kek = await keyEncryptionKey(0x79);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBe('unrecognised');

    // **And still nothing on `SessionService`.** The word is new; the rule is
    // not. A body this browser cannot parse says nothing about who is asking,
    // and publishing `anonymous` over it would sign somebody out of an account
    // they are demonstrably inside.
    expect(session.ended).not.toHaveBeenCalled();
    expect(session.established).not.toHaveBeenCalled();
  });

  it('still reads a throw that is not that refusal as unreachable', async () => {
    // Arrange
    // The control for the case above, and the half that keeps the new word from
    // swallowing the old one. A `#fail('unrecognised')` written for every throw
    // that is not an `HttpErrorResponse` passes the case above perfectly — and
    // would tell somebody whose network blinked to reload a page that is fine.
    //
    // A bare `Error` is what an interceptor, an operator or a platform fault
    // throws. None of them is this route saying something this client cannot
    // read.
    api.getAccountKeys.mockReturnValue(
      throwError(() => new Error('The connection was closed while reading.')),
    );
    const kek = await keyEncryptionKey(0x7a);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.unlockFailure()).toBe('unreachable');
  });

  // **The gate that confirms the content key is the content key.**
  //
  // A decapsulation that succeeds proves the 64 bytes are what was encapsulated
  // to this factor, and proves *nothing whatever* about which half of them is
  // which: the two keys sit in one plaintext with no separator, told apart by
  // position alone. A client that encapsulated them the other way round
  // produces a value of the right width, leading with the right version byte,
  // which stores, reads back and opens — and neither side of the wire can see
  // it. The manifest is sealed **under the content key**, so opening it is the
  // only runtime check anywhere that a reversed pair fails.
  //
  // **A manifest that does not open is `inconsistent`, and it used to be
  // `unopened`.** The two failures are one gate apart and their remedies are
  // opposite: `unopened` says *present another factor*, and every factor of an
  // account encapsulates the same two keys — so if the pair that came out will
  // not open this account's manifest, no passkey and none of the ten recovery
  // codes will change that, in this browser or any other, after any number of
  // reloads. Told `unopened`, somebody spends their card of codes one at a time
  // on a door that cannot open. The word here has to say that nothing they hold
  // is the answer, and it has to keep saying it when story 12.14 adds the
  // neighbouring integrity failures — a served factor set that disagrees with
  // the manifest's, an epoch rolled back — which are the same statement about
  // the same material.
  describe("the content key is confirmed against the account's manifest", () => {
    it('unlocks when the manifest opens under the key an entry handed over', async () => {
      // Arrange
      // The positive control, and the case every refusal below stands on:
      // without it, a gate that refused every manifest ever sent would satisfy
      // all of them.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x90);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(of(await custodyWith(keys, [factor])));

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('unlocked');
      expect(custody.unlockFailure()).toBeNull();
    });

    it('stays locked when the manifest does not open under it', async () => {
      // Arrange
      // **The reversed pair, in the one shape this client can build.** The
      // entries encapsulate the account's two keys content-first, as production
      // writes them; the manifest is sealed under the account's *index* key. To
      // this service that is indistinguishable from a client that had swapped
      // the halves — what it obtained as a content key is not the key the
      // manifest was sealed under — and it is precisely the fault no width
      // check, no version byte and no round trip can see.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x91);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(keys, [factor], 1, keys.indexKey)),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // **`inconsistent` and never `unopened`.** `unopened`'s remedy — present
      // another factor — cannot help here: every factor of this account
      // encapsulates the same two keys, so a pair that will not open the
      // manifest will not open it under any of them. The two words are the two
      // halves of this section's rule, and the case below feeds the same
      // service a factor that genuinely did not open, so neither word can be
      // the answer to both.
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    it('still answers unopened when it was the factor that did not open', async () => {
      // Arrange
      // **The control, and it is what makes the case above a distinction rather
      // than a rename.** One entry, one manifest, both of them the account's
      // own and perfectly consistent — and a key-encryption key that belongs to
      // some other factor. Nothing about this account's key material is wrong;
      // what is wrong is which factor was presented, and the way forward is
      // another one.
      //
      // Without this case, `#fail('inconsistent')` written over the whole
      // attempt — or a gate moved inside the trial loop — passes every other
      // case in this describe and tells somebody holding the wrong passkey that
      // nothing they own will ever open their account.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x96);
      const otherKek = await keyEncryptionKey(0x97);
      const factor = await factorFor(otherKek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(of(await custodyWith(keys, [factor])));

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unopened');
    });

    it('stays locked when one character of the manifest was altered', async () => {
      // Arrange
      // **The accident rather than the attack, and it is the reason the word
      // matters.** A reversed pair is a client defect that the frozen vectors
      // catch before it can ship; a single altered character is a bad restore,
      // a half-written promote, a proxy that re-encoded a response. The
      // character is replaced from inside the base64url alphabet, so the value
      // still decodes and still has the right width — what fails is the tag,
      // which is the same failure any of those three produces.
      //
      // It is the same account, the same factor and the right key-encryption
      // key: everything a person holds is correct, and nothing they present can
      // make this account open. That is the whole of what the screen has to say
      // to them.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x98);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const served = await custodyWith(keys, [factor]);
      const { manifest } = served;

      // The fixture's own guard. `manifest` is `string | null` on the wire, and
      // a `?? ''` here would quietly turn this case into a width refusal over
      // zero bytes — the same colour of bar for a different reason.
      if (manifest === null) {
        throw new Error(
          'The fixture served no manifest, so there was nothing to alter.',
        );
      }

      const at = Math.floor(manifest.length / 2);
      const altered = `${manifest.slice(0, at)}${
        manifest[at] === 'A' ? 'B' : 'A'
      }${manifest.slice(at + 1)}`;

      api.getAccountKeys.mockReturnValue(of({ ...served, manifest: altered }));

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    it('stays locked when the manifest is at an epoch it was not sealed at', async () => {
      // Arrange
      // The epoch is the manifest's associated data, so a body that names a
      // generation the blob was not sealed at authenticates nothing. It is the
      // shape a replay takes: a manifest from before a rotation, served beside
      // the epoch the account is in now.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x92);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const custodyBody = await custodyWith(keys, [factor], 1);

      api.getAccountKeys.mockReturnValue(
        of({ ...custodyBody, rotationEpoch: 2 }),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // `inconsistent` for the reason the reversed pair gets it: the epoch is
      // part of what the manifest authenticates, so a body naming one the blob
      // was not sealed at is the account's key material disagreeing with
      // itself — and there is no factor a person could present that would make
      // the two agree.
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    // **A response carrying no manifest is refused, and this case replaces one
    // that asserted the opposite** — `unlocks on an account whose manifest is
    // absent`, which stood here until this story and which proceeded on `null`.
    //
    // That case was right while the manifest was a self-check: the gate could
    // only ever catch this client's own encapsulation order, so switching it off
    // cost an adversary nothing and refusing `manifest: null` would have locked
    // out rows written before the first manifest landed — a permanent lockout no
    // factor, no reload and no sign-in clears, over a defect the frozen keypair
    // vectors catch in the suite anyway. **In the suite and not in the build**:
    // nothing `ng build` compiles reads `factor-keypair-v1.json`. Its readers are
    // `factor-keypair.spec.ts`, `factor-manifest.spec.ts` and the C# integration
    // suite, and `tsconfig.app.json` lists `src/main.ts` alone, so no production
    // compilation ever sees them. The property is real; what holds it is a red
    // bar, and a sentence promising a broken build promises a gate nobody owns.
    //
    // Both halves of that argument are now false, and neither of them quietly.
    //
    //   * **The gate is load-bearing against an operator**, which is what the
    //     three refusals beside this one are for: a served factor the manifest
    //     does not name, a manifest naming a factor that was not served, an
    //     epoch below one this device has watched the account pass. Every one of
    //     them is switched off by a body answering `null`, so the bypass is not
    //     one branch of the gate — it is the gate.
    //   * **No account this product brings into existence can be in that
    //     state.** What holds the refusal is *not* that the server would decline
    //     to send a `null` — it sends one quite readily. The read behind this
    //     body answers a 200 carrying a `null` manifest at epoch `0`, that
    //     route's own prose spends a paragraph arguing a 404 there would be the
    //     mistake, and
    //     `AccountKeys_ForAnAccountWithNoManifest_AreNullAtEpochZeroAndNeverANotFound`
    //     pins exactly that. What is true instead is that registration files the
    //     first manifest at epoch 1 in the same save as the account, and each of
    //     the four paths that move a factor set carries one — so a `null`
    //     reaching this gate cannot be describing an account the product made.
    //     It is a response somebody shaped, and that is the one thing this gate
    //     exists to refuse.
    //
    // `inconsistent` and not a sixth word. What was observed is that the
    // account's material does not agree with itself — the same statement the
    // reversed pair, the altered byte and the rolled-back epoch make — and the
    // remedy is the same one: nothing this person holds changes it.
    it('refuses an account whose response carries no manifest', async () => {
      // Arrange
      // A factor that really opens, so `unopened` is not available as an answer
      // and the word under test cannot be reached by the loop failing.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa0);

      api.getAccountKeys.mockReturnValue(
        of(custodyOf([await entryFor(kek, FIRST_FACTOR_ID, keys)])),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    // **The gate runs once, after the trial loop, and never inside it.**
    //
    // Inside, it becomes a second reason an entry can be skipped — so "no
    // factor opened" and "a factor opened and its content key is wrong" arrive
    // at the same place by the same road, and the two can never be told apart
    // again. Both answer `unopened` today, which is exactly why the *word* is
    // blind to this and the count is not.
    //
    // The words differ now — `unopened` and `inconsistent` — so the *word* is
    // no longer blind to the arrangement below, but it is still not what this
    // case rests on: a gate moved inside the loop would answer `unopened` here,
    // which is a difference a reader could satisfy by editing one string. The
    // count is what cannot be satisfied that way, and it is asserted beside the
    // word rather than instead of it.
    //
    // **The instrument is the third entry's own envelopes, read through a
    // getter, and the assertion is that nothing read them.** What it replaced
    // was a census of `ECDH` imports — two if the gate runs once after the loop,
    // four if it runs inside it — and that number misleads in both directions.
    // Measured: validating the ephemeral point at the platform *before*
    // unwrapping the private half, an unrelated and entirely plausible
    // refactor, also produces four while the gate stays exactly where it is. A
    // red bar reading "expected 4 to be 2" therefore meant two opposite things,
    // told apart only by going and reading code the message does not name.
    //
    // A getter on the entry the loop must never reach has neither problem: it
    // is a fact about *this* arrangement, it cannot be moved by an import
    // order, and its failure names the rule — the trial loop went on to a third
    // entry after one had already opened.
    //
    // So the arrangement is three entries of which **two open**: the first is
    // filed under a key-encryption key this unlock does not hold, the second and
    // third under the one it does. It is an ordinary account — a passkey and a
    // card of codes file factors under whatever key each of them derives. Once
    // after the loop, the trial stops at the second entry and the third is never
    // touched. Inside the loop, the gate refuses the second entry, the loop goes
    // on to the third, and the first thing `openFactorKeypair` does with it is
    // read both envelopes.
    it('tries no further entry once one has opened and the manifest has not', async () => {
      // Arrange
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0x94);
      const otherKek = await keyEncryptionKey(0x95);

      const first = await factorFor(otherKek, FIRST_FACTOR_ID, keys);
      const second = await factorFor(kek, SECOND_FACTOR_ID, keys);
      const third = await factorFor(kek, THIRD_FACTOR_ID, keys);

      // The third entry's two envelopes behind accessors that record the read
      // and then answer exactly what the mint produced. It is a real, openable
      // entry — a watched one is not a broken one, or the loop would stop at it
      // for a reason this case did not arrange.
      const read: string[] = [];
      const watched = {
        factorId: third.entry.factorId,
        get wrappedPrivateKey(): string {
          read.push('wrappedPrivateKey');

          return third.entry.wrappedPrivateKey;
        },
        get encapsulatedAccountKeys(): string {
          read.push('encapsulatedAccountKeys');

          return third.entry.encapsulatedAccountKeys;
        },
      };

      const body = await custodyWith(
        keys,
        [first, second, third],
        1,
        keys.indexKey,
      );

      // Swapped in after the body is built, because building it reads nothing
      // off an entry but its `factorId` today and this case must not rest on
      // that staying true.
      api.getAccountKeys.mockReturnValue(
        of({ ...body, factors: [first.entry, second.entry, watched] }),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // **The instrument first, so that a gate moved into the loop is reported
      // by the sentence that says what went wrong** rather than by a word
      // mismatch three cases also produce. The whole of the rule: the second
      // entry opened, and the third was never asked for a byte.
      expect(
        read,
        'the trial loop went on to a third entry after one had already opened',
      ).toEqual([]);

      // `inconsistent`: an entry opened and what it handed over did not open
      // the manifest. A gate inside the loop would answer `unopened` — but that
      // is a word, and a word is one string edit away from agreeing with any
      // arrangement, so it is asserted beside the instrument rather than
      // instead of it.
      expect(custody.unlockFailure()).toBe('inconsistent');
    });
  });

  // **Two of `openFactorManifest`'s refusals happen before any cipher runs, and
  // neither of them is a statement about the account's key material.**
  //
  //   * `decodeBase64Url(wire)` throws on a manifest string that is not unpadded
  //     base64url — padding, the standard alphabet's `+` and `/`, any character
  //     outside `A–Z a–z 0–9 - _`, a length no encoding can have, a final group
  //     no encoder would emit.
  //   * `requireManifestWidth` throws on a sealed value outside the width
  //     window, and that window is **the API edge's too, to the byte at both
  //     ends**: every request carrying a manifest is measured there against this
  //     framing's floor of 29, its version byte and the entity's ceiling of
  //     4096. The stored rule, `length(manifest) between 1 and 4096`, is wider
  //     at the bottom on purpose — "this `bytea` is not empty" is the whole of
  //     what a column can state declaratively about a blob, and 29 is a total
  //     length rather than a byte inside the sealed value, so the floor is
  //     applied where the bytes arrive rather than where they are stored.
  //
  // Both are facts about **the shape of the answer this read brought back**, and
  // this client has nowhere else to observe either: `me-api.service.ts` checks
  // neither on purpose. `isManifestWire` admits every non-empty string and says
  // outright that base64url and width are `openFactorManifest`'s rule, because
  // "a second, weaker copy of it here would be a second definition of what a
  // manifest is". So whatever custody makes of that throw is the whole of what
  // the product makes of it.
  //
  // **Reported as `inconsistent`, a server that moved the manifest's wire form
  // becomes a permanent lockout with advice that can never work.**
  // `inconsistent`'s screen says that no passkey and no recovery code will
  // change it — true of a reversed pair, of an altered byte and of a replayed
  // epoch, and false of a version skew, where the one act that can change the
  // answer is a reload. The word for that already exists and is `unrecognised`:
  // its own definition is a body this client could not read, whose way forward
  // is to **reload this tab**, because a reload is the only act in the product
  // that fetches a different copy of this JavaScript. It is what the identity
  // read's missing `budgetId` and the boundary's refused body already get, and
  // filing these two under `inconsistent` is story 12.13's defect reintroduced
  // one layer down.
  //
  // **The precedent for the split is in this class already.** `openField`
  // re-throws `NarrativeFieldMisuseError` out of its `catch` rather than
  // swallowing a caller's defect into `unreadable`, for exactly this reason: a
  // refusal made about the *call*, before any cipher ran, is not a claim about
  // the value the column holds.
  //
  // **Every case below is behaviour over the service and names nothing of how
  // the split is spelled** — a body whose manifest is malformed in a stated way,
  // an unlock, the word that was published. The reason is written at the
  // `factor-manifest.ts` import at the head of this file: a case reaching for
  // the type such a split would throw pins a spelling rather than a report, and
  // an import that fails to resolve takes every file in the suite down instead
  // of reddening one case.
  //
  // **The refusals come with controls, and the controls are the half that makes
  // the describe mean anything.** A split routing *every* manifest failure to
  // `unrecognised` satisfies each refusal below perfectly; what refuses it is
  // the pair of cases that keep answering `inconsistent` for a manifest that got
  // past the decoder and the width window. Said without a tally, for the reason
  // written above `ROW_ID_SPELLINGS`.
  describe('a manifest this browser could not read at all', () => {
    const utf8 = new TextEncoder();

    // Restored after every case rather than in a `finally` around each act, for
    // the reason the narrative-field describe gives at its own: the acts here
    // are fire-and-forget and a `finally` could not bracket them anyway, so the
    // restore is moved to where it runs whatever the case did. Only the last
    // rows below spy on anything; the restore is cheap and no case may leave
    // `crypto.subtle` stubbed for the ones after it.
    afterEach(() => {
      vi.restoreAllMocks();
    });

    // An account with nothing else wrong with it: one factor, served and
    // openable under the key handed back, a manifest naming exactly it, at an
    // epoch this device has never watched it pass. The manifest's wire form is
    // the only thing left for a case to change, which is what makes the word
    // each case asserts attributable to that change and to nothing else.
    //
    // The content key comes back beside the body because two of the cases seal
    // their own manifest under it. It is imported from a **copy**, for
    // `custodyWith`'s reason: `importAesGcmKey` wipes the material it is handed
    // and the same bytes have to reach the entries.
    async function anOtherwiseImpeccableAccount(seed: number): Promise<{
      served: AccountKeyCustodyDto;
      presented: CryptoKey;
      contentKey: CryptoKey;
      factorId: string;
      publicKey: Uint8Array;
    }> {
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(seed);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const contentKey = await importAesGcmKey(
        Uint8Array.from(keys.contentKey),
      );

      return {
        served: await custodyWith(keys, [factor]),
        presented: kek,
        contentKey,
        factorId: factor.entry.factorId,
        publicKey: factor.publicKey,
      };
    }

    // The manifest the account really answers, or a throw.
    //
    // `manifest` is `string | null` on the wire and a `?? ''` here would quietly
    // turn a case that means to alter one character into a width refusal over
    // zero bytes — the same colour of bar for a different reason.
    function manifestOf(served: AccountKeyCustodyDto): string {
      if (served.manifest === null) {
        throw new Error(
          'The fixture served no manifest, so there was nothing for this case to malform.',
        );
      }

      return served.manifest;
    }

    // A manifest sealed the way `sealFactorManifest` seals one, over a plaintext
    // this file chose.
    //
    // **Needed because no legal writer can produce an illegal plaintext.**
    // `sealFactorManifest` folds a caller's spelling, sorts a caller's order and
    // refuses a repeat, so every plaintext it emits is one the reader accepts —
    // and the refusal the last case is about lives *after* the tag has verified.
    // The only way to reach it is to seal the bytes here.
    //
    // **The associated data is assembled from the constants their owners
    // export, and neither is restated.** The label and the version byte are
    // `factor-keypair.ts`', which is where `factor-manifest.ts` takes them from
    // as well, and the join is `associated-data.ts`'. What is second here is the
    // *assembly*, and the control case is what holds it: a message this file got
    // wrong fails the tag, which would make the refusal case green for a reason
    // that has nothing whatever to do with the grammar.
    async function sealedUnder(
      contentKey: CryptoKey,
      plaintext: Uint8Array,
      rotationEpoch: number,
    ): Promise<string> {
      return encodeBase64Url(
        await sealEnvelope(
          contentKey,
          plaintext,
          manifestAssociatedData(rotationEpoch),
        ),
      );
    }

    // `label ‖ 0x1F ‖ version ‖ 0x1F ‖ rotationEpoch`, decimal digits — the
    // message a manifest is sealed against.
    //
    // **Assembled once and read twice**: by the seal above, and by the cipher
    // spy at the foot of this describe, which has to tell the call that opens
    // the manifest apart from the two that ran before it. Two assemblies would
    // be two chances to get one message wrong, and the halves would disagree
    // without either of them looking wrong.
    function manifestAssociatedData(rotationEpoch: number): Uint8Array {
      return joinFields(
        utf8.encode(FACTOR_KEYPAIR_LABEL),
        Uint8Array.of(FACTOR_KEYPAIR_VERSION),
        utf8.encode(String(rotationEpoch)),
      );
    }

    // `count ‖ 0x1F ‖ factorId ‖ 0x1F ‖ publicKey` — the grammar's own shape for
    // an account holding one factor, with the count supplied so that a case can
    // write one the entries do not agree with. The point goes in raw and is
    // never re-encoded, for the reason the module gives: pushed through UTF-8 a
    // 65-byte point comes out 129 bytes long with no error anywhere.
    function onEntryPlaintext(
      count: string,
      factorId: string,
      publicKey: Uint8Array,
    ): Uint8Array {
      return joinFields(utf8.encode(count), utf8.encode(factorId), publicKey);
    }

    // Nothing was observed and nothing was written. Every refusal below runs
    // before `recordRotationEpochSeen`, and a word that refused a body while
    // filing the epoch beside it would hand whoever shaped that body the oracle
    // the record exists to deny them — one request, and the device's high-water
    // mark is past every epoch the account will ever reach.
    //
    // `localStorage` beside the reading, because the reading cannot see a record
    // filed under some other key, and a record this device cannot read back is
    // one that still denies a legitimate manifest the day the spelling is
    // corrected.
    function expectTheDeviceRememberedNothing(): void {
      expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBeNull();
      expect(localStorage.length).toBe(0);
    }

    // **A wire string the strict decoder refuses.** Neither row is exotic: `+`
    // is what a peer emitting standard base64 sends, and `=` is what one that
    // pads sends — the two shapes of "the other side's encoder changed", which
    // is a version skew and nothing else. A reload is the only act that can
    // change the answer, and it is the only act `unrecognised` asks for.
    //
    // The character is **replaced** rather than appended, so the value keeps the
    // width the account's own manifest had. A case that changed both would be
    // answered by either refusal and could not say which one it drove.
    it.each([
      { how: 'a character outside the URL-safe alphabet', character: '+' },
      { how: 'the padding an unpadded encoding never carries', character: '=' },
    ])(
      'reports a manifest carrying $how as unrecognised',
      async ({ character }) => {
        // Arrange
        const { served, presented } = await anOtherwiseImpeccableAccount(0xc0);
        const manifest = manifestOf(served);
        const at = Math.floor(manifest.length / 2);

        api.getAccountKeys.mockReturnValue(
          of({
            ...served,
            manifest: `${manifest.slice(0, at)}${character}${manifest.slice(at + 1)}`,
          }),
        );

        // Act
        custody.unlock(presented);
        await settled(custody);

        // Assert
        // **`unrecognised`, and the account stays locked.** Nothing about this
        // answer says anything about the factor that was presented or about the
        // keys the account holds — no cipher ran at all — so `inconsistent`'s
        // *nothing you hold will change this* is a sentence about a state this
        // browser never observed, offered to somebody a reload would have let
        // straight in.
        expect(custody.status()).toBe('locked');
        expect(custody.unlockFailure()).toBe('unrecognised');
        expectTheDeviceRememberedNothing();
      },
    );

    it('reports a sealed manifest below the width floor as unrecognised', async () => {
      // Arrange
      // One byte under the floor, which `factor-manifest.ts` states is the AEAD
      // envelope's own floor to the byte — so the number is taken from
      // `key-envelope.ts`, which owns that arithmetic, rather than from the
      // module that restates it. The value is legal base64url, so the decoder
      // hands it over and the width check is the only thing that can refuse it.
      const { served, presented } = await anOtherwiseImpeccableAccount(0xc1);

      api.getAccountKeys.mockReturnValue(
        of({
          ...served,
          manifest: encodeBase64Url(new Uint8Array(MINIMUM_ENVELOPE_BYTES - 1)),
        }),
      );

      // Act
      custody.unlock(presented);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unrecognised');
      expectTheDeviceRememberedNothing();
    });

    it('reports a sealed manifest above the width ceiling as unrecognised', async () => {
      // Arrange
      // One byte over the ceiling the server stores. The number is written out
      // rather than imported, and that is the one literal in this describe: the
      // rule has exactly one owner in `factor-manifest.ts`, and nothing in this
      // file may resolve a symbol from that module beyond the writer it already
      // takes — see the import at the head of the file. The cost is a number
      // that goes stale silently if the ceiling moves; what it buys is that a
      // split which has not been written yet cannot take the suite down.
      const { served, presented } = await anOtherwiseImpeccableAccount(0xc2);

      api.getAccountKeys.mockReturnValue(
        of({ ...served, manifest: encodeBase64Url(new Uint8Array(4097)) }),
      );

      // Act
      custody.unlock(presented);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unrecognised');
      expectTheDeviceRememberedNothing();
    });

    // **The control that decides whether the split is a distinction or a
    // rename, and it is the most important case in this describe.** A gate that
    // answered `unrecognised` for everything `openFactorManifest` throws passes
    // all three refusals above and is refused here — and what it would have done
    // in production is tell somebody holding a genuinely inconsistent account to
    // reload, forever, over a state no reload touches.
    //
    // **Exactly at the floor, and that is a second thing this case holds.** The
    // window is inclusive at both ends: a sealed value of the envelope's own
    // minimum width is a legal manifest that simply did not open. Written `<=`
    // instead of `<`, the width check would answer `unrecognised` here, and this
    // is the only case in the file that would see it.
    //
    // The neighbouring describe already arranges a tag failure — one character
    // of a 310-byte manifest altered — and this one is not a copy of it: that
    // case is about an altered byte reaching the *word*, this one is about which
    // side of the decode-and-width split a tag failure falls on, and a control
    // that lives three describes away from the rule it controls is one somebody
    // deletes as a duplicate.
    it('still answers inconsistent for a manifest at the floor whose tag does not verify', async () => {
      // Arrange
      // A version byte a reader accepts, a zero nonce and a zero tag: legal
      // base64url, exactly the minimum width, and no ciphertext between them.
      // Everything before the cipher passes; the cipher is what refuses it.
      const { served, presented } = await anOtherwiseImpeccableAccount(0xc3);
      const atTheFloor = new Uint8Array(MINIMUM_ENVELOPE_BYTES);
      atTheFloor[0] = ENVELOPE_VERSION;

      api.getAccountKeys.mockReturnValue(
        of({ ...served, manifest: encodeBase64Url(atTheFloor) }),
      );

      // Act
      custody.unlock(presented);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
      expectTheDeviceRememberedNothing();
    });

    // **The hand-sealed control, and the case below it cannot be read without
    // this one.** It proves that a manifest this file sealed itself — under the
    // account's own content key, against an associated-data message this file
    // assembled — is one the service accepts. Without it, a message assembled
    // wrongly here would fail the tag, the case below would report
    // `inconsistent` for that reason instead of the one it names, and the two
    // would be indistinguishable from outside.
    it('unlocks on a hand-sealed manifest whose plaintext the grammar accepts', async () => {
      // Arrange
      const { served, presented, contentKey, factorId, publicKey } =
        await anOtherwiseImpeccableAccount(0xc4);

      api.getAccountKeys.mockReturnValue(
        of({
          ...served,
          manifest: await sealedUnder(
            contentKey,
            onEntryPlaintext('1', factorId, publicKey),
            served.rotationEpoch,
          ),
        }),
      );

      // Act
      custody.unlock(presented);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('unlocked');
      expect(custody.unlockFailure()).toBeNull();
    });

    // **An authenticated plaintext that breaks the grammar is `inconsistent`,
    // and the reasoning is the whole point of the split.** The tag verified, so
    // these bytes were written by somebody holding the account's content key —
    // which is this client, or another client of this product. A grammar refusal
    // over them is therefore this client's own writer being wrong about a format
    // it owns both halves of: a statement about the account's material, not
    // about the wire the answer arrived on. A reload fetches a different bundle
    // and changes nothing about a blob already sealed.
    //
    // The break is a count that disagrees with the entries, which is what a
    // chopped tail looks like — the one direction the count field exists to
    // catch. It differs from the control above by a single character of the
    // plaintext, so nothing about how either was sealed is in play.
    it('still answers inconsistent when an authenticated plaintext breaks the grammar', async () => {
      // Arrange
      const { served, presented, contentKey, factorId, publicKey } =
        await anOtherwiseImpeccableAccount(0xc5);

      api.getAccountKeys.mockReturnValue(
        of({
          ...served,
          manifest: await sealedUnder(
            contentKey,
            onEntryPlaintext('2', factorId, publicKey),
            served.rotationEpoch,
          ),
        }),
      );

      // Act
      custody.unlock(presented);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
      expectTheDeviceRememberedNothing();
    });

    // **The gate tells the two kinds of failure apart by the type, and every
    // case above is satisfied by a gate that matched the label instead.**
    // Measured, and that is why these rows exist: matching
    // `error.name === 'FactorManifestWireError'` passes all six cases above and
    // the whole of this file. The only thing that caught it was a census three
    // files away which forbids the word `name` in the service's source for an
    // entirely unrelated reason — an accident, not a rule about how errors are
    // matched, and one nobody may rely on. A `message.includes(…)` match is the
    // same defect with no accident standing in front of it at all.
    //
    // **Why no case above can see it.** Over every input the product can
    // produce, matching the type and matching the label are the same function:
    // the only errors carrying that name *are* instances, and every other
    // refusal is a bare `Error`. So the difference is invisible until an
    // impostor exists — an error that wears the label and is not the type — and
    // nothing in the ceremony makes one. These rows make one.
    //
    // **The injection is at the platform cipher and nowhere nearer**, which is
    // the same boundary the narrative-field cases force an interleaving at. The
    // service is driven through `unlock` and read through `unlockFailure`;
    // nothing reaches inside it, nothing stubs `factor-manifest.ts`, and no
    // symbol of that module is named here. `openEnvelope` does not catch what
    // `crypto.subtle.decrypt` rejects with and neither does `openFactorManifest`,
    // so the impostor arrives at the gate's own `catch` exactly as thrown.
    //
    // **What the arrangement is, in the product's terms**: a manifest whose tag
    // did not verify, whose failure happens to be labelled like a wire refusal.
    // The right answer is the module's own doctrine and not this file's opinion
    // — these bytes went through the cipher, so whatever they are they are a
    // statement about the account's material, and `inconsistent` is the word.
    // A gate reading the label sends that person to reload, forever.
    //
    // **Stated limits.** The impostor is not a body a browser produces: a real
    // `decrypt` rejects with an `OperationError`, so this is a mutation the
    // rows are built to kill rather than a scenario somebody will meet. And the
    // two sentences are quoted from the module as it throws them today, which
    // is what a message match would be written against — reword the module and
    // these rows keep passing while quietly ceasing to kill anything.
    it.each([
      {
        // The measured one.
        how: 'the wire type’s own name',
        label: 'FactorManifestWireError',
        sentence: 'The cipher would not open these bytes.',
      },
      {
        how: 'the width refusal’s sentence',
        label: 'Error',
        sentence:
          'A sealed factor manifest is between 29 and 4096 bytes, not 3.',
      },
      {
        how: 'the decoder refusal’s sentence',
        label: 'Error',
        sentence:
          'A sealed factor manifest arrives as unpadded base64url, and this value is not.',
      },
      // Read as a list rather than as a tally, for the reason written above
      // `ROW_ID_SPELLINGS`: what holds is that each lie a gate could be fooled
      // by has a row, not that there are three of them.
    ])(
      'still answers inconsistent when a cipher failure wears $how',
      async ({ label, sentence }) => {
        // Arrange
        // The body is served untouched — this is the impeccable account, and
        // the only thing wrong with it is a cipher that will refuse one call.
        const { served, presented } = await anOtherwiseImpeccableAccount(0xc6);
        const impostor = new Error(sentence);

        impostor.name = label;

        // **Matched on the message the call is bound to, never on a call
        // count.** An unlock decrypts twice per entry before it reaches the
        // manifest, so a spy that failed the third call would be pinned to how
        // many factors the fixture serves and would move the day one is added.
        // The manifest's message is the only one in the ceremony whose third
        // field is a decimal epoch; the two before it carry a 36-character
        // factor id there.
        //
        // **A spy watching for the wrong bytes reddens rather than passes**:
        // it would call through on every call, the manifest would open, the
        // account would unlock, and `unlockFailure` would answer `null`.
        const realDecrypt = crypto.subtle.decrypt;
        const boundToTheManifest = manifestAssociatedData(served.rotationEpoch);

        vi.spyOn(crypto.subtle, 'decrypt').mockImplementation(
          (algorithm, key, data) => {
            if (opensTheManifest(algorithm, boundToTheManifest)) {
              return Promise.reject(impostor);
            }

            return realDecrypt.call(crypto.subtle, algorithm, key, data);
          },
        );

        api.getAccountKeys.mockReturnValue(of(served));

        // Act
        custody.unlock(presented);
        await settled(custody);

        // Assert
        // `inconsistent`, and **not** `unopened`: a factor did open, which is
        // what makes the word attributable to the gate and to nothing else.
        expect(custody.status()).toBe('locked');
        expect(custody.unlockFailure()).toBe('inconsistent');
        expectTheDeviceRememberedNothing();
      },
    );

    // Whether this cipher call is the one that opens the manifest.
    //
    // `algorithm` is `unknown` because the platform's own parameter is a union
    // that includes a bare string, so there is no member to read until it has
    // been narrowed — and narrowing it here is cheaper than writing that union
    // out, which would be a copy of a lib declaration kept true by nobody.
    function opensTheManifest(
      algorithm: unknown,
      associatedData: Uint8Array,
    ): boolean {
      if (
        typeof algorithm !== 'object' ||
        algorithm === null ||
        !('additionalData' in algorithm)
      ) {
        return false;
      }

      const { additionalData } = algorithm;

      return (
        ArrayBuffer.isView(additionalData) &&
        sameBytes(bytesOf(additionalData), associatedData)
      );
    }

    // A view's bytes, without copying them. The envelope hands the cipher a
    // `Uint8Array` over its own buffer, and this reads that same memory back
    // rather than asserting anything about which kind of view arrived.
    function bytesOf(view: ArrayBufferView): Uint8Array {
      return new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
    }

    // Byte equality, width first. `toEqual` is not available here — this runs
    // inside a stub rather than inside an assertion — and a comparison that
    // only walked the shorter of the two would call a prefix a match.
    function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
      return (
        left.length === right.length &&
        left.every((byte, at) => byte === right[at])
      );
    }
  });

  // **The manifest's set and the served set are compared, in both directions,
  // and a disagreement is the account's material disagreeing with itself.**
  //
  // Opening the manifest proves the content key is the content key. It proves
  // nothing at all about *who else* can open this account, and that is the half
  // the comparison adds: the manifest is one authenticated blob per account,
  // sealed under a key this server has never held, so the set it names is the
  // only unforgeable statement of which factors exist. The rows beside it are
  // not — an operator who can write the database can add a row of their own,
  // wrapped under a key-encryption key they chose, and every envelope in it is
  // correctly framed and opens perfectly under it.
  //
  // So the refusals run both ways and neither direction is the other's mirror:
  //
  //   * a **served factor the manifest does not name** is the row somebody
  //     added. Checked in one direction only — "every factor the manifest names
  //     was served" — it passes, because the attacker's row is an addition and
  //     nothing the manifest names has gone missing.
  //   * a **manifest naming a factor that was not served** is a row somebody
  //     removed, or a set this client is being shown half of. Checked the other
  //     way only — "every factor served is named" — it passes for the same
  //     reason in reverse.
  //
  // `inconsistent` for both, and for the reason the whole word exists: the
  // remedy is not another factor, because every factor of this account
  // encapsulates the same two keys and none of them changes what the server is
  // willing to serve.
  describe("the served factor set is compared against the manifest's", () => {
    // A body whose manifest names one set and whose response serves another.
    //
    // `custodyWith` seals over the factors it is handed and serves those same
    // ones, which is the agreement case — so the served list is replaced
    // afterwards rather than that helper growing a fifth parameter no case but
    // these three would pass.
    async function bodyNaming(
      keys: AccountKeys,
      named: readonly { entry: AccountKeyEntry; publicKey: Uint8Array }[],
      served: readonly { entry: AccountKeyEntry; publicKey: Uint8Array }[],
      rotationEpoch = 1,
    ): Promise<AccountKeyCustodyDto> {
      const body = await custodyWith(keys, named, rotationEpoch);

      return { ...body, factors: served.map(({ entry }) => entry) };
    }

    it('refuses a served factor the manifest does not name', async () => {
      // Arrange
      // The shape of an added row: the account's own factor, named by the
      // manifest and openable by the person presenting it, beside a second row
      // the manifest says nothing about. The intruder's row is minted under a
      // key-encryption key of its own, which is what an operator enrolling
      // themselves would have — it is a perfectly well formed factor, and that
      // is the point.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa1);
      const intruderKek = await keyEncryptionKey(0xa2);

      const mine = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const intruder = await factorFor(intruderKek, SECOND_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await bodyNaming(keys, [mine], [mine, intruder])),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // The factor presented opened, and the manifest opened under what it
      // handed over — so neither `unopened` nor a failure of the gate's first
      // half is available here, and only the comparison can reach this word.
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    it('refuses a manifest naming a factor the server did not serve', async () => {
      // Arrange
      // The other direction, and it is a different event: a row removed, or a
      // response carrying half the set. A person whose recovery card is quietly
      // gone from the response would otherwise unlock, see nothing, and find out
      // the day they needed a code.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa3);
      const otherKek = await keyEncryptionKey(0xa4);

      const mine = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const withheld = await factorFor(otherKek, SECOND_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await bodyNaming(keys, [mine, withheld], [mine])),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    // **The substitution, and it is the one arrangement in this file that a
    // count cannot pass.**
    //
    // Measured: replacing the two `every`s with `declaredIds.size ===
    // servedIds.size` leaves every other case in this file green — deliberately
    // said without a count, for the reason stated forty lines above
    // `ROW_ID_SPELLINGS`: a number written into a sentence goes stale the first
    // time a case is added or dropped and nothing reddens about it.
    // The two cases above are the reason — one is an **addition** and the
    // other a **removal**, so each changes the size of the served set, and a
    // cardinality check refuses both by accident, for a reason that has nothing
    // to do with what either case is about.
    //
    // A swap is what the threat model actually describes, and it is the whole of
    // the story's sentence: *an operator who can write the database still cannot
    // add a factor that opens my account.* Adding a row is not what such an
    // operator has to do. They write a row for a factor they control **and**
    // drop one of the account's own, and the response then carries exactly as
    // many factors as the manifest names — `{A, B, EVIL}` against a manifest
    // naming `{A, B, C}`. Every other check in this gate passes: the factor
    // presented opens, the manifest opens under what it handed over, the epoch
    // is not below anything this device has watched. A count passes too, and the
    // account unlocks for an authenticator its owner never enrolled.
    //
    // Set equality refuses it twice over, and either direction on its own would
    // be enough: the intruder is served and not named, and the dropped factor is
    // named and not served. That is not redundancy here — it is the same pair of
    // events the two cases above are each about, arriving together.
    it('refuses a served factor substituted for one the manifest names', async () => {
      // Arrange
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xb3);
      const otherKek = await keyEncryptionKey(0xb4);
      const intruderKek = await keyEncryptionKey(0xb5);

      // The account's own three: the factor being presented, a second the person
      // also holds, and the one the operator takes out of the response.
      const mine = await factorFor(kek, FIRST_FACTOR_ID, keys);
      const alsoMine = await factorFor(otherKek, SECOND_FACTOR_ID, keys);
      const dropped = await factorFor(otherKek, THIRD_FACTOR_ID, keys);
      // A perfectly well formed factor wrapped under a key-encryption key the
      // operator chose, which is what enrolling themselves gives them. Nothing
      // about the row is malformed, and that is the point.
      const intruder = await factorFor(intruderKek, FOURTH_FACTOR_ID, keys);

      const named = [mine, alsoMine, dropped];
      const served = [mine, alsoMine, intruder];

      // **The arrangement's own two guards, and the first is the reason this
      // case exists.** Equal sizes are what makes a cardinality check blind
      // here; unequal sets are what makes set equality able to see it. Without
      // the pair, a fixture that drifted into one of the two cases above would
      // go on passing while pinning nothing new.
      expect(
        served,
        'the two sets are different sizes, so a cardinality check would refuse this response and the case pins nothing a count does not already catch.',
      ).toHaveLength(named.length);
      expect(
        new Set(served.map(({ entry }) => entry.factorId)),
        'the served set is the set the manifest names, so there is no substitution to refuse.',
      ).not.toEqual(new Set(named.map(({ entry }) => entry.factorId)));

      api.getAccountKeys.mockReturnValue(
        of(await bodyNaming(keys, named, served)),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // The factor presented opened and the manifest opened under what it handed
      // over, so neither `unopened` nor the gate's first half is available — only
      // the comparison can reach this word.
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');
    });

    // **Set equality, and the order either set arrived in is not part of it.**
    //
    // The manifest's own order is not a free variable: the grammar names the
    // factors ascending by the canonical spelling of the identifier, and
    // `sealFactorManifest` sorts whatever it is handed — a manifest in any other
    // order does not open at all. So the order that *can* vary is the response's,
    // and it varies for ordinary reasons: the server has no order to promise
    // here, and a row inserted by a later enrolment arrives wherever the query
    // put it.
    //
    // A comparison written as two lists walked in step is therefore green on
    // every account that happens to be served ascending — which is most of
    // them — and refuses the rest with a word that tells the person nothing
    // they hold can help.
    it.each([
      { how: 'in the order the manifest names them', reversed: false },
      { how: 'in the opposite order', reversed: true },
    ])(
      'unlocks when the two sets are equal, served $how',
      async ({ reversed }) => {
        // Arrange
        const keys = generateAccountKeys();
        const kek = await keyEncryptionKey(0xa5);
        const otherKek = await keyEncryptionKey(0xa6);

        const first = await factorFor(kek, FIRST_FACTOR_ID, keys);
        const second = await factorFor(otherKek, SECOND_FACTOR_ID, keys);

        // The order the manifest will name them in, derived by the rule the
        // grammar states rather than assumed off these two constants.
        const ascending = [first, second].sort((left, right) =>
          left.entry.factorId < right.entry.factorId ? -1 : 1,
        );
        const served = reversed ? [...ascending].reverse() : ascending;

        // The guard that keeps the reversed arrangement honest: with equal ids
        // there would be no second order to serve.
        expect(first.entry.factorId).not.toBe(second.entry.factorId);

        // The seal is handed the factors jumbled on purpose — it sorts, so both
        // rows below name the same manifest and the only thing that differs
        // between them is the response.
        api.getAccountKeys.mockReturnValue(
          of(await bodyNaming(keys, [second, first], served)),
        );

        // Act
        custody.unlock(kek);
        await settled(custody);

        // Assert
        expect(custody.status()).toBe('unlocked');
        expect(custody.unlockFailure()).toBeNull();
      },
    );
  });

  // **What this device has already watched this account pass, and the one
  // attack it answers.**
  //
  // An operator who can write the database serves back an old `(manifest,
  // epoch)` pair the account genuinely was in once. It is correctly sealed,
  // correctly framed, opens under the content key and names a factor set that
  // really was this account's — because it is not fake, it is simply from
  // before. Every check above passes on it. Replayed, it reinstates a factor the
  // person revoked: the manifest names it, the row is served, the two sets
  // agree, and the browser goes on treating a retired authenticator as one of
  // the account's own.
  //
  // Nothing in the cryptography can see that, and no value carried by the
  // response can either — whoever can replay the manifest can replay the epoch
  // beside it, and the pair verifies exactly as it did the day it was written.
  // The only party able to tell is one that watched the account move past it,
  // and on a browser that party is a device. Hence a per-device high-water mark,
  // and hence ASM-016: a device that has never seen the account cannot detect a
  // rollback at all. This narrows the window; it does not close it.
  describe('the rotation epoch this device has already seen', () => {
    it('records the highest epoch it has seen for this account', async () => {
      // Arrange
      // Seven rather than one, so that a record written from a constant, from
      // the manifest's floor or from the count of anything is a different number
      // from the one asserted.
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa7);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(keys, [factor], 7)),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // The unlock succeeded first, or "the record is 7" would be a claim about
      // an attempt that never reached the keys.
      expect(custody.status()).toBe('unlocked');
      expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBe(7);
    });

    it('refuses a manifest at an epoch below the one recorded', async () => {
      // Arrange
      // **The whole point of the memory.** The body is impeccable: the manifest
      // opens, it was sealed at the epoch it is served beside, and it names
      // exactly the factor that was served. It is simply from before — and this
      // device watched the account reach 9.
      recordRotationEpochSeen(SESSION_BUDGET_ID, 9);

      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa8);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(keys, [factor], 3)),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('inconsistent');

      // And the record did not move. A refusal that also wrote what it refused
      // would be a defence that disarms itself on the second attempt.
      expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBe(9);
    });

    it.each([
      { why: 'the epoch it recorded', recorded: 5, served: 5 },
      { why: 'an epoch above it', recorded: 5, served: 9 },
    ])('accepts a manifest at $why', async ({ recorded, served }) => {
      // Arrange
      // **Not-lower, never strictly-higher.** An account that has not rotated
      // answers the same epoch on every unlock, and a browser refusing the epoch
      // it recorded a minute ago would lock every returning visitor out of an
      // account nothing has happened to. The upper row is the ordinary case of a
      // rotation this device has not seen yet.
      recordRotationEpochSeen(SESSION_BUDGET_ID, recorded);

      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xa9);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(keys, [factor], served)),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('unlocked');
      expect(custody.unlockFailure()).toBeNull();
    });

    // **Nothing is recorded from an attempt that did not reach the keys, and
    // this is the only case in the file that catches it.**
    //
    // An implementation that writes the record from `custody.rotationEpoch` as
    // soon as the body arrives — before a factor has opened, before the manifest
    // has opened, before the sets have been compared — passes every case above
    // in every arrangement. What it has built is an oracle: an operator serving
    // `rotationEpoch: 9999` beside anything at all pushes this device's
    // high-water mark past every epoch the account will ever reach, and the
    // browser then refuses the account's own genuine manifest forever, with copy
    // that offers the person nothing to do. One request, and that browser is
    // finished with that account.
    //
    // So the write belongs after the last refusal and nowhere else, and the
    // epoch a refused attempt carried is never an observation.
    it.each([
      {
        // **The epoch is 7 and not the 0 `custodyOf` gives, and that is what
        // makes this row pin custody rather than the store.**
        //
        // A manifest-less body served beside `rotationEpoch: 0` is refused by
        // the record's own floor — 0 is the server's "no manifest row" sentinel
        // and the store will not keep it — so a custody that wrote the epoch
        // before the gate would have been turned away by the store, this row
        // would have stayed green, and the rule it is named for would have been
        // held by a module it is not about. Measured: it was the only one of the
        // four that a record moved above the gate did not redden.
        //
        // Seven is a legitimate arrangement rather than a contrivance. A `null`
        // manifest arriving here is not an old account — not because the server
        // withholds one, which it does not, but because registration files the
        // first manifest at epoch 1 in the same save as the account and every
        // path that moves a factor set carries one — so it is a response
        // somebody shaped, and
        // somebody shaping a response chooses the epoch beside it. Seven beside
        // a `null` manifest is exactly the oracle this row is about: one request,
        // and the device's high-water mark is past every epoch the account will
        // ever reach.
        why: 'a response carrying no manifest',
        arrange: async (): Promise<{
          served: AccountKeyCustodyDto;
          presented: CryptoKey;
        }> => {
          const keys = generateAccountKeys();
          const kek = await keyEncryptionKey(0xaa);

          return {
            served: {
              ...custodyOf([await entryFor(kek, FIRST_FACTOR_ID, keys)]),
              rotationEpoch: 7,
            },
            presented: kek,
          };
        },
      },
      {
        why: 'a manifest that did not open',
        arrange: async (): Promise<{
          served: AccountKeyCustodyDto;
          presented: CryptoKey;
        }> => {
          const keys = generateAccountKeys();
          const kek = await keyEncryptionKey(0xab);
          const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

          return {
            // Sealed under the account's index key, which is what a client that
            // encapsulated the two halves the wrong way round looks like from
            // here.
            served: await custodyWith(keys, [factor], 7, keys.indexKey),
            presented: kek,
          };
        },
      },
      {
        why: 'a factor set that disagreed with the manifest',
        arrange: async (): Promise<{
          served: AccountKeyCustodyDto;
          presented: CryptoKey;
        }> => {
          const keys = generateAccountKeys();
          const kek = await keyEncryptionKey(0xac);
          const intruderKek = await keyEncryptionKey(0xad);
          const mine = await factorFor(kek, FIRST_FACTOR_ID, keys);
          const intruder = await factorFor(intruderKek, SECOND_FACTOR_ID, keys);
          const body = await custodyWith(keys, [mine], 7);

          return {
            served: { ...body, factors: [mine.entry, intruder.entry] },
            presented: kek,
          };
        },
      },
      {
        // **The row that cannot be strengthened, and the weakness is stated
        // rather than papered over with an assertion that would look like one.**
        //
        // This attempt returns at `unopened`, inside the trial loop's verdict and
        // *before* the gate exists to be moved past — so a record written
        // anywhere from the gate downwards is unreachable on this branch and
        // this row is green whichever of those placements is chosen. What it
        // does still catch is the earliest placement, the one the paragraph
        // above is about: a record written as soon as the body arrives, before
        // any factor has been tried. That is a real mutant and this row reddens
        // on it — it is simply not the *only* row that does.
        //
        // The honest alternative would be to assert that the record was not
        // *called*, which is a claim about how the method is written rather than
        // about what the device remembered, and the file would then redden on a
        // refactor that changed nothing anybody can observe. A stated weakness
        // is worth more than that.
        why: 'a factor set none of which opened',
        arrange: async (): Promise<{
          served: AccountKeyCustodyDto;
          presented: CryptoKey;
        }> => {
          const keys = generateAccountKeys();
          const kek = await keyEncryptionKey(0xae);
          const presented = await keyEncryptionKey(0xaf);
          const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

          return {
            served: await custodyWith(keys, [factor], 7),
            presented,
          };
        },
      },
    ])(
      'records nothing from an attempt refused over $why',
      async ({ arrange }) => {
        // Arrange
        const { served, presented } = await arrange();

        api.getAccountKeys.mockReturnValue(of(served));

        // Act
        custody.unlock(presented);
        await settled(custody);

        // Assert
        // The attempt really did fail, or every assertion below holds over a
        // service that unlocked and recorded nothing.
        expect(custody.status()).toBe('locked');
        expect(custody.unlockFailure()).not.toBeNull();

        expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBeNull();

        // And nothing was written anywhere else either. The reading above cannot
        // see a record filed under some other key — a factor id, a shared key, the
        // epoch alone — and a record this device cannot read back is one that
        // still permanently denies a legitimate manifest the day the key spelling
        // is corrected.
        expect(localStorage.length).toBe(0);
      },
    );

    it("keeps one account's record out of another's", async () => {
      // Arrange
      // Two accounts on one device, which is an ordinary thing: a person with a
      // second budget, or two people sharing a browser. A high-water mark kept
      // in one variable rather than one key per account refuses the second
      // account's perfectly genuine manifest because the first has rotated
      // further — and there is nothing the person can do about it in either
      // account.
      const mine = generateAccountKeys();
      const kek = await keyEncryptionKey(0xb0);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, mine);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(mine, [factor], 9)),
      );

      custody.unlock(kek);
      await settled(custody);
      // The first unlock really did happen and really was recorded, or the
      // assertions below hold over a device that has seen nothing at all.
      expect(custody.status()).toBe('unlocked');
      expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBe(9);

      // The second account, whose identity read answers a different budget.
      const theirs = generateAccountKeys();
      const otherKek = await keyEncryptionKey(0xb1);
      const otherFactor = await factorFor(otherKek, SECOND_FACTOR_ID, theirs);

      api.getSessionOwner.mockReturnValue(
        of({ ...SESSION_OWNER, budgetId: OTHER_BUDGET_ID }),
      );
      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(theirs, [otherFactor], 2)),
      );

      // Act
      custody.unlock(otherKek);
      await settled(custody);

      // Assert
      // Epoch 2 is below the 9 this device watched the *other* account reach, so
      // a single shared high-water mark refuses this unlock outright.
      expect(custody.status()).toBe('unlocked');
      expect(custody.unlockFailure()).toBeNull();

      // Both records, both ways: a shared key answers 2 for the first account,
      // and a record that ignored the account it was filed under answers 9 for
      // the second.
      expect(highestRotationEpochSeen(SESSION_BUDGET_ID)).toBe(9);
      expect(highestRotationEpochSeen(OTHER_BUDGET_ID)).toBe(2);
    });
  });

  // **The account the record is filed under is read here, from the API, and the
  // read is part of the unlock rather than beside it.**
  //
  // The epoch record is per account, and the only per-account identifier a
  // browser ever holds is the budget this session is scoped by. It cannot be
  // derived, it is in no other response this path reads, and — the reason this
  // read exists at all — it is `null` on the sign-in path at the moment
  // `unlock()` is called: `SessionService.established()` starts its own read
  // without awaiting it, and sign-in unlocks on the line after. Borrowing the
  // signal would file the first unlock of every session under nothing.
  //
  // So an unlock that cannot learn which account it is opening cannot record
  // anything, and an unlock that records nothing cannot refuse a replay — which
  // makes the identity read a step of the gate and not an optimisation. It
  // refuses rather than proceeding, and the words are the ones `failureOf`
  // already gives the other read: what was observed is a read that did not
  // arrive, or an answer this client could not read.
  describe('the account the epoch record is filed under', () => {
    // A body nothing else in the attempt can refuse, so that the identity read
    // is the only thing left that can decide these cases.
    async function anImpeccableAccount(): Promise<CryptoKey> {
      const keys = generateAccountKeys();
      const kek = await keyEncryptionKey(0xb2);
      const factor = await factorFor(kek, FIRST_FACTOR_ID, keys);

      api.getAccountKeys.mockReturnValue(
        of(await custodyWith(keys, [factor], 4)),
      );

      return kek;
    }

    it('stays locked and says unreachable when the identity read never answered', async () => {
      // Arrange
      // Status `0` — the request never reached a server. The envelopes are fine,
      // the factor is fine and the manifest is fine; what is missing is the one
      // thing that says which account's memory this is. **Never `unopened`**,
      // which would send somebody hunting for a recovery card over a network
      // that blinked, and never a silent unlock, which is this defence switched
      // off by a dropped request.
      const kek = await anImpeccableAccount();

      api.getSessionOwner.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 0 })),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unreachable');

      // Nothing was recorded, because nothing knew what to record it against.
      expect(localStorage.length).toBe(0);

      // And still nothing on `SessionService`. A read of this route that did not
      // arrive says nothing whatever about who is asking.
      expect(session.ended).not.toHaveBeenCalled();
      expect(session.established).not.toHaveBeenCalled();
    });

    // **A refused identity read is `unauthenticated`, and until this case that
    // mapping held only by construction.**
    //
    // The two reads share one `try` and one `catch`, so today every word
    // `failureOf` gives is true of both of them without anything asserting it.
    // That is the property the sharing exists for — and it is one refactor from
    // gone: a second `catch` written over the identity read, for a message or a
    // log line, satisfies this whole file while answering a refused identity
    // read with whatever word that catch happens to pick. `unreachable` is the
    // one it would most plausibly pick, and it is false by its own definition —
    // the server was reached and it answered — while the advice it carries, *try
    // again in a minute*, sends somebody round a loop that ends the same way
    // every time. Signing in again is the only thing that changes a 401.
    //
    // **Both statuses, because they are two different events that must land on
    // one word.** A 401 is a session that is over; the 403 is what a locked
    // session and the `X-Budgetoid-Client` control give, on a cookie that is
    // perfectly alive. Neither is a fact about the factor the person presented
    // and neither is a network that blinked.
    it.each([
      { why: 'the session is over', status: 401 },
      { why: 'the request was refused outright', status: 403 },
    ])(
      'stays locked and says unauthenticated when $why',
      async ({ status }) => {
        // Arrange
        // An account nothing else in the attempt can refuse, so the identity
        // read is the only thing left that can decide this case.
        const kek = await anImpeccableAccount();

        api.getSessionOwner.mockReturnValue(
          throwError(() => new HttpErrorResponse({ status })),
        );

        // Act
        custody.unlock(kek);
        await settled(custody);

        // Assert
        expect(custody.status()).toBe('locked');
        expect(custody.unlockFailure()).toBe('unauthenticated');

        // Nothing was recorded, because nothing knew what to record it against.
        expect(localStorage.length).toBe(0);

        // **And still nothing on `SessionService`, which is the half a reader
        // will want to "finish".** This is the one refusal in the file that
        // really does mean the person has to sign in again, so it is the one
        // place where publishing `anonymous` from here looks correct. It is not:
        // only the interceptor and the sign-out path may move that status, and a
        // custody that moved it would be deciding, from one read, that somebody
        // is out of an account they may well still be inside.
        expect(session.ended).not.toHaveBeenCalled();
        expect(session.established).not.toHaveBeenCalled();
      },
    );

    // **An answer carrying no budget is `unrecognised`, and it is the word for
    // exactly what was observed**: the server answered, and this client could
    // not read the answer. `unreachable` would be false — the server was reached
    // — and its advice, *try again in a minute*, cannot help, because the next
    // minute runs this bundle against that route and gets the same body.
    //
    // The three bodies are the three shapes a skew produces, and `SessionService`
    // reads all three the same way one layer up: a missing member, a member of
    // the wrong type, and the empty string, which is the absence of an answer
    // wearing the type of one.
    it.each([
      { why: 'a body naming no budget', body: {} },
      { why: 'a budget that is not a spelling at all', body: { budgetId: 42 } },
      { why: 'an empty budget', body: { budgetId: '' } },
    ])('stays locked and says unrecognised for $why', async ({ body }) => {
      // Arrange
      const kek = await anImpeccableAccount();

      api.getSessionOwner.mockReturnValue(
        of({ email: SESSION_OWNER.email, ...body } as MeDto),
      );

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      // **Refused and not proceeded with.** An unlock that shrugged and skipped
      // the record here would hand an operator a way to switch the rollback
      // refusal off for every browser at once, by dropping one member from a
      // response nothing else in this path reads.
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unrecognised');
      expect(localStorage.length).toBe(0);
    });

    it('asks the API who this is, and asks SessionService nothing', async () => {
      // Arrange
      // **The edge that must not close, and the identity read is what makes it
      // tempting.** `SessionService` already holds the budget as a signal, four
      // characters away — and it injects this class, so custody reaching back
      // puts the two in a cycle and leaves the rule this whole class is built on
      // (a key that will not open is not a session that ended) one call away
      // from being undone by somebody reusing what was already there.
      //
      // The counter is what makes the assertion a census rather than a guess
      // about which member somebody would have reached for: `budgetId` is read
      // by a call, exactly as `ended` and `established` are.
      const kek = await anImpeccableAccount();

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('unlocked');

      // The positive half: it did ask, and it asked the route whose 401 is this
      // call's own answer rather than a session ending.
      expect(api.getSessionOwner).toHaveBeenCalled();

      // And the negative half, every member of it.
      expect(session.budgetId).not.toHaveBeenCalled();
      expect(session.ended).not.toHaveBeenCalled();
      expect(session.established).not.toHaveBeenCalled();
    });
  });

  it('returns nothing at all from unlock, and nothing anybody can await', async () => {
    // Arrange
    api.getAccountKeys.mockReturnValue(of(custodyOf([])));
    const kek = await keyEncryptionKey(0x7a);

    // Act
    const returned = custody.unlock(kek);

    // Assert
    // **`void`, and it is enforcement rather than a signature that happens to be
    // convenient.** A `Promise<void>` is awaitable, and every caller has an
    // obvious place to put the `await` — each arrives here straight out of a
    // ceremony an authenticator has just agreed to, with a screen to move on to.
    // One refactor later that `await` grows a `catch`, and a key that did not
    // open becomes a ceremony that failed: on the way into the account an
    // authentication failure, which must never happen because only `anonymous`
    // may bounce anybody out of an account, and on a screen inside the account a
    // device blamed for something it did not do.
    //
    // Both readings, because they fail on different widenings. `undefined` is
    // false for a `Promise`; the second is false for anything thenable at all,
    // including a hand-rolled object with a `then` that a `Promise` type
    // annotation was never put on.
    expect(returned).toBeUndefined();
    expect((returned as { then?: unknown } | undefined)?.then).toBeUndefined();

    await settled(custody);
  });

  it('keeps the keys behind fields the language hides, not the compiler', async () => {
    // Arrange
    // Adopted rather than unlocked, so the fields are known to be holding
    // something at the moment they are read: against `#contentKey: CryptoKey |
    // null = null`, a bracket read answers `undefined` whichever kind of private
    // the field is, and a class that had never held a key would pass this on
    // either spelling.
    const contentKey = await keyEncryptionKey(0x7b);
    const indexKey = await keyEncryptionKey(0x7c);

    // Act
    custody.adopt(contentKey, indexKey);

    // Assert
    expect(custody.status()).toBe('unlocked');

    // **The one assertion that tells `#` from TypeScript's `private`.** `private`
    // is a compile-time annotation and nothing else: it is erased on the way out,
    // so this exact expression reads the field at runtime with the compiler's
    // blessing — and so does any devtools panel, any `JSON.stringify` of the
    // instance and any structured clone of it. `#contentKey` is unreachable from
    // outside the class body by the language, not by review and not by anybody's
    // discipline. Swap the spelling and every other case in this file stays
    // green.
    expect((custody as never)['contentKey']).toBeUndefined();
    expect((custody as never)['indexKey']).toBeUndefined();

    // Two more readings of the same property, because the first is about a
    // *name* and these are about the object: nothing enumerable on the instance
    // carries a key, so neither a serializer nor a structured clone can carry one
    // out of the tab.
    expect(Object.keys(custody)).not.toContain('contentKey');
    expect(JSON.stringify(custody)).not.toContain('CryptoKey');
  });

  it('takes custody of keys a caller already holds, with no round trip', async () => {
    // Arrange
    // Registration is the case and for now the only one: it draws the account's
    // keys itself, so asking the server to hand back envelopes it has only just
    // written — to open them under a key-encryption key it has only just derived
    // — would be a round trip whose whole purpose is to arrive back where it
    // started.
    const contentKey = await keyEncryptionKey(0x7d);
    const indexKey = await keyEncryptionKey(0x7e);

    // Act
    custody.adopt(contentKey, indexKey);

    // Assert
    expect(custody.status()).toBe('unlocked');
    expect(custody.unlockFailure()).toBeNull();

    // And it asked nobody anything. A path that read the route "for consistency"
    // would work perfectly and cost a registration one more request that can
    // fail at the happiest moment of the flow.
    expect(api.getAccountKeys).not.toHaveBeenCalled();
  });

  it('drops what an attempt opened when the world moved while the cipher ran', async () => {
    // Arrange
    // Signing out with an unlock in flight. Without the generation counter the
    // account is unlocked again a few hundred milliseconds after the person left
    // it, by a promise nobody is holding — and nothing on screen would say so.
    //
    // The read is a `Subject` rather than an `of`, so the attempt is genuinely in
    // flight when `lock()` lands rather than merely early in a microtask queue.
    // The entry is one that **opens**: an entry that failed would leave the
    // service locked for the wrong reason and this case would pass on a module
    // with no counter in it at all.
    //
    // The body carries a manifest, and here that is load-bearing rather than
    // cosmetic: over a manifest-less one this case would stay green while the
    // generation counter was gone, because the gate would refuse the attempt
    // before the counter was ever consulted. What must be dropped is a pair that
    // really did open an account whose material is entirely in order.
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x7f);
    const answer = new Subject<AccountKeyCustodyDto>();
    const body = await custodyWith(keys, [
      await factorFor(kek, FIRST_FACTOR_ID, keys),
    ]);

    api.getAccountKeys.mockReturnValue(answer);

    // Act
    custody.unlock(kek);
    expect(custody.status()).toBe('unlocking');

    custody.lock();
    expect(custody.status()).toBe('locked');

    answer.next(body);
    answer.complete();

    // Long enough for the read, both opens and both imports to have finished —
    // `settled` cannot be used here, because the status left `'unlocking'` at the
    // `lock()` above and it would return before the attempt had run at all.
    await flush(10);

    // Assert
    // The attempt opened the entry and then found the world moved, so it dropped
    // what it held instead of publishing it. The status is the one `lock()` set
    // and the failure is null, because an attempt the world has moved past says
    // nothing about the state it moved to — publishing `unopened` here would
    // blame a factor that worked.
    expect(custody.status()).toBe('locked');
    expect(custody.unlockFailure()).toBeNull();
  });

  it('leaves no live copy of the unwrapped bytes on the way to the keys', async () => {
    // Arrange
    // **The bytes die inside the imports, in the statement that produces the
    // keys**, and nothing in this class ever holds a `Uint8Array`. There is no
    // way to observe that from outside the service — no member returns a key, let
    // alone material — so the buffers are reached at the platform boundary, by
    // spying on `crypto.subtle.importKey` and calling through. Faking it would
    // make every assertion below a statement about the fake: the buffer being
    // read has to be one a real import really consumed.
    //
    // Both doors are watched, because the unwrap produces both keys and a wipe
    // dropped from either leaves half of the account's material on the heap for
    // the life of the tab. They are told apart the way the doors themselves are:
    // the content key's algorithm is the string `'AES-GCM'` and the index key's
    // is an object naming `'HMAC'`. `'HKDF'` imports are ignored for the reason
    // `account-keys.spec.ts` gives — a branch on a call index would silently move
    // the moment a derivation was added anywhere below.
    //
    // The fixtures carry a manifest and are built **before** the spy below is
    // installed, so the seal's own import is outside the census. Opening a
    // manifest imports nothing — it is one `decrypt` under a key custody is
    // already holding — so the three buffers counted below are the same three
    // they were while this body carried none.
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x80);
    const body = await custodyWith(keys, [
      await factorFor(kek, FIRST_FACTOR_ID, keys),
    ]);

    api.getAccountKeys.mockReturnValue(of(body));

    const realImportKey = crypto.subtle.importKey;
    const live: Uint8Array[] = [];
    const atImportTime: Uint8Array[] = [];

    const importer = vi
      .spyOn(crypto.subtle, 'importKey')
      .mockImplementation(
        (format, keyData, algorithm, extractable, keyUsages) => {
          const name =
            typeof algorithm === 'string' ? algorithm : algorithm.name;

          if (name === 'AES-GCM' || name === 'HMAC') {
            const bytes = ArrayBuffer.isView(keyData)
              ? new Uint8Array(
                  keyData.buffer,
                  keyData.byteOffset,
                  keyData.byteLength,
                )
              : new Uint8Array(keyData);

            live.push(bytes);
            atImportTime.push(Uint8Array.from(bytes));
          }

          return realImportKey.call(
            crypto.subtle,
            format,
            keyData,
            algorithm,
            extractable,
            keyUsages,
          );
        },
      );

    // Act
    try {
      custody.unlock(kek);
      await settled(custody);
    } finally {
      // Restored before the assertions, so a failure below does not leave
      // `crypto.subtle.importKey` spied for every test after this one.
      importer.mockRestore();
    }

    // Assert
    expect(custody.status()).toBe('unlocked');

    // **Three, and each of them is named.** Opening one entry runs the whole
    // keypair path, and the first of the three is that path's: the key the ECDH
    // agreement derives goes through the same AES-GCM door the content key does,
    // so it is in this census whether or not this rule is about it — and it
    // belongs here, because it is one more buffer holding material that has to
    // die on the way past. The other two are the account's content key through
    // that door and its index key through the HMAC one. A fourth would mean a
    // key was made somewhere this file is not looking.
    //
    // ECDH and HKDF imports are outside the filter above, for the reason
    // `account-keys.spec.ts` gives: neither carries an account key, and
    // branching on a call index would silently move the moment a derivation was
    // added anywhere below.
    expect(atImportTime).toHaveLength(3);

    // Every buffer held something at the moment it was handed over. Without this
    // reading, "all zeros afterwards" is a property of a buffer that never held
    // anything, and an implementation that imported thirty-two zeros would pass
    // the half below.
    for (const snapshot of atImportTime) {
      expect(snapshot).toHaveLength(ACCOUNT_KEY_BYTES);
      expect(
        Array.from(snapshot).filter((byte) => byte === 0),
      ).not.toHaveLength(ACCOUNT_KEY_BYTES);
    }

    // And every one of them is zeroes now. These are the account's content key
    // and index key in the clear — the values that decrypt every column it ever
    // wrote and key its whole search space — on buffers nothing outside the door
    // names, which is why nothing outside the door could ever wipe them.
    for (const region of live) {
      expect(Array.from(region)).toEqual(
        Array.from(new Uint8Array(region.length)),
      );
    }
  });

  // **The index key goes through the HMAC door, and the shorter route through
  // `importAesGcmKey` is silent.** It compiles, it returns a perfectly good
  // `CryptoKey`, and this service reports `unlocked` exactly as it does now —
  // the object it hands custody simply cannot sign a single blind index, and by
  // then it is non-extractable and the bytes are zeroes, so there is no
  // correcting it afterwards. Measured on this runner, `sign` under an AES-GCM
  // key and `encrypt` under an HMAC key are both refused with
  // `InvalidAccessError`, which is what makes the wrong door permanent rather
  // than merely wrong.
  //
  // Nothing about it is observable from outside the service — no member returns
  // a key — so the algorithms are read where they cross the platform boundary,
  // by the same spy the case above uses and for the same reason: a fake would
  // make every assertion here a statement about the fake.
  //
  // The neighbouring case counts the imports and is blind to this: two imports
  // is still two when both of them are `'AES-GCM'`.
  it('imports the content key as a cipher key and the index key as a MAC key', async () => {
    // Arrange
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x84);
    const body = await custodyWith(keys, [
      await factorFor(kek, FIRST_FACTOR_ID, keys),
    ]);

    api.getAccountKeys.mockReturnValue(of(body));

    // Installed after the fixtures, so the key-encryption key and the two
    // envelopes above — which go through the doors themselves — are not in the
    // census.
    const realImportKey = crypto.subtle.importKey;
    const algorithms: string[] = [];

    const importer = vi
      .spyOn(crypto.subtle, 'importKey')
      .mockImplementation(
        (format, keyData, algorithm, extractable, keyUsages) => {
          algorithms.push(
            typeof algorithm === 'string' ? algorithm : algorithm.name,
          );

          return realImportKey.call(
            crypto.subtle,
            format,
            keyData,
            algorithm,
            extractable,
            keyUsages,
          );
        },
      );

    // Act
    try {
      custody.unlock(kek);
      await settled(custody);
    } finally {
      importer.mockRestore();
    }

    // Assert
    expect(custody.status()).toBe('unlocked');

    // Sorted, because the account's two imports are issued in one `Promise.all`
    // and their order is the platform's business rather than this rule's.
    //
    // **The whole census and not a filtered one, which is what keeps the rule
    // sharp.** Opening one entry runs the keypair path, and that path's own
    // imports are in here: two `ECDH` — the factor's private half out of its
    // PKCS#8, and the ephemeral point it agrees against — one `HKDF` turning the
    // raw agreement into a key, and one `AES-GCM` for the key that agreement
    // derives. The account's content key is the second `AES-GCM` and its index
    // key is the one `HMAC`.
    //
    // An implementation that sent both account keys through one door produces
    // three `AES-GCM` and no `HMAC`, which this multiset refuses. A count could
    // not see it: six imports is still six.
    expect([...algorithms].sort()).toEqual([
      'AES-GCM',
      'AES-GCM',
      'ECDH',
      'ECDH',
      'HKDF',
      'HMAC',
    ]);
  });

  // **One instance for the whole application, and the widening that breaks it
  // is one word.** `providedIn: 'any'` reads as the harmless relaxation — it is
  // literally "whatever injector asks" — and what it does is give every lazily
  // loaded part of the route table its own custody. A person unlocks the
  // account on `/welcome`, walks into `/app`, and the screen there asks an
  // instance that has never held a key. Nothing goes red; the symptom is an
  // account that was readable a moment ago and is not now, and a factor to
  // present all over again to get it back — on a screen that can give no reason
  // for asking.
  //
  // The same word is what makes route-providing on `app` wrong, which this
  // class's header argues at length. This is that argument made executable.
  it('is one instance however many injectors ask for it', () => {
    // Arrange
    // A child of the application's environment injector — the shape a lazily
    // loaded route creates. Under `'root'` a request from here resolves to the
    // instance the root already holds; under `'any'` it builds a second one.
    const child = createEnvironmentInjector(
      [],
      TestBed.inject(EnvironmentInjector),
    );

    // Act
    const fromChild = child.get(AccountKeyCustodyService);

    // Assert
    expect(fromChild).toBe(custody);
  });

  // **A stale attempt's failure may not speak for a world that has moved**, and
  // this is the case where the two halves of that rule fail together.
  //
  // `#fail` sets `status` to `'locked'` *without* nulling the keys, because the
  // attempt it is reporting on never held any. So a failure published out of
  // turn does not merely say the wrong word: it says `'locked'` while the
  // fields hold a newer generation's keys, and every reader of `status()` is
  // then wrong about what this service is holding.
  //
  // Two edits reach it, and both are the kind somebody makes while tidying:
  //
  //   * dropping `#fail`'s generation guard, and
  //   * having `adopt()` call `#hold` directly instead of `#forget` first —
  //     which looks redundant, since `#hold` sets the same status and clears
  //     the same failure. What it drops is the generation bump, and the bump is
  //     the only part of `#forget` that `#hold` does not repeat.
  //
  // The read is a `Subject` rather than an `of`, so the attempt is genuinely in
  // flight when `adopt()` lands rather than merely early in a microtask queue.
  it('keeps adopted keys when an attempt that started earlier fails later', async () => {
    // Arrange
    const answer = new Subject<AccountKeyCustodyDto>();
    const kek = await keyEncryptionKey(0x85);
    const contentKey = await keyEncryptionKey(0x86);
    const indexKey = await keyEncryptionKey(0x87);

    api.getAccountKeys.mockReturnValue(answer);

    // Act
    custody.unlock(kek);
    expect(custody.status()).toBe('unlocking');

    custody.adopt(contentKey, indexKey);
    expect(custody.status()).toBe('unlocked');

    // The attempt now finishes, and finishes badly: an empty list is
    // `unopened`, which is the branch that publishes through `#fail`.
    answer.next(custodyOf([]));
    answer.complete();

    // `settled` cannot be used: the status left `'unlocking'` at the `adopt()`
    // above, so it would return before the attempt had run at all.
    await flush(10);

    // Assert
    // Still holding what the caller handed over, and blaming nobody. A stale
    // `unopened` here would lock an account whose keys this service is
    // demonstrably holding, and would blame a factor that was never presented.
    expect(custody.status()).toBe('unlocked');
    expect(custody.unlockFailure()).toBeNull();
  });

  // **A read the server refused is neither of the other two words**, and once
  // the request stopped routing its 401 into `sessionExpiryInterceptor` this is
  // the only place that answer can be read at all.
  //
  //   * `unreachable` advises the same factor again in a minute. A 401 will
  //     never change on its own: there is no session, so there are no envelopes
  //     to read, this minute or any other. It is also false by that word's own
  //     definition — a 401 is a usable answer, arrived from a server that was
  //     reached.
  //   * `unopened` advises another factor. Also wrong, and worse: the factor
  //     was never judged. Nothing this person presents opens an account the
  //     server will not talk about.
  //
  // 403 joins it rather than getting a fourth word, because the two share a
  // next step exactly: the locked-session refusal and the CSRF refusal both
  // mean this browser may not read these envelopes, and no amount of retrying
  // or of hunting for a recovery card changes that. Signing in again does.
  it.each([
    { status: 401, why: 'the server named nobody' },
    { status: 403, why: 'the server named somebody who may not read them' },
  ])(
    'reads a refused read as unauthenticated when $why',
    async ({ status }) => {
      // Arrange
      api.getAccountKeys.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status })),
      );
      const kek = await keyEncryptionKey(0x88);

      // Act
      custody.unlock(kek);
      await settled(custody);

      // Assert
      expect(custody.status()).toBe('locked');
      expect(custody.unlockFailure()).toBe('unauthenticated');

      // **And still nothing on `SessionService`.** The word changed; the rule did
      // not. A read the server refused is the one failure that looks most like a
      // session ending, which is exactly why this assertion belongs on this case:
      // the tidy answer to a 401 is to publish `anonymous` from here, and that
      // would sign somebody out from a service the session class reaches *into*.
      expect(session.ended).not.toHaveBeenCalled();
      expect(session.established).not.toHaveBeenCalled();
    },
  );

  it.each([
    { status: 0, why: 'the request never reached a server' },
    { status: 404, why: 'the route answered as though it did not exist' },
    { status: 500, why: 'the server is up and broken' },
  ])('still reads $why as unreachable', async ({ status }) => {
    // Arrange
    // The control for the split above, and it is the half that keeps the new
    // word from swallowing the old one. A `#fail('unauthenticated')` written
    // for every failed read passes both cases above perfectly.
    //
    // A `404` is in here deliberately: this route answers an empty array and
    // never a `404`, so one arriving is a proxy or a deployment answering for
    // it — not a statement about this browser's session, and not one about the
    // factor either.
    api.getAccountKeys.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status })),
    );
    const kek = await keyEncryptionKey(0x89);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.unlockFailure()).toBe('unreachable');
  });

  // **This rule reads source text rather than behaviour, and says so.**
  //
  // `#forget` dropping its two `= null` assignments is the central promise of
  // this class broken — `lock()` is documented to *drop* the keys, not merely
  // to stop admitting to them. The keys stay unreadable whatever happens in
  // that method, which is the property the class is built on: no public member
  // returns one, `#` fields are unreachable from outside the class body by the
  // language, and the header argues at length that an accessor added to make
  // this checkable would be the very defect it is checking for.
  //
  // **Each key field is watched by a running case, and each one is watched
  // through the operation that reads it.** `sealField` and `openField` read the
  // content field to decide whether they are locked, so `answers locked to a
  // seal after adopt and then lock` and `answers locked to a read after adopt
  // and then lock` catch that field's assignment going missing. `blindIndex`
  // reads the index field, so `answers locked to an index after adopt and then
  // lock` — over in the indexing describe, for the fixture reason stated there
  // — catches the other. The three are one rule about `lock()` over two fields,
  // named here because they do not sit together.
  //
  // Measured on this runner, each assignment removed on its own and nothing
  // else touched: dropping the content key's reddens this rule and its two
  // cases, a seal after `lock()` answering `sealed` and a read answering
  // `text`; dropping the index key's reddens this rule and its one, an index
  // after `lock()` answering `computed` with the MAC having run.
  //
  // **All three are green over a `#forget` that drops nothing, and that is why
  // this rule stays.** A behavioural case cannot see the field — it sees an
  // *operation's answer*, and an operation can reach `locked` by more than one
  // road. Give the three of them a `status()` check above the key read, which
  // is the tidier-looking guard and the one a reader reaches for, and every one
  // of them passes while both keys sit on the instance for the life of the tab:
  // measured, that edit reddens this rule **alone**. The text is the only
  // reading aimed at the method the promise is about rather than at what the
  // class admits to afterwards.
  //
  // What this cannot catch, stated rather than papered over:
  //
  //   * a `#forget` that nulls the fields and then puts the keys back — the
  //     text is a presence check, not a reading of what the method does;
  //   * a third key field added later and not nulled, because the two names are
  //     written here rather than derived from the class. It would also arrive
  //     read by nothing, so no behavioural case would cover it either until an
  //     operation grew for it;
  //   * the assignments moved into a helper `#forget` calls, which is a correct
  //     refactor this case would call a failure. That is the cost of the
  //     technique and it is accepted: a red bar that a reader has to think
  //     about is the right price for the one reading that does not go through
  //     an operation.
  describe('the keys are dropped, not merely disowned', () => {
    it('nulls both key fields inside #forget', () => {
      // Arrange, Act
      const forget = bodyOf(
        readFileSync(CUSTODY_SOURCE, 'utf8'),
        '#forget(status: AccountKeyStatus): number {',
      );

      // Assert
      expect(
        forget,
        '#forget no longer drops the content key, so `lock()` stops admitting to a key it is still holding',
      ).toContain('this.#contentKey = null;');
      expect(
        forget,
        '#forget no longer drops the index key, so `lock()` stops admitting to a key it is still holding',
      ).toContain('this.#indexKey = null;');
    });

    it('would report a #forget that had stopped nulling them', () => {
      // Arrange
      // The negative control, and the case that makes the one above worth
      // anything. Both assertions there are green over a `bodyOf` that returned
      // the whole file — `#hold` is three lines away and mentions both field
      // names — so this plants exactly that trap: a `#forget` with the
      // assignments removed, and another method that still carries them.
      const mutated = [
        '  #forget(status: AccountKeyStatus): number {',
        '    this.#failure.set(null);',
        '    this.#status.set(status);',
        '    this.#generation += 1;',
        '',
        '    return this.#generation;',
        '  }',
        '',
        '  #reset(): void {',
        '    this.#contentKey = null;',
        '    this.#indexKey = null;',
        '  }',
      ].join('\n');

      // Act
      const forget = bodyOf(
        mutated,
        '#forget(status: AccountKeyStatus): number {',
      );

      // Assert
      expect(forget).not.toContain('this.#contentKey = null;');
      expect(forget).not.toContain('this.#indexKey = null;');
      // And the extractor really did read a region rather than nothing at all,
      // which is what stops this control passing over a `bodyOf` that returned
      // an empty string for every input.
      expect(forget).toContain('this.#generation += 1;');
    });

    it('refuses to pin a method that is no longer declared', () => {
      // Arrange, Act, Assert
      // The third control. `bodyOf` throwing is the whole reason a rename does
      // not silently retire the rule — a helper that answered `''` for a
      // missing declaration would leave both cases above green forever the day
      // `#forget` was renamed.
      expect(() => bodyOf('class Empty {}', '#forget(')).toThrow(
        /no longer declares/,
      );
    });
  });

  // **Two operations, and the fact that they are operations is the design.**
  // The account's content key never leaves this class, so a screen that needs a
  // description sealed asks for the description to be sealed rather than for
  // the key to seal it with — the rule `#forget`'s comment states and `the
  // public surface hands back no key` reads off the source text. Named rather
  // than counted off the file: a describe added between the two moves an
  // ordinal and reddens nothing.
  //
  // The cases are arranged so that the class's own answer can be checked
  // against the codec directly: `adopt` is the one door into custody that takes
  // key objects, so this file can hold the very key the service is holding and
  // open, by itself, what the service sealed. Without that, every round trip
  // here would be an implementation agreeing with itself.
  describe('sealing and opening a narrative field', () => {
    // Restored after every case rather than in a `finally` around each act.
    // The acts below reject while the operations are stubs, and a `finally`
    // that has to capture a return value cannot bracket a call that throws —
    // so the restore is moved to where it runs either way, and no case can
    // leave `crypto.subtle` spied for the ones after it.
    afterEach(() => {
      vi.restoreAllMocks();
    });

    // An unlocked account, and the content key it was unlocked with.
    //
    // Through `keyEncryptionKey` — a key-encryption key and a content key are
    // the same shape and the same door, 32 bytes through `importAesGcmKey`, so
    // the fixture is reused rather than copied. What makes it a content key is
    // only what it is handed to.
    async function adoptedContentKey(): Promise<CryptoKey> {
      const contentKey = await keyEncryptionKey(0x90);
      const indexKey = await keyEncryptionKey(0x91);

      custody.adopt(contentKey, indexKey);

      return contentKey;
    }

    // The wire value, or a failure naming the word that came back instead.
    //
    // A bare `expect(sealed.state).toBe('sealed')` asserts and narrows nothing,
    // so every assertion after it would need a non-null assertion over a
    // discriminated union — the reading this file refuses everywhere else.
    function wireOf(sealed: SealedField): string {
      if (sealed.state !== 'sealed') {
        throw new Error(
          `sealField answered '${sealed.state}' where a wire value was expected.`,
        );
      }

      return sealed.wire;
    }

    it('seals under the content key the account is holding', async () => {
      // Arrange
      const contentKey = await adoptedContentKey();

      // Act
      const sealed = await custody.sealField(BINDING, 'Lunch with Ana');

      // Assert
      expect(sealed.state).toBe('sealed');

      // **Opened by this file rather than by the service**, which is what makes
      // this a statement about the ciphertext instead of about a round trip
      // agreeing with itself. A `sealField` that base64url'd its own plaintext,
      // or that sealed under a key it minted, passes a seal-then-open pair
      // perfectly and fails here.
      expect(
        await openNarrativeField(contentKey, wireOf(sealed), BINDING),
      ).toBe('Lunch with Ana');
    });

    it('opens what it sealed, under the same binding', async () => {
      // Arrange
      await adoptedContentKey();

      // Act
      const sealed = await custody.sealField(BINDING, 'Dentist, second visit');
      const opened = await custody.openField(BINDING, wireOf(sealed));

      // Assert
      // The whole result and not just the text, so a `state` that came back
      // wrong beside a right `value` is a finding rather than a pass.
      expect(opened).toEqual({
        state: 'text',
        value: 'Dentist, second visit',
      });
    });

    it('answers locked to a seal on an account holding no key, and runs no cipher', async () => {
      // Arrange
      // Nothing adopted and nothing unlocked — the state every reloaded tab is
      // in until a factor is presented.
      //
      // `vi.spyOn` with no implementation calls through, which is what the two
      // import cases in this file rely on and the right default here too: a
      // substituted cipher would make the assertion below a statement about the
      // substitute.
      const cipher = vi.spyOn(crypto.subtle, 'encrypt');

      // Act
      const sealed = await custody.sealField(BINDING, 'Never written down');

      // Assert
      // **The discriminant itself, not merely "it did not seal".** A member
      // added later, or an `unreadable` copied across from the reading side,
      // passes every absence check in this case and is refused by this line.
      expect(sealed).toEqual({ state: 'locked' });

      // Nothing was sealed. Without this, an implementation that sealed under a
      // key it derived on the spot and *then* answered `locked` reads as
      // correct, while having put narrative text through a cipher key nobody
      // authorised.
      expect(cipher).not.toHaveBeenCalled();

      // And it asked nobody anything. A `sealField` that read
      // `GET /api/me/account-keys` to find out whether it could seal would work
      // perfectly, and would put a round trip and a possible 401 behind every
      // save in the product.
      expect(api.getAccountKeys).not.toHaveBeenCalled();
    });

    it('answers locked to a read on an account holding no key, never unreadable', async () => {
      // Arrange
      // A genuine envelope, sealed by this file under a key custody has never
      // been given. The value is perfectly good; the only thing missing is the
      // key — which is exactly the state a reloaded tab is in.
      const elsewhere = await keyEncryptionKey(0x92);
      const wire = await sealNarrativeField(elsewhere, 'Rent, March', BINDING);

      // Act
      const opened = await custody.openField(BINDING, wire);

      // Assert
      // **`locked`, and this case and the next one pin the split together.**
      // Either one alone is passed by an implementation that answers a single
      // word to every failure — and the two words are two different next steps:
      // present a factor, against nothing you can do about this value.
      expect(opened).toEqual({ state: 'locked' });
    });

    it('answers unreadable to a value that does not open under the key it has', async () => {
      // Arrange
      await adoptedContentKey();

      const elsewhere = await keyEncryptionKey(0x93);
      const wire = await sealNarrativeField(
        elsewhere,
        'Another account',
        BINDING,
      );

      // The guard that keeps the arrangement honest: the value really is a
      // well-formed envelope, so what fails below is the authentication and not
      // the decoder. Without it, a wire value this file mangled would produce
      // the same word for the wrong reason.
      expect(await openNarrativeField(elsewhere, wire, BINDING)).toBe(
        'Another account',
      );

      // Act
      const opened = await custody.openField(BINDING, wire);

      // Assert
      // **`unreadable`, and never `locked`.** The account is open; this one
      // column is not. Told `locked`, a person presents a factor they already
      // presented and watches nothing change.
      expect(opened).toEqual({ state: 'unreadable' });
    });

    // **The case a foreign wire cannot reach, and the only one that refuses a
    // memoising `openField`.**
    //
    // An implementation in which `sealField` remembers `wire → plaintext` and
    // `openField` answers `text` on a cache hit without running a cipher at all
    // passes every other case in this describe. The `unreadable` case above
    // cannot see it: the value it feeds in was sealed by this file, so it misses
    // the cache and falls through to a real decrypt that really fails. What
    // reaches it is a wire this service itself produced — a guaranteed hit —
    // read back under a binding the ciphertext was never sealed against.
    //
    // Same table, same column, one row along. That is the swap the associated
    // data exists to catch, and it is the realistic one: two descriptions in the
    // same column, and a mapper that carried the wrong row id in from a list.
    // The cache is keyed on the wire, so it answers `text` and hands one row's
    // narrative text back as another's.
    it('will not open a value it sealed under a binding naming another row', async () => {
      // Arrange
      await adoptedContentKey();

      const sealed = await custody.sealField(BINDING, 'Lunch with Ana');

      // The guard that keeps the arrangement honest: the two bindings differ in
      // the row and in nothing else, and both row ids are spellings the codec
      // accepts — so what fails below is the authentication, and not the
      // refusal the case after this one is about.
      expect(OTHER_ROW_BINDING.rowId).not.toBe(BINDING.rowId);
      expect(OTHER_ROW_BINDING.table).toBe(BINDING.table);
      expect(OTHER_ROW_BINDING.column).toBe(BINDING.column);
      expect(isCanonicalRowId(OTHER_ROW_BINDING.rowId)).toBe(true);

      // Act
      const opened = await custody.openField(OTHER_ROW_BINDING, wireOf(sealed));

      // Assert
      expect(opened).toEqual({ state: 'unreadable' });
    });

    // **The `catch` that re-throws a caller's defect, which nothing else in
    // this file reaches.**
    //
    // A key whose bytes can be read back out is one that can already be logged,
    // posted to a crash reporter or written to `localStorage`, and no API undoes
    // an extractable import. `openField` keeps no copy of that test and judges
    // no key itself: the refusal is the codec's, it is raised from **inside**
    // this method's `try`, and what decides whether it reaches the caller as a
    // rejection is the `catch` re-throwing on `NarrativeFieldMisuseError`.
    // Swallowed, the same defect lands as `unreadable` — dressed as a sentence
    // about damaged text, shown to somebody who can do nothing whatever about
    // it, over a row that is perfectly fine.
    //
    // **It is the only case that reaches that branch, and by construction
    // rather than by luck.** The binding refusal is raised above the `try`, so
    // it never passes through the `catch` at all; a wrong key, a foreign
    // binding and a mangled wire throw nothing the codec owns and are *meant*
    // to arrive as `unreadable`. An extractable content key is the one defect
    // raised inside the `try`, so only a **rejection** here pins the re-throw.
    //
    // **The fixture is minted with a bare `crypto.subtle.importKey`, which
    // production may not do and a spec may.** `key-import-single-source.spec.ts`
    // exempts specs from the two-doors rule, and the exemption is exactly what
    // this case needs: both doors in `account-keys.ts` hard-code
    // `extractable: false`, so an extractable content key is unreachable from
    // every real path into this class. The re-throw is pinned anyway, against
    // the day a third door appears — the state being unreachable is the reason
    // the branch looks like decoration, and the reason a reader would remove it.
    //
    // **What this case does not cover: `sealField` has no `catch` at all**, so
    // there is nothing on the sealing side that could turn the codec's refusal
    // into a word, and it reaches that caller as the rejection it already is.
    // The asymmetry is not a check forgotten on one side — neither operation
    // carries a copy of the test, and only the reading one has a `catch` the
    // refusal has to survive.
    it('rejects a read under an extractable content key, never unreadable', async () => {
      // Arrange
      // Two key objects over the same 32 bytes. The non-extractable twin seals
      // a genuine wire value — the codec refuses to seal under the other one at
      // all — and the extractable one is what custody is handed.
      const sealing = await keyEncryptionKey(0x94);
      const extractable = await crypto.subtle.importKey(
        'raw',
        new Uint8Array(ACCOUNT_KEY_BYTES).fill(0x94),
        'AES-GCM',
        true,
        ['encrypt', 'decrypt'],
      );
      const wire = await sealNarrativeField(sealing, 'Bus fare', BINDING);

      custody.adopt(extractable, await keyEncryptionKey(0x95));

      // The guard that keeps the arrangement honest: what is under test is the
      // extractability of the key custody holds, and not a fixture that quietly
      // came back non-extractable and would have been refused for the ordinary
      // reason.
      expect(extractable.extractable).toBe(true);

      // Act, Assert
      await expect(custody.openField(BINDING, wire)).rejects.toThrow(
        /non-extractable/,
      );

      // The control the refusal needs: the same wire, the same binding and the
      // same bytes, imported through the door production uses, open. Without
      // it, an `openField` that rejected every read passes the assertion above.
      custody.adopt(await keyEncryptionKey(0x94), await keyEncryptionKey(0x95));

      await expect(custody.openField(BINDING, wire)).resolves.toEqual({
        state: 'text',
        value: 'Bus fare',
      });
    });

    it('rejects a row id in any spelling but the canonical one', async () => {
      // Arrange
      const contentKey = await adoptedContentKey();
      const wire = await sealNarrativeField(contentKey, 'Groceries', BINDING);
      const shouted: NarrativeFieldBinding = {
        ...BINDING,
        rowId: ROW_ID.toUpperCase(),
      };

      // Act, Assert
      // **A rejection, and it must not be caught into a result.** The codec
      // refuses a non-canonical row id because associated data is rebuilt from
      // where a ciphertext was found, so a value sealed under a spelling no
      // later read reproduces stops opening in both directions, permanently.
      // Swallowed into `unreadable`, that defect arrives on screen as a
      // sentence about damaged text — a bug wearing a UI, in front of somebody
      // who cannot act on it, while the row it names is fine.
      await expect(custody.openField(shouted, wire)).rejects.toThrow(
        /canonical/,
      );

      // The control the refusal needs: the same call under the spelling the row
      // really carries opens. Without it, an `openField` that rejected on every
      // binding passes the assertion above and seals nothing ever again.
      await expect(custody.openField(BINDING, wire)).resolves.toEqual({
        state: 'text',
        value: 'Groceries',
      });
    });

    // **The derivation's own control**, and it is here because one of the
    // candidates a reader would list is not refused.
    //
    // `isCanonicalRowId` documents that it says nothing about the all-zero
    // uuid: that value *is* canonically spelled, and the server refuses it for a
    // reason that is not about spelling at all. So a case asserting the codec
    // rejects it would pin a rule that does not exist and would stay red
    // through the green pass. Filtering the candidates through the predicate
    // drops it automatically — and this case is what keeps that drop visible
    // instead of silent.
    it('derives the refused spellings from the codec, all-zero uuid excluded', () => {
      // Arrange, Act, Assert
      // Non-empty, or the `it.each` below drives no cases at all and reports a
      // clean run over nothing.
      expect(REFUSED_ROW_IDS.length).toBeGreaterThan(0);

      expect(REFUSED_ROW_IDS.map(({ how }) => how)).not.toContain(
        'the all-zero uuid',
      );
      expect(isCanonicalRowId('00000000-0000-0000-0000-000000000000')).toBe(
        true,
      );
    });

    // **Both operations, over every spelling the codec refuses**, and the
    // target is an implementation that judges the row id with a regular
    // expression of its own instead of letting the codec judge it.
    //
    // That implementation passes every other case in this file, including the
    // single-spelling rejection above: it refuses upper-case hex too. This
    // narrows the hole rather than closing it — a hand-rolled check that
    // happens to refuse all of these is still a second definition of one
    // spelling, and the half that drifts still seals and still opens everything
    // it wrote. Only review catches that, and this case is the reason review is
    // looking.
    //
    // Both directions, because they are two separate call sites and a check
    // written on one of them is the shape the drift actually takes: a seal that
    // refuses a spelling the read admits binds text to associated data no later
    // read reproduces.
    it.each(REFUSED_ROW_IDS)(
      'refuses $how in a row id, sealing and opening alike',
      async ({ rowId }) => {
        // Arrange
        const contentKey = await adoptedContentKey();
        const wire = await sealNarrativeField(contentKey, 'Groceries', BINDING);
        const binding: NarrativeFieldBinding = { ...BINDING, rowId };

        // Act, Assert
        await expect(custody.sealField(binding, 'Groceries')).rejects.toThrow(
          /canonical/,
        );
        await expect(custody.openField(binding, wire)).rejects.toThrow(
          /canonical/,
        );
      },
    );

    // **The order of the two gates, which no case above this one can see.**
    //
    // Both operations judge the binding *before* they read the key field, and
    // the order is the property rather than the pair of checks. Reversed, the
    // one caller defect that is unrecoverable — a row id in a spelling no later
    // read of that row reproduces — is reported to an unlocked tab and
    // **swallowed** by a locked one: found on the machines that happened to be
    // open, silent on every reloaded one, which is to say surfacing exactly
    // where nobody is looking for it. Whether a factor has been presented is not
    // a fact about whether the caller assembled its binding correctly.
    //
    // Every other case that feeds a refused spelling in has adopted a key
    // first, so all of them pass with the gates the other way round. This one
    // adopts nothing, which is the whole arrangement.
    //
    // The spelling comes from `REFUSED_ROW_IDS`, so this file still holds no
    // opinion about which spellings are refused — the sweep over all of them is
    // the `it.each` above, and this case is about the order alone.
    it('judges the row id before custody, on an account holding no key', async () => {
      // Arrange
      // Nothing adopted and nothing unlocked — the state every reloaded tab is
      // in until a factor is presented, and the state in which a reversed order
      // is invisible.
      const [{ rowId }] = REFUSED_ROW_IDS;
      const binding: NarrativeFieldBinding = { ...BINDING, rowId };

      // A genuine envelope, sealed by this file under a key custody has never
      // been given, so nothing below rests on a wire value that was malformed
      // to begin with.
      const elsewhere = await keyEncryptionKey(0x96);
      const wire = await sealNarrativeField(elsewhere, 'Rent, April', BINDING);

      // The guard that keeps the arrangement honest: the account really is
      // holding nothing, so `locked` is what a reversed order would answer.
      expect(custody.status()).toBe('locked');

      // Act, Assert
      await expect(custody.sealField(binding, 'Rent, April')).rejects.toThrow(
        /canonical/,
      );
      await expect(custody.openField(binding, wire)).rejects.toThrow(
        /canonical/,
      );

      // The control both halves need: under the spelling the row really
      // carries, this same locked account answers `locked` rather than
      // rejecting. Without it, a service that rejected everything while holding
      // no key passes the two assertions above.
      await expect(custody.sealField(BINDING, 'Rent, April')).resolves.toEqual({
        state: 'locked',
      });
      await expect(custody.openField(BINDING, wire)).resolves.toEqual({
        state: 'locked',
      });
    });

    it('answers locked when custody ends while the cipher is running', async () => {
      // Arrange
      const contentKey = await adoptedContentKey();
      const wire = await sealNarrativeField(
        contentKey,
        'Still in flight',
        BINDING,
      );

      // **The world moves between the read of the key and the answer**, which
      // is the one window this operation has: it reads the field, hands the key
      // to the platform, and comes back several turns later. The interleaving is
      // forced at the platform boundary rather than by a timer, so it lands in
      // that window on every run rather than usually — the same technique the
      // two import cases above use on `crypto.subtle.importKey`, and the
      // counterpart of the `Subject` the unlock cases use to hold a read open.
      //
      // It calls through, so the open genuinely succeeds. That is the point: an
      // implementation that published what it had just recovered would be
      // handing narrative text to a tab whose keys were dropped before the
      // answer arrived, and a failing decrypt could never show it.
      const realDecrypt = crypto.subtle.decrypt;

      vi.spyOn(crypto.subtle, 'decrypt').mockImplementation(
        (algorithm, key, data) => {
          custody.lock();

          return realDecrypt.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const opened = await custody.openField(BINDING, wire);

      // Assert
      // `toEqual` is exact, so a `value` riding along beside the word would be
      // a failure here rather than an extra property nobody looked at.
      expect(opened).toEqual({ state: 'locked' });
      expect(custody.status()).toBe('locked');
    });

    // **`unreadable` is dropped by the same line, and the case above cannot say
    // so.** Its decrypt succeeds, so the only answer that ever reaches the
    // generation check there is `text` — and an implementation that returned
    // `unreadable` before the check, or that only guarded the `text` branch,
    // passes it and passes both `unreadable` cases above, neither of which locks
    // anything mid-cipher.
    //
    // What the word costs is a claim this frame is no longer entitled to make.
    // `unreadable` says *the account is open and this one column is not*, which
    // sends somebody looking at a row that is fine; after a `lock()` the account
    // is not open at all, and the honest answer — the one that brings every
    // other field on the screen back with it — is `locked`.
    it('drops an unreadable answer too when custody ends mid-cipher', async () => {
      // Arrange
      // A genuine envelope sealed under a key custody has never been given, so
      // the decrypt below really fails rather than being made to.
      const contentKey = await adoptedContentKey();
      const elsewhere = await keyEncryptionKey(0x97);
      const wire = await sealNarrativeField(
        elsewhere,
        'Another account',
        BINDING,
      );

      // The guard that keeps the arrangement honest: without the interruption
      // this read answers `unreadable`, so the word that comes back below is
      // the generation check and not the shape of the fixture.
      await expect(custody.openField(BINDING, wire)).resolves.toEqual({
        state: 'unreadable',
      });
      expect(contentKey.extractable).toBe(false);

      const realDecrypt = crypto.subtle.decrypt;

      vi.spyOn(crypto.subtle, 'decrypt').mockImplementation(
        (algorithm, key, data) => {
          custody.lock();

          return realDecrypt.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const opened = await custody.openField(BINDING, wire);

      // Assert
      expect(opened).toEqual({ state: 'locked' });
      expect(custody.status()).toBe('locked');
    });

    // **A seal interrupted by `lock()` keeps its answer, and that asymmetry with
    // the read above is the argument rather than a check forgotten on one
    // side.** The class says so at length; this is the case that makes it fail.
    //
    // **It is what makes the generation counter unusable on this operation**,
    // and it is half a statement on its own — `answers locked to a seal whose
    // key was replaced mid-cipher` is the other half, and neither means much
    // without it. Take this case away and the only mid-cipher interleaving left
    // is on `decrypt`, so a reader who "fixed" the asymmetry by copying the
    // counter comparison onto `sealField` gets a clean run — and from then on,
    // signing out while a save is in flight silently discards text somebody has
    // just typed. The ciphertext is bound to the account's key whatever this
    // tab drops next; there is nobody it could be wrong for. With this case
    // present, that copy goes red here: `lock()` bumps the counter, exactly as
    // `adopt()` does.
    //
    // Mirrors the read's arrangement exactly, at the other platform boundary, so
    // the two read as one decision made twice rather than as two cases that
    // happen to differ.
    it('keeps the wire a seal produced when custody ends mid-cipher', async () => {
      // Arrange
      const contentKey = await adoptedContentKey();
      const realEncrypt = crypto.subtle.encrypt;

      vi.spyOn(crypto.subtle, 'encrypt').mockImplementation(
        (algorithm, key, data) => {
          custody.lock();

          return realEncrypt.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const sealed = await custody.sealField(BINDING, 'Typed, then signed out');

      // Assert
      // The account really did lock while the cipher ran, which is what stops
      // this passing over an implementation whose interruption never landed.
      expect(custody.status()).toBe('locked');
      expect(sealed.state).toBe('sealed');

      // **And the wire is the account's, opened by this file under the key that
      // was adopted.** Without this the case is passed by a `sealField` that
      // answered `sealed` over a value sealed under something else — the same
      // hole `seals under the content key the account is holding` closes for the
      // uninterrupted path.
      expect(
        await openNarrativeField(contentKey, wireOf(sealed), BINDING),
      ).toBe('Typed, then signed out');
    });

    // **`adopt` and then `lock` as a plain sequence — custody handed keys, then
    // told to drop them, with no cipher in flight and nothing racing.** Every
    // other `lock()` in this file is an *interruption*: it lands inside a spy on
    // the platform, or while an unlock is still in the air, and there a
    // generation guard answers and the key field is never read a second time.
    // The kind is what distinguishes these and not the number of them — a count
    // written here goes stale the moment a third is written, and one has been,
    // `answers locked to an index after adopt and then lock` doing this to the
    // **index** field from the indexing describe.
    //
    // The two below are what the source-text pin on `#forget` cannot reach —
    // that pin reads what was written, and its own comment lists the correct
    // refactors it would call a failure, and the guard that would leave all
    // three of these green.
    //
    // Measured on this runner, in a scratch copy with `#forget`'s content-key
    // `= null` removed: a seal after `lock()` answers `sealed`, and an open
    // answers **`text`** — narrative plaintext handed to a tab whose `status()`
    // reads `locked`. That is the promise of the class broken in the one way a
    // person could see it.
    it('answers locked to a seal after adopt and then lock', async () => {
      // Arrange
      await adoptedContentKey();

      // The guard that keeps the arrangement honest: this same call seals while
      // custody is holding the keys, so what changes below is the `lock()` and
      // not the binding, the text or the fixture.
      const before = await custody.sealField(BINDING, 'Before the sign-out');

      expect(before.state).toBe('sealed');

      const cipher = vi.spyOn(crypto.subtle, 'encrypt');

      custody.lock();

      // Act
      const sealed = await custody.sealField(BINDING, 'After the sign-out');

      // Assert
      expect(sealed).toEqual({ state: 'locked' });

      // And no cipher ran, which is the half that separates "the keys were
      // dropped" from "the answer was overwritten on the way out". A service
      // still holding the key could seal and then report `locked`, and every
      // assertion above would be green.
      expect(cipher).not.toHaveBeenCalled();
    });

    it('answers locked to a read after adopt and then lock', async () => {
      // Arrange
      const contentKey = await adoptedContentKey();
      const wire = await sealNarrativeField(contentKey, 'Rent, May', BINDING);

      // The guard, and here it carries the whole case: the value opens under
      // the key custody was handed, so a `locked` below is the key having been
      // dropped rather than a wire this file assembled badly. It is also the
      // measured failure written as an assertion — with `#forget` no longer
      // nulling the field, the second read answers this same `text` result.
      await expect(custody.openField(BINDING, wire)).resolves.toEqual({
        state: 'text',
        value: 'Rent, May',
      });

      custody.lock();

      // Act
      const opened = await custody.openField(BINDING, wire);

      // Assert
      // **`locked`, never `unreadable`.** Nothing was judged, so nothing may be
      // blamed: the value is demonstrably fine — the assertion above just opened
      // it — and the only thing missing is a factor.
      expect(opened).toEqual({ state: 'locked' });
    });

    // **A seal whose key was *replaced* mid-cipher, which is not what
    // `keeps the wire a seal produced when custody ends mid-cipher` arranges
    // and must not answer the same way.**
    //
    // `lock()` drops the account's keys; the wire a seal was already computing
    // is still that account's, so keeping it discards nothing. An `adopt()`
    // publishes **another account's** keys, and the wire in flight was sealed
    // under the ones this tab no longer holds — handing it back invites the
    // caller to write one account's ciphertext into a row belonging to the next,
    // where nothing in the product will ever open it and nothing on the server
    // can see that it happened.
    //
    // **What this pins is the instrument, and the instrument cannot be the
    // generation counter.** That counter is bumped by everything that changes
    // custody — by `lock()` and by `adopt()` alike, by design — so it cannot
    // tell the two interruptions apart: it moves identically for the tab that
    // dropped its keys, where the wire is still that account's and must be
    // kept, and for the tab handed another account's, where the wire must be
    // dropped. What `sealField` compares instead is **key identity**: the key
    // the cipher ran under against the one the account holds when it comes
    // back, kept while that is the same object or none is held, dropped only
    // when it was replaced.
    //
    // So this case and its neighbour are one statement in two halves, and
    // neither means much alone. Either one on its own is satisfied by an answer
    // written the same way for both interruptions — a counter comparison passes
    // this case and reddens the neighbour, an unconditional return passes the
    // neighbour and reddens this one, and nothing but a comparison over the key
    // itself passes both. Both assert the answer rather than the mechanism, so
    // a later instrument that also tells the two apart is free to replace it.
    it('answers locked to a seal whose key was replaced mid-cipher', async () => {
      // Arrange
      // Both replacement keys are drawn before the spy is installed, because the
      // interruption has to be synchronous inside the cipher call.
      await adoptedContentKey();

      const nextContentKey = await keyEncryptionKey(0x98);
      const nextIndexKey = await keyEncryptionKey(0x99);
      const realEncrypt = crypto.subtle.encrypt;

      vi.spyOn(crypto.subtle, 'encrypt').mockImplementation(
        (algorithm, key, data) => {
          custody.adopt(nextContentKey, nextIndexKey);

          return realEncrypt.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const sealed = await custody.sealField(BINDING, 'Sealed for whom?');

      // Assert
      // The account is open — under other keys. This is what separates the case
      // from the `lock()` one above, where the answer is deliberately kept, and
      // it is why an implementation cannot satisfy both by reading `status()`.
      expect(custody.status()).toBe('unlocked');

      // `toEqual` is exact, so the wire sealed under the previous account's key
      // cannot ride along beside the word.
      expect(sealed).toEqual({ state: 'locked' });
    });
  });

  // **Indexing a name so a lookup can key on it**, which reads the account's
  // *index* key where the two operations above read its content key.
  //
  // The defining property is the exact inverse of theirs. A narrative field is
  // bound to its row precisely so that two rows can never share a value; an
  // index must be **equal across rows** for equal names, or a uniqueness
  // constraint and a lookup over a column the operator cannot read both stop
  // meaning anything. Every case below is written against that inversion, and
  // `equal names across rows give one value` is the one that states it outright.
  //
  // **Every failure here is silent in production.** A blind index that is
  // stable, unique and 43 characters wide looks exactly like a working one from
  // every side: nothing on the server can see that it was computed under a
  // grammar no second client shares, and nothing on the client can see it
  // either. That is why the frozen vectors are read again in this file — see
  // the loader's own comment for what the duplication buys.
  describe('indexing a name for a lookup', () => {
    // Restored after every case rather than in a `finally` around each act, for
    // the reason the describe above gives: the acts below reject while the
    // operation is a stub, and a `finally` that has to capture a return value
    // cannot bracket a call that throws.
    afterEach(() => {
      vi.restoreAllMocks();
    });

    // An HMAC key of a stated seed, through the module's own door — the second
    // of the two, because the platform refuses an AES key in this role and an
    // HMAC key in the other. `key-import-single-source.spec.ts` exempts specs
    // from the two-doors rule and this file has no reason to take the
    // exemption.
    function indexKeyOf(seed: number): Promise<CryptoKey> {
      return importHmacSha256Key(new Uint8Array(ACCOUNT_KEY_BYTES).fill(seed));
    }

    // An unlocked account, and the index key it was unlocked with.
    //
    // The pair is taken in one `adopt`, so the content key is a fixture with no
    // part in anything below — which is itself the class's rule: a screen that
    // can seal can always index, and there is no state in which it does one and
    // not the other.
    async function adoptedIndexKey(seed = 0xa1): Promise<CryptoKey> {
      const contentKey = await keyEncryptionKey(0xa0);
      const indexKey = await indexKeyOf(seed);

      custody.adopt(contentKey, indexKey);

      return indexKey;
    }

    // The index value, or a failure naming the word that came back instead.
    //
    // A bare `expect(computed.state).toBe('computed')` asserts and narrows
    // nothing, so every assertion after it would need a non-null assertion over
    // a discriminated union — the reading this file refuses everywhere else.
    function indexValueOf(computed: BlindIndexValue): string {
      if (computed.state !== 'computed') {
        throw new Error(
          `blindIndex answered '${computed.state}' where an index value was expected.`,
        );
      }

      return computed.value;
    }

    // **The cast is the hazard, not a convenience.** `BlindIndexBinding` is a
    // closed union the compiler assembled out of callers it could see, plus one
    // member; a table and a column arriving as data — off a response, out of a
    // configuration, through one `as` in a mapper — has been through that check
    // not at all. This line *is* that mapper, written on purpose, and the two
    // cases it drives are what say the refusal happens at run time rather than
    // only in the type.
    const UNINDEXED_FIELD = {
      ...UNINDEXED_PAIR,
      budgetId: BUDGET_ID,
    } as BlindIndexBinding;

    // **Every frozen answer, and this is the only case that can tell delegation
    // from a reimplementation that happens to agree with itself.** Custody could
    // hash the name under the index key and reach `computed` for every other
    // case in this describe — equal names would still agree, different fields
    // would still differ, and every value written would key perfectly and match
    // no second client and no row already in the database.
    //
    // The list is non-empty by construction: the loader throws on a file with no
    // vectors and on a vector with no inputs, so there is no arrangement in
    // which this reports a clean run over nothing.
    it.each(FROZEN_BLIND_INDEX_CASES)(
      'computes the frozen answer for $why, spelled $input',
      async ({ table, column, budgetId, input, blindIndex }) => {
        // Arrange
        // The pair is resolved through the codec's own list rather than cast, so
        // a vector naming a table this product does not index fails here loudly
        // instead of typing as a legal pair it is not. The tenancy is the
        // vector's own, which is what makes the tenth case — one name, one pair,
        // one key, a second budget — a case this file can see at all.
        const binding = indexBindingFor(table, column, budgetId);

        custody.adopt(await keyEncryptionKey(0xa2), await frozenIndexKey());

        // The guard that keeps the arrangement honest: the account really is
        // open, so a `locked` below is the operation and not the fixture.
        expect(custody.status()).toBe('unlocked');

        // Act
        const computed = await custody.blindIndex(binding, input);

        // Assert
        // The whole result and not just the value, so a `state` that came back
        // wrong beside a right value is a finding rather than a pass.
        expect(computed).toEqual({ state: 'computed', value: blindIndex });
      },
    );

    // **The property the operation exists for, and the deliberate inverse of
    // what `openField` requires.** There the row is carried into the associated
    // data precisely so that two rows can never share a value; here two rows
    // holding the same name *must* land on the same 43 characters, or the
    // uniqueness constraint and the lookup this value is computed for both stop
    // meaning anything. An implementation that mixed a row, a nonce or a clock
    // into the message would still be stable, still be the right width, and
    // would answer no query anybody ever writes.
    it('gives equal names across rows one value, under one binding', async () => {
      // Arrange
      await adoptedIndexKey();

      // Act
      // Two calls with nothing between them but the call itself — which is the
      // whole arrangement, because there is no row to vary: the binding carries
      // none, so "across rows" is exactly "twice". Two separate binding objects,
      // so nothing can pass by reference identity.
      const first = await custody.blindIndex(
        firstIndexBinding(),
        "Trader Joe's",
      );
      const second = await custody.blindIndex(
        firstIndexBinding(),
        "Trader Joe's",
      );

      // Assert
      expect(indexValueOf(first)).toBe(indexValueOf(second));

      // The control equality needs: a different name under the same binding is a
      // different value. Without it, a `blindIndex` that answered one constant
      // to everything passes the assertion above and indexes the whole account
      // onto a single row.
      const other = await custody.blindIndex(
        firstIndexBinding(),
        'Somewhere else entirely',
      );

      expect(indexValueOf(other)).not.toBe(indexValueOf(first));
    });

    // **The tenancy reaches the codec, and this is the case that says so from
    // custody's side.** The account holds one index key, so a class that
    // dropped the member on its way past — or that took a pair and ignored the
    // rest of the argument — computes one value for both tenancies: two rows in
    // two ledgers an operator can see hold the same word, which is exactly what
    // NFR-014 refuses. It is the frozen pair rather than two values that merely
    // differ, so a class that separated them under some grammar of its own is a
    // failure here too.
    it('gives one name in two budgets the two frozen values', async () => {
      // Arrange
      const elsewhere = BLIND_INDEX_VECTORS.vectors.find(
        (vector) => vector.budgetId !== BUDGET_ID,
      );

      if (elsewhere === undefined) {
        throw new Error(
          'No frozen vector names a second budget, so this case is driven by nothing.',
        );
      }

      custody.adopt(await keyEncryptionKey(0xa4), await frozenIndexKey());

      // Act
      const here = await custody.blindIndex(
        indexBindingFor(elsewhere.table, elsewhere.column),
        elsewhere.inputs[0],
      );
      const there = await custody.blindIndex(
        indexBindingFor(elsewhere.table, elsewhere.column, elsewhere.budgetId),
        elsewhere.inputs[0],
      );

      // Assert
      expect(indexValueOf(there)).toBe(elsewhere.blindIndex);
      expect(indexValueOf(here)).not.toBe(indexValueOf(there));
    });

    // **Every entry of the list, and never entry zero alone.** A `blindIndex`
    // that only ever indexes the first pair — because it hard-codes the table,
    // or because it drops the field from the message altogether — passes a suite
    // built from `BLIND_INDEXED_FIELDS[0]` and silently refuses, or silently
    // collides, on the other three. That hole was measured on the codec's own
    // spec, which is why this one sweeps rather than samples.
    //
    // What it buys is the separation the grammar is for: one name under
    // `payees`, `accounts`, `categories` and `category_groups` is four unrelated
    // values, so nothing an operator learns about the payee list transfers to
    // the account list.
    it('gives one name a different value under every indexed field', async () => {
      // Arrange
      await adoptedIndexKey();

      // The guard that keeps the arrangement honest: there is more than one
      // pair to tell apart, so the distinctness assertion below is not
      // vacuously true of a one-entry list.
      expect(BLIND_INDEXED_FIELDS.length).toBeGreaterThan(1);

      // Act
      const computed = await Promise.all(
        BLIND_INDEXED_FIELDS.map((field) =>
          custody.blindIndex({ ...field, budgetId: BUDGET_ID }, "Trader Joe's"),
        ),
      );

      // Assert
      // A set against a count, so *any* two colliding is a failure rather than
      // only an adjacent pair.
      expect(new Set(computed.map(indexValueOf)).size).toBe(
        BLIND_INDEXED_FIELDS.length,
      );
    });

    it('answers locked on an account holding no key, and reaches no cipher', async () => {
      // Arrange
      // Nothing adopted and nothing unlocked — the state every reloaded tab is
      // in until a factor is presented.
      //
      // `vi.spyOn` with no implementation calls through, which is the right
      // default here for the reason the sealing case gives: a substituted MAC
      // would make the assertion below a statement about the substitute.
      const mac = vi.spyOn(crypto.subtle, 'sign');

      // Act
      const computed = await custody.blindIndex(
        firstIndexBinding(),
        'Never indexed',
      );

      // Assert
      // **The discriminant itself, not merely "it did not compute".** A member
      // added later, or an `unreadable` copied across from the reading side,
      // passes every absence check in this case and is refused by this line.
      expect(computed).toEqual({ state: 'locked' });

      // Nothing was signed. Without this, an implementation that indexed under a
      // key it derived on the spot and *then* answered `locked` reads as
      // correct, while having put a name through a MAC key nobody authorised —
      // and a keyed fingerprint of a name is the one thing this whole design
      // exists to keep away from a key the account did not choose.
      expect(mac).not.toHaveBeenCalled();

      // And it asked nobody anything, for the reason `sealField` may not: a
      // round trip and a possible 401 behind every lookup in the product.
      expect(api.getAccountKeys).not.toHaveBeenCalled();
    });

    // **The third half of the `adopt` then `lock` rule, and the only running
    // case that reaches the *index* field.** Its two siblings —
    // `answers locked to a seal after adopt and then lock` and `answers locked
    // to a read after adopt and then lock` — read the **content** field, so
    // both of them are green over a `#forget` that has stopped dropping this
    // one. The three are one rule about `lock()` over two fields, and they are
    // joined by naming each other rather than by sitting together, which is how
    // this file has always joined cases that are not neighbours.
    //
    // **It is written here and not beside them, and the fixture is the
    // reason.** `adoptedIndexKey` is what hands custody a real HMAC key;
    // `adoptedContentKey` next door puts an **AES** key in the index slot,
    // which is harmless while nothing signs under it and becomes a fixture that
    // lies the moment a guard moves and the MAC really runs — the case would
    // then fail with `InvalidAccessError` instead of with its own assertion,
    // which is a red bar that names the wrong thing.
    //
    // Measured on this runner, in a scratch copy with `#forget`'s
    // `this.#indexKey = null;` removed and nothing else touched: this case goes
    // red **beside** `nulls both key fields inside #forget` rather than instead
    // of it — the index answers `computed` after a `lock()`, and the MAC ran.
    // Before it was written, that mutation reddened the text scan alone.
    it('answers locked to an index after adopt and then lock', async () => {
      // Arrange
      await adoptedIndexKey();

      // The guard that keeps the arrangement honest: this same call computes
      // while custody is holding the keys, so what changes below is the
      // `lock()` and not the field, the name or the fixture.
      const before = await custody.blindIndex(
        firstIndexBinding(),
        'Before the sign-out',
      );

      expect(before.state).toBe('computed');

      // Installed after that call, so the census below is about the second
      // index and not about the fixture that proved the first one works.
      const mac = vi.spyOn(crypto.subtle, 'sign');

      custody.lock();

      // Act
      const computed = await custody.blindIndex(
        firstIndexBinding(),
        'After the sign-out',
      );

      // Assert
      // `toEqual` is exact, so a value computed under a key this account no
      // longer holds cannot ride along beside the word.
      expect(computed).toEqual({ state: 'locked' });

      // And no MAC ran, which is the half that separates "the key was dropped"
      // from "the answer was overwritten on the way out". A service still
      // holding the index key could sign and then report `locked`, and every
      // assertion above would be green — which is exactly the state a `#forget`
      // that stopped nulling this field leaves it in.
      expect(mac).not.toHaveBeenCalled();
    });

    // **`locked`, never a computed value, when custody ends mid-cipher** —
    // mirroring the read's interleaving case at the other platform boundary, and
    // deliberately *not* the seal's, which keeps its answer.
    //
    // The seal is kept because a ciphertext is entitled to nobody: it is
    // readable only under the key it was sealed under, so handing it back after
    // a `lock()` discards work and misleads no one. An index is the opposite —
    // it is a value the caller puts straight into a query or a column, and a tab
    // that has just dropped its keys is a tab that may no longer name the
    // account's rows. Publishing one is the same mistake as publishing narrative
    // text after a lock, one indirection along.
    it('answers locked, never a value, when custody ends mid-cipher', async () => {
      // Arrange
      await adoptedIndexKey();

      // **The world moves between the read of the key and the answer**, which is
      // the one window this operation has. The interleaving is forced at the
      // platform boundary rather than by a timer, so it lands in that window on
      // every run rather than usually.
      //
      // It calls through, so the MAC genuinely succeeds. That is the point: an
      // implementation that published what it had just computed would be handing
      // an account's index value to a tab whose keys were dropped before the
      // answer arrived, and a failing sign could never show it.
      const realSign = crypto.subtle.sign;

      vi.spyOn(crypto.subtle, 'sign').mockImplementation(
        (algorithm, key, data) => {
          custody.lock();

          return realSign.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const computed = await custody.blindIndex(
        firstIndexBinding(),
        'Indexed, then signed out',
      );

      // Assert
      // The account really did lock while the MAC ran, which is what stops this
      // passing over an implementation whose interruption never landed.
      expect(custody.status()).toBe('locked');

      // `toEqual` is exact, so a `value` riding along beside the word would be a
      // failure here rather than an extra property nobody looked at.
      expect(computed).toEqual({ state: 'locked' });
    });

    // **An index whose key was *replaced* mid-cipher — and the pair this makes
    // with the case above says something the sealing pair next door does not.**
    // A reader who assumes the three operations behave alike will get it
    // backwards, so it is written out rather than left to be inferred.
    //
    // `sealField` **keeps** its answer when custody merely dropped its keys, and
    // drops it only when they were **replaced**. That is why it compares key
    // *identity* and why the generation counter is unusable there: the counter
    // is bumped by `lock()` and `adopt()` alike, by design, so it cannot tell
    // the two apart. A ciphertext stays bound to the account's key whatever the
    // tab does next — returned after a `lock()` it discards no work and
    // misleads nobody, while one sealed under the *previous* account's key
    // invites the caller to write it into the next account's row.
    //
    // `blindIndex` **drops its answer in both cases**, so the generation counter
    // is exactly the right instrument here where it was exactly the wrong one
    // there. An index computed under a key the account no longer holds keys
    // nothing: it matches no row, and the lookup it is handed to comes back
    // **empty rather than failing** — a silence, over data that is all still
    // sitting there. Two neighbouring operations, two different instruments, and
    // what decides which is **what the value is for** rather than how it was
    // made.
    //
    // **The check a reader can run to see that, and it is the reason these two
    // cases are one statement in two halves.** Copy `sealField`'s key-identity
    // guard onto this operation — keep the answer while the account holds the
    // very key this MAC ran under, or holds none at all — and it passes *this*
    // case and reddens the one above: a `lock()` leaves the index key `null`, so
    // that guard keeps the value the rule says must be dropped. An
    // unconditional return passes neither. Only a comparison that treats both
    // interruptions alike passes the two together, and both assert the answer
    // rather than the mechanism, so a later instrument that also does is free to
    // replace it.
    it('answers locked to an index whose key was replaced mid-cipher', async () => {
      // Arrange
      // Both replacement keys are drawn before the spy is installed, because the
      // interruption has to be synchronous inside the MAC call.
      await adoptedIndexKey();

      const nextContentKey = await keyEncryptionKey(0xa3);
      const nextIndexKey = await indexKeyOf(0xa4);
      const realSign = crypto.subtle.sign;

      vi.spyOn(crypto.subtle, 'sign').mockImplementation(
        (algorithm, key, data) => {
          custody.adopt(nextContentKey, nextIndexKey);

          return realSign.call(crypto.subtle, algorithm, key, data);
        },
      );

      // Act
      const computed = await custody.blindIndex(
        firstIndexBinding(),
        'Indexed for whom?',
      );

      // Assert
      // The account is open — under other keys. This is what separates the case
      // from the `lock()` one above, and it is why an implementation cannot
      // satisfy both by reading `status()`.
      expect(custody.status()).toBe('unlocked');

      // `toEqual` is exact, so the value computed under the previous account's
      // key cannot ride along beside the word.
      expect(computed).toEqual({ state: 'locked' });
    });

    // **A pair that is not one of the four rejects, and is not caught into a
    // result** — the same terms as a refused binding next door, and for the same
    // reason. It is a caller's mistake about a value it read off a row, not a
    // state anybody can be told about, and a defect rendered as a word on a
    // screen is a bug wearing a UI in front of somebody who can do nothing
    // whatever about it.
    //
    // The pair is derived rather than typed — see `UNINDEXED_PAIR` for what it
    // is, and `UNINDEXED_FIELD` for why the cast that feeds it in is the hazard
    // being tested rather than a convenience.
    it('rejects a pair that is not one of the four, never answering a word', async () => {
      // Arrange
      await adoptedIndexKey();

      // The guards that keep the arrangement honest: the table really is one the
      // product indexes and the combination really is not, so what is refused
      // below is a legal table carrying an illegal column rather than a name
      // this product has never heard of. `UNINDEXED_PAIR` states what that does
      // and does not separate.
      expect(
        BLIND_INDEXED_FIELDS.some(
          ({ table }) => table === UNINDEXED_PAIR.table,
        ),
      ).toBe(true);
      expect(
        BLIND_INDEXED_FIELDS.some(
          ({ table, column }) =>
            table === UNINDEXED_PAIR.table && column === UNINDEXED_PAIR.column,
        ),
      ).toBe(false);

      // Act, Assert
      await expect(
        custody.blindIndex(UNINDEXED_FIELD, "Trader Joe's"),
      ).rejects.toThrow(/pair/);

      // The control the refusal needs: the same call over a pair the codec lists
      // computes. Without it, a `blindIndex` that rejected every field passes the
      // assertion above and indexes nothing ever again.
      const legal = await custody.blindIndex(
        firstIndexBinding(),
        "Trader Joe's",
      );

      expect(legal.state).toBe('computed');
    });

    // **The order of the two gates, which no case above this one can see** — the
    // rule both siblings pin, and nothing would hold it on this operation
    // otherwise.
    //
    // Reversed, a caller's defect is reported to an unlocked tab and
    // **swallowed** by a locked one: found on the machines that happened to be
    // open, silent on every reloaded one, which is to say surfacing exactly
    // where nobody is looking for it. Whether a factor has been presented is not
    // a fact about whether the caller assembled its field correctly.
    //
    // The case above adopts a key first, so it passes with the gates the other
    // way round. This one adopts nothing, which is the whole arrangement.
    it('judges the binding before custody, on an account holding no key', async () => {
      // Arrange
      // Nothing adopted and nothing unlocked — the state in which a reversed
      // order is invisible.
      //
      // The guard that keeps the arrangement honest: the account really is
      // holding nothing, so `locked` is what a reversed order would answer.
      expect(custody.status()).toBe('locked');

      // Act, Assert
      // **Both halves of the refusal, because they are two statements inside
      // one function and either could be moved without the other.** The pair
      // check and the tenancy check are asked of one argument; a case over the
      // pair alone would stay green over a class that reached for the key
      // before judging the spelling of the budget it was handed.
      await expect(
        custody.blindIndex(UNINDEXED_FIELD, 'Rent, June'),
      ).rejects.toThrow(/pair/);
      await expect(
        custody.blindIndex(firstIndexBinding(''), 'Rent, June'),
      ).rejects.toThrow(/budget/);

      // The control the refusal needs: under a binding the codec accepts, this
      // same locked account answers `locked` rather than rejecting. Without it,
      // a service that rejected everything while holding no key passes both
      // assertions above.
      await expect(
        custody.blindIndex(firstIndexBinding(), 'Rent, June'),
      ).resolves.toEqual({ state: 'locked' });
    });
  });

  // **The one capability a view-model mapper is handed, and the two pins that
  // say it is really this class's.**
  //
  // `narrative-text.ts` declares `NarrativeOpener` and `NarrativeIndexer` so a
  // mapper can be given a capability rather than this whole service: handed the
  // class, a row-shaped transform would need a `TestBed` to be exercised at
  // all, could reach `unlock`, `lock` and `adopt` on the way past, and would
  // tie a pure function to Angular's injector for the sake of one call. **What
  // a declaration cannot do is make anything assignable to it.** Until this
  // describe existed both were named nowhere outside their own file — a type
  // nothing is typed as is a comment with syntax, and the first mapper written
  // could still be handed the service with nothing going red.
  //
  // **Both cases call *through* the typed value, and that half is the reason
  // they exist.** A pin that assigned the method to a typed variable and
  // stopped would prove the signature, which is not where the defect lives:
  // `openField` reads `#contentKey`, so handing it over as a bare
  // `custody.openField` rather than as an arrow makes every call a `TypeError`
  // on the wrong receiver — and **a `#` read on the wrong receiver is not a
  // compile error**. The lint rule that would find it,
  // `@typescript-eslint/unbound-method`, is switched **off** for `*.spec.ts` in
  // `eslint.config.js`, so in this file a call is the only thing that can.
  //
  // What the pins do not hold is that a mapper takes the narrow type rather
  // than the service. Nothing in the compiler can say that — and five mappers
  // take it today (`toAccountView`, `toCategoryView`, `toCategoryGroupView`,
  // `toTransactionView`, `toPayeeView`), so these pins now protect a habit
  // already in the tree rather than one predicted for it. A sixth handed the
  // whole service would redden nothing here; that half stays held by review,
  // exactly as `narrative-text.ts` says.
  describe('the capability a view-model mapper is handed', () => {
    // The account's two keys, adopted, and handed back — so a case can seal or
    // compute *beside* the service and compare, rather than asking the service
    // both questions and watching it agree with itself.
    async function adoptedPair(): Promise<{
      contentKey: CryptoKey;
      indexKey: CryptoKey;
    }> {
      const contentKey = await keyEncryptionKey(0xb0);
      const indexKey = await importHmacSha256Key(
        new Uint8Array(ACCOUNT_KEY_BYTES).fill(0xb1),
      );

      custody.adopt(contentKey, indexKey);

      return { contentKey, indexKey };
    }

    it('hands openField over as a NarrativeOpener, and opens through it', async () => {
      // Arrange
      const { contentKey } = await adoptedPair();

      // Sealed by this file under the key the account is holding, so the answer
      // below is a statement about a real ciphertext rather than about a seal
      // and an open that would agree with each other however either was
      // written.
      const wire = await sealNarrativeField(
        contentKey,
        'Lunch with Ana',
        BINDING,
      );

      // **An arrow, and never `custody.openField`.** The bare reference type-
      // checks — the signatures are identical — and then every call reads
      // `#contentKey` on a receiver that is not the service, which the language
      // answers with a `TypeError` no compiler and no lint rule in this file
      // will report. This is the wiring a mapper's provider has to use, so it
      // is the wiring the pin uses.
      const open: NarrativeOpener = (binding, value) =>
        custody.openField(binding, value);

      // Act
      const opened = await open(BINDING, wire);

      // Assert
      // The whole result, so a `state` that came back wrong beside a right
      // `value` is a finding rather than a pass — and a real one, so a pin that
      // had only assigned the function would not have got this far.
      expect(opened).toEqual({ state: 'text', value: 'Lunch with Ana' });
    });

    it('hands blindIndex over as a NarrativeIndexer, and computes through it', async () => {
      // Arrange
      // **The indexer exists because no read hands a blind index back.**
      // `PayeeDto` carries no `nameKey` on the wire and nothing in the API
      // returns one, so a mapper that has just opened a name has to recompute
      // the index it keys on — which it can only do through the account's index
      // key, which only this class holds.
      const { indexKey } = await adoptedPair();

      // An arrow for the reason the case above gives: `blindIndex` reads
      // `#indexKey`, so the bare reference is the same silent `TypeError`.
      const index: NarrativeIndexer = (indexed, plaintext) =>
        custody.blindIndex(indexed, plaintext);

      // Act
      // **Every pair and never entry zero alone**, which is the habit this file
      // keeps in the describe above and the one a seam pin is most likely to
      // drop, having a different rule to make. An operation that hard-coded the
      // first pair would agree with the codec on `payees.name` and answer the
      // other three under a message nobody asked for.
      const computed = await Promise.all(
        BLIND_INDEXED_FIELDS.map((indexed) =>
          index({ ...indexed, budgetId: BUDGET_ID }, "Trader Joe's"),
        ),
      );

      // Assert
      // Against the codec's own answers under the same key, which says that
      // custody **delegated** — to `computeBlindIndex`, under the index key it
      // is holding, for the pair it was handed — and says nothing whatever
      // about the grammar those values came out of. Both sides call the same
      // function, so dropping the table from the message, changing the
      // separator or skipping normalization moves the expectation and the
      // answer together and this case stays green. The grammar is
      // `blind-index.spec.ts`'s, over frozen vectors. Delegation is worth
      // pinning on its own: a member that keyed the plaintext raw, hard-coded
      // one pair for all four, or reached for the content key instead is caught
      // by exactly this comparison, and by nothing about the width or the
      // distinctness of what comes back.
      const expected = await Promise.all(
        BLIND_INDEXED_FIELDS.map(async (indexed) => ({
          state: 'computed',
          value: await computeBlindIndex(
            indexKey,
            { ...indexed, budgetId: BUDGET_ID },
            "Trader Joe's",
          ),
        })),
      );

      expect(computed).toEqual(expected);
    });
  });

  // **No public member hands a key back, and this reads source text because
  // nothing running can see it.**
  //
  // Non-extractability stops the *bytes* leaving and does nothing at all about
  // a caller that holds the key object and decrypts a whole budget into a log
  // line. The rule is therefore about the shape of what was written, and the
  // operations above are what makes it affordable: a screen asks for the thing
  // it wants done to a value, and has no reason left to ask for the key that
  // would do it. Not counted, for the reason this file's header gives — the
  // class is expected to grow more of them.
  //
  // What this cannot catch is stated at each scanner. The short version: it
  // reads declarations, not what a body does, so a member that hands a key back
  // as `unknown` is caught by the census reddening on the member and by nothing
  // else.
  describe('the public surface hands back no key', () => {
    it('declares exactly the members it is meant to declare', () => {
      // Arrange, Act
      const declared = publicMembers(readFileSync(CUSTODY_SOURCE, 'utf8'));

      // Assert
      // Set against set. A member renamed or removed reddens too, and that is
      // wanted: both are changes to the surface this rule is about, and both
      // should be read by somebody rather than absorbed.
      expect(
        declared,
        'the public surface of AccountKeyCustodyService has moved — every member here is a way for a key to leave',
      ).toEqual(PUBLIC_SURFACE);
    });

    it('returns a key from nothing it declares', () => {
      // Arrange, Act
      const findings = keysHandedBack(readFileSync(CUSTODY_SOURCE, 'utf8'));

      // Assert
      // Named, not counted: this is a rule about *which* member, and the name
      // is the whole of the finding.
      expect(
        findings,
        `a key object is handed back from: ${[...findings].join(', ')}`,
      ).toEqual(new Set());
    });

    it('would report an accessor added over a key field', () => {
      // Arrange
      // The negative control, and the case that makes the two above worth
      // anything: both are green over a `publicMembers` that matched nothing
      // and a `keysHandedBack` that never looked. So this plants the exact edit
      // the rule exists to catch — the one an ESLint `no-unused-private-class-
      // members` result invites, and the one this class's header argues against
      // at length — written on a single line, which is the form no brace-walking
      // extractor would find.
      //
      // That lint result is not live over either key field: an operation reads
      // each of them now, and the directive that once stood over the index key
      // went out with the throw it stood over. It is the pressure the next field
      // this class grows will arrive under, which is why the accessor is still
      // the edit worth planting.
      const mutated = [
        'class AccountKeyCustodyService {',
        '  #contentKey: CryptoKey | null = null;',
        '',
        '  public lock(): void {}',
        '',
        '  public get contentKey() { return this.#contentKey; }',
        '}',
      ].join('\n');

      // Act
      const declared = publicMembers(mutated);
      const findings = keysHandedBack(mutated);

      // Assert
      // The census sees the member, and the second scanner says what it does.
      // Both halves, because each is the one that survives an edit the other
      // misses: an accessor named something innocent is caught by the census
      // only, and an annotated method added to a surface somebody also updated
      // is caught by the second scanner only.
      expect(declared).toEqual(new Set(['lock', 'contentKey']));
      expect(findings).toEqual(new Set(['#contentKey']));
    });
  });

  // **This class names no table and no column of the eight pairs**, and the
  // rule follows the codec rather than restating it: the words are derived from
  // `NARRATIVE_FIELDS`, so a ninth pair is covered the day it is added.
  //
  // The argument is that custody holds keys and knows nothing about the ledger.
  // A table name appearing here — in a branch, in a default, in a comment
  // explaining which column a caller probably meant — is the first line of a
  // second copy of the field list, and the copy that drifts still seals and
  // still opens everything it wrote. It is also how a special case gets in:
  // once the class can name a column, it can treat one differently, and the
  // codec's binding stops being the only thing that decides what a ciphertext
  // is bound to.
  //
  // Comments are included on purpose. Prose here is where the copy starts.
  describe('the eight narrative pairs are named nowhere in this class', () => {
    it('names no table and no column of them', () => {
      // Arrange
      // The control on the derivation, before any claim about what it found. An
      // empty `FORBIDDEN_WORDS` — an import that resolved to nothing, a list
      // that moved — reports the assertion below perfectly clean forever.
      expect(FORBIDDEN_WORDS.size).toBeGreaterThan(0);

      // Act
      const named = narrativeWordsIn(readFileSync(CUSTODY_SOURCE, 'utf8'));

      // Assert
      expect(
        named,
        `account-key-custody.service.ts names: ${[...named].join(', ')}`,
      ).toEqual(new Set());
    });

    it('would report one of them written into a comment', () => {
      // Arrange
      // The negative control. The case above is green over a scanner that
      // matched nothing at all, so this hands the same scanner a source that
      // carries exactly one of the derived words — in a comment, which is the
      // form a leak really takes and the one an implementation-only scan would
      // miss.
      //
      // The word is taken off the derived set rather than typed, or this file
      // would be carrying the copy it is hunting.
      const [word] = [...FORBIDDEN_WORDS];
      const mutated = [
        '// A note somebody left behind about which rows this is really for:',
        `// the ${word} a caller is most likely holding.`,
        'export class AccountKeyCustodyService {}',
      ].join('\n');

      // Act
      const named = narrativeWordsIn(mutated);

      // Assert
      // Exactly that word and no other, which is the half that says the scanner
      // is reading rather than reporting its whole input.
      expect(named).toEqual(new Set([word]));
    });

    // **The two cases above pass while the class holds the eight pairs**, and
    // that is the hole this one closes.
    //
    // Add `import { NARRATIVE_FIELDS } from './narrative-cipher';` to custody
    // and branch on `NARRATIVE_FIELDS[0].table`, and the word scan comes back
    // clean: the words live in the codec, and the class only ever writes the
    // identifier. The requirement is that the class holding the keys does not
    // learn which table and which column a value belongs to, and a value it can
    // index is learning them — with the first special case one `===` away, at
    // which point the codec's binding has stopped being the only thing that
    // decides what a ciphertext is bound to.
    //
    // So the rule is about the *edge* and not about the vocabulary: custody may
    // import the codec's operations, and it may import its types, which is where
    // `NarrativeFieldBinding` comes from and which cross nothing into the
    // bundle. It may not import its data. The limits of the scanner are stated
    // at the scanner, and the short version is that this half and the word scan
    // cover two different shapes of the same leak and neither sees the other's.
    it('imports the codec operations and its types, never its data', () => {
      // Arrange
      // The control on the derivation, before any claim about what it found. A
      // `FORBIDDEN_VALUE_IMPORTS` that came back empty — a namespace import that
      // resolved to nothing, a classifier that sorted everything into
      // `'function'` — would report the assertion below clean forever.
      expect(FORBIDDEN_VALUE_IMPORTS.has('NARRATIVE_FIELDS')).toBe(true);

      // Act
      const imported = valueImportsFromCodec(
        readFileSync(CUSTODY_SOURCE, 'utf8'),
      );

      // Assert
      // The scanner really read a clause. Custody does import operations from
      // the codec, so an empty answer here means the clause was not found and
      // the rule below is a statement about nothing.
      expect(
        imported,
        'no value import from narrative-cipher was found in account-key-custody.service.ts, so this rule is pinned against nothing',
      ).not.toEqual(new Set());

      const data = [...imported].filter(
        (name) => FORBIDDEN_VALUE_IMPORTS.has(name) || name.startsWith('* as '),
      );

      // Named, not counted, the way the accessor scan reports: the name is the
      // whole of the finding.
      expect(
        data,
        `account-key-custody.service.ts imports the codec's data: ${data.join(', ')}`,
      ).toEqual([]);
    });

    it('would report the list imported as a value, and a namespace import', () => {
      // Arrange, Act, Assert
      // The negative control, and the case that makes the one above worth
      // anything: it is green over a `valueImportsFromCodec` that never looked.
      //
      // The names are taken off the derived set rather than typed, or this file
      // would be deciding which of the codec's exports are data — the second
      // opinion the derivation exists to avoid.
      for (const name of FORBIDDEN_VALUE_IMPORTS) {
        expect(
          valueImportsFromCodec(
            `import { ${name} } from './narrative-cipher';`,
          ),
        ).toEqual(new Set([name]));
      }

      // The namespace form, which carries every export at once and which a
      // specifier-only scanner would report as nothing at all.
      expect(
        valueImportsFromCodec("import * as codec from './narrative-cipher';"),
      ).toEqual(new Set(['* as codec']));

      // **And the two shapes that must stay legal**, without which this rule is
      // red on arrival over the import custody actually has and gets answered by
      // deletion. A whole type-only statement crosses nothing, and a `type`
      // specifier inside a mixed clause crosses nothing either.
      expect(
        valueImportsFromCodec(
          "import type { NarrativeFieldBinding } from './narrative-cipher';",
        ),
      ).toEqual(new Set());
      expect(
        valueImportsFromCodec(
          [
            'import {',
            '  sealNarrativeField,',
            '  type NarrativeFieldBinding,',
            "} from './narrative-cipher';",
          ].join('\n'),
        ),
      ).toEqual(new Set(['sealNarrativeField']));
    });
  });
});
