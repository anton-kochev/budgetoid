// What a key rotation runs on, assembled from one passkey ceremony and the two
// reads a browser has already made.
//
// A run needs four things and none of them can be had from a response alone:
// the generation still in force, so the rows sitting there now can be opened;
// the generation everything will be rewritten under; every factor's public key,
// so that generation reaches each of them; and the epoch the next manifest is
// filed at. This module produces those or refuses. It performs no HTTP, holds
// nothing between calls, and writes nowhere; the one thing it reads beyond its
// arguments is this device's rotation-epoch record, and only on a begin.
//
// **Nothing here is Angular and nothing here ever may be** — no `inject()`, no
// decorator, no signal, no injectable. The two imports that reach into
// `+core/api` are `import type` over the wire records and are erased, exactly as
// `me-api.service.ts` reaches the other way for `FactorKeypairEnvelopes`. What
// that buys is that a rotation's domain rules are testable with no service, no
// component and no HTTP fake: the whole of this file is two key generations, a
// factor set and an epoch, and the day any of it wants a `TestBed` is the day a
// rule has been written into a driver instead of beside the ciphers it is
// about.
//
// **Whether this shares the unlock gate, which is the question worth answering
// before anything below is read.** `AccountKeyCustodyService.manifestRefusal`
// runs four ordered refusals over the very same response, and a second, weaker
// definition of that rule is how a gate rots — so the copy below is a
// deliberate answer rather than an oversight. It cannot be shared, for three
// reasons that each hold on their own. That method is `private static` on a
// decorated class, so nothing outside that file can reach it. Its answer is
// `Extract<UnlockFailure, …>` — custody's own vocabulary, which is about *what a
// person does next to get back into their account*, a question a rotation does
// not ask. And it **discards the points**, returning a word where this caller
// needs the factor set itself, which is the whole of what it came for. What
// could have been shared is the set-equality predicate underneath, and its
// natural home is `factor-manifest.ts`, whose export census is closed at five
// names and argues in its own words against a third function there. Moving it
// is a decision with its own commit; inventing a fourth module to hold one
// `every` in two directions is not that decision taken, it is that decision
// dodged. So the rule is written once *here*, used by both of the comparisons
// below, and the difference from custody's copy is recorded in this paragraph
// rather than left for somebody to find.
//
// **Two entry points, one per caller, and minting is unreachable from either
// while a run is staged.** A begin that replaces a run already in flight must
// carry that run's generation forward and must never draw a fresh one: the
// staged seals are the only copy of the generation every row that run already
// rewrote is sealed under, a second begin overwrites them in place, and from
// that moment each of those rows opens under nothing at all — silently, with no
// error, no SQLSTATE and no repair path. Nothing in the product enforces that
// today. What enforces it here is that there is no way to say it.
// {@link assembleKeyRotationBegin} is handed `GET /api/me/key-rotation`'s own
// answer and decides on `rotation === null`, so a draw is reachable in exactly
// the one case the rule permits; {@link assembleKeyRotationResume} is handed
// the staged run itself rather than the read, so it has no null case to branch
// on and no draw anywhere on its path. A boolean over one function was the
// other shape and is weaker — a caller holding a staged run can pass the wrong
// value, which is the mistake, spelled with one extra character.
//
// **The split is by caller and not by state, because the two callers want
// different things from the same staged run.**
//
//   * **A resume finishes *this* run**, so it restates the staged manifest and
//     the staged seals byte for byte. Re-sealing would produce a manifest of the
//     right shape over the right set under a fresh nonce, which is a *different*
//     value for a run the server already has on file; re-encapsulating would
//     replace the staged seals with new values of the generation they already
//     carry, for no gain and one more chance to differ. What a resume recovers
//     is the *keys*; what it carries is what was staged.
//   * **A begin over the same run is the repair a `factor_set_moved` points
//     at.** It recovers that same generation and then encapsulates it to the
//     factor set the account holds **now**, under a manifest sealed over that
//     set at the epoch a begin files at. Restating there would post a seal set
//     naming the factors that were enrolled when the run began, which is the
//     refusal the person pressed the control to get out of.
//
// **A staged generation no live factor can recover is `inconsistent`, and that
// word is a decision rather than a fall-through.** Recovering it needs a factor
// present in **both** the staged seal set and the live set. If every factor that
// run staged a seal for is gone, nothing anywhere holds that generation, and
// every row an earlier chunk already re-sealed under it is stranded — which
// happened when the last of those factors went, and nothing on this path can
// undo it. `unopened` would be a lie of exactly the kind the two words exist to
// prevent: it means *another factor may well work*, and here it would send
// somebody through a whole recovery card over a state no card touches.
// `inconsistent` is the word that says out loud that nothing the person holds
// changes the answer, which is true here to the letter. A third word is refused
// for the reason the union below gives: it would be one more shade of *nothing
// you hold will change this*, and the driver renders both of these as one
// sentence anyway.
//
// **The refusals are ordered rather than arranged**, each reading something a
// refusal before it proved, and every one of them aborts with no material at
// all. A partial answer here is a run begun against material nobody verified.
//
//   1. A response carrying no manifest, or a manifest that does not open under
//      the content key a factor just handed over. The rows beside a manifest
//      are not evidence — whoever can write the database can add one of their
//      own, wrapped under a key-encryption key they chose, and every envelope in
//      it is correctly framed and opens perfectly. The blob is sealed under a
//      key the server has never held, so opening it is what proves the content
//      key is the content key.
//   2. A served factor set that is not the set the manifest declares, **in both
//      directions**. The two are different events and neither is the other's
//      mirror: a served factor the manifest does not account for is a row
//      somebody added, and a declared factor that was not served is a row
//      somebody removed or a set this client is being shown half of. Checked one
//      way only, the other passes cleanly.
//   3. On a begin only, a served epoch below the highest this device has
//      recorded for the account. Refusals 1 and 2 pass a read replayed from
//      before a revocation, revoked factor and all, because it is not forged —
//      only a device that watched the account move past it can tell. Compared
//      only once refusal 1 has authenticated the epoch, and recorded never.
//   4. A factor whose public key will not take the next generation. Abort, and
//      stage nothing — a run that sealed the factors it could reach and left one
//      out is the silent orphaning the whole key-pair scheme exists to prevent,
//      discovered by whoever reaches for that factor, which is by definition the
//      moment they have lost the others.
//   5. A seal set that is not exactly the set the staged manifest declares, both
//      directions again. `key-rotation.md` says outright that nothing on either
//      side of the wire holds this comparison and that the first client to begin
//      a run owes it; this is that client. On a resume it judges what the
//      previous begin really posted. On a begin it is a self-check over what is
//      about to be posted — the manifest is opened again rather than compared
//      against the list it was built from, so what passes is a value that
//      round-tripped, not a variable that was reused.
//
// **No point is built here.** Every one comes off `openFactorManifest`'s answer
// and is passed straight to `encapsulateAccountKeysTo`, which is why
// `factor-public-key-single-source.spec.ts` gives this file no standing and must
// never be made to: a point read off an account-keys response and paired with
// the factor id beside it is two lines, compiles, produces envelopes that all
// open, and is a rotation sealed to keys the account's owner does not hold.
//
// **What is not here, and belongs to the driver.** Nothing posts, nothing reads
// a conflict, nothing renders a word, and nothing decides how many rows travel
// in a chunk. `key-rotation-api.service.ts` is the transport and
// `write-outcome.ts` is what turns a refused write into something a screen says.
import type {
  KeyRotationStateDto,
  RotationSealBody,
  StagedRotationDto,
} from '@app-core/api/key-rotation-api.service';
import type {
  AccountKeyCustodyDto,
  AccountKeyEntry,
} from '@app-core/api/me-api.service';
// The two doors, and the draw. The account's keys leave this module as
// `CryptoKey` and never as bytes — the material is a local of whichever function
// produced it and dies inside the import in the same statement that produces a
// key, which is `account-keys.ts`' own rule applied at a second caller.
import {
  generateAccountKeys,
  importAesGcmKey,
  importHmacSha256Key,
  type AccountKeys,
} from './account-keys';
// The read half of a factor's key pair, and the half of a mint a rotation
// performs on its own: `encapsulateAccountKeysTo` needs no secret of the
// factor's, which is the entire reason a run can reach an authenticator that is
// in a drawer.
import { encapsulateAccountKeysTo, openFactorKeypair } from './factor-keypair';
// The account's own unforgeable statement of which factors it has, and the seal
// over the set the next generation is filed under.
import {
  openFactorManifest,
  sealFactorManifest,
  type FactorPublicKey,
} from './factor-manifest';
// The per-device high-water mark, read and never written. Two functions over
// `localStorage` with no Angular in them, so this file stays framework-free.
import { highestRotationEpochSeen } from './rotation-epoch-record';

/**
 * Why a run could not be assembled, and the two words are two different next
 * steps for a person.
 *
 *   * `unopened` — the factor presented opened nothing this call needed: no
 *     served entry opened under it, or the run in flight staged nothing it can
 *     open. Another factor may well work, and on the second of those it is the
 *     only thing that can — a factor enrolled after a run began holds no staged
 *     seal, while every factor that was there when it began holds one.
 *   * `inconsistent` — something opened, and what came out does not agree with
 *     itself. No factor of this account clears it: every factor encapsulates the
 *     same two keys, so another passkey and all ten recovery codes produce the
 *     same pair in every browser and after every reload.
 *
 * **Two words for two next steps, and the count is the rule rather than the
 * number.** Collapsed, somebody is sent through a whole recovery card over a
 * state no card touches. Split further — a word each for a missing manifest, a
 * tag that did not verify and a set disagreement — a person is handed four
 * shades of *nothing you hold will change this*, which is a diagnosis this
 * client cannot make. It is `UnlockFailure`'s discipline over a narrower
 * question, and deliberately not that union: the three words it leaves out are
 * about a read that failed, and nothing here reads anything.
 *
 * Which refusal fired is in the message, for a console. What a screen acts on
 * is the word.
 */
export type KeyRotationMaterialReason = 'unopened' | 'inconsistent';

/**
 * What this module throws, carrying the word a caller acts on.
 *
 * **A type rather than a bare `Error`, because a caller has to tell a refusal
 * this module made from a fault it did not.** `crypto.subtle` failing, a
 * `RangeError`, a bug — none of those is a statement about the account, and
 * swallowing them into a word would put a sentence about recovery factors in
 * front of somebody whose browser simply broke.
 *
 * **A class extending `Error` with a stable {@link name}**, the shape
 * `factor-manifest.ts` argues for in full: `instanceof` is the check, and the
 * `name` is the second answer for two copies of this module in one page. It is
 * written as a class *field* so it is an own, enumerable property, with the
 * measured limit that file records — `structuredClone` does not carry it.
 *
 * What refused is kept as the `cause` wherever there was one. The module
 * underneath says which of its rules the value broke, in words this file could
 * not improve on and has no business restating.
 */
export class KeyRotationMaterialError extends Error {
  public override readonly name = 'KeyRotationMaterialError';

  public readonly reason: KeyRotationMaterialReason;

  constructor(
    reason: KeyRotationMaterialReason,
    message: string,
    options?: ErrorOptions,
  ) {
    super(message, options);

    this.reason = reason;
  }
}

/**
 * One generation of the account's two keys, as key objects and never as bytes.
 *
 * The material exists inside this module for exactly as long as an
 * encapsulation needs it and is zero-filled on the way out of the door that
 * consumes it. A caller re-seals a row through the content key and keys a blind
 * index through the index key; neither operation wants the bytes, and a field
 * holding them would keep the plaintext of both of an account's keys alive for
 * the life of the tab.
 */
export interface AccountKeyGeneration {
  readonly contentKey: CryptoKey;
  readonly indexKey: CryptoKey;
}

/**
 * Everything a client needs to begin a run, or to carry on with one it lost.
 *
 * {@link manifest} and {@link seals} are the two values a begin posts, already
 * agreeing with each other — freshly built on either begin, restated byte for
 * byte on a resume. {@link factors} is the account's live factor set as its
 * **own** manifest declares it, which is the set a begin encapsulates to,
 * including the corrected one after a `factor_set_moved`.
 *
 * Both generations are here because a rotation needs both at once: the one in
 * force opens what is stored, and the next one is what it is all rewritten
 * under. That both are readable for as long as a run is in flight is the
 * correctness property the staging tables exist for, restated on this side.
 */
export interface KeyRotationMaterial {
  /** The generation every stored row is sealed under right now. */
  readonly current: AccountKeyGeneration;
  /** The generation every row will be rewritten under. */
  readonly next: AccountKeyGeneration;
  /** The account's factor set, as the manifest that opened declares it. */
  readonly factors: readonly FactorPublicKey[];
  /** The generation {@link manifest} is filed at. */
  readonly rotationEpoch: number;
  /** The next manifest, sealed under {@link next}'s content key. */
  readonly manifest: string;
  /** One copy of {@link next} per factor, encapsulated to that factor. */
  readonly seals: readonly RotationSealBody[];
}

// The factor a presented key-encryption key turned out to be, and what came out
// of it. The entry travels with the keys because everything downstream is bound
// to that row's own `factorId` — the staged seal is looked up by it, and both of
// its envelopes were sealed against it.
interface OpenedFactor {
  readonly entry: AccountKeyEntry;
  readonly keys: AccountKeys;
}

// What the account really is, before either entry point stages anything: the
// row that opened, the generation in force, and the set the manifest declares.
//
// **The entry travels rather than the opened keys**, because those are spent by
// the time this exists — `holdGeneration` wipes what it is handed. What is still
// needed of the row downstream is its `factorId`, to find the staged seal, and
// its live `wrappedPrivateKey`, to open that seal against.
interface LiveAccount {
  readonly entry: AccountKeyEntry;
  readonly current: AccountKeyGeneration;
  readonly factors: readonly FactorPublicKey[];
}

// The next generation and the three values that travel with it, whichever of
// the two paths produced them. One shape, so the fifth refusal is one function
// called once rather than a comparison written on each path.
interface StagedGeneration {
  readonly next: AccountKeyGeneration;
  readonly rotationEpoch: number;
  readonly manifest: string;
  readonly seals: readonly RotationSealBody[];
}

/**
 * Assembles the material a **begin** runs on, or refuses.
 *
 * `keyEncryptionKey` is what a factor ceremony just yielded. `custody` is
 * `GET /api/me/account-keys`' answer and `state` is `GET /api/me/key-rotation`'s;
 * **the second is what decides whether a generation is drawn or recovered**,
 * which is why it is the read itself rather than anything a caller computes from
 * it. See the head of this file for what that shape is worth.
 *
 * `budgetId` names the account whose rotation-epoch record `custody` is judged
 * against — the session's own tenancy, the value every blind index is keyed
 * under. It is the budget rather than the recorded epoch so that the record is
 * read beside the comparison, as the unlock gate reads it, and no caller holds a
 * number it could hand over softer. It is read and never written.
 *
 * **Over a run already in flight this is the repair**, not a second rotation: it
 * recovers the staged generation and encapsulates *that* to the factor set the
 * account holds now, under a manifest sealed over that set. A begin pressed
 * after a `factor_set_moved` is the whole reason it exists, and carrying the
 * generation forward is what makes the design chapter's *the records already
 * re-encrypted stay that way* true.
 *
 * The key-encryption key is a parameter and is not retained, copied or named
 * anywhere but this call's own frame. Nothing of the account's material outlives
 * the returned value.
 *
 * Rejects with {@link KeyRotationMaterialError} on every refusal this module
 * makes, and with whatever the platform threw on anything else.
 */
export async function assembleKeyRotationBegin(
  keyEncryptionKey: CryptoKey,
  custody: AccountKeyCustodyDto,
  state: KeyRotationStateDto,
  budgetId: string,
): Promise<KeyRotationMaterial> {
  const live = await liveAccountOf(keyEncryptionKey, custody);

  // Refusal 3, before anything is drawn or recovered.
  requireNoEpochBelowTheRecord(custody.rotationEpoch, budgetId);

  // **The epoch comes off the live read and never off the staged run.** A
  // manifest promoted by some other path while that run sat there — a passkey
  // revoked, which is the very event that sends somebody to this repair — has
  // already moved the stored epoch, and the server refuses a begin filed at
  // anything but stored + 1.
  const rotationEpoch = custody.rotationEpoch + 1;
  // **The one place in this module a generation is drawn**, and the branch
  // above it is the whole of what keeps it out of reach of a run in flight.
  const keys =
    state.rotation === null
      ? generateAccountKeys()
      : await recoverStagedKeys(keyEncryptionKey, live, state.rotation);
  // Refusal 4 is inside this call, over the **live** set either way.
  const run = await stageGeneration(keys, live.factors, rotationEpoch);

  // Refusal 5, over the pair that is about to be posted.
  await requireSealsCoverTheManifest(run);

  return materialOf(live, run);
}

/**
 * Assembles the material a **resume** runs on, or refuses.
 *
 * `staged` is the run `GET /api/me/key-rotation` handed back, and it is the
 * staged run rather than the read around it because a resume has nothing to say
 * about an account with nothing in flight — the caller has already gone back to
 * rest by then. What that buys over a second `KeyRotationStateDto` parameter is
 * that there is no arm on this path where a generation could be drawn, in the
 * type rather than in a comment.
 *
 * **It restates the staged manifest and the staged seals byte for byte**, for
 * the reason the head of this file gives. What it recovers is the keys.
 *
 * Rejects with {@link KeyRotationMaterialError} on every refusal this module
 * makes, and with whatever the platform threw on anything else.
 */
export async function assembleKeyRotationResume(
  keyEncryptionKey: CryptoKey,
  custody: AccountKeyCustodyDto,
  staged: StagedRotationDto,
): Promise<KeyRotationMaterial> {
  const live = await liveAccountOf(keyEncryptionKey, custody);
  const run: StagedGeneration = {
    next: await holdGeneration(
      await recoverStagedKeys(keyEncryptionKey, live, staged),
    ),
    // The epoch, the manifest and the seals are the run's own and are restated
    // rather than recomputed. A resume that re-derived the epoch from the
    // account-key read would agree today and would be a second arithmetic for
    // nobody to keep true.
    rotationEpoch: staged.stagedRotationEpoch,
    manifest: staged.stagedManifest,
    seals: staged.seals,
  };

  // Refusal 5, over what the previous begin really posted.
  await requireSealsCoverTheManifest(run);

  return materialOf(live, run);
}

// The account as it stands right now, judged before either entry point stages
// anything: the factor that opened, the generation in force, and the set the
// account's own manifest declares.
//
// **Refusals 1 and 2 are here, and they run before anything is drawn,
// recovered or encapsulated.** Everything downstream reaches this set, so a run
// staged against a set nobody authenticated hands the account's next generation
// to whoever wrote the extra row.
async function liveAccountOf(
  keyEncryptionKey: CryptoKey,
  custody: AccountKeyCustodyDto,
): Promise<LiveAccount> {
  // **Every entry, in turn, each under its own `factorId`.** An account holding
  // one passkey is answered with one entry, so the list of one is what a reader
  // optimises into `entries[0]` — and it works forever on that kind of account.
  // An ordinary account is a passkey beside a card of ten codes, answered with
  // eleven, of which exactly one opens under the factor just presented.
  const opened = await openOneFactor(keyEncryptionKey, custody.factors);

  if (opened === null) {
    // An empty list lands here too, which is right: the route answers no
    // factors both for a session it cannot see and for an account carrying
    // none, indistinguishably and on purpose.
    throw refusal(
      'unopened',
      'No factor of this account opened under the key presented.',
    );
  }

  const current = await holdGeneration(opened.keys);
  // Refusal 1.
  const factors = await declaredFactors(current.contentKey, custody);

  // Refusal 2.
  requireOneFactorSet(
    factors,
    custody.factors,
    'The account serves a factor set that is not the one its manifest declares.',
  );

  return { entry: opened.entry, current, factors };
}

// The two halves of the answer, put together in one place so that neither entry
// point can forget a member or fill one from the wrong side.
function materialOf(
  live: LiveAccount,
  run: StagedGeneration,
): KeyRotationMaterial {
  return {
    current: live.current,
    next: run.next,
    factors: live.factors,
    rotationEpoch: run.rotationEpoch,
    manifest: run.manifest,
    seals: run.seals,
  };
}

// The first entry that opens, or `null`.
//
// The `catch` is deliberately total and deliberately silent. A wrong factor, a
// private half that did not unwrap, an ephemeral point the curve refuses and a
// value that is not base64url are one symptom by design — exactly one entry
// opens and the rest fail to authenticate, which is the AEAD doing what the
// binding is for rather than an error worth telling apart.
async function openOneFactor(
  keyEncryptionKey: CryptoKey,
  entries: readonly AccountKeyEntry[],
): Promise<OpenedFactor | null> {
  for (const entry of entries) {
    try {
      // The entry itself, not two members picked off it: `AccountKeyEntry` is
      // composed from the interface this parameter is typed by, so there is no
      // call site that can pass the two envelopes in the wrong order.
      return {
        entry,
        keys: await openFactorKeypair(keyEncryptionKey, entry.factorId, entry),
      };
    } catch {
      continue;
    }
  }

  return null;
}

// Refusal 1: the account's own statement of which factors it has, or a word.
//
// **A response carrying no manifest is refused rather than shrugged off, and
// that is the refusal every other one below rests on.** `null` is a body this
// route sends quite readily — an account holding neither a manifest nor a factor
// is answered with one — but no account this product brings into existence can
// be in that state: registration files the first manifest at epoch 1 in the same
// save as the session, and each of the four paths that move a factor set carries
// one. So a `null` arriving here is a response somebody shaped, and letting it
// through would switch off the three refusals beside it at once.
async function declaredFactors(
  contentKey: CryptoKey,
  custody: AccountKeyCustodyDto,
): Promise<readonly FactorPublicKey[]> {
  if (custody.manifest === null) {
    throw refusal(
      'inconsistent',
      'The account-key response carried no factor manifest.',
    );
  }

  try {
    return await openFactorManifest(
      contentKey,
      custody.manifest,
      custody.rotationEpoch,
    );
  } catch (cause: unknown) {
    // **One word past this line and no attempt to sort the causes.** Custody
    // splits a manifest it could not *read* from one that did not verify,
    // because it owes a person the difference between *reload this tab* and
    // *nothing you hold will change this*. A rotation owes neither sentence:
    // the answer to both is that this run does not start, and there is no
    // branch here that could act on the difference.
    throw refusal(
      'inconsistent',
      "The account's factor manifest did not open under the content key a factor handed over.",
      cause,
    );
  }
}

// Refusal 2 and refusal 5, which are one rule over two different pairs of sets.
//
// **Two `every`s over two sets, never two lists walked in step.** Neither
// order is a contract: the account-key read sorts on the factor id today and
// says outright that what it owes is that determinism and nothing about the
// particular sequence, and a staged seal list is whatever the begin sent. A
// positional comparison passes on any pair that happens to arrive in step,
// which is exactly what today's sort hands it.
//
// **Set equality and not a count.** A count measures cardinality where the rule
// is about a set — two entries carrying one identifier satisfy both directions
// and a count would refuse them, or admit a twelve-against-eleven that set
// equality catches, depending on which way it is written.
//
// The identifiers are compared as they arrived, never folded. Both sides of
// each comparison come out of the same `uuid` column through the same
// serializer, so a fold would be a second notion of factor identity written to
// cover a spelling this API does not emit.
function requireOneFactorSet(
  declared: readonly { readonly factorId: string }[],
  present: readonly { readonly factorId: string }[],
  message: string,
): void {
  const declaredIds = new Set(declared.map((entry) => entry.factorId));
  const presentIds = new Set(present.map((entry) => entry.factorId));

  if (
    declared.every((entry) => presentIds.has(entry.factorId)) &&
    present.every((entry) => declaredIds.has(entry.factorId))
  ) {
    return;
  }

  throw refusal('inconsistent', message);
}

// Refusal 3: an epoch below one this device has already watched this account
// reach.
//
// **Every refusal before this one passes a replayed read.** Revoking a factor
// promotes the manifest to the next epoch under the *same* content key, so the
// manifest from before the revocation still opens, still declares a set that
// really was this account's, and matches that set exactly once the revoked
// factor's row is written back beside it. A begin over that read encapsulates
// the next generation to the factor somebody revoked — and files it at the
// epoch this device then records, so the gate custody runs after the
// completion, which refuses only what is *lower*, passes it too. Whoever can
// replay the manifest can replay the epoch beside it, so only a party that
// watched the account move past it can tell, and on a browser that party is
// the device. It is `manifestRefusal`'s third refusal, over the same record and
// with the same comparison.
//
// **Strictly lower is refused; equal passes**, because an account that has not
// rotated serves the same epoch every time. A device holding no record answers
// `null` and passes — it cannot detect a replay at all, which is ASM-016.
//
// **After refusal 1, never before it.** The served epoch is the manifest's
// associated data, so until the manifest has opened it is a number whoever wrote
// the response chose. **After refusal 2 as well, and that half is arranged
// rather than argued**: this refusal reads what refusal 1 proved and nothing of
// what refusal 2 did, and both answer the same word with no material. It sits
// here because refusal 2 lives inside `liveAccountOf`, which the resume shares,
// and this one is the begin's alone. Custody runs the pair the other way round;
// neither order changes what a person is told.
//
// **The begin's alone, because the begin is the entry point that encapsulates.**
// A resume restates the seals a begin already posted and hands the generation to
// no factor the staged run did not name, so a replayed read has no recipient to
// add there.
//
// **It reads the record and never writes it.** Raising a device's high-water
// mark is custody's act, taken only after its own four refusals pass over a
// read it made itself; a begin that recorded the epoch it just compared would
// be advancing the mark from a read no gate of custody's has judged.
function requireNoEpochBelowTheRecord(
  servedEpoch: number,
  budgetId: string,
): void {
  const seen = highestRotationEpochSeen(budgetId);

  if (seen === null || servedEpoch >= seen) {
    return;
  }

  throw refusal(
    'inconsistent',
    `The account serves its factor manifest at epoch ${servedEpoch}, below the ${seen} this device has already watched it reach.`,
  );
}

// One generation carried to every factor the manifest names, whether it was
// just drawn or recovered out of a run already in flight.
//
// **One function for both, because everything after the keys is identical and a
// second copy is a second chance to seal the manifest under the wrong one of
// them.** What differs between a fresh rotation and a repair is exactly where
// `keys` came from, and that is decided one frame up, next to the branch that
// keeps a draw out of reach of a staged run.
//
// The keys are encapsulated per factor from **one** pair. Drawing a pair per
// factor passes every round trip and gives one account as many keyspaces as it
// has authenticators.
async function stageGeneration(
  keys: AccountKeys,
  factors: readonly FactorPublicKey[],
  rotationEpoch: number,
): Promise<StagedGeneration> {
  try {
    const seals: RotationSealBody[] = [];

    // Sequential rather than `Promise.all`, and the reason is the ephemeral
    // pair: `encapsulateAccountKeysTo` draws one per call and must, since a
    // reused `(key, nonce)` pair across two factors surrenders the plaintext of
    // both. Running them together would not reuse one — the draw is inside —
    // but it would hold one live agreement per factor at once for no gain, over
    // a set that is eleven on an ordinary account.
    for (const factor of factors) {
      // Refusal 4. The point comes off the manifest and is handed straight
      // over; a point that is the right width and not on the curve is refused
      // by the platform inside this call, and a point of another encoding by
      // the guard above it.
      seals.push({
        factorId: factor.factorId,
        encapsulatedAccountKeys: await encapsulateTo(factor, keys),
      });
    }

    // The doors, which end the material: from this line on the next
    // generation exists only as two key objects nothing can read back out.
    const next = await holdGeneration(keys);

    return {
      next,
      rotationEpoch,
      // **Sealed under the *next* content key, never the current one.** After
      // the promotion a factor decapsulates the next generation and has to open
      // this blob with it; sealed under the generation being replaced it would
      // store, pass every check the server makes, and lock the account out of
      // its own factor set the moment the run finished.
      manifest: await sealFactorManifest(
        next.contentKey,
        factors,
        rotationEpoch,
      ),
      seals,
    };
  } finally {
    // The doors wipe what they are handed, so this covers the path where
    // something above them threw. Wiping twice costs nothing; the path that
    // skips a wipe is the path where something already went wrong.
    keys.contentKey.fill(0);
    keys.indexKey.fill(0);
  }
}

// Refusal 4, as a frame of its own so the refusal can name the factor.
//
// **Abort, never skip.** Staging the factors that could be reached and leaving
// one out produces a run that completes perfectly and orphans an authenticator
// still enrolled — the copy it holds opens nothing, and nobody finds out until
// they reach for it.
async function encapsulateTo(
  factor: FactorPublicKey,
  keys: AccountKeys,
): Promise<string> {
  try {
    return await encapsulateAccountKeysTo(
      factor.factorId,
      keys,
      factor.publicKey,
    );
  } catch (cause: unknown) {
    throw refusal(
      'inconsistent',
      `The account's manifest names ${factor.factorId} with a public key the next generation cannot be encapsulated to, so nothing was staged.`,
      cause,
    );
  }
}

// The generation a run already in flight is sealed under, recovered rather than
// drawn, as bytes.
//
// **The pairing is the whole of it**: the factor's *live* wrapped private key,
// which a rotation never touches because the key-encryption key that factor
// derives does not change when the account's keys do, opened against the
// *staged* encapsulated value. The two open together although they were never
// written together.
//
// **Bytes and not a {@link AccountKeyGeneration}, because one of the two callers
// has to encapsulate them again.** A resume hands them straight through
// `holdGeneration`, which ends them in the same statement; the repair carries
// them to every live factor first. Neither keeps them past its own frame.
async function recoverStagedKeys(
  keyEncryptionKey: CryptoKey,
  live: LiveAccount,
  staged: StagedRotationDto,
): Promise<AccountKeys> {
  requireSomeLiveFactorHoldsASeal(live.factors, staged.seals);

  const seal = staged.seals.find(
    (candidate) => candidate.factorId === live.entry.factorId,
  );

  if (seal === undefined) {
    // A factor enrolled after this run began holds no staged seal, and that is
    // an honest state rather than a broken one — the read is specified to
    // answer what was *staged* rather than to fill a gap from a live row, which
    // would hand back the generation this run is replacing. The refusal above
    // has just established that another factor still carries it, which is what
    // makes `unopened` — *try another one* — true here rather than hopeful.
    throw refusal(
      'unopened',
      `The rotation in flight staged nothing for ${live.entry.factorId}.`,
    );
  }

  try {
    return await openFactorKeypair(keyEncryptionKey, live.entry.factorId, {
      wrappedPrivateKey: live.entry.wrappedPrivateKey,
      encapsulatedAccountKeys: seal.encapsulatedAccountKeys,
    });
  } catch (cause: unknown) {
    throw refusal(
      'unopened',
      `The value the rotation in flight staged for ${live.entry.factorId} did not open.`,
      cause,
    );
  }
}

// The edge that decides which of the two words the refusal above may use.
//
// **It is the intersection of two sets and not a lookup**, and it is checked
// before the presented factor is looked up at all, because the two states it
// separates are two different next steps. A factor of this account that holds no
// staged seal while another one does is *try another one*; a staged seal set
// with nothing live in it is a generation no factor of this account can ever
// recover, and every row an earlier chunk re-sealed under it is already
// stranded. The head of this file argues why the second is `inconsistent`.
function requireSomeLiveFactorHoldsASeal(
  factors: readonly { readonly factorId: string }[],
  seals: readonly { readonly factorId: string }[],
): void {
  const sealed = new Set(seals.map((seal) => seal.factorId));

  if (factors.some((factor) => sealed.has(factor.factorId))) {
    return;
  }

  throw refusal(
    'inconsistent',
    'The rotation in flight staged a seal for no factor this account still holds, so the generation it re-sealed rows under can no longer be recovered.',
  );
}

// Refusal 5: the seals a run carries are exactly the set its manifest declares.
//
// **`key-rotation.md` names this as the comparison nothing on either side of
// the wire holds.** The server gates a begin's seals against the account's live
// factors and is deliberately blind to the manifest beside them, whose set is
// authenticated by a key it does not hold; so a client may stage a manifest
// naming one set and seals covering another and nothing there refuses it. The
// client holds that half, and this is it.
//
// **The manifest is opened rather than compared against the list it was built
// from**, which is what keeps this from being vacuous on the minting path: what
// it judges is a value that round-tripped through the seal and the open, under
// the very key the seals carry, at the very epoch it is filed under. A
// comparison against the input variable would agree with itself on a manifest
// sealed under the wrong generation, at the wrong epoch, or over a set some
// later line changed.
async function requireSealsCoverTheManifest(
  run: StagedGeneration,
): Promise<void> {
  let declared: readonly FactorPublicKey[];

  try {
    declared = await openFactorManifest(
      run.next.contentKey,
      run.manifest,
      run.rotationEpoch,
    );
  } catch (cause: unknown) {
    throw refusal(
      'inconsistent',
      'The manifest this run is filed under did not open under the generation its seals carry.',
      cause,
    );
  }

  requireOneFactorSet(
    declared,
    run.seals,
    'This run carries a seal set that is not the one its manifest declares.',
  );
}

// Both of the account's keys as key objects, and the end of the material they
// were made of.
//
// **Two doors, because the two keys are two different keys.** The content key
// encrypts, so it goes through the AES-GCM door; the index key is what a blind
// index is computed under, which is HMAC-SHA-256, so it goes through the other.
// Taking the shorter route through one door produces a key object that cannot
// do the job and cannot be corrected afterwards, because by then it is
// non-extractable and the bytes are zeroes.
async function holdGeneration(
  keys: AccountKeys,
): Promise<AccountKeyGeneration> {
  try {
    const [contentKey, indexKey] = await Promise.all([
      importAesGcmKey(keys.contentKey),
      importHmacSha256Key(keys.indexKey),
    ]);

    return { contentKey, indexKey };
  } finally {
    // Each door already wipes what it was handed, including on its own
    // refusal. This covers the arrangement where one of the two rejects before
    // the other has been reached.
    keys.contentKey.fill(0);
    keys.indexKey.fill(0);
  }
}

// One spelling of the throw, so that every refusal in this module carries a
// word and a `cause` rather than depending on eight call sites to remember.
function refusal(
  reason: KeyRotationMaterialReason,
  message: string,
  cause?: unknown,
): KeyRotationMaterialError {
  return cause === undefined
    ? new KeyRotationMaterialError(reason, message)
    : new KeyRotationMaterialError(reason, message, { cause });
}
