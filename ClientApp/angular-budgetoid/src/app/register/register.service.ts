// The registration flow: one act, and the only place in this client where the
// account's keys exist in the clear.
//
// **Not `providedIn: 'root'`, and that is a custody decision rather than a
// lifetime preference.** The register screen provides this service, so it is
// discarded with the screen — and the account keys, the eleven key-encryption
// keys and the ten recovery codes are discarded with it. Held at the root, the
// codes of an abandoned registration would still be readable from the injector
// on an unrelated screen an hour later, and there is nowhere in this product
// they could legitimately be read from.
//
// **A value is a signal only if a template renders it.** Everything else is a
// local or a private field. A signal on an injectable is one `effect()` away
// from being logged by somebody who wanted to debug a re-render, and these are
// the values that unlock the account. That rule is why the key-encryption keys
// never touch `this` at all, why the account keys' **bytes** are wiped inside
// the method that draws them, why what survives that method is two opaque
// `CryptoKey` objects on a `#` field no reflective API can reach, and why the
// ten codes — which the codes step must render — are the one secret published
// here.
//
// The ordering rules below are each load-bearing and each argued at the line
// that implements them. `RegisterAccountHandler.cs` is the other half of most of
// them; where a rule is really the server's, the comment points at it.
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  RegistrationApiService,
  type RecoveryCodeSubmissionBody,
  type RegistrationRequestBody,
} from '@app-core/api/registration-api.service';
import { AccountKeyCustodyService } from '@app-core/security/account-key-custody.service';
import {
  generateAccountKeys,
  importAesGcmKey,
  importHmacSha256Key,
  keyEncryptionKeyFromRecoveryCode,
  wrapAccountKeys,
} from '@app-core/security/account-keys';
import { mintFactorId } from '@app-core/security/factor-id';
import {
  mintRecoveryCodeSet,
  type RecoveryCode,
} from '@app-core/security/recovery-codes';
import {
  WebauthnCeremonyService,
  type PasskeyCeremonyFailure,
} from '@app-core/security/webauthn-ceremony.service';
import type { PasskeyCreationOptionsJson } from '@app-core/security/webauthn-encoding';
import { AuthService } from '@app-core/services/auth-service';
import { SessionService } from '@app-core/session/session.service';

/**
 * Where the reader is.
 *
 * Three places and not a state machine over outcomes: `busy` and `failure` are
 * separate readings on purpose, because a step is *where somebody is standing*
 * and those two are *what is happening there*. Folded into this union, every
 * failure would move the person somewhere, and the screen would have to move
 * them back.
 */
export type RegisterStep = 'intro' | 'passkey' | 'codes';

/**
 * Why the flow did not get further, in the words the screen says out loud.
 *
 * The first five are the ceremony's, one for one — `PasskeyCeremonyFailure`
 * argues why none of them is a synonym of another, and only `failed` is renamed,
 * to `ceremony-failed`, because `failed` on this surface would read as "the
 * registration failed" rather than "the device did not finish".
 *
 * The last five are this flow's own:
 *
 *   * `start-failed` — the server never issued a challenge. Nothing was minted.
 *   * `provider-token-refused` — the options leg answered 401, so the provider
 *     token this browser attached was not accepted. **Named for what the answer
 *     proves and not for the cause behind it**: a 401 on a route declared on the
 *     provider scheme says the bearer was refused, and expiry is only the
 *     likeliest of several reasons — a token already spent, one minted for
 *     another audience, a clock far enough out to make a live token look dead.
 *     `provider-token-expired` would be a diagnosis the answer does not carry,
 *     and a word this flow later has to be careful not to believe. It is not a
 *     narrower `refused` either: that word is the POST leg's and means the
 *     server read a registration and said no, while this one means the request
 *     never reached a handler at all. The way out is the provider rather than
 *     another press, which is what makes it a word rather than a status.
 *   * `refused` — the server judged the request and said no. Nothing was
 *     created and the codes on screen are dead.
 *   * `conflict` — an account already exists for this provider identity. The
 *     one word published from **both** legs, and it can arrive on any of the
 *     three steps. From the options leg nothing has been minted: on the
 *     introduction, which is where {@link RegisterService.begin} asks and where
 *     the answer ordinarily lands, and on the passkey step for the refetch a
 *     restart or a failed ceremony makes. From the POST leg ten codes are on
 *     screen and the shell replaces the codes step with it. Each of the three
 *     says its own sentence, because what is true beside the refusal differs
 *     every time.
 *   * `unknown` — no answer, or an answer that says nothing about what
 *     happened. It is **not** a synonym of `refused`; see {@link create}.
 */
export type RegisterFailure =
  | 'unsupported'
  | 'cancelled'
  | 'duplicate'
  | 'no-prf'
  | 'ceremony-failed'
  | 'start-failed'
  | 'provider-token-refused'
  | 'refused'
  | 'conflict'
  | 'unknown';

@Injectable()
export class RegisterService {
  private readonly api = inject(RegistrationApiService);
  private readonly ceremony = inject(WebauthnCeremonyService);
  private readonly session = inject(SessionService);
  // Root-provided, unlike this service, and that is the whole difference
  // between an attempt and a session: this flow dies with the screen, and the
  // keys it hands over on the 201 have to outlive it.
  private readonly custody = inject(AccountKeyCustodyService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  private readonly stepSignal = signal<RegisterStep>('intro');
  private readonly busySignal = signal(false);
  private readonly failureSignal = signal<RegisterFailure | null>(null);
  // The one secret in a signal, because the codes step renders it and there is
  // no other way to hand ten values to a template. It reaches no storage, no
  // route state and no `history.state`; `null` means "not minted", which is a
  // different thing from an empty set and is never collapsed into one.
  private readonly codesSignal = signal<readonly RecoveryCode[] | null>(null);
  private readonly mayHaveCreatedSignal = signal(false);

  public readonly step: Signal<RegisterStep> = this.stepSignal.asReadonly();
  public readonly busy: Signal<boolean> = this.busySignal.asReadonly();
  public readonly failure: Signal<RegisterFailure | null> =
    this.failureSignal.asReadonly();
  public readonly codes: Signal<readonly RecoveryCode[] | null> =
    this.codesSignal.asReadonly();
  /**
   * The address the provider asserted, for the screen to show back.
   *
   * A `computed` over a claim rather than a stored copy: the claim is the one
   * fact this flow has about who is registering, and reading it where it lives
   * keeps this service from holding a second, older copy of it. It has no signal
   * dependency, so it settles on first read and stays — which is what is wanted
   * here, because {@link create} discards the provider token moments before the
   * screen goes away and a re-reading value would blank the header on the way
   * out.
   */
  public readonly email: Signal<string | null> = computed(() =>
    this.auth.providerEmail(),
  );
  /**
   * Whether a request from this browser may have created the account.
   *
   * **What the previous POST ended as, and never whether a button was
   * pressed.** `unknown` is the only outcome under which anything may have been
   * written: a status 0, a timeout and a 5xx say nothing about whether thirty
   * rows were committed and a 201 was lost coming back. Every 400, 401 and 403
   * leaves `RegisterAccountHandler` before `RegisterAsync` is reached, and a 409
   * refuses the request it answers — so after any of those, nothing exists that
   * did not exist before, and the passkey the device made was never seen by the
   * server.
   *
   * **Set and never cleared, and the asymmetry between the three words is what
   * makes that right.** A 400 and a 409 are judgements — the server looked and
   * said no — so neither of them ever opens the question. `unknown` opens it,
   * and nothing that happens later closes it: a request that may have committed
   * thirty rows stays one whatever the next attempt answers, and the account it
   * may have created does not stop existing because a request after it was
   * refused. That is why the sentence this signal buys the screen is still true
   * when it appears after a restart.
   */
  public readonly mayHaveCreatedAccount: Signal<boolean> =
    this.mayHaveCreatedSignal.asReadonly();

  // The assembled request body, and the reason it is a field rather than an
  // argument to `create()`: what a person acknowledges on the codes step is
  // that they have kept *these* codes, and the body that is posted must be the
  // one whose envelopes those codes open. Passing it back in through the screen
  // would put a rendered value between the two.
  //
  // Cleared on the 201, on any failure of the POST, by `restart()`, and with the
  // screen. It carries no code and no key — only verifiers and sealed envelopes
  // — but it is the request that creates an account, and one that outlives its
  // outcome is one somebody will eventually re-send.
  private pending: RegistrationRequestBody | null = null;

  // The account's two keys, imported, waiting for the 201 that makes the
  // account they belong to real.
  //
  // **One field holding both, and never two fields.** The two are drawn
  // together, wrapped together and handed over together, and a pair of nullable
  // fields would admit a state — one key held, the other not — that no path
  // produces and every reader downstream would then have to rule out. Here
  // "both or neither" is what the type says, so {@link create} has one question
  // to ask.
  //
  // **A `#` field, and this is the second place in `src/` that spells private
  // this way** — `account-key-custody.service.ts` is the first and argues it in
  // full. TypeScript's `private` is erased on the way out, so an ordinary field
  // here would be an own property of the instance: readable by
  // `(service as never)['accountKeys']`, walked by `JSON.stringify`, carried by
  // a structured clone, and shown by any devtools panel. What it would be
  // showing is the pair that decrypts everything the account ever writes. Every
  // other field on this class is `private` and right to be, because none of
  // them is that.
  //
  // **A field and not a signal**, the header rule of this file: no template
  // renders these and none may.
  //
  // Cleared by {@link restart} and on any failure of the POST, beside
  // {@link pending} in both places — the body and the keys are made in one
  // breath and an attempt that ended keeps neither. Not cleared on the 201:
  // {@link AccountKeyCustodyService} is holding the same two objects by then
  // and the screen is being navigated away from.
  #accountKeys: {
    readonly contentKey: CryptoKey;
    readonly indexKey: CryptoKey;
  } | null = null;

  // The challenge {@link begin} fetched, waiting for the press that spends it.
  //
  // **A field and not a signal, because no template renders it** — the header
  // rule of this file. It is also taken *once*: {@link createPasskey} reads it
  // and clears it in the same statement, so a second ceremony can never run
  // against a challenge the first one consumed. `RegisterAccountHandler` calls
  // `challengeStore.ConsumeAsync` above its verifier, so a second use meets the
  // undifferentiated challenge refusal — the same argument {@link create} makes
  // about re-posting a body.
  //
  // Cleared by {@link restart} as well, which promises a new challenge and
  // would otherwise hand back the spent one.
  private options: PasskeyCreationOptionsJson | null = null;

  /**
   * Asks whether this provider identity may have an account at all, and leaves
   * the introduction for the passkey step if it may.
   *
   * **The refusal belongs here, not one step further on.**
   * `POST /api/registration/options` answers 409 when the subject already holds
   * an account, and it answers *above* its own `challengeStore.IssueAsync` —
   * `BeginAccountRegistrationHandler` argues the position there. Asked from the
   * passkey step, that answer arrives after somebody has read "your account
   * will be created under <address>", pressed `Continue`, read a screen about
   * authenticators and pressed again. The server knew at the first press.
   *
   * Nothing here touches {@link mayHaveCreatedAccount}, and no later edit may:
   * this leg creates nothing, ever — the argument {@link startFailureOf}
   * carries and {@link createPasskey}'s error branch restates.
   *
   * `failure` is cleared here rather than only where one is set, which is the
   * rule every act in this service follows: a failure is cleared when an act
   * *starts*. Cleared only on failure, the sentence from a refused start
   * survives the press that retries it, and the reader is told what went wrong
   * last time while this time is still running.
   */
  public begin(): void {
    if (this.busySignal()) {
      return;
    }

    this.failureSignal.set(null);

    // **A browser that cannot run the ceremony asks for no challenge, and that
    // rule outranks asking early.** A challenge is a nonce the server persisted
    // — on this route it is also the value the account identifier is derived
    // from — and this browser was never going to finish. The conflict it would
    // learn about is not worth one either: the way out of a conflict is a
    // passkey assertion on `/welcome`, which needs the same WebAuthn this
    // browser does not have. So the step advances with no options in hand and
    // {@link createPasskey} says `unsupported` without a request, exactly as it
    // did before this leg existed.
    if (!this.ceremony.available()) {
      this.stepSignal.set('passkey');

      return;
    }

    this.busySignal.set(true);
    this.api.getCreationOptions().subscribe({
      next: (options) => {
        this.options = options;
        this.busySignal.set(false);
        this.stepSignal.set('passkey');
      },
      error: (error: unknown) => {
        // **The step does not move.** A refusal published against the passkey
        // step would be a refusal the reader has to be walked back from, and
        // the introduction is where both of this leg's two words make sense:
        // `conflict` says this address has an account, and `start-failed` says
        // the server never answered.
        this.busySignal.set(false);
        this.failureSignal.set(RegisterService.startFailureOf(error));
      },
    });
  }

  /**
   * Registers the passkey, draws the account's keys, mints the card of codes,
   * and wraps the keys under all eleven factors.
   *
   * The order of the first three steps is the whole of this method's design and
   * each is argued where it happens. Nothing is posted here: what this produces
   * is {@link pending} and ten codes on screen, and the account is created by
   * {@link create} once the person says they have kept them.
   *
   * **The challenge is usually already in hand**, fetched by {@link begin} so
   * the 409 could be answered a step earlier. When it is, this press runs the
   * ceremony and nothing else — a second options request would spend a second
   * nonce and strand the first. When it is not, this leg fetches its own, and
   * that is the path a {@link restart} and every `Try again` on the passkey
   * step take: the previous challenge is spent, so a fresh one is the only
   * thing that can work.
   */
  public createPasskey(): void {
    if (this.busySignal()) {
      return;
    }

    this.failureSignal.set(null);

    // **Above everything, on both paths.** A browser that was never going to
    // finish the ceremony would otherwise spend a challenge on its way to being
    // told so, and a challenge is a nonce the server persisted — on this route
    // it is also the value the account identifier is derived from. {@link begin}
    // reads the same answer before *its* request for the same reason.
    if (!this.ceremony.available()) {
      this.failureSignal.set('unsupported');

      return;
    }

    this.busySignal.set(true);

    // Taken, not read: cleared in the same breath so the ceremony below is the
    // only thing that can ever run against this nonce. A prefetched challenge
    // left in place is one a later `Try again` would re-use after the server
    // had consumed it, and the refusal that follows names nothing.
    const prefetched = this.options;

    if (prefetched !== null) {
      this.options = null;
      void this.mintUnder(prefetched);

      return;
    }

    this.api.getCreationOptions().subscribe({
      next: (options) => {
        void this.mintUnder(options);
      },
      error: (error: unknown) => {
        // Two words, and {@link startFailureOf} argues which. Everything but
        // the 409 is `start-failed`: no challenge exists, so nothing was
        // minted, nothing was spent and the next step is the same for all of
        // them. Nothing here touches {@link mayHaveCreatedAccount} — no request
        // that creates anything has left this browser, and a leg that cannot
        // write is not one that can open the question.
        //
        // **The 409 is still reachable from here** now that {@link begin}
        // usually answers it first, and the passkey step keeps its sentence for
        // it: this refetch runs after a restart or a ceremony that failed, and
        // another tab — or the same person on another device — can have
        // finished registering in between.
        this.busySignal.set(false);
        this.failureSignal.set(RegisterService.startFailureOf(error));
      },
    });
  }

  /**
   * Posts the assembled registration and, on 201, hands the visitor to the app.
   *
   * **There is no retry, only {@link restart}.** The challenge is consumed
   * before anything is verified — `RegisterAccountHandler` calls
   * `challengeStore.ConsumeAsync` above its verifier and states why there — so
   * a second POST of the same body meets the undifferentiated challenge refusal
   * with certainty. A retry button here would look like a way out and be a way
   * to be told no twice.
   */
  public create(): void {
    const body = this.pending;

    if (body === null || this.busySignal()) {
      return;
    }

    this.failureSignal.set(null);
    this.busySignal.set(true);

    // **The subscription is deliberately not torn down with the screen**, for
    // the reason `settings.service.ts` gives about the export: the request has
    // been made and an account is being created by it, so the three statements
    // below are how this application stops lying about who the visitor is. A
    // subscription cancelled on destroy would leave a created account behind a
    // client still calling itself anonymous.
    // Read before the request is made rather than inside the handler, so the
    // pair the account is created with is the pair this call started from —
    // and so the `null` case is one branch here instead of a question the
    // success path has to ask at its busiest moment.
    const keys = this.#accountKeys;

    this.api.register(body).subscribe({
      next: () => {
        this.pending = null;
        this.busySignal.set(false);

        // **This order, and each pair of neighbours is the reason.** Publishing
        // the session first means `authGuard` reads `'authenticated'` when the
        // navigation below asks it — navigate first and the guard judges `/app`
        // against a stale `'anonymous'` and bounces the person out of the
        // account they just created. Forgetting the provider token last, for the
        // mirror of that: dropped before the navigation, it is dropped while a
        // request may still be leaving with a bearer attached to it.
        this.session.established();
        this.adopt(keys);
        this.auth.forgetProviderToken();
        void this.router.navigateByUrl('/app');
      },
      error: (error: unknown) => {
        const failure = RegisterService.failureOf(error);

        this.pending = null;
        // **Beside the body, on every way this can end badly**, including the
        // `unknown` one where the account may exist after all. Keeping them for
        // that case is the tempting edit and it is wrong twice: nothing here can
        // adopt keys for an account it cannot confirm, and the way back into an
        // account that may have been created is a passkey assertion on
        // `/welcome`, which derives its own key-encryption key and unwraps from
        // the row this attempt would have written.
        this.#accountKeys = null;
        this.busySignal.set(false);

        // **Published here, from the answer, and nowhere else.** This is what
        // the screen's two readings of a 409 fork on, so it has to be a fact
        // about the request that just ended rather than about anything the
        // person did afterwards — see {@link mayHaveCreatedAccount}. `unknown`
        // is the only word that leaves the question open; the other two are
        // judgements, and after one of them nothing was written.
        if (failure === 'unknown') {
          this.mayHaveCreatedSignal.set(true);
        }

        this.failureSignal.set(failure);
      },
    });
  }

  /**
   * Throws this attempt away and starts a new one from the passkey step.
   *
   * Everything is re-drawn: a new challenge, a new passkey, new account keys,
   * new codes and eleven new factor identifiers. Nothing from the previous
   * attempt is reused, and nothing could be — the challenge is spent, the keys
   * were wiped, and the codes somebody may have written down a minute ago
   * belong to nothing.
   *
   * **This publishes no fact of its own, because a press is not a fact about
   * the server.** `Start again` is offered from two states, so forking the two
   * readings of a 409 on "did this browser restart?" told somebody whose first
   * attempt was *refused* that it had created their account — and every clause
   * of that was false. They were then sent to sign in with a passkey the server
   * had never seen, which answers a byte-identical 401 with nothing naming the
   * cause. {@link mayHaveCreatedAccount} is what that fork needs, and it is set
   * from the answer rather than from the press.
   */
  public restart(): void {
    if (this.busySignal()) {
      return;
    }

    this.pending = null;
    // With the body, because they are the body's pair: the envelopes in it were
    // sealed under factor identifiers this attempt minted, and the next attempt
    // mints eleven new ones over two new keys. Kept, they would be adopted on
    // the *next* attempt's 201 — the account would be created under the second
    // draw and unlocked with the first, which opens nothing, permanently, with
    // nothing anywhere naming the cause.
    this.#accountKeys = null;
    // **The prefetched challenge goes with everything else**, or the promise in
    // the paragraph above is false the one time it is load-bearing. The nonce
    // {@link begin} fetched is consumed by the ceremony that ran before this
    // restart, so a copy left here would be handed to the next ceremony and
    // refused by a server that has already seen it — for reasons the refusal
    // does not name. Cleared here, {@link createPasskey} fetches a fresh one.
    this.options = null;
    this.codesSignal.set(null);
    this.failureSignal.set(null);
    this.stepSignal.set('passkey');
  }

  // Hands the account's keys to the service that holds them for the session.
  //
  // **No round trip, and that is the whole reason `adopt` exists.** This
  // browser drew the pair, wrapped it eleven times and posted the envelopes a
  // moment ago; asking the server to hand those envelopes back — to open them
  // under a key-encryption key derived from a passkey this device has only just
  // registered — would be a request whose entire purpose is to arrive back
  // where it started, on the happiest path in the product, with a failure mode
  // attached.
  //
  // **Called on the 201 and never at ceremony time**, which is the position a
  // reader will move it to. Adopting when the keys are drawn needs three
  // clearing sites — the restart, the failed POST, and a destroy hook this
  // service does not have — and the one that gets forgotten leaves the keys of
  // an account that was never created in a root singleton for the life of the
  // tab, with nothing on screen and nothing red.
  //
  // The `try` is for the reason `sign-in.service.ts` states over its own
  // custody call: an exception out of this subscriber's `next` is not routed to
  // the `error` callback beside it, and the statements after this one — the
  // token being forgotten, the navigation into the app — would simply not run.
  // Somebody would be left on the register screen holding a created account and
  // a card of live codes, with the screen saying nothing because nothing
  // failed.
  //
  // `null` is unreachable today: {@link create} refuses without {@link pending}
  // and the two are written in one breath. It is a branch rather than a `!`
  // because a non-null assertion here would be a claim about a coupling two
  // methods apart, and the honest thing to do with an account whose keys are
  // missing is to leave it locked — every screen this client has renders
  // without them, and the way in is another factor.
  private adopt(
    keys: {
      readonly contentKey: CryptoKey;
      readonly indexKey: CryptoKey;
    } | null,
  ): void {
    if (keys === null) {
      return;
    }

    try {
      this.custody.adopt(keys.contentKey, keys.indexKey);
    } catch {
      // Nothing. See above.
    }
  }

  // The ceremony, the keys, the codes and the eleven wraps — the whole of what
  // has to happen between a challenge arriving and a person being shown ten
  // codes.
  private async mintUnder(options: PasskeyCreationOptionsJson): Promise<void> {
    try {
      // **The device agrees first, and this departs from the story's written
      // plan on purpose.** The plan minted the card before running the
      // ceremony. Cancelling the system passkey sheet is the most common thing
      // that happens on this screen, and minting first leaves that person's
      // browser holding ten recovery codes for a flow that ended — secrets
      // created for an account that does not exist. That is the header rule of
      // `codes-step.component.ts` — never mint inside the screen that displays
      // a set — read in the other direction. In this order nothing secret is
      // created until the authenticator has agreed. It costs nothing: the
      // challenge is already spent either way.
      const ceremony = await this.ceremony.createPasskey(options);

      if (!ceremony.ok) {
        this.busySignal.set(false);
        this.failureSignal.set(
          RegisterService.ceremonyFailureOf(ceremony.failure),
        );

        return;
      }

      // **Once, here, outside every loop.** The account owns one content key
      // and one index key and every factor wraps *those*; a pair drawn per
      // factor passes every round trip and every constraint, and gives the
      // second factor a second, incompatible account — see `account-keys.ts`.
      const keys = generateAccountKeys();

      try {
        const set = await mintRecoveryCodeSet();

        const passkeyFactorId = mintFactorId();
        const passkeyKeys = await wrapAccountKeys(
          ceremony.value.keyEncryptionKey,
          keys,
          passkeyFactorId,
        );

        // **One code's four members are produced in one scope, from one code.**
        // The obvious implementation derives ten key-encryption keys into an
        // array, wraps ten times into a second, and zips the results against
        // the ten factor ids at post time. That satisfies every type, every
        // count and every round trip.
        //
        // **The pairing that must not come apart is the factor id and the
        // envelopes beside it**, because that id is the associated data both
        // envelopes were sealed with: a submission carrying one code's id and
        // another code's envelopes rebuilds associated data reproducing neither
        // seal, so that factor opens nothing — ever, for anybody. Nothing on
        // either side of the wire can see it: the set validates, the account is
        // created, a session is handed over, and it is discovered by somebody
        // who redeemed a code months later and found the account still locked.
        // It is the client-side twin of the argument `RegisterAccountHandler`
        // makes over its `wrappedAccountKeys` list, where the card's rows are
        // projected from the one validated list rather than zipped from three.
        //
        // The verifier is the one member that could float without consequence,
        // and naming which half is which matters more than the rule does: it
        // lands in `recovery_code_hashes`, a table with no `factor_id` and no
        // link to `wrapped_account_keys`, so a set whose verifiers were
        // shuffled against its id-and-envelope triples redeems and unwraps
        // exactly like a correct one. Building all four here is still right,
        // because one scope is cheaper than a rule about which members may be
        // zipped.
        //
        // The factor id is minted inside the loop for the same reason, rather
        // than eleven at a time up front: an array of ten identifiers is a
        // third thing to zip. Nothing checks the eleven for distinctness —
        // a `randomUUID` collision is not a case a branch can honestly cover,
        // and the server refuses one in two places:
        // `RecoveryCodeSetValidation.DecodeAndValidate` holds the ten apart
        // from each other, and `RegisterAccountHandler` compares the passkey's
        // own factor id against all ten before it writes anything.
        const codes: RecoveryCodeSubmissionBody[] = [];

        for (let index = 0; index < set.codes.length; index += 1) {
          const code = set.codes[index];
          const verifier = set.verifiers[index];
          const factorId = mintFactorId();
          const wrapped = await wrapAccountKeys(
            await keyEncryptionKeyFromRecoveryCode(code),
            keys,
            factorId,
          );

          // The key-encryption key above is an expression and never a field:
          // eleven of them exist during this method and none of them outlives
          // it, whatever a later reader adds to this class. They are
          // non-extractable by construction as well —
          // `account-keys.ts`'s exported `importAesGcmKey` is the one import
          // both derivations share and it passes `extractable: false` — so
          // neither half of that rests on the other. It is no longer only the
          // key-encryption keys' door: it is the one place in this client
          // where bytes become an AES-GCM key, whichever key they are, and it
          // refuses any material that is not `ACCOUNT_KEY_BYTES` wide. Named
          // rather than cited by line: that function has already moved once
          // under a comment pointing at where it used to be, and a name
          // survives the next edit above it — though not a rename, which this
          // line has already been corrected for once.
          codes.push({ verifier, factorId, ...wrapped });
        }

        // **The account keys become keys here, and this is the last place
        // their bytes exist.**
        //
        // The paragraph that used to stand here refused to keep them at all,
        // and its objection was to keeping *bytes*: sixty-four of them, the
        // account's whole keyspace, alive on an instance for a feature that did
        // not exist. That objection no longer reaches, because after these two
        // statements there are no bytes. Both doors zero-fill the material they
        // are handed — on the rejecting path as thoroughly as on the succeeding
        // one — so what this method leaves behind is two non-extractable
        // `CryptoKey` objects, which no API in the platform reads back out.
        //
        // The alternative it argued for is the one a reader will now propose in
        // reverse: drop these and let the app re-read `wrapped_account_keys`
        // through the route once the session exists. It costs a round trip on
        // the happiest path in the product, adds a failure mode there, and buys
        // a verification that is illusory — a passkey session's read hands back
        // the **passkey** factor's pair alone and never exercises the ten code
        // pairs, which is precisely where the mispairing hazard the loop above
        // is built around lives. It also needs the passkey's key-encryption key
        // to survive the codes step on some instance, which is the same power
        // one step removed.
        //
        // **Two doors, because the two keys are two different keys.** The
        // content key encrypts, so it goes through the AES-GCM door; the index
        // key is what a blind index is computed under, which is HMAC-SHA-256,
        // and sending it through `importAesGcmKey` because that line is already
        // written hands back an object that cannot compute a single index and
        // cannot be corrected afterwards.
        const [contentKey, indexKey] = await Promise.all([
          importAesGcmKey(keys.contentKey),
          importHmacSha256Key(keys.indexKey),
        ]);

        this.#accountKeys = { contentKey, indexKey };

        // Kept, and now belt to the doors' braces: both buffers were wiped by
        // the imports above, and the `finally` below wipes them again on the
        // path where one of the imports rejected. A wipe of a buffer that is
        // already zeroes costs nothing; the line that is missing the day
        // somebody replaces a door is what these cost nothing to prevent.
        keys.contentKey.fill(0);
        keys.indexKey.fill(0);

        this.pending = {
          // Spread, and safe **only** because `webauthn-encoding.ts`'s
          // `toRegistrationPayload` projects the client's extension results
          // into a fresh `{prf:{enabled}}` rather than forwarding them. A
          // spread of a raw credential's results would ship `prf.results.first`
          // — the PRF output itself, which is the value the passkey factor's
          // key-encryption key is derived from.
          ...ceremony.value.payload,
          factorId: passkeyFactorId,
          ...passkeyKeys,
          codes,
        };

        this.codesSignal.set(set.codes);
        this.stepSignal.set('codes');
        this.busySignal.set(false);
      } finally {
        // However this ended. A wrap that rejected halfway leaves the account
        // keys in two buffers the runtime still holds, and that is the path
        // where something has already gone wrong.
        keys.contentKey.fill(0);
        keys.indexKey.fill(0);
      }
    } catch {
      // Nothing above throws in the ordinary course: the ceremony answers with
      // a result rather than an exception, and the crypto is the platform's. A
      // rejection here is therefore something nobody predicted, which is what
      // `unknown` is the word for — and swallowing it would leave the screen on
      // a spinner forever.
      this.busySignal.set(false);
      this.failureSignal.set('unknown');
    }
  }

  // The ceremony's five words, mapped one for one. A `switch` over the closed
  // union rather than a lookup object, so a sixth word added there fails to
  // compile here instead of arriving as `undefined` on a screen.
  private static ceremonyFailureOf(
    failure: PasskeyCeremonyFailure,
  ): RegisterFailure {
    switch (failure) {
      case 'unsupported':
        return 'unsupported';
      case 'cancelled':
        return 'cancelled';
      case 'duplicate':
        return 'duplicate';
      case 'no-prf':
        return 'no-prf';
      case 'failed':
        return 'ceremony-failed';
    }
  }

  // The options leg's own mapper, called by both presses that make that request
  // — {@link begin} and {@link createPasskey} — and **it is a second mapper on
  // purpose.**
  //
  // A reader will want to hand this leg {@link failureOf} and be done with it,
  // because both legs answer HTTP and one of the two statuses even means the
  // same thing on both. It cannot be done: **the same status is a different
  // fact on each leg**, and the two disagree in the direction that costs.
  // `POST /api/registration/options` refuses with a 409 when the provider
  // identity already has an account, and it does so *above* its own
  // `challengeStore.IssueAsync` — `BeginAccountRegistrationHandler` argues the
  // position there — so nothing is minted, nothing is spent and, the part that
  // reaches the person, the browser never runs
  // `navigator.credentials.create()` and is not left holding a passkey for an
  // account that was never created. Everything else on this leg is
  // `start-failed`: the server never issued a challenge.
  //
  // **The expensive half is `unknown`, which is why it appears in neither
  // branch below.** `failureOf`'s `unknown` carries one meaning and one only —
  // the request may have committed all thirty rows and lost its answer coming
  // back — and it is the word the screen renders "keep your codes" from. This
  // leg creates nothing, ever, so that sentence is false here in every case,
  // and `start-failed` is the honest one. Leave `failureOf`'s argument where it
  // is: it is written about the POST leg throughout and is not true of this
  // one.
  //
  // **The 401 is the third word, and it was the most expensive one to be
  // missing.** Both registration routes are declared on the provider scheme and
  // nothing else, so a 401 here is the API refusing the bearer this browser
  // attached — measured on `/register` loaded with an id token 71 minutes past
  // its expiry, which answered `invalid_token` and the instant it expired at.
  // Read as `start-failed` it produced a screen saying Budgetoid could not reach
  // the server, beside a `Try again` that re-sends the same dead token for as
  // long as anybody keeps pressing it: wrong about what happened, and offering
  // the one act certain to end here again. `AuthService.providerEmail` closes
  // the cold-load half of this by answering `null` for a token that is no longer
  // valid; this closes the half where the token lapses with the screen already
  // open, which that one cannot see — `email` is a `computed` with no signal
  // dependency and settles on first read, deliberately.
  //
  // **The 403 is not this word and must not become it.** It is the
  // `X-Budgetoid-Client` refusal — a defect in this client, answered to a
  // browser whose token is perfectly good — so it stays `start-failed` with
  // every other status, and a person sent through the provider for it would come
  // back to exactly the same refusal.
  private static startFailureOf(error: unknown): RegisterFailure {
    if (!(error instanceof HttpErrorResponse)) {
      return 'start-failed';
    }

    switch (error.status) {
      case 401:
        return 'provider-token-refused';
      case 409:
        return 'conflict';
      default:
        return 'start-failed';
    }
  }

  // **`refused` and `conflict` are not `unknown`, and this is the most
  // consequential branch in the file.**
  //
  // Every 400 leaves `RegisterAccountHandler` by exception before `RegisterAsync`
  // is reached, a 401 and a 403 are refused before the handler is entered at
  // all, and a 409 refuses *this* request against an account that already
  // exists. All four are certainly-not-created, so the codes on screen are
  // certainly dead and the honest sentence is to throw them away and start
  // again.
  //
  // A network failure, a timeout and a 5xx are not. The request may have
  // arrived, committed all thirty rows and had its 201 lost on the way back.
  // Collapsing the two readings means telling somebody whose account *was*
  // created that the only codes it has are worthless — and there is no way for
  // them to make more, because `POST /api/me/recovery-codes` has no caller in
  // this client. It is the four-valued reading `SessionStatus` and
  // `SessionService.readingOf` carry in `session.service.ts`, and
  // `SettingsService`'s rule about never collapsing `null` into `0`, on the one
  // screen where the cost is an account nobody can ever open again.
  private static failureOf(error: unknown): RegisterFailure {
    if (!(error instanceof HttpErrorResponse)) {
      return 'unknown';
    }

    switch (error.status) {
      case 400:
      case 401:
      case 403:
        return 'refused';
      case 409:
        return 'conflict';
      default:
        // Status `0` for a request that never reached a server, every 5xx, and
        // anything else a proxy invents. None of them is a statement about
        // whether the account exists.
        return 'unknown';
    }
  }
}
