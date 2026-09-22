// The material a key rotation runs on, assembled from one passkey ceremony and
// two reads.
//
// **Nothing here stands up an injector, a component or an HTTP fake**, and that
// is the property the module under test was shaped to have rather than an
// economy in this file. A run's domain logic is two key generations, a factor
// set and an epoch; if any of it needed `TestBed` it would mean the rules had
// been written into a service instead of beside the ciphers they are about.
//
// **The account here is real, not a fixture of shapes.** Every factor is minted
// by `mintFactorKeypair`, every manifest is sealed by `sealFactorManifest`, and
// every refusal case below is a *perturbation* of a coherent account rather
// than a hand-built body. That is what keeps the cases honest: a refusal case
// that built its own malformed value could be refused for a reason the
// production path never reaches.
//
// **Building a factor set here is not the thing
// `factor-public-key-single-source.spec.ts` forbids.** That census excludes
// `.spec.ts` by suffix, deliberately, because a manifest with no factors in it
// pins nothing. The module under test reads every point off
// `openFactorManifest`'s answer and builds none, which is why it is not an owner
// over there.
import { describe, expect, it, beforeAll } from 'vitest';

import type {
  KeyRotationStateDto,
  RotationSealBody,
  StagedRotationDto,
} from '@app-core/api/key-rotation-api.service';
import type {
  AccountKeyCustodyDto,
  AccountKeyEntry,
} from '@app-core/api/me-api.service';
import {
  ACCOUNT_KEY_BYTES,
  generateAccountKeys,
  importAesGcmKey,
  type AccountKeys,
} from './account-keys';
import { mintFactorId } from './factor-id';
import {
  FACTOR_PUBLIC_KEY_BYTES,
  mintFactorKeypair,
  openFactorKeypair,
} from './factor-keypair';
import {
  openFactorManifest,
  sealFactorManifest,
  type FactorPublicKey,
} from './factor-manifest';
import {
  assembleKeyRotationBegin,
  assembleKeyRotationResume,
  KeyRotationMaterialError,
  type KeyRotationMaterial,
} from './key-rotation-material';

// The generation the account's manifest is in when a run begins. Four rather
// than one, so a case that read the epoch as a constant would be visible.
const EPOCH = 4;

// Nothing staged. The one state in which a fresh generation may be minted.
const NO_ROTATION: KeyRotationStateDto = { rotation: null };

// A point that is the right *encoding* and is not on the curve: 65 bytes
// leading `0x04`, both coordinates zero. `requireUncompressedPoint` accepts it —
// it checks a length and a leading byte and does no curve arithmetic, by
// design — and `crypto.subtle.importKey` refuses it, which is IFR-023 doing the
// half the encoding guard deliberately leaves to the platform. `(0, 0)`
// satisfies `y² = x³ - 3x + b` only if `b` is zero, and P-256's is not, so this
// is off the curve by arithmetic rather than by luck. A flipped byte of a real
// point would be off the curve about half the time, which is not a test.
const OFF_CURVE_POINT = (() => {
  const point = new Uint8Array(FACTOR_PUBLIC_KEY_BYTES);
  point[0] = 0x04;

  return point;
})();

// One factor of the fixture account: what the route serves for it, and the
// point its manifest entry carries.
interface MintedFactor {
  readonly factorId: string;
  readonly entry: AccountKeyEntry;
  readonly point: Uint8Array;
}

// A whole account: the key-encryption key one factor ceremony would yield, the
// account's own two keys, and the body `GET /api/me/account-keys` answers.
interface Account {
  readonly keyEncryptionKey: CryptoKey;
  readonly keys: AccountKeys;
  readonly contentKey: CryptoKey;
  readonly factors: readonly MintedFactor[];
  readonly custody: AccountKeyCustodyDto;
}

let account: Account;

// A key-encryption key that is a function of its seed, so two different seeds
// are two different factors' worth of key material and the same seed is the
// same key twice.
async function keyEncryptionKeyFrom(seed: number): Promise<CryptoKey> {
  return importAesGcmKey(new Uint8Array(ACCOUNT_KEY_BYTES).fill(seed));
}

async function mintFactor(
  keyEncryptionKey: CryptoKey,
  keys: AccountKeys,
): Promise<MintedFactor> {
  const factorId = mintFactorId();
  const minted = await mintFactorKeypair(keyEncryptionKey, factorId, keys);

  return {
    factorId,
    entry: {
      factorId,
      wrappedPrivateKey: minted.wrappedPrivateKey,
      encapsulatedAccountKeys: minted.encapsulatedAccountKeys,
    },
    point: minted.publicKey,
  };
}

function declaredSetOf(
  factors: readonly MintedFactor[],
): readonly FactorPublicKey[] {
  return factors.map((factor) => ({
    factorId: factor.factorId,
    publicKey: factor.point,
  }));
}

// The account's own content key as a key object, from a **copy** of the bytes:
// `importAesGcmKey` wipes what it is handed, so passing the account's own array
// would leave the fixture holding zeroes for every case after the first.
function contentKeyOf(keys: AccountKeys): Promise<CryptoKey> {
  return importAesGcmKey(Uint8Array.from(keys.contentKey));
}

async function buildAccount(): Promise<Account> {
  const keyEncryptionKey = await keyEncryptionKeyFrom(0x11);
  const keys = generateAccountKeys();
  const factors = [
    await mintFactor(keyEncryptionKey, keys),
    await mintFactor(keyEncryptionKey, keys),
    await mintFactor(keyEncryptionKey, keys),
  ];
  const contentKey = await contentKeyOf(keys);

  return {
    keyEncryptionKey,
    keys,
    contentKey,
    factors,
    custody: {
      manifest: await sealFactorManifest(
        contentKey,
        declaredSetOf(factors),
        EPOCH,
      ),
      rotationEpoch: EPOCH,
      factors: factors.map((factor) => factor.entry),
    },
  };
}

// A staged run as `GET /api/me/key-rotation` hands one back. Everything a
// resuming client cannot get anywhere else is a parameter; the rest is filler
// this module never reads.
function stagedRun(
  stagedManifest: string,
  stagedRotationEpoch: number,
  seals: readonly RotationSealBody[],
): StagedRotationDto {
  return {
    rotationId: '3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d',
    stagedRotationEpoch,
    stagedManifest,
    startedAtUtc: '2026-01-01T00:00:00Z',
    inventory: {
      accounts: 1,
      payees: 2,
      categoryGroups: 3,
      categories: 4,
      transactions: 5,
      budgets: 1,
    },
    maxChunkBytes: 65536,
    seals,
  };
}

// The refusal a call made, or a failure saying it made none. Written as a
// helper rather than `rejects.toThrow` because every case below asserts on the
// **reason** and on which refusal fired, and `toThrow` over a message is a
// match on prose.
async function refusalFrom(
  call: Promise<KeyRotationMaterial>,
): Promise<KeyRotationMaterialError> {
  try {
    await call;
  } catch (error: unknown) {
    if (error instanceof KeyRotationMaterialError) {
      return error;
    }

    throw error;
  }

  throw new Error(
    'The call produced material where it owed a refusal, so nothing below was judged.',
  );
}

function factorIdsOf(
  entries: readonly { readonly factorId: string }[],
): string[] {
  return entries.map((entry) => entry.factorId).sort();
}

// A coherent staged run, built the way a previous begin would have left one:
// the material this module assembles for a fresh rotation *is* what that begin
// posted.
async function stagedFromAMint(): Promise<{
  readonly begun: KeyRotationMaterial;
  readonly staged: StagedRotationDto;
}> {
  const begun = await assembleKeyRotationBegin(
    account.keyEncryptionKey,
    account.custody,
    NO_ROTATION,
  );

  return {
    begun,
    staged: stagedRun(begun.manifest, begun.rotationEpoch, begun.seals),
  };
}

// The account after one of its factors was revoked while a run was in flight.
//
// **The manifest moves with the set and the stored epoch moves with the
// manifest**, because the path that revokes a passkey *promotes* the manifest
// in the unit of work it already had. A fixture that dropped the entry alone
// would meet the *served set is not the declared set* refusal instead of the
// moved-set state it meant to arrange — and one that left the epoch where it
// was could not tell a derived epoch from a restated one.
async function afterRevoking(revoked: MintedFactor): Promise<{
  readonly surviving: readonly MintedFactor[];
  readonly custody: AccountKeyCustodyDto;
}> {
  const surviving = account.factors.filter((factor) => factor !== revoked);

  return {
    surviving,
    custody: {
      manifest: await sealFactorManifest(
        account.contentKey,
        declaredSetOf(surviving),
        EPOCH + 1,
      ),
      rotationEpoch: EPOCH + 1,
      factors: surviving.map((factor) => factor.entry),
    },
  };
}

beforeAll(async () => {
  account = await buildAccount();
}, 30000);

describe('assembling a key rotation', () => {
  describe('the generation still in force', () => {
    it("hands back the content key the account's manifest is sealed under", async () => {
      // Arrange, Act
      const material = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );

      // Assert
      // The manifest opens under it, which is the one runtime check anywhere
      // that this really is the content key rather than the index key wearing
      // its name — the two halves of an encapsulated value are told apart by
      // position alone.
      await expect(
        openFactorManifest(
          material.current.contentKey,
          account.custody.manifest ?? '',
          EPOCH,
        ),
      ).resolves.toHaveLength(account.factors.length);
    });

    it('sends each of the two keys through its own door', async () => {
      // Arrange, Act
      const material = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );

      // Assert
      // A content key that went through the HMAC door cannot encrypt and a
      // index key that went through the AES door cannot sign, and neither
      // mistake is visible until the first row is re-sealed.
      expect(material.current.contentKey.algorithm.name).toBe('AES-GCM');
      expect(material.current.indexKey.algorithm.name).toBe('HMAC');
      expect(material.next.contentKey.algorithm.name).toBe('AES-GCM');
      expect(material.next.indexKey.algorithm.name).toBe('HMAC');
    });
  });

  describe('the generation a run carries to every factor', () => {
    it('is minted, and filed one epoch on, when nothing is staged', async () => {
      // Arrange, Act
      const material = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );

      // Assert
      expect(material.rotationEpoch).toBe(EPOCH + 1);

      // It is a *different* generation, which a round trip alone cannot see: a
      // run that re-encapsulated the keys already in force would produce values
      // of exactly the right width that open perfectly and rotate nothing.
      await expect(
        openFactorManifest(
          account.contentKey,
          material.manifest,
          material.rotationEpoch,
        ),
      ).rejects.toThrow();
      await expect(
        openFactorManifest(
          material.next.contentKey,
          material.manifest,
          material.rotationEpoch,
        ),
      ).resolves.toHaveLength(account.factors.length);
    });

    it('is encapsulated to every factor the manifest names, and to no other', async () => {
      // Arrange, Act
      const material = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );

      // Assert
      expect(factorIdsOf(material.seals)).toEqual(factorIdsOf(account.factors));
      expect(factorIdsOf(material.factors)).toEqual(
        factorIdsOf(account.factors),
      );
    });

    it('is what each factor really opens, paired with the private half already stored', async () => {
      // Arrange
      const material = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );

      for (const factor of account.factors) {
        const seal = material.seals.find(
          (candidate) => candidate.factorId === factor.factorId,
        );

        // Act
        // The staged value beside the private key already on file — the two
        // open together although they were never written together, which is
        // the whole of what a rotation promises an authenticator it never saw.
        const opened = await openFactorKeypair(
          account.keyEncryptionKey,
          factor.factorId,
          {
            wrappedPrivateKey: factor.entry.wrappedPrivateKey,
            encapsulatedAccountKeys: seal?.encapsulatedAccountKeys ?? '',
          },
        );

        // Assert
        // And what comes out is the generation the staged manifest is sealed
        // under, which is what makes the seal set and the manifest one run
        // rather than two values of the right shape.
        await expect(
          openFactorManifest(
            await importAesGcmKey(opened.contentKey),
            material.manifest,
            material.rotationEpoch,
          ),
        ).resolves.toHaveLength(account.factors.length);
      }
    });
  });

  describe('a resume over a run already in flight', () => {
    it("carries the staged run's generation forward rather than drawing one", async () => {
      // Arrange
      const { begun, staged } = await stagedFromAMint();

      // Act
      const resumed = await assembleKeyRotationResume(
        account.keyEncryptionKey,
        account.custody,
        staged,
      );

      // Assert
      // The staged seals are the only copy of the generation every row this run
      // already rewrote is sealed under. A freshly drawn generation here would
      // leave each of those rows opening under nothing at all, silently, with
      // no error and no repair path — so the recovered content key has to be
      // the one the staged manifest was sealed under.
      await expect(
        openFactorManifest(
          resumed.next.contentKey,
          staged.stagedManifest,
          staged.stagedRotationEpoch,
        ),
      ).resolves.toHaveLength(account.factors.length);
      expect(resumed.rotationEpoch).toBe(begun.rotationEpoch);
    });

    it('restates the staged manifest and the staged seals byte for byte', async () => {
      // Arrange
      const { staged } = await stagedFromAMint();

      // Act
      const resumed = await assembleKeyRotationResume(
        account.keyEncryptionKey,
        account.custody,
        staged,
      );

      // Assert
      // Re-sealing would produce a manifest of the right shape over the right
      // set under a fresh nonce, which is a *different* value for the run the
      // server already has on file.
      expect(resumed.manifest).toBe(staged.stagedManifest);
      expect(resumed.seals).toEqual(staged.seals);
    });
  });

  // The repair a `factor_set_moved` points at: a begin pressed over a run that
  // is still staged, for a factor set the account no longer has.
  describe('a begin over a run already in flight', () => {
    it('carries the staged generation to every factor the account holds now, and to no other', async () => {
      // Arrange
      const { staged } = await stagedFromAMint();
      const { surviving, custody } = await afterRevoking(account.factors[2]);

      // Act
      const repaired = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        custody,
        { rotation: staged },
      );

      // Assert
      // Set equality in both directions: a seal still naming the revoked factor
      // and a live factor with no seal are two different failures.
      expect(factorIdsOf(repaired.seals)).toEqual(factorIdsOf(surviving));

      for (const factor of surviving) {
        const seal = repaired.seals.find(
          (candidate) => candidate.factorId === factor.factorId,
        );
        // The staged value beside the private key already on file, and what
        // comes out opens the manifest the *interrupted* run filed — which is
        // the whole claim: this is that run's generation and not a new one, and
        // every surviving factor now holds it.
        const opened = await openFactorKeypair(
          account.keyEncryptionKey,
          factor.factorId,
          {
            wrappedPrivateKey: factor.entry.wrappedPrivateKey,
            encapsulatedAccountKeys: seal?.encapsulatedAccountKeys ?? '',
          },
        );

        await expect(
          openFactorManifest(
            await importAesGcmKey(opened.contentKey),
            staged.stagedManifest,
            staged.stagedRotationEpoch,
          ),
        ).resolves.toHaveLength(account.factors.length);
      }
    });

    it('seals a manifest over the live set, at the epoch a begin files at', async () => {
      // Arrange
      // The revoke promoted the account's manifest, so the stored epoch has
      // already moved past the one the staged run is filed at — which is what
      // makes this case able to tell a derived epoch from a restated one.
      const { staged } = await stagedFromAMint();
      const { surviving, custody } = await afterRevoking(account.factors[2]);

      // Act
      const repaired = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        custody,
        { rotation: staged },
      );

      // Assert
      expect(repaired.rotationEpoch).toBe(custody.rotationEpoch + 1);
      expect(repaired.rotationEpoch).not.toBe(staged.stagedRotationEpoch);
      // Restating the staged manifest would post a set naming the revoked
      // factor, which is the refusal this press exists to get out of.
      expect(repaired.manifest).not.toBe(staged.stagedManifest);

      const declared = await openFactorManifest(
        repaired.next.contentKey,
        repaired.manifest,
        repaired.rotationEpoch,
      );

      expect(factorIdsOf(declared)).toEqual(factorIdsOf(surviving));
    });

    it('refuses when the presented factor holds no staged seal while another does', async () => {
      // Arrange
      // A factor enrolled after the run began holds nothing this call can open,
      // and another factor still carries the generation — so the way forward
      // really is a different authenticator.
      const { staged } = await stagedFromAMint();
      const opening = account.custody.factors[0].factorId;

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(account.keyEncryptionKey, account.custody, {
          rotation: stagedRun(
            staged.stagedManifest,
            staged.stagedRotationEpoch,
            staged.seals.filter((seal) => seal.factorId !== opening),
          ),
        }),
      );

      // Assert
      expect(refusal.reason).toBe('unopened');
      expect(refusal.message).toContain(opening);
    });

    it('refuses when the staged run sealed nothing for any factor this account still holds', async () => {
      // Arrange
      // A run really staged by an account whose only factor is one this account
      // does not have: every byte of it is genuine, and not one of its seals
      // names a factor still enrolled here. The generation it re-sealed rows
      // under is gone for good, and no factor of this account brings it back.
      const strangerKey = await keyEncryptionKeyFrom(0x33);
      const stranger = await mintFactor(strangerKey, account.keys);
      const elsewhere = await assembleKeyRotationBegin(
        strangerKey,
        {
          manifest: await sealFactorManifest(
            account.contentKey,
            declaredSetOf([stranger]),
            EPOCH,
          ),
          rotationEpoch: EPOCH,
          factors: [stranger.entry],
        },
        NO_ROTATION,
      );

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(account.keyEncryptionKey, account.custody, {
          rotation: stagedRun(
            elsewhere.manifest,
            elsewhere.rotationEpoch,
            elsewhere.seals,
          ),
        }),
      );

      // Assert
      // **Not `unopened`.** That word means *another factor may well work*, and
      // it would send somebody through a whole recovery card over a state no
      // card touches: every factor that held this generation is gone, and the
      // rows an earlier chunk re-sealed under it were stranded when the last of
      // them went.
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('no factor this account still holds');
    });
  });

  describe('the refusals, each of which produces nothing at all', () => {
    it('refuses when no factor of the account opens under the key presented', async () => {
      // Arrange
      const stranger = await keyEncryptionKeyFrom(0x22);

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(stranger, account.custody, NO_ROTATION),
      );

      // Assert
      expect(refusal.reason).toBe('unopened');
    });

    it('refuses a response carrying no manifest', async () => {
      // Arrange
      // The state the route really answers for an account that has none, which
      // is also the one body that switches every refusal below off at once.
      const custody: AccountKeyCustodyDto = {
        ...account.custody,
        manifest: null,
        rotationEpoch: 0,
      };

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(
          account.keyEncryptionKey,
          custody,
          NO_ROTATION,
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('no factor manifest');
    });

    it('refuses a manifest that does not open under the content key a factor handed over', async () => {
      // Arrange
      // Sealed under somebody else's content key: correctly framed, the right
      // width, and authenticated by a key this account has never held.
      const custody: AccountKeyCustodyDto = {
        ...account.custody,
        manifest: await sealFactorManifest(
          await contentKeyOf(generateAccountKeys()),
          declaredSetOf(account.factors),
          EPOCH,
        ),
      };

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(
          account.keyEncryptionKey,
          custody,
          NO_ROTATION,
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('did not open');
    });

    it('refuses a served factor the manifest does not name', async () => {
      // Arrange
      // A row somebody added: its envelopes are correctly framed and open
      // perfectly under the key-encryption key whoever wrote it chose, which is
      // exactly why the rows beside a manifest are not evidence.
      const stranger = await mintFactor(account.keyEncryptionKey, account.keys);
      const custody: AccountKeyCustodyDto = {
        ...account.custody,
        factors: [...account.custody.factors, stranger.entry],
      };

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(
          account.keyEncryptionKey,
          custody,
          NO_ROTATION,
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('The account serves');
    });

    it('refuses a factor the manifest names that the response did not serve', async () => {
      // Arrange
      // The other direction, and it is a different event rather than this
      // rule's mirror: a row somebody removed, or a set this client is being
      // shown half of. Checked one way only, it passes cleanly.
      const custody: AccountKeyCustodyDto = {
        ...account.custody,
        factors: account.custody.factors.slice(1),
      };

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(
          account.keyEncryptionKey,
          custody,
          NO_ROTATION,
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('The account serves');
    });

    it('refuses a factor whose public key will not encapsulate, and stages nothing for the rest', async () => {
      // Arrange
      // One entry of the manifest carries a point of exactly the right
      // encoding that is not on the curve. Every served row is real, so the
      // set comparison above passes and this is the first thing that can fire.
      const [first, ...rest] = account.factors;
      const declared: readonly FactorPublicKey[] = [
        { factorId: first.factorId, publicKey: OFF_CURVE_POINT },
        ...declaredSetOf(rest),
      ];
      const custody: AccountKeyCustodyDto = {
        ...account.custody,
        manifest: await sealFactorManifest(account.contentKey, declared, EPOCH),
      };

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationBegin(
          account.keyEncryptionKey,
          custody,
          NO_ROTATION,
        ),
      );

      // Assert
      // There is no partial answer to inspect, and that is the point: a run
      // that staged the two factors it could reach and left the third out is
      // the silent orphaning the whole scheme exists to prevent, so the call
      // has one way out and it produces no material.
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain(first.factorId);
    });

    it('refuses a resume when the run in flight staged nothing for the factor presented', async () => {
      // Arrange
      // The factor whose key-encryption key this is holds no staged seal — a
      // factor enrolled after the run began. Another factor may still carry
      // the generation, so the way forward is a different authenticator.
      const begun = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );
      const opening = account.custody.factors[0].factorId;
      const seals = begun.seals.filter((seal) => seal.factorId !== opening);

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationResume(
          account.keyEncryptionKey,
          account.custody,
          stagedRun(begun.manifest, begun.rotationEpoch, seals),
        ),
      );

      // Assert
      expect(refusal.reason).toBe('unopened');
      expect(refusal.message).toContain(opening);
    });

    it('refuses a staged run whose seals leave out a factor its manifest declares', async () => {
      // Arrange
      // The comparison `key-rotation.md` says nothing on either side of the
      // wire holds: the staged manifest names three factors and the staged
      // seals cover two. The factor that opens keeps its seal, so the
      // generation is recoverable and this is the first thing that can fire.
      const begun = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );
      const dropped = account.custody.factors[1].factorId;

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationResume(
          account.keyEncryptionKey,
          account.custody,
          stagedRun(
            begun.manifest,
            begun.rotationEpoch,
            begun.seals.filter((seal) => seal.factorId !== dropped),
          ),
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('seal set');
    });

    it('refuses a staged run whose seals name a factor its manifest does not', async () => {
      // Arrange
      // The other direction, over the same pair. A manifest naming a smaller
      // set than the seals that travelled with it promotes an account into a
      // factor set it never agreed to.
      const begun = await assembleKeyRotationBegin(
        account.keyEncryptionKey,
        account.custody,
        NO_ROTATION,
      );
      const narrowed = await sealFactorManifest(
        begun.next.contentKey,
        declaredSetOf(account.factors.slice(1)),
        begun.rotationEpoch,
      );

      // Act
      const refusal = await refusalFrom(
        assembleKeyRotationResume(
          account.keyEncryptionKey,
          account.custody,
          stagedRun(narrowed, begun.rotationEpoch, begun.seals),
        ),
      );

      // Assert
      expect(refusal.reason).toBe('inconsistent');
      expect(refusal.message).toContain('seal set');
    });
  });
});
