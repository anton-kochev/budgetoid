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
// **What can be observed at all is `status` and `unlockFailure`, and that is the
// design rather than a limitation of the test.** No public member returns a key
// and none ever will: non-extractability stops the *bytes* leaving and does
// nothing about a caller holding the key object and decrypting a whole budget
// into a log line. So "the right entry opened" is read here as "the service
// reports `unlocked`", and the arrangements are built so that a wrong
// implementation cannot reach that word.
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import {
  MeApiService,
  type AccountKeyEntry,
} from '@app-core/api/me-api.service';
import { SessionService } from '@app-core/session/session.service';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ACCOUNT_KEY_BYTES,
  generateAccountKeys,
  importAesGcmKey,
  wrapAccountKeys,
  type AccountKeys,
} from './account-keys';
import { AccountKeyCustodyService } from './account-key-custody.service';

// Three canonical factor ids, distinct and in the spelling the server renders.
// The identifier a row carries **is** the associated data its two envelopes were
// sealed with, so these are not labels — a wrong one here is an envelope that
// does not open, which is exactly the property two of the cases below turn on.
const FIRST_FACTOR_ID = 'c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6';
const SECOND_FACTOR_ID = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0';
const THIRD_FACTOR_ID = '7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d';

class MeApiStub {
  // An empty list by default, which is the answer the route gives for a session
  // it cannot see. Every case that means something else says so out loud.
  public getAccountKeys = vi.fn(
    (): Observable<readonly AccountKeyEntry[]> => of([]),
  );
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
// two envelopes were sealed against, and the envelopes.
async function entryFor(
  kek: CryptoKey,
  factorId: string,
  keys: AccountKeys,
): Promise<AccountKeyEntry> {
  return { factorId, ...(await wrapAccountKeys(kek, keys, factorId)) };
}

describe('AccountKeyCustodyService', () => {
  let api: MeApiStub;
  let session: SessionStub;
  let custody: AccountKeyCustodyService;

  beforeEach(() => {
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
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x11);
    const otherKek = await keyEncryptionKey(0x22);

    const entries = [
      await entryFor(otherKek, FIRST_FACTOR_ID, keys),
      await entryFor(kek, SECOND_FACTOR_ID, keys),
      await entryFor(otherKek, THIRD_FACTOR_ID, keys),
    ];

    api.getAccountKeys.mockReturnValue(of(entries));

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

    const entries = [
      await entryFor(otherKek, FIRST_FACTOR_ID, keys),
      await entryFor(kek, SECOND_FACTOR_ID, keys),
    ];

    // The guard that keeps the arrangement honest: equal ids here would make the
    // case pass on the implementation it exists to refuse.
    expect(entries[0].factorId).not.toBe(entries[1].factorId);

    api.getAccountKeys.mockReturnValue(of(entries));

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
      of([
        await entryFor(otherKek, FIRST_FACTOR_ID, keys),
        await entryFor(otherKek, SECOND_FACTOR_ID, keys),
      ]),
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
    api.getAccountKeys.mockReturnValue(of([]));
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

  it('reads a refused body as unreachable, not as a factor that did not open', async () => {
    // Arrange
    // The failure the two-word split is least obvious about. `getAccountKeys`
    // refuses a body it cannot read, and that refusal arrives at exactly the
    // same place a network failure does — which is right: this client could not
    // read what came back, and that says nothing whatever about the factor the
    // person presented. Telling them to go and find their recovery card over a
    // version skew is the worse of the two wrong answers.
    api.getAccountKeys.mockReturnValue(
      throwError(
        () =>
          new Error(
            'The account-key response did not arrive as a list of factors.',
          ),
      ),
    );
    const kek = await keyEncryptionKey(0x79);

    // Act
    custody.unlock(kek);
    await settled(custody);

    // Assert
    expect(custody.unlockFailure()).toBe('unreachable');
  });

  it('returns nothing at all from unlock, and nothing anybody can await', async () => {
    // Arrange
    api.getAccountKeys.mockReturnValue(of([]));
    const kek = await keyEncryptionKey(0x7a);

    // Act
    const returned = custody.unlock(kek);

    // Assert
    // **`void`, and it is enforcement rather than a signature that happens to be
    // convenient.** A `Promise<void>` is awaitable, and the caller this will
    // have is a sign-in: somebody would await it, and a round trip would land on
    // the path between a verified assertion and the app. One refactor later that
    // `await` grows a `catch`, and a key that did not open becomes an
    // authentication that failed — which must never happen, because only
    // `anonymous` may bounce anybody out of an account.
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
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x7f);
    const answer = new Subject<readonly AccountKeyEntry[]>();
    const entries = [await entryFor(kek, FIRST_FACTOR_ID, keys)];

    api.getAccountKeys.mockReturnValue(answer);

    // Act
    custody.unlock(kek);
    expect(custody.status()).toBe('unlocking');

    custody.lock();
    expect(custody.status()).toBe('locked');

    answer.next(entries);
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
    const keys = generateAccountKeys();
    const kek = await keyEncryptionKey(0x80);
    const entries = [await entryFor(kek, FIRST_FACTOR_ID, keys)];

    api.getAccountKeys.mockReturnValue(of(entries));

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

    // Two imports and no more: the content key's and the index key's. A third
    // would mean a key was made somewhere this file is not looking.
    expect(atImportTime).toHaveLength(2);

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
});
