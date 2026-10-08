// What this device has already seen, and nothing else. The module under test
// remembers the highest rotation epoch this browser has observed for an
// account; the refusal built on that memory lives elsewhere.
//
// The attack it exists for is not forgery. An operator who can write the
// database replays an old `(manifest, epoch)` pair the account genuinely was in
// once — correctly sealed, correctly framed, authenticating perfectly under the
// content key. Nothing in the cryptography can see that it is stale, because it
// is not fake; it is simply from before. The only party that can tell is one
// that watched the account move past it, and on the client that party is a
// device. Hence a per-device high-water mark, and hence ASM-016: a device that
// has never seen the account cannot detect a rollback at all. This narrows the
// window; it does not close it.
//
// Three things follow, and they are what the cases below are mostly about.
//
// **The floor is 1, not 0.** Epoch 0 is the server's "no manifest row" state,
// which no account this product creates can be in. A stored value that reads
// back as 0 is therefore never an observation, and a reader that lets one
// through hands the refusal a high-water mark below every real epoch — which is
// the same as having no memory at all, silently.
//
// **A parse here is a security decision, not a convenience.** Every lazy
// reading of a string is wrong in its own direction: `Number('')` is 0,
// `Number('0x2')` is 2, `parseInt('1.5')` is 1, and `Number('1e21')` is an
// integer by `Number.isInteger` while being far past the point where `+ 1`
// still changes the value. Each of those turns a junk cell — which is what an
// attacker who can reach this store writes — into a number the refusal will
// compare against.
//
// **A store that throws must read as a device that has not seen the account.**
// Blocked-storage modes (private browsing, a cleared origin, a quota) throw on
// the read as well as the write. A throw that escaped would refuse a manifest
// this client can perfectly well open, and lock private-mode browsing out of
// the product; a browser that cannot remember is simply permanently in
// ASM-016's first-visit state, which is a state this product already accepts.
// Note that `theme.service.ts` guards only its write and reads bare — do not
// read that asymmetry as the house pattern. It is not one here.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  highestRotationEpochSeen,
  recordRotationEpochSeen,
} from './rotation-epoch-record';

// Two accounts, spelled the way a budget id is spelled everywhere else in this
// folder. The second exists so that "keeps two accounts apart" is a statement
// about two real keys rather than about one key and a hole.
const BUDGET_ID = '01a05f2c-7b19-7c3d-8e4f-5a6b7c8d9e0f';
const OTHER_BUDGET_ID = '0192f4c1-6d2a-7e88-9b31-2c4d5e6f7a80';

// Spelled out, not derived from the subject. One key per account is the
// decision — not one JSON map — because a map is a read-modify-write two tabs
// can lose an update through, and a parsed object is a shape an attacker who
// can write this store gets to choose. A test that imported the key builder
// from the subject would agree with any spelling it invented, including a
// single shared key.
const storageKeyFor = (budgetId: string): string =>
  `budgetoid-rotation-epoch:${budgetId}`;

describe('rotation epoch record', () => {
  beforeEach(() => {
    // jsdom's `localStorage` survives from case to case inside one file — it is
    // per test *file*, not per case — so without this a record written by an
    // earlier case is an invisible fixture for a later one.
    localStorage.clear();
  });

  afterEach(() => {
    // Nothing configures `restoreMocks`, so a `Storage.prototype` spy installed
    // by one case stays installed for every case after it in this file.
    vi.restoreAllMocks();
  });

  describe('an account this device has not seen', () => {
    it('answers no record at all', () => {
      // Arrange
      // Nothing recorded: the store was cleared above.

      // Act
      const seen = highestRotationEpochSeen(BUDGET_ID);

      // Assert
      expect(seen).toBeNull();
    });

    it('writes nothing while answering', () => {
      // Arrange
      // Nothing recorded.

      // Act
      highestRotationEpochSeen(BUDGET_ID);

      // Assert
      // A read that seeded a default would make the very first manifest this
      // device ever sees the thing the record was built to refuse.
      expect(localStorage.length).toBe(0);
    });
  });

  describe('recording an observation', () => {
    it('answers back the epoch it was told', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 7);

      // Act
      const seen = highestRotationEpochSeen(BUDGET_ID);

      // Assert
      expect(seen).toBe(7);
    });

    it('answers a number, never the digits it stored', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 7);

      // Act
      const seen = highestRotationEpochSeen(BUDGET_ID);

      // Assert
      // `'9' < '10'` is false and `'9' < 7` is false: a caller comparing a
      // string high-water mark against a served epoch gets an answer that is
      // wrong in both directions and never throws.
      expect(typeof seen).toBe('number');
    });

    it('survives being recorded by one call and read by another', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 3);

      // Act
      const first = highestRotationEpochSeen(BUDGET_ID);
      const second = highestRotationEpochSeen(BUDGET_ID);

      // Assert
      // Two reads, because an implementation holding the value in a module
      // variable rather than in the store answers the first one correctly and
      // forgets everything the moment the tab reloads.
      expect(first).toBe(3);
      expect(second).toBe(3);
    });

    it('stores one value under a key naming the account', () => {
      // Arrange
      // Nothing recorded.

      // Act
      recordRotationEpochSeen(BUDGET_ID, 4);

      // Assert
      // The key spelling is the contract, not an implementation detail: it is
      // what makes the record survive a reload, and what makes two tabs of two
      // accounts unable to overwrite each other.
      expect(localStorage.getItem(storageKeyFor(BUDGET_ID))).toBe('4');
      expect(localStorage.length).toBe(1);
    });
  });

  describe('the record never lowers', () => {
    it('keeps the higher epoch when told a lower one', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 9);

      // Act
      recordRotationEpochSeen(BUDGET_ID, 4);

      // Assert
      // This is the whole module. A replayed manifest arrives with a real,
      // earlier epoch, and the client is told about it by the same code path a
      // genuine one uses.
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(9);
    });

    it('keeps the epoch when told the same one again', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 9);

      // Act
      recordRotationEpochSeen(BUDGET_ID, 9);

      // Assert
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(9);
    });

    it('rises when told a higher epoch', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 9);

      // Act
      recordRotationEpochSeen(BUDGET_ID, 10);

      // Assert
      // Ten and nine are chosen over two and one: `'10' < '9'` in string order,
      // so an implementation comparing the stored text refuses this promotion
      // and passes every single-digit case anyone would have written.
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(10);
    });

    it('rises across a gap, not by one', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 2);

      // Act
      recordRotationEpochSeen(BUDGET_ID, 17);

      // Assert
      // The epoch is a concurrency token that holds atomicity only: `N + 17`
      // satisfies the server's rule exactly as `N + 1` does, so a record
      // accepting only the successor would refuse an account that really moved.
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(17);
    });
  });

  describe('two accounts', () => {
    it('keeps each account’s record apart', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 5);

      // Act
      recordRotationEpochSeen(OTHER_BUDGET_ID, 2);

      // Assert
      // Both directions: a single shared key would answer 2 for the first
      // account, and a record ignoring its second argument would answer 5 for
      // the second.
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(5);
      expect(highestRotationEpochSeen(OTHER_BUDGET_ID)).toBe(2);
    });

    it('does not lower one account against the other’s ceiling', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 9);

      // Act
      recordRotationEpochSeen(OTHER_BUDGET_ID, 3);

      // Assert
      // A record comparing against the highest epoch it has seen anywhere —
      // one variable rather than one key each — drops this observation, and the
      // second account is left unprotected by a fact about the first.
      expect(highestRotationEpochSeen(OTHER_BUDGET_ID)).toBe(3);
    });

    it('answers no record for an account whose neighbour has one', () => {
      // Arrange
      recordRotationEpochSeen(BUDGET_ID, 6);

      // Act
      const seen = highestRotationEpochSeen(OTHER_BUDGET_ID);

      // Assert
      expect(seen).toBeNull();
    });
  });

  describe('a store that throws', () => {
    it('reads as a device that has not seen this account', () => {
      // Arrange
      const getItem = vi
        .spyOn(Storage.prototype, 'getItem')
        .mockImplementation(() => {
          throw new Error('SecurityError: access to storage is denied');
        });

      // Act
      const read = (): number | null => highestRotationEpochSeen(BUDGET_ID);

      // Assert
      // Never a refusal. A browser that cannot remember is in ASM-016's
      // first-visit state, which this product accepts; a throw escaping here
      // would instead lock private-mode browsing out of the product entirely.
      expect(read).not.toThrow();
      expect(read()).toBeNull();
      expect(getItem).toHaveBeenCalled();
    });

    it('writes without failing the caller', () => {
      // Arrange
      const setItem = vi
        .spyOn(Storage.prototype, 'setItem')
        .mockImplementation(() => {
          throw new Error('QuotaExceededError');
        });

      // Act
      const record = (): void => recordRotationEpochSeen(BUDGET_ID, 8);

      // Assert
      // The caller is a screen in the middle of opening an account. A record it
      // could not keep is a smaller failure than the screen not finishing.
      expect(record).not.toThrow();
      expect(setItem).toHaveBeenCalled();
    });

    it('does not fail the caller when the read inside the write throws', () => {
      // Arrange
      // The write has to consult the current record to know whether this one is
      // higher, so a store throwing on `getItem` alone reaches the write path
      // too — and that is the arrangement `theme.service.ts` has no equivalent
      // of, because it never reads before writing.
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new Error('SecurityError: access to storage is denied');
      });

      // Act
      const record = (): void => recordRotationEpochSeen(BUDGET_ID, 8);

      // Assert
      expect(record).not.toThrow();
    });
  });

  describe('a stored value that is not a whole epoch', () => {
    it.each([
      // `Number('')` is 0 and `''` is falsy in different places — a guard
      // written either way lets this through as an epoch or as a zero.
      { why: 'the empty string', stored: '' },
      // Epoch 0 is the server's "no manifest" sentinel, so it is never an
      // observation, and a record holding 0 refuses nothing.
      { why: 'the no-manifest sentinel', stored: '0' },
      { why: 'a negative epoch', stored: '-1' },
      // `parseInt('1.5')` is 1, silently.
      { why: 'a fraction', stored: '1.5' },
      // `Number.isInteger(1e21)` is true. It is also past the point where the
      // next epoch is representable, so a record holding it refuses forever.
      { why: 'an epoch past the safe integers', stored: '1e21' },
      { why: 'a word', stored: 'abc' },
      // `Number('0x2')` is 2 — a second spelling of an epoch, which means a
      // store an attacker can write offers two ways to say every number.
      { why: 'a hexadecimal literal', stored: '0x2' },
      { why: 'whitespace alone', stored: '   ' },
      { why: 'a boolean', stored: 'true' },
      { why: 'the JSON null', stored: 'null' },
      // `Number('Infinity')` is Infinity, which compares higher than every real
      // epoch — the one junk value that refuses a *genuine* manifest.
      { why: 'an infinity', stored: 'Infinity' },
      { why: 'a not-a-number', stored: 'NaN' },
      // The shape a `JSON.parse` reader would accept and an integer reader
      // would not.
      { why: 'an object', stored: '{"epoch":3}' },
      { why: 'digits with a tail', stored: '3abc' },
      // **Digits all the way down, and past the safe integers.** `'1e21'` above
      // is refused by the shape rule alone — it carries an `e` — so nothing in
      // this table reached the check that runs *after* the shape. Twenty digits
      // is decimal, has no leading zero and passes that rule perfectly; what
      // refuses it is `Number.isSafeInteger`. Without that second check it reads
      // back as `1e20` and pins this account's high-water mark above every epoch
      // it will ever reach, permanently, from one cell an attacker wrote.
      {
        why: 'a decimal past the safe integers',
        stored: '99999999999999999999',
      },
      // **Digits with whitespace around them, which is the other half.** The
      // shape rule refuses it because it is anchored and whitespace is not a
      // digit — but a reader that trimmed first, which is the tidying edit
      // somebody makes to be generous about a stored value, accepts it as 5.
      // That gives every epoch a second spelling in a store an attacker also
      // writes, and two spellings of one number is how a record gets overwritten
      // by something that does not look like it.
      { why: 'digits wrapped in whitespace', stored: ' 5 ' },
    ])('reads $why as no record at all', ({ stored }) => {
      // Arrange
      localStorage.setItem(storageKeyFor(BUDGET_ID), stored);

      // Act
      const seen = highestRotationEpochSeen(BUDGET_ID);

      // Assert
      expect(seen).toBeNull();
    });

    it('records over a value it could not read', () => {
      // Arrange
      // Beyond the seven the brief names, and deliberately: an implementation
      // comparing a fresh epoch against a `NaN` current writes nothing —
      // `NaN < 3` is false — and the device is then permanently unable to
      // record anything for this account, which is exactly the state an
      // attacker who can write one junk cell would want it in.
      localStorage.setItem(storageKeyFor(BUDGET_ID), 'abc');

      // Act
      recordRotationEpochSeen(BUDGET_ID, 3);

      // Assert
      expect(highestRotationEpochSeen(BUDGET_ID)).toBe(3);
    });
  });
});
