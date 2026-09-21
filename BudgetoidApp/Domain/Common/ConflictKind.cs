namespace Domain.Common;

/// <summary>
/// What a caller does next about a <see cref="ConflictException"/>, as a closed vocabulary a machine can
/// branch on.
/// </summary>
/// <remarks>
/// <para>
/// <b>A member names a REMEDY, not a throw site.</b> Every 409 in this product answers under one fixed
/// title, so before this type existed the only thing separating two conflicts was a sentence written for
/// a person to read — and two conflicts whose remedies are opposites were, to a client, the same
/// response. Sites whose next step is identical therefore share a member deliberately: five tables raise
/// <see cref="DuplicateIdentifier"/> and three routes raise <see cref="FactorAlreadyRegistered"/>,
/// because in each case the caller does exactly one thing regardless of which table or route refused it.
/// One member per throw site would be a second spelling of the stack trace and would tell a client
/// nothing the status did not.
/// </para>
/// <para>
/// <b>It does not replace the sentence and must not be read as a chance to shorten one.</b> The two
/// answer different readers — the member is what a client branches on, the detail is what a person is
/// shown — and each is the whole of what its reader gets. <c>docs/design/voice.md</c> owns the
/// sentences.
/// </para>
/// <para>
/// <b>No member takes the value zero, and that is the guard rather than an omission.</b>
/// <see cref="ConflictException"/> requires a kind so that a conflict raised without one does not
/// compile, but <c>default</c> would satisfy that requirement while meaning nothing — so numbering
/// starts at one and <see cref="ConflictKindSpelling.Of"/> refuses every undeclared value, which turns
/// <c>default(ConflictKind)</c> into a loud failure at the site that wrote it rather than a token on the
/// wire that no client has a branch for. It is the same decision <c>CredentialType</c> made by ordering
/// its members so that <c>default</c> is a shape the database refuses; the mechanism differs because
/// nothing below this type is in a position to refuse anything.
/// </para>
/// </remarks>
public enum ConflictKind
{
    /// <summary>
    /// The client-minted identifier in the request is already held by a row of the same table.
    /// </summary>
    /// <remarks>
    /// Raised by all five client-minted tables — accounts, payees, category groups, categories and
    /// transactions — because the caller's next step does not vary with the table: read that row back by
    /// its identifier if this request was a retry, and otherwise mint a fresh identifier and post again.
    /// Deliberately <em>not</em> <see cref="DuplicateName"/>: the row already wearing the identifier may
    /// hold a different name, or sit in a budget the caller cannot read.
    /// </remarks>
    DuplicateIdentifier = 1,

    /// <summary>
    /// A row in this budget already carries the name the create proposed, so the client's list was stale.
    /// </summary>
    /// <remarks>
    /// The remedy is to adopt the row that already exists, which is not a correction to a field — which
    /// is why this is a conflict and not the validation problem the <em>rename</em> leg of the same index
    /// answers with. See <c>PayeeRepository.AddAsync</c> for the full argument.
    /// </remarks>
    DuplicateName,

    /// <summary>
    /// The provider identity the caller proved they hold already has an account.
    /// </summary>
    /// <remarks>
    /// Shared by both legs of <c>/api/registration</c>, which is the same pairing
    /// <c>RegistrationConflicts.SubjectAlreadyRegisteredMessage</c> exists to keep: one fact reached from
    /// two routes, and the remedy is to sign in with the passkey that account holds or to redeem a
    /// recovery code.
    /// </remarks>
    SubjectAlreadyRegistered,

    /// <summary>
    /// The email address on the provider token belongs to an account opened under a different provider
    /// subject.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="SubjectAlreadyRegistered"/>, though the two are decided one line
    /// apart.</b> That one says the caller already has a way in and can use it; this one says they do not
    /// — the account holding the address answers to another Google identity, so no passkey or recovery
    /// code of theirs opens it, and the next step is a different address or the other Google account.
    /// Folding the two would tell somebody who cannot get in to go and sign in.
    /// </remarks>
    EmailAlreadyLinked,

    /// <summary>
    /// The WebAuthn credential the ceremony produced is already enrolled.
    /// </summary>
    /// <remarks>
    /// The authenticator, not the factor identifier beside it: the remedy is to use a different
    /// authenticator — or to stop, because this one already works — where
    /// <see cref="FactorAlreadyRegistered"/>'s is to re-wrap the account keys under a fresh identifier
    /// and run the ceremony again.
    /// </remarks>
    AuthenticatorAlreadyRegistered,

    /// <summary>
    /// The client-minted factor identifier is already standing in <c>wrapped_account_keys</c>.
    /// </summary>
    /// <remarks>
    /// One fact about one table reached from three routes — registration, passkey registration and
    /// recovery-code generation — and the caller's situation is identical on each: nothing was written,
    /// and the envelopes cannot simply be re-sent because the old identifier was the associated data they
    /// were sealed with. Mint a fresh one, wrap the account keys under it, and run the ceremony again.
    /// </remarks>
    FactorAlreadyRegistered,

    /// <summary>
    /// The passkey the caller asked to revoke is the account's last one.
    /// </summary>
    /// <remarks>
    /// The remedy is an act on a different resource — register another passkey first — which is what
    /// separates it from every other member here.
    /// </remarks>
    LastPasskey,

    /// <summary>
    /// Another request replaced this account's recovery codes while this one was in flight.
    /// </summary>
    /// <remarks>
    /// A fact about timing rather than about anything the caller sent, which is why the remedy is to
    /// present a fresh re-authentication and generate again — this attempt's nonce is spent — and why it
    /// is not <see cref="FactorAlreadyRegistered"/>, whose cause is an identifier that at 122 random bits
    /// is never chance.
    /// </remarks>
    RecoveryCodesReplaced,

    /// <summary>
    /// Another change to the account's recovery factors landed first, so the manifest the request's
    /// rotation epoch was computed from is no longer the current generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller sent nothing wrong, and the sentence this kind travels with must not suggest they
    /// did.</b> Their epoch was the stored generation plus one at the moment they read it; a concurrent
    /// registration or recovery-code issue committed in between and took that generation. The remedy is
    /// to read the account's keys back, seal a manifest over the generation it now reports, and run the
    /// ceremony again — the same fact reached from the two routes that change a factor set, which is why
    /// they share this member.
    /// </para>
    /// <para>
    /// <b>Deliberately not the 400 the same rule raises one layer up, and the two must never be merged.</b>
    /// <c>FactorManifest.Promote</c> refuses an epoch that was never one greater than the stored
    /// generation, keyed on the member the caller can correct — that caller's arithmetic was wrong.
    /// This member is the optimistic concurrency token firing on a request whose arithmetic was
    /// <em>right</em> and has since been overtaken. One rule, two different facts about the caller, and a
    /// reader who folds them tells somebody who did everything correctly to go and fix their request.
    /// </para>
    /// <para>
    /// <b>Deliberately not <see cref="RecoveryCodesReplaced"/> either</b>, though both are facts about
    /// timing and one of the two routes can raise both. That one says the account's <em>set of recovery
    /// codes</em> was replaced, so the codes this caller has already shown a person will never redeem and
    /// nothing short of issuing again helps. This one says the manifest generation moved: the request is
    /// otherwise intact, and what has to be rebuilt is the manifest and its epoch.
    /// </para>
    /// </remarks>
    FactorSetMoved,

    /// <summary>
    /// The rotation the caller asked to complete still has rows that have not been re-sealed under its
    /// new keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The remedy is an act on a different resource</b> — send the outstanding chunks — which is what
    /// separates it from every kind about the request itself and puts it beside
    /// <see cref="LastPasskey"/> in shape. Nothing the caller sent was wrong: the identifier is the run
    /// the account really has staged, and the work is simply not finished.
    /// </para>
    /// <para>
    /// <b>Deliberately not <see cref="FactorSetMoved"/>, and the two are refused one step apart on the
    /// same route.</b> That one asks for a whole new ceremony because the account's factors changed under
    /// the run. This one asks for the chunks the client already knows it owes, after which the very same
    /// request succeeds. Folding them would send somebody with two chunks left to send back to the
    /// beginning of a rotation that is nearly done.
    /// </para>
    /// </remarks>
    RotationIncomplete,

    /// <summary>
    /// The rotation the caller asked to complete has already been promoted, and the generation it staged
    /// is the one in force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because the staging row outlives the run.</b> A completion deletes nothing — the
    /// application role holds no <c>DELETE</c> on either rotation table, and a tidy-up run a moment early
    /// would destroy the only copies of a generation rows have already been rewritten under — so a
    /// re-sent request finds the same row carrying the same identifier it is quoting, and "is this the
    /// staged run" answers yes for a run that finished minutes ago.
    /// </para>
    /// <para>
    /// <b>The remedy is nothing at all, and that is the point of giving it a member.</b> Without one, a
    /// re-sent completion would reach <c>FactorManifest.Promote</c> and be refused as a <c>400</c> about
    /// the caller's arithmetic — which tells a client whose first request succeeded and whose response was
    /// lost that its number is wrong, where the truth is that the work is done. The second promotion would
    /// otherwise be harmless in the bytes and wrong in every word.
    /// </para>
    /// <para>
    /// <b>Deliberately not <see cref="RotationIncomplete"/>'s opposite in name only.</b> That one is a
    /// run that cannot yet be promoted; this one is a run that no longer can, because there is nothing
    /// left to promote.
    /// </para>
    /// </remarks>
    RotationAlreadyCompleted,
}
