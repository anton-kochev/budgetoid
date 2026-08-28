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
import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  MeApiService,
  type AccountKeyEntry,
} from '@app-core/api/me-api.service';
import { SessionService } from '@app-core/session/session.service';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
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

// The service's own source, for the two rules below that nothing running can
// observe. `process.cwd()` is the project root under this runner, the same
// anchor `key-import-single-source.spec.ts` uses, and `src/` is read rather
// than the emitted bundle: the claim is about what a reviewer reads, and a
// minifier that renamed a `#` field would answer the question wrongly whichever
// way it answered it.
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
    const entries = [await entryFor(kek, FIRST_FACTOR_ID, keys)];

    api.getAccountKeys.mockReturnValue(of(entries));

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

    // Sorted, because the two imports are issued in one `Promise.all` and their
    // order is the platform's business rather than this rule's. One of each,
    // and no third: an implementation that sent both keys through one door is
    // caught by the *set* and not by the count.
    expect([...algorithms].sort()).toEqual(['AES-GCM', 'HMAC']);
  });

  // **One instance for the whole application, and the widening that breaks it
  // is one word.** `providedIn: 'any'` reads as the harmless relaxation — it is
  // literally "whatever injector asks" — and what it does is give every lazily
  // loaded part of the route table its own custody. A person unlocks the
  // account on `/welcome`, walks into `/app`, and the screen there asks an
  // instance that has never held a key. Nothing goes red; the only symptom is
  // an account that was readable a moment ago and is not now, with no ceremony
  // on screen to open it again.
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
    const answer = new Subject<readonly AccountKeyEntry[]>();
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
    answer.next([]);
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

  // **This case reads source text rather than behaviour, and says so.**
  //
  // `#forget` dropping its two `= null` assignments is the central promise of
  // this class broken — `lock()` is documented to *drop* the keys, not merely
  // to stop admitting to them — and there is no way to observe it from outside.
  // That is by design and is the property the class is built on: no public
  // member returns a key, `#` fields are unreachable from outside the class
  // body by the language, and the header argues at length that an accessor
  // added to make this checkable would be the very defect it is checking for.
  // So the only witness is the shape of what was written.
  //
  // What this cannot catch, stated rather than papered over:
  //
  //   * a `#forget` that nulls the fields and then puts the keys back — the
  //     text is a presence check, not a reading of what the method does;
  //   * a third key field added later and not nulled, because the two names are
  //     written here rather than derived from the class;
  //   * the assignments moved into a helper `#forget` calls, which is a correct
  //     refactor this case would call a failure. That is the cost of the
  //     technique and it is accepted: a red bar that a reader has to think
  //     about is the right price for a rule with no other witness.
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
});
