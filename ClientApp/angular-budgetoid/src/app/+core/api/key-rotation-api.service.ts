// The four routes of a content-key rotation, and nothing else.
//
// **This is a transport.** It holds no key, runs no cipher, makes no refusal of
// its own and reads no conflict. Every value below is already sealed, wrapped
// or encapsulated by the time it reaches this file, and every value that comes
// back is still sealed when it leaves: `factor-keypair.ts` opens the envelopes,
// `factor-manifest.ts` opens the manifest, and `write-outcome.ts` is the one
// place in this client that turns a refused write into a word a screen renders.
// A guard, a decoder or a second reading of `conflictKind` added here would be a
// second definition of something one of those three already owns — and the copy
// is the one that drifts, because the screen renders the original.
//
// `KeyRotationEndpoints.cs` argues every member of the records this module
// mirrors, and `docs/business-logic/key-rotation.md` argues the run they make
// up; where a decision here is really one of theirs, the comment points rather
// than restates.
//
// **No request below carries `EXPECTS_UNAUTHENTICATED`, and the absence is the
// decision.** That token marks a request whose 401 is the *route's own verdict*,
// made by a browser holding no session to lose. All four of these are made by a
// browser that believes it holds one — a rotation is reachable only from a full
// session — so a 401 is that session having ended, which is exactly the fact
// `sessionExpiryInterceptor` owns. Suppressing it would leave somebody on a
// rotation screen whose every call is refused with nothing saying why.
import { Injectable } from '@angular/core';
import type { FactorKeypairEnvelopes } from '@app-core/security/factor-keypair';
import type { PasskeyAssertionPayload } from '@app-core/security/webauthn-encoding';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

/**
 * One factor's copy of the next generation's account keys, in both directions.
 *
 * **One type for `SealRequest` and `StagedSealResponse`, which are the same two
 * members on purpose.** The resume read exists so that a client which lost a run
 * to a reload reads back what it sent, "under names it has already written code
 * against" — that record says so itself — so a second type for the way back
 * would be two spellings of one value, kept equal by nobody. Should the server
 * ever part them, `key-rotation-wire-v1.json` carries a list per record and the
 * spec compares this shape against both.
 *
 * `encapsulatedAccountKeys` is **picked** from {@link FactorKeypairEnvelopes}
 * rather than written out, for the reason `RecoveryCodeSubmissionBody` extends
 * that interface: it is what `mintFactorKeypair` emits and what
 * `openFactorKeypair` takes, and a local copy of the spelling could be renamed
 * here while the crypto went on producing the old one. `wrappedPrivateKey` is
 * not picked beside it — a rotation moves the account's keys and never the
 * factor's own private half, which stays wrapped under the key-encryption key
 * that factor derives and is not this route's business.
 */
export interface RotationSealBody
  extends Pick<FactorKeypairEnvelopes, 'encapsulatedAccountKeys'> {
  /**
   * The factor this copy was encapsulated to, in the canonical lower-case
   * 36-character hyphenated spelling. It **is** the associated data the value
   * beside it was bound with, so it travels unaltered — never normalised,
   * re-cased or prettified on the way past.
   */
  readonly factorId: string;
}

/**
 * The body of `POST /api/me/key-rotation`: which run, which generation, one
 * seal per factor, and the assertion that authorizes it.
 *
 * **The five assertion members arrive through {@link PasskeyAssertionPayload}
 * rather than being restated.** The server's re-authentication gates — erasure,
 * revocation, code regeneration and this one — bind byte-identical member sets,
 * and a caller comparing them must learn nothing from a difference between
 * them; one spelling on this side is what keeps a rename from reaching one gate
 * and not the others.
 *
 * `seals` is an **array and never an object keyed on the factor**. A repeated
 * factor is a refusal the server owes its caller, and a JSON object makes one
 * unconstructible on the wire: the binder drops the repeat, last wins, and a
 * begin naming twelve factors in thirteen seals would arrive as twelve, satisfy
 * the set comparison perfectly, and leave the account one seal short of what
 * this client believed it sent.
 *
 * There is no `userId` and none may be added: the only account a rotation moves
 * is the session's.
 */
export type BeginRotationRequestBody = PasskeyAssertionPayload & {
  /** The client-minted identifier of this run. */
  readonly rotationId: string;
  /**
   * The next generation's manifest of factor public keys, sealed under the
   * account's content key, as one unpadded base64url AEAD envelope. The server
   * enforces framing, width and epoch and can read no byte of it.
   */
  readonly manifest: string;
  /**
   * The generation that manifest will be filed at — a number, not text standing
   * for bytes, because it is the manifest's associated data. The server refuses
   * anything that is not the stored value plus one.
   */
  readonly rotationEpoch: number;
  /** One copy of the next generation's keys per factor the account holds. */
  readonly seals: readonly RotationSealBody[];
};

/**
 * How many rows carrying a narrative value each of the six narrative-bearing
 * tables holds — the denominator a client drives its progress against.
 *
 * Six members rather than a map keyed on a table name: a map lets a table go
 * missing with nothing failing, and a table the response forgot is a rotation
 * that reports itself finished with one still to go. Counts and nothing else —
 * every narrative column in the product is an envelope the server cannot open,
 * so the useful version of a "which payee is it on" member cannot exist and the
 * useless version would ship ciphertext to a progress bar.
 */
export interface RotationInventoryDto {
  readonly accounts: number;
  readonly payees: number;
  readonly categoryGroups: number;
  readonly categories: number;
  readonly transactions: number;
  readonly budgets: number;
}

/**
 * What a begun rotation answers: how much there is to rewrite, and how much of
 * it may travel in one request.
 *
 * It echoes back neither the run nor the staged manifest, and that is the
 * server's decision rather than an omission — a response restating what was
 * just sent invites a client to read the echo as agreement.
 */
export interface KeyRotationBegunDto {
  readonly inventory: RotationInventoryDto;
  /**
   * The byte budget one chunk's re-sealed rows may occupy. It is **advice a
   * client sizes its batches by**, not a limit this route enforces: enforcement
   * is the host's request-body cap, in one place.
   */
  readonly maxChunkBytes: number;
}

/**
 * One row whose whole narrative is a required name — an account or a payee.
 *
 * `nameKey` rides beside the envelope because a rotation replaces the **index**
 * key as well as the content key, so the blind index over the same text has to
 * be recomputed. They are two members because they are two columns, and the
 * server can check neither against the other.
 *
 * `id` is the identifier this server rendered, not base64url text. That is the
 * one place a chunk parts company with the create bodies it resembles: a create
 * seals its envelope against a client-minted identifier, while a chunk names a
 * row that already exists and nothing is sealed against what this body says
 * about it.
 */
export interface ResealedNamedRowBody {
  readonly id: string;
  readonly name: string;
  readonly nameKey: string;
}

/**
 * A row carrying a note beside its name — a category or a category group.
 *
 * **`description` is `string | null` and the member is always present.** Null
 * and absent mean the same thing to the route, and sending the member always is
 * what keeps an arm's member set a fact about this client rather than about
 * which rows a chunk happened to name. It is **not** a way to clear the column:
 * the server refuses a reseal that changes whether a row holds a note in either
 * direction, so `null` for a row that holds one is a refusal rather than an
 * edit.
 */
export interface ResealedDescribedRowBody extends ResealedNamedRowBody {
  readonly description: string | null;
}

/**
 * One transaction's new value: a note and no name, because `transactions` has
 * none.
 *
 * It is the only arm whose whole narrative is a nullable column — which is why
 * a note-less transaction, what most rows of a real account are, is a row a
 * chunk never names at all rather than one it sends an empty entry for.
 */
export interface ResealedTransactionBody {
  readonly id: string;
  readonly description: string | null;
}

/**
 * The body of `POST /api/me/key-rotation/chunks`: the run it continues, and the
 * rows a client has re-sealed under that run's generation.
 *
 * **Five arms and no budget arm**, which is a decision rather than a gap — the
 * budget's name moves by a path of its own.
 *
 * **No count of its own.** The begin published the denominator; a "rows in this
 * chunk" or "rows remaining" member here would be a second one able to disagree
 * with it, which on a screen is a progress bar that never reaches the end.
 */
export interface ResealChunkRequestBody {
  readonly rotationId: string;
  readonly accounts: readonly ResealedNamedRowBody[];
  readonly payees: readonly ResealedNamedRowBody[];
  readonly categoryGroups: readonly ResealedDescribedRowBody[];
  readonly categories: readonly ResealedDescribedRowBody[];
  readonly transactions: readonly ResealedTransactionBody[];
}

/**
 * The body of `POST /api/me/key-rotation/completion` — the run being finished,
 * and deliberately nothing else.
 *
 * **One member, and every other candidate is refused.** The staged manifest,
 * the generation it is filed at and the value each factor adopts were all fixed
 * by the begin and are on file already; carried again they would be a second
 * statement of the same values, able to disagree with the staged one at the one
 * moment a disagreement cannot be undone. A member added here would bind on the
 * other side, be forwarded nowhere, and redden nothing.
 */
export interface CompleteRotationRequestBody {
  readonly rotationId: string;
}

/**
 * One staged run as a resuming client needs it back.
 *
 * **It echoes the epoch and the manifest where a begun rotation deliberately
 * echoes neither**, and the asymmetry is the whole difference between the two
 * answers: a begin's caller sent those values a moment ago, while this caller
 * sent nothing — it is a client that lost the run to a reload, and these are the
 * values it no longer has.
 *
 * **`seals` is what the run staged, which is not always the set of factors the
 * account holds now.** A factor with no staged seal is absent rather than
 * carried with a null value or filled in from its live row, and either of those
 * would fail a resuming client worse: the second is a well-formed value of the
 * right width carrying the generation the run is replacing.
 *
 * No member carries progress — not "rows remaining", not a percentage, not a
 * list of stamped row ids. {@link inventory} is the denominator and the client
 * holds the numerator.
 */
export interface StagedRotationDto {
  readonly rotationId: string;
  readonly stagedRotationEpoch: number;
  /** Byte for byte what the begin staged, as unpadded base64url. */
  readonly stagedManifest: string;
  /**
   * When the run was begun, as the server wrote it. It stays a string all the
   * way to whatever renders it; nothing here parses a calendar.
   */
  readonly startedAtUtc: string;
  readonly inventory: RotationInventoryDto;
  readonly maxChunkBytes: number;
  readonly seals: readonly RotationSealBody[];
}

/**
 * The whole answer of `GET /api/me/key-rotation`.
 *
 * **A wrapper carrying one member, and the member is the point.** "Nothing in
 * flight" is `rotation: null` inside an ordinary 200 — never a 404, which every
 * client here reads as "try again in a minute", advice that for an account which
 * has simply never begun a rotation never succeeds; and never an empty object,
 * which is a staged run naming no factor and would have a client resume by
 * re-encapsulating the account's keys to nobody.
 *
 * **No second member may be added saying whether a run is in flight.** The
 * presence of {@link rotation} is that fact, and a flag beside it would be one
 * statement able to disagree with the other — on the one read a client consults
 * when it has already lost track of what it was doing.
 */
export interface KeyRotationStateDto {
  readonly rotation: StagedRotationDto | null;
}

@Injectable({ providedIn: 'root' })
export class KeyRotationApiService extends BaseApiService {
  /**
   * Opens a run: stages the next generation's manifest and one copy of the new
   * account keys per factor the account holds.
   *
   * **200 and never 201**, so there is a body to read: the inventory and the
   * chunk budget the rest of the run is driven by. A begin creates no resource
   * this API exposes at an address, which is why nothing points anywhere.
   */
  public beginRotation(
    body: BeginRotationRequestBody,
  ): Observable<KeyRotationBegunDto> {
    return this.post<KeyRotationBegunDto>('api/me/key-rotation', body);
  }

  /**
   * Carries one batch of rows a client has re-sealed under the staged
   * generation.
   *
   * **`Observable<void>` because the route answers 204 with an empty body.** A
   * chunk is all-or-nothing in one save, so there is no partial-accept count to
   * report, and a declared body here would be a shape the route does not have
   * and one a caller could be tempted to publish progress from.
   */
  public resealRows(body: ResealChunkRequestBody): Observable<void> {
    return this.post<void>('api/me/key-rotation/chunks', body);
  }

  /**
   * Promotes what the begin staged into the account's live rows.
   *
   * **Nothing comes back, and that is the route's one decision.** Not a body
   * member, not an `ETag`, not a `Location`: a client's rotation-epoch record
   * may rise only after the four-refusal gate over `GET /api/me/account-keys`
   * has passed, and a generation returned from here is a number a client could
   * advance its record from having judged nothing — an oracle rather than an
   * observation. The way to learn the promoted generation is to re-read the
   * account keys.
   */
  public completeRotation(body: CompleteRotationRequestBody): Observable<void> {
    return this.post<void>('api/me/key-rotation/completion', body);
  }

  /**
   * Hands back the run this account has staged and not yet completed, which is
   * what makes an interruption recoverable rather than terminal.
   *
   * The staged seals are the only copies of the generation every row an
   * interrupted run already rewrote is sealed under — `wrapped_account_keys`
   * still holds the superseded pair until the promotion — so this read is the
   * way back, and it answers 200 whether or not there is anything to resume.
   */
  public getRotationState(): Observable<KeyRotationStateDto> {
    return this.get<KeyRotationStateDto>('api/me/key-rotation');
  }
}
